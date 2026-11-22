using Cuebot.App;
using Cuebot.App.ViewModels.Chat;
using Cuebot.Core;

namespace Cuebot.UiTests.Chat;

public sealed class ChatSettingsShutdownTests
{
    [Fact]
    public async Task A_chat_setting_changed_just_before_closing_is_saved()
    {
        var dataDir = Directory.CreateTempSubdirectory("cuebot-ui-chat-close-").FullName;
        try
        {
            var paths = new AppPaths(dataDir);
            var host = AppHost.Create(paths);
            var settings = ChatPageServices.From(host).Settings!;
            settings.Value.TextSize = 21;
            settings.ScheduleSave();

            await host.DisposeAsync();   // well inside the save delay

            Assert.Contains("\"textSize\": 21", File.ReadAllText(paths.File("chat-page.json")), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
