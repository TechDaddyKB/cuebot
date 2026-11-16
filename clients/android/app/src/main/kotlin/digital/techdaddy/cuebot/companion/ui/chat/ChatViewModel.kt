package digital.techdaddy.cuebot.companion.ui.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.Session
import digital.techdaddy.cuebot.core.ChatCapabilities
import digital.techdaddy.cuebot.core.ChatLine
import digital.techdaddy.cuebot.core.CuebotClient
import digital.techdaddy.cuebot.core.CuebotException
import digital.techdaddy.cuebot.core.ModAction
import digital.techdaddy.cuebot.core.ModerationNotice
import digital.techdaddy.cuebot.core.PlatformCapabilities
import digital.techdaddy.cuebot.core.SendAs
import digital.techdaddy.cuebot.core.StreamState
import digital.techdaddy.cuebot.core.durationLabel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** How many lines the Chat tab keeps. */
private const val MAX_ROWS = 300

/**
 * The Chat tab: the paired Cuebot's combined chat (GET /api/v1/chat, then `chat.received` and `chat.moderated` events),
 * sending as the broadcaster or the bot account, and moderating as the streamer. This is the streamer's own phone, paired
 * with the REST API token: mods moderate through the mod portal instead.
 */
class ChatViewModel(
    private val connection: Connection,
    foreground: StateFlow<Boolean>,
) : ViewModel() {
    /** One line of the list: a message, or a ban or timeout someone made. */
    sealed interface Row {
        val key: String
        val platform: String
        val time: String

        data class Message(val line: ChatLine) : Row {
            override val key get() = "m:${line.platform}:${line.id}"
            override val platform get() = line.platform
            override val time get() = line.time
        }

        data class Notice(override val key: String, override val platform: String, override val time: String, val text: String) : Row
    }

    /** What the last moderation or send did, shown under the list. [websiteUrl]: "On the website" opens it. */
    data class Outcome(val text: String, val ok: Boolean, val websiteUrl: String = "")

    data class UiState(
        val rows: List<Row> = emptyList(),
        val platforms: List<String> = emptyList(),
        val sendablePlatforms: List<String> = emptyList(),
        val botPlatforms: List<String> = emptyList(),
        val capabilities: ChatCapabilities = ChatCapabilities(),
        /** The platform shown, or null for all of them. */
        val filter: String? = null,
        /** Where a message goes: a platform, or [ALL]. */
        val sendTo: String = ALL,
        val sendAs: SendAs = SendAs.Broadcaster,
        val sending: Boolean = false,
        val outcome: Outcome? = null,
        val streamState: StreamState = StreamState.Connecting,
        val loaded: Boolean = false,
        /** Cuebot answered 404: it is older than the Chat tab. */
        val tooOld: Boolean = false,
        val error: String? = null,
    ) {
        val visibleRows: List<Row> get() = if (filter == null) rows else rows.filter { it.platform.equals(filter, ignoreCase = true) }

        /** "Bot" is offered only where a bot account is signed in (for All: on some platform). */
        val canSendAsBot: Boolean get() =
            if (sendTo == ALL) botPlatforms.isNotEmpty() else botPlatforms.any { it.equals(sendTo, ignoreCase = true) }

        val sendTargets: List<String> get() = if (sendablePlatforms.size > 1) listOf(ALL) + sendablePlatforms else sendablePlatforms.ifEmpty { listOf(ALL) }

        fun capabilities(platform: String): PlatformCapabilities = capabilities.of(platform) ?: PlatformCapabilities(platform)

        val isReconnecting: Boolean get() = streamState is StreamState.Reconnecting
    }

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state
    private var session: Session? = null
    private var noticeCount = 0

    init {
        viewModelScope.launch {
            combine(connection.session, foreground) { s, fg -> s to fg }.collectLatest { (s, fg) ->
                if (s !== session) {
                    session = s
                    _state.value = UiState()
                }
                if (s == null || !fg) return@collectLatest
                live(s)
            }
        }
    }

    private suspend fun live(s: Session) = coroutineScope {
        if (!load(s)) return@coroutineScope
        try {
            s.client.chatEvents { st ->
                val was = _state.value.isReconnecting
                _state.update { it.copy(streamState = st) }
                if (st == StreamState.Connected && was) launch { load(s) }   // catch up on what was missed
            }.collect { event ->
                val e = event.apiEvent
                when (e.type) {
                    CuebotClient.CHAT_RECEIVED -> add(ChatLine.from(e.args))
                    CuebotClient.CHAT_MODERATED -> apply(ModerationNotice.from(e.args))
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(error = e.message) }
        }
    }

    /** The recent chat and what each tool does; false when this Cuebot has no Chat endpoints. */
    private suspend fun load(s: Session): Boolean {
        try {
            val snapshot = s.client.chat()
            // Best effort: without moderation (503) the chat still reads and sends, and every tool shows as not available.
            val capabilities = try {
                s.client.chatCapabilities()
            } catch (e: CancellationException) {
                throw e
            } catch (e: CuebotException) {
                if (e is CuebotException.Unauthorized) throw e
                ChatCapabilities()
            }
            val rows = snapshot.messages.map { Row.Message(it) } + snapshot.moderation.mapNotNull(::noticeRow)
            _state.update { st ->
                val sendTo = if (st.sendTo == ALL || snapshot.sendablePlatforms.any { it.equals(st.sendTo, ignoreCase = true) }) st.sendTo else ALL
                st.copy(
                    rows = rows.sortedBy { it.time }.takeLast(MAX_ROWS),
                    platforms = snapshot.platforms, sendablePlatforms = snapshot.sendablePlatforms, botPlatforms = snapshot.botPlatforms,
                    capabilities = capabilities, sendTo = sendTo, loaded = true, error = null,
                ).fixSendAs()
            }
            return true
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException.NotFound) {
            _state.update { it.copy(tooOld = true, loaded = true) }
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(error = e.message, loaded = true) }
        }
        return false
    }

    private fun add(line: ChatLine) {
        if (line.id.isEmpty()) return
        _state.update { st ->
            val row = Row.Message(line)
            if (st.rows.any { it.key == row.key }) st else st.copy(rows = (st.rows + row).takeLast(MAX_ROWS))
        }
    }

    private fun apply(notice: ModerationNotice) {
        _state.update { st ->
            when (notice.kind) {
                ModerationNotice.Kind.MessageDeleted -> st.copy(rows = st.rows.map { r ->
                    if (r is Row.Message && r.line.id == notice.messageId && r.line.platform.equals(notice.platform, ignoreCase = true)) Row.Message(r.line.copy(deleted = true)) else r
                })
                else -> noticeRow(notice)?.let { st.copy(rows = (st.rows + it).takeLast(MAX_ROWS)) } ?: st
            }
        }
    }

    private fun noticeRow(n: ModerationNotice): Row.Notice? {
        val who = n.user.ifEmpty { n.userId }
        val by = if (n.moderator.isNotEmpty()) " by ${n.moderator}" else ""
        val text = when (n.kind) {
            ModerationNotice.Kind.UserBanned -> "$who was banned$by"
            ModerationNotice.Kind.UserTimedOut -> "$who was timed out" + (n.durationSeconds?.let { " for ${durationLabel(it)}" } ?: "") + by
            else -> return null
        }
        return Row.Notice("n:${noticeCount++}:${n.time}", n.platform, n.time, text)
    }

    // Choices

    fun showPlatform(platform: String?) = _state.update { it.copy(filter = platform) }

    fun sendTo(platform: String) = _state.update { it.copy(sendTo = platform).fixSendAs() }

    fun sendAs(sendAs: SendAs) = _state.update { if (sendAs == SendAs.Bot && !it.canSendAsBot) it else it.copy(sendAs = sendAs) }

    fun dismissOutcome() = _state.update { it.copy(outcome = null) }

    /** Bot is only kept while the chosen target has a bot account. */
    private fun UiState.fixSendAs() = if (sendAs == SendAs.Bot && !canSendAsBot) copy(sendAs = SendAs.Broadcaster) else this

    // Sending

    /** Sends [text]; true when Cuebot took it (the box can be cleared). */
    suspend fun send(text: String): Boolean {
        val s = session ?: return false
        if (text.isBlank()) return false
        val st = _state.value
        _state.update { it.copy(sending = true) }
        return try {
            s.client.sendChat(st.sendTo, text.trim(), st.sendAs)
            _state.update { it.copy(outcome = null) }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(outcome = Outcome(errorText(e), ok = false)) }
            false
        } finally {
            _state.update { it.copy(sending = false) }
        }
    }

    // Moderation

    /** Runs [action] on [line]'s message or author as the streamer. The result is shown as [UiState.outcome]. */
    fun moderate(line: ChatLine, action: ModAction, durationSeconds: Int? = null, reason: String? = null) {
        val s = session ?: return
        viewModelScope.launch {
            val outcome = try {
                val result = s.client.moderate(
                    line.platform, action, messageId = line.id, userId = line.userId, userName = line.user,
                    durationSeconds = durationSeconds, reason = reason,
                )
                val text = result.message.ifEmpty { if (result.ok) "${action.label}: done." else "${action.label} did not work." }
                Outcome(text, result.ok, result.websiteUrl)
            } catch (e: CancellationException) {
                throw e
            } catch (e: CuebotException) {
                connection.report(e)
                Outcome(errorText(e), ok = false)
            }
            _state.update { it.copy(outcome = outcome) }
        }
    }

    companion object {
        const val ALL = "All"

        /** The `error` of an API error answer, else the exception's own text. */
        fun errorText(e: CuebotException): String =
            (e as? CuebotException.Http)?.body?.let { Regex("\"error\"\\s*:\\s*\"([^\"]*)\"").find(it)?.groupValues?.get(1) } ?: e.message ?: "It did not work."
    }
}
