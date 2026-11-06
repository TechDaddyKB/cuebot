import XCTest

/// Smoke tests: the app starts on iPhone and iPad, shows its tabs, and pairing by hand is possible.
final class LaunchTests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    /// A tab: in the tab bar on iPhone, in the floating tab bar (plain buttons) on iPad.
    @MainActor
    private func tab(_ app: XCUIApplication, _ name: String) -> XCUIElement {
        let inTabBar = app.tabBars.buttons[name]
        return inTabBar.exists ? inTabBar : app.buttons.matching(identifier: name).firstMatch
    }

    /// CI simulators are slow: generous waits everywhere.
    private let wait: TimeInterval = 30

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
        tab(app, "Settings").tap()
        XCTAssertTrue(app.buttons["pair-button"].waitForExistence(timeout: wait))
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
        XCTAssertTrue(dark.isSelected)
        // The choice is kept: it survives a relaunch.
        app.terminate()
        app.launchArguments = ["-tab", "settings"]
        app.launch()
        XCTAssertTrue(app.buttons["Dark"].waitForExistence(timeout: wait))
        XCTAssertTrue(app.buttons["Dark"].isSelected)
        app.buttons["System"].tap()
        XCTAssertTrue(app.buttons["System"].isSelected)
        XCTAssertFalse(app.buttons["Dark"].isSelected)
    }
}
