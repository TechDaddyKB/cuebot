using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Obs;
using Cuebot.Relay;
using Cuebot.Kick;
using Cuebot.Donations;
using Cuebot.Integrations;
using Cuebot.Rumble;
using Cuebot.Server;
using Cuebot.Scripting;
using Cuebot.Speech;
using Cuebot.Speech.Emotes;
using Cuebot.Controllers;
using Cuebot.Elgato;
using Cuebot.Midi;
using Cuebot.StreamApps;
using Cuebot.StreamDeck;
using Cuebot.WaveLink;
using Cuebot.Twitch;
using Cuebot.VoiceControl;
using Cuebot.Webhooks;
using Cuebot.YouTube;
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
            .AddSpeechEmoteNames()
            .AddVoiceControl()   // uses Speech's microphone capture
            .AddScripting()
            .AddRumble()
            .AddYouTube()
            .AddKick()
            .AddDonations()
            .AddIntegrations()
            .AddStreamApps()
            .AddElgato()
            .AddWaveLink()
            .AddMidi()
            .AddGameControllers()
            .AddWebhooks()
            .AddServers()
            .AddStreamDeck()
            .AddRelay();   // after every module, so it sees all IWebhookHandlers
        // The App provides the real clipboard and in-app toasts; Core registers no-op defaults (last registration wins).
        services.AddSingleton<Cuebot.Core.Desktop.Clipboard.IClipboardService, Cuebot.App.Views.Extras.Desktop.AvaloniaClipboardService>();
        services.AddSingleton<Cuebot.Core.Desktop.Notifications.IToastPresenter, Cuebot.App.Views.Extras.Desktop.AvaloniaToastPresenter>();
        services.AddSingleton(settings);
        services.AddSingleton(_ => new BackupService(paths, () => settings.Settings.Backups.Keep));
        var host = new AppHost(services.BuildServiceProvider());
        TextCulture.Apply(host.Get<Cuebot.Core.Variables.TextEnvironment>(), settings.Settings.Text.Culture);
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
        Get<Cuebot.Core.Chat.Emotes.EmoteService>();   // indexes emote args from the first message on
        Get<ActionEngine>().Start();
        Get<TimerService>().Start();

        await Task.WhenAll(
            StartSafely(log, "Twitch", () => Get<TwitchService>().StartAsync()),
            StartSafely(log, "OBS", () => Get<ObsService>().StartAsync()),
            StartSafely(log, "Rumble", () => Get<RumbleService>().StartAsync()),
            StartSafely(log, "YouTube", () => Get<Cuebot.YouTube.YouTubeService>().StartAsync()),
            StartSafely(log, "Kick", () => Get<Cuebot.Kick.KickService>().StartAsync()),
            StartSafely(log, "TipeeeStream", () => Get<Cuebot.Donations.TipeeeStream.TipeeeStreamService>().StartAsync()),
            StartSafely(log, "DonorDrive", () => Get<Cuebot.Donations.DonorDrive.DonorDriveService>().StartAsync()),
            StartSafely(log, "TreatStream", () => Get<Cuebot.Donations.TreatStream.TreatStreamService>().StartAsync()),
            StartSafely(log, "Pally.gg", () => Get<Cuebot.Donations.Pally.PallyService>().StartAsync()),
            StartSafely(log, "Streamlabs", () => Get<Cuebot.Donations.Streamlabs.StreamlabsService>().StartAsync()),
            StartSafely(log, "StreamElements", () => Get<Cuebot.Donations.StreamElements.StreamElementsService>().StartAsync()),
            StartSafely(log, "VTube Studio", () => Get<Cuebot.Integrations.VTubeStudio.VTubeStudioService>().StartAsync()),
            StartSafely(log, "Voicemod", () => Get<Cuebot.Integrations.Voicemod.VoicemodService>().StartAsync()),
            StartSafely(log, "Pulsoid", () => Get<Cuebot.Integrations.HeartRate.Pulsoid.PulsoidService>().StartAsync()),
            StartSafely(log, "HypeRate", () => Get<Cuebot.Integrations.HeartRate.HypeRate.HypeRateService>().StartAsync()),
            StartSafely(log, "StreamLoots", () => Get<Cuebot.Integrations.StreamLoots.StreamLootsService>().StartAsync()),
            StartSafely(log, "Crowd Control", () => Get<Cuebot.Integrations.CrowdControl.CrowdControlService>().StartAsync()),
            StartSafely(log, "Meld Studio", () => Get<Cuebot.StreamApps.Meld.MeldStudioService>().StartAsync()),
            StartSafely(log, "PolyPop", () => Get<Cuebot.StreamApps.PolyPop.PolyPopService>().StartAsync()),
            StartSafely(log, "OBSBOT", () => Get<Cuebot.StreamApps.Obsbot.ObsbotService>().StartAsync()),
            StartSafely(log, "mtion studio", () => Get<Cuebot.StreamApps.Mtion.MtionService>().StartAsync()),
            StartSafely(log, "Elgato Lights", () => Get<Cuebot.Elgato.Lights.ElgatoLightsService>().StartAsync()),
            StartSafely(log, "Camera Hub", () => Get<Cuebot.Elgato.CameraHub.CameraHubService>().StartAsync()),
            StartSafely(log, "Wave Link", () => Get<Cuebot.WaveLink.WaveLinkService>().StartAsync()),
            StartSafely(log, "MIDI", () => Get<Cuebot.Midi.MidiService>().StartAsync()),
            StartSafely(log, "Controllers", () => Get<Cuebot.Controllers.ControllerService>().StartAsync()),
            StartSafely(log, "Voice Control", () => Get<Cuebot.VoiceControl.VoiceControlService>().StartAsync()),
            StartSafely(log, "Servers", () => Get<Cuebot.Server.ServerManager>().StartAsync()),
            StartSafely(log, "Webhook relay", () => Get<Cuebot.Relay.RelayClient>().StartAsync()),
            StartSafely(log, "Hotkeys", () => Get<Cuebot.Core.Desktop.Hotkeys.HotkeyService>().StartAsync()),
            StartSafely(log, "Speech", () => Get<SpeechService>().StartAsync()),
            StartSafely(log, "Python", () => Get<PythonRuntime>().StartAsync()),
            StartSafely(log, "Watchers", () => Get<Cuebot.Core.SystemTools.WatcherService>().StartAsync()),
            StartSafely(log, "Audio", () => Get<Cuebot.Speech.AudioOutput>().WarmUpAsync()));   // first sound plays without device-open delay

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
