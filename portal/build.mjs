// Builds the browser loader (web/main.ts) into dist/loader.js, which the Worker serves as /loader.js.
import { build } from "esbuild";

await build({
  entryPoints: ["web/main.ts"],
  outfile: "dist/loader.js",
  bundle: true,
  format: "esm",
  target: "es2022",
  minify: true,
  sourcemap: false,
  legalComments: "none",
});
