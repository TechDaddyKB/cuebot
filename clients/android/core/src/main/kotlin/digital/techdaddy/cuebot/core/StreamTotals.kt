package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.JsonElement

data class StreamTotal(val type: String, val label: String, val value: String, val detail: String)

/**
 * This stream's numbers from the dashboard's `totals` (the StreamElements-style session data Cuebot keeps for
 * widgets): one row per `<type>-session` entry. For types that are not money (follows, subs, raids) the session amount
 * counts events too, so only the count is shown; a raid adds the last raid's viewers from `raid-latest`.
 */
object StreamTotals {
    private val ORDER = listOf("follower", "subscriber", "cheer", "tip", "superchat", "raid", "host", "merch", "charityCampaignDonation")

    fun rows(totals: Map<String, JsonElement>): List<StreamTotal> = totals.entries
        .filter { it.key.endsWith("-session") && !it.key.removeSuffix("-session").contains('-') }
        .map { (key, value) ->
            val type = key.removeSuffix("-session")
            val o = value.obj()
            row(type, o?.dbl("count") ?: 0.0, o?.dbl("amount") ?: 0.0, totals["$type-latest"].obj()?.num("amount"))
        }
        .sortedWith(compareBy({ ORDER.indexOf(it.type).let { i -> if (i < 0) 99 else i } }, { it.type }))

    internal fun row(type: String, count: Double, amount: Double, latest: Double? = null): StreamTotal {
        fun n(v: Double) = if (v % 1.0 == 0.0) v.toLong().toString() else String.format(java.util.Locale.ROOT, "%.2f", v).trimEnd('0').trimEnd('.')
        return when (type) {
            "follower" -> StreamTotal(type, "Follows", n(count), "")
            "subscriber" -> StreamTotal(type, "Subs", n(count), "")
            "cheer" -> StreamTotal(type, "Bits", n(amount), if (count == 1.0) "1 cheer" else "${n(count)} cheers")
            "tip" -> StreamTotal(type, "Tips", n(amount), if (count == 1.0) "1 tip" else "${n(count)} tips")
            "superchat" -> StreamTotal(type, "Super Chats", n(amount), if (count == 1.0) "1 sent" else "${n(count)} sent")
            "raid" -> StreamTotal(type, "Raids", n(count),
                if (count > 0 && latest != null) (if (latest == 1.0) "last: 1 viewer" else "last: ${n(latest)} viewers") else "")
            else -> {
                val words = type.replace(Regex("(?<=[a-z])(?=[A-Z])"), " ")
                val label = words.lowercase().replaceFirstChar { it.uppercase() }
                StreamTotal(type, label, n(if (amount > 0) amount else count), if (amount > 0) (if (count == 1.0) "once" else "${n(count)} times") else "")
            }
        }
    }
}
