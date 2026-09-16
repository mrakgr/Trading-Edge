# IBKR API — paper-trading notes

Working notes on Interactive Brokers' TWS API as seen from this project: setup, the message model,
order lifecycle, data regimes, and what the paper simulator actually does. Everything here was measured
against the paper account unless marked otherwise. Dates are 2026.

Sections: §I1 setup · §I2 message model · §I3 order lifecycle (measured) · §I4 rejections · §I5 what the
paper simulator fills on · §I6 market data regimes · §I7 venues and permissions · §I8 the paper calendar ·
§I9 gotchas · §I10 what a fresh connection can recover · §I11 margin requirements per instrument.

---

## §I1 Setup that works (09-12)

| item | value |
|---|---|
| gateway | IB Gateway, paper mode, started by hand on the Windows host; socket port **4002**; read-only API **off** |
| reach from WSL2 | `127.0.0.1:4002` (mirrored networking). The LAN IP and `10.255.255.254` do not answer. |
| client | `pip install ib_async` (2.1.0), Python; `ib.connect('127.0.0.1', 4002, clientId=N, timeout=20)` |
| server version | 178 |
| paper account | ~1.0M EUR simulated; `BuyingPower` reports ~6.7M (futures leverage) |
| request timeout | set `ib.RequestTimeout = 15`; the default waits forever on a blocked request |

Each script uses its own `clientId`; two clients with the same id are refused. Status codes 2104/2106/2158
(farm connections OK), 2119 (farm connecting) and 10167 (delayed data substituted) are informational.

## §I2 The message model

There is **no request/response pairing.** The API is one persistent TCP socket; the client pushes messages
in and the gateway pushes messages back on its own schedule. Every "call" returns immediately with nothing;
the answer, if any, arrives later as an event keyed by an id the client chose.

- On connect the gateway sends `nextValidId`. Every order carries an integer `orderId` the client assigns,
  strictly increasing from that. `ib_async` allocates them. Ids restart per session; `permId` (in
  `openOrder`) survives reconnects and is what to match on after one.
- `placeOrder(id, contract, order)` returns at once. Later events about the order are: `openOrder`
  (the gateway's echo with `permId`), `orderStatus` (the state machine), `execDetails` (one per execution,
  i.e. per partial fill, with `execId`, side, shares, price, `cumQty`, `avgPrice`), `commissionReport`
  (keyed by `execId`), `error(id, code, text)` (rejections and warnings keyed by the order id; `id = -1`
  for connection-level notices).
- Nothing ever says "done". Completion = `orderStatus.status == "Filled"` with `remaining == 0`, or, more
  robustly, executions summing to the order quantity.
- Nothing is replayed after a disconnect. Recovery is the client's job: `reqExecutions` (today's fills),
  `reqAllOpenOrders` (still working), `reqPositions`, then reconcile against the client's own book.

**Documented guarantees** (legacy TWS API guide, "Order submission", verbatim): *"Typically there are
duplicate orderStatus messages with the same information that will be received by a client. This
corresponds to messages sent back from TWS, the IB server, or the exchange. There are not guaranteed to be
orderStatus callbacks for every change in order status. For example with market orders when the order is
accepted and executes immediately, there commonly will not be any corresponding orderStatus callbacks.
For that reason it is recommended to monitor the IBApi.EWrapper.execDetails function in addition to
IBApi.EWrapper.orderStatus."* ⇒ book fills from executions, dedupe statuses by (orderId, filled).
Out-of-order delivery is NOT documented; treat it as a defensive assumption only.

**`ib_async` on top:** one `Trade` per order with `orderStatus`, `fills`, `log`; events `trade.fillEvent`,
`trade.statusEvent`, `trade.filledEvent` (awaitable), `ib.execDetailsEvent`, `ib.orderStatusEvent`,
`ib.errorEvent`. None of them fire unless the event loop runs (`ib.sleep`, `ib.run`, or `await`).

## §I3 Order lifecycle, measured (09-14, Monday 07:45 UTC, MBTU6, paper)

