package digital.techdaddy.cuebot.core

/**
 * The kinds of stream events the Chat page's events card can show or hide, one switch each. Cuebot's feed kinds
 * (`FeedKind`: Follow, Subscription, GiftSubscription, Tip, Raid, Redemption, StreamOnline, StreamOffline, Other) and the
 * names other sources use (cheer, bits, Kicks, Super Chat, rant, membership...) fall into one group each.
 */
enum class FeedGroup(val key: String, val label: String, val shownByDefault: Boolean) {
    Follows("follows", "Follows", true),
    Subs("subs", "Subs and memberships", true),
    Tips("tips", "Tips, bits, Kicks, Super Chats and rants", true),
    Raids("raids", "Raids", true),
    Redemptions("redemptions", "Channel point redemptions", false),
    Other("other", "Everything else (going live, offline, other)", false);

    companion object {
        val defaults: Set<FeedGroup> = entries.filter { it.shownByDefault }.toSet()

        private val kinds = mapOf(
            Follows to setOf("follow", "follower"),
            Subs to setOf("subscription", "giftsubscription", "sub", "resub", "resubscription", "giftsub", "giftedsub", "membership", "membergift", "giftmembership"),
            Tips to setOf("tip", "donation", "cheer", "bits", "kicks", "kicksgifted", "superchat", "supersticker", "rant", "charity"),
            Raids to setOf("raid", "host"),
            Redemptions to setOf("redemption", "reward"),
        )

        /** The group of a feed item's `kind` (case and separators do not matter); unknown kinds are [Other]. */
        fun of(kind: String): FeedGroup {
            val k = kind.lowercase().filter { it.isLetterOrDigit() }
            return kinds.entries.firstOrNull { k in it.value }?.key ?: Other
        }

        /** The groups in a comma-separated list of keys; unknown keys are skipped. */
        fun parse(text: String): Set<FeedGroup> =
            text.split(',').mapNotNull { key -> entries.firstOrNull { it.key == key.trim() } }.toSet()

        fun encode(groups: Set<FeedGroup>): String = entries.filter { it in groups }.joinToString(",") { it.key }
    }
}

/** The events card's per-device choices: whether it shows, which groups it shows, and whether the narrow strip is open. */
data class EventsCardSettings(
    val visible: Boolean = true,
    val groups: Set<FeedGroup> = FeedGroup.defaults,
    val expanded: Boolean = false,
) {
    fun shows(item: FeedItem): Boolean = FeedGroup.of(item.kind) in groups

    /** The items the card shows, in the feed's order (Cuebot sends the newest first), at most [limit]. */
    fun filter(feed: List<FeedItem>, limit: Int = 50): List<FeedItem> = feed.asSequence().filter(::shows).take(limit).toList()

    companion object {
        const val VISIBLE_KEY = "eventsCard.visible"
        const val GROUPS_KEY = "eventsCard.groups"
        const val EXPANDED_KEY = "eventsCard.expanded"

        /** Read from the app's string settings; missing keys keep the defaults. */
        fun from(values: Map<String, String>) = EventsCardSettings(
            visible = values[VISIBLE_KEY] != "false",
            groups = values[GROUPS_KEY]?.let(FeedGroup::parse) ?: FeedGroup.defaults,
            expanded = values[EXPANDED_KEY] == "true",
        )
    }
}

/** A feed item's `kind` for people: "GiftSubscription" is "Gift subscription"; empty for none. */
fun feedKindLabel(kind: String): String {
    val words = kind.replace('_', ' ').replace(Regex("(?<=[a-z])(?=[A-Z])"), " ").trim().lowercase()
    return words.replaceFirstChar { it.uppercase() }
}
