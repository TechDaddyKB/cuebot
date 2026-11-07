package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

// Every model reads its JSON tolerantly (see Tolerant.kt): a missing or changed field falls back to a default.

enum class ConnectionState(val label: String) {
    Disconnected("Disconnected"), Connecting("Connecting"), Connected("Connected"), Error("Error"), Unknown("Unknown");

    companion object {
        fun of(text: String) = entries.firstOrNull { it.label == text } ?: Unknown
    }
}

data class ConnectionInfo(val name: String, val state: ConnectionState, val status: String = "") {
    companion object {
        fun from(o: JsonObject) = ConnectionInfo(o.str("name"), ConnectionState.of(o.str("state")), o.str("status"))
    }
}

data class AppInfo(
    val name: String = "", val version: String = "", val runtime: String = "", val os: String = "",
    val instanceId: String = "", val apiVersion: String = "",
) {
    companion object {
        fun from(o: JsonObject?) = o?.let {
            AppInfo(it.str("name"), it.str("version"), it.str("runtime"), it.str("os"), it.str("instanceId"), it.str("apiVersion"))
        } ?: AppInfo()
    }
}

/** `GET /api/v1/status`. */
data class StatusResponse(val app: AppInfo = AppInfo(), val connections: List<ConnectionInfo> = emptyList()) {
    companion object {
        fun from(e: JsonElement?): StatusResponse {
            val o = e.obj() ?: return StatusResponse()
            return StatusResponse(AppInfo.from(o["app"].obj()), o.list("connections", ConnectionInfo::from))
        }
    }
}

/** One entry of `GET /api/v1/actions`. */
data class ActionSummary(val id: String, val name: String, val group: String = "", val enabled: Boolean = true) {
    companion object {
        fun from(o: JsonObject) = ActionSummary(o.str("id"), o.str("name"), o.str("group"), o.bool("enabled", true))
    }
}

/** One entry of `GET /api/v1/decks`; only enabled decks answer on /deck/{id}. */
data class DeckSummary(
    val id: String, val name: String, val rows: Int = 3, val columns: Int = 5, val pageCount: Int = 1, val enabled: Boolean = true,
) {
    companion object {
        fun from(o: JsonObject) = DeckSummary(
            o.str("id"), o.str("name"), o.int("rows", 3), o.int("columns", 5), o.int("pageCount", 1), o.bool("enabled", true),
        )
    }
}

/** One button of a deck view. */
data class DeckItem(
    val id: String, val name: String = "", val row: Int = 0, val column: Int = 0, val state: Int = 0,
    val title: String = "", val titleColor: String = "", val background: String = "", val backgroundImage: String = "",
    val icon: String = "", val iconColor: String = "", val iconUrl: String = "", val value: String = "",
) {
    /** What TalkBack reads: the title, else the name. */
    val accessibilityName: String get() = title.ifEmpty { name.ifEmpty { "Button" } }

    companion object {
        fun from(o: JsonObject) = DeckItem(
            o.str("id"), o.str("name"), o.int("row"), o.int("column"), o.int("state"), o.str("title"), o.str("titleColor"),
            o.str("background"), o.str("backgroundImage"), o.str("icon"), o.str("iconColor"), o.str("iconUrl"), o.str("value"),
        )
    }
}

/** `GET /deck/{id}/state` and each `state` event of `/deck/{id}/events`. */
data class DeckView(
    val id: String, val name: String = "", val rows: Int = 3, val columns: Int = 5, val background: String = "", val gap: Int = 8,
    val page: Int = 0, val pageName: String = "", val pageCount: Int = 1, val items: List<DeckItem> = emptyList(),
) {
    fun item(row: Int, column: Int) = items.firstOrNull { it.row == row && it.column == column }

    companion object {
        fun from(e: JsonElement?): DeckView? {
            val o = e.obj() ?: return null
            return DeckView(
                o.str("id"), o.str("name"), max(1, o.int("rows", 3)), max(1, o.int("columns", 5)), o.str("background"),
                max(0, o.int("gap", 8)), o.int("page"), o.str("pageName"), max(1, o.int("pageCount", 1)), o.list("items", DeckItem::from),
            )
        }
    }
}

/** A `notify` event of a deck (the "Deck notify" sub-action). */
data class DeckNotification(val title: String, val description: String = "") {
    companion object {
        fun from(e: JsonElement?) = e.obj()?.let { DeckNotification(it.str("title"), it.str("description")) }
    }
}

