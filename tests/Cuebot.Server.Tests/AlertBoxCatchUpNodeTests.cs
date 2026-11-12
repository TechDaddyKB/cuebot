using System.Diagnostics;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Server.Tests;

/// <summary>
/// Cuebot's alert box widget (its real widget.js, run in node with a tiny DOM stand-in): catch-up alerts are skipped and
/// release the queue unless the box's "show catch-up alerts" setting is on; live ones play.
/// </summary>
public sealed class AlertBoxCatchUpNodeTests
{
    private const string Harness = """
        const fs = require("fs");
        const [widgetPath, alertsJson] = process.argv.slice(2);
        const listeners = {};
        const shown = [];
        let released = 0;
        function el(tag) {
          return {
            tagName: String(tag).toUpperCase(), style: { setProperty() {} }, children: [], parentNode: null, offsetWidth: 0,
            appendChild(c) { this.children.push(c); c.parentNode = this; return c; }, removeChild() {}, setAttribute() {},
            play() { return Promise.resolve(); }, pause() {}
          };
        }
        const root = el("div");
        root.appendChild = function (c) { shown.push(c); c.parentNode = root; return c; };
        globalThis.window = {
          addEventListener(name, f) { listeners[name] = f; },
          SE_API: { resumeQueue() { released++; }, sendMessage() { return Promise.resolve(); } }
        };
        globalThis.SE_API = globalThis.window.SE_API;
        globalThis.document = { createElement: el, getElementById: () => root, head: el("head") };
        globalThis.setTimeout = () => 0;
        globalThis.clearTimeout = () => {};
        eval(fs.readFileSync(widgetPath, "utf8"));
        const results = [];
        for (const show of [false, true]) {
          const alerts = JSON.parse(alertsJson);
          alerts.showCatchUp = show;
          listeners.onWidgetLoad({ detail: { fieldData: { alerts }, overlay: { muted: true } } });
          shown.length = 0; released = 0;
          const tip = (catchUp) => ({ detail: { listener: "tip-latest", event: { name: "Kris", amount: 5, currency: "USD", isCatchUp: catchUp } } });
          listeners.onEventReceived(tip(true));
          const afterCatchUp = { shown: shown.length, released };
          listeners.onEventReceived(tip(false));
          results.push({ show, afterCatchUp, afterLive: { shown: shown.length, released } });
        }
        console.log(JSON.stringify(results));
        """;

    [NodeFact]
    public async Task Catch_up_alerts_only_pop_up_when_the_box_shows_them()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-alertbox-node-").FullName;
        try
        {
            var code = BuiltInWidgets.AlertBox();
            var widget = Path.Combine(dir, "widget.js");
            var harness = Path.Combine(dir, "harness.js");
            await File.WriteAllTextAsync(widget, code.Js);
            await File.WriteAllTextAsync(harness, Harness);
            var alerts = JsonNode.Parse(code.Fields)!["alerts"]!["value"]!;
            Assert.False(alerts["showCatchUp"]!.GetValue<bool>());   // off by default

            var start = new ProcessStartInfo(NodeClient.Node!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { harness, widget, alerts.ToJsonString() }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);

            var results = JsonNode.Parse(output)!.AsArray();
            // Off: the catch-up tip is skipped and releases the queue; the live one plays.
            Assert.Equal((0, 1), (results[0]!["afterCatchUp"]!["shown"]!.GetValue<int>(), results[0]!["afterCatchUp"]!["released"]!.GetValue<int>()));
            Assert.Equal(1, results[0]!["afterLive"]!["shown"]!.GetValue<int>());
            // On: both play.
            Assert.Equal((1, 2), (results[1]!["afterCatchUp"]!["shown"]!.GetValue<int>(), results[1]!["afterLive"]!["shown"]!.GetValue<int>()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
