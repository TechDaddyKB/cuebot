import CryptoKit
import Foundation

/// The mod portal's end-to-end encryption, the phone's half of src/Cuebot.Portal/Crypto and portal/src/crypto.ts
/// (docs/MOD_PORTAL_PLAN.md, section 3). CryptoKit only. Formats: P-256 public keys as raw uncompressed points
/// (65 bytes, CryptoKit's x963Representation), signatures as IEEE P1363 (64 bytes, CryptoKit's rawRepresentation),
/// frames as AES-256-GCM ciphertext || tag with a counter nonce. Checked against portal/test/vectors.json.
public enum PortalCrypto {
    public static let version: UInt8 = 1
    public static let label = "cuebot-portal-v1"
    public static let deviceProofLabel = "cuebot-portal-device-v1"
    public static let publicKeyLength = 65
    public static let signatureLength = 64
    public static let clientHelloLength = 1 + publicKeyLength
    public static let serverHelloLength = 1 + 2 * publicKeyLength + signatureLength

    public static func base64Url(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    public static func fromBase64Url(_ text: String) -> Data? {
        var s = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        s += String(repeating: "=", count: (4 - s.count % 4) % 4)
        return Data(base64Encoded: s)
    }

    public static func sha256(_ data: Data) -> Data { Data(SHA256.hash(data: data)) }

    /// Bytes from the system's CSPRNG (SystemRandomNumberGenerator).
    public static func randomBytes(_ count: Int) -> Data {
        var rng = SystemRandomNumberGenerator()
        return Data((0 ..< count).map { _ in UInt8.random(in: .min ... .max, using: &rng) })
    }

    /// base64url of SHA-256 of the raw public key: what an invite pins.
    public static func fingerprint(_ publicKey: Data) -> String { base64Url(sha256(publicKey)) }

    public static func newEphemeral() -> P256.KeyAgreement.PrivateKey { P256.KeyAgreement.PrivateKey() }

    public static func clientHello(_ ephemeral: P256.KeyAgreement.PrivateKey) -> Data {
        Data([version]) + ephemeral.publicKey.x963Representation
    }

    public static func transcript(client: Data, server: Data, identity: Data) -> Data {
        Data(label.utf8) + client + server + identity
    }

    /// The raw ECDH secret (the x coordinate); throws when `other` is not a point on P-256.
    public static func sharedSecret(_ own: P256.KeyAgreement.PrivateKey, _ other: Data) throws(PortalCryptoError) -> SharedSecret {
        guard other.count == publicKeyLength, other.first == 4,
              let key = try? P256.KeyAgreement.PublicKey(x963Representation: other),
              let secret = try? own.sharedSecretFromKeyAgreement(with: key) else { throw .notAPublicKey }
        return secret
    }

    public struct Keys: Sendable {
        public let c2s: SymmetricKey, s2c: SymmetricKey
        /// SHA-256 of the transcript: the same on both ends and unique to the connection.
        public let binding: Data
    }

    /// HKDF-SHA256 with the transcript's hash as salt: one AES-256 key per direction.
    public static func deriveKeys(_ shared: SharedSecret, transcript: Data) -> Keys {
        let salt = sha256(transcript)
        func key(_ info: String) -> SymmetricKey {
            shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: salt, sharedInfo: Data(info.utf8), outputByteCount: 32)
        }
        return Keys(c2s: key("c2s"), s2c: key("s2c"), binding: salt)
    }

    /// The bytes a device signs with its own key to prove it holds it on the connection with this binding.
    public static func deviceProof(_ binding: Data) -> Data { Data(deviceProofLabel.utf8) + binding }

    /// 12 bytes: four zeros, then the counter big-endian.
    public static func nonce(_ counter: UInt64) -> Data {
        var n = Data(count: 4)
        withUnsafeBytes(of: counter.bigEndian) { n.append(contentsOf: $0) }
        return n
    }

