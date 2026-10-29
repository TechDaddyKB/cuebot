namespace Cuebot.StreamDeck.Hid;

/// <summary>
/// One open Stream Deck: the protocol on top of an <see cref="IHidDevice"/>. Writes (images, feature reports) are
/// serialized so the pages of two images never interleave; input is read on the caller's (reader) thread.
/// </summary>
public sealed class StreamDeckHidDeck : IDisposable
{
    private readonly IHidDevice _device;
    private readonly Lock _writeGate = new();
    private readonly byte[] _input;

    public StreamDeckHidDeck(IHidDevice device, StreamDeckHidModel model)
    {
        if (!model.Supported) throw new NotSupportedException($"{model.Name} is not supported in direct USB mode yet.");
        _device = device;
        Model = model;
        _input = new byte[Math.Max(model.InputLength, 64)];
    }

    public StreamDeckHidModel Model { get; }

    public string ReadSerial() => StreamDeckHidProtocol.ParseSerial(Model, _device.GetFeature(StreamDeckHidProtocol.FeatureSerialId, Model.FeatureReportLength));

    public string ReadFirmware() => StreamDeckHidProtocol.ParseFirmware(Model, _device.GetFeature(StreamDeckHidProtocol.FeatureFirmwareId, Model.FeatureReportLength));

    public void SetBrightness(int percent)
    {
        lock (_writeGate) _device.SetFeature(StreamDeckHidProtocol.BrightnessReport(Model, percent));
    }

    /// <summary>Clears every key (the deck shows its own boot screen until an image arrives).</summary>
    public void Reset()
    {
        lock (_writeGate) _device.SetFeature(StreamDeckHidProtocol.ResetReport(Model));
    }

    /// <summary>Drops a half-sent image (e.g. after another program was interrupted mid-image).</summary>
    public void ResetKeyStream()
    {
        if (!Model.HasDisplay) return;
        lock (_writeGate) _device.Write(StreamDeckHidProtocol.ResetKeyStreamReport(Model));
    }

    /// <summary>Sends an already-encoded key image (see <see cref="StreamDeckKeyRenderer"/>).</summary>
    public void SetKeyImage(int key, ReadOnlySpan<byte> encoded)
    {
        if (!Model.HasDisplay || key >= Model.ImageKeyCount) return;
        var reports = StreamDeckHidProtocol.ImageReports(Model, key, encoded);
        lock (_writeGate)
        {
            foreach (var report in reports) _device.Write(report);
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for input; null on timeout or an unknown report.</summary>
    /// <exception cref="HidDisconnectedException">The deck is gone.</exception>
    public StreamDeckHidInput? ReadInput(int timeoutMs)
    {
        var n = _device.Read(_input, timeoutMs);
        return n <= 0 ? null : StreamDeckHidProtocol.ParseInput(Model, _input.AsSpan(0, n));
    }

    public void Dispose() => _device.Dispose();
}
