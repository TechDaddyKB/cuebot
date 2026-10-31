// The GitHub side: signs in as the GitHub App (a JWT signed with its private key), gets a short-lived installation
// token for the repository, and creates issues and comments with it. The App only has Issues: write on that repo.

export interface GitHubConfig {
	appId: string;
	/** PKCS#8 PEM ("BEGIN PRIVATE KEY"); the setup script converts GitHub's PKCS#1 key. */
	privateKey: string;
	/** "owner/name". */
	repo: string;
	api: string;
	fetch: typeof fetch;
}

export class GitHubError extends Error {
	constructor(
		message: string,
		readonly status: number,
	) {
		super(message);
	}
}

const USER_AGENT = "cuebot-report-intake";

let cachedToken: { repo: string; token: string; expires: number } | null = null;

function base64Url(bytes: ArrayBuffer | Uint8Array | string): string {
	const data = typeof bytes === "string" ? new TextEncoder().encode(bytes) : new Uint8Array(bytes);
	let binary = "";
	for (const b of data) binary += String.fromCharCode(b);
	return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function pemBody(pem: string): ArrayBuffer {
	const text = pem.replace(/-----(BEGIN|END) [A-Z ]+-----/g, "").replace(/\s+/g, "");
	const binary = atob(text);
	const bytes = new Uint8Array(binary.length);
	for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
	return bytes.buffer;
}

/** A JWT that authenticates as the App for 9 minutes (GitHub allows 10; iat is set 60 s back for clock drift). */
export async function appJwt(appId: string, privateKey: string, now = Date.now()): Promise<string> {
	if (privateKey.includes("BEGIN RSA PRIVATE KEY")) throw new GitHubError("The private key must be PKCS#8; run the setup script", 500);
	const key = await crypto.subtle.importKey("pkcs8", pemBody(privateKey), { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
	const seconds = Math.floor(now / 1000);
	const header = base64Url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
	const payload = base64Url(JSON.stringify({ iat: seconds - 60, exp: seconds + 540, iss: appId }));
	const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(`${header}.${payload}`));
	return `${header}.${payload}.${base64Url(signature)}`;
}

async function call(config: GitHubConfig, token: string, method: string, path: string, body?: unknown): Promise<any> {
	const response = await config.fetch(`${config.api}${path}`, {
		method,
		headers: {
			Authorization: `Bearer ${token}`,
			Accept: "application/vnd.github+json",
			"X-GitHub-Api-Version": "2022-11-28",
			"User-Agent": USER_AGENT,
			...(body === undefined ? {} : { "Content-Type": "application/json" }),
		},
		body: body === undefined ? undefined : JSON.stringify(body),
	});
	const text = await response.text();
	if (!response.ok) {
		let message = "";
		try {
			message = JSON.parse(text).message ?? "";
		} catch {
			// not JSON
		}
		throw new GitHubError(`GitHub ${method} ${path.split("?")[0]} answered ${response.status}${message ? `: ${message}` : ""}`, response.status);
	}
	return text.length ? JSON.parse(text) : null;
}

/** An installation token for the repository, reused until a minute before it expires. */
export async function installationToken(config: GitHubConfig, now = Date.now()): Promise<string> {
	if (cachedToken && cachedToken.repo === config.repo && cachedToken.expires - 60_000 > now) return cachedToken.token;
	const jwt = await appJwt(config.appId, config.privateKey, now);
	const installation = await call(config, jwt, "GET", `/repos/${config.repo}/installation`);
	const created = await call(config, jwt, "POST", `/app/installations/${installation.id}/access_tokens`, {
		repositories: [config.repo.split("/")[1]],
		permissions: { issues: "write" },
	});
	cachedToken = { repo: config.repo, token: created.token, expires: Date.parse(created.expires_at) || now + 30 * 60_000 };
	return created.token;
}

/** Forgets the cached token (tests). */
export function resetTokenCache(): void {
	cachedToken = null;
}

export async function createIssue(config: GitHubConfig, title: string, body: string, labels: string[]): Promise<{ number: number; url: string }> {
	const token = await installationToken(config);
	const issue = await call(config, token, "POST", `/repos/${config.repo}/issues`, { title, body, labels });
	return { number: issue.number, url: issue.html_url };
}

export async function getIssue(config: GitHubConfig, number: number): Promise<{ state: string; url: string } | null> {
	const token = await installationToken(config);
	try {
		const issue = await call(config, token, "GET", `/repos/${config.repo}/issues/${number}`);
		return { state: issue.state, url: issue.html_url };
	} catch (e) {
		if (e instanceof GitHubError && (e.status === 404 || e.status === 410)) return null;
		throw e;
	}
}

export async function addComment(config: GitHubConfig, number: number, body: string): Promise<string> {
	const token = await installationToken(config);
	const comment = await call(config, token, "POST", `/repos/${config.repo}/issues/${number}/comments`, { body });
	return comment.html_url;
}

/** Checks the App can reach the repository (used by dry runs). */
export async function checkAccess(config: GitHubConfig): Promise<void> {
	await installationToken(config);
}
