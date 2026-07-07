# DipRider V2 — run-above-9EMA slopes (LONG intraday, research mode)

**System:** `TradingEdge.DipRider` with `--diprider-v2` (branch `dip-rider`). V1 is archived & untouched
(see `docs/diprider_results.md`); V2 is the research successor.
**Doc discipline:** journal each finding here AS YOU GO (see `feedback_document_findings_as_you_go`).

## The thesis

V1's cleanest lever was **pullback DEPTH** — shallow dips resume, deep ones are broken trends (V1 F2,
monotone). Restated from the other side: **the longer/stronger the run of bars ABOVE the 9-EMA before
the pullback, the better the resumption.** V2 gates on that run and records its shape (slope, R²,
volume slope, volatility) so we can find which strong runs resume.

## The engine

- **Entry** (long, fill at the re-break close): the SAME re-break trigger as V1 —
  `close ≥ prevBar.high · (1 + k·ATR%)` (k = `DipRebreakAtr`, default 0.5) — but **gates 2-5 are
  DROPPED**. The only entry conditions are: the trading window, price has closed below the 9-EMA for
  ≥ 1 bar (a pullback started), the re-break trigger, and the **min prior-run-length gate** below.
- **Run tracking:** a "run" = consecutive bars closed ABOVE the 9-EMA, tolerating up to
  `RunResetBarsBelow` (default 1) consecutive below-EMA closes before it breaks. On break, the run's
  stats are SAVED (before the OLS objects reset) and read at the next entry.
- **EXIT & STOP = identical to V1** (unchanged): **hold-to-MOC** (no target, no time-stop) with a
  **2-bar-low, close-based protective stop** (min of the re-break bar's low and the prior bar's low;
  V1 Finding 5, the F5 winner). V2 shares V1's `AdvanceDip` exit path.

### Recorded features (the just-ended above-9EMA run + context)

| feature | meaning |
|---|---|
| `run_len` | bars the just-ended run lasted (also the **entry gate**, `DipV2MinRunLen`, default 10) |
| `run_slope` | OLS slope of the run's **log-close** (per-bar log-return = %/bar trend strength) |
| `run_r2` | R² of that log-close fit (trend **cleanliness**: smooth vs choppy) |
| `run_vol_slope` | OLS slope of the run's **log-volume** (per-bar; **is volume rising into the run?**) |
| `run_vol_r2` | R² of the log-volume fit |
| `run_atr_v2` | mean per-bar **log-TR over the run** (the run's own volatility) |
| `run_last_close` / `entry_vs_run_top` | the run's TOP close / `entry ÷ run_top − 1` (pullback depth at entry) |
| `vol_20/10/5/2` | trailing raw volume (exhaustion-cutoff inputs) |
| `vwap_at_entry` / `entry_vs_vwap` | VWAP + `entry ÷ VWAP − 1` (buy-below vs buy-above-VWAP split) |

**Implementation notes:** OLS via `OlsSlopeMa` in `TradingEdge.RollingMa` (closed-form slope + R²,
verified to 1e-9). Zero-volume bars are skipped from BOTH the price and volume OLS **together**, so the
two regressions stay on the identical push-index x-axis (`run_slope` and `run_vol_slope` remain directly
comparable) — user decision. `run_len` still counts a skipped bar (it's part of the run in wall-clock
time), so `run_len` can exceed the OLS point count on a 0-vol day.

---

## Findings

### Finding 1 — ungated V2 already beats gated V1; prior-run-length is U-SHAPED

Dropping gates 2-5 and taking every re-break-after-a-pullback (2026 H1) = **3,452 trips / PF 1.92**
(vs V1's gated 871 trips / PF 1.04 on the same window). The raw entry is already better ungated.

Breaking down by the just-ended run's length reveals a **non-monotone U-shape** (2026 H1):

| prior run length | n | avg ret | PF |
|---|---|---|---|
| len 1 (no real run) | 2,476 | +1.65% | 1.96 |
| 2-4 | 422 | +0.67% | 1.42 |
| 5-9 | 291 | +0.46% | 1.28 |
| **10-19** | **189** | **+4.22%** | **3.43** |
| 20-39 | 66 | +2.22% | 2.35 |
| 40+ | 8 | −2.86% | 0.03 |

Two SEPARATE edges with a dead zone between: (a) the **immediate re-break** (len 1, no measurable run
— vindicates the "maybe just buy the re-break" aside), and (b) the **long-sustained-run resumption**
(len 10-19, the best cell). A *medium* run (2-9) is the WORST. The 40+ tail dies (exhaustion), but tiny n.
Most entries (2,476 / 3,452) have `run_len = 1` — a 1-bar OLS = NaN slope/R² (correct), so the
slope/R²/volume features only exist on the minority with a real run.

### Finding 2 — the `run_len ≥ 10` gate isolates the good cell → PF 3.00

Adding `DipV2MinRunLen = 10` (require the prior above-9EMA run ≥ 10 bars) — 2026 H1:
**263 trips / PF 3.00 / 18.3% win / +$92k.** Clean: min run len = 10, zero NaN slopes (≥10 OLS points),
avg log-close slope +0.167%/bar, avg `run_r2` 0.49, avg `run_atr_v2` 0.0105.

- **`entry_vs_run_top` avg +1.27%, and only 3.8% of entries are BELOW the run's top** — the re-break
  entries are almost always ABOVE where the prior run topped out: shallow-pullback continuations that
  already cleared the old high, not deep-dip buys. Consistent with V1's "shallow resumes" lesson.

**Exit/stop are still V1's** (hold-to-MOC + 2-bar-low close-based stop) — untouched in V2.

⚠ 2026 H1 only (263 trips is thin). NEXT = the full-modern-era breakdown of `run_slope` / `run_r2` /
`run_vol_slope` / `run_atr_v2` / `entry_vs_run_top` to find which strong runs resume; then a 22-year
regime check. The `run_len` sweep (is 10 the right floor? is there an upper cap like V1's?) is open.
