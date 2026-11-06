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
        saveData(data, account: id)
    }

    static func load(for id: String) -> Secrets? {
        loadData(account: id).flatMap { try? JSONDecoder().decode(Secrets.self, from: $0) }
    }

    static func delete(for id: String) { deleteData(account: id) }

    /// Any small secret under `account` (the mod portal's pending sign-in, with its PKCE verifier, uses this too).
    static func saveData(_ data: Data, account: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound { status = SecItemAdd(query.merging(attributes) { $1 } as CFDictionary, nil) }
        SimulatorFallback.save(data, for: account, keychainStatus: status)
    }

    static func loadData(account: String) -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        return status == errSecSuccess ? result as? Data : SimulatorFallback.load(for: account, keychainStatus: status)
    }

    static func deleteData(account: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(query as CFDictionary)
        SimulatorFallback.delete(for: account)
    }

    /// A simulator build made without signing (CI, `CODE_SIGNING_ALLOWED=NO`) has no keychain access; when the keychain
    /// refuses there, the tokens go to UserDefaults so the app can still be tested. Never on a device.
    enum SimulatorFallback {
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
