using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Animaze;
using Cuebot.StreamApps.Nizima;
using Cuebot.StreamApps.PngtuberRemix;
using Cuebot.StreamApps.Rahituber;
using Cuebot.StreamApps.Veadotube;
using Cuebot.StreamApps.Vmc;
using Cuebot.StreamApps.Vnyan;
using Cuebot.StreamApps.Vrchat;
using Cuebot.StreamApps.Warudo;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>VTuber apps tab: one card per app (veadotube, PNGTuber Remix, RahiTuber, VNyan, VRChat, Warudo, nizima LIVE, Animaze, VMC).</summary>
public sealed class VtuberAppsViewModel(VeadotubeService veadotube, VnyanService vnyan, VrchatService vrchat, WarudoService warudo,
    PngtuberRemixService pngtuberRemix, AnimazeService animaze, VmcService vmc, NizimaService nizima, RahituberService rahituber)
{
    public VeadotubeCardViewModel Veadotube { get; } = new(veadotube);
    public VnyanCardViewModel Vnyan { get; } = new(vnyan);
    public VrchatCardViewModel Vrchat { get; } = new(vrchat);
    public WarudoCardViewModel Warudo { get; } = new(warudo);
    public PngtuberRemixCardViewModel PngtuberRemix { get; } = new(pngtuberRemix);
    public AnimazeCardViewModel Animaze { get; } = new(animaze);
    public VmcCardViewModel Vmc { get; } = new(vmc);
    public NizimaCardViewModel Nizima { get; } = new(nizima);
    public RahituberCardViewModel Rahituber { get; } = new(rahituber);
}

/// <summary>A button row: a name and what pressing it does (a test from the Apps page).</summary>
public sealed partial class VtuberRowViewModel(string name, string actionLabel, string tip, Func<Task> run, Avalonia.Media.Imaging.Bitmap? image = null)
    : ObservableObject
{
    public string Name => name;
    public string ActionLabel => actionLabel;
    public string Tip => tip;

    /// <summary>A picture of the item (veadotube's state thumbnails), when the app has one.</summary>
    public Avalonia.Media.Imaging.Bitmap? Image => image;

    public bool HasImage => image is not null;

    [RelayCommand]
    private Task Run() => run();
}

public sealed partial class VeadotubeCardViewModel : StreamAppCardViewModel
{
    private readonly VeadotubeService _veado;

