import streamDeck, {
	action,
	type DialAction,
	type DialDownEvent,
	type DialRotateEvent,
	type DialUpEvent,
	type DidReceiveSettingsEvent,
	type KeyAction,
	type KeyDownEvent,
	type KeyUpEvent,
	type PropertyInspectorDidAppearEvent,
	type SendToPluginEvent,
	SingletonAction,
	type TouchTapEvent,
	type WillAppearEvent,
	type WillDisappearEvent,
} from "@elgato/streamdeck";
import type { JsonValue } from "@elgato/utils";
import { answerPropertyInspector, cachedActions, client, registry, targetFor } from "./hub.js";
import {
	ACTION_UUIDS,
	type ActionKind,
	type ActionSummary,
	type ButtonInfo,
	type InputEvent,
	type KeySettings,
	newButtonId,
	parseArgs,
} from "./protocol.js";

type AnyAction = KeyAction<KeySettings> | DialAction<KeySettings>;

/**
 * Shared behaviour of every Cuebot key and dial: a button id (assigned on first use) so Cuebot's sub-actions can reach
 * it, registration with Cuebot while visible, and the property inspector bridge.
 */
abstract class CuebotAction extends SingletonAction<KeySettings> {
	abstract readonly kind: ActionKind;

	/** The latest settings of each visible key (saves a round trip to the Stream Deck app). */
	protected readonly settings = new Map<string, KeySettings>();

	override async onWillAppear(ev: WillAppearEvent<KeySettings>): Promise<void> {
		const settings = await this.ensureButtonId(ev.action, ev.payload.settings);
		this.settings.set(ev.action.id, settings);
		const info = this.info(ev.action, settings, ev.payload);
		await registry.add(info, targetFor(ev.action));
		client.send({ type: "appear", button: info });
		await this.refresh(ev.action, settings);
	}

	override onWillDisappear(ev: WillDisappearEvent<KeySettings>): void {
		registry.remove(ev.action.id);
		this.settings.delete(ev.action.id);
		client.send({ type: "disappear", context: ev.action.id });
	}

	override async onDidReceiveSettings(ev: DidReceiveSettingsEvent<KeySettings>): Promise<void> {
		const settings = await this.ensureButtonId(ev.action, ev.payload.settings);
		this.settings.set(ev.action.id, settings);
		await registry.update(ev.action.id, { buttonId: settings.buttonId ?? "" });
		const info = registry.get(ev.action.id);
		if (info) client.send({ type: "appear", button: info });
		await this.refresh(ev.action, settings);
	}

	override async onPropertyInspectorDidAppear(ev: PropertyInspectorDidAppearEvent<KeySettings>): Promise<void> {
		await answerPropertyInspector({ event: "getStatus" }, this.settings.get(ev.action.id)?.buttonId);
	}

	override async onSendToPlugin(ev: SendToPluginEvent<JsonValue, KeySettings>): Promise<void> {
		await answerPropertyInspector(ev.payload, this.settings.get(ev.action.id)?.buttonId);
	}

	/** Kind-specific appearance after appearing, settings changes and Cuebot pushes. */
	protected refresh(_action: AnyAction, _settings: KeySettings): Promise<void> | void {}

	protected async ensureButtonId(target: AnyAction, settings: KeySettings): Promise<KeySettings> {
		if (settings.buttonId && settings.buttonId.trim().length > 0) return settings;
		const updated = { ...settings, buttonId: newButtonId(this.kind) };
		await target.setSettings(updated);
		return updated;
	}

	protected info(target: AnyAction, settings: KeySettings, payload: WillAppearEvent<KeySettings>["payload"]): ButtonInfo {
		const coordinates = payload.isInMultiAction ? undefined : payload.coordinates;
		return {
			context: target.id,
			buttonId: settings.buttonId ?? "",
			kind: this.kind,
			controller: target.isDial() ? "Encoder" : "Keypad",
			device: target.device.id,
			row: coordinates?.row,
			column: coordinates?.column,
			state: payload.state ?? 0,
			isInMultiAction: payload.isInMultiAction,
		};
	}

