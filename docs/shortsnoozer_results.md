# ShortSnoozer — short the last-hour rally, hold overnight

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
document).** The tables below were measured against the old corpus, which ended at
**15:58:59**, AND at the official close — an entry no order placed before 16:00 can
achieve. They are kept for provenance. **§S43by supersedes them.**

⭐⭐ **2026-08-14, S43cb: THE TAIL IS FINALLY MOVING.** A second lever (last-hour
dollar SHAPE) stacked with tape persistence takes the best cell to **PF 5.198 with a
worst trade of −64%** — the first time this system's worst loss has come in under
100% of notional. Read §S43cb at the end before anything below. It is still not
adopted, but the disqualifier is no longer untouchable.

✅ **THE EDGE SURVIVES A TRADEABLE ENTRY.** `>+6%` reproduces at **PF 1.706**
(close entry, new corpus) vs the old 1.653, and a real 15:59 limit entry costs only
**1.706 → 1.643**. The corpus rebuild and the knowability constraint both leave it
essentially intact.

🛑 **STILL NOT ADOPTED — and the reason is the TAIL, which nothing has touched.**
worst trade **−245%**, 0.4% of trades lose more than 100% of notional, and there is
no stopping out overnight.

⚠⚠ **DO NOT APPLY THE LONGSNOOZER DENSITY FILTER TO THIS SYSTEM.** It is
*monotonically destructive* here — see §2. An earlier draft of §S43by gated on
`gaps ≤ 760` and reported PF 1.137 as "the tradeable number", blaming the entry.
That was wrong on both counts: the gate cost 0.51 PF, the entry cost 0.06, and
`gaps ≤ 760` is among the WORST cells in the grid, not the spec.

---

## ⭐ THE SIGNAL

**Last hour > +6% → median −3.31% overnight, 30.7% win** (n 4,051), i.e. the short
makes money ~69% of nights. Negative in no year 2018–2026. Density strengthens it:
`>+6%` reads −4.4% to −4.7% median in the top 3–10% most continuously traded, and
the effect is monotone across every decile.

| last hour | n | median overnight | win% |
|---|---:|---:|---:|
| +2..+4% | 27,223 | −0.213 | 43.9 |
| +4..+6% | 4,813 | −0.921 | 38.6 |
| **>+6%** | **4,051** | **−3.308** | **30.7** |

🛑 **NOT ADOPTED.** See the loss distribution below before doing anything with it.

---

## ⭐⭐ THE LOSS DISTRIBUTION of the >+6% short — positive EV, ruinous tail

The median said short. The tail says size it like an option you are writing.

| | ALL >+6% (n 4,051) | top 25% density (n 1,776) |
|---|---|---|
| p01 / p10 / p25 | −39.9 / −17.8 / −10.4 | −50.0 / −21.8 / −12.8 |
| **median** | **−3.31** | **−3.51** |
| p75 / p90 / p95 | +1.4 / +11.2 / +22.0 | +3.6 / +18.1 / +31.0 |
| **p99 / p99.9 / max** | **+64.0 / +150.2 / +245.0** | **+86.3 / +171.7 / +245.0** |
| mean | −2.65 | −1.87 |

(overnight move; a short's P&L is the negative. **Short: mean +2.65%, median
+3.31%, PF 1.653.**)

**⚠ DENSITY HELPS THE MEDIAN AND HURTS THE TAIL.** The top-25% filter that roughly
triples the LONG edge moves the short's p99 loss from −64% to −86% and its mean
from +2.65% to +1.87%. The continuously-traded names *are* the squeeze
candidates — GME, OCGN, HOLO, GNS all sit in the top decile of tape density. The
long and short sides want opposite filters.

**How often it goes wrong:** >5% loss **16.7%** of the time, >10% **10.8%**,
>20% **5.6%**, >50% **1.6%**, **>100% 0.4% (16 trades)**.

**Concentration — the inverse of what you want.** The 50 worst trades (1.2% of
the sample) cost **45% of total P&L**; the 20 worst cost 24.7%. PF 1.653 raw →
2.506 excluding the 64 losses over 50%. Worst singles: TOP 2023-04-27 **+245%**,
BNAI 2026-01-23 +235%, AHPI +167%, OCGN +153%, **GME 2021-01-26 +140%**.

**Per year:** negative in 2016 (−6.3%, n 30) and 2017 (−2.8%, n 104), positive
every year 2018–2026 (+1.0% to +5.8%, win 62–78%).

**Filters do NOT rescue it.** An upper bound on the last-hour move does nothing
(+6..+10 PF 1.76, +10..+20 1.57, +20..+40 1.85, >+40 1.02) — the −235% and −245%
losses are in the *bulk* buckets, not the extreme. A price floor runs backwards:
`<$1` is the BEST slice (PF 2.10) and `>$20` the worst (1.46), and sub-$1 is
fee-dead and unborrowable anyway.

**Clustering is the one piece of good news.** 227 losses >20% fall across 205
distinct sessions — max 5 on any day (2020-02-27). They are idiosyncratic, not
one correlated squeeze. But median positions per night is **2**, so there is
almost no diversification to lean on either.

**Sizing.** Equal-weight within a night, fixed fraction of account deployed:

| deployed | terminal | CAGR | maxDD | worst night |
|---|---|---|---|---|
| 100% | **RUINED 2021-08-24** | — | −100% | −119.7% |
| 50% | 6,974,369× | 385% | **−81.1%** | −59.9% |
| 25% | 10,004× | 152% | −48.2% | −29.9% |
| 10% | 52.3× | 48.7% | −20.9% | −12.0% |
| **5%** | **7.6×** | **22.5%** | **−10.7%** | −6.0% |
| 2% | 2.3× | 8.6% | −4.3% | −2.4% |

⚠ The 50% row survives this particular path and is not a strategy — one worse
night wipes it. **A short's loss is unbounded and there is no stopping out
overnight**, so the sizing must assume a >100% single-name loss is reachable:
0.4% of trades already did it.

⚠ NOT MODELLED: borrow cost and availability (these are precisely the
hard-to-borrow names), fees, and whether the MOC/MOO auction prints are
attainable at size. All three cut the same way.

---

## ⭐⭐ LONG AND SHORT WANT OPPOSITE FILTERS

The tape-density cut that roughly TRIPLES the LongSnoozer edge moves this short's
p99 loss from **−64% to −86%** and its mean from +2.65% to +1.87%. The
continuously-traded names ARE the squeeze candidates — GME, OCGN, HOLO and GNS all
sit in the top density decile. Do not let ShortSnoozer (or SpikeFader) inherit a
LongSnoozer/FlushFader filter on faith.

## ⏭ Open questions

1. **Borrow.** These are precisely the hard-to-borrow names, and the study models
   no borrow cost, no locate, and no availability constraint. It may be
   unshortable exactly when it pays best. This is the first thing to settle.
2. **Is the median or the tail the real signal?** PF 1.653 raw becomes 2.506 with
   the 64 losses over 50% excluded. If a filter existed that removed squeeze-prone
   names ex ante, this changes character entirely — none of price, flush depth or
   density does it.
3. **Does the limit-entry finding transfer?** LongSnoozer gains from resting a bid
   into sellers. The mirror — resting an offer into buyers on a +6% rally — has not
   been measured, and an uptick rule may bind.

---

# 🛑 S43by (2026-08-14) — RE-DERIVED: a tradeable entry roughly halves the edge

New cache `snoozer_cache.parquet` (1,429,281 ticker-days). Tools:
`scripts/equity/snoozer_{build_cache,windows,grid}.py`. Full method — the knowability
ladder, the matched-selectivity argument, the fill study — is written up once in
`docs/longsnoozer_results.md` §S43by and not repeated here.

## §1 DOES A SHORTER WINDOW HELP? The raw tables say yes. They are wrong.

By fixed threshold, shorter windows look dramatically better:

| `>+6%` band | n | median overnight |
|---|---:|---:|
| 60m | 4,443 | −3.80% |
| 30m | 2,722 | −4.91% |
| 15m | 1,589 | −5.89% |
| 5m | **525** | **−7.66%** |

⚠ **This is a threshold artefact.** "+6% in 5 minutes" is a far rarer, far more extreme
event than "+6% in an hour" — the 5m row is simply a smaller, deeper tail. At MATCHED
selectivity the ordering **inverts completely**:

| n most positive | 60m | 30m | 15m | 5m |
|---:|---:|---:|---:|---:|
| 500 | **−8.48** | −7.96 | −7.66 | −7.44 |
| 1,000 | **−7.51** | −6.96 | −6.60 | −6.89 |
| 2,500 | **−5.36** | −5.12 | −5.14 | −4.83 |
| 10,000 | **−1.91** | −1.95 | −1.59 | −1.39 |

**60m is the best window at every selectivity, on this side too.** The shorter windows
are not a better signal; they are a narrower one.

## ⭐⭐ §2 THE DENSITY x SPIKE GRID — and the mis-attribution it caught

**User question (2026-08-14): "How could the PF for the ShortSnoozer collapse like
that?"** It could not, and it did not. Decomposing the reported 1.653 → 1.137:

| arm | n | PF | mean% | med% | win% | worst% |
|---|---:|---:|---:|---:|---:|---:|
| A. close entry, **no** density gate *(the old headline)* | 4,443 | **1.706** | +2.86 | +3.80 | 69 | −310.1 |
| B. **k59 limit** entry, no density gate | 4,091 | **1.643** | +2.60 | +3.33 | 69 | −245.0 |
| C. k59 entry **+ `gaps ≤ 760`** | 838 | **1.137** | +1.06 | +4.44 | 64 | −234.6 |

**A → B, the entry change: −0.06 PF.** Exactly what 3.1bp of give-up should cost
(§3). **B → C, the density gate: −0.51 PF.** The gate was the whole effect.
A also reproduces the old 1.653 at 1.706, so the corpus rebuild broke nothing either.

### 🛑 DENSITY IS PERFECTLY INVERTED BETWEEN THE TWO SYSTEMS

RAW PF (n), entry `k59` — read DOWN each column:

| gaps≤ | >2% | >3% | >4% | >6% | >8% | >10% |
|---|---|---|---|---|---|---|
| 200 | 0.835 (1,203) | 0.873 (738) | 0.923 (519) | 1.062 (310) | 1.040 (237) | 1.056 (176) |
| 400 | 0.917 (2,099) | 0.931 (1,240) | 0.923 (841) | 1.033 (513) | 1.020 (366) | 1.075 (274) |
| 600 | 0.958 (3,074) | 0.976 (1,738) | 0.970 (1,167) | 1.074 (697) | 1.062 (486) | 1.153 (357) |
| 760 | 1.001 (3,925) | 1.019 (2,179) | 1.026 (1,438) | 1.137 (838) | 1.164 (586) | 1.241 (422) |
| 900 | 1.046 (4,766) | 1.060 (2,604) | 1.060 (1,692) | 1.151 (972) | 1.168 (671) | 1.203 (485) |
| 1200 | 1.120 (6,974) | 1.152 (3,631) | 1.157 (2,292) | 1.221 (1,274) | 1.215 (859) | 1.198 (624) |
| 1800 | 1.244 (13,600) | 1.266 (6,507) | 1.263 (3,877) | 1.298 (2,006) | 1.257 (1,255) | 1.236 (892) |
| **all** | **1.422 (36,194)** | **1.541 (16,156)** | **1.582 (8,917)** | **1.643 (4,087)** | **1.603 (2,307)** | 1.557 (1,490) |

