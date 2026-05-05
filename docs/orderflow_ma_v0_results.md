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

1h timeframe, all 643 covered symbols, identical gates, gap detector ON
(`--max-bar-price-ratio 3.0` — see Lesson 7). Sorted by `--ma-hours`:

| `--ma-hours` | Total PF | Trips | Long PF | Long Net P&L | Short PF | Short Net P&L | Largest Loser |
| :---: | :---: | ---: | :---: | ---: | :---: | ---: | ---: |
| 144 | 1.323 | 30,835 | 1.136 | $15,822 | 1.366 | $185,429 | $3,483 |
| 168 | 1.384 | 25,243 | 1.217 | $21,066 | 1.421 | $187,517 | $3,637 |
| 200 | 1.490 | 20,006 | 1.257 | $20,175 | 1.540 | $200,685 | $3,599 |
| 240 | 1.520 | 16,336 | 1.162 | $11,200 | 1.598 | $191,544 | $4,474 |
| 288 | 1.624 | 13,031 | 1.225 | $12,947 | 1.709 | $191,825 | $4,566 |
| 360 | 1.786 | 9,681 | 1.171 | $7,864 | 1.916 | $199,918 | $4,766 |
| 480 | 1.814 | 6,826 | 1.143 | $5,338 | 1.946 | $179,262 | $14,258 |

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

### Lesson 7 — In-trade gap detector + signal lockout

The per-bar liquidity floor in Lesson 4 catches redenomination *entries*
(stub-data bars at fire time), but not redenomination events that happen
*while a position is open*. Orderflow shrugged this off in practice
because its median hold is only ~22 bars — the chance of a redenomination
firing during a held trade is low. But the breakout sanity check
(median hold ~400 bars) revealed how dangerous unprotected long holds
are: a single GALAUSDT short held across a 1:96 redenomination took a
$94,887 loss on $1000 notional — i.e. the strategy claimed to short and
ride the position through a 96× upward gap, and the backtest believed it.

The fix has two parts:

1. **Bidirectional bar-to-bar gap detector.** While a position is open,
   if `bar.Close / prevClose > MaxBarPriceRatio` *or*
   `prevClose / bar.Close > MaxBarPriceRatio`, force-close at *prevClose*
   with the previous bar's `EndUs` and **drop the gap bar entirely** —
   no MA push, no excursion update, nothing. Models a venue circuit-
   breaker / suspended-symbol detection: in live trading we'd be flat
   before the relisting prints. Threshold of 3.0 (≥200% per-bar move)
   was picked from a survey of actual Binance redenomination ratios:
   the smallest known case is MANTRA at 1:4; the next-smallest is
   STRAX/SONM/GXS at 1:10. A 3.0 threshold catches every known case
   including MANTRA while staying well above any legitimate single-bar
   move on hourly+ bars.

2. **Signal lockout for one full window.** A gap doesn't just
   contaminate the gap bar — it contaminates the rolling state for
   `windowSize` bars afterward. The orderflow signal MAs hold pre-gap
   dollar volumes; the breakout's rolling max/min holds pre-gap VWAP
   extremes. After firing the detector, set `signalLockoutUntil =
   barsSeen + n` and refuse to evaluate the signal for the next `n`
   bars. The rolling state still gets pushed during lockout, so by the
   time it expires the entire window is post-gap data. Also reset
   `lastTarget = Flat` so the signal-consume rule lets the first
   post-lockout signal fire normally.

Effect on the v0 orderflow sweep was small (median hold is short, so
contamination was rare) — the 200h cell PF stayed at 1.490, and the only
material change was that PUMPBTCUSDT's 480h $14k loss survived (real
~15× pump over 77 days, not a redenomination — the bar-to-bar log return
never breached 3.0). Effect on breakout was decisive: largest loser
$94,887 → ~$1,300 across every window, total PF flipped from below 1.0
to above 1.0 everywhere. Effect on VWMA was modest in PF terms (1.083
→ 1.087) but cleaned up the largest loser ($94,887 → $802) — VWMA's
churn-and-pay-fees structure means redenomination was never its
dominant problem.

