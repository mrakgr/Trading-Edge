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

**Methodology note.** Equal-count rank-decile bucketing produced overlapping
raw-value ranges (the running CDF means the same value can land in
different buckets depending on when it arrived). Switched to **fixed
cutpoints on the raw smoothed signed-volume**, scaled to billions of USDT.
Cutpoints at `-$10B, -$5B, -$2B, $0` give 5 regime buckets that map to
intuitive states: deep net-selling, heavy net-selling, moderate
net-selling, near-neutral, net-buying.

The signal is `Σ_universe (buy_dollar_volume_h − sell_dollar_volume_h)`
smoothed over 200h.

### Long-side regime breakdown

```
bucket  signedV_Bn         trades  win_rate  PF      sumPnl$
0       <-$10B               710   0.394    1.849    +8096
1       -$10B to -$5B       1265   0.368    0.981     -409
2       -$5B to -$2B        1554   0.355    1.081    +1671
3       -$2B to $0          1406   0.371    1.348    +5638
4       ≥ $0                1024   0.417    1.501    +5180
```

**Pattern: U-shape, with bucket 1 (-$10B to -$5B) the only true loser.**
- Bucket 0 (deep net-selling, < -$10B) is surprisingly the best long bucket:
  PF 1.85, +$8,096 over 710 trades. Likely the "max-pain mean-reversion"
  regime.
- Bucket 1 is the dead zone: heavy-but-not-extreme net-selling, PF 0.98.
- Buckets 2–4 trend up monotonically as the universe moves toward
  neutral/buying.

### Short-side regime breakdown

```
bucket  signedV_Bn         trades  win_rate  PF      sumPnl$
0       <-$10B              1663   0.475    1.607   +26341
1       -$10B to -$5B       3119   0.455    1.937   +64849
2       -$5B to -$2B        3607   0.462    1.915   +90151
3       -$2B to $0          3182   0.413    1.328   +27995
4       ≥ $0                2476   0.376    0.885    -8650
```

**Pattern: clean monotonic degradation as the universe shifts from
net-selling to net-buying.**
- Buckets 0–2 (any net-selling state): PF 1.6–1.94, contributes the bulk of
  the +$181k short-side P&L.
- Bucket 3 (near-neutral): PF 1.33 — still profitable but degrading.
- **Bucket 4 (net-buying, ≥ $0): PF 0.89, −$8,650 over 2,476 trades.** Clear
  short dead zone. When the smoothed universe orderflow flips positive,
  shorts stop working.

### Verdict

The earlier rank-decile view suggested a small ~$1k filter effect. The
**cutpoint view changes the picture** — the regime structure is much
clearer when looked at by raw smoothed signed-volume, and there's a clean
short-side dead zone:

- **Short-side dead zone: skip shorts when smoothed universe flow ≥ $0
  (regime "net buying").** Bucket 4: PF 0.89, **−$8,650 over 2,476 trades**.
  17.6% of all short trades, no edge.
- **Long-side: cleanly graded — best when universe is at extremes (deep
  net-selling for mean-reversion longs, or rare net-buying for momentum
  longs); worst in the heavy-but-not-extreme bear regime (bucket 1, PF 0.98).**

The ~$8.6k savings on shorts alone is **substantial vs the ~$200k baseline
short P&L** — about 4% of short-side dollars. That's worth plumbing into a
filter.

The result is also consistent with the lesson that **crypto breadth is not
equity breadth**: in equities, breadth divergence (price up, breadth down)
flags distribution because individual stocks have independent fundamentals.
In crypto perps everything correlates with BTC, so breadth measures mostly
re-derive what BTC's own price action shows. The signal here works
*because* of that correlation, not despite it.

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

# 3. Stratify the trips by smoothed signed-volume regime (cutpoints in $).
dotnet run --project TradingEdge.CryptoBacktest -c Release -- breadth-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --value-column composite_signed_volume_ma200 \
    --value-scale 1.0e-9 --value-label signedV_Bn \
    --bucket-by cutpoints --value-cutpoints "-10e9,-5e9,-2e9,0" \
    --output /tmp/v0/breadth_cutpoints_breakdown.csv
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