The LongSnoozer grid rises as the gap cut TIGHTENS (`gaps≤760 × <−6%` = 1.848, all =
1.200). This one **falls monotonically in every column**, and at `gaps ≤ 200` the short
side is outright unprofitable (0.835–1.06). The continuously-traded names ARE the
squeeze candidates: filtering FOR them concentrates exactly the tail that ruins this
book, while removing the illiquid names that drift down quietly.

⚠ **This was already on record** (S43bs: *"density makes the tail WORSE"*) and an
earlier draft of this section imported the LongSnoozer filter anyway and then blamed
the entry for the result. The lesson in §3 is not rhetorical — it was violated in the
act of writing it up.

### Per-year, the ungated `>+6%` cell (entry k59, n 4,091, PF 1.643)

Depth helps a little and then flattens: `>+3%` 1.541 · `>+4%` 1.582 · `>+6%` 1.643 ·
`>+8%` 1.603 · `>+10%` 1.557. There is no cell in this grid that both survives the
tail and beats the ungated one — the best available short spec is simply
**`chg60k59 > +6%`, no density filter**.

## ⭐⭐ §3 THE ENTRY: adverse here, favourable for the long — but SMALL either way

The same measurement, opposite consequence. On the gated population (`gaps ≤ 760`),
where the 15:59–16:00 limit fills relative to the official close:

| regime | n | fill below close | median fill vs close |
|---|---:|---:|---:|
| flush `< −4%` | 1,397 | 64.9% | −0.065% |
| quiet | 72,238 | 51.7% | −0.000% |
| **rally `> +4%`** | 1,438 | **58.1%** | **−0.031%** |

**The last minute keeps drifting in the direction of the move.** A long buying a flush
therefore fills 6.5bp BELOW the close — paid to enter. A short selling a rally also
fills below the close — **3.1bp of pure give-up**, every trade, because a short wants
to sell HIGH.

⚠ **But keep the magnitude straight: 3.1bp is worth 0.06 PF (1.706 → 1.643), not the
0.51 the density gate cost.** The entry is a real, directional, permanent cost and it
is on the wrong side here — it is simply not what decides this system.

This is the LONG/SHORT ASYMMETRY again, in a third place: the two sides wanted opposite
density filters (S43bs), opposite tail treatment, and now opposite entry mechanics.
**Do not port a LongSnoozer component to this system on faith.**

## 🛑 Verdict — unchanged, now on better evidence

**NOT ADOPTED.** The best tradeable spec is `chg60k59 > +6%` with **no** density
filter: **PF 1.643, mean +2.60%, median +3.33%, win 69%, n 4,091** — pre-cost,
pre-borrow, unlevered, one trade per ticker-day with no concurrency cap.

What disqualifies it is unchanged and untouched by any of this work: **worst trade
−245%**, 0.4% of trades losing more than 100% of notional, and no way to stop out
overnight. Borrow costs and hard-to-borrow availability on exactly the names that
rally 6% into a close are not modelled, and would land on the 64 points of edge that
separate 1.643 from break-even.

⏭ If this is revisited, three things are now settled and should not be re-litigated:
the window (**60m**, at matched selectivity), the entry (**15:59 limit**, costing
0.06 PF), and the density filter (**do not use one** — it is inverted here). The open
question is the only one that ever mattered: whether the tail can be **capped
structurally** — a married put, a defined-risk spread, or a hard notional cap per
name — because no filter tried so far touches it, and density makes it worse.

---

# ⭐⭐ S43cb (2026-08-14) — the SECOND lever, and the first real dent in the tail

## The feature

**`shape` = last-hour dollars ÷ first-15-minutes dollars.** Found by accident while
decomposing the S43ca mis-attribution — the 15-minute denominator was in the cache
only because `dv_0945_tape` is the universe gate.

⚠ **It was nearly discarded as an artefact, and the sweep is what saved it.**
`snoozer_openref_sweep.py` walks the reference window 5m → 15m → 30m → 60m → 90m →
rest-of-day at matched selectivity:

| reference | PF | mean | median | losing yrs |
|---|---:|---:|---:|---:|
| baseline | 1.643 | +2.60% | +3.33% | |
| ⭐ RANDOM same-n | 1.611 | +2.62% | +3.57% | |
| first 5m | 3.430 | +5.61% | +5.95% | 0 |
| **first 15m** | **3.476** | +5.63% | +6.00% | 0 |
| first 30m | 3.174 | +5.37% | +5.98% | 0 |
| first 60m | 2.967 | +5.27% | +6.06% | 0 |
| first 90m | 2.983 | +5.33% | +6.01% | 0 |
| rest of day (~5.5h) | 2.580 | +5.12% | +6.12% | 0 |

**Monotone decay in reference length.** Comparing the closing hour to the day's
OPENING BURST carries information that comparing it to the whole day does not — the
midday lull dilutes the signal. Every rung beats the random floor of 1.611, so the
whole family is real; the ordering is the finding.

⚠ **Shallow optimum.** 15m and 30m share 92% of their picks, and by selectivity cut
30m wins at q10% (3.95) while 5m wins at q50% (2.38). **"Short reference beats long"
is robust — rest-of-day is last at every cut. "15m beats 30m" is not.** Do not tune
inside the 5m–30m plateau.

⚠ Three things called "thin tape" are NOT interchangeable, and one is harmful:

| measure | PF | vs baseline 1.666 |
|---|---:|---|
| `shape` (dollars vs open), lowest 25% | 3.414 | ✅ |
| gap count, thinnest 25% | 2.670 | ✅ |
| `vol_lh / avgvol20_prior`, lowest 25% | **1.275** | ❌ worse than doing nothing |

ρ between the first and third is **0.008**. "Shorts want thin tape" is right and
underdetermined — the operationalisation decides the result.

## ⭐ THE TWO-LEVER GRID (`snoozer_grid2.py --side short --ref 15`)

RAW PF (n), `chg60k59 > +6%`. Rows loosen PERSISTENCE toward thinner tape, columns
tighten SHAPE toward a quieter close:

| gaps≥ | shape ≤ q10% | shape ≤ q25% | shape ≤ q50% | shape: all |
|---|---|---|---|---|
| 3000 | 2.966 (36) | 3.645 (88) | 3.761 (176) | 2.632 (352) |
| **2500** | 3.218 (109) | **5.198 (273)** | 3.996 (545) | 2.702 (1,090) |
| 2000 | 3.353 (183) | **4.028 (456)** | 3.665 (911) | 2.741 (1,821) |
| 1500 | 3.374 (247) | 3.369 (617) | 3.031 (1,234) | 2.242 (2,468) |
| 1000 | 3.847 (302) | 3.448 (754) | 2.816 (1,508) | 2.114 (3,016) |
| 0 (all) | 3.143 (409) | 3.476 (1,021) | 2.244 (2,042) | 1.640 (4,084) |

## ⭐⭐ THE TAIL, AT LAST

| cell | n | PF | mean% | med% | win% | **worst%** | losing yrs |
|---|---:|---:|---:|---:|---:|---:|---:|
| `>+6%` alone (S43by spec) | 4,084 | 1.640 | +2.60 | +3.33 | 69 | **−245** | — |
| `gaps≥2000 × shape≤q25%` | 456 | 4.028 | +5.78 | +5.95 | 80 | −123 | 1 |
| ⭐ `gaps≥2500 × shape≤q25%` | 273 | **5.198** | **+6.56** | +6.04 | **82** | **−64** | 1 |
| `gaps≥2000 × shape≤q50%` | 911 | 3.665 | +5.28 | +5.64 | 80 | −123 | 1 |
| `gaps≥2000 × shape all` | 1,821 | 2.741 | +3.65 | +3.24 | 73 | −123 | 2 |

**Worst trade −245% → −64%.** For the first time no trade in the book loses more than
its notional, which was the single disqualifying property of this system.

🛑 **BUT READ THE YEARS BEFORE BELIEVING IT.** `gaps≥2500 × shape≤q25%`:
2017 5.29 · 2018 1.89 · **2019 0.47** · 2020 4.33 · **2021 16.49** · 2022 2.91 ·
**2023 57.39** · 2024 6.73 · 2025 6.23 · 2026 4.65.

A 57.39 and a 16.49 are not profit factors, they are **near-zero-loss years on ~25
trades**. n = 273 over 10 years is ~25/yr, so single-year cells are 20-30 trades and
the headline 5.198 is carried by two of them. The `gaps≥2000 × shape≤q50%` cell
(911 trades, PF 3.665, worst −123%) is the honest middle: four times the sample,
still a −123% tail.

## 🛑 Verdict — NOT ADOPTED, but the objection has changed

The tail is no longer untouchable: density + shape cut it from −245% to −123% at
n=911, and to −64% at n=273. What blocks adoption now is **sample size and year
concentration**, not an unmanageable loss distribution.

⏭ To move this forward the questions are, in order:
1. **Does the −64% cell survive out of sample?** It is 25 trades/yr with two carrier
   years. Everything else is secondary until this is answered.
2. **Borrow.** Names that rally 6%+ into a close on thin tape are exactly the
   hard-to-borrow list. Unmodelled, and it lands on the edge directly.
3. **The overnight stop** (S43bz): a post-market cover at 17:00 moved the ungated
   worst trade −245% → −169%. Untested in combination with these two levers, and it
   attacks the same tail from a different direction — it may be redundant now.

Settled and not to be re-litigated: window **60m**; entry **15:59 limit** (−0.06 PF);
density **thin, not dense**; reference window **short (5m–30m), not the full day**.


---

## S43cd (2026-08-14) — the BAR-COUNT family on the short side

Method, normalisation, and the overlap analysis are written up once in
`docs/longsnoozer_results.md` §S43cd. Short-side result only here.

`chg60k59 > +6%`, n 4,085, matched selectivity n = 1,022:

| lever | PF | mean% | med% | **worst%** |
|---|---:|---:|---:|---:|
| no filter | 1.643 | +2.60 | +3.33 | −245 |
| ⭐ RANDOM same-n | 1.613 | +2.63 | +3.57 | −155 |
| **$ `dv_over_open15`** | **3.476** | **+5.63** | +6.00 | −152 |
| $ `dv_over_open30` | 3.174 | +5.37 | +5.98 | −152 |
| $ `dv_over_rest` | 2.580 | +5.12 | +6.12 | −152 |
| **BAR `bar_over_open5`** | 3.031 | +4.42 | +4.74 | **−123** |
| **BAR `bar_over_open30`** | 3.025 | +4.40 | +4.94 | **−123** |
| **BAR `bar_over_open15`** | 3.008 | +4.37 | +4.77 | **−123** |
| BAR `bar_over_rest` | 2.015 | +3.63 | +5.20 | −152 |
| `tc_rate` | 2.407 | +4.91 | +6.27 | −152 |
| `gaps` (absolute) | 2.645 | +3.44 | +3.40 | −123 |

⭐ **Bar counts sit between dollars and gaps on PF (3.01-3.03 vs 3.476 and 2.645) and
carry the SMALLER TAIL (−123 vs −152).**

