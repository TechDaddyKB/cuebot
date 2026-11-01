using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Integrations;
using Cuebot.App.Views.Integrations;
using Cuebot.Discord;
using Cuebot.Discord.Tests.Support;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Integrations;

/// <summary>
/// The Discord bot tab: the token is masked and saved, switches and slash commands write through, Invite builds the
/// OAuth2 link, Refresh lists servers from a fake REST API, Register sends the commands, and the page renders without
/// binding errors in both themes (screenshots tests/Cuebot.UiTests/screenshots/discord-bot-*.png).
/// </summary>
public sealed class DiscordBotViewTests
{
    [AvaloniaFact]
    public async Task Sets_up_the_bot_lists_servers_and_registers_commands_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        await using var env = new TestEnvironment(withToken: false);
        env.Http.Handler = r => r.Path switch
        {
            "users/@me/guilds" => FakeDiscordHttp.Json(new[] { new { id = Ids.Guild, name = "My Server" } }),
            var p when p == $"guilds/{Ids.Guild}/channels" => FakeDiscordHttp.Json(new object[]
            {
                new { id = Ids.General, name = "general", type = 0, position = 0 },
                new { id = Ids.Announcements, name = "going-live", type = 5, position = 1 },
            }),
            var p when p == $"guilds/{Ids.Guild}/roles" => FakeDiscordHttp.Json(new object[]
            {
                new { id = Ids.Guild, name = "@everyone", position = 0, managed = false },
                new { id = Ids.LiveRole, name = "Live notifications", position = 1, managed = false },
            }),
            _ => null,
        };
        var opened = new List<string>();
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new DiscordBotViewModel(env.Bot) { OpenUrl = opened.Add };
            var window = new Window
            {
                Content = new DiscordBotView { DataContext = vm },
                Width = 1000,
                Height = 1900,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();
            Assert.True(vm.HasNoServers);
            Assert.True(vm.HasNoCommands);
            Assert.Equal("No bot token saved", vm.MaskedToken);

            vm.BotToken = "Bot " + TestToken.Value + " ";
            Assert.Equal(TestToken.Value, env.Settings.Value.BotToken);
            Assert.DoesNotContain(TestToken.Value, vm.MaskedToken, StringComparison.Ordinal);
            vm.MessageContent = true;
            vm.ServerMembers = true;
            Assert.True(env.Settings.Value.MessageContentIntent && env.Settings.Value.ServerMembersIntent);

            await vm.InviteCommand.ExecuteAsync(null);
            Assert.Equal(DiscordBotService.InviteUrl("100000000000000001"), Assert.Single(opened));
            vm.OpenPortalCommand.Execute(null);
            Assert.Equal(DiscordBotViewModel.PortalUrl, opened[^1]);

            await vm.RefreshCommand.ExecuteAsync(null);
            UiPump.Pump();
            Assert.Equal("", vm.Error);
            var server = Assert.Single(vm.Servers);
            Assert.Equal("#general   #going-live", server.Channels);
            Assert.Equal("Live notifications", server.Roles);

            vm.AddCommandCommand.Execute(null);
            vm.Commands[0].Name = "/Socials";
            vm.Commands[0].Description = "My socials";
            vm.AddCommandCommand.Execute(null);
            vm.Commands[1].Name = "notify";
            vm.Commands[1].Private = true;
            Assert.Equal("socials", env.Settings.Value.Commands[0].Name);
            Assert.True(env.Settings.Value.Commands[1].Private);
            await vm.RegisterCommand.ExecuteAsync(null);
            Assert.Equal("", vm.Error);
            Assert.Contains("Registered 2 slash command(s) in 1 server(s)", vm.Info);
            Assert.Single(env.Http.Matching(HttpMethod.Put, $"applications/100000000000000001/guilds/{Ids.Guild}/commands"));

            UiPump.Pump();
            Assert.Equal(1, window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "DiscordServers").ItemCount);
            Assert.Equal(2, window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "DiscordCommands").ItemCount);
            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"discord-bot-{name}");
            }

            vm.Commands[1].RemoveCommand.Execute(null);
            Assert.Single(env.Settings.Value.Commands);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }
}
