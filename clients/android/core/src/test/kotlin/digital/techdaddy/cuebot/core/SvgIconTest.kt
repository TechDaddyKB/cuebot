package digital.techdaddy.cuebot.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SvgIconTest {
    @Test fun parsesAnIconifyPath() {
        val icon = SvgIcon.parse("""<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24"><path fill="currentColor" d="M12 2a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V5a3 3 0 0 1 3-3Z"/></svg>""")!!
        assertEquals(24.0, icon.width, 0.0)
        val shape = icon.shapes.single()
        assertTrue(shape.fill && !shape.evenOdd)
        assertEquals(SvgIcon.Segment.Move(SvgIcon.Point(12.0, 2.0)), shape.segments.first())
        assertEquals(SvgIcon.Segment.Close, shape.segments.last())
        assertEquals(SvgIcon.Segment.Line(SvgIcon.Point(15.0, 11.0)), shape.segments.first { it is SvgIcon.Segment.Line })
    }

    @Test fun readsStrokesInheritedFromGroups() {
        val icon = SvgIcon.parse("""<svg viewBox="0 0 24 24"><g fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><rect x="4" y="4" width="4" height="4"/></g></svg>""")!!
        assertEquals(2, icon.shapes.size)
        assertTrue(icon.shapes.all { !it.fill && it.strokeWidth == 2.0 && it.roundCaps })
    }

    @Test fun handlesShorthandAndRelativeCommands() {
        val segs = PathData.parse("m1 1h2v2H1zM5 5l1-1-1-1")
        assertEquals(SvgIcon.Segment.Line(SvgIcon.Point(3.0, 1.0)), segs[1])
        assertEquals(SvgIcon.Segment.Line(SvgIcon.Point(3.0, 3.0)), segs[2])
        assertEquals(SvgIcon.Segment.Close, segs[4])
        assertEquals(SvgIcon.Segment.Line(SvgIcon.Point(5.0, 3.0)), segs.last())
        assertEquals(listOf(1.0, -0.5, 0.25, 3e2), PathData.numbers("1-.5.25 3e2"))
    }

    @Test fun rejectsWhatItCannotDraw() {
        assertNull(SvgIcon.parse("not svg"))
        assertNull(SvgIcon.parse("""<svg viewBox="0 0 0 24"><path d="M0 0h1"/></svg>"""))
        assertNull(SvgIcon.parse("""<svg viewBox="0 0 24 24"></svg>"""))
    }

    @Test fun iconifyNamesBecomeUrls() {
        assertEquals("https://api.iconify.design/mdi/microphone-off.svg", Iconify.url("mdi:Microphone-Off"))
        assertNull(Iconify.url("microphone"))
        assertNull(Iconify.url("mdi:../x"))
    }
}
