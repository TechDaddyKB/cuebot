using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Sharing;
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
    public Guid OverlayId => overlayId;
    public string Name => layer.Name;

    /// <summary>Packages can be made (the export menu is shown).</summary>
    public bool CanShare => owner.CanShare;

    /// <summary>Its code is not Cuebot's own, so sharing it needs the user's word that they may.</summary>
    public bool HasCustomCode => owner.CustomWidgets(overlayId, layer.Id).Count > 0;

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
    public bool CanShare => owner.CanShare;

    /// <summary>Some widgets' code is not Cuebot's own (bought, imported or written by someone else).</summary>
    public bool HasCustomCode => owner.CustomWidgets(overlay.Id).Count > 0;

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

    /// <summary>Adds Cuebot's alert box (a design per alert type) to this overlay.</summary>
    [RelayCommand]
    private void AddAlertBox() => owner.AddAlertBox(overlay.Id);

    /// <summary>Adds a label, goal bar, event list, credits roll or countdown (by kind id).</summary>
    [RelayCommand]
    private void AddBuiltIn(string kind) => owner.AddBuiltIn(overlay.Id, kind);

    [RelayCommand]
    private void Delete() => owner.Delete(overlay.Id);
}

/// <summary>An overlay in the connected StreamElements account, offered for import.</summary>
public sealed partial class StreamElementsOverlayRowViewModel(StreamElementsOverlaySummary overlay, OverlaysViewModel owner) : ObservableObject
{
    public string Name => overlay.Name;
    public string Canvas => $"{overlay.Width} × {overlay.Height}";

    [RelayCommand]
    private Task Import() => owner.ImportFromStreamElementsAsync(overlay);
}

/// <summary>An item in the community gallery: what it is, its preview, and import / report / view.</summary>
public sealed partial class GalleryItemViewModel(GalleryItem item, OverlaysViewModel owner) : ObservableObject
{
    public GalleryItem Item => item;
    public string Name => item.Name;
    public string Byline => item.Author.Length > 0 ? $"by {item.Author}" : "";
    public string Description => item.Description;
    public string Details => string.Join(" · ", new[]
    {
        item.Kind == "widget" ? "Widget" : "Overlay", item.License, item.Size >= 1024 * 1024 ? $"{item.Size / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, item.Size / 1024)} KB",
        item.OriginalCode ? "includes the author's own code" : "",
    }.Concat(item.Tags.Select(t => "#" + t)).Where(s => s.Length > 0));

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _preview;

    [RelayCommand]
    private Task Import() => owner.ImportFromGalleryAsync(this);

    [RelayCommand]
    private void Report() => owner.OpenUrl(OverlayGallery.ReportUrl(item));

    [RelayCommand]
    private void View() => owner.OpenUrl(OverlayGallery.PageUrl(item));
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
    private readonly OverlayAlertQueue? _alerts;
    private readonly IStreamElementsOverlaySource? _streamElements;
    private readonly OverlayPackages? _packages;
    private readonly OverlayGallery? _gallery;

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

