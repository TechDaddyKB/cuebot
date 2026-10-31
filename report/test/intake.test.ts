import { env } from "cloudflare:test";
import { beforeAll, beforeEach, describe, expect, it } from "vitest";
import { appJwt, resetTokenCache } from "../src/github";
import { handler, issueBody, issueTitle, parseReport, type Env } from "../src/index";
import { scrub } from "../src/scrub";

const BASE = "https://intake.example";
const INSTALL = "0123456789abcdef0123456789abcdef";

/** A fake GitHub: answers the App's calls and records them. */
class FakeGitHub {
	calls: { method: string; path: string; body: any; auth: string }[] = [];
	issues = new Map<number, { title: string; body: string; labels: string[]; state: string; comments: string[] }>();
	next = 100;
	failCreate = false;

	fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
		const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url);
		const method = init?.method ?? "GET";
		const body = init?.body ? JSON.parse(init.body as string) : null;
		const auth = new Headers(init?.headers).get("Authorization") ?? "";
		this.calls.push({ method, path: url.pathname, body, auth });
		const reply = (status: number, value: unknown) => new Response(JSON.stringify(value), { status });
		if (method === "GET" && url.pathname === "/repos/TechDaddyKB/cuebot/installation") return reply(200, { id: 777 });
		if (method === "POST" && url.pathname === "/app/installations/777/access_tokens")
			return reply(201, { token: "ghs_installationtoken", expires_at: new Date(Date.now() + 3_600_000).toISOString() });
		if (method === "POST" && url.pathname === "/repos/TechDaddyKB/cuebot/issues") {
			if (this.failCreate) return reply(500, { message: "boom" });
			const number = this.next++;
			this.issues.set(number, { ...body, state: "open", comments: [] });
			return reply(201, { number, html_url: `https://github.com/TechDaddyKB/cuebot/issues/${number}` });
		}
		const issue = url.pathname.match(/^\/repos\/TechDaddyKB\/cuebot\/issues\/(\d+)(\/comments)?$/);
		if (issue) {
			const found = this.issues.get(Number(issue[1]));
			if (!found) return reply(404, { message: "Not Found" });
			if (issue[2] && method === "POST") {
				found.comments.push(body.body);
				return reply(201, { html_url: `https://github.com/TechDaddyKB/cuebot/issues/${issue[1]}#comment` });
			}
			return reply(200, { state: found.state, html_url: `https://github.com/TechDaddyKB/cuebot/issues/${issue[1]}` });
		}
		return reply(404, { message: "Not Found" });
	};
}

let privateKey = "";
let publicKey: CryptoKey;