    public VeadotubeCardViewModel(VeadotubeService veado) : base(veado)
    {
        _veado = veado;
        veado.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private VeadotubeSettings S => _veado.Settings;

    public string Instance
    {
        get => S.Instance;
        set { S.Instance = value.Trim(); _veado.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _veado.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _veado.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<VtuberRowViewModel> States { get; } = [];

    public bool HasNoStates => States.Count == 0;

    public string PushToTalk => _veado.SwitchNode() is { } node
        ? $"Push-to-talk: {(_veado.SwitchValue(node) is true ? "on" : "off")}"
        : "";

    protected override Task StartAsync() => _veado.ConnectAsync();

    protected override Task StopAsync() => _veado.DisconnectAsync();

    [RelayCommand]
    private Task FindOpen() => RunAsync(() =>
    {
        var open = _veado.FindInstances();
        return Task.FromResult(open.Count == 0
            ? "No veadotube is open right now."
            : "Open now: " + string.Join(", ", open.Select(i => $"{i.Name} {i.Version} ({i.Server})")));
    });

    [RelayCommand]
    private Task TogglePushToTalk() => RunAsync(async () =>
    {
        await _veado.SetSwitchAsync(SwitchMode.Toggle);
        return "Sent push-to-talk toggle.";
    });

    private void RefreshData()
    {
        States.Clear();
        if (_veado.StateNode() is { } node)
            foreach (var state in _veado.States(node))
                States.Add(new VtuberRowViewModel(state.Name, "Show", "Switches your avatar to this state right now, as a test.",
                    () => RunAsync(async () =>
                    {
                        await _veado.ChangeStateAsync("Set", state.Name);
                        return $"Showing {state.Name}.";
                    }), Picture(_veado.Thumbnail(node, state.Id))));
        OnPropertyChanged(nameof(HasNoStates));
        OnPropertyChanged(nameof(PushToTalk));
        Refresh();
    }

    /// <summary>veadotube's state thumbnail as a bitmap; null when there is none or it does not decode.</summary>
    private static Avalonia.Media.Imaging.Bitmap? Picture(byte[]? png)
    {
        if (png is null) return null;
        try
        {
            using var stream = new MemoryStream(png);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}

public sealed partial class VnyanCardViewModel(VnyanService vnyan) : StreamAppCardViewModel(vnyan)
{
    private VnyanSettings S => vnyan.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); vnyan.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 8069); vnyan.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? WebSocketPort
    {
        get => S.WebSocketPort;
        set { S.WebSocketPort = (int)(value ?? 8000); vnyan.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>VNyan's OSC receiver port; empty until set (VNyan's docs give no default).</summary>
    public decimal? OscPort
    {
        get => S.OscPort > 0 ? S.OscPort : null;
        set { S.OscPort = (int)(value ?? 0); vnyan.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? ListenPort
    {
        get => S.ListenPort;
        set
        {
            S.ListenPort = (int)(value ?? 8070);
            vnyan.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ListenAddress));
            if (S.Listen) _ = RunAsync(async () => { await vnyan.StartListeningAsync(); return $"Cuebot listens for VNyan on {ListenAddress}"; });
        }
    }

    public bool Listen
    {
        get => S.Listen;
        set
        {
            S.Listen = value;
            vnyan.SaveSettings();
            OnPropertyChanged();
            _ = RunAsync(async () =>
            {
                if (value) await vnyan.StartListeningAsync();
                else await vnyan.StopListeningAsync();
                return value ? $"Cuebot listens for VNyan on {ListenAddress}" : "Cuebot stopped listening for VNyan.";
            });
        }
    }

    /// <summary>The address to type into VNyan's "Websocket Message" node.</summary>
    public string ListenAddress => $"ws://127.0.0.1:{(S.ListenPort is > 0 and < 65536 ? S.ListenPort : 8070)}/";

    /// <summary>Action names used in "Send to VNyan" steps, and a warning about names that differ only in capital letters.</summary>
    public string UsedNames
    {
        get
        {
            var names = vnyan.UsedActionNames().ToList();
            if (names.Count == 0) return "";
            var text = $"Names used in your actions: {string.Join(", ", names)}.";
            var clashes = VnyanService.CaseClashes(names);
            return clashes.Count == 0 ? text : $"{text} Check {string.Join("; ", clashes)}: VNyan treats different capital letters as different nodes.";
        }
    }

    [ObservableProperty] private string _testAction = "CuebotTest";
    [ObservableProperty] private string _testCommand = "CuebotTest";

    protected override Task StartAsync() => vnyan.TestAsync();

    protected override Task StopAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task SendTest() => RunAsync(async () =>
    {
        await vnyan.SendAsync(TestAction, new Dictionary<string, string> { ["user"] = "Cuebot" });
        return $"VNyan got '{TestAction.Trim()}'. If nothing happened, check the API Message node's name (capital letters matter).";
    });

    [RelayCommand]
    private Task SendWebSocketTest() => RunAsync(async () =>
    {
        await vnyan.SendWebSocketAsync(TestCommand.Trim());
        return $"Sent '{TestCommand.Trim()}' to VNyan's WebSocket receiver. If nothing happened, check the WebSocket node's Command Text.";
    });
}

public sealed partial class VrchatCardViewModel : StreamAppCardViewModel
{
    private readonly VrchatService _vrchat;

    public VrchatCardViewModel(VrchatService vrchat) : base(vrchat)
    {
        _vrchat = vrchat;
        vrchat.DataChanged += () => StreamAppsViewModel.OnUi(Refresh);
    }

    private VrchatSettings S => _vrchat.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _vrchat.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? SendPort
    {
        get => S.SendPort;
        set { S.SendPort = (int)(value ?? 9000); _vrchat.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? ListenPort
    {
        get => S.ListenPort;
        set { S.ListenPort = (int)(value ?? 9001); _vrchat.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _vrchat.SaveSettings(); OnPropertyChanged(); }
    }

    public string Heard => !_vrchat.IsConnected
        ? ""
        : _vrchat.AvatarId.Length == 0 && _vrchat.Parameters.Count == 0
            ? "Nothing heard from VRChat yet (it sends when you change avatar or a parameter changes)."
            : $"Avatar {(_vrchat.AvatarId.Length > 0 ? _vrchat.AvatarId : "not reported yet")} · {_vrchat.Parameters.Count} parameters heard";

    [ObservableProperty] private string _chatText = "Hello from Cuebot!";

    protected override Task StartAsync() => _vrchat.ConnectAsync();

    protected override Task StopAsync() => _vrchat.DisconnectAsync();

    [RelayCommand]
    private Task SendChat() => RunAsync(async () => $"Sent \"{await _vrchat.SendChatboxAsync(ChatText)}\" to the chatbox.");
}

public sealed partial class WarudoCardViewModel : StreamAppCardViewModel
{
    private readonly WarudoService _warudo;

    public WarudoCardViewModel(WarudoService warudo) : base(warudo)
    {
        _warudo = warudo;
        warudo.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private WarudoSettings S => _warudo.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _warudo.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 19069); _warudo.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? WebSocketPort
    {
        get => S.WebSocketPort;
        set { S.WebSocketPort = (int)(value ?? 19190); _warudo.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _warudo.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _warudo.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<VtuberRowViewModel> Receivers { get; } = [];

    public bool HasNoReceivers => Receivers.Count == 0;

    protected override Task StartAsync() => _warudo.ConnectAsync();

    protected override Task StopAsync() => _warudo.DisconnectAsync();

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        await _warudo.RefreshAsync();
        return $"{Receivers.Count} receivers in the open scene.";
    });

    [RelayCommand]
    private Task TestWebSocket() => RunAsync(async () =>
    {
        await _warudo.SendWebSocketMessageAsync("Hello from Cuebot");
        return $"Sent \"Hello from Cuebot\" to Warudo's WebSocket port {_warudo.WebSocketPort} (On WebSocket Message Received nodes).";
    });

    private void RefreshData()
    {
        Receivers.Clear();
        foreach (var name in _warudo.Receivers(WarudoReceiverKind.Trigger))
            Receivers.Add(new VtuberRowViewModel($"{name} (trigger)", "Trigger", "Runs this On Stream Deck Trigger node now, as a test.",
                () => RunAsync(async () => { await _warudo.TriggerAsync(name); return $"Triggered {name}."; })));
        foreach (var name in _warudo.Receivers(WarudoReceiverKind.Toggle))
            Receivers.Add(new VtuberRowViewModel(
                $"{name} (toggle, {(_warudo.Toggles.TryGetValue(name, out var on) ? on ? "on" : "off" : "state unknown")})",
                "Toggle", "Flips this On Stream Deck Toggle node now, as a test.",
                () => RunAsync(async () => { await _warudo.ToggleAsync(name); return $"Toggled {name}."; })));
        foreach (var name in _warudo.Receivers(WarudoReceiverKind.Message))
            Receivers.Add(new VtuberRowViewModel($"{name} (message)", "Send test", "Sends \"Hello from Cuebot\" to this On Stream Deck Message node.",
                () => RunAsync(async () => { await _warudo.SendMessageAsync(name, "Hello from Cuebot"); return $"Sent a message to {name}."; })));
        Refresh();
    }
}

public sealed partial class PngtuberRemixCardViewModel : StreamAppCardViewModel
{
    private readonly PngtuberRemixService _remix;

    public PngtuberRemixCardViewModel(PngtuberRemixService remix) : base(remix)
    {
        _remix = remix;
        remix.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private PngtuberRemixSettings S => _remix.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _remix.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 9321); _remix.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _remix.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _remix.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Seconds between checks for state changes; 0 = off.</summary>
    public decimal? PollSeconds
    {
        get => (decimal)S.PollSeconds;
        set { S.PollSeconds = value is { } v ? (double)Math.Clamp(v, 0, 3600) : 2; _remix.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>The most items one throw sends.</summary>
    public decimal? MaxThrowAmount
    {
        get => S.MaxThrowAmount;
        set { S.MaxThrowAmount = value is { } v ? (int)Math.Clamp(v, 1, 10_000) : 50; _remix.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<VtuberRowViewModel> States { get; } = [];

    public bool HasNoStates => States.Count == 0;

    protected override Task StartAsync() => _remix.ConnectAsync();

    protected override Task StopAsync() => _remix.DisconnectAsync();

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        await _remix.RefreshAsync();
        return $"{_remix.States.Count} states and {_remix.Sprites.Count} sprites.";
    });

    [RelayCommand]
    private Task ThrowTest() => RunAsync(async () =>
    {
        await _remix.ThrowAsync("", null);
        return "Threw some things at your avatar.";
    });

    private void RefreshData()
    {
        States.Clear();
        foreach (var state in _remix.States)
            States.Add(new VtuberRowViewModel(state.IsCurrent ? $"{state.Name} (showing)" : state.Name, "Show",
                "Switches your avatar to this state right now, as a test.",
                () => RunAsync(async () => $"Showing {await _remix.SetStateAsync(state.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))}.")));
        Refresh();
    }
}

public sealed partial class AnimazeCardViewModel : StreamAppCardViewModel
{
    private readonly AnimazeService _animaze;

    public AnimazeCardViewModel(AnimazeService animaze) : base(animaze)
    {
        _animaze = animaze;
        animaze.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private AnimazeSettings S => _animaze.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _animaze.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 9000); _animaze.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _animaze.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _animaze.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<VtuberRowViewModel> Emotes { get; } = [];

    public bool HasNoEmotes => Emotes.Count == 0;

    public string Avatar => _animaze.CurrentAvatar.Length > 0 ? $"Avatar: {_animaze.CurrentAvatar}" : "";

    protected override Task StartAsync() => _animaze.ConnectAsync();

    protected override Task StopAsync() => _animaze.DisconnectAsync();

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        await _animaze.RefreshAsync();
        return $"{_animaze.Avatars.Count} avatars and {_animaze.Emotes.Count} emotes.";
    });

    private void RefreshData()
    {
        Emotes.Clear();
        foreach (var emote in _animaze.Emotes)
            Emotes.Add(new VtuberRowViewModel(emote.FriendlyName.Length > 0 ? emote.FriendlyName : emote.ItemName, "Play",
                "Plays this emote on your avatar now, as a test.",
                () => RunAsync(async () => $"Played {await _animaze.TriggerEmoteAsync(emote.ItemName)}.")));
        Refresh();
    }
}

public sealed partial class VmcCardViewModel(VmcService vmc) : StreamAppCardViewModel(vmc)
{
    private VmcSettings S => vmc.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); vmc.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 39539); vmc.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; vmc.SaveSettings(); OnPropertyChanged(); }
    }

    [ObservableProperty] private string _testExpression = "Joy";

    protected override Task StartAsync() => vmc.ConnectAsync();

    protected override Task StopAsync() => vmc.DisconnectAsync();

    [RelayCommand]
    private Task SendTest() => RunAsync(async () =>
    {
        await vmc.SetExpressionAsync(TestExpression, 1, 3);
        return $"Showing {TestExpression.Trim()} for 3 seconds.";
    });
}

public sealed partial class NizimaCardViewModel : StreamAppCardViewModel
{
    private readonly NizimaService _nizima;

    public NizimaCardViewModel(NizimaService nizima) : base(nizima)
    {
        _nizima = nizima;
        nizima.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private NizimaSettings S => _nizima.Settings;

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 22022); _nizima.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _nizima.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _nizima.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<VtuberRowViewModel> Expressions { get; } = [];

    public bool HasNoExpressions => Expressions.Count == 0;

    protected override Task StartAsync() => _nizima.ConnectAsync();

    protected override Task StopAsync() => _nizima.DisconnectAsync();

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        await _nizima.RefreshAsync();
        return $"{_nizima.Models.Count} models on screen, {_nizima.Expressions.Count} expressions, {_nizima.Motions.Count} motions.";
    });

