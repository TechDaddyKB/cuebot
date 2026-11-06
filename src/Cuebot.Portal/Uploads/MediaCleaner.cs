using System.Globalization;
using System.Text.Json.Nodes;

namespace Cuebot.Portal.Uploads;

/// <summary>A clean file: <see cref="Path"/> is in the work folder, ready to move into the library.</summary>
public sealed record CleanedMedia(string Path, string Extension, long DurationMs, int Width, int Height, bool Animated);

public sealed record CleanResult(CleanedMedia? Media, string? Error, string? Detail = null)
{
    public static CleanResult Fail(string error, string? detail = null) => new(null, error, detail);
}

/// <summary>
/// Turns one untrusted upload into a fresh file (docs/MOD_PORTAL_PLAN.md, section 8): the first bytes pick the format
/// (never the name), ffprobe checks streams and limits through that format's demuxer only, ffmpeg decodes it fully and
/// encodes a new file with no metadata, and ffprobe decodes the result again. Nothing of the original survives except
/// the pictures and sound, so a polyglot or a file with a payload in its metadata comes out as plain media.
/// Outputs: sounds MP3, still images PNG, animations animated WebP, video VP9 WebM (transparent when the source is).
/// </summary>
public sealed class MediaCleaner(MediaTools tools)
{
    public const int MaxImageSide = 4096;
    public const int MaxAnimationSide = 1280;
    public const int MaxVideoWidth = 1920;
    public const int MaxVideoHeight = 1080;
    public const int MaxAnimationFrames = 1500;
    private const string Unreadable = "Cuebot could not read this file. It may be damaged, or use a format the portal does not take.";

    public MediaTools Tools => tools;

