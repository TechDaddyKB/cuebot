import CryptoKit
import Foundation
import Network
import Testing
@testable import CuebotKit

/// A stand-in for the relay and the streamer's Cuebot on a local WebSocket: answers the handshake with its identity key,
/// then answers every request with {ok, req, op, body}. "binding" answers with the connection's binding, "close"
/// closes the socket with the code in the body. `closeCode` closes every new connection before the handshake.
final class FakePortalCuebot: @unchecked Sendable {
    let identity = P256.Signing.PrivateKey()
    var closeCode: Int?
    private let listener: NWListener
    private let queue = DispatchQueue(label: "fake-portal")
    private(set) var port: UInt16 = 0

    var fingerprint: String { PortalCrypto.fingerprint(identity.publicKey.x963Representation) }
    var target: PortalTarget { PortalTarget(relay: String(repeating: "a", count: 32), fingerprint: fingerprint, relayOrigin: "ws://127.0.0.1:\(port)") }

    init() async throws {
        let parameters = NWParameters.tcp
        let ws = NWProtocolWebSocket.Options()
        ws.autoReplyPing = true
        parameters.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        listener = try NWListener(using: parameters, on: .any)
        listener.newConnectionHandler = { [weak self] in self?.accept($0) }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            nonisolated(unsafe) var done = false
            listener.stateUpdateHandler = { [weak self] state in
                guard !done else { return }
                switch state {
                case .ready:
                    done = true
                    self?.port = self?.listener.port?.rawValue ?? 0
                    continuation.resume()
                case .failed(let error):
                    done = true
                    continuation.resume(throwing: error)
                default: break
                }
            }
            listener.start(queue: queue)
        }
    }

    deinit { listener.cancel() }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: queue)
        if let code = closeCode {
            close(connection, code)
            return
        }
        var session: PortalSession?
        func receive() {
            connection.receiveMessage { [weak self] data, _, _, error in
                guard let self, error == nil, let data else { return }
                if let s = session {
                    guard let plain = try? s.decrypt(data),
                          var message = try? JSONDecoder().decode(PortalAnswer.self, from: plain) else { return }
                    let op = message.removeValue(forKey: "op")?.string ?? ""
                    let req = message.removeValue(forKey: "req") ?? .null
                    if op == "close" {
                        self.close(connection, Int(message["code"]?.double ?? 1000))
                        return
                    }
                    var answer: PortalAnswer = ["ok": .bool(true), "req": req, "op": .string(op), "body": .object(message)]
                    if op == "binding" { answer["binding"] = .string(PortalCrypto.base64Url(s.binding)) }
                    self.send(connection, s.encrypt(try! JSONEncoder().encode(answer)))
                } else {
                    let (hello, s) = self.handshake(data)
                    session = s
                    self.send(connection, hello)
                }
                receive()
            }
        }
        receive()
    }

    /// Cuebot's side of the handshake (src/Cuebot.Portal/Crypto): its ephemeral key and a signature over the transcript.
    private func handshake(_ clientHello: Data) -> (Data, PortalSession) {
        let ephemeral = P256.KeyAgreement.PrivateKey()
        let client = clientHello.subdata(in: 1 ..< 66)
        let identityRaw = identity.publicKey.x963Representation
        let transcript = PortalCrypto.transcript(client: client, server: ephemeral.publicKey.x963Representation, identity: identityRaw)
        let signature = try! identity.signature(for: transcript).rawRepresentation
        let keys = PortalCrypto.deriveKeys(try! PortalCrypto.sharedSecret(ephemeral, client), transcript: transcript)
        return (Data([1]) + ephemeral.publicKey.x963Representation + identityRaw + signature,
                PortalSession(send: keys.s2c, receive: keys.c2s, binding: keys.binding))
    }

    private func send(_ connection: NWConnection, _ data: Data) {
        let meta = NWProtocolWebSocket.Metadata(opcode: .binary)
        connection.send(content: data, contentContext: .init(identifier: "frame", metadata: [meta]), isComplete: true, completion: .idempotent)
    }

    private func close(_ connection: NWConnection, _ code: Int) {
        let meta = NWProtocolWebSocket.Metadata(opcode: .close)
        meta.closeCode = .privateCode(UInt16(code))
        connection.send(content: nil, contentContext: .init(identifier: "close", metadata: [meta]), isComplete: true,
                        completion: .contentProcessed { _ in connection.cancel() })
    }
}

