// The shared mod portal relay (docs/MOD_PORTAL_PLAN.md, sections 3, 7, 9 and 10; protocol in portal/RELAY.md).
//
// One Worker on three hosts:
//   relay.<domain>/connect?id=<relay id>   the streamer's Cuebot (outbound WebSocket, proves its identity key)
//   relay.<domain>/c/<relay id>            mod devices (WebSocket; end-to-end encrypted with Cuebot, opaque here)
//   hooks.<domain>/<relay id>/<route secret>/<service>[/<path>]   webhooks, sealed to Cuebot's identity key
//   hooks.<domain>/kick                    Kick events of Cuebot's built-in Kick app, routed by broadcaster (kick.ts)
//   portal.<domain>/                       the loader page and /auth/return
// Any other host (workers.dev, local tests) serves all of them, with webhooks under /h/.
//
// One Durable Object per relay ID holds the sockets and writes nothing to storage. The only stored data is one
// KickRoute object per Kick broadcaster on the built-in app: that broadcaster's Kick user id (the object's name) and
// the relay ID of the Cuebot that proved it holds a token of that broadcaster, forgotten after 30 days without a
// refresh. No KV, no logs. Webhooks for an offline Cuebot are answered 200 and dropped.

import { DurableObject } from "cloudflare:workers";
import { type Bytes, base64Url, concat, fromBase64Url, relayId, seal, verify } from "../src/crypto.js";
import { broadcasterOf, isFresh, kickAccount, verifyKickSignature } from "./kick.js";
import { CLOSE, CONNECT_LABEL, HOOK_CHANNEL, KICK_ROUTE_TTL_MS, LIMITS, PROTOCOL_VERSION } from "./protocol.js";

export interface Env {
	PORTAL_RELAY: DurableObjectNamespace<PortalRelay>;
	KICK_ROUTES: DurableObjectNamespace<KickRoute>;
	/** Client ID of Cuebot's built-in Kick app (a Worker secret). Without it the Kick intake takes no routes. */
	KICK_CLIENT_ID?: string;
	/** The domain whose portal., relay. and hooks. names this Worker answers (wrangler.jsonc "vars"). */
	PORTAL_DOMAIN?: string;
	/** dist/: the built loader (npm run build). */
	ASSETS: Fetcher;
}

const RELAY_ID = /^[a-z2-7]{32}$/;
const SERVICE_NAME = /^[a-z0-9][a-z0-9-]{0,63}$/;
const HOOK_METHODS = new Set(["POST", "GET", "PUT"]);
/** Never forwarded: they carry the sender's address or Cloudflare's own routing data. */
const DROPPED_HEADER = /^(cf-|x-forwarded-|x-real-ip$|true-client-ip$|forwarded$|cdn-loop$)/;
const encoder = new TextEncoder();
const utf8 = { encode: (s: string): Bytes => encoder.encode(s) as Bytes };

/** One forwarded webhook, the same shape as the self-hosted relay's (relay/README.md), before sealing. */
export interface Delivery {
	type: "delivery";
	id: string;
	service: string;
	path: string;
	method: string;
	query: string;
	headers: Record<string, string>;
	bodyBase64: string;
	receivedAt: string;
}

type Route = "connect" | "device" | "hook" | "kick" | "portal";

function route(url: URL, env: Env): { route: Route; path: string } | null {
	const domain = (env.PORTAL_DOMAIN ?? "").toLowerCase();
	const host = url.hostname.toLowerCase();
	const sub = domain && host.endsWith("." + domain) ? host.slice(0, -(domain.length + 1)) : null;
	const p = url.pathname;
	const relayRoute = (): Route | null => (p === "/connect" ? "connect" : p.startsWith("/c/") ? "device" : null);
	switch (sub) {
		case "relay": {
			const r = relayRoute();
			if (r) return { route: r, path: p };
			// Cuebot builds /h/ addresses from a relay URL override, so the relay host takes them too.
			if (p === "/h/kick") return { route: "kick", path: "/kick" };
			if (p.startsWith("/h/")) return { route: "hook", path: p.slice(2) };
			return p === "/" ? { route: "portal", path: "/health" } : null;
		}
		case "hooks":
			return { route: p === "/kick" ? "kick" : "hook", path: p };
		case "portal":
			return { route: "portal", path: p };
		case null: {
			const r = relayRoute();
			if (r) return { route: r, path: p };
			if (p === "/h/kick") return { route: "kick", path: "/kick" };
			if (p.startsWith("/h/")) return { route: "hook", path: p.slice(2) };
			return { route: "portal", path: p };
		}
		default:
			return null;
	}
}

