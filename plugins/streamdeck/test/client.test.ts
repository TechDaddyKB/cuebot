import { describe, expect, it } from "vitest";
import { CuebotClient, friendlyError, type SocketLike, type Status } from "../src/client.js";
import { connectionFrom } from "../src/protocol.js";

/** A scripted socket: tests call open(), receive() and close() to play the server. */
class FakeSocket implements SocketLike {
	readonly sent: Record<string, unknown>[] = [];
	readonly listeners = new Map<string, ((...args: unknown[]) => void)[]>();
	closed = false;

	constructor(readonly url: string, readonly token: string) {}

	on(event: string, listener: (...args: never[]) => void): this {
		const list = this.listeners.get(event) ?? [];
		list.push(listener as (...args: unknown[]) => void);
		this.listeners.set(event, list);
		return this;
	}

	send(text: string): void {
		this.sent.push(JSON.parse(text));
	}

	close(): void {
		this.closed = true;
	}

	emit(event: string, ...args: unknown[]): void {
		for (const l of this.listeners.get(event) ?? []) l(...args);
	}

	open(): void {
		this.emit("open");
	}

	receive(message: object): void {
		this.emit("message", Buffer.from(JSON.stringify(message)));
	}

	welcome(): void {
		this.receive({ type: "welcome", protocol: 1, session: "s", app: { name: "Cuebot", version: "0.1.0" } });
	}
}

function setup() {
	const sockets: FakeSocket[] = [];
	const timers: { fn: () => void; ms: number; cleared: boolean }[] = [];
	const statuses: Status[] = [];
	const client = new CuebotClient({
		socket: (url, token) => {
			const s = new FakeSocket(url, token);
			sockets.push(s);
			return s;
		},
		hello: () => ({
			plugin: { uuid: "io.github.cuebot.streamdeck", version: "0.1.0" },
			app: { version: "7.1", platform: "windows", language: "en" },
			devices: [{ id: "D", name: "Deck", type: 0, size: { columns: 5, rows: 3 } }],
			buttons: [],
		}),
		setTimer: (fn, ms) => {
			const t = { fn, ms, cleared: false };
			timers.push(t);
			return t;
		},
		clearTimer: (h) => {
			(h as { cleared: boolean }).cleared = true;
		},
	});
	client.onStatus((s) => statuses.push(s));
	const connect = () => client.configure(connectionFrom({ token: "cb_token" }));
	const pending = () => timers.filter((t) => !t.cleared);
	return { client, sockets, timers, statuses, connect, pending };
}

