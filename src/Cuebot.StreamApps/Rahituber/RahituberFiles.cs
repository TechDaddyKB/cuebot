using System.Globalization;
using System.Xml.Linq;

namespace Cuebot.StreamApps.Rahituber;

/// <summary>How a RahiTuber state reacts to its hotkey (its "activeType").</summary>
public enum RahituberStateType { Toggle = 0, Held = 1, Permanent = 2 }

/// <summary>When a RahiTuber state may switch (its "canTrigger"); a press at another time is dropped.</summary>
public enum RahituberStateCondition { Always = 0, WhileTalking = 1, WhileIdle = 2 }

/// <summary>One state from the avatar file, in RahiTuber's list order.</summary>
/// <param name="Timeout">Seconds until a Toggle state turns itself off, or null when its timeout is off.</param>
public sealed record RahituberStateInfo(int Index, string Name, RahituberStateType Type, bool Enabled,
    RahituberStateCondition Condition, double? Timeout)
{
    /// <summary>What to send for this state: its name, or its place for a state without a name.</summary>
    public string Target => Name.Length > 0 ? Name : Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>A short description such as "Toggle, off after 5 s, while talking".</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            Type switch
            {
                RahituberStateType.Held => "Held",
                RahituberStateType.Permanent => "Permanent (can't be undone)",
                _ => Timeout is { } t ? $"Toggle, off after {t.ToString("0.##", CultureInfo.InvariantCulture)} s" : "Toggle",
            },
        };
        if (Condition == RahituberStateCondition.WhileTalking) parts.Add("only while talking");
        if (Condition == RahituberStateCondition.WhileIdle) parts.Add("only while quiet");
        if (!Enabled) parts.Add("switched off in RahiTuber");
        return string.Join(", ", parts);
    }
}

/// <summary>The states of the avatar RahiTuber has loaded, read from its files (RahiTuber has no way to list them).</summary>
public sealed record RahituberAvatar(string Path, IReadOnlyList<RahituberStateInfo> States)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// The state RahiTuber would pick for <paramref name="target"/>: like RahiTuber, the first state (in list order)
    /// whose place or name matches exactly. Null when none does.
    /// </summary>
    public RahituberStateInfo? Find(string target) =>
        States.FirstOrDefault(s => s.Index.ToString(CultureInfo.InvariantCulture) == target || s.Name == target);
}

/// <summary>
/// Reads RahiTuber's own files, read-only: config.xml beside the program names the avatar in use (lastLayerSet, without
/// ".xml"; lastLayers.xml when missing), whose &lt;hotkeys&gt; are the states in list order. RahiTuber_Log.txt in the same
/// folder lists every request it got ("State change added to queue: id = active").
/// </summary>
public static class RahituberFiles
{
    public const string ConfigFile = "config.xml";
    public const string FallbackAvatar = "lastLayers.xml";
    public const string LogFile = "RahiTuber_Log.txt";

    /// <summary>The avatar file RahiTuber loads, or null when the folder has no RahiTuber files.</summary>
    public static string? AvatarPath(string folder)
    {
        var config = Path.Combine(folder, ConfigFile);
        if (File.Exists(config))
        {
            var last = LoadLenient(config)?.Descendants("Common").FirstOrDefault()?.Attribute("lastLayerSet")?.Value.Trim();
            if (!string.IsNullOrEmpty(last))
            {
                var path = Path.IsPathRooted(last) ? last : Path.Combine(folder, last);
                if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) path += ".xml";
                if (File.Exists(path)) return path;
            }
        }
        var fallback = Path.Combine(folder, FallbackAvatar);
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>The states in an avatar file. Lenient: older files have toggle="true|false" and no names.</summary>
    public static IReadOnlyList<RahituberStateInfo> ReadStates(string avatarPath)
    {
        var doc = LoadLenient(avatarPath) ?? throw new InvalidDataException($"{Path.GetFileName(avatarPath)} is not a RahiTuber avatar file.");
        var hotkeys = doc.Root?.Element("hotkeys");
        if (hotkeys is null) return [];
        var states = new List<RahituberStateInfo>();
        foreach (var hotkey in hotkeys.Elements("hotkey"))
        {
            var type = Bool(hotkey, "toggle") == false ? RahituberStateType.Held : RahituberStateType.Toggle;
            if (Int(hotkey, "activeType") is { } active && Enum.IsDefined((RahituberStateType)active)) type = (RahituberStateType)active;
            var condition = Int(hotkey, "canTrigger") is { } c && Enum.IsDefined((RahituberStateCondition)c)
                ? (RahituberStateCondition)c : RahituberStateCondition.Always;
            var timeout = Bool(hotkey, "useTimeout") ?? true
                ? double.TryParse(hotkey.Attribute("timeout")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 5
                : (double?)null;
            states.Add(new RahituberStateInfo(states.Count, hotkey.Attribute("name")?.Value ?? "", type,
                Bool(hotkey, "enabled") ?? true, condition, timeout));
        }
        return states;
    }

    /// <summary>The RahiTuber version from buildnumber.txt beside the program, or null.</summary>
    public static string? Version(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "buildnumber.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 and < 32 } v ? v : null : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static XDocument? LoadLenient(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return XDocument.Load(stream);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    // tinyxml2 reads true/false and 1/0 for booleans.
    private static bool? Bool(XElement e, string name) => e.Attribute(name)?.Value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static int? Int(XElement e, string name) =>
        int.TryParse(e.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
