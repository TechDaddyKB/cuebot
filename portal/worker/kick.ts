// Cuebot's shared Kick event intake (portal/RELAY.md, "Kick events"; src/Cuebot.Kick/PROTOCOL.md section 3).
//
// Kick sends every webhook of an app to that app's one address, so the events of every streamer on Cuebot's built-in
// Kick app arrive at hooks.<domain>/kick. This module checks that a delivery really comes from Kick (its signature over
// "<message id>.<timestamp>.<raw body>", RSA PKCS#1 v1.5 SHA-256 with the key from api.kick.com) and that a Cuebot
// asking for a broadcaster's events holds a valid token of that broadcaster for the built-in app.

/** Kick's addresses and the fetch used to reach them; the tests replace them. */
export const kick = {
	api: "https://api.kick.com",
	id: "https://id.kick.com",
	fetch: (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => fetch(input, init),
};

/** How long Kick's public key is reused before it is fetched again (it is also fetched again when a check fails). */
const KEY_LIFETIME_MS = 60 * 60 * 1000;
/** Deliveries older or newer than this are dropped, so a captured delivery cannot be replayed later. */
export const MAX_DELIVERY_AGE_MS = 10 * 60 * 1000;

let cachedKey: { key: CryptoKey; at: number } | null = null;

/** Forgets the cached key (tests). */
export function resetKickKey(): void {
	cachedKey = null;
}

async function publicKey(renew: boolean): Promise<CryptoKey | null> {
	if (!renew && cachedKey && Date.now() - cachedKey.at < KEY_LIFETIME_MS) return cachedKey.key;
	try {
		const response = await kick.fetch(`${kick.api}/public/v1/public-key`, { headers: { accept: "application/json" } });
		if (!response.ok) return null;
		const body = (await response.json()) as { data?: { public_key?: unknown } };
		const pem = typeof body.data?.public_key === "string" ? body.data.public_key : "";
		const der = pemToDer(pem);
		if (!der) return null;
		const key = await crypto.subtle.importKey("spki", der, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
		cachedKey = { key, at: Date.now() };
		return key;
	} catch {
		return null;
	}
}

function pemToDer(pem: string): ArrayBuffer | null {
	const b64 = pem.replace(/-----(BEGIN|END) PUBLIC KEY-----/g, "").replace(/\s+/g, "");
	if (!b64) return null;
	try {
		const bin = atob(b64);
		const out = new Uint8Array(bin.length);
		for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
		return out.buffer;
	} catch {
		return null;
	}
}

function fromBase64(s: string): Uint8Array | null {
	try {
		const bin = atob(s.trim());
		const out = new Uint8Array(bin.length);
		for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
		return out;
	} catch {
		return null;
	}
}

/** True when the delivery was signed by Kick (fetching the key again once if the cached one fails). */
export async function verifyKickSignature(messageId: string, timestamp: string, body: Uint8Array, signature: string): Promise<boolean> {
	const sig = fromBase64(signature);
	if (!messageId || !timestamp || !sig) return false;
	const prefix = new TextEncoder().encode(`${messageId}.${timestamp}.`);
	const signed = new Uint8Array(prefix.length + body.length);
	signed.set(prefix, 0);
	signed.set(body, prefix.length);
	for (const renew of [false, true]) {
		const key = await publicKey(renew);
		if (!key) return false;
		if (await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, sig, signed)) return true;
	}
	return false;
}

/** True when the delivery's RFC 3339 timestamp is within MAX_DELIVERY_AGE_MS of now. */
export function isFresh(timestamp: string, now = Date.now()): boolean {
	const at = Date.parse(timestamp);
	return Number.isFinite(at) && Math.abs(now - at) <= MAX_DELIVERY_AGE_MS;
}

/**
 * The Kick user a token belongs to, after checking with Kick that it is an active user token of the built-in app
 * (token introspection), then reading the user (GET /public/v1/users). The token is used for these two calls only.
 */
export async function kickAccount(token: string, clientId: string): Promise<{ userId: number } | { error: string }> {
	if (!clientId) return { error: "Kick events are not set up on this relay" };
	if (!token || token.length > 4096) return { error: "No Kick token" };
	const auth = { authorization: `Bearer ${token}`, accept: "application/json" };
	try {
		const introspect = await kick.fetch(`${kick.id}/oauth/token/introspect`, { method: "POST", headers: auth });
		if (!introspect.ok) return { error: "Kick did not accept the token" };
		const info = ((await introspect.json()) as { data?: { active?: unknown; client_id?: unknown; token_type?: unknown } }).data;
		if (info?.active !== true) return { error: "The Kick token is no longer valid" };
		if (info.client_id !== clientId) return { error: "The token is not for Cuebot's Kick app" };
		if (info.token_type !== undefined && info.token_type !== "user") return { error: "Not a user token" };
		const users = await kick.fetch(`${kick.api}/public/v1/users`, { headers: auth });
		if (!users.ok) return { error: "Kick did not return the user" };
		const data = ((await users.json()) as { data?: { user_id?: unknown }[] }).data;
		const userId = data?.[0]?.user_id;
		return typeof userId === "number" && Number.isSafeInteger(userId) && userId > 0 ? { userId } : { error: "Kick did not return the user" };
	} catch {
		return { error: "Kick could not be reached" };
	}
}

/** The broadcaster a Kick event is for (every event body has broadcaster.user_id), or null. */
export function broadcasterOf(body: Uint8Array): number | null {
	try {
		const json = JSON.parse(new TextDecoder().decode(body)) as { broadcaster?: { user_id?: unknown } };
		const id = json?.broadcaster?.user_id;
		return typeof id === "number" && Number.isSafeInteger(id) && id > 0 ? id : null;
	} catch {
		return null;
	}
}
