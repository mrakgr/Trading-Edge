
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
