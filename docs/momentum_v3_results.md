# Mid-Cap Momentum v3 — Clipped-Methodology Era

**Status: working long-only daily-momentum edge. Filtered (breadth + ≥2005): 4,245 trips, PF raw
1.960 / clip 1.600, +$1.08M. Honest next-open fills, 5-day time-stop.**

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
| intraday return | **`close/open − 1 ≥ −0.07`** | reject deep intraday FADES (gap-up-then-sell-off — the toxic tail of the red-candle band). Added 2026-06-21 |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear scale; sharper loose-tail cut than log) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high (closing-high channel beats the intraday-high channel) |
| price floor | **≥ $1** | lowered 5 → 1 on 2026-06-21 (sub-$5 is real edge under the clip, not a lottery; $1–3 kept for the future past-runner floor to rescue) |
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
| trips | 4,245 |
| win rate | 52.7% |
| profit factor (raw) | **1.960** |
| profit factor (clip +50%) | **1.600** |
| net P&L | +$1,078,992 |

*(Unfiltered engine run at production defaults: 6,647 trips / PF 1.851 / +$1.56M. With heat<0.25 added:
3,157 trips / PF raw 2.191 / clip 1.678 / post-2015 1.648 / +$879k.)*

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

### Vol-window length (ATR% + tightness lookback) — 14 is well-chosen; 13–18 is a plateau (2026-06-21)

The ATR% and tightness measures share a single lookback, fixed at **14 bars** since inception and never swept.
Added a `--vol-window` flag (sets BOTH lookbacks) and ran the engine for every window **10→25** (each is a full
run — the window redefines the measures, so it can't be sub-filtered in SQL; production caps atr%<0.10 / tight<4.5
re-apply inside each run). Clip +50%, breadth on, ≥2005. Script:
[`scripts/equity/vol_window_sweep.sql`](../scripts/equity/vol_window_sweep.sql).

| window | n | PF clip | PF raw | clip post | net P&L | | window | n | PF clip | clip post | net P&L |
|---|--:|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| 10 | 5,356 | 1.428 | 1.695 | 1.381 | $1.07M | | 18 | 3,442 | 1.612 | 1.480 | $0.93M |
| 11 | 5,079 | 1.481 | 1.772 | 1.440 | $1.09M | | 19 | 3,252 | 1.592 | 1.445 | $0.89M |
| 12 | 4,793 | 1.497 | 1.805 | 1.477 | $1.07M | | 20 | 3,045 | 1.581 | 1.440 | $0.84M |
| **13** | 4,540 | 1.569 | 1.906 | **1.521** | **$1.10M** | | 21 | 2,845 | 1.577 | 1.440 | $0.82M |
| **14** | 4,314 | 1.575 | 1.923 | 1.509 | $1.07M | | 22 | 2,649 | 1.582 | 1.464 | $0.78M |
| 15 | 4,084 | 1.560 | 1.933 | 1.501 | $1.01M | | 23 | 2,483 | 1.629 | 1.505 | $0.79M |
| 16 | 3,855 | 1.564 | 1.943 | 1.486 | $0.97M | | 24 | 2,324 | 1.641 | 1.516 | $0.65M |
| 17 | 3,622 | 1.581 | 1.983 | 1.496 | $0.95M | | 25 | 2,160 | 1.672 | 1.519 | $0.64M |

**Findings:**
1. **Short windows (10–12) are clearly worse** (clip 1.43–1.50) — a 10-bar base is too noisy to capture a real
   consolidation. Rules out anything below 13.
2. **13–14 is a local optimum AND the post-2015 peak.** Clip PF jumps 1.497 (w12) → 1.569 (w13) → 1.575 (w14), and
   **post-2015 PF tops the entire range at w13 (1.521) / w14 (1.509)** while capacity is still healthy (~4,300–4,500
   trips) and total P&L is at its max ($1.10M / $1.07M).
3. **The long-end full-sample PF rise (23–25 → 1.63–1.67) is a thinning artifact, not a real edge.** Trips more than
   halve (4,540 @ w13 → 2,160 @ w25) and **net P&L falls steadily** ($1.10M → $0.64M) even as PF climbs — the classic
   "stricter filter keeps only the easy trades" pattern. Post-2015 PF also *dips* through 19–21 (1.44) before the
   thin recovery, so it's not a clean monotone improvement.

**Verdict: 14 is well-chosen — it sits at the front of the 13–18 plateau where post-2015 PF is best and capacity is
full.** w=13 marginally edges it (post-2015 1.521 vs 1.509, +226 trips, +$33k), but the gap is within noise.
**✅ DECISION (2026-06-21): KEEP the window at 14** — the long-standing, well-understood default on the plateau;
not worth re-tuning to a coin-flip edge. `AtrWindow = TightnessWindow = 14` unchanged. The `--vol-window` flag
stays available for future sweeps.

### 2D joint FLOOR — entry-day move% × rvol: both floors reinforce; the move floor may be too LOW (2026-06-21)

The volatility *ceilings* (tightness, ATR%) define which bases qualify; the move% and rvol *floors* define how
hard the breakout must announce itself. Joint cumulative grid, each cell = `move ≥ M` **AND** `move < 0.30`
(the blow-off cap always on) **AND** `rvol ≥ R`, clip +50%. Wide dump with both gates opened
(`--up-threshold 0 --max-up-threshold 1000 --rvol-min 0 --rvol-max 100000`); rest production. Script:
[`scripts/equity/move_rvol_2d_sweep.sql`](../scripts/equity/move_rvol_2d_sweep.sql). Production corner
`(move ≥ 10%, rvol ≥ 5)` is **bolded**.

**PF (clipped +50%), rows = move ≥ M, cols = rvol ≥ R:**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.048 | 1.192 | 1.269 | 1.361 | 1.506 | 1.483 |
| ≥ 5% | 1.199 | 1.288 | 1.435 | 1.576 | 1.790 | 1.638 |
| ≥ 10% | 1.364 | 1.425 | **1.590** | 1.641 | 1.777 | 1.552 |
| ≥ 15% | 1.414 | 1.493 | 1.650 | 1.621 | 1.740 | 1.463 |
| ≥ 20% | 1.487 | 1.593 | 1.728 | 1.727 | **2.054** | 1.779 |
| ≥ 25% | 1.430 | 1.490 | 1.768 | 1.617 | 1.981 | 1.240 |