beforeAll(async () => {
	// A throwaway App key for the tests.
	const pair = (await crypto.subtle.generateKey(
		{ name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
		true,
		["sign", "verify"],
	)) as CryptoKeyPair;
	const pkcs8 = new Uint8Array((await crypto.subtle.exportKey("pkcs8", pair.privateKey)) as ArrayBuffer);
	let binary = "";
	for (const b of pkcs8) binary += String.fromCharCode(b);
	privateKey = `-----BEGIN PRIVATE KEY-----\n${btoa(binary).match(/.{1,64}/g)!.join("\n")}\n-----END PRIVATE KEY-----\n`;
	publicKey = pair.publicKey;
});

let github: FakeGitHub;
let worker: ReturnType<typeof handler>;
let testEnv: Env;
let ipCounter = 0;

beforeEach(() => {
	github = new FakeGitHub();
	worker = handler(github.fetch);
	testEnv = { ...env, GITHUB_PRIVATE_KEY: privateKey };
	resetTokenCache();
});

function report(overrides: Record<string, unknown> = {}) {
	return {
		kind: "problem",
		title: "The chat box stops scrolling",
		body: "It stops after a raid.",
		version: "0.1.0-rc.3",
		os: "Linux (Arch)",
		installId: INSTALL,
		...overrides,
	};
}

function send(value: unknown, ip = `10.0.0.${++ipCounter}`) {
	return worker.fetch(
		new Request(`${BASE}/v1/reports`, { method: "POST", body: JSON.stringify(value), headers: { "CF-Connecting-IP": ip } }),
		testEnv,
	);
}

describe("parsing and formatting", () => {
	it("accepts good reports and names what is wrong with bad ones", () => {
		expect(parseReport(report())).toMatchObject({ kind: "problem", title: "The chat box stops scrolling" });
		expect(parseReport(report({ kind: "spam" }))).toBe("kind must be problem or crash");
		expect(parseReport(report({ title: "x" }))).toMatch(/title/);
		expect(parseReport(report({ installId: "not-hex" }))).toMatch(/installId/);
		expect(parseReport(report({ version: "1.0; drop table" }))).toMatch(/version/);
		expect(parseReport(report({ kind: "crash" }))).toMatch(/signature/);
		expect(parseReport(report({ kind: "crash", signature: "abcdef0123456789" }))).toMatchObject({ signature: "abcdef0123456789" });
	});

	it("scrubs secrets, e-mail addresses and mentions", () => {
		const text = scrub(
			"token ghp_abcdefghijklmnopqrstuvwxyz123456 key sk-ant-api03-abcdefghijklmnopqrstuv mail me@example.com " +
				"Authorization: Bearer abcdefghijklmnopqrstuvwxyz access_token=supersecretvalue oauth:abcdefghijklmnopqrstuvwxyz " +
				"rtmp://live.twitch.tv/app/live_123456789_abcdefghijkl cc @octocat and `@code`",
		);
		expect(text).not.toMatch(/ghp_|sk-ant-|me@example|abcdefghijklmnopqrstuvwxyz|supersecretvalue|live_123456789/);
		expect(text).toContain("@​octocat");
		expect(text).toContain("`@code`");
	});

	it("builds the issue with where it came from and a hidden marker, but not the install id", async () => {
		const body = await issueBody(parseReport(report({ kind: "crash", signature: "abcdef0123456789" })) as any);
		expect(body).toContain("It stops after a raid.");
		expect(body).toContain("Sent from Cuebot 0.1.0-rc.3 on Linux (Arch)");
		expect(body).toContain("<!-- cuebot-report kind=crash signature=abcdef0123456789");
		expect(body).not.toContain(INSTALL);
		expect(issueTitle(parseReport(report({ kind: "crash", signature: "abcdef0123456789", title: "NullReferenceException in X" })) as any)).toBe(
			"Crash: NullReferenceException in X",
		);
	});

	it("signs the App's JWT with its key", async () => {
		const jwt = await appJwt("12345", privateKey, 1_700_000_000_000);
		const [header, payload, signature] = jwt.split(".");
		const decode = (s: string) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
		expect(JSON.parse(new TextDecoder().decode(decode(payload)))).toEqual({ iat: 1_699_999_940, exp: 1_700_000_540, iss: "12345" });
		expect(await crypto.subtle.verify("RSASSA-PKCS1-v1_5", publicKey, decode(signature), new TextEncoder().encode(`${header}.${payload}`))).toBe(true);
		await expect(appJwt("1", "-----BEGIN RSA PRIVATE KEY-----\nAA\n-----END RSA PRIVATE KEY-----")).rejects.toThrow(/PKCS#8/);
	});
});

describe("the intake", () => {
	it("files a problem as an issue with the installation token", async () => {
		const res = await send(report());
		expect(res.status).toBe(201);
		const result = (await res.json()) as any;
		expect(result).toMatchObject({ number: 100, url: "https://github.com/TechDaddyKB/cuebot/issues/100", duplicate: false });
		const issue = github.issues.get(100)!;
		expect(issue.title).toBe("The chat box stops scrolling");
		expect(issue.labels).toEqual(["from-app"]);
		const create = github.calls.find((c) => c.path.endsWith("/issues"))!;
		expect(create.auth).toBe("Bearer ghs_installationtoken");
		expect(github.calls.filter((c) => c.path.includes("access_tokens")).every((c) => c.body.permissions.issues === "write")).toBe(true);
	});

	it("adds repeat crashes to the open issue, and files a new one when it was closed", async () => {
		const crash = report({ kind: "crash", title: "InvalidOperationException: Sequence contains no elements", signature: "feedface12345678", installId: "d".repeat(32) });
		const first = (await (await send(crash)).json()) as any;
		expect(first.duplicate).toBe(false);
		expect(github.issues.get(first.number)!.labels).toEqual(["bug", "crash", "from-app"]);

		const again = (await (await send({ ...crash, version: "0.1.0-rc.4" })).json()) as any;
		expect(again).toMatchObject({ number: first.number, duplicate: true });
		expect(github.issues.get(first.number)!.comments[0]).toContain("Seen again in Cuebot 0.1.0-rc.4");

		github.issues.get(first.number)!.state = "closed";
		const regression = (await (await send(crash)).json()) as any;
		expect(regression.number).not.toBe(first.number);
		expect(github.issues.get(regression.number)!.body).toContain(`reported before in #${first.number} (closed)`);
	});

	it("checks everything on a dry run without filing", async () => {
		const res = await send(report({ dryRun: true }));
		expect(res.status).toBe(200);
		expect(github.issues.size).toBe(0);
		expect(github.calls.map((c) => c.path)).toContain("/app/installations/777/access_tokens");
	});

	it("limits reports per install and per IP", async () => {
		const install = "fedcba9876543210fedcba9876543210";
		for (let i = 0; i < 3; i++) expect((await send(report({ installId: install }))).status).toBe(201);
		const limited = await send(report({ installId: install }));
		expect(limited.status).toBe(429);
		expect(Number(limited.headers.get("Retry-After"))).toBeGreaterThan(0);

		for (let i = 0; i < 5; i++) {
			const other = i.toString(16).padStart(32, "a");
			expect((await send(report({ installId: other }), "192.0.2.9")).status).toBe(201);
		}
		expect((await send(report({ installId: "b".repeat(32) }), "192.0.2.9")).status).toBe(429);
	});

	it("refuses bad requests and reports GitHub failures without details", async () => {
		expect((await send(report({ title: "" }))).status).toBe(400);
		expect((await worker.fetch(new Request(`${BASE}/v1/reports`, { method: "POST", body: "{" }), testEnv)).status).toBe(400);
		expect((await worker.fetch(new Request(`${BASE}/v1/reports`, { method: "POST", body: "x".repeat(100_000) }), testEnv)).status).toBe(413);
		expect((await worker.fetch(new Request(`${BASE}/v1/reports`), testEnv)).status).toBe(405);
		expect((await worker.fetch(new Request(`${BASE}/nope`), testEnv)).status).toBe(404);
		expect(await (await worker.fetch(new Request(`${BASE}/`), testEnv)).json()).toEqual({ name: "cuebot-report-intake", ok: true });

		github.failCreate = true;
		const failed = await send(report({ installId: "c".repeat(32) }));
		expect(failed.status).toBe(502);
		expect(await failed.text()).not.toContain("boom");
	});
});
