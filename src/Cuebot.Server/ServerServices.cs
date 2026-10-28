using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Scripting.Compat;
using Cuebot.Server.SpeakerBot;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Server;

/// <summary>
/// The Cuebot services the servers expose. With a provider they are resolved on first use: the servers' script
/// functions and sub-actions are created while the action engine is being built, so eager resolution would loop.
/// Tests set them directly. Optional ones may be null; requests that need them return empty results.
/// </summary>
public sealed class ServerServices
{
    private readonly IServiceProvider? _provider;
    private ActionEngine? _engine;
    private ProfileService? _profile;
    private GlobalVariables? _globals;
    private EventBus? _bus;
    private AppLog? _log;
    private UserVariables? _userVariables;
    private UserRegistry? _users;
    private CommandStore? _commands;
    private ChatHub? _chat;
    private CodeTriggerRegistry? _codeTriggers;
    private ISpeakerBackend? _speaker;
    private IReadOnlyList<IConnectionStatus>? _connections;
    private Cuebot.Core.Credits.ICreditsSource? _credits;
    private Registry? _registry;
    private TimerService? _timers;
    private Cuebot.Core.Transfer.TransferService? _transfer;
    private Cuebot.Core.Scripting.ScriptApi? _scripts;

    public ServerServices()
    {
    }

    public ServerServices(IServiceProvider provider) => _provider = provider;

    public ActionEngine Engine { get => _engine ??= Required<ActionEngine>(); init => _engine = value; }
    public ProfileService Profile { get => _profile ??= Required<ProfileService>(); init => _profile = value; }
    public GlobalVariables Globals { get => _globals ??= Required<GlobalVariables>(); init => _globals = value; }
    public EventBus Bus { get => _bus ??= Required<EventBus>(); init => _bus = value; }
    public AppLog Log { get => _log ??= _provider?.GetService<AppLog>() ?? new AppLog(); init => _log = value; }
    public UserVariables? UserVariables { get => _userVariables ??= _provider?.GetService<UserVariables>(); init => _userVariables = value; }
    public UserRegistry? Users { get => _users ??= _provider?.GetService<UserRegistry>(); init => _users = value; }
    public CommandStore? Commands { get => _commands ??= _provider?.GetService<CommandStore>(); init => _commands = value; }
    public ChatHub? Chat { get => _chat ??= _provider?.GetService<ChatHub>(); init => _chat = value; }

    public CodeTriggerRegistry CodeTriggers
    {
        get => _codeTriggers ??= _provider?.GetService<CodeTriggerRegistry>() ?? new CodeTriggerRegistry();
        init => _codeTriggers = value;
    }

    public ISpeakerBackend? Speaker { get => _speaker ??= _provider?.GetService<ISpeakerBackend>(); init => _speaker = value; }

    /// <summary>The stream credits (wave 4) for GetCredits / TestCredits / ClearCredits.</summary>
    public Cuebot.Core.Credits.ICreditsSource? Credits
    {
        get => _credits ??= _provider?.GetService<Cuebot.Core.Credits.ICreditsSource>();
        init => _credits = value;
    }

    /// <summary>Platform connections (Twitch, YouTube, Kick, OBS...) for GetBroadcaster.</summary>
    public IReadOnlyList<IConnectionStatus> Connections
    {
        get => _connections ??= _provider?.GetServices<IConnectionStatus>().ToArray() ?? [];
        init => _connections = value;
    }

    /// <summary>Trigger and sub-action types, for the REST API / MCP catalogue and action validation.</summary>
    public Registry? Registry { get => _registry ??= _provider?.GetService<Registry>(); init => _registry = value; }

    /// <summary>Timer runtime state (REST API / MCP).</summary>
    public TimerService? Timers { get => _timers ??= _provider?.GetService<TimerService>(); init => _timers = value; }

    /// <summary>Import/export (REST API / MCP).</summary>
    public Cuebot.Core.Transfer.TransferService? Transfer
    {
        get => _transfer ??= _provider?.GetService<Cuebot.Core.Transfer.TransferService>();
        init => _transfer = value;
    }

    /// <summary>Script functions, listed by the REST API / MCP catalogue.</summary>
    public Cuebot.Core.Scripting.ScriptApi? Scripts
    {
        get => _scripts ??= _provider?.GetService<Cuebot.Core.Scripting.ScriptApi>();
        init => _scripts = value;
    }

    /// <summary>Module WebSocket endpoints served on the REST API listener (<c>/api/v1/{name}</c>), e.g. the Stream Deck plugin.</summary>
    public IReadOnlyList<Api.IApiWebSocketRoute> WebSocketRoutes
    {
        get => _webSocketRoutes ??= _provider?.GetServices<Api.IApiWebSocketRoute>().ToArray() ?? [];
        init => _webSocketRoutes = value;
    }

    private IReadOnlyList<Api.IApiWebSocketRoute>? _webSocketRoutes;

    private T Required<T>() where T : notnull =>
        _provider is null
            ? throw new InvalidOperationException($"{typeof(T).Name} was not provided")
            : _provider.GetRequiredService<T>();
}
