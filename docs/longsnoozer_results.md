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

Windows (`snoozer_build_volat.py`, 1,006s over 2,514 days → 24.8M ticker-days):
`volat_open15` / `open30` / `open60` / `day` (09:30–15:00, deliberately DISJOINT from
the last hour) / `dayfull` (09:30–15:59) / `lh` (15:00–15:59). Every one ends at or
before **15:59**, so none is a lookahead against the k59 limit entry. Medians:
20.4 / 17.9 / 15.4 / 10.8 / 6.5 bp — volatility decays monotonically through the day.

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
**tail cut**, which is visible in every year, not the PF magnitude.

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

Scripts: `snoozer_build_volat.py` (cache) · `snoozer_volat_test.py` (deciles,
substitution, stacking, overlap) · `snoozer_volat_robust.py` (bootstrap + sweep).

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
