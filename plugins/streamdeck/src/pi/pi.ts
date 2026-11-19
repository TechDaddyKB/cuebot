/**
 * Property inspector for every Cuebot action (ui/cuebot.html). It speaks the Stream Deck property inspector protocol
 * directly (no CDN components, so it works offline): settings and global settings go to the Stream Deck app, and
 * status, action and variable lists come from the plugin (sendToPlugin / sendToPropertyInspector).
 */
import { kindOf, type ActionKind } from "../protocol.js";
import { describeStatus, sectionVisible } from "./view.js";

type Json = Record<string, unknown>;
type ActionInfo = { action: string; context: string; device: string; payload: { settings: Json; controller?: string } };

let socket: WebSocket | null = null;
let uuid = "";
let actionUuid = "";
let kind: ActionKind | undefined;
let settings: Json = {};
let globalSettings: Json = {};

function send(event: string, extra: Json = {}): void {
	if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ event, context: uuid, ...extra }));
}

function toPlugin(payload: Json): void {
	send("sendToPlugin", { action: actionUuid, payload });
}

function $(selector: string): HTMLElement | null {
	return document.querySelector(selector);
}

function inputs(attribute: "data-setting" | "data-global"): (HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement)[] {
	return [...document.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(`[${attribute}]`)];
}

function readValue(el: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement): unknown {
	return el instanceof HTMLInputElement && el.type === "checkbox" ? el.checked : el.value;
}

function writeValue(el: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement, value: unknown): void {
	if (el instanceof HTMLInputElement && el.type === "checkbox") {
		if (typeof value === "boolean") el.checked = value;
	} else if (value !== undefined && value !== null) el.value = String(value);
}

function fill(): void {
	for (const el of inputs("data-setting")) writeValue(el, settings[el.dataset.setting!]);
	for (const el of inputs("data-global")) writeValue(el, globalSettings[el.dataset.global!]);
	const id = $("#buttonId") as HTMLInputElement | null;
	if (id) id.value = String(settings.buttonId ?? "");
}

let saveTimer: number | undefined;
function saveSettings(): void {
	window.clearTimeout(saveTimer);
	saveTimer = window.setTimeout(() => {
		for (const el of inputs("data-setting")) settings[el.dataset.setting!] = readValue(el);
		const select = $("#actionId") as HTMLSelectElement | null;
		if (select && select.selectedIndex >= 0 && select.value) settings.actionName = select.options[select.selectedIndex]!.dataset.name ?? "";
		send("setSettings", { payload: settings });
	}, 250);
}

function saveGlobal(): void {
	for (const el of inputs("data-global")) globalSettings[el.dataset.global!] = readValue(el);
	send("setGlobalSettings", { payload: globalSettings });
	window.setTimeout(() => toPlugin({ event: "getStatus" }), 1500);
}

function showStatus(status: { state: string; message: string }): void {
	const view = describeStatus(status);
	const dot = $("#status-dot");
	const text = $("#status-text");
	if (dot) dot.className = `dot ${view.className}`;
	if (text) text.textContent = view.text;
	const connected = status.state === "connected";
	for (const el of document.querySelectorAll<HTMLElement>("[data-needs-connection]")) el.toggleAttribute("disabled", !connected);
	const details = $("#connection") as HTMLDetailsElement | null;
	if (details && !connected) details.open = true;
}

function showActions(list: { value: string; label: string; enabled: boolean }[], error?: string): void {
	const select = $("#actionId") as HTMLSelectElement | null;
	if (!select) return;
	const current = String(settings.actionId ?? "");
	select.innerHTML = "";
	const placeholder = new Option(list.length ? "Choose an action..." : error ? "(not connected)" : "(no actions in Cuebot)", "");
	select.add(placeholder);
	for (const item of list) {
		const option = new Option(item.enabled ? item.label : `${item.label} (disabled)`, item.value);
		option.dataset.name = item.label.includes(" / ") ? item.label.slice(item.label.lastIndexOf(" / ") + 3) : item.label;
		select.add(option);
	}
	if (current && !list.some((i) => i.value === current)) {
		const missing = new Option(`${String(settings.actionName ?? current)} (not found)`, current);
		missing.dataset.name = String(settings.actionName ?? "");
		select.add(missing);
	}
	select.value = current;
	const note = $("#actions-note");
	if (note) note.textContent = error ?? "";
}

