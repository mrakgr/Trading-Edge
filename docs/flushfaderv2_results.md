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
- Sizing measure = PF−1 net of the \$0.001/sh/side credit (the 2026-09-09 ruling: this system sizes on PF−1, not
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
\$0.001/sh/side credit. Book PF−1 0.53. Full per-band tables (with the 2020–23 / 2024–26 halves):
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
reference book's 37,254 exactly). Flat, net of the \$0.001/sh/side credit.

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

**The V2 book** = the 209,252 engine-level trips − dslo ∈ (0, 2 %], then the reference construction (raw px ≥ \$1,
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
168,632 engine-level trips with raw px ≥ \$1, chronological, first come first served; one CONCURRENT position per
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
| book (raw px ≥ \$1, mc=1) | **35,612** (= the slice study, identical keys) | **192** (V1: 201) |
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
| full period 2020-01-02 .. 2026-08-21 `--from-bars` | **book 35,612 = 35,612** (multiplier Δ ≤ 7.6e-15); trips 195,764 shared, zero-diff (one exit_px 2e-16), + 34 Oms-only trips = ONE ticker-day, PSNYW 2025-08-27 (a warrant at \$0.55: in today's candidate table, absent from base_v19 built 09-08 — universe drift, not the engine; all below \$1, so no book effect) |
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
  - ⭐ The production reference is now built with `--coef models/flushfaderv2/v2_coefficients.json`: the V2-7 model is
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

**Setup.** The books are replayed by `flushfaderv2_reference.py --near-low-hi H --coef models/flushfaderv2/v2_coefficients.json`
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
reference: `models/flushfaderv2/v2_coefficients.json`, book 35,185. The 2026-10-18 files are in
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
  book, being under \$1.)

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
r net of the \$0.001 credit. Rows whose window spans < 300 days (17 %) are kept apart.

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

## V2-20 — the raw downside CUSUM as a SIZING term, against and beside the 20m slope (2026-11-04)

**The user:** "the faster the move against the trend, the better it is to fade it ... we're already using the 20m slope
for sizing in the production system, so it will be directly competing with it." The candidates are from
flushfader_results.md §S49an:
- `cus` = cusr_dn_k435: the raw downside CUSUM at k = 4.35 bp/bar, the bp of selling in excess of the median bar move
  since its last reset;
- `vel` = cus / bars since that reset: its velocity.

Each enters like s20 (winsorised at 1 % / 99 %, standardised). The protocol is V2-16's: fit on one year block's
barnum ≥ 22 trades, apply to the other, production units (`scripts/equity/flushfaderv2_cusum_holdout.py` →
`data/flushfaderv2/cusum_holdout.md`). All 35,185 V2 book trades joined (base_v21 + the young splice base_v21_young).

| model | sized PF−1 fwd / mirror (**mean**) | net fwd / mirror (**sum**) | net per unit-trade | max DD fwd / mirror |
|---|---|---|---|---|
| A s20 (production) | 0.721 / 0.908 (**0.815**) | 8,412 / 8,588 (**17,000**) | 0.805 | 74 / 101 |
| B cus instead of s20 | 0.706 / 0.902 (**0.804**) | 8,452 / 8,276 (**16,728**) | 0.797 | 80 / 96 |
| C s20 + cus | 0.723 / 0.911 (**0.817**) | 8,587 / 8,595 (**17,182**) | 0.813 | 85 / 96 |
| D vel instead of s20 | 0.708 / 0.904 (**0.806**) | 8,396 / 8,269 (**16,665**) | 0.796 | 72 / 82 |
| **E s20 + vel** | 0.725 / 0.918 (**0.822**) | 8,522 / 8,586 (**17,109**) | 0.813 | **70 / 83** |

A paired day-bootstrap against A (each trade's out-of-sample sized return, 2,000 resamples of the trading days):

| model | Δ PF−1 [90 %] | resamples > 0 | Δ net [90 %] |
|---|---|---|---|
| B | −0.014 [−0.026, −0.003] | 2 % | −272 [−430, −116] |
| C | +0.001 [−0.004, +0.006] | 62 % | +182 [+109, +256] |
| D | −0.012 [−0.025, +0.001] | 7 % | −336 [−513, −155] |
| E | +0.005 [−0.001, +0.012] | 92 % | +109 [+20, +197] |

- **Neither replaces the slope.** B and D lose 0.012–0.014 PF−1 and 1.6–2.0 % of net, both reliably.
- **They are not competing with s20:** Spearman s20~cus −0.16, s20~vel −0.03. s20 stays the win-rate term (odds ×0.92
  per SD, both fits).
- **cus is a SIZE term, not an edge term.** p odds ×0.99–1.00 per SD, but W ×1.15–1.22 and L ×1.14–1.18. It scales
  the trade's move both ways, like volatility. So C's +1.1 % net comes from exposure (PF−1 +0.001, 62 %), and its
  forward max DD rises 74 → 85.
- **vel is the small real addition.** E is +0.005–0.007 PF−1 (92 % of resamples above 0), +0.6 % net (interval above
  0), with LOWER max DD on both holdouts (74 → 70, 101 → 83). Per SD: p odds ×1.015, W ×1.04–1.05, L ×1.01–1.04.
- **The size of it:** about half of each V2-16 change the user adopted (+0.009–0.011 PF−1 each).

**Verdict:** keep s20. If a CUSUM term goes into production, it is the VELOCITY (cus / bars since its reset) as a fourth
continuous term beside s20: model E. The gain is small but consistent: +0.005–0.007 PF−1, +0.6 % net, a lower drawdown.
The level adds exposure, not edge. The port would need the CUSUM at k = 4.35 in the Oms engine (O(1) per bar) and a
refit of the production coefficients. **The user's call.**

## V2-21 — the CUSUM at SMALLER k: the gain is the depth below the session high (2026-11-04)

**The user, on V2-20:** "This k that we're using is quite aggressive." At k = 4.35 bp/bar (the median bar move) the
CUSUM resets after any pause, so its reset is a median of only 11 bars back. The research engine now also records
k = 0.25 / 0.5 / 1 / 2 bp/bar (flushfader_results.md §S49ap: base_v22 + base_v22_young). The 391 base_v21 columns are
bit-identical on the control week, and the new values equal a Python recomputation exactly.

On the V2 book, a smaller k reaches further back up the leg:

| k (bp/bar) | 0 (= the session high) | 0.25 | 0.5 | 1 | 2 | 4.35 |
|---|---|---|---|---|---|---|
| bars since the reset (median) | 1,955 | 1,361 | 772 | 245 | 54 | 11 |
| the drop since it (median, bp) | 1,579 | 1,425 | 1,095 | 634 | 327 | 171 |

The level S⁻ = the drop since the reset − k × the bars since it, exactly (no reset in between, by definition), so
S⁻ / bars = the drop's speed − k.

**V2-20's protocol** (`flushfaderv2_cusum_holdout.py --base …/base_v22 --young …/base_v22_young --ks 025,05,1,2,435`
→ `data/flushfaderv2/cusum_holdout_lo.md`; `--ks 0,025` → `cusum_holdout_k0.md`). The refactored script reproduces
V2-20's A / C / E to the digit (the control).

| model | sized PF−1 fwd / mirror (**mean**) | Δ PF−1 vs A, pooled [90 %] (resamples > 0) | Δ net [90 %] | max DD fwd / mirror |
|---|---|---|---|---|
| A s20 (production) | 0.721 / 0.908 (**0.815**) | | | 74 / 101 |
| s20 + cus k0 (the drop from the session high, bp) | 0.740 / 0.912 (**0.826**) | +0.014 [+0.000, +0.028] (95 %) | +181 [−4, +366] | 66 / 106 |
| **s20 + cus k0.25** | 0.743 / 0.914 (**0.829**) | +0.016 [+0.000, +0.033] (95 %) | **+251 [+31, +479]** | **60** / 103 |
| s20 + cus k0.5 | 0.730 / 0.911 (0.821) | +0.007 (77 %) | +64 | 60 / 100 |
| s20 + cus k1 | 0.724 / 0.910 (0.817) | +0.003 (64 %) | +24 | 63 / 102 |
| s20 + cus k2 | 0.723 / 0.911 (0.817) | +0.002 (75 %) | +74 | 69 / 101 |
| s20 + cus k4.35 (V2-20 C) | 0.723 / 0.911 (0.817) | +0.001 (64 %) | +182 | 85 / 96 |
| s20 + vel k4.35 (V2-20 E) | 0.725 / 0.918 (0.822) | +0.005 (92 %) | +109 | 70 / 83 |
| s20 + vel at k 0 … 2 | 0.810–0.816 | −0.005 … 0.000 | | |

- **The gain is the LEVEL at small k, and it decays smoothly as k grows:** k0 +0.014, k0.25 +0.016, k0.5 +0.007,
  k1 +0.003, k2 +0.002. A smooth trend across neighbouring cells, not one lucky cell; still, the best of 12 variants
  at a one-sided 95 % is modest evidence.
- At small k the level is nearly **how far the price has fallen from the session high**, which k0 is exactly, at
  almost the whole gain.
- It correlates more with s20 than the k4.35 terms did (Spearman −0.35 to −0.45) but does not replace it: both terms
  stay in the fits.
- **Its coefficients (k0.25, per SD):** p odds ×0.95 / ×1.05 (no consistent win-rate effect), W ×1.08–1.11,
  L ×1.20–1.21. Given volatility, s20 and dslo, a deeper fall from the high raises the typical LOSS more than the
  typical win, so the model sizes those trades DOWN. The deepest flushes are the riskiest per unit of edge.
- **The velocities add nothing at small k.** The only useful velocity is k4.35's: the speed of the final ~11-second
  burst (V2-20 E).
- Each of the best two is about the size of one V2-16 change the user adopted (+0.009–0.011 PF−1).

**Verdict:**
- **For production: s20 + the drop from the session high (k0), or its k0.25 refinement:** +0.014–0.016 sized PF−1,
  +1.1–1.5 % net, the forward max DD 74 → 60–66 (the mirror +2–5).
- **k0 is the simplest port:** ln(session high / price) in bp, from state the Oms engine already keeps.
- k0.25 needs the CUSUM itself (O(1) per bar) for +0.002 more and a lower forward DD.
- Combining with V2-20's burst velocity (vel k4.35) is untested.
- **The user's call:** which term, then a production refit + the Oms port + the pins.

## V2-22 — an OLS slope that RESETS with the CUSUM, against the fixed 20m slope (2026-11-04)

**The user:** "what if we made an OLS slope feature that resets when the CUSUM does? Would it work better than the
20m one?"
- **The feature:** `aslope_k*` (flushfader_results.md §S49aq, base_v23 + base_v23_young). The OLS slope of ln(vwap),
  in bp per bar, over the present bars since the k-CUSUM's last downside reset (its anchor bar included): the leg's
  slope, with the CUSUM choosing the window.
- **Checks:** = numpy polyfit over those bars (1,179 trip-k pairs, worst 4.5e-12 relative). The 407 base_v22 columns
  are bit-identical on the control week.
- **Protocol:** V2-20's (`flushfaderv2_cusum_holdout.py --base …/base_v23 --young …/base_v23_young --ks 025,05,1,2
  --aslope` → `data/flushfaderv2/cusum_holdout_aslope.md`). The script still reproduces V2-21's tables on base_v22
  exactly (the control).

| model | sized PF−1 (mean of the holdouts) | Δ PF−1 vs A, pooled (resamples > 0) | Δ net | max DD fwd / mirror |
|---|---|---|---|---|
| A s20 (production) | 0.815 | | | 74 / 101 |
| s20 + cus k0.25 (V2-21) | **0.829** | +0.016 (95 %) | +251 | 60 / 103 |
| aslope k0.25 instead of s20 | 0.798 | −0.018 (2 %) | −470 | 72 / 95 |
| s20 + aslope k0.25 | 0.814 | −0.001 (41 %) | +18 | 71 / 98 |
| cus k0.25 + aslope k0.25 (no s20) | 0.805 | −0.009 (26 %) | −394 | 64 / 100 |
| s20 + cus k0.25 + aslope k0.25 | 0.828 | +0.016 (91 %) | +260 | 57 / 101 |
| aslope instead of s20, k 0.5 / 1 / 2 | 0.805 / 0.807 / 0.802 | −0.012 / −0.010 / −0.015 | −330 … −440 | |
| s20 + aslope, k 0.5 / 1 / 2 | 0.818 / 0.818 / 0.815 | +0.003 / +0.003 / +0.000 | +12 … +123 | |

- **The anchored slope does not replace the fixed 20m slope.** At every k it loses 0.010–0.018 sized PF−1 and 2–3 %
  of net, reliably at k0.25 and k2 (2 % / 1 % of resamples above A).
- **Without s20, even the CUSUM level cannot make up for it:** cus + aslope (no s20) is below production at every k.
- **Beside s20 it adds nothing:** −0.001 … +0.003. In the full model (s20 + cus k0.25 + aslope k0.25) it leaves V2-21's
  best where it was (0.828 vs 0.829, net +260 vs +251; forward DD 60 → 57).
- **Why:** the CUSUM's window is the current LEG. The fixed 20 minutes covers more than the leg: the slide into it, and
  its setting (Spearman with s20: +0.19 / −0.07 / −0.18 / −0.08 at k0.25 / 0.5 / 1 / 2, so they are barely related).
  What predicts a good fade is the 20-minute context, not the leg's own slope. The leg's endpoint speed (V2-21's
  velocity) failed the same way.

**Verdict:** keep the fixed 20m slope. V2-21's addition stands: **s20 + the CUSUM level at k0.25** (the user's pick
over k0). The anchored slope is not worth the port.

## V2-23 — the CUSUM's AGE as a sizing term; rate600 continuous instead of binary (2026-11-04)

**The user:** "whether the number of bars since the CUSUM anchor could serve as a feature in its own right. In SpikeFader,
I think we had some luck with rate features ... Maybe we are [using them] as a binary feature."
- **The age:** ln(1 + bars since the k-CUSUM's downside reset), standardised. At k0.25 it is the leg's duration since
  the recent high the CUSUM anchors to (median 1,361 bars, p90 4,729).
- **The rate:** production already uses rate600 as a BINARY term, "fast" iff lows_600 / bars_600 >= 0.07 (44.6 % of
  the book; odds ×0.89, W ×1.03, L ×1.04). Here the continuous rate600 (winsorised, standardised) is tested instead of
  the flag and beside it.
- **Protocol:** V2-20's (`flushfaderv2_cusum_holdout.py … --ks 025,05,1,2,435 --age` → `data/flushfaderv2/cusum_holdout_age.md`).

| model | sized PF−1 (mean) | Δ PF−1 vs A, pooled (resamples > 0) | Δ net [90 %] | max DD fwd / mirror |
|---|---|---|---|---|
| A s20 (production) | 0.815 | | | 74 / 101 |
| s20 + cus k0.25 (V2-21) | 0.829 | +0.016 (95 %) | +251 [+25, +480] | 60 / 103 |
| s20 + age k0.25 | 0.818 | +0.005 (87 %) | +238 [+138, +338] | 71 / **92** |
| s20 + cus k0.25 + age k0.25 | 0.827 | +0.015 (93 %) | **+287 [+58, +525]** | **57 / 99** |
| s20 + age k0.5 / 1 / 2 / 4.35 | 0.814 / 0.812 / 0.814 / 0.813 | +0.000 / −0.003 / −0.001 / −0.002 | −6 … +30 | |
| s20, rate600 continuous INSTEAD of the flag | 0.807 | −0.008 (4 %) | −188 [−296, −80] | 82 / 98 |
| s20 + rate600 continuous beside the flag | 0.814 | −0.000 (30 %) | −13 | 73 / 99 |

- **The age is a weak term on its own, and only at k0.25.**
  - Beside s20: +0.005 sized PF−1 and +1.4 % net (interval above 0), the mirror DD 101 → 92.
  - Its fits: p odds ×0.98 / ×1.02 (no win-rate effect), L ×1.03 / ×1.11, W ×1.02. A longer leg since the recent high
    means somewhat larger losses, so it sizes down a little.
  - At larger k (0.5 … 4.35) it adds nothing.
- **Beside the level it mostly overlaps.** The level carries the age by construction (S⁻ = the drop − k × the age).
  s20 + cus + age at k0.25 is 0.827 against 0.829 without it. It adds +36 net (+287 vs +251) and lowers both drawdowns
  (60 / 103 → 57 / 99), but these are small and inside the bootstrap noise. With the level in, its coefficients shrink
  to ~1.
- **The rate works better as the binary flag.**
  - The continuous rate600 in its place is reliably worse (−0.008, 4 %; net −1.1 %).
  - Beside the flag it adds nothing.
  - The effect sits at the threshold ("fast" vs not), not along the scale, so the production flag stays.

**Verdict:**
- **Not worth a separate term:** the age. It is mostly what the level already says.
- **If the port wants a little more net and a slightly lower drawdown:** s20 + cus k0.25 + age k0.25. The age comes
  free from the same CUSUM state.
- **Otherwise:** V2-21's s20 + cus k0.25 stays the candidate.
- **rate600:** keep it binary.

## V2-24 — the production refit with the CUSUM level (k0.25), and why the reference needs a rebuild (2026-11-04)

**The user:** "we'll use the new CUSUM level feature in the production system ... Distance from the high should have
some effect on our decision making." Production is FIT on the full period (2020-01-02 .. 2026-08-21, every barnum ≥ 22
trade). The year blocks (V2-16, V2-20..23) are only for evaluating a candidate.

**`flushfaderv2_reference.py` changes:**
- **The CUSUM level:** `cusr_dn_k025` is joined from `--cus-trips` (base_v23 + its young splice) on symbol /
  trade_date / signal_sec. It enters as a 9th term, winsorised at the fit set's 1 % / 99 %, standardised, a NaN at
  z = 0.
  - It is gated on the coefficient file: a file without the term gives exactly the older model.
  - `--no-cusum` fits the old model.
  - The pins carry `cusum_dn_k025`.
- **⚠ THE DATA HAS MOVED UNDER THE PRODUCTION REFERENCE.** The script no longer ran as it was: 630 base_v19 trips on 47
  ticker-days, 7 of them book trades (APPH, IRBT, UEC), have no barnum in today's `mr_candidate_1s_v2`.
  - **The cause:** on 2026-10-25 the 1s bars were rebuilt without the condition-15/16 markers (the official open /
    close prints that restate a cross already on the tape), and the candidates were rebuilt from them at the $1.6M
    dv_0945 floor.
  - These ticker-days' morning volume had been inflated by the markers; without them they are below the floor.
  - The ATR5 table was rebuilt with them, so those days read NaN there too.
  - The production fit (2026-10-19) predates all of this.
  - **`--barnum-fallback`** (for this one-change refit only) fills such a trip's barnum and ATR5 from the production
    reference's own trips file: 44 filled, the other 586 were never engine-level trips.

**The controls:**
1. Apply mode with the production file reproduces the production book: 35,185 trades, every column identical,
   pf1_fitted and the multiplier exactly. The one exception is BBBY 2026-04-28's ATR5 (1.4e-17, a rounding of the
   ATR5 rebuild).
2. `--no-cusum` reproduces every production coefficient, both s20 constants, BasePf1 and all 200 pins exactly.

**The fit WITH the term** (the same book; `data/flushfaderv2/cusum_k025/`):
- **The term:** p odds ×0.992, W ×1.098, L ×1.210 per SD, the V2-21 shape. It sizes deep falls from the recent high
  down. Winsorised at [291, 5,256] bp, mean 1,300.6, SD 950.0.
- **BasePf1:** 0.6184 → 0.6318.
- **Production units:** the mean position 0.616 → 0.631 units, the median 0.541 → 0.557, at the 2-unit cap 0.32 % →
  0.49 %.
- **Agreement:** Spearman 0.945 between the old and new PF−1.

**⏭ The user, on the cause: "we should rebuild the reference with the open and close markers removed."** This fit
is the CONTROL point, not production. The reference is being rebuilt on today's data (V2-25), and the CUSUM term is
refitted there.

## V2-25 — the reference REBUILT on the marker-free base, with and without the CUSUM level (2026-11-05)

**User (11-04):** "we should rebuild the reference with the open and close markers removed." The base: **base_v24**, the
current engine on the full current universe (`data/equity/flushfader/v24/cands_b11.parquet`: every barnum ≥ 11
ticker-day of the 2026-10-25 marker-free `mr_candidate_1s_v2`, 1,183,355 ticker-days, 2020-01-02 .. 2026-08-21; the main
book and the young trades in one run, `scripts/equity/flushfader_run_base_v24.sh`). 8,440,488 trips.
- ⚠ The run was OOM-KILLED in its 11 GB scope at 81.5 % (the engine's memory grows through a run: 7.8 GB at 22 %, 9.4 GB
  at 37 %). Resumed from 2025-04-15 into a side directory (`flushfader_run_base_v24_tail.sh`, peak 11.35 GB); the
  overlap 2025-04-15 .. 05-14 matched the original parts row for row (72,897 rows × 411 columns, both directions); the
  parts were merged at 2025-05-15 (the parts never run more than 15 days out of date order). ⏭ The memory growth
  needs fixing before the next full-period base.

**Without the CUSUM** (`flushfaderv2_reference.py --trips/--cus-trips base_v24 --barnum-fallback '' --no-cusum`,
`data/flushfaderv2/v24_nocus/`): 195,660 engine-level trips, book **35,767** (production 35,185), PF 1.601 (1.596),
sized 1.815 (1.825), BasePf1 **0.6212** (0.6184). The coefficients barely move: the p intercept 1.130 → 1.088, the S
tier's L term −0.151 → −0.167, every other within ~0.01; the s20 winsorisation [−106.73, 10.55] bp/min (was
[−106.54, 10.51]). The markers' removal changed the book by 1.6 %, the model by almost nothing.

**With the CUSUM** (k0.25, the same command without `--no-cusum`, `data/flushfaderv2/v24_cusum/`, 200 pins):

| CUSUM dn k0.25 per SD | p (odds ×) | W (×) | L (×) |
|---|---|---|---|
| rebuilt (v24) | 0.991 | 1.098 | 1.206 |
| pre-rebuild (V2-24) | 0.992 | 1.098 | 1.210 |

- Winsorised at [292.0, 5,248.5] bp, mean 1,302.5, SD 947.9. BasePf1 0.6212 → **0.6343**; in sample, sized PF 1.815 →
  1.830.
- It takes weight from the volatility term (ln volat: W ×2.22 → ×1.89, L ×2.17 → ×1.54): the depth below the recent
  high in bp carries part of what the volatility measured.
- Production units: mean 0.619 → 0.634, median 0.545 → 0.563, at the 2-unit cap 0.24 % → 0.41 %, at the 0.125 floor
  1.4 % → 3.0 %. Spearman 0.943 between the two PF−1s.

**The holdout on the rebuilt book** (`flushfaderv2_cusum_holdout.py --book v24_nocus --base base_v24 --young '' --ks
0,025`; fwd = fit 2020–23 → 2024–26, mirror the reverse; production units, net and DD in units):

| model | sized PF−1 fwd / mirror / **mean** | net sum | max DD fwd / mirror | Δ PF−1 vs s20 (90 %), share > 0 | Δ net (90 %) |
|---|---|---|---|---|---|
| s20 (production) | 0.716 / 0.893 / **0.805** | 17,289 | 75 / 103 | | |
| **s20 + cus k0.25** | 0.738 / 0.899 / **0.819** | 17,552 | 70 / 104 | +0.017 [+0.002, +0.032], 96 % | +263 [+44, +486] |
| s20 + cus k0 | 0.734 / 0.897 / 0.816 | 17,486 | 76 / 108 | +0.014 [+0.001, +0.027], 96 % | +197 [+17, +379] |
| s20 + vel k0.25 | 0.713 / 0.887 / 0.800 | 17,296 | 72 / 100 | −0.004, 18 % | +7 |

- **The V2-20/21 result holds on the rebuilt data:** +0.014 mean PF−1 (was +0.016), +263 units (was +251), 96 % of
  day resamples. The forward drawdown gains less than before (75 → 70; was 74 → 60). The coefficients agree across the
  fits (L ×1.196 / ×1.209 per SD fwd / mirror). The velocity adds nothing, again.
- **⇒ The production refit = `data/flushfaderv2/v24_cusum/v2_coefficients.json`** (the user's decision 11-04: "we'll
  use the new CUSUM level feature in the production system"). Next: the Oms engine's CUSUM, sealed against research's
  `cusr_dn_k025`; Sizing.fs's new term, constants and pins.

## V2-26 — the production book WEEK BY WEEK and MONTH BY MONTH: the percentiles (2026-11-05, user)

**User:** "how the new FlushFader system does on a monthly and weekly basis ... look into specific percentiles".
`scripts/equity/flushfaderv2_period_percentiles.py` → `data/flushfaderv2/period_percentiles.md`. The V2-25 production
book (35,767 trades, 2020-01-02 .. 2026-08-21) at L2's production size (units = clip(PF−1, 0.125, 2), one unit = 15 % of
equity), % of equity, research's net (+ the \$0.001/sh/side maker credit), periods by the entry date.

| % of equity | positive | mean | p1 | p5 | p10 | p25 | p50 | p75 | p90 | worst |
|---|---|---|---|---|---|---|---|---|---|---|
| DAY (1,666) | 79 % | | −5.08 | −1.92 | −0.86 | +0.20 | +1.27 | +2.78 | +4.66 | −20.53 (2020-03-18) |
| WEEK (347) | 90 % | +7.83 | −5.45 | −0.93 | +0.11 | +2.55 | +6.23 | +10.78 | +17.67 | −8.66 (2022-05-13) |
| WEEK, without the CUSUM | 89 % | +7.64 | −6.26 | −1.45 | −0.24 | +2.54 | +6.08 | +10.46 | +17.59 | −9.71 (2024-10-04) |
| WEEK 2023–26 (190) | 90 % | +6.99 | −4.90 | −1.32 | +0.02 | +2.24 | +6.27 | +9.94 | +15.28 | −6.54 (2024-10-04) |
| MONTH (80) | 99 % | +33.95 | +0.89 | +4.87 | +6.87 | +16.12 | +29.13 | +43.82 | +60.34 | −0.60 (2023-11) |
| MONTH 2023–26 (44) | 98 % | +30.17 | +0.21 | +4.68 | +6.81 | +18.70 | +29.13 | +38.73 | +51.34 | −0.60 (2023-11) |

- One losing month in 80 (2023-11, −0.6 %); the longest losing streak 2 weeks; the worst 4-week run −0.93 %.
- **The CUSUM tightens the LEFT tail** at a slightly higher mean: weekly p5 −1.45 → −0.93, p1 −6.26 → −5.45, the worst
  week −9.71 → −8.66 (2023–26: −9.71 → −6.54).
- ⚠ **What these numbers are NOT.** No commission, slippage or borrow. No concurrency cap: the book deploys a median 166 %
  of equity per day summed over its trades (p90 352 %; 17 trades a day, p90 36), which L2's margin budget does not allow.
  No compounding. Research's fills (the next bar's vwap in, the target on a trade-through).
- ⚠ **Against live:** the 11-04 live FlushFader day, −\$668 on the \$8,000 sizing equity (−8.4 %), is worse than all but
  0.18 % of research days (3 of 1,666; none in 2024–26). The research fill model applied to those live signals gave −\$660
  — the SIGNALS lost, not the fills. Whether research would have taken the same trades at the same sizes needs the
  day's bars in the research data (the weekly execution review, exec_review.py / signal_review.py).

**SEALED (2026-11-05):** the Oms's port (private repo: `Engine/FlushFader.fs` the down CUSUM `CusumDn025`, k `CusumKBp`
0.25, stepped on every present bar before the entry block in research's arithmetic order; `Engine/Sizing.fs` Dim 9, the
V2-25 betas / constants / BasePf1 0.6343; Parity's signals and book + `cusr_dn_k025`, `cus_z`). `parity trips -s
2020-01-02 -e 2026-08-21 --from-bars --system flushfader --min-barnum 11 --candidates data/v2_candidates/mr_candidate_1s_v2`
(68 min, peak 2.1 GB), diffed with `scanner_diff.py --rtol 1e-12`:
- **Book 35,767 = 35,767, zero-diff:** keys identical; every column exact (cusr_dn_k025, cus_z, s20_z, dslo, barnum,
  atr5 …) but pf1_model / pf1_fitted 3.3e-15 and the multiplier 3.9e-15 relative.
- **Engine-level trips 195,660 = 195,660, zero-diff** on the 12 shared columns, cusr_dn_k025 among them.
- Oms tests 216 / 216: SizingTests' 200 pins to 1e-9 (the pins carry cusum_dn_k025), HUT 2026-07-27's pf1 = the book's
  0.5146670937966513. Controls: k = 0 (the constant check fails), no winsorising (2 tests fail).
- The production files: `data/flushfaderv2/{v2_coefficients.json, flushfaderv2_reference_book.parquet,
  flushfaderv2_reference_trips.parquet, flushfaderv2_reference.md}` = v24_cusum's; the 10-19 production files are in
  `pre_v25_2026-11-05/`. `flushfaderv2_reference.py`'s defaults (base_v24, no barnum fallback) reproduce the JSON byte for
  byte.

## V2-27 — the RIGHT SIDE OF THE V on the efficiency variant: wait for the 1m high (2026-11-07, user)

**User:** "let's try Lance's right side of V concept again. In backtesting it only lowered our PF ... buy at the 1m high
after the signal instead of directly at the signal price ... A lot of our losses from what I've seen are cascades, where
waiting for a 1m high would have let us avoid the loss entirely." The first test was `flushfader_results.md` §S22 on
the v1.x book (flush entry PF 2.137 / +1.28 % vs 1m-high break 1.917 / +0.90 %).

**Method** (`scripts/equity/flushfaderv2_right_side.py`; `--report` prints these tables). Every V2-25 production book
trade, the same signal and the same size, entered instead at the FIRST new 60-present-bar high made strictly after the
reversal's entry-fill bar, filled at the following present bar's vwap (the engine's `aux_hi_60` discipline, recomputed
from the 1s bars because the engine stops tracking it 20 min after the entry). The exit is production's, unchanged: a
5m-high break implies a 1m-high break, so the first 5m break after the confirmation IS the original exit (S22's
composition). A trade with no 1m high before its exit is not taken. **Controls:** the recomputed fill bar = the book's
`entry_sec` and `entry_px` on 100 %; the recomputed break = the engine's `aux_hi_60` (second and price) on all 35,766
trades it recorded. Sizing and the metric are V2-26's (% of equity at clip(PF−1, 0.125, 2) × 15 %). "Maker" keeps the
\$0.001/sh credit on both sides (optimistic for an entry at a fresh high); "taker" pays \$0.003/sh on the entry.

| 2020-01 … 2026-08 | trades | mean | PF | worst | ≤ −20 % per 1,000 | % of equity | weekly median | worst 4 weeks |
|---|---|---|---|---|---|---|---|---|
| reversal (production) | 35,767 | +0.61 % | **1.601** | −84.3 % | 3.1 | **+2,716** | +6.23 | −0.93 |
| 1m-high break, maker | 35,766 | +0.34 % | 1.380 | −84.5 % | 2.3 | +1,538 | +3.44 | −8.72 |
| 1m-high break, taker | 35,766 | +0.24 % | 1.262 | −84.5 % | 2.3 | +1,188 | +2.55 | −11.74 |

| year | reversal PF · % eq | 1m break (maker) PF · % eq | (taker) PF · % eq |
|---|---|---|---|
| 2020 | 1.863 · +607 | 1.726 · +440 | 1.588 · +381 |
| 2021 | 1.804 · +610 | 1.467 · +339 | 1.352 · +274 |
| 2022 | 1.414 · +172 | 1.260 · +89 | 1.132 · +53 |
| 2023 | 1.338 · +163 | 1.092 · +62 | 0.986 · +28 |
| 2024 | 1.540 · +417 | 1.326 · +227 | 1.211 · +175 |
| 2025 | 1.542 · +463 | 1.321 · +233 | 1.201 · +166 |
| 2026 | 1.462 · +285 | 1.261 · +149 | 1.158 · +113 |

- **Worse in every year, both liquidity halves** (gap_60 ≤ 4: PF 1.813 → 1.474; > 4: 1.487 → 1.328) and in the tail
  measure that matters for drawdowns: the worst 4-week run −0.93 → −8.72 %. The confirmation premium is smaller per trade than on
  the v1.x book (−0.27 % vs S22's −0.38 %) but larger against the edge: −44 % of the mean trade vs S22's −30 %.
- **It does not avoid cascades: it re-enters them.** Only ONE of 35,767 trades never printed a 1m high before its exit
  (the exit is itself a 5m high). The confirmation comes a median 111 s later (p90 321 s) at a median +0.52 % above the
  reversal fill; a cascade's first small bounce makes a 1m high, so the trade is bought a few cents from the reversal and
  rides the rest of the collapse. The 30 worst reversal trades average −46.4 % → −27.8 %: the tail shrinks, but the
  ordinary trades pay for it many times over. It helps only slow cascades whose first bounce comes after a long slide.
- **The live weeks (08-24 … 11-06, the parity book's 786 trades on today's data; controls 100 % as above):** reversal
  +19.9 % of equity (PF 1.026) → 1m break −23.5 % (PF 0.855); from 10-12: −8.8 → −21.5 %. The live tail losers were
  entered within 2–4 minutes at almost the same price — RAYA 1.434 → 1.423 (exit 0.890), AEHL 23.585 → 23.282 (14.217),
  AMOD 1.849 → 1.700 (1.026), RETO 8.885 → 9.132 (2.146); only XHLD (−37.0 → −5.8 %) and MSGY (−30.9 → +3.5 %), whose
  first 1m high came 30 minutes into the slide, were saved.
- **Verdict:** the reversal entry wins again (S22 confirmed on the efficiency variant); the "right side of the V" is not
  a fix for the cascade tail. A confirmation that would skip cascades has to be slower than any reversal exit, which
  then makes it a different trade.

## V2-28 — the QUIET entry: wait until 60 s pass without a new 20m low (2026-11-07, user)

**User:** "What if we instead wait for 60s without a new 20m low?" (after V2-27). `scripts/equity/flushfaderv2_quiet_entry.py`
(`--report` prints these tables). Every V2-25 book trade, the same signal and size; after the signal each new 20m low (a
vwap strictly under the prior 1,200-present-bar min — the engine's entry channel) restarts a clock, and the trade is
bought at the bar after the first one Q seconds past the last new low. The exit is SIMULATED from the bars (the first
vwap strictly above the prior 300-present-bar max after the fill, filled at the next bar; overnight = the reversal's
next-open exit), since a quiet entry can come after the reversal's own exit. **Controls:** every signal bar is a new 20m
low under this definition (100 %); the exit simulated from the reversal's fill = the book's exit second and price on
99.98 % of the same-day exits (the 7 others exit in the last second before 16:00); window 100 % / 100 %.

| 2020-01 … 2026-08 | mean | PF | ≤ −20 % per 1,000 | % of equity | weekly median | worst 4 weeks | entered after the reversal's exit |
|---|---|---|---|---|---|---|---|
| reversal (production) | +0.61 % | **1.601** | 3.1 | **+2,716** | +6.23 | −0.93 | — |
| quiet 30 s | +0.46 % | 1.477 | 2.9 | +2,034 | +4.51 | −11.52 | 0 % |
| quiet 60 s | +0.34 % | 1.358 | 2.4 | +1,517 | +3.68 | −8.71 | 0 % |
| quiet 120 s | +0.23 % | 1.264 | 2.2 | +1,031 | +2.67 | −12.51 | 3 % |
| quiet 300 s | +0.07 % | 1.090 | 2.0 | +338 | +1.10 | −17.53 | 37 % |

| year | reversal PF · % eq | quiet 30 s | quiet 60 s | quiet 120 s | quiet 300 s |
|---|---|---|---|---|---|
| 2020 | 1.863 · +607 | 1.866 · +553 | 1.677 · +430 | 1.443 · +284 | 1.087 · +68 |
| 2021 | 1.804 · +610 | 1.626 · +462 | 1.453 · +337 | 1.332 · +232 | 1.105 · +72 |
| 2022 | 1.414 · +172 | 1.349 · +134 | 1.258 · +98 | 1.237 · +78 | 1.154 · +45 |
| 2023 | 1.338 · +163 | 1.223 · +110 | 1.102 · +64 | 0.995 · +25 | 0.927 · −6 |
| 2024 | 1.540 · +417 | 1.378 · +293 | 1.290 · +214 | 1.236 · +159 | 1.139 · +80 |
| 2025 | 1.542 · +463 | 1.372 · +303 | 1.298 · +235 | 1.253 · +156 | 1.119 · +70 |
| 2026 | 1.462 · +285 | 1.310 · +179 | 1.237 · +139 | 1.194 · +97 | 1.051 · +9 |

- **Monotone: every second of waiting costs edge**, in every year. At 60 s the trade fills a median 80 s after the
  reversal (p90 160 s) at a median +0.36 % above it (lower on 35 %); the result is V2-27's 1m-high break almost exactly
  (PF 1.358 vs 1.380) — both buy the first pause.
- **Cascades pause.** The live tail losers went 60 s without a new low within about a minute, at nearly the reversal's
  price, and then resumed: RAYA 1.434 → 1.451 (exit 0.890), AEHL 23.585 → 23.700, AMOD 1.849 → 1.709, RETO 8.885 → 9.084.
  The 30 worst trades average −46.4 % → −28.3 % at 60 s, −22.1 % at 300 s: the tail shrinks with the wait, the edge
  faster.
- **The live weeks:** from 10-12, reversal −8.8 % of equity, quiet 60 s −21.8 %, 120 s −7.3 %, 300 s −1.9 %. Only the
  long wait helps there, and over 2020–26 that wait leaves PF 1.09 (2023 below 1): it trades the edge away for the tail.
- **Verdict:** no quiet-time rule improves the book; 60 s ≈ V2-27. The flush entry stays.

## V2-29 — the gates PEELED OFF: are the big losers really more frequent? (2026-11-07, user)

**User:** "keep removing the gates as a sanity check ... keep the >= 40bps volatility, and the gap count ... then see if the
number of big losers really is higher than usual ... the gates we're using currently could be overfit, and simplifying the
system will give us a more honest view of the true state of the market." `scripts/equity/flushfaderv2_gate_ladder.py`:
gates-off slices of base_v24 (2020-01 … 2026-08-21, 8.44M trips) and of the 08-24 … 11-06 base run (today's universe),
each layer a mask + greedy mc=1 per ticker-day, every trade ONE equal unit (the sizing model belongs to layer E), raw
`ret_exit` (no maker credit). The frame everywhere: dv_0945 ≥ \$1.6M, ≥ \$1 raw, 09:45–15:00, barnum ≥ 11. **Control:**
layer E = 35,795 trades vs the production book's 35,767 (the 28 = the early-close entry cutoff, not modelled here).

| layer | 2020–26 trades / wk | mean | PF | ≤ −20 % per 1,000 | ≤ −20 % per 4 wk | from 10-12: trades | mean | PF | ≤ −20 % | percentile among 4-week stretches: big losers · Σ return |
|---|---|---|---|---|---|---|---|---|---|---|
| A volat ≥ 40 + gap_60 < 40 | 267 | +0.12 % | 1.097 | 3.0 | 3.21 | 1,004 | +0.03 % | 1.019 | 10 | p96 · **p31** |
| B + freshness (lows300, eff10, v10r, z20, lows180) | 185 | +0.32 % | 1.300 | 2.9 | 2.12 | 624 | −0.07 % | 0.953 | 9 | p99 · p7 |
| C + crf + coil | 113 | +0.49 % | 1.460 | 3.6 | 1.63 | 339 | −0.41 % | 0.797 | 5 | p92 · p0 |
| D + rule140 + the halt wait | 111 | +0.50 % | 1.484 | 3.1 | 1.35 | 331 | −0.42 % | 0.793 | 5 | p95 · p0 |
| E + near-low + ATR5 = production | 103 | +0.56 % | 1.544 | 3.1 | 1.28 | 308 | −0.15 % | 0.916 | 4 | p91 · p2 |

The big-loser rate (≤ −20 % per 1,000 trades) by year, layer A / layer E: 2020 1.7 / 1.8 · 2021 1.0 / 1.1 · 2022 3.2 / 4.2 ·
2023 5.3 / 6.7 · 2024 4.1 / 3.2 · 2025 3.5 / 3.9 · 2026 (to 08-21) 5.1 / 5.2 · **the window by month (A / E): 2026-09
8.5 / 14.3, 10 7.4 / 13.0, 11 (to the 6th) 9.8 / 8.5.**

- **The big losers are really more frequent, with or without the gates.** Every layer's live 4 weeks sit at p91–p99 of
  its own 4-week big-loser counts; the minimal system had 10 (its history averages 3.2). The rate has climbed for years
  (2021 1.0 per 1,000 → 2026 5.1) and the live months run near twice 2026's.
- **The minimal system's 4 weeks were ORDINARY in return (p31)** — the raw flush-fade's PF 1.019 against its 1.097; October
  was even good for it (PF 1.46). The weeks look historically bad only through the gates: each gate group that adds
  history (freshness +0.20 %/trade, crf + coil +0.17 %) SUBTRACTED live (−0.10 %, −0.34 %), and the gated layers' 4 weeks
  are p0–p7. Four weeks cannot separate overfit gates from a regime that turned against what they select, but the order
  is consistent: the more selection, the worse the live gap to history.
- Near-low + ATR5 are the exception (+0.27 %/trade live, ATR5 = WFF 10-14).

## V2-30 — since June: big losers or the body? And SpikeFader-depth leg floors (2026-11-07, user)

**User:** "You said that performance has been getting worse since June. Was that because there were more big losers during
that period, excluding the last 4 weeks? It might be worth experimenting with tighter leg counter gates in the direction
that SpikeFader has." (SpikeFader: highs300 ≥ 40, highs600 ≥ 90, highs180 ≥ 15; FlushFader: lows300 ≥ 6, lows180 ≥ 3,
no 600 rung — 40 / 15 / 90 sit at ≈ p90 of the production book's lows300 / lows180 / lows600.)

**1. The decomposition** (the V2-25 production book to 08-21 + the 08-24 … 11-06 parity book; V2-26's metric, % of equity
per month; a big loser = ret + maker credit ≤ −20 %):

| period | trades / mo | % eq / mo | big losers / mo | their % eq / mo | the rest's % eq / mo | PF | PF ex big | avg win | avg loss ex big |
|---|---|---|---|---|---|---|---|---|---|
| 2020–24 | 436 | +32.8 | 1.13 | −3.3 | +36.1 | 1.64 | 1.79 | 2.31 % | −2.81 % |
| 2025 H2 | 533 | +44.7 | 2.16 | −8.0 | +52.8 | 1.61 | 1.83 | 2.46 % | −2.99 % |
| 2026 Jan–May | 455 | +39.6 | 1.83 | −5.5 | +45.1 | 1.58 | 1.85 | 2.52 % | −3.01 % |
| **2026 Jun – 10-09** | 392 | +27.7 | **3.28** | −8.4 | +36.1 | 1.23 | 1.51 | 2.82 % | **−3.59 %** |
| live 10-12 … 11-06 | 308 | −8.8 | 4.00 | −26.2 | +17.5 | 0.95 | 1.30 | 2.53 % | −3.69 % |

Months: 06 +54.1 · **07 +8.7** (big losers only −8.8; the body failed: PF ex big 1.20, median +0.92 %) · 08 +39.6 ·
**09 +9.9** (big losers −17.7) · 10 +4.8 (−24.2).

Per trade, Jun – 10-09 against Jan–May (bp of equity per trade, by return band):

| band | ≤ −20 % | −20 … −10 | −10 … −5 | −5 … 0 | 0 … 5 | 5 … 10 | > 10 | total |
|---|---|---|---|---|---|---|---|---|
| share Jan–May → Jun–10-09 | 0.4 → 0.8 % | 1.3 → 2.4 % | 4.6 → 5.4 % | 25.1 → 26.1 % | 61.6 → 56.0 % | 6.3 → 8.1 % | 0.7 → 1.1 % | |
| bp eq / trade Jan–May → Jun–10-09 | −1.2 → −2.1 | −1.8 → −3.4 | −3.0 → −3.4 | −4.0 → −4.7 | +12.8 → +12.2 | +4.8 → +6.4 | +1.1 → +2.1 | +8.7 → +7.1 |

- **Partly.** The ≤ −20 % tail doubled per trade (0.4 → 0.8 %) and per month nearly tripled against 2020–24, but it explains
  only −2.9 of the −11.9 % eq / month drop from Jan–May. The −10 … −20 % band doubled too (−1.6 bp / trade), every loss
  band deepened (−3.6 bp in all), and the winners grew (+2.0 bp): the whole distribution widened, losers faster than
  winners. Fewer trades (455 → 392 / month) make the rest. July was a body month, September and October tail months.

**2. Tighter leg floors** (`scripts/equity/flushfaderv2_leg_floors.py`: V2-29's layer E with the lows floors replaced, greedy
mc=1 re-run inside each mask — a cut trade can be replaced by a later signal — equal units, raw ret; control: the
production floors = 35,795 vs the book's 35,767, as V2-29; the random column = PF p5–p95 of 300 same-n random subsamples
of the production book):

| lows300 · lows180 · lows600 | trades | mean | PF | random same-n PF | ≤ −20 % per 1,000 | 2020–24 | 2025 | 2026 Jan–May | Jun – 08-21 | 08-24 … 10-09 n · PF · Σret | live n · PF · Σret · ≤ −20 % |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 6 · 3 · 0 (production) | 35,795 | +0.56 % | 1.544 | — | 3.1 | 1.58 | 1.49 | 1.52 | 1.26 | 478 · 1.04 · +33 % | 308 · 0.92 · −45 % · 4 |
| 10 · 3 · 0 | 30,795 | +0.60 % | 1.593 | 1.53–1.56 | 3.3 | 1.63 | 1.54 | 1.48 | 1.36 | 396 · 1.05 · +36 % | 254 · 0.77 · −106 % · 4 |
| 15 · 3 · 0 | 24,724 | +0.63 % | 1.626 | 1.51–1.58 | 3.8 | 1.68 | 1.59 | 1.44 | 1.27 | 306 · 1.10 · +54 % | 217 · 0.81 · −76 % · 3 |
| 20 · 3 · 0 | 19,619 | +0.66 % | 1.643 | 1.51–1.59 | 4.1 | 1.70 | 1.59 | 1.50 | 1.20 | 235 · 0.98 · −11 % | 167 · 0.67 · −113 % · 3 |
| 30 · 3 · 0 | 12,339 | +0.69 % | 1.659 | 1.49–1.61 | 5.4 | 1.70 | 1.66 | 1.46 | 1.25 | 145 · 0.91 · −26 % | 116 · 0.74 · −65 % · 3 |
| 40 · 3 · 0 | 7,967 | +0.65 % | 1.587 | 1.46–1.63 | 6.5 | 1.60 | 1.60 | 1.71 | 1.12 | 90 · 0.80 · −42 % | 81 · 0.62 · −73 % · 2 |
| 6 · 8 · 0 | 32,456 | +0.58 % | 1.574 | 1.53–1.56 | 3.3 | 1.61 | 1.52 | 1.53 | 1.23 | 418 · 1.04 · +27 % | 271 · 0.83 · −81 % · 4 |
| 6 · 15 · 0 | 22,934 | +0.64 % | 1.635 | 1.51–1.58 | 3.8 | 1.68 | 1.61 | 1.45 | 1.28 | 274 · 1.08 · +42 % | 201 · 0.75 · −99 % · 3 |
| 6 · 3 · 30 | 16,929 | +0.65 % | 1.644 | 1.49–1.59 | 4.6 | 1.66 | 1.69 | 1.57 | 1.32 | 179 · 0.98 · −5 % | 159 · 0.79 · −68 % · 4 |
| 6 · 3 · 60 | 7,156 | +0.58 % | 1.512 | 1.47–1.65 | 6.4 | 1.49 | 1.64 | 1.72 | 1.19 | 72 · 0.72 · −45 % | 74 · 0.68 · −42 % · 2 |
| 6 · 3 · 90 | 3,123 | +0.48 % | 1.378 | 1.42–1.71 | 7.7 | 1.34 | 1.54 | 1.71 | 1.00 | 25 · 0.22 · −83 % | 40 · 0.62 · −32 % · 2 |
| 15 · 8 · 30 | 14,926 | +0.68 % | 1.667 | 1.49–1.60 | 4.9 | 1.70 | 1.67 | 1.64 | 1.17 | 164 · 1.01 · +4 % | 138 · 0.67 · −98 % · 3 |
| 20 · 15 · 60 | 5,803 | +0.63 % | 1.553 | 1.45–1.64 | 7.6 | 1.56 | 1.52 | 1.90 | 0.99 | 60 · 0.64 · −53 % | 65 · 0.70 · −33 % · 1 |
| **40 · 15 · 90 (SpikeFader's)** | 2,139 | +0.56 % | 1.426 | 1.38–1.72 | **10.8** | 1.44 | 1.38 | 1.68 | 0.84 | 18 · 0.19 · −76 % | 29 · 0.86 · −8 % · 1 |

- **Deeper legs carry MORE big losers on the long side, not fewer:** the ≤ −20 % rate climbs monotonically with every floor
  (3.1 → 5.4 per 1,000 at lows300 ≥ 30; 10.8 at SpikeFader's floors). A deep flush is a cascade more often — the same
  side-flip inversion as `feedback_side_flip_inverts_rulings`: a long leg of new highs exhausts, a long leg of new lows
  is a collapse.
- The moderate floors (lows300 15–30, lows180 15, lows600 30) lift PF 1.544 → 1.63–1.67, just past the random band, but
  ALL of it is 2020–25; 2026 Jan–May is equal or worse (1.44–1.57 vs 1.52), Jun–Aug no better, and the live 4 weeks are
  worse at every floor (PF 0.92 → 0.62–0.81). Σreturn falls with the trade count (15 · 3 · 0: 156 units vs 200).
- **Not adopted.** The leg floors do not address the tails; tightening them toward SpikeFader trades breadth for depth,
  and depth is where the live losers came from.

## V2-31 — a cap on the 1m drop (the 60-bar range) (2026-11-07, user)

**User:** "Back when we were doing MR experiments on 1m bars, we noticed that drops higher that 12% in 1m did quite poorly
going forward. What if we create a gate using the 60-bar range to avoid buying into some of these more significant
collapses?" (LowFlyer kept `1m ≥ −12 %`; LowFader's port found it already absorbed, `docs/lowfader_results.md`.)
Feature: `d1m` = signal vwap / the 60-bar high − 1 (the gate-review tool's; at a new-low signal the 60-bar range).
`scripts/equity/flushfaderv2_d1m_cap.py`, V2-30's harness (layer E, greedy re-run, equal units, raw ret; control 35,795).

| 1m drop | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2026 Jan–Aug n · PF | 08-24 … 10-09 n · PF · Σret · big | live n · PF · Σret · big |
|---|---|---|---|---|---|---|---|
| ≤ −15 % | 58 | +0.98 % | 1.26 | 52 | 9 · 12.6 | 3 · 0.52 · −15 % · 1 | 0 |
| −15 … −12 % | 91 | +2.20 % | 1.68 | 33.0 | 18 · 0.40 | 0 | 2 · 1.69 · +3 % · 0 |
| −12 … −8 % | 631 | +1.42 % | 1.67 | 14.3 | 121 · 1.64 | 19 · 0.71 · −23 % · 0 | 13 · 2.27 · +27 % · 0 |
| −8 … −5 % | 2,806 | +0.93 % | 1.56 | 10.0 | 400 · 1.40 | 59 · 0.73 · −40 % · 2 | 31 · 0.73 · −28 % · 2 |
| −5 … −3 % | 8,220 | +0.87 % | 1.73 | 4.5 | 802 · 1.49 | 134 · 1.06 · +17 % · 2 | 75 · 0.69 · −67 % · 2 |
| > −3 % | 23,989 | +0.38 % | 1.44 | 1.3 | 2,088 · 1.38 | 263 · 1.36 · +94 % · 0 | 187 · 1.11 · +21 % · 0 |

Caps (greedy re-run): none 35,795 · PF 1.544 · −15 % 35,751 · 1.546 · −12 % 35,694 · 1.547 · −10 % 35,590 · 1.541 · −8 %
35,300 · 1.538 (random same-n bands ±0.005); live 4 weeks unchanged to −12 % (PF 0.92 → 0.91), worse below.

- **The −12 % knife is already gone:** 149 of 35,795 trades (0.4 %) drop more than 12 % in the minute — the freshness,
  crf and volatility gates removed it, as on LowFader. A cap at −12 % moves PF by +0.003.
- **The tail rate IS monotone in the 1m drop** (1.3 per 1,000 above −3 % → 10 at −5 … −8 % → 33–52 below −12 %), but so is
  the mean (+0.38 % → +0.9–2.2 %): deeper drops are higher variance and higher mean, not worse trades.
- **The live losers were shallow in the minute:** AMOD −3.6 %, AEHL −4.7 %, RAYA −7.6 % (the deepest), WFF −5.7 %, CHSN
  −4.0 %, BRAI −1.9 %. Catching them takes a −3 % cap, which cuts a third of the book including its best band
  (−5 … −3 %, PF 1.73). **Not adopted.**

## V2-32 — CAPS on the leg counts (2026-11-07, user)

**User:** "Should we put a max on the leg counts? I see that there is a cliff at some point where PF goes lower."
`scripts/equity/flushfaderv2_leg_caps.py` (V2-30's harness; control 35,795). Bands of the production book:

| band | trades | mean | PF | ≤ −20 % per 1,000 | 2020–24 PF | 2025 PF | 2026 Jan–Aug PF |
|---|---|---|---|---|---|---|---|
| lows300 6 … 59 | 34,342 | ≈ +0.57 % | 1.46–1.63 by band | 2.3–4.9 | 1.54–1.67 | 1.28–1.84 | 0.96–2.04 |
| lows300 60 … 99 | 1,044 | +0.24 % | 1.20 | 4.8 | 1.15 | 1.43 | 1.36 |
| lows300 ≥ 100 | 409 | +0.07 % | 1.05 | 12.2 | 0.97 | 1.75 | 1.00 |
| lows600 6 … 89 | 34,024 | ≈ +0.58 % | 1.47–1.81 by band | 2.1–4.1 | 1.36–1.90 | 1.29–2.08 | 1.19–1.70 |
| lows600 90 … 149 | 1,460 | +0.26 % | 1.21 | 5.5 | 1.15 | 1.42 | 1.50 |
| lows600 ≥ 150 | 311 | +0.10 % | 1.07 | 12.9 | 1.03 | 1.16 | 1.59 |
| lows180 20 … 29 | 4,607 | +0.44 % | 1.42 | 2.8 | 1.47 | 1.49 | 1.03 (08-24 … 10-09 0.58, live 0.48) |
| lows180 30 … 49 / ≥ 50 | 2,457 / 1,012 | +0.51 / +0.44 % | 1.49 / 1.36 | 5.3 / 7.9 | 1.47 / 1.32 | 1.45 / 1.43 | 1.82 / 1.87 |

Caps (greedy re-run; random = PF p5–p95 of same-n random cuts):

| cap | trades | PF | random | ≤ −20 % per 1,000 | Σret (units) | 2026 Jan–May | Jun – 08-21 | 08-24 … 10-09 PF · Σret | live PF · Σret · big |
|---|---|---|---|---|---|---|---|---|---|
| none | 35,795 | 1.544 | — | 3.1 | +199 | 1.52 | 1.26 | 1.04 · +33 % | 0.92 · −45 % · 4 |
| lows300 ≤ 60 | 34,400 | **1.567** | 1.535–1.553 | 3.0 | +197 | 1.53 | 1.27 | 1.04 · +27 % | 0.93 · −36 % · 4 |
| lows600 ≤ 90 | 34,085 | **1.568** | 1.535–1.554 | 2.9 | +195 | 1.53 | 1.25 | 1.05 · +39 % | 0.92 · −40 % · 3 |
| lows180 ≤ 20 | 28,823 | 1.577 | 1.521–1.566 | 2.8 | +168 | 1.50 | 1.40 | 1.18 · +109 % | 1.06 · +23 % · 3 |
| 60 · 30 · 90 | 31,265 | 1.580 | 1.527–1.560 | 2.8 | +183 | 1.48 | 1.26 | 1.04 · +30 % | 1.00 · −1 % · 3 |
| 40 · 20 · 60 | 25,959 | 1.602 | 1.518–1.571 | 2.6 | +156 | 1.52 | 1.35 | 1.20 · +109 % | 1.12 · +43 % · 2 |

- **The cliff is real at the very deep end:** lows300 ≥ 60 and lows600 ≥ 90 (≈ 4–5 % of the book) earn +0.07–0.26 % at
  PF 1.05–1.21 with 2–4× the tail rate. A cap there clears the random band (PF +0.023) but at equal units leaves Σreturn
  flat (+199 → +195–197): those trades are ≈ break-even, not losers. It is a 2020–24 effect (PF 0.97–1.15); 2025–26 deep
  legs are ordinary (1.00–1.75) and the cap does nothing in 2026 or live.
- **lows180 ≤ 20 is a different thing:** it cuts the 20 … 29 band (weak in 2026 and live) together with 30+, which is no
  worse than the middle historically (1.49 / 1.36, 2026 1.82 / 1.87). The live improvement is AMOD (lows 26) and ALIT
  (20); Σreturn −16 %. Fitted to the live losers; not a cliff.
- The live losers sat mostly in the middle of every counter (RAYA 10, CHSN 11–14, AEHL 18, AMOD 26; WFF's lows600 109 the
  one deep exception).
- **Verdict:** lows300 ≤ 60 (or lows600 ≤ 90) is a defensible, cheap tidy-up — PF up, tails slightly down, return
  unchanged — not a fix for the current losses. Not built (user: no changes during the 4-week trial).

## V2-33 — the 60-session MEDIAN VOLUME as a floor (2026-11-07, user)

**User:** "check how 60d median volume affects the trades. I see that many of our losses are in low float thrash that
even a modest median would have filtered out." `scripts/equity/flushfaderv2_med60.py` (V2-30's harness; control 35,795).
Causal: the 60 sessions BEFORE D from `data/v2/daily_adjusted.parquet` — shares `n(D) × median(volume / n)` (D's share
scale), dollars `median(close × volume)`; < 20 prior sessions = young, passes every floor (1.4 % of the book).

| median shares (60 d) | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2026 Jan–Aug n · PF | 08-24 … 10-09 n · PF · Σret | live n · PF · Σret · big |
|---|---|---|---|---|---|---|---|
| < 50k | 4,596 | **+0.93 %** | **1.70** | 4.4 | 772 · 1.52 | 156 · 1.00 · +1 % | 68 · 0.87 · −23 % · 1 |
| 50k … 100k | 2,789 | +0.66 % | 1.49 | 6.5 | 425 · 1.56 | 54 · 0.86 · −16 % | 31 · 1.01 · +1 % · 1 |
| 100k … 250k | 4,570 | +0.72 % | 1.62 | 5.0 | 428 · 1.67 | 116 · 1.48 · +77 % | 17 · 0.43 · −51 % · 2 |
| 250k … 500k | 3,755 | +0.51 % | 1.47 | 4.0 | 255 · 1.50 | 22 · 0.72 · −12 % | 18 · 0.62 · −12 % · 0 |
| 500k … 1M | 4,117 | +0.42 % | 1.42 | 2.4 | 302 · 1.86 | 53 · 0.78 · −17 % | 22 · 1.33 · +8 % · 0 |
| 1M … 3M | 6,947 | +0.41 % | 1.47 | 1.7 | 622 · 1.08 | 48 · 0.72 · −16 % | 61 · 0.99 · −1 % · 0 |
| ≥ 3M | 8,496 | +0.45 % | 1.59 | 1.6 | 598 · 0.90 | 28 · 1.48 · +11 % | 75 · 1.74 · +34 % · 0 |

(Dollar bands say the same: < \$0.25M +0.90 % PF 1.70, ≥ \$30M +0.31 % PF 1.39.)

| floor (greedy re-run) | trades | PF | random same-n | ≤ −20 % per 1,000 | Σret (units) | 2026 Jan–May | Jun – 08-21 | 08-24 … 10-09 PF · Σret | live PF · Σret · big |
|---|---|---|---|---|---|---|---|---|---|
| none | 35,795 | 1.544 | — | 3.1 | +199 | 1.52 | 1.26 | 1.04 · +33 % | 0.92 · −45 % · 4 |
| shares ≥ 50k | 31,199 | 1.513 | 1.527–1.561 | 2.9 | +157 | 1.55 | 1.09 | 1.07 · +32 % | 0.94 · −22 % · 3 |
| shares ≥ 100k | 28,410 | 1.516 | 1.522–1.564 | 2.6 | +138 | 1.49 | 1.02 | 1.13 · +48 % | 0.92 · −23 % · 2 |
| shares ≥ 250k | 23,840 | 1.490 | 1.510–1.574 | 2.1 | +105 | 1.31 | 1.00 | 0.86 · −29 % | 1.15 · +28 % · 0 |
| shares ≥ 500k | 20,085 | 1.494 | 1.508–1.583 | 1.8 | +86 | 1.22 | 1.02 | 0.89 · −17 % | 1.25 · +40 % · 0 |
| shares ≥ 1M | 15,968 | 1.516 | 1.495–1.596 | 1.6 | +69 | 1.13 | 0.81 | 1.00 · +0 % | 1.24 · +31 % · 0 |
| \$ ≥ 1M | 22,166 | 1.444 | 1.509–1.584 | 2.1 | +88 | 1.22 | 0.95 | 0.85 · −31 % | 1.04 · +8 % · 1 |

Live losers' median shares: RAYA 10k, TCRT 16k, BRAI 28k, CHSN 43k, TNMG 50k, AMOD 55k, AEHL 167k, WFF 183k, FTCI 219k,
ALIT 428k, STLN 1.16M, SNDX 1.66M.

- **The user's read of the live losers is right:** 8 of the 12 worst sat under 250k shares a day, and thin names do
  carry 2–4× the tail rate (4–6.5 per 1,000 vs 1.6 at ≥ 1M).
- **But the thin names ARE the edge:** under 50k shares is the book's best band (+0.93 % a trade, PF 1.70; liquid names
  +0.4 %), in every period through 2026 Jan–Aug (1.52–1.67, while ≥ 1M fell to 0.90–1.08). Every floor puts PF BELOW
  the random band and cuts Σreturn 21–65 %; a 250k floor makes 2026 Jan–May 1.52 → 1.31 and 08-24 … 10-09 negative.
- Only the live 4 weeks favour a floor — another live inversion of a historically strong region (as dslo 25–50 %).
  **Not adopted.** If thin-name tails keep repeating, a units cut there (not a floor) is the variant to replay.

**The model vs live reconciliation (asked the same turn).** V2-26's % is of the \$8,000 SIZING equity, every book trade
at the production size formula with no concurrency cap, compounding or costs. 10-14 … 11-06 at \$8k:
model (today's build) −\$1,197 (−15.0 %; the −8.8 % counts 10-12/13, +\$496) · minus the model's trades not taken live
(42 ticker-days, +\$619 — FUFU 10-19, CDT 11-06, CHSN 10-28) · trades both took: model −\$1,816 → live −\$2,576
(−\$760: L2's sizes, its adds to falling positions, fills) · live-only +\$193 · WFF 10-14 −\$953 (blocked in today's build
by the ATR5 gate, added after) ⇒ **live FlushFader ≈ −\$3,340 = −42 % of the sizing equity**. The broker account
(29,945 → 27,272, −8.9 %) holds the other systems too (Snoozer BRAI +\$1,163).

## V2-34 — the TWO-DAY RANGE gate, against ATR5 (2026-11-07, user)

**User:** "A range filter that takes the max of yesterday's high and current session's and min of yesterday's low and
current session's. Maybe that could work better than the ATR 5 filter." `scripts/equity/flushfaderv2_range2.py` (V2-30's
harness with ATR5 made switchable; control: production = 35,795 as before). `rng2 = max(H[D−1], sess_high) /
min(L[D−1], sess_low)`, the session extremes AT THE SIGNAL (the trip's own), D−1 = the previous daily_adjusted row on
D's raw scale (× n[D−1] / n[D]). Coverage 100 %.

| two-day range | 2020–26 trades | mean | PF | ≤ −20 % per 1,000 | 2025 PF | 2026 Jan–Aug PF | 08-24 … 10-09 n · PF · Σret · big | live n · PF · Σret · big |
|---|---|---|---|---|---|---|---|---|
| < 1.25 | 6,116 | +0.34 % | 1.47 | 1.0 | 1.42 | 0.94 | 47 · 1.49 · +15 % · 0 | 71 · 1.06 · +3 % · 0 |
| 1.25 … 1.5 | 10,105 | +0.42 % | 1.47 | 1.8 | 1.43 | 1.08 | 76 · 0.52 · −84 % · 1 | 85 · 1.70 · +42 % · 0 |
| 1.5 … 2 | 9,383 | +0.59 % | 1.57 | 2.6 | 1.59 | 1.33 | 170 · 2.09 · +177 % · 0 | 70 · 0.69 · −59 % · 1 |
| 2 … 2.5 | 4,033 | +0.75 % | 1.67 | 2.2 | 1.75 | 1.95 | 67 · 1.39 · +27 % · 0 | 28 · 1.34 · +21 % · 1 |
| 2.5 … 3 | 2,167 | +0.78 % | 1.63 | 5.5 | 1.30 | 1.99 | 45 · 1.23 · +16 % · 0 | 23 · 0.47 · −32 % · 0 |
| 3 … 4 | 1,939 | +0.92 % | 1.69 | 6.7 | 1.79 | 1.99 | 31 · 0.85 · −9 % · 0 | 10 · 0.65 · −17 % · 1 |
| 4 … 6 | 1,338 | +0.69 % | 1.40 | 12.7 | 1.01 | 1.51 | 19 · 0.28 · −60 % · 2 | 11 · 0.90 · −2 % · 0 |
| ≥ 6 | 714 | +0.97 % | 1.48 | 18.2 | 1.42 | 1.38 | 23 · 0.60 · −49 % · 2 | 10 · 1.00 · +0 % · 1 |

| variant (greedy re-run) | trades | PF | random same-n | ≤ −20 % per 1,000 | Σret (units) | 2025 | 2026 Jan–May | Jun – 08-21 | 08-24 … 10-09 PF · Σret · big | live PF · Σret · big |
|---|---|---|---|---|---|---|---|---|---|---|
| production (ATR5) | 35,795 | 1.544 | — | 3.1 | +199 | 1.49 | 1.52 | 1.26 | 1.04 · +33 % · 5 | 0.92 · −45 % · 4 |
| no ATR5 | 35,824 | 1.542 | — | 3.2 | +199 | 1.48 | 1.52 | 1.26 | 1.04 · +33 % · 5 | 0.80 · −126 % · 5 |
| no ATR5, rng2 < 6 | 35,096 | 1.546 | 1.538–1.549 | 2.8 | +192 | 1.49 | 1.59 | 1.18 | 1.12 · +81 % · 3 | 0.91 · −45 % · 3 |
| no ATR5, rng2 < 4 | 33,758 | 1.557 | 1.532–1.555 | 2.4 | +183 | 1.54 | 1.62 | 1.11 | 1.25 · +142 % · 1 | 0.91 · −43 % · 3 |
| no ATR5, rng2 < 3 | 31,819 | 1.546 | 1.529–1.559 | 2.2 | +165 | 1.52 | 1.59 | 1.01 | 1.30 · +151 % · 1 | 0.94 · −25 % · 2 |
| no ATR5, rng2 < 2.5 | 29,647 | 1.537 | 1.524–1.564 | 1.9 | +148 | 1.55 | 1.49 | 0.98 | 1.31 · +136 % · 1 | 1.02 · +7 % · 2 |
| no ATR5, rng2 < 2 | 25,610 | 1.510 | 1.516–1.572 | 1.9 | +118 | 1.50 | 1.37 | 0.84 | 1.29 · +109 % · 1 | 0.96 · −14 % · 1 |

(ATR5 + rng2 < X = no ATR5 + rng2 < X to within 15 trades: rng2 subsumes ATR5.)

The window's death spirals: WFF 10-14 rng2 14.4 (ATR5 7.18 — the one ATR5 catches) · RETO 09-16 59.6 (ATR5 0.32) · WFF
10-13 8.9 (0.71) · SXTC 10-07 8.1 · MSGY 09-28 5.8 · IMCC 09-18 5.3 · CHSN 11-04 4.7 · AEHL 10-14 3.7 · ZTG 3.5 — but
RAYA 11-06 2.3, AMOD 1.9, BRAI 1.8, XHLD 1.5 (quiet yesterday, the collapse came from inside the day).

- **ATR5 is inert historically** (29 trades, PF 1.542 → 1.544); its whole value is WFF 10-14. rng2 sees TODAY's pump as
  well as yesterday's, so it catches WFF on both days, RETO, SXTC, MSGY, IMCC — the class ATR5 was meant for.
- **The tail rate climbs steeply with the range** (1.0 → 18 per 1,000) while the mean also rises — the usual variance
  trade, but above ~4 the PF bends (4 … 6: 1.40, 2025 1.01).
- **rng2 < 6:** PF neutral (1.546), tails −10 %, Σreturn −3.5 %; the pre-live window +33 → +81 %. **rng2 < 4:** PF 1.557
  (at the random band's edge), tails −23 %, Σreturn −8 %, 2026 Jan–May 1.52 → 1.62 but Jun–Aug 1.26 → 1.11; pre-live
  +142 %. Either replaces ATR5 outright. Neither catches RAYA or AMOD.
- **Candidate, not built:** a strict superset of ATR5's job. The live port is cheap (yesterday's H/L/n from the reference
  dailies, as Reference.atr5s; sess_high/sess_low already in the engine).

## V2-35 — 6-month momentum: the share of days below the 40-day SMA (2026-11-07, user)

**User:** "calculating the daily 40d moving average and calculating how many days the stock has been below it in the
last 6 months. I wonder if stocks which have strongly negative momentum based on that measure would be poorer dip buys.
Maybe we could at least filter out low volume < 250k stocks with persistently negative 6-month momentum?"
`scripts/equity/flushfaderv2_below40.py` (V2-30's harness; control 35,795): close × n (causal scale), SMA40, `below6m` =
the share of the 126 sessions BEFORE D closing under their SMA40 (≥ 60 rows, else NaN = passes; 91 % covered, median
0.60); thin = V2-33's med60 < 250k shares.

| below6m | all: trades · mean · PF · ≤ −20 %/1,000 | 2026 Jan–Aug PF | thin: trades · mean · PF · ≤ −20 %/1,000 | thin 2026 Jan–Aug PF | live (all) n · PF · Σret |
|---|---|---|---|---|---|
| < 30 % | 3,601 · +0.35 % · 1.37 · 3.3 | 1.02 | 465 · +0.67 % · 1.55 · 8.6 | 2.47 | 14 · 1.25 · +2 % |
| 30 … 50 % | 7,764 · +0.54 % · 1.60 · 1.5 | 0.75–1.14 | 1,465 · +0.79 % · 1.68 · 2.0 | 0.78–2.20 | 57 · 1.29 · +18 % |
| 50 … 70 % | 10,275 · +0.54 % · 1.53 · 2.5 | 1.37–1.53 | 3,399 · +0.76 % · 1.62 · 4.7 | 1.27–1.77 | 78 · 1.53 · +34 % |
| 70 … 80 % | 4,422 · +0.70 % · 1.68 · 2.7 | 1.55 | 2,013 · +0.93 % · 1.77 · 2.5 | 1.61 | 60 · 0.64 · −55 % |
| 80 … 90 % | 3,895 · +0.66 % · 1.60 · 4.6 | 1.69 | 1,990 · +0.86 % · 1.71 · 5.0 | 1.73 | 40 · 0.90 · −9 % |
| ≥ 90 % | 2,714 · +0.73 % · 1.59 · 7.0 | 1.70 | 1,690 · +0.84 % · 1.62 · 8.3 | 1.60 | 27 · 0.78 · −17 % |

| gate (greedy re-run) | trades | PF | random same-n | ≤ −20 %/1,000 | Σret (units) | 2026 Jan–May | Jun – 08-21 | 08-24 … 10-09 PF · Σret | live PF · Σret · big |
|---|---|---|---|---|---|---|---|---|---|
| none | 35,795 | 1.544 | — | 3.1 | +199 | 1.52 | 1.26 | 1.04 · +33 % | 0.92 · −45 % · 4 |
| below6m < 90 % | 33,081 | 1.539 | 1.533–1.556 | 2.8 | +179 | 1.46 | 1.19 | 0.97 · −20 % | 0.94 · −28 % · 3 |
| below6m < 80 % | 29,186 | 1.530 | 1.525–1.565 | 2.6 | +153 | 1.33 | 1.18 | 0.90 · −52 % | 0.95 · −19 % · 2 |
| below6m < 70 % | 24,764 | 1.502 | 1.512–1.575 | 2.5 | +122 | 1.24 | 1.11 | 0.59 · −166 % | 1.17 · +36 % · 1 |
| thin ∧ ≥ 90 % off | 34,105 | 1.539 | 1.534–1.555 | 2.9 | +185 | 1.49 | 1.20 | 0.99 · −6 % | 0.95 · −21 % · 3 |
| thin ∧ ≥ 80 % off | 32,115 | 1.526 | 1.530–1.557 | 2.7 | +168 | 1.42 | 1.16 | 0.93 · −39 % | 1.02 · +8 % · 2 |
| thin ∧ ≥ 70 % off | 30,102 | 1.506 | 1.524–1.563 | 2.8 | +149 | 1.36 | 1.11 | 0.67 · −142 % | 1.19 · +51 % · 1 |
| thin ∧ ≥ 60 % off | 28,013 | 1.500 | 1.520–1.567 | 2.5 | +134 | 1.32 | 1.16 | 0.83 · −47 % | 1.16 · +40 % · 1 |

The window's losers: RAYA 1.00 (10k sh), IMCC 0.97, AMOD 0.87, DCOY 0.85, BRAI 0.84, CHSN 0.76, MSGY 0.74, AEHL 0.71 —
but RETO 0.67, SXTC 0.65, ZTG 0.58, WFF 10-13 0.49, XHLD 0.48.

- **The user's picture of the live losers is right** (most were persistent downtrenders, several thin), and persistent
  downtrend does carry 2× the tail rate (≥ 80 %: 4.6–7 per 1,000 vs 1.1–1.9 at 30–60 %).
- **But persistent downtrenders are better dip buys, not worse:** mean +0.66–0.73 % vs +0.35 % for the strongest names
  (< 30 %), and in 2026 Jan–Aug the ≥ 70 % bands are the BEST (PF 1.55–1.70); thin downtrenders likewise (1.60–1.73).
  A dip in a stock that is always falling is a sharper overreaction, the MR thesis itself.
- Every gate falls under the random band or onto its edge and costs 7–53 % of Σreturn; each one HURTS the 7 pre-live
  weeks (+33 % → −6 … −166 %) while helping the live 4 — the same live-only inversion as the dslo band and med60
  (V2-33). **Not adopted.**

## V2-36 — the range over TWO prior days + the session; the range gates at production size (2026-11-07, user)

**User:** "What do you mean by total return being down 8%?" / "Is the range >= 6 really profitable?" / "What about
range >= 10?" / "let's try 2-day high/low combined with the session's high/low. Will our conclusions change?"
`flushfaderv2_range2.py --prior-days 2` (k = 1 reproduces V2-34's rows exactly): `max(H[D−1], H[D−2], sess_high) /
min(L[D−1], L[D−2], sess_low)`. ⚠ V2-34's "Σret" is EQUAL-unit (no sizing); at production size (the V2-25 book,
V2-26's % of equity, the gated trades removed without replacement) the gated bands are worth MORE, because the sizing
model sizes wide ranges up:

| gated band (production book, 2020-01 … 2026-08-21) | trades | ticker-days | mean | median | PF | ≤ −20 % | % eq | share of +2,716 |
|---|---|---|---|---|---|---|---|---|
| 1 prior day, ≥ 4 | 2,047 | | | | | | +387.5 | 14.3 % |
| 1 prior day, ≥ 6 | 711 | 239 | +1.05 % | +2.21 % | 1.53 | 12 | +161.0 | 5.9 % |
| 1 prior day, ≥ 10 | 138 | 54 | +1.69 % | +3.56 % | 1.67 | 2 | +53.2 | 2.0 % |
| 2 prior days, ≥ 4 | 3,323 | 1,244 | +0.81 % | +1.83 % | 1.50 | 38 | +518.5 | 19.1 % |
| 2 prior days, ≥ 6 | 1,361 | 500 | +0.75 % | +1.87 % | 1.40 | 21 | +220.4 | 8.1 % |
| 2 prior days, ≥ 10 | 358 | 139 | +1.03 % | +2.23 % | 1.49 | 4 | +91.8 | 3.4 % |

The 1-day ≥ 6 band is genuinely profitable (median +2.2 %, 70 % winners, +129 without its 10 best trades; positive
every year but 2023's −0.4) — the gate is insurance (16.9 vs 2.8 big losers per 1,000), not an edge. 1-day 6–10 holds
10 of its 12 big losers.

The harness at k = 2 (equal units, greedy re-run; k = 1 in brackets):

| variant | trades | PF | random same-n | ≤ −20 %/1,000 | Σret | 08-24 … 10-09 Σret · big | live PF · Σret · big |
|---|---|---|---|---|---|---|---|
| no ATR5, < 10 | 35,448 | 1.545 (1.543) | 1.539–1.548 | 3.0 (3.1) | +196 (+197) | +85 % · 4 (+80 · 4) | 0.94 · −33 % · 3 (0.90 · −54 % · 4) |
| no ATR5, < 6 | 34,440 | **1.557** (1.546) | 1.534–1.553 | 2.6 (2.8) | +190 (+192) | +88 % · 3 (+81 · 3) | 0.91 · −45 % · 3 (same) |
| no ATR5, < 4 | 32,474 | 1.559 (1.557) | 1.528–1.558 | 2.2 (2.4) | +174 (+183) | +131 % · 1 (+142 · 1) | 0.93 · −31 % · 3 (0.91 · −43 % · 3) |

- **The conclusions hold, slightly strengthened.** Two prior days see a pump that started the day before yesterday:
  WFF 10-13 8.9 → 28.9, WFF 10-14 14.4 → 46.8 — so **< 10 now catches WFF on both days and RETO** (the 1-day < 10 only
  WFF 10-14 and RETO). The gated band is weaker per trade (≥ 6 PF 1.40 vs 1.53; ≥ 10 1.49 vs 1.67), so each big loser
  removed costs less: ≥ 6 removes 21 of the book's 110 big losers for 8.1 % of the profit (1-day: 12 for 5.9 %), and the
  < 6 harness PF clears the random band (1.557 vs ≤ 1.553). RAYA (2.3), AMOD (1.9), BRAI (2.6) still pass.
- Candidate, not built: 2-prior-day range < 10 as ATR5's replacement (≈ 3.4 % of the profit; WFF ×2 + RETO), or < 6 as
  the stronger insurance (≈ 8 %).

**Higher thresholds (2 prior days, production size, user "What about ≥ 15 or ≥ 20?"):** ≥ 15: 111 trades / 41
ticker-days, median +2.56 %, PF 1.42, 2 big losers (TDIC −84 %, CWD −20 %), +40.6 % eq = 1.5 % of the profit (top 5 trades
+14.8) · ≥ 20: 54 / 23, PF 1.15, 2 big losers (the same), +24.0 = 0.9 % · ≥ 25: 25 / 14, PF 1.62, 1, +16.6 = 0.6 %. The
window's three death spirals are all above 25 (WFF 10-13 28.9, WFF 10-14 46.8, RETO 59.6) and nothing else in the
window is above 8.1 (SXTC), so every threshold from 10 to 25 removes exactly those three; the higher the threshold, the
less history it gives up and the more it relies on the next spiral being as extreme.

## V2-37 — YANG-ZHANG volatility (plain, and with today's session) as the death-spiral gate (2026-11-07, user)

**User:** "Instead of using ATR, what if we tried the Yang Zhang realized volatility estimator? Could we also combine it
with the current session's data assuming the close is the current price?" `scripts/equity/flushfaderv2_yangzhang.py`
(V2-30's harness; header has the formula): `yzN` over the N sessions before D, `yzN_s` adds today's partial day (D's
open, the session high / low at the signal, the signal vwap as the close). Daily σ, not annualized. Cut at the book's top
q. Coverage ≥ 98.4 %; Spearman with ATR5: yz5 0.94, yz10 0.91, yz20 0.83, the `_s` versions 0.53–0.60.

Production book cut (book-only, production size; the book total +2,716 % eq):

| gate | threshold (daily σ) | cut: trades · PF · ≤ −20 % · % eq · share | harness PF (random) | live Σret · big |
|---|---|---|---|---|
| yz5 top 1 % | 0.72 | 358 · 1.29 · 2 · +33.9 · 1.2 % | 1.546 (1.539–1.548) | −33 % · 3 |
| yz5 top 2 % | 0.63 | 719 · 1.41 · 4 · +67.5 · 2.5 % | 1.548 (1.538–1.550) | −33 % · 3 |
| yz5 top 5 % | 0.47 | 1,788 · 1.45 · 14 · +209.3 · 7.7 % | 1.554 (1.534–1.554) | −29 % · 3 |
| yz5_s top 1 % | 0.72 | 358 · 1.54 · 3 · +64.6 · 2.4 % | 1.544 (1.539–1.548) | −33 % · 3 |
| yz5_s top 2 % | 0.64 | 716 · 1.76 · 5 · +135.6 · 5.0 % | 1.539 (1.537–1.550) | −33 % · 3 |
| yz5_s top 5 % | 0.53 | 1,790 · 1.53 · 19 · +271.9 · 10.0 % | 1.548 (1.534–1.555) | −47 % · 3 |
| yz10 top 5 % | 0.38 | 1,788 · 1.43 · 14 · +181.9 · 6.7 % | 1.555 (1.534–1.553) | −16 % · 3 |
| yz20 top 5 % | 0.31 | 1,762 · 1.52 · 12 · +184.5 · 6.8 % | 1.548 (1.532–1.555) | −6 % · 2 |
| for comparison: 2-day range ≥ 15 | | 111 · 1.42 · 2 · +40.6 · 1.5 % | | (WFF ×2 + RETO) |
| 2-day range ≥ 10 | | 358 · 1.49 · 4 · +91.8 · 3.4 % | 1.545 (1.539–1.548) | −33 % · 3 |
| 2-day range ≥ 6 | | 1,361 · 1.40 · 21 · +220.4 · 8.1 % | 1.557 (1.534–1.553) | −45 % · 3 |
| ATR5 ≥ 400 % (production) vs none | | 29 trades in the harness | 1.544 vs 1.542, Σret +199 both | −45 % vs −126 % |

Percentile in the book: WFF 10-14 yz5 99.8 / yz5_s 99.7 · WFF 10-13 99.5 / 99.3 · **RETO 97.4 / 99.1** · MEDS 99.3 /
98.6 · SXTC 80.6 / 97.6 · AEHL 35.0 / 92.3 · RAYA 40.2 / 60.6 · AMOD 68.6 / 68.9. The book's 10 worst trades at
production size sit at yz5 p11–p97 (GME 2021-01-28 p96.5, BLUW p11.5).

- **ATR5 ≈ yz5** (ρ 0.94); ATR5 ≥ 400 % is a top-0.1 % cut of the same thing. Historically it is NEUTRAL (29 trades,
  Σreturn unchanged), not net-cutting losers; its value is WFF 10-14.
- **Plain YZ cuts cheaper per trade than the range** (yz5 top 1 %: 358 trades for 1.2 % of the profit, PF 1.29 in the
  cut set) but misses RETO (p97.4) until the top 2.6 %.
- **Adding today's session makes YZ a WORSE gate:** the `_s` cut sets have PF 1.5–1.8 (the session's own flush range is
  the opportunity), so each costs about twice the profit for the same count; it does rank RETO / SXTC / AEHL higher.
- No YZ variant clears the random band except yz10 top 5 % at its edge (1.555 vs 1.553). **The 2-prior-day range ≥ 15
  remains the cheapest gate that removes all three spirals (1.5 %)**; yz5 top 2 % (σ ≥ 0.63, 2.5 %) is the volatility
  equivalent. Not built.

**Tighter YZ5 cuts (user: "What about YZ5 top 0.5% or even less?"; scratch yz5_top.py, thresholds = the harness's):**
top 0.5 % σ ≥ 0.796: 179 trades / 97 ticker-days, PF 1.43, 1 big loser (TOP 2023-05-04 −60 %), +22.4 % eq = 0.8 % ·
top 0.25 % σ ≥ 0.90: 90 / 48, PF 2.47, 0, +13.2 = 0.5 % · top 0.1 % σ ≥ 1.15: 38 / 17, PF 4.47, 0, 0.2 % · top 0.05 %:
21 / 11, PF 3.78, 0. The spirals: WFF 10-14 0.916, WFF 10-13 0.804, MEDS 09-18 0.744, RETO 0.584. So top 0.5 % catches
both WFF days (10-13 by 0.008) and nothing else in the window; top 0.25 % only WFF 10-14 (= ATR5's job); tighter catches
none. The very top of the volatility distribution is the book's best region (PF 2.5–4.5, no big losers) — every cut
above 0.5 % removes winners only.

**Tighter cuts WITH today's session (yz5_s; scratch yz5s_top.py, thresholds = the harness's):** top 1.5 % σ ≥ 0.673:
537 trades, PF 1.61, 5 big losers, 3.6 % of the profit (WFF ×2, RETO, MEDS) · top 1 % σ ≥ 0.720: 358, PF 1.54, 3,
2.4 % (WFF ×2 + RETO 0.729) · top 0.75 % σ ≥ 0.746: 269, PF 1.49, 3, 2.0 % (WFF ×2) · top 0.5 % σ ≥ 0.809: 179, PF 1.56,
2, 1.3 % (WFF 10-14 only) · top 0.25 %: 90, PF 1.89, 0.8 %, none · top 0.1 %: 36, PF 2.71, none. Including the session
LOWERS the spirals' readings (WFF 10-13 0.804 → 0.764, 10-14 0.916 → 0.892: a sixth, partial observation dilutes the five
wild days) and raises the flush days': at every count the cut set is better than plain YZ5's, so the session version
costs more for the same protection. Ranking for "remove WFF ×2 + RETO": 2-day range ≥ 15 (1.5 %) < yz5_s top 1 % (2.4 %);
for WFF ×2 only: yz5 top 0.5 % (0.8 %) < yz5_s top 0.75 % (2.0 %).

## V2-38 — THE ATR5 GATE REMOVED (2026-11-07, user) — sealed

**User:** "all of this convinced me that I am misguided in putting in the ATR 5 filter. If pretty much every volatility
estimate is telling me that more is better as far as mean reversion is concerned, then we're making a mistake having it
in. We'll accept the WFF loss and remove the ATR 5 gate."

- **What it costs (2020-01 … 2026-08-21):** the gate blocked 29 book trades (equal units: Σreturn unchanged, PF 1.542 →
  1.544, V2-34). **At production size they lost −11.1 % of equity** (+2,716.3 → +2,705.2, 0.4 %), almost all ONE trade:
  OCG 2025-12-12 −41.7 % (ATR5 7.08, −12.4 % eq); the other 28 net +1.3. Live: WFF 10-14 (−\$953 at the 8k sizing) is
  the class accepted.
- **The production reference, ATR5 off:** `flushfaderv2_reference.py --coef models/flushfaderv2/v2_coefficients.json
  --max-atr5 inf` (the V2-25 coefficients APPLIED, no refit) → book 35,796 (+29, none removed), sized PF 1.823 (was
  1.830); engine-level trips 196,474. Swapped into `data/flushfaderv2/flushfaderv2_reference_{book,trips}.parquet` /
  `.md`; the V2-25 files are in `pre_noatr5_2026-11-07/`. The script's `--max-atr5` now defaults to inf. ⚠ Its FIT-mode
  defaults therefore no longer reproduce `v2_coefficients.json` byte for byte (that fit saw the ATR5-gated set; pass
  `--max-atr5 4` to reproduce it).
- **SEALED:** the private Oms with `MaxAtr5 = +infinity` (the knob kept; ATR5 still recorded) — `parity trips -s 2020-01-02
  -e 2026-08-21 --from-bars --system flushfader --min-barnum 11` (71.6 min, peak 1.96 GB): **book 35,796 = 35,796 zero-diff**
  (every column exact but pf1_model / pf1_fitted 3.3e-15 and the multiplier 3.9e-15 relative), **trips 196,474 = 196,474
  zero-diff** (`scanner_diff.py --rtol 1e-12`).

## V2-39 — the time from the 20m high to the signal (2026-11-08, user)

**User:** "a breakdown of the time from 20m high to the signal. I wonder if faster signals have a higher EV? Given how
bounces from 20m low to 5m behaved, there might be something to it." `scripts/equity/flushfaderv2_since_high.py`: the
production book (35,796, ATR5 off) joined to base_v24's `bars_since_high` (present 1s bars since the last new 20m high —
a vwap above the prior 1,200-present-bar max; median 1,377, p10 620, p90 3,058) and `secs_since_first_low`.

| bars from the 20m high | trades | mean | median | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | bp eq / trade | 2020–22 | 2023–24 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| < 180 | 107 | −0.14 % | +0.49 % | 0.93 | | 0 | −0.6 | −0.5 | | | | |
| 180–299 | 398 | +0.18 % | +0.97 % | 1.12 | 2.02 | 2.5 | +12 | +3.0 | 1.44 | 1.16 | 0.83 | 0.85 |
| 300–449 | 1,090 | +0.40 % | +1.03 % | 1.34 | 2.44 | 1.8 | +49 | +4.5 | 1.24 | 1.26 | 2.34 | 1.05 |
| 450–599 | 1,723 | +0.70 % | +1.20 % | 1.70 | 3.24 | 1.7 | +139 | +8.0 | 1.82 | 1.70 | 1.72 | 1.33 |
| 600–899 | 4,715 | +0.63 % | +1.23 % | 1.63 | 3.34 | 2.8 | +373 | +7.9 | 1.88 | 1.65 | 1.29 | 1.31 |
| 900–1199 | 5,766 | +0.68 % | +1.19 % | 1.74 | 3.59 | 2.3 | +542 | +9.4 | 1.83 | 1.68 | 1.74 | 1.49 |
| ≥ 1,200 | 21,997 | +0.60 % | +1.18 % | 1.59 | 3.28 | 3.6 | +1,591 | +7.2 | 1.73 | 1.39 | 1.52 | 1.55 |

(< 180 = 107 trades: 30–59 9 trades PF 0.12, 60–119 20 PF 0.85, 120–179 77 PF 1.17.)

By the seconds since the leg's first low: 0–29 s PF 1.73 (2,091) · 30–59 1.56 · 1–2 min 1.67 · 2–3 min 1.94 · 3–5 min
1.67 · 5–10 min 1.63 · 10–20 min 1.95 · ≥ 20 min **1.46** (17,449, 4.1 big losers per 1,000) — flat but for the long legs.

- **No: FAST signals are the WEAKER ones.** A flush that reaches a new 20m low within 7½ minutes of a 20m high (4.5 % of
  the book) earns +0.2–0.4 % a trade at PF 1.12–1.34 against +0.6–0.7 % at 1.6–1.7 for the rest, in most periods. The
  quickest drops are knife-like: the bounce has not been prepared by a slide. Beyond 7½ minutes the time does not matter
  (450 … ∞ all PF 1.59–1.74); the longest declines (≥ 20 min, 61 % of the book) carry the highest tail rate.
- Not a candidate to build (user 11-07: no more FlushFader tinkering); the sub-450 band is 4.5 % of the trades and +2 %
  of the profit, so a gate there would be nearly inert.

## V2-40 — the 20m standard deviation against volat_20m (2026-11-08, user)

**User:** "Suppose we compared 20m standard deviation to the 20m volatility feature we're using based on 30s slot
absolute log returns. What would the correlation between them be. Is there any signal to be found when those measures
diverge significantly?" … "we should compare the stddev calculated using the same half life and EWMA then."
`scripts/equity/flushfaderv2_sd_vs_volat.py` (formulas in its header), every production book trade (35,796) recomputed
from the 1s bars FROM 09:30 (the engine's SessionStartSec — premarket bars misalign the slots: the first pass was off
3.4 % median). **Control: the recomputed volat = base_v24's volat_20m on 100 % of the trades, exactly.**

- **The same-EWMA std (hl 40 slots, centered, signed slot returns) IS volat_20m:** Spearman 0.975, log-Pearson 0.980.
  The ratio k_slot = σ / mean|r| has median 1.32 (p5 1.20, p95 1.54) — above the Gaussian √(π/2) = 1.25: slot returns
  are fat-tailed. The other readings are different things: the 1s-return std × √30 (Spearman 0.69; k_1s median 1.90 —
  within-slot noise ~3.6× the variance of a random walk) and the std of ln price over 1,200 bars (0.68; k_px median 3.11).
- **The ratios are nearly independent of the existing gates** (|ρ| ≤ 0.19 with coil, |eff10|, volat; k_px 0.40 with
  |eff10| and rng_20m).

| k_slot band | range | trades | mean | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | 2020–22 | 2023–24 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| p0–5 (smooth) | 0.85–1.20 | 1,790 | +0.42 % | 1.44 | 3.00 | 3.9 | +54 | 1.65 | 0.97 | 1.46 | 1.55 |
| p5–10 | 1.20–1.23 | 1,790 | +0.42 % | 1.47 | 2.88 | 2.2 | +69 | 1.69 | 1.06 | 2.09 | 0.67 |
| p10–25 | 1.23–1.27 | 5,369 | +0.58 % | 1.67 | 3.33 | 1.3 | +327 | 1.95 | 1.33 | 1.52 | 1.20 |
| p25–50 | 1.27–1.32 | 8,949 | +0.62 % | 1.67 | 3.38 | 2.5 | +671 | 1.85 | 1.62 | 1.49 | 1.23 |
| p50–75 | 1.32–1.39 | 8,949 | +0.64 % | 1.61 | 3.36 | 3.7 | +774 | 1.73 | 1.43 | 1.53 | 1.71 |
| p75–90 | 1.39–1.47 | 5,369 | +0.71 % | 1.67 | 3.31 | 3.5 | +497 | 1.51 | 1.84 | 1.60 | 1.97 |
| p90–95 | 1.47–1.54 | 1,790 | +0.57 % | 1.46 | 2.90 | 5.0 | +167 | 1.55 | 1.53 | 1.16 | 1.63 |
| p95–100 (jumpy) | 1.54–5.02 | 1,790 | +0.51 % | 1.36 | 2.86 | 5.6 | +146 | 1.60 | 1.17 | 1.81 | 0.98 |

k_1s: p0–5 (ratio < 1.13: the 1s path as smooth as the slots — trending inside them) PF 1.37, 4.5 big losers per 1,000;
the rest 1.53–1.70, flat. k_px: p0–10 (< 2.06: the price has hardly travelled against its step — chop) PF 1.34–1.35;
p95–100 (> 5.67: a long one-way slide) PF 1.43 with 8.4 big losers per 1,000 (the tail rate climbs with k_px from 1.1
at p25–50); the middle 1.51–1.74.

- **Little signal in the divergence.** Each ratio has weaker EXTREMES (about a tenth of the book each side, PF 1.34–1.47
  against ~1.6–1.7 in the middle) and no monotone effect; the extremes swap rank between periods (k_slot p0–5: 0.97 in
  2023–24, 1.55 in 2026; p95–100: 1.81 in 2025, 0.98 in 2026). Jumpy slot returns and long one-way slides carry more
  big losers (5–8 per 1,000), consistent with V2-30 / V2-39 (cascades), but the sizing model already gives them their
  higher mean. Not a candidate (steady path, user 11-07).

## V2-41 — the AVERAGE 1m–5m slope over the last 20m / since the first low, on top of the leg counters (2026-11-08, user)

**User:** "Instead of using leg counters it might be worth it to use the average 1m-5m slope over the last 20m or since the
first 20m low … Not the total slope, but a short-term average of it. The leg counters might be sensitive to spikes … We
should try breaking down the average slope on top of them." `scripts/equity/flushfaderv2_avg_slope.py` (formulas in its
header): every production book trade (35,796, ATR5 off) recomputed from the 1s bars from 09:30. The rolling OLS slope of
ln vwap over w = 60/120/180/300 present bars is averaged over the last 1,200 present bars (`_20`) and over the leg since its
first 20m low (`_fl`, base_v24's `bars_since_first_low`; 98.8 % of the trades). **Control: the rolling 60/300 slopes =
base_v24's ols_slope_60/300 on 100 % of the trades (|rel| < 1e-6; the engine's running sums differ at 1e-10).**

⚠ **The signed average IS the total slope, by construction.** An OLS slope's weights sum to zero, so in a window's average
of rolling slopes every interior price cancels: what remains is the window's end-to-end change, with its ends smoothed
over w bars. Measured: avg60_20 vs ols_slope_1200 Spearman 0.74, avg60_fl vs ols_slope_since_flow 0.74 (and their band
tables match the totals' tables below). Two readings that do not cancel were added: the mean |slope| (`aabs`, path
speed) and the share of bars with a falling slope (`fneg`).

| feature (Spearman) | 20m OLS slope | slope since first low | lows300 | lows600 | volat_20m |
|---|---|---|---|---|---|
| avg60_20 (mean 1m slope, 20m) | +0.74 | +0.10 | −0.38 | −0.51 | −0.39 |
| avg300_20 (mean 5m slope, 20m) | +0.69 | −0.03 | −0.41 | −0.59 | −0.24 |
| avg60_fl (mean 1m slope, leg) | +0.09 | +0.74 | +0.06 | +0.19 | −0.43 |
| aabs60_20 (mean \|1m slope\|, 20m) | −0.39 | −0.29 | −0.15 | −0.12 | **+0.92** |
| fneg60_20 (share falling, 20m) | −0.42 | +0.02 | +0.42 | +0.51 | −0.14 |

**Mean 1m slope over the last 20m** (bp/min; same pattern for the 5m slope and the 20m OLS slope itself):

| band | range | trades | mean | median | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq | bp eq / trade | 2020–22 | 2023–24 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| p0–10 (steep) | −902 … −58 | 3,580 | +1.16 % | +2.21 % | 1.74 | 3.96 | 11.5 | +665 | +18.6 | 1.89 | 1.57 | 1.80 | 1.70 |
| p10–25 | −58 … −40 | 5,369 | +0.71 % | +1.49 % | 1.61 | 3.31 | 5.4 | +494 | +9.2 | 1.72 | 1.51 | 1.66 | 1.40 |
| p25–50 | −40 … −26 | 8,949 | +0.67 % | +1.24 % | 1.74 | 3.60 | 2.0 | +700 | +7.8 | 1.93 | 1.52 | 1.62 | 1.64 |
| p50–75 | −26 … −15 | 8,949 | +0.44 % | +1.02 % | 1.48 | 2.87 | 1.5 | +425 | +4.8 | 1.72 | 1.36 | 1.20 | 1.28 |
| p75–90 | −15 … −7 | 5,369 | +0.38 % | +0.94 % | 1.44 | 2.70 | 0.7 | +241 | +4.5 | 1.47 | 1.36 | 1.37 | 1.55 |
| p90–100 (flat) | −7 … +55 | 3,580 | +0.47 % | +0.93 % | 1.50 | 2.85 | 1.7 | +181 | +5.1 | 1.57 | 1.35 | 1.81 | 1.15 |

Since the first low (avg60_fl) the same direction: p0–10 +0.98 % · PF 1.72 → p90–100 +0.35 % · 1.42, tail 4.5 → 1.4.
Mean |1m slope| (= volatility, ρ 0.92): p0–10 +0.25 % · PF 1.35 → p90–100 +1.41 % · 1.88 (tail 0.3 → 10.3) — V2-34…38's
"more volatility is better" again. Share falling: flat but for p90–100 of the 20m window (≥ 66 % of bars falling: PF 1.27,
6.3 big losers per 1,000 — a steady staircase); over the leg it ties at 1.0 on short legs, no effect.

**On top of the leg counters** (terciles of the average WITHIN each lows300 band):

| lows300 | steep tercile n · mean · PF · big/1,000 | mid | flat |
|---|---|---|---|
| 6–9 | 4,311 · +0.91 % · 1.77 · 4.2 | +0.54 % · 1.55 · 1.4 | +0.48 % · 1.49 · 1.9 |
| 10–19 | 4,251 · +0.94 % · 1.82 · 5.2 | +0.57 % · 1.65 · 2.4 | +0.41 % · 1.51 · 0.5 |
| 20–39 | 2,341 · +0.93 % · 1.71 · 9.4 | +0.54 % · 1.63 · 1.7 | **+0.19 % · 1.21 · 0.4** (2023–24 0.88, 2026 1.00) |
| ≥ 40 | 1,030 · +0.53 % · 1.32 · **12.6** | +0.47 % · 1.57 · 3.9 | +0.25 % · 1.32 · 1.0 |

(avg300_20 and avg60_fl give the same shape; the flat 20–39 cell is PF 1.27 / 1.46 there.)

⚠ **The 20m OLS slope is ALREADY a sizing term** (`s20` = ols_slope_1200 × 6e5, winsorised −107 … +10.6 bp/min,
`TradingEdge.Oms/Engine/Sizing.fs`; its weight is small, hence Spearman −0.13 with pf1_fitted). The steep tercile's higher
mean within every pf1_fitted quintile (q5: +1.50 % vs +0.77 %) comes with 3–6× the flat tercile's tail rate: more mean AND
more loss, so PF−1 — what production sizes by — is close to what the model expects. **Head to head with s20** (user: "are
you suggesting we replace it?"), terciles of one within quintiles of the other:

| | steep tercile | mid | flat |
|---|---|---|---|
| avg60_20 within s20 q1 (−719 … −41) | +1.23 % · 1.76 · 12.6 | +0.90 % · 1.77 · 6.3 | +0.88 % · **1.88** · 3.4 |
| avg60_20 within s20 q2 … q5 | PF 1.53 / 1.71 / 1.60 / 1.43 | 1.89 / 1.44 / 1.59 / 1.31 | 1.41 / 1.57 / 1.58 / 1.43 |
| s20 within avg60_20 q1 | +1.25 % · **1.81** · 12.1 | +0.85 % · 1.68 · 6.3 | +0.73 % · 1.53 · 8.4 |
| s20 within avg60_20 q2 … q5 | PF 1.89 / 1.57 / 1.61 / 1.54 | 1.67 / 1.55 / 1.56 / 1.48 | 1.64 / 1.62 / 1.30 / 1.42 |

(cells: mean · PF · ≤ −20 % per 1,000; Spearman 0.74, Pearson 0.81.) With s20 held, the averaged slope adds mean but no PF
and mostly tail; with the averaged slope held, s20 still separates PF in most quintiles. **s20 is the better form; nothing
to replace.**

- **The averaged slope is the 20m (or leg) slope**, not a new measure, and that slope carries real information the leg
  counters do not: a steep fall in the last 20m (or since the first low) means a bigger bounce in
  every lows300 band and most periods (exceptions: 2025 at lows300 10–19, flat 1.87 vs steep 1.84; 2026 at 20–39,
  steep only 1.08) — the familiar higher-mean / higher-tail shape of a deeper flush (V2-30, V2-31).
  The counters do not hide a spike problem: many lows on a SHALLOW slope (lows300 ≥ 20, the flat tercile — a slow
  staircase) is the one weak cell (PF 1.21–1.32), and many lows on a steep slope (≥ 40) is the tail-heavy one.
- The non-cancelling readings add nothing new: the mean |slope| is volatility (ρ 0.92, the same "more is better"), and
  the share of falling bars is flat but for its top decile.
- **Nothing to build:** the information is the 20m slope, already in the sizing model as s20, which beats the averaged
  form head to head. (The old slope20 gate, `MaxSlope20Bpm`, dropped in §S49, pointed the same way.)

## V2-42 — a 20m OLS slope of VOLUME as a sizing term (2026-11-08, user)

**User:** "Would a 20m OLS slope of volume rather than price make any difference in sizing on top of what we currently
have?" The production model has no volume term (`rate600` is lows per bar). `scripts/equity/flushfaderv2_vol_slope.py`
(formulas in its header): at every production book trade's signal bar (35,796, ATR5 off), from the 1s bars from 09:30:
**vlog** = the OLS slope of ln(bar volume) over the last 1,200 present bars × 1,200 (the fitted ln change across 20m;
median −0.35, p10 −1.49, p90 +0.65), **vrel** = the slope of bar volume over its mean, **vwall** = 20 wall-clock minute bins
(silence counts as zero), **v5_20** = ln(vol_300_bar / vol_1200_bar) (the cheap control). Controls: the 1,200-bar volume =
base_v24's vol_1200_bar on 100 %; production refitted on the full book reproduces the book's pf1_model to a median 0.003
(max 0.11: the production coefficients were fitted on the ATR5-on book, 35,767). Nearly independent of the model's terms
(vlog Spearman: s20 +0.12, cus −0.05, volat −0.25, dslo −0.17).

**The production model's predictions against what happened, by vlog band** (non-young):

| vlog band | range | trades | win % real / model | mean win real / model | mean loss real / model | PF−1 real | model PF−1 | sized net / trade |
|---|---|---|---|---|---|---|---|---|
| p0–10 (volume dying) | −4.50 … −1.49 | 3,521 | 72.0 / 69.9 | 2.74 / 2.89 | 3.02 / **3.72** | **1.335** | 0.855 | +1.110 |
| p10–25 | −1.49 … −0.92 | 5,280 | 70.1 / 69.0 | 2.45 / 2.56 | 3.01 / 3.28 | 0.900 | 0.749 | +0.753 |
| p25–50 | −0.92 … −0.35 | 8,804 | 68.3 / 68.0 | 2.26 / 2.34 | 2.98 / 3.11 | 0.634 | 0.633 | +0.482 |
| p50–75 | −0.35 … +0.16 | 8,820 | 66.5 / 67.6 | 2.19 / 2.20 | 3.03 / 3.01 | 0.433 | 0.567 | +0.319 |
| p75–90 | +0.16 … +0.65 | 5,282 | 66.9 / 67.5 | 2.25 / 2.18 | 3.24 / 3.06 | 0.407 | 0.536 | +0.304 |
| p90–100 (volume climbing) | +0.65 … +7.04 | 3,508 | 67.2 / 67.8 | 2.72 / 2.25 | **4.11** / 3.17 | **0.351** | 0.539 | +0.324 |

vwall and v5_20 show the same shape, weaker (p0–10 PF−1 1.19 / 1.25 against a model 0.85 / 0.89).

**Holdout** (V2-20/25's harness: fit 2020–23 → 2024–26 and the mirror; young × 0.8; production units clip(PF−1, .125, 2);
net and DD in % of one unit):

| model | sized PF−1 fwd | mirror | **mean** | net sum | unit-trades | net / unit-trade | max DD fwd / mirror | new term p / W / L × per SD (fwd; mirror) |
|---|---|---|---|---|---|---|---|---|
| A production (s20 + cus) | 0.726 | 0.896 | **0.811** | 17,400 | 21,892 | 0.795 | 103 / 106 | — |
| **+ vlog** | 0.769 | 0.942 | **0.855** | **18,517** | 22,883 | 0.809 | 79 / 121 | .941 / 1.083 / 1.120; .981 / 1.068 / 1.163 |
| + vrel | 0.747 | 0.915 | 0.831 | 18,044 | 22,620 | 0.798 | 93 / 124 | .962 / 1.113 / 1.130; 1.000 / 1.095 / 1.177 |
| + vwall | 0.741 | 0.911 | 0.826 | 17,866 | 22,504 | 0.794 | 80 / 125 | .984 / 1.104 / 1.143; .986 / 1.104 / 1.163 |
| + v5_20 | 0.738 | 0.907 | 0.823 | 17,738 | 22,257 | 0.797 | 88 / 114 | .968 / 1.102 / 1.129; 1.012 / 1.091 / 1.146 |

Paired day-bootstrap against A (2,000 resamples): vlog **Δ PF−1 +0.0445 [+0.031, +0.059], Δ net +1,118 [+915, +1,317]**,
100 % of resamples > 0; vrel +0.021 / +644; vwall +0.016 / +466; v5_20 +0.012 / +338 (all 100 %). By period (Δ net, A's
own net in brackets): 2020–21 +387 (6,996) · 2022–23 +243 (1,934) · 2024 +147 (3,102) · 2025 +157 (3,314) · **2026 +183
(2,054)** — positive in every period. For scale: the CUSUM term adopted in V2-25 gained +0.014 PF−1 / +263 units.

- **Yes — and it is the largest sizing gain the V2 model has seen.** A flush on DYING volume (the 20m ln-volume slope in
  its bottom decile) loses less than the model expects (mean loss 3.02 % vs 3.72 %) and earns PF−1 1.34 against a
  predicted 0.86; a flush on CLIMBING volume loses more (4.11 % vs 3.17 %) at PF−1 0.35 against 0.54. The fit reads it
  as mostly a LOSS-size term (L × 1.12–1.16 per SD of rising volume, W × 1.07–1.08, odds × 0.94–0.98): climbing volume
  into a new low is selling being accepted (the same thing the old price-acceptance stops looked for), dying volume is
  exhaustion.
- The log slope beats the linear (vrel) and wall-clock versions and the 5m-share shortcut: ln damps the 1s volume
  spikes. It is not volatility or the price slope in disguise (|ρ| ≤ 0.25 with every model term).
- ⚠ Before production: (1) the sized-UP trades are the dying-tape ones — where L2's TooSmall/cap refusals and unfilled
  maker entries bite (V2-26/28: 90 % of the missed model profit was unfilled maker entries); the gain is model-units, not
  verified fills. (2) Max DD improves fwd (103 → 79) but worsens mirror (106 → 121). (3) Building it = a new engine
  feature (an OLS roller on ln volume, `OlsSlopeMa` exists), a refit of the reference, a parity re-seal. User's call
  (steady path, 11-07).

## V2-43 — vlog into production: the canonical feature and the refit (2026-11-08, user) — sealed

**User:** "I like this a lot … we absolutely need to put this feature into the production system. Let's do it."
- **The canonical feature** (`scripts/equity/flushfaderv2_vlog.py`): `vlog = (S / 143999900) × 1200`, S = Σ_{j=0..1199}
  (j − 599.5) · ln(vol_j) summed sequentially oldest first, over the last 1,200 present bars ending at the signal bar
  (NaN when the window is not full). THE OPERATION ORDER IS THE SPEC: the Oms runs the same loop (`FlushFader.vlogOf`,
  computed only when a signal is born) and Python's math.log and .NET's Math.Log are both glibc's, so the two agree bit
  for bit. All 196,474 reference trips: no NaN; = V2-42's vectorised vlog to 6e-14 (summation order).
- **The reference** (`flushfaderv2_reference.py --vlog-trips`, a tenth standardised term; `--no-vlog` = the V2-25
  design). Controls: applying the production coefficients reproduces the current reference book bit for bit. Refitted on
  the ATR5-off book (35,796; the V2-25 coefficients were fit with the gate on). Without vlog the refit lands on the
  production coefficients (volat^0.637 / ^0.428 vs 0.635 / 0.429); with it:

| term (per SD) | p (odds ×) | W (×) | L (×) |
|---|---|---|---|
| ln volat (power) | 0.915 | volat^0.707 | volat^0.548 |
| s20 | 0.918 | 0.972 | 0.961 |
| CUSUM dn k0.25 | 1.001 | 1.076 | 1.169 |
| **vlog** | **0.958** | **1.076** | **1.141** |

  In-sample sized PF 1.823 → 1.864 (net 28,631 → 29,083 units·%), BasePf1 0.634 → 0.658. The volatility exponents rise
  because high-volatility flushes come on dying volume (ρ −0.25): the volatility term was absorbing the smaller losses
  that vlog now carries; part of the CUSUM's loss effect moves to vlog too (L 1.206 → 1.169). The holdout (V2-42)
  validated the combined model. Promoted to `models/flushfaderv2/v2_coefficients.json` with the Oms port.
- **SEALED (vlog alone, before the V2-44 gate):** the private Oms (`FlushFader.vlogOf` on a 1,200-bar volume ring;
  `Sizing.Dim = 10`) — `parity trips -s 2020-01-02 -e 2026-08-21 --from-bars --system flushfader --min-barnum 11`: **book
  35,796 = 35,796 zero-diff** (25 columns; vlog and vlog_z exact, pf1 to 4e-15 relative), **trips 196,474 = 196,474
  zero-diff** (vlog exact on every trip) against `vlog_ref/fit_vlog/` (`scanner_diff.py --rtol 1e-12`).

## V2-44 — a GATE on the time since the last 20m high, re-run as a gate (2026-11-08, user) — built, sealed

**User:** "consider adding a gate for the time since last 20m high. Maybe adding a gate that the decline should last 3.5m
would push some of those earlier marginal trades into the stronger buckets and not even lower our net profit. We need to
test out how the actual gates would affect the results rather than just using the feature as a filter."
`scripts/equity/flushfaderv2_since_high_gate.py`: the gate refuses SIGNALS (all 196,474 V2 engine-level trips), then the
book is re-run as the reference builds it ($1 floor, mc=1 greedy per ticker-day — a refused early signal frees the
ticker-day for a later one), sized with the V2-43 model, scored at production size (V2-26's % eq). `bars` = present bars
since the last new 20m high (V2-39's bars_since_high); `secs` = wall-clock seconds from that high's bar. Controls: the
ungated re-run = the V2-43 reference book (keys identical, pf1 to 7e-16); a same-count RANDOM refusal of signals (40
draws), re-run the same way.

| gate | signals refused | book | mean | PF | ≤ −20 %/1,000 | % eq | Δ % eq | random drop: PF · Δ % eq (p5–p95) | 2020–22 · 2023–24 · 2025 · 2026 % eq |
|---|---|---|---|---|---|---|---|---|---|
| none (production) | 0 | 35,796 | +0.605 % | 1.600 | 3.1 | +2,851 | — | — | 1,451 · 621 · 474 · 305 |
| bars ≥ 180 | 370 | 35,744 | +0.606 % | 1.602 | 3.1 | +2,852 | +1 | 1.599–1.600 · −5 … +2 | 1,452 · 623 · 473 · 303 |
| bars ≥ 210 (3.5 m) | 581 | 35,713 | +0.608 % | 1.604 | 3.1 | +2,854 | +4 | 1.598–1.600 · −6 … +1 | 1,455 · 623 · 473 · 304 |
| bars ≥ 240 | 887 | 35,666 | +0.609 % | 1.607 | 3.1 | +2,855 | +4 | 1.599–1.601 · −6 … +3 | 1,455 · 623 · 474 · 303 |
| **bars ≥ 300** | 1,876 | 35,519 | +0.613 % | **1.612** | 3.1 | **+2,861** | **+11** | 1.598–1.602 · −12 … +2 | 1,452 · 625 · 480 · 305 |
| bars ≥ 450 | 6,035 | 34,759 | +0.620 % | 1.622 | 3.1 | +2,851 | +1 | 1.596–1.605 · −28 … −3 | 1,446 · 624 · 473 · 308 |
| secs ≥ 210 | 363 | 35,749 | +0.608 % | 1.604 | 3.1 | +2,858 | +7 | 1.598–1.601 · −4 … +2 | 1,455 · 624 · 474 · 304 |
| secs ≥ 300 | 1,143 | 35,640 | +0.610 % | 1.607 | 3.1 | +2,860 | +9 | 1.598–1.601 · −8 … +1 | 1,455 · 623 · 478 · 304 |
| secs ≥ 450 | 3,397 | 35,268 | +0.611 % | 1.610 | 3.1 | +2,847 | −3 | 1.598–1.603 · −16 … +1 | 1,448 · 621 · 472 · 307 |

(bars ≥ 120 / secs ≥ 120: +1, inert.)

- **The user's hypothesis holds — but the effect is small.** A 3.5–5 minute minimum decline lifts PF above the random
  band (1.604–1.612 vs ≤ 1.602) and does NOT lower the production-size profit: the re-run replaces most refused
  signals with later ones (bars ≥ 300 refuses 1,876 signals and loses only 277 book trades), and the profit rises by
  +4 … +11 % eq — against the random drop's −12 … +2. But +11 is 0.4 % of the book's +2,851 over 6.6 years (≈ +1.7 % eq
  a year), every period moves by 0–6, and 2026 by 0. Fast signals are rare at the SIGNAL level (bars < 210: 0.3 % of
  signals; V2-39's 4.5 % of the book was < 450).
- The best cell is bars ≥ 300 (5 present-bar minutes): PF +0.012, +11 % eq, no period negative; beyond it (≥ 450) the
  refusals start costing profit. Wall-clock seconds are no better than present bars.
- **By year** (bars ≥ 300 vs ungated, both with the V2-43 coefficients): PF better in ALL 7 years (+0.005 … +0.027),
  production-size % eq better in 6 of 7:

| year | PF ungated → gated | Δ % eq |
|---|---|---|
| 2020 | 1.864 → 1.879 | +0.4 |
| 2021 | 1.804 → 1.811 | −0.3 |
| 2022 | 1.414 → 1.419 | +0.7 |
| 2023 | 1.340 → 1.367 | +3.0 |
| 2024 | 1.543 → 1.551 | +1.0 |
| 2025 | 1.532 → 1.545 | +5.5 |
| 2026 (to 08-21) | 1.459 → 1.468 | +0.7 |

  By month a coin flip: 40 of 80 better, 40 worse (worst −1.5, 2024-06; best +2.1, 2023-04). A cleaner book, not a
  profit lever.
- **BUILT (user 2026-11-08: "Let's do it."), bars ≥ 300.** The reference: `flushfaderv2_reference.py
  --min-bars-since-high 300` (now the default; 0 = off) with the V2-43 coefficients APPLIED (not refitted: the gate
  changes 0.8 % of the book) → engine-level trips 194,598, book 35,519, PF 1.612, sized 1.875. Swapped into
  `data/flushfaderv2/flushfaderv2_reference_{book,trips}.parquet` / `.md`; the ATR5-off files (V2-38) are in
  `pre_vlog_2026-11-08/`. The Oms: `IntradayConfig.MinBarsSinceHigh = 300` (Parity's engine-level `baseConfig` = 0);
  the counter = research's `olsSinceHigh.Count` (+1 on every bar, reset at the end of a bar whose vwap tops the
  strictly-prior 1,200-bar entry-channel max); recorded on every position (`BarsSinceHigh`, the trips' `bars_since_high`).
- **SEALED:** `parity trips` as above (71.3 min, peak 2.0 GB): **book 35,519 = 35,519 zero-diff** (25 columns, pf1 to
  4e-15 relative, the multiplier 5e-15), **trips 194,598 = 194,598 zero-diff** (14 columns, bars_since_high exact; its
  minimum 300) against `vlog_ref/gate300/`.
