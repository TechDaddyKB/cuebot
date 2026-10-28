/**
 * Cuebot's Stream Deck protocol (version 1). The plugin connects to ws://host:port/api/v1/streamdeck on Cuebot's
 * REST API listener with "Authorization: Bearer <token>". See src/Cuebot.StreamDeck/PROTOCOL.md in the Cuebot repo.
 * Nothing in this file touches the Stream Deck SDK, so it is unit-tested directly.
 */

export const PROTOCOL_VERSION = 1;
export const PLUGIN_UUID = "io.github.cuebot.streamdeck";
export const DEFAULT_HOST = "127.0.0.1";
export const DEFAULT_PORT = 7475;

/** What a key or dial does (the plugin action it is). */
export type ActionKind = "run" | "toggle" | "global" | "trigger";

export const ACTION_UUIDS: Record<ActionKind, string> = {
	run: `${PLUGIN_UUID}.run`,
	toggle: `${PLUGIN_UUID}.toggle`,
	global: `${PLUGIN_UUID}.global`,
	trigger: `${PLUGIN_UUID}.trigger`,
};

export function kindOf(actionUuid: string): ActionKind | undefined {
	return (Object.keys(ACTION_UUIDS) as ActionKind[]).find((k) => ACTION_UUIDS[k] === actionUuid);
}

/** Connection settings, stored in the plugin's global settings (shared by every key). */
export type GlobalSettings = {
	host?: string;
	port?: number | string;
	token?: string;
};

/** Per-key settings (one object for all action kinds; each uses its own fields). */
export type KeySettings = {
	/** Cuebot button id: sub-actions (Set Title, Set State, ...) target keys by it. Assigned on first use. */
	buttonId?: string;
	/** run / toggle: the Cuebot action's id (name kept for display and as a fallback). */
	actionId?: string;
	actionName?: string;
	/** run / trigger: extra arguments, one name=value per line (or a JSON object). */
	args?: string;
	/** run: fire on key down (default) or key up. */
	runOn?: "down" | "up";
	/** toggle: what a press does. */
	mode?: "toggle" | "enable" | "disable";
	/** global: variable name, value, operation, persistence, dial step and whether the key shows the value. */
	variable?: string;
	value?: string;
	op?: "set" | "add" | "toggle";
	persisted?: boolean;
	step?: string;
	showValue?: boolean;
};

export type Connection = { host: string; port: number; token: string; url: string };

/** Normalizes global settings into a connection, or a reason it cannot connect yet. */
export function connectionFrom(settings: GlobalSettings): Connection | { error: string } {
	const host = (settings.host ?? "").toString().trim() || DEFAULT_HOST;
	const portText = (settings.port ?? "").toString().trim();
	const port = portText.length === 0 ? DEFAULT_PORT : Number(portText);
	const token = (settings.token ?? "").toString().trim();
	if (!Number.isInteger(port) || port < 1 || port > 65535) return { error: `Port "${portText}" is not a valid port` };
	if (token.length === 0) return { error: "Paste the API token from Cuebot (Stream Deck page) into the plugin settings" };
	if (/[\s/]/.test(host)) return { error: `Host "${host}" is not a host name or IP address` };
	const hostPart = host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
	return { host, port, token, url: `ws://${hostPart}:${port}/api/v1/streamdeck` };
}

export type DeviceInfo = { id: string; name: string; type: number; size: { columns: number; rows: number }; connected?: boolean };

export type ButtonInfo = {
	context: string;
	buttonId: string;
	kind: ActionKind;
	controller: "Keypad" | "Encoder";
	device: string;
	row?: number;
	column?: number;
	state: number;
	isInMultiAction: boolean;
};

export type InputEvent = "keyDown" | "keyUp" | "dialDown" | "dialUp" | "dialRotate" | "touchTap";

// ---- Messages: plugin → Cuebot -------------------------------------------------------------------------------------

