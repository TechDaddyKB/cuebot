import type { CuebotClient, Status } from "./client.js";
import { type ActionSummary, actionChoices } from "./protocol.js";

/** Messages the property inspector sends to the plugin (sendToPlugin) and what the plugin answers. */
export type PiRequest =
	| { event: "getStatus" }
	| { event: "getActions" }
	| { event: "getGlobals" }
	| { event: "reconnect" };

export type PiReply =
	| { event: "status"; status: Status; buttonId?: string }
	| { event: "actions"; actions: { value: string; label: string; enabled: boolean }[]; error?: string }
	| { event: "globals"; names: string[]; error?: string };

export type BridgeDeps = {
	client: Pick<CuebotClient, "status" | "isConnected" | "request" | "reconnect">;
	/** The latest action list pushed by Cuebot (refreshed on demand). */
	cachedActions: () => ActionSummary[] | null;
	setCachedActions: (actions: ActionSummary[]) => void;
	buttonId?: string;
};

/** Answers one property inspector message; null for messages that need no answer. */
export async function handlePiMessage(message: unknown, deps: BridgeDeps): Promise<PiReply | null> {
	const event = (message as { event?: unknown } | null)?.event;
	switch (event) {
		case "getStatus":
			return { event: "status", status: deps.client.status, buttonId: deps.buttonId };
		case "reconnect":
			deps.client.reconnect();
			return { event: "status", status: deps.client.status, buttonId: deps.buttonId };
		case "getActions": {
			try {
				const result = await deps.client.request<{ actions: ActionSummary[] }>("getActions");
				deps.setCachedActions(result.actions ?? []);
				return { event: "actions", actions: actionChoices(result.actions ?? []) };
			} catch (error) {
				const cached = deps.cachedActions();
				return { event: "actions", actions: cached ? actionChoices(cached) : [], error: errorText(error) };
			}
		}
		case "getGlobals": {
			try {
				const result = await deps.client.request<{ variables: { name: string }[] }>("getGlobals");
				const names = (result.variables ?? []).map((v) => v.name).sort((a, b) => a.localeCompare(b));
				return { event: "globals", names };
			} catch (error) {
				return { event: "globals", names: [], error: errorText(error) };
			}
		}
		default:
			return null;
	}
}

function errorText(error: unknown): string {
	return error instanceof Error ? error.message : String(error);
}
