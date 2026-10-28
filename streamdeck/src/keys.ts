import { type ButtonInfo, type CommandMessage, normalizeId } from "./protocol.js";

/** What a visible key or dial can show; the Stream Deck SDK action in the plugin, a fake in tests. */
export interface KeyTarget {
	readonly isDial: boolean;
	setTitle(title: string | undefined, state?: number): Promise<void> | void;
	setState(state: number): Promise<void> | void;
	setImage(image: string | undefined, state?: number): Promise<void> | void;
	setFeedback(payload: Record<string, unknown>): Promise<void> | void;
	showAlert(): Promise<void> | void;
	showOk(): Promise<void> | void;
}

type Entry = { info: ButtonInfo; target: KeyTarget };

export type RegistryOptions = {
	/** Turns an http(s) image URL into a data URI (keys cannot load URLs themselves). */
	loadImage?: (url: string) => Promise<string>;
	/** Called when a command cannot be applied (e.g. an image URL fails to load). */
	onError?: (message: string) => void;
};

/**
 * The visible Cuebot keys and dials, by Stream Deck context. Applies Cuebot's commands to every key with the button id
 * and remembers the latest appearance per button id, so keys on another page (or in another profile) show it when they
 * appear.
 */
export class KeyRegistry {
	readonly #entries = new Map<string, Entry>();
	/** button id → command key ("setTitle:1") → latest command. */
	readonly #appearance = new Map<string, Map<string, CommandMessage>>();
	readonly #options: RegistryOptions;

	constructor(options: RegistryOptions = {}) {
		this.#options = options;
	}

	get size(): number {
		return this.#entries.size;
	}

