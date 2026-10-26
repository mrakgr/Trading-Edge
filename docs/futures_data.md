# Futures data — Sierra Chart level-1 trades → 1s bars per contract (2026-10-26)

## The source

`/mnt/d/trading-edge-bulk/futures/level1_tick/<ROOT>/<year>_<CONTRACT>.parquet` (26 GB, 1,742 files, 24 roots), exported
from Sierra Chart. **One file per contract, NOT rolled.** The year in the name is the contract's expiry year. Each file
runs from ~3 weeks (ZN / ZB / HG / MGC ~5–6) before the contract becomes the front month until its expiry, so
consecutive contracts overlap, each at its own price (the calendar spread):

| day | contract | volume | last |
|---|---|---|---|
| 2025-03-10 | MESH25 (front) | 2.24M | 5602.00 |
| | MESM25 (next) | 44k | 5652.50 |
| 2025-11-14 | CLZ25 (front) | 171k | 59.95 |
| | CLF26 (next) | 108k | 59.81 |
| 2025-05-22 | ZNM25 (front) | 1.92M | 109.9375 |
| | ZNU25 (next) | 229k | 109.9688 |

Not enough for back-month or curve studies: only the overlap before each roll.

**Columns:**

- `timestamp`: µs, **UTC**. MES ends at 13:30 UTC on expiry (the 09:30 ET settlement), CL at 19:30 (14:30 ET). ⚠ It is
  a real clock only from 2020-08-23 (see *The clock* below).
- `ticker`: the contract.
- `event_type`: always 3 (a trade).
- `trade_price`, `bid_price`, `ask_price`: × 1e9, converted from a **float32**, so 87.9 is stored as 87900001526.
- `trade_size`.
- `trade_side`: +1 when the buyer aggressed, −1 when the seller did.
- `bid_size`, `ask_size`: always null.

The bid / ask is the quote **before** the trade. Every fill of one sweep carries the pre-sweep quote.

**The roots:**

