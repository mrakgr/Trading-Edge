# Data v2 — the daily data and the research universe as parquet (2026-09-29)

**Why.** Production (the VPS) needs the universe and the previous closes without a 22 GB DuckDB file, and research and
production must build their data **the same way** (user). v2 is parquet built from the raw downloaded files by one command;
`trading.db` stays as **v1** for the legacy systems. No database is needed to read v2: DuckDB queries the files directly.

## Layout and commands

| what | where | built by | run time |
|---|---|---|---|
| raw inputs | `data/daily_aggregates/*.csv.gz`, `data/{splits,dividends,tickers}.csv`, `data/fx/eurofxref-hist.csv` (the ECB rates the dividends are converted to USD with, 2026-10-21; docs/price_adjustment.md §4b) | `backfill-daily` downloads | — |
| daily set | `data/v2/{daily_prices,splits,dividends,ticker_reference,split_corrections,daily_adjusted}.parquet` + `manifest.json` (previous build: `data/v2.prev/`) | `TradingEdge.Database build-v2`, or `backfill-daily` (after the v1 ingest) / `backfill-daily --v2-only` (VPS: no trading.db) | ~50 s (16 threads); 67 s / 4.2 GB peak at 4 threads |
| research universe | `data/v2_candidates/mr_candidate_1s_v2/date=…/` (hive) + `liq_cache/` | `dotnet fsi scripts/equity/build_mr_candidate_1s.fsx -- --v2 data/v2` | full 2.5 min (2,534 slim days), cached rerun 11 s |
| reference corpora | `data/v2_candidates/corpora/{flushfader_v49tkd_cand,flushfader_v17tkd_cand,spikefader_s44_whitelist,lowfader_wide_whitelist}.parquet` | exported once from v1 | — |

Readers:
- `scan trade --data-dir research/data/v2` (the default) — `TradingEdge.Scanner/DataV2.fs`.
- Engines: `--candidates data/v2_candidates/mr_candidate_1s_v2` (FlushFader, LowFader, SpikeFader, MaxFader, LongHiker,
  `scan trips`) — a v1 table name still works; `TradingEdge.Orb/CandidateSource.fs`.
- Python: `import data_v2; data_v2.attach(con)` (`scripts/equity/data_v2.py`) exposes v2 under the v1 names (`db.daily_prices`,
  `db.mr_candidate_1s_v2`, `daily_episodes_causal`, …). The Snoozer builders and `springflyer_daily.py` default to it
  (`--db data/trading.db` for v1).
- `daily_episodes_causal` everywhere comes from `scripts/equity/build_daily_episodes_causal_view.sql` (the Scanner compiles it in).

## Rules the build keeps

- The daily set is rebuilt **in full** every run: splits and dividends are corrected retroactively and `n` / `cum_div` are
  cumulative, so an append would leave stale rows. The raw-file → row SQL is `Database.fs`'s `*Source` functions, shared
  with the v1 ingest; `02_split_corrections.sql` / `03_daily_adjusted.sql` are the same files v1 runs.
- **Deterministic**: identical parquet bytes run to run and at any thread count (the per-date sums in 02/03 carry an
  explicit `ORDER BY` — without it two builds disagreed on 6.2M `cum_div` values in the last bits).
- `ticker_reference` **accumulates** (the previous v2 list upserted with tickers.csv), as v1 never deletes: the API drops
  delisted / reclassified names whose history the universe still needs. Seeded once from v1
  (`data/v2_seed/ticker_reference_v1_2026-09-29.parquet`).
- A (ticker, date) in two day files: the file that owns that session wins (2019-08-12's file stamps 29 thin ETFs 08-13).
- Guards: duplicate keys refuse; splits / dividends shrinking > 10 % against the previous build refuse.
- The universe's expensive liquidity scan is cached per slim day (thresholds pinned in `params.json`; `--full` redoes
  it); its daily context is recomputed in full every run.

## Verification (2026-09-29)

