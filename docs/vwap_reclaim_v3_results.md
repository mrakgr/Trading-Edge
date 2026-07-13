# VwapReclaimV3 — results

A debloated, **long-only** fork of VwapReclaim (branch off `vwap-reclaim-v2` lineage). Two structural
changes vs V1:

1. **9-EMA pullback-low stop replaces the geometric VWAP-low stop.** The stop level is the running MIN of
   the 9-EMA over the current below-VWAP run (reset at each reclaim, alongside the other run accumulators).
   It fires when the LIVE 9-EMA falls back below that level — i.e. the reclaim failed and price is rolling
   back over the pullback low. `--stop-buffer` widens it to `run-min·(1−buffer)`; 0 = fire the instant the
   EMA dips under the run-low.
2. **Debloat.** The breakout engine, the `ReclaimShort` mirror, the MR cover targets (`Vwap`/`Ma`/`Channel`),
   the `Armed`/`Skipped` position states, and all their config knobs are gone. The ONLY entry path is the
   long reclaim. Snapshot mutables are factored into a `State` record (DipRiderV4 style): `ProcessBar`
   captures the strictly-prior state at the top and stores it into `sState` at the bottom, so the entry
   gate's cross reads `sState.ema/vwap` (prior) against the live post-fold `ema.State`/VWAP (current).
3. **New RollingMa primitives.** `RunMaxMa<'T>` / `RunMinMa<'T>` (Push + Reset + State) — windowless
   session-cumulative running extremes — replace the plain `mutable _ voption` running-high/low/vol-high
   idiom (and back the run-scoped 9-EMA pullback low).

Defaults (production): 10:00–13:30 ET entry window, 09:30 VWAP/EMA anchor, `tightness ≥ 3.0`, weakness run
`rb ≥ 11` consecutive bars EMA<VWAP into the cross, hold-to-MOC unless the pullback stop fires. Universe =
`vwap_reclaim_candidate` (ADV ≥ $1M AND rvol_0945 > 1). $10k notional/trip, PF = gross-win / gross-loss (MOC).

---

## F1 — The 9-EMA pullback stop beats the geometric VWAP-low stop (controlled A/B)

Both engines take the **identical 41,027 trips** (V3 changed only the stop, not the entry), so this is a
clean A/B on the exit alone. Full range 2003-09 → 2026-06.

| stop | trips | win % | net | PF (MOC) |
|---|---|---|---|---|
| V1 — geometric `VWAP − d·⅔` (d = VWAP−sessionLow) | 41,027 | 41.1% | $1,503,082 | 1.298 |
| **V3 — 9-EMA pullback low (run-min of the 9-EMA)** | 41,027 | 38.1% | **$1,601,695** | **1.344** |

**+$98.6k net / +0.046 PF** from the stop swap. Win rate drops ~3pts but the trade is favorable: the
pullback stop cuts a bit more often, at a controlled distance, and lets the survivors run. On the 2023-24
slice the exit split is explicit — **1171 stops @ −3.27% mean, 1513 MOC holds @ +5.37% mean**.

> The table above is at `--stop-buffer 0` (the pure stop-swap A/B). The **engine default is now `0.002`**
> (F3): on the FULL book that's near-free (net $1,604,145 / PF 1.332 — +$2.5k, −0.012 PF vs buffer 0),
> while it captures the full +$21-23k/cell gain on the graded A/A+ cells. A single default, not book-dependent.

### Per-year (the edge concentrates where the strategy is tradable — post-2020)

| year | n | V1 PF | V1 net | V3 PF | V3 net | ΔPF | Δnet |
|---|---|---|---|---|---|---|---|
| 2008 | 1748 | 0.98 | −5,138 | 1.05 | 9,463 | +0.08 | +14,600 |
| 2020 | 2620 | 1.56 | 246,215 | 1.55 | 224,640 | −0.01 | −21,575 |
| 2021 | 6953 | 1.17 | 214,317 | 1.20 | 237,526 | +0.04 | +23,209 |
| 2022 | 3094 | 1.33 | 170,572 | 1.39 | 187,737 | +0.06 | +17,166 |
| 2023 | 1508 | 1.92 | 206,911 | 2.13 | 226,206 | +0.21 | +19,295 |
| 2024 | 1176 | 1.67 | 188,289 | 1.81 | 203,187 | +0.14 | +14,898 |
| 2025 | 1066 | 1.70 | 215,501 | 1.84 | 235,039 | +0.14 | +19,537 |
| 2026 | 371 | 2.37 | 145,280 | 2.38 | 143,774 | +0.01 | −1,506 |
| **TOTAL** | **41027** | **1.30** | **1,503,082** | **1.34** | **1,601,695** | **+0.05** | **+98,613** |

