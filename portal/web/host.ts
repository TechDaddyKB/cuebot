// What the loader hands the portal UI that Cuebot sends (ui/, built into src/Cuebot.Portal/Web): the signed-in link.
// The UI module exports `mount(root, host)`.

export type Answer = { ok: boolean; error?: string; signIn?: boolean; [key: string]: unknown };

/** What a page hands to a push subscription (web/push.ts). */
export interface PushHandlers {
  /** Events Cuebot pushed, in order, batched (up to 100 a frame, about every 250 ms). */
  onEvents(events: unknown[]): void;
  /**
   * Events were missed: the device fell behind (`missed` says how many) or the link reconnected (`null`). Load the
   * current state again; events that follow are newer than the drop.
   */
  onResync?(missed: number | null): void;
  /** Cuebot refused or ended the subscription (no permission, signed out, an older Cuebot). It is not retried. */
  onEnd?(error: string): void;
}

export interface PortalHost {
  /** The welcome answer: the member's name, role, permissions and level (PortalApi "me"). */
  readonly me: Answer;
  /** One request to Cuebot over the encrypted link (PortalApi). */
  request(op: string, body?: Record<string, unknown>): Promise<Answer>;
  /**
   * Subscribes to a push topic (for example "chat") with optional params; resubscribes after a reconnect. Returns the
   * unsubscribe function. Missing on loaders older than push: pages then keep polling.
   */
  subscribe?(topic: string, params: Record<string, unknown>, handlers: PushHandlers): () => void;
  /** Signs this device out and forgets it; the loader then shows its signed-out page. */
  signOut(): Promise<void>;
}

export interface PortalUiModule {
  mount(root: HTMLElement, host: PortalHost): void;
}
