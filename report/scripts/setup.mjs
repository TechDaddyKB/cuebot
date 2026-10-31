#!/usr/bin/env node
// One-time setup of the report intake (TechDaddyKB only):
//   1. Creates the GitHub App from a manifest: this script serves a page on 127.0.0.1, you click "Create GitHub App"
//      on GitHub, and GitHub hands a one-time code back to the script, which exchanges it for the App's id and key.
//   2. Writes the App id into wrangler.jsonc, deploys the Worker and stores the private key as the
//      GITHUB_PRIVATE_KEY secret (converted to PKCS#8 and piped to wrangler; never printed or written to disk).
//   3. Prints the link to install the App on the repository (Issues: write, that repository only).
//
// Usage: node scripts/setup.mjs [--repo TechDaddyKB/cuebot] [--name "Cuebot Reports"] [--port 8917]
// Needs: `npx wrangler login` done, a browser signed in to the GitHub account that will own the App.

import { createServer } from "node:http";
import { createPrivateKey } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { spawn } from "node:child_process";

const args = process.argv.slice(2);
const option = (name, fallback) => {
	const i = args.indexOf(`--${name}`);
	return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const repo = option("repo", "TechDaddyKB/cuebot");
const name = option("name", "Cuebot Reports");
const port = Number(option("port", "8917"));
const owner = repo.split("/")[0];
const state = crypto.randomUUID();

const manifest = {
	name,
	url: `https://github.com/${repo}`,
	description: "Files problem and crash reports sent from the Cuebot app as issues.",
	hook_attributes: { url: "https://example.invalid/unused", active: false },
	redirect_url: `http://127.0.0.1:${port}/callback`,
	public: false,
	default_permissions: { issues: "write", metadata: "read" },
	default_events: [],
};

const escape = (s) => s.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");
const page = `<!doctype html><meta charset="utf-8"><title>Create the Cuebot Reports GitHub App</title>
<body style="font-family:system-ui;max-width:40em;margin:3em auto">
<h1>Create the GitHub App</h1>
<p>This creates <b>${escape(name)}</b> on <b>${escape(owner)}</b> with one permission: Issues (read and write).
It has no webhooks. After you click the button below, GitHub shows the App; click <b>Create GitHub App</b> there.</p>
<form action="https://github.com/settings/apps/new?state=${state}" method="post">
<input type="hidden" name="manifest" value="${escape(JSON.stringify(manifest))}">
<button type="submit" style="font-size:1.2em;padding:.5em 1em">Continue to GitHub</button></form>`;

function run(command, commandArgs, input) {
	return new Promise((resolve, reject) => {
		const child = spawn(command, commandArgs, { stdio: [input === undefined ? "inherit" : "pipe", "inherit", "inherit"], shell: process.platform === "win32" });
		if (input !== undefined) {
			child.stdin.write(input);
			child.stdin.end();
		}
		child.on("exit", (code) => (code === 0 ? resolve() : reject(new Error(`${command} ${commandArgs.join(" ")} exited with ${code}`))));
	});
}

async function finish(code) {
	const response = await fetch(`https://api.github.com/app-manifests/${encodeURIComponent(code)}/conversions`, {
		method: "POST",
		headers: { Accept: "application/vnd.github+json", "User-Agent": "cuebot-report-setup" },
	});
	if (!response.ok) throw new Error(`GitHub answered ${response.status} to the manifest conversion`);
	const app = await response.json();
	const pkcs8 = createPrivateKey(app.pem).export({ type: "pkcs8", format: "pem" });
	app.pem = undefined;

	const config = readFileSync("wrangler.jsonc", "utf8").replace(/"GITHUB_APP_ID": "[^"]*"/, `"GITHUB_APP_ID": "${app.id}"`).replace(/"GITHUB_REPO": "[^"]*"/, `"GITHUB_REPO": "${repo}"`);
	writeFileSync("wrangler.jsonc", config);
	console.log(`\nCreated the GitHub App "${app.name}" (id ${app.id}, ${app.html_url}).`);

	console.log("\nDeploying the Worker...");
	await run("npx", ["wrangler", "deploy"]);
	console.log("\nStoring the App's private key as the GITHUB_PRIVATE_KEY secret...");
	await run("npx", ["wrangler", "secret", "put", "GITHUB_PRIVATE_KEY"], pkcs8);

	console.log(`\nLast step: install the App on ${repo} only:\n  https://github.com/apps/${app.slug}/installations/new\n`);
	console.log("Then put the Worker's address (printed by wrangler deploy) + /v1/reports into Cuebot's SupportLinks.ReportEndpoint.");
}

const server = createServer(async (req, res) => {
	const url = new URL(req.url, `http://127.0.0.1:${port}`);
	if (url.pathname === "/") {
		res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }).end(page);
		return;
	}
	if (url.pathname === "/callback") {
		const code = url.searchParams.get("code");
		if (!code || url.searchParams.get("state") !== state) {
			res.writeHead(400).end("Unexpected callback.");
			return;
		}
		res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }).end("<p style='font-family:system-ui'>Got it. Back to the terminal; you can close this tab.</p>");
		server.close();
		try {
			await finish(code);
			process.exit(0);
		} catch (e) {
			console.error(`\nSetup failed: ${e.message}`);
			process.exit(1);
		}
		return;
	}
	res.writeHead(404).end();
});

server.listen(port, "127.0.0.1", () => {
	console.log(`Open http://127.0.0.1:${port}/ in a browser signed in to GitHub as ${owner}, and follow the button.`);
});
