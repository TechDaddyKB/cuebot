namespace Cuebot.Server.Mcp;

/// <summary>
/// Files a feature request (a public GitHub issue labelled enhancement) through Cuebot's report intake. The app provides
/// it; without one the request_feature tool says it is unavailable.
/// </summary>
public interface IFeatureRequestSender
{
    Task<FeatureRequestResult> SendAsync(string title, string body, CancellationToken ct = default);
}

/// <summary>The issue that was filed, or why not plus a prefilled GitHub link the streamer can open instead.</summary>
public sealed record FeatureRequestResult(bool Sent, string Url, int Number, string Error, string BrowserUrl = "");
