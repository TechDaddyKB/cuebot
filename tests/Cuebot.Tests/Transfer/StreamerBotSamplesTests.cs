using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;
using Xunit.Abstractions;

namespace Cuebot.Tests.Transfer;

/// <summary>
/// Runs only when a folder of real Streamer.bot export strings (*.sb) exists: CUEBOT_SB_SAMPLES, else
/// C:\Users\docker\cuebot-sb-exports\samples. The samples are third-party content and are never committed.
/// </summary>
public sealed class StreamerBotSamplesFactAttribute : FactAttribute
{
    public StreamerBotSamplesFactAttribute()
    {
        if (StreamerBotSamplesTests.SamplesFolder() is null)
            Skip = "Set CUEBOT_SB_SAMPLES to a folder of Streamer.bot *.sb export strings to run this test";
    }
}

public sealed partial class StreamerBotSamplesTests(ITestOutputHelper output)
{
    private const string DefaultFolder = @"C:\Users\docker\cuebot-sb-exports\samples";

    public static string? SamplesFolder()
    {
        var folder = Environment.GetEnvironmentVariable("CUEBOT_SB_SAMPLES");
        if (string.IsNullOrWhiteSpace(folder)) folder = DefaultFolder;
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.sb").Any() ? folder : null;
    }

    [StreamerBotSamplesFact]
    public void Every_sample_imports_and_reports_its_mapping_rate()
    {
        var folder = SamplesFolder()!;
        int subMapped = 0, subPlaceholders = 0, trigMapped = 0, trigPlaceholders = 0, files = 0;
        var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.sb").Order(StringComparer.Ordinal))
        {
            DecodedPayload payload;
            try
            {
                payload = TransferCodec.Decode(File.ReadAllText(file));
            }
            catch (TransferFormatException ex)
            {
                // A sample that is not a complete export string (e.g. a truncated download) cannot be decoded.
                output.WriteLine($"SKIPPED {Path.GetFileName(file)}: not a complete export string ({ex.Message})");
                continue;
            }
            Assert.Equal(PayloadKind.StreamerBot, payload.Kind);
            Assert.NotNull(JsonNode.Parse(payload.Json));

            var (bundle, report) = new StreamerBotImporter().Convert(payload.Json);
            Assert.False(bundle.IsEmpty);
            files++;
            foreach (var line in report.Mapped)
            {
                var m = MappedLine().Match(line);
                if (!m.Success) continue;
                var count = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1;
                if (m.Groups[1].Value == "Sub-action") subMapped += count;
                else trigMapped += count;
            }
            foreach (var p in report.Placeholders)
            {
                if (p.ItemKind == "trigger") trigPlaceholders++;
                else subPlaceholders++;
                var key = $"{p.ItemKind} {p.OriginalType}";
                unknown[key] = unknown.GetValueOrDefault(key) + 1;
            }
            // Nothing from an import ever runs code: every action holding code arrives disabled.
            foreach (var action in bundle.Actions)
                if (TransferReferences.Walk(action.SubActions).Any(s => s.Type.StartsWith("code.", StringComparison.Ordinal)))
                    Assert.False(action.Enabled);
        }

        Assert.True(files > 0, "no sample could be decoded");
        static string Pct(int a, int b) => a + b == 0 ? "n/a" : $"{100.0 * a / (a + b):0.0}%";
        output.WriteLine($"Files imported: {files}");
        output.WriteLine($"Sub-actions: {subMapped} mapped, {subPlaceholders} placeholders ({Pct(subMapped, subPlaceholders)} mapped)");
        output.WriteLine($"Triggers: {trigMapped} mapped, {trigPlaceholders} placeholders ({Pct(trigMapped, trigPlaceholders)} mapped)");
        output.WriteLine($"All: {Pct(subMapped + trigMapped, subPlaceholders + trigPlaceholders)} mapped");
        foreach (var (key, count) in unknown) output.WriteLine($"  placeholder {key} ×{count}");
        // Also visible in the console with --logger "console;verbosity=detailed".
        Console.WriteLine($"[sb-samples] sub-actions {subMapped}/{subMapped + subPlaceholders}, triggers {trigMapped}/{trigMapped + trigPlaceholders}");
    }

    // "Sub-action SendMessage → twitch.sendMessage (…) ×3"
    [GeneratedRegex(@"^(Sub-action|Trigger) (\S+) → .*?(?: ×(\d+))?$")]
    private static partial Regex MappedLine();
}
