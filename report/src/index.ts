// Cuebot report intake: a Cloudflare Worker that turns problem and crash reports from the Cuebot app into GitHub
// issues, so streamers can report without a GitHub account.
//
//   POST /v1/reports   {kind, title, body, version, os, installId, signature?, dryRun?}
//     201 {number, url, duplicate}   an issue was created, or (crashes) a comment was added to the open issue
//                                    already filed for the same crash signature
//     200 {ok: true}                 dryRun: everything checked, GitHub reachable, nothing filed
//     400 / 413 / 429 / 502 {error}
//   GET /                            {name, ok}
//
// The Worker signs in to GitHub as a GitHub App that can only write issues on one repository. Rate limits (per
// install, per IP and overall) and the crash-signature map live in one Durable Object. Text is scrubbed again here
// (secret shapes, e-mail addresses) and @mentions are defused, because the issues are public.

import { DurableObject } from "cloudflare:workers";
import { addComment, createIssue, getIssue, checkAccess, GitHubError, type GitHubConfig } from "./github";
import { scrub } from "./scrub";

export interface Env {
	INTAKE: DurableObjectNamespace<Intake>;
	/** The GitHub App's id (wrangler.jsonc vars). */
	GITHUB_APP_ID: string;
	/** The App's private key, PKCS#8 PEM. Set with the setup script (`wrangler secret put GITHUB_PRIVATE_KEY`). */
	GITHUB_PRIVATE_KEY: string;
	/** "owner/name" the issues go to. */
	GITHUB_REPO: string;
	/** GitHub's API (overridable for tests). */
	GITHUB_API?: string;
	/** Limits (wrangler.jsonc vars). */
	PER_INSTALL_PER_HOUR?: string;
	PER_INSTALL_PER_DAY?: string;
	PER_IP_PER_HOUR?: string;
	PER_DAY?: string;
}

export interface Report {
	kind: "problem" | "crash";
	title: string;
	body: string;
	version: string;
	os: string;
	installId: string;
	signature?: string;
	dryRun?: boolean;
}

export const MAX_TITLE = 200;
export const MAX_BODY = 60_000;
const MAX_REQUEST = 96 * 1024;

const json = (status: number, value: unknown, headers: Record<string, string> = {}) =>
	new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", ...headers } });

const str = (v: unknown) => (typeof v === "string" ? v.trim() : "");

/** The report from a request body, or an error message. */
export function parseReport(value: unknown): Report | string {
	if (typeof value !== "object" || value === null) return "The body must be a JSON object";
	const o = value as Record<string, unknown>;
	const kind = o.kind === "crash" ? "crash" : o.kind === "problem" ? "problem" : null;
	if (!kind) return "kind must be problem or crash";
	const title = str(o.title);
	if (title.length < 3 || title.length > MAX_TITLE) return `title must be 3 to ${MAX_TITLE} characters`;
	const body = str(o.body);
	if (body.length > MAX_BODY) return `body must be at most ${MAX_BODY} characters`;
	const version = str(o.version);
	if (!/^[0-9A-Za-z.+-]{1,40}$/.test(version)) return "version is missing or not a version";
	const os = str(o.os).slice(0, 100);
	const installId = str(o.installId);
	if (!/^[0-9a-f]{32}$/.test(installId)) return "installId must be 32 hex characters";
	const signature = str(o.signature);
	if (kind === "crash" && !/^[0-9a-f]{16,64}$/.test(signature)) return "a crash needs its signature (16 to 64 hex characters)";
	return { kind, title, body, version, os, installId, signature: signature || undefined, dryRun: o.dryRun === true };
}

async function shortHash(text: string): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
	return [...new Uint8Array(digest)].slice(0, 6).map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** The issue text: the report, then where it came from, then a hidden marker maintainers (and the Worker) can find. */
export async function issueBody(report: Report): Promise<string> {
	const reporter = await shortHash(report.installId);
	const lines = [
		scrub(report.body) || "_No description._",
		"",
		"---",
		`Sent from Cuebot ${report.version}${report.os ? ` on ${scrub(report.os)}` : ""} (reporter ${reporter}).`,
		`<!-- cuebot-report kind=${report.kind}${report.signature ? ` signature=${report.signature}` : ""} reporter=${reporter} -->`,
	];
	return lines.join("\n");
}

export function issueTitle(report: Report): string {
	const title = scrub(report.title).replace(/[\r\n]+/g, " ");
	return report.kind === "crash" && !/^crash\b/i.test(title) ? `Crash: ${title}`.slice(0, MAX_TITLE) : title;
}

export function labelsFor(report: Report): string[] {
	return report.kind === "crash" ? ["bug", "crash", "from-app"] : ["from-app"];
}

