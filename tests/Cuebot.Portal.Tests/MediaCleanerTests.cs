using System.Text;
using Cuebot.Portal.Uploads;

namespace Cuebot.Portal.Tests;

/// <summary>
/// The upload cleaner against real ffmpeg (Cuebot's build through CUEBOT_FFMPEG_DIR in CI, or one on the PATH):
/// every accepted format comes out as fresh media, and polyglots, damaged and fuzzed files either come out clean
/// or are refused, never with a byte of their payload. Skipped where there is no ffmpeg.
/// </summary>
/// <summary>A test that needs ffmpeg: skipped where there is none.</summary>
public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (MediaTools.Find() is null) Skip = "No ffmpeg: set CUEBOT_FFMPEG_DIR or install ffmpeg";
    }
}

public sealed class MediaTheoryAttribute : TheoryAttribute
{
    public MediaTheoryAttribute()
    {
        if (MediaTools.Find() is null) Skip = "No ffmpeg: set CUEBOT_FFMPEG_DIR or install ffmpeg";
    }
}

public sealed class MediaCleanerTests : IDisposable
{
    private static readonly MediaTools? Tools = MediaTools.Find();
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-clean-").FullName;

    /// <summary>Text that must never survive cleaning.</summary>
    private const string Payload = "<script>alert('cuebot-payload')</script><?php system($_GET['x']); ?>";

    private static string Media(string name) => Path.Combine(AppContext.BaseDirectory, "Media", name);

    private static MediaCleaner Cleaner() => new(Tools!);

    private async Task<CleanResult> CleanAsync(byte[] data, UploadKind kind)
    {
        var work = Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N"))).FullName;
        var input = Path.Combine(work, "input");
        await File.WriteAllBytesAsync(input, data, CancellationToken.None);
        return await Cleaner().CleanAsync(input, kind, work, CancellationToken.None);
    }