	list(): ButtonInfo[] {
		return [...this.#entries.values()].map((e) => ({ ...e.info }));
	}

	get(context: string): ButtonInfo | undefined {
		const entry = this.#entries.get(context);
		return entry ? { ...entry.info } : undefined;
	}

	targets(predicate: (info: ButtonInfo) => boolean): { info: ButtonInfo; target: KeyTarget }[] {
		return [...this.#entries.values()].filter((e) => predicate(e.info)).map((e) => ({ info: { ...e.info }, target: e.target }));
	}

	/** Adds (or replaces) a key and shows the remembered appearance for its button id. */
	async add(info: ButtonInfo, target: KeyTarget): Promise<void> {
		this.#entries.set(info.context, { info: { ...info }, target });
		await this.#replay(info.context);
	}

	/** Changes a key's details (e.g. a new button id from its settings); replays the appearance when the id changed. */
	async update(context: string, changes: Partial<Omit<ButtonInfo, "context">>): Promise<void> {
		const entry = this.#entries.get(context);
		if (!entry) return;
		const idChanged = changes.buttonId !== undefined && normalizeId(changes.buttonId) !== normalizeId(entry.info.buttonId);
		entry.info = { ...entry.info, ...changes };
		if (idChanged) await this.#replay(context);
	}

	remove(context: string): boolean {
		return this.#entries.delete(context);
	}

	/** Records a state the Stream Deck app reported (e.g. after a press). */
	setLocalState(context: string, state: number): void {
		const entry = this.#entries.get(context);
		if (entry) entry.info.state = state;
	}

	/** Forgets remembered appearance (e.g. when Cuebot reconnects and will push fresh state). */
	clearAppearance(): void {
		this.#appearance.clear();
	}

	/** Applies a command to every key with its button id; returns how many keys it reached. */
	async apply(command: CommandMessage): Promise<number> {
		const id = normalizeId(command.buttonId);
		if (id.length === 0) return 0;
		const matches = [...this.#entries.values()].filter((e) => normalizeId(e.info.buttonId) === id);
		let resolved = command;
		if (command.command === "toggleState") {
			const remembered = this.#appearance.get(id)?.get("setState:")?.state;
			const current = remembered ?? matches[0]?.info.state ?? 0;
			resolved = { type: "command", command: "setState", buttonId: command.buttonId, state: current === 0 ? 1 : 0 };
		}
		if (resolved.command === "setImage" && resolved.image && /^https?:\/\//i.test(resolved.image)) {
			const loaded = await this.#load(resolved.image);
			if (loaded === null) {
				for (const m of matches) await safe(() => m.target.showAlert());
				return matches.length;
			}
			resolved = { ...resolved, image: loaded };
		}
		this.#remember(id, resolved);
		for (const m of matches) await this.#applyTo(m, resolved);
		return matches.length;
	}

	async #replay(context: string): Promise<void> {
		const entry = this.#entries.get(context);
		if (!entry) return;
		const remembered = this.#appearance.get(normalizeId(entry.info.buttonId));
		if (!remembered) return;
		for (const command of remembered.values()) await this.#applyTo(entry, command);
	}

	#remember(id: string, command: CommandMessage): void {
		if (command.command === "showAlert" || command.command === "showOk") return;
		let map = this.#appearance.get(id);
		if (!map) {
			map = new Map();
			this.#appearance.set(id, map);
		}
		const key = command.command === "setState" || command.command === "setValue" ? `${command.command}:` : `${command.command}:${command.state ?? ""}`;
		// An all-states title/image replaces the per-state ones.
		if ((command.command === "setTitle" || command.command === "setImage") && command.state === undefined) {
			for (const k of [...map.keys()]) if (k.startsWith(`${command.command}:`)) map.delete(k);
		}
		map.set(key, command);
	}

	async #applyTo(entry: Entry, c: CommandMessage): Promise<void> {
		const t = entry.target;
		switch (c.command) {
			case "setTitle":
				await safe(() => t.setTitle(emptyToUndefined(c.title), t.isDial ? undefined : c.state));
				break;
			case "setImage":
				await safe(() => t.setImage(emptyToUndefined(c.image), t.isDial ? undefined : c.state));
				break;
			case "setState": {
				const state = c.state === 1 ? 1 : 0;
				entry.info.state = state;
				if (!t.isDial) await safe(() => t.setState(state));
				break;
			}
			case "setValue":
				if (t.isDial) {
					const payload: Record<string, unknown> = { value: c.value ?? "" };
					if (typeof c.indicator === "number") payload.indicator = Math.max(0, Math.min(100, c.indicator));
					await safe(() => t.setFeedback(payload));
				} else {
					await safe(() => t.setTitle(emptyToUndefined(c.value)));
				}
				break;
			case "showAlert":
				await safe(() => t.showAlert());
				break;
			case "showOk":
				// Dials have no OK overlay; flash nothing rather than an alert.
				if (!t.isDial) await safe(() => t.showOk());
				break;
			default:
				break;
		}
	}

	async #load(url: string): Promise<string | null> {
		if (!this.#options.loadImage) return url;
		try {
			return await this.#options.loadImage(url);
		} catch (error) {
			this.#options.onError?.(`Could not load image ${url}: ${error instanceof Error ? error.message : String(error)}`);
			return null;
		}
	}
}

function emptyToUndefined(text: string | undefined): string | undefined {
	return text === undefined || text.length === 0 ? undefined : text;
}

async function safe(fn: () => Promise<void> | void): Promise<void> {
	try {
		await fn();
	} catch {
		// The key went away between the lookup and the call; nothing to do.
	}
}

/** Downloads an image and returns it as a data URI (max 2 MB, image/* only). */
export async function fetchImageAsDataUri(url: string, fetcher: typeof fetch = fetch, maxBytes = 2 * 1024 * 1024): Promise<string> {
	const response = await fetcher(url, { signal: AbortSignal.timeout(10_000) });
	if (!response.ok) throw new Error(`HTTP ${response.status}`);
	const type = (response.headers.get("content-type") ?? "").split(";")[0]!.trim().toLowerCase();
	if (!type.startsWith("image/")) throw new Error(`not an image (${type || "no content type"})`);
	const bytes = new Uint8Array(await response.arrayBuffer());
	if (bytes.byteLength > maxBytes) throw new Error(`image is larger than ${Math.round(maxBytes / 1024)} KB`);
	return `data:${type};base64,${Buffer.from(bytes).toString("base64")}`;
}
