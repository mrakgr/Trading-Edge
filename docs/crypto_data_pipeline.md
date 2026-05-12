# Binance USDT-Perp Trade-Data Pipeline

`TradingEdge.CryptoData` is the F# CLI that builds the on-disk dataset every
crypto backtest reads: per-day trade parquets, per-(symbol, timeframe) OHLCV
bars, and per-symbol funding-rate parquets. It pulls from Binance's public
S3 archive (`data.binance.vision`) — no API keys, no rate-limit budget to
manage. This doc is the counterpart to `bulk_to_binary_pipeline.md` for the
equities side: it explains what each subcommand does, where it writes, and
the end-to-end sequence for extending the dataset to a new window.

## Stage map

```
fapi exchangeInfo + S3 archive listing
        │
        │  (1) download-universe
        ▼
data/crypto/perps_universe.json                    (symbol list + active/delisted flag)
        │
        ├──────────────────────────────┐
        ▼                              ▼
        (2) estimate-size              (3) download-perps
                                       │  (S3 archive listing → manifest → fetch+convert)
                                       ▼
{outputDir}/{SYMBOL}/{SYMBOL}-trades-{YYYY-MM-DD}.parquet
                                       │
                                       │  (4) build-bars  (per symbol, timeframes 1h/2h/4h by default)
                                       ▼
{barsDir}/{TF}/{SYMBOL}.parquet                    (one OHLCV+orderflow parquet per (symbol, tf))
                                       │
                                       │  (5) download-funding  (independent of trades)
                                       ▼
{fundingDir}/{SYMBOL}.parquet                      (funding-rate timeseries per symbol)
                                       │
                                       │  (6) verify-perps  (coverage report; no writes)
                                       ▼
                                       stdout: per-symbol first/last/gaps
```

