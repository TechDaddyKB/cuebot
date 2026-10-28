import { describe, expect, it } from "vitest";
import { fetchImageAsDataUri, KeyRegistry, type KeyTarget } from "../src/keys.js";
import type { ButtonInfo, CommandMessage } from "../src/protocol.js";

class FakeTarget implements KeyTarget {
	readonly calls: string[] = [];
	constructor(readonly isDial = false) {}
	setTitle(title: string | undefined, state?: number): void {
		this.calls.push(`title:${title ?? "<none>"}${state === undefined ? "" : `@${state}`}`);
	}
	setState(state: number): void {
		this.calls.push(`state:${state}`);
	}
	setImage(image: string | undefined, state?: number): void {
		this.calls.push(`image:${image ?? "<none>"}${state === undefined ? "" : `@${state}`}`);
	}
	setFeedback(payload: Record<string, unknown>): void {
		this.calls.push(`feedback:${JSON.stringify(payload)}`);
	}
	showAlert(): void {
		this.calls.push("alert");
	}
	showOk(): void {
		this.calls.push("ok");
	}
}

function button(context: string, buttonId: string, extra: Partial<ButtonInfo> = {}): ButtonInfo {
	return { context, buttonId, kind: "trigger", controller: "Keypad", device: "D", row: 0, column: 0, state: 0, isInMultiAction: false, ...extra };
}

const cmd = (command: CommandMessage["command"], buttonId: string, extra: Partial<CommandMessage> = {}): CommandMessage => ({
	type: "command",
	command,
	buttonId,
	...extra,
});

