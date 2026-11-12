# StraddleHarvester — selling single-stock straddles when IV > RV

The first system of the **Harvester** family: options systems that sell volatility and harvest the volatility risk
premium (user, 2026-11-10). Branch `options`, project `TradingEdge.StraddleHarvester`. Sections are numbered `SH-n`.

## SPEC v0 (user, 2026-11-10)

| | |
|---|---|
| Underlyings | **Individual stocks**, not indices. (MiFID / PRIIPs bars the user from *buying* US ETF shares, not from trading options on them, but the line starts on single names.) |
| Signal | ATM implied volatility at the target expiry **>** realized volatility, the gap above a threshold. Every input known at entry. |
| Realized vol | **Yang-Zhang** (open-to-close + overnight + Rogers-Satchell), not close-to-close. Window to be set (20 days to start). |
| Trade | Sell the ATM straddle about 30 days out; hold to expiry, or close at about 21 days left — both to be tested. |
| Events | **Never hold through earnings** or another scheduled corporate event. |
| Fills | **Leeway for now**: we assume the straddle can be legged into rather than crossing both spreads. A fill sim comes later. Report results at the mid *and* at the bid/ask so the gap between them is visible from the start. |
| Hedge | v1 unhedged; v2 with a daily delta hedge. |

## SH-1 — the data (2026-11-10)

### ThetaData has intraday option history, trades AND quotes (checked on the local Terminal)

Malcolm's mixed signals settled with one query each on our Options STANDARD account (AAPL calls):

| query | 2016-02-03 (AAPL 95C Feb-16) | 2025-02-03 (AAPL 230C Feb-21) |
|---|---|---|
| `option/history/trade` | ✅ every trade, **ms** timestamps (652 trades) | ✅ ms |
| `option/history/quote` (default interval) | 23,402 rows = **one NBBO snapshot per second** | the same, 1 s |
| `option/history/quote?interval=tick` | ✅ every NBBO change (267,694), but timestamps **truncated to the second** | ✅ every change (669,634), **ms** timestamps |
| `option/history/quote?interval=1m` | ✅ minute snapshots | ✅ |
| `option/history/trade_quote` | — | ✅ each trade paired with the NBBO at that moment |

- **Read:** yes — tick-level option trades and quotes from 2016-01-04 (earlier needs PRO).
- **One caveat:** before some date the quote timestamps are whole seconds, so ordering trades against quotes within the
  same second is ambiguous in the early years. Find the cutover date before any intraday fill sim.
- Also available: EOD rows carrying the closing NBBO, open interest, IV and first-order greeks (`history/implied_volatility`,
  `history/greeks/...`), and the flat files. ThetaData's docs list a calendar (market hours and holidays) but
  **nothing for earnings, dividends, splits or corporate actions**.

### Earnings dates: neither vendor, on our plans

| source | what | status |
|---|---|---|
| ThetaData | — | no corporate-events endpoint |
| Massive `/benzinga/v1/earnings` | historical and upcoming earnings, confirmed vs projected | **403 — not entitled** (Benzinga partner add-on) |
| Massive `/tmx/v1/corporate-events` | Wall Street Horizon events (earnings, dividends, …) from 2018 | **403 — not entitled** (TMX partner add-on) |
| Massive `/v3/reference/dividends`, `/v3/reference/splits` | ex-dates, splits | ✅ on our plan (already behind `daily_adjusted`) |
| **SEC EDGAR** 8-K **Item 2.02** ("Results of Operations") | the earnings release itself, with the acceptance timestamp (before the open vs after the close) | ✅ free, every US filer, back to 2004 |

**Proposal:**

1. **Earnings: EDGAR 8-K Item 2.02 filings.** The release date of record is causal, and the acceptance time tells
   pre-market from after-close.
   - This is a historical record, so it is not an upcoming calendar. For the backtest that is fine: the dates of the
     next report are announced weeks ahead, and the rule "no expiry past the next report" needs only a projection.
     The projection would be the last report + ~91 days, confirmed against the actual date.
   - For a live system, a real calendar is needed later. Options include Nasdaq's, a vendor's, or IBKR's
     Wall Street Horizon feed.
   - EDGAR asks automated clients for a User-Agent with contact details, so that needs the user's choice first.
2. **The options themselves as the event detector (a cross-check).** A scheduled event shows up as a kink in the
   ATM IV term structure: the expiry straddling the event carries far more IV than the one before it.
   - The implied event move can be backed out of the two IVs. This also catches events EDGAR does not list, such as
     FDA dates, investor days and votes.
   - It costs nothing beyond the chain we pull anyway.
3. **Ex-dividend dates** from Massive. Large dividends matter for early exercise of the short call.
4. **Pending M&A** (a stock pinned to a deal price) needs a separate filter. It can be detected from the options too
   (IV collapsed, price flat), or from 8-K Item 1.01 / DEFM14A filings.

