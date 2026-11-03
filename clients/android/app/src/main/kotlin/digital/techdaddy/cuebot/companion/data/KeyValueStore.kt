package digital.techdaddy.cuebot.companion.data

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

/** The app's few settings as strings: read at once from memory, written through to disk in the background. */
interface KeyValueStore {
    val values: StateFlow<Map<String, String>>

    operator fun get(key: String): String? = values.value[key]

    /** Sets [key], or removes it when [value] is null. */
    fun put(key: String, value: String?)
}

/** In memory only: for tests. */
class MemoryKeyValueStore(initial: Map<String, String> = emptyMap()) : KeyValueStore {
    private val state = MutableStateFlow(initial)
    override val values: StateFlow<Map<String, String>> = state

    override fun put(key: String, value: String?) = state.update { if (value == null) it - key else it + (key to value) }
}

/**
 * Preferences DataStore. The file is small, so it is read once at start (blocking, before the first screen); after
 * that every change is in memory at once and the latest snapshot is written out, so writes cannot overtake each other.
 */
class DataStoreKeyValueStore(private val store: DataStore<Preferences>, scope: CoroutineScope) : KeyValueStore {
    private val state = MutableStateFlow(runBlocking { store.data.first() }.asMap().entries.associate { (k, v) -> k.name to v.toString() })
    private val dirty = Channel<Unit>(Channel.CONFLATED)
    override val values: StateFlow<Map<String, String>> = state

    init {
        scope.launch {
            for (unused in dirty) {
                val snapshot = state.value
                store.edit { prefs ->
                    prefs.clear()
                    snapshot.forEach { (k, v) -> prefs[stringPreferencesKey(k)] = v }
                }
            }
        }
    }

    override fun put(key: String, value: String?) {
        state.update { if (value == null) it - key else it + (key to value) }
        dirty.trySend(Unit)
    }
}