export type HelloMessage = {
	type: "hello";
	protocol: number;
	plugin: { uuid: string; version: string };
	app: { version: string; platform: string; language: string };
	devices: DeviceInfo[];
	buttons: ButtonInfo[];
};

export type InputMessage = {
	type: "input";
	event: InputEvent;
	context: string;
	buttonId: string;
	controller: "Keypad" | "Encoder";
	device: string;
	state?: number;
	ticks?: number;
	pressed?: boolean;
	hold?: boolean;
	tapPos?: [number, number];
	args?: Record<string, string>;
};

export type RequestMethod = "getActions" | "runAction" | "setActionEnabled" | "getGlobals" | "setGlobal";

// ---- Messages: Cuebot → plugin -------------------------------------------------------------------------------------

export type CommandName = "setTitle" | "setState" | "setImage" | "setValue" | "showAlert" | "showOk" | "toggleState";

export type CommandMessage = {
	type: "command";
	command: CommandName;
	buttonId: string;
	title?: string;
	state?: number;
	image?: string;
	value?: string;
	indicator?: number;
};

export type ActionSummary = { id: string; name: string; group: string; enabled: boolean };

export type ServerMessage =
	| { type: "welcome"; protocol: number; session: string; app: { name: string; version: string } }
	| { type: "response"; id: number; ok: boolean; result?: unknown; error?: string }
	| CommandMessage
	| { type: "actionsChanged"; actions: ActionSummary[] }
	| { type: "globalChanged"; name: string; value: string | null }
	| { type: "pong" };

/** Parses a server message; null for anything that is not a JSON object with a string type. */
export function parseServerMessage(text: string): ServerMessage | null {
	try {
		const value: unknown = JSON.parse(text);
		if (value === null || typeof value !== "object" || Array.isArray(value)) return null;
		const type = (value as { type?: unknown }).type;
		return typeof type === "string" ? (value as ServerMessage) : null;
	} catch {
		return null;
	}
}

// ---- Helpers -------------------------------------------------------------------------------------------------------

/**
 * Key arguments: one name=value per line (blank lines and lines starting with # are skipped), or a JSON object.
 * Values stay text; Cuebot types them like any other argument.
 */
export function parseArgs(text: string | undefined): Record<string, string> {
	const result: Record<string, string> = {};
	const trimmed = (text ?? "").trim();
	if (trimmed.length === 0) return result;
	if (trimmed.startsWith("{")) {
		try {
			const parsed: unknown = JSON.parse(trimmed);
			if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
				for (const [k, v] of Object.entries(parsed)) result[k] = typeof v === "string" ? v : JSON.stringify(v);
				return result;
			}
		} catch {
			// fall through to name=value parsing
		}
	}
	for (const raw of trimmed.split(/\r?\n/)) {
		const line = raw.trim();
		if (line.length === 0 || line.startsWith("#")) continue;
		const eq = line.indexOf("=");
		if (eq <= 0) continue;
		result[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
	}
	return result;
}

/** A short, readable button id, e.g. "key-7f3a9c". */
export function newButtonId(kind: ActionKind, random: () => number = Math.random): string {
	const prefix = kind === "trigger" ? "key" : kind;
	let suffix = "";
	for (let i = 0; i < 6; i++) suffix += Math.floor(random() * 16).toString(16);
	return `${prefix}-${suffix}`;
}

/** Button ids are matched case-insensitively and without surrounding spaces. */
export function normalizeId(id: string | undefined): string {
	return (id ?? "").trim().toLowerCase();
}

/** "Group / Name" labels for an action picker, sorted by group then name. */
export function actionChoices(actions: ActionSummary[]): { value: string; label: string; enabled: boolean }[] {
	return [...actions]
		.sort((a, b) => (a.group || "").localeCompare(b.group || "") || a.name.localeCompare(b.name))
		.map((a) => ({ value: a.id, label: a.group ? `${a.group} / ${a.name}` : a.name, enabled: a.enabled }));
}
