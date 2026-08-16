# LongSnoozer — buy the last-hour flush, hold overnight

**Status: RESEARCH. Not built, not traded.** Split out of
`docs/flushfader_results.md` on 2026-08-12 (§S43bq–S43bv) once it was clear this
is a **separate system**, not a FlushFader exit variant. It shares FlushFader's
universe and 1s tape and nothing else: different holding period (overnight, not
intraday), different signal (last-hour momentum, not a 20m-low flush), different
entry (limit into the close, not a next-bar fill).

The question that produced it — *should FlushFader's MOC exits hold to the next
open?* — was answered **no** and stays in `docs/flushfader_results.md` §S43bq:
the traded book contains **zero** MOC exits, because all 175 fail `gap_60 < 4`.

## The measurement

Population: every `mr_candidate_1s_v2` universe ticker-day, **1,420,627** with a
next-session open. Signal from the 1s tape; outcome from the causal daily columns:

```
last-hour change   lh = vwap(last bar <= T) / vwap(last bar <= 15:00) - 1
overnight return   (open_p1 + div_p1) / entry - 1
```

`open_p1`/`div_p1`/`close_d` come from `mr_candidate_1s_v2` (see
`docs/price_adjustment.md`). Reconciled against an independent hand-rolled
raw+splits+dividends join: **1,420,585 of 1,420,586 agree to 1e-9**.

⚠ **MEDIAN is the headline** throughout. The raw mean is distorted by corporate
actions the splits table does not describe; where the distribution is strongly
skewed the mean is reported alongside and the skew is called out.

⚠ **Thresholds are ABSOLUTE, never a cross-sectional rank.** A per-day rank is
**lookahead** — it needs every other name's completed hour before you can classify
your own. And rising pass-rates are a REAL increase in opportunity here, not filter
drift: this setup went from 1–8/yr pre-2020 to 56–199/yr after. Forward frequency
is what matters, so an absolute threshold is the right instrument.

✅ **RE-DERIVED ON THE FULL-DAY CORPUS, 2026-08-14 (§S43by, at the end of this
document).** The tables in the body below were measured against the old corpus,
which ended at **15:58:59** — no 16:00 bar, and the last RTH minute (the heaviest
of the session, and exactly where this system fills) missing entirely. They are
kept for provenance. **§S43by supersedes them and is what you should read.**

⭐ The headline survived: `gaps ≤ 760 × lh < −4%` reproduces at **PF 1.702 / mean
+3.26% / median +1.03% / n 1,397** on the rebuilt tape with a tradeable last-minute
entry, against 1.717 / +3.40% / +1.01% / 1,392 before. The spec is real.

---

## The overnight map — the long side

Per ticker-day from the 1s tape, `lh_chg = vwap(last bar ≤16:00) / vwap(last bar
≤15:00) − 1`; overnight from the daily tables. **Median** shown (the mean is not
usable raw — see §5); YEAR columns.

**Full 1s universe:**

| last hour | n | med% | win% | 2016 | 2017 | 2018 | 2019 | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---|---|---|---|---|---|---|---|---|---|---|
| <−6% | 4,210 | **−0.403** | 46.5 | 2.06 | 0.52 | 0.00 | −1.23 | −0.09 | 0.00 | −2.60 | −1.05 | 0.10 | −1.52 | 0.10 |
| −6..−4 | 5,011 | +0.212 | 51.9 | 0.00 | −0.12 | 0.70 | 0.24 | 0.67 | 0.06 | −0.60 | −0.56 | 0.60 | 0.00 | 0.80 |
| −4..−3 | 6,977 | **+0.251** | 53.1 | 0.18 | 0.26 | 0.31 | 0.00 | 0.60 | 0.00 | −0.10 | 0.07 | 0.67 | 0.00 | 0.55 |
| −3..−2 | 20,063 | +0.213 | 53.6 | 0.00 | 0.11 | 0.37 | 0.00 | 0.35 | 0.22 | 0.00 | 0.21 | 0.58 | 0.09 | 0.10 |
| −2..−1 | 82,815 | +0.179 | 54.3 | 0.04 | 0.12 | 0.22 | 0.06 | 0.41 | 0.22 | 0.17 | 0.13 | 0.30 | 0.08 | 0.00 |
| −1..−0.5 | 158,213 | +0.118 | 54.1 | 0.00 | 0.11 | 0.07 | 0.05 | 0.33 | 0.16 | 0.10 | 0.08 | 0.12 | 0.08 | 0.06 |
| −0.5..+0.5 | 885,177 | +0.049 | 52.3 | 0.05 | 0.06 | 0.04 | 0.07 | 0.16 | 0.10 | −0.02 | 0.00 | 0.05 | 0.05 | 0.00 |
| +0.5..+2 | 222,073 | 0.000 | 49.6 | 0.17 | 0.00 | 0.09 | 0.05 | −0.03 | 0.15 | −0.12 | 0.00 | 0.05 | 0.07 | −0.02 |
| +2..+4 | 27,223 | −0.213 | 43.9 | 0.18 | 0.00 | 0.06 | 0.00 | −0.72 | −0.09 | −0.42 | −0.19 | −0.04 | −0.14 | −0.04 |
| +4..+6 | 4,813 | −0.921 | 38.6 | −0.10 | −0.32 | −0.15 | −0.23 | −2.17 | −0.66 | −1.50 | −1.85 | −0.48 | −0.63 | −0.47 |
| **>+6%** | 4,052 | **−3.308** | **30.7** | 0.00 | −0.38 | −1.50 | −2.24 | −3.85 | −2.93 | −3.41 | −6.60 | −3.82 | −3.25 | −2.53 |

ALL: n 1,420,627, median +0.051%, win 51.9%.

**Three readings:**

1. **The long side is real but small and it INVERTS in the deep tail.** The peak
   is only **+0.25%** (−4..−3), and `<−6%` goes **negative** (−0.403%, win 46.5%).
   A big last-hour flush is *continuation*, not reversal.
2. **⭐⭐ THE SHORT SIDE IS THE BIG ONE.** `>+6%` in the last hour → median
   **−3.31%** overnight, win **30.7%**, and it is negative in **10 of 11 years**.
   `+4..+6` → −0.92%, win 38.6%. This is a far stronger and far more consistent
   effect than anything on the long side — **directly relevant to SpikeFader**.
3. It is monotone across the whole middle: the sign flips cleanly at 0.

**Split by last-hour tape density** — this is what settles the MOC question:

| last hour | LIQUID (≥3000/3600 s) n | med% | win% | THIN (<600/3600 s) n | med% | win% |
|---|---:|---:|---:|---:|---:|---:|
| <−6% | 601 | **+1.221** | 53.2 | 413 | **−0.877** | 41.2 |
| −6..−4 | 416 | **+1.035** | **59.6** | 630 | **−0.795** | 37.3 |
| −4..−3 | 564 | +0.625 | 55.1 | 700 | 0.000 | 46.1 |
| −3..−2 | 1,218 | +0.611 | 58.9 | 1,630 | +0.028 | 50.1 |
| −2..−1 | 3,623 | +0.346 | 56.8 | 4,329 | +0.080 | 51.1 |
| >+6% | 646 | −4.675 | 36.2 | 430 | −3.561 | 24.7 |

**The long-side effect is a LIQUIDITY effect, and it flips sign on thin tape.**
On a continuously-traded name a −6..−4% last hour is worth **+1.04%** overnight
at a 59.6% hit rate. On a name that trades <600 of 3,600 seconds the same setup
is worth **−0.79%** at 37.3%. MOC exits are 100% in the thin column.

Population matched exactly to the MOC exits' own profile (`nbars_lh < 1900` ∧
`lh_chg < −2%`): **n = 24,903, median +0.120%, mean +0.076%, win 51.5%** — i.e.
the 43-ticker-day sample's +0.97% is noise around an effect of roughly **+0.1%**,
which is far below the spread on a name that traded 535 of 3,600 seconds.

---

## ⭐ S43bt — THE 2D GRID: gap count × flush depth

The reference grid. Signal window **15:00 → 15:58:59** (the corpus end — see the
banner above), gaps counted out of 3,600, entry at the close. Superseded for
EXECUTION by S43bv, which moves the signal to 15:57 and the entry to a limit —
but this is the surface the thresholds were found on, and the cell S43bv is
measured against.

**PF (n)** — rows `gaps ≤ X` of 3,600, cols last-hour flush deeper than.
Cells are profit factor with the trade count beside it, so the thin corners
are visible at a glance rather than hidden behind an attractive PF.

| gaps≤ | <−2% | <−3% | <−4% | <−5% | <−6% | <−8% | <−10% |
|---|---|---|---|---|---|---|---|
| 200 | 1.913 (982) | 1.924 (582) | 1.857 (363) | 1.958 (278) | 2.165 (221) | **2.379 (147)** | 2.309 (102) |
| 400 | 1.667 (1,811) | 1.655 (1,048) | 1.680 (673) | 1.743 (510) | 1.978 (400) | 2.038 (254) | 2.062 (186) |
| 600 | 1.570 (2,799) | 1.577 (1,581) | 1.573 (1,017) | 1.590 (759) | 1.687 (601) | 1.666 (388) | 1.601 (282) |
| **800** | 1.539 (3,920) | 1.599 (2,132) | 1.626 (1,385) | 1.684 (1,023) | **1.783 (806)** | 1.515 (526) | 1.459 (377) |
| 1000 | 1.455 (5,095) | 1.504 (2,710) | 1.544 (1,724) | 1.600 (1,261) | 1.660 (993) | 1.366 (639) | 1.307 (463) |
| 1200 | 1.395 (6,471) | 1.423 (3,360) | 1.470 (2,104) | 1.494 (1,533) | 1.555 (1,189) | 1.336 (764) | 1.314 (552) |
| 1600 | 1.288 (10,212) | 1.312 (5,048) | 1.356 (3,090) | 1.337 (2,202) | 1.383 (1,681) | 1.233 (1,062) | 1.208 (760) |
| any | 1.139 (36,258) | 1.117 (16,195) | 1.117 (9,218) | 1.123 (5,945) | 1.136 (4,207) | 1.076 (2,417) | 1.096 (1,584) |

**Two readings.**

**The gap axis dominates.** PF runs 2.17 → 1.14 top to bottom at `<−6%`, while
moving along the flush axis inside any row shifts it by ~0.1–0.3. Tape continuity
is the first-order variable; how far the stock fell is second-order.

**⭐ There is a real interaction at the deep end.** On tight tapes (`gaps ≤ 400`)
deeper flushes keep paying — PF rises monotonically through `<−10%`. On looser
tapes (`gaps ≥ 800`) it **peaks at −6% and then collapses** (1.783 → 1.515 →
1.459). A deep flush on a gappy tape is a different, worse animal than the same
flush on a continuous one, so the two thresholds cannot be tuned independently.

**The standout cell: `gaps ≤ 800 × <−6%`** — PF **1.783**, mean **+4.67%**,
median **+0.92%**, n **806**, worst-20 concentration **−20.7%**, and per-year
1.82 · 1.48 · 1.07 · 1.70 · 3.00 · 1.84 · 1.63 — **zero losing years**.
`gaps ≤ 200 × <−8%` has the higher PF (2.379) but only 147 trades, a losing 2025
and −40% concentration in its worst 20: a lottery ticket, not a book.

**Frequency by year** (`gaps ≤ 800 × <−6%`) — the setup barely existed before 2020:

| 2016 | 2017 | 2018 | 2019 | **2020** | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 3 | 4 | 8 | **114** | 199 | 56 | 56 | 97 | 150 | 118 |

A 20–30× step change, not a drift — which is why absolute thresholds are the right
instrument here (see the banner) and why the pre-2020 sparsity is the finding
rather than something to normalise away.

Grid tool: `scripts/equity/overnight_by_density.py`.

---

## ⭐ S43bu — THE KNOWABILITY VERSION: 15:45 signal, MOC entry

> 🛑 **SUPERSEDED BY S43bv — AND ITS PREMISE WAS WRONG.** The 1s corpus ends at
> **15:58:59** (bucket 57539) on every day, so S43bt's "16:00" signal was really a
> **15:58:59** signal: too late for an MOC order, but only by a minute. Amputating
> to 15:45 gave up ~14 minutes of signal for no reason and destroyed the positive
> median. The fix is to drop the auction entry, not the signal — see S43bv. Kept
> for the record because the blind-window measurement below is still valid.

S43bt measured the last hour **15:00 → 16:00** and entered at the close. That is
not implementable: **NYSE's MOC cutoff is 15:50**, so a signal needing the 16:00
print cannot produce an MOC order. The knowable analogue stops at **15:45**,
leaving a 15-minute blind window before the fill.

**Signal fidelity:** `corr(lh45, lh60) = 0.843`. Of flushes visible at 15:45,
**76%** are still < −6% at the close; but only **57%** of the eventual 16:00
flushes were visible at 15:45. You see about half the setups, and a quarter of
what you see recovers before you are filled.

**The blind window is mildly HELPFUL, not harmful.** After a deep flush the stock
keeps falling into the close, so the MOC fill is *better* than the signal price:

| flush by 15:45 | n | median 15:45→16:00 | mean |
|---|---:|---:|---:|
| < −8% | 1,722 | **−0.252%** | −0.634% |
| −8..−6% | 1,422 | +0.144% | −0.011% |
| −6..−4% | 3,664 | +0.083% | +0.097% |

**🛑 BUT THE MEDIAN FLIPS NEGATIVE.** At matched selectivity:

| cut | n | PF | mean% | **median%** | worst-20 as %P&L |
|---|---:|---:|---:|---:|---:|
| 16:00 `gaps≤800/3600 × <−6%` (unusable) | 806 | 1.783 | +4.67 | **+0.92** | −20.7% |
| 15:45 `gaps≤600/2700 × <−6%` | 694 | 1.516 | +3.63 | **−1.51** | −34.8% |
| 15:45 `gaps≤600/2700 × <−8%` | 441 | **1.795** | **+5.83** | **−2.34** | −30.7% |

PF survives — the best knowable cell matches the unusable one at 1.795 — but the
trade becomes a **pure right-tail lottery**: more than half of entries lose, and
the whole return is carried by the tail. Concentration worsens with it
(−20.7% → −31/35% of P&L in the worst 20). That is a materially harder thing to
size and to sit through than the 16:00 version's positive-median profile.

