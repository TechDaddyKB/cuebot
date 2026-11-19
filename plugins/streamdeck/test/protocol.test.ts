import { describe, expect, it } from "vitest";
import {
	ACTION_UUIDS,
	actionChoices,
	connectionFrom,
	kindOf,
	newButtonId,
	normalizeId,
	parseArgs,
	parseServerMessage,
} from "../src/protocol.js";
import { describeStatus, sectionVisible } from "../src/pi/view.js";

describe("connectionFrom", () => {
	it("uses defaults for host and port", () => {
		expect(connectionFrom({ token: "cb_x" })).toEqual({
			host: "127.0.0.1",
			port: 7475,
			token: "cb_x",
			url: "ws://127.0.0.1:7475/api/v1/streamdeck",
		});
	});

	it("accepts LAN hosts, string ports and IPv6", () => {
		expect(connectionFrom({ host: " 192.168.1.20 ", port: "8000", token: " t " })).toMatchObject({
			url: "ws://192.168.1.20:8000/api/v1/streamdeck",
			token: "t",
		});
		expect(connectionFrom({ host: "::1", token: "t" })).toMatchObject({ url: "ws://[::1]:7475/api/v1/streamdeck" });
	});

	it("explains what is missing", () => {
		expect(connectionFrom({})).toEqual({ error: expect.stringContaining("token") });
		expect(connectionFrom({ token: "t", port: "99999" })).toEqual({ error: expect.stringContaining("port") });
		expect(connectionFrom({ token: "t", port: "abc" })).toEqual({ error: expect.stringContaining("port") });
		expect(connectionFrom({ token: "t", host: "http://x/y" })).toEqual({ error: expect.stringContaining("Host") });
	});
});

describe("parseArgs", () => {
	it("reads name=value lines", () => {
		expect(parseArgs("scene = Main\n# comment\n\nmessage=a=b\nbroken")).toEqual({ scene: "Main", message: "a=b" });
	});

	it("reads a JSON object", () => {
		expect(parseArgs('{"a":"x","n":5,"o":{"k":1}}')).toEqual({ a: "x", n: "5", o: '{"k":1}' });
	});

	it("is empty for nothing", () => {
		expect(parseArgs(undefined)).toEqual({});
		expect(parseArgs("   ")).toEqual({});
	});
});

describe("ids and kinds", () => {
	it("makes short readable button ids", () => {
		let i = 0;
		const seq = [0.1, 0.2, 0.3, 0.4, 0.5, 0.99];
		expect(newButtonId("trigger", () => seq[i++]!)).toBe("key-13468f");
		expect(newButtonId("run")).toMatch(/^run-[0-9a-f]{6}$/);
	});

	it("normalizes ids", () => {
		expect(normalizeId("  Live ")).toBe("live");
		expect(normalizeId(undefined)).toBe("");
	});

	it("maps action UUIDs to kinds", () => {
		expect(kindOf(ACTION_UUIDS.global)).toBe("global");
		expect(kindOf("com.other.action")).toBeUndefined();
	});
});

describe("parseServerMessage", () => {
	it("accepts objects with a type only", () => {
		expect(parseServerMessage('{"type":"pong"}')).toEqual({ type: "pong" });
		expect(parseServerMessage("[1]")).toBeNull();
		expect(parseServerMessage('{"x":1}')).toBeNull();
		expect(parseServerMessage("nope")).toBeNull();
	});
});

describe("actionChoices", () => {
	it("sorts by group then name and labels with the group", () => {
		const choices = actionChoices([
			{ id: "2", name: "Zed", group: "", enabled: true },
			{ id: "1", name: "Alpha", group: "Scenes", enabled: false },
			{ id: "3", name: "Beta", group: "", enabled: true },
		]);
		expect(choices.map((c) => c.label)).toEqual(["Beta", "Zed", "Scenes / Alpha"]);
		expect(choices[2]!.enabled).toBe(false);
	});
});

describe("property inspector view", () => {
	it("shows sections for the right kinds and controllers", () => {
		expect(sectionVisible("all", undefined, "run", "Keypad")).toBe(true);
		expect(sectionVisible("run toggle", undefined, "toggle", "Keypad")).toBe(true);
		expect(sectionVisible("run toggle", undefined, "global", "Keypad")).toBe(false);
		expect(sectionVisible("global", "Encoder", "global", "Keypad")).toBe(false);
		expect(sectionVisible("global", "Encoder", "global", "Encoder")).toBe(true);
		expect(sectionVisible("run", undefined, undefined, undefined)).toBe(false);
	});

	it("describes the connection status", () => {
		expect(describeStatus({ state: "connected", message: "Connected to Cuebot 0.1.0" })).toEqual({ className: "ok", text: "Connected to Cuebot 0.1.0" });
		expect(describeStatus({ state: "error", message: "" }).className).toBe("bad");
		expect(describeStatus(undefined).className).toBe("off");
	});
});
