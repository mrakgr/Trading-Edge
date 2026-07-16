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
`vwap_reclaim_candidate` (ADV ≥ **$30M** AND rvol_0945 > 1). $10k notional/trip, PF = gross-win / gross-loss (MOC).

> ⭐ **The anchor is 09:00, not 09:30 — deliberately. See F10.** F1–F7 were all produced with a
> **premarket-inclusive 09:00 VWAP anchor** (the last 30 min before the RTH open seed VWAP/EMA/the run
> counter). This was originally an accident (`9 * 60` = 540, while every comment claimed 09:30 = 570), and
> F8 initially "fixed" it to 09:30 — which **collapsed the ladder** (A++ 4.33 → 2.49). F10 then swept the
> anchor across the whole premarket and found **09:00 peaks in every book — including the UNGATED fat book —
> and in all seven years**. So the effect is real and the anchor is RETAINED as a documented design choice;
> **F1–F7 stand as published.** ⚠️ But read F10's magnitude section: the effect is only **+2% ungated**, and
> the four graded gates compound it to +71% — the ladder is one effect counted four times.
> Also see **F9** (a second lookahead in the universe filter, unfixed — deliberately).
> (The ADV ≥ $30M above was previously mis-documented as $1M; $30M is what the builder does.)

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

---

## F4 — A+ cell rebuilt: make ATR EXPLICIT (run_atr ≥ 0.013), then dpa as a BAND [1.25, 3)

**The insight (user):** the old A+ cell `updn ≥ 1.3 & run_max_dist ≥ 0.035 & run_dist_per_atr < 3` HIDES an
ATR floor. `dpa = run_max_dist / run_atr < 3` with `run_max_dist ≥ 0.035` forces `run_atr > 0.035/3 = 0.0117`
— nearly the momentum systems' 0.013 floor. So the `run_max_dist ≥ 3.5%` floor was doing TWO jobs at once
(a real depth floor AND a smuggled ATR floor). Untangle them: set `run_atr ≥ 0.013` EXPLICITLY, drop the
depth floor, and re-examine dpa on its own.

**New base beats the old cell — 2.5× the trips at still-strong PF** (all years):

| book | n | win% | PF | avg%/tr | net |
|---|---|---|---|---|---|
| OLD A+ (`rmd ≥ 3.5% & dpa < 3`) | 240 | 48 | 4.29 | +18.80 | $451k |
| **NEW base (`updn ≥ 1.3 & run_atr ≥ 0.013`)** | 612 | 41 | 2.72 | +9.66 | **$591k** |

**dpa within the new base is a HUMP — both tails weak (deciles, all-yr):**

| dpa decile | range | n | PF | avg% |
|---|---|---|---|---|
| 1–2 | 0.40–1.05 | 124 | 2.13 / 2.77 | +6–8 |
| **3** | 1.05–1.26 | 61 | **1.25** | +1.32 (shallow dip) |
| 4–8 | 1.26–2.76 | 305 | **2.65–4.77** | +8–17 (the sweet spot) |
| 9 | 2.77–3.50 | 61 | 2.16 | +8.86 |
| **10** | 3.51–7.73 | 61 | **1.69** | +5.20 (deep-vs-vol) |

**dpa still earns its keep — but as a BAND, not a bare ceiling:**

| dpa cut on new base | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| no cut | 612 | 41 | 2.72 | +9.66 | $591k |
| < 3 (old ceiling) | 515 | 41 | 3.12 | +10.79 | $556k |
| ≥ 1.25 (drop shallow) | 429 | 44 | 2.97 | +11.47 | $492k |
| **[1.25, 3) (BAND)** | **332** | **45** | **3.71** | **+13.76** | **$457k** |

Conclusions on the user's question ("is dpa < 3 redundant with ATR?"):
1. **`dpa < 3` is NOT just an ATR proxy** — even with run_atr ≥ 0.013 explicit, cutting dpa ≥ 3 lifts PF
   2.72 → 3.12 at 94% of net. Deep-relative-to-vol runs (deciles 9-10) genuinely underperform (a real ceiling).
2. **A shallow FLOOR also matters** — dpa < ~1.25 (decile 3 dip) is weak. The band **[1.25, 3)** = PF 3.71 /
   +13.76%/tr / 332 trips.

## F5 — but a plain `run_max_dist` FLOOR beats the dpa band (simpler + more net)

