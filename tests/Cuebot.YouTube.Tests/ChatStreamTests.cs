using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Cuebot.Core.Events;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Api.Grpc;
using Cuebot.YouTube.Auth;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Tests.Support;
using Google.Protobuf;
using Grpc.Core;
using static Cuebot.YouTube.Tests.Support.ChatJson;

namespace Cuebot.YouTube.Tests;

public sealed class ChatStreamTests
{
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly CancellationTokenSource _done = new();

    private TestApi Create(FakeChatStream stream, out ChatPoller reader, Func<RecordedRequest, HttpResponseMessage>? http = null)
    {
        var api = new TestApi(http ?? (_ =>
        {
            _done.Cancel();
            return FakeHttpHandler.Json(Page("end", 0));
        }));
        stream.Done = _done;
        var ctx = new ChatContext("b1", "chat1", new ChatterDirectory());
        reader = new ChatPoller(api.Api, api.Broadcaster, ctx, _published.Enqueue, api.Timings, api.Time, () => 0, api.Log, stream: stream);
        return api;
    }

    private string[] Messages() => _published.Where(e => e.Type == YouTubeEventTypes.ChatMessage).Select(e => (string)e.Args["message"]!).ToArray();

    private static string Published(string itemJson, string time) => itemJson.Replace("2026-03-10T18:00:00Z", time, StringComparison.Ordinal);

    private static ChatPage StreamPage(string token, params string[] items) => ChatPage.Parse(Parse(Page(token, 0, items)));

    // ------------------------------------------------------------------ reader

    [Fact]
    public async Task History_sent_when_the_stream_opens_is_not_replayed_but_new_messages_are()
    {
        var stream = new FakeChatStream();
        stream.Connect(
            StreamPage("t1", Published(Text("old1", "history 1"), "2026-03-10T17:50:00Z")),
            StreamPage("t2", Published(Text("old2", "history 2"), "2026-03-10T17:59:59Z")),
            StreamPage("t3", Text("new1", "fresh"), Published(Text("new2", "later"), "2026-03-10T18:00:05Z")));
        using var api = Create(stream, out var reader);

        var reason = await reader.RunAsync(_done.Token);

        Assert.Equal(ChatEndReason.Cancelled, reason);
        Assert.Equal(["fresh", "later"], Messages());
        Assert.Equal(2, reader.StreamConnections);   // the second is the fake ending the test
        Assert.Empty(api.Http.To("/liveChat/messages"));
    }

    [Fact]
    public async Task Closed_stream_reconnects_from_the_last_page_token_without_duplicates()
    {
        var stream = new FakeChatStream();
        stream.Connect(StreamPage("t1"), StreamPage("t2", Text("m1", "one")));
        stream.Connect(StreamPage("t3", Text("m1", "one"), Text("m2", "two")));
        using var api = Create(stream, out var reader);

        await reader.RunAsync(_done.Token);

        Assert.Equal(["one", "two"], Messages());
        Assert.Equal([null, "t2", "t3"], stream.Tokens);
        Assert.Contains(TestApi.Fast.MinimumChatPoll, ((FakeTimeProvider)api.Time).Delays);   // closed quickly: paused first
    }

    [Fact]
    public async Task Stream_that_never_opens_falls_back_to_polling()
    {
        var stream = new FakeChatStream();
        for (var i = 0; i < ChatPoller.StreamAttemptsBeforePolling; i++) stream.Fail(new HttpRequestException("no route"));
        var polls = new ConcurrentQueue<HttpResponseMessage>();
        polls.Enqueue(FakeHttpHandler.Json(Page("p2", 0)));
        polls.Enqueue(FakeHttpHandler.Json(Page("p3", 0, Text("m1", "polled"))));
        using var api = Create(stream, out var reader, _ =>
        {
            if (polls.TryDequeue(out var answer)) return answer;
            _done.Cancel();
            return FakeHttpHandler.Json(Page("end", 0));
        });

        await reader.RunAsync(_done.Token);

        Assert.Equal(ChatPoller.StreamAttemptsBeforePolling, reader.StreamConnections);
        Assert.Equal(["polled"], Messages());
        Assert.True(reader.Polls >= 2);
    }

