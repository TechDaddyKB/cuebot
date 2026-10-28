import {
	type ActionSummary,
	type CommandMessage,
	type Connection,
	type HelloMessage,
	type InputMessage,
	parseServerMessage,
	PROTOCOL_VERSION,
	type RequestMethod,
	type ServerMessage,
} from "./protocol.js";

/** The subset of the `ws` WebSocket the client uses (a fake in tests). */
export interface SocketLike {
	send(text: string): void;
	close(code?: number, reason?: string): void;
	on(event: "open", listener: () => void): unknown;
	on(event: "message", listener: (data: unknown) => void): unknown;
	on(event: "close", listener: (code: number) => void): unknown;
	on(event: "error", listener: (error: Error) => void): unknown;
}

export type SocketFactory = (url: string, token: string) => SocketLike;

export type Status = { state: "disconnected" | "connecting" | "connected" | "error"; message: string; cuebotVersion?: string };

export type ClientOptions = {
	socket: SocketFactory;
	/** The hello message (devices and visible keys) to send whenever a connection opens. */
	hello: () => Omit<HelloMessage, "type" | "protocol">;
	setTimer?: (fn: () => void, ms: number) => unknown;
	clearTimer?: (handle: unknown) => void;
	requestTimeoutMs?: number;
	maxBackoffMs?: number;
};

type Pending = { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: unknown };

/**
 * One connection to Cuebot with automatic reconnects (1 s doubling to 30 s). Requests resolve with the result or
 * reject with Cuebot's error text. Commands and pushes go to the listeners.
 */
export class CuebotClient {
	readonly #options: Required<Omit<ClientOptions, "socket" | "hello">> & Pick<ClientOptions, "socket" | "hello">;
	readonly #pending = new Map<number, Pending>();
	readonly #statusListeners: ((status: Status) => void)[] = [];
	readonly #commandListeners: ((command: CommandMessage) => void)[] = [];
	readonly #actionsListeners: ((actions: ActionSummary[]) => void)[] = [];
	readonly #globalListeners: ((name: string, value: string | null) => void)[] = [];
	#connection: Connection | null = null;
	#socket: SocketLike | null = null;
	#reconnectTimer: unknown = null;
	#attempts = 0;
	#nextId = 1;
	#status: Status = { state: "disconnected", message: "Not configured" };
	#welcomed = false;

