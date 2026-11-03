import Foundation
import Testing
@testable import CuebotKit

struct PairingLinkTests {
    @Test func parsesEveryField() throws {
        let endpoint = try PairingLink.parse("cuebot://pair?v=1&name=Studio%20PC&host=192.168.1.20&api=7575&http=7574&token=abc&deckToken=dk&id=inst-1")
        #expect(endpoint == CuebotEndpoint(name: "Studio PC", host: "192.168.1.20", apiPort: 7575, httpPort: 7574,
                                           token: "abc", deckToken: "dk", instanceId: "inst-1"))
        #expect(endpoint.apiBase.absoluteString == "http://192.168.1.20:7575")
        #expect(endpoint.httpBase.absoluteString == "http://192.168.1.20:7574")
    }

    @Test func fillsDefaultsAndRejectsBadLinks() throws {
        let endpoint = try PairingLink.parse("  CUEBOT://pair?host=pc.local&token=t  ")
        #expect(endpoint.apiPort == 7475 && endpoint.httpPort == 7474 && endpoint.name == "Cuebot" && endpoint.deckToken == nil)
        #expect(throws: PairingLink.Failure.notAPairingLink) { try PairingLink.parse("https://example.com/pair?host=x&token=y") }
        #expect(throws: PairingLink.Failure.missing("token")) { try PairingLink.parse("cuebot://pair?host=x") }
        #expect(throws: PairingLink.Failure.missing("host")) { try PairingLink.parse("cuebot://pair?token=x") }
        #expect(throws: PairingLink.Failure.unsupportedVersion("2")) { try PairingLink.parse("cuebot://pair?v=2&host=x&token=y") }
        #expect(throws: PairingLink.Failure.badPort("api")) { try PairingLink.parse("cuebot://pair?host=x&token=y&api=99999") }
    }

    @Test func bracketsIPv6Hosts() {
        #expect(CuebotEndpoint(host: "fe80::1", token: "t").apiBase.absoluteString == "http://[fe80::1]:7475")
    }
}

struct ModelTests {
    private func decode<T: Decodable>(_ type: T.Type, _ json: String) throws -> T {
        try JSONDecoder().decode(type, from: Data(json.utf8))
    }

    @Test func statusDecodesAndToleratesUnknownValues() throws {
        let status = try decode(StatusResponse.self, """
        { "app": { "name": "Cuebot", "version": "0.1.0", "instanceId": "abc", "apiVersion": "v1", "extra": 1 },
          "connections": [ { "name": "Twitch", "state": "Connected", "status": "ok" }, { "name": "OBS", "state": "Sleeping" },
                           { "name": "Kick", "state": 3 } ],
          "counts": { "actions": 4 } }
        """)
        #expect(status.app.instanceId == "abc")
        #expect(status.connections.map(\.state) == [.connected, .unknown, .unknown])
        #expect(status.connections[1].status == "")
    }

