// Shared helpers of the Worker tests: a Cuebot identity key, sockets, the connect handshake and opening sealed webhooks.
import { SELF } from "cloudflare:test";
import { expect } from "vitest";
import { type Bytes, base64Url, concat, fromBase64Url, rawPublicKey, relayId } from "../../src/crypto.js";
import type { Delivery } from "../../worker/index";
import { CONNECT_LABEL, LIMITS } from "../../worker/protocol.js";

export const RELAY = "https://relay.techdaddydigital.com";
export const HOOKS = "https://hooks.techdaddydigital.com";
export const ROUTE_SECRET = "route-secret-0123456789abcdef";
export const subtle = crypto.subtle;
export const encoder = new TextEncoder();
export const text = (s: string) => encoder.encode(s) as Bytes;

export interface Identity {
	sign: CryptoKey;
	ecdh: CryptoKey;
	raw: Bytes;
	id: string;
}

/** A Cuebot identity key, usable for signing (connect) and ECDH (opening sealed webhooks), like IdentityKey in .NET. */
export async function identity(): Promise<Identity> {
	const pair = (await subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
	const jwk = (await subtle.exportKey("jwk", pair.privateKey)) as JsonWebKey;
	const ecdh = await subtle.importKey("jwk", { ...jwk, key_ops: undefined }, { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
	const raw = await rawPublicKey(pair.publicKey);
	return { sign: pair.privateKey, ecdh, raw, id: await relayId(raw) };
}

export class Socket {
	private readonly queue: (string | ArrayBuffer)[] = [];
	private readonly waiters: ((m: string | ArrayBuffer) => void)[] = [];
	readonly closed: Promise<CloseEvent>;

	constructor(readonly ws: WebSocket) {
		ws.addEventListener("message", (e) => {
			const w = this.waiters.shift();
			if (w) w(e.data as string | ArrayBuffer);
			else this.queue.push(e.data as string | ArrayBuffer);
		});
		this.closed = new Promise((resolve) => ws.addEventListener("close", resolve));
		ws.accept();
	}

	next(): Promise<string | ArrayBuffer> {
		const m = this.queue.shift();
		if (m !== undefined) return Promise.resolve(m);
		return new Promise((resolve, reject) => {
			this.waiters.push(resolve);
			setTimeout(() => reject(new Error("timed out waiting for a message")), 3000);
		});
	}

	async json(): Promise<any> {
		const m = await this.next();
		expect(typeof m).toBe("string");
		return JSON.parse(m as string);
	}

	/** The next binary message (the test runtime hands them over as Blobs). */
	async bytes(): Promise<ArrayBuffer> {
		const m: unknown = await this.next();
		expect(typeof m).not.toBe("string");
		return m instanceof Blob ? m.arrayBuffer() : (m as ArrayBuffer);
	}

	async binary(): Promise<{ ch: number; payload: Bytes }> {
		const b = await this.bytes();
		return { ch: new DataView(b).getUint32(0), payload: new Uint8Array(b.slice(4)) };
	}

	/** Nothing arrives within a short wait. */
	async silent(): Promise<void> {
		await new Promise((r) => setTimeout(r, 150));
		expect(this.queue).toHaveLength(0);
	}
}

export async function upgrade(url: string): Promise<Socket> {
	const res = await SELF.fetch(url, { headers: { Upgrade: "websocket" } });
	expect(res.status).toBe(101);
	return new Socket(res.webSocket!);
}

export async function connectCuebot(me: Identity, opts: { hooks?: string; signer?: Identity } = {}): Promise<{ socket: Socket; first: any }> {
	const socket = await upgrade(`${RELAY}/connect?id=${me.id}`);
	const challenge = await socket.json();
	expect(challenge).toMatchObject({ type: "challenge", version: 1 });
	const signed = concat(text(CONNECT_LABEL), fromBase64Url(challenge.nonce), text("relay.techdaddydigital.com"));
	const signer = opts.signer ?? me;
	const signature = new Uint8Array(await subtle.sign({ name: "ECDSA", hash: "SHA-256" }, signer.sign, signed)) as Bytes;
	const hooks = new Uint8Array(await subtle.digest("SHA-256", text(opts.hooks ?? ROUTE_SECRET))) as Bytes;
	socket.ws.send(JSON.stringify({ type: "auth", key: base64Url(me.raw), signature: base64Url(signature), hooks: base64Url(hooks) }));
	const first = await Promise.race([socket.json(), socket.closed]);
	return { socket, first };
}

export async function onlineCuebot(): Promise<{ me: Identity; cuebot: Socket }> {
	const me = await identity();
	const { socket, first } = await connectCuebot(me);
	expect(first).toMatchObject({ type: "ready", version: 1, maxDevices: LIMITS.maxDevices });
	return { me, cuebot: socket };
}

export function frame(ch: number, payload: Bytes): ArrayBuffer {
	const out = new Uint8Array(4 + payload.length);
	new DataView(out.buffer).setUint32(0, ch);
	out.set(payload, 4);
	return out.buffer;
}

/** The receiving half of PortalSeal, as Cuebot does it. */
export async function open(me: Identity, sealed: Bytes): Promise<Delivery> {
	expect(sealed[0]).toBe(1);
	const eph = sealed.slice(1, 66);
	const other = await subtle.importKey("raw", eph, { name: "ECDH", namedCurve: "P-256" }, false, []);
	const params = { name: "ECDH", public: other } as unknown as Parameters<typeof subtle.deriveBits>[0];
	const shared = await subtle.deriveBits(params, me.ecdh, 256);
	const salt = await subtle.digest("SHA-256", concat(text("cuebot-seal-v1"), eph, me.raw));
	const base = await subtle.importKey("raw", shared, "HKDF", false, ["deriveKey"]);
	const key = await subtle.deriveKey({ name: "HKDF", hash: "SHA-256", salt, info: text("seal") }, base, { name: "AES-GCM", length: 256 }, false, ["decrypt"]);
	const plain = await subtle.decrypt({ name: "AES-GCM", iv: new Uint8Array(12), tagLength: 128 }, key, sealed.slice(66));
	return JSON.parse(new TextDecoder().decode(plain));
}