    public OverlaysViewModel(OverlayStore store, ServerManager servers, WidgetLibrary? widgets = null, OverlayAlertQueue? alerts = null,
        ChatPollPanelViewModel? polls = null, IStreamElementsOverlaySource? streamElements = null, OverlayPackages? packages = null,
        OverlayGallery? gallery = null)
        : base(PageTitle)
    {
        _packages = packages;
        _gallery = gallery;
        Polls = polls;
        _streamElements = streamElements;
        _store = store;
        _servers = servers;
        _widgets = widgets;
        _alerts = alerts;
        _newCanvas = Presets[0];
        if (alerts is not null)
            alerts.Changed += () => OnUi(() => { OnPropertyChanged(nameof(AlertsPaused)); OnPropertyChanged(nameof(AlertsMuted)); });
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

    public bool HasAlertQueue => _alerts is not null;

    /// <summary>Cuebot chat polls and predictions (null when not available).</summary>
    public ChatPollPanelViewModel? Polls { get; }

    public bool HasPolls => Polls is not null;

    /// <summary>Alerts on every overlay wait (the one playing finishes) until resumed.</summary>
    public bool AlertsPaused
    {
        get => _alerts?.Paused ?? false;
        set
        {
            if (_alerts is not null) _alerts.Paused = value;
        }
    }

    /// <summary>Alert sounds, videos and alert TTS are silent on every overlay.</summary>
    public bool AlertsMuted
    {
        get => _alerts?.Muted ?? false;
        set
        {
            if (_alerts is not null) _alerts.Muted = value;
        }
    }

    // ---- Packages ----

    public bool CanShare => _packages is not null;

    internal IReadOnlyList<string> CustomWidgets(Guid overlayId, Guid? layerId = null) => _packages?.CustomWidgets(overlayId, layerId) ?? [];

    /// <summary>
    /// Writes an overlay (or one of its widgets) as a package. Code that is not Cuebot's own goes in only with
    /// <paramref name="includeCustomCode"/>, which the user picks to confirm they may share it.
    /// </summary>
    internal void Export(Guid overlayId, Guid? layerId, Stream output, bool includeCustomCode)
    {
        if (_packages is null) return;
        try
        {
            var result = layerId is { } id
                ? _packages.ExportWidget(overlayId, id, output, includeCustomCode)
                : _packages.ExportOverlay(overlayId, output, includeCustomCode);
            Status = "Saved the package"
                     + (result.Assets > 0 ? $" with {result.Assets} file{(result.Assets == 1 ? "" : "s")}" : "") + "."
                     + (result.LeftOut.Count > 0 ? " Left out (code not yours to share): " + string.Join(", ", result.LeftOut) + "." : "");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Status = "Could not save the package: " + ex.Message;
        }
    }

    /// <summary>
    /// Opens a package: an overlay package becomes a new overlay; a widget package is added to <paramref name="overlayId"/>.
    /// </summary>
    public async Task ImportPackageAsync(Stream input, Guid? overlayId = null)
    {
        if (_packages is null) return;
        try
        {
            PackageImportResult? result = null;
            _editing = true;
            try
            {
                result = await _packages.ImportAsync(input, overlayId);
            }
            finally
            {
                _editing = false;
            }
            Refresh();
            var what = result.Kind == "overlay"
                ? $"Imported \"{result.Overlay.Name}\" with {result.Layers} layer{(result.Layers == 1 ? "" : "s")}. It has its own new link for OBS."
                : $"Added the widget to \"{result.Overlay.Name}\".";
            Status = what + (result.LeftOut.Count > 0 ? " Not included: " + string.Join(", ", result.LeftOut) + "." : "");
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentException or IOException or InvalidDataException)
        {
            Status = "Could not import the package: " + ex.Message;
        }
    }

    // ---- Community gallery ----

    public bool CanBrowseGallery => _gallery is not null && _packages is not null;

    public ObservableCollection<GalleryItemViewModel> GalleryItems { get; } = [];

    [ObservableProperty]
    private bool _galleryOpen;

    [ObservableProperty]
    private bool _galleryBusy;

    /// <summary>The overlay widget items are added to.</summary>
    [ObservableProperty]
    private OverlayRowViewModel? _galleryTarget;

    /// <summary>Lists the gallery's items and loads their previews.</summary>
    [RelayCommand]
    private async Task BrowseGallery()
    {
        if (_gallery is null) return;
        GalleryBusy = true;
        try
        {
            var items = await _gallery.ListAsync();
            GalleryItems.Clear();
            foreach (var item in items) GalleryItems.Add(new GalleryItemViewModel(item, this));
            GalleryTarget ??= Overlays.FirstOrDefault();
            GalleryOpen = true;
            Status = items.Count == 0 ? "The gallery is empty so far." : $"{items.Count} item{(items.Count == 1 ? "" : "s")} in the community gallery. Every item was reviewed before it was listed.";
        }
        catch (GalleryException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            GalleryBusy = false;
        }
        foreach (var row in GalleryItems.ToArray())
        {
            try
            {
                var bytes = await _gallery.PreviewAsync(row.Item);
                using var stream = new MemoryStream(bytes);
                row.Preview = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex) when (ex is GalleryException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // No preview; the item is still listed.
            }
        }
    }

    [RelayCommand]
    private void CloseGallery() => GalleryOpen = false;

    /// <summary>Opens the wiki's guide to sharing an item.</summary>
    [RelayCommand]
    private void ShareToGallery() => OpenUrl(OverlayGallery.ContributeUrl);

    /// <summary>Downloads an item (checked against the gallery's index) and imports it.</summary>
    internal async Task ImportFromGalleryAsync(GalleryItemViewModel row)
    {
        if (_gallery is null) return;
        if (row.Item.Kind == "widget" && GalleryTarget is null)
        {
            Status = "Pick the overlay to add this widget to (\"Add widgets to\" above the list), or create one first.";
            return;
        }
        GalleryBusy = true;
        try
        {
            using var package = await _gallery.DownloadAsync(row.Item);
            await ImportPackageAsync(package, row.Item.Kind == "widget" ? GalleryTarget?.Id : null);
        }
        catch (GalleryException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            GalleryBusy = false;
        }
    }

    // ---- Import from StreamElements ----

    public bool CanImportFromStreamElements => _streamElements is not null && _widgets is not null;

    /// <summary>The account's overlays, listed after "Import from StreamElements…".</summary>
    public ObservableCollection<StreamElementsOverlayRowViewModel> StreamElementsOverlays { get; } = [];

    [ObservableProperty]
    private bool _streamElementsOpen;

    [ObservableProperty]
    private bool _streamElementsBusy;

    /// <summary>Lists the overlays in the connected StreamElements account (read only).</summary>
    [RelayCommand]
    private async Task ShowStreamElementsOverlays()
    {
        if (_streamElements is null) return;
        if (!_streamElements.HasToken)
        {
            Status = "Connect StreamElements first: paste your JWT token on the Donations page, then try again.";
            return;
        }
        StreamElementsBusy = true;
        try
        {
            var list = await _streamElements.ListOverlaysAsync(CancellationToken.None);
            StreamElementsOverlays.Clear();
            foreach (var overlay in list) StreamElementsOverlays.Add(new StreamElementsOverlayRowViewModel(overlay, this));
            StreamElementsOpen = true;
            Status = list.Count == 0 ? "Your StreamElements account has no overlays." : "Pick an overlay to copy into Cuebot. Your StreamElements overlays are not changed.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Cuebot.Donations.StreamElements.StreamElementsApiException)
        {
            Status = "Could not list your StreamElements overlays: " + ex.Message;
        }
        finally
        {
            StreamElementsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseStreamElementsOverlays() => StreamElementsOpen = false;

    /// <summary>Copies one StreamElements overlay into a new Cuebot overlay.</summary>
    internal async Task ImportFromStreamElementsAsync(StreamElementsOverlaySummary summary)
    {
        if (_streamElements is null || _store is null || _widgets is null) return;
        StreamElementsBusy = true;
        try
        {
            var json = await _streamElements.GetOverlayAsync(summary.Id, CancellationToken.None);
            StreamElementsImportResult? result = null;
            Edit(() => result = StreamElementsOverlayImport.Import(json, _store, _widgets));
            Refresh();
            if (result is null) return;
            Status = $"Imported \"{result.Overlay.Name}\" with {result.Imported} widget{(result.Imported == 1 ? "" : "s")}."
                     + (result.Skipped.Count > 0 ? " Not imported: " + string.Join(", ", result.Skipped) + "." : "")
                     + " Copy its link into OBS; alert and label settings can be changed with Edit….";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException
                                       or Cuebot.Donations.StreamElements.StreamElementsApiException)
        {
            Status = $"Could not import \"{summary.Name}\": {ex.Message}";
        }
        finally
        {
            StreamElementsBusy = false;
        }
    }

    /// <summary>Ends the alert playing on every overlay.</summary>
    [RelayCommand]
    private void SkipAlert() => _alerts?.Skip();

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

    internal void AddAlertBox(Guid overlayId)
    {
        if (_widgets is null) return;
        Edit(() => _widgets.AddAlertBox(overlayId));
        Refresh();
        Status = "Added an alert box at the top of the canvas. Use Edit… to design each alert.";
    }

    internal void AddBuiltIn(Guid overlayId, string kind)
    {
        if (_widgets is null || BuiltInWidgets.Kinds.FirstOrDefault(k => k.Id == kind) is not { } found) return;
        Edit(() => _widgets.AddBuiltIn(overlayId, kind));
        Refresh();
        Status = $"Added a {found.Name.ToLowerInvariant()}. Use Edit… to choose what it shows and how it looks.";
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
        var target = GalleryTarget?.Id;
        Overlays.Clear();
        if (_store is null) return;
        foreach (var overlay in _store.All().OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Overlays.Add(new OverlayRowViewModel(overlay, this));
        }
        // The rows are new, so the gallery's "Add widgets to" picks the same overlay again (or the first, if it is gone).
        GalleryTarget = Overlays.FirstOrDefault(o => o.Id == target) ?? (target is null ? null : Overlays.FirstOrDefault());
        OnPropertyChanged(nameof(HasOverlays));
    }
}