    [Fact]
    public async Task Errors_after_the_stream_worked_reconnect_instead_of_polling()
    {
        var stream = new FakeChatStream();
        stream.Connect(StreamPage("t1"));
        for (var i = 0; i < ChatPoller.StreamAttemptsBeforePolling + 1; i++) stream.Fail(new HttpRequestException("reset"));
        stream.Connect(StreamPage("t2", Text("m1", "back")));
        using var api = Create(stream, out var reader);

        await reader.RunAsync(_done.Token);

        Assert.Equal(["back"], Messages());
        Assert.Empty(api.Http.To("/liveChat/messages"));
        Assert.Equal(["t1", "t1", "t1", "t1", "t1"], stream.Tokens[1..^1]);
    }

    [Theory]
    [InlineData("liveChatEnded", "ChatEnded")]
    [InlineData("liveChatNotFound", "Unavailable")]
    [InlineData("liveChatDisabled", "Unavailable")]
    public async Task Chat_gone_errors_stop_the_stream(string reason, string expected)
    {
        var stream = new FakeChatStream();
        stream.Fail(new YouTubeApiException("gone", 403, reason));
        using var api = Create(stream, out var reader);

        Assert.Equal(Enum.Parse<ChatEndReason>(expected), await reader.RunAsync(_done.Token));
        Assert.Equal(1, reader.StreamConnections);
    }

    [Fact]
    public async Task Offline_at_or_chat_ended_event_ends_the_streamed_chat()
    {
        var stream = new FakeChatStream();
        stream.Connect(StreamPage("t1"), ChatPage.Parse(Parse("""{"nextPageToken":"t2","offlineAt":"2026-03-10T19:00:00Z","items":[]}""")));
        using var api = Create(stream, out var reader);
        Assert.Equal(ChatEndReason.ChatEnded, await reader.RunAsync(_done.Token));

        var stream2 = new FakeChatStream();
        stream2.Connect(StreamPage("t1"), StreamPage("t2", Item("e1", "chatEndedEvent", "")));
        using var api2 = Create(stream2, out var reader2);
        Assert.Equal(ChatEndReason.ChatEnded, await reader2.RunAsync(_done.Token));
    }

    // ------------------------------------------------------------------ protobuf to REST JSON

    private static LiveChatMessage Message(string id, LiveChatMessageSnippet.Types.TypeWrapper.Types.Type type) => new()
    {
        Id = id,
        Snippet = new LiveChatMessageSnippet { Type = type, LiveChatId = "chat1", AuthorChannelId = Alice, PublishedAt = "2026-03-10T18:00:01Z", DisplayMessage = "display " + id },
        AuthorDetails = new LiveChatMessageAuthorDetails { ChannelId = Alice, DisplayName = "Alice", IsChatSponsor = true, ProfileImageUrl = "https://yt3.test/a.jpg" },
    };

    private static IReadOnlyList<BotEvent> Map(LiveChatMessage message) =>
        ChatItemMapper.Map(ChatStreamPages.ToJson(message), new ChatContext("b1", "chat1", new ChatterDirectory())).Events;

    [Theory]
    [InlineData("TEXT_MESSAGE_EVENT", "textMessageEvent")]
    [InlineData("TOMBSTONE", "tombstone")]
    [InlineData("GIFT_MEMBERSHIP_RECEIVED_EVENT", "giftMembershipReceivedEvent")]
    public void Proto_enum_names_become_rest_spellings(string proto, string rest) => Assert.Equal(rest, ChatStreamPages.Camel(proto));

