// The mod portal UI. Cuebot embeds the build of this folder (src/Cuebot.Portal/Web) and sends it to a signed-in
// device; the loader (web/main.ts) runs it with mount(). Every request goes over the encrypted link, and Cuebot
// re-checks the member's role on each one: hiding a page here is only for clarity.

import { render } from "preact";
import type { PortalHost } from "../web/host.js";
import { App } from "./app.js";
import "./portal.css";

export function mount(root: HTMLElement, host: PortalHost): void {
  render(<App host={host} />, root);
}
