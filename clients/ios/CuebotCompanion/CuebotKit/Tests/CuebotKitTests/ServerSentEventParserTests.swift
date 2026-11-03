import Testing
@testable import CuebotKit

struct ServerSentEventParserTests {
    private func parse(_ text: String, chunkSize: Int = .max) -> (events: [ServerEvent], parser: ServerSentEventParser) {
        var parser = ServerSentEventParser()
        var events: [ServerEvent] = []
        let bytes = Array(text.utf8)
        var index = 0
        while index < bytes.count {
            let end = min(bytes.count, index + chunkSize)
            events += parser.feed(bytes[index..<end])
            index = end
        }
        return (events, parser)
    }

    @Test func parsesCuebotsStream() {
        let (events, parser) = parse(": connected\n\nid: 7\nevent: twitch.cheer\ndata: {\"bits\":100}\n\n: keep-alive\n\n")
        #expect(events == [ServerEvent(type: "twitch.cheer", data: #"{"bits":100}"#, id: "7")])
        #expect(events[0].json["bits"]?.double == 100)
        #expect(parser.comments == ["connected", "keep-alive"])
        #expect(parser.lastEventId == "7")
    }

    @Test func joinsDataLinesAndDefaultsTheType() {
        #expect(parse("data: a\ndata:b\ndata\n\n").events == [ServerEvent(data: "a\nb\n")])
    }

    @Test func acceptsCrLfAndBareCrAcrossChunkBoundaries() {
        for size in [1, 2, 3, 7] {
            let (events, _) = parse("event: state\r\ndata: {}\r\n\r\nevent: notify\rdata: x\r\r", chunkSize: size)
            #expect(events.map(\.type) == ["state", "notify"], "chunk size \(size)")
        }
    }

    @Test func keepsTheLastIdAndRetryAndIgnoresUnknownFields() {
        let (events, parser) = parse("id: 1\nfoo: bar\nretry: 2500\ndata: one\n\ndata: two\n\nid: 9\n\n")
        #expect(events.map(\.id) == ["1", "1"])
        #expect(parser.retryMilliseconds == 2500)
        #expect(parser.lastEventId == "9")
    }

    @Test func dispatchesNothingWithoutDataOrBeforeTheBlankLine() {
        #expect(parse("event: x\n\nid: 3\n\n").events.isEmpty)
        #expect(parse("data: half").events.isEmpty)
    }

    @Test func backoffFollowsThePlan() {
        #expect((0..<7).map(Backoff.standard.delay) == [1, 2, 4, 8, 15, 15, 15])
    }
}
