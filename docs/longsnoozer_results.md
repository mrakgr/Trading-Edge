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
| HIGH `lh_over_rest` | 1,042 | 2.174 | +2.70% | +1.58% | −90 |
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