/** `GET /api/v1/dashboard`: everything the Dashboard shows; `revision` goes up with every change. */
data class Dashboard(
    val instanceId: String = "", val version: String = "", val connections: List<ConnectionInfo> = emptyList(),
    val totals: Map<String, JsonElement> = emptyMap(), val subathon: SubathonState = SubathonState(),
    val topSpenders: TopSpenders = TopSpenders(), val feed: List<FeedItem> = emptyList(), val revision: Long = 0,
) {
    companion object {
        fun from(e: JsonElement?): Dashboard {
            val o = e.obj() ?: return Dashboard()
            return Dashboard(
                o.str("instanceId"), o.str("version"), o.list("connections", ConnectionInfo::from), o["totals"].obj() ?: emptyMap(),
                SubathonState.from(o["subathon"]), TopSpenders.from(o["topSpenders"]), o.list("feed", FeedItem::from),
                (o["revision"] as? JsonPrimitive)?.content?.toDoubleOrNull()?.toLong() ?: 0,
            )
        }
    }
}

data class SubathonState(val timers: List<SubathonTimer> = emptyList(), val goals: List<PointsGoal> = emptyList()) {
    companion object {
        fun from(e: JsonElement?): SubathonState {
            val o = e.obj() ?: return SubathonState()
            return SubathonState(o.list("timers", SubathonTimer::from), o.list("goals", PointsGoal::from))
        }
    }
}

data class SubathonTimer(
    val id: String, val name: String, val state: State, val remaining: Double,
    /** Unix time in milliseconds when a running timer reaches zero; null while paused or stopped. */
    val endsAt: Double? = null, val elapsed: Double = 0.0, val added: Double = 0.0, val multiplier: Double = 1.0,
) {
    enum class State { Running, Paused, Stopped, Ended, Unknown }

    /** Seconds left at [nowMillis]: counted down on the phone from `endsAt` while running, else the snapshot's value. */
    fun remainingAt(nowMillis: Long): Double =
        if (state == State.Running && endsAt != null) max(0.0, (endsAt - nowMillis) / 1000.0) else max(0.0, remaining)

    companion object {
        fun from(o: JsonObject) = SubathonTimer(
            o.str("id"), o.str("name"),
            when (o.str("state").lowercase()) { "running" -> State.Running; "paused" -> State.Paused; "stopped", "" -> State.Stopped; "ended" -> State.Ended; else -> State.Unknown },
            o.dbl("remaining"), o.num("endsAt"), o.dbl("elapsed"), o.dbl("added"), o.dbl("multiplier", 1.0),
        )

        /** "H:MM:SS" (or "D:HH:MM:SS" past a day), rounded down like Cuebot's own timer. */
        fun format(seconds: Double): String {
            val total = floor(max(0.0, seconds)).toLong()
            val days = total / 86_400; val hours = total / 3_600 % 24; val minutes = total / 60 % 60; val secs = total % 60
            return if (days > 0) String.format(java.util.Locale.ROOT, "%d:%02d:%02d:%02d", days, hours, minutes, secs) else String.format(java.util.Locale.ROOT, "%d:%02d:%02d", hours, minutes, secs)
        }
    }
}

data class GoalStep(val name: String, val points: Double, val reached: Boolean = false) {
    companion object {
        fun from(o: JsonObject) = GoalStep(o.str("name"), o.dbl("points"), o.bool("reached"))
    }
}

data class PointsGoal(
    val id: String, val name: String, val points: Double, val steps: List<GoalStep>, val multiplier: Double = 1.0, val next: GoalStep? = null,
) {
    /** Progress (0..1) from the last reached step (or 0) to the next one; 1 when every step is reached. */
    val progressToNext: Double get() {
        val reached = steps.filter { it.points <= points }.maxOfOrNull { it.points } ?: 0.0
        val target = steps.map { it.points }.filter { it > points }.minOrNull() ?: return if (steps.isEmpty()) 0.0 else 1.0
        val span = target - reached
        return if (span <= 0) 0.0 else min(1.0, max(0.0, (points - reached) / span))
    }

    companion object {
        fun from(o: JsonObject) = PointsGoal(
            o.str("id"), o.str("name"), o.dbl("points"), o.list("steps", GoalStep::from), o.dbl("multiplier", 1.0),
            o["next"].obj()?.let(GoalStep::from)?.takeUnless { it.name.isEmpty() && it.points == 0.0 },
        )
    }
}

data class Spender(val rank: Int, val name: String, val platform: String, val amount: Double, val formatted: String, val count: Int) {
    companion object {
        fun from(o: JsonObject) = Spender(o.int("rank"), o.str("name"), o.str("platform"), o.dbl("amount"), o.str("formatted"), o.int("count"))
    }
}

data class SpenderBoard(val total: Double = 0.0, val formattedTotal: String = "", val spenders: List<Spender> = emptyList()) {
    companion object {
        fun from(o: JsonObject) = SpenderBoard(o.dbl("total"), o.str("formattedTotal"), o.list("spenders", Spender::from))
    }
}