Every command is **resume-aware** — re-running for a wider window only
fetches archives we don't already have. Trade-download idempotency uses both
per-day parquet presence and a per-monthly `.complete-YYYY-MM` sentinel
(see `PerpsDownload.fs` for why the heuristic alone wasn't enough). Funding
downloads gate on a per-symbol `.complete-{SYMBOL}` sentinel.

## Default paths

| Artifact            | Default location                                       |
|---------------------|--------------------------------------------------------|
| Universe JSON       | `data/crypto/perps_universe.json` (in-repo)            |
| Trade parquets      | `/mnt/d/trading-edge-bulk/crypto/binance/perps`        |
| Bar parquets        | `/mnt/d/trading-edge-bulk/crypto/binance/perps_bars`   |
| Funding parquets    | `/mnt/d/trading-edge-bulk/crypto/binance/perps_funding`|

All paths are overridable on each subcommand via `--output-dir` /
`--universe-file`.

## Stage 1 — universe enumeration

`download-universe` merges two sources so we don't have survivorship bias:

1. Live `fapi.binance.com/fapi/v1/exchangeInfo` — currently-trading USDT
   perps (`contractType = PERPETUAL`, `status = TRADING`).
2. S3 listing of `data/futures/um/daily/trades/` — every symbol that ever
   published an archive, including delisted ones.

Binance's recently-launched **TRADIFI_PERPETUAL** stock-tracker symbols
(AAPLUSDT, SPYUSDT, etc.) are deny-listed: thin volume, RTH-bound,
different asset class.

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-universe -o data/crypto/perps_universe.json
```

Output JSON shape: an array of `{ "symbol": "...", "status": "active" | "delisted_or_archived" }`.
Re-run any time to refresh; downstream commands pick up new tickers
automatically.

## Stage 2 — estimate-size (no downloads)

Builds the same listing-based manifest the downloader uses and reports
total bytes-on-the-wire for the requested window. The "Top 20 by bytes"
table is useful for spotting outlier symbols before kicking a multi-TB run.

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    estimate-size --start-date 2020-01-01 --end-date 2026-05-12 \
    --parallelism 32
```

Numbers are exact (sum of S3 `<Size>` fields), not heuristic.

## Stage 3 — download-perps

Manifest-driven trade-archive downloader. Two listing prefixes per symbol —
`monthly/trades/{SYMBOL}/` and `daily/trades/{SYMBOL}/` — let it pick the
monthly archive when its tile fully nests inside the window, and fall back
to dailies otherwise. The manifest guarantees the file exists, so the
download path never 404s.

Output is per-day parquet at
`{outputDir}/{SYMBOL}/{SYMBOL}-trades-{YYYY-MM-DD}.parquet` with schema:

| column         | type    | notes                                                                 |
|----------------|---------|-----------------------------------------------------------------------|
| `price`        | DOUBLE  |                                                                       |
| `quantity`     | DOUBLE  |                                                                       |
| `timestamp_us` | BIGINT  | Binance ms × 1000 — **microseconds** (matches the spot loader)        |
| `sign`         | DOUBLE  | `+1.0` buyer-aggressed, `-1.0` seller-aggressed (from `is_buyer_maker`)|

For monthly archives, the converter rotates the DuckDB appender on UTC day
boundaries: one HTTP fetch ⇒ N per-day parquets ⇒ one `.complete-YYYY-MM`
sentinel.

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-perps --start-date 2020-01-01 --end-date 2026-05-12 \
    --parallelism 4 --list-parallelism 32
```

Notes:
- `--parallelism` controls concurrent downloads — keep it small (default 4).
  Each in-flight monthly can hold ~250 MB of appender state, so 4 keeps the
  working set ~1 GB.
- `--list-parallelism` only affects the initial manifest-build phase (S3
  listings); 32 is fine, default is 16.
- Power-loss resume: `.parquet.tmp` orphans are swept on next run; finalized
  parquets and the monthly sentinel survive.
- A single mid-stream stall (no bytes for 15s) cancels the body copy and
  retries the whole fetch — covers the multi-hour "200 OK then silence"
  failure mode we observed on PARTIUSDT 2026-04.

## Stage 4 — build-bars

Aggregates per-day trade parquets into per-(symbol, timeframe) OHLCV+orderflow
parquets. Streams trades through DuckDB ordered by `timestamp_us`, pushes
each one through a `BarWriter` per requested timeframe, emits when the bucket
index changes. Per-bar schema:

```
start_us, end_us, open, high, low, close,
volume, vwap, vol_weighted_std,
buy_dollar_volume, sell_dollar_volume,
trade_count
```

`vwap` and `vol_weighted_std` are computed from running `Σ(p·v)`, `Σ(p²·v)`,
`Σv`. Bucket alignment is unix-epoch-anchored: the same `bucketIdx` maps to
the same wall-clock window across symbols and runs.

**Default timeframes are `1h, 2h, 4h`.** For FlowSwing / OrderflowCumsumZ
backtests we need **1m** bars — pass `-f 1m` explicitly:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    build-bars -f 1m --parallelism 8
```

Output: `{barsDir}/1m/{SYMBOL}.parquet`. The build is idempotent: it skips
symbols whose output parquets all already exist unless `--overwrite` is
passed. **If you extend the trade history, you must pass `--overwrite` (or
delete the affected `1m/{SYMBOL}.parquet`) — otherwise the bars stay frozen
on the old window.**

## Stage 5 — download-funding

Independent of trades. Pulls
`data/futures/um/monthly/fundingRate/{SYMBOL}/` ZIPs, concatenates all
months into one parquet per symbol:

```
calc_time_us         BIGINT  funding settlement timestamp (us)
funding_interval_us  BIGINT  8h × 3.6e9 (always 28_800_000_000)
funding_rate         DOUBLE  decimal rate per interval (0.0001 = 0.01%/8h)
```

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-funding --parallelism 8
```

Per-symbol idempotency via `.complete-{SYMBOL}` sentinel. Funding archives
are tiny (~2200 rows × ~600 symbols ≈ a few MB total), so this completes
in minutes.

## Stage 6 — verify-perps

Coverage report against the on-disk parquets. No downloads — just walks
expected dates per symbol and reports first/last present + gap count.

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    verify-perps --start-date 2020-01-01 --end-date 2026-05-12
```

Use this after a long download run to spot symbols where some dailies are
still missing.

## Walkthrough — historical backfill to 2020 + 1m bars

This is the worked example for "I want the full FlowSwing dataset from
2020 to today, with 1m bars ready for backtesting."

### 0. Estimate first

The download is large; **always estimate before kicking the fetch**:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    estimate-size --start-date 2020-01-01 --end-date 2026-05-12 \
    --parallelism 32
```

Reference numbers from the 2026-05-12 run (669-symbol universe):

| Metric                  | Value           |
|-------------------------|-----------------|
| Monthly archives        | 17,034          |
| Daily archives          | 5,808           |
| **Total compressed**    | **1.80 TB**     |
| Heaviest symbol         | ETHUSDT (~77 GB)|
| Second                  | BTCUSDT (~66 GB)|

Verify `df -h /mnt/d` shows enough headroom — the 1m bars add roughly
another ~50–80 GB on top of the trade parquets.

### 1. Refresh the universe

New listings since the last `download-universe` would silently drop from
the manifest. Refresh first:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- download-universe
```

### 2. Download the trade archives

```bash
mkdir -p logs/crypto_data_2020
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-perps --start-date 2020-01-01 --end-date 2026-05-12 \
    --parallelism 4 --list-parallelism 32 \
    > logs/crypto_data_2020/download.log 2>&1 &
tail -f logs/crypto_data_2020/download.log
```

Notes:
- **Always background + log.** This is a many-hour to multi-day run
  depending on your link. Don't let it tie up the foreground shell.
- The downloader is **resume-aware**: if it dies, just re-run the same
  command. Per-day parquets and per-monthly `.complete-YYYY-MM` sentinels
  short-circuit work that's already on disk.
- Stay at `--parallelism 4`. Higher numbers don't help — Binance throttles,
  and each in-flight monthly holds ~250 MB of DuckDB appender state.
  4 is the sweet spot in practice.

### 3. Verify coverage

When the download settles, sanity-check the per-symbol coverage:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    verify-perps --start-date 2020-01-01 --end-date 2026-05-12 \
    > logs/crypto_data_2020/verify.log 2>&1
```

The "gaps inside active windows" total at the bottom should be small —
single-digit gaps are usually venue-side outages (Binance archives are
not gap-free; a handful of dates have no published file at all). A large
gap count means dailies got missed; re-run `download-perps` for the
affected window.

### 4. Build 1m bars

The bars stage **is independent** of the trade-download stage. Once the
trade parquets are on disk, the bar builder reads them via DuckDB and
streams trade-by-trade through a per-bucket accumulator. Memory is bounded
by per-day trade count — not by history length — so a 6-year build is
not meaningfully harder than a 2-year build, just slower.

**Important:** the bar builder skips symbols whose output parquet already
exists. After backfilling, the existing 1m parquets cover only the old
2024-04 → 2026-04 window — they need to be rebuilt against the extended
trade history. Pass `--overwrite` to force the rebuild:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    build-bars -f 1m --parallelism 8 --overwrite \
    > logs/crypto_data_2020/build_bars_1m.log 2>&1
```

Output lands at `/mnt/d/trading-edge-bulk/crypto/binance/perps_bars/1m/{SYMBOL}.parquet`,
which is what `FlowSwing` / `OrderflowCumsumZ` / `OrderflowShortFadeMA`
read directly.

Bar parquet schema reminder:

```
start_us, end_us, open, high, low, close,
volume, vwap, vol_weighted_std,
buy_dollar_volume, sell_dollar_volume,
trade_count
```

`buy_dollar_volume` and `sell_dollar_volume` are aggressor-flagged — this
is the **CVD-grade signal** that motivated the move from Polygon to
Binance perps in the first place.

### 5. (Optional) Refresh funding

Funding is independent of trade history and is needed for accurate
FlowSwing P&L (the strategy holds positions long enough for funding to
matter). Re-run any time:

```bash
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-funding --parallelism 8
```

Tiny — finishes in minutes.

### 6. Re-run the strategy

At this point `/mnt/d/trading-edge-bulk/crypto/binance/perps_bars/1m/` is
the extended-history dataset. Re-run FlowSwing (or any other 1m-bar
strategy) at the production config with the existing MTM-decomposition
infrastructure to see how the strategy survives 2020–2024.

## End-to-end recipe — extending the dataset

The full sequence for backfilling history to a new earliest date — e.g.
pushing from a 2-year window back to 2020-01-01:

```bash
START=2020-01-01
END=2026-05-12

# 1. Refresh the universe (catch any new listings)
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-universe

# 2. (Optional) Estimate bytes first to confirm disk space
dotnet run --project TradingEdge.CryptoData -c Release -- \
    estimate-size --start-date $START --end-date $END

# 3. Download trade archives — the slow step
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-perps --start-date $START --end-date $END \
    --parallelism 4 --list-parallelism 32

# 4. Verify coverage
dotnet run --project TradingEdge.CryptoData -c Release -- \
    verify-perps --start-date $START --end-date $END

# 5. Rebuild 1m bars over the extended history (--overwrite is mandatory)
dotnet run --project TradingEdge.CryptoData -c Release -- \
    build-bars -f 1m --parallelism 8 --overwrite

# 6. Refresh funding (rerun is cheap, picks up new months)
dotnet run --project TradingEdge.CryptoData -c Release -- \
    download-funding --parallelism 8
```

## Common knobs

| Flag                    | Default | Notes                                                              |
|-------------------------|---------|--------------------------------------------------------------------|
| `--start-date`          | 2y ago  | yyyy-MM-dd inclusive                                               |
| `--end-date`            | yesterday | yyyy-MM-dd inclusive                                             |
| `-t SYMBOL` (repeatable)| all     | Restrict to specific symbols; mainly useful for debugging          |
| `--active-only`         | off     | Skip symbols flagged `delisted_or_archived`                        |
| `--universe-file PATH`  | `data/crypto/perps_universe.json` | Override the symbol list source                      |
| `--output-dir PATH`     | per-stage default | Override the data root                                      |
| `--overwrite`           | off     | `build-bars` / `download-funding` — re-build/re-fetch ignoring sentinels |

## Notes

- **Universe survivorship bias is handled at the source.** Both active and
  archived symbols are in `perps_universe.json` from day one. Don't filter
  with `--active-only` for historical backtests unless you specifically
  want to model a "today's listings only" experiment.
- **Stock-tracker perps are explicitly excluded.** The deny-list also
  strips them from the archived set so legacy entries don't sneak in via
  the S3 listing.
- **Timestamps are microseconds** end-to-end. Binance publishes
  milliseconds; the converter multiplies by 1000 once at ingest. Any tool
  reading these parquets should assume `timestamp_us / 1e6` to get seconds
  since epoch.
- **`sign` is from `is_buyer_maker`, inverted.** `+1` = aggressive buyer
  (taker bought into resting ask) — the orderflow convention.
- **The 1h/2h/4h bar parquets exist for the old MA experiments** (`OrderflowMa`).
  They aren't read by the current production strategies (FlowSwing,
  OrderflowCumsumZ, OrderflowShortFadeMA) which all consume the 1m parquets.
