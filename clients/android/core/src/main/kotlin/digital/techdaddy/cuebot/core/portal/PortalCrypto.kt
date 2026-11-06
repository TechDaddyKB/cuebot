package digital.techdaddy.cuebot.core.portal

import java.math.BigInteger
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * The mod portal's end-to-end encryption, the phone's half of src/Cuebot.Portal/Crypto and portal/src/crypto.ts
 * (docs/MOD_PORTAL_PLAN.md, section 3). Java's built-in crypto only. Formats: P-256 public keys as raw uncompressed
 * points (65 bytes), signatures as IEEE P1363 (64 bytes), frames as AES-256-GCM ciphertext || tag with a counter
 * nonce. Checked against portal/test/vectors.json.
 */
object PortalCrypto {
    const val VERSION = 1
    const val LABEL = "cuebot-portal-v1"
    const val DEVICE_PROOF_LABEL = "cuebot-portal-device-v1"
    const val PUBLIC_KEY_LENGTH = 65
    const val SIGNATURE_LENGTH = 64
    const val CLIENT_HELLO_LENGTH = 1 + PUBLIC_KEY_LENGTH
    const val SERVER_HELLO_LENGTH = 1 + 2 * PUBLIC_KEY_LENGTH + SIGNATURE_LENGTH

    private val random = SecureRandom()

    /** secp256r1, for building keys from raw points. */
    val p256: ECParameterSpec = AlgorithmParameters.getInstance("EC").run {
        init(ECGenParameterSpec("secp256r1"))
        getParameterSpec(ECParameterSpec::class.java)
    }

    fun base64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    fun fromBase64Url(text: String): ByteArray = Base64.getUrlDecoder().decode(text)

    fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)

    fun randomBytes(count: Int): ByteArray = ByteArray(count).also(random::nextBytes)

    /** base64url of SHA-256 of the raw public key: what an invite pins. */
    fun fingerprint(publicKey: ByteArray): String = base64Url(sha256(publicKey))

    /** 0x04 || x || y. */
    fun rawPublicKey(key: ECPublicKey): ByteArray = byteArrayOf(4) + fixed(key.w.affineX) + fixed(key.w.affineY)

    /** A raw P-256 point as a public key; throws when it is not a point on the curve. */
    fun publicKey(raw: ByteArray): ECPublicKey {
        if (raw.size != PUBLIC_KEY_LENGTH || raw[0] != 4.toByte()) throw PortalCryptoException("Not a raw P-256 public key.")
        val x = BigInteger(1, raw.copyOfRange(1, 33))
        val y = BigInteger(1, raw.copyOfRange(33, 65))
        if (!onCurve(x, y)) throw PortalCryptoException("Not a point on P-256.")
        return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(x, y), p256)) as ECPublicKey
    }

    /** y² = x³ - 3x + b (mod p), with both coordinates below p. */
    private fun onCurve(x: BigInteger, y: BigInteger): Boolean {
        val curve = p256.curve
        val p = (curve.field as java.security.spec.ECFieldFp).p
        if (x.signum() < 0 || y.signum() < 0 || x >= p || y >= p) return false
        val right = x.pow(3).add(curve.a.multiply(x)).add(curve.b).mod(p)
        return y.pow(2).mod(p) == right
    }

    private fun fixed(n: BigInteger): ByteArray {
        val b = n.toByteArray()
        return when {
            b.size == 32 -> b
            b.size == 33 && b[0] == 0.toByte() -> b.copyOfRange(1, 33)
            b.size < 32 -> ByteArray(32 - b.size) + b
            else -> throw PortalCryptoException("Not a P-256 coordinate.")
        }
    }

    fun newEphemeral(): KeyPair = KeyPairGenerator.getInstance("EC").run {
        initialize(ECGenParameterSpec("secp256r1"), random)
        generateKeyPair()
    }

    fun clientHello(ephemeral: KeyPair): ByteArray = byteArrayOf(VERSION.toByte()) + rawPublicKey(ephemeral.public as ECPublicKey)

    fun transcript(clientEphemeral: ByteArray, serverEphemeral: ByteArray, identity: ByteArray): ByteArray =
        LABEL.toByteArray() + clientEphemeral + serverEphemeral + identity

    fun sharedSecret(own: PrivateKey, otherPublicKey: ByteArray): ByteArray = KeyAgreement.getInstance("ECDH").run {
        init(own)
        doPhase(publicKey(otherPublicKey), true)
        generateSecret()
    }

    /** HKDF-SHA256 (RFC 5869) for one 32-byte key. */
    fun hkdf(secret: ByteArray, salt: ByteArray, info: String): ByteArray {
        val prk = hmac(salt, secret)
        return hmac(prk, info.toByteArray() + byteArrayOf(1))
    }

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").run {
        init(SecretKeySpec(key, "HmacSHA256"))
        doFinal(data)
    }

    class Keys(val c2s: ByteArray, val s2c: ByteArray, val binding: ByteArray)

    fun deriveKeys(shared: ByteArray, transcript: ByteArray): Keys {
        val salt = sha256(transcript)
        return Keys(hkdf(shared, salt, "c2s"), hkdf(shared, salt, "s2c"), salt)
    }

    /** The bytes a device signs with its own key to prove it holds it on the connection with this binding. */
    fun deviceProof(binding: ByteArray): ByteArray = DEVICE_PROOF_LABEL.toByteArray() + binding

    fun nonce(counter: Long): ByteArray = ByteArray(12).also { n -> for (i in 0 until 8) n[11 - i] = (counter ushr (8 * i)).toByte() }

    fun verify(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean {
        if (signature.size != SIGNATURE_LENGTH) return false
        return runCatching {
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(publicKey(publicKey))
                update(data)
                verify(derFromP1363(signature))
            }
        }.getOrDefault(false)
    }

    /** Signs with a software key, as Cuebot does (tests stand in for Cuebot with it). */
    fun sign(key: PrivateKey, data: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(key)
        update(data)
        p1363FromDer(sign())
    }

    /** A DER ECDSA signature (what Java and the Android Keystore make) as r || s. */
    fun p1363FromDer(der: ByteArray): ByteArray {
        var at = 0
        fun next(): Int = der[at++].toInt() and 0xff
        fun length(): Int {
            val first = next()
            if (first < 0x80) return first
            var n = 0
            repeat(first and 0x7f) { n = (n shl 8) or next() }
            return n
        }
        if (next() != 0x30) throw PortalCryptoException("Not a DER signature.")
        length()
        fun integer(): ByteArray {
            if (next() != 0x02) throw PortalCryptoException("Not a DER signature.")
            val n = length()
            val value = der.copyOfRange(at, at + n)
            at += n
            return fixed(BigInteger(1, value))
        }
        return integer() + integer()
    }

    fun derFromP1363(signature: ByteArray): ByteArray {
        fun integer(part: ByteArray): ByteArray {
            val trimmed = part.dropWhile { it == 0.toByte() }.toByteArray()
            val value = if (trimmed.isEmpty() || trimmed[0] < 0) byteArrayOf(0) + trimmed else trimmed
            return byteArrayOf(2, value.size.toByte()) + value
        }
        val body = integer(signature.copyOfRange(0, 32)) + integer(signature.copyOfRange(32, 64))
        return byteArrayOf(0x30, body.size.toByte()) + body
    }

    /** A device's side: check the server hello against the pinned fingerprint and derive the session. */
    fun finishHandshake(ephemeral: KeyPair, serverHello: ByteArray, pinned: String): PortalSession {
        if (serverHello.size != SERVER_HELLO_LENGTH || serverHello[0] != VERSION.toByte()) throw PortalCryptoException("Not a portal server hello.")
        val serverEphemeral = serverHello.copyOfRange(1, 1 + PUBLIC_KEY_LENGTH)
        val identity = serverHello.copyOfRange(1 + PUBLIC_KEY_LENGTH, 1 + 2 * PUBLIC_KEY_LENGTH)
        val signature = serverHello.copyOfRange(1 + 2 * PUBLIC_KEY_LENGTH, SERVER_HELLO_LENGTH)
        if (fingerprint(identity) != pinned) throw PortalCryptoException("This is not the Cuebot the invite was for.")
        val t = transcript(rawPublicKey(ephemeral.public as ECPublicKey), serverEphemeral, identity)
        if (!verify(identity, t, signature)) throw PortalCryptoException("The handshake signature is wrong.")
        val keys = deriveKeys(sharedSecret(ephemeral.private, serverEphemeral), t)
        return PortalSession(keys.c2s, keys.s2c, keys.binding)
    }
}

