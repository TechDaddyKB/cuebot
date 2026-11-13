using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Cuebot.Portal.Uploads;
using Cuebot.Server.Mcp;
using SkiaSharp;

namespace Cuebot.Portal.Media;

/// <summary>
/// The Assistant's picture and video editing (MCP media_info, edit_image, edit_video) on the streamer's own files.
/// Pictures are edited in process with SkiaSharp (already in the app for Avalonia); videos go through ffmpeg, boxed in
/// like portal uploads (<see cref="MediaTools"/>: time and memory limits, file protocol only, the format forced from the
/// file's first bytes). The source is only read; results go to a new file unless the caller passes overwrite.
/// </summary>
public sealed class MediaEditor(string mediaFolder, Func<MediaTools?> findTools, HttpMessageHandler? handler = null) : IMediaEditor
{
    public const long MaxDownloadBytes = 50L << 20;
    public const int MaxSide = 8192;
    private const long MaxSourcePixels = 12000L * 12000;

    private static readonly string[] ImageFormats = ["png", "jpg", "webp"];
    private static readonly string[] VideoFormats = ["webm", "webp", "png", "mp3"];

    private readonly Lazy<MediaTools?> _tools = new(findTools);
    private readonly HttpClient _http = CreateClient(handler);

    // ---- where files come from and go ----------------------------------------------------------------------------

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cuebot");
        return http;
    }

    private static bool IsUrl(string source, out Uri uri) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A full path with "~" expanded; throws for a relative path.</summary>
    internal static string FullPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"Give the full path of the file (not just '{path}').");
        return Path.GetFullPath(path);
    }

    /// <summary>The name a web address's file goes by ("kick-logo" for .../kick-logo.png?v=2).</summary>
    private static string UrlName(Uri uri)
    {
        var name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").TrimEnd('/'));
        name = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '-')).Trim();
        return name.Length > 0 ? name[..Math.Min(name.Length, 60)] : "download";
    }

    public string OutputFor(string source, string? output, string format, bool video)
    {
        format = Format(format, video);
        string folder, baseName;
        if (IsUrl(source, out var uri))
        {
            folder = mediaFolder;
            baseName = UrlName(uri);
        }
        else
        {
            var full = FullPath(source);
            folder = Path.GetDirectoryName(full) ?? mediaFolder;
            baseName = Path.GetFileNameWithoutExtension(full);
        }
        string path;
        if (string.IsNullOrWhiteSpace(output)) path = Path.Combine(folder, baseName + "-edited." + format);
        else
        {
            var given = output.Trim().Trim('"');
            path = given.Contains('/') || given.Contains('\\') || given.StartsWith('~') ? FullPath(given) : Path.Combine(folder, given);
            if (Path.GetFileName(path).Length == 0) throw new ArgumentException("The output needs a file name.");
            path = Path.ChangeExtension(path, format);
        }
        return Path.GetFullPath(path);
    }

    private static string Format(string format, bool video)
    {
        var f = format.Trim().TrimStart('.').ToLowerInvariant();
        if (f == "jpeg") f = "jpg";
        var allowed = video ? VideoFormats : ImageFormats;
        return allowed.Contains(f) ? f : throw new ArgumentException($"The format must be one of {string.Join(", ", allowed)}.");
    }

    /// <summary>The source as a local file: the path itself, or a download into <paramref name="work"/>.</summary>
    private async Task<string> LocalAsync(string source, string work, CancellationToken ct)
    {
        if (!IsUrl(source, out var uri))
        {
            var path = FullPath(source);
            return File.Exists(path) ? path : throw new FileNotFoundException($"There is no file at {path}.");
        }
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The web address answered {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidOperationException("The file is larger than 50 MB.");
        var file = Path.Combine(work, "download");
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(file))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxDownloadBytes) throw new InvalidOperationException("The file is larger than 50 MB.");
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        return file;
    }

    private static string NewWorkFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuebot-media-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Head(string file)
    {
        var head = new byte[64];
        using var f = File.OpenRead(file);
        var n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..n];
    }

    private static bool LooksLikeSvg(byte[] head)
    {
        var text = System.Text.Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private const string SvgNote = "SVG pictures cannot be edited here yet; save it as a PNG first (or find a PNG version).";

    /// <summary>Moves the finished file to <paramref name="output"/>; refuses to replace a file unless allowed.</summary>
    private static void Place(string finished, string output, bool overwrite)
    {
        if (File.Exists(output) && !overwrite) throw new InvalidOperationException($"{output} already exists.");
        var folder = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder {folder} does not exist.");
        File.Move(finished, output, overwrite: true);
    }

    // ---- looking ---------------------------------------------------------------------------------------------------

    public async Task<JsonObject> InfoAsync(string source, CancellationToken ct)
    {
        var work = NewWorkFolder();
        try
        {
            var file = await LocalAsync(source, work, ct).ConfigureAwait(false);
            var head = Head(file);
            var info = new JsonObject { ["source"] = source, ["bytes"] = new FileInfo(file).Length };
            if (LooksLikeSvg(head))
            {
                info["kind"] = "image";
                info["format"] = "SVG";
                info["note"] = SvgNote;
                return info;
            }
            var format = MediaFormat.Sniff(head);
            if (format is null || format == MediaFormat.Png || format == MediaFormat.Jpeg || format == MediaFormat.WebP || format == MediaFormat.Gif)
            {
                using var codec = SKCodec.Create(file);
                if (codec is not null)
                {
                    info["kind"] = "image";
                    info["format"] = codec.EncodedFormat.ToString().ToUpperInvariant();
                    info["width"] = codec.Info.Width;
                    info["height"] = codec.Info.Height;
                    info["frames"] = Math.Max(1, codec.FrameCount);
                    info["seeThrough"] = codec.Info.AlphaType != SKAlphaType.Opaque;
                    return info;
                }
                if (format is null) throw new NotSupportedException("This is not a picture, video or sound file Cuebot can read.");
            }
            var probe = await ProbeAsync(file, format, work, ct).ConfigureAwait(false);
            info["kind"] = probe.Video is not null ? "video" : "sound";
            info["format"] = format.Name;
            if (probe.Video is { } v)
            {
                info["width"] = Int(v, "width");
                info["height"] = Int(v, "height");
            }
            info["seconds"] = Math.Round(probe.Seconds, 2);
            info["hasSound"] = probe.Audio is not null;
            return info;
        }
        finally
        {
            Delete(work);
        }
    }

    // ---- pictures --------------------------------------------------------------------------------------------------

    public async Task<JsonObject> EditImageAsync(string source, string? output, string format, JsonArray steps, bool overwrite, CancellationToken ct)
    {
        var target = OutputFor(source, output, format, video: false);
        format = Format(format, video: false);
        if (steps.Count > 30) throw new ArgumentException("Use at most 30 steps.");
        var work = NewWorkFolder();
        try
        {
            var file = await LocalAsync(source, work, ct).ConfigureAwait(false);
            var notes = new List<string>();
            var bitmap = Load(file, notes);
            try
            {
                foreach (var node in steps)
                {
                    ct.ThrowIfCancellationRequested();
                    if (node is not JsonObject step) throw new ArgumentException("Each step is an object like {\"op\": \"trim\"}.");
                    var next = Apply(bitmap, step, notes);
                    if (!ReferenceEquals(next, bitmap)) { bitmap.Dispose(); bitmap = next; }
                    if (bitmap.Width > MaxSide || bitmap.Height > MaxSide)
                        throw new ArgumentException($"The picture would be larger than {MaxSide} pixels on a side.");
                }
                if (format == "jpg" && HasTransparency(bitmap))
                {
                    var flat = Background(bitmap, SKColors.White);
                    bitmap.Dispose();
                    bitmap = flat;
                    notes.Add("JPG has no see-through parts: they were filled with white (use PNG or WebP to keep them).");
                }
                var finished = Path.Combine(work, "out." + format);
                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(format switch { "jpg" => SKEncodedImageFormat.Jpeg, "webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png }, 90)
                                  ?? throw new InvalidOperationException("The picture could not be saved."))
                await using (var stream = File.Create(finished))
                    data.SaveTo(stream);
                Place(finished, target, overwrite);
                var result = new JsonObject
                {
                    ["output"] = target, ["format"] = format, ["width"] = bitmap.Width, ["height"] = bitmap.Height,
                    ["bytes"] = new FileInfo(target).Length,
                };
                if (notes.Count > 0) result["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]);
                return result;
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        finally
        {
            Delete(work);
        }
    }

    /// <summary>The first frame, upright (camera photos are turned the way their EXIF says), as premultiplied RGBA.</summary>
    internal static SKBitmap Load(string file, List<string> notes)
    {
        if (LooksLikeSvg(Head(file))) throw new NotSupportedException(SvgNote);
        using var codec = SKCodec.Create(file) ?? throw new NotSupportedException("This is not a picture Cuebot can read (PNG, JPG, WebP, GIF, BMP or ICO).");
        if ((long)codec.Info.Width * codec.Info.Height > MaxSourcePixels) throw new ArgumentException("The picture is too large to edit.");
        if (codec.FrameCount > 1) notes.Add($"It is animated ({codec.FrameCount} frames); only the first frame was used. Use edit_video for the animation.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new NotSupportedException($"The picture could not be read ({result}).");
        }
        var degrees = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (degrees == 0) return bitmap;
        var turned = Rotate(bitmap, degrees);
        bitmap.Dispose();
        return turned;
    }

    internal static SKBitmap Apply(SKBitmap b, JsonObject step, List<string> notes)
    {
        var op = Text(step, "op");
        switch (op.ToLowerInvariant())
        {
            case "resize":
            {
                var w = Num(step, "width");
                var h = Num(step, "height");
                if (w is null && h is null) throw new ArgumentException("resize needs a width or a height.");
                var fit = Text(step, "fit").ToLowerInvariant();
                if (w is null || h is null || fit == "stretch")
                {
                    var tw = w ?? b.Width * h!.Value / b.Height;
                    var th = h ?? b.Height * w!.Value / b.Width;
                    return Scale(b, Px(tw), Px(th));
                }
                var scale = fit == "cover" ? Math.Max(w.Value / b.Width, h.Value / b.Height) : Math.Min(w.Value / b.Width, h.Value / b.Height);
                var scaled = Scale(b, Px(b.Width * scale), Px(b.Height * scale));
                if (fit != "cover") return scaled;
                var cut = Crop(scaled, (scaled.Width - Px(w.Value)) / 2, (scaled.Height - Px(h.Value)) / 2, Px(w.Value), Px(h.Value));
                scaled.Dispose();
                return cut;
            }
            case "maxsize":
            {
                var size = Num(step, "size") ?? throw new ArgumentException("maxSize needs a size.");
                var longest = Math.Max(b.Width, b.Height);
                if (longest <= size) return b;
                return Scale(b, Px(b.Width * size / longest), Px(b.Height * size / longest));
            }
            case "crop":
                return Crop(b, (int)(Num(step, "x") ?? 0), (int)(Num(step, "y") ?? 0),
                    Px(Num(step, "width") ?? throw new ArgumentException("crop needs a width.")),
                    Px(Num(step, "height") ?? throw new ArgumentException("crop needs a height.")));
            case "trim":
                return Trim(b, (int)Math.Clamp(Num(step, "tolerance") ?? 10, 0, 255));
            case "square":
            {
                var side = Math.Max(b.Width, b.Height);
                return Draw(side, side, c => c.DrawBitmap(b, (side - b.Width) / 2f, (side - b.Height) / 2f));
            }
            case "pad":
            {
                var p = (int)Math.Clamp(Num(step, "pixels") ?? 0, 0, 2000);
                return Draw(b.Width + 2 * p, b.Height + 2 * p, c => c.DrawBitmap(b, p, p));
            }
            case "rotate":
            {
                var degrees = (int)(Num(step, "degrees") ?? 0);
                degrees = ((degrees % 360) + 360) % 360;
                return degrees is 90 or 180 or 270 ? Rotate(b, degrees) : degrees == 0 ? b : throw new ArgumentException("rotate takes 90, 180 or 270 degrees.");
            }
            case "flip":
            {
                var vertical = Text(step, "direction").Equals("vertical", StringComparison.OrdinalIgnoreCase);
                return Draw(b.Width, b.Height, c =>
                {
                    c.Translate(vertical ? 0 : b.Width, vertical ? b.Height : 0);
                    c.Scale(vertical ? 1 : -1, vertical ? -1 : 1);
                    c.DrawBitmap(b, 0, 0);
                });
            }
            case "circle":
            {
                var side = Math.Min(b.Width, b.Height);
                return Draw(side, side, c =>
                {
                    using var path = new SKPath();
                    path.AddCircle(side / 2f, side / 2f, side / 2f);
                    c.ClipPath(path, antialias: true);
                    c.DrawBitmap(b, -(b.Width - side) / 2f, -(b.Height - side) / 2f);
                });
            }
            case "maketransparent":
            {
                var color = Text(step, "color");
                var key = color.Length == 0 || color.Equals("corner", StringComparison.OrdinalIgnoreCase) ? Pixel(b, 0, 0) : ParseColor(color);
                var count = MakeTransparent(b, key, (int)Math.Clamp(Num(step, "tolerance") ?? 30, 0, 255));
                notes.Add($"{count} pixels became see-through.");
                return b;
            }
            case "background":
                return Background(b, ParseColor(Text(step, "color") is { Length: > 0 } c ? c : "#FFFFFF"));
            default:
                throw new ArgumentException($"Unknown step '{op}'. Steps: resize, maxSize, crop, trim, square, pad, rotate, flip, circle, makeTransparent, background.");
        }
    }

    private static int Px(double value) => Math.Max(1, (int)Math.Round(value));

    private static SKBitmap Draw(int width, int height, Action<SKCanvas> draw)
    {
        if (width > MaxSide || height > MaxSide) throw new ArgumentException($"The picture would be larger than {MaxSide} pixels on a side.");
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        canvas.Flush();
        return result;
    }

    private static SKBitmap Scale(SKBitmap b, int width, int height)
    {
        if (width > MaxSide || height > MaxSide) throw new ArgumentException($"The picture would be larger than {MaxSide} pixels on a side.");
        return b.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new InvalidOperationException("The picture could not be resized.");
    }

    private static SKBitmap Crop(SKBitmap b, int x, int y, int width, int height)
    {
        var area = SKRectI.Intersect(new SKRectI(x, y, x + width, y + height), new SKRectI(0, 0, b.Width, b.Height));
        if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException($"The crop is outside the picture ({b.Width}x{b.Height}).");
        return Draw(area.Width, area.Height, c => c.DrawBitmap(b, -area.Left, -area.Top));
    }

    private static SKBitmap Rotate(SKBitmap b, int degrees)
    {
        var swap = degrees is 90 or 270;
        int w = swap ? b.Height : b.Width, h = swap ? b.Width : b.Height;
        return Draw(w, h, c =>
        {
            c.Translate(w / 2f, h / 2f);
            c.RotateDegrees(degrees);
            c.DrawBitmap(b, -b.Width / 2f, -b.Height / 2f);
        });
    }

    private static SKBitmap Background(SKBitmap b, SKColor color) => Draw(b.Width, b.Height, c =>
    {
        c.Clear(color);
        c.DrawBitmap(b, 0, 0);
    });

    private static SKColor Pixel(SKBitmap b, int x, int y)
    {
        var p = b.GetPixelSpan();
        var i = (y * b.Width + x) * 4;
        return Unpremultiply(p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    private static SKColor Unpremultiply(byte r, byte g, byte bl, byte a) => a == 0
        ? new SKColor(0, 0, 0, 0)
        : new SKColor((byte)Math.Min(255, r * 255 / a), (byte)Math.Min(255, g * 255 / a), (byte)Math.Min(255, bl * 255 / a), a);

    private static bool Near(SKColor a, SKColor b, int tolerance) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static bool HasTransparency(SKBitmap b)
    {
        var p = b.GetPixelSpan();
        for (var i = 3; i < p.Length; i += 4) if (p[i] < 255) return true;
        return false;
    }

    /// <summary>Cuts edges that are see-through (alpha at most <paramref name="tolerance"/>) or the colour of the top-left corner.</summary>
    private static SKBitmap Trim(SKBitmap b, int tolerance)
    {
        var corner = Pixel(b, 0, 0);
        var seeThrough = corner.Alpha <= tolerance;
        var p = b.GetPixelSpan().ToArray();
        bool Empty(int x, int y)
        {
            var i = (y * b.Width + x) * 4;
            if (seeThrough) return p[i + 3] <= tolerance;
            return p[i + 3] >= 255 - tolerance && Near(Unpremultiply(p[i], p[i + 1], p[i + 2], p[i + 3]), corner, tolerance);
        }
        int left = b.Width, top = b.Height, right = -1, bottom = -1;
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                if (Empty(x, y)) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        if (right < 0) throw new ArgumentException("The picture is empty: nothing is left after trimming.");
        return Crop(b, left, top, right - left + 1, bottom - top + 1);
    }

    private static int MakeTransparent(SKBitmap b, SKColor key, int tolerance)
    {
        var p = b.GetPixelSpan().ToArray();
        var count = 0;
        for (var i = 0; i < p.Length; i += 4)
        {
            if (p[i + 3] == 0 || !Near(Unpremultiply(p[i], p[i + 1], p[i + 2], p[i + 3]), key, tolerance)) continue;
            p[i] = p[i + 1] = p[i + 2] = p[i + 3] = 0;
            count++;
        }
        Marshal.Copy(p, 0, b.GetPixels(), p.Length);
        b.NotifyPixelsChanged();
        return count;
    }

    internal static SKColor ParseColor(string text)
    {
        var t = text.Trim();
        if (SKColor.TryParse(t, out var color)) return color;
        var named = typeof(SKColors).GetField(t, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
        return named?.GetValue(null) is SKColor c ? c : throw new ArgumentException($"'{text}' is not a colour (use #RRGGBB, #AARRGGBB or a name like white).");
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static double? Num(JsonObject o, string key)
    {
        double? n = o[key] switch
        {
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<long>(out var l) => l,
            JsonValue v when v.TryGetValue<int>(out var i) => i,
            JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        return n is { } x && double.IsFinite(x) ? x : null;
    }

    // ---- videos ----------------------------------------------------------------------------------------------------

    private MediaTools Tools => _tools.Value ?? throw new InvalidOperationException(
        "Editing videos needs ffmpeg, and Cuebot found none. Cuebot's downloads include it; or install ffmpeg on this computer.");

    private static readonly TimeSpan VideoTimeout = TimeSpan.FromMinutes(10);
    private const long VideoMemory = 2048L << 20;

    public async Task<JsonObject> EditVideoAsync(string source, string? output, string format, JsonObject options, bool overwrite, CancellationToken ct)
    {
        var target = OutputFor(source, output, format, video: true);
        format = Format(format, video: true);
        var tools = Tools;
        var work = NewWorkFolder();
        try
        {
            var file = await LocalAsync(source, work, ct).ConfigureAwait(false);
            var input = MediaFormat.Sniff(Head(file));
            if (input is null || input == MediaFormat.Png || input == MediaFormat.Jpeg)
                throw new NotSupportedException("This is not a video or sound file ffmpeg can edit here (MP4, MOV, WebM, GIF, MP3, WAV, OGG...).");
            if (input == MediaFormat.WebP) throw new NotSupportedException("Animated WebP cannot be edited as a video; use the GIF, MP4 or WebM version.");
            var probe = await ProbeAsync(file, input, work, ct).ConfigureAwait(false);
            if (format == "mp3" ? probe.Audio is null : probe.Video is null)
                throw new NotSupportedException(format == "mp3" ? "This file has no sound." : "This file has no picture.");

            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-threads", "4" };
            var start = Num(options, "start");
            var end = Num(options, "end");
            var duration = Num(options, "duration");
            if (start is { } s && s > 0) args.AddRange(["-ss", Inv(s)]);
            if (input == MediaFormat.Mp4) args.AddRange(["-enable_drefs", "0"]);
            // libvpx keeps WebM transparency, which ffmpeg's own VP8/VP9 decoders drop.
            if (input == MediaFormat.WebM && probe.Video is { } v0 && Str(v0["tags"] as JsonObject, "alpha_mode") == "1")
                args.AddRange(["-c:v", Str(v0, "codec_name") == "vp8" ? "libvpx" : "libvpx-vp9"]);
            args.AddRange(["-f", input.Demuxer, "-i", "file:" + file]);
            if (end is { } e) duration = e - (start ?? 0);
            if (duration is { } d)
            {
                if (d <= 0) throw new ArgumentException("The end must be after the start.");
                args.AddRange(["-t", Inv(d)]);
            }

            var filters = VideoFilters(options, evenSize: format == "webm");
            var mute = options["mute"] is JsonValue m && m.TryGetValue<bool>(out var mv) && mv;
            switch (format)
            {
                case "webm":
                {
                    var alpha = Str(probe.Video, "pix_fmt").StartsWith("yuva", StringComparison.Ordinal) || input == MediaFormat.Gif;
                    args.AddRange(["-map", "0:v:0"]);
                    if (filters.Length > 0) args.AddRange(["-vf", filters]);
                    args.AddRange(["-c:v", "libvpx-vp9", "-pix_fmt", alpha ? "yuva420p" : "yuv420p", "-b:v", "0", "-crf", "32", "-deadline", "good",
                        "-cpu-used", "4", "-row-mt", "1", "-auto-alt-ref", alpha ? "0" : "1"]);
                    if (probe.Audio is not null && !mute) args.AddRange(["-map", "0:a:0", "-c:a", "libopus", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
                    args.AddRange(["-f", "webm"]);
                    break;
                }
                case "webp":
                    args.AddRange(["-map", "0:v:0"]);
                    if (filters.Length > 0) args.AddRange(["-vf", filters]);
                    args.AddRange(["-c:v", "libwebp_anim", "-lossless", "0", "-quality", "80", "-loop", "0", "-an", "-f", "webp"]);
                    break;
                case "png":
                    args.AddRange(["-map", "0:v:0"]);
                    if (filters.Length > 0) args.AddRange(["-vf", filters]);
                    args.AddRange(["-frames:v", "1", "-c:v", "png", "-update", "1", "-f", "image2"]);
                    break;
                default:
                    args.AddRange(["-map", "0:a:0", "-vn", "-c:a", "libmp3lame", "-b:a", "192k", "-f", "mp3"]);
                    break;
            }
            var finished = Path.Combine(work, "out." + format);
            args.AddRange(["-sn", "-dn", "-y", "file:" + finished]);
            var run = await tools.RunAsync(tools.Ffmpeg, args, work, VideoTimeout, VideoMemory, ct).ConfigureAwait(false);
            if (!run.Ok || !File.Exists(finished))
                throw new InvalidOperationException(run.Stopped switch
                {
                    ToolStop.Timeout => "Editing took longer than 10 minutes; try a shorter part or a smaller size.",
                    ToolStop.Memory => "Editing needed too much memory; try a smaller size.",
                    ToolStop.Cancelled => "Editing was stopped.",
                    _ => "ffmpeg could not edit it: " + FirstLine(run.Errors),
                });
            Place(finished, target, overwrite);
            var result = new JsonObject { ["output"] = target, ["format"] = format, ["bytes"] = new FileInfo(target).Length };
            if (format is "webm" or "mp3")
            {
                var done = await ProbeAsync(target, format == "mp3" ? MediaFormat.Mp3 : MediaFormat.WebM, work, ct).ConfigureAwait(false);
                if (done.Video is { } v)
                {
                    result["width"] = Int(v, "width");
                    result["height"] = Int(v, "height");
                }
                result["seconds"] = Math.Round(done.Seconds, 2);
            }
            else if (format == "png" && SKCodec.Create(target) is { } codec)
            {
                using (codec)
                {
                    result["width"] = codec.Info.Width;
                    result["height"] = codec.Info.Height;
                }
            }
            return result;
        }
        finally
        {
            Delete(work);
        }
    }

    /// <summary>crop, then turn, then flip, then shrink, then fps (the order a person would do them in).</summary>
    internal static string VideoFilters(JsonObject options, bool evenSize)
    {
        var filters = new List<string>();
        if (options["crop"] is JsonObject crop)
        {
            var w = Num(crop, "width") ?? throw new ArgumentException("crop needs a width.");
            var h = Num(crop, "height") ?? throw new ArgumentException("crop needs a height.");
            filters.Add($"crop={Px(w)}:{Px(h)}:{(int)(Num(crop, "x") ?? 0)}:{(int)(Num(crop, "y") ?? 0)}");
        }
        switch ((int)(Num(options, "rotate") ?? 0) % 360)
        {
            case 0: break;
            case 90 or -270: filters.Add("transpose=1"); break;
            case 180 or -180: filters.Add("hflip,vflip"); break;
            case 270 or -90: filters.Add("transpose=2"); break;
            default: throw new ArgumentException("rotate takes 90, 180 or 270.");
        }
        switch (Text(options, "flip").ToLowerInvariant())
        {
            case "": break;
            case "horizontal": filters.Add("hflip"); break;
            case "vertical": filters.Add("vflip"); break;
            default: throw new ArgumentException("flip is horizontal or vertical.");
        }
        var maxW = Num(options, "maxWidth");
        var maxH = Num(options, "maxHeight");
        if (maxW is not null || maxH is not null || evenSize)
        {
            var w = maxW is { } mw ? Px(mw).ToString(CultureInfo.InvariantCulture) : "iw";
            var h = maxH is { } mh ? Px(mh).ToString(CultureInfo.InvariantCulture) : "ih";
            filters.Add($"scale=w='min(iw,{w})':h='min(ih,{h})':force_original_aspect_ratio=decrease" + (evenSize ? ":force_divisible_by=2" : ""));
        }
        if (Num(options, "fps") is { } fps)
        {
            if (fps is <= 0 or > 120) throw new ArgumentException("fps must be between 1 and 120.");
            filters.Add("fps=" + Inv(fps));
        }
        return string.Join(",", filters);
    }

    private async Task<(JsonObject? Audio, JsonObject? Video, double Seconds)> ProbeAsync(string file, MediaFormat format, string work, CancellationToken ct)
    {
        var tools = Tools;
        var args = new List<string> { "-v", "error", "-hide_banner", "-protocol_whitelist", "file", "-f", format.Demuxer };
        if (format == MediaFormat.Mp4) args.AddRange(["-enable_drefs", "0"]);
        args.AddRange(["-show_entries", "format=duration:stream=codec_type,codec_name,width,height,pix_fmt,duration:stream_disposition=attached_pic:stream_tags=alpha_mode",
            "-of", "json", "file:" + file]);
        var run = await tools.RunAsync(tools.Ffprobe, args, work, TimeSpan.FromMinutes(1), VideoMemory, ct).ConfigureAwait(false);
        JsonNode? json = null;
        if (run.Ok)
        {
            try { json = JsonNode.Parse(run.Output); }
            catch (System.Text.Json.JsonException) { }
        }
        if (json is null) throw new NotSupportedException("ffprobe could not read this file: " + FirstLine(run.Errors));
        var streams = json["streams"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var audio = streams.FirstOrDefault(s => Str(s, "codec_type") == "audio");
        var video = streams.FirstOrDefault(s => Str(s, "codec_type") == "video" && Int(s["disposition"] as JsonObject, "attached_pic") == 0);
        var seconds = Dbl(json["format"] as JsonObject, "duration");
        if (seconds <= 0 && video is not null) seconds = Dbl(video, "duration");
        return (audio, video, seconds);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line
            ? line[..Math.Min(line.Length, 300)]
            : "no details";

    private static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

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
