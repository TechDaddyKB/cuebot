namespace Cuebot.Portal.Uploads;

/// <summary>What a mod uploads: sound alerts need <see cref="Members.PortalPermission.UploadSounds"/>, the others
/// <see cref="Members.PortalPermission.UploadMedia"/>.</summary>
public enum UploadKind { Sound, Image, Video }

/// <summary>
/// An accepted input format: the ffmpeg demuxer it is forced through (ffmpeg never guesses the format of an upload), the
/// kinds it may be uploaded as and the codecs allowed inside it (docs/MOD_PORTAL_PLAN.md, section 8).
/// </summary>
public sealed record MediaFormat(string Name, string Demuxer, UploadKind[] Kinds, string[] Codecs)
{
    private static readonly string[] Audio = ["mp3", "aac", "vorbis", "opus", "flac", "alac", "pcm_u8", "pcm_s16le", "pcm_s16be",
        "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be", "pcm_f32le", "pcm_f32be", "pcm_f64le"];
    private static readonly string[] Video = ["h264", "hevc", "vp8", "vp9"];

    public static readonly MediaFormat Png = new("PNG", "png_pipe", [UploadKind.Image], ["png"]);
    public static readonly MediaFormat Jpeg = new("JPEG", "jpeg_pipe", [UploadKind.Image], ["mjpeg"]);
    public static readonly MediaFormat WebP = new("WebP", "webp_pipe", [UploadKind.Image], ["webp"]);
    public static readonly MediaFormat Gif = new("GIF", "gif", [UploadKind.Image], ["gif"]);
    public static readonly MediaFormat Wav = new("WAV", "wav", [UploadKind.Sound], Audio);
    public static readonly MediaFormat Mp3 = new("MP3", "mp3", [UploadKind.Sound], ["mp3"]);
    public static readonly MediaFormat Flac = new("FLAC", "flac", [UploadKind.Sound], ["flac"]);
    public static readonly MediaFormat Ogg = new("Ogg", "ogg", [UploadKind.Sound], ["vorbis", "opus", "flac"]);
    public static readonly MediaFormat Aac = new("AAC", "aac", [UploadKind.Sound], ["aac"]);
    public static readonly MediaFormat Mp4 = new("MP4/MOV/M4A", "mov", [UploadKind.Sound, UploadKind.Video], [.. Video, "aac", "mp3", "alac", "opus", "flac"]);
    public static readonly MediaFormat WebM = new("WebM", "matroska", [UploadKind.Video, UploadKind.Sound], [.. Video, "vorbis", "opus"]);

    /// <summary>The file types the portal's file picker offers per kind.</summary>
    public static string Accept(UploadKind kind) => kind switch
    {
        UploadKind.Sound => ".mp3,.wav,.ogg,.oga,.opus,.flac,.m4a,.aac,.mp4,.webm",
        UploadKind.Image => ".png,.jpg,.jpeg,.webp,.gif",
        _ => ".mp4,.mov,.m4v,.webm",
    };

    /// <summary>
    /// The format by the file's first bytes (its name and the browser's type are never trusted), or null. Anything not
    /// recognised here is refused before ffmpeg sees it: SVG, HTML, scripts, archives, executables and so on.
    /// </summary>
    // Bytes above 0x7F cannot go in a u8 string literal (it would encode them as UTF-8).
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> EbmlSignature => [0x1A, 0x45, 0xDF, 0xA3];

    public static MediaFormat? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature)) return Png;
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return Jpeg;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return Gif;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            if (head[8..12].SequenceEqual("WEBP"u8)) return WebP;
            if (head[8..12].SequenceEqual("WAVE"u8)) return Wav;
            return null;
        }
        if (head.StartsWith("fLaC"u8)) return Flac;
        if (head.StartsWith("OggS"u8)) return Ogg;
        if (head.StartsWith(EbmlSignature)) return WebM;
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8)) return Mp4;
        if (head.StartsWith("ID3"u8)) return Mp3;
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return Aac;   // ADTS: sync word, layer 0
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return Mp3;   // MPEG audio frame sync
        return null;
    }
}

/// <summary>Size, duration and resolution limits for one kind of upload.</summary>
public sealed record UploadLimits(long MaxBytes, TimeSpan MaxDuration, int MaxSourcePixels, TimeSpan CleanTimeout, long MaxMemoryBytes)
{
    public static readonly UploadLimits Sound = new(25L << 20, TimeSpan.FromSeconds(60), 0, TimeSpan.FromMinutes(2), 512L << 20);
    public static readonly UploadLimits Image = new(15L << 20, TimeSpan.FromSeconds(30), 8192 * 8192, TimeSpan.FromMinutes(3), 1024L << 20);
    public static readonly UploadLimits Video = new(200L << 20, TimeSpan.FromSeconds(120), 4096 * 2304, TimeSpan.FromMinutes(15), 1536L << 20);

    public static UploadLimits For(UploadKind kind) => kind switch
    {
        UploadKind.Sound => Sound,
        UploadKind.Image => Image,
        _ => Video,
    };
}
