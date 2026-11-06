using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Microsoft.Win32.SafeHandles;

namespace Cuebot.Portal.Uploads;

/// <summary>
/// Uploads from the portal (docs/MOD_PORTAL_PLAN.md, section 8). The browser sends a file in small chunks (the relay
/// takes frames up to 64 KB); Cuebot writes them into a quarantine folder of its own, checks the size and SHA-256 the
/// browser declared, then cleans one file at a time with <see cref="MediaCleaner"/>. Only the cleaned file reaches the
/// library, as a change: live at once or waiting for the streamer, by the member's change setting.
/// </summary>
public sealed class PortalUploads : IDisposable
{
    public const int ChunkSize = 32 * 1024;
    public const int MaxItemsPerMember = 100;
    public const long MaxBytesPerMember = 1L << 30;
    public const int MaxOpenUploads = 4;
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    private const string LogSource = "Mod portal";

    private readonly PortalMediaStore _store;
    private readonly PortalChanges _changes;
    private readonly MediaCleaner? _cleaner;
    private readonly AppLog? _log;
    private readonly TimeProvider _time;
    private readonly string _quarantine;
    private readonly Dictionary<Guid, Upload> _uploads = [];
    private readonly SemaphoreSlim _cleaning = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public PortalUploads(PortalMediaStore store, PortalChanges changes, MediaCleaner? cleaner, string quarantine, AppLog? log = null,
        TimeProvider? time = null)
    {
        _store = store;
        _changes = changes;
        _cleaner = cleaner;
        _log = log;
        _time = time ?? TimeProvider.System;
        _quarantine = Path.GetFullPath(quarantine);
        // Leftovers from a crash or a restart are never resumed.
        try { if (Directory.Exists(_quarantine)) Directory.Delete(_quarantine, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _store.Sweep(_changes.All);
    }

    public PortalMediaStore Store => _store;

    /// <summary>Whether uploads can be cleaned here (an ffmpeg was found).</summary>
    public bool Available => _cleaner is not null;

    public static bool Handles(string op) => op.StartsWith("uploads.", StringComparison.Ordinal);

    public static PortalPermission PermissionFor(UploadKind kind) => kind == UploadKind.Sound ? PortalPermission.UploadSounds : PortalPermission.UploadMedia;

    public async Task<JsonObject> HandleAsync(PortalMember member, PortalRole? role, string op, JsonObject request)
    {
        ForgetIdle();
        return op switch
        {
            "uploads.list" => List(member, role),
            "uploads.start" => Start(member, role, request),
            "uploads.chunk" => await ChunkAsync(member, request).ConfigureAwait(false),
            "uploads.finish" => await FinishAsync(member, request).ConfigureAwait(false),
            "uploads.status" => Status(member, request),
            "uploads.cancel" => Cancel(member, request),
            "uploads.delete" => await DeleteAsync(member, role, request).ConfigureAwait(false),
            _ => Error("Unknown request."),
        };
    }

    private JsonObject List(PortalMember member, PortalRole? role)
    {
        var kinds = Enum.GetValues<UploadKind>().Where(k => PortalPolicy.Has(role, PermissionFor(k))).ToList();
        var canSee = kinds.Count > 0 || PortalPolicy.Has(role, PortalPermission.CreateActions) || PortalPolicy.Has(role, PortalPermission.EditActions);
        if (!canSee) return Error("Your role does not include this.");
        return Ok(new JsonObject
        {
            ["available"] = Available,
            ["canUpload"] = new JsonArray([.. kinds.Select(k => (JsonNode)k.ToString())]),
            ["accept"] = new JsonObject(kinds.Select(k => KeyValuePair.Create(k.ToString(), (JsonNode?)MediaFormat.Accept(k)))),
            ["limits"] = new JsonObject(Enum.GetValues<UploadKind>().Select(k => KeyValuePair.Create(k.ToString(), (JsonNode?)new JsonObject
            {
                ["maxBytes"] = UploadLimits.For(k).MaxBytes,
                ["maxSeconds"] = UploadLimits.For(k).MaxDuration.TotalSeconds,
            }))),
            ["chunkSize"] = ChunkSize,
            ["items"] = new JsonArray([.. _store.Items.OrderByDescending(i => i.At).Select(i => (JsonNode)Describe(i, member, role))]),
        });
    }

    /// <summary>How the portal sees an item: never the folder on the streamer's computer.</summary>
    public static JsonObject Describe(PortalMediaItem i, PortalMember? member = null, PortalRole? role = null) => new()
    {
        ["id"] = i.Id.ToString(),
        ["name"] = i.Name,
        ["kind"] = i.Kind.ToString(),
        ["bytes"] = i.Bytes,
        ["durationMs"] = i.DurationMs,
        ["width"] = i.Width,
        ["height"] = i.Height,
        ["animated"] = i.Animated,
        ["by"] = i.MemberName,
        ["at"] = i.At.ToString("O"),
        ["canDelete"] = member is not null && PortalPolicy.Has(role, PermissionFor(i.Kind))
            && (i.MemberId == member.Id || PortalPolicy.Has(role, PortalPermission.UndoOthers)),
    };

    private JsonObject Start(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (!Enum.TryParse<UploadKind>((string?)request["kind"], ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            return Error("Pick what you are uploading: a sound, a picture or a video.");
        if (!PortalPolicy.Has(role, PermissionFor(kind))) return Error("Your role cannot upload this.");
        if (!Available) return Error("The streamer's Cuebot has no ffmpeg to check uploads with, so uploads are off.");
        var limits = UploadLimits.For(kind);
        var size = Whole(request["size"]);
        if (size <= 0) return Error("The file is empty.");
        if (size > limits.MaxBytes) return Error($"Files like this can be up to {limits.MaxBytes >> 20} MB.");
        var sha = (string?)request["sha256"] ?? "";
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return Error("The upload is missing its checksum.");
        var name = CleanName((string?)request["name"]);

        var mine = _store.Items.Where(i => i.MemberId == member.Id).ToList();
        var pendingBytes = _changes.Pending.Where(c => c.Area == UploadArea.AreaName && c.MemberId == member.Id).Sum(c => (long?)c.After?["bytes"] ?? 0);
        if (mine.Count + _changes.Pending.Count(c => c.Area == UploadArea.AreaName && c.MemberId == member.Id) >= MaxItemsPerMember)
            return Error($"You have {MaxItemsPerMember} uploads. Delete some before adding more.");
        if (mine.Sum(i => i.Bytes) + pendingBytes >= MaxBytesPerMember) return Error("Your uploads have used all of your space. Delete some first.");

        Upload upload;
        lock (_uploads)
        {
            // One upload at a time per member: a new one replaces theirs.
            foreach (var old in _uploads.Values.Where(u => u.MemberId == member.Id && u.State == UploadState.Receiving).ToList()) Drop(old);
            if (_uploads.Values.Count(u => u.State is UploadState.Receiving or UploadState.Cleaning) >= MaxOpenUploads)
                return Error("Other mods are uploading right now. Try again in a minute.");
            var dir = Path.Combine(_quarantine, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            upload = new Upload(Guid.NewGuid(), member.Id, member.Name, kind, name, size, Convert.FromHexString(sha), dir, _time.GetUtcNow());
            _uploads[upload.Id] = upload;
        }
        return Ok(new JsonObject { ["id"] = upload.Id.ToString(), ["chunkSize"] = ChunkSize });
    }

    private async Task<JsonObject> ChunkAsync(PortalMember member, JsonObject request)
    {
        if (Find(member, request) is not { State: UploadState.Receiving } upload) return Error("This upload is no longer open.");
        var index = request["index"] is null ? -1 : Whole(request["index"]);
        if (index < 0 || index >= upload.Chunks) return Error("That piece does not belong to this upload.");
        byte[] data;
        try { data = Convert.FromBase64String((string?)request["data"] ?? ""); }
        catch (FormatException) { return Error("That piece was damaged on the way."); }
        var expected = index == upload.Chunks - 1 ? (int)(upload.Size - index * ChunkSize) : ChunkSize;
        if (data.Length != expected) return Error("That piece has the wrong size.");
        await upload.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (upload.State != UploadState.Receiving) return Error("This upload is no longer open.");
            upload.File ??= File.OpenHandle(upload.InputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, upload.Size);
            RandomAccess.Write(upload.File, data, index * ChunkSize);
            upload.Received.Add((int)index);
            upload.Touched = _time.GetUtcNow();
        }
        finally
        {
            upload.Gate.Release();
        }
        return Ok(new JsonObject { ["received"] = upload.Received.Count });
    }

    private async Task<JsonObject> FinishAsync(PortalMember member, JsonObject request)
    {
        if (Find(member, request) is not { State: UploadState.Receiving } upload) return Error("This upload is no longer open.");
        await upload.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (upload.Received.Count != upload.Chunks || upload.File is null) return Error($"Some of the file is missing ({upload.Received.Count} of {upload.Chunks} pieces).");
            upload.File.Dispose();
            upload.File = null;
            byte[] hash;
            await using (var input = File.OpenRead(upload.InputPath)) hash = await SHA256.HashDataAsync(input).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, upload.Sha256)) { Fail(upload, "The file arrived damaged. Try again."); return Status(upload); }
            upload.State = UploadState.Cleaning;
        }
        finally
        {
            upload.Gate.Release();
        }
        _ = Task.Run(() => CleanAsync(upload, member), CancellationToken.None);
        return Status(upload);
    }

    private async Task CleanAsync(Upload upload, PortalMember member)
    {
        try { await _cleaning.WaitAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            DeleteFolder(upload.Dir);   // Cuebot is closing
            return;
        }
        try
        {
            if (upload.State != UploadState.Cleaning) return;
            var result = await _cleaner!.CleanAsync(upload.InputPath, upload.Kind, upload.Dir, upload.Cancel.Token).ConfigureAwait(false);
            if (result.Media is not { } media)
            {
                _log?.Info(LogSource, $"{member.Name}'s upload '{upload.Name}' was refused: {result.Error}{(result.Detail is { Length: > 0 } d ? " (" + FirstLine(d) + ")" : "")}");
                Fail(upload, result.Error ?? "The file could not be checked.");
                return;
            }
            var bytes = new FileInfo(media.Path).Length;
            var item = new PortalMediaItem
            {
                Name = upload.Name, Kind = upload.Kind, Bytes = bytes, DurationMs = media.DurationMs, Width = media.Width,
                Height = media.Height, Animated = media.Animated, MemberId = member.Id, MemberName = member.Name, At = _time.GetUtcNow(),
            };
            item.File = _store.Adopt(media.Path, upload.Kind, media.Extension);
            var (status, _) = await _changes.SubmitAsync(member, UploadArea.AreaName, item.Id, PortalChanges.ToNode(item)).ConfigureAwait(false);
            upload.ItemId = item.Id;
            upload.State = status == ChangeResult.Pending ? UploadState.Pending : UploadState.Live;
            _log?.Info(LogSource, $"{member.Name} uploaded the {upload.Kind.ToString().ToLowerInvariant()} '{upload.Name}' ({bytes / 1024} KB, "
                + (status == ChangeResult.Pending ? "waiting for approval)" : "live)"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Error(LogSource, $"Could not store {member.Name}'s upload '{upload.Name}'", ex);
            Fail(upload, "Cuebot could not store the file.");
        }
        finally
        {
            _cleaning.Release();
            DeleteFolder(upload.Dir);
        }
    }

    private JsonObject Status(PortalMember member, JsonObject request) =>
        Find(member, request) is { } upload ? Status(upload) : Error("This upload is no longer open.");

    private static JsonObject Status(Upload u)
    {
        var body = new JsonObject { ["state"] = u.State.ToString().ToLowerInvariant(), ["received"] = u.Received.Count, ["chunks"] = u.Chunks };
        if (u.Error is not null) body["error"] = u.Error;
        if (u.ItemId is { } id) body["itemId"] = id.ToString();
        return Ok(body);
    }

    private JsonObject Cancel(PortalMember member, JsonObject request)
    {
        if (Find(member, request) is { } upload) lock (_uploads) Drop(upload);
        return Ok();
    }

    private async Task<JsonObject> DeleteAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id) || _store.Get(id) is not { } item) return Error("That upload is gone already.");
        if (!PortalPolicy.Has(role, PermissionFor(item.Kind))) return Error("Your role cannot remove this.");
        if (item.MemberId != member.Id && !PortalPolicy.Has(role, PortalPermission.UndoOthers)) return Error("You can only remove your own uploads.");
        var (status, _) = await _changes.SubmitAsync(member, UploadArea.AreaName, id, null).ConfigureAwait(false);
        return Ok(new JsonObject { ["message"] = status == ChangeResult.Pending ? "Asked the streamer to remove it." : "Removed." });
    }

