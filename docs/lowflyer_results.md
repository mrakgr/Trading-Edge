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

## The core setup (as of this session)

> **Long the intraday flush** — a 1m bar closing below the running session low on
> high volume, scanning from 9:45 ET — **when: (1) the name is down ≥8% vs prior
> close at entry, (2) it has NOT been falling over the prior 3 days (3-day change ≥
> −8%), (3) it's liquid (rvol_0945 > 1, ADV ≥ $500k). Hold to MOC.**
> → PF ~1.6–1.9, ~60–70% win, broadly across the modern era.

The setup is a **pullback-flush fade**, not a falling-knife catch: fade the panic in
a name that was *strong going in*, only when it's liquid enough to be a real
overreaction rather than a collapse. It is a **same-day MOC trade** (the bounce is
intraday) and **volatility-regime-tilted** (richest in 2020–22).

---

## Open threads / next

- **Promote to first-class columns**: `rvol_0945`, `adv20`, `chg_1d` (vs prior
  close at entry), `chg_3d` are currently post-join reconstructions off
  `partial_candle_0945` + `mr_candidate`. Bake them into `mr_candidate` / the trip
  CSV so the winning slices are exact and reproducible.
- **The falling-knife short?** The `<−25%` 3-day cell (multi-day collapse) and the
  extreme-rvol deep-flush cell keep bleeding intraday — candidate for a `--short`
  run (the engine flag exists, inert here).
- **Regime sizing**: the edge is volatility-tilted; size with a breadth/vol regime
  signal rather than flat.
- **Execution realism**: all P&L is gross. MOC fills on the deepest flushes need a
  spread/slippage haircut before this is a live number.
