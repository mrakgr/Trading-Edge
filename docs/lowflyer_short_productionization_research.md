# LowFlyer SHORT — Productionization Research (Brokers, APIs, Data, Codebase Scope)

**Date:** 2026-07-04. **Status:** RESEARCH ONLY — no code written, no build started. This document
consolidates a multi-agent web-research sweep + a codebase-scope exploration, gathered to decide how to
take the LowFlyer SHORT (pop-fade) book live **without watching screens** — a scanner fires signals and
software places the short. Captured so it survives across sessions.

> **Sourcing caveat (applies throughout):** broker sites (cobratrading.com, centerpointsecurities.com,
> IBKR campus, Elite Trader) frequently 403 automated fetches, and Reddit was unreachable. Many
> community/fee claims are therefore search-snippet / editorial-grade, not primary-fetched — flagged
> inline. Per-share locate cost cards are "call the desk" everywhere. **Verify decision-grade numbers in
> a browser before committing.** Dated primary facts (API launches, FINRA rule changes, doc URLs) are
> high-confidence.

---

## 0. TL;DR / decision

- **The system to productionize:** LowFlyer SHORT, gate `bar_rvol_20d ≥ 100` + ATR%≥0.03 (raw PF 6.65 /
  2,760 trips / 88.7% win / +17.3% avg — see `docs/lowflyer_short_results.md`). Best book in the stack.
- **Broker (v0): TradeZero America.** Only broker combining self-serve HTB locates with a **free,
  official, documented REST/WebSocket API** that treats **short locates as a first-class programmatic
  feature** (quote→accept→sell-back). US FINRA/SIPC. Launched **2026-05-22**.
- **Data: bring your own (Massive).** The TradeZero API **serves no market data by design** →
  execution-only, nothing to decline, no broker data line to pay. Exactly the goal.
- **The one real risk = borrow inventory depth.** Community consensus is unanimous that CenterPoint /
  Cobra borrow *deeper* on the hardest low-float names than TradeZero. Mitigation: keep the execution
  layer behind a **broker-agnostic adapter** and, before committing, run a **live locate-coverage probe**
  on real `brv20d≥100` signal names (the locate leg cannot be paper-tested).
- **PDT is gone (2026-06-04),** so the offshore/Bahamas angle is moot — use US entities.

---

## 1. Architecture already implied (Massive = signals, broker = execution)

The long-side live work (`docs/highflyer_v2_results.md` Run 29, `scripts/equity/live_scan.py`) already
established: **Massive is a Polygon-shaped DATA vendor** ("Massive Advanced ~$199/mo real-time"), and the
broker does **execution only**. The short inherits this split. The user's "avoid data fees" goal maps to:
*can each broker run execution-only on an external feed?* Answer below (§4) — **yes on API-native brokers.**

---

## 2. Broker comparison — automated low-float shorting

The binding constraint is **programmatic LOCATES** (requesting/accepting an HTB borrow over an API), not
order entry (which is trivial everywhere). Ranked for that axis:

