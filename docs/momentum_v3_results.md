# Mid-Cap Momentum v3 — Clipped-Methodology Era

**Status: working long-only daily-momentum edge. Filtered (breadth + ≥2005): 3,678 trips, PF raw
2.021 / clip 1.590, +$915k. Honest next-open fills, 5-day time-stop.**

v3 is not a new engine or a new system — it is the **same `TradingEdge.MomentumV2` production system**,
carried forward into a research methodology that computes every PF/return figure on **clipped per-trade
returns** and decides on **cumulative** views. That shift (2026-06-21) was significant enough — it
re-derived the tightness and ATR% optima and exposed several prior "edges" as lottery-winner mirages —
to warrant a fresh document. The full historical research record (exit-mechanic sweeps, 52w-proximity
studies, loose-base shorts, regime-switching, chandelier/time-stop derivation) lives in
[`momentum_v2_results.md`](momentum_v2_results.md) and is **not** repeated here; v3 carries only the
*current production state* and the work done under the clipped methodology.

`TradingEdge.MomentumV2` is a ground-up F# rewrite: all indicators computed in a single in-memory pass
(no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~16s**, so the
parameter sweeps below are minutes, not hours.

---

## 📐 Methodology — the clipped, cumulative standard (PROJECT STANDARD as of 2026-06-21)

Two disciplines now govern every sweep in this document:

**1. Winner-clip: PF / mean-return are computed on each trade's RETURN clipped at +50% (`LEAST(ret, 0.50)`);
the loss side is left untouched.** PF is mean-driven and therefore hostage to lottery winners — a single
buyout pop or +800% runner can hand a bucket a gaudy PF that has nothing to do with its *reliable* edge, and
that contamination is exactly what makes bucketed/decile views go non-monotonic. Clipping the upside to a
sensible ceiling (+50% is above p99 of qualifying trades — generous) makes PF reflect the *typical* trade in
a bucket, which is what a floor/ceiling filter decision actually rests on. **Total P&L is understated by
design** (the conservative read); when raw total-$ matters it is reported separately as `PF raw` / `tot`.

**2. Decide on CUMULATIVE views** (`X ≥ thr` / `X < thr`), which are low-variance and monotone; use
**non-cumulative bands only diagnostically** to locate where edge changes — their edge-of-range cells are
noisy and must not drive a threshold choice on their own.

*Why this matters (evidence):* under the clip, `tight < 3.0` showed **PF raw 2.67 vs PF clip 1.388** — its
apparent edge was almost entirely a handful of monster winners. The clipped column is the honest read; the
raw column is seductive and non-monotone. Several v2-era "findings" that rested on raw PF have been re-checked
under this standard (and where they changed, the v3 entry says so).

---

## The system in one screen (current production state)

Long-only daily momentum on US common stocks / ADRs (`ticker_reference.type IN ('CS','ADRC')`),
2005–2026. One position per breakout signal, fixed $10k notional, uncapped concurrency, no
compounding (so net P&L is a raw edge-and-breadth measure, not an achievable equity curve).

**Entry** — on a daily bar, go long at the close when ALL hold (each indicator uses *prior* bars,
no lookahead):

| gate | threshold | meaning |
| --- | --- | --- |
| entry-day move | **10% ≤ move < 30%** | `close/prevClose − 1`. The breakout must announce itself; the 30% cap removes the single-day exhaustion blow-off (and makes an rvol upper cap redundant) |
| relative volume | **rvol ≥ 5** (no upper cap) | `volume / 28-day avg volume`. 5 is enough to be significant; the move cap handles the toxic high-rvol blow-off tail |
| ATR% (log) | **< 0.10** | mean log-true-range over 14 prior bars. Tightened 0.11 → 0.10 on 2026-06-21 (the 0.10–0.11 band was dead) |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear scale; sharper loose-tail cut than log) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high (closing-high channel beats the intraday-high channel) |
| price floor | **≥ $5** | no sub-$5 names |
| liquidity | **avg dollar volume ≥ $100k** | 28-day average |
| breadth (market-wide) | **`pct_above_20` lagged 1 day > 0.5** | applied post-hoc; risk-on regime only |
| heat (market-wide) | **`h10 < 0.25`** | skip entries when the top-1%-gainer froth measure is hot (Sykes-inspired); applied post-hoc |

**Exit** — **5-day time-stop, NO price stop** (the current default; the v2 doc's header still describes the
older Qullamaggie trailing stop — superseded). Sell at the next bar's open on the 5th day. The "disaster
exit" tested in v2 turned out to be a SHORT signal, not a long exit, and is off. Open positions at the data's
end are marked-to-market at the final close.

**Headline (filtered: breadth + 2005-start, 21.4 trading-day-years; ATR% 0.10):**

| | value |
| --- | ---: |
| trips | 3,678 |
| win rate | 52.3% |
| profit factor (raw) | **2.021** |
| profit factor (clip +50%) | **1.590** |
| net P&L | +$915,056 |

*(Unfiltered engine run at production defaults: 5,827 trips / PF 1.802 / +$1.19M.)*

---

## Entry-filter geometry — the clipped re-derivation (2026-06-21)