const notFound = () => new Response("Not found", { status: 404 });

function relay(env: Env, id: string): DurableObjectStub<PortalRelay> {
	return env.PORTAL_RELAY.get(env.PORTAL_RELAY.idFromName(id));
}

function kickRoute(env: Env, userId: number): DurableObjectStub<KickRoute> {
	return env.KICK_ROUTES.get(env.KICK_ROUTES.idFromName(`kick:${userId}`));
}

export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const url = new URL(request.url);
		const r = route(url, env);
		if (!r) return notFound();
		switch (r.route) {
			case "connect": {
				const id = url.searchParams.get("id") ?? "";
				if (!RELAY_ID.test(id)) return notFound();
				if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("Expected a WebSocket upgrade", { status: 426 });
				return relay(env, id).fetch(new Request(`https://relay/cuebot?id=${id}&host=${encodeURIComponent(url.host)}`, request));
			}
			case "device": {
				const id = r.path.slice(3);
				if (!RELAY_ID.test(id)) return notFound();
				if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("Expected a WebSocket upgrade", { status: 426 });
				return relay(env, id).fetch(new Request("https://relay/device", request));
			}
			case "hook":
				return hook(request, url, r.path, env);
			case "kick":
				return kickIntake(request, url, env);
			case "portal":
				return portal(request, r.path, env);
		}
	},
} satisfies ExportedHandler<Env>;

async function hook(request: Request, url: URL, path: string, env: Env): Promise<Response> {
	// /<relay id>/<route secret>/<service>[/<path...>]
	const [, id = "", secret = "", service = "", ...rest] = path.split("/");
	if (!RELAY_ID.test(id) || secret.length < 16 || !SERVICE_NAME.test(service)) return notFound();
	if (!HOOK_METHODS.has(request.method)) return new Response("Method not allowed", { status: 405, headers: { allow: "GET, POST, PUT" } });
	if (Number(request.headers.get("content-length") ?? "0") > LIMITS.maxHookBody) return new Response("Payload too large", { status: 413 });
	const body = await readLimited(request, LIMITS.maxHookBody);
	if (body === null) return new Response("Payload too large", { status: 413 });

	const headers: Record<string, string> = {};
	request.headers.forEach((value, name) => {
		const lower = name.toLowerCase();
		if (!DROPPED_HEADER.test(lower)) headers[lower] = value;
	});
	const delivery: Delivery = {
		type: "delivery",
		id: crypto.randomUUID(),
		service,
		path: rest.join("/"),
		method: request.method,
		query: url.search.startsWith("?") ? url.search.slice(1) : "",
		headers,
		bodyBase64: toBase64(body),
		receivedAt: new Date().toISOString(),
	};
	// Whatever happens (Cuebot offline, wrong secret, over the limit) the sender sees the same quick 200, so a
	// webhook address reveals nothing about whether the streamer is online. Nothing is kept for later.
	await relay(env, id).hook(secret, delivery);
	return new Response(null, { status: 200 });
}

/**
 * A Kick event for any streamer on Cuebot's built-in Kick app. Checked against Kick's signature and age, then handed
 * (sealed) to the Cuebot that registered that broadcaster. Kick always gets a quick empty 200, because it unsubscribes an
 * app whose address keeps failing; nothing is kept for an offline Cuebot.
 */
