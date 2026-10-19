# Stock data: the `trades` and `intraday_1s_slim` parquet files

There is one file per trading day: `{YYYY-MM-DD}.parquet`. Both folders cover **2016-08-08 → 2026-10-16** (2,563 days)
and are extended daily. All US-listed stocks are included, plus ETFs and other types. Prices and sizes are **raw**
(as traded, not adjusted for splits or dividends) in both folders.

The parquet footer gives the column names and physical types. It does not give the units, the clock, which trades
were kept, or the price scale. That is what this page documents. It also covers a **type drift** across the
`trades` files, which an ingest module has to normalise (§1.2).

## 1. `trades/` — every trade print, as published by the SIP

Source: Massive's (formerly Polygon) flat files `us_stocks_sip/trades_v1/{Y}/{M}/{D}.csv.gz`. Each day's CSV is
converted to parquet unchanged (`TradingEdge.Massive/S3Download.fs`), except for `conditions`, which is parsed from
the CSV's comma-separated string into a list. The rows are in the flat file's order: by `ticker`, then
`sip_timestamp`. The rows are **unfiltered**: corrections, odd lots, late and out-of-sequence prints, and all
venues are included. About 130 M rows per day in 2026.

| column | type | meaning |
|---|---|---|
| `ticker` | string | The symbol **as of that day** (point in time). Renames change it; the same company can have different tickers on different days. |
| `conditions` | list\<uint8\> | The trade's sale-condition codes (CTA/UTP, in Massive's numbering; see `GET /v3/reference/conditions`). Empty list = a regular trade. The ones that matter most are in §1.1. |
| `correction` | int64 | Correction indicator. 0 = a normal trade. Non-zero values are rare (~4k of 131 M rows on 2026-10-16: codes 1, 8, 10, 12). |
| `exchange` | int64 | The venue, as Massive's exchange id (`GET /v3/reference/exchanges?asset_class=stocks`). **4 = an off-exchange report (FINRA TRF)**; `trf_id` then says which TRF. Every other id is a lit exchange. |
| `id` | int64 *or* string (§1.2) | The venue's trade id; **often NULL in 2016–17**. It is **not unique** across venues; use (ticker, exchange, id) as the key, and even that is not guaranteed. |
| `participant_timestamp` | int64 | When the venue executed the trade. **Nanoseconds since the Unix epoch, UTC.** |
| `sip_timestamp` | int64 | When the SIP (the consolidated tape) published it. Same unit. This is the clock a live feed sees. |
| `trf_timestamp` | int64 | When the trade was reported to the TRF. Same unit. **0 for lit trades** (`trf_id = 0`). |
| `price` | double | The trade price in dollars, raw. |
| `size` | int64 *or* double (§1.2) | Shares. **Fractional from 2026-02-23** (TRF retail odd lots, e.g. 0.005713); never truncate to an integer. `size = 0` occurs (tens of thousands per day) and means nothing. |
| `sequence_number` | int64 | The SIP's sequence number. Increasing within a ticker, but **only nearly unique**; don't use it as a key. |
| `tape` | int64 | The listing tape: **1 = A (NYSE-listed), 2 = B (NYSE Arca / NYSE American / regional-listed), 3 = C (Nasdaq-listed).** |
| `trf_id` | int64 | 0 = a lit exchange. Otherwise the TRF that took the report: **201 = FINRA/NYSE TRF, 202 = FINRA/Nasdaq TRF Carteret, 203 = FINRA/Nasdaq TRF Chicago.** |

### 1.1 Condition codes that matter

| codes | meaning | what the 1s bars do with them (§2) |
|---|---|---|
| 17, 25 | opening prints (market-center opening trade, opening prints) | always kept |
| 19, 8 | closing prints (market-center closing trade, closing prints) | always kept |
| 2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53 | average-price, cash sale, derivatively priced, extended hours sold out of sequence, next day, price variation, prior reference price, seller, sold out of sequence, contingent, qualified contingent | excluded |
| 12 | Form T (an extended-hours trade) | kept (every premarket and after-hours trade carries it) |
| 37 | odd lot | kept (odd lots are a large share of modern volume) |

⚠ Massive's live feed also emits codes 62–71 (issuer-status flags). They are a separate namespace, and the bulk
history stops at 53.

### 1.2 ⚠ The type drift (the footers disagree)

DuckDB inferred the CSV column types per file, so they vary from day to day:

