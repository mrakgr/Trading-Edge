# Crypto Lake Order-Book Pipeline

This doc captures what was built for order-book data on Binance USDM perps and
why we paused the work. Read it before resuming.

## Why we're not using Binance Vision for order books

Binance's public archive at `data.binance.vision` publishes two L2-related
products. Both fail our requirements:

- **`bookDepth`** — percentage-band view (`±0.2%`, `±1%`, `±2%`, `±3%`, `±4%`,
  `±5%`), 12 rows per snapshot, **~30s targeted cadence**. Empirical
  measurement on BTCUSDT 2026-04-29: median 30s, mean 33s, **max gap 241s**
  during busy periods. On a heavy day with 100+ trades/sec, that 30s gap means
  the snapshot trails ~3000 trades. Too coarse to align with per-trade
  orderflow signals. ~124 GB compressed for the 669-symbol universe over 2
  years.
- **`bookTicker`** — top-1 quote, every update. **Discontinued 2024-03-30.**
  Last BTCUSDT file: `BTCUSDT-bookTicker-2024-03-30.zip`. Predates our
  2024-05-01 window start, so unusable.
- **No L2-deltas product on Vision.** Would require live WebSocket capture if
  we wanted that path.

The percentage-band shape of `bookDepth` also doesn't match the
top-N-levels formulation of the imbalance signal we want
(`I = (Q_bid - Q_ask) / (Q_bid + Q_ask)` over the best N levels).

## Why Crypto Lake

[https://crypto-lake.com](https://crypto-lake.com) sells per-day historical
crypto market data via the `lakeapi` Python package, distributed from S3.
Their products that are relevant for us:

- **`book`** — 20 L2 levels per side, **≤100ms snapshots**. 300× faster than
  Binance Vision `bookDepth` and into the per-trade-alignable regime.
  Empirically rock-solid 100ms heartbeat (verified on the free sample).
- **`book_delta`** — incremental L2 deltas, unlimited depth. Highest fidelity,
  but requires writing an order-book reconstructor. Defer.
- **`book_1m`** — 1-min aggregated snapshots, 20 levels. Useful only when 100ms
  is overkill.
- **`trades`**, `funding`, `open_interest`, `liquidations`, etc. — also
  available; we already have trades from Binance Vision.

### Coverage on `BINANCE_FUTURES`

This is the load-bearing constraint that drove our pause:

| product | symbol count | covers our 669-symbol universe? |
|---|---|---|
| `book` (full L2 snapshots) | **24** | ~3.6% |
| `book_delta` | similar small set | ~3.6% |
| `candles` + `funding` | 219 | ~33% |
| `funding` only | 286 | ~43% |

Crypto Lake's `book` covers about 24 USDM perps — the majors. Our trading
premise is the long tail of alts, which is **not** covered with order-book
data. Paying $64/mo to enrich 4% of the universe is poor ROI without first
demonstrating the trade-only orderflow signal has edge on the long tail.

### Pricing

- **Free sample** — anonymous S3 access via `lakeapi.use_sample_data(...)`.
  ~3 days of BINANCE BTC-USDT (spot) `book` data, plus a handful of other
  symbols/products. **No `BINANCE_FUTURES` order-book data in the sample.**
  Sufficient for schema/pipeline validation but not for backtesting.
- **Individual** — $64/mo, 300 GB/mo download cap. Sufficient for our 24
  in-coverage perps × 2 years of 100ms book.
- **Companies** — $500/mo, 3 TB/quarter. Overkill for v0.

## What's been built

Three Python scripts under `scripts/crypto/`. Each works against the free
sample by default; pass `--paid` to switch to the paid bucket (requires AWS
credentials in env or `~/.aws/credentials`).

### `scripts/crypto/lake_universe.py`

Enumerates `lakeapi.available_symbols(table=...)` for a given exchange and
writes a tagged JSON manifest. Each entry carries both
the Crypto Lake hyphenated symbol (`BTC-USDT`) and the Binance flat
symbol (`BTCUSDT`) so downstream consumers can match either format.

```
python scripts/crypto/lake_universe.py \
    --table book --exchange BINANCE_FUTURES --paid \
    --output data/crypto/lake_book_universe.json
```

### `scripts/crypto/download_book.py`

Day-by-day `load_data()` calls. Writes one parquet per (symbol, date) at
`/mnt/d/trading-edge-bulk/crypto/lake/{table}/{exchange}/{symbol}/{date}.parquet`.
Atomic `.tmp` + `os.replace` for power-loss safety, skip-if-exists for
resume. The Lake schema is preserved with light reshaping:

- `origin_time` and `received_time` → int64 microsecond columns
  (`timestamp_us`, `received_time_us`). `origin_time` is the exchange-side
  stamp — use it for joining with the trade tape.
- `bid_0` is **best bid** (top of book), not bid_1. Same for ask_0.
- 20 levels per side × 2 (price/size) = 80 level columns + 3 metadata
  columns (`timestamp_us`, `received_time_us`, `sequence_number`) = 83 total.

Validated on the sample: 3 days of BINANCE BTC-USDT, 863k rows/day, 134-150
MB/day after zstd parquet compression.

```
python scripts/crypto/download_book.py \
    --paid \
    --symbol BTCUSDT-PERP --exchange BINANCE_FUTURES \
    --start-date 2024-05-01 --end-date 2026-04-30
```

### `scripts/crypto/verify_book.py`

Five-check sanity pass on a downloaded parquet:

1. **Schema** — confirms 83 columns including `timestamp_us` and the full
   20-level grid.
2. **Level ordering** — `bid_0_price > bid_1_price > ...` and `ask_0_price <
   ask_1_price < ...` per row.
3. **Spread** — `ask_0_price >= bid_0_price` every row (no crossed books).
4. **Cadence** — p50/p95/p99/max gap between consecutive snapshots.
5. **Imbalance shape** — `I = (Q_bid - Q_ask) / (Q_bid + Q_ask)` over top-1,
   top-5, top-10 cumulative quantities, with quantiles at p05/p50/p95.

Sample-data verdict on 2022-10-01 BTC-USDT:

```
[1/5] schema: 83 columns
[2/5] level ordering: bid_violations=0  ask_violations=0
[3/5] spread: total=863,465  crossed=0  locked=0
[4/5] cadence over 863,464 intervals:
        p50=100.0ms  p95=101.0ms  p99=101.0ms  max=300.0ms
[5/5] imbalance distribution:
        top-1:  I p05=-0.987  p50=+0.000  p95=+0.988
        top-5:  I p05=-0.931  p50=+0.002  p95=+0.941
        top-10: I p05=-0.691  p50=+0.008  p95=+0.692
PASS
```

The 100ms heartbeat is exact, the imbalance distribution is symmetric
mean-zero (no side-classification bug), and depth-stability increases as we
widen the band (top-10 has tighter tails than top-1 — expected).

## What's NOT built (and why)

- **F# integration** — the backtester (`TradingEdge.CryptoBacktest`,
  itself unbuilt) hasn't been wired to read these parquets. Per-trade
  carry-forward join logic is sketched in the plan but not implemented.
  Deferred until the trade-only orderflow-MA backtest demonstrates edge
  worth enriching.
