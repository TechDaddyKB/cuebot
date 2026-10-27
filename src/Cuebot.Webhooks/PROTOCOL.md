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

**Dedupe.** Each event is remembered (last 1000) by the service's own id where there is one and always by the relay
delivery id, so a service retry and a relay replay both publish once. Blank ids are ignored (a delivery without an id
never looks like a duplicate of another one without an id). Local test events are never deduplicated.

**Test events.** Each service's *Send test event* (and the `webhooks.sendTestEvent(service, kind)` script function)
publishes a built-in sample locally, marked `isTest`, without verification. Deliveries sent by a service's own test
button are marked `isTest` where the service says so (Fourthwall `testMode`, Shopify `X-Shopify-Test` / `"test"`,
Ko-fi's placeholder transaction id); Patreon and Throne test sends carry no test flag.

| Route | Verification | What the user enters |
| --- | --- | --- |
| `kofi` | Form field `data` holds JSON; its `verification_token` must equal the saved token | Ko-fi → Settings → API → Webhooks → Advanced → *Verification Token* |
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

`application/x-www-form-urlencoded`, `data=<json>` (a raw JSON body is accepted too). Dedupe on `message_id` only:
Ko-fi's retries reuse it. Ko-fi's test buttons all send the placeholder `kofi_transaction_id`
`00000000-1111-2222-3333-444444444444` (with a fresh `message_id` each time), so deliveries with that transaction id
are marked `isTest` and every test click is published.

| Ko-fi `type` | Event | Arguments |
| --- | --- | --- |
| `Donation` | `kofi.donation` | `amount`, `currency`, `from`, `isPublic`, `message`, `messageId`, `timestamp`, `url`, `transactionId`, `kofiType`, `discordUsername` / `discordUserId` (when Ko-fi sends `discord_username` / `discord_userid`) |
| `Subscription` with `is_first_subscription_payment` | `kofi.subscription` | + `tier` (`tier_name`), `isFirstPayment` |
| `Subscription` (later payments) | `kofi.resubscription` | same |
| `Commission` | `kofi.commission` | as donation |
| `Shop Order` | `kofi.shopOrder` | + `itemCount`, `item0..n` (variation name or link code), `item<n>.code`, `item<n>.quantity` (1 when missing) |

Ko-fi asks that supports with `is_public: false` are not shown publicly: the activity feed (and so event speech and
credits) shows "Someone" and no message for them; the trigger arguments keep the real values.

## Patreon

`X-Patreon-Event` names the event. The body is JSON:API: `data` is the member, `included` holds the user, tiers and
campaign. Patreon sends no event id, so dedupe uses event name + SHA-256 of the body. The v1 `pledges:*` triggers
were retired on 2026-10-07; only the v2 `members:*` / `posts:*` triggers below exist.

| `X-Patreon-Event` | Event |
| --- | --- |
| `members:create` for a free member, or a gifted / free-trial member (`is_gifted` / `is_free_trial`: active patron with no pledge event) | `patreon.followCreated` (Streamer.bot's mapping; the feed shows gifted / trial members as a membership) |
| `members:create` for any other active patron (it also gets `members:pledge:create`) | ignored |
| `members:delete` | `patreon.followDeleted` |
| `members:pledge:create` / `:update` / `:delete` | `patreon.pledgeCreated` / `pledgeUpdated` / `pledgeDeleted` |
| `members:update` | `patreon.memberUpdated` (Cuebot extra) |
| `posts:publish` | `patreon.postPublished` (Cuebot extra; `title`, `url` made absolute with `https://www.patreon.com`) |

Arguments follow Streamer.bot: `id`, `eventType`, `attributes.<member attribute>` (`patron_status`, `full_name`,
`email`, `currently_entitled_amount_cents`, `last_charge_status`, `note`, ...), `user.id`, `user.type`,
`user.Attributes.<user attribute>`, `entitledTiers[i].id/type/Attributes.<tier attribute>`, plus `tier` (titles),
`patronStatus`, `isGifted`, `isFreeTrial`, `note`, and `amount` = entitled cents / 100. `attributes.note` is the
creator's private note about the member, so it is never the `message` (which is empty for members). `tier` leaves out
$0 tiers when a paid tier is also entitled (members of a paid tier usually hold the Free tier too); tiers referenced
but missing from `included` keep their `entitledTiers[i].id` and add no title. Patreon does not name the currency in
the member resource, so `currency` is empty.

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
| Patreon follow created | Follow (→ credits "Follows") |
| Ko-fi commission / shop order, Shopify orders, Fourthwall order / gift purchase, Patreon pledge updated, Throne crowdfunded | Other (no purchase kind exists; not in the credits) |

## Sources

- Ko-fi webhooks: https://help.ko-fi.com/hc/en-us/articles/360004162298, ko-fi.com/manage/webhooks, the official
  example payload image (help.ko-fi.com/hc/article_attachments/16595546601629); test payload shapes from
  github.com/rainbowdashlabs/reputation-bot (`dev/*.json`)
- Patreon webhooks: https://docs.patreon.com/#webhooks; payload shapes from Zulip's Patreon fixtures
  (github.com/zulip/zulip, `zerver/webhooks/patreon/fixtures`) and a captured portal test (patreon-api.ts issue 89)
- Fourthwall: https://docs.fourthwall.com/webhooks/webhook-model, .../signature-verification, OpenAPI `openapi/platform.json` (`webhooks`)
- Shopify: https://shopify.dev/docs/apps/build/webhooks (HMAC verification, `X-Shopify-Webhook-Id` dedupe, test
  deliveries), captured test deliveries in github.com/hookdeck/webhook-samples (`providers/shopify/2026-07`)
- Throne: https://help.throne.com/en/articles/15935990-how-do-i-set-up-webhook-integration (Ed25519, envelope);
  github.com/WolfwithSword/SubathonManager (`ThroneService.cs`) for the delivered format only
- Streamer.bot trigger docs: https://docs.streamer.bot/api/triggers/integrations/{ko-fi,patreon,shopify,fourthwall,throne}
