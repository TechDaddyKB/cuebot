using Cuebot.Rumble.LiveApi;

namespace Cuebot.Rumble.Tests.Support;

/// <summary>Builds Live Stream API snapshots for diffing tests.</summary>
internal static class Snapshots
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static LiveStreamSnapshot Channel(
        IReadOnlyList<ApiFollower>? followers = null,
        IReadOnlyList<ApiSubscriber>? subscribers = null,
        IReadOnlyList<ApiGiftedSub>? gifts = null,
        ApiLivestream? live = null,
        DateTimeOffset? now = null) =>
        new(now ?? T0, "CuebotDev", null, followers?.Count ?? 0, subscribers?.Count ?? 0,
            followers ?? [], subscribers ?? [], gifts ?? [], live is null ? [] : [live]);

    public static ApiLivestream Stream(string id = "4ponnu", DateTimeOffset? createdOn = null,
        IReadOnlyList<ApiChatMessage>? chat = null, IReadOnlyList<ApiRant>? rants = null) =>
        new(id, "dev testing", true, createdOn ?? T0, 3, chat ?? [], rants ?? []);

    public static ApiFollower Follower(string name, int minute) => new(name, T0.AddMinutes(minute));

    public static ApiChatMessage ChatLine(string user, string text, int minute) => new(user, text, T0.AddMinutes(minute), []);

    public static ApiRant Rant(string user, string text, int minute, long cents) => new(user, text, T0.AddMinutes(minute), cents, []);
}
