
---

# LowFader — LowFlyer on the 1s tape (2026-09-04)

The second 1m→1s conversion (after MaxFlyerV2 → MaxFader, retired 2026-09-04). User: LowFlyer
"is quite good and didn't get hurt by the lookahead issue like MaxFlyer; the only thing to
note is that 1d and 3d % change features are gates in it."

## The engine: a fork of FlushFader, the MaxFader edits mirrored

`TradingEdge.LowFader` = FlushFader (the 1s LONG MR engine) verbatim — zero-diff proven,
132 = 132 trips on 312 columns — with: `EntryChannelBars = 0` → the **session channel** (a
new session LOW is the flush breakout; leg reset on a new session HIGH); `ExitChannelBars =
0` → **no target, hold to MOC**; `NextOpenExit` default **OFF** (LowFlyer never holds
overnight; `--next-open` restores FlushFader's S43bw rule); `close_m7/div_m7/avgvol20_prior`
on the row; `vol_60_prior_max` (SpikeFader's S9 volume-high mirror, which FlushFader lacked);
and the one-copy advance loop (235.9 → 89.9 s on the 5-day smoke, byte-identical on 315
columns — the sampler-fold finding from MaxFader §S4a). Smoke invariants: signal below the
prior session low on 33,220/33,220 trips; exit = `moc` on all, zero next-open fills.
Commits `06545b6` → `9983f32`.

## LowFlyer's spec, mapped onto recorded 1s columns (all causal)

| LowFlyer gate (1m) | 1s column / expression | note |
|---|---|---|
| entry-bar flush `close/prevClose ≤ −0.7%` | `flush_1m = signal_vwap / vwap_60_prev − 1` | the 1s analog of the 1m candle |
| flush-depth floor `≥ −12%` | same, floored | |
| intraday log-ATR `< 0.02` | `volat_20m` ceiling | the 1s volatility driver; record-first, banded |
| `vol_vs_high ≥ 0.90` | `vol_60 / vol_60_prior_max` | passes 3.6% of session-low signals |
| `chg_1d ≤ −8%` | `entry_px / (close_m1 + div_m1) − 1` | `docs/price_adjustment.md`: prior close in day-D raw scale + its dividend increment |
| `chg_20m ≤ −3%` | `signal_vwap / vwap_1200 − 1` | ⚠ vs the 20m VWMA, not the price 20m ago — nearest recorded |
| `chg_3d ∈ [−3%, +30%]` | `entry_px / (close_m3 + div_m3) − 1` | |
| `chg_7d ≥ −5%` | `entry_px / (close_m7 + div_m7) − 1` | |
| `ADV ≥ $500k` | `avgvol20_prior × close_m1` | causal (20 PRECEDING AND 1 PRECEDING) |
| `rvol_0945 ≥ 0.1` | `rvol_0945_honest` | |
| morning | `entry ≤ 11:30` | Run 27 |
| float < $300M, breadth ×3 | not in v1 | joins to float.db / `pct_above_20` — later |

## The whitelists (no base run — the user's standing rule)

* `lowfader_whitelist` (session-low mirror of the MaxFader prepass: barnum ≥ 22 ∧ a bar in
  [09:45, 15:00] strictly below the running prior min ∧ max slot return ≥ 40bp) = **571,466
  ticker-days (39.6%)** — at this engine's density (~40 trips per ticker-day: the long sampler
  opens on every new session low) a multi-hour base run; **not launched**.
* `lowfader_spec_whitelist` = the above ∧ the table-exact gates (`adv ≥ 500k`, `rvol_0945 ≥
  0.1`) ∧ the `chg_1d ≤ −8%` bound (some bar in [09:45, 15:01] ≤ 0.92 × `close_m1` — `entry_px`
  is such a bar, so the gate implies it) = **44,402 ticker-days (3.1%)** — provably a SUPERSET of
  the spec book, not of the base signal. ⚠ Its first run was OOM-killed when overlapped with
  the base prepass (two 8 GB DuckDB processes on 15 GB); run prepasses one at a time.
* Study: `scripts/analysis/lowfader_study1.py <dir>` — base, gate-by-gate and remove-one, the
  spec book mc=0 / mc=1 by year, the flush-depth sizing lever, and the daily-gate bands inside
  the intraday-gated book (where the 1d/3d/7d floors sit on 1s — the side-flip law says the
  1m thresholds are hypotheses).

## §L1 — LowFlyer's spec on the 1s tape, first read (spec-superset corpus, 2020–26)

`data/lowfader_wl_spec` (37,934 whitelisted ticker-days in range, 15 min): **695,906 trips /
21,700 tkd**. `data/lowfader_study1_spec.log`. ⚠ Not yet comparable to the 1m PF 3.38: no
float < $300M (the 1m book's biggest lever: sub-$300M PF 2.8–3.3 vs 1.3–1.4 large-cap) and
no breadth ×3; 2020+ only; fills at the next 1s bar's vwap, not the 1m breakout close.

### L1a — the bare session-low long is a loser in every year

mc=0 PF−1 **−0.26** (695,906), mc=1 **−0.46** (21,700; win 38.7%), negative in all seven years
(−0.22 … −0.58). LowFlyer's edge is entirely selection; the flush itself is a knife.

### L1b — the spec transfers, thinly; four gates carry it, three are inert, one inverts

| gate | alone (mc=0) | pass | remove-one from the full spec |
|---|---:|---:|---:|
| NONE | −0.260 | — | FULL SPEC **0.994** (n 1,029) |
| `flush_1m ≤ −0.7%` | −0.260 | 94% | drop → 0.995 (inert: a session-low bar IS a flush) |
| `flush_1m ≥ −12%` | −0.260 | 99% | drop → 0.728 (tail 1.3 → 2.3%) |
| **`vol_vs_high ≥ 0.9`** | −0.107 | **4%** | drop → **0.308** at n 30,316 (the gate that makes the book) |
| **`chg_1d ≤ −8%`** | **+0.091** | 69% | drop → **0.400** |
| **`chg_20m ≤ −3%`** | −0.263 | 46% | drop → **0.552** |
| **`chg_3d ∈ [−3, +30]%`** | −0.445 | 25% | drop → **0.532** |
| `chg_7d ≥ −5%` | −0.413 | 50% | drop → **1.022** (inert-to-harmful) |
| `adv`, `rvol_0945` | — | 100% | (enforced by the whitelist) |
| **`entry ≤ 11:30`** | −0.268 | 85% | drop → **1.558** at n 1,244 — **the morning gate INVERTS** |

**The spec book:** mc=0 1,029 trips, PF−1 0.994, win 61.7%, worst −65.8%, tail 1.26%. mc=1
**202 trips (~29/yr), PF−1 1.083, net 501%, win 64.4%, worst −65.8%, tail 1.49%.** By year:
2020 2.78 (38) · 2021 2.24 (70) · 2022 −0.24 (12) · 2023 −0.39 (16) · 2024 1.15 (28) · 2025 0.36
(24) · 2026 2.41 (14) — five of seven positive, the two misses on 12 and 16 trips.

### L1c — where the daily gates sit on 1s (intraday-gated book, mc=1, n = 3,760)

`chg_1d`: positive only in **[−30%, −8%]** (0.14 → 0.55, best at −20..−15%); ≤ −30% is a knife
(−0.31 / −0.65, tail 13–23%) and > −8% is dead. **On 1s the 1d gate wants a BAND, not a
floor** — the 1m Run 19 ruling ("don't band 1d") inverts. `chg_20m`: best at −8..−3%; ≤ −15%
catastrophic (−0.73, tail 44%) — floor AND ceiling. `chg_3d`: every band negative alone
(the 1m [−3, +30] band reads −0.50 / −0.33 / −0.27 here) yet dropping it from the full spec
halves PF−1 — its value is in the conjunction, not alone. `chg_7d`: negative in every band.
`volat_20m`: monotone worse with volatility (tail 0.7% → 26%) — the 1m ATR ceiling
transfers as a volat ceiling.

### L1d — the flush-depth sizing lever transfers (Run 26)

Inside the spec minus its flush gates (mc=1): −12..−7% → PF−1 **3.13** (n 19), −7..−4% → 1.22,
−4..−2% → 1.13, −2..−1% → 2.42; deeper than −12% → −0.04 with a 17% tail. Deeper flush =
higher PF up to the −12% floor, with n in the tens.

### Verdicts and next

* LowFlyer's spec **transfers to 1s as a positive, thin book** (PF−1 ~1.0 mc=0 / 1.08 mc=1,
  ~29 trades/yr) with the same four load-bearing gates the 1m research found —
  `vol_vs_high`, `chg_1d`, `chg_20m`, `chg_3d` — and `chg_7d` inert.
* Two side-flip inversions, both actionable: **drop the morning gate** (afternoon entries
  lift the book to 1.558) and **band `chg_1d` at [−30%, −8%]** (the deep end is a knife).
* ⏭ The float < $300M join (the 1m book's biggest lever) and breadth ×3 — without them the
  1m comparison is not fair. Then the exit grid (the aux HIGH marks are recorded: is a 9m
  high cover better than MOC on the long, as it was on the short?). The base run (571k
  tkd, multi-hour) only if the record-first breadth is wanted.

## §L2 — the two missing levers transfer: FLOAT and BREADTH, and the 1s spec (2026-09-04)

`scripts/analysis/lowfader_study2.py`, `data/lowfader_study2_spec.log`. Float = SEC
`dei:EntityPublicFloat` ASOF `known_date ≤ trade_date`, re-anchored causally
(`float_usd / (P_pe·n_pe) × entry_px·n_D` — the split factor cancels exactly as the old
adj_close ratio did), resolved at ticker-day level (a per-trip ASOF against all of
`daily_adjusted` ran away for an hour, twice; a 10-day bounded range join is instant).
Coverage 68.5% (the uncovered are names without SEC float filings — foreign, ADRs, new
listings — and they are the weak set: PF−1 0.24 / 0.60). Breadth = D−1 `pct_above_20`, 97.7%.

### L2a — the 1s spec: the two inversions applied

Drop the morning gate, drop `chg_7d`, band `chg_1d` at [−30%, −8%]:

| book | view | n | PF−1 | net% | win% | worst% | <−20% |
|---|---|---:|---:|---:|---:|---:|---:|
| 1m spec as-is | mc=1 | 202 | 1.082 | 500 | 64.4 | −65.8 | 1.49 |
| **1s spec** | mc=1 | **279** | **1.161** | **645** | 61.3 | **−24.5** | 1.08 |
| 1s spec | mc=1, 20m-high cover | 279 | **1.939** | 662 | 70.3 | −25.1 | 1.43 |

+38% trips, +29% net, the worst trade from −66% to −25% (the `chg_1d` band removes the
knives), and 7-year PF−1 by year 2.82 / 2.17 / −0.52 / 0.13 / 1.11 / 0.62 / 1.00 (2022 the
one loser, on 17 trades). The 20m-high cover on top: PF−1 1.94 at 103% of MOC's net.

### L2b — ⭐ FLOAT < $300M is the lever on 1s exactly as on 1m

| float at entry ($M), 1s spec mc=1 | n | PF−1 | net% | win% | worst% | <−20% |
|---|---:|---:|---:|---:|---:|---:|
| 0–50 | 46 | 1.35 | 147 | 65.2 | −22.7 | 2.17 |
| **50–150** | 28 | **5.49** | 184 | 75.0 | −16.7 | 0.00 |
| 150–300 | 34 | 2.09 | 99 | 61.8 | −8.5 | 0.00 |
| 300–1,000 | 56 | 0.47 | 54 | 58.9 | −23.2 | 1.79 |
| > 1,000 | 13 | ~0.6 | 22 | | | |
| no float data | 102 | 0.60 | 138 | 55.9 | −24.5 | 0.98 |

**`float < $300M`: 108 trips, PF−1 2.27, net 430, win 67%, worst −22.7%, tail 0.9%** (vs ≥ $300M:
0.57). On the 1m spec the same cut gives 80 trips at **3.02** — the 1m book's own number (PF
3.38 = PF−1 2.38) with its own lever. The 1m doc's reading holds verbatim: "$150–300M best,
micro-floats not best once 3d strength is required" — the 50–150M band is the apex here.
By year (1s spec, float < 300M): 7.6 / 4.0 / −0.8 / −0.2 / 29.3 / 2.3 / −0.2 — 4 of 7 positive,
n in the teens per year.

### L2c — ⭐ BREADTH ×3 transfers (Run 23/24)

| 1s spec mc=1 (279) | n | PF−1 | net% | worst% | <−20% |
|---|---:|---:|---:|---:|---:|
| breadth D−1 ≥ 0.65 | 115 | **2.27** | 380 | −16.7 | **0.00** |
| breadth < 0.65 | 164 | 0.68 | 265 | −24.5 | 1.83 |

Equal weight: net 645, PF−1 1.16. **×3 on breadth ≥ 0.65: net 1,405, net/exposure 770
(+19%), PF−1 1.58.** With the 20m-high cover: PF−1 **2.57** sized. On the 1m spec the split is
even sharper (3.19 vs 0.35). A size-up, not a gate — the weak-breadth days still net +265.

### L2d — the full 1m production analog on 1s

**1s spec ∧ float < $300M, ×3 on breadth ≥ 0.65** (mc=1, 108 trips, ~15/yr): equal-weight
PF−1 2.27 / net 430; sized net/exposure 503, **PF−1 2.97**; with the 20m-high cover PF−1 **3.11**.
The high-breadth half alone: **51 trips, PF−1 3.92, win 72.5%, worst −16.7%, 0% below −20%.**

**Verdict.** LowFlyer reproduces on the 1s tape with its own structure intact: a thin,
selection-driven long whose edge is float-tightness × breadth × the 1d/20m/3d gates, at
PF−1 2.3–3.1 on ~15 trades a year, worst trade −23%. The two 1s inversions (no morning gate,
`chg_1d` band) add 38% of trips and cut the worst trade by two thirds; the 20m-high cover
is a near-free PF lift. ⏭ The base run (571k tkd, in flight) re-reads all of this on the
record-first breadth; float coverage (68.5%) is the open data question the user deferred.

## §L3 — the BASE run: the whitelist was exactly complete; `rvol_0945` is a non-lever; the float book reads 3.42 (2026-09-04)

`data/lowfader_wl_base` (`lowfader_whitelist`, 489,058 ticker-days in range, 1.4 GB):
**1,754,105 trips / 80,458 tkd**, 2020–26. `data/lowfader_study{1,2}_base.log`.

**Substitution test passed by construction:** the full 1m spec on the base corpus is
**1,029 mc=0 / 202 mc=1 trips — identical to the spec-superset run.** The superset whitelist
(3.1% of the universe, 15 min) lost nothing; the base run (33% of the universe, 4 h) was the
control that proves it.

**The bare session-low long on full breadth:** mc=0 PF−1 0.002, mc=1 −0.024 (80,458; win
48.6%); 2020 +0.17, 2021 +0.07, every year since ≤ 0. The flush itself is worth nothing;
selection is the whole system.

**One gate changes verdict on full breadth — `rvol_0945 ≥ 0.1` is a non-lever:** alone it
passes 78% and reads −0.046; dropping it from the full spec *raises* PF−1 0.994 → 1.056 and
the book 1,029 → 1,316 (the whitelist had enforced it, hiding this). `adv ≥ 500k` passes 95%
and is inert. Neither belongs in the 1s spec.

**The books, with those two gates gone (study2, mc=1):**

| book | n | PF−1 | net% | worst% | <−20% | tkd/yr |
|---|---:|---:|---:|---:|---:|---|
| 1m spec as-is | 267 | 1.247 | 669 | −65.8 | 1.12 | 47 / 98 / 17 / 21 / 32 / 35 / 17 |
| 1s spec (no morning gate, no 7d, 1d band) | 378 | 1.228 | 854 | −24.5 | 0.79 | 68 / 120 / 25 / 26 / 40 / 65 / 34 |
| **1m spec ∧ float < $300M** | 105 | **3.417** | 495 | −22.7 | 0.95 | (11.1 / 5.0 / −0.9 / 0.7 / 30.0 / 3.9 / 1.2) |
| 1s spec ∧ float < $300M | 143 | 2.281 | 518 | −22.7 | 0.70 | (6.6 / 4.8 / −0.9 / −0.3 / 44.1 / 2.3 / 0.2) |
| 1s spec ∧ float < $300M, ×3 breadth | 143 | 2.678 (net/exp 582) | 1,111 | −50.6 | | |
| — its breadth ≥ 0.65 half | 65 | **3.158** | 296 | −16.9 | **0.00** | |

**The 1m spec with the float gate reads PF−1 3.42 on the 1s tape — LowFlyer's own
production number (PF 3.38) reproduced causally, on 2020–26, at ~15 trades a year.** The
1s spec trades more (143 vs 105) at a lower PF (2.28) with a smaller worst trade; the two
are the same net. Float bands agree with the 1m doc in every detail (50–150M the apex:
23.0 on the 1m spec, 3.36 on the 1s; ≥ $300M ~0.6–1.0; no-float names 0.36–0.58).

**⚠ The 20m-high cover does NOT help the float-gated book:** on the broad books it lifts
PF−1 (1.25 → 1.89; 1.23 → 1.69) at ~95% of net, but on 1s spec ∧ float < $300M it *lowers*
it (2.28 → 2.18, ×3 book 2.68 → 2.09). A tight-float flush's edge is the whole-day bounce;
covering at the first 20m high leaves it on the table. **MOC stays the exit for the float
book; the cover is a broad-book tool.**

`chg_1d` on full breadth (intraday-gated mc=1, 6,405): positive only in [−30%, −8%] (0.14–0.55,
peak −20..−15%), a knife below −30% (13–23% tails), ~0 above −5% — the band stands.

### Verdict — LowFlyer on 1s, and what stays

* **Working spec (1s):** session-low LONG, fill next bar, hold to **MOC**; `flush_1m ≥ −12%`,
  `vol_vs_high ≥ 0.90`, `chg_1d ∈ [−30%, −8%]`, `chg_20m ≤ −3%`, `chg_3d ∈ [−3%, +30%]`,
  **float < $300M**, all session (no morning gate); size ×3 on D−1 breadth ≥ 0.65 and on flush
  depth toward −12%. Dropped from the 1m spec: the −0.7% flush gate (inert on 1s), `chg_7d`,
  `adv`, `rvol_0945`, the morning gate. ~20 trades a year at PF−1 2.3–2.7; the 1m spec's
  literal transplant with float reads 3.42 at ~15.
* **Open data question (user: "at the end"):** float coverage 69% — the uncovered third is
  the weak set, so the float gate is doing double duty as a "has SEC filings" gate; a second
  float source (Polygon shares outstanding is in `float.db`) would say how much of the lever
  is float-tightness and how much is filing status.
* The engine is done: `TradingEdge.LowFader` = FlushFader + the session channel, MOC exit,
  causal daily gates, the volume-high mirror, the one-copy loop. Base corpus 1.4 GB kept.

## §L4 — the REBUILD from scratch (2026-09-05): mc=0 marginals, volatility ceiling, EWMA efficiency, leg density

**Frame (user):** rebuild the spec from nothing on the base corpus, mc=0 (sampler attribution) first, one
feature at a time, MOC exit fixed. Every table carries **PF22 = PF on 2022–26 only**, because 2020–21 make
every feature look monotone (user: "the only problem is that 2020 and 2021 are so good for this system").
Scripts `scripts/analysis/lowfader_rebuild{,2..13}.py`; logs `data/lowfader_rebuild_*.log`.

**L4a — the bare book and the VOLATILITY CEILING.** Every session low held to MOC: 1.75M trips / 80,458 tkd,
PF 1.002 (mc=1 0.976); by year 1.44 / 1.26 / 0.93 / 0.87 / 0.85 / 0.81 / 0.85 — a zero after 2021.
⚠ the corpus already floors `volat_20m` at 40bp (engine default `MinVolat20m = 0.004` + the prepass), so
"every session low" = every session low at ≥ 40bp. By volat tier the bare book is a **monotone in the
WRONG direction for a floor**: 40–60bp PF 1.27 (mc=1 1.08), 60–80 1.12, 80–100 0.95, 100–150 **0.72**,
150+ 0.66 (37% win, 24% of trips < −20%), negative in EVERY year from 80bp up — *"the first time I've
ever seen a consistent negative edge for buying low"* (a short there would pay; not this system).
**Volatility is a CEILING here, the inverse of FlushFader's floor.** User set it at **100bp**
(after a first pass at 60bp).

**L4b — the intraday continuous features are 2020–21 features.** Inside 40–60bp: the FlushFader speed pair
(`speed_1m`, `d1m`), `chg_20m`, `vol_vs_high` and `rr` each sort trips only in 2020–21 and fail the mc=1
control after (speed <−3%: mc=0 1.41 → 2022+ ≤1.0; `rr` U-shaped, quiet end mildly positive; `vol_vs_high
≥ 0.9` mc=1 −0.02, negative every modern year — LowFlyer's volume-confirm INVERTS as a marginal).
⚠ `chg_20m` (and `eff_20m`) are **NaN on ~55% of trips** — the 20m VWAP needs 1,200 tradeable seconds on a
sparse tape (median gap_1200 ≈ 600 s), so 91% of 09:45–10:00 signals have none; any hard gate on a
20m-warm feature is a "not before ~10:15" rule — and the early signals are the BETTER ones (the bare
40–60bp book is afternoon-best, 13:00–15:00 PF 1.6–1.7 vs 1.13 before 10:00 — the morning gate inverts).

**L4c — marginal ≠ conditional (the reconciliation).** As marginals the daily changes read *decliner*
(chg_3d −30..−20 best, LowFlyer's [−3,+30] band dead); but INSIDE the intraday-gated book (flush ∧
vol_vs_high ∧ chg_20m, 2,410 mc=1 trips) the 3d band is LowFlyer's inverted-U with the peak at [−3,+15]
(2.03 / 1.93 vs 1.09–1.44 elsewhere). The full 1m spec on the 40–100bp base, NO float, mc=1: **333 trips
PF 2.52** (user: "2 to 2.5 without float" — float is the multiplier, not the source). Build-up: vol_vs_high
1.18 → +chg_20m 1.41 → +chg_1d 1.49 → **+chg_3d 2.31** → +chg_7d 2.52. Remove-one: vol_vs_high → 1.41,
chg_20m 1.74, chg_3d 1.82, chg_1d 1.90, chg_7d 2.31, flush floor 2.20 (worst −24 → −41). Drop flush+chg_1d+
vol_vs_high → 1.14 (= base); the flush band collapses to the **−12% floor alone** (a 14-trip knife cut,
PF 0.48; the ≤−0.7% side removes 3 trips). mc=0 marginals cannot recover this spec; the daily features only
order winners once the event is defined. `chg_1d` 2022+ ladder: plateau 1.0–1.17 on [−30,−8], 0.8–0.9 on
up-days, knife < −50% — user: "wouldn't make a winning system, but it makes sense to exclude the upper half".

**L4d — EFFICIENCY.** Window `eff_10m` / `eff_20m` (signed; negative = a directional slide into the low):
positive eff = a whipsaw against the trend = 0.80–0.87 and a knife above +0.15 (robust both eras); the
directional side ~1.0 in 2022+. `eff_20m ≤ −0.9` (PF 4.47, 81% win) = **11 ticker-days**, 7 of them
2020-03-10/18 (TRGP/PAA 97 trips at +23/+27%) — a per-trip n of 202 was a per-event n of 11. `eff_10m
< −0.7 ∧ eff_20m < −0.3` fails NaN and deletes the best modern cell (eff_10m<−0.7 ∧ eff_20m NaN: 3,701 tkd
PF22 1.13); `eff_20m` adds nothing inside `eff_10m` (non-monotone in 2022+). ⭐ **The EWMA efficiency**
(SpikeFader's `EwmaEffMa`, Σ decayed r / Σ decayed |r|, half-lives 40/20/10 slot returns = 20m/10m/5m —
PORTED into LowFader 2026-09-05 as `eff_ewma_20m/10m/5m`, record-only, byte-identical trip set, 100%
filled, no warmth cliff; corpus `data/lowfader_wl_ewma` = the base rerun on the 80,458 trip-tkd whitelist
`lowfader_trip_whitelist`, 45 min) **replaces the window pair outright**:

| gate (40–100bp, mc=0) | n | tkd | PF | PF22 | win22 | tail% |
|---|---:|---:|---:|---:|---:|---:|
| eff_10m<−0.7 ∧ (eff_20m<−0.3 \| NaN) | 96k | 7,904 | 1.57 | 1.020 | 51.5 | 2.39 |
| **eff_ewma_10m < −0.7** | 98k | 8,642 | 1.56 | **1.137** | 53.5 | **0.64** |
| eff_ewma_20m < −0.7 | 95k | 8,324 | 1.44 | 1.129 | 53.4 | 0.61 |
| eff_ewma_5m < −0.7 | 126k | 11,035 | 1.61 | 1.086 | 53.3 | 1.30 |

Window vs EWMA `<−0.7` sets overlap at Jaccard 0.10 (ρ 0.62): EWMA-only 80k trips PF22 1.14 / tail 0.4%;
window-only 92k PF22 1.00 / tail 2.5%. The three EWMA horizons are one feature (10m~20m ρ 0.97; any
conjunction = the 10m gate). Knee at −0.7 (−0.5 0.99, −0.6 1.06, −0.7 1.14, −0.9 1.15). **ADOPTED:
`eff_ewma_10m < −0.7`.**

**L4e — LEG COUNTERS and the RATE.** Marginals flat in 2022+ (0.92–1.01 up to 60 lows). INSIDE the eff
gate they order: `lows_since_first_low_600` 18–26 0.96 → 40–60 1.13 → **60–100 1.38** (2,533 tkd, 56% win)
→ 100–200 1.14 → >200 0.24 (56 tkd, knife). Leg age 20–60 min best (1.26–1.60), >60 min 0.32. ⭐ **The
RATE (user: "a really good idea"): `rate_600 = lows_since_first_low_600 / bars_since_first_low_600`** =
new session lows per present bar over the WHOLE current leg (the leg = since the last 10m-HIGH reset; NOT
a 10m lookback). Conditioned on `lows_600 ≥ 40` (601k / 30,229 tkd, PF22 0.977 — the count alone is
inert): rate ≤0.03 **0.73** (a 40-low leg spread over 1,300+ bars = a grind, 45% win, every year <1) →
0.03–0.15 ~1.0 → 0.15–0.175 1.11 → **0.175–0.2 1.44** → 0.2–0.5 1.0–1.8 (thin). Inside eff ∧ lows≥40
(5,820 tkd PF22 1.18): rate ≥0.10 1.23 (5,085 tkd) · **≥0.15 1.30 (2,569 tkd, 57% win)** · ≥0.20 1.68 (848).
Rate × age: a moderate rate SUSTAINED 30–60 min is the best cell; >2h collapses. ⚠ 2026 weakens as the
rate tightens (1.19 → 0.81 → 0.57) — the reverse of 2025.

**⭐ WORKING SPEC (user, 2026-09-05 PM):** `volat_20m ∈ (40, 100] bp ∧ eff_ewma_10m < −0.7 ∧
lows_since_first_low_600 ≥ 40 ∧ rate_600 ≥ 0.15` → mc=0 30.6k trips / 2,569 tkd, PF 2.24, **PF22 1.30**,
win22 57%, tail 0.56%. Not yet in: the −12% flush floor, `chg_1d ≤ −8%` (user leaning to it), the 7d-low
band, `gap_adj_60` (dense tape 0.82 / sparse 1.08 — the strongest 2022+ marginal; "air pocket vs real
selloff"), float. **Candidate tables now carry `low_m1/m3/m7/m20`** (causal prior-session lows in D's raw
scale, total-return convention; `data/equity/daily_lows_causal.parquet`; `dlow_k = entry_px/low_mk − 1`,
NEGATIVE = broke below): on the base every horizon peaks at **−10..−7% below the prior low** (PF22 1.25–1.35)
and reads 0.83–0.93 when the session low still HOLDS above it — a fresh multi-day breakdown bounces, a
pullback above last week's low does not; inside the eff window pair the −20..−3% band below the 7d low read
PF22 1.24–1.49 at 54–60% win. ρ(dlow_3, chg_3d) = 0.81 — the 3d change was a proxy for this.
