import AuthenticationServices
import CuebotKit
import Foundation
import Observation
import SwiftUI   // WebAuthenticationSession

/// Mod mode (docs/MOD_PORTAL_PLAN.md, section 6): join a streamer's Cuebot by invite, sign in with a provider, then use
/// the portal UI that Cuebot sends, all over the encrypted link through the relay. Mirrors the Android app's
/// ModViewModel and the web loader (portal/web/main.ts).
@MainActor @Observable
final class ModStore {
    /// Signed in: the portal UI the streamer's Cuebot sent (`js` nil when that Cuebot is too old to send one). When the
    /// link drops, the app reconnects by itself and swaps in the new `link`; the portal stays on screen meanwhile, with
    /// `reconnecting` set.
    @MainActor @Observable
    final class OpenPortal: Identifiable {
        let membership: Membership, me: PortalAnswer, js: String?, css: String?
        fileprivate(set) var link: PortalLink
        fileprivate(set) var reconnecting = false

        init(membership: Membership, link: PortalLink, me: PortalAnswer, js: String?, css: String?) {
            self.membership = membership
            self.link = link
            self.me = me
            self.js = js
            self.css = css
        }

        /// One request on the current link; while reconnecting, it waits for the new link first (up to 30 seconds).
        func request(_ op: String, _ body: PortalAnswer = [:]) async -> PortalAnswer {
            var waited = 0
            while reconnecting && waited < 300 {
                try? await Task.sleep(for: .milliseconds(100))
                waited += 1
            }
            return await link.request(op, body)
        }
    }

    enum State {
        /// The streamers this phone mods for, and joining with an invite.
        case home
        case busy(title: String, text: String)
        case providers(PortalTarget, [PortalProvider])
        /// The provider's page is open; it comes back through the portal's return page.
        case inBrowser(provider: String)
        case problem(String, retry: (() -> Void)?)
        case portal(OpenPortal)
    }

    /// How this phone appears in the streamer's list of the member's devices.
    static let deviceName = "Cuebot app on iOS"

    let memberships: ModMemberships
    private let open: (PortalTarget) async throws(LinkClosed) -> PortalLink
    private(set) var state: State = .home
    /// A note on the home screen ("Signed out"), or nil.
    var notice: String?
    private var browserTask: Task<Void, Never>?

    /// The waits between tries to get a dropped portal link back, before the mod is told it is lost.
    private let reconnectDelays: [Duration]

    init(memberships: ModMemberships,
         open: @escaping (PortalTarget) async throws(LinkClosed) -> PortalLink = { target throws(LinkClosed) in try await PortalLink.open(target) },
         reconnectDelays: [Duration] = [.milliseconds(500), .seconds(1), .seconds(2), .seconds(4), .seconds(8), .seconds(15), .seconds(15)]) {
        self.memberships = memberships
        self.open = open
        self.reconnectDelays = reconnectDelays
    }

    /// Opens an invite (or portal) link; the reason when it is not one.
    func openInvite(_ link: String) -> String? {
        guard let target = PortalTarget.parse(link) else {
            return "That is not a mod portal invite. Ask the streamer for the invite link or its QR code."
        }
        if target.invite == nil {
            if let m = memberships.find(target.relay) { resume(m); return nil }
            return "That link has no invite in it. Ask the streamer for a new invite."
        }
        chooseProvider(target)
        return nil
    }

    /// The provider buttons. With an invite the mod joins; without one a member signs in again.
    func chooseProvider(_ target: PortalTarget) {
        state = .busy(title: target.invite != nil ? "Join as a mod" : "Sign in again", text: "Connecting to the streamer's Cuebot…")
        let retry: () -> Void = { [weak self] in self?.chooseProvider(target) }
        Task {
            guard let link = await connect(target, retry: retry) else { return }
            let answer = await link.request("providers")
            link.close()
            let providers = PortalProvider.list(answer)
            state = !answer.ok || providers.isEmpty ? .problem(answer.error ?? "This Cuebot has no sign-in options set up.", retry: nil)
                : .providers(target, providers)
        }
    }

