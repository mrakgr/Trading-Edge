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

⚠ 2026 H1 only (263 trips is thin). NEXT = the full-modern-era breakdown (Finding 3).

### Finding 3 — full modern era (2020-2026): VOLATILITY is the lever, not slope; the gate adds nothing alone

Gated `run_len ≥ 10`, 2020-2026: **15,054 trips / PF 1.169 / +$238k** — MUCH thinner than the 2026 H1
snapshot (PF 3.00 was a regime slice, not the gate). Critically, the **ungated** book (run_len ≥ 0) is
**161,142 trips / PF 1.166** — nearly identical PF. So `run_len ≥ 10` **adds no edge on its own**; it
just cuts 90% of trips at the same PF. The edge must come from the run's SHAPE. All bucket tables below
are the **gated book** (`run_len ≥ 10`, 15,054 trips); `avg_ret_pct` = mean `ret_moc` %, `pf` = MOC PF.

**`run_atr_v2` (run volatility) — THE lever, clean & monotone.** `<.004` dead → `.03+` PF 1.81 / +5.35%.
Volatile runs resume hard. Same mechanism as V1 F2 ("the volatility IS the edge") and VwapReclaim's
dist/ATR — **volatility is the fuel** across all the systems.

| run_atr_v2 | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <.004 | 7536 | −0.021 | 0.949 |
| .004-.008 | 4718 | −0.049 | 0.944 |
| .008-.015 | 1883 | 0.388 | 1.240 |
| .015-.03 | 679 | 1.139 | 1.367 |
| **.03+** | 238 | **5.353** | **1.806** |

**`run_slope` (log-close %/bar) — real but SECONDARY.** Steeper is better in isolation, BUT it does NOT
stack with volatility (see the 2×2 below).

| run_slope | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <0 | 757 | 0.211 | 1.272 |
| 0 to .001 | 10214 | −0.036 | 0.941 |
| .001-.002 | 2604 | 0.494 | 1.424 |
| .002-.004 | 1059 | 0.540 | 1.259 |
| **.004-.008** | 293 | **2.048** | **1.521** |
| .008+ | 127 | 1.005 | 1.130 |

**Stack — `run_slope ≥ .002` (steep) × `run_atr_v2 ≥ .012` (vol_hi):** volatility DOMINATES; requiring a
steep slope on top of volatility HURTS (steep-but-calm runs are mediocre; forcing steepness excludes the
best pure-volatility trades). Volatile runs are already steep enough.

| steep | vol_hi | n | avg_ret_pct | pf |
|---|---|--:|--:|--:|
| true | true | 961 | 1.176 | 1.313 |
| true | false | 518 | 0.328 | 1.234 |
| **false** | **true** | 418 | **2.892** | **2.151** |
| false | false | 13157 | −0.010 | 0.986 |

**`run_len` — 15-19 is the sweet spot; 50+ DIES (exhaustion).** An upper cap exists → use `run_len < 50`.

| run_len | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| 10-14 | 6831 | 0.072 | 1.081 |
| **15-19** | 3736 | 0.300 | **1.319** |
| 20-29 | 3179 | 0.185 | 1.185 |
| 30-49 | 1209 | 0.203 | 1.191 |
| 50-99 | 99 | −0.593 | 0.380 |

**`run_r2` (trend cleanliness) — INVERTED.** LOW R² (`<.2`) is best; a too-clean straight run is
over-extended, a choppier climb has more left. Weaker than volatility.

| run_r2 | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| **<.2** | 3152 | 0.325 | **1.395** |
| .2-.4 | 2538 | 0.072 | 1.085 |
| .4-.6 | 3197 | 0.116 | 1.130 |
| .6-.8 | 3915 | 0.211 | 1.211 |
| .8-1 | 2252 | −0.010 | 0.991 |

**`run_vol_slope` (is volume rising in the run?) — FLAT/DEAD.** The VOLUME trend is NOT the signal — a
clean negative. (The PRICE volatility is.)

| run_vol_slope | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <−.02 | 5313 | 0.136 | 1.166 |
| −.02 to 0 | 2413 | 0.192 | 1.240 |
| 0-.02 | 2292 | 0.026 | 1.027 |
| .02-.05 | 2463 | 0.272 | 1.259 |
| .05+ | 2573 | 0.181 | 1.154 |

