using System.Collections.Concurrent;
using System.Net;
using Cuebot.Speech;
using Cuebot.VoiceControl.Engines;

namespace Cuebot.VoiceControl.Tests.Support;

/// <summary>Microphone whose samples the test pushes by hand.</summary>
internal sealed class FakeMicrophone : IAudioInput
{
    public List<AudioDevice> Devices { get; } = [new("Mic A", "Mic A", true), new("Mic B", "Mic B", false)];
    public FakeStream? Stream { get; private set; }
    public List<string?> Opened { get; } = [];
    public bool CanCapture { get; set; } = true;
    public int Rate { get; set; } = 48000;

    public IReadOnlyList<AudioDevice> GetInputDevices() => Devices;

    public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb) => throw new NotSupportedException();

    public IAudioInputStream? CaptureSamples(string? deviceId, CaptureCallback onSamples)
    {
        Opened.Add(deviceId);
        if (!CanCapture) return null;
        return Stream = new FakeStream(deviceId, Rate, onSamples);
    }

    internal sealed class FakeStream(string? deviceId, int rate, CaptureCallback callback) : IAudioInputStream
    {
        public string? DeviceId => deviceId;
        public int SampleRate => rate;
        public bool Disposed { get; private set; }
        public void Feed(float[] samples) => callback(samples);
        public void Dispose() => Disposed = true;
    }
}

/// <summary>Engine whose sessions record audio and let the test raise results.</summary>
internal sealed class FakeEngine(string id = "fake", bool available = true) : ISpeechRecognizerEngine
{
    public string Id => id;
    public string DisplayName => $"Fake {id}";
    public bool IsAvailable => available;
    public string? UnavailableReason => available ? null : "Fake engine is unavailable";
    public bool NeedsModel => false;
    public FakeSession? Session { get; private set; }
    public int Starts { get; private set; }
    public Exception? StartError { get; set; }

    public IReadOnlyList<string> GetLocales() => ["en-US", "de-DE"];

    public Task<IRecognitionSession> StartAsync(RecognitionOptions options, CancellationToken ct)
    {
        Starts++;
        if (StartError is not null) throw StartError;
        Session = new FakeSession(options);
        return Task.FromResult<IRecognitionSession>(Session);
    }
}

internal sealed class FakeSession(RecognitionOptions options) : IRecognitionSession
{
    public RecognitionOptions Options { get; private set; } = options;
    public ConcurrentQueue<float> Samples { get; } = new();
    public bool Disposed { get; private set; }
    public int SampleRate => 16000;
    public string Description => "Fake recogniser";
    public event Action<RecognitionResult>? Recognized;
    public event Action<Exception>? Failed;

    public void Accept(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) Samples.Enqueue(s);
    }

    public void UpdateOptions(RecognitionOptions options) => Options = options;

    public void Hear(string text, double confidence = 0.9, bool isFinal = true, long id = 1) =>
        Recognized?.Invoke(new RecognitionResult(id, text, confidence, isFinal));

    public void Fail(Exception ex) => Failed?.Invoke(ex);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Transcriber that answers from a queue (or a function of the clip).</summary>
internal sealed class FakeTranscriber : IUtteranceTranscriber
{
    public ConcurrentQueue<(float[] Samples, RecognitionOptions Options)> Calls { get; } = new();
    public Func<float[], Transcription?> Answer { get; set; } = s => new Transcription($"clip of {s.Length}", 0.8);
    public TaskCompletionSource? Gate { get; set; }
    public bool Disposed { get; private set; }
    public string Description => "Fake transcriber";

    public async Task<Transcription?> TranscribeAsync(float[] samples, RecognitionOptions options, CancellationToken ct)
    {
        Calls.Enqueue((samples, options));
        if (Gate is { } gate) await gate.Task.WaitAsync(ct);
        return Answer(samples);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Answers every request with fixed bytes (or a status code) without touching the network.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static FakeHttpHandler Bytes(byte[] body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
}

internal static class Signals
{
    public static float[] Silence(int samples, float noise = 0.0005f, int seed = 1)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, samples).Select(_ => (float)((random.NextDouble() * 2 - 1) * noise)).ToArray();
    }

    public static float[] Tone(int samples, int rate = 16000, double hz = 220, float amplitude = 0.3f) =>
        Enumerable.Range(0, samples).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}
