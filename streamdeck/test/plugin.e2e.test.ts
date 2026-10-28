/**
 * Runs the built plugin (bin/plugin.js) the way Stream Deck does — with Node, its registration arguments and the
 * .sdPlugin folder as the working directory — against a fake Stream Deck app and a fake Cuebot, both WebSocket servers.
 * Needs `npm run build` first (the test script does it).
 */
import { type ChildProcess, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { WebSocket, WebSocketServer } from "ws";

const pluginDir = join(import.meta.dirname, "..", "io.github.cuebot.streamdeck.sdPlugin");
const bundle = join(pluginDir, "bin", "plugin.js");
const PLUGIN_UUID = "io.github.cuebot.streamdeck";
const TOKEN = "cb_e2e_token";

type Message = Record<string, unknown> & { event?: string; type?: string };

/** A WebSocket server that records messages from its single client. */
class FakeServer {
	readonly messages: Message[] = [];
	readonly headers: Record<string, string | string[] | undefined>[] = [];
	socket: WebSocket | null = null;
	private waiters: { match: (m: Message) => boolean; resolve: (m: Message) => void }[] = [];

	private constructor(readonly server: WebSocketServer) {
		server.on("connection", (socket, request) => {
			this.socket = socket;
			this.headers.push(request.headers);
			socket.on("message", (data) => {
				const message = JSON.parse(String(data)) as Message;
				this.messages.push(message);
				this.waiters = this.waiters.filter((w) => {
					if (!w.match(message)) return true;
					w.resolve(message);
					return false;
				});
			});
		});
	}

	static async start(verify?: (auth: string | undefined) => boolean): Promise<FakeServer> {
		const server = new WebSocketServer({
			host: "127.0.0.1",
			port: 0,
			verifyClient: verify ? (info, cb) => cb(verify(info.req.headers.authorization), 401) : undefined,
		});
		await new Promise<void>((r) => server.once("listening", () => r()));
		return new FakeServer(server);
	}

	get port(): number {
		return (this.server.address() as AddressInfo).port;
	}

	send(message: object): void {
		this.socket!.send(JSON.stringify(message));
	}

	next(match: (m: Message) => boolean, timeoutMs = 10_000): Promise<Message> {
		const found = this.messages.find(match);
		if (found) return Promise.resolve(found);
		return new Promise((resolve, reject) => {
			const timer = setTimeout(() => reject(new Error("timed out waiting for a message")), timeoutMs);
			this.waiters.push({ match, resolve: (m) => (clearTimeout(timer), resolve(m)) });
		});
	}

	close(): Promise<void> {
		for (const client of this.server.clients) client.terminate();
		return new Promise((r) => this.server.close(() => r()));
	}
}

let child: ChildProcess | null = null;
const servers: FakeServer[] = [];

afterEach(async () => {
	child?.kill();
	child = null;
	await Promise.all(servers.splice(0).map((s) => s.close()));
});

function launch(streamDeckPort: number): ChildProcess {
	const info = {
		application: { font: "Segoe UI", language: "en", platform: "windows", platformVersion: "10.0.26200", version: "7.1.0.0" },
		colors: {},
		devicePixelRatio: 1,
		devices: [{ id: "DEV1", name: "Test XL", size: { columns: 8, rows: 4 }, type: 2 }],
		plugin: { uuid: PLUGIN_UUID, version: "0.1.0.0" },
	};
	const process = spawn(
		globalThis.process.execPath,
		[bundle, "-port", String(streamDeckPort), "-pluginUUID", PLUGIN_UUID, "-registerEvent", "registerPlugin", "-info", JSON.stringify(info)],
		{ cwd: pluginDir, stdio: "pipe" },
	);
	process.stderr?.on("data", (d) => globalThis.process.stderr.write(`[plugin] ${d}`));
	return process;
}

describe.skipIf(!existsSync(bundle))("built plugin", () => {
	it("registers, connects to Cuebot with the token and relays keys and commands", async () => {
		const cuebot = await FakeServer.start((auth) => auth === `Bearer ${TOKEN}`);
		const deck = await FakeServer.start();
		servers.push(cuebot, deck);
		child = launch(deck.port);

		const registration = await deck.next((m) => m.event === "registerPlugin");
		expect(registration.uuid).toBe(PLUGIN_UUID);
		await deck.next((m) => m.event === "getGlobalSettings");
		deck.send({
			event: "didReceiveGlobalSettings",
			payload: { settings: { host: "127.0.0.1", port: cuebot.port, token: TOKEN } },
		});

		const hello = await cuebot.next((m) => m.type === "hello");
		expect(cuebot.headers[0]?.authorization).toBe(`Bearer ${TOKEN}`);
		expect(hello).toMatchObject({
			protocol: 1,
			plugin: { uuid: PLUGIN_UUID },
			app: { version: "7.1.0.0", platform: "windows" },
			devices: [{ id: "DEV1", name: "Test XL", type: 2, size: { columns: 8, rows: 4 } }],
		});
		cuebot.send({ type: "welcome", protocol: 1, session: "s1", app: { name: "Cuebot", version: "0.1.0" } });

		// A Cuebot trigger key appears: it is registered with Cuebot under its button id.
		deck.send({
			event: "willAppear",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx1",
			device: "DEV1",
			payload: { settings: { buttonId: "go-live", args: "scene=Main" }, coordinates: { column: 2, row: 1 }, controller: "Keypad", isInMultiAction: false, state: 0 },
		});
		const appear = await cuebot.next((m) => m.type === "appear");
		expect(appear.button).toMatchObject({ context: "ctx1", buttonId: "go-live", kind: "trigger", controller: "Keypad", device: "DEV1", row: 1, column: 2 });

		// Pressing it fires the trigger in Cuebot.
		deck.send({
			event: "keyDown",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx1",
			device: "DEV1",
			payload: { settings: { buttonId: "go-live", args: "scene=Main" }, coordinates: { column: 2, row: 1 }, isInMultiAction: false, state: 0 },
		});
		const input = await cuebot.next((m) => m.type === "input");
		expect(input).toMatchObject({ event: "keyDown", context: "ctx1", buttonId: "go-live", args: { scene: "Main" } });

		// Cuebot changes its title and state.
		cuebot.send({ type: "command", command: "setTitle", buttonId: "GO-LIVE", title: "LIVE" });
		const title = await deck.next((m) => m.event === "setTitle");
		expect(title).toMatchObject({ context: "ctx1", payload: { title: "LIVE" } });
		cuebot.send({ type: "command", command: "toggleState", buttonId: "go-live" });
		const state = await deck.next((m) => m.event === "setState");
		expect(state).toMatchObject({ context: "ctx1", payload: { state: 1 } });

		// A key without a button id gets one assigned and saved.
		deck.send({
			event: "willAppear",
			action: `${PLUGIN_UUID}.run`,
			context: "ctx2",
			device: "DEV1",
			payload: { settings: {}, coordinates: { column: 0, row: 0 }, controller: "Keypad", isInMultiAction: false, state: 0 },
		});
		const saved = await deck.next((m) => m.event === "setSettings" && m.context === "ctx2");
		expect((saved.payload as { buttonId: string }).buttonId).toMatch(/^run-[0-9a-f]{6}$/);

		// Running an action: the request goes to Cuebot, the answer shows OK on the key.
		deck.send({
			event: "keyDown",
			action: `${PLUGIN_UUID}.run`,
			context: "ctx2",
			device: "DEV1",
			payload: { settings: { buttonId: "run-1", actionId: "a-1" }, coordinates: { column: 0, row: 0 }, isInMultiAction: false, state: 0 },
		});
		const request = await cuebot.next((m) => m.type === "request" && m.method === "runAction");
		expect(request.params).toMatchObject({ action: "a-1" });
		cuebot.send({ type: "response", id: request.id, ok: true, result: { queued: true } });
		await deck.next((m) => m.event === "showOk" && m.context === "ctx2");

		// Disappearing keys are dropped in Cuebot.
		deck.send({
			event: "willDisappear",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx1",
			device: "DEV1",
			payload: { settings: { buttonId: "go-live" }, coordinates: { column: 2, row: 1 }, controller: "Keypad", isInMultiAction: false, state: 1 },
		});
		await cuebot.next((m) => m.type === "disappear" && m.context === "ctx1");
	}, 30_000);

	it("reports a refused token and keeps retrying", async () => {
		const cuebot = await FakeServer.start(() => false);
		const deck = await FakeServer.start();
		servers.push(cuebot, deck);
		child = launch(deck.port);
		await deck.next((m) => m.event === "getGlobalSettings");
		deck.send({ event: "didReceiveGlobalSettings", payload: { settings: { port: cuebot.port, token: "wrong" } } });
		deck.send({
			event: "willAppear",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx9",
			device: "DEV1",
			payload: { settings: { buttonId: "k" }, coordinates: { column: 0, row: 0 }, controller: "Keypad", isInMultiAction: false, state: 0 },
		});
		// The property inspector asks for the status; the plugin answers through sendToPropertyInspector.
		deck.send({
			event: "propertyInspectorDidAppear",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx9",
			device: "DEV1",
		});
		await new Promise((r) => setTimeout(r, 1500));
		deck.send({
			event: "sendToPlugin",
			action: `${PLUGIN_UUID}.trigger`,
			context: "ctx9",
			payload: { event: "getStatus" },
		});
		const reply = await deck.next(
			(m) => m.event === "sendToPropertyInspector" && JSON.stringify(m.payload).includes("refused the token"),
		);
		expect(reply.payload).toMatchObject({ event: "status", status: { state: "error" } });
	}, 30_000);
});