| Broker | API | Programmatic **locate**? | Low-float borrow | Data model | Min / regs | Verdict |
|---|---|---|---|---|---|---|
| **TradeZero America** | Official **REST + WebSocket** (2026-05-22), **free** | **YES — first-class** (quote→accept→sell-back credit; `is-easy-to-borrow` precheck) | Strong self-serve (~14 sources); NOT deepest on hardest names | **Serves none** → BYO feed | ~$2.5k; FINRA/SIPC | **★ v0 pick** |
| **CenterPoint** (Clear Street) | Clear Street REST (**ATLAS** locate platform) + DAS/FIX | ATLAS = REST+FIX locates (institutional stack); retail exposure = sales call | **Deepest** (~8,000 HTB, in-house desk) | DAS GUI needs a live feed | ~$30k; FINRA/SIPC | Best borrow, gated automation |
| **Cobra** | DAS **CMD**/.NET/FIX | **YES via DAS CMD** (`SLPRICEINQUIRE`/`SLNEWORDER`) — practitioner-confirmed | Strong; **no overnight multiplier** (cost edge for multi-day) | DAS GUI needs a live feed | ~$27–30k; FINRA/SIPC (clears Wedbush) | Best *proven* specialist automation |
| **Alpaca** | REST, free | **YES** — HTB locate API launched **2026-06-24** (round-lot, single-use) | **Unproven** on micro-caps | BYO feed OK | ~$2k; FINRA/SIPC | API-native, inventory untested |
| **Guardian** (Velocity Clearing) | Velocity **VHub** FIX + stock-locate API (parent layer) | Advertised at parent; **retail access unconfirmed** | In-house desk; low-float depth not independently documented | GUI feed; BYO undocumented | ~$30k; FINRA/SIPC | ⚠ **$1M FINRA supervision fine 2025-09-30** |
| **SpeedTrader** | DAS FIX/API; lists a "Short Locate API" | Claimed; **no public docs**; day-trade needs prior approval | Good (12 venues + own desk) | DAS GUI feed | ~$30k (PRO $10k); FINRA/SIPC | Works via DAS, thinner docs; 2024 FINRA fine |
| **IBKR** | **TWS API** (best docs) + Web API | **NO locate API** (manual $20 pre-borrow; on-demand locates Portfolio-Margin-gated ≥$110k) | **Weakest** on day-one low-float runners | **Sanctioned execution-only, no data sub** | $0 open / $2k to short; FINRA/SIPC | Great API, **wrong borrow** |
| Tradier / Schwab / TradeStation / Webull / Lightspeed | Various REST/APIs | **No** usable HTB locate API (ETB-only, or manual GUI, or none) | Weak→none for low-float | mixed | — | Not viable for this book |

**One-liners:**
- **TradeZero** = least integration work, free, execution-only-with-your-own-data. Weakness: thinnest
  inventory on the hardest sub-$5 names + recurring **premarket-outage** complaints (Trustpilot/TradingView)
  — and premarket is exactly when a low-float momentum short fires. The Tim-Sykes TradeZero endorsement is
  **affiliate-tainted** (sykeszero.com funnel) — discount it.
- **CenterPoint** = "best borrows, it isn't even close" (Grittani); in-house lending desk; **Clear Street
  ATLAS** = the automated-locates REST/FIX platform. Downsides: ~3× overnight locate multiplier, $30k min.
- **Cobra** = the community's proven headless-locate path (DAS CMD), cheaper, **no overnight multiplier**.
- **IBKR** = excellent everything *except* the borrow; keep only for liquid/large-cap shorts, if at all.
- **Consensus prescription among serious short-sellers: hold MORE THAN ONE locate account** — different
  clearing firms carry different HTB names at different times.

---

## 2b. Clear Street / CenterPoint deep-dive (they are ONE company)

**Clear Street OWNS CenterPoint.** Clear Street is a cloud-native non-bank **prime broker + self-clearing
broker-dealer** (FINRA CRD #288933, SIPC; founded 2018; ~$685M raised @ $2.1B val; **filed for a Nasdaq
IPO 2026-01-20, ticker "CSIG"**). It acquired CenterPoint (year disputed 2019–2021; SEC S-1 says 2019) and
on **2024-10-10 rebranded it "Clear Street Active Trading."** CenterPoint brand still live as *"CenterPoint
Securities, a Division of Clear Street LLC"* — a transitional dual-brand. **CenterPoint = customer-facing
active-trader front end; Clear Street = clearing / securities-lending / API backend.** CenterPoint's in-house
lending desk (8,000+ HTB symbols) now sits inside Clear Street's platform (whether it draws on Clear Street's
prime book or stays separate = unconfirmed; no source says borrow quality changed post-acquisition).

**Programmatic stack (docs fetched cleanly — docs.clearstreet.io):**
- **Order API** `POST /v1/accounts/{id}/orders`, `side ∈ buy/sell/sell-short`, `strategy ∈ sor/vwap/twap/
  appo/vdark/dma` → real programmatic order entry + algos. REST + WebSocket + FIX.
- **ATLAS** = "Automated Trading Locates Allocation System" (launched **Dec 2022**): automated stock-loan
  allocation + real-time locate pricing over **REST + FIX + web portal**, explicitly aimed at "quantitative
  strategies and robo-advisors." Locate endpoints under the "Olympus" sec-finance product.
