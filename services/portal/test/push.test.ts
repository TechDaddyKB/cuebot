import { describe, expect, it, vi } from "vitest";
import type { Answer } from "../web/host.js";
import { isPushFrame, Subscriptions, type Requester } from "../web/push.js";

/** A link that records requests and answers them when the test says so. */
class FakeLink implements Requester {
  readonly sent: { op: string; body: Record<string, unknown>; answer: (a: Answer) => void }[] = [];
  private nextSub = 1;

  request(op: string, body: Record<string, unknown> = {}): Promise<Answer> {
    return new Promise((answer) => this.sent.push({ op, body, answer }));
  }

  /** Answers every waiting subscribe with a new sub id (or the given refusal). */
  acceptAll(): number[] {
    const ids: number[] = [];
    for (const r of this.sent.splice(0)) {
      if (r.op !== "subscribe") continue;
      const sub = this.nextSub++;
      ids.push(sub);
      r.answer({ ok: true, sub, topic: r.body.topic });
    }
    return ids;
  }
}

const flush = () => new Promise((r) => setTimeout(r, 0));

function handlers() {
  return { onEvents: vi.fn(), onResync: vi.fn(), onEnd: vi.fn() };
}

describe("push subscriptions", () => {
  it("recognises pushed frames", () => {
    expect(isPushFrame({ push: "chat", sub: 1, events: [] })).toBe(true);
    expect(isPushFrame({ ok: true, req: 1 })).toBe(false);
    expect(isPushFrame(null)).toBe(false);
  });

  it("subscribes with topic and params, delivers events, and unsubscribes", async () => {
    const subs = new Subscriptions();
    const link = new FakeLink();
    subs.attach(link);
    const h = handlers();
    const stop = subs.subscribe("chat", { platform: "twitch" }, h);
    expect(link.sent[0]).toMatchObject({ op: "subscribe", body: { topic: "chat", params: { platform: "twitch" } } });
    const [sub] = link.acceptAll();
    await flush();

    subs.deliver({ push: "chat", sub: sub!, events: [{ n: 1 }, { n: 2 }] });
    expect(h.onEvents).toHaveBeenCalledWith([{ n: 1 }, { n: 2 }]);
    expect(h.onResync).not.toHaveBeenCalled();

    stop();
    expect(link.sent[0]).toMatchObject({ op: "unsubscribe", body: { sub } });
    subs.deliver({ push: "chat", sub: sub!, events: [{ n: 3 }] });
    expect(h.onEvents).toHaveBeenCalledTimes(1);
    expect(subs.size).toBe(0);
  });

  it("waits for a link, and keeps frames that arrive just before the answer is read", async () => {
    const subs = new Subscriptions();
    const h = handlers();
    subs.subscribe("chat", {}, h);
    const link = new FakeLink();
    subs.attach(link);
    expect(link.sent).toHaveLength(1);
    subs.deliver({ push: "chat", sub: 1, events: ["early"] });
    link.acceptAll();
    await flush();
    expect(h.onEvents).toHaveBeenCalledWith(["early"]);
  });

  it("subscribes again after a reconnect and asks the page to resync", async () => {
    const subs = new Subscriptions();
    const first = new FakeLink();
    subs.attach(first);
    const h = handlers();
    subs.subscribe("chat", {}, h);
    first.acceptAll();
    await flush();

    subs.detach();
    const second = new FakeLink();
    subs.attach(second);
    expect(second.sent[0]).toMatchObject({ op: "subscribe", body: { topic: "chat" } });
    second.acceptAll();
    await flush();
    expect(h.onResync).toHaveBeenCalledWith(null);
    subs.deliver({ push: "chat", sub: 1, events: ["new"] });
    expect(h.onEvents).toHaveBeenCalledWith(["new"]);
  });

  it("ignores an answer from a link that was already lost", async () => {
    const subs = new Subscriptions();
    const first = new FakeLink();
    subs.attach(first);
    const h = handlers();
    subs.subscribe("chat", {}, h);
    const late = first.sent.splice(0)[0]!;
    subs.detach();
    late.answer({ ok: false, error: "The connection to the streamer's Cuebot was lost." });
    await flush();
    expect(h.onEnd).not.toHaveBeenCalled();
    expect(subs.size).toBe(1);
  });

  it("passes on resync frames with the missed count", async () => {
    const subs = new Subscriptions();
    const link = new FakeLink();
    subs.attach(link);
    const h = handlers();
    subs.subscribe("chat", {}, h);
    const [sub] = link.acceptAll();
    await flush();
    subs.deliver({ push: "chat", sub: sub!, resync: true, missed: 501 });
    expect(h.onResync).toHaveBeenCalledWith(501);
    expect(h.onEvents).not.toHaveBeenCalled();
  });

  it("reports a refusal or an ended subscription once and does not retry it", async () => {
    const subs = new Subscriptions();
    const link = new FakeLink();
    subs.attach(link);
    const refused = handlers();
    subs.subscribe("chat", {}, refused);
    link.sent.splice(0)[0]!.answer({ ok: false, error: "Your role does not include this." });
    await flush();
    expect(refused.onEnd).toHaveBeenCalledWith("Your role does not include this.");

    const ended = handlers();
    subs.subscribe("chat", {}, ended);
    const [sub] = link.acceptAll();
    await flush();
    subs.deliver({ push: "chat", sub: sub!, ended: true, error: "This device was signed out." });
    expect(ended.onEnd).toHaveBeenCalledWith("This device was signed out.");
    expect(subs.size).toBe(0);

    subs.detach();
    const next = new FakeLink();
    subs.attach(next);
    expect(next.sent).toHaveLength(0);
  });

  it("unsubscribes when the page stops before Cuebot answered", async () => {
    const subs = new Subscriptions();
    const link = new FakeLink();
    subs.attach(link);
    const h = handlers();
    const stop = subs.subscribe("chat", {}, h);
    stop();
    link.acceptAll();
    await flush();
    expect(link.sent[0]).toMatchObject({ op: "unsubscribe", body: { sub: 1 } });
    expect(h.onEnd).not.toHaveBeenCalled();
  });

  it("keeps delivering when one page's handler throws", async () => {
    const subs = new Subscriptions();
    const link = new FakeLink();
    subs.attach(link);
    const bad = { onEvents: () => { throw new Error("page bug"); } };
    const good = handlers();
    subs.subscribe("chat", {}, bad);
    subs.subscribe("chat", {}, good);
    const [a, b] = link.acceptAll();
    await flush();
    const error = vi.spyOn(console, "error").mockImplementation(() => undefined);
    subs.deliver({ push: "chat", sub: a!, events: [1] });
    subs.deliver({ push: "chat", sub: b!, events: [1] });
    error.mockRestore();
    expect(good.onEvents).toHaveBeenCalledWith([1]);
  });
});
