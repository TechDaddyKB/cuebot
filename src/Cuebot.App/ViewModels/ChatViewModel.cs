using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat;

namespace Cuebot.App.ViewModels;

/// <summary>Colours that identify each platform in the combined chat.</summary>
internal static class PlatformColors
{
    public static IBrush For(string platform) => platform.ToLowerInvariant() switch
    {
        "twitch" => new SolidColorBrush(Color.Parse("#9146FF")),
        "youtube" => new SolidColorBrush(Color.Parse("#FF3B3B")),
        "kick" => new SolidColorBrush(Color.Parse("#53FC18")),
        "rumble" => new SolidColorBrush(Color.Parse("#85C742")),
        _ => new SolidColorBrush(Color.Parse("#6CA8FF")),
    };
}

public sealed class ChatLineViewModel(ChatMessage message)
{
    public ChatMessage Message { get; } = message;
    public string Platform => Message.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(message.Platform);
    public string Time => Message.Time.ToLocalTime().ToString("HH:mm");
    public string User => Message.User;
    public IBrush UserBrush { get; } = message.Color is { } c && Color.TryParse(c, out var color) ? new SolidColorBrush(color) : Brushes.White;
    public string Text => Message.Text;
    public string? Amount => Message.Amount;
    public bool HasAmount => Message.Amount is not null;

    /// <summary>Plain-text role badges (no emoji, so they render with any font on Linux).</summary>
    public string Badges => string.Concat(
        Message.Roles.HasFlag(ChatRoles.Broadcaster) ? "[host] " : "",
        Message.Roles.HasFlag(ChatRoles.Moderator) ? "[mod] " : "",
        Message.Roles.HasFlag(ChatRoles.Vip) ? "[vip] " : "",
        Message.Roles.HasFlag(ChatRoles.Subscriber) ? "[sub] " : "",
        Message.Roles.HasFlag(ChatRoles.Verified) ? "[verified] " : "");
}

public sealed class FeedLineViewModel(FeedItem item)
{
    public FeedItem Item { get; } = item;
    public string Platform => Item.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(item.Platform);
    public string Time => Item.Time.ToLocalTime().ToString("HH:mm");
    public string Title => Item.Title;
    public string? Detail => Item.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Item.Detail);
    public string Kind => Item.Kind.ToString();
}

/// <summary>Combined chat across all platforms, the activity feed, and a send box.</summary>
public sealed partial class ChatViewModel : PageViewModel
{
    private const int MaxLines = 500;
    private readonly ChatHub _hub;

    public ChatViewModel(ChatHub hub) : base("Chat")
    {
        _hub = hub;
        PlatformFilters = [ChatHub.AllPlatforms, .. hub.Platforms];
        SendTargets = [ChatHub.AllPlatforms, .. hub.Platforms];
        foreach (var message in hub.RecentMessages()) AddLine(message);
        foreach (var item in hub.RecentFeed()) Feed.Insert(0, new FeedLineViewModel(item));
        hub.MessageReceived += m => OnUi(() => AddLine(m));
        hub.FeedItemAdded += f => OnUi(() => AddFeed(f));
    }

    public ObservableCollection<ChatLineViewModel> Messages { get; } = [];
    public ObservableCollection<FeedLineViewModel> Feed { get; } = [];
    public IReadOnlyList<string> PlatformFilters { get; }
    public IReadOnlyList<string> SendTargets { get; }

    /// <summary>Raised after a line is appended, so the view can keep the newest message in sight.</summary>
    public event Action? LineAdded;

    [ObservableProperty] private string _filter = ChatHub.AllPlatforms;
    [ObservableProperty] private string _sendTarget = ChatHub.AllPlatforms;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _status = "";

    private readonly List<ChatMessage> _all = [];

    partial void OnFilterChanged(string value)
    {
        Messages.Clear();
        foreach (var m in _all.Where(Visible)) Messages.Add(new ChatLineViewModel(m));
    }

    private bool Visible(ChatMessage m) =>
        Filter == ChatHub.AllPlatforms || string.Equals(m.Platform, Filter, StringComparison.OrdinalIgnoreCase);

    private void AddLine(ChatMessage message)
    {
        _all.Add(message);
        if (_all.Count > MaxLines) _all.RemoveAt(0);
        if (!Visible(message)) return;
        Messages.Add(new ChatLineViewModel(message));
        while (Messages.Count > MaxLines) Messages.RemoveAt(0);
        LineAdded?.Invoke();
    }

    private void AddFeed(FeedItem item)
    {
        Feed.Insert(0, new FeedLineViewModel(item));
        while (Feed.Count > MaxLines) Feed.RemoveAt(Feed.Count - 1);
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        try
        {
            var sent = await _hub.SendAsync(SendTarget, text);
            Draft = "";
            Status = $"Sent to {string.Join(", ", sent)}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearFeed() => Feed.Clear();
}
