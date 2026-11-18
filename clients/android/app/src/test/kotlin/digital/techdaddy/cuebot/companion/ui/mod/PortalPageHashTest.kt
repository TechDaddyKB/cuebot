package digital.techdaddy.cuebot.companion.ui.mod

import org.junit.Assert.assertEquals
import org.junit.Test

/** The portal page kept across a rotation: only a page id from the portal's own hash, never the pop-out or anything else. */
class PortalPageHashTest {
    @Test
    fun keeps_a_page_id() {
        assertEquals("#chat", pageHash("https://appassets.androidplatform.net/assets/portal/index.html#chat"))
        assertEquals("#my-changes", pageHash("https://appassets.androidplatform.net/assets/portal/index.html#my-changes"))
    }

    @Test
    fun drops_anything_else() {
        assertEquals("", pageHash(null))
        assertEquals("", pageHash("https://appassets.androidplatform.net/assets/portal/index.html"))
        assertEquals("", pageHash("https://appassets.androidplatform.net/assets/portal/index.html#chat-popout"))
        assertEquals("", pageHash("https://appassets.androidplatform.net/assets/portal/index.html#x\"><script>"))
        assertEquals("", pageHash("https://appassets.androidplatform.net/assets/portal/index.html#Chat"))
    }
}