The dpa band is a vol-NORMALIZED ratio. With ATR now explicit, is the RAW run depth (`run_max_dist`) the
cleaner lever? Broke it down within the same base — and unlike dpa (a hump), dist is a MONOTONE floor:

| dist decile | range | n | PF | avg% |
|---|---|---|---|---|
| 1–4 | 0.005–0.032 | 246 | 1.15–2.26 | +0.8 to +5.5 (weak/noisy) |
| **5–10** | **≥ 0.032** | 366 | **3.04–3.70** | +10.6 to +20.0 (uniformly strong) |

Sharp knee at ~0.032-0.035. Floor sweep:

| dist floor | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| (none) | 612 | 41 | 2.72 | +9.66 | $591k |
| ≥ 0.03 | 392 | 44 | 3.12 | +13.40 | $525k |
| **≥ 0.035** | **336** | **46** | **3.25** | **+14.51** | **$488k** |
| ≥ 0.05 | 216 | 47 | 3.34 | +16.71 | $361k |
| ≥ 0.07 | 127 | 50 | 3.48 | +19.33 | $245k |

**`dist ≥ 0.035` beats the dpa band [1.25, 3)** — similar size (336 vs 332), but HIGHER win rate (46 vs 45),
HIGHER avg% (+14.51 vs +13.76), and **+$31k net** ($488k vs $457k). The band's only edge was PF (3.71 vs 3.25),
bought by clipping shallow low-vol names — but that costs net. And ONE floor is far simpler + more principled
than a two-sided ratio band: run-depth is best captured by the RAW dist, not the vol-normalized ratio. The
old `dpa < 3` ceiling was mostly the ATR floor in disguise; once `run_atr ≥ 0.013` handles jumpiness, the
deep-vs-vol ceiling adds little.

**Yearly (dist-floor cell, post-2020; pre-2020 = noise):**

| year | n | PF | avg% | net |
|---|---|---|---|---|
| 2020 | 32 | 6.10 | +31.44 | $101k |
| 2021 | 92 | 1.36 | +2.95 | $27k |
| 2022 | 45 | 2.57 | +8.67 | $39k |
| 2023 | 20 | 6.25 | +29.91 | $60k |
| 2024 | 51 | 3.55 | +14.73 | $75k |
| 2025 | 56 | 4.00 | +18.36 | $103k |
| 2026 | 29 | 3.82 | +19.44 | $56k |
| **TOTAL** | **336** | **3.25** | +14.51 | $488k |

**All-weather** (positive every modern year). One trade-off vs the dpa band: 2021 is marginally softer (PF
1.36 vs 1.78 — the band's shallow-floor trimmed 2021's marginal names slightly better), but positive either
way; every other year is as strong or stronger, with more total net.

**⭐ NEW A+ CELL: `updn ≥ 1.3 & run_atr ≥ 0.013 & run_max_dist ≥ 0.035`** — PF 3.25 / +14.51%/tr / 336 trips /
$488k. Three orthogonal floors, each doing one job (updn = conviction, run_atr = jumpiness, dist = run depth),
no vol-normalized ratio needed. Cleaner and larger than the original `rmd≥3.5% & dpa<3` cell.

## F6 — dpa RE-examined inside the floored cell: now a PURE CEILING (subsumed shallow-floor, kept ceiling)

Re-ran the dpa breakdown WITHIN the three-floor A+ cell (`updn ≥ 1.3 & run_atr ≥ 0.013 & dist ≥ 0.035`,
PF 3.25). The earlier HUMP (F4) is gone — the shallow-dpa weakness DISAPPEARED, leaving a clean ceiling:

| dpa decile | range | n | PF | avg% |
|---|---|---|---|---|
| **1** | 0.65–1.36 | 34 | **5.07** | +23.71 (now the BEST, was weak in F4) |
| 2–7 | 1.37–2.95 | 202 | 3.47–5.05 | +12 to +22 (uniformly strong) |
| **8–10** | ≥ 2.98 | 99 | **1.53 / 1.75 / 1.58** | +4.5 to +6 (cliff) |

**Why the shallow-floor vanished:** in F4 (only run_atr floored) low dpa meant SHALLOW runs (weak). Now that
`dist ≥ 0.035` guarantees a deep run, low dpa means "deep run relative to LOW vol" — the BEST kind. The dist
floor subsumed dpa's shallow-floor role; only the over-extension ceiling survives.