    @Test func deckViewDecodesThePlanSample() throws {
        let view = try decode(DeckView.self, """
        { "id": "d1", "name": "Main deck", "rows": 3, "columns": 5, "background": "#16161c", "gap": 8,
          "page": 0, "pageName": "Main", "pageCount": 2,
          "items": [ { "id": "i1", "name": "Mic", "row": 0, "column": 1, "state": 0, "title": "Mute",
                       "titleColor": "#fff", "background": "#333", "backgroundImage": "", "icon": "mdi:microphone",
                       "iconColor": "#fff", "iconUrl": "", "value": "on" } ] }
        """)
        #expect(view.rows == 3 && view.columns == 5 && view.pageCount == 2)
        #expect(view.item(row: 0, column: 1)?.icon == "mdi:microphone")
        #expect(view.item(row: 2, column: 2) == nil)
        #expect(view.items[0].accessibilityName == "Mute")
        let empty = try decode(DeckView.self, #"{ "rows": 0, "gap": -3 }"#)
        #expect(empty.rows == 1 && empty.columns == 5 && empty.gap == 0 && empty.items.isEmpty)
    }

    @Test func dashboardDecodesSubathonGoalsSpendersAndFeed() throws {
        let dashboard = try decode(Dashboard.self, """
        { "instanceId": "abc", "version": "0.2.0", "revision": 42,
          "connections": [ { "name": "Twitch", "state": "Connecting" } ],
          "totals": { "followers": 3, "bits": 500, "tips": "$12.00" },
          "subathon": {
            "timers": [ { "id": "t1", "name": "Main", "state": "running", "remaining": 3540.5, "endsAt": 1762130000000,
                          "elapsed": 60, "added": 3590.0, "multiplier": 2.0 },
                        { "id": "t2", "name": "Side", "state": "paused", "remaining": 75.9, "endsAt": null },
                        { "id": "t3", "name": "Done", "state": "ended", "remaining": 0 } ],
            "goals": [ { "id": "g1", "name": "Points", "points": 12, "multiplier": 1.0,
                         "steps": [ { "name": "Emote", "points": 10, "reached": true }, { "name": "Second", "points": 20 } ],
                         "next": { "name": "Second", "points": 20 }, "previous": { } } ] },
          "topSpenders": { "currency": "USD", "periods": {
            "week": { "all": { "total": 1, "formattedTotal": "$1.00", "spenders": [] } },
            "stream": { "all": { "total": 22.34, "formattedTotal": "$22.34", "spenders": [
                { "rank": 1, "name": "ann", "platform": "Twitch", "amount": 22.34, "formatted": "$22.34", "count": 2 } ] },
                        "twitch": { "total": 22.34, "spenders": [] } } } },
          "feed": [ { "platform": "Twitch", "kind": "Cheer", "user": "ann", "detail": "Cheer100 hype", "amount": "500 bits", "isTest": true } ] }
        """)
        #expect(dashboard.revision == 42)
        #expect(dashboard.totals["bits"]?.double == 500 && dashboard.totals["tips"]?.string == "$12.00")
        let main = dashboard.subathon.timers[0]
        #expect(main.state == .running && main.multiplier == 2)
        #expect(main.remaining(at: Date(timeIntervalSince1970: 1_762_130_000 - 90)) == 90)
        #expect(main.remaining(at: Date(timeIntervalSince1970: 1_762_130_100)) == 0)
        #expect(dashboard.subathon.timers[1].remaining(at: .now) == 75.9)
        #expect(dashboard.subathon.timers[2].state == .ended)
        let goal = dashboard.subathon.goals[0]
        #expect(goal.next?.name == "Second" && goal.previous == nil)
        #expect(abs(goal.progressToNext - 0.2) < 1e-9)
        #expect(dashboard.topSpenders.periodKeys == ["stream", "week"])
        #expect(dashboard.topSpenders.platformKeys(period: "stream") == ["all", "twitch"])
        #expect(dashboard.topSpenders.board(period: "stream").spenders.first?.formatted == "$22.34")
        #expect(dashboard.topSpenders.board(period: "day").spenders.isEmpty)
        #expect(dashboard.feed.first?.isTest == true && dashboard.feed.first?.detail == "Cheer100 hype")
    }

    @Test func aBrokenPartFallsBackInsteadOfFailingTheSnapshot() throws {
        let dashboard = try decode(Dashboard.self, #"{ "revision": 7, "subathon": "nope", "feed": { }, "topSpenders": null }"#)
        #expect(dashboard.revision == 7 && dashboard.subathon.timers.isEmpty && dashboard.feed.isEmpty)
        #expect(dashboard.topSpenders.periodKeys.isEmpty)
    }

    @Test func apiEventsUnwrapTypeTimeAndArgs() {
        let event = ServerEvent(type: "dashboard.changed", data: #"{"type":"dashboard.changed","time":"2026-11-03T01:00:00Z","args":{"revision":42}}"#, id: "5")
        #expect(event.apiEvent.type == "dashboard.changed" && event.apiEvent.args["revision"]?.double == 42)
        #expect(ServerEvent(type: "x.y", data: "not json").apiEvent.type == "x.y")
    }

    @Test func streamTotalsComeFromTheSessionEntries() {
        let totals: [String: JSONValue] = [
            "cheer-session": ["count": .number(2), "amount": .number(600)],
            "cheer-latest": ["name": .string("ann"), "amount": .number(500)],
            "subscriber-session": ["count": .number(3), "amount": .number(0)],
            "subscriber-new-session": ["count": .number(1)],
            "follower-session": ["count": .number(7)],
            "raid-session": ["count": .number(2), "amount": .number(2)],
            "raid-latest": ["name": .string("bob"), "amount": .number(75)],
            "charityCampaignDonation-session": ["count": .number(1), "amount": .number(25)],
        ]
        let rows = StreamTotals.rows(totals)
        #expect(rows.map(\.label) == ["Follows", "Subs", "Bits", "Raids", "Charity campaign donation"])
        #expect(rows.map(\.value) == ["7", "3", "600", "2", "25"])
        #expect(rows[3].detail == "last: 75 viewers")
        #expect(rows[2].detail == "2 cheers")
        #expect(StreamTotals.rows([:]).isEmpty)
    }

    @Test func timersFormatLikeCuebot() {
        #expect(SubathonTimer.format(3540.9) == "0:59:00")
        #expect(SubathonTimer.format(90_061) == "1:01:01:01")
        #expect(SubathonTimer.format(-5) == "0:00:00")
    }

    @Test func goalProgressCoversTheEnds() {
        let steps = [GoalStep(name: "a", points: 10), GoalStep(name: "b", points: 30)]
        #expect(PointsGoal(id: "g", name: "g", points: 0, steps: steps).progressToNext == 0)
        #expect(PointsGoal(id: "g", name: "g", points: 20, steps: steps).progressToNext == 0.5)
        #expect(PointsGoal(id: "g", name: "g", points: 40, steps: steps).progressToNext == 1)
        #expect(PointsGoal(id: "g", name: "g", points: 5, steps: []).progressToNext == 0)
    }

    @Test func colorsParseCssForms() {
        #expect(HexColor("#fff") == HexColor(red: 1, green: 1, blue: 1))
        #expect(HexColor("#16161c")?.blue == Double(0x1c) / 255)
        #expect(HexColor("#ff000080")?.alpha == Double(0x80) / 255)
        #expect(HexColor("rgb(255, 0, 0)") == HexColor(red: 1, green: 0, blue: 0))
        #expect(HexColor("red") == nil && HexColor("#12") == nil && HexColor("") == nil)
        #expect(HexColor("#16161c")!.isDark && !HexColor("#ffffff")!.isDark && !HexColor("#ffd43b")!.isDark)
    }
}
