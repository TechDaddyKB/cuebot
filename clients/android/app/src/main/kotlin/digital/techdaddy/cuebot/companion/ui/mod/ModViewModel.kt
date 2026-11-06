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
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
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

    /** Signed in: the portal UI the streamer's Cuebot sent ([js] null when that Cuebot is too old to send one). */
    class Portal(val membership: Membership, val link: PortalLink, val me: JsonObject, val js: String?, val css: String?) : ModState
}

class ModViewModel(
    private val memberships: ModMemberships,
    private val open: suspend (PortalTarget) -> PortalLink = { PortalLink.open(it) },
    private val now: () -> Long = System::currentTimeMillis,
) : ViewModel() {
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
        link.onClosed { reason -> if (_state.value === portal) _state.value = ModState.Problem(reason) { resume(membership) } }
        _state.value = portal
    }

    /** Signs this phone out of the open portal and forgets the membership and its key. */
    fun signOut() {
        val portal = _state.value as? ModState.Portal ?: return
        viewModelScope.launch {
            runCatching { portal.link.request("signout") }
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
    }
}
