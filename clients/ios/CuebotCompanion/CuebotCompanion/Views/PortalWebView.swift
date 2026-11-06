import CuebotKit
import SwiftUI
import UniformTypeIdentifiers
import WebKit

/// The portal UI the streamer's Cuebot sent, in a web view that loads only the app's own page (PortalPage, served as
/// cuebot-app://portal/) and reaches nothing on the network: a content rule blocks every http, https and WebSocket load,
/// and the UI's requests come to the app as messages and go over the encrypted link (`ModStore.OpenPortal.link`). The
/// UI itself runs from blob: URLs, as in the web loader.
struct PortalWebView: UIViewRepresentable {
    let portal: ModStore.OpenPortal
    let onSignOut: () -> Void
    let onFailed: (String) -> Void

    static let scheme = "cuebot-app"
    static let host = "portal"
    /// The files the page may load, and where they are in the app bundle.
    private static let files = ["index.html": ("portal-index", "html", "text/html"),
                                "host.js": ("portal-host", "js", "text/javascript"),
                                "host.css": ("portal-host", "css", "text/css")]

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.setURLSchemeHandler(context.coordinator, forURLScheme: Self.scheme)
        configuration.userContentController.add(context.coordinator, name: "cuebotApp")
        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = context.coordinator
        webView.isOpaque = false
        webView.backgroundColor = .clear
        #if DEBUG
        webView.isInspectable = true
        #endif
        context.coordinator.webView = webView
        // No network for the page, whatever the UI tries: compile the rule, then load.
        WKContentRuleListStore.default().compileContentRuleList(forIdentifier: "cuebot-portal-no-network", encodedContentRuleList: """
            [{"trigger":{"url-filter":"^(https?|wss?|ftp)://"},"action":{"type":"block"}}]
            """) { list, _ in
            if let list { configuration.userContentController.add(list) }
            webView.load(URLRequest(url: URL(string: "\(Self.scheme)://\(Self.host)/index.html")!))
        }
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {}

    static func dismantleUIView(_ webView: WKWebView, coordinator: Coordinator) {
        webView.configuration.userContentController.removeScriptMessageHandler(forName: "cuebotApp")
        webView.stopLoading()
    }

    @MainActor
    final class Coordinator: NSObject, WKURLSchemeHandler, WKScriptMessageHandler, WKNavigationDelegate {
        let parent: PortalWebView
        weak var webView: WKWebView?

        init(_ parent: PortalWebView) { self.parent = parent }

        // MARK: The bundled page

        func webView(_ webView: WKWebView, start task: any WKURLSchemeTask) {
            let url = task.request.url
            guard url?.host == PortalWebView.host, let name = url?.path.split(separator: "/").last.map(String.init),
                  let (resource, ext, type) = PortalWebView.files[name],
                  let file = Bundle.main.url(forResource: resource, withExtension: ext), let data = try? Data(contentsOf: file) else {
                task.didFailWithError(URLError(.fileDoesNotExist))
                return
            }
            task.didReceive(HTTPURLResponse(url: url!, statusCode: 200, httpVersion: "HTTP/1.1",
                                            headerFields: ["Content-Type": "\(type); charset=utf-8", "Cache-Control": "no-store"])!)
            task.didReceive(data)
            task.didFinish()
        }

        func webView(_ webView: WKWebView, stop task: any WKURLSchemeTask) {}

        // MARK: Navigation: the page and blob:/about: only; https links open in Safari, never in here

        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction) async -> WKNavigationActionPolicy {
            guard let url = action.request.url else { return .cancel }
            if url.scheme == PortalWebView.scheme && url.host == PortalWebView.host { return .allow }
            // The UI's own blob: objects and about:blank, never as the main page.
            if (url.scheme == "blob" || url.scheme == "about") && action.targetFrame?.isMainFrame != true { return .allow }
            if url.scheme == "https", action.navigationType == .linkActivated { await UIApplication.shared.open(url) }
            return .cancel
        }

        // MARK: Messages from the page

        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame, message.frameInfo.securityOrigin.protocol == PortalWebView.scheme,
                  message.frameInfo.securityOrigin.host == PortalWebView.host,
                  let text = message.body as? String, let data = text.data(using: .utf8),
                  let m = try? JSONDecoder().decode(PortalAnswer.self, from: data) else { return }
            switch m.text("type") {
            case "ready":
                var mount: PortalAnswer = ["type": .string("mount"), "me": .object(parent.portal.me)]
                mount["js"] = parent.portal.js.map(JSONValue.string) ?? .null
                mount["css"] = parent.portal.css.map(JSONValue.string) ?? .null
                post(mount)
            case "request":
                guard let id = m["id"], let op = m.text("op") else { return }
                var body: PortalAnswer = [:]
                if case .object(let b)? = m["body"] { body = b }
                let link = parent.portal.link
                Task {
                    let answer = await link.request(op, body)
                    post(["type": .string("answer"), "id": id, "answer": .object(answer)])
                }
            case "signOut": parent.onSignOut()
            case "failed": parent.onFailed(m.text("error") ?? "The mod portal did not start.")
            default: break
            }
        }

        /// Hands a message to the page as an argument, never as script text, so no answer can inject script.
        private func post(_ message: PortalAnswer) {
            guard let data = try? JSONEncoder().encode(message), let text = String(data: data, encoding: .utf8) else { return }
            webView?.callAsyncJavaScript("window.cuebotAppReceive(data)", arguments: ["data": text], in: nil, in: .page) { _ in }
        }
    }
}
