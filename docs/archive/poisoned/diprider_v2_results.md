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

### Finding 9 — pullback depth is ~FLAT on V2; V1's "shallower = better" does NOT transfer (CORRECTED)

⚠ **This finding was initially reported WRONG and is corrected here.** The first pass claimed
`bars_below_ema` "maxes out at 3" and that the `RunResetBarsBelow = 1` tolerance was an "implicit
pullback-depth cap" — both FALSE. That cap was an artifact of conditioning on the `run_len ≥ 10` gate +
the candidate cell, NOT a property of the engine. (The user correctly disbelieved "no pullbacks deeper
than 3 bars in 6 years.")

**Deep pullbacks are everywhere.** On the UNGATED book (`--dip-v2-min-run-len 0`), `bars_below_ema` runs a
full smooth distribution out to **55 bars** below the 9-EMA. The tolerance does NOT cap depth: `barsBelowEma`
counts consecutive below-EMA closes (resets on any close ≥ EMA), and the entry fires on the reclaim/re-break
bar, so it reads the FULL preceding below-streak.

**And pullback depth is ~FLAT vs outcome** (ungated full modern book, 2020-2026):

| bars_below_ema | re-break PF | reclaim PF |
|---|--:|--:|
| 1 | 1.181 | 1.106 |
| 2 | 1.194 | 1.157 |
| 3-4 | 1.187 | 1.142 |
| 5-7 | 1.206 | 1.208 |
| 8-14 | 1.095 | 1.111 |
| 15-29 | 1.145 | 1.075 |
| 30+ | **1.293** | 1.213 |

No monotone "shallower = better" — PF hovers ~1.1-1.2 across ALL depths, and the DEEPEST bucket (30+) is
actually the best on re-break (1.29). **V1 F2's "deeper = broken trend" does NOT transfer to V2.**

Why the divergence: V1 measured depth on ITS OWN gated book; that "deeper = worse" was real for V1's
population but is NOT a universal law. V2 selects on **run VOLATILITY** (F3), and once you're in the V2
book, pullback depth is roughly ORTHOGONAL to outcome. The "shallow wins" pattern that appeared when
conditioning on `run_len ≥ 10` + the cell was a SELECTION EFFECT (strong long runs happen to resume
shallow), not the depth mechanism. **Volatility, not depth, is V2's lever.**

**User's open idea (untested):** skip the re-break/reclaim entirely and just BUY 1/2/3 bars into a
pullback (no resumption trigger). Worth a direct test given depth is flat — the trigger may matter more
than the depth.

### Finding 10 — dedicated `bars_since_break` counter (true pullback age); re-break dies at 20+ bars

Per user: `bars_below_ema` (raw consecutive below-EMA streak, resets on ANY close ≥ EMA) conflates the
pullback age with the OLS-reset logic. Added a DEDICATED `barsSinceBreak` counter (separate from
`barsBelowEma` and the tolerance counter `runBelowStreak`): starts at 0 the bar the above-EMA run BREAKS,
then counts EVERY bar until entry — **surviving above-EMA blips** (does not reset on a pop back above the
EMA). This is the true "bars since the uptrend broke." Recorded as `bars_since_break`; gate-ready via
`DipV2Min/MaxBarsSinceBreak` (`--dip-v2-min/max-bars-since-break`, both OFF by default).

It captures cases `bars_below_ema` misses: e.g. a pullback that chops around the EMA for 9 bars reads
`bars_below_ema = 1` (last blip) but `bars_since_break = 9` (true age). Breakdown (ungated full modern):

| bars_since_break | re-break PF | reclaim PF |
|---|--:|--:|
| 0 | 1.156 | 1.148 |
| 1 | 1.172 | 1.144 |
| 2 | 1.125 | 1.026 |
| 3-4 | 1.252 | 1.096 |
| 5-9 | 1.145 | 1.132 |
| 10-19 | **1.359** | 1.158 |
| 20+ | **0.749** | 1.045 |

- **Re-break: mostly flat ~1.1-1.25, EXCEPT a sharp cliff at 20+ (PF 0.75)** — a re-break that takes 20+
  bars to develop is a dead/broken trend. `10-19` is the best (1.36). So there's a clear UPPER cap
  (`< 20`), but no "shallower = better" at the front.
- **Reclaim: flat (1.03-1.16)** — the earlier entry washes out the age effect; no 20+ cliff.

Cleaner than `bars_below_ema` (F9): exposes the 20+ re-break cliff the raw streak obscured. A
`bars_since_break < 20` cap is a candidate re-break gate (gate-ready, not yet applied).

### Finding 11 — ⭐ BUY INTO THE PULLBACK (no resumption trigger) is the FATTEST + BEST book — $1M+ / all years +

User's idea: skip the re-break/reclaim entirely and just BUY the Nth bar into the pullback, WHILE STILL
BELOW the 9-EMA (fill at the Nth below-EMA bar's close). Added `DipV2PullbackBar = N`
(`--dip-v2-pullback-bar N`, overrides the trigger). On the candidate cell (`run_atr_v2 ≥ .015 &
run_len < 50`, 2020-2026):

| entry | n | avg_ret_pct | win | pf | net |
|---|--:|--:|--:|--:|--:|
| re-break k=0.5 | 912 | 2.257 | 17.2 | 1.561 | +205,835 |
| reclaim | 4799 | 1.210 | 13.2 | 1.403 | +580,638 |
| buy N=1 | 5111 | 0.899 | 8.9 | 1.416 | +459,410 |
| **buy N=2** | 8896 | 1.171 | 10.0 | 1.510 | **+1,041,393** |
| **buy N=3** | 7005 | 1.168 | 10.1 | 1.517 | +818,124 |

**Buy N=2 does >$1M net — 5× the re-break's dollars — at essentially the same PF (1.51 vs 1.56).** N=3 is
a near-tie (1.52 / +$818k). You do NOT need the resumption trigger: on a VOLATILE run (the selected cell),
a shallow 2-3 bar dip reliably bounces, and entering while still below the EMA gets a BETTER price than
waiting for the re-break/reclaim confirmation. The confirmation was costing the entry discount. Win rate
is lower (10% vs 17% — more trades that don't work) but the winners start cheaper, so PF holds and the
dollars explode.

**Buy N=2 is POSITIVE EVERY YEAR — including 2021** (PF 1.03 / +$16k), the weak regime that was NEGATIVE
at every trigger-based variant. Buying the dip instead of the confirmation even (marginally) fixes 2021.

| year | n | pf | net |
|---|--:|--:|--:|
| 2020 | 1075 | 1.599 | +144,096 |
| 2021 | 2669 | 1.029 | +15,946 |
| 2022 | 1082 | 1.299 | +64,556 |
| 2023 | 622 | 2.081 | +149,467 |
| 2024 | 1347 | 1.941 | +324,577 |
| 2025 | 1523 | 1.720 | +281,991 |
| 2026 | 578 | 1.384 | +60,759 |

⚠ Caveat: at N=1 (and N=2 when tolerance=1) the above-EMA run may not have BROKEN yet, so `run_len` /
`run_atr_v2` read the PREVIOUS saved run for some entries — revisit the tolerance/save timing if N is
promoted. Also: full-book (ungated) PF rises with N (1.12/1.19/1.21 for N=1/2/3) — the cell filter is
doing real work; buy-into-pullback is not free money without the volatility gate.

**⭐ NEW LEADING CANDIDATE: buy N=2 (or N=3) into the pullback + `run_atr_v2 ≥ .015 & run_len < 50`,
hold-to-MOC, 2-bar stop → PF ~1.51 / +$1.0M / positive every year.** NEXT: sweep N further (4,5?),
re-verify the run-feature breakdowns under this entry, address the stale-run caveat, then 22-yr check.

### Finding 12 — ⭐⭐ the 2-bar stop TRIPS on the bought dip; a run-geometry stop ~TRIPLES net → +$2.9M

User's call: buying INTO a falling dip with a 2-bar-low stop gets knifed — the 2-bar low sits right under
a still-falling entry. Confirmed: **buy-N=2 with the 2-bar stop STOPS OUT 89.8% of trades** (only 10.2%
reach MOC). Borrowed VwapReclaim's structure-anchored geometry (`--dip-v2-geom-stop`): save the run's MIN
close (`savedRunMinClose`, the run floor) and its top (`savedRunLastClose`); `d = top − floor` (run range);
**stop = floor − d·(2/3)** (below the run's floor, VwapReclaim F14). Falls back to the 2-bar low if the
saved run closes are missing. Candidate cell (buy N=2, 2020-2026):

| stop | stop-out % | win | pf | net |
|---|--:|--:|--:|--:|
| 2-bar low | 89.8 | 10.0 | 1.510 | +1,041,393 |
| **geometry d·2/3** | 52.9 | 33.1 | **1.694** | **+2,916,402** |

**Stop-out 90%→53%, win 10%→33%, PF 1.51→1.69, net NEARLY TRIPLES to +$2.9M** (same trips — the stop only
changes exits). The 2-bar low was a noise-level stop under a still-falling dip; anchoring d·2/3 below the
RUN's floor gives the reversion room to happen. **This is the single biggest lift in the whole V2 line.**

**Positive EVERY year, and it FIXES 2021** (the weak regime): 2021 PF 1.13 / +$158k (was +$16k with the
2-bar stop; negative at every trigger variant). 2024-26 are huge (+$735k/+$779k/+$416k):

| year | pf | net |
|---|--:|--:|
| 2020 | 1.704 | +339,962 |
| 2021 | 1.126 | +157,959 |
| 2022 | 1.406 | +191,553 |
| 2023 | 2.104 | +297,511 |
| 2024 | 2.119 | +734,705 |
| 2025 | 2.014 | +778,542 |
| 2026 | 2.377 | +416,169 |

**Stop-frac sweep — a broad plateau** (d/2 → d·1.5, PF 1.65-1.70); d·2/3 is a fine default, wider (`d`)
squeezes a touch more net (+$3.04M) at ~same PF:

| frac | stop-out % | pf | net |
|---|--:|--:|--:|
| d/2 | 56.1 | 1.702 | +2,857,109 |
| d·2/3 | 52.8 | 1.695 | +2,921,985 |
| d | 47.7 | 1.689 | +3,037,238 |
| d·1.5 | 41.3 | 1.652 | +3,042,095 |

**⭐⭐ LEADING SYSTEM NOW: buy N=2 into the pullback + `run_atr_v2 ≥ .015 & run_len < 50` + run-geometry
stop (d·2/3, floor = run min-close) + hold-to-MOC → PF 1.69 / +$2.9M / positive every year incl. 2021.**
NEXT: promote to defaults; sweep N (2 vs 3) under the geom stop; re-verify run-feature breakdowns; the
stale-run caveat (N=2 at tol=1); then the 22-yr regime check.

### Finding 13 — BUY INTO THE RUN (momentum, no pullback): a 2nd independent path to ~$3M / all years +

Instead of waiting for the run to break, buy INTO the live above-9EMA run when its length first reaches N
(`--dip-v2-buy-into-run N`, fires once/run; NOT a pullback). Live-run GEOMETRY stop (floor = live run's
min-close; d = live top − floor; stop = floor − d·2/3). The live run's len/slope/R²/ATR/%gain are recorded.

**Length sweep (full gated book, live-run geom stop, 2020-2026) — longer run = higher PF, monotone:**

| N | n | avg_ret_pct | win | stop-out % | pf | net |
|---|--:|--:|--:|--:|--:|--:|
| 3 | 282607 | 0.153 | 19.2 | 85.4 | 1.200 | +4,321,993 |
| 5 | 196516 | 0.221 | 22.2 | 78.4 | 1.218 | +4,336,342 |
| 8 | 130504 | 0.323 | 27.1 | 69.0 | 1.253 | +4,215,352 |
| 10 | 101680 | 0.369 | 30.3 | 62.8 | 1.259 | +3,748,633 |
| 15 | 55445 | 0.528 | 37.3 | 47.9 | 1.314 | +2,930,012 |
| 20 | 30259 | 0.612 | 41.9 | 35.6 | 1.331 | +1,852,098 |

Buying into a LONGER (more proven) run = higher PF + per-trade avg (PF 1.20→1.33), fewer trips. Total
dollars peak at N=5-8 (~$4.3M ungated). A much bigger book than buy-into-dip (196k trips at N=5).

**Post-hoc breakdowns (N=8) — all three live-run features MONOTONE, same "strong run resumes" story
(and heavily correlated → they don't all stack):**

_run volatility `run_atr_v2`:_ `<.004` PF 1.03 → `.015-.03` 1.55 → **`.03+` 1.79 / +5.76%.**
_run %gain (entry/floor−1):_ `<.5%` PF 1.07 → **`8%+` 1.98 / +7.97%.**
_run OLS slope:_ `0-.001` PF 1.07 → `.004-.008` 1.52 → **`.008+` 1.79 / +5.64%.**

Volatility is the through-line across the ENTIRE V2 line — pullback AND momentum entries alike.

**Leading buy-into-run cell — N=8 + `run_atr_v2 ≥ .015`: PF 1.633 / 10,098 trips / 29% win / +$2.99M,
POSITIVE every year** (2021 PF 1.11 / +$151k; 2024 +$836k). A PEER to the buy-into-dip system (F12,
+$2.9M / PF 1.69) — a SECOND, independent entry path to the same ~$3M edge, with slightly more trips and
slightly lower PF.

| year | pf | net |
|---|--:|--:|
| 2020 | 1.731 | +388,228 |
| 2021 | 1.113 | +151,051 |
| 2022 | 1.305 | +145,036 |
| 2023 | 2.141 | +352,033 |
| 2024 | 2.066 | +836,413 |
| 2025 | 1.698 | +643,047 |
| 2026 | 2.288 | +478,085 |

**Two ~$3M systems now: (a) buy-into-DIP N=2 + geom stop (F12, PF 1.69); (b) buy-into-RUN N=8 + geom stop
(F13, PF 1.63).** NEXT (user): sweep N under the volatility cell for buy-into-run; compare/combine the two
paths (do they fire on different days?); the stale-run caveat; 22-yr check.

### Finding 14 — volume slope is DEAD on pullbacks (F8) but a LIVE, STACKABLE lever on buy-into-run

(Fix: buy-into-run was recording the SAVED (prior) run's `run_vol_slope`, not the live run's — corrected
to snapshot the LIVE run's log-volume OLS. The F13 breakdowns used the live price slope/ATR already; only
the volume slope was wrong. Re-ran N=8.)

Unlike the pullback book (F8, volume slope flat/dead), on buy-into-run the LIVE run's volume slope IS a
signal (full N=8 book): `.05+` (strongly rising volume) → PF **1.388 / +0.62%**, up from the ~1.1-1.2
middle. ⚠ But that 1.388 is a BLEND — see the 2×2: it's the mix of a small volatile subset (PF 1.84) and
the large non-volatile bulk (PF 1.13). Rising volume ALONE (holding volatility low) is only ~1.13.

| run_vol_slope | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| <−.02 | 59877 | 0.167 | 1.160 |
| −.02-0 | 8168 | 0.224 | 1.185 |
| 0-.02 | 8018 | 0.092 | 1.070 |
| .02-.05 | 11365 | 0.259 | 1.201 |
| **.05+** | 43076 | 0.618 | **1.388** |

**And it STACKS with volatility** (2×2) — NOT redundant, unlike on the pullback book:

| rising vol (≥.05) | volatile (atr≥.015) | n | avg_ret_pct | pf |
|---|---|--:|--:|--:|
| ✓ | ✓ | 4629 | 4.483 | **1.837** |
| ✗ | ✓ | 5469 | 1.680 | 1.408 |
| ✓ | ✗ | 38447 | 0.153 | 1.134 |
| ✗ | ✗ | 81959 | 0.077 | 1.084 |

Rising-vol + volatile (PF 1.84) clearly beats volatile-alone (1.41). But note: rising-vol ALONE (✓/✗ =
1.134) is barely above the ✗/✗ baseline (1.084) — **volume slope is NOT a standalone edge; it's a
CONDITIONING lever that only pays inside the volatility cell** (1.41 → 1.84). The full-book 1.39 for
`.05+` was pulled up by the volatile minority; don't read it as "rising volume ≈ 1.39 on its own."
**Mechanism:** a PULLBACK entry buys
AFTER the run paused — its volume story is stale (the fuel ran out, that's why it pulled back). A
buy-into-RUN entry buys the run WHILE LIVE, so rising volume = the move is still being fueled RIGHT NOW
(accumulation in progress) — exactly the confirmation a momentum entry wants. So volume-slope-is-dead
(F8) and volume-slope-is-live (F14) are BOTH correct, distinguished by pullback-vs-momentum entry timing.

**Stacked cell — N=8 + `run_atr_v2 ≥ .015 & run_vol_slope ≥ .05`: PF 1.837 / 4,629 trips / 34% win /
+$2.08M, POSITIVE every year** (2021 PF 1.11; 2026 PF 3.16). Higher PF than the volatility-only cell (1.84
vs 1.63) at fewer trips (4.6k vs 10k) — rising volume is a real stackable momentum lever.

| year | pf | net |
|---|--:|--:|
| 2020 | 1.937 | +258,013 |
| 2021 | 1.112 | +78,622 |
| 2022 | 1.581 | +142,933 |
| 2023 | 2.850 | +307,499 |
| 2024 | 2.289 | +537,029 |
| 2025 | 1.726 | +354,338 |
| 2026 | 3.158 | +396,677 |

### Finding 15 — RUN-INDEPENDENT ATR% + vol-slope (trailing-20): cleaner at short N; run measures win at N>20

The run-based `run_atr_v2` / `run_slope` / `run_vol_slope` are estimated over only N bars — at N=8 that's an
8-point OLS/mean, noisy. Added run-INDEPENDENT trailing-20-bar measures (fixed window, fed EVERY bar, NOT
reset at run breaks): `intraday_atr_pct_at_entry` (the trailing-20 log-ATR — ALREADY existed, we just
weren't slicing on it) + NEW `trail_vol_slope` (trailing-20 OLS log-volume) and `trail_slope` (log-close).
The ATR pair correlates 0.96 with the run version (ATR is stationary); the VOLUME-slope pair correlates
only **0.32** — the trailing window is a genuinely different, broader estimate.

**At short N (8) the trailing measures are as good, and the trailing VOL-slope is CLEANER** (monotone, no
dead middle): `<-.02` PF 1.21 → `.05+` **1.41** (vs the run-based vol-slope which dipped to 1.03 mid-range,
F14). Trailing ATR% matches the run ATR (`.03+` PF 1.75 vs 1.79).

**Crossover rule (user): use the RUN measures for N > 20, the trailing measures for shorter runs.** Verified
— cell = `atr ≥ .015 & vol_slope ≥ .05`:

| N | run-based cell PF (net) | independent cell PF (net) |
|---|--:|--:|
| 8 | 1.837 (+$2.08M) | 1.898 (+$0.62M) |
| 30 | **2.168** (+$190k) | 1.673 (+$108k) |

At N=8 the independent cell is a hair higher PF but far fewer trips; at N=30 the RUN measure clearly WINS
(PF 2.17 vs 1.67) — once the run is 30 bars its own 30-point OLS/ATR is both reliable AND more relevant
(describes the exact run bought), uninfluenced by pre-run bars. So: **short run → trailing-20; long run
(>20) → run's own.**

**Run-length sweep on the INDEPENDENT cell (`intraday_atr_pct ≥ .015 & trail_vol_slope ≥ .05`) — longer
helps, peaks at N=20:**

| N | n | avg_ret_pct | pf | net |
|---|--:|--:|--:|--:|
| 8 | 1189 | 5.21 | 1.898 | +619,437 |
| 15 | 1892 | 6.09 | 1.914 | +1,152,293 |
| **20** | 1113 | 7.87 | **2.170** | +875,466 |
| 30 | 264 | 4.08 | 1.673 | +107,712 |

**N=20 is the sweet spot (PF 2.17, +$875k)** — going beyond 8 genuinely helps (8 was too short, as the user
suspected); N=30 thins out (264 trips) and is the regime to switch to the run measures (which hold 2.17
there). So the leading momentum config is ~**N=15-20 + trailing-20 volatility & vol-slope cell** (PF 1.9-2.2),
crossing over to run-based measures past N=20. **N=20 LOCKED as the `--diprider-v2` default (`DipV2BuyIntoRun = 20`).**

### Finding 16 — full 22-year test (2003-2026): PF 2.17, edge holds in BOTH eras, but 93% modern by trip count

Ran the locked config (buy-into-run N=20) over the full 22 years, cell = `intraday_atr_pct ≥ .015 &
trail_vol_slope ≥ .05`:

| era | n | avg_ret_pct | pf | net |
|---|--:|--:|--:|--:|
| **full 22y (2003-2026)** | 1193 | 7.71 | **2.167** | +919,808 |
| pre-2020 (2003-2019) | 80 | 5.54 | 2.126 | +44,342 |
| modern (2020-2026) | 1113 | 7.87 | 2.170 | +875,466 |

**The edge is NOT regime-dependent** — PF ~2.1 in BOTH eras (unlike VwapReclaim, which was flat/PF~1.0
pre-2020). But it's overwhelmingly a **modern book by TRIP COUNT: 93% of trips are post-2020** (~5/yr
pre-2020 vs ~160/yr modern). The reason is the UNIVERSE, not the pattern — the `rvol_0945 > 1 & ADV ≥ $30M`
in-play filter rarely triggered in 2003-2019 (far fewer high-volume small-caps in play then), so few
candidate days qualified — but the few that did worked just as well (pre-2020 PF 2.13 on 80 trips).

**By year (modern):** every year PF 1.7-4.0 EXCEPT **2021 (PF 0.82 / −$41k)** — the one persistent losing
regime seen throughout V2 (the meme-squeeze chop year; NOT breadth-explained, F6). Pre-2020 years have
tiny samples (1-13 trips) with wild individual PFs (statistically meaningless singly; the aggregate 2.13
is the signal). 2024-26 are the strongest (PF 3.26 / 3.36 / 4.01).

| yr | n | pf | net |   | yr | n | pf | net |
|---|--:|--:|--:|---|---|--:|--:|--:|
| 2020 | 151 | 1.722 | +65,129 | | 2023 | 90 | 2.497 | +86,069 |
| 2021 | 286 | 0.824 | −41,347 | | 2024 | 181 | 3.256 | +269,817 |
| 2022 | 155 | 2.073 | +97,198 | | 2025 | 183 | 3.358 | +246,927 |
|   |   |   |   | | 2026 | 67 | 4.005 | +151,673 |

**⭐⭐ SETTLED momentum system: buy-into-run N=20 + trailing-20 cell (`intraday_atr_pct ≥ .015 &
trail_vol_slope ≥ .05`) + geometry stop + hold-to-MOC → 22y PF 2.17 / +$920k / edge in both eras / positive
every modern year except 2021.** (Full ungated N=20 book, 22y = PF 1.244 / 76,395 trips / +$2.22M.)

NEXT (for the user): still-open — the 2021 regime (the only recurring hole); wire the cell as ENGINE gates
(currently post-hoc SQL); compare/combine vs the buy-into-DIP system (F12); the stale-run caveat.

### Finding 17 — LOW FLOAT helps (edge dies > $1B); same low-float lesson as HighFlyerV2 / LowFlyer

Float breakdown on the N=20 cell (modern, ASOF `known_date ≤ trade_date`, as-reported dollar-float from
SEC `float_sec`; 71% coverage):

| dollar-float | n | avg_ret_pct | win | pf | net |
|---|--:|--:|--:|--:|--:|
| <$50M | 503 | 7.87 | 43.7 | 2.162 | +395,618 |
| $50-150M | 131 | 7.51 | 44.3 | 2.223 | +98,391 |
| $150-300M | 91 | 7.33 | 51.6 | **2.513** | +66,705 |
| $300M-1B | 50 | 1.32 | 50.0 | 1.284 | +6,610 |
| $1-5B | 16 | −2.73 | 25.0 | **0.535** | −4,369 |
| $5B+ | 12 | −1.46 | 50.0 | 0.725 | −1,749 |
| no-float (uncovered) | 310 | 10.14 | 40.0 | 2.285 | +314,260 |

**The edge is entirely in LOW-FLOAT (<$300M) names; it DIES above $1B** (PF 0.5-0.7). Mechanism: a
sustained volatile run in a supply-constrained low-float name is a squeeze that keeps going; large-caps
are too liquid to sustain intraday momentum. SAME lesson as HighFlyerV2 & LowFlyer (float<$300M → big PF).
A `float < $300M` gate cuts the dead >$1B tail (78 trips, PF ~0.7-1.3) at ~no cost. ⚠ Keep the "no-float"
uncovered names (PF 2.29 — they behave low-float); cut only the KNOWN-large tier. (Used as-reported
float_usd, not re-anchored to entry price — sharpens tiers but won't flip the direction.)

### Finding 18 — the pre-2020 sparsity is NOT a data bug: the SETUP is genuinely rarer, universe is full

User's concern: are all the intraday systems post-2020-dependent because of a DATA problem? **No — the
data is sound.** The candidate universe (`vwap_reclaim_candidate`, ADV≥$30M & rvol_0945>1) has
**1,500-2,500 qualifying ticker-days EVERY year back to 2004** (2010: 2,490; 2014: 2,557) — NOT sparse
pre-2020. So the ~5-trips/yr pre-2020 (F16) is not missing data.

What IS post-2020 is the SETUP frequency: DipRider's cell needs **20 consecutive minutes of a run that is
both VOLATILE and VOLUME-EXPANDING** — that specific microstructure was genuinely uncommon pre-2020 (only
80 cell-trips in 17 years despite ~30k candidate-days). It's a real market-structure change, not a gap:
sustained parabolic 1-minute intraday runs simply persist MORE now. (Supports the "more big runners now"
folklore heard for the short side — it's frequency, not existence.)

Telling detail: **2021 had 8,639 candidate-days — 2.7× any other year** (the meme-stock flood), yet 2021
is the WORST modern PF (0.82). More in-play names ≠ better; 2021 was volume-driven CHOP, not clean
momentum — which is exactly why the user's next step (avoid trading when the BROADER MARKET is
down-on-the-day) targets it: 2021's problem is market-regime, not universe or data.

NEXT (user, in order): (1) add a "broader market down-on-the-day" feature/gate to fix 2021; (2) the
float<$300M gate (cut known-large only); then wire the cell as engine gates + the stale-run caveat.

### Finding 19 — broader-market (SPY) context feature BUILT (reusable); but SPY does NOT explain 2021

Built a reusable **market-context** layer: the engine takes a per-day `SetMarketCtx` lookup
(`etMin → struct(SPY %-from-open, SPY %-from-prev-close)`), built once per date from SPY's own 1m bars +
raw daily prev-close (`buildMarketCtx` in Backtest.fs), shared across ALL tickers, snapshotted at entry as
`mkt_chg_open` / `mkt_chg_prev`. NOT folded into per-ticker state — pure read-only context, no-lookahead
(value at et_min uses only SPY bars through et_min). **Reusable by every intraday system** (the design goal).
Verified: same (date, et_min) → identical SPY value across tickers; evolves through the day; 0 NaN.

**But SPY does NOT explain 2021 — the simplest regime hypothesis is RULED OUT:**

_By SPY %-from-open at entry (N=20 cell):_ PF flat-to-inverted — SPY < −1% → **PF 3.08**, flat → 2.34,
SPY > +1% → 1.78. Works fine (even best) when the market is DOWN hard.

_By SPY %-from-prev-close (day direction):_ same — day < −1.5% → PF 2.79, flat → 2.32, day > +1.5% → 2.62.
No "down market = bad."

_2021 diagnosis:_ 2021's avg SPY-from-open (−0.009%) and from-prev (+0.039%) are UNREMARKABLE — in line
with the good years (2024 open −0.002%; 2020 prev +0.047%). **2022 had the MOST negative SPY backdrop**
(open −0.073%, prev −0.096%) yet 2022 was a GOOD year (PF 2.07). So 2021's hole is NOT a down-market
effect — it's a CHOP regime (failed follow-through) that neither the intraday nor daily SPY move captures.

Mechanistic read: DipRider buys an INDIVIDUAL name's volatile run — a stock ripping on its own catalyst is
largely indifferent to SPY minute-to-minute. So SPY %-move is a weak conditioning variable for this pattern
(it may matter more for index-correlated setups). 2021 likely needs a name-level whipsaw/chop measure or
SPY's MULTI-DAY trend/vol, not the single-day move. The feature stays (cheap, reusable, recorded); it's
just not the 2021 fix.

NEXT (user): 2021 still open — try SPY multi-day trend / realized-vol, or a name-level follow-through
measure; the float<$300M gate; wire the cell as engine gates; the stale-run caveat.

### Finding 20 — 2021 = a WIN-RATE collapse (2× stop-outs), NOT a payoff problem; moderate tail concentration

**Tail concentration (N=20 cell, all modern):** top 1 trade = 6% of net, top 10 = 34%, top 25 = 59%, top
50 = **87%**. Top-heavy but NOT fragile — far healthier than DipRider V1 (top 100 = 117% there, i.e. the
body lost money). Here the body is genuinely profitable with a fat right tail; the geometry stop +
volatility selection produce a real edge, not a lottery.

**2021 anatomy — the failure mode is a WIN-RATE collapse, payoff structure is NORMAL:**

| yr | win% | avg_win | avg_loss | W/L | stop-out% | pf |
|---|--:|--:|--:|--:|--:|--:|
| 2020 | 45.7 | +22.5 | −11.0 | 2.05 | 11.9 | 1.72 |
| **2021** | **32.5** | +20.9 | −12.3 | 1.70 | **26.9** | **0.82** |
| 2022 | 43.2 | +28.0 | −10.3 | 2.72 | 22.6 | 2.07 |
| 2023 | 43.3 | +36.8 | −11.5 | 3.20 | 18.9 | 2.50 |
| 2024 | 49.2 | +43.8 | −13.0 | 3.37 | 16.0 | 3.26 |
| 2025 | 53.0 | +36.3 | −12.2 | 2.98 | 11.5 | 3.36 |
| 2026 | 44.8 | +67.4 | −13.6 | 4.94 | 28.4 | 4.01 |

2021's **win rate craters to 32.5%** (every other year 43-53%), and its **stop-out rate DOUBLES to 26.9%**
(good years ~12%). But avg win (+20.9%) and avg loss (−12.3%) are NORMAL. So 2021 isn't "winners got
smaller / losses got bigger" — **the runs simply followed through ~2× less often** (reverted into the stop).
The winners that worked paid normally; there were just far fewer of them. **This is the chop-regime
signature** — and it's a per-NAME follow-through failure (why SPY-direction missed it, F19).

(2026 also has a high stop-out 28.4% but is a great year — its winners were huge, +67% avg, overwhelming
the stops; small n=67. In 2021 the winners were normal-sized, so the extra stops weren't compensated.)

**⇒ A 2021 fix must measure FOLLOW-THROUGH / chop, not direction:** e.g. a name-level "recent breakouts
failing today" signal, a market-wide breadth-of-follow-through, or SPY realized-VOL (chop = high whipsaw
vol). Direction (SPY up/down) is confirmed irrelevant (F19). NEXT (user): the float<$300M gate; a
follow-through/chop measure for 2021; wire the cell as engine gates.

### Finding 21 — 1d-return (`chg_1d`) is a strong MONOTONE lever (book → PF 2.40); still NOT the 2021 fix

`chg_1d` = entry px / prev daily close − 1 = the stock's total move (gap + intraday-so-far) into the
entry. Breakdown on the N=20 cell (modern) — **cleanly MONOTONE, "more up on the day = better":**

| 1d return into trade | n | avg_ret_pct | win | pf | net |
|---|--:|--:|--:|--:|--:|
| <0% (down on day) | 89 | −1.61 | 37.1 | **0.752** | −14,317 |
| 0-5% | 43 | −0.83 | 37.2 | 0.855 | −3,556 |
| 5-10% | 55 | 2.07 | 43.6 | 1.431 | +11,359 |
| 10-20% | 132 | 2.95 | 39.4 | 1.583 | +38,894 |
| 20-40% | 229 | 4.21 | 40.6 | 1.758 | +96,313 |
| **40%+** | 563 | 13.37 | 47.2 | **2.711** | +752,870 |

The two LOSING buckets are flat/down-on-the-day (`<5%`, PF 0.75-0.86, both net negative); everything ≥5%
is profitable & rising. Same "buy strength" logic at the DAILY scale — a volatile intraday run in a stock
already up 40%+ is a genuine leader continuing; the same run in a flat/red name is a fakeout. Winning
buckets don't just win more often (~40-47% throughout) — their avg return is far bigger (+13.4% at 40%+).

**As a book filter, `chg_1d ≥ 10%` is a KEEPER:** lifts the cell PF **2.17 → 2.40** at +$888k (vs +$920k) —
cuts the flat/down fakeouts nearly for free; every year improves EXCEPT 2021.

**But it does NOT fix 2021** (stays PF 0.79 / −$44k). And the diagnostic proves 2021 was NEVER a
daily-weakness problem: 2021's avg `chg_1d` = 48.7% and only 15.4% of entries were weak (<10%) — BETTER
than 2020 (21.2% weak) and 2022 (25.2% weak), both profitable. So 2021's names were strong daily gainers
that still reverted INTRADAY. This SHARPENS F20: 2021 is a pure intraday whipsaw regime that NO
daily-context feature (SPY direction F19, or the stock's own 1d return) can catch — it needs an INTRADAY
follow-through / chop measure.

NEXT (user): adopt `chg_1d ≥ 10%` into the cell; the float<$300M gate; an INTRADAY follow-through measure
for 2021 (daily context is exhausted); wire the cell as engine gates.

### Finding 22 — relative-volume PACE is an EXHAUSTION cut (lower = better); helps 2021 by 2/3

Broke down the volume-relative measures on the N=20 cell. First correction (user): `bar_rvol_15m` (entry
BAR volume vs opening-15m tempo) is a SPIKE/exhaustion framing that doesn't fit a momentum-continuation
system — and indeed it's flat (PF 1.9-2.6, no trend). The right framing is TRAILING-WINDOW pace, which I
built from the recorded raw sums: `rvol_20m = vol_20 / (avgvol20/390·20)`, `rvol_10m` likewise (avgvol20
reconstructed as `cum_vol_to_entry / rvol`).

All three volume-pace measures point the SAME way — **lower relative pace = better = an EXHAUSTION cut on
the high tail** (correlated; one lever viewed three ways):

_`rvol` (cumulative day vol / 20d avg daily):_ `<2x` PF **4.59** (+20% avg) → `2-5x` 2.73 → `10-25x` 1.84
→ `75x+` **1.56**. Monotone down.
_`rvol_20m` (last 20m pace):_ `<5x` PF 3.46, `5-15x` 3.02 → `40-100x` 1.62, `250x+` 1.63.
_`rvol_10m` (last 10m pace):_ `<5x` PF **4.78** (67% win) → `15-40x` 3.10 → high tail 1.7-2.1.

Mechanism: a run happening on ALREADY-blown-out volume (75×+ the day / 250×+ recent) is LATE — participation
is spent; a run on more modest relative volume is still BUILDING, more room to continue. Consistent with a
continuation system wanting the move still in progress, not exhausted. (Contrast the run's OWN volume SLOPE,
F14, which is "rising is good" — that's the run building; this is "already-blown-out total is bad" — the day
exhausted. Different axes: slope = rate now, rvol = accumulated level.)

**Book filter `rvol < 25x` — a KEEPER, and it HELPS 2021:** cell PF **2.17 → 2.51** (+$591k of $875k), and
2021 improves −$41k → **−$14k** (PF 0.82 → 0.88) — cuts 2/3 of 2021's bleed (2021's chop was partly
exhausted-name entries), every year up. Doesn't FIX 2021 (still slightly negative) but is the first feature
that meaningfully dents it. Stacks conceptually with `chg_1d ≥ 10%` (F21) — both cut a different bad tail.

NEXT (user): stack `chg_1d ≥ 10%` + `rvol < 25x` into the cell (measure the combined book + 2021); the
float<$300M gate; then an intraday follow-through measure for the residual 2021; wire cell as engine gates.

### Finding 23 — NEGATIVE: MaxFlyer-style exhaustion EXIT (sell the blow-off top) does NOT help; hurts 2021

Idea (user): DipRider is long the names MaxFlyer shorts; sell into the MaxFlyer entry signal — a NEW
SESSION HIGH on a VOLUME BLOW-OFF (bar vol ≥ mult × BOTH the 20d-per-min AND opening-15m-per-min baselines,
fill at that bar's close). Built `--dip-v2-exhaust-exit` / `--dip-v2-exhaust-vol-mult`. Swept vs MOC on the
N=20 cell:

| exit | exhaust-fire % | win | pf | net |
|---|--:|--:|--:|--:|
| **MOC (baseline)** | 0 | 43.5 | **2.17** | **+875,466** |
| exhaust 10× | 11.4 | 46.4 | 2.024 | +731,307 |
| exhaust 20× | 3.5 | 44.6 | 2.085 | +797,600 |

**Every variant LOWERS PF and net.** And on 2021 (the target) it's WORSE: exhaust-10× = PF 0.784 / −$48k vs
MOC 0.824 / −$41k.

Same lesson as EVERY exit test here (V1 F3, VwapReclaim F13, V2 F4): **selling into strength caps the
winners.** It raises win rate (46% vs 43% — banks more trades) but in these low-float CONTINUATION names a
new-high-on-huge-volume bar often KEEPS GOING — it's the +40% tail that carries the book. MaxFlyer's
blow-off logic works as a SHORT ENTRY because it selects reversal-prone setups; DipRider already selected
for CONTINUATION, so the same bar means "still running," not "done." For 2021 specifically it hit the WRONG
trades: it fired on WINNERS (banking early) while 2021's losses came from runs reverting into the STOP
(F20), not blowing off at new highs — so it couldn't help. ⇒ **Keep hold-to-MOC. Don't retry blow-off
exits.** 2021 needs a chop/whipsaw ENTRY filter, not an exit.

NEXT (user): stack `chg_1d ≥ 10%` + `rvol < 25x` (F21/F22 entry cuts — the working levers); float<$300M;
an intraday chop/whipsaw ENTRY measure for residual 2021; wire cell as engine gates.

### Finding 24 — 2021 is TWO-SIDED CHOP, not a reversal regime: shorting the same days ALSO loses

Test (user): run the dedicated short system (MaxFlyerV2, `brv20d≥100` pop-fade) on the EXACT 2021 DipRider
cell (ticker,date) pairs (257 pairs → exported to a parquet; added `MFV2_CANDIDATE_PARQUET` override to
MaxFlyerV2's readCandidates so any short book can be pointed at an arbitrary (ticker,date) list). If 2021's
longs failed because the names REVERTED, shorting them should WIN.

**It doesn't — the short ALSO loses: PF 0.784 / −$83k / 470 trips.** Near mirror-image to DipRider's 0.824.

| book (same 2021 days) | win% | avg_win | avg_loss | pf |
|---|--:|--:|--:|--:|
| DipRider LONG | 32.5 | +20.9 | −12.3 | 0.824 |
| MaxFlyerV2 SHORT | 54.9 | +11.7 | −18.3 | 0.784 |

The short WINS more often (55% — it catches the reversions) but its LOSERS are bigger (−18.3%): when a
name didn't revert it ran up and SQUEEZED the short. So neither direction works — longs stop out when price
fails to follow through UP; shorts get squeezed when price fails to revert DOWN. **Whipsaw both ways.**

⇒ **2021 is a two-sided CHOP regime, definitively NOT a directional reversal.** This RULES OUT the
reversal hypothesis and confirms why F23's blow-off exit was doomed (no consistent direction to exit into).
The only 2021 fixes are (a) an ENTRY filter that DETECTS & AVOIDS chop days, or (b) accept 2021 as an
unavoidable ~1-yr drawdown (1 of 7 years; system still +$875k / PF 2.17 overall). Infra win: MaxFlyerV2
`MFV2_CANDIDATE_PARQUET` override is reusable for any cross-system (ticker,date) probe.

NEXT (user): a chop-DETECTION entry filter (e.g. intraday whipsaw/reversal-count, or a market-wide
follow-through-breadth measure) for 2021; else adopt F21/F22 cuts + float<$300M and accept 2021; wire cell.

### Finding 25 — ⭐ GEOMETRY-STOP BUG FIX (user-caught from charts) — 34-46% stops → 8%; FIXES 2021

Reviewing the 2021 loser charts (F/charts), the user spotted the stops were ABSURDLY WIDE — "feels like
1.5d below the run low." Confirmed a real BUG: the geometry stop used `d = run_top − run_floor` (the ENTIRE
run's bottom-to-top height), so on names up 100%+ intraday, `stop = floor − d·2/3` gave **34-46% stops**
(CNET 45.8%, NVOS 34%). A −45% stop on a chop day is catastrophic — this drove 2021's losses.

Fixed per user: **`d = entry − run_floor` (how far above the floor we bought), `stop = entry − d·frac`** (2/3
of the way from entry down to the run floor). Impact (cell, modern):

| | buggy stop | **fixed stop** |
|---|--:|--:|
| avg stop dist | 34-46% (max) | **8.0%** (median 6.8%) |
| stop rate | ~53% | ~60% |
| cell PF | 2.17 | **2.305** |
| cell net | +$875k | +$812,667 |
| **2021 PF / net** | 0.824 / **−$41k** | **1.003 / +$452** |

**The fix FIXES 2021** (−$41k → break-even) AND lifts the whole book (PF 2.17 → 2.31) — every strong year's
PF also improves, and the winners' tail survives (8% avg still lets volatile runs breathe). So 2021's core
problem was NOT (only) chop — it was a too-wide stop letting the chop-day losses run to −45%. The
correct stop cuts them fast. This is the biggest single fix in the V2 line; the volatility/geometry-stop
findings (F12, F13) still hold directionally but their exact PFs shift under the corrected stop.

### Finding 26 — NEGATIVE: loss-of-VWAP exit doesn't help either (5th exit test, same lesson)

User idea: once the 9-EMA has been above VWAP ≥10 bars, exit the long when it crosses BELOW VWAP (trend
broke). Built `--dip-v2-vwap-exit-bars`. Full modern (fixed-stop cell):

| exit | vwap-exit fire% | win | pf | net |
|---|--:|--:|--:|--:|
| **MOC (fixed stop)** | 0 | 34.5 | **2.305** | **+812,667** |
| loss-of-VWAP (≥10) | 36.8 | 32.5 | 2.168 | +632,812 |

Fires on 37% of trades, LOWERS PF (2.31→2.17) and net (−$180k), and makes 2021 WORSE (+$452 → −$15k). Same
lesson as EVERY exit test (V1 F3, VWR F13, V2 F4, V2 F23): in low-float CONTINUATION names, exiting on any
signal caps the winners — these names dip below VWAP intraday then rip to new highs (the +40% tail), so the
VWAP loss cuts you out right before the resumption. And the (now-correct) STOP already handles the
genuinely-broken trends, so the VWAP exit is redundant on losers, destructive on winners. **Keep
hold-to-MOC.** The stop fix (F25), not a new exit, was the real 2021 solution.

NEXT (user): the fix likely shifts F12/F13/F16 exact numbers — consider re-baselining the leading configs;
adopt F21/F22 entry cuts (chg_1d≥10%, rvol<25x); float<$300M; wire cell as engine gates. 2021 now ≈flat.

### Finding 27 — exhaustion `rvol` gate re-verified on the FIXED-STOP book (wired `DipV2MaxRvol`, default OFF)

Re-ran F22's exhaustion cut on the fixed-stop book (F25). It HOLDS: cumulative-day `rvol` (day vol / 20d
avg daily) has a single clean weak tail at `75x+` (PF 1.53) vs ~2.2-4.5 below. Wired it as an engine gate
`DipV2MaxRvol` (`--dip-v2-max-rvol`; verified byte-exact vs post-hoc SQL). `rvol < 75` → cell PF **2.31 →
2.71**, +$700k (86% of net), every year positive incl. **2021 PF 1.16 / +$17k**. `rvol < 25` overcuts
(PF 2.67 but only +$540k). **Left OFF by default (user)** pending the distance breakdowns.
- Checked `rvol20m_20d` (last-20m pace vs 20d) too: 96% CORRELATED with cumulative `rvol`, and JAGGED
  (dead pockets at 40-100x AND 300x+, strong 100-300x between) → a WORSE gate. Cumulative `rvol` wins.

### Finding 28 — entry distance from VWAP and from the SESSION HIGH (fixed-stop cell)

**% distance from VWAP (`entry_vs_vwap`):** below-VWAP is the WEAK bucket; just-above is best, then a plateau.

| entry vs VWAP | n | pf |
|---|--:|--:|
| below VWAP | 109 | **1.569** |
| 0-2% above | 52 | **5.176** |
| 2-5% | 148 | 2.817 |
| 5-10% | 306 | 2.046 |
| 10-20% | 310 | 2.244 |
| 20-40% | 155 | 2.403 |
| 40%+ | 33 | 1.991 |

Reads: an `entry ≥ VWAP` floor cuts the worst bucket (below-VWAP = day-trend unconfirmed); a run that just
reclaimed/held VWAP (0-2%) is the freshest entry (PF 5.18). Beyond that it's a broad ~2.0-2.8 plateau.

**% distance BELOW the session high (`entry_vs_sess_high`, ≤0) — U-SHAPED, the two extremes win:**

| entry below sess high | n | avg_ret_pct | pf |
|---|--:|--:|--:|
| at high (0 to −.1%) | 108 | 4.92 | 1.638 |
| **−.1 to −1%** | 62 | 12.05 | **3.984** |
| −1 to −3% | 200 | 3.15 | 1.676 |
| −3 to −6% | 305 | 4.32 | 1.780 |
| −6 to −12% | 234 | 9.28 | 2.547 |
| **< −12%** | 204 | 13.39 | **3.433** |

TWO distinct good trades: (a) buy JUST off the high (−.1 to −1%, tiny dip = fresh continuation, PF 3.98),
or (b) buy DEEP below the high (< −12%, a mid-run name well under its earlier peak with room to run back
up, PF 3.43 / +13.4% avg / biggest net +$273k). DEAD zones: buying AT the high (chasing the top, 1.64) and
the shallow-to-moderate 1-6% pullbacks (~1.7-1.8). Non-monotone → not a simple threshold gate; the two
peaks would need a carve-out (like the F6 breadth U — user rejected carve-outs there, so treat as insight).

NEXT (user): decide whether to floor `entry ≥ VWAP` (cuts the weak below-VWAP bucket cleanly); the sess-high
U-shape is insight not an obvious gate; adopt rvol<75 + chg_1d≥10%; float<$300M; re-baseline leading configs.

### Finding 29 — run-tolerance sweep (2-5) + MAX-CONCURRENT=1: later same-day adds have worse EV (user right)

Swept `RunResetBarsBelow ∈ {2,3,4,5}` × `MaxConcurrent ∈ {0 unlimited, 1}` on the fixed-stop cell.

| tol | mc=0 (unlimited) PF / net | mc=1 PF / net |
|---|--:|--:|
| 2 | 2.154 / +847k | 2.150 / +519k |
| 3 | 2.042 / +778k | 2.245 / +522k |
| **4** | 2.297 / +846k | **2.498 / +513k** |
| 5 | 2.111 / +628k | 2.286 / +402k |

- **Tolerance:** modest lever, non-monotone; **tol=4 is best** (vs the old tol=1 which gave the fixed-stop
  cell PF 2.31). tol=2/3/5 slightly worse.
- **MAX-CONCURRENT=1 confirms the user's intuition:** capping at ONE position per (ticker,day) raises PF
  ~+0.2 in almost every cell (tol=2 the only flat one) while cutting ~40% of trips (the later same-day adds)
  and keeping ~60% of net. **The later concurrent adds re-qualify on a MORE-EXTENDED run → worse EV**; cutting
  them lifts quality. It also HELPS 2021 the most (fewer chase-adds on chop days).

**New defaults: `RunResetBarsBelow = 4`, `MaxConcurrent = 1`.** `tol=4 mc=1` = **PF 2.498 / 666 trips /
+$513k, POSITIVE every year, 2021 its best yet (PF 1.31 / +$33k)**:

| yr | pf | net |   | yr | pf | net |
|---|--:|--:|---|---|--:|--:|
| 2020 | 3.544 | +74,700 | | 2023 | 4.461 | +91,760 |
| 2021 | 1.308 | +32,962 | | 2024 | 3.262 | +105,116 |
| 2022 | 2.060 | +43,956 | | 2025 | 2.671 | +107,248 |
|   |   |   | | 2026 | 3.109 | +57,166 |

⚠ `MaxConcurrent` is SHARED across all engines — the new default (1) shifts V1's archived behavior too; pass
`--max-concurrent 0` to reproduce V1's old numbers. NEXT (user): entry≥VWAP floor; rvol<75; chg_1d≥10%;
float<$300M; re-baseline the leading config on these new defaults.

NEXT (for the user): choose the trigger/selectivity point on the dial (robust k=0.25 vs max-$ reclaim/k=0);
the 2021 regime is the standing risk at ALL points (non-breadth); then run_atr/run_len sweeps + 22-yr check.