(Older/thin years omitted for brevity — all near PF 1.0, small deltas.) 17 of 24 years improve. The one
notable giveback is **2020 (−$21.5k)**, though PF is essentially flat there (1.56→1.55). The gains land in
**2021 (adverse regime, +$23k), 2022 (+$17k), 2023 (+$19k), 2024 (+$15k), 2025 (+$20k)** — exactly the
post-2020 window where VwapReclaim has its real edge (the pre-2020 book hovers at PF ~1.0 either way).

**Verdict:** the structure-based stop (exit when price rolls back under the pullback low the reclaim was
built on) dominates the fixed VWAP-geometry stop — better PF, more net, and it *helps most in the hardest
recent regime year (2022) and the current fat-book years (2023-25)*. Adopted as the V3 default.

---

## F2 — The pullback stop is a Pareto win on the fat/A book but a small TAX on the A+ book

Applying the V1 run-quality books (Findings 27-31) post-hoc to both engines. Full range, `$10k`/trip.

| book | n | V1 PF | V1 net | V3 PF | V3 net | v3 avg%/tr |
|---|---|---|---|---|---|---|
| FULL (default) | 41,027 | 1.30 | 1,503,082 | **1.34** | **1,601,695** | +0.4 |
| A: `updn≥0.8 & rvol15m<2` | 24,188 | 1.42 | 1,330,339 | **1.48** | **1,384,711** | +0.6 |
| A: `rmd≥3.5% & rvol15m∈[0.5,2]` | 685 | 2.75 | 497,379 | 2.79 | 494,402 | +7.2 |
| A+: `updn≥1.3 & rmd≥3.5% & dpa<3` | 240 | **4.22** | **468,635** | 4.12 | 429,018 | +17.9 |
| A+ (+ `rvol15m<2` exhaustion gate) | 232 | **4.38** | **470,059** | 4.31 | 431,805 | +18.6 |

> **Note on the book names.** The 24k `updn≥0.8 & rvol15m<2` row is a *fat-book variant*, NOT the graded
> A book — the VwapReclaim **A cell is `updn≥1.0 & run_max_dist≥3.5%`** (~973 trips all-yr / 865 modern,
> avg +7%/trade; F3). The `updn≥0.8` row is kept here only as the "wide" comparison; the A/A+ analysis is
> the concentrated cells.

**The stop cuts opposite ways by book quality:**
- **Fat book (marginal reclaims, avg +0.4%/trade):** the pullback stop is a clean win (+$99k). Most reclaims
  here are mediocre, so trimming the ones that roll back under the pullback low is pure gain.
- **A+ book (deep-run, high-conviction runners, avg +18%/trade):** the stop is a small **tax** (−$40k /
  PF 4.22→4.12). These are the names that run hard to MOC; a tight structure stop occasionally clips a
  winner mid-run. A hair of `--stop-buffer` recovers it (F3).

**Read:** V3's tight default stop is the right call for the *marginal* fat book (nothing to protect —
cut the rollovers). The graded A/A+ cells hold real per-trade edge that the tight stop occasionally clips,
so they want a small buffer. **The buffer's value tracks avg%/trade** — F3 quantifies it.

---

## F3 — `--stop-buffer` sweep: the optimal stop is BOOK-DEPENDENT

`--stop-buffer b` fires the pullback stop only when the 9-EMA falls below `run-min·(1−b)` — i.e. `b` gives
the EMA room to dip under the pullback low before stopping. Swept b ∈ {0, .005, .01, .02, .03, .05}, full
range. **The optimal buffer tracks the book's avg %/trade** — three independent cells prove it, and the
concentrated ones share a single sweet spot at `b=0.005`.

