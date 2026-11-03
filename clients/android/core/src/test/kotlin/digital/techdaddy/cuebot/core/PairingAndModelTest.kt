package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingLinkTest {
    @Test fun parsesEveryField() {
        val e = PairingLink.parse("cuebot://pair?v=1&name=Studio%20PC&host=192.168.1.20&api=7575&http=7574&token=abc&deckToken=dk&id=inst-1")
        assertEquals(CuebotEndpoint("Studio PC", "192.168.1.20", 7575, 7574, "abc", "dk", "inst-1"), e)
        assertEquals("http://192.168.1.20:7575", e.apiBase)
        assertEquals("http://192.168.1.20:7574", e.httpBase)
    }

    @Test fun fillsDefaultsAndRejectsBadLinks() {
        val e = PairingLink.parse("  CUEBOT://pair?host=pc.local&token=t  ")
        assertTrue(e.apiPort == 7475 && e.httpPort == 7474 && e.name == "Cuebot" && e.deckToken == null)
        assertThrows(PairingLink.Failure.NotAPairingLink::class.java) { PairingLink.parse("https://example.com/pair?host=x&token=y") }
        assertEquals(PairingLink.Failure.Missing("token"), runCatching { PairingLink.parse("cuebot://pair?host=x") }.exceptionOrNull())
        assertEquals(PairingLink.Failure.Missing("host"), runCatching { PairingLink.parse("cuebot://pair?token=x") }.exceptionOrNull())
        assertEquals(PairingLink.Failure.UnsupportedVersion("2"), runCatching { PairingLink.parse("cuebot://pair?v=2&host=x&token=y") }.exceptionOrNull())
        assertEquals(PairingLink.Failure.BadPort("api"), runCatching { PairingLink.parse("cuebot://pair?host=x&token=y&api=99999") }.exceptionOrNull())
    }

    @Test fun keepsPlusSignsAndBracketsIpv6() {
        assertEquals("a+b", PairingLink.parse("cuebot://pair?host=x&token=a+b").token)
        assertEquals("http://[fe80::1]:7475", CuebotEndpoint(host = "fe80::1", token = "t").apiBase)
    }

    @Test fun neverPrintsTokens() {
        assertFalse(CuebotEndpoint(host = "h", token = "secret", deckToken = "deck").toString().contains("secret"))
    }
}

class ModelTest {
    @Test fun statusToleratesUnknownValues() {
        val s = StatusResponse.from(parseJson("""
            { "app": { "name": "Cuebot", "version": "0.1.0", "instanceId": "abc", "apiVersion": "v1", "extra": 1 },
              "connections": [ { "name": "Twitch", "state": "Connected", "status": "ok" }, { "name": "OBS", "state": "Sleeping" },
                               { "name": "Kick", "state": 3 } ] }"""))
        assertEquals("abc", s.app.instanceId)
        assertEquals(listOf(ConnectionState.Connected, ConnectionState.Unknown, ConnectionState.Unknown), s.connections.map { it.state })
    }

    @Test fun deckViewDecodesAndClamps() {
        val v = DeckView.from(parseJson("""
            { "id": "d1", "name": "Main deck", "rows": 3, "columns": 5, "background": "#16161c", "gap": 8, "page": 0, "pageName": "Main",
              "pageCount": 2, "items": [ { "id": "i1", "name": "Mic", "row": 0, "column": 1, "title": "Mute", "icon": "mdi:microphone", "value": "on" } ] }"""))!!
        assertEquals("mdi:microphone", v.item(0, 1)?.icon)
        assertNull(v.item(2, 2))
        assertEquals("Mute", v.items[0].accessibilityName)
        val empty = DeckView.from(parseJson("""{ "rows": 0, "gap": -3 }"""))!!
        assertTrue(empty.rows == 1 && empty.columns == 5 && empty.gap == 0 && empty.items.isEmpty())
    }

