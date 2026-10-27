using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Scripting;
using Cuebot.Speech;
using Cuebot.Twitch;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.App;

/// <summary>Composition root: wires core and modules together and owns their lifetime.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private AppHost(ServiceProvider services) => _services = services;

    public static AppHost Create(AppPaths paths)
    {
        var firstRun = !File.Exists(paths.File("actions.json"));
        var services = new ServiceCollection()
            .AddCuebotCore(paths)
            .AddTwitch()
            .AddObs()
            .AddSpeech()
            .AddScripting();
        var host = new AppHost(services.BuildServiceProvider());
        if (firstRun)
        {
            var profile = host.Get<ProfileService>();
            SampleProfile.Seed(profile.Profile);
            profile.NotifyChanged();
        }
        return host;
    }

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public IEnumerable<T> GetAll<T>() => _services.GetServices<T>();

    /// <summary>Starts the engine and auto-connects platforms. Never throws; failures are logged.</summary>
    public async Task StartAsync()
    {
        var log = Get<AppLog>();
        log.Info("Cuebot", $"Starting (data folder: {Get<AppPaths>().Root}, display backend: {Program.BackendName})");
        Get<ActionEngine>().Start();
        Get<TimerService>().Start();

        await Task.WhenAll(
            StartSafely(log, "Twitch", () => Get<TwitchService>().StartAsync()),
            StartSafely(log, "OBS", () => Get<ObsService>().StartAsync()),
            StartSafely(log, "Speech", () => Get<SpeechService>().StartAsync()),
            StartSafely(log, "Python", () => Get<PythonRuntime>().StartAsync()));

        Get<EventBus>().Publish(new BotEvent(CoreEvents.AppStarted));
    }

    private static async Task StartSafely(AppLog log, string name, Func<Task> start)
    {
        try { await start(); }
        catch (Exception ex) { log.Error(name, "Failed to start", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        Get<ProfileService>().Flush();
        await _services.DisposeAsync();
    }
}