	constructor(options: ClientOptions) {
		this.#options = {
			setTimer: (fn, ms) => setTimeout(fn, ms),
			clearTimer: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
			requestTimeoutMs: 10_000,
			maxBackoffMs: 30_000,
			...options,
		};
	}

	get status(): Status {
		return this.#status;
	}

	get isConnected(): boolean {
		return this.#status.state === "connected";
	}

	onStatus(listener: (status: Status) => void): void {
		this.#statusListeners.push(listener);
	}

	onCommand(listener: (command: CommandMessage) => void): void {
		this.#commandListeners.push(listener);
	}

	onActionsChanged(listener: (actions: ActionSummary[]) => void): void {
		this.#actionsListeners.push(listener);
	}

	onGlobalChanged(listener: (name: string, value: string | null) => void): void {
		this.#globalListeners.push(listener);
	}

	/** Connects to (or reconnects with) new settings; an error string stops and shows it. */
	configure(connection: Connection | { error: string }): void {
		if ("error" in connection) {
			this.#connection = null;
			this.#teardown();
			this.#setStatus({ state: "error", message: connection.error });
			return;
		}
		const same = this.#connection && this.#connection.url === connection.url && this.#connection.token === connection.token;
		this.#connection = connection;
		if (same && this.#socket) return;
		this.#attempts = 0;
		this.#teardown();
		this.#open();
	}

	/** Drops the connection and connects again now. */
	reconnect(): void {
		if (!this.#connection) return;
		this.#attempts = 0;
		this.#teardown();
		this.#open();
	}

	dispose(): void {
		this.#connection = null;
		this.#teardown();
		this.#setStatus({ state: "disconnected", message: "Stopped" });
	}

	/** Sends a message when connected; returns false otherwise. */
	send(message: object): boolean {
		if (!this.#socket || !this.#welcomed) return false;
		try {
			this.#socket.send(JSON.stringify(message));
			return true;
		} catch {
			return false;
		}
	}

	sendInput(message: Omit<InputMessage, "type">): boolean {
		return this.send({ type: "input", ...message });
	}

	request<T = unknown>(method: RequestMethod, params: object = {}): Promise<T> {
		if (!this.#socket || !this.#welcomed) return Promise.reject(new Error(this.#status.message || "Not connected to Cuebot"));
		const id = this.#nextId++;
		return new Promise<T>((resolve, reject) => {
			const timer = this.#options.setTimer(() => {
				this.#pending.delete(id);
				reject(new Error(`Cuebot did not answer ${method} in time`));
			}, this.#options.requestTimeoutMs);
			this.#pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
			if (!this.send({ type: "request", id, method, params })) {
				this.#options.clearTimer(timer);
				this.#pending.delete(id);
				reject(new Error("Not connected to Cuebot"));
			}
		});
	}

	#open(): void {
		const connection = this.#connection;
		if (!connection) return;
		this.#setStatus({ state: "connecting", message: `Connecting to ${connection.host}:${connection.port}...` });
		let socket: SocketLike;
		try {
			socket = this.#options.socket(connection.url, connection.token);
		} catch (error) {
			this.#failed(errorText(error));
			return;
		}
		this.#socket = socket;
		this.#welcomed = false;
		let lastError = "";
		socket.on("open", () => {
			if (this.#socket !== socket) return;
			socket.send(JSON.stringify({ type: "hello", protocol: PROTOCOL_VERSION, ...this.#options.hello() }));
		});
		socket.on("message", (data) => {
			if (this.#socket !== socket) return;
			const message = parseServerMessage(typeof data === "string" ? data : String(data));
			if (message) this.#handle(message);
		});
		socket.on("error", (error) => {
			lastError = friendlyError(error);
		});
		socket.on("close", () => {
			if (this.#socket !== socket) return;
			this.#socket = null;
			this.#welcomed = false;
			this.#rejectPending("Connection to Cuebot closed");
			this.#failed(lastError || "Cuebot closed the connection");
		});
	}

	#handle(message: ServerMessage): void {
		switch (message.type) {
			case "welcome":
				this.#welcomed = true;
				this.#attempts = 0;
				this.#setStatus({
					state: "connected",
					message:
						message.protocol === PROTOCOL_VERSION
							? `Connected to Cuebot ${message.app?.version ?? ""}`.trim()
							: `Connected, but Cuebot speaks protocol ${message.protocol} (plugin: ${PROTOCOL_VERSION}); update the plugin`,
					cuebotVersion: message.app?.version,
				});
				break;
			case "response": {
				const pending = this.#pending.get(message.id);
				if (!pending) return;
				this.#pending.delete(message.id);
				this.#options.clearTimer(pending.timer);
				if (message.ok) pending.resolve(message.result);
				else pending.reject(new Error(message.error ?? "Request failed"));
				break;
			}
			case "command":
				for (const l of this.#commandListeners) l(message);
				break;
			case "actionsChanged":
				for (const l of this.#actionsListeners) l(message.actions ?? []);
				break;
			case "globalChanged":
				for (const l of this.#globalListeners) l(message.name, message.value ?? null);
				break;
			default:
				break;
		}
	}

	#failed(reason: string): void {
		if (!this.#connection) return;
		const delay = Math.min(this.#options.maxBackoffMs, 1000 * 2 ** this.#attempts);
		this.#attempts++;
		this.#setStatus({ state: "error", message: `${reason}; retrying in ${Math.round(delay / 1000)} s` });
		this.#reconnectTimer = this.#options.setTimer(() => {
			this.#reconnectTimer = null;
			this.#open();
		}, delay);
	}

	#teardown(): void {
		if (this.#reconnectTimer !== null) {
			this.#options.clearTimer(this.#reconnectTimer);
			this.#reconnectTimer = null;
		}
		const socket = this.#socket;
		this.#socket = null;
		this.#welcomed = false;
		this.#rejectPending("Connection to Cuebot closed");
		if (socket) {
			try {
				socket.close(1000, "reconfigured");
			} catch {
				// already closed
			}
		}
	}

	#rejectPending(reason: string): void {
		for (const [id, p] of this.#pending) {
			this.#options.clearTimer(p.timer);
			p.reject(new Error(reason));
			this.#pending.delete(id);
		}
	}

	#setStatus(status: Status): void {
		this.#status = status;
		for (const l of this.#statusListeners) l(status);
	}
}

function errorText(error: unknown): string {
	return error instanceof Error ? error.message : String(error);
}

/** Turns socket errors into something a streamer can act on. */
export function friendlyError(error: Error): string {
	const text = error.message ?? String(error);
	if (/\b401\b/.test(text)) return "Cuebot refused the token (copy it again from Cuebot's Stream Deck page)";
	if (/\b403\b/.test(text)) return "Cuebot refused the connection (origin not allowed)";
	if (/\b404\b/.test(text)) return "Cuebot's REST API is off or too old (turn on the REST API on the Servers page)";
	if (/ECONNREFUSED/.test(text)) return "Cuebot is not running or its REST API is off";
	if (/ENOTFOUND|EAI_AGAIN/.test(text)) return "Host not found";
	return text;
}
