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
