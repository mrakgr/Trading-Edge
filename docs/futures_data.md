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
  It needs resending from the source. (2026-10-28: the raw .scid is complete; see the second batch below.)

### The second batch: Malcolm's raw .scid (2026-10-28)

`home/futures/sierra_data` on the storage box holds Sierra Chart's own intraday files: 2,995 files, 622 GB.
- **What it contains:**
  - the raw form of every contract above;
  - new contracts: ES, VX (VIX futures), the micro FX contracts M6E / M6A / MJY / M6J, one YM and one E7;
  - older CL (2009–2015 expiries);
  - 41 spot FX pairs (227 GB);
  - 659 header-only placeholder files.
- **What we copied** to `/mnt/d/trading-edge-bulk/futures/sierra_scid/` (user: only data from Sierra's sub-second clock
  on; no spot FX; no MNQ, since NQ covers it): the new roots' contracts expiring September 2020 or later, plus ZTU26.
  That is 164 files, 65 GB (the list: `sierra_scid_files.txt` beside it).

| root | contracts | GB |
|---|---|---|
| ES | 23 | 60.6 |
| VX | 69 | 2.8 |
| M6E | 23 | 1.0 |
| M6A | 23 | 0.4 |
| ZT (ZTU26) | 1 | 0.3 |
| YM | 1 | 0.2 |
| MJY | 15 | 0.02 |
| E7 | 1 | 0.01 |
| M6J | 8 | 0.004 |

### The tick tape: `/mnt/d/trading-edge-bulk/futures/ticks/<ROOT>/<year>_<CONTRACT>.parquet` (2026-10-28)

`scripts/futures/build_ticks.py` writes both batches in ONE clean layout (user 2026-10-28). Sierra's tick records reuse
its bar fields (Open / High / Low / Close / NumTrades / BidVolume / AskVolume), and the first export kept them
half-renamed; the new layout keeps only what the data is:

| column | type | from the .scid | from the first batch |
|---|---|---|---|
| `timestamp` | int64 µs UTC | DateTime − 25,569 days | `timestamp` |
| `ticker` | string | the file name | `ticker` |
| `price` | float32 | Close | `trade_price` / 1e9, checked to round-trip exactly |
| `size` | int32 | TotalVolume | `trade_size` |
| `side` | int8 ±1 | which of Ask/BidVolume holds the size | `trade_side` |
| `bid`, `ask` | float32 | Low, High: the quote before the fill (= the price before mid-2014: no quote) | `bid_price`, `ask_price` |
| `fill_pos` | int8 | Open's sub-trade codes, read in sequence; NULL before 2017-07-25 05:21:20 UTC and on a price-form record | NULL (the export dropped Open) |

**Dropped:**
- NumTrades: always 1 on a fill.
- Ask/BidVolume: they only split the size by aggressor; `side` holds that.
- `bid_size` / `ask_size`: always null, because a tick file has no quote sizes. Sierra keeps those only in separate
  market-depth files, and Malcolm sent none.
- `event_type`: always 3.

**`fill_pos`, the sweep marker.** When one aggressive order hits several resting orders, the exchange reports one trade
and Sierra writes a record per fill. It marks the first fill with Open = `0xfd709ef9` (−1.99900095e37) and the last with
`0xfd709f01` (−1.99900197e37). These are Sierra's FIRST/LAST_SUB_TRADE_OF_UNBUNDLED_TRADE constants: absurd prices, so a
code can't be read as a price. Open = 0 is a single fill, or a middle one when it sits between a first and a last.
- **The codes:** 0 single · 1 first · 2 middle · 3 last.
- **Checked on 200,000 orders each in ESH21 and ZTZ25:** every order closes, every fill in it shares the first's side
  and millisecond, and the prices move only in the aggressor's direction.
- **ESH21's last 3M fills:** 876k single fills, and 499k multi-fill orders holding 2.12M fills. 2-fill orders are the
  most common (80k of 200k); some run past 100 fills.
- ⚠ **A lower bound, not the true count:** Sierra treats consecutive single fills at the same millisecond, side and
  price as separate orders, and the file can't show otherwise. In ESH21 that's 22 % of single fills.
- **A recording can stop mid-order:** ZTZ25 ends 2025-09-23 01:46:35 inside a sweep, months before its expiry, and
  ZTU26 stops 2026-07-22 15:00:09 inside one and resumes on 09-01. An order still open across a gap of more than a
  minute is cut there, and a file may end inside one (both counted). A code out of sequence anywhere else fails the file.