function showGlobals(names: string[]): void {
	const list = $("#globals") as HTMLDataListElement | null;
	if (!list) return;
	list.innerHTML = "";
	for (const n of names) list.appendChild(new Option(n, n));
}

function onMessage(message: { event: string; payload?: Json & { settings?: Json } }): void {
	switch (message.event) {
		case "didReceiveSettings":
			settings = message.payload?.settings ?? {};
			fill();
			break;
		case "didReceiveGlobalSettings":
			globalSettings = message.payload?.settings ?? {};
			fill();
			break;
		case "sendToPropertyInspector": {
			const p = message.payload as Json & { event?: string };
			if (p.event === "status") {
				showStatus(p.status as { state: string; message: string });
				if (typeof p.buttonId === "string" && p.buttonId && !settings.buttonId) {
					settings.buttonId = p.buttonId;
					fill();
				}
				if ((p.status as { state: string }).state === "connected") requestLists();
			} else if (p.event === "actions") showActions(p.actions as never[], p.error as string | undefined);
			else if (p.event === "globals") showGlobals(p.names as string[]);
			break;
		}
		default:
			break;
	}
}

function requestLists(): void {
	if (kind === "run" || kind === "toggle") toPlugin({ event: "getActions" });
	if (kind === "global") toPlugin({ event: "getGlobals" });
}

function wire(controller: string | undefined): void {
	for (const section of document.querySelectorAll<HTMLElement>("[data-kinds]")) {
		section.hidden = !sectionVisible(section.dataset.kinds ?? "", section.dataset.controller, kind, controller);
	}
	for (const el of inputs("data-setting")) el.addEventListener(el instanceof HTMLSelectElement || el.type === "checkbox" ? "change" : "input", saveSettings);
	for (const el of inputs("data-global")) el.addEventListener("change", saveGlobal);
	$("#reconnect")?.addEventListener("click", () => toPlugin({ event: "reconnect" }));
	$("#refresh")?.addEventListener("click", requestLists);
	$("#copy-id")?.addEventListener("click", () => {
		const id = $("#buttonId") as HTMLInputElement | null;
		if (!id) return;
		id.select();
		void navigator.clipboard?.writeText(id.value).catch(() => document.execCommand("copy"));
	});
	const token = $("#token") as HTMLInputElement | null;
	$("#show-token")?.addEventListener("click", () => {
		if (token) token.type = token.type === "password" ? "text" : "password";
	});
}

(window as unknown as { connectElgatoStreamDeckSocket: unknown }).connectElgatoStreamDeckSocket = (
	port: string,
	propertyInspectorUuid: string,
	registerEvent: string,
	_info: string,
	actionInfoText: string,
): void => {
	uuid = propertyInspectorUuid;
	const info = JSON.parse(actionInfoText) as ActionInfo;
	actionUuid = info.action;
	kind = kindOf(actionUuid);
	settings = info.payload?.settings ?? {};
	wire(info.payload?.controller);
	fill();
	socket = new WebSocket(`ws://127.0.0.1:${port}`);
	socket.addEventListener("open", () => {
		socket!.send(JSON.stringify({ event: registerEvent, uuid }));
		send("getGlobalSettings");
		toPlugin({ event: "getStatus" });
	});
	socket.addEventListener("message", (e) => {
		try {
			onMessage(JSON.parse(String(e.data)));
		} catch {
			// ignore malformed messages
		}
	});
};