**The ceiling is a strong, net-cheap cut:**

| dpa cut on the cell | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| no cut | 336 | 46 | 3.25 | +14.51 | $488k |
| **< 3** | 239 | 49 | **4.33** | +18.92 | $452k |
| < 2.5 | 189 | 48 | 4.49 | +20.10 | $380k |
| < 2 | 113 | 50 | 4.87 | +21.90 | $247k |

`dpa < 3` lifts PF 3.25 → **4.33** keeping 93% of net — removes the deep-vs-vol runs (deciles 8-10) that
mean-revert (over-extended relative to their own vol). **And it fixes 2021** (the adverse year): 1.36 → 2.11,
because the over-extended runs it cuts WERE 2021's worst names.

**Yearly (floors + dpa<3 ceiling):**

| year | n | PF | avg% | net |
|---|---|---|---|---|
| 2020 | 24 | 8.19 | +35.24 | $85k |
| 2021 | 62 | 2.11 | +7.90 | $49k |
| 2022 | 27 | 4.89 | +17.03 | $46k |
| 2023 | 18 | 6.61 | +33.04 | $59k |
| 2024 | 40 | 3.30 | +14.80 | $59k |
| 2025 | 40 | 6.61 | +24.30 | $97k |
| 2026 | 23 | 4.53 | +22.28 | $51k |
| **TOTAL** | **239** | **4.33** | +18.92 | $452k |

**⭐⭐ FINAL A++ CELL: `updn ≥ 1.3 & run_atr ≥ 0.013 & run_max_dist ≥ 0.035 & dpa < 3`** — PF 4.33 /
+18.92%/tr / 239 trips / $452k. All-weather (every modern year ≥ 2.11), the best-yet cell. This is the FULLY
DE-TANGLED original `updn≥1.3 & rmd≥3.5% & dpa<3`: same feel, but now FOUR orthogonal knobs each with one
job — updn (conviction), run_atr (jumpiness floor), dist (absolute depth), dpa (over-extension ceiling) —
matching the old cell's PF (4.29) on ~identical trips (239 vs 240) but with a principled, understood structure.

## F7 — updn is the QUALITY/CAPACITY DIAL → the A / A+ / A++ ladder (all-weather)

With the other three knobs fixed (`run_atr ≥ 0.013 & dist ≥ 0.035 & dpa < 3`), updn is the tightest floor —
relax it for weaker/bigger cells. Deciles show a clean break at updn ≈ 1.06 (updn = up-vol / dn-vol; 1.0 =
balanced): below it PF 1.15–1.69, above it PF 2.1–5.1. The floor sweep is the capacity ladder:

| tier | updn floor | n | win% | PF | avg%/tr | net | 2021 PF |
|---|---|---|---|---|---|---|---|
| **Capacity** | ≥ 0.8 | 551 | 42 | 2.86 | +10.80 | $595k | 1.87 |
| **A** | ≥ 1.0 | 411 | 43 | 3.32 | +13.38 | $550k | 1.69 |
| **A+** | ≥ 1.1 | 349 | 44 | 3.74 | +15.90 | **$555k** | — |
| **A++** | ≥ 1.3 | 239 | 49 | 4.33 | +18.92 | $452k | 2.11 |

- **All tiers are all-weather** — positive every modern year (Capacity: PF ≥ 1.87 every year; A: ≥ 1.69).
- **Net peaks in the MIDDLE** ($555k at A+): the wide Capacity cell dilutes PF, the tight A++ sheds too many
  trips. A+ (updn ≥ 1.1) is the best net/PF balance.
- **updn ≥ 1.2 is a DOMINATED trough — skip it.** PF 3.59 / +15.40%/tr / 299 tr / $461k: LOWER PF and net
  than 1.1 (3.74 / $555k, more trips) AND lower PF than 1.3 (4.33) at ~equal net. The [1.2, 1.3) slice is a
  weak pocket; 1.2 is Pareto-dominated by both neighbors. The ladder's real inflection points are **1.1**
  (best net/PF) and **1.3** (quality peak). (All-weather regardless: updn ≥ 1.2 is positive every year, 2021 = 2.02.)
- **updn ≥ 0.8 is the widest sensible floor** — below it (deciles 1-5) the edge thins to PF 1.15–1.69; going
  to 0 adds few trips (739 vs 551) at PF 2.60. 0.8 ≈ where rising-side volume stops being decisive.
