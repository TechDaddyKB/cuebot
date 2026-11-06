// The loader's sign-in steps that need no page or network: reading the invite link, PKCE, the state carried through
// the provider's redirect, and where the relay is. docs/MOD_PORTAL_PLAN.md, section 4.

import { base64Url, type Bytes } from "../src/crypto.js";

const RELAY_ID = /^[a-z2-7]{32}$/;
const FINGERPRINT = /^[A-Za-z0-9_-]{43}$/;
const INVITE = /^[A-Za-z0-9_-]{43}$/;

/** What an invite link (or a saved device) points at. Everything comes from the URL fragment, which no server sees. */
export interface Target {
  relay: string;
  fingerprint: string;
  invite?: string;
}

/** Reads `#i=<invite>&r=<relay id>&k=<fingerprint>`; null when the fragment is not a valid invite link. */
export function parseInvite(hash: string): Target | null {
  const p = new URLSearchParams(hash.replace(/^#/, ""));
  const relay = p.get("r") ?? "";
  const fingerprint = p.get("k") ?? "";
  const invite = p.get("i") ?? "";
  if (!RELAY_ID.test(relay) || !FINGERPRINT.test(fingerprint)) return null;
  if (invite && !INVITE.test(invite)) return null;
  return invite ? { relay, fingerprint, invite } : { relay, fingerprint };
}

/** A provider as Cuebot describes it (`providers`): the authorize URL lacks only state and the PKCE challenge. */
export interface Provider {
  id: string;
  name: string;
  authorize: string;
  pkce: boolean;
}

/** What survives the trip to the provider and back, in sessionStorage. */
export interface PendingSignIn extends Target {
  provider: string;
  state: string;
  verifier: string;
}

export function random(bytes = 32): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(bytes)) as Bytes);
}

export async function challenge(verifier: string): Promise<string> {
  return base64Url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier))));
}

/** Only the provider pages Cuebot may send a mod to. */
const PROVIDER_HOSTS = new Set(["accounts.google.com", "id.twitch.tv", "discord.com"]);

/** The address to send the mod to; throws when Cuebot named a page that is not a known provider's. */
export async function authorizeUrl(provider: Provider, pending: PendingSignIn): Promise<string> {
  const url = new URL(provider.authorize);
  if (url.protocol !== "https:" || !PROVIDER_HOSTS.has(url.hostname)) throw new Error(`Unexpected sign-in address for ${provider.name}.`);
  url.searchParams.set("state", pending.state);
  if (provider.pkce) {
    url.searchParams.set("code_challenge", await challenge(pending.verifier));
    url.searchParams.set("code_challenge_method", "S256");
  }
  return url.toString();
}

/** The provider's answer on /auth/return, checked against what this tab sent. */
export function readReturn(search: string, pending: PendingSignIn | null): { code: string } | { error: string } {
  const p = new URLSearchParams(search);
  if (!pending) return { error: "This sign-in was started in another tab or has expired. Open the invite link again." };
  if (p.get("state") !== pending.state) return { error: "This sign-in did not come from this page. Start again." };
  const error = p.get("error");
  if (error) return { error: error === "access_denied" ? "The sign-in was cancelled." : `The sign-in failed (${error}).` };
  const code = p.get("code");
  return code ? { code } : { error: "The provider did not send a sign-in code. Start again." };
}

/** relay.<domain> next to portal.<domain>; the same host anywhere else (workers.dev, wrangler dev). */
export function relayOrigin(location: { protocol: string; host: string; hostname: string }): string {
  if (location.hostname.startsWith("portal.")) return `wss://relay.${location.host.slice("portal.".length)}`;
  return `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}`;
}

/** A short name for this device in the streamer's list, from the browser's own description. */
export function deviceName(userAgent: string): string {
  const browser = /Edg\//.test(userAgent) ? "Edge" : /OPR\//.test(userAgent) ? "Opera" : /Firefox\//.test(userAgent) ? "Firefox"
    : /Chrome\//.test(userAgent) ? "Chrome" : /Safari\//.test(userAgent) ? "Safari" : "Browser";
  const os = /iPhone|iPad/.test(userAgent) ? "iOS" : /Android/.test(userAgent) ? "Android" : /Windows/.test(userAgent) ? "Windows"
    : /Mac OS X/.test(userAgent) ? "macOS" : /Linux/.test(userAgent) ? "Linux" : "";
  return os ? `${browser} on ${os}` : browser;
}

/** Words for the relay's close codes (portal/RELAY.md). */
export function closeReason(code: number): string {
  switch (code) {
    case 4404:
    case 4503:
      return "The streamer's Cuebot is not online right now. Try again when it is running.";
    case 4403:
      return "The streamer has closed the mod portal.";
    case 4429:
      return "Too many people are connecting right now. Try again in a minute.";
    default:
      return "The connection to the streamer's Cuebot was lost.";
  }
}
