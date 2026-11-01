using Cuebot.Core;
using Cuebot.Discord.Gateway;
using Cuebot.Discord.Tests.Support;

namespace Cuebot.Discord.Tests;

public sealed class GatewayTests : IAsyncDisposable
{
    private readonly TestEnvironment _env = new();

    public ValueTask DisposeAsync() => _env.DisposeAsync();

    [Fact]
    public async Task Hello_identify_ready_connects_with_the_gateway_url_from_rest_and_basic_intents()
    {
        await _env.Bot.ConnectAsync();
        var socket = await _env.Gateway.NextAsync();
        Assert.Equal("wss://gateway.example/?v=10&encoding=json", socket.Url!.AbsoluteUri);
        Assert.Single(_env.Http.Matching(HttpMethod.Get, "gateway/bot"));
        Assert.Equal("Bot " + TestToken.Value, _env.Http.Matching(HttpMethod.Get, "gateway/bot")[0].Authorization);

        socket.Hello();
        var identify = await socket.NextSentAsync(GatewayOp.Identify);
        Assert.Equal(TestToken.Value, identify["d"]!["token"]!.GetValue<string>());
        Assert.Equal(1 | 512, identify["d"]!["intents"]!.GetValue<int>());
        Assert.Equal("Cuebot", identify["d"]!["properties"]!["browser"]!.GetValue<string>());

        socket.Ready();
        socket.GuildCreate();
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Connected, "connected");
        var connected = await _env.WaitForEventAsync(DiscordEventTypes.Connected);
        Assert.Equal("CuebotTestBot", connected.Args["botName"]);
        Assert.Equal("100000000000000001", _env.Settings.Value.ApplicationId);
        Assert.Contains("CuebotTestBot", _env.Bot.StatusMessage);
        await Wait.UntilAsync(() => _env.Bot.Guilds.Count == 1, "guild");
        var guild = _env.Bot.Guilds[0];
        Assert.Equal("My Server", guild.Name);
        Assert.Equal(["general", "announcements"], guild.Channels.Where(c => c.IsText).Select(c => c.Name));
        Assert.Equal(["Live notifications"], DiscordBotService.AssignableRoleNames(guild));
    }

    [Fact]
    public async Task Privileged_intents_are_requested_only_when_ticked()
    {
        _env.Settings.Value.MessageContentIntent = true;
        _env.Settings.Value.ServerMembersIntent = true;
        await _env.Bot.ConnectAsync();
        var socket = await _env.Gateway.NextAsync();
        socket.Hello();
        var identify = await socket.NextSentAsync(GatewayOp.Identify);
        Assert.Equal(1 | 2 | 512 | 32768, identify["d"]!["intents"]!.GetValue<int>());
    }

    [Fact]
    public async Task Heartbeats_carry_the_last_sequence_and_a_missing_ack_resumes_on_a_new_connection()
    {
        await _env.Bot.ConnectAsync();
        var socket = await _env.Gateway.NextAsync();
        socket.Hello(intervalMs: 100);
        await socket.NextSentAsync(GatewayOp.Identify);
        socket.Ready();   // sequence 1
        socket.GuildCreate();   // sequence 2
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Connected, "connected");

        var beat = await socket.NextSentAsync(GatewayOp.Heartbeat);
        Assert.True(beat["d"] is null || beat["d"]!.GetValue<int>() >= 1);
        // No ACK: the next beat finds the connection "zombied" and closes it without 1000, then resumes.
        var resumed = await _env.Gateway.NextAsync();
        Assert.Equal(4000, socket.ClientCloseCode);
        Assert.Equal("wss://resume.example/?v=10&encoding=json", resumed.Url!.AbsoluteUri);
        resumed.Hello();
        var resume = await resumed.NextSentAsync(GatewayOp.Resume);
        Assert.Equal("session-1", resume["d"]!["session_id"]!.GetValue<string>());
        Assert.Equal(2, resume["d"]!["seq"]!.GetValue<int>());
        Assert.Equal(TestToken.Value, resume["d"]!["token"]!.GetValue<string>());
        resumed.Dispatch("RESUMED", new { });
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Connected, "resumed");
        Assert.Single(_env.Events(DiscordEventTypes.Disconnected));
        Assert.Equal(2, _env.Events(DiscordEventTypes.Connected).Length);
        // Guilds survive a resume.
        Assert.Single(_env.Bot.Guilds);
    }

    [Fact]
    public async Task Heartbeat_acks_keep_the_connection_and_op1_gets_an_immediate_beat()
    {
        await _env.Bot.ConnectAsync();
        var socket = await _env.Gateway.NextAsync();
        socket.Hello(intervalMs: 60);
        await socket.NextSentAsync(GatewayOp.Identify);
        socket.Ready();
        for (var i = 0; i < 3; i++)
        {
            await socket.NextSentAsync(GatewayOp.Heartbeat);
            socket.Push(new { op = 11 });
        }
        socket.Push(new { op = 1 });
        var beat = await socket.NextSentAsync(GatewayOp.Heartbeat);
        Assert.Equal(1, beat["d"]!.GetValue<int>());
        Assert.Single(_env.Gateway.Urls);
        Assert.Null(socket.ClientCloseCode);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_and_op7_reconnect_resumes_too()
    {
        var socket = await _env.ConnectReadyAsync();
        socket.CloseFromServer(4000);
        var second = await _env.Gateway.NextAsync();
        Assert.Equal("wss://resume.example/?v=10&encoding=json", second.Url!.AbsoluteUri);
        second.Hello();
        await second.NextSentAsync(GatewayOp.Resume);
        second.Dispatch("RESUMED", new { });
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Connected, "resumed");

        second.Push(new { op = 7, d = (object?)null });
        var third = await _env.Gateway.NextAsync();
        Assert.Equal(4000, second.ClientCloseCode);
        third.Hello();
        await third.NextSentAsync(GatewayOp.Resume);
    }

    [Fact]
    public async Task Invalid_session_false_identifies_again_on_the_gateway_url_and_true_resumes()
    {
        var socket = await _env.ConnectReadyAsync();
        socket.Push(new { op = 9, d = true });
        var second = await _env.Gateway.NextAsync();
        Assert.Contains("resume.example", second.Url!.Host);
        second.Hello();
        await second.NextSentAsync(GatewayOp.Resume);

        second.Push(new { op = 9, d = false });
        var third = await _env.Gateway.NextAsync();
        Assert.Equal("gateway.example", third.Url!.Host);
        third.Hello();
        var identify = await third.NextSentAsync(GatewayOp.Identify);
        Assert.Equal(TestToken.Value, identify["d"]!["token"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(4007)]
    [InlineData(4009)]
    public async Task Session_ending_close_codes_start_a_fresh_session(int code)
    {
        var socket = await _env.ConnectReadyAsync();
        socket.CloseFromServer(code);
        var second = await _env.Gateway.NextAsync();
        Assert.Equal("gateway.example", second.Url!.Host);
        second.Hello();
        await second.NextSentAsync(GatewayOp.Identify);
    }

    [Fact]
    public async Task Close_4004_stops_with_a_plain_bad_token_message()
    {
        var socket = await _env.ConnectReadyAsync();
        socket.CloseFromServer(4004);
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Error, "error");
        Assert.Contains("Reset Token", _env.Bot.StatusMessage);
        await Task.Delay(150);
        Assert.Single(_env.Gateway.Urls);
        Assert.False(_env.Bot.IsRunning);
    }

    [Fact]
    public async Task Close_4014_names_the_portal_switch_to_turn_on()
    {
        _env.Settings.Value.MessageContentIntent = true;
        await _env.Bot.ConnectAsync();
        var socket = await _env.Gateway.NextAsync();
        socket.Hello();
        await socket.NextSentAsync(GatewayOp.Identify);
        socket.CloseFromServer(4014);
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Error, "error");
        Assert.Contains("Message Content Intent", _env.Bot.StatusMessage);
        Assert.DoesNotContain("Server Members", _env.Bot.StatusMessage);
        Assert.Contains("Privileged Gateway Intents", _env.Bot.StatusMessage);
        await Task.Delay(150);
        Assert.Single(_env.Gateway.Urls);
    }

    [Fact]
    public async Task A_refused_token_on_gateway_bot_stops_before_connecting()
    {
        _env.Http.Handler = r => r.Path == "gateway/bot"
            ? FakeDiscordHttp.Json(new { message = "401: Unauthorized", code = 0 }, System.Net.HttpStatusCode.Unauthorized)
            : null;
        await _env.Bot.ConnectAsync();
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Error, "error");
        Assert.Contains("Reset Token", _env.Bot.StatusMessage);
        Assert.Empty(_env.Gateway.Urls);
    }

    [Fact]
    public async Task Connect_without_a_token_explains_what_to_do()
    {
        await using var env = new TestEnvironment(withToken: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Bot.ConnectAsync());
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Error, env.Bot.State);
    }

    [Fact]
    public async Task Disconnect_closes_with_1000_and_publishes_disconnected()
    {
        var socket = await _env.ConnectReadyAsync();
        await _env.Bot.DisconnectAsync();
        Assert.Equal(1000, socket.ClientCloseCode);
        Assert.Equal(ConnectionState.Disconnected, _env.Bot.State);
        await _env.WaitForEventAsync(DiscordEventTypes.Disconnected);
    }

    [Fact]
    public async Task Set_status_sends_a_presence_update_and_keeps_it_for_the_next_identify()
    {
        var socket = await _env.ConnectReadyAsync();
        await _env.Bot.SetPresenceAsync("idle", ActivityTypes.Watching, "my stream", null, CancellationToken.None);
        var update = await socket.NextSentAsync(GatewayOp.PresenceUpdate);
        Assert.Equal("idle", update["d"]!["status"]!.GetValue<string>());
        Assert.Equal(3, update["d"]!["activities"]![0]!["type"]!.GetValue<int>());
        Assert.Equal("my stream", update["d"]!["activities"]![0]!["name"]!.GetValue<string>());

        socket.CloseFromServer(4009);
        var second = await _env.Gateway.NextAsync();
        second.Hello();
        var identify = await second.NextSentAsync(GatewayOp.Identify);
        Assert.Equal("idle", identify["d"]!["presence"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Messages_and_member_joins_become_events_and_bot_messages_are_ignored()
    {
        var socket = await _env.ConnectReadyAsync();
        socket.Dispatch("MESSAGE_CREATE", new
        {
            id = "800000000000000001", channel_id = Ids.General, guild_id = Ids.Guild, content = "from a bot",
            author = new { id = "900000000000000001", username = "otherbot", bot = true },
        });
        socket.Dispatch("MESSAGE_CREATE", new
        {
            id = "800000000000000002", channel_id = Ids.General, guild_id = Ids.Guild, content = "hello !clip",
            author = new { id = Ids.User, username = "viewer1", global_name = "Viewer One" }, member = new { nick = "V1" },
        });
        socket.Dispatch("GUILD_MEMBER_ADD", new { guild_id = Ids.Guild, user = new { id = Ids.User, username = "viewer1" } });

        var message = await _env.WaitForEventAsync(DiscordEventTypes.MessagePosted);
        Assert.Equal("hello !clip", message.Args["message"]);
        Assert.Equal("V1", message.Args["user"]);
        Assert.Equal("viewer1", message.Args["userName"]);
        Assert.Equal("general", message.Args["channelName"]);
        Assert.Equal("My Server", message.Args["serverName"]);
        Assert.Single(_env.Events(DiscordEventTypes.MessagePosted));
        var joined = await _env.WaitForEventAsync(DiscordEventTypes.MemberJoined);
        Assert.Equal($"<@{Ids.User}>", joined.Args["mention"]);
        Assert.Equal("My Server", joined.Args["serverName"]);
    }

    [Fact]
    public async Task The_token_is_kept_out_of_the_settings_file_status_and_log()
    {
        _env.Bot.SaveSettings();
        _env.Settings.Flush();
        var socket = await _env.ConnectReadyAsync();
        socket.CloseFromServer(4004);
        await Wait.UntilAsync(() => _env.Bot.State == ConnectionState.Error, "error");
        var file = await File.ReadAllTextAsync(Path.Combine(_env.Dir, "discord-bot.json"));
        Assert.DoesNotContain(TestToken.Value, file, StringComparison.Ordinal);
        Assert.DoesNotContain(TestToken.Value, _env.Bot.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(TestToken.Value, string.Join("\n", _env.Log.Recent().Select(e => e.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public void Close_codes_and_urls_are_described_plainly()
    {
        Assert.True(GatewayProtocol.DescribeClose(4004, GatewayIntents.None).Fatal);
        Assert.True(GatewayProtocol.DescribeClose(4014, GatewayIntents.GuildMembers).Fatal);
        Assert.Contains("Server Members Intent", GatewayProtocol.DescribeClose(4014, GatewayIntents.GuildMembers).Message);
        Assert.True(GatewayProtocol.DescribeClose(4000, GatewayIntents.None).Resumable);
        Assert.False(GatewayProtocol.DescribeClose(4009, GatewayIntents.None).Resumable);
        Assert.False(GatewayProtocol.DescribeClose(4009, GatewayIntents.None).Fatal);
        Assert.True(GatewayProtocol.DescribeClose(null, GatewayIntents.None).Resumable);
        Assert.Equal("wss://x.example/?v=10&encoding=json", GatewayProtocol.WithQuery("wss://x.example").AbsoluteUri);
        Assert.Equal("wss://x.example/?v=10&encoding=json", GatewayProtocol.WithQuery("wss://x.example/?v=9").AbsoluteUri);
    }
}
