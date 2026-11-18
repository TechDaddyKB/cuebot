package digital.techdaddy.cuebot.companion.ui.mod

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import digital.techdaddy.cuebot.companion.data.Membership
import digital.techdaddy.cuebot.companion.data.ModMemberships
import digital.techdaddy.cuebot.core.portal.LinkClosed
import digital.techdaddy.cuebot.core.portal.PortalLink
import digital.techdaddy.cuebot.core.portal.PortalProvider
import digital.techdaddy.cuebot.core.portal.PortalSignIn
import digital.techdaddy.cuebot.core.portal.PortalTarget
import digital.techdaddy.cuebot.core.portal.deviceProof
import digital.techdaddy.cuebot.core.portal.error
import digital.techdaddy.cuebot.core.portal.ok
import digital.techdaddy.cuebot.core.portal.string
import digital.techdaddy.cuebot.companion.Alert
import digital.techdaddy.cuebot.companion.AlertMemory
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.util.UUID

/** Where the mod mode is (docs/MOD_PORTAL_PLAN.md, section 6: the phone app joins by invite, then shows the portal). */
sealed interface ModState {
    /** The streamers this phone mods for, and joining with an invite. */
    data object Home : ModState

    data class Busy(val title: String, val text: String) : ModState

    data class Providers(val target: PortalTarget, val providers: List<PortalProvider>) : ModState {
        val joining: Boolean get() = target.invite != null
    }

    /** The provider's page is open in the browser; it comes back through the portal's return page. */
    data class InBrowser(val provider: String) : ModState

    data class Problem(val text: String, val retry: (() -> Unit)? = null) : ModState

    /**
     * Signed in: the portal UI the streamer's Cuebot sent ([js] null when that Cuebot is too old to send one). When the
     * link drops, the app reconnects by itself and swaps in the new [link]; the portal stays on screen meanwhile, with
     * [reconnecting] set.
     */
    class Portal(val membership: Membership, link: PortalLink, val me: JsonObject, val js: String?, val css: String?) : ModState {
        @Volatile var link: PortalLink = link
            internal set
        val reconnecting = MutableStateFlow(false)

        /** One request on the current link; while reconnecting, it waits for the new link first (up to a while). */
        suspend fun request(op: String, body: JsonObject = JsonObject(emptyMap())): JsonObject {
            if (reconnecting.value) withTimeoutOrNull(RECONNECT_WAIT_MS) { reconnecting.first { !it } }
            return link.request(op, body)
        }

        companion object {
            const val RECONNECT_WAIT_MS = 30_000L
        }
    }
}