    public async Task<CleanResult> CleanAsync(string input, UploadKind kind, string workDir, CancellationToken ct = default)
    {
        var limits = UploadLimits.For(kind);
        var head = new byte[64];
        int read;
        await using (var file = File.OpenRead(input)) read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (MediaFormat.Sniff(head.AsSpan(0, read)) is not { } format)
            return CleanResult.Fail("The portal does not take this type of file.");
        if (!format.Kinds.Contains(kind))
            return CleanResult.Fail($"That is a {format.Name} file, which cannot be uploaded as {Article(kind)}.");

        var animatedCandidate = format == MediaFormat.Gif || format == MediaFormat.WebP;
        var probe = await ProbeAsync(input, format, countFrames: animatedCandidate, workDir, limits, ct).ConfigureAwait(false);
        if (probe is null) return CleanResult.Fail(Unreadable);
        var (audio, video, durationMs) = probe.Value;

        string ext;
        var animated = false;
        var args = InputArgs(input, format, video);
        switch (kind)
        {
            case UploadKind.Sound:
                if (audio is null) return CleanResult.Fail("This file has no sound in it.");
                if (!format.Codecs.Contains(Str(audio, "codec_name"))) return CleanResult.Fail(Unreadable);
                if (durationMs <= 0 || durationMs > limits.MaxDuration.TotalMilliseconds) return TooLong(limits);
                ext = "mp3";
                args.AddRange(["-map", "0:a:0", "-vn", "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "192k",
                    "-id3v2_version", "0", "-write_xing", "0", "-f", "mp3"]);
                break;
            case UploadKind.Image:
            {
                if (video is null || !format.Codecs.Contains(Str(video, "codec_name"))) return CleanResult.Fail(Unreadable);
                if (TooBig(video, limits)) return CleanResult.Fail("This picture is too large.");
                var frames = Int(video, "nb_read_frames");
                if (frames > MaxAnimationFrames || durationMs > limits.MaxDuration.TotalMilliseconds)
                    return CleanResult.Fail($"Animations can be up to {limits.MaxDuration.TotalSeconds:0} seconds and {MaxAnimationFrames} frames.");
                animated = frames > 1;
                if (animated)
                {
                    ext = "webp";
                    args.AddRange(["-map", "0:v:0", "-vf", Fit(MaxAnimationSide, MaxAnimationSide), "-c:v", "libwebp_anim",
                        "-lossless", "0", "-quality", "80", "-loop", "0", "-f", "webp"]);
                }
                else
                {
                    ext = "png";
                    args.AddRange(["-map", "0:v:0", "-frames:v", "1", "-vf", Fit(MaxImageSide, MaxImageSide), "-c:v", "png",
                        "-update", "1", "-f", "image2"]);
                }
                break;
            }
            default:
            {
                if (video is null || !format.Codecs.Contains(Str(video, "codec_name"))) return CleanResult.Fail(Unreadable);
                if (audio is not null && !format.Codecs.Contains(Str(audio, "codec_name"))) return CleanResult.Fail(Unreadable);
                if (TooBig(video, limits)) return CleanResult.Fail("This video's picture is too large.");
                if (durationMs <= 0 || durationMs > limits.MaxDuration.TotalMilliseconds) return TooLong(limits);
                ext = "webm";
                var alpha = Str(video, "pix_fmt").StartsWith("yuva", StringComparison.Ordinal) || HasAlphaTag(video);
                args.AddRange(["-map", "0:v:0", "-vf", Fit(MaxVideoWidth, MaxVideoHeight) + ":force_divisible_by=2", "-fpsmax", "60",
                    "-c:v", "libvpx-vp9", "-pix_fmt", alpha ? "yuva420p" : "yuv420p", "-b:v", "0", "-crf", "32", "-deadline", "good",
                    "-cpu-used", "5", "-row-mt", "1", "-auto-alt-ref", alpha ? "0" : "1"]);
                if (audio is not null) args.AddRange(["-map", "0:a:0", "-c:a", "libopus", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
                args.AddRange(["-f", "webm"]);
                break;
            }
        }

        var output = Path.Combine(workDir, "output." + ext);
        args.AddRange(["-t", limits.MaxDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture), "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags", "+bitexact", "-y", "file:" + output]);
        var run = await tools.RunAsync(tools.Ffmpeg, args, workDir, limits.CleanTimeout, limits.MaxMemoryBytes, ct).ConfigureAwait(false);
        if (!run.Ok || !File.Exists(output))
            return run.Stopped switch
            {
                ToolStop.Timeout => CleanResult.Fail("Cleaning this file took too long. Try a shorter or smaller one.", run.Errors),
                ToolStop.Memory => CleanResult.Fail("This file needs too much memory to clean. Try a smaller one.", run.Errors),
                ToolStop.Cancelled => CleanResult.Fail("The upload was cancelled."),
                _ => CleanResult.Fail(Unreadable, run.Errors),
            };

        // The result must decode on its own, with only what Cuebot wrote. ffmpeg cannot decode animated WebP, so an
        // animation (made by libwebp from frames ffmpeg already decoded) is checked by its structure instead.
        if (animated)
            return AnimatedWebPSize(output) is var (w, h) && w > 0
                ? new CleanResult(new CleanedMedia(output, ext, durationMs, w, h, Animated: true), null)
                : CleanResult.Fail(Unreadable, "the animation came out malformed");
        var outFormat = ext switch
        {
            "mp3" => MediaFormat.Mp3,
            "png" => MediaFormat.Png,
            "webp" => MediaFormat.WebP,
            _ => MediaFormat.WebM,
        };
        var check = await ProbeAsync(output, outFormat, countFrames: true, workDir, limits, ct).ConfigureAwait(false);
        if (check is not { } done || (kind == UploadKind.Sound ? done.Audio is null : done.Video is null))
            return CleanResult.Fail(Unreadable, "the cleaned file did not decode");
        return new CleanResult(new CleanedMedia(output, ext, done.DurationMs, Int(done.Video, "width"), Int(done.Video, "height"), animated), null);
    }

    /// <summary>The first audio stream, the first real video stream (not cover art) and the duration; null if unreadable.</summary>
    private async Task<(JsonObject? Audio, JsonObject? Video, long DurationMs)?> ProbeAsync(string file, MediaFormat format, bool countFrames,
        string workDir, UploadLimits limits, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-hide_banner", "-max_alloc", (256L << 20).ToString(CultureInfo.InvariantCulture),
            "-protocol_whitelist", "file", "-f", format.Demuxer };
        if (format == MediaFormat.Mp4) args.AddRange(["-enable_drefs", "0"]);
        if (countFrames) args.Add("-count_frames");
        args.AddRange(["-show_entries", "format=duration:stream=index,codec_type,codec_name,width,height,pix_fmt,duration,nb_read_frames"
            + ":stream_disposition=attached_pic:stream_tags=alpha_mode", "-of", "json", "file:" + file]);
        var run = await tools.RunAsync(tools.Ffprobe, args, workDir, limits.CleanTimeout, limits.MaxMemoryBytes, ct).ConfigureAwait(false);
        if (!run.Ok) return null;
        JsonNode? json;
        try { json = JsonNode.Parse(run.Output); }
        catch (System.Text.Json.JsonException) { return null; }
        var streams = json?["streams"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var audio = streams.FirstOrDefault(s => Str(s, "codec_type") == "audio");
        var video = streams.FirstOrDefault(s => Str(s, "codec_type") == "video" && Int(s["disposition"] as JsonObject, "attached_pic") == 0);
        var seconds = Dbl(json?["format"] as JsonObject, "duration");
        if (seconds <= 0 && video is not null) seconds = Dbl(video, "duration");
        return (audio, video, (long)(seconds * 1000));
    }

    private static List<string> InputArgs(string input, MediaFormat format, JsonObject? video)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-max_alloc", (256L << 20).ToString(CultureInfo.InvariantCulture),
            "-threads", "2", "-filter_threads", "1", "-protocol_whitelist", "file" };
        if (format == MediaFormat.Mp4) args.AddRange(["-enable_drefs", "0"]);
        // libvpx keeps WebM transparency, which ffmpeg's own VP8/VP9 decoders drop.
        if (format == MediaFormat.WebM && video is not null && HasAlphaTag(video))
            args.AddRange(["-c:v", Str(video, "codec_name") == "vp8" ? "libvpx" : "libvpx-vp9"]);
        args.AddRange(["-f", format.Demuxer, "-i", "file:" + input]);
        return args;
    }

    /// <summary>The canvas size from an animated WebP's VP8X header, or null.</summary>
    private static (int Width, int Height)? AnimatedWebPSize(string file)
    {
        var head = new byte[30];
        using (var f = File.OpenRead(file)) if (f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
        if (MediaFormat.Sniff(head) != MediaFormat.WebP || !head.AsSpan(12, 4).SequenceEqual("VP8X"u8) || (head[20] & 0x02) == 0) return null;
        return (1 + (head[24] | head[25] << 8 | head[26] << 16), 1 + (head[27] | head[28] << 8 | head[29] << 16));
    }

    private static string Fit(int width, int height) =>
        $"scale=w='min(iw,{width})':h='min(ih,{height})':force_original_aspect_ratio=decrease";

    private static bool TooBig(JsonObject video, UploadLimits limits)
    {
        long w = Int(video, "width"), h = Int(video, "height");
        return w <= 0 || h <= 0 || w * h > limits.MaxSourcePixels;
    }

    private static bool HasAlphaTag(JsonObject video) => Str(video["tags"] as JsonObject, "alpha_mode") == "1";

    private static CleanResult TooLong(UploadLimits limits) => CleanResult.Fail($"This can be up to {limits.MaxDuration.TotalSeconds:0} seconds long.");

    private static string Article(UploadKind kind) => kind switch { UploadKind.Sound => "a sound", UploadKind.Image => "a picture", _ => "a video" };

    private static string Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Int(JsonObject? o, string key) => o?[key] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var i) => i,
        _ => 0,
    };

    private static double Dbl(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && double.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