class PortalCryptoException(message: String) : Exception(message)

/** One encrypted connection. Frames must arrive in order; any failure breaks the session for good. */
class PortalSession(private val sendKey: ByteArray, private val receiveKey: ByteArray, val binding: ByteArray) {
    private var sent = 0L
    private var received = 0L
    private var broken = false

    @Synchronized
    fun encrypt(plaintext: ByteArray): ByteArray = gcm(Cipher.ENCRYPT_MODE, sendKey, sent++).doFinal(plaintext)

    @Synchronized
    fun decrypt(frame: ByteArray): ByteArray {
        if (broken) throw PortalCryptoException("The connection already failed.")
        return try {
            gcm(Cipher.DECRYPT_MODE, receiveKey, received).doFinal(frame).also { received++ }
        } catch (e: Exception) {
            broken = true
            throw PortalCryptoException("A frame failed to decrypt.")
        }
    }

    private fun gcm(mode: Int, key: ByteArray, counter: Long): Cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
        init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(128, PortalCrypto.nonce(counter)))
    }
}

/**
 * The key a device proves itself with on every connection (docs/MOD_PORTAL_PLAN.md, section 4). In the app it lives
 * in the Android Keystore and cannot be read out; tests use [SoftwareDeviceKey].
 */
interface DeviceKey {
    /** The raw P-256 public key (65 bytes). */
    val publicKey: ByteArray

    /** An ECDSA P-256 SHA-256 signature over [data], as r || s. */
    fun sign(data: ByteArray): ByteArray
}

class SoftwareDeviceKey(private val pair: KeyPair = PortalCrypto.newEphemeral()) : DeviceKey {
    override val publicKey: ByteArray = PortalCrypto.rawPublicKey(pair.public as ECPublicKey)
    override fun sign(data: ByteArray): ByteArray = PortalCrypto.sign(pair.private, data)
}