That matters more here than the PF ranking suggests. This system is not blocked on
expectancy — it is blocked entirely on the tail (§S43cb: worst −245% ungated, −123% at
`gaps ≥ 2000 × shape ≤ q50`). A lever that gives up 0.45 PF to hold the worst trade at
−123% is buying the thing that actually disqualifies the book.

⚠ The reference window is nearly irrelevant for bar counts (3.008 / 3.025 / 3.031)
where it was decisive for dollars (3.476 at 15m → 2.580 at rest-of-day). Do not carry
the dollar family's "short reference beats long" finding across — it does not apply.

⏭ NOT YET TESTED: `dv_over_open15 ∧ bar_over_open5` stacked. At 64% overlap there is
room, and it pairs the best-PF lever with the best-tail lever — the natural next step
given that the tail is the binding constraint.


---

# 🛑 S43cf (2026-08-16) — VOLATILITY DOES NOT WORK ON THIS SIDE

⭐ USER: *"first 15m, 30m, 60m and the entire day's volatility... whether along with
volume that makes a difference."* Full method, measure and clock decision in
`docs/longsnoozer_results.md` §S43cf, where the long side gets a **third lever**
(`volat_open60 ≤ 66bp`, PF 2.296 → 4.875, worst −55% → −24%, 100.0 bootstrap pctile).

**Here, nothing survives.** Incumbent cell `gaps ≥ 2000 ∧ shape ≤ q25`, n = 759,
PF 3.948, worst −123%. Against 2,000 random same-size subsets of that cell:

| filter (half the cell) | n | PF | null p50 | null p95 | pctile | worst% |
|---|---:|---:|---:|---:|---:|---:|
| `volat_over_day` ≥ median | 380 | 5.546 | 3.934 | **5.568** | 94.8 | −57 |
| ⚠ `gaps` ≤ median (incumbent, WRONG way) | 381 | 5.215 | 3.938 | 5.451 | 92.2 | −105 |
| `volat_open60` ≤ median | 380 | 4.521 | 3.909 | 5.521 | 76.0 | −64 |
| ⚠ `shape` ≤ median (incumbent tightened) | 380 | 4.485 | 3.917 | 5.434 | 75.0 | −64 |
| `volat_lh` ≤ median | 380 | 4.156 | 3.944 | 5.487 | 60.1 | −105 |
| `volat_day` ≥ median | 380 | 3.991 | 3.940 | 5.405 | 53.4 | −123 |
| `volat_open30` ≤ median | 380 | 3.962 | 3.939 | 5.600 | 51.5 | −64 |

**The best candidate (94.8) does not clear its own null's p95 (5.568 vs 5.546).**
Every ABSOLUTE volatility window lands at 33–76 — i.e. nothing. The threshold sweep on
`volat_over_day` runs 82.2 / 97.5 / 95.6 / 95.0 / 97.5 / 96.5 / 48.6 across keeps of
20–80%: it hovers ON the significance boundary rather than sitting above it, and
per-year it prints 0.64 (2019) and 1.06 (2018).

⚠⚠ **THE METHODOLOGICAL FINDING, which matters more than the null result.**
`snoozer_volat_test.py`'s single random-half control read **PF 4.681 against the
incumbent's 3.948** — *a random half of the cell beat the cell it came from*. One draw
at n≈380 with this tail is a coin flip, and reading it as a control would have
promoted `volat_over_day` (5.546) as a clear winner. It is not: 4.8% of random halves
do better. **Bootstrap the control on any short-side cell** — the fat tail that makes
this system hard to size is the same thing that makes single-draw controls useless.
See `feedback_iso_trip_control_for_stacked_features`.

⚠ Note in passing (not pursued): `gaps ≤ cell median` reads 92.2 — INSIDE the thin-tape
cell, the *less* thin half does better. The `gaps ≥` half reads 4.0. The persistence
lever may be non-monotone past its own threshold; worth a look, but it is a
re-derivation of §S43cb, not a volatility result.

## Why the asymmetry is coherent

The long spec buys a dense, loud flush, so "is this name violent to begin with?" is
new information — it separates a real dislocation from a normal day for that name. The
short spec already keys on **thin and quiet** tape, and a thin quiet tape is a
low-volatility tape: the incumbent levers have already consumed that axis. The 62–73%
overlap between `volat_lh` and `volat_open30`/`volat_day` picks confirms the
volatility family is largely one variable here, and `shape` overlaps it by only 0–5%
precisely because `shape` is measuring the thing that already worked.

**No change to the short spec.** `gaps ≥ 2500 ∧ shape ≤ q25 ∧ chg60k59 > +6%` stands.

---

# ⭐⭐ S43co (2026-08-16) — the short side rebuilt on the 30m window: `bar_over_open30` ADDS, `volat_over_day` is DEAD

⭐ USER: *"let's make the baseline cell the 2.816 (1,508) one with gaps >= 1000 and
shape ≤ q50%... We'll replace shape with dv_over_open30 and also add bar_over_open30.
Hmmm, it does seem like volat_over_day ≥ median is giving an improvement. We need to
check this on a bigger bucket though."*

✅ Baseline reproduced exactly: `chg60k59 > +6% ∧ gaps ≥ 1000 ∧ shape ≤ q50` →
**n = 1,508, PF 2.816**. ⚠ Note the quantile is computed WITHIN the `gaps ≥ 1000`
subset, not globally — a global q50 gives n=1,761 / PF 2.556 instead. The grid
(`snoozer_grid2.py`) has always done it this way; it matters and is easy to miss.

## §1 Swapping the shape window costs almost nothing

On the `gaps ≥ 1000` gate (n = 3,016, PF 2.114), each filter at its q50, percentile vs
1,500 random same-n subsets of the gate:

| filter | n | PF | worst5% | yrs<1 | pctile |
|---|---:|---:|---:|---:|---:|
| `dv_over_open15` (incumbent) | 1,508 | **2.816** | −35.5 | 1 | 100.0 |
| ⭐ `dv_over_open30` | 1,508 | 2.781 | −36.2 | 1 | 100.0 |
| `dv_over_open60` | 1,507 | 2.708 | −35.7 | 1 | 99.9 |
| `bar_over_open60` | 1,508 | 2.494 | −36.0 | 2 | 98.3 |
| `bar_over_open30` | 1,508 | 2.371 | −36.3 | 1 | 93.9 |
| `bar_over_open15` | 1,508 | 2.356 | −34.7 | 2 | 92.9 |

**The 15m→30m swap costs 0.035 PF** — the alignment to a common 30m window (§S43ck) is
effectively free on this side too. ⚠ Unlike the long side, where intensity was MONOTONE
in favour of 60m, the short side prefers the SHORTEST reference. That is consistent with
§S43cb's original short-side sweep (15m 3.476 → rest-of-day 2.580) and is the sign-flip
pattern again.

## ⭐⭐ §2 `volat_over_day` DIES on the bigger bucket — exactly as the ladder predicted

§S43cf flagged `volat_over_day ≥ median` at the 94.8 percentile on the narrow 759-trade
cell, and §S43cg's ladder showed it DECAYING across widths (97.5 → 97.0 → 81.0 → 74.2).
On the n=3,016 gate:

| feature | n | PF | mean% | worst5% | pctile |
|---|---:|---:|---:|---:|---:|
| ⭐ `volat_open30` **HIGH** | 1,508 | **2.409** | +4.68 | −44.5 | **95.8** |
| `volat_open15` HIGH | 1,508 | 2.367 | +4.59 | −44.1 | 92.7 |
| 🛑 **`volat_over_day` HIGH** | 1,508 | 2.122 | +3.10 | −36.5 | **51.9** |
| 🛑 `volat_over_day` low | 1,508 | 2.106 | +3.18 | −35.1 | 47.8 |
| `volat_open30` **low** | 1,508 | 1.689 | +1.59 | −26.1 | **0.1** |

🛑 **`volat_over_day` is at chance in BOTH directions (51.9 / 47.8).** The 94.8 was
narrow-cell noise, and the ladder called it correctly. **Do not use it.**

⭐⭐ **What IS real is ABSOLUTE volatility — and it flips sign from the long side.**
`volat_open30` HIGH sits at 95.8 and LOW at 0.1, a clean inversion of the long side's
`volat_open30` low. **Fifth feature to flip between the two systems** (after density,
dollar shape, entry mechanics, tail behaviour).

⚠ But look at the tail column: HIGH volatility buys PF and mean at a −44.5% worst-5%
against the gate's −35.8%, while LOW volatility gives −26.1%. On a system where the
tail is the binding constraint, that is not a free lunch — see §4.

## ⭐⭐ §3 `bar_over_open30` DOES add — but only at the right threshold

⚠⚠ **METHOD NOTE, and it changes the answer.** Applying `bar_over_open30 ≤ q50` where
q50 is the median of the `gaps ≥ 1000` GATE gives PF 2.639 — it HURTS. Applying it at
the median of the cell it actually refines (`gaps ∧ dv30`) gives **3.012** — it helps,
on both PF and tail. Same feature, same direction, opposite conclusion. **Build
sequentially: each threshold is the median OF THE CELL IT REFINES.**

| step | n | PF | PFtrim | mean% | med% | win% | p5% | worst5% | worst% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| population `chg > +6%` | 4,084 | 1.640 | 4.181 | +2.59 | +3.32 | 69 | −21.5 | −49.1 | −245 | 31 | 2 |
| + `gaps ≥ 1000` | 3,016 | 2.114 | 5.786 | +3.14 | +3.07 | 70 | −15.7 | −35.8 | −168 | 30 | 2 |
| + `dv_over_open30 ≤ q50` | 1,508 | 2.781 | 8.975 | +4.67 | +5.08 | 76 | −15.4 | −36.2 | −152 | 24 | 1 |
| ⭐ + `bar_over_open30 ≤ q50` | 754 | **3.012** | 9.859 | +4.38 | +5.03 | **78** | **−12.1** | **−30.5** | −123 | **22** | 1 |
| + `volat_open30` HIGH | 377 | 3.055 | 10.198 | **+5.39** | **+6.26** | 77 | −14.2 | −37.7 | −123 | 2 | 2 |
| ⭐ + `volat_open30` LOW | 377 | 2.947 | 9.044 | +3.37 | +3.81 | 78 | **−9.9** | **−23.9** | **−64** | **22** | 1 |

⭐ **`bar_over_open30` is a genuine addition: PF 2.781 → 3.012 AND the tail improves
on every measure** (p5 −15.4 → −12.1, worst-5% −36.2 → −30.5, worst −152 → −123,
loss rate 24% → 22%). It confirms the §S43cd read that bar counts are the better
TAIL instrument on this side.

## ⭐⭐ §4 On the short side volatility is a TAIL DIAL, not a PF lever

The last two rows are the same size (377) and near-identical PF (3.055 vs 2.947), but:

    volat HIGH   mean +5.39%  med +6.26%   worst-5% −37.7%   worst −123%   2 losing yrs
    volat LOW    mean +3.37%  med +3.81%   worst-5% −23.9%   worst  −64%   1 losing yr