**PF post-2015 (the decision surface):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.060 | 1.169 | 1.280 | 1.411 | 1.574 | 1.454 |
| ≥ 5% | 1.205 | 1.262 | 1.428 | 1.593 | 1.728 | 1.558 |
| ≥ 10% | 1.317 | 1.343 | **1.520** | 1.604 | 1.628 | 1.405 |
| ≥ 15% | 1.312 | 1.386 | 1.622 | 1.646 | 1.512 | 1.219 |
| ≥ 20% | 1.455 | 1.560 | 1.754 | 1.758 | 1.755 | 1.473 |
| ≥ 25% | 1.394 | 1.471 | **1.807** | 1.647 | 1.610 | 1.142 |

**Trip counts (n):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 493,741 | 39,872 | 14,464 | 7,758 | 4,038 | 2,078 |
| ≥ 5% | 40,622 | 14,181 | 6,076 | 3,187 | 1,550 | 755 |
| ≥ 10% | 9,724 | 6,384 | **3,678** | 2,149 | 1,125 | 578 |
| ≥ 15% | 3,468 | 2,850 | 1,994 | 1,327 | 758 | 418 |
| ≥ 20% | 1,271 | 1,150 | 926 | 679 | 442 | 267 |
| ≥ 25% | 438 | 410 | 359 | 269 | 184 | 121 |

**Findings:**
1. **Both floors monotonically reinforce up the diagonal.** Along rvol (any move row) PF climbs `≥1 → ≥10` then
   dips at `≥15` (the toxic pump tail — bad in *every* row). Along move (any rvol col) PF climbs `≥0 → ≥20–25`.
   The two floors stack: the rich cells are the high-move × high-rvol diagonal, the poor cells the low-low corner
   (`move≥0 × rv≥1 = 1.048` — essentially no edge).
2. **The production move floor of 10% looks too LOW.** The shipped corner `(move≥10, rv≥5)` is post-2015 **1.520**,
   but raising the move floor to **15–20%** at the same rvol lifts it to **1.62 → 1.75** with still-usable size
   (`move≥15 × rv≥5` = 1.622 / n 1,994; `move≥20 × rv≥5` = 1.754 / n 926; `move≥20 × rv≥7` = 1.758 / n 679). The
   `move≥25 × rv≥5` cell is the post-2015 PF max of the whole grid at usable size (**1.807**, n 359).
3. **The `rv≥15` column is a wall** — PF falls off in every move row (toxic blow-off), confirming the high-rvol
   tail is genuinely bad, not merely thin. The deep corner (`move≥25 × rv≥15`, n 121) is noise.

**✅ DECISION (2026-06-21): KEEP the move floor at 10% (`UpThreshold = 0.10`, unchanged).** Raising it would lift
PF (`move≥15` → post-2015 1.622, `move≥20` → 1.754) but at a steep capacity cost — `move≥15` roughly halves the
book (3,678 → 1,994 at rv≥5) and `move≥20` quarters it (→ 926). The 10% floor is the **capacity anchor**; the PF
gain isn't worth that much lost size while sizing/capacity is unmodeled (the backtest is uncapped, non-compounding,
so PF-per-trip is the relevant axis only once a real book constrains concurrency). The finding stands as documented
upside to revisit *after* capital efficiency is modeled — at that point a higher move floor is the first lever to
pull. The prior notch work (25–30% best, 30–40% worst, *which is why the 30% cap exists*) is consistent: within
the [0,30] window the edge keeps rising toward the cap.

### Sub-$5 stocks under the clip — the $5 floor was a raw-PF over-correction (2026-06-21)

The $5 price floor was set in the raw-PF era because the sub-$5 bucket showed a gaudy PF driven by a few lottery
winners. With the clip we can finally see the region honestly. Wide dump with the price floor dropped
(`--min-price 0`, rest production); script [`scripts/equity/sub5_price_sweep.sql`](../scripts/equity/sub5_price_sweep.sql).

**The whole sub-$5 region is NOT a lottery mirage** — its raw PF (1.592) is barely above its clipped PF (1.526),
i.e. *under the production filters there is no fat tail to clip*.

**Where did the original "PF ~6" sub-$5 lottery go? It was never in THIS population — and the cause was NOT the
return clip.** Tracing it back to `momentum_v0_results.md` (the deep-discount structure study), the old inflation
had two compounding causes, both orthogonal to the +50% clip:
1. **It was dollar-P&L-weighted, not return-weighted.** Old PF was computed on dollar `net_pnl`. At fixed $10k
   notional a sub-$5 name buys a huge share count, so a split/adjustment glitch became a giant *dollar* number —
   the v0 doc records **a single trade at +$25M (a ~2,500× return) that was 84.6% of its tier**. The v3 sweeps
   compute PF on per-trade **return** (`exit/entry−1`), which is structurally immune to the share-count explosion
   (a 2,500× glitch is `ret=2500`, not `+$25M`). *This* — return-weighting, not the +50% price-gain clip — is the
   real reason "PF 6" became "PF 1.59".
2. **The lottery lived in a universe we now exclude.** Those glitch trades were **deep-discount, >50%-below-52w-high
   weakness breakouts** (54% sub-$5, median $4.22 per the v0 doc). Production now requires `52w ≥ 0.95` — *near
   highs* — so we never enter the beaten-down penny names where the corrupted rows were.

**Proof (verified 2026-06-21):** the single largest sub-$5 trade in the production population is **+161%** (a clean
$3.34 → $8.71 move) — no monster glitch exists; and `pf_dollar == pf_return == 1.592` *identically* (they only
diverge when a tail glitch is present). The v0 clean test even used a **+500% clip** (10× looser than ours) and
*still* found deep-discount was a loser. So the 1.526 clipped sub-$5 PF is honest and propped up by nothing.

| region | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| sub-$5 [0,5) | 667 | 1.526 | 1.592 | 1.402 |
| ≥ $5 [5,∞) | 3,678 | 1.590 | 2.021 | 1.520 |

Sub-$5 is only **modestly** worse than ≥$5 — not the disaster the floor implied. The price BANDS show the edge is
strongly **non-monotone in price**, and the floor is cutting good names:

| price band | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| < $1 | 31 | 1.582 | 1.582 | 0.551 ← broken post-2015, thin |
| $1–2 | 121 | 1.442 | 1.460 | 1.422 |
| $2–3 | 173 | 1.296 | 1.316 | 1.452 |
| **$3–5** | 342 | **1.698** | 1.820 | **1.501** ← better than $20+ |
| **$5–10** | 677 | **2.246** | 2.316 | **2.488** ← best band in the book |
| $10–20 | 920 | 1.780 | 2.854 | 1.608 |
| $20+ | 2,081 | 1.236 | 1.491 | 1.151 ← *worst* band |