    public static func verify(publicKey: Data, data: Data, signature: Data) -> Bool {
        guard signature.count == signatureLength, publicKey.count == publicKeyLength, publicKey.first == 4,
              let key = try? P256.Signing.PublicKey(x963Representation: publicKey),
              let sig = try? P256.Signing.ECDSASignature(rawRepresentation: signature) else { return false }
        return key.isValidSignature(sig, for: data)
    }

    /// A device's side: check the server hello against the pinned fingerprint and derive the session.
    public static func finishHandshake(_ ephemeral: P256.KeyAgreement.PrivateKey, serverHello: Data,
                                       pinned: String) throws(PortalCryptoError) -> PortalSession {
        let hello = Data(serverHello)   // indices from 0
        guard hello.count == serverHelloLength, hello.first == version else { throw .notAServerHello }
        let serverEphemeral = hello.subdata(in: 1 ..< 1 + publicKeyLength)
        let identity = hello.subdata(in: 1 + publicKeyLength ..< 1 + 2 * publicKeyLength)
        let signature = hello.subdata(in: 1 + 2 * publicKeyLength ..< serverHelloLength)
        guard fingerprint(identity) == pinned else { throw .wrongCuebot }
        let t = transcript(client: ephemeral.publicKey.x963Representation, server: serverEphemeral, identity: identity)
        guard verify(publicKey: identity, data: t, signature: signature) else { throw .badSignature }
        let keys = deriveKeys(try sharedSecret(ephemeral, serverEphemeral), transcript: t)
        return PortalSession(send: keys.c2s, receive: keys.s2c, binding: keys.binding)
    }
}

public enum PortalCryptoError: Error, Equatable, Sendable {
    case notAPublicKey, notAServerHello, wrongCuebot, badSignature, frameFailed, alreadyFailed
}

/// One encrypted connection. Frames must arrive in order; any failure breaks the session for good.
public final class PortalSession: @unchecked Sendable {
    private let sendKey: SymmetricKey, receiveKey: SymmetricKey
    /// SHA-256 of the handshake transcript: what a device signs to prove its key on this connection.
    public let binding: Data
    private let lock = NSLock()
    private var sent: UInt64 = 0, received: UInt64 = 0, broken = false

    public init(send: SymmetricKey, receive: SymmetricKey, binding: Data) {
        sendKey = send
        receiveKey = receive
        self.binding = binding
    }

    public func encrypt(_ plaintext: Data) -> Data {
        lock.withLock {
            defer { sent += 1 }
            // A 12-byte nonce is always valid and sealing in memory cannot fail otherwise.
            let box = try! AES.GCM.seal(plaintext, using: sendKey, nonce: try! AES.GCM.Nonce(data: PortalCrypto.nonce(sent)))
            return box.ciphertext + box.tag
        }
    }

    public func decrypt(_ frame: Data) throws(PortalCryptoError) -> Data {
        lock.lock()
        defer { lock.unlock() }
        if broken { throw .alreadyFailed }
        let f = Data(frame)
        guard f.count >= 16,
              let box = try? AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: PortalCrypto.nonce(received)),
                                               ciphertext: f.prefix(f.count - 16), tag: f.suffix(16)),
              let plain = try? AES.GCM.open(box, using: receiveKey) else {
            broken = true
            throw .frameFailed
        }
        received += 1
        return plain
    }
}

/// The key a device proves itself with on every connection (docs/MOD_PORTAL_PLAN.md, section 4). In the app it lives in
/// the Secure Enclave or the Keychain and cannot be read out; tests use `SoftwareDeviceKey`.
public protocol DeviceKey: Sendable {
    /// The raw P-256 public key (65 bytes).
    var publicKey: Data { get }
    /// An ECDSA P-256 SHA-256 signature over `data`, as r || s.
    func sign(_ data: Data) throws -> Data
}

public struct SoftwareDeviceKey: DeviceKey {
    public let key: P256.Signing.PrivateKey
    public init(_ key: P256.Signing.PrivateKey = .init()) { self.key = key }
    public var publicKey: Data { key.publicKey.x963Representation }
    public func sign(_ data: Data) throws -> Data { try key.signature(for: data).rawRepresentation }
}
