import Foundation

/// What a live stream is doing, for the "Reconnecting…" bar.
public enum StreamState: Equatable, Sendable {
    case connecting
    case connected
    /// Waiting `retryIn` seconds after `error` before the next attempt.
    case reconnecting(retryIn: Double, error: String)
}

/// Server-sent events over URLSession, reconnecting with backoff until the consumer stops iterating (or the task is
/// cancelled). Ends with an error only when retrying cannot help: 401 (pair again) or 404 (no such stream).
enum EventStream {
    static func events(session: URLSession, backoff: Backoff, onState: @escaping @Sendable (StreamState) -> Void,
                       request: @escaping @Sendable (String?) -> URLRequest) -> AsyncThrowingStream<ServerEvent, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                var lastEventId: String?
                var attempt = 0
                while !Task.isCancelled {
                    onState(.connecting)
                    var parser = ServerSentEventParser(lastEventId: lastEventId)
                    var problem: String
                    do {
                        let (bytes, response) = try await session.bytes(for: request(lastEventId))
                        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                        if let failure = CuebotClient.failure(status: status, body: Data()) {
                            if failure == .unauthorized || failure == .notFound {
                                continuation.finish(throwing: failure)
                                return
                            }
                            throw failure
                        }
                        onState(.connected)
                        attempt = 0
                        var chunk: [UInt8] = []
                        chunk.reserveCapacity(4096)
                        for try await byte in bytes {
                            chunk.append(byte)
                            // Parse at line ends so events are delivered as soon as their blank line arrives.
                            guard byte == 0x0A || byte == 0x0D || chunk.count >= 4096 else { continue }
                            for event in parser.feed(chunk) { continuation.yield(event) }
                            chunk.removeAll(keepingCapacity: true)
                        }
                        problem = "the stream ended"
                    } catch is CancellationError {
                        break
                    } catch let error as CuebotError {
                        problem = String(describing: error)
                    } catch {
                        if Task.isCancelled { break }
                        problem = error.localizedDescription
                    }
                    lastEventId = parser.lastEventId
                    let wait = parser.retryMilliseconds.map { max(Double($0) / 1000, backoff.delay(attempt: attempt)) } ?? backoff.delay(attempt: attempt)
                    attempt += 1
                    onState(.reconnecting(retryIn: wait, error: problem))
                    do { try await Task.sleep(for: .seconds(wait)) } catch { break }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}