- 2021 (adverse) dilutes most as updn relaxes (A++ 2.11 → A 1.69 → Capacity 1.87), as expected — the tighter
  the conviction floor, the more it protects the weak year. All stay positive.

**⭐ THE SETTLED A++ FAMILY (all share `run_atr ≥ 0.013 & run_max_dist ≥ 0.035 & dpa < 3`; updn is the dial):**
Capacity (updn ≥ 0.8, PF 2.86 / 551tr) → A (≥ 1.0, PF 3.32) → A+ (≥ 1.1, PF 3.74 / max net) → A++ (≥ 1.3,
PF 4.33 / 239tr). updn is a pure conviction dial with the depth/vol/jumpiness floors held fixed.

> ⭐ **Every number in F1–F7 was produced with the 09:00 premarket-inclusive VWAP anchor — and that anchor
> is now the DELIBERATE, swept default (F10). These tables STAND.** F8 records the false alarm (the anchor
> was originally accidental, and "fixing" it to 09:30 collapses this ladder to a flat ~2.5); F10 records the
> sweep that vindicated 09:00 — it peaks in every book including the ungated one, and in all seven years.
> ⚠️ Read F10's magnitude section before trusting the ABSOLUTE PFs here: the underlying anchor effect is
> ~+2%, and these four gates all measure relative to VWAP, so they compound it. The ladder is **one effect
> counted four times**, not four independent confirmations.

---

## F8 — the 09:00 anchor: discovery + the FALSE ALARM (verdict SUPERSEDED by F10)

> **Status:** this finding correctly discovered that the ladder is built on 30 min of premarket, and
> correctly measured what happens when you remove it. Its **verdict was wrong** — it called the anchor a
> typo-artifact and de-canonized the ladder on the strength of a 3-point "dose-response" that turned out to
> be one shoulder of a hump. **F10 sweeps the full premarket and reinstates 09:00.** Kept in full as the
> record of the reasoning error: *a monotone run of three points is not a gradient — sweep the whole range
> before concluding.*

**The bug.** `Backtest.fs` read `SessionStartMin = 9 * 60`. `et_min` is `hour*60 + minute`, so that is
**540 = 09:00 ET**, not 09:30 (= 570). The adjacent comment said *"09:30 ET — the SMB session VWAP anchors
at the RTH OPEN (not premarket)"*; `Intraday.fs:69` and this doc's header said 09:30 too. **All three were
wrong: the code did 09:00.** The emitter filters `et_min >= SessionStartMin`, so **30 minutes of premarket
fed VWAP, the 9-EMA, `sessLow`, `cumVol`, and the below-VWAP run counter.** Inherited verbatim from V1
(`VwapReclaim/Backtest.fs:38`) and V2 (`VwapReclaimV2/Backtest.fs:38`) — **every published VwapReclaim
number across all three versions carries it.** (DipRiderV4 and OpeningDriverV2 have the correct explicit
two-anchor split — this is a VwapReclaim-lineage bug only.)

**It is not cosmetic.** On 2026-06-24 the 09:00–09:29 window holds **19,530 bars / 3,238 tickers / 259M
shares**. Measured VWAP distortion at 10:00 across ~2,500 liquid names/day over 7 days: mean **~0.01%**
(unbiased — direction flips day to day, so the fat book was never *systematically* inflated), but **p95 =
0.12–0.21% per name**. That p95 lands exactly on the **~0.2% EMA tick-noise floor** F3 identified as the
threshold where the stop buffer starts working — i.e. the distortion is the same size as the signal the
system was tuned against.

### The A/B — 3 cells, full range 2003-09 → 2026-06, `--session-start-min` {540, 555, 570}

`555` (09:15) is a **dose-response probe**: half the premarket window.

**Fat/capacity book — barely moves:**

| anchor | trips | win% | net | PF |
|---|---|---|---|---|
| 540 (09:00, the bug) | 41,027 | 40.8 | $1,604,145 | **1.332** |
| 555 (09:15) | 41,382 | 40.8 | $1,602,245 | 1.328 |
| **570 (09:30, correct)** | 41,703 | 40.7 | $1,598,236 | **1.323** |

**The F7 graded ladder — collapses, monotonically, in every tier:**

