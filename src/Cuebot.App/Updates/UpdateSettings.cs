using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Storage;

namespace Cuebot.App.Updates;

/// <summary>Update checker settings and the last result, stored as <c>updates.json</c> in the data folder.</summary>
public sealed class UpdateSettings
{
    public static readonly TimeSpan StartCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Check when Cuebot starts (at most once per <see cref="StartCheckInterval"/>).</summary>
    public bool CheckOnStart { get; set; } = true;

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public DateTimeOffset? LastChecked { get; set; }

    public string LastResult { get; set; } = "";

    /// <summary>True when a start-up check is due at <paramref name="now"/>.</summary>
    public bool StartCheckDue(DateTimeOffset now) =>
        CheckOnStart && (LastChecked is not { } last || now - last >= StartCheckInterval || last > now);
}

public sealed class UpdateSettingsStore
{
    public const string FileName = "updates.json";
    private readonly object _gate = new();

    public UpdateSettingsStore(AppPaths paths)
    {
        Path = paths.File(FileName);
        Settings = Load(Path);
    }

    public string Path { get; }

    public UpdateSettings Settings { get; }

    /// <summary>Saves, ignoring IO errors (the setting simply does not stick).</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json.Options));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static UpdateSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Json.Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new UpdateSettings();
        }
    }
}