| order | placed | sequence seen | fill |
|---|---|---|---|
| BUY 1 MKT | 07:45:41.2 | `PendingSubmit` → `Filled` (no `Submitted`) | 77,920 @ 07:45:42, comm 2.01 |
| SELL 1 MKT | 07:46:12.7 | `PendingSubmit` → `Filled` | 77,865 @ 07:46:13, comm 2.01 |
| BUY 1 LMT 77,300 (far below) | 07:47:03 | `PendingSubmit` → `Submitted`, rests | — |
| modify same order to LMT 78,115 | 07:47:23 | `Submitted` → `Filled`; log: Modify, Modified, Fill | 77,860 @ 07:47:24 (at the market, not the limit) |
| SELL 1 LMT 77,960 (above) | 07:47:29 | `Submitted`, rests | — |
| cancel it | 07:47:49 | `PendingCancel` → `Cancelled`; error **202** "Order Canceled" = the confirmation | — |
| SELL 1 MKT | 07:47:50 | `PendingSubmit` → `Filled` | 77,900 @ 07:47:51 |

Round trips: ~1 s from placement to fill event, ~1 s cancel to confirmation. The `PendingSubmit` seen
immediately after `placeOrder` is the client-side placeholder, not a gateway message. The market buy went
straight to `Filled` — the documented skipped-status case in a mild form. Commission on a micro BTC
contract: 2.01 per side (paper).

## §I4 Rejections (measured 09-14)

Test: BUY 1,000 MBTU6 (initial margin ~2.37M EUR) against ~1.0M EUR equity, once as a far limit, once at
market.

| stage | what arrives | where it does NOT arrive |
|---|---|---|
| `placeOrder` returns | immediately, status `PendingSubmit` (placeholder) | no synchronous error |
| +0.35 s | `orderStatus` → **`Inactive`**, filled 0 | `execDetails`: nothing, ever |
| +0.40 s | `error(orderId, 201, "Order rejected - reason: We are unable to accept your order. Your Available Funds are in sufficient to cover the change in the account's margin requirements if this order executes. In order to obtain the desired position your Equity with Loan Value [999754.35 EUR] must exceed the new total Initial Margin of [2365590.72 EUR].")` | |
| `whatIfOrder` on the same size | the numbers come back (init 2,365,590 / maint 2,028,062) AND an error 201 keyed by the what-if's request id with the same reason | |

So: a rejection is `orderStatus = Inactive` plus **error code 201** on the order's id with the reason as
free text (HTML `<br>` inside). Limit and market forms are identical. The order object stays in
`openTrades()` until the client drops it (`isDone()` is true). No position, no execution.
Other codes seen so far: **202** = cancel confirmed (not an error); **10349** = TIF changed to DAY (warning;
pre-empts `whatIfOrder`, which then returns `[]` — always pass `tif="DAY"`); **399** = order will be held
until the session opens (warning, order still accepted); **2109** = `outsideRth` ignored for this
destination (warning).

## §I5 What the paper simulator fills on (09-14) — REAL-TIME, not the delayed feed

The question from 09-12: the free paper account gets CME data 10 min delayed; does the simulator fill on
that delayed quote? **No.** Fills at the four times above vs the delayed MBT quote, the real-time Coinbase
nano-perp (`BIPZ30`) quote, and the true MBTU6 1-min bars fetched 11 min later:

| time (UTC) | fill | delayed MBT bid/ask | Coinbase BIP bid/ask | true MBTU6 minute range (trades) | true bid/ask range |
|---|---|---|---|---|---|
| 07:45:42 BUY | **77,920** | 77,745 / 77,760 | 77,800 / 77,805 | 77,865–77,940 | 77,855–77,950 |
| 07:46:13 SELL | **77,865** | 77,780 / 77,795 | 77,770 / 77,775 | 77,865–77,885 | 77,850–77,905 |
| 07:47:24 BUY | **77,860** | — / 77,815 | 77,760 / 77,770 | 77,860–77,895 | 77,830–77,915 |
| 07:47:50 SELL | **77,900** | 77,825 / — | 77,810 / 77,820 | 77,860–77,895 (07:47), 77,915–77,930 (07:48) | |

