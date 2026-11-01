using Cuebot.Core;
using Cuebot.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Discord.Tests;

public sealed class ModuleTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-discord-di-").FullName;
    private readonly ServiceProvider _services;

    public ModuleTests() => _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddDiscordBot().BuildServiceProvider();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Registers_the_service_status_triggers_and_every_sub_action()
    {
        Assert.Contains(_services.GetServices<IConnectionStatus>(), s => s.Name == DiscordBotService.ServiceName);
        var registry = _services.GetRequiredService<Registry>();
        var set = _services.GetRequiredService<DiscordServiceCollectionExtensions.SubActionSet>();
        Assert.Equal(set.All.Select(h => h.TypeId).Order(), DiscordSubActions.TypeIds.Order());
        foreach (var id in DiscordSubActions.TypeIds) Assert.Equal("Discord", registry.GetSubAction(id)!.Category);
        foreach (var id in new[] { DiscordEventTypes.SlashCommand, DiscordEventTypes.MessagePosted, DiscordEventTypes.MemberJoined,
                     DiscordEventTypes.Connected, DiscordEventTypes.Disconnected })
            Assert.NotNull(registry.GetTrigger(id));

        // Off by default: StartAsync never connects.
        var bot = _services.GetRequiredService<DiscordBotService>();
        Assert.False(bot.Settings.AutoConnect);
        Assert.False(bot.Settings.MessageContentIntent);
        Assert.False(bot.Settings.ServerMembersIntent);
        await bot.StartAsync();
        Assert.False(bot.IsRunning);
    }
}
