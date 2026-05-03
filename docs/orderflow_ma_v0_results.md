# Orderflow-MA v0 — Backtest Results & Lessons

This doc captures the v0 orderflow-MA backtest on Binance USDT perpetuals. The
strategy: rolling-sum ratio of buyer-aggressive vs seller-aggressive dollar
volume; long when ratio > 1, short when < 1. The point of v0 is not to ship
this signal, but to validate the data pipeline and identify the parameter
choices that matter — which gates, which windows, which sizing.

The headline finding: **shorts are the durable edge**, **longs are
window-sensitive**, **PF rises monotonically with the signal-window length**
from 144h up to 480h on the 1h timeframe. Net dollars are roughly
window-invariant in the 200h–480h band.

## Universe and setup

- **669 USDT-perpetual symbols** from Binance USDM (active + delisted), 2-year
  window 2024-05-01 → 2026-04-30.
- **1-hour bars**, pre-aggregated from raw trades into per-symbol parquets
  (carrying buy/sell dollar volume per bar — the aggressor flag is the unlock
  vs Polygon).
- **Single-symbol $1000 notional**; per-fill taker fee 4 bps.
- **Vol-based position sizing** with `--reference-vol-pct 1.0`: high-vol
  entries are downsized so the per-bar log-return std doesn't blow up the
  realized-loss tail. The vol estimate runs over a separate window
  (`--vol-window-days`) from the signal window.
- **Funding accounting** on by default — Binance's 8-hour funding payments are
  applied across the holding window, signed by side.
- **Per-side ADV gates**: longs require trailing-90d ADV ≥ $28.8M;
  shorts ≥ $8M. Side-specific because the ADV-stratification revealed that
  longs only carry edge in the upper deciles, while shorts work across the
  full liquidity range. Trailing-90d, not whole-window — leak-free.
- **Per-bar quote-volume floor** of $5,000 (entries only) — added late after
  diagnosing redenomination disasters. See "Lesson 4" below.
- **Signal-consume rule**: the rolling-sum signal fires only on regime change
  (`target ≠ lastTarget`), not whenever the signal happens to disagree with
  the current side. Gated entries are dropped *and* the signal is consumed
  — no retry on the next bar that happens to share the same target. Without
  this the engine chases.

## Bar-window sweep — the headline curve

1h timeframe, all 643 covered symbols, identical gates. Sorted by `--ma-hours`:

| `--ma-hours` | Total PF | Trips | Long PF | Long Net P&L | Short PF | Short Net P&L | Largest Loser |
| :---: | :---: | ---: | :---: | ---: | :---: | ---: | ---: |
| 144 | 1.312 | 30,861 | 1.123 | $14,424 | 1.356 | $182,016 | $4,744 |
| 168 | 1.392 | 25,253 | 1.201 | $19,737 | 1.434 | $192,099 | $3,637 |
| 200 | 1.490 | 20,016 | 1.236 | $18,774 | 1.544 | $202,546 | $3,599 |
| 240 | 1.521 | 16,351 | 1.134 | $9,421 | 1.606 | $193,920 | $4,474 |
| 288 | 1.617 | 13,045 | 1.197 | $11,551 | 1.708 | $192,069 | $4,566 |
| 360 | 1.760 | 9,688 | 1.136 | $6,407 | 1.893 | $197,435 | $4,766 |
| 480 | 1.826 | 6,828 | 1.123 | $4,666 | 1.967 | $182,221 | $14,258 |

The structure is clean:

- **PF is monotonic** in window length across the entire range (1.31 → 1.83).
  Longer windows trade fewer but cleaner setups.
- **Net dollars are flat** in the 200h–360h band ($202k–$204k combined). Either
  end (144h, 480h) gives back ~$10–15k.
- **Long P&L peaks at 168h–200h ($19k)** and decays at longer windows ($5k by
  480h). The signal-window length that captures bullish edge is much shorter
  than the one that captures bearish edge.
- **Short net P&L is near-flat** $182k–$202k. Shorts work at every window we
  tried; the window length just changes how the same total is sliced across
  trade count.
- **The 480h $14k loser is the only outlier.** Unrelated to data quality —
  this is PUMPBTCUSDT's real ~15× pump in late September 2025 against a short
  position held for 77 days.

## Lessons

### Lesson 1 — Trailing-window ADV, not whole-window

