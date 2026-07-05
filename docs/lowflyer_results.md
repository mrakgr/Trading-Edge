# LowFlyer — Market-Wide Intraday Mean-Reversion Research Log

`TradingEdge.LowFlyer` (branch `highflyer_v2`). A market-wide intraday LONG
mean-reversion engine on 1-minute candles. The hypothesis: MaxFlyer's finding that
a **high-volume breakout to a new session LOW** is a fadeable long (buy the flush,
expect a bounce) should generalize **across the whole US-equity universe, every
day** — not just the gapped-up, quality-gated names MaxFlyer ever saw it on. If the
mean-reversion doesn't care where the stock opened, dropping the entire daily
selection funnel should multiply the sample count and let the edge be found by
slicing **recorded features post-hoc** rather than pre-gating on them.

**The hypothesis holds.** Across 2003–2026 the raw setup is +EV market-wide (PF
1.22 MOC, +$5.3M, ~20/23 years positive over 247,900 trips), and the post-hoc
slices reveal a sharp, interpretable, tradeable core: **fade the intraday flush of
a liquid, recently-strong name — not a falling knife.**

See [[project_maxflyer_engine_built_2026-06-28]] for the shared intraday engine
lineage. LowFlyer reuses MaxFlyer's `IntradaySystem` verbatim (Intraday.fs), driven
downside/long, fed by a pure-SQL candidate pass instead of the daily engine.

---

## The system under test

There is **no daily selection engine** — the only day-level preconditions are pure
SQL, materialized once into the `mr_candidate` table
(`scripts/equity/build_mr_candidate.fsx`):

- **Universe / price**: common stock + ADRs (`ticker_reference type IN ('CS','ADRC')`),
  day-D adjusted close ≥ $1, warmed up (>21 bars in the current gap-episode).
- **Liquidity gate**: the **median of the 09:30–09:45 ET 1m-bar volumes ≥ 10,000
  AND ≥ 10 of the (max 15) bars present**. A robust "the opening 15 minutes are
  liquid" floor that a couple of spike bars can't fake, known by 09:45 (no lookahead).
- **Gap-episode partitioning** via the shared `daily_episodes` view (gap-severed at
  >45 days; see `build_daily_episodes_view.sql`) so no rolling window / LAG / LEAD
  reaches across a recycled-ticker listing gap.

The **entry** (intraday, reused engine, RTH warmup from 09:30, scanning from **09:45
ET**): a 1-minute bar that **CLOSES below the running session low** (accumulated from
the open) on a bar whose **volume exceeds the running session max 1m-bar volume**
(the volume-confirmation gate). **Fill at the breakout bar's close.** NO stop. Hold
to MOC (16:00). Multiple entries per day allowed. The two intraday quality gates
(tightness / ATR%) are OFF — only new-session-low + volume-confirm + the 09:45 floor
survive.

**Recorded per trade** (features for post-hoc slicing, NOT gates): rvol
(cumulative-to-entry / 20-day avg), breakout-bar volume, cumulative-day-volume-to-
entry, %-change-since-open, close-1d, close-3d, and forward closes at D+1/D+3/D+5
(base = day-D close). See §"rvol definitions" below — the recorded `rvol` differs
from the fixed-checkpoint `rvol_0945` used in the winning slices.

Window: 2003-09-10 → 2026-06-25. Notional $10k/trip. P&L is gross (no fees/slippage —
a viability + feature-discovery test, not an execution model). PF is the
**+50%-winner-clip** standard: `Σ min(ret,0.5) over wins / −Σ ret over losses`.

---

## Run 1 — the full market-wide baseline (hold to MOC, no feature gate)

```
dotnet run --project TradingEdge.LowFlyer -c Release -- \
  --start-date 2003-09-10 --end-date 2026-06-25
```

| candidates | trips | win% | net P&L | PF (MOC) |
|---:|---:|---:|---:|---:|
| 1,900,632 | 247,900 | 51.9% | +$5,315,892 | **1.221** |

A positive raw edge across the entire market with zero feature selection, over a
quarter-million trips. The `mr_candidate` liquidity gate keeps 7.3% of CS/ADRC
price≥$1 ticker-days (1.9M of 26M). ~20/23 years positive (the misses — 2003, 2017 —
are marginal / thin-data early years). This is the population to slice.

---

## Run 2 — the edge is INTRADAY, not multi-day

Forward clipped PF by horizon (base = day-D close), full book:

| horizon | clipped PF | mean return |
|---|---:|---:|
| MOC (same-day) | **1.205** | +0.214% |
| +1 day | 0.957 | −0.059% |
| +3 day | 0.956 | −0.031% |
| +5 day | 0.974 | +0.072% |

**The flush-bounce is entirely intraday.** It pays ~+0.21% by the close, then
round-trips to roughly flat over the next 1–5 days. **Hold to MOC, never overnight.**
This is the *opposite* of HighFlyer (a multi-day edge) and confirms LowFlyer as a
same-day trade by design. (149 rows have a glitchy forward daily close — likely a
split-adjustment artifact — that poisons plain-average forward returns; the sum-based
PF is unaffected and those rows are excluded from the means.)

---

## Run 3 — rvol matters, non-monotonically (the "stocks in play" hypothesis)

Recorded-`rvol` buckets (cumulative-volume-to-entry / 20-day avg daily volume):

| rvol | n | win% | PF (MOC) | avg% |
|---|---:|---:|---:|---:|
| <0.5 | 142,401 | 51.4 | 1.124 | 0.123 |
| 1–2 | 19,565 | 50.6 | 1.336 | 0.291 |
| 2–3 | 6,386 | 52.4 | 1.412 | 0.480 |
| **3–5** | 6,912 | **53.4** | **1.510** | **0.590** |
| 5–10 | 7,657 | 54.5 | 1.385 | 0.491 |
| ≥10 | 20,678 | 55.5 | 1.200 | 0.464 |

The huge low-rvol bulk (142k trips) is near-dead at PF 1.12; the edge sharpens to
PF ~1.5 in the **3–5× band**, then *fades* at the extreme. Crossed with flush depth
(%-since-open), the interaction is stark: **moderate rvol (2–5×) on a deep flush
(down >5% since open) → PF 1.88**, but **extreme rvol (≥5) on a deep flush →
PF 0.67** (the falling-knife signal — extreme volume + deep flush = it keeps
falling). Not "higher rvol = better"; there's a moderate-volume sweet spot.

---

## rvol definitions (important — the recorded column ≠ the checkpoint metric)

- **Recorded `rvol`** (Run 3): `CumVolAtEntry / avgvol20` — cumulative RTH volume
  09:30→**the breakout bar** (a *floating* entry time), premarket-EXCLUDED, over the
  20-day avg daily volume. A "how active so far" proxy at a variable time.
