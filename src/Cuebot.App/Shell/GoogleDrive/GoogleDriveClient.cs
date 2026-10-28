using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cuebot.App.Shell.GoogleDrive;

/// <summary>Google's OAuth and Drive v3 REST endpoints; tests point them at a fake server.</summary>
public sealed record GoogleDriveEndpoints(Uri OAuth, Uri Authorize, Uri Api, Uri Upload)
{
    public static GoogleDriveEndpoints Google { get; } = new(
        new Uri("https://oauth2.googleapis.com/"),
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://www.googleapis.com/drive/v3/"),
        new Uri("https://www.googleapis.com/upload/drive/v3/"));
}

/// <summary>A Drive call failed. <see cref="SignInRequired"/>: the refresh token is gone, expired or revoked.</summary>
public sealed class GoogleDriveException(string message, int status = 0, string reason = "", bool signInRequired = false)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public bool SignInRequired { get; } = signInRequired;
}

/// <summary>A backup zip in the Drive folder.</summary>
public sealed record RemoteBackup(string Id, string Name, long Size, DateTimeOffset CreatedTime)
{
    /// <summary>When the backup was made: from its name when it is a Cuebot backup name, else Drive's created time.</summary>
    public DateTime Created => BackupService.TryParseName(Name, out var created, out _) ? created : CreatedTime.LocalDateTime;

    public string Reason => BackupService.TryParseName(Name, out _, out var reason) ? reason : "";
}

