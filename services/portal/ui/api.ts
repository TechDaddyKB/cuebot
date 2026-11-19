// Shared state for the pages: the link to Cuebot, the member, and small helpers.

import { createContext } from "preact";
import { useContext, useEffect, useRef, useState } from "preact/hooks";
import type { Answer, PortalHost, PushHandlers } from "../web/host.js";

export type { Answer, PushHandlers };

/** What the member's role allows (PortalPermission in src/Cuebot.Portal/Members/PortalModel.cs). */
export type Permission =
  | "RunActions" | "CreateCommands" | "EditCommands" | "DeleteCommands" | "CreateActions" | "EditActions" | "DeleteActions"
  | "AudioLevels" | "StreamControl" | "ShowCritical" | "UploadSounds" | "UploadMedia" | "EditOverlays" | "CreateOverlays"
  | "ViewActivity" | "ViewLogs" | "UndoOthers" | "UseAssistant" | "ManageRewards"
  | "ViewChat" | "ChatDeleteTimeout" | "ChatBan" | "ChatModes" | "BlockedTerms" | "ViewModLog" | "NotesRead" | "NotesAdd"
  | "NotesDelete" | "ProposeLinks" | "ConfirmLinks" | "LinkedTimeouts" | "LinkedBans";

export interface Me {
  name: string;
  role: string;
  maxRisk: number;
  permissions: Permission[];
  changes: "Live" | "LiveAndNotify" | "NeedsApproval";
}

export interface Portal {
  host: PortalHost;
  me: Me;
  can(p: Permission): boolean;
  /** Sends a request; failures become a message at the top of the page and an answer with ok false. */
  call(op: string, body?: Record<string, unknown>): Promise<Answer>;
  notify(text: string, kind?: "ok" | "error"): void;
}

export const PortalContext = createContext<Portal | null>(null);

export function usePortal(): Portal {
  return useContext(PortalContext)!;
}

/** Loads with `op` when shown, again every `refreshMs` (0 = never), and on reload(). */
export function useLoad<T extends Answer>(op: string, refreshMs = 0, body?: Record<string, unknown>): [T | null, () => void] {
  const { host } = usePortal();
  // Loading errors are shown on the page (LoadError), not as notices: a refresh would repeat them.
  const call = (o: string, b?: Record<string, unknown>) =>
    host.request(o, b).catch((e: unknown) => ({ ok: false, error: e instanceof Error ? e.message : String(e) }) as Answer);
  const [data, setData] = useState<T | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    const load = () => void call(op, body).then((a) => live && setData(a as T));
    load();
    const timer = refreshMs > 0 ? setInterval(load, refreshMs) : undefined;
    return () => {
      live = false;
      if (timer) clearInterval(timer);
    };
  }, [op, tick, refreshMs]);
  return [data, () => setTick((t) => t + 1)];
}

/** What a saved change did: live at once, or waiting for the streamer. */
export function changeMessage(answer: Answer): string {
  if (answer.status === "unchanged") return "Nothing had changed.";
  return answer.status === "pending" ? "Sent to the streamer for approval." : "Saved. It's live.";
}

export const LEVELS = ["", "Basic", "Stream control", "Show-critical", "Trusted tech"];

/** Running an action with show-critical steps (level 3 and up: ending the stream, bans, title) asks first. */
export function confirmRun(name: string, risk: number | undefined): boolean {
  return (risk ?? 0) < 3 || confirm(`"${name}" can change the stream itself (for example end it, ban someone or change the title). Run it?`);
}

/**
 * Subscribes to a push topic on the encrypted link (services/portal/RELAY.md, "Push"); the loader subscribes again after a
 * reconnect and then calls onResync. Returns the unsubscribe function, or null when the loader is older than push
 * (the page should keep polling then).
 */
export function subscribe(host: PortalHost, topic: string, params: Record<string, unknown>, handlers: PushHandlers): (() => void) | null {
  return host.subscribe ? host.subscribe(topic, params, handlers) : null;
}

/**
 * subscribe() for a page: subscribed while shown and whenever `topic` or the params (compared as JSON) change; the
 * latest handlers are always used. Returns false when push is not available, so the page can fall back to useLoad
 * with a refresh.
 */
export function useSubscription(topic: string, params: Record<string, unknown>, handlers: PushHandlers): boolean {
  const { host } = usePortal();
  const latest = useRef(handlers);
  latest.current = handlers;
  const key = JSON.stringify(params);
  useEffect(() => {
    const stop = subscribe(host, topic, JSON.parse(key) as Record<string, unknown>, {
      onEvents: (events) => latest.current.onEvents(events),
      onResync: (missed) => latest.current.onResync?.(missed),
      onEnd: (error) => latest.current.onEnd?.(error),
    });
    return () => stop?.();
  }, [host, topic, key]);
  return typeof host.subscribe === "function";
}
