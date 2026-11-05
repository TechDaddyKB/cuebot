import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { type Bytes, base64Url, concat, fromBase64Url, rawPublicKey, relayId } from "../../src/crypto.js";
import type { Delivery } from "../../worker/index";
import { CLOSE, CONNECT_LABEL, HOOK_CHANNEL, LIMITS } from "../../worker/protocol.js";

const RELAY = "https://relay.techdaddydigital.com";
const HOOKS = "https://hooks.techdaddydigital.com";
const ROUTE_SECRET = "route-secret-0123456789abcdef";
const subtle = crypto.subtle;
const encoder = new TextEncoder();
const text = (s: string) => encoder.encode(s) as Bytes;

interface Identity {
	sign: CryptoKey;
	ecdh: CryptoKey;
	raw: Bytes;
	id: string;
}

/** A Cuebot identity key, usable for signing (connect) and ECDH (opening sealed webhooks), like IdentityKey in .NET. */
async function identity(): Promise<Identity> {
	const pair = (await subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
	const jwk = (await subtle.exportKey("jwk", pair.privateKey)) as JsonWebKey;
	const ecdh = await subtle.importKey("jwk", { ...jwk, key_ops: undefined }, { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
	const raw = await rawPublicKey(pair.publicKey);
	return { sign: pair.privateKey, ecdh, raw, id: await relayId(raw) };
}

class Socket {
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

async function upgrade(url: string): Promise<Socket> {
	const res = await SELF.fetch(url, { headers: { Upgrade: "websocket" } });
	expect(res.status).toBe(101);
	return new Socket(res.webSocket!);
}

async function connectCuebot(me: Identity, opts: { hooks?: string; signer?: Identity } = {}): Promise<{ socket: Socket; first: any }> {
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

async function onlineCuebot(): Promise<{ me: Identity; cuebot: Socket }> {
	const me = await identity();
	const { socket, first } = await connectCuebot(me);
	expect(first).toMatchObject({ type: "ready", version: 1, maxDevices: LIMITS.maxDevices });
	return { me, cuebot: socket };
}

function frame(ch: number, payload: Bytes): ArrayBuffer {
	const out = new Uint8Array(4 + payload.length);
	new DataView(out.buffer).setUint32(0, ch);
	out.set(payload, 4);
	return out.buffer;
}

/** The receiving half of PortalSeal, as Cuebot does it. */
async function open(me: Identity, sealed: Bytes): Promise<Delivery> {
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

describe("portal pages", () => {
	it("serves the loader placeholder and the return page with a strict policy", async () => {
		for (const path of ["/", "/auth/return"]) {
			const res = await SELF.fetch(`https://portal.techdaddydigital.com${path}`);
			expect(res.status).toBe(200);
			expect(res.headers.get("content-security-policy")).toContain("default-src 'none'");
			expect(res.headers.get("referrer-policy")).toBe("no-referrer");
			expect(await res.text()).toContain("Cuebot mod portal");
		}
		expect((await SELF.fetch("https://portal.techdaddydigital.com/portal.css")).headers.get("content-type")).toContain("text/css");
		expect((await SELF.fetch("https://portal.techdaddydigital.com/nope")).status).toBe(404);
	});

	it("keeps each host to its own routes", async () => {
		expect((await SELF.fetch(`${RELAY}/`)).status).toBe(200);
		expect((await SELF.fetch(`${RELAY}/auth/return`)).status).toBe(404);
		expect((await SELF.fetch("https://portal.techdaddydigital.com/connect?id=" + "a".repeat(32))).status).toBe(404);
		expect((await SELF.fetch("https://other.techdaddydigital.com/")).status).toBe(404);
		expect((await SELF.fetch(`${RELAY}/connect?id=${"a".repeat(32)}`)).status).toBe(426);
		expect((await SELF.fetch(`${RELAY}/connect?id=not-a-relay-id`, { headers: { Upgrade: "websocket" } })).status).toBe(404);
		expect((await SELF.fetch(`${RELAY}/c/short`, { headers: { Upgrade: "websocket" } })).status).toBe(404);
	});
});

describe("Cuebot connection", () => {
	it("accepts a Cuebot that proves the key behind its relay ID", async () => {
		const { cuebot } = await onlineCuebot();
		cuebot.ws.close();
	});

	it("refuses a key that does not hash to the relay ID, or a wrong signature", async () => {
		const me = await identity();
		const stranger = await identity();
		const wrongKey = await upgrade(`${RELAY}/connect?id=${me.id}`);
		const challenge = await wrongKey.json();
		const signed = concat(text(CONNECT_LABEL), fromBase64Url(challenge.nonce), text("relay.techdaddydigital.com"));
		const sig = new Uint8Array(await subtle.sign({ name: "ECDSA", hash: "SHA-256" }, stranger.sign, signed)) as Bytes;
		const hooks = new Uint8Array(32) as Bytes;
		wrongKey.ws.send(JSON.stringify({ type: "auth", key: base64Url(stranger.raw), signature: base64Url(sig), hooks: base64Url(hooks) }));
		expect((await wrongKey.closed).code).toBe(CLOSE.authFailed);

		const { first } = await connectCuebot(me, { signer: stranger });
		expect((first as CloseEvent).code).toBe(CLOSE.authFailed);
	});

	it("lets only a proven Cuebot replace the current connection", async () => {
		const { me, cuebot } = await onlineCuebot();
		const impostor = await upgrade(`${RELAY}/connect?id=${me.id}`);
		await impostor.json();
		impostor.ws.send(JSON.stringify({ type: "auth", key: "x", signature: "y", hooks: "z" }));
		expect([CLOSE.authFailed, CLOSE.protocol]).toContain((await impostor.closed).code);
		await cuebot.silent();

		const { first } = await connectCuebot(me);
		expect(first.type).toBe("ready");
		expect((await cuebot.closed).code).toBe(CLOSE.replaced);
	});
});

describe("mod devices", () => {
	it("are refused while the streamer's Cuebot is offline", async () => {
		const me = await identity();
		const device = await upgrade(`${RELAY}/c/${me.id}`);
		expect((await device.closed).code).toBe(CLOSE.cuebotOffline);
	});

	it("exchange opaque frames with Cuebot on their own channel", async () => {
		const { me, cuebot } = await onlineCuebot();
		const device = await upgrade(`${RELAY}/c/${me.id}`);
		const opened = await cuebot.json();
		expect(opened.type).toBe("open");
		expect(opened.ch).not.toBe(HOOK_CHANNEL);

		device.ws.send(new Uint8Array([1, 2, 3]));
		const up = await cuebot.binary();
		expect(up.ch).toBe(opened.ch);
		expect([...up.payload]).toEqual([1, 2, 3]);

		cuebot.ws.send(frame(opened.ch, new Uint8Array([9, 8]) as Bytes));
		expect([...new Uint8Array(await device.bytes())]).toEqual([9, 8]);

		// Frames for unknown channels go nowhere.
		cuebot.ws.send(frame(opened.ch + 1, new Uint8Array([7]) as Bytes));
		await device.silent();

		cuebot.ws.send(JSON.stringify({ type: "close", ch: opened.ch, code: 4100, reason: "Signed out" }));
		const closed = await device.closed;
		expect(closed.code).toBe(4100);
		expect(closed.reason).toBe("Signed out");
		cuebot.ws.close();
	});

	it("tell Cuebot when they leave, and are closed when Cuebot leaves", async () => {
		const { me, cuebot } = await onlineCuebot();
		const a = await upgrade(`${RELAY}/c/${me.id}`);
		const chA = (await cuebot.json()).ch;
		const b = await upgrade(`${RELAY}/c/${me.id}`);
		await cuebot.json();

		a.ws.close(1000, "bye");
		expect(await cuebot.json()).toEqual({ type: "close", ch: chA });

		cuebot.ws.close(1000, "quit");
		expect((await b.closed).code).toBe(CLOSE.cuebotLeft);
	});

	it("are closed for text frames and oversized frames", async () => {
		const { me, cuebot } = await onlineCuebot();
		const talker = await upgrade(`${RELAY}/c/${me.id}`);
		await cuebot.json();
		talker.ws.send("hello");
		expect((await talker.closed).code).toBe(CLOSE.protocol);

		const big = await upgrade(`${RELAY}/c/${me.id}`);
		await cuebot.json();
		big.ws.send(new Uint8Array(LIMITS.maxDeviceFrame + 1));
		expect((await big.closed).code).toBe(CLOSE.tooMany);
		cuebot.ws.close();
	});
});

describe("webhooks", () => {
	it("are sealed to Cuebot's identity key, without the sender's address", async () => {
		const { me, cuebot } = await onlineCuebot();
		const res = await SELF.fetch(`${HOOKS}/${me.id}/${ROUTE_SECRET}/kofi/extra/path?x=1`, {
			method: "POST",
			body: "data=hello",
			headers: { "content-type": "application/x-www-form-urlencoded", "x-forwarded-for": "203.0.113.9", "cf-connecting-ip": "203.0.113.9" },
		});
		expect(res.status).toBe(200);
		const { ch, payload } = await cuebot.binary();
		expect(ch).toBe(HOOK_CHANNEL);
		const delivery = await open(me, payload);
		expect(delivery).toMatchObject({ type: "delivery", service: "kofi", path: "extra/path", method: "POST", query: "x=1" });
		expect(atob(delivery.bodyBase64)).toBe("data=hello");
		expect(delivery.headers["content-type"]).toBe("application/x-www-form-urlencoded");
		expect(Object.keys(delivery.headers).filter((h) => h.startsWith("cf-") || h.startsWith("x-forwarded"))).toEqual([]);
		cuebot.ws.close();
	});

	it("answer 200 and go nowhere for a wrong secret or an offline Cuebot", async () => {
		const { me, cuebot } = await onlineCuebot();
		expect((await SELF.fetch(`${HOOKS}/${me.id}/wrong-secret-0123456789/kofi`, { method: "POST", body: "x" })).status).toBe(200);
		await cuebot.silent();
		cuebot.ws.close();

		const offline = await identity();
		expect((await SELF.fetch(`${HOOKS}/${offline.id}/${ROUTE_SECRET}/kofi`, { method: "POST", body: "x" })).status).toBe(200);
	});

	it("refuse malformed addresses, other methods and big bodies", async () => {
		const me = await identity();
		expect((await SELF.fetch(`${HOOKS}/${me.id}/short/kofi`, { method: "POST" })).status).toBe(404);
		expect((await SELF.fetch(`${HOOKS}/${me.id}/${ROUTE_SECRET}/Bad_Name`, { method: "POST" })).status).toBe(404);
		expect((await SELF.fetch(`${HOOKS}/${me.id}/${ROUTE_SECRET}/kofi`, { method: "DELETE" })).status).toBe(405);
		expect((await SELF.fetch(`${HOOKS}/${me.id}/${ROUTE_SECRET}/kofi`, { method: "POST", body: new Uint8Array(LIMITS.maxHookBody + 1) })).status).toBe(413);
	});

	it("also work under /h/ on the workers.dev address", async () => {
		const { me, cuebot } = await onlineCuebot();
		expect((await SELF.fetch(`https://cuebot-portal.example.workers.dev/h/${me.id}/${ROUTE_SECRET}/patreon`, { method: "POST", body: "{}" })).status).toBe(200);
		expect((await cuebot.binary()).ch).toBe(HOOK_CHANNEL);
		cuebot.ws.close();
	});
});
