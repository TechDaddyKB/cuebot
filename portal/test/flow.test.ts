import { describe, expect, it } from "vitest";
import { appReturnLink, authorizeUrl, challenge, closeReason, deviceName, isAppReturn, parseInvite, readReturn, relayOrigin, type PendingSignIn, type Provider } from "../web/flow.js";

const RELAY = "a".repeat(32);
const KEY = "K".repeat(43);
const INVITE = "i".repeat(43);

const pending: PendingSignIn = { relay: RELAY, fingerprint: KEY, invite: INVITE, provider: "google", state: "s1", verifier: "v".repeat(64) };

describe("invite links", () => {
  it("reads the fragment Cuebot makes", () => {
    expect(parseInvite(`#i=${INVITE}&r=${RELAY}&k=${KEY}`)).toEqual({ relay: RELAY, fingerprint: KEY, invite: INVITE });
    expect(parseInvite(`#r=${RELAY}&k=${KEY}`)).toEqual({ relay: RELAY, fingerprint: KEY });
  });

  it("refuses anything else", () => {
    expect(parseInvite("")).toBeNull();
    expect(parseInvite(`#i=${INVITE}&r=NOTARELAY&k=${KEY}`)).toBeNull();
    expect(parseInvite(`#i=${INVITE}&r=${RELAY}&k=short`)).toBeNull();
    expect(parseInvite(`#i=bad!&r=${RELAY}&k=${KEY}`)).toBeNull();
  });
});

describe("sign-in addresses", () => {
  const google: Provider = { id: "google", name: "Google", authorize: "https://accounts.google.com/o/oauth2/v2/auth?client_id=x&scope=openid", pkce: true };

  it("uses the RFC 7636 S256 challenge", async () => {
    expect(await challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")).toBe("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
  });

  it("adds state, and the PKCE challenge where the provider takes one", async () => {
    const url = new URL(await authorizeUrl(google, pending));
    expect(url.searchParams.get("client_id")).toBe("x");
    expect(url.searchParams.get("state")).toBe("s1");
    expect(url.searchParams.get("code_challenge")).toBe(await challenge(pending.verifier));
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
    const twitch = new URL(await authorizeUrl({ id: "twitch", name: "Twitch", authorize: "https://id.twitch.tv/oauth2/authorize?client_id=t", pkce: false }, pending));
    expect(twitch.searchParams.get("state")).toBe("s1");
    expect(twitch.searchParams.has("code_challenge")).toBe(false);
  });

  it("only sends the mod to the providers' own pages", async () => {
    await expect(authorizeUrl({ ...google, authorize: "https://evil.example/auth" }, pending)).rejects.toThrow();
    await expect(authorizeUrl({ ...google, authorize: "http://accounts.google.com/auth" }, pending)).rejects.toThrow();
  });
});

describe("the return page", () => {
  it("takes the code only with this tab's state", () => {
    expect(readReturn("?code=abc&state=s1", pending)).toEqual({ code: "abc" });
    expect(readReturn("?code=abc&state=other", pending)).toHaveProperty("error");
    expect(readReturn("?code=abc&state=s1", null)).toHaveProperty("error");
    expect(readReturn("?error=access_denied&state=s1", pending)).toEqual({ error: "The sign-in was cancelled." });
    expect(readReturn("?state=s1", pending)).toHaveProperty("error");
  });
});

describe("the phone app's sign-in", () => {
  it("is told apart by its state and handed to the app only", () => {
    expect(isAppReturn("?code=c&state=app.xyz")).toBe(true);
    expect(isAppReturn("?code=c&state=xyz")).toBe(false);
    expect(isAppReturn("?code=c")).toBe(false);
    const android = "Mozilla/5.0 (Linux; Android 16) Chrome/140.0 Mobile Safari/537.36";
    expect(appReturnLink("?code=4%2F0A&scope=email&state=app.xyz", android))
      .toBe("intent://signin?code=4%2F0A&state=app.xyz#Intent;scheme=cuebot-portal;package=digital.techdaddy.cuebot.companion;end");
    expect(appReturnLink("?error=access_denied&state=app.xyz", "Mozilla/5.0 (iPhone; CPU iPhone OS 26_0)"))
      .toBe("cuebot-portal://signin?state=app.xyz&error=access_denied");
  });
});

describe("helpers", () => {
  it("finds the relay next to the portal", () => {
    expect(relayOrigin({ protocol: "https:", host: "portal.techdaddydigital.com", hostname: "portal.techdaddydigital.com" })).toBe("wss://relay.techdaddydigital.com");
    expect(relayOrigin({ protocol: "https:", host: "x.workers.dev", hostname: "x.workers.dev" })).toBe("wss://x.workers.dev");
    expect(relayOrigin({ protocol: "http:", host: "localhost:8787", hostname: "localhost" })).toBe("ws://localhost:8787");
  });

  it("names devices and close codes in plain words", () => {
    expect(deviceName("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")).toBe("Chrome on Windows");
    expect(deviceName("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile Safari/604.1")).toBe("Safari on iOS");
    expect(closeReason(4404)).toContain("not online");
  });
});