	/** Shows the outcome of a request on the key: OK on success, the alert triangle (and a log line) on failure. */
	protected async outcome(target: AnyAction, work: () => Promise<unknown>): Promise<void> {
		try {
			await work();
			if (!target.isDial()) await (target as KeyAction<KeySettings>).showOk();
		} catch (error) {
			streamDeck.logger.warn(`Cuebot request failed: ${error instanceof Error ? error.message : String(error)}`);
			await target.showAlert();
		}
	}
}

/** Runs a Cuebot action with optional arguments. */
@action({ UUID: ACTION_UUIDS.run })
export class RunActionKey extends CuebotAction {
	readonly kind = "run" as const;

	override async onKeyDown(ev: KeyDownEvent<KeySettings>): Promise<void> {
		if ((ev.payload.settings.runOn ?? "down") === "down") await this.run(ev.action, ev.payload.settings);
	}

	override async onKeyUp(ev: KeyUpEvent<KeySettings>): Promise<void> {
		if (ev.payload.settings.runOn === "up") await this.run(ev.action, ev.payload.settings);
	}

	private run(target: KeyAction<KeySettings>, settings: KeySettings): Promise<void> {
		return this.outcome(target, () => {
			const id = settings.actionId || settings.actionName;
			if (!id) throw new Error("Pick a Cuebot action in the key's settings");
			return client.request("runAction", { action: id, args: { ...parseArgs(settings.args), buttonId: settings.buttonId ?? "" } });
		});
	}
}

/** Enables, disables or toggles a Cuebot action; state 0 shows enabled, state 1 disabled. */
@action({ UUID: ACTION_UUIDS.toggle })
export class ToggleActionKey extends CuebotAction {
	readonly kind = "toggle" as const;

	constructor() {
		super();
		client.onActionsChanged((actions) => this.showAll(actions));
		client.onStatus((status) => {
			if (status.state === "connected") void client.request<{ actions: ActionSummary[] }>("getActions").then((r) => this.showAll(r.actions)).catch(() => undefined);
		});
	}

	override async onKeyDown(ev: KeyDownEvent<KeySettings>): Promise<void> {
		const settings = ev.payload.settings;
		await this.outcome(ev.action, async () => {
			const id = settings.actionId || settings.actionName;
			if (!id) throw new Error("Pick a Cuebot action in the key's settings");
			const mode = settings.mode ?? "toggle";
			const result = await client.request<ActionSummary>("setActionEnabled", {
				action: id,
				enabled: mode === "toggle" ? "toggle" : mode === "enable",
			});
			await ev.action.setState(result.enabled ? 0 : 1);
		});
	}

	protected override refresh(target: AnyAction, settings: KeySettings): void {
		const actions = cachedActions();
		if (actions && !target.isDial()) void this.show(target as KeyAction<KeySettings>, settings, actions);
	}

	private showAll(actions: ActionSummary[]): void {
		for (const a of this.actions) {
			const s = this.settings.get(a.id);
			if (s && !a.isDial()) void this.show(a as KeyAction<KeySettings>, s, actions);
		}
	}

	private async show(target: KeyAction<KeySettings>, settings: KeySettings, actions: ActionSummary[]): Promise<void> {
		const found = actions.find((x) => x.id === settings.actionId) ?? actions.find((x) => x.name.toLowerCase() === (settings.actionName ?? "").toLowerCase());
		if (!found) return;
		const state = found.enabled ? 0 : 1;
		registry.setLocalState(target.id, state);
		await target.setState(state);
	}
}

/** Sets, adds to or toggles a global variable; on a dial, rotating adds the step per tick. */
@action({ UUID: ACTION_UUIDS.global })
export class SetGlobalKey extends CuebotAction {
	readonly kind = "global" as const;

	constructor() {
		super();
		client.onGlobalChanged((name, value) => {
			for (const a of this.actions) {
				const s = this.settings.get(a.id);
				if (s?.showValue && (s.variable ?? "").trim().toLowerCase() === name.toLowerCase()) void showValue(a, value ?? "");
			}
		});
		client.onStatus((status) => {
			if (status.state === "connected") void this.showAllValues();
		});
	}

	protected override refresh(target: AnyAction, settings: KeySettings): void {
		if (settings.showValue && client.isConnected) void this.showAllValues(target.id);
	}

