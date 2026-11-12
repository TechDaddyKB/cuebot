// Screenshots of Cuebot's web editors for the documentation site: the overlay designer, every widget's editor and the
// alert designer, plus the live overlay. Run by tests/Cuebot.UiTests/DocsOverlayScreenshots.cs, which starts the
// overlay server with example overlays and writes the addresses to a JSON file:
//   node overlay-shots.mjs <urls.json> <output folder>
// Needs playwright-core (PLAYWRIGHT_CORE = its folder, or installed next to this file) and a Chromium
// (CHROMIUM = its path, default /usr/bin/chromium).
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { join } from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_CORE || "playwright-core");
const [urlsFile, out] = process.argv.slice(2);
const urls = JSON.parse(readFileSync(urlsFile, "utf8"));
const shot = (target, name, options = {}) => target.screenshot({ path: join(out, name + ".png"), ...options });
const wait = (page, ms) => page.waitForTimeout(ms);

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || "/usr/bin/chromium", args: ["--autoplay-policy=no-user-gesture-required", "--mute-audio"] });
const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: 1, colorScheme: "dark" });
const page = await context.newPage();
page.on("pageerror", e => console.error("page error:", e.message));

// ---- The overlay designer ----------------------------------------------------------------------------------------
await page.goto(urls.designer);
await wait(page, 3500);
await shot(page, "designer-overview");
await shot(page.locator("aside.layers"), "designer-layers");
await shot(page.locator("header.bar"), "designer-toolbar");

await page.click("#add-button");
await wait(page, 400);
await shot(page, "designer-add-menu");
await page.keyboard.press("Escape");
await page.click("#add-button").catch(() => {});   // closes the menu when Escape did not
await wait(page, 300);
if (await page.locator("#add-menu").isVisible()) await page.mouse.click(800, 880);

// Select the chat box: handles on the canvas, its settings on the right.
const boxes = page.locator("#boxes .box");
const names = await page.locator("#boxes .box .label").allTextContents();
const pick = name => names.findIndex(n => n.toLowerCase().includes(name));
const chat = pick("chat");
await boxes.nth(chat >= 0 ? chat : 0).click({ position: { x: 20, y: 20 } });
await wait(page, 800);
await shot(page, "designer-selected");
await shot(page.locator("aside.props"), "designer-properties");

// A text layer: what Text's properties look like.
await page.click("#add-button");
await page.click('#add-menu [data-add="text"]');
await wait(page, 1500);
await shot(page, "designer-text-layer");
await shot(page.locator("aside.props"), "designer-text-properties");
await page.click("#undo");
await wait(page, 800);

// The library dialog.
await page.click("#add-button");
await page.click('#add-menu [data-add="library"]');
await wait(page, 800);
await shot(page, "designer-library");
await page.keyboard.press("Escape");
await wait(page, 300);

// ---- The "Starting soon" worked example: text, an image, a slideshow, grouping, hiding and locking ----------------
// A row of the Properties panel by its label ("Size", "Text"...): its first input, select or text box.
// Two rows are called "Size" (the layer's box, then the text's font size): row picks which.
const prop = (label, n = 0, row = 0) => page.locator("#props .row").filter({ has: page.locator("label", { hasText: new RegExp("^" + label + "$") }) })
  .nth(row).locator("input, textarea, select").nth(n);
const setProp = async (label, value, n = 0, row = 0) => {
  const control = prop(label, n, row);
  const tag = await control.evaluate(e => e.tagName + ":" + (e.type || ""));
  if (tag.startsWith("SELECT")) await control.selectOption(value);
  else if (tag.endsWith("checkbox")) { if (value) await control.check(); else await control.uncheck(); }
  else { await control.fill(String(value)); await control.dispatchEvent("change"); }
  await wait(page, 250);
};
const addLayer = async kind => { await page.click("#add-button"); await page.click(`#add-menu [data-add="${kind}"]`); await wait(page, 1200); };
const layerItem = name => page.locator("#layer-list li").filter({ has: page.locator(".lname", { hasText: name }) }).first();