    private static void AssertNoPayload(CleanResult result)
    {
        if (result.Media is null) return;
        var bytes = File.ReadAllBytes(result.Media.Path);
        foreach (var marker in new[] { "cuebot-payload", "<script", "<?php", "PK\u0003\u0004" })
            Assert.True(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) < 0, $"'{marker}' survived cleaning");
    }

    [MediaTheory]
    [InlineData("sound.wav", UploadKind.Sound, "mp3")]
    [InlineData("sound.mp3", UploadKind.Sound, "mp3")]
    [InlineData("sound.ogg", UploadKind.Sound, "mp3")]
    [InlineData("sound.opus", UploadKind.Sound, "mp3")]
    [InlineData("sound.flac", UploadKind.Sound, "mp3")]
    [InlineData("sound.m4a", UploadKind.Sound, "mp3")]
    [InlineData("clip.mp4", UploadKind.Sound, "mp3")]
    [InlineData("picture.png", UploadKind.Image, "png")]
    [InlineData("picture.jpg", UploadKind.Image, "png")]
    [InlineData("picture.webp", UploadKind.Image, "png")]
    [InlineData("animation.gif", UploadKind.Image, "webp")]
    [InlineData("clip.mp4", UploadKind.Video, "webm")]
    [InlineData("transparent.webm", UploadKind.Video, "webm")]
    public async Task Every_accepted_format_comes_out_as_fresh_media(string file, UploadKind kind, string extension)
    {
        var result = await CleanAsync(File.ReadAllBytes(Media(file)), kind);
        Assert.True(result.Media is not null, result.Error + " " + result.Detail);
        Assert.Equal(extension, result.Media.Extension);
        Assert.True(new FileInfo(result.Media.Path).Length > 100);
        if (kind != UploadKind.Image) Assert.InRange(result.Media.DurationMs, 400, 1500);
        if (file == "animation.gif") Assert.True(result.Media.Animated);
        if (kind == UploadKind.Image) Assert.Equal(file == "animation.gif" ? (96, 72) : (160, 120), (result.Media.Width, result.Media.Height));
    }

    [MediaFact]
    public async Task Transparent_video_stays_transparent()
    {
        var result = await CleanAsync(File.ReadAllBytes(Media("transparent.webm")), UploadKind.Video);
        Assert.NotNull(result.Media);
        // libvpx decodes it with its transparency.
        var probe = await Tools!.RunAsync(Tools.Ffprobe, ["-v", "error", "-c:v", "libvpx-vp9", "-show_entries", "stream=pix_fmt", "-of", "csv=p=0",
            result.Media.Path], _dir, TimeSpan.FromMinutes(1), 1L << 30, CancellationToken.None);
        Assert.Equal("yuva420p", probe.Output.Trim());
    }

    [MediaTheory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<!DOCTYPE html><html><script>alert(1)</script></html>")]
    [InlineData("#EXTM3U\n#EXTINF:1,\n/etc/passwd\n")]
    [InlineData("ffconcat version 1.0\nfile /etc/passwd\n")]
    [InlineData("MZ\u0090\u0000this is not media")]
    [InlineData("PK\u0003\u0004zip")]
    public async Task Anything_else_is_refused_before_ffmpeg_sees_it(string content)
    {
        var result = await CleanAsync(Encoding.Latin1.GetBytes(content), UploadKind.Image);
        Assert.Null(result.Media);
        Assert.Contains("does not take", result.Error);
    }

    [MediaFact]
    public async Task A_file_must_match_what_it_is_uploaded_as()
    {
        var result = await CleanAsync(File.ReadAllBytes(Media("sound.mp3")), UploadKind.Image);
        Assert.Null(result.Media);
        Assert.Contains("cannot be uploaded as a picture", result.Error);
    }

    public static TheoryData<string, UploadKind> Polyglots => new()
    {
        { "png+html", UploadKind.Image },
        { "png+zip", UploadKind.Image },
        { "gifar", UploadKind.Image },
        { "jpeg-comment", UploadKind.Image },
        { "mp3-id3", UploadKind.Sound },
        { "wav-chunk", UploadKind.Sound },
        { "mp4-metadata", UploadKind.Video },
    };

    [MediaTheory]
    [MemberData(nameof(Polyglots))]
    public async Task Polyglots_come_out_clean(string kind, UploadKind as_)
    {
        var payload = Encoding.ASCII.GetBytes(Payload);
        byte[] data = kind switch
        {
            "png+html" => [.. File.ReadAllBytes(Media("picture.png")), .. payload],
            "png+zip" => [.. File.ReadAllBytes(Media("picture.png")), .. "PK\u0003\u0004"u8, .. payload],
            // GIFAR-style: a JavaScript comment opened right after the header, the payload after the trailer.
            "gifar" => [.. File.ReadAllBytes(Media("animation.gif")), .. "*/="u8, .. payload, .. "//"u8],
            "jpeg-comment" => JpegWithComment(File.ReadAllBytes(Media("picture.jpg")), payload),
            "mp3-id3" => [.. Id3Tag(payload), .. File.ReadAllBytes(Media("sound.mp3"))],
            "wav-chunk" => WavWithChunk(File.ReadAllBytes(Media("sound.wav")), payload),
            _ => [.. File.ReadAllBytes(Media("clip.mp4")), .. Atom("udta", payload)],
        };
        var result = await CleanAsync(data, as_);
        Assert.True(result.Media is not null, result.Error + " " + result.Detail);
        AssertNoPayload(result);
    }

    [MediaTheory]
    [InlineData("picture.png", UploadKind.Image)]
    [InlineData("picture.jpg", UploadKind.Image)]
    [InlineData("sound.flac", UploadKind.Sound)]
    [InlineData("clip.mp4", UploadKind.Video)]
    [InlineData("transparent.webm", UploadKind.Video)]
    public async Task Cut_off_files_are_refused_or_cleaned(string file, UploadKind kind)
    {
        var whole = File.ReadAllBytes(Media(file));
        var result = await CleanAsync(whole[..(whole.Length / 3)], kind);
        AssertNoPayload(result);
        if (result.Media is null) Assert.False(string.IsNullOrEmpty(result.Error));
    }

    /// <summary>Random damage to every format: each run either refuses or produces a file that decodes. Never a crash or a hang.</summary>
    [MediaTheory]
    [InlineData("picture.png", UploadKind.Image)]
    [InlineData("picture.jpg", UploadKind.Image)]
    [InlineData("picture.webp", UploadKind.Image)]
    [InlineData("animation.gif", UploadKind.Image)]
    [InlineData("sound.wav", UploadKind.Sound)]
    [InlineData("sound.mp3", UploadKind.Sound)]
    [InlineData("sound.ogg", UploadKind.Sound)]
    [InlineData("sound.m4a", UploadKind.Sound)]
    [InlineData("clip.mp4", UploadKind.Video)]
    [InlineData("transparent.webm", UploadKind.Video)]
    public async Task Fuzzed_files_are_refused_or_cleaned(string file, UploadKind kind)
    {
        var whole = File.ReadAllBytes(Media(file));
        var random = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 20261106);
        for (var run = 0; run < 12; run++)
        {
            var data = (byte[])whole.Clone();
            // Keep the first bytes so the format is still recognised and ffmpeg has to parse the damage.
            for (var i = 0; i < 1 + run * 3; i++) data[random.Next(16, data.Length)] = (byte)random.Next(256);
            var result = await CleanAsync(data, kind);
            Assert.True(result.Media is not null || !string.IsNullOrEmpty(result.Error));
        }
    }

    [MediaFact]
    public async Task A_picture_claiming_huge_dimensions_is_refused()
    {
        // A PNG header for 30000 × 30000 pixels: refused from the header, before anything is decoded.
        var png = File.ReadAllBytes(Media("picture.png"));
        var bomb = (byte[])png.Clone();
        WriteBigEndian(bomb, 16, 30000);
        WriteBigEndian(bomb, 20, 30000);
        var crc = Crc32(bomb.AsSpan(12, 17));
        WriteBigEndian(bomb, 29, (int)crc);
        var result = await CleanAsync(bomb, UploadKind.Image);
        Assert.Null(result.Media);
    }

    [MediaFact]
    public async Task Sounds_longer_than_the_limit_are_refused()
    {
        var result = await CleanAsync(Wav(seconds: 61, rate: 8000), UploadKind.Sound);
        Assert.Null(result.Media);
        Assert.Contains("60 seconds", result.Error);
    }

    [Fact]
    public void Sniffing_reads_bytes_not_names()
    {
        Assert.Equal(MediaFormat.Png, MediaFormat.Sniff(File.ReadAllBytes(Media("picture.png"))));
        Assert.Equal(MediaFormat.Jpeg, MediaFormat.Sniff(File.ReadAllBytes(Media("picture.jpg"))));
        Assert.Equal(MediaFormat.WebP, MediaFormat.Sniff(File.ReadAllBytes(Media("picture.webp"))));
        Assert.Equal(MediaFormat.Gif, MediaFormat.Sniff(File.ReadAllBytes(Media("animation.gif"))));
        Assert.Equal(MediaFormat.Wav, MediaFormat.Sniff(File.ReadAllBytes(Media("sound.wav"))));
        Assert.Equal(MediaFormat.Mp3, MediaFormat.Sniff(File.ReadAllBytes(Media("sound.mp3"))));
        Assert.Equal(MediaFormat.Ogg, MediaFormat.Sniff(File.ReadAllBytes(Media("sound.opus"))));
        Assert.Equal(MediaFormat.Flac, MediaFormat.Sniff(File.ReadAllBytes(Media("sound.flac"))));
        Assert.Equal(MediaFormat.Mp4, MediaFormat.Sniff(File.ReadAllBytes(Media("sound.m4a"))));
        Assert.Equal(MediaFormat.WebM, MediaFormat.Sniff(File.ReadAllBytes(Media("transparent.webm"))));
        Assert.Null(MediaFormat.Sniff("<svg"u8));
        Assert.Null(MediaFormat.Sniff("RIFF\0\0\0\0AVI "u8));
        Assert.Null(MediaFormat.Sniff([]));
    }

    private static byte[] JpegWithComment(byte[] jpeg, byte[] text)
    {
        var length = text.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xFE, (byte)(length >> 8), (byte)length, .. text, .. jpeg[2..]];
    }

    private static byte[] Id3Tag(byte[] text)
    {
        byte[] frame = [.. "COMM"u8, .. BigEndian(text.Length + 4), 0, 0, 0, .. "eng"u8, .. text];
        var size = frame.Length;
        return [.. "ID3"u8, 3, 0, 0, (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F), .. frame];
    }

    private static byte[] WavWithChunk(byte[] wav, byte[] text)
    {
        byte[] chunk = [.. "LIST"u8, .. LittleEndian(text.Length + 4 + (text.Length & 1)), .. "INFO"u8, .. text, .. (text.Length & 1) == 1 ? new byte[] { 0 } : []];
        var result = (byte[])[.. wav[..12], .. chunk, .. wav[12..]];
        LittleEndian(result.Length - 8).CopyTo(result, 4);
        return result;
    }

    private static byte[] Atom(string type, byte[] body) => [.. BigEndian(body.Length + 8), .. Encoding.ASCII.GetBytes(type), .. body];

    private static byte[] Wav(int seconds, int rate)
    {
        var samples = seconds * rate;
        var data = new byte[samples];
        for (var i = 0; i < samples; i++) data[i] = (byte)(128 + 100 * Math.Sin(i * 0.3));
        return [.. "RIFF"u8, .. LittleEndian(36 + samples), .. "WAVEfmt "u8, .. LittleEndian(16), 1, 0, 1, 0, .. LittleEndian(rate),
            .. LittleEndian(rate), 1, 0, 8, 0, .. "data"u8, .. LittleEndian(samples), .. data];
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] LittleEndian(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static void WriteBigEndian(byte[] b, int at, int v) => BigEndian(v).CopyTo(b, at);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