@Suite(.serialized)
struct PortalLinkTests {
    @Test func requestsAreEncryptedAnsweredAndMatchedByReq() async throws {
        let cuebot = try await FakePortalCuebot()
        let link = try await PortalLink.open(cuebot.target)
        defer { link.close() }
        async let a = link.request("first", ["n": .number(1)])
        async let b = link.request("second")
        let (first, second) = await (a, b)
        #expect(first.ok && first.text("op") == "first" && first["body"]?["n"]?.double == 1)
        #expect(second.text("op") == "second")
        // A big answer (the portal UI bundle comes in one frame).
        let big = await link.request("echo", ["js": .string(String(repeating: "x", count: 2_000_000))])
        #expect(big["body"]?["js"]?.string?.count == 2_000_000)
    }

    @Test func theDeviceProofIsBoundToThisConnection() async throws {
        let cuebot = try await FakePortalCuebot()
        let link = try await PortalLink.open(cuebot.target)
        defer { link.close() }
        let answer = await link.request("binding")
        let binding = PortalCrypto.fromBase64Url(answer.text("binding")!)!
        #expect(binding == link.binding)
        let key = SoftwareDeviceKey()
        guard case .object(let device) = try link.deviceProof(key, name: "Cuebot app on iOS") else { Issue.record("not an object"); return }
        #expect(device["key"]?.string == PortalCrypto.base64Url(key.publicKey) && device["name"]?.string == "Cuebot app on iOS")
        let proof = PortalCrypto.fromBase64Url(device["proof"]!.string!)!
        #expect(PortalCrypto.verify(publicKey: key.publicKey, data: PortalCrypto.deviceProof(binding), signature: proof))
    }

    @Test func aCuebotThatIsNotThePinnedOneIsRefused() async throws {
        let cuebot = try await FakePortalCuebot()
        var target = cuebot.target
        target.fingerprint = String(repeating: "A", count: 43)
        await #expect(throws: LinkClosed("This link does not lead to the streamer's Cuebot it was made for. Ask the streamer for a new invite.")) {
            try await PortalLink.open(target)
        }
    }

    @Test func aLostLinkAnswersWaitingRequestsAndSaysWhy() async throws {
        let cuebot = try await FakePortalCuebot()
        let link = try await PortalLink.open(cuebot.target)
        let closed = AsyncStream<String>.makeStream()
        link.onClosed { closed.continuation.yield($0) }
        let answer = await link.request("close", ["code": .number(4403)])
        #expect(!answer.ok && answer.error == "The streamer has closed the mod portal.")
        var reasons = closed.stream.makeAsyncIterator()
        #expect(await reasons.next() == "The streamer has closed the mod portal.")
        let after = await link.request("anything")
        #expect(after.error == "The streamer has closed the mod portal.")
        link.close()
    }

    @Test(arguments: [(4404, "not online"), (4429, "Too many people")])
    func anOfflineCuebotGetsWordsForTheMod(code: Int, words: String) async throws {
        let cuebot = try await FakePortalCuebot()
        cuebot.closeCode = code
        do {
            _ = try await PortalLink.open(cuebot.target)
            Issue.record("opened")
        } catch {
            #expect(error.message.contains(words))
        }
    }

    @Test func nothingListeningIsLost() async throws {
        let target = PortalTarget(relay: String(repeating: "a", count: 32), fingerprint: String(repeating: "A", count: 43), relayOrigin: "ws://127.0.0.1:9")
        await #expect(throws: LinkClosed.self) { try await PortalLink.open(target, timeout: .seconds(5)) }
    }
}