**HIGH buys 2pp of mean for 14pp of worst-5% and 59pp of worst trade.** Since the tail
is this system's binding constraint (§S43cb: −245% raw worst; a −100%+ short is a
margin event, not a drawdown), **`volat_open30` LOW is the correct choice here despite
scoring worse standalone.** That is the exact opposite trade-off from the long side,
where low volatility improved BOTH PF and tail.

⚠⚠ **2023 IS DOING ENORMOUS WORK** — it reads 31.58 in the `+bar30` cell and **108.45**
in the `volat HIGH` cell. Those are near-zero-loss cells, and any PF at or below them in
this section should be read as "positive", not as its printed magnitude. The tail and
win-rate columns are the trustworthy ones.

## The short spec, updated

    SHORT, W = 30m:
      chg60k59 > +6%
      gaps            >= 1000       ABSOLUTE persistence — THIN tape
      dv_over_open30  <= q50        intensity   — QUIET close vs the open
      bar_over_open30 <= q50        relative persistence — GAPPY close
      (thresholds SEQUENTIAL: each is the median of the cell it refines)
      LIMIT 15:59-16:00, cover next open
    -> n = 754, PF 3.012, mean +4.38%, median +5.03%, win 78%,
       p5 -12.1%, worst-5% -30.5%, worst -123%, loss rate 22%

    Optional tail cell: + volat_open30 LOW -> n = 377, PF 2.947, worst -64%.

⚠ Every direction is INVERTED from the long side except that both want their own
`dv_over_open` extreme — long HIGH, short LOW. Do not import a long-side sign.

---

# ⭐⭐ S43cp (2026-08-16) — THE 16-CELL COMPLEMENTS TABLE: the short edge is BROAD and SHALLOW

⭐ USER: *"We'll have to convert the dv_over_open30 and bar_over_open30 so they use
absolute thresholds like the long system did... I also want to see the net profit %
figures. In the long system that revealed that the A++ cells have 3/4th of the profit.
I want to see whether that holds for the short system as well."*

**Answer: it does NOT hold. The short side is far more spread out.**

Four binary features, all at ABSOLUTE population medians (n = 4,084), `+` = favourable
FOR THIS SYSTEM — ⚠ **every direction is inverted from the long side**:

    V+  volat_open30    >= 81.9bp    HIGH volatility
    I+  dv_over_open30  <= 0.99      LOW intensity   (QUIET close vs the open)
    B+  bar_over_open30 <= 0.76      LOW rel. persistence (GAPPY close)
    G+  gaps            >= 1831s     HIGH gaps       (THIN tape)

⚠ The script's legend was printing the LONG-side directions on a short run — fixed.
⚠ Sorted by RAW PF here: short-side **trim lifts run 1.8–3.4** (long side: 1.3–1.7), so
the trimmed column re-ranks cells on which losers get dropped rather than on edge.

## §1 The 16 cells

| cell | score | n | share% | PF raw | PF trim5 | mean% | med% | win% | p5% | worst5% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `V+I+B−G+` | 3 | 25 | 0.6 | 8.012 | 19.525 | +8.36 | +5.36 | 76 | −6.2 | −11.3 | 24 | 0 |
| `V−I+B−G+` | 2 | 17 | 0.4 | 5.302 | — | +6.88 | +0.68 | 65 | −7.4 | −9.4 | 35 | 0 |
| ⭐ **`V+I+B+G+`** | 4 | **951** | **23.3** | **3.181** | 10.846 | **+5.04** | +5.41 | **78** | −13.0 | −32.8 | **22** | 1 |
| `V−I−B−G+` | 1 | 366 | 9.0 | 2.844 | 6.575 | +2.59 | +1.57 | 67 | −7.7 | **−15.8** | 33 | 2 |
| `V−I+B+G+` | 3 | 372 | 9.1 | 2.117 | 5.896 | +2.02 | +2.33 | 73 | −9.5 | −23.4 | 27 | 1 |
| `V−I+B+G−` | 2 | 66 | 1.6 | 1.768 | 3.538 | +2.25 | +1.79 | 64 | −15.3 | −27.0 | 36 | 1 |
| ⚠ `V+I+B−G−` | 2 | 294 | 7.2 | 1.752 | 4.030 | **+4.92** | **+9.09** | 75 | −36.9 | **−75.0** | 25 | 1 |
| `V+I+B+G−` | 3 | 270 | 6.6 | 1.485 | 3.607 | +2.70 | +5.22 | 68 | −26.1 | −66.1 | 32 | 1 |
| `V+I−B+G+` | 3 | 68 | 1.7 | 1.401 | 2.506 | +1.92 | +4.06 | 66 | −26.1 | −39.0 | 34 | 0 |
| ⚠ `V+I−B−G+` | 2 | 36 | 0.9 | 1.343 | 5.023 | +2.58 | +1.01 | 58 | −20.4 | **−167.6** | 42 | 0 |
| `V+I−B+G−` | 2 | 40 | 1.0 | 1.286 | 2.854 | +2.36 | +11.29 | 72 | −41.8 | −90.6 | 28 | 1 |
| `V−I−B+G+` | 2 | 207 | 5.1 | 1.248 | 2.638 | +0.65 | +1.43 | 65 | −15.1 | −27.0 | 35 | 2 |
| `V−I−B−G−` | 0 | 899 | 22.0 | 1.202 | 2.369 | +0.80 | +1.82 | 61 | −19.5 | −39.4 | 39 | 3 |
| ⚠ `V+I−B−G−` | 1 | 358 | 8.8 | 1.120 | 2.023 | +1.31 | +8.45 | 68 | −58.3 | **−98.6** | 32 | 2 |
| 🛑 `V−I+B−G−` | 1 | 47 | 1.2 | 0.739 | 1.002 | −1.01 | −0.57 | 45 | −12.2 | −17.6 | 55 | 1 |
| 🛑 `V−I−B+G−` | 1 | 68 | 1.7 | 0.539 | 1.054 | −2.38 | −0.26 | 47 | −22.9 | −48.5 | 53 | 2 |

⭐⭐ **14 of 16 cells have POSITIVE mean.** On the long side, 3 laddered cells were
negative and covered **34.7% of the population**; here the two negative cells are 115
trades (2.8%). **The short edge is nearly everywhere; the long edge is concentrated.**

## ⭐⭐ §2 NET PROFIT SHARE — the answer to the user's question

FLAT = `n × mean%`, the profit if every cell were traded at EQUAL size. This is the
sizing-model-free measure and the one the A++-share claim must be judged on.

| cell | book | n | mean% | FLAT P&L | flat % | flat cum % | SIZED % | sized cum % |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| ⭐ `V+I+B+G+` | A++ | 951 | +5.04 | 4,793 | **47.4%** | 47.4% | 72.9% | 72.9% |
| ⚠ `V+I+B−G−` | A+ | 294 | +4.92 | 1,446 | **14.3%** | 61.7% | 6.8% | 93.6% |
| `V−I−B−G+` | A+ | 366 | +2.59 | 948 | 9.4% | 71.1% | 8.2% | 81.1% |
| `V−I+B+G+` | A+ | 372 | +2.02 | 751 | 7.4% | 78.5% | 5.7% | 86.8% |
| `V+I+B+G−` | B++ | 270 | +2.70 | 729 | 7.2% | 85.8% | 2.9% | 96.5% |
| `V−I−B−G−` | B++ | 899 | +0.80 | 719 | 7.1% | 92.9% | 1.5% | 99.3% |
| `V+I−B−G−` | B++ | 358 | +1.31 | 469 | 4.6% | 97.5% | 0.7% | 100.0% |
| others (4 cells) | B++ | 549 | — | 414 | 4.1% | 101.6% | 1.2% | — |
| 🛑 `V−I−B+G−` | SKIP | 68 | −2.38 | −162 | −1.6% | 100.0% | 0.0% | 100.0% |

| | LONG (§S43cl) | SHORT (here) |
|---|---|---|
| top cell, flat P&L share | 75.9% | **47.4%** |
| cells to reach ~78% | **2** (87.6%) | **4** (78.5%) |
| cells to reach ~93% | 3 | **6** |
| population sizing to ZERO | **34.7%** | **2.8%** |
| cells with negative mean | 3 of 11 | 2 of 16 |

⭐⭐ **The 3/4 concentration does NOT transfer.** The short's single A++ cell is 47.4% of
flat P&L, and it takes FOUR cells to reach what two did on the long side. The SIZED
column reads 72.9% only because the §2 ladder assigns A++ a 1.00 multiplier and the
next cell 0.57 — **that number is the sizing model talking, not the profit
distribution.** For "where does the money live", read the FLAT column.

⚠⚠ **`V+I+B−G−` is the trap in this table.** It is the SECOND-largest flat contributor
(14.3%) on a +4.92% mean and a **+9.09% median** — and it carries a **−75.0% worst-5%**.
The short side's mean is inflated by the same fat right tail that makes its losses
ruinous, so **flat P&L share systematically OVERSTATES the tradeable value of the
high-volatility `G−` cells**. `V+I−B−G−` (−98.6%) and `V+I−B−G+` (−167.6%) are worse.
The ladder is right to cut them to 0.10–0.31×; the flat column is not a size
recommendation.

## ⭐ §3 Marginal value — and `B` INVERTS its role between the systems

| feature | ΔPF > 0 in | median ΔPF | fails in |
|---|---|---:|---|
| **G** thin tape | **5/5** | +0.710 | — |
| **I** quiet close | **5/6** | **+0.750** | `V−B−G−` (−0.463) |
| V high volatility | 4/6 | +0.450 | `I+B+G−`, `I−B−G−` |
| 🛑 **B** gappy rel. persistence | **2/5** | **−0.267** | `V−I−G+` (**−1.596**), `V−I−G−`, `V+I+G−` |

⭐⭐ **`B` is the MOST reliable feature on the long side (4/4, median +0.578) and the
LEAST on the short side (2/5, median −0.267).** §S43co found it adding 2.781 → 3.012 —
that holds, but ONLY inside the `I+G+` context it was measured in. Across the lattice it
is negative on average. **Do not promote `bar_over_open30` to a general short-side
filter**; it is a conditional refinement of the `quiet ∧ thin` cell.

## §4 Volatility terciles × score

| tercile | score | n | PF raw | mean% | med% | win% | worst5% | loss% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ⭐ **T3** [112, 1065)bp | 3 | 626 | **3.322** | **+5.84** | +6.31 | **79** | −37.4 | **21** |
| T3 | 1 | 275 | 1.889 | +6.23 | +11.36 | 80 | **−93.3** | 20 |
| T3 | 0 | 230 | 1.102 | +1.26 | +10.38 | 69 | **−115.5** | 31 |
| T2 [56, 112)bp | 3 | 573 | 2.408 | +2.83 | +3.40 | 75 | −25.4 | 25 |
| ⭐ **T1** [4, 56)bp | 3 | 124 | 2.870 | +2.21 | +1.97 | 70 | **−12.7** | 30 |
| T1 | 1 | 360 | 2.050 | +1.99 | +1.50 | 66 | −22.0 | 34 |

Same two-cell structure as the long side but **mirrored into the opposite tercile**:
**T3 score-3 is the expectancy cell** (PF 3.322, mean +5.84%) and **T1 score-3 the
survival cell** (worst-5% −12.7%, the best tail in the short study). ⚠ T3 at low scores
is where the −93% and −115% worst-5% figures live — high volatility without the other
three features is exactly the squeeze profile §S43bv warned about.

