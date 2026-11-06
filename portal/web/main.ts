// The mod portal loader on portal.<domain>: opens an invite, signs the mod in with Google, Twitch or Discord, and
// keeps this device's key so it can come back without signing in again. The portal itself arrives in a later phase
// (docs/MOD_PORTAL_PLAN.md, section 11). No inline script or style: the page's CSP forbids both.

import { base64Url, deviceProof, rawPublicKey, type Bytes } from "../src/crypto.js";
import { authorizeUrl, deviceName, parseInvite, random, readReturn, relayOrigin, type PendingSignIn, type Provider, type Target } from "./flow.js";
import type { PortalHost, PortalUiModule } from "./host.js";
import { Link, LinkClosed, type Answer } from "./link.js";

/** A device signed in to one streamer's Cuebot. The private key is non-extractable: script can use it, not read it. */
interface SavedDevice {
  relay: string;
  fingerprint: string;
  key: CryptoKeyPair;
  member: string;
  role: string;
  at: number;
}

const PENDING = "cuebot-signin";
const INVITE = "cuebot-invite";
const main = document.querySelector("main")!;
const origin = relayOrigin(location);

// ---- Saved devices (IndexedDB) ----

function db(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open("cuebot-portal", 1);
    open.onupgradeneeded = () => open.result.createObjectStore("devices", { keyPath: "relay" });
    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error);
  });
}

async function store<T>(mode: IDBTransactionMode, run: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const d = await db();
  return new Promise((resolve, reject) => {
    const r = run(d.transaction("devices", mode).objectStore("devices"));
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

const savedDevices = () => store<SavedDevice[]>("readonly", (s) => s.getAll() as IDBRequest<SavedDevice[]>);
const savedDevice = (relay: string) => store<SavedDevice | undefined>("readonly", (s) => s.get(relay) as IDBRequest<SavedDevice | undefined>);
const saveDevice = (d: SavedDevice) => store("readwrite", (s) => s.put(d));
const forgetDevice = (relay: string) => store("readwrite", (s) => s.delete(relay));

async function newDeviceKey(): Promise<CryptoKeyPair> {
  return (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, false, ["sign"])) as CryptoKeyPair;
}

async function proveDevice(link: Link, key: CryptoKeyPair, name?: string): Promise<Record<string, string>> {
  const proof = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key.privateKey, deviceProof(link.session.binding)));
  const device: Record<string, string> = { key: base64Url(await rawPublicKey(key.publicKey)), proof: base64Url(proof as Bytes) };
  if (name) device.name = name;
  return device;
}

// ---- Screens ----

function el<K extends keyof HTMLElementTagNameMap>(tag: K, text = "", cls = ""): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (text) e.textContent = text;
  if (cls) e.className = cls;
  return e;
}

function show(title: string, ...content: (Node | string)[]): void {
  main.replaceChildren(el("h1", title), ...content.map((c) => (typeof c === "string" ? el("p", c) : c)));
}

function button(text: string, onClick: () => void, cls = ""): HTMLButtonElement {
  const b = el("button", text, cls);
  b.type = "button";
  b.addEventListener("click", onClick);
  return b;
}

function problem(text: string, retry?: () => void): void {
  show("Something went wrong", el("p", text, "error"), ...(retry ? [button("Try again", retry)] : []));
}

async function connect(target: Target): Promise<Link> {
  return Link.open(origin, target.relay, target.fingerprint, (reason) => problem(reason, () => location.reload()));
}

/** The provider buttons. With an invite the mod joins; without one a member signs in again. */
async function chooseProvider(target: Target): Promise<void> {
  show(target.invite ? "Join as a mod" : "Sign in again", "Connecting to the streamer's Cuebot…");
  let link: Link;
  try {
    link = await connect(target);
  } catch (e) {
    return problem(e instanceof LinkClosed ? e.message : "Could not reach the streamer's Cuebot.", () => void chooseProvider(target));
  }
  const answer = await link.request("providers");
  link.close();
  const providers = (answer.providers as Provider[] | undefined) ?? [];
  if (!answer.ok || providers.length === 0) return problem(answer.error ?? "This Cuebot has no sign-in options set up.");

  const buttons = el("div", "", "providers");
  for (const p of providers) {
    buttons.append(button(`Sign in with ${p.name}`, () => void startSignIn(target, p), `provider ${p.id}`));
  }
  show(target.invite ? "Join as a mod" : "Sign in again",
    target.invite
      ? "The streamer invited you to help with their stream. Sign in so their Cuebot knows it's you. Your account is only used to recognise you; Cuebot does not post or read anything with it."
      : "Your sign-in on this device has expired. Sign in with the same account as before.",
    buttons);
}

async function startSignIn(target: Target, provider: Provider): Promise<void> {
  const pending: PendingSignIn = { ...target, provider: provider.id, state: random(), verifier: random(48) };
  try {
    const url = await authorizeUrl(provider, pending);
    sessionStorage.setItem(PENDING, JSON.stringify(pending));
    location.assign(url);
  } catch (e) {
    problem(e instanceof Error ? e.message : String(e));
  }
}

