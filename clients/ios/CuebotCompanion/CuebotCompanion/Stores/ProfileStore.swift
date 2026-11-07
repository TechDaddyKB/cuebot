import CuebotKit
import Foundation
import Observation

/// The streamer profiles of the active Cuebot (one persona's whole Cuebot each) and switching between them. A switch
/// restarts Cuebot on the other profile; the pairing keeps working because server settings are shared by every profile.
@MainActor @Observable
final class ProfileStore {
    /// Nil when this Cuebot has no profiles to offer (an older build, or the headless tools).
    private(set) var state: ProfilesState?
    /// The profile Cuebot is restarting on, until it answers on it.
    private(set) var switchingTo: StreamerProfile?
    private(set) var message: String?
    /// Bumped when a switch has finished, so everything that belongs to a profile (deck, dashboard) loads again.
    private(set) var switchCount = 0

    private var client: CuebotClient?
    private var waitTask: Task<Void, Never>?

    /// Shown only with two or more profiles: with one there is nothing to switch to.
    var isShown: Bool { (state?.profiles.count ?? 0) >= 2 }

    /// How long to wait between asking whether Cuebot is back, and how often before giving up.
    var pollInterval: Duration = .seconds(1)
    var pollAttempts = 30

    func load(client: CuebotClient?) async {
        if client?.endpoint != self.client?.endpoint {
            waitTask?.cancel()
            switchingTo = nil
            state = nil
        }
        self.client = client
        message = nil
        await refresh()
    }

    func refresh() async {
        guard let client else { return }
        do { state = try await client.profiles() }
        catch {}   // unreachable for a moment: keep what was shown
    }

    enum Outcome: Equatable {
        case started
        case alreadyOpen
        /// Live: ask "Switch anyway?" with this question, then call again with force.
        case askForce(String)
        case failed
    }

    func activate(_ profile: StreamerProfile, force: Bool = false) async -> Outcome {
        guard let client, switchingTo == nil else { return .failed }
        message = nil
        do {
            switch try await client.activateProfile(profile.id, force: force) {
            case .switching:
                switchingTo = profile
                waitForSwitch(to: profile, client: client)
                return .started
            case .alreadyOpen:
                await refresh()
                return .alreadyOpen
            case .live(let words):
                return .askForce(Self.liveQuestion(live: state?.live ?? [], fallback: words))
            case .busy(let words):
                message = words
                return .failed
            }
        } catch {
            message = ConnectionStore.describe(error)
            return .failed
        }
    }

    static func liveQuestion(live: [String], fallback: String) -> String {
        live.isEmpty ? "\(fallback). Switch anyway?" : "You are live on \(ListFormatter.localizedString(byJoining: live)). Switch anyway?"
    }

    /// Cuebot goes away for a few seconds; ask until it answers on the new profile, then reload everything.
    private func waitForSwitch(to profile: StreamerProfile, client: CuebotClient) {
        waitTask?.cancel()
        waitTask = Task { [weak self, pollInterval, pollAttempts] in
            var arrived = false
            for _ in 0..<pollAttempts {
                try? await Task.sleep(for: pollInterval)
                if Task.isCancelled { return }
                if let answer = try? await client.profiles(), answer.current?.id == profile.id, !answer.switching {
                    self?.state = answer
                    arrived = true
                    break
                }
            }
            guard let self, !Task.isCancelled else { return }
            if !arrived { message = "Cuebot has not come back on \(profile.name) yet. Pull down to refresh." }
            switchingTo = nil
            switchCount += 1
        }
    }
}
