# TradeZero API — notes

Companion to `ibkr_api.md`. §T1 is a pre-read from the developer portal (2026-09-14, UNTESTED); measured
sections follow once the key is live.

## §T1 What the documentation says (developer.tradezero.com, read 09-14)

**Model:** plain REST over HTTPS plus two beta WebSocket streams. No fee for API access (launched 2026;
changelog runs 2025-12 → 2026-07). Everything keys on a client-chosen `clientOrderId`; the server-side
`orderId` was REMOVED in the 2026-01-14 breaking change.

| item | value |
|---|---|
| base URL | `https://webapi.tradezero.com` |
| auth | static headers on every call: `TZ-API-KEY-ID`, `TZ-API-SECRET-KEY`; no token, no refresh |
| accounts | `GET /v1/api/accounts` → `{accounts:[...]}`; `accountType == "Paper"` marks the simulated one ("never derive it from the id prefix") |
| account | `GET /v1/api/account/{id}` → incl. `bp` (buying power); 200 = key + account aligned |
| routes | `GET /v1/api/accounts/{id}/routes` — venues the account may direct to (e.g. `ARCA`); the `route` you send is echoed verbatim, no substitution |
| place order | `POST /v1/api/accounts/{id}/order` body `{securityType:"Stock", symbol, side:"Buy"|"Sell", openClose:"Open"|"Close", orderQuantity, orderType:"Market"|"Limit"|"Stop"|"StopLimit", limitPrice, timeInForce:"Day", clientOrderId, route?}` → `{clientOrderId, orderStatus, route}` |
| one order | `GET /v1/api/accounts/{id}/order/{clientOrderId}` (since 2026-02-10) → `orderStatus`, `executed`, `priceAvg`; **404 = never seen** (the idempotency probe) |
| all orders | `GET /v1/api/accounts/{id}/orders`; history `GET .../orders/start-date/{date}` with `tradeId, qty, price, commission, totalFees, netProceeds` — **paper accounts have NO order history**, fall back to `/orders` |
| cancel | by `clientOrderId` (DELETE); "cancel all open orders" = one DELETE |
| positions | `GET .../positions` (`positionId`); closed positions with realized P&L endpoint since 2026-07-07 |
| statuses | terminal = `{Filled, Canceled, Rejected, Expired, DoneForDay}`; partial fills via the `executed` count; recipe polls at 1 s |
| WebSocket | `wss://webapi.tradezero.com/stream/portfolio`: server `{status:"PENDING_AUTH"}` → client `{key, secret}` → `{status:"CONNECTED"}` → client `{accountId, subscriptions:["Order","Position"]}`; then `{action:"update", subscription:"Order", order:{userOrderId, clientOrderId, orderStatus, status, text}}` / `{... subscription:"Position", position:{id, symbol, shares}}`; every update is the FULL row; `{status:"FAILED_AUTH"|"TERMINATED"}` ends it. Second stream `/stream/pnl`. **Beta**: schema may change. |
| bootstrap rule | open the socket FIRST and buffer, then `GET /orders` + `GET /positions`, then apply the buffer (else changes during the fetch are lost) |
| `clientOrderId` | ≤ 36 chars (the live exchange truncates longer ids and appends `INVALID{date}`, breaking lookups); recipe format `<prefix>-<symbol>-<hex8>`; never blind-retry a POST that timed out or 5xx'd — GET the id first |
| rejections | orders: "4xx HTTP responses" at submission, plus the `Rejected` terminal status with `text` on the row (shape not documented); locates: the quote POST returns **200 even when rejected**, the rejection sits in `/locates/history` as `locateStatus 56` + `text` (`R56` = ETB, no locate needed; `R06` = restricted; `11` = no inventory; `14` = already pending; `18` = already offered) |
| locates | `POST /v1/api/accounts/locates/quote` → accept/cancel; inventory vs history endpoints (split 2025-12-11); pre-borrow vs single-use; sell-back |
| market data | none in the API; no sandbox beyond the paper `accountType` |