    @Test fun dashboardDecodesEveryPart() {
        val d = Dashboard.from(parseJson("""
            { "instanceId": "abc", "revision": 42, "connections": [ { "name": "Twitch", "state": "Connecting" } ],
              "totals": { "cheer-session": { "count": 1, "amount": 500 } },
              "subathon": {
                "timers": [ { "id": "t1", "name": "Main", "state": "running", "remaining": 3540.5, "endsAt": 1762130000000, "added": 3590.0, "multiplier": 2.0 },
                            { "id": "t2", "name": "Side", "state": "paused", "remaining": 75.9, "endsAt": null } ],
                "goals": [ { "id": "g1", "name": "Points", "points": 12, "steps": [ { "name": "Emote", "points": 10, "reached": true }, { "name": "Second", "points": 20 } ],
                             "next": { "name": "Second", "points": 20 }, "previous": 10 } ] },
              "topSpenders": { "currency": "USD", "periods": {
                "week": { "all": { "total": 1, "formattedTotal": "$1.00", "spenders": [] } },
                "stream": { "all": { "total": 22.34, "formattedTotal": "$22.34", "spenders": [
                    { "rank": 1, "name": "ann", "platform": "Twitch", "amount": 22.34, "formatted": "$22.34", "count": 2 } ] },
                            "twitch": { "total": 22.34, "spenders": [] } } } },
              "feed": [ { "platform": "Twitch", "kind": "Cheer", "user": "ann", "detail": "Cheer100 hype", "amount": "500 bits", "isTest": true } ] }"""))
        assertEquals(42L, d.revision)
        val main = d.subathon.timers[0]
        assertTrue(main.state == SubathonTimer.State.Running && main.multiplier == 2.0)
        assertEquals(90.0, main.remainingAt(1_762_130_000_000L - 90_000), 1e-9)
        assertEquals(0.0, main.remainingAt(1_762_130_100_000L), 1e-9)
        assertEquals(75.9, d.subathon.timers[1].remainingAt(System.currentTimeMillis()), 1e-9)
        val goal = d.subathon.goals[0]
        assertEquals("Second", goal.next?.name)
        assertEquals(0.2, goal.progressToNext, 1e-9)
        assertEquals(listOf("stream", "week"), d.topSpenders.periodKeys)
        assertEquals(listOf("all", "twitch"), d.topSpenders.platformKeys("stream"))
        assertEquals("$22.34", d.topSpenders.board("stream").spenders.first().formatted)
        assertTrue(d.topSpenders.board("day").spenders.isEmpty())
        assertTrue(d.feed[0].isTest && d.feed[0].detail == "Cheer100 hype")
    }

    @Test fun aBrokenPartFallsBackInsteadOfFailingTheSnapshot() {
        val d = Dashboard.from(parseJson("""{ "revision": 7, "subathon": "nope", "feed": { }, "topSpenders": null }"""))
        assertTrue(d.revision == 7L && d.subathon.timers.isEmpty() && d.feed.isEmpty() && d.topSpenders.periodKeys.isEmpty())
    }

    @Test fun apiEventsUnwrapTypeTimeAndArgs() {
        val e = ServerEvent("dashboard.changed", """{"type":"dashboard.changed","time":"2026-11-03T01:00:00Z","args":{"revision":42}}""", "5")
        assertEquals("dashboard.changed", e.apiEvent.type)
        assertEquals(42.0, e.apiEvent.args["revision"].numberValue()!!, 0.0)
        assertEquals("x.y", ServerEvent("x.y", "not json").apiEvent.type)
    }

    @Test fun timersFormatLikeCuebot() {
        assertEquals("0:59:00", SubathonTimer.format(3540.9))
        assertEquals("1:01:01:01", SubathonTimer.format(90_061.0))
        assertEquals("0:00:00", SubathonTimer.format(-5.0))
    }

    @Test fun goalProgressCoversTheEnds() {
        val steps = listOf(GoalStep("a", 10.0), GoalStep("b", 30.0))
        assertEquals(0.0, PointsGoal("g", "g", 0.0, steps).progressToNext, 0.0)
        assertEquals(0.5, PointsGoal("g", "g", 20.0, steps).progressToNext, 0.0)
        assertEquals(1.0, PointsGoal("g", "g", 40.0, steps).progressToNext, 0.0)
        assertEquals(0.0, PointsGoal("g", "g", 5.0, emptyList()).progressToNext, 0.0)
    }

    @Test fun streamTotalsComeFromTheSessionEntries() {
        fun o(vararg pairs: Pair<String, Number>) = buildJsonObject { pairs.forEach { (k, v) -> put(k, JsonPrimitive(v)) } }
        val rows = StreamTotals.rows(mapOf(
            "cheer-session" to o("count" to 2, "amount" to 600),
            "cheer-latest" to buildJsonObject { put("name", "ann"); put("amount", 500) },
            "subscriber-session" to o("count" to 3, "amount" to 0),
            "subscriber-new-session" to o("count" to 1),
            "follower-session" to o("count" to 7),
            "raid-session" to o("count" to 2, "amount" to 2),
            "raid-latest" to buildJsonObject { put("name", "bob"); put("amount", 75) },
            "charityCampaignDonation-session" to o("count" to 1, "amount" to 25),
        ))
        assertEquals(listOf("Follows", "Subs", "Bits", "Raids", "Charity campaign donation"), rows.map { it.label })
        assertEquals(listOf("7", "3", "600", "2", "25"), rows.map { it.value })
        assertEquals("2 cheers", rows[2].detail)
        assertEquals("last: 75 viewers", rows[3].detail)
    }

    @Test fun colorsParseCssForms() {
        assertEquals(HexColor(1.0, 1.0, 1.0), HexColor.parse("#fff"))
        assertEquals(0x1c / 255.0, HexColor.parse("#16161c")!!.blue, 1e-9)
        assertEquals(0x80 / 255.0, HexColor.parse("#ff000080")!!.alpha, 1e-9)
        assertEquals(HexColor(1.0, 0.0, 0.0), HexColor.parse("rgb(255, 0, 0)"))
        assertTrue(HexColor.parse("red") == null && HexColor.parse("#12") == null && HexColor.parse("") == null)
        assertTrue(HexColor.parse("#16161c")!!.isDark && !HexColor.parse("#ffffff")!!.isDark)
        assertEquals(0xFF0B1120.toInt(), HexColor.parse("#0B1120")!!.argb)
    }
}
