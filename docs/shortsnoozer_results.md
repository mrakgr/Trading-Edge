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
