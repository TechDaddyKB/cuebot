using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Uploads;

namespace Cuebot.Portal.Tests;

/// <summary>Uploads through the portal API: chunks, checks, approval, the library and the file rule for steps.</summary>
public sealed class PortalUploadsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-uploads-").FullName;
    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly PortalDirectory _directory;
    private readonly PortalChanges _changes;
    private readonly PortalMediaStore _store;
    private readonly PortalUploads _uploads;
    private readonly PortalApi _api;

    public PortalUploadsTests()
    {
        _profile = new ProfileService(null, _log);
        var commands = new CommandStore(null, _log);
        var registry = new Registry([], []);
        var engine = new ActionEngine(new EventBus(_log), _profile, registry, new GlobalVariables(null, _log), _log);
        _directory = new PortalDirectory(new PortalSettings(), () => { });
        _store = new PortalMediaStore(new PortalMediaLibrary(), () => { }, Path.Combine(_dir, "media"));
        _changes = new PortalChanges(new PortalChangeLog(), () => { }, _directory,
            [new ActionArea(_profile), new CommandArea(commands), new UploadArea(_store)]);
        var tools = MediaTools.Find();
        _uploads = new PortalUploads(_store, _changes, tools is null ? null : new MediaCleaner(tools), Path.Combine(_dir, "quarantine"), _log);
        _api = new PortalApi(_directory, _changes, _profile, commands, engine, registry, uploads: _uploads);
    }

    private PortalMember Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _directory.RedeemInvite(_directory.CreateInvite(role).Secret,
            new ProviderIdentity(IdentityProvider.Google, Guid.NewGuid().ToString(), "m@x", "Sam"), PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
    }

    private Task<JsonObject> Call(PortalMember m, string op, JsonObject? body = null)
    {
        var request = body ?? new JsonObject();
        request["op"] = op;
        return _api.HandleAsync(m.Id, request);
    }

    /// <summary>Uploads like the portal does and waits for the result.</summary>
    private async Task<JsonObject> UploadAsync(PortalMember m, byte[] data, string kind, string name = "airhorn.wav", byte[]? declaredSha = null)
    {
        var start = await Call(m, "uploads.start", new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["size"] = data.Length,
            ["sha256"] = Convert.ToHexStringLower(declaredSha ?? SHA256.HashData(data)),
        });
        if (!(bool)start["ok"]!) throw new InvalidOperationException((string?)start["error"]);
        var id = (string)start["id"]!;
        for (var i = 0; i * PortalUploads.ChunkSize < data.Length; i++)
        {
            var piece = data.AsSpan(i * PortalUploads.ChunkSize, Math.Min(PortalUploads.ChunkSize, data.Length - i * PortalUploads.ChunkSize));
            var sent = await Call(m, "uploads.chunk", new JsonObject { ["id"] = id, ["index"] = i, ["data"] = Convert.ToBase64String(piece) });
            Assert.True((bool)sent["ok"]!, (string?)sent["error"]);
        }
        var status = await Call(m, "uploads.finish", new JsonObject { ["id"] = id });
        for (var t = 0; t < 600 && (string?)status["state"] == "cleaning"; t++)
        {
            await Task.Delay(50);
            status = await Call(m, "uploads.status", new JsonObject { ["id"] = id });
        }
        return status;
    }

    private static byte[] Media(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", name));

    [MediaFact]
    public async Task A_trusted_mods_sound_goes_live_as_a_fresh_mp3()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var status = await UploadAsync(tech, Media("sound.wav"), "sound");
        Assert.Equal("live", (string?)status["state"]);
        var item = Assert.Single(_store.Items);
        Assert.Equal("airhorn", item.Name);
        Assert.EndsWith(".mp3", item.File, StringComparison.Ordinal);
        Assert.DoesNotContain("airhorn", item.File, StringComparison.Ordinal);   // random file names
        Assert.True(File.Exists(_store.FullPath(item)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "quarantine")));

        var list = await Call(tech, "uploads.list");
        var listed = list["items"]!.AsArray().Single()!;
        Assert.Null(listed["file"]);   // the portal never sees the folder
        Assert.True((bool)listed["canDelete"]!);
    }

    [MediaFact]
    public async Task A_standard_mods_upload_waits_for_the_streamer()
    {
        var standard = Member(DefaultRoles.StandardModId);
        var status = await UploadAsync(standard, Media("sound.mp3"), "sound");
        Assert.Equal("pending", (string?)status["state"]);
        Assert.Empty(_store.Items);
        var change = Assert.Single(_changes.Pending);
        Assert.Equal(UploadArea.AreaName, change.Area);

        Assert.Equal(ChangeResult.Applied, await _changes.ApproveAsync(change.Id));
        Assert.Single(_store.Items);
    }

    [MediaFact]
    public async Task Rejected_uploads_are_swept_away()
    {
        var standard = Member(DefaultRoles.StandardModId);
        await UploadAsync(standard, Media("sound.mp3"), "sound");
        var change = Assert.Single(_changes.Pending);
        await _changes.RejectAsync(change.Id);
        var file = Path.Combine(_store.Folder, (string)change.After!["file"]!);
        Assert.True(File.Exists(file));   // the change log still mentions it
        _store.Sweep([]);   // once the log has forgotten it
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Pictures_need_the_media_permission()
    {
        var standard = Member(DefaultRoles.StandardModId);
        var start = await Call(standard, "uploads.start", new JsonObject { ["kind"] = "image", ["name"] = "x.png", ["size"] = 10, ["sha256"] = new string('0', 64) });
        Assert.False((bool)start["ok"]!);
        Assert.Contains("cannot upload", (string?)start["error"]);
    }

    [Fact]
    public async Task Size_limits_apply_before_anything_is_sent()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var start = await Call(tech, "uploads.start", new JsonObject
        {
            ["kind"] = "sound", ["name"] = "x.wav", ["size"] = UploadLimits.Sound.MaxBytes + 1, ["sha256"] = new string('0', 64),
        });
        Assert.False((bool)start["ok"]!);
    }

    [MediaFact]
    public async Task A_file_that_changed_on_the_way_is_refused()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var status = await UploadAsync(tech, Media("sound.wav"), "sound", declaredSha: new byte[32]);
        Assert.Equal("failed", (string?)status["state"]);
        Assert.Contains("damaged", (string?)status["error"]);
        Assert.Empty(_store.Items);
    }

    [MediaFact]
    public async Task Pieces_must_fit_the_upload()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var data = Media("sound.wav");
        var start = await Call(tech, "uploads.start", new JsonObject
        {
            ["kind"] = "sound", ["name"] = "x.wav", ["size"] = data.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)),
        });
        var id = (string)start["id"]!;
        Assert.False((bool)(await Call(tech, "uploads.chunk", new JsonObject { ["id"] = id, ["index"] = 99, ["data"] = "AAAA" }))["ok"]!);
        Assert.False((bool)(await Call(tech, "uploads.chunk", new JsonObject { ["id"] = id, ["index"] = 0, ["data"] = "AAAA" }))["ok"]!);
        Assert.False((bool)(await Call(tech, "uploads.finish", new JsonObject { ["id"] = id }))["ok"]!);
        // Another member cannot touch it.
        var other = Member(DefaultRoles.TechLeadId);
        Assert.False((bool)(await Call(other, "uploads.status", new JsonObject { ["id"] = id }))["ok"]!);
    }

    [MediaFact]
    public async Task Refused_files_say_why_and_leave_nothing_behind()
    {
        var tech = Member(DefaultRoles.TechLeadId);
        var status = await UploadAsync(tech, "<svg><script>alert(1)</script></svg>"u8.ToArray(), "image", "cat.svg");
        Assert.Equal("failed", (string?)status["state"]);
        Assert.Contains("does not take", (string?)status["error"]);
        Assert.Empty(_store.Items);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "quarantine")));
    }

    [MediaFact]
    public async Task Mods_remove_their_own_uploads_only()
    {
        var owner = Member(DefaultRoles.TechLeadId);
        await UploadAsync(owner, Media("sound.wav"), "sound");
        var id = _store.Items.Single().Id.ToString();
        var standard = Member(DefaultRoles.StandardModId);   // no UndoOthers
        Assert.False((bool)(await Call(standard, "uploads.delete", new JsonObject { ["id"] = id }))["ok"]!);
        Assert.True((bool)(await Call(owner, "uploads.delete", new JsonObject { ["id"] = id }))["ok"]!);
        Assert.Empty(_store.Items);
    }

    [MediaFact]
    public async Task Steps_take_uploads_and_never_other_files()
    {
        var lead = Member(DefaultRoles.LeadModId);
        await UploadAsync(lead, Media("sound.wav"), "sound");
        var sound = _store.Items.Single();

        ActionDefinition Play(string file) => new()
        {
            Name = "Horn",
            SubActions = [new SubActionDefinition { Type = "audio.playSound", Parameters = new() { ["file"] = file } }],
        };
        var bad = await Call(lead, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(Play("/home/streamer/secret.wav")) });
        Assert.False((bool)bad["ok"]!);
        Assert.Contains("uploaded sound", bad["problems"]!.AsArray().Single()!.ToString());

        var good = await Call(lead, "actions.save", new JsonObject { ["action"] = PortalChanges.ToNode(Play(PortalFiles.Prefix + sound.Id)) });
        Assert.True((bool)good["ok"]!, good.ToJsonString());
        var saved = _profile.Snapshot.Actions.Single();
        Assert.Equal(_store.FullPath(sound), saved.SubActions.Single().Parameters["file"]);   // stored as the real path

        var shown = await Call(lead, "actions.get", new JsonObject { ["id"] = saved.Id.ToString() });
        Assert.Equal(PortalFiles.Prefix + sound.Id, (string?)shown["action"]!["subActions"]![0]!["parameters"]!["file"]);
    }

    [Fact]
    public void Steps_the_streamer_set_up_keep_their_files()
    {
        var files = new PortalFiles(_store);
        var step = new SubActionDefinition { Type = "obs.setMediaFile", Parameters = new() { ["file"] = "/videos/intro.mp4" } };
        var before = new ActionDefinition { SubActions = [step] };
        var after = PortalChanges.FromNode<ActionDefinition>(PortalChanges.ToNode(before));
        after.Name = "Renamed";
        Assert.Empty(files.Check(before, after));
        after.SubActions[0].Parameters["file"] = "/videos/other.mp4";
        Assert.NotEmpty(files.Check(before, after));
        after.SubActions[0].Parameters["file"] = "";
        Assert.Empty(files.Check(before, after));
    }

    public void Dispose()
    {
        _uploads.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