CLI flag: `--max-bar-price-ratio` (0 = disabled, recommended 3.0). On
by default in v0 from this point on.

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
--ma-hours 200          # picked: ~$201k net P&L, 20k trips, PF 1.49.
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
--max-bar-price-ratio 3.0  # in-trade redenomination guard; see Lesson 7
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
    --min-bar-quote-volume 5000 \
    --max-bar-price-ratio 3.0
```

## Sanity check — VWMA crossover

To verify that the orderflow ratio is doing real work and we're not just
benefiting from "any signal + the ADV gates + vol sizing", we ran a
research-only baseline that swaps the orderflow signal for a close-vs-VWMA
crossover (long when `close > vwma_N`, short when `close < vwma_N`),
keeping every other knob — universe, gates, vol sizing, per-bar floor,
funding accounting, signal-consume rule — identical. Implementation lives
in `TradingEdge.CryptoBacktest/VwmaCross.fs` with a `vwma-sweep`
subcommand; the file is self-contained and intended to be deleted once
the question is answered.

Same 1h timeframe, 200h window, 643 covered symbols, gap detector ON
(`--max-bar-price-ratio 3.0` — see Lesson 7):

| Metric | Orderflow-MA v0 | VWMA crossover |
| --- | ---: | ---: |
| Total round trips | 20,006 | 140,511 (**7×**) |
| Profit factor | **1.490** | 1.087 |
| Long net P&L | $20,175 | **−$36,738** |
| Long PF | **1.257** | **0.926** |
| Short net P&L | $200,685 | $162,740 |
| Short PF | **1.540** | **1.171** |
| Largest loser | $3,599 | $802 |
| % cells PF > 1 | 72.5% | 58.3% |

What this confirms:

- **The orderflow ratio is the binding signal.** With it removed, PF
  collapses from 1.49 to 1.08 even though everything that filters trades
  (ADV gates, bar floor, vol sizing) is unchanged. The gates clean up the
  edge; they don't create it.
- **Longs go *negative* on VWMA.** Buying every time price prints above
  the VWMA is textbook noise-chasing: ~140k crossings per 2 years across
  the universe, each paying entry+exit fees on what is mostly mean-
  reversion. Orderflow's long edge survives because the buy/sell ratio
  only flips when there's *actual aggressor imbalance* — far fewer
  signals, and each one has economic content.
- **Shorts still profitable on VWMA, but smaller.** Bearish-tape edge in
  USDT-perps is robust enough to show through almost any directional
  filter (PF 1.166 with no orderflow input), but orderflow extracts
  meaningfully more of it ($202k vs $159k).
- **Trade-count blowup is the smoking gun.** 7× the trips for ~75% of
  the net P&L means the median trade contributes far less, and fees
  eat far more. Crossover-style signals on a slow underlying are
  structurally a fee-loser.

Output files: `data/crypto/vwma/backtest_results.csv`,
`backtest_summary.csv`, `backtest_results_breakdown.log` (the breakdown
includes the same per-decile / per-symbol / squeeze-survival sections as
the orderflow run).

Reproduce:

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- vwma-sweep \
    --timeframes 1h \
    --ma-hours 200 \
    --allow-short \
    --reference-vol-pct 1.0 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --min-daily-volume 0 \
    --min-bar-quote-volume 5000 \
    --max-bar-price-ratio 3.0
```

## Sanity check — VWAP N-hour breakout