**Per-year (knowable), all populated years:**

| cell | n | PF | mean% | trd/yr | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | yrs<1 |
|---|---:|---:|---:|---:|---|---|---|---|---|---|---|---:|
| `gaps≤600 × <−8%` | 441 | **1.795** | +5.83 | 80 | 1.47 | 1.32 | 1.41 | 1.43 | 2.73 | 2.17 | 1.51 | **0** |
| `gaps≤600 × <−6%` | 694 | 1.516 | +3.63 | 118 | 1.50 | 1.05 | 1.30 | 1.30 | 2.63 | 1.67 | 1.46 | **0** |
| `gaps≤1200 × <−3%` | 3,675 | 1.324 | +1.36 | 613 | 1.41 | 1.29 | 1.02 | 1.19 | 1.97 | 1.05 | 1.46 | **0** |
| `gaps≤900 × <−6%` | 989 | 1.323 | +2.25 | 173 | 1.33 | 1.06 | 0.98 | 1.11 | 2.23 | 1.32 | 1.29 | 1 |

⚠ **The knowable PF surface is NOISIER than the 16:00 one** — non-monotone in the
gap axis (`≤150` 1.637, `≤300` 1.384, `≤450` 1.320, `≤600` 1.516 at −6%). Picking
the maximum cell out of a jumpy grid is a selection-bias trap; prefer a cell whose
neighbours also hold up.

⏭ **The obvious next move: do not use MOC.** The entry does not have to be an
auction print — crossing the spread at ~15:58 with a market or marketable-limit
order would let the signal run to ~15:55 and recover most of the gap between 1.516
and 1.783, at the cost of the spread. On `gaps ≤ 600` names the tape is
continuous by construction, so that spread should be small. Untested.

---

## ⭐⭐ S43bv — THE TRADEABLE VERSION: 15:57 signal, 15:57-15:59 LIMIT entry

**User (2026-08-12): the entry does not have to be an MOC order.** Rest a limit at
15:57 and take the fill before the close. That removes the 15:50 cutoff constraint
entirely — and it recovers everything S43bu's 15:45 amputation gave away.

⚠ **First, a data fact that reframes all of this: the 1s corpus ends at 15:58:59**
(bucket 57539) on **every** day — there is no 16:00 bar, and the last RTH minute is
absent. So S43bt's "16:00 signal" was always a 15:58:59 signal. It was never 15
minutes from tradeable; it was one.

**Buying a flush with a limit is the favourable side of the trade.** Sellers are
hitting bids into the close, so a resting bid fills readily and earns the spread
rather than paying it. Measured: the 15:57–15:59 VWAP fill lands **below** the
official close **51.8%** of the time (median −0.005%) — essentially free, with a
slight edge. On identical trades:

| entry | n | PF | mean% | median% |
|---|---:|---:|---:|---:|
| closing auction (MOC-style) | 805 | 1.541 | +3.17 | +1.01 |
| **15:57–15:59 limit** | 805 | **1.561** | **+3.29** | **+1.02** |

**⭐ THE RECOMMENDED CELL: `gaps ≤ 760 of 3420` × `lh57 < −4%`**

| | |
|---|---|
| PF | **1.717** |
| mean / median | **+3.40% / +1.01%** |
| n | 1,392 (**232 trades/yr** at 2024–26 rates) |
| per-year PF | 1.64 · 1.52 · 1.35 · 1.61 · 3.29 · 1.65 · 1.55 |
| **years below 1.0** | **zero** (min 1.35) |
| worst-20 as %P&L | **−18%** |

Against the S43bt reference (`gaps≤800/3600 × <−6%`, PF 1.783, mean +4.67%, median
+0.92%, n 806): **more trades, better median, lower concentration, no losing
year** — for 0.07 of PF. And unlike S43bt, this one is actually executable.

Note the flush optimum **shifted from −6% to −4%**: with two fewer minutes of tape
the same event measures shallower, so the threshold has to follow it. Runner-up
`gaps ≤ 570 × <−4%`: PF 1.730, median +1.28%, 175/yr, also zero losing years.

**Median grid (the axis that matters for sitting through it)** — positive
everywhere at `gaps ≤ 950` for flushes between −2% and −6%, and turning negative
only for the deep `<−8%` flushes on looser tapes:

| gaps≤ | <−3% | <−4% | <−5% | <−6% | <−8% |
|---|---|---|---|---|---|
| 190 | +1.08 | +1.38 | +0.93 | +1.38 | +1.11 |
| 570 | +0.83 | **+1.28** | +1.13 | +1.12 | −0.65 |
| 760 | +0.73 | **+1.01** | +1.01 | +1.02 | −1.02 |
| 950 | +0.61 | +0.72 | +0.60 | +0.63 | −1.77 |
| 1140 | +0.59 | +0.63 | +0.17 | −0.18 | −1.71 |

✅ **FOLLOW-UP DONE (2026-08-13): the 1s bars were rebuilt for the whole day.**
The corpus used to stop at 15:58:59 with no post-market. Two consequences: the last RTH
minute — typically the day's heaviest — is invisible to every study here, and the
post-close session cannot be examined at all. Rebuilding would also open a new
question: **is there an edge buying flushes in the after-hours session?**


---

# ⭐ S43by (2026-08-14) — RE-DERIVED on the full-day corpus, with the last-minute entry

Everything above was measured on a 1s tape ending 15:58:59. This section replaces it.
New cache: `scripts/equity/snoozer_build_cache.py` → `snoozer_cache.parquet`,
**1,429,281 universe ticker-days**, 7,568 tickers, 2016-08-08 → 2026-08-06.
Grid tool: `scripts/equity/snoozer_grid.py`. Window study: `snoozer_windows.py`.

## The knowability ladder — three decision times

You cannot condition an order on tape that has not printed. Each signal window comes
in three flavours, each paired with the fill window that starts where its signal stops:

| suffix | signal to | limit rests in | gaps out of |
|---|---|---|---|
| *(none)* | 16:00 | — ⚠ **UNTRADEABLE**, upper bound only | 3600 |
| `k` | 15:57 | 15:57 → 16:00 | 3420 |
| ⭐ `k59` | **15:59** | **15:59 → 16:00** | **3540** |

⭐ `k59` is the user's 2026-08-14 request ("limit entries in the last minute") and is
now the default. It keeps 3 more minutes of signal AND buys the fill the heaviest
minute of the session — the half of the trade the old corpus could not see at all.

## ⭐⭐ §1 DOES A SHORTER WINDOW HELP? No — 60m wins at matched selectivity

⚠ **THE TRAP.** Thresholds are NOT comparable across windows: "−6% in 5 minutes" is a
far rarer and more extreme event than "−6% in an hour", so a fixed-threshold table
measures the *threshold*, not the window. The comparison must be at MATCHED n.

Median overnight %, the n most-negative ticker-days by each window:

| n | 60m | 30m | 15m | 5m |
|---:|---:|---:|---:|---:|
| 2,500 | **−1.02** | −1.51 | −1.18 | −1.88 |
| 10,000 | **+0.12** | +0.02 | 0.00 | −0.19 |
| 25,000 | +0.23 | **+0.26** | +0.14 | 0.00 |

**60m is the signal.** 30m only ties at the loosest cut; 15m and 5m are worse everywhere.

⭐ **There IS a real effect in the short windows, but it points the other way.** A
sharp *recent* decline **continues down** overnight — 5m `[−6,−4)` = **−2.77%**, win
38.2% — where an hour-long grind bounces (60m `[−6,−4)` = **+0.33%**, win 53.8%). Late,
fast selling is information; a slow drift is exhaustion. That is a SHORT-side
observation and does not belong to this system.

## ⭐⭐ §2 THE PF GRID — `gaps ≤ row` × `last hour < col`, entry `k59`

RAW PF (n). One trade = one ticker-day, unlevered, pre-cost.

| gaps≤ | <−2% | <−3% | <−4% | <−5% | <−6% | <−8% | <−10% |
|---|---|---|---|---|---|---|---|
| 200 | 1.831 (1,222) | 1.845 (714) | 1.803 (450) | 1.896 (344) | 2.114 (272) | 2.383 (182) | **2.492 (125)** |
| 400 | 1.627 (2,133) | 1.636 (1,199) | 1.644 (777) | 1.691 (587) | 1.850 (461) | 1.909 (298) | 1.842 (215) |
| 600 | 1.555 (3,144) | 1.570 (1,742) | 1.556 (1,114) | 1.586 (840) | 1.634 (669) | 1.627 (436) | 1.652 (309) |
| **760** | 1.589 (4,030) | 1.654 (2,182) | **1.702 (1,397)** | 1.762 (1,045) | **1.848 (822)** | 1.560 (538) | 1.526 (381) |
| 900 | 1.523 (4,869) | 1.591 (2,588) | 1.641 (1,637) | 1.693 (1,215) | 1.750 (955) | 1.444 (627) | 1.401 (448) |
| 1200 | 1.411 (6,983) | 1.451 (3,572) | 1.487 (2,215) | 1.530 (1,610) | 1.573 (1,244) | 1.387 (818) | 1.370 (581) |
| 1800 | 1.319 (13,564) | 1.358 (6,447) | 1.390 (3,882) | 1.381 (2,693) | 1.406 (2,037) | 1.271 (1,292) | 1.259 (906) |
| all | 1.182 (36,259) | 1.178 (16,155) | 1.174 (9,174) | 1.182 (5,913) | 1.200 (4,170) | 1.152 (2,421) | 1.174 (1,566) |

TRIMMED PF (bottom 5% of trades dropped) — what the SIZING decision should read:

| gaps≤ | <−2% | <−3% | <−4% | <−6% | <−8% |
|---|---|---|---|---|---|
| 200 | 3.242 (1,160) | 2.874 (678) | 2.620 (427) | 2.973 (258) | 3.313 (172) |
| **760** | 2.786 (3,828) | 2.607 (2,072) | **2.507 (1,327)** | **2.588 (780)** | 2.104 (511) |
| 1200 | 2.535 (6,633) | 2.332 (3,393) | 2.228 (2,104) | 2.224 (1,181) | 1.894 (777) |
| all | 2.196 (34,446) | 1.981 (15,347) | 1.856 (8,715) | 1.772 (3,961) | 1.640 (2,300) |

**Density is monotone and load-bearing on every window** — tightening the gap cut lifts
the long side at every depth (60m, 5,000 most-negative: all names +0.00% → densest 25%
**+0.47%**). Liquid names, exactly as before.

## ⭐ §3 THE CONTROL PASSED, AND THE DEEPER CUT CAME BACK

| cell | n | PF | mean% | med% | win% | worst% | yrs PF<1 |
|---|---:|---:|---:|---:|---:|---:|---:|
| `gaps≤760 × <−4%` (the old spec) | 1,397 | 1.702 | +3.26 | +1.03 | 55 | −55.2 | 1 |
| ⭐ `gaps≤760 × <−6%` | 822 | **1.848** | **+4.86** | **+1.21** | 53 | −55.2 | **0** |
| `gaps≤600 × <−6%` | 669 | 1.634 | +3.60 | +1.22 | 53 | −55.2 | 2 |
| `gaps≤1200 × <−3%` | 3,572 | 1.451 | +1.63 | +0.63 | 55 | −70.5 | 0 |

Per-year PF for `gaps≤760 × <−6%`: 2019 2.99 · 2020 1.95 · 2021 1.50 · 2022 1.10 ·
2023 1.72 · 2024 3.16 · 2025 1.88 · 2026 1.69 — **no losing year**.

