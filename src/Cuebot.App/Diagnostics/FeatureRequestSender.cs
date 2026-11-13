using Cuebot.App.ViewModels.Support;
using Cuebot.Core;
using Cuebot.Server.Mcp;

namespace Cuebot.App.Diagnostics;

/// <summary>
/// The Assistant's feature requests (MCP request_feature): sent to Cuebot's report intake as kind "feature", which files
/// them as public issues labelled enhancement. The text is scrubbed of secrets first, like Send report.
/// </summary>
public sealed class FeatureRequestSender(AppPaths paths, HttpMessageHandler? handler = null, Uri? endpoint = null,
    Func<EnvironmentSnapshot>? environment = null) : IFeatureRequestSender
{
    /// <summary>Longest body put into a GitHub link (browsers and GitHub refuse very long addresses).</summary>
    private const int MaxLinkBody = 1500;

    private readonly ReportSender _sender = new(handler, endpoint);
    private readonly Func<EnvironmentSnapshot> _environment = environment ?? (() => EnvironmentInfo.Collect(Program.BackendName));

    public async Task<FeatureRequestResult> SendAsync(string title, string body, CancellationToken ct = default)
    {
        title = SecretScrubber.ScrubText(title.Trim());
        body = SecretScrubber.ScrubText(body.Trim()) + "\n\n_Filed by Cuebot's Assistant after the streamer approved this text._";
        var environment = _environment();
        var result = await _sender.SendAsync("feature", title, body, ReportProblemViewModel.VersionOf(environment),
            ReportProblemViewModel.OsOf(environment), new ReportSettingsStore(paths).Settings.InstallId, ct: ct).ConfigureAwait(false);
        return result.Sent
            ? new FeatureRequestResult(true, result.Url, result.Number, "")
            : new FeatureRequestResult(false, "", 0, result.Error, BrowserLink(title, body));
    }

    /// <summary>GitHub's new-issue page with the request filled in (needs the streamer's GitHub account).</summary>
    public static string BrowserLink(string title, string body)
    {
        if (body.Length > MaxLinkBody) body = body[..MaxLinkBody] + "…";
        return $"{SupportLinks.NewIssueUrl}?labels=enhancement&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }
}