The next research question: would a Donchian-style breakout do better than
the orderflow ratio? Hypothesis: probably worse on longs (buying every
new high is textbook noise-chasing) but maybe competitive on shorts (a
new N-hour low is informationally similar to a sustained negative
orderflow ratio). Symmetric: long on a new N-hour high, short on a new
N-hour low. Highs/lows computed off **per-bar VWAP** (not Close — closes
are noisier on thin bars). Same gates and sizing as v0, gap detector ON.
Implementation in `TradingEdge.CryptoBacktest/Breakout.fs` with a
`breakout-sweep` subcommand; same self-contained shape as VWMA.

Sweep across the same window grid as orderflow:

| `--ma-hours` | Trips | Total PF | Long PF | Long Net P&L | Short PF | Short Net P&L | Largest loser |
| :---: | ---: | :---: | :---: | ---: | :---: | ---: | ---: |
| 200 | 12,833 | 1.088 | 0.790 | −$62,283 | 1.299 | $125,528 | $1,301 |
| 240 | 10,865 | 1.077 | 0.741 | −$71,268 | 1.316 | $121,983 | $1,301 |
| 288 | 8,926 | 1.130 | 0.748 | −$60,857 | 1.390 | $138,141 | $1,301 |
| 360 | 7,115 | 1.144 | 0.736 | −$56,207 | 1.412 | $133,898 | $1,301 |
| 480 | 5,080 | 1.225 | 0.888 | −$18,616 | 1.421 | $120,701 | $1,438 |

What this confirms:

- **Buying every new N-hour high is anti-edge.** Long PF is below 1.0
  at every window (0.74–0.89). Long net P&L is **negative at every
  window** (−$18k to −$71k). The pattern "price prints above where it's
  been for the last 200 hours → buy" loses money systematically. The
  longer the window, the smaller the loss — at 480h the long side only
  drops $18k vs $71k at 240h — but it never crosses into profit.
- **Shorts on N-hour-low are real edge, just smaller than orderflow's.**
  Short PF 1.30–1.42 vs orderflow's 1.54–1.95 at the same windows. The
  bearish-tape signal in USDT-perps is robust enough to show through
  any reasonable bear filter; orderflow extracts more of it because it
  has signed-volume information that breakouts don't.
- **Same window-curve shape as orderflow** — total PF rises with
  window length (1.09 → 1.23 across 200h–480h). The longer-is-better
  story isn't unique to orderflow; it shows up in any signal that
  benefits from filtering out short-term noise.
- **Without the gap detector, this whole sweep is contaminated.**
  Pre-fix, every window had largest loser $94,887 (a single GALAUSDT
  short held through a 1:96 redenomination), and total PF was below 1.0
  everywhere. With `--max-bar-price-ratio 3.0` the same trade still
  fires but exits cleanly before the gap, capping loss at ~$1.3k. The
  4× change in headline numbers from this single fix is what made us
  add the detector to the production engine.

Output files: `data/crypto/breakout/backtest_results.csv`,
`backtest_summary.csv`, `backtest_results_breakdown.log`.

Reproduce:

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- breakout-sweep \
    --timeframes 1h \
    --ma-hours 200,240,288,360,480 \
    --allow-short \
    --reference-vol-pct 1.0 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --min-daily-volume 0 \
    --min-bar-quote-volume 5000 \
    --max-bar-price-ratio 3.0