describe("CuebotClient", () => {
	it("connects with the token, says hello and becomes connected on welcome", () => {
		const { client, sockets, connect, statuses } = setup();
		connect();
		const s = sockets[0]!;
		expect(s.url).toBe("ws://127.0.0.1:7475/api/v1/streamdeck");
		expect(s.token).toBe("cb_token");
		expect(client.status.state).toBe("connecting");
		s.open();
		expect(s.sent[0]).toMatchObject({ type: "hello", protocol: 1, devices: [{ id: "D" }] });
		expect(client.isConnected).toBe(false);
		s.welcome();
		expect(client.isConnected).toBe(true);
		expect(statuses.at(-1)).toMatchObject({ state: "connected", cuebotVersion: "0.1.0" });
	});

	it("warns about a newer protocol", () => {
		const { client, sockets, connect } = setup();
		connect();
		sockets[0]!.open();
		sockets[0]!.receive({ type: "welcome", protocol: 2, session: "s", app: { name: "Cuebot", version: "9" } });
		expect(client.status.message).toContain("update the plugin");
	});

	it("does not send before the welcome", () => {
		const { client, sockets, connect } = setup();
		connect();
		sockets[0]!.open();
		expect(client.send({ type: "input" })).toBe(false);
		expect(sockets[0]!.sent).toHaveLength(1);
	});

	it("matches responses to requests", async () => {
		const { client, sockets, connect } = setup();
		connect();
		const s = sockets[0]!;
		s.open();
		s.welcome();
		const ok = client.request<{ actions: unknown[] }>("getActions");
		const bad = client.request("runAction", { action: "x" });
		const [first, second] = s.sent.slice(1) as { id: number; method: string }[];
		expect(first).toMatchObject({ type: "request", method: "getActions" });
		s.receive({ type: "response", id: second!.id, ok: false, error: "Action 'x' not found" });
		s.receive({ type: "response", id: first!.id, ok: true, result: { actions: [] } });
		await expect(ok).resolves.toEqual({ actions: [] });
		await expect(bad).rejects.toThrow("Action 'x' not found");
	});

	it("times out unanswered requests", async () => {
		const { client, sockets, connect, pending } = setup();
		connect();
		sockets[0]!.open();
		sockets[0]!.welcome();
		const p = client.request("getGlobals");
		pending().find((t) => t.ms === 10_000)!.fn();
		await expect(p).rejects.toThrow("did not answer");
	});

	it("rejects requests when not connected", async () => {
		const { client } = setup();
		await expect(client.request("getActions")).rejects.toThrow();
	});

	it("reconnects with backoff and rejects pending requests when the socket closes", async () => {
		const { client, sockets, connect, pending } = setup();
		connect();
		sockets[0]!.open();
		sockets[0]!.welcome();
		const p = client.request("getActions");
		sockets[0]!.emit("error", new Error("Unexpected server response: 401"));
		sockets[0]!.emit("close", 1006);
		await expect(p).rejects.toThrow("closed");
		expect(client.status.state).toBe("error");
		expect(client.status.message).toContain("refused the token");
		const retry = pending().find((t) => t.ms === 1000)!;
		retry.fn();
		expect(sockets).toHaveLength(2);
		sockets[1]!.emit("close", 1006);
		expect(pending().some((t) => t.ms === 2000)).toBe(true);
	});

	it("caps the backoff", () => {
		const { sockets, connect, pending } = setup();
		connect();
		for (let i = 0; i < 10; i++) {
			sockets.at(-1)!.emit("close", 1006);
			const timer = pending().at(-1)!;
			timer.cleared = true;
			timer.fn();
		}
		expect(Math.max(...pending().map((t) => t.ms), 0)).toBeLessThanOrEqual(30_000);
	});

	it("reconfigures only when the url or token changes", () => {
		const { client, sockets } = setup();
		client.configure(connectionFrom({ token: "a" }));
		client.configure(connectionFrom({ token: "a" }));
		expect(sockets).toHaveLength(1);
		client.configure(connectionFrom({ token: "b" }));
		expect(sockets).toHaveLength(2);
		expect(sockets[0]!.closed).toBe(true);
	});

	it("stops and shows the reason for bad settings", () => {
		const { client, sockets, pending } = setup();
		client.configure(connectionFrom({ token: "a" }));
		client.configure(connectionFrom({}));
		expect(sockets[0]!.closed).toBe(true);
		expect(client.status).toMatchObject({ state: "error", message: expect.stringContaining("token") });
		sockets[0]!.emit("close", 1000);
		expect(pending()).toHaveLength(0);
	});

	it("routes commands and pushes to listeners", () => {
		const { client, sockets, connect } = setup();
		const seen: string[] = [];
		client.onCommand((c) => seen.push(`${c.command}:${c.buttonId}`));
		client.onActionsChanged((a) => seen.push(`actions:${a.length}`));
		client.onGlobalChanged((n, v) => seen.push(`global:${n}=${v}`));
		connect();
		const s = sockets[0]!;
		s.open();
		s.welcome();
		s.receive({ type: "command", command: "setTitle", buttonId: "k", title: "x" });
		s.receive({ type: "actionsChanged", actions: [{ id: "1", name: "A", group: "", enabled: true }] });
		s.receive({ type: "globalChanged", name: "score", value: "3" });
		s.emit("message", "not json");
		expect(seen).toEqual(["setTitle:k", "actions:1", "global:score=3"]);
	});

	it("ignores events from a socket it already replaced", () => {
		const { client, sockets } = setup();
		client.configure(connectionFrom({ token: "a" }));
		client.configure(connectionFrom({ token: "b" }));
		sockets[0]!.open();
		sockets[0]!.welcome();
		expect(client.isConnected).toBe(false);
		expect(sockets[0]!.sent).toHaveLength(0);
	});
});

describe("friendlyError", () => {
	it("explains common failures", () => {
		expect(friendlyError(new Error("connect ECONNREFUSED 127.0.0.1:7475"))).toContain("not running");
		expect(friendlyError(new Error("Unexpected server response: 404"))).toContain("REST API is off");
		expect(friendlyError(new Error("something else"))).toBe("something else");
	});
});
