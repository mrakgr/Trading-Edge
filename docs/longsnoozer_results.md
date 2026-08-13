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

🛑 **EVERY NUMBER IN THIS DOCUMENT IS MEASURED ON THE OLD, TRUNCATED CORPUS AND
MUST BE RE-DERIVED.** When these studies ran, `data/intraday_1s_slim` ended at
**15:58:59** (bucket 57539) — no 16:00 bar, no post-market, and the last RTH
minute (typically the day's heaviest) missing entirely.

**As of 2026-08-13 that path holds the FULL-DAY corpus: 04:00 → 20:00+ ET**
(rebuilt 2026-08-12/13, +3.17% rows). The path did not change, so nothing below
fails loudly — it is simply measured against a different tape than the one the
code now reads. In particular **every gap count and `nbars` figure changes
meaning**: the signal window that was "the last hour, 3,600 seconds" is no longer
the end of the session, and `mr_candidate_1s_v2`'s own `n_bars_1s >= 200` gate is
folded over a 16-hour day. Re-run before trusting any threshold here.

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

