// Builds the browser loader (web/main.ts) into dist/loader.js, which the Worker serves as /loader.js, and the portal
// UI (ui/index.tsx) into src/Cuebot.Portal/Web, which Cuebot embeds and sends to signed-in mods.
import { build } from "esbuild";

const common = { bundle: true, format: "esm", target: "es2022", minify: true, sourcemap: false, legalComments: "none" };

await build({ ...common, entryPoints: ["web/main.ts"], outfile: "dist/loader.js" });
await build({
  ...common,
  entryPoints: ["ui/index.tsx"],
  outfile: "../src/Cuebot.Portal/Web/portal-ui.js",
  jsx: "automatic",
  jsxImportSource: "preact",
  charset: "utf8",
});
