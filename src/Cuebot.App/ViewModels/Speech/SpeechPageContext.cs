using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>An item of a picker: the stored value and what the user sees. Value equality keeps ComboBox selection stable.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// What every Speech tab shares: the service, the live settings, the voice catalog and the pickers that depend on
/// other tabs (alias names, filter profiles, devices). Tabs call <see cref="NotifyChoicesChanged"/> after renaming,
/// adding or removing something another tab can pick.
/// </summary>
public sealed class SpeechPageContext
{
    public SpeechPageContext(SpeechService speech, IAudioInput? audioInput)
    {
        Speech = speech;
        AudioInput = audioInput;
        Voices = new VoiceCatalog(speech.Engines);
    }

    public SpeechService Speech { get; }

    public SpeechSettings Settings => Speech.Settings;

    public IAudioInput? AudioInput { get; }

    public VoiceCatalog Voices { get; }

    /// <summary>Raised on the UI thread when alias names, filter profiles or devices change.</summary>
    public event Action? ChoicesChanged;

    public void Save() => Speech.SaveSettings();

    public void NotifyChoicesChanged() => ChoicesChanged?.Invoke();

    /// <summary>Aliases with a leading "none" entry whose value is empty.</summary>
    public IReadOnlyList<Choice> AliasChoices(string emptyLabel) =>
    [
        new Choice("", emptyLabel),
        .. Settings.Aliases.Select(a => a.Name.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new Choice(n, n)),
    ];

    /// <summary>Filter profiles; the empty value is the default profile.</summary>
    public IReadOnlyList<Choice> FilterProfileChoices() =>
    [
        new Choice("", $"{FilterProfile.DefaultName} profile"),
        .. Settings.FilterProfiles.Select(p => p.Name.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new Choice(n, n)),
    ];

    public IReadOnlyList<Choice> OutputDeviceChoices(string emptyLabel)
    {
        IReadOnlyList<AudioDevice> devices;
        try
        {
            devices = Speech.OutputDevices;
        }
        catch (Exception)
        {
            devices = []; // a backend without device enumeration still plays on the default device
        }
        return [new Choice("", emptyLabel), .. devices.Select(DeviceChoice)];
    }

    public IReadOnlyList<Choice> InputDeviceChoices()
    {
        IReadOnlyList<AudioDevice> devices = [];
        try
        {
            if (AudioInput is not null) devices = AudioInput.GetInputDevices();
        }
        catch (Exception)
        {
            devices = [];
        }
        return [new Choice("", "Default microphone"), .. devices.Select(DeviceChoice)];
    }

    public IReadOnlyList<Choice> EngineChoices() =>
        Speech.Engines.Select(e => new Choice(e.Id, e.IsAvailable ? e.DisplayName : $"{e.DisplayName} (unavailable)")).ToArray();

    /// <summary>The choice with <paramref name="value"/> (case-insensitive); null when the list has none.</summary>
    public static Choice? Find(IReadOnlyList<Choice> choices, string? value) =>
        choices.FirstOrDefault(c => string.Equals(c.Value, value ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>Points settings that named <paramref name="oldName"/> at <paramref name="newName"/> after an alias rename.</summary>
    public void RenameAliasReferences(string oldName, string newName)
    {
        if (oldName.Length == 0 || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        string Follow(string current) => string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase) ? newName : current;
        var s = Settings;
        s.DefaultAlias = Follow(s.DefaultAlias);
        s.SpeakChat.Alias = Follow(s.SpeakChat.Alias);
        s.SpeakChat.HighlightAlias = Follow(s.SpeakChat.HighlightAlias);
        s.Events.Alias = Follow(s.Events.Alias);
        foreach (var template in s.Events.Templates) template.Alias = Follow(template.Alias);
        foreach (var command in s.Commands.CustomCommands) command.Alias = Follow(command.Alias);
        for (var i = 0; i < s.Commands.ViewerAliases.Count; i++) s.Commands.ViewerAliases[i] = Follow(s.Commands.ViewerAliases[i]);
    }

    /// <summary>Points settings that named a filter profile at its new name.</summary>
    public void RenameFilterProfileReferences(string oldName, string newName)
    {
        if (oldName.Length == 0 || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        string Follow(string current) => string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase) ? newName : current;
        Settings.SpeakChat.FilterProfile = Follow(Settings.SpeakChat.FilterProfile);
        Settings.Events.FilterProfile = Follow(Settings.Events.FilterProfile);
    }

    private static Choice DeviceChoice(AudioDevice d) => new(d.Id, d.IsDefault ? $"{d.Name} (system default)" : d.Name);
}

/// <summary>Helpers for list settings edited as text.</summary>
public static class SpeechText
{
    private static readonly char[] ListSeparators = [',', '\n', '\r'];

    /// <summary>Splits a comma- or line-separated list, trimming and dropping empty entries.</summary>
    public static List<string> SplitList(string? text) =>
        (text ?? "").Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Splits one entry per line (entries may contain commas).</summary>
    public static List<string> SplitLines(string? text) =>
        (text ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string JoinList(IEnumerable<string>? items) => string.Join(", ", items ?? []);

    public static string JoinLines(IEnumerable<string>? items) => string.Join(Environment.NewLine, items ?? []);

    /// <summary>A unique name like "Voice 3" that is not in <paramref name="taken"/>.</summary>
    public static string UniqueName(string stem, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        for (var i = set.Count + 1; ; i++)
        {
            var name = $"{stem} {i}";
            if (!set.Contains(name)) return name;
        }
    }

    public static int Clamp(double value, int min, int max) => Math.Clamp((int)Math.Round(value), min, max);

    public static int Clamp(decimal? value, int min, int max) => Math.Clamp((int)Math.Round(value ?? 0), min, max);
}
