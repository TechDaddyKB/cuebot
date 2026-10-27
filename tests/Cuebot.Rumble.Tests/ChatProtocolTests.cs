using Cuebot.Rumble.Chat;
using Cuebot.Rumble.Tests.Support;

namespace Cuebot.Rumble.Tests;

public sealed class SseParserTests
{
    private static List<SseEvent> FeedAll(SseParser parser, params string[] lines) =>
        lines.Select(parser.Feed).OfType<SseEvent>().ToList();

    [Fact]
    public void Joins_multi_line_data_and_dispatches_on_blank_line()
    {
        var events = FeedAll(new SseParser(), "data: {\"a\":", "data:1}", "", "data: second", "");

        Assert.Equal(["{\"a\":\n1}", "second"], events.Select(e => e.Data));
        Assert.All(events, e => Assert.Equal("message", e.Type));
    }

    [Fact]
    public void Reads_event_names_and_ids_and_keeps_the_last_id()
    {
        var events = FeedAll(new SseParser(), "event: init", "id: 7", "data: x", "", "data: y", "");

        Assert.Equal(("init", "7"), (events[0].Type, events[0].Id));
        Assert.Equal(("message", "7"), (events[1].Type, events[1].Id));
    }

    [Fact]
    public void Ignores_keep_alive_comments_unknown_fields_and_empty_events()
    {
        var events = FeedAll(new SseParser(), ": ", "", ":keep-alive", "retry: 1000", "", "foo: bar", "data", "");

        Assert.Equal("", Assert.Single(events).Data);   // a bare "data" field is an event with empty data
    }

    [Fact]
    public void Only_one_leading_space_is_stripped()
    {
        Assert.Equal("  indented", FeedAll(new SseParser(), "data:   indented", "").Single().Data);
    }

    [Fact]
    public void Reset_drops_a_half_received_event()
    {
        var parser = new SseParser();
        parser.Feed("data: partial");
        parser.Reset();

        Assert.Equal("fresh", FeedAll(parser, "data: fresh", "").Single().Data);
    }
}

public sealed class ChatIdTests
{
    [Theory]
    [InlineData("4ponnu", 285005514L)]
    [InlineData("4PONNU", 285005514L)]
    [InlineData("0", 0L)]
    [InlineData("z", 35L)]
    [InlineData("10", 36L)]
    public void Converts_base_36_stream_ids_to_base_10_chat_ids(string streamId, long chatId) =>
        Assert.Equal(chatId, ChatId.FromStreamId(streamId));

    [Theory]
    [InlineData("")]
    [InlineData("v-12")]
    [InlineData("abcdefghijklm")]
    public void Rejects_ids_that_are_not_base_36(string streamId) => Assert.Null(ChatId.FromStreamId(streamId));
}

public sealed class ChatStreamDecoderTests
{
    [Fact]
    public void Resolves_authors_badges_colors_channels_and_rants()
    {
        var batch = new ChatStreamDecoder().Decode(TestData.Read("chat-init.json"));

        Assert.NotNull(batch);
        Assert.Equal(ChatStreamBatch.Init, batch.Type);
        Assert.Equal(3, batch.Messages.Count);

        var owner = batch.Messages[0];
        Assert.Equal(("1346698903156345836", "88707682", "CuebotDev", "welcome!"), (owner.Id, owner.UserId, owner.UserName, owner.Text));
        Assert.Equal(["admin", "verified"], owner.Badges);
        Assert.Equal("e8b44c", owner.Color);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 59, 0, TimeSpan.Zero), owner.Time);
        Assert.False(owner.IsRant);

        var rant = batch.Messages[2];
        Assert.Equal("Gina", rant.UserName);   // posted as the channel "Gina"
        Assert.Equal("777", rant.UserId);
        Assert.Equal(500, rant.RantCents);
        Assert.True(rant.IsRant);
    }

    [Fact]
    public void Remembers_authors_from_earlier_events()
    {
        var decoder = new ChatStreamDecoder();
        decoder.Decode(TestData.Read("chat-init.json"));

        var batch = decoder.Decode("""{"type":"messages","data":{"messages":[{"id":"9","user_id":"12345","text":"again","time":"2026-01-01T13:00:00+0000"}]}}""");

        Assert.Equal("Bob", Assert.Single(batch!.Messages).UserName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":{}}""")]
    public void Returns_null_for_payloads_that_are_not_chat_events(string json) => Assert.Null(new ChatStreamDecoder().Decode(json));

    [Fact]
    public void Other_event_types_carry_no_messages()
    {
        var batch = new ChatStreamDecoder().Decode("""{"type":"delete_messages","data":{"message_ids":[1,2]}}""");

        Assert.Equal(("delete_messages", 0), (batch!.Type, batch.Messages.Count));
    }
}

public sealed class ChatEchoFilterTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_record_cancels_one_matching_echo_within_the_window()
    {
        var filter = new ChatEchoFilter();
        filter.Record("Bob", "hi", T);

        Assert.False(filter.TryConsume("Bob", "other", T));
        Assert.False(filter.TryConsume("Bob", "hi", T.AddMinutes(10)));
        Assert.True(filter.TryConsume("bob", "hi", T.AddSeconds(2)));
        Assert.False(filter.TryConsume("Bob", "hi", T));
    }
}
