# The shared portal relay

One Cloudflare Worker (`worker/index.ts`, `wrangler.jsonc`, Worker name `cuebot-portal`) that every Cuebot uses for the
mod portal and, if the streamer turns it on, for webhooks. The design and its reasons are in
[docs/MOD_PORTAL_PLAN.md](../docs/MOD_PORTAL_PLAN.md) (sections 3, 7, 9 and 10). The Cuebot side is
`src/Cuebot.Portal/Relay/PortalRelayClient.cs`.

```
Mod device ──WSS /c/<relay id>──▶ relay.techdaddydigital.com ◀──WSS /connect?id=<relay id>── Cuebot
                                   one Durable Object per relay ID, sockets only, nothing stored
Service ──POST──▶ hooks.techdaddydigital.com/<relay id>/<route secret>/<service>[/<path>] ──sealed──▶ Cuebot
Browser ──GET──▶ portal.techdaddydigital.com/   and   /auth/return
```

## Hosts

| Host | Routes |
| --- | --- |
| `relay.<domain>` | `GET /connect?id=<relay id>` (Cuebot), `GET /c/<relay id>` (devices), `GET /` health check |
| `hooks.<domain>` | `/<relay id>/<route secret>/<service>[/<path>]` |
| `portal.<domain>` | `/`, `/auth/return`, `/portal.css` (placeholders until the loader, phase 4) |
| any other `<name>.<domain>` | 404 |
| any other host (workers.dev, `wrangler dev`) | all of the above; webhooks under `/h/<relay id>/...` |

`<domain>` is the `PORTAL_DOMAIN` var (`techdaddydigital.com`). A relay ID is 32 characters of lowercase base32
(`[a-z2-7]`): the first 20 bytes of SHA-256 of Cuebot's raw P-256 identity public key.

## Cuebot's connection

WebSocket to `/connect?id=<relay id>`. Text messages are JSON; binary messages are frames.

1. Relay → `{"type":"challenge","version":1,"nonce":"<base64url, 32 bytes>"}`
2. Cuebot → `{"type":"auth","key":"<base64url raw public key, 65 bytes>","signature":"<base64url P1363, 64 bytes>","hooks":"<base64url SHA-256 of the route secret>"}`.
   The signature is ECDSA P-256 SHA-256 over `"cuebot-relay-connect-v1" || nonce || host`, where host is the
   address Cuebot connected to (`relay.techdaddydigital.com`, or `host:port`).
3. The relay checks the key hashes to the relay ID and the signature, then answers
   `{"type":"ready","version":1,"maxDevices":20}`. Only now does it close an older Cuebot connection for the same
   ID (`4000`), so a stranger cannot knock a streamer offline. Any connected devices are closed (`4503`) so they
   redo the handshake with the new connection.

No answer within 15 seconds: closed with `4002`. Wrong key or signature: `4001`. Malformed answer: `4003`.

After `ready`:

| Direction | Message | Meaning |
| --- | --- | --- |
| relay → Cuebot | `{"type":"open","ch":<n>}` | A device connected on channel `n` (random, never 0). |
| relay → Cuebot | `{"type":"close","ch":<n>}` | That device left. |
| relay → Cuebot | binary `ch (uint32 BE) ‖ payload` | A frame from device `ch`, untouched. |
| relay → Cuebot | binary `0 ‖ sealed` | A webhook, sealed to the identity key (below). |
| Cuebot → relay | binary `ch ‖ payload` | A frame for device `ch` (unknown channels are dropped). |
| Cuebot → relay | `{"type":"close","ch":<n>,"code":<4000-4999>,"reason":"..."}` | Close that device with that code. |
| either | `{"type":"ping"}` / `{"type":"pong"}` | Heartbeat; the runtime answers it without waking the object. |

## Devices

WebSocket to `/c/<relay id>`. Binary frames only, passed to Cuebot as they are: the first is the handshake hello, the
rest are AES-256-GCM frames of the end-to-end session (`src/crypto.ts`, `src/Cuebot.Portal/Crypto`). The relay
cannot read them. Close codes:

| Code | Why |
| --- | --- |
| `4404` | The streamer's Cuebot is not connected. |
| `4429` | Too many devices or connection attempts, a frame over 64 KB, too many frames, or the daily limit. |
| `4503` | Cuebot left or reconnected. |
| `4003` | A text frame, or (from Cuebot) the secure channel failed. |
| `4403` | (from Cuebot) The streamer has not opened the mod portal. |
| `4100`-`4999` | Anything else Cuebot chooses (for example signing a device out). |

## Webhooks

`POST`, `GET` or `PUT` to the hooks address. The relay answers **200 with an empty body in every case** once the body
is read (Cuebot offline, wrong route secret, over the limit), so an address tells an outsider nothing. Only a malformed
address is `404`, another method `405`, a body over 1 MB `413`. When Cuebot is connected and SHA-256 of the route
secret matches what it registered, the relay builds the same delivery JSON as the self-hosted relay
(`relay/README.md`: `id`, `service`, `path`, `method`, `query`, `headers`, `bodyBase64`, `receivedAt`), leaving out
`cf-*`, `x-forwarded-*`, `x-real-ip`, `true-client-ip`, `forwarded` and `cdn-loop` (the sender's address and
Cloudflare's routing data), seals it and sends it on channel 0. Nothing is buffered: a webhook for an offline Cuebot is
gone.

Sealing: `0x01 ‖ ephemeral public key (65) ‖ AES-256-GCM ciphertext ‖ tag`, key from HKDF-SHA256 over the ECDH secret
between a fresh ephemeral key and the identity key, salt SHA-256(`"cuebot-seal-v1"` ‖ ephemeral ‖ identity), info
`"seal"`, nonce zero. `PortalSeal` and `IdentityKey.OpenSealed` in .NET, `seal` in `src/crypto.ts`.

## Limits (per relay ID, in memory)

20 devices; 60 device connections a minute; 50 frames a second per device (bursts of 200); 64 KB per device frame,
1 MB per Cuebot frame; 300 webhooks a minute; 4 GB a day through the relay. They reset when the object is evicted.
There is no per-address limit: the Worker never reads the sender's IP address.

## What is kept

Nothing is written to Durable Object storage, KV, R2 or D1. Workers logs, traces, Logpush and tail consumers are off
in `wrangler.jsonc`. The sockets' hibernation attachments hold only: the challenge nonce while Cuebot signs in; then
the identity public key and the route secret's hash; and each device's channel number. Cloudflare itself still sees IP
addresses in transit (docs/MOD_PORTAL_PLAN.md, section 9).

## Develop, test, deploy

```sh
cd portal
npm ci
npm test                      # crypto tests in Node, then the Worker's tests inside workerd
npx wrangler dev              # http://localhost:8787 serves every route
CUEBOT_PORTAL_RELAY_URL=http://localhost:8787 dotnet test ../tests/Cuebot.Portal.Tests --filter PortalRelayLiveTests
npx wrangler deploy           # Kris's account; custom domains are attached in the Cloudflare dashboard
```

The live test connects a real Cuebot client, a device and a webhook through the running Worker. Point
`CUEBOT_PORTAL_RELAY_URL` at the deployed `workers.dev` address to check a deployment. In Cuebot, `RelayUrl` in
`portal.json` overrides the shared relay the same way.