class ModViewModel(
    private val memberships: ModMemberships,
    private val open: suspend (PortalTarget) -> PortalLink = { PortalLink.open(it) },
    private val now: () -> Long = System::currentTimeMillis,
    /** True while the app is on screen: requests are checked only then. */
    private val foreground: StateFlow<Boolean> = MutableStateFlow(true),
    /** Shows a notification for a request waiting for a yes. */
    alert: (Alert) -> Unit = {},
    private val requestInterval: Long = 10_000,
    /** The waits between tries to get a dropped portal link back, before the mod is told it is lost. */
    private val reconnectDelays: List<Long> = listOf(500, 1_000, 2_000, 4_000, 8_000, 15_000, 15_000),
) : ViewModel() {
    private val alerts = AlertMemory(alert)
    private val _state = MutableStateFlow<ModState>(ModState.Home)
    val state: StateFlow<ModState> = _state
    val all: StateFlow<List<Membership>> = memberships.all

    private val _browser = Channel<String>(Channel.BUFFERED)
    /** Provider pages to open in a browser tab (a Custom Tab: providers refuse sign-in inside an app's WebView). */
    val browser = _browser.receiveAsFlow()

    /** A note on the home screen ("Signed out"), or null. */
    val notice = MutableStateFlow<String?>(null)

    /** Opens an invite (or portal) link; the reason when it is not one. */
    fun openInvite(link: String): String? {
        val target = PortalTarget.parse(link) ?: return "That is not a mod portal invite. Ask the streamer for the invite link or its QR code."
        if (target.invite == null) {
            memberships.find(target.relay)?.let { resume(it); return null }
            return "That link has no invite in it. Ask the streamer for a new invite."
        }
        chooseProvider(target)
        return null
    }

    /** The provider buttons. With an invite the mod joins; without one a member signs in again. */
    fun chooseProvider(target: PortalTarget) {
        val title = if (target.invite != null) "Join as a mod" else "Sign in again"
        _state.value = ModState.Busy(title, "Connecting to the streamer's Cuebot…")
        viewModelScope.launch {
            val link = connect(target) { chooseProvider(target) } ?: return@launch
            val answer = link.request("providers")
            link.close()
            val providers = PortalProvider.list(answer)
            _state.value = if (!answer.ok || providers.isEmpty()) ModState.Problem(answer.error ?: "This Cuebot has no sign-in options set up.")
            else ModState.Providers(target, providers)
        }
    }

    fun signIn(target: PortalTarget, provider: PortalProvider) {
        val pending = PortalSignIn.start(target, provider)
        val url = try {
            PortalSignIn.authorizeUrl(provider, pending)
        } catch (e: IllegalArgumentException) {
            _state.value = ModState.Problem(e.message ?: "That sign-in address is not allowed.")
            return
        }
        memberships.pending = pending
        _state.value = ModState.InBrowser(provider.name)
        _browser.trySend(url)
    }

    /** The phone has no browser to sign in with. */
    fun noBrowser() {
        memberships.pending = null
        _state.value = ModState.Problem("Signing in needs a web browser on this phone, like Chrome. Install or turn one on, then try again.")
    }

    /** Back from the provider: the return page opened cuebot-portal://signin?code=…&state=… */
    fun finishSignIn(returnLink: String) {
        val pending = memberships.pending
        memberships.pending = null
        val again = pending?.let { p -> { chooseProvider(p.target) } }
        val code = when (val r = PortalSignIn.readReturn(returnLink, pending)) {
            is PortalSignIn.Return.Failed -> { _state.value = ModState.Problem(r.message, again); return }
            is PortalSignIn.Return.Code -> r.code
        }
        val target = pending!!.target
        _state.value = ModState.Busy("Signing in", "Checking your account with the streamer's Cuebot…")
        viewModelScope.launch {
            val link = connect(target, again) ?: return@launch
            val existing = memberships.find(target.relay)
            // A new membership (an invite) gets its own key; signing in again keeps this device's key.
            val reuse = existing?.takeIf { target.invite == null && it.target.fingerprint == target.fingerprint }?.let { m -> memberships.keys.open(m.keyAlias)?.let { m.keyAlias to it } }
            val (alias, key) = reuse ?: newAlias().let { it to memberships.keys.create(it) }
            val body = buildJsonObject {
                put("provider", pending.provider)
                put("code", code)
                put("verifier", pending.verifier)
                put("device", link.deviceProof(key, DEVICE_NAME))
                target.invite?.let { put("invite", it) }
            }
            val answer = link.request("signin", body)
            if (!answer.ok) {
                link.close()
                if (reuse == null) memberships.keys.delete(alias)
                _state.value = ModState.Problem(answer.error ?: "The sign-in failed.", again)
                return@launch
            }
            welcome(link, target, alias, answer)
        }
    }

    /** Opens the portal for a streamer this phone already mods for. */
    fun resume(membership: Membership) {
        _state.value = ModState.Busy(membership.name, "Connecting to the streamer's Cuebot…")
        viewModelScope.launch {
            val signInAgain = { chooseProvider(membership.target) }
            val key = memberships.keys.open(membership.keyAlias) ?: return@launch signInAgain()
            val link = connect(membership.target) { resume(membership) } ?: return@launch
            val answer = link.request("resume", buildJsonObject { put("device", link.deviceProof(key)) })
            when {
                (answer["signIn"] as? JsonPrimitive)?.content == "true" -> { link.close(); signInAgain() }
                !answer.ok -> { link.close(); _state.value = ModState.Problem(answer.error ?: "Could not sign in.") { resume(membership) } }
                else -> welcome(link, membership.target, membership.keyAlias, answer)
            }
        }
    }

    private suspend fun welcome(link: PortalLink, target: PortalTarget, alias: String, me: JsonObject) {
        val name = memberships.find(target.relay)?.name ?: Membership.DEFAULT_NAME
        val membership = Membership(target.copy(invite = null), name, me.string("name") ?: "", me.string("role") ?: "", alias, now())
        memberships.save(membership)
        val ui = link.request("ui")
        val portal = ModState.Portal(membership, link, me, ui.string("js")?.takeIf { ui.ok }, ui.string("css")?.takeIf { ui.ok })
        watchLink(portal, link, alias)
        _state.value = portal
        watchRequests(portal)
    }

    private fun watchLink(portal: ModState.Portal, link: PortalLink, alias: String) {
        link.onClosed { reason -> viewModelScope.launch { reconnect(portal, alias, reason) } }
    }

    /**
     * The link dropped (a network change, the phone waking up, a relay restart): reconnect and resume with this
     * device's key, keeping the portal and its page on screen. Requests made meanwhile wait for the new link. Only
     * when every try fails, or Cuebot wants a new sign-in, does the mod see a problem.
     */
    private suspend fun reconnect(portal: ModState.Portal, alias: String, reason: String) {
        if (_state.value !== portal || portal.reconnecting.value) return
        portal.reconnecting.value = true
        val target = portal.membership.target
        for (wait in reconnectDelays) {
            delay(wait)
            if (_state.value !== portal) return
            val key = memberships.keys.open(alias) ?: break
            val link = runCatching { open(target) }.getOrNull() ?: continue
            val answer = withTimeoutOrNull(RESUME_TIMEOUT_MS) { link.request("resume", buildJsonObject { put("device", link.deviceProof(key)) }) }
            when {
                // No answer, or one without "req" (the link's own: it dropped again): try again. Cuebot's answers carry "req".
                answer == null || answer["req"] == null -> { link.close(); continue }
                _state.value !== portal -> { link.close(); return }
                answer.ok -> {
                    portal.link = link
                    watchLink(portal, link, alias)
                    portal.reconnecting.value = false
                    return
                }
                (answer["signIn"] as? JsonPrimitive)?.content == "true" -> { link.close(); chooseProvider(target); return }
                else -> {
                    link.close()
                    _state.value = ModState.Problem(answer.error ?: reason) { resume(portal.membership) }
                    return
                }
            }
        }
        if (_state.value === portal) _state.value = ModState.Problem(reason) { resume(portal.membership) }
    }

    /**
     * While this portal is open and the app on screen, asks Cuebot every few seconds for its requests (e.g. "Switch
     * the category to Helldivers 2?") and alerts new ones, so the mod sees them from any page. The mod answers on the
     * portal's Home page. A role without stream control gets an error answer here, and nothing is shown.
     */
    private fun watchRequests(portal: ModState.Portal) {
        viewModelScope.launch {
            while (_state.value === portal) {
                if (foreground.value) {
                    val answer = runCatching { portal.request("requests.list") }.getOrNull()
                    val list = (answer?.takeIf { it.ok }?.get("requests") as? JsonArray)?.mapNotNull { it as? JsonObject } ?: emptyList()
                    alerts.update(list, { it.string("id") ?: "" }) { r ->
                        val detail = r.string("detail").orEmpty()
                        Alert("mod-request-${r.string("id")}", "${portal.membership.name}: Cuebot needs a yes",
                            listOf(r.string("title").orEmpty(), detail).filter { it.isNotEmpty() }.joinToString(" ") +
                                " Answer on the Home page.")
                    }
                }
                delay(requestInterval)
            }
        }
    }

    /** Signs this phone out of the open portal and forgets the membership and its key. */
    fun signOut() {
        val portal = _state.value as? ModState.Portal ?: return
        viewModelScope.launch {
            runCatching { portal.request("signout") }
            portal.link.close()
            memberships.forget(portal.membership.relay)
            notice.value = "Signed out of ${portal.membership.name} on this phone."
            _state.value = ModState.Home
        }
    }

    /** Leaves the portal (the phone stays signed in) or a screen in the join flow. */
    fun home() {
        (_state.value as? ModState.Portal)?.link?.close()
        if (_state.value is ModState.InBrowser) memberships.pending = null
        _state.value = ModState.Home
    }

    fun rename(membership: Membership, name: String) = memberships.rename(membership.relay, name)

    /** Forgets a membership without asking Cuebot (for one this phone can no longer reach). */
    fun forget(membership: Membership) = memberships.forget(membership.relay)

    private suspend fun connect(target: PortalTarget, retry: (() -> Unit)?): PortalLink? = try {
        open(target)
    } catch (e: LinkClosed) {
        _state.value = ModState.Problem(e.message ?: "Could not reach the streamer's Cuebot.", retry)
        null
    } catch (e: Exception) {
        _state.value = ModState.Problem("Could not reach the streamer's Cuebot.", retry)
        null
    }

    override fun onCleared() {
        (_state.value as? ModState.Portal)?.link?.close()
    }

    private fun newAlias() = "cuebot-portal-" + UUID.randomUUID()

    companion object {
        /** How this phone appears in the streamer's list of the member's devices. */
        const val DEVICE_NAME = "Cuebot app on Android"
        private const val RESUME_TIMEOUT_MS = 15_000L
    }
}