if (urls.startingDesigner) {
  await page.goto(urls.startingDesigner);
  await wait(page, 2500);
  await addLayer("text");
  await setProp("Name", "Title");
  await setProp("Text", "Starting soon");
  await setProp("Size", 110, 0, 1);
  await setProp("Weight", "900");
  await setProp("Align", "center");
  await setProp("Shadow", true);
  await setProp("Size", 1200, 0); await setProp("Size", 160, 1);
  await page.locator("#props button", { hasText: "Centre on the canvas" }).click();
  await wait(page, 300);
  await setProp("Position", 340, 1);
  await wait(page, 800);
  await shot(page.locator("aside.props"), "designer-example-text");

  await addLayer("image");
  await setProp("Name", "Logo");
  const chooser = page.waitForEvent("filechooser");
  await page.locator("#props button", { hasText: "Upload…" }).first().click();
  await (await chooser).setFiles(urls.logo);
  await wait(page, 1500);
  await setProp("Fit", "contain");
  await setProp("Size", 200, 0); await setProp("Size", 200, 1);
  await setProp("Position", 860, 0); await setProp("Position", 90, 1);
  await wait(page, 800);
  await shot(page.locator("aside.props"), "designer-image-properties");
  const logoUrl = await prop("Image").inputValue();

  await addLayer("slideshow");
  await setProp("Slides", [logoUrl, logoUrl].join("\n"));
  await setProp("Seconds each", 8);
  await shot(page.locator("aside.props"), "designer-slideshow-properties");
  await page.click("#delete");
  await wait(page, 800);

  // Click empty canvas space so nothing is selected, then the finished scene.
  await page.mouse.click(1250, 860);
  await wait(page, 1500);
  await shot(page, "designer-starting-soon");

  // Snap guide: drag the title a little, holding the mouse while the picture is taken.
  const title = page.locator("#boxes .box").filter({ has: page.locator(".label", { hasText: "Title" }) }).first();
  const box = await title.boundingBox();
  if (box) {
    await page.mouse.move(box.x + 30, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + 60, box.y + box.height / 2 + 25, { steps: 8 });
    await page.mouse.move(box.x + 32, box.y + box.height / 2 + 2, { steps: 8 });
    await wait(page, 300);
    await shot(page, "designer-snapping");
    await page.mouse.up();
    await page.click("#undo").catch(() => {});
    await wait(page, 600);
  }

  // Group the title and the logo (Shift-click in the Layers list).
  await layerItem("Title").click();
  await layerItem("Logo").click({ modifiers: ["Shift"] });
  await wait(page, 400);
  await shot(page, "designer-two-selected");
  await page.click("#group");
  await wait(page, 800);
  await shot(page.locator("aside.layers"), "designer-layers-grouped");
  await page.click("#ungroup");
  await wait(page, 600);

  // One layer hidden, one locked.
  await layerItem("Logo").locator("button").first().click();
  await layerItem("Countdown").locator("button").last().click();
  await wait(page, 800);
  await shot(page, "designer-hidden-locked");
  await shot(page.locator("aside.layers"), "designer-layers-hidden-locked");
  await layerItem("Logo").locator("button").first().click();
  await layerItem("Countdown").locator("button").last().click();
  await wait(page, 600);

  // Save the title as a template; the library then lists it next to the widgets.
  await layerItem("Title").click();
  await wait(page, 300);
  await page.locator("#props button", { hasText: "Save to library" }).click();
  await wait(page, 800);
  await page.click("#add-button");
  await page.click('#add-menu [data-add="library"]');
  await wait(page, 800);
  await shot(page.locator("#library"), "designer-library-templates");
  await page.keyboard.press("Escape");
  await wait(page, 1500);

  const starting = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  await starting.goto(urls.startingOverlay);
  await starting.addStyleTag({ content: "html { background: linear-gradient(135deg, #1b2735, #3a2a4a) !important; }" });
  await wait(starting, 4000);
  await shot(starting, "overlay-starting-soon");
  await starting.close();
}