| `id` | `size` | days |
|---|---|---|
| string | int64 | 2016-08-08 → 2017-11-03, except 2016-12-23, 2016-12-27 and 2017-10-31 (313 days) |
| int64 | int64 | those 3 days, and 2017-11-06 → 2026-02-20 (2,085 days) |
| int64 | double | **2026-02-23 → today** (165 days so far) |

Why:

- **`id` as a string:** most of those days' ids are **missing**. On 2017-06-15, 21.6 M of 32.2 M are NULL, and the
  rest are numeric.
- **`size` as a double:** it starts on the first day the flat files carry **fractional shares**. Before
  2026-02-23 sizes are whole shares; from then on, millions of TRF retail prints a day are fractional.

An ingest module should cast on read: `id` → nullable int64 (or string), `size` → double.

## 2. `intraday_1s_slim/` — 1-second bars built from `trades/`

Built by `scripts/conversion/build_all_1s_bars.fsx`. The filter, clock and bucket rules live in
`TradingEdge.Orb/Bars.fs` and are shared with the live bar builder, so backtest and live agree by construction.
One row per (ticker, second) **with at least one kept trade**. A second with no trades has no row; there is no
forward fill. The rows are sorted by `ticker, bucket`. zstd-9. About 26 M rows per day in 2026.

| column | type | meaning |
|---|---|---|
| `ticker` | string | As in `trades` (point in time). |
| `bucket` | int32 | **Seconds since 00:00 America/New_York on that date** (DST-aware). The bar covers `[bucket, bucket + 1)`. 04:00 = 14400, **09:30 = 34200**, 10:00 = 36000, 16:00 = 57600, 20:00 = 72000. The bars run 04:00 → 20:00 ET (pre-market, regular hours and post-market). Minute = `bucket // 60`, 10 s = `bucket // 10`. |
| `vwap` | float32 | Σ(price × size) / Σ(size) over the second's kept trades. **Raw dollars** (not adjusted). It is the only price: there is no OHLC. |
| `volume` | float32 | Σ size over the kept trades: shares (fractional from 2026-02-23). |
| `trade_count` | int32 | The number of kept trades. |

**The clock.** A trade's second is taken from `sip_timestamp`, or from `participant_timestamp` when the SIP
timestamp is 0. Timestamps are **truncated to milliseconds** first (the live feed's resolution); the `.precision`
marker in the folder says `MILLISECOND`.

**Which trades are kept.** All venues, TRF included (no `trf_id` filter). A trade is kept if `size > 0` and
`price > 0`, and then:

- it is an opening or closing print (17, 25, 19, 8), which is kept **unconditionally**, because auction prints are
  disseminated late but are the session's anchor prices; **or**
- it was published within **50 ms** of its execution (`sip − participant ≤ 50 ms`, the check skipped when either is
  0) **and** it carries none of the excluded conditions in §1.1.

The 50 ms cap removes TRF prints that are reported seconds or even ~45 minutes late. Under a SIP clock those would
land in the wrong second. With the cap in place the bars keep ≈ 93 % of all volume (≈ 84 % of TRF volume).

**Things to know when using them:**

- The closing auction print (16:00) can land in a 15:59 or 16:00 bucket. Handle it explicitly if a study needs
  the close.
- Early-close days (13:00 ET) are not marked in the files. Use a market calendar.
- `float32` vwap has ~7 significant digits. That is fine for returns, but recompute from `trades` if you need
  exact cents on high-priced names.

## 3. Prices are raw, so splits and dividends are for the ingest to apply

Neither folder is adjusted. Our research code adjusts **at load time**, and we learned the hard way that how it's
done matters:

- **Backward adjustment is a lookahead.** The textbook adjustment (scale history by every later split, `adj_close /
  raw_close`) uses information from after day D. That is harmless inside a ratio of two adjusted prices on the
  same scale. But in a filter or gate (e.g. "price ≥ $1") it silently selects stocks that will reverse-split
  later. This cost us a whole system's results once.
- **Backward dividend adjustment can push prices negative.** Subtracting every later dividend from the past
  prices (the additive method) takes a long-paying stock's early history below zero once the cumulative
  dividends exceed the old price. Then logs, returns and price gates all break. The multiplicative method stays
  positive but is still a lookahead.
- **What we use instead is a causal forward scheme** (`docs/price_adjustment.md`). Each day keeps its raw price `P`,
  a cumulative split factor `n` built **only from splits up to day D**, and a cumulative dividend term. Every value
  depends only on events up to D, so this class of bug can't happen.

For an ingest module that means:

- store the raw values (as these files do);
- carry the split and dividend factors as separate, as-of-D columns;
- derive any adjusted series from them, never overwriting the raw prices.
