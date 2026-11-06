// Mounts the portal UI the streamer's Cuebot sends (portal/ui, `mount(root, host)`) with a host whose requests go to
// the app: messages to the `cuebotApp` handler, answers back through window.cuebotAppReceive (Views/PortalWebView.swift,
// which takes messages only from this page's main frame). The app pinned that Cuebot at sign-in and checks every frame,
// as the web loader does (portal/web/main.ts). The Android app's assets/portal/host.js is the same apart from the bridge.

const handler = window.webkit.messageHandlers.cuebotApp;
const app = { postMessage: (text) => handler.postMessage(text), onmessage: null };
window.cuebotAppReceive = (data) => app.onmessage?.({ data });
const waiting = new Map();
let next = 1;

function send(message) {
  app.postMessage(JSON.stringify(message));
}

function request(op, body = {}) {
  const id = next++;
  return new Promise((resolve) => {
    waiting.set(id, resolve);
    send({ type: "request", id, op, body });
  });
}

async function mount({ js, css, me }) {
  const style = document.createElement("link");
  style.rel = "stylesheet";
  style.href = URL.createObjectURL(new Blob([css], { type: "text/css" }));
  document.head.append(style);
  const url = URL.createObjectURL(new Blob([js], { type: "text/javascript" }));
  try {
    const module = await import(url);
    const main = document.querySelector("main");
    main.replaceChildren();
    document.body.classList.add("portal");
    module.mount(main, { me, request, signOut: async () => send({ type: "signOut" }) });
    send({ type: "mounted" });
  } catch (e) {
    send({ type: "failed", error: String(e) });
  } finally {
    URL.revokeObjectURL(url);
  }
}

app.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.type === "mount") {
    void mount(message);
  } else if (message.type === "answer") {
    const done = waiting.get(message.id);
    if (done) {
      waiting.delete(message.id);
      done(message.answer);
    }
  }
};
send({ type: "ready" });