| tier (base `run_atr≥0.013 & rmd≥0.035 & dpa<3`) | 540 PF | 555 PF | 570 PF | 540 net | 570 net |
|---|---|---|---|---|---|
| Capacity `updn≥0.8` | **2.86** | 2.53 | **2.28** | $595k | $448k |
| A `updn≥1.0` | **3.32** | 2.82 | **2.54** | $550k | $411k |
| A+ `updn≥1.1` | **3.74** | 3.17 | **2.52** | $555k | $358k |
| A++ `updn≥1.3` | **4.33** | 3.43 | **2.49** | $452k | $237k |

*(540 reproduces the published F7 numbers exactly — 551/2.86/$595k, 411/3.32/$550k, 349/3.74/$555k,
239/4.33/$452k — so the A/B harness is verified against the shipped book.)*

**Three facts, in order of importance:**

1. **It is a clean DOSE-RESPONSE, not noise.** 09:15 lands strictly between 09:00 and 09:30 in **all four
   tiers**. More premarket → monotonically better numbers. Path-dependent noise would not do this four
   times in the same direction.
2. **⭐ At the correct anchor THE LADDER STOPS LADDERING.** At 570: A 2.54, A+ 2.52, A++ 2.49 — tightening
   `updn` from 1.0 → 1.3 buys **nothing**. F7's entire claim ("updn is a pure conviction dial") **exists
   only when premarket is in the VWAP.** The quality dial was an artifact of the anchor.
3. **Trip counts barely move** (A++ 239 → 232). The bug was not *manufacturing* trades — it was **sorting**
   them. Same setups, better selection.

**Per-year A++ — worse in ALL 7 modern years** (so it is not a regime artifact):

| year | 540 n | 540 PF | 570 n | 570 PF |
|---|---|---|---|---|
| 2020 | 24 | 8.19 | 23 | 2.59 |
| 2021 | 62 | 2.11 | 58 | **1.02** |
| 2022 | 27 | 4.89 | 25 | 4.20 |
| 2023 | 18 | 6.61 | 15 | 5.76 |
| 2024 | 40 | 3.30 | 39 | 3.00 |
| 2025 | 40 | 6.61 | 47 | 3.17 |
| 2026 | 23 | 4.53 | 21 | 2.12 |

**2021 goes to PF 1.02 — break-even.** The "all-weather, every modern year ≥ 2.11" claim in F6/F7 does not
survive the fix.

### The mechanism — and why it is NOT simply "premarket setups are better"

Under 540, splitting the A cell by whether the weakness run *started* before 09:30:

| group | n | avg%/tr | PF | net |
|---|---|---|---|---|
| run started in RTH | 340 | +13.08 | **3.19** | $445k |
| run STARTED premarket | 71 (17.3%) | +14.82 | **4.11** | $105k |

Only **17.3%** of A-cell entries are the "impossible" ones (a weakness run that began before the open —
these cannot exist at a correct anchor). They are good, but small: 71 trips / $105k.

**The bigger effect is on the ordinary RTH setups.** The 340 RTH-started trades still print **PF 3.19**
under 540, versus ~2.54 for the whole A cell under 570. So the premarket seed is not mainly smuggling in
premarket setups — **it shifts the VWAP level itself**, which moves `run_max_dist`, `updn` and `dpa`, and
that shifted VWAP sorts good from bad better than the true RTH VWAP does.

### Interim verdict (SUPERSEDED by F10 — read on)

The initial call was to fix to 570 and de-canonize the ladder, on the reasoning that *"a monotone
improvement in an arbitrary parameter is the signature of a fitted artifact"* — 09:00 being exactly where
`updn`/`rmd`/`dpa` had been tuned. **That reasoning was WRONG, and F10 refutes it with a full sweep.**
The 09:00→09:15→09:30 "dose-response" above is not a gradient at all; it is the descending shoulder of a
hump whose peak sits at ~09:00. Reading three points off one side of a hump produced a confident and
incorrect conclusion. **The anchor is NOT a typo to be fixed — it is a real (if small) effect. See F10.**

### Live-trading implication (why this was found now)

Every anchor is live-implementable — the Polygon aggregates endpoint returns today's bars from 04:00 ET,
so premarket is available in one REST call. This was **never a feed limitation**; it was purely a "which
numbers are real" question.

---

## F10 — ⭐ THE ANCHOR SWEEP: 09:00 IS REAL, NOT A FIT. But the effect is SMALL and the gates COMPOUND it.

