package digital.techdaddy.cuebot.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FeedGroupsTest {
    @Test fun sortsCuebotsKindsAndOtherNamesIntoGroups() {
        assertEquals(FeedGroup.Follows, FeedGroup.of("Follow"))
        assertEquals(FeedGroup.Subs, FeedGroup.of("GiftSubscription"))
        assertEquals(FeedGroup.Subs, FeedGroup.of("membership"))
        assertEquals(FeedGroup.Tips, FeedGroup.of("Tip"))
        assertEquals(FeedGroup.Tips, FeedGroup.of("Cheer"))
        assertEquals(FeedGroup.Tips, FeedGroup.of("super_chat"))
        assertEquals(FeedGroup.Tips, FeedGroup.of("Rant"))
        assertEquals(FeedGroup.Raids, FeedGroup.of("Raid"))
        assertEquals(FeedGroup.Redemptions, FeedGroup.of("Redemption"))
        assertEquals(FeedGroup.Other, FeedGroup.of("StreamOnline"))
        assertEquals(FeedGroup.Other, FeedGroup.of(""))
    }

    @Test fun keysRoundTripAndUnknownKeysAreSkipped() {
        val groups = setOf(FeedGroup.Raids, FeedGroup.Follows)
        assertEquals("follows,raids", FeedGroup.encode(groups))
        assertEquals(groups, FeedGroup.parse("raids, follows,nonsense"))
        assertEquals(emptySet<FeedGroup>(), FeedGroup.parse(""))
    }

    @Test fun settingsDefaultToTheMoneyAndCommunityEventsAndFilterInOrder() {
        val defaults = EventsCardSettings.from(emptyMap())
        assertTrue(defaults.visible)
        assertFalse(defaults.expanded)
        assertEquals(setOf(FeedGroup.Follows, FeedGroup.Subs, FeedGroup.Tips, FeedGroup.Raids), defaults.groups)
        val feed = listOf(
            FeedItem("Twitch", "Raid", "c", time = "3"), FeedItem("Twitch", "Redemption", "r", time = "2"),
            FeedItem("Kick", "Tip", "b", time = "1"),
        )
        assertEquals(listOf("c", "b"), defaults.filter(feed).map { it.user })
        assertEquals(listOf("c"), defaults.filter(feed, limit = 1).map { it.user })

        val chosen = EventsCardSettings.from(mapOf(EventsCardSettings.VISIBLE_KEY to "false", EventsCardSettings.GROUPS_KEY to "redemptions",
            EventsCardSettings.EXPANDED_KEY to "true"))
        assertFalse(chosen.visible)
        assertTrue(chosen.expanded)
        assertEquals(listOf("r"), chosen.filter(feed).map { it.user })
        // Every switch off: an empty (but stored) choice, not the defaults.
        assertEquals(emptySet<FeedGroup>(), EventsCardSettings.from(mapOf(EventsCardSettings.GROUPS_KEY to "")).groups)
    }

    @Test fun kindsReadAsWords() {
        assertEquals("Gift subscription", feedKindLabel("GiftSubscription"))
        assertEquals("Super chat", feedKindLabel("super_chat"))
        assertEquals("", feedKindLabel(""))
    }
}
