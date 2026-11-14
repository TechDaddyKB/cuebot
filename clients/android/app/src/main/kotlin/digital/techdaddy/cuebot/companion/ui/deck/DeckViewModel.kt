package digital.techdaddy.cuebot.companion.ui.deck

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.data.KeyValueStore
import digital.techdaddy.cuebot.companion.data.Session
import digital.techdaddy.cuebot.core.CuebotException
import digital.techdaddy.cuebot.core.DeckItem
import digital.techdaddy.cuebot.core.DeckNotification
import digital.techdaddy.cuebot.core.DeckSummary
import digital.techdaddy.cuebot.core.DeckView
import digital.techdaddy.cuebot.core.StreamState
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** The Deck tab: the list of decks, the chosen one's live view, presses, pages and notifications. */
class DeckViewModel(
    private val connection: Connection,
    private val store: KeyValueStore,
    foreground: StateFlow<Boolean>,
) : ViewModel() {
    enum class PressResult { Ok, Failed }

    data class UiState(
        val decks: List<DeckSummary> = emptyList(),
        val decksError: String? = null,
        val loaded: Boolean = false,
        val deckId: String? = null,
        val view: DeckView? = null,
        val streamState: StreamState = StreamState.Connecting,
        val notification: DeckNotification? = null,
    ) {
        val isReconnecting: Boolean get() = streamState is StreamState.Reconnecting
        val needsChoice: Boolean get() = view == null && decks.size > 1 && deckId == null
    }

    private val _state = MutableStateFlow(UiState(deckId = store[DECK_KEY]))
    val state: StateFlow<UiState> = _state
    private val chosen = MutableStateFlow(store[DECK_KEY])
    private var session: Session? = null
    private var notificationJob: Job? = null

    init {
        viewModelScope.launch {
            combine(connection.session, foreground) { s, fg -> s to fg }.collectLatest { (s, fg) ->
                if (s !== session) {
                    session = s
                    _state.update { UiState(deckId = chosen.value) }
                }
                if (s == null || !fg) return@collectLatest
                // Without a deck to show (Cuebot was unreachable, or has no decks yet), try again as soon as the
                // connection check passes again (Retry, or coming back online) and every RETRY_MS meanwhile; the
                // session stays the same through all of that, so nothing else would reload the list.
                while (!loadDecks(s)) {
                    withTimeoutOrNull(RETRY_MS) { connection.status.drop(1).first { it is ConnectionStatus.Online } }
                }
                chosen.collectLatest { id -> if (id != null) stream(s, id) }
            }
        }
    }

    /** True when there is at least one enabled deck to show. */
    private suspend fun loadDecks(s: Session): Boolean {
        return try {
            val decks = s.client.decks().filter { it.enabled }
            _state.update {
                it.copy(decks = decks, loaded = true,
                    decksError = if (decks.isEmpty()) "Cuebot has no enabled decks. Make one in Cuebot under Input > Decks." else null)
            }
            val id = chosen.value
            when {
                id != null && decks.any { it.id == id } -> {}
                decks.size == 1 -> choose(decks[0].id)
                else -> choose(null)
            }
            decks.isNotEmpty()
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(decks = emptyList(), loaded = true, decksError = e.message) }
            false
        }
    }

    private suspend fun stream(s: Session, id: String) {
        try {
            s.client.deckEvents(id) { st -> _state.update { it.copy(streamState = st) } }.collect { event ->
                when (event.type) {
                    "state" -> DeckView.from(event.json)?.let { view -> _state.update { it.copy(view = view) } }
                    "notify" -> DeckNotification.from(event.json)?.let(::show)
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(streamState = StreamState.Reconnecting(0.0, e.message ?: "")) }
        }
    }

    /** Opens a deck (and remembers it). */
    fun open(id: String) = choose(id)

    private fun choose(id: String?) {
        if (chosen.value == id) return
        store.put(DECK_KEY, id)
        _state.update { it.copy(deckId = id, view = null, streamState = StreamState.Connecting) }
        chosen.value = id
    }

    suspend fun press(item: DeckItem): PressResult {
        val s = session ?: return PressResult.Failed
        val deck = _state.value.view?.id ?: chosen.value ?: return PressResult.Failed
        return try {
            s.client.press(deck, item.id)
            PressResult.Ok
        } catch (e: CuebotException) {
            connection.report(e)
            PressResult.Failed
        }
    }

    fun page(delta: Int) {
        val s = session ?: return
        val view = _state.value.view ?: return
        if (view.pageCount <= 1) return
        viewModelScope.launch { runCatching { s.client.page(view.id, delta) } }
    }

    fun dismissNotification() = _state.update { it.copy(notification = null) }

    private fun show(note: DeckNotification) {
        _state.update { it.copy(notification = note) }
        notificationJob?.cancel()
        notificationJob = viewModelScope.launch {
            delay(4_000)
            _state.update { if (it.notification == note) it.copy(notification = null) else it }
        }
    }

    private companion object {
        const val DECK_KEY = "deckId"
        const val RETRY_MS = 15_000L
    }
}
