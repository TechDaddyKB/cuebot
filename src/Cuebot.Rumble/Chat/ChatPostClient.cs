using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cuebot.Rumble.Auth;

namespace Cuebot.Rumble.Chat;

/// <summary>
/// Posts chat messages the way Rumble's web chat does (unofficial, see PROTOCOL.md): a JSON body to the stream's
/// chat message endpoint, authenticated by the <c>u_s</c> session cookie.
/// </summary>
internal sealed class ChatPostClient(HttpClient http, RumbleEndpoints endpoints)
{
    /// <summary>Rumble's chat rejects longer messages.</summary>
    public const int MaxLength = 200;

    public async Task SendAsync(long chatId, string text, string sessionCookie, CancellationToken ct)
    {
        if (text.Length > MaxLength) throw new RumbleApiException($"Rumble chat messages are limited to {MaxLength} characters.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.ChatMessage(chatId))
        {
            Content = JsonContent.Create(new
            {
                data = new { request_id = NewRequestId(), message = new { text }, rant = (object?)null, channel_id = (long?)null },
            }),
        };
        request.Headers.Add("Cookie", $"{RumbleSession.CookieName}={sessionCookie}");
        request.Headers.Add("Origin", endpoints.Site.GetLeftPart(UriPartial.Authority));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new RumbleApiException("Rumble rejected the chat sign-in. Sign in to Rumble again.", (int)response.StatusCode)
            : new RumbleApiException($"Rumble did not accept the chat message (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
    }

    /// <summary>Random id the web chat attaches to each message (43 base64 characters, unpadded).</summary>
    private static string NewRequestId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');
}
