import streamDeck, { type DialAction, type FeedbackPayload, type KeyAction } from "@elgato/streamdeck";
import WebSocket from "ws";
import { CuebotClient } from "./client.js";
import { fetchImageAsDataUri, KeyRegistry, type KeyTarget } from "./keys.js";
import { handlePiMessage } from "./pi-bridge.js";
import { type ActionSummary, connectionFrom, type DeviceInfo, type GlobalSettings, PLUGIN_UUID } from "./protocol.js";

/** Shared plugin state: the Cuebot connection, the visible keys and the latest action list. */
export const registry = new KeyRegistry({
	loadImage: (url) => fetchImageAsDataUri(url),
	onError: (message) => streamDeck.logger.warn(message),
});

let latestActions: ActionSummary[] | null = null;

export const client = new CuebotClient({
	socket: (url, token) =>
		new WebSocket(url, {
			headers: { Authorization: `Bearer ${token}` },
			handshakeTimeout: 5000,
			// The local Cuebot API never redirects; refuse to follow one with the token.
			followRedirects: false,
		}),
	hello: () => ({
		plugin: { uuid: PLUGIN_UUID, version: streamDeck.info.plugin.version },
		app: {
			version: streamDeck.info.application.version,
			platform: streamDeck.info.application.platform,
			language: streamDeck.info.application.language,
		},
		devices: devices(),
		buttons: registry.list(),
	}),
});

export function cachedActions(): ActionSummary[] | null {
	return latestActions;
}

export function setCachedActions(actions: ActionSummary[]): void {
	latestActions = actions;
}

export function devices(): DeviceInfo[] {
	return [...streamDeck.devices].map((d) => ({
		id: d.id,
		name: d.name,
		type: d.type,
		size: { columns: d.size.columns, rows: d.size.rows },
		connected: d.isConnected,
	}));
}

/** Wraps an SDK key or dial in the registry's interface. */
export function targetFor(action: KeyAction | DialAction): KeyTarget {
	if (action.isDial()) {
		const dial = action;
		return {
			isDial: true,
			setTitle: (title) => dial.setTitle(title ?? ""),
			setState: () => undefined,
			setImage: (image) => dial.setImage(image),
			setFeedback: (payload) => dial.setFeedback(payload as FeedbackPayload),
			showAlert: () => dial.showAlert(),
			showOk: () => undefined,
		};
	}
	const key = action as KeyAction;
	return {
		isDial: false,
		setTitle: (title, state) => key.setTitle(title, state === undefined ? undefined : { state }),
		setState: (state) => key.setState(state),
		setImage: (image, state) => key.setImage(image, state === undefined ? undefined : { state }),
		setFeedback: () => undefined,
		showAlert: () => key.showAlert(),
		showOk: () => key.showOk(),
	};
}

/** Answers a property inspector message for a key. */
export async function answerPropertyInspector(message: unknown, buttonId: string | undefined): Promise<void> {
	const reply = await handlePiMessage(message, { client, cachedActions, setCachedActions, buttonId });
	if (reply) await streamDeck.ui.sendToPropertyInspector(reply).catch(() => undefined);
}

/** Starts the connection: reads the global settings now and whenever the property inspector changes them. */
export async function start(): Promise<void> {
	client.onCommand((command) => {
		void registry.apply(command);
	});
	client.onActionsChanged((actions) => setCachedActions(actions));
	client.onStatus((status) => {
		streamDeck.logger.info(`Cuebot: ${status.state} - ${status.message}`);
		if (streamDeck.ui.action) void streamDeck.ui.sendToPropertyInspector({ event: "status", status }).catch(() => undefined);
	});

	const sendDevices = (): void => void client.send({ type: "devices", devices: devices() });
	streamDeck.devices.onDeviceDidConnect(sendDevices);
	streamDeck.devices.onDeviceDidDisconnect(sendDevices);
	streamDeck.devices.onDeviceDidChange(sendDevices);

	streamDeck.settings.onDidReceiveGlobalSettings<GlobalSettings>((ev) => client.configure(connectionFrom(ev.settings)));
	const settings = await streamDeck.settings.getGlobalSettings<GlobalSettings>();
	client.configure(connectionFrom(settings));
}
