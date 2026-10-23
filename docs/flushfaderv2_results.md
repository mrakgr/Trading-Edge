# FlushFaderV2 — a linear model over the whole feature set

**Started 2026-10-12.** `TradingEdge.FlushFaderV2` is a copy of `TradingEdge.FlushFader` at research commit
`f193920` (modules renamed `TradingEdge.FlushFaderV2.*`, nothing else changed). The original's results document,
`docs/flushfader_results.md`, is ~18,500 lines; V2's research is written here. Everything up to §S49aj (the broad
book in the Scanner, 2026-09-09) is the starting point and lives there.

## Where V1 left off

| piece | state (V1, `docs/production_specs.md` §1) |
|---|---|
| book | THE BROAD BOOK = the efficiency variant (§S49d step 7: frame + lows300 + eff10 + v10r + z20 + lows180 + crf + coil5lo) + gap_60 < 40 + rule140 + cascade + WAIT; 37,254 trades 2020-01 .. 2026-08, PF ~1.46 gross |
| sizing | model A3 (§S49ae): log-linear fits of win rate, mean win, mean loss on gap {0, 1–3, 4–39} × volat 5 bands × rate600 fast flag × S-tier flag; multiplier = fitted PF−1 / 0.519, clip [0.25, 4] |
| reference | `data/flushfader_gate_review/broad_reference_book.parquet` (37,254 rows, A3 multiplier) — the book the Oms trades |
| features | `data/flushfader_review_slice.parquet` — every gate / voice input per sampler trip of base_v19 (8.27M trips) |

## The V2 goal (user, 2026-10-12)

Fold every feature into ONE linear sizing model instead of gates and a 4-axis bucket model. The candidate set is
**the efficiency-variant list** (every gate on the §S49d curve) **plus the VOTE broken into its voices** (the user,
2026-10-09: "turn the voice family roster's voices into plain sizing features").

### The efficiency-variant list (§S49d, in curve order)

| step | gate | input column (slice) | definition | in the broad book? |
|---|---|---|---|---|
| 1 | lows300 | `l300` | lows since the leg's first low, 300-bar window | gate ≥ 6 |
| 2 | eff10 | `eff10a` | \|eff_10m\| | gate ≥ .15 |
| 3 | v10r | `v10r` | (vol_10 / 10) / (vol_60 / 60), bar clock | gate ≥ .75 |
| 4 | z20 | `z20` | z of ln(signal_vwap) vs the 1200-bar volume-weighted log-price moments | gate < −1.5 |
| 5 | lows180 | `l180` | lows since first low, 180-bar window | gate ≥ 3 |
| 6 | crf | `crf` | chg_since_run_first_low | gate ≤ −0.2 % |
| 7 | coil5lo | `consol_5m_lag1m` | the 5m consolidation ratio, lagged 1m | gate ≤ .22 |
| 8 | s20 | `s20` | ols_slope_1200 × 6e5 (bp/min) | — |
| 9 | speed | `speed` | signal_vwap / vwap_60_prev − 1 | — |
| 10 | accel | `accel` | (ols_slope_600 − ols_slope_1200) × 6e5 | — |
| 11 | rngf | `rngf` | rng_300 / rng_20m | — |
| 12 | rflow | `rflow` | ols_r_since_flow | — |
| 13 | VOTE | (the voices below) | the roster OR | — |
| 14 | d1m | `d1m` | signal_vwap / hi_60 − 1 | — |
| 15 | eff20 | `eff20a` | \|eff_20m\| | — |
| 16 | s5 | `s5` | ols_slope_300 × 6e5 | — |
| 17 | cascade | `ht`, `ssh` | halts today, secs since the last halt | gate (two-case wait) |
| 18 | K | `k20` | lows since the leg's first low (1200-bar leg) | — |
| 19 | dlv | `dlv` | signal_vwap / leg vwap − 1 | — |
| 20 | ssf | `ssf` | ols_slope_since_flow × 6e5 | — |
| 21 | e9 | `e9` | eff_9ema_10m | — |

### The voice family (the VOTE, ROSTER v3.3 as the review tool encodes it)