describe("KeyRegistry", () => {
	it("applies commands to every key with the id, case-insensitively", async () => {
		const r = new KeyRegistry();
		const a = new FakeTarget();
		const b = new FakeTarget();
		const other = new FakeTarget();
		await r.add(button("a", "Live"), a);
		await r.add(button("b", "live "), b);
		await r.add(button("c", "other"), other);
		expect(await r.apply(cmd("setTitle", "LIVE", { title: "On air" }))).toBe(2);
		expect(a.calls).toEqual(["title:On air"]);
		expect(b.calls).toEqual(["title:On air"]);
		expect(other.calls).toEqual([]);
	});

	it("passes the state for two-state keys and clears with an empty title", async () => {
		const r = new KeyRegistry();
		const t = new FakeTarget();
		await r.add(button("a", "k"), t);
		await r.apply(cmd("setTitle", "k", { title: "Second", state: 1 }));
		await r.apply(cmd("setTitle", "k", { title: "" }));
		expect(t.calls).toEqual(["title:Second@1", "title:<none>"]);
	});

	it("toggles state per button id and remembers it", async () => {
		const r = new KeyRegistry();
		const t = new FakeTarget();
		await r.add(button("a", "mic"), t);
		await r.apply(cmd("toggleState", "mic"));
		await r.apply(cmd("toggleState", "mic"));
		await r.apply(cmd("toggleState", "mic"));
		expect(t.calls).toEqual(["state:1", "state:0", "state:1"]);
		expect(r.get("a")!.state).toBe(1);
	});

	it("toggles from the key's reported state", async () => {
		const r = new KeyRegistry();
		const t = new FakeTarget();
		await r.add(button("a", "mic", { state: 1 }), t);
		await r.apply(cmd("toggleState", "mic"));
		expect(t.calls).toEqual(["state:0"]);
	});

	it("replays the latest appearance when a key appears later or changes id", async () => {
		const r = new KeyRegistry();
		await r.apply(cmd("setTitle", "score", { title: "1" }));
		await r.apply(cmd("setTitle", "score", { title: "2" }));
		await r.apply(cmd("setState", "score", { state: 1 }));
		await r.apply(cmd("showOk", "score"));
		const late = new FakeTarget();
		await r.add(button("x", "score"), late);
		expect(late.calls).toEqual(["title:2", "state:1"]);

		const renamed = new FakeTarget();
		await r.add(button("y", "nothing"), renamed);
		await r.update("y", { buttonId: "SCORE" });
		expect(renamed.calls).toEqual(["title:2", "state:1"]);
		await r.update("y", { row: 3 });
		expect(renamed.calls).toHaveLength(2);
	});

	it("an all-states title replaces per-state ones in the replay", async () => {
		const r = new KeyRegistry();
		await r.apply(cmd("setTitle", "k", { title: "a", state: 0 }));
		await r.apply(cmd("setTitle", "k", { title: "b", state: 1 }));
		await r.apply(cmd("setTitle", "k", { title: "all" }));
		const t = new FakeTarget();
		await r.add(button("a", "k"), t);
		expect(t.calls).toEqual(["title:all"]);
	});

	it("shows values on dials as feedback and on keys as the title", async () => {
		const r = new KeyRegistry();
		const dial = new FakeTarget(true);
		const key = new FakeTarget();
		await r.add(button("d", "vol", { controller: "Encoder" }), dial);
		await r.add(button("k", "vol"), key);
		await r.apply(cmd("setValue", "vol", { value: "75%", indicator: 175 }));
		await r.apply(cmd("showOk", "vol"));
		await r.apply(cmd("setState", "vol", { state: 1 }));
		expect(dial.calls).toEqual(['feedback:{"value":"75%","indicator":100}']);
		expect(key.calls).toEqual(["title:75%", "ok", "state:1"]);
	});

	it("loads image URLs and flashes the alert when they fail", async () => {
		const loaded: string[] = [];
		const errors: string[] = [];
		const r = new KeyRegistry({
			loadImage: async (url) => {
				loaded.push(url);
				if (url.includes("bad")) throw new Error("HTTP 404");
				return "data:image/png;base64,AA==";
			},
			onError: (m) => errors.push(m),
		});
		const t = new FakeTarget();
		await r.add(button("a", "k"), t);
		await r.apply(cmd("setImage", "k", { image: "https://example.com/good.png", state: 1 }));
		await r.apply(cmd("setImage", "k", { image: "https://example.com/bad.png" }));
		await r.apply(cmd("setImage", "k", { image: "data:image/png;base64,BB==" }));
		expect(loaded).toEqual(["https://example.com/good.png", "https://example.com/bad.png"]);
		expect(t.calls).toEqual(["image:data:image/png;base64,AA==@1", "alert", "image:data:image/png;base64,BB=="]);
		expect(errors[0]).toContain("HTTP 404");
	});

	it("survives keys that throw and lists what is registered", async () => {
		const r = new KeyRegistry();
		const broken: KeyTarget = {
			isDial: false,
			setTitle: () => {
				throw new Error("gone");
			},
			setState: () => undefined,
			setImage: () => undefined,
			setFeedback: () => undefined,
			showAlert: () => undefined,
			showOk: () => undefined,
		};
		await r.add(button("a", "k"), broken);
		expect(await r.apply(cmd("setTitle", "k", { title: "x" }))).toBe(1);
		expect(r.list().map((b) => b.context)).toEqual(["a"]);
		expect(r.remove("a")).toBe(true);
		expect(r.size).toBe(0);
		expect(await r.apply(cmd("setTitle", " ", { title: "x" }))).toBe(0);
	});
});

describe("fetchImageAsDataUri", () => {
	const respond = (body: Uint8Array, type: string, status = 200) =>
		(async () => new Response(body as unknown as BodyInit, { status, headers: { "content-type": type } })) as unknown as typeof fetch;

	it("returns a data URI for images", async () => {
		await expect(fetchImageAsDataUri("https://x/a.png", respond(new Uint8Array([1, 2, 3]), "image/png; charset=binary"))).resolves.toBe(
			"data:image/png;base64,AQID",
		);
	});

	it("refuses non-images, errors and large files", async () => {
		await expect(fetchImageAsDataUri("https://x", respond(new Uint8Array([1]), "text/html"))).rejects.toThrow("not an image");
		await expect(fetchImageAsDataUri("https://x", respond(new Uint8Array([1]), "image/png", 500))).rejects.toThrow("HTTP 500");
		await expect(fetchImageAsDataUri("https://x", respond(new Uint8Array(10), "image/png"), 5)).rejects.toThrow("larger");
	});
});
