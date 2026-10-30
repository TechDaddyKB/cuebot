using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Media;

namespace Cuebot.App.ViewModels;

/// <summary>A waiting request: remove (refunded) or move it.</summary>
public sealed class MediaQueueRowViewModel(MediaItem item, MediaRequests media)
{
    public string Title { get; } = item.Title;
    public string Detail { get; } = $"{item.Author} · asked by {item.UserName} ({item.Platform})" + (item.Cost > 0 ? $" · paid {item.Cost}" : "");
    public string Url { get; } = item.Url;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => media.Remove(item.Id));
    public IRelayCommand UpCommand { get; } = new RelayCommand(() => media.Move(item.Id, -1));
    public IRelayCommand DownCommand { get; } = new RelayCommand(() => media.Move(item.Id, 1));
}

/// <summary>
/// Media requests: what plays (skip, pause), the queue (remove, reorder, add a link yourself), the latest played and
/// the settings (commands, cost, limits). The Media request player widget plays them on an overlay.
/// </summary>
public sealed partial class MediaRequestsViewModel : PageViewModel
{
    public const string PageTitle = "Media Requests";

    private readonly MediaRequests _media;

    [ObservableProperty] private string _newLink = "";
    [ObservableProperty] private string _status = "";

    public MediaRequestsViewModel(MediaRequests media) : base(PageTitle)
    {
        _media = media;
        media.Changed += () => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<MediaQueueRowViewModel> Queue { get; } = [];
    public ObservableCollection<string> Played { get; } = [];

    public string NowPlaying => _media.Current is { } c ? $"{c.Title} — asked by {c.UserName} ({c.Platform})" : "Nothing is playing.";
    public bool Playing => _media.Current is not null;
    public bool Paused => _media.Paused;
    public string QueueSummary => Queue.Count == 0 ? "The queue is empty." : $"{Queue.Count} waiting";

    // Settings write through as they change.
    private void Set(Action<MediaSettings> edit) => _media.UpdateSettings(edit);

    public bool Open { get => _media.Settings.Open; set => Set(s => s.Open = value); }
    public string Command { get => _media.Settings.Command; set => Set(s => s.Command = value); }
    public string WrongCommand { get => _media.Settings.WrongCommand; set => Set(s => s.WrongCommand = value); }
    /// <summary>The moderators' skip command (named apart from <see cref="SkipCommand"/>, the Skip button's command).</summary>
    public string ModSkipCommand { get => _media.Settings.SkipCommand; set => Set(s => s.SkipCommand = value); }
    public decimal Cost { get => _media.Settings.Cost; set => Set(s => s.Cost = (long)value); }
    public decimal MaxPerViewer { get => _media.Settings.MaxPerViewer; set => Set(s => s.MaxPerViewer = (int)value); }

    /// <summary>The length limit in minutes (0: whole videos).</summary>
    public decimal MaxMinutes { get => _media.Settings.MaxSeconds / 60m; set => Set(s => s.MaxSeconds = (int)(value * 60)); }
    public bool SubscribersOnly { get => _media.Settings.SubscribersOnly; set => Set(s => s.SubscribersOnly = value); }
    public bool ReplyInChat { get => _media.Settings.ReplyInChat; set => Set(s => s.ReplyInChat = value); }
    public double Volume { get => _media.Settings.Volume; set => Set(s => s.Volume = (int)value); }

    private void Refresh()
    {
        Queue.Clear();
        foreach (var item in _media.Queue) Queue.Add(new MediaQueueRowViewModel(item, _media));
        Played.Clear();
        foreach (var item in _media.History.Take(10)) Played.Add($"{item.Title} — {item.UserName}");
        foreach (var name in (string[])[nameof(NowPlaying), nameof(Playing), nameof(Paused), nameof(QueueSummary), nameof(Open), nameof(Volume),
                     nameof(Command), nameof(WrongCommand), nameof(ModSkipCommand), nameof(Cost), nameof(MaxPerViewer), nameof(MaxMinutes),
                     nameof(SubscribersOnly), nameof(ReplyInChat)])
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Skip() => _media.Skip();

    [RelayCommand]
    private void TogglePause() => _media.SetPaused(!_media.Paused);

    [RelayCommand]
    private void ClearQueue() => Status = $"Removed {_media.Clear()} request(s); paid ones were refunded.";

    /// <summary>Adds a link for free, as the streamer.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var (item, error) = await _media.RequestAsync(NewLink, "Cuebot", "streamer", "you", free: true);
        Status = item is null ? "Not added: " + error + "." : $"Added \"{item.Title}\".";
        if (item is not null) NewLink = "";
    }
}