| voice | input column | voice threshold |
|---|---|---|
| v20 | `volat` | ≥ 140 bp (already an A3 axis, 5 bands) |
| d20a | `d20a` | (signal_vwap / first_low_vwap)(1 + d_hi_flow) − 1 < −28 % |
| dslo | `dslo` | signal_vwap / sess_low − 1 ≥ +8 % |
| vexp | `vexp` | (volat_slope_10m − volat_slope_20m) × 2e4 > 12 |
| vcrush | `vcrush` | volat_slope_5m × 2e4 ≤ −24 |
| acneg | `ac1` | ac1_ewma < −0.1 |
| legage | `esf` | secs_since_first_low ∈ [0, 450] |
| dsu | `dsu` | downticks_since_uptick ≥ 8 |
| haltband | `ssh` | ∈ [1200, 4800) |
| stier | `ht`, `ssh` | ht ≥ 1 ∧ ssh ∈ [120, 1200) (the broad book's S tier is ssh ∈ [300, 2400), already an A3 flag) |

The A3 axes (gap_60, volat, rate600, the S tier) are the **baseline** every V2 model has to beat out of sample.

## Method (carried over from V1)

- Book = construction 3 (mc=1 greedy per ticker-day, replayed inside the filtered set); null resamples TICKER-DAYS.
- Sizing measure = PF−1 net of the $0.001/sh/side credit (the 2026-09-09 ruling: this system sizes on PF−1, not
  trimPF−1); multiplier = fitted PF−1 / base, clip [0.25, 4].
- Every claim gets the year table and the two year-block holdouts (fit ≤ 2023 → 2024–26, and the mirror), and a
  steepness control (a steeper A3 map) before a gain is credited to a feature.
- Lookahead: every input above is knowable at the signal second (they are V1's gates/voices, audited in §S49). A new
  feature gets the knowability-clock check before it enters.

---

## V2-1 — the copy and the plan (2026-10-12)

`TradingEdge.FlushFaderV2` added to `TradingEdge.slnx`; builds clean (Release). No engine change yet: the model work
starts post-hoc on the slice, the engine changes only if a feature the slice lacks is needed.

## V2-2 — every candidate's shape on the broad book (2026-10-12)

`scripts/equity/flushfaderv2_features.py` on `data/flushfaderv2/book_features.parquet` (the 37,254-trade broad
reference book + every slice input, joined on symbol / trade_date / signal_sec: 37,254 of 37,254 matched, the shared
columns identical). The book's membership is fixed, so the octile tables need no replay. Return = ret_exit + the
$0.001/sh/side credit. Book PF−1 0.53. Full per-band tables (with the 2020–23 / 2024–26 halves):
`data/flushfaderv2/features.md`.

| feature | Spearman ρ (ret) | PF−1 low octile → high octile | min / max band | shape |
|---|---|---|---|---|
| d1m | −0.185 | 0.59 → 0.17 | 0.17 / 0.82 | monotone (deeper below the 1m high = better), both halves |
| speed | −0.170 | 0.66 → 0.27 | 0.27 / 0.71 | monotone, both halves |
| d20a | −0.151 | 0.50 → 0.25 | 0.25 / 0.69 | monotone except the deepest octile |
| s5 | −0.131 | 0.68 → 0.37 | 0.36 / 0.68 | monotone |
| crf | −0.112 | 0.61 → 0.33 | 0.32 / 0.71 | monotone |
| s20 | −0.109 | 0.82 → 0.32 | 0.32 / 0.82 | monotone, both halves |
| dlv | −0.099 | 0.47 → 0.44 | 0.44 / 0.71 | hump |
| ssf | −0.069 | 0.60 → 0.46 | 0.39 / 0.68 | weak |
| accel | −0.049 | 0.60 → 0.61 | 0.43 / 0.62 | flat |
| dsu | −0.035 | 0.60 → 0.47 | 0.47 / 0.60 | weak (4 bands) |
| z20 | −0.033 | 0.56 → 0.37 | 0.37 / 0.63 | weak |
| K (k20) | −0.021 | 0.61 → **0.19** | 0.19 / 0.63 | flat to 77 lows, then a cliff (k20 ≥ 77 = 4,753 @ 0.19, both halves) |
| esf | −0.016 | 0.58 → 0.33 | 0.33 / 0.78 | noisy |
| volat (v20) | +0.176 | 0.41 → 0.75 | 0.37 / 0.75 | monotone (an A3 axis already) |
| dslo | +0.074 | 0.35 → **1.02** | 0.23 / 1.02 | a STEP at ~5 % above the session low (≥ 4.6 %: 0.84–1.02, both halves) |
| ht / ssh | +0.088 / +0.085 | 0.50 → 0.69 | — | halted vs not (2 bands) |
| gap_60 (A3) | −0.077 | 0.86 → 0.30 | 0.30 / 0.86 | monotone (an A3 axis already) |
| rate600 (A3) | −0.039 | 0.67 → 0.47 | 0.41 / 0.72 | weak |
| eff10, rngf, rflow, e9, lows300, lows180, coil5lo, v10r, ac1, vcrush, vexp, eff20 | \|ρ\| ≤ 0.04 | — | — | flat or noisy humps |

Reading:
- The strong group is **the flush's depth and speed**: d1m, speed, d20a, s5, s20, crf. They all measure "how far /
  how fast it fell" and are surely collinear with each other and with volat (a magnitude axis). ρ against the return
  mixes edge with magnitude; the PF−1 column is the edge read.
- Two features are **non-linear steps**, not slopes: K's cliff at ~77 lows and dslo's step at ~5 % above the session
  low. A linear model needs them as flags (or a hinge), not raw.
- The freshness gates the broad book already applies (lows300, lows180, eff10, v10r, z20, coil5lo) are flat inside
  the book: their work is done as gates.

**Ruling (user, 2026-10-12): the broad book's gates stay GATES** — lows300, eff10, v10r, z20, lows180, crf, coil5lo, the
gap door, rule140, cascade / WAIT are not model candidates. The candidates are the remaining efficiency-list gates
(s20, speed, accel, rngf, rflow, d1m, eff20, s5, K, dlv, ssf, e9) and the voices (d20a, dslo, vexp, vcrush, ac1, esf,
dsu, haltband); volat is already an A3 axis.

## V2-3 — A3 + one candidate at a time, forward selection on the year-block holdouts (2026-10-12)

`scripts/equity/flushfaderv2_linear.py` (full report `data/flushfaderv2/linear.md`). Model = A3's three IRLS component
fits (logit win rate, log mean win, log mean loss) on the A3 design + the selected candidates; continuous candidates
winsorised 1 / 99 % and standardised on the fit set; K ≥ 77 and dslo ≥ 4 % as flags (V2-2 / V2-4 edges, read off the
full sample). Score = sized PF−1 (mean-1 weights on the apply set, clip [0.25, 4]); fwd = fit 2020–23 → 2024–26,
mirror = fit 2024–26 → 2020–23; selection by the mean of the two.

**Baselines:**

| model | fwd | mirror | mean | in-sample |
|---|---|---|---|---|
| flat | 0.442 | 0.613 | 0.528 | 0.536 |
| A3 | 0.529 | 0.728 | 0.628 | 0.634 |
| A3 ^ 1.25 (steepness control) | 0.550 | 0.759 | 0.654 | 0.661 |
| A3 ^ 1.5 (steepness control) | 0.570 | 0.787 | 0.679 | 0.687 |

**Forward selection:**

| step | + candidate | fwd | mirror | mean | Δ mean | sized net (in-sample) | sized maxDD | sized PF by year 2020 … 2026 |
|---|---|---|---|---|---|---|---|---|
| 0 | (A3) | 0.529 | 0.728 | 0.628 | — | 24,893 | 471 | 1.96 1.79 1.45 1.40 1.56 1.52 1.56 |
| 1 | **dslo ≥ 4 %** | 0.638 | 0.882 | 0.760 | **+0.132** | 28,309 | 286 | 2.16 1.89 1.57 1.53 1.76 1.59 1.75 |
| 2 | s20 | 0.659 | 0.902 | 0.781 | +0.021 | 29,074 | 307 | 2.20 1.91 1.55 1.54 1.81 1.61 1.75 |
| 3 | speed | 0.667 | 0.906 | 0.786 | +0.005 | 29,835 | 307 | 2.21 1.92 1.55 1.55 1.83 1.63 1.73 |
| 4 | K ≥ 77 | 0.663 | 0.917 | 0.790 | +0.004 | 29,948 | 284 | 2.24 1.94 1.55 1.59 1.84 1.64 1.68 |
| 5 | rflow | 0.671 | 0.924 | 0.798 | +0.008 | 30,128 | 255 | 2.27 1.96 1.54 1.57 1.84 1.65 1.69 |
| 6–12 | haltband, ssf, dlv, esf, K, eff20, dslo ∈ [0.5 %, 3 %) | — | — | 0.799 → 0.793 | ≤ +0.001 each | — | — | — |

Single additions on top of A3 (Δ mean holdout, both holdouts up): dslo ≥ 4 % +0.132 · dslo linear +0.050 · K +0.025 ·
K ≥ 77 +0.023 · esf +0.021 · dlv +0.012 · d20a +0.012 · s20 +0.006 · d1m +0.005 · ssf / speed +0.004. Not up on both:
e9, s5, dsu, rflow, ac1, vcrush, eff20, vexp, accel (−0.016).

**Collinearity (Spearman, |ρ| ≥ 0.5):** speed–d1m +0.88 · K–esf +0.82 · dlv–d20a +0.80 · dlv–esf −0.79 · K–dlv −0.74 ·
d20a–volat −0.63 · s20–rngf +0.62 · d1m–volat −0.61 · rflow–ssf +0.60 · s20–eff20 −0.59 · speed–volat −0.55. The depth
group (speed, d1m, d20a) is largely volat: alone on top of A3 each adds ≤ 0.012.

## V2-4 — dslo: three regimes, and the flag's threshold is a plateau (2026-10-12)

`scripts/equity/flushfaderv2_dslo.py` (`data/flushfaderv2/dslo.log`). dslo = signal_vwap / sess_low − 1, sess_low =
the engine's running session minimum (`RunMinMa`, pushed bar by bar, Intraday.fs:1708) — knowable at the signal.

| dslo | n | PF−1 | avg % | win % | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0, 0.5 %) — AT the session low | 17,704 | 0.38 | 0.41 | 66.0 | 0.54 | 0.74 | 0.18 | 0.13 | 0.29 | 0.37 | 0.13 |
| [0.5 %, 1 %) | 830 | −0.12 | −0.16 | 61.0 | −0.17 | −0.03 | 0.46 | −0.26 | −0.17 | −0.25 | −0.11 |
| [1 %, 2 %) | 1,580 | 0.01 | 0.02 | 62.6 | 0.26 | 0.03 | 0.67 | −0.01 | −0.37 | −0.12 | 0.05 |
| [2 %, 3 %) | 1,299 | 0.18 | 0.21 | 66.0 | 1.02 | −0.05 | 0.01 | 0.87 | −0.26 | 0.30 | 0.55 |
| [3 %, 4 %) | 1,203 | 0.48 | 0.44 | 68.7 | 0.86 | 0.85 | 0.60 | 0.06 | 0.09 | 0.39 | 0.31 |
| [4 %, 5 %) | 1,011 | 0.76 | 0.60 | 68.8 | 1.16 | 1.06 | 1.21 | 0.45 | 0.46 | 0.67 | 0.35 |
| [5 %, 6 %) | 998 | 0.94 | 0.71 | 71.7 | 1.99 | 1.28 | 1.32 | 0.87 | 0.33 | 0.46 | 1.02 |
| [6 %, 8 %) | 1,690 | 0.78 | 0.63 | 70.4 | 1.28 | 0.64 | 0.58 | 0.48 | 1.68 | 0.60 | 0.49 |
| [8 %, 10 %) | 1,417 | 0.71 | 0.62 | 71.0 | 1.18 | 0.74 | 0.18 | 0.16 | 0.62 | 0.91 | 1.07 |
| [10 %, 15 %) | 2,654 | 1.02 | 0.80 | 70.3 | 1.74 | 1.17 | 0.50 | 0.90 | 1.22 | 0.56 | 1.22 |
| [15 %, 20 %) | 1,738 | 1.05 | 0.86 | 71.2 | 1.11 | 1.27 | 1.09 | 1.38 | 0.86 | 0.83 | 0.97 |
| [20 %, 30 %) | 2,068 | 0.97 | 0.92 | 70.3 | 1.44 | 0.86 | 1.10 | 0.51 | 0.97 | 1.16 | 0.66 |
| [30 %, ∞) | 3,062 | 1.02 | 1.15 | 70.8 | 1.40 | 0.88 | 0.78 | 0.92 | 1.31 | 0.54 | 1.70 |

Threshold sweep, A3 + (dslo ≥ t), mean holdout sized PF−1: 0.5 % 0.668 · 1 % 0.689 · 2 % 0.727 · 3 % 0.753 · **4 %
0.760** · 4.6 % 0.757 · 5 % 0.752 · 6 % 0.739 · 8 % 0.732 · 10 % 0.726 · 15 % 0.687; log(dslo + 1 %) linear 0.728.

Reading:
- **A flush to a new 20m low that is still ≥ 4 % above the day's low is the best trade in the book** (PF−1 ~0.75–1.05
  in every band above 4 %, every year positive). A flush AT the day's low is mediocre (0.38). A flush that lands
  JUST above the day's low (0.5–3 %) has no edge at all (3,709 trades, PF−1 ~0.03): price heading back to retest
  the session low tends to go through it.
- The 3–5 % plateau means the flag's threshold is not a fitted knife-edge.
- The gain (+0.132 mean holdout) is ~2.6× what A3 ^ 1.5's extra steepness buys (+0.051): it is information, not
  concentration. Everything after dslo adds ≤ +0.021 per feature and needs a matched-steepness control before it
  is credited.
- The near-low zone [0.5 %, 3 %) as a second flag adds nothing once dslo ≥ 4 % is in (the 0.25 clip already floors
  it). Whether it should be a GATE (≈ 0 edge before costs) is a separate question.

**⏭ LATER (user, 2026-10-12): the near-low zone dslo ∈ [0.5 %, 3 %)** — no gate for now. The user expects it may have
an edge under PASSIVE execution (buy at the bid, sell at the ask), which the next-bar-vwap fill model cannot see.
Revisit with the fill model / the NBBO.

## V2-5 — the MATCHED-STEEPNESS control: dslo is real, s20 is the only feature after it that earns (2026-10-12)

`scripts/equity/flushfaderv2_steep.py` (`data/flushfaderv2/steep.md`). A model M is compared with its BASE's
multipliers raised to the power p at which the base's spread equals M's (spread = the coefficient of variation of the
final, clipped, mean-1 weights on the apply set; matched separately on each holdout). Credit = M − base^p.

**dslo ≥ 4 % itself (base = A3):** fwd 0.638 vs A3^1.39 0.562 (**+0.077**) · mirror 0.882 vs A3^1.48 0.784
(**+0.098**). Half of its raw gain is spread, half is information, both holdouts.

**Each candidate alone on top of A3 + dslo ≥ 4 %** (credit fwd / mirror / mean; ✓ = up on both):

| + candidate | credit fwd | credit mirror | mean |
|---|---|---|---|
| **s20** | +0.016 | +0.013 | **+0.015 ✓** |
| dslo ∈ [0.5 %, 3 %) | +0.011 | +0.015 | +0.013 ✓ (a down-weight; the user parked this zone, see above) |
| rngf | +0.004 | +0.007 | +0.005 ✓ |
| rflow | +0.006 | +0.004 | +0.005 ✓ |
| haltband | +0.004 | +0.003 | +0.003 ✓ |
| ssf | +0.001 | +0.001 | +0.001 ✓ |
| eff20, esf, K ≥ 77 | ≈ 0 | ≈ 0 | ≈ 0 |
| speed, d1m, e9, dlv, d20a, dsu, ac1, K, dslo (linear), s5 | −0.001 … −0.007 | | < 0 |
| vcrush, vexp, accel | −0.006 … −0.021 | | −0.009 … −0.019 |