- ⭐ **The codes begin 2017-07-25 05:21:20 UTC.** Sierra started writing them within two minutes in every root (6NU17
  05:21:20, 6EU17 05:21:26 … CLU17 05:22:17; checked on the 23 files spanning that day). Before it Open is 0 on every
  fill, which would read as "a single fill": `fill_pos` is NULL there. A code before the boundary fails the file.
  ⇒ the sweep structure exists from 2017-07-25; its sub-second TIMING only from 2020-08-23 (the clock above).
- **The record layout is unofficial:** Sierra removed its file-format page in 2026 ("permanently removed and will not be
  restored"). Everything but `fill_pos` is checked against the first batch's export: 6NZ25, 6ZM25 and ZTZ25 convert
  identical. `fill_pos` rests on the constants' bit-exact match and the order checks above.

**What a .scid holds besides fills (found 2026-10-28 by the first full build, which failed 29 files).** Sierra uses
one record for bars and ticks, and a tick file is not ticks only. A FILL is a record whose size sits in exactly one of
Bid/AskVolume, with NumTrades 1; everything else is dropped from the tape and counted with its span:

| record | where | NumTrades · Bid/AskVolume | Malcolm's export |
|---|---|---|---|
| 1-minute bars (Open a price, whole-minute stamps) | ALL of 72 files: NG 23, 6C 13, 6E 11, ZN 11, 6A 5, 6B 5, HG 3 (2008–13) and VXK21; the start of the 2010–11 contracts; backfilled gaps later (HGU14 2014-08-29..09-02, NGU15 2015-07-06..08-27: 3,056 bars) | 0 or the bar's trades · none | as rows: event_type 2 (no volume) or 3 with a null side |
| aggregates (sub-minute, sizes 20–90) | 2010–11 (6EH11, ZBH11, ZNH11, ZSX11) | 1 or more · none, or BOTH | kept some, left others out (6EH11: NumTrades > 1 and both-sided) |
| VX settlement marks (4 decimals, off the grid, size 0) | VXF25 / VXG25 2025-01-13 21:01:55; VXX24 / VXZ24 2024-11-15 21:05:45 | 1 · none | (VX is not in the first batch) |

- **The price form:** Open = Close (O = H = L = C), no quote and no code. Every record before mid-2014 has it, and a
  stray few later (ZNU20 2020-05-17 22:00, 5 fills; 6EM18 2). `bid` = `ask` = the price, `fill_pos` NULL.
- **A file with no fills** (the 72 bar files) is left out of `ticks/`, and its parquet-derived output, made from bars,
  is deleted. The docs' "aggregates, not ticks" row above is these files.
- **The export's rounding:** round(float64(f32) × 1e9) HALF AWAY FROM ZERO. Gold above $4,000 lands on exact halves
  (4640.2001953125 × 1e9), which is how MGC's export failed numpy's half-to-even round-trip.
- ⚠ **The 1s bars from `level1_tick/` counted the post-2015 bar records as trades.** About 25,000 records in 30 files:
  NG's minute bars of 2015-05-04..08-27 (NGM15–NGV15, 25,020), backfill bursts on single days (2017-03-15 in nine roots,
  2018-11-08 and 2018-12-16 in the FX, 2022-10-30 in all eight FX: ~110 each), and the four VX marks. Small, but every one
  is a fake print at a whole minute. The rebuild from `ticks/` drops them.
- **Late-stamped fills:** some records carry a stamp about a second EARLIER than the record before them (6AM12: 145).
  They are fills, and the tape keeps them in the file's order; Malcolm's export left them out (98 files, all checked:
  the left-out fills are exactly the back-stamped ones). ⇒ `ticks/` is in the file's order, NOT sorted by timestamp:
  sort on read where order matters.
- **A LAST with no open order** (6NU19 2019-07-22 03:50:40.001, after 343 s of silence: the second's .000 record, its
  FIRST, is missing; 6BU20 2020-08-28 20:59:00.001 at the Friday close): its `fill_pos` is NULL, counted per file.
- **The .scid kept recording after the export** (MESM26 exported to 2026-06-01, 10.46M fills more in the .scid; HGU26,
  SR3U26, ZBU26, ZCU26 to 2026-09-01). The check accepts exactly these two kinds of omission, late stamps and fills
  after the export's last stamp; any other fill missing from the export fails the file.

