import CuebotKit
import Foundation
import Observation

/// The Dashboard tab's data: one /api/v1/dashboard snapshot, fetched again whenever a `dashboard.changed` event says
/// the revision moved on, plus the subathon and goal controls.
@MainActor @Observable
final class DashboardStore {
    private(set) var dashboard: Dashboard?
    /// Cuebot answered 404: it is older than the dashboard endpoint (the Deck tab still works).
    private(set) var tooOld = false
    private(set) var error: String?
    private(set) var streamState: StreamState = .connecting
    /// The last control that failed, shown under the cards for a few seconds.
    private(set) var controlError: String?

    private var client: CuebotClient?
    private var streamTask: Task<Void, Never>?
    private var fetching = false
    private var fetchAgain = false
    /// Bumped by every load, so a fetch begun for an earlier Cuebot (or an earlier load) cannot land its result.
    private var generation = 0

    var isReconnecting: Bool {
        if case .reconnecting = streamState { return true }
        return false
    }

    func load(client: CuebotClient?) async {
        stop()
        generation += 1
        fetching = false
        self.client = client
        dashboard = nil
        tooOld = false
        error = nil
        guard client != nil else { return }
        await refresh()
        if !tooOld { startStream() }
    }

    /// Fetches the snapshot; calls that arrive while a fetch runs are folded into one more fetch afterwards.
    func refresh() async {
        guard let client else { return }
        if fetching {
            fetchAgain = true
            return
        }
        let mine = generation
        fetching = true
        defer { if generation == mine { fetching = false } }
        repeat {
            fetchAgain = false
            do {
                let snapshot = try await client.dashboard()
                guard generation == mine else { return }
                if snapshot.revision >= (dashboard?.revision ?? .min) || snapshot.instanceId != dashboard?.instanceId { dashboard = snapshot }
                error = nil
            } catch .notFound {
                if generation == mine { tooOld = true }
            } catch {
                guard generation == mine, !Task.isCancelled else { return }   // a cancelled load is not an error
                self.error = ConnectionStore.describe(error)
            }
        } while fetchAgain && generation == mine
    }

    private func startStream() {
        guard let client else { return }
        streamTask = Task { [weak self] in
            let events = client.apiEvents(types: ["dashboard.changed"]) { state in
                Task { @MainActor in
                    guard let self else { return }
                    let wasReconnecting = self.isReconnecting
                    self.streamState = state
                    if state == .connected && wasReconnecting { await self.refresh() }   // catch up on what was missed
                }
            }
            do {
                for try await event in events {
                    guard let self else { return }
                    let revision = event.apiEvent.args["revision"]?.double.map(Int.init) ?? Int.max
                    if revision > (self.dashboard?.revision ?? -1) { await self.refresh() }
                }
            } catch {
                self?.error = ConnectionStore.describe(error as? CuebotError ?? .unreachable("\(error)"))
            }
        }
    }

    func stop() {
        streamTask?.cancel()
        streamTask = nil
    }

    func resume() {
        guard streamTask == nil, client != nil, !tooOld else { return }
        Task {
            await refresh()
            startStream()
        }
    }

    // MARK: Controls

    func subathon(_ timer: SubathonTimer, _ command: CuebotClient.SubathonCommand) async {
        await control { try await $0.subathon(timer.id, command) }
    }

    func addTime(_ timer: SubathonTimer, seconds: Double) async {
        await control { try await $0.subathonTime(timer.id, add: seconds) }
    }

    func setMultiplier(_ timer: SubathonTimer, _ value: Double) async {
        await control { try await $0.subathonMultiplier(timer.id, value) }
    }

    func addPoints(_ goal: PointsGoal, _ points: Double) async {
        await control { try await $0.goalPoints(goal.id, add: points) }
    }

    func setPoints(_ goal: PointsGoal, _ points: Double) async {
        await control { try await $0.goalPoints(goal.id, set: points) }
    }

    func setMultiplier(_ goal: PointsGoal, _ value: Double) async {
        await control { try await $0.goalMultiplier(goal.id, value) }
    }

    func reset(_ goal: PointsGoal) async {
        await control { try await $0.goalReset(goal.id) }
    }

    private func control(_ call: (CuebotClient) async throws -> SubathonState) async {
        guard let client else { return }
        do {
            let state = try await call(client)
            if !state.timers.isEmpty || !state.goals.isEmpty { dashboard?.subathon = state }
            controlError = nil
        } catch {
            controlError = ConnectionStore.describe(error as? CuebotError ?? .unreachable(error.localizedDescription))
        }
    }
}