| group | roots |
|---|---|
| equity index | MES (the MICRO E-mini S&P, $5 / pt: ES's price, a retail-heavier flow; no ES), NQ (the E-mini Nasdaq, $20 / pt) |
| FX | 6A 6B 6C 6E 6J 6N 6S 6Z |
| energy | CL MCL NG RB |
| metals | HG MHG MGC |
| rates | ZN ZB ZT SR3 |
| ags | ZC ZS HE |

**Quality by era (sampled per root):**

| era | timestamps | side | bid / ask | |
|---|---|---|---|---|
| the first file or two of some roots (2008–11) | whole seconds | none | partial | mean size 20–90: aggregates, not ticks |
| to mid-2014 | ms | yes | **bid = ask** in every root (no quote) | |
| mid-2014 → 2020-08-21 | **whole seconds** + a row counter | yes | yes | one row per FILL |
| 2020-08-23 → | a real ms clock + a +1 µs sequence per fill | yes | yes | one row per FILL: a sweep is several rows in one ms |

The quotes begin in the contracts of 2014-05..08 in every root (the share with bid = ask falls from 1.0 to 0.0 across
one or two contracts). ⇒ **From 2015-01-01 only** (user 2026-10-26).

**The clock (measured on every contract from 2015).** A row is one FILL in both eras: one resting order matched by an
incoming one. The mean row size is 1.18 contracts (NQ 2018) and 1.09 (NQ 2025). An incoming order that takes 12 resting
1-lots is 12 rows: one AGGRESSOR EVENT (a sweep).

**Before 2020-08-21:**

- The second is real.
- Below the second, the stamp is a ROW COUNTER: the k-th row of a second is stamped s + k ms (00:20:21.000, .001,
  .002 …). 92 % of NQ 2018's gaps are exactly 1 ms.
- In a second of more than 1,000 rows, the counter stops at .999 and counts µs (.999001 …). It never spills into the
  next second: ZTU20 2020-07-31 18:59:59 has 3,746 rows, 2,747 of them at .999xxx.
- Distinct ms per second = the rows.

**From 2020-08-23:**

- The second is real.
- Below the second, the ms clock is real. The fills of one aggressor event share the ms and are numbered +1 µs: NQ
  2025-07-29 14:31:51.165000 … .165011 is a 12-lot sell sweep through 3 levels.
- Distinct ms per second = the aggressor events (two events in one ms merge).

The switch falls on one weekend in every root (NQ / 6E from 2020-08-23, ZN from 2020-08-21), so it was a change on
Sierra's side. The U20 / Z20 contracts hold both clocks. ⇒ The 1s bars are right in both eras, but sub-second timing and
the sweep structure exist only from 2020-08-23.

⚠ **Prices are on Sierra's scale, not always CME's.** 6J is stored as 0.6916, which is 100 × CME's 0.006916, with a tick
of 0.00005 (CME's 0.0000005). Check every root's scale and point value before computing P&L in dollars.

## The bars: `data/futures/intraday_1s/YYYY-MM-DD.parquet` — the equity corpus's shape plus the delta

`scripts/futures/build_1s_bars.py` (user 2026-10-26: "match those we're using for stocks, with the exception of having
the volume delta"). The layout is `data/intraday_1s_slim`'s: one file per **ET calendar date**, every contract, sorted by
(ticker, bucket).

| column | |
|---|---|
| `ticker` VARCHAR | the contract (`NQZ25`) |
| `bucket` INTEGER | seconds since midnight ET, the equity clock. Globex is shut over every DST change (02:00 Sunday), so no second repeats |
| `vwap` FLOAT | Σ px·size / Σ size, px on the root's grid |
| `volume` FLOAT | contracts |
| `trade_count` INTEGER | fills (Sierra rows): comparable across both clock eras |
| `delta` FLOAT | buy − sell volume. A CROSS fill (bid = ask) counts half buy and half sell, so it adds 0 |

**The cross.** The open's match after the daily break is an auction with no aggressor, but Sierra labels the whole match
with one side: NQ 2025-06-22 22:00:00 UTC is 70 fills / 267 contracts at one price with bid = ask, all −1. The same rule
covers the rare locked moments (bid = ask) during the day. The cross is a median 0.02 % of a contract's volume (max
1.9 %). Against the earlier build without the rule (MHGH26), 61 of 649,838 bars differ in delta, all by exactly their
cross fills' signed volume; 52 of them are at 18:00 ET.

Left out (user 2026-10-26): OHLC, the bid / ask, and the `prints` (distinct ms) count. Sub-second structure exists only
from 2020-08-23 anyway, and 1s bars are the wrong tool for sweep-level ideas.

**How it is built.**
- **Pass 1, per contract:** one copier stages each source to the SSD (4 MB blocks, retried); three DuckDB workers convert
  the staged copies. Each worker spills to its own temp dir: 40 MES / NQ files ran out of memory with no spill dir, and a
  shared one corrupted 33 builds. A finished contract is skipped on a rerun.
- **Pass 2, by date:** the contracts are split into the per-date files, each one sorted, and the bar count is checked
  against pass 1's; then the per-contract files are deleted.
- Pass 2 rebuilds the whole corpus, so adding contracts (ES, MNQ) means rerunning everything: ~16 min.
- A source with no parquet footer (a truncated transfer) is skipped and named.

**Rounding.** Each price is rounded to a per-root grid (`GRID`), a divisor of the real tick. The manifest records the
measured tick and the worst residual in grid units; a file over 0.05 fails the run.
- **Control:** MHG on a 0.001 grid (coarser than its 0.0005 tick) fails every file at 0.5.
- Accepted residuals: up to ~0.02 from float32 noise at the 1e-5 FX grids. ZTM17 holds 2,497 prints at 107.8438034 on
  2017-03-15, 0.027 off the 1/512 grid: a feed artifact, rounded to 107.84375.

**A spot check (MHGH26, one second):** 7 sell fills in 1 ms through 5.5005 / 5.5000 / 5.4995 give vwap 5.49979, volume
7, 7 fills, delta −7. That is the raw tape exactly. Against the earlier per-contract build, all 649,838 bars of the
contract match on vwap, volume and fills.

**`data/futures/intraday_1s_manifest.parquet`** sits BESIDE the corpus, so a `*.parquet` glob reads only bars. Per
contract: `rows_in`, `rows_kept`, `bars`, `first_sec`, `last_sec`, `quoted` (the share of fills with bid < ask, ≥ 0.98 in
every root), `cross_share` (of the volume), `tick`, `max_residual`.

**Verified per file** (`logs/futures_intraday_1s_verify.log`, memory-capped: an all-at-once check of 863 M rows took WSL
down on 2026-10-26):
- 3,632 dates and 862,773,026 bars, equal to the manifest's total;
- no duplicate (ticker, bucket), every file sorted, every bucket in [0, 86399];
- no bar in the Sunday 01:00–03:00 ET window.

**Result:** 1,364 contracts, 6.27 B fills, 863 M bars, 5.0 GB.

⚠ **Negative prices are kept.** CLK20 traded down to −40.32 on 2020-04-20/21 (5,481 fills, 2,052 bars down to a vwap of
−40.16). The first build's `trade_price > 0` filter dropped them silently; since the fix only a null price is dropped. The
front by volume had rolled to CLM20 on 04-17 (CLM20's low: $6.58), so a front series never sees them.

| root | from | to |
|---|---|---|
| most roots | 2015-01 | 2026-03 … 2026-09 |
| CL | 2015-11 | |
| MGC | 2015-03 | |
| MES | 2019-05 | |
| 6Z | 2019-12 | |
| SR3 | 2020-11 | |
| MCL | 2021-07 | |
| MHG | 2022-05 | |

- Two contracts are empty: NGF15 and RBF15 expired in December 2014.
- ⚠ **ZTU26 is missing:** the source file is truncated in the storage box itself (5,000,076 bytes, no parquet footer).
  It needs resending from the source.

## Rolling (planned, not built)

1. **The front contract per root per day** comes from the PREVIOUS day's volume: the next contract takes over the day
   after its volume first exceeds the old one's, and never switches back.
2. **Returns are taken within one contract.** A trade is closed on the contract it was opened on, or exited at the roll,
   so a roll gap (+50 pts on MES above) is never a move.
3. **Long-history features use a FORWARD-adjusted series.** The first contract keeps its prices, and each later contract
   is shifted by the gap at its roll, which is known at that moment (the `daily_adjusted` idea). A BACK-adjusted series
   rewrites old levels with future gaps: a level gate on one is a lookahead (CLAUDE.md rule 4).
