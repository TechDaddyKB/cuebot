using System.Text.Json;
using Cuebot.App;
using Cuebot.Assistant;
using Cuebot.Assistant.Providers;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.UiTests;

/// <summary>Runs only with CUEBOT_LIVE_LOCAL_MODEL=1 and a model in Ollama (or CUEBOT_LIVE_LOCAL_SERVER) on this computer.</summary>
public sealed class LocalModelFactAttribute : FactAttribute
{
    public LocalModelFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("CUEBOT_LIVE_LOCAL_MODEL") != "1")
            Skip = "Set CUEBOT_LIVE_LOCAL_MODEL=1 with Ollama running to try a local model on real build requests";
    }
}

/// <summary>
/// Typical requests sent through the Assistant to a local model, on a complete Cuebot, checking what it really built.
/// Small models do not get these right every time (docs/ASSISTANT.md, "Local models"); rerun to measure how often.
/// </summary>
public sealed class LocalModelBuildTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-local-model-").FullName;
    private AppHost? _host;

    private async Task<string> AskAsync(string request)
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        var assistant = _host.Get<AssistantService>();
        assistant.Settings.Provider = LocalModelProvider.ProviderId;
        var local = assistant.Settings.For(LocalModelProvider.ProviderId);
        local.ServerUrl = Environment.GetEnvironmentVariable("CUEBOT_LIVE_LOCAL_SERVER") ?? "";
        local.Model = Environment.GetEnvironmentVariable("CUEBOT_LIVE_LOCAL_MODEL_NAME") ?? "";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var text = new System.Text.StringBuilder();
        var started = DateTime.UtcNow;
        await foreach (var update in assistant.SendAsync(request, cts.Token))
        {
            output.WriteLine(update.ToString());
            if (update is AssistantText t) text.Append(t.Text);
            if (update is AssistantConfirmation c) c.Approve();
            if (update is AssistantError e) output.WriteLine("ERROR " + e.Message);
        }
        output.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:0} s");
        return text.ToString();
    }

    private ProfileSnapshot Profile => _host!.Get<ProfileService>().Snapshot;
    private IReadOnlyList<CommandDefinition> Commands => _host!.Get<CommandStore>().Snapshot;

    private IEnumerable<ActionDefinition> TriggeredBy(CommandDefinition command) =>
        Profile.Actions.Where(a => a.Triggers.Any(t => t.Type == "command.triggered"
            && t.Settings.TryGetValue("command", out var c) && (c.Equals(command.Name, StringComparison.OrdinalIgnoreCase) || c == command.Id.ToString())));

    private static IEnumerable<SubActionDefinition> Steps(ActionDefinition action) =>
        action.SubActions.SelectMany(s => new[] { s }.Concat(s.Branches.Values.SelectMany(b => b)));

    private static bool IsChat(SubActionDefinition s) => s.Type is "chat.send" or "twitch.sendMessage" or "kick.sendMessage" or "youtube.sendMessage";

    [LocalModelFact]
    public async Task Lurk_command_thanks_the_viewer()
    {
        await AskAsync("Make a !lurk command. When a viewer types it, Cuebot should thank them by name in chat.");
        var lurk = Assert.Single(Commands, c => c.Commands.Any(x => x.Contains("!lurk", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(TriggeredBy(lurk).SelectMany(Steps),
            s => IsChat(s) && JsonSerializer.Serialize(s.Parameters).Contains("%user", StringComparison.OrdinalIgnoreCase));
    }

    [LocalModelFact]
    public async Task Pause_and_resume_commands_for_moderators()
    {
        await AskAsync("Create moderator-only commands !pausealerts and !resumealerts that pause and resume both the overlay alerts and text-to-speech.");
        foreach (var (word, mode) in new[] { ("!pausealerts", "Pause"), ("!resumealerts", "Resume") })
        {
            var command = Assert.Single(Commands, c => c.Commands.Any(x => x.Contains(word, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(ChatPermission.Moderator, command.MinimumRole);
            var steps = TriggeredBy(command).SelectMany(Steps).ToList();
            Assert.Contains(steps, s => s.Type == "overlays.alertQueue" && s.Parameters.GetValueOrDefault("mode", "").StartsWith(mode, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(steps, s => s.Type == "speech.pause" && s.Parameters.GetValueOrDefault("mode", "").StartsWith(mode, StringComparison.OrdinalIgnoreCase));
        }
    }

    [LocalModelFact]
    public async Task Timed_chat_message()
    {
        await AskAsync("Every 15 minutes, post 'Follow me on socials: example.com/links' in chat.");
        var timer = Assert.Single(Profile.Timers, t => t.IntervalSeconds == 900 || (t.Cron ?? "").Contains("15", StringComparison.Ordinal));
        Assert.Contains(Profile.Actions, a => a.Triggers.Any(t => t.Type == "core.timer"
                && t.Settings.TryGetValue("timer", out var name) && (name.Equals(timer.Name, StringComparison.OrdinalIgnoreCase) || name == timer.Id.ToString()))
            && Steps(a).Any(s => IsChat(s) && JsonSerializer.Serialize(s.Parameters).Contains("socials", StringComparison.OrdinalIgnoreCase)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