- **Access:** institutional Studio/Olympus is **sales/onboarding-gated** (no self-serve keys, no published
  min). A **Clear Street retail Trading App went live 2026-05-14** (US-only, first 10k) exposing the same
  REST API + SDKs to individual systematic traders — the realistic non-institutional route to keys.
- **⚠ THE MAKE-OR-BREAK UNCONFIRMED POINT:** the automated ATLAS locate API is the **institutional/OMS
  surface**; the **retail CenterPoint locate tool is documented GUI-ONLY** ("reach out to our team"). Whether
  an individual CenterPoint active-trader account can hit the automated ATLAS REST/FIX locate API is the one
  thing to verify by phone. **If retail gets ATLAS, CenterPoint/Clear Street is likely the STRONGEST option**
  (deepest borrow + clean automated locate API — beating TradeZero's thinner borrow and Cobra's DAS socket).

**Lance Breitstein's endorsement is affiliate-driven, NOT a tech review.** His own FAQ: *"Centerpoint by
Clear St is my broker of choice. I've known the founder for years from... Traders4ACause... I'm now a
marketing partner of theirs which allows new clients to get discounted commissions and software."* Sign-up
is a FirstPromoter affiliate link (`?fpr=lance&promo=...`). Genuine-but-compensated, relationship-based; he
cites NO execution/API/borrow reason. Discount it like the Tim-Sykes/TradeZero affiliate funnel. (He's ex-
Trillium prop / DMA-heavy — the type who'd value the tech — but doesn't tie that to the rec.)

---

## 3. Locate mechanics — the fiddly, load-bearing part (applies to TradeZero & Alpaca)

Placing the short order is trivial; the **locate** is where automation gets hard and where strategy
assumptions get tested:
- **Async + short quote window:** TradeZero locate quotes **expire in 30 s**; POST/DELETE return 200
  immediately but real state lands in `/history` seconds later → the bot must **poll**. One open quote per
  symbol (2nd in-flight → error).
- **Single-use:** cover a position → you need a **fresh locate** before re-shorting the same name.
- **Non-refundable if unused;** charged in **100-share round lots** (fee on nearest round lot regardless of
  shares actually shorted). Minimum 100 shares, multiples of 100.
- **Availability flag ≠ fill:** an asset flagged shortable/ETB can still reject at order time (documented on
  both Alpaca and IBKR). Inventory changes intraday from demand/recalls.
- **⚠ Paper trading CANNOT exercise the locate leg** — TradeZero returns empty/mock inventory in paper;
  Alpaca locates are live-only. **Implication: the locate leg needs a small LIVE probe** (a few round lots
  on real signal names) before the full loop is trusted. Everything else paper-tests fine.
- **The community DOES automate this at scale** — the DAS CMD API (`SLPRICEINQUIRE` / `SLNEWORDER`, added
  Nov 2021, paired with Cobra/CenterPoint) is the proven workhorse. `das-bridge` (github.com/misantroop)
  is an open-source Python CMD client. So it's a solved-but-involved problem.

---

## 4. "Avoid data fees" — answered: YES on API-native brokers

