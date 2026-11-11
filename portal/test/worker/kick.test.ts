import { SELF } from "cloudflare:test";
import { afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { kick, resetKickKey } from "../../worker/kick";
import { HOOK_CHANNEL } from "../../worker/protocol.js";
import { HOOKS, RELAY, onlineCuebot, open, text, type Socket } from "./helpers";

const CLIENT_ID = "kick-test-client";
const BROADCASTER = 4242;
const GOOD_TOKEN = "good-user-token";
const realFetch = kick.fetch;
let signer: CryptoKey;
let pem: string;
let calls: string[] = [];

beforeAll(async () => {
	const pair = (await crypto.subtle.generateKey(
		{ name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
		true,
		["sign", "verify"],
	)) as CryptoKeyPair;
	signer = pair.privateKey;
	const spki = new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer);
	pem = `-----BEGIN PUBLIC KEY-----\n${btoa(String.fromCharCode(...spki)).replace(/(.{64})/g, "$1\n")}\n-----END PUBLIC KEY-----\n`;
});

/** Kick's public key, token introspection and users endpoints, as the intake calls them. */
beforeEach(() => {
	calls = [];
	resetKickKey();
	kick.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url);
		const auth = new Headers(init?.headers).get("authorization") ?? "";
		calls.push(`${init?.method ?? "GET"} ${url.pathname}`);
		const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
		switch (url.pathname) {
			case "/public/v1/public-key":
				return json({ data: { public_key: pem }, message: "OK" });
			case "/oauth/token/introspect":
				if (auth === `Bearer ${GOOD_TOKEN}`) return json({ data: { active: true, client_id: CLIENT_ID, token_type: "user", scope: "events:subscribe" } });
				if (auth === "Bearer other-app-token") return json({ data: { active: true, client_id: "someone-else", token_type: "user" } });
				return json({ data: { active: false } });
			case "/public/v1/users":
				return json({ data: [{ user_id: BROADCASTER, name: "streamer" }] });
			default:
				return new Response("not found", { status: 404 });
		}
	};
});

afterEach(() => {
	kick.fetch = realFetch;
});

let rid = 0;
async function request(cuebot: Socket, body: Record<string, unknown>): Promise<any> {
	const id = ++rid;
	cuebot.ws.send(JSON.stringify({ ...body, rid: id }));
	const answer = await cuebot.json();
	expect(answer).toMatchObject({ type: body.type, rid: id });
	return answer;
}

/** A Kick webhook delivery for <broadcaster>, signed like Kick signs them. */
async function delivery(broadcaster: number, opts: { at?: Date; badSignature?: boolean } = {}): Promise<RequestInit> {
	const body = JSON.stringify({ broadcaster: { user_id: broadcaster }, follower: { user_id: 7, username: "fan" } });
	const id = `01K${crypto.randomUUID().replaceAll("-", "").slice(0, 23).toUpperCase()}`;
	const timestamp = (opts.at ?? new Date()).toISOString();
	const signature = new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", signer, text(`${id}.${timestamp}.${opts.badSignature ? "x" : ""}${body}`)));
	return {
		method: "POST",
		body,
		headers: {
			"content-type": "application/json",
			"kick-event-message-id": id,
			"kick-event-message-timestamp": timestamp,
			"kick-event-signature": btoa(String.fromCharCode(...signature)),
			"kick-event-type": "channel.followed",
			"kick-event-version": "1",
			"cf-connecting-ip": "203.0.113.9",
		},
	};
}