async function kickIntake(request: Request, url: URL, env: Env): Promise<Response> {
	if (request.method !== "POST") return new Response("Method not allowed", { status: 405, headers: { allow: "POST" } });
	if (Number(request.headers.get("content-length") ?? "0") > LIMITS.maxHookBody) return new Response("Payload too large", { status: 413 });
	const body = await readLimited(request, LIMITS.maxHookBody);
	if (body === null) return new Response("Payload too large", { status: 413 });
	const ok = () => new Response(null, { status: 200 });
	const h = (name: string) => request.headers.get(name) ?? "";
	const timestamp = h("kick-event-message-timestamp");
	if (!isFresh(timestamp) || !(await verifyKickSignature(h("kick-event-message-id"), timestamp, body, h("kick-event-signature")))) return ok();
	const userId = broadcasterOf(body);
	if (userId === null) return ok();
	const relayId = await kickRoute(env, userId).get();
	if (!relayId || !RELAY_ID.test(relayId)) return ok();
	const headers: Record<string, string> = {};
	request.headers.forEach((value, name) => {
		const lower = name.toLowerCase();
		if (!DROPPED_HEADER.test(lower)) headers[lower] = value;
	});
	await relay(env, relayId).kickHook({
		type: "delivery",
		id: crypto.randomUUID(),
		service: "kick",
		path: "",
		method: "POST",
		query: url.search.startsWith("?") ? url.search.slice(1) : "",
		headers,
		bodyBase64: toBase64(body),
		receivedAt: new Date().toISOString(),
	});
	return ok();
}

async function readLimited(request: Request, max: number): Promise<Bytes | null> {
	if (!request.body) return new Uint8Array(0);
	const reader = request.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	for (;;) {
		const { done, value } = await reader.read();
		if (done) break;
		total += value.byteLength;
		if (total > max) {
			await reader.cancel();
			return null;
		}
		chunks.push(value);
	}
	const out = new Uint8Array(total);
	let at = 0;
	for (const c of chunks) {
		out.set(c, at);
		at += c.byteLength;
	}
	return out;
}

function toBase64(bytes: Uint8Array): string {
	let binary = "";
	for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
	return btoa(binary);
}