/// <summary>The tokens of the authorization-code exchange.</summary>
public sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Google OAuth 2.0 for installed apps (authorization code + PKCE, refresh, revoke) and the few Drive v3 calls the
/// backups need: find/create a folder, list, resumable upload, download and delete. Only the
/// <c>drive.file</c> scope is used, so Cuebot sees nothing but the files it created. Plain REST, no Google SDK.
/// Never logs or puts tokens in exception messages.
/// </summary>
public sealed class GoogleDriveClient(HttpClient http, GoogleDriveEndpoints endpoints, TimeProvider time) : IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public const string FolderMimeType = "application/vnd.google-apps.folder";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string _accessToken = "";
    private DateTimeOffset _accessTokenExpiresAt;
    private string _accessTokenFor = "";

    /// <summary>Client id, secret and refresh token of the signed-in account; read on every call.</summary>
    public Func<(string ClientId, string ClientSecret, string RefreshToken)> Credentials { get; set; } = () => ("", "", "");

    // ---------------------------------------------------------------- OAuth

    /// <summary>The browser URL that starts the loopback sign-in (authorization code + PKCE, offline access).</summary>
    public Uri BuildAuthorizeUri(string clientId, string redirectUri, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        };
        var text = string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{endpoints.Authorize}?{text}");
    }

    public async Task<GoogleTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string codeVerifier,
        string redirectUri, CancellationToken ct)
    {
        using var response = await PostFormAsync("token", new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw OAuthError("Google sign-in failed", response, body);
        var tokens = ParseTokens(body, "");
        if (tokens.RefreshToken.Length == 0)
            throw new GoogleDriveException("Google did not return a refresh token. Remove Cuebot's access in your Google account and sign in again.");
        Remember(tokens.AccessToken, tokens.ExpiresAt, tokens.RefreshToken);
        return tokens;
    }

    /// <summary>Revokes the grant at Google (best effort, errors are ignored) and forgets the access token.</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        Forget();
        try
        {
            using var response = await PostFormAsync("revoke", new() { ["token"] = token }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { }
    }

    /// <summary>Drops the cached access token (after sign-out or when the account changes).</summary>
    public void Forget()
    {
        _accessToken = "";
        _accessTokenFor = "";
        _accessTokenExpiresAt = default;
    }

    private async Task<string> GetAccessTokenAsync(string rejected, CancellationToken ct)
    {
        var (clientId, clientSecret, refreshToken) = Credentials();
        if (refreshToken.Length == 0)
            throw new GoogleDriveException("Sign in to Google Drive first.", 401, "signInRequired", signInRequired: true);
        if (Valid(refreshToken, rejected)) return _accessToken;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Valid(refreshToken, rejected)) return _accessToken;
            using var response = await PostFormAsync("token", new()
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }, ct).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw OAuthError("Refreshing the Google Drive sign-in failed", response, body);
            var tokens = ParseTokens(body, refreshToken);
            Remember(tokens.AccessToken, tokens.ExpiresAt, refreshToken);
            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private bool Valid(string refreshToken, string rejected) =>
        _accessToken.Length > 0 && _accessToken != rejected && _accessTokenFor == refreshToken
        && _accessTokenExpiresAt - RefreshMargin > time.GetUtcNow();

    private void Remember(string accessToken, DateTimeOffset expiresAt, string refreshToken)
    {
        _accessToken = accessToken;
        _accessTokenExpiresAt = expiresAt;
        _accessTokenFor = refreshToken;
    }

    // ---------------------------------------------------------------- Drive

    /// <summary>The signed-in account's e-mail address (about.get, allowed with drive.file).</summary>
    public async Task<string> GetAccountEmailAsync(CancellationToken ct)
    {
        var body = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, Api("about?fields=user(emailAddress,displayName)")), ct)
            .ConfigureAwait(false);
        return body.TryGetProperty("user", out var user) ? Str(user, "emailAddress") : "";
    }

    /// <summary>
    /// The id of the folder named <paramref name="name"/> in My Drive: <paramref name="knownId"/> when it still exists
    /// (not trashed), else the first such folder Cuebot created, else a new one.
    /// </summary>
    public async Task<string> EnsureFolderAsync(string name, string knownId, CancellationToken ct)
    {
        if (knownId.Length > 0)
        {
            try
            {
                var existing = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    Api($"files/{Uri.EscapeDataString(knownId)}?fields=id,trashed")), ct).ConfigureAwait(false);
                if (!Bool(existing, "trashed")) return knownId;
            }
            catch (GoogleDriveException ex) when (ex.Status == 404) { }
        }

        var q = $"name = '{Escape(name)}' and mimeType = '{FolderMimeType}' and 'root' in parents and trashed = false";
        var found = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
            Api($"files?q={Uri.EscapeDataString(q)}&spaces=drive&fields=files(id,name)&orderBy=createdTime&pageSize=10")), ct).ConfigureAwait(false);
        if (found.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
            return Str(files[0], "id");

        var created = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Post, Api("files?fields=id"))
        {
            Content = JsonContent(new Dictionary<string, object> { ["name"] = name, ["mimeType"] = FolderMimeType }),
        }, ct).ConfigureAwait(false);
        return Str(created, "id");
    }

    /// <summary>Every zip in the folder (not trashed), newest first.</summary>
    public async Task<IReadOnlyList<RemoteBackup>> ListAsync(string folderId, CancellationToken ct)
    {
        var q = $"'{Escape(folderId)}' in parents and trashed = false and mimeType != '{FolderMimeType}'";
        var result = new List<RemoteBackup>();
        var pageToken = "";
        do
        {
            var url = $"files?q={Uri.EscapeDataString(q)}&spaces=drive&orderBy=createdTime%20desc&pageSize=100" +
                      "&fields=nextPageToken,files(id,name,size,createdTime)" +
                      (pageToken.Length > 0 ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
            var page = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, Api(url)), ct).ConfigureAwait(false);
            if (page.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (Str(f, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) result.Add(ParseFile(f));
            pageToken = Str(page, "nextPageToken");
        } while (pageToken.Length > 0);
        return result.OrderByDescending(b => b.Created).ThenByDescending(b => b.CreatedTime).ToArray();
    }

    /// <summary>
    /// Uploads <paramref name="path"/> into the folder with a resumable upload: one request starts the session
    /// (metadata), one PUT sends the bytes.
    /// </summary>
    public async Task<RemoteBackup> UploadAsync(string folderId, string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var length = new FileInfo(path).Length;
        var metadata = new Dictionary<string, object> { ["name"] = name, ["mimeType"] = "application/zip", ["parents"] = new[] { folderId } };

        Uri? session = null;
        await SendAsync(() =>
        {
            var start = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoints.Upload,
                "files?uploadType=resumable&fields=id,name,size,createdTime")) { Content = JsonContent(metadata) };
            start.Headers.Add("X-Upload-Content-Type", "application/zip");
            start.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
            return start;
        }, response =>
        {
            session = response.Headers.Location
                      ?? throw new GoogleDriveException("Google Drive did not return an upload session.", (int)response.StatusCode);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        // The session URL itself authorizes the upload; no bearer token is needed (or sent) for the bytes.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(file) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        put.Content.Headers.ContentLength = length;
        using var uploaded = await http.SendAsync(put, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(uploaded, ct).ConfigureAwait(false);
        if (!uploaded.IsSuccessStatusCode) throw ApiError("Uploading to Google Drive failed", uploaded, body);
        var result = ParseFile(body);
        return result with { Name = result.Name.Length > 0 ? result.Name : name, Size = result.Size > 0 ? result.Size : length };
    }

    /// <summary>Downloads a file's content to <paramref name="target"/> (written to a temp file, then renamed).</summary>
    public async Task DownloadAsync(string fileId, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Api($"files/{Uri.EscapeDataString(fileId)}?alt=media")),
                async response =>
                {
                    await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
                }, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task DeleteAsync(string fileId, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Api($"files/{Uri.EscapeDataString(fileId)}")), _ => Task.CompletedTask, ct);

    // ---------------------------------------------------------------- plumbing

    private Uri Api(string relative) => new(endpoints.Api, relative);

    private async Task<JsonElement> SendJsonAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        var body = default(JsonElement);
        await SendAsync(request, async response => body = await ReadJsonAsync(response, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>Sends with a bearer token; on 401 refreshes once and retries. Non-success answers throw.</summary>
    private async Task SendAsync(Func<HttpRequestMessage> build, Func<HttpResponseMessage, Task> read, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var token = await GetAccessTokenAsync("", ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, completion, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = await GetAccessTokenAsync(token, ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw ApiError("Google Drive refused the request", response, await ReadJsonAsync(response, ct).ConfigureAwait(false));
            await read(response).ConfigureAwait(false);
            return;
        }
    }

    private Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
        http.PostAsync(new Uri(endpoints.OAuth, path), new FormUrlEncodedContent(form), ct);

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private GoogleTokens ParseTokens(JsonElement body, string refreshFallback)
    {
        var refresh = Str(body, "refresh_token");
        var expiresIn = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600;
        return new GoogleTokens(Str(body, "access_token"), refresh.Length > 0 ? refresh : refreshFallback,
            time.GetUtcNow().AddSeconds(expiresIn > 0 ? expiresIn : 3600));
    }

    private static RemoteBackup ParseFile(JsonElement f)
    {
        // Drive returns int64 values such as size as strings.
        var size = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("size", out var s)
            ? s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
              : s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0
            : 0;
        DateTimeOffset.TryParse(Str(f, "createdTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created);
        return new RemoteBackup(Str(f, "id"), Str(f, "name"), size, created);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Escapes a value inside single quotes of a Drive search query.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static GoogleDriveException OAuthError(string prefix, HttpResponseMessage response, JsonElement body)
    {
        var error = Str(body, "error");
        var description = Str(body, "error_description");
        var detail = description.Length > 0 ? $"{error}: {description}" : error.Length > 0 ? error : $"HTTP {(int)response.StatusCode}";
        if (error == "invalid_grant") detail += " (sign in again; Google Cloud projects in Testing mode expire sign-ins after 7 days)";
        return new GoogleDriveException($"{prefix}: {detail}", (int)response.StatusCode, error, signInRequired: error == "invalid_grant");
    }

    /// <summary>Drive's error body: <c>{"error":{"code":403,"message":"…","errors":[{"reason":"…"}]}}</c>.</summary>
    private static GoogleDriveException ApiError(string prefix, HttpResponseMessage response, JsonElement body)
    {
        var message = "";
        var reason = "";
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            message = Str(error, "message");
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                reason = Str(errors[0], "reason");
            if (reason.Length == 0) reason = Str(error, "status");
        }
        var status = (int)response.StatusCode;
        var detail = message.Length > 0 ? message : $"HTTP {status}";
        if (reason is "storageQuotaExceeded") detail = "Your Google Drive is full. " + detail;
        return new GoogleDriveException($"{prefix}: {detail}", status, reason, signInRequired: status == 401);
    }

    public void Dispose()
    {
        http.Dispose();
        _tokenGate.Dispose();
    }
}