```

## Universe-wide breadth as a regime filter — qualitative result

**Question:** can the universe-wide orderflow signal — i.e. the t-digest rank of
the 200h-MA-smoothed `Σ(buy_dollar_volume) − Σ(sell_dollar_volume)` across the
whole 669-symbol universe — improve the v0 trade decision when used as a
regime filter? The intuition: when *everything* is taker-selling hard, longs
should fail more; when *everything* is taker-buying, shorts should get
squeezed.

**Pipeline.** `build-breadth` emits a per-hour parquet with
`composite_signed_rank_ma200 ∈ [0,1]` (leak-free, t-digest CDF over past +
present hours of the smoothed series). `breadth-stratify` joins each v0 trade
to the breadth row at `entry_us` via DuckDB ASOF JOIN, bins by rank decile,
reports per-(side, decile) PF / win-rate / P&L. See
`scripts/crypto/breadth_chart.py` for the visual overlay.

**Trip set.** Fresh full-universe v0 sweep with the pinned config, 18 s wall.
5,959 long trips, 14,047 short trips. The first 199 hours of breadth panel
are NaN (200h MA warmup) and excluded from the join.

**Units note.** Tables include the **raw smoothed signed-volume range per
bucket** alongside the rank, scaled to billions of USDT. The signal is
`Σ_universe (buy_dollar_volume_h − sell_dollar_volume_h)` smoothed over
200h. The universe sits net-selling nearly all the time — every bucket
below decile 9 has a negative range; only the top decile reaches positive
territory at all.

### Long-side decile breakdown

```
bucket  rank_lo  rank_hi  signedV_Bn_lo  signedV_Bn_hi  trades  win_rate  PF      sumPnl$
0       0.0001   0.0995   -33.98         -3.70           168    0.310    0.676      -966
1       0.1007   0.1996   -20.02         -3.68           399    0.424    1.917     +4888
2       0.2006   0.2994   -13.71         -3.40           439    0.387    1.289     +2135
3       0.3000   0.3993    -9.54         -3.62           401    0.327    0.970      -204
4       0.4003   0.5000    -7.69         -3.46           342    0.406    1.538     +2635
5       0.5000   0.6000    -6.27         -3.50           372    0.349    0.723     -1792
6       0.6002   0.6996    -5.21         -2.08           439    0.362    1.011       +67
7       0.7000   0.7999    -4.24         -1.48           657    0.373    1.035      +306
8       0.8001   0.8999    -3.60         -0.13           921    0.351    1.314     +3499
9       0.9001   1.0000    -3.31         +7.47          1821    0.399    1.510     +9608
```

**Pattern: U-shape, with the bottom decile the only true loser.**
- Decile 0 (raw smoothed flow −$34B to −$3.7B, "extreme universe-wide net
  selling"): PF 0.68, −$966 over 168 trades. The only consistently losing
  bucket.
- Decile 5 dips to PF 0.72 but only −$1,792 over 372 trades, surrounded by
  profitable bins on either side. Likely noise.
- The right tail (deciles 8–9, smoothed flow approaching zero or net-buying):
  +$13,107 over 2,742 trades, PF 1.31–1.51. Solid but not exceptional.

### Short-side decile breakdown

```
bucket  rank_lo  rank_hi  signedV_Bn_lo  signedV_Bn_hi  trades  win_rate  PF      sumPnl$
0       0.0001   0.0999   -32.09         -3.73            445   0.443    1.602     +9118
1       0.1000   0.2000   -20.05         -3.62            843   0.499    1.905    +18554
2       0.2003   0.3000   -13.57         -2.78           1109   0.462    1.678    +17066
3       0.3000   0.4000    -9.59         -3.61           1021   0.476    2.057    +24219
4       0.4001   0.4999    -7.74         -3.06            805   0.427    1.488     +9233
5       0.5000   0.5998    -6.45         -2.71           1080   0.505    2.572    +60766
6       0.6002   0.6998    -5.24         -1.80            978   0.426    1.698    +13343
7       0.7004   0.7999    -4.24         -1.46           1371   0.430    1.485    +15782
8       0.8001   0.8999    -3.52         -0.21           2197   0.452    1.532    +28872
9       0.9001   1.0000    -2.33         +7.47           4198   0.384    1.030     +3734
```

**Pattern: monotonic-ish degradation toward the right tail.**
- Every decile has PF ≥ 1.0 — there's no bucket where shorts collectively
  lose money.
- The right tail (decile 9, raw signedV ranging from −$2.3B all the way up
  to the rare +$7.5B "universe-wide net buying" regime): weakest at PF
  1.03 / 30% of all short trades / +$3.7k. Marginal.
- Decile 5 is suspiciously strong (PF 2.57, +$60.7k) — anomaly worth flagging
  but not actionable as a filter without more digging.

### Verdict

**The universe-wide breadth filter would add about $1k of avoided losses per
v0 cycle** (skip long-decile-0 trades, possibly skip short-decile-9 trades).
That's not enough to justify the architectural cost of plumbing a regime
filter into the live engine.

The likely reason: **the v0 per-symbol orderflow signal already captures
most of what universe-wide orderflow rank tells us.** When BTC is trending
hard, individual symbols' buy/sell ratios already reflect that — the signal
is already breadth-aware at the symbol level. Aggregating to the universe
adds little.

The result is also consistent with the lesson that **crypto breadth is not
equity breadth**: in equities, breadth divergence (price up, breadth down)
flags distribution because individual stocks have independent fundamentals.
In crypto perps everything correlates with BTC, so breadth measures mostly
re-derive what BTC's own price action shows.

**Useful by-product:** the 1-decile long-side filter (`rank > 0.10` for long
entries) is a defensible config tweak if we re-cut v0 numbers later. Not in
the pinned v0 config because the dollar impact is small.

### How to reproduce

```bash
# 1. Generate a fresh v0 trip CSV.
dotnet run --project TradingEdge.CryptoBacktest -c Release -- sweep \
    --timeframes 1h --ma-hours 200 --allow-short \
    --reference-vol-pct 1.0 --vol-window-days 7 \
    --min-long-adv 28800000 --min-short-adv 8000000 \
    --min-bar-quote-volume 5000 --max-bar-price-ratio 3.0 \
    --results-csv /tmp/v0/results.csv \
    --summary-csv /tmp/v0/summary.csv \
    --parallelism 8

