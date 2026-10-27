using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// The Cuebot services the CPH layer maps Streamer.bot methods onto. Everything is optional: a method whose service
/// is missing throws <see cref="NotSupportedException"/>. Platform features (chat, OBS, speech, WebSocket) go through
/// script functions instead, so this layer does not depend on those modules. With a service provider, services are
/// resolved on first use (the action engine is built after the script sub-actions, so eager resolution would loop).
/// </summary>
public sealed class CphServices
{
    private readonly IServiceProvider? _provider;
    private ProfileService? _profile;
    private ActionEngine? _engine;
    private Registry? _registry;
    private EventBus? _bus;
    private GlobalVariables? _globals;
    private UserVariables? _userVariables;
    private UserRegistry? _users;
    private GroupStore? _groups;
    private CommandStore? _commands;
    private CommandService? _commandService;
    private TimerService? _timers;
    private ChatHub? _chat;
    private HttpClient? _http;
    private AppLog? _log;
    private CodeTriggerRegistry? _codeTriggers;
    private ChatModerationService? _chatModeration;
    private IKeyboardOutput? _keyboard;

    public CphServices()
    {
    }

    public CphServices(IServiceProvider provider) => _provider = provider;

    public ProfileService? Profile { get => _profile ??= Get<ProfileService>(); init => _profile = value; }
    public ActionEngine? Engine { get => _engine ??= Get<ActionEngine>(); init => _engine = value; }
    public Registry? Registry { get => _registry ??= Get<Registry>(); init => _registry = value; }
    public EventBus? Bus { get => _bus ??= Get<EventBus>(); init => _bus = value; }
    public GlobalVariables? Globals { get => _globals ??= Get<GlobalVariables>(); init => _globals = value; }
    public UserVariables? UserVariables { get => _userVariables ??= Get<UserVariables>(); init => _userVariables = value; }
    public UserRegistry? Users { get => _users ??= Get<UserRegistry>(); init => _users = value; }
    public GroupStore? Groups { get => _groups ??= Get<GroupStore>(); init => _groups = value; }
    public CommandStore? Commands { get => _commands ??= Get<CommandStore>(); init => _commands = value; }
    public CommandService? CommandService { get => _commandService ??= Get<CommandService>(); init => _commandService = value; }
    public TimerService? Timers { get => _timers ??= Get<TimerService>(); init => _timers = value; }
    public ChatHub? Chat { get => _chat ??= Get<ChatHub>(); init => _chat = value; }
    public HttpClient? Http { get => _http ??= Get<HttpClient>(); init => _http = value; }
    public AppLog? Log { get => _log ??= Get<AppLog>(); init => _log = value; }

    /// <summary>Platform announcers (used for /me lines by CPH.SendAction).</summary>
    public ChatModerationService? ChatModeration { get => _chatModeration ??= Get<ChatModerationService>(); init => _chatModeration = value; }

    /// <summary>Key presses for CPH.KeyboardPress, from whichever module registers <see cref="IKeyboardOutput"/>.</summary>
    public IKeyboardOutput? Keyboard { get => _keyboard ??= Get<IKeyboardOutput>(); init => _keyboard = value; }

    public CodeTriggerRegistry CodeTriggers
    {
        get => _codeTriggers ??= Get<CodeTriggerRegistry>() ?? new CodeTriggerRegistry();
        init => _codeTriggers = value;
    }

    private T? Get<T>() where T : class => _provider?.GetService<T>();
}
