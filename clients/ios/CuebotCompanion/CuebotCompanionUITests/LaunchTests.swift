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

    @MainActor
    private func launchUnpaired() -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["-resetPairing"]
        app.launch()
        return app
    }

    @MainActor
    func testStartsUnpairedWithTheThreeTabs() {
        let app = launchUnpaired()
        XCTAssertTrue(app.staticTexts["Pair with Cuebot"].waitForExistence(timeout: 10))
        XCTAssertTrue(tab(app, "Deck").exists)
        XCTAssertTrue(tab(app, "Dashboard").exists)
        tab(app, "Settings").tap()
        XCTAssertTrue(app.buttons["pair-button"].waitForExistence(timeout: 5))
    }

    @MainActor
    func testPairingByHandAddsTheCuebot() {
        let app = launchUnpaired()
        XCTAssertTrue(app.staticTexts["Pair with Cuebot"].waitForExistence(timeout: 10))
        tab(app, "Settings").tap()
        app.buttons["pair-button"].tap()
        if app.buttons["Enter the details instead"].waitForExistence(timeout: 2) { app.buttons["Enter the details instead"].tap() }
        let host = app.textFields["pair-host"]
        XCTAssertTrue(host.waitForExistence(timeout: 5))
        host.tap()
        host.typeText("127.0.0.1")
        let token = app.secureTextFields["pair-token"]
        token.tap()
        token.typeText("test-token")
        app.buttons["pair-confirm"].tap()
        let row = app.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", "127.0.0.1:7475")).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 5))
    }
}
