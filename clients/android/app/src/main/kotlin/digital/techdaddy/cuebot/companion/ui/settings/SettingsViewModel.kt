package digital.techdaddy.cuebot.companion.ui.settings

import androidx.lifecycle.ViewModel
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.data.KeyValueStore
import digital.techdaddy.cuebot.companion.data.PairedCuebots
import digital.techdaddy.cuebot.companion.ui.MENU_PINNED_KEY
import digital.techdaddy.cuebot.companion.ui.theme.Appearance
import digital.techdaddy.cuebot.core.EventsCardSettings
import digital.techdaddy.cuebot.core.FeedGroup
import digital.techdaddy.cuebot.core.CuebotEndpoint
import digital.techdaddy.cuebot.core.PairingLink
import kotlinx.coroutines.flow.StateFlow

/** The Settings tab: paired Cuebots, pairing, Appearance, the Chat page's events card, the side menu and About. */
class SettingsViewModel(
    private val paired: PairedCuebots,
    private val connection: Connection,
    private val store: KeyValueStore,
) : ViewModel() {
    val cuebots: StateFlow<PairedCuebots.State> = paired.state
    val status: StateFlow<ConnectionStatus> = connection.status
    val settings: StateFlow<Map<String, String>> = store.values

    fun appearance(values: Map<String, String>) = Appearance.of(values[Appearance.STORAGE_KEY])
    fun setAppearance(appearance: Appearance) = store.put(Appearance.STORAGE_KEY, appearance.key)

    // The Chat page's events card and the side menu: per device, in the app's settings.

    fun eventsCard(values: Map<String, String>) = EventsCardSettings.from(values)
    fun setEventsCardVisible(visible: Boolean) = store.put(EventsCardSettings.VISIBLE_KEY, visible.toString())
    fun setEventGroup(group: FeedGroup, shown: Boolean) {
        val groups = eventsCard(store.values.value).groups
        store.put(EventsCardSettings.GROUPS_KEY, FeedGroup.encode(if (shown) groups + group else groups - group))
    }

    fun menuPinned(values: Map<String, String>) = values[MENU_PINNED_KEY] == "true"
    fun setMenuPinned(pinned: Boolean) = store.put(MENU_PINNED_KEY, pinned.toString())

    fun activate(id: String) = paired.activate(id)
    fun rename(id: String, name: String) = paired.rename(id, name)
    fun remove(id: String) = paired.remove(id)

    /** Pairs from a scanned, pasted or opened link; null when it worked, else what to tell the user. */
    fun pair(link: String): String? = try {
        paired.pair(PairingLink.parse(link))
        null
    } catch (e: PairingLink.Failure) {
        describe(e)
    }

    /** Pairs from typed details (a pasted link in the address field works too). */
    fun pairManually(host: String, apiPort: String, httpPort: String, token: String, deckToken: String, name: String): String? {
        val address = host.trim()
        if (address.lowercase().startsWith("cuebot://")) return pair(address)
        if (address.isEmpty()) return "Enter the computer's address."
        val api = apiPort.trim().toIntOrNull()
        val http = httpPort.trim().toIntOrNull()
        if (api == null || http == null || api !in 1..65535 || http !in 1..65535) return "The ports must be numbers between 1 and 65535."
        if (token.isBlank()) return "Enter the API token Cuebot shows."
        paired.pair(CuebotEndpoint(name.trim().ifEmpty { "Cuebot" }, address, api, http, token.trim(), deckToken.trim().ifEmpty { null }))
        return null
    }

    fun retry() = connection.check()

    companion object {
        fun describe(e: PairingLink.Failure): String = e.message ?: "That is not a Cuebot pairing code."
    }
}
