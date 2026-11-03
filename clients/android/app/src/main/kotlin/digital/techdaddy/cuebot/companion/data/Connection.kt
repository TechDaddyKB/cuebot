package digital.techdaddy.cuebot.companion.data

import digital.techdaddy.cuebot.core.CuebotClient
import digital.techdaddy.cuebot.core.CuebotEndpoint
import digital.techdaddy.cuebot.core.CuebotException
import digital.techdaddy.cuebot.core.StatusResponse
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/** Whether the active Cuebot can be talked to. */
sealed interface ConnectionStatus {
    data object Unpaired : ConnectionStatus
    data object Checking : ConnectionStatus
    data class Online(val status: StatusResponse) : ConnectionStatus
    data class Unreachable(val message: String) : ConnectionStatus
    data object Unauthorized : ConnectionStatus
}

/** The active Cuebot's client, with the paired entry's id (per-Cuebot settings such as quick actions follow it). */
class Session(val cuebotId: String, val client: CuebotClient)

/** Keeps a client for the active Cuebot and checks that it answers. */
class Connection(
    private val paired: PairedCuebots,
    private val scope: CoroutineScope,
    private val newClient: (CuebotEndpoint) -> CuebotClient = { CuebotClient(it) },
) {
    private val _status = MutableStateFlow<ConnectionStatus>(ConnectionStatus.Unpaired)
    val status: StateFlow<ConnectionStatus> = _status
    private val _session = MutableStateFlow<Session?>(null)
    val session: StateFlow<Session?> = _session
    private var checkJob: Job? = null

    init {
        scope.launch { paired.state.map { it.revision }.distinctUntilChanged().collect { connect() } }
    }

    val endpoint: CuebotEndpoint? get() = _session.value?.client?.endpoint

    /** Rebuilds the client when the active Cuebot changed, and checks it. */
    fun connect() {
        val id = paired.state.value.activeId
        val endpoint = paired.activeEndpoint()
        if (id == null || endpoint == null) {
            checkJob?.cancel()
            _session.value = null
            _status.value = ConnectionStatus.Unpaired
            return
        }
        val current = _session.value
        if (current?.cuebotId != id || current.client.endpoint != endpoint) _session.value = Session(id, newClient(endpoint))
        check()
    }

    fun check() {
        val session = _session.value ?: return
        checkJob?.cancel()
        checkJob = scope.launch {
            _status.value = ConnectionStatus.Checking
            _status.value = try {
                val status = session.client.status()
                paired.learnInstanceId(status.app.instanceId, session.cuebotId)
                ConnectionStatus.Online(status)
            } catch (e: CancellationException) {
                throw e   // a newer check is on its way
            } catch (e: CuebotException.Unauthorized) {
                ConnectionStatus.Unauthorized
            } catch (e: CuebotException) {
                ConnectionStatus.Unreachable(e.message ?: e.toString())
            }
        }
    }

    /** A call failed: a 401 means pair again; anything else is shown where it happened. */
    fun report(error: Throwable) {
        if (error is CuebotException.Unauthorized) _status.value = ConnectionStatus.Unauthorized
    }
}
