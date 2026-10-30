using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Server;

namespace Cuebot.App.ViewModels;

/// <summary>A canvas size offered when creating an overlay.</summary>
public sealed record CanvasPreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>A widget placed on an overlay: its name and where it sits (overlay pixels); edits save as they are made.</summary>
public sealed partial class LayerRowViewModel(Guid overlayId, OverlayLayer layer, OverlaysViewModel owner) : ObservableObject
{
    public Guid Id => layer.Id;
    public string Name => layer.Name;

    public decimal X { get => (decimal)layer.X; set { layer.X = (double)value; Save(); } }
    public decimal Y { get => (decimal)layer.Y; set { layer.Y = (double)value; Save(); } }
    public decimal Width { get => (decimal)layer.Width; set { layer.Width = (double)value; Save(); } }
    public decimal Height { get => (decimal)layer.Height; set { layer.Height = (double)value; Save(); } }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        owner.MoveLayer(overlayId, layer);
        OnPropertyChanged(property);
    }

    [RelayCommand]
    private void Remove() => owner.RemoveLayer(overlayId, layer.Id);

    /// <summary>Opens the web widget editor (settings, code, preview, Emulate) in the browser.</summary>
    [RelayCommand]
    private void Edit() => owner.EditLayer(overlayId, layer.Id);
}

/// <summary>One overlay row: its name, canvas, browser source link and widgets.</summary>
public sealed partial class OverlayRowViewModel : ObservableObject
{
    private readonly Overlay overlay;
    private readonly OverlaysViewModel owner;

    public OverlayRowViewModel(Overlay overlay, OverlaysViewModel owner)
    {
        this.overlay = overlay;
        this.owner = owner;
        foreach (var layer in overlay.Layers) Layers.Add(new LayerRowViewModel(overlay.Id, layer, owner));
    }

    public ObservableCollection<LayerRowViewModel> Layers { get; } = [];
    public bool HasLayers => Layers.Count > 0;
    public Guid Id => overlay.Id;
    public int CanvasWidth => overlay.Width;
    public int CanvasHeight => overlay.Height;
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

    /// <summary>Opens the overlay designer in the browser.</summary>
    [RelayCommand]
    private void Design() => owner.Design(overlay.Id);

    /// <summary>Adds Cuebot's chat box (chat from every platform) to this overlay.</summary>
    [RelayCommand]
    private void AddChatBox() => owner.AddChatBox(overlay.Id);

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
    private readonly WidgetLibrary? _widgets;

    /// <summary>Opens a link in the browser (tests replace it).</summary>
    public Action<string> OpenUrl { get; init; } = Cuebot.App.Shell.UrlLauncher.Open;
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

    public OverlaysViewModel(OverlayStore store, ServerManager servers, WidgetLibrary? widgets = null) : base(PageTitle)
    {
        _store = store;
        _servers = servers;
        _widgets = widgets;
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

    /// <summary>
    /// Imports a StreamElements widget zip (widget.io format, or the five panes as files) and places it on the overlay.
    /// </summary>
    public void AddWidget(Guid overlayId, Stream zip, string fileName)
    {
        if (_widgets is null) return;
        try
        {
            var code = WidgetImport.FromZip(zip);
            var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();
            Edit(() => _widgets.AddCustomWidget(overlayId, name.Length > 0 ? name : "Widget", code));
            Refresh();
            Status = $"Added \"{name}\". It covers the whole canvas; change its position and size below if needed.";
        }
        catch (InvalidDataException ex)
        {
            Status = "Could not add the widget: " + ex.Message;
        }
    }

    internal void AddChatBox(Guid overlayId)
    {
        if (_widgets is null) return;
        Edit(() => _widgets.AddChatBox(overlayId));
        Refresh();
        Status = "Added a chat box in the lower left. Use Edit… to change its look, or its Layout setting for YouTube chat CSS themes.";
    }

    internal void MoveLayer(Guid overlayId, OverlayLayer layer) =>
        Edit(() => _widgets?.MoveLayer(overlayId, layer.Id, layer.X, layer.Y, layer.Width, layer.Height));

    internal void EditLayer(Guid overlayId, Guid layerId)
    {
        if (!ServerRunning("widget editor")) return;
        OpenUrl(_servers!.EditorUrl(overlayId, layerId));
        Status = "Opened the widget editor in your browser.";
    }

    internal void Design(Guid overlayId)
    {
        if (!ServerRunning("designer")) return;
        OpenUrl(_servers!.DesignerUrl(overlayId));
        Status = "Opened the designer in your browser. Changes there save on their own.";
    }

    private bool ServerRunning(string what)
    {
        if (_servers is null) return false;
        if (_servers.Statuses.FirstOrDefault(s => s.Kind == ServerKind.Overlays) is { Running: true }) return true;
        Status = $"The {what} runs on the overlay server, which is not running. Turn it on on the Servers page.";
        return false;
    }

    internal void RemoveLayer(Guid overlayId, Guid layerId)
    {
        if (_widgets?.RemoveLayer(overlayId, layerId) == true) Status = "Removed the widget from the overlay (it stays in the library).";
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