// The portal pages: one page for the loader (web/main.ts, built to dist/loader.js) and the sign-in return address.
// Script only from this origin, plus blob: for the portal UI the streamer's Cuebot sends over the encrypted link (the
// loader makes those blobs); connections only here and to the relay.
function securityHeaders(env: Env): Record<string, string> {
	const relay = env.PORTAL_DOMAIN ? ` wss://relay.${env.PORTAL_DOMAIN}` : "";
	return {
		"content-security-policy": `default-src 'none'; script-src 'self' blob:; style-src 'self' blob:; img-src 'self' blob: data:; media-src blob:; connect-src 'self'${relay}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
		"referrer-policy": "no-referrer",
		"x-content-type-options": "nosniff",
		"cross-origin-opener-policy": "same-origin",
		"cross-origin-resource-policy": "same-origin",
		"permissions-policy": "camera=(), microphone=(), geolocation=(), interest-cohort=()",
		"strict-transport-security": "max-age=31536000",
		"cache-control": "no-store",
	};
}

const CSS = `body{font:16px/1.5 system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;color:#1d1d20;background:#fafafa}
h1{font-size:1.4rem}.error{color:#b3261e}.providers{display:flex;flex-direction:column;gap:.6rem;margin:1.2rem 0}
button{font:inherit;padding:.65rem 1rem;border-radius:.5rem;border:1px solid #c9c9d1;background:#fff;color:inherit;cursor:pointer;text-align:left}
button:hover{border-color:#6b5cff}button.secondary{margin-top:1rem}
@media(prefers-color-scheme:dark){body{color:#e8e8ea;background:#17171a}button{background:#24242a;border-color:#3a3a44}.error{color:#f2b8b5}}`;

const LOADER_PAGE = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Cuebot mod portal</title><link rel="stylesheet" href="/portal.css"><script type="module" src="/loader.js"></script></head>
<body><main><h1>Cuebot mod portal</h1><noscript><p>The mod portal needs JavaScript.</p></noscript></main></body></html>`;

async function portal(request: Request, path: string, env: Env): Promise<Response> {
	if (request.method !== "GET" && request.method !== "HEAD") return new Response("Method not allowed", { status: 405, headers: { allow: "GET, HEAD" } });
	const headers = securityHeaders(env);
	switch (path) {
		case "/":
		case "/auth/return":
			return new Response(LOADER_PAGE, { headers: { "content-type": "text/html; charset=utf-8", ...headers } });
		case "/loader.js": {
			const asset = await env.ASSETS.fetch(new Request(new URL("/loader.js", request.url)));
			if (!asset.ok) return notFound();
			return new Response(asset.body, { headers: { "content-type": "text/javascript; charset=utf-8", ...headers } });
		}
		case "/portal.css":
			return new Response(CSS, { headers: { "content-type": "text/css; charset=utf-8", ...headers } });
		case "/health":
			return new Response("Cuebot portal relay\n", { headers: { "content-type": "text/plain", ...headers } });
		default:
			return notFound();
	}
}

/** What each socket's hibernation attachment holds. */
type Attachment =
	| { role: "pending"; id: string; nonce: string; host: string; issued: number }
	| { role: "cuebot"; id: string; key: string; hooks: string }
	| { role: "device"; ch: number };

interface Bucket {
	tokens: number;
	at: number;
}

/**
 * One relay ID: the Cuebot connection, its mod devices and its webhooks. Sockets use the Hibernation API, so an idle
 * relay costs nothing; everything here is rebuilt from the sockets' attachments when the object wakes.
 */
export class PortalRelay extends DurableObject<Env> {
	private readonly buckets = new Map<number, Bucket>();
	private connects: Bucket = { tokens: LIMITS.connectsPerMinute, at: Date.now() };
	private hooks: Bucket = { tokens: LIMITS.hooksPerMinute, at: Date.now() };
	private kickRoutes: Bucket = { tokens: LIMITS.kickRoutesPerMinute, at: Date.now() };
	private day = utcDay();
	private bytes = 0;
	private identity: { raw: Bytes; hooks: Bytes } | null = null;

	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('{"type":"ping"}', '{"type":"pong"}'));
	}

	async fetch(request: Request): Promise<Response> {
		const url = new URL(request.url);
		const pair = new WebSocketPair();
		const [client, server] = [pair[0], pair[1]];
		if (url.pathname === "/cuebot") {
			const nonce = base64Url(crypto.getRandomValues(new Uint8Array(32)));
			const id = url.searchParams.get("id") ?? "";
			const host = url.searchParams.get("host") ?? "";
			this.ctx.acceptWebSocket(server);
			server.serializeAttachment({ role: "pending", id, nonce, host, issued: Date.now() } satisfies Attachment);
			server.send(JSON.stringify({ type: "challenge", version: PROTOCOL_VERSION, nonce }));
			setTimeout(() => {
				if ((server.deserializeAttachment() as Attachment | null)?.role === "pending") close(server, CLOSE.authTimeout, "No answer to the challenge");
			}, LIMITS.authTimeoutMs);
			return new Response(null, { status: 101, webSocket: client });
		}

		this.ctx.acceptWebSocket(server);
		const cuebot = this.cuebot();
		if (!cuebot) {
			close(server, CLOSE.cuebotOffline, "The streamer's Cuebot is not connected");
		} else if (!take(this.refill(this.connects, LIMITS.connectsPerMinute, LIMITS.connectsPerMinute / 60_000), 1)
			|| this.devices().length >= LIMITS.maxDevices || this.overDailyCap()) {
			close(server, CLOSE.tooMany, "Too many connections");
		} else {
			const ch = this.newChannel();
			server.serializeAttachment({ role: "device", ch } satisfies Attachment);
			send(cuebot, JSON.stringify({ type: "open", ch }));
		}
		return new Response(null, { status: 101, webSocket: client });
	}

	/** A webhook for this relay: sealed and forwarded when Cuebot is connected and the route secret matches. */
	async hook(secret: string, delivery: Delivery): Promise<void> {
		const cuebot = this.cuebot();
		if (!cuebot) return;
		const identity = this.identityOf(cuebot);
		if (!identity || !(await sameHash(secret, identity.hooks))) return;
		if (!take(this.refill(this.hooks, LIMITS.hooksPerMinute, LIMITS.hooksPerMinute / 60_000), 1) || this.overDailyCap()) return;
		const sealed = await seal(identity.raw, utf8.encode(JSON.stringify(delivery)));
		this.count(sealed.length);
		send(cuebot, frame(HOOK_CHANNEL, sealed));
	}

	/** A Kick event the intake checked and routed here: sealed and forwarded while Cuebot is connected. */
	async kickHook(delivery: Delivery): Promise<void> {
		const cuebot = this.cuebot();
		if (!cuebot) return;
		const identity = this.identityOf(cuebot);
		if (!identity) return;
		if (!take(this.refill(this.hooks, LIMITS.hooksPerMinute, LIMITS.hooksPerMinute / 60_000), 1) || this.overDailyCap()) return;
		const sealed = await seal(identity.raw, utf8.encode(JSON.stringify(delivery)));
		this.count(sealed.length);
		send(cuebot, frame(HOOK_CHANNEL, sealed));
	}

	async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
		const a = ws.deserializeAttachment() as Attachment | null;
		switch (a?.role) {
			case "pending":
				return this.authenticate(ws, a, message);
			case "cuebot":
				return this.fromCuebot(ws, a, message);
			case "device":
				return this.fromDevice(ws, a.ch, message);
			default:
				close(ws, CLOSE.protocol, "Unknown connection");
		}
	}

	async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
		close(ws, code === 1005 || code === 1006 ? 1000 : code, reason);
		this.left(ws);
	}

	async webSocketError(ws: WebSocket): Promise<void> {
		close(ws, 1011, "error");
		this.left(ws);
	}

	private async authenticate(ws: WebSocket, a: Extract<Attachment, { role: "pending" }>, message: string | ArrayBuffer): Promise<void> {
		if (Date.now() - a.issued > LIMITS.authTimeoutMs) return close(ws, CLOSE.authTimeout, "No answer to the challenge");
		let msg: { type?: unknown; key?: unknown; signature?: unknown; hooks?: unknown };
		try {
			msg = typeof message === "string" ? JSON.parse(message) : {};
		} catch {
			msg = {};
		}
		if (msg.type !== "auth" || typeof msg.key !== "string" || typeof msg.signature !== "string" || typeof msg.hooks !== "string")
			return close(ws, CLOSE.protocol, "Expected the auth answer");
		let key: Bytes, signature: Bytes, hooks: Bytes;
		try {
			key = fromBase64Url(msg.key);
			signature = fromBase64Url(msg.signature);
			hooks = fromBase64Url(msg.hooks);
		} catch {
			return close(ws, CLOSE.protocol, "Malformed auth answer");
		}
		const signed = concat(utf8.encode(CONNECT_LABEL), fromBase64Url(a.nonce), utf8.encode(a.host));
		if (hooks.length !== 32 || (await relayId(key)) !== a.id || !(await verify(key, signed, signature)))
			return close(ws, CLOSE.authFailed, "The identity key does not match this relay ID");

		// Only a Cuebot that proved the key replaces the previous connection, so a stranger cannot knock it off.
		for (const old of this.ctx.getWebSockets()) {
			if (old !== ws && (old.deserializeAttachment() as Attachment | null)?.role === "cuebot") close(old, CLOSE.replaced, "Replaced by a newer connection");
		}
		for (const device of this.devices()) close(device, CLOSE.cuebotLeft, "The streamer's Cuebot reconnected");
		ws.serializeAttachment({ role: "cuebot", id: a.id, key: base64Url(key), hooks: base64Url(hooks) } satisfies Attachment);
		this.identity = { raw: key, hooks };
		send(ws, JSON.stringify({ type: "ready", version: PROTOCOL_VERSION, maxDevices: LIMITS.maxDevices }));
	}

	private async fromCuebot(ws: WebSocket, a: Extract<Attachment, { role: "cuebot" }>, message: string | ArrayBuffer): Promise<void> {
		if (typeof message === "string") {
			let msg: { type?: unknown; ch?: unknown; code?: unknown; reason?: unknown; rid?: unknown; token?: unknown; userId?: unknown };
			try {
				msg = JSON.parse(message);
			} catch {
				return;
			}
			if (msg.type === "kick-route" || msg.type === "kick-unroute") return this.kickRouteRequest(ws, a.id, msg);
			if (msg.type === "close" && typeof msg.ch === "number") {
				const device = this.device(msg.ch);
				const code = typeof msg.code === "number" && msg.code >= 4000 && msg.code <= 4999 ? msg.code : 1000;
				if (device) close(device, code, typeof msg.reason === "string" ? msg.reason.slice(0, 120) : "");
			}
			return;
		}
		if (message.byteLength < 4 || message.byteLength > 4 + LIMITS.maxCuebotFrame) return;
		const ch = new DataView(message).getUint32(0);
		const device = ch === HOOK_CHANNEL ? undefined : this.device(ch);
		if (!device) return;
		this.count(message.byteLength);
		if (this.overDailyCap()) return this.closeAllDevices(CLOSE.tooMany, "The relay's daily limit was reached");
		send(device, message.slice(4));
	}

	/**
	 * kick-route: {"type":"kick-route","rid":n,"token":"<Kick user token>"} routes that broadcaster's Kick events to this
	 * relay once Kick confirms the token (the token is not kept). kick-unroute: {"type":"kick-unroute","rid":n,"userId":n}
	 * removes the route when it points here. Answer: {"type":<same>,"rid":n,"ok":true,"userId":n} or {...,"ok":false,"error":"..."}.
	 */
	private async kickRouteRequest(ws: WebSocket, relayId: string, msg: { type?: unknown; rid?: unknown; token?: unknown; userId?: unknown }): Promise<void> {
		const rid = typeof msg.rid === "number" ? msg.rid : 0;
		const answer = (body: Record<string, unknown>) => send(ws, JSON.stringify({ type: msg.type, rid, ...body }));
		if (!RELAY_ID.test(relayId)) return answer({ ok: false, error: "Unknown relay" });
		if (!take(this.refill(this.kickRoutes, LIMITS.kickRoutesPerMinute, LIMITS.kickRoutesPerMinute / 60_000), 1))
			return answer({ ok: false, error: "Too many requests; try again in a minute" });
		if (msg.type === "kick-unroute") {
			const userId = typeof msg.userId === "number" && Number.isSafeInteger(msg.userId) && msg.userId > 0 ? msg.userId : 0;
			if (!userId) return answer({ ok: false, error: "No Kick user" });
			await kickRoute(this.env, userId).clear(relayId);
			return answer({ ok: true, userId });
		}
		const account = await kickAccount(typeof msg.token === "string" ? msg.token : "", this.env.KICK_CLIENT_ID ?? "");
		if ("error" in account) return answer({ ok: false, error: account.error });
		await kickRoute(this.env, account.userId).set(relayId);
		return answer({ ok: true, userId: account.userId });
	}

	private fromDevice(ws: WebSocket, ch: number, message: string | ArrayBuffer): void {
		if (typeof message === "string") return close(ws, CLOSE.protocol, "Binary frames only");
		if (message.byteLength > LIMITS.maxDeviceFrame) return close(ws, CLOSE.tooMany, "Frame too large");
		const bucket = this.buckets.get(ch) ?? { tokens: LIMITS.deviceFrameBurst, at: Date.now() };
		this.buckets.set(ch, bucket);
		if (!take(this.refill(bucket, LIMITS.deviceFrameBurst, LIMITS.deviceFrameRate / 1000), 1)) return close(ws, CLOSE.tooMany, "Too many frames");
		const cuebot = this.cuebot();
		if (!cuebot) return close(ws, CLOSE.cuebotLeft, "The streamer's Cuebot went offline");
		this.count(message.byteLength);
		if (this.overDailyCap()) return close(ws, CLOSE.tooMany, "The relay's daily limit was reached");
		send(cuebot, frame(ch, new Uint8Array(message)));
	}

	private left(ws: WebSocket): void {
		const a = ws.deserializeAttachment() as Attachment | null;
		if (a?.role === "device") {
			this.buckets.delete(a.ch);
			const cuebot = this.cuebot();
			if (cuebot) send(cuebot, JSON.stringify({ type: "close", ch: a.ch }));
		} else if (a?.role === "cuebot" && !this.cuebot(ws)) {
			this.identity = null;
			this.closeAllDevices(CLOSE.cuebotLeft, "The streamer's Cuebot went offline");
		}
	}

	private closeAllDevices(code: number, reason: string): void {
		for (const device of this.devices()) close(device, code, reason);
	}

	/** The authenticated Cuebot socket (other than <paramref name="except"/>), if any. */
	private cuebot(except?: WebSocket): WebSocket | undefined {
		return this.ctx.getWebSockets().find((ws) => ws !== except && ws.readyState === WebSocket.OPEN
			&& (ws.deserializeAttachment() as Attachment | null)?.role === "cuebot");
	}

	private identityOf(cuebot: WebSocket): { raw: Bytes; hooks: Bytes } | null {
		if (this.identity) return this.identity;
		const a = cuebot.deserializeAttachment() as Attachment | null;
		if (a?.role !== "cuebot") return null;
		this.identity = { raw: fromBase64Url(a.key), hooks: fromBase64Url(a.hooks) };
		return this.identity;
	}

	private devices(): WebSocket[] {
		return this.ctx.getWebSockets().filter((ws) => ws.readyState === WebSocket.OPEN && (ws.deserializeAttachment() as Attachment | null)?.role === "device");
	}

	private device(ch: number): WebSocket | undefined {
		return this.devices().find((ws) => (ws.deserializeAttachment() as Extract<Attachment, { role: "device" }>).ch === ch);
	}

	private newChannel(): number {
		const used = new Set(this.devices().map((ws) => (ws.deserializeAttachment() as Extract<Attachment, { role: "device" }>).ch));
		for (;;) {
			const ch = crypto.getRandomValues(new Uint32Array(1))[0]!;
			if (ch !== HOOK_CHANNEL && !used.has(ch)) return ch;
		}
	}

	private refill(bucket: Bucket, capacity: number, perMs: number): Bucket {
		const now = Date.now();
		bucket.tokens = Math.min(capacity, bucket.tokens + (now - bucket.at) * perMs);
		bucket.at = now;
		return bucket;
	}

	private count(n: number): void {
		const today = utcDay();
		if (today !== this.day) {
			this.day = today;
			this.bytes = 0;
		}
		this.bytes += n;
	}

	private overDailyCap(): boolean {
		return utcDay() === this.day && this.bytes > LIMITS.dailyBytes;
	}
}

function take(bucket: Bucket, n: number): boolean {
	if (bucket.tokens < n) return false;
	bucket.tokens -= n;
	return true;
}

function utcDay(): number {
	return Math.floor(Date.now() / 86_400_000);
}

function frame(ch: number, payload: Uint8Array): ArrayBuffer {
	const out = new Uint8Array(4 + payload.length);
	new DataView(out.buffer).setUint32(0, ch);
	out.set(payload, 4);
	return out.buffer;
}

/** SHA-256 of the given secret compared in constant time with the hash Cuebot registered. */
async function sameHash(secret: string, expected: Bytes): Promise<boolean> {
	const given = await crypto.subtle.digest("SHA-256", utf8.encode(secret));
	return crypto.subtle.timingSafeEqual(given, expected);
}

function send(ws: WebSocket, data: string | ArrayBuffer): void {
	try {
		ws.send(data);
	} catch {
		// The socket is closing.
	}
}

function close(ws: WebSocket, code: number, reason: string): void {
	try {
		ws.close(code, reason);
	} catch {
		// Already closed.
	}
}

/**
 * Where one Kick broadcaster's events go: the relay ID of the Cuebot that last proved it holds a token of that
 * broadcaster for the built-in app. Named "kick:<user id>". Kept KICK_ROUTE_TTL_MS after the last refresh (Cuebot
 * refreshes it on every relay connection), then forgotten.
 */
export class KickRoute extends DurableObject<Env> {
	async set(relayId: string): Promise<void> {
		await this.ctx.storage.put("route", { relay: relayId, at: Date.now() });
	}

	async get(): Promise<string | null> {
		const route = await this.ctx.storage.get<{ relay: string; at: number }>("route");
		if (!route) return null;
		if (Date.now() - route.at > KICK_ROUTE_TTL_MS) {
			await this.ctx.storage.deleteAll();
			return null;
		}
		return route.relay;
	}

	/** Removes the route when it points to relayId (a Cuebot can only remove its own route). */
	async clear(relayId: string): Promise<boolean> {
		const route = await this.ctx.storage.get<{ relay: string; at: number }>("route");
		if (route?.relay !== relayId) return false;
		await this.ctx.storage.deleteAll();
		return true;
	}
}
