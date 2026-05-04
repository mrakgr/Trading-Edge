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
