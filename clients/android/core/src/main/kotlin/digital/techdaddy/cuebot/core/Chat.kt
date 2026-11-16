package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

// The combined chat of the Chat tab: GET /api/v1/chat, /chat/capabilities, /chat/modlog, and the chat.received,
// chat.moderated and chat.modLog events. Read tolerantly like every other model (see Tolerant.kt).

private fun JsonObject.strings(key: String): List<String> =
    (this[key] as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.contentOrNull } ?: emptyList()

/** One chat message, text only. [deleted] once the platform reported it deleted. */
data class ChatLine(
    val platform: String, val id: String, val user: String, val userId: String = "", val text: String = "",
    val time: String = "", val roles: String = "", val color: String = "", val amount: String = "",
    val canDelete: Boolean = true, val deleted: Boolean = false,
) {
    val isBroadcaster: Boolean get() = roles.contains("Broadcaster")
    val isModerator: Boolean get() = roles.contains("Moderator")

    companion object {
        fun from(o: JsonObject) = ChatLine(
            o.str("platform"), o.str("id"), o.str("user"), o.str("userId"), o.str("text"), o.str("time"), o.str("roles"),
            o.str("color"), o.str("amount"), o.bool("canDelete", true), o.bool("deleted"),
        )
    }
}

/** A deletion, ban or timeout a platform reported, whoever made it (Cuebot, a mod, the platform's website). */
data class ModerationNotice(
    val platform: String, val kind: Kind, val messageId: String = "", val userId: String = "", val user: String = "",
    val durationSeconds: Int? = null, val moderator: String = "", val time: String = "",
) {
    enum class Kind { MessageDeleted, UserBanned, UserTimedOut, Unknown }

    companion object {
        fun from(o: JsonObject) = ModerationNotice(
            o.str("platform"),
            when (o.str("kind")) { "messageDeleted" -> Kind.MessageDeleted; "userBanned" -> Kind.UserBanned; "userTimedOut" -> Kind.UserTimedOut; else -> Kind.Unknown },
            o.str("messageId"), o.str("userId"), o.str("user"), o.num("durationSeconds")?.toInt(), o.str("moderator"), o.str("time"),
        )
    }
}

/** `GET /api/v1/chat`: recent messages (oldest first) and who can send, and send as the bot account. */
data class ChatSnapshot(
    val messages: List<ChatLine> = emptyList(), val moderation: List<ModerationNotice> = emptyList(),
    val platforms: List<String> = emptyList(), val sendablePlatforms: List<String> = emptyList(), val botPlatforms: List<String> = emptyList(),
) {
    companion object {
        fun from(e: JsonElement?): ChatSnapshot {
            val o = e.obj() ?: return ChatSnapshot()
            return ChatSnapshot(
                o.list("messages", ChatLine::from), o.list("moderation", ModerationNotice::from),
                o.strings("platforms"), o.strings("sendablePlatforms"), o.strings("botPlatforms"),
            )
        }
    }
}

/** Who writes a message: the platform's own setting, the broadcaster, or the bot account. */
enum class SendAs(val wire: String) { Default("default"), Broadcaster("broadcaster"), Bot("bot") }

/** The moderation tools the Chat tab offers (Cuebot names them in camelCase). */
enum class ModAction(val wire: String, val label: String) {
    Delete("delete", "Delete message"), Timeout("timeout", "Timeout"), Ban("ban", "Ban"), Unban("unban", "Unban"), Warn("warn", "Warn");
}

/** What one tool does on one platform now. */
data class ToolCapability(
    val action: String, val label: String = "", val state: State = State.NotAvailable, val canRun: Boolean = false,
    val note: String = "", val websiteUrl: String = "", val maxReasonLength: Int? = null,
) {
    enum class State { Works, Closest, OnWebsite, NotAvailable }

    companion object {
        fun from(o: JsonObject): ToolCapability {
            val state = when (o.str("state")) { "works" -> State.Works; "closest" -> State.Closest; "onWebsite" -> State.OnWebsite; else -> State.NotAvailable }
            return ToolCapability(
                o.str("action"), o.str("label"), state, o.bool("canRun", state == State.Works || state == State.Closest),
                o.str("note"), o.str("websiteUrl"), o.num("maxReasonLength")?.toInt(),
            )
        }
    }
}

