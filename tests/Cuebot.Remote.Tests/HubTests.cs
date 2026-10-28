using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Scripting;
using Cuebot.Server;
using Cuebot.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Remote.Tests;

public class HubTests
{
    [Fact]
    public async Task Module_registers_with_core_and_servers()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuebot-remote-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ServiceCollection().AddCuebotCore(new AppPaths(root)).AddScripting().AddServers().AddRemote().BuildServiceProvider();
            await using (provider)
            {
                var registry = provider.GetRequiredService<Registry>();
                Assert.NotNull(registry.GetTrigger(RemoteHub.SignalEvent));
                Assert.NotNull(registry.GetTrigger(RemoteHub.ConnectedEvent));
                Assert.NotNull(registry.GetTrigger(RemoteHub.DisconnectedEvent));
                Assert.NotNull(registry.GetSubAction(SendRemoteSignalSubAction.Id));
                Assert.NotNull(registry.GetSubAction(WaitForRemoteSignalSubAction.Id));
                var hub = provider.GetRequiredService<RemoteHub>();
                Assert.Contains(provider.GetServices<IApiWebSocketRoute>(), r => r.Name == "remote");
                Assert.Contains(provider.GetServices<IConnectionStatus>(), s => ReferenceEquals(s, hub));
                Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "remote.sendSignal");
                Assert.NotEqual(Guid.Empty, hub.Settings.InstanceId);
                Assert.Equal(Environment.MachineName, hub.SelfName);
                Assert.Contains("Not set up", hub.StatusMessage);   // REST API off, nothing configured
                provider.GetRequiredService<JsonFile<RemoteSettings>>().Flush();
                Assert.True(File.Exists(Path.Combine(root, "remote.json")));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task StartAsync_connects_enabled_instances_only()
    {
        var b = await Instance.StartAsync("Bravo");
        var a = await Instance.StartAsync("Alpha");
        await using (a)
        await using (b)
        {
            a.Hub.Settings.Instances.Add(new RemoteInstanceDefinition { Name = "on", Port = b.Port, Token = b.Token });
            a.Hub.Settings.Instances.Add(new RemoteInstanceDefinition { Name = "off", Port = b.Port, Token = b.Token, Enabled = false });
            await a.Hub.StartAsync();
            var statuses = a.Hub.Instances;
            Assert.Equal(ConnectionState.Connected, statuses.Single(s => s.Name == "on").State);
            Assert.Equal(ConnectionState.Disconnected, statuses.Single(s => s.Name == "off").State);
            Assert.Equal(["off", "on"], a.Hub.KnownNames.Where(n => n is "on" or "off"));
        }
    }

    [Fact]
    public async Task Script_functions_send_wait_and_list()
    {
        var (a, b) = (await Instance.StartAsync("Alpha"), await Instance.StartAsync("Bravo"));
        await using (a)
        await using (b)
        {
            await a.LinkToAsync(b, "studio");
            await Eventually.TrueAsync(() => b.Hub.Instances.Count == 1, "linked");
            var fa = new ScriptApi([new RemoteScriptFunctions(a.Hub)]);
            var fb = new ScriptApi([new RemoteScriptFunctions(b.Hub)]);

            var wait = fb.CallAsync(null!, "remote.waitForSignal", new { name = "ping", timeoutMs = 10000 }, CancellationToken.None);
            await Task.Delay(100);
            Assert.Equal(1, await fa.CallAsync(null!, "remote.sendSignal", new { name = "ping", instance = "studio", args = new { n = 5, s = "x" } }, CancellationToken.None));
            var result = JsonSerializer.SerializeToElement(await wait);
            Assert.Equal("Alpha", result.GetProperty("instanceName").GetString());
            Assert.Equal(5, result.GetProperty("args").GetProperty("n").GetInt32());

            Assert.Null(await fb.CallAsync(null!, "remote.waitForSignal", new { name = "never", timeoutMs = 50 }, CancellationToken.None));
            await Assert.ThrowsAsync<ScriptException>(() => fa.CallAsync(null!, "remote.sendSignal", new { name = " " }, CancellationToken.None));

            var list = JsonSerializer.SerializeToElement(await fa.CallAsync(null!, "remote.getInstances", new { }, CancellationToken.None));
            Assert.Equal("Connected", list[0].GetProperty("state").GetString());
            Assert.Equal("Bravo", list[0].GetProperty("instanceName").GetString());
            Assert.DoesNotContain(b.Token, list.GetRawText());
        }
    }

    [Fact]
    public async Task Received_signals_release_local_waiters_only_when_enabled()
    {
        var (a, b) = (await Instance.StartAsync("Alpha"), await Instance.StartAsync("Bravo"));
        await using (a)
        await using (b)
        {
            await a.LinkToAsync(b, "studio");
            await Eventually.TrueAsync(() => b.Hub.Instances.Count == 1, "linked");
            b.Hub.Settings.ReleaseLocalWaiters = false;
            var local = b.Signals.WaitAsync("x", TimeSpan.FromMilliseconds(500), CancellationToken.None);
            var remote = b.Hub.WaitForSignalAsync("x", null, TimeSpan.FromSeconds(10), CancellationToken.None);
            await a.Hub.SendSignalAsync("", "x", "", null);
            Assert.NotNull(await remote);
            Assert.Null(await local);
        }
    }
}
