using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Overlays;
using Cuebot.Server;

namespace Cuebot.App.ViewModels;

/// <summary>A canvas size offered when creating an overlay.</summary>
public sealed record CanvasPreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>One overlay row: its name, canvas and browser source link.</summary>
public sealed partial class OverlayRowViewModel(Overlay overlay, OverlaysViewModel owner) : ObservableObject
{
    public Guid Id => overlay.Id;
    public string Canvas => $"{overlay.Width} × {overlay.Height}";
    public string Url => owner.UrlFor(overlay);

    /// <summary>The link with its token hidden, so a shared screen does not give it away; Copy copies the real one.</summary>
    public string DisplayUrl => OverlaysViewModel.Mask(Url);

    public string Name
    {
        get => overlay.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == overlay.Name) return;
            overlay.Name = value.Trim();
            owner.Rename(overlay.Id, overlay.Name);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void NewLink() => owner.RegenerateLink(overlay.Id);

    [RelayCommand]
    private void Delete() => owner.Delete(overlay.Id);
}

/// <summary>
/// The Overlays &amp; Widgets page. For now it manages the overlays themselves (create, rename, copy the OBS link,
/// replace the link, delete); the designer comes in a later phase.
/// </summary>
public sealed partial class OverlaysViewModel : PageViewModel
{
    public const string PageTitle = "Overlays & Widgets";

    private readonly OverlayStore? _store;
    private readonly ServerManager? _servers;
    private bool _editing;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private CanvasPreset _newCanvas;

    [ObservableProperty]
    private decimal _newWidth = 1920;

    [ObservableProperty]
    private decimal _newHeight = 1080;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Design-time and tests without services.</summary>
    public OverlaysViewModel() : base(PageTitle) => _newCanvas = Presets[0];

    public OverlaysViewModel(OverlayStore store, ServerManager servers) : base(PageTitle)
    {
        _store = store;
        _servers = servers;
        _newCanvas = Presets[0];
        store.Changed += _ => OnUi(() => { if (!_editing) Refresh(); });
        servers.StatusChanged += () => OnUi(() => OnPropertyChanged(nameof(ServerHint)));
        Refresh();
    }

    public static IReadOnlyList<CanvasPreset> Presets { get; } =
    [
        new("1080p (1920 × 1080)", 1920, 1080),
        new("1440p (2560 × 1440)", 2560, 1440),
        new("4K (3840 × 2160)", 3840, 2160),
        new("Vertical (1080 × 1920)", 1080, 1920),
        new("Custom size", 0, 0),
    ];

    public ObservableCollection<OverlayRowViewModel> Overlays { get; } = [];

    public bool IsCustomSize => NewCanvas.Width == 0;
    public bool HasOverlays => Overlays.Count > 0;

    /// <summary>Why links will not load, when the overlay server is off or failed; empty when it is running.</summary>
    public string ServerHint
    {
        get
        {
            if (_servers?.Statuses.FirstOrDefault(s => s.Kind == ServerKind.Overlays) is not { } status || status.Running) return "";
            if (!status.Enabled) return "The overlay server is off, so these links will not load. Turn it on on the Servers page.";
            return status.Message == "Not running"
                ? "The overlay server is not running, so these links will not load yet."
                : $"The overlay server could not start: {status.Message}. Check it on the Servers page.";
        }
    }

    partial void OnNewCanvasChanged(CanvasPreset value)
    {
        if (value.Width > 0) (NewWidth, NewHeight) = (value.Width, value.Height);
        OnPropertyChanged(nameof(IsCustomSize));
    }

    [RelayCommand]
    private void Create()
    {
        if (_store is null) return;
        var name = string.IsNullOrWhiteSpace(NewName) ? $"Overlay {Overlays.Count + 1}" : NewName.Trim();
        var overlay = _store.Create(name, (int)NewWidth, (int)NewHeight);
        NewName = "";
        Status = $"Created \"{overlay.Name}\". Copy its link into an OBS browser source of {overlay.Width} × {overlay.Height}.";
    }

    internal string UrlFor(Overlay overlay) => _servers?.OverlayUrl(overlay) ?? "";

    /// <summary>"…?token=ov_ab…" with everything after the first few token characters replaced.</summary>
    internal static string Mask(string url)
    {
        var at = url.IndexOf("token=", StringComparison.Ordinal);
        if (at < 0) return url;
        var keep = Math.Min(url.Length, at + "token=".Length + 5);
        return url[..keep] + new string('•', 12);
    }

    internal void Rename(Guid id, string name) => Edit(() => _store?.Update(id, o => o.Name = name));

    internal void RegenerateLink(Guid id)
    {
        if (_store?.RegenerateToken(id) is null) return;
        Status = "Made a new link. Browser sources using the old one stop working; paste the new link into OBS.";
    }

    internal void Delete(Guid id)
    {
        if (_store?.Delete(id) == true) Status = "Deleted the overlay.";
    }

    /// <summary>Shows that a row's link was copied.</summary>
    public void Copied(OverlayRowViewModel row) => Status = $"Copied the link for \"{row.Name}\".";

    private void Edit(Action action)
    {
        _editing = true;
        try { action(); }
        finally { _editing = false; }
    }

    private void Refresh()
    {
        Overlays.Clear();
        if (_store is null) return;
        foreach (var overlay in _store.All().OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Overlays.Add(new OverlayRowViewModel(overlay, this));
        }
        OnPropertyChanged(nameof(HasOverlays));
    }
}