Cumulative price FLOOR — **peaks at ~$3, not $5:**

| floor | n | PF clip | clip post |
|---|--:|--:|--:|
| ≥ $0 | 4,345 | 1.575 | 1.492 |
| ≥ $1 | 4,314 | 1.575 | 1.509 |
| ≥ $2 | 4,193 | 1.581 | 1.513 |
| **≥ $3** | 4,020 | **1.604** ← peak | 1.517 |
| ≥ $5 | 3,678 | 1.590 | 1.520 |
| ≥ $10 | 3,001 | 1.417 | 1.297 |

**Findings:**
1. **The edge is low-priced-but-not-penny.** `$5–10` is the single best band in the entire system (clip 2.246,
   **post-2015 2.488**); `$3–5` (clip 1.698, post 1.501) is *better than the `$20+` band the system currently
   trades* (clip 1.236, post 1.151). High-priced large-caps are the WORST band — same "energetic middle beats the
   safe extreme" theme as tightness/ATR%.
2. **The $5 floor is slightly too high.** The cumulative floor peaks at **≥$3** (clip 1.604 / post 1.517), which
   recovers the strong `$3–5` band while still excluding the weak `$1–3` zone and the broken-post-2015 penny band
   (`<$1` post 0.551). Below $3 the $2–3 + penny bands drag PF down.
3. **The aggregate gain is small but it is FREE trips in a GOOD band** (≥$3 vs ≥$5: clip 1.590 → 1.604, post 1.520
   → 1.517 ≈ flat, +342 trips in the 1.70-PF $3–5 band). Not a PF play — a capacity play that doesn't cost quality.

**✅ DECISION (2026-06-21): drop `MinPrice` 5 → 1** (not 3). The cumulative-floor *optimum* is $3, but we
deliberately keep the weak `$1–3` zone IN rather than floor at $3, because those low-priced names are the prime
candidates to be **rescued by the past-runner max-ADR% / max-slope FLOORS** (deferred, not yet in engine — see
below). A flat $3 floor would throw them away before that filter gets a chance; a $1 floor keeps them while still
excluding only the broken-post-2015 sub-$1 penny band (clip_post 0.551). Engine-verified end-to-end at $1:

| filter | trips | PF raw | PF clip | net P&L |
|---|--:|--:|--:|--:|
| breadth, ≥2005 ($5 floor, prior) | 3,678 | 2.021 | 1.590 | +$915k |
| **breadth, ≥2005 ($1 floor, NEW)** | **4,314** | 1.923 | **1.575** | **+$1.07M** |
| breadth + heat<0.25 ($1 floor) | 3,195 | 2.164 | 1.662 | +$879k |

Adding `$1–5` is **+636 breadth-filtered trips (+17%) / +$156k** at **flat clip PF** (1.590 → 1.575 — the honest
measure barely moves; raw dips 2.021 → 1.923 only because the `$1–3` names are weaker, exactly the band the
past-runner floor should lift). Unfiltered engine run: 6,749 trips / PF 1.820 / +$1.55M. `MinPrice = 1.0` in
`defaultConfig`.

### Entry-day candle BODY shape — the "fat green candle" hypothesis is WRONG; it's a middle-body hump (2026-06-21)

From the gap-over < reclaim finding, the hypothesis was: a **fat green body** (opens low, closes high — conviction
built through the day) should beat a **doji / top-heavy** candle. Tested on the production trips by joining each
entry day's adjusted OHLC and computing body shape. Script:
[`scripts/equity/candle_body_breakdown.sql`](../scripts/equity/candle_body_breakdown.sql). Clip +50%, breadth, ≥2005.

**Body fraction `(close−open)/range` — the FATTEST green bodies are the WORST (after red):**

| body band | n | PF clip | clip post |
|---|--:|--:|--:|
| < 0 (red close) | 518 | 1.345 | 1.077 |
| 0.0–0.2 (doji-ish) | 249 | **1.888** | **2.514** |
| 0.2–0.4 | 420 | 1.629 | 1.588 |
| 0.4–0.6 | 752 | 1.837 | 1.714 |
| 0.6–0.8 | 1,168 | 1.602 | 1.491 |
| **0.8–1.0 (fat green)** | 1,207 | **1.426** | **1.405** ← worst non-red |

The hypothesis is **inverted**: the fattest green candle (body 0.8–1.0) is the *worst* non-red band, *below*
baseline (1.575). The best are the **doji-ish and mid-body** bands. A mild "don't close red" floor (`body ≥ 0`)
helps (clip 1.603 / post 1.572), but tightening to `body ≥ 0.8` *hurts* (1.426).

**Open position `(open−low)/range` — the cleanest signal, and it CONFIRMS the gap-over intuition:**

| open position | n | PF clip | clip post |
|---|--:|--:|--:|
| 0.0–0.2 (opened at LOW) | 3,001 | 1.538 | 1.488 |
| **0.2–0.4** | 604 | **1.895** | **1.805** |
| 0.4–0.6 | 313 | 1.619 | 1.694 |
| 0.6–0.8 | 192 | 1.794 | 1.380 |
| **0.8–1.0 (opened at HIGH)** | 204 | **1.047** | **0.899** ← a loser |

**Opening at the high is a near-loser (clip 1.047, post-2015 0.899)** — exactly the gap-over story: a stock that
opens at its high already did the move premarket, nothing left. The sweet spot is opening **lower-middle (0.2–0.4)**.

**The key test — among CLOSE-HIGH trades, does opening low (fat body) beat opening high (doji)?**

| group (close_pos ≥ 0.8) | n | PF clip | clip post |
|---|--:|--:|--:|
| A: opened LOW (full fat body) | 1,569 | 1.463 | 1.420 |
| **B: opened MID** | 310 | **1.977** | **1.968** |
| C: opened HIGH (doji-at-top) | 90 | 1.489 | 1.880 |

**It's a hump, not a ramp — the MIDDLE body wins (B: clip 1.977 / post 1.968).** Neither extreme is best: the
full fat body (open-at-low → close-at-high, body_frac ≈ 1) is a **climactic full-range bar — over-extended and
exhausted**, the *worst* of the three; the doji-at-top is thin/noisy. The winner is a **controlled intraday
reclaim**: opened slightly down, closed near the high, moderate body.