Script: `snoozer_complements.py --side short`.

---

# ⭐⭐ S43cq (2026-08-16) — RELATIVE GAP counts vs RELATIVE BAR counts: the premise is right, the conclusion is not

⭐ USER: *"Instead of using bar counts, it might be better to use relative gap counts as
the persistence feature in the short system since it prefers absolute high gap counts.
Gap counts and bar count ratios don't measure the same thing and it might be better to
use gap count ratios."*

## The reasoning is sound and worth stating

With `b` = fraction of seconds that traded,

    bar_over_openW = b_lh / b_open           ratio of PRESENCE rates
    gap_over_openW = (1−b_lh) / (1−b_open)   ratio of ABSENCE rates

These are **not** monotone transforms of each other, and they resolve in opposite
regimes: where both windows are near-continuous the BAR ratio saturates at 1 while the
GAP ratio has unbounded range; where both are thin it is the reverse. The short system
selects THIN late tape (`gaps ≥ 1831s` of 3540, `b_lh ≈ 0.48`) against a much denser
open (median 486 gaps of 1800, `b ≈ 0.73`), so the gap ratio *should* have the better
resolution here.

✅ **The premise checks out empirically** — Spearman −0.652 / −0.676 / −0.713 at
15/30/60m with only 74–79% pick overlap. A reparameterisation would be ±1.000 and 100%.
**They really are different features.**

⚠ **The denominator degenerates: 155 of 4,084 short ticker-days (3.8%) have a ZERO-gap
opening 30m** (252 of 4,164 on the long side). Dropping them would silently delete the
most continuously-traded names — the squeeze candidates §S43bv warns about. Both sides
get **+1 Laplace smoothing**: `((gapsLh+1)/3541) / ((gapsOpenW+1)/(W+1))`. With a median
of 486 opening gaps that is a ~0.2% correction on ordinary rows.

## 🛑 §1 Head to head — BAR wins everywhere that matters

Percentile is against 1,500 random same-n subsets of the same context.

**Whole population (n = 4,084, PF 1.640):**

| filter | n | PF | mean% | med% | worst5% | pctile |
|---|---:|---:|---:|---:|---:|---:|
| ⭐ BAR `bar_over_open15 ≤ q50` | 2,042 | **2.156** | +3.35 | +3.80 | **−36.6** | **100.0** |
| BAR `bar_over_open30 ≤ q50` | 2,042 | 2.083 | +3.24 | +3.86 | −37.8 | 99.9 |
| GAP `gap_over_open30 ≥ q50` | 2,042 | 1.750 | +3.10 | +4.72 | −49.3 | 86.8 |
| 🛑 GAP `gap_over_open15 ≥ q50` | 2,042 | 1.618 | +2.72 | **+4.43** | −53.1 | **40.9** |

**In the sequential build** (`gaps ≥ 1000` → `dv_over_open30 ≤ q50`, n = 1,508, PF 2.781):

| + filter | n | PF | mean% | med% | win% | p5% | worst5% | worst% | pctile |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ⭐⭐ **BAR `bar_over_open15`** | 754 | **3.242** | +4.45 | +4.87 | 78 | **−11.1** | **−27.4** | **−123** | **89.4** |
| BAR `bar_over_open30` | 754 | 3.012 | +4.38 | +5.03 | 78 | −12.1 | −30.5 | −123 | 74.1 |
| GAP `gap_over_open15` | 754 | 2.992 | **+5.53** | **+6.13** | **79** | −17.2 | −40.1 | −152 | 72.8 |
| BAR `bar_over_open60` | 754 | 2.922 | +4.35 | +5.09 | 77 | −13.1 | −31.7 | −123 | 67.9 |
| 🛑 GAP `gap_over_open30` | 754 | 2.614 | +5.05 | +6.12 | 78 | −17.8 | −45.1 | −152 | **29.9** |

⭐⭐ **THE PATTERN IS THE SAME ONE VOLATILITY SHOWED (§S43co §4): the GAP ratio buys
MEAN and MEDIAN at a materially WORSE TAIL.** At matched n=754, GAP delivers +5.53%
mean / +6.13% median against BAR's +4.45% / +4.87%, but its worst-5% is −40.1% against
−27.4% and its worst trade −152% against −123%. **On a system whose only disqualifier is
the tail, that trade is the wrong way round.** BAR is the correct choice, for the same
reason `volat_open30` LOW was.

⭐ **Incidental finding: `bar_over_open15` beats `bar_over_open30`** in the sequential
build (3.242 vs 3.012, better tail on every measure, 89.4 vs 74.1 percentile). Consistent
with §S43co §1 — this side prefers the SHORTEST reference window. ⚠ It breaks the 30m
alignment of §S43ck; whether that is worth 0.23 PF and 3pp of worst-5% is a judgement
call, but the 15m version should be the default here unless alignment is being enforced.

## §2 Across the lattice, GAP is marginally more consistent — but both fail the same way

Threshold = median of each context (§S43co):

| holding | ctx PF | BAR ΔPF | GAP ΔPF |
|---|---:|---:|---:|
| `V+I+G+` | 3.246 | +0.197 | +0.390 |
| `V+I+G−` | 1.635 | −0.130 | +0.201 |
| `V+I−G+` | 1.375 | +0.262 | +0.567 |
| `V+I−G−` | 1.133 | −0.229 | −0.047 |
| `V−I+G+` | 2.241 | −0.221 | +0.645 |
| `V−I+G−` | 1.270 | +0.517 | −1.172 |
| 🛑 `V−I−G+` | 2.026 | **−3.868** | **−3.920** |
| `V−I−G−` | 1.143 | −1.123 | −0.432 |
| | | **3/8, med −0.175** | **4/8, med +0.077** |

GAP is nominally more consistent, but **both collapse identically in `V−I−G+`** (Δ ≈
−3.9: the context PF is 2.026, the favourable half reads ~1.1 and the unfavourable half
~5.0). That single inversion — quiet-volatility, loud-close, thin-tape — is what makes
relative persistence unreliable on this side, and swapping the parameterisation does not
touch it. **Neither is a general short-side feature** (§S43cp).

## §3 On the LONG side they are a dead heat

Sequential build (`gaps ≤ 760` → `dv_over_open30 ≥ q50`, n = 410, PF 2.274):

    + BAR bar_over_open30   205   PF 3.228   worst5% −22.3   pctile 99.6
    + GAP gap_over_open30   205   PF 3.301   worst5% −22.3   pctile 99.5

Identical within noise, despite Spearman being only −0.484 there. The long side sits in
the dense regime where BOTH parameterisations resolve, so it is indifferent — which is
itself evidence that the short-side difference is real and regime-driven rather than
arbitrary.

## Verdict

**Keep `bar_over_open*`. The hypothesis was well-reasoned and the features genuinely
differ, but the gap ratio loses head-to-head on this side, and it loses specifically on
the axis this system cannot afford to lose on.** The one change worth making is
`bar_over_open15` over `bar_over_open30`.

Script: `snoozer_gap_ratio.py`.

---

# 🛑⭐ S43cr (2026-08-16) — CORRECTION: volatility DOES matter here. And the long-system shape does NOT transfer.

## 🛑 §1 CORRECTION to §S43cf — "nothing survives on the short side" was WRONG

⭐ USER: *"So the volatility split for this is real. Weren't you telling me at some
point that the volatility didn't matter?"*

Yes — §S43cf concluded *"SHORT: nothing survives"* for volatility. That conclusion was
measured on ONE cell and does not generalise. §S43co found the opposite on a wider
population and reported it as a new finding rather than flagging it as a REVERSAL; this
section makes the correction explicit.

**The mechanism — the §S43cf cell had already selected high-volatility names:**

| cell | n | median `volat_open30` | HIGH PF | LOW PF | ΔPF | pctile | null p95/p50 |
|---|---:|---:|---:|---:|---:|---:|---:|
| §S43cf `gaps≥2000 ∧ shape≤q25` | 759 | **125.4bp** | 3.899 | 4.016 | **−0.117** | 46.5 | 1.401 |
| §S43co gate `gaps≥1000` | 3,016 | 81.7bp | 2.409 | 1.689 | **+0.721** | **95.7** | 1.138 |
| §S43cp ctx `I+B+G+` | 1,323 | 109.1bp | 3.369 | 2.363 | **+1.007** | 86.9 | 1.220 |

The short population's median `volat_open30` is **81.9bp**. The §S43cf cell's median is
**125.4bp and its q25 is 97.3bp** — so **more than 75% of that cell was already above
the population median.** Splitting it at its own median compared high-vol against
very-high-vol; there was no contrast left to measure. Its noise floor was also 1.401
against the wider gate's 1.138.

⭐ **This is the §S43cd mechanism recurring** — "the door and the roster already
extracted it". A feature that measures null INSIDE a narrow cell may simply be the
thing that cell was already selecting on. **Always print the cell's own distribution of
the candidate feature against the population's before concluding a null.**

The user's read of the lattice was correct: `V+I+B+G+` 3.181 vs `V−I+B+G+` 2.117 is a
real ΔPF of +1.064 on 951 vs 372 trades.

## ⭐ §2 Volatility here is a RISK/RETURN DIAL, not a selector

Sequential build, volatility split LAST into terciles of the final cell:

| step | n | PF | mean% | med% | win% | p5% | worst5% | worst% | loss% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `gaps ≥ 1000` | 3,016 | 2.114 | +3.14 | +3.07 | 70 | −15.7 | −35.8 | −168 | 30 |
| + `dv_over_open30 ≤ q50` | 1,508 | 2.781 | +4.67 | +5.08 | 76 | −15.4 | −36.2 | −152 | 24 |
| ⭐ + `bar_over_open15 ≤ q50` | 754 | **3.242** | +4.45 | +4.87 | **78** | **−11.1** | **−27.4** | −123 | **22** |
| ↳ **V-LOW** [0, 94)bp | 251 | 3.000 | +3.07 | +3.26 | **80** | **−8.2** | **−21.0** | **−64** | **20** |
| ↳ **V-MID** [94, 132)bp | 251 | 3.027 | +4.14 | +4.79 | 77 | −8.8 | −28.7 | **−64** | 23 |
| ↳ **V-HIGH** [132, ∞)bp | 252 | **3.582** | **+6.13** | **+6.41** | 78 | −13.5 | −33.1 | −123 | 22 |

⭐⭐ **Mean is monotone (+3.07 → +4.14 → +6.13) and the tail degrades in lockstep
(−21.0 → −28.7 → −33.1) while PF stays flat at 3.0–3.6.** All three cells are
tradeable; volatility chooses HOW MUCH TAIL you accept, not WHETHER to trade. That is
the same role it played in §S43co §4 and the same role the GAP ratio played in §S43cq —
three independent measurements of one property of this system.

⚠ `V-HIGH`'s 2023 reads 192.97 on n=39 (near-zero-loss) and it prints 0.64 in 2019 and
0.60 in 2021. **The tail ORDERING is the trustworthy part; the PF advantage is not.**

## 🛑 §3 THE LONG-SYSTEM SHAPE DOES NOT TRANSFER

