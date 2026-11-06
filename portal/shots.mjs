// Docs screenshots of the mod portal: `node shots.mjs <invite link> <output folder>`, with the demo Cuebot from
// tests/Cuebot.Portal.Tests/PortalUiLiveTests running (its stand-in sign-in accepts the code "test"; uploads need an
// ffmpeg, e.g. CUEBOT_FFMPEG_DIR). Drives headless
// Chromium over the DevTools protocol and saves PNGs in dark mode.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [invite, out] = process.argv.slice(2);
if (!invite || !out) throw new Error("usage: node shots.mjs <invite link> <output folder>");
const browserPath = process.env.CHROMIUM ?? "chromium";
const profile = mkdtempSync(join(tmpdir(), "cuebot-shots-"));
const port = 9333;
const browser = spawn(browserPath, [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--headless=new", "--no-first-run",
  "--hide-scrollbars", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

try {
  let target;
  for (let i = 0; i < 50 && !target; i++) {
    await sleep(200);
    target = await fetch(`http://127.0.0.1:${port}/json/list`).then((r) => r.json()).then((l) => l.find((t) => t.type === "page")).catch(() => undefined);
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));
  let id = 0;
  const waiting = new Map();
  ws.onmessage = (e) => {
    const m = JSON.parse(e.data);
    if (m.id && waiting.has(m.id)) {
      waiting.get(m.id)(m);
      waiting.delete(m.id);
    }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const n = ++id;
    waiting.set(n, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
    ws.send(JSON.stringify({ id: n, method, params }));
  });
  const run = async (expression) => (await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result.value;
  const until = async (expression, ms = 15000) => {
    for (let t = 0; t < ms; t += 250) {
      if (await run(expression)) return;
      await sleep(250);
    }
    throw new Error(`Timed out waiting for ${expression}`);
  };
  const size = (width, height, mobile = false) =>
    send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: mobile ? 2 : 1, mobile });
  const shot = async (name) => {
    await sleep(600);
    const { data } = await send("Page.captureScreenshot", { format: "png" });
    writeFileSync(join(out, name), Buffer.from(data, "base64"));
    console.log(name);
  };
  const open = (title) => run(`[...document.querySelectorAll(".nav button")].find(b => b.textContent === ${JSON.stringify(title)}).click(), true`);

  await send("Page.enable");
  await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "dark" }] });
  await size(1280, 800);
  await send("Page.navigate", { url: invite });
  await until(`!!sessionStorage.getItem("cuebot-invite") && document.querySelector("button.provider") !== null`);
  await run(`(() => { const i = JSON.parse(sessionStorage.getItem("cuebot-invite"));
    sessionStorage.setItem("cuebot-signin", JSON.stringify({ ...i, provider: "google", state: "shots", verifier: "v".repeat(64) }));
    location.assign("/auth/return?code=test&state=shots"); return true; })()`);
  await until(`document.querySelector(".shell .feed") !== null`);

  await shot("portal-home.png");
  await open("Actions");
  await until(`document.querySelectorAll(".rows li").length > 0`);
  await shot("portal-actions.png");
  await run(`[...document.querySelectorAll(".rows li")].find(li => li.textContent.includes("Hype train")).querySelector("button:last-child").click(), true`);
  await until(`document.querySelector(".steps .branch") !== null`);
  await size(1280, 1500);
  await shot("portal-action-editor.png");
  await size(1280, 800);
  await run(`[...document.querySelectorAll("button.link")].find(b => b.textContent === "Back").click(), true`);
  // Uploads: a generated one-second tone, put into the file input as if picked, then uploaded and cleaned.
  await open("Uploads");
  await until(`document.getElementById("upload-file") !== null`);
  await run(`(() => {
    const rate = 22050, n = rate, data = new Uint8Array(44 + n * 2), v = new DataView(data.buffer);
    const w = (o, s) => [...s].forEach((c, i) => (data[o + i] = c.charCodeAt(0)));
    w(0, "RIFF"); v.setUint32(4, 36 + n * 2, true); w(8, "WAVEfmt "); v.setUint32(16, 16, true); v.setUint16(20, 1, true);
    v.setUint16(22, 1, true); v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true);
    v.setUint16(34, 16, true); w(36, "data"); v.setUint32(40, n * 2, true);
    for (let i = 0; i < n; i++) v.setInt16(44 + i * 2, Math.round(8000 * Math.sin((i * 2 * Math.PI * 440) / rate)), true);
    const input = document.getElementById("upload-file"), dt = new DataTransfer();
    dt.items.add(new File([data], "Air horn.wav", { type: "audio/wav" }));
    input.files = dt.files;
    input.dispatchEvent(new Event("change", { bubbles: true }));
    return true; })()`);
  await sleep(300);
  await run(`document.querySelector("form.card button.primary").click(), true`);
  await until(`document.querySelectorAll(".rows li").length > 0`, 30000);
  await shot("portal-uploads.png");
  await open("Commands");
  await until(`document.querySelectorAll(".rows li").length > 0`);
  await size(390, 760, true);
  await open("Home");
  await until(`document.querySelector(".feed") !== null`);
  await shot("portal-phone.png");
  ws.close();
} finally {
  browser.kill();
  await sleep(300);
  rmSync(profile, { recursive: true, force: true });
}