- **Tier A / Tier B cohort split** — the universe of 669 perps splits into
  ~24 with order-book coverage (Tier A) and ~645 without (Tier B). The
  backtest harness needs to report these cohorts separately so we can see
  whether the order-book features add edge specifically on Tier A. Not yet
  written.
- **Paid bucket validation** — we have a Crypto Lake account but haven't
  generated the API key yet. Once generated, the same scripts run against
  `--paid --exchange BINANCE_FUTURES`. No code changes needed.
- **`book_delta` reconstructor** — would let us synthesize the order book at
  any timestamp at full fidelity. Big engineering project; deferred until
  `book` (snapshot) results justify it.

## When you return to this

The next concrete step is **trade-only orderflow-MA backtest** to determine
whether order-book enrichment is worth the subscription. If that backtest
shows broad edge on the long tail, the order-book work is irrelevant to
that thesis — the long-tail alts aren't covered. If the backtest's edge
concentrates on majors that ARE covered, then proceed:

1. Generate Crypto Lake API key.
2. `python scripts/crypto/lake_universe.py --paid --table book` to confirm
   the 24-symbol Tier A list against the current Lake catalog.
3. `python scripts/crypto/download_book.py --paid --symbol BTCUSDT-PERP
   --exchange BINANCE_FUTURES --start-date 2024-05-01 --end-date 2026-04-30`
   for a single symbol to size the actual data volume.
4. If it fits the 300 GB/mo Individual cap, expand to all 24 Tier A symbols.
5. Build the F# carry-forward join in `TradingEdge.CryptoBacktest` and
   report Tier A vs Tier B metrics separately.

The plan file at `~/.claude/plans/let-s-go-with-this-velvety-milner.md` has
the full design including the verification checklist and CLI workflow.