**Conclusion — same lesson as everywhere this session: MODERATE energy beats EXTREME energy.** The fattest candle
is the over-extended one, just like the fattest move (30%+ blow-off) and the highest rvol (>15 pump). The gap-over
instinct was right (opening at the high is genuinely bad), but the "fat body = better" framing had the curve
backwards. **Not adopted as a filter** (the effect is real but the bands are smaller than the core filters, and a
"middle body" gate is fiddly to specify cleanly) — documented as a strong characterization of *what a good
breakout day looks like*: opens slightly down, closes near the high, with a controlled rather than vertical body.

#### The RED-CLOSE band drilled — it's "gap up huge then fade," and the damage is ALL in the 20%+ gap (2026-06-21)

The body band `< 0` is the oddest cell: trades that close **up ≥ 10% on the day** yet print a **RED candle**
(close < open). The only way both hold is a stock that **gapped up huge at the open and then sold off into the
close** — it finished green purely on the size of the gap. Drill
([`scripts/equity/candle_redbody_detail.sql`](../scripts/equity/candle_redbody_detail.sql)):

| | n | avg overnight gap | open→close | open_pos | PF clip | clip post |
|---|--:|--:|--:|--:|--:|--:|
| GREEN (body ≥ 0) | 3,796 | +6.5% | +9.9% | 0.12 (opens low) | 1.603 | 1.572 |
| **RED (gap-up then fade)** | 518 | **+21.5%** | −3.5% | 0.68 (opens high) | 1.345 | **1.077** |

