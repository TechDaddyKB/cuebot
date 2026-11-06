package digital.techdaddy.cuebot.companion.ui.mod

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.core.ChangeDecision
import digital.techdaddy.cuebot.core.CuebotException
import digital.techdaddy.cuebot.core.PendingChange
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * The streamer's side: mods' changes that wait for an OK on the paired Cuebot (GET /api/v1/portal/changes), checked
 * every few seconds while the app is on screen, approved or rejected from the phone (docs/MOD_PORTAL_PLAN.md,
 * section 5). Approving something the streamer changed since the mod asked needs a second tap, as on the desktop.
 */
class ApprovalsViewModel(
    private val connection: Connection,
    visible: StateFlow<Boolean>,
    private val interval: Long = 5_000,
) : ViewModel() {
    data class UiState(
        val pending: List<PendingChange> = emptyList(),
        /** False until the first answer (and for a Cuebot without the mod portal). */
        val loaded: Boolean = false,
        /** Cuebot answered 404: it is older than the approval queue on the REST API. */
        val tooOld: Boolean = false,
        /** Changes that changed since the mod asked: the next approve uses the mod's version anyway. */
        val conflicts: Set<String> = emptySet(),
        val message: String? = null,
    )

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state

    init {
        viewModelScope.launch {
            combine(connection.session, visible) { s, v -> s to v }.collectLatest { (s, v) ->
                if (s == null) _state.value = UiState()
                if (s == null || !v) return@collectLatest
                while (true) {
                    refresh()
                    delay(interval)
                }
            }
        }
    }

    suspend fun refresh() {
        val client = connection.session.value?.client ?: return
        try {
            val pending = client.portalChanges()
            _state.update { s -> s.copy(pending = pending, loaded = true, tooOld = false, conflicts = s.conflicts.filterTo(mutableSetOf()) { id -> pending.any { it.id == id } }) }
        } catch (e: CancellationException) {
            throw e
        } catch (e: CuebotException.NotFound) {
            _state.update { it.copy(loaded = true, tooOld = true, pending = emptyList()) }
        } catch (e: CuebotException) {
            connection.report(e)
            // Cuebot answers 503 when the mod portal is not set up: nothing to approve.
            if (e is CuebotException.Http && e.status == 503) _state.update { it.copy(loaded = true, pending = emptyList()) }
        }
    }

    fun approve(change: PendingChange) = decide(change, approve = true)

    fun reject(change: PendingChange) = decide(change, approve = false)

    private fun decide(change: PendingChange, approve: Boolean) {
        val client = connection.session.value?.client ?: return
        val force = approve && change.id in _state.value.conflicts
        viewModelScope.launch {
            try {
                val d = client.decideChange(change.id, approve, force)
                val message = when (d.result) {
                    ChangeDecision.Result.Applied -> "Approved: it is live now."
                    ChangeDecision.Result.Rejected -> "Rejected. Nothing changed."
                    ChangeDecision.Result.Conflict -> "'${change.target}' changed since ${change.member} asked. Approve again to use their version anyway."
                    ChangeDecision.Result.NotPending -> "That change was already decided, or it expired."
                }
                _state.update { s ->
                    val conflicts = if (d.result == ChangeDecision.Result.Conflict) s.conflicts + change.id else s.conflicts - change.id
                    s.copy(pending = d.pending, conflicts = conflicts, message = message)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: CuebotException) {
                connection.report(e)
                _state.update { it.copy(message = if (e is CuebotException.NotFound) "That change was already decided, or it expired." else e.message) }
                refresh()
            }
        }
    }

    fun clearMessage() = _state.update { it.copy(message = null) }
}