    [Fact]
    public void Streamed_text_message_maps_like_a_polled_one()
    {
        var m = Message("m1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.TextMessageEvent);
        m.Snippet.TextMessageDetails = new LiveChatTextMessageDetails { MessageText = "hello from the stream" };

        var e = Assert.Single(Map(m));

        Assert.Equal(YouTubeEventTypes.ChatMessage, e.Type);
        Assert.Equal("hello from the stream", e.Args["message"]);
        Assert.Equal("Alice", e.Args["user"]);
    }

    [Fact]
    public void Streamed_super_chat_keeps_its_amount()
    {
        var m = Message("s1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.SuperChatEvent);
        m.Snippet.SuperChatDetails = new LiveChatSuperChatDetails
        {
            AmountMicros = 5_000_000, Currency = "USD", AmountDisplayString = "$5.00", UserComment = "thanks", Tier = 2,
        };

        var e = Assert.Single(Map(m));

        Assert.Equal(YouTubeEventTypes.SuperChat, e.Type);
        Assert.Equal("thanks", e.Args["message"]);
        Assert.Equal(5d, e.Args["amount"]);
        Assert.Equal(5_000_000L, e.Args["amountMicros"]);
        Assert.Equal("$5.00", e.Args["amountText"]);
        Assert.Equal(2, e.Args["tier"]);
    }

    [Fact]
    public void Streamed_timeout_reads_ban_type_and_duration()
    {
        var m = Message("b1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.UserBannedEvent);
        m.Snippet.UserBannedDetails = new LiveChatUserBannedMessageDetails
        {
            BanType = LiveChatUserBannedMessageDetails.Types.BanTypeWrapper.Types.BanType.Temporary,
            BanDurationSeconds = 300,
            BannedUserDetails = new ChannelProfileDetails { ChannelId = Bob, DisplayName = "Bob" },
        };

        var events = Map(m);

        var e = events.First(x => x.Type == YouTubeEventTypes.UserTimedOut);
        Assert.Equal("Bob", e.Args["user"]);
        Assert.Equal(300L, e.Args["duration"]);
    }

    [Fact]
    public void Streamed_closed_poll_is_reported_closed()
    {
        var m = Message("p1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.PollEvent);
        var meta = new LiveChatPollDetails.Types.PollMetadata { QuestionText = "Best snack?" };
        meta.Options.Add(new LiveChatPollDetails.Types.PollMetadata.Types.PollOption { OptionText = "Chips", Tally = 3 });
        meta.Options.Add(new LiveChatPollDetails.Types.PollMetadata.Types.PollOption { OptionText = "Fruit", Tally = 5 });
        m.Snippet.PollDetails = new LiveChatPollDetails
        {
            Metadata = meta, Status = LiveChatPollDetails.Types.PollStatusWrapper.Types.PollStatus.Closed,
        };

        var e = Assert.Single(Map(m));

        Assert.Equal(YouTubeEventTypes.PollClosed, e.Type);
        Assert.Equal("Fruit", e.Args["pollWinningChoice"]);
        Assert.Equal(8L, e.Args["pollTotalVotes"]);
    }

    [Fact]
    public void Offline_at_and_page_token_carry_over_to_the_page()
    {
        var response = new LiveChatMessageListResponse { NextPageToken = "next", OfflineAt = "2026-03-10T19:00:00Z" };
        response.Items.Add(Message("m1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.TextMessageEvent));

        var page = ChatStreamPages.ToPage(response);

        Assert.Equal("next", page.NextPageToken);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 19, 0, 0, TimeSpan.Zero), page.OfflineAt);
        Assert.Single(page.Items);
    }

    // ------------------------------------------------------------------ gRPC transport

    [Theory]
    [InlineData(StatusCode.NotFound, "", "liveChatNotFound")]
    [InlineData(StatusCode.FailedPrecondition, "", "liveChatDisabled")]
    [InlineData(StatusCode.ResourceExhausted, "Quota exceeded for quota metric 'Queries'", "quotaExceeded")]
    [InlineData(StatusCode.ResourceExhausted, "slow down", "rateLimitExceeded")]
    [InlineData(StatusCode.Unavailable, "", "")]
    public void Grpc_errors_become_rest_reasons(StatusCode code, string detail, string reason) =>
        Assert.Equal(reason, GrpcChatStream.ToApiException(new RpcException(new Status(code, detail))).Reason);

    [Fact]
    public async Task Stream_call_sends_the_chat_id_parts_and_bearer_token_and_spends_quota_once()
    {
        var grpc = new GrpcFake();
        grpc.Answer(Response("t1"), Response("t2", Message("m1", LiveChatMessageSnippet.Types.TypeWrapper.Types.Type.TextMessageEvent)));
        using var api = new TestApi(_ => FakeHttpHandler.Json("{}"));
        using var stream = new GrpcChatStream(api.Quota, new Uri("https://grpc.test"), grpc);

        var pages = await ReadAll(stream.ReadAsync(api.Broadcaster, "chat1", "resume", CancellationToken.None));

        Assert.Equal(["t1", "t2"], pages.Select(p => p.NextPageToken));
        var request = Assert.Single(grpc.Requests);
        Assert.Equal("/youtube.api.v3.V3DataLiveChatMessageService/StreamList", request.Path);
        Assert.Equal("Bearer BROADCASTER-TOKEN", request.Authorization);
        Assert.Equal("chat1", request.Body.LiveChatId);
        Assert.Equal("resume", request.Body.PageToken);
        Assert.Equal(["id", "snippet", "authorDetails"], request.Body.Part);
        Assert.Equal(QuotaCosts.LiveChatMessagesStream, api.Quota.UsedToday);
    }

    [Fact]
    public async Task Rejected_token_is_refreshed_once_and_the_stream_reopened()
    {
        var grpc = new GrpcFake();
        grpc.Status(StatusCode.Unauthenticated);
        grpc.Answer(Response("t1"));
        using var api = new TestApi(_ => FakeHttpHandler.Json("""{"access_token":"FRESH","expires_in":3600}"""));
        using var stream = new GrpcChatStream(api.Quota, new Uri("https://grpc.test"), grpc);

        var pages = await ReadAll(stream.ReadAsync(api.Broadcaster, "chat1", null, CancellationToken.None));

        Assert.Single(pages);
        Assert.Equal(["Bearer BROADCASTER-TOKEN", "Bearer FRESH"], grpc.Requests.Select(r => r.Authorization));
        Assert.Equal("RT-BROADCASTER-TOKEN", api.Http.To("/token").Single().Form("refresh_token"));
    }

    [Fact]
    public async Task Quota_error_marks_the_tracker_exhausted()
    {
        var grpc = new GrpcFake();
        grpc.Status(StatusCode.ResourceExhausted, "The request cannot be completed because you have exceeded your quota.");
        using var api = new TestApi(_ => FakeHttpHandler.Json("{}"));
        using var stream = new GrpcChatStream(api.Quota, new Uri("https://grpc.test"), grpc);

        var ex = await Assert.ThrowsAsync<YouTubeApiException>(() => ReadAll(stream.ReadAsync(api.Broadcaster, "chat1", null, CancellationToken.None)));

        Assert.True(ex.IsQuotaExceeded);
        Assert.NotNull(api.Quota.ExhaustedUntil);
    }

    private static LiveChatMessageListResponse Response(string token, params LiveChatMessage[] items)
    {
        var r = new LiveChatMessageListResponse { NextPageToken = token };
        r.Items.Add(items);
        return r;
    }

    private static async Task<List<ChatPage>> ReadAll(IAsyncEnumerable<ChatPage> pages)
    {
        var list = new List<ChatPage>();
        await foreach (var page in pages) list.Add(page);
        return list;
    }

    /// <summary>Scripted stream connections; once the script is used up it cancels the test's token.</summary>
    private sealed class FakeChatStream : IChatStream
    {
        private readonly Queue<Func<IReadOnlyList<ChatPage>>> _connections = new();

        public CancellationTokenSource Done { get; set; } = new();
        public List<string?> Tokens { get; } = [];

        public void Connect(params ChatPage[] pages) => _connections.Enqueue(() => pages);

        public void Fail(Exception ex) => _connections.Enqueue(() => throw ex);

        public async IAsyncEnumerable<ChatPage> ReadAsync(AccountTokens account, string liveChatId, string? pageToken,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Tokens.Add(pageToken);
            await Task.Yield();
            if (!_connections.TryDequeue(out var next))
            {
                await Done.CancelAsync();
                ct.ThrowIfCancellationRequested();
                yield break;
            }
            foreach (var page in next()) yield return page;
        }
    }

    /// <summary>Answers gRPC calls in-process: framed protobuf responses with a grpc-status trailer.</summary>
    private sealed class GrpcFake : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _answers = new();

        public List<(string Path, string Authorization, LiveChatMessageListRequest Body)> Requests { get; } = [];

        public void Answer(params LiveChatMessageListResponse[] responses) => _answers.Enqueue(() =>
        {
            using var body = new MemoryStream();
            foreach (var r in responses) Frame(body, r.ToByteArray());
            return Reply(body.ToArray(), StatusCode.OK, "");
        });

        public void Status(StatusCode code, string detail = "") => _answers.Enqueue(() => Reply([], code, detail));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var body = LiveChatMessageListRequest.Parser.ParseFrom(bytes.AsSpan(5));
            Requests.Add((request.RequestUri!.AbsolutePath, string.Join(",", request.Headers.GetValues("authorization")), body));
            var reply = _answers.Dequeue()();
            reply.RequestMessage = request;
            return reply;
        }

        private static void Frame(Stream s, byte[] message)
        {
            Span<byte> header = stackalloc byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)message.Length);
            s.Write(header);
            s.Write(message);
        }

        private static HttpResponseMessage Reply(byte[] body, StatusCode code, string detail)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, Version = HttpVersion.Version20 };
            response.TrailingHeaders.Add("grpc-status", ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (detail.Length > 0) response.TrailingHeaders.Add("grpc-message", detail);
            return response;
        }
    }
}
