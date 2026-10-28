using System.Text.RegularExpressions;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;
using Cuebot.Scripting.Compat;
using Xunit.Abstractions;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Compiles every C# step imported from a folder of real Streamer.bot export strings (*.sb): CUEBOT_SB_SAMPLES,
/// else C:\Users\docker\cuebot-sb-exports\samples. Skipped without one; the samples are third-party content and
/// are never committed.
/// </summary>
public sealed class StreamerBotSampleCodeTests(ITestOutputHelper output)
{
    private const string DefaultFolder = @"C:\Users\docker\cuebot-sb-exports\samples";

    private static string? SamplesFolder()
    {
        var folder = Environment.GetEnvironmentVariable("CUEBOT_SB_SAMPLES");
        if (string.IsNullOrWhiteSpace(folder)) folder = DefaultFolder;
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.sb").Any() ? folder : null;
    }

    [Fact]
    public void Every_imported_csharp_step_compiles()
    {
        if (SamplesFolder() is not { } folder) return; // no samples on this machine

        using var inline = new CphInlineHost();
        var compiler = new CSharpScriptCompiler();
        int steps = 0;
        var failures = new List<string>();
        var expected = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.sb").Order(StringComparer.Ordinal))
        {
            DecodedPayload payload;
            try
            {
                payload = TransferCodec.Decode(File.ReadAllText(file));
            }
            catch (TransferFormatException)
            {
                continue; // a truncated download
            }
            var (bundle, _) = new StreamerBotImporter().Convert(payload.Json);
            foreach (var action in bundle.Actions)
            foreach (var step in TransferReferences.Walk(action.SubActions).Where(s => s.Type == CSharpCodeSubAction.Id))
            {
                var code = step.Parameters.GetValueOrDefault("code") ?? "";
                steps++;
                try
                {
                    if (CphInlineHost.IsInlineClass(code)) inline.Get(code, CSharpCodeSubAction.References(step));
                    else compiler.GetRunner(code, CSharpCodeSubAction.References(step));
                }
                catch (ScriptException ex)
                {
                    var line = $"{Path.GetFileName(file)} / {action.Name}:\n    {ex.Message.Replace("\n", "\n    ")}";
                    if (ExpectedFailure(step, code) is { } reason) expected.Add($"{reason}: {line}");
                    else failures.Add(line);
                }
            }
        }

        output.WriteLine($"C# steps: {steps}, compile failures: {failures.Count}, expected failures: {expected.Count}");
        foreach (var failure in failures) output.WriteLine(failure);
        foreach (var failure in expected) output.WriteLine("EXPECTED " + failure);
        Assert.Empty(failures);
    }

    /// <summary>Why a step cannot compile in any Cuebot, or null when it should.</summary>
    private static string? ExpectedFailure(SubActionDefinition step, string code)
    {
        // The importer leaves a "# X.dll (was ...)" line for a DLL from the user's Streamer.bot dlls folder.
        if (step.Parameters.GetValueOrDefault("references") is { } references && references.Contains("# ", StringComparison.Ordinal))
            return "needs the user's own DLL";
        if (code.Contains("System.Windows.Forms", StringComparison.Ordinal)) return "uses Windows Forms";
        // `using` directives pasted inside Execute(): broken in Streamer.bot too.
        if (Regex.IsMatch(code, @"Execute\(\)\s*\{[^}]*?^\s*using [\w.]+;", RegexOptions.Multiline)) return "broken in the export";
        return null;
    }
}
