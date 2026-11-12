# Webhook services: Ko-fi, Patreon, Fourthwall, Shopify, Throne, custom hooks

Everything here comes from each service's public documentation (sources at the end), real captured test deliveries
published by others (payload shapes only), and Streamer.bot's public trigger docs (trigger names and argument names).
None of it has been tried against the real services from Cuebot yet.

Deliveries reach Cuebot through the relay (`https://<relay>/hook/<relay secret>/<route>[/<path>]`), which answers 200
at once and forwards the raw bytes (header names lower-cased) without checking them. **Each handler verifies the
delivery itself**: with a secret stored in `webhooks.json`, or for Throne with Throne's published public key. A
delivery that fails verification is `Rejected` and never publishes events; one that arrives before the secret is
entered is `NotConfigured`. A switched-off service verifies, then returns `Ignored`. Secrets are compared in constant
time (`CryptographicOperations.FixedTimeEquals`), masked on the page and never logged.

Every event carries `service` (display name), `user`, `amount` (major units, a double), `currency`,
`formattedAmount`, `message` and `isTest`, plus the service-specific arguments below (Streamer.bot's names).

**Dedupe.** Each event is remembered (last 1000, at most 48 hours) by the service's own id where there is one and always
by the relay delivery id, so a service retry and a relay replay both publish once. The app keeps these ids on disk
(`webhooks-seen.json`, written before the event is published), so a delivery the own relay replays after Cuebot crashed
or quit before acknowledging it is still dropped. 48 hours covers the own relay's 24-hour buffer. Blank ids are ignored
(a delivery without an id never looks like a duplicate of another one without an id). Local test events are never
deduplicated.

**Test events.** Each service's *Send test event* (and the `webhooks.sendTestEvent(service, kind)` script function)
publishes a built-in sample locally, marked `isTest`, without verification. Deliveries sent by a service's own test
button are marked `isTest` where the service says so (Fourthwall `testMode`, Shopify `"test": true` in the body — a
documented sample-payload field — or an `X-Shopify-Test: true` header, seen in captured deliveries but not documented,
Ko-fi's placeholder transaction id); Patreon and Throne test sends carry no test flag.

| Route | Verification | What the user enters |
| --- | --- | --- |
| `kofi` | Form field `data` holds JSON; its `verification_token` must equal the saved token | https://ko-fi.com/manage/webhooks (Settings → API → Webhooks) → Advanced → *Verification Token* |
| `patreon` | `X-Patreon-Signature` = hex HMAC-MD5 of the raw body, keyed with the webhook secret | patreon.com/portal → My Webhooks → the webhook's *secret* |
| `fourthwall` | `X-Fourthwall-Hmac-SHA256` and/or `X-Fourthwall-Hmac-Apps-SHA256` = base64 HMAC-SHA256 of the raw body; accepted when either verifies | Settings → For developers → Webhooks → *webhook secret key* |
| `shopify` | `X-Shopify-Hmac-Sha256` = base64 HMAC-SHA256 of the raw body | Admin-created webhooks (Settings → Notifications → Webhooks): the store's key shown below the webhook list ("Your webhooks are signed with …", one per store). Only for webhooks an existing custom app or a Dev Dashboard app subscribed itself: that app's client secret |
| `throne` | Ed25519: `X-Signature-Ed25519` (hex) over `X-Signature-Timestamp + "." + raw body`, Throne's public key; plus the optional Cuebot URL token when one is set | Nothing required. Optional: *Generate token* in Cuebot and add `/<token>` to the URL in Throne |
| `custom/<name>` | The named hook's token as `?token=` or `X-Cuebot-Token` | *Add hook* in Cuebot (token generated) |

**How to send a real test** (through the relay, verified like a real delivery):

- Ko-fi: ko-fi.com/manage/webhooks → the test buttons (donation, subscription, commission, shop order); all reuse one transaction id.
- Patreon: patreon.com/portal → My Webhooks → *Send Test* next to each trigger (no test flag; `posts:publish` cannot be test-sent).
- Shopify: Settings → Notifications → Webhooks → ⋯ next to the webhook → *Send test* (whether it sets `"test": true`, the header, or both is not documented).
- Fourthwall: Settings → For developers → Webhooks → *Send test notification* (arrives with `testMode: true`).
- Throne: Settings → Integrations → Webhooks → the test button.

## Ko-fi

Source: Ko-fi's help article "Does Ko-fi have an API or webhook?". Ko-fi sends a POST only when a payment is made:
there is no cancellation, refund or goal event and no API to read payments back. It retries (same `message_id`) only
when it gets no 200; the relay always answers 200, so a delivery the shared relay drops while Cuebot is closed is lost
for good (the own relay buffers 24 h). There is no signature: the only check is the `verification_token` in the body.

`application/x-www-form-urlencoded`, `data=<json>` (a raw JSON body is accepted too). Dedupe on `message_id` only:
Ko-fi's retries reuse it. Ko-fi's test buttons all send the placeholder `kofi_transaction_id`
`00000000-1111-2222-3333-444444444444` (with a fresh `message_id` each time), so deliveries with that transaction id
are marked `isTest` and every test click is published.

| Ko-fi `type` | Event | Arguments |
| --- | --- | --- |
| `Donation` | `kofi.donation` | `amount`, `currency`, `from`, `isPublic`, `message`, `messageId`, `timestamp`, `url`, `transactionId`, `kofiType`, `isSubscriptionPayment`, `isReplay`, `delaySeconds`, `discordUsername` / `discordUserId` / `discordMention` (when Ko-fi sends `discord_username` / `discord_userid`), `kofiGoal*` |
| `Subscription` with `is_first_subscription_payment` | `kofi.subscription` | + `tier` (`tier_name`), `isFirstPayment`, `months` (payments Cuebot has seen) |
| `Subscription` (later payments) | `kofi.resubscription` | same (`months` at least 2) |
| `Commission` | `kofi.commission` | as donation, + `commissionsOpen` |
| `Shop Order` | `kofi.shopOrder` | + `itemCount`, `item0..n` (product name from settings, else variation, else `item <code>`), `item<n>.code`, `item<n>.variation`, `item<n>.image`, `item<n>.quantity` (1 when missing) |

**Private supports.** With `is_public: false` (a payload without the field counts as public) `user` is "Anonymous",
`message` and `from` are empty everywhere (triggers, overlays, feed, speech, credits); the real values are only in
`privateName` / `privateMessage`, plus `privateShown`. The opt-in `KofiOptions.ShowPrivateSupporters` puts them back in
`user` / `message` (the feed and credits still say "Someone"). `email` and `shipping` are personal data and are never
arguments: do not flatten the whole payload.

**Late deliveries.** `isReplay` is true when the delivery reaches Cuebot more than `KofiOptions.LateMinutes` (10) after
the relay received it; the payment triggers' *Skip late deliveries* skips those while the feed, totals and goal count.

**Overlays.** Donations → `tip-latest`; shop orders and commissions (`items: "Commission"`) → `merch-latest`;
memberships → `subscriber-latest` with `amount` = `months`, `tier` "1000", `tierName`, `price`. A late delivery
(`isReplay`) gives an alert with `isCatchUp` = true: Cuebot's alert box skips it unless "show catch-up alerts" is on;
event lists and session totals count it.

**Kept by Cuebot (`kofi.json`, `KofiTracker`).** Only real, new payments change it (after the duplicate check; tests
never). The goal (`KofiOptions.Goal`: title, target, start amount, currency filter, which types count) fires
`kofi.goalReached` once per crossing; members (key: Discord id, else lower-case Ko-fi name) fire
`kofi.membershipLapsed` once when the last payment is older than `LapseDays` (35), an estimate; commissions form an
open list. Overlay session data gets `kofi-goal` and `kofi-commissions`.

## Patreon

`X-Patreon-Event` names the event. The body is JSON:API: `data` is the member, `included` holds the user, tiers and
campaign. API v1 was retired on 2026-10-07; creators who were granted an extension keep the v1 `pledges:*` triggers
until 2027-01-20. Cuebot ignores `pledges:*`, so tick the `members:pledge:*` triggers instead.

| `X-Patreon-Event` | Event |
| --- | --- |
| `members:create` for a free member, or a gifted / free-trial member (`is_gifted` / `is_free_trial`: active patron with no pledge event) | `patreon.followCreated` (Streamer.bot's id; the feed says "joined as a free member", or a gifted membership / free trial) |
| `members:create` for any other active patron (it also gets `members:pledge:create`) | ignored |
| `members:delete` (a free or paying member who left completely) | `patreon.followDeleted` |
| `members:pledge:create` / `:update` / `:delete` | `patreon.pledgeCreated` / `pledgeUpdated` / `pledgeDeleted` |
| `members:update` | `patreon.memberUpdated` (Cuebot extra) |
| `posts:publish` / `posts:update` / `posts:delete` | `patreon.postPublished` / `postUpdated` / `postDeleted` (Cuebot extras; `title`, `url` made absolute with `https://www.patreon.com`, `isPaid`, `isPublic`) |

Patreon documents that a gift gets no `members:pledge:create`; it does not say the same for a free trial. Cuebot treats
a trial like a gift (members:create → followCreated). If live deliveries show a pledge event for trials too, the trial
branch must go (to be confirmed live).

**Dedupe.** Patreon sends no event id. Every delivery is keyed by event name + SHA-256 of the body (kept 48 h in
webhooks-seen.json). `members:pledge:create` is also keyed by member id + `pledge_relationship_start`, so a re-sent new
pledge with a changed body counts once. Patreon says `members:create` "may fire more than once per patron" with
different bodies, so it is also keyed by member id for 10 minutes.

**Arguments** follow Streamer.bot: `id`, `eventType`, `attributes.<member attribute>` (`patron_status`, `full_name`,
`email`, `currently_entitled_amount_cents`, `last_charge_status`, `note`, ...), `user.id`, `user.type`,
`user.Attributes.<user attribute>`, `entitledTiers[i].id/type/Attributes.<tier attribute>`. Deprecated fields are still
forwarded for Streamer.bot parity but should not be used: `attributes.is_follower` (always false: following was
replaced by free membership), `attributes.lifetime_support_cents` (use `campaign_lifetime_support_cents`, published as
`lifetimeSupport`) and `user.Attributes.vanity` (use the campaign's vanity).

Cuebot adds `tier` (titles joined with ", "; $0 tiers left out next to a paid one), `tiers` / `tierIds` (lists, used by
the tier filter, so a title with a comma still matches), `patronStatus`, `isGifted`, `isFreeTrial`, `note` (the
creator's private note, never the `message`), and the money:

- `currency`: the campaign's currency. From `included` campaign `attributes.currency` (added to the API on 2026-03-09;
  whether webhook payloads carry it is still to be checked live), else the currency the API connection or an earlier
  delivery told Cuebot, else the "currency when unknown" setting, else empty (the currency filter then lets it pass).
- `amount` = `monthlyAmount`: the pledge per month. The charge is `currently_entitled_amount_cents` (the current
  period), else `will_pay_amount_cents`; `pledge_cadence` is the months between charges, so a yearly pledge of 60 is
  `amount` 5. The raw charge is `chargeAmount` / `formattedCharge`, the cadence `pledgeCadence`. Filters, the feed,
  top spenders and subathon time use the monthly value. On `members:pledge:delete` `amount` is the pledge that ended
  (Patreon may keep the entitled amount until the period ends).

Since 2026-05-26 a patron can hide their identity: the user's `full_name`, `image_url`, `url`... are then null. When
the included user's `full_name` is null, `user` is "An anonymous patron", `isAnonymous` is true, the member's name is
only in `privateName`, and `avatarUrl` / `profileUrl` are empty. Overlays should have a fallback picture anyway.

**Stale backlog.** Failed deliveries are retried (about 30 s, 5 min, 15 min, 1 h, 3 h, 1 day, 1 week); after repeated
failures Patreon pauses the webhook, and resuming it sends everything held back at once. A `members:pledge:create`
whose `pledge_relationship_start` and `last_charge_date` are both older than `StaleHours` (6; 0 = off) gets
`isStale` true (`ageHours` says how old); every Patreon trigger skips stale events unless "Skip stale deliveries" is
unticked. They still update the member list and totals. Their overlay alert has `isCatchUp` = true (skipped by the
alert box by default, counted by lists and totals).

**Overlays** (`WebhookOverlayAdapter`, service `patreon`). `patreon.pledgeCreated` → `subscriber-latest` with `amount`
1 (Patreon sends no renewals, so there is no month count), `tier` "1000", `tierName`, `price` (monthly), `currency`;
`patreon.pledgeUpdated` with `direction` = `upgrade` → the same plus `upgrade: true`, `previousTier`,
`previousAmount` (downgrades, `same` and an unknown direction are no alert); `patreon.followCreated` (free member,
gift, free trial) → `follower-latest` with `isGifted` / `isFreeTrial`. A paying patron's `members:create` is already
ignored, so a join alerts once. `name` is `user` ("An anonymous patron" when hidden), `avatar` is `avatarUrl` (empty
when hidden), `providerId` is the user id (empty when hidden); `privateName` never reaches overlays.

**API connection (optional).** The creator pastes their own client's Client ID, Client Secret, Creator's Access Token
and Refresh Token (patreon.com/portal/registration/register-clients; a creator token has every v2 scope). All calls
send a User-Agent. A 401 refreshes the token once (`POST /api/oauth2/token`, `grant_type=refresh_token`); a refused
refresh (or a 403) stops all calls until the user connects again, because Patreon's edge limiter blocks clients with
many 4xx answers. Calls:

| Call | Use |
| --- | --- |
| `GET /api/oauth2/v2/identity?include=campaign` | the campaign id |
| `GET /api/oauth2/v2/campaigns/{id}?include=tiers&fields[campaign]=creation_name,currency,patron_count,vanity,url&fields[tier]=title,amount_cents,published` | currency, patron count, tiers (tier picker suggests "Title #id"; the filter matches the id, so renaming a tier keeps triggers working) |
| `GET /api/oauth2/v2/campaigns/{id}/members?include=currently_entitled_tiers,user&...&page[count]=1000` (cursor `meta.pagination.cursors.next`) | member list on connect and every 6 h: totals, patron-to-viewer links |
| `GET /api/oauth2/v2/members/{id}?include=pledge_history&fields[pledge-event]=type,amount_cents,currency_code,tier_title,date` | after `members:pledge:update` (5 s limit): `direction` (`pledge_upgrade` / `pledge_downgrade`), `previousAmount`, `previousTier`. Without the API the direction comes from the member list Cuebot keeps |
| `GET /api/oauth2/v2/posts/{id}?fields[post]=title,url,is_paid,is_public,tiers,published_at` | after `posts:publish` / `posts:update`: `isPaid`, `isPublic`, `tierIds`, `tierNames` |
| `GET` / `POST` / `PATCH /api/oauth2/v2/webhooks` | create the webhook for the relay URL with every `members:*` / `members:pledge:*` / `posts:*` trigger and save its `secret`; read `paused`, `num_consecutive_times_failed`, `last_attempted_at`; resume with `paused: false`. Patreon lists only webhooks made by the same client, so a webhook made by hand in the portal cannot be checked |

Kept in patreon.json: campaign (name, vanity, url, currency, patron count), tiers, members (status, amount, cadence,
tiers, since, linked Twitch / YouTube / Discord ids), last sync, webhook health. Totals: global variables
`patreonPatronCount` (Patreon's count, else active members known), `patreonPaidCount` (active, amount > 0, not a gift
or trial), `patreonMonthly` (their monthly pledges added up), `patreonCurrency`; overlay session data `patreon`; the
Goal widget's "Patreon paying patrons" / "Patreon monthly pledges". Campaign `goals` are deprecated (always empty), so
goals are set in Cuebot. Linked viewers (`user.attributes.social_connections.twitch.user_id` / `.url`, `youtube.user_id`)
get the user variables `isPatron`, `patreonTier`, `patreonStatus`, `patreonAmount`, `patreonSince`; a Twitch link
that names a login rather than the numeric id is resolved through the viewers Cuebot has seen in chat.

## Fourthwall

Envelope `{ testMode, id, webhookId, shopId, type, apiVersion, createdAt, data }`; dedupe on `id` (kept 48 h in
webhooks-seen.json, so a Fourthwall retry that reaches the relay after Cuebot restarted is still recognised). Delivery
is at least once and in no set order across topics (an `ORDER_UPDATED` can arrive before its `ORDER_PLACED`).
`apiVersion` is never checked: the webhook-model example says `V1`, the spec `V1_BETA`. Arguments use Streamer.bot's
`fw.*` names (`fw.statmessageus` is kept as documented, `fw.message` is the same text), each also without the `fw.`
prefix (Streamer.bot 1.0.7's runs). User text may arrive HTML-escaped (`&#39;`): `user` and `message` (the feed and
speech) are HTML-decoded, the `fw.*` arguments keep the raw text (low confidence that this is needed; harmless when the
text is not escaped).

Docs drift (2026-11-11): the Alerts guide says the signature is hex and that the secret comes back in the webhook create
response; the signature-verification page and the OpenAPI spec say base64 HMAC-SHA256 with the shop-wide secret, which
the API never returns. Cuebot follows the signature page and the spec. Webhook URLs must be https on port 443 with a
hostname (`WEBHOOK_URI_IP_NOT_ALLOWED`, `WEBHOOK_URI_PORT_NOT_ALLOWED`, `WEBHOOK_URI_UNALLOWED_DOMAIN`): use the relay.

| `type` | Event | Main arguments |
| --- | --- | --- |
| `ORDER_PLACED` / `ORDER_UPDATED` (`data.order` + `data.update.type`) | `fourthwall.orderPlaced` / `orderUpdated` | `fw.orderId`, `fw.friendly`, `fw.checkoutId`, `fw.status`, `fw.email`, `fw.username`, `fw.subtotal/shipping/tax/donation/discount/total`, `fw.currency`, `fw.source`, `fw.giftId`, `fw.giftPurchaseId`, `fw.promotionId`, `fw.variants[i].id/name/sku/quantity/unitPrice/currencyCode/image/attributes.*`, `fw.updateType`, `isPurchase`, `isCancelled` |
| `DONATION` | `fourthwall.donation` | `fw.donationId`, `fw.amount`, `fw.currency`, `fw.status`, `fw.username`, `fw.email`, `fw.message`, `isCompleted` |
| `GIFT_PURCHASE` | `fourthwall.giftPurchase` | `fw.Id`, `fw.offer.*`, totals, `fw.quantity`, `fw.gifts[i].id/status/winner/redeemUri/selectedAt/url/...`, `fw.giftUrls`, `fw.giftUrl` |
| `GIFT_DRAW_STARTED` / `_ENDED` | `fourthwall.giftDrawStarted` / `giftDrawEnded` | as gift + `fw.drawId`, `fw.giveawayId`, `fw.durationSeconds`, `fw.winners`, `fw.winnerCount`; `fw.quantity` = number of gifts (draws send no quantity); started: `fw.cuebotRunsDraw`, `fw.entryCommand` |
| `SUBSCRIPTION_PURCHASED` / `_CHANGED` / `_EXPIRED` | `fourthwall.subscription*` | `fw.id`, `fw.nickname`, `fw.email`, `fw.type` = `fw.state`, `fw.isCancelled`, `fw.isSuspended`, `fw.isActive`, `fw.interval`, `fw.tierId`, `fw.tierName` = `tier`, `fw.amount`, `fw.monthlyAmount`, `fw.currency`, `fw.previousTierId/previousTier/previousAmount/previousState`, `direction` |
| `NEWSLETTER_SUBSCRIBED` | `fourthwall.newsletterSubscribed` | `fw.email` |
| `THANK_YOU_SENT` | `fourthwall.thankYouSent` | `fw.id`, `fw.mediaUrl`, `fw.contributionType` (`ORDER`, `DONATION`, `GIFT_PURCHASE`, `MEMBERSHIP_PAYMENT_CHARGE`), `fw.contributionId`, `fw.username`, `fw.message` |
| `PRODUCT_CREATED` (bare `OfferFullV1`) / `PRODUCT_UPDATED` (`{product:{results:[...]}}`, or bare) | `fourthwall.productCreated` / `productUpdated` (one event per product) | `fw.id`, `fw.name`, `fw.slug`, `fw.type`, `fw.state`, `fw.access`, `fw.isPublic`, `fw.isSoldOut`, `fw.imageUrl`, `fw.price`, `fw.formattedPrice`, `fw.variants[i].*` |
| `PROMOTION_CREATED` / `_UPDATED` / `_STATUS_CHANGED` (`PromotionV1`) | `fourthwall.promotionCreated` / `promotionUpdated` / `promotionStatusChanged` (Cuebot extras) | `fw.promotionId`, `fw.promotionType`, `fw.title`, `fw.code`, `fw.codes`, `fw.status` (`Live`, `Ended`, `Archived`, `AllUsed`), `fw.isLive`, `fw.isAllUsed`, `fw.usageCount`, `fw.maxUses`, `fw.discountType`, `fw.percentage`, `fw.discount` (text) |
| `COLLECTION_UPDATED` | `fourthwall.collectionUpdated` (Cuebot extra) | `fw.id`, `fw.name`, `fw.slug`, `fw.description`, `fw.available`, `fw.access`, `fw.isPublic`, `fw.productCount`, `fw.productIds` |

Ignored: `CART_ABANDONED_1H/24H/72H` (no streamer use), `PLATFORM_APP_DISCONNECTED` (OAuth apps only),
`MEMBERSHIP_POST_UPSERTED`, `MEMBERSHIP_SERIES_UPSERTED/DELETED`, `MEMBERSHIP_TAG_CREATED/UPDATED/DELETED` and
`MEMBERSHIP_TIER_UPSERTED/DELETED` (in `WebhookConfigurationV1.allowedTypes`, but the spec documents no payload for them).

**Money.** Only money that arrived counts: a `DONATION` whose `status` is not `COMPLETED` (`OPEN`, `ABANDONED`,
`CANCELLED`, `FAILED`; a missing status counts as completed) has `amount` 0 and `isCompleted` false, stays out of the
feed, overlay alerts, top spenders and subathon time, and the Donation trigger skips it unless its status option says
otherwise (`fw.amount` keeps the number Fourthwall sent). An order whose `source.type` is not `ORDER`
(`SAMPLES_ORDER`, `GIVEAWAY_LINKS`, `TWITCH_GIFT_REDEMPTION`) has `amount` 0 and `isPurchase` false: samples are not
shown, a redeemed gift shows as "redeemed a gift" with no amount (the gift already counted on `GIFT_PURCHASE`).
Gift amounts are `subtotal, tax, total, profit, prepaidShipping` (draws: `subtotal, total`); `fw.donation` and
`fw.discount` stay on gifts for Streamer.bot parity and are always 0. A gift purchase is a `merch-latest` alert with
`gift: true`.

**Names.** `username` / `nickname` are optional: a blank one is "Anonymous" with `isAnonymous` true. "Hide supporter
names" (the Alerts guide recommends such a toggle) makes `user` "A supporter" for every supporter event; `fw.username`
keeps the real name.

**Memberships.** `subscription.type` is the membership's state (`ACTIVE`, `CANCELLED`, `SUSPENDED`, `NONE`), not what
happened: `SUBSCRIPTION_CHANGED` with `CANCELLED` is a cancellation. No payload names the tier or what changed, so
Cuebot keeps the last tier and monthly price per member id (fourthwall.json, from webhooks and the API's member list)
for `fw.previous*` and `direction` (`upgrade`, `downgrade`, `change`, `same`, empty when unknown); tier names come from
the API. Renewals send no webhook.

**Open API (optional).** HTTP Basic with the shop's Open API user (Settings → For developers; SUPER ADMIN only) at
`https://api.fourthwall.com/open-api/v1.0` (posts: `/open-api/v1.1/memberships/posts/*`). 100 requests / 10 s per shop;
a 429 (`OPEN_API_TOO_MANY_REQUESTS`, no `Retry-After`) is retried after 2, 4 and 8 s; 401/403 stops periodic calls until
the user connects again. Pages are `page` (0-based) / `size` (at most 100). Used for: `GET /shops/current` (gift link
domain), `GET /memberships/tiers` and `/memberships/members` (every 6 h; emails stay in memory), `GET /products` or
`/collections/{id}/products` (showcase widget, after `PRODUCT_*` / `COLLECTION_UPDATED` and every 30 min),
`GET/POST/PUT /webhooks` (set up / repair with every handled type; the shop's signing secret is not returned and is
still pasted by hand), `GET /webhook-events` (catch-up: events newer than the first connection and the last 24 h are
mapped like deliveries, deduped on `id`, marked `isReplay`, so their overlay alerts are catch-ups (`isCatchUp`); the
event `body` may be the envelope or only its data),
`PUT /streaming/start|end` (Twitch stream online / offline), `GET /gifting/config` (shows who runs draws),
`PUT /gifting/draw/{id}/finish` (entrants from Twitch chat; Fourthwall picks the winners), `POST/GET/PUT /promotions`,
`PUT /products/{id}/availability`, `PUT /collections/{id}/availability`, `GET /order` / `GET /donations` (totals),
`GET/POST /thank-you-contributions`, `POST /giveaway-links`, `POST /open-api/v1.1/memberships/posts/texts|video_embeds`.
OAuth platform apps are not used (client secret in the binary, no documented loopback redirect, manual app review), so
the separate `X-Fourthwall-Hmac-Apps-SHA256` app key is checked with the same shop secret.

## Shopify

| `X-Shopify-Topic` | Event | Notes |
| --- | --- | --- |
| `orders/create` | `shopify.orderCreated` | Also for orders never paid (manual payment, fraud, cancelled): the setup hint recommends Order payment first |
| `orders/paid` | `shopify.orderPaid` | Counted in the merch counter; key `paid:<order id>` |
| `orders/cancelled` | `shopify.orderCancelled` | Takes a counted order back off; key `cancelled:<order id>`; `cancelReason`, `cancelledAt` |
| `refunds/create` | `shopify.refundCreated` | `{id, order_id, note, restock, refund_line_items[{quantity, line_item}], transactions[{kind, status, amount, currency}]}`: amount = successful `refund` transactions (else line subtotals); buyer and order name from the order Cuebot saw; key `refund:<refund id>` |
| `inventory_levels/update` | `shopify.lowStock` / `shopify.soldOut` / `shopify.restocked` | `{inventory_item_id, location_id, available}`; stock is summed over the locations Cuebot has seen; an event only when the total crosses the threshold, 0, or back above 0 |
| `products/update` (create, delete) | none | Re-reads the showcase products (Admin API) |

Others are ignored. `shopify.goalReached` follows the Order Paid that reaches the merch goal (once per counter).

**Headers** (https://shopify.dev/docs/apps/build/webhooks/delivery-structure): `apiVersion` (`X-Shopify-API-Version`),
`shopDomain` (`X-Shopify-Shop-Domain`), `triggeredAt` (`X-Shopify-Triggered-At`). The last-delivery line shows the API
version; a version in its last 3 months of the 12-month window, retired (Shopify then "falls forward" to the oldest
supported one) or newer than this quarter's stable release gets advice to pick this quarter's version, also logged once
per version (`ShopifyApiVersions`, computed from `YYYY-MM`, no network). A Rejected delivery names its
`X-Shopify-Shop-Domain` (each store signs with its own key).

**Dedupe.** `X-Shopify-Webhook-Id` (one per delivery, kept on retries) **and** `<topic>:<X-Shopify-Event-Id>` (shared by
all deliveries of one merchant action, so two admin webhooks for the same topic run once), plus the content keys above
for real (non-test) paid / cancelled / refund events, which also dedupe Admin API catch-up against late deliveries.
The order's second feed line (created, then paid) is suppressed with `isRepeatOrder`; the marker
(`shopify:fed:<order id>`) lives in the shared, saved id store, so it survives a restart. Shopify does not guarantee
ordering: Order Created can follow Order Paid.

**Arguments.** The order is flattened to `shopify.<field>`, `shopify.line_items[i].<field>` etc., plus `shopify.event`
(topic) and `_json`. Common args: `user` (the buyer's first name by default; "Name shown on stream": first name / full
name / Someone), `firstName`, `amount` (`total_price`, shop currency), `message` (empty by default: see privacy),
`orderNote`, `cartMessage` (the `note_attributes` entry named in the options, default "Stream message"), `orderId`,
`orderName`, `orderUrl` (`https://admin.shopify.com/store/<handle>/orders/<id>`), `financialStatus`,
`fulfillmentStatus`, `sourceName`, `discountCodes`, `items`, `itemCount`, `item{i}.name / .quantity / .variant / .sku /
.productId / .price`, `presentmentAmount` / `presentmentCurrency` (`total_price_set.presentment_money`), and after the
tracker: `wasCounted`, `shopifySales*`, `shopifyGoal*`. The name comes from `customer.first_name/last_name` (or the
camelCase `firstName/lastName` of newer shapes), else `billing_address`. Catch-up replays add `isReplay` and
`delaySeconds`; their `merch-latest` alert has `isCatchUp` = true (skipped by the alert box unless "show catch-up
alerts" is on, counted by lists and totals).

**Privacy.** The order note is the checkout's free-text delivery instructions, so it is never `message` unless the
user picks it. "Redact contact details" (default on) removes, at any depth, `email`, `contact_email`, `phone`,
`sms_marketing_phone`, `browser_ip`, `client_details`, `billing_address`, `shipping_address`, `default_address`,
`addresses`, `destination`, `origin_address`, `note`, `order_status_url`, `token`, `cart_token` and `checkout_token`
before flattening; `_json` is then the redacted body, which is also what the Streamer.bot-compatible WebSocket server
forwards for `Shopify.OrderCreated` / `Shopify.OrderPaid`.

Only JSON webhooks are read. A webhook created with the XML format fails with "Shopify webhook is set to XML; choose
JSON in Shopify's webhook settings" (detected from `content-type` or a body starting with `<`). Test deliveries are
exempt from the repeat rule and never count: Shopify's *Send test* always uses the sample order 820982911946154508.

An admin-created webhook whose address keeps answering non-2xx is deleted by Shopify without an email (help center),
so the user guide says to update the URL straight away after a relay route-secret change, and the page warns after 7
days without a verified delivery (time kept in shopify.json). The relay answers 200 at once, well inside Shopify's
5-second limit.

**Optional Admin API** (`ShopifyApi`, GraphQL `2026-10`): a Dev Dashboard app installed on the user's own store, token
from the client credentials grant (`POST https://{shop}/admin/oauth/access_token`, `grant_type=client_credentials`,
24-hour token cached and renewed 5 minutes early or once after a 401), or a pasted `shpat_` token from an older custom
app. Scopes `read_orders`, `read_products`, `read_inventory`. Calls: `shop`, `products(query:"status:active")`,
`inventoryItem(id)` (names for stock events, cached), `orders(query:"financial_status:paid created_at:>'…'")` (catch-up
every N minutes from the first connection, at most 24 h back, and sales totals). GraphQL orders are converted to the
REST webhook shape and run through the same mapping. THROTTLED / 429 is retried after 2, 4, 8 s; 401/403 stops
periodic calls until the user connects again. No public app, OAuth, webhook subscriptions or compliance webhooks.

## Throne

Source: help.throne.com "How do I set up webhook integration?" (updated 2026-07). Each delivery is signed with
Ed25519: `X-Signature-Timestamp` (Unix seconds, decimal) and `X-Signature-Ed25519` (hex, 64 bytes) over the UTF-8
timestamp, `.`, then the exact raw body. Throne's public key (SPKI PEM,
`MCowBQYDK2VwAyEAPXbUfxh7XL4SYUVcfhmYMIbxvtR9E9LDd8gPJ1PwSD8=`) is built in; the .NET 10 BCL has no Ed25519, so
BouncyCastle verifies. Missing or non-numeric timestamps, a signature that is not 64 bytes, a bad signature, or a
timestamp more than 10 minutes from the delivery's arrival are `Rejected`. The key is the same for every Throne
creator (any Throne delivery verifies), so a Cuebot URL token can narrow it to the user's own webhook: when one is
set it must also be in the URL (`…/throne/<token>`, `?token=<token>` or header `X-Cuebot-Token`).

Body: `{ "contract_version": "1", "event_id": "<uuid>", "event_type": "gift_purchased" | "contribution_purchased" |
"gift_crowdfunded", "data": { creator_id, creator_username, gifter_username, message?, item_name,
item_thumbnail_url, price | amount, currency, is_surprise_gift? } }` (`price` in cents on gifts and crowdfunded
gifts, `amount` in cents on contributions, `message` omitted when there is none, `is_surprise_gift` not on
contributions). A `contract_version` other than "1" is logged as a warning and read the same way. Dedupe on
`event_id`.

Arguments: `throne.eventId`, `throne.eventType`, `throne.contractVersion`, `throne.creatorId`,
`throne.creatorLogin`, `throne.gifterLogin`, `throne.itemName`, `throne.itemThumbnailUrl`, `throne.message`,
`throne.isSurpriseGift`, `throne.currency`, `throne.createdAt` (the signed timestamp; the arrival time if absent),
`throne.price` (gifts) / `throne.amount` (contributions) in cents, `item`, `isAnonymous`; `amount` is in major units.
Throne sends `gifter_username: "Anonymous"` for anonymous gifters: `isAnonymous` is true and `user` (the feed) is
"Anonymous".

## Custom hooks

`…/custom/<name>[/more]`: the first path segment selects the hook (case-insensitive); unknown hooks or hooks without
a token are `NotConfigured`, switched-off hooks `Ignored`. Event `webhook.custom` with `hookName`, `method`, `path`,
`query` (without `token`), `query.<key>`, `headers` (JSON object, without `X-Cuebot-Token`), `contentType`, `body`,
and the body's top-level JSON keys (scalars as values, objects/arrays as JSON text) or form fields as arguments of
their own (never overwriting Cuebot's own names). Dedupe on the relay delivery id only.

## Activity feed, speech, credits

| Event | Feed kind |
| --- | --- |
| Ko-fi donation, Fourthwall donation, Throne gift / contribution | Tip (→ credits "Tips") |
| Ko-fi (re)subscription, Patreon pledge created, Patreon gifted / free-trial member, Fourthwall subscription purchased | Subscription (→ credits "Subscriptions") |
| Patreon free member joined (followCreated) | Follow (→ credits "Follows") |
| Ko-fi commission / shop order, Shopify orders, Fourthwall order / gift purchase, Patreon pledge updated, Throne crowdfunded | Other (no purchase kind exists; not in the credits) |
| Shopify order cancelled / refund created | Other, no spend (the spending ledger only adds; the merch counter takes them off) |

## Sources

- Ko-fi webhooks: https://help.ko-fi.com/hc/en-us/articles/360004162298, ko-fi.com/manage/webhooks, the official
  example payload image (help.ko-fi.com/hc/article_attachments/16595546601629); test payload shapes from
  github.com/rainbowdashlabs/reputation-bot (`dev/*.json`)
- Patreon webhooks and API v2: https://docs.patreon.com/ (#webhooks, #triggers-v2, #robust-retries, #member, #campaign-v2,
  #pledge-event, #apiv2-webhook-endpoints, #pagination, changelog, read 2026-11-11); payload shapes from Zulip's Patreon fixtures
  (github.com/zulip/zulip, `zerver/webhooks/patreon/fixtures`) and a captured portal test (patreon-api.ts issue 89)
- Fourthwall: https://docs.fourthwall.com/webhooks/webhook-model, .../signature-verification, OpenAPI `openapi/platform.json` (`webhooks`)
- Shopify (read 2026-11-11): https://shopify.dev/docs/apps/build/webhooks (ordering, reconciliation),
  .../webhooks/delivery-structure (headers), .../webhooks/verify-deliveries (HMAC, 200 within 5 s, 8 retries over 4 h),
  .../webhooks/troubleshoot, https://shopify.dev/docs/api/usage/versioning (quarterly versions, 12 months, fall
  forward), https://help.shopify.com/en/manual/fulfillment/setup/notifications/webhooks (admin-created webhooks: JSON or
  XML, Webhook API version, the store's signing key, deletion after repeated failures),
  https://shopify.dev/docs/apps/build/authentication-authorization/client-credentials-grant,
  https://shopify.dev/docs/api/admin-graphql (shop, products, inventoryItem, orders), the REST webhook sample payloads
  for orders/*, refunds/create and inventory_levels/update; captured test deliveries in
  github.com/hookdeck/webhook-samples (`providers/shopify/2026-07`)
- Throne: https://help.throne.com/en/articles/15935990-how-do-i-set-up-webhook-integration (Ed25519, envelope);
  github.com/WolfwithSword/SubathonManager (`ThroneService.cs`) for the delivered format only
- Streamer.bot trigger docs: https://docs.streamer.bot/api/triggers/integrations/{ko-fi,patreon,shopify,fourthwall,throne}
