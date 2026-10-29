using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Server.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task Servers_register_without_dependency_cycles()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuebot-servers-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ServiceCollection().AddCuebotCore(new AppPaths(root)).AddScripting().AddServers().BuildServiceProvider();
            await using (provider)
            {
                var registry = provider.GetRequiredService<Registry>();
                Assert.NotNull(registry.GetTrigger("custom.codeEvent"));
                Assert.NotNull(registry.GetTrigger("websocket.serverMessage"));
                Assert.NotNull(registry.GetSubAction("websocket.broadcast"));
                Assert.NotNull(provider.GetRequiredService<ActionEngine>());
                Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "websocket.clientSend");
                var manager = provider.GetRequiredService<ServerManager>();
                Assert.False(manager.Settings.WebSocket.Enabled);
                Assert.False(manager.Settings.Api.Enabled);
                Assert.True(manager.Settings.Mcp.Enabled);   // a new servers.json starts MCP (version 2)
                Assert.Equal(ServerSettings.CurrentVersion, manager.Settings.Version);
                var services = provider.GetRequiredService<ServerServices>();
                Assert.Same(registry, services.Registry);
                Assert.NotNull(services.Timers);
                Assert.NotNull(services.Transfer);
                Assert.NotNull(services.Scripts);
                Assert.True(manager.ApiOperations.ListSubActions()["count"]!.GetValue<int>() > 10);
                Assert.NotNull(provider.GetRequiredService<CSharpScriptEngine>());
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
