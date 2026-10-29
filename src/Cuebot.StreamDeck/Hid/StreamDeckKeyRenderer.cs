using SkiaSharp;

namespace Cuebot.StreamDeck.Hid;

/// <summary>A brief overlay on a key (Show Alert / Show Ok).</summary>
public enum KeyOverlay { None, Alert, Ok }

/// <summary>What a key shows: an optional picture (encoded PNG/JPEG/GIF/WebP/BMP bytes) with an optional title on top.</summary>
public sealed record KeyFace(string Title = "", byte[]? Image = null, KeyOverlay Overlay = KeyOverlay.None)
{
    public static readonly KeyFace Blank = new();

    public bool IsBlank => Title.Length == 0 && Image is null && Overlay == KeyOverlay.None;
}

/// <summary>
/// Draws key faces at the model's key size with SkiaSharp and encodes them in the model's format, already rotated and
/// flipped for the panel (the XL's panel is mounted upside down: its images are mirrored both ways).
/// </summary>
public static class StreamDeckKeyRenderer
{
    private static readonly Lazy<SKTypeface> Typeface = new(() =>
        SKFontManager.Default.MatchFamily("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default);

    /// <summary>The encoded image for one key.</summary>
    public static byte[] Render(StreamDeckHidModel model, KeyFace face)
    {
        if (model.ImageFormat != StreamDeckImageFormat.Jpeg || model.KeyPixels <= 0)
            throw new NotSupportedException($"{model.Name} key images are not supported yet.");
        using var bitmap = Draw(model.KeyPixels, face);
        using var oriented = Orient(bitmap, model);
        using var image = SKImage.FromBitmap(oriented);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>The face as the user sees it (not yet oriented for the panel).</summary>
    internal static SKBitmap Draw(int size, KeyFace face)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        if (face.Image is { Length: > 0 } bytes) DrawPicture(canvas, size, bytes);
        if (face.Title.Length > 0) DrawTitle(canvas, size, face.Title, face.Image is not null);
        if (face.Overlay != KeyOverlay.None) DrawOverlay(canvas, size, face.Overlay);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Rotates (counter-clockwise, like python-elgato-streamdeck's PIL helper) and then flips.</summary>
    internal static SKBitmap Orient(SKBitmap source, StreamDeckHidModel model)
    {
        var size = source.Width;
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Black);
        var c = size / 2f;
        canvas.Translate(c, c);
        canvas.Scale(model.FlipX ? -1 : 1, model.FlipY ? -1 : 1);
        if (model.Rotation != 0) canvas.RotateDegrees(-model.Rotation);
        canvas.Translate(-c, -c);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }

    private static void DrawPicture(SKCanvas canvas, int size, byte[] bytes)
    {
        using var picture = SKBitmap.Decode(bytes);
        if (picture is null || picture.Width <= 0 || picture.Height <= 0) return;
        var scale = Math.Min((float)size / picture.Width, (float)size / picture.Height);
        var w = picture.Width * scale;
        var h = picture.Height * scale;
        var dest = SKRect.Create((size - w) / 2, (size - h) / 2, w, h);
        using var image = SKImage.FromBitmap(picture);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static void DrawTitle(SKCanvas canvas, int size, string title, bool overPicture)
    {
        var lines = title.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var margin = Math.Max(3, size / 24f);
        var fontSize = size * 0.19f;
        using var font = new SKFont(Typeface.Value, fontSize) { Edging = SKFontEdging.Antialias };
        while (fontSize > size * 0.09f && (lines.Max(l => font.MeasureText(l)) > size - 2 * margin || lines.Length * fontSize * 1.15f > size - 2 * margin))
        {
            fontSize -= 1;
            font.Size = fontSize;
        }
        var lineHeight = fontSize * 1.15f;
        var block = lineHeight * lines.Length;
        // Over a picture the title sits at the bottom (like the Stream Deck app's default); alone it is centred.
        var top = overPicture ? size - margin - block : (size - block) / 2;
        var metrics = font.Metrics;
        using var outline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, fontSize / 6), StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < lines.Length; i++)
        {
            var baseline = top + i * lineHeight + (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
            canvas.DrawText(lines[i], size / 2f, baseline, SKTextAlign.Center, font, outline);
            canvas.DrawText(lines[i], size / 2f, baseline, SKTextAlign.Center, font, fill);
        }
    }

    private static void DrawOverlay(SKCanvas canvas, int size, KeyOverlay overlay)
    {
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 140) };
        canvas.DrawRect(0, 0, size, size, dim);
        var s = size;
        if (overlay == KeyOverlay.Alert)
        {
            using var triangle = new SKPath();
            triangle.MoveTo(s * 0.5f, s * 0.16f);
            triangle.LineTo(s * 0.86f, s * 0.80f);
            triangle.LineTo(s * 0.14f, s * 0.80f);
            triangle.Close();
            using var yellow = new SKPaint { Color = new SKColor(0xF5, 0xC5, 0x18), IsAntialias = true };
            canvas.DrawPath(triangle, yellow);
            using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = s * 0.07f, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(s * 0.5f, s * 0.38f, s * 0.5f, s * 0.58f, black);
            canvas.DrawPoint(s * 0.5f, s * 0.70f, black);
        }
        else
        {
            using var green = new SKPaint { Color = new SKColor(0x2E, 0xB8, 0x4B), IsAntialias = true };
            canvas.DrawCircle(s * 0.5f, s * 0.5f, s * 0.34f, green);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true, StrokeWidth = s * 0.08f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Style = SKPaintStyle.Stroke };
            using var check = new SKPath();
            check.MoveTo(s * 0.36f, s * 0.51f);
            check.LineTo(s * 0.46f, s * 0.61f);
            check.LineTo(s * 0.65f, s * 0.40f);
            canvas.DrawPath(check, white);
        }
    }
}
