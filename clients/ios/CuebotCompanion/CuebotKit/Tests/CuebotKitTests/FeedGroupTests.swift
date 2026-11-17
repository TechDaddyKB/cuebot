import Testing
@testable import CuebotKit

struct FeedGroupTests {
    @Test func sortsCuebotsKindsAndOtherNamesIntoGroups() {
        #expect(FeedGroup.of("Follow") == .follows)
        #expect(FeedGroup.of("GiftSubscription") == .subs)
        #expect(FeedGroup.of("membership") == .subs)
        #expect(FeedGroup.of("Tip") == .tips)
        #expect(FeedGroup.of("Cheer") == .tips)
        #expect(FeedGroup.of("super_chat") == .tips)
        #expect(FeedGroup.of("Rant") == .tips)
        #expect(FeedGroup.of("Raid") == .raids)
        #expect(FeedGroup.of("Redemption") == .redemptions)
        #expect(FeedGroup.of("StreamOnline") == .other)
        #expect(FeedGroup.of("") == .other)
    }

    @Test func keysRoundTripAndUnknownKeysAreSkipped() {
        #expect(FeedGroup.encode([.raids, .follows]) == "follows,raids")
        #expect(FeedGroup.parse("raids, follows,nonsense") == [.raids, .follows])
        #expect(FeedGroup.parse("").isEmpty)
        #expect(FeedGroup.defaults == [.follows, .subs, .tips, .raids])
    }

    @Test func filtersInTheFeedsOrder() {
        let feed = [FeedItem(platform: "Twitch", kind: "Raid", user: "c"), FeedItem(platform: "Twitch", kind: "Redemption", user: "r"),
                    FeedItem(platform: "Kick", kind: "Tip", user: "b")]
        #expect(FeedGroup.filter(feed, groups: FeedGroup.defaults).map(\.user) == ["c", "b"])
        #expect(FeedGroup.filter(feed, groups: FeedGroup.defaults, limit: 1).map(\.user) == ["c"])
        #expect(FeedGroup.filter(feed, groups: [.redemptions]).map(\.user) == ["r"])
    }

    @Test func kindsReadAsWords() {
        #expect(FeedGroup.kindLabel("GiftSubscription") == "Gift subscription")
        #expect(FeedGroup.kindLabel("super_chat") == "Super chat")
        #expect(FeedGroup.kindLabel("") == "")
    }
}
