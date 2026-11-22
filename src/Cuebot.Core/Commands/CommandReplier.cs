using System.Collections.Concurrent;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Commands;

/// <summary>
/// Sends a command's reply text when it runs, so a simple text command needs no action. Runs after the usual
/// permission and cooldown checks because it listens to "command.triggered". Created at start-up as a periodic service.
/// </summary>
public sealed class CommandReplier : IPeriodicService, IDisposable
{
    private const string Source = "Commands";

    private readonly CommandStore _store;
    private readonly ChatHub _chat;
    private readonly AppLog _log;
    private readonly Func<string, string?>? _globals;
    private readonly Random _random;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<Guid, int> _rotation = new();

    public CommandReplier(CommandStore store, ChatHub chat, EventBus bus, AppLog log, Func<string, string?>? globals = null, Random? random = null)
    {
        _store = store;
        _chat = chat;
        _log = log;
        _globals = globals;
        _random = random ?? Random.Shared;
        _subscription = bus.Subscribe(OnEvent);
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type != CommandService.TriggeredEvent) return;
        if (!Guid.TryParse(e.Args.GetString("commandId"), out var id) || _store.Get(id) is not { } command) return;
        if (string.IsNullOrWhiteSpace(command.Reply)) return;
        _ = SendAsync(command, e.Args);
    }

    private async Task SendAsync(CommandDefinition command, IReadOnlyDictionary<string, object?> args)
    {
        try
        {
            var template = PickLine(command);
            if (template.Length == 0) return;
            var text = VariableResolver.Resolve(template, args, _globals).Trim();
            if (text.Length == 0) return;
            if (BlockReason(template, text, _store.Snapshot) is { } reason)
            {
                _log.Warn(Source, $"Reply of '{command.Name}' not sent: {reason}");
                return;
            }
            var platform = command.ReplyTo == CommandReplyTarget.AllPlatforms ? ChatHub.AllPlatforms : args.GetString("platform");
            var options = command.ReplyAs switch
            {
                CommandReplyAccount.Bot => new ChatSendOptions(Bot: true),
                CommandReplyAccount.Broadcaster => new ChatSendOptions(Bot: false),
                _ => ChatSendOptions.Default,
            };
            await _chat.SendAsync(platform, text, options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(Source, $"Reply of '{command.Name}' could not be sent: {ex.Message}");
        }
    }

    private string PickLine(CommandDefinition command)
    {
        var lines = command.Reply.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return "";
        return command.ReplyMode switch
        {
            CommandReplyMode.Random => lines[_random.Next(lines.Length)],
            CommandReplyMode.Rotate => lines[(_rotation.AddOrUpdate(command.Id, 0, (_, i) => unchecked(i + 1)) & int.MaxValue) % lines.Length],
            _ => lines[0],
        };
    }

    /// <summary>
    /// Why a reply must not be sent: viewer text must not turn it into a chat command ("/ban x"), and it must not start
    /// another reply command, which could answer each other for ever.
    /// </summary>
    internal static string? BlockReason(string template, string text, IEnumerable<CommandDefinition> commands)
    {
        var start = template.TrimStart();
        if (text[0] is '/' or '.' && !(start.Length > 0 && start[0] == text[0]))
            return "a variable made it start with a chat command character";
        foreach (var other in commands)
        {
            if (!other.Enabled || string.IsNullOrWhiteSpace(other.Reply) || other.Commands.Count == 0) continue;
            try
            {
                if (CommandMatcher.MatchAny(text, other.Commands, other.Mode, other.Location, other.CaseSensitive) is not null)
                    return $"it would run the reply command '{other.Name}'";
            }
            catch (Exception)
            {
                // A broken pattern matches nothing; the command's own evaluation logs it.
            }
        }
        return null;
    }

    void IPeriodicService.Tick(DateTimeOffset now)
    {
    }

    public void Dispose() => _subscription.Dispose();
}
