// What the loader hands the portal UI that Cuebot sends (ui/, built into src/Cuebot.Portal/Web): the signed-in link.
// The UI module exports `mount(root, host)`.

export type Answer = { ok: boolean; error?: string; signIn?: boolean; [key: string]: unknown };

export interface PortalHost {
  /** The welcome answer: the member's name, role, permissions and level (PortalApi "me"). */
  readonly me: Answer;
  /** One request to Cuebot over the encrypted link (PortalApi). */
  request(op: string, body?: Record<string, unknown>): Promise<Answer>;
  /** Signs this device out and forgets it; the loader then shows its signed-out page. */
  signOut(): Promise<void>;
}

export interface PortalUiModule {
  mount(root: HTMLElement, host: PortalHost): void;
}