⭐ USER: *"For the long system we got rid of the absolute gaps, and only kept a
volatility band + 2 relative features... Maybe we could do it this time as well."*

Tried directly. **It does not work here, for two separate reasons.**

### (a) Absolute `gaps` is NOT redundant — but the useful threshold is much tighter

| rule | n | PF | PFtrim | worst5% | worst% | pctile vs `I+×B+` |
|---|---:|---:|---:|---:|---:|---:|
| `I+ × B+` (NO gaps) — the long-system shape | 1,659 | 2.406 | 7.183 | −36.9 | −152 | — |
| + `gaps ≥ 1000` | 1,633 | 2.446 | 7.475 | −36.5 | −152 | 89.0 |
| ⭐ + **`gaps ≥ q50` (1831s)** | 1,323 | **2.932** | 9.567 | **−30.0** | **−123** | **99.9** |
| (control) + `gaps < 1000` | 26 | 1.289 | 2.705 | −54.5 | −55 | — |

⭐ **`gaps ≥ 1000` really IS nearly redundant** — it removes only **26 of 1,659 trades**
once the two relative features are applied, which is why it looked droppable. But the
tighter **`gaps ≥ 1831s` adds materially: 2.406 → 2.932 at the 99.9 percentile, with the
tail improving −36.9% → −30.0% and the worst trade −152% → −123%.**

This is consistent with §S43cp's lattice, where `G` was the ONLY feature positive in
**5/5** contexts on the short side, while on the long side it was erratic (3/4, and just
64.4 percentile on the wide bucket) — which is why dropping it there was free.
**The two systems disagree about absolute gaps, and both readings are correct for their
own side.**

### (b) There is no clean volatility BAND on this side

Fine bp bands within `I+ × B+`, no gaps filter (n = 1,659):

| band bp | n | PF | mean% | worst5% | yrs<1 |
|---|---:|---:|---:|---:|---:|
| [30, 45) | 44 | 3.707 | +2.92 | −9.4 | 0 |
| [45, 60) | 127 | 1.701 | +1.52 | −26.0 | 1 |
| [60, 80) | 231 | 2.124 | +2.21 | −22.5 | 2 |
| [80, 100) | 280 | 1.839 | +2.40 | −34.4 | 2 |
| [100, 120) | 272 | 2.842 | +4.20 | −28.1 | 2 |
| [120, 140) | 192 | 2.136 | +3.88 | −54.1 | 2 |
| [140, 170) | 221 | 1.994 | +3.77 | −53.7 | 1 |
| [170, 210) | 163 | 4.132 | +8.21 | −41.3 | 0 |
| [210, 260) | 86 | 2.393 | +6.16 | −52.3 | 1 |
| [260, ∞) | 33 | 5.901 | +9.63 | −29.3 | 0 |

🛑 **It oscillates — 3.71 / 1.70 / 2.12 / 1.84 / 2.84 / 2.14 / 1.99 / 4.13 / 2.39 /
5.90 — with no plateau anywhere.** Contrast the long side (§S43cm), where PF was flat at
2.60–2.80 across a 60–120bp span and fell off cleanly outside it. **A band needs a
plateau to be a band; this is a dial with noise on it.** Imposing `volat ≥ 100bp` raises
PF (2.406 → 2.643) but WORSENS the tail (−36.9% → −43.3%), which is §2's dial again.

### Verdict

**Do not mirror the long-system shape.** The short system's three-feature core is
`gaps ≥ q50 ∧ dv_over_open30 ≤ q50 ∧ bar_over_open* ≤ q50`, with volatility applied
AFTERWARDS as a size dial rather than as a band. Two candidate structures, both
un-adopted:

    WIDE   I+ x B+ x gaps>=1831s              n=1,323  PF 2.932  worst-5% -30.0%
    TIGHT  gaps>=1000 -> dv30 -> bar15        n=  754  PF 3.242  worst-5% -27.4%
           (sequential thresholds, §S43co)

⚠ **NOT ADOPTED, and the tail still disqualifies it** — worst trade −123% in both. See
the banner at the top of this document.

Scripts: `snoozer_complements.py --side short`, `snoozer_gap_ratio.py`.

---

# ⭐⭐⭐ S43cs (2026-08-16) — THE QUIET CELL: the tail disqualifier finally breaks

⭐ USER: *"What if we do a vol breakdown on just the gaps ≥ 1000 feature? We could also
maybe try raising the chg60k59 >= 10%."*

Both parts of that were right, and together they produce **the first ShortSnoozer cell
whose worst trade is smaller than a normal stop.**

## Why `gaps ≥ 1000` was the right place to look

Median `volat_open30` on `gaps ≥ 1000` is **81.7bp** against the population's **81.9bp**
— the gate is NOT volatility-selected, unlike the §S43cf cell (125.4bp) that produced
the false null. This is the population where volatility can actually be measured.

## ⭐⭐ §1 The relation is BIMODAL — which is why every median split misread it

`gaps ≥ 1000 ∧ chg > +6%` (n = 3,016, PF 2.114):

| band bp | n | PF | PFtrim | mean% | med% | win% | p5% | worst5% | worst% | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ⭐⭐ **[0, 40)** | 542 | **3.248** | 6.417 | +2.93 | +1.64 | 66 | **−7.4** | **−12.6** | **−49** | 34 | 1 |
| 🛑 [40, 60) | 535 | **1.208** | 2.602 | +0.59 | +1.23 | 64 | −14.8 | −30.4 | −89 | 36 | **4** |
| 🛑 [60, 80) | 402 | **1.380** | 3.196 | +1.12 | +2.03 | 67 | −16.4 | −32.8 | −117 | 33 | 2 |
| [80, 100) | 378 | 1.857 | 4.253 | +2.50 | +3.72 | 72 | −17.1 | −33.5 | −95 | 28 | 2 |
| [100, 120) | 318 | 2.672 | 7.047 | +4.26 | +5.01 | 74 | −16.5 | −32.4 | −64 | 26 | 2 |
| [120, 150) | 307 | 2.012 | 7.982 | +3.95 | +5.90 | 76 | −19.2 | −58.3 | −123 | 24 | 2 |
| ⭐ [150, 190) | 274 | 3.165 | 10.262 | +6.29 | +7.37 | 78 | −17.0 | −40.9 | −152 | 22 | 2 |
| [190, 240) | 145 | 1.939 | 6.944 | +5.14 | +7.10 | 77 | −32.7 | −76.9 | −168 | 23 | 1 |
| ⭐ [240, ∞) | 115 | 3.801 | 12.735 | +9.88 | +10.80 | 79 | −22.1 | −52.1 | −92 | 21 | 0 |

⭐⭐ **TWO separate good regions — the very QUIET tape and the very VIOLENT tape — with
a dead zone at 40–80bp.** The population median is 81.9bp, so **every median split in
this document has been lumping the excellent [0,40) with the terrible [40,80)** and
calling the result "LOW". That is why §S43co read `volat_open30` HIGH at the 95.8
percentile and LOW at 0.1: the HIGH half won because the LOW half was diluted, not
because quiet tape is bad. **Third correction in this thread with the same root cause —
a median split on a non-monotone relation.**

## ⭐⭐⭐ §2 Raising the signal threshold DEEPENS the quiet cell

`gaps ≥ 1000 ∧ volat_open30 < 40bp`, sweeping `chg60k59`:

| chg > | ctx n | ctx PF | ctx worst% | cell n | PF | pctile | mean% | med% | win% | p5% | worst5% | **WORST%** | **>100% losses** | loss% | yrs<1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| +6% | 3,016 | 2.114 | −168 | 542 | 3.248 | 99.7 | +2.93 | +1.64 | 66 | −7.4 | −12.6 | **−49** | **0** | 34 | 1 |
| ⭐ **+8%** | 1,569 | 2.167 | −168 | **200** | **5.310** | **99.6** | +4.24 | +2.30 | 74 | **−6.7** | **−11.1** | **−21** | **0** | 26 | **0** |
| +10% | 954 | 2.143 | −168 | 103 | 4.338 | 95.1 | +4.51 | +3.30 | 74 | −8.3 | −12.9 | −21 | **0** | 26 | **0** |
| +12% | 606 | 2.064 | −168 | 54 | 9.799 | 98.7 | +6.87 | +4.92 | 83 | −5.5 | −11.5 | −13 | **0** | 17 | **0** |
| +15% | 341 | 1.845 | −168 | 30 | 10.562 | 96.1 | +7.81 | +5.58 | 90 | −6.1 | −13.1 | −13 | **0** | 10 | **0** |

⭐⭐⭐ **THIS IS THE DISQUALIFIER GONE.** The banner at the top of this document says
*"STILL NOT ADOPTED — and the reason is the TAIL, which nothing has touched: worst trade
−245%, 0.4% of trades lose more than 100% of notional."*

In the `chg > +8%` quiet cell: **worst trade −21%, ZERO trades losing over 100%,
p5 −6.7%, zero losing years.** The context it is drawn from still has a −168% worst
trade; the cell has none of them. Nothing else in this study has come close — the best
prior tail was −64% (§S43cb).

⚠ **`chg > +8%` is the operating point.** +12% and +15% look better but are 54 and 30
trades; +8% keeps 200 with the tail already collapsed.

## ⭐⭐ §3 It is NOT a price or liquidity proxy

Matched-n (200) substitutes inside the same `chg > +8%` gate:

| filter | n | PF | pctile | mean% | worst5% | WORST% |
|---|---:|---:|---:|---:|---:|---:|
| ⭐ **`volat_open30 < 40bp`** | 200 | **5.310** | **99.6** | +4.24 | **−11.1** | **−21** |
| ⭐ `volat_lh` LOW (the same idea, last hour) | 200 | 5.061 | 99.8 | +3.56 | −8.8 | **−17** |
| 🛑 `close_d` HIGH | 200 | 1.366 | 2.8 | +1.41 | −46.1 | −98 |
| 🛑 `dv_lh` HIGH | 200 | 1.223 | 1.6 | +1.04 | −61.4 | −98 |
| 🛑 `gaps` LOW | 200 | 1.636 | 13.4 | +3.35 | −65.8 | −98 |

**Only volatility works, and BOTH volatility windows work** (opening 5.310 / last-hour
5.061, worst −21% / −17%). Price and dollar volume are at the 2.8 and 1.6 percentiles
with −98% worst trades. The quiet names ARE bigger and more liquid — median price
**$17.16 vs $3.78**, last-hour dollars **$36.5M vs $6.1M** — but that correlation is not
the mechanism, and gating on it directly fails.

⭐ **The reading:** a low-volatility name that nonetheless ran +8% in the closing hour is
a name doing something genuinely out of character, on a tape that does not squeeze. The
ruinous shorts in this system have always been the sub-$5, high-volatility, thin names
(GME, OCGN, HOLO, GNS — §S43bv). This filter removes exactly that population, and it
does so by measuring the morning, hours before the entry.

## ⚠ §4 The caveats, and they are real

**2020 is 79 of the 200 trades (40%).** Excluding it:

    full cell        n=200  PF 5.310  mean +4.24%  med +2.30%  win 74%  worst -21%  loss 26%
    EXCLUDING 2020   n=121  PF 3.619  mean +3.10%  med +1.63%  win 68%  worst -21%  loss 32%
    2020 only        n= 79  PF 9.824  mean +5.99%  med +4.49%  win 85%  worst -13%  loss 15%

