import Foundation
import Security

/// Tokens of paired Cuebots, one generic-password item per Cuebot (readable after the first unlock, so the app can
/// reconnect without the phone being unlocked again).
enum Keychain {
    private static let service = "digital.techdaddy.cuebot.companion.tokens"

    struct Secrets: Codable, Equatable {
        var token: String
        var deckToken: String?
    }

    static func save(_ secrets: Secrets, for id: String) {
        guard let data = try? JSONEncoder().encode(secrets) else { return }
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: id]
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound { status = SecItemAdd(query.merging(attributes) { $1 } as CFDictionary, nil) }
        SimulatorFallback.save(data, for: id, keychainStatus: status)
    }

    static func load(for id: String) -> Secrets? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: id, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard let data = status == errSecSuccess ? result as? Data : SimulatorFallback.load(for: id, keychainStatus: status) else { return nil }
        return try? JSONDecoder().decode(Secrets.self, from: data)
    }

    static func delete(for id: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: id]
        SecItemDelete(query as CFDictionary)
        SimulatorFallback.delete(for: id)
    }

    /// A simulator build made without signing (CI, `CODE_SIGNING_ALLOWED=NO`) has no keychain access; when the keychain
    /// refuses there, the tokens go to UserDefaults so the app can still be tested. Never on a device.
    private enum SimulatorFallback {
        private static func key(_ id: String) -> String { "simulatorTokens.\(id)" }

        static func save(_ data: Data, for id: String, keychainStatus: OSStatus) {
            #if targetEnvironment(simulator)
            if keychainStatus != errSecSuccess { UserDefaults.standard.set(data, forKey: key(id)) }
            #endif
        }

        static func load(for id: String, keychainStatus: OSStatus) -> Data? {
            #if targetEnvironment(simulator)
            if keychainStatus != errSecSuccess { return UserDefaults.standard.data(forKey: key(id)) }
            #endif
            return nil
        }

        static func delete(for id: String) {
            #if targetEnvironment(simulator)
            UserDefaults.standard.removeObject(forKey: key(id))
            #endif
        }
    }
}
