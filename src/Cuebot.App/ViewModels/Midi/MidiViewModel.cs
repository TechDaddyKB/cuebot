using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Midi;
using Cuebot.Midi.Backends;
using Cuebot.Midi.Protocol;

namespace Cuebot.App.ViewModels.Midi;

/// <summary>
/// MIDI tab: MIDI In and MIDI Out device connections (add, edit, delete, open/close, auto open, reopen, retry), the
/// selected MIDI In device's event listeners with learn mode, a live monitor and a test sender.
/// </summary>
public sealed partial class MidiViewModel : ObservableObject
{
    private readonly MidiService _midi;
    private CancellationTokenSource? _learning;

    public MidiViewModel(MidiService midi)
    {
        _midi = midi;
        midi.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
        midi.ConnectionsChanged += () => OnUi(Sync);
        midi.MessageReceived += (connection, message) => OnUi(() => Received(connection, message));
        Sync();
        RefreshPortList();
    }

    public string Title => "MIDI";

    internal MidiService Service => _midi;

    public string State => _midi.State.ToString();
    public string StatusMessage => _midi.StatusMessage;
    public string Backend => $"Backend: {_midi.BackendName}";
    public bool IsAvailable => _midi.IsAvailable;
    public string UnavailableReason => _midi.UnavailableReason ?? "";

    public ObservableCollection<MidiDeviceRowViewModel> Inputs { get; } = [];
    public ObservableCollection<MidiDeviceRowViewModel> Outputs { get; } = [];
    public ObservableCollection<MidiEventRowViewModel> Events { get; } = [];
    public ObservableCollection<string> InputPorts { get; } = [];
    public ObservableCollection<string> OutputPorts { get; } = [];
    public ObservableCollection<string> Monitor { get; } = [];

    public static IReadOnlyList<string> Commands { get; } = Enum.GetNames<MidiCommand>();
    public static IReadOnlyList<string> SendCommands { get; } = MidiGeneric.Commands;

    public bool HasNoInputs => Inputs.Count == 0;
    public bool HasNoOutputs => Outputs.Count == 0;
    public bool HasSelectedInput => SelectedInput is not null;
    public string EventsTitle => SelectedInput is { } s ? $"Events of {s.Name}" : "Events (select a MIDI In device)";

    // ---- Add device ----------------------------------------------------------------------------------------------