The red band gaps **+21.5% overnight** (vs +6.5% green), opens near the top of its range (open_pos 0.68 vs 0.12),
and gives back ~3.5% intraday — the most extreme gap-over there is (the whole move happened pre-open; live you
can't capture it). **But it is NOT uniformly bad — the damage is entirely in the 20%+ gap subset:**

| red band × overnight gap | n | PF clip |
|---|--:|--:|
| gap 10–20% | 270 | **1.889** ← fine |
| gap 20%+ | 248 | **1.014** ← the loser |

A red candle on a *moderate* (10–20%) gap is fine (1.889); the loser is the **20%+ gap** (1.014, ≈ breakeven), and
the deepest intraday fades are worst of all (open→close < −15%: clip 0.516, n 14). The most extreme examples are
+40–175% overnight pops on speculative micro-caps (JG +176%, ZKIN +76%, DOGZ, MICT, TTNP) that opened at the top
and bled all day — unrepeatable premarket squeezes. **So the red band is not a new bad signal — it's the candle-shape
silhouette of the same 30%+ over-extension cohort we already cap with the day-move ceiling.** Same theme, fourth axis.

#### Intraday-return floor `close/open − 1 ≥ N` — swept; peaks at N=0, but NOT adopted (2026-06-21)

Tested the cleanest possible version of the finding: a single floor that rejects the trade unless `close/open − 1 ≥ N`
([`scripts/equity/intraday_ret_floor_sweep.sql`](../scripts/equity/intraday_ret_floor_sweep.sql)). The floor peaks
**exactly at N = 0 ("no red candle")** and over-cuts for any positive N:

| floor N | n | PF clip | clip post | | excluded tail | n | PF clip of CUT |
|---|--:|--:|--:|---|---|--:|--:|
| −∞ (all) | 4,314 | 1.575 | 1.509 | | — | — | — |
| −0.05 | 4,192 | 1.590 | 1.541 | | cut < 0.00 | 518 | **1.345** ← bad, drop |
| **0.00 (no red)** | 3,796 | **1.603** | **1.572** | | cut < 0.02 | 910 | 1.496 |
| 0.02 | 3,404 | 1.590 | 1.541 | | cut < 0.05 | 1,461 | **1.612** ← *above* baseline! |
| 0.05 | 2,853 | 1.562 | 1.490 | |  |  |  |
| 0.10 | 1,777 | 1.541 | 1.470 | |  |  |  |

Clip PF rises monotonically to N=0 then falls — N=0 is the optimum on both eras (post-2015 1.509 → 1.572). The
excluded-tail table shows *why* 0 is the line: the `close/open < 0` reds are genuinely bad (clip 1.345, worth
dropping), but pushing past 0 starts cutting trades that are **at/above baseline** (the `< 0.05` cut population is
1.612 > the 1.575 baseline) — a positive floor throws away good trades.

**Deep-fade-only variant — cut ONLY the worst reds, keep the mild-red band (2026-06-21).** A follow-up: rather than
the full no-red rule, keep everything *except* deep intraday fades. The negative region is strongly graded — the
damage is in the deep fades, the mild reds are near-baseline:

| intraday_ret band | n | PF clip | clip post |  | keep ≥ N (deep-fade cut) | trips | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < −20% | 8 | 0.736 | 0.736 | | all | 4,314 | 1.575 | 1.509 |
| −20..−15% | 6 | 0.295 | 0.383 | | keep ≥ −0.10 | 4,287 | 1.587 | 1.524 |
| −15..−10% | 13 | 1.187 | 1.218 | | **keep ≥ −0.07** | 4,245 | **1.600** | 1.541 |
| −10..−5% | 95 | 1.484 | 1.038 | | keep ≥ −0.05 | 4,192 | 1.590 | 1.541 |
| −5..0% (mild red) | 396 | 1.420 | 1.179 | | keep ≥ 0.00 | 3,796 | 1.603 | 1.572 |
| ≥ 0 (green) | 3,796 | 1.603 | 1.572 |  |  |  |  |  |

The deep-fade tail is genuinely toxic (the cut population `< −10%` is clip ~0.72, `< −15%` is 0.52) — but it is
**tiny: only ~27 trades over 21 years below −10%, 69 below −7%.** So **`N = −0.07` is the capacity-efficient sweet
spot**: it captures most of the no-red rule's PF gain (clip 1.575 → **1.600**, post 1.509 → 1.541) for a cost of just
**69 trips (1.6%)** vs the full rule's 518 — because the mild −5..0% band it keeps is near-baseline (1.42), not worth
dropping.

**✅ DECISION (2026-06-21): ADOPTED at `N = −0.07` (deep-fade-only).** Wired into the engine as a new entry gate
`close/open − 1 ≥ −0.07` (`MinIntradayRet` in `defaultConfig.Entry`; CLI `--min-intraday-ret`). It targets *only* the
toxic deep-fade tail of the red-candle band while keeping the near-baseline mild reds — the capacity-efficient choice
over the full no-red rule (−69 trips vs −518 for nearly the same PF). **Engine-verified end-to-end** (matches the
post-hoc projection exactly): breadth-filtered **4,314 → 4,245 trips, clip PF 1.575 → 1.600, post-2015 1.509 → 1.541,
P&L +$1.07M → +$1.08M** (raw engine PF 1.820 → 1.851). With heat<0.25: clip 1.662 → **1.678**, post 1.572 → **1.648**.
The earlier overlap concern (the 30% move cap already catches much of this cohort) is real but the deep-fade tail is
the residual it *doesn't* catch — and it's nearly free (1.6% of trips), so it earns its place. `N = 0` (full no-red)
remains available via the flag for a slightly higher PF at a much larger capacity cost.

#### Full intraday-return breakdown — the hump strikes again: 0–5% is the SWEET SPOT (2026-06-21)

With the deep-fade floor live, mapped the *whole* intraday-return range to see what else it carries. Script:
[`scripts/equity/intraday_ret_breakdown.sql`](../scripts/equity/intraday_ret_breakdown.sql). Clip +50%, breadth, ≥2005.

| intraday_ret band | n | PF clip | clip post | | quintile (data-driven) | range | PF clip | clip post |
|---|--:|--:|--:|---|---|---|--:|--:|
| < −7% (cut by gate) | 69 | 0.791 | 0.766 | | Q1 | −55%..+1.8% | 1.445 | 1.264 |
| −7..0% (mild red, kept) | 449 | 1.566 | 1.226 | | **Q2** | +1.8%..+6.3% | **1.912** | **2.092** |
| **0–2%** | 392 | **1.827** | **2.186** | | Q3 | +6.3%..+10.2% | 1.437 | 1.352 |
| **2–5%** | 551 | **1.810** | 1.986 | | Q4 | +10.2%..+14.2% | 1.650 | 1.568 |
| 5–10% | 1,076 | 1.610 | 1.546 | | Q5 | +14.3%..+34.5% | 1.511 | 1.451 |
| 10–15% | 1,029 | 1.561 | 1.512 | |  |  |  |  |
| 15–25% | 677 | 1.601 | 1.437 | |  |  |  |  |
| 25%+ | 71 | 1.043 | 1.401 | |  |  |  |  |

**Findings:**
1. **The best intraday band is 0–5% (clip ~1.8, post-2015 ~2.0–2.2) — a LOW push, not a big one.** A stock that
   closes +10% on the *day* but only moved **0–5% from its open** got most of its gain **overnight (the gap)** and then
   **drifted up calmly** intraday — a controlled, non-vertical session. The quintiles agree: **Q2 (+1.8–6.3% intraday)
   is the standout at clip 1.912 / post 2.092.**
2. **The 25%+ intraday band rolls over (1.043)** — a 25%+ open→close climb is the *vertical intraday blow-off*, the
   worst green band. **Same hump as every axis this session:** moderate beats both extremes (deep fade AND vertical push).
3. **Intraday return is a GENUINELY NEW signal, not a move% proxy** — corr with the day-move (pct_up) is only **0.29**
   (and −0.14 with the overnight gap). It measures *where in the session* the move happened, which the move% gate doesn't.
4. **A green push with NO overnight gap is excellent** (gap <2%: clip 1.839 / post 1.732) — the move is happening
   cleanly *during* tradeable hours. (Most production trips DO gap, by construction of the ≥10% day-move + rvol filters.)

**No new gate taken** — the 0–5% peak is a soft preference, not a cliff, and the existing gate already removes the bad
tail; a 25%+ *intraday* cap would only catch 71 trades and overlaps the 30% day-move cap. Documented as another face of
the **moderate-energy-beats-extreme** theme: the ideal entry day gaps up, then drifts calmly higher — it does **not**
go vertical intraday.

### Path dependency — day-1 direction is MECHANICAL, not predictive; no early-exit rule (2026-06-21)

Entry = close of day 0; hold days 1–5; exit at the **open of the 6th trading day** (`MaxHoldBars = 5`). Asked whether
day 1 (first held bar) telegraphs the trade. The raw split is enormous — and entirely a trap:

| day 1 | n | full-trade PF clip | clip post |
|---|--:|--:|--:|
| **UP** | 2,160 | **5.739** | 6.061 |
| **DOWN** | 2,033 | **0.460** | 0.390 |

Day-1-up trades show PF 5.7, day-1-down are a money-loser at 0.46, perfectly monotone across bands (day1 < −10% →
0.075; day1 10%+ → 29.4). **But this is almost entirely MECHANICAL, not predictive:** day-1 return is a *component*
of the 5-day trade return — an up day-1 has already booked part of the win, a down day-1 has dug a hole. The actionable
question is whether day-1 weakness predicts the **rest** of the trade going badly (which would justify an early exit).
It does **not** — the **forward** return (day-1 close → exit) is the same regardless of day-1 direction:

| day 1 | n | FORWARD PF clip | forward mean | forward median |
|---|--:|--:|--:|--:|
| UP | 2,160 | 1.428 | +2.25% | +0.03% |
| DOWN | 2,033 | **1.474** | +1.13% | **+0.25%** |

**Day-1-down trades have the SAME forward edge as day-1-up (PF 1.47 vs 1.43), and a *higher* forward median.** Graded
forward bands are flat-to-noisy (even day1 < −10% is forward PF 1.175; the −10..−5% band is the *best* at 1.674) — no
"weakness begets weakness" continuation. **Conclusion: NO early-exit-on-day-1-weakness rule.** It feels right ("cut the
losers early") but day-1-down trades have positive forward EV (mean +1.1%, median +0.25%) — exiting on day 1 would
realize the dip and forfeit the bounce. Same lesson as the v2 tight-stop whipsaw: these breakouts need room to shake
out; a quick stop on early weakness destroys the edge. The 5-day time-stop (hold through) stays.

**Extended to 2- and 3-day STREAKS (2026-06-21)** — same mechanical trap, even more extreme, and a NEW twist on the
up side. Script: [`scripts/equity/streak_path_breakdown.sql`](../scripts/equity/streak_path_breakdown.sql). A "down
day" = close below the prior day's close; streaks measured from entry.

| streak | n | full-trade PF (mechanical) | FORWARD PF (streak-end → exit) | fwd mean | fwd median |
|---|--:|--:|--:|--:|--:|
| DOWN-DOWN | 991 | 0.151 | **1.393** | +0.75% | +0.13% |
| UP-UP | 1,038 | 21.065 | 1.217 | +2.67% | 0.00% |
| DOWN-DOWN-DOWN | 472 | 0.035 | **1.281** | +0.44% | 0.00% |
| UP-UP-UP | 480 | 107.9 | **1.026** | +4.41% | −0.05% |

The full-trade PF spread is now absurd (UP-UP-UP = 108, DOWN-DOWN-DOWN = 0.035) — by day 3 almost the entire trade is
already booked; pure mechanics. The **forward** PF is the real signal and it splits the two sides:
- **Down streaks KEEP their forward edge** — DD forward PF 1.393, DDD 1.281, both solidly > 1 with positive forward
  mean/median. A stock down 2–3 days straight after entry still has positive expected return over the rest of the hold.
  No capitulation. This **re-confirms: do NOT cut on weakness**, even a multi-day run of it.
- **Up streaks DECAY toward no edge** — UU forward PF 1.217, and **UUU drops to 1.026 with a *negative* forward median
  (−0.05%).** After a 3-day winning streak the forward edge is nearly exhausted: the move is front-loaded and the
  remaining hold is ~a coin flip.

**The actionable asymmetry is the OPPOSITE of intuition.** The reflex ("cut down-streaks early") is wrong — down
streaks mean-revert. If anything, the signal is on the UP side: **a 3-day up streak is where forward edge dies**, hinting
at an early *profit*-take (sell into a 3-day run rather than holding to day 6). ⏳ *Open: test an early-profit exit on
the 3-day up streak — but it's modest (PF 1.026 ≠ a loss, just no edge), so it would free capital, not avoid losses.*

**4-day streaks complete the progression (2026-06-21)** — the trend is monotone and the up-side finally crosses below 1:

| streak | n | full-trade PF | FORWARD PF (streak-end → exit) | fwd mean | fwd median |
|---|--:|--:|--:|--:|--:|
| DOWN ×4 | 220 | 0.001 | **1.212** | +0.26% | +0.15% |
| UP ×4 | 229 | 343.9 | **0.916** | **−0.14%** | **−0.04%** |

Forward PF down the streak ladder: **up** = 1.217 (×2) → 1.026 (×3) → **0.916 (×4)** — a clean monotone decay that goes
*negative* by 4 days (the breakout has fully spent its move; the last held day is a slight drag). **Down** = 1.393 → 1.281
→ **1.212** — still solidly positive after 4 straight down days; the mean-reversion never breaks. **Caveats keep it
non-actionable:** the samples are tiny (~220 each, ~10/yr), the forward window at a 4-day streak is only ~1 day (day 5 →
day-6 open, so "UP×4 negative" just means *the last day of a 4-day winner gives a hair back*), and the eras disagree
(post-2015 DOWN×4 0.898 / UP×4 0.982 — both ≈ noise). So the **shape is real and consistent** but the 4-day effect is a
micro-drag, not a regime — it reinforces the day-3 up-streak-exhaustion read rather than adding a separate signal.

### Dead-zone reclaim vs gap-over, revisited through intraday return — reproduced, and only PARTLY a proxy (2026-06-21)

The v2 dead-zone refinement was *"require an intraday reclaim of the prior 52w high, not a gap over it"* — in the
0–10%-above-the-high dead zone, names that **opened below** the prior 52w intraday high and **closed above** it
(reclaim) beat names that **gapped over** it pre-open: PF 1.43 vs 1.09, ~2,244 trips, reclaim carrying nearly all the
P&L. With the intraday-return (`close/open − 1`) signal now established (§ *Full intraday-return breakdown*), the
question was whether that geometry was ever real or just a proxy for the intraday push.

**First, the reproduction — which initially failed and is the key lesson.** The original split ONLY reproduces on the
population it was actually run on: **"pure gainers" (`--up-threshold 0`, NO move floor)** on the v2-era defaults
(rvol [6,20], tight < 4.0, ATR% < 0.11, price ≥ $5) + breadth. The small movers the up = 0 floor admits are most of the
2,244 trips and are *where the reclaim edge lives*. Restricting to the v3 production move band [10,30]% (or even
[5,10]%) carves a different, reclaim-heavy population on which the split **inverts** — so an earlier write-up of this
section that used the move-restricted tiers was reproducing the wrong population and is **superseded by this one**.
Repro script: [`scripts/equity/deadzone_repro_v2era.sql`](../scripts/equity/deadzone_repro_v2era.sql) (regen CSV with
`--up-threshold 0 --rvol-min 6 --rvol-max 20 --min-price 5 --max-tightness 4.0 --max-atr-pct 0.11`). Raw PF (no clip),
breadth lag-1 > 0.5, ≥ 2005. The v3 5-day exit shifts the PF *levels* vs the original 20-day-era numbers, but the
direction and ~2,256-trip count reproduce:

| dead-zone split (up=0 pure gainers, v2-era filters, 5d stop) | n | win% | PF raw | net | pre-2015 | post-2015 |
|---|---|---|---|---|---|---|
| **reclaim (open < high → close > high)** | 1408 | 51.3 | **1.295** | +$97.5k | 1.426 | 1.207 |
| gap-over (open ≥ high) | 848 | 49.8 | 1.172 | +$27.8k | 1.062 | 1.298 |

Reclaim wins (1.295 vs 1.172) and carries ~78% of the dead-zone P&L — the original finding holds on its own population.

**The 20-day time-stop (the v2-era exit) does NOT recover the exact original 1.43/1.09.** Re-running with
`--max-hold-bars 20`: reclaim 1.303 / gap-over 1.348 raw PF (gap-over actually edges *ahead* on raw PF), but reclaim
still carries **~2× the P&L** ($189.8k vs $91.5k on 1395/835 trips) and still wins the early era decisively (pre-2015
**1.68** vs 1.22). So the time-stop is not the lever; the residual gap to the original 1.09 is most likely the v2-era
**entry-day stop** geometry (a gap-over's day-low stop sat tighter, penalizing it — the original doc flagged this as a
"second-order effect" worth ~0.07 PF) plus minor 52w-window drift. The **direction and P&L-dominance reproduce under
both exits**; the precise 1.09 was specific to a now-superseded stop config and isn't worth chasing.

**Is it just the intraday signal? Partly — but NOT fully.** A reclaim is **intraday-up by construction** (open below the
high, close above it ⇒ close > open): the cross-tab leaks nothing, **100% of reclaims are intraday-up** (1408/1408),
while gap-overs are mixed (557 up / 291 down). So the two variables are mechanically entangled. The controlling test —
hold the intraday sign fixed and compare:

| intraday sign | split | n | PF raw | net |
|---|---|---|---|---|
| intraday UP | reclaim | 1408 | **1.295** | +$97.5k |
| intraday UP | gap-over | 557 | 1.171 | +$18.7k |
| intraday DOWN/flat | gap-over | 291 | 1.174 | +$9.1k |

Among **intraday-up** names, reclaim (1.295) still beats gap-over-up (1.171) by **+0.12 PF** — a *residual* edge that
does **not** vanish when you control for intraday return. So the reclaim rule is **not merely** the intraday signal in
disguise: opening below resistance and reclaiming it intraday is genuinely better than gapping through it, even at the
same intraday-up sign. (Note too that for a **gap-over**, the intraday sign barely matters — 1.171 up vs 1.174 down —
so intraday return is informative for reclaim-type entries but not gap-over-type ones.) The push-size band on this
population is **rising-then-flat**, not the hump seen on the strong v3 tier: `−2..0%` 1.22 → `0..5%` 1.24 → `5..10%`
**1.33** → `10%+` 1.25.

**Verdict:** the original reclaim > gap-over result is **real and reproduced** (on up=0 pure gainers). Intraday return
is **correlated with but does not fully explain** it — reclaim keeps a ~+0.12 PF residual after controlling for the
intraday sign. So reclaim and intraday-return are two related-but-distinct reads of the same entry bar; the reclaim
geometry is the stronger one in the dead zone, and intraday return is the more general signal across the rest of the
system. **Both are worth carrying into intraday entries.** Net effect on production: none — the engine never wired in the
reclaim rule (it stayed a candidate); this corrects the record and keeps both signals on the table.

> ⚠️ **Methodology lesson:** always reproduce a prior finding **on its original population** before re-interpreting it.
> The reclaim result was run on `--up-threshold 0` pure gainers; reading it through the v3 production move band silently
> swapped the population and flipped the sign. Match the filters first, *then* test the new hypothesis.

**Lower-tier aside [5,10]% / rvol ≥ 3 — intraday return INVERTS vs the production tier.** On weak breakouts (PF 1.21
overall, vs the production tier's 1.60) an intraday **fade is the GOOD version** — intraday-down 1.455 vs intraday-up
1.193, decaying monotonically as the push grows (`−2..0%` 1.62 → `5%+` 1.13 / post-2015 1.06). So the production −0.07
deep-fade reject gate is a **strong-tier phenomenon that does not generalize down**: on a small 5–10% mover, a name that
opened roughly flat and *ground out* its close is live; one that gapped and *drifted* is exhausted. Script:
[`scripts/equity/deadzone_intraday_explains.sql`](../scripts/equity/deadzone_intraday_explains.sql).

### Distance from the 52w MAX CLOSE on the [5,10]% / rvol > 3 tier — spike at the fresh high, decay when extended (2026-06-21)

On the lower-tier system (move ∈ [5,10]%, rvol > 3) with **full production settings intact incl. heat + breadth**, how
does PF depend on `pct_52w_at_entry` = close / 52w-max-close − 1 (the close-vs-close-high reference)? Negative = still
below the prior max close; ~0 = fresh new closing high; positive = extended above it. Script:
[`scripts/equity/dist_52w_close_510_rvol3.sql`](../scripts/equity/dist_52w_close_510_rvol3.sql). Baseline 7,069 trips,
PF clip **1.217** / post-2015 1.161. Clip +50%, breadth lag-1 > 0.5, heat (CS/ADRC) h10 < 0.25, ≥ 2005.

| distance from 52w max close | n | PF clip | post-2015 |
|---|---|---|---|
| < −3% (below max close) | 760 | 1.353 | 0.852 |
| −3..−1% | 755 | 1.328 | 1.713 |
| −1..0% (just under) | 417 | 1.158 | 0.889 |
| **0..1% (fresh close-high)** | 487 | **1.735** | **1.909** |
| 1..3% | 1003 | 1.359 | 1.171 |
| 3..5% | 1269 | 1.133 | 1.125 |
| **5%+ (extended)** | 2378 | **1.065** | 1.066 |

**A sharp spike right at the fresh closing high (0–1%, PF 1.735 / post-2015 1.909) then monotone decay the further the
name extends above it.** The `5%+` bucket is the biggest (2,378 trips, a third of the band) and is **dead** at 1.065 —
the same moderate>extreme shape as every other axis, here on the 52w-close distance: the best entry is *exactly at* the
breakout to a new closing high; chasing it once it's run 5%+ past the high is no edge. The deep-below-high `<−3%` bucket
looks decent (1.353) but is **era-fragile** (post-2015 0.852) — a recovering-laggard artifact, not the signal; the
fresh-high spike is the era-robust one.

**Cumulative — the CEILING is the refinement, the floor isn't.** Requiring *more* extension (`d52 ≥ N`) flat-to-hurts
(d52 ≥ 0.03 → 1.086). Requiring *less* (`d52 < N`) concentrates the edge: **d52 < 0.05 → 1.305** (keeps 4,691 of 7,069),
**d52 < 0.03 → 1.368**, d52 < 0.01 → 1.372. So capping the extension — *not pinning a floor above the high* — lifts the
lower tier from 1.217 to ~1.31–1.37 by dropping the dead extended bucket. **Takeaway for the lower tier: buy the fresh
close-high, cap the extension at ≤ ~3–5% above the prior max close.** (Diagnostic for now — the lower tier is not in
production; logged for when intraday entries pull in weaker breakouts.)

**Can the reclaim/gap-over split rescue the dead extended (`d52 ≥ 3%`) bucket? No — reclaim only works in the breakout
zone, not the extended zone.** Splitting `d52 ≥ 3%` (above the max *close*) into reclaim vs gap-over (vs the *intraday*
high, the v2 definition):

| extended dead zone (d52 ≥ 3%) | n | PF clip | post-2015 |
|---|---|---|---|
| reclaim (open < intraday-high) | 2276 | 1.104 | 1.058 |
| gap-over (open ≥ high) | 1371 | 1.049 | 1.148 |
| └ `3..5%` gap-over | 328 | 1.211 | **1.886** |
| └ `3..5%` reclaim | 941 | 1.116 | 1.004 |
| └ `5%+` gap-over | 1043 | 1.013 | 1.021 |
| └ `5%+` reclaim | 1335 | 1.097 | 1.090 |

The reclaim edge is **gone** here: reclaim 1.104 vs gap-over 1.049 (a trivial 0.06, and it *reverses* post-2015), and in
`3..5%` the **gap-over is the better half** post-2015 (1.886 vs 1.004). This is the crucial contrast with the original v2
dead zone: that edge was in the **0–10%-above-the-52w-INTRADAY-HIGH** zone (names right *at the breakout level*), whereas
`d52 ≥ 3% above the max CLOSE` is the **already-extended/run** territory. Once a name is well past its breakout, opening
below vs above the intraday high no longer matters — it's extended and dead either way. **So the reclaim signal is a
property of the breakout zone, not the extended zone; the only tool for the `d52 ≥ 3%` bucket is the extension ceiling
(cap d52), not a reclaim filter.** Script:
[`scripts/equity/dist_52w_close_510_rvol3.sql`](../scripts/equity/dist_52w_close_510_rvol3.sql).

**Does intraday return rescue it instead? No — same dead result, even flatter.** Splitting the same `d52 ≥ 3%` bucket by
intraday return: by sign, intraday-up 1.087 vs intraday-down 1.028 (n=3376/262) — no edge; by band, **non-monotone and
hovering ~1.0** (`0..2%` 1.39 is the one bright cell on n=369, but `2..5%` is **0.985** on n=984, and `5%+` 1.103 on
n=2032) — scatter, not a gradient. So **neither microstructure lever (reclaim geometry NOR intraday push) discriminates
in the extended zone** — both are *breakout-zone* signals that work only at the fresh high (the 0–1% close-distance cell,
where intraday push and reclaim both mattered) and go silent once a name has run 3%+ past its max close. **Confirmed: you
can't rescue over-extended names — you can only avoid them (cap `d52`).**

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
  era-robust, gate-amplified. "Higher is better" → use a FLOOR. *(v2: "Past-runner personality".)* **Now also the
  designated rescue for the weak `$1–3` price band** admitted by the 2026-06-21 floor drop (5 → 1): the hypothesis
  is that a low-priced name that has *recently been a runner* (high max-ADR%/slope) carries the breakout edge that
  a generic $1–3 name lacks. First thing to test when this floor goes into the engine.

### Regime filters (post-hoc, not yet in engine)

- **HEAT** (Sykes-inspired, ⭐) — daily mean return of the **top 1% gainers** (**CS/ADRC** + 30-cal-day ADV ≥ $1M
  universe; **+1000% per-stock return clip is LOAD-BEARING**), trailing-10d lagged = `h10`. **Skip entries when
  h10 ≥ 0.25** (80th pctile). Froth is bad for breakouts. *(Heat universe unified to CS/ADRC on 2026-06-21 — see
  next subsection; under the clip the gate gives clip-PF 1.702 / post-2015 1.686 at h10<0.25 vs 1.590/1.520 ungated.)*
- **BREADTH** — `pct_above_20` on CS/ADRC + 30-cal-day ADV ≥ $1M (reproduces the production `breadth.parquet`).
  PF rises with breadth to a peak at ≥0.70, then rolls over; current gate 0.5.
- **COLD** (bottom-1% losers) — same axis as heat (corr −0.65), redundant; not wired.
- Both built by **`scripts/equity/build_breadth_and_heat.sql`**.

#### Heat universe unified to CS/ADRC — drops non-frothy CEFs/preferreds, gates STRONGER, no blocklist (2026-06-21)

Originally the heat universe read the **whole liquid tape** (any ticker ≥ $1M ADV) + a hardcoded `is_test_ticker`
blocklist, on the rationale that top-gainer froth lives in names that lack a clean CS/ADRC ref row. **That
rationale was wrong** (caught by inspection): a recent IPO *is* CS and a foreign listing *is* ADRC — both get ref
rows. The ~16,400 ref-less tickers in the price table break down as ~5,600 genuine non-equity (warrants `…W`,
units `…U`, rights `…R`, dotted class/`.WS`) and the rest mostly **closed-end funds** (MHD, NCV, NRK, DHY, …) and
**preferreds** (lowercase-`p` tickers). CEFs and preferreds **structurally cannot be top-1% gainers** — a muni CEF
doesn't pop 50% — so including them only **dilutes** the froth mean.

Empirically the two universes are **0.815-correlated** but materially different (mean h10 0.172 whole-tape vs
0.179 CS/ADRC — the CS/ADRC version runs *hotter*, confirming the dilution). And the CS/ADRC heat **gates
strictly better** under the clip (production trips, breadth on, ≥2005; script
[`scripts/equity/heat_csadrc_gate_sweep.sql`](../scripts/equity/heat_csadrc_gate_sweep.sql)):

| gate | n | PF clip | clip post | (old whole-tape clip post) |
|---|--:|--:|--:|--:|
| no heat gate | 3,678 | 1.590 | 1.520 | — |
| h10 < 0.20 | 2,476 | 1.778 | **1.819** | 1.722 |
| h10 < 0.25 | 2,772 | 1.702 | **1.686** | 1.620 |
| h10 < 0.30 | 3,051 | 1.689 | 1.666 | 1.527 |

**✅ DECISION (2026-06-21): heat is now built on the CS/ADRC inner join** (unified with breadth + the engine).
The new 80th pctile of h10 is **0.251 ≈ the old 0.25**, so the **gate threshold is unchanged**; it just gates a
cleaner series (post-2015 1.686 vs 1.620 at h10<0.25). `build_breadth_and_heat.sql` heat block switched to
`JOIN ticker_reference ... WHERE type IN ('CS','ADRC')`. The `is_test_ticker` blocklist is now **removed entirely**
(2026-06-21): with the CS/ADRC filter on BOTH universes it never fired — *proven* no-op, 0 test-ticker rows survive
CS/ADRC + $1M ADV on breadth — and a filter that can't fire only misleads a reader into thinking test tickers are a
live threat. The **+1000% return clip stays** (still needed for residual real-CS/ADRC glitches like LU/EPIX).

- **TEST-TICKER mechanics (verified 2026-06-21).** NASDAQ test symbols (ZXZZT etc.) have corrupt 0.0001→$200k
  prices but **NO `ticker_reference` row at all** (an earlier note wrongly called them "tagged CS" — corrected).
  Every CS/ADRC **inner join** therefore drops them for free: the engine (`Backtest.fs`), breadth, and now heat.
  Confirmed 0 test-ticker trips in every dump this session (the 3 `ZY*` names — ZYBT/ZYME/ZYXI — are *real* CS).
  Do **not** remove the engine's CS/ADRC inner join thinking a blocklist covers it — no blocklist runs in the engine.

---

## Reproduction

```bash
# v3 default (2026-06-21): NO price stop, 5-day time-stop, next-open exit; entry filters
# ATR% < 0.10 (log), tightness < 4.5 (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap),
# price >= $1, ADV >= $100k, 52w-close >= 0.95, intraday close/open-1 >= -0.07 (reject deep fades).
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