Every fill is 45–160 off the delayed quote and inside the real-time minute range (two sit exactly on a
minute's low). The simulator prices on the live MBTU6 book; the delayed stream is only what the client is
allowed to *see*. Consequences:

1. A system deciding on the delayed quote trades 10 min blind on paper. For CME crypto this is a real
   handicap. For **stocks** the paper account can be fed real-time data by subscription, so paper trading
   equities is unaffected — fills will be at the real market.
2. The 09-12 rule "decide on the same stream the simulator fills on" is moot; the simulator fills on the
   market. What stays true: the free real-time Coinbase feed must not be used as a proxy for CME quotes in
   a trading loop (the basis moved 60–115 within these three minutes).

## §I6 Market data regimes (paper, 09-12)

| feed | status |
|---|---|
| CME futures quotes | delayed 10 min for free: `ib.reqMarketDataType(3)`; error 10167 confirms the substitution |
| CME futures history | `reqHistoricalData(... whatToShow="TRADES"|"BID_ASK", useRTH=False)` works, lagged 10 min; ticks too |
| Coinbase Derivatives (`COINDERIV`) | real-time quotes and history, free |
| forex (EUR.USD) | API needs the "Forex/TOP/ALL" subscription (error 10089); not needed |
| error **10197** "No market data during competing live session" on every contract | not a subscription issue; cured by **restarting the gateway** (data-sharing setting was already off) |

## §I7 Venues and permissions (09-12)

- **CME crypto futures are 24/7 since 05-29.** MBT hours string: Sat 02:00–04:00 ET maintenance, otherwise
  continuous. Micro BTC `MBT` (0.1 BTC, tick 5.0 = $0.50), micro ETH `MET`. What-if margin MBTU6: init
  $2,341 / maint $2,007; METU6 init $101.
- **Coinbase Derivatives** = IBKR exchange `COINDERIV` (Coinbase Derivatives LLC, ex-FairX, CFTC DCM,
  cleared at Nodal Clear; not Coinbase International). Nano BTC `BIT` (0.01 BTC, tick $0.05), nano ETH `ET`
  monthlies, plus perpetual-style `BIP`/`ETP` expiring 2030-12-20 (`BIPZ30` conId 805366677, market rule
  380). Continuous Fri 18:00 → Mon 17:00 ET. Live weekend volume (3,444 contracts in 3 h on a Saturday).
  ⚠ **Rejected for this account** at what-if: *"No Trading Permission, Customer Ineligible; No Opening
  Trades: Only available to EU retail clients with accounts under a professional advisor"* (IBIE rule).
- Tradeable set for this account: CME micro BTC/ETH, forex, US equities/futures.

## §I8 The paper calendar (09-12, Saturday)

The simulator does **not** follow the 24/7 CME session. On Saturday: MBT DAY orders sat in `PendingSubmit`
forever (no ack, no warning); GTC far-off → `PreSubmitted` (held); ES/AAPL DAY → `PreSubmitted` with
warning 399 "will not be placed at the exchange until Sun 17:00 CT / Mon 09:30 ET"; every futures cancel
hung in `PendingCancel`; `outsideRth=True` → warning 2109, ignored for futures. All six stuck orders were
gone by Monday 07:44 UTC (no fills, no positions) without any further action. ⇒ paper futures: Sunday
18:00 ET → Friday only, regardless of the exchange.

## §I9 Gotchas

- `whatIfOrder` returns `[]` when a warning pre-empts it (10349) → pass `tif="DAY"`; and it can return the
  margin numbers AND a 201 rejection for the same request.
- `Execution.time` on the LIVE `execDetails` came back **2 h behind the UTC wall clock** (05:45 for a
  07:45 UTC fill) while `ib_async` labels it `+00:00`; the same execution via `reqExecutions` later showed
  the correct 07:45:42 UTC. Stamp fills with the client's own clock at receipt.
- Bid/ask fields flip to `-1` between ticks on the delayed feed (seen mid-update); treat `-1` as "unknown",
  not a price.
- Unfiltered `reqContractDetails(exchange="COINDERIV")` blocks past any timeout; always filter by symbol.
- Process hygiene: kill scripts by PID; `pkill -f <script>` matches its own shell (exit 144).
- Order ids are per-session integers; if two scripts connect with the same `clientId` the second is
  refused; `reqGlobalCancel()` cancels every client's orders.

## §I10 What a fresh connection can recover (09-14)

There is **no replay of the message stream**; errors and status transitions are gone once missed. What a new
client (`clientId` 40, which placed none of the morning's orders) gets back:

| request | returned | caveats |
|---|---|---|
| `reqExecutions(ExecutionFilter())` | all 4 executions of the day, every client, with `execId`, `orderId`, `permId`, `clientId`, price, side, correct UTC time | **commission = 0.0** — the `commissionReport` is not re-sent, only the live one carries it (2.01 live). Day-scoped: the gateway's daily restart clears it; older history = Flex queries on the web, not the API. |
| `reqCompletedOrders(apiOnly=False)` | all 7 orders of the day with `permId`, action, type, final status | **lossy**: filled orders report `totalQuantity 0`, `filled 0`, `avgFillPrice 0.0`; margin-rejected orders show as `Cancelled` (live they were `Inactive`). Use it for the set of orders, not their numbers. |
| `reqAllOpenOrders()` | working orders of every client, incl. manual TWS ones | `reqOpenOrders()` = this client only |
| `reqPositions()` / `positions()` | current positions | |
| `accountSummary()` | equity, margin, available funds | |

`ib_async` issues the executions and completed-orders requests on connect by itself, so `ib.fills()` was
already 4 before any request. **`orderId` repeats across clients** (both client 32 and client 33 had an
order 7); `permId` is the only cross-session key. Reconcile the OMS book on reconnect as: executions by
`execId` (idempotent), open orders by `permId`, positions as the ground truth.

## §I11 Margin requirements per instrument (09-14)

`ContractDetails` carries **no margin field** for any security type. The only programmatic source is
`whatIfOrder(contract, order)`: it returns `initMarginChange` / `maintMarginChange` / `equityWithLoanChange`
/ `commission` for THIS account, size and side — in the account's base currency (EUR here), priced at the
current market (a limit order at 230 and a market order gave identical numbers). Outside the API the same
numbers come from TWS "Check Margin" on an order ticket and the website's stock margin search.

Measured (EUR figures; the what-if prices at the MARKET, and AAPL was at ~330 that morning, not the 230 in the
limit — at 330 the AAPL long comes out at the same **33% / 30%** as the EU large caps below):

| what-if | notional (USD) | init (EUR) | maint (EUR) | note |
|---|---|---|---|---|
| AAPL BUY 100 @ 230 | 23,000 | 9,446 | 8,587 | large cap long |
| AAPL SELL 100 @ 230 | 23,000 | 10,182 | 8,588 | short costs a little more initially |
| MSTR BUY 100 @ 150 | 15,000 | 11,269 | 11,269 | volatile name: ~2× AAPL's rate, init = maint |
| MSTR SELL 100 @ 150 | 15,000 | 22,988 | 16,903 | short = 2× the long |
| GME SELL 500 @ 25 | 12,500 | 18,159 | 9,079 | short init > notional |
| SNDL BUY 5,000 @ 2 | 10,000 | 5,894 | 5,894 | low-priced long |
| SNDL SELL 5,000 @ 2 | 10,000 | 16,609 | 11,615 | low-priced short: 2.8× the long, > notional |
| SPY BUY 100 | — | rejected | | error 201 "No Trading Permission, Customer Ineligible ... This product does not have a KID" — **US ETFs are closed to EU retail (PRIIPs)** |

So: margin is per-name (volatility, price, borrow) and per-side, and the short side of low-priced or
volatile names needs MORE than 100% of notional. For the OMS: size against `initMarginChange` from a
what-if, not against a fixed leverage; the what-if is cheap (~0.3 s) and can run at signal time.

**Intraday vs overnight (09-14, documentation + one measurement, rerun pending).** IBKR LLC (US entity)
accounts get a reduced INTRADAY stock requirement — "generally 25% of the long stock value" during active
market hours, reverting to the Reg T 50% to hold overnight, applied at 15:50–16:00 ET via the SMA check
(IBKR margin webinar notes). **IBIE (our entity) says the intraday margin reduction is not available to
clients classified as Retail Clients**, and its rule is "Initial margin will be 110% of Maintenance Margin"
(IBIE margin pages; the tables are rendered client-side and could not be scraped). Our 04:30 ET what-ifs
match the IBIE rule exactly: AAPL init/maint = 9,446/8,587 = **1.100**, and MSTR/SNDL longs show init =
maint (house requirement already above the 110% floor). A rerun of the same what-ifs after the 09:30 ET
open (`margin_rth.py`) was scheduled for 09-14 13:36 UTC but its output was lost with the session
scratchpad — still to be measured during RTH; expectation: none. ⇒ for an EU retail account, size on ONE requirement all day; there is no cheaper
intraday tier to exploit and no 15:50 step-up to fear.

**European stocks (09-14, EUR vs EUR so the percentages are exact):**

| what-if | notional EUR | init | maint | leverage |
|---|---|---|---|---|
| SAP (IBIS) BUY 100 @ 181.64 | 18,164 | 6,022 (33.2%) | 5,474 (30.1%) | 3.0× |
| ASML (AEB) BUY 100 @ 1,397.60 | 139,760 | 46,061 (33.0%) | 41,874 (30.0%) | 3.0× |
| ZAL (IBIS) BUY 100 @ 22.58 | 2,258 | 748 (33.1%) | 680 (30.1%) | 3.0× |
| SAP SELL 100 | 18,164 | 6,495 (35.8%) | 5,477 (30.2%) | 2.8× |
| ZAL SELL 100 | 2,258 | 807 (35.7%) | 680 (30.1%) | 2.8× |
| SIE, MC, HFG BUY 100 | (no delayed price) | 8,537 / 13,625 / 276 | 7,761 / 12,386 / 251 | same 1.10 ratio |

EU large caps: maintenance **30%**, initial **33%** (= 1.10 × 30%) ⇒ **3.0× long, 2.8× short**, all day.
The account-level `BuyingPower` field (6,676,637 on 1,001,496 net liquidation = **6.67×**, i.e. available
funds / 15%) is IBKR's headline number, not what any stock what-if allows; a specific order is bounded by
that stock's initial requirement. (Error 354 = no EU stock data subscription on paper; 10197 reappeared
during this run — a competing live session was open.)

**09:33 UTC check:** the 10197 during the EU-stock run was transient — a fresh client connected, and a
delayed AAPL snapshot came back (330.35) with no error, while the user's gateway window was showing
"disconnected". So 10197 can also be the gateway's own upstream link dropping and re-authenticating, not
only a competing login. Treat 10197 as "retry after a pause", and log the gateway's connection state.

**Latency, event-driven (09-14 12:55 UTC, MBTU6 paper, gateway on the Windows host, client in WSL):**
market BUY → first message +903 ms, then `execDetails`, `Filled` and `commissionReport` within 3 ms of each
other (one burst); market SELL → +555 ms; far limit acknowledged +558 ms; cancel confirmed +559 ms. The
margin rejections this morning (no exchange leg) came back in +350 ms, so ~350 ms is the gateway↔IBKR
server round trip and the rest is the simulator/exchange leg. TradeZero paper (`tradezero_api.md` §T5):
POST ~240 ms, fill on the row ~300 ms after — roughly 2× faster than IBKR paper from the same desk.