	/** Shows the current value on keys with "Show the value" (all of them, or one). */
	private async showAllValues(only?: string): Promise<void> {
		const wanted = [...this.actions].filter((a) => (only === undefined || a.id === only) && this.settings.get(a.id)?.showValue);
		if (wanted.length === 0) return;
		try {
			const result = await client.request<{ variables: { name: string; value: unknown }[] }>("getGlobals");
			for (const a of wanted) {
				const name = (this.settings.get(a.id)?.variable ?? "").trim().toLowerCase();
				const found = result.variables.find((v) => v.name.toLowerCase() === name);
				if (found) await showValue(a, typeof found.value === "string" ? found.value : JSON.stringify(found.value));
			}
		} catch {
			// not connected; the next connect refreshes
		}
	}

	override onKeyDown(ev: KeyDownEvent<KeySettings>): Promise<void> {
		return this.apply(ev.action, ev.payload.settings);
	}

	override onDialDown(ev: DialDownEvent<KeySettings>): Promise<void> {
		return this.apply(ev.action, ev.payload.settings);
	}

	override async onDialRotate(ev: DialRotateEvent<KeySettings>): Promise<void> {
		const step = Number((ev.payload.settings.step ?? "1").trim() || "1");
		await this.outcome(ev.action, () =>
			client.request("setGlobal", {
				name: required(ev.payload.settings.variable),
				value: (Number.isFinite(step) ? step : 1) * ev.payload.ticks,
				op: "add",
				persisted: ev.payload.settings.persisted ?? true,
			}),
		);
	}

	private apply(target: AnyAction, settings: KeySettings): Promise<void> {
		return this.outcome(target, () =>
			client.request("setGlobal", {
				name: required(settings.variable),
				value: settings.value ?? "",
				op: settings.op ?? "set",
				persisted: settings.persisted ?? true,
			}),
		);
	}
}

/** Fires Cuebot's Stream Deck Action trigger with the key's button id, the event and its arguments. */
@action({ UUID: ACTION_UUIDS.trigger })
export class TriggerKey extends CuebotAction {
	readonly kind = "trigger" as const;

	override onKeyDown(ev: KeyDownEvent<KeySettings>): void {
		this.fire(ev.action, "keyDown", ev.payload.settings, { state: ev.payload.state });
	}

	override onKeyUp(ev: KeyUpEvent<KeySettings>): void {
		this.fire(ev.action, "keyUp", ev.payload.settings, { state: ev.payload.state });
	}

	override onDialDown(ev: DialDownEvent<KeySettings>): void {
		this.fire(ev.action, "dialDown", ev.payload.settings, {});
	}

	override onDialUp(ev: DialUpEvent<KeySettings>): void {
		this.fire(ev.action, "dialUp", ev.payload.settings, {});
	}

	override onDialRotate(ev: DialRotateEvent<KeySettings>): void {
		this.fire(ev.action, "dialRotate", ev.payload.settings, { ticks: ev.payload.ticks, pressed: ev.payload.pressed });
	}

	override onTouchTap(ev: TouchTapEvent<KeySettings>): void {
		this.fire(ev.action, "touchTap", ev.payload.settings, { hold: ev.payload.hold, tapPos: [ev.payload.tapPos[0], ev.payload.tapPos[1]] });
	}

	private fire(target: AnyAction, event: InputEvent, settings: KeySettings, extra: { state?: number; ticks?: number; pressed?: boolean; hold?: boolean; tapPos?: [number, number] }): void {
		const sent = client.sendInput({
			event,
			context: target.id,
			buttonId: settings.buttonId ?? "",
			controller: target.isDial() ? "Encoder" : "Keypad",
			device: target.device.id,
			args: parseArgs(settings.args),
			...extra,
		});
		if (!sent && (event === "keyDown" || event === "dialDown" || event === "touchTap")) void target.showAlert();
	}
}

function required(variable: string | undefined): string {
	const name = (variable ?? "").trim();
	if (name.length === 0) throw new Error("Enter a global variable name in the key's settings");
	return name;
}

function showValue(target: AnyAction, value: string): Promise<void> {
	return target.isDial() ? target.setFeedback({ value }) : (target as KeyAction<KeySettings>).setTitle(value);
}
