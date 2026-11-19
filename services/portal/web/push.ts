// Server push on the encrypted link (services/portal/RELAY.md, "Push"): the device subscribes to a topic, Cuebot sends
// {"push": topic, "sub": n, ...} frames as things happen. Subscriptions outlive one link: after a reconnect they are
// made again and each one is told to resync, because whatever happened in between was missed.

import type { Answer, PushHandlers } from "./host.js";

export type { PushHandlers };

/** A frame Cuebot pushed (no "req": it answers nothing). */
export interface PushFrame {
  push: string;
  sub: number;
  events?: unknown[];
  resync?: boolean;
  missed?: number;
  ended?: boolean;
  error?: string;
}

export function isPushFrame(message: unknown): message is PushFrame {
  const m = message as Partial<PushFrame> | null;
  return typeof m === "object" && m !== null && typeof m.push === "string" && typeof m.sub === "number";
}

/** What subscriptions are sent over: the current link. */
export interface Requester {
  request(op: string, body?: Record<string, unknown>): Promise<Answer>;
}

interface Entry {
  topic: string;
  params: Record<string, unknown>;
  handlers: PushHandlers;
  sub: number | null;
  /** Bumped on every (re)subscribe, so an answer from an older link is ignored. */
  generation: number;
  /** Was live on an earlier link: a new subscription means events were missed. */
  wasLive: boolean;
  closed: boolean;
}

/** Frames for a subscription whose answer has not been read yet are kept, at most this many. */
const EARLY_FRAMES = 64;

export class Subscriptions {
  private link: Requester | null = null;
  private readonly entries = new Set<Entry>();
  private readonly live = new Map<number, Entry>();
  private early: PushFrame[] = [];

  /**
   * Subscribes now (or as soon as a link is attached) and after every reconnect. Returns the unsubscribe function.
   * A refusal (old Cuebot, no permission) or an "ended" frame calls onEnd and is not retried.
   */
  subscribe(topic: string, params: Record<string, unknown>, handlers: PushHandlers): () => void {
    const entry: Entry = { topic, params, handlers, sub: null, generation: 0, wasLive: false, closed: false };
    this.entries.add(entry);
    if (this.link) this.start(entry, this.link);
    return () => this.stop(entry);
  }

  /** A (new) link is ready: subscribe everything again on it. */
  attach(link: Requester): void {
    this.detach();
    this.link = link;
    for (const entry of this.entries) this.start(entry, link);
  }

  /** The link was lost: subscriptions wait for the next one. */
  detach(): void {
    this.link = null;
    this.live.clear();
    this.early = [];
    for (const entry of this.entries) {
      entry.generation++;
      if (entry.sub !== null) entry.wasLive = true;
      entry.sub = null;
    }
  }

  /** Hands a pushed frame to its subscription. */
  deliver(frame: PushFrame): void {
    const entry = this.live.get(frame.sub);
    if (!entry) {
      // The subscribe answer may still be on its way through the page's promise queue.
      if (this.early.length < EARLY_FRAMES) this.early.push(frame);
      return;
    }
    if (frame.ended) {
      this.forget(entry);
      call(() => entry.handlers.onEnd?.(frame.error ?? "Cuebot ended the subscription."));
      return;
    }
    if (frame.resync) call(() => entry.handlers.onResync?.(typeof frame.missed === "number" ? frame.missed : null));
    if (Array.isArray(frame.events) && frame.events.length > 0) call(() => entry.handlers.onEvents(frame.events!));
  }

  /** Subscriptions waiting or live (for tests and status). */
  get size(): number {
    return this.entries.size;
  }

  private start(entry: Entry, link: Requester): void {
    const generation = ++entry.generation;
    entry.sub = null;
    link.request("subscribe", { topic: entry.topic, params: entry.params }).then(
      (answer) => this.started(entry, link, generation, answer),
      () => undefined, // the link failed; detach/attach handles it
    );
  }

  private started(entry: Entry, link: Requester, generation: number, answer: Answer): void {
    if (generation !== entry.generation || this.link !== link) return;
    if (entry.closed) {
      if (answer.ok && typeof answer.sub === "number") void link.request("unsubscribe", { sub: answer.sub }).catch(() => undefined);
      return;
    }
    if (!answer.ok || typeof answer.sub !== "number") {
      this.forget(entry);
      call(() => entry.handlers.onEnd?.(answer.error ?? "Cuebot did not accept the subscription."));
      return;
    }
    entry.sub = answer.sub;
    this.live.set(answer.sub, entry);
    if (entry.wasLive) call(() => entry.handlers.onResync?.(null));
    entry.wasLive = true;
    const early = this.early.filter((f) => f.sub === answer.sub);
    this.early = this.early.filter((f) => f.sub !== answer.sub);
    for (const frame of early) this.deliver(frame);
  }

  private stop(entry: Entry): void {
    if (entry.closed) return;
    entry.closed = true;
    const sub = entry.sub;
    this.forget(entry);
    if (sub !== null && this.link) void this.link.request("unsubscribe", { sub }).catch(() => undefined);
  }

  private forget(entry: Entry): void {
    this.entries.delete(entry);
    if (entry.sub !== null) this.live.delete(entry.sub);
    entry.sub = null;
  }
}

/** A page's handler that throws must not stop the other subscriptions. */
function call(run: () => void): void {
  try {
    run();
  } catch (e) {
    console.error(e);
  }
}
