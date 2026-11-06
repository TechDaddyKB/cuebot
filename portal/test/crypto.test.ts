import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  type Bytes,
  PortalCryptoError,
  PortalSession,
  base32,
  base64Url,
  clientHello,
  concat,
  deriveKeys,
  finishHandshake,
  fingerprint,
  fromBase64Url,
  newEphemeral,
  rawPublicKey,
  relayId,
  seal,
  sharedSecret,
  transcript,
  verify,
} from "../src/crypto.js";

// test/vectors.json is shared with tests/Cuebot.Portal.Tests. WRITE_VECTORS=1 makes new keys and rewrites it (the
// .NET signature is then added by running the .NET tests with CUEBOT_WRITE_PORTAL_VECTORS=1).
const vectorsPath = join(dirname(fileURLToPath(import.meta.url)), "vectors.json");
const subtle = globalThis.crypto.subtle;
const text = new TextEncoder();

interface Jwk { d: string; x: string; y: string }

const ECDH = { name: "ECDH", namedCurve: "P-256" } as const;
const ECDSA = { name: "ECDSA", namedCurve: "P-256" } as const;

async function pair(jwk: Jwk, algorithm: typeof ECDH | typeof ECDSA): Promise<CryptoKeyPair> {
  const ecdh = algorithm.name === "ECDH";
  const privateKey = await subtle.importKey("jwk", { kty: "EC", crv: "P-256", ...jwk }, algorithm, true, ecdh ? ["deriveBits"] : ["sign"]);
  const publicKey = await subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y }, algorithm, true, ecdh ? [] : ["verify"]);
  return { privateKey, publicKey };
}

async function newJwk(): Promise<Jwk> {
  const k = (await subtle.generateKey(ECDH, true, ["deriveBits"])) as CryptoKeyPair;
  const j = await subtle.exportKey("jwk", k.privateKey);
  return { d: j.d!, x: j.x!, y: j.y! };
}

async function rawKey(jwk: Jwk): Promise<Bytes> {
  return rawPublicKey((await pair(jwk, ECDH)).publicKey);
}

async function sign(jwk: Jwk, data: Bytes): Promise<Bytes> {
  const k = await pair(jwk, ECDSA);
  return new Uint8Array(await subtle.sign({ name: "ECDSA", hash: "SHA-256" }, k.privateKey, data));
}

async function exportAes(key: CryptoKey): Promise<string> {
  return base64Url(new Uint8Array(await subtle.exportKey("raw", key)));
}

const FRAMES = [
  { from: "client", text: "hello from the mod" },
  { from: "client", text: "" },
  { from: "server", text: "{\"ok\":true}" },
  { from: "client", text: "third frame, counter 2" },
] as const;

async function build(): Promise<Record<string, unknown>> {
  const identity = await newJwk();
  const clientEphemeral = await newJwk();
  const serverEphemeral = await newJwk();
  const recipient = await newJwk();
  const sealEphemeral = await newJwk();
  const identityPublic = await rawKey(identity);
  const clientPublic = await rawKey(clientEphemeral);
  const serverPublic = await rawKey(serverEphemeral);
  const t = transcript(clientPublic, serverPublic, identityPublic);
  const shared = await sharedSecret((await pair(clientEphemeral, ECDH)).privateKey, serverPublic);
  const keys = await deriveKeys(shared, t);
  const client = new PortalSession(keys.c2s, keys.s2c, keys.binding);
  const server = new PortalSession(keys.s2c, keys.c2s, keys.binding);
  const frames = [];
  for (const f of FRAMES) frames.push({ ...f, frame: base64Url(await (f.from === "client" ? client : server).encrypt(text.encode(f.text))) });
  const sealPlain = "POST /hook/kofi {\"amount\":\"5.00\"}";
  return {
    identity,
    identityPublic: base64Url(identityPublic),
    relayId: await relayId(identityPublic),
    fingerprint: await fingerprint(identityPublic),
    clientEphemeral,
    serverEphemeral,
    clientHello: base64Url(concat(new Uint8Array([1]), clientPublic)),
    transcript: base64Url(t),
    shared: base64Url(shared),
    c2s: await exportAes(keys.c2s),
    s2c: await exportAes(keys.s2c),
    frames,
    jsSignature: base64Url(await sign(identity, t)),
    dotnetSignature: "",
    seal: {
      recipient,
      ephemeral: sealEphemeral,
      plaintext: sealPlain,
      sealed: base64Url(await seal(await rawKey(recipient), text.encode(sealPlain), await pair(sealEphemeral, ECDH))),
    },
  };
}