    /// Opens the provider's page in a browser sheet (providers refuse sign-in inside an app's web view). The return page
    /// hands the answer back as cuebot-portal://signin, which the sheet catches (or the app, through onOpenURL).
    func signIn(_ target: PortalTarget, _ provider: PortalProvider, session: WebAuthenticationSession) {
        let pending = PortalSignIn.start(target, provider)
        let url: URL
        do { url = try PortalSignIn.authorizeURL(provider, pending) }
        catch { state = .problem(error.message, retry: nil); return }
        memberships.pending = pending
        state = .inBrowser(provider: provider.name)
        browserTask?.cancel()
        let again: () -> Void = { [weak self] in self?.chooseProvider(target) }
        browserTask = Task {
            do {
                let answer = try await session.authenticate(using: url, callbackURLScheme: PortalSignIn.returnScheme,
                                                            preferredBrowserSession: .ephemeral)
                finishSignIn(answer)
            } catch {
                // Cancelled by the mod, or because the answer came in through onOpenURL instead.
                guard !Task.isCancelled, case .inBrowser = state else { return }
                memberships.pending = nil
                state = .problem("The sign-in was cancelled.", retry: again)
            }
        }
    }

    /// Back from the provider: cuebot-portal://signin?code=…&state=…
    func finishSignIn(_ answer: URL) {
        browserTask?.cancel()
        browserTask = nil
        let pending = memberships.pending
        memberships.pending = nil
        let again: (() -> Void)? = pending.map { p in { [weak self] in self?.chooseProvider(p.target) } }
        let code: String
        switch PortalSignIn.readReturn(answer, pending: pending) {
        case .failed(let message): state = .problem(message, retry: again); return
        case .code(let c): code = c
        }
        let pending1 = pending!
        let target = pending1.target
        state = .busy(title: "Signing in", text: "Checking your account with the streamer's Cuebot…")
        Task {
            guard let link = await connect(target, retry: again) else { return }
            // A new membership (an invite) gets its own key; signing in again keeps this device's key.
            var alias: String, key: DeviceKey, reused = false
            if target.invite == nil, let m = memberships.find(target.relay), m.target.fingerprint == target.fingerprint, let k = DeviceKeys.open(m.keyAlias) {
                (alias, key, reused) = (m.keyAlias, k, true)
            } else {
                alias = "portal-" + UUID().uuidString
                do { key = try DeviceKeys.create(alias) }
                catch { link.close(); state = .problem("This phone could not make a key for the mod portal.", retry: again); return }
            }
            var body: PortalAnswer = ["provider": .string(pending1.provider), "code": .string(code), "verifier": .string(pending1.verifier)]
            if let invite = target.invite { body["invite"] = .string(invite) }
            do { body["device"] = try link.deviceProof(key, name: Self.deviceName) }
            catch { link.close(); state = .problem("This phone could not use its mod portal key.", retry: again); return }
            let answer = await link.request("signin", body)
            guard answer.ok else {
                link.close()
                if !reused { DeviceKeys.delete(alias) }
                state = .problem(answer.error ?? "The sign-in failed.", retry: again)
                return
            }
            await welcome(link, target, alias, answer)
        }
    }

    /// Opens the portal for a streamer this phone already mods for.
    func resume(_ membership: Membership) {
        state = .busy(title: membership.name, text: "Connecting to the streamer's Cuebot…")
        let retry: () -> Void = { [weak self] in self?.resume(membership) }
        Task {
            guard let key = DeviceKeys.open(membership.keyAlias) else { return chooseProvider(membership.target) }
            guard let link = await connect(membership.target, retry: retry) else { return }
            let device: JSONValue
            do { device = try link.deviceProof(key) }
            catch { link.close(); return chooseProvider(membership.target) }
            let answer = await link.request("resume", ["device": device])
            if case .bool(true)? = answer["signIn"] {
                link.close()
                chooseProvider(membership.target)
            } else if !answer.ok {
                link.close()
                state = .problem(answer.error ?? "Could not sign in.", retry: retry)
            } else {
                await welcome(link, membership.target, membership.keyAlias, answer)
            }
        }
    }

