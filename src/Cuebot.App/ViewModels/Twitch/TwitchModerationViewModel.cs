using System.ComponentModel;
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

    /// <summary>Ban asks for a second click: a ban is public and lasts until you unban.</summary>
    public ConfirmClick BanConfirm { get; } = new("Ban", "Click again to ban");

    /// <summary>Clear chat asks for a second click: the messages are gone for everyone.</summary>
    public ConfirmClick ClearChatConfirm { get; } = new("Clear chat", "Click again to clear chat");

    [RelayCommand]
    private Task Ban()
    {
        var name = User.Trim().TrimStart('@');
        if (name.Length > 0 && !BanConfirm.Confirm(name.ToLowerInvariant()))
        {
            Error = "";
            Info = $"Click again within 5 seconds to ban {name}.";
            return Task.CompletedTask;
        }
        return OnUser(id => Api.BanAsync(id, Reason), $"{User} banned.");
    }

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

    private ChatModes _sent;

    /// <summary>The chat mode ticks and numbers as one value, to see whether they differ from what Twitch has.</summary>
    private readonly record struct ChatModes(bool Emote, bool Follower, int FollowerMinutes, bool Slow, int SlowSeconds, bool Subscriber, bool Unique);

    private ChatModes Current => new(EmoteMode, FollowerMode, FollowerMode ? (int)FollowerMinutes : 0,
        SlowMode, SlowMode ? (int)SlowSeconds : 0, SubscriberMode, UniqueChatMode);

    /// <summary>True when the chat mode ticks were changed here but not sent to Twitch with Apply yet.</summary>
    public bool HasPendingChatModes => Current != _sent;

    private static readonly HashSet<string> ChatModeProperties =
    [
        nameof(EmoteMode), nameof(FollowerMode), nameof(FollowerMinutes), nameof(SlowMode), nameof(SlowSeconds),
        nameof(SubscriberMode), nameof(UniqueChatMode),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && ChatModeProperties.Contains(name)) OnPropertyChanged(nameof(HasPendingChatModes));
    }

    private void Show(TwitchChatSettings s)
    {
        EmoteMode = s.EmoteMode;
        FollowerMode = s.FollowerMode;
        FollowerMinutes = s.FollowerModeDuration;
        SlowMode = s.SlowMode;
        if (s.SlowModeWaitTime > 0) SlowSeconds = s.SlowModeWaitTime;
        SubscriberMode = s.SubscriberMode;
        UniqueChatMode = s.UniqueChatMode;
        _sent = Current;
        OnPropertyChanged(nameof(HasPendingChatModes));
    }

    [RelayCommand]
    private Task ApplyShieldMode() => Run(async () => ShieldMode = (await Api.SetShieldModeAsync(ShieldMode)).IsActive,
        "Shield mode updated.");

    [RelayCommand]
    private Task ClearChat()
    {
        if (!ClearChatConfirm.Confirm())
        {
            Error = "";
            Info = "Click again within 5 seconds to wipe every message in your chat for everyone.";
            return Task.CompletedTask;
        }
        return Run(() => Api.ClearChatAsync(), "Chat cleared.");
    }

    [RelayCommand]
    private Task SendAnnouncement() => Run(async () =>
    {
        await Api.SendAnnouncementAsync(Announcement, AnnouncementColor);
        Announcement = "";
    }, "Announcement sent.");
}
