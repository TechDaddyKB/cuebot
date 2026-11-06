import CryptoKit
import Foundation

/// Why the link could not be opened or was lost, in words for the mod.
public struct LinkClosed: Error, Equatable, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

/// One encrypted connection from this phone to the streamer's Cuebot through the relay: the handshake pinned to the
/// invite's fingerprint, then JSON requests and answers matched by "req" (portal/web/link.ts does the same in a
/// browser). A lost link answers every waiting request with ok false and the reason.
public final class PortalLink: NSObject, URLSessionWebSocketDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionWebSocketTask!
    private var urlSession: URLSession!
    private var portalSession: PortalSession?
    private let ephemeral = PortalCrypto.newEphemeral()
    private var hello: CheckedContinuation<Data, any Error>?
    private var helloResult: Result<Data, LinkClosed>?
    private var closeCode: Int?
    private var next = 1
    private var waiting: [Int: CheckedContinuation<PortalAnswer, Never>] = [:]
    private var failure: String?
    private var onClosed: (@Sendable (String) -> Void)?
    private var pinger: Task<Void, Never>?

    /// SHA-256 of the handshake transcript: what a device signs to prove its key on this connection.
    public var binding: Data { portalSession?.binding ?? Data() }

    private override init() { super.init() }

    /// Connects to `<relay origin>/c/<relay id>` and completes the handshake; throws `LinkClosed` with words for the mod.
    public static func open(_ target: PortalTarget, timeout: Duration = .seconds(15)) async throws(LinkClosed) -> PortalLink {
        guard let url = URL(string: "\(target.relayOrigin)/c/\(target.relay)") else { throw LinkClosed(closeReason(0)) }
        let link = PortalLink()
        link.urlSession = URLSession(configuration: .ephemeral, delegate: link, delegateQueue: nil)
        link.task = link.urlSession.webSocketTask(with: url)
        link.task.maximumMessageSize = 64 * 1024 * 1024   // the portal UI bundle arrives in one frame
        let serverHello: Data
        do {
            serverHello = try await withThrowingTaskGroup(of: Data.self) { group in
                group.addTask { try await link.waitForHello() }
                group.addTask {
                    try await Task.sleep(for: timeout)
                    throw LinkClosed("The streamer's Cuebot did not answer.")
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
        } catch {
            link.shutDown()
            throw error as? LinkClosed ?? LinkClosed(closeReason(0))
        }
        do {
            link.portalSession = try PortalCrypto.finishHandshake(link.ephemeral, serverHello: serverHello, pinned: target.fingerprint)
        } catch {
            link.shutDown()
            throw LinkClosed("This link does not lead to the streamer's Cuebot it was made for. Ask the streamer for a new invite.")
        }
        link.receiveLoop()
        // The relay drops idle sockets after a while; pings keep a page that is only being read open.
        link.pinger = Task { [weak link] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                link?.task.sendPing { _ in }
            }
        }
        return link
    }

    private func waitForHello() async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if let helloResult {
                lock.unlock()
                continuation.resume(with: helloResult.mapError { $0 as any Error })
                return
            }
            hello = continuation
            lock.unlock()
            task.resume()
        }
    }

    private func finishHello(_ result: Result<Data, LinkClosed>) {
        lock.lock()
        let waiter = hello
        hello = nil
        if helloResult == nil { helloResult = result }
        lock.unlock()
        waiter?.resume(with: result.mapError { $0 as any Error })
    }

    /// Calls `listener` once when the link is lost (not when `close()` is called).
    public func onClosed(_ listener: @escaping @Sendable (String) -> Void) {
        lock.lock()
        onClosed = listener
        let failed = failure
        lock.unlock()
        if let failed { listener(failed) }
    }

    /// Sends one request and waits for its answer.
    public func request(_ op: String, _ body: PortalAnswer = [:]) async -> PortalAnswer {
        await withCheckedContinuation { continuation in
            lock.lock()
            if let failure {
                lock.unlock()
                continuation.resume(returning: ["ok": .bool(false), "error": .string(failure)])
                return
            }
            let req = next
            next += 1
            waiting[req] = continuation
            var message = body
            message["op"] = .string(op)
            message["req"] = .number(Double(req))
            let plain = (try? JSONEncoder().encode(message)) ?? Data("{}".utf8)
            // Frames carry counter nonces: encrypting and queueing them happen in one order, under the lock.
            let frame = portalSession!.encrypt(plain)
            task.send(.data(frame)) { [weak self] error in
                if error != nil { self?.fail(Self.closeReason(self?.closeCodeNow() ?? 0)) }
            }
            lock.unlock()
        }
    }

    public func close() {
        lock.lock()
        onClosed = nil
        lock.unlock()
        fail("Closed.", notify: false)
        shutDown()
    }

    private func shutDown() {
        pinger?.cancel()
        task?.cancel(with: .normalClosure, reason: nil)
        urlSession?.invalidateAndCancel()   // the session holds its delegate (this link) until invalidated
    }

    private func closeCodeNow() -> Int {
        lock.lock()
        defer { lock.unlock() }
        if let closeCode { return closeCode }
        let code = task.closeCode.rawValue
        return code == URLSessionWebSocketTask.CloseCode.invalid.rawValue ? 0 : code
    }

    private func receiveLoop() {
        task.receive { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(.data(let data)):
                self.receive(data)
                self.receiveLoop()
            case .success:
                self.receiveLoop()   // the relay sends only binary frames
            case .failure:
                self.fail(Self.closeReason(self.closeCodeNow()))
            }
        }
    }

    private func receive(_ frame: Data) {
        let answer: PortalAnswer
        do {
            let plain = try portalSession!.decrypt(frame)
            answer = try JSONDecoder().decode(PortalAnswer.self, from: plain)
        } catch {
            task.cancel(with: .normalClosure, reason: nil)
            fail("The secure connection failed. Open the portal again.")
            return
        }
        guard let req = answer["req"]?.double.map(Int.init) else { return }
        lock.lock()
        let waiter = waiting.removeValue(forKey: req)
        lock.unlock()
        waiter?.resume(returning: answer)
    }

    private func fail(_ reason: String, notify: Bool = true) {
        lock.lock()
        if failure != nil {
            lock.unlock()
            return
        }
        failure = reason
        let waiters = waiting.values
        waiting.removeAll()
        let listener = notify ? onClosed : nil
        lock.unlock()
        for waiter in waiters { waiter.resume(returning: ["ok": .bool(false), "error": .string(reason)]) }
        listener?(reason)
    }

    // MARK: URLSessionWebSocketDelegate

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        webSocketTask.send(.data(PortalCrypto.clientHello(ephemeral))) { _ in }
        webSocketTask.receive { [weak self] result in
            switch result {
            case .success(.data(let data)): self?.finishHello(.success(data))
            case .success: self?.finishHello(.failure(LinkClosed(Self.closeReason(0))))
            case .failure: self?.finishHello(.failure(LinkClosed(Self.closeReason(self?.closeCodeNow() ?? 0))))
            }
        }
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                           didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        lock.lock()
        self.closeCode = closeCode.rawValue
        lock.unlock()
        let words = Self.closeReason(closeCode.rawValue)
        finishHello(.failure(LinkClosed(words)))
        if portalSession != nil { fail(words) }
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        let words = Self.closeReason(closeCodeNow())
        finishHello(.failure(LinkClosed(words)))
        if portalSession != nil { fail(words) }
    }

    /// Words for the relay's close codes (portal/RELAY.md).
    public static func closeReason(_ code: Int) -> String {
        switch code {
        case 4404, 4503: "The streamer's Cuebot is not online right now. Try again when it is running."
        case 4403: "The streamer has closed the mod portal."
        case 4429: "Too many people are connecting right now. Try again in a minute."
        default: "The connection to the streamer's Cuebot was lost."
        }
    }
}

public extension PortalLink {
    /// `{key, proof[, name]}`: this device's public key and its signature over the link's binding.
    func deviceProof(_ key: DeviceKey, name: String? = nil) throws -> JSONValue {
        var device: [String: JSONValue] = [
            "key": .string(PortalCrypto.base64Url(key.publicKey)),
            "proof": .string(PortalCrypto.base64Url(try key.sign(PortalCrypto.deviceProof(binding)))),
        ]
        if let name { device["name"] = .string(name) }
        return .object(device)
    }
}
