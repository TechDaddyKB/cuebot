using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Chat.Pronouns;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Tests.ChatExtras;

public sealed class PronounTests : IDisposable
{
    private const string Base = "https://api.pronouns.alejo.io/v1/";
    private readonly AppLog _log = new();
    private readonly FakeHttp _http = new();
    private readonly ManualClock _clock = new();
    private readonly PronounService _pronouns;

    public PronounTests()
    {
        _http.Json(Base + "pronouns", """
            {"hehim":{"name":"hehim","subject":"He","object":"Him","singular":false},
             "theythem":{"name":"theythem","subject":"They","object":"Them","singular":false},
             "any":{"name":"any","subject":"Any","object":"Any","singular":true}}
            """);
        _http.Json(Base + "users/alice", """{"channel_id":"1","channel_login":"alice","pronoun_id":"theythem","alt_pronoun_id":null}""");
        _http.Json(Base + "users/bob", """{"channel_id":"2","channel_login":"bob","pronoun_id":"hehim","alt_pronoun_id":"theythem"}""");
        _http.Json(Base + "users/cat", """{"channel_id":"3","channel_login":"cat","pronoun_id":"any","alt_pronoun_id":null}""");
        _pronouns = new PronounService(_http.Client(), _log, _clock);
    }

    [Fact]
    public async Task Looks_up_primary_alt_and_singular_pronouns()
    {
        Assert.Equal("They/Them", (await _pronouns.GetAsync("Alice"))!.Display);
        var bob = (await _pronouns.GetAsync("@bob"))!;
        Assert.Equal("He/They", bob.Display);
        Assert.Equal("Him", bob.Primary.Object);
        Assert.Equal("Them", bob.Alt!.Object);
        Assert.Equal("Any", (await _pronouns.GetAsync("cat"))!.Display);
        Assert.Null(await _pronouns.GetAsync("nobody"));   // 404
        Assert.Equal(1, _http.Count(Base + "pronouns"));    // catalogue fetched once
    }

    [Fact]
    public async Task Answers_including_none_are_cached_until_they_expire()
    {
        await _pronouns.GetAsync("alice");
        await _pronouns.GetAsync("nobody");
        await _pronouns.GetAsync("ALICE");
        await _pronouns.GetAsync("nobody");
        Assert.Equal(1, _http.Count(Base + "users/alice"));
        Assert.Equal(1, _http.Count(Base + "users/nobody"));
        Assert.Equal((true, null), _pronouns.TryGetCached("nobody"));
        Assert.True(_pronouns.TryGetCached("alice").Known);

        _clock.Advance(PronounService.UserTtl + TimeSpan.FromSeconds(1));
        Assert.False(_pronouns.TryGetCached("alice").Known);
        await _pronouns.GetAsync("alice");
        Assert.Equal(2, _http.Count(Base + "users/alice"));
    }

    [Fact]
    public async Task Server_errors_return_null_without_caching()
    {
        _http.Json(Base + "users/err", "boom", System.Net.HttpStatusCode.InternalServerError);
        Assert.Null(await _pronouns.GetAsync("err"));
        Assert.False(_pronouns.TryGetCached("err").Known);
    }

    [Fact]
    public async Task Sub_action_adds_pronoun_arguments_with_optional_prefix()
    {
        using var engine = new ActionEngine(new EventBus(_log), new ProfileService(null, _log), new Registry([], []), new GlobalVariables(null, _log), _log);
        var context = new ActionRunContext(new ActionDefinition(), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            { ["user"] = "Bob", ["userName"] = "bob" }, engine, new GlobalVariables(null, _log), _log, 0);
        var handler = new AddPronounsSubAction(_pronouns);

        await handler.ExecuteAsync(context, new ParameterValues(new Dictionary<string, string> { ["user"] = "bob", ["prefix"] = "" }), default);
        Assert.Equal(true, context.Args["hasPronouns"]);
        Assert.Equal("He/They", context.Args["pronoun"]);
        Assert.Equal("He", context.Args["pronounSubject"]);
        Assert.Equal("They", context.Args["pronounAltSubject"]);

        await handler.ExecuteAsync(context, new ParameterValues(new Dictionary<string, string> { ["user"] = "nobody", ["prefix"] = "target" }), default);
        Assert.Equal(false, context.Args["targetHasPronouns"]);
        Assert.Equal("", context.Args["targetPronoun"]);

        // Unresolved %userName% falls back to the trigger's user.
        context.Args.Remove("pronoun");
        await handler.ExecuteAsync(context, new ParameterValues(new Dictionary<string, string> { ["user"] = "%userName%", ["prefix"] = "" }), default);
        Assert.Equal("He/They", context.Args["pronoun"]);

        var api = new Cuebot.Core.Scripting.ScriptApi([new PronounScriptFunctions(_pronouns)]);
        var result = (Dictionary<string, object?>)(await api.CallAsync(context, "pronouns.get", new { user = "alice" }, default))!;
        Assert.Equal("They/Them", result["display"]);
        Assert.Null(await api.CallAsync(context, "pronouns.get", new { user = "nobody" }, default));
    }

    [Fact]
    public void Moderation_lookup_finds_platform_moderators_and_announcers()
    {
        var service = new ChatModerationService([new Moderator()], [new Announcer()]);
        Assert.Equal(ChatModerationActions.DeleteMessage | ChatModerationActions.Ban, service.AvailableFor("twitch"));
        Assert.Equal(ChatModerationActions.None, service.AvailableFor("Kick"));
        Assert.Single(service.AnnouncersFor("All", ChatAnnouncementKind.Announcement));
        Assert.Empty(service.AnnouncersFor("Twitch", ChatAnnouncementKind.Action));
    }

    private sealed class Moderator : IChatModerator
    {
        public string Platform => "Twitch";
        public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Ban;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Announcer : IChatAnnouncer
    {
        public string Platform => "Twitch";
        public bool Supports(ChatAnnouncementKind kind) => kind == ChatAnnouncementKind.Announcement;
        public Task AnnounceAsync(string text, ChatAnnouncementKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose() => _log.Dispose();
}

public sealed class ChatExtrasRegistrationTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public async Task Features_register_services_sub_actions_triggers_and_script_functions()
    {
        await using var services = new ServiceCollection()
            .AddCuebotCore(new Cuebot.Core.AppPaths(_dir.Path))
            .AddChatExtrasFeatures()
            .BuildServiceProvider();
        services.GetRequiredService<Cuebot.Core.Chat.Emotes.EmoteService>();
        services.GetRequiredService<ChatModerationService>();
        var registry = services.GetRequiredService<Registry>();
        var api = services.GetRequiredService<Cuebot.Core.Scripting.ScriptApi>();

        Assert.NotNull(registry.GetSubAction("chat.refreshEmotes"));
        Assert.NotNull(registry.GetSubAction("pronouns.addForUser"));
        Assert.NotNull(registry.GetTrigger("chat.emoteAdded"));
        Assert.Contains(api.Functions, f => f.Name == "emotes.find");
        Assert.Contains(api.Functions, f => f.Name == "pronouns.get");
    }

    public void Dispose() => _dir.Dispose();
}
