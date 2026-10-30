using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;

namespace Cuebot.Tests.Overlays;

public sealed class OverlayStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-overlays-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Create_gives_each_overlay_a_token_and_clamps_the_canvas()
    {
        using var store = new OverlayStore(null, _log);
        var a = store.Create("  Main  ", 3840, 2160);
        var b = store.Create("", 1, 99999);

        Assert.Equal("Main", a.Name);
        Assert.Equal((3840, 2160), (a.Width, a.Height));
        Assert.Equal("Overlay", b.Name);
        Assert.Equal((OverlayStore.MinSize, OverlayStore.MaxSize), (b.Width, b.Height));
        Assert.StartsWith("ov_", a.Token);
        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void Authorize_needs_the_overlays_own_token()
    {
        using var store = new OverlayStore(null, _log);
        var a = store.Create("A");
        var b = store.Create("B");

        Assert.Equal(a.Id, store.Authorize(a.Id, a.Token)?.Id);
        Assert.Null(store.Authorize(a.Id, b.Token));
        Assert.Null(store.Authorize(a.Id, ""));
        Assert.Null(store.Authorize(a.Id, null));
        Assert.Null(store.Authorize(Guid.NewGuid(), a.Token));
    }

    [Fact]
    public void Regenerating_the_token_invalidates_the_old_one()
    {
        using var store = new OverlayStore(null, _log);
        var overlay = store.Create("A");
        var token = store.RegenerateToken(overlay.Id);

        Assert.NotNull(token);
        Assert.Null(store.Authorize(overlay.Id, overlay.Token));
        Assert.NotNull(store.Authorize(overlay.Id, token));
        Assert.Null(store.RegenerateToken(Guid.NewGuid()));
    }

    [Fact]
    public void Update_keeps_id_and_token_and_raises_changed()
    {
        using var store = new OverlayStore(null, _log);
        var overlay = store.Create("A");
        var changed = new List<Guid>();
        store.Changed += changed.Add;

        Assert.True(store.Update(overlay.Id, o =>
        {
            o.Name = "Renamed";
            o.Id = Guid.NewGuid();
            o.Token = "stolen";
            o.Layers.Add(new OverlayLayer { Type = "chat", Settings = new JsonObject { ["fontSize"] = 18 } });
        }));

        var saved = store.Get(overlay.Id)!;
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(overlay.Token, saved.Token);
        Assert.Equal(18, saved.Layers.Single().Settings!["fontSize"]!.GetValue<int>());
        Assert.Equal([overlay.Id], changed);
        Assert.False(store.Update(Guid.NewGuid(), _ => { }));
    }

    [Fact]
    public void Reads_are_copies()
    {
        using var store = new OverlayStore(null, _log);
        var overlay = store.Create("A");
        store.Get(overlay.Id)!.Name = "Changed outside";
        Assert.Equal("A", store.Get(overlay.Id)!.Name);
    }

    [Fact]
    public void Overlays_and_elements_survive_a_restart()
    {
        var paths = new AppPaths(_dir);
        Guid overlayId, elementId;
        string token;
        using (var overlays = new OverlayStore(paths, _log))
        using (var elements = new ElementStore(paths, _log))
        {
            var overlay = overlays.Create("Main", 1080, 1920);
            overlayId = overlay.Id;
            token = overlay.Token;
            elementId = elements.Add(new OverlayElement { Name = "Follower alert", Type = "alertbox" }).Id;
        }

        using var reloaded = new OverlayStore(paths, _log);
        using var reloadedElements = new ElementStore(paths, _log);
        var again = reloaded.Get(overlayId)!;
        Assert.Equal((1080, 1920), (again.Width, again.Height));
        Assert.NotNull(reloaded.Authorize(overlayId, token));
        Assert.Equal("Follower alert", reloadedElements.Get(elementId)!.Name);
    }

    [Fact]
    public void A_saved_overlay_without_a_token_gets_one()
    {
        var paths = new AppPaths(_dir);
        var id = Guid.NewGuid();
        File.WriteAllText(paths.File("overlays.json"), $$"""{"overlays":[{"id":"{{id}}","name":"Hand-edited"}]}""");

        using var store = new OverlayStore(paths, _log);
        Assert.StartsWith("ov_", store.Get(id)!.Token);
    }

    [Fact]
    public void Element_ids_stay_unique()
    {
        using var store = new ElementStore(null, _log);
        var first = store.Add(new OverlayElement { Name = "A" });
        var second = store.Add(new OverlayElement { Id = first.Id, Name = "B" });
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(store.Delete(first.Id));
        Assert.False(store.Delete(first.Id));
        Assert.Single(store.All());
    }

    [Fact]
    public async Task Assets_are_stored_once_by_content()
    {
        using var assets = new AssetStore(null);
        var a = await assets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("same")), "Alert.PNG");
        var b = await assets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("same")), "copy.png");
        var c = await assets.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes("other")), "sound.mp3");

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}\\.png$", a);
        Assert.EndsWith(".mp3", c);
        Assert.Equal("same", File.ReadAllText(assets.PathOf(a)!));
        Assert.Equal(2, Directory.GetFiles(assets.Folder).Length);
    }

    [Fact]
    public async Task Assets_reject_unknown_types_and_bad_names()
    {
        using var assets = new AssetStore(null);
        await Assert.ThrowsAsync<ArgumentException>(() => assets.AddAsync(new MemoryStream([1]), "script.html"));
        await Assert.ThrowsAsync<ArgumentException>(() => assets.AddAsync(new MemoryStream([1]), "noextension"));

        var name = await assets.AddAsync(new MemoryStream([1, 2, 3]), "a.png");
        Assert.Null(assets.PathOf("../" + name));
        Assert.Null(assets.PathOf(name.ToUpperInvariant()));
        Assert.Null(assets.PathOf(name[..^3] + "exe"));
        Assert.Null(assets.PathOf(null));
        Assert.Empty(Directory.GetFiles(assets.Folder, "*.tmp"));
    }

    [Fact]
    public void Feed_delivers_to_subscribers_until_disposed()
    {
        var feed = new OverlayFeed(_log);
        var got = new List<string>();
        var subscription = feed.Subscribe(m => got.Add(m.Json));
        feed.Subscribe(_ => throw new InvalidOperationException("one bad handler"));

        feed.Publish(OverlayMessage.Event("follower-latest", new JsonObject { ["name"] = "kris" }));
        subscription.Dispose();
        feed.Publish(OverlayMessage.Command("reload"));

        var message = JsonNode.Parse(Assert.Single(got))!;
        Assert.Equal("event", message["type"]!.GetValue<string>());
        Assert.Equal("follower-latest", message["listener"]!.GetValue<string>());
        Assert.Equal("kris", message["event"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Only_chat_and_session_messages_are_droppable()
    {
        Assert.True(OverlayMessage.Event("message", null).Droppable);
        Assert.False(OverlayMessage.Event("subscriber-latest", null).Droppable);
        Assert.True(OverlayMessage.Session([]).Droppable);
        Assert.False(OverlayMessage.Command("skip").Droppable);
    }
}