⭐ **The old spec reproduces almost exactly** (1.702/+3.26%/+1.03%/1,397 vs the old
corpus's 1.717/+3.40%/+1.01%/1,392). The rebuild neither created nor destroyed the edge.

⭐ **The −6% threshold is back.** S43bv had to loosen it to −4% because amputating the
signal at 15:57 made the same event measure shallower. `k59` restores 3 of those
minutes, and with them the deeper cut — and it is the deeper cut that has zero losing
years. This is the S43bv lesson confirmed from the other direction: **the threshold
must track the measurement window.**

Trades/year for `gaps≤760 × <−4%`: 2016 1 · 2017 6 · 2018 10 · 2019 14 · 2020 230 ·
2021 331 · 2022 102 · 2023 84 · 2024 159 · 2025 234 · 2026 226. The single losing year
(2018, PF 0.386) is **10 trades** — the setup barely existed pre-2020. Rising frequency
is a real increase in opportunity, not filter drift (see the absolute-threshold note).

## ⭐ §4 THE LAST-MINUTE ENTRY — and why it helps the LONG specifically

| entry | `<−4%` PF | `<−6%` PF |
|---|---:|---:|
| `k57` → limit 15:57–16:00 | **1.755** | 1.607 |
| ⭐ `k59` → limit 15:59–16:00 | 1.702 | **1.848** |

The mechanism, measured on the gated population (`gaps ≤ 760`):

| regime | n | fill below close | median fill vs close |
|---|---:|---:|---:|
| flush `chg60k59 < −4%` | 1,397 | **64.9%** | **−0.065%** |
| quiet | 72,238 | 51.7% | −0.000% |
| rally `chg60k59 > +4%` | 1,438 | 58.1% | −0.031% |

**The last minute keeps drifting in the direction of the move**, so a limit resting in
it buys a flush **6.5bp below the official close** — the long side is paid to enter.
(The mirror image is why it *hurts* the short — see `docs/shortsnoozer_results.md`.)
The old 51.8% close-beating figure holds up on the newly-visible minute: **52.0%**
universe-wide.

⭐ **Fills are not a constraint** (user was right to push back on this): inside the
spec's own door (`gaps ≤ 760 of 3540`) there are **ZERO** unfillable ticker-days,
the median trades all **60 of 60** seconds, and even the worst 1% trades 57 of 60.
Universe-wide the no-fill rate is 0.407% — and that population includes the thin tail
this system never touches.

## Recommended spec (research; still not built)

    universe   mr_candidate_1s_v2  (dv_0945_tape >= $2M, n_bars_1s >= 200)
    signal     chg60k59 = vwap(<=15:59)/vwap(<=15:00) - 1  <  -6%
    density    gaps <= 760 of 3540 seconds in (15:00, 15:59]
    entry      LIMIT resting 15:59-16:00
    exit       next session's OPEN (market-on-open)
    -> PF 1.848, mean +4.86%, median +1.21%, win 53%, n 822 (~137/yr), 0 losing years

⚠ **worst trade −55.2%.** The overnight gap is unstoppable — there is no exit between
the close and the next open. Size on trimmed PF−1, never on empirical Kelly.
⚠ Unlevered, pre-cost, and one trade per ticker-day with no concurrency limit. A real
book needs a position cap and a borrow/fee model before any of this is tradeable.

---

# ⭐⭐ S43cb (2026-08-14) — the SECOND lever: last-hour DOLLAR SHAPE

## Where it came from

Chasing "why did the ShortSnoozer PF collapse?" (S43ca) turned up a feature nobody
had designed: **last-hour dollars ÷ first-15-minutes dollars**. It beat the gap count
on the short side and was only ~53% overlapping with it. This section tests it here.

⚠ **It was almost dismissed as an artefact.** The 15-minute denominator was in the
cache only because `dv_0945_tape` is the universe gate. Sweeping the reference window
properly (`snoozer_openref_sweep.py`) showed the choice is real: PF decays
monotonically as the reference lengthens, 3.48 (15m) → 2.58 (rest of day) on the short
side. Comparing the closing hour to the day's OPENING BURST carries information that
comparing it to the whole day does not.

## ⭐⭐ THE SIGN IS OPPOSITE TO THE SHORT SIDE

On `chg60k59 < −6%` (n 4,168), keeping 25% by each lever:

| lever | n | PF | mean | median | worst |
|---|---:|---:|---:|---:|---:|
| baseline | 4,168 | 1.198 | +0.94% | −0.31% | −90 |
| ⭐ RANDOM same-n control | 1,041 | 1.219 | +1.00% | −0.01% | −90 |
| **LOW** shape (the SHORT's lever) | 1,041 | **0.829** | −1.04% | −3.59% | −79 |
| **HIGH** shape, 15m ref | 1,041 | 1.984 | +3.40% | +1.21% | −90 |
| **HIGH** shape, 30m ref | 1,041 | **2.186** | +3.61% | +1.29% | −90 |
| HIGH `dv_over_rest` | 1,042 | 2.174 | +2.70% | +1.58% | −90 |
| dense tape (low gaps) | 1,042 | 1.698 | +4.00% | +0.71% | −70 |

**Importing the short side's filter gives PF 0.829 with 7 losing years — worse than
baseline AND worse than random.** Flipping it gives ~2.2.

⭐ **One variable, read from both ends: the overnight move continues in the direction
the closing hour's PARTICIPATION points.** Heavy, continuous late selling is real
supply that exhausts and bounces. A light, gappy late rally is nobody, and it fades.

⚠ The reference window is a SHALLOW optimum — 15m and 30m share 89% of their picks and
trade places by selectivity cut. **"Short reference beats long" is robust; "30m beats
15m" is not.** Do not tune inside the 5m–30m plateau.

## ⭐ THE TWO-LEVER GRID (`snoozer_grid2.py --side long --ref 30`)

RAW PF (n), `chg60k59 < −6%`, entry `k59`. Rows tighten PERSISTENCE, columns tighten
SHAPE:

| gaps≤ | shape ≥ q10% | shape ≥ q25% | shape ≥ q50% | shape: all |
|---|---|---|---|---|
| 200 | . | **3.967 (68)** | 2.907 (135) | 2.140 (270) |
| 400 | 3.326 (46) | **3.651 (115)** | 2.719 (230) | 1.863 (459) |
| **760** | 3.304 (82) | **3.591 (205)** | 2.274 (410) | 1.855 (820) |
| 1200 | 3.172 (125) | 2.855 (311) | 2.079 (621) | 1.570 (1,241) |
| 1800 | 2.790 (204) | 2.650 (508) | 1.983 (1,016) | 1.407 (2,032) |
| all | 2.314 (417) | 2.186 (1,041) | 1.677 (2,081) | 1.198 (4,162) |

Both axes are monotone and they compose — the incumbent spec sits in the rightmost
column, i.e. it was using only one of the two available levers.

## ⭐ THE REVISED SPEC

| cell | n | PF | mean% | med% | win% | worst% | losing yrs |
|---|---:|---:|---:|---:|---:|---:|---:|
| `gaps≤760 × <−6%` (**S43by spec**) | 820 | 1.855 | +4.90 | +1.21 | 53 | −55 | 0 |
| ⭐ `gaps≤760 × shape≥q25% × <−6%` | 205 | **3.591** | **+7.64** | **+4.13** | **65** | **−30** | **0** |
| `gaps≤760 × shape≥q50% × <−6%` | 410 | 2.274 | +5.26 | +2.56 | 58 | −36 | 0 |
| `gaps≤400 × shape≥q25% × <−6%` | 115 | 3.651 | +8.66 | +2.47 | 63 | −30 | 0 |
| `gaps≤1200 × shape≥q25% × <−4%` | 553 | 2.405 | +3.52 | +1.30 | 63 | −69 | 0 |

Per-year for the recommended cell: 2020 3.10 · 2021 2.46 · 2022 2.09 · 2023 6.85 ·
2024 4.83 · 2025 2.78 · 2026 5.15 — **no losing year**.

⭐ **The shape lever nearly doubles PF (1.855 → 3.591) AND cuts the worst trade
−55% → −30%** — the first thing on this side to move the tail rather than just the
centre.

⚠ **The cost is trade count: 820 → 205, about 34/yr.** The `q50%` variant (410 trades,
PF 2.274, worst −36%) is the middle option and is the one to prefer if concurrency
ever matters more than per-trade edge.

⚠ Pre-2020 cells are empty (`.` = fewer than 5 trades). This setup barely existed
before 2020 — see the absolute-threshold note in §S43by. The seven years shown are the
whole out-of-sample story.

## Spec v2 (research; still not built)

    universe   mr_candidate_1s_v2
    signal     chg60k59 = vwap(<=15:59)/vwap(<=15:00) - 1  <  -6%
    density    gaps <= 760 of 3540 seconds in (15:00, 15:59]        PERSISTENCE
    ⭐ shape    dv(15:00-16:00) / dv(09:30-10:00)  >= its 75th pct  MAGNITUDE
    entry      LIMIT resting 15:59-16:00
    exit       next session's OPEN
    -> PF 3.591, mean +7.64%, median +4.13%, win 65%, n 205 (~34/yr), 0 losing years,
       worst trade -30%

⚠ Unlevered, pre-cost, one trade per ticker-day, no concurrency cap. The shape cut is
a QUANTILE of the gated population, so a live implementation needs it restated as an
absolute threshold (a per-day cross-sectional rank would be LOOKAHEAD).

---

## S43cd (2026-08-14) — the BAR-COUNT family: a third lever, better on the tail

**User idea.** The `dv_over_open*` family compares last-hour DOLLARS to opening
dollars. The bar-count twin compares last-hour PRESENCE to opening presence:

    bar_over_openN = (nbLh/3600) / (nbOpenN/openN_secs)

⭐ **RATE-NORMALISED, unlike the dollar family.** A bar count is hard-capped by window
length (900 seconds cannot yield more than 900 bars), so a raw ratio would be bounded
by the window ratio itself and compress at the top. Dividing each side by its own
window makes **1.0 mean "as continuous now as at the open"** — and the universe median
lands at **1.005**, so the normalisation has a real zero point. Dollars have no such
cap, which is why `dv_over_*` stays a plain ratio (median 3.155).

⭐ It expresses something the ABSOLUTE gap count cannot: a name trading 850/900s at the
open and 2,000/3,600s into the close has **decayed**, while 300/900 → 1,200/3,600 has
**improved** — and both can land on the same absolute gap count.

### Long side (`chg60k59 < −6%`, n 4,165, matched n 1,042)

| lever | PF | mean% | **worst%** |
|---|---:|---:|---:|
| ⭐ RANDOM same-n | 1.321 | +1.56 | −58 |
| **$ `dv_over_open30`** | **2.194** | +3.63 | −90 |
| $ `dv_over_rest` | 2.174 | +2.70 | −90 |
| `tc_rate` | 2.125 | +2.60 | −90 |
| BAR `bar_over_open30` | 1.844 | **+3.97** | −90 |
| BAR `bar_over_open5` | 1.754 | +3.31 | **−55** |
| `gaps` (absolute) | 1.702 | **+4.02** | −70 |
| BAR `bar_over_open15` | 1.591 | +2.82 | −70 |

Dollars win on PF. But `bar_over_open5` gives the **best tail of any lever (−55 vs
−90)** and `gaps` the best mean with the worst PF — the levers trade off rather than
ranking cleanly.

### ⭐ THE OVERLAP MATRIX — three clusters, not one family

| | dv_open15 | bar_open15 | gaps |
|---|---:|---:|---:|
| **dv_over_open15** | — | 66% | 53% |
| **bar_over_open15** | 66% | — | **75%** |
| **gaps** | 53% | 75% | — |

Dollar family 92% internally coherent, bar family 86–93%, and **`gaps` sits 75% with
bar counts but only 53% with dollars**. Exactly the expected structure: bar counts and
gaps both measure PRESENCE, dollars measure MAGNITUDE. So bar counts are not a
replacement for either — they are a smoothed, RELATIVE version of `gaps`, which is why
they inherit its tail behaviour while picking up part of the dollar family's edge.

⚠ The reference window barely matters for bar counts (short side: 3.008 / 3.025 /
3.031 across 15m/30m/5m) where it mattered a lot for dollars (3.476 → 2.580 across
15m → rest-of-day). Consistent with a saturating PRESENCE measure vs a MAGNITUDE one.

**Practical read: dollars stay the primary lever on both sides; bar counts are the
better instrument if the TAIL is what is being optimised** — which on ShortSnoozer,
where the tail is the entire disqualifier, may matter more than the PF given up.

Build: `snoozer_build_barcounts.py` (its own light scan — adding these six aggregates
to `snoozer_build_shape.py` OOM-killed it twice at 13.4GB RSS; reading only
`ticker`+`bucket` instead of also `vwap`+`volume` is what makes it affordable).

⚠ **NAMING**: `lh_over_*` was renamed **`dv_over_*`** (user) so the prefix says which
quantity is compared, pairing with `bar_over_*`. `lh` = last hour throughout.

---

# ⭐⭐ S43cf (2026-08-16) — the THIRD lever: LOW OPENING VOLATILITY

⭐ USER: *"first 15m, 30m, 60m and the entire day's volatility. I want to see whether
**along with volume** that makes a difference to the trade."*

The emphasis is the whole test. Both Snoozer sides already run on PARTICIPATION —
`gaps` × `dv_over_open15` (§S43cb). A feature that merely sorts returns can still be
redundant; the question is whether it survives **on top of the incumbent cell**.

**Answer: yes on the long side, emphatically, and no on the short side.**

## The measure, and one deliberate departure from FlushFader

Locked by `project_surgerider_vol_bakeoff_2026-07-21`: dollar-weighted **30s slot
vwap** → `r_i = ln(sv_i/sv_{i−1})` → **mean |r_i|**. Not stdev, not close-to-close —
that is the comparison the bake-off already lost.

⚠⚠ **The slot CLOCK differs from FlushFader's.** `Intraday.fs` slots **30 PRESENT
BARS** (`slotN = slotBars`), not 30 wall-clock seconds. Right for a streaming feature,
wrong here, for two measured reasons:

1. **Coverage** — on 2024-03-05 only **4,804 of 10,670** ticker-days (45%) accumulate
   even 3 complete 30-bar slots inside the last hour. A present-bar `volat_lh` is
   undefined for over half the universe.
2. **Comparability** — these are FIXED CALENDAR windows ("first 15m"), and a
   present-bar slot straddles those boundaries by construction, so a thin name's
   "first 15m volatility" would be measured over a horizon a dense name's is not.

So: 30 **wall-clock** seconds. Same two-clocks question as §S43cd, resolved the other
way because this use is cross-sectional rather than streaming.

## ⭐ THE WINDOWS — what each feature actually covers

All six are the SAME measure over different slots. Five of the six are **cumulative
from the open**; only `volat_lh` is a standalone segment.

| feature | clock | length | knowable at | median |
|---|---|---|---|---:|
| `volat_open15` | 09:30 → 09:45 | 15 min | 09:45 | 20.4bp |
| `volat_open30` | 09:30 → **10:00** | 30 min | 10:00 | 17.9bp |
| `volat_open60` | 09:30 → **10:30** | 60 min | 10:30 | 15.4bp |
| `volat_day` | 09:30 → **15:00** | 5.5 h | 15:00 | 10.8bp |
| `volat_lh` | **15:00 → 15:59** | 59 min | 15:59 | 6.5bp |
| `volat_dayfull` | 09:30 → **15:59** | 6.5 h | 15:59 | 10.3bp |

Plus the relative twins `volat_over_open15 / _open30 / _open60 / _day`, all of the form
`volat_lh / volat_<window>`: below 1.0 = the closing hour was calmer than the opening
window, above 1.0 = it heated up.

⚠ **`volat_day` STOPS at 15:00** so it is disjoint from the last hour — a "day"
volatility containing the last hour would be partly made of the thing it is being used
to explain, since the −6% signal lives there. `volat_dayfull` is the literal "entire
day" and is the only opening-anchored window that overlaps `volat_lh`.

⚠ **`volat_lh` stops at 15:59, not 16:00** — that is when the k59 limit entry decides.
Measuring to the close would be a lookahead against our own fill. Every window in the
table ends at or before 15:59, so none is a lookahead.

⚠ **The four opening/day windows are NESTED**, hence 62–82% shared picks. They are
close to ONE variable read at four horizons, not four features — which is why they all
land at 99.9–100.0 percentile together and why their internal ranking shuffles between
cell widths.

⚠ **Volatility DECAYS through the session** (20.4 → 17.9 → 15.4 → 10.8 → 6.5bp), so bp
thresholds are NOT transferable between windows. The `[34, 83)bp` band of §S43ch is
calibrated on `volat_open15` specifically; the equivalent cut on `volat_day` sits at
roughly half those numbers.

Build: `snoozer_build_volat.py`, 1,006s over 2,514 days → 24.8M ticker-days.

## ⭐ §1 The result, against a BOOTSTRAP null (not one control draw)

`snoozer_volat_test.py` printed one random-half control, and on the SHORT side that
single draw read **PF 4.681 against the incumbent's 3.948** — a random half beat the
cell it came from. At n≈380 with a fat tail, one draw is a coin flip. So
`snoozer_volat_robust.py` re-tests every candidate against **2,000 random subsets of
the same size**, drawn from the same cell. Incumbent cell: `gaps ≤ 760 ∧ shape ≥ q75`,
n = 370, PF 2.296, worst −55%.

| filter (half the cell) | n | PF | null p50 | null p95 | ⭐ pctile | worst% |
|---|---:|---:|---:|---:|---:|---:|
| **`volat_open60` ≤ cell median** | 185 | **4.875** | 2.294 | 2.945 | **100.0** | **−24** |
| `volat_open30` ≤ median | 185 | 4.670 | 2.311 | 2.957 | 100.0 | −24 |
| `volat_open15` ≤ median | 185 | 4.502 | 2.303 | 2.916 | 100.0 | −24 |
| `volat_dayfull` ≤ median | 185 | 3.825 | 2.295 | 2.905 | 100.0 | −24 |
| `volat_day` ≤ median | 185 | 3.700 | 2.295 | 2.910 | 100.0 | −24 |
| `volat_lh` ≤ median | 185 | 3.482 | 2.293 | 2.925 | 99.7 | −26 |
| `volat_over_open30` ≥ median | 185 | 3.157 | 2.323 | 2.910 | 98.2 | −30 |
| ⚠ `gaps` ≤ median (INCUMBENT tightened) | 185 | 2.639 | 2.302 | 2.917 | 81.9 | −35 |
| ⚠ `shape` ≥ median (INCUMBENT tightened) | 185 | 2.511 | 2.304 | 2.970 | 71.7 | −35 |
| `volat_open30` ≥ median (WRONG TAIL) | 185 | 1.723 | 2.297 | 2.947 | 3.5 | −55 |
| `volat_open60` ≥ median (WRONG TAIL) | 185 | 1.695 | 2.306 | 2.938 | 1.9 | −55 |

Three things make this convincing beyond the headline number:

1. **Both incumbent levers, tightened on themselves, sit at 82 and 72** — a new
   feature at 100 is not doing what they do.
2. **The opposite tail sits at 1.9–3.5**, a clean mirror. A lucky corner has no mirror.
3. ⭐ **The RELATIVE framing LOSES to the absolute one here** (98.2 vs 100.0) — the
   opposite of dollars and bars, where relative was the productive framing. Volatility
   is already rate-free, so it does not need the relative treatment to be comparable
   across names, and normalising it away throws information out.

## §2 A PLATEAU, not a spike — and the absolute threshold

A median is not a tradeable rule. Sweeping the absolute threshold on `volat_open60`:

| keeps | T (bp) | n | PF | null p50 | pctile | mean% | med% | win% | worst% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20% | 28.45 | 74 | 3.295 | 2.310 | 88.4 | +3.98 | +3.78 | 74 | −17 |
| 30% | 36.97 | 111 | 3.619 | 2.291 | 97.6 | +4.06 | +3.81 | 76 | −17 |
| 40% | 49.19 | 148 | 3.542 | 2.280 | 99.6 | +4.50 | +4.01 | 74 | −24 |
| **50%** | **66.28** | **185** | **4.875** | 2.297 | **100.0** | +6.33 | +4.45 | 74 | −24 |
| 60% | 88.96 | 222 | 4.388 | 2.303 | 100.0 | +6.64 | +4.45 | 70 | −24 |
| 70% | 109.10 | 259 | 3.775 | 2.303 | 100.0 | +6.56 | +4.10 | 67 | −30 |
| 80% | 133.20 | 296 | 2.844 | 2.300 | 99.9 | +5.81 | +3.66 | 64 | −30 |

97.6+ from 30% to 80% — the whole interior is significant, so **do not tune inside it**.
`volat_open60 ≤ ~66bp` is the working absolute threshold at the 50% cut.

## ⭐ It CUTS LOSSES rather than adding gains — and that is the point

Mean return barely moves (+5.60% → +6.33%) while PF more than doubles, win rate goes
58% → 74%, and **the worst trade goes −55% → −24%**. Per year, every year with ≥5
trades is above 1.0:

| variant | n | PF | 2019 | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---|---|---|---|---|---|---|---|
| incumbent | 370 | 2.296 | 4.15 (7) | 2.66 (58) | 1.96 (96) | 1.11 (26) | 2.36 (29) | 4.14 (45) | 1.37 (57) | 2.63 (47) |
| **+ `volat_open60` ≤ 66.3bp** | 185 | **4.875** | 1.99 (6) | 6.56 (30) | 1.99 (49) | 18.50 (11) | 5.47 (13) | 9.61 (23) | 2.87 (19) | 30.89 (31) |

⚠ 2026 (30.89, n=31) and 2022 (18.50, n=11) are near-zero-loss cells and carry more
of the headline than their trade counts suggest. The claim that survives is the
**tail cut**, not the PF magnitude — and §S43cg below confirms it holds all the way
out to the full 4,164-trade population.

⚠ **State the tail in p5 / worst-5% terms, NOT "the worst trade".** §S43cg §4 shows
`min` is unstable enough to invert the conclusion: on the wide cells it sits at −90%
for every volatility cut AND for the base, while p5 goes −18.8% → −10.1%. This is
`feedback_trim_bottom_5pct_when_comparing` biting in a new place.

## ⭐⭐ It is NOT a liquidity proxy — the substitution test

The obvious objection: low-volatility names are big liquid names, and the user's prior
is that *"it's the liquid names which work better whenever we looked"*. So every
liquidity measure was run at matched selectivity inside the same cell, against the same
2,000-draw null:

| filter (half the cell) | n | PF | pctile | worst% | win% |
|---|---:|---:|---:|---:|---:|
| **`volat_open60` ≤ median** | 185 | **4.875** | **100.0** | −24 | 74 |
| `dv_lh` ≥ median (last-hour $) | 185 | 2.671 | 85.4 | −35 | 63 |
| `close_d` ≥ median (price) | 185 | 2.665 | 83.9 | −35 | 65 |
| `nbDay` ≥ median (day bar count) | 185 | 2.203 | 39.6 | −55 | 53 |
| `dv_rest` ≥ median (day $) | 185 | 1.855 | 8.0 | −55 | 54 |

**No liquidity measure clears the null.** And low volatility still nearly doubles PF
*inside* each liquidity-high half — where a proxy would have nothing left to add:

    inside dv_lh-high   (n=185, PF 2.671)  ->  + volat low  n=116  PF 4.425
    inside close_d-high (n=185, PF 2.665)  ->  + volat low  n=119  PF 5.365
    inside nbDay-high   (n=185, PF 2.203)  ->  + volat low  n= 72  PF 4.876

Overlap with dollars/price is 63–64% — correlated, as expected — but the residual is
what carries the result. ⚠ This is the §S43ca lesson again: correlation is not
substitutability, and the substitution test is the only thing that settles it.

## The reading

The long spec buys a **dense, loud** closing flush (`gaps` low, `shape` high) — heavy
continuous late selling that exhausts and bounces. Volatility adds an orthogonal
condition: **that flush should happen in a name that is not violent to begin with.**
A −6% last hour in a name whose whole morning ran at 30bp/30s is a normal day for it
and carries no information; the same −6% in a name that spent the morning at 15bp/30s
is a genuine dislocation. That is why it cuts the tail rather than raising the mean —
it removes the trades where −6% never meant anything.

**SPEC (long, updated):** `gaps ≤ 760 ∧ dv_over_open15 ≥ q75 ∧ chg60k59 < −6%
∧ volat_open60 ≤ ~66bp`, LIMIT 15:59–16:00, exit next open.
PF 4.875 · mean +6.33% · median +4.45% · win 74% · worst −24% · **0 losing years**.

⚠ **SUPERSEDED IN FORM by §S43ch** — a one-sided `≤ T` floor is the wrong shape. The
relation is an INVERTED U and the quietest decile is *below* baseline; use a BAND.
The wide-book form `shape ≥ q50 ∧ volat_open15 ∈ [34, 83)bp` (n=851, PF 2.250) is the
better-evidenced spec. The direction and the tail claim are unchanged.

Scripts: `snoozer_build_volat.py` (cache) · `snoozer_volat_test.py` (deciles,
substitution, stacking, overlap) · `snoozer_volat_robust.py` (bootstrap + sweep).

---

# ⭐⭐ S43cg (2026-08-16) — CONFIRMED on 2,082 and 4,164 trades, and it is the ONLY TAIL LEVER

⭐ USER: *"Instead of the 370 trade incumbent, we should verify the volatility feature
against shape ≥ q50%. The 2k trade bucket would give us much better confirmation."*

## Why widening is the right test, not merely a bigger one

A 370-trade cell splits into halves of 185, where the bootstrap null's **own p95 sits
26.6% above its median**. Nothing can be resolved below that floor. Widening drops it:

| base cell | n | base PF | null p50 | null p95 | ⚠ floor p95/p50 | `volat_open60` PF | pctile |
|---|---:|---:|---:|---:|---:|---:|---:|
| `gaps ≤ 760 ∧ shape ≥ q75` | 370 | 2.296 | 2.299 | 2.910 | **1.266** | 4.875 | 100.0 |
| `gaps ≤ 760 ∧ shape ≥ q50` | 622 | 1.868 | 1.876 | 2.257 | 1.203 | 3.380 | 100.0 |
| ⭐ `shape ≥ q50` (no gaps) | 2,082 | 1.621 | 1.630 | 1.822 | 1.118 | 1.980 | 99.9 |
| no filter at all | 4,164 | 1.200 | 1.198 | 1.331 | **1.110** | 1.509 | 100.0 |

So a real lever should get MORE significant as the cell grows (the floor drops faster
than the effect), while an artefact of the narrow cut decays toward the floor. **That
divergence is the test.**

⚠ The four cells are NESTED — this is one population read at four depths, not four
independent confirmations.

## ⭐ §1 It survives everywhere; the incumbent levers do not

Bootstrap percentile, each feature cut at its own median WITHIN each cell:

| feature (cut at cell median) | 370 | 622 | **2,082** | **4,164** |
|---|---:|---:|---:|---:|
| `volat_open15` ≤ | 100.0 | 100.0 | **100.0** | **100.0** |
| `volat_open30` ≤ | 100.0 | 100.0 | 99.9 | 100.0 |
| `volat_open60` ≤ | 100.0 | 100.0 | 99.9 | 100.0 |
| `volat_day` ≤ | 99.9 | 99.2 | 100.0 | 99.0 |
| `volat_dayfull` ≤ | 99.9 | 98.4 | 100.0 | 99.6 |
| `volat_lh` ≤ | 99.7 | 98.9 | 100.0 | 96.1 |
| `volat_over_open30` ≥ | 98.7 | 99.8 | 94.9 | 99.7 |
| ⚠ `volat_over_day` ≥ | 97.5 | 97.0 | **81.0** | **74.2** |
| ⚠ `gaps` (INCUMBENT) | 83.4 | 96.9 | **64.4** | 99.2 |
| `shape` (INCUMBENT) | 71.9 | 99.0 | 99.6 | 100.0 |

Three readings:

1. ⭐ **The three opening-volatility windows are pinned at 99.9–100.0 at every depth.**
   Nothing else in the table is that stable.
2. ⚠ **`volat_over_day` DECAYS — 97.5 → 97.0 → 81.0 → 74.2.** That is the artefact
   signature the ladder was built to catch, and it is precisely the feature that
   looked best on the SHORT side (§S43cf, 94.8). The ladder is working.
3. ⚠ **`gaps` is erratic (83.4 / 96.9 / 64.4 / 99.2)** — the incumbent persistence
   lever is the least stable thing here.

⚠ **The LIFT SHRINKS as the cell widens** — `volat_open60` runs 2.12× / 1.81× / 1.22× /
1.26× the base PF. It is real at every depth but does the most work INSIDE the tight
`gaps ∧ shape` cell, i.e. it interacts with the incumbents rather than replacing them.
`shape`'s lift moves the other way (1.09× → 1.30× → 1.22× → 1.35×).

## ⭐⭐ §2 THE HEADLINE: volatility is the only lever that touches the TAIL

Mean of the **worst 5%** (never `min` — see below):

| feature | 370 | 622 | 2,082 | 4,164 |
|---|---|---|---|---|
| base | −26.7% | −28.6% | −28.7% | −30.6% |
| **`volat_open15` ≤ med** | **−14.3%** | **−16.0%** | **−18.2%** | **−22.3%** |
| `volat_open30` ≤ med | −14.3% | −16.7% | −19.2% | −23.3% |
| `volat_open60` ≤ med | −14.3% | −17.2% | −20.9% | −23.8% |
| `volat_dayfull` ≤ med | −14.6% | −20.2% | −18.1% | −21.5% |
| `volat_over_open30` ≥ med | −23.1% | −27.6% | −30.1% | −29.3% |
| ⚠ `gaps` (INCUMBENT) | −26.6% | −27.3% | **−30.6%** | **−32.6%** |
| ⚠ `shape` (INCUMBENT) | −25.2% | −26.6% | −27.3% | −28.7% |

**Neither incumbent lever touches the tail at any width** — `gaps` actually makes it
WORSE on the two wide cells. `gaps` and `shape` raise PF by finding winners; only
absolute volatility removes losses. Supporting percentiles on `shape ≥ q50` (n=2,082):

    base            PF 1.621   p1 -33.0   p5 -18.8   worst5% -28.7   loss rate 43%
    + volat_open30  PF 2.061   p1 -22.0   p5 -10.1   worst5% -19.2   loss rate 36%

and on the full 4,164: p5 −20.3 → −13.4, worst-5% −30.6 → −23.3, loss rate 51% → 42%.

⚠⚠ **`min` LIES HERE and would have reversed the conclusion.** On both wide cells the
single worst trade is −90% for the base AND for every volatility cut — reading it
alone says "the tail cut does not survive widening", which is false in every robust
statistic. `feedback_trim_bottom_5pct_when_comparing` in a new place: the worst trade
is the least stable number in the book, so **report p5 / worst-5% / loss rate**.

## Practical consequence

The `gaps ≤ 760` door can be dropped without losing the volatility result: on
`shape ≥ q50` alone, `gaps` reads 64.4 percentile while `volat_open15` reads 100.0.
The tight `gaps ∧ shape ≥ q75 ∧ volat_open60` spec (PF 4.875, n=370) remains the
best-performing cell, but the wide `shape ≥ q50 ∧ volat_open30 ≤ med` book —
**n=1,041, PF 2.061, worst-5% −19.2%** — is 2.8× the trade count at a PF the
pre-volatility system could not reach at any selectivity, and is the more credible
basis for sizing.

Script: `snoozer_volat_ladder.py`.

---

# ⭐⭐ S43ch (2026-08-16) — it is an INVERTED U, not a floor: the median split was hiding the bottom decile

⭐ USER: *"When testing the vol features, are you just looking at the bottom half using
the median as the threshold?"*

Yes — §S43cf §1 and §S43cg §1 both cut at the **cell median**. That was deliberate (it
fixes `k = n/2` so the bootstrap null is identical across features, which is what makes
the percentile column comparable across the ladder) but it is a COARSE instrument: it
cannot distinguish a monotone gradient from one good decile, and only ONE threshold
sweep had been run — `volat_open60`, on the narrow 370-trade cell.

Swept on the 2,082-trade cell, **the gradient is not monotone.**

## The decile table the median split was averaging over

`volat_open15` inside `shape >= q50` (n = 2,082, base PF 1.621):

| band | range | n | PF | mean% | med% | win% | p5% | worst5% | loss% |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **D1** | 3–34bp | 209 | **1.559** | +1.17 | +1.46 | 67 | −11.0 | −20.1 | **33** |
| D2 | 34–47bp | 208 | **2.691** | +2.70 | +2.25 | 69 | −7.0 | −20.3 | 31 |
| D3 | 47–57bp | 208 | **2.747** | +2.79 | +1.47 | 66 | −9.9 | −13.4 | 34 |
| D4 | 57–69bp | 208 | 1.597 | +1.31 | +0.79 | 56 | −10.5 | −17.9 | 44 |
| D5 | 69–83bp | 208 | 2.127 | +2.81 | +1.11 | 58 | −11.0 | −20.0 | 42 |
| D6 | 83–99bp | 208 | 1.791 | +2.52 | +1.17 | 59 | −14.0 | −26.9 | 41 |
| D7 | 99–118bp | 208 | 2.041 | +3.67 | +0.95 | 56 | −15.4 | −20.8 | 44 |
| D8 | 118–150bp | 208 | 1.697 | +3.60 | −0.25 | 50 | −19.7 | −32.1 | 50 |
| D9 | 150–198bp | 208 | 1.276 | +2.25 | −3.08 | 43 | −30.6 | −42.7 | 57 |
| D10 | 198–914bp | 209 | 1.119 | +0.98 | −3.89 | 44 | −25.7 | −37.3 | **56** |

⭐ **D1 — the QUIETEST decile — reads 1.559, BELOW the 1.621 base.** The cumulative
sweep agrees: bottom-10% gives lift 0.96× at the 43.2 percentile (nothing), and for
`volat_open30` bottom-10% is 0.73× at the **7.8** percentile — actively harmful.

## ⭐ Why: the TAIL is monotone even though PF is not

Read the last two columns straight down — loss rate 33% → 56%, worst-5% −20% → −37%,
p5 −11% → −26%. Risk rises monotonically with volatility exactly as §S43cg claimed.
But D1's MEDIAN return is only +1.46%: an ultra-quiet name has small moves, so the
overnight bounce is small too. **Good tail, poor payoff.** The two effects cross around
D2–D3, which is where PF peaks.

This also explains why the median split still worked — the median sits at ~83bp, far
enough out that the weak bottom decile is diluted by D2–D5.

## The band, and how much of it is load-bearing

| rule | n | PF | lift | pctile | mean% | med% | win% | p5% | worst5% | loss% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **`shape >= q50`, n = 2,082** | | | | | | | | | | |
| base | 2,082 | 1.621 | 1.00× | 6.1 | +2.38 | +0.99 | 57 | −18.8 | −28.7 | 43 |
| `< 83bp` (floor only) | 1,049 | 2.081 | 1.28× | 100.0 | +2.14 | +1.42 | 63 | −10.3 | −18.2 | 37 |
| `[34, 118)bp` | 1,263 | 2.118 | 1.31× | 100.0 | +2.70 | +1.33 | 61 | −12.0 | −20.2 | 39 |
| ⭐ **`[34, 83)bp`** | 851 | **2.250** | **1.39×** | 100.0 | +2.42 | +1.43 | 63 | −10.2 | **−17.7** | 37 |
| **no filter at all, n = 4,164** | | | | | | | | | | |
| base | 4,164 | 1.200 | 1.00× | 3.0 | +0.95 | −0.30 | 49 | −20.3 | −30.6 | 51 |
| `< 83bp` (floor only) | 1,213 | 1.927 | 1.61× | 100.0 | +1.91 | +1.30 | 62 | −10.5 | −18.1 | 38 |
| ⭐ **`[34, 83)bp`** | 1,014 | **2.061** | **1.72×** | 100.0 | +2.13 | +1.26 | 61 | −10.3 | −17.2 | 39 |

⚠ **The UPPER bound is the load-bearing half.** Adding the lower bound buys ~8% of PF
for ~19% of the trades and moves the tail almost not at all (−18.2% → −17.7%). It is
removing LOW-PAYOFF trades, not risky ones — a real refinement, but not the lever, and
the first thing to drop if trade count matters.

⚠ Per-year, the band does NOT repair the two weak years — `shape >= q50` base reads
0.78 (2016) / 0.81 (2019); the `[34,118)` band reads 0.22 (n=10) / 0.87. It lifts the
good years (2018 1.10 → 2.77, 2024 2.41 → 4.16, 2025 1.12 → 1.99) rather than fixing
the bad ones.

⚠ **Thresholds are ABSOLUTE bp and were read off a decile grid, so treat 34 and 83 as
the centre of a plateau, not tuned values.** D2–D5 all sit between 1.60 and 2.75 with
overlapping intervals at these counts; do not narrow the band further on this evidence.

---

# ⭐⭐ S43ci (2026-08-16) — WHICH volatility feature is best? A matched-selectivity head-to-head

⭐ USER: *"On the 2k bucket, which feature works the best? You've only shown me
percentiles so far."*

Fair — a percentile answers "is it real", not "which is best". Every feature below
keeps **exactly the same number of trades** and picks its own best CONTIGUOUS band of
deciles, so the comparison is at matched selectivity and each feature is shown at its
own optimum rather than at an arbitrary median split.

⚠ Each feature chooses the best of 6 candidate windows, so these PFs carry a mild
in-sample selection premium. The defence is that the ranking is stable across two
selectivities and that all six volatility windows cluster tightly — not a smaller p.

## Base: `shape ≥ q50`, n = 2,082, PF 1.621, mean +2.38%, worst-5% −28.7%, loss 43%

**KEEP 5 of 10 deciles (50%)**

| feature | best band | n | PF | lift | mean% | med% | win% | p5% | worst5% | loss% | yrs<1 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ⭐ **`volat_open30`** | D2–D6 [30, 85)bp | 1,040 | **2.217** | 1.37× | +2.61 | +1.48 | 62 | −10.7 | −19.6 | 38 | 1 |
| `volat_day` | D1–D5 [2, 39)bp | 1,041 | 2.169 | 1.34× | +2.13 | +1.75 | **66** | −9.7 | −18.4 | **34** | 1 |
| `volat_open60` | D2–D6 [25, 72)bp | 1,040 | 2.134 | 1.32× | +2.55 | +1.58 | 62 | −11.0 | −21.6 | 38 | 1 |
| `volat_dayfull` | D1–D5 [4, 39)bp | 1,041 | 2.115 | 1.30× | +1.99 | +1.63 | **66** | −9.6 | −18.1 | **34** | 1 |
| `volat_open15` | D2–D6 [34, 99)bp | 1,040 | 2.096 | 1.29× | +2.43 | +1.35 | 61 | −10.9 | −19.8 | 39 | 1 |
| `volat_lh` | D1–D5 [9, 39)bp | 1,041 | 2.034 | 1.25× | +1.87 | +1.50 | 64 | −9.7 | **−17.1** | 36 | 1 |
| ⚠ `shape` (INCUMBENT) | D6–D10 | 1,041 | 1.984 | 1.22× | **+3.40** | +1.21 | 59 | −15.8 | −27.3 | 41 | 2 |
| `volat_over_open30` | D6–D10 | 1,041 | 1.821 | 1.12× | +3.13 | +1.21 | 58 | −17.7 | −30.1 | 42 | 2 |
| `volat_over_open15` | D6–D10 | 1,041 | 1.770 | 1.09× | +3.00 | +1.05 | 56 | −17.0 | −29.7 | 44 | 2 |
| `volat_over_day` | D5–D9 | 1,040 | 1.758 | 1.08× | +2.42 | +1.23 | 60 | −15.3 | −26.7 | 40 | 1 |
| ⚠ `gaps` (INCUMBENT) | D1–D5 | 1,040 | 1.669 | 1.03× | +3.22 | +1.23 | 54 | −21.5 | −30.6 | 46 | 1 |

**KEEP 4 of 10 deciles (40%)** — same ordering at the top:

| feature | best band | n | PF | lift | worst5% | loss% |
|---|---|---:|---:|---:|---:|---:|
| ⭐ **`volat_open30`** | D2–D5 [30, 71)bp | 832 | **2.374** | 1.46× | −17.1 | 37 |
| `volat_open15` | D2–D5 [34, 83)bp | 832 | 2.220 | 1.37× | −17.9 | 38 |
| `volat_dayfull` | D1–D4 [4, 33)bp | 833 | 2.181 | 1.35× | −17.0 | 33 |
| `volat_day` | D2–D5 [16, 39)bp | 832 | 2.178 | 1.34× | −18.9 | 36 |
| `volat_open60` | D3–D6 [33, 72)bp | 832 | 2.163 | 1.33× | −21.9 | 39 |
| `volat_lh` | D1–D4 [9, 32)bp | 833 | 2.094 | 1.29× | −16.5 | 33 |
| ⚠ `shape` | D7–D10 | 833 | 1.884 | 1.16× | −28.4 | 41 |
| ⚠ `gaps` | D1–D4 | 832 | 1.736 | 1.07× | −28.9 | 46 |

## The readings

⭐ **`volat_open30` wins at both selectivities** — 2.217 and 2.374, band ≈ **[30, 85)bp**.

⚠⚠ **But the volatility family is a statistical TIE.** At n≈1,041 the bootstrap noise
floor is 1.12 (§S43cg §3), i.e. ±0.26 PF. The six windows span 2.034–2.217, a range of
0.18 — INSIDE the floor. `volat_open30` is the point estimate, not a demonstrated
winner over `volat_day` or `volat_open15`. Choose on secondary grounds:

- **`volat_open30` / `open15`** are known by **10:00 / 09:45** — hours before the
  entry, so they can gate an intraday watchlist rather than a 15:59 decision.
- **`volat_day` / `dayfull` / `lh`** need no LOWER bound (they pick D1–D5, a plain
  floor) and give the best win rate (66%) and loss rate (34%) — but are only known at
  15:00 / 15:59.
- ⭐ **`volat_lh` has the best tail of all** (worst-5% −17.1%, −16.5% at the tighter
  cut) despite the lowest PF. If the tail is the binding constraint, it is the pick.

⭐ **THE TRADE-OFF vs `shape` is explicit here.** `shape` has the HIGHEST mean return
(+3.40% vs +2.61%) and the WORST tail (−27.3% vs −19.6%). `shape` finds big winners;
volatility removes losses. They are doing different jobs, which is why they stack.

⚠ **`gaps` is barely a feature at this width** — 1.03× lift, 65.8 percentile.

## Is `shape` still needed? Yes — they stack

On the FULL 4,164 population, with `volat_open30 ∈ [30, 85)bp`:

| rule | n | PF | mean% | med% | win% | p5% | worst5% | loss% | losing years |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| base | 4,164 | 1.200 | +0.95 | −0.30 | 49 | −20.3 | −30.6 | 51 | 2018, 2019, 2022 |
| `shape ≥ q50` only | 2,082 | 1.621 | +2.38 | +0.99 | 57 | −18.8 | −28.7 | 43 | 2016, 2019 |
| **`volat_open30` band only** | 1,398 | 1.828 | +2.02 | +1.21 | 60 | −11.7 | −21.4 | 40 | **2022** |
| ⭐ **BOTH** | 1,038 | **2.217** | +2.64 | +1.49 | 62 | −10.9 | −19.8 | 38 | **2019** |

🛑 **THIS ROW COMPARISON IS NOT MATCHED — SEE §S43cj.** The volatility band keeps 1,398
trades and `shape >= q50` keeps 2,082, so reading "volatility alone (1.828) beats shape
alone (1.621)" off this table is a SELECTIVITY ARTEFACT, exactly the error
`feedback_iso_trip_control_for_stacked_features` exists to prevent. At matched n,
`shape` is slightly AHEAD on PF at every cut. The stacking result (2.217 from both, at
74% overlap) stands — only the head-to-head claim was wrong.

⚠ Neither fixes 2019, and the combined rule's 2016 cell is 0.14 on n=8. The weak years
are a different variable; see the §NEXT SESSION note.

Script: the head-to-head is a one-off; the reusable pieces are `snoozer_volat_ladder.py`
and `snoozer_volat_robust.py`.

---

# ⭐⭐ S43cj (2026-08-16) — the PLAIN MEDIAN split, and two corrections

⭐ USER: *"I don't really like that you're clipping the lower band though. What if you
used the median as the threshold, how would the features compare then?"*

Right instinct — the lower bound of §S43ch bought ~8% of PF for 19% of the trades and
is the piece most likely to be fitted. Here is every feature as a plain ONE-SIDED
median split, no band, no per-feature window search, so nothing is chosen in-sample.

## §1 On the 2k bucket (`shape >= q50`, n = 2,082, base PF 1.621, worst-5% −28.7%, loss 43%)

| feature | rule | n | PF | lift | pctile | mean% | med% | win% | p5% | worst5% | loss% | yrs<1 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `volat_day` | ≤ 39.4bp | 1,041 | **2.169** | 1.34× | 100.0 | +2.13 | +1.75 | **66** | −9.7 | −18.4 | **34** | 1 |
| `volat_dayfull` | ≤ 39.5bp | 1,041 | 2.115 | 1.30× | 100.0 | +1.99 | +1.63 | **66** | −9.6 | −18.1 | **34** | 1 |
| `volat_open15` | ≤ 82.6bp | 1,041 | 2.081 | 1.28× | 100.0 | +2.15 | +1.43 | 63 | −10.4 | −18.2 | 37 | 2 |
| `volat_open30` | ≤ 70.7bp | 1,041 | 2.061 | 1.27× | 100.0 | +2.06 | +1.51 | 64 | −10.1 | −19.2 | 36 | 1 |
| `volat_lh` | ≤ 39.2bp | 1,041 | 2.034 | 1.25× | 100.0 | +1.87 | +1.50 | 64 | −9.7 | **−17.1** | 36 | 1 |
| ⚠ `shape` | ≥ 2.0 | 1,041 | 1.984 | 1.22× | 99.5 | **+3.40** | +1.21 | 59 | −15.8 | −27.3 | 41 | 2 |
| `volat_open60` | ≤ 58.9bp | 1,041 | 1.980 | 1.22× | 99.9 | +2.03 | +1.45 | 64 | −10.5 | −20.9 | 36 | 2 |
| `volat_over_open30` | ≥ 0.6 | 1,041 | 1.821 | 1.12× | 94.7 | +3.13 | +1.21 | 58 | −17.7 | −30.1 | 42 | 2 |
| `volat_over_open15` | ≥ 0.5 | 1,041 | 1.770 | 1.09× | 89.1 | +3.00 | +1.05 | 56 | −17.0 | −29.7 | 44 | 2 |
| `volat_over_day` | ≥ 1.0 | 1,041 | 1.725 | 1.06× | 80.8 | +2.44 | +1.21 | 60 | −15.3 | −28.5 | 40 | 3 |
| ⚠ `gaps` | ≤ 1431 | 1,042 | 1.666 | 1.03× | 64.4 | +3.20 | +1.22 | 54 | −21.5 | −30.6 | 46 | 1 |

Dropping the band costs remarkably little: `volat_open15` goes 2.096 (banded) → 2.081
(median), `volat_open30` 2.217 → 2.061. **The lower bound was worth ~0.02–0.16 PF, not
a structural part of the effect.** Every absolute window still sits at 99.9–100.0
percentile, and the relative family still trails.

## ⚠⚠ §2 CORRECTION 1 — the ranking is NOT stable across populations

The same median splits on the FULL 4,164 (base PF 1.200):

| feature | rule | n | PF | lift | mean% | win% | worst5% | loss% | yrs<1 | rank here / on 2k |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ⚠ `shape` | ≥ 0.9 | 2,082 | **1.621** | 1.35× | +2.38 | 57 | −28.7 | 43 | 2 | 1st / 6th |
| ⭐ `volat_open30` | ≤ 104.2bp | 2,082 | 1.574 | 1.31× | +1.64 | 58 | −23.3 | 42 | **1** | 2nd / 4th |
| ⭐ `volat_open15` | ≤ 122.4bp | 2,082 | 1.556 | 1.30× | +1.58 | 57 | −22.3 | 43 | **1** | 3rd / 3rd |
| `volat_open60` | ≤ 85.9bp | 2,082 | 1.509 | 1.26× | +1.46 | 58 | −23.8 | 42 | **1** | 4th / 7th |
| `volat_dayfull` | ≤ 51.9bp | 2,082 | 1.417 | 1.18× | +1.10 | 58 | −21.5 | 42 | 2 | 5th / 2nd |
| ⚠ `gaps` | ≤ 1844 | 2,083 | 1.390 | 1.16× | +2.15 | 49 | −32.6 | 51 | 3 | 8th / 11th |
| ⚠ `volat_day` | ≤ 52.6bp | 2,082 | **1.386** | 1.16× | +1.05 | 58 | −22.0 | 42 | 2 | **9th / 1st** |
| `volat_lh` | ≤ 45.4bp | 2,082 | 1.346 | 1.12× | +0.96 | 56 | −21.6 | 44 | 3 | 11th / 5th |

⚠⚠ **`volat_day` is 1st on the 2k bucket and 9th on the full population.** So is
`volat_lh` (5th → 11th). The afternoon-anchored windows only look good once `shape` has
already been applied — their apparent edge is partly `shape`'s, re-expressed.

⭐ **Only `volat_open15` and `volat_open30` are stable**, sitting 2nd–4th in BOTH
populations with 1 losing year each. **The opening windows are the robust choice**, and
they have the operational advantage anyway (known by 09:45/10:00, so they can gate a
watchlist hours before the 15:59 entry). §S43ci's nomination of `volat_open30` survives
— but for this reason, not the one given there.

## 🛑 §3 CORRECTION 2 — "volatility alone beats shape alone" was a SELECTIVITY ARTEFACT

§S43ci compared the `volat_open30` band at **n=1,398** against `shape ≥ q50` at
**n=2,082** and concluded volatility was the stronger lever. Those are different
selectivities; PF rises mechanically as trades are cut. At MATCHED n:

| keep | `volat_open30 ≤ T` | | | | `shape ≥ T` | | | |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| | PF | worst5% | loss% | yrs<1 | PF | worst5% | loss% | yrs<1 |
| 50% (n=2,082) | 1.574 | **−23.3** | 42 | **1** | **1.621** | −28.7 | 43 | 2 |
| 40% (n=1,666) | 1.693 | **−22.2** | 40 | **1** | **1.831** | −28.2 | 41 | 2 |
| 34% (n=1,399) | 1.809 | **−20.9** | 38 | **0** | **1.847** | −28.6 | 42 | 2 |
| 25% (n=1,041) | 1.944 | **−18.1** | 37 | **0** | **1.984** | −27.3 | 41 | 2 |

And the direct fix to the §S43ci row — the band against a `shape` cut at the same n:

    volat_open30 [30,85)bp   n=1,398  PF 1.828  worst5% -21.4%  loss 40%  1 losing yr
    shape >= 1.48            n=1,398  PF 1.848  worst5% -28.6%  loss 41%  2 losing yrs

**`shape` is slightly ahead on PF at every cut.** The reversal claimed in §S43ci is
WITHDRAWN — `shape` remains the higher-PF lever, as §S43cb had it.

⭐ **But the "different jobs" reading is now much stronger, not weaker.** At every
matched cut volatility delivers a **7–9pp better worst-5%**, a lower loss rate, and
**0–1 losing years against `shape`'s 2**, while `shape` carries nearly double the mean
return (+3.40% vs +1.85% at 25%). `shape` buys expectancy; volatility buys survival.
That is why they stack to 2.217 at 74% overlap, and it is the honest form of the claim.

⚠ Lesson repeated: `feedback_iso_trip_control_for_stacked_features` — **never compare
two filters that keep different numbers of trades.** This is the second time in this
session that an unmatched comparison produced a wrong headline (the first was the
single random-half control, §S43cf).

---

# ⭐⭐ S43ck (2026-08-16) — ONE OPENING WINDOW FOR THE SYSTEM: 30m, and KEEP relative persistence

⭐ USER: *"Just like we're evaluating volatility during set windows, we should do the
same for relative gaps and relative dollar volume. So far we have 4 main features:
volatility, absolute gaps, relative dv (intensity) and relative gaps (persistence).
We'll probably omit the last one and use absolute rather than relative persistence...
I feel that for whatever system we pick, we should use the same opening window for the
features, either 30m or 60m. Probably 30m."*

## Why the alignment was needed

`shape` = `dv_over_open15`, and **that window was never chosen** — it is what
`dv_0945_tape` covers, because that column is the FlushFader UNIVERSE GATE. S43cb swept
the reference length only on the SHORT side and found a shallow optimum there. The long
side never had the sweep. Volatility then arrived with its own independent ladder. So
the system was mixing a 15-minute denominator with a 60-minute volatility window for no
reason anyone chose.

⚠ `gaps` (= 3540 − nb60k59, seconds of (15:00,15:59] that did not trade) has NO opening
window by construction, so it cannot vote on W.

Everything below is a one-sided MEDIAN split (§S43cj), so every single-feature cell is
exactly n/2 — matched by construction, nothing chosen in-sample.

## ⭐ §1 Per family, on the full 4,164 (base PF 1.200)

| family | 15m | 30m | 60m | best |
|---|---:|---:|---:|---|
| **volatility** `volat_open*` | 1.556 | **1.574** | 1.509 | 30m — but FLAT (range 0.065) |
| **intensity** `dv_over_open*` | 1.621 | 1.677 | **1.723** | ⭐ 60m, and MONOTONE |
| **persistence** `bar_over_open*` | 1.532 | 1.531 | **1.603** | 60m |
| ⚠ **absolute gaps** (no window) | — | 1.390 | — | worst single feature |

Losing years: volatility 1/1/1 · intensity 2/**1**/2 · persistence 2/2/2.
Worst-5%: volatility −22.3/−23.3/−23.8 · intensity −28.7/−28.3/−28.2.

⭐⭐ **The inherited 15m is the WORST choice for intensity, and the relation is
monotone (1.621 → 1.677 → 1.723).** The universe gate's window was actively costing
the long side. Note this is the OPPOSITE of the short side, where §S43cb found 15m beat
rest-of-day 3.476 → 2.580 — another sign-flip between the two systems.

⚠ No window wins for all three families. 60m takes intensity and persistence, 30m takes
volatility. The decision therefore rests on the COMBINED spec, not on §1.

## ⭐⭐ §2 The combined spec, three filters at a COMMON quantile q

`volat_open{W}` low ∧ `dv_over_open{W}` high ∧ `bar_over_open{W}` high:

| q | W=15m | W=30m | W=60m |
|---|---|---|---|
| 70% | 1.838 (n=1893, 1 yr<1) | 1.733 (n=1917, **0**) | 1.722 (n=1941, 2) |
| 60% | 2.116 (n=1454, 1) | **2.172** (n=1470, 2) | 2.137 (n=1515, **0**) |
| 50% | 2.329 (n=1084, 2) | **2.420** (n=1092, 2) | 2.334 (n=1158, 2) |
| 40% | 2.439 (n=767, 1) | **2.451** (n=791, 1) | 2.387 (n=828, 1) |

Two-filter version (`vol × int`, no persistence) at q=50%: 15m 2.033 (2 losing yrs),
30m 2.013 (**0**), 60m 2.042 (**0**).

⭐ **30m wins the three-filter spec at q=40/50/60.** ⚠ But the margins are 0.03–0.09 PF
on n≈1,100 — well inside the noise floor. **The honest statement is that W barely
matters once the three features are combined**, and 30m is chosen because it never
loses, not because it demonstrably wins. The user's instinct was right for a reason the
single-feature table does not show.

## 🛑 §3 THE ONE PLACE THE DATA DISAGREES WITH THE PLAN

The proposal was to drop relative persistence and keep absolute `gaps`. Tested directly
at W=30m, on the `vol × int` cell (n=1,509, PF 2.013, 0 losing years):

| rule | n | PF | mean% | med% | win% | p5% | worst5% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| base `vol × int` | 1,509 | 2.013 | +2.46 | +1.40 | 62 | −12.0 | −21.7 | 38 | **0** |
| + ABSOLUTE `gaps` | 756 | 2.636 | +4.07 | +2.52 | 63 | −12.1 | −20.1 | 37 | 1 |
| ⭐ + RELATIVE `bar_over_open30` | 755 | **2.888** | +3.97 | +2.30 | **67** | −10.9 | −19.4 | **33** | **0** |
| ⭐⭐ + **BOTH** | 545 | **3.258** | **+4.89** | **+2.99** | 66 | −11.0 | **−17.0** | 34 | **0** |

**Relative persistence BEATS absolute gaps** — 2.888 vs 2.636, better tail, better loss
rate, 0 losing years against 1. A paired bootstrap over 4,000 resamples of the base cell
puts **P(relative > absolute) = 80%**, point estimate +0.252, 90% CI [−0.221, +0.729].
Not decisive on its own — but it leans the opposite way to the plan, and nothing here
supports dropping the relative twin.

⭐⭐ **Better still: they are NOT substitutes.** Overlap is only 72%, ρ = −0.405, and
**BOTH together reach PF 3.258 with the best tail in the entire study (worst-5%
−17.0%) and 0 losing years.** Absolute gaps asks "did the tape stop?", relative
persistence asks "did it stop MORE than this name usually does?" — the same
absolute/relative distinction that S43cd found for dollars, and it resolves the same
way: keep both.

**RECOMMENDATION: keep all four features.** Dropping relative persistence costs
3.258 → 2.636 (−19%) and gives back a losing year.

## The aligned spec

    LONG, W = 30m:
      chg60k59 < -6%                        the signal
      volat_open30   <= median (~104bp)     volatility   — buys survival
      dv_over_open30 >= median (~0.50)      intensity    — buys expectancy
      bar_over_open30 >= median             persistence  (RELATIVE)
      gaps           <= median (~1844s)     persistence  (ABSOLUTE)
      LIMIT 15:59-16:00, exit next open
    -> n = 545, PF 3.258, mean +4.89%, median +2.99%, win 66%,
       worst-5% -17.0%, loss rate 34%, ZERO losing years

⚠ All thresholds are medians of the FULL population, i.e. absolute values, not
per-day ranks. ⚠ 545 trades over 11 years ≈ 50/yr.

⚠ **Intensity genuinely prefers 60m** (monotone 1.621/1.677/1.723) and is the one
family with a real reason to keep its own window. Aligning it to 30m is a deliberate
concession to system simplicity, not a free choice — worth revisiting if intensity ever
becomes the binding lever.

Script: `snoozer_window_alignment.py`.

---

# ⭐⭐ S43cl (2026-08-16) — THE 16-CELL COMPLEMENTS TABLE: a size ladder, not a threshold

⭐ USER: *"All of these are worth trading, just with different sizes. Let's make a
complements table... There should be a total of 16 combinations. That should be more
informative than trying to pick the thresholds. We know what our A++ book would be, but
on live trading we'd want to trade the lesser cells with smaller size."*

This is §S43cc's broker-doc lesson applied to the Snoozer: the trades outside the top
cell had PF ≈ 2 and were being thrown away. A threshold answers "in or out"; a lattice
answers "how much".

Four binary features at W = 30m, `+` always the favourable side:

    V  volat_open30    <= 104.2bp    LOW volatility
    I  dv_over_open30  >= 0.50       HIGH intensity
    B  bar_over_open30 >= 0.65       HIGH relative persistence
    G  gaps            <= 1844s      LOW absolute gaps

The 16 cells are DISJOINT and exhaust the 4,164-trade population (verified by assert).

## ⚠ FIRST — what the §S43ck `vol × int` cell actually was (user's question)

Both plain MEDIAN splits, bottom-half volatility and top-half intensity — not terciles.
It held 1,509 rather than the ~1,041 independence predicts because **the two features
are strongly rank-correlated: Spearman −0.607**, so V+ and I+ co-occur **1.45× more
than chance**. Consequence for everything below: **the cells are very unequal and the
corners are thin**. Read n before PF.

## ⭐⭐ §1 The ladder

| cell | book | n | share% | PF raw | PF trim5 | ⚠ trim lift | mult | mean% | med% | win% | worst5% | loss% | yrs<1 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `V+I+B+G−` | A++ | 307 | 7.4 | 1.720 | 4.712 | **2.74** | 1.000 | +1.43 | +1.25 | **67** | −25.2 | **33** | 1 |
| ⭐ `V+I+B+G+` | A++ | 785 | 18.9 | **2.651** | 4.457 | 1.68 | 0.931 | **+3.90** | **+2.45** | 63 | **−19.0** | 37 | 1 |
| `V−I+B−G−` | A+ | 81 | 1.9 | 1.636 | 2.376 | 1.45 | 0.371 | +2.20 | +0.75 | 57 | −21.8 | 43 | 2 |
| `V+I−B+G+` | A+ | 68 | 1.6 | 1.571 | 2.373 | 1.51 | 0.370 | +2.17 | +1.35 | 57 | −23.1 | 43 | 1 |
| `V+I−B−G+` | B++ | 64 | 1.5 | 1.277 | 1.881 | 1.47 | 0.237 | +1.09 | −1.19 | 47 | −21.2 | 53 | 1 |
| `V+I+B−G−` | B++ | 361 | 8.7 | 1.083 | 1.855 | 1.71 | 0.230 | +0.23 | +0.43 | 54 | −23.3 | 46 | 2 |
| `V−I+B+G+` | B++ | 427 | 10.3 | 1.325 | 1.765 | 1.33 | 0.206 | +2.52 | −3.22 | 44 | −37.9 | 56 | 3 |
| `V−I−B+G+` | B++ | 429 | 10.3 | 1.167 | 1.536 | 1.32 | 0.144 | +1.42 | −4.81 | 38 | −40.3 | 62 | 3 |
| 🛑 `V−I−B−G−` | SKIP | 827 | 19.9 | 0.828 | 1.144 | 1.38 | 0 | **−0.87** | −2.32 | 40 | −27.8 | 60 | **8** |
| 🛑 `V+I−B−G−` | SKIP | 439 | 10.5 | 0.698 | 1.090 | 1.56 | 0 | **−1.21** | −0.55 | 46 | −29.2 | 54 | 6 |
| 🛑 `V−I−B−G+` | SKIP | 222 | 5.3 | 0.648 | 0.849 | 1.31 | 0 | **−2.80** | −5.35 | 32 | −35.5 | 68 | 6 |

(5 cells with n < 60 — 154 trades — omitted from the ladder; the full 16 are in §1 of
the script output.)

⚠⚠ **`V+I+B+G−` tops the TRIMMED column but its trim lift is 2.74** — its PF nearly
triples when the worst 5% is dropped, and its RAW PF is only 1.720 against
`V+I+B+G+`'s 2.651. **The real A++ cell is `V+I+B+G+`** (n=785, 18.9% of the
population, mean +3.90%, best tail at −19.0%). Trim lift is printed precisely so a cell
that is one bad trade away from mediocrity cannot quietly rank first.

⚠⚠ **THREE CELLS HAVE trimmed PF > 1 AND NEGATIVE RAW MEAN** and are forced to size 0.
Trimming exists to COMPARE cells whose tails are uncertain, NOT to decide whether an
edge exists — dropping the worst 5% flips a losing cell above 1.0 and would otherwise
hand it real money. `V−I−B−G−` is 19.9% of the population with **8 losing years**.

⚠ Multipliers are trimmed PF − 1 scaled to the best cell, on RAW returns. The house
standard vol-normalises, but the natural normaliser here is `volat_open30` — one of the
four cell-defining features — so dividing by it would make the sizing signal partly BE
the cell definition. Left un-normalised deliberately.

## §2 Does trading the lesser cells actually pay?

| cell | book | n | mult | mean% | % of total P&L | cumulative |
|---|---|---:|---:|---:|---:|---:|
| `V+I+B+G+` | A++ | 785 | 0.931 | +3.90 | **75.9%** | 75.9% |
| `V+I+B+G−` | A++ | 307 | 1.000 | +1.43 | 11.7% | 87.6% |
| `V−I+B+G+` | B++ | 427 | 0.206 | +2.52 | 5.9% | 93.5% |
| `V−I−B+G+` | B++ | 429 | 0.144 | +1.42 | 2.3% | 95.8% |
| `V−I+B−G−` | A+ | 81 | 0.371 | +2.20 | 1.8% | 97.6% |
| `V+I−B+G+` | A+ | 68 | 0.370 | +2.17 | 1.5% | 99.1% |
| others | B++ | 425 | 0.23 | — | 0.9% | 100% |

⚠ **The honest answer is that the lesser cells add ~12%, not the ~50% the FlushFader
widening added.** The two A++ cells are 87.6% of P&L on 26% of the trades. The B++
tier is worth having (8.2% for 856 trades at ~0.2× size) but this system is far more
concentrated than FlushFader's was — because 34.7% of the population sizes to ZERO here,
which had no analogue there.

## ⭐ §3 Marginal value of each feature, holding the other three fixed

| feature | ΔPF > 0 in | median ΔPF | fails in |
|---|---|---:|---|
| **B** relative persistence | **4/4** | **+0.578** | — |
| **I** intensity | **5/5** | +0.405 | — |
| G absolute gaps | 3/4 | +0.589 | `V−I−B−` (−0.180) |
| ⚠ **V** volatility | **3/5** | +0.404 | `I+B−G−` (**−0.554**), `I−B−G−` (−0.131) |

⭐⭐ **Volatility is the LEAST consistent of the four across the lattice**, and it fails
specifically in `B−G−` contexts — i.e. when BOTH persistence measures are unfavourable.
Low volatility helps when the tape held together and hurts when it did not. That is a
genuine interaction, and it qualifies §S43cf/cg: volatility is the best TAIL lever but
it is not unconditionally additive.

⭐ **Relative persistence `B` is the most reliable feature in the system** — positive in
every context tested, largest median Δ. Further evidence against dropping it (§S43ck).

## ⭐ §4 Volatility in TERCILES × how many of the other three are favourable

⭐ USER: *"it might be worth breaking the volatility down into terciles instead of
halves."* Yes — and it is arguably the better instrument, because it prices the
volatility GRADIENT that a median split flattens:

| volat tercile | I+B+G score | n | PF raw | PF trim5 | mean% | med% | win% | worst5% | loss% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **T1** [2, 77)bp | 3 | 596 | **2.594** | 4.545 | +3.14 | +2.34 | 66 | **−17.0** | **34** |
| T1 | 2 | 328 | 1.599 | 4.316 | +1.26 | +1.18 | 66 | −26.5 | 34 |
| T1 | 1 | 294 | 1.301 | 2.336 | +0.78 | +0.61 | 55 | −23.2 | 45 |
| T1 | 0 | 170 | 0.976 | 1.544 | −0.07 | +0.19 | 51 | −20.4 | 49 |
| **T2** [77, 137)bp | 3 | 356 | 2.246 | 3.331 | **+5.65** | +1.36 | 53 | −29.7 | 47 |
| T2 | 2 | 211 | 1.560 | 2.220 | +2.37 | +0.12 | 52 | −24.9 | 48 |
| T2 | 1 | 257 | 0.603 | 0.855 | −2.08 | −1.71 | 43 | −31.2 | 57 |
| T2 | 0 | 564 | 0.674 | 0.973 | −1.48 | −1.61 | 41 | −27.6 | 59 |
| ⚠ **T3** [137, 897)bp | 3 | 260 | 1.106 | 1.426 | +0.97 | −5.34 | 40 | −40.9 | 60 |
| T3 | 2 | 385 | 1.170 | 1.525 | +1.52 | −5.26 | 37 | −40.9 | 63 |
| T3 | 1 | 211 | 0.907 | 1.176 | −0.65 | −4.66 | 37 | −31.4 | 63 |
| T3 | 0 | 532 | 0.862 | 1.201 | −0.75 | −2.51 | 39 | −30.7 | 61 |

Three clean readings the binary lattice cannot express:

1. ⭐ **T3 is untradeable at ANY score.** Its best cell is PF 1.106 with a −40.9% worst-5%
   and a NEGATIVE median. 33% of the population can be cut on volatility alone.
2. ⭐ **T1 is tradeable down to score 1** (PF 1.301) while T2 collapses below score 2
   (0.603, 0.674). The score threshold for entry DEPENDS on the volatility tercile —
   a genuine 2-D structure.
3. ⚠ **T2 score 3 has the HIGHEST MEAN of the whole table (+5.65%)** but a −29.7%
   worst-5% and a +1.36% median against T1's +2.34%. Restates §S43ch: low volatility
   means smaller moves in BOTH directions, so T1 wins on PF and tail while T2 wins on
   mean. Which you prefer is a sizing question, not a selection one.

⚠ No inverted-U is visible at tercile resolution — T1 > T2 > T3 monotonically at every
score. That does NOT contradict §S43ch: T1 spans 2–77bp and so CONTAINS both the weak
bottom decile (3–34bp) and the strong D2–D3, averaging them. The inverted U is a
decile-scale feature; do not conclude from this table that the lower bound is unneeded.

Script: `snoozer_complements.py`.

---

# ⭐⭐ S43cm (2026-08-16) — IS THE UPPER HALF OF T2 TRADEABLE? Yes, but only its bottom third

⭐ USER: *"Is the upper half of T2 specifically tradable? How does it compare to the
bottom half of T2? Have we done well by putting the cutoff at the median or could we
loosen it to T1 + T2?"*

Volat_open30 landmarks: **T1/T2 edge 76.6bp · MEDIAN 104.2bp · T2/T3 edge 136.9bp.**
So the median sits almost exactly in the middle of T2, and the question is whether the
half of T2 above it earns its place.

## §1 T2 split at the median, inside the `I+B+G+` book (n=1,212)

| band | n | PF raw | PF trim5 | mean% | med% | win% | worst5% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| T1 all [0, 77)bp | 596 | 2.594 | 4.545 | +3.14 | +2.34 | **66** | **−17.0** | **34** | 1 |
| ⭐ **T2 LOWER** [77, 104)bp | 189 | **2.749** | 4.261 | **+6.28** | **+3.71** | 57 | −25.2 | 43 | **0** |
| ⚠ T2 UPPER [104, 137)bp | 167 | 1.881 | 2.717 | +4.94 | **−0.71** | 49 | −33.1 | 51 | 1 |
| 🛑 T3 all [137, 900)bp | 260 | 1.106 | 1.426 | +0.97 | −5.34 | 40 | −40.9 | 60 | 3 |

⭐⭐ **T2's LOWER half is the best band in the system on raw PF (2.749) and mean
(+6.28%)** — better than T1 on both. T1 wins on tail, win rate and loss rate. The
median cutoff is therefore slicing T2 at a real seam, not arbitrarily.

## ⭐ §2 But T2's upper half is NOT homogeneous — it splits again at ~120bp

Fine fixed-bp bands, same book:

| band bp | n | PF | mean% | med% | win% | worst5% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ⚠ [0, 30) | 141 | 1.891 | +1.66 | +2.30 | 68 | −15.0 | 32 | **3** |
| ⭐ **[30, 45)** | 159 | **3.389** | +3.01 | +2.36 | 69 | **−11.5** | **31** | **0** |
| ⭐ **[45, 60)** | 145 | **3.230** | +4.31 | +2.56 | 66 | −16.3 | 34 | **0** |
| [60, 77) | 155 | 2.265 | +3.58 | +2.36 | 60 | −24.4 | 40 | 1 |
| [77, 90) | 110 | 2.645 | +5.41 | +4.73 | 56 | −24.7 | 44 | **0** |
| [90, 104) | 74 | 2.804 | **+7.57** | +2.99 | 55 | −26.5 | 45 | **0** |
| ⭐ **[104, 120)** | 92 | **2.420** | +6.19 | +1.96 | 57 | −27.5 | 43 | **0** |
| 🛑 **[120, 137)** | 76 | **1.500** | +3.52 | **−3.05** | 41 | −37.6 | 59 | **3** |
| 🛑 [137, 170) | 100 | 0.689 | −2.93 | −8.03 | 32 | −38.4 | 68 | 4 |

**Answer: the upper half of T2 is half tradeable.** `[104, 120)` is a perfectly good
cell — PF 2.420, mean +6.19%, **zero losing years**. `[120, 137)` is not: PF 1.500,
**negative median**, 3 losing years. Treating "T2 upper" as one block averages a good
cell with a bad one.

⚠⚠ **And `[0, 30)` is the WORST band below 120bp** — PF 1.891 with **3 losing years**,
against 3.389 for `[30, 45)`. The §S43ch inverted U reproduces on `volat_open30`, at the
same place: the very quietest tape is not the best tape.

## §3 So where should the cutoff go?

Cumulative keep-below-T on the `I+B+G+` book:

| keep < T | n | PF | mean% | med% | win% | worst5% | yrs<1 | P&L proxy (n × mean) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 60bp | 445 | 2.799 | +3.00 | +2.31 | 68 | −14.2 | **0** | 1,337 |
| 77bp (T1) | 600 | 2.601 | +3.15 | +2.34 | 66 | −16.8 | 1 | 1,891 |
| 104bp (**current**) | 784 | 2.644 | +3.88 | +2.44 | 63 | −19.0 | 1 | 3,046 |
| **120bp** | 876 | 2.604 | +4.13 | +2.42 | 63 | −20.5 | 1 | **3,616** |
| 137bp (T1+T2) | 952 | 2.392 | +4.08 | +2.24 | 61 | −23.1 | **0** | 3,883 |
| 200bp | 1,105 | 1.847 | +3.25 | +1.50 | 57 | −27.3 | 2 | 3,586 |

⭐ **PF is FLAT — 2.60 to 2.80 — anywhere from 60bp to 120bp.** The median cutoff is not
special; it is simply inside a wide plateau. **So the answer to "have we done well by
putting the cutoff at the median" is: we have done neither well nor badly — the choice
barely matters between 60 and 120.** What DOES matter is stopping before 137.

⚠ Loosening all the way to T1+T2 (137bp) is where PF finally breaks (2.392) because it
swallows the bad `[120,137)` slice — but note it also reaches 0 losing years and the
highest P&L proxy. That apparent contradiction is the [120,137) band being bad on
MEDIAN and consistency while still carrying enough winners to lift the aggregate mean.
Trading it at a reduced size, not excluding it, is the resolution consistent with §S43cl.

## ⭐⭐ §4 The refined band — and the lower bound matters far MORE on `G−`

| book | rule | n | PF | mean% | med% | win% | worst5% | loss% | yrs<1 | P&L proxy |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **G+** | current `< 104bp` | 785 | 2.651 | +3.90 | +2.45 | 63 | **−19.0** | 37 | 1 | 3,059 |
| **G+** | ⭐ `[30, 120)bp` | 735 | **2.698** | **+4.60** | +2.62 | 62 | −21.5 | 38 | 1 | **3,381** |
| **G+** | `[30, 137)bp` | 811 | 2.445 | +4.50 | +2.22 | 60 | −24.4 | 40 | **0** | 3,649 |
| **G−** | current `< 104bp` | 307 | 1.720 | +1.43 | +1.25 | 67 | −25.2 | 33 | 1 | 440 |
| **G−** | ⭐⭐ `[30, 120)bp` | 256 | **2.776** | **+2.65** | +1.42 | **68** | **−16.5** | **32** | **0** | **677** |

⭐⭐ **On the `G−` book the refined band is transformative: PF 1.720 → 2.776, worst-5%
−25.2% → −16.5%, one losing year → zero, and a HIGHER P&L proxy (677 vs 440) on FEWER
trades (256 vs 307).** That also explains the §S43cl anomaly where `V+I+B+G−` had a
trim lift of 2.74 — its losers were concentrated in the volatility tails the median
split left in.

On `G+` the same band is a modest gain (+11% P&L proxy, PF 2.651 → 2.698) bought with a
slightly worse tail. Per-year it trades 2026 (4.64 → 2.67) for 2017/2023/2024
(4.58→5.59, 3.12→3.53, 5.11→5.62).

⚠ Neither variant repairs 2016 (0.29 → 0.63, n≈5) or 2019 (0.80 → 0.62, n≈20). Those
two years have now survived every feature in this study and are a separate problem.

**RECOMMENDED:** `volat_open30 ∈ [30, 120)bp` on both books, with `[120, 137)` retained
as a reduced-size cell rather than excluded.

Script: the sweeps are one-off; `snoozer_complements.py` carries the lattice.

---

# ⭐⭐⭐ S43cn (2026-08-16) — THE REBUILT BOOK: drop G, split volatility, split B (but only where it helps)

⭐ USER: *"I'll take your recommendation. I guess with this we should consider dropping
the G as a sizeup cell. But then we should consider the [30,60) and [60,120) as two
separate volatility cells and size-up on the former. We're measuring volatility based on
the first 30m off the open, right? ... The only question that will remain is whether
splitting the I+ and B+ features into the bottom and top halves would give a benefit."*

✅ **Confirmed: `volat_open30` is 09:30–10:00**, the first 30 minutes off the open.

## ⭐ §1 Dropping `G` is right — it INVERTS between the two volatility cells

| base `I+B+` | n | PF | mean% | win% | worst5% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|
| volat [30,60), **G merged** | 476 | **3.452** | +3.31 | 68 | **−12.1** | **0** |
|   + G+ | 304 | 3.297 | +3.63 | 67 | −13.6 | 0 |
|   + G− | 172 | **3.909** | +2.76 | 69 | **−7.9** | 0 |
| volat [60,120), **G merged** | 515 | **2.435** | +4.82 | 59 | −25.9 | 1 |
|   + G+ | 431 | **2.508** | +5.29 | 58 | −25.3 | 1 |
|   + G− | 84 | 1.927 | +2.41 | 64 | −27.7 | 3 |

⭐ **`G−` BEATS `G+` in the quiet cell (3.909 vs 3.297) and loses in the loud one
(1.927 vs 2.508).** Once volatility is banded properly, absolute gaps has no consistent
sign — exactly the §S43cl finding (`G` positive in only 3/4 lattice contexts) sharpened.
Merging is not a concession; it is the correct read. **Dropping `G` is free.**

## ⭐⭐ §2 The two volatility cells are genuinely different animals

| cell | n | PF | PFtrim | mean% | med% | win% | p5% | worst5% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **volat [30,60)** `I+B+` | 476 | 3.452 | 6.226 | +3.31 | +1.87 | **68** | **−7.1** | **−12.1** | **32** | **0** |
| **volat [60,120)** `I+B+` | 515 | 2.435 | 3.952 | **+4.82** | **+2.62** | 59 | −15.9 | −25.9 | 41 | 1 |
| 🛑 dropped [120,137) | 78 | 1.494 | 2.024 | +3.41 | −3.05 | 41 | −28.2 | −37.6 | 59 | 3 |

**−12.1% worst-5% is the best tail anywhere in this study** (prior best −17.0%).

⚠ **One amendment to the plan.** The proposal was to size up on `[30,60)`. That is right
on TAIL but not on EDGE: `[60,120)` carries a 46% higher mean (+4.82% vs +3.31%) and a
higher median. They deserve comparable size for OPPOSITE reasons — `[30,60)` is the
survival cell, `[60,120)` the expectancy cell. §3 makes `[60,120)` the stronger of the
two once `B` is split.

## ⭐⭐⭐ §3 Splitting `I+`/`B+` — the answer DIFFERS between the two cells

Quartile thresholds: `inten` q50 0.50 / q75 1.18 · `pers` q50 0.65 / q75 0.93.
Percentiles are against 1,500 random same-n subsets OF THAT CELL.

**volat [30,60) — base PF 3.452:**

| sub-cell | n | PF | mean% | worst5% | pctile |
|---|---:|---:|---:|---:|---:|
| `I++` (any B+) | 368 | 3.822 | +3.68 | −12.2 | 88.1 |
| `I++ × B++` | 241 | 3.958 | +3.72 | −12.0 | 80.8 |
| `B++` (any I+) | 273 | 3.222 | +3.26 | −13.1 | 30.3 |
| ⚠ `I+lo × B++` | 32 | 0.924 | −0.23 | −21.4 | **0.5** |

🛑 **NOTHING clears 95.** In the quiet cell the split is NOT worth the complication —
the cell is already homogeneous. (The one real pocket is `I+lo × B++` at the 0.5
percentile, but it is 32 trades.)

**volat [60,120) — base PF 2.435:**

| sub-cell | n | PF | mean% | med% | win% | worst5% | yrs<1 | pctile |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ⭐⭐ **`B++` (any I+)** | 211 | **4.152** | **+8.52** | +3.37 | 62 | **−19.1** | **0** | **99.9** |
| `I++ × B++` | 158 | 3.966 | +8.30 | +3.50 | 59 | −19.7 | 0 | 99.0 |
| `I+lo × B++` | 53 | 4.793 | +9.17 | +3.37 | 70 | −20.0 | 0 | 95.0 |
| 🛑 `I++ × B+lo` | 118 | 1.749 | +2.56 | +0.78 | 55 | −23.3 | **3** | 8.7 |
| 🛑 `I+lo × B+lo` | 186 | 1.505 | +2.05 | +2.07 | 57 | −33.2 | 1 | **0.3** |

⭐⭐ **Split `B`, not `I`.** `B++` alone reaches the 99.9 percentile and works
IRRESPECTIVE of `I` (both `I++×B++` and `I+lo×B++` clear 95; the two `B+lo` cells sit at
8.7 and 0.3). `I++` adds nothing once `B++` is imposed — `B++`(any I) 4.152 vs
`I++×B++` 3.966. Third confirmation that **relative persistence is the strongest feature
in this system** (§S43ck, §S43cl §3).

## ⭐⭐⭐ §4 THE REBUILT BOOK

| cell | rule | n | PF | PFtrim | mean% | med% | win% | worst5% | loss% | yrs<1 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **A++** | `volat[30,60) ∧ I+ ∧ B+` | 476 | 3.452 | 6.226 | +3.31 | +1.87 | **68** | **−12.1** | **32** | **0** |
| **A+** | `volat[60,120) ∧ I+ ∧ B++` | 211 | **4.152** | 6.466 | **+8.52** | **+3.37** | 62 | −19.1 | 38 | **0** |
| **B++** | `volat[60,120) ∧ I+ ∧ B+lo` | 304 | 1.590 | 2.622 | +2.25 | +1.40 | 56 | −29.1 | 44 | 1 |
| ⭐ **A++ + A+** | | **687** | **3.781** | **6.558** | **+4.91** | +2.29 | **66** | **−14.8** | 34 | **0** |

Per year, `A++ + A+`: 0.55(6) · 2.96(14) · 6.43(17) · **1.13(9)** · 4.74(169) ·
2.22(160) · 4.15(65) · 4.35(50) · 7.76(55) · 3.07(74) · 6.06(68).
⭐ **2019 finally goes positive (1.13)** — the first structure in this study to fix it.

At A++ 1.0× / A+ 1.0× / B++ 0.35×, the P&L split is 43.6% / 49.7% / 6.6%. The two
full-size cells are ~93% of P&L on 687 trades (≈62/yr).

## ⚠⚠ Two caveats that must travel with this

1. ⚠⚠ **`A+` is essentially UNTESTED before 2020** — fewer than 5 trades in each of
   2016–2019, so its entire record is 6 years, and it is 49.7% of projected P&L. The
   early years are thin in the population generally (54–192 trades/yr vs 400–640 later),
   but `A+`'s hit rate there is lower still. **Do not treat `A+`'s 4.152 as an 11-year
   number.** `A++` spans all years.
2. ⚠ `A++`'s 2026 cell reads 29.78 on n=46 — a near-zero-loss year doing heavy lifting
   in the headline PF. The tail and win-rate claims are stable across years; the PF
   magnitude is not.

⚠ Thresholds are absolute (`volat_open30` in bp; `inten`/`pers` at population quantiles
0.50/0.75 = 0.50/1.18 and 0.65/0.93). Nothing is a per-day rank.

Scripts: `snoozer_complements.py` for the lattice; these sweeps are one-off.

---

# ⏭ NEXT SESSION (queued 2026-08-14 ~19:50)

⚠ **Item 1 below is DONE — see §S43cf above.** Items 2+ still open.

## 1. ✅ VOLATILITY FEATURES for the Snoozer family (user request) — DONE 2026-08-16

**The ask:** compute volatility over the **first 30m**, the **first 60m**, and the
**whole day**, and do a breakdown — the same treatment `dv_over_*` and `bar_over_*`
already got.

### Use the LOCKED measure, not a fresh one

`project_surgerider_vol_bakeoff_2026-07-21` settled this: **slot-EmaMa of |r| on 30s
slot vwaps** beat r², path-RV and every kernel tried, 43/43 days. The fixed-window
analogue is:

    slot vwaps on 30s buckets  ->  r_i = ln(vwap_i / vwap_{i-1})  ->  mean |r_i|

⚠ Do NOT reach for stdev or close-to-close — that is the comparison the bake-off
already lost. ⚠ And `volat_*` = VOLATILITY while `vol_*` = VOLUME in this codebase.

### The windows, and the feature family that follows

| window | buckets | note |
|---|---|---|
| open 30m | 34200–35999 | |
| open 60m | 34200–37799 | |
| day (to 15:00) | 34200–53999 | the denominator for a "rest of day" twin |
| last hour | 54001–57600 | measure to **15:59** for the k59 entry |

Then mirror the two existing families — today's lesson is that the **RELATIVE** framing
is the productive one:

    volat_over_open30 = volat(last hour) / volat(first 30m)
    volat_over_open60 = volat(last hour) / volat(first 60m)
    volat_over_rest   = volat(last hour) / volat(09:30-15:00)

⭐ These are ALREADY RATE-FREE (a mean per-slot |r| does not scale with window length),
so unlike `bar_over_*` they need **no rate normalisation** and unlike `dv_over_*` their
1.0 point is meaningful as-is.

### The hypothesis worth pre-registering

Both Snoozer sides key on **participation** (§S43cb: the overnight move continues in
the direction the closing hour's participation points). Volatility is a different
axis — *how far price moves per unit of activity* rather than how much activity there
is. So it may be genuinely orthogonal to both the dollar and bar families, or it may
just be a noisier proxy for them.

⚠ **Decide it with the substitution test + a RANDOM same-n control + the overlap
matrix**, never ρ. Today produced two cautionary cases: ρ 0.008 measures reading
PF 3.41 vs 1.28, and three "thin tape" measures ranking 3.41 / 2.67 / 1.28 with one
actively WORSE than baseline.

### Build note — do NOT extend `snoozer_build_shape.py`

That scan **OOM-killed twice** (13.4GB RSS on a 15GB box, at both 12GB and 6GB DuckDB
limits) when six aggregates were added. Follow `snoozer_build_barcounts.py`: a separate
light scan reading only the columns it needs, then a join. The 30s-slot reduction makes
this heavier than the bar-count scan, so consider aggregating to slots first and
computing returns in a second pass.

## 2. ⏭ `pers` / `inten` on the B++ and B+ segments (deferred from today)

§S43cd found relative shape works on the WIDE book but is 0-for-3 inside A++, and
explained it: the `gap_60 < 4` door plus the eight-voice roster have already extracted
what it measures. **B++ and B+ have never been touched by the roster**, so they are
where the feature should have the most room. Test `inten_60 >= q50` and `pers_1200`
there as segment-level filters, with the random control.
