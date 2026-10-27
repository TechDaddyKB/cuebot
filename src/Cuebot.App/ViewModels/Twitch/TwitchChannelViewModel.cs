using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>Channel tab: title, category and tags editor, stream status and channel quick actions.</summary>
public sealed partial class TwitchChannelViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    public static IReadOnlyList<int> AdLengths { get; } = [30, 60, 90, 120, 150, 180];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string _gameId = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private bool _isBrandedContent;
    [ObservableProperty] private string _categoryQuery = "";
    [ObservableProperty] private TwitchCategory? _selectedCategory;
    [ObservableProperty] private string _streamStatus = "Not loaded";
    [ObservableProperty] private string _markerDescription = "";
    [ObservableProperty] private int _adLength = 30;
    [ObservableProperty] private string _raidTarget = "";
    [ObservableProperty] private string _shoutoutTarget = "";

    public ObservableCollection<TwitchCategory> Categories { get; } = [];

    partial void OnSelectedCategoryChanged(TwitchCategory? value)
    {
        if (value is null) return;
        GameId = value.Id;
        GameName = value.Name;
    }

    [RelayCommand]
    private Task Load() => Run(async () =>
    {
        var channel = await Api.GetChannelAsync();
        if (channel is not null)
        {
            Title = channel.Title;
            GameName = channel.GameName;
            GameId = channel.GameId;
            Tags = string.Join(", ", channel.Tags);
            IsBrandedContent = channel.IsBrandedContent;
        }
        var stream = await Api.GetStreamAsync();
        StreamStatus = stream is null
            ? "Offline"
            : $"Live for {(DateTimeOffset.UtcNow - (stream.StartedAt ?? DateTimeOffset.UtcNow)):h\\:mm} with {stream.ViewerCount} viewers";
    }, "Channel info loaded.");

    [RelayCommand]
    private Task SearchCategories() => Run(async () =>
    {
        Categories.Clear();
        foreach (var category in await Api.SearchCategoriesAsync(CategoryQuery)) Categories.Add(category);
        if (Categories.Count == 0) Info = "No category found.";
    });

    [RelayCommand]
    private Task Save() => Run(() => Api.UpdateChannelAsync(new ChannelUpdate
    {
        Title = Title,
        GameId = GameId.Length > 0 ? GameId : null,
        Tags = TwitchTags(Tags),
        IsBrandedContent = IsBrandedContent,
    }), "Channel updated.");

    internal static string[] TwitchTags(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private Task CreateMarker() => Run(async () =>
    {
        var marker = await Api.CreateStreamMarkerAsync(MarkerDescription);
        MarkerDescription = "";
        Info = $"Marker created at {TimeSpan.FromSeconds(marker.PositionSeconds):h\\:mm\\:ss}.";
    });

    [RelayCommand]
    private Task CreateClip() => Run(async () =>
    {
        var clip = await Api.CreateClipAsync();
        Info = $"Clip created: https://clips.twitch.tv/{clip.Id}";
    });

    [RelayCommand]
    private Task RunAd() => Run(async () =>
    {
        var ad = await Api.RunCommercialAsync(AdLength);
        Info = $"Running a {ad.Length}s ad. Next one possible in {ad.RetryAfter}s.";
    });

    [RelayCommand]
    private Task SnoozeAd() => Run(async () =>
    {
        var schedule = await Api.SnoozeAdAsync();
        Info = $"Ad snoozed; {schedule.SnoozeCount} snoozes left.";
    });

    [RelayCommand]
    private Task StartRaid() => Run(() => Api.StartRaidAsync(RaidTarget), $"Raid on {RaidTarget} started.");

    [RelayCommand]
    private Task CancelRaid() => Run(() => Api.CancelRaidAsync(), "Raid cancelled.");

    [RelayCommand]
    private Task Shoutout() => Run(() => Api.SendShoutoutAsync(ShoutoutTarget), $"Shoutout sent to {ShoutoutTarget}.");
}
