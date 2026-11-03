package digital.techdaddy.cuebot.companion.data

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Seals the paired Cuebots' tokens before they are stored. */
interface TokenCipher {
    fun seal(plain: String): String

    /** The plain text, or null when [sealed] cannot be opened (a different key, or damaged). */
    fun open(sealed: String): String?
}

/** No sealing: for tests. */
object PlainTokenCipher : TokenCipher {
    override fun seal(plain: String) = plain
    override fun open(sealed: String) = sealed
}

/**
 * AES-256-GCM with a key that lives in the Android Keystore and never leaves it. Sealed text is
 * base64(IV ‖ ciphertext ‖ tag).
 */
class KeystoreTokenCipher(private val alias: String = "cuebot-companion-tokens") : TokenCipher {
    private val key: SecretKey by lazy {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (keyStore.getKey(alias, null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE).run {
            init(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
            generateKey()
        }
    }

    override fun seal(plain: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(plain.toByteArray()), Base64.NO_WRAP)
    }

    override fun open(sealed: String): String? = runCatching {
        val bytes = Base64.decode(sealed, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, bytes, 0, IV_LENGTH))
        }
        String(cipher.doFinal(bytes, IV_LENGTH, bytes.size - IV_LENGTH))
    }.getOrNull()

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_LENGTH = 12
    }
}