All three sweeps below use the **wide dump** (both volatility gates opened so the full range is visible):

```bash
dotnet run -c Release --project TradingEdge.MomentumV2 -- \
  --out /tmp/v2_wide_tight_atr.csv --max-tightness 1000 --max-atr-pct 1000   # ~9,090 trips
```

then sub-filter in SQL with breadth lag1 > 0.5, ≥2005, closed trips, clip +50%.

### Tightness ceiling — a HUMP peaking exactly at 4.5

The naive intuition ("tighter = safer = better") is **wrong**: clipped PF climbs off the ultra-tight floor,
peaks in the 3.5–4.5 zone, and falls off a cliff above 4.5. Script:
[`scripts/equity/tightness_atr_cum_sweep.sql`](../scripts/equity/tightness_atr_cum_sweep.sql).

| tight ceiling | n | PF clip | clip post | | tight ceiling | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 2.5 | 298 | 1.219 | 1.499 | | **< 4.5** | 3,713 | **1.571** ← peak | 1.478 |
| < 3.0 | 1,082 | 1.388 | 1.247 | | < 5.0 | 4,301 | 1.495 | 1.386 |
| < 3.5 | 2,047 | 1.447 | 1.338 | | < 5.5 | 4,727 | 1.475 | 1.389 |
| < 4.0 | 3,003 | 1.568 | 1.447 | | < 7.0 | 5,365 | 1.390 | 1.291 |

Non-cumulative bands locate the source — best band is **3.5–4.0 (clip 1.864)**; **< 2.0 is a loser (0.836)**;
edge collapses above 4.5 (**5.5–7.0 = 1.015, post 0.886**). `tight<3.0` raw PF 2.67 → clip 1.388 is the clearest
lottery-mirage in the data. `MaxTightness = 4.5` is the optimum of the entire 2.5–7 range.

### ATR% ceiling — peaks at 0.10; the edge lives in the 0.06–0.10 band, NOT the quiet bulk

Same lesson on the other axis. Cumulative PF peaks at **< 0.10**; the non-cumulative bands show the edge is
concentrated in **0.06–0.10 (PF ~1.9–2.5)**, not the quiet `< 0.04` bulk (1.42):

| atr% ceiling | n | PF clip | clip post | | atr% band | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 0.04 | 2,254 | 1.420 | 1.236 | | < 0.04 | 2,254 | 1.420 | 1.236 |
| < 0.06 | 3,158 | 1.438 | 1.355 | | 0.06–0.07 | 222 | 1.925 | 1.867 |
| < 0.08 | 3,524 | 1.529 | 1.470 | | 0.07–0.08 | 144 | 2.013 | 1.853 |
| < 0.09 | 3,615 | 1.569 | 1.501 | | **0.08–0.09** | 91 | **2.460** | **2.016** |
| **< 0.10** | 3,678 | **1.590** ← peak | **1.520** | | 0.09–0.10 | 63 | 2.208 | 1.903 |
| < 0.11 (old) | 3,713 | 1.571 | 1.478 | | 0.10–0.11 | 35 | 0.984 | 0.633 ← dead |

**✅ DECISION (2026-06-21): `MaxAtrPct` 0.11 → 0.10.** Engine-verified: raw PF **1.774 → 1.802**, P&L flat at
**+$1.19M**, trips 5,883 → 5,827 (−56). The 0.10–0.11 band was net-neutral on $ but PF-dilutive.

### 2D joint ceiling — the production corner IS the joint optimum

The joint cumulative grid (each cell = `tight < T` AND `atr% < A`, clip +50%) rules out a hidden off-diagonal
sweet-spot. Script: [`scripts/equity/tightness_atr_2d_sweep.sql`](../scripts/equity/tightness_atr_2d_sweep.sql).

**PF post-2015 (the era that matters); production corner `(4.5, 0.10)` is the grid max:**

| tight \ atr% | < .06 | < .07 | < .08 | < .09 | < .10 | < .11 |
|---|--:|--:|--:|--:|--:|--:|
| < 4.0 | 1.371 | 1.384 | 1.412 | 1.453 | 1.483 | 1.447 |
| **< 4.5** | 1.355 | 1.425 | 1.470 | 1.501 | **1.520** | 1.478 |
| < 5.0 | 1.314 | 1.387 | 1.398 | 1.429 | 1.427 | 1.386 |
| < 5.5 | 1.375 | 1.433 | 1.433 | 1.438 | 1.434 | 1.389 |
| < 7.0 | 1.276 | 1.304 | 1.322 | 1.305 | 1.318 | 1.291 |

The axes **reinforce** (no trade-off — moving toward the corner along either axis helps); both cliffs (quiet
`< 0.05` ATR%, loose `tight < 7.0`) hold across the other axis. **`(MaxTightness 4.5, MaxAtrPct 0.10)` is the
joint optimum — nothing to change.** *(Full-sample grid and trip-count grid: see the script output / v2 doc
equivalents.)*

---

## Active production-defining findings (carried from v2, still live)

These define the current entry/exit and remain in force. Full derivation and era-split tables are in
[`momentum_v2_results.md`](momentum_v2_results.md) at the linked sections; summarized here as the live spec.

