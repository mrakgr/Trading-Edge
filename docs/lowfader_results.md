
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
