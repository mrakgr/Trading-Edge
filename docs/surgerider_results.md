# SurgeRider / PlungeRider — results

`TradingEdge.SurgeRider` (long) / `TradingEdge.PlungeRider` (short) — **intraday MOMENTUM on 1-second
bars** (branch `momentum-1s-bars`). Volume/trade-count-acceleration breakout in the trend direction,
sampled mc=0 first. Data: `data/intraday_1s_slim/` (1,642 days, 2020-01-02 → 2026-07-17). Design
decisions D1-D8 in the plan; per-bar semantics: engine steps on PRESENT 1s bars only, windows are
present-bar-count.

## ⚠ Terminology — "vol" means VOLATILITY here; much of the older codebase uses "vol" for VOLUME

This confusion has already caused misreadings (2026-07-22: an assistant summary cited `brv20d` and
MaxRiderV1's "quiet-vol" as volatility precedents — both are **volume** features). The legacy glossary:

- **`rvol*`** (rvol_0945, rvol20d, …) = **relative VOLUME** — today's volume vs a trailing average.
- **`brv20d`** (MaxFlyer family) = **breakout-bar relative VOLUME** vs the 20d average. Volume. (Also
  fails the lookahead audit — dead regardless.)
- **"quiet-vol"** (MaxRiderV1 F18, the stop-substitute) = quiet **VOLUME**, not low volatility.
- **ATR%, `vol20m`, `ew20`/`ew10`, and everything in this document's F1-F8** = **VOLATILITY**.

**Rule going forward:** volume-derived features must spell it out (`volume`, `rvolume`, `tc`); `vol` bare
or in `vol20m` is reserved for volatility. When citing an MR-book finding, check which family the feature
belongs to before using it as precedent.

---

## Finding 1 — ⭐ the volatility bake-off: the VWAP-path realized vol wins, all three are strong

**Question (user):** the vol feature exists to predict near-term return volatility — so before building the
engine, test the three candidates on exactly that job.

**Setup:** per (ticker, day, spot) — spots every minute 10:00→15:30 ET; trailing 20m in one-minute slots;
target = std of the next ~10 one-minute-vwap log-returns. Guards: ≥16/20 trailing and ≥8/10 forward slots
non-empty. Universe: same-day RTH dollar vol ≥ $50M (⚠ NON-TRADABLE same-day selection — fine for a
measurement study, never for a backtest). 15 days, one per quarter 2023→2026.
**n = 6,328,182 obs / 2,636 tickers / 4,965 cross-sectional spots.**

The measures (all scale-invariant):
- **(a) path RV** — std of the 20 trailing 1m-vwap log-returns ("how far price traveled").
- **(b) decomposed vwstd** — per-minute Var via law of total variance (within-second `vwstd` + between-
  second vwap dispersion), √/V̄, averaged over 20m.
- **(c) log-range** — per-minute `ln(high/low)`, averaged over 20m (the ATR% analog, user's spec; the
  Parkinson-estimator kernel). An initial run used the variant `(high−low)/vwap`; rerunning with
  `ln(high/low)` left **every statistic identical to 4 decimals** (pooled 0.8304, xsec 0.8159, lnP 0.8662,
  all pairwise, all 15 per-day winners) — the first-order equivalence of the two forms, confirmed
  empirically at n=6.33M. The tables below therefore hold for BOTH forms; the engine records the log form.

### How to read the metrics (used throughout this document)

Every study row is one (ticker, date, spot-minute) observation: a trailing-window measure vs the forward
target (std of the next ~10 one-minute-vwap log-returns). The metrics differ in *what gets ranked against
what*:

- **Pooled Spearman** — throw ALL observations from all 15 days into one pile, rank the measure, rank the
  target, correlate the ranks. Answers "over everything, does a higher reading mean higher forward vol?"
  Note it mixes two sources of agreement: *level* differences between names (GME is always more volatile
  than KO — easy to get right, and vol persistence hands it to every measure) and *variation* within a
  name/day. It is the headline number but the easiest one to score well on.
- **Cross-sectional Spearman** — at each single (date, minute) spot, rank only the names trading at that
  instant against each other, compute Spearman inside that one spot, then average over all ~4,965 spots.
  This is the metric a universe-ranking feature actually needs: "at 10:37 today, does the feature order
  the names by who is about to be volatile?" Day-level regime and the intraday vol smile are constant
  within a spot, so they cannot inflate this score — which is why it always reads lower than pooled.
- **ln-Pearson** — plain Pearson on ln(measure) vs ln(target). Magnitude-sensitive: it asks not just for
  the right ordering but for a straight-line relation between the log levels, and it punishes being wrong
  in the tails in a way ranks do not. Secondary, because vol is heavy-tailed.
- **Per-day pooled Spearman / winner counts** — the pooled Spearman recomputed inside each day separately;
  counting which measure wins each day is a sign test for regime stability. A measure that wins 15/15 is
  genuinely better; one that wins on average but loses days is riding a few favorable days.
- **Pairwise redundancy** — pooled Spearman between the measures themselves. Above ~0.95 they carry the
  same information and the choice should be made on compute cost / engine simplicity, not on score.

**Correlation with forward 1m return vol:**

| metric | (a) path RV | (b) vwstd | (c) range |
|---|---|---|---|
| pooled Spearman | **0.857** | 0.850 | 0.830 |
| cross-sectional Spearman (avg of 4,965 spots) | **0.833** | 0.828 | 0.816 |
| ln-Pearson | **0.894** | 0.859 | 0.866 |

**Pairwise redundancy (pooled Spearman):** a↔b **0.935**, a↔c **0.914**, b↔c **0.971**.

**Per-day pooled Spearman (regime stability):**

| date | (a) | (b) | (c) | winner |
|---|---|---|---|---|
| 2023-02-15 | 0.846 | 0.850 | 0.851 | c |
| 2023-05-15 | 0.829 | 0.827 | 0.831 | c |
| 2023-08-15 | 0.826 | 0.822 | 0.822 | a |
| 2023-11-15 | 0.840 | 0.838 | 0.835 | a |
| 2024-02-15 | 0.863 | 0.860 | 0.858 | a |
| 2024-05-15 | 0.834 | 0.830 | 0.831 | a |
| 2024-08-15 | 0.838 | 0.831 | 0.819 | a |
| 2024-11-15 | 0.844 | 0.843 | 0.823 | a |
| 2025-02-18 | 0.848 | 0.840 | 0.828 | a |
| 2025-05-15 | 0.845 | 0.836 | 0.814 | a |
| 2025-08-15 | 0.856 | 0.849 | 0.830 | a |
| 2025-11-17 | 0.873 | 0.866 | 0.838 | a |
| 2026-02-17 | 0.850 | 0.850 | 0.823 | b (tie) |
| 2026-05-15 | 0.854 | 0.854 | 0.834 | a |
| 2026-07-10 | 0.854 | 0.843 | 0.833 | a |

**Readings:**
1. **Near-term vol is highly persistent** — every measure lands ρ ≈ 0.82-0.86 against the next 10 minutes.
   The vol feature will be a strong conditioning lever whatever we pick.
2. **(a) the path RV wins on all three metrics and on 12/15 days** (the 3 exceptions are ≤0.005
   photo-finishes, all in 2023; the win is clean and *widening* in 2024-2026).
3. **(b) and (c) are near-duplicates of each other (ρ 0.971)** — both are "intra-minute dispersion"
   family. (a) is the most distinct member (0.91-0.93 vs the others): *price travel* is different
   information from *intra-minute spread*, and it's the travel that better predicts future travel.
4. Target-construction caveat, acknowledged: the target is (a)-shaped on the forward window (std of
   forward 1m vwap returns) — that is the *definition* of forward return vol, not a bias; and (b)/(c)
   were only ~0.01-0.03 behind, so nothing hinges on it.

**⭐ DECISION (settles D5):** the engine records **(a) as THE volatility feature** (`vol20m` = std of the
last 20 one-minute-vwap log-returns, present-bar construction) **plus (c) as a cheap secondary**
(`logrange20m` = 20m average of per-minute `ln(high/low)`; ATR%-continuity with the MR systems; a↔c =
0.914 leaves some independent information). **(b) is dropped** — dominated by (a) on prediction and
0.971-redundant with (c), and it's the most expensive to compute.

**Measure construction detail (a):** 1s bars aggregate to 1-minute slots; minute vwap
`V_m = Σ(vol·vwap)/Σvol` is EXACT (each 1s `vol·vwap` = that second's dollar volume, so the ratio is the
true trade-level minute VWAP); returns `r_m = ln(V_m/V_{m−1})` only where the previous minute is exactly
`m−1` (no gap-spanning returns); measure = sample std of the trailing-20m `r_m` (~19-20 obs).

Study artifacts: `/tmp/volbake/*.csv` (per-spot rows), queries in `/tmp/volbake_day.sql.tmpl` +
`/tmp/volbake_corr.sql`.

---

## Finding 2 — ⭐ path-RV grid: 30s sampling × 20m window wins; 1s sampling is POISON; longer windows always beat shorter

**Question (user):** would computing the path RV from 1s bars directly work better? What about a 10m
window instead of 20m? → swept the full grid **{1,2,5,10,15,20,30,60}s sampling × {2,2.5,4,5,7.5,10,15,20}m
window** (64 cells). Same 15 days/target/universe as F1; returns from k-second vwap slots
(consecutive-slot guard), exact wall-clock windows via 30s-block partial sums keyed on the return's
*known-at* second. Common sample (all 64 cells defined): **n = 5,528,968 / 2,619 tickers.**

**Pooled Spearman vs forward 1m-return vol (rows = return sampling, cols = trailing window):**

| k_sec | 2m | 2.5m | 4m | 5m | 7.5m | 10m | 15m | 20m |
|---|---|---|---|---|---|---|---|---|
| 1s | 0.634 | 0.646 | 0.667 | 0.673 | 0.680 | 0.685 | 0.689 | 0.691 |
| 2s | 0.666 | 0.675 | 0.691 | 0.695 | 0.701 | 0.704 | 0.708 | 0.710 |
| 5s | 0.713 | 0.722 | 0.737 | 0.742 | 0.747 | 0.751 | 0.754 | 0.756 |
| 10s | 0.746 | 0.758 | 0.779 | 0.785 | 0.794 | 0.799 | 0.804 | 0.806 |
| 15s | 0.751 | 0.768 | 0.796 | 0.805 | 0.817 | 0.824 | 0.830 | 0.833 |
| 20s | 0.742 | 0.771 | 0.799 | 0.811 | 0.827 | 0.835 | 0.843 | 0.847 |
| **30s** | 0.705 | 0.740 | 0.789 | 0.806 | 0.828 | 0.839 | 0.851 | **⭐ 0.857** |
| 60s | 0.524 | 0.659 | 0.719 | 0.753 | 0.801 | 0.817 | 0.838 | 0.849 |

**Readings:**
1. **1s sampling is the WORST row** (0.69 even at 20m) — microstructure noise (bid-ask bounce, tick
   discreteness) swamps the signal; the 1s→1m vol ratio (~6.3 vs the √60≈7.7 diffusion scaling) showed the
   noise signature directly. **Answer: no — 1s bars directly are much worse.**
2. **Longer windows dominate at every sampling rate** — 20m > 15m > … > 2m, monotone, no interior optimum
   in the tested range. **Answer: 10m is worse than 20m everywhere** (e.g. at 30s: 0.839 vs 0.857).
   Freshness never beats estimation noise at these lengths.
3. **Sampling has an interior optimum ≈ 20-30s** (noise ↓ vs return-count ↑ tradeoff); it shifts coarser
   as the window grows (short windows favor 15-20s, long windows 30s). k=60 suffers at short windows (2m:
   0.524 — only ~2 returns) and nearly recovers by 20m.
4. **Winner: 30s returns × 20m window** (0.8568) — beats the F1 baseline (60s × 20m, 0.8485 on this
   sample) on **15/15 days**, and cross-sectionally 0.8318 vs 0.8205. Redundancy with the baseline 0.976 —
   same family, consistently sharper.

**⭐ DECISION (refines F1's):** `vol20m` = **std of 30s-vwap log-returns over the last 20m** (~40 returns;
consecutive-slot guard). Engine-wise: a 30s-slot vwap accumulator + `WinStdMa(40)` on its log-returns.
`logrange20m` (F1's secondary) unchanged.

Artifacts: `/tmp/volbake_grid/*.parquet`, generator `/tmp/volbake_grid_day.sql.tmpl` (python3-generated),
corr `/tmp/volbake_grid_corr.sql`.

---

## Finding 3 — overlapping (subsampled) 30s returns do NOT beat non-overlapping slots

**Question (user):** instead of 40 non-overlapping 30s returns in the 20m window, use ~1170 overlapping
30s returns (engine-trivial via `LagMa(30)`) and take the std of those — the subsampled-RV idea
(Zhang-Mykland-Aït-Sahalia phase-averaging). Tested three constructions vs the same target/days
(n = 2.84M common sample; note this sample is more liquid than F2's, so baselines differ across tables —
within-table comparisons only):

| construction | endpoints | pooled Spearman | xsec | per-day wins |
|---|---|---|---|---|
| **non-overlapping 30s slots (F2 winner)** | 30s-aggregated vwaps | **0.8774** | **0.8579** | **15/15** |
| overlapping, wall-clock (`ln(V_t/V_{t−30s})`) | single-1s-bar vwaps | 0.8652 | 0.8473 | 0 |
| overlapping, `LagMa(30)` present-bar lag | single-1s-bar vwaps | 0.8407 | 0.8142 | 0 |
| overlapping, ROLLING-30s-vwap endpoints (fair subsample) | rolling 30s vwaps | 0.8724 | 0.8528 | 0 |

**Readings:**
1. The first overlap attempts lost big because of a construction confound: their returns connect
   **single-second point vwaps** (full microstructure noise at both endpoints — the F2 k=1 poison),
   while the non-overlap slots connect noise-averaged 30s vwaps. The `LagMa(30)` present-bar version is
   worst of all — bar-lag stretch on quieter names compounds the endpoint noise.
2. The **fair** subsampled version (rolling 30s vwap `W_t`, `r_t = ln(W_t/W_{t−30})` — clean endpoints AND
   phase-averaging) closes most of the gap but **still loses on all 15 days** (−0.005 pooled, −0.005
   xsec, redundancy 0.9931). The theoretical phase-averaging gain is real but tiny, and the moving-average
   smoothing of the endpoints slightly smears the measure against a 1m-return-vol target; net negative,
   consistently.
3. **Decision: F2's construction stands** — non-overlapping 30s slots, 20m window (~40 returns). In the
   engine this is a small slot-builder (accumulate Σwv/Σw per `FLOOR(bucket/30)`; on slot change, finalize
   vwap, take the return vs the previous slot if consecutive, push into `WinStdMa(40)`) rather than
   `LagMa(30)` — slightly more machinery than the LagMa route, justified by 15/15.

Artifacts: `/tmp/volbake_ovl/*.parquet`, `/tmp/volbake_roll/*.parquet` + their `.sql.tmpl` templates.
**Exact per-variant formulas: Appendix A below.**

---

## Finding 4 — ⭐ the overlap crossover: subsampling wins at coarse k, loses at fine k; the champion stays k=30s

**Question (user):** F3 showed overlap losing at k=30s — but would it hold at k=4m/5m, where the
non-overlapping 20m window holds only 3-4 returns and phase-averaging has the most to add? Grid:
k ∈ {30s, 60s, 2m, 4m, 5m} × {non-overlap slots, rolling-vwap overlap (App. A construction 3)}, w=20m
fixed, same 15 days/target. Common sample **n = 4,076,820 / 2,428 tickers.**

| k | non-overlap | overlap | Δ (ovl−novl) | per-day winner |
|---|---|---|---|---|
| 30s | **0.8609** | 0.8547 | −0.0062 | novl 15/15 |
| 60s | 0.8506 | 0.8462 | −0.0044 | novl 15/15 |
| 2m | 0.8204 | **0.8233** | +0.0030 | ovl 13/15 |
| 4m | 0.7558 | **0.7801** | +0.0243 | ovl 15/15 |
| 5m | 0.7225 | **0.7616** | +0.0391 | ovl 15/15 |

**Readings:**
1. **The crossover is at k ≈ 2m** — overlap wins exactly where the non-overlap partition is
   return-starved (4m → 5 returns, 5m → 4). Subsampling theory vindicated *where estimator variance
   dominates*; at fine k the 40 slot-returns already suffice and the overlap's endpoint-smoothing costs a
   consistent hair. (User's hypothesis confirmed.)
2. **No coarse-k overlap cell approaches the fine-k cells** (best coarse: 2m-ovl 0.8233 ≪ 30s cells).
   The champion remains **k=30s non-overlap (0.8609)**, with **k=30s overlap at 0.8547 (−0.006)**.
3. **Engine trade (user's call):** the overlap form is `SumMa(30)`-rolling vwap → `LagMa(30)` →
   `WinStdMa` — simpler than a slot-builder. Measured price of that simplicity at k=30s: **−0.006 pooled,
   15/15 days.** ⚠ Caveat: the study's overlap is wall-clock; the engine's SumMa/LagMa are present-bar —
   identical on every-second names, mildly stretched on quieter ones (untested for the rolling form; the
   F3 point-endpoint present-bar variant suffered, but that conflated endpoint noise).

Artifacts: `/tmp/volbake_f4/*.parquet`, `/tmp/volbake_f4_day.sql.tmpl` (python3-generated),
`/tmp/volbake_f4_corr.sql`.

---

## Finding 5 — ⭐⭐ the estimator survey: EWMA of squared 30s returns BEATS the equal-weight champion; range estimators lose badly

**Question (user):** before locking the vol feature — are there other estimation methods we should
consider? Survey: **Parkinson², Garman-Klass, Rogers-Satchell** (all from 1m trade-price OHLC),
**bipower variation** (jump-robust) and **MAD** (robust) on the 30s slot returns, **EWMA of squared 30s
returns** at half-lives {5m, 10m, 20m}, and a multi-scale rank-combo — against the two k=30s champions
from F4 as in-run baselines. Same 15 days, same target, common row set
**n = 4,331,984 / 2,473 tickers** (all-measures-defined guard).

| estimator | pooled Spearman | xsec Spearman | per-day wins vs baseline |
|---|---|---|---|
| **EWMA hl=10m** | **0.8698** | 0.8488 | **15/15** |
| combo rv+ew5+ew20 (rank-avg) | 0.8693 | 0.8478 | 15/15 |
| **EWMA hl=20m** | 0.8676 | **0.8526** | 15/15 |
| EWMA hl=5m | 0.8616 | 0.8368 | 14/15 |
| path RV 30s×20m (baseline, F4 champion) | 0.8594 | 0.8348 | — |
| bipower | 0.8562 | 0.8308 | 0/15 |
| combo rv+parkinson | 0.8538 | 0.8337 | 7/15 |
| rolling-overlap 30s (baseline 2) | 0.8527 | 0.8267 | 0/15 |
| MAD (×1.4826) | 0.8396 | 0.8074 | 0/15 |
| Parkinson | 0.8245 | 0.8099 | 0/15 |
| Garman-Klass | 0.8073 | 0.7956 | 0/15 |
| Rogers-Satchell | 0.7937 | 0.7842 | 0/15 |

ln-Pearson agrees: ew10 **0.9010**, ew20 0.8984, rv 0.8929, ovl 0.8867.

**Per-day pooled Spearman (top contenders):** ew10 beats rv on ALL 15 days by +0.007 to +0.015; ew20
beats rv 15/15 as well; ew10 vs ew20 is 11/15 to ew10. (Full per-day table in the corr run;
2026-07-10 is the widest gap: rv 0.8353 → ew10 0.8505.)

**Readings:**
1. **⭐ Recency-weighting is worth more than any kernel change.** Every challenger that reweights *time*
   (EWMA at any half-life) beats the equal-weight champion 15/15; every challenger that changes the
   *estimation kernel* (range-based, jump-robust, robust-scale) loses. Vol prediction at this horizon
   wants "what happened in the last few minutes, smoothly discounted" — not a better estimator of the
   same 20m average. The redundancy confirms it: rv↔ew10 = 0.9901, yet the 1% of disagreement is
   systematically in ew10's favor.
2. **The 1m-OHLC range estimators lose badly** (Parkinson 0.8245 → RS 0.7937, 0/15 days). Trade-price
   highs/lows carry bid-ask bounce and stray prints that the vwap path averages away — the same
   microstructure poison as F2's 1s row, entering through the range instead of the sampling. More
   sophistication in the OHLC family = worse (RS < GK < Parkinson), because each refinement leans harder
   on exact O/H/L/C geometry that noise corrupts. (F1's (c) log-range survived only because it was the
   *average* of ranges — a level proxy — not a per-minute variance estimator.)
3. **Jump- and outlier-robustness are the wrong direction** (bipower ≈ baseline but 0/15; MAD −0.02).
   The tails robust estimators discard are *signal* here — a name that just printed an outlier 30s return
   is exactly the name about to be volatile.
4. **Half-life sweet spot ≈ 10m** for the pooled/per-day view; 20m edges ahead cross-sectionally
   (0.8526). The multi-scale combo buys nothing over ew10 alone (0.8693 vs 0.8698) — the half-lives are
   too redundant (ew5↔ew20 0.9701) for rank-averaging to add information.
5. **Engine bonus:** EWMA is the *simplest* construction in the whole bake-off — `EmaMa` fed squared 30s
   returns. The best measure is also the cheapest to run. The remaining question is only what return
   series feeds it (slot vs overlap — F5b below).

Artifacts: `/tmp/volbake_f5/*.parquet`, `/tmp/volbake_f5_day.sql.tmpl`, `/tmp/volbake_f5_corr2.sql`.

### F5b — EWMA over the overlap returns (the engine-simplest form): the −0.006 toll is invariant

F5's winner was fed non-overlapping slot returns; the engine-simplest chain is the overlap form
(`SumMa(30)`-rolling vwap → `LagMa(30)` → now `EmaMa` of r² instead of `WinStdMa`). Does the F3/F4
overlap penalty survive the switch to EWMA weighting? Same 15 days, same rows (n = 4,331,984):

| construction | pooled Spearman | xsec | vs its slot twin |
|---|---|---|---|
| slot EWMA hl=10m | **0.8698** | 0.8488 | — |
| slot EWMA hl=20m | 0.8676 | **0.8526** | — |
| overlap EWMA hl=10m | 0.8634 | 0.8416 | −0.0065, slot 15/15 |
| overlap EWMA hl=20m | 0.8626 | 0.8475 | −0.0050 |
| path RV 30s×20m (old champion) | 0.8594 | 0.8348 | — |
| overlap EWMA hl=5m | 0.8539 | — | — |

**Readings:**
1. **The two effects are independent and additive:** EWMA weighting is worth ≈ +0.010, the overlap
   endpoint-smoothing costs ≈ −0.006, at every half-life, 15/15 days each way. Redundancy between the
   twins: 0.991.
2. **The engine-simplest form still beats the old champion** — overlap EWMA hl=10m (0.8634) > equal-weight
   slot RV (0.8594). Choosing pure simplicity no longer means accepting the worst of the fine-k cells.
3. **The slot builder is no longer complex under EWMA:** it needs no 40-slot buffer, only (ΣpV, Σv, count)
   accumulated over 30 present bars → emit r = ln(V_slot/V_prev) → `EmaMa(r²)` → reset. That is *less*
   state than the overlap chain's `SumMa(30)` + `LagMa(30)` ring buffers. The original simplicity argument
   for overlap (F4 reading 3) was about `WinStdMa` vs buckets; with `EmaMa` it mostly evaporates.

**Decision state (vol20m lock):** the sampler records BOTH cheaply — **primary = slot EWMA of squared
30-bar-vwap returns, hl=10m (20 slots)**, best on every metric; **secondary = hl=20m twin** (one extra
`EmaMa` off the same r² stream; wins cross-sectionally); the overlap-EWMA hl=10m as a control column if
wanted. Half-life in engine terms: α = 1 − 0.5^(1/20) per slot (hl=10m), 1 − 0.5^(1/40) (hl=20m).
⚠ Same present-bar caveat as F4: study decay is wall-clock, engine decay is per-present-bar-slot.

### How slot-EWMA works (the locked construction, spelled out)

**The slot builder.** The engine accumulates three numbers over 30 consecutive present bars:
Σ(vol·vwap), Σvol, and the bar count. When the count hits 30 it emits the slot vwap
`V = Σ(vol·vwap) / Σvol` — the *exact* vwap of every trade in those 30 bars, since each 1s bar's
vol·vwap is that second's dollar volume — and resets. One vwap out every 30 bars.

**The return.** `r = ln(V / V_prev)`, current slot vwap over the previous slot's. One return per slot
(~every 30 wall-clock seconds on an active name).

**The EWMA.** It averages the **squared** returns, not the returns (an average of r would estimate
drift, ≈ 0; an average of r² estimates variance — the RiskMetrics construction, on 30-bar slots instead
of daily bars):

```
ewma ← (1 − α)·ewma + α·r²      once per slot
vol  = sqrt(ewma)
```

**What half-life 10m means.** Unrolled, the EWMA is a weighted average of all past r² where each
slot's weight is (1−α)× the weight of the slot after it — geometric decay. The half-life is how far
back the weight halves. hl = 10m = 20 slots: the slot from 10 minutes ago carries 1/2 the weight of the
newest, 20m ago 1/4, 30m ago 1/8. That pins α: (1−α)²⁰ = 0.5 → **α = 1 − 0.5^(1/20) ≈ 0.0341**
(hl=20m = 40 slots → α ≈ 0.0172). Nothing ever drops out of the window — old returns fade smoothly
instead of falling off a 20-minute cliff, which is exactly the recency-weighting that beat the
equal-weight window 15/15 in F5. The support is infinite but ~75% of the weight sits in the last 20
minutes (hl=10m).

Caveats: (1) "10 minutes" is really "20 present-bar slots" — on gappy names the slots stretch, same as
every present-bar window in the design; (2) the feature updates only at slot boundaries — between
emissions the engine carries the last value, so intra-slot it is up to 29 bars stale (the overlap
variant updates every bar and pays the measured −0.006 for it).

**How hl=10m and hl=20m are used together:** as two *separate recorded columns*, not an average.
Averaging was tested in F5 (the rank-combo row): it adds nothing over ew10 alone (0.8693 vs 0.8698) —
at 0.97 mutual redundancy there is no diversification to harvest. The value of carrying both is
post-hoc: ew10 is the better pooled/per-day predictor, ew20 the better cross-sectional ranker, and the
sampler's breakdowns can use whichever fits the question (or reveal one to be strictly dominated in the
live gates).

Artifacts: `/tmp/volbake_f5b/*.parquet`, `/tmp/volbake_f5b_day.sql.tmpl`.

### F5c — slot-size sweep under EWMA (user request): 30–40s is a dead-flat plateau; 30s stays

Slot length k ∈ {30, 35, 40, 45}s, all EWMA hl = 10 *wall-clock* minutes (the decay exponent uses
seconds, so non-integer slot counts per half-life are fine). Same 15 days, same rows (n = 4,331,984):

| k | pooled Spearman | xsec | per-day wins vs 30s | mean Δ |
|---|---|---|---|---|
| **30s** | **0.8698** | **0.8488** | — | — |
| 35s | 0.8697 | 0.8484 | 5/15 | −0.0006 |
| 40s | 0.8698 | 0.8483 | 4/15 | −0.0008 |
| 45s | 0.8689 | 0.8471 | 1/15 | −0.0018 |

Redundancy with 30s: 0.992–0.996. **Reading:** EWMA weighting has flattened the slot-size sensitivity
that the equal-weight F2 grid showed — 30–40s is a plateau to the 4th decimal, with 45s starting a
just-visible decline. Nothing above 30s buys anything, so **k = 30s stands** (marginal best on every
metric, and the clean divisor of the minute). The vol20m lock from F5b is unchanged.

Artifacts: `/tmp/volbake_f5c/*.parquet`, `/tmp/volbake_f5c_day.sql.tmpl`, `/tmp/volbake_f5c_corr.sql`.

---

## Finding 6 — ⭐ the slot-vs-overlap verdict at 43 days + staleness-fair spots: slot wins 43/43, staleness recovers only ~1/5 of the gap

**Question (user):** "It's too bad the overlapping variant isn't the winner — we might want to try it on
more days just to be completely sure." Plus the user's live-trading point: the overlap estimate is always
current, while the slot estimate is up to 29s stale at an arbitrary entry second — and every spot in
F1–F5 was minute-aligned, i.e. **exactly where 30s slots complete**, so all prior measurements evaluated
the slot version at its freshest. The −0.006 could have been flattering it.

**Setup:** 43 monthly days (nearest trading day to the 15th, 2023-01 → 2026-07, ~3× the prior sample),
rerun homogeneously; slot-EWMA vs overlap-EWMA at hl ∈ {10m, 20m}; TWO spot grids per day — minute-aligned
(off=0, slot fresh) and **+15s offset (off=15, slot ~15s stale = its average live condition, overlap
fresh)**. The offset grid shifts the forward target grid with it (60s cells starting at :15).
**n = 17.9M obs per grid / 43 days / 3,251 tickers.**

| | pooled slot | pooled ovl | Δ | xsec slot | xsec ovl | per-day |
|---|---|---|---|---|---|---|
| off=0, hl=10m | **0.8684** | 0.8612 | −0.0073 | **0.8481** | 0.8389 | slot 43/43 |
| **off=15, hl=10m** | **0.8670** | 0.8610 | **−0.0060** | **0.8465** | 0.8387 | **slot 43/43** |
| off=0, hl=20m | **0.8666** | 0.8629 | −0.0037 | **0.8504** | 0.8452 | — |
| off=15, hl=20m | **0.8657** | 0.8627 | −0.0030 | **0.8494** | 0.8449 | — |

**Per-year mean per-day Δ (ovl − slot, hl=10m):** 2023 −0.0125 · 2024 −0.0083 · 2025 −0.0052 ·
2026 −0.0068 (off=0; off=15 uniformly ~0.0015 kinder). **Zero overlap wins in any year at either
offset.**

**Readings:**
1. **The user's staleness intuition is real but small.** Going from slot-friendly to staleness-fair spots
   costs the slot version ~0.0014 (0.8684→0.8670) while the overlap version doesn't move (0.8612→0.8610,
   as expected — it's always fresh). Staleness recovers only ~a fifth of the gap; the rest is the F3/F4
   endpoint-smoothing toll, which no spot grid can undo.
2. **The verdict is now about as sure as this harness can make it:** 86/86 day-grid cells to the slot
   construction, across 43 months and two half-lives. The overlap variant is the better *engineered*
   object (always-current) but the worse *estimator*, and the estimator deficit dominates.
3. The gap narrows in recent years (−0.0125 → ~−0.005) and at hl=20m (−0.003) — if the engine ever wants
   the always-fresh property anyway (e.g. for an intra-bar stop rule), overlap-EWMA hl=20m at −0.003 is
   the cheapest version of it. Not the default.

**🔒 vol20m LOCKED: slot-EWMA — 30-present-bar slot vwaps → r = ln(V/V_prev) → EmaMa of r², hl=10m
(α = 1−0.5^(1/20)) primary + hl=20m (α = 1−0.5^(1/40)) twin, √ on read.** Staleness between slot
boundaries is measured and priced: ~0.0014, not worth the −0.006 estimator toll to remove.

Artifacts: `/tmp/volbake_f6/*.parquet`, `/tmp/volbake_f6_day.sql.tmpl`, `/tmp/volbake_f6_corr.sql`.

---

## Finding 7 — ⭐⭐ |r| beats r²: EWMA of absolute returns, hl=20m, is the campaign champion — the lock is AMENDED

**Question (user):** "Would averaging the abs r instead of r × r be better?" Same 43-day / two-grid F6
harness; EWMA of |r| vs EWMA of r² on the same 30s slot returns, hl ∈ {10m, 20m}, then a half-life sweep
{10, 20, 30, 40}m for the abs form.

| | pooled (off=0) | pooled (off=15) | xsec | per-day vs r² twin |
|---|---|---|---|---|
| **abs, hl=20m** | **0.8707** | **0.8698** | 0.8539 | **43/43** (42/43 off=15) |
| abs, hl=10m | 0.8695 | 0.8681 | 0.8485 | 27/43 |
| r², hl=10m (the F6 lock) | 0.8684 | 0.8670 | 0.8481 | — |
| r², hl=20m | 0.8666 | 0.8657 | 0.8504 | — |

**The full kernel × half-life surface (pooled, off=0; user asked for the r² side too):**

| hl | abs | r² | abs − r² |
|---|---|---|---|
| 10m | 0.8695 | **0.8684** | +0.0011 |
| 20m | **0.8707** ⭐ | 0.8666 | +0.0041 |
| 30m | 0.8688 | 0.8621 | +0.0067 |
| 40m | 0.8672 | 0.8585 | +0.0087 |

abs peaks at 20m (30m wins only 4/43 days vs 20m); r² peaks at ≤10m and decays ~2.5× faster with
memory — the confirmation of the interaction story: the longer the memory, the more the quadratic
kernel's stale outlier-spikes cost, so the abs advantage *widens* monotonically with hl. Cross-sectional
view agrees in shape (abs 20-40m plateau ~0.854; r² peaks at 20m then falls). Redundancy abs↔r² at
matched hl: 0.996.

**Readings:**
1. **The user's instinct was right — and it resolves the F5 tail story into a clean dose-response.**
   Median |r| (MAD) *ignores* the tail → lost −0.03. Squared r *amplifies* it quadratically → good, but
   the spike it injects forces a short memory (r² prefers hl=10m; at hl=20m it degrades). Mean |r| keeps
   the outlier's vote but **linearly** — and with tail influence tamed, longer memory turns from liability
   to asset (abs *improves* 10m→20m). Linear tail influence is the sweet spot between robust and
   responsive: it beats the r² form 43/43 days at hl=20m.
2. **The interaction matters more than either choice alone:** kernel and half-life are not separable
   (r²: 10m > 20m; abs: 20m > 10m). Sweeping half-life under only one kernel would have missed the
   champion.
3. **Engine form is unchanged in complexity:** `EmaMa` fed `abs r` instead of `r*r`, no √ on read. In
   σ-units multiply by √(π/2) ≈ 1.2533 (Gaussian E|r| = σ·√(2/π)) — irrelevant for ranking/gating.

**🔒 vol20m RE-LOCKED (amends F6): slot construction unchanged — 30-present-bar slot vwaps →
r = ln(V/V_prev) → `EmaMa` of |r|, hl=20m (α = 1−0.5^(1/40)) primary + hl=10m (α = 1−0.5^(1/20))
twin.** The r² columns are dropped.

### F7b — slot vs overlap under the abs kernel (user request): the toll is kernel-independent; the lock survives its last challenger

Same 43 days / both grids; overlap-EWMA of |r| (per-second rolling-30s-vwap returns) vs the slot twin:

| | slot | overlap | Δ | per-day |
|---|---|---|---|---|
| hl=20m, off=0 | **0.8707** | 0.8661 | −0.0046 | slot 43/43 |
| hl=20m, off=15 (staleness-fair) | **0.8698** | 0.8660 | −0.0038 | slot 43/43 |
| hl=10m, off=0 | **0.8695** | 0.8611 | −0.0083 | — |
| hl=10m, off=15 | **0.8681** | 0.8610 | −0.0071 | — |

Cross-sectional agrees (0.8539 vs 0.8478 at hl20/off=0). Redundancy 0.988. The overlap toll is
**kernel-independent** (r²: −0.003/−0.006; abs: −0.004/−0.008 — same size, same shape, larger at the
shorter half-life where endpoint noise matters more), and overlap-abs-hl20 (0.8661) does not even reach
slot-r²-hl10 (0.8684). Every construction axis now points the same way: **the F7 lock stands.**

Artifacts: `/tmp/volbake_f6abs*/*.parquet`, `/tmp/volbake_f6oabs/*.parquet`,
`/tmp/volbake_f6abs*_day.sql.tmpl`, `/tmp/volbake_f6oabs_day.sql.tmpl`, `/tmp/volbake_f6*_corr.sql`.

---

## Finding 8 — ⭐ the complement study: 20m range adds real independent signal on top of the EWMA lock; efficiency ratio is a clean orthogonal trendiness axis; the lock stands

**Question (user):** could volatility estimation be improved by *combining* the locked EWMA with shape
features of the same window — the 20m range, the 20m max of 30s |r|? E.g. "tight consolidation now but
the 20m range is wide" — is that a state where the EWMA misreads? Test the pairwise redundancy of these
estimators.

**Setup:** F6/F7-style harness rebuilt (the /tmp artifacts did not survive; template
`volbake_f8_day.sql.tmpl` in the session scratchpad): 43 monthly days 2023-01 → 2026-07 (nearest trading
day to the 15th — day picks differ slightly from F6's list), off=0 minute grid 10:00→15:30, same target
(std of next ~10 one-minute-vwap log-returns), same guards (≥16/20 trailing, ≥8/10 forward 1m slots) plus
≥10 slot returns in the trailing 20m, same-day $50M RTH dollar-vol universe, source =
`data/intraday_1s_slim/`. **n = 18,078,233 obs / 43 days / 14,233 xsec spots.** Validation: the champion
reproduces — **ew20 pooled 0.8717** here vs the published 0.8707 (different day picks account for it).

The candidates (all from the same 30s slot-vwap return stream, trailing 20m = 40 slots unless noted):
- **ew20 / ew10** — the F7 lock: EWMA of |r|, hl=20m/10m, wall-clock decay, full-session support.
- **mean_abs** — equal-weight 20m mean of |r| (the SMA control).
- **mx** — max |r| over the 20m window (spike detector).
- **rng** — `ln(max V / min V)` over the 20m slot-vwap path (total range traveled).
- **net** — `|ln(V_last / V_first)|` (net traverse — the BM drift MLE numerator; for pure BM the
  endpoints are the *sufficient statistic* for drift, interior points add nothing).
- **eff** — `net / Σ|r|` (Kaufman efficiency ratio). For fixed n this is a monotone transform of the
  drift t-statistic: `t = eff·√(2n/π)` ≈ 5.05·eff at n=40. Random-walk null: E[eff] ≈ 1/√n = 0.158;
  measured mean 0.162 — the average 20m window is almost exactly a random walk.
  ⚠ Study-construction fencepost (user caught it): the study's Σ|r| selects returns by *known-time* ∈
  window (the EWMA event-stream convention), so it includes the first window slot's return, whose left
  endpoint precedes the window — 40 returns in the denominator vs the 39 the numerator telescopes over,
  and on gappy names net can span holes Σ|r| skips (eff > 1 possible). Uniform ~1/40 dilution,
  rank-invisible; NOT re-run. The engine form (`net = |ln(V_t / LagMa(40) V)|`, `SumMa(40)` of |r|) is
  exactly consistent — both cover the same 40 returns (present-bar slots are gapless by construction),
  so eff ≤ 1 unconditionally.

**Standalone predictive power + redundancy with the lock:**

| measure | pooled ρ | xsec ρ | redundancy vs ew20 |
|---|---|---|---|
| **ew20 (lock)** | **0.8717** | **0.8550** | — |
| ew10 | 0.8702 | — | 0.9939 |
| mean_abs | 0.8609 | — | 0.9824 |
| mx | 0.8288 | 0.8015 | 0.9345 |
| rng | 0.8002 | 0.7664 | 0.8791 |
| net | 0.5552 | — | 0.5894 |
| eff | 0.0625 | 0.0608 | **0.0237** |

Other pairs: mx↔rng 0.877, mx↔mean_abs 0.951, rng↔net 0.898, rng↔eff 0.311, eff↔net 0.769.

**Incremental value on top of ew20 (pooled partial Spearman, rank-OLS combined R, per-day wins):**

| added to ew20 | partial ρ \| ew20 | combined R | gain | per-day combo wins | per-day partial > 0 |
|---|---|---|---|---|---|
| **rng** | **+0.1452** | **0.8746** | **+0.0029** | **43/43** (mean +0.0032, min +0.0012) | 43/43 |
| net | +0.1048 | 0.8732 | +0.0015 | — | — |
| eff | +0.0854 | 0.8727 | +0.0010 | 43/43 | 43/43 |
| mx | +0.0816 | 0.8726 | +0.0009 | 43/43 | 43/43 |
| ew10 (twin) | +0.0705 | 0.8724 | +0.0007 | — | — |
| mean_abs | +0.0489 | 0.8720 | +0.0003 | — | — |

Second-order: partial of mx given BOTH ew20 and rng ≈ +0.037 — the spike detector is mostly a subset of
the range signal.

**The user's scenario directly (pooled ew20-decile × within-decile rng/eff terciles, median forward vol
in bp):**

| ew20 decile | med ew20 (bp/30s) | rng T1 | rng T2 | rng T3 | eff T1 | eff T3 |
|---|---|---|---|---|---|---|
| 1 (quietest) | 0.85 | 0.57 | 1.26 | **1.66** | 0.87 | 1.37 |
| 2 | 1.55 | 2.22 | 2.36 | 2.50 | 2.31 | 2.42 |
| 5 | 2.90 | 3.94 | 4.10 | 4.31 | 4.02 | 4.25 |
| 8 | 5.31 | 6.55 | 7.02 | 7.55 | 6.83 | 7.33 |
| 9 | 7.18 | 8.48 | 9.33 | 10.38 | 9.05 | 9.83 |
| 10 (hottest) | 12.39 | 12.78 | 15.89 | **27.16** | 15.96 | 17.94 |

(Full 10-decile table monotone in every row for both features.)

**Readings:**
1. **The user's intuition is confirmed in the exact corner named:** at a fixed EWMA reading, a wide
   trailing range means the EWMA *underestimates* forward vol — by ~3× in the quietest decile
   (0.57 → 1.66 bp) and ~2× in the hottest (12.78 → 27.16 bp). "Tight consolidation inside a wide range"
   is a real, measurable EWMA blind spot.
2. **But rank-wise the correction is small:** combined R 0.8746 vs 0.8717 (+0.003). For context the whole
   F4→F7 campaign was worth +0.011 — so rng is worth ~¼ of a campaign, concentrated in the tail states
   the deciles expose. As a *normalizer* the lock stands; as a *state descriptor* rng carries genuinely
   new information (partial +0.145, 43/43 days).
3. **Efficiency ratio is the orthogonal axis, as designed:** ρ(eff, ew20) = 0.024 — trendiness is
   *independent of vol level* — yet its partial is +0.085, 43/43 days positive: given the same EWMA,
   a trending window precedes more vol than a choppy one. Small but perfectly clean.
4. **mx is dominated** (partial +0.037 after ew20+rng): the spike's contribution to the range covers most
   of what the max knows. Range estimators failed as *replacements* (F5); the range succeeds as a
   *complement* — different job.
5. Under the BM-with-drift model, `net` is the drift MLE numerator, `eff` its t-statistic, and the
   measured mean eff sitting on the 1/√n random-walk null says drift is mostly *not* identifiable in
   20 minutes — which is exactly why eff carries no vol-level information and works as a pure shape/state
   feature.

**⭐ DECISION: the vol20m lock (F7) is UNCHANGED — ew20 remains THE normalizer. The engine additionally
records two complement columns: `rng20m` (≈ free — derivable from the 1200-bar MaxMa/MinMa vwap channel
already in the design as ln(chanHigh/chanLow); note the engine form uses 1s vwaps, so it reads slightly
wider than the study's slot-vwap form) and `eff20m` (`LagMa(40)` on slot vwaps for net + `SumMa(40)` on
slot |r|). `mx`, `net`, `mean_abs` are NOT carried (dominated). Combining into a single fitted estimator
is deferred — the +0.003 doesn't justify a fitted object in the engine; the sampler's post-hoc breakdowns
get the two columns instead.**

Artifacts: `volbake_f8/*.parquet`, `volbake_f8_day.sql.tmpl`, `f8_corr.sql` (session scratchpad;
perishable like all /tmp study artifacts).

### F8b — out-of-sample interaction fits (user request): the rng complement survives OOS; the interaction is real but NOT multiplicative — it lives in the per-decile slope shape

**Question (user):** does the ew20+rng correction survive as an actual *estimator* out-of-sample, and
does an interaction-aware fit (product term, or rng fit within ew20-deciles) beat the additive one?

**Setup:** train = the 24 study days of 2023-24, test = the 19 days of 2025-26. Fits in log space
(deployable — ranks don't transfer across datasets; ln-Pearson ≈ 0.9 says log-linear is the right
parametrization): `ln(tgt) ~ ln(ew20) [+ ln(rng)] [+ ln(ew20)·ln(rng)]`, and the per-decile variant
(decile boundaries of ew20 frozen on train; separate bivariate fit inside each decile). Baseline M0 =
ew20 alone (fit-free; monotone transforms don't move Spearman). OOS n = 9,253,863.

| model | OOS pooled Spearman | OOS ln-Pearson | per-day vs M0 |
|---|---|---|---|
| M0 ew20 alone | 0.8787 | 0.9081 | — |
| M2 additive +ln(rng) | 0.8802 | 0.9111 | 18/19 (mean +0.0014) |
| M3 + product term | 0.8802 | 0.9111 | 18/19 (+0.00003 over M2) |
| **M4 per-decile** | **0.8804** | **0.9122** | **19/19** (mean +0.0017; beats M2 19/19) |

**The fitted shape is the finding (train coefficients):**

| | b_ew (log-slope) | b_rng |
|---|---|---|
| global additive (M2) | 0.770 | 0.169 |
| decile 1 (quietest) | 0.901 | 0.181 |
| decile 3 | 0.754 | 0.094 |
| decile 5 | 0.739 | 0.119 |
| decile 8 | 0.735 | 0.162 |
| decile 10 (hottest) | 0.786 | **0.240** |

**Readings:**
1. **The rng correction is a real OOS estimator improvement, not attribution:** +0.0014-0.0017 Spearman
   and +0.003-0.004 ln-Pearson on unseen 2025-26, 18-19/19 days. (~Half the in-sample rank-optimal
   +0.0029 — expected: the log-linear fit isn't rank-optimal and the weights are frozen.)
2. **The multiplicative product term is worthless** (M3−M2 = +0.00003, coefficient −0.005): the
   interaction is not a smooth product. It lives in the *slope profile*: b_rng is J-shaped across
   deciles — 0.18 in the quietest decile, dip to ~0.09-0.12 mid-range, monotone climb to 0.24 in the
   hottest. Range information matters most exactly where the F8 tercile table showed the biggest
   spread: hot names (12.8→27.2bp) and dead-quiet ones.
3. **b_ew ≈ 0.74-0.79 < 1 everywhere** — the mean-reversion shrinkage measured in the calibration query
   (fwd vol / EWMA-implied falls 0.87→0.72 with level) shows up directly as a log-slope below 1: a 1%
   hotter EWMA predicts only ~0.77% more forward vol.
4. **Engine consequence: none now.** The sampler records ew20 + rng20m (+ eff20m) as separate columns
   per F8; the fitted calibration (per-decile or the shrinkage map) belongs to the SIZING stage of a
   live book, not to gating/ranking. When sizing is designed, M4's coefficients are the starting point.

Artifacts: `f8b_train_agg.csv`, `f8b_train_dec.csv`, `f8b_score.sql` (session scratchpad).

---

## Finding 9 — first sampler breakdowns (60 trading days): EARLY-in-leg wins, loose channels beat tight, extreme-z is the only acceleration signal, gap-free tape INVERTS

**Setup:** the first real SurgeRider run — 2026-04-22 → 2026-07-17 (60 trading days), 60-bar entry/exit
channels, $10M dv_0945 floor, mc=0 sampler → **3,373,627 trips / 7,566 ticker-days / 289s** (the
active/retired-split engine at 0.04 s/candidate). ⚠ ALL numbers are ATTRIBUTION (mc=0), costs NOT
modeled (~0.01-0.03%/trip cells are BELOW round-trip costs; only cells ≥ ~0.1% clear them).
`ret_f300`/`ret_f1200` = the exit-independent forward-mark returns (fwd vwap / entry − 1).

**A. The core acceleration hypothesis — z_vol_60 at entry vs outcome:**

| z bucket | n | win% | ret_exit% | ret_f300% | ret_f1200% | PF |
|---|---|---|---|---|---|---|
| z<0 | 1,883,357 | 48.5 | +0.0024 | +0.0096 | +0.0077 | 1.101 |
| 0-1 | 942,708 | 47.9 | +0.0068 | +0.0092 | +0.0027 | 1.089 |
| 1-2 | 404,533 | 46.8 | +0.0111 | +0.0102 | −0.0099 | 1.084 |
| 2-3 | 114,180 | 45.6 | −0.0092 | +0.0059 | −0.0374 | 0.947 |
| **z≥3** | **28,849** | 45.6 | +0.0145 | **+0.060** | **+0.104** | 1.090 |

NON-monotone: mid-z (2-3) is the WORST cell, and only the extreme tail (z≥3, 0.9% of trips) shows a
real forward kick (+0.10% at 20m — the only cell that clears costs). Echoes the MR-era finding that the
long-momentum edge lives in the pre-breakout volume SURGE, not in mild elevation. ⚠ z<0's high PF is
partly an artifact: z<0 IS the exit condition, so those trips exit in ~1 bar with near-zero spread-free
returns. Confounds (time-of-day, liquidity) not yet controlled.

**B. trade_idx (the user's up-leg reset counter) — early beats late, cleanly:**

| trade_idx | n | ret_exit% | ret_f300% | PF |
|---|---|---|---|---|
| 0 | 75,817 | +0.0083 | +0.0238 | 1.159 |
| 1-2 | 117,319 | +0.0089 | **+0.0274** | **1.167** |
| 3-5 | 150,397 | +0.0078 | +0.0239 | 1.153 |
| 6-10 | 210,042 | +0.0058 | +0.0198 | 1.116 |
| 11-30 | 609,006 | +0.0050 | +0.0149 | 1.100 |
| >30 | 2,211,046 | +0.0034 | +0.0051 | 1.056 |

Monotone decay past idx ~5; the first ~5 trades of a leg are a broad plateau (PF ~1.16, f300 ~+0.025%).
**65% of the sampler's book is >30-idx late chases** diluting the aggregate.

**C. gap60 = 0 (dense tape) — INVERTED vs expectation:**

| gap-free (gap60=0) | n | ret_exit% | ret_f300% | PF |
|---|---|---|---|---|
| false | 2,663,989 | +0.0073 | +0.0177 | 1.145 |
| **true** | 709,638 | **−0.0067** | **−0.0195** | **0.919** |

The "only trade gap-free tape" hypothesis fails at first pass — gap60=0 selects the mega-liquid names
(the 34%-of-entries-at-≥$100M band) whose 1s momentum apparently mean-reverts. ⚠ It is a LIQUIDITY
PROXY here, not a tape-quality signal; needs conditioning on dv band before any conclusion.

**D. Channel tightening (free from the breach counters — the 60→1200 "sweep" in one run):**

| book | n | ret_exit% | PF |
|---|---|---|---|
| all (60-bar gate) | 3,373,627 | +0.0044 | 1.076 |
| breach_120 = 0 | 2,408,903 | +0.0038 | 1.058 |
| breach_300 = 0 | 1,546,916 | +0.0031 | 1.040 |
| breach_1200 = 0 | 736,829 | +0.0011 | 1.011 |
| breach_sess = 0 | 278,728 | +0.0058 | 1.046 |

Monotonically WORSE as the channel tightens (60 → 1200), with a partial recovery at session-high
breakouts. The local 60-bar pop carries more forward drift than the big-window breakout at these
horizons — and it vindicates gating the sampler on the loosest channel (a 300-gated run could never
have seen rows the tightened books exclude).

**Readings:** (1) the promising cells to intersect next: trade_idx ≤ 5 × z≥3 × (gappy or small-dv),
with time-of-day and vol-block conditioning; (2) the z-exit at 0 makes most trips 1-2-bar scratches —
the exit rule itself is a lever to study via the forward marks; (3) everything here is one 60-day
recent-regime window — the 2023→2026 run is the next scale-up.

Artifacts: `trips_60d/*.parquet` (session scratchpad), engine commit 4e6338d.

---

## Finding 10 — ⭐ the vol20m breakdown: forward returns rise MONOTONICALLY with vol through p90, then INVERT catastrophically — a vol BAND, the momentum mirror of the MR ATR band

**Question (user):** the ATR% breakdown was a huge lever for the MR systems — does vol20m do the same
here? Hoped: avg trade % rises with volatility, ideally PF too. (Also the motivating worry: most trips'
gains are below commissions — which cells clear costs?)

Same 60-day / 3.37M-trip sampler as F9. `vol_20m` = the locked EmaHlMa of |slot r|, units = mean-|r|
per 30s slot (×1.77 ≈ 1m σ, per F8b's calibration).

**Deciles (bp = vol_20m × 1e4, per 30s slot):**

| dec | med vol (bp) | n | win% | ret_exit% | ret_f300% | ret_f1200% | PF | PF clip+50% |
|---|---|---|---|---|---|---|---|---|
| 1 | 3.7 | 337k | 47.5 | +0.0014 | +0.0043 | +0.0018 | 1.116 | 1.116 |
| 2 | 5.9 | 337k | 47.5 | +0.0030 | +0.0066 | +0.0139 | 1.166 | 1.166 |
| 3 | 7.8 | 337k | 48.0 | +0.0043 | +0.0096 | +0.0242 | 1.180 | 1.180 |
| 4 | 9.6 | 337k | 47.8 | +0.0050 | +0.0098 | +0.0206 | 1.173 | 1.173 |
| 5 | 11.7 | 337k | 48.0 | +0.0036 | +0.0145 | +0.0296 | 1.106 | 1.106 |
| 6 | 14.1 | 337k | 48.1 | +0.0035 | +0.0170 | +0.0299 | 1.090 | 1.090 |
| 7 | 17.2 | 337k | 48.3 | +0.0044 | +0.0216 | +0.0454 | 1.097 | 1.097 |
| 8 | 21.4 | 337k | 48.7 | +0.0080 | +0.0352 | +0.0749 | 1.152 | 1.152 |
| 9 | 28.7 | 337k | 48.7 | +0.0115 | +0.0361 | **+0.0821** | 1.165 | 1.165 |
| 10 | 54.8 | 337k | 47.4 | −0.0010 | −0.0561 | **−0.2875** | 0.996 | 0.991 |

**Decile 10's interior — the break is at ~p92 (≈41 bp/30s):**

| slice | from (bp) | n | ret_exit% | ret_f300% | ret_f1200% | PF |
|---|---|---|---|---|---|---|
| p90-92 | 36.0 | 67k | +0.0138 | +0.0344 | +0.0763 | 1.148 |
| p92-94 | 41.0 | 67k | +0.0189 | +0.0096 | −0.0325 | 1.164 |
| p94-96 | 49.0 | 67k | +0.0160 | −0.0126 | −0.1803 | 1.099 |
| p96-98 | 62.6 | 67k | +0.0156 | −0.1092 | −0.4121 | 1.056 |
| p98-100 | 90.4 | 67k | −0.0694 | −0.2025 | **−0.8890** | 0.885 |

**The band × the F9 positives (band = vol_20m ∈ [p20, p90) = [6.8, 36.0) bp/30s):**

| cell | n | ret_exit% | f300% | f1200% | PF |
|---|---|---|---|---|---|
| band alone | 2,361,535 | +0.0057 | +0.0205 | +0.0438 | 1.137 |
| band × idx≤5 | 232,104 | +0.0074 | +0.0309 | +0.0442 | 1.176 |
| **band × z≥3** | **19,187** | +0.0216 | **+0.0556** | **+0.1163** | **1.190** |
| band × idx≤5 × z≥3 | 1,523 | +0.0155 | +0.0193 | +0.0162 | 1.156 |

**Readings:**
1. **The user's hope confirmed through p90:** forward returns scale monotonically with vol (f1200
   +0.002% → +0.082%, 40×, deciles 1→9). PF is U-shaped (best 2-4 and 8-9) because the low-vol
   deciles are scratch-heavy (tiny wins, tiny losses) while 8-9 add real drift.
2. **⭐ THE CEILING: past ~41 bp/30s (~p92; ≈0.7% 1m σ) everything inverts,** reaching −0.89%/20m in
   the top 2%. The exact momentum mirror of the MR MaxAtrPct finding ("past ~3.5% the name is not
   oscillating, it is BROKEN") — here, past ~p92 the name is not breaking out, it is ALREADY blown
   off. Note ret_exit stays positive to p98 — the z-exit escapes the collapse; the marks show what
   the entry signal itself walks into.
3. **The band × z≥3 cell is the first that clears costs by a margin:** +0.116%/20m on 19k trips/60d
   (~320/day). idx≤5 helps the band too (PF 1.176).
4. **⚠ The two positives do NOT compound:** band × idx≤5 × z≥3 (n=1.5k) DROPS to +0.016%/20m. The
   very first extreme-z spike of a fresh leg (right off a 20m low) looks more like a bounce-to-fade
   than a breakout; the z≥3 edge lives in the LATER legs. Small n — verify on the long run.
5. Working parameter sense: **vol band ≈ [7, 40] bp/30s** (p20-p92); the next run's breakdowns should
   treat vol_20m as the primary conditioning axis, as ATR% was for MR.

Artifacts: same trips_60d parquet; queries inline in the session log.

---

## Finding 11 — the [20,40)bp band × STOCKS IN PLAY: rvol≥10 lifts the cell to PF ~1.4-1.5 / +0.2%/5m; the z-exit leaves most of the drift on the table; band promoted to the ENGINE DEFAULT

**Question (user):** +0.116% is far too low — target ≥ +0.3%/trade. Focus the [20,40)bp vol band
("we need volatility for the larger gains") and intersect with stocks in play — high rvol in the first
15m including premarket (`rvol_0945_honest`). How does it look?

Same 60-day sampler, now filtered to vol_20m ∈ [20,40)bp/30s. `moc` = the hold-to-close counterfactual
`day_close/entry_px − 1` (per feedback convention: raw AND +50%-clipped shown).
⚠ TWO caveats: (1) MOC returns are PSEUDO-REPLICATED — every trip on the same ticker-day shares one
day_close, so the effective n is the ticker-day count, not the trip count; (2) the in-play cells are
thin at 60 days (~100s of ticker-days) — the long run must confirm.

| rvol bucket | n | ret_exit% | f300% | f1200% | moc% | moc_clip% | PF_moc | PF_moc_clip |
|---|---|---|---|---|---|---|---|---|
| <2 | 637,249 | +0.0097 | +0.0324 | +0.0807 | +0.5418 | +0.5418 | 1.413 | 1.413 |
| 2-5 | 10,554 | +0.0092 | +0.0473 | +0.0844 | −0.6972 | −0.6972 | 0.663 | 0.663 |
| 5-10 | 3,801 | +0.0105 | +0.0336 | −0.1208 | −1.5885 | −1.5885 | 0.566 | 0.566 |
| **10-30** | **3,643** | **+0.0657** | **+0.1774** | **+0.1984** | **+1.1962** | +1.1962 | **1.666** | 1.666 |
| ≥30 | 3,608 | +0.0673 | +0.2097 | +0.0563 | −1.0387 | −1.0387 | 0.633 | 0.633 |

(Exit-rule PF for the two top buckets: 1.495 / 1.357 — F9 metrics table in the session log.)

**Inside rvol≥10 (n=7,251), by leg position × entry hour:**

| early leg (idx≤5) | before 11:00 | n | f300% | f1200% | moc_clip% | PF_moc_clip |
|---|---|---|---|---|---|---|
| yes | yes | 251 | +0.4635 | +0.0887 | −1.9599 | 0.524 |
| no | yes | 1,297 | +0.1401 | −0.5777 | +0.5483 | 1.221 |
| **yes** | **no** | **722** | +0.1424 | **+0.2374** | **+1.0549** | **1.673** |
| no | no | 4,981 | +0.2012 | +0.2974 | −0.0744 | 0.967 |

**Readings:**
1. **Stocks-in-play works as hoped in the 10-30 bucket:** in-band × rvol 10-30 = exit-PF ~1.5,
   +0.18%/5m, +0.20%/20m, and the MOC counterfactual says +1.20%/PF 1.67 — the first cell in the
   system's history to smell like the MR books. But the rvol ladder is NON-monotone (2-10 negative,
   ≥30 collapses at MOC) — at these n's, treat the shape as suggestive, not measured.
2. **⭐ The path to the user's +0.3% target is the EXIT, not more entry filters:** in the hot cell the
   z-exit banks +0.066% while the 5-minute mark shows +0.18-0.21% and MOC +1.2% — the exit rule
   captures ~a third of the 5m drift and ~5% of the day drift. The z<0 exit was designed to detect
   "acceleration died"; in an in-play name that fires on every breather. Exit redesign (hold-to-MOC,
   trailing channel, or a much lower EZV) is the next engine lever.
3. The afternoon early-leg cell (idx≤5, after 11:00) is the cleanest sub-cell (f1200 +0.24%, MOC_clip
   +1.05%, PF 1.67) — afternoon breakouts of in-play names that just started a fresh leg.
4. **ENGINE CHANGE: the [20,40)bp band is now the DEFAULT hard gate** (`MinVol20m = 0.0020`,
   `MaxVol20m = 0.0040`, CLI `--min-vol-20m`/`--max-vol-20m`, floor 0 / ceiling inf = off). This
   shrinks future sampler runs ~5× and focuses them on the drift-rich band; vol_20m is still recorded
   for within-band slicing. ⚠ rvol_0945_honest stays RECORDED, NOT gated (a gate would need its own
   lookahead audit — the honest denominator passed once, but gating changes the universe).
5. ⏭ The in-play cells need the 2023→2026 run for real statistics (~20× the ticker-days).

Artifacts: same trips_60d parquet; band gate in commit (pending).

---

## Finding 12 — ⭐⭐ CHANNEL-ONLY EXITS: the scalp book emerges — in-play cells hit +0.39%/trip at PF 2.1-2.6; fast (15/30-bar) z's beat the 1m z in-play

**Decision (user):** this is a SCALPING system — no holding to MOC (intraday swings to the close are a
known dead end from prior systems). z-exits DISABLED (Ezv/Ezt default → −inf; the F11 mechanisms made
them an instant-eject + disguised time stop). **Exit = the 60-bar channel break alone.** Re-run of the
60-day sampler: band gate [20,40)bp (now default) + 60-bar entry/exit channels →
**658,855 trips / 7,566 ticker-days / 121s** (5.1× fewer trips than the z-exit run — the band gate).

**Headline (all trips, mc=0 attribution, no costs):** avg +0.0388%, median −0.0963% (the ride-winners/
cut-losers skew — win rate 41.1%), median hold 76 present bars / p90 186, **100.0% channel exits,
0.0% MOC** (nothing survives to the close even without the z-exits), PF 1.204 (vs 1.076 under z-exits).

**The in-play ladder under the real exit (rvol_0945_honest buckets; clip+50% twin identical):**

| rvol | n | win% | avg ret% | f1200% | PF | med hold |
|---|---|---|---|---|---|---|
| <2 | 637,249 | 41.1 | +0.0361 | +0.0807 | 1.192 | 76 |
| 2-5 | 10,554 | 41.4 | +0.0461 | +0.0844 | 1.200 | 76 |
| 5-10 | 3,801 | 39.0 | +0.0008 | −0.1208 | 1.003 | 77 |
| 10-30 | 3,643 | 42.2 | +0.1792 | +0.1984 | 1.660 | 83 |
| **≥30** | **3,608** | **45.6** | **+0.3931** | +0.0563 | **2.118** | 104 |

**⭐ THE USER'S +0.3% TARGET IS HIT** in rvol≥30 (+0.39%/trip, ~60 trips/day) — the SAME bucket that
collapsed at MOC in F11 (−1.04%). The scalp exit monetizes what the swing hold gives back: these names
spike and retrace intraday; the channel exit banks the spike leg. trade_idx inside rvol≥10:

| idx | n | avg ret% | PF | med hold |
|---|---|---|---|---|
| 0 | 275 | +0.3369 | 2.400 | 102 |
| **1-5** | **1,107** | **+0.3980** | **2.639** | 104 |
| 6-20 | 2,039 | +0.2630 | 1.828 | 93 |
| >20 | 3,830 | +0.2615 | 1.785 | 88 |

**The z-scale ladder (user question: are 15/30-bar z's clearer than the 1m?):** full band-gated sample
= flat noise on every k (no bucket exceeds +0.046%; z≥3 vol is WORSE). Inside rvol≥10:

| feature, z≥2 | n | avg ret% | PF |
|---|---|---|---|
| z_vol_15 | 441 | +0.3464 | 1.938 |
| z_vol_30 | 400 | +0.3575 | 1.898 |
| z_tc_15 | 822 | +0.3366 | 1.890 |
| z_tc_30 | 622 | +0.2903 | 1.715 |
| z_tc_60 | 464 | +0.2262 | 1.504 |
| **z_vol_60** | 374 | **+0.0953** | 1.206 |

**Readings:**
1. **The scalp thesis is vindicated against both alternative exits:** channel-only beats the z-exit
   book everywhere (PF 1.204 vs 1.076 overall) and beats MOC exactly where it matters (rvol≥30:
   +0.39% scalped vs −1.04% held). Spike-and-retrace names want the trailing exit.
2. **In-play × early-leg is the flagship cell: rvol≥10 × idx≤5 = +0.39%/trip, PF 2.5-2.6, ~23
   trips/day.** rvol≥30 alone: +0.39%, PF 2.12, ~60/day.
3. **⭐ The user's instinct on fast z's is right — IN-PLAY:** at z≥2 the 15/30-bar z's hold
   +0.29-0.36% (PF 1.7-1.9) while the 1m z_vol_60 collapses to +0.095%. Acceleration is a
   seconds-scale phenomenon; by the time the 1m aggregate is 2σ hot the move is stale. (In the full
   band-gated sample NO z separates — the z's only matter once the name is in play.)
4. Win rate 41% with median −0.1%: the book is a classic cut-losers/ride-winners scalper; sizing and
   cost modeling (spread on in-play names) are the open practical questions.
5. ⏭ Confirm on 2023→2026 (in-play cells are ~100s of ticker-days here); then mc=1.

Artifacts: `trips_60d_chan/*.parquet` (session scratchpad).

---

## Finding 13 — the z-scale gradient points to SECONDS (z_tc_1 is the best z yet) + ⭐ the tc floor INVERTS: quiet tape beats busy tape

**Questions (user):** (1) 15s z's look promising — try 5-10s too? (2) find the minimum trade-count
threshold "where the stock isn't dead."

**1. The ladder's short end (free — k=1 was already recorded). In-play (rvol≥10), z≥2, channel-exit
run:** `z_tc_1` = **+0.378% / PF 2.11 / n=1,294** — the strongest z-feature measured, with 3× the
sample of z_vol_15. The full gradient (avg ret% at z≥2, in-play):

| k (bars ≈ secs) | z_vol | z_tc |
|---|---|---|
| 1 | +0.200 | **+0.378** |
| 15 | +0.346 | +0.337 |
| 30 | +0.358 | +0.290 |
| 60 | +0.095 | +0.226 |

Trade-count improves MONOTONICALLY toward shorter windows; volume peaks at 15-30s and dies at 1m.
Acceleration is a seconds-scale phenomenon, and the trade-count tape (participation) leads the volume
tape (size). → **k=5 and k=10 z's added to the engine** (z_vol_5/10, z_tc_5/10 — 4 sums + 4 baselines
+ 4 columns) to fill the gap straddling both peaks; measured in F13b below once the re-run lands.

**2. The tc floor inverts.** Raw tc_60 (prints over the trailing 60 present bars; hard floor 60):

| tc_60 (all trips) | n | avg ret% | PF | | tc_60 (in-play) | n | avg ret% | PF |
|---|---|---|---|---|---|---|---|---|
| 60-120 | 1,529 | +0.079 | 1.390 | | <300 | 4,115 | **+0.385** | **2.330** |
| 120-300 | 144,420 | +0.057 | 1.305 | | 300-600 | 2,137 | +0.163 | 1.457 |
| 300-600 | 252,154 | +0.039 | 1.205 | | 600-1500 | 778 | +0.107 | 1.335 |
| 600-1500 | 193,204 | +0.031 | 1.163 | | 1500-4k | 221 | +0.253 | 1.962 |
| 1500-4k | 55,834 | +0.016 | 1.082 | | | | | |
| ≥4k | 11,714 | +0.035 | 1.181 | | | | | |

**There is no "dead stock" floor to find above the existing gates** (tc≥60 + $100k/min + the vol band
already cut the corpses): PF DECLINES monotonically with activity, and in-play the tc<300 cell is the
best cell in the study (+0.385%, PF 2.33). Fewer prints = fewer participants = less efficient pricing =
momentum persists. Mirrors MaxRiderV1's quiet-VOLUME stop-substitute — the same "quiet tape carries the
edge" asymmetry, now on the long-momentum side. (Thin-n curiosity: partial recovery at 1500-4k
prints/min — the meme-crowd regime; n=221, unverified.) **Do NOT raise TcFloor60 — it would cut the
best cells.**

Artifacts: same trips_60d_chan parquet.

### F13b — the completed ladder: vol PEAKS at k=10 (+0.47%, PF 2.38); tc peaks at k=1; the two are complementary

Re-run with the k=5/10 columns (identical book — 658,855 trips, PF 1.204 — only the new columns
populated).

⚠ **CONDITIONING (read before citing): every cell below is `rvol_0945_honest >= 10` (stocks in play)
AND inside the [20,40)bp vol band** (the engine's hard gate at the time of this run — the 7bp floor
came later, F14c), z≥2 per cell, channel exits. The k=10/k=1 peaks are facts about IN-PLAY, WITHIN-BAND
names; the full band-gated sample without the rvol filter showed NO z separation at any k (F13). The
ladder has NOT been measured on the 7-20bp slice the F14c floor drop re-admits — re-measure on the
2023→2026 run.

| k | z_vol ret% | n | PF | z_tc ret% | n | PF |
|---|---|---|---|---|---|---|
| 1 | +0.200 | 454 | 1.516 | **+0.378** | 1,294 | **2.113** |
| 5 | +0.300 | 512 | 1.867 | +0.358 | 1,279 | 2.043 |
| **10** | **+0.467** | 497 | **2.379** | +0.351 | 944 | 1.951 |
| 15 | +0.346 | 441 | 1.938 | +0.337 | 822 | 1.890 |
| 30 | +0.358 | 400 | 1.898 | +0.290 | 622 | 1.715 |
| 60 | +0.095 | 374 | 1.206 | +0.226 | 464 | 1.504 |

**The two champions overlap only partially (225 of ~1,570) and COMBINE:**

| cell (in-play) | n | ret% | PF |
|---|---|---|---|
| z_vol_10≥2 only | 272 | +0.436 | 2.384 |
| z_tc_1≥2 only | 1,069 | +0.351 | 2.052 |
| BOTH ≥2 | 225 | **+0.505** | 2.373 |
| **EITHER ≥2** | **1,566** | +0.388 | 2.157 |

**Readings:** (1) volume acceleration is a ~10-second phenomenon (sharp peak; half the effect gone by
1s — single-bar volume is too noisy — and dead by 1m); trade-count acceleration is fastest of all,
peaking at the single second. (2) The EITHER-cell is the working entry-signal candidate: ~26 trips/day
at +0.39%/PF 2.16, built from two partially-independent tells. (3) All cells are 60-day thin (n
225-1,600) — the 2023→2026 run must confirm before anything is believed.

Artifacts: `trips_60d_chan2/*.parquet` (session scratchpad).

---

## Finding 14 — ⭐ session-high entries: a CONDITIONAL FLIP — worst in the broad universe, BEST in-play (+0.56%, PF 2.71, win 49.8%)

**Question (user, ahead of the aux-exit design):** stocks breaking out to session highs — special case?
Do those entries have better expectancy? `breach_sess = 0` at the signal = the signal bar broke the
session high. Channel-exit run (trips_60d_chan2):

| cell | n | win% | ret% | PF |
|---|---|---|---|---|
| ALL: at session high | 70,225 | 41.2 | +0.0232 | 1.114 |
| ALL: recent high (≤300b) | 38,151 | 39.4 | +0.0294 | 1.150 |
| ALL: far/never | 550,479 | 41.3 | +0.0414 | 1.220 |
| **in-play (rvol≥10): at sess high** | **556** | **49.8** | **+0.5554** | **2.710** |
| in-play: not at sess high | 6,695 | 43.4 | +0.2632 | 1.848 |
| in-play × EITHER-z × at sess high | 168 | — | +0.5713 | 2.564 |

**Reading:** on an ordinary band-gated name, the session-high breakout is where the fade lives (worst
cell of the three). On a stock IN PLAY it is the strongest continuation signal measured — the long-side
rhyme of MaxRiderV1's don't-fade-the-session-high. **No defensive special-casing needed: breach_sess=0
is a positive conditioning flag in-play, already recorded.** (The win-rate jump to ~50% also partially
pre-answers the aux-exit motivation.) tc≤300 as a default gate: deferred until the long run (user).

Artifacts: same trips_60d_chan2 parquet.

### F14b — the F14 cell WITHOUT the vol band (user question): the 40bp CEILING is what makes the cell work; the 20bp floor may be too high in-play

All channel-exit runs since F11 carry the [20,40)bp band as a hard gate — F14's numbers are
within-band. Unbanded re-run (`--min-vol-20m 0 --max-vol-20m 1e9`; 3,373,627 trips, PF 1.137 vs the
banded 1.204). The in-play × session-high cell by vol region:

| vol_20m region | n | win% | ret% | PF |
|---|---|---|---|---|
| <7bp | 55 | 58.2 | +0.018 | 1.666 |
| 7-20bp | 59 | 50.8 | **+0.641** | **3.607** |
| **20-40bp (the band)** | 556 | 49.8 | +0.555 | 2.710 |
| **≥40bp** | **4,699** | **35.9** | **−0.344** | **0.817** |

In-play (any breach state): <7bp 1.526 (n=677) · 7-20bp **2.566** (n=484) · band 1.917 (n=7,251) ·
≥40bp 1.048 (n=96,800).

**Readings:** (1) **the ceiling is load-bearing:** 87% of unbanded in-play session-high breakouts
happen in the ≥40bp blown-off region and LOSE (PF 0.82, win 36%) — without the band, F14's flagship
cell drowns net-negative. The band didn't ride along; it *created* the cell. (2) **the 20bp floor may
be too high for the in-play book:** the 7-20bp slice it excludes runs PF 2.6-3.6 in-play (n=59-484 —
THIN; the F10 p20 floor was calibrated on the whole universe, not in-play). Candidate adjustment for
the long run: keep the 40bp ceiling hard, drop the floor to ~7bp, and re-measure the in-play cells.

Artifacts: `trips_60d_noband/*.parquet` (session scratchpad).

### F14c — the cell's anatomy (user questions) + ⭐ regime-dependent exits: in the blowoff region the aux profit-take FLIPS the sign

**What the F14/F14b cell IS:** `rvol_0945_honest >= 10 AND breach_sess = 0` at the signal, channel
exit, 60-bar channels — **NO z conditions**. The z overlay (EITHER z_vol_10≥2 / z_tc_1≥2), unbanded run:

| vol region × z≥2 | n | ret% | PF |
|---|---|---|---|
| 7-20bp, no z | 40 | +0.345 | 2.076 |
| 7-20bp, z≥2 | 19 | +1.263 | 15.31 (lottery-ticket n) |
| band, no z | 388 | +0.549 | 2.786 |
| band, z≥2 | 168 | +0.571 | 2.564 |
| ≥40bp, no z | 2,748 | −0.458 | 0.743 |
| ≥40bp, z≥2 | 1,951 | −0.185 | 0.908 |

Inside the band the sess-high cell is z-AGNOSTIC (the session-high breach subsumes the acceleration
signal); z only softens the ≥40bp losses without flipping them.

**Not-at-session-high (in-play), by vol region:** <7bp 1.509 (n=622) · 7-20bp **2.283** (+0.161,
n=425) · band 1.848 (+0.263, n=6,695) · ≥40bp 1.073 (+0.064, n=92,101). The extreme-vol carnage is
SPECIFIC to session-high chasing (−0.344%); ordinary breakouts in blowoff are merely breakeven.

**⭐ The user's regime hypothesis — aux exits when vol is EXTREME (≥40bp):**

| ≥40bp cell | book | win% | ret% | PF |
|---|---|---|---|---|
| sess-high | channel | 35.9 | −0.344 | 0.817 |
| sess-high | **aux (any)** | **71.8** | **+0.025** | **1.046** |
| not sess-high | channel | 38.0 | +0.064 | 1.073 |
| not sess-high | aux 20m | 45.6 | +0.112 | 1.155 |

**Readings:** (1) confirmed — in the blowoff regime the F15 verdict INVERTS: taking the first new high
rescues the sess-high cell from −0.34% to +0.03% (win 72%) and mildly improves the rest. There is no
tail to protect above 40bp — the pop IS the trade. (2) Still ≈ breakeven after costs → the ceiling
(don't enter) remains the right default for the LONG book; the regime-dependent exit matters if the
≥40bp region is ever traded — and PF 0.74-0.82 channel-exit there is PlungeRider material for later.
(3) **ENGINE CHANGE: MinVol20m default 0.0020 → 0.0007** (the F14b floor drop, user-confirmed).

Artifacts: same trips_60d_noband parquet.

---

## Finding 15 — ⭐ the aux-exit tournament: profit-taking at {2,5,10,20}m highs LIFTS win rate and DESTROYS expectancy in every cell — the trailing channel stands

**Question (user):** win rate is lowish (41-46%) — add auxiliary profit-take exits at the 2m/5m/10m/20m
highs alongside the trailing channel. Engine: AUX-HIGH MARKS added (first new {120,300,600,1200}-bar
high strictly after the entry fill, marked at the FOLLOWING bar's vwap — px + sec recorded), so every
aux book is a CASE expression: `ret = aux_px/entry−1 if aux_sec <= exit_sec else ret_exit`. Run
reproduces the F12 book exactly (658,855 trips / PF 1.204). Aux-before-exit rates: 2m 82.5%, 5m 66.0%,
10m 49.8%, 20m 33.7%.

| cell | book | win% | ret% | PF |
|---|---|---|---|---|
| all | **channel only** | 41.1 | **+0.0388** | **1.204** |
| all | aux 2m | 64.2 | +0.0089 | 1.110 |
| all | aux 20m | 49.3 | +0.0286 | 1.186 |
| in-play (rvol≥10) | **channel only** | 43.9 | **+0.2856** | **1.917** |
| in-play | aux 2m | 60.9 | +0.0440 | 1.294 |
| in-play | aux 10m | 53.6 | +0.0824 | 1.364 |
| in-play | aux 20m | 50.1 | +0.0912 | 1.360 |
| in-play × EITHER-z | **channel only** | 46.3 | **+0.3877** | **2.157** |
| in-play × EITHER-z | aux 20m (best aux) | 54.7 | +0.1102 | 1.466 |
| in-play × sess-high | **channel only** | 49.8 | **+0.5554** | **2.710** |
| in-play × sess-high | aux (any window) | 65.5 | +0.0734 | 1.596 |

**Readings:**
1. **The win rate is cosmetic; the expectancy is 3-8× worse in every cell.** The entries fire at
   60-bar highs of moving names — the next N-minute high arrives almost immediately ON THE WINNERS
   (82.5% of trips print the 2m high before the channel break), so the aux caps every good trip at a
   sliver while the losers still ride the full channel distance down. Cut-winners/keep-losers.
2. The MaxFlyer lesson ("every EXIT loses to hold; selection ≠ exit") reproduces INTRA-SCALP: the
   right tail IS the product; profit targets amputate it. The trailing channel already banks the tail.
3. In the sess-high cell all four aux windows produce the IDENTICAL book (+0.0734/1.596) — at a
   session high every window's max coincides, so the first new high is a new high of all of them.
   Internal consistency check passed.
4. **Decision: no aux exits. The trailing channel remains the sole exit.** If win rate matters for
   sizing psychology, the lever is entry selection (sess-high in-play cell: win 49.8% at FULL
   expectancy), not exit truncation. The aux marks stay recorded — they cost nothing and other exit
   ideas (e.g. aux-high STOPS-to-breakeven) can be prototyped from the same columns.

Artifacts: `trips_60d_aux/*.parquet` (session scratchpad; supersedes trips_60d_chan2 — same book +
aux/k=5/10 columns).

---

## Finding 17 — ⭐ the exit-channel sweep: 2m trailing is the flagship-cell sweet spot (+0.81%/trip at the same PF); ≥5m bleeds; 20m gives everything back

**Question (user):** instead of the 1m (60-bar) trailing exit low, would 2m or more be better?
Path-dependent → four engine runs, exit ∈ {60,120,300,1200} bars, entry fixed at 60, band [7,40)bp
(the new default — trip count jumps to 2,390,478/run from the 7-20bp re-admission). ⭐ The entry rule
is identical across runs, so all four books contain the SAME 2,390,478 trips — a PAIRED comparison.

**Overall / in-play / in-play × sess-high:**

| exit | win% | ret% | PF | med hold | | in-play: win% | ret% | PF | | sess-hi: win% | ret% | PF |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 60 | 40.9 | +0.020 | **1.167** | 75 | | **43.9** | +0.281 | **1.936** | | 49.9 | +0.564 | **2.777** |
| **120** | 39.0 | +0.027 | 1.156 | 154 | | 41.2 | +0.302 | 1.715 | | **52.4** | **+0.814** | 2.756 |
| 300 | 36.5 | +0.036 | 1.134 | 376 | | 36.3 | +0.316 | 1.484 | | 35.6 | +0.635 | 1.753 |
| 1200 | 35.5 | +0.069 | 1.150 | 1327 | | 30.6 | +0.080 | 1.066 | | 43.7 | +0.109 | 1.072 |

**Readings:**
1. **⭐ In the flagship cell (in-play × sess-high, n=615) the 2m exit is strictly better than 1m:**
   +0.814%/trip vs +0.564% (+44%) at essentially the SAME PF (2.756 vs 2.777) and a HIGHER win rate
   (52.4%). The extra minute of room lets the session-high runners complete a second leg.
2. **For the broad in-play book, 1m keeps the best PF** (1.94 vs 1.72) though 2m earns slightly more
   per trip — the marginal in-play names don't sustain second legs.
3. **≥5m bleeds and 20m is death in-play** (+0.080%/PF 1.066 — everything the spike gave, the 20m
   trailing stop hands back). The scalp thesis re-confirms from a third direction (z-exits F12, MOC
   F11, now wide trailing stops): these names spike and RETRACE.
4. Practical: exit window becomes a per-book knob — 120 for the sess-high tier, 60 for the rest.
   ⚠ mc=1 capacity: 120 doubles median holds (178 bars in-play). ⚠ Engine runtime: the exit=1200 run
   ran ~25 min (holds ≈ nothing exits → the active list balloons — O(bars × open)); wide-exit sweeps
   are expensive at mc=0.

Artifacts: `trips_60d_exit{60,120,300,1200}/*.parquet` (session scratchpad).

### F17b — the z tier under the sweep (user question): NOT-at-high z-breakouts want the FAST exit; the three-tier exit map

**In-play × EITHER-z (z_vol_10≥2 OR z_tc_1≥2) × NOT at session high** (n=1,497, paired across exits):

| exit | win% | ret% | PF | med hold |
|---|---|---|---|---|
| **60 (1m)** | 45.0 | +0.355 | **2.103** | 100 |
| 120 (2m) | 42.8 | +0.372 | 1.877 | 188 |
| 300 (5m) | 39.0 | **+0.477** | 1.748 | 405 |
| 1200 (20m) | 32.5 | +0.476 | 1.389 | 1408 |

**z AND sess-high** (n=187): 1m is best outright — +0.642 / PF 2.903; wider only decays (2m 2.414,
5m 1.811, 20m 1.221).

**Readings:**
1. **The opposite of the sess-high tier:** for z-selected breakouts NOT at the session high, the 1m
   exit keeps the best PF; widening buys per-trip return only by paying PF away (5m: +0.48% at 1.75).
   The sess-high runner's second leg came free (F17: 2m = +44% ret at unchanged PF); the local
   z-breakout's does not — bank the leg.
2. The z tier degrades far more GRACEFULLY under wide exits than the broad in-play book (20m: PF 1.39
   vs the book's 1.07) — acceleration-selected momentum is genuinely more persistent; it just doesn't
   pay enough extra to justify the wait.
3. **⭐ THE THREE-TIER EXIT MAP (the working spec going into the long run):**
   - in-play × sess-high → **2m exit** (+0.81% / PF 2.76, n=615)
   - in-play × EITHER-z, not at high → **1m exit** (+0.36% / PF 2.10, n=1,497)
   - everything else in-play → **1m exit** (+0.28% / PF 1.94 baseline)
   All 60-day-thin; tier boundaries are post-hoc structure the 2023→2026 run must confirm.

Artifacts: same sweep parquets.

---

## Finding 18 — tc≤300 (quiet tape) on top of the tiers: helps EVERYWHERE robustly; the sess-high × quiet headline (PF 14) is 18 ticker-days / 89% top-3 concentration — record, don't believe

**Question (user):** add the F13 quiet-tape filter (tc_60 ≤ 300) on top of the three tiers. Paired
across the exit sweep, in-play:

| tier | exit | quiet? | n | win% | ret% | PF |
|---|---|---|---|---|---|---|
| rest | 60 | no | 2,205 | 41.1 | +0.123 | 1.415 |
| rest | 60 | **yes** | 3,418 | 44.2 | **+0.301** | **2.034** |
| z-not-high | 60 | no | 820 | 42.9 | +0.232 | 1.674 |
| z-not-high | 60 | **yes** | 677 | 47.4 | **+0.504** | **2.710** |
| sess-high | 120 | no | 476 | 46.2 | +0.076 | 1.144 |
| sess-high | 120 | **yes** | 139 | 73.4 | +3.340 | 14.24 ⚠ |

(Quiet also rescues wide exits everywhere — e.g. z-not-high × quiet at 20m: +1.02%/PF 1.95 vs the
busy side's 1.02 — quiet-tape momentum persists.)

**⚠ THE PF-14 CELL'S ANATOMY (checked before believing, per the protocol):** 139 trips = **7 symbols /
18 ticker-days**; top ticker-day (SRXH 2026-06-16: 27 trips at +13.3% avg) ≈ 77% of the P&L, top 3
ticker-days = **88.7%**. Median entry $9.58 / dv_0945 $43M — mechanically real, NOT penny junk, but
this is one runaway runner pseudo-replicated at mc=0 (the DonchianScalp lesson: one day ≈ the whole
P&L → regime artifact until proven otherwise).

**Readings:**
1. **The quiet-tape inversion (F13) generalizes to every tier and every exit** — the two broad cells
   (rest: 1.42→2.03 on n=3,418; z-not-high: 1.67→2.71 on n=677) have real breadth and survive scrutiny.
   The tc≤300 default (user: decide after the long run) looks increasingly justified.
2. Also notable: quiet × sess-high separates BUSY sess-high entries as the weak ones (PF 1.14 at 2m) —
   the crowd chasing a session high on heavy tape is the fade; the quiet grind to a high is the runner.
3. The sess-high × quiet cell: RECORDED as the most promising cell in the study AND the least
   believable — 18 ticker-days. The 2023→2026 run is the only arbiter.

Artifacts: same sweep parquets.

---

## Finding 19 — chg_1d (gain vs yesterday's close at entry): the HYPOTHESIS INVERTS — mid-gainers (10-60%) are the fade zone; the best broad host is in-play names DOWN on the day

**Question (user):** are these momentum plays better on large % gainers? `chg_1d = entry_px /
prev_adj_close − 1` (gap + intraday as of entry — derivable post-hoc, no engine change). In-play,
1m exit:

| chg_1d at entry | n | win% | ret% | PF |
|---|---|---|---|---|
| **down (<0)** | 3,582 | 47.3 | **+0.433** | **2.628** |
| 0-10% | 1,610 | 46.8 | +0.286 | 1.874 |
| 10-30% | 596 | 38.9 | +0.075 | 1.207 |
| **30-60%** | 814 | 34.0 | **−0.075** | **0.771** |
| 60-100% | 1,018 | 34.8 | +0.043 | 1.128 |
| >100% | 115 | 74.8 | +1.215 | 7.678 ⚠ |

**Concentration audit (the F18 discipline):** down = 65 ticker-days, top-3 = 66.5% of P&L (fat-tailed
but has breadth) · **>100% = 2 ticker-days (discard)** · sess-high × 10-60% (+1.16/PF 6.05, n=66) =
**4 ticker-days, top-3 > 100% (discard)**.

**By tier × gain zone (lo <10% incl. down / mid 10-60% / hi >60%), in-play, 1m exit:**

| tier | gain | n | ret% | PF |
|---|---|---|---|---|
| rest | lo | 3,961 | +0.333 | 2.199 |
| rest | mid | 1,066 | −0.056 | 0.832 |
| rest | hi | 596 | +0.065 | 1.200 |
| z-not-high | lo | 867 | +0.479 | 2.535 |
| z-not-high | mid | 278 | −0.120 | 0.706 |
| **z-not-high** | **hi** | **352** | **+0.425** | **2.525** |
| sess-high | lo | 364 | +0.754 | 3.558 |
| sess-high | mid | 66 | +1.162 | 6.051 ⚠ 4 tkdays — discard |
| sess-high | hi | 185 | −0.025 | 0.937 |

Every tier's MID zone is the worst broad region (rest 0.83, z-not-high 0.71); the z tier alone stays
strong on hi (2.53, n=352 — a >60% runner that is STILL accelerating is a different animal from a
stale one drifting mid-extension). sess-high × hi is weak (0.94) — chasing a blown-out runner to
fresh session highs adds nothing even in-play.

**Readings:**
1. **Large gainers are NOT better hosts — the 10-60% zone is where breakout longs go to die** (the
   only broadly-negative chg_1d region; win rate collapses to 34%). Chasing yesterday's runner
   mid-extension is the fade.
2. **The best broad cell is in-play names DOWN vs yesterday's close** (+0.43%/PF 2.63, 65 tkdays):
   heavy-volume selloff names reclaiming to 60-bar highs — the gap-down-bounce/reclaim structure. The
   MR-recovery asymmetry ("LONG BUYS WEAKNESS") reappears inside the momentum system itself.
3. The >100% lottery cells are 2-4 ticker-days — recorded, discarded.
4. ⏭ chg_1d joins the long-run conditioning axes (down/flat vs mid-gainer especially).

Artifacts: same sweep parquets.

---

## Finding 20 — the fine grid {30,45,60}×{30,45,60} on the in-play universe: entry plateaus at 45-60 (30 dilutes); exit 45≈60 > 30 overall — but the sess-high tier LOVES the 30s exit (PF 4.0)

**Setup (user):** does the tighter-is-better gradient extend below 1m? 3×3 entry×exit grid; NEW
`--min-rvol-0945 10` universe pre-filter (318 candidates vs 7,566 — ~24×; each run seconds). 45-bar
channels added to the engine set. All cells are the in-play universe by construction.

**The full grid (paired across exits within an entry; entries change the trip population):**

| entry | exit | n | win% | ret% | PF | med hold |
|---|---|---|---|---|---|---|
| 30 | 30 | 10,456 | 44.4 | +0.175 | 1.805 | 48 |
| 30 | 45 | 10,456 | 43.8 | +0.233 | 1.913 | 70 |
| 30 | 60 | 10,456 | 43.3 | +0.250 | 1.879 | 91 |
| 45 | 30 | 8,663 | 45.1 | +0.192 | 1.862 | 49 |
| **45** | **45** | 8,663 | 44.4 | +0.256 | **1.972** | 71 |
| 45 | 60 | 8,663 | 43.9 | +0.276 | 1.945 | 92 |
| 60 | 30 | 7,735 | 45.2 | +0.193 | 1.845 | 50 |
| 60 | 45 | 7,735 | 44.0 | +0.257 | 1.946 | 71 |
| 60 | 60 | 7,735 | 43.9 | +0.282 | 1.936 | 92 |

**The sess-high tier is IDENTICAL across entry windows** (a session-high breach breaches every
shorter channel, so the same 615 signal bars fire regardless — mechanical identity, consistency check
passed). Its exit profile, now spanning 30→1200 (with F17):

| exit | 30 | 45 | 60 | 120 | 300 | 1200 |
|---|---|---|---|---|---|---|
| ret% | +0.582 | +0.538 | +0.564 | **+0.814** | +0.635 | +0.109 |
| PF | **4.008** | 2.975 | 2.777 | 2.756 | 1.753 | 1.072 |

**Readings:**
1. **The F9-D gradient does NOT extend below 1m on the entry side:** 30-bar entries add ~35% more
   trips at LOWER quality (dilution — every minor uptick is a "breakout"); 45 ≈ 60 within noise
   (45×45 is the nominal PF peak, 1.972 vs 1.936). No default change — 60 stands.
2. **Exit side, overall book: 45 ≈ 60 > 30** — the 30s trailing stop banks too early (+0.19 vs +0.28)
   AND loses PF. The 1m default stands for the broad book.
3. **⭐ But the sess-high tier's exit profile is bimodal: PF peaks at the 30s exit (4.008, +0.582%)
   while ret peaks at 2m (+0.814%, PF 2.76).** The strongest entries tolerate BOTH a razor stop (they
   rarely pull back 30s-deep before running — hence PF 4) and a wide one (the second leg). The choice
   is a risk-preference knob, and at PF 4 the 30s exit halves the loss tail — likely the better mc=1
   book. Long-run confirmation required as always (n=615, 60d).

Artifacts: `grid_e{30,45,60}_x{30,45,60}/*.parquet` (session scratchpad).

### F20b — the z tier on the fine grid (user question): the razor stop is SPECIFIC to session-high entries; z-breakouts want the full 1m

**z tier (z_vol_10≥2 OR z_tc_1≥2, NOT at session high) across the grid:**

| entry | exit | n | win% | ret% | PF |
|---|---|---|---|---|---|
| 30 | 30 | 1,834 | 45.0 | +0.203 | 1.796 |
| 30 | 60 | 1,834 | 44.4 | +0.319 | 2.026 |
| 45 | 30 | 1,610 | 46.0 | +0.223 | 1.859 |
| **45** | **60** | 1,610 | 45.2 | **+0.352** | **2.114** |
| 60 | 30 | 1,497 | 45.9 | +0.222 | 1.843 |
| 60 | 60 | 1,497 | 45.0 | +0.355 | 2.103 |

(45-bar exits sit between; F17b: 120 declines to 1.877 — the z tier's exit peak is AT 60.)

**z AND sess-high (n=187, e=60):** exit 30 → +0.564 / PF **3.222** · 45 → 2.689 · 60 → +0.642 / 2.903.

**The 20s/25s probe (user, expecting noise — confirmed): the 30s peak is INTERIOR.** Sess-high exit
curve, 20s→20m (same 615 trips throughout):

| exit | 20 | 25 | **30** | 45 | 60 | 120 | 300 | 1200 |
|---|---|---|---|---|---|---|---|---|
| PF | 3.379 | 3.818 | **4.008** | 2.975 | 2.777 | 2.756 | 1.753 | 1.072 |
| ret% | +0.442 | +0.515 | **+0.582** | +0.538 | +0.564 | +0.814 | +0.635 | +0.109 |

PF rises smoothly into 30s and falls smoothly away — an interior hump, not a boundary artifact —
and ret degrades monotonically below 30s (the sub-30s wiggle is noise; the stop sells it). ⚠ Still
one 615-trip sample: 60 days cannot separate PF 4.0 from 3.0 — the SHAPE is credible, the LEVEL is
not. Convergence note: F2 found ~30s as the optimal vwap-path SAMPLING scale (finer = microstructure
poison); the exit geometry independently lands on ~30s as the optimal STOP-DISTANCE scale — the same
noise floor measured two ways. 20/25-bar channels stay in the engine set.

**Readings:**
1. **Exit gradients INVERT between tiers:** sess-high PF rises monotonically as the stop TIGHTENS
   (4.01 at 30s); the z-not-high tier rises as it WIDENS to 1m (1.84 → 2.10) and only declines beyond.
   Mechanical story: a session-high entry has NO overhead supply — pullbacks are shallow, so a razor
   stop cuts only real failures; a z-breakout below the high fights overhead structure — it wiggles
   30-45s deep before continuing, and the tight stop sells those wiggles.
2. Entry side: 45 ≈ 60 > 30 in this tier too (45×60 nominal best, 2.114 — within noise of 60×60).
3. z × sess-high inherits the sess-high shape (tight stop → PF 3.2) — the breach state, not the z,
   decides the exit's geometry.
4. ⭐ The A+ tier map after the grid: **sess-high entries → 30s razor exit (PF 4.0); z-accel
   non-high → 1m exit (PF 2.1); rest of in-play → 1m (PF 1.9).** All still 60-day evidence.

Artifacts: same grid parquets.

---

## Finding 21 — the eff_20m (trendiness / drift-t-stat) breakdown: a REAL axis with TIER-DEPENDENT SIGN — coiled-spring at the highs, continuation off them

**Question (user):** breakdown on the F8 efficiency measure (`eff_20m` = |net|/Σ|r| over 40 slots;
t-stat ≈ 5.05·eff; random-walk null E[eff] ≈ 0.158). In-play universe, 1m exit:

| eff_20m | n | win% | ret% | PF |
|---|---|---|---|---|
| <0.10 (chop) | 2,620 | 40.0 | +0.204 | 1.635 |
| 0.10-0.16 (~null) | 1,094 | 44.1 | +0.331 | 2.120 |
| 0.16-0.25 | 1,413 | 43.4 | +0.229 | 1.762 |
| **0.25-0.40** | 1,474 | **51.4** | +0.387 | **2.559** |
| ≥0.40 (hard trend) | 551 | 39.2 | +0.327 | 1.872 |

Overall: moderate trendiness best (t ≈ 1.3-2.0); hard-trended (t ≥ 2) rolls over — the move is late.

**By tier (each at its preferred exit) — the sign FLIPS:**

⚠ **CORRECTED (F21c): the original table's `CASE ... ELSE 'hi'` swallowed NULL-eff trips (vol-block
warm-up entries) into the hi bucket** — DuckDB `NULL < x` → NULL → ELSE. Clean numbers (NULL excluded;
verified against the signed-eff re-run):

| tier | eff lo (<0.16) | eff mid | eff hi (≥0.40) |
|---|---|---|---|
| sess-high @30s exit | **+1.307 / PF 17.5 / n=79 ⚠** | +0.736 / 5.109 / n=267 | **−0.166 / 0.540 / n=103** |
| z-not-high @1m | +0.302 / 1.886 / n=809 | +0.325 / 2.128 / n=531 | **+0.981 / 3.567 / n=83** |
| rest @1m | +0.195 / 1.628 / n=2,826 | +0.258 / 1.949 / n=2,089 | +0.427 / 2.578 / n=365 |

The correction SHARPENS every reading: hard-trended sess-high entries are outright NEGATIVE (0.54, not
the reported 1.89 — exhaustion, unmasked), and the z/rest hi cells are stronger than reported (the
NULL dilution had dragged them down).

**⚠ Concentration audit of the PF-17.5 corner:** 79 trips = 6 symbols / 10 ticker-days, top-3 = 80%
of P&L (WOK/SRXH again — the F18 suspects). LEVEL = noise. But the DIRECTION repeats independently
across three tiers, which is harder to fake:

**Readings:**
1. **Session-high breakouts want LOW prior trendiness — the coiled spring.** A name at its session
   high whose last 20m was a tight CHOP (eff < 0.16) explodes on the break; one that trended all the
   way up (eff ≥ 0.40) is exhausted at the high (PF 1.89, the tier's worst). The consolidation-
   breakout pattern — dead on 1m crypto (2026-05-09, shelved) — shows up alive at 1s on in-play
   equities, in exactly the state (at the highs, post-consolidation) where the playbooks put it.
2. **Off-high z-breakouts want HIGH trendiness — continuation, not reversal** (PF 3.14 at eff ≥ 0.40):
   an accelerating push inside an established trend leg continues; the same push in chop is just chop.
3. The F8 design intent (eff as the ORTHOGONAL state axis, ρ vs vol 0.024) pays off exactly as hoped:
   it doesn't predict returns alone (F8: ρ≈0.06) but SPLITS the tiers' geometry.
4. All of it: 60-day, in-play, concentrated cells — the long run arbitrates. eff bucket boundaries
   worth carrying: {0.16 (the null), 0.40}.

Artifacts: same grid parquets.

### F21b — eff-hi split by 20m DIRECTION (user catch: eff is |net|/Σ|r|, direction-BLIND): continuation needs z; without z the money is the smooth-DECLINE reversal

Direction proxy from the recorded counters: 20m trended UP if the 1200-bar HIGH breach is fresher
than the 20m LOW (`breach_1200 < bars_since_low_1200`; −1 sentinels → +inf). In-play, 1m exit:

| tier | eff | dir | n | win% | ret% | PF |
|---|---|---|---|---|---|---|
| z-not-high | hi | **up (continuation)** | **36** | 36.1 | **+1.905** | **5.113** ⚠ dust |
| z-not-high | hi | down (reversal) | 47 | 44.7 | +0.273 | 1.853 |
| z-not-high | lo | up | 421 | 47.5 | +0.480 | 2.317 |
| z-not-high | lo | down | 388 | 37.4 | +0.109 | 1.345 |
| rest | hi | up (continuation) | 105 | — | **−0.070** | **0.866** |
| **rest** | **hi** | **down (reversal)** | **260** | — | **+0.628** | **4.736** |
| rest | lo | up | 1,234 | — | +0.405 | 2.149 |
| rest | lo | down | 1,592 | — | +0.031 | 1.113 |

Audit of the rest×hi×down cell: 16 symbols / 28 ticker-days / top-3 = 65% — fat-tailed but the most
BREADTH of any spectacular cell so far.

**Readings:**
1. **The user's catch was load-bearing: the F21 "continuation" story was HALF direction-confounded.**
   With z-acceleration, the up-leg continuation IS the money (+1.9%, PF 5.1 — but n=36, dust). WITHOUT
   z, continuation is the only NEGATIVE eff-hi cell (0.87) — a smooth up-trend breaking its 60-bar
   high on no acceleration is exhaustion, not continuation.
2. **⭐ The rest-tier's hidden gem: the smooth-decline reversal** — 20m of orderly selling, then a
   60-bar-high breakout on no particular acceleration = the decline SNAPPING (+0.63%, PF 4.7, n=260,
   28 tkdays). The F19 "down on the day" echo at the 20m scale, and LowFlyer's "long buys weakness"
   a third time. Weakness-turning-strength is this codebase's most persistent long edge.
3. In lo-eff, direction still matters the SAME way for both tiers (up-chop > down-chop) — chop below
   a fresh low is dead money either way.
4. dir20m is derivable post-hoc from recorded counters (no engine change); bucket boundaries carried
   forward. Long-run confirmation required, as everything today.

Artifacts: same grid parquets.

### F21c — eff SIGNED (engine change) + the verification that caught the F21 bucketing bug

**Engine change (user):** `Eff20m` numerator loses its abs — now `ln(V/V_40ago)/Σ|r|` ∈ [−1,1].
|eff| = trendiness (unchanged semantics); **sign = the 20m net direction**, replacing F21b's
breach-counter proxy with the exact quantity.

**Verification (user-ordered):** re-ran the two F21 configs. (1) All 7,735 trips join 1:1 with
`|eff_new| = eff_old` to 1e-12 and zero differences elsewhere. (2) The true sign agrees with the
F21b counter proxy on **100%** of |eff|≥0.40 trips (307 down + 244 up, 0 off-diagonal); the F21b
reversal split reproduces exactly. (3) The F21 TIER TABLE did NOT reproduce — which exposed the
NULL-into-hi CASE bug corrected above. The signed re-run is now the canonical eff data
(`grid_se60_x{30,60}`); NULL eff (≈8% of trips, vol-block warm-up) is its own bucket everywhere.

Artifacts: `grid_se60_x30`, `grid_se60_x60` (session scratchpad).

## Finding 22 — eff_10m (20-slot twin, new engine feature): NOT a replacement for eff_20m — a FRESHNESS confirm; hi-on-both = the live trend, hi-20m-only = the stale one (PF 0.76-1.68)

**Setup (user, 2026-07-24):** "20m would be a pretty long trend in the market" — added signed
`eff_10m` (`ln(V/V_20slots_ago)/Σ20|r|`, same slot stream, half the horizon) to the engine and re-ran
the two canonical configs (60d, in-play, band; totals reproduce F20 exactly, so the change is
regression-clean). ⚠ **Scale note:** the random-walk null of |eff| scales as 1/√slots — E≈0.158 at 40
slots but ≈0.224 at 20 — so eff_10m's raw values sit mechanically higher (26.6% of trips ≥0.40 vs
7.1% for eff_20m). Fair buckets for eff_10m are the F21 edges ×√2: **{0.226, 0.566}** (used below as
"t-adj"). Redundancy: corr(|eff_10m|,|eff_20m|) = **0.44** (signed 0.68) — a genuinely different
feature. NULL warm-up drops 7.5% → 1.9% (20 slots warm in half the time).

**Head-to-head ladders (full in-play @1m):** eff_10m alone is FLATTER — its interior peak is
PF 2.20 (0.25-0.40 raw) vs eff_20m's 2.56, and its lo/mid separation is weaker. As a standalone axis
the 20m version wins.

**Tier table, eff_10m t-adj {0.226, 0.566} (compare F21c's corrected eff_20m table):**

| tier | NULL | eff lo | eff mid | eff hi |
|---|---|---|---|---|
| sess-high @30s | +0.250 / 2.16 / n=64 | +0.859 / 6.98 / n=80 | +1.169 / 9.12 / n=218 | +0.071 / 1.29 / n=253 |
| z-accel @1m | +0.782 / 4.36 / n=16 | +0.327 / 2.00 / n=747 | +0.248 / 1.79 / n=608 | +0.988 / 3.95 / n=126 |
| rest @1m | +0.260 / 2.06 / n=64 | +0.201 / 1.65 / n=2,755 | +0.207 / 1.75 / n=2,336 | +0.526 / 2.83 / n=468 |

Same GEOMETRY as F21 (exhaustion-kill at the highs, continuation reward off them) but every edge is
SOFTER: the sess-high kill is 1.29 vs eff_20m's 0.54, the z-accel hi is 3.95 vs 3.57 with more n.
Direction split (sign, exact): the F21b asymmetry nearly VANISHES at 10m — rest×hi = up 2.60 (n=172)
vs down 3.00 (n=296, **43 tkdays — the most breadth of any spectacular cell yet**); at 20m it was
0.87 vs 4.74. The smooth-decline-reversal read is specifically a **20m**-horizon fact.

**⭐ The cross (the actual finding) — eff_10m is the freshness filter for eff_20m's trend:**

| tier @1m | hi20 × hi10 | hi20 × lo10 (STALE) | lo20 × hi10 | lo20 × lo10 |
|---|---|---|---|---|
| z-accel | **+2.126 / 6.19 / n=40 ⚠** | **−0.085 / 0.76 / n=43** | +0.359 / 2.11 / n=78 | +0.308 / 1.96 / n=1,262 |
| rest | **+0.845 / 4.36 / n=132** | +0.190 / 1.68 / n=233 | +0.359 / 2.18 / n=309 | +0.212 / 1.72 / n=4,606 |

A 20m trend whose last 10m went quiet is a DYING trend — buying its breakout fails (z-accel: PF 0.76,
outright negative) or merely muddles through (rest: 1.68). The same 20m trend still running at 10m is
the live one: 4.36-6.19. At the session high the logic INVERTS into a union-kill — hi on EITHER
horizon poisons the break; chop on BOTH is the coiled spring proper:

**Sess-high @30s exit, eff_20m {hi ≥0.40} × eff_10m t-adj {hi ≥0.566}:**

| eff_20m | eff_10m | n | win% | ret% | PF | tkdays |
|---|---|---|---|---|---|---|
| **lo/mid** | **lo/mid (coiled spring)** | **233** | **64.4** | **+1.304** | **12.88** | 15 ⚠ top-3 73% |
| lo/mid | hi | 113 | 35.4 | −0.036 | 0.858 | 7 |
| hi | lo/mid | 17 | 0.0 | −0.524 | 0.000 | 5 |
| hi | hi | 86 | 14.0 | −0.095 | 0.711 | 8 |

Every cell containing a hi is at-or-below water; the both-quiet cell beats the F21 single-horizon
buckets (5.1-17.5) with the same caveat as always in this tier: 15 tkdays, top-3 = 73% of gross,
LEVEL = noise, shape = the signal.

**Audits:** rest×hi10 t-adj = 56 tkdays / top-3 48% (broadest yet); z-accel hi20×hi10 n=40 = dust
with a loud voice; sess-high cells stay WOK/SRXH-concentrated.

**Verdict:** keep BOTH. eff_20m remains the primary state axis (sharper alone, sharper kill);
eff_10m earns its column as the confirm: off-high trend-following wants **hi on both**, session-high
coiled-spring wants **quiet on both**. Bucket edges to carry for eff_10m: {0.226, 0.566}. All 60-day,
in-play, mc=0 — the long run arbitrates.

**⚠ F23 POSTSCRIPT: the freshness cross did NOT survive the 2023→2026 confirmation** — OOS, hi20×lo10
matches or beats hi20×hi10 in both off-high tiers, and the sess-high union-kill reverses outright.
eff_10m stays a recorded feature; as a LEVER it is dead. See F23.

Artifacts: `grid_se60_x30`, `grid_se60_x60` (regenerated with `eff_10m`), `f22_*.sql` (scratchpad).

## Finding 23 — ⭐⭐ THE 2023→2026 CONFIRMATION RUN: the three-tier map SURVIVES (sess-high PF 2.2-2.4 EVERY year); quiet-tape + fade-zone survive; the eff stories at the session high DIE

**Setup:** the standing arbiter for every F9-F22 claim. Both canonical configs re-run over
**2023-01-01 → 2026-07-17** (in-play `--min-rvol-0945 10`, band [7,40)bp, entry 60; exits 60 and 30):
**262,884 trips / 16,349 ticker-days** (~34× the 60-day sample). Trips now DURABLE:
`data/equity/surgerider/confirm23_e60_x{30,60}/`. "OOS" below = trade_date < 2026-04-22 (excludes the
whole F9-F22 discovery window). All mc=0, no costs.

**Broad sampler by year (x60) — positive every year, no regime collapse:**

| yr | n | win% | ret% | PF | net | tkdays |
|---|---|---|---|---|---|---|
| 2023 | 128,598 | 44.2 | +0.121 | 1.481 | $1.56M | 1,625 |
| 2024 | 64,531 | 47.2 | +0.245 | 1.902 | $1.58M | 1,162 |
| 2025 | 52,249 | 45.0 | +0.227 | 1.729 | $1.18M | 909 |
| 2026 | 17,506 | 41.3 | +0.175 | 1.550 | $0.31M | 265 |

**⭐ The three-tier map, OOS vs the discovery window — ORDERING CONFIRMED, levels compressed ~½:**

| tier | OOS (23→26.04) | 60d window (F17b/F20) |
|---|---|---|
| sess-high @30s | **+0.321 / PF 2.224 / n=22,472 / 1,088 tkd** | +0.581 / 4.008 / n=615 |
| z-accel @1m | +0.187 / 1.665 / n=52,082 / 3,478 tkd | +0.355 / 2.103 / n=1,497 |
| rest @1m | +0.143 / 1.550 / n=180,595 / 3,739 tkd | +0.231 / 1.788 / n=5,623 |

The 60-day window was uniformly rosier (hot recent regime) — the F17b LEVELS were inflated ~2×, the
STRUCTURE is real. And the flagship tier is astonishingly stable by year:

**sess-high @30s razor, by year: PF 2.22 / 2.30 / 2.28 / 2.38** (2023/24/25/26; win 48-55%, ret
+0.27→+0.45). Four years, four indistinguishable readings.

**Lever verdicts (all OOS, per tier):**

| lever | claim (60d) | OOS verdict |
|---|---|---|
| tc_60 ≤ 300 quiet tape (F18) | helps every tier | **✅ CONFIRMED everywhere**: sess 2.55 vs 2.03, z 1.94 vs 1.37, rest 1.66 vs 1.36 (n=9k-118k) |
| chg_1d 10-60% = fade zone (F19) | worst bucket | **✅ CONFIRMED**: worst in every tier (1.38-1.41); ≥60% gainers ALSO dead off-high (1.02-1.04); 0-10% & down-on-day best |
| eff_20m hi = continuation off-high (F21) | hi best off-high | ✅ direction survives, compressed: z 2.01 / rest 1.98 vs lo 1.59/1.40 |
| eff sess-high exhaustion-kill (F21) | hi = PF 0.54 | **❌ DEAD**: sess-high eff-FLAT OOS (lo 2.35 / mid 2.19 / hi 2.17) — the kill was WOK/SRXH noise |
| F21b smooth-decline reversal | down 4.74 vs up 0.87 | **❌ DEAD**: up 1.96-1.97 vs down 2.00-2.07 — sign doesn't matter, only \|eff\| does |
| F22 freshness cross (hi20×hi10) | stale trend fails | **❌ DEAD**: hi20×lo10 ≥ hi20×hi10 in both tiers (z: 2.11 vs 1.90; rest: 1.95 vs 2.02) |
| F22 sess-high union-kill / coiled spring | both-quiet PF 12.9 | **❌ REVERSED**: any-hi 2.36 vs both-quiet 2.03 |

The concentration audits called every casualty in advance: each dead lever's 60-day evidence lived in
10-18 ticker-days of WOK/SRXH. The levers that survived were exactly the ones with 3,000+ tkday
breadth. NULL-eff (vol warm-up = first ~20m after 09:45) is GOOD in every tier (2.0-2.3) — early
entries are fine; a time-of-day breakdown is future work.

**⭐ THE TRADABLE COMPOSITE — sess-high @30s razor + tc_60≤300 + not-10-60%-gainer:**

| yr | n | win% | ret% | PF | tkdays |
|---|---|---|---|---|---|
| 2023 | 4,053 | 54.4 | +0.293 | 2.475 | 370 |
| 2024 | 2,341 | 58.2 | +0.474 | 2.676 | 228 |
| 2025 | 1,395 | 55.1 | +0.579 | 2.988 | 162 |
| 2026 | 299 | 61.5 | +1.084 | 6.858 | 36 |

(2026 = 6.5 months incl. the hot discovery window.) **Concentration: 796 tkdays / 181 symbols /
top-3 = 6.9% of gross / top-20 = 27.4% / 51.3% of ticker-days positive — the FIRST cell of the
campaign to pass every audit at once.** ~9 trips/day average, rising PF across years.

**Verdict:** SurgeRider's skeleton is confirmed 2023→2026: session-high breakouts on in-play,
in-band, quiet-tape names, razor 30s trailing exit, avoid the mid-gainer fade zone. PF ~2.5 mc=0
pre-cost. **Next: mc=1 on the composite + the cost model (spreads on in-play names)** — at +0.29-0.58%
per trip, spread is the entire question.

Artifacts: `data/equity/surgerider/confirm23_e60_x{30,60}/` (durable), `confirm_*.sql` (scratchpad).

## Finding 24 — the breakout-horizon ladder (1m/2m/5m/20m/session): NOT a gradient — the session high is a DISCONTINUITY; interior horizons barely matter

**Question (user):** "session highs are a bit special — is a 1m vs 2m … 20m breakout better?" All
recorded: every trip carries breach counters for the 60/120/300/1200-bar channels + session, and with
entry = 60 the longer breaches nest inside the population. Rung = the LONGEST channel broken at entry
(exclusive tiers). Full 2023→2026 confirmation data, both exits paired on the same 262,884 trips:

| rung (longest broken) | exit | n | win% | ret% | PF |
|---|---|---|---|---|---|
| 1m only (60) | 30s | 73,010 | 45.8 | +0.109 | 1.606 |
| 1m only (60) | 60s | 73,010 | 43.3 | +0.129 | 1.537 |
| 2m (120) | 30s | 68,002 | 46.8 | +0.123 | 1.651 |
| 2m (120) | 60s | 68,002 | 44.3 | +0.145 | 1.578 |
| 5m (300) | 30s | 63,349 | 47.0 | +0.148 | 1.717 |
| 5m (300) | 60s | 63,349 | 45.4 | +0.195 | 1.719 |
| 20m (1200) | 30s | 35,436 | 46.4 | +0.146 | 1.585 |
| 20m (1200) | 60s | 35,436 | 44.7 | +0.162 | 1.484 |
| **session** | **30s** | **23,087** | **52.3** | **+0.328** | **2.259** |
| session | 60s | 23,087 | 50.7 | +0.386 | 2.105 |

PF creeps 1.61 → 1.72 from 1m to 5m, DIPS at 20m, then JUMPS at session. Not a gradient — a step.

**Time-of-day control** (is "session" just "early, when session high ≈ 20m high"?): NO — the session
rung wins BOTH halves of the day (am 2.514 / pm 2.099; every other rung: am 1.6-2.1, pm 1.48-1.58).
Within the pm alone the interior ladder is essentially FLAT (1.48→1.58 for 1m→20m) and session still
steps to 2.10. ⚠ Composition caveat: rung-20m is pm-heavy by construction (the 1200-bar channel needs
~20m of present bars to warm), which explains most of its "dip" vs 5m — but not the session step.

**By year (30s exit):** session = 2.22 / 2.30 / 2.28 / 2.38 — the ONLY rung that is both top and
stable; interior rungs shuffle (5m spans 1.44-2.02, 20m 1.27-1.97) with no persistent identity.

**Reading — overhead supply is the mechanism, and it's binary, not graded.** Below the session high
there exist intraday buyers who are underwater and sell into the breakout; above it there are none.
How far below (1m vs 20m of consolidation) barely matters — what matters is whether the overhead
cohort EXISTS. This is why the razor exit works there (F17: shallow pullbacks) and why the tier was
entry-window invariant (F20). The session high isn't the top rung of a ladder; it's a different
regime. (Longer-horizon highs — multi-day/52-week, the classic Qullamaggie territory — are the
natural next question; needs daily context joined in, future work.)

Artifacts: same confirmation parquets; `breakout_ladder.sql`, `ladder_checks.sql` (scratchpad).

## Finding 25 — the 52-WEEK-high context (multi-timeframe alignment test): the classic story INVERTS — the edge lives in WRECKAGE (<25% of the prior 52w high ≈ sub-$2 names); blue-sky alignment is the WORST host

**Question (user, via Lance Breitstein's multi-timeframe-alignment principle):** is a session-high
breakout better when it aligns with the daily timeframe — near/above the 52-week high? Feature
computed post-hoc, no engine re-run: `high52 = max(adj_high)` over the **prior 252 trading days**
(`ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING` — ⚠ current day EXCLUDED (user), determined at D−1
close, knowability-clean), partitioned by `(ticker, episode)` (daily_episodes — no listing-gap
spans), from `split_adjusted_prices` — same adjustment basis as the trips' `entry_px`
(adj_ratio = adj_close/raw_close), so the ratio `entry_px/high52` is basis-consistent. ⚠ Known
subtractive-dividend bug in that table shifts BOTH legs; residual distortion ≈ dividends paid inside
the window — negligible for this (largely non-paying) universe. Guarded `adj_high > 0`, `nprior ≥ 126`.

**The depth ladder (2023→2026 confirmation trips):**

| entry/high52 | sess-high @30s | off-high @1m |
|---|---|---|
| ≥0.80 (at/above the high) | −0.062 / **PF 0.750** / n=1,185 / 45 tkd | +0.023 / 1.080 / n=12,455 |
| 0.50-0.80 | +0.053 / 1.175 / n=861 | −0.010 / 0.965 / n=11,255 |
| 0.25-0.50 | −0.027 / 0.906 / n=1,398 | +0.050 / 1.177 / n=24,728 |
| **0.10-0.25** | +0.305 / **2.557** / n=8,967 / 343 tkd | +0.145 / 1.657 / n=72,630 |
| **<0.10** | +0.485 / **2.664** / n=9,935 / 559 tkd | +0.214 / 1.761 / n=111,915 |
| short-hist (<6m, IPO-ish) | +0.101 / 1.186 / n=741 | +0.216 / 1.605 / n=6,814 |

Monotone the WRONG way for the alignment story: ~85% of in-play trips sit below 25% of the prior 52w
high, and that's where ALL the edge is. Blue-sky names (≥1.0: PF 0.839, n=772) and near-high names
(0.80-0.95: 0.543) are the tier's worst hosts. The blue-sky bucket is real names, not adjustment junk
(audited: CLEU/BOLD/LODE/CVAC/CRVS… small per-day P&L, mixed signs).

**Price confound (⚠ raw price = entry_px/adj_ratio — the ADJUSTED px overstates traded price for
later reverse-splitters):** depth and cheapness are nearly the SAME coordinate — the two deep buckets
are ~95% sub-$2 names (PF 2.64-2.66 there); within depth ≥0.25 no price band works (0.61-1.49); the
off-diagonal cells are dust (≤18 tkdays). With these n's the two descriptions are inseparable:
**"<25% of prior 52w high" ≈ "sub-$2 catastrophically beaten-down name having a ≥10× volume day."**

**Readings:**
1. **Multi-timeframe alignment does NOT transfer to this system.** The 1s in-play momentum edge is
   hosted by wreckage — busted biotechs/reverse-split candidates squeezing off multi-year lows — not
   by quality names breaking to new highs. The LowFlyer/HighFlyerV2 DNA ("long buys weakness", low
   float, low price) reappears at the 52-week scale. F19 said it intraday (down-on-day best,
   10-60% gainers fade); this says it at the yearly scale.
2. The daily-timeframe momentum books (Qullamaggie-style new-high continuation) and this system want
   OPPOSITE universes — more evidence SurgeRider is an isolated alpha source, not a proxy.
3. **The cost model is now THE question**: sub-$2 names mean the minimum tick and effective spread
   are large relative to +0.3-0.5%/trip. Nothing here is believed until spreads are modeled.
4. Blue-sky in-play names aren't just "no edge" — sess-high 0.75-0.84 hints the FADE side works
   there (PlungeRider material, with F16's ≥40bp blowoff note).

Artifacts: `high52*.sql` (scratchpad); d52 temp construction reusable for any daily-context join.

---

# Appendix A — the four path-RV constructions (F3 companion)

*(What exactly each variant in the F3 overlap study computes. Open in VS Code markdown preview
(Ctrl+Shift+V) for rendered math.)*

**Common to all four:** the final measure is the **sample standard deviation** of a 30-second-return
series over the trailing 20-minute window — that is what "path RV" means (the dispersion of the returns,
not their mean). The variants differ only in **how the return series is built**, and specifically in
*what "the vwap" means at the two endpoints of each return*.

Notation: $\text{vwap}_s$, $\text{vol}_s$ are the stored fields of the 1s bar at second $s$ (present
bars only — seconds with no trades have no bar).

---

## (0) Non-overlapping 30s slots — the F2 winner

Chop the session into fixed 30s slots $[0,30), [30,60), \dots$. The slot vwap aggregates **all trades
in that 30 seconds** (exact, since each 1s bar's $\text{vol}\cdot\text{vwap}$ is that second's dollar
volume):

$$V^{\text{slot}}_s \;=\; \frac{\sum_{j \in \text{slot } s} \text{vol}_j \cdot \text{vwap}_j}{\sum_{j \in \text{slot } s} \text{vol}_j}$$

One return per boundary between consecutive slots:

$$r_s \;=\; \ln\!\big(V^{\text{slot}}_s \,/\, V^{\text{slot}}_{s-1}\big)$$

→ **40 returns per 20m window**; measure = std of those 40. Each endpoint is a 30-second *aggregate*, so
endpoint microstructure noise is averaged down inside each slot.

## (1) Overlapping, wall-clock point endpoints

For every present second $t$ where a bar also exists at exactly $t-30$:

$$r_t \;=\; \ln\!\big(V_t \,/\, V_{t-30}\big)$$

where $V_t$ is the **single 1-second bar's vwap** — one second of trades, a point snapshot. The "30s" is
only the *lag span*; the endpoints are 1s snapshots. → **~1170 returns per 20m**; measure = std of those.

This is what `LagMa(30)` applied to `bar.vwap` computes *when the name trades every second*. The flaw:
each endpoint carries a full second's microstructure noise (a handful of prints straddling the spread),
which contaminates every return — the same poison as the F2 grid's $k=1$ row.

## (2) Overlapping, `LagMa(30)` present-bar endpoints

Identical to (1) except the lag is **30 present bars ago** rather than 30 wall-clock seconds:

$$r_t \;=\; \ln\!\big(V_t \,/\, V_{t'}\big), \qquad t' = \text{the 30th-most-recent present bar before } t$$

For a name trading every second, $t' = t-30$ and this equals (1). For a name active 30% of seconds,
$t - t' \approx 100\text{s}$ — the "30s return" silently becomes a ~100s return. This is the *exact*
engine semantics of `LagMa(30)` on `bar.vwap`. Worst of all four: point-endpoint noise **plus** lag-span
stretch.

## (3) Overlapping, rolling-30s-vwap endpoints — the faithful subsample

Define the **rolling 30s vwap**, recomputed at every present second (engine: `SumMa(30)` of
$w\!\cdot\!v$ and $w$):

$$W_t \;=\; \frac{\sum_{s=t-29}^{t} \text{vol}_s \cdot \text{vwap}_s}{\sum_{s=t-29}^{t} \text{vol}_s}$$

Then:

$$r_t \;=\; \ln\!\big(W_t \,/\, W_{t-30}\big)$$

The numerator's window is $[t-29,\,t]$ and the denominator's is $[t-59,\,t-30]$ — **two adjacent,
non-overlapping 30-second spans**, exactly like one slot-pair of construction (0), but re-evaluated at
every second instead of only at slot boundaries. Each $r_t$ is a genuine "30s-vwap rate of change," and
the ~1170 of them per window are construction (0)'s return computed **at all 30 phase offsets** — clean
aggregated endpoints *and* the phase-averaging (the subsampled-RV idea of Zhang–Mykland–Aït-Sahalia).

⚠ Engine note: to get (3) you must apply `LagMa(30)` to the **rolling $W$**, not to `bar.vwap` —
applying it to `bar.vwap` gives (2), the worst variant.

---

## Scoreboard (F3, 15 days, n = 2.84M, same target)

| construction | endpoints | pooled ρ | xsec ρ | days won |
|---|---|---|---|---|
| **(0) non-overlapping slots** | 30s aggregates | **0.8774** | **0.8579** | **15/15** |
| (1) overlap, wall-clock | 1s snapshots | 0.8652 | 0.8473 | 0 |
| (2) overlap, `LagMa(30)` bars | 1s snapshots | 0.8407 | 0.8142 | 0 |
| (3) overlap, rolling-vwap | 30s aggregates | 0.8724 | 0.8528 | 0 |

**Conclusion:** the overlap idea, correctly constructed as (3), ties (0) to within noise
(rank-redundancy $\rho = 0.9931$) but loses by a consistent $-0.005$ on **all 15 days** — the
phase-averaging gain is real but tiny, and the moving-average smoothing of the endpoints slightly smears
the measure against the 1m-return-vol target. (1) and (2) lose bigger for reasons **orthogonal to
overlap-vs-not**: noisy point endpoints. Decision: construction (0) — non-overlapping 30s slots, 20m
window, ~40 returns, `WinStdMa(40)` over a small slot-builder.

**Untested alternative** (flagged, not run): if "average the returns" meant the **mean of $|r_t|$** (a
mean-absolute-deviation vol estimator, more outlier-robust) rather than the std, that is a different
estimator family and one more cheap run away.

---

# Appendix B — why `vwstd / vwap` ≈ `log_vwstd` (first-order note; D5/F1 background)

*(Companion note to the MomentumV1 plan, decision D5(b). "First order" = keeping only the linear
term of a Taylor expansion and dropping the quadratic-and-beyond terms.)*

## Setup

Within one 1-second bar, trades happen at prices $p_i$ with sizes $w_i$. Write each price relative
to the bar's vwap:

$$p_i = V\,(1 + x_i)$$

where $V$ is the vwap and $x_i$ is the **fractional deviation** of that trade from vwap — e.g.
$x_i = 0.001$ means the print was 0.1% above vwap. Intra-bar, the $x_i$ are tiny: a 1-second bar
spans a few cents on a \$100 stock, so typically $|x_i| \sim 10^{-4}$ to $10^{-3}$.

## The two quantities

**Ratio measure** — dividing by a constant scales a standard deviation, so this is *exact*:

$$\frac{\text{vwstd}}{\text{vwap}} \;=\; \frac{\operatorname{std}_w(p_i)}{V} \;=\; \operatorname{std}_w(x_i)$$

**Log measure** — the constant $\ln V$ drops out of any standard deviation:

$$\text{log\_vwstd} \;=\; \operatorname{std}_w(\ln p_i) \;=\; \operatorname{std}_w\big(\ln V + \ln(1+x_i)\big) \;=\; \operatorname{std}_w\big(\ln(1+x_i)\big)$$

(Throughout, $\operatorname{std}_w$ is the volume-weighted standard deviation.)

## The approximation

The Taylor series of the logarithm:

$$\ln(1+x) \;=\; x \;-\; \frac{x^2}{2} \;+\; \frac{x^3}{3} \;-\; \dots$$

**"To first order" means keeping just the $x$ term:** $\ln(1+x) \approx x$. Under that truncation,

$$\operatorname{std}_w\big(\ln(1+x_i)\big) \;\approx\; \operatorname{std}_w(x_i)
\qquad\Longleftrightarrow\qquad
\text{log\_vwstd} \;\approx\; \frac{\text{vwstd}}{\text{vwap}}$$

The two quantities differ only through the $-\tfrac{x^2}{2}$ and higher terms the truncation discards.

## How big is the discarded part?

The relative error of $\ln(1+x) \approx x$ is about $x/2$. With intra-bar deviations of
$x \sim 10^{-3}$ (a 0.1% intra-second spread — already a wide bar), each log-deviation differs from
its linear version by about **0.05% of itself**. So the two stds agree to roughly **1 part in
2,000** on a wide bar, and far better on a normal one.

For comparison: the bars store these as float32 (~7 significant digits) and the research consumes
them in coarse breakdown buckets. The approximation error is orders of magnitude below anything the
study could resolve.

## Where it would break down

If $x$ were large — say a bar whose trades span $\pm 10\%$ ($x = 0.1$) — the second-order term is
$5\%$ of the first, and the two measures visibly diverge: `log_vwstd` reads systematically
*smaller*, because $\ln(1+x)$ compresses upside deviations more than it expands downside ones. But a
1-second bar with a ±10% internal price range is a halt-reopen or a broken print, not a bar either
statistic should be trusted on.

## The recurring pattern

Same mechanism as the `log_vwap` vs `ln(vwap)` discussion: by Jensen's inequality,

$$\ln\big(\operatorname{mean}(p)\big) \;-\; \operatorname{mean}\big(\ln p\big) \;\approx\; \frac{\sigma^2}{2}$$

— the Jensen gap is a **second-order** quantity in the deviations. *First-order equal,
second-order different* is the recurring pattern with log transforms of tightly-clustered data:

| pair | first-order relation | second-order gap |
|---|---|---|
| $\text{vwstd}/\text{vwap}$ vs $\text{log\_vwstd}$ | equal | $O(x^2)$ skew correction |
| $\ln(\text{vwap})$ vs $\text{log\_vwap}$ | equal | $\sigma^2/2$ (Jensen) |

**Practical conclusion (D5b):** `vwstd/vwap` and `log_vwstd` carry the same information for every
bar that matters at 1-second granularity, so parking `log_vwstd`'s absence costs nothing — the
ratio reconstructs it to well beyond the precision the breakdowns will ever use.
