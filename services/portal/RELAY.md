# The shared portal relay

One Cloudflare Worker (`worker/index.ts`, `wrangler.jsonc`, Worker name `cuebot-portal`) that every Cuebot uses for the
mod portal and, if the streamer turns it on, for webhooks. The design and its reasons are in
[docs/design/MOD_PORTAL_PLAN.md](../../docs/design/MOD_PORTAL_PLAN.md) (sections 3, 7, 9 and 10). The Cuebot side is
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
| `relay.<domain>` | `GET /connect?id=<relay id>` (Cuebot), `GET /c/<relay id>` (devices), `GET /` health check; webhooks under `/h/<relay id>/...` too, for a relay URL override |
| `hooks.<domain>` | `/<relay id>/<route secret>/<service>[/<path>]` |
| `portal.<domain>` | `/` and `/auth/return` (the loader page), `/loader.js` (built from `web/` by `npm run build`), `/portal.css` |
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

## Push (inside the encrypted channel)

The relay never sees these: they are JSON messages inside a device's AES-256-GCM frames, sharing the session's counter
nonces with requests and answers (Cuebot encrypts and sends each frame under one lock, so the order holds). The relay
forwards them like any other frame and stores nothing. Cuebot side: `src/Cuebot.Portal/Relay/PortalPush.cs`
(`IPortalPush`); device side: `web/push.ts`, `web/link.ts`.

| Direction | Message | Meaning |
| --- | --- | --- |
| device → Cuebot | `{"op":"subscribe","req":n,"topic":"chat","params":{…}}` | Subscribe a signed-in device. `params` is an object of at most 2 KB, checked by the topic. |
| Cuebot → device | `{"ok":true,"req":n,"sub":<id>,"topic":"chat"}` or `{"ok":false,"req":n,"error":"…"}` (`"signIn":true` before sign-in) | The answer. It is always sent before the subscription's first push frame. |
| device → Cuebot | `{"op":"unsubscribe","req":n,"sub":<id>}` | Stop; always answered `{"ok":true}`. |
| Cuebot → device | `{"push":"chat","sub":<id>,"events":[…]}` | Events in order, batched about every 250 ms: at most 100 events and 32 KB a frame, 4 frames a subscription per batch. |
| Cuebot → device | `{"push":"chat","sub":<id>,"resync":true,"missed":<k>}` | The device fell behind (over 500 events or 256 KB waiting): the queue was dropped. Load the current state again; later events follow. |
| Cuebot → device | `{"push":"chat","sub":<id>,"ended":true,"error":"…"}` | The subscription ended: the member was removed or paused, the device signed out, the role lost the topic's permission, or the topic was withdrawn. |

Push frames carry no `req`, so loaders without push ignore them. A device may hold 8 subscriptions; one event is at
most 16 KB. A device whose earlier frames Cuebot is still sending is skipped until they are out, so a slow connection
ends in a resync rather than a growing queue. Subscriptions end with the device's connection: after a reconnect the
loader resumes the device, subscribes again and tells each page to resync (the gap was missed).

Caps checked for a busy chat: push frames stay far below the 1 MB Cuebot frame cap and Cuebot's frames have no rate
limit, so the Worker is unchanged. The 4 GB daily budget is shared: 20 devices reading a 20-message-a-second chat
(about 300 bytes a message) use about 120 KB/s, roughly 3 GB over 7 hours.

## Webhooks