- **5-day time-stop, NO price stop** — on high-edge breakouts the move is concentrated in the first ~5 days;
  moving stops around doesn't help. The "disaster exit" is a SHORT setup, not a long exit (off).
  *(v2: "5-day time-stop, NO price stop"; "edge concentrated in first ~5 days".)*
- **Entry-day move is a NOTCH, not a ramp** — 25–30% is the best band, 30–40% the worst (exhaustion). Cap at 30%.
  ⏳ *Re-confirm under the clip — see "next" below.* *(v2: "Entry-day move — SWEET-SPOT NOTCH".)*
- **rvol three regimes** — <1 fakeout, ~6–15 durable edge, >15 toxic blow-off (the 2020-21 pump cohort, not
  buyouts). Floor at 5; the 30%-move cap supersedes an rvol upper cap. *(v2: "rvol sweep"; "why >15 is neutral".)*
- **Past-runner floor** (deferred, not in engine) — max-over-6mo of 14d ATR%/slope; top decile PF ~2.7, monotone,
  era-robust, gate-amplified. "Higher is better" → use a FLOOR. *(v2: "Past-runner personality".)*

### Regime filters (post-hoc, not yet in engine)

- **HEAT** (Sykes-inspired, ⭐) — daily mean return of the **top 1% gainers** (30-cal-day ADV ≥ $1M universe;
  **+1000% per-stock return clip is LOAD-BEARING**), trailing-10d lagged = `h10`. **Skip entries when h10 ≥ 0.25**
  (80th pctile). Froth is bad for breakouts. PF 1.99 → 2.27.
- **BREADTH** — `pct_above_20` on CS/ADRC + 30-cal-day ADV ≥ $1M (reproduces the production `breadth.parquet`).
  PF rises with breadth to a peak at ≥0.70, then rolls over; current gate 0.5.
- **COLD** (bottom-1% losers) — same axis as heat (corr −0.65), redundant; not wired.
- Both built by **`scripts/equity/build_breadth_and_heat.sql`**.
- **TEST-TICKER GOTCHA (resolved):** NASDAQ test symbols (ZXZZT etc.) have corrupt prices; Polygon does NOT list
  them — fix is a hardcoded blocklist in the build script. The +1000% clip is the backstop for real-ticker glitches.

---

## Reproduction

```bash
# v3 default (2026-06-21): NO price stop, 5-day time-stop, next-open exit; entry filters
# ATR% < 0.10 (log), tightness < 4.5 (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap).
# Run from dataset start for the 252-day warmup; filter entries to >=2005 post-hoc.
dotnet run --project TradingEdge.MomentumV2 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 -o /tmp/v3.csv

# then apply, post-hoc:
#   breadth_lag1 > 0.5   — LAG(pct_above_20) by 1 trading day on
#                          data/equity/momentum_v0/breadth.parquet, joined on entry_date
#   heat h10 < 0.25      — join data/equity/momentum_v0/heat.parquet on entry_date
#   entry_date >= 2005-01-01
#
# PF/return figures: clip per-trade return at +50% (LEAST(ret,0.50)); decide on cumulative views.
```

All defaults live in `TradingEdge.MomentumV2/Backtest.fs` (`defaultConfig`). Threshold sweeps use the CLI
overrides `--max-atr-pct`, `--max-tightness`, `--rvol-min/--rvol-max`, `--up-threshold/--max-up-threshold`,
`--exit-time-cap`, `--stop-low-window`, `--trail-window`.

For engine internals, the dividend-adjustment data fix, and the in-code TODO, see
[`momentum_v2_results.md`](momentum_v2_results.md) (§ Engine notes / Data fix / Known TODO) — unchanged in v3.

---

## Next session — INTRADAY entries near the open (planned 2026-06-21)

The whole system so far enters **at the close**. Next: test entering the same high-conviction setups **near the
open** and holding a few days, to capture the part of the move that happens *during* the signal day.

**Why retry — it failed before, but under different conditions.** The old opening-range-breakout test used
essentially *every* rvol stock with **no tightness/ATR%/move/breadth/heat filter** — none of the edge-concentrating
conditions discovered since. An intraday entry on the *filtered* population is a genuinely different experiment.

**Open questions to design around:**
- **Entry timing:** at the open? after an opening range (first 5/15/30 min)? "Buy near the open if the setup is
  intact" may beat waiting for a range break.
- **Which signal is known pre-close?** rvol / move / tightness / ATR% are partly intraday — define them as-of the
  entry *moment* (e.g. rvol projected from morning volume), not the full-day close. **Main correctness risk.**
- **Data:** needs intraday bars (`intraday_10s` exists). The engine is daily-bar only — an intraday entry path is
  a real engine change, not a post-hoc SQL study.
- **Special case:** the very-high-rvol blow-offs the close-entry system *excludes* might behave differently entered
  early in the day — deferred, worth a separate look once the harness exists.

The bar is high: the close-entry system is PF ~2.0 raw (filtered, post-2015 clip ~1.5) with multiple independent
edges stacked. Intraday only earns its complexity if it adds materially on top.