**Contrast with IBKR (`ibkr_api.md` §I2):** request/response instead of one multiplexed socket; the fill
signal is a status row (`executed`, `priceAvg`) not a separate execution stream; idempotency is explicit
(`clientOrderId` + GET-before-retry) instead of `permId` reconciliation; rejections come back synchronously
as HTTP errors; and the locate workflow is first-class (quote → accept → inventory → sell-back). IBKR has no
explicit locate/pre-borrow call in the TWS API — instead it (a) streams availability per name via generic
tick 236 (`shortable` 3/2/1 = easy / locate needed / none, `shortableShares`; measured 09-14: AAPL 186M,
GME 15M, SNDL 10M, MSTR 15M shares, all "easy"), (b) locates AUTOMATICALLY at order acceptance from its
stock-loan pool (warning 404 "Shares for this order are not immediately available for short sale. The
order will be held while we attempt to locate the shares"), and (c) offers pre-borrow only through the
Stock Loan Borrow tool in Account Management, not the API. So IBKR's model is "implicit locate at order
time", TradeZero's is "explicit locate as a priced, cancellable object" — the latter is what a short-side
OMS can reason about (cost known before the entry).

**Open questions to measure:** the exact HTTP rejection body; whether the paper account fills at the real
NBBO and honours limits; whether `executed`/`priceAvg` update per partial; WebSocket latency vs the 1 s
poll; what `routes` the paper account lists; buying power per name (no what-if exists — `bp` is a single
account number).

**Keys and the paper environment (docs/documentation/api-keys, read 09-14):** two separate key pairs.
LIVE keys need "an active live account with at least one platform bundle (ProBundle or FreeBundle) selected
and API Trading enabled" — that is the "fund your account first" wall. PAPER keys are made in the **Paper
Portal** (same URL as live, select the paper account → "API Keys" / "Enable API Trading" → sign the API
Trading agreements → API Key Management → Generate): "No platform bundle selection is required — paper
accounts get every platform by default"; $1,000,000 paper dollars; the paper account lives **30 days
("permanent if linked to a live account")**; after the free month a live account with ≥ $200 keeps it. The
base URL is "the same for paper and live; the key pair selects the environment". One active pair per
account, full permissions; the secret is shown exactly once; "Regenerate Secret" rotates the secret only.

## §T2 First paper round, measured (09-14, 10:18 UTC = 06:18 ET, pre-market, AAPL, route `PAPER`)

Setup: paper keys from the Paper Portal (separate registration at registration.tradezero.com/paper; the
live-key path demands a funded account); credentials in `~/.config/tradezero/credentials` (mode 600).
`GET /v1/api/accounts` → one account, `accountType "Paper"`, id in the **`account`** field (not `accountId`),
$1,000,000, `leverage 1.0`, `bp` = `overnightBp` = 1,000,000, `marginType "M"`. Routes: `PAPER`
(Stock/Option; Market/Limit/Stop/StopLimit/RangeOrder; Day/GTC/GoodTillCrossing) and `PAPERM` (multi-leg).

| step | HTTP | round trip | what happened |
|---|---|---|---|
| BUY 1 LMT 336 (ask ~331) | 200, row `PendingNew` | 817 ms | `Filled` 1 @ **331.50** on the next poll, 300 ms after the POST; `text "TRAFIX_SIM"` — **pre-market fills work** |
| BUY 1 LMT 300 (far) | 200 `PendingNew` | 247 ms | `New`, rests |
| `DELETE .../order/{cid}` | **405** | | wrong path — the cancel path is PLURAL: `DELETE /v1/api/accounts/{id}/orders/{cid}` |
| BUY 100,000 LMT 336 (~$33M vs $1M bp) | **200** `PendingNew` | 917 ms | `Rejected` 200 ms later, `text "R16: Your order exceeds the maximum Qty allowed"` — a size cap fired before any buying-power check |
| re-POST with the SAME clientOrderId as the filled buy | **200** | 238 ms | `Rejected`, `"R114: Invalid duplicate UserOrderId"` — ⚠ and the rejected row **REPLACED the filled order's row**: `GET /order/{cid}` and `/orders` now show the original as `Rejected, executed 0` |
| SELL 1 MKT (flatten) | 200 `PendingNew` | 250 ms | `Rejected`, `"R78: Market orders are not allowed at this time"` (pre-market) |
| SELL 1 LMT 326 | 200 | | `Filled` 1 @ **331.45** within 700 ms |
| `DELETE .../orders/{cid}` on the resting 300 | 200, row `canceledQuantity 1, leavesQuantity 0` | 274 ms | `Canceled` on the next poll; `bp` back from 999,700 to 1,000,000 (a resting limit reserves its notional) |

**Rejections are NOT HTTP 4xx** (contrary to the portal text): every POST returned 200 with a `PendingNew`
row, and the rejection arrived ~200 ms later as `orderStatus "Rejected"` + `text "Rnn: reason"` on the
order row (`canceledQuantity` = the whole size). Same shape as a fill: poll the row (or the WebSocket).
Codes seen: **R16** max quantity, **R78** market orders not allowed now, **R114** duplicate id.

**Fills** are on the order row: `executed`, `priceAvg`, `lastPrice`, `lastQuantity`, `leavesQuantity`,
`lastUpdated`; the position row carries `positionId`, `priceAvg`, `priceOpen`, `priceClose`, `shares`
(a closed position lingers with `shares 0.0`). No per-execution records on paper (no order history).
The 331.50 / 331.45 fills sit on the real pre-market NBBO (IBKR's delayed AAPL was 330.10/330.65 at
09:33 UTC) — paper fills at the real market, like IBKR's. Account totals did NOT update within 3 s of the
round trip (`realized 0.0`, `sharesTraded 0`, `totalCommissions 0.0` after a −$0.05 round trip) — treat the
account object as lagging, the order rows as truth.

**Rules for the broker adapter:** never reuse a `clientOrderId` (a duplicate does not just fail, it
overwrites the original's row — so the OMS must keep its own fill ledger from the rows as they arrive);
GET-before-retry on any uncertain POST; cancel via the plural `orders/{cid}`; market orders only in RTH;
the 100k-share cap exists (find the exact ceiling before sizing large).

## §T3 Post-only and pegged orders (09-14)

**In the API: neither exists as an order type.** The schema validator (HTTP 400, plain text) says
`- at '/orderType': value must be one of 'Limit', 'Market', 'Stop', 'StopLimit'` for `"Pegged"` and
`"PostOnly"` alike. Unknown flags are silently IGNORED, not rejected: `postOnly: true`, `pegOffsetType /
pegDifference`, and `maxDisplayQuantity` all returned 200 and rested as plain `Limit` rows with
`pegOffsetType "Price" / 0`, `maxDisplayQuantity 0` (the trading doc: peg fields are "metadata-only, read
from order responses"; `maxDisplayQuantity` "reserved"). `timeInForce "Day_Plus"` IS honoured (extended
hours 04:00–20:00 ET; `GTC_Plus` likewise). ZeroPro the platform lists `Pegged` in its order-type menu
(manual §37) — the API does not expose it.

**What exists instead: the ROUTE carries the passivity.** From the fee schedule (Select Routes, $25,000
minimum balance; direct routing $25,000 too):

| route | commission | behaviour | hours ET |
|---|---|---|---|
| ARCA / BATS / EDGX / NSDQ (direct) | add **−$0.0005** rebate, remove $0.0045, route-out $0.0049 | plain limit at the venue; no rebate under $1 or 04:00–07:00 ET ($0.005 applies instead) | 04:00–20:00 |
| BATY / IEX / NQBX / EDGA (inverted) | add $0.0045, **remove −$0.0005** | taker-rebate venues | |
| **SPOST** | **free** | "A passive strategy that floats on best bid or best offer. Orders are pegged to the best bid or best offer and spread out over many major exchanges. Will not exceed limit price." | 09:30–16:00 |
| MID | $0.005 | midpoint of the spread or better, never beyond the limit | 04:00–20:00 |
| FAN-A/-N/-P | $0.005 | smart router, aggressive / neutral / passive | 07:00–20:00 |
| SPDRX / DAGR | $0.005 | sweep & post lit / dark | 09:30–16:00 |
| SDUSK | $0.005 | sweep midpoint then post at the lit exchange with most liquidity | 04:00–20:00 |
| CATCH / POV-10/20/30 | $0.005 | arrival-price and participation algos | 09:30–16:00 |

So the "never cross the spread" tool is **`route: "SPOST"` on a Limit** — a broker-side peg to the
near touch, capped by the limit, free of commission — and the rebate tool is a direct `ARCA`/`EDGX`/`NSDQ`
limit resting inside the spread. Neither is a true exchange post-only (an SPOST peg that becomes
marketable is capped by the limit, not cancelled), so the OMS's own guard stays: a resting entry's limit
must sit at or behind the touch at placement. The paper account exposes only `PAPER`; whether `SPOST`
appears in a live account's `GET /routes` is to be confirmed once funded (the recipe names ARCA, CTDL,
SMART, SMARTO, SMARTM as live examples).

## §T4 Locates: cost model (09-14, docs + a paper probe)

**Quoting is free; only ACCEPTING is charged.** Flow: `GET .../is-easy-to-borrow/symbol/{sym}` → if true, short
directly (no locate). Else `POST /v1/api/accounts/locates/quote {account, symbol, quantity, quoteReqID}` →
`{locateQuoteSent:"true"}` (always 200); the outcome lands asynchronously in `GET .../locates/history` as a row
with `locateStatus` 54 pending / **65 offered** / 56 rejected / 67 expired / 50 filled, `locatePrice` (per
share), `locateType` 3 = Pre-Borrow (reusable across short/cover cycles all day) / 4 = Single-Use (cheaper,
consumed on the first cover). An offer must be accepted within **30 s** (`POST .../locates/accept {accountId,
quoteReqID}`), else it expires unpaid. Accepted shares sit in `GET .../locates/inventory` (`available`, `sold`,
`toBeSold`, `unavailable`); size the short ≤ `available`. Unused shares can be **sold back** (`POST /locates/sell`,
"Credit" in the platform) until 20:00 ET: they are re-offered to other clients and you recover **up to 50%** of
the fee IF someone buys them; Single-Use rows only if never used. Locates are day-only. Decision rule from the
recipe: Pre-Borrow if `pb_price ≤ su_price × expected_cycles`.

**Paper probe:** ETB works on paper (AAPL/SNDL/MSTR true); a GME quote came back in the history within 4 s as
`locateStatus 56, locateError 2, text "2 Easy to borrow.", locatePrice 0.0` — the locate machinery runs on
paper, at least for the reject path. ⚠ **HTTP 429 on the second of four back-to-back ETB calls** — there IS a
rate limit and it is tight and undocumented; the adapter needs a throttle + 429 retry from day one.

## §T5 Latency (09-14, from WSL in Europe)

The WebSocket streams (`/stream/portfolio`, `/stream/pnl`) are READ-ONLY; orders go through the REST POST.
Measured: TCP connect to `webapi.tradezero.com` (an Akamai edge, 104.83.x.x) **28 ms**; `GET /account` on a
fresh connection 232–347 ms; on a keep-alive session **142–254 ms, median 195 ms**; order POSTs earlier
238–917 ms (the first call of a process pays TLS). Fill arrived on the row ~300 ms after the POST. ⇒ the
cost is the API server's own processing behind the edge, not transport; a WebSocket order path would not
remove it. For 1 s-bar mean reversion with minute-scale holds this is immaterial; the adapter should keep
ONE HTTP session alive (HTTP/1.1 keep-alive or HTTP/2), take fills from the WebSocket rather than polling
(polling also burns the tight rate limit — 429 seen on 4 quick calls), and budget ~0.5 s from decision to
confirmed fill.

**On-open / on-close orders (09-14, 08:42 ET on paper):** MOO/LOO are a TIME IN FORCE, not an order type:
`Market` or `Limit` + `timeInForce "AtTheOpening"`, submittable 04:00–09:25 ET, executes in the opening
auction. Paper accepted a `Limit 300 AtTheOpening` (`New`, cancellable) although the PAPER route's
advertised TIF list omits it; `GoodTillCrossing` rested too; `ImmediateOrCancel` at a non-marketable price
came back `Canceled` at once (IOC works). MOC/LOC are NOT placeable via the API (`MarketOnClose` /
`LimitOnClose` appear in `GET /routes` as metadata only) — the 15:58 flatten stays a crossing limit.
Other TIFs per the trading doc: `Day_Plus` / `GTC_Plus` (extended hours 04:00–20:00), `FillOrKill`.