`POST`, `GET` or `PUT` to the hooks address. The relay answers **200 with an empty body in every case** once the body
is read (Cuebot offline, wrong route secret, over the limit), so an address tells an outsider nothing. Only a malformed
address is `404`, another method `405`, a body over 1 MB `413`. When Cuebot is connected and SHA-256 of the route
secret matches what it registered, the relay builds the same delivery JSON as the self-hosted relay
(`services/relay/README.md`: `id`, `service`, `path`, `method`, `query`, `headers`, `bodyBase64`, `receivedAt`), leaving out
`cf-*`, `x-forwarded-*`, `x-real-ip`, `true-client-ip`, `forwarded` and `cdn-loop` (the sender's address and
Cloudflare's routing data), seals it and sends it on channel 0. Nothing is buffered: a webhook for an offline Cuebot is
gone.

Sealing: `0x01 ‖ ephemeral public key (65) ‖ AES-256-GCM ciphertext ‖ tag`, key from HKDF-SHA256 over the ECDH secret
between a fresh ephemeral key and the identity key, salt SHA-256(`"cuebot-seal-v1"` ‖ ephemeral ‖ identity), info
`"seal"`, nonce zero. `PortalSeal` and `IdentityKey.OpenSealed` in .NET, `seal` in `src/crypto.ts`.

## Kick events

Kick sends every webhook of an app to one address, so the events of all streamers on Cuebot's built-in Kick app arrive
at `hooks.<domain>/kick` (`/h/kick` on other hosts), served by `worker/kick.ts`:

| Direction | Message | Meaning |
| --- | --- | --- |
| Cuebot → relay | `{"type":"kick-route","rid":<n>,"token":"<Kick user access token>"}` | Route this broadcaster's Kick events here. The Worker checks the token with Kick (`POST id.kick.com/oauth/token/introspect`: active, the built-in app's client id, a user token; then `GET api.kick.com/public/v1/users` for the user id) and does not keep it. |
| Cuebot → relay | `{"type":"kick-unroute","rid":<n>,"userId":<n>}` | Remove the route, only when it points to this relay ID. |
| relay → Cuebot | `{"type":"kick-route" or "kick-unroute","rid":<n>,"ok":true,"userId":<n>}` or `{…,"ok":false,"error":"…"}` | The answer. |

At most 6 route requests a minute per relay ID. A POST to `/kick` is checked for age (its
`Kick-Event-Message-Timestamp` within 10 minutes) and Kick's signature (RSA PKCS#1 v1.5 SHA-256 over
`<message id>.<timestamp>.<raw body>`, key from `api.kick.com/public/v1/public-key`, cached an hour and fetched again
after a failure). The Worker reads `broadcaster.user_id` from the body, looks up the route and sends the delivery
(`service` `kick`, Kick's headers, raw body) sealed on channel 0, within the relay's webhook limits. Kick always gets an
empty 200: it unsubscribes an app whose address keeps failing. Only `POST` is accepted (`405` otherwise).

The built-in app's client ID is the Worker secret `KICK_CLIENT_ID` (`npx wrangler secret put KICK_CLIENT_ID`); without
it no routes are accepted. In Kick's developer settings the built-in app's webhook URL is
`https://hooks.techdaddydigital.com/kick`.

## Limits (per relay ID, in memory)

20 devices; 60 device connections a minute; 50 frames a second per device (bursts of 200); 64 KB per device frame,
1 MB per Cuebot frame; 300 webhooks a minute; 4 GB a day through the relay. They reset when the object is evicted.
There is no per-address limit: the Worker never reads the sender's IP address.

## What is kept

Nothing is written to Durable Object storage, KV, R2 or D1, except one `KickRoute` Durable Object per Kick broadcaster
on the built-in app: its name is `kick:<Kick user id>` and it stores the relay ID that Kick events go to and when that
was last confirmed. It is forgotten 30 days after the last confirmation (Cuebot confirms on every connection) and
removed when the streamer signs out of Kick. Workers logs, traces, Logpush and tail consumers are off
in `wrangler.jsonc`. The sockets' hibernation attachments hold only: the challenge nonce while Cuebot signs in; then
the identity public key and the route secret's hash; and each device's channel number. Cloudflare itself still sees IP
addresses in transit (docs/design/MOD_PORTAL_PLAN.md, section 9).

## Develop, test, deploy

```sh
cd portal
npm ci
npm test                      # crypto tests in Node, then the Worker's tests inside workerd
npx wrangler dev              # http://localhost:8787 serves every route
CUEBOT_PORTAL_RELAY_URL=http://localhost:8787 dotnet test ../../tests/Cuebot.Portal.Tests --filter PortalRelayLiveTests
npx wrangler deploy           # Kris's account; custom domains are attached in the Cloudflare dashboard
```

The live test connects a real Cuebot client, a device and a webhook through the running Worker. Point
`CUEBOT_PORTAL_RELAY_URL` at the deployed `workers.dev` address to check a deployment. In Cuebot, `RelayUrl` in
`portal.json` overrides the shared relay the same way.
