namespace Cuebot.Twitch.Tests.Support;

/// <summary>Builders for EventSub WebSocket messages shaped like Twitch's.</summary>
public static class EventSubMessages
{
    private const string Timestamp = "2026-01-01T12:00:00.000000000Z";

    public static string Welcome(string sessionId, int keepaliveSeconds = 10) => Fill(
        """
        {"metadata":{"message_id":"@id","message_type":"session_welcome","message_timestamp":"@time"},
         "payload":{"session":{"id":"@session","status":"connected","connected_at":"@time",
           "keepalive_timeout_seconds":@keepalive,"reconnect_url":null,"recovery_url":null}}}
        """,
        ("@session", sessionId), ("@keepalive", keepaliveSeconds.ToString()));

    public static string Keepalive() => Fill(
        """
        {"metadata":{"message_id":"@id","message_type":"session_keepalive","message_timestamp":"@time"},"payload":{}}
        """);

    public static string Reconnect(string sessionId, Uri reconnectUrl) => Fill(
        """
        {"metadata":{"message_id":"@id","message_type":"session_reconnect","message_timestamp":"@time"},
         "payload":{"session":{"id":"@session","status":"reconnecting","keepalive_timeout_seconds":null,
           "reconnect_url":"@url","connected_at":"@time"}}}
        """,
        ("@session", sessionId), ("@url", reconnectUrl.ToString()));

    public static string Notification(string messageId, string subscriptionType, string eventJson) => Fill(
        """
        {"metadata":{"message_id":"@message","message_type":"notification","message_timestamp":"@time",
           "subscription_type":"@type","subscription_version":"1"},
         "payload":{"subscription":{"id":"sub-1","status":"enabled","type":"@type","version":"1",
           "condition":{"broadcaster_user_id":"1337"},"transport":{"method":"websocket","session_id":"s1"},
           "created_at":"@time","cost":0},
           "event":@event}}
        """,
        ("@message", messageId), ("@type", subscriptionType), ("@event", eventJson));

    public static string Revocation(string subscriptionType, string status) => Fill(
        """
        {"metadata":{"message_id":"@id","message_type":"revocation","message_timestamp":"@time",
           "subscription_type":"@type","subscription_version":"1"},
         "payload":{"subscription":{"id":"sub-1","status":"@status","type":"@type","version":"1",
           "condition":{"broadcaster_user_id":"1337"},"transport":{"method":"websocket","session_id":"s1"},
           "created_at":"@time","cost":0}}}
        """,
        ("@type", subscriptionType), ("@status", status));

    public const string FollowEvent =
        """
        {"user_id":"1234","user_login":"cool_user","user_name":"Cool_User","broadcaster_user_id":"1337",
         "broadcaster_user_login":"cooler_user","broadcaster_user_name":"Cooler_User","followed_at":"2026-01-01T12:00:00Z"}
        """;

    private static string Fill(string template, params (string Placeholder, string Value)[] values)
    {
        var text = template.Replace("@id", Guid.NewGuid().ToString()).Replace("@time", Timestamp);
        foreach (var (placeholder, value) in values) text = text.Replace(placeholder, value);
        return text;
    }
}
