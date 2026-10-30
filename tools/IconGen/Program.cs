// Renders the Cuebot icon (PNG sizes + a PNG-compressed .ico) into the directory given as the first argument.
using SkiaSharp;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

static byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 256f);

    // Tech Daddy Digital brand gradient (blue, green, magenta, purple), along the banner's diagonal.
    using var bg = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(
        new SKPoint(0, 51), new SKPoint(256, 205),
        [SKColor.Parse("#0195DA"), SKColor.Parse("#0195DA"), SKColor.Parse("#00B361"), SKColor.Parse("#AF0070"), SKColor.Parse("#6E0075")],
        [0f, 0.30f, 0.55f, 0.78f, 1f], SKShaderTileMode.Clamp) };
    c.DrawRoundRect(new SKRect(8, 8, 248, 248), 56, 56, bg);

    // A cue: a play triangle flowing out of a node.
    using var white = new SKPaint { IsAntialias = true, Color = SKColors.White };
    using var line = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round };
    c.DrawCircle(78, 128, 26, white);
    c.DrawLine(104, 128, 128, 128, line);
    using var tri = new SKPath();
    tri.MoveTo(140, 82); tri.LineTo(206, 128); tri.LineTo(140, 174); tri.Close();
    c.DrawPath(tri, white);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

int[] pngSizes = [16, 32, 48, 64, 128, 256, 512];
var pngs = pngSizes.ToDictionary(s => s, Render);
foreach (var (size, bytes) in pngs) File.WriteAllBytes(Path.Combine(outDir, $"cuebot-{size}.png"), bytes);
File.WriteAllBytes(Path.Combine(outDir, "cuebot.png"), pngs[256]);

// ICO with embedded PNG images (supported since Windows Vista).
int[] icoSizes = [16, 32, 48, 256];
using var ico = new BinaryWriter(File.Create(Path.Combine(outDir, "cuebot.ico")));
ico.Write((short)0); ico.Write((short)1); ico.Write((short)icoSizes.Length);
var offset = 6 + 16 * icoSizes.Length;
foreach (var s in icoSizes)
{
    ico.Write((byte)(s >= 256 ? 0 : s)); ico.Write((byte)(s >= 256 ? 0 : s));
    ico.Write((byte)0); ico.Write((byte)0); ico.Write((short)1); ico.Write((short)32);
    ico.Write(pngs[s].Length); ico.Write(offset);
    offset += pngs[s].Length;
}
foreach (var s in icoSizes) ico.Write(pngs[s]);
Console.WriteLine($"Icons written to {Path.GetFullPath(outDir)}");