- **`rvol_0945`** (Runs 4+): the **fixed 09:45 ET, premarket-INCLUSIVE** relative
  volume = `partial_candle_0945.volume (04:00→09:45) / avgvol20`. Same wall-clock for
  every trade. This is the operator-relevant metric ("how unusual is the volume by
  9:45?") and is what the winning slices condition on. Sourced by joining the
  existing `partial_candle_0945` table (its volume leg is 04:00→09:45) — no engine
  re-run needed.

---

## Run 4 — condition rvol_0945 > 1, break down by day-change vs prior close AT ENTRY

Tradeable day-change = `entry_price / close_1d − 1` (premarket gap + intraday move,
known at entry). rvol_0945 > 1 keeps ~29.7k trips.

| chg vs prior close @ entry | n | win% | PF (MOC) | avg% |
|---|---:|---:|---:|---:|
| < −15% | 3,469 | 58.5 | 1.17 | +1.10 |
| **−15..−8%** | 5,396 | **62.3** | **1.78** | **+1.38** |
| −8..−3% | 12,095 | 54.9 | 1.34 | +0.44 |
| −3..0% | 6,531 | 49.8 | 0.95 | −0.07 |
| 0..3% | 1,333 | 47.8 | 1.00 | +0.01 |
| 3..8% | 460 | 54.8 | 1.33 | +0.93 |
| ≥15% | 221 | 52.5 | 1.02 | +1.27 |

The best fade is a liquid name **already down 8–15% vs yesterday's close at entry** —
PF 1.78, 62% win. Dead in the "barely down" (−3..0%) bucket (PF 0.95). The very
deepest (<−15%) is *worse* than −15..−8 (PF 1.17 vs 1.78) — the falling-knife hint
again: catch the hard flush, not the worst-hit names.

**NOTE — the look-ahead trap:** conditioning on the *full-day* change
(`day_close/close_1d`) produces spectacular but UNTRADEABLE numbers (PF 17–73 in the
green buckets) because `day_close` is only known at 16:00, after the MOC exit. It
merely says "trades on days that closed green did great." Excluded from all
conclusions; the entry-time change above is the tradeable version.

---

## Run 5 — the ADV floor: NOT monotonic, cuts two ways

Added a 20-day average-dollar-volume floor (`adv20 = avgvol20 × day_close`). On the
**unconditioned** book a plain ADV floor slightly *hurts* (PF 1.206 no-floor →
1.177 at ≥$100M) — the edge has a small-cap tilt (thinner names overshoot more, so
revert more). But within cells it splits:

- **Moderate-flush cell (−8..−15% @ entry): ADV HURTS.** PF 1.78 (no floor) → 1.64
  ($10M) → 0.96 ($100M). A −8..−15% drop on a big-ADV name is often real bad news
  that keeps bleeding; on a smaller name it's an overreaction that snaps back.
- **Deep-flush cell (<−15% @ entry): ADV HELPS.** PF 1.17 (no floor) → **1.57
  ($10M)** → 1.26 ($100M). A −15%+ drop on a *thin* name is often a genuine collapse
  (going to zero); on a *liquid* name it's a violent overreaction with real buyers
  underneath. ADV separates a survivable flush from a death spiral.

**Verdict:** ADV is a **tradeability floor, not a PF booster.** A **light $500k floor**
drops the untradeable microcaps without touching the small-cap overshoot that is the
core edge; $10M+ starts eroding the moderate-flush cell; $100M is wrong (destroys it).
The production floor is **ADV ≥ $500k**.

---

## Run 6 — the 3-day change: the REAL setup is a pullback in an uptrend

Base filter: rvol_0945 > 1, ADV ≥ $500k (~28.4k trips). 3-day change to entry =
`entry_price / close_3d − 1` (tradeable). Overall, the standout 3-day bucket is
**up 5–15% (PF 1.91)**, and the multi-day *decliners* are the weakest:

| 3-day change @ entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −25% | 2,823 | 54.4 | 1.02 | +0.42 |
| −15..−8 | 5,739 | 56.5 | 1.45 | +0.61 |
| **+5..+15** | 2,504 | **57.9** | **1.91** | +1.35 |
| ≥15% | 2,049 | 53.7 | 1.24 | +0.95 |

**Within the profitable −8..−15% 1-day bucket, it sharpens dramatically:**

| 3-day change @ entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| −25..−15 | 1,199 | 61.9 | 1.77 | +1.37 |
| −15..−8 | 1,251 | 65.2 | 1.91 | +1.27 |
| −8..−3 | 605 | 66.0 | 1.84 | +1.18 |
| **+5..+15** | 371 | **67.7** | **3.68** | +2.97 |
| ≥15% | 389 | 58.4 | 1.71 | +1.66 |

**The premium setup: down 8–15% today, but UP 5–15% over the prior 3 days →
PF 3.68, 68% win, +2.97%/trade.** Interpretation: a **strong recent name taking a
sharp one-day flush** — a *pullback in an uptrend*, not a falling knife. Multi-day
strength means real buyers underneath; the one-day panic overshoots and snaps back.
This is the intraday analogue of HighFlyer's "buy the dip in a runner." The `<−25%`
3-day bucket (multi-day collapse) is the weakest even inside the good 1-day flush.

---

## Run 7 — by-year robustness of the 3d-up / 1d-flush finding

The **tight** cell (1d −8..−15 AND 3d +5..+15) is **over-fit** — most years have
1–14 trips; the PF-17/PF-34 years (2010, 2013) are 6–10 trips of noise. Real where
sample exists (2020–25 all PF 2–7) but not tradeable standalone.

The **broad, robust** condition is **1d flush ≥ 8% AND 3-day change ≥ −8% (not a
multi-day decliner)**, rvol_0945 > 1, ADV ≥ $500k:

| era | behavior |
|---|---|
| 2019 | PF 2.09 (97 trips) |
| 2020 | PF 4.18 (262) |
| 2021 | PF 3.66 (397) |
| 2022 | PF 1.91 (358) |
| 2023 | PF 1.53 (340) |
| 2024 | PF 1.89 (367) |
| 2025 | PF 1.39 (354) |

**Positive PF in ~19 of 23 years, and NOT just 2020/21** — the modern era
(2019→2025, 90–400 trips/year) is consistently PF 1.4–4.2. The losers are early-era
small samples (2005, 2007, 2015) where thin pre-2010 minute data leaves few liquid
names — a data-coverage artifact, not a strategy failure. Still **volatility-tilted**
(2020–22 richest), so sizing should scale with a volatility/breadth regime, not be flat.

---

## Run 8 — entry-timing: how fast is the flush? (an ACUTE flush is better)

Two entry-timing cuts, both tradeable (known at the entry bar). Base filter
rvol_0945 > 1, ADV ≥ $500k.

**(a) % change from the 09:30 OPEN to entry** (`entry_price / day_open − 1`, the
recorded `pct_chg_since_open`) is the *messier* of the two: most entries fire close
to the open with little net move-from-open, so ~27.6k of 28.4k trips fall in the
`≥ top` catch-all. The deep-from-open tail (< −10%) fades well (PF ~1.5–1.6) but the
middle is noisy. Weaker feature — the 09:45 anchor below is cleaner.

**(b) % change from the 09:45 checkpoint to entry** (`entry_price / px_0945 − 1`,
where `px_0945` = `partial_candle_0945.close × adj_ratio`) is **clean and monotonic**
— the further the name has fallen *during the scan window*, the better the fade:

| move 09:45 → entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −10% | 3,233 | 61.2 | 1.21 | +1.30 |
| **−6..−10%** | 4,626 | **59.8** | **1.56** | +1.13 |
| −3..−6% | 9,122 | 56.5 | 1.42 | +0.61 |
| −1..−3% | 6,924 | 51.6 | 1.17 | +0.23 |
| −0..−1% | 3,834 | 49.1 | 0.98 | −0.02 |

PF climbs smoothly from 0.98 (barely down since 9:45) to ~1.5+ (down 6%+). The
deepest (<−10%) has a slightly lower *clipped* PF (1.21) but the highest raw avg
(+1.30% — the +50% clip caps its bigger, more variable bounces).

**Stacked on the core setup** (rvol_0945 > 1, ADV ≥ $500k, down ≥8% @ entry, 3d ≥
−8%; baseline PF 1.83 / 2,783 trips), the acute-flush gradient still adds:

| 9:45 → entry move | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −8% | 1,227 | 66.1 | 1.84 | +2.34 |
| **−5..−8%** | 774 | 63.7 | **2.18** | +1.80 |
| −3..−5% | 478 | 59.0 | 1.49 | +0.95 |
| −1..−3% | 266 | 55.3 | 1.45 | +0.82 |

The **−5..−8% acute flush → PF 2.18** is the best-populated stacked cell (the ≥−1%
cell shows PF 2.39 on only 38 trips — ignore). **An accelerating flush beats a slow
drift, even after conditioning on being down ≥8% for the day.** So the ideal is a
**liquid, recently-strong name in an acute, accelerating flush** — the fastest
overshoots snap back hardest.

---

## Run 9 — microstructure: the 1m entry-bar body + intraday ATR%

Added three engine-exact columns (`entry_bar_open`, `intraday_atr_pct_at_entry`,
`intraday_tightness_at_entry` — the breakout bar's open and the 1m log-ATR /
tightness snapshot at the breakout). Base filter rvol_0945 > 1, ADV ≥ $500k.

**(a) The 1m entry-bar body** = `entry_price / entry_bar_open − 1` (the breakout
minute's own candle). Cleanly monotonic — a hard flush candle beats a tiny poke
below the reference:

| entry-bar body | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −3% | 4,452 | 62.0 | 1.33 | +1.51 |
| **−1.5..−3%** | 6,134 | 57.7 | **1.50** | +0.94 |
| −0.7..−1.5% | 8,267 | 54.0 | 1.23 | +0.34 |
| −0.3..−0.7% | 6,053 | 52.1 | 1.09 | +0.11 |
| −0..−0.3% | 3,414 | 50.1 | 1.03 | +0.04 |

The −0.3..0% bucket (PF 1.03) is the dead one-tick-poke-below-the-reference noise —
exactly what a **breakout-bar flush threshold would remove.** Stacked on the core
setup (down ≥8% + 3d ≥ −8%), a `< −2%` entry bar → **PF 2.07, 68% win, +2.61%** (1,583
trips). **Indicated threshold: entry-bar body ≤ −0.7%** (drops the flat-poke tail).

**(b) Intraday ATR% at entry** (1m log-ATR) — helps, but wants a BAND not a floor:

| 1m log-ATR | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < .002 | 2,651 | 52.9 | 1.13 | +0.10 |
| .004–.006 | 5,623 | 55.6 | 1.30 | +0.41 |
| **.006–.01** | 7,370 | 56.0 | **1.53** | +0.82 |
| .01–.02 | 5,010 | 57.5 | 1.48 | +1.25 |
| **≥ .02** | 1,557 | 50.1 | **0.87** | +0.04 |

Calm names (< .002) barely revert (no overshoot); the sweet spot is .006–.02
(PF ~1.5); the MOST volatile (≥ .02) **collapse to PF 0.87** — genuine chaos that
keeps going (falling-knife family). **Indicated cap: log-ATR < 0.02** (favor
moderate, reject the extreme). Both thresholds noted for a future gated run — TBD how
to apply cleanly.

---

## Run 10 — the rvol_0945 floor is over-gating: within a real flush it's the CEILING

Bucketing the FULL rvol_0945 spectrum (incl. sub-1), the earlier `> 1` floor
(Runs 4+) is revealed as too aggressive. RAW (ADV ≥ $500k, no flush filter), sub-1 is
mediocre-but-alive (PF 1.15–1.31) and > 1 is genuinely better (PF 1.4–1.5), so the
floor trades quantity for quality — defensible on the raw book. **But conditioned on
a real flush (down ≥8% @ entry, 3d ≥ −8%), it flips:**

| rvol_0945 (within core flush) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| <0.1 | 2,423 | 49.9 | 1.00 | −0.00 |
| 0.1–0.25 | 2,002 | 56.3 | 1.53 | +0.88 |
| 0.5–0.75 | 311 | 56.6 | 1.40 | +2.23 |
| **0.75–1** | 211 | 64.9 | **2.88** | +2.16 |
| 1–2 | 533 | 61.2 | 1.96 | +1.97 |
| 2–5 | 760 | 63.7 | 2.15 | +1.97 |
| ≥5 | 1,490 | 63.6 | 1.68 | +1.66 |

**Once the flush filter is on, everything from ~0.1 upward is PF 1.4–2.9** — only the
untraded-by-9:45 tail (<0.1) is dead. The `> 1` floor was doing double duty ("real
event" + "liquid"), but the **flush filter captures 'real event' far better**, so a
high rvol floor mostly discards good trades. A hard 9:45 flush in a moderately-active
name (rvol 0.75–1) still reverts strongly (the volume arrives *with* the flush, not
front-loaded). **Correction: drop the rvol_0945 floor to a light ~0.1–0.25 (exclude
only the <0.1 untraded tail) and lean on the flush + 3-day + ADV filters instead.**

---

## Run 11 — intraday tightness: coiled is dead, but no strong upside gradient

Motivated by the DAILY-side finding that mean-reversion prefers STRETCHED names
(daily tightness > 7–8.5), not coiled. LowFlyer's intraday tightness gate is **OFF**
(`MaxTightness = +inf`) — `intraday_tightness_at_entry` is recorded but never gated,
so the trades' tightness is whatever the flush naturally produces. Distribution: p10
3.0, **median 5.2**, p90 8.3 — the flush entries already skew well above MaxFlyer's
old `< 4.5`, i.e. we were never trading tight consolidations here. Base filter
rvol_0945 > 1, ADV ≥ $500k:

| intraday tightness @ entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| <2 | 1,541 | 49.9 | 0.98 | −0.02 |
| 3–4 | 5,311 | 54.1 | 1.29 | +0.54 |
| **4–5** | 5,648 | 55.4 | **1.41** | +0.70 |
| 6–7 | 3,186 | 56.1 | 1.34 | +0.71 |
| 7–9 | 3,345 | 56.7 | 1.24 | +0.52 |
| **≥9** | 1,855 | 59.0 | 1.29 | **+1.23** |

**Confirms the DIRECTION of the daily thesis — coiled (<2) is the only dead cell
(PF 0.98)** — but there is NO strong upside gradient the way daily >7 was a sweet
spot. PF rises to ~4–5 then plateaus. The most-stretched (≥9) names have the highest
win% (59%) and highest avg return (+1.23%, ~2× the middle) but a clip-capped PF (1.29)
— they pay BIGGER, not more reliably. Stacked on the core setup the gradient flattens
entirely (everything ≥2 is PF 1.4–1.68) — the flush/3d/ADV filters already capture it.

**Verdict: no intraday tightness gate.** Two caveats vs the daily result: (1) scales
aren't comparable — this is 20-MINUTE tightness vs the daily 14-DAY; (2) it's measured
AT the flush (inherently stretched), not going INTO the setup — the true analogue
would be the pre-9:45 opening-range coil, a feature not recorded. The daily >7 sweet
spot does NOT transfer; the only actionable read is "avoid the coiled <2–3."

---

## Run 12 — min-CLOSE breakout reference: now the DEFAULT (capacity win)

Added a min-CLOSE breakout reference (running min-CLOSE, not min-LOW — so a long lower
WICK can't push the channel boundary; trigger stays close-based). A/B over the full
range:

| reference | trips | win% | net P&L | PF (MOC) |
|---|---:|---:|---:|---:|
| min-LOW | 247,900 | 51.9% | +$5.32M | 1.221 |
| **min-CLOSE** | 318,740 | 51.3% | +$5.94M | 1.200 |

Counterintuitively min-close fires MORE (+29%), not fewer: the min-close boundary is
HIGHER than min-low (lowest close ≥ lowest low), so it's EASIER to break — it admits
earlier/shallower breakouts. Within the core setup the two are near-identical (min-low
PF 1.80 vs min-close 1.76). **Decision: min-CLOSE is the DEFAULT** — +29% trips (more
capacity) for a marginal −0.02 PF is a good trade, and the closes-only boundary is the
more sensible definition (wick-immune). `--min-low-ref` switches back to the min-LOW
channel. (These are pre-08:30-warmup / pre-rvol-prune numbers; the new-defaults
baseline is re-measured in Run 15.)

## Run 13 — rvol_0945 ≥ 0.1 candidate prune (promoted to first-class)

Promoted `rvol_0945` (premarket-inclusive 04:00→09:45 vol / 20-bar avg daily vol) to a
first-class `mr_candidate` column and dropped the dead <0.1 tail (Run 10) at the
candidate level — pruning both the data artifact and the intraday engine's work:

| book | candidates | trips | win% | PF (MOC) |
|---|---:|---:|---:|---:|
| all rvol | 1,900,632 | 247,900 | 51.9% | 1.221 |
| **rvol_0945 ≥ 0.1** | 850,107 | 96,110 | **53.4%** | **1.280** |

Cutting the <0.1 tail removed ~55% of candidate-days / ~60% of trips but LIFTED PF
1.221 → 1.280 and win% 51.9 → 53.4 (and halved the backtest to ~220s). Confirms that
tail was pure noise — a light rvol floor is a free quality + speed win.

## Run 14 — 20m price %-change into entry: the flush should be SUSTAINED

Added an engine-computed `chg_20m` = the 20-minute %-change into entry, via a new
generic `LagMa<'T>(20)` close-delay line (`RollingMa.fs`) read post-fold at the
breakout — NO post-hoc minute re-scan. (Entries in the first ~5 min after the 09:30
warmup, i.e. 09:45–09:49, have < 21 bars so `chg_20m` is `nan` — inherent, ~12% of
trips; excluded below.) Base filter rvol_0945 > 0.5, ADV ≥ $500k:

| 20m change into entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −10% | 2,334 | 59.0 | 1.08 | +1.05 |
| **−6..−10%** | 3,771 | 62.4 | **1.76** | +1.75 |
| −3..−6% | 9,098 | 57.7 | 1.47 | +0.75 |
| −1.5..−3% | 10,668 | 54.0 | 1.27 | +0.32 |
| −1.5..0% | 11,179 | 50.3 | 1.01 | +0.01 |

**Stacked on the core setup**, sharper still — **−6..−10% → PF 2.40, 66% win** (1,285
trips); < −10% backs off to PF 1.68 (falling-knife tail, but highest avg +3.06%).
An **inverted-U**: a barely-moving 20m (−1.5..0%) is dead (PF 1.0), a sustained ~20-min
flush (−6..−10%) is the peak, the most-extreme backs off. **The flush should be a
SUSTAINED ~20-minute slide, not a one-minute air-pocket.** Combined with the 1m
entry-bar (Run 9) and the 9:45→entry acute-flush (Run 8) findings, the ideal is a name
**flushing steadily for ~20 minutes into an acute breakout minute.**

---

## Run 15 — new-defaults baseline: 08:30 warmup + min-CLOSE + rvol prune

Consolidated the settled defaults: SessionStartMin = **08:30** (all indicators + the
20-bar LagMa now warm from premarket, like MaxFlyer — so `chg_20m` is populated by the
09:45 entry floor regardless of VolWindow), **min-CLOSE** reference (Run 12), and the
**rvol_0945 ≥ 0.1** candidate prune (Run 13). Full range, ungated:

| baseline | candidates | trips | win% | net P&L | PF (MOC) |
|---|---:|---:|---:|---:|---:|
| new defaults | 850,107 | 110,479 | 52.8% | +$3.86M | **1.268** |

The 08:30 warmup cut the `chg_20m` nan rate from ~12% → **2.69%** (the residual are
days with too few premarket bars — genuinely unmeasurable). min-close gives 110k trips
vs 96k for min-low at ~the same PF — the capacity gain the Run 12 decision bought.

## Run 16 — the microstructure gates as HARD gates: flush + ATR cap

First run applying the Run 9 microstructure thresholds as ENGINE gates (not post-hoc
slices): `--min-bar-flush -0.007` (entry-bar `close/prevClose ≤ −0.7%`) and
`--max-intraday-atr-pct 0.02` (1m log-ATR < 0.02).

| config | trips | win% | net P&L | PF (MOC) |
|---|---:|---:|---:|---:|
| ungated baseline | 110,479 | 52.8% | +$3.86M | 1.268 |
| **gated (flush + ATR)** | 46,290 | **56.0%** | +$3.12M | **1.420** |

**PF 1.27 → 1.42, win% 52.8 → 56.0** — cutting 58% of trips while keeping **81% of the
net P&L.** The removed 64k trips were low-value volume, not edge. Decomposition:

| gate | trips | win% | PF | net |
|---|---:|---:|---:|---:|
| ungated | 110,479 | 52.8 | 1.24 | $3.86M |
| **flush ≤ −0.7% only** | 49,185 | 55.8 | 1.34 | $3.39M |
| log-ATR < 0.02 only | 107,388 | 52.9 | 1.28 | $3.60M |
| both | 46,290 | 56.0 | 1.41 | $3.12M |

**The flush gate is the heavy lifter** — alone it takes PF 1.24 → 1.34 and cuts 55% of
trips while keeping 88% of net (removing the one-tick-poke breakouts of Run 9). **The
ATR cap barely prunes alone** (clips only ~3%, the genuine-chaos names) but **stacks
cleanly** on top (1.34 → 1.41) — a cheap additive tail-clip. Both kept: flush is the
edge-sharpener, ATR the tail-clip. **PF 1.42 is the best full-book number to date.**

## Run 17 — everything stacked: gated book + the selection core

The gated book (flush + ATR gates, Run 16) AND the selection core (down ≥8% @ entry,
3d ≥ −8%, ADV ≥ $500k, rvol_0945 ≥ 0.1):

| stage | n | win% | PF | avg% | net |
|---|---:|---:|---:|---:|---:|
| gated book alone | 46,290 | 56.0 | 1.41 | +0.67% | $3.12M |
| **+ selection core** | 4,869 | **62.1** | **1.92** | +1.63% | $794k |

**PF 1.92, 62% win, +1.63%/trade over 4,869 trips — and now broad across the years:**

| era | behavior |
|---|---|
| 2009–2018 | every year PF 1.33–2.13 (46–242 trips) |
| 2019 | 2.86 (170) · 2020 3.28 (529) · 2021 2.99 (690) |
| 2022–2026 | 2.07 · 1.54 · 1.89 · 1.42 · 1.60 (195–605 trips) |

**Positive PF in ~22 of 24 years** — the only misses are 2007 (0.84, 41 trips) and
2008 (0.94, 101 trips, GFC free-fall where even non-decliners kept dropping). **Every
year 2009→2026 is positive.** This is MORE robust than the pre-gate core (~19/23) — the
flush + ATR gates didn't just lift the headline, they made the edge more consistent
year-to-year by cutting the noise trades that dragged the weak years. Still
volatility-tilted (2020–21 the peaks), but no longer dependent on them.

## Run 18 — are 1d and 20m redundant? (No — depth vs velocity, orthogonal)

Both the 1d flush (`entry/close_1d − 1`) and the 20m change (`chg_20m`) reward larger
declines, so: is conditioning on both redundant? **They are correlated but NOT
redundant.** ρ(1d, 20m) = **0.576** (so ~2/3 of the variance is independent), and on
down-days the 20m window captures a **median of only ~40%** of the 1d move (IQR
23–65%) — the rest happened earlier in the session or overnight. The 2D PF grid
(rvol_0945 ≥ 0.1, ADV ≥ $500k) proves each adds PF holding the other fixed:

```
PF          20m:<-6  -6..-3  -3..-1  -1..0
1d:<-12       1.34    1.11    0.97   0.82     <- falling knife: DEGRADES as 20m steepens
1d:-12..-8    1.49    1.73    1.40   1.10     <- best row
1d:-8..-4     1.42    1.39    1.31   1.07
1d:-4..0      1.08    1.11    1.05   1.01     <- barely-down day: DEAD at every 20m
```

- **Across a row (fix 1d, vary 20m):** PF rises with 20m velocity — same day-flush, a
  FAST one fades better than a slow one (e.g. 1d:-8..-4 → 1.07 flat → 1.42 steep).
- **Down a column (fix 20m, vary 1d):** PF rises with 1d depth — same velocity, a
  deeper day-flush is better (up to a point).
- **The best cell is OFF-diagonal** (1d:-12..-8 × 20m:-6..-3 = **1.73**), not the
  extreme-extreme corner. **Barely-down days (1d:-4..0) are dead at EVERY 20m** — a fast
  flush without a real day-scale move doesn't work; you need both.
- **The extreme-extreme corner is the falling knife:** 1d < −12% row DEGRADES as 20m
  steepens (1.34 → 0.82) — deeply down AND plunging fast = genuine collapse, not overreaction.

**Verdict: keep both — 1d = DEPTH ("is this a real day-scale dislocation?"), 20m =
VELOCITY ("is it dislocating acutely right now?").** Complementary, not redundant. The
final selection wants **3d ≥ −8%** (not a multi-day decliner) + **1d moderately negative**
(real flush, avoid the <−12% knife) + **20m moderately negative** (acute, avoid the
extreme-extreme corner). `chg_1d`/`chg_3d` promoted to first-class recorded columns.

## Run 19 — pinning the 1d/20m ranges: don't narrow (floor, not band)

Tested tightening the loose core (1d ≤ −8%, any negative 20m) into a BOX
(1d ∈ [−12%,−5%], 20m ∈ [−8%,−3%]) on the gated + 3d ≥ −8% book:

| config | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **loose** (1d ≤ −8%, any 20m) | 4,823 | 62.1 | **1.92** | +1.63% |
| boxed (1d [−12,−5], 20m [−8,−3]) | 4,594 | 61.1 | 1.82 | +1.13% |

**Narrowing HURTS** — lower PF and much lower avg return (+1.13 vs +1.63), and it adds
two negative early years (2004/2005) as the sample thins. Why: the fine grid's high-PF
cells are *already inside* the loose filter; boxing amputates the profitable deep-flush
tail (1d < −12% has high avg return) and the good 20m wings. **Operative thresholds:
1d ≤ −8% is a FLOOR, not a band (deeper is fine — the falling-knife risk is handled by
the ATR gate + 3d filter, not a 1d ceiling); 20m just needs to be NEGATIVE (velocity
present), not boxed.** More knobs ≠ better here.

## Run 20 — LOW-FLOAT: the intraday flush-fade is a low-float overreaction (< $300M)

Joined dollar-float (SEC public-float revalued to the trade-day price, polygon-shares
fallback — the `live_scan.py` pattern; 62% trip coverage). Within the core (gated +
3d ≥ −8% + 1d ≤ −8%):

| dollar-float | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < $50M | 1,284 | 65.3 | 2.23 | +2.16 |
| $50–150M | 562 | 63.2 | 2.23 | +2.12 |
| **$150–300M** | 355 | 62.0 | **2.45** | +1.97 |
| $300M–1B | 490 | 56.5 | 1.59 | +1.00 |
| ≥ $1B | 471 | 60.5 | 1.55 | +0.90 |

**Clean break at ~$300M** — the < $300M band runs PF 2.23–2.45 vs 1.55–1.59 for large
caps (> $300M). The flush-fade is fundamentally a **low-float overreaction** (a
squeeze-prone name overshoots on a flush and snaps back; a mega-cap doesn't) — the SAME
<$300M threshold that drives the HighFlyer daily edge, now on the intraday side. Below
$300M the three sub-bands are all strong (2.23–2.45) — the < $50M "smaller is worse"
tilt of the earlier (fan-out-contaminated) read does NOT survive the correction; it's
just "< $300M." Stacked: core + low-float < $300M → **PF 1.92 → 2.26, avg +1.63% → +2.12%.**

**Caveat:** low-float is strong in the modern era (2017+: 2019 PF 3.13, 2021 2.76, 2024–26
1.6–1.9) but the sub-$300M sample is thin/erratic pre-2017 (float data coverage) — treat
it as a modern-era filter, not a full-history one.

## Run 21 — a 20m FLOOR (not a band) DOES help: require some velocity

Run 19 said don't BAND the 20m; but a FLOOR (require 20m ≤ some small negative — remove
the velocity-absent trades without amputating the deep tail) improves everything
monotonically. Sweep on the core (gated + 1d ≤ −8% + 3d ≥ −8% + ADV + rvol):

| 20m floor | n | win% | PF | avg% | net |
|---|---:|---:|---:|---:|---:|
| none | 4,823 | 62.1 | 1.92 | +1.63% | $788k |
| ≤ −1.5% | 4,606 | 62.2 | 1.94 | +1.69% | $779k |
| ≤ −2% | 4,369 | 62.4 | 1.95 | +1.75% | $766k |
| **≤ −3%** | 3,786 | 63.0 | 1.96 | +1.88% | $713k |
| ≤ −4% | 3,152 | 64.2 | 2.05 | +2.14% | $674k |
| ≤ −5% | 2,540 | 65.0 | 2.16 | +2.47% | $627k |

Monotonic PF/avg/win improvement, capacity the only cost. The difference from Run 19:
a floor removes ONLY the flat/rising-20m trades (velocity absent); the band ALSO chopped
the good deep-20m tail. **Chosen floor: 20m ≤ −3%** — the knee: PF 1.92→1.96, avg
+1.63→+1.88%, win 62→63%, keeping ~78% of trips and ~90% of net. Past −3% keeps climbing
(≤ −5% → PF 2.16) but pays real capacity. Stacks with low-float: core + 20m ≤ −3% +
low-float < $300M → **PF 2.34, 65.2% win** (1,778 trips). So all three declines are
FLOORS: 1d ≤ −8%, 3d ≥ −8% (a ceiling on the *downtrend*), 20m ≤ −3%.

> **⚠ Correction (fan-out bug):** the low-float / stacked figures in Runs 20–21 were
> first computed with a float table joined on `(symbol, trade_date)` only — but
> LowFlyer allows multiple entries per day (18,973 such day-pairs), so that join
> FANNED OUT multi-entry days (k×k), deflating the aggregate PF. Fixed by computing
> `dollar_float` as a 1:1 LATERAL column on the trip row. The corrected numbers above
> are HIGHER (core+float PF 1.65→2.26, core+float+20m-floor 1.67→2.34); the *pattern*
> (clean < $300M break, low-float is the edge) was unchanged, only the magnitudes.
> The no-float sweep numbers (Run 21, core = PF 1.96) were never affected — they used
> the un-joined trip table.

## Run 22 — the 3d return has an inverted-U too: tighten to [−3%, +30%] (PRODUCTION)

Ran the 3d breakdown on the full production system (gates + 1d ≤ −8% + 20m ≤ −3% +
ADV + rvol + low-float < $300M), 3d filter REMOVED so the whole spectrum shows (1:1
float join, fan-out-free):

| 3d return | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < −25% | 1,178 | 57.6 | 1.54 | +1.83 |
| −25..−15 | 1,057 | 61.0 | 1.83 | +1.74 |
| −15..−8 | 781 | 67.5 | 2.11 | +1.76 |
| −8..−3 | 429 | 63.9 | 1.67 | +1.35 |
| −3..0 | 189 | 67.7 | 2.96 | +2.50 |
| **0..5** | 269 | 66.9 | **3.10** | +2.84 |
| **5..15** | 379 | 68.6 | 2.76 | +3.15 |
| **15..30** | 286 | 65.7 | 2.90 | +3.05 |
| ≥30% | 226 | 57.1 | 1.70 | +1.86 |

**An inverted-U centered slightly ABOVE zero** — the best fade is a name that was
**flat-to-UP over the prior 3 days** (0..+15% → PF 2.76–3.10) taking a sharp one-day
flush: the purest *pullback in an uptrend* (multi-day strength intact → real buyers
snap the panic back). Both tails are weak: < −25% (multi-day collapse) and ≥ +30% (a
parabolic blow-off, too extended). 3d threshold sweep **(this table has low-float <
$300M ALREADY in the base — it measures the 3d effect WITHIN the low-float book):**

| 3d filter (× low-float) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| ≥ −8 (old) | 1,778 | 65.2 | 2.34 | +2.42 |
| ≥ −3 | 1,349 | 65.6 | 2.59 | +2.76 |
| ≥ 0 | 1,160 | 65.3 | 2.55 | +2.80 |
| **[−3, +30]** | 1,123 | 67.3 | **2.90** | +2.94 |

**PRODUCTION CHANGE: 3d ∈ [−3%, +30%]** (was ≥ −8%) — the cleanest single PF
improvement, **2.34 → 2.90 within the low-float book** at ~63% of the trips. Both ends
earn their keep: the −3% floor cuts the weak multi-day-decliner tail; the +30% cap cuts
the parabolic blow-offs. By-year (3d ≥ 0): 2017→2026 all positive (PF 1.7–4.9, peak
2021 4.87); weak years are pre-2017 tiny samples (float-coverage-thin), same modern-era
caveat as low-float.

**IMPORTANT — 3d and low-float are SEPARABLE (both required for PF 2.90):** with 3d ∈
[−3, +30] but WITHOUT the low-float filter the system is only **PF 2.20** (2,278 trips);
adding low-float < $300M takes it **2.20 → 2.90** (1,123 trips). So the 2.90 headline is
NOT the 3d-tightening alone — it needs BOTH the tightened 3d band AND the low-float
filter. Each does real, independent work.

| 3d ∈ [−3,+30] | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| no float filter | 2,278 | 65.2 | 2.20 | +2.19 |
| float present | 1,474 | 65.7 | 2.44 | +2.42 |
| **+ low-float < $300M** | 1,123 | 67.3 | **2.90** | +2.94 |

**Low-float breakdown within the NEW production (3d ∈ [−3%, +30%]):**

| dollar-float | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < $50M | 681 | 67.3 | 2.76 | +2.84 |
| $50–150M | 277 | 68.6 | 3.03 | +3.07 |
| **$150–300M** | 165 | 65.5 | **3.33** | +3.14 |
| $300M–1B | 191 | 59.7 | 1.44 | +0.89 |
| $1–5B | 81 | 60.5 | 1.26 | +0.58 |
| ≥ $5B | 79 | 62.0 | 1.28 | +0.63 |

The < $300M break is now even sharper — all three sub-bands PF **2.76–3.33** vs
1.26–1.44 for large caps (>2× gap). Within sub-$300M, PF RISES toward the $150–300M
band (2.76 → 3.03 → 3.33) — the "small-but-real" names fade most cleanly once 3d
strength is already required; the micro-floats aren't the best here.

## Run 23 — 20d breadth: strong-breadth days nearly DOUBLE the edge (size-up input)

Joined the market-wide 20d breadth (`pct_above_20`, the HighFlyer measure —
`data/equity/momentum_v0/breadth.parquet`), using **D-1 breadth** (prior-day close-based,
no lookahead — the intraday trade fires on day D). Breakdown on the production system
(gates + 1d + 20m + 3d[−3,+30] + float < $300M + ADV/rvol):

| breadth (D-1) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| < 0.20 | 51 | 68.6 | 2.82 | +2.91 |
| 0.20–0.35 | 106 | 59.4 | 1.20 | +0.58 |
| 0.35–0.50 | 249 | 67.1 | 2.76 | +3.03 |
| 0.50–0.65 | 280 | 64.3 | 2.61 | +2.31 |
| **0.65–0.80** | 332 | 69.9 | **3.80** | +3.36 |
| **≥ 0.80** | 105 | 75.2 | **5.69** | +5.48 |

Strong vs weak split: **breadth ≥ 0.65 → PF 4.23, 71% win (437 trips)** vs < 0.65 →
PF 2.33, 65% win (686). **Strong breadth nearly DOUBLES the PF.** By-year, the strong
cohort is broad, NOT just 2020-21: 2020 (5.34), 2021 (8.33), 2022 (10.0), 2024 (4.46),
2025 (3.58), 2026 (3.36), 2017 (6.75) — every meaningful-sample year is excellent; only
pre-2017 tiny samples are weak. Intuition: an intraday flush in a low-float name is a
transient overreaction when the tape is risk-ON (dip-buyers everywhere) but more likely
a real move when breadth is weak. (Two wrinkles: the < 0.20 deep-bear cell bumps to 2.82
— extreme-fear violent snapbacks; the 0.20–0.35 cell is a PF-1.20 dead zone.)

**Use breadth to SIZE UP, not to skip** (same as HighFlyer) — the weak/mid book is still
good (PF 2.33), so keep trading it, but **size up hard when breadth ≥ 0.65** (the PF gap
justifies ~2–3× base size). A sizing input, NOT a gate.

## Run 24 — yearly breakdown (flat + breadth-sized)

The production system per year (flat = 1× every trip; sized = **3× when D-1 breadth ≥
0.65**, 1× otherwise — the Run 23 size-up). `n_up` = trips in the 3×-sized bucket.

| year | n | n_up | flat PF | sized PF | flat avg% | sized avg% | sized net |
|---|---:|---:|---:|---:|---:|---:|---:|
| 2011 | 8 | 4 | 1.58 | 3.42 | +0.78 | +1.63 | $2.6k |
| 2012 | 8 | 5 | 0.37 | 0.37 | −3.05 | −1.92 | −$3.5k |
| 2013 | 9 | 3 | 19.1 | 33.2 | +2.98 | +3.18 | $4.8k |
| 2014 | 14 | 7 | 5.95 | 3.99 | +4.33 | +3.12 | $8.7k |
| 2015 | 22 | 2 | 1.97 | 2.03 | +1.11 | +1.03 | $2.7k |
| 2016 | 27 | 13 | 1.28 | 1.03 | +0.60 | +0.09 | $0.5k |
| 2017 | 32 | 8 | 3.94 | 4.61 | +3.54 | +3.79 | $18.2k |
| 2018 | 31 | 11 | 3.81 | 2.65 | +2.96 | +2.35 | $12.5k |
| 2019 | 45 | 19 | 2.82 | 2.26 | +2.62 | +1.81 | $15.1k |
| **2020** | 170 | 91 | 3.82 | 4.50 | +3.52 | +3.89 | $136.8k |
| **2021** | 198 | 100 | 4.96 | 6.41 | +4.10 | +4.88 | $194.4k |
| **2022** | 108 | 37 | 2.37 | 3.82 | +2.80 | +4.21 | $76.7k |
| 2023 | 116 | 56 | 1.63 | 1.82 | +1.61 | +1.96 | $44.6k |
| 2024 | 136 | 30 | 3.04 | 3.36 | +3.56 | +3.69 | $72.4k |
| 2025 | 162 | 34 | 2.71 | 2.95 | +2.48 | +2.76 | $63.5k |
| 2026 | 34 | 15 | 2.14 | 2.58 | +1.62 | +1.85 | $11.9k |
| **TOTAL** | **1,123** | — | **2.90** | **3.40** | **+2.94** | **+3.35** | **$668k** |

Flat: PF 2.90, +$330k. Breadth-sized 3×: **PF 3.40, +$668k (+102% net)** — same trips,
3× weight on the ~40% that fall on high-breadth days. Every large-sample modern year
(2020–2025) improves (2021 4.96→6.41, 2022 2.37→3.82). The sized book has HIGHER
VARIANCE (3× bets on 40% of trips), so sized PF isn't directly risk-comparable to flat —
it shows breadth is a real +EV sizing signal, not a like-for-like PF. **First trip is
2011 — SEC XBRL float coverage begins ~2011, so pre-2011 is a DATA boundary, not a
strategy boundary.** The lone losing year (2012, PF 0.37) is 8-trip noise. Where sample
is real (2017+, 30–200 trips/yr): positive every year, flat PF 1.6–5.0.

---

## Run 25 — chg_7d floor: add a 7-day-return floor on top of production (PF 2.86 → 3.25)

Added `chg_7d` (7-trading-day return into entry = `entryPx / close_7d − 1`, `close_7d
= LAG(adj_close,7)` episode-partitioned) as a first-class recorded column (candidate
builder + Trip/CSV), symmetric with chg_1d/chg_3d. Sliced it on the production long
(gates + 1d ≤ −8% + 20m ≤ −3% + 3d ∈ [−3,+30] + float < $300M + ADV, 1:1 float join).
Baseline: 1,101 trips, PF 2.86, median chg_7d = **+14%** (production names are already
mostly UP over 7 days — the low-float + 3d selection pre-loads a "runner pulling back").

**Per-bucket = an inverted-U, same shape as 3d** — the sweet spot is flat-to-moderately-
up over 7 days:

| chg_7d | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| < −30% | 48 | 70.8 | 2.62 | +2.73 |
| −30..−15 | 66 | 63.6 | 1.28 | +0.78 |
| −15..−5 | 99 | 57.6 | 1.72 | +1.23 |
| **−5..0** | 79 | 73.4 | **4.15** | +3.48 |
| **0..15** | 270 | 70.4 | 3.38 | +2.94 |
| **15..40** | 328 | 67.1 | 3.37 | +3.44 |
| 40..100 | 180 | 66.1 | 2.84 | +3.37 |
| ≥100% | 31 | 58.1 | 2.54 | +3.52 |

**FLOOR sweep — the knee is chg_7d ≥ −5%:**

| floor | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| none | 1,101 | 67.0 | 2.86 | +2.92 |
| ≥ −15% | 987 | 67.1 | 3.07 | +3.07 |
| **≥ −5%** | 888 | 68.1 | **3.25** | +3.28 |
| ≥ 0% | 809 | 67.6 | 3.18 | +3.26 |

**PRODUCTION CHANGE: add chg_7d ≥ −5%** — PF **2.86 → 3.25 (+14%)** for only ~19% fewer
trips. Past 0% plateaus/dips; the +100% tail is fine (a CEILING sweep only LOWERS PF —
don't cap the parabolic-7d names once low-float + 3d are applied). It's a FLOOR, like 1d
and 20m.

**INDEPENDENCE — chg_7d does real work BEYOND the 3d band (2×2, both toggled on the same
base):**

| 3d | 7d | n | win% | PF |
|---|---|---:|---:|---:|
| in [−3,+30] | **≥ −5%** | 888 | 68.1 | **3.25** |
| in [−3,+30] | < −5% | 213 | 62.4 | 1.71 |
| OUT | ≥ −5% | 1,108 | 63.9 | 2.00 |
| OUT | < −5% | 2,473 | 60.7 | 1.64 |

NOT redundant. WITHIN the production 3d band, adding the 7d floor lifts PF **1.71 →
3.25**: the 213 trips that pass 3d ∈ [−3,+30] but fail 7d ≥ −5% are the WORST cell in
the band — names flat-to-up over 3 days but still down >5% over 7, i.e. a **dead-cat
bounce inside a week-long decline**. The 3d window can't see them; the 7d window can.
Each floor cuts a genuinely different bad cohort.

**BY-YEAR (OLD 3d-only vs NEW 3d + 7d ≥ −5%):** the floor holds up and generally
IMPROVES — 2016 (1.35→1.80), 2022 (2.37→3.08), 2023 (1.62→1.81), 2025 (2.62→3.55),
2020/21 both up; roughly flat in 2019/2024; small dips only in tiny-n pre-2015 cells.
Every meaningful-sample year (2015+) stays positive (PF ≥ 1.8). The weak spots (2011/12,
n≈7) are the same SEC-float-boundary noise as everywhere else. Broad improvement, not
one-era-driven.

---

## Run 26 — bar_rvol_15m & 1m-flush depth on the PRODUCTION long (mirror of the short)

Re-ran the `bar_rvol_15m` (= breakout_bar_vol / mean [9:30,9:45) 1m vol) and 1m-return
breakdowns on the production system (888 trips, PF 3.25) — the short-side session showed
these two discriminate OPPOSITELY by side, so re-tested on the long.

**bar_rvol — mild inverted-U, NOT a useful floor (confirms the early-session finding):**

| rvol | n | clip PF | avg% |
|---|---:|---:|---:|
| <3 | 111 | 2.79 | +2.7 |
| **3–5** | 341 | **3.67** | +3.8 |
| 5–8 | 270 | 3.26 | +3.1 |
| 8–12 | 113 | 3.52 | +3.2 |
| 12–20 | 39 | 2.85 | +2.9 |
| **≥20** | 14 | **1.20** | +1.0 |

Floor sweep: rvol≥3 3.31 (tiny) → rvol≥8 2.78 → rvol≥12 1.96 → rvol≥20 1.20 — a floor barely
helps and pushing it HIGH actively HURTS. **Mirror of the short:** on the long flush-fade an
EXTREME volume spike is a FALLING KNIFE (PF 1.20 at ≥20×), not a signal; the mild sweet spot is
3–5× (a modest, not extreme, spike). rvol is ~useless as a long filter.

**1m-flush DEPTH — a REAL edge on the long (the tradeable feature, not rvol):**

| 1m flush | n | clip PF | avg% |
|---|---:|---:|---:|
| 0..−1% | 46 | 1.93 | +1.6 |
| −1..−2% | 207 | 2.00 | +1.6 |
| −2..−4% | 368 | 3.49 | +3.1 |
| −4..−7% | 178 | 4.07 | +4.4 |
| **−7..−12%** | 71 | 12.0* | +7.8 |
| ≤−12% | 18 | **1.33** | +2.2 |

Ceiling sweep (deeper flush): any 3.25 → **1m≤−2% 3.84** (635 trips) → ≤−4% 4.22 (267) → ≤−7%
4.43 (89). **A deeper flush lifts the long PF cleanly** — the prod spec already gates flush
≤−0.7%; tightening it to ≤−2% (3.25→3.84, keeps 71%) or ≤−4% (4.22, 30%) is a real improvement.
(*the −7..−12% PF 12.0 is clip-inflated by a few big MOC winners on capped losses; avg% +7.8 is
the honest read — still the best.) But the ≤−12% EXTREME flush DROPS to PF 1.33 — the falling-
knife tail; it's an inverted-U at the extreme, same as rvol. Sweet spot ≈ −4 to −12%.

## bar_rvol_20d on the long — a NON-LEVER (opposite of the short, where it's the main gate)

`brv20d = breakout_bar_vol / (avgvol20·adj_ratio/390)` — the breakout bar vs the STABLE 20-day
per-minute baseline. On the SHORT this became the new main gate (brv20d≥100 → PF 6.65, 2,760
trips). Tested on the production long (870 trips, PF 3.447; median brv20d 17.4, p90 48.4 — a
flush-fade doesn't produce the 100×+ climax bars the short's pops do). (`lowflyer_long_brv20d.sql`.)

| brv20d band (non-cum) | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| 0–10 | 216 | 70.4 | **4.97** | +4.4 |
| 10–25 | 368 | 65.5 | 2.98 | +2.9 |
| 25–50 | 202 | 67.3 | 2.77 | +2.5 |
| 50–100 | 72 | 72.2 | 3.99 | +4.1 |
| 100–200 | 11 | 90.9 | 8.27 | +3.2 |

A mild **U-shape** (low tail + extreme tail good, middle weakest) — the exact OPPOSITE of the
short's monotone escalation from a losing low tail. But **it is NOT a robust lever on the long:**
- Floor sweep gains only at trivial capacity (≥50 → 4.29 but 84 trips; ≥100 → 10.5 but 12 trips).
- Ceiling `≤25` mildly lifts PF (3.45→3.58, keeps 584) — echoing brv15's "extreme-volume flush =
  falling knife" but weak.
- **The 0–10 "best bucket" (PF 4.97) does NOT survive scrutiny:** its median flush is the
  SHALLOWEST (−2.5%), so it's not a flush-depth confound — but the `≤25` vs `>25` **by-year split
  is noisy with no consistent winner** (2017 lo 2.05/hi 7.56; 2019 lo 1.44/hi 5.52; 2024 lo 3.72/hi
  1.54), and the full-sample "low wins" is driven by two tiny huge-PF years (2014 30.8, 2018 47.1
  on 4–13 trips). Strip those → no stable edge either way.

**Verdict: brv20d is a NON-LEVER on the long — leave it out.** Mechanistically clean: the long is
*selling* exhaustion (a flush), whose edge lives in flush DEPTH + low-float + morning timing; a
stable-baseline UP-volume-climax measure is the wrong axis for it. brv20d is a short-only feature —
it discriminates a pop's exhaustion, not a flush's. (Mirrors brv15's weak/inverted long behavior;
confirms the two books are different setups, not mirror images, on the volume-spike axis.)

**Long/short mirror confirmed on both features:** rvol useless-to-harmful on the long (extreme
spike = falling knife) vs the master gate on the short (extreme spike = exhaustion); 1m-candle
DEPTH is a real edge on BOTH (deeper flush → long, bigger pop → short), each with an inverted-U
that collapses at the runaway extreme. **On the long, the 1m FLUSH is the tradeable feature, not
rvol** — candidate production tightening: flush gate −0.7% → −2%. (Not yet locked.)

## Dropping the VOLUME-CONFIRM gate (`--no-vol-high`) — reveals a SECOND A-tier long book

Wired a `--no-vol-high` flag (engine field `RequireVolHigh`, default on) that DROPS the
volume-confirmation gate (`bar.volume > runVolHi`), so entries fire on the FIRST new-session-low
bar regardless of its 1m volume; the recorded `new_vol_high` column flags which entries WOULD have
cleared the gate. Hypothesis (user): the vol gate may just be cutting trade count while the 1m-flush
does the real work. (`lowflyer_long_novolhigh.sql`.)

**The vol gate is NOT redundant — it's a real quality filter (hypothesis rejected):** the full
production selection on the no-vol-high book = **PF 2.18 / 12,669 trips** (vs 3.45 / 870 with the
gate). Split by `new_vol_high`:

| cohort | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| **new_vol_high=1** (old gate passers) | **870** | 68.0 | **3.447** | +3.30 |
| new_vol_high=0 (newly admitted) | 11,799 | 61.7 | 2.104 | +1.90 |

The =1 cohort reproduces the production book EXACTLY (870 / 3.447 — validates the flag). A flush bar
that ALSO makes a new session vol high (real sellers dumping = genuine capitulation) fades better than
one on ordinary volume. **Volume confirmation carries independent edge.**

**BUT the rejected cohort is a strong STANDALONE book once you ramp the flush DEPTH** (the user's
follow-up — flush-depth substitutes for volume-confirm). Flush-ceiling sweep on `new_vol_high=0`:

| flush ≤ | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| 0% (all) | 11,799 | 61.7 | 2.10 | +1.9 |
| **−2%** | **3,220** | 64.4 | **2.37** | +2.4 |
| −3% | 1,206 | 65.3 | 2.73 | +3.0 |
| **−4%** | 532 | 68.8 | **3.48** | +3.8 |
| −5% | 233 | 72.1 | 4.38 | +4.3 |
| −7% | 77 | 76.6 | 7.78 | +6.5 |

Monotone ramp. **At flush ≤ −4% the no-vol-high book hits PF 3.48 — matching the full production book
(3.45)**: a deep enough flush candle is ITSELF proof of capitulation and fully SUBSTITUTES for the
volume-high confirmation. (Head-to-head at each depth, vol-high=1 still wins — e.g. −5%: 5.89 vs 4.38 —
so vol-high AND deep-flush is best; but deep-flush ALONE is A-tier.)

**This is a NEW second book, not a diluted first one.** `new_vol_high=0 & flush ≤ −2%` = 3,220 trips
@ PF 2.37 (~3.7× the production capacity, from names the current system discards) — or ≤ −3% = 1,206 @
2.73 as the balance pick. **By-year robust:** positive every year 2012→2026 (only 2011 negative, 12
trips), modern-strong (2020: 578 trips PF 3.79; 2018 3.69, 2019 4.65, 2025 2.67, 2026 2.82), soft only
in the 2022/2023 regime years (1.2/1.6) that also stressed the main book — NOT an era artifact.

**Reframed conclusion:** the vol-high gate and the flush-depth floor are PARTIAL SUBSTITUTES — both
certify capitulation. The production long uses vol-confirm (highest PF, lowest capacity); a parallel
`--no-vol-high` + flush-depth-floor book adds a large A-tier cohort of DIFFERENT trips. Good "add more
good systems" candidate: run BOTH (vol-high book at full size + no-vol-high deep-flush book), dedup the
overlap. Threshold not yet locked — ≤ −2% (capacity) vs ≤ −3/−4% (PF) is a sizing call.

## Relaxing the vol gate to a FRACTION (`--vol-high-frac`) — the vol edge is MONOTONE, no free capacity

Rather than binary on/off, made the vol-confirm gate a fraction: enter if breakout-bar vol ≥ `frac ×`
running vol high (1.0 = original strict "exceed the high"; 0.8 = within 20% of it). Recorded the
CONTINUOUS `vol_vs_high` = breakout_bar_vol / runVolHi so a relaxed run slices post-hoc. Ran the
production long at `--vol-high-frac 0.8`. (`lowflyer_long_volfrac.sql`.)

**Bucketed by vol_vs_high — "near the high" (0.90–1.00) is ~as good as a new high; only <0.90 is weak:**

| vol_vs_high | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| 0.80–0.90 | 342 | 64.6 | **2.36** (weak) | +2.1 |
| 0.90–0.95 | 127 | 68.5 | 3.17 | +3.0 |
| 0.95–1.00 | 112 | 67.9 | 3.08 | +2.5 |
| 1.00–1.25 (new high) | 330 | 63.9 | 2.79 | +3.0 |
| 1.25–2.0 | 403 | 70.2 | **4.00** | +3.6 |
| ≥2.0 | 137 | 71.5 | 4.00 | +3.3 |

**Floor sweep — MONOTONE, relaxing below 1.0 only trades PF for trips, and the edge keeps climbing ABOVE 1.0:**

| vol_vs_high ≥ | n | clip PF | avg% |
|---|---:|---:|---:|
| 0.80 | 1,451 | 3.12 | +2.9 |
| 0.90 | 1,109 | 3.38 | +3.2 |
| **1.00 (current gate)** | **870** | **3.45** | +3.3 |
| 1.10 | 709 | 3.69 | +3.4 |
| 1.25 | 540 | **4.00** | +3.5 |

**Verdict: no free capacity in relaxing below 1.0.** The "within 5–10% of the high" band (0.90–1.00) is
nearly as good as a fresh high (PF 3.1 vs 3.45) — so being NEAR elevated volume carries most of the
signal — but the ≥0.90 floor only buys +239 trips (870→1,109) at PF 3.38 vs 3.45, a marginal capacity
option at best. Below 0.90 dilutes (0.80–0.90 = PF 2.36). **The edge is MONOTONE in volume**: it keeps
climbing ABOVE the high (≥1.25 → PF 4.00, 540 trips) — so a vol-high FLOOR above 1.0 is the real lever,
not a relaxation. Consistent with the whole long story — the more the breakout bar dwarfs prior volume,
the better the capitulation fade. **Keep the strict gate; optionally 0.90 for a little capacity, or ≥1.25
as a higher-PF/lower-capacity variant.** (The trips-vs-PF tradeoff is unfavorable either way — PF rises
slower than trips fall, the user's standing observation.)

**CEILINGS ADDED (cut the falling-knife tails):** both features have a runaway extreme that
collapses, so cap them.

| filter | n | clip PF | avg% |
|---|---:|---:|---:|
| baseline | 888 | 3.25 | +3.28 |
| rvol ≤ 12 | 835 | 3.39 | +3.33 |
| 1m ≥ −12% (flush no deeper than −12%) | 870 | 3.45 | +3.30 |
| **BOTH** | 826 | **3.45** | +3.32 |

**rvol ≤ 12** (cut the extreme-spike knife) → 3.25→3.39 at −6% trips. **1m ≥ −12%** (a FLOOR on
flush DEPTH — reject flushes deeper than −12%, the ≤−12% dead bucket) → 3.25→**3.45** at only −2%
trips (cheapest single lift). Combined = PF 3.45 / 826 trips — **identical to the 1m floor alone
(3.45 / 870)** because the extreme-rvol and extreme-flush names are LARGELY THE SAME TRIPS (a 12×+
spike and a −12% flush candle co-occur — the falling knives). So the two ceilings are NOT additive.
**DECISION: keep 1m ≥ −12% ONLY, DROP rvol ≤ 12** — same PF 3.45 with MORE trips (870 vs 826),
since rvol≤12 adds nothing over the 1m floor. → **production long now PF 3.45 / 68% win / 870
trips.**

---

## PRODUCTION SPEC (locked this session)

**FINAL system → PF 3.45 / 68% win / +3.30% per trade / 870 trips (2003–2026)**
(was 3.25/888 before the Run 26 1m-flush floor; 2.90/1,123 before the Run 25 chg_7d floor).

Long the intraday flush, scanning from 9:45 ET (indicators warm from 08:30), fill at
the breakout-bar close, hold to MOC. **min-CLOSE breakout reference.**

- **ENGINE GATES** (all three now wired in-engine, no post-hoc SQL): entry-bar flush
  `close/prevClose ≤ −0.7%` (`--min-bar-flush -0.007`) · **entry-bar flush-DEPTH floor
  `≥ −12%`** (`--min-bar-flush-floor -0.12`, the Run 26 falling-knife cut — reject flushes
  deeper than −12%; PF 3.25→3.45 at −2% trips; wired 2026-07-04, verified byte-identical to the
  old SQL floor at 870 / 3.447) · intraday log-ATR `< 0.02` (`--max-intraday-atr-pct 0.02`).
  (rvol≤12 was tested as an alternative to the flush floor but DROPPED — redundant, fewer trips.)
  **PRODUCTION INVOCATION:** `--min-bar-flush -0.007 --min-bar-flush-floor -0.12 --max-intraday-atr-pct 0.02`.
- **SELECTION** (post-hoc SQL on the gated CSV): 1d ≤ −8% (depth floor) · 20m ≤ −3% (velocity floor) ·
  **3d ∈ [−3%, +30%]** (trend band — flat-to-strong, not a decliner, not parabolic) ·
  **7d ≥ −5%** (7-day trend floor — not a dead-cat bounce in a weekly decline; Run 25) ·
  dollar-float < $300M (low-float overreaction) · ADV ≥ $500k · rvol_0945 ≥ 0.1.
- **SIZING:** base size, **3× when D-1 breadth (`pct_above_20`) ≥ 0.65** (PF 4.23 vs
  2.33 — Run 23; 3× → PF 2.90→3.40, net +102% — Run 24). A size-up input, NOT a gate
  (keep trading weak-breadth days); the 3× book carries higher variance.
  **ALSO size on the 1m-flush DEPTH (Run 26):** the deeper the entry flush candle the higher the
  PF (−1..−2% → 2.0, −4..−7% → 4.1, −7..−12% → best), inverted-U capped by the ≥−12% floor. Size
  UP as the flush deepens toward −12%; it's the strongest continuous long feature (rvol is NOT —
  it's a falling-knife on the long).
- Same-day MOC trade (the bounce is intraday); volatility-regime-tilted; low-float +
  3d-band + breadth-size-up are modern-era-strongest (pre-2017 float coverage thin).

---

## Run 27 — entry-time (30m buckets): the long is MORNING-concentrated

Broke the production long down by 30m entry bucket (hold-to-MOC, so later entry = shorter hold):

| entry | n | win% | clip PF | avg% |
|---|---:|---:|---:|---:|
| 09:30 | 162 | 63.0 | 3.02 | +3.1 |
| **10:00** | 248 | 66.9 | 3.73 | +4.0 |
| **10:30** | 118 | 73.7 | 6.77 | +4.5 |
| 11:00 | 85 | 71.8 | 2.82 | +3.3 |
| 11:30–12:30 | ~100 | ~65 | 1.5–2.6 | +1–2.7 |
| 13:00–15:30 | ~187 | mixed | noisy (1.2–13*) | +0.4–6 |

**70% of the book (613/870) fires 9:30–11:00, and that's where the edge is cleanest** — 10:00–10:30
is the sweet spot (PF 3.7–6.8, +4–4.5% avg). It SOFTENS after 11:30 (12:30 & 14:00 dip to PF
~1.2–1.5). Mechanism: the flush-FADE needs TIME for the bounce to develop before MOC, so morning
flushes (most room to revert) dominate; late-day entries have little time left and a weaker edge.
**Implication: the long can likely be time-gated to the morning (9:30–11:30) with little loss** —
the afternoon adds few trips and is noisier. (Opposite of the SHORT, which is all-day — see the
short doc: a blow-off reverses FAST regardless of clock, so it doesn't need the morning; midday
12:00 is even a peak.)

---

## The core setup (as of this session)

> **Long the intraday flush** — a 1m bar closing below the running session **min-close**
> on high volume, scanning from 9:45 ET (indicators warm from 08:30) — **when:**
> **ENGINE GATES (Run 16; flush-depth floor added 2026-07-04):**
> - entry-bar flush `close/prevClose ≤ −0.7%` (`--min-bar-flush -0.007`) — a real flush
>   candle, not a one-tick poke. *The heavy lifter: PF 1.24→1.34 alone.*
> - entry-bar flush-DEPTH floor `≥ −12%` (`--min-bar-flush-floor -0.12`) — reject flushes
>   DEEPER than −12% (the Run 26 falling-knife cut). *Bands the entry move with the ceiling
>   above; PF 3.25→3.45 at −2% trips. Now engine-wired (verified byte-identical to the old
>   post-hoc SQL floor).*
> - intraday log-ATR `< 0.02` (`--max-intraday-atr-pct 0.02`) — reject genuine chaos.
>   *A cheap additive tail-clip: 1.34→1.41 stacked.*
>
> **SELECTION (post-hoc filters, the pullback-flush core — depth × velocity × trend × float):**
> - **3d ∈ [−3%, +30%]** — flat-to-STRONG over 3 days (pullback in an uptrend), not a
>   multi-day decliner and not a parabolic blow-off. A BAND — inverted-U, peak in
>   [0,+15%] (Run 22). Tightened from ≥ −8%: PF 2.34 → 2.90.
> - **7d ≥ −5%** — flat-to-up over the trailing 7 days too; a FLOOR (inverted-U, but the
>   ≥100% tail stays fine so no ceiling — Run 25). Cuts the dead-cat-bounce-in-a-weekly-
>   decline cohort the 3d window can't see. Independent of 3d: PF 2.86 → 3.25.
> - **1d ≤ −8%** — a real day-scale flush (DEPTH); a FLOOR, not a band — deeper is fine
>   (falling-knife handled by the ATR gate + 3d, not a 1d ceiling). Run 19.
> - **20m ≤ −3%** — dislocating acutely NOW (VELOCITY); a FLOOR (require some velocity),
>   NOT a band (Run 21 — deeper is better, PF climbs to 2.16 at ≤ −5%; −3% is the
>   capacity knee). Orthogonal to 1d (ρ 0.58, each adds PF — Run 18).
> - **dollar-float < $300M** (sweet spot $50–300M) — the flush-fade is a LOW-FLOAT
>   overreaction; large caps (>$300M) barely work (Run 20). Modern-era filter.
> - tradeable: ADV ≥ $500k, rvol_0945 ≥ ~0.1 (NOT the old > 1 floor — Run 10).
> Hold to MOC.
>
> → gated full-book **PF 1.42 / 56% win** (Run 16); the SELECTION core reaches
> **PF ~1.6–2.2, ~60–70% win**, broadly across the modern era.
>
> **Further boosters (Runs 8, 14) — not yet gated:** the more ACUTE the fall
> (down ~5–8%+ from the 09:45 price into entry, stacked PF ~2.2) and the more SUSTAINED
> (20m change −6..−10%, stacked PF ~2.4). The ideal is a name flushing steadily for
> ~20 min into an acute breakout minute.

The setup is a **pullback-flush fade**, not a falling-knife catch: fade the panic in
a name that was *strong going in*, in an **acute, sustained** move, on a tradeable
name that isn't in genuine chaos. **The flush filter — not a high rvol floor — is what
isolates a real event** (Run 10). It is a **same-day MOC trade** (the bounce is
intraday) and **volatility-regime-tilted** (richest in 2020–22).

---

## Open threads / next

- **Settled defaults (this session):** min-CLOSE reference (Run 12), 08:30 warmup for
  all indicators + the 20-bar LagMa (so `chg_20m` is warm by 09:45 regardless of
  VolWindow), rvol_0945 ≥ 0.1 candidate prune (Run 13). Engine gates `--min-bar-flush`
  and `--max-intraday-atr-pct` wired (default off); first GATED A/B (flush ≤ −0.7%,
  log-ATR < 0.02) in Run 15.
- **No tightness gate** (Run 11 settled it — coiled <2 dead, but no upside gradient
  worth gating; the flush already selects for stretched names).
- **Promote to first-class columns**: `adv20`, `chg_1d` (vs prior close at entry),
  `chg_3d` are still post-join reconstructions off `mr_candidate`. (`rvol_0945`,
  `intraday_atr_pct_at_entry`, `intraday_tightness_at_entry`, `entry_bar_open`,
  `prev_bar_close`, `chg_20m` are now recorded first-class — added this session.)
- **The falling-knife short?** The `<−25%` 3-day cell (multi-day collapse) and the
  extreme-rvol deep-flush cell keep bleeding intraday — candidate for a `--short`
  run (the engine flag exists, inert here).
- **Regime sizing**: the edge is volatility-tilted; size with a breadth/vol regime
  signal rather than flat.
- **Execution realism**: all P&L is gross. MOC fills on the deepest flushes need a
  spread/slippage haircut before this is a live number.
- **⚠ SWING-EXIT research (TODO, deferred):** the short book proved that high-volume
  breakouts to new session HIGHS carry strong NEGATIVE intraday expectancy (the pop
  fades hard — raw PF 3–4+ shorting it). Implication for the multi-day SWING/momentum
  book (HighFlyer/Momentum, which HOLDS through such days): those intraday pop-and-fade
  events are a signal to EXIT (or trim) a swing long, not ride it into MOC. Research: for
  an open swing position, does flattening on a new-session-high high-volume-spike bar beat
  holding? (The short's entry signal = the swing's exit signal.) Deferred with the loss study.
- **⚠ LOSS STUDY (TODO, deferred):** study the big LOSING trades in BOTH MR books (long
  flush-fade and short pop-fade) — the tail that blows through the fade. Characterize them
  (what regime / rvol / extension / news) to find an avoidance gate or a catastrophe stop.
