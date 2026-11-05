// The portal's end-to-end encryption, the browser and Worker half of src/Cuebot.Portal/Crypto (docs/MOD_PORTAL_PLAN.md,
// section 3). WebCrypto only. Formats: P-256 public keys as raw uncompressed points (65 bytes), signatures as IEEE
// P1363 (64 bytes), frames as AES-256-GCM ciphertext || tag with a counter nonce. Both sides are checked against
// test/vectors.json.

/** Bytes on an ordinary (not shared) buffer, which is what WebCrypto accepts. */
export type Bytes = Uint8Array<ArrayBuffer>;

export const VERSION = 1;
export const LABEL = "cuebot-portal-v1";
export const SEAL_LABEL = "cuebot-seal-v1";
export const PUBLIC_KEY_LENGTH = 65;
export const SIGNATURE_LENGTH = 64;
export const TAG_LENGTH = 16;
export const CLIENT_HELLO_LENGTH = 1 + PUBLIC_KEY_LENGTH;
export const SERVER_HELLO_LENGTH = 1 + 2 * PUBLIC_KEY_LENGTH + SIGNATURE_LENGTH;

const subtle = crypto.subtle;
const utf8 = new TextEncoder();
const ECDH = { name: "ECDH", namedCurve: "P-256" } as const;
const ECDSA = { name: "ECDSA", namedCurve: "P-256" } as const;

export class PortalCryptoError extends Error {}

export function concat(...parts: Uint8Array[]): Bytes {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

export function base64Url(bytes: Bytes): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

export function fromBase64Url(s: string): Bytes {
  const b = s.replace(/-/g, "+").replace(/_/g, "/");
  const bin = atob(b + "=".repeat((4 - (b.length % 4)) % 4));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

const BASE32 = "abcdefghijklmnopqrstuvwxyz234567";

export function base32(bytes: Bytes): string {
  let out = "";
  let buffer = 0;
  let bits = 0;
  for (const b of bytes) {
    buffer = ((buffer << 8) | b) & 0xffff;
    bits += 8;
    while (bits >= 5) {
      out += BASE32[(buffer >> (bits - 5)) & 31];
      bits -= 5;
    }
  }
  if (bits > 0) out += BASE32[(buffer << (5 - bits)) & 31];
  return out;
}

async function sha256(data: Bytes): Promise<Bytes> {
  return new Uint8Array(await subtle.digest("SHA-256", data));
}

/** Lowercase base32 of the first 20 bytes of SHA-256 of the raw public key. */
export async function relayId(publicKey: Bytes): Promise<string> {
  return base32((await sha256(publicKey)).slice(0, 20));
}

/** base64url of SHA-256 of the raw public key: what an invite pins. */
export async function fingerprint(publicKey: Bytes): Promise<string> {
  return base64Url(await sha256(publicKey));
}

export async function rawPublicKey(key: CryptoKey): Promise<Bytes> {
  return new Uint8Array((await subtle.exportKey("raw", key)) as ArrayBuffer);
}

function checkRaw(raw: Bytes): void {
  if (raw.length !== PUBLIC_KEY_LENGTH || raw[0] !== 0x04) throw new PortalCryptoError("Not a raw P-256 public key.");
}

async function importEcdhPublic(raw: Bytes): Promise<CryptoKey> {
  checkRaw(raw);
  return subtle.importKey("raw", raw, ECDH, true, []);
}

export async function verify(publicKey: Bytes, data: Bytes, signature: Bytes): Promise<boolean> {
  if (signature.length !== SIGNATURE_LENGTH) return false;
  try {
    checkRaw(publicKey);
    const key = await subtle.importKey("raw", publicKey, ECDSA, false, ["verify"]);
    return await subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, signature, data);
  } catch {
    return false;
  }
}

export async function sharedSecret(own: CryptoKey, otherPublicKey: Bytes): Promise<Bytes> {
  const other = await importEcdhPublic(otherPublicKey);
  // Cloudflare's typings name the field "$public"; the runtime, like every browser, takes "public".
  const params = { name: "ECDH", public: other } as unknown as Parameters<typeof subtle.deriveBits>[0];
  return new Uint8Array(await subtle.deriveBits(params, own, 256));
}

async function hkdf(secret: Bytes, salt: Bytes, info: string): Promise<CryptoKey> {
  const base = await subtle.importKey("raw", secret, "HKDF", false, ["deriveKey"]);
  return subtle.deriveKey(
    { name: "HKDF", hash: "SHA-256", salt, info: utf8.encode(info) },
    base,
    { name: "AES-GCM", length: 256 },
    true,
    ["encrypt", "decrypt"],
  );
}

export function transcript(clientEphemeral: Bytes, serverEphemeral: Bytes, identity: Bytes): Bytes {
  return concat(utf8.encode(LABEL), clientEphemeral, serverEphemeral, identity);
}

export async function deriveKeys(shared: Bytes, transcriptBytes: Bytes): Promise<{ c2s: CryptoKey; s2c: CryptoKey }> {
  const salt = await sha256(transcriptBytes);
  return { c2s: await hkdf(shared, salt, "c2s"), s2c: await hkdf(shared, salt, "s2c") };
}

export function nonce(counter: number): Bytes {
  const n = new Uint8Array(12);
  new DataView(n.buffer).setBigUint64(4, BigInt(counter));
  return n;
}

/** One encrypted connection. Frames must arrive in order; any failure breaks the session for good. */
export class PortalSession {
  private sent = 0;
  private received = 0;
  private broken = false;

  constructor(private readonly sendKey: CryptoKey, private readonly receiveKey: CryptoKey) {}

  async encrypt(plaintext: Bytes): Promise<Bytes> {
    const n = nonce(this.sent++);
    return new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: n, tagLength: 128 }, this.sendKey, plaintext));
  }

  async decrypt(frame: Bytes): Promise<Bytes> {
    if (this.broken) throw new PortalCryptoError("The connection already failed.");
    try {
      const plain = await subtle.decrypt({ name: "AES-GCM", iv: nonce(this.received), tagLength: 128 }, this.receiveKey, frame);
      this.received++;
      return new Uint8Array(plain);
    } catch {
      this.broken = true;
      throw new PortalCryptoError("A frame failed to decrypt.");
    }
  }
}

