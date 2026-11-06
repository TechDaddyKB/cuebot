import CryptoKit
import CuebotKit
import Foundation
import Security

/// This phone's mod portal keys, one ECDSA P-256 key per membership (docs/MOD_PORTAL_PLAN.md, section 4): in the Secure
/// Enclave when the phone has one, else a Keychain key marked not extractable. The app can sign with them; nothing can
/// read them out.
enum DeviceKeys {
    private static func tag(_ alias: String) -> Data { Data("digital.techdaddy.cuebot.companion.portal.\(alias)".utf8) }

    static func create(_ alias: String) throws -> DeviceKey {
        delete(alias)
        var privateAttributes: [String: Any] = [kSecAttrIsPermanent as String: true, kSecAttrApplicationTag as String: tag(alias)]
        var attributes: [String: Any] = [kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom, kSecAttrKeySizeInBits as String: 256,
                                         kSecAttrIsExtractable as String: false]
        if SecureEnclave.isAvailable {
            attributes[kSecAttrTokenID as String] = kSecAttrTokenIDSecureEnclave
            privateAttributes[kSecAttrAccessControl as String] = SecAccessControlCreateWithFlags(
                nil, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly, .privateKeyUsage, nil) as Any
        } else {
            privateAttributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        }
        attributes[kSecPrivateKeyAttrs as String] = privateAttributes
        var error: Unmanaged<CFError>?
        if let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error), let device = KeychainKey(key) { return device }
        #if targetEnvironment(simulator)
        // An unsigned simulator build (CI) has no keychain: a software key kept like the other simulator fallbacks.
        let software = P256.Signing.PrivateKey()
        Keychain.SimulatorFallback.save(software.rawRepresentation, for: "portalKey.\(alias)", keychainStatus: errSecMissingEntitlement)
        return SoftwareDeviceKey(software)
        #else
        throw error?.takeRetainedValue() ?? CocoaError(.featureUnsupported)
        #endif
    }

    /// The key under `alias`, or nil when it is gone (the app was reinstalled, or the phone restored).
    static func open(_ alias: String) -> DeviceKey? {
        let query: [String: Any] = [kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag(alias),
                                    kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom, kSecReturnRef as String: true]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecSuccess, let found = result, CFGetTypeID(found) == SecKeyGetTypeID() {
            return KeychainKey(found as! SecKey)   // swiftlint:disable:this force_cast
        }
        #if targetEnvironment(simulator)
        if let raw = Keychain.SimulatorFallback.load(for: "portalKey.\(alias)", keychainStatus: status),
           let key = try? P256.Signing.PrivateKey(rawRepresentation: raw) { return SoftwareDeviceKey(key) }
        #endif
        return nil
    }

    static func delete(_ alias: String) {
        SecItemDelete([kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag(alias)] as CFDictionary)
        Keychain.SimulatorFallback.delete(for: "portalKey.\(alias)")
    }

    /// A key in the Keychain or the Secure Enclave. They sign in DER; Cuebot checks r || s, as WebCrypto makes it.
    private struct KeychainKey: DeviceKey, @unchecked Sendable {
        let key: SecKey
        let publicKey: Data

        init?(_ key: SecKey) {
            guard let pub = SecKeyCopyPublicKey(key), let raw = SecKeyCopyExternalRepresentation(pub, nil) as Data?,
                  raw.count == PortalCrypto.publicKeyLength else { return nil }
            self.key = key
            publicKey = raw
        }

        func sign(_ data: Data) throws -> Data {
            var error: Unmanaged<CFError>?
            guard let der = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &error) as Data? else {
                throw error?.takeRetainedValue() ?? CocoaError(.featureUnsupported)
            }
            return try P256.Signing.ECDSASignature(derRepresentation: der).rawRepresentation
        }
    }
}
