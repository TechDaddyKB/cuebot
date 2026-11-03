package digital.techdaddy.cuebot.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerSentEventParserTest {
    private fun parse(text: String, chunk: Int = Int.MAX_VALUE): Pair<List<ServerEvent>, ServerSentEventParser> {
        val parser = ServerSentEventParser()
        val bytes = text.toByteArray()
        val events = mutableListOf<ServerEvent>()
        var i = 0
        while (i < bytes.size) {
            val n = minOf(chunk, bytes.size - i)
            events += parser.feed(bytes, i, n)
            i += n
        }
        return events to parser
    }

    @Test fun parsesCuebotsStream() {
        val (events, parser) = parse(": connected\n\nid: 7\nevent: twitch.cheer\ndata: {\"bits\":100}\n\n: keep-alive\n\n")
        assertEquals(listOf(ServerEvent("twitch.cheer", """{"bits":100}""", "7")), events)
        assertEquals(listOf("connected", "keep-alive"), parser.comments.toList())
        assertEquals("7", parser.lastEventId)
    }

    @Test fun joinsDataLinesAndDefaultsTheType() {
        assertEquals(listOf(ServerEvent(data = "a\nb\n")), parse("data: a\ndata:b\ndata\n\n").first)
    }

    @Test fun acceptsCrLfAndBareCrAcrossChunkBoundaries() {
        for (size in listOf(1, 2, 3, 7)) {
            val (events, _) = parse("event: state\r\ndata: {}\r\n\r\nevent: notify\rdata: x\r\r", size)
            assertEquals("chunk $size", listOf("state", "notify"), events.map { it.type })
        }
    }

    @Test fun keepsTheLastIdAndRetry() {
        val (events, parser) = parse("id: 1\nfoo: bar\nretry: 2500\ndata: one\n\ndata: two\n\nid: 9\n\n")
        assertEquals(listOf("1", "1"), events.map { it.id })
        assertEquals(2500L, parser.retryMilliseconds)
        assertEquals("9", parser.lastEventId)
    }

    @Test fun dispatchesNothingWithoutDataOrBeforeTheBlankLine() {
        assertTrue(parse("event: x\n\nid: 3\n\n").first.isEmpty())
        assertTrue(parse("data: half").first.isEmpty())
    }

    @Test fun backoffFollowsThePlan() {
        assertEquals(listOf(1.0, 2.0, 4.0, 8.0, 15.0, 15.0, 15.0), (0..6).map(Backoff.Standard::delay))
    }
}
