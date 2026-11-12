import type { ComponentType } from "preact";
import { useMemo, useState } from "preact/hooks";
import type { PortalHost } from "../web/host.js";
import { PortalContext, type Me, type Permission, type Portal } from "./api.js";
import { ActionsPage } from "./pages/actions.js";
import { AssistantPage } from "./pages/assistant.js";
import { AudioPage } from "./pages/audio.js";
import { ChangesPage } from "./pages/changes.js";
import { CommandsPage } from "./pages/commands.js";
import { HomePage } from "./pages/home.js";
import { OverlaysPage } from "./pages/overlays.js";
import { QueuesPage } from "./pages/queues.js";
import { ScenesPage } from "./pages/scenes.js";
import { SoundsPage } from "./pages/sounds.js";
import { SpeechPage } from "./pages/speech.js";
import { UploadsPage } from "./pages/uploads.js";

interface PageInfo {
  id: string;
  title: string;
  page: ComponentType;
  /** Shown when the role has any of these (none = always). */
  needs?: Permission[];
}

export const PAGES: PageInfo[] = [
  { id: "home", title: "Home", page: HomePage },
  { id: "assistant", title: "Assistant", page: AssistantPage, needs: ["UseAssistant"] },
  { id: "actions", title: "Actions", page: ActionsPage, needs: ["RunActions", "CreateActions", "EditActions", "DeleteActions"] },
  { id: "commands", title: "Commands", page: CommandsPage, needs: ["CreateCommands", "EditCommands", "DeleteCommands"] },
  { id: "sounds", title: "Sounds", page: SoundsPage, needs: ["RunActions"] },
  { id: "overlays", title: "Overlays", page: OverlaysPage, needs: ["EditOverlays", "CreateOverlays"] },
  { id: "uploads", title: "Uploads", page: UploadsPage, needs: ["UploadSounds", "UploadMedia"] },
  { id: "audio", title: "Audio", page: AudioPage, needs: ["AudioLevels"] },
  { id: "scenes", title: "Scenes", page: ScenesPage, needs: ["StreamControl"] },
  { id: "queues", title: "Queues", page: QueuesPage, needs: ["StreamControl"] },
  { id: "speech", title: "Text-to-speech", page: SpeechPage, needs: ["RunActions", "StreamControl"] },
  { id: "changes", title: "My changes", page: ChangesPage },
];

export function App({ host }: { host: PortalHost }) {
  const me = host.me as unknown as Me;
  const [notice, setNotice] = useState<{ text: string; kind: "ok" | "error" } | null>(null);
  const portal = useMemo<Portal>(() => {
    const can = (p: Permission) => me.permissions?.includes(p) ?? false;
    const notify = (text: string, kind: "ok" | "error" = "ok") => {
      setNotice({ text, kind });
      setTimeout(() => setNotice((n) => (n?.text === text ? null : n)), kind === "error" ? 8000 : 4000);
    };
    return {
      host, me, can, notify,
      async call(op, body) {
        const answer = await host.request(op, body).catch((e: unknown) => ({ ok: false, error: e instanceof Error ? e.message : String(e) }));
        if (!answer.ok && answer.error) notify(answer.error, "error");
        return answer;
      },
    };
  }, [host]);

  const pages = PAGES.filter((p) => !p.needs || p.needs.some(portal.can));
  const [current, setCurrent] = useState(() => pages.find((p) => `#${p.id}` === location.hash)?.id ?? "home");
  const page = pages.find((p) => p.id === current) ?? pages[0]!;
  const go = (id: string) => {
    setCurrent(id);
    setNotice(null);
    window.scrollTo(0, 0);
    history.replaceState(null, "", `#${id}`);
  };
  const Page = page.page;

  return (
    <PortalContext.Provider value={portal}>
      <div class="shell">
        <header class="top">
          <div class="brand">Cuebot <span>mod portal</span></div>
          <div class="who">
            <span>{me.name}</span>
            <span class="badge">{me.role}</span>
            <button type="button" class="link" onClick={() => void host.signOut()}>Sign out</button>
          </div>
        </header>
        <nav class="nav" aria-label="Pages">
          {pages.map((p) => (
            <button type="button" key={p.id} class={p.id === page.id ? "active" : ""} aria-current={p.id === page.id ? "page" : undefined}
              onClick={() => go(p.id)}>{p.title}</button>
          ))}
        </nav>
        <main class="content">
          {notice && <div class={`notice ${notice.kind}`} role="status">{notice.text}</div>}
          <Page key={page.id} />
        </main>
      </div>
    </PortalContext.Provider>
  );
}
