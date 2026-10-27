# Cuebot webhook relay

Ko-fi, Patreon, Shopify, Fourthwall, Throne, Kick events and similar services deliver events **only** by HTTP
webhook, i.e. to a public URL. A streamer's PC usually has none, so Cuebot ships this small relay that you deploy
once to **your own free Cloudflare account**:

```
service ──POST──▶ https://cuebot-relay.<you>.workers.dev/hook/<route secret>/<service>
                         │  (Worker answers 200 at once, hands the request to one Durable Object)
                         ▼
                 RelayHub Durable Object ── buffers in SQLite until acknowledged
                         ▲
Cuebot ──outbound WSS──▶ wss://cuebot-relay.<you>.workers.dev/connect   (Authorization: Bearer <connect token>)
```

- **No port forwarding**: Cuebot opens the connection, outbound, like a browser.
- **Nothing secret on Cloudflare** besides the two relay credentials: the relay forwards each request's raw bytes and
  headers untouched and does **not** check service signatures. Each Cuebot webhook handler (`IWebhookHandler`) verifies
  the signature or verification token with secrets kept in Cuebot's settings on your PC.
- **Offline buffering**: deliveries Cuebot has not acknowledged are kept (up to 500, at most 24 hours, oldest dropped
  first) and replayed when Cuebot reconnects. Cuebot drops replays it already handled.
- **Free plan**: one Worker plus one SQLite-backed Durable Object (available on the Workers Free plan). The
  connection uses the WebSocket Hibernation API, so an idle connection costs no Durable Object duration; heartbeats
  are answered by the runtime without waking the object.

## Deploy

Needs [Node.js](https://nodejs.org/) 20 or newer and a free Cloudflare account (<https://dash.cloudflare.com/sign-up>).

Windows (PowerShell):

```powershell
powershell -ExecutionPolicy Bypass -File relay\setup.ps1
```

Linux / macOS:

```sh
./relay/setup.sh
```

The script:

1. runs `npm install` (installs `wrangler`, Cloudflare's CLI, into `relay/node_modules`);
2. runs `npx wrangler login` if you are not signed in: approve the request in the browser window that opens;
3. generates a random **route secret** (48 hex characters) and **connect token** (43 characters);
4. runs `npx wrangler deploy` (on a brand-new account wrangler first asks you to choose a `workers.dev` subdomain);
5. stores both values as Worker secrets with `npx wrangler secret put ROUTE_SECRET` / `CONNECT_TOKEN`;
6. prints the **Relay URL**, **Connect token** and **Route secret**.

In Cuebot open **Webhooks**, paste the three values, tick "Use the webhook relay" and click **Connect**. The page
then lists the webhook URL for each service (`https://<relay>/hook/<route secret>/<service>`) with a Copy button;
paste it into the service's webhook settings.

The values are also saved in `relay/.relay.local.json` (git-ignored) so running the script again (for example after
updating Cuebot) redeploys without changing any URL. Use `setup.ps1 -Rotate` / `setup.sh --rotate` to generate new
values; every service then needs its new URL. To remove the relay, delete the `cuebot-relay` Worker in the
Cloudflare dashboard (Workers & Pages).

Manual deploy, if you prefer: `npm install`, `npx wrangler login`, `npx wrangler deploy`, then
`npx wrangler secret put ROUTE_SECRET` and `npx wrangler secret put CONNECT_TOKEN` (each at least 16 characters;
use long random values). Secrets never go in `wrangler.jsonc` or the repository.

## Endpoints

| Request | Answer |
| --- | --- |
| `POST`, `GET` or `PUT /hook/<route secret>/<service>[/<path...>][?query]` | `200`, empty body, as soon as the delivery is stored. `<service>`: lower-case letters, digits and dashes (1–64). |
| ... wrong or missing route secret, invalid service name | `404` (does not reveal whether the secret was wrong) |
| ... other methods (with the right secret) | `405` |
| ... body over 1 MB (`MAX_BODY_BYTES`) | `413` |
| `GET /connect` with `Upgrade: websocket` and `Authorization: Bearer <connect token>` | `101`; replaces any previous Cuebot connection (closed with code `4000`) |
| ... wrong or missing token | `401`; without the upgrade header `426` |
| `GET /` | `200` "Cuebot relay" (a health check for the setup script) |
| anything else | `404` |

Secrets are compared in constant time (both sides hashed with SHA-256, then `crypto.subtle.timingSafeEqual`). A
secret shorter than 16 characters counts as unset, so a relay deployed without secrets rejects everything.

### Services and their responses

Every service Cuebot targets only needs a fast `2xx` and ignores the body; the relay answers within milliseconds:
Ko-fi (retries when it gets no 200), Patreon, Shopify (must answer within 5 s, retries otherwise), Fourthwall,
Throne and Kick (retries on failure; Kick's webhook URL is configured in your Kick developer app, see
`src/Cuebot.Kick/PROTOCOL.md` §3). Services that require the endpoint to **echo a challenge** in the response
(Twitch EventSub webhooks, Meta `hub.challenge`, Slack `url_verification`) are not supported by this relay; Cuebot
uses Twitch's EventSub WebSocket instead.

## Protocol (relay ⇄ Cuebot)

JSON text messages over the `/connect` websocket.

Relay → Cuebot:

```jsonc
{ "type": "hello", "version": 1, "buffered": 2 }          // first message after connecting
{ "type": "delivery",
  "id": "4b7f…",                // UUID, unique per delivery; acknowledge it, drop replays of it
  "service": "kofi",             // the <service> URL segment
  "path": "my/hook",             // anything after the service, without a leading slash ("" when none)
  "method": "POST",
  "query": "x=1&y=2",            // without the leading "?"
  "headers": { "content-type": "application/x-www-form-urlencoded", … },   // lower-case names
  "bodyBase64": "ZGF0YT0…",      // the exact request body
  "receivedAt": "2026-10-27T12:00:00.000Z" }
{ "type": "pong" }
```

Cuebot → relay:

```jsonc
{ "type": "ack", "id": "4b7f…" }            // or { "type": "ack", "ids": ["…", "…"] }
{ "type": "ping" }                          // heartbeat every 30 s; answered with {"type":"pong"}
```

On connect the relay sends `hello` and then every buffered delivery, oldest first. A delivery stays buffered until
acknowledged, so a delivery sent while the connection drops is replayed on the next connection. Cuebot acknowledges
every delivery once its handler has finished, whatever the outcome (including "no handler for that service").

## Limits (wrangler.jsonc `vars`)

| Variable | Default | Meaning |
| --- | --- | --- |
| `MAX_BUFFERED` | 500 | Unacknowledged deliveries kept; the oldest are dropped beyond it |
| `MAX_AGE_HOURS` | 24 | Older unacknowledged deliveries are dropped |
| `MAX_BUFFER_BYTES` | 268435456 | Total size of buffered deliveries (256 MB) |
| `MAX_BODY_BYTES` | 1048576 | Largest accepted request body (1 MB) |

## Development

```sh
cd relay
npm install
npm test          # type-check + vitest inside the Workers runtime (@cloudflare/vitest-plugin)
npx wrangler dev  # local relay on http://localhost:8787 (put test secrets in relay/.dev.vars)
```

`@cloudflare/vitest-plugin` is the renamed successor of `@cloudflare/vitest-pool-workers`. npm 11 asks before running
install scripts; `package.json` `allowScripts` approves the two that are needed (`workerd`, Cloudflare's runtime used
by wrangler and the tests, and `esbuild`).
