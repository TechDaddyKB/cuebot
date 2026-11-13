using System.Text.Json.Nodes;
using Cuebot.Server.Api;

namespace Cuebot.Server.Mcp;

public static partial class McpTools
{
    private const string MediaSource = "The file: a full path on this computer (e.g. one the streamer attached), or an http(s) web address";

    /// <summary>Looking at and editing the streamer's pictures and videos (always into a new file unless they agree).</summary>
    private static IEnumerable<McpTool> MediaTools(ApiOperations ops) =>
    [
        new("media_info", "Look at a picture or video",
            "Kind, format, width and height, frames, length and whether there is sound, for a picture, video or sound file (or web address).",
            Schema(["source"], ("source", Str(MediaSource))), new McpToolHints(ReadOnly: true, Idempotent: true, OpenWorld: true),
            async (a, ct) => await ops.MediaInfoAsync(ReqStr(a, "source"), ct).ConfigureAwait(false)),

        new("edit_image", "Edit a picture",
            "Make an edited copy of a picture (PNG, JPG, WebP, GIF's first frame, BMP, ICO), for example to turn a logo into a VTube Studio "
            + "throwable, a square avatar or an overlay image. Steps run in order: "
            + "resize {width, height, fit: contain|cover|stretch} (one side keeps the shape); maxSize {size} (longest side, never enlarges); "
            + "crop {x, y, width, height} (pixels); trim {tolerance} (cuts see-through or one-colour edges); square {} (pads to a square); "
            + "pad {pixels}; rotate {degrees: 90|180|270}; flip {direction: horizontal|vertical}; circle {} (round cut-out); "
            + "makeTransparent {color: \"#FFFFFF\" or \"corner\", tolerance} (a background colour becomes see-through); "
            + "background {color} (fills see-through parts). The source is never changed: the copy is saved next to it with \"-edited\" "
            + "added (web pictures go to Cuebot's media folder), or to output. Replacing an existing file asks the streamer first. "
            + "VTube Studio items and images: PNG with a see-through background, under 5 MB; about 512 pixels is plenty.",
            Schema(["source", "steps"], ("source", Str(MediaSource)),
                ("steps", new JsonObject
                {
                    ["type"] = "array", ["description"] = "e.g. [{\"op\": \"trim\"}, {\"op\": \"square\"}, {\"op\": \"resize\", \"width\": 512}]",
                    ["items"] = new JsonObject { ["type"] = "object" },
                }),
                ("format", Enum("png", "jpg", "webp")), ("output", Str("Where to save: a full path, or a file name next to the source (optional)")), Confirm()),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.EditImageAsync(ReqStr(a, "source"), OptStr(a, "output"), OptStr(a, "format") ?? "png",
                a["steps"] as JsonArray ?? throw Missing("steps"), ct).ConfigureAwait(false))
        {
            Confirmation = a => Replaces(ops.MediaOutput(ReqStr(a, "source"), OptStr(a, "output"), OptStr(a, "format") ?? "png", video: false)),
        },

        new("edit_video", "Edit a video",
            "Make an edited copy of a video (MP4, MOV, WebM, GIF): cut it (start, end or duration in seconds), shrink it (maxWidth, "
            + "maxHeight), crop {x, y, width, height}, rotate (90|180|270), flip (horizontal|vertical), change fps, mute it, or convert it. "
            + "format: webm (VP9, keeps transparency; for overlays and OBS), webp (animated picture, no sound), png (one frame, at start) "
            + "or mp3 (only the sound). Saved next to the source with \"-edited\" added, or to output; replacing a file asks first. "
            + "Needs ffmpeg (Cuebot's own comes with the app).",
            Schema(["source"], ("source", Str(MediaSource)), ("format", Enum("webm", "webp", "png", "mp3")),
                ("start", Num("Start at this second")), ("end", Num("End at this second")), ("duration", Num("Seconds to keep (instead of end)")),
                ("maxWidth", Int("Shrink to at most this width")), ("maxHeight", Int("Shrink to at most this height")),
                ("crop", new JsonObject { ["type"] = "object", ["description"] = "{x, y, width, height} in pixels" }),
                ("rotate", Int("90, 180 or 270")), ("flip", Enum("horizontal", "vertical")), ("fps", Num("Frames per second")),
                ("mute", Bool("Drop the sound")), ("output", Str("Where to save: a full path, or a file name next to the source (optional)")), Confirm()),
            new McpToolHints(ReadOnly: false, OpenWorld: true),
            async (a, ct) => await ops.EditVideoAsync(ReqStr(a, "source"), OptStr(a, "output"), OptStr(a, "format") ?? "webm", a, ct).ConfigureAwait(false))
        {
            Confirmation = a => Replaces(ops.MediaOutput(ReqStr(a, "source"), OptStr(a, "output"), OptStr(a, "format") ?? "webm", video: true)),
        },
    ];

    private static string? Replaces(string output) => File.Exists(output) ? $"Replace the file {output}" : null;
}