    [ObservableProperty] private string _newInputName = "";
    [ObservableProperty] private string? _newInputPort;
    [ObservableProperty] private string _newOutputName = "";
    [ObservableProperty] private string? _newOutputPort;
    [ObservableProperty] private bool _newAutoOpen = true;
    [ObservableProperty] private bool _newReopen = true;
    [ObservableProperty] private decimal? _newRetry = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInput), nameof(EventsTitle))]
    private MidiDeviceRowViewModel? _selectedInput;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _learnStatus = "";
    [ObservableProperty] private bool _ignoreNoteOffWhileLearning = true;

    partial void OnSelectedInputChanged(MidiDeviceRowViewModel? value) => SyncEvents();

    [RelayCommand]
    private void RefreshPorts() => RefreshPortList();

    [RelayCommand]
    private Task AddInput() => AddDevice(MidiDirection.Input, NewInputName, NewInputPort);

    [RelayCommand]
    private Task AddOutput() => AddDevice(MidiDirection.Output, NewOutputName, NewOutputPort);

    private Task AddDevice(MidiDirection direction, string name, string? port) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(port)) throw new ArgumentException("Choose a device first (press Refresh if it is missing).");
        var config = new MidiDeviceConfig
        {
            Name = string.IsNullOrWhiteSpace(name) ? port : name.Trim(),
            Device = port,
            AutoOpen = NewAutoOpen,
            ReopenOnClose = NewReopen,
            RetryIntervalSeconds = (int)(NewRetry ?? 5),
        };
        var connection = await Task.Run(() =>
        {
            var added = _midi.AddDevice(direction, config);
            if (config.AutoOpen && !added.IsOpen) _midi.Open(added);
            return added;
        });
        if (direction == MidiDirection.Input) NewInputName = "";
        else NewOutputName = "";
        return $"Added {connection.Name}: {connection.Message}.";
    });

    // ---- Events --------------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task AddEvent() => Run(() =>
    {
        var input = SelectedInput ?? throw new InvalidOperationException("Select a MIDI In device first.");
        _midi.AddEvent(input.Connection, new MidiInEventConfig { Name = $"Event {input.Connection.Config.Events.Count + 1}" });
        return Task.FromResult("Event added; set it up below or press Learn on it.");
    });

    /// <summary>Waits for the next message on the selected input and adds an event configured from it.</summary>
    [RelayCommand]
    private Task LearnNewEvent() => Learn(null);

    [RelayCommand]
    private void CancelLearn()
    {
        _learning?.Cancel();
        LearnStatus = "";
    }

    internal Task Learn(MidiEventRowViewModel? into) => Run(async () =>
    {
        var input = SelectedInput ?? throw new InvalidOperationException("Select a MIDI In device first.");
        _learning?.Cancel();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _learning = cts;
        LearnStatus = $"Press a key, pad, knob or button on {input.Name}...";
        try
        {
            var message = await Task.Run(() => _midi.LearnAsync(input.Connection, IgnoreNoteOffWhileLearning, cts.Token));
            if (into is null)
            {
                var learned = MidiEventMatcher.Learn(message, new MidiInEventConfig { IgnoreNoteOff = IgnoreNoteOffWhileLearning });
                _midi.AddEvent(input.Connection, learned);
                return $"Learned {learned.Name}.";
            }
            _midi.UpdateEvent(input.Connection, into.Event, e => MidiEventMatcher.Learn(message, e));
            into.Refresh();
            return $"{into.Name} now listens for {MidiEventMatcher.Describe(message)}.";
        }
        catch (OperationCanceledException)
        {
            return "Learning stopped.";
        }
        finally
        {
            LearnStatus = "";
            if (_learning == cts) _learning = null;
        }
    });

    // ---- Test send -----------------------------------------------------------------------------------------------

    [ObservableProperty] private MidiDeviceRowViewModel? _testOutput;
    [ObservableProperty] private string _testCommand = nameof(MidiCommand.NoteOn);
    [ObservableProperty] private string _testChannel = "1";
    [ObservableProperty] private string _testData1 = "60";
    [ObservableProperty] private string _testData2 = "127";
    [ObservableProperty] private string _testSysex = "F0 7E 7F 06 01 F7";

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        var output = TestOutput ?? Outputs.FirstOrDefault() ?? throw new InvalidOperationException("Add a MIDI Out device first.");
        if (!MidiNames.TryParseCommand(TestCommand, out var command)) throw new ArgumentException($"Unknown command '{TestCommand}'.");
        await _midi.SendChannelAsync(output.Connection.Id, TestChannel, c => MidiGeneric.Build(command, c, TestData1, TestData2));
        return $"Sent {TestCommand} to {output.Name}.";
    });

    [RelayCommand]
    private Task SendSysex() => Run(async () =>
    {
        var output = TestOutput ?? Outputs.FirstOrDefault() ?? throw new InvalidOperationException("Add a MIDI Out device first.");
        var message = MidiMessage.SysexFromHex(TestSysex);
        await _midi.SendAsync(output.Connection.Id, message);
        return $"Sent {message.Hex} to {output.Name}.";
    });

    [RelayCommand]
    private void ClearMonitor() => Monitor.Clear();

    // ---- Plumbing ------------------------------------------------------------------------------------------------

    internal async Task Run(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
        Sync();
    }

    private void Received(MidiConnection connection, MidiMessage message)
    {
        var matched = connection.Config.Events.Where(e => MidiEventMatcher.Matches(e, message)).Select(e => e.Name).ToArray();
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {connection.Name}  {MidiEventMatcher.Describe(message)}  value {message.Value}  [{message.Hex}]"
                   + (matched.Length > 0 ? $"  -> {string.Join(", ", matched)}" : "");
        Monitor.Insert(0, line);
        while (Monitor.Count > 200) Monitor.RemoveAt(Monitor.Count - 1);
    }

    private void RefreshPortList()
    {
        _ = Task.Run(() =>
        {
            var ins = _midi.RefreshPorts(MidiDirection.Input).Select(p => p.Name).ToArray();
            var outs = _midi.RefreshPorts(MidiDirection.Output).Select(p => p.Name).ToArray();
            OnUi(() =>
            {
                Replace(InputPorts, ins);
                Replace(OutputPorts, outs);
                NewInputPort ??= ins.FirstOrDefault();
                NewOutputPort ??= outs.FirstOrDefault();
                foreach (var row in Inputs.Concat(Outputs)) row.Refresh();
            });
        });
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.SequenceEqual(values)) return;
        target.Clear();
        foreach (var v in values) target.Add(v);
    }

    private void Sync()
    {
        SyncRows(Inputs, _midi.Inputs);
        SyncRows(Outputs, _midi.Outputs);
        if (SelectedInput is not null && !Inputs.Contains(SelectedInput)) SelectedInput = null;
        SelectedInput ??= Inputs.FirstOrDefault();
        if (TestOutput is not null && !Outputs.Contains(TestOutput)) TestOutput = null;
        TestOutput ??= Outputs.FirstOrDefault();
        SyncEvents();
        foreach (var port in _midi.Ports(MidiDirection.Input).Select(p => p.Name).Except(InputPorts).ToArray()) InputPorts.Add(port);
        foreach (var port in _midi.Ports(MidiDirection.Output).Select(p => p.Name).Except(OutputPorts).ToArray()) OutputPorts.Add(port);
        OnPropertyChanged(nameof(HasNoInputs));
        OnPropertyChanged(nameof(HasNoOutputs));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void SyncRows(ObservableCollection<MidiDeviceRowViewModel> rows, IReadOnlyList<MidiConnection> connections)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!connections.Contains(rows[i].Connection)) rows.RemoveAt(i);
        foreach (var connection in connections)
        {
            var row = rows.FirstOrDefault(r => r.Connection == connection);
            if (row is null) rows.Add(new MidiDeviceRowViewModel(this, connection));
            else row.Refresh();
        }
    }

    private void SyncEvents()
    {
        var events = SelectedInput?.Connection.Config.Events.ToArray() ?? [];
        for (var i = Events.Count - 1; i >= 0; i--)
            if (!events.Contains(Events[i].Event) || Events[i].Owner != SelectedInput) Events.RemoveAt(i);
        foreach (var e in events)
        {
            var row = Events.FirstOrDefault(r => r.Event == e);
            if (row is null) Events.Add(new MidiEventRowViewModel(this, SelectedInput!, e));
            else row.Refresh();
        }
    }

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One MIDI In or Out connection: inline-editable settings, open/close, delete.</summary>
public sealed partial class MidiDeviceRowViewModel : ObservableObject
{
    private readonly MidiViewModel _page;

