namespace Cuebot.Integrations.NowPlaying;

/// <summary>Now playing (integrations.json "NowPlaying"). Nothing here is secret.</summary>
public sealed class NowPlayingSettings
{
    /// <summary>
    /// Watch the players all the time (needed for the "Song changed" trigger and the text file). Off by default: the
    /// "Get now playing" step still looks when it runs.
    /// </summary>
    public bool Enabled { get; set; }

    public NowPlayingSource Source { get; set; } = NowPlayingSource.Automatic;

    /// <summary>For <see cref="NowPlayingSource.Player"/>: part of the player's name, e.g. "spotify" or "vlc".</summary>
    public string Player { get; set; } = "";

    /// <summary>Keep a text file with the current song up to date (for an OBS text source).</summary>
    public bool WriteTextFile { get; set; }

    /// <summary>The text file; empty means now-playing.txt in Cuebot's data folder.</summary>
    public string TextFilePath { get; set; } = "";

    /// <summary>What the file says while a song plays: %title%, %artist%, %album%, %player% and %duration% are replaced.</summary>
    public string TextFormat { get; set; } = "%artist% - %title%";

    /// <summary>What the file says while nothing plays (empty by default, so the OBS text disappears).</summary>
    public string IdleText { get; set; } = "";
}
