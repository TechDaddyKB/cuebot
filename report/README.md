# Cuebot report intake

A Cloudflare Worker that turns **Send report** (Support page) and crash reports from the Cuebot app into GitHub
issues on `TechDaddyKB/cuebot`, so streamers can report problems without a GitHub account.

Live at `https://cuebot-report-intake.kris-barrantes.workers.dev` (the app posts to `/v1/reports`; the address is
`SupportLinks.ReportEndpoint`).

## What it does

- `POST /v1/reports` with `{kind: "problem"|"crash", title, body, version, os, installId, signature?, dryRun?}`:
  - **problem** → a new issue labelled `from-app`;
  - **crash** → a new issue labelled `bug`, `crash`, `from-app`, unless an **open** issue was already filed for the
    same crash signature: then a "Seen again in Cuebot vX on OS" comment is added there instead. A crash whose earlier
    issue was closed gets a new issue that links the old one.
  - **dryRun** checks the request and that the App can reach the repository, and files nothing.
- Answers `201 {number, url, duplicate}`, `200 {ok}` (dry run), `400` (with the reason), `413`, `429` (with
  `Retry-After`) or `502` (GitHub refused; no details leak).
- **Limits** (`wrangler.jsonc` vars): 5 reports per install per hour, 20 per day; 10 per IP per hour; 300 per day in
  all. They and the crash-signature map live in one SQLite-backed Durable Object (`Intake`, Workers Free plan).
- **Privacy:** the app scrubs reports before showing the preview (`SecretScrubber`); the Worker scrubs again (tokens
  and keys of GitHub, Slack, OpenAI, Anthropic, Google, xAI, Twitch; bearer headers; JWTs; `*token=`/`*secret=`/
  `password=` values; RTMP stream keys; e-mail addresses) and defuses `@mentions` so a report cannot ping anyone. The
  issue shows a 12-character hash of the install id, never the id.

## How it signs in to GitHub

The **Cuebot Reports** GitHub App (owned by TechDaddyKB, id in `wrangler.jsonc`) has one permission, Issues read and
write, and is installed on `TechDaddyKB/cuebot` only. The Worker signs a JWT with the App's private key (the
`GITHUB_PRIVATE_KEY` secret, PKCS#8), exchanges it for a one-hour installation token limited to that repository and
issue permissions, and creates issues and comments with it. Issues appear as authored by `cuebot-reports[bot]`.

## Setup (done 2026-10-30; for a fresh deployment)

```bash
npm install
npx wrangler login
node scripts/setup.mjs            # creates the App from a manifest, deploys, stores the key
```

The script serves a page on `127.0.0.1:8917`; its button posts the App manifest to GitHub, you click **Create GitHub
App**, and GitHub hands a one-time code back to the script, which exchanges it for the App's id and key. The id goes
into `wrangler.jsonc`; the key is converted to PKCS#8 and piped to `wrangler secret put GITHUB_PRIVATE_KEY` (never
printed or written to disk). Then install the App on the repository only (the script prints the link) and create the
labels: `gh label create crash`, `gh label create from-app` (`bug` exists).

## Operating it

- **Check it:** `curl -X POST <worker>/v1/reports -H "Content-Type: application/json" -d '{"kind":"problem","title":"Dry run","body":"","version":"0.0.0","os":"x","installId":"00000000000000000000000000000001","dryRun":true}'` → `{"ok":true}`.
- **Rotate the key:** GitHub → Settings → Developer settings → GitHub Apps → Cuebot Reports → Private keys →
  Generate; convert with `openssl pkcs8 -topk8 -nocrypt -in key.pem | npx wrangler secret put GITHUB_PRIVATE_KEY`;
  delete the old key on GitHub and the downloaded file.
- **Stop it** (spam, abuse): suspend the App's installation on GitHub, or `npx wrangler delete`; the app then shows
  "could not reach the report service" and offers the browser link.
- **Change limits:** edit `wrangler.jsonc` vars and `npx wrangler deploy`.

## Tests

`npm test` (TypeScript check + vitest in the Workers runtime): parsing, scrubbing, issue text, the App's JWT
(verified with the matching public key), filing, crash grouping, dry run, rate limits and errors, against a fake
GitHub and a throwaway key.
