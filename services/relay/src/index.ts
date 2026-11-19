// Cuebot webhook relay.
//
// Services (Ko-fi, Patreon, Shopify, Fourthwall, Throne, Kick, ...) POST webhooks to
//   https://<worker>/hook/<ROUTE_SECRET>/<service>[/<path...>]
// The Worker answers 200 at once and hands the raw request to a single Durable Object (RelayHub), which stores it
// in SQLite and pushes it to the Cuebot app connected at
//   wss://<worker>/connect   (Authorization: Bearer <CONNECT_TOKEN>)
// Cuebot acknowledges each delivery; unacknowledged ones stay buffered and are replayed when Cuebot reconnects.
//
// The relay deliberately does NOT verify service signatures: it forwards the exact bytes and headers, and Cuebot
// checks them with secrets that never leave the user's PC.

import { DurableObject } from "cloudflare:workers";

export interface Env {
	RELAY: DurableObjectNamespace<RelayHub>;
	/** Path segment that makes webhook URLs unguessable. Set with `wrangler secret put ROUTE_SECRET`. */
	ROUTE_SECRET: string;
	/** Bearer token Cuebot sends to /connect. Set with `wrangler secret put CONNECT_TOKEN`. */
	CONNECT_TOKEN: string;
	/** Optional tuning (wrangler.jsonc "vars"). */
	MAX_BUFFERED?: string;
	MAX_AGE_HOURS?: string;
	MAX_BODY_BYTES?: string;
	MAX_BUFFER_BYTES?: string;
}

/** One forwarded webhook, exactly as sent to Cuebot (see README.md, "Protocol"). */
export interface Delivery {
	type: "delivery";
	id: string;
	service: string;
	path: string;
	method: string;
	/** The query string without the leading "?" ("" when none). */
	query: string;
	/** Request headers with lower-case names. */
	headers: Record<string, string>;
	bodyBase64: string;
	/** ISO 8601 UTC time the Worker received the request. */
	receivedAt: string;
}

const PROTOCOL_VERSION = 1;
const SERVICE_NAME = /^[a-z0-9][a-z0-9-]{0,63}$/;
const HOOK_METHODS = new Set(["POST", "GET", "PUT"]);
/** Secrets shorter than this are treated as "not configured" so a forgotten secret never opens the relay. */
const MIN_SECRET_LENGTH = 16;
const DEFAULTS = {
	maxBuffered: 500,
	maxAgeHours: 24,
	maxBodyBytes: 1024 * 1024,
	maxBufferBytes: 256 * 1024 * 1024,
};

function limits(env: Env) {
	const num = (value: string | undefined, fallback: number) => {
		const n = Number(value);
		return Number.isFinite(n) && n > 0 ? n : fallback;
	};
	return {
		maxBuffered: Math.floor(num(env.MAX_BUFFERED, DEFAULTS.maxBuffered)),
		maxAgeMs: num(env.MAX_AGE_HOURS, DEFAULTS.maxAgeHours) * 3600_000,
		maxBodyBytes: Math.floor(num(env.MAX_BODY_BYTES, DEFAULTS.maxBodyBytes)),
		maxBufferBytes: Math.floor(num(env.MAX_BUFFER_BYTES, DEFAULTS.maxBufferBytes)),
	};
}

/** Constant-time comparison (both sides hashed first, so lengths never leak either). */
async function secretMatches(given: string, expected: string | undefined): Promise<boolean> {
	expected = expected?.trim();
	if (!expected || expected.length < MIN_SECRET_LENGTH) return false;
	const enc = new TextEncoder();
	const [a, b] = await Promise.all([
		crypto.subtle.digest("SHA-256", enc.encode(given)),
		crypto.subtle.digest("SHA-256", enc.encode(expected)),
	]);
	return crypto.subtle.timingSafeEqual(a, b);
}

const notFound = () => new Response("Not found", { status: 404 });

function hub(env: Env): DurableObjectStub<RelayHub> {
	return env.RELAY.get(env.RELAY.idFromName("relay"));
}

// Only handlers and the Durable Object class may be exported from this entry module (workerd refuses other values).
export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const url = new URL(request.url);
		if (url.pathname === "/connect") return connect(request, env);
		if (url.pathname.startsWith("/hook/")) return hook(request, url, env);
		if (url.pathname === "/" && request.method === "GET") return new Response("Cuebot relay\n", { headers: { "content-type": "text/plain" } });
		return notFound();
	},
} satisfies ExportedHandler<Env>;

async function connect(request: Request, env: Env): Promise<Response> {
	const auth = request.headers.get("authorization") ?? "";
	const token = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
	if (!(await secretMatches(token, env.CONNECT_TOKEN))) return new Response("Unauthorized", { status: 401 });
	if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("Expected a WebSocket upgrade", { status: 426 });
	return hub(env).fetch(request);
}

async function hook(request: Request, url: URL, env: Env): Promise<Response> {
	// /hook/<secret>/<service>[/<path...>]
	const [, , secret = "", service = "", ...rest] = url.pathname.split("/");
	if (!(await secretMatches(secret, env.ROUTE_SECRET))) return notFound();
	if (!SERVICE_NAME.test(service)) return notFound();
	if (!HOOK_METHODS.has(request.method)) return new Response("Method not allowed", { status: 405, headers: { allow: "GET, POST, PUT" } });

	const { maxBodyBytes } = limits(env);
	const declared = Number(request.headers.get("content-length") ?? "0");
	if (declared > maxBodyBytes) return new Response("Payload too large", { status: 413 });
	const body = await readLimited(request, maxBodyBytes);
	if (body === null) return new Response("Payload too large", { status: 413 });

	const headers: Record<string, string> = {};
	request.headers.forEach((value, name) => {
		headers[name.toLowerCase()] = value;
	});
	const delivery: Delivery = {
		type: "delivery",
		id: crypto.randomUUID(),
		service,
		path: rest.join("/"),
		method: request.method,
		query: url.search.startsWith("?") ? url.search.slice(1) : url.search,
		headers,
		bodyBase64: toBase64(body),
		receivedAt: new Date().toISOString(),
	};
	await hub(env).deliver(delivery);
	// Every supported service only needs a quick 2xx; an empty body is fine for all of them.
	return new Response(null, { status: 200 });
}