The first decile-stratification we ran had a beautiful "size = direction"
pattern: low-decile longs PF 0.17, high-decile longs PF 7.68, with shorts
mirroring it. That pattern was 100% look-ahead. Tokens that *eventually died*
(low decile) couldn't reach the high decile anymore because their cumulative
volume was already capped; tokens that *eventually mooned* (high decile) had
the future built into their decile assignment.

Re-running with **trailing-90d ADV at entry** — computed in-engine from a
rolling buffer ending at the signal bar — flattened the pattern entirely.
Long PF varied 0.82–1.47 across deciles with no monotonicity. Short PF varied
1.07–1.88, also no clean monotonicity. The "edge" disappeared. The real
takeaway, with the leak removed: shorts work everywhere, longs work mainly
in the upper-ADV tail.

This is the same mistake the equities breakout system hit on stocks-in-play:
that strategy used intraday rvol thresholds, which respond to the same bar
that produced the signal — look-ahead in disguise. Removing the leak there
killed the edge entirely. Here it just clipped a fake "decile gradient"
narrative.

### Lesson 2 — Side-specific ADV gates + signal consumption

With trailing-90d ADV available per-trade, the next question was whether
filtering by it improves anything. It does, but only for longs:

- **`--min-long-adv 28800000`** (cuts deciles 1–7 of longs by trailing-90d
  ADV at entry, leaving only deciles 8–10 where long PF is consistently > 1):
  cell PF 1.258 → 1.371; long PF 1.077 → 1.205; long avg trade $1.38 → $2.77.
- **`--min-short-adv 8000000`** (cuts the bottom three deciles of shorts):
  cell PF 1.371 → 1.406; short PF 1.397 → 1.453; short avg trade $9.96 →
  $12.46. Shorts work even at the bottom but the gate trims dilutive trips
  without losing the durable edge.

Crucially, the gate logic depended on a separate fix: the engine previously
re-evaluated the orderflow signal on every bar via `if target ≠ side`. If
the entry bar failed a gate, the engine would happily fire the same trade on
any subsequent bar that still had the same target — chasing the signal
backward. The fix is `if target ≠ lastTarget`: act on regime change, not
on side disagreement, and update `lastTarget` unconditionally (gate or no
gate). The signal is *consumed at fire time*. Without this fix, the
side-specific ADV gates leak: an entry blocked at fire time gets retried
later when the gate happens to relax, dragging up the trade count without
adding edge.

### Lesson 3 — Vol window decoupled from signal window

Vol-based sizing was originally using the same window length as the
orderflow signal (200 bars at 1h MA200). Decoupling them and sweeping the
vol window separately:

- **200-bar vol** (matched MA): total PF 1.416, largest loser $20.5k.
- **90-day vol**: total PF 1.287 (worse), largest loser **$67k**.
- **7-day vol**: total PF 1.430, largest loser $18.7k.

Vol wants to be *responsive*, not stable. It's a risk measurement; a 90-day
window lags badly through regime shifts and the engine sizes up just before
getting hit. ADV is the opposite: a slow, stable measurement of structural
liquidity is what we want there. Same kind of quantity (rolling window over
per-bar floats), different time horizons.

The CLI flag is `--vol-window-days`; default 7d.

### Lesson 4 — Per-bar liquidity floor catches redenomination stubs

The 288h cell came back with total PF 1.215 and a single trade losing
**$109,057** — FLOWUSDT short, entered at $7.4e-06, exited at $0.05736 after a
**~10,000× redenomination gap** at 2026-01-28 10:00 UTC. The pre-event tail
traded $5–30/hour for days; the 90-day ADV gate didn't catch it because
the trailing window integrated over old, normal trading.

Crypto perps don't get split-adjusted historical prices the way equities do.
A token redenomination (1 old = 10,000 new, or vice versa) shows up as an
unmodeled price gap stitched into the same time series. The exchange's
public archive emits the raw tape; our parquets carry it as-is.

Adding a **per-bar absolute quote-volume floor** (`--min-bar-quote-volume`)
defaults to 0 (off) but enabling it at $5k blocks any entry on a bar with
< $5k of dollar volume. At 288h:

- 1 trip blocked (the FLOW disaster).
- Cell PF 1.215 → 1.617; largest loser $109k → $4.5k.

A symmetric pre-redenomination gap on CRVUSDT at 240h that *made* $18k
windfall short was also blocked — correctly, because both the disaster and
the windfall are unmodeled data artifacts, not strategy edge. The 5k floor
is universal: it filters at every window length but only triggers on the
genuinely-pathological bars.

