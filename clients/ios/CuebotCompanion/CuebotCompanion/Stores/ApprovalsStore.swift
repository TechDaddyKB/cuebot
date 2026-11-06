import CuebotKit
import Foundation
import Observation

/// The streamer's side: mods' changes that wait for an OK on the paired Cuebot (GET /api/v1/portal/changes), checked
/// every few seconds while the Mods tab is on screen, approved or rejected from the phone (docs/MOD_PORTAL_PLAN.md,
/// section 5). Approving something the streamer changed since the mod asked needs a second tap, as on the desktop.
@MainActor @Observable
final class ApprovalsStore {
    private(set) var pending: [PendingChange] = []
    /// False until the first answer.
    private(set) var loaded = false
    /// Cuebot answered 404: it is older than the approval queue on the REST API.
    private(set) var tooOld = false
    /// Changes that changed since the mod asked: the next approve uses the mod's version anyway.
    private(set) var conflicts: Set<String> = []
    var message: String?

    /// Checks the queue every `interval` until the task is cancelled (the tab goes off screen).
    func poll(_ connection: ConnectionStore, interval: Duration = .seconds(5)) async {
        while !Task.isCancelled {
            await refresh(connection)
            try? await Task.sleep(for: interval)
        }
    }

    func refresh(_ connection: ConnectionStore) async {
        guard let client = connection.client else {
            (pending, loaded) = ([], false)
            return
        }
        do {
            pending = try await client.portalChanges()
            conflicts = conflicts.filter { id in pending.contains { $0.id == id } }
            (loaded, tooOld) = (true, false)
        } catch .notFound {
            (pending, loaded, tooOld) = ([], true, true)
        } catch .http(status: 503, _) {
            (pending, loaded) = ([], true)   // the mod portal is not set up on that Cuebot: nothing to approve
        } catch {
            if Task.isCancelled { return }
            connection.report(error)
        }
    }

    func decide(_ change: PendingChange, approve: Bool, _ connection: ConnectionStore) async {
        guard let client = connection.client else { return }
        let force = approve && conflicts.contains(change.id)
        do {
            let d = try await client.decideChange(change.id, approve: approve, force: force)
            message = switch d.result {
            case .applied: "Approved: it is live now."
            case .rejected: "Rejected. Nothing changed."
            case .conflict: "'\(change.target)' changed since \(change.member) asked. Approve again to use their version anyway."
            case .notPending: "That change was already decided, or it expired."
            }
            if d.result == .conflict { conflicts.insert(change.id) } else { conflicts.remove(change.id) }
            pending = d.pending
        } catch {
            connection.report(error)
            message = error == .notFound ? "That change was already decided, or it expired." : ConnectionStore.describe(error)
            await refresh(connection)
        }
    }
}
