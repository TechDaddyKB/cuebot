import { env, reset, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import type { Delivery } from "../src/index";

const ROUTE = "test-route-secret-0123456789abcdef";
const TOKEN = "test-connect-token-0123456789abcdef";
const BASE = "https://relay.example";

function hub() {
	return env.RELAY.get(env.RELAY.idFromName("relay"));
}

function post(service: string, body: string, init: RequestInit & { secret?: string; suffix?: string } = {}) {
	const { secret = ROUTE, suffix = "", ...rest } = init;
	return SELF.fetch(`${BASE}/hook/${secret}/${service}${suffix}`, { method: "POST", body, ...rest });
}

interface Client {
	ws: WebSocket;
	next(): Promise<any>;
	closed: Promise<CloseEvent>;
}

async function connect(token = TOKEN): Promise<Client> {
	const res = await SELF.fetch(`${BASE}/connect`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${token}` } });
	expect(res.status).toBe(101);
	const ws = res.webSocket!;
	const queue: any[] = [];
	const waiters: ((m: any) => void)[] = [];
	ws.addEventListener("message", (e) => {
		const msg = JSON.parse(e.data as string);
		const w = waiters.shift();
		if (w) w(msg);
		else queue.push(msg);
	});
	const closed = new Promise<CloseEvent>((resolve) => ws.addEventListener("close", resolve));
	ws.accept();
	return {
		ws,
		closed,
		next: () =>
			queue.length
				? Promise.resolve(queue.shift())
				: new Promise((resolve, reject) => {
						waiters.push(resolve);
						setTimeout(() => reject(new Error("timed out waiting for a message")), 3000);
					}),
	};
}

async function nextDelivery(c: Client): Promise<Delivery> {
	for (;;) {
		const m = await c.next();
		if (m.type === "delivery") return m;
	}
}

/** Waits until the hub's buffer reaches the expected size (acks are processed asynchronously). */
async function waitForBuffered(n: number) {
	for (let i = 0; i < 100; i++) {
		if ((await hub().buffered()) === n) return;
		await new Promise((r) => setTimeout(r, 20));
	}
	expect(await hub().buffered()).toBe(n);
}

beforeEach(async () => {
	await reset();
});

describe("webhook endpoint", () => {
	it("returns 404 for a wrong route secret and stores nothing", async () => {
		const res = await post("kofi", "x", { secret: "wrong-secret-wrong-secret-wrong" });
		expect(res.status).toBe(404);
		expect(await hub().buffered()).toBe(0);
	});

	it("returns 404 for a missing secret, bad service names and unknown paths", async () => {
		expect((await SELF.fetch(`${BASE}/hook/`, { method: "POST" })).status).toBe(404);
		expect((await post("Ko-Fi", "x")).status).toBe(404);
		expect((await post("ko_fi", "x")).status).toBe(404);
		expect((await SELF.fetch(`${BASE}/hook/${ROUTE}`, { method: "POST" })).status).toBe(404);
		expect((await SELF.fetch(`${BASE}/other`)).status).toBe(404);
	});

	it("answers 200 with an empty body and rejects other methods and large bodies", async () => {
		const ok = await post("kofi", "data=1");
		expect(ok.status).toBe(200);
		expect(await ok.text()).toBe("");
		expect((await SELF.fetch(`${BASE}/hook/${ROUTE}/kofi`, { method: "GET" })).status).toBe(200);
		expect((await SELF.fetch(`${BASE}/hook/${ROUTE}/kofi`, { method: "DELETE" })).status).toBe(405);
		expect((await post("kofi", "x".repeat(2000))).status).toBe(413);
	});
});

describe("Cuebot connection", () => {
	it("requires the connect token", async () => {
		const none = await SELF.fetch(`${BASE}/connect`, { headers: { Upgrade: "websocket" } });
		expect(none.status).toBe(401);
		const wrong = await SELF.fetch(`${BASE}/connect`, { headers: { Upgrade: "websocket", Authorization: "Bearer nope-nope-nope-nope-nope" } });
		expect(wrong.status).toBe(401);
		const noUpgrade = await SELF.fetch(`${BASE}/connect`, { headers: { Authorization: `Bearer ${TOKEN}` } });
		expect(noUpgrade.status).toBe(426);
	});

	it("forwards a delivery with raw body, lower-case headers, path and query", async () => {
		const c = await connect();
		expect(await c.next()).toMatchObject({ type: "hello", version: 1, buffered: 0 });
		const body = '{"amount":"5.00","ünïcode":true}';
		const res = await post("custom", body, { suffix: "/my/hook?x=1&y=two", headers: { "X-Custom-Sig": "abc", "Content-Type": "application/json" } });
		expect(res.status).toBe(200);
		const d = await nextDelivery(c);
		expect(d.service).toBe("custom");
		expect(d.path).toBe("my/hook");
		expect(d.query).toBe("x=1&y=two");
		expect(d.method).toBe("POST");
		expect(d.headers["x-custom-sig"]).toBe("abc");
		expect(d.headers["content-type"]).toBe("application/json");
		expect(new TextDecoder().decode(Uint8Array.from(atob(d.bodyBase64), (ch) => ch.charCodeAt(0)))).toBe(body);
		expect(Number.isNaN(Date.parse(d.receivedAt))).toBe(false);
		expect(d.id).toMatch(/^[0-9a-f-]{36}$/);
		c.ws.close(1000);
	});

	it("keeps deliveries until acknowledged", async () => {
		const c = await connect();
		await post("kofi", "a");
		const d = await nextDelivery(c);
		expect(await hub().buffered()).toBe(1);
		c.ws.send(JSON.stringify({ type: "ack", id: d.id }));
		await waitForBuffered(0);
		c.ws.close(1000);
	});

	it("buffers while offline and replays on reconnect, oldest first", async () => {
		await post("kofi", "one");
		await post("patreon", "two");
		expect(await hub().buffered()).toBe(2);
		const c = await connect();
		expect(await c.next()).toMatchObject({ type: "hello", buffered: 2 });
		const first = await nextDelivery(c);
		const second = await nextDelivery(c);
		expect([first.service, second.service]).toEqual(["kofi", "patreon"]);
		c.ws.send(JSON.stringify({ type: "ack", ids: [first.id, second.id] }));
		await waitForBuffered(0);
		c.ws.close(1000);
	});

	it("replays unacknowledged deliveries again after a reconnect", async () => {
		const c1 = await connect();
		await post("kofi", "one");
		const d1 = await nextDelivery(c1);
		c1.ws.close(1000);
		const c2 = await connect();
		const again = await nextDelivery(c2);
		expect(again.id).toBe(d1.id);
		c2.ws.close(1000);
	});

	it("drops the oldest deliveries beyond the cap", async () => {
		for (const n of ["1", "2", "3", "4", "5"]) await post("kofi", n);
		expect(await hub().buffered()).toBe(3);
		const c = await connect();
		const bodies: string[] = [];
		for (let i = 0; i < 3; i++) bodies.push(atob((await nextDelivery(c)).bodyBase64));
		expect(bodies).toEqual(["3", "4", "5"]);
		c.ws.close(1000);
	});

	it("replaces the previous connection with a new one", async () => {
		const c1 = await connect();
		const c2 = await connect();
		const closed = await c1.closed;
		expect(closed.code).toBe(4000);
		await post("kofi", "x");
		expect((await nextDelivery(c2)).service).toBe("kofi");
		c2.ws.close(1000);
	});

	it("answers the heartbeat ping", async () => {
		const c = await connect();
		expect((await c.next()).type).toBe("hello");
		c.ws.send('{"type":"ping"}');
		expect(await c.next()).toEqual({ type: "pong" });
		c.ws.close(1000);
	});
});

describe("entry module", () => {
	it("exports only the handler and the Durable Object class (workerd rejects other values at deploy)", async () => {
		const mod = await import("../src/index");
		expect(Object.keys(mod).sort()).toEqual(["RelayHub", "default"]);
	});
});
