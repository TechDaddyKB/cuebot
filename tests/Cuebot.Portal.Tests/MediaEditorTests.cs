using System.Net;
using System.Text.Json.Nodes;
using Cuebot.Portal.Media;
using Cuebot.Portal.Uploads;
using SkiaSharp;

namespace Cuebot.Portal.Tests;

/// <summary>The Assistant's picture and video editing (MCP edit_image, edit_video, media_info).</summary>
public sealed class MediaEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-edit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private MediaEditor Editor(HttpMessageHandler? handler = null) => new(Path.Combine(_dir, "media"), MediaTools.Find, handler);

    private static string Media(string name) => Path.Combine(AppContext.BaseDirectory, "Media", name);

    private static JsonArray Steps(params string[] json) => [.. json.Select(j => JsonNode.Parse(j))];

    /// <summary>A 300x200 picture: <paramref name="background"/> with a red 100x100 square at (100, 50).</summary>
    private string Logo(SKColor background, string name = "logo.png")
    {
        using var bitmap = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(100, 50, 100, 100, paint);
        }
        var path = Path.Combine(_dir, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static (int W, int H, SKColor TopLeft, SKColor Middle) Read(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        return (bitmap.Width, bitmap.Height, bitmap.GetPixel(0, 0), bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    [Fact]
    public async Task A_logo_becomes_a_square_see_through_throwable_next_to_the_original()
    {
        var logo = Logo(SKColors.White);
        var before = File.ReadAllBytes(logo);

        var result = await Editor().EditImageAsync(logo, null, "png",
            Steps("""{"op": "makeTransparent", "color": "corner"}""", """{"op": "trim"}""", """{"op": "pad", "pixels": 10}""",
                """{"op": "square"}""", """{"op": "resize", "width": 512}"""), overwrite: false, CancellationToken.None);

        var output = result["output"]!.GetValue<string>();
        Assert.Equal(Path.Combine(_dir, "logo-edited.png"), output);
        Assert.Equal(before, File.ReadAllBytes(logo));   // the source is never changed
        var (w, h, corner, middle) = Read(output);
        Assert.Equal((512, 512), (w, h));
        Assert.Equal(0, corner.Alpha);
        Assert.Equal(SKColors.Red, middle);
        Assert.Contains("pixels became see-through", result["notes"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Resize_fits_cover_and_stretch_and_max_size_never_enlarges()
    {
        var logo = Logo(SKColors.Blue);
        async Task<(int, int)> Size(string step, string name)
        {
            var r = await Editor().EditImageAsync(logo, name, "png", Steps(step), false, CancellationToken.None);
            return (r["width"]!.GetValue<int>(), r["height"]!.GetValue<int>());
        }
        Assert.Equal((150, 100), await Size("""{"op": "resize", "width": 150, "height": 150}""", "contain.png"));
        Assert.Equal((150, 150), await Size("""{"op": "resize", "width": 150, "height": 150, "fit": "cover"}""", "cover.png"));
        Assert.Equal((50, 70), await Size("""{"op": "resize", "width": 50, "height": 70, "fit": "stretch"}""", "stretch.png"));
        Assert.Equal((60, 40), await Size("""{"op": "resize", "width": 60}""", "width.png"));
        Assert.Equal((300, 200), await Size("""{"op": "maxSize", "size": 1000}""", "max.png"));
        Assert.Equal((200, 300), await Size("""{"op": "rotate", "degrees": 90}""", "turned.png"));
        Assert.Equal((200, 200), await Size("""{"op": "circle"}""", "round.png"));
        Assert.Equal((100, 100), await Size("""{"op": "crop", "x": 100, "y": 50, "width": 100, "height": 100}""", "crop.png"));
    }

    [Fact]
    public async Task Jpg_fills_see_through_parts_and_existing_files_are_not_replaced_without_permission()
    {
        var logo = Logo(SKColors.Transparent);
        var r = await Editor().EditImageAsync(logo, "flat", "jpg", Steps(), false, CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "flat.jpg"), r["output"]!.GetValue<string>());
        Assert.Contains("filled with white", r["notes"]!.ToJsonString());
        Assert.Equal(255, Read(Path.Combine(_dir, "flat.jpg")).TopLeft.Red);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Editor().EditImageAsync(logo, "flat", "jpg", Steps(), false, CancellationToken.None));
        await Editor().EditImageAsync(logo, "flat", "jpg", Steps("""{"op": "flip"}"""), overwrite: true, CancellationToken.None);
    }

    [Fact]
    public async Task Bad_input_says_what_is_wrong()
    {
        var logo = Logo(SKColors.Blue);
        var editor = Editor();
        await Assert.ThrowsAsync<ArgumentException>(() => editor.EditImageAsync(logo, null, "png", Steps("""{"op": "sparkle"}"""), false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.EditImageAsync("logo.png", null, "png", Steps(), false, default));   // not a full path
        await Assert.ThrowsAsync<FileNotFoundException>(() => editor.EditImageAsync(Path.Combine(_dir, "nope.png"), null, "png", Steps(), false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.EditImageAsync(logo, null, "bmp", Steps(), false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.EditImageAsync(logo, null, "png", Steps("""{"op": "crop", "x": 900, "y": 0, "width": 5, "height": 5}"""), false, default));
        var svg = Path.Combine(_dir, "logo.svg");
        File.WriteAllText(svg, "<svg xmlns='http://www.w3.org/2000/svg'/>");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => editor.EditImageAsync(svg, null, "png", Steps(), false, default));
        Assert.Contains("PNG", error.Message);
        Assert.Equal("SVG", (await editor.InfoAsync(svg, default))["format"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pictures_from_the_web_land_in_the_media_folder()
    {
        var png = File.ReadAllBytes(Logo(SKColors.Green));
        var handler = new Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        var editor = Editor(handler);
        Directory.CreateDirectory(Path.Combine(_dir, "media"));

        var info = await editor.InfoAsync("https://example.com/brand/kick-logo.png?v=2", default);
        Assert.Equal(("image", 300), (info["kind"]!.GetValue<string>(), info["width"]!.GetValue<int>()));
        var r = await editor.EditImageAsync("https://example.com/brand/kick-logo.png?v=2", null, "webp", Steps("""{"op": "trim"}"""), false, default);
        Assert.Equal(Path.Combine(_dir, "media", "kick-logo-edited.webp"), r["output"]!.GetValue<string>());
    }

    [MediaFact]
    public async Task Videos_are_cut_shrunk_turned_and_converted()
    {
        var editor = Editor();
        var clip = Path.Combine(_dir, "clip.mp4");
        File.Copy(Media("clip.mp4"), clip);
        var info = await editor.InfoAsync(clip, default);
        Assert.Equal("video", info["kind"]!.GetValue<string>());
        int w = info["width"]!.GetValue<int>(), h = info["height"]!.GetValue<int>();

        var webm = await editor.EditVideoAsync(clip, null, "webm", new JsonObject { ["start"] = 0.2, ["duration"] = 0.5, ["maxWidth"] = 32 }, false, default);
        Assert.Equal(Path.Combine(_dir, "clip-edited.webm"), webm["output"]!.GetValue<string>());
        Assert.True(webm["width"]!.GetValue<int>() <= 32);
        Assert.InRange(webm["seconds"]!.GetValue<double>(), 0.2, 0.8);

        var frame = await editor.EditVideoAsync(clip, "frame", "png", new JsonObject { ["rotate"] = 90 }, false, default);
        Assert.Equal((h, w), (frame["width"]!.GetValue<int>(), frame["height"]!.GetValue<int>()));

        var cropped = await editor.EditVideoAsync(clip, "small", "webm", new JsonObject { ["crop"] = new JsonObject { ["width"] = 16, ["height"] = 16 }, ["mute"] = true }, false, default);
        Assert.Equal(16, cropped["width"]!.GetValue<int>());

        var sound = Path.Combine(_dir, "sound.mp3");
        File.Copy(Media("sound.mp3"), sound);
        var mp3 = await editor.EditVideoAsync(sound, "short", "mp3", new JsonObject { ["duration"] = 0.5 }, false, default);
        Assert.InRange(mp3["seconds"]!.GetValue<double>(), 0.3, 0.8);
        await Assert.ThrowsAsync<NotSupportedException>(() => editor.EditVideoAsync(sound, "x", "webm", [], false, default));
    }

    [Fact]
    public void Video_filters_go_crop_turn_flip_shrink_fps()
    {
        Assert.Equal("crop=10:20:3:4,transpose=1,hflip,scale=w='min(iw,640)':h='min(ih,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,fps=30",
            MediaEditor.VideoFilters(new JsonObject
            {
                ["crop"] = new JsonObject { ["x"] = 3, ["y"] = 4, ["width"] = 10, ["height"] = 20 },
                ["rotate"] = 90, ["flip"] = "horizontal", ["maxWidth"] = 640, ["fps"] = 30,
            }, evenSize: true));
        Assert.Equal("", MediaEditor.VideoFilters([], evenSize: false));
        Assert.Throws<ArgumentException>(() => MediaEditor.VideoFilters(new JsonObject { ["rotate"] = 45 }, false));
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
