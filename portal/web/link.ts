// One encrypted connection from this browser to the streamer's Cuebot through the relay: the handshake pinned to the
// invite's fingerprint, then JSON requests and answers matched by "req". docs/MOD_PORTAL_PLAN.md, section 3.

import { clientHello, finishHandshake, newEphemeral, type Bytes, type PortalSession } from "../src/crypto.js";
import { closeReason } from "./flow.js";

export type Answer = { ok: boolean; error?: string; signIn?: boolean; [key: string]: unknown };

export class LinkClosed extends Error {}

export class Link {
  private next = 1;
  private readonly waiting = new Map<number, (answer: Answer) => void>();
  private failure: string | null = null;
  // Frames carry counter nonces, so they are encrypted, sent and decrypted strictly one after another.
  private inbox: Promise<void> = Promise.resolve();
  private outbox: Promise<void> = Promise.resolve();

  private constructor(private readonly ws: WebSocket, readonly session: PortalSession, private readonly onClose: (reason: string) => void) {
    ws.onmessage = (e) => {
      this.inbox = this.inbox.then(() => this.receive(e.data as ArrayBuffer));
    };
    ws.onclose = (e) => this.fail(closeReason(e.code));
  }

  /** Connects to `<relay origin>/c/<relay id>` and completes the handshake; rejects with words for the mod. */
  static async open(relayOrigin: string, relay: string, fingerprint: string, onClose: (reason: string) => void): Promise<Link> {
    const ephemeral = await newEphemeral();
    const ws = new WebSocket(`${relayOrigin}/c/${relay}`);
    ws.binaryType = "arraybuffer";
    const hello = await new Promise<Bytes>((resolve, reject) => {
      const timer = setTimeout(() => reject(new LinkClosed("The streamer's Cuebot did not answer.")), 15_000);
      ws.onopen = async () => ws.send(await clientHello(ephemeral));
      ws.onmessage = (e) => {
        clearTimeout(timer);
        resolve(new Uint8Array(e.data as ArrayBuffer));
      };
      ws.onclose = (e) => {
        clearTimeout(timer);
        reject(new LinkClosed(closeReason(e.code)));
      };
    });
    try {
      return new Link(ws, await finishHandshake(ephemeral, hello, fingerprint), onClose);
    } catch {
      ws.close(1000);
      throw new LinkClosed("This link does not lead to the streamer's Cuebot it was made for. Ask the streamer for a new invite.");
    }
  }

  /** Sends one request and waits for its answer. */
  async request(op: string, body: Record<string, unknown> = {}): Promise<Answer> {
    if (this.failure) throw new LinkClosed(this.failure);
    const req = this.next++;
    const answer = new Promise<Answer>((resolve) => this.waiting.set(req, resolve));
    const plain = new TextEncoder().encode(JSON.stringify({ ...body, op, req })) as Bytes;
    this.outbox = this.outbox.then(async () => this.ws.send(await this.session.encrypt(plain)));
    await this.outbox;
    return answer;
  }

  close(): void {
    this.ws.onclose = null;
    this.ws.close(1000);
  }

  private async receive(data: ArrayBuffer): Promise<void> {
    try {
      const answer = JSON.parse(new TextDecoder().decode(await this.session.decrypt(new Uint8Array(data)))) as Answer & { req?: number };
      const done = typeof answer.req === "number" ? this.waiting.get(answer.req) : undefined;
      if (done) {
        this.waiting.delete(answer.req!);
        done(answer);
      }
    } catch {
      this.ws.close(1000);
      this.fail("The secure connection failed. Reload the page.");
    }
  }

  private fail(reason: string): void {
    if (this.failure) return;
    this.failure = reason;
    for (const done of this.waiting.values()) done({ ok: false, error: reason });
    this.waiting.clear();
    this.onClose(reason);
  }
}