if (process.env.WRITE_VECTORS === "1" || !existsSync(vectorsPath)) {
  writeFileSync(vectorsPath, JSON.stringify(await build(), null, 2) + "\n");
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const v = JSON.parse(readFileSync(vectorsPath, "utf8")) as any;

function serverHello(signature: Bytes, serverPublic: Bytes, identityPublic: Bytes): Bytes {
  return concat(new Uint8Array([1]), serverPublic, identityPublic, signature);
}

describe("encodings", () => {
  it("base32 matches RFC 4648 (lowercase, no padding)", () => {
    const cases: Record<string, string> = { "": "", f: "my", fo: "mzxq", foo: "mzxw6", foob: "mzxw6yq", fooba: "mzxw6ytb", foobar: "mzxw6ytboi" };
    for (const [input, output] of Object.entries(cases)) expect(base32(text.encode(input))).toBe(output);
  });

  it("base64url round-trips", () => {
    const bytes = Uint8Array.from({ length: 257 }, (_, i) => i % 256);
    expect(fromBase64Url(base64Url(bytes))).toEqual(bytes);
  });

  it("relay ID and fingerprint come from the identity key", async () => {
    const pub = fromBase64Url(v.identityPublic);
    expect(await relayId(pub)).toBe(v.relayId);
    expect(v.relayId).toMatch(/^[a-z2-7]{32}$/);
    expect(await fingerprint(pub)).toBe(v.fingerprint);
  });
});

describe("handshake", () => {
  it("derives the shared keys of the vectors", async () => {
    const serverPublic = await rawKey(v.serverEphemeral);
    const t = transcript(await rawKey(v.clientEphemeral), serverPublic, fromBase64Url(v.identityPublic));
    expect(base64Url(t)).toBe(v.transcript);
    const shared = await sharedSecret((await pair(v.clientEphemeral, ECDH)).privateKey, serverPublic);
    expect(base64Url(shared)).toBe(v.shared);
    const keys = await deriveKeys(shared, t);
    expect(await exportAes(keys.c2s)).toBe(v.c2s);
    expect(await exportAes(keys.s2c)).toBe(v.s2c);
  });

  it("the client hello is version 1 and the raw ephemeral key", async () => {
    expect(base64Url(await clientHello(await pair(v.clientEphemeral, ECDH)))).toBe(v.clientHello);
  });

  it("verifies the signatures made by both implementations", async () => {
    const pub = fromBase64Url(v.identityPublic);
    const t = fromBase64Url(v.transcript);
    expect(await verify(pub, t, fromBase64Url(v.jsSignature))).toBe(true);
    if (v.dotnetSignature) expect(await verify(pub, t, fromBase64Url(v.dotnetSignature))).toBe(true);
    const tampered = fromBase64Url(v.jsSignature);
    tampered[10] = tampered[10]! ^ 1;
    expect(await verify(pub, t, tampered)).toBe(false);
  });

  it("finishes against a server hello signed by .NET and decrypts the vector frames", async () => {
    const signature = fromBase64Url(v.dotnetSignature || v.jsSignature);
    const hello = serverHello(signature, await rawKey(v.serverEphemeral), fromBase64Url(v.identityPublic));
    const session = await finishHandshake(await pair(v.clientEphemeral, ECDH), hello, v.fingerprint);
    // The client session only decrypts server frames; check them and encrypt the client frames afresh.
    let clientIndex = 0;
    for (const f of v.frames) {
      if (f.from === "server") expect(new TextDecoder().decode(await session.decrypt(fromBase64Url(f.frame)))).toBe(f.text);
      else {
        expect(base64Url(await session.encrypt(text.encode(f.text)))).toBe(f.frame);
        clientIndex++;
      }
    }
    expect(clientIndex).toBe(3);
  });

  it("refuses a server hello from another identity", async () => {
    const other = await newJwk();
    const otherPublic = await rawKey(other);
    const hello = serverHello(await sign(other, fromBase64Url(v.transcript)), await rawKey(v.serverEphemeral), otherPublic);
    await expect(finishHandshake(await pair(v.clientEphemeral, ECDH), hello, v.fingerprint)).rejects.toThrow(PortalCryptoError);
  });

  it("refuses a bad signature", async () => {
    const signature = fromBase64Url(v.jsSignature);
    signature[0] = signature[0]! ^ 0x80;
    const hello = serverHello(signature, await rawKey(v.serverEphemeral), fromBase64Url(v.identityPublic));
    await expect(finishHandshake(await pair(v.clientEphemeral, ECDH), hello, v.fingerprint)).rejects.toThrow(PortalCryptoError);
  });
});

describe("session", () => {
  async function sessions(): Promise<[PortalSession, PortalSession]> {
    const keys = await deriveKeys(fromBase64Url(v.shared), fromBase64Url(v.transcript));
    return [new PortalSession(keys.c2s, keys.s2c, keys.binding), new PortalSession(keys.s2c, keys.c2s, keys.binding)];
  }

  it("matches the vector frames byte for byte", async () => {
    const [client, server] = await sessions();
    for (const f of v.frames) {
      const sender = f.from === "client" ? client : server;
      expect(base64Url(await sender.encrypt(text.encode(f.text)))).toBe(f.frame);
    }
  });

  it("breaks on a replayed or reordered frame", async () => {
    const [client, server] = await sessions();
    const first = await client.encrypt(text.encode("one"));
    const second = await client.encrypt(text.encode("two"));
    await expect(server.decrypt(second)).rejects.toThrow(PortalCryptoError);
    await expect(server.decrypt(first)).rejects.toThrow(PortalCryptoError);
  });

  it("breaks on a changed frame", async () => {
    const [client, server] = await sessions();
    const frame = await client.encrypt(text.encode("change me"));
    frame[0] = frame[0]! ^ 1;
    await expect(server.decrypt(frame)).rejects.toThrow(PortalCryptoError);
  });

  it("round-trips with fresh keys", async () => {
    const a = await newEphemeral();
    const b = await newEphemeral();
    const ap = await rawPublicKey(a.publicKey);
    const bp = await rawPublicKey(b.publicKey);
    expect(await sharedSecret(a.privateKey, bp)).toEqual(await sharedSecret(b.privateKey, ap));
  });
});

describe("seal", () => {
  it("matches the vector", async () => {
    const sealed = await seal(await rawKey(v.seal.recipient), text.encode(v.seal.plaintext), await pair(v.seal.ephemeral, ECDH));
    expect(base64Url(sealed)).toBe(v.seal.sealed);
  });
});
