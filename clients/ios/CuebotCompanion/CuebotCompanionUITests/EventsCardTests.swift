import XCTest

/// The Chat page's events card, fed by the Dashboard's activity feed, against the fake Cuebot of ProfileSwitchTests.
final class EventsCardTests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    private let wait: TimeInterval = 30

    @MainActor
    func testChatShowsTheNewestEventsAndSettingsCanHideTheCard() throws {
        let server = try FakeProfilesCuebot(feed: #"[{"platform":"Twitch","kind":"Cheer","user":"ann","title":"ann cheered 500 bits","amount":"500 bits"}]"#)
        defer { server.stop() }
        let app = XCUIApplication()
        let link = "cuebot://pair?v=1&name=Fake&host=127.0.0.1&api=\(server.port)&http=\(server.port)&token=test-token"
        // Settings come from the command line too (the argument domain), so earlier runs cannot hide the card.
        app.launchArguments = ["-resetPairing", "-pair", link, "-tab", "chat", "-eventsCard.visible", "YES"]
        app.launch()

        // A strip above chat on iPhone, a panel beside it on iPad: either way the event is there.
        let event = app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", "ann cheered 500 bits")).firstMatch
        XCTAssertTrue(event.waitForExistence(timeout: wait))
        XCTAssertTrue(app.descendants(matching: .any)["events-card"].exists)

        // The menu opens with the hamburger and closes once a section is chosen.
        SideMenuHelper.section(app, "Settings", wait: wait).tap()
        XCTAssertTrue(SideMenuHelper.waitUntilGone(app.buttons.matching(identifier: "Chat").firstMatch, timeout: wait))
        let visible = app.switches["events-visible"]
        for _ in 0..<6 where !visible.exists { app.swipeUp() }
        XCTAssertTrue(visible.waitForExistence(timeout: wait))
        let pinned = app.switches["menu-pinned"]
        for _ in 0..<6 where !pinned.exists { app.swipeUp() }
        XCTAssertTrue(pinned.waitForExistence(timeout: wait))
    }
}