    private func welcome(_ link: PortalLink, _ target: PortalTarget, _ alias: String, _ me: PortalAnswer) async {
        var saved = target
        saved.invite = nil
        // The name the mod gave this portal stays; Cuebot's "name" is the mod's own.
        let name = memberships.find(target.relay)?.name ?? Membership.defaultName
        let membership = Membership(target: saved, name: name, member: me.text("name") ?? "", role: me.text("role") ?? "", keyAlias: alias, at: .now)
        memberships.save(membership)
        let ui = await link.request("ui")
        let portal = OpenPortal(membership: membership, link: link, me: me, js: ui.ok ? ui.text("js") : nil, css: ui.ok ? ui.text("css") : nil)
        watch(link, of: portal, alias: alias)
        state = .portal(portal)
    }

    private func isOpen(_ portal: OpenPortal) -> Bool {
        if case .portal(let open) = state { return open === portal }
        return false
    }

    private func watch(_ link: PortalLink, of portal: OpenPortal, alias: String) {
        link.onClosed { [weak self] reason in
            Task { @MainActor in await self?.reconnect(portal, alias: alias, reason: reason) }
        }
    }

    /// The link dropped (a network change, the phone waking up, a relay restart): reconnect and resume with this
    /// device's key, keeping the portal and its page on screen. Requests made meanwhile wait for the new link. Only when
    /// every try fails, or Cuebot wants a new sign-in, does the mod see a problem.
    private func reconnect(_ portal: OpenPortal, alias: String, reason: String) async {
        guard isOpen(portal), !portal.reconnecting else { return }
        portal.reconnecting = true
        let membership = portal.membership
        let retry: () -> Void = { [weak self] in self?.resume(membership) }
        for wait in reconnectDelays {
            try? await Task.sleep(for: wait)
            guard isOpen(portal) else { return }
            guard let key = DeviceKeys.open(alias) else { break }
            guard let link = try? await open(membership.target) else { continue }
            guard let device = try? link.deviceProof(key) else { link.close(); break }
            let answer = await link.request("resume", ["device": device])
            guard isOpen(portal) else { link.close(); return }
            if answer.ok {
                portal.link = link
                watch(link, of: portal, alias: alias)
                portal.reconnecting = false
                return
            }
            link.close()
            // An answer without "req" is the link's own (it dropped again): try again. Cuebot's answers carry "req".
            if answer["req"] == nil { continue }
            if case .bool(true)? = answer["signIn"] { return chooseProvider(membership.target) }
            state = .problem(answer.error ?? reason, retry: retry)
            return
        }
        if isOpen(portal) { state = .problem(reason, retry: retry) }
    }

    /// Signs this phone out of the open portal and forgets the membership and its key.
    func signOut() {
        guard case .portal(let portal) = state else { return }
        Task {
            _ = await portal.link.request("signout")
            portal.link.close()
            memberships.forget(portal.membership.target.relay)
            notice = "Signed out of \(portal.membership.name) on this phone."
            state = .home
        }
    }

    /// Leaves the portal (the phone stays signed in) or a screen in the join flow.
    func home() {
        switch state {
        case .portal(let portal): portal.link.close()
        case .inBrowser:
            browserTask?.cancel()
            memberships.pending = nil
        default: break
        }
        state = .home
    }

    func rename(_ membership: Membership, to name: String) { memberships.rename(membership.target.relay, to: name) }

    /// Forgets a membership without asking Cuebot (for one this phone can no longer reach).
    func forget(_ membership: Membership) { memberships.forget(membership.target.relay) }

    private func connect(_ target: PortalTarget, retry: (() -> Void)?) async -> PortalLink? {
        do { return try await open(target) }
        catch { state = .problem(error.message, retry: retry); return nil }
    }
}
