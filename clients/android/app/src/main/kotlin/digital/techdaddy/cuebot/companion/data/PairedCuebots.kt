package digital.techdaddy.cuebot.companion.data

import digital.techdaddy.cuebot.core.CuebotEndpoint
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.util.UUID

/** One paired Cuebot as the app keeps it; the tokens are sealed in [secret]. */
@Serializable
data class PairedCuebot(
    val id: String = UUID.randomUUID().toString(),
    val name: String,
    val host: String,
    val apiPort: Int,
    val httpPort: Int,
    val instanceId: String? = null,
    val secret: String = "",
)

@Serializable
private data class Secrets(val token: String, val deckToken: String? = null)

/** The paired Cuebots and which one is active. */
class PairedCuebots(private val store: KeyValueStore, private val cipher: TokenCipher) {
    /** [revision] changes whenever the active Cuebot or its details change (pairing again keeps the id). */
    data class State(val all: List<PairedCuebot> = emptyList(), val activeId: String? = null, val revision: Int = 0) {
        val active: PairedCuebot? get() = all.firstOrNull { it.id == activeId }
    }

    private val json = Json { ignoreUnknownKeys = true }
    private val _state: MutableStateFlow<State>
    val state: StateFlow<State>

    init {
        val all = store[LIST_KEY]?.let { runCatching { json.decodeFromString<List<PairedCuebot>>(it) }.getOrNull() } ?: emptyList()
        val activeId = store[ACTIVE_KEY]?.takeIf { id -> all.any { it.id == id } } ?: all.firstOrNull()?.id
        _state = MutableStateFlow(State(all, activeId))
        state = _state
    }

    /** The active Cuebot with its tokens, or null when none is paired (or its tokens cannot be opened). */
    fun activeEndpoint(): CuebotEndpoint? {
        val active = _state.value.active ?: return null
        val secrets = cipher.open(active.secret)?.let { runCatching { json.decodeFromString<Secrets>(it) }.getOrNull() } ?: return null
        return CuebotEndpoint(active.name, active.host, active.apiPort, active.httpPort, secrets.token, secrets.deckToken, active.instanceId)
    }

    /** Adds a Cuebot, or updates the one with the same instance id (or host and port), and makes it active. */
    fun pair(endpoint: CuebotEndpoint): PairedCuebot {
        lateinit var entry: PairedCuebot
        update { current ->
            // The same Cuebot: its instance id, else the same address (a reinstalled Cuebot gets a new id but keeps
            // its address, and pairing it again should update the entry, not add a second one).
            val existing = current.all.firstOrNull { e ->
                (endpoint.instanceId != null && endpoint.instanceId == e.instanceId) || (e.host == endpoint.host && e.apiPort == endpoint.apiPort)
            }
            entry = (existing ?: PairedCuebot(name = endpoint.name, host = endpoint.host, apiPort = endpoint.apiPort, httpPort = endpoint.httpPort)).copy(
                name = endpoint.name,
                host = endpoint.host,
                apiPort = endpoint.apiPort,
                httpPort = endpoint.httpPort,
                instanceId = endpoint.instanceId ?: existing?.instanceId,
                secret = cipher.seal(json.encodeToString(Secrets(endpoint.token, endpoint.deckToken))),
            )
            val others = current.all.filter { it.id != entry.id && !(it.host == entry.host && it.apiPort == entry.apiPort) }
            val list = if (existing != null) current.all.filter { it in others || it.id == entry.id }.map { if (it.id == entry.id) entry else it } else others + entry
            current.copy(all = list, activeId = entry.id)
        }
        return entry
    }

    fun activate(id: String) = update { it.copy(activeId = id) }

    fun rename(id: String, name: String) {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return
        update { s -> s.copy(all = s.all.map { if (it.id == id) it.copy(name = trimmed) else it }) }
    }

    fun remove(id: String) = update { s ->
        val all = s.all.filter { it.id != id }
        s.copy(all = all, activeId = if (s.activeId == id) all.firstOrNull()?.id else s.activeId)
    }

    /** Remembers the instance id the status reported, so the same Cuebot is recognized after an address change. */
    fun learnInstanceId(instanceId: String, id: String) {
        val entry = _state.value.all.firstOrNull { it.id == id } ?: return
        if (instanceId.isEmpty() || entry.instanceId == instanceId) return
        update(bumpRevision = false) { s -> s.copy(all = s.all.map { if (it.id == id) it.copy(instanceId = instanceId) else it }) }
    }

    private fun update(bumpRevision: Boolean = true, change: (State) -> State) {
        _state.update { s -> change(s).let { if (bumpRevision) it.copy(revision = s.revision + 1) else it } }
        val s = _state.value
        store.put(LIST_KEY, json.encodeToString(s.all))
        store.put(ACTIVE_KEY, s.activeId)
    }

    private companion object {
        const val LIST_KEY = "pairedCuebots"
        const val ACTIVE_KEY = "activeCuebot"
    }
}
