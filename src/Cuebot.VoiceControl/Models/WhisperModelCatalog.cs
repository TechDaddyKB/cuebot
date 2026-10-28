using System.Globalization;

namespace Cuebot.VoiceControl;

/// <summary>A downloadable Whisper model (ggml format, from the whisper.cpp model repository on Hugging Face).</summary>
/// <param name="Id">Stored in voicecontrol.json, e.g. "base.en-q5_1".</param>
/// <param name="EnglishOnly">".en" models only understand English but are more accurate for it.</param>
/// <param name="Sha256">Checked after every download (hex, lower case).</param>
public sealed record WhisperModel(string Id, string DisplayName, string FileName, long SizeBytes, string Sha256, bool EnglishOnly, string Notes)
{
    public Uri Url => new($"{WhisperModelCatalog.BaseUrl}{FileName}");

    public string SizeText => SizeBytes >= 1_000_000_000
        ? $"{SizeBytes / 1_000_000_000.0:0.0} GB"
        : $"{SizeBytes / 1_000_000.0:0} MB";
}

/// <summary>
/// The models Cuebot offers. Downloads come from a pinned revision of <c>ggerganov/whisper.cpp</c> (MIT; the weights are
/// OpenAI's MIT-licensed Whisper models), so sizes and SHA-256 sums stay valid. Quantised (q5/q8) variants are listed
/// because they are much smaller and faster on a CPU at almost the same accuracy.
/// </summary>
public static class WhisperModelCatalog
{
    /// <summary>Revision of huggingface.co/ggerganov/whisper.cpp the sums below were taken from.</summary>
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + Revision + "/";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("tiny.en-q5_1", "Tiny (English)", "ggml-tiny.en-q5_1.bin", 32166155, "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b", true,
            "Fastest; fine for short command phrases on slow PCs"),
        new("tiny-q5_1", "Tiny (multilingual)", "ggml-tiny-q5_1.bin", 32152673, "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7", false,
            "Fastest; any language"),
        new("base.en-q5_1", "Base (English)", "ggml-base.en-q5_1.bin", 59721011, "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f", true,
            "Recommended for English: good accuracy, real-time on any recent CPU"),
        new("base-q5_1", "Base (multilingual)", "ggml-base-q5_1.bin", 59707625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", false,
            "Recommended for other languages"),
        new("small.en-q5_1", "Small (English)", "ggml-small.en-q5_1.bin", 190098681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", true,
            "More accurate; needs a fast CPU or a GPU runtime"),
        new("small-q5_1", "Small (multilingual)", "ggml-small-q5_1.bin", 190085487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", false,
            "More accurate; needs a fast CPU or a GPU runtime"),
        new("medium.en-q5_0", "Medium (English)", "ggml-medium.en-q5_0.bin", 539225533, "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0", true,
            "High accuracy; GPU recommended"),
        new("medium-q5_0", "Medium (multilingual)", "ggml-medium-q5_0.bin", 539212467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", false,
            "High accuracy; GPU recommended"),
        new("large-v3-turbo-q5_0", "Large v3 Turbo (multilingual)", "ggml-large-v3-turbo-q5_0.bin", 574041195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", false, "Best accuracy; GPU strongly recommended"),
    ];

    public static WhisperModel? Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The recommended model for a language: Base English for English, Base multilingual otherwise.</summary>
    public static WhisperModel DefaultFor(CultureInfo culture) =>
        Find(culture.TwoLetterISOLanguageName == "en" ? "base.en-q5_1" : "base-q5_1")!;
}
