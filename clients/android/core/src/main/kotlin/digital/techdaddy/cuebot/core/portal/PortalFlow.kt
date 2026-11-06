package digital.techdaddy.cuebot.core.portal

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.net.URI
import java.net.URLDecoder

/**
 * What an invite link (or a saved membership) points at: the streamer's relay ID, the fingerprint of their Cuebot's
 * identity key, the invite secret when joining, and where the relay is. Everything comes from the link's fragment,
 * which no server sees (docs/MOD_PORTAL_PLAN.md, section 4).
 */
@Serializable
data class PortalTarget(
    val relay: String,
    val fingerprint: String,
    val relayOrigin: String,
    val invite: String? = null,
) {
    companion object {
        private val RELAY_ID = Regex("^[a-z2-7]{32}$")
        private val SECRET = Regex("^[A-Za-z0-9_-]{43}$")

        /**
         * Reads `https://portal.<domain>/#i=<invite>&r=<relay id>&k=<fingerprint>`; null when it is not an invite (or
         * portal) link. The relay is relay.<domain> next to portal.<domain>, else the link's own host.
         */
        fun parse(link: String): PortalTarget? {
            val uri = runCatching { URI(link.trim()) }.getOrNull() ?: return null
            if (uri.scheme != "https" || uri.host.isNullOrEmpty()) return null
            val p = (uri.rawFragment ?: return null).split('&').mapNotNull { part ->
                val at = part.indexOf('=')
                if (at <= 0) null else part.substring(0, at) to URLDecoder.decode(part.substring(at + 1), "UTF-8")
            }.toMap()
            val relay = p["r"] ?: ""
            val fingerprint = p["k"] ?: ""
            val invite = p["i"]?.takeIf { it.isNotEmpty() }
            if (!RELAY_ID.matches(relay) || !SECRET.matches(fingerprint)) return null
            if (invite != null && !SECRET.matches(invite)) return null
            val host = uri.host + (if (uri.port > 0) ":${uri.port}" else "")
            val origin = if (host.startsWith("portal.")) "wss://relay.${host.removePrefix("portal.")}" else "wss://$host"
            return PortalTarget(relay, fingerprint, origin, invite)
        }
    }
}

/** A provider as Cuebot describes it (`providers`): the authorize URL lacks only state and the PKCE challenge. */
data class PortalProvider(val id: String, val name: String, val authorize: String, val pkce: Boolean) {
    companion object {
        fun list(answer: JsonObject): List<PortalProvider> = (answer["providers"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = e as? JsonObject ?: return@mapNotNull null
            val id = o.string("id") ?: return@mapNotNull null
            val authorize = o.string("authorize") ?: return@mapNotNull null
            PortalProvider(id, o.string("name") ?: id, authorize, (o["pkce"] as? JsonPrimitive)?.booleanOrNull == true)
        }
    }
}

/** What survives the trip to the provider and back (kept sealed while the browser is open). */
@Serializable
data class PendingSignIn(val target: PortalTarget, val provider: String, val state: String, val verifier: String)

/**
 * The sign-in steps that need no screen or network (portal/web/flow.ts does the same in a browser). The provider
 * sends the mod back to the one fixed return page on portal.<domain>; when [PendingSignIn.state] starts with
 * [APP_STATE], that page hands the code to this app instead of signing the browser in.
 */
object PortalSignIn {
    /** The prefix the return page looks for; "." never appears in base64url, so a browser's own state never has it. */
    const val APP_STATE = "app."

    /** The app link the return page opens: cuebot-portal://signin?code=…&state=… */
    const val RETURN_SCHEME = "cuebot-portal"
    const val RETURN_HOST = "signin"

    /** Only the provider pages Cuebot may send a mod to. */
    private val PROVIDER_HOSTS = setOf("accounts.google.com", "id.twitch.tv", "discord.com")

    fun start(target: PortalTarget, provider: PortalProvider): PendingSignIn =
        PendingSignIn(target, provider.id, APP_STATE + PortalCrypto.base64Url(PortalCrypto.randomBytes(32)), PortalCrypto.base64Url(PortalCrypto.randomBytes(48)))

    fun challenge(verifier: String): String = PortalCrypto.base64Url(PortalCrypto.sha256(verifier.toByteArray()))

    /** The address to send the mod to; throws when Cuebot named a page that is not a known provider's. */
    fun authorizeUrl(provider: PortalProvider, pending: PendingSignIn): String {
        val url = provider.authorize.toHttpUrlOrNull()
        if (url == null || url.scheme != "https" || url.host !in PROVIDER_HOSTS) throw IllegalArgumentException("Unexpected sign-in address for ${provider.name}.")
        val b = url.newBuilder().setQueryParameter("state", pending.state)
        if (provider.pkce) b.setQueryParameter("code_challenge", challenge(pending.verifier)).setQueryParameter("code_challenge_method", "S256")
        return b.build().toString()
    }

    sealed interface Return {
        data class Code(val code: String) : Return
        data class Failed(val message: String) : Return
    }

    /** The return page's hand-over (`cuebot-portal://signin?…`), checked against the sign-in this app started. */
    fun readReturn(link: String, pending: PendingSignIn?): Return {
        val uri = runCatching { URI(link) }.getOrNull()
        if (uri == null || uri.scheme != RETURN_SCHEME || uri.host != RETURN_HOST) return Return.Failed("That is not a sign-in answer.")
        val p = (uri.rawQuery ?: "").split('&').mapNotNull { part ->
            val at = part.indexOf('=')
            if (at <= 0) null else part.substring(0, at) to URLDecoder.decode(part.substring(at + 1), "UTF-8")
        }.toMap()
        if (pending == null) return Return.Failed("This sign-in has expired. Open the invite again.")
        if (p["state"] != pending.state) return Return.Failed("This sign-in did not come from this app. Start again.")
        p["error"]?.let { return Return.Failed(if (it == "access_denied") "The sign-in was cancelled." else "The sign-in failed ($it).") }
        return p["code"]?.takeIf { it.isNotEmpty() }?.let(Return::Code) ?: Return.Failed("The provider did not send a sign-in code. Start again.")
    }
}