The 480h $14k loser (PUMPBTCUSDT) is *not* a stub-bar event — it's a real
news-driven 15× pump over 77 days. The bar floor doesn't catch it, and
shouldn't.

### Lesson 5 — Hours, not bars

Specifying the signal window in bars (`MA200`) ties the parameter to the
timeframe: 200 bars is 8.3 days at 1h but 33 days at 4h. Renaming to
`--ma-hours` made the parameter cross-comparable: 200h is the same wall-clock
span at any timeframe. The engine converts hours → bars internally via
`BucketUs`. The 1h MA200 ≡ 4h MA50 equivalence (both ≈ 200h) was visible
empirically in earlier results and motivated the rename.

### Lesson 6 — Generic rolling-MA abstraction

With three rolling windows (orderflow signal, ADV, vol) each maintained as
hand-rolled `Queue<float>` + sum boilerplate at three sites in `ProcessBar`,
the natural abstraction is a generic base class. The implementation:

```fsharp
[<AbstractClass>]
type RollingMa<'Bar, 'State>(initState: 'State, windowSize: int) =
    let q = Queue<'Bar>(windowSize)
    let mutable state = initState
    abstract member Add    : 'Bar * 'State -> 'State
    abstract member Remove : 'Bar * 'State -> 'State
    member _.Count = q.Count
    member _.State = state
    member this.Push (x: 'Bar) =
        if q.Count = windowSize then
            state <- this.Remove (q.Dequeue(), state)
        q.Enqueue x
        state <- this.Add (x, state)
```

`SumMa` (state = `float`) handles the buy/sell signal and ADV windows.
`StdMa` (state = `struct (float * float)` for `(ΣX, ΣX²)`) handles the vol
window and exposes a derived `SampleStd` reader. Subclasses are sealed so
the JIT can devirtualize the abstract calls.

Pure update functions on the abstract members `Add`/`Remove`; mutation hidden
in the base. Verified zero behavior change: the queue-based sweep and the
abstracted sweep produce byte-identical breakdown CSVs.

## What we deliberately did not pursue

- **MAE-fraction stops** were tested and hurt overall PF when the data was
  contaminated by redenomination flukes. Worth retesting with the 5k floor
  on, but de-prioritized for v0.
- **Bars-held caps** (e.g. force exit at 3× window) — same status. Would
  curtail the PUMPBTC-style real-pump losses on long-window shorts.
- **Faster exit signal** layered over a slow entry signal — interesting, but
  it's a v1 idea.
- **Crypto Lake order-book features** for the 24-symbol Tier-A subset. The
  pipeline is documented in
  [crypto_lake_orderbook_pipeline.md](crypto_lake_orderbook_pipeline.md);
  decision to subscribe is gated on whether v0 trades-only results justify
  the spend. They do, marginally — but order-book features are the kind of
  upgrade that wants a working baseline first.

## Result CSVs and breakdown logs

All saved under `data/crypto/`:

- `floor5k_results.csv` / `floor5k_summary.csv` / `floor5k_results_breakdown.log`
  — the 200/240/288/360/480 sweep with the 5k bar floor on. The headline
  curve above comes from this run.
- `short_window_results.csv` / `short_window_summary.csv` /
  `short_window_results_breakdown.log` — the 144/168/200 sweep, same gates.
  Confirms 144h is materially worse than 200h; the curve goes the wrong way
  if you shorten further.

Per-trip detail per cell-config in `*_trips_1h_ma{N}h_ls.csv`. The "breakdown"
log carries pooled per-symbol stats, long/short split, excursion percentiles,
squeeze-survival buckets, and the per-trade ADV decile stratification.

## Pinned configuration for v0

```
--timeframes 1h
--ma-hours 200          # picked: ~$202k net P&L, 20k trips, PF 1.49.
                        # Higher-PF cells exist at 240–480h with similar net dollars
                        # but fewer trips per symbol; 200h gives more breathing room
                        # for per-symbol statistical significance.
--allow-short
--reference-vol-pct 1.0
--vol-window-days 7
--min-long-adv 28800000
--min-short-adv 8000000
--min-bar-quote-volume 5000
--min-daily-volume 0    # superseded by the trailing-ADV gates + bar floor
```

Reproduce:

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- sweep \
    --timeframes 1h \
    --ma-hours 200 \
    --allow-short \
    --reference-vol-pct 1.0 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --min-daily-volume 0 \
    --min-bar-quote-volume 5000
```
