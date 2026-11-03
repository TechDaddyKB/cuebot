package digital.techdaddy.cuebot.companion.ui.dashboard

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.KeyValueStore
import digital.techdaddy.cuebot.companion.data.Session
import digital.techdaddy.cuebot.core.ActionSummary
import digital.techdaddy.cuebot.core.CuebotClient
import digital.techdaddy.cuebot.core.CuebotException
import digital.techdaddy.cuebot.core.Dashboard
import digital.techdaddy.cuebot.core.PointsGoal
import digital.techdaddy.cuebot.core.StreamState
import digital.techdaddy.cuebot.core.SubathonState
import digital.techdaddy.cuebot.core.SubathonTimer
import digital.techdaddy.cuebot.core.numberValue
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * The Dashboard tab: one /api/v1/dashboard snapshot, fetched again whenever a `dashboard.changed` event says the
 * revision moved on, the subathon and goal controls, and the actions pinned for the active Cuebot.
 */
class DashboardViewModel(
    private val connection: Connection,
    private val store: KeyValueStore,
    foreground: StateFlow<Boolean>,
) : ViewModel() {
    @Serializable
    data class Pinned(val id: String, val name: String, val askFirst: Boolean = false)

    sealed interface RunResult {
        data object Started : RunResult
        data class Failed(val message: String) : RunResult
    }

    data class UiState(
        val dashboard: Dashboard? = null,
        /** Cuebot answered 404: it is older than the dashboard endpoint (the Deck tab still works). */
        val tooOld: Boolean = false,
        val error: String? = null,
        val streamState: StreamState = StreamState.Connecting,
        val refreshing: Boolean = false,
        /** The last control that failed, shown above the cards. */
        val controlError: String? = null,
        val pinned: List<Pinned> = emptyList(),
        val available: List<ActionSummary> = emptyList(),
        val actionsError: String? = null,
    ) {
        val isReconnecting: Boolean get() = streamState is StreamState.Reconnecting
    }

    private val json = Json { ignoreUnknownKeys = true }
    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state
    private var session: Session? = null
    /** Fetch requests while live: conflated, so a burst of events becomes one more fetch. */
    private var refreshRequests: Channel<Unit>? = null

    init {
        viewModelScope.launch {
            combine(connection.session, foreground) { s, fg -> s to fg }.collectLatest { (s, fg) ->
                if (s !== session) {
                    session = s
                    _state.value = UiState(pinned = s?.let { loadPinned(it.cuebotId) } ?: emptyList())
                }
                if (s == null || !fg) return@collectLatest
                live(s)
            }
        }
    }

    private suspend fun live(s: Session) = coroutineScope {
        fetch(s)
        if (_state.value.tooOld) return@coroutineScope
        val requests = Channel<Unit>(Channel.CONFLATED)
        refreshRequests = requests
        launch { for (unused in requests) fetch(s) }
        try {
            s.client.apiEvents(listOf("dashboard.changed")) { st ->
                val was = _state.value.isReconnecting
                _state.update { it.copy(streamState = st) }
                if (st == StreamState.Connected && was) requests.trySend(Unit)   // catch up on what was missed
            }.collect { event ->
                val revision = event.apiEvent.args["revision"].numberValue()?.toLong() ?: Long.MAX_VALUE
                if (revision > (_state.value.dashboard?.revision ?: -1)) requests.trySend(Unit)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(error = e.message) }
        } finally {
            refreshRequests = null
            requests.close()
        }
    }

    private suspend fun fetch(s: Session) {
        _state.update { it.copy(refreshing = true) }
        try {
            val snapshot = s.client.dashboard()
            _state.update { st ->
                val old = st.dashboard
                val newer = old == null || snapshot.revision >= old.revision || snapshot.instanceId != old.instanceId
                st.copy(dashboard = if (newer) snapshot else old, error = null)
            }
        } catch (e: CuebotException.NotFound) {
            _state.update { it.copy(tooOld = true) }
        } catch (e: CuebotException) {
            connection.report(e)
            _state.update { it.copy(error = e.message) }
        } finally {
            _state.update { it.copy(refreshing = false) }
        }
    }

    /** Pull to refresh. */
    fun refresh() {
        val requests = refreshRequests
        if (requests != null) requests.trySend(Unit) else session?.let { s -> viewModelScope.launch { fetch(s) } }
    }

    // Controls

    fun subathon(timer: SubathonTimer, command: CuebotClient.SubathonCommand) = control { it.subathon(timer.id, command) }
    fun addTime(timer: SubathonTimer, seconds: Double) = control { it.subathonTime(timer.id, add = seconds) }
    fun setMultiplier(timer: SubathonTimer, value: Double) = control { it.subathonMultiplier(timer.id, value) }
    fun addPoints(goal: PointsGoal, points: Double) = control { it.goalPoints(goal.id, add = points) }
    fun setPoints(goal: PointsGoal, points: Double) = control { it.goalPoints(goal.id, set = points) }
    fun setMultiplier(goal: PointsGoal, value: Double) = control { it.goalMultiplier(goal.id, value) }
    fun reset(goal: PointsGoal) = control { it.goalReset(goal.id) }

    private fun control(call: suspend (CuebotClient) -> SubathonState) {
        val s = session ?: return
        viewModelScope.launch {
            try {
                val subathon = call(s.client)
                _state.update { st ->
                    val board = st.dashboard
                    val apply = board != null && (subathon.timers.isNotEmpty() || subathon.goals.isNotEmpty())
                    st.copy(dashboard = if (apply) board.copy(subathon = subathon) else board, controlError = null)
                }
            } catch (e: CuebotException) {
                connection.report(e)
                _state.update { it.copy(controlError = e.message) }
            }
        }
    }

    // Quick actions

    /** The Cuebot's actions, for choosing which to pin. */
    fun fetchActions() {
        val s = session ?: return
        viewModelScope.launch {
            try {
                val actions = s.client.actions().sortedWith(compareBy({ it.group }, { it.name }))
                // Keep pinned names current (an action renamed in Cuebot keeps its id).
                updatePinned { list -> list.map { p -> actions.firstOrNull { it.id == p.id }?.let { p.copy(name = it.name) } ?: p } }
                _state.update { it.copy(available = actions, actionsError = null) }
            } catch (e: CuebotException) {
                _state.update { it.copy(actionsError = e.message) }
            }
        }
    }

    fun isPinned(id: String) = _state.value.pinned.any { it.id == id }

    fun toggle(action: ActionSummary) = updatePinned { list ->
        if (list.any { it.id == action.id }) list.filter { it.id != action.id } else list + Pinned(action.id, action.name)
    }

    fun setAskFirst(id: String, askFirst: Boolean) = updatePinned { list -> list.map { if (it.id == id) it.copy(askFirst = askFirst) else it } }

    fun move(id: String, by: Int) = updatePinned { list ->
        val from = list.indexOfFirst { it.id == id }
        val to = (from + by).coerceIn(0, list.lastIndex)
        if (from < 0 || from == to) list else list.toMutableList().apply { add(to, removeAt(from)) }
    }

    suspend fun run(action: Pinned): RunResult {
        val s = session ?: return RunResult.Failed("Not connected")
        return try {
            s.client.runAction(action.id)
            RunResult.Started
        } catch (e: CuebotException.NotFound) {
            RunResult.Failed("${action.name} no longer exists in Cuebot.")
        } catch (e: CuebotException) {
            connection.report(e)
            RunResult.Failed(e.message ?: "")
        }
    }

    private fun loadPinned(cuebotId: String): List<Pinned> =
        store["quickActions.$cuebotId"]?.let { runCatching { json.decodeFromString<List<Pinned>>(it) }.getOrNull() } ?: emptyList()

    private fun updatePinned(change: (List<Pinned>) -> List<Pinned>) {
        val id = session?.cuebotId ?: return
        _state.update { it.copy(pinned = change(it.pinned)) }
        store.put("quickActions.$id", json.encodeToString(_state.value.pinned))
    }
}
