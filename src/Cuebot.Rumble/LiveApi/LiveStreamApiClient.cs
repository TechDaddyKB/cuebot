using System.Net;
using System.Text.Json;

namespace Cuebot.Rumble.LiveApi;

/// <summary>Fetches the user's Live Stream API URL. Error messages never contain the URL (it holds the key).</summary>
internal sealed class LiveStreamApiClient(HttpClient http)
{
    public async Task<LiveStreamSnapshot> FetchAsync(Uri apiUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RumbleApiException($"Could not reach the Rumble Live Stream API ({ex.HttpRequestError})");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                return LiveStreamApiParser.Parse(json);
            }
            catch (JsonException)
            {
                throw new RumbleApiException("The Rumble Live Stream API URL did not return Live Stream API data. Check the URL.");
            }
        }
    }

    private static RumbleApiException StatusError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => new(
            "Rumble rejected the Live Stream API URL. Copy it again from rumble.com → Account → Live Stream API.", (int)status),
        HttpStatusCode.TooManyRequests => new("Rumble is rate-limiting the Live Stream API; polling more slowly.", (int)status),
        _ => new($"The Rumble Live Stream API answered HTTP {(int)status}", (int)status),
    };
}