describe("Kick event routes", () => {
	it("are set for the broadcaster Kick confirms the token belongs to", async () => {
		const { cuebot } = await onlineCuebot();
		expect(await request(cuebot, { type: "kick-route", token: GOOD_TOKEN })).toEqual({ type: "kick-route", rid, ok: true, userId: BROADCASTER });
		expect(calls).toEqual(["POST /oauth/token/introspect", "GET /public/v1/users"]);
		cuebot.ws.close();
	});

	it("refuse tokens Kick does not accept, and tokens of another app", async () => {
		const { cuebot } = await onlineCuebot();
		expect(await request(cuebot, { type: "kick-route", token: "expired" })).toMatchObject({ ok: false, error: "The Kick token is no longer valid" });
		expect(await request(cuebot, { type: "kick-route", token: "other-app-token" })).toMatchObject({ ok: false, error: "The token is not for Cuebot's Kick app" });
		expect(await request(cuebot, { type: "kick-route" })).toMatchObject({ ok: false, error: "No Kick token" });
		cuebot.ws.close();
	});

	it("can only be removed by the Cuebot they point to", async () => {
		const first = await onlineCuebot();
		const second = await onlineCuebot();
		await request(first.cuebot, { type: "kick-route", token: GOOD_TOKEN });
		await request(second.cuebot, { type: "kick-unroute", userId: BROADCASTER });
		expect((await SELF.fetch(`${HOOKS}/kick`, await delivery(BROADCASTER))).status).toBe(200);
		expect((await first.cuebot.binary()).ch).toBe(HOOK_CHANNEL);

		await request(first.cuebot, { type: "kick-unroute", userId: BROADCASTER });
		expect((await SELF.fetch(`${HOOKS}/kick`, await delivery(BROADCASTER))).status).toBe(200);
		await first.cuebot.silent();
		first.cuebot.ws.close();
		second.cuebot.ws.close();
	});
});

describe("the Kick intake", () => {
	it("hands a signed event, sealed, to the Cuebot that registered the broadcaster", async () => {
		const { me, cuebot } = await onlineCuebot();
		await request(cuebot, { type: "kick-route", token: GOOD_TOKEN });
		const init = await delivery(BROADCASTER);
		expect((await SELF.fetch(`${HOOKS}/kick`, init)).status).toBe(200);
		const { ch, payload } = await cuebot.binary();
		expect(ch).toBe(HOOK_CHANNEL);
		const d = await open(me, payload);
		expect(d).toMatchObject({ type: "delivery", service: "kick", path: "", method: "POST" });
		expect(atob(d.bodyBase64)).toBe(init.body);
		const headers = init.headers as Record<string, string>;
		for (const name of ["kick-event-message-id", "kick-event-message-timestamp", "kick-event-signature", "kick-event-type"])
			expect(d.headers[name]).toBe(headers[name]);
		expect(d.headers["cf-connecting-ip"]).toBeUndefined();
		cuebot.ws.close();
	});

	it("follows the route to the newest Cuebot", async () => {
		const first = await onlineCuebot();
		const second = await onlineCuebot();
		await request(first.cuebot, { type: "kick-route", token: GOOD_TOKEN });
		await request(second.cuebot, { type: "kick-route", token: GOOD_TOKEN });
		await SELF.fetch(`${HOOKS}/kick`, await delivery(BROADCASTER));
		expect((await second.cuebot.binary()).ch).toBe(HOOK_CHANNEL);
		await first.cuebot.silent();
		first.cuebot.ws.close();
		second.cuebot.ws.close();
	});

	it("drops forged, stale and unrouted events with a 200", async () => {
		const { cuebot } = await onlineCuebot();
		await request(cuebot, { type: "kick-route", token: GOOD_TOKEN });
		expect((await SELF.fetch(`${HOOKS}/kick`, await delivery(BROADCASTER, { badSignature: true }))).status).toBe(200);
		expect((await SELF.fetch(`${HOOKS}/kick`, await delivery(BROADCASTER, { at: new Date(Date.now() - 30 * 60_000) }))).status).toBe(200);
		expect((await SELF.fetch(`${HOOKS}/kick`, await delivery(999_999))).status).toBe(200);
		expect((await SELF.fetch(`${HOOKS}/kick`, { method: "POST", body: "{}" })).status).toBe(200);
		await cuebot.silent();
		cuebot.ws.close();
	});

	it("takes POST only, and also answers under /h/kick on the relay and workers.dev hosts", async () => {
		expect((await SELF.fetch(`${HOOKS}/kick`, { method: "GET" })).status).toBe(405);
		const { cuebot } = await onlineCuebot();
		await request(cuebot, { type: "kick-route", token: GOOD_TOKEN });
		for (const base of [RELAY, "https://cuebot-portal.example.workers.dev"]) {
			expect((await SELF.fetch(`${base}/h/kick`, await delivery(BROADCASTER))).status).toBe(200);
			expect((await cuebot.binary()).ch).toBe(HOOK_CHANNEL);
		}
		cuebot.ws.close();
	});
});