⭐ **The edge survives (3.619) and the tail is UNCHANGED at −21%** — the tail claim, which
is the whole point, does not depend on 2020 at all. But half the PF does.

⚠⚠ **Frequency is low and very uneven:** 2016 n=3 · 2017 n=1 · 2018 n=12 · 2019 n=3 ·
2020 n=79 · 2021 n=47 · 2022 n=14 · 2023 n=2 · 2024 n=6 · 2025 n=20 · 2026 n=13.
**Median ~13/yr, and four years have fewer than 5 trades.** This is not a book that can
be sized on its own; it is a high-quality satellite.

⚠ Still unmodelled, and all three cut the same way: borrow cost and availability, fees,
and whether the 15:59 limit and the next open are attainable at size. These are less
alarming here than for the wider book — median price $17.16 and $36.5M of closing-hour
dollars is a very different borrow proposition from a $3.78 name on $6.1M — but they are
still unmodelled.

## The candidate spec (⚠ NOT ADOPTED — borrow unmodelled)

    SHORT-QUIET:
      chg60k59       > +8%          the signal, raised from +6%
      gaps           >= 1000        thin late tape (absolute)
      volat_open30   <  40bp        ⭐ the quiet-morning filter
      LIMIT 15:59-16:00, cover next open
    -> n = 200, PF 5.310, mean +4.24%, median +2.30%, win 74%,
       p5 -6.7%, worst-5% -11.1%, WORST TRADE -21%, zero >100% losses,
       zero losing years, ~13 trades/yr

⚠ The two relative features (`dv_over_open30`, `bar_over_open*`) are NOT in this spec —
they have not been tested on top of the quiet cell and n=200 does not have room for
them. That is the next test, not an omission by choice.

Script: `scripts/equity/snoozer_volat_quiet.py`.

---

# 🛑 S43ct (2026-08-16) — inside the QUIET cell: intensity does nothing, and persistence's spectacular inversion is a 2020 ARTEFACT

⭐ USER: *"Let's focus on this cell. Do intensity and persistence make a difference
inside it?"*

Cell = `gaps ≥ 1000 ∧ chg > +6% ∧ volat_open30 < 40bp` — n = 542, PF 3.248,
worst −49%, median price $16.97, median closing-hour dollars $31.2M.

⚠ **Both directions were tested for every feature**, not the incumbent short-side sign
only: the quiet cell is a different name population from the one those signs were fitted
on ($16.97 vs $3.78 median price), and four features have already flipped between the
two Snoozer systems.

## §1 INTENSITY: nothing, in any window, in either direction

| filter | n | PF | pctile |
|---|---:|---:|---:|
| `dv_over_open15` ≤ / ≥ cell median | 271 | 3.230 / 3.269 | 48.0 / 54.5 |
| `dv_over_open30` ≤ / ≥ | 271 | 3.155 / 3.360 | 42.0 / 60.4 |
| `dv_over_open60` ≤ / ≥ | 270 | 2.788 / 3.585 | 14.6 / 72.4 |
| `dv_over_rest` ≤ / ≥ | 271 | 3.456 / 3.039 | 63.8 / 30.2 |

**Eight measurements, all between the 15th and 72nd percentile.** Intensity — the
feature that took the wide short book from 2.114 to 2.781 — is pure chance here. The
quiet cell has already selected on whatever it was measuring.

## ⚠⚠ §2 PERSISTENCE looked like the find of the session. It is not.

| filter | n | PF | PFtrim | mean% | win% | worst5% | WORST% | pctile |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 🛑 `bar_over_open15` ≤ median (**incumbent sign**) | 271 | **1.696** | 2.933 | +1.28 | 59 | −15.4 | −49 | **0.0** |
| ⭐ `bar_over_open15` ≥ median (**opposite**) | 271 | **6.938** | 16.926 | +4.57 | 73 | −9.0 | −21 | **100.0** |
| `bar_over_open30` ≤ / ≥ | 271 | 1.850 / 6.180 | | | | | | 0.0 / 100.0 |
| `bar_over_open60` ≤ / ≥ | 271 | 1.853 / 5.868 | | | | | | 0.0 / 100.0 |
| `volat_lh` ≤ median | 271 | 4.627 | 7.788 | +3.44 | 65 | −7.6 | **−9** | 98.9 |

A complete sign inversion at 0.0 vs 100.0 percentile, across all three windows, and it
**replicated at `chg > +8%`** (9.419 at the 97th vs 3.379 at the 1.7th). It would have
been the strongest single result in this document.

### 🛑 THE 2020 CONTROL KILLS IT

2020 is **39% of the quiet cell** (212 of 542) and its PF there is 6.26. A filter that
preferentially selects 2020 dates inherits that without having any edge. It does:

| variant | n | PF | worst% | pctile | **2020 share** |
|---|---:|---:|---:|---:|---:|
| the cell (all years) | 542 | 3.248 | −49 | — | 39% |
| the cell EX-2020 | 330 | **1.807** | −24 | — | — |
| EX-2020 + `bar_over_open15` ≥ | 165 | 1.976 | −21 | **69.2** | **54%** |
| EX-2020 + `bar_over_open30` ≥ | 165 | 2.064 | −21 | **75.3** | **52%** |
| EX-2020 + `bar_over_open60` ≥ | 165 | 2.301 | −21 | **91.5** | **51%** |
| 🛑 EX-2020 + `volat_lh` ≤ | 165 | **1.081** | −9 | **0.1** | 41% |

**PF 6.938 → 1.976, percentile 100.0 → 69.2.** The high-persistence half is **54% 2020**
against the cell's own 39% — it is a 2020 selector. `volat_lh` is worse still: **98.9 →
0.1**, a total reversal, and it carried **5 losing years** even in the full sample.

⭐ **The physical mechanism confirms it is a name-type selector, not a signal:**

    bar HI  n=271  open-15m 301/900 bars (33%)  last-hr 1812/3540 (51%)  price $23.21  $44.1M
    bar LO  n=271  open-15m 514/900 bars (57%)  last-hr 1390/3540 (39%)  price $11.58  $21.0M

`bar_over_open15` HIGH selects **larger, more liquid names that were DORMANT in the
morning and woke up in the afternoon**. 2020 was full of them. Outside 2020 they are
ordinary.

## ⚠⚠ §3 The important side-effect: the `chg > +6%` cell is far more 2020-dependent than `chg > +8%`

    chg > +6%  cell:  full PF 3.248  ->  EX-2020  1.807   (worst -49% -> -24%)
    chg > +8%  cell:  full PF 5.310  ->  EX-2020  3.619   (worst -21% -> -21%)

**The user's other instinct — raising the signal threshold — is the durable one.** At
+8% the cell keeps PF 3.619 without 2020; at +6% it falls to 1.807, barely above the
2.114 gate it came from. ⭐ **`chg > +8%` should be the operating point, and §S43cs's
`chg > +6%` row should be read as the weaker variant it is.**

⭐ **The tail claim survives everywhere** — worst −24% (chg>+6%) and −21% (chg>+8%)
with 2020 removed. That is the part of §S43cs that does not depend on the regime.

## Verdict

**Neither intensity nor persistence adds inside the quiet cell.** The quiet-volatility
filter appears to be doing all the work by itself, which is consistent with §S43cs's
substitution table where only volatility measures cleared the null. The spec stays:

    chg60k59 > +8%  ∧  gaps >= 1000  ∧  volat_open30 < 40bp

⚠ **METHOD NOTE, third time in this thread.** The failure mode was again a subgroup
composition effect: §S43cf's null came from a cell already selected on volatility,
§S43cs's median splits came from a bimodal relation, and this one came from a filter
selecting a single year. **When a cell is dominated by one regime, run the
leave-that-regime-out control BEFORE writing anything down.**

Script: `scripts/equity/snoozer_quiet_features.py` (the 2020 control is §3 of it).

---

# ⭐⭐ S43cu (2026-08-16) — the marginal table was mis-COUNTED, and persistence should be DROPPED

⭐ USER: *"I am thinking of maybe removing persistence from the mix for the short system.
Maybe a slowdown simply isn't as strong of a signal as one would expect it to be for the
short system, but I am confused as to why it is 2/5? Shouldn't there be 16 different
systems to consider it in?"*

## 🛑 §1 The counting was wrong — and it made the features LOOK comparable when they were not

With 4 features, holding the other three fixed gives **2³ = 8 contexts** per feature (not
16 — 16 is the number of CELLS in the lattice; each marginal comparison uses two cells,
so 8 comparisons). §S43cp's table then **silently dropped any context with fewer than 40
trades on either side**, which left different features judged on DIFFERENT context sets:
V and I kept 6, B and G kept 5. **"B is 2/5" and "G is 5/5" were never comparable
numbers.** My error, and the fix is to print all 8 and flag the thin ones.

| feature | ΔPF>0 (all 8) | median ΔPF | ⭐ n-weighted mean ΔPF | (old, mis-counted) |
|---|---|---:|---:|---|
| **G** thin tape | **8/8** | +1.176 | **+1.551** | 5/5 |
| **I** quiet close | **7/8** | +1.049 | **+0.998** | 5/6 |
| V high volatility | 5/8 | +0.450 | +0.426 | 4/6 |
| 🛑 **B** gappy persistence | **3/8** | **−0.465** | **−0.796** | 2/5 |

n-weighted uses `min(+n, −n)` so a context whose minority side is 17 trades cannot
outvote one with 900. **The corrected picture makes B look WORSE, not better.**

## ⭐⭐ §2 But the reason is COLLINEARITY, not that slowdown hurts

Pairwise agreement between the four favourable halves (50% = independent):

| | V | I | B | G |
|---|---|---|---|---|
| **V** | — | 75% | 65% | 53% |
| **I** | 75% | — | **81%** | 67% |
| **B** | 65% | **81%** | — | **78%** |
| **G** | 53% | 67% | **78%** | — |

**`B` is 81% the same pick as `I` and 78% the same as `G`.** It is the most redundant of
the four. `V` and `G` are nearly independent at 53%.

The consequence is visible directly — `B+` share inside each context:

| context | ctx n | B+ n | B− n | B+ share |
|---|---:|---:|---:|---:|
| 🛑 `V+I+G+` | 976 | 951 | **25** | **97%** |
| 🛑 `V−I+G+` | 389 | 372 | **17** | **96%** |
| `V+I−G+` | 104 | 68 | 36 | 65% |
| `V+I+G−` | 564 | 270 | 294 | 48% |
| `V−I−G+` | 573 | 207 | 366 | 36% |
| `V+I−G−` | 398 | 40 | 358 | 10% |
| `V−I−G−` | 967 | 68 | 899 | **7%** |

⭐ **In the two best contexts there is essentially NO variation left in `B` — 96–97% of
those cells are already `B+`.** The catastrophic ΔPFs that drove the negative
(−4.831 at `V+I+G+`, −3.185 at `V−I+G+`) compare **951 trades against 25** and **372
against 17**, where the tiny complements read PF 8.012 and 5.302. Those are noise, not
evidence that dense tape is better. **The marginal-value framework simply cannot measure
a feature that has no variation left to measure.**

