using System.Net;
using System.Text.Json;
using Cuebot.Rumble.Auth;

namespace Cuebot.Rumble.Chat;

/// <summary>How long a Rumble mute lasts: Rumble's website offers these three.</summary>
internal enum RumbleMute
{
    /// <summary>Five minutes, in this stream's chat.</summary>
    FiveMinutes,

    /// <summary>Until this stream ends.</summary>
    ThisStream,

    /// <summary>In every chat of the channel, until unmuted.</summary>
    Permanent,
}

/// <summary>
/// Deletes chat messages and mutes viewers the way Rumble's own web chat does (unofficial, see PROTOCOL.md section 4),
/// authenticated by the <c>u_s</c> session cookie. Only used when the streamer turned on
/// <see cref="RumbleSettings.UnofficialModeration"/>.
/// </summary>
internal sealed class ChatModerationClient(HttpClient http, RumbleEndpoints endpoints)
{
    public async Task DeleteMessageAsync(long chatId, string messageId, string sessionCookie, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("No message id to delete.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoints.ChatMessage(chatId, messageId.Trim()));
        Authorize(request, sessionCookie);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, "delete the message");
    }

    /// <summary>Mutes <paramref name="userName"/> (Rumble mutes by name) in the chat of <paramref name="chatId"/>.</summary>
    public async Task MuteAsync(long chatId, string userName, RumbleMute mute, string sessionCookie, CancellationToken ct)
    {
        userName = userName.Trim().TrimStart('@');
        if (userName.Length == 0) throw new ArgumentException("No user name to mute.");
        var form = new List<KeyValuePair<string, string>>
        {
            new("user_to_mute", userName),
            new("entity_type", "user"),
            new("video", chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("type", mute == RumbleMute.Permanent ? "total" : "video"),
        };
        if (mute == RumbleMute.FiveMinutes) form.Add(new("duration", "300"));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Service("moderation.mute")) { Content = new FormUrlEncodedContent(form) };
        Authorize(request, sessionCookie);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        Check(response, "mute the viewer");
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (ReportsFailure(body)) throw new RumbleApiException("Rumble did not mute the viewer.");
    }

    private void Authorize(HttpRequestMessage request, string sessionCookie)
    {
        if (sessionCookie.Length == 0) throw new InvalidOperationException("Sign in to Rumble chat first.");
        request.Headers.Add("Cookie", $"{RumbleSession.CookieName}={sessionCookie}");
        request.Headers.Add("Origin", endpoints.Site.GetLeftPart(UriPartial.Authority));
    }

    private static void Check(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new RumbleApiException($"Rumble refused to {what}. Sign in to Rumble again, as the channel owner or a moderator.", (int)response.StatusCode)
            : new RumbleApiException($"Rumble did not {what} (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
    }

    /// <summary>service.php answers <c>{"data": {"success": false}}</c> when it refuses.</summary>
    private static bool ReportsFailure(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
