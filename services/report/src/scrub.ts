// Last line of defence before text becomes a public GitHub issue. Cuebot already scrubs reports on the PC
// (SecretScrubber); this catches anything that slipped through, and stops reports from pinging people.

const PATTERNS: [RegExp, string][] = [
	// GitHub, Slack, OpenAI, Anthropic, Google, xAI, Twitch-style and generic tokens.
	[/\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b/g, "[redacted-token]"],
	[/\bxox[abposr]-[A-Za-z0-9-]{10,}\b/g, "[redacted-token]"],
	[/\bsk-(ant-)?[A-Za-z0-9_-]{20,}\b/g, "[redacted-key]"],
	[/\bxai-[A-Za-z0-9]{20,}\b/g, "[redacted-key]"],
	[/\bAIza[0-9A-Za-z_-]{30,}\b/g, "[redacted-key]"],
	[/\boauth:[a-z0-9]{20,}\b/gi, "oauth:[redacted]"],
	[/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{16,}/g, "$1 [redacted]"],
	// JSON Web Tokens.
	[/\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g, "[redacted-jwt]"],
	// key=value / "key": "value" pairs whose name says secret.
	[
		/((?:access|refresh|id|api|auth|client|session|bot|stream)?[_-]?(?:token|secret|password|passwd|apikey|api_key|key))(["']?\s*[:=]\s*["']?)([^\s"'&,;]{6,})/gi,
		"$1$2[redacted]",
	],
	// RTMP stream keys.
	[/(rtmps?:\/\/[^\s/]+\/[^\s/]+\/)[A-Za-z0-9_?=&-]{8,}/g, "$1[redacted]"],
	// E-mail addresses.
	[/\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b/g, "[redacted-email]"],
];

/** The text with known secret shapes replaced and @mentions defused (a zero-width space after the @). */
export function scrub(text: string): string {
	let out = text;
	for (const [pattern, replacement] of PATTERNS) out = out.replace(pattern, replacement);
	return out.replace(/(^|[^\w`])@(?=[A-Za-z0-9-])/g, "$1@​");
}