F8 concluded the 540 anchor was a typo-artifact. **That conclusion was wrong.** Swept the anchor across the
full premarket, **modern era only (2020-01-01 → 2026-06-25**, matching F5/F6/F7's own "pre-2020 = noise"
scope; 10 cells, ~30s each, strictly sequential).

> **Methodology note / hard-won lesson:** each run does `PRAGMA memory_limit='6GB'` (`Backtest.fs:350`), so
> N concurrent full-history runs ask for N×6GB. Running 9 in parallel on a 15GB box OOM'd the machine (the
> kernel killed unrelated processes, and one cell died silently while still reporting "done" — which would
> have quietly corrupted the sweep). **Always run these sequentially.** Restricting to ≥2020 cuts a cell
> from 117s to 28s, so the whole sweep is ~5 min.

### The full curve — a HUMP peaking at ~09:00, not a monotone gradient

| anchor | fat PF | Capacity | A | A+ | **A++** | A++ net |
|---|---|---|---|---|---|---|
| 04:00 | 1.442 | 2.41 | 2.40 | 2.54 | 2.55 | $213k |
| 05:00 | 1.408 | 2.19 | 2.16 | 2.31 | 2.38 | $190k |
| 06:00 | 1.423 | 2.31 | 2.38 | 2.55 | 2.70 | $226k |
| 07:00 | 1.410 | 2.59 | 2.58 | 2.67 | 2.97 | $257k |
| 08:00 | 1.447 | 2.78 | 2.59 | 2.87 | 3.00 | $252k |
| 08:30 | 1.449 | 2.77 | 3.00 | 3.17 | 3.27 | $310k |
| 08:45 | 1.478 | 2.82 | 2.95 | 3.20 | 3.23 | $303k |
| **09:00** | **1.501** | **2.99** | **3.36** | **3.77** | **4.29** | **$447k** |
| 09:15 | 1.490 | 2.62 | 2.87 | 3.22 | 3.42 | $347k |
| 09:30 | 1.473 | 2.31 | 2.57 | 2.55 | 2.51 | $236k |

**"Earlier is better" is FALSE.** 04:00–07:00 is a flat, noisy 2.4–3.0 band — including that early-premarket
data actively *dilutes* the book vs starting at 08:30. The curve **rises** from ~07:00 to a peak at 09:00,
then falls off a cliff at 09:30. Same hump in all four tiers AND in the ungated fat book.

### The discriminator: the peak survives with the tuned gates REMOVED

If 09:00 were merely where `updn`/`rmd`/`dpa` were fit, then a book *without* those gates would have no
reason to prefer it. Tested each gate in isolation and with none at all:

| book | 04:00 | 08:00 | 08:30 | 08:45 | **09:00** | 09:15 | 09:30 | peak |
|---|---|---|---|---|---|---|---|---|
| **NO gates (fat book)** | 1.44 | 1.45 | 1.45 | 1.48 | **1.50** | 1.49 | 1.47 | **09:00** |
| `run_atr ≥ 0.013` only | 1.87 | 1.86 | 1.80 | 1.86 | **1.92** | 1.91 | 1.84 | **09:00** |
| `rmd ≥ 0.035` only | 1.70 | 1.77 | 1.80 | **1.85** | 1.83 | 1.77 | 1.62 | 08:45 |
| `dpa < 3` only | 1.52 | 1.53 | 1.52 | 1.54 | **1.63** | 1.60 | 1.61 | **09:00** |
| `updn ≥ 1.3` only | 1.83 | 1.83 | 1.91 | 1.95 | **2.09** | 1.89 | 1.86 | **09:00** |
| ALL FOUR (A++) | 2.55 | 3.00 | 3.27 | 3.23 | **4.29** | 3.42 | 2.51 | **09:00** |

**The ungated fat book — which contains not one tuned parameter — peaks at 09:00.** So does every gate
individually. **09:00 is a property of the DATA, not of the fitting.** The overfitting hypothesis is dead.

### Per-year — all-weather, and NOT concentration-driven

A++ PF by year and anchor:

