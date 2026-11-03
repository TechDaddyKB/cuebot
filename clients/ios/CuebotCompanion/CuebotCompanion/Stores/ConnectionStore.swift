import CuebotKit
import Foundation
import Observation

/// The active Cuebot's client and whether it can be reached.
@MainActor @Observable
final class ConnectionStore {
    enum State: Equatable {
        case unpaired
        case checking
        case online(StatusResponse)
        case unreachable(String)
        case unauthorized
    }

    private(set) var state: State = .unpaired
    private(set) var client: CuebotClient?
    let paired: PairedCuebots

    init(paired: PairedCuebots) {
        self.paired = paired
    }

    var endpoint: CuebotEndpoint? { client?.endpoint }

    var status: StatusResponse? {
        if case .online(let status) = state { return status }
        return nil
    }

    /// Rebuilds the client for the active Cuebot and checks it.
    func connect() async {
        guard let endpoint = paired.activeEndpoint else {
            client = nil
            state = .unpaired
            return
        }
        if client?.endpoint != endpoint { client = CuebotClient(endpoint: endpoint) }
        await check()
    }

    func check() async {
        guard let client else { return }
        state = .checking
        do {
            let status = try await client.status()
            if let id = paired.activeId { paired.learnInstanceId(status.app.instanceId, for: id) }
            state = .online(status)
        } catch .unauthorized {
            state = .unauthorized
        } catch {
            if Task.isCancelled { return }   // a newer check is on its way
            state = .unreachable(Self.describe(error))
        }
    }

    /// A call failed: a 401 means pair again; anything else is shown where it happened.
    func report(_ error: CuebotError) {
        if error == .unauthorized { state = .unauthorized }
    }

    static func describe(_ error: CuebotError) -> String {
        switch error {
        case .unreachable(let message): message
        case .unauthorized: "Cuebot did not accept the token. Pair again."
        case .notFound: "Cuebot does not know that (it may need an update)."
        case .tooManyStreams: "Too many live connections are open to Cuebot."
        case .http(let status, let message): message.isEmpty ? "Cuebot answered \(status)." : "Cuebot answered \(status): \(message)"
        case .invalidResponse(let message): "Cuebot's answer was not understood (\(message))."
        }
    }
}
