# Webhook services: Ko-fi, Patreon, Fourthwall, Shopify, Throne, custom hooks

Everything here comes from each service's public documentation (sources at the end) and Streamer.bot's public
trigger docs (trigger names and argument names). None of it has been tried against the real services yet.

Deliveries reach Cuebot through the relay (`https://<relay>/hook/<relay secret>/<route>[/<path>]`), which forwards
the raw request without checking it. **Each handler verifies the delivery itself** with a secret stored in
`webhooks.json`. A delivery that fails verification is `Rejected` and never publishes events; one that arrives
before the secret is entered is `NotConfigured`. A switched-off service verifies, then returns `Ignored`.
Secrets are compared in constant time (`CryptographicOperations.FixedTimeEquals`), masked on the page and never
logged.

Every event carries `service` (display name), `user`, `amount` (major units, a double), `currency`,
`formattedAmount`, `message` and `isTest`, plus the service-specific arguments below (Streamer.bot's names).

**Dedupe.** Each event is remembered (last 1000) by the service's own id where there is one and always by the relay
delivery id, so a service retry and a relay replay both publish once. Local test events are never deduplicated.

**Test events.** Each service's *Send test event* (and the `webhooks.sendTestEvent(service, kind)` script function)
publishes a built-in sample locally, marked `isTest`, without verification. Fourthwall's own `testMode` deliveries
are also marked `isTest`.

| Route | Verification | What the user enters |
| --- | --- | --- |
| `kofi` | Form field `data` holds JSON; its `verification_token` must equal the saved token | Ko-fi → Settings → API → Webhooks → Advanced → *Verification Token* |
| `patreon` | `X-Patreon-Signature` = hex HMAC-MD5 of the raw body, keyed with the webhook secret | patreon.com/portal → My Webhooks → the webhook's *secret* |
| `fourthwall` | `X-Fourthwall-Hmac-SHA256` (or `X-Fourthwall-Hmac-Apps-SHA256`) = base64 HMAC-SHA256 of the raw body | Settings → For developers → Webhooks → *webhook secret key* |
| `shopify` | `X-Shopify-Hmac-Sha256` = base64 HMAC-SHA256 of the raw body | Admin-created webhooks: the key under "Your webhooks will be signed with"; app webhooks: the app's client secret |
| `throne` | No signature exists, so a Cuebot-generated token must be in the URL: `…/throne/<token>`, `?token=<token>` or header `X-Cuebot-Token` | *Generate token* in Cuebot, then paste the relay URL + `/<token>` into Throne → Settings → Integrations → Webhooks |
| `custom/<name>` | The named hook's token as `?token=` or `X-Cuebot-Token` | *Add hook* in Cuebot (token generated) |

## Ko-fi

`application/x-www-form-urlencoded`, `data=<json>` (a raw JSON body is accepted too). Dedupe: `message_id`
(and `type:kofi_transaction_id`).

| Ko-fi `type` | Event | Arguments |
| --- | --- | --- |
| `Donation` | `kofi.donation` | `amount`, `currency`, `from`, `isPublic`, `message`, `messageId`, `timestamp`, `url`, `transactionId`, `kofiType` |
| `Subscription` with `is_first_subscription_payment` | `kofi.subscription` | + `tier` (`tier_name`), `isFirstPayment` |
| `Subscription` (later payments) | `kofi.resubscription` | same |
| `Commission` | `kofi.commission` | as donation |
| `Shop Order` | `kofi.shopOrder` | + `itemCount`, `item0..n` (variation name or link code), `item<n>.code`, `item<n>.quantity` |

Ko-fi asks that supports with `is_public: false` are not shown publicly: the activity feed (and so event speech and
credits) shows "Someone" and no message for them; the trigger arguments keep the real values.

## Patreon

`X-Patreon-Event` names the event. The body is JSON:API: `data` is the member, `included` holds the user, tiers and
campaign. Patreon sends no event id, so dedupe uses event name + SHA-256 of the body.

| `X-Patreon-Event` | Event |
| --- | --- |
| `members:create` (only when not an active patron; paying patrons also get `members:pledge:create`) | `patreon.followCreated` |
| `members:delete` | `patreon.followDeleted` |
| `members:pledge:create` / `:update` / `:delete` | `patreon.pledgeCreated` / `pledgeUpdated` / `pledgeDeleted` |
| `members:update` | `patreon.memberUpdated` (Cuebot extra) |
| `posts:publish` | `patreon.postPublished` (Cuebot extra; `title`, `url`) |

Arguments follow Streamer.bot: `id`, `eventType`, `attributes.<member attribute>` (`patron_status`, `full_name`,
`email`, `currently_entitled_amount_cents`, `last_charge_status`, `note`, ...), `user.id`, `user.type`,
`user.Attributes.<user attribute>`, `entitledTiers[i].id/type/Attributes.<tier attribute>`, plus `tier` (titles),
`patronStatus`, and `amount` = entitled cents / 100. Patreon does not name the currency in the member resource, so
`currency` is empty.

## Fourthwall

Envelope `{ testMode, id, webhookId, shopId, type, apiVersion, createdAt, data }`; dedupe on `id`. Arguments use
Streamer.bot's `fw.*` names (`fw.statmessageus` is kept as documented, `fw.message` is the same text).

| `type` | Event | Main arguments |
| --- | --- | --- |
| `ORDER_PLACED` / `ORDER_UPDATED` (`data.order`) | `fourthwall.orderPlaced` / `orderUpdated` | `fw.orderId`, `fw.friendly`, `fw.checkoutId`, `fw.status`, `fw.email`, `fw.username`, `fw.subtotal/shipping/tax/donation/discount/total`, `fw.currency`, `fw.source`, `fw.variants[i].id/name/sku/quantity/unitPrice/currencyCode/image/attributes.*`, `fw.updateType` |
| `DONATION` | `fourthwall.donation` | `fw.donationId`, `fw.amount`, `fw.currency`, `fw.status`, `fw.username`, `fw.email`, `fw.message` |
| `GIFT_PURCHASE` | `fourthwall.giftPurchase` | `fw.Id`, `fw.offer.*`, totals, `fw.quantity`, `fw.gifts[i].id/status/winner/...` |
| `GIFT_DRAW_STARTED` / `_ENDED` | `fourthwall.giftDrawStarted` / `giftDrawEnded` | as gift + `fw.drawId`, `fw.giveawayId`, `fw.durationSeconds` |
| `SUBSCRIPTION_PURCHASED` / `_CHANGED` / `_EXPIRED` | `fourthwall.subscription*` | `fw.id`, `fw.nickname`, `fw.email`, `fw.type`, `fw.interval`, `fw.tierId`, `fw.amount`, `fw.currency` |
| `NEWSLETTER_SUBSCRIBED` | `fourthwall.newsletterSubscribed` | `fw.email` |
| `THANK_YOU_SENT` | `fourthwall.thankYouSent` | `fw.id`, `fw.mediaUrl`, `fw.contributionType/Id`, `fw.username`, `fw.message` |
| `PRODUCT_CREATED` / `_UPDATED` | `fourthwall.productCreated` / `productUpdated` | `fw.id`, `fw.name`, `fw.slug`, `fw.type`, `fw.state`, `fw.variants[i].*` |

Other types (promotions, carts, collections, app disconnected) are ignored.

## Shopify

`X-Shopify-Topic` `orders/create` → `shopify.orderCreated`, `orders/paid` → `shopify.orderPaid`; others ignored.
Dedupe on `X-Shopify-Event-Id` (else `X-Shopify-Webhook-Id`). The order is flattened to `shopify.<field>`,
`shopify.line_items[i].<field>` etc., plus `shopify.event` (topic), `_json` (raw body), `orderName`, `itemCount`,
`items`; `user` is the customer's name. An order's second event (created, then paid) is not shown in the feed again.
Shopify test orders (`"test": true`) are marked `isTest`.

## Throne

Throne documents no public webhook schema or signing. Known from Throne's feedback board and Streamer.bot's trigger
docs: event types `gift_purchased`, `contribution_purchased`, `gift_crowdfunded`; fields `creator_id`,
`creator_username`, `gifter_username`, `message`, `item_name`, `item_thumbnail_url`, `is_surprise_gift`, `amount` /
`price` (cents), `currency`. Cuebot reads the type from `type`/`event`/`event_type`, the fields from `data`/`payload`
or the top level, snake_case or camelCase. Arguments: `throne.creatorId`, `throne.creatorLogin`,
`throne.gifterLogin`, `throne.itemName`, `throne.itemThumbnailUrl`, `throne.message`, `throne.isSurpriseGift`,
`throne.currency`, `throne.createdAt`, `throne.price` (gifts) / `throne.amount` (contributions), in cents; `amount`
is in major units. Dedupe on an `id` field if present, else SHA-256 of the body.

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
| Ko-fi (re)subscription, Patreon pledge created, Fourthwall subscription purchased | Subscription (→ credits "Subscriptions") |
| Patreon follow created | Follow (→ credits "Follows") |
| Ko-fi commission / shop order, Shopify orders, Fourthwall order / gift purchase, Patreon pledge updated, Throne crowdfunded | Other (no purchase kind exists; not in the credits) |

## Sources

- Ko-fi webhooks: https://help.ko-fi.com/hc/en-us/articles/360004162298 and ko-fi.com/manage/webhooks
- Patreon webhooks: https://docs.patreon.com/#webhooks
- Fourthwall: https://docs.fourthwall.com/webhooks/webhook-model, .../signature-verification, OpenAPI `openapi/platform.json` (`webhooks`)
- Shopify: https://shopify.dev/docs/apps/build/webhooks/subscribe/https (HMAC verification), order webhook payloads
- Throne: https://throne.userjot.com/board/p/webhook-integration-enrichment, Streamer.bot Throne guide
- Streamer.bot trigger docs: https://docs.streamer.bot/api/triggers/integrations/{ko-fi,patreon,shopify,fourthwall,throne}