/** A timeout length the Chat tab offers, and what the platform really applies ([note] when it differs). */
data class TimeoutPreset(val requestedSeconds: Int, val appliedSeconds: Int, val note: String = "") {
    /** "10 min", "1 h", "1 day"... */
    val label: String get() = durationLabel(requestedSeconds)

    companion object {
        fun from(o: JsonObject) = TimeoutPreset(o.int("requestedSeconds"), o.int("appliedSeconds", o.int("requestedSeconds")), o.str("note"))
    }
}

/** One platform's tools. */
data class PlatformCapabilities(
    val platform: String, val canSend: Boolean = false, val canSendAsBot: Boolean = false,
    val actions: List<ToolCapability> = emptyList(), val timeoutPresets: List<TimeoutPreset> = emptyList(),
) {
    fun tool(action: ModAction): ToolCapability =
        actions.firstOrNull { it.action == action.wire } ?: ToolCapability(action.wire, action.label, note = "Cuebot did not say.")

    companion object {
        fun from(o: JsonObject) = PlatformCapabilities(
            o.str("platform"), o.bool("canSend"), o.bool("canSendAsBot"), o.list("actions", ToolCapability::from), o.list("timeoutPresets", TimeoutPreset::from),
        )
    }
}

/** `GET /api/v1/chat/capabilities`. */
data class ChatCapabilities(val platforms: List<PlatformCapabilities> = emptyList()) {
    fun of(platform: String): PlatformCapabilities? = platforms.firstOrNull { it.platform.equals(platform, ignoreCase = true) }

    companion object {
        fun from(e: JsonElement?) = ChatCapabilities(e.obj()?.list("platforms", PlatformCapabilities::from) ?: emptyList())
    }
}

/** One mod log row. */
data class ModLogEntry(
    val id: String, val at: String = "", val actorKind: String = "", val actorName: String = "", val platform: String = "",
    val action: String = "", val targetUserId: String = "", val targetName: String = "", val reason: String = "",
    val durationAppliedSeconds: Int? = null, val result: String = "", val error: String = "",
) {
    companion object {
        fun from(o: JsonObject) = ModLogEntry(
            o.str("id"), o.str("at"), o.str("actorKind"), o.str("actorName"), o.str("platform"), o.str("action"), o.str("targetUserId"),
            o.str("targetName"), o.str("reason"), o.num("durationAppliedSeconds")?.toInt(), o.str("result"), o.str("error"),
        )
    }
}

/** What `POST /api/v1/chat/moderate` did. [websiteUrl] when the platform only does it on its website. */
data class ModerationOutcome(
    val ok: Boolean, val result: String, val message: String = "", val appliedDurationSeconds: Int? = null, val websiteUrl: String = "",
) {
    companion object {
        fun from(e: JsonElement?): ModerationOutcome {
            val o = e.obj() ?: return ModerationOutcome(false, "failed", "Cuebot's answer was not understood.")
            return ModerationOutcome(o.bool("ok"), o.str("result"), o.str("message"), o.num("appliedDurationSeconds")?.toInt(), o.str("websiteUrl"))
        }
    }
}

/** "45 s", "10 min", "1 h 30 min", "1 day", "7 days". */
fun durationLabel(seconds: Int): String = when {
    seconds < 60 -> "$seconds s"
    seconds < 3_600 -> if (seconds % 60 == 0) "${seconds / 60} min" else "${seconds / 60} min ${seconds % 60} s"
    seconds < 86_400 -> if (seconds % 3_600 == 0) "${seconds / 3_600} h" else "${seconds / 3_600} h ${seconds % 3_600 / 60} min"
    else -> (seconds / 86_400).let { if (it == 1) "1 day" else "$it days" } + (seconds % 86_400 / 3_600).let { if (it > 0) " $it h" else "" }
}
