using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Xunit.Abstractions;

namespace Cuebot.Server.Tests;

/// <summary>
/// The official <c>@streamerbot/client</c> against a Cuebot server started here: <c>NodeClient/streamerbot-client-check.mjs</c>
/// connects with a password, subscribes with <c>client.on()</c>, receives every documented event and calls every
/// request the client has, checking the responses and Twitch payloads against the client's TypeScript types.
/// Needs node (on PATH or CUEBOT_TEST_NODE) and CUEBOT_TEST_SBCLIENT: a folder where
/// <c>npm install @streamerbot/client typescript</c> was run.
/// </summary>
public sealed class StreamerBotClientNodeTests(ITestOutputHelper output)
{
    private const string Password = "client-test-password";
    private const string ResponseAction = "Client response";

    /// <summary>Answers the client's customEventResponse: fires the custom event it waits for, with the run's args.</summary>
    private const string RespondCode = """
        public class CPHInline
        {
            public bool Execute()
            {
                if (args.TryGetValue("sbClientResponse", out var id)) CPH.TriggerEvent(id.ToString(), true);
                return true;
            }
        }
        """;

    [NodeClientFact]
    public async Task Official_client_receives_events_and_calls_every_request()
    {
        var samples = StreamerBotDocumentedShapeTests.Samples;
        var settings = new ServerSettings
        {
            WebSocket = new StreamerBotWebSocketSettings
            {
                Enabled = true, Port = 0, AuthenticationEnabled = true, Password = Password, EnforceAuthentication = true,
            },
        };
        var options = StreamerBotFixtures.Options(StreamerBotFixtures.Triggers([.. samples.Values.Select(s => s.Type).Distinct()]));
        await using var bot = await StreamerBotDocumentedShapeTests.PreparedBotAsync(settings, options);
        bot.AddAction(ResponseAction, RespondCode);
        bot.AddAction("On channel created", RespondCode, new TriggerDefinition
        {
            Type = CodeTriggerRegistry.TriggerTypeId, Settings = { ["eventName"] = "channel_created" },
        });
        await bot.Manager.StartAsync();

        var config = Path.Combine(Path.GetTempPath(), $"cuebot-sbclient-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            clientDir = NodeClient.ClientDir,
            port = bot.Manager.WebSocket.Port,
            password = Password,
            responseAction = ResponseAction,
            events = samples.Keys.Order(StringComparer.Ordinal).ToArray(),
        }));
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "NodeClient", "streamerbot-client-check.mjs");
            using var node = Process.Start(new ProcessStartInfo(NodeClient.Executable!, [script, config])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            })!;
            var stderr = node.StandardError.ReadToEndAsync();
            string? result = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline && await node.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line == "READY")
                {
                    foreach (var (_, (type, args)) in samples) bot.Bus.Publish(new BotEvent(type, args));
                }
                else if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                {
                    result = line["RESULT ".Length..];
                }
            }
            if (!node.WaitForExit(30_000)) node.Kill(entireProcessTree: true);
            Assert.True(result is not null, "The node script gave no result:\n" + await stderr);

            var report = JsonNode.Parse(result)!;
            foreach (var line in report["checked"]!.AsArray()) output.WriteLine(line!.GetValue<string>());
            var failures = report["failures"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray();
            Assert.True(failures.Length == 0, string.Join("\n", failures));
            Assert.Equal(samples.Count, report["received"]!.AsArray().Count);
            Assert.Contains(report["checked"]!.AsArray(), c => c!.GetValue<string>().StartsWith("Twitch.ChatMessage.data", StringComparison.Ordinal));
            Assert.Equal(0, node.ExitCode);

            // SendMessage's options reached the chat sender.
            var (text, sent) = Assert.Single(bot.Sender.SentWithOptions);
            Assert.Equal("hello from node", text);
            Assert.True(sent.Bot);
            Assert.False(sent.Internal);
            Assert.Equal("m1", sent.ReplyTo);
        }
        finally
        {
            File.Delete(config);
        }
    }
}

/// <summary>Finds node and the prepared client folder for <see cref="StreamerBotClientNodeTests"/>.</summary>
internal static class NodeClient
{
    public static string? ClientDir { get; } = FindClientDir();
    public static string? Executable { get; } = ClientDir is null ? null : FindNode();

    private static string? FindClientDir()
    {
        var dir = Environment.GetEnvironmentVariable("CUEBOT_TEST_SBCLIENT");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var modules = Path.Combine(dir, "node_modules");
        return File.Exists(Path.Combine(modules, "@streamerbot", "client", "dist", "index.js"))
               && File.Exists(Path.Combine(modules, "typescript", "package.json"))
            ? Path.GetFullPath(dir)
            : null;
    }

    private static string? FindNode()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("CUEBOT_TEST_NODE"), "node"];
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (process is null) continue;
                var version = process.StandardOutput.ReadToEnd().Trim();
                if (process.WaitForExit(10_000) && process.ExitCode == 0 && version.StartsWith('v')) return candidate;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed under that name.
            }
        }
        return null;
    }
}

/// <summary>A fact that is skipped unless node and a folder with the official client (CUEBOT_TEST_SBCLIENT) are available.</summary>
internal sealed class NodeClientFactAttribute : FactAttribute
{
    public NodeClientFactAttribute()
    {
        if (NodeClient.ClientDir is null)
            Skip = "Set CUEBOT_TEST_SBCLIENT to a folder where `npm install @streamerbot/client typescript` was run";
        else if (NodeClient.Executable is null)
            Skip = "No node found (put node on PATH or set CUEBOT_TEST_NODE)";
    }
}