/** Rate limits and the crash map. One instance ("intake") serialises every report, which is plenty at this volume. */
export class Intake extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS hits (bucket TEXT NOT NULL, at INTEGER NOT NULL)");
		ctx.storage.sql.exec("CREATE INDEX IF NOT EXISTS hits_bucket ON hits (bucket, at)");
		ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS crashes (signature TEXT PRIMARY KEY, issue INTEGER NOT NULL)");
	}

	/**
	 * Counts one report against every limit, or refuses it with the seconds to wait. Nothing is counted when refused.
	 */
	take(installId: string, ip: string, now: number): { ok: true } | { ok: false; retryAfter: number } {
		const hour = 3_600_000;
		const day = 24 * hour;
		const limits: [string, number, number][] = [
			[`install:${installId}`, hour, Number(this.env.PER_INSTALL_PER_HOUR ?? 5)],
			[`install:${installId}`, day, Number(this.env.PER_INSTALL_PER_DAY ?? 20)],
			[`ip:${ip}`, hour, Number(this.env.PER_IP_PER_HOUR ?? 10)],
			["all", day, Number(this.env.PER_DAY ?? 300)],
		];
		const sql = this.ctx.storage.sql;
		sql.exec("DELETE FROM hits WHERE at < ?", now - day);
		for (const [bucket, window, max] of limits) {
			const rows = sql.exec<{ n: number; oldest: number }>("SELECT COUNT(*) AS n, MIN(at) AS oldest FROM hits WHERE bucket = ? AND at >= ?", bucket, now - window).one();
			if (rows.n >= max) return { ok: false, retryAfter: Math.max(1, Math.ceil((rows.oldest + window - now) / 1000)) };
		}
		for (const bucket of new Set(limits.map((l) => l[0]))) sql.exec("INSERT INTO hits (bucket, at) VALUES (?, ?)", bucket, now);
		return { ok: true };
	}

	crashIssue(signature: string): number | null {
		const row = this.ctx.storage.sql.exec<{ issue: number }>("SELECT issue FROM crashes WHERE signature = ?", signature).toArray()[0];
		return row ? row.issue : null;
	}

	rememberCrash(signature: string, issue: number): void {
		this.ctx.storage.sql.exec("INSERT OR REPLACE INTO crashes (signature, issue) VALUES (?, ?)", signature, issue);
	}
}

export function handler(fetchImpl: typeof fetch = (input, init) => fetch(input, init)) {
	return {
		async fetch(request: Request, env: Env): Promise<Response> {
			const url = new URL(request.url);
			if (request.method === "GET" && url.pathname === "/") return json(200, { name: "cuebot-report-intake", ok: true });
			if (url.pathname !== "/v1/reports") return json(404, { error: "Not found" });
			if (request.method !== "POST") return json(405, { error: "Use POST" }, { Allow: "POST" });

			const length = Number(request.headers.get("Content-Length") ?? "0");
			if (length > MAX_REQUEST) return json(413, { error: "The report is too large" });
			const text = await request.text();
			if (text.length > MAX_REQUEST) return json(413, { error: "The report is too large" });
			let value: unknown;
			try {
				value = JSON.parse(text);
			} catch {
				return json(400, { error: "The body is not JSON" });
			}
			const report = parseReport(value);
			if (typeof report === "string") return json(400, { error: report });

			const config: GitHubConfig = {
				appId: env.GITHUB_APP_ID,
				privateKey: env.GITHUB_PRIVATE_KEY,
				repo: env.GITHUB_REPO,
				api: env.GITHUB_API || "https://api.github.com",
				fetch: fetchImpl,
			};
			const intake = env.INTAKE.get(env.INTAKE.idFromName("intake"));
			try {
				if (report.dryRun) {
					await checkAccess(config);
					return json(200, { ok: true });
				}
				const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
				const quota = await intake.take(report.installId, ip, Date.now());
				if (!quota.ok)
					return json(429, { error: "Too many reports from here; try again later", retryAfter: quota.retryAfter }, { "Retry-After": String(quota.retryAfter) });

				const body = await issueBody(report);
				if (report.kind === "crash" && report.signature) {
					const known = await intake.crashIssue(report.signature);
					const existing = known === null ? null : await getIssue(config, known);
					if (known !== null && existing?.state === "open") {
						const reporter = await shortHash(report.installId);
						const extra = scrub(report.body).length > 0 ? `\n\n<details><summary>Report</summary>\n\n${scrub(report.body)}\n\n</details>` : "";
						await addComment(config, known, `Seen again in Cuebot ${report.version}${report.os ? ` on ${scrub(report.os)}` : ""} (reporter ${reporter}).${extra}`);
						return json(201, { number: known, url: existing.url, duplicate: true });
					}
					const issue = await createIssue(
						config,
						issueTitle(report),
						existing ? `${body}\n\nThe same crash was reported before in #${known} (closed).` : body,
						labelsFor(report),
					);
					await intake.rememberCrash(report.signature, issue.number);
					return json(201, { ...issue, duplicate: false });
				}
				const issue = await createIssue(config, issueTitle(report), body, labelsFor(report));
				return json(201, { ...issue, duplicate: false });
			} catch (e) {
				if (e instanceof GitHubError) return json(502, { error: "GitHub did not take the report; try again later" });
				throw e;
			}
		},
	};
}

export default handler();
