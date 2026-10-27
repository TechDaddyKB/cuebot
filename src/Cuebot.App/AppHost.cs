using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Rumble;
using Cuebot.Scripting;
using Cuebot.Speech;
using Cuebot.Twitch;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.App;

/// <summary>Composition root: wires core and modules together and owns their lifetime.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private Task? _disposal;

    private AppHost(ServiceProvider services) => _services = services;

    /// <param name="paths">Data folder.</param>
    /// <param name="settings">The shell's already-loaded app.json; loaded from the data folder when null.</param>
    public static AppHost Create(AppPaths paths, AppSettingsStore? settings = null)
    {
        var firstRun = !File.Exists(paths.File("actions.json"));
        settings ??= new AppSettingsStore(paths);
        var services = new ServiceCollection()
            .AddCuebotCore(paths)
            .AddTwitch()
            .AddObs()
            .AddSpeech()
            .AddScripting()
            .AddRumble();
        services.AddSingleton(settings);
        services.AddSingleton(_ => new BackupService(paths, () => settings.Settings.Backups.Keep));
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

    public AppSettingsStore Settings => Get<AppSettingsStore>();

    public BackupService Backups => Get<BackupService>();

    /// <summary>Starts the engine and auto-connects platforms. Never throws; failures are logged.</summary>
    public async Task StartAsync()
    {
        var log = Get<AppLog>();
        log.Info("Cuebot", $"Starting (data folder: {Get<AppPaths>().Root}, display backend: {Program.BackendName})");
        Get<Cuebot.Core.Chat.ChatHub>();   // created before platforms connect, so no chat is missed
        Get<ActionEngine>().Start();
        Get<TimerService>().Start();

        await Task.WhenAll(
            StartSafely(log, "Twitch", () => Get<TwitchService>().StartAsync()),
            StartSafely(log, "OBS", () => Get<ObsService>().StartAsync()),
            StartSafely(log, "Rumble", () => Get<RumbleService>().StartAsync()),
            StartSafely(log, "Speech", () => Get<SpeechService>().StartAsync()),
            StartSafely(log, "Python", () => Get<PythonRuntime>().StartAsync()));

        Get<EventBus>().Publish(new BotEvent(CoreEvents.AppStarted));
    }

    private static async Task StartSafely(AppLog log, string name, Func<Task> start)
    {
        try { await start(); }
        catch (Exception ex) { log.Error(name, "Failed to start", ex); }
    }

    /// <summary>
    /// Flushes every store, then disposes all services. Safe to call more than once and from any thread: later calls
    /// return the first call's task. Callers on the UI thread should not block on it (see <see cref="ShutDown"/>).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_services) _disposal ??= Task.Run(DisposeCoreAsync);
        return new ValueTask(_disposal);
    }

    /// <summary>Synchronous shutdown for the end of <c>Main</c>: runs the disposal off any UI context, with a time limit.</summary>
    public bool ShutDown(TimeSpan timeout)
    {
        try
        {
            return DisposeAsync().AsTask().Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;   // disposal failed but finished; errors were logged where possible
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            Get<AppLog>().Info("Cuebot", "Shutting down");
            Get<ProfileService>().Flush();
            Get<AppSettingsStore>().TrySave();
        }
        catch (ObjectDisposedException) { }
        await _services.DisposeAsync();
    }
}
