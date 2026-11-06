using Cuebot.Core.Actions;
using Cuebot.Portal.Changes;

namespace Cuebot.Portal.Uploads;

/// <summary>
/// Step settings that name a file on the streamer's computer. A mod may only point them at uploads (the portal shows
/// an upload as "upload:&lt;id&gt;" and never the folder it lives in); a setting the streamer chose stays as it is
/// until a mod changes it. Without this a mod could play, show or put on stream any file whose path they guess.
/// </summary>
public sealed class PortalFiles(PortalMediaStore? store)
{
    public const string Prefix = "upload:";

    /// <summary>The folder of uploaded sounds, for "Play a random sound from a folder".</summary>
    public const string SoundFolder = Prefix + "sounds";

    /// <summary>Step type → its file setting and the upload kinds it takes (an empty list: the sound folder).</summary>
    public static readonly IReadOnlyDictionary<string, (string Key, UploadKind[] Kinds)> Settings = new Dictionary<string, (string, UploadKind[])>
    {
        ["audio.playSound"] = ("file", [UploadKind.Sound]),
        ["audio.playSoundFromFolder"] = ("folder", []),
        ["obs.setImageFile"] = ("file", [UploadKind.Image]),
        ["obs.setMediaFile"] = ("file", [UploadKind.Video, UploadKind.Sound, UploadKind.Image]),
    };

    /// <summary>A copy for the portal, with upload paths shown as "upload:…".</summary>
    public ActionDefinition ToPortal(ActionDefinition action)
    {
        var copy = PortalChanges.FromNode<ActionDefinition>(PortalChanges.ToNode(action));
        foreach (var (step, key, kinds) in FileSettings(copy))
        {
            var value = step.Parameters[key];
            if (kinds.Length == 0 && store is not null && SamePath(value, store.KindFolder(UploadKind.Sound))) step.Parameters[key] = SoundFolder;
            else if (store?.ByPath(value) is { } item) step.Parameters[key] = Prefix + item.Id;
        }
        return copy;
    }

    /// <summary>Turns the portal's "upload:…" values back into paths, in place (unknown ones stay, and are refused).</summary>
    public void FromPortal(ActionDefinition action)
    {
        if (store is null) return;
        foreach (var (step, key, kinds) in FileSettings(action))
        {
            var value = step.Parameters[key].Trim();
            if (kinds.Length == 0 && value == SoundFolder) step.Parameters[key] = store.KindFolder(UploadKind.Sound);
            else if (value.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParse(value[Prefix.Length..], out var id)
                     && store.Get(id) is { } item && kinds.Contains(item.Kind))
                step.Parameters[key] = store.FullPath(item);
        }
    }

    /// <summary>Problems with the file settings a mod added or changed.</summary>
    public IReadOnlyList<string> Check(ActionDefinition? before, ActionDefinition after)
    {
        var old = before is null ? [] : PortalPolicy.Steps(before.SubActions).ToDictionary(s => s.Id);
        var problems = new List<string>();
        foreach (var (step, key, kinds) in FileSettings(after))
        {
            var value = step.Parameters[key];
            if (old.TryGetValue(step.Id, out var was) && was.Type == step.Type && was.Parameters.GetValueOrDefault(key, "") == value) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;
            var ok = kinds.Length == 0
                ? store is not null && SamePath(value, store.KindFolder(UploadKind.Sound))
                : store?.ByPath(value) is { } item && kinds.Contains(item.Kind);
            if (!ok) problems.Add(kinds.Length == 0
                ? $"'{step.Type}' can only use the folder of uploaded sounds."
                : $"'{step.Type}' can only use an uploaded {string.Join(" or ", kinds.Select(Word))}: mods cannot use other files on the streamer's computer.");
        }
        return [.. problems.Distinct()];
    }

    private static IEnumerable<(SubActionDefinition Step, string Key, UploadKind[] Kinds)> FileSettings(ActionDefinition action) =>
        PortalPolicy.Steps(action.SubActions)
            .Where(s => Settings.ContainsKey(s.Type))
            .Select(s => (s, Settings[s.Type].Key, Settings[s.Type].Kinds))
            .Where(x => x.s.Parameters.ContainsKey(x.Key));

    private static bool SamePath(string value, string folder)
    {
        try
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static string Word(UploadKind kind) => kind switch { UploadKind.Sound => "sound", UploadKind.Image => "picture", _ => "video" };
}
