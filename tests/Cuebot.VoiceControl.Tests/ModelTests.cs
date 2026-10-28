using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Cuebot.Core.Logging;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Models;
using Cuebot.VoiceControl.Tests.Support;

namespace Cuebot.VoiceControl.Tests;

public sealed class WhisperModelCatalogTests
{
    [Fact]
    public void Every_model_is_pinned_to_a_revision_with_a_sha256_and_size()
    {
        Assert.Equal(WhisperModelCatalog.All.Count, WhisperModelCatalog.All.Select(m => m.Id).Distinct().Count());
        Assert.All(WhisperModelCatalog.All, m =>
        {
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.SizeBytes > 10_000_000);
            Assert.StartsWith($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{WhisperModelCatalog.Revision}/ggml-", m.Url.ToString());
            Assert.Equal(m.EnglishOnly, m.Id.Contains(".en"));
        });
    }

    [Fact]
    public void Defaults_follow_the_language()
    {
        Assert.Equal("base.en-q5_1", WhisperModelCatalog.DefaultFor(CultureInfo.GetCultureInfo("en-GB")).Id);
        Assert.Equal("base-q5_1", WhisperModelCatalog.DefaultFor(CultureInfo.GetCultureInfo("de-DE")).Id);
        Assert.Equal("Base (English)", WhisperModelCatalog.Find(" BASE.EN-Q5_1 ")!.DisplayName);
        Assert.Equal("60 MB", WhisperModelCatalog.Find("base.en-q5_1")!.SizeText);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("de", "de")]
    [InlineData("nb-NO", "no")]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("xx-not-a-culture", "auto")]
    public void Locales_map_to_whisper_language_codes(string locale, string expected) =>
        Assert.Equal(expected, WhisperLanguages.FromLocale(locale));
}

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-voice-models-").FullName;
    private readonly byte[] _body = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i * 31)).ToArray();

    private WhisperModel TestModel(byte[]? body = null, string? sha = null) => new("test", "Test model", "ggml-test.bin",
        (body ?? _body).Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(body ?? _body)), false, "");

    [Fact]
    public async Task Downloads_verify_the_checksum_report_progress_and_land_in_place()
    {
        var handler = FakeHttpHandler.Bytes(_body);
        var store = new ModelStore(_dir, new HttpClient(handler), new AppLog());
        var model = TestModel();
        var changes = 0;
        store.Changed += _ => Interlocked.Increment(ref changes);
        Assert.False(store.IsDownloaded(model));

        await store.DownloadAsync(model);

        Assert.True(store.IsDownloaded(model));
        Assert.Equal(_body, await File.ReadAllBytesAsync(store.PathFor(model)));
        Assert.False(File.Exists(store.PathFor(model) + ".part"));
        Assert.Equal(model.Url, Assert.Single(handler.Requests));
        Assert.True(changes >= 3, "start, progress and finish are reported");
        await Wait.UntilAsync(() => !store.IsDownloading(model), "download bookkeeping");
        Assert.Null(store.Progress(model));

        await store.DownloadAsync(model);   // already present: no second request
        Assert.Single(handler.Requests);
        store.Delete(model);
        Assert.False(store.IsDownloaded(model));
    }

    [Fact]
    public async Task A_corrupted_download_is_discarded()
    {
        var store = new ModelStore(_dir, new HttpClient(FakeHttpHandler.Bytes(_body)), new AppLog());
        var model = TestModel(sha: new string('0', 64));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DownloadAsync(model));
        Assert.Contains("checksum", error.Message);
        Assert.False(File.Exists(store.PathFor(model)));
        Assert.False(File.Exists(store.PathFor(model) + ".part"));
    }

    [Fact]
    public async Task Short_downloads_and_http_errors_fail_cleanly()
    {
        var model = TestModel();
        var truncated = new ModelStore(_dir, new HttpClient(FakeHttpHandler.Bytes(_body[..1000])), new AppLog());
        Assert.Contains("stopped early", (await Assert.ThrowsAsync<InvalidOperationException>(() => truncated.DownloadAsync(model))).Message);

        var missing = new ModelStore(_dir, new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), new AppLog());
        Assert.Contains("HTTP 404", (await Assert.ThrowsAsync<InvalidOperationException>(() => missing.DownloadAsync(model))).Message);
        Assert.False(missing.IsDownloaded(model));
    }

    [Fact]
    public async Task Whisper_engine_refuses_to_start_without_the_model()
    {
        var store = new ModelStore(_dir, new HttpClient(FakeHttpHandler.Bytes([])), new AppLog());
        var settings = new VoiceControlSettings { Whisper = { Model = "tiny.en-q5_1" } };
        var engine = new WhisperEngine(() => settings, store);
        Assert.True(engine.NeedsModel);
        Assert.Equal("auto", engine.GetLocales()[0]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartAsync(new RecognitionOptions("en", [], [], false), CancellationToken.None));
        Assert.Contains("not downloaded", error.Message);

        settings.Whisper.Model = "nope";
        Assert.Contains("Unknown Whisper model", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartAsync(new RecognitionOptions("en", [], [], false), CancellationToken.None))).Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
