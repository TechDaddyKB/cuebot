using System.Collections.Concurrent;
using System.Diagnostics;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Obs;
using Cuebot.Twitch;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// A real Cuebot (core engine + Twitch + OBS modules) shared by the live tests. Signs in to Twitch once per
/// data directory: the first run opens the device-activation page in your browser and waits for approval.
/// </summary>
public sealed class LiveBot : IAsyncLifetime
{
    public const string TestCommand = "!cuebottest";
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private ServiceProvider? _services;
    private readonly ConcurrentQueue<BotEvent> _events = new();

    public TwitchService Twitch => Get<TwitchService>();
    public ObsService Obs => Get<ObsService>();
    public ProfileService Profile => Get<ProfileService>();
    public IReadOnlyCollection<BotEvent> Events => _events;

    private T Get<T>() where T : notnull => (_services ?? throw new InvalidOperationException("Live tests are not configured")).GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var clientId = LiveEnvironment.TwitchClientId;
        if (!LiveEnvironment.RunRequested || (clientId is null && LiveEnvironment.ObsPassword is null)) return;   // every test skips

        Directory.CreateDirectory(LiveEnvironment.DataDirectory);
        _services = new ServiceCollection()
            .AddCuebotCore(new AppPaths(LiveEnvironment.DataDirectory))
            .AddTwitch()
            .AddObs()
            .BuildServiceProvider();
        Get<EventBus>().Subscribe(_events.Enqueue);
        AddTestActions();
        Get<ActionEngine>().Start();

        if (clientId is not null)
        {
            Twitch.Settings.ClientId = clientId;
            Twitch.Settings.AutoConnect = false;
            if (!Twitch.IsAuthorized) await SignInAsync();
            await Twitch.ConnectAsync();
            await WaitUntilAsync(() => Twitch.State == ConnectionState.Connected, TimeSpan.FromSeconds(30), "Twitch EventSub connection");
        }

        if (LiveEnvironment.ObsPassword is { } password)
        {
            var obs = Get<JsonFile<ObsSettings>>().Value;
            obs.Host = LiveEnvironment.ObsHost;
            obs.Port = LiveEnvironment.ObsPort;
            obs.Password = password;
            obs.AutoConnect = false;
            await Obs.ConnectAsync();
        }
    }

    private async Task SignInAsync()
    {
        var auth = await Twitch.BeginAuthorizationAsync();
        var message = $"Approve Cuebot's live tests on Twitch: {auth.VerificationUri} (code {auth.UserCode})";
        Console.Error.WriteLine(message);
        await File.WriteAllTextAsync(Path.Combine(LiveEnvironment.DataDirectory, "sign-in.txt"), message);
        try { Process.Start(new ProcessStartInfo(auth.VerificationUri) { UseShellExecute = true }); }
        catch { /* headless: use the printed link or sign-in.txt */ }

        using var timeout = new CancellationTokenSource(SignInTimeout);
        await Twitch.CompleteAuthorizationAsync(auth, timeout.Token);
    }

    /// <summary>Actions the tests drive, built the same way the editor would build them.</summary>
    private void AddTestActions()
    {
        Profile.Profile.Actions.AddRange(
        [
            new ActionDefinition
            {
                Name = "live test: command",
                Triggers = [new TriggerDefinition { Type = "twitch.command", Settings = { ["command"] = TestCommand } }],
                SubActions =
                [
                    new SubActionDefinition
                    {
                        Type = "twitch.sendMessage",
                        Parameters = { ["message"] = "dev testing: Cuebot received %command% from %user% (automated test)" },
                    },
                ],
            },
            new ActionDefinition
            {
                Name = "live test: stream online",
                Triggers = [new TriggerDefinition { Type = "twitch.streamOnline" }],
                SubActions =
                [
                    new SubActionDefinition
                    {
                        Type = "twitch.sendMessage",
                        Parameters = { ["message"] = "dev testing: Cuebot saw stream.online from Twitch (automated test)" },
                    },
                ],
            },
        ]);
        Profile.NotifyChanged();
    }

    /// <summary>
    /// Points OBS at <paramref name="target"/> (custom RTMP server + key) until the returned scope is disposed,
    /// then restores whatever stream settings OBS had before.
    /// </summary>
    public async Task<IAsyncDisposable> UseStreamTargetAsync(StreamTarget target)
    {
        var previous = await Obs.RequestAsync("GetStreamServiceSettings");
        await Obs.RequestAsync("SetStreamServiceSettings", new
        {
            streamServiceType = "rtmp_custom",
            streamServiceSettings = new { server = target.Url, key = target.Key },
        });
        return new Restore(async () =>
        {
            await StopStreamIfActiveAsync();   // OBS refuses to change stream settings while live
            await Obs.RequestAsync("SetStreamServiceSettings", new
            {
                streamServiceType = previous.GetProperty("streamServiceType").GetString(),
                streamServiceSettings = previous.GetProperty("streamServiceSettings"),
            });
        });
    }

    /// <summary>Stops the stream (if live) and waits until OBS reports it inactive.</summary>
    public async Task StopStreamIfActiveAsync()
    {
        if (!Obs.IsConnected || !(await Obs.RequestAsync("GetStreamStatus")).GetProperty("outputActive").GetBoolean()) return;
        await Obs.ControlStreamAsync(OutputCommand.Stop);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await Obs.RequestAsync("GetStreamStatus")).GetProperty("outputActive").GetBoolean() && DateTime.UtcNow < deadline)
            await Task.Delay(500);
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }

    /// <summary>Waits for an event published after <paramref name="since"/>.</summary>
    public async Task<BotEvent> WaitForEventAsync(string type, DateTimeOffset since, TimeSpan timeout, Func<BotEvent, bool>? match = null)
    {
        BotEvent? hit = null;
        await WaitUntilAsync(() =>
        {
            hit = _events.FirstOrDefault(e => e.Type == type && e.Time >= since && (match?.Invoke(e) ?? true));
            return hit is not null;
        }, timeout, type);
        return hit!;
    }

    public Task<BotEvent> WaitForChatAsync(string containing, DateTimeOffset since, TimeSpan timeout) =>
        WaitForEventAsync(TwitchEventTypes.ChatMessage, since, timeout,
            e => (e.Args.GetValueOrDefault("message") as string)?.Contains(containing, StringComparison.Ordinal) == true);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}");
            await Task.Delay(250);
        }
    }

    public async Task DisposeAsync()
    {
        if (_services is null) return;
        // Leave the Twitch sign-in in the data directory so the next run needs no approval.
        // Revoke it any time by deleting the directory and disconnecting the app on twitch.tv/settings/connections.
        await StopStreamIfActiveAsync();
        Get<ProfileService>().Flush();
        await _services.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class LiveCollection : ICollectionFixture<LiveBot>
{
    public const string Name = "Live Twitch and OBS";
}