    private void RefreshData()
    {
        Expressions.Clear();
        foreach (var expression in _nizima.Expressions)
            Expressions.Add(new VtuberRowViewModel(expression.Name, "On/off", "Turns this expression on or off right now, as a test.",
                () => RunAsync(async () =>
                    $"{expression.Name} is now {(await _nizima.SetExpressionAsync("", expression.Name, SwitchMode.Toggle, only: false) ? "on" : "off")}.")));
        Refresh();
    }
}

public sealed partial class RahituberCardViewModel : StreamAppCardViewModel
{
    private readonly RahituberService _rahi;

    public RahituberCardViewModel(RahituberService rahi) : base(rahi)
    {
        _rahi = rahi;
        LoadStates();
    }

    private RahituberSettings S => _rahi.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _rahi.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 8000); _rahi.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>RahiTuber's folder (optional): lists the avatar's states and confirms requests from its log.</summary>
    public string Folder
    {
        get => S.Folder;
        set { S.Folder = value.Trim(); _rahi.SaveSettings(); OnPropertyChanged(); LoadStates(); }
    }

    // Empty at first: a test press of state 0 could switch it off, or rewrite the avatar if it is a Permanent state.
    [ObservableProperty] private string _testState = "";

    /// <summary>The loaded avatar's states with their type, when the folder is set.</summary>
    public ObservableCollection<VtuberRowViewModel> States { get; } = [];

    public string StatesInfo => _rahi.Folder is null
        ? "Set RahiTuber's folder (where its program and config.xml are) to list your states here."
        : _rahi.Avatar is { } avatar
            ? avatar.States.Count == 0 ? $"{avatar.Name} has no states yet." : $"States of {avatar.Name}:"
            : _rahi.AvatarProblem ?? "";

    protected override async Task StartAsync()
    {
        Info = await _rahi.TestAsync();
        LoadStates();
    }

    protected override Task StopAsync() => Task.CompletedTask;

    [RelayCommand]
    private void ReloadStates() => LoadStates();

    private void LoadStates()
    {
        States.Clear();
        foreach (var state in _rahi.Avatar?.States ?? [])
            States.Add(new VtuberRowViewModel($"{state.Index}: {(state.Name.Length > 0 ? state.Name : "(no name)")} - {state.Describe()}", "Press",
                state.Type == RahituberStateType.Permanent
                    ? "Permanent states rewrite your avatar's defaults, so this button does not press them."
                    : "Presses this state in RahiTuber now, as a test. A Toggle state switches; press again to switch it back.",
                () => RunAsync(() => PressAsync(state.Target))));
        OnPropertyChanged(nameof(StatesInfo));
    }

    private async Task<string> PressAsync(string state)
    {
        if (_rahi.Avatar?.Find(state) is { Type: RahituberStateType.Permanent })
            return $"'{state}' is a Permanent state: pressing it changes your avatar's defaults for good, so the test does not. Use a step if you mean it.";
        await _rahi.SetStateAsync(state, true);
        return _rahi.Avatar?.Find(state) is { } info
            ? $"RahiTuber got '{state}' ({info.Describe()}). Watch your avatar."
            : $"RahiTuber got '{state}'. Watch your avatar: RahiTuber does not say whether that state exists.";
    }

    [RelayCommand]
    private Task SendTest() => RunAsync(() => PressAsync(TestState.Trim()));
}