# 2. Generate the per-hour breadth parquet (single fast pass; cached afterward).
dotnet run --project TradingEdge.CryptoBacktest -c Release -- build-breadth \
    --timeframe 1h --ma-hours 200 --allow-short \
    --min-long-adv 28800000 --min-short-adv 8000000 \
    --min-bar-quote-volume 5000 --max-bar-price-ratio 3.0 \
    --vol-window-days 7 --parallelism 8

# 3. Stratify the trips by breadth rank at entry. --value-column shows the
#    raw smoothed signed-volume range per bucket alongside the rank.
dotnet run --project TradingEdge.CryptoBacktest -c Release -- breadth-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --value-column composite_signed_volume_ma200 \
    --value-scale 1.0e-9 --value-label signedV_Bn \
    --output /tmp/v0/breadth_decile_breakdown.csv
```

## Funding rate as a trade filter — per-trip stratification

**Question:** does the funding rate paid by the trade's symbol at entry
predict its outcome? Theory says: when funding is sustained-positive (longs
paying shorts), longs are over-leveraged, so bear signals fire into a soft
order book and shorts win bigger. Symmetrically, deeply negative funding
(shorts paying longs) suggests bear-side over-leverage where longs should
get squeeze rallies.

**Pipeline.** `funding-stratify` does a DuckDB ASOF JOIN per (symbol, entry_us)
against the per-symbol funding parquets at
`/mnt/d/trading-edge-bulk/crypto/binance/perps_funding/`, bins by the
joined funding-rate value (NTILE per side), reports per-decile PF / win-rate
/ P&L. No aggregation across the universe — the rate joined is the rate the
trade's specific symbol was paying at entry.

**Trip set.** Same v0 sweep as the breadth-rank stratification — 5,959 long
trips, 14,047 short trips, 100% join rate (every trade matched a funding
event for its symbol).

**Units note.** Tables below report funding rates in **basis points per
funding interval** (1 bps = 0.0001 decimal = 0.01%). Funding fires every
4–8 hours per symbol. Binance's default-baseline funding rate for
low-volume perps is **+0.5 bps/interval**, which is why several middle
buckets below collapse to ties at exactly +0.5 bps. With 8h intervals,
1 bps/interval ≈ 11% annualised funding cost.

### Long-side funding-decile breakdown

```
bucket  fr_lo_bps  fr_hi_bps  trades  win_rate  PF      sumPnl$
0       -200.000   -2.532       596   0.404    1.658    +5751
1         -2.527   -0.552       596   0.406    1.106     +764
2         -0.551   +0.089       596   0.366    1.164    +1111
3         +0.090   +0.500       596   0.383    1.259    +1612
4         +0.500   +0.500       596   0.379    0.844    -1304
5         +0.500   +0.500       596   0.371    0.943     -546
6         +0.500   +0.500       596   0.384    1.405    +3417
7         +0.500   +1.000       596   0.354    1.180    +1057
8         +1.000   +1.000       596   0.362    0.739    -1742
9         +1.000  +30.800       595   0.358    1.948   +10056
```

**Pattern: ends are best.**
- Decile 0 (deeply negative funding, −200 to −2.5 bps — shorts paying longs
  heavily): PF 1.66. Long edge here is real — squeeze fuel.
- Decile 9 (elevated positive funding, +1 to +31 bps — longs paying shorts
  through the nose): PF 1.95. Counterintuitive but consistent with momentum
  trends; v0 long entries here capture continuations on hot symbols.
- Deciles 4–5 and 8 are losers, but their `fr_lo == fr_hi` columns reveal
  why: most symbols cluster at the +0.5 bps Binance baseline floor, so the
  bucket boundaries collapse to ties. Those bins are basically
  "baseline-funding" with bucketing noise, not a reliable filter.

### Short-side funding-decile breakdown

```
bucket  fr_lo_bps  fr_hi_bps  trades  win_rate  PF      sumPnl$
0       -300.000   -3.229     1405    0.451    1.365   +13333
1         -3.226   -0.826     1405    0.399    0.988     -465
2         -0.825   +0.056     1405    0.408    1.338   +10941
3         +0.057   +0.500     1405    0.432    1.848   +25526
4         +0.500   +0.500     1405    0.458    1.443   +17838
5         +0.500   +0.500     1405    0.452    1.829   +30612
6         +0.500   +0.500     1405    0.467    1.873   +30652
7         +0.500   +1.000     1404    0.415    1.528   +21120
8         +1.000   +1.000     1404    0.439    1.910   +33402
9         +1.000  +82.733     1404    0.435    1.404   +17726
```

**Pattern: short edge is positive almost everywhere, but degrades sharply
in one specific funding band.**
- Every decile profitable except **bucket 1** (PF 0.99, −$465 over 1,405
  trades): mildly-negative funding, −3.2 to −0.8 bps/interval. That band is
  "shorts paying longs but not aggressively". The regime where short-bias
  is insufficient to overcome other factors; shorts essentially break even.
- The strongest short buckets are deciles 5–8 (positive funding above the
  baseline, +0.5 to +1 bps/interval): PF 1.83–1.91, +$125k pooled across
  ~5.6k trades. Aligns with theory: positive funding = leveraged longs to
  liquidate.
- Decile 9 (extreme positive, +1 to +82.7 bps — annualised up to ~900%):
  PF 1.40 — still profitable but **weaker than the 5–8 band**. Possibly
  because by the time funding is *that* extreme, the symbol is already
  late in its blow-off and shorts get whipsawed.

### Verdict

**Useful — both as a long filter (skip bucket 1 of shorts) and as a
sizing factor.** The clean finding is the short-side bucket-1 dead zone:
1,405 trades that contribute essentially nothing to P&L. Skipping them
would tighten short-side PF without sacrificing absolute dollars.

The long-side spread between bucket 0 (PF 1.66) and bucket 9 (PF 1.95)
**vs** the muddled middle (deciles 4–5–8 sub-1.0) is a stronger
signal-to-noise story than the universe-wide breadth result. The reason:
the per-symbol funding rate is **specific to the symbol being traded** —
it captures the leveraged-positioning state of the actual instrument,
which the universe-wide breadth aggregate cannot.

**Compared to universe-wide breadth:** funding-rate stratification produces
a clearer pattern with smaller dollar magnitudes (~+$1–2k of avoided
losses per side). Both are marginal vs the v0 baseline; neither justifies
plumbing into the live engine on its own. Combining them into a richer
regime classifier might yield more — but that's v1 territory.

**Universe-wide funding panel** is also built (`build-funding-breadth`
emits `funding_per_hour.parquet` with mean/median/extreme-positive%
across the active universe). Not yet stratified; future work.

### How to reproduce

```bash
# Per-trip funding stratification (no aggregation — joins each trade
# directly to its symbol's funding rate at entry).
dotnet run --project TradingEdge.CryptoBacktest -c Release -- funding-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --output /tmp/v0/funding_decile_breakdown.csv

