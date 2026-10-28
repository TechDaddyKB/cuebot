import { describe, expect, it } from "vitest";
import type { Status } from "../src/client.js";
import { handlePiMessage } from "../src/pi-bridge.js";
import type { ActionSummary } from "../src/protocol.js";

function deps(request: (method: string) => Promise<unknown>, cached: ActionSummary[] | null = null) {
	let cache = cached;
	let reconnects = 0;
	const status: Status = { state: "connected", message: "Connected to Cuebot 0.1.0" };
	return {
		deps: {
			client: {
				status,
				isConnected: true,
				request: request as never,
				reconnect: () => {
					reconnects++;
				},
			},
			cachedActions: () => cache,
			setCachedActions: (a: ActionSummary[]) => {
				cache = a;
			},
			buttonId: "key-1",
		},
		cache: () => cache,
		reconnects: () => reconnects,
	};
}

describe("handlePiMessage", () => {
	it("reports status with the button id", async () => {
		const d = deps(async () => ({}));
		expect(await handlePiMessage({ event: "getStatus" }, d.deps)).toEqual({
			event: "status",
			status: { state: "connected", message: "Connected to Cuebot 0.1.0" },
			buttonId: "key-1",
		});
	});

	it("reconnects on request", async () => {
		const d = deps(async () => ({}));
		await handlePiMessage({ event: "reconnect" }, d.deps);
		expect(d.reconnects()).toBe(1);
	});

	it("lists actions and caches them", async () => {
		const actions = [{ id: "1", name: "Go", group: "Scenes", enabled: true }];
		const d = deps(async () => ({ actions }));
		expect(await handlePiMessage({ event: "getActions" }, d.deps)).toEqual({
			event: "actions",
			actions: [{ value: "1", label: "Scenes / Go", enabled: true }],
		});
		expect(d.cache()).toEqual(actions);
	});

	it("falls back to the cached list with the error", async () => {
		const d = deps(async () => {
			throw new Error("Not connected to Cuebot");
		}, [{ id: "9", name: "Old", group: "", enabled: true }]);
		expect(await handlePiMessage({ event: "getActions" }, d.deps)).toEqual({
			event: "actions",
			actions: [{ value: "9", label: "Old", enabled: true }],
			error: "Not connected to Cuebot",
		});
	});

	it("lists global variable names sorted", async () => {
		const d = deps(async () => ({ variables: [{ name: "b" }, { name: "a" }] }));
		expect(await handlePiMessage({ event: "getGlobals" }, d.deps)).toEqual({ event: "globals", names: ["a", "b"] });
	});

	it("ignores unknown messages", async () => {
		const d = deps(async () => ({}));
		expect(await handlePiMessage({ event: "dance" }, d.deps)).toBeNull();
		expect(await handlePiMessage(null, d.deps)).toBeNull();
	});
});