- **Regulatory truth:** placing orders does NOT require buying the broker's data — order entry and data are
  separate products (IBKR FIX literally can't carry data, proving the decoupling).
- **TradeZero API serves NO market data** ("does not provide quotes, charts, or historical prices"). It's
  **execution-only by design** → you feed signals from Massive and there is simply no broker data line to
  pay. Cleanest possible answer to the user's goal.
- **IBKR sanctions execution-only too** (official, 2025-09-25): *"Clients are not obligated to subscribe to
  market data... The lack of real-time market data will have no effect upon execution quality."* Gotcha: a
  "blind trading" warning pop-up (a warning, not a block) — run **IB Gateway headless** to avoid it;
  `whatIf` margin preview works with no data line; don't call `reqMktData` (error 354 if you do). US-equity
  free delayed data was discontinued, so Massive does 100% of pricing — exactly the intended setup.
- **DAS/Sterling GUI brokers (CenterPoint/Cobra the GUI way) CANNOT fully decline data** — the platform
  needs a live feed to render. So the "no data fees" win is **specific to API-native brokers** (TradeZero,
  Alpaca, IBKR-headless), NOT the DAS path.
- **⚠ "Professional" data classification (a Massive-side cost, not a broker one):** consuming exchange data
  under a **trading entity** or **with outside capital / shared profits** flips you to Professional →
  exchange fees jump ~**5–7×** (e.g. Nasdaq TotalView $60 → ~$400+/mo non-pro→pro). A single natural person
  trading own capital, not registered, stays Non-Professional **even running an algo** (the policy keys on
  the person/use, not on automation). Budget for pro if an entity is ever used.

**Free borrow-availability tool worth remembering (any broker):** IBKR publishes an anonymous FTP file —
host `ftp3.interactivebrokers.com`, user `shortstock`, **blank password**, US file `usa.txt`, pipe-delimited
`#SYM|CUR|NAME|CON|ISIN|REBATERATE|FEERATE|AVAILABLE|`, ~15-min refresh, **no history retained**. Free, no
account. Could pre-filter `brv20d≥100` signals for "actually shortable + at what fee" even if executing
elsewhere. (Archive it yourself for a borrow-fee time series.)

---

## 5. Regulatory / account facts (2026)

- **PDT $25k rule ELIMINATED.** SEC approved FINRA Rule 4210 amendments **2026-04-14**; effective
  **2026-06-04** (FINRA Notice 26-10). Removes the PDT designation + $25k minimum, replaced by a **real-time
  risk-based Intraday Margin Deficit (IMD)** regime. **4+ IMD violations in 12 months → 90-day restriction**
  (a real hazard for high-turnover algos). Floor is now the **$2,000 margin minimum** (Reg T). Phase-in to
  ~Oct 2027 (not every broker flips day one; TradeZero America & IBKR did on 2026-06-04).
  **→ The whole offshore-PDT rationale is dead. Use US (SIPC) entities.**
- **TradeZero America vs International (Bahamas):** America = US FINRA/SIPC (**use this**). International =
  Bahamas, no SIPC, and **does not accept US persons** anyway. Bahamas relevance now only for non-US persons.
- **Automated-trading permission (ToS):** explicitly ALLOWED for own-account personal use at **IBKR**
  (TWS API non-commercial license; no resale), **TradeZero** (sign the API Trading Agreement; personal use;
  200 req/min), **Alpaca** (algo IS the product), **Robinhood** (2026 customer agreement explicitly
  contemplates autonomous AI-agent order flow — striking, though RH has no HTB locates). **Fidelity
  PROHIBITS** third-party trading automation unless approved in writing. DAS-path brokers (Cobra/CenterPoint/
  SpeedTrader): automation is a marketed product but the *unattended/headless* dimension is silent in public
  ToS → **get it in writing at certification.**
- **IBKR headless caveat:** TWS API / Web API **do not support a truly headless retail login** — IB Gateway
  needs a GUI login (drive via **IBC/Docker**) and a **weekly manual re-auth** (Sun ~01:00 ET token
  invalidation). OAuth-2 headless is institutional-only. So "never-touched fully-headless IBKR bot" is off
  the sanctioned path; daily/weekly manual auth + automated thereafter is fine.

---

## 6. Codebase scope — how far the repo is from live (Explore agent)

**Execution is 100% GREENFIELD.** No broker/order/execution/position/live-loop code exists anywhere in the
repo. Three greenfield pieces + reusable parts:

| Piece | State | Notes |
|---|---|---|
| **Real-time 1-minute feed** | **MISSING — the biggest gap** | The short engine keys off per-minute running session-**high** / **max-1m-vol** / breakout bar. Massive's *snapshot* endpoint (used by `live_scan.py`) is coarse **daily** OHLCV — **cannot** feed it. Must build a streaming/polling 1m source (Polygon/Massive aggregates WebSocket `AM.*`, or per-minute REST poll of the small candidate set). |
| **Live short-engine host** | **MISSING** (but engine reusable) | `IntradaySystem` (`TradingEdge.LowFlyer/Intraday.fs`) is PURE and correct; it's only ever fed from historical parquet via `MinuteEmitter` (`Backtest.fs:215-254`). Build a live host: one `IntradaySystem` per (ticker,day), `Short=true, Downside=false`, fed **closed** 1m bars rescaled by `adj_ratio`, evaluate `ShouldEnter` (`Intraday.fs:263-311`) each bar. **F# host preferred** — calling the F# engine directly avoids re-deriving `ShouldEnter` in Python (a parity hazard). |
| **TradeZero execution/locate layer** | **MISSING** | New module behind a broker-agnostic interface (`requestLocate / placeShort / cancel / positions`). |
| **Overnight daily context** | **EXISTS, reusable** | `scripts/equity/build_mr_candidate.fsx` + the `daily_episodes` view already compute `avgvol20`, `adj_ratio`, `prev_adj_close`, universe/price gates. Just schedule a fresh morning run after D-1 ingest. The `med_bar_vol_0945` liquidity prune needs the live first 15m → becomes a 09:45 gate in the host. |
| `live_scan.py` | Exists, **WRONG strategy** | HighFlyerV2 **long**, one-shot, daily-snapshot gate. Reusable only as a credential/DuckDB/snapshot *pattern*, not the short logic. |

**Real-time inputs the live short host must maintain per (ticker,day)** (from 08:30 ET warmup, pre-push
snapshots, no lookahead): running session **high** (`runHi`/`runHiClose`, breakout ref), running **max 1m
bar volume** (`runVolHi`, volume-confirm), cumulative day volume, prior 1m bar. Plus overnight `avgvol20` +
`adj_ratio` for the live gate `brv20d = breakout_bar_vol / (avgvol20·adj_ratio/390) ≥ 100`. Fill = breakout
bar close; exit = hold-to-MOC (the short's default).

**Verification pattern (mirror the long's Run 29 parity check):** (1) captured live 1m bars ==
`data/minute_aggs/{date}.parquet` bar-for-bar; (2) live host shadow-run signals == backtest `--short`
signals for the same day (ticker/minute/price); (3) morning-context row == same-day `mr_candidate` build;
(4) TradeZero **paper** loop for order plumbing, then a small **live** locate probe (paper can't test locates).

---

## 7. Open items to confirm directly (not resolvable from public docs)

1. **TradeZero:** the unread "Application Interface Licensing Agreement" fine print; exact locate fees on
   our specific low-float tickers; API eligibility on any non-America entity (moot — we use America).
2. **Locate coverage on OUR names:** the decisive empirical test — query each candidate broker's
   locate/shortability API for a sample of recent `brv20d≥100` signal names; measures real inventory vs
   marketing list-sizes. Do this **before** committing to a broker.
3. **CenterPoint/Cobra/Guardian/SpeedTrader:** whether their locate APIs work end-to-end on a *retail*
   account, exact DAS CMD locate syntax (behind certification), and whether you can decline their data.
4. **Squeeze tail (strategy, not broker):** brv20d≥100 worst trip was **−839%**; ~3% of trips lose >20%.
   The high PF holds *despite* these, but sizing must respect it — the deferred **big-loss study /
   catastrophe stop** (`docs/lowflyer_results.md`) feeds position sizing before real money.

---

## 8. Recommended sequence (paper-first, when build resumes)

1. **Real-time 1m feed** (Massive/Polygon aggregates) → verify bar-for-bar vs `minute_aggs`.
2. **F# live host** wrapping `IntradaySystem` (`Short=true`) → shadow-run parity vs backtest `--short`.
3. **Morning batch context** (reuse `mr_candidate`/`daily_episodes`) fresh each pre-open.
4. **TradeZero execution module** behind a broker-agnostic adapter → paper loop, then a small live locate
   probe. Swap adapter to Cobra-via-DAS if the locate-coverage probe shows TradeZero inventory too thin.
5. Only then: unattended live, conservatively sized for the squeeze tail.

*(A working plan draft lives at `~/.claude/plans/let-s-go-with-this-velvety-milner.md`; per-agent raw
reports were written under `~/.claude/plans/*-agent-*.md` during research.)*