Sources: [ThetaData docs](https://thetadata.net/docs/operations/calendar_on_date.html) ·
[Massive Benzinga earnings](https://massive.com/docs/rest/partners/benzinga/earnings) ·
[Massive TMX corporate events](https://massive.com/blog/build-a-corporate-events-calendar-using-massive-tmx-wall-street-horizon)

**Earnings source — decided (user, 2026-11-10):** EDGAR 8-K Item 2.02, with the same User-Agent as
`scripts/equity/download_float.py`.

## SH-2 — the universe and the cost of the chain pull (2026-11-10)

**Universe (user, after Volatility Vibes):** the name's option volume averages **≥ 25,000 contracts a day** over the
**20 trading days BEFORE day D**. That is calls + puts, every expiry, and day D excluded (rule 2 of the lookahead
protocol: `20 PRECEDING AND 1 PRECEDING`).

### Where the per-name option volume can come from

| source | result |
|---|---|
| ThetaData `option/flat_file/eod` (whole market per day) | ❌ PRO tier, and only the last 7 days |
| ThetaData `option/history/eod?symbol=*` | ❌ "Cannot specify '*' for the symbol" |
| Massive options flat files (`us_options_opra/day_aggs_v1`) | ❌ listing works, download **403** (not on our plan) |
| Massive options REST aggregates | ❌ only ~2 years back (2016 and 2022 both refused) |
| **ThetaData `option/history/eod?symbol=X&expiration=*`** | ✅ every contract of one root per day: OHLC, volume, trade count, and the closing NBBO |

### Measured throughput (Options STANDARD, 4 concurrent)

| request | rows | bytes (csv) | time |
|---|---|---|---|
| AAPL, Jan 2016, all expiries | 23,909 | — (8.3 MB ndjson) | 4.0 s |
| AAPL, Jan 2025 | 46,981 | 6.4 MB | 8–15 s |
| F, Jan 2025 | ~12k | 1.6 MB | 1.9 s |
| AAPL+TSLA+NVDA+AMZN, Jan 2025, 4 in parallel | 303,362 | — | 18.7 s (**~16k rows/s**) |

- **The whole market is too much.** The listing has 15,853 option roots. Whole-market EOD is ~150 MB a day (ThetaData's
  figure), i.e. on the order of 1M+ contract rows a day. At ~16k rows/s, 2016 → 2026 is roughly **two days of pulling**.
- **Not wasted for the universe names.** Their chains are the backtest's own input: the EOD rows carry the closing
  NBBO, which gives the ATM straddle's bid, ask and mid every day. Only the roots that never pass the screen are wasted.

**Proposal — a superset prefilter by STOCK liquidity, then the exact causal screen on the options:**

- The prefilter decides only what to *download*, never what to *trade*. Even using the whole period it is harmless,
  as long as it is a superset of every name that ever passes the 25k screen.
- Validate it on a recent pilot year: pull every root, then check that no name passing the screen falls outside the
  prefilter.

## SH-3 — earnings dates from EDGAR, built (2026-11-10)

`scripts/options/edgar_earnings.py`: for each ticker, every 8-K / 8-K/A carrying Item 2.02, with the acceptance time in
ET and its session — **BMO** (before 09:30: hits day D's open), **RTH**, **AMC** (16:00+: hits the next open).

- **Ticker → CIK comes from Massive**, `/v3/reference/tickers/X?date=D`. That lookup is point in time: FB on 2020-06-01
  and TWTR on 2021-06-01 both resolve.
- **No contact details are sent.** `data.sec.gov` serves a generic User-Agent, but `www.sec.gov` (where
  `company_tickers.json` lives) **403s** it. So the user's email is not needed: the float scraper's header is kept out
  of this script.
- **acceptanceDateTime is real UTC.** AAPL's releases land at 20:30Z in July and 21:30Z in January, both 16:30 ET.
- **Test (AAPL, TSLA, NVDA, BRK.B, FB as of 2020):** 273 filings since 2015-07, sessions AMC 212 · BMO 45 · RTH 16.
  - **TSLA has 95, not ~45.** Tesla files its quarterly DELIVERY numbers under Item 2.02 too (early in the quarter's
    first month, BMO). Those move the stock as well, so excluding them errs on the safe side.
  - Other Item 2.02 filings that are not the quarterly report (preliminary results, guidance updates) will likewise
    count as events. That is conservative for a short-volatility system.
- **⚠ The 8-K can trail the press release** (minutes, occasionally a day). The cross-check is the options' IV term
  structure.

## SH-4 — the Massive OPTIONS plan (user, 2026-11-10)

The user added a Massive options key, `~/.config/massive-options/credentials` (JSON: API key + S3 keys; chmod 600
done). Measured:

| what | access |
|---|---|
| Options flat files, `day_aggs_v1` / `minute_aggs_v1` / `trades_v1` | ✅ **from 2022-11-09** — a ROLLING 4-year window (2022-11-08 refused; binary search over the day files) |
| Options flat files, `quotes_v1` (135 GB a day) | ❌ not on the plan |
| REST aggregates, 2016 and 2022-11-01 | ❌ "plan doesn't include this data timeframe" |
| REST quotes | ❌ |
| REST chain snapshot | ✅ live only |

- **A whole-market day is one ~4 MB file in ~1.4 s.** The 2022-11-09 → today window (1,003 days) downloads in minutes
  (`scripts/options/massive_options_day_aggs.py` -> `data/options/massive_day_aggs/`). The equivalent ThetaData pull,
  one root per request, is about an hour per MONTH.
- **It agrees with ThetaData.** On 2026-10-15, across the 315 roots both had: **91% of roots identical**; Massive's
  total is 1% lower (AAPL −2.2%, ANET −5.6%) — it drops some trade conditions. That is immaterial at a 25k threshold.
- **⚠ No bid/ask.** Day aggregates carry trades only (OHLC, volume, transactions). The straddle's prices, and so the
  backtest's fills, still need ThetaData's EOD NBBO — but only for the universe's names.

**The plan this suggests:**

1. **Phase 1, 2022-11 → today.** Screen the universe from Massive (minutes). Then pull ThetaData EOD chains for the
   names that pass (hours, not days). Run the first backtest on those 4 years.
2. **Phase 2, 2016 → 2022-11, only if Phase 1 earns it.** A ThetaData-only screen, cut down by the stock-liquidity
   prefilter that the October pilot calibrates.

## SH-5 — the universe, 2022-11 → 2026-11 (Massive day aggregates)

`scripts/options/universe_screen.py` -> `data/options/universe_daily.parquet`, log `logs/options_universe_screen.log`.
1,003 Massive days (2022-11-09 → 2026-11-09, 9,443 option roots).

- **Rule:** the underlying's STANDARD option contracts average ≥ 25,000 a day over the 20 prior trading days; single
  stocks only (ticker_reference type CS or ADRC).
- **Adjusted roots are dropped** from the screen and the book (1,438 roots, 0.10% of all contracts). These are
  corporate-action contracts with a digit appended (CMCS1, WOLF1; WOLF1 averaged 86k a day after the restructuring).
  They deliver non-standard packages, so their prices are not straddles on the stock.
- **Passing the volume bar on any day, by type:** CS 492 · ETF 83 · ADRC 38 · ETV 12 · ETS 1 · untyped 8. The untyped
  are all indices: SPXW, VIX, SPX, XSP, NDXP, RUTW, VIXW, RUT.

| | |
|---|---|
| single stocks ever in the universe | **530** (38 of them ADRs) |
| names per evaluable day | min 122 · **median 162** · max 242 |
| in on ≥ 950 of 983 evaluable days | 61 names |
| days in the universe per name | median 148; 90 names < 20 days |

Names per day by quarter: 138–142 through 2023, 148–180 in 2024–25H1, 197–218 in 2025H2–2026H1, 171 in 2026Q4.

- **The top by median 20-day average (thousands of contracts a day):** NVDA 2,613 · TSLA 2,300 · AAPL 980 · SPCX 912
  (from 2026-06) · AMZN 688 · AMD 605 · PLTR 491 · MSTR 467 · META 435 · GOOGL 367 · MSFT 361 · INTC 338.
- **At the bar on 2026-11-09:** ACHR, DJT, AXTI, DVN, SCHW, TEM, GM, RBLX, all at 25.1–26.0k.

⏭ **Next: the ThetaData EOD chains for these 530 names** — the closing bid/ask for the straddles, per name over its
universe span plus the holding period.

## SH-6 — the universe's EOD chains from ThetaData (launched 2026-11-10)

- **Expiries ≤ 90 days** (`max_dte=90`). The user raised the cap from 60 so the same data serves a later
  CALENDAR-SPREAD study.
- **Per name:** from its first universe day to 90 days after its last, capped at 2026-11-09. That is
  `data/options/universe_jobs.tsv` (530 names, ~12,500 name-months), cut into calendar-quarter requests.
- **Output:** `data/options/eod_universe/<ROOT>__<chunkstart>.parquet`; `done.tsv` is the resume record. Log
  `logs/options_eod_universe.log`.
- **It starts when the October pilot ends,** chained on the pilot's PID (`logs/options_eod_universe_launch.sh`) and
  detached with nohup.
- **Measured on AAPL 2025-01:**

  | filter | rows | time |
  |---|---|---|
  | none | 46,981 | 9.2 s |
  | `max_dte=60` | 16,541 | 4.6 s |
  | `max_dte=60` + `strike_range=10` | 5,921 | 33.5 s |

  `strike_range` is ~7× SLOWER: ThetaData looks up the stock price per day server-side. So every strike is kept and
  trimmed locally.
- **Size:** parquet runs ~11.5 bytes a row (the pilot: 17M rows in 195 MB), so this is a few GB at most.
- **Time:** roughly 3–4 h is expected.
- **ThetaData Pro** is under discussion with the user's trading partner, to cut download times. ⚠ Pro does NOT bring
  bulk history. The flat files cover *"the 7 most recent calendar days"* only
  ([Flat Files](https://thetadata.net/docs/Flat-Files/Getting-Started.html),
  [Option Flat File EOD](https://thetadata.net/docs/operations/option_flat_file_eod.html); older history is a separate
  deal with sales). What Pro adds for history: **8 concurrent requests instead of 4** (≈ half the time, if the limit is
  per request) and **options history from 2012-06**. The docs say "professional users" — confirm with ThetaData that
  this means the Pro tier, not commercial-user status.

## SH-7 — the October 2026 pilot: every ThetaData root, one month (done 2026-11-10)

`scripts/options/eod_pull.py` over all 15,852 listed roots, 2026-10-01 → 11-06, every expiry →
`data/options/eod_pilot_2026-10/` (626 MB). Analysis: `scripts/options/universe_pilot.py`, log
`logs/options_universe_pilot.log`.

**The pull:** 61.7 min · **0 errors** · 6,349 roots with rows, 9,503 with none (HTTP 472) · **55.3M rows** (~2M
listed contracts a day) · 14,900 rows/s.

**⭐ ThetaData and Massive give the SAME universe.** On 2026-11-06 the single-stock universe has **184 names from each
source, and they are the same 184**. The ~1% volume gap measured in SH-4 flips no name. So the Massive screen (SH-5)
stands as the universe for 2022-11 →.

**The stock-liquidity prefilter, for a later 2016 → 2022-11 ThetaData-only screen:**

- Of the 193 single names that pass on any of the 7 evaluable days, the lowest stock 20-prior-day dollar volume is
  **\$37.6M** (LCID). Next: HTZ 39.3 · QUBT 43.9 · NN 44.7 · MPT 47.3 · TE 50.9.
- Most pass with far more: the median is \$795M.

| stock dv20 floor | stock tickers kept (2026-11-06) | passing single names lost |
|---|---|---|
| \$5M | 4,550 | 0 |
| \$20M | 2,943 | 0 |
| \$30M | 2,533 | 0 |
| \$50M | 2,003 | 5 |

- **⚠ Calibrated on ONE recent month.** The contracts-per-dollar ratio of 2016 is unknown. Single-stock option volume
  grew a lot after 2020, so the same 25k bar should need MORE stock liquidity back then — which makes the floor
  conservative. Check it on one 2016 pilot month before relying on it.
- **⚠ The prefilter saves little.** Even \$30M keeps 2,500 tickers. The rows are in the big chains, which pass anyway.
  The real saving for 2016 → 2022 is `max_dte` (SH-6).

## SH-8 — universe size vs the contract bar (Massive, 2022-11 → 2026-11, single stocks)

| 20-day avg contracts ≥ | names ever | names / day (median) | name-days |
|---|---|---|---|
| **25,000** (SPEC) | **530** | **162** | 165k |
| 15,000 | 736 | 231 | 236k |
| 10,000 | 960 | 297 | 300k |
| 5,000 | 1,484 | 439 | 439k |
| 2,500 | 2,044 | 646 | 648k |
| 1,000 | 2,937 | 1,033 | 1.03M |

**User, 2026-11-10: stay at 25k for now; widening is for later.** The plan for widening:

- Report the first backtest by liquidity bucket.
- If the edge holds toward the bar, extend to 10k (+430 names, ~1–1.5 h of ThetaData).
- Measure the quoted spread against the premium collected, since the legging assumption gets more optimistic in thinner
  chains.

## SH-9 — Yang-Zhang realized volatility for the universe (2026-11-10)

`scripts/options/yz_vol.py` -> `data/options/yz_vol.parquet` (log `logs/options_yz_vol.log`). Covers all 530 names,
528k bars, 2022-06-01 → **2026-10-30** (`daily_adjusted` ends there; refresh before the last week is used).

- **Prices:** from `daily_adjusted`. The overnight return is the only term that crosses days, so it alone uses the
  causal `n` and `cum_div`: `ln((O_t·n_t + C_t − C_{t−1}) / (Cl_{t−1}·n_{t−1}))`. The open→close and Rogers-Satchell
  terms use the same day's raw prices. NVDA's 10:1 split (2024-06-10) passes through cleanly.
- **Columns:** `yz10`, `yz20`, `yz60` (annualized ×252), `cc20` (close-to-close), the daily `r_on` / `r_oc`, and two
  flags per window: the largest calendar gap and the largest |overnight| return.
- **⚠ Knowability:** yz_N on day t INCLUDES day t's bar, so it is known at t's 16:00 close. It is valid for an entry at
  t's closing option quotes only; for anything earlier, use the previous row.

| | |
|---|---|
| yz20 across all name-days | p5 0.19 · median **0.48** · p95 1.29 · max 17.6 |
| yz20 / cc20 | median **1.048** |
| NVDA, 2026-10-30 | yz20 0.324 vs cc20 0.325 |
| AAPL, 2026-10-30 | yz20 **0.251** vs cc20 **0.145** |

**AAPL's gap is real, not a bug.** Over those 20 days its overnight and open→close returns are correlated **−0.49**
(gap-and-fade): std overnight 0.153, std intraday 0.134, but std of their sum only 0.145. Yang-Zhang ADDS the component
variances, i.e. it assumes they are independent. **When they offset, YZ overstates the close-to-close variance** — the
variance a straddle held between closes is actually exposed to.

⏭ **To test, not assume:** which of yz20 / cc20 / yz60 better forecasts the variance realized over the straddle's
holding period. That is the comparison the IV is really being made against.

**Flags to exclude from the book (reused tickers, data holes):**

- `gap_days_20 > 5`: 269 rows on 14 tickers — B, BBBY, BBIG, COHR, CORZ, FIG, FISV, GOLD, PARA, SI, SPWR, **SPCX**,
  VG, SATS. SPCX's rows before its 2026-06 listing belong to an ETF that used the ticker.
- `max_on_20 > ln 2` (an overnight move of ±100%, with splits already adjusted): 1,196 rows on 50 tickers — mostly
  genuine biotech and meme jumps (REPL, CAPR, ABVX, BYND, AMC…), but a reused ticker or a missed split would also land
  here. Review before use.

## SH-10 — earnings dates for the universe (EDGAR, 2026-11-10)

`scripts/options/edgar_earnings.py --tickers-file data/options/universe_tickers.tsv` ->
`data/options/earnings_8k202.parquet` (log `logs/options_edgar_earnings.log`). Each name is looked up as of its last
universe day.

**Result:** **16,182 Item 2.02 filings on 444 of the 530 names**, 2015-06 →. Sessions: AMC 9,048 · BMO 6,692 · RTH 442.

**Two bugs found and fixed on the first run** (that run had 419 names):

1. **De-duplication by accession alone dropped a ticker's whole history** when two tickers share a CIK: GOOG/GOOGL, and
   GPS/GAP (Gap's 2024 ticker change). The fix de-duplicates per ticker + accession: GOOG 46, GOOGL 46, GPS 55, GAP 55.
2. **22 delisted or renamed tickers got no CIK** (SQ, ATVI, X, WBD, PARA, SAVE…). Massive's point-in-time
   `/v3/reference/tickers/X?date=` 404s for them. The fallback is the ticker SEARCH with `active=false` → 0 missing.

**86 names have NO Item 2.02 filings** (38 ADRs + 48 others). These are foreign private issuers, which report earnings
on **Form 6-K** (no item codes) or 40-F: BABA, TSM, ASML, NVO, SHEL, SPOT, UBS, DB, NU, GRAB, ONON, NBIS, STLA, the
Canadian miners and energy names (AG, KGC, PAAS, CCJ, NXE, DNN, SU, TECK), and others. FRC is different: a bank, it
filed with the FDIC, not the SEC. **They have no earnings source yet.**

- **v1 proposal:** leave these 86 out of the book.
- **v2:** detect their events from the options themselves — the IV term-structure kink: the first expiry after an event
  carries much more IV than the one before it.

**Cadence, 2023 →:** median **4.15** filings a year per name. 12 names file more than 6 a year: LCID 9.3, TSLA 8.3,
ABBV 8.3, EQT 8.3, OXY 7.5, HOOD 7.0, APO 6.5, ASTS 6.5. These are deliveries, preliminary results and operating
updates under 2.02 — all are treated as events (the conservative side). 66 names file fewer than 3 a year, mostly partial
periods (IPOs, delistings). Check a sample before trusting a gap between filings as "no event".

## SH-11 — ⚠ ThetaData returned CUT-OFF data; the pull now verifies every chunk (2026-11-10)

**The problem.** From about 16:25 CET the Terminal began logging `Internal server error: Corrupt FIT data … HTTP 500`
(121 occurrences by 17:15).

- **Sometimes the client gets the 500.**
- **Sometimes the response simply ENDS EARLY with HTTP 200**, and the old script saved it as complete. Rows come ordered
  by date, so a cut-off chunk is just missing its later days. GOOGL 2024Q1 was saved with **one day (2024-01-02, 1,112
  rows)** against ~59,500 for the full quarter.
- **The cut can REPEAT across attempts.** For a while every 2024Q1 chunk ended in its first week, and two attempts
  agreed. So "two attempts agree = a genuine end (delisting)" — the first fix — accepted 13 cut chunks.
- **It is random per request.** Single days that failed succeeded on the next try. 2024Q1 is by far the worst hit; a
  quarter-long request (~60 days) almost never got through.

**The fix** (`scripts/options/eod_pull.py`):

1. **Completeness is checked against an INDEPENDENT source.** A chunk must reach the last day Massive's whole-market day
   files have the root within the chunk's range (`data/options/massive_root_days.parquet`: 9,443 roots, 4.3M root-days).
   Genuine ends pass: ATVI 2023-10-12, DWAC 2024-03-25, FRC 2023-04-28, GPS 2024-08-21.
2. **Bisect on failure.** A piece that fails 3 times is split in half, recursively, down to single days (8 attempts
   each), and the pieces are reassembled. GOOGL 2024Q1 came back complete in 4.1 min: 61 days, 59,500 rows, no
   duplicate contract-days.
3. **Only complete chunks are final.** Anything else writes no file and is retried by a rerun. Files are written via
   `.part` + rename.
4. **`--validate`** re-checks every saved chunk and moves the cut ones to `data/options/eod_universe_quarantine/`
   (`done.tsv.bak` is kept).

**The damage, and where it stands:** of 1,985 chunks saved by then, **41 were cut**: 28 found by the calendar check, 13
more wrongly accepted by the agreement rule. All 41 are quarantined and are being re-pulled. Every other chunk reaches
Massive's last day for its root. The pull restarted at 17:20 CET. The 2024Q1 chunks now take minutes each while the
server is degraded.

**Not yet re-checked:** the October pilot (`eod_pilot_2026-10`, pulled 14:05–15:09, before the errors began). Its
single-stock universe matched Massive's exactly (SH-7), so the screen is unaffected.

## SH-12 — ⛔ ThetaData cancelled; the downloads stopped (2026-11-10, 17:4x CET)

The user's trading partner asked ThetaData to cancel the subscription and refund it. The universe pull, its queued retry
pass and the Theta Terminal were all stopped.

**What was downloaded:**

| | |
|---|---|
| `data/options/eod_universe/` | **2,450 of 4,753 chunks complete** (52%), 88.8M rows, 1.2 GB. **283 of 530 names fully pulled** (alphabetically A → ~LUNR, partially beyond). Every chunk verified complete against Massive (SH-11). |
| `data/options/eod_universe_quarantine/` | the 41 cut-off chunks (2.6 MB) — not usable |
| `data/options/eod_pilot_2026-10/` | every root, 2026-10-01 → 11-06, 55.3M rows, 626 MB (pulled before the errors began; not re-validated) |

- **The pull is resumable** if a subscription returns: `done.tsv` + `logs/options_eod_universe_launch.sh`.
- **⚠ Check ThetaData's terms** on keeping and using data from a refunded subscription before building on these files.
- **Massive (SH-4) is unaffected:** day / minute aggregates and trades from 2022-11-09 — but **no quotes** on that plan.
  So the straddle's bid/ask would need a different source.

## SH-13 — ThetaData PRO; the 2012–2022 whole-market history moves to the VPS (2026-11-11)

**PRO bought by the user** (`Options: PROFESSIONAL`, 8 concurrent, history from 2012-06). The 2022-11+ universe pull
resumed on the PC with 8 workers: ~12.5k rows/s at first, ~19.7k later, zero server errors. It is not 2× the 4-worker
rate, so the server sets the pace.

**The 2012-06-01 → 2022-11-08 history runs on the VPS, outside trading hours only** (user: no overnight noise at home).
It covers EVERY root and EVERY expiry — the user: "we'll get all of it". From 2022-11-09 on, Massive gives the screen.

- **The window** (`scripts/options/market_calendar.py`, parsing `TradingEdge.Orb/Timezone.fs`): BLOCKED on trading days
  **07:00**–16:30 ET (13:30 on early closes); OPEN all day on weekends and holidays. The user said 08:00; 07:00 is used
  because `te-oms.timer` fires at **07:30 ET** — to be confirmed with the user. The OMS ended at 16:17 ET on 11-10.
- **`te-options.service`** (enabled, `Restart=on-failure`, `MemoryMax=5G`) runs `scripts/options/vps_runner.py`:
  1. Sleeps through blocks.
  2. Starts the Terminal, bound to 127.0.0.1.
  3. Builds the job list on the first window (`build_market_jobs.py`: every listed root, its window = [first expiry −
     3 y, last expiry] ∩ the range).
  4. Runs `eod_pull.py --deadline` (no new request in the last 20 min before a block; a watchdog stops it 2 min before).
  5. Every 5 min, `rsync --remove-source-files` to the storage box at
     the storage box's VPS sub-account, `te-vps/options/<run>/`.
  6. Stops the Terminal.
  7. Writes `DONE` when every chunk is final, or `ALERT` after 3 passes with no progress.

  `--not-before 2026-11-11 16:30 ET`, so it cannot run a second Terminal on the key while the PC pull is running.
- **⚠ The VPS disk floor:** `oms_day.ps1` deletes the oldest captures while < 40 GB is free, and 38–39 GB was free on
  11-11. So nothing accumulates on the VPS: files leave every 5 min.

**Completeness before 2022-11** (Massive does not reach back): each piece must reach the last TRADING day of its range
(`data/options/trading_days.txt`, from `daily_prices`, 2012 → 2026-10-30). When a response stops early, ONE request for
the rest of the range decides:

- **472 there** = the root's genuine end (accept).
- **Rows there** = a cut (retry, then bisect).

TWTR 2022Q4 (delisted 2022-10-27) settles in 9 s; bisecting it took 177 s. AAPL 2013Q1: 156,748 rows, 60 days, every
expiry (max 745 DTE).

**⏭** Tonight's first window opens at 16:30 ET (22:30 CET). Things to check: `~/te/options/logs/runner.log`, the
job-build log, and the first uploads on the box.

## SH-14 — the universe pull is DONE; the whole-market priorities; PC by day, VPS by night (2026-11-11)

**The 2022-11+ universe is complete.** All 4,753 chunks are final (4,721 with data, 32 with none); 0 errors on the PRO
key; the last session ran at ~24.8k rows/s. It is uploaded to the box at `te-vps/options/universe_2022_2026_dte90/`
(4,723 files, 2.47 GB, in 43 s) and kept locally. The October pilot is also on the box, at
`te-vps/options/pilot_2026-10_all/` (642 MB).

**The user's priorities for everything else:**

1. 2022-11-09 → 2026-11-10, every root outside the universe's spans, ≤ 90 DTE.
2. 2012-06-01 → 2022-11-08, every root, ≤ 90 DTE.
3. Later: the > 90 DTE remainder of the whole history. ThetaData has `max_dte` but no minimum, so the remainder means
   per-expiry requests (monthlies, LEAPS) — or a full re-pull at ~2× the cost. To be designed.

**Priorities 1 and 2 = ONE run, `market_dte90`.** The job list is
`build_market_jobs.py --ranges 2022-11-09:2026-11-10,2012-06-01:2022-11-08 --exclude-file data/options/universe_jobs.tsv`:
15,853 roots looked up, 0 errors; 12,192 lines for range 1 then 14,851 for range 2; ~487k quarter requests, many of
them empty quarters of dead roots (a fast 472). AAPL, for example, gets only the gaps around its universe span, then
2012–2022.

**Both machines share it through the box** (`te-vps/options/market_dte90/`: `jobs.tsv`, `done_pc.tsv`,
`done_vps.tsv`, the parquet):

- **The PC** (`scripts/options/pc_pull.sh`, started by hand each trading day) pulls 07:03 → 16:10 ET and keeps its
  files.
- **The VPS** (`te-options.service`, `scripts/options/options_runner.py --mode vps`) pulls outside those hours and
  deletes after upload.
- **Before each window,** a machine downloads the other's `done_*.tsv` and passes it to `eod_pull.py --also-done`, so a
  chunk on the box is never pulled twice.
- **Never at the same time:** one Terminal per key; the runner refuses to start if port 25503 is taken.

**Estimates:** ~2–3B rows for priorities 1 + 2 at ~20k rows/s — ~2–3 days of calendar time with both machines, then
~55–70 GB on the box with priority 3 included.

## SH-15 — the plan changed: EVERY expiry in one run, `market_full` (user, 2026-11-11)

ThetaData's EOD endpoint has `max_dte` but no minimum, so "≤ 90 DTE first, the rest later" would re-pull the ≤ 90 rows.
Measured on the October pilot (55.3M rows, every root, every expiry): **56.0% of the rows are ≤ 90 DTE** (45.9% ≤ 60).
So the capped-first plan costs ~2.8× the data of a single full pull at ~1.8×.

- **Switched before a single `market_dte90` chunk was pulled.** `market_dte90` is removed (locally and on the box).
- **The run is `market_full`:** `build_market_jobs.py --ranges 2022-11-09:2026-11-10,2012-06-01:2022-11-08`, with no
  universe exclusion and no `max_dte`. 26,513 job lines (11,662 + 14,851), ~490.5k quarter requests.
- **The 530 universe names are re-pulled in full,** so the whole dataset comes from one consistent run (their ≤ 90 DTE
  rows twice: ~170M rows, ~2.5 h).
- **`universe_2022_2026_dte90` stays,** for the first backtest.
- **Both machines point at `te-vps/options/market_full/`:** `pc_pull.sh` defaults to it, and `te-options.service` was
  updated. The box holds `market_full/`, `pilot_2026-10_all/`, `universe_2022_2026_dte90/`.

**Estimate:** ~4–4.6B rows, ~55–62 GB; at ~20k rows/s ~55–65 pull-hours, i.e. ~3.5–4 calendar days with the PC by day
and the VPS by night.

## SH-16 — newest first (user, 2026-11-11)

The chunks are pulled **newest period first**, across all roots: every root's 2026Q4 chunk, then 2026Q3, …, back to
2012-06 (`eod_pull.py --order newest`; `options_runner.py` passes it by default). Within a period the roots run
alphabetically. The recent years — the most useful ones — land first.

- **The chunks stay calendar QUARTERS.** Months would triple the requests (~490k → ~1.5M), mostly quick 472s for dead
  roots, ~10 h of extra overhead.
- **Chunks per year:** 2026 28.4k · 2025 31.5k · 2024 34.7k · 2023 37.7k · 2022 53.3k (two ranges split at 2022-11-09)
  · 2021 41.7k · 2020 40.8k · 2019 39.4k · 2018 32.0k · 2017–2013 25–27k each · 2012 17.8k.
- **The key names (`ROOT__chunkstart`) are unchanged,** so the done records on both machines stay valid.

## SH-17 — the data lives on the SHARE sub-account, `options/`; both machines log in by KEY (2026-11-11)

- **The user moved the box folders** (as master) from the VPS's backup sub-account to the share: `options/market_full`,
  `options/pilot_2026-10_all`, `options/universe_2022_2026_dte90`. The VPS backup folder keeps only its own `out/`.
- **Password logins to the share failed** from the PC and the VPS alike, over ports 22 and 23, even after a reset. So
  both machines now use KEYS: the share's `.ssh/authorized_keys` holds the PC's `~/.ssh/storagebox_pc_ed25519.pub` (new)
  and the VPS's `~/.ssh/storagebox_ed25519.pub`. No password sits on the VPS.
- **`pc_pull.sh`** reads the share's user and host from `~/.config/hetzner-storage-box/credentials-share` (kept out of
  this public repo) and passes `--key ~/.ssh/storagebox_pc_ed25519`.
- **`te-options.service`** uses `--dest <share>:options/market_full/ --key ~/.ssh/storagebox_ed25519`.
- **Restarted 07:03 ET:** the PC resumed from `done.tsv` (2,388 chunks already final). `done_pc.tsv` and `jobs.tsv`
  survived the move. The VPS is enabled again and blocked until 16:30 ET.

## SH-18 — the Volatility Vibes baseline: three features, every straddle and calendar, held to expiry (2026-11-11)

User: test VV's three features (IV30/RV30, term-structure slope front→45 DTE, 30-day average volume) on earnings AND
non-earnings plays. Sell every ATM straddle and buy every ATM calendar, hold to expiry (the calendar's back leg is sold
when the front expires), then do a decile breakdown per feature; gating comes next. Analysis in SQL on the trade files.

- `scripts/options/vv_backtest.py` -> `data/options/vv/{features,straddles,calendars}.parquet` (40 s, 6 workers):
  259k name-days, **1.98M straddles** (every expiry 1-90 DTE), **1.60M calendars** (every front up to 60 DTE, back = the
  expiry closest to front+30 within [+21,+45]). The docstring has the definitions. In short: ATM = nearest the
  regular-session close (Massive day bars; checked: NVDA 2024-08-28 close 125.61 is the 16:00 print, not after-hours).
  IV = Black-Scholes r=q=0 vol of the straddle mid, linear in DTE. rv30 = Yang-Zhang 30 (`yz_vol.py` now also writes
  yz30). The calendar strike = the nearest to spot quoted in BOTH expiries (weekly half-strikes are not listed in the
  monthly back: AAPL 207.5). Earnings reaction day: BMO/RTH = that day, AMC = the next.
- `scripts/options/vv_deciles.py` (log `logs/options_vv_deciles.log`). Samples: **EARN** = entry the day before the
  reaction, front = the first expiry holding the event. **NON30 / NON7** = no event in the hold, straddle/calendar
  front nearest 30 / 7 DTE, entered every day (holds overlap). Exclusions: split in hold, not in universe,
  gap_days_30 > 5, 6-K filers (no earnings source).
- P&L in bp of spot. mid = in and out at the mid; nat = sell at the bid, buy at the ask; legs at expiry settle at
  intrinsic.

**Totals (mid / nat, bp of spot):**

| sample | straddle n | straddle mid | straddle nat | win | calendar mid | calendar nat |
|---|---|---|---|---|---|---|
| EARN (dte 2.7) | 2,299 | **+52.9** (t 3.1) | +2.7 | 59% | +8.1 | −373 |
| NON30 (dte 25) | 138k | +17.5 | −88 | 60% | +16.3 | −362 |
| NON7 (dte 7) | 138k | +15.1 | −32 | 60% | +11.8 | −313 |

**Deciles, the shape:**

- **ts_slope_0_45 is the strongest and it is monotone.** EARN straddle D1 (most inverted, < −0.041) **+316 bp**,
  D10 (≈ flat, > −0.003) **−155**. Calendar mid D1 +58 / D10 −119. NON30 straddle D1 +186 / D7-10 ≤ 0, calendar
  +70 → −61. NON7 the same shape. (EARN decile SE ≈ 55 bp.)
- **iv30_rv30: no clean monotone.** EARN: D10 (> 1.71) +208, but D1-D3 (< 1.11) +55-69 too. NON30: D1 (IV well
  BELOW RV, < 0.68) is the best straddle decile (+98): RV right after a jump mean-reverts.
- **avg_vol30: the top decile is best** (EARN straddle +165, calendar +95; NON30 straddle +116). Otherwise flat.
  VV's 1.5M floor binds on almost nothing in this universe (min ≈ 0.5M, D1 tops out at 4.5M).
- **VV's published gates** (avg_vol ≥ 1.5M, iv30/rv30 ≥ 1.25, slope ≤ −0.00406) on EARN: 1,097 trades, straddle
  **+80 mid / +36 nat**, t 3.3; the rejected half +28 / −28. Calendar +13 mid.

**⚠ Before reading nat numbers:** the calendar's natural P&L is catastrophic everywhere (−300 to −2,000 bp). Six
crossings of the EOD (16:00) NBBO, which is wide at the close (AAPL 2025-07-31, Aug-22 205C 7.05 × 9.80). Median
calendar half-spread cost ≈ 100-135 bp vs a median debit ≈ 400-650 bp. The real fill lies between mid and nat; that is
the fill sim's job. Straddle means are tail-dominated: median return on credit +17-30%, but the means are small.

## SH-19 — VV earnings play, crush exit + fill fractions (scripts/options/vv_crush.py, logs/options_vv_crush.log)

EARN events (entry at the close the day before the reaction), 2,288 with all reaction-day quotes. Exit at the REACTION
day's close NBBO (EOD data has no morning quote) vs hold to expiry. Fill: each leg at mid ∓ f·half-spread, in and out.
Returns are per-trade means as % of credit (straddle) / debit (calendar).

| | f=0 (mid) | f=0.25 | f=1 (natural) |
|---|---|---|---|
| straddle crush, all / VV gate | −1.7 / +2.0 | −5.2 / −0.6 | −17.8 / −9.8 |
| straddle to expiry, all / VV gate | +0.2 / +3.4 | −1.2 / +2.5 | −6.4 / −0.8 |
| calendar crush, all / VV gate | +3.4 / +3.8 | −14.1 / −15.9 | −47.1 / −52.4 |

Crush exit, quintile 5 (most selective): slope (most negative) straddle +9.6% mid / +6.9% f=.25; fwd_ratio (iv_front /
forward vol front→back, = the event premium: the event variance cancels in the forward) calendar +18.0% mid / −10.1%
f=.25, straddle +6.3% mid. VV's ~10%/trade is reproducible only at MID fills on the selective bucket.
Why calendars die on costs: back-month call spread median 76bp of S (11% of its price) vs front 23bp (5.9%), and the
cost is charged against a NET debit (4.9% of S) instead of a gross credit (8.3% of S): entry cost = 24% of the debit
vs 3.3% of the straddle credit.

### SH-19b — single-side calendars (same events, 2,255 with both legs' debit > 0.25% of S)

The 'both' calendar = call calendar + put calendar at the same ATM K; by put-call parity the two are nearly the same
position (C−P is a forward; the front and back forwards nearly cancel), so 'both' only doubles the legs.
% of debit, per-trade mean:

| | both mid | call mid | put mid | both f=.25 | call f=.25 | put f=.25 |
|---|---|---|---|---|---|---|
| crush exit | +1.1 | +3.1 | +5.0 | −14.5 | −12.1 | −15.2 |
| to expiry | +4.4 | +5.5 | +10.9 | −8.8 | −7.6 | −7.0 |
| crush, fwd_ratio Q5 | +11.3 | +14.1 | +17.5 | −11.4 | −7.2 | −14.4 |

fwd_ratio quintiles (crush, mid) rise monotonically for every variant: call −4.6/−1.3/+1.4/+5.9/+14.1.
Entry cost (natural − mid) median: call 20% of debit, put 25%, both 24%. Use the OTM side: strike ≥ spot -> call
calendar costs 18% vs put 27%; strike < spot -> ~equal (22% / 23%). Mid looks better for puts, but put spreads are
wider so their mid is the less trustworthy price. Conclusion: one side (the OTM one) suffices; every calendar variant
is negative at f=.25 even in Q5 -> viable only with maker fills at/inside mid (needs intraday quotes to model:
fill-at-mid is conditional on the price moving against us).
⚠ VV's slope as implemented: ts_slope_0_45 = (iv45 − iv_front)/(45 − dte_front), vol (not variance) linear in DTE —
from memory of his published script, unverified. fwd_ratio (variance-based forward) is our addition.

## SH-20 — event variance: implied earnings move vs history, LONG (k=10) and SHORT (k=1) straddles (user 2026-11-11)

Scripts `scripts/options/event_vol.py` (trades, `data/options/event_vol/trades.parquet`), `event_vol_report.py`,
`event_vol_short.py`; logs `logs/options_event_vol*.log`. 2022-12 -> 2026-10, ~370 names.
Decomposition per day: E1 = first expiry >= reaction day R, E2 = next (<= 45 days later, before the next event);
w_i = iv_i^2 T_i (T in TRADING days/252) = sa2 T_i + se2 (event = a jump): sa2 = forward variance, imp_move = sqrt(se2).
hist_rms = RMS of the stock's previous 4-8 reaction-day moves (known at entry); ratio = imp_move / hist_rms; se2 <= 0 ->
ratio 0 ('no event priced'). Cross-check: the pre-event weekly's IV / forward vol median 0.97, corr of the two implied
moves 0.83.

**Calibration (k=10, deciles of ratio)**: realized/implied RMS move falls monotonically 1.43 -> 1.09 -> 0.97 ... 0.62 ->
0.26; P(real > implied) 56% -> 3%. The ratio PREDICTS which events are under/over-priced.

**LONG straddle, k=10 entry (unhedged, % of premium; A = R-1 close, B = R close, C = expiry)**: only the cheapest
decile (ratio ~0.5) is positive: B +5.4% at mid (sd 79%, t 1.0, median -24%), +1.6% at f=.25, -2.0% at f=.5. All
events: A +0.2 / B -1.3 / C -4.0 at mid, -3.6 / -4.9 / -5.5 at f=.25. The ratio-target exit (D) and the +25% P&L target
(E) do not change that. Delta-hedged: same picture, smaller. By year (deciles 1-2, B f=.25): 2023 -2.2, 2024 +7.7,
2025 -11.5, 2026 -5.5.

**SHORT straddle, k=1 (VV's entry, R-1 close; % of credit, f=.25)**: B (crush) / C (expiry): all -7.5 / -3.5; VV gate
-1.7 / +1.65; ratio top 10% +1.1 / +8.3 (t 0.3); VV gate & ratio top 20% -3.3 / +3.9. Deciles 2-10 are not monotone.
ratio is nearly independent of VV's features (spearman 0.24 with fwd_ratio, -0.11 with the slope, 0.03 with IV/RV).

**⚠ 'No event priced' (se2 <= 0) is mostly a DATA artifact, not a signal**: 392 events at k=10, 163 at k=1. LONG: +9.8%
(B) / +15.3% (C) at f=.25; SHORT: -60% / -54% of credit (worst -3956%: tiny 2-day credits vs surprise moves). These are
often NOT scheduled earnings: Item 2.02 also covers deliveries/production updates, preliminary results and
pre-announcements (LCID 2026-10-06, FCX 2024-07-02, OXY 2024-07-11); only 46% (k=1) have a regular 70-110 day gap to the
previous filing vs 82% otherwise. A trader cannot know an unscheduled event is coming (and before the company confirms
the date, neither can the market): LOOKAHEAD. The same events also sit in the SH-18/19 EARN samples, where they were
losses for the short.
Next: a SCHEDULED-earnings filter (cadence 70-110 days and/or the market pricing an event at entry, se2 > 0 — both
live-knowable); ideally a real earnings calendar with announcement dates.

### SH-20b — fwd_ratio on the long side, why the short fails, and an audit of 'no event priced' (user 2026-11-11)

- **ThetaData has no earnings calendar** (v3 docs: stock / option / index / rate / MARKET calendar only; every
  earnings path 404s on the Terminal).
- **fwd_ratio = iv_front / forward vol (se2 > 0 only), LONG**: k=10 decile 1 (fwd ~1.05, the event barely priced)
  A/B/C at f=.25 = +3.1 / +6.1 / +2.5% of premium (B t 0.6); deciles 2-10 mostly negative. k=5 decile 1 +1.4% (B).
  Same picture as the history ratio: only the cheapest decile, not significant after costs.
- **Same events at k=10 vs R-1** (k=10 ratio deciles): the implied move converges toward history by R-1 (decile 1:
  6.0% -> 10.1%; decile 10: 9.2% -> 6.4%); real/imp at R-1 is 0.95 (d1) .. 0.54 (d10). The run-up that exit A wants
  happens, but the front's ambient variance decays over the same days (w1 = sa2 T1 + se2 roughly unchanged), so the
  straddle's value barely moves.
- **Why the SHORT fails (k=1, expiry, f=.25)**: in EVERY ratio decile the median trade wins +12..+30% of credit and
  56-64% win, but 8-15% of trades lose > 100% of credit and the worst 5% average -175..-264%, which takes the mean to
  -8..+8%. The ratio raises the median (decile 10: +29.9%) but does not lower the tail frequency (10.3%). History does
  not predict the jumps; the market at R-1 already prices the event about right on average. Short by fwd_ratio: decile 1
  (event cheap at R-1) -18% (avoid), deciles 7-10 ~ +1..+2% (B).
- **Audit of 12 random 'no event priced' events (k=1, se2 <= 0), from the 8-K exhibits**: 7-8 are NOT scheduled
  earnings (ENVX strategic realignment, C3 AI preliminary results + CEO change, NCLH operations update, ABBV IPR&D
  guidance update, TSLA deliveries, RILY delayed 10-Q / preliminary, CRWD layoff memo, ASST); 2 are scheduled earnings
  on low-event names (AGNC, NEE); **2 are TICKER-MAPPING ERRORS in earnings_8k202** (GOLD 2023 = Barrick's prices with
  A-Mark/Gold.com's filings — the ticker moved in 2025; PARA 2025-04 carries a Banzai filing). The 444-ticker file
  resolved each ticker's CIK at TODAY's date: reused tickers need the point-in-time fix (done for the md500k pull).

## SH-21 — scheduled earnings only: EDGAR releases matched to 10-Q/10-K, the audit, event-vol rerun (2026-11-11)

**Earnings dates: what we pull.** Only EDGAR's filing INDEX (form, date, acceptance time to the second, item numbers,
report period), never the reports. The Item 2.02 8-K's acceptance time IS the release time (BMO/RTH/AMC). Since
2026-11-11 the whole index comes from the SEC's nightly bulk `submissions.zip` (1.6 GB, ~1M company files; 30 s
download; `scripts/options/sec_bulk.py` -> `data/sec/filings.parquet` in 70 s: Item 2.02 8-Ks + 10-Q/10-K/10-KT,
532k filings, 14.6k CIKs, 2015 ->) instead of an hour of per-company requests. www.sec.gov needs a contact email in
the User-Agent (the user's, 2026-11-11). Ticker -> CIK on a date still needs Massive (the SEC knows current tickers only).

**Scheduled = the quarterly results release** (`scripts/options/earnings_scheduled.py`; `--bulk` = no requests): for
each 10-Q/10-K (period end P, filed F) the release is the LAST Item 2.02 8-K in (P, F]; if none, the FIRST in
(F, F+3]. (An extra 2.02 the day after the release is common - SAVE 2023-02-07, UAMY 2026-05-15 - so the slack is only
a fallback; the first version took the last in (P, F+3] and lost those two releases.) Labelled per ACCESSION (GOOG and
GOOGL share a CIK - the first version labelled only one of them). The 10-Q date only says WHICH 8-K was the release;
live, the earnings calendar announces it weeks ahead. 444 option names: 91% of Item 2.02 filings are releases;
release-to-release gap median 91 d (5-95%: 70-112). md500k universe: 222,942 of 249,673 (89%).

**Audit** (`scripts/options/earnings_audit.py`, logs/options_earnings_audit.log: exhibit headline + full-text markers),
2023 ->, 15 random each:
- DROPPED (559 filings): 13/15 are not scheduled results - AEO 'expects Q2 above guidance', APA supplemental info,
  ASTS / MULN / SPWR (CSLR) / WAL preliminary results, OXY 'earnings considerations', CVX 2024-01-02, NKLA deliveries,
  SAVE investor update, MDB CEO change + prelim, IBRX revenue update, HOOD order-routing report. 2/15 were real
  releases (SAVE 2023-02-06, UAMY 2026-05-14) -> the rule fix above; both are now labelled releases.
- RELEASE chosen among > 1 candidates (488): 15/15 the real 'Reports Qn Results'.
- RELEASE alone (control, 5,689): 15/15 real results releases.

**Event-vol rerun on releases only** (`event_vol.py --earnings data/options/earnings_sched.parquet --releases-only`
-> data/options/event_vol_sched/; logs/options_event_vol_sched_{report,short}.log):
- 'No event priced' (se2 <= 0) at k=1: 163 -> 70; at k=10: 392 -> 317. Their LONG 'edge' was the unscheduled events:
  B/C at f=.25 +9.8 / +15.3% -> **-0.6 / -0.1%**. Confirmed lookahead, gone.
- LONG (k=10): still nothing after costs. Cheapest ratio decile B f=.25 -2.0% (was +1.6). Calibration unchanged
  (real/imp RMS 1.42 -> 0.33 across the ratio deciles).
- **SHORT (k=1, R-1 close, held to the E1 expiry, median 2 days), % of credit at f=.25:** all 2,586: +1.3 (t 0.8; was
  -3.5). Ratio deciles 4-10: +4.0 (t 2.35); deciles 1-3: -5.3. **Decile 10 (ratio >= 2.3): +12.9% (t 2.76), +10.1% at
  f=.5, median +31.6%, positive every year (2023 +9.1, 2024 +14.7, 2025 +10.3, 2026 +16.5)**; 9.7% of its trades still
  lose > 100% of credit. Decile 9 alone is weak (deciles 9-10 +5.4, t 1.7) -> partly a threshold on noise, and one of
  many cuts looked at.
- ⚠ Decile 10 is mostly cheap stocks: S < $10 +24.5% (n 88, entry spread 10% of mid), $10-30 +5.4 (n 109), > $30 +9.4
  (n 62); S >= $10: +6.9% (t 1.2), negative at natural fills (f=1). Concentrated: 85 names, AMC / LUMN / AGNC / MPW /
  RIG ~10 events each; without the best 5 names +7.4%. Largely short-dated options on low-priced, high-IV names priced
  off a wide spread - the fill assumption decides it.

## SH-22 — VV pre-earnings LONG vol, his four features (scripts/options/vv_long.py, vv_long_report.py; user 2026-11-12)

Spec from the VV video: entry = session closest to (announcement - 14 cal days, +-4), exit R-1 close (before the
announcement), ATM straddle of the first MONTHLY expiry >= R, scheduled releases only (earnings_sched is_release).
Implied move with the event = ONE trading day: iv1^2 T1 = s_amb^2 (T1 - 1/252) + v_ev; ambient = the IV of the last
pre-event expiry (>= 3 td) if any (VV's preference, 99% of events) else the forward vol E1->E2; iem = sqrt(2/pi) sqrt(v_ev)
= expected |reaction-day move|. Realized = |V_R/V_{R-1} - 1|. Features: iem / iem(prev event), iem / mean(iem all prev),
iem - real(prev), iem - mean(real all prev; EDGAR history from 2015). iem_pre vs iem_fwd corr 0.69, median ratio 1.03.

**Sample: 3,450 events, 416 names, 2023-01 -> 2026-10** (eod_universe is only partly pulled for ~half the names).
Baseline: mean +2.7% mid / -0.7% f=.25 / -3.8% f=.5, median -5.0% (VV: +0.30% mean, -6.3% median on 21k trades). Median
half-spread 2.5% of mid. Calibration: mean iem 7.8% vs mean realized 7.1%, P(real > iem) 37%.

| feature | spearman / pearson (ret mid) | decile 1 mid / f=.25 | decile 10 mid / f=.25 |
|---|---|---|---|
| implied_hist_avg_implied_ratio | -0.016 / -0.020 | +9.2 / +2.6 | +0.9 / -2.9 |
| gap_implied_hist_avg_realized | -0.041 / +0.025 | +1.6 / -2.9 | +6.8 / +1.0 |
| implied_to_last_implied_ratio | -0.005 / +0.002 | +10.5 / +4.5 | +1.7 / -2.1 |
| gap_implied_last_realized | -0.067 / +0.003 | +4.7 / +1.3 | +3.2 / -0.9 |

The two implied-ratio features have VV's shape at the cheap end only (decile 1 ~+10% mid, t < 1 at f=.25) and it is
year-unstable: deciles 1-2 at f=.25 +5/+15 in 2023/2024, -8/-4 in 2025/2026. The gap features show nothing. None
reaches VV's |r| 0.05-0.10. Underpowered (3.4k vs 21k, 4 years): rerun on the full-market pull (VPS, 2012 ->).
Earnings calendar: Massive has a Benzinga earnings endpoint (confirmed/projected date status) — NOT in our plan.
- **Ratios instead of gaps (user)**: iem / mean(real all prev): spearman -0.034, deciles not monotone, decile 1 (< 0.73)
  -4.2% f=.25, decile 10 (> 2.3) +2.9%. iem / real(prev): spearman -0.053 (the closest to VV's range), but again not
  monotone: decile 2 +2.8% (t 1.8), decile 8 -5.9%, decile 10 (> 8.5, a near-zero last move) +3.5%. No edge at the cheap end.
- **The other reading of VV's realized features (user): IV vs realized VOL.** iv_em = the traded straddle's IV at entry;
  realized vol of each event's own trade window (entry = ann - 14d -> the monthly expiry >= R, incl. the reaction day,
  'win'; or entry -> R-1, 'pre'), annualized RMS of daily log returns, daily_adjusted 2015 ->; last = previous event, hist
  = mean of all previous; plus rv30 at entry. Medians: iv_em 0.57, rv_win 0.44, rv_pre 0.32-0.34, rv30 0.35.
  **The window version is the first feature with VV's correlation**: spearman(ret mid) iv - hist_avg_rv_win -0.104,
  iv - last_rv_win -0.097, ratios -0.082 / -0.075; negative in every year (2023 -.08, 2024 -.14/-.12, 2025 -.13/-.13,
  2026 -.02/-.06). Pearson is ~-0.01 (the right tail of the long straddle's returns). Bottom 40% vs the rest at f=.25:
  iv - last_rv_win +0.86% (t 1.15) vs -1.84%, difference t 2.18; iv - hist_avg_rv_win +0.53 vs -1.61, diff t 1.74.
  The low bucket beats the rest in 7 of 8 year x feature cells, but is itself negative in 2025/2026 (the whole trade
  decays: the 'rest' falls from +3.3 in 2024 to -3 / -5). 'pre' versions and rv30 are weaker (|spearman| <= 0.07, rv30 +).
- **The six combined (user)**: implied/hist-avg-implied, implied/last-implied, iv vs last / hist-avg rv_win (ratio and
  gap). 3,003 events have all six. Spearman: the two implied ratios 0.70 with each other, ~0 with the four IV-vs-RV (which
  are 0.4-0.9 among themselves) = two independent families. Gate 'all six in the cheapest q' (full-sample percentiles):
  q=10% n=11 (-3.3 f=.25), 20% n=38 (-1.4), 30% n=108 (+2.0), 40% n=205 (+3.9, t 1.96), **50% n=358 +6.7 mid / +4.0 f=.25
  (t 2.53) / +1.4 f=.5, by year +4 / +11 / +0 / +2**. Composite = mean percentile of the six: spearman -0.100, decile 1
  +7.5 mid / +4.4 f=.25 (t 2.38) but 2025/2026 ~0; deciles 2-10 ~flat to negative (d7 -5.3, t -3.9). The edge is in the
  cheap tail only, and thin after costs. ⚠ Percentile thresholds are full-sample; a live gate needs expanding thresholds.
- **Four instead of six (user: drop the IV/RV ratios)**: the 2 implied ratios + the 2 IV-RV gaps. Barely changes the
  gates (ratio and gap are 0.9 correlated, so gating both cost little): all four in the cheapest 40% n=240, +7.5 mid /
  +4.6 f=.25 (t 2.47) / +1.8 f=.5, by year +8 / +9 / +0 / +1; 50% n=440 +3.2 (t 2.36). Gaps beat the ratios (ratio set:
  40% +4.2 t 2.11, 50% +2.6 t 1.89). Composite spearman: 4-gap -0.087, 4-ratio -0.072, all six -0.100.
- **Implied-move GAPS instead of ratios (user)**: iem - last_iem / iem - hist_avg_iem are 0.97 rank-correlated with the
  ratios; singly the same (spearman -0.02, cheapest 10% +3..+4% f=.25, t < 1, 2024-driven). In the 4-gate (with the IV-RV
  gaps): 40% n=245 +3.4 (t 1.92) vs the ratio version +4.6 (t 2.47); 50% +3.4 (t 2.46) vs +3.2. Keep the ratios.

## ⚠ ERRATUM 2026-11-12 — returns from the fused `close*n + cum_div` level were SHRUNK (fixed: scripts/options/tr_index.py)

`daily_adjusted` IS the causal table (docs/price_adjustment.md), but event_vol.py, vv_long.py, pead.py (and the new
skew_vert.py) took returns as V_t/V_{t-1} - 1 of V = P n + C. The documented return divides by P n, not P n + C, so
every return was scaled by P n / (P n + C), C = all dividends since the ticker's first row: median row 0.98, but 34% of
rows have C/V > 0.1; realized vol AGNC 0.24x the truth, MPW 0.41x, RIG 0.37x, T 0.49x, MO 0.56x (AAPL 0.95x). yz_vol.py
was right. Everything re-run with the causal TR index (r = [(P n)_t - (P n)_{t-1} + dC] / (P n)_{t-1}):
- **SH-20/21 SHORT k=1, held to expiry**: the dividend payers' history ratio was inflated (hist_rms too small) -> they
  filled decile 10 (AGNC / MPW / RIG / LUMN). Fixed: all +1.1% f=.25 (was +1.3); ratio top 10% (>= 1.98, was 2.3)
  **+8.4% f=.25 (was +12.9)**, t 2.73 on n 355, by year -0.4 / +11.5 / +14.0 / +6.8; **S < $10 +23.5% (t 4.0, n 115),
  S >= $10 +3.0% (t 0.7)** -> it is the cheap-stock effect, not the event ratio. Top names now FHN / CHPT / PCG / ALB.
- **SH-22 VV long**: IV-RV gaps spearman -0.089 / -0.087 (was -0.097 / -0.104); 4-gate 40% n 211 +2.8% f=.25 (t 1.77;
  was +4.6, t 2.47), 50% n 397 +2.5 (t 1.85); composite -0.082. Weaker, same direction.
- **PEAD md500k sched** (docs/pead_results.md): up5_v2.5 xs_20 +0.24 (t 1.9; was +0.32), hi52 +0.44 (t 2.4) / +0.55 at
  10d (t 3.6), gap_hold +0.61 (t 3.1; was +0.74), down5 -0.44 (was -0.32).

## SH-23 — VV skew verticals (scripts/options/skew_vert.py, skew_vert_report.py; user 2026-11-12)

VV (oquants skew video): buy the ~50-delta option, sell the rich ~25 / ~15-delta wing of the same expiry when ATM IV <
RV < wing IV and the skew is steep; calls into call skew, puts into put skew; momentum layered on top. Ours: one
entry per (root, expiry) at the session with calendar DTE closest to 10 in [5, 15], held to expiry (intrinsic), IV per
option from the NBBO mid (BS r=q=0, trading-day T), skew = (atm - wing25)/atm (more negative = steeper), skew z = vs
its own previous 60 sessions (10-day constant tenor). 190k spreads, 512 roots, 2022-12 -> 2026-10; earnings inside the
life excluded below. Return = payoff / debit - 1; f = fill fraction of both legs' half-spreads (median hs = 8-11% of
the debit: fills dominate). 'excess' = return minus the same leg's average on the same expiry (removes the market's
direction: raw call spreads won 2023-25 and lost 2026, puts the mirror); t-stats clustered by expiry date.

| f=.25, no earnings | n | raw | excess (date-t) |
|---|---|---|---|
| c25 / c15 / p25 / p15 all | 41k each | -0.8 / +2.6 / -6.1 / -4.1 | 0 |
| **ATM < RV20 < wing** c25 | 1,363 | +0.8 | **+6.5 (1.1)** |
| c15 | 2,993 | +13.0 | **+11.5 (2.4)**, by year +19 / +15 / +12 / +2 |
| p25 | 3,083 | +3.0 | **+8.6 (2.4)**, by year +6 / +14 / +12 / +5 |
| p15 | 5,659 | +3.9 | **+7.8 (1.6)** |
| gate & skew z <= -1 | 412-813 | | c25 +15.0 (1.5), c15 +13.8 (1.7), p25 +11.3 (0.9), p15 +5.4 (0.1) |
| skew z >= +1.2 (FLAT vs its history) | ~4k each | | **-5.6 / -4.2 / -5.1 / -2.8 (t -3.8 / -2.5 / -3.2 / -2.0)** |

- **The RV gate is the signal**: positive excess on all four legs, most years. The skew LEVEL alone does nothing (its
  steep decile is the illiquid names, hs 22% of mid); the skew z adds little on top of the gate, but its other end is
  the cleanest single result: don't buy a vertical into a skew that is flat vs its own history.
- f=.5 roughly halves the raw numbers (c15 gate +6.0, p25 gate -2.2): execution decides it.
- **Momentum** (ret_63/126 aligned with the spread, OLS ln-price slope, OLS ln-volume slope on calendar time) inside
  the gate: no consistent help (calls: LOW aligned momentum better; p25: high aligned 126d better, +10.2 t 2.6). Not VV's
  reported effect on this sample.
- ⚠ 4 years; the four legs overlap (the same root/expiry); results before the 2012-> pull are provisional.
- **The SHORT side (user: sell ATM, buy the wing when the skew is flat / inverted)**: return on capital at risk
  (width - credit), f=.25, no earnings. Skew > 0 (wing IV below ATM) is common: 37% of all spreads, 7-11% above +0.05.
  Absolute: all short verticals -0.5..-3.5; skew z >= 1.2: c25 -0.5, c15 -1.6, **p25 +4.4 (t 1.7; +1.1 at f=.5)**, p15
  +2.5; the mirror gate ATM > RV20 > wing: c25 +2.9, c15 +1.3, p25 +2.5, **p15 +7.6 (t 3.2)** but -0.7..-6.7 at f=.5.
  Excess over the same leg/expiry: z >= 1.2 +1.1..+3.7 (t 1.8-3.4), mirror gate +3.9..+11.5 (t 2.3-3.7), positive in
  most years. Real relative signal, but after the doubled spread cost the absolute edge is ~0 except put credit spreads.
- **Why the skew z is not a lever inside the gate (user)**: the gate already selects steep skew (median own skew -0.087
  vs -0.015 overall for c25; z median -0.52), and z is 0.64-0.71 rank-correlated with the skew level. Gate x z quartile
  (excess, f=.25): c25 +19.2 / +1.7 / +11.9 / **-6.3**; c15 +11.4 / +13.8 / +13.0 / **+5.6**; p25 +8.8 / +11.6 / +9.3 /
  **+4.3**; p15 +7.1 / +11.8 / +11.3 / **+1.0** -> z works as a VETO (the flattest quartile is the worst on every leg),
  not as a booster (Q1 ~ Q2 ~ Q3). The z tail is noisy (the 60d std of a 10-DTE skew can be tiny: z down to -118).
  The gate MARGIN = min(RV/iv_long - 1, iv_short/RV - 1) is the better lever: c15 terciles -0.6 / +15.0 / +20.1, c25 -10.3
  / +16.8 / +13.0, p15 +3.8 / +4.4 / +15.3 (p25 flat +7.2 / +10.3 / +8.3).
- **Which leg makes the money (user)**: leg prices at mid from the leg IVs (reconstruct the quoted mid exactly), P&L in
  % of the spread debit, 'hedged' = each leg's entry delta hedged in stock to expiry (removes direction). In the gate
  (ATM < RV20 < wing), hedged: c25 long -1.6 / **short wing +20.4 (t 3.9)**; c15 long +5.3 (t 0.8) / **short +11.6
  (t 2.0)**; p15 long +7.3 (t 1.6) / **short +14.0 (t 4.9)**; p25 **long ATM put +15.9 (t 2.3)** / short +3.1 (t 0.5).
  Gate & margin top 2/3: short wing c25 +29.9 (t 3.9), c15 +20.2 (t 4.4), p15 +18.7 (t 5.6); p25 long +18.8 (t 2.9).
  -> mostly the SHORT RICH WING carries it (the 'wing IV > RV' half of the gate); the long ATM leg is mainly the hedge,
  except the 25-delta put spreads where the cheap ATM put pays. Outside the gate both legs are ~0 hedged.
- **Skew z ALONE, no RV gate (user)**: deciles of the own-side skew z, no earnings. Vertical excess (f=.25): the flat end
  is reliably bad (d10 c25 -5.6 t -3.8, c15 -4.0 t -2.4, p25 -5.4 t -3.3, p15 -3.2 t -2.2); the steep end is weak and
  noisy (d1 +6.9 t 0.2 / +2.4 t -1.5 / +4.5 t 2.3 / +0.4 t 0.9; spearman -0.002..-0.035). Raw put spreads stay negative
  (market drift). BUT the hedged SHORT WING (mid, % of the spread debit) at the steep end: **p25 d1 +20.0 (t 5.7), z <=
  -1.5 +24.2 (t 5.5); p15 d1 +10.9 (t 5.3), z <= -1.5 +11.9 (t 5.1)**; calls weaker (c25 z <= -1.5 +12.3 t 2.0, c15 +7.2
  t 1.8). The hedged long ATM leg at the steep end is negative (p25 d1 -8.1, c15 -4.8) and cancels it inside the vertical.
  -> steep put skew vs its own history marks an overpriced put WING; the vertical is the wrong wrapper for it -> next:
  the short wing alone (delta-hedged, capped farther out), with costs.
