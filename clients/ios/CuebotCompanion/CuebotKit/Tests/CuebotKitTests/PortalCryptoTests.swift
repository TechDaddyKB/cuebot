import CryptoKit
import Foundation
import Testing
@testable import CuebotKit

/// The phone's half of the portal encryption against portal/test/vectors.json, which .NET, TypeScript and the Android
/// app check too.
struct PortalCryptoTests {
    nonisolated(unsafe) private static let vectors: [String: Any] = {   // read once, never changed
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0 ..< 7 { url.deleteLastPathComponent() }   // Tests/CuebotKitTests/<file> → the repository root
        let data = try! Data(contentsOf: url.appending(path: "portal/test/vectors.json"))
        return try! JSONSerialization.jsonObject(with: data) as! [String: Any]
    }()

    private func string(_ name: String) -> String { Self.vectors[name] as! String }
    private func bytes(_ name: String) -> Data { PortalCrypto.fromBase64Url(string(name))! }

    private func agreementKey(_ name: String) -> P256.KeyAgreement.PrivateKey {
        let jwk = Self.vectors[name] as! [String: String]
        return try! P256.KeyAgreement.PrivateKey(rawRepresentation: PortalCrypto.fromBase64Url(jwk["d"]!)!)
    }

    private func serverHello(_ signature: String) -> Data {
        Data([1]) + agreementKey("serverEphemeral").publicKey.x963Representation + bytes("identityPublic") + bytes(signature)
    }

    private var frames: [[String: String]] { Self.vectors["frames"] as! [[String: String]] }

    @Test func fingerprintHelloTranscriptAndKeysMatchTheVectors() throws {
        #expect(PortalCrypto.fingerprint(bytes("identityPublic")) == string("fingerprint"))
        let client = agreementKey("clientEphemeral")
        #expect(PortalCrypto.clientHello(client) == bytes("clientHello"))
        let serverRaw = agreementKey("serverEphemeral").publicKey.x963Representation
        let transcript = PortalCrypto.transcript(client: client.publicKey.x963Representation, server: serverRaw, identity: bytes("identityPublic"))
        #expect(transcript == bytes("transcript"))
        let shared = try PortalCrypto.sharedSecret(client, serverRaw)
        #expect(shared.withUnsafeBytes { Data($0) } == bytes("shared"))
        let keys = PortalCrypto.deriveKeys(shared, transcript: transcript)
        #expect(keys.c2s.withUnsafeBytes { Data($0) } == bytes("c2s"))
        #expect(keys.s2c.withUnsafeBytes { Data($0) } == bytes("s2c"))
    }

    @Test(arguments: ["jsSignature", "dotnetSignature"])
    func theHandshakeAcceptsBrowserAndDotnetSignaturesAndTheFramesMatch(signature: String) throws {
        let session = try PortalCrypto.finishHandshake(agreementKey("clientEphemeral"), serverHello: serverHello(signature), pinned: string("fingerprint"))
        for frame in frames {
            let data = PortalCrypto.fromBase64Url(frame["frame"]!)!
            if frame["from"] == "client" {
                #expect(session.encrypt(Data(frame["text"]!.utf8)) == data)
            } else {
                #expect(String(decoding: try session.decrypt(data), as: UTF8.self) == frame["text"])
            }
        }
    }

    @Test func aWrongFingerprintABadSignatureOrAChangedFrameIsRefused() throws {
        let client = agreementKey("clientEphemeral")
        let pinned = string("fingerprint")
        #expect(throws: PortalCryptoError.wrongCuebot) {
            try PortalCrypto.finishHandshake(client, serverHello: serverHello("jsSignature"), pinned: String(repeating: "A", count: 43))
        }
        var bad = serverHello("jsSignature")
        bad[bad.count - 1] &+= 1
        #expect(throws: PortalCryptoError.badSignature) { try PortalCrypto.finishHandshake(client, serverHello: bad, pinned: pinned) }
        #expect(throws: PortalCryptoError.notAServerHello) {
            try PortalCrypto.finishHandshake(client, serverHello: serverHello("jsSignature").prefix(100), pinned: pinned)
        }

        let session = try PortalCrypto.finishHandshake(client, serverHello: serverHello("jsSignature"), pinned: pinned)
        var serverFrame = PortalCrypto.fromBase64Url(frames.first { $0["from"] == "server" }!["frame"]!)!
        serverFrame[0] &+= 1
        #expect(throws: PortalCryptoError.frameFailed) { try session.decrypt(serverFrame) }
        // One failure breaks the session for good, even for a frame that would have been fine.
        #expect(throws: PortalCryptoError.alreadyFailed) { try session.decrypt(Data(count: 16)) }
    }

    @Test func pointsOffTheCurveAreRefused() {
        var raw = bytes("identityPublic")
        raw[64] &+= 1
        #expect(throws: PortalCryptoError.notAPublicKey) { try PortalCrypto.sharedSecret(agreementKey("clientEphemeral"), raw) }
        #expect(!PortalCrypto.verify(publicKey: raw, data: Data([1]), signature: Data(count: 64)))
    }

    @Test func deviceKeysSignInTheFormatCuebotChecks() throws {
        let key = SoftwareDeviceKey()
        let data = PortalCrypto.deviceProof(Data(0 ..< 32))
        let signature = try key.sign(data)
        #expect(signature.count == 64)
        #expect(PortalCrypto.verify(publicKey: key.publicKey, data: data, signature: signature))
        #expect(!PortalCrypto.verify(publicKey: key.publicKey, data: data + [1], signature: signature))
        #expect(String(decoding: data.prefix(23), as: UTF8.self) == "cuebot-portal-device-v1")
        #expect(PortalCrypto.nonce(258) == Data([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2]))
        #expect(PortalCrypto.base64Url(Data([1, 2, 3])) == "AQID" && PortalCrypto.fromBase64Url("AQID") == Data([1, 2, 3]))
        #expect(PortalCrypto.fromBase64Url("-_8") == Data([0xfb, 0xff]))
    }
}
