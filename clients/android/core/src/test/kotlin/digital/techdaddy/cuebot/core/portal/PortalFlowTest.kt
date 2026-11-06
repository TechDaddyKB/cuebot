package digital.techdaddy.cuebot.core.portal

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PortalFlowTest {
    private val relay = "fyrsbvvtain3yaljh3d4eu5ka25jvkjt"
    private val key = "LiMg1rMCG7wBaT7HwlOqBrqaqTOGyoa7JxAEw-mJEuc"
    private val secret = "x".repeat(43)

    @Test fun `invite links name the relay next to the portal`() {
        val t = PortalTarget.parse("https://portal.techdaddydigital.com/#i=$secret&r=$relay&k=$key")!!
        assertEquals(PortalTarget(relay, key, "wss://relay.techdaddydigital.com", secret), t)
        assertEquals("wss://cuebot-portal.example.workers.dev", PortalTarget.parse("https://cuebot-portal.example.workers.dev/#r=$relay&k=$key")!!.relayOrigin)
        assertNull(PortalTarget.parse("https://portal.techdaddydigital.com/#r=$relay&k=$key")!!.invite)
    }

    @Test fun `anything else is not an invite`() {
        assertNull(PortalTarget.parse("http://portal.techdaddydigital.com/#i=$secret&r=$relay&k=$key"))
        assertNull(PortalTarget.parse("https://portal.techdaddydigital.com/?r=$relay&k=$key"))
        assertNull(PortalTarget.parse("https://portal.techdaddydigital.com/#r=${relay.uppercase()}&k=$key"))
        assertNull(PortalTarget.parse("https://portal.techdaddydigital.com/#i=short&r=$relay&k=$key"))
        assertNull(PortalTarget.parse("cuebot://pair?v=1"))
        assertNull(PortalTarget.parse("not a link"))
    }

    private val google = PortalProvider("google", "Google", "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id=c", pkce = true)

    @Test fun `providers come from Cuebot's answer and only known hosts are opened`() {
        val answer = Json.parseToJsonElement("""{"ok":true,"providers":[{"id":"google","name":"Google","authorize":"${google.authorize}","pkce":true},
            {"id":"twitch","name":"Twitch","authorize":"https://id.twitch.tv/oauth2/authorize?client_id=t","pkce":false},{"name":"broken"}]}""").jsonObject
        assertEquals(listOf("google", "twitch"), PortalProvider.list(answer).map { it.id })

        val pending = PortalSignIn.start(PortalTarget(relay, key, "wss://relay.example", secret), google)
        assertTrue(pending.state.startsWith("app."))
        val url = PortalSignIn.authorizeUrl(google, pending).toHttpUrl()
        assertEquals(pending.state, url.queryParameter("state"))
        assertEquals(PortalSignIn.challenge(pending.verifier), url.queryParameter("code_challenge"))
        assertEquals("S256", url.queryParameter("code_challenge_method"))
        val twitch = PortalSignIn.authorizeUrl(PortalProvider.list(answer)[1], pending).toHttpUrl()
        assertNull(twitch.queryParameter("code_challenge"))
        assertThrows(IllegalArgumentException::class.java) { PortalSignIn.authorizeUrl(google.copy(authorize = "https://evil.example/auth"), pending) }
        assertThrows(IllegalArgumentException::class.java) { PortalSignIn.authorizeUrl(google.copy(authorize = "http://accounts.google.com/auth"), pending) }
        // RFC 7636, appendix B.
        assertEquals("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", PortalSignIn.challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"))
    }

    @Test fun `the return hand-over is checked against the sign-in this app started`() {
        val pending = PendingSignIn(PortalTarget(relay, key, "wss://relay.example"), "google", "app.abc", "v")
        assertEquals(PortalSignIn.Return.Code("4/0Ab_x"), PortalSignIn.readReturn("cuebot-portal://signin?code=4%2F0Ab_x&state=app.abc", pending))
        assertEquals(PortalSignIn.Return.Failed("This sign-in did not come from this app. Start again."),
            PortalSignIn.readReturn("cuebot-portal://signin?code=c&state=app.other", pending))
        assertEquals(PortalSignIn.Return.Failed("The sign-in was cancelled."), PortalSignIn.readReturn("cuebot-portal://signin?error=access_denied&state=app.abc", pending))
        assertTrue(PortalSignIn.readReturn("cuebot-portal://signin?code=c&state=app.abc", null) is PortalSignIn.Return.Failed)
        assertTrue(PortalSignIn.readReturn("cuebot://pair?code=c&state=app.abc", pending) is PortalSignIn.Return.Failed)
        assertTrue(PortalSignIn.readReturn("cuebot-portal://signin?state=app.abc", pending) is PortalSignIn.Return.Failed)
    }
}