**`entry_vs_run_top` — same shape as volatility (and correlated with it; a volatile run makes a big
re-break, so this ≈ the volatility signal — don't double-count).** `4%+` above the run top = PF 2.03.

| entry_vs_run_top | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| below top | 627 | 0.386 | 1.460 |
| 0-1% | 11747 | 0.016 | 1.025 |
| 1-2% | 1754 | −0.112 | 0.934 |
| 2-4% | 664 | 1.121 | 1.410 |
| **4%+** | 262 | **5.372** | **2.028** |

**`entry_vs_vwap` (buy below vs above VWAP) — mild.** 2-5% above VWAP (PF 1.43) beats just-below (0.96).
Above-VWAP better, but a weaker lever than volatility.

| entry_vs_vwap | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <−2% | 1451 | 0.234 | 1.190 |
| −2 to 0% | 3708 | −0.026 | 0.959 |
| 0-2% | 5719 | 0.024 | 1.039 |
| **2-5%** | 3031 | 0.426 | **1.429** |
| 5%+ | 1145 | 0.621 | 1.209 |

**Candidate cell — `run_atr_v2 ≥ .015 & run_len < 50`: PF 1.561 / 912 trips / 17.2% win / +$206k**,
POSITIVE 6 of 7 years. ~140 trips/year — a stable volatile-run-resumption book: ONE dominant lever (run
volatility) + the exhaustion cap. Exit/stop unchanged (hold-to-MOC + 2-bar-low close-based stop).

| year | n | pf | net |
|---|--:|--:|--:|
| 2020 | 97 | 1.290 | +9,062 |
| 2021 | 236 | 0.904 | −8,999 |
| 2022 | 120 | 1.401 | +14,776 |
| 2023 | 77 | 3.594 | +65,736 |
| 2024 | 156 | 1.463 | +32,584 |
| 2025 | 175 | 1.321 | +26,557 |
| 2026 | 51 | 3.548 | +66,120 |

### Finding 4 — short-term exits (new-high target + time-stops) all LOSE; hold-to-MOC wins (V1 F3 again)

V2 is an intraday scalp, so retested new-high exits + time-stops (2020-2026, on the candidate cell
`run_atr_v2 ≥ .015 & run_len < 50`, 912 trips). Every short-term exit RAISES win rate but LOWERS PF and
net — the textbook signature of cutting winners short. The intraday-ness is in the ENTRY; the P&L lives
in letting the volatile-run resumption run to the close.

| exit | n | avg_ret_pct | win | pf | net |
|---|--:|--:|--:|--:|--:|
| **MOC (baseline)** | 912 | 2.257 | 17.2 | **1.561** | **+205,835** |
| new-high only | 912 | 0.044 | 43.5 | 1.017 | +3,994 |
| time-stop 10m | 912 | 0.526 | 45.8 | 1.290 | +47,973 |
| time-stop 20m | 912 | 0.430 | 40.2 | 1.172 | +39,224 |
| time-stop 30m | 912 | 0.438 | 35.3 | 1.154 | +39,961 |
| new-high + ts10 | 912 | 0.130 | 52.6 | 1.081 | +11,900 |
| new-high + ts20 | 912 | 0.067 | 50.9 | 1.032 | +6,137 |
| new-high + ts30 | 912 | 0.079 | 48.6 | 1.035 | +7,167 |

- **The new-high target is the worst** (PF 1.56→1.02, net +$206k→+$4k) — it caps the +5% resumptions that
  ARE the edge. Same mechanism as V1 F3 and VwapReclaim F13.
- Time-stops are less destructive (ts10 best at PF 1.29 / +$48k) but still leave ~$158k vs MOC on the table.
- ⇒ **Keep hold-to-MOC.** Don't retest short-term exits on this pattern again.

### Finding 5 — the re-break ATR% filter is a SELECTIVITY knob, not the edge; relaxing it fattens the book

The re-break trigger is `close ≥ prevBar.high · (1 + k·ATR%)` (k = `DipRebreakAtr`, `--dip-rebreak-atr`).
Swept k on the candidate cell (`run_atr_v2 ≥ .015 & run_len < 50`, 2020-2026):

| k (re-break ATR mult) | n | avg_ret_pct | pf | net |
|---|--:|--:|--:|--:|
| **0.0** (no expansion req) | 3897 | 1.536 | 1.483 | **+598,706** |
| **0.25** | 1937 | 2.044 | **1.576** | +395,905 |
| 0.5 (current default) | 912 | 2.257 | 1.561 | +205,835 |
| 1.0 | 198 | 7.061 | **2.509** | +139,803 |

k is a **fat-book ⇄ high-PF dial on the SAME edge** — the volatility that matters is captured by
`run_atr_v2` (the RUN's vol), so also requiring the re-break BAR to be an expansion bar is largely
redundant on this cell:
- **k=0 ≈ 4× the trips and ~3× the dollars** (+$206k → +$599k) at a small PF cost (1.56 → 1.48).
- **k=0.25 is the robust sweet spot** — highest PF (1.576) AND 2× the default's trips.
- k=1.0 = a tiny spectacular book (PF 2.51 / +7% avg) but only 198 trips/6yr — too thin.

**Year stability (cell):** 2021 is the system's weak regime at EVERY k (a persistent feature, not a k
artifact). k=0 makes 2021 a −$65k hole; **k=0.25 halves it to −$33k** while every other year stays solidly
positive (2023-2026 all strong). So k=0.25 is the better RISK-ADJUSTED fat book; k=0 maximizes raw dollars
but concentrates the 2021 drawdown.

| year | k=0.0 pf / net | k=0.25 pf / net |
|---|--:|--:|
| 2020 | 1.452 / +62,435 | 1.348 / +25,097 |
| 2021 | 0.802 / **−65,374** | 0.819 / **−33,098** |
| 2022 | 1.047 / +6,094 | 1.259 / +17,447 |
| 2023 | 2.730 / +145,069 | 3.219 / +116,307 |
| 2024 | 1.759 / +162,502 | 1.752 / +91,194 |
| 2025 | 1.771 / +195,386 | 1.706 / +100,546 |
| 2026 | 2.014 / +92,592 | 2.600 / +78,411 |

### Finding 6 — breadth: a mild-strong CHOP zone (.55-.65) is the dead cell; breadth does NOT explain 2021

Joined **prior-day** breadth (`pct_above_20` = frac of stocks above their 20-day MA, from
`data/equity/momentum_v0/breadth.parquet`, `LAG` over date = no lookahead) to the candidate cell
(k=0.25, `run_atr_v2 ≥ .015 & run_len < 50`, 2020-2026, 1,934 trips matched). PF is **non-monotone** with
two strong zones and a dead middle:

| prior-day breadth | n | avg_ret_pct | pf | net |
|---|--:|--:|--:|--:|
| <.30 (washed out) | 277 | 0.366 | 1.103 | +10,136 |
| **.30-.45 (weak)** | 457 | 2.318 | 1.637 | +105,940 |
| **.45-.55 (neutral)** | 285 | 2.772 | 1.791 | +78,989 |
| .55-.65 (mild-strong) | 318 | 0.906 | 1.235 | +28,810 |
| .65-.75 (strong) | 317 | 1.466 | 1.418 | +46,480 |
| **.75+ (ripping)** | 280 | 4.550 | 2.450 | +127,389 |

The pattern works when the market is either **washed-out/reverting** (breadth .30-.55, PF ~1.6-1.8) or
**ripping** (.75+, PF 2.45 / +4.5% avg); the **.55-.65 mild-strong CHOP zone is the dead cell** (PF 1.24).
Simple filters:

| filter | n | avg_ret_pct | pf | net |
|---|--:|--:|--:|--:|
| none (cell, k=0.25) | 1934 | 2.044 | 1.576 | +395,905 |
| **drop .55-.65 chop zone** | 1616 | 2.283 | **1.655** | +368,933 |
| keep only <.55 OR ≥.75 (two peaks) | 1299 | 2.482 | 1.714 | +322,453 |

**Dropping the .55-.65 chop zone is the best trade** — −16% trips, +PF (1.576→1.655), keeps 93% of the
dollars. Keeping only the two peaks lifts PF further (1.71) but sacrifices more.

**Breadth does NOT explain the 2021 hole** (F5): 2021's avg prior-breadth (0.535) matches the GOOD years
(2023 = 0.533, 2026 = 0.525), yet 2021 PF is 0.82 vs 2023's 3.22. 2021's weakness is regime-specific
(the meme-squeeze chop year), not a breadth artifact — a breadth filter won't rescue it.

(User decided F6's drop-.55-.65 breadth filter is NOT worth it — dropping a middle bucket while keeping
both tails is overfit; the breadth breakdown stays informative but no breadth gate is applied.)

### Finding 7 — 9-EMA RECLAIM trigger = the FATTEST book (+$581k), but concentrates the 2021 risk

Alternative entry trigger (`--dip-v2-reclaim`, `DipV2Reclaim`): instead of a RE-BREAK above the prior
bar's high, enter when this bar's close crosses back ABOVE the 9-EMA (the pullback's below-EMA run just
ended). Fires earlier (before price takes out the prior high) and needs no prior-high/ATR%. Candidate
cell (`run_atr_v2 ≥ .015 & run_len < 50`, 2020-2026):

| trigger | n | avg_ret_pct | win | pf | net |
|---|--:|--:|--:|--:|--:|
| re-break k=0.5 (default) | 912 | 2.257 | 17.2 | 1.561 | +205,835 |
| re-break k=0.25 | 1937 | 2.044 | 16.0 | 1.576 | +395,905 |
| **9-EMA reclaim** | 4799 | 1.210 | 13.2 | 1.403 | **+580,638** |

The reclaim is the **far "fat" end of the same fat-book ⇄ PF spectrum as the k-sweep (F5):**
- **~5× the re-break-default trips (4,799) and the MOST dollars of any variant (+$581k)** — earlier entry
  catches more of each resumption.
- **Lower PF (1.40)** — earlier entry admits more marginal signals; per-trade avg +1.21% vs +2.26%. But
  the core edge holds — the run-volatility lever still works (reclaim run_atr `.03+` → PF 1.69, monotone).
- **Amplifies the 2021 hole (−$56k vs k=0.25's −$33k) and turns 2022 flat (−$3k)** — the earlier entry
  makes the bad regime worse, same way k=0 did.

| year | reclaim pf | reclaim net |
|---|--:|--:|
| 2020 | 1.566 | +102,016 |
| 2021 | 0.850 | **−56,301** |
| 2022 | 0.982 | −2,614 |
| 2023 | 2.408 | +141,311 |
| 2024 | 1.669 | +166,049 |
| 2025 | 1.544 | +156,391 |
| 2026 | 1.721 | +73,786 |

⚠ Semantic caveat: with `RunResetBarsBelow = 1`, a SINGLE excused below-close doesn't break the up-run,
so a quick 1-bar-dip-then-reclaim reads the PREVIOUS (stale) saved run, not the just-paused one. Minor
(most pullbacks are ≥2 bars), but if the reclaim trigger is promoted, revisit the tolerance/save timing.

**Summary of the fat-book ⇄ PF dial** (candidate cell, all hold-to-MOC + 2-bar stop):
re-break k=1.0 (PF 2.51 / 198 trips) → k=0.5 (1.56 / 912) → k=0.25 (1.58 / 1,937 / +$396k) → k=0
(1.48 / 3,897 / +$599k) ≈ EMA-reclaim (1.40 / 4,799 / +$581k). **2021 is the weak regime at every point.**

### Finding 8 — the dead volume-slope result HOLDS on the 9-EMA reclaim trigger too

Rechecked `run_vol_slope` on the reclaim book (F7) — the "volume trend doesn't help" finding (F3) is
robust across BOTH triggers:

Full gated reclaim book (`run_len ≥ 10`): PF 0.97 → 1.22 with NO trend (falling-vol `<-.02` is actually
the WORST at 0.97; everything above is a flat ~1.06-1.22).

Candidate cell (reclaim, `run_atr_v2 ≥ .015 & run_len < 50`):

| run_vol_slope | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <−.02 | 1185 | 0.855 | 1.331 |
| −.02 to 0 | 480 | 0.941 | 1.323 |
| 0-.02 | 645 | 1.421 | 1.446 |
| .02-.05 | 1019 | 1.474 | 1.475 |
| .05+ | 1470 | 1.308 | 1.406 |

A hair more shape than the re-break cell (1.33 → 1.47 rising, then dips at the top) but WEAK (0.14 PF
spread, non-monotone) — nothing like the run-volatility lever (0.95 → 1.81).

**Mechanism (why volume slope is dead here):** V2 already conditions on `run_atr_v2` (run volatility), and
a volatile up-run almost always carries elevated/rising volume anyway — so once volatility is selected,
the volume SLOPE adds little independent information. The volatility already "contains" the volume story.
(Would likely matter more in a setup NOT pre-selected on volatility.)

NEXT (for the user): choose the trigger/selectivity point on the dial (robust k=0.25 vs max-$ reclaim/k=0);
the 2021 regime is the standing risk at ALL points (non-breadth); then run_atr/run_len sweeps + 22-yr check.
