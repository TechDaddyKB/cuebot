// Deploys the Cuebot relay to the signed-in Cloudflare account. Run through setup.ps1 / setup.sh.
//
//   node scripts/setup.mjs            first run: generate secrets, deploy, print what to paste into Cuebot
//   node scripts/setup.mjs --rotate   new route secret and connect token (every webhook URL changes!)
//
// The generated values are kept in .relay.local.json (git-ignored) so running the script again redeploys
// without changing the URLs already pasted into Ko-fi, Patreon, Kick and the others.

import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const localFile = join(root, ".relay.local.json");
const rotate = process.argv.includes("--rotate");
const isWindows = process.platform === "win32";

function wrangler(args, options = {}) {
	return spawnSync("npx", ["wrangler", ...args], { cwd: root, shell: isWindows, encoding: "utf8", ...options });
}

function fail(message) {
	console.error(`\nSetup failed: ${message}`);
	process.exit(1);
}

// 1. Sign in (opens the browser once; wrangler remembers the login).
const who = wrangler(["whoami"], { stdio: ["ignore", "pipe", "pipe"] });
if (who.status !== 0 || /not authenticated/i.test(`${who.stdout}${who.stderr}`)) {
	console.log("Signing in to Cloudflare: approve the request in the browser window that opens...");
	if (wrangler(["login"], { stdio: "inherit" }).status !== 0) fail("wrangler login did not complete.");
}

// 2. Secrets: reuse the saved ones unless --rotate.
let saved = {};
if (existsSync(localFile)) {
	try {
		saved = JSON.parse(readFileSync(localFile, "utf8"));
	} catch {
		saved = {};
	}
}
const routeSecret = !rotate && saved.routeSecret ? saved.routeSecret : randomBytes(24).toString("hex");
const connectToken = !rotate && saved.connectToken ? saved.connectToken : randomBytes(32).toString("base64url");

// 3. Deploy (the first deploy on a new account asks for a workers.dev subdomain).
console.log("\nDeploying the relay Worker...");
const outDir = mkdtempSync(join(tmpdir(), "cuebot-relay-"));
const outFile = join(outDir, "wrangler-output.ndjson");
const deploy = wrangler(["deploy"], { stdio: "inherit", env: { ...process.env, WRANGLER_OUTPUT_FILE_PATH: outFile } });
if (deploy.status !== 0) fail("wrangler deploy failed (see the messages above).");
let url = saved.url ?? "";
if (existsSync(outFile)) {
	for (const line of readFileSync(outFile, "utf8").split(/\r?\n/)) {
		try {
			const entry = JSON.parse(line);
			const target = entry.type === "deploy" ? (entry.targets ?? []).find((t) => /^https:\/\//.test(t)) : undefined;
			if (target) url = target.replace(/\/+$/, "");
		} catch {
			// Not JSON.
		}
	}
}
rmSync(outDir, { recursive: true, force: true });
if (!url) fail("could not find the Worker URL in the deploy output. Look it up in the Cloudflare dashboard (Workers & Pages).");

// 4. Store the secrets in Cloudflare (each put publishes a new Worker version).
for (const [name, value] of [["ROUTE_SECRET", routeSecret], ["CONNECT_TOKEN", connectToken]]) {
	console.log(`Setting ${name}...`);
	const put = wrangler(["secret", "put", name], { input: value, stdio: ["pipe", "inherit", "inherit"] });
	if (put.status !== 0) fail(`wrangler secret put ${name} failed.`);
}
writeFileSync(localFile, JSON.stringify({ url, connectToken, routeSecret }, null, 2));

// 5. Check it answers (a brand-new workers.dev subdomain can take a minute to resolve).
let reachable = false;
for (let i = 0; i < 12 && !reachable; i++) {
	try {
		const res = await fetch(url);
		reachable = res.ok && (await res.text()).includes("Cuebot relay");
	} catch {
		// Not resolvable yet.
	}
	if (!reachable) await new Promise((r) => setTimeout(r, 5000));
}

console.log(`
==================================================================
 Cuebot relay is deployed${reachable ? "" : " (not reachable yet: give it a minute)"}.
 In Cuebot open Webhooks and paste:

   Relay URL     : ${url}
   Connect token : ${connectToken}
   Route secret  : ${routeSecret}

 Keep these private. They are also saved in relay/.relay.local.json
 (git-ignored); run the setup again with --rotate to replace them.
==================================================================`);
