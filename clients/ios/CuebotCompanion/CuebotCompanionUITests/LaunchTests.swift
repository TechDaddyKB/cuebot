import XCTest

/// Smoke tests: the app starts on iPhone and iPad, shows its tabs, and pairing by hand is possible.
final class LaunchTests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    /// CI simulators are slow: generous waits everywhere.
    private let wait: TimeInterval = 30

    /// A section in the side menu, opened with the hamburger first unless the menu is already open (or pinned).
    @MainActor
    private func tab(_ app: XCUIApplication, _ name: String) -> XCUIElement {
        SideMenuHelper.section(app, name, wait: wait)
    }

    /// A segment's selected state follows a tap a moment later on slow simulators: wait for it rather than checking once.
    @MainActor
    private func waitUntil(_ element: XCUIElement, isSelected selected: Bool, file: StaticString = #filePath, line: UInt = #line) {
        let done = XCTNSPredicateExpectation(predicate: NSPredicate(format: "isSelected == %@", NSNumber(value: selected)), object: element)
        XCTAssertEqual(XCTWaiter.wait(for: [done], timeout: wait), .completed,
                       "\(element) isSelected never became \(selected)", file: file, line: line)
    }

    @MainActor
    private func launchUnpaired(tab: String? = nil) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["-resetPairing"] + (tab.map { ["-tab", $0] } ?? [])
        app.launch()
        return app
    }

    @MainActor
    func testStartsUnpairedWithTheFourTabs() {
        let app = launchUnpaired()
        XCTAssertTrue(app.staticTexts["Pair with Cuebot"].waitForExistence(timeout: wait))
        XCTAssertTrue(tab(app, "Deck").exists)
        XCTAssertTrue(tab(app, "Dashboard").exists)
        // Mods works without a paired Cuebot: a mod joins other streamers' portals by invite.
        tab(app, "Mods").tap()
        XCTAssertTrue(app.buttons["join-invite"].waitForExistence(timeout: wait))
        // Choosing a section closes the menu, so the page gets the whole screen.
        XCTAssertTrue(SideMenuHelper.waitUntilGone(app.buttons.matching(identifier: "Dashboard").firstMatch, timeout: wait))
        tab(app, "Settings").tap()
        XCTAssertTrue(app.buttons["pair-button"].waitForExistence(timeout: wait))
    }

    @MainActor
    func testFeedbackNeedsTextAndShowsWhatIsIncluded() {
        let app = launchUnpaired(tab: "settings")
        XCTAssertTrue(app.buttons["pair-button"].waitForExistence(timeout: wait))
        let row = app.buttons["settings-feedback"]
        for _ in 0..<8 where !row.isHittable { app.swipeUp() }
        row.tap()
        let send = app.buttons["feedback-send"]
        XCTAssertTrue(send.waitForExistence(timeout: wait))
        XCTAssertFalse(send.isEnabled)
        let summary = app.descendants(matching: .any)["feedback-title"].firstMatch
        summary.tap()
        summary.typeText("Deck is blank")
        let details = app.descendants(matching: .any)["feedback-details"].firstMatch
        details.tap()
        details.typeText("It went blank")
        XCTAssertTrue(send.isEnabled)
        app.buttons["Cancel"].tap()
        XCTAssertTrue(app.buttons["settings-feedback"].waitForExistence(timeout: wait))
    }

    @MainActor
    func testPairingByHandAddsTheCuebot() {
        let app = launchUnpaired(tab: "settings")
        XCTAssertTrue(app.buttons["pair-button"].waitForExistence(timeout: wait))
        app.buttons["pair-button"].tap()
        if app.buttons["Enter the details instead"].waitForExistence(timeout: 3) { app.buttons["Enter the details instead"].tap() }
        let host = app.textFields["pair-host"]
        XCTAssertTrue(host.waitForExistence(timeout: wait))
        host.tap()
        host.typeText("127.0.0.1")
        let token = app.secureTextFields["pair-token"]
        token.tap()
        token.typeText("test-token")
        app.buttons["pair-confirm"].tap()
        let row = app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", "127.0.0.1:7475")).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: wait))
    }

    @MainActor
    func testAppearanceSwitchesToDarkAndBack() {
        let app = launchUnpaired(tab: "settings")
        let dark = app.buttons["Dark"]
        XCTAssertTrue(dark.waitForExistence(timeout: wait))
        dark.tap()
        waitUntil(dark, isSelected: true)
        // The choice is kept: it survives a relaunch.
        app.terminate()
        app.launchArguments = ["-tab", "settings"]
        app.launch()
        XCTAssertTrue(app.buttons["Dark"].waitForExistence(timeout: wait))
        waitUntil(app.buttons["Dark"], isSelected: true)
        app.buttons["System"].tap()
        waitUntil(app.buttons["System"], isSelected: true)
        waitUntil(app.buttons["Dark"], isSelected: false)
    }
}

/// The side menu that replaced the tab bar: sections are buttons named after them ("Deck", "Dashboard"...).
enum SideMenuHelper {
    @MainActor
    static func section(_ app: XCUIApplication, _ name: String, wait: TimeInterval) -> XCUIElement {
        let item = app.buttons.matching(identifier: name).firstMatch
        if !item.exists {
            let menu = app.buttons["menu-button"].firstMatch
            if menu.waitForExistence(timeout: wait) { menu.tap() }
            _ = item.waitForExistence(timeout: wait)
        }
        return item
    }

    @MainActor
    static func waitUntilGone(_ element: XCUIElement, timeout: TimeInterval) -> Bool {
        let gone = XCTNSPredicateExpectation(predicate: NSPredicate(format: "exists == false"), object: element)
        return XCTWaiter.wait(for: [gone], timeout: timeout) == .completed
    }
}