/** Back from the provider on /auth/return. */
async function finishSignIn(): Promise<void> {
  const raw = sessionStorage.getItem(PENDING);
  sessionStorage.removeItem(PENDING);
  const pending = raw ? (JSON.parse(raw) as PendingSignIn) : null;
  const result = readReturn(location.search, pending);
  // The code is single-use and now in memory; it should not stay in the address bar or history.
  history.replaceState(null, "", "/");
  const again = pending ? () => void chooseProvider(pending) : undefined;
  if ("error" in result) return problem(result.error, again);

  show("Signing in", "Checking your account with the streamer's Cuebot…");
  let link: Link;
  try {
    link = await connect(pending!);
  } catch (e) {
    return problem(e instanceof LinkClosed ? e.message : "Could not reach the streamer's Cuebot.", again);
  }
  const existing = await savedDevice(pending!.relay);
  // A new membership (an invite) gets its own key; signing in again keeps this device's key.
  const key = !pending!.invite && existing?.fingerprint === pending!.fingerprint ? existing.key : await newDeviceKey();
  const body: Record<string, unknown> = {
    provider: pending!.provider,
    code: result.code,
    verifier: pending!.verifier,
    device: await proveDevice(link, key, deviceName(navigator.userAgent)),
  };
  if (pending!.invite) body.invite = pending!.invite;
  const answer = await link.request("signin", body);
  if (!answer.ok) {
    link.close();
    return problem(answer.error ?? "The sign-in failed.", again);
  }
  if (pending!.invite) sessionStorage.removeItem(INVITE);
  await welcome(link, pending!, key, answer);
}

async function resume(device: SavedDevice): Promise<void> {
  show("Mod portal", "Connecting to the streamer's Cuebot…");
  let link: Link;
  try {
    link = await connect(device);
  } catch (e) {
    return problem(e instanceof LinkClosed ? e.message : "Could not reach the streamer's Cuebot.", () => void resume(device));
  }
  const answer = await link.request("resume", { device: await proveDevice(link, device.key) });
  if (answer.signIn) {
    link.close();
    return chooseProvider({ relay: device.relay, fingerprint: device.fingerprint });
  }
  if (!answer.ok) {
    link.close();
    return problem(answer.error ?? "Could not sign in.", () => void resume(device));
  }
  await welcome(link, device, device.key, answer);
}

async function welcome(link: Link, target: Target, key: CryptoKeyPair, me: Answer): Promise<void> {
  const member = String(me.name ?? "");
  const role = String(me.role ?? "");
  await saveDevice({ relay: target.relay, fingerprint: target.fingerprint, key, member, role, at: Date.now() });
  const signOut = async () => {
    await link.request("signout").catch(() => undefined);
    link.close();
    await forgetDevice(target.relay);
    show("Signed out", "This device is no longer signed in. Open an invite link or sign in again to come back.");
  };

  show(`Welcome, ${member}`, "Loading the mod portal…");
  const ui = await link.request("ui");
  if (ui.ok && typeof ui.js === "string" && typeof ui.css === "string") {
    try {
      await mountUi(ui.js, ui.css, { me, request: (op, body) => link.request(op, body), signOut });
      return;
    } catch (e) {
      console.error(e);
    }
  }
  // An older Cuebot without the portal UI: say who is signed in.
  show(`Welcome, ${member}`,
    el("p", role ? `You're signed in. Your role: ${role}.` : "You're signed in."),
    "The streamer's Cuebot needs an update for the full mod portal. This device stays signed in until then.",
    button("Sign out on this device", () => void signOut(), "secondary"));
}

/**
 * Runs the portal UI the streamer's Cuebot sent. It is code from the Cuebot this device pinned at sign-in (the
 * handshake proves it), loaded from blob: URLs, which the page's CSP allows for script and style and nothing else.
 */
async function mountUi(js: string, css: string, host: PortalHost): Promise<void> {
  const style = document.createElement("link");
  style.rel = "stylesheet";
  style.href = URL.createObjectURL(new Blob([css], { type: "text/css" }));
  document.head.append(style);
  const url = URL.createObjectURL(new Blob([js], { type: "text/javascript" }));
  try {
    const module = (await import(/* @vite-ignore */ url)) as PortalUiModule;
    main.replaceChildren();
    document.body.classList.add("portal");
    module.mount(main, host);
  } finally {
    URL.revokeObjectURL(url);
  }
}

async function start(): Promise<void> {
  if (!window.isSecureContext || !crypto.subtle || !window.indexedDB) {
    return problem("This browser cannot open the mod portal. Use a current version of Chrome, Edge, Firefox or Safari.");
  }
  if (location.pathname === "/auth/return") return finishSignIn();

  const invite = parseInvite(location.hash);
  if (invite) {
    // The invite secret should not linger in the address bar; the tab keeps it until it is used.
    sessionStorage.setItem(INVITE, JSON.stringify(invite));
    history.replaceState(null, "", "/");
    return chooseProvider(invite);
  }
  const kept = sessionStorage.getItem(INVITE);
  if (kept) return chooseProvider(JSON.parse(kept) as Target);

  const devices = (await savedDevices()).sort((a, b) => b.at - a.at);
  if (devices.length === 1) return resume(devices[0]!);
  if (devices.length > 1) {
    const list = el("div", "", "providers");
    for (const d of devices) list.append(button(`${d.member} (${d.role || "mod"})`, () => void resume(d)));
    return show("Mod portal", "You're a mod for more than one streamer on this device. Pick one:", list);
  }
  show("Cuebot mod portal", "When a streamer sends you an invite link, it opens here.");
}

// An invite link opened in a tab that is already on the portal changes only the fragment: start over with it.
window.addEventListener("hashchange", () => location.reload());
start().catch((e) => problem(e instanceof Error ? e.message : String(e)));
