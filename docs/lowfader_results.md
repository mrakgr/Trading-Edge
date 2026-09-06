
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

## §L5 — rr, chg_1d, the gap ceiling, the sweeps, the exits, and the ORDINAL structure (2026-09-05 PM)

**L5a — `rr` INVERTS inside the spec (the volume-confirm mechanism on the tape's own clock).** On the base
the quiet end was mildly better; inside eff ∧ lows≥40 ∧ rate≥0.15 loud is monotone: rr 0.25–2 PF22 1.05–1.19,
**2–4 1.68** (793 tkd, 62% win), 4–8 1.95, ≥8 huge/thin. Floor sweep: ≥1 1.40 (1,852 tkd; the first cut to
repair 2026), ≥1.5 1.86 (1,251), **≥2 2.22 (874, positive every modern year 1.42–3.51)**, ≥3 2.78 (439). rr
≥2 repairs the weak 40–50bp volat column (0.86 → 1.14–1.27). `vol_vs_high` is NOT monotone here; rr is the
cleaner measure of the same idea. User: rr ≥1.5 first ("we'll use higher values as sizing tiers"), then ≥2.

**L5b — `chg_1d` is the one daily gate that is ADDITIVE; chg_3d ≥ −3% is not.** Inside the rr≥1.5 spec: the
barely-down day chg_1d ∈ (−4, 0] loses in every modern year (PF22 0.88, 49% win, 314 tkd) and gets WORSE as rr
tightens (0.67 at rr≥2, 0.46 at rr≥3) — orthogonal, not a proxy. Same-n rr control (user: "a lot of these
would lose on iso-trip control with rr"): chg_1d ≤−4 1.98 vs rr-matched 2.00 (tie); ≤−8 2.11 vs 2.22; dlow_7
<−2 2.15 vs 2.23; **chg_3d ≥ −3% 1.71 vs 4.14** (below random); every daily gate ≤ the rr control alone — but
on TOP of rr≥2, chg_1d ≤−4% adds +0.27 (2.22 → 2.49, 717 tkd) where tightening rr to the same size adds 0.
dlow_7 splits the book (breakdown 2.15 vs holding-above 1.47, both positive) → a sizing tier, not a gate.
LowFlyer's 3d band was a property of ITS event (a 1m volume-high bar); once eff/rate/rr define a capitulation
the median trip is already −11% on 3d and the "pullback in an uptrend" cell reads 0.79. **ADOPTED: rr ≥ 2 ∧
chg_1d ≤ −4%.** User: "it's actually quite nice that we don't have to rely on multi-day features anymore."

**L5c — the chg_1d > −4% cell as a SHORT (user).** Inside the long gates it is 901 tkd, 97% before 10:00, PF22
(returns flipped) 0.73 / 1.04 at rr≥1.5 / 1.38 at rr≥2; the (−4,−2] band 1.78 at rr≥2 (184 tkd, positive every
modern year), the ≥−2% bands too thin to read. Best spots each 35–50 modern tkd: rr 3–4 2.81, a 7d low just
broken 3.04, gap-UP +2..+5% then sold to the low 1.97 (a gap-up >+5% BOUNCES, 0.08), dense-ish tape 1.87. 2020
NEGATIVE nearly everywhere (regime). On the broad base (48k tkd) the mechanism = a FAILED GAP-UP sold from the
open on high rr: rr≥3 ∧ gap-up (0,5%] = 2,670 tkd PF22 1.43 / 53% win, all modern years >1, whole-period ~1.1.
A candidate ("failed gap-up short"), needs its own sampler/entry logic; not pursued now.

**L5d — VOLATILITY re-check with the gates on: ≥100bp is NOT fixed, and the low end inverted.** Inside the full
spec (no ceiling): 80–100bp is the BEST cell (PF22 2.01, 62% win, all 5 modern years) and 40–50bp the weakest
(0.92); 100–125bp 0.60 (46% win, 10% tail, worst −73), 125–200 1.5 (49% win, 9% tail), 200–500 5.7–7.3 on ~60
tkd with a 10–16% tail (a lottery), ≥500 1.06. Ceiling sweep peaks at ≤100 (2.49; ≤125 1.74). Band options:
(50,100] 1.55 on 1,665 tkd; user: "raising the floor to 50 would be overfitting; ideally all volat bands work"
→ ceiling stays at 100, no floor. Re-checked after the gap gate (L5e): 100–200bp 1.08–1.13 at every gap level
(even the densest tapes read 0.56 at 100–150bp) — the ceiling STAYS; 200bp+ = a satellite tier later.

**L5e — GAPS invert inside the spec, and the ceiling clears the same-n test.** Inside rr≥2 ∧ chg_1d≤−4 (717 tkd,
PF22 2.49) `gap_adj_60` is monotone DECREASING: 0–10 missing s 3.4–7.3 (80% win), 10–25 2.7–5.0, 25–30 2.21,
30–35 1.46, 35–45 **0.76–1.01** (48–50% win) — the base's "sparse air pocket bounces" was the low-rr/low-eff
population; once a loud dense leg is required, a tape missing 35+ s/min is a flush that isn't being traded.
Loses even at rr 3–8 (0.57–0.64). Same at 300/600/1200 s. Ceiling sweep vs the same-n rr control: ≤35 2.89 vs
2.54 · ≤30 3.25 vs 2.60 · ≤25 3.50 vs 2.94 — **the first feature since rr to beat the rr control at every
size**, positive every modern year. PF−1 grid (2022+), rr floor × gap ceiling, tkd22 / net22:

| | none | ≤40 | ≤35 | ≤30 | ≤25 |
|---|---|---|---|---|---|
| rr≥1.5 | 0.98 (597/6,864) | 1.05 | 1.22 (464/6,545) | 1.41 (352/5,957) | 1.75 (230/5,401) |
| rr≥1.75 | 1.23 | 1.32 | 1.53 | 1.75 (310/5,821) | 2.16 |
| rr≥2 | 1.49 (424/6,155) | 1.58 | 1.89 (341/6,037) | **2.25 (266/5,737)** | 2.50 (187/5,128) |

≤35 → ≤30 at rr≥2: +19% PF−1 for −5% net (the 30–35 slice PF−1 0.46; 35–40 is DEAD, 0.01). Relaxing rr to
trade for a tighter gap is dominated on every column (rr is ~3× more net-efficient). **ADOPTED: gap_adj_60 ≤ 30.**

**L5f — the rr and gap SWEEPS are monotone; higher values = SIZING tiers (user).** rr floor on the gap≤30 spec
(PF−1 22+ / tkd22 / net22 / net per tkd): ≥1.5 1.41/352/5,957/17 · **≥2 2.25/266/5,737/22** · ≥2.5 2.37/199/
4,564/23 · ≥3 3.59/155/4,188/27 · ≥4 5.12/89/3,372/38 · ≥6 7.10/45/2,774/62 · ≥8 20.6/25/2,418/97. The 1.5–2
slice is dead (PF−1 0.13, avg +0.29%) — hence "free"; above 2 every step removes GOOD slices (2–2.5 = 1.87 at
74% win) but the average trade doubles by rr 4 and quadruples by 8, tail → 0 above 4 ⇒ floor at 2, size UP by
tier (3–6 ~1.5–2×, ≥6 ~3×); ⚠ 2023 negative from rr≥3 up (thin). Gap ceiling on the rr≥2 spec: ≤30 2.25 → ≤25
2.50 → ≤20 2.67 → ≤15 2.82 (peak) → ≤10 2.61 → ≤5 2.39; below 30 each step costs 11–13% of net for ~+0.2 —
the good slices 15–30 read 1.2–2.1 — stop at 30. Both levers monotone "apart from the last bucket" (user).

**⭐ WORKING SPEC v2 (2026-09-05 PM):** `volat_20m ∈ (40,100] ∧ eff_ewma_10m < −0.7 ∧ lows_since_first_low_600
≥ 40 ∧ rate_600 ≥ 0.15 ∧ rr ≥ 2 ∧ chg_1d ≤ −4% ∧ gap_adj_60 ≤ 30`, MOC → mc=0 3,559 trips / **466 tkd** (266 in
2022+), PF 3.33, **PF22 3.25 (PF−1 2.25)**, win22 69.5%, avg22 +2.8%, tail 1.35%, worst −42.8. Whitelist table
`lowfader_spec2_whitelist` (466 tkd; a rerun takes seconds). Corpus `data/lowfader_wl_spec2` carries the
long-horizon covers `aux_hi_{2400,3600,7200,10800}` added 2026-09-05 (present-bar windows, like every price
channel; byte-identical trip set).

**L5g — EXITS (user): the 10m/20m channel-HIGH cover beats MOC in the modern era.** Per-trip (mc=0; an unfilled
mark holds to MOC), PF−1 all / 2022+, net22, worst, p5, tail, median hold:

| exit | fill | PF−1 | PF−1 22+ | win22 | avg22 | net22 | worst | p5 | tail% | hold |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| MOC | – | 2.33 | 2.25 | 69.5 | 2.79 | 5,737 | −42.8 | −8.3 | 1.35 | 368m |
| 2m | 100% | 2.43 | 6.92 | 69.4 | 2.41 | 4,954 | −28.4 | −3.3 | 0.65 | 4m |
| 5m | 100% | 2.97 | 4.84 | 70.9 | 2.90 | 5,964 | −29.2 | −4.0 | 0.51 | 12m |
| **10m** | 99.9% | 2.94 | **4.57** | 69.0 | **3.47** | **7,138** | −29.1 | −5.8 | 0.51 | 27m |
| 20m | 94.6% | 2.35 | 3.46 | 70.1 | 3.50 | 7,195 | −40.6 | −7.7 | 0.14 | 65m |
| 40m/1h/2h/3h | 79/63/29/22% | 2.1–2.5 | 2.6–2.7 | 66–70 | 3.3–3.4 | 6,750–6,938 | −42.8 | −8 | 1.35 | 3–6h |

The 10m cover: +24% net22, PF−1 ×2, tail ÷2.6, worst −43 → −29, and the position is out in ~27 minutes instead
of six hours; by year 3.48/6.70/10.38/3.86/−0.02 vs MOC 2.29/0.83/3.89/3.28/1.50 — **2026 is the exception**
(the cover loses there). MOC only wins in 2020 (8.38 vs 2.07 — the meme-year bounces to the close), which is
why all-years PF−1 is nearly a tie (2.33 vs 2.94) while 2022+ is not. The long-horizon covers converge to MOC
as their fill rates fall. Section C: on the trips it fires on, the 10m cover is better than MOC on 45% of them
and wins by cutting the losers. NOT yet re-run at the book level with the cover freeing the slot (see L5h).

**⚠⚠ L5h — the ORDINAL structure: mc=1 (first qualifying trip per day) reads PF−1 0.33 on 2022+ vs 2.25 for
the same spec at mc=0.** Trips per tkd: median 4, mean 7.6, p90 19. By ordinal (MOC PF−1 22+ | 10m cover):
1st 0.33 | 1.06 · 2nd 0.45 | 1.38 · 3rd 0.68 | 1.94 · 4–5th 1.09 | 3.05 · 6–10th 2.99 | 3.84 · 11–20th 3.74 |
7.20 · 21–50th 36 | 27 (45 tkd). By minutes since the day's first spec trip: 0 → 0.33 · 0–2m 1.32 (2,192 trips,
359 tkd) · 2–5m 10.4 · 5–10m 5.7. The first trip is a slightly quieter, shorter leg (rr 2.66 vs 3.13, lows 69
vs 85); everything else is the same — **the edge is in re-entering as the flush keeps printing lows, not in the
first qualifying bar.** mc=1 replay skipping the first k−1 trips (MOC | 10m): start at 2nd 0.45 | 1.38 · 3rd
0.68 | 1.94 · 5th 1.19 | 3.45 · 10th 3.39 | 4.93 (118 tkd). ⇒ the sampler PF is an AVERAGING-DOWN book (the
S38 greedy replay at mc=1 is the wrong portfolio view for this system); the production question is the
scaling-in rule (mc>1 / pyramid on each new qualifying low), and the 10m cover looks better at every ordinal.
OPEN — the user's call on how to size the ladder.

## §L6 — ENGINE: the mutable trip record (2026-09-05 evening) — 3.9× on trip days, zero-diff

**Where the time went.** Raw tape reads are not the cost: a full day of 1s tape (11k tickers, 16–21M rows,
~110 MB) materializes in 0.2–0.7 s (0.03 ms per ticker-day). The engine spent a median **7.1 ms per
ticker-day** on the old universe (base run 489,058 tkd in 3,736 s — ⚠ NOT the "4 h" quoted earlier today,
which was the whole chain), and **23 ms on trip-producing days** (the `ewma` rerun: 80,458 trip-tkd in
1,872 s) — the trip record is ~700 fields (~5–6 KB) and the "one-copy" advance loop (2026-09-04) still
allocated and copied the whole record per open trip per bar to change ~50 fields. User: *"we designed
the engine so that we're doing immutable updates on the trips… that might have been a mistake."*

**The change (LowFader first):** the 50 per-bar fields (`EntrySec/Px`, the 4 forward vwaps, the 9
`AuxHi/AuxSec` pairs, the 12 `Ma*/Vwma*` `Px/Sec` pairs, `BarsHeld`, `State`) are `mutable`; the five
`{ p with … }` sites (entry fill, pending-exit fill, the per-bar update, Flatten's MOC stamp, Flatten's
MA-mark straggler resolution — `Sec` assigned before `Px` there since `finSec` reads the price) became
in-place assignments. The ~650 feature fields stay immutable (written once at signal time). Nothing
aliases a live trip (`.Positions` is read once after `Flatten`). Smoke: 2,713 trips on 2024-03-04/05
byte-identical on all 319 columns. **Benchmark: the 80,458 trip-tkd rerun 1,872 s → 485 s (3.9×)**,
identical corpus; trip days now cost ~6 ms = the trip-free rate. Expanded-universe estimates drop to
~2.5 h (full period, mirrors) / ~5 h (no prepass). Next: port to FlushFader/SpikeFader/MaxFader
(same loop), then a per-ticker channel pipeline (the day loop uses ~4 cores).
**§L6 addendum — the port.** The same change on SpikeFader (101 mutable fields), MaxFader (128) and FlushFader (41 + the
five-copy loop's `State`/`BarsHeld` match arms rewritten as statements), all byte-identical to their reference corpora:
SpikeFader 1,409 smoke trips / 470 cols vs `spikefader_s47`; MaxFader the whole `rr8` corpus 182,291 trips / 531 cols
(76 s); FlushFader 62 trips / 312 cols vs `v49_spec20` (⚠ that run predates the `--min-lows-180 3` default — reproduce
it with `--min-lows-180 0`; the equity scripts apply the floor post-hoc). Port script:
`scratchpad/port_mutable.py` pattern (collect the fields from every `{ p with … }`, mark, replace).

## §L5i — the SPEC ORDINAL in the engine (2026-09-05 evening) + the EXPANDED-UNIVERSE run

**Engine feature (user):** `spec_ord` = this signal's ordinal among SPEC-qualifying signals of the current 20m leg
(1-based; 0 = the signal did not pass), `leg_id_1200` = 20m-high breaches so far today. RECORD-ONLY: eight
`Ord*` config fields with SPEC v2 defaults (`--ord-volat-lo/hi`, `--ord-max-eff-ewma-10m`, `--ord-min-lows-600`,
`--ord-min-rate-600`, `--ord-min-rr`, `--ord-max-chg-1d`, `--ord-max-gap-adj-60`); `IntradaySystem` now takes the
prior close (`close_m1 + div_m1`) so `chg_1d` is computable at the SIGNAL bar (signal vwap, not the fill px — a
boundary-only difference vs the post-hoc column). Counter increments on bars passing every gate; a new 20m HIGH
(the `br1200` breach) resets it. Validation on the 466-tkd spec whitelist: all 327 shared columns byte-identical;
the passing set matches the post-hoc signal-bar spec exactly (3,563 = 3,563). ⚠ **The post-hoc "leg ordinal" of
§L5h/step-28 was WRONG on 62 of 465 days**: it detected a reset whenever `breach_1200` did not increase between
consecutive spec trips, and `breach_1200 = −1` (no 20m high printed yet today) is non-increasing by construction —
so every pre-first-breach trip read as ordinal 1. The engine counter is the reference (its ordinals are ≥ the
post-hoc ones on those 424 trips; the DAY-ordinal analysis was unaffected). Re-do the ordinal tables on `spec_ord`.

**The expanded-universe run:** `lowfader_alltape_cand` = the WHOLE tape 2020-01..2026-08 with no dv_0945 / n_bars
/ barnum floors (9,075,669 tkd, 9,564 tickers, built in 120 s via the builder's new `--pattern`); prepass =
the 20bp VOLATILITY mirror ONLY (no liquidity screen — user: a `dv_day` screen is lookahead unless its implied
engine gate is always applied, and the illiquid tail is the point; no session-low mirror either) → 8,095,300
tkd (89.2%; 1,103,634 of them in the old candidate table). ⚠ The 20bp bound passes 62–85% of names per day
and the EXACT per-day max of the EMA only ~10 points fewer (the bias-corrected EMA equals the first slot return
on its first push, and sparse tapes print large early slot returns — on illiquid tape `volat_20m` is largely
bid-ask bounce). Engine flags `--min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002`. The single-process
run sat at 11–13 GB resident with 1–3 GB free (the 8M-row candidate array + DuckDB native memory over the 8.5 GB
managed cap) → restarted as TWO date halves (`lowfader_run_alltape_halves.sh`, GC cap 45%, dirs
`data/lowfader_alltape_h1` 2020–22 and `_h2` 2023–26; read both globs; h1 at 7 GB resident with 5.9 GB free).
⚠ The prepass's DuckDB `executemany` of 8.1M rows ran >15 min at 11 GB and was replaced by a pandas parquet
write (seconds). Study script: `scripts/analysis/lowfader_alltape.py` (universe split, time-clock
`dollar_vol_60` ladder, rr × liquidity for the user's TODO — "the outperformance of the low-rr cell might be
manifesting in very illiquid stocks" — the 20–40bp band, spec v2 + 10m cover on the new names, mc=0 and mc=1).

## §L7 — the EXPANDED-UNIVERSE corpus (2026-09-05 night): 11.78M trips / 916,633 tkd, the whole tape ≥ $100k/min

**What ran.** `lowfader_alltape_whitelist` (8.1M tkd, 20bp volat mirror only), floors `--min-dv-0945-tape 0
--min-barnum 0 --min-volat-20m 0.002`, eight globs after two OOM kills (14 workers), one pre-emptive stop (5 workers
climbing) and two boundary reruns (h1c/h2e) — every whitelist day present, zero duplicate tkd. ⚠ **The engine's HARD
ENTRY FLOORS `dv60 ≥ $100k ∧ tc60 ≥ 60` were ACTIVE** (`--base-run` keeps the signal definition; `min(dollar_vol_60)`
in the corpus = $100,000.10). So this is "the whole tape *above $100k a minute and 60 trades a minute at the signal*";
the illiquid tail of the user's TODO (rr < 0.5 as an illiquidity premium) is NOT sampled — a floors-off rerun
(`--dv-floor-60 0 --tc-floor-60 0`, ~3 h at 3 workers) is the user's call. Study: `data/lowfader_alltape_study.log`.

**L7a — the added names, bare:** 658,553 tkd / 4.76M trips beyond the old candidate table read PF22 0.96 (old
universe 0.94) — the same zero. The old-candidate names admitted only by the 20bp floor (4,938 tkd, 27 trips/tkd)
are the WORST cell on the tape bare: PF22 0.52, 44% win, −2.6%/trade, 4.1% tail. Raw price < $1: 0.70–0.78, 5–12%
tail (fee-dead, as LowFlyer found). By `dollar_vol_60` the bare book is flat 0.91–0.97 at every tier; the one
bare cell above 1.1 is the most liquid new names (≥ $2.5M/min, 11k tkd, 1.34).

**L7b — rr × liquidity (the TODO, as far as sampled):** 2022+ every cell 0.76–1.07 except the ≥ $2.5M/min column at
rr ≥ 4 (1.25–1.32, 6.5k tkd). In the least-liquid tier sampled ($100–250k/min) the quiet end reads 1.00–1.01 and
the loud end 0.86–0.91 — the U's quiet side, no premium. Whether a premium exists BELOW $100k/min is open.

**L7c — VOLATILITY on the whole tape:** the bare book's best cells moved DOWN: 20–30bp PF22 1.03 (481k tkd, 0.06%
tail), 30–40bp 1.05, 40–50 1.03, 50–60 1.01, 60–80 0.95, 80–100 0.89, ≥ 100bp 0.63–0.76 (tail 7–24%). Same shape
on the old universe alone (1.04 / 1.06 at 20–40bp). The 40bp floor was never a feature; the 100bp CEILING is.

**L7d — SPEC v2 on the expanded universe (mc=0):**

| slice | n | tkd | tkd22 | PF | PF22 | win22 | worst | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| old universe (now incl. barnum < 22 days) | 3,861 | 509 | 292 | 3.02 | 2.93 | 68.3 | −42.8 | 3.40 | 1.58 | 4.69 | 3.67 | 1.76 |
| **NEW names** | 4,141 | 538 | 303 | 1.38 | **0.66** | 51.9 | −70.9 | 0.49 | 0.69 | 0.31 | 2.18 | 0.90 |
| **spec v2 gates in the 20–40bp band** | 4,316 | 560 | 387 | 2.04 | **1.61** | 60.3 | −24.8 | 1.38 | 1.05 | 2.83 | 3.17 | 2.71 |

**The spec does NOT transfer to the added names** (0.66 mc=0; mc=1 −0.17 MOC / −0.07 10m) — and the reason is a
LIQUIDITY gate the old universe had hidden: spec v2 by `dollar_vol_60` reads $100–250k 0.51 · $250k–1M 0.99–1.00 ·
**$1–2.5M 1.53 · ≥ $2.5M 2.26**. The new names' trips sit at $100k–$1M (median $234k/min vs $560k old). ⇒ spec v2
needs **`dollar_vol_60 ≥ $1M` at the signal** (time-clock) as an explicit gate — the old $2M-first-15m candidate
floor was standing in for it. **The 20–40bp band under the spec gates WORKS**: 560 tkd at PF22 1.61, 60% win, worst
−24.8, 0.4% tail, positive in all five modern years — the volat floor can drop to 20bp *inside the spec* (bare it
is the worst cell). Old universe 509 vs the earlier 466 tkd: the 43 extra are `barnum < 22` early-episode days,
and they read worse (2.93 vs 3.25) — S40e's cut holds. mc=1 on the expanded spec book: old universe 10m 0.85, all
else ≤ 0.25 — the ordinal/ladder question is unchanged.

**Verdict:** the expansion adds NOTHING under $1M/min; above it the added names are few. Next: (1) `dollar_vol_60 ≥
$1M` into the spec and re-read the whole ladder on the union corpus; (2) the 20–40bp band joins the spec's volat
band → (20, 100]; (3) the floors-off rerun only if the illiquid TODO is still worth 3 h — the ≥ $100k evidence says
the quiet-rr cell carries no liquidity premium down to $100k/min.

## §L8 — SPEC v3: `dollar_vol_60 ≥ $1M` (time-clock) + what the old candidate floor was really doing (2026-09-05 night)

**Adopted (user):** `dollar_vol_60 ≥ $1M` — TIME-clock (`tDvSum60`, 60 tradeable seconds; the `_bar` twin is the
substitution pair), the same accumulator as the engine's own $100k floor. In the engine as the ordinal gate
`OrdMinDv60 = 1e6` (`--ord-min-dv-60`); `spec_ord` validated 2,520 = 2,520 vs post-hoc on the spec whitelist.
**On the old universe it is additive: (40,100]bp spec v2 498 tkd PF−1 2.09 → +dv_60 ≥ $1M 331 tkd PF−1 3.01**
(2022+ 4.81 / 0.88 / 4.71 / 4.22 / 2.10; net22 5,731 → 5,348, −7%). ⚠ "old universe" here = in `mr_candidate_1s_v2`
∧ `barnum ≥ 22`; 498 vs the earlier 466 tkd = signals with volat in (39, 40) bp that the old 40bp mirror excluded.

**But the ADDED names still lose above $1M/min** (228 tkd, PF−1 −0.57; mc=1 negative). Split by WHY they failed
the old candidate floor (spec v2 ∧ dv_60 ≥ $1M, 2022+ PF−1): failed only `n_bars_1s ≥ 200` **−0.60 (216 tkd)**;
had `dv_0945_tape ≥ $2M` but failed n_bars/barnum/type −0.39 (149); morning dv $500k–2M −0.74; < $500k −0.57;
`barnum < 22` −0.71. ⇒ **the load-bearing morning condition is `n_bars_1s ≥ 200`** (≥ 200 traded seconds in the
first 15 minutes — a name trading continuously from the open), not the dollars; a name that is loud at the signal
but was not trading at the open does not bounce. Both are causal at 09:45. And `dv_0945_tape` is a BAND on the
spec-v3 book: < $2M loses (−0.57..−0.77), **$2M–5M 0.80, $5M–20M 2.76 (271 tkd, 71.5% win)**, ≥ $20M −0.06
(91 tkd, median px $41 — the mega-liquid names do not bounce). The 20–40bp band inside the spec is positive but
DILUTIVE on the old universe ((20,100] 803 tkd 1.31 vs (40,100] 498 tkd 2.09) → a lower tier, not the core.

**⭐ SPEC v3 (2026-09-05 night):** v2 ∧ `dollar_vol_60 ≥ $1M` ∧ `n_bars_1s ≥ 200` ∧ `dv_0945_tape ∈ [$2M, $20M)`
(the last two = the old candidate floor made explicit, plus a mega-cap ceiling to test) — the expanded run's net
contribution is to have NAMED the morning gate; the universe it adds is worthless for this system. mc=1 on spec v3
(first bar/day) is still 0.23 MOC / 0.47 10m — the ordinal/ladder question is untouched by any of this.

## §L9 — the 50bp FLOOR and AVERAGING DOWN (mc = k) (2026-09-05 late night)

**Ceiling re-test under spec v3:** stays at 100bp — 100–125bp is the WORST cell in the ladder (PF−1 −0.67 MOC / −0.53
10m, 21% tail), above it a 2024 lottery; the liquidity gate removed the illiquid crashers, not the high-volat losses.
**Floor (user: raise to 50bp):** at the single-position level the 40–50bp trips were worth ~0.2%/trade — mc=1 first-bar
net22 247 → 218 (−12%) for PF−1 0.85 → 1.32 and worst −43 → −17; the ordinal-≥4 book −2% net; mc=0 −8% net at 2×
PF−1 (3.01 → 6.77). At 60bp the first-bar MOC book nets MORE than at 40 (272 vs 247) at 18 trades/yr, PF−1 5.34, worst
−12 — the volat floor does what the ordinal hack did (the mc=1/mc=0 gap shrinks from 3.5× to 4× of a much larger
base). **ADOPTED: floor 50bp.** The `rr` dependence and the trade count are untouched: more trades need a second
entry family, not a looser gate.

**⭐ SPEC v3 (2026-09-05 night):** `volat_20m ∈ (50, 100] ∧ eff_ewma_10m < −0.7 ∧ lows_600 ≥ 40 ∧ rate_600 ≥ 0.15 ∧
rr ≥ 2 ∧ chg_1d ≤ −4% ∧ gap_adj_60 ≤ 30 ∧ dollar_vol_60 ≥ $1M` on the old universe (`n_bars_1s ≥ 200`, `barnum ≥ 22`).
mc=0: 1,532 trips / 200 tkd (114 in 2022+), PF−1 6.77, 75.5% win, worst −16.7, 0% tail.

**mc = k (user: "maybe we should actually consider averaging down"):** greedy replay, up to k concurrent positions per
tkd, each opened by a qualifying bar while < k are open, closed at its own exit. 2022+ net % at 1 unit/trade:

| exit | mc | trades/yr | trips/tkd | PF−1 22+ | win22 | avg22 | net22 | net22 / k | worst |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| MOC | 1 | 30 | 1.00 | 1.32 | 62.3 | 1.91 | 218 | 218 | −16.7 |
| MOC | 2 | 55 | 1.82 | 1.58 | 61.9 | 2.21 | 465 | 232 | −16.7 |
| MOC | 3 | 75 | 2.50 | 1.78 | 62.5 | 2.39 | 695 | 232 | −16.7 |
| MOC | **5** | 110 | 3.64 | **2.47** | 65.9 | 2.95 | **1,252** | **250** | −16.7 |
| MOC | 10 | 161 | 5.32 | 3.48 | 68.5 | 3.66 | 2,290 | 229 | −16.7 |
| MOC | 0 (all) | 231 | 7.66 | 6.77 | 75.5 | 5.32 | 4,895 | 70 | −16.7 |
| 10m | 1 | 30 | 1.00 | 1.75 | 62.3 | 1.98 | 225 | 225 | −20.0 |
| 10m | 5 | 110 | 3.64 | 2.59 | 61.4 | 2.87 | 1,219 | 244 | −24.5 |
| 10m | 10 | 161 | 5.32 | 3.61 | 61.0 | 3.82 | 2,387 | 239 | −24.5 |

**Every additional position is BETTER than the one before it** (MOC, PF−1 22+ by position: 1st 1.32 · 2nd 1.93 · 3rd
2.35 · 4th 4.64 · 5th 5.03; avg/trade 1.9 → 4.4%; 10m: 1.75 → 4.05). Net scales ~linearly with k (each unit of
capital earns the same ~230–250%/4.6 yr whether it is the 1st or 5th slot) while PF−1 rises — averaging down into
the flush is the natural shape of this system (the sampler's mc=0 edge was never a sampler artifact; it was the
later entries). mc=5 with MOC: **110 trades/yr, PF−1 2.47, net 1,252% over 2022+ (270%/yr at 5 units), worst
−16.7, zero trades under −20%** — the "few trades" complaint answered without touching a gate. Sizing per slot
(rr / gap / ordinal tiers) is the remaining lever; the engine's `spec_ord` is the live-knowable slot index.

## §L10 — SPEC v3 REMOVE-ONE at mc = 0 / 1 / 5 (2026-09-05 late night; user: "try removing some of the old gates, starting with eff")

Old universe, `dollar_vol_60 ≥ $1M` kept; mc=5 = MOC averaging down. 2022+ (`scripts/analysis/lowfader_specv3_removeone.py`):

| spec | mc=0 tkd22 / PF−1 | mc=1 trades/yr / PF−1 / net / worst | mc=5 trades/yr / PF−1 / net / worst |
|---|---|---|---|
| SPEC v3 (full) | 114 / 6.77 | 30 / 1.32 / 218 / −17 | 110 / 2.47 / 1,252 / −17 |
| drop eff_ewma_10m < −0.7 | 207 / 2.37 | 56 / 0.48 / 206 / −34 | 202 / 0.84 / 1,231 / −34 (2026 −42) |
| drop volat > 50bp | 193 / 3.01 | 50 / 0.85 / 247 / −43 | 178 / 1.29 / 1,305 / −43 |
| drop lows_600 ≥ 40 | 120 / 6.42 | 32 / 1.38 / 232 / −17 | 115 / 2.36 / 1,235 / −17 |
| drop rate_600 ≥ 0.15 | 281 / 1.05 | 71 / 0.46 / 292 / −29 | 251 / 0.69 / 1,508 / −30 |
| drop rr ≥ 2 | 162 / 1.12 | 44 / 0.67 / 202 / −21 | 162 / 1.01 / 1,129 / −21 |
| drop chg_1d ≤ −4% | 117 / 5.24 | 32 / 1.13 / 203 / −17 | 119 / 2.20 / 1,212 / −17 |
| **drop gap_adj_60 ≤ 30** | 163 / 5.42 | 40 / 1.18 / 281 / −19 | **144 / 1.98 / 1,441 / −19, 0% tail** |
| drop eff + lows + rate (the leg block) | 1,803 / 0.32 | 467 / 0.08 / 389 / −76 | 1,681 / 0.07 / 1,233 / −76 |

**eff stays.** Dropping it nearly doubles the trades (110 → 202/yr at mc=5) for LESS net (1,252 → 1,231), PF−1 2.47 →
0.84, worst −17 → −34, and a negative 2026 — the trades it admits are net-zero and carry the tail. Inside the rest of
the spec the eff ladder is a cliff, not a slope: (−0.9,−0.8] mc=0 10.9 / (−0.8,−0.7] 8.6 / (−0.7,−0.6] 4.9 (mc=5 2.27,
worst −33) / (−0.6,−0.5] **−0.11** / (−0.5,−0.3] 0.18. Floor sweep: −0.7 is the knee (net 1,252); −0.6 adds 40
trades/yr for the same net (1,245) and worst −33; −0.8 loses 30% of net at the same PF. **The leg block (eff ∧ lows ∧
rate) IS the event**: without it the book is the base sampler (1,681 trades/yr, PF−1 0.07, same net).

**The one benign relaxation is `gap_adj_60 ≤ 30`**: dropping it adds 31% trades and **+15% net (1,441) at PF−1 1.98,
worst −19, zero tail** — the 30–45 gap trips are positive at mc=5 (they were the 0.46–1.0 slices at mc=0). ⇒ gap is a
SIZING tier, not a gate, in the averaging-down book. `chg_1d` is cheap to keep (−3% net, +12% PF−1); `lows ≥ 40` is
implied by the rate gate (−1% net). `rr ≥ 2` is load-bearing (drop → PF−1 1.01, net −10%) and not the trade-count
lever (162 vs 110/yr). ⚠ 2023 is thin in every variant (net 152–225 vs 350+ elsewhere).

## §L11 — rr vs SPEED as the loudness gate, and the −12% depth floor (2026-09-05 late night, user)

Frame = spec v3 minus rr (162 tkd22). rr vs each speed measure at the matched trade count (mc=5 MOC, 2022+):

| gate | trades/yr | PF−1 | net22 | worst | tail% |
|---|---:|---:|---:|---:|---:|
| **rr ≥ 2 (spec v3)** | 110 | **2.47** | **1,252** | −17 | 0 |
| speed_1m < −2% | 99 | 1.19 | 933 | −21 | 0.76 |
| d1m < −2% | 91 | 1.89 | 1,038 | −21 | 0.17 |
| speed_30s < −1% | 122 | 1.26 | 1,080 | −21 | 0.62 |
| s5 < −0.5% | 90 | 1.92 | 1,106 | −21 | 0.67 |
| s10 < −1% | 64 | 2.71 | 951 | −21 | 0.24 |
| speed_1m < −4% | 43 | 6.51 | 977 | −17 | 0 |
| d1m < −4% | 37 | 9.00 | 1,018 | −17 | 0 |
| rr ≥ 2 ∧ speed_1m < −2% | 72 | 4.01 | 1,122 | −17 | 0 |
| rr ≥ 2 ∧ s5 < −0.5% | 62 | 5.62 | 1,153 | −17 | 0 |

**rr cannot be replaced by speed.** At rr's trade count every speed gate has half the PF, less net and a fatter tail;
the speed gates reach rr's PF only at −4% (a third of the trades, 20% less net). ρ(rr, speed) ≈ −0.3: different
information. Inside rr ≥ 2 the speed ladder is monotone at mc=5 (−1..−0.5% PF−1 0.06 / net 4 → −2..−1.5 3.4 → −4..−3
4.8 → −12..−6 39) ⇒ **speed is a SIZING tier on top of rr** (rr ∧ speed<−2%: −10% net for +62% PF−1), and the
−1..0% slice (~55 trades, net ≈ 0) is the one cuttable sliver.

**LowFlyer's −12% depth floor: NO.** On spec v3 the trips deeper than −12% are the BEST cell (148 trips / 16 tkd,
PF−1 35.8, worst −16.7); the floor costs 16% of net (1,252 → 1,047) and lowers PF (2.47 → 2.30); −6% is worse (710).
The falling knife LowFlyer cut at −12% does not exist here — `dollar_vol_60 ≥ $1M ∧ eff ∧ rate` already removed it.

## §L12 — can SPEED replace the ORDINAL? (mc=1, spec v3 @ 50bp; 2026-09-05 late night)

| which-bar gate | trades/yr | MOC PF−1 / net22 / worst | 10m PF−1 / net22 / worst | median ordinal selected |
|---|---:|---|---|---:|
| 1st bar (none) | 30 | 1.32 / 218 / −17 | 1.75 / 225 / −20 | 6 |
| **ordinal ≥ 4** | 19 | **4.64 / 282 / −11** | 3.61 / **266** / −15 | 10 |
| ordinal ≥ 5 | 16 | 5.03 / 275 / −11 | 4.05 / 261 / −14 | 11 |
| speed_1m < −2% | 19 | 1.98 / 199 / −17 | 2.92 / 224 / −20 | 7 |
| speed_1m < −3% | 12 | 3.75 / 204 / −17 | 6.74 / 245 / −20 | 7 |
| d1m < −3% | 10 | 4.55 / 202 / −17 | 10.8 / 252 / −20 | 6 |
| s5 < −1% | 9 | 5.24 / 196 / −17 | 14.2 / 251 / −20 | 7 |
| ordinal ≥ 4 ∧ speed_1m < −2% | 14 | 8.20 / 266 / −11 | 7.14 / 290 / −15 | 9 |

**No.** Speed does not select later bars (median ordinal 7 vs 10, and 9–10% of its picks are still the day's first
bar; median speed barely rises with the ordinal, −2.1% → −3.2%) and on MOC it never reaches the ordinal's net
(164–204 vs 275–282) nor its worst (−17 vs −11). With the 10m cover the fast bars post huge PFs (d1m < −3% 10.8,
s5 < −1% 14.2) at ~250 net — near the ordinal's 261–266 but on 9–12 trades/yr. The two are complementary: the
ordinal says "the leg has kept going", speed says "this bar is fast"; stacked, ordinal ≥ 4 ∧ speed < −2% is 14
trades/yr at PF−1 8.2 (MOC, net −6%) / 7.1 (10m, net +9%). ⇒ speed = a sizing tier inside the ordinal (or inside
the mc=5 ladder), not its replacement. The mc=5 structure (§L9) makes the ordinal moot anyway.
