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
button are marked `isTest` where the service says so (Fourthwall `testMode`, Shopify `X-Shopify-Test` / `"test"`,
Ko-fi's placeholder transaction id); Patreon and Throne test sends carry no test flag.

| Route | Verification | What the user enters |
| --- | --- | --- |
| `kofi` | Form field `data` holds JSON; its `verification_token` must equal the saved token | https://ko-fi.com/manage/webhooks (Settings → API → Webhooks) → Advanced → *Verification Token* |
| `patreon` | `X-Patreon-Signature` = hex HMAC-MD5 of the raw body, keyed with the webhook secret | patreon.com/portal → My Webhooks → the webhook's *secret* |
| `fourthwall` | `X-Fourthwall-Hmac-SHA256` and/or `X-Fourthwall-Hmac-Apps-SHA256` = base64 HMAC-SHA256 of the raw body; accepted when either verifies | Settings → For developers → Webhooks → *webhook secret key* |
| `shopify` | `X-Shopify-Hmac-Sha256` = base64 HMAC-SHA256 of the raw body | Admin-created webhooks: the key under "Your webhooks will be signed with"; app webhooks: the app's client secret |
| `throne` | Ed25519: `X-Signature-Ed25519` (hex) over `X-Signature-Timestamp + "." + raw body`, Throne's public key; plus the optional Cuebot URL token when one is set | Nothing required. Optional: *Generate token* in Cuebot and add `/<token>` to the URL in Throne |
| `custom/<name>` | The named hook's token as `?token=` or `X-Cuebot-Token` | *Add hook* in Cuebot (token generated) |

**How to send a real test** (through the relay, verified like a real delivery):

- Ko-fi: ko-fi.com/manage/webhooks → the test buttons (donation, subscription, commission, shop order); all reuse one transaction id.
- Patreon: patreon.com/portal → My Webhooks → *Send Test* next to each trigger (no test flag; `posts:publish` cannot be test-sent).
- Shopify: Settings → Notifications → Webhooks → ⋯ next to the webhook → *Send test*.
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
memberships → `subscriber-latest` with `amount` = `months`, `tier` "1000", `tierName`, `price`.

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
unticked. They still update the member list and totals.

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

Envelope `{ testMode, id, webhookId, shopId, type, apiVersion, createdAt, data }`; dedupe on `id`. Arguments use
Streamer.bot's `fw.*` names (`fw.statmessageus` is kept as documented, `fw.message` is the same text). User text
may arrive HTML-escaped (`&#39;`): `user` and `message` (the feed and speech) are HTML-decoded, the `fw.*` arguments
keep the raw text (low confidence that this is needed; harmless when the text is not escaped).

| `type` | Event | Main arguments |
| --- | --- | --- |
| `ORDER_PLACED` / `ORDER_UPDATED` (`data.order`) | `fourthwall.orderPlaced` / `orderUpdated` | `fw.orderId`, `fw.friendly`, `fw.checkoutId`, `fw.status`, `fw.email`, `fw.username`, `fw.subtotal/shipping/tax/donation/discount/total`, `fw.currency`, `fw.source`, `fw.variants[i].id/name/sku/quantity/unitPrice/currencyCode/image/attributes.*`, `fw.updateType` |
| `DONATION` | `fourthwall.donation` | `fw.donationId`, `fw.amount`, `fw.currency`, `fw.status`, `fw.username`, `fw.email`, `fw.message` |
| `GIFT_PURCHASE` | `fourthwall.giftPurchase` | `fw.Id`, `fw.offer.*`, totals, `fw.quantity`, `fw.gifts[i].id/status/winner/...` |
| `GIFT_DRAW_STARTED` / `_ENDED` | `fourthwall.giftDrawStarted` / `giftDrawEnded` | as gift + `fw.drawId`, `fw.giveawayId`, `fw.durationSeconds`; `fw.quantity` = number of gifts (draws send no quantity) |
| `SUBSCRIPTION_PURCHASED` / `_CHANGED` / `_EXPIRED` | `fourthwall.subscription*` | `fw.id`, `fw.nickname`, `fw.email`, `fw.type`, `fw.interval`, `fw.tierId`, `fw.amount`, `fw.currency` |
| `NEWSLETTER_SUBSCRIBED` | `fourthwall.newsletterSubscribed` | `fw.email` |
| `THANK_YOU_SENT` | `fourthwall.thankYouSent` | `fw.id`, `fw.mediaUrl`, `fw.contributionType/Id`, `fw.username`, `fw.message` |
| `PRODUCT_CREATED` / `_UPDATED` | `fourthwall.productCreated` / `productUpdated` | `fw.id`, `fw.name`, `fw.slug`, `fw.type`, `fw.state`, `fw.variants[i].*` |

Other types (promotions, carts, collections, app disconnected) are ignored.

## Shopify

`X-Shopify-Topic` `orders/create` → `shopify.orderCreated`, `orders/paid` → `shopify.orderPaid`; others ignored.
Dedupe on `X-Shopify-Webhook-Id` (one per delivery, kept on retries: Shopify's documented dedupe key), else
`<topic>:<X-Shopify-Event-Id>`. The order is flattened to `shopify.<field>`, `shopify.line_items[i].<field>` etc.,
plus `shopify.event` (topic), `_json` (raw body), `orderName`, `itemCount`, `items`, `presentmentAmount` /
`presentmentCurrency` (`total_price_set.presentment_money`, what the buyer paid in their currency); `user` is the
customer's name. An order's second event (created, then paid) is not shown in the feed again (`isRepeatOrder`).
Test deliveries (`X-Shopify-Test: true` or `"test": true`) are marked `isTest` and exempt from that rule: Shopify's
*Send test* always uses the sample order 820982911946154508, so every test still shows.

Only JSON webhooks are read. A webhook created with the XML format fails with "Shopify webhook is set to XML; choose
JSON in Shopify's webhook settings" (detected from `content-type` or a body starting with `<`).

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

## Sources

- Ko-fi webhooks: https://help.ko-fi.com/hc/en-us/articles/360004162298, ko-fi.com/manage/webhooks, the official
  example payload image (help.ko-fi.com/hc/article_attachments/16595546601629); test payload shapes from
  github.com/rainbowdashlabs/reputation-bot (`dev/*.json`)
- Patreon webhooks and API v2: https://docs.patreon.com/ (#webhooks, #triggers-v2, #robust-retries, #member, #campaign-v2,
  #pledge-event, #apiv2-webhook-endpoints, #pagination, changelog, read 2026-11-11); payload shapes from Zulip's Patreon fixtures
  (github.com/zulip/zulip, `zerver/webhooks/patreon/fixtures`) and a captured portal test (patreon-api.ts issue 89)
- Fourthwall: https://docs.fourthwall.com/webhooks/webhook-model, .../signature-verification, OpenAPI `openapi/platform.json` (`webhooks`)
- Shopify: https://shopify.dev/docs/apps/build/webhooks (HMAC verification, `X-Shopify-Webhook-Id` dedupe, test
  deliveries), captured test deliveries in github.com/hookdeck/webhook-samples (`providers/shopify/2026-07`)
- Throne: https://help.throne.com/en/articles/15935990-how-do-i-set-up-webhook-integration (Ed25519, envelope);
  github.com/WolfwithSword/SubathonManager (`ThroneService.cs`) for the delivered format only
- Streamer.bot trigger docs: https://docs.streamer.bot/api/triggers/integrations/{ko-fi,patreon,shopify,fourthwall,throne}
