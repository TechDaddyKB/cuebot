using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Scripting.Compat;
using Cuebot.Server.StreamerBot;

namespace Cuebot.UiTests;

/// <summary>
/// Checks the Streamer.bot WebSocket event names against the real composition root: every event type a loaded module's
/// triggers can raise maps to a name GetEvents advertises, and names under a Streamer.bot source are documented ones.
/// </summary>
public sealed class StreamerBotEventCatalogTests : IAsyncDisposable
{
    /// <summary>
    /// Cuebot events under a documented Streamer.bot source that Streamer.bot itself does not document. They keep the
    /// generic name (the module's event, PascalCased) so clients can still subscribe to them.
    /// </summary>
    internal static readonly HashSet<string> CuebotExtras = new(StringComparer.Ordinal)
    {
        "CrowdControl.Connected", "CrowdControl.Disconnected", "CrowdControl.EffectRefund", "CrowdControl.EffectRetry",
        "CrowdControl.GameSessionPaused",
        "DonorDrive.Connected", "DonorDrive.Disconnected",
        "Elgato.ControlCenterColorChanged", "Elgato.ControlCenterDeviceOffline", "Elgato.ControlCenterDeviceOnline",
        "Kick.Host", "Kick.MessageDeleted", "Kick.UserUnbanned",
        "Misc.FirstWords", "Misc.PresentViewers",
        "Obs.FilterStateChanged", "Obs.InputMuteChanged", "Obs.InputVolumeChanged", "Obs.MediaAction",
        "Obs.MediaEnded", "Obs.MediaStarted", "Obs.PreviewSceneChanged", "Obs.ProfileChanged", "Obs.RecordFileChanged",
        "Obs.RecordPaused", "Obs.RecordResumed", "Obs.ReplayBufferSaved", "Obs.ReplayBufferStarted",
        "Obs.ReplayBufferStopped", "Obs.SceneCollectionChanged", "Obs.ScreenshotSaved", "Obs.SourceVisibilityChanged",
        "Obs.StudioModeChanged", "Obs.TransitionChanged", "Obs.TransitionDurationChanged", "Obs.TransitionEnded",
        "Obs.TransitionStarted", "Obs.VirtualCamStarted", "Obs.VirtualCamStopped",
        "Patreon.MemberUpdated", "Patreon.PostPublished",
        "Pulsoid.Connected", "Pulsoid.Disconnected",
        "Quote.Removed", "Quote.Updated",
        "StreamElements.Activity", "StreamElements.CharityDonation", "StreamElements.Cheer",
        "StreamElements.CommunityGift", "StreamElements.Follow", "StreamElements.Host", "StreamElements.Raid",
        "StreamElements.Redemption", "StreamElements.Sponsor", "StreamElements.Subscriber", "StreamElements.Superchat",
        "StreamLoots.Alert", "StreamLoots.Connected", "StreamLoots.Disconnected",
        "Streamlabs.Bits", "Streamlabs.Follow", "Streamlabs.Host", "Streamlabs.LoyaltyStoreRedemption",
        "Streamlabs.PrimeSubGift", "Streamlabs.Raid", "Streamlabs.Subscription", "Streamlabs.Superchat",
        "StreamlabsDesktop.ReplayBufferSaved",
        "ThrowingSystem.Disconnected",
        "TipeeeStream.Cheer", "TipeeeStream.Connected", "TipeeeStream.Disconnected", "TipeeeStream.Follow",
        "TipeeeStream.Host", "TipeeeStream.Raid", "TipeeeStream.Subscription",
        "Twitch.BitsUsed", "Twitch.ChatSettingsUpdated", "Twitch.GiftSubscriptions", "Twitch.UserMessagesCleared",
        "VTubeStudio.Event", "VTubeStudio.ExpressionToggled", "VTubeStudio.ModelMoved",
        "VTubeStudio.PostProcessingChanged", "VTubeStudio.TrackingFound", "VTubeStudio.TrackingLost",
        "VoiceMod.BackgroundEffectsToggled", "VoiceMod.HearMyselfToggled", "VoiceMod.MuteToggled",
        "VoiceMod.VoiceChangerToggled",
        "YouTube.QuotaExceeded",
    };

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-sbmap-").FullName;
    private readonly AppHost _host;

    public StreamerBotEventCatalogTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private IEnumerable<string> EventTypes() => _host.Get<Registry>().Triggers.SelectMany(t => t.EventTypes).Distinct();

    [Fact]
    public void GetEvents_lists_every_name_the_modules_can_emit()
    {
        var catalog = StreamerBotRequests.Catalog(_host.Get<Registry>());
        foreach (var type in EventTypes())
        {
            foreach (var name in StreamerBotEventMap.PossibleNames(type))
            {
                Assert.True(catalog[name.Source] is { } list && list.AsArray().Any(t => t!.GetValue<string>() == name.Type),
                    $"{type} → {name} is not in GetEvents");
            }
        }
    }

    [Fact]
    public void Names_under_streamerbot_sources_are_documented()
    {
        var undocumented = EventTypes()
            .SelectMany(type => StreamerBotEventMap.PossibleNames(type).Select(name => (type, name)))
            .Where(x => StreamerBotEventNames.IsDocumentedSource(x.name.Source)
                        && !StreamerBotEventNames.IsDocumented(x.name.Source, x.name.Type)
                        && !CuebotExtras.Contains(x.name.ToString()))
            .Select(x => $"{x.type} → {x.name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var dump = Environment.GetEnvironmentVariable("CUEBOT_DUMP_EVENTS");
        if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(dump, undocumented);
        Assert.True(undocumented.Length == 0, "Undocumented names (fix the map or add to CuebotExtras):\n" + string.Join("\n", undocumented));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