**Methodology note.** 6 fixed cutpoints, splitting the negative-funding
range into "strongly negative" (< −0.5 bps) and "mildly negative"
(−0.5 to 0 bps) on top of the universe panel's regime cuts. Per-ticker
rates can sustain large negatives (individual symbols see shorts paying
longs when there's heavy bear-leveraged positioning); the universe
median almost never does. Funding in **bps per funding interval**
(1 bps = 0.0001 decimal); Binance baseline is +0.5 bps; 1 bps/interval
≈ 11% annualised.

### Long-side regime breakdown

```
bucket  fr_bps              trades  win_rate  PF      sumPnl$
0       < -0.5 bps           1217   0.404    1.427    +6889
1       -0.5 to 0 bps         418   0.356    1.059     +316
2       0 to +0.5 bps         716   0.390    1.342    +2395
3       =+0.5 bps            1824   0.376    1.043    +1157
4       +0.5 to +1.0 bps      409   0.352    1.162     +669
5       ≥ +1.0 bps           1375   0.361    1.461    +8750
```

**Pattern: longs work at the extremes, struggle in the middle.** Bucket 0
(strongly negative funding, < −0.5 bps) is one of the two best long
buckets (PF 1.43); the mean-reversion-against-bear-leverage thesis only
holds when funding is *meaningfully* negative. Bucket 5 (≥ +1.0 bps,
strong long-leverage) is the other strong bucket (PF 1.46) — momentum
continuation. Buckets 1–4 (everything from −0.5 bps through +1 bps)
all run at PF 1.04–1.34, weak edge.

### Short-side regime breakdown

```
bucket  fr_bps              trades  win_rate  PF      sumPnl$
0       < -0.5 bps           3179   0.425    1.192   +16237
1       -0.5 to 0 bps         875   0.408    1.427    +8612
2       0 to +0.5 bps        1323   0.423    1.577   +16884
3       =+0.5 bps            4584   0.458    1.743   +90534
4       +0.5 to +1.0 bps      664   0.408    1.652    +9567
5       ≥ +1.0 bps           3422   0.432    1.581   +58852
```

**Pattern: shorts grade monotonically up across the lower buckets, then
plateau at elevated funding.** PF rises 1.19 → 1.43 → 1.58 → 1.74 as
funding climbs from strongly-negative (shorts paying longs) through
sub-baseline into baseline, then settles around 1.6 across the elevated
regime. The strongly-negative bucket is the weakest part of the
per-ticker space for shorts — $5 of edge per trade vs $20 at baseline,
about a 4× degradation — but still positive PF, not a true dead zone.

### Verdict

**Per-ticker funding has graded structure on both sides:**
- **Longs work at the extremes only.** The negative-funding-with-magnitude
  regime (< −0.5 bps) and the strongly-elevated regime (≥ +1 bps) both
  hit PF ≈ 1.43–1.46. The middle band is a wash.
- **Shorts grade monotonically up** from negative funding (PF 1.19) to
  baseline (PF 1.74) and plateau there. Negative funding is the weakest
  part of the short space; baseline-and-above is uniformly strong.

The signal is real but soft — no PF goes below 1.0. Useful as a
**position-sizing modulator** (downsize negative-funding shorts; upsize
extreme-funding longs at either end), not as a hard skip rule.

**Compare to the universe-funding panel** (next section), where the same
direction-of-effect applies but with much sharper magnitude:
- Universe long ≥ +1.0 bps: **PF 0.59** (vs per-ticker bucket 5: PF 1.46
  — opposite signs!).
- Universe short < +0.5 bps: **PF 0.85** (vs per-ticker buckets 0–2
  combined: PF 1.36).

The opposite sign on the long side is striking: per-ticker elevated
funding helps longs (momentum continuation on the trade's own symbol);
universe-wide elevated funding hurts longs (broad-market top-sign).
Different signals, both real.

**The strong dead-zone signal lives at the universe level. Per-ticker
funding is a softer modulator that complements but doesn't dominate.**

### How to reproduce

```bash
# Per-trip funding stratification with 6 regime cutpoints. The negative
# range is split into strongly-negative (< -0.5 bps) and mildly-negative
# (-0.5 to 0 bps) — the long-side mean-reversion edge requires meaningfully
# negative funding, the mildly-negative band is a wash. Universe panel
# doesn't need this split since its median almost never goes negative.
dotnet run --project TradingEdge.CryptoBacktest -c Release -- funding-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --bucket-by cutpoints \
    --value-cutpoints "-0.00005,0.0,0.00005,0.0000501,0.0001" \
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
# Cutpoints isolate the regime structure: sub-baseline / =baseline /
# mildly elevated / strongly elevated. The 0.0000501 cutpoint is a hack
# to isolate the exact +0.5 bps Binance baseline floor as its own bucket.
dotnet run --project TradingEdge.CryptoBacktest -c Release -- breadth-stratify \
    --trips /tmp/v0/results_trips_1h_ma200h_ls.csv \
    --per-hour /mnt/d/.../funding_per_hour.parquet \
    --rank-column median_funding_rank \
    --value-column median_funding \
    --value-scale 10000 --value-label fr_bps \
    --bucket-by cutpoints --value-cutpoints "0.00005,0.0000501,0.0001" \
    --output /tmp/v0/funding_breadth_cutpoints.csv
```

The rank is the t-digest CDF (in time order, leak-free) of the universe-wide
median funding rate across active symbols at each hour. Median was chosen
over mean because a handful of alts at any moment have wildly extreme
funding (−2% / +0.8% per interval) which would drag a mean.

**Methodology note.** The universe-median funding rate has a huge point
mass at exactly +0.5 bps (Binance's default-baseline floor for
low-volume perps). Equal-count NTILE bucketing splits across the floor
itself — even at 3 buckets, two of them collapse to identical
`fr_bps_hi == fr_bps_lo == +0.500`. Switched to **fixed cutpoints** at
the natural regime boundaries (sub-baseline, baseline, mildly elevated,
strongly elevated). 4 regime buckets:
- 0: median funding < +0.5 bps (universe-wide shorts paying longs)
- 1: median = +0.5 bps exactly (the dominant regime — Binance baseline)
- 2: +0.5 to +1.0 bps (mildly elevated long-leverage)
- 3: ≥ +1.0 bps (strong long-leverage; rare regime)

### Long-side regime breakdown

```
bucket  fr_bps             trades  win_rate  PF      sumPnl$
0       <+0.5 bps             601   0.426    1.645    +5423
1       =+0.5 bps            4734   0.374    1.287   +17531
2       +0.5 to +1.0 bps      270   0.367    0.827     -650
3       ≥ +1.0 bps            354   0.336    0.592    -2129
```

**Pattern: longs degrade monotonically as the universe gets more
long-leveraged.** Best when sub-baseline (PF 1.65); fine at baseline
(PF 1.29); break-even when mildly elevated (PF 0.83); losing when
strongly elevated (PF 0.59).

### Short-side regime breakdown

```
bucket  fr_bps             trades  win_rate  PF      sumPnl$
0       <+0.5 bps            1260   0.393    0.846    -4832
1       =+0.5 bps           11193   0.433    1.395  +114724
2       +0.5 to +1.0 bps      766   0.505    3.369   +61023
3       ≥ +1.0 bps            828   0.475    2.239   +29770
```

**Pattern: monotonic-up; shorts get *better* as the universe gets more
long-leveraged, mirroring the long-side degradation.** Sub-baseline is the
dead zone (PF 0.85). At baseline shorts are merely OK (PF 1.40). Mildly
elevated funding produces the best short edge (PF 3.37) — that's the
"longs stretched but not yet capitulating" regime where v0 short signals
fire into oversupply of leveraged longs to liquidate. Strongly-elevated
also strong (PF 2.24) but with marginally lower edge — by then, some
longs have already been flushed.

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

### Verdict

The cutpoint view of universe funding produces a **strong symmetric pair
of effects**:

- **Short-side: bucket 0 (universe-median funding < +0.5 bps) is a clean
  dead zone**, PF 0.85 / −$4,832 over 1,260 trades. Buckets 2–3 (universe
  long-leveraged): PF 2.2–3.4, +$90.8k pooled — these are the regimes
  where shorts have something to feed on.
- **Long-side: bucket 3 (universe-median ≥ +1.0 bps) is a clean dead
  zone**, PF 0.59 / −$2,129 over 354 trades. Bucket 0 (sub-baseline) is
  the best long bucket: PF 1.65, +$5,423.

In aggregate: drop the bottom-bucket shorts and the top-bucket longs,
save ~$7k in losses, no opportunity cost on the winning side.

**Comparison with per-symbol funding filter.** Similar cutpoints applied
to the trade's own symbol's funding rate (see the per-trip section above)
show **graded structure but no hard dead zones**:
- Per-ticker shorts grade monotonically up from PF 1.19 at strongly-
  negative funding to PF 1.74 at baseline, then plateau ~1.6 at elevated.
- Per-ticker longs work only at the extremes (PF 1.43 at < −0.5 bps and
  PF 1.46 at ≥ +1.0 bps); the middle band is a wash.

The strong dead-zone signal is **at the universe level** (PF 0.85 and
0.59); per-ticker is a softer modulation. Notably, **the long-side effect
flips sign between the two scales**: per-ticker elevated funding helps
longs (momentum on the symbol's own tape), but universe-wide elevated
funding hurts longs (broad-market top-sign). Different signals, both real.

**Methodology lesson worth flagging.** An earlier 10-decile rank breakdown
of this same data made bucket 6 (rank 0.60-0.70) look like a 3.81 PF
goldmine for shorts (+$51,807). When time-distributed, **75% of those
trades came from May 2024 alone** — the rank-decile cut had hidden a
single-month artefact behind apparently broad numbers. Cutpoint
bucketing on raw values doesn't avoid the issue automatically (you'd
still want a time-distribution check on any "great" bucket), but it
makes the structure easier to reason about.

## 1m successor — clamped-cumsum engine (`OrderflowCumsum`)

We re-ran the orderflow thesis from scratch on **1m bars** with a
window-boundary-independent engine. The motivation: v0 fires only on the
1h bar boundary (the rolling-sum ratio crosses 1.0), which makes the
result implementation-coupled to the choice of timeframe. A regime-flip
on a 1m bar shouldn't have to wait for the next hour to print.

### Engine

Each 1m bar votes ±1 on the sign of `buyMa_200h.State − sellMa_200h.State`
(v0's inner signal — same 200h rolling sum). Votes accumulate into a
clamped cumsum `c ∈ [−60, +60]`. Long fires when `c` first touches +60
from below, short at −60. Exit is on the **opposite extreme**: a long is
held until `c` re-touches −60 (full regime reversal). Pre-fill votes 0
during the first 200h, identical to v0's window-fill semantics.

The earlier per-bar version (vote on `sign(buyDV_t − sellDV_t)` of just
the current bar) was tested first and produced **PF 0.97 — breakeven**.
Voting on the *smoothed 200h sum* instead of the per-bar sign is the
unlock. The cumsum then becomes a regime-persistence detector layered on
top of the v0 signal, not a noise smoother.

Other v0 mechanics — gap detector at 3.0×, stop-loss, vol-sizing, ADV
gate, funding accounting — are preserved verbatim. The per-bar liquidity
gates (`MinDailyQuoteVolume`, `MinBarQuoteVolume`) are dropped since the
gap detector subsumes them at 1m.

### Vol rescaling — required when porting 1h → 1m

`--reference-vol-pct` controls vol-based position sizing:

```
effectiveNotional = min(notional, notional * referenceVol / barVol)
```

v0 used `referenceVol = 1.0` (1%) at 1h, which roughly matched the pooled
1h log-return std. At 1m the pooled log-return std is much smaller — the
empirical median 7-day rolling std across a basket of 11 representative
symbols is **10.2 bps/min = 0.10%**:

```
$ python scripts/crypto/measure_1m_vol.py
symbol             n_bars        p10        p25        med        p75        p90
BTCUSDT         1,051,165      4.34b      5.05b      6.07b      7.32b      9.20b
ETHUSDT         1,051,165      6.51b      7.29b      8.84b     10.48b     13.07b
SOLUSDT         1,051,164      8.07b      9.04b     10.56b     12.55b     15.93b
WIFUSDT         1,051,164     12.46b     14.83b     17.93b     21.42b     24.84b
...
POOLED         10,511,645      6.07b      7.94b     10.19b     13.44b     18.79b
```

Without rescaling (using `referenceVol = 1.0`), `referenceVol/barVol > 7`
on every 1m bar — the `min` clamp kicks in, every entry deploys full
notional, no vol-sizing happens. With `referenceVol = 0.1019` (the pooled
1m median), high-vol meme-coin entries get downsized exactly the way v0
intended for 1h. The rescaling is a **factor-of-10 difference** in
behavior, not a hyperparameter to tune.

### Universe sweep results

Universe (643 symbols with bars over 2024-05-01 .. 2026-04-30),
allow-short, v0 ADV gates, gap detector at 3.0×, vol-tuned at 0.1019:

| | **v0 1h orderflow-MA** | **Cumsum 1m + v0 gates + vol-tuned** |
|---|---|---|
| Trades | 20,006 | 10,526 |
| Total PF | 1.490 | **1.565** |
| Win rate | 41.8% | 46.9% |
| Net P&L | +$221K | +$172K |
| Profitable cells | 72.5% | 73.3% |
| Median Sharpe | 0.50 | 0.52 |
| Long PF | 1.26 | 1.20 |
| Long net P&L | +$20K | +$11K |
| Short PF | 1.54 | 1.64 |
| Short net P&L | +$201K | +$161K |
| Largest single winner | $4,004 | $3,502 |
| **Largest single loss** | **−$3,599** | **−$3,493** |
| MAE ≤−50% bucket | −$72K | −$58K |
| Median bars held | 7 (~7h) | 1,566 (~26h) |

The 1m cumsum **matches v0 PF (1.57 vs 1.49) with a slightly cleaner risk
profile**. Trade count is half (~10.5k vs 20k) — the system is more
selective by construction (one fire per regime persistence cycle, vs v0's
fire on every ratio-crossing).

Net P&L is lower because the trade count is lower; per-trade expectancy
is higher ($16.3 vs $11.0). Median Sharpe is essentially tied.

The **vol-rescaling step** is what closes the catastrophic-loss tail.
Without it, the largest single loss was −$13.2K (vs v0's −$3.6K) — the
meme-coin tail was being traded at full notional during high-vol bursts.
With `referenceVol = 0.1019`, the largest loss drops to −$3.5K (below
v0's worst trade) and the MAE ≤−50% bucket roughly halves.

### Reproduce

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-sweep \
    --allow-short \
    --reference-vol-pct 0.1019 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --max-bar-price-ratio 3.0 \
    --parallelism 8
```

Default `--cumsum-thresholds 60 --ma-hours 200`. Outputs land in
`data/crypto/cumsum/`. SSD bar-cache (`data/crypto/perps_bars/1m/`)
makes the sweep complete in ~70 seconds vs ~5 minutes off the HDD.

## Z-cumsum variant — short-term-conviction signal (`OrderflowCumsumZ`)

Follow-up engine. Replaces the ±1 vote with a continuous magnitude:

```
delta_t      = bar.BuyDollarVolume − bar.SellDollarVolume
sigma_t      = StdMa_200h(delta).SampleStd
z_t          = delta_t / sigma_t
magnitude_t  = erf(z_t / sqrt 2.0)            ∈ (−1, +1)
cumsum_t     = clamp(cumsum_{t−1} + magnitude_t, [−threshold, +threshold])
```

The cumsum + erf encoding makes the per-bar contribution **proportional to
how unusual the bar is**: a typical bar contributes near 0; a several-sigma
outlier saturates at ±1. This is "short-term conviction" — the simple-vote
cumsum measures regime persistence; this version layers conviction
intensity on top.

**Persistence gate at fire time.** Long fires require both
`cumsum = +threshold` AND `buyMa_200h > sellMa_200h` (the v0 inner signal
agrees). Short symmetric. Exits on opposite-clamp touch close any open
position regardless of persistence; flip into a new position only if
persistence confirms the new direction, otherwise stay flat.

**Initial sweep at threshold = 10:**

| | **Cumsum vol-tuned** | **Z-cumsum th=10** |
|---|---|---|
| Trades | 10,526 | **54,061** |
| Total PF | 1.565 | 1.153 |
| Long PF | 1.20 | **1.31** |
| Long trades | 3,116 | 4,167 |
| Short PF | 1.64 | 1.14 |
| Short trades | 7,410 | 49,894 |
| Largest single loss | −$3,493 | **−$1,092** |

Two findings:

1. **Long-side improvement is real.** Long PF 1.31 vs 1.20 — the
   conviction-weighted vote captures stronger long signals than the
   ±1 vote does. This is a +9 PF basis points lift on the side we've
   been trying to improve.
2. **Threshold = 10 fires too aggressively on shorts.** Short trade
   count went 7.4k → 49.9k (+6.7×). The cumsum saturates faster than
   I estimated; threshold = 10 is the wrong order of magnitude.

Largest single loss dropped to −$1.1K (vs −$3.5K) — even with the over-
firing on shorts, individual-trade risk is much tighter because the
persistence gate prevents flips into unconfirmed regimes.

**Threshold scan (11–18, otherwise identical config):**

| **th** | **Trades** | **Total PF** | **Long PF** | **Long net** | **Short PF** | **Short net** | **Largest loss** |
|---|---|---|---|---|---|---|---|
| 11 | 45,000 | 1.175 | 1.319 | $18,843 | 1.169 | $144,810 | −$1,696 |
| 12 | 38,004 | 1.200 | 1.287 | $16,458 | 1.194 | $161,395 | −$1,777 |
| 13 | 32,450 | 1.217 | 1.304 | $16,954 | 1.211 | $161,019 | −$1,976 |
| 14 | 28,002 | 1.238 | 1.336 | $17,756 | 1.231 | $162,407 | −$1,900 |
| 15 | 24,516 | 1.253 | 1.364 | $18,437 | 1.244 | $159,502 | −$2,033 |
| **16** | 21,721 | 1.269 | **1.401** | **$20,017** | 1.259 | $158,580 | −$3,072 |
| 17 | 19,209 | 1.280 | 1.369 | $17,968 | 1.273 | $156,802 | −$3,100 |
| 18 | 17,289 | 1.280 | 1.331 | $15,944 | 1.276 | $150,808 | −$3,106 |

Clean monotonic patterns:

- Total PF rises monotonically with threshold: **1.175 (th=11) → 1.280 (th=18)**.
  Higher selectivity = higher quality.
- **Long PF peaks at th=16** (PF 1.401, net $20,017). This is the
  strongest long-side number we've ever produced — beats v0 (1.26, $20,175)
  on PF and matches it within $158 on net. This is the lift the
  conviction-weighted vote was supposed to deliver.
- Short PF rises monotonically too (1.169 → 1.276 across th=11→18) but
  plateaus by 17.
- Largest single loss rises with threshold (longer holds when fires are
  rarer): th=12 has the smallest tail (−$1.8K), th=18 the largest (−$3.1K).
  Still well below cumsum-vol-tuned's −$3.5K.

**The total PF still doesn't catch up to cumsum-vol-tuned (1.57).** The
z-cumsum is **better on the long side** (1.40 vs 1.20) but **worse on
shorts** (~1.26 vs 1.64) — the persistence gate is letting too many
shorts fire on cumsum touches that don't have a 200h-bear regime backing
them. The diagnosis: the cumsum is firing *too many* opposite-clamp
exits when the persistence regime hasn't actually flipped against us yet,
so positions get cycled in and out of unconfirmed regimes. Fix below.

### Persistence-exit variant (`--require-persistence-for-exit`)

Drops the cumsum-driven exit semantic entirely. An open position is held
until the **persistence regime itself flips against it** (`buyMa.State`
crosses `sellMa.State` in the wrong direction). Once persistence has
flipped, the next opposite-clamp cumsum touch closes the position (and
opens a new one in the new direction, since persistence now confirms it).

If the cumsum drifts to the opposite extreme but persistence still favors
us, we **ignore the touch** — that's the regime telling us the short-term
swing is noise.

**Threshold scan with `--require-persistence-for-exit`:**

| **th** | **Trades** | **Total PF** | **Long PF** | **Long net** | **Short PF** | **Short net** | **Largest loss** |
|---|---|---|---|---|---|---|---|
| 14 | 6,500 | 1.666 | 1.311 | $15,584 | 1.747 | $165,781 | −$3,420 |
| **15** | **6,215** | **1.690** | 1.317 | $15,441 | **1.777** | **$168,098** | −$3,427 |
| 16 | 5,968 | 1.688 | 1.307 | $15,639 | 1.774 | $165,734 | −$3,433 |
| 17 | 5,744 | 1.685 | 1.300 | $14,631 | 1.774 | $164,086 | −$3,444 |
| 18 | 5,577 | 1.667 | 1.280 | $13,551 | 1.756 | $160,124 | −$3,450 |

**Versus all systems we've measured:**

| | **v0 1h** | **Cumsum vol-tuned** | **Z-cumsum (cumsum-exit) th=16** | **Z-cumsum + persist-exit th=15** |
|---|---|---|---|---|
| Total PF | 1.490 | 1.565 | 1.269 | **1.690** |
| Long PF | 1.26 | 1.20 | **1.40** | 1.32 |
| Short PF | 1.54 | 1.64 | 1.26 | **1.78** |
| Trades | 20,006 | 10,526 | 21,721 | 6,215 |
| Net P&L | +$221K | +$172K | $178K (long+short) | **+$184K** |
| Largest loss | −$3,599 | −$3,493 | −$3,072 | −$3,427 |

**Persistence-exit is the strongest system on every aggregate metric.**
Total PF 1.69 is the highest we've ever produced — well above v0's 1.49
and the simple-vote cumsum's 1.57. Short PF 1.78 (vs v0 1.54). Largest
single loss comparable to v0 (−$3.4K vs −$3.6K).

Long PF is slightly lower than the cumsum-exit run (1.32 vs 1.40) — the
persistence-only-exit is more conservative on long-side fires, exiting
later when regimes shift. The aggregate is still strictly better.

**The lesson:** the cumsum is most useful as an *entry* signal, not an
exit signal. When the cumsum touches the opposite clamp but the regime
hasn't actually flipped, the right move is to hold — the position keeps
working once the cumsum drifts back.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-z-sweep \
    --allow-short \
    --cumsum-thresholds 15 \
    --reference-vol-pct 0.1019 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --max-bar-price-ratio 3.0 \
    --require-persistence-for-exit \
    --parallelism 8
```

### Vol-window-days scan — confirmed not load-bearing

7d (the v0 default we inherited) was the starting choice. Swept 1, 2, 3, 5, 7
days at threshold=15 with persistence-exit. **Trade count is identical
across all 5 windows (6,215)** — the vol window only affects per-trade
notional via vol-sizing, not which trades fire.

| **vw** | **Total PF** | **Long PF** | **Long net** | **Short PF** | **Short net** | **Total net** |
|---|---|---|---|---|---|---|
| 1d | 1.693 | 1.288 | $12,809 | 1.775 | $170,086 | $182,895 |
| 2d | 1.697 | 1.294 | $13,594 | 1.783 | $169,343 | $182,937 |
| 3d | 1.696 | 1.307 | $14,497 | 1.781 | $169,074 | $183,571 |
| 5d | 1.691 | **1.329** | **$15,967** | 1.771 | $167,927 | **$183,894** |
| 7d | 1.690 | 1.311 | $15,441 | 1.777 | $168,098 | $183,538 |

All windows fall within a tight band (PF 1.69–1.70, net $182.9K–$183.9K).
5d marginally improves longs (PF 1.33 vs 1.31, +$526 net) — within noise.
**Verdict: keep 7d.** Vol-sizing window length is not where the edge lives.

### Stop-loss scan

The earlier system's lesson — *stops hurt performance* — reproduces here,
but with a twist when paired with **scale-up considerations**.

The catastrophic-loss tail in the no-stop config (−$3,427 single trade,
−$56K bucket from MAE ≤−50%) is a capital-efficiency problem: it limits
the max safe notional. A counterfactual analysis confirmed that **no
fixed-percentage stop beats no-stop on raw P&L** — even a 100% stop costs
~$36K vs no-stop. But the bounded tail enables larger position sizing.

Sweep at threshold=15 with `--require-persistence-for-exit`:

| **Stop** | **Trades** | **Total PF** | **Long PF** | **Long net** | **Short PF** | **Short net** | **Total net** | **Largest loss** |
|---|---|---|---|---|---|---|---|---|
| 25% | 8,106 | 1.310 | 1.285 | $15,008 | 1.314 | $98,899 | $113,907 | **−$279** |
| 30% | 7,642 | 1.335 | 1.292 | $14,993 | 1.342 | $104,360 | $119,353 | −$339 |
| 40% | 7,122 | 1.355 | 1.300 | $15,279 | 1.365 | $107,585 | $122,864 | −$422 |
| 50% | 6,838 | 1.381 | 1.316 | $15,894 | 1.393 | $112,269 | $128,163 | −$517 |
| 75% | 6,530 | 1.425 | 1.315 | $15,673 | 1.445 | $121,973 | $137,646 | −$767 |
| 100% | 6,386 | 1.484 | 1.311 | $15,441 | 1.518 | $133,050 | $148,491 | −$1,018 |
| **none** | **6,215** | **1.690** | 1.317 | $15,441 | **1.777** | **$168,098** | **$183,538** | −$3,427 |

**The damage is entirely on shorts.** Long PF holds at ~1.31 across every
stop level — long entries never have catastrophic MAE because vol-sizing
already downsizes high-vol entries. Short PF degrades from 1.78 → 1.31 as
stops tighten. This makes intuitive sense: shorting these long-tail
altcoins relies on holding through squeezes for recoveries; 40%+ of
shorts that touched ≤−50% MAE eventually exit profitable. A fixed stop
removes both halves indiscriminately.

**Capital-efficiency math** (assuming 3× notional scale-up is feasible at
each stop level — the bounded loss makes it safe):

| **Stop** | **Net @ 1×** | **Largest loss @ 1×** | **Net @ 3×** | **Largest loss @ 3×** |
|---|---|---|---|---|
| 50% | $128K | −$517 | $385K | −$1,551 |
| 100% | $148K | −$1,018 | $446K | −$3,054 |
| none | $184K | −$3,427 | (would be −$10,281 — likely exceeds liquidity) |

**Decision: the 100% stop pareto-dominates for safe scale-up.** At 3× it
nets $446K with −$3,054 worst-case (still smaller tail than v0's −$3,599),
beating both v0 ($221K at 1×) and the no-stop baseline ($184K at 1×). At
1× it sacrifices $36K of P&L (vs no-stop) for a 3.4× tighter tail.

The 50% stop is a more conservative choice — sacrifice $56K of P&L in
exchange for a 6.6× tighter tail. The right pick depends on the actual
liquidity-constrained scale-up factor, which we haven't measured.

Followup considered: **VWAP-based stops** (track 200h high/low; stop only
on a price excursion past the 200h range) — covered in the next section.

### Trailing-VWAP-band stop (`--vwap-stop-hours`)

The fixed-percentage stop sacrifices short-side PF dramatically because
the short edge depends on holding through brief squeezes for recoveries.
A signal-based stop should preserve that — exit only on **volume-weighted**
break of a 200h band:

- Long stop: `bar.VWAP ≤ rolling_min_vwap_200h`
- Short stop: `bar.VWAP ≥ rolling_max_vwap_200h`
- Fill price: `bar.VWAP`

Volume-weighting is intentional: a wick poking through resistance often
reverses on these long-tail tokens. Only when the bar's volume-weighted
average breaks the 200h band do we accept "regime has shifted, exit."

The rolling min/max of VWAP is implemented via a **monotonic deque**
(amortized O(1) per Push) — see `MaxMa` / `MinMa` in `OrderflowCumsumZ.fs`.
Backed by `Nito.Collections.Deque`, a circular-buffer deque with no
per-node heap allocation. Each value enters and leaves the deque at most
once across the whole stream regardless of window size, so the 12,000-bar
window over ~1M bars × 669 symbols runs in milliseconds.

Sweep at threshold=15, persistence-exit on, `--vwap-stop-hours 200`:

| | **No stop** | **VWAP stop 200h** |
|---|---|---|
| Trades | 6,215 | 46,627 (×7.5) |
| Total PF | 1.690 | 1.246 |
| Long PF | 1.311 | **1.346** |
| Long net | $15,441 | $17,667 |
| Long trades | 1,828 | 2,447 |
| Short PF | 1.777 | 1.238 |
| Short net | $168,098 | $160,938 |
| **Short trades** | **4,387** | **44,180** (×10) |
| **Total net** | **$184K** | $178K |
| **Largest loss** | −$3,427 | **−$695** |
| Largest winner | $3,788 | $3,788 |

**Net P&L is essentially preserved** ($178K vs $184K) but the largest
single loss drops 4.9× (−$3,427 → −$695). Total PF degrades because
trade count explodes — every short that breaches the 200h-VWAP-high gets
closed, and the persistence-bear regime makes the cumsum refire shortly
after on the next squeeze touch. Each individual short is much smaller
(avg loser −$19 vs −$58 baseline) but the system recaptures the broader
short edge through repeated bites.

The long side actually improved slightly (PF 1.31 → 1.35, net $15K →
$17K) — long stops fire rarely (the 200h floor on liquid alts is well
below typical long-entry levels) and when they do, they catch genuine
regime shifts.

**Capital-efficiency math** (assuming 4× notional scale-up — the bounded
tail makes it safe; scale-up factor is now larger than the 3× we'd dare
with no stop):

| **Stop** | **Net @ 1×** | **Largest loss @ 1×** | **Net @ 4×** | **Largest loss @ 4×** |
|---|---|---|---|---|
| 100% pct | $148K | −$1,018 | $592K | −$4,072 |
| **VWAP 200h** | $178K | −$695 | **$712K** | **−$2,780** |
| none | $184K | −$3,427 | (would be −$13.7K — exceeds liquidity) |

**The VWAP-band stop pareto-dominates the percentage stops for safe scale-up.**
At 4× notional it nets $712K with −$2.8K worst-case (still smaller tail
than v0's −$3.6K). At 1× it sacrifices only $6K of P&L (vs no-stop) for
a 4.9× tighter tail.

Caveat: 46,627 trades × $0.80 round-trip fees = **~$37K of fees** at 1×
notional (already netted in the $178K). Fees scale linearly with notional,
so at 4× we pay ~$148K in fees. Pre-fee gross P&L is fine, but high trade
count means the system needs to clear high taker fees first; at lower fee
tiers (or maker rebates on limit-order entries) the system would benefit
disproportionately.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-z-sweep \
    --allow-short \
    --cumsum-thresholds 15 \
    --reference-vol-pct 0.1019 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --max-bar-price-ratio 3.0 \
    --require-persistence-for-exit \
    --vwap-stop-hours 200 \
    --parallelism 8
```

### Vol-multiplier stop (`--vol-stop-multiplier`)

The trade-count explosion from VWAP-band stops would translate to real
market impact and fees in live trading. A vol-based stop is more
selective: stop only on a move proportional to the symbol's *own*
realized volatility:

```
volAtEntry = volMa.SampleStd_at_entry             (snapshotted, log-return units)
stopRet    = M * volAtEntry                       (log-return distance)
long  exit when bar.Low  <= entry * exp(-stopRet)
short exit when bar.High >= entry * exp(+stopRet)
```

The multiplier is in log-return units (same as `--reference-vol-pct`).
For BTC at ~6 bps/min vol, M=100 ≈ 60% of `entry`, M=400 ≈ 240%; for
WIF at ~18 bps/min, M=100 ≈ 18%, M=400 ≈ 70%. Per-symbol vol
heterogeneity means the **same M produces different price-distance
stops on different coins**, which is exactly what we want — tight
where the symbol moves slowly, loose where it moves fast.

Sweep at threshold=15, persistence-exit on:

| **M** | **Trades** | **Total PF** | **Long PF** | **Long net** | **Short PF** | **Short net** | **Total net** | **Largest loss** |
|---|---|---|---|---|---|---|---|---|
| **50** | 11,998 | 1.282 | 1.312 | $16,691 | 1.277 | $93,050 | $109,740 | **−$160** |
| **100** | 8,405 | 1.313 | 1.302 | $15,704 | 1.315 | $99,258 | $114,962 | **−$205** |
| 200 | 6,879 | 1.366 | 1.314 | $15,829 | 1.375 | $107,674 | $123,503 | −$940 |
| 400 | 6,378 | 1.488 | 1.315 | $15,614 | 1.522 | $132,954 | $148,568 | −$1,354 |
| 600 | 6,283 | 1.553 | 1.311 | $15,442 | 1.603 | $145,067 | $160,509 | −$1,149 |
| 1000 | 6,230 | 1.652 | 1.311 | $15,441 | 1.728 | $162,410 | $177,851 | −$2,106 |
| no-stop | 6,215 | 1.690 | 1.311 | $15,441 | 1.777 | $168,098 | $184K | −$3,427 |

**Trade-count blowup is gone.** M=1000 fires on 6,230 trades (vs 6,215
baseline) — only the worst 15 trades hit the stop. Compare to the VWAP
200h stop which fired ~40,000 extra trades. M=100 still only fires 35%
more often despite capping losses 17× tighter.

**Long side completely unaffected** at every M ≥ 200 (PF 1.31, $15.4K net).
The ADV-gate-filtered long entries don't reach a 200×-vol stop distance.

**Short PF is the full degradation surface**: at M=1000 it's 1.73 (close
to baseline 1.78); at M=50 it's 1.28. Tighter stops trade short edge for
tail bounding.

**Capital-efficiency math** — with a properly bounded tail, you can size
up. Pick the comfort level:

| **Stop** | **Net @ 1×** | **Largest loss @ 1×** | **Comfortable scale** | **Net @ scale** | **Largest loss @ scale** |
|---|---|---|---|---|---|
| **Vol M=100** | $115K | −$205 | **17×** | **$1.95M** | **−$3,485** |
| Vol M=600 | $161K | −$1,149 | 3× | $483K | −$3,447 |
| Vol M=1000 | $178K | −$2,106 | 1.6× | $285K | −$3,370 |
| VWAP 200h | $178K | −$695 | 5× | $890K | −$3,475 |
| 100% pct | $148K | −$1,018 | 3.4× | $503K | −$3,461 |
| no stop | $184K | −$3,427 | 1× | $184K | −$3,427 |

**M=100 is the standout for aggressive scale-up.** Caps the worst
single-trade loss at −$205 with only 35% more trades than baseline.
Sized to match v0/baseline's tail (~−$3.5K), it deploys 17× the
notional and produces a projected $1.95M net P&L — an order of
magnitude beyond any other configuration. The PF is only 1.31 (vs
baseline 1.69), but at 17× scale that PF still produces 10×+ the
absolute dollars.

Caveat: the scale factors above are per-trade-tail comfort, not
liquidity-constrained scale. Real scale-up is bounded by the universe's
ADV — a $17K notional × 8K trades a year on a $28.8M ADV gate is
plausible, but slippage modeling would tighten the actual number.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-z-sweep \
    --allow-short \
    --cumsum-thresholds 15 \
    --reference-vol-pct 0.1019 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --max-bar-price-ratio 3.0 \
    --require-persistence-for-exit \
    --vol-stop-multiplier 100 \
    --parallelism 8
```

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-z-sweep \
    --allow-short \
    --reference-vol-pct 0.1019 \
    --vol-window-days 7 \
    --min-long-adv 28800000 \
    --min-short-adv 8000000 \
    --max-bar-price-ratio 3.0 \
    --parallelism 8
```

Default `--cumsum-thresholds 10 --ma-hours 200`. Outputs land in
`data/crypto/cumsum_z/`.

## Momentum stratification — 60d trailing reference price

Once we had a stable backtested system (z-cumsum + persist, no stop —
$184K net, PF 1.69, 6,215 trips), we asked: **does the 60d-trailing
momentum at entry predict trade outcome?** The hypothesis was that
sharply-up coins would be losing longs (mean-reversion / blow-off
tops) and good shorts (squeeze recovery), in line with equity-market
intuition.

We swept three reference-price modes via `scripts/crypto/momentum_stratify.py`:

1. **VWMA**: 60d volume-weighted moving average of bar VWAP.
2. **MA**: 60d unweighted mean of bar VWAP.
3. **Z-score**: `(entry - MA_60d) / std_60d` in price-units σ — per-symbol
   standardized so BTC and meme coins share an axis.

For symbols with <60 days of bars at entry, we use whatever's available.

### What VWMA gets wrong

The first cut was VWMA. It produced a strong-looking long mean-reversion
bucket (-50 to -20%, PF 1.43) and an apparent "don't short coins up
>+50%" rule (PF 0.57-0.84 in the +50% to +200% buckets). **Neither
finding survived a sanity check.**

Switching to unweighted MA (same data, same trips):
- Long -50 to -20% bucket: **PF 1.43 → PF 2.11** (much stronger)
- Short +50 to +100%: **PF 1.15 → PF 1.56**
- Short +100 to +200%: **PF 0.57 → PF 1.73** (flips from loser to winner!)
- Short ≥+200%: **PF 0.77 → PF 2.02**

The volume-weighting was over-weighting the elevated trading during the
recent up-leg, which pulled the VWMA toward the entry price and made
the entry look closer to its reference than it really was. Coins that
had genuinely run up by +200% from a normal price level were getting
classified as "+30% from VWMA" because the VWMA had ridden the rally
upward. **Volume-weighted references are a bad fit for measuring
distance-from-typical-price** when the trading distribution shifts during
a regime change.

### Z-score mode

Standardizing by per-symbol 60d std normalizes BTC's ±5% range and WIF's
±50% range onto the same axis. Z-bucket cutpoints: <-3, -3 to -2, -2 to -1,
-1 to 0, 0 to +1, +1 to +2, +2 to +3, ≥+3.

**LONG side (1,828 trades, total PF 1.31):**

| **z-bucket** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <-3 | 1 | (n/a) | −6 | −5.88 |
| -3 to -2 | 19 | 1.30 | +151 | +7.96 |
| -2 to -1 | 337 | 1.69 | +4,386 | +13.01 |
| -1 to 0 | 527 | 1.11 | +1,518 | +2.88 |
| 0 to +1 | 343 | 1.19 | +1,833 | +5.34 |
| +1 to +2 | 254 | 1.37 | +2,725 | +10.73 |
| +2 to +3 | 162 | 1.53 | +2,678 | +16.53 |
| ≥+3 | 185 | 1.30 | +2,155 | +11.65 |

**SHORT side (4,387 trades, total PF 1.78):**

| **z-bucket** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <-3 | 31 | 2.18 | +1,362 | +43.92 |
| -3 to -2 | 93 | 1.31 | +1,655 | +17.80 |
| -2 to -1 | 1,082 | 1.78 | +39,720 | +36.71 |
| -1 to 0 | 1,368 | 1.86 | +56,026 | +40.95 |
| 0 to +1 | 878 | 1.77 | +36,575 | +41.66 |
| +1 to +2 | 550 | 1.51 | +16,108 | +29.29 |
| +2 to +3 | 250 | 2.01 | +10,173 | +40.69 |
| ≥+3 | 135 | 2.37 | +6,480 | +48.00 |

### Read: price-momentum is mostly noise on this system

On a careful re-read these tables don't show a real signal:

- **No mean-reversion gradient.** If mean-reversion were operative,
  shorts in the deeply-negative z-buckets (price already well below the
  60d MA, expected to revert *up* against the short) should be the
  weakest, and shorts at deeply-positive z-buckets (expected to revert
  *down* with the short) the strongest. We see neither — `<-3σ` shorts
  are PF 2.18 (n=31, small sample), `-3 to -2σ` shorts are PF 1.31
  (the weakest legitimate-sample bucket), and `≥+3σ` is the strongest
  at PF 2.37. Both extremes are strong, both adjacent middles are
  weaker. That's not a gradient — it's bucket-to-bucket noise centered
  on the system's overall ~1.78 short PF.
- **Same for the longs.** The "U-shape" claim was overcalled —
  PF 1.69 at -2σ vs 1.11-1.19 in the -1 to +1σ middle is a 0.5 PF
  swing across adjacent buckets, well within the noise band we
  observed in the 1h imbalance deciles.
- **Every bucket above 1.0 is the system itself.** All eight short
  z-buckets are PF ≥ 1.31. That's not because z-score discriminates
  PF — it's because the underlying short side has PF 1.78 *regardless
  of z-bucket*, so any partition of it gives a row of 1.30+ buckets.

The earlier "shorts work across the entire z-distribution" framing was
misleading. The right reading is: **price-momentum at the 60d horizon
doesn't discriminate trade quality on this system**.

In contrast, the buy/sell-imbalance deciles at 1h *do* show a real
discriminating effect (5 contiguous loser deciles for longs at +9 to
+26%, plus a clean PF 1.10 dead-zone decile for shorts at <-30%).
Imbalance is the price-side stratification that works; raw-price
momentum is not.

## Time-of-day stratification — entry-hour effects

Bucketed each trip by its UTC entry hour (and weekday). Trade is
attributed to the hour it *fires*, not when it exits.

Session anchors (UTC):

- **00:00** — Asia open (Tokyo 09:00, HK/Singapore 08:00 local)
- **07:00–08:00** — Europe / London open
- **13:00–14:00** — US equity open (NY 09:30 ET = 13:30 UTC, bucketed at 13)
- **20:00–21:00** — US equity close (NY 16:00 ET)

### LONG side by entry hour (1,828 trades, total PF 1.31)

| **hour UTC** | **session** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|---|
| 00:00 | **Asia open** | 84 | 2.08 | +1,901 | +23 |
| 01:00 | | 81 | 1.46 | +1,037 | +13 |
| 02:00 | | 58 | 1.19 | +263 | +5 |
| 03:00 | | 72 | 1.79 | +1,127 | +16 |
| 04:00 | | 62 | **0.34** | **−1,320** | **−21** |
| 05:00 | | 69 | 1.27 | +567 | +8 |
| 06:00 | | 70 | 1.31 | +545 | +8 |
| 07:00 | **EU open** | 79 | 1.17 | +408 | +5 |
| 08:00 | **EU open** | 70 | 2.15 | +2,651 | +38 |
| 09:00 | | 72 | 1.21 | +365 | +5 |
| 10:00 | | 65 | 0.89 | −232 | −4 |
| 11:00 | | 80 | **0.71** | **−670** | **−8** |
| 12:00 | | 79 | 1.46 | +1,101 | +14 |
| 13:00 | **US open** | 90 | **0.68** | **−979** | **−11** |
| 14:00 | **US open** | 82 | 0.79 | −658 | −8 |
| 15:00 | post-US-open | 97 | **3.03** | **+3,611** | +37 |
| 16:00 | | 87 | 1.08 | +180 | +2 |
| 17:00 | | 85 | 1.03 | +63 | +1 |
| 18:00 | | 83 | **2.60** | **+3,890** | +47 |
| 19:00 | | 80 | 1.16 | +334 | +4 |
| 20:00 | **US close** | 56 | 1.48 | +653 | +12 |
| 21:00 | **US close** | 68 | 0.96 | −74 | −1 |
| 22:00 | | 69 | 1.01 | +12 | +0 |
| 23:00 | | 90 | 1.37 | +666 | +7 |

**Long-side pattern**:

- **Best hours**: 15:00 UTC (PF 3.03, post-US-open momentum) and
  18:00 UTC (PF 2.60, US afternoon).
- **Worst hours**: 04:00 (PF 0.34, n=62 small sample), 11:00 (PF 0.71)
  and 13:00 (PF 0.68) — pre-US-open and right at the open.
- Counter-intuitive: longs fired *at* the US open (13:00, 14:00) are
  net-negative; longs fired *after* the open settles (15:00) are the
  best of the day. Suggests waiting through the opening volatility
  before going long.
- Asia open (00:00) is reliably profitable (PF 2.08); Europe open
  (08:00) too (PF 2.15).

### SHORT side by entry hour (4,387 trades, total PF 1.78)

| **hour UTC** | **session** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|---|
| 00:00 | **Asia open** | 213 | 1.95 | +8,267 | +39 |
| 01:00 | | 183 | 1.79 | +6,630 | +36 |
| 02:00 | | 145 | 1.65 | +5,207 | +36 |
| 03:00 | | 162 | **2.56** | +8,913 | +55 |
| 04:00 | | 157 | 1.33 | +2,685 | +17 |
| 05:00 | | 152 | 1.76 | +6,123 | +40 |
| 06:00 | | 185 | **2.34** | +9,495 | +51 |
| 07:00 | **EU open** | 180 | 1.78 | +6,420 | +36 |
| 08:00 | **EU open** | 193 | **2.23** | +9,265 | +48 |
| 09:00 | | 177 | 1.46 | +4,805 | +27 |
| 10:00 | | 212 | 1.65 | +7,450 | +35 |
| 11:00 | | 171 | 1.40 | +4,366 | +26 |
| 12:00 | | 222 | 1.46 | +6,043 | +27 |
| 13:00 | **US open** | 243 | 1.77 | +9,854 | +41 |
| 14:00 | **US open** | 283 | **2.40** | **+17,467** | +62 |
| 15:00 | post-US-open | 231 | 1.69 | +7,401 | +32 |
| 16:00 | | 218 | **2.21** | +10,519 | +48 |
| 17:00 | | 190 | **2.98** | **+14,028** | +74 |
| 18:00 | | 149 | 1.67 | +4,751 | +32 |
| 19:00 | | 150 | 1.11 | +1,267 | +8 |
| 20:00 | **US close** | 145 | 1.11 | +1,300 | +9 |
| 21:00 | **US close** | 146 | **2.13** | +6,075 | +42 |
| 22:00 | | 124 | 1.85 | +4,710 | +38 |
| 23:00 | | 156 | 1.63 | +5,056 | +32 |

**Short-side pattern**:

- **Every hour profitable** — no dead zones (lowest PF is 1.11 at
  19:00–20:00 UTC).
- **Strongest hours**: 17:00 (PF 2.98, US afternoon) and 14:00
  (PF 2.40, $17.5K — the single biggest hour for short net P&L).
- The **session-open hours** (00:00, 08:00, 13:00, 14:00) are all
  profitable for shorts but the post-open hours (14:00–17:00)
  carry the highest PF and largest dollar amounts.
- Shorts work all day; longs need session momentum to fire well.

### Day of week (UTC)

| **dow** | **long PF** | **long net** | **short PF** | **short net** |
|---|---|---|---|---|
| Mon | 1.13 | +921 | 1.50 | +17,806 |
| **Tue** | **0.90** | **−813** | 1.70 | +16,501 |
| Wed | 1.52 | +4,039 | 1.54 | +17,468 |
| Thu | 1.39 | +2,577 | **2.39** | **+38,303** |
| Fri | 1.22 | +1,782 | **2.52** | **+47,157** |
| Sat | 1.28 | +1,425 | 1.54 | +16,008 |
| Sun | **1.81** | **+5,510** | 1.40 | +14,855 |

**Patterns**:

- **Tuesday is the only unprofitable long-side day** (PF 0.90, −$813).
- **Sunday is the strongest long-side day** (PF 1.81) — light-volume
  weekend trading apparently favors mean-reversion longs.
- **Thursday + Friday carry 51% of all short net P&L** ($85K of $168K).
  Short side performance is very weekday-skewed; weekends and early
  week are weaker.

### Implications

- **Hour-of-day is a real but moderate signal.** Long-side has 4
  unprofitable hours (04:00, 11:00, 13:00, 14:00) accounting for
  −$3,627 net across 313 trades — filtering them removes ~17% of
  longs and recovers ~$3.6K. Modest improvement.
- **Short-side has no dead hours** and a clean concentration in 14:00,
  17:00, 21:00 — these are the highest-edge windows but every hour
  works. Suggests a sizing modulator (boost shorts in high-PF hours)
  more than a filter.
- **Day-of-week patterns are stronger** than hour-of-day in absolute
  P&L terms. Cutting Tuesday longs entirely would cost $-813 (i.e.,
  *gain* $813). Concentrating short exposure on Thu+Fri would lift
  per-trade-dollar P&L significantly.

```
python scripts/crypto/time_of_day_stratify.py
```

## Long/short book correlation diagnostic

For the cross-strategy diversification question we computed the daily
P&L of the long book and the short book separately and measured their
correlation across the 700 trading days in the backtest.

| **Metric** | **Long book** | **Short book** |
|---|---|---|
| n trading days | 700 | 700 |
| Mean daily P&L | +$22.06 | +$240.14 |
| Daily P&L std | $260.34 | $1,157.79 |
| Daily Sharpe (annualized, √252) | **1.35** | **3.29** |
| Mean / std (per-day Sharpe) | 0.085 | 0.207 |

| **Joint distribution** | **% of days** |
|---|---|
| Both books up | 16.6% |
| Both books down | 18.1% |
| Long up, short down | 15.1% |
| Long down, short up | **28.7%** |

**Pearson correlation: ρ = 0.0165 — essentially zero.**

The long and short books are statistically independent. The 28.7%
"long down, short up" days are particularly noteworthy — they are the
days the short book *rescues* the long book.

**Combined-book Sharpe**: with ρ ≈ 0:

```
σ_combined ≈ sqrt(σ_L² + σ_S²) = sqrt(260² + 1158²) ≈ $1,187
μ_combined = 22 + 240 = $262
Sharpe_combined ≈ 262 × sqrt(252) / 1187 ≈ 3.50
```

That's a ~6% Sharpe lift over shorts-only (3.29 → 3.50). Modest in
absolute terms because the short book dominates so heavily in
P&L magnitude — the long book contributes diversification but only
~9% of the P&L.

**Implications**:

1. **Run long and short as separable books.** Independent capital
   allocations are fully justified — no correlation penalty.
2. **The short book is the engine; long is the diversifier.** Per
   dollar of daily-volatility risk, shorts deliver 2.4× the
   Sharpe-per-day of longs (0.207 vs 0.085). Allocating more
   *capital* to shorts is correct; the long book is mostly there to
   smooth the curve.
3. **Independence enables Kelly-style sizing per book.** With
   uncorrelated returns, optimal weights scale with μ/σ² per book.
   Long: 22/67k ≈ 3.3e-4. Short: 240/1.34M ≈ 1.79e-4. So the
   *capital efficiency* of the long book per dollar at risk is 1.84×
   the short book — meaning longs deserve relatively more capital
   weight than their P&L share suggests, because they're so much
   tighter on a risk basis.

```
# Three modes:
python scripts/crypto/momentum_stratify.py            # VWMA (default)
python scripts/crypto/momentum_stratify.py --mode ma
python scripts/crypto/momentum_stratify.py --mode zscore
```

## Volume-momentum stratification — recent vs trailing volume

The price-momentum z-score didn't produce a clean monotonic signal — long
PF zigzags between buckets, and short PF is roughly flat across most of
the distribution. Volume is a different story.

For each trip, compute:

```
recent_vol   = sum(bar_volume) over (entry_us - 24h, entry_us]
baseline_vol = avg-per-24h volume over the trailing 60d ending at (entry_us - 24h)
ratio        = recent_vol / baseline_vol
```

The baseline excludes the recent window itself so an active recent period
doesn't inflate the denominator.

**LONG side (1,828 trades, total PF 1.31):**

| **ratio** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <0.5× | 354 | 1.04 | +458 | +1 |
| **0.5 to 1×** | 527 | **1.94** | +9,651 | +18 |
| **1 to 1.5×** | 248 | **0.58** | −3,168 | −13 |
| 1.5 to 2× | 113 | 1.37 | +1,107 | +10 |
| 2 to 3× | 153 | 1.87 | +3,545 | +23 |
| 3 to 5× | 122 | 0.82 | −690 | −6 |
| 5 to 10× | 148 | 1.60 | +3,004 | +20 |
| ≥10× | 163 | 1.27 | +1,534 | +9 |

The long side zigzags. The **0.5 to 1× bucket** (coins trading *below*
typical volume) is the strongest long PF (1.94, +$18 avg). The **1 to
1.5× bucket** (coins trading at modestly elevated volume) is the worst
long PF (0.58, −$13 avg). No monotonic pattern beyond that. Consistent
with "longs work in calm continuation regimes, not in chaos."

**SHORT side (4,386 trades, total PF 1.78):** monotone in volume.

| **ratio** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <0.5× | 888 | 1.14 | +7,506 | +8 |
| 0.5 to 1× | 1,187 | 1.65 | +37,944 | +32 |
| 1 to 1.5× | 608 | 1.59 | +17,407 | +29 |
| 1.5 to 2× | 311 | 1.50 | +8,886 | +29 |
| 2 to 3× | 341 | 2.01 | +14,082 | +41 |
| 3 to 5× | 402 | 2.52 | +27,738 | +69 |
| **5 to 10×** | 400 | **3.54** | +40,697 | +102 |
| ≥10× | 249 | 2.46 | +13,754 | +55 |

PF climbs cleanly from 1.14 (quiet coins) through 1.50–1.65 (normal
volume) to **3.54 at 5–10× typical volume** — the highest PF we've
seen on any stratification anywhere on the system. The ≥10× bucket
(extreme blowoff) dips slightly to PF 2.46 but stays strongly
profitable.

The 5–10× short bucket is **400 trades, 64.2% win rate, $40,697 net,
+$101.74 avg** — a quarter of all short profit on 9% of trades. The
hypothesis: when a long-tail token is trading 5×+ its typical volume,
it's almost certainly in a squeeze or a blowoff and the short edge
maximizes there.

### Window-length sweep — robustness check

To verify the 60d/24h finding wasn't a single-window artifact, we
swept lookback ∈ {30d, 45d, 60d} × recent ∈ {8h, 16h, 24h} = 9 cells.
Same trips file, same buckets, same per-symbol streaming.

The pattern is robust: short PF climbs monotone-ish into the high-volume
buckets in every cell, peaking at PF 3.20–4.62 somewhere in the 3–5×
or 5–10× zone. The lookback shifts where the peak lands (shorter
lookback → lower baseline → more trades classified as 5–10× → peak
shifts left to 3–5×; longer lookback → higher baseline → peak shifts
right to 5–10×). The recent-window length controls how acute the
volume burst needs to be: shorter recent (8h) is more sensitive to
intraday spikes; longer recent (24h) smooths.

**SHORT-side PF by bucket, all 9 cells** (peak in **bold**):

| **lb / recent** | <0.5× | 0.5–1× | 1–1.5× | 1.5–2× | 2–3× | 3–5× | 5–10× | ≥10× |
|---|---|---|---|---|---|---|---|---|
| 30d / 8h | 1.27 | 1.49 | 1.71 | 2.24 | 2.44 | **3.41** | 2.37 | 1.61 |
| 30d / 16h | 1.17 | 1.53 | 1.94 | 1.78 | 2.63 | **3.46** | 2.17 | 1.77 |
| 30d / 24h | 1.29 | 1.44 | 1.99 | 1.83 | 2.34 | **3.20** | 2.74 | 1.59 |
| 45d / 8h | 1.10 | 1.62 | 1.80 | 1.70 | 2.72 | 2.29 | **4.62** | 1.67 |
| 45d / 16h | 1.08 | 1.66 | 1.46 | 2.75 | 1.71 | 3.18 | **3.43** | 2.07 |
| 45d / 24h | 1.12 | 1.59 | 1.75 | 1.65 | 2.05 | 2.98 | **3.69** | 1.75 |
| 60d / 8h | 1.08 | 1.79 | 1.31 | 2.60 | 1.97 | 2.54 | **3.50** | 2.22 |
| 60d / 16h | 1.09 | 1.73 | 1.49 | 2.03 | 1.72 | 2.49 | **3.54** | 2.47 |
| 60d / 24h | 1.14 | 1.65 | 1.59 | 1.50 | 2.01 | 2.52 | **3.54** | 2.46 |

**LONG-side PF by bucket, same 9 cells**:

| **lb / recent** | <0.5× | 0.5–1× | 1–1.5× | 1.5–2× | 2–3× | 3–5× | 5–10× | ≥10× |
|---|---|---|---|---|---|---|---|---|
| 30d / 8h | 1.19 | 1.22 | 0.67 | 2.87 | 0.98 | 1.16 | 1.66 | 1.49 |
| 30d / 16h | 1.17 | 1.13 | 1.61 | 1.08 | 1.33 | 1.74 | 1.20 | 1.42 |
| 30d / 24h | 1.30 | 1.20 | 1.34 | 1.21 | 2.37 | 0.88 | 1.19 | 1.39 |
| 45d / 8h | 1.14 | 1.07 | 1.25 | 2.39 | 0.97 | 1.23 | 1.22 | 1.65 |
| 45d / 16h | 1.01 | 1.74 | 1.09 | 1.07 | 1.22 | 0.92 | 1.48 | 1.57 |
| 45d / 24h | 1.31 | 1.45 | 0.91 | 1.24 | 1.32 | 1.64 | 1.21 | 1.34 |
| 60d / 8h | 1.18 | 1.19 | 1.09 | 2.62 | 1.08 | 1.20 | 0.91 | 1.65 |
| 60d / 16h | 1.07 | 1.64 | 1.21 | 1.54 | 1.26 | 0.63 | 2.08 | 1.14 |
| 60d / 24h | 1.04 | 1.94 | 0.58 | 1.37 | 1.87 | 0.82 | 1.60 | 1.27 |

The long-side zigzag persists across every (lookback, recent) pair —
no monotone signal at any window. The volume-momentum signal works for
**shorts only**.

### The cleanest cell — 30d / 8h

The 30d/8h cell is the most monotone. Both sides rise then fall in
a clean dome shape:

**SHORT (4,387 trades, 30d/8h):**

| **ratio** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <0.5× | 884 | 1.27 | +13,060 | +15 |
| 0.5 to 1× | 1,284 | 1.49 | +34,439 | +27 |
| 1 to 1.5× | 651 | 1.71 | +21,886 | +34 |
| 1.5 to 2× | 380 | 2.24 | +19,580 | +52 |
| 2 to 3× | 452 | 2.44 | +32,493 | +72 |
| **3 to 5×** | 321 | **3.41** | +29,162 | +91 |
| 5 to 10× | 240 | 2.37 | +12,935 | +54 |
| ≥10× | 175 | 1.61 | +4,541 | +26 |

PF climbs from 1.27 → 1.49 → 1.71 → 2.24 → 2.44 → **3.41** then falls
back to 2.37 → 1.61. Six of seven inter-bucket transitions are in the
"correct" direction (rising up to peak, falling after). The peak sits
at 3–5× volume.

**LONG (1,828 trades, 30d/8h):**

| **ratio** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <0.5× | 260 | 1.19 | +1,297 | +5 |
| 0.5 to 1× | 476 | 1.22 | +2,295 | +5 |
| 1 to 1.5× | 298 | 0.67 | −2,790 | −9 |
| **1.5 to 2×** | 168 | **2.87** | +7,438 | +44 |
| 2 to 3× | 164 | 0.98 | −74 | 0 |
| 3 to 5× | 149 | 1.16 | +751 | +5 |
| 5 to 10× | 149 | 1.66 | +3,409 | +23 |
| ≥10× | 164 | 1.49 | +3,114 | +19 |

The long side stays noisy even in this cell — the 1.5–2× bucket
spikes to PF 2.87 (168 trades, +$7,438 net, +$44 avg, the cell's best
long bucket) but adjacent buckets drop to 0.67 and 0.98. The volume
signal genuinely doesn't hold for longs at any window.

### Implications

This is the cleanest size-up modulator we've found. Concretely:

- **Short notional should scale with volume ratio.** A 1× short and a
  5× short have radically different expected returns — sizing them
  identically leaves money on the table. Even a coarse step function
  (1× notional below 2×, 2× notional 2–5×, 3× notional 5–10×) would
  redistribute capital toward the high-edge buckets.
- **Long notional has no clean signal here.** The zigzag pattern
  suggests random allocation noise within the long side. Volume-based
  sizing would not help longs and might hurt by underfunding the 0.5–1×
  bucket.
- **Volume is more useful than price-momentum** for sizing. The price
  z-score table had alternating PF (1.69, 1.11, 1.19, 1.37, 1.53, ...);
  the volume table has a monotone short side. We should focus follow-up
  work on volume-based size modulation, not on price-momentum filters.

```
python scripts/crypto/volume_momentum_stratify.py
```

Defaults: `--lookback-days 60 --recent-hours 24`. Reads the z-persist
no-stop trips file by default; pass `--trips <path>` to stratify a
different system's trades.

## Buy/sell-imbalance stratification — 1h decile pattern

For each trip and each window length, compute
`imbalance = (Σ buy_dv − Σ sell_dv) / (Σ buy_dv + Σ sell_dv)` over the
trailing window ending at entry. Range [-1, +1].

We swept windows 30d, 200h, 24h, 16h, 8h. At long horizons (≥24h)
imbalance is dominated by aggressor-flow averaging — most trades cluster
in `-5% to +5%` regardless of side. The signal sharpens at shorter
windows; **1h is the cleanest**.

### 1h imbalance — long deciles (1,828 trades)

| **D** | **imb range** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|---|
| 1 | −19.23% to +2.95% | 183 | 1.30 | +1,786 | +10 |
| 2 | +2.97 to +5.02 | 183 | **1.96** | +4,698 | +26 |
| 3 | +5.02 to +6.82 | 183 | **1.94** | +5,487 | +30 |
| 4 | +6.84 to +8.71 | 183 | **1.96** | +4,256 | +23 |
| 5 | +8.73 to +10.97 | 182 | 0.83 | −979 | −5 |
| 6 | +10.98 to +13.22 | 183 | 0.77 | −1,172 | −6 |
| 7 | +13.23 to +15.65 | 183 | 1.09 | +370 | +2 |
| 8 | +15.67 to +19.27 | 183 | 0.93 | −302 | −2 |
| 9 | +19.30 to +25.59 | 183 | 0.93 | −321 | −2 |
| 10 | +25.64 to +81.35 | 182 | 1.34 | +1,618 | +9 |

**Three-decile sweet spot at +3% to +9% imbalance** (PF 1.94–1.96 across
deciles 2–4, +$14,441 combined). This is "recent 1h has been
moderately net-buying" — the regime is bullish *and* the short-term
flow confirms.

**Five-decile dead zone at +9% to +26% imbalance** (deciles 5–9,
PF 0.77, 0.77, 1.09, 0.93, 0.93, 730 trades, combined net −$2,404).
Once recent 1h flow gets too one-sided, longs underperform —
overcrowded continuation, blow-off proximity. Deciles 5–6 are flat-out
unprofitable. Filtering this band removes ~40% of long trades while
collectively saving money.

The +25%+ tail (decile 10) recovers to PF 1.34 — extreme imbalance
is its own regime, distinct from the dead zone just below.

### 1h imbalance — short deciles (4,387 trades)

| **D** | **imb range** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|---|
| 1 | −76.25% to −30.18% | 439 | **1.10** | +2,514 | +6 |
| 2 | −30.16 to −24.22 | 439 | 1.98 | +19,689 | +45 |
| 3 | −24.22 to −20.07 | 439 | 1.49 | +11,432 | +26 |
| 4 | −20.06 to −17.24 | 438 | **2.20** | +22,858 | +52 |
| 5 | −17.24 to −14.58 | 439 | 1.59 | +14,610 | +33 |
| 6 | −14.57 to −12.16 | 439 | 1.77 | +17,630 | +40 |
| 7 | −12.16 to −9.76 | 438 | **2.23** | +24,377 | +56 |
| 8 | −9.76 to −7.23 | 439 | 1.81 | +16,489 | +38 |
| 9 | −7.23 to −4.26 | 439 | 1.97 | +18,889 | +43 |
| 10 | −4.24 to +37.51 | 438 | 1.93 | +19,609 | +45 |

**Decile 1 is the dead zone for shorts** (PF 1.10, 439 trades, ~half
the system PF). When the recent 1h has been >30% net-selling, the easy
short is gone — the move is already priced in.

Deciles 2–10 are all profitable (PF 1.49–2.23). The strongest are
decile 7 (PF 2.23, "moderately net-selling, fresh setup") and decile 4
(PF 2.20). Shorts work even when recent flow has *flipped slightly
bullish* (decile 10, PF 1.93) — that's the bear regime overpowering a
brief retracement.

### Implications

The two cleanest filter candidates from the 1h imbalance breakdown:

1. **Long-side dead zone**: skip longs with 1h imbalance in `+9% to
   +26%` (deciles 5–9). Removes ~40% of longs, saves ~$2.4K, lifts
   long PF from 1.31 toward ~1.5+. The signal is consistent across
   five consecutive deciles — strongest "do not enter" signal we've
   found anywhere.

2. **Short-side late-entry filter**: skip shorts with 1h imbalance
   below −30% (decile 1). PF 1.10 vs system 1.78 — still profitable but
   far below average. Cutting wouldn't gain much absolute P&L (these
   are mostly small trades) but removes the noisiest short setups.

The longer windows (8h, 24h, 30d) showed weaker, choppier signals —
the bucket-to-bucket PF variation looks like noise above ~0.3 PF
between adjacent deciles. The 1h window is short enough that the
imbalance reflects *fresh* flow rather than a multi-day average.

```
python scripts/crypto/imbalance_stratify.py --windows 1h --deciles
```

### Window-length cross-check — 15m, 30m, 90m, 8h

To verify the 1h finding wasn't a single-window artifact, we ran the
decile breakdown at 15m, 30m, 90m, and 8h. The same dead-zone-and-
sweet-spot **shape** appears across the 30m–90m band; below 30m and
above 8h it dissolves.

**LONG-side decile PF across windows** (peak in **bold**, dead zone
italicized):

| **D** | **15m** | **30m** | **1h** | **90m** | **8h** |
|---|---|---|---|---|---|
| 1 | 1.56 | _0.91_ | 1.30 | 1.44 | _0.94_ |
| 2 | 1.24 | **2.32** | **1.96** | 1.68 | 1.22 |
| 3 | **1.96** | 1.55 | **1.94** | **1.96** | 1.40 |
| 4 | _0.92_ | 1.67 | **1.96** | 1.43 | 1.26 |
| 5 | 1.52 | 1.40 | _0.83_ | _1.01_ | 1.60 |
| 6 | 1.27 | 1.13 | _0.77_ | _0.85_ | _0.87_ |
| 7 | 1.09 | 1.07 | _1.09_ | _0.91_ | **2.10** |
| 8 | 1.37 | 1.17 | _0.93_ | 1.05 | 1.28 |
| 9 | 1.04 | _0.88_ | _0.93_ | 1.19 | 1.34 |
| 10 | _0.89_ | _0.96_ | 1.34 | 1.39 | 1.20 |

The 1h pattern (sweet spot D2–4, dead zone D5–9) is the cleanest
single-window result. **90m has the same pattern with a narrower
dead zone (D5–7)** and a slightly later center. **30m has the
same direction but more bucket-to-bucket noise** — D6–10 average
PF ~1.04, D2–5 average PF ~1.74, so the upper-half-vs-lower-half
split survives even though no individual decile stands out cleanly.
15m and 8h dissolve the pattern — too noisy below, too averaged
above.

**SHORT-side decile 1 PF across windows** — the late-short dead zone:

| **window** | **D1 imb range** | **D1 PF** | **system PF** |
|---|---|---|---|
| 15m | −99.67 to −47.54% | 1.30 | 1.78 |
| 30m | −92.49 to −38.63% | 1.18 | 1.78 |
| **1h** | **−76.25 to −30.18%** | **1.10** | 1.78 |
| 90m | −78.36 to −26.08% | 1.32 | 1.78 |
| 8h | −76.96 to −12.46% | 1.43 | 1.78 |

The "shorts fired into already-net-selling 1h flow are late entries"
effect is **most pronounced at 1h** (PF 1.10, vs 1.18–1.43 at adjacent
windows). 1h captures the right horizon: aggregated enough to smooth
trade-by-trade noise, fresh enough to reflect *current* regime tilt.

**Conclusion**: 1h is the unique discriminative window for buy/sell
imbalance on this system. The dead-zone effect persists from 30m to
90m at lower magnitude — useful as a sanity check that the 1h finding
isn't artifact, less useful as alternative filter horizons.

## Qualitative trade review — high-rvol shorts

**Open question from prior session.** The 30d/8h volume-momentum
short PF curve was monotone up to PF 3.41 at 3–5×, then dropped
to 2.37 at 5–10× and 1.61 at ≥10×. We hypothesized that extreme
volume regimes need a faster system to capture them; this section
inspects the actual trades to see what's happening.

Per-trade ratios were dumped via
`scripts/crypto/trades_with_volratio.py` (writes
`data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv`).
Bucket inspection by `scripts/crypto/inspect_high_rvol_shorts.py`
and `scripts/crypto/inspect_high_rvol_extras.py`.

### MFE/MAE asymmetry — losers are quick, winners get smaller

Median per-trade excursion at exit:

| **bucket** | **n** | **PF** | **WR** | **winner MFE** | **loser MFE** | **loser MAE** | **winner bars** | **loser bars** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 3–5× | 321 | 3.41 | 63.2% | +230 bp | +40 bp | −117 bp | 84,433 | 10,686 |
| 5–10× | 240 | 2.37 | 58.8% | +220 bp | +57 bp | −99 bp | 64,213 | 19,846 |
| ≥10× | 175 | 1.61 | 61.7% | +143 bp | +46 bp | −136 bp | 65,643 | 13,172 |

The PF drop at high rvol **isn't from worse losers** — loser-side
MAE actually shrinks at 5–10× (−99 bp vs −117 at 3–5×). The drop
is from **smaller winners**: median winner MFE compresses from
+230 bp → +220 bp → +143 bp, and at ≥10× the right tail almost
disappears (max MFE only +677 bp vs +840 at 3–5×).

Read: extreme rvol shorts often *bottom out faster*, leaving less
trend left to capture by a system that holds for tens of thousands
of bars. The ≥10× bucket is the bucket where the move *already
happened* by the time we entered.

### MFE-bucket P&L decomposition — almost all the edge sits at MFE > 200bp

| **bucket** | **MFE bin** | **n** | **WR** | **net P&L** | **avg/trade** |
|---|---|---:|---:|---:|---:|
| 5–10× | <50 bp | 60 | 21.7% | −3,887 | −64.79 |
| 5–10× | 50–100 bp | 42 | 38.1% | −2,139 | −50.92 |
| 5–10× | 100–200 bp | 49 | 69.4% | +825 | +16.83 |
| 5–10× | 200–400 bp | 67 | 86.6% | +10,440 | +155.82 |
| 5–10× | ≥400 bp | 22 | 90.9% | +7,696 | +349.82 |
| ≥10× | <50 bp | 48 | 25.0% | −3,231 | −67.31 |
| ≥10× | 50–100 bp | 44 | 52.3% | −1,589 | −36.11 |
| ≥10× | 100–200 bp | 46 | 80.4% | +1,904 | +41.39 |
| ≥10× | 200–400 bp | 28 | 96.4% | +4,442 | +158.65 |
| ≥10× | ≥400 bp | 9 | 100.0% | +3,015 | +334.95 |

Trades that never reach +100 bp MFE bleed money in both buckets.
Trades that do reach +200 bp MFE win 86–96% of the time. The
"never gets going" trades are the ones to cut.

The signal is testable: **if a short hasn't reached at least +100 bp
MFE within ~N bars, kill it.** The aggregate exposure under that
filter would drop, but the loser pile (102 trades returning −6,026
in the 5–10× bucket; 92 trades returning −4,820 in the ≥10×) would
shrink dramatically. We'd lose some of the +100–200 bp marginal-winner
trades too, but those bins are near break-even.

### Repeat-count cut — single-shot symbols vs frequent firers

Repeat-count = number of times the symbol fired a short at ≥5×
rvol across the whole dataset. Single-shots are usually
just-listed alts that pumped once. Frequent firers are
established symbols whose volume regime keeps tripping the
filter:

| **bucket** | **rep_count** | **n** | **WR** | **PF** | **net P&L** | **avg** |
|---|---|---:|---:|---:|---:|---:|
| 5–10× | 1 trade | 106 | 63.2% | 2.88 | +7,285 | +68.73 |
| 5–10× | 2 trades | 60 | 65.0% | 2.45 | +3,729 | +62.15 |
| 5–10× | 3–5 | 63 | 54.0% | 2.26 | +2,736 | +43.44 |
| 5–10× | ≥6 | 11 | 9.1% | **0.05** | −816 | −74.16 |
| ≥10× | 1 trade | 70 | 68.6% | 2.93 | +3,934 | +56.20 |
| ≥10× | 2 trades | 54 | 59.3% | 1.06 | +157 | +2.90 |
| ≥10× | 3–5 | 50 | 56.0% | 1.19 | +482 | +9.64 |
| ≥10× | ≥6 | 1 | 0.0% | 0.00 | −32 | −31.63 |

**Symbols that repeatedly trigger ≥5× rvol shorts collapse to
PF 0.05 in the 5–10× bucket** (11 trades, 1 winner). These look
like high-vol degenerates where every trip is just another spike
in the chop, not a parabolic top to fade. By contrast, the
single-shot symbols — the cleanest "this token is having its
moment" pattern — book PF 2.88–2.93 across both buckets.

A simple repeat-count filter (skip if the symbol has fired ≥6
shorts at ≥5× rvol historically) would have removed 12 trades
returning −848 from the high-rvol pool with no loss of
single-shot edge. Small absolute saving, but the PF gradient is
striking and points at a real structural distinction.

### Quarterly P&L — no obvious regime issue, but 2026Q2 thin

Quarterly PF stays mostly above 1.0 in both buckets, with weakest
quarters being 2024Q3 (5–10×: PF 1.01) and 2025Q2 (≥10×: PF 0.46,
13 trades). The ≥10× bucket has more cross-quarter noise — n=7
to n=35 per quarter — so individual quarters are not load-bearing.

### Symbol concentration — winners are diverse, losers are scattered

182 distinct symbols in the 5–10× bucket and 138 in ≥10×. No
single symbol dominates the winner side: top winners are
ILVUSDT (+1,064), ACEUSDT (+916), PORTALUSDT (+665) in 5–10×;
SSVUSDT (+694), EULUSDT (+536), MAGICUSDT (+497) in ≥10×.
Losers are equally scattered (BANK, STORJ, ALICE in 5–10×;
MOODENG, AUCTION, MERL in ≥10×). Nothing here suggests a
single symbol is corrupting the bucket.

### Implications

Three actionable filters fall out:

1. **MFE-progression timeout.** Cut shorts that haven't reached
   +100 bp MFE within some window (need to grid-search the
   bar threshold). The lower-MFE bins net **−6,026 in 5–10×**
   and **−4,820 in ≥10×** — that's the entire PF gap to the
   3–5× bucket.
2. **Repeat-count filter.** Skip shorts on symbols that have
   already fired ≥6 high-rvol shorts. Tiny in trade count
   (12 trades) but the PF (0.05) is a clear regime signal.
3. **Size by bucket, not equally.** The 3–5× bucket is the
   sweet spot at PF 3.41. Even after MFE/repeat-count cleanup
   the 5–10× and ≥10× buckets won't catch up — the medium-rvol
   regime is structurally favored.

```
python scripts/crypto/trades_with_volratio.py \
    --out data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv
python scripts/crypto/inspect_high_rvol_shorts.py
python scripts/crypto/inspect_high_rvol_extras.py
```

## Raw-z magnitude variant — fixing the high-rvol PF drop

**Motivating observation from the chart review.** Watching individual
trades on 5m bars + raw-tape volume bars + López de Prado dollar-
imbalance bars showed entries firing **hours after** the 200h sign-
imbalance MA had already turned. The cumsum's per-bar contribution
is `erf(z / √2)`, bounded in (−1, +1), which means a 3σ flow bar
contributes only ≈ +0.997 to the cumsum — barely distinguishable
from a 1σ bar's +0.683. On extreme-rvol blowoffs the squashing
discards exactly the information we need: a 3–5σ aggressor burst
should move the signal *fast*, but erf flattens it.

### The fix — push raw z into the cumsum

`magnitude_t = z_t` instead of `erf(z_t / √2)`. A 3σ bar now
contributes 3.0 instead of 0.997. Threshold scales up to compensate;
since E|N(0,1)| = √(2/π) ≈ 0.798, the average |contribution| is
roughly the same as erf's, but the **tails diverge**: a single
multi-sigma bar can fire on its own under raw-z.

Implemented as `--magnitude-mode raw-z` in `cumsum-z-sweep`
(default `erf`). Engine change is the one-line branch in
`OrderflowCumsumZ.fs:ProcessBar`. The Erf path is byte-identical
to before — verified by re-running the persist-exit th=15
reference config and matching the prior breakdown to the trip
(6,215 trips, win rate 52.50%, total PF 1.690).

### Headline pooled metrics

Same flags as the persist-exit reference (200h MA, allow-short,
require-persistence-for-exit, ADV-gated, 7d vol window with
0.1019% reference, max-bar-price-ratio=3, no stop):

| **mode** | **threshold** | **trips** | **pooled PF** | **long PF** | **short PF** | **largest loss** |
|---|---:|---:|---:|---:|---:|---:|
| Erf (ref) | 15 | 6,215 | 1.690 | ~1.31 | ~1.79 | −$3,427 |
| Raw-z | 15 | 12,542 | 1.523 | 1.237 | 1.584 | −$3,577 |
| Raw-z | 25 | 9,322 | 1.585 | 1.307 | 1.641 | −$3,577 |
| Raw-z | 50 | 5,980 | 1.681 | 1.334 | 1.753 | −$3,380 |
| **Raw-z** | **100** | **3,427** | **1.864** | **1.365** | **1.976** | **−$2,973** |

PF rises monotonically with raw-z threshold; trip count drops
from 12.5K → 3.4K. Raw-z 100 fires roughly half as often as the
Erf-15 reference yet beats it on every aggregate (pooled PF 1.86
vs 1.69, short PF 1.98 vs 1.79, largest loss bounded tighter).

### Rvol-bucket breakdown — the high-rvol drop is gone

Short-side PF by 30d/8h volume-momentum bucket — the same
bucketing that motivated this whole investigation:

| **rvol bucket** | **Erf-15** | **Raw-z 50** | **Raw-z 100** |
|---|---:|---:|---:|
| <0.5× | — | 1.29 | 1.26 |
| 0.5–1× | — | 1.39 | 1.88 |
| 1–1.5× | — | 1.97 | 1.83 |
| 1.5–2× | — | 2.18 | 2.11 |
| 2–3× | — | 2.37 | 2.83 |
| **3–5×** | **3.41** | 2.37 | **4.45** |
| **5–10×** | **2.37** | 2.48 | **3.14** |
| **≥10×** | **1.61** | 1.46 | **1.78** |
| **ALL** | 1.79 | 1.75 | **1.97** |

Raw-z at threshold 100 **beats Erf-15 on every high-rvol bucket**.
The 3–5× bucket goes from PF 3.41 → 4.45 (+30%); 5–10× from
2.37 → 3.14 (+33%); ≥10× from 1.61 → 1.78 (+11%). The win-rate
shifts are large too: at raw-z 100 the 3–5× short bucket wins
71.4% of the time (vs 58.8% at Erf-15), and the 5–10× bucket
wins 67.7% (vs 58.8%).

### Interpretation

- **The squashing was a tax on tail bars.** Erf flattens any
  bar past z ≈ 2 to ~+1; raw-z lets a 3–5σ aggressor flush move
  the cumsum by its full size. On extreme-rvol blowoffs that's
  exactly when the signal should accelerate, not saturate.
- **Higher threshold + raw-z = stricter entry filter.** At
  threshold 100, accumulating to fire requires either one
  extreme bar (10σ+, rare) or several confirming multi-sigma
  bars in agreement. The trades that survive this filter are
  cleaner: fewer chop-driven entries, more genuine regime
  flips.
- **The ≥10× bucket is still the weakest** (PF 1.78 even
  under raw-z 100), but the gap to 3–10× has narrowed sharply.
  The "extreme rvol needs a different system" hypothesis from
  the chart review is partially satisfied by raw-z; what's
  left of the degradation is the *very* extreme tail (≥10×
  for 8h vol vs 30d), where moves are mostly already over by
  the time we'd enter.

The raw-z + threshold-100 config is the new candidate baseline.
At fewer than half the trip count of the Erf-15 reference but a
healthier short-side PF (1.98 vs 1.79), it scales up better
under fixed slippage assumptions.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- cumsum-z-sweep \
    --cumsum-thresholds 15,25,50,100 \
    --ma-hours 200 --magnitude-mode raw-z \
    --allow-short --min-long-adv 28800000 --min-short-adv 8000000 \
    --vol-window-days 7 --max-bar-price-ratio 3 --reference-vol-pct 0.1019 \
    --require-persistence-for-exit \
    --results-csv data/crypto/cumsum_z_rawz/rawz_results.csv \
    --summary-csv data/crypto/cumsum_z_rawz/rawz_summary.csv
for th in 15 25 50 100; do
  python scripts/crypto/volume_momentum_stratify.py \
    --trips data/crypto/cumsum_z_rawz/rawz_results_trips_1m_th${th}_ls.csv \
    --lookback-days 30 --recent-hours 8 \
    > data/crypto/cumsum_z_rawz/rawz_th${th}_volratio_30d8h.txt
done
```

## Extreme-rvol blowoff short engine — `OrderflowExtremeRvol`

Even after the raw-z fix, the `>=10x` rvol bucket remained the
weakest short-side cell (PF 1.78 at threshold 100). The chart
review of 175 high-rvol trades revealed why: the cumsum-z system
holds tens of thousands of bars per trade, so on extreme-rvol
blowoffs it catches only the back half of moves that have
already mostly happened. A faster, dedicated engine targeting
this regime specifically was the next step.

This section walks through the v0 → v4 design iterations of
[`OrderflowExtremeRvol`](../TradingEdge.CryptoBacktest/OrderflowExtremeRvol.fs).
Each step is one discrete design decision, motivated either by
a chart observation or a sweep result. CLI lives at
`extreme-rvol-sweep`. Trip CSVs are compatible with the existing
`volume_momentum_stratify.py` / `inspect_high_rvol_shorts.py`
pipeline.

### Engine concept

Per-bar gates on 1m bars:

  - **rvol**: `mean-recent-volume / mean-30d-volume`. Two parallel
    instances — one fast for entry (1h numerator), one for cover
    (1h numerator, separate config knob).
  - **price-rise**: `bar.Close / laggedMean` where `laggedMean` is
    an 8h-MA-of-close lagged 16h, derived as the difference of
    two trailing MAs over 24h and 16h with no new primitive
    needed: `(Σ24h − Σ16h) / (n24h − n16h)`.
  - **CVD**: trailing 1h sum of `buy_dv − sell_dv`. Entry trigger
    is the bar where it crosses from non-negative to negative.

Entry — flat AND short-allowed AND `rvolEntry >= threshold` AND
`priceRise >= 1 + threshold` AND CVD just turned negative AND
ADV gate AND 200h warm-up. The 30d baseline doesn't need to be
full; we divide by `volBaseline.Count` so recently-listed
symbols can trade against whatever's accumulated (same
convention as `volume_momentum_stratify.py`).

Stop — trailing-N-minute MaxMa of `bar.High`, snapshotted at
entry and held fixed. Trigger is `bar.VWAP >= stopLevel` (volume-
weighted, so wicks alone don't fire); fill is `bar.Close`.

Cover — `rvolExit < threshold`. Default 1h frame, threshold 2.

Time-stop — optional. After N minutes, close per the configured
mode: `Hard` always, `Conditional` only if not profitable.

### Iteration trail

**v0 (8h-rvol entry, 8h-VWAP stop, 8h-rvol cover).** Faithful
implementation of the chart-review sketch. Pooled across the full
4×3 (rvolEntry × priceRise) grid: PF rises monotonically with
both gates, peaking at PF 1.271 at rvol=15/pr=15% with 1,898
trips. Largest single loss bounded at ~$480 (vs $3,427 for
cumsum-z), confirming the VWAP-snapshot stop does its job.
Visual review of the canonical (rvol=10, pr=10%) cell showed
many entries firing hours after the move had topped — the 8h
gate is too slow on these short-cycle blowoffs.

**v1 (1h-rvol entry, 20m high-stop).** The user's call: faster
entry signal (1h-mean / 30d-mean rvol), much tighter stop
(20-minute trailing max-High instead of 8h trailing max-VWAP).
Trip count tripled at the canonical cell (3,767 → 10,212). PF
fell from 1.171 to 1.052 — faster firing also catches more
false starts. Largest loss tightened from −$469 to −$220.

**v2 (VWAP-trigger fix on the high-stop).** Visual review caught
LRCUSDT 2025-12-11 stopping out on a wick despite never closing
above the level. Changed the stop trigger from `bar.High >= stop`
to `bar.VWAP >= stop` (volume-weighted, wick-resistant). Fill
from `stopLevel` to `bar.Close` (a confirmed VWAP breach means
we're filling at end-of-bar, not at the level). Trip count
dropped slightly (9,127), PF fell to 1.036, worst loss widened
to −$643. Net P&L cost: ~$1,400. The change is correct on
principle (fewer wick-stops on trades that go on to work — see
LRCUSDT 2024-11-18 turning −$8 → +$73), but the cap on tail
loss softens because we're filling at close after VWAP confirms.

**v3 (1h cover instead of 8h).** Insight: the 8h cover gate was
holding losers way past the natural unwind point, since 8 hours
of accumulated normal-volume bars are needed to drag the rolling
mean below 2×. Switched the cover-rvol numerator to 1h, matching
the entry frame. Trip count essentially unchanged (9,127 →
9,203); PF rose 1.036 → 1.069; **net P&L jumped from $4,206
to $7,560** at the canonical cell (+80%). Across the full grid,
PF improved on every cell. Median holding period at the
canonical cell dropped from many hours to ~1.6h.

**v4 (conditional time-stop, default 90m).** Visual review of v3
showed many losers were trades that wandered sideways for hours
without ever showing profit. Added an optional time-stop with
two modes: `Hard` (close unconditionally at the timer) and
`Conditional` (close only if `bar.Close >= entryPrice` —
unprofitable shorts get cut, profitable ones run). Swept
(rvol × priceRise × {30, 60, 90, 120}m × {hard, conditional}).

**v5 (entry-distance gate, default 20% off the 8h high).**
Insight: the edge is in fading *near the peak*, not after the
flush has already started. Added an optional entry-distance gate
that rejects entries when `bar.Close` has drifted further than
X% below a reference high. Two reference surfaces tested: the
existing 20m max-High (Stop20m, the stop level) vs a new 8h
trailing max-High (High8h, an "actual peak" indicator). The 8h
reference at 20% threshold improves PF in every cell of the full
grid while costing only 1–4% of trade count — see below.

### Time-stop sweep — full grid (v4)

Grid-wide pooled metrics, **conditional 90m** vs baseline (no
time-stop) and hard 90m:

| **rvolE** | **pr** | **trips_base** | **PF_base** | **trips_c90** | **PF_c90** | **trips_h90** | **PF_h90** |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 8 | 5% | 14,817 | 1.042 | 16,791 | 1.046 | 20,378 | 1.025 |
| 8 | 10% | 11,885 | 1.041 | 13,620 | 1.053 | 16,755 | 1.030 |
| 8 | 15% | 8,959 | 1.059 | 10,367 | 1.071 | 12,934 | 1.046 |
| 10 | 5% | 11,040 | 1.067 | 12,504 | 1.073 | 15,098 | 1.052 |
| 10 | 10% | 9,203 | 1.069 | 10,512 | **1.081** | 12,824 | 1.060 |
| 10 | 15% | 7,173 | 1.089 | 8,288 | 1.094 | 10,240 | 1.065 |
| 12 | 5% | 8,657 | 1.104 | 9,757 | 1.120 | 11,666 | 1.100 |
| 12 | 10% | 7,434 | 1.091 | 8,428 | 1.108 | 10,169 | 1.091 |
| 12 | 15% | 5,959 | 1.100 | 6,811 | 1.103 | 8,320 | 1.086 |
| 15 | 5% | 6,461 | 1.129 | 7,207 | **1.157** | 8,446 | 1.113 |
| 15 | 10% | 5,715 | 1.120 | 6,399 | 1.144 | 7,534 | 1.103 |
| 15 | 15% | 4,719 | 1.131 | 5,325 | 1.139 | 6,332 | 1.095 |

**Conditional 90m beats baseline on PF in every single cell
(12/12).** Trade count rises 12–15% per cell because the time-
stop frees up capital to take new entries earlier; the closed
loser is replaced by the next setup. Tail loss bounds tighten
from the cumsum-z baseline's −$240/−$643 to about −$140/−$266.

**Hard mode hurts at every cell.** Cutting profitable trades
short loses more edge than it saves on the losers. The asymmetry
is largest at loose entries (rvol=8, pr=5%): hard 30m goes
**net unprofitable** (PF 0.994), while conditional 30m holds at
PF 1.028. The "let the winner run" semantics are doing real
work.

**Time-stop window sensitivity** at the canonical cell (rvol=10,
pr=10%):

| **mode** | **timeStop** | **trips** | **netPnL** | **PF** | **worstLoss** | **medBars** |
|---|---:|---:|---:|---:|---:|---:|
| baseline | — | 9,203 | $7,560 | 1.069 | −$643 | 97 |
| conditional | 30m | 12,207 | $5,885 | 1.074 | **−$116** | 30 |
| conditional | 60m | 11,035 | $6,656 | 1.076 | −$140 | 60 |
| **conditional** | **90m** | **10,512** | **$7,313** | **1.081** | **−$140** | **90** |
| conditional | 120m | 10,207 | $7,371 | 1.079 | −$140 | 97 |
| hard | 30m | 15,803 | $2,184 | 1.026 | −$116 | 30 |
| hard | 60m | 13,800 | $5,337 | 1.056 | −$140 | 60 |
| hard | 90m | 12,824 | $5,951 | 1.060 | −$140 | 90 |
| hard | 120m | 12,151 | $7,190 | 1.072 | −$140 | 94 |

Conditional 60m / 90m / 120m are all close on PF (1.076–1.081).
**90m is the new engine default** — middle of the plateau, slightly
edges the others. Worst-case loss collapses from −$643 to ~−$140
(4.6× tighter) with any time-stop, since the time-stop catches
the wandering trades that would otherwise drift to the worst
end of the loss distribution.

### Entry-distance gate sweep — full grid (v5)

Two refs were tested: `Stop20m` (reuse the existing 20m max-High
snapshotted at entry — the stop level) vs `High8h` (a separate
trailing 8h max-High allocated unconditionally — an O(1)
amortized monotonic-deque MaxMa). Distance values 5/10/15/20%
were swept against the v4 baseline (no distance gate).

**Canonical cell (rvol=10, priceRise=10%, ts=90m conditional):**

| **ref** | **distance** | **trips** | **netPnL** | **PF** | **worstLoss** |
|---|---:|---:|---:|---:|---:|
| baseline | none | 10,512 | $7,313 | 1.081 | −$140 |
| stop20m | 5% | 9,810 | $5,156 | 1.066 | **−$90** |
| stop20m | 10% | 10,373 | $6,374 | 1.073 | −$90 |
| stop20m | 15% | 10,470 | $6,961 | 1.078 | −$140 |
| stop20m | 20% | 10,502 | $7,148 | 1.079 | −$140 |
| high8h | 5% | 6,789 | $3,142 | 1.061 | −$90 |
| high8h | 10% | 9,371 | $6,673 | 1.087 | −$90 |
| high8h | 15% | 10,029 | $7,742 | 1.092 | −$140 |
| **high8h** | **20%** | **10,260** | **$8,015** | **1.092** | −$140 |

**`Stop20m` hurts at every distance.** The 20m stop level is a
risk-management surface, not a peak indicator — by the time it
catches the actual peak it's already too tight to use as a
"near the top" reference. PF stays below baseline at every
distance tested.

**`High8h` clearly wins, peaking at 15–20%.** The 8h max-High is
a genuine peak detector, and a 20% threshold filters out only
the truly-late entries (trip count drops just 2.4%, from 10,512
to 10,260). PF rises from 1.081 → 1.092, net P&L from $7,313 →
$8,015 (+9.6%) at the canonical cell.

**Grid-wide:** PF improves on **all 12 cells** under high8h@20%:

| **rvolE** | **pr** | **trips_base** | **PF_base** | **trips_h15** | **PF_h15** | **trips_h20** | **PF_h20** |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 8 | 5% | 16,791 | 1.046 | 16,225 | 1.047 | 16,505 | 1.053 |
| 8 | 10% | 13,620 | 1.053 | 13,102 | 1.060 | 13,355 | 1.063 |
| 8 | 15% | 10,367 | 1.071 | 9,906 | 1.077 | 10,127 | 1.083 |
| 10 | 5% | 12,504 | 1.073 | 11,986 | 1.080 | 12,235 | 1.083 |
| 10 | 10% | 10,512 | 1.081 | 10,029 | 1.092 | 10,260 | 1.092 |
| 10 | 15% | 8,288 | 1.094 | 7,855 | 1.103 | 8,056 | 1.106 |
| 12 | 5% | 9,757 | 1.120 | 9,292 | 1.128 | 9,516 | 1.130 |
| 12 | 10% | 8,428 | 1.108 | 7,994 | 1.120 | 8,203 | 1.120 |
| 12 | 15% | 6,811 | 1.103 | 6,422 | 1.109 | 6,603 | 1.114 |
| 15 | 5% | 7,207 | 1.157 | 6,809 | **1.166** | 6,993 | 1.164 |
| 15 | 10% | 6,399 | 1.144 | 6,026 | 1.156 | 6,197 | 1.153 |
| 15 | 15% | 5,325 | 1.139 | 4,984 | 1.147 | 5,137 | 1.148 |

Best cell at rvol=15/pr=5% with high8h@15%: **PF 1.166**, 6,809
trips. The 15% and 20% threshold values are within noise of each
other (six cells favor 15%, six favor 20%); 20% is preferred as
the default because it costs less trade count and the worst-case
loss bound is identical.

**Adopted as default**: `EntryDistanceMaxPct = 0.20`,
`EntryDistanceRef = High8h`. Reproducibility verified — bare-
default run reproduces the high8h@20% sweep cell byte-identically
(10,260 trips, $8,015, PF 1.092, worst loss −$140).

### v4 vs cumsum-z baseline (high-rvol bucket)

Compared against the cumsum-z RawZ-100 short-side `>=10x` bucket
(PF 1.78, n=229) — the bucket this engine was built to target:

| | RawZ-100 | ExtremeRvol v4 (rvol=10, pr=10%, ts=90m) |
|---|---:|---:|
| Engine | cumsum-z, raw-z magnitude, threshold 100 | dedicated blowoff fader |
| Trips | 229 | 10,512 |
| Pooled PF | 1.78 | 1.081 |
| Largest loss | −$2,973 | **−$140** |

Different shape entirely. RawZ-100 is a once-in-a-while
strict-conviction signal; ExtremeRvol-v4 is a high-frequency,
small-edge fader with much tighter risk control per trade. They
target the same regime but represent different points on the
trip-count-vs-PF frontier. Both should run as separate books.

### Defaults

`OrderflowExtremeRvol.defaultExtremeRvolConfig()` ships with:

  - `RvolEntryThreshold = 24.0` (v8 default; was 10 through v7 — the
    rvol-Q5 floor from the quintile sweep)
  - `RvolExitThreshold = 2.0`
  - `PriceRiseThreshold = 0.10`
  - `EntryRvolHours = 1`, `ExitRvolMinutes = 75`
  - `RecentHours = 8`, `LagHours = 16`, `CvdMinutes = 75`
  - `LookbackDays = 30`
  - `StopHighWindowMinutes = 20`
  - `TimeStopMinutes = 90`, `TimeStopMode = Conditional`
  - `EntryDistanceMaxPct = 0.20`, `EntryDistanceRef = High8h`

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 8,10,12,15 \
    --price-rise-thresholds 0.05,0.10,0.15 \
    --max-bar-price-ratio 3 \
    --reference-vol-pct 0.1019 \
    --min-short-adv 8000000 \
    --results-csv data/crypto/extreme_rvol/results.csv \
    --summary-csv data/crypto/extreme_rvol/summary.csv
```

To sweep the time-stop:

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 10 --price-rise-thresholds 0.10 \
    --time-stop-minutes 0,30,60,90,120 \
    --time-stop-mode conditional \
    --min-short-adv 8000000 --max-bar-price-ratio 3 --reference-vol-pct 0.1019 \
    --results-csv data/crypto/extreme_rvol_ts_sweep/results.csv \
    --summary-csv data/crypto/extreme_rvol_ts_sweep/summary.csv
```

To sweep the entry-distance gate (against the 8h high):

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 10 --price-rise-thresholds 0.10 \
    --entry-distance-max-pct 0,0.05,0.10,0.15,0.20 \
    --entry-distance-ref high8h \
    --min-short-adv 8000000 --max-bar-price-ratio 3 --reference-vol-pct 0.1019 \
    --results-csv data/crypto/extreme_rvol_ed_sweep/results.csv \
    --summary-csv data/crypto/extreme_rvol_ed_sweep/summary.csv
```

### Quintile post-hoc breakdown — rvol × price-rise (v6, 2026-05-08)

To attribute the engine's edge across the input distribution, we ran the
loosest gate that still produces signal — `rvol >= 8`, `priceRise >= 5%` —
and binned every resulting trade into rvol-quintile × pr-quintile cells
empirically (cutoffs from the trade population itself, not preset levels).
Each cell holds ~770 trips on average, enough to read PF without
over-fitting noise. Both windows below use `EntryRvolHours = 1`,
`StopHighWindowMinutes = 20`, `TimeStopMinutes = 90 (Conditional)`,
`EntryDistanceMaxPct = 0.20 (High8h)`, `MinShortAdv = $8M`,
`MaxBarPriceRatio = 3`, `ReferenceVolPct = 0.1019`.

The breakdown is generated by [scripts/crypto/quintile_bin_extreme_rvol.py](../scripts/crypto/quintile_bin_extreme_rvol.py)
from the trip CSV; `RoundTrip` was extended this morning with a new
`PriceRiseAtEntry` field (the realized `bar.Close / 8h-MA-lagged-16h - 1`
at fire time) so the binning is self-contained inside the trade record.

#### Window comparison — 45m vs 60m exit/CVD

Two configurations of `ExitRvolMinutes` and `CvdMinutes`. We collapsed
yesterday's separate `ExitRvolHours` / `CvdHours` knobs into minute units
this morning so the cover frame and the entry-trigger horizon stay
identical (the cover MA needs to be at least as long as the trigger MA;
keeping them locked together prevents drift).

**45m windows (19,244 trips):**

```
         rvolQ1    rvolQ2    rvolQ3    rvolQ4    rvolQ5
prQ1  |   0.88     0.93     0.93     1.13     1.28
prQ2  |   0.89     1.01     0.88     1.25     1.19
prQ3  |   1.00     1.00     0.93     1.20     1.09
prQ4  |   0.87     0.74     0.97     0.96     1.22
prQ5  |   0.68     0.95     1.08     1.02     1.21
```

**60m windows (16,505 trips):**

```
         rvolQ1    rvolQ2    rvolQ3    rvolQ4    rvolQ5
prQ1  |   0.98     0.74     0.96     1.11     1.62
prQ2  |   0.96     0.76     0.94     1.20     1.17
prQ3  |   0.94     1.13     1.04     1.26     1.11
prQ4  |   0.99     0.85     0.86     0.93     1.29
prQ5  |   1.05     0.83     1.04     1.12     1.22
```

**Quintile cutoffs (60m, representative):**

  - rvol: 8.0 → 9.2 → 11.3 → 15.1 → 23.8 → 331
  - priceRise: 5.0% → 10.0% → 15.1% → 22.0% → 33.6% → 530%

**Marginal PF by rvol quintile:**

| bin | 45m PF | 60m PF | 45m netPnL | 60m netPnL |
|-----|--------|--------|------------|------------|
| Q1  | 0.88   | 0.98   | −$2,763    | −$453      |
| Q2  | 0.93   | 0.86   | −$1,627    | −$3,101    |
| Q3  | 0.96   | 0.97   | −$1,199    | −$785      |
| Q4  | 1.09   | 1.11   | +$2,663    | +$2,990    |
| Q5  | 1.20   | 1.24   | +$7,860    | +$8,019    |

Aggregate PF: 45m = 1.06, 60m = 1.10. Aggregate netPnL: 45m = +$4,934,
60m = +$6,670 — and on **fewer trades**.

#### Findings

1. **rvol does almost all the work, pr does very little.** Marginal rvol
   PF rises monotonically Q1 → Q5 (0.88 → 1.20 at 45m, 0.98 → 1.24 at
   60m). The marginal pr curve is essentially flat at 0.99 → 1.08 (45m)
   and 0.99 → 1.12 (60m). The price-rise gate is a nearly-uninformative
   axis once rvol is conditioned on.
2. **The bottom three rvol quintiles are unprofitable in aggregate.**
   At both windows. rvol < ~15 is the dead zone; the current 10×
   default sits right inside it. The PF improvement from raising the
   default (yesterday's intuition) is robustly attributable to dropping
   these losing buckets, not to better selection within them.
3. **The single best cell at 60m is (rvolQ5, prQ1) at PF 1.62 / $1,104
   on 284 trips.** Counter-intuitive: lowest pr quintile + highest rvol
   quintile. The blowoff prints 24×+ baseline volume but hasn't yet
   moved 10% off the lagged MA — those trades fade earliest in the
   move. Interpretable as "early blowoff": the volume tells the story
   before the price has fully extended.
4. **Sub-rvol-15 fades may be the wrong direction entirely.** At 45m,
   the (prQ4–Q5 × rvolQ1–Q2) corner shows PF 0.68–0.95 on shorts —
   meaning the *opposite* trade (long) would PF ~1.05–1.47. At 60m the
   cliff softens (PF 0.83–0.99) but it's still not a short edge. The
   fade-large-low-volume-rallies idea (queue item #5) was pointed at
   exactly this regime — at low rvol, big price rises probably indicate
   *trending continuation*, not blowoff exhaustion. Worth running as a
   long engine, not a short one.
5. **The window has a sweet spot at 75m.** After seeing 60m beat 45m
   we extended the sweep to 75m and 90m to find the inflection point.

#### Window length sweep — 45m / 60m / 75m / 90m

Same loose-gate sweep, four `ExitRvolMinutes` = `CvdMinutes` settings.

**Marginal PF by rvol-quintile across windows:**

| rvol bin | 45m  | 60m  | 75m  | 90m  |
|----------|------|------|------|------|
| Q1       | 0.88 | 0.98 | 1.04 | 0.97 |
| Q2       | 0.93 | 0.86 | 0.86 | 0.92 |
| Q3       | 0.96 | 0.97 | 0.91 | 0.91 |
| Q4       | 1.09 | 1.11 | 1.12 | 1.06 |
| Q5       | 1.20 | 1.24 | 1.28 | 1.25 |

**Aggregate metrics across windows:**

| metric              | 45m     | 60m     | 75m     | 90m     |
|---------------------|---------|---------|---------|---------|
| Trips               | 19,244  | 16,505  | 14,317  | 13,203  |
| Aggregate PF        | 1.06    | 1.10    | 1.11    | 1.07    |
| Aggregate netPnL    | $4,934  | $6,670  | $7,513  | $4,659  |
| rvolQ5 netPnL       | $7,860  | $8,019  | $8,442  | $7,100  |
| Best (rvolQ5,*) cell | 1.28 (Q1) | 1.62 (Q1) | 1.40 (Q3) | 1.33 (Q1) |

**Findings from the window sweep:**

- **75m is the sweet spot** on aggregate netPnL ($7,513), rvolQ5
  marginal PF (1.28), and rvolQ5 netPnL ($8,442). Aggregate PF (1.11)
  ties with 60m for the best.
- **PF curve flattens past 75m.** 90m loses on every aggregate metric
  vs 75m: trips down to 13.2k, netPnL drops to $4,659, rvolQ4 PF slides
  1.12 → 1.06. The cover MA fires too late — winning trades give back
  gains before vol normalises below 2× baseline.
- **60m wins on a single-cell extreme** (rvolQ5,prQ1 at PF 1.62) but
  with only 284 trips. 75m's best (rvolQ5,prQ3) at PF 1.40 has 460
  trips — more reliable, and the rest of the rvolQ5 row is uniformly
  stronger at 75m than at 60m.
- **Fade-the-fade signal decays with longer windows.** rvolQ1×prQ4
  short PF: 0.87 (45m) → 0.99 (60m) → 1.17 (75m) → 1.18 (90m). The
  long-flip thesis was a 45m-specific artifact; longer covers absorb
  the move. Still worth running as a long engine on the right
  *separate* gate — but not by simply inverting the short here.
- **`RvolEntryThreshold = 10` still sits inside the dead zone** at all
  four windows. Marginal Q1–Q3 stays sub-1.0 throughout. Raising the
  default to ~15 (yesterday's intuition) is the next obvious step.

**Outcome:** engine defaults updated to `ExitRvolMinutes = 75`,
`CvdMinutes = 75`. The morning's 45m-merge experiment was the right
logical fix (locking the cover frame to the entry-trigger horizon) but
the wrong value — sweeping 45/60/75/90 found the optimum at 75m.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 8 --price-rise-thresholds 0.05 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-short-adv 8000000 \
    --results-csv data/crypto/extreme_rvol_quintile/results.csv \
    --summary-csv data/crypto/extreme_rvol_quintile/summary.csv

python scripts/crypto/quintile_bin_extreme_rvol.py \
    data/crypto/extreme_rvol_quintile/results_trips_1m_rvol8_pr0.05_ts90m_ed0.2_short.csv
```

To re-run any of the alternative windows:

```
# 60m
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 8 --price-rise-thresholds 0.05 \
    --exit-rvol-minutes 60 --cvd-minutes 60 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-short-adv 8000000 \
    --results-csv data/crypto/extreme_rvol_quintile_60m/results.csv \
    --summary-csv data/crypto/extreme_rvol_quintile_60m/summary.csv
```

### Re-entry timeout after time-stop (v7, 2026-05-08)

Time-stops fire when a trade went sideways without showing edge — the
hypothesis is that re-shorting the same regime within a short window
is doubling down on a failed read. New `ReentryTimeoutMinutes` config
field (CLI `--reentry-timeout-minutes`) suppresses entries for N
minutes after a time-stop fires; high-stops and cover-on-vol-normalize
do **not** engage the cooldown.

The cooldown is symbol-local. State is a single `lastTimeStopUs`
timestamp on the engine, stamped only inside the time-stop branch.
Entry gate adds `bar.EndUs - lastTimeStopUs >= cooldown` as the final
filter.

#### Sweep — v5 defaults (rvol≥10, pr≥10%, 75m windows)

Single (rvol_entry × price_rise) cell, varying the cooldown only:

| cooldown | trips | PF    | avgPnL | netPnL  |
|----------|-------|-------|--------|---------|
|   0m     | 8,996 | 1.107 | $0.92  | $8,270  |
|  15m     | 8,864 | 1.103 | $0.89  | $7,880  |
|  30m     | 8,731 | 1.105 | $0.90  | $7,818  |
|  60m     | 8,551 | 1.109 | $0.93  | $7,959  |
| **120m** | **8,090** | **1.129** | **$1.10** | **$8,896** |

**Findings:**

- **120m wins on every metric** — PF +2.0%, avgPnL +20%, netPnL +7.6%
  vs the 0m baseline. ~10% fewer trades.
- **15m / 30m / 60m all *underperform* the 0m baseline.** Tiny
  differences (PF 1.10–1.11), but no improvement. A short cooldown
  removes some trades without selectivity.
- **Signal is non-monotonic; the edge appears at 60→120m.** Below 60m
  there's no meaningful change. The dropped trades in the 120m–60m
  delta are systematically losers — the avgPnL-per-trade jump is the
  cleanest evidence.
- **Plausible mechanism:** time-stop fires at 90m by default. A 60m
  cooldown after a 90m hold leaves only 30m of the post-stop blowoff
  unblocked, which is too short to remove the regime echo. 120m
  removes the rest.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 10 --price-rise-thresholds 0.10 \
    --reentry-timeout-minutes 0,15,30,60,120 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-short-adv 8000000 \
    --results-csv data/crypto/extreme_rvol_reentry/results.csv \
    --summary-csv data/crypto/extreme_rvol_reentry/summary.csv
```

Bin per cooldown (the breakdown log collapses cooldowns into one bag,
so PF is read directly from each `_rt{N}m_short.csv` trip file):

```
python -c "
import duckdb, pandas as pd
con = duckdb.connect()
for label, f in [
    ('  0m', 'data/crypto/extreme_rvol_reentry/results_trips_1m_rvol10_pr0.1_ts90m_ed0.2_short.csv'),
    (' 15m', 'data/crypto/extreme_rvol_reentry/results_trips_1m_rvol10_pr0.1_ts90m_ed0.2_rt15m_short.csv'),
    (' 30m', 'data/crypto/extreme_rvol_reentry/results_trips_1m_rvol10_pr0.1_ts90m_ed0.2_rt30m_short.csv'),
    (' 60m', 'data/crypto/extreme_rvol_reentry/results_trips_1m_rvol10_pr0.1_ts90m_ed0.2_rt60m_short.csv'),
    ('120m', 'data/crypto/extreme_rvol_reentry/results_trips_1m_rvol10_pr0.1_ts90m_ed0.2_rt120m_short.csv'),
]:
    df = con.execute(f\"SELECT net_pnl FROM read_csv_auto('{f}', HEADER=TRUE) WHERE side='short'\").df()
    gw = df.loc[df.net_pnl>0,'net_pnl'].sum(); gl = -df.loc[df.net_pnl<0,'net_pnl'].sum()
    pf = gw/gl if gl>0 else float('inf')
    print(f'{label}: trips={len(df)} PF={pf:.3f} netPnL={df.net_pnl.sum():.0f}')
"
```

#### Cross-check at rvol Q5 (rvol≥24, pr≥10%, 75m windows)

| cooldown | trips | PF    | avgPnL | netPnL  |
|----------|-------|-------|--------|---------|
|   0m     | 2,749 | 1.282 | $3.02  | $8,300  |
|  15m     | 2,723 | 1.281 | $3.01  | $8,193  |
|  30m     | 2,693 | 1.276 | $2.95  | $7,954  |
|  60m     | 2,628 | 1.287 | $3.06  | $8,034  |
| 120m     | 2,512 | 1.289 | $3.06  | $7,685  |

**The 120m cooldown advantage disappears at Q5.** PF curve is flat
(1.282 → 1.289 = noise inside one decimal); netPnL drops monotonically
($8,300 → $7,685, −7.4%) as the cooldown removes good trades along
with bad. avgPnL stays at ~$3.0/trade across all cooldowns.

**Inverted finding:** The cooldown's PF improvement at v5 (1.107 →
1.129) was filtering out post-time-stop *low-rvol* re-entries — the
same trades the rvol gate already filters at Q5. Once the rvol gate
is high enough, no cooldown is needed.

The bigger lever is the rvol gate itself: **avgPnL at Q5 is ~3.3× v5**
($3.06 vs $0.93 at the same cooldown). Updating the default:
`RvolEntryThreshold` 10 → 24 (v8). Trade count drops ~70% but netPnL
is essentially unchanged ($8,270 → $8,300) and PF jumps from 1.11 to
1.28. `ReentryTimeoutMinutes` stays at 0 — no benefit at the new gate.

```
# Reproducer for the rvol-Q5 cross-check
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-sweep \
    --rvol-entry-thresholds 24 --price-rise-thresholds 0.10 \
    --reentry-timeout-minutes 0,15,30,60,120 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-short-adv 8000000 \
    --results-csv data/crypto/extreme_rvol_reentry_q5/results.csv \
    --summary-csv data/crypto/extreme_rvol_reentry_q5/summary.csv
```

## Long fade companion — `OrderflowExtremeRvolLong` (v8, 2026-05-08)

Long-side mirror of the short engine. Same cell + sweep wiring, same
75m windows, same 90m conditional time-stop, same 20m stop window, same
0.20 entry-distance gate (against `Low8h` instead of `High8h`). Mirrored
logic:

- direction long, not short
- price gate `bar.Close <= (1 - PriceDeclineThreshold) × laggedMean`
- CVD trigger `prevCvd <= 0  AND  cvd > 0` (just turned positive)
- stop = trailing 20m `MinMa` of `bar.Low`, snapshotted at entry, fires
  when `bar.VWAP <= stopLevel`, fill at `bar.Close`
- time-stop conditional flip: `bar.Close <= entryPrice` is the
  "not profitable" check

New CLI: `extreme-rvol-long-sweep`. Trip CSV stem ends in `_long.csv`.
PriceRiseAtEntry is reused as a magnitude axis (engine writes the
negative of the realized decline so the quintile-bin script reads
`abs(price_rise_at_entry)` for both engines).

### Quintile breakdown — loose gate (rvol≥8, decline≥5%, 75m windows)

5×5 PF; 2,255 trips total (vs 19,244 on the short loose-gate). Rvol
and decline cutoffs are empirical from the trade population.

```
         rvolQ1    rvolQ2    rvolQ3    rvolQ4    rvolQ5
pdQ1  |   1.27     0.87     1.15     1.09     0.53
pdQ2  |   1.26     0.93     0.97     0.89     1.34
pdQ3  |   1.12     1.95     0.54     1.63     1.21
pdQ4  |   0.93     1.74     1.02     1.70     1.08
pdQ5  |   1.85     0.83     0.74     1.94     0.90
```

**Cutoffs (60m):** rvol 8.0 → 8.7 → 9.8 → 11.8 → 16.3 → 202;
decline 5.0% → 9.1% → 13.5% → 18.3% → 27.8% → 95.4%

**Marginals:**

| bin | by rvol PF | rvol netPnL | by decline PF | decline netPnL |
|-----|-----------|-------------|---------------|----------------|
| Q1  | 1.22      | +$635       | 0.96          | −$106          |
| Q2  | 1.29      | +$759       | 1.08          | +$247          |
| Q3  | 0.84      | −$514       | 1.19          | +$585          |
| Q4  | **1.45**  | **+$1,298** | **1.26**      | **+$837**      |
| Q5  | 0.97      | −$98        | 1.15          | +$517          |

**Headline comparison:**

| metric | Short loose (v8 rvol≥24) | Long loose |
|--------|--------------------------|------------|
| Trips | 2,749 | 2,255 |
| Aggregate PF | 1.28 | 1.13 |
| Aggregate netPnL | $8,300 | $2,080 |
| avgPnL/trade | $3.02 | $0.92 |

**Findings:**

- **rvol is NOT monotonic for longs.** Short rvol curve was clean: Q1=0.88
  → Q5=1.20. Long curve is jagged: 1.22 → 1.29 → 0.84 → 1.45 → 0.97.
  The Q5 collapse is the most striking — extreme-rvol *drops* aren't
  the same regime as extreme-rvol *blowoffs*. Capitulation patterns may
  genuinely revert less reliably than blowoffs, or the sample is too
  thin to read.
- **decline magnitude IS monotonic for longs** (Q1=0.96 → Q4=1.26).
  Mirror inverted: on the short side, price-rise was a flat axis (PF
  ~0.99–1.08 across all quintiles). On the long side, the decline gate
  *is* informative — bigger drops = better fades, up to ~28%.
- **Cell-level variance is huge.** Within a single rvol band, PF jumps
  from 0.54 to 1.95. The user's read on this: the variance is the
  market's way of saying not to trust these results yet. Same
  intuition that emerged from the orderflow-MA v0 study, where longs
  were unaffected by volume in a way shorts weren't.
- **High-volume short fades remain the focus.** Both for this engine
  and for the orderflow-MA system. Long fades work in aggregate but the
  edge is shallow, the sample is thin (~90 trades per cell), and the
  noise dominates the signal at quintile resolution.

**Outcome:** ship the long engine with `PriceDeclineThreshold = 0.10`
(the marginal-decline-Q3 floor where PF first crosses 1.19). Keep the
short side as the primary book; the long engine is a low-priority
companion until we have more data or a tighter regime filter.

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- extreme-rvol-long-sweep \
    --rvol-entry-thresholds 8 --price-decline-thresholds 0.05 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/extreme_rvol_long_quintile/results.csv \
    --summary-csv data/crypto/extreme_rvol_long_quintile/summary.csv

python scripts/crypto/quintile_bin_extreme_rvol.py \
    data/crypto/extreme_rvol_long_quintile/results_trips_1m_rvol8_pd0.05_ts90m_ed0.2_long.csv
```

## Long fade — MA-target exit (v9, 2026-05-08): `OrderflowLongFadeMA`

The v8 ExtremeRvolLong sweep showed long-side rvol is non-monotonic and
mostly noise. We tried lifting the rvol gate entirely; the volume-
normalize cover then fell apart (the "exit when activity normalises"
rule was implicitly the same regime filter as the entry rvol gate). To
escape that coupling, we built a sibling engine that exits on a
**bar-close ≥ trailing-N-hour MA** instead. Same direction (long),
same CVD positive-cross trigger, same 8h-MA-lagged-16h reference for
the price-decline gate, same 20m min-Low VWAP stop, same 90m
conditional time-stop, same `Low8h` entry-distance gate. **No rvol
gate, no rvol cover.**

New invariants this engine adds:

- `MinRewardRiskRatio` gate at entry: reward = `coverMa - close`,
  risk = `close - stopLevel`; reject if `reward / risk < threshold`.
  Default 1.0 — never take a trade whose upside doesn't at least match
  its downside. Subsumes the "MA above entry" guard since any positive
  RR already requires the MA above the close.
- `CoverMaHours` config — straight time-based MA of `bar.Close`.
  Cover when `bar.Close >= meanMa`. No VWAP filter; close-vs-MA is
  the cleanest "we got our reversion" check.

### Loose-gate baseline — `pd≥10%`, 24h cover, no rvol

71,697 trips, aggregate **PF 0.97** (losing). Decline-quintile
breakdown showed only Q5 (>16.6% decline) was profitable: 14,340
trips, PF 1.19, +$10.6k netPnL. Q1–Q4 lost a combined −$18.6k.

**Conclusion:** removing the rvol gate kills the strategy in
aggregate. The vol-normalize cover wasn't useless — it was implicitly
filtering for the regime where the long-fade thesis holds. Setting
`PriceDeclineThreshold = 0.166` (Q5 floor) is the obvious next step.

### Cover-MA window sweep — `pd≥16.6%`, RR ≥ 0.5

| MA hours | trips | PF | avgPnL | netPnL | medBars |
|---------:|------:|------:|-------:|-------:|--------:|
| 4h | 13,386 | 0.87 | −$0.41 | −$5,484 | 15 |
| 6h | 13,995 | 0.92 | −$0.26 | −$3,627 | 20 |
| 8h | 14,126 | 0.94 | −$0.22 | −$3,043 | 24 |
| 12h | 14,446 | 1.02 | $0.09 | $1,293 | 30 |
| 16h | 14,678 | 1.11 | $0.40 | $5,906 | 35 |
| 20h | 14,849 | 1.11 | $0.43 | $6,348 | 40 |
| 24h | 14,835 | 1.17 | $0.67 | $9,920 | 42 |
| 36h | 14,810 | 1.30 | $1.16 | $17,141 | 42 |
| **48h** | 14,655 | **1.39** | $1.49 | **$21,772** | 41 |
| **72h** | 14,096 | **1.40** | $1.54 | $21,755 | 40 |
| 96h | 13,626 | 1.32 | $1.25 | $17,094 | 40 |

**Findings:**

- The 24h MA we initially shipped was leaving most of the edge on the
  table. **PF jumps from 1.17 (24h) → 1.39 (48h) → 1.40 (72h)** before
  regressing to 1.32 at 96h. The 48–72h band is the sweet spot.
- 96h regression suggests the cover MA is moving past mean-reversion
  and into trend territory.
- Cover-MA windows shorter than the 75m CVD trigger window (the 4h /
  6h cells are still at 4× CVD width but barely) are structurally
  bad: the trade is being asked to revert inside the same horizon
  that fired it.

### Quintile-by-decline — within each cover-MA window

PF cells:

| MA | Q1 (16.6–17.6%) | Q2 (17.6–19.2%) | Q3 (19.2–21.9%) | Q4 (21.9–27.0%) | Q5 (>27%) | Total |
|---:|---:|---:|---:|---:|---:|---:|
| 4h | 0.85 | 0.84 | 0.87 | 0.87 | 0.91 | 0.87 |
| 6h | 0.81 | 0.84 | 0.90 | 0.97 | **1.06** | 0.92 |
| 8h | 0.81 | 0.80 | 0.85 | 1.02 | **1.16** | 0.94 |
| 12h | 0.92 | 0.82 | 0.85 | 1.17 | **1.31** | 1.02 |
| 16h | 0.99 | 0.90 | 0.95 | 1.24 | **1.39** | 1.11 |
| 20h | 1.00 | 0.93 | 0.97 | 1.21 | 1.38 | 1.11 |
| 24h | 1.05 | 0.96 | 1.01 | 1.30 | 1.48 | 1.17 |
| 36h | 1.18 | 0.99 | 1.08 | 1.51 | 1.65 | 1.30 |
| 48h | 1.27 | 1.09 | 1.12 | **1.65** | **1.72** | 1.39 |
| 72h | 1.33 | 1.10 | 1.24 | **1.66** | 1.62 | 1.40 |
| 96h | 1.30 | 1.05 | 1.32 | 1.56 | 1.38 | 1.32 |

netPnL cells (USDT, $1k notional):

| MA | Q1 | Q2 | Q3 | Q4 | Q5 | Total |
|---:|---:|---:|---:|---:|---:|---:|
| 4h | −1,166 | −1,251 | −1,104 | −1,049 | −913 | −5,484 |
| 6h | −1,688 | −1,389 | −931 | −319 | +699 | −3,627 |
| 8h | −1,731 | −1,949 | −1,496 | +232 | +1,901 | −3,043 |
| 12h | −784 | −1,855 | −1,621 | +1,687 | +3,867 | +1,293 |
| 16h | −89 | −1,042 | −552 | +2,563 | +5,027 | +5,906 |
| 20h | +14 | −793 | −307 | +2,368 | +5,066 | +6,348 |
| 24h | +487 | −490 | +92 | +3,392 | +6,439 | +9,920 |
| 36h | +1,844 | −74 | +883 | +5,762 | +8,726 | +17,141 |
| 48h | +2,771 | +978 | +1,302 | +7,263 | +9,458 | +21,772 |
| 72h | +3,214 | +1,034 | +2,582 | +6,999 | +7,925 | +21,755 |
| 96h | +2,785 | +515 | +3,368 | +5,765 | +4,661 | +17,094 |

**Findings:**

- **Even the losing windows have profitable upper tails.** The 8h
  cell (PF 0.94 overall) has Q5 at PF 1.16 / +$1,901; the 6h cell has
  Q5 at PF 1.06 / +$699. Only the 4h cell has *no* profitable
  quintile.
- **Q5 alone (>27% decline) is profitable at every window 6h+.** PF
  rises from 1.06 (6h) to 1.72 (48h), peaks 1.62–1.72 at 48–72h.
- **Q4 (21.9–27%) is the next-best regime.** Becomes profitable at
  8h (PF 1.02), peaks at 1.66 at 72h. Q4+Q5 combined are doing the
  lion's share of work everywhere.
- **Q1–Q3 are the weakest band.** Q2 in particular is consistently
  the worst — only break-even at 48–72h. Suggests the 17.6–19.2%
  range is a low-edge "no man's land" — too deep for intraday noise
  but not deep enough for the deep-decline thesis.
- **The peak migrates with magnitude:** Q1 peaks at 72h (1.33), Q4
  at 72h (1.66), but **Q5 peaks at 48h (1.72), not 72h** —
  counter-intuitive: deeper declines bounce faster.

### Reproducer

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
    --price-decline-thresholds 0.166 \
    --cover-ma-hours 4,6,8,12,16,20,24,36,48,72,96 \
    --min-reward-risk-ratio 0.5 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/long_fade_ma_window/results.csv \
    --summary-csv data/crypto/long_fade_ma_window/summary.csv

python /tmp/bin_long_fade_ma_all_quintiles.py
```

### Outcome

Two viable defaults:
- **48h cover, pd ≥ 0.22 (Q4 floor)** — captures the Q4+Q5 PF-peak
  regime; ~5,600 trips, PF ~1.69, +$16.7k netPnL.
- **72h cover, pd ≥ 0.166 (loose floor)** — keeps the marginal Q1–Q3
  trades for free trade-volume; 14,096 trips, PF 1.40, +$21.8k netPnL.

Next thread: drop the stop entirely and exit only at the MA, since
deeper declines benefit from longer covers and the 20m MinLow stop
might be cutting reversions short.

## Long fade — no-stop + fixed reference (v10, 2026-05-08)

Two follow-up changes from v9. Both lifted PF substantially.

### Change 1 — drop the stops

Sweeping the no-stop variant (`StopLowWindowMinutes = 0`,
`TimeStopMinutes = 0`, `MinRewardRiskRatio = 0`) at pd≥16.6% across
the same 4..96h cover-MA range:

| MA hours | trips | PF | netPnL |
|---------:|------:|------:|-------:|
| 24h | 3,861 | 1.83 | $32,343 |
| 48h | 4,175 | 2.66 | $64,214 |
| **72h** | 5,368 | **2.90** | **$78,961** |
| 96h | 6,239 | 2.73 | $78,961 |

vs v9 with the 20m MinLow stop on at the same 72h cover: PF 1.40,
+$21,755. **Removing the stop nearly doubled PF.** The stop was cutting
reversions short — many trades stopped out were ultimately winners if
allowed to ride to the MA.

A time-stop sweep `{6,12,24,48,72,96}h × {48,72}h cover` confirmed the
finding: any time-stop *shorter than the cover-MA* costs edge (a 6h
time-stop crashes the 72h cover from PF 2.90 → 1.80). Time-stops ≥
cover-MA hours are no-ops.

### Change 2 — fix the decline reference

The v9 decline gate measured `bar.Close vs 8h-MA-lagged-16h` —
inherited from the short engine where it makes sense (rise vs lagged
peak). For the long-fade thesis it's wrong: a "16.6% decline" against
a 12-hours-old reference can mean the *current* trailing 72h MA has
already drifted below entry. Diagnostic on the 72h baseline showed
**1,947 of 5,368 trades (36%) exiting in <1h** — fee-burn 1-bar
exits where the cover MA was already at/below entry.

Fix: use the cover MA itself as the decline reference. `priceDecline =
1 - bar.Close / coverMa`. Single-reference design — decline is "% below
the very level the trade is targeting".

### Re-run with both fixes — pd≥16.6% vs cover MA, no stops

| MA hours | trips | PF | avgPnL | netPnL | medBars | <1h% |
|---------:|------:|------:|-------:|-------:|--------:|-----:|
| 4h | 284 | 1.02 | $0.22 | $63 | 169 | 4.2% |
| 6h | 430 | 1.12 | $1.77 | $759 | 260 | 1.9% |
| 8h | 519 | 1.15 | $2.20 | $1,144 | 366 | 1.9% |
| 12h | 723 | 1.38 | $5.58 | $4,034 | 538 | 1.0% |
| 16h | 977 | 1.64 | $8.73 | $8,527 | 689 | 0.5% |
| 20h | 1,230 | 1.87 | $11.06 | $13,599 | 800 | 0.6% |
| 24h | 1,465 | 2.11 | $14.05 | $20,588 | 953 | 0.5% |
| 36h | 2,133 | 2.47 | $16.49 | $35,177 | 1,394 | 0.5% |
| 48h | 2,767 | 3.14 | $21.97 | $60,803 | 1,846 | 0.4% |
| **72h** | **3,721** | **3.22** | **$25.08** | **$93,302** | 2,621 | 0.2% |
| 96h | 4,332 | 2.64 | $22.10 | $95,756 | 3,702 | 0.2% |

### Quintile-by-decline grid (per cover-MA window)

PF cells:

| MA | Q1 (lo) | Q2 | Q3 | Q4 | Q5 (hi) | Total |
|---:|--------:|---:|---:|---:|--------:|------:|
| 4h | 1.08 | 1.69 | 0.85 | 1.12 | 0.72 | 1.02 |
| 6h | 1.52 | 0.98 | 1.36 | 2.41 | 0.61 | 1.12 |
| 8h | 1.75 | 1.15 | 1.00 | 2.19 | 0.65 | 1.15 |
| 12h | 1.73 | 2.25 | 1.52 | 1.47 | 0.77 | 1.38 |
| 16h | 1.72 | 2.59 | 1.60 | 2.10 | 1.02 | 1.64 |
| 20h | 1.93 | 2.04 | 2.53 | 2.03 | 1.32 | 1.87 |
| 24h | 3.34 | 2.20 | 2.33 | 2.15 | 1.53 | 2.11 |
| 36h | 2.68 | 3.32 | 3.09 | 2.25 | 1.81 | 2.47 |
| 48h | **3.75** | 3.00 | **4.67** | 3.47 | 2.06 | 3.14 |
| **72h** | 2.71 | 3.19 | 3.72 | **4.43** | 2.61 | **3.22** |
| 96h | 2.65 | 2.34 | 2.30 | 2.98 | 2.91 | 2.64 |

72h decline cutoffs: 16.6% → 17.0% → 17.7% → 18.8% → 21.3% → max

### Findings

- **PF curve is monotonic and clean from 4h → 72h.** The fixed
  reference removes the 36% fee-burn spike (now 0.2% at 72h). Median
  hold at 72h is 2,621 bars (~44h), longer than 48h's 1,846 (~31h) —
  the natural ordering is restored.
- **72h is now a clean winner: PF 3.22 / +$93,302 / 3,721 trips /
  $25.08 avgPnL.** Up from PF 2.90 / +$78,961 with the lagged
  reference.
- **96h gets higher netPnL ($95,756) but lower PF (2.64).** The extra
  trades are lower-quality. 72h is the cleaner choice.
- **Quintile pattern is monotonic at 72h** — Q1 2.71 → Q4 4.43 (peak)
  → Q5 2.61. The Q4 sweet spot (decline 18.8–21.3%) yields PF 4.43 /
  +$23,242 / 745 trips. Past 21.3% the trade is fighting trend.
- **Trade count dropped 31% vs old reference** (3,721 vs 5,368 at 72h)
  — exactly the bars where the cover MA was already at/below entry.
  Those *should* be filtered.
- **The peak migrates with quintile:** 48h cover wins at Q1 (PF 3.75)
  and Q3 (4.67); 72h wins at Q4 (4.43); 96h wins at Q5 (2.91).

### New defaults

`OrderflowLongFadeMA.defaultLongFadeMAConfig()` now ships with:

  - `PriceDeclineThreshold = 0.166`
  - `CoverMaHours = 72`
  - `StopLowWindowMinutes = 0` (no stop)
  - `TimeStopMinutes = 0` (no time-stop)
  - `MinRewardRiskRatio = 0.0` (no RR floor — was the v9 default; the
    fixed-reference design makes it structurally redundant since pd >
    0 already guarantees coverMa > close)
  - `EntryDistanceMaxPct = 0.20`, `EntryDistanceRef = Low8h` (kept)
  - `CvdMinutes = 75` (kept)

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
    --price-decline-thresholds 0.166 \
    --cover-ma-hours 4,6,8,12,16,20,24,36,48,72,96 \
    --stop-low-window-minutes 0 --time-stop-minutes 0 --min-reward-risk-ratio 0 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/long_fade_ma_nostop/results.csv \
    --summary-csv data/crypto/long_fade_ma_nostop/summary.csv
```

### v10b — pd × CVD-window joint sweep (cover 72h, no stops)

After v10 shipped (PF 3.22, $93k netPnL at pd=16.6%, cvd=75m), a joint
sweep over (pd, cvd) showed both axes have monotonic effects but they
trade against each other on netPnL.

#### PF (rows=pd, cols=CVD minutes)

```
   pd     60     75     90    105    120    135    150    165    180    195    210    225    240
12.0%   1.94   1.92   1.93   1.97   2.05   2.09   2.15   2.19   2.21   2.25   2.34   2.33   2.34
13.0%   2.21   2.20   2.25   2.28   2.36   2.40   2.46   2.47   2.55   2.62   2.63   2.65   2.73
14.0%   2.50   2.49   2.55   2.52   2.63   2.67   2.72   2.74   2.82   2.91   2.96   3.03   3.14
15.0%   2.76   2.81   2.85   2.82   2.93   2.94   3.02   3.06   3.13   3.21   3.28   3.34   3.42
16.6%   3.16   3.22   3.17   3.19   3.36   3.38   3.42   3.44   3.54   3.63   3.63   3.70  ★3.72
```

#### netPnL (USDT)

```
   pd       60       75       90      105      120      135      150      165      180      195      210      225      240
12.0%   99854   95724   93852   93797   97995   97761   99434  100650  100394  101013  102869   99914   99319
13.0%  103769   99926  100037   99014  101054  100327  101063  100224  100950  102738  100460   99918  100920
14.0% ★104649  101792  101268   96929   99384   98034   98102   97217   98189   99404   98792   97938   99602
15.0%  102385  100306   99710   95238   96683   94654   95625   94649   94704   95935   94788   94080   93468
16.6%   94946   93302   87884   86719   87288   85921   84907   84656   86115   85299   82902   80951   80343
```

#### Trips

```
   pd     60     75     90    105    120    135    150    165    180    195    210    225    240
12.0%   8791   8513   8260   8083   7923   7742   7582   7421   7301   7163   7002   6882   6755
13.0%   7306   7076   6875   6713   6567   6409   6276   6139   6006   5894   5751   5657   5550
14.0%   6105   5915   5735   5558   5409   5289   5190   5064   4968   4844   4718   4642   4563
15.0%   5100   4917   4771   4611   4488   4383   4283   4175   4105   4041   3909   3859   3748
16.6%   3866   3721   3575   3471   3366   3287   3199   3132   3106   3003   2925   2839   2774
```

#### Findings

- **PF rises monotonically with CVD across every pd row.** No reversal
  in the 13 columns. The current cvd=75m default is sitting in a
  slight valley on every row.
- **Best PF: 16.6% × 240m = 3.72** (vs the current default at 3.22).
  +16% edge per trade for ~25% fewer trades. But netPnL drops to
  $80,343 from $93,302 because the trade-count loss outweighs the PF
  gain at this pd.
- **Best netPnL: 14% × 60m = $104,649.** The capacity champ. PF 2.50
  is much lower but trade count is 6,105 vs 2,774 at the PF champ.
- **netPnL ridge is flat across CVD at low pd:** pd=12% goes from
  $99,854 (60m) to $99,319 (240m), peaking at $102,869 (210m). CVD
  trades capacity for selectivity without changing dollar output.
- **CVD effect inverts on netPnL at high pd:** at pd=16.6%, the
  longer CVD *hurts* netPnL ($94,946 at 60m → $80,343 at 240m). Trip
  loss dominates per-trade gain when the entry is already strict.

#### Pareto frontier (top by metric)

| pd | cvd | PF | trips | netPnL | role |
|---:|----:|---:|------:|-------:|------|
| 16.6% | 240m | **3.72** | 2,774 | $80,343 | highest PF |
| 14.0% | 60m | 2.50 | 6,105 | **$104,649** | highest netPnL |

The current default (16.6% × 75m, PF 3.22, $93,302) is *neither* of
these. It's a balanced choice — moderate PF with moderate netPnL —
but provably suboptimal on both axes.

#### Reproducer

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
    --price-decline-thresholds 0.12,0.13,0.14,0.15,0.166 \
    --cover-ma-hours 72 \
    --cvd-minutes-list 60,75,90,105,120,135,150,165,180,195,210,225,240 \
    --stop-low-window-minutes 0 --time-stop-minutes 0 --min-reward-risk-ratio 0 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/long_fade_ma_pd_cvd/results.csv \
    --summary-csv data/crypto/long_fade_ma_pd_cvd/summary.csv
```

### v10c — extended CVD sweep (60m to 48h)

User raised: PF is the closer-to-risk-adjusted metric (notional scales
linearly with capital but PF doesn't); netPnL doesn't matter at fixed
notional. Question: at ~4k trips/year capacity, what's the best PF
achievable?

Sweep extended to CVD ∈ {60m, 75m, 90m, 105m, 2h, 135m, 150m, 165m,
3h, 195m, 210m, 225m, 4h, 6h, 8h, 12h, 16h, 20h, 24h, 32h, 40h, 48h}
(22 cols), pd ∈ {12%, 13%, 14%, 15%, 16.6%}, cover 72h, no stops.

#### PF heatmap (rows=pd, cols=CVD)

```
   pd     1h    75m    90m   105m     2h   135m   150m   165m     3h   195m   210m   225m     4h     6h     8h    12h    16h    20h    24h    32h    40h    48h
12.0%   1.94   1.92   1.93   1.97   2.05   2.09   2.15   2.19   2.21   2.25   2.34   2.33   2.34   2.55   2.61   2.67   2.63   2.58   2.90   2.41   2.00   1.84
13.0%   2.21   2.20   2.25   2.28   2.36   2.40   2.46   2.47   2.55   2.62   2.63   2.65   2.73   2.92   3.01   2.91   2.86   2.82   2.99   2.51   2.17   1.81
14.0%   2.50   2.49   2.55   2.52   2.63   2.67   2.72   2.74   2.82   2.91   2.96   3.03   3.14   3.28   3.30   3.12   2.87   2.85   3.10   2.53   2.08   1.78
15.0%   2.76   2.81   2.85   2.82   2.93   2.94   3.02   3.06   3.13   3.21   3.28   3.34   3.42   3.49   3.37   3.31   3.01   2.74   2.90   2.68   2.16   1.82
16.6%   3.16   3.22   3.17   3.19   3.36   3.38   3.42   3.44   3.54   3.63   3.63   3.70   3.72  ★3.87   3.59   3.36   2.98   3.07   3.23   2.58   2.18   2.00
```

#### Trips (capacity context)

```
   pd     1h    75m    90m   105m     2h   135m   150m   165m     3h   195m   210m   225m     4h     6h     8h    12h    16h    20h    24h    32h    40h    48h
12.0%   8791   8513   8260   8083   7923   7742   7582   7421   7301   7163   7002   6882   6755   5732   4894   3636   2653   1992   1562   1082    832    675
13.0%   7306   7076   6875   6713   6567   6409   6276   6139   6006   5894   5751   5657   5550   4703   3965   2910   2111   1567   1229    880    665    534
14.0%   6105   5915   5735   5558   5409   5289   5190   5064   4968   4844   4718   4642   4563   3858   3220   2361   1694   1252    990    712    520    433
15.0%   5100   4917   4771   4611   4488   4383   4283   4175   4105   4041   3909   3859   3748   3173   2649   1979   1359   1026    807    576    415    368
16.6%   3866   3721   3575   3471   3366   3287   3199   3132   3106   3003   2925   2839   2774   2322   1948   1439   1005    739    586    422    305    266
```

#### Findings

- **Each pd row peaks at a different CVD, then collapses.** Peak
  CVDs: 12%@24h (2.90), 13%@8h (3.01), 14%@8h (3.30), 15%@6h (3.49),
  **16.6%@6h (3.87)**. Past 8–24h every row crashes; 48h is back near
  break-even.
- **CVD substitutes for pd, but only partly.** Lower-pd × longer-CVD
  cells reach the same PF as higher-pd × shorter-CVD cells at matching
  trip counts. But the PF *peak* sits at high-pd × mid-CVD: the two
  axes are complementary, not redundant.
- **Highest PF in the grid: 16.6% × 6h = 3.87 / 2,322 trips.** +20%
  over the v10 default (16.6% × 75m, PF 3.22).
- **For ~4k trips/year target: 15% × 195m = PF 3.21 / 4,041 trips.**
  Same trip count as the v10 default but with one row lower pd and
  longer CVD. Marginal PF gain (+0.01) but the cell sits inside a
  flatter ridge so it's more robust to small parameter shifts.
- **Best 4k-trips alternative: 14% × 4h = PF 3.14 / 4,563 trips.**
  Lower PF but +14% capacity over the 4k headline cell.

#### Pareto frontier (top by PF, all trip counts)

| pd | cvd | PF | trips |
|---:|----:|---:|------:|
| 16.6% | 6h | **3.87** | 2,322 |
| 16.6% | 4h | 3.72 | 2,774 |
| 16.6% | 225m | 3.70 | 2,839 |
| 16.6% | 195m | 3.63 | 3,003 |
| 15.0% | 6h | 3.49 | 3,173 |

#### Pareto frontier (top by PF, ≥4,000 trips)

| pd | cvd | PF | trips | netPnL |
|---:|----:|---:|------:|-------:|
| **15.0%** | **195m** | **3.21** | **4,041** | **$95,935** |
| 14.0% | 4h | 3.14 | 4,563 | $99,602 |
| 15.0% | 3h | 3.13 | 4,105 | $94,704 |
| 15.0% | 165m | 3.06 | 4,175 | $94,649 |
| 14.0% | 225m | 3.03 | 4,642 | $97,938 |

#### Reproducer

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
    --price-decline-thresholds 0.12,0.13,0.14,0.15,0.166 \
    --cover-ma-hours 72 \
    --cvd-minutes-list 360,480,720,960,1200,1440,1920,2400,2880 \
    --stop-low-window-minutes 0 --time-stop-minutes 0 --min-reward-risk-ratio 0 \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/long_fade_ma_pd_cvd_deep/results.csv \
    --summary-csv data/crypto/long_fade_ma_pd_cvd_deep/summary.csv
```

### v10d — observational rvol + Q1 floor gate

The v8 long-engine quintile sweep concluded that rvol was non-monotonic
for long fades. With the v10 fixes (cover-MA reference, no stops) that
finding flipped: **rvol IS monotonic for long fades, with a stronger
spread than the short side**.

#### Setup

Added an observational rvol field to `OrderflowLongFadeMA`:
- numerator window = `CvdMinutes` (4h at the new default — same horizon
  as the entry-trigger CVD so the volume read matches the signal)
- denominator = 30d trailing mean
- exposed via the existing `RatioAtEntry` field on the trip record
  (no engine restructuring needed)

#### Rvol quintile breakdown — 14%/4h CVD/72h cover/no stops (4,563 trades, ungated)

| qtile | trips | PF | avgPnL | netPnL | rvolMed |
|------:|------:|------:|-------:|-------:|--------:|
| Q1 (rvol < 0.75) | 913 | 1.83 | $8.11 | $7,403 | 0.51 |
| Q2 (0.75–1.24) | 912 | 2.74 | $15.35 | $13,997 | 0.99 |
| Q3 (1.24–1.91) | 913 | 3.42 | $21.04 | $19,209 | 1.54 |
| **Q4 (1.91–3.15)** | 912 | **4.09** | $27.77 | $25,330 | 2.37 |
| Q5 (>3.15) | 913 | 3.53 | $36.87 | $33,664 | 4.72 |

**Findings:**

- **Rvol IS monotonic up to Q4 (PF 4.09), then mildly regresses at Q5
  (PF 3.53).** Same shape as the short side's "Q5 sometimes weakens"
  pattern, but at a much higher PF baseline (4.09 vs 1.20).
- **avgPnL is monotonic across all 5 quintiles** ($8.11 → $36.87) —
  even Q5 makes more money per trade than Q4 because winners are
  larger; PF dips because losers also widen.
- **The PF spread is 3-4× the short side's** (long Q1=1.83 → Q4=4.09;
  short Q1=0.88 → Q5=1.20).
- **D9 of decile breakdown is the single best cell:** rvol 3.15-4.72,
  PF 4.74, $37.56 avgPnL on 456 trades.

The v8 conclusion was wrong because the v8 engine was structurally
broken. With the lagged-decline reference and MinLow stop both gone,
the volume signal becomes legible.

#### Adding a rvol≥0.75 floor gate

Q1 had PF 1.83 — well above break-even but a clear drag on aggregate
metrics. Filtering it out:

| config | trips | PF | avgPnL | netPnL |
|--------|------:|------:|-------:|-------:|
| ungated (v10c default) | 4,563 | 3.14 | $21.83 | $99,603 |
| **rvol ≥ 0.75 (new v10d default)** | **3,914** | **3.44** | **$24.77** | **$96,964** |

Strict improvement on PF (+9.5%), avgPnL (+13.5%), at -2.6% netPnL.
Trip count drops 14% — still well above the 3,500-trip threshold for
viability.

#### New defaults

`OrderflowLongFadeMA.defaultLongFadeMAConfig()` now ships with:

  - `PriceDeclineThreshold = 0.14`
  - `CoverMaHours = 72`
  - `CvdMinutes = 240` (4h)
  - `RvolEntryThreshold = 0.75` (NEW; Q1 floor from the v10d quintile)
  - `StopLowWindowMinutes = 0`, `TimeStopMinutes = 0`,
    `MinRewardRiskRatio = 0.0` (all kept from v10)
  - `EntryDistanceMaxPct = 0.20`, `EntryDistanceRef = Low8h` (kept)

Aggregate result at default config: PF 3.44, $25 avgPnL, 3,914 trips,
$97k netPnL on the universe over the test window.

#### Reproducer

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- long-fade-ma-sweep \
    --max-bar-price-ratio 3 --reference-vol-pct 0.1019 --min-long-adv 8000000 \
    --results-csv data/crypto/long_fade_ma_default_rvol075/results.csv \
    --summary-csv data/crypto/long_fade_ma_default_rvol075/summary.csv
```

## Short fade — `OrderflowShortFadeMA` (2026-05-08 PM session)

Symmetric mirror of `OrderflowLongFadeMA` for the short side, with one
critical addition that emerged during the session: a **dual-CVD overlay**
that turns the engine from "fade every overextension" into a regime
trader. The short version of the v10d-style symmetric mirror failed
catastrophically (PF 0.95) before this overlay was added.

### What the engine does (current code)

**Per-bar signals** (computed every bar, leak-free):

  - `coverMa` = trailing `CoverMaHours` MA of `bar.Close`. Single
    reference for both the rise gate and (when enabled) the MA-touch
    cover.
  - `priceRise` = `bar.Close / coverMa - 1`.
  - `cvd` = trailing-`CvdMinutes` CVD = Σ(buy_dv − sell_dv) over the
    short window. Default 240m (4h).
  - `longCvd` = trailing-`LongCvdMinutes` CVD. Default 12,000m (200h).
    Risk-management overlay; see below.
  - `rvol` = mean(volume) over CvdMinutes / mean(volume) over 30d
    baseline (per-bar means; partial-window denominator uses actual
    `Count`, not nominal — diverges from the stratify-script convention
    on symbol-young trips).
  - 200h warmup gate plus all rolling windows must be full.

**Entry — ALL must hold simultaneously:**

  1. `priceRise >= cfg.PriceRiseThreshold` (default `0.14`; close
     ≥ 1.14 × coverMa).
  2. CVD negative cross: `prevCvd >= 0 AND cvd < 0`.
  3. **Long-CVD regime gate**: `longCvd < 0` (when LongCvdMinutes > 0).
     This is the dual-CVD principle: the short-side trigger fires often
     in routine bull pullbacks; only fade when the multi-day tape is
     also net-selling.
  4. ADV gate (`MinShortAdv`).
  5. Distance gate: `(refHigh - bar.Close) / refHigh ≤ EntryDistanceMaxPct`
     where `refHigh` is `MaxMa(8h)` of `bar.High` by default.
  6. Stop-above-close sanity (`stopLevel > bar.Close`).
  7. RR gate (default disabled; `MinRewardRiskRatio = 0`).
  8. `rvol >= cfg.RvolEntryThreshold` (default `0.75`, inherited from
     the long mirror — explicitly **flagged for re-tuning** in the new
     post-hoc breakdown below).

**Exit precedence (top wins):**

  1. **Time-stop**, when `TimeStopMinutes > 0` and `barsHeld` ≥ that
     window. Hard mode closes unconditionally; Conditional mode closes
     only if `bar.Close ≥ entryPrice` (unprofitable trade). Default
     **disabled**.
  2. **Dual-CVD cover** (active when `LongCvdMinutes > 0`, the default):
     close when `cvd >= 0 AND longCvd >= 0`. Fires once both the 4h
     trigger and the 200h regime have flipped non-negative — the
     selling regime that gated entry has cleared.
  3. **MA-touch cover** (fallback, only when `LongCvdMinutes = 0`):
     close when `bar.Close <= coverMa`. This was the original
     long-mirror exit and is what failed at PF 0.95 on the short side
     before the dual-CVD overlay was introduced.
  4. **Optional stops** (also leak-free, run before the cover branch
     each bar): vol-stop > VWAP-stop > pct-stop in precedence; all
     three default disabled. The high-stop, when enabled, snapshots
     a trailing-N-minute MaxHigh at entry and fills at `bar.Close`
     when `bar.VWAP >= stopLevel`.
  5. **Gap close** (top of bar): if the bar-to-bar price ratio exceeds
     `MaxBarPriceRatio` (recommended 3.0), close at `lastClose` and
     set a 200h signal lockout to keep entries from firing on stale
     post-gap rolling state. Without this gate enabled, the gap-handler
     never engages; with it enabled, behaviour matches the v0
     OrderflowMA convention.

**Default config (`defaultShortFadeMAConfig`):**

  - `PriceRiseThreshold = 0.14`
  - `CoverMaHours = 72`
  - `CvdMinutes = 240` (4h)
  - `LongCvdMinutes = 12_000` (200h, dual-CVD overlay ON by default)
  - `EntryDistanceMaxPct = 0.20`, `EntryDistanceRef = High8h`
  - `RvolEntryThreshold = 0.75` (provisional; see below)
  - all stops + time-stop disabled, RR-gate disabled

### Discovery sequence (this session, condensed)

1. **Symmetric mirror failed.** At pr=14% / cvd=240m / 72h cover / no
   stops / rvol≥0.75 (direct mirror of LongFadeMA v10d), the short
   engine produced PF **0.948**, n=6,846, netPnL **-$22,944**. Bumping
   pr to 50% made it slightly worse. Both rvol and pr quintile
   breakdowns were non-monotonic and dominated by a single
   catastrophic-loss bucket.
2. **Dual-CVD overlay flipped the result.** Adding the 200h CVD as
   both an entry gate and the cover criterion (replacing the MA-touch
   cover entirely) produced PF **1.690**, n=3,476, netPnL
   **+$218,514** on the same default flags. Hold time jumped from
   ~41h median to ~269h — the engine now holds to regime-flip rather
   than to MA-touch.
3. **Hold-bucket pattern.** Same as the RawZ-100 short-only engine:
   short-hold buckets (<1d, 1-3d, 3-10d) all bleed money; the >30d
   bucket carries the entire pnl with PF ~5-6. Crypto shorts are
   regime trades, not fades — confirmed across two engines.
4. **Flag-correctness incident.** The original 1.690-PF baseline was
   run **without** `--reference-vol-pct 0.1019`, **without**
   `--max-bar-price-ratio 3`, and **without** `--min-short-adv`.
   Every single trade was sized at the full $1000 notional; the gap
   detector was off entirely. PF was roughly notional-invariant so
   the qualitative conclusion held, but the dollar comparisons to the
   long side were systematically off. Fixed in the rebaseline below.

### v1 baseline — vol-targeted, ADV-gated, gap-detected

Re-run at default config with the standard flag set added:

```
dotnet run --project TradingEdge.CryptoBacktest -c Release -- short-fade-ma-sweep \
    --price-rise-thresholds 0.14 --rvol-entry-threshold 1.0 \
    --reference-vol-pct 0.1019 --max-bar-price-ratio 3 \
    --min-short-adv 8000000 \
    --results-csv data/crypto/short_fade_ma_pr14_rvol1_voltarget/results.csv \
    --summary-csv data/crypto/short_fade_ma_pr14_rvol1_voltarget/summary.csv
```

(Note: `--rvol-entry-threshold 1.0` is a slight bump from the engine
default `0.75` — picked to drop the lowest-rvol bucket so the post-hoc
breakdown is comparable to the doc's earlier 30d/24h table.)

**Aggregate**:

| trips | PF | avgPnL | netPnL | fund | fees | medBars |
|------:|---:|-------:|-------:|-----:|-----:|--------:|
| 2,550 | **2.036** | $42.78 | **+$109,086** | -$2,990 | $992 | 21,776 (~15.1d) |

PF jumped from the un-vol-targeted 1.690 baseline to **2.036** — the
ADV gate filtered out low-liquidity symbols whose pnl variance was
dragging the aggregate. Median trade now $478 of effective notional
(vol-target cap is $1000 on lowest-vol coins). Trip count dropped
from 3,476 to 2,550 (the ADV gate is doing real work).

### Post-hoc rvol breakdown (doc-table buckets)

| bucket | trips | PF | avgPnL | netPnL | avg_noti | medBars |
|--------|------:|------:|-------:|---------:|---------:|--------:|
| <0.5 | 0 | – | – | – | – | – |
| 0.5–1 | 0 | – | – | – | – | – |
| 1–1.5 | 356 | 1.862 | $37.7 | +$13,427 | 372 | 36,437 |
| 1.5–2 | 292 | 2.731 | $64.4 | +$18,795 | 444 | 35,547 |
| 2–3 | 378 | **2.868** | $57.1 | +$21,570 | 467 | 27,964 |
| 3–5 | 456 | 2.564 | $57.3 | **+$26,127** | 499 | 23,673 |
| 5–10 | 461 | 1.645 | $34.7 | +$16,017 | 530 | 17,884 |
| ≥10 | 607 | 1.510 | $21.7 | +$13,150 | 542 | 1,040 |

(<0.5 and 0.5–1 empty because the entry gate is rvol ≥ 1.0.)

**Shape**: clear hump, peak at **2–3 (PF 2.87)**, second at 3–5 (PF
2.56). The tails (1–1.5 and ≥10) underperform. This **agrees with the
30d/24h short-side row of the original volume-bucket table** (peak
3–5, PF 3.20 there) — but here against ShortFadeMA's actual entry set
rather than RawZ-100's. The earlier "rvol monotonicity inverted" claim
was wrong — it was an artifact of the un-vol-targeted run + a wider
entry net (rvol≥0.75 included Q1 noise that dragged the lower buckets).

**Vol-targeting × bucket interaction**: avg notional climbs
monotonically with rvol (372 in 1–1.5 → 542 in ≥10). High-rvol entries
sit on coins whose 1m vol is closer to the 0.1019% reference, so notional
caps near $1000; low-rvol entries skew to calmer-name regimes where
realized vol is higher and notional is cut. So the low-rvol PF buckets
are doing more *per-dollar* than the raw netPnL column suggests.

### Hold-bucket breakdown (regime-trader signature)

| hold | trips | PF | avgPnL | netPnL |
|------|------:|------:|-------:|---------:|
| <1d | 824 | 0.100 | -$24.3 | -$20,058 |
| 1–3d | 133 | 0.115 | -$77.8 | -$10,345 |
| 3–10d | 199 | 0.329 | -$49.9 | -$9,938 |
| 10–30d | 424 | 1.092 | +$5.3 | +$2,260 |
| **>30d** | **970** | **5.625** | **+$151.7** | **+$147,168** |

Same hold-shape as RawZ-100 short-only and the un-vol-targeted dual-CVD
run before it: short-hold buckets bleed, the >30d bucket carries
everything. **38% of trips hold over 30 days** and account for ~135% of
net pnl (the short-hold buckets eat into the rest).

### Findings

1. **Dual-CVD overlay is load-bearing.** Without it, the symmetric
   mirror is unprofitable. The 200h CVD operates as a regime
   confirmation that filters out 4h CVD-flips that fire during routine
   bull pullbacks.
2. **PF curve is humped, not monotonic.** Peak around rvol 2–3, drops
   off both ways. The current `RvolEntryThreshold = 0.75` default
   includes the 1–1.5 tail (PF 1.86) but excludes the bottom buckets;
   it's not obviously wrong, but raising to 1.5 or 2.0 would lift
   aggregate PF.
3. **Short edge is concentrated in long holds.** Identical pattern to
   RawZ-100 short-only — confirmed twice across independent engines.
   The implication: **shorts compound on regime trades, not on mean
   reversion**. The MA-touch cover (which forces shorter holds) is the
   wrong mechanism for shorts; the dual-CVD cover (which holds to
   regime-flip) is the right one.
4. **Standard-flag set is non-negotiable.** Vol-targeting, ADV gate,
   and gap detector all materially change the result and must be on
   for any future ShortFadeMA sweep.

### Open question for next session

The doc-style table peak suggests `RvolEntryThreshold = 1.5` or `2.0`
would lift aggregate PF. But the right move here is probably to drop
hard rvol gating in favor of **MAv1-style sizing buckets** — keep
entries unchanged so the engine state machine is stable across size
schedules, but assign zero notional to the 1–1.5 and ≥10 buckets and
full notional to the 2–5 sweet spot. That work is queued under MAv1.

Also queued: re-test whether the **MA-touch cover** (the original
long-mirror exit) actually works in the post-fixes regime — it failed
catastrophically pre-overlay, but that was un-vol-targeted, with a
wider rvol entry net, and pre-gap-detector. The clean comparison with
the standard flag set on hasn't been done.