/**
 * Top spenders: `periods[period][platform]`, where platform is "all" or a lowercase platform name. Keys come from the
 * snapshot (never hard-coded); "stream" is listed first. Null in the snapshot when spending tracking is off.
 */
data class TopSpenders(val currency: String = "", val periods: Map<String, Map<String, SpenderBoard>> = emptyMap()) {
    val periodKeys: List<String> get() = periods.keys.sortedWith(compareBy({ ORDER.indexOf(it).let { i -> if (i < 0) 99 else i } }, { it }))

    fun platformKeys(period: String): List<String> =
        (periods[period]?.keys ?: emptySet()).sortedWith(compareBy({ if (it == "all") 0 else 1 }, { it }))

    fun board(period: String, platform: String = "all") = periods[period]?.get(platform) ?: SpenderBoard()

    companion object {
        private val ORDER = listOf("stream", "day", "week", "month", "year", "all", "alltime")

        fun from(e: JsonElement?): TopSpenders {
            val o = e.obj() ?: return TopSpenders()
            val periods = o["periods"].obj()?.mapNotNull { (period, value) ->
                value.obj()?.let { boards -> period to boards.mapNotNull { (platform, b) -> b.obj()?.let { platform to SpenderBoard.from(it) } }.toMap() }
            }?.toMap() ?: emptyMap()
            return TopSpenders(o.str("currency"), periods)
        }
    }
}

/** One line of the activity feed. */
data class FeedItem(
    val platform: String, val kind: String, val user: String, val title: String = "", val detail: String = "",
    val amount: String = "", val time: String = "", val isTest: Boolean = false,
) {
    companion object {
        fun from(o: JsonObject) = FeedItem(
            o.str("platform"), o.str("kind"), o.str("user"), o.str("title"), o.str("detail", o.str("message")),
            o.str("amount"), o.str("time"), o.bool("isTest"),
        )
    }
}

/** The data of a `/api/v1/events` message: `{"type": "...", "time": "ISO-8601", "args": {...}}`. */
data class ApiEvent(val type: String, val time: String = "", val args: JsonObject = JsonObject(emptyMap()))

/** One entry of `GET /api/v1/portal/changes`: a mod's change waiting for the streamer. Times are Unix milliseconds. */
data class PendingChange(
    val id: String, val member: String, val area: String, val kind: String, val target: String, val summary: String,
    val at: Long, val expires: Long,
) {
    companion object {
        fun from(o: JsonObject) = PendingChange(o.str("id"), o.str("member"), o.str("area"), o.str("kind"), o.str("target"),
            o.str("summary").ifEmpty { "${o.str("member")} changed '${o.str("target")}'" }, o.num("at")?.toLong() ?: 0, o.num("expires")?.toLong() ?: 0)
    }
}

/** One streamer profile (persona) of the paired Cuebot. */
data class ProfileInfo(val id: String, val name: String, val color: String, val open: Boolean) {
    companion object {
        fun from(o: JsonObject) = ProfileInfo(o.str("id"), o.str("name").ifEmpty { o.str("id") }, o.str("color"), o.bool("open"))
    }
}

/** `GET /api/v1/profiles`: the open profile, every profile, the platforms that are live, and whether a switch is under way. */
data class Profiles(val current: ProfileInfo?, val profiles: List<ProfileInfo>, val live: List<String>, val switching: Boolean) {
    companion object {
        fun from(e: JsonElement?): Profiles {
            val o = e.obj() ?: return Profiles(null, emptyList(), emptyList(), false)
            return Profiles(o["current"].obj()?.let(ProfileInfo::from), o.list("profiles", ProfileInfo::from),
                (o["live"] as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull } ?: emptyList(), o.bool("switching"))
        }
    }
}

/** What asking Cuebot to switch profile did. */
sealed interface ProfileSwitch {
    /** Cuebot is restarting on the other profile; it is back in a few seconds. */
    data object Switching : ProfileSwitch
    data object AlreadyOpen : ProfileSwitch
    /** A stream is live; ask again with force to switch anyway. */
    data class Live(val message: String) : ProfileSwitch
    /** Another switch is under way. */
    data class Busy(val message: String) : ProfileSwitch
}

/** What approving or rejecting did, and the queue after it. */
data class ChangeDecision(val result: Result, val pending: List<PendingChange>) {
    enum class Result {
        Applied, Rejected,
        /** The thing changed since the mod asked: nothing was applied; approving with force uses the mod's version. */
        Conflict,
        /** Someone decided it already, or it expired. */
        NotPending;

        companion object {
            fun of(text: String) = when (text) { "applied" -> Applied; "rejected" -> Rejected; "conflict" -> Conflict; else -> NotPending }
        }
    }
}