/** Reads the body, or returns null as soon as it exceeds the limit (chunked uploads have no content-length). */
async function readLimited(request: Request, max: number): Promise<Uint8Array | null> {
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
	let offset = 0;
	for (const c of chunks) {
		out.set(c, offset);
		offset += c.byteLength;
	}
	return out;
}

function toBase64(bytes: Uint8Array): string {
	let binary = "";
	const step = 0x8000;
	for (let i = 0; i < bytes.length; i += step) binary += String.fromCharCode(...bytes.subarray(i, i + step));
	return btoa(binary);
}

/**
 * The one relay instance. Holds the Cuebot WebSocket through the Hibernation API (an idle connection costs nothing:
 * the object is evicted from memory while the socket stays open, and the ping/pong heartbeat is answered by the
 * runtime without waking it) and buffers unacknowledged deliveries in SQLite.
 */
export class RelayHub extends DurableObject<Env> {
	private readonly sql: SqlStorage;

	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		this.sql = ctx.storage.sql;
		this.sql.exec(`CREATE TABLE IF NOT EXISTS deliveries (
			seq INTEGER PRIMARY KEY AUTOINCREMENT,
			id TEXT NOT NULL UNIQUE,
			received_at INTEGER NOT NULL,
			size INTEGER NOT NULL,
			json TEXT NOT NULL)`);
		ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('{"type":"ping"}', '{"type":"pong"}'));
	}

	/** Stores a delivery (dropping the oldest beyond the caps) and pushes it to Cuebot when connected. */
	async deliver(delivery: Delivery): Promise<void> {
		const json = JSON.stringify(delivery);
		this.sql.exec(
			"INSERT INTO deliveries (id, received_at, size, json) VALUES (?, ?, ?, ?)",
			delivery.id,
			Date.parse(delivery.receivedAt),
			json.length,
			json,
		);
		this.prune();
		for (const ws of this.ctx.getWebSockets()) {
			try {
				ws.send(json);
			} catch {
				// The socket is closing; the delivery stays buffered for the next connection.
			}
		}
	}

	/** Number of buffered (unacknowledged) deliveries. */
	async buffered(): Promise<number> {
		return this.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM deliveries").one().n;
	}

	/** Ids of buffered deliveries, oldest first. */
	async bufferedIds(): Promise<string[]> {
		return this.sql.exec<{ id: string }>("SELECT id FROM deliveries ORDER BY seq").toArray().map((r) => r.id);
	}

	/** Called by the Worker for an authorized /connect upgrade: replaces any previous Cuebot connection. */
	async fetch(_request: Request): Promise<Response> {
		for (const old of this.ctx.getWebSockets()) {
			try {
				old.close(4000, "Replaced by a newer connection");
			} catch {
				// Already closed.
			}
		}
		const pair = new WebSocketPair();
		const [client, server] = [pair[0], pair[1]];
		this.ctx.acceptWebSocket(server);
		this.prune();
		const pending = this.sql.exec<{ json: string }>("SELECT json FROM deliveries ORDER BY seq").toArray();
		server.send(JSON.stringify({ type: "hello", version: PROTOCOL_VERSION, buffered: pending.length }));
		for (const row of pending) server.send(row.json);
		return new Response(null, { status: 101, webSocket: client });
	}

	async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
		if (typeof message !== "string") return;
		let msg: { type?: unknown; id?: unknown; ids?: unknown };
		try {
			msg = JSON.parse(message);
		} catch {
			return;
		}
		if (msg.type === "ack") {
			const ids = Array.isArray(msg.ids) ? msg.ids : [msg.id];
			for (const id of ids) if (typeof id === "string") this.sql.exec("DELETE FROM deliveries WHERE id = ?", id);
		} else if (msg.type === "ping") {
			ws.send('{"type":"pong"}');
		}
	}

	async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
		try {
			ws.close(code === 1005 || code === 1006 ? 1000 : code, reason);
		} catch {
			// Already closed (the runtime answers close frames itself on recent compatibility dates).
		}
	}

	async webSocketError(ws: WebSocket): Promise<void> {
		try {
			ws.close(1011, "error");
		} catch {
			// Already closed.
		}
	}

	/** Drops deliveries older than the age cap, then the oldest beyond the count and size caps. */
	private prune(): void {
		const { maxBuffered, maxAgeMs, maxBufferBytes } = limits(this.env);
		this.sql.exec("DELETE FROM deliveries WHERE received_at < ?", Date.now() - maxAgeMs);
		this.sql.exec(
			"DELETE FROM deliveries WHERE seq NOT IN (SELECT seq FROM deliveries ORDER BY seq DESC LIMIT ?)",
			maxBuffered,
		);
		let total = 0;
		let cutoff: number | null = null;
		for (const row of this.sql.exec<{ seq: number; size: number }>("SELECT seq, size FROM deliveries ORDER BY seq DESC")) {
			total += row.size;
			if (total > maxBufferBytes) {
				cutoff = row.seq;
				break;
			}
		}
		if (cutoff !== null) this.sql.exec("DELETE FROM deliveries WHERE seq <= ?", cutoff);
	}
}
