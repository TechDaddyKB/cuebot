using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cuebot.Core.Overlays.Sharing;

namespace Cuebot.Tests.Overlays;

public sealed class OverlayGalleryTests
{
    /// <summary>Answers gallery paths from a dictionary and records what was asked for.</summary>
    internal sealed class FakeGallery(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requested.Add(path);
            return Task.FromResult(files.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    internal static string Entry(string id, byte[] package, string kind = "overlay", string? sha = null, string? path = null) => $$"""
        {"id":"{{id}}","name":"{{id}} name","author":"Someone","description":"A thing","kind":"{{kind}}","license":"MIT","tags":["a","b"],
         "originalCode":false,"package":"{{path ?? $"items/{id}/package.cuebot-overlay"}}","preview":"items/{{id}}/preview.png",
         "size":{{package.Length}},"sha256":"{{sha ?? Convert.ToHexStringLower(SHA256.HashData(package))}}"}
        """;

    private static readonly Uri Base = new("https://gallery.test/");

    [Fact]
    public async Task The_index_lists_items_that_follow_the_rules_and_downloads_are_checked()
    {
        var package = Encoding.UTF8.GetBytes("pretend package");
        var index = $$"""
            {"format":"cuebot-gallery","version":1,"items":[
              {{Entry("good-one", package)}},
              {{Entry("elsewhere", package, path: "items/other/package.cuebot-overlay")}},
              {{Entry("Bad Id", package)}},
              {{Entry("wrong-hash", package, sha: new string('0', 64))}},
              {{Entry("weird-kind", package, kind: "script")}}]}
            """;
        var http = new FakeGallery(new()
        {
            ["index.json"] = Encoding.UTF8.GetBytes(index),
            ["items/good-one/package.cuebot-overlay"] = package,
            ["items/wrong-hash/package.cuebot-overlay"] = package,
            ["items/good-one/preview.png"] = [1, 2, 3],
        });
        var gallery = new OverlayGallery(new HttpClient(http), Base);

        var items = await gallery.ListAsync();
        Assert.Equal(["good-one", "wrong-hash"], items.Select(i => i.Id));
        var good = items[0];
        Assert.Equal(("good-one name", "Someone", "overlay", "MIT"), (good.Name, good.Author, good.Kind, good.License));
        Assert.Equal(["a", "b"], good.Tags);

        using var downloaded = await gallery.DownloadAsync(good);
        Assert.Equal(package, downloaded.ToArray());
        Assert.Equal([1, 2, 3], await gallery.PreviewAsync(good));
        var error = await Assert.ThrowsAsync<GalleryException>(() => gallery.DownloadAsync(items[1]));
        Assert.Contains("did not download as the gallery lists it", error.Message);
        Assert.All(http.Requested, p => Assert.True(p == "index.json" || p.StartsWith("items/good-one/") || p.StartsWith("items/wrong-hash/"), p));

        Assert.Equal("https://github.com/TechDaddyKB/cuebot-gallery/issues/new?template=report.yml&item=good-one", OverlayGallery.ReportUrl(good));
    }

    [Fact]
    public async Task Unreadable_or_missing_indexes_and_oversized_files_give_a_message()
    {
        var gallery = new OverlayGallery(new HttpClient(new FakeGallery(new() { ["index.json"] = "nope"u8.ToArray() })), Base);
        Assert.Equal("The gallery's index is not readable.", (await Assert.ThrowsAsync<GalleryException>(() => gallery.ListAsync())).Message);

        var missing = new OverlayGallery(new HttpClient(new FakeGallery([])), Base);
        Assert.Equal("The gallery answered 404. Try again later.", (await Assert.ThrowsAsync<GalleryException>(() => missing.ListAsync())).Message);

        var small = new byte[10];
        var item = OverlayGallery.Parse((System.Text.Json.Nodes.JsonNode.Parse(Entry("big", small))!.AsObject()))!;
        var big = new OverlayGallery(new HttpClient(new FakeGallery(new() { ["items/big/package.cuebot-overlay"] = new byte[11] })), Base);
        Assert.Equal("The gallery sent more than expected.", (await Assert.ThrowsAsync<GalleryException>(() => big.DownloadAsync(item))).Message);
    }
}