**Every contract from its .scid (user 2026-10-28: "I'll want that column to be filled in exactly", for a sweep strategy
to backtest later).** The first batch's 1,742 .scid files (280 GB) were downloaded the same day into `sierra_scid/`.
- A contract with a .scid is built from it, and the parquet export is used only without one. A parquet-derived output
  is rebuilt once its .scid arrives (each output's metadata names its source).
- Each one is compared with Malcolm's export field by field, after the export's rows of the dropped records are taken
  out. Where the export left records out itself (6EH11), it is lined up with the records by timestamp, and every
  record it left out must be one we dropped too. A difference fails the run.

**The checks and controls:**
- Truncating prices instead of rounding fails the comparison on 6NZ25 and 6ZM25.
- One first code zeroed in 6ZM25 fails the file ("a last code out of sequence").
- One price raised by 0.0001 in a copy of 6NZ25's .scid fails the comparison (DIFFER: price, exit 1).
- The 12-file test of the rules above (the bar files, 6AH11, 6EH11, HGU14, NGU15, NQU17 across the 2017 boundary,
  MGCJ26, ZNU20, VXF25, VXK21, ZTU26, 6NZ25): every one with a reference IDENTICAL; NQU17's `fill_pos` turns from
  NULL to filled at 05:21:20 exactly.
- **Empty exports:** three first-batch files are 4-byte empty exports (ZTU11, ZTZ11, ZTH12). They are left out, as is the
  truncated ZTU26 parquet, which comes from its complete .scid instead. Their .scid have data (ZTU26's arrived first).

  cd research && python -u scripts/futures/build_ticks.py > logs/futures_build_ticks.log 2>&1; echo exit $?

`level1_tick/` stays as Malcolm's export.

**The rerun of 2026-10-29: 0 failed, 0 differing, exit 0.** Its first attempt LOOPED at 100 % CPU on 6NU19, and the 10-28
night run had died at the same file. The file has an orphan LAST right after a recording gap of more than 60 s. The
orphan fix raised the depth, which made the row look like an open order to cut at the gap, and the cut made it an
orphan again. Fixed: an orphan row is never tested as a cut. 6NU19 and 6BU20 are IDENTICAL. ⚠ The run peaked at
13.9 GB RSS on a 15 GB machine: still to find which file is heavy.

### The off-book prints: `data/futures/blocks.parquet` (VX, 2026-10-29)

The 1s-bar build stopped on VX's rounding check: 37 fills on half cents (21.335, 18.475). CFE prices VX **block and
ECRP trades in 0.005 steps** ([CFE block/ECRP sheet](https://cdn.cboe.com/resources/membership/cfe-block-ecrp-trades.pdf)),
while book trades print on cents, so these are certainly off-book. Many sit points away from the market: VXN21
21.335 against a 19.65 bid, 18.625 against 22.20. Sierra records CFE's off-book Trade messages as ordinary fills with
an aggressor side, and its 40-byte record has no condition field.

`scripts/futures/find_blocks.py` lists a fill when either rule fires (the rules are in its header):
1. **grid:** the price is off the trade grid (VX: 0.01);
2. **outlier:** a normal quote (0 < spread ≤ 4 ticks of 0.05), the fill ≥ 3 ticks outside it, ≥ 3 ticks from the
   median of both the 10 fills before and the 10 after (same side), and alone in its millisecond.

| VX 2020-09..2026-10 | fills | share of the volume |
|---|---|---|
| grid only | 22 | 0.0024 % |
| both | 15 | 0.0011 % |
| outlier only (blocks on whole cents) | 39 | 0.0064 % |
| **listed** | **76** | **0.0099 %** of 69.6M fills |

- **The outlier rule finds blocks the grid can't:** VXN24 2024-07-12, 2,610 contracts at 11.65, a point below a 12.65
  bid; VXQ21 1,933 at 20.80; VXJ25 800; VXJ23 797.
- **Without "alone in its ms",** 49 more fills were flagged, mostly 1–5 lots on crash days (2024-08-05, the
  2025-04-06 reopen, 2022-02-27, 2023-03-19). They were sweeps: several fills in one millisecond, past the pre-trade
  quote. Every one of the 37 off-grid blocks is alone in its millisecond.
- **Unseen:** a whole-cent block priced near the quote. The CME roots are not scanned: whether CME blocks reach
  Sierra's tape is unknown, and before 2020-08 the millisecond test cannot be made.
- **Ground truth, if ever wanted (not bought, user 2026-10-29: too pricey):** Databento's XCBF.PITCH labels them exactly:
  side None, publisher `XCBF.PITCH.XOFF`. Its daily statistics (block and ECRP volume per contract) cost $4.87 for
  2020-09..2026-10; trades cost $3–40 a day. Massive's futures trades have no condition, block or aggressor field,
  and cover no CFE.
- ⚠ The scan is numpy, one contract at a time (0.76 GB, 10 s for VX). A DuckDB window `quantile_disc` ignores
  `memory_limit`: three runs of it hit 13.5 GB and crashed the editor.

`build_1s_bars.py` drops the listed fills on (ticker, timestamp, price, size), checks each one matches exactly one
fill, and counts them in the manifest's `blocks`. Rerun `find_blocks.py` whenever ticks/ changes.

### The rebuild from ticks/ (2026-10-29): `data/futures/intraday_1s_ticks/`

1,527 contracts, 965.5M bars, 3,634 dates, 5.6 GB (the old `intraday_1s`: 1,364 contracts, 862.8M bars).
- **New:** 163 contracts (ES 23, VX 68, the micro FX 69, E7, YM, ZTU26 now complete), 102.0M bars.
- **Identical:** 1,305 of the 1,364 shared contracts in rows and bars.
- **The 59 others differ only in the documented ways:**
  - longer .scid (MESM26 +10.5M rows, SR3U26, ZBU26, HGU26, ZCU26);
  - the non-fill records dropped (NG 2015 minute bars, the 2022-10-30 and 2017-03-15 bursts);
  - back-stamped fills the export had lost (ZS / ZC / ZN 2017–18, +12..+352).
- **Values:** on 2024-03-14 and 2019-06-12 every old bar is matched, with an identical vwap, volume, trade_count and
  delta.
- ⚠ Pass 2 peaked at 13.4 GB RSS (2 workers × 3 GB DuckDB limit).

  cd research && python -u scripts/futures/find_blocks.py > logs/futures_find_blocks.log 2>&1
  cd research && python -u scripts/futures/build_1s_bars.py --out data/futures/intraday_1s_ticks ... > logs/... 2>&1

## Rolling (planned, not built)

1. **The front contract per root per day** comes from the PREVIOUS day's volume: the next contract takes over the day
   after its volume first exceeds the old one's, and never switches back.
2. **Returns are taken within one contract.** A trade is closed on the contract it was opened on, or exited at the roll,
   so a roll gap (+50 pts on MES above) is never a move.
3. **Long-history features use a FORWARD-adjusted series.** The first contract keeps its prices, and each later contract
   is shifted by the gap at its roll, which is known at that moment (the `daily_adjusted` idea). A BACK-adjusted series
   rewrites old levels with future gaps: a level gate on one is a lookahead (CLAUDE.md rule 4).

### Toward ONE off-book filter for every root: the ticks and grids (2026-10-30)

User: one filter for all the instruments instead of cleaning each by hand. `scripts/futures/measure_grids.py` →
`data/futures/grids.parquet`, per contract: the QUOTE tick (the smallest positive spread on ≥ 0.5 % of the quoted fills,
snapped to the nearest exchange tick — 1 / 2 / 2.5 / 5 × 10ⁿ or a binary fraction — within 2 %: float32 prices make the
raw spread noisy, CL 0.00999, ZN 0.0156) and the TRADE grid (the coarsest tick / k holding ≥ 99.99 % of the fills).
Streamed in 5M-row batches (ESZ22 has 93M fills): 780 MB, 19 min.

- **Every CME root trades on its quote tick, with NO print off it** — 0 of 8.6 billion fills: ES / MES / NQ 0.25, YM 1,
  CL / MCL 0.01, NG 0.001, RB / 6B / M6E / MJY 0.0001, HG / MHG 0.0005, MGC 0.1, HE 0.025, SR3 0.0025, ZC / ZS 0.25,
  ZB 1/32, ZN 1/64, ZT 1/128 → 1/256 (2019), 6E / 6J 0.0001 → 0.00005 (2016), 6A 0.0001 → 0.00005 (2021), 6Z 0.000025.
- **Only VX prints off its quote tick:** quotes 0.05, trades on cents (spread legs, 26 % of fills), and the 37 half-cent
  blocks off the 0.01 grid — find_blocks.py's rule 1.
- **So the GRID rule catches only VX; a generic filter rests on the OUTLIER rule**, which needs only the contract's tick
  (now known for all of them).
- 263 contracts (2011–13 expiries, 0.6 B fills) have no tick: no quote before mid-2014. All are before the bars' 2015 start.
