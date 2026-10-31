using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Sharing;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in, read-only test against the real community gallery on GitHub: lists it, downloads every item (checked against
/// the index) and imports each into throwaway stores. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class GalleryLiveTests
{
    [LiveServiceFact("CUEBOT_LIVE_GALLERY", flag: true)]
    public async Task Every_gallery_item_downloads_and_imports()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var gallery = new OverlayGallery(http);
        var items = await gallery.ListAsync();
        Assert.Contains(items, i => i.Id == "starter-scene");

        var log = new AppLog();
        using var overlays = new OverlayStore(null, log);
        using var elements = new ElementStore(null, log);
        using var code = new WidgetCodeStore(null);
        using var assets = new AssetStore(null);
        var library = new WidgetLibrary(overlays, elements, code);
        var packages = new OverlayPackages(overlays, elements, code, assets, library);
        var target = overlays.Create("Widget target", 1920, 1080);
        foreach (var item in items)
        {
            using var package = await gallery.DownloadAsync(item);
            Assert.NotEmpty(await gallery.PreviewAsync(item));
            var result = await packages.ImportAsync(package, item.Kind == "widget" ? target.Id : null);
            Assert.True(result.Layers > 0, item.Id);
            Assert.Empty(result.LeftOut);
        }
    }
}
