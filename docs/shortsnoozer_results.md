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

⚠ **The 1s corpus ends at 15:58:59** (bucket 57539) on every day — no 16:00 bar,
no post-market, and the last RTH minute (typically the day's heaviest) is missing.
A full-day rebuild is queued; every number here should be re-derived after it.

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