**The V2-3 forward path, cumulative** (credit vs base^p): + s20 +0.015 · + speed +0.011 · + K ≥ 77 +0.015 · + rflow
+0.022 (+0.014 / +0.030) · then flat to +0.018 with six more. Beyond s20 the extra ~0.007 sits mostly in the mirror.

**Coefficients, all years** (p = win-rate odds ratio, W / L = × mean win / loss, λ = L/W):

| model | dslo ≥ 4 %: p · W · L · λ | s20 (per SD, higher = flatter slope): p · W · L · λ | multiplier range | at the 0.25 floor | > 2× |
|---|---|---|---|---|---|
| A3 (V1) | — | — | 0.52–3.12 | 0 % | — |
| A3 + dslo | 1.252 · 1.003 · 0.879 · **0.877** | — | 0.25–3.73 | 11.9 % | 5.0 % |
| A3 + dslo + s20 | 1.289 · 1.019 · 0.897 · 0.881 | **0.915** · 0.955 · 0.945 · 0.990 | 0.25–4.00 | 10.8 % | 7.7 % |

Sized PF by year, A3 + dslo + s20: fwd (fit 2020–23) 2024 1.74 · 2025 1.56 · 2026 1.73; mirror (fit 2024–26) 2020
2.27 · 2021 1.92 · 2022 1.59 · 2023 1.60 (A3 + dslo: 1.69 / 1.55 / 1.73; 2.21 / 1.90 / 1.61 / 1.57).

Reading:
- **dslo works on BOTH edge components:** a flush ≥ 4 % above the day's low wins more often (odds ×1.25) AND loses
  less per win (λ ×0.88). It is not a magnitude axis.
