using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuebot.App.Shell.GoogleDrive;

namespace Cuebot.UiTests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// An in-memory Google OAuth + Drive v3 server answering the way Google's REST reference documents it: token and
/// revoke endpoints (form posts, <c>{"error":"invalid_grant"}</c> errors), files.list/get/create/delete, resumable
/// uploads (session URI in <c>Location</c>, bytes PUT to it), <c>alt=media</c> downloads, about.get, and Drive's
/// <c>{"error":{"code":…,"errors":[{"reason":…}]}}</c> error bodies. Only files this "app" created exist, like the
/// drive.file scope.
/// </summary>
public sealed partial class FakeGoogleDrive : HttpMessageHandler
{
    public const string ClientId = "123-test.apps.googleusercontent.com";
    public const string ClientSecret = "GOCSPX-test";
    public static readonly GoogleDriveEndpoints Endpoints = new(
        new Uri("https://oauth.test/"), new Uri("https://accounts.test/o/oauth2/v2/auth"),
        new Uri("https://drive.test/drive/v3/"), new Uri("https://drive.test/upload/drive/v3/"));

    public sealed class DriveFile
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string MimeType { get; init; }
        public List<string> Parents { get; init; } = [];
        public byte[] Content { get; set; } = [];
        public DateTimeOffset CreatedTime { get; init; }
        public bool Trashed { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, DriveFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string MimeType, List<string> Parents)> _sessions = new();
    private readonly List<string> _log = [];
    private int _next;

    public FakeGoogleDrive(ManualTime time) => Time = time;

    public ManualTime Time { get; }

    /// <summary>The code the browser "returns" and the PKCE challenge it was issued for.</summary>
    public string AuthorizationCode { get; } = "4/0AeanS0test";
    public string? IssuedChallenge { get; set; }

    public string RefreshToken { get; set; } = "1//0gRefreshTest";
    public string AccessToken { get; private set; } = "";
    public DateTimeOffset AccessTokenExpires { get; private set; }

    /// <summary>The refresh token was revoked (or expired): the token endpoint answers invalid_grant.</summary>
    public bool RefreshRevoked { get; set; }

    /// <summary>Drive answers uploads with storageQuotaExceeded.</summary>
    public bool QuotaFull { get; set; }

    public int TokenRefreshes { get; private set; }
    public bool Revoked { get; private set; }
    public List<string> Uploads { get; } = [];

    /// <summary>"METHOD path" of every request, for asserting the call sequence.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    public IReadOnlyList<DriveFile> Files
    {
        get { lock (_gate) return _files.Values.ToArray(); }
    }

    public DriveFile? Folder => Files.FirstOrDefault(f => f.MimeType == GoogleDriveClient.FolderMimeType && !f.Trashed);

    public IReadOnlyList<DriveFile> ZipsInFolder => Folder is { } folder
        ? Files.Where(f => f.Parents.Contains(folder.Id) && !f.Trashed).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray()
        : [];

    /// <summary>Puts a file in the folder as if an earlier Cuebot had uploaded it.</summary>
    public DriveFile Seed(string name, byte[] content, string? folderId = null)
    {
        lock (_gate)
        {
            var folder = folderId ?? EnsureFolderLocked();
            var file = new DriveFile
            {
                Id = NewId(), Name = name, MimeType = "application/zip", Parents = [folder], Content = content, CreatedTime = Time.Now,
            };
            _files[file.Id] = file;
            return file;
        }
    }

    /// <summary>Plays the browser after consent: remembers the PKCE challenge and follows the redirect to the loopback receiver.</summary>
    public void ApproveConsent(Uri consent)
    {
        var q = Parse(consent.Query.TrimStart('?'));
        IssuedChallenge = q["code_challenge"];
        var redirect = $"{q["redirect_uri"]}?state={Uri.EscapeDataString(q["state"])}&code={Uri.EscapeDataString(AuthorizationCode)}";
        _ = Task.Run(async () =>
        {
            using var browser = new HttpClient();
            await browser.GetStringAsync(redirect);
        });
    }