| check | result |
|---|---|
| v2 daily set vs v1 (`scripts/db/compare_v1_v2.py`) | splits, dividends, ticker_reference, split_corrections identical; daily_prices 27 rows (the 2019-08-12 ETFs, none CS/ADRC); daily_adjusted beyond 1e-9 only on those 27 |
| `daily_episodes_causal` v1 vs v2 | 27,007,179 rows both, same episodes, 0 values beyond 1e-9 |
| `backfill-daily --v2-only` in a raw-only copy | fetched 09-25 / 09-28, built through 09-28 in 3m22s, never opened trading.db |
| `scan trade` replays 09-21, 09-24 (make+cap) on v1 vs v2 | every output table identical |
| universe builder: parquet vs a fresh v1 build (Aug 2026) | identical but last-bit `div_*` (v1's pre-fix `cum_div`) |
| the `--candidates` flag alone | 10-day base runs = the sealed references trip for trip (FlushFader 71,220, LowFader 28,309, SpikeFader 5,907) |
| the same runs on the parquet universe | every trip matches (symbol, date, signal, entry, trade_idx; prices and returns identical); numeric columns ≤ 6e-14 relative; `n` differs on 1,478 / 404 / 6 trips — see below |
| corpora exports | content hashes match (HUGEINT `tc_0945_tape` cast exact) |

**Split history (resolved 2026-09-29):** Polygon rewrote old split history by 09-24 (~40 old splits added, some
dates/ratios moved, IAU's 2010 split now correct at the source). Every tape-testable changed row was checked against
`daily_prices` (38 added agree, the 3 contradicting are REJECTed by `02_split_corrections.sql`, the 11 removed were
tape-contradicted); `validate_daily_adjusted.py` is re-pinned (SHIFT 29) and passes. The v1 `mr_candidate_1s_v2` table
was then rebuilt: it equals the parquet universe (1,451,254 rows, same keys, same `n`); against the old table 3 rows are
new and `n` moved on 111,511 rows / 217 tickers (a level, never a decision input). The old table is archived as
`db_archive/2026-09-29/mr_candidate_1s_v2_pre_split_fix.parquet`.

**Home = VPS:** the v2 build is byte-identical on both machines (every output hash). Polygon silently revises old day
files: the VPS's fresh download of 2025-10-28..11-21 differed from home's February copies in 6 rows (warrants/rights,
busted prints); home took the current files. `backfill-daily` never re-downloads a day file it already has.

## v1 cleanup (same day)

69 dead tables archived to `/mnt/d/trading-edge-bulk/db_archive/2026-09-29/*.parquet` (`manifest.tsv`; each verified by
row count and content hash) and dropped (`scripts/db/archive_and_drop.py`); the file compacted 22.79 → 7.68 GB with every
kept object, constraint and view verified identical (`scripts/db/compact.py`). The pre-cleanup file is kept as
`data/trading.pre_cleanup.db` until the user deletes it.

## The marker-free 1s corpus (2026-10-24/25)

**Why.** Codes 15 (Market Center Official Close) and 16 (Market Center Official Open) are markers, not trades. The
condition table marks them Hi/Lo N, Op/Cl N, Volume N, and each one restates a print already on the tape:
- on the listing venue, a copy of its 17 / 8 cross;
- elsewhere, a copy of that venue's first or last trade;
- NYSE Arca also restates Nasdaq's closing cross: AUR 10-20, the 8.25M-share cross counted three times.

The 1s filter kept them (GF-47): 9.5–18 % of a day's bar volume.

How often a marker copies a real print (same ticker, size and price that day):

| day | 16 copies | 15 copies | volume found nowhere else |
|---|---|---|---|
| 2017-06-15 | 99.1 % | 99.5 % | ≤ 0.05 % |
| 2022-03-15 | 98.6 % | 86.9 % | ≤ 0.03 % |
| 2026-10-20 | 96.1 % | 78.8 % | ≤ 0.11 % |

The unmatched markers are almost all ≤ 100 shares.

**The fix.**
- `TradeFilters.markerConditions = {15, 16}`.
- `Bars.fs` drops them FIRST, before the auction override, in both the SQL (the corpus builder) and `keepByMask` (the
  Oms's `L1.eligible`).
- The lit-only `whereClauseSql` lineage (1m bars) still counts them.
- `build_all_1s_bars.fsx` stamps `.filter` = MARKERS_DROPPED and refuses to add marker-free days to a corpus without the
  stamp (the old `data/intraday_1s_slim` has none).

**The build.**
- `data/intraday_1s_slim_v3/`: 2,567 days, 0 missing, 198 GB.
- `--stage-dir`: the trades are on a 5,400 rpm HDD behind WSL's 9P mount. DuckDB's column reads got ~40 MB/s; one
  sequential 4 MB-block copy to the SSD gets ~139 MB/s, so a day takes ~5–11 s instead of 30–40 s.
- 16 MB blocks failed with ENOMEM on 19 days under memory pressure; 4 MB blocks plus a retry fixed it.
- Effect on 10-20: −16.7 % bar volume, −10.3 % 09:30–09:45 dollar volume. On the same ticker-day the new
  dv_0945_tape is 0.879× the old (median; p10 0.768, p90 0.998).

**Iso-trip study, 2016-08 … 2026-10-22.**
- OLD = today's code on the old corpus at $2M (`TradingEdge.Parity`, `out/volfix/old`, private repo).
- NEW = the marker-free corpus, the candidate table at $1M (`data/v3_candidates`), FlushFader's own floor lowered to
  match (`parity --min-dv0945-tape`), each book cut at dv_0945_tape ≥ X.
- Script: `out/volfix/iso.py` (private repo).

"vs old" = trips at the floor ÷ trips on the old corpus at $2M; PF is equal-weight (sized in parentheses).

| floor | universe vs old | FlushFader vs old (reproduced), PF (sized) | SpikeFader vs old, PF | Snoozer vs old, PF | LowFader vs old, PF |
|---|---|---|---|---|---|
| old $2M | 1,482,156 ticker-days | 39,117, 1.525 (1.739) | 3,424, 2.254 | 1,596, 2.904 | 322, 3.273 |
| $1.50M | 1.021 | 1.023 (99.6 %), 1.534 (1.744) | 1.047, 2.291 | 1.121, 2.613 | 1.075, 2.939 |
| $1.75M | **1.001** | 1.005 (99.5 %), 1.529 (1.740) | 1.013, 2.275 | 1.033, 2.778 | 1.065, 2.953 |
| **$1.80M** | 0.998 | **1.002 (99.4 %)**, 1.530 (1.745) | 1.007, 2.273 | 1.016, 2.801 | 1.062, 2.950 |
| $1.85M | 0.994 | 0.998 (99.3 %), 1.528 (1.744) | **1.001**, 2.267 | **1.003**, 2.821 | 1.059, 2.956 |
| $2.00M | 0.982 | 0.987 (98.5 %), 1.534 (1.751) | 0.982, 2.253 | 0.954, 2.895 | 1.056, 2.996 |

- **FlushFader is indifferent to the fix:** 99.4 % of trips identical at $1.80M, PF 1.525 → 1.530. SpikeFader is
  slightly better.
- The Snoozer is at the old level at $1.85M. A looser floor adds weaker names: at $1.50M +12 % trips, PF 2.90 → 2.61.
- **Recommendation: the floor $2M → $1.80M**, the FlushFader iso point (the universe's is $1.75M). It lives in three
  places, which must change together:
  - the candidate table (`--min-dv`);
  - FlushFader's `MinDv0945Tape`;
  - the Oms's `Gate.MinDv0945Tape`.
- ⚠ **LowFader moves at EVERY floor** (+40 trips at mean +2.28 %, PF 2.29; −15 at +3.70 %, PF 8.21): its own volume
  features changed, not the floor. `rr = vol_60 / (vol_0945_tape / 15)` divides by the 09:30–09:45 volume, which held
  the duplicated opening cross, so rr rises and more signals pass `rr >= 2` (A grade `rr >= 5`). Also, 6 of the 40
  are names now under its $20M upper cap (`OrdMaxDv0945Tape`). Iso would scale these: rr ≈ /0.88 (2 → ~2.3, 5 → ~5.7),
  the cap $20M → ~$17.6M. A LowFader re-tune, for the user.