// ---- Each widget's editor ----------------------------------------------------------------------------------------
for (const w of urls.widgets) {
  await page.goto(w.url);
  await wait(page, 3000);
  await shot(page, "widget-" + w.kind);
  // Every settings group open, in a window tall enough to show them all: widget-<kind>-settings.png.
  await page.evaluate(() => document.querySelectorAll("#form details").forEach(d => { d.open = true; }));
  const height = await page.evaluate(() => document.querySelector("#form").scrollHeight + 200);
  await page.setViewportSize({ width: 1600, height: Math.min(Math.max(900, height), 6000) });
  await wait(page, 400);
  await shot(page.locator("#panel-settings"), "widget-" + w.kind + "-settings");
  await page.setViewportSize({ width: 1600, height: 900 });
}

// The editor's tabs, on the blank custom widget (its code is the example that comes with it).
if (urls.blank) {
  await page.goto(urls.blank);
  await wait(page, 2500);
  await shot(page, "widget-editor-settings");
  for (const tab of ["html", "css", "js", "fields", "data", "cssOverride"]) {
    await page.click(`nav.tabs [data-tab="${tab}"]`);
    await wait(page, 500);
    await shot(page, "widget-editor-" + tab.toLowerCase());
  }
}

// Emulate: a Twitch follow and chat into the chat box and event list previews.
for (const w of urls.widgets.filter(x => x.kind === "chatbox" || x.kind === "eventlist")) {
  await page.goto(w.url);
  await wait(page, 2500);
  const buttons = page.locator("#emulate-buttons button");
  const count = await buttons.count();
  for (let i = 0; i < Math.min(count, 4); i++) { await buttons.nth(i).click(); await wait(page, 500); }
  await wait(page, 1200);
  await shot(page, "widget-" + w.kind + "-emulated");
}

// ---- The alert designer ------------------------------------------------------------------------------------------
await page.goto(urls.alertbox);
await wait(page, 3000);
await shot(page, "alert-designer");
await shot(page.locator("section.side"), "alert-designer-side");
const types = page.locator("#types li");
const typeNames = await types.allTextContents();
const sub = typeNames.findIndex(t => /subscriber/i.test(t));
if (sub >= 0) { await types.nth(sub).click(); await wait(page, 800); await shot(page, "alert-designer-subscriber"); }
await page.click("#mute");
await page.click("#play");
await wait(page, 1500);
await shot(page, "alert-designer-test");
// A new variation, showing its "When" rules.
const addVariation = page.locator("button", { hasText: "+ Variation" });
if (await addVariation.count()) {
  await addVariation.first().click();
  await wait(page, 800);
  await shot(page, "alert-designer-variation");
}
// The whole design form, in a window tall enough to show it all.
await page.setViewportSize({ width: 1600, height: 3200 });
await wait(page, 500);
await shot(page.locator("section.side"), "alert-designer-form-full");
await page.setViewportSize({ width: 1600, height: 900 });
await wait(page, 300);
// Emulate a Ko-fi membership when the list has Ko-fi.
const platforms = await page.locator("#emulate-platform option").evaluateAll(os => os.map(o => o.value));
if (platforms.includes("kofi")) {
  await page.selectOption("#emulate-platform", "kofi");
  await wait(page, 300);
  await shot(page.locator(".emulate"), "alert-designer-emulate");
  await page.locator("#emulate-buttons button").nth(1).click();
  await wait(page, 2000);
  await shot(page, "alert-designer-emulated");
}

// ---- The live overlay, as OBS shows it (on a dark background instead of the stream) ------------------------------
const live = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
await live.goto(urls.overlay);
await live.addStyleTag({ content: "html { background: linear-gradient(135deg, #1b2735, #3a2a4a) !important; }" });
await wait(live, 6000);
await shot(live, "overlay-live");

await browser.close();
console.log("done");