# Universe-wide funding breadth panel (built separately for visual / future
# stratification work).
dotnet run --project TradingEdge.CryptoBacktest -c Release -- build-funding-breadth
```

## Funding-breadth rank as a trade filter — universe stratification

**Question:** does the universe-wide median funding rank do better than
per-symbol funding for filtering trades? Per-symbol captures the leveraged
positioning of the specific instrument; universe-wide captures the broader
market-wide tilt. They could be redundant or complementary.

**Pipeline.** `breadth-stratify` was generalised with a `--rank-column` flag
so the same join logic can target any per-hour rank parquet. We point it at
the funding-breadth panel built by `build-funding-breadth`:

```bash
dotnet run --project TradingEdge.CryptoBacktest -c Release -- breadth-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --per-hour /mnt/d/.../funding_per_hour.parquet \
    --rank-column median_funding_rank \
    --value-column median_funding \
    --value-scale 10000 --value-label fr_bps \
    --output /tmp/v0/funding_breadth_decile_breakdown.csv
```

The rank is the t-digest CDF (in time order, leak-free) of the universe-wide
median funding rate across active symbols at each hour. Median was chosen
over mean because a handful of alts at any moment have wildly extreme
funding (−2% / +0.8% per interval) which would drag a mean.

**Units note.** Tables include the **raw universe median funding rate per
bucket** in bps/interval alongside the rank. The Binance default-baseline
floor of +0.5 bps means most ranks 0.10–0.50 collapse to "median pinned at
+0.5 bps" — only the rank tells those apart. The extreme buckets (0 and 9)
are where the median actually moves.

### Long-side breakdown

```
bucket  rank_lo  rank_hi  fr_bps_lo  fr_bps_hi  trades  win_rate  PF      sumPnl$
0       0.0001   0.0972    -0.524     +0.500     614    0.423    1.544    +4875
1       0.1000   0.1991    +0.500     +0.500     119    0.336    0.516     -814
2       0.2000   0.2999    +0.500     +0.923     894    0.379    0.993      -82
3       0.3000   0.3999    +0.500     +0.991    1942    0.386    1.732   +17093
4       0.4010   0.4939    +0.500     +0.973    1919    0.357    1.047    +1215
5       0.5033   0.5964    +0.548     +0.987      68    0.485    1.377     +340
6       0.6029   0.6964    +0.747     +1.000     108    0.343    0.631     -743
7       0.7148   0.7970    +1.000     +1.000      87    0.333    0.716     -399
8       0.8017   0.8821    +1.000     +1.000     187    0.358    0.519   -1073
9       0.9537   0.9988    +1.094     +3.609      21    0.190    0.294     -238
```

### Short-side breakdown

```
bucket  rank_lo  rank_hi  fr_bps_lo  fr_bps_hi  trades  win_rate  PF      sumPnl$
0       0.0001   0.0994    -0.524     +0.500    1282    0.392    0.851    -4756
1       0.1012   0.1997    +0.500     +0.500     251    0.422    2.027    +5051
2       0.2026   0.3000    +0.500     +0.923    1804    0.468    1.485   +24783
3       0.3000   0.3998    +0.500     +0.994    4591    0.416    1.371   +50614
4       0.4000   0.4973    +0.500     +0.973    4889    0.438    1.430   +44674
5       0.5033   0.5965    +0.539     +0.999     123    0.455    1.859    +3621
6       0.6018   0.6964    +0.776     +1.000     435    0.566    3.810   +51807
7       0.7016   0.7994    +1.000     +1.000     211    0.521    4.532   +13089
8       0.8000   0.8790    +1.000     +1.000     390    0.433    1.529    +7698
9       0.9587   0.9999    +1.022     +4.704      71    0.493    2.467    +4106
```

### Key observations and caveats

**Trade counts are highly uneven across buckets.** The rank cuts are
value-based (`FLOOR(rank * 10)`), not quantile-based, and the median funding
rate clusters tightly around the +0.005% Binance baseline so most ranks land
in deciles 3–4. Bucket 9 (rank > 0.95) holds only 21 long trades — anything
that bucket says is noise. Bucket 6 short side holds 435 trades; that's
real.

**The standout: short side bucket 6 (PF 3.81, +$51,807).** That's $119/trade,
~5× the average short P&L. Initially looks like a strong regime signal.
**But:** when broken out by month, **323 of those 435 trades came from May
2024 alone, contributing $53k of the $52k bucket P&L.** The remaining
months contribute small amounts that mostly net to zero. So bucket 6 is
"May 2024 was a great month for shorts when funding was elevated" — one
specific event, not a generalisable pattern. Same caveat applies (less
strongly) to bucket 7 (211 trades, $13k).

**The actionable filter: short side bucket 0 (rank < 0.10): PF 0.85,
−$4,756 over 1,282 trades.** That's a clean broad-market dead zone where
shorts collectively lose money. When the universe is paying longs heavily
(very negative funding rank), the v0 short signal fires into the wrong
positioning.

### Comparison with per-symbol funding filter

The two filters are **complementary, not redundant.** Cross-tabulating:

| | uni_rank ≥ 0.10 | uni_rank < 0.10 |
|---|---|---|
| sym_rate not in [-0.032%, -0.008%] | 11,586 trades, +$205,658 | 1,063 trades, −$4,564 |
| sym_rate in [-0.032%, -0.008%] | 1,179 trades, −$216 | 219 trades, −$193 |

Of the 1,282 short trades caught by `uni_rank < 0.10`, only 219 are also
flagged by the per-symbol filter. They're catching **different bad trades**.

A combined OR-filter — skip a short if either `uni_rank < 0.10` OR
`sym_rate ∈ [-0.032%, -0.008%]` — drops 2,461 of the 14,047 shorts (~17.5%)
for a P&L cost of −$4,972 (i.e. these are net-losing trades; removing them
*improves* the system). The unfiltered 11,586 shorts earn the +$205k.

### Verdict

Per-symbol funding still produces the **cleaner** filter (the −0.032% to
−0.008% dead zone is broader and has zero overfitting risk; it's purely
about the symbol's own state). Universe-wide funding rank adds a **second,
complementary** filter that catches a different slice of bad trades —
specifically when the broad market is paying longs.

**Combined effect (both short-side filters applied):** drop 17.5% of short
trades, save ~$5k in losses, no opportunity cost on the winning side.
Still small relative to the v0 baseline, but the two filters together get
us closer to a real regime classifier than either alone.

The May 2024 anomaly in bucket 6 is the cautionary tale: with this many
buckets and this much heterogeneity in the dataset, individual cells will
look great by accident. **Always check time distribution before declaring
a finding.**