    internal MidiDeviceRowViewModel(MidiViewModel page, MidiConnection connection)
    {
        _page = page;
        Connection = connection;
    }

    internal MidiConnection Connection { get; }

    private MidiService Midi => _page.Service;

    public string Name
    {
        get => Connection.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Connection.Name) return;
            Midi.UpdateDevice(Connection, c => c.Name = value.Trim());
        }
    }

    public string Device
    {
        get => Connection.Device;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Connection.Device) return;
            _ = _page.Run(async () =>
            {
                await Task.Run(() => Midi.UpdateDevice(Connection, c => c.Device = value));
                return $"{Connection.Name} now uses {value}.";
            });
        }
    }

    public IReadOnlyList<string> Ports => (Connection.Direction == MidiDirection.Input ? _page.InputPorts : _page.OutputPorts)
        .Append(Connection.Device).Distinct().ToArray();

    public bool AutoOpen
    {
        get => Connection.Config.AutoOpen;
        set => Midi.UpdateDevice(Connection, c => c.AutoOpen = value);
    }

    public bool ReopenOnClose
    {
        get => Connection.Config.ReopenOnClose;
        set => Midi.UpdateDevice(Connection, c => c.ReopenOnClose = value);
    }

    public decimal? RetryInterval
    {
        get => Connection.Config.RetryIntervalSeconds;
        set => Midi.UpdateDevice(Connection, c => c.RetryIntervalSeconds = (int)(value ?? 5));
    }

    public string State => Connection.State.ToString();
    public string Message => (Connection.Message == State ? "" : Connection.Message)
        + (Connection.IsOpen || (Connection.Direction == MidiDirection.Input ? _page.InputPorts : _page.OutputPorts).Contains(Connection.Device) ? "" : " · device not connected");
    public bool IsOpen => Connection.IsOpen;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private Task Open() => _page.Run(async () =>
    {
        var open = await Task.Run(() => Midi.Open(Connection));
        return open ? $"Opened {Connection.Name}." : $"{Connection.Name}: {Connection.Message}.";
    });

    [RelayCommand]
    private Task Close() => _page.Run(async () =>
    {
        await Task.Run(() => Midi.Close(Connection));
        return $"Closed {Connection.Name}.";
    });

    [RelayCommand]
    private Task Delete() => _page.Run(async () =>
    {
        await Task.Run(() => Midi.RemoveDevice(Connection));
        return $"Deleted {Connection.Name}.";
    });
}