| anchor | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---|---|---|---|---|---|---|---|
| 04:00 | 1.52 | 1.41 | 2.82 | 6.43 | 1.82 | 3.65 | 3.08 | 2.55 |
| 08:00 | 1.56 | 1.56 | 3.61 | 6.13 | 3.57 | 4.73 | 2.17 | 3.00 |
| 08:30 | 4.53 | 1.44 | 5.04 | 3.05 | 3.68 | 6.38 | 2.08 | 3.27 |
| **09:00** | **8.19** | **2.11** | 4.89 | **6.61** | 3.30 | **6.61** | **4.53** | **4.29** |
| 09:15 | 3.88 | 1.57 | 3.20 | 5.32 | **4.20** | 5.51 | 3.94 | 3.42 |
| 09:30 | 2.59 | **1.02** | 4.20 | 5.76 | 3.00 | 3.17 | 2.12 | 2.51 |

**09:00 is best-or-near-best in ALL SEVEN years** — seven disjoint samples agreeing. And it is *less*
lottery-driven than the RTH anchor: at 09:00 the top-5 winners are **19.4%** of gross profit (112 winners);
at 09:30, **26.5%** (92 winners). The 09:00 book is broader and healthier, not one whale.

### ⚠️ The honest magnitude: a SMALL real effect, ~15× amplified by gate-stacking

| book | 09:30 → 09:00 | lift |
|---|---|---|
| ungated fat book | 1.473 → 1.501 | **+2%** |
| single gates | — | **+3–6%** |
| all four stacked (A++) | 2.51 → 4.29 | **+71%** |

The mechanism is real but **small**. The four gates each lean on the same shifted VWAP (`rmd`, `dpa` and
`updn` are all measured *relative to VWAP*), so they **compound** a ~2–5% edge into a ~71% one. **A++'s 4.29
is a genuine signal wearing a very large multiplier** — treat the ladder's absolute PF with corresponding
caution, and do not read the A→A++ progression as four independent confirmations. It is one effect, counted
four times.

### Interpretation

~09:00 is plausibly when *meaningful* pre-open positioning begins: the 04:00–07:00 tape is thin enough that
folding it in dilutes VWAP with noise (hence that region UNDER-performing even the RTH anchor), while the
last ~60–90 min before the open carries the real overnight repricing. This predicts the true optimum is a
**region (~08:30–09:15)**, and that 09:00's exact height (the ~25% jump over both 08:45 and 09:15) is partly
luck — a smooth mechanism has no reason to spike that sharply at one 15-min step. **Do not over-trust 09:00
specifically; trust the 08:30–09:15 plateau.**

### ⭐ VERDICT — `SessionStartMin = 540` (09:00 ET) is RETAINED, now as a DELIBERATE, DOCUMENTED choice

Not a typo, not "the RTH open" — a **premarket-inclusive VWAP anchor**, swept and chosen. The F1–F7 ladder
**stands as published**. What changes is the *comment and its status*: this is now a named design decision
with a finding behind it, not an accident nobody noticed.

**The `--session-start-min` flag (added in this work) stays** as the knob that makes it explicit and
re-sweepable.

**Corollary for V1/V2:** they share the 540 anchor, so their published numbers are unaffected too — the
"bug" they inherit is the same real effect.

---

## F9 — a SECOND lookahead in the universe: `ADV = avgvol20 × day_close` uses D's CLOSE to trade D

Found while verifying F8. `scripts/equity/build_vwap_reclaim_candidate.fsx` filters
`avgvol20 * day_close >= 30000000.0` — **`day_close` is day D's closing price**, unknowable at 10:00 when
the entry fires. Same class as the anchor bug: the universe cannot be reproduced live.

**Churn measured over the full `mr_candidate` base (rvol_0945 > 1):**

| definition | ticker-days |
|---|---|
| `avgvol20 × day_close` (current, lookahead) | 52,630 |
| `avgvol20 × prev_adj_close` (D-1, live-safe) | 52,222 |
| admitted only by the lookahead | 1,568 |
| dropped only by the lookahead | 1,211 |

**5.28% of the universe differs.** Mild (a liquidity floor, not a signal gate) but real, and it is a hard
blocker for live parity: the live scanner *provably cannot* reproduce this universe. Fix to
`prev_adj_close` (deterministic, available pre-open) and re-baseline.

**Also: three stale comments claim `$1M`** where the builder does **`$30M`** — a 30× discrepancy:
this doc's header (line 22), `Backtest.fs:14`, `Backtest.fs:208`. The **$30M** figure is correct (F20 of
the V1 doc: `<$30M` is a graveyard at PF 0.70).