export async function newEphemeral(): Promise<CryptoKeyPair> {
  return (await subtle.generateKey(ECDH, true, ["deriveBits"])) as CryptoKeyPair;
}

export async function clientHello(ephemeral: CryptoKeyPair): Promise<Bytes> {
  return concat(new Uint8Array([VERSION]), await rawPublicKey(ephemeral.publicKey));
}

/** A device's side: check the server hello against the pinned fingerprint and derive the session. */
export async function finishHandshake(ephemeral: CryptoKeyPair, serverHello: Bytes, pinned: string): Promise<PortalSession> {
  if (serverHello.length !== SERVER_HELLO_LENGTH || serverHello[0] !== VERSION) throw new PortalCryptoError("Not a portal server hello.");
  const serverEphemeral = serverHello.slice(1, 1 + PUBLIC_KEY_LENGTH);
  const identity = serverHello.slice(1 + PUBLIC_KEY_LENGTH, 1 + 2 * PUBLIC_KEY_LENGTH);
  const signature = serverHello.slice(1 + 2 * PUBLIC_KEY_LENGTH);
  if ((await fingerprint(identity)) !== pinned) throw new PortalCryptoError("This is not the Cuebot the invite was for.");
  const t = transcript(await rawPublicKey(ephemeral.publicKey), serverEphemeral, identity);
  if (!(await verify(identity, t, signature))) throw new PortalCryptoError("The handshake signature is wrong.");
  const keys = await deriveKeys(await sharedSecret(ephemeral.privateKey, serverEphemeral), t);
  return new PortalSession(keys.c2s, keys.s2c);
}

async function sealKey(shared: Bytes, ephemeral: Bytes, recipient: Bytes): Promise<CryptoKey> {
  return hkdf(shared, await sha256(concat(utf8.encode(SEAL_LABEL), ephemeral, recipient)), "seal");
}

/** Seal a message to a raw ECDH public key (the relay sealing a webhook to Cuebot). */
export async function seal(recipient: Bytes, plaintext: Bytes, ephemeral?: CryptoKeyPair): Promise<Bytes> {
  const eph = ephemeral ?? (await newEphemeral());
  const ephPublic = await rawPublicKey(eph.publicKey);
  const key = await sealKey(await sharedSecret(eph.privateKey, recipient), ephPublic, recipient);
  const body = new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: new Uint8Array(12), tagLength: 128 }, key, plaintext));
  return concat(new Uint8Array([VERSION]), ephPublic, body);
}
