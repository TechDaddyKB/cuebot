// Docs screenshots of the mod portal: `node shots.mjs <invite link> <output folder>`, with the demo Cuebot from
// tests/Cuebot.Portal.Tests/PortalUiLiveTests running (its stand-in sign-in accepts the code "test"; uploads need an
// ffmpeg, e.g. CUEBOT_FFMPEG_DIR). Drives headless
// Chromium over the DevTools protocol and saves PNGs in dark mode.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// With "overlays" as a third argument it drives the overlay editor instead, against tests/Cuebot.UiTests/PortalOverlaysLiveDemo
// (a picture upload, then text, picture, goal and alert edits saved to the overlay that OBS shows).
const [invite, out, mode] = process.argv.slice(2);
if (!invite || !out) throw new Error("usage: node shots.mjs <invite link> <output folder> [overlays]");
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
    throw new Error(`Timed out waiting for ${expression}; the page says: ${(await run("document.body.innerText")).slice(0, 400)}`);
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
  await until(mode === "overlays" ? `document.querySelector(".shell .nav") !== null` : `document.querySelector(".shell .feed") !== null`);
  // Sets a labelled form control as if typed or picked (inside the element matching scope).
  await run(`window.setField = (label, value, scope = "main") => {
    const l = [...document.querySelectorAll(scope + " label")].find(x => x.textContent === label);
    if (!l) throw new Error("No field " + label);
    const el = document.getElementById(l.htmlFor);
    if (el.tagName === "SELECT") {
      const o = [...el.options].find(o => o.value === value || o.textContent.startsWith(value));
      if (!o) throw new Error("No choice " + value);
      el.value = o.value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
    } else {
      el.value = value;
      el.dispatchEvent(new Event("input", { bubbles: true }));
    }
    return true;
  }; true`);
  const button = (text, scope = "main") => run(`[...document.querySelectorAll(${JSON.stringify(scope + " button")})].find(b => b.textContent === ${JSON.stringify(text)}).click(), true`);
  const saved = async () => {
    const state = await run(`(() => { const b = [...document.querySelectorAll(".sticky-save button")].find(b => b.textContent === "Save"); return b ? (b.disabled ? "disabled" : "ready") : "missing"; })()`);
    if (state !== "ready") throw new Error(`The Save button is ${state}`);
    await button("Save", ".sticky-save");
    // Saved when nothing is left unsaved (an error leaves the changes there and shows why).
    await until(`document.querySelector(".notice.error") !== null || document.querySelector(".sticky-save .muted")?.textContent === ""`);
    const notice = await run(`document.querySelector(".notice")?.textContent ?? ""`);
    if (!notice.includes("live")) throw new Error(notice);
    console.log(notice);
  };

  if (mode === "overlays") {
    await open("Uploads");
    await until(`document.getElementById("upload-file") !== null`);
    await run(`setField("What is it?", "Image")`);
    await run(`(async () => {
      const c = new OffscreenCanvas(256, 256), g = c.getContext("2d");
      const grad = g.createLinearGradient(0, 0, 256, 256); grad.addColorStop(0, "#7b6dff"); grad.addColorStop(1, "#22d3ee");
      g.fillStyle = grad; g.beginPath(); g.arc(128, 128, 120, 0, Math.PI * 2); g.fill();
      g.fillStyle = "#fff"; g.font = "bold 120px sans-serif"; g.textAlign = "center"; g.textBaseline = "middle"; g.fillText("C", 128, 136);
      const blob = await c.convertToBlob({ type: "image/png" });
      const input = document.getElementById("upload-file"), dt = new DataTransfer();
      dt.items.add(new File([blob], "Cuebot logo.png", { type: "image/png" }));
      input.files = dt.files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
      return true; })()`);
    await sleep(300);
    await run(`document.querySelector("form.card button.primary").click(), true`);
    await until(`[...document.querySelectorAll(".rows li")].some(li => li.textContent.includes("Cuebot logo"))`, 60000);

    await open("Overlays");
    await until(`document.querySelectorAll(".rows li").length > 0`);
    await button("Edit");
    await until(`document.querySelector(".canvas") !== null`);
    await button("Headline", ".layers");
    await run(`setField("Text", "Back in 5 minutes!")`);
    await run(`setField("Colour", "#ffcc33")`);
    await button("Logo", ".layers");
    await run(`setField("Picture", "Cuebot logo")`);
    await button("Goal", ".layers");
    await run(`setField("Title", "Road to 100 followers")`);
    await run(`setField("Goal", "100")`);
    await saved();
    await until(`document.querySelector(".canvas") !== null`);
    await button("Headline", ".layers");
    await size(1280, 1700);
    await shot("portal-overlays.png");
    await size(1280, 800);
    await button("Alert box", ".layers");
    await until(`document.querySelector("details.item") !== null`);
    await run(`document.querySelector("details.item").open = true, true`);
    await run(`setField("Text", "{name} joined the crew!", "details.item")`);
    await saved();
    await size(390, 760, true);
    await until(`document.querySelector(".canvas") !== null`);
    await shot("portal-overlays-phone.png");
    ws.close();
  } else {
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
  }
} finally {
  browser.kill();
  await sleep(300);
  rmSync(profile, { recursive: true, force: true });
}
