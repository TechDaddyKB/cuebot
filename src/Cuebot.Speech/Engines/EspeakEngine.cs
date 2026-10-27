using System.Globalization;
using System.Text;

namespace Cuebot.Speech;

/// <summary>Speaks through the espeak-ng command-line program (Linux, or Windows when installed).</summary>
internal sealed class EspeakEngine : ITtsEngine
{
    private const int NormalWordsPerMinute = 175;
    private const int MinimumWordsPerMinute = 80;
    private const int MaximumWordsPerMinute = 450;

    private readonly Lazy<string?> _executable;

    public EspeakEngine() : this(FindExecutable)
    {
    }

    /// <param name="findExecutable">Locates the espeak binary; null result means not installed.</param>
    internal EspeakEngine(Func<string?> findExecutable) => _executable = new Lazy<string?>(findExecutable);

    public string Id => "espeak";
    public string DisplayName => "eSpeak NG";
    public bool IsAvailable => _executable.Value is not null;
    public string? UnavailableReason => IsAvailable ? null : "espeak-ng is not installed or not on PATH.";

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        var result = await RunAsync(["--voices"], input: null, ct).ConfigureAwait(false);
        return EspeakVoiceParser.Parse(Encoding.UTF8.GetString(result.Output));
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct)
    {
        // Text goes over stdin so input starting with '-' is never parsed as an option.
        var result = await RunAsync(BuildArguments(request), request.Text, ct).ConfigureAwait(false);
        // espeak streams to stdout with placeholder sizes; rebuilding gives a header players accept.
        return SynthesizedAudio.Wav(Wav.Normalize(result.Output));
    }

    internal static List<string> BuildArguments(SynthesisRequest request)
    {
        List<string> arguments =
        [
            "-b", "1",
            "-s", RateToWordsPerMinute(request.Rate).ToString(CultureInfo.InvariantCulture),
            "-p", PitchToEspeak(request.Pitch).ToString(CultureInfo.InvariantCulture),
            "--stdout", "--stdin",
        ];
        if (!string.IsNullOrWhiteSpace(request.VoiceId)) arguments.InsertRange(0, ["-v", request.VoiceId]);
        return arguments;
    }

    /// <summary>Maps the -10..10 rate scale to espeak's words per minute (80..450, 175 normal).</summary>
    internal static int RateToWordsPerMinute(int rate)
    {
        var clamped = Math.Clamp(rate, -10, 10);
        var step = clamped < 0
            ? (NormalWordsPerMinute - MinimumWordsPerMinute) / 10.0
            : (MaximumWordsPerMinute - NormalWordsPerMinute) / 10.0;
        return (int)Math.Round(NormalWordsPerMinute + clamped * step);
    }

    /// <summary>Maps the -10..10 pitch scale to espeak's 0..99 (50 normal).</summary>
    internal static int PitchToEspeak(int pitch) => Math.Clamp(50 + Math.Clamp(pitch, -10, 10) * 5, 0, 99);

    private async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, string? input, CancellationToken ct)
    {
        var executable = _executable.Value ?? throw new InvalidOperationException(UnavailableReason);
        var result = await ProcessRunner.RunAsync(executable, arguments, input, ct).ConfigureAwait(false);
        return result.EnsureSuccess("espeak-ng");
    }

    private static string? FindExecutable()
    {
        var installed = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "eSpeak NG", "espeak-ng.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "eSpeak NG", "espeak-ng.exe"),
            }
            : [];
        return ExecutableLocator.Find(["espeak-ng", "espeak"], installed);
    }
}
