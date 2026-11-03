import Foundation
import Testing
@testable import CuebotKit

struct SVGIconTests {
    /// Real Iconify answers (api.iconify.design, November 2026).
    static let microphone = ##"<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24"><path fill="#ffffff" d="M12 2a3 3 0 0 1 3 3v6a3 3 0 0 1-3 3a3 3 0 0 1-3-3V5a3 3 0 0 1 3-3m7 9c0 3.53-2.61 6.44-6 6.93V21h-2v-3.07c-3.39-.49-6-3.4-6-6.93h2a5 5 0 0 0 5 5a5 5 0 0 0 5-5z"/></svg>"##
    static let heart = ##"<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24"><path fill="none" stroke="#ffffff" stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2 9.5a5.5 5.5 0 0 1 9.591-3.676a.56.56 0 0 0 .818 0A5.49 5.49 0 0 1 22 9.5c0 2.29-1.5 4-3 5.5l-5.492 5.313a2 2 0 0 1-3 .019L5 15c-1.5-1.5-3-3.2-3-5.5"/></svg>"##
    static let skull = ##"<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 512 512"><path fill="#ffffff" d="M416 398.9c58.5-41.1 96-104.1 96-174.9C512 100.3 397.4 0 256 0S0 100.3 0 224c0 70.7 37.5 133.8 96 174.9V464c0 26.5 21.5 48 48 48h48v-48c0-8.8 7.2-16 16-16s16 7.2 16 16v48h64v-48c0-8.8 7.2-16 16-16s16 7.2 16 16v48h48c26.5 0 48-21.5 48-48zM96 256a64 64 0 1 1 128 0a64 64 0 1 1-128 0m256-64a64 64 0 1 1 0 128a64 64 0 1 1 0-128"/></svg>"##

    /// Every point the shapes pass through or pull toward.
    private func points(_ icon: SVGIcon) -> [SVGIcon.Point] {
        icon.shapes.flatMap(\.segments).flatMap { segment -> [SVGIcon.Point] in
            switch segment {
            case .move(let p), .line(let p): [p]
            case .cubic(let a, let b, let c): [a, b, c]
            case .quad(let a, let b): [a, b]
            case .close: []
            }
        }
    }

    @Test func readsFilledIconsInsideTheirViewBox() throws {
        for (svg, size) in [(Self.microphone, 24.0), (Self.skull, 512.0)] {
            let icon = try #require(SVGIcon(svg: svg))
            #expect(icon.width == size && icon.height == size)
            #expect(icon.shapes.allSatisfy { $0.fill && $0.strokeWidth == nil })
            let all = points(icon)
            #expect(all.count > 10)
            // Arc control points may poke out a little; the icon stays within a 5% margin of its box.
            #expect(all.allSatisfy { $0.x >= -size * 0.05 && $0.x <= size * 1.05 && $0.y >= -size * 0.05 && $0.y <= size * 1.05 })
        }
    }

    @Test func readsStrokedIconsWithTheirLineStyle() throws {
        let icon = try #require(SVGIcon(svg: Self.heart))
        let shape = try #require(icon.shapes.first)
        #expect(!shape.fill && shape.strokeWidth == 2 && shape.roundCaps && shape.roundJoins)
        // "a.56.56 0 0 0 .818 0" packs four numbers into ".56.56" and ".818 0".
        if case .move(let start) = shape.segments.first { #expect(start == SVGIcon.Point(2, 9.5)) } else { Issue.record("no move") }
    }

    @Test func relativeCommandsAndShorthandsBecomeAbsolute() {
        let segments = PathData.parse("m1 1 2 0v2h-2zM10 10l1-1s2 0 2 2t1 1")
        #expect(segments.prefix(5) == [.move(.init(1, 1)), .line(.init(3, 1)), .line(.init(3, 3)), .line(.init(1, 3)), .close])
        #expect(segments[5] == .move(.init(10, 10)))
        #expect(segments[6] == .line(.init(11, 9)))
        // S without a previous curve uses the current point as its first control point.
        #expect(segments[7] == .cubic(.init(11, 9), .init(13, 9), .init(13, 11)))
        #expect(segments[8] == .quad(.init(13, 11), .init(14, 12)))
    }

    @Test func compactArcFlagsAndExponentsParse() {
        let segments = PathData.parse("M0 0a1 1 0 011 1L1e1 2.5e-1")
        #expect(segments.count >= 3)
        if case .cubic(_, _, let end) = segments[segments.count - 2] { #expect(abs(end.x - 1) < 1e-9 && abs(end.y - 1) < 1e-9) }
        #expect(segments.last == .line(.init(10, 0.25)))
    }

    @Test func aHalfCircleArcEndsWhereItShould() {
        // From (0,0) to (2,0) with radius 1, sweeping: a half circle through (1,-1) or (1,1).
        let segments = PathData.arc(from: .init(0, 0), to: .init(2, 0), rx: 1, ry: 1, rotation: 0, large: false, sweep: true)
        #expect(segments.count == 2)
        guard case .cubic(_, _, let middle) = segments[0], case .cubic(_, _, let end) = segments[1] else { return }
        #expect(abs(middle.x - 1) < 1e-6 && abs(abs(middle.y) - 1) < 1e-6)
        #expect(end == .init(2, 0))
    }

    @Test func shapesAndGroupsInheritTheirStyle() throws {
        let svg = ##"<svg viewBox="0 0 10 10"><g fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="5" cy="5" r="4"/><rect x="1" y="1" width="3" height="2" rx="0.5"/><polyline points="1,1 2,2 3,1"/></g><polygon points="0 0 1 0 1 1"/></svg>"##
        let icon = try #require(SVGIcon(svg: svg))
        #expect(icon.shapes.count == 4)
        #expect(icon.shapes.prefix(3).allSatisfy { !$0.fill && $0.strokeWidth == 1.5 })
        #expect(icon.shapes[3].fill && icon.shapes[3].segments.last == .close)
    }

    @Test func rejectsWhatItCannotDraw() {
        #expect(SVGIcon(svg: "not svg") == nil)
        #expect(SVGIcon(svg: #"<svg viewBox="0 0 24 24"></svg>"#) == nil)
        #expect(SVGIcon(svg: #"<svg viewBox="0 0 24 24"><defs><path d="M0 0h1v1z"/></defs></svg>"#) == nil)
    }

    @Test func iconifyNamesBecomeUrls() {
        #expect(Iconify.url(for: "mdi:microphone")?.absoluteString == "https://api.iconify.design/mdi/microphone.svg")
        #expect(Iconify.url(for: "fa6-solid:skull")?.absoluteString == "https://api.iconify.design/fa6-solid/skull.svg")
        #expect(Iconify.url(for: "microphone") == nil)
        #expect(Iconify.url(for: "mdi:../x") == nil)
    }
}
