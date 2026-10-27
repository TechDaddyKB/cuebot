using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Donations;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.Pally;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.TreatStream;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in, read-only tests against the real TipeeeStream, DonorDrive, TreatStream and Pally.gg. Each connects, checks the
/// service answers and disconnects; nothing reaches viewers. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class CharityLiveTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    [LiveServiceFact("CUEBOT_LIVE_TIPEEESTREAM_API_KEY")]
    public async Task TipeeeStream_socket_connects_with_the_api_key()
    {
        await using var services = Build("tipeeestream");
        var tipeee = services.GetRequiredService<TipeeeStreamService>();
        tipeee.Settings.ApiKey = ServicesLiveEnvironment.Get("CUEBOT_LIVE_TIPEEESTREAM_API_KEY")!;
        tipeee.Settings.Username = ServicesLiveEnvironment.Get("CUEBOT_LIVE_TIPEEESTREAM_USERNAME") ?? "";
        await tipeee.ConnectAsync();
        await WaitConnectedAsync(tipeee);
        await tipeee.DisconnectAsync();
    }

    /// <summary>Public data, no key: two or three requests (profile, donations, incentives), well inside DonorDrive's limits.</summary>
    [LiveServiceFact("CUEBOT_LIVE_DONORDRIVE_PARTICIPANT_ID")]
    public async Task DonorDrive_reads_the_participant()
    {
        await using var services = Build("donordrive");
        var dd = services.GetRequiredService<DonorDriveService>();
        dd.Settings.ProfileId = ServicesLiveEnvironment.Get("CUEBOT_LIVE_DONORDRIVE_PARTICIPANT_ID")!;
        dd.Settings.ProfileType = ServicesLiveEnvironment.Get("CUEBOT_LIVE_DONORDRIVE_TEAM") == "1" ? DonorDriveProfileType.Team : DonorDriveProfileType.Participant;
        if (ServicesLiveEnvironment.Get("CUEBOT_LIVE_DONORDRIVE_BASE_URL") is { } site)
        {
            dd.Settings.Program = DonorDriveProgram.Custom;
            dd.Settings.CustomBaseUrl = site;
        }
        dd.Settings.PollSeconds = 60;   // one poll during the test
        await dd.ConnectAsync();
        await WaitConnectedAsync(dd);
        Assert.NotNull(dd.Profile);
        Assert.False(string.IsNullOrEmpty(dd.Profile!.Name));
        await dd.DisconnectAsync();
    }

    [LiveServiceFact("CUEBOT_LIVE_TREATSTREAM_TOKEN")]
    public async Task TreatStream_gets_a_socket_token_and_connects()
    {
        await using var services = Build("treatstream");
        var treat = services.GetRequiredService<TreatStreamService>();
        treat.Settings.AccessToken = ServicesLiveEnvironment.Get("CUEBOT_LIVE_TREATSTREAM_TOKEN")!;
        treat.Settings.ClientId = ServicesLiveEnvironment.Get("CUEBOT_LIVE_TREATSTREAM_CLIENT_ID")
            ?? throw new InvalidOperationException("Set CUEBOT_LIVE_TREATSTREAM_CLIENT_ID too (the socket token needs the app's client id)");
        await treat.ConnectAsync();
        await WaitConnectedAsync(treat);
        await treat.DisconnectAsync();
    }

    /// <summary>Connects and asks Pally's server to echo a sample tip, which must come back as a (test) campaign tip.</summary>
    [LiveServiceFact("CUEBOT_LIVE_PALLY_API_KEY")]
    public async Task Pally_connects_and_echoes_a_test_tip()
    {
        await using var services = Build("pally");
        var pally = services.GetRequiredService<PallyService>();
        var tips = new List<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(e =>
        {
            if (e.Type == PallyEventTypes.CampaignTip) lock (tips) tips.Add(e);
        });
        pally.Settings.ApiKey = ServicesLiveEnvironment.Get("CUEBOT_LIVE_PALLY_API_KEY")!;
        await pally.ConnectAsync();
        await WaitConnectedAsync(pally);
        Assert.True(await pally.SendEchoTestAsync());
        await KickLiveBot.WaitUntilAsync(() => { lock (tips) return tips.Count > 0; }, ConnectTimeout, "the echoed Pally.gg tip");
        lock (tips) Assert.Equal(true, tips[0].Args["isTest"]);
        await pally.DisconnectAsync();
    }

    private static ServiceProvider Build(string name)
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, name);
        Directory.CreateDirectory(dataDirectory);
        return new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddDonations().BuildServiceProvider();
    }

    private static async Task WaitConnectedAsync(IConnectionStatus service)
    {
        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (service.State != ConnectionState.Connected)
        {
            if (service.State == ConnectionState.Error || DateTime.UtcNow > deadline)
                throw new TimeoutException($"{service.Name} did not connect within {ConnectTimeout.TotalSeconds:0}s (status: {service.StatusMessage})");
            await Task.Delay(200);
        }
    }
}
