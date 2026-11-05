// Constants of the shared relay protocol (portal/RELAY.md). Kept out of index.ts: a Worker's entry module may only
// export handlers and Durable Object classes.

export const PROTOCOL_VERSION = 1;
export const CONNECT_LABEL = "cuebot-relay-connect-v1";
/** Channel 0 carries sealed webhooks to Cuebot; mod devices get random non-zero channels. */
export const HOOK_CHANNEL = 0;

/** Abuse limits per relay ID (section 10). In memory only: they reset when the object is evicted. */
export const LIMITS = {
	maxDevices: 20,
	/** Largest frame a device may send; uploads are chunked by the portal. */
	maxDeviceFrame: 64 * 1024,
	/** Largest frame Cuebot may send to a device (the portal UI bundle is chunked too). */
	maxCuebotFrame: 1024 * 1024,
	/** Frames per second per device, with this burst. */
	deviceFrameRate: 50,
	deviceFrameBurst: 200,
	/** Device connection attempts per minute. */
	connectsPerMinute: 60,
	/** Webhooks per minute. */
	hooksPerMinute: 300,
	maxHookBody: 1024 * 1024,
	/** Bytes per UTC day through the relay, both directions. */
	dailyBytes: 4 * 1024 * 1024 * 1024,
	/** Cuebot must answer the challenge within this. */
	authTimeoutMs: 15_000,
} as const;

/** Close codes (portal/RELAY.md). */
export const CLOSE = {
	replaced: 4000,
	authFailed: 4001,
	authTimeout: 4002,
	protocol: 4003,
	cuebotOffline: 4404,
	tooMany: 4429,
	cuebotLeft: 4503,
} as const;