    private Upload? Find(PortalMember member, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id)) return null;
        lock (_uploads) return _uploads.TryGetValue(id, out var u) && u.MemberId == member.Id ? u : null;
    }

    private void ForgetIdle()
    {
        var now = _time.GetUtcNow();
        lock (_uploads)
        {
            foreach (var u in _uploads.Values.ToList())
            {
                var done = u.State is not (UploadState.Receiving or UploadState.Cleaning);
                if (now - u.Touched > IdleLimit && (done || u.State == UploadState.Receiving)) Drop(u);
            }
        }
    }

    /// <summary>Caller holds the _uploads lock.</summary>
    private void Drop(Upload u)
    {
        _uploads.Remove(u.Id);
        u.Cancel.Cancel();
        if (u.State != UploadState.Receiving) return;
        // Under the upload's own gate, so a piece being written finishes first.
        u.Gate.Wait();
        try
        {
            u.File?.Dispose();
            u.State = UploadState.Failed;
        }
        finally
        {
            u.Gate.Release();
        }
        DeleteFolder(u.Dir);
    }

    private void Fail(Upload u, string error)
    {
        u.Error = error;
        u.State = UploadState.Failed;
        u.Touched = _time.GetUtcNow();
    }

    /// <summary>A whole number however the JSON carried it (-1 when it is not one).</summary>
    private static long Whole(JsonNode? node) => node is JsonValue v
        && (v.TryGetValue<long>(out var l) || (v.TryGetValue<int>(out var i) && (l = i) == i)
            || (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15 && (l = (long)d) == d)) ? l : -1;

    private static string CleanName(string? name)
    {
        var text = Path.GetFileNameWithoutExtension((name ?? "").Trim());
        text = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > 60) text = text[..60].Trim();
        return text.Length == 0 ? "Upload" : text;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    private static void DeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonObject Ok(JsonObject? body = null)
    {
        body ??= [];
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    public void Dispose()
    {
        _stop.Cancel();
        lock (_uploads) foreach (var u in _uploads.Values.ToList()) Drop(u);
    }

    private enum UploadState { Receiving, Cleaning, Live, Pending, Failed }

    private sealed class Upload(Guid id, Guid memberId, string memberName, UploadKind kind, string name, long size, byte[] sha256, string dir, DateTimeOffset at)
    {
        public Guid Id { get; } = id;
        public Guid MemberId { get; } = memberId;
        public string MemberName { get; } = memberName;
        public UploadKind Kind { get; } = kind;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public byte[] Sha256 { get; } = sha256;
        public string Dir { get; } = dir;
        public string InputPath => Path.Combine(Dir, "input");
        public int Chunks => (int)((Size + ChunkSize - 1) / ChunkSize);
        public HashSet<int> Received { get; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Cancel { get; } = new();
        public SafeFileHandle? File { get; set; }
        public UploadState State { get; set; }
        public string? Error { get; set; }
        public Guid? ItemId { get; set; }
        public DateTimeOffset Touched { get; set; } = at;
    }
}