    /// <summary>Lets the current access token expire early, as Google may do; the next call gets 401.</summary>
    public void InvalidateAccessToken() => AccessToken = "revoked-" + AccessToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var uri = request.RequestUri!;
        lock (_gate)
        {
            _log.Add($"{request.Method} {uri.AbsolutePath}");
            if (uri.Host == "oauth.test") return OAuth(uri.AbsolutePath, Form(Encoding.UTF8.GetString(body)));
            if (uri.AbsolutePath.StartsWith("/session/", StringComparison.Ordinal)) return FinishUpload(uri, request, body);

            var bearer = request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null;
            if (bearer is null || bearer != AccessToken || Time.Now >= AccessTokenExpires)
                return Error(HttpStatusCode.Unauthorized, "authError", "Request had invalid authentication credentials.", "UNAUTHENTICATED");

            var path = uri.AbsolutePath;
            var query = Query(uri);
            if (path == "/drive/v3/about") return Json(new { user = new { displayName = "Test User", emailAddress = "tester@example.com" } });
            if (path == "/upload/drive/v3/files" && request.Method == HttpMethod.Post) return StartUpload(query, request, body);
            if (path == "/drive/v3/files" && request.Method == HttpMethod.Get) return List(query);
            if (path == "/drive/v3/files" && request.Method == HttpMethod.Post) return Create(body);
            var match = FileRoute().Match(path);
            if (match.Success)
            {
                var id = Uri.UnescapeDataString(match.Groups[1].Value);
                if (!_files.TryGetValue(id, out var file))
                    return Error(HttpStatusCode.NotFound, "notFound", $"File not found: {id}.");
                if (request.Method == HttpMethod.Delete)
                {
                    _files.Remove(id);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (query.GetValueOrDefault("alt") == "media")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) };
                return Json(Resource(file));
            }
            return Error(HttpStatusCode.NotFound, "notFound", "Unknown route " + path);
        }
    }

    private HttpResponseMessage OAuth(string path, Dictionary<string, string> form)
    {
        if (path == "/revoke")
        {
            Revoked = true;
            return Json(new { });
        }
        if (form.GetValueOrDefault("client_id") != ClientId)
            return Json(new { error = "invalid_client", error_description = "The OAuth client was not found." }, HttpStatusCode.Unauthorized);
        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                if (form.GetValueOrDefault("code") != AuthorizationCode || form.GetValueOrDefault("client_secret") != ClientSecret)
                    return Json(new { error = "invalid_grant", error_description = "Malformed auth code." }, HttpStatusCode.BadRequest);
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? "")))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (challenge != IssuedChallenge)
                    return Json(new { error = "invalid_grant", error_description = "Invalid code verifier." }, HttpStatusCode.BadRequest);
                IssueAccessToken();
                return Json(new
                {
                    access_token = AccessToken, expires_in = 3599, refresh_token = RefreshToken, scope = GoogleDriveClient.Scope,
                    token_type = "Bearer",
                });
            case "refresh_token":
                if (RefreshRevoked || form.GetValueOrDefault("refresh_token") != RefreshToken)
                    return Json(new { error = "invalid_grant", error_description = "Token has been expired or revoked." }, HttpStatusCode.BadRequest);
                TokenRefreshes++;
                IssueAccessToken();
                return Json(new { access_token = AccessToken, expires_in = 3599, scope = GoogleDriveClient.Scope, token_type = "Bearer" });
            default:
                return Json(new { error = "unsupported_grant_type", error_description = "Invalid grant_type." }, HttpStatusCode.BadRequest);
        }
    }

    /// <summary>Hands out an access token without the OAuth dance (for tests that start signed in).</summary>
    public void IssueAccessToken()
    {
        AccessToken = $"ya29.test-{++_next}";
        AccessTokenExpires = Time.Now.AddSeconds(3599);
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        var q = query.GetValueOrDefault("q") ?? "";
        IEnumerable<DriveFile> files = _files.Values;
        if (q.Contains("trashed = false", StringComparison.Ordinal)) files = files.Where(f => !f.Trashed);
        if (NameClause().Match(q) is { Success: true } name) files = files.Where(f => f.Name == name.Groups[1].Value.Replace("\\'", "'"));
        if (q.Contains($"mimeType = '{GoogleDriveClient.FolderMimeType}'", StringComparison.Ordinal))
            files = files.Where(f => f.MimeType == GoogleDriveClient.FolderMimeType);
        if (q.Contains($"mimeType != '{GoogleDriveClient.FolderMimeType}'", StringComparison.Ordinal))
            files = files.Where(f => f.MimeType != GoogleDriveClient.FolderMimeType);
        if (ParentClause().Match(q) is { Success: true } parent)
            files = files.Where(f => parent.Groups[1].Value == "root" ? f.Parents.Count == 0 : f.Parents.Contains(parent.Groups[1].Value));
        files = (query.GetValueOrDefault("orderBy") ?? "") == "createdTime desc"
            ? files.OrderByDescending(f => f.CreatedTime)
            : files.OrderBy(f => f.CreatedTime);

        // Pages of 2, so the client must follow nextPageToken.
        var all = files.ToArray();
        var start = int.TryParse(query.GetValueOrDefault("pageToken"), out var token) ? token : 0;
        var page = all.Skip(start).Take(2).ToArray();
        var next = start + page.Length < all.Length ? (start + page.Length).ToString() : null;
        return next is null
            ? Json(new { kind = "drive#fileList", incompleteSearch = false, files = page.Select(Resource) })
            : Json(new { kind = "drive#fileList", nextPageToken = next, incompleteSearch = false, files = page.Select(Resource) });
    }

    private HttpResponseMessage Create(byte[] body)
    {
        var meta = JsonSerializer.Deserialize<JsonElement>(body);
        var file = new DriveFile
        {
            Id = NewId(), Name = meta.GetProperty("name").GetString()!, MimeType = meta.GetProperty("mimeType").GetString()!,
            CreatedTime = Time.Now,
        };
        _files[file.Id] = file;
        return Json(new { id = file.Id });
    }

    private HttpResponseMessage StartUpload(Dictionary<string, string> query, HttpRequestMessage request, byte[] body)
    {
        if (query.GetValueOrDefault("uploadType") != "resumable")
            return Error(HttpStatusCode.BadRequest, "badRequest", "Expected a resumable upload.");
        if (!request.Headers.Contains("X-Upload-Content-Length"))
            return Error(HttpStatusCode.BadRequest, "badRequest", "Missing X-Upload-Content-Length.");
        if (QuotaFull)
            return Error(HttpStatusCode.Forbidden, "storageQuotaExceeded", "The user's Drive storage quota has been exceeded.");
        var meta = JsonSerializer.Deserialize<JsonElement>(body);
        var parents = meta.TryGetProperty("parents", out var p) ? p.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
        if (parents.Any(id => !_files.ContainsKey(id)))
            return Error(HttpStatusCode.NotFound, "notFound", $"File not found: {parents[0]}.");
        var session = $"upload-{++_next}";
        _sessions[session] = (meta.GetProperty("name").GetString()!, meta.GetProperty("mimeType").GetString()!, parents);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Headers.Location = new Uri($"https://drive.test/session/{session}?upload_id={session}");
        return response;
    }

    private HttpResponseMessage FinishUpload(Uri uri, HttpRequestMessage request, byte[] body)
    {
        var session = uri.AbsolutePath["/session/".Length..];
        if (request.Method != HttpMethod.Put || !_sessions.Remove(session, out var meta))
            return Error(HttpStatusCode.NotFound, "notFound", "Upload session not found.");
        var file = new DriveFile
        {
            Id = NewId(), Name = meta.Name, MimeType = meta.MimeType, Parents = meta.Parents, Content = body, CreatedTime = Time.Now,
        };
        _files[file.Id] = file;
        Uploads.Add(file.Name);
        Time.Now = Time.Now.AddSeconds(1);   // distinct createdTime values
        return Json(Resource(file));
    }

    private string EnsureFolderLocked()
    {
        var folder = _files.Values.FirstOrDefault(f => f.MimeType == GoogleDriveClient.FolderMimeType && !f.Trashed);
        if (folder is not null) return folder.Id;
        folder = new DriveFile { Id = NewId(), Name = GoogleDriveBackups.FolderName, MimeType = GoogleDriveClient.FolderMimeType, CreatedTime = Time.Now };
        _files[folder.Id] = folder;
        return folder.Id;
    }

    private string NewId() => $"1Abc{++_next:D4}xyz";

    private static object Resource(DriveFile f) => new
    {
        kind = "drive#file", id = f.Id, name = f.Name, mimeType = f.MimeType, trashed = f.Trashed,
        size = f.MimeType == GoogleDriveClient.FolderMimeType ? null : f.Content.Length.ToString(),
        createdTime = f.CreatedTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
    };

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string reason, string message, string? statusName = null) =>
        Json(new
        {
            error = new
            {
                code = (int)status, message,
                errors = new[] { new { message, domain = "global", reason } },
                status = statusName ?? status.ToString().ToUpperInvariant(),
            },
        }, status);

    private static Dictionary<string, string> Form(string body) => Parse(body);

    private static Dictionary<string, string> Query(Uri uri) => Parse(uri.Query.TrimStart('?'));

    private static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            result[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    [GeneratedRegex(@"^/drive/v3/files/([^/]+)$")]
    private static partial Regex FileRoute();

    [GeneratedRegex(@"name = '((?:\\'|[^'])*)'")]
    private static partial Regex NameClause();

    [GeneratedRegex(@"'([^']+)' in parents")]
    private static partial Regex ParentClause();
}