## ⭐⭐⭐ §3 The decisive test: does `B` add ON TOP of the others?

Sequential, threshold = median of the cell it refines, percentile vs 3,000 random
same-n subsets of that cell:

| base | base n | base PF | + B n | + B PF | pctile | worst5% (base) |
|---|---:|---:|---:|---:|---:|---|
| `G+` only | 2,042 | 2.568 | 1,021 | 3.018 | **93.8** | −30.3 (−29.4) |
| `G+ I+` | 1,365 | 3.006 | 683 | 3.131 | **64.0** | −29.2 (−29.5) |
| `G+ I+ V+` | 976 | 3.246 | 488 | 3.354 | **59.8** | −28.3 (−32.4) |

⭐⭐ **`B` adds only when `I` is ABSENT.** On `G+` alone it reaches 93.8; add intensity
and it drops to 64.0, add volatility too and 59.8. That is exactly what 81% collinearity
with `I` predicts — and `I` is much the stronger of the pair (7/8 and +0.998 against
3/8 and −0.796).

⚠ **This also downgrades §S43co.** That section reported *"`bar_over_open30` DOES add:
2.781 → 3.012"* — but §S43cq's percentile for that same row was **74.1**, and
`bar_over_open15`'s was **89.4**. Neither ever cleared 95. I stated it as a genuine
addition on the strength of the PF and tail movement without leading with the
percentile. **Persistence has never cleared the bar on this side.**

## ✅ VERDICT: drop persistence from the short system

The user's instinct is right, and for a sharper reason than "slowdown isn't a strong
signal":

1. It is **81% redundant with intensity**, which is strictly better on every measure.
2. It **adds nothing once intensity is applied** (64.0 / 59.8 percentile).
3. Its best-ever showing was **89.4** (§S43cq), below the 95 bar.
4. Inside the quiet cell it does nothing, and its apparent inversion there was a **2020
   composition artefact** (§S43ct).
5. Swapping to the gap-ratio parameterisation does not rescue it (§S43cq).

**The short system is three features: `gaps` (absolute, thin tape) × `dv_over_open30`
(quiet close) × volatility.** ⚠ The ONE thing lost is the tail: §S43co's `+bar15` step
improved worst-5% −36.2% → −27.4% and worst −152% → −123%. That gain was real even
though the PF gain was not, so **if the tail is the binding constraint, keep it as an
optional tail filter rather than a core feature** — and note §S43cs's quiet-volatility
cell achieves a far better tail (−21%) without it.

⚠ **METHOD:** print every context, flag the thin ones, and n-weight. A ratio like "2/5"
hides both how many comparisons were discarded and how lopsided the survivors were.

Script: the corrected 8-context table is in
`scripts/equity/snoozer_complements.py` §4 (re-run with the fix below).

---

# ⭐⭐⭐ S43cv (2026-08-16) — the GAP COUNT is the strongest lever in the quiet cell, and it is GRADED

⭐ USER: *"intensity and persistence didn't make much of a difference, but what about
the gap count?"* — plus a theory worth testing rather than quoting:

> *"if the number of gaps is already significant, the increase in sparsity doesn't make a
> strong signal. A slow stock becoming slower maybe isn't that big of a deal. It's
> acceleration that would get the trader's attention."*

⚠ The `gaps ≥ 1000` floor is **removed** for this test. Measuring gaps only above its
own floor would repeat the §S43cf error — judging a feature inside a cell already
selected on it. ⚠⚠ And the **2020 control is built in**, not bolted on, after §S43ct
watched a 100.0-percentile result collapse to 69.2 once the year was removed.

Base: `chg > +8% ∧ volat_open30 < 40bp`, **no gaps filter** — n = 319, PF 2.514
(the full `chg > +8%` population is n = 2,306, PF 1.602; 2020 is 32% of the cell).

## ⭐⭐ §1 It is a strong MONOTONE GRADE, not a floor — and dense tape LOSES MONEY

| band (seconds not traded, of 3540) | n | PF | mean% | win% | p5% | worst5% | WORST% | loss% | 2020% | **n ex20** | **PF ex20** | **WORST ex20** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 🛑 **[0, 500)** | 69 | **0.770** | **−1.19** | 57 | −22.0 | −28.0 | −34 | 43 | 13% | 60 | **0.720** | −34 |
| [500, 1000) | 50 | 3.505 | +3.67 | 68 | −5.1 | −18.8 | −32 | 32 | 28% | 36 | 2.528 | −32 |
| [1000, 1500) | 58 | 3.004 | +2.76 | 66 | −8.5 | −16.9 | −21 | 34 | 43% | 33 | 2.448 | −21 |
| ⭐ **[1500, 2000)** | 61 | **7.773** | +5.07 | **84** | −4.9 | −9.2 | −12 | **16** | 38% | 38 | **3.883** | −12 |
| ⭐ **[2000, 2500)** | 56 | **6.934** | +4.95 | 77 | −6.6 | **−7.9** | **−9** | 23 | 45% | 31 | **5.080** | **−9** |
| [2500, 3000) | 23 | 4.854 | +4.14 | 65 | −3.7 | −15.2 | −15 | 35 | 26% | 17 | 2.906 | −15 |

⭐⭐ **The densest band is a LOSING SHORT — PF 0.770, mean −1.19%, and 0.720 with 2020
removed.** A quiet-volatility name that ran +8% on a *continuous* tape is a move with
real participation behind it, and shorting it loses money. Sparsity is not a nice-to-have
filter here; its absence is a disqualifier.

⭐ Everything improves monotonically to [1500, 2500), where the tail reaches **−9% to
−12%** and the win rate 77–84%. **And the ex-2020 column tracks it** — unlike §S43ct,
this survives the year control at every band.

## ⭐⭐ §2 `>= 1000` was INHERITED, not chosen

| floor | n | PF | win% | worst5% | WORST% | yrs<1 | 2020% | n ex20 | **PF ex20** | pctile |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `>= 0` | 319 | 2.514 | 70 | −21.0 | −34 | 1 | 32% | 217 | 1.745 | — |
| `>= 500` | 250 | 4.820 | 73 | −12.5 | −32 | **0** | 37% | 157 | 3.280 | **100.0** |
| `>= 750` | 228 | 5.324 | 73 | −10.7 | −21 | **0** | 38% | 141 | 3.489 | **100.0** |
| `>= 1000` *(incumbent)* | 200 | 5.310 | 74 | −11.1 | −21 | **0** | 40% | 121 | 3.619 | **100.0** |
| `>= 1500` | 142 | 6.884 | 78 | −9.4 | −15 | **0** | 38% | 88 | 4.096 | **100.0** |
| ⭐ **`>= 1750`** | **111** | **8.100** | 78 | **−8.9** | **−15** | **0** | 41% | 66 | **5.003** | **100.0** |
| `>= 2000` | 81 | 6.316 | 74 | −9.4 | −15 | **0** | 38% | 50 | 4.263 | 99.4 |
| `>= 2500` | 25 | 5.147 | 68 | −15.2 | −15 | 1 | 24% | 19 | 3.199 | 82.5 |

**A wide plateau from 500 to 2000, every rung at the 100th percentile**, with PF and tail
both improving as the floor rises. `>= 1000` sits in the middle of it and is not special
— it came from the §S43cb grid, not from a sweep. **`>= 1750` is the better operating
point on this evidence**: PF 8.100 (ex-2020 **5.003**), worst-5% −8.9%, worst trade
−15%, zero losing years.

⚠ **The cost is frequency: 111 trades over 11 years ≈ 10/yr**, against 200 (≈18/yr) at
`>= 1000`. Given four years already have fewer than 5 trades (§S43cs §4), this is the
binding practical constraint, not the statistics. **`>= 1500` (n=142, PF 6.884, ex-2020
4.096, worst −15%) is the sensible compromise.**

## ⭐⭐⭐ §3 THE THEORY IS CONFIRMED

Head to head inside the quiet cell, each at its own median, same n:

| filter | n | PF | **pctile** | worst5% | WORST% | n ex20 | **PF ex20** |
|---|---:|---:|---:|---:|---:|---:|---:|
| the quiet cell (no filter) | 319 | 2.514 | — | −21.0 | −34 | 217 | 1.745 |
| ⭐⭐ **ABSOLUTE sparsity** `gaps ≥ median` | 160 | **6.061** | **100.0** | **−9.4** | **−15** | 97 | **4.042** |
| 🛑 **CHANGE in sparsity** `bar_over_open30 ≤ median` | 160 | **1.987** | **7.0** | −20.6 | −34 | 126 | 1.650 |
| intensity `dv_over_open30 ≤ median` | 160 | 3.812 | 98.4 | −16.5 | −24 | 90 | 2.315 |

⭐⭐⭐ **Absolute sparsity: 100.0 percentile. Change in sparsity: 7.0 percentile.** The
prediction was exact — and note the change measure is not merely useless but sits BELOW
random, i.e. the trades it selects are worse than a coin flip on the same cell.

**The mechanism the user proposed holds up.** Once the tape is already sparse, *further*
relative thinning carries nothing, because the level — not the change — is what says
nobody is on the other side. `bar_over_*` measures deceleration from a baseline that has
already been conditioned away. This is the same conclusion §S43cu reached from the
collinearity side, arrived at independently.

⚠ **Intensity behaves differently here than in §S43ct** (98.4 vs "nothing"): §S43ct
tested it at `chg > +6%` with `gaps ≥ 1000` ALREADY applied, and `gaps`/`dv_over_open30`
are 67% collinear (§S43cu §2). With the gaps floor off, intensity has room; with it on,
it does not. **`gaps` is the primary and intensity is its redundant partner** — the same
relationship `I` had to `B`, one level up.

## Updated candidate spec (⚠ still NOT ADOPTED — borrow unmodelled)

    SHORT-QUIET v2:
      chg60k59      > +8%
      volat_open30  <  40bp      the quiet-morning filter
      gaps          >= 1500      ⭐ raised from 1000; graded, not a floor
      (persistence DROPPED per §S43cu; intensity redundant with gaps)
    -> n = 142, PF 6.884, win 78%, worst-5% -9.4%, WORST TRADE -15%,
       zero losing years, ex-2020 PF 4.096  ·  ~13 trades/yr

**Two features and a signal threshold.** The system is simpler than it was three
sections ago and its worst trade has gone −245% → −15%.

Script: `scripts/equity/snoozer_quiet_gaps.py`.

---

## ⏭ NEXT SESSION (queued 2026-08-14)

⚠ **Volatility is DONE — see §S43cf above (negative result).** Still open:

- The binding constraint here is the **TAIL**, not expectancy. Judge any new
  feature on `worst%` and p99 first, PF second — that is the opposite emphasis to the
  long side, and it is how `bar_over_*` earned its place despite losing 0.45 PF.
- ⏭ Still untested from §S43cd: **`dv_over_open15` ∧ `bar_over_open5`** stacked. 64%
  overlap, pairs the best-PF lever with the best-tail lever.
- ⚠ Expect the SIGN to be flipped from the long side on anything that works. Four
  features have now behaved that way (density, dollar shape, entry mechanics, tail);
  importing a long-side sign gave PF 0.829, worse than random. ⭐ Volatility is the
  first feature that does not flip — it simply **does not transfer at all**.