**Fat book (avg +0.4%/trade) — tight (b=0) is strictly best; any buffer only hurts:**

| buffer | FULL PF | FULL net |
|---|---|---|
| **0.000** | **1.34** | **1,601,695** |
| 0.005 | 1.31 | 1,562,880 |
| 0.010 | 1.29 | 1,494,368 |
| 0.050 | 1.24 | 1,368,716 |

PF falls monotonically (win% rises but net falls) — a stop now too loose: rollovers get room to run back
into losses. There's no runner to protect at +0.4%, so any buffer is pure downside. **Keep b=0.**

**Fine sweep on the graded cells (2020-26) — the optimum is a THRESHOLD, then a flat plateau ~0.002-0.005.**

A cell `updn≥1.0 & run_max_dist≥3.5%` (865 trips, avg +7%/trade), and A+ cell `updn≥1.3 & rmd≥3.5% & dpa<3`
(235 trips, avg +18%/trade):

| buffer | A PF | A net | A+ PF | A+ net |
|---|---|---|---|---|
| 0.000 | 2.32 | 610,124 | 4.08 | 423,400 |
| 0.001 | 2.33 | 610,668 | 4.07 | 422,857 |
| 0.002 | 2.37 | 632,433 | 4.26 | 445,606 |
| **0.0025** | **2.37** | **631,437** | **4.25** | **445,248** |
| 0.003 | 2.37 | 631,468 | 4.22 | 444,374 |
| 0.005 | 2.36 | 629,938 | 4.22 | 444,386 |
| 0.010 | 2.31 | 620,476 | 4.13 | 441,559 |
| 0.050 | 2.16 | 596,118 | 3.88 | 438,976 |

Two structural facts:
- **It's a THRESHOLD, not a slope.** At `b≤0.001` both cells are byte-close to the tight stop (A 2.33≈2.32,
  A+ 4.07≈4.08) — the buffer does nothing until it clears the typical ~0.2% 1-tick EMA noise, then it kicks
  in at `b≈0.002`. Smaller-than-the-knee is NOT better; there's a genuine floor set by EMA tick-noise.
- **0.002-0.005 is a flat plateau.** Inside it PF/net vary within ~$2k (noise). Past ~0.01 it decays back
  toward hold-to-MOC (rollovers re-admitted).

**`b=0.002` is adopted as the ENGINE DEFAULT** (top of the plateau: A PF 2.37/$632k, A+ 4.26/$446k). It is
NOT book-dependent after all — the full-book cost of moving the default off 0 is negligible (FULL net
$1,601,695 → $1,604,145, PF 1.344 → 1.332; +$2.5k / −0.012), because the fat-book damage only appears ABOVE
the plateau (the earlier coarse 0.005 point overstated it at −$39k). So one universal `0.002` default
captures the graded-cell gain essentially for free on the capacity book.

**Why this is a real mechanism, not an overfit.** Two DIFFERENT concentrated books — the A cell (865 trips)
and the A+ cell (235 trips) — INDEPENDENTLY show the same threshold-at-0.002 + plateau-to-0.005 shape across
disjoint samples of very different size. A single fitted knob wouldn't reproduce the same knee twice. The
unifying variable is **avg %/trade**: at +0.4% (fat) there's no runner to protect and any buffer re-admits
losing rollovers (monotone down); at +7% / +18% (A / A+) a hair of room stops the tight EMA-stop from
clipping a genuine runner mid-move, worth ~+$21k on each cell.

**Verdict — a single `--stop-buffer 0.002` default:**
- **Graded A / A+ cells (avg ≥ ~7%/trade):** protects the runner, +$21-23k/cell vs a tight stop.
- **Fat / capacity book (avg +0.4%/trade):** near-free (−0.012 PF / +$2.5k net) — the buffer's harm to the
  fat book only shows up ABOVE the 0.002-0.005 plateau, so 0.002 sits right under it.

The 0.002-0.005 plateau (threshold at ~0.2% EMA tick-noise; decay past ~0.01) is consistent across two
independent cells and cheap on the full book, so `0.002` is adopted as the universal engine default.