/// <summary>One MIDI In event listener, edited inline.</summary>
public sealed partial class MidiEventRowViewModel : ObservableObject
{
    private readonly MidiViewModel _page;

    internal MidiEventRowViewModel(MidiViewModel page, MidiDeviceRowViewModel owner, MidiInEventConfig e)
    {
        _page = page;
        Owner = owner;
        Event = e;
    }

    internal MidiDeviceRowViewModel Owner { get; }
    internal MidiInEventConfig Event { get; }

    private void Edit(Action<MidiInEventConfig> edit)
    {
        try
        {
            _page.Service.UpdateEvent(Owner.Connection, Event, edit);
        }
        catch (Exception ex)
        {
            _page.Error = ex.Message;
        }
        Refresh();
    }

    public bool Enabled { get => Event.Enabled; set => Edit(e => e.Enabled = value); }

    public string Name
    {
        get => Event.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) Edit(e => e.Name = value);
        }
    }

    public string Command
    {
        get => Event.Command.ToString();
        set
        {
            if (Enum.TryParse<MidiCommand>(value, out var c)) Edit(e => e.Command = c);
        }
    }

    public decimal? Channel { get => Event.Channel; set => Edit(e => e.Channel = (int)(value ?? 1)); }
    public bool IgnoreChannel { get => Event.IgnoreChannel; set => Edit(e => e.IgnoreChannel = value); }
    public decimal? Data { get => Event.Data; set => Edit(e => e.Data = (int)(value ?? 0)); }
    public bool IgnoreData { get => Event.IgnoreData; set => Edit(e => e.IgnoreData = value); }
    public bool IgnoreNoteOff { get => Event.IgnoreNoteOff; set => Edit(e => e.IgnoreNoteOff = value); }
    public string RawMessage => Event.RawMessage.Length == 0 ? "—" : Event.RawMessage;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private Task Learn() => _page.Learn(this);

    [RelayCommand]
    private void Delete()
    {
        _page.Service.RemoveEvent(Owner.Connection, Event);
    }
}
