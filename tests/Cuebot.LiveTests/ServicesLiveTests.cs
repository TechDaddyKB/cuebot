using System.Runtime.CompilerServices;
using Cuebot.Core;
using Cuebot.Donations;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Integrations;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in, read-only connection tests against the real Streamlabs, StreamElements, VTube Studio and Voicemod.
/// Each one connects, checks the service answers, and disconnects; nothing is sent to viewers. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class ServicesLiveTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The first VTube Studio connection waits for you to click "Allow" on the plugin popup in VTS.</summary>
    private static readonly TimeSpan VtsApprovalTimeout = TimeSpan.FromMinutes(2);

    [LiveServiceFact("CUEBOT_LIVE_STREAMLABS_SOCKET_TOKEN")]
    public async Task Streamlabs_socket_connects()
    {
        await using var services = Build("streamlabs");
        var streamlabs = services.GetRequiredService<StreamlabsService>();
        streamlabs.Settings.SocketToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMLABS_SOCKET_TOKEN")!;
        await streamlabs.ConnectAsync();
        await WaitConnectedAsync(streamlabs, ConnectTimeout);
        await streamlabs.DisconnectAsync();
    }

    /// <summary>
    /// Read-only Streamlabs REST checks with an access token from "Connect with Streamlabs" (nothing shows on stream):
    /// the account, the socket token, the alert queue status, the latest donation and the alert profiles answer, and
    /// the socket connects with the fetched token. Optional CUEBOT_LIVE_STREAMLABS_POINTS_CHANNEL / _POINTS_USER also
    /// read one viewer's Cloudbot points.
    /// </summary>
    [LiveServiceFact("CUEBOT_LIVE_STREAMLABS_ACCESS_TOKEN")]
    public async Task Streamlabs_rest_reads_answer()
    {
        await using var services = Build("streamlabs-rest");
        var streamlabs = services.GetRequiredService<StreamlabsService>();
        streamlabs.Settings.AccessToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMLABS_ACCESS_TOKEN")!;
        streamlabs.Settings.SocketToken = "";
        var user = await streamlabs.GetUserAsync();
        Assert.NotEmpty(user.DisplayName);
        await streamlabs.IsAlertQueuePausedAsync();
        Assert.NotNull(await streamlabs.GetDonationsAsync(1));
        Assert.NotNull(await streamlabs.GetAlertProfilesAsync());
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMLABS_POINTS_USER") is { } viewer)
            await streamlabs.GetPointsAsync(viewer, ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMLABS_POINTS_CHANNEL"));
        await streamlabs.ConnectAsync();   // fetches the socket token first
        await WaitConnectedAsync(streamlabs, ConnectTimeout);
        await streamlabs.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_STREAMELEMENTS_JWT")]
    public async Task StreamElements_connects_with_the_jwt()
    {
        await using var services = Build("streamelements");
        var se = services.GetRequiredService<StreamElementsService>();
        se.Settings.JwtToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMELEMENTS_JWT")!;
        await se.ConnectAsync();
        await WaitConnectedAsync(se, ConnectTimeout);
        await se.DisconnectAsync();
    }

    /// <summary>
    /// Read-only REST checks and one Astro session with every topic (nothing is visible to viewers): the channel, points
    /// top list, latest tip, chatbot status and session data answer, and the activities topic is subscribed in the
    /// channel's room. Which other topics the JWT may read is written to the test output via the log.
    /// </summary>
    [LiveServiceFact("CUEBOT_LIVE_STREAMELEMENTS_JWT")]
    public async Task StreamElements_rest_reads_and_topics_answer()
    {
        await using var services = Build("streamelements");
        var se = services.GetRequiredService<StreamElementsService>();
        se.Settings.JwtToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_STREAMELEMENTS_JWT")!;
        se.ResetChannel();
        Assert.NotEmpty(await se.GetChannelIdAsync());
        Assert.NotNull(await se.GetPointsLeaderboardAsync(3));
        Assert.NotNull(await se.GetTipsAsync(1));
        Assert.NotNull(await se.GetBotAsync());
        await se.ConnectAsync();
        await WaitConnectedAsync(se, ConnectTimeout);
        await KickLiveBot.WaitUntilAsync(() => se.SubscribedTopics.Count >= 5, ConnectTimeout, "StreamElements topics");
        Assert.Contains(StreamElementsTopics.Activities, se.SubscribedTopics);
        Assert.NotEmpty(se.Room);
        await se.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_VTS", flag: true)]
    public async Task VTube_Studio_authenticates_and_reads_the_model_and_hotkeys()
    {
        // Kept between runs so the plugin approval in VTube Studio is only needed once.
        await using var services = Build("vtubestudio");
        var vts = services.GetRequiredService<VTubeStudioService>();
        if (int.TryParse(ServicesLiveEnvironment.Get("CUEBOT_LIVE_VTS_PORT"), out var port)) vts.Settings.Port = port;
        await vts.ConnectAsync();
        await WaitConnectedAsync(vts, VtsApprovalTimeout);
        vts.SaveSettings();

        Assert.False(string.IsNullOrEmpty(vts.Version));
        var model = await vts.GetCurrentModelAsync();
        Assert.NotNull(model);
        Assert.NotNull(await vts.GetHotkeysAsync());
        await vts.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_VOICEMOD_API_KEY")]
    public async Task Voicemod_connects_and_lists_voices()
    {
        await using var services = Build("voicemod");
        var voicemod = services.GetRequiredService<VoicemodService>();
        voicemod.Settings.ClientKey = ServicesLiveEnvironment.Get("CUEBOT_LIVE_VOICEMOD_API_KEY")!;
        await voicemod.ConnectAsync();
        await WaitConnectedAsync(voicemod, ConnectTimeout);
        await KickLiveBot.WaitUntilAsync(() => voicemod.Voices.Count > 0, ConnectTimeout, "Voicemod's voice list");
        await voicemod.DisconnectAsync();
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddDonations().AddIntegrations().BuildServiceProvider();
    }

    private static async Task WaitConnectedAsync(IConnectionStatus service, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (service.State != ConnectionState.Connected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{service.Name} did not connect within {timeout.TotalSeconds:0}s (status: {service.StatusMessage})");
            await Task.Delay(200);
        }
    }
}

internal static class ServicesLiveEnvironment
{
    public static string? Get(string name)
    {
        // LiveEnvironment's static constructor loads live.env into the environment.
        RuntimeHelpers.RunClassConstructor(typeof(LiveEnvironment).TypeHandle);
        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }
}

/// <summary>Runs only when <c>variable</c> is set (or, for a flag, set to 1).</summary>
public sealed class LiveServiceFactAttribute : LiveFactAttribute
{
    public LiveServiceFactAttribute(string variable, bool flag = false)
    {
        if (Skip is not null) return;
        var value = ServicesLiveEnvironment.Get(variable);
        if (flag ? value != "1" : value is null)
            Skip = $"Set {variable}{(flag ? "=1" : "")} to run against the real service (see docs/LIVE_TESTING.md)";
    }
}
