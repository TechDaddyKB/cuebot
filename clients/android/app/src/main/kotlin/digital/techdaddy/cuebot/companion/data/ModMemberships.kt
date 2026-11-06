package digital.techdaddy.cuebot.companion.data

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import digital.techdaddy.cuebot.core.portal.DeviceKey
import digital.techdaddy.cuebot.core.portal.PendingSignIn
import digital.techdaddy.cuebot.core.portal.PortalCrypto
import digital.techdaddy.cuebot.core.portal.PortalTarget
import digital.techdaddy.cuebot.core.portal.SoftwareDeviceKey
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec

/**
 * One streamer this phone is a mod for: where their Cuebot is, the identity it pinned at sign-in, and the alias of this
 * device's key for that membership. Like a browser's saved device (portal/web/main.ts), but the key is in the Keystore.
 */
@Serializable
data class Membership(
    val target: PortalTarget,
    /** What the mod calls this streamer here (Cuebot does not say whose it is); they can rename it. */
    val name: String = DEFAULT_NAME,
    /** The mod's own name, as Cuebot knows them. */
    val member: String,
    val role: String,
    val keyAlias: String,
    val at: Long,
) {
    val relay: String get() = target.relay

    companion object {
        const val DEFAULT_NAME = "Mod portal"
    }
}

/** This device's portal keys, one per membership. */
interface DeviceKeys {
    fun create(alias: String): DeviceKey

    /** The key under [alias], or null when it is gone (the app's data was cleared, or the Keystore lost it). */
    fun open(alias: String): DeviceKey?

    fun delete(alias: String)
}

/** ECDSA P-256 keys in the Android Keystore: the app can sign with them, nothing can read them out. */
class KeystoreDeviceKeys : DeviceKeys {
    private val keyStore: KeyStore by lazy { KeyStore.getInstance(KEYSTORE).apply { load(null) } }

    override fun create(alias: String): DeviceKey {
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE).run {
            initialize(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build(),
            )
            generateKeyPair()
        }
        return open(alias) ?: error("The new key is missing from the Keystore.")
    }

    override fun open(alias: String): DeviceKey? {
        val entry = runCatching { keyStore.getEntry(alias, null) as? KeyStore.PrivateKeyEntry }.getOrNull() ?: return null
        return KeystoreKey(entry.privateKey, PortalCrypto.rawPublicKey(entry.certificate.publicKey as ECPublicKey))
    }

    override fun delete(alias: String) {
        runCatching { keyStore.deleteEntry(alias) }
    }

    private class KeystoreKey(private val key: PrivateKey, override val publicKey: ByteArray) : DeviceKey {
        // The Keystore signs in DER; Cuebot checks r || s, as WebCrypto makes it.
        override fun sign(data: ByteArray): ByteArray = PortalCrypto.p1363FromDer(Signature.getInstance("SHA256withECDSA").run {
            initSign(key)
            update(data)
            sign()
        })
    }

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
    }
}

/** Software keys in memory: for tests. */
class MemoryDeviceKeys : DeviceKeys {
    val keys = mutableMapOf<String, DeviceKey>()
    override fun create(alias: String): DeviceKey = SoftwareDeviceKey().also { keys[alias] = it }
    override fun open(alias: String): DeviceKey? = keys[alias]
    override fun delete(alias: String) { keys.remove(alias) }
}

/**
 * The streamers this phone mods for, and the sign-in waiting for the browser to come back. The pending sign-in holds
 * the PKCE verifier, so it is sealed like the paired Cuebots' tokens; it survives the app being closed while the
 * browser is open.
 */
class ModMemberships(private val store: KeyValueStore, private val cipher: TokenCipher, val keys: DeviceKeys) {
    private val json = Json { ignoreUnknownKeys = true }
    private val _all = MutableStateFlow(
        store[LIST_KEY]?.let { runCatching { json.decodeFromString<List<Membership>>(it) }.getOrNull() } ?: emptyList(),
    )
    /** The newest first. */
    val all: StateFlow<List<Membership>> = _all

    fun find(relay: String): Membership? = _all.value.firstOrNull { it.relay == relay }

    /** Adds or replaces the membership for its relay; a replaced one's key is deleted when it is not reused. */
    fun save(membership: Membership) {
        val old = find(membership.relay)
        if (old != null && old.keyAlias != membership.keyAlias) keys.delete(old.keyAlias)
        _all.update { list -> (listOf(membership) + list.filter { it.relay != membership.relay }).sortedByDescending { it.at } }
        store.put(LIST_KEY, json.encodeToString(_all.value))
    }

    fun rename(relay: String, name: String) {
        val trimmed = name.trim()
        val m = find(relay) ?: return
        if (trimmed.isEmpty()) return
        _all.update { list -> list.map { if (it.relay == relay) m.copy(name = trimmed) else it } }
        store.put(LIST_KEY, json.encodeToString(_all.value))
    }

    /** Forgets a membership and deletes its key. */
    fun forget(relay: String) {
        find(relay)?.let { keys.delete(it.keyAlias) }
        _all.update { list -> list.filter { it.relay != relay } }
        store.put(LIST_KEY, json.encodeToString(_all.value))
    }

    var pending: PendingSignIn?
        get() = store[PENDING_KEY]?.let(cipher::open)?.let { runCatching { json.decodeFromString<PendingSignIn>(it) }.getOrNull() }
        set(value) = store.put(PENDING_KEY, value?.let { cipher.seal(json.encodeToString(it)) })

    private companion object {
        const val LIST_KEY = "modMemberships"
        const val PENDING_KEY = "modPendingSignIn"
    }
}
