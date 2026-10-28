// Bundles the plugin (Node) and the property inspector (browser) into the .sdPlugin folder.
// Usage: node scripts/build.mjs [--watch]
import { context } from "esbuild";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const plugin = join(root, "io.github.cuebot.streamdeck.sdPlugin");
const watch = process.argv.includes("--watch");

const builds = [
	{
		entryPoints: [join(root, "src/plugin.ts")],
		outfile: join(plugin, "bin/plugin.js"),
		bundle: true,
		platform: "node",
		format: "esm",
		target: "node20",
		sourcemap: watch ? "inline" : false,
		// Optional native speed-ups of `ws`; it works without them.
		external: ["bufferutil", "utf-8-validate"],
		// `ws` is CommonJS; give the ESM bundle a require() for its Node built-ins.
		banner: { js: "import { createRequire as __cuebotRequire } from 'node:module'; const require = __cuebotRequire(import.meta.url);" },
		logLevel: "info",
	},
	{
		entryPoints: [join(root, "src/pi/pi.ts")],
		outfile: join(plugin, "ui/pi.js"),
		bundle: true,
		platform: "browser",
		format: "iife",
		target: "chrome110",
		logLevel: "info",
	},
];

mkdirSync(join(plugin, "bin"), { recursive: true });
// Stream Deck runs bin/plugin.js with Node; this marks it as an ES module.
writeFileSync(join(plugin, "bin/package.json"), JSON.stringify({ type: "module" }, null, "\t") + "\n");

for (const options of builds) {
	const ctx = await context(options);
	if (watch) {
		await ctx.watch();
	} else {
		await ctx.rebuild();
		await ctx.dispose();
	}
}
