using System.Text.Json.Nodes;

namespace Cuebot.Server.Mcp;

/// <summary>
/// Looks at and edits the streamer's pictures and videos for the Assistant (MCP media_info, edit_image, edit_video):
/// the result is always a new file (or one the streamer agreed to replace); the source is only read. The app provides
/// it; without one those tools say they are unavailable.
/// </summary>
public interface IMediaEditor
{
    /// <summary>Kind, format, size and length of a file or web address.</summary>
    Task<JsonObject> InfoAsync(string source, CancellationToken ct);

    /// <summary>
    /// Where an edit writes: <paramref name="output"/> (a full path, or a file name next to the source) or, when empty, a
    /// new name next to the source (pictures from the web go to Cuebot's media folder), with the extension of
    /// <paramref name="format"/>. Throws for a bad path or format.
    /// </summary>
    string OutputFor(string source, string? output, string format, bool video);

    /// <summary>Applies <paramref name="steps"/> (resize, crop, trim, square...) and saves to <see cref="OutputFor"/>.</summary>
    Task<JsonObject> EditImageAsync(string source, string? output, string format, JsonArray steps, bool overwrite, CancellationToken ct);

    /// <summary>Cuts, resizes, crops, turns, mutes or converts a video (or takes a frame or the sound out of it).</summary>
    Task<JsonObject> EditVideoAsync(string source, string? output, string format, JsonObject options, bool overwrite, CancellationToken ct);
}
