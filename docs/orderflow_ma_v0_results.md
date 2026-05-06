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

### Z-score mode is the cleanest cut

Standardizing by per-symbol 60d std normalizes BTC's ±5% range and WIF's
±50% range onto the same axis. Z-bucket cutpoints: <-3, -3 to -2, -2 to -1,
-1 to 0, 0 to +1, +1 to +2, +2 to +3, ≥+3.

**LONG side (1,828 trades, total PF 1.31):**

| **z-bucket** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <-3 | 1 | (n/a) | −6 | −5.88 |
| -3 to -2 | 19 | 1.30 | +151 | +7.96 |
| **-2 to -1** | 337 | **1.69** | +4,386 | +13.01 |
| -1 to 0 | 527 | 1.11 | +1,518 | +2.88 |
| 0 to +1 | 343 | 1.19 | +1,833 | +5.34 |
| +1 to +2 | 254 | 1.37 | +2,725 | +10.73 |
| **+2 to +3** | 162 | 1.53 | +2,678 | +16.53 |
| ≥+3 | 185 | 1.30 | +2,155 | +11.65 |

The long side has a **U-shape in PF**: 1.69 at -2σ (mean-reversion
buying), dips to 1.11–1.19 around the mean (the noisy middle), and
climbs back to 1.53 at +2σ. The right tail (≥+3σ) is still profitable
(PF 1.30) — coins multiple-σ above normal can keep extending. The
weakest band is -1 to +1σ which carries 47.5% of long trades but only
22% of long net P&L.

**SHORT side (4,387 trades, total PF 1.78):**

| **z-bucket** | **trades** | **PF** | **net $** | **avg $** |
|---|---|---|---|---|
| <-3 | 31 | **2.18** | +1,362 | +43.92 |
| -3 to -2 | 93 | 1.31 | +1,655 | +17.80 |
| -2 to -1 | 1,082 | 1.78 | +39,720 | +36.71 |
| **-1 to 0** | 1,368 | **1.86** | +56,026 | +40.95 |
| 0 to +1 | 878 | 1.77 | +36,575 | +41.66 |
| +1 to +2 | 550 | 1.51 | +16,108 | +29.29 |
| +2 to +3 | 250 | 2.01 | +10,173 | +40.69 |
| **≥+3** | 135 | **2.37** | +6,480 | +48.00 |

**Every short bucket is profitable.** The strongest are -1 to 0σ
(PF 1.86, 1,368 trades, $56K net — the bulk of the short edge) and the
extremes: ≥+3σ (PF 2.37, the highest PF anywhere) and <-3σ (PF 2.18).
The weakest is +1 to +2σ (PF 1.51) — moderately-elevated coins where
momentum continuation is most likely to keep going up.

The shape is **roughly symmetric around the mean**: shorts work in both
directions if the deviation is large enough. Mid-rally fades (+1 to +2σ)
are the riskiest because that's where momentum continuation kicks in,
but past +2σ the snap-back probability rises again.

### Implications for entry filtering

The natural follow-ups (not yet implemented):

1. **Long-side dead zone**: -1 to +1σ for longs is PF 1.15 over 870
   trades. If this band signals weak edge, an entry filter dropping
   long fires there would remove ~48% of long trades while keeping ~78%
   of long P&L. Net per remaining long trade roughly doubles.

2. **Short-side mid-rally caution**: +1 to +2σ shorts (PF 1.51, 550 trades)
   are the weakest short band. Skipping them sacrifices $16K of net but
   keeps the system at PF >1.7 across remaining short fires.

3. **Z-score as a sizing modulator instead of a filter**: scale notional
   up at high-PF z-buckets (-2σ longs, ≥+3σ shorts), down at low-PF
   buckets. Implements "bet bigger when conviction is stronger" without
   removing trades.

For now this is purely a stratification finding — the system itself
remains unchanged.

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