- **s20 is a win-rate feature:** a steeper 20m downtrend into the flush wins more often (odds ×0.915 per SD of
  flatter slope); its W and L move together (magnitude, already volat's job). Adding it soaks part of volat's
  win-rate term (volat 250+ odds 1.149 → 0.908).
- **The depth group (speed, d1m, d20a, dlv) earns nothing once dslo is in** — the V2-3 raw gains for speed were
  spread.
- **New: the 0.25 floor binds for ~11 % of the book** (A3 never reached it). Those are the at-low or near-low,
  shallow-slope trades; under a 20-unit cap they matter for capacity, not just for PF.
- **Candidate V2 sizing = A3 + dslo ≥ 4 % + s20** (11 parameters per component). rflow / K ≥ 77 / rngf add
  ≤ +0.007 each, below what this test can separate from noise.

**User, 2026-10-12 (after reading V2-3 – V2-5): ADD dslo ≥ 4 % and s20 to the sizing model. Gate the weak near-low
zone away — [0.5 %, 2 %], or should the bottom end be 0.2 or 0.1 %?** (This supersedes the earlier "later" note for
the zone; the passive-fill question stays open for whatever the gate removes.)

## V2-6 — the near-low gate: the bottom end is "anything off the exact low" (2026-10-12)

`scripts/equity/flushfaderv2_dslo_gate.py` (`data/flushfaderv2/dslo_gate.log`). Gate variants are REPLAYED from the
209,252 engine-level trips (`broad_reference_trips.parquet`, + dslo from the slice; the base replay reproduces the
reference book's 37,254 exactly). Flat, net of the $0.001/sh/side credit.

**Fine bands below 3 % (base book, fixed membership):**

| dslo | n | PF−1 | avg % | win % | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **= 0 (the signal bar IS the session low)** | 16,888 | **0.41** | 0.44 | 66.4 | 0.58 | 0.78 | 0.18 | 0.15 | 0.34 | 0.38 | 0.14 |
| (0, 0.1 %) | 177 | −0.03 | −0.04 | 54.2 | −0.02 | −0.49 | 0.94 | −0.32 | 0.52 | 0.03 | 0.53 |
| [0.1 %, 0.2 %) | 155 | 0.03 | 0.03 | 57.4 | −0.41 | 1.55 | 0.68 | −0.28 | −0.68 | −0.09 | 3.40 |
| [0.2 %, 0.3 %) | 168 | −0.09 | −0.11 | 59.5 | 0.33 | −0.31 | 0.29 | −0.03 | −0.49 | 2.58 | −0.55 |
| [0.3 %, 0.5 %) | 316 | **−0.21** | −0.29 | 56.6 | 0.00 | 0.23 | −0.13 | −0.61 | −0.54 | −0.10 | −0.35 |
| [0.5 %, 0.75 %) | 422 | **−0.21** | −0.29 | 56.9 | −0.40 | 0.27 | 0.30 | −0.46 | −0.27 | −0.28 | −0.44 |
| [0.75 %, 1 %) | 408 | −0.01 | −0.02 | 65.2 | 0.18 | −0.25 | 0.70 | 0.06 | −0.02 | −0.21 | 0.33 |
| [1 %, 1.5 %) | 798 | 0.00 | 0.00 | 61.5 | 0.48 | 0.17 | 0.31 | −0.11 | −0.29 | −0.20 | −0.27 |
| [1.5 %, 2 %) | 782 | 0.03 | 0.03 | 63.7 | 0.10 | −0.08 | 2.42 | 0.09 | −0.44 | −0.03 | 0.54 |
| [2 %, 2.5 %) | 633 | 0.15 | 0.18 | 65.1 | 1.94 | −0.00 | −0.23 | 0.65 | −0.47 | 0.23 | 0.77 |
| [2.5 %, 3 %) | 666 | 0.21 | 0.24 | 66.8 | 0.49 | −0.09 | 0.44 | 1.21 | −0.04 | 0.38 | 0.40 |
| ≥ 3 % | 15,841 | 0.90 | 0.81 | 70.4 | 1.35 | 0.95 | 0.73 | 0.66 | 0.95 | 0.66 | 0.96 |

**Gate variants, replayed:**

| drop dslo ∈ | n | PF−1 | net % | Δ n | Δ net | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| — (base) | 37,254 | 0.536 | 20,347 | — | — | 0.80 | 0.71 | 0.39 | 0.33 | 0.45 | 0.44 | 0.43 |
| [0.5 %, 2 %] (the user's first draft) | 36,094 | 0.564 | 20,631 | −1,160 | +284 | 0.84 | 0.75 | 0.38 | 0.35 | 0.49 | 0.48 | 0.44 |
| [0.3 %, 2 %] | 35,901 | 0.570 | 20,703 | −1,353 | +356 | 0.85 | 0.75 | 0.38 | 0.35 | 0.51 | 0.48 | 0.45 |
| [0.2 %, 2 %] | 35,810 | 0.576 | 20,808 | −1,444 | +461 | 0.85 | 0.77 | 0.38 | 0.36 | 0.51 | 0.49 | 0.45 |
| [0.1 %, 2 %] | 35,729 | 0.579 | 20,852 | −1,525 | +505 | 0.85 | 0.77 | 0.39 | 0.36 | 0.51 | 0.50 | 0.45 |
| [0.05 %, 2 %] | 35,680 | 0.581 | 20,888 | −1,574 | +541 | 0.86 | 0.77 | 0.39 | 0.36 | 0.51 | 0.50 | 0.45 |
| **(0, 2 %]** | 35,612 | **0.583** | **20,925** | −1,642 | **+578** | 0.86 | 0.77 | 0.39 | 0.36 | 0.52 | 0.50 | 0.45 |
| [x, 3 %] variants | | PF−1 +0.01 higher | | ~ −950 more | ~ −100 to −130 less net | | | | | | | |

Reading:
- **The exact session low is its own class** (16,888 @ 0.41, every year positive). Every band ABOVE it and below
  2 % is at or below zero — [0.3 %, 0.75 %) is the worst at −0.21, i.e. the 0.1–0.5 % band the user asked about is
  bad too, not a gray zone. So the gate's bottom end is **> 0**: "the flush is not at the session low, but within
  2 % of it". Each step down from 0.5 % to 0 adds net (+284 → +578) and PF−1, monotonically.
- The upper end: 2 % beats 3 % on net (the [2 %, 3 %) bands are +0.15 / +0.21).
- **The replay matters:** the (0, 2 %] zone holds 3,226 base-book trades, but the gated book is only 1,642 smaller —
  half the freed slots are refilled by a later trip of the same ticker-day. This is the mc=1 slot mechanism that made
  the freshness gates "free" in §S49d, and it is something a sizing model on a fixed book cannot do.
- Mechanism (reading): at dslo = 0 the flush is making new session lows (capitulation); a few tenths of a percent
  above the low it is APPROACHING the session low from above, where the low tends to get retested and broken.
- Live note: dslo = 0 is an exact float equality (sess_low is a min over the same pushed vwaps), so the port must
  compute it the same way (min including the signal bar).

Thresholds read off the full sample, but the curve is monotone in lo with no spike; a holdout check comes with the
refit of the sizing model on the gated book.

## V2-7 — THE V2 BOOK (broad + the near-low gate) and the sizing model refitted on it (2026-10-12)

`scripts/equity/flushfaderv2_book.py` → `data/flushfaderv2/v2_book.parquet` (`v2_book.log`); the model scripts take
it with `--book data/flushfaderv2/v2_book.parquet --tag v2` (`linear_v2.md`, `steep_v2.md`, `dslo_v2.log`).

**The V2 book** = the 209,252 engine-level trips − dslo ∈ (0, 2 %], then the reference construction (raw px ≥ $1,
mc=1 greedy per ticker-day): **35,612 trades, flat PF−1 0.583, net 20,925 %** (V1: 37,254 @ 0.536 / 20,347).

**The gate's cutoffs, held out** (pick (lo, hi] on one year block by flat net, apply to the other; grid lo 0 … 1 %, hi
1 … 4 %):

| pick on | chosen | gain on the fit block | gain on the other block | other block PF−1 base → gated |
|---|---|---|---|---|
| 2020–23 | (0, 1 %] | +149 | **+198** (2024–26) | 0.442 → 0.465 |
| 2024–26 | (0, 3 %] | +531 | **−50** (2020–23) | 0.613 → 0.666 |
| (the ruled (0, 2 %]) | — | +145 (2020–23) | +432 (2024–26) | — |

Both blocks pick **lo = 0** (and every row of the grid falls as lo rises): the bottom end holds out. The top end does
not pin down (1 % vs 3 %); 2 % is the one choice that gains net on both blocks.

**Sizing on the V2 book** (sized PF−1, fwd = fit 2020–23 → 2024–26, mirror = fit 2024–26 → 2020–23):

| model | fwd | mirror | mean | in-sample sized net | sized maxDD | worst sized trade |
|---|---|---|---|---|---|---|
| flat | 0.494 | 0.657 | 0.576 | 20,925 | — | — |
| A3 | 0.574 | 0.757 | 0.666 | 24,931 | 462 | −115 |
| A3 ^ 1.5 (steepness control) | 0.614 | 0.811 | 0.713 | — | — | — |
| A3 + dslo ≥ 4 % | 0.660 | 0.888 | 0.774 | 27,411 | 295 | — |
| **A3 + dslo ≥ 4 % + s20 (V2 sizing)** | **0.675** | **0.899** | **0.787** | **28,014** | **312** | −161 |

- Matched-steepness credit, on the V2 book: **dslo ≥ 4 % +0.059 / +0.078** (vs A3^1.33 / A3^1.48) · **s20 on top
  +0.011 / +0.008**. Both smaller than on the V1 book (the gate took the zone dslo's flag used to separate), both
  still positive on both holdouts.
- dslo flag threshold on the V2 book: 3 % 0.767 · **4 % 0.774** · 4.6 % 0.772 · 5 % 0.767 · 6 % 0.755 — the same
  plateau, 4 % stays.
- After s20 the forward path adds K ≥ 77 (+0.008) and rflow (+0.007), the credit again mostly in the mirror; nothing
  else earns. Not adopted.
- V1 → V2 end to end (V1 = base book + A3, V2 = gated book + A3 + dslo + s20): **fwd 0.529 → 0.675, mirror 0.728 →
  0.899**; in-sample sized net 24,893 → 28,014 (+12.5 %), maxDD 471 → 312, at equal mean exposure per trade.
- ⚠ The worst single sized trade is worse (−115 → −161 %·units): an up-weighted dslo ≥ 4 % trade with a large
  overnight loss. The tail, not the PF, is what the capped replay has to check.

**V2 sizing coefficients (all years, V2 book):**

| term | p odds ratio | W × | L × | λ × |
|---|---|---|---|---|
| intercept | p = 0.695 | W = 1.68 % | L = 2.29 % | λ = 1.36 |
| gap 1–3 | 0.917 | 0.984 | 1.065 | 1.083 |
| gap 4–39 | 0.891 | 1.016 | 1.107 | 1.089 |
| volat 60–90 | 0.993 | 1.427 | 1.377 | 0.965 |
| volat 90–140 | 0.951 | 1.914 | 1.855 | 0.969 |
| volat 140–250 | 0.915 | 2.537 | 2.459 | 0.969 |
| volat 250+ | 0.872 | 3.843 | 3.424 | 0.891 |
| rate600 fast | 0.890 | 1.031 | 1.038 | 1.007 |
| S tier | 1.002 | 1.029 | 0.918 | 0.892 |
| **dslo ≥ 4 %** | **1.256** | 1.009 | 0.903 | **0.895** |
| **s20 (per SD, winsorised 1/99 %)** | **0.920** | 0.954 | 0.939 | 0.983 |

Multiplier range 0.25–4.00; 5.0 % of the book at the 0.25 floor (11 % before the gate), 5.9 % above 2×. Sized PF by
year: fwd 2024 1.76 · 2025 1.58 · 2026 1.74; mirror 2020 2.25 · 2021 1.94 · 2022 1.57 · 2023 1.60.

Production note: s20 enters standardised — the port needs the fit set's winsorising quantiles, mean and SD as
constants beside the betas (A3 needed only band edges).

## V2-8 — the CAPPED replay, V1 vs V2 (2026-10-12): +14 % net, −30 % max DD, worst day −296 → −199, 80 of 80 months

`scripts/equity/flushfaderv2_capped_replay.py` (`data/flushfaderv2/capped_replay.md`). The §S49ag ruled replay: the
168,632 engine-level trips with raw px ≥ $1, chronological, first come first served; one CONCURRENT position per
ticker-date (mc=1 re-entries allowed); at most 20 units open (the Oms's MaxUnits); a trip that would breach the cap is
skipped. V1 = all trips, A3 sizes; V2 = − dslo ∈ (0, 2 %] (11,177 trips), A3 + dslo ≥ 4 % + s20 sizes. Sizes fitted on
each system's uncapped book (which this script reproduces: 37,254 / 35,612); cross-fit = every year's sizes come from
the other year block. P&L in position units (100 = one flat position); % equity at the Oms's 7.5 % per unit.

| replay (cap 20) | trades | PF | net units | avg | max DD (% eq) | worst day (% eq) | worst trade (% eq) | days / weeks prof. | months prof. | peak open units med / p90 | refused by cap |
|---|---|---|---|---|---|---|---|---|---|---|---|
| V1 flat | 36,240 | 1.532 | 19,744 | 0.54 | 422 (32 %) | −339 (−25.4 %) | −84 (−6.3 %) | 76.1 / 86.2 % | 76/80 | 4.0 / 10.0 | 3.1 % |
| V1 A3 in-sample | 36,456 | 1.627 | 24,298 | 0.67 | 430 (32 %) | −309 (−23.1 %) | −132 (−9.9 %) | 77.1 / 87.9 % | 78/80 | 4.4 / 9.4 | 2.5 % |
| V1 A3 cross-fit | 36,522 | 1.625 | 24,300 | 0.67 | 443 (33 %) | −296 (−22.2 %) | −221 (−16.6 %) | 77.0 / 86.2 % | 77/80 | 4.4 / 9.5 | 2.4 % |
| V2 flat (gated) | 34,695 | 1.579 | 20,335 | 0.59 | 394 (30 %) | −315 (−23.6 %) | −84 (−6.3 %) | 77.2 / 85.0 % | 77/80 | 4.0 / 9.0 | 3.0 % |
| V2 in-sample | 34,998 | 1.792 | 27,606 | 0.79 | 327 (25 %) | −223 (−16.8 %) | −161 (−12.1 %) | 78.6 / 89.0 % | 80/80 | 4.1 / 8.3 | 2.0 % |
| **V2 cross-fit** | 35,038 | **1.782** | **27,615** | **0.79** | **308 (23 %)** | **−199 (−15.0 %)** | −221 (−16.6 %) | 78.8 / 89.3 % | **80/80** | 4.2 / 8.4 | 1.9 % |
| V1 A3 cross-fit, cap 10 | 35,300 | 1.622 | 23,502 | 0.67 | 325 (24 %) | −202 (−15.1 %) | −221 | 76.5 / 86.2 % | 78/80 | 4.4 / 9.3 | 6.1 % |
| V2 cross-fit, cap 10 | 34,145 | 1.796 | 27,308 | 0.80 | 274 (21 %) | −199 (−15.0 %) | −221 | 78.9 / 89.3 % | 80/80 | 4.2 / 8.4 | 4.9 % |

**By year (cross-fit):**

| year | V1 net | V2 net | Δ | V1 PF | V2 PF | V1 worst day | V2 worst day | V1 max DD | V2 max DD |
|---|---|---|---|---|---|---|---|---|---|
| 2020 | 5,382 | 6,101 | +720 | 1.99 | 2.26 | −296 | −178 | 443 | 307 |
| 2021 | 5,534 | 5,929 | +396 | 1.79 | 1.92 | −128 | −111 | 193 | 267 |
| 2022 | 1,536 | 1,790 | +253 | 1.47 | 1.56 | −91 | −130 | 292 | 308 |
| 2023 | 1,387 | 1,824 | +437 | 1.42 | 1.60 | −89 | −62 | 137 | 79 |
| 2024 | 3,492 | 4,317 | +826 | 1.54 | 1.76 | −202 | −199 | 224 | 223 |
| 2025 | 4,038 | 4,407 | +369 | 1.49 | 1.57 | −171 | −156 | 177 | 177 |
| 2026 | 2,931 | 3,246 | +315 | 1.58 | 1.74 | −86 | −68 | 198 | 137 |

Worst days (cross-fit): V1's ten worst are all better or equal under V2 except 2022-03-08 (−91 → −130); V2's own worst
ten add 2021-01-28 (GME; −111 vs −56), 2024-09-30 (−99 vs −55), 2020-01-22 (−79 vs −42). The worst V2 trades: IFBD
2024-02-27 −55 % at 4 units (−221, identical in V1: dslo = 0, A3 already maxed it), OCG 2025-12-12 −42 % at 4 units
(identical in V1); the ones V2 made worse by up-sizing: XHG 2024-09-30 (3.18 vs 2.10 units), NTRP 2020-01-22 (1.98
vs 0.96), TDTH 2025-01-31 (2.65 vs 1.72).

**V2 P&L by size band (cross-fit):** 0.25× 2,921 trades @ PF 1.38 (1 % of net) · 0.25–0.5× 6,317 @ 1.32 (3 %) ·
0.5–1× 10,312 @ 1.32 (10 %) · 1–1.5× 7,752 @ 1.70 (24 %) · 1.5–2× 5,310 @ 2.12 (32 %) · 2–3× 2,162 @ 2.41 (24 %) ·
3–4× 264 @ 2.02 (6 %). The PF rises with the size up to 3× — the model ranks; the 3–4× band dips (264 trades, the
two −221 / −166 trades sit there).

Reading:
- **Under the real cap V2 beats V1 on every line, out of sample:** net +13.6 % (24,300 → 27,615), PF 1.625 → 1.782,
  max DD 443 → 308 (33 % → 23 % of equity at 7.5 %/unit), worst day −296 → −199 (−22 % → −15 %), months profitable
  77 → 80 of 80, every year more net and higher PF.
- **The worst-trade worry of V2-7 does not survive cross-fitting:** the −161 was an in-sample artefact; the
  cross-fit worst trade (IFBD −221) is the same trade at the same 4 units in V1.
- The cap binds less (1.9 % of trips refused vs 2.4 %): fewer trades, smaller in the tail. At cap 10 V2 keeps 99 % of
  its cap-20 net (V1 keeps 97 %).
- Worst-day risk at the Oms's sizing is still large: −15 % of equity on 2024-02-27 (and −13 % on 2020-03-18). That is
  the user's stated tolerance ("occasional 10–30 % losing days"), not a free lunch.

## V2-9 — the PRODUCTION reference in double precision (2026-10-12; user: "replace FlushFader with V2 in production")

`scripts/equity/flushfaderv2_reference.py` — the broad-book reference (flushfader_broad_reference.py's mask,
calendar-aware entry window) − dslo ∈ (0, 2 %], with dslo and s20 read in DOUBLE from the base corpus (sess_low,
ols_slope_1200), not the float32 slice. Outputs in `data/flushfaderv2/`: `flushfaderv2_reference_{trips,book}.parquet`
(the Oms's Parity targets), `v2_coefficients.json` (committed: β vectors, BasePf1, the s20 constants, the thresholds,
200 pin trades for the F# tests); `_10d` twins for the trades-tape seal (coefficients APPLIED, not refitted).

| set | base_v19 (full period) | base_10d |
|---|---|---|
| engine-level trips, broad → V2 | 209,252 → **195,764** (gate −13,488) | 1,180 → **1,112** (−68) |
| book (raw px ≥ $1, mc=1) | **35,612** (= the slice study, identical keys) | **192** (V1: 201) |
| book PF net of credit, flat → sized | 1.583 → 1.798 | 1.676 → 2.027 |
| multiplier mean / range / at 0.25 | 1.0021 / 0.25–4.00 / 5.0 % | 1.062 / 0.25–3.55 / 4.7 % |

The coefficients reproduce V2-7's table to every printed digit (the float32 slice made no difference on this book).
s20 constants: winsorise [−105.830750, 10.477523] bp/min, mean −26.001895, SD 21.187586; BasePf1 0.5955897035.
The mean multiplier is 1.0021, not 1: the 0.25 floor binds (A3's never did), lifting the clipped mean.
NaN dslo / NaN s20 at an engine-level trip: 0 / 0 over the full period (the live port still guards: a NaN s20 enters
as z = 0).

## V2-10 — V2 in the Oms, SEALED (2026-10-12); deployed for the 10-12 session

Private repo: `Engine/FlushFader.fs` (SessLow + OlsSlope1200 restored, the near-low gate `NearLowLo/Hi` = 0 / 0.02),
`Engine/Sizing.fs` (the V2 model, betas + constants pasted from `v2_coefficients.json`), `Sizer.UnitPf1` = 0.519 (one
PF−1 scale for every system, user ruling: V2 positions average ~1.15 units), Parity's signals (+ sess_low,
ols_slope_1200) and book (+ dslo, dslo_flag, s20, s20_z, pf1_fitted).

| seal | result |
|---|---|
| 10d `--from-bars`, gate OFF, vs V1 `broad_reference_trips_10d` | 1,180 = 1,180, zero-diff; every signal column vs base_10d zero-diff incl. sess_low / ols_slope_1200 (`n`, a candidate column, 1 row) |
| 10d `--from-bars`, gate ON, vs V2 reference | trips 1,112 = 1,112 · book 192 = 192 (multiplier Δ ≤ 4.4e-15) |
| 10d trades tape `--gate --ms-precision` | trips 1,112 = 1,112 · book 192 = 192; the gate's universe = the table on all 10 days |
| full period 2020-01-02 .. 2026-08-21 `--from-bars` | **book 35,612 = 35,612** (multiplier Δ ≤ 7.6e-15); trips 195,764 shared, zero-diff (one exit_px 2e-16), + 34 Oms-only trips = ONE ticker-day, PSNYW 2025-08-27 (a warrant at $0.55: in today's candidate table, absent from base_v19 built 09-08 — universe drift, not the engine; all below $1, so no book effect) |
| Oms tests | 100 / 100 (SizingTests: betas + constants digit for digit, 200 pins to 1e-9; FaderTests AXTI restated — V2's continuous s20 gives overlapping trips different pf1) |
| controls | the session-low pushed AFTER the gate: sess_low differs on 589 / 1,112 (the trip set does NOT move — only the record would drift); s20 without the winsorising: 2 tests fail (pin 92 Δ 0.198) |

## V2-11 — LISTING AGE: the floor moves to barnum 11, sessions 11–21 sized × 0.8 (2026-10-18)

**Why.** Since S40e research has cut a listing's first 21 sessions (`barnum >= 22`). The live Oms applied no barnum
floor at all, which the first live week's execution review found: a barnum-2 ticker traded live with no research trip.
The user asked how the current systems do on the early sessions.

**How.** The production engine (the private Parity harness) was run on only the barnum < 22 ticker-days of
`mr_candidate_1s_v2` (8,762 ticker-days, 2016-08 … 2026-10). Every book is one position per ticker-day, so the slice is
disjoint from the reference book.

| barnum | trades | PF−1 | sized PF−1 | mean multiplier |
|---|---|---|---|---|
| 1 (first session) | 100 | 0.31 | 0.25 | 0.99 |
| 2–5 | 851 | 0.10 | 0.26 | 0.98 |
| 6–10 | 414 | 0.23 | 0.42 | 1.01 |
| 11–21 | 628 | 0.32 | **0.59** | 0.98 |
| ≥ 22 (the V2 book) | 35,612 | 0.53 | **0.73** | 1.00 |

- The young sessions were flat to negative in 2022–2024 (barnum 1–21: 2022 0.01 · 2023 −0.30 · 2024 −0.02). They have
  run at about 0.22–0.26 per half-year since 2025, and matched the book in 2020–21. The edge looks regime-dependent.
- The model sizes young listings like everything else (mean multiplier ≈ 1): it has no listing-age term.
- ⚠ **barnum 1 never reaches an engine in research.** The candidate SELECT's `rvol_0945_honest >= MinRvol0945` (0.0,
  meant as no floor) is NULL on a first session (no volume history), and NULL >= 0 is false. Under any barnum floor
  ≥ 2 this changes nothing.

**User ruling 2026-10-18:**
- The FlushFader floor moves to **barnum ≥ 11**.
- A YOUNG trade (barnum 11–21) is sized at **0.8 × the model's PF−1** (0.59 / 0.73 = 0.81).
- The other systems keep their gates as they are.

**The reference rebuilt.**
- `scripts/equity/flushfader_run_base_v19_young.sh` splices the young sessions onto base_v19: the exact base_v19
  command on the barnum 11–21 ticker-days, `--min-barnum 11` → `data/equity/flushfader/young/base_v19_young` (76,550
  engine trips on 1,790 ticker-days).
  - Control: the same command on five ordinary days (2025-03-03 … 07, barnum ≥ 22, from the same v2 universe)
    reproduces base_v19 exactly: 32,498 = 32,498 trips, 0 differences in entry, exit or return.
- `flushfaderv2_reference.py` joins `barnum` from the universe, gates `barnum >= --min-barnum` (11), flags
  `young = barnum < --young-below` (22) and sizes `pf1_fitted = --young-scale (0.8) × pf1_model` when young.
  - The model is fitted on the barnum ≥ 22 book only, so the betas, BasePf1, s20 constants and pins are V2-7's.
  - Control: `--min-barnum 22` on base_v19 alone reproduces the previous reference: `v2_coefficients.json` identical in
    every key, the book identical in every column, trips 195,764 = 195,764.
  - The previous outputs are kept in `data/flushfaderv2/pre_barnum_2026-10-18/`.

| set | n | PF net of credit | sized PF | net % |
|---|---|---|---|---|
| the book (2020-01-02 … 2026-08-21) | **36,200** | 1.579 | 1.796 | 21,172 |
| · barnum ≥ 22 (= the V2 book; prices identical, multiplier Δ ≤ 7e-16) | 35,612 | 1.583 | 1.798 | 20,925 |
| · young, barnum 11–21, × 0.8 | 588 | 1.352 | 1.634 | 247 |

Young trades by year: 2020 61 · 2021 166 · 2022 105 · 2023 48 · 2024 63 · 2025 106 · 2026 39. Engine-level trips:
195,764 → 198,573.

⏭ Production next: barnum in the Oms's universe, the gate and the 0.8 in the engine/sizing, the Parity re-seal
against this reference.

## V2-12 — the 5-day ATR % and ADR % (2026-10-18): no edge gradient; the extreme tail is a risk question, not a PF one

**Why (user).** WFF 2026-10-14, a FlushFader long, fell from 2.05 to 0.16 in the last hour and was carried into a
0.15 open (−92.7 %, as research also has it). WFF had enormous daily ranges, unlike an ordinary catalyst breakout.
Would a 5-day ATR % (and ADR %) filter have kept it out?

**Definitions (causal: only the 5 sessions before D, in the P·n scale).**
- ATR5 % = mean(max(H, C₋₁) − min(L, C₋₁)) / C(D−1).
- ADR5 % = mean(H / L − 1).
- Spearman ρ between the two = 0.95.
- `scripts/equity/atr5_filter.py`; full tables in `data/equity/atr5/atr5.md`.

**WFF 10-14: ATR5 718 %, ADR5 458 %, above 99.99 % / 99.96 % of the FlushFader book.** (AEHL, the week's other
loss, was ordinary: ATR5 17.6 %, the 61st percentile.)

**FlushFader (the 36,200 book): flat deciles.**
- ATR5 deciles PF−1 0.41–0.64, ADR5 0.43–0.62, no slope. Only the extreme tail differs:

| ATR5 | trades | PF−1 | sized PF−1 | Σ sized %·units |
|---|---|---|---|---|
| ≥ 100 % | 333 | 0.30 | 0.19 | +121 |
| ≥ 200 % | 82 (47 ticker-days, 34 tickers) | −0.01 | −0.53 | −149 |
| ≥ 400 % | 28 | −0.30 | −0.76 | −151 |

- ATR5 ≥ 200 % by year (trades / sized PF−1): 2020 4 / −0.60 · 2021 7 / 1.35 · 2022 15 / 0.20 · 2023 14 / 5.12 · 2024 25 /
  −0.49 · 2025 8 / −0.92 · 2026 9 / −0.11. Most of the −149 is 2025 (−151).
- ADR5 200–400 %: 117 trades @ −0.08 (it holds the book's worst trade, TDIC −84.3 %, ADR5 221 % but ATR5 26 %).
- **The threshold does not hold out.** Picked on 2020–23 (by the sized gain of the cut), the best is 600 %, ≈ +1;
  applied to 2024–26 it gains +173 from 6 trades. Picked on 2024–26, it is 200 %; applied to 2020–23 it LOSES −31.
- The book never had a WFF-sized loss in this zone: the worst trade at ATR5 ≥ 200 % is −42 %.

**The other systems: high range is neutral-to-good** (they are reversion trades that like volatility).

| system | ATR5 top decile PF−1 (vs all) | ATR5 ≥ 100 % |
|---|---|---|
| LowFader | 6.50 (4.15) | 0 trades |
| SpikeFader | 1.48 (1.33); top 1 % 1.51 | 31 @ 1.73 |
| Snoozer long | 3.72 (1.78) | 21 |
| Snoozer short | 0.82 (2.34), but the top 2 % is 6.11 | 9 |

ADR5's top 2 % is weak for SpikeFader (62 trades @ 0.49) and the Snoozer long (19 @ 0.44); small, not pursued.

**Reading.**
- Neither measure grades the edge in any system.
- For FlushFader the ATR5 ≥ 200 % tail (~12 trades a year, 0.23 % of the book) is break-even raw and negative sized
  (the model up-sizes it: mean multiplier 1.12–1.34). A gate there costs almost nothing in expectancy and would have
  kept WFF out. But the backtest cannot show it earns anything: the threshold does not hold out, and the sized gain
  sits in a handful of 2025 trades.
- So it is a RISK rule (the user's call), not a measured edge.

## V2-13 — THE ATR5 GATE: no FlushFader entry at a 5-day ATR % >= 400 % (user 2026-10-18)

**User ruling:** "Once you get burnt by a move like the one in WFF once, you learn quickly not to touch the stove again."
A risk rule; V2-12 shows it cannot be credited with edge. Only FlushFader is gated; the other systems as they are.

**The bands it chooses between** (the V2 book, ATR5 by fixed band):

| ATR5 | trades | ticker-days | PF−1 | sized PF−1 | median % | Σ raw % | Σ sized | worst % |
|---|---|---|---|---|---|---|---|---|
| [100, 200) | 251 | 124 | 0.45 | 0.79 | +1.15 | +139 | +270 | −47.3 |
| [200, 400) | 54 | 32 | 0.29 | 0.02 | +1.03 | +21 | +2 | −11.3 |
| **[400, ∞)** | 28 | 15 | −0.30 | −0.76 | −0.01 | −22 | −151 | −41.7 |

- [400, ∞) is concentrated (11 tickers; HUDI, TOP and ELPW are half of it). Its −151 is essentially ONE trade: OCG
  2025-12-12, −41.7 % at multiplier 3.89 (−162). Without OCG the band is about +20 raw / +13 sized.
- The two catastrophic losses on record in this zone, OCG and WFF (ATR5 718 %), both sit in it. [200, 400) never lost
  more than 11 %.

**Implementation.**
- `scripts/equity/atr5_candidates.py` writes the ATR5 of every universe ticker-day:
  `data/equity/atr5/candidates_atr5.parquet`, 1,478,663 rows, 60 at ≥ 400 %.
  - The definition is the daily rows before D, in the P·n scale, with no episode split; NaN with fewer than 6 prior rows.
  - ⚠ DuckDB's `greatest` / `least` skip NULLs, so a row without a previous close would get a "true range" of H − L. The
    TR is set to NULL there instead. That changed 343 ticker-days, none at barnum ≥ 11.
- `flushfaderv2_reference.py --max-atr5 4.0` drops ticker-days at ATR5 ≥ 4.0; a NaN ATR5 passes.
  - ⭐ The production reference is now built with `--coef data/flushfaderv2/v2_coefficients.json`: the V2-7 model is
    APPLIED, because a refit would see the gated book.
  - Control: `--max-atr5 inf` reproduces the barnum-11 book in every column.
  - The previous outputs are kept in `data/flushfaderv2/pre_atr5_2026-10-18/`.

| reference | trades | PF net of credit | sized PF | net % | sized net % |
|---|---|---|---|---|---|
| barnum 11 (V2-11) | 36,200 | 1.579 | 1.796 | 21,172 | 28,400 |
| **+ the ATR5 gate** | **36,172** | 1.581 | 1.804 | 21,192 | 28,548 |

The gate removes exactly the 28 trades on 15 ticker-days of the [400, ∞) band (Σ sized −151.3): 3,285 engine-level
trips on 46 ticker-days, and nothing else moves.

## V2-14 — volatility as ONE winsorised continuous term instead of 4 band dummies (2026-10-18)

**Question (user).** Would a single winsorised continuous volatility term, or its square root, beat the four volat
band dummies of the V2 model?

**Setup.** `scripts/equity/flushfaderv2_volat_shape.py` (`data/flushfaderv2/volat_shape.md`).
- The book: the production reference, barnum ≥ 22, ATR5-gated (35,584 trades).
- The model: V2's design with only the volatility columns swapped: 4 dummies → one standardised term,
  `clip(volat, 40, cap)` in linear, sqrt or log form, caps 150–600 bp, the fit set's q95 / q99 / q99.5, or none.
- Scores, V2's method:
  - sized PF−1 on the two year-block holdouts;
  - the matched-steepness credit against the bands;
  - the OUT-OF-SAMPLE fit of each component: the p log-loss, and the W / L quasi-Poisson deviance.
- volat on the book: median 60 bp, p95 135, p99 198, max 628.

| volatility term | params / component | fwd | mirror | mean | credit (fwd / mirror) | Δ W dev ×1e3 | Δ L dev ×1e3 |
|---|---|---|---|---|---|---|---|
| V2: 4 band dummies | 4 | 0.691 | 0.908 | 0.800 | — | — | — |
| **none** | 0 | 0.705 | 0.917 | **0.811** | +0.020 / +0.006 | +185 / +137 | +255 / +155 |
| linear, cap q95 (≈ 135 bp) | 1 | 0.707 | 0.912 | 0.810 | +0.020 / +0.003 | −10 / −10 | −37 / −32 |
| linear, no cap | 1 | 0.704 | 0.908 | 0.806 | +0.020 / −0.003 | +52 / +9 | +36 / −1 |
| sqrt, cap q99 | 1 | 0.706 | 0.914 | 0.810 | +0.019 / +0.003 | −16 / −18 | −35 / −32 |
| **log, cap 250 bp** | 1 | 0.706 | 0.914 | 0.810 | +0.017 / +0.004 | **−26 / −20** | **−43 / −33** |

(p log-loss: every single-term model −1.0 to −1.7 ×1e-4 vs the bands. In-sample sized PF−1: bands 0.812, log 0.814,
none 0.810.)

**Reading.**
- **Volatility does not rank the trades.** Its PF−1 effect is near nil: with NO volatility term the holdout sized PF−1
  is 0.811, slightly above the bands' 0.800. Volatility scales the mean win W and the mean loss L together, and the two
  cancel in PF−1 = p / ((1 − p) · L/W) − 1. The four dummies per component are a little overfit (fwd 0.691 is the
  lowest in the table).
- **Its real job is the magnitudes**, which matter for the average trade (`AvgTradePct`), not for FlushFader's sizing.
  There a continuous term is better than the bands, and the shapes rank log > sqrt > linear:
  - log fits W and L best out of sample, and is cap-insensitive: it self-winsorises.
  - Linear needs a cap; without one it is worse than the bands.
  - The decile table shows why. The bands are flat inside each band (W fitted 1.70 across 40–60 bp while the actual runs
    1.50 → 1.98), and log tracks the actual decile means closely. That is a power law, W ∝ volat^b.
- **Every single term "beats" the bands by +0.010–0.011 credit on both holdouts, but so does dropping volatility
  altogether** (+0.013). The gain is the bands' overfit going away, not information in the shape.

**Recommendation (pending the user).** For sizing it makes no material difference. A one-parameter
`ln(clip(volat, 40, 250 bp))` would replace 12 parameters (4 per component) with 3, with the same PF−1 and better
calibrated W / L, and therefore a better `AvgTradePct`. Changing production would mean a refit (new β, new test pins)
and a re-seal. Worth doing only together with the next refit of the model. → **Done in V2-16 (2026-10-19), adopted.**

## V2-15 — the near-low gate at 3 % instead of 2 %? (2026-10-19)

**Question (user).** Raise the gate from dslo ∈ (0, 2 %] to (0, 3 %]: what is the (2 %, 3 %] band like?

**Setup.** The books are replayed by `flushfaderv2_reference.py --near-low-hi H --coef data/flushfaderv2/v2_coefficients.json`
(new option; `data/flushfaderv2/dslo3/`, tags g2 / g25 / g3), so the freed mc=1 slots refill. Control: g2 = the
production book, 36,172 trades, multipliers identical. Tables: `scripts/equity/flushfaderv2_dslo3.py`
(`data/flushfaderv2/dslo3/dslo3.md`). Flat and sized by the production coefficients, net of the credit.

The band inside the production book (year columns: flat PF−1):

| dslo | n | flat PF−1 | avg % | win % | mean mult | sized PF−1 | sized net | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| = 0 | 18,759 | 0.381 | 0.418 | 66.2 | 0.64 | 0.472 | 6,458 | 0.56 | 0.72 | 0.22 | 0.13 | 0.26 | 0.36 | 0.14 |
| (2 %, 2.5 %] | 648 | 0.155 | 0.180 | 65.3 | 0.51 | 0.178 | 70 | 1.98 | 0.02 | −0.20 | 0.57 | −0.47 | 0.28 | 0.77 |
| (2.5 %, 3 %] | 675 | 0.200 | 0.224 | 66.5 | 0.52 | 0.120 | 52 | 0.49 | −0.07 | 0.28 | 0.96 | −0.02 | 0.38 | 0.35 |
| **(2 %, 3 %]** | 1,323 | **0.178** | 0.202 | 65.9 | 0.51 | 0.147 | **122** | 1.04 | −0.03 | −0.01 | 0.73 | −0.27 | 0.33 | 0.51 |
| (3 %, 4 %) | 1,214 | 0.454 | 0.428 | 68.5 | 0.52 | 0.496 | 305 | 0.87 | 0.77 | 0.60 | 0.06 | 0.09 | 0.37 | 0.25 |
| ≥ 4 % (flagged) | 14,876 | 0.947 | 0.844 | 70.6 | 1.53 | 1.063 | 21,663 | 1.39 | 0.96 | 0.74 | 0.70 | 1.02 | 0.74 | 1.03 |

The gates replayed, production coefficients (in sample; year columns: sized PF−1):

| gate drops dslo ∈ | n | flat PF−1 | flat net | sized PF−1 | sized net | Δ flat net | Δ sized net | sized max DD | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **(0, 2 %] — production** | 36,172 | 0.581 | 21,192 | 0.804 | 28,548 | — | — | 237 | 1.195 | 0.928 | 0.548 | 0.525 | 0.808 | 0.676 | 0.752 |
| (0, 2.5 %] | 35,716 | 0.589 | 21,182 | 0.811 | 28,584 | −10 | +35 | 242 | 1.191 | 0.942 | 0.543 | 0.530 | 0.829 | 0.683 | 0.756 |
| (0, 3 %] | 35,185 | 0.596 | 21,101 | 0.817 | 28,584 | −92 | +35 | 240 | 1.202 | 0.950 | 0.536 | 0.534 | 0.840 | 0.690 | 0.759 |

Reading:
- **The band is thin, not negative.** (2 %, 3 %] holds 1,323 trades at flat PF−1 0.18 (avg +0.20 %), negative in 3 of 7
  years. It is between the near-low zone (≈ 0) and the 3–4 % band (0.45). The sizing already holds it at ~0.5 × the
  mean multiplier, so it carries 0.4 % of the sized net.
- **The refill.** The 3 % gate removes 1,323 trades (sized net +122, flat PF−1 0.18). Later trips of the same
  ticker-days fill 336 of the freed slots (sized net +158, flat PF−1 0.40). Net effect: flat −92, sized +35.
- With the production coefficients it is a wash. On the holdouts, with each gate's own refit, it is a small gain
  (V2-16).

## V2-16 — THE REFIT CANDIDATES on the holdouts: ln volatility and the 3 % gate are each worth ~+0.01 (2026-10-19)

**Setup.** `flushfaderv2_reference.py` gains `--volat log`: the four band dummies become ONE column
`ln(clip(volat, 40, 250 bp))` (V2-14's best shape), unstandardised, so the port needs only the clip. In apply mode the
shape is read from the coefficient file (`volat_shape`, default bands).
- Controls: apply mode with the production file gives the production book exactly. A bands fit with the ATR5 gate off
  reproduces the V2-7 β, BasePf1 and s20 constants exactly (Δ = 0).

Scoring: `scripts/equity/flushfaderv2_refit_holdout.py` (`data/flushfaderv2/dslo3/refit_holdout.md`).
- The method is V2's: fit on one year block's barnum ≥ 22 trades, apply to the other; young trades × 0.8.
- Two weightings:
  - the research multiplier, normalised to mean 1;
  - **PRODUCTION units**: clip(PF−1, 0.125, 2), not rescaled (`Sizer.fs`).
- The normalised view is biased against a gate: the trades a gate removes are low-weight, so the normalisation deflates
  the rest. **Use the production view to compare gates.**

| gate | volatility | sized PF−1 fwd / mirror (mean), normalised | net (sum), normalised | **sized PF−1 fwd / mirror (mean), production units** | **net (sum), production units** | unit-trades | net / unit-trade | max DD fwd / mirror, production units |
|---|---|---|---|---|---|---|---|---|
| (0, 2 %] — production | 4 bands | 0.689 / 0.902 (0.796) | 28,654 | 0.690 / 0.902 (**0.796**) | **16,576** | 20,949 | 0.791 | 96 / 97 |
| (0, 2 %] | ln volat | 0.705 / 0.908 (0.806) | 28,821 | 0.703 / 0.907 (**0.805**) | **16,776** | 21,056 | 0.797 | 72 / 94 |
| (0, 3 %] | 4 bands | 0.711 / 0.904 (0.807) | 28,237 | 0.711 / 0.903 (**0.807**) | **16,837** | 20,973 | 0.803 | 96 / 105 |
| **(0, 3 %]** | **ln volat** | 0.724 / 0.909 (0.817) | 28,298 | 0.721 / 0.908 (**0.815**) | **17,000** | 21,113 | **0.805** | **74** / 101 |

Reading:
- **ln volatility:** +0.009–0.010 sized PF−1 at either gate, both holdouts. Net +1.0–1.2 % in production units. The
  forward max DD falls from 96 to 72–74. The parameter count per component falls from 4 to 1. It keeps V2-14's power law:
  W ∝ volat^0.80, L ∝ volat^0.77 (fit on the 3 % book: 0.798 / 0.768), with odds ×0.93 per e-fold.
- **The 3 % gate:** +0.010–0.011 sized PF−1. In production units net is +1.3–1.6 % on about the same capital (the
  removed trades were sized near the 0.125 floor). The mirror max DD rises by 7–8.
- **Combined (ln volat + 3 %): 0.796 → 0.815, net 16,576 → 17,000 (+2.6 %), forward DD 96 → 74.**
- The full-period fit on the 3 % book: book mean PF−1 (BasePf1) 0.596 → 0.618. In production units, the mean position
  is 0.594 → 0.616 units, the median 0.525 → 0.541, and 0.3 % of trades reach the 2-unit cap.
  `data/flushfaderv2/dslo3/fit_log_g3/v2_coefficients.json`.

| term (fit: 3 % gate, ln volat, n = 34,612) | β_p (odds ×) | β_W (×) | β_L (×) |
|---|---|---|---|
| 1 | +1.129957 (3.096) | −2.568573 (0.077) | −2.166973 (0.115) |
| gap 1-3 | −0.091111 (0.913) | −0.011441 (0.989) | +0.089789 (1.094) |
| gap 4-39 | −0.128391 (0.880) | +0.032263 (1.033) | +0.124652 (1.133) |
| ln volat (clip 40–250 bp) | −0.074331 (0.928) | +0.798220 (2.222) | +0.768031 (2.156) |
| rate600 fast | −0.120362 (0.887) | +0.026258 (1.027) | +0.039696 (1.040) |
| S tier | +0.008054 (1.008) | −0.022309 (0.978) | −0.151376 (0.860) |
| dslo ≥ 4 % | +0.233656 (1.263) | −0.005184 (0.995) | −0.116247 (0.890) |
| s20 (standardised) | −0.087419 (0.916) | −0.031876 (0.969) | −0.048585 (0.953) |

s20 winsorise [−106.535, 10.509] bp/min, mean −26.375, SD 21.351.

**Status: ADOPTED (user 2026-10-19: "We'll adopt both changes. We always want more net with a better pf1").** ln volat
plus the 3 % gate are now `flushfaderv2_reference.py`'s defaults. The fit on the full period is the production
reference: `data/flushfaderv2/v2_coefficients.json`, book 35,185. The 2026-10-18 files are in
`data/flushfaderv2/pre_refit_2026-10-19/`. Ported to the Oms (`Engine/Sizing.fs`: Dim 8, `lnVolat`;
`Config.fs`: NearLowHi 0.03), with the 200 pins reproduced to 1e-9.

**SEALED (2026-10-19):** `parity trips -s 2020-01-02 -e 2026-08-21 --from-bars --system flushfader` (the Oms
engine + Sizing, 66 min).
- **Book 35,185 = 35,185**, keys identical. Max |Δ|: entry/exit secs and px 0 (exit px 2e-16); dslo, s20_z, barnum,
  young 0; pf1_model / pf1_fitted 6.7e-15; multiplier 1.1e-14.
- Engine-level signals 192,399 vs the reference's 192,365: the +34 are all PSNYW, the known extra of the previous seals.
- **The live universe filter** (the Oms's `Universe.excluded`: mislabelled warrants, rights, units, preferreds,
  closed-end funds; names from `ticker_reference`) applied to the book's 3,679 tickers: **8 trades on one ticker,
  VCX** (Fundrise Innovation Fund, a closed-end fund). That is 0.02 % of the book, with sized net +7.9 of 28,308. The
  reference and live trade the same universe to a rounding error. (PSNYW is excluded live too; it never reached the
  book, being under $1.)

## V2-17 — how often are signal days THIN? (2026-10-21)

**Question (user).** The first live week gave 10 · 9 · 6 · 5 · 1 tickers a day (10-14 … 10-20) against a 2026 book mean of
~15. Is the pipeline losing signals, or is the market thin? And how often do such days happen?

**Pipeline check.** `parity trips --from-bars` (all four systems) on the same days, after the backfill of 10-19 / 10-20:
the reference makes exactly the live tickers every day — 10-14 10, 10-15 9, 10-16 6 (the week-1 review's runs), 10-19 5
(FUFU AAME YMAT XLAB OCUL), 10-20 1 (AIXI: SpikeFader short 11:25, FlushFader long 11:51). **Nothing is lost live.**

**Setup.** `scripts/equity/book_daily_counts.py` (tables: `data/flushfaderv2/daily_counts.md`): the distinct tickers per day
of the four production reference books (FlushFaderV2 + LowFader + SpikeFader + Snoozer), 2020-01-02 … 2026-07-17 (the span
all four cover), every trading day counted (no trade = 0). The driver: universe names (`mr_candidate_1s_v2`) whose day low
is ≥ 10 % under the previous close (`low / close_m1 − 1 ≤ −10 %`, from `data/v2/daily_prices`).

| year | mean / day | median | days ≤ 1 | days ≤ 5 | 5-day stretches with mean ≤ 6.2 (the live week) |
|---|---|---|---|---|---|
| 2020 | 18.6 | 14 | 2 | 9.9 % | 6.4 % |
| 2021 | 21.6 | 16 | 0 | 0.4 % | 0 % |
| 2022 | 10.3 | 9 | 3 | 14.7 % | 14.3 % |
| 2023 | 7.6 | 7 | 3 | 28.0 % | **32.8 %** |
| 2024 | 11.9 | 11 | 0 | 6.7 % | 2.4 % |
| 2025 | 16.6 | 15 | 0 | 2.0 % | 0 % |
| 2026 (to 07-17) | 15.5 | 15 | 0 | 1.5 % | 0 % |
| all | 14.5 | 12 | 8 (0.5 %) | 9.6 % | 8.5 % |

| names down ≥ 10 % (decile) | ≤ 8 | 9–11 | 12–13 | 14–16 | 17–18 | 19–21 | 22–27 | 28–34 | 35–51 | > 51 |
|---|---|---|---|---|---|---|---|---|---|---|
| mean tickers / day | 7.7 | 9.7 | 10.5 | 11.4 | 12.8 | 13.2 | 14.9 | 17.0 | 18.2 | 31.6 |
| days ≤ 5 | 32.1 % | 13.5 % | 10.1 % | 10.7 % | 9.5 % | 6.6 % | 1.7 % | 3.3 % | 2.5 % | 1.2 % |

The driver in the live weeks and by month (2026, mean names down ≥ 10 % per day):

| Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct (to 20th) |
|---|---|---|---|---|---|---|---|---|---|
| 32.7 | 48.6 | 32.2 | 25.0 | 40.5 | 48.3 | 40.9 | 25.5 | **13.7** | 24.9 |

10-14 77 · 10-15 48 · 10-16 51 · **10-19 8 · 10-20 3** (the universe: ~1,086 names / day in June, 918 in October; no gap).

Reading:
- **A one-ticker day is rare (8 in 6½ years) but thin STRETCHES are not:** 8.5 % of all 5-day windows were as thin as the
  live week, a third of 2023's. None in 2025–26 before September.
- **Thin days cluster.** Lag-1 autocorrelation of the count 0.67 (lag 20: 0.23). After a thin day (≤ 6 tickers, the
  bottom decile with ties: 14.5 % of days) the next is thin 43 % of the time. After a 20-day mean < 6, 43 % of days have ≤ 5.
- **The count follows the market's flushes** (ρ 0.53 with names down ≥ 10 %; 0.55 with names up ≥ 20 %). 10-19 and 10-20
  had 8 and 3 such names, the bottom decile; September 2026 was the quietest month of the year.
- Many of the thinnest days are holiday-adjacent (2022-11-23/25, 2023-07-03, 2023-11-24, 2025-11-28).
- **Verdict:** the live week is the market, not the system: a quiet tape, as in 2022–23. Expect weeks like it, in runs.

## V2-18 — names down ≥ 90 % from their 1-year high (2026-10-22, after AMOD)

**Why:** live 2026-10-21 lost −912 on AMOD: bought 1,132 sh @ ~1.83 at 13:51, LULD-halted 13:58 (1.68 → 1.34), reopened
14:08 @ 1.10, out @ 1.02 at 14:30. AMOD was **96.5 % below its 1-year high** (52.00 on 2025-11-06, D's raw scale) at the
entry. User: how do the ≥ 90 % names behave?

`scripts/equity/flushfaderv2_dd52.py` → `data/flushfaderv2/dd52.md` + `dd52_book.parquet` (log `logs/flushfaderv2_dd52.log`).
dd = 1 − signal_vwap / hi365, hi365 = max(high·n) over D's EPISODE's rows in [D − 365 d, D), ÷ n(D) — causal, the
book's prices being day D's raw prints (checked on split days). The book: the production reference (35,185 trades),
r net of the $0.001 credit. Rows whose window spans < 300 days (17 %) are kept apart.

| dd from 1y high | n | share | PF flat | PF sized | mean r % | mean mult | p05 r % |
|---|---|---|---|---|---|---|---|
| < 50 % | 13,687 | 38.9 % | 1.661 | 1.907 | +0.602 | 1.02 | −5.02 |
| 50–70 % | 6,255 | 17.8 % | 1.676 | 1.943 | +0.620 | 0.92 | −5.21 |
| 70–80 % | 3,120 | 8.9 % | 1.591 | 1.895 | +0.589 | 0.97 | −5.40 |
| 80–90 % | 2,877 | 8.2 % | 1.587 | 1.845 | +0.632 | 1.02 | −5.54 |
| 90–95 % | 1,251 | 3.6 % | 1.484 | 1.842 | +0.615 | 1.13 | −7.70 |
| ≥ 95 % | 1,882 | 5.3 % | 1.413 | 1.681 | +0.607 | 1.09 | −7.84 |
| window < 300 d | 6,113 | 17.4 % | 1.513 | 1.600 | +0.559 | 0.99 | −5.66 |
| all | 35,185 | 100 % | 1.596 | 1.825 | +0.600 | 1.00 | −5.49 |

| year | PF < 90 % (n) | PF ≥ 90 % (n) |
|---|---|---|
| 2020 | 1.845 (5,860) | 1.651 (172) |
| 2021 | 1.839 (6,227) | 1.451 (32) |
| 2022 | 1.222 (2,112) | 1.279 (328) |
| 2023 | 1.405 (1,905) | 1.008 (399) |
| 2024 | 1.652 (3,319) | 1.530 (696) |
| 2025 | 1.670 (4,263) | 1.421 (909) |
| 2026 | 1.385 (2,253) | 1.712 (597) |

Control — is it volatility in disguise (the sizing model prices volat)? No: worse in **every** volat band, and the gap
widens with volatility — the model sizes the ≥ 90 % names up with the band without their edge rising with it.

| volat band | PF < 90 % (n) | PF ≥ 90 % (n) | mult < 90 % | mult ≥ 90 % |
|---|---|---|---|---|
| 0 | 1.437 (13,713) | 1.366 (787) | 0.85 | 0.90 |
| 1 | 1.640 (7,523) | 1.324 (1,149) | 1.01 | 1.02 |
| 2 | 1.905 (3,786) | 1.515 (932) | 1.23 | 1.20 |
| 3 | 2.397 (844) | 1.700 (247) | 1.84 | 1.74 |
| 4 | 3.230 (73) | 1.408 (18) | 2.18 | 2.40 |

- **The ≥ 90 % names are profitable but weaker:** PF 1.41–1.48 vs 1.59–1.68; the MEAN return is the same (+0.61 %), the
  tail fatter (p05 −7.8 % vs −5.0 %; losses worse than −20 %: 0.77 % of trades vs 0.22 %, 3.5×).
- 5 of 7 years worse (2022 and 2026 better) — a sizing input, not a gate: removing 8.9 % of the book would cut real
  profit. The natural use is a term in the V2 sizing model (they are over-sized today: mean mult 1.09–1.13 for a PF−1
  ~0.6× the book's).
- Not studied yet: the halt itself (AMOD's loss was the halt gap; `ht` counts halts only BEFORE the signal).

## V2-19 — the drawdown as a SIZING TERM, on the holdouts: not worth it (2026-10-22)

User: "on the PF sized basis there really isn't that much difference in the 50-90 buckets. Only the >= 95% bucket is
noticeably worse … it might not be bad to introduce a variable." `scripts/equity/flushfaderv2_dd52_holdout.py`
(`logs/flushfaderv2_dd52_holdout.log`): V2-16's method on V2-18's book (fit one year block's barnum ≥ 22 trades, apply
to the other; PRODUCTION units clip(PF−1, 0.125, 2)). A window shorter than a year uses the history it has, so here
dd ≥ 90 % is 10.5 % of the book (8.9 % in V2-18, which set the short windows apart). **Control: the baseline reproduces
V2-16's adopted row exactly** (0.721 / 0.908, net 16,999 vs 17,000).

| model | sized PF−1 fwd | mirror | **mean** | **net (sum)** | unit-trades | net / unit-trade | max DD fwd / mirror |
|---|---|---|---|---|---|---|---|
| production (control) | 0.721 | 0.908 | **0.815** | **16,999** | 21,113 | 0.805 | 74 / 101 |
| + [dd ≥ 95 %] | 0.730 | 0.909 | **0.819** | **16,685** | 20,736 | 0.805 | 78 / 101 |
| + [dd ≥ 90 %] | 0.726 | 0.908 | **0.817** | **16,499** | 20,591 | 0.801 | 65 / 100 |
| + [90–95 %] + [≥ 95 %] | 0.728 | 0.907 | **0.817** | **16,490** | 20,581 | 0.801 | 68 / 100 |
| + ln(px / 1y high), clip 2 % | 0.720 | 0.907 | **0.814** | **16,396** | 20,603 | 0.796 | 68 / 102 |

Full-period fit of [dd ≥ 95 %]: odds × 0.977, wins × 1.096, **losses × 1.196** — the fatter tail of V2-18, and the
term cuts those trades' PF−1 by ~30 %.

- **Every shape buys ≤ +0.004 sized PF−1 and costs 1.8–3.5 % of net.** The net per unit-trade is unchanged (0.805):
  the term moves capital off trades that still earn the book's average per unit. By the V2-16 rule ("more net with a
  better pf1") none qualifies.
- Why V2-18's gap does not carry over (not proven, a reading): in the SIZED column only ≥ 95 % stood out (1.681 vs
  1.825), and the term mostly fits that as bigger losses AND bigger wins (× 1.196 / × 1.096). Sizing those trades down
  removes both, so PF−1 barely moves while net falls.
- **Verdict: NOT ADOPTED** (recommendation; the user decides). AMOD's loss is a HALT problem (held through an LULD
  pause), not a drawdown-sizing problem — the next study if any.
