using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>Moderation tab: user actions, chat modes, shield mode, clear chat and announcements.</summary>
public sealed partial class TwitchModerationViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    public static IReadOnlyList<string> Colors => AnnouncementColors.All;

    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private decimal _timeoutSeconds = 600;

    [ObservableProperty] private bool _emoteMode;
    [ObservableProperty] private bool _followerMode;
    [ObservableProperty] private decimal _followerMinutes;
    [ObservableProperty] private bool _slowMode;
    [ObservableProperty] private decimal _slowSeconds = 30;
    [ObservableProperty] private bool _subscriberMode;
    [ObservableProperty] private bool _uniqueChatMode;
    [ObservableProperty] private bool _shieldMode;

    [ObservableProperty] private string _announcement = "";
    [ObservableProperty] private string _announcementColor = "primary";

    private Task OnUser(Func<string, Task> action, string done) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(User)) throw new InvalidOperationException("Enter a user name first.");
        await action(await Api.ResolveUserIdAsync(User));
    }, done);

    [RelayCommand] private Task Ban() => OnUser(id => Api.BanAsync(id, Reason), $"{User} banned.");
    [RelayCommand] private Task Timeout() => OnUser(id => Api.TimeoutAsync(id, (int)TimeoutSeconds, Reason), $"{User} timed out.");
    [RelayCommand] private Task Unban() => OnUser(id => Api.UnbanAsync(id), $"{User} unbanned.");
    [RelayCommand] private Task Warn() => OnUser(id => Api.WarnAsync(id, Reason), $"{User} warned.");
    [RelayCommand] private Task AddVip() => OnUser(id => Api.AddVipAsync(id), $"{User} is now a VIP.");
    [RelayCommand] private Task RemoveVip() => OnUser(id => Api.RemoveVipAsync(id), $"{User} is no longer a VIP.");
    [RelayCommand] private Task AddModerator() => OnUser(id => Api.AddModeratorAsync(id), $"{User} is now a moderator.");
    [RelayCommand] private Task RemoveModerator() => OnUser(id => Api.RemoveModeratorAsync(id), $"{User} is no longer a moderator.");

    [RelayCommand]
    private Task LoadChatSettings() => Run(async () =>
    {
        Show(await Api.GetChatSettingsAsync());
        try
        {
            ShieldMode = (await Api.GetShieldModeAsync()).IsActive;
        }
        catch (TwitchApiException)
        {
            // Shield mode needs its own scope; the chat modes are still useful without it.
        }
    }, "Chat settings loaded.");

    [RelayCommand]
    private Task ApplyChatSettings() => Run(async () => Show(await Api.UpdateChatSettingsAsync(new ChatSettingsUpdate
    {
        EmoteMode = EmoteMode,
        FollowerMode = FollowerMode,
        FollowerModeDuration = FollowerMode ? (int)FollowerMinutes : null,
        SlowMode = SlowMode,
        SlowModeWaitTime = SlowMode ? (int)SlowSeconds : null,
        SubscriberMode = SubscriberMode,
        UniqueChatMode = UniqueChatMode,
    })), "Chat settings updated.");

    private void Show(TwitchChatSettings s)
    {
        EmoteMode = s.EmoteMode;
        FollowerMode = s.FollowerMode;
        FollowerMinutes = s.FollowerModeDuration;
        SlowMode = s.SlowMode;
        if (s.SlowModeWaitTime > 0) SlowSeconds = s.SlowModeWaitTime;
        SubscriberMode = s.SubscriberMode;
        UniqueChatMode = s.UniqueChatMode;
    }

    [RelayCommand]
    private Task ApplyShieldMode() => Run(async () => ShieldMode = (await Api.SetShieldModeAsync(ShieldMode)).IsActive,
        "Shield mode updated.");

    [RelayCommand]
    private Task ClearChat() => Run(() => Api.ClearChatAsync(), "Chat cleared.");

    [RelayCommand]
    private Task SendAnnouncement() => Run(async () =>
    {
        await Api.SendAnnouncementAsync(Announcement, AnnouncementColor);
        Announcement = "";
    }, "Announcement sent.");
}
