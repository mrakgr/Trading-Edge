# Mid-Cap Momentum v2 — Log-Space Volatility Filters

**Status: working long-only daily-momentum edge, PF ~1.77 post-breadth, on honest next-open fills.**
This is the current production system. It supersedes both `momentum_v0` (whose mean-reversion
trailing-limit results were inflated by a fill bug) and the v1 *exit*-correction work, by re-deriving
the **entry** filters on a log-volatility scale and dropping the trailing-limit / expansion exits in
favour of a plain next-open exit.

`TradingEdge.MomentumV2` is a ground-up F# rewrite: all indicators computed in a single in-memory
pass (no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~15s**,
so the parameter sweeps below are minutes, not hours.

---

## The system in one screen

Long-only daily momentum on US common stocks / ADRs (`ticker_reference.type IN ('CS','ADRC')`),
2005–2026. One position per breakout signal, fixed $10k notional, uncapped concurrency, no
compounding (so net P&L is a raw edge-and-breadth measure, not an achievable equity curve).

**Entry** — on a daily bar, go long at the close when ALL hold (each indicator uses *prior* bars,
no lookahead):

| gate | threshold | meaning |
| --- | --- | --- |
| entry-day move | **≥ 10%** | `close/prevClose − 1` — the breakout has to *announce itself* |
| relative volume | **6 ≤ rvol ≤ 20** | `volume / 28-day avg volume`; band, not a floor |
| ATR% (log) | **< 0.11** | mean log-true-range over 14 prior bars (see below) |
| tightness (linear) | **< 4.0** | `(14d range) / ATR` — prior consolidation must be tight (linear default; sharper loose-tail cut than log) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high |
| price floor | **≥ $5** | no sub-$5 names |
| liquidity | **avg dollar volume ≥ $100k** | 28-day average |
| breadth (market-wide) | **`pct_above_20` lagged 1 day > 0.5** | applied post-hoc; risk-on regime only |

**Exit** — a Qullamaggie-style trailing stop: floor = `max(min-low over prior 4 bars, entry-day
low)`. When the bar's low breaches it, **sell at the next bar's open**. No trailing limit, no
time stop, no expansion exit. Open positions at the data's end are marked-to-market at the final
close.

**Headline (filtered: breadth + 2005-start, 21.4 trading-day-years):**

| | value |
| --- | ---: |
| trips | 2,227 |
| win rate | 46.4% |
| profit factor | **1.769** |
| net P&L | +$527,594 |
| % months positive | 58.1% |
| max monthly drawdown | **−$33,772** |
| years positive | **21 / 22** |

*(Headline recomputed 2026-06-18 on the dividend-adjustment-fixed `split_adjusted_prices` — see the
data-fix note below. The change vs the pre-fix numbers was negligible: PF 1.758→1.769, DD −37.6k→
−33.8k, 26 fewer trips — the production filters mostly exclude the low-priced dividend payers that
the bug corrupted.)*

---

## Why log-space ATR / tightness (the v2 change)

The v0/v1 ATR% was the prior-14-day average true range divided by the **current** bar's close.
That denominator is wrong for a momentum entry: on a big breakout day the close jumps, which
**deflates** the ATR% — so a genuinely volatile name can slip *under* an `ATR% < 0.08` filter
purely because its trigger bar ran up. The filter was meant to reject jumpy names and was being
defeated by exactly the bars it should catch.

**The fix:** compute true range in **log space**. Each leg becomes a log-price difference (a
log-return magnitude):

```
logTR = max( log(high)−log(low),
             |log(high)−log(prevClose)|,
             |log(low) −log(prevClose)| )
```

The 14-bar average of `logTR` *is* an ATR% — intrinsically a per-bar percentage-of-price — with no
division by any close. A breakout day no longer distorts it; it measures the bar's volatility on
its own scale regardless of where the close landed. Tightness follows the same logic:
`tightness = log(maxHigh/minLow) / logATR` (no `× window` factor — ATR is already a per-bar
average and the threshold is set by sweep, so the span constant is redundant).

This moved both filters onto a new numeric scale (live ATR% ≈ 0.003–0.5, median ~0.04; live
tightness ≈ 1.4–13, median ~3.9), so every old cutoff (`0.08`, `0.30`) was meaningless and had to
be re-swept from scratch.

---

## Tuning — post-hoc sweeps on the realistic (next-open) baseline

All sweeps below run the engine with the relevant filter **off**, then carve thresholds in SQL off
the trips CSV (breadth_lag1 > 0.5, entries ≥ 2005). Because the entry filters are entry-time gates,
post-hoc cutting is exact; the exit choice (next-open) was fixed first so nothing is tuned against
an unrealistic fill.

### ATR% (log) — the single biggest lever

The high-volatility tail is the drag, exactly as the broken denominator was hiding. Cutting it
lifts PF from 1.13 → ~1.47:

| ATR% cut | trips | PF | net |
| ---: | ---: | ---: | ---: |
| none | 6922 | 1.146 | 451,908 |
| < 0.09 | 6033 | 1.470 | 868,426 |
| **< 0.11** | 6331 | **1.467** | **970,707** |
| < 0.12 | 6424 | 1.429 | 936,091 |
| < 0.15 | — | lower | — |

Flat plateau across 0.09–0.11, falling above 0.12. **0.11** sits at the top of the plateau and
maxes P&L. Clear-cut.

### Tightness (log) — monotonic; tighter = better on every axis

Unlike the old absolute formula (where the relationship was muddied), log-tightness is cleanly
monotonic. Tighter caps raise PF *and* shrink drawdown *and* lift monthly consistency — only raw
P&L falls (fewer, better trades = less exposure):

| tightness cut | trips | PF | net | % months + | worst mo | max DD |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| < 3.5 | 2342 | 1.701 | 486,592 | 59.9% | −19.0k | −32.4k |
| **< 4.0** ⭐ | 3357 | 1.636 | 627,079 | 57.2% | −23.9k | **−40.8k** |
| < 4.5 | 4187 | 1.575 | 705,646 | 59.0% | −25.5k | −43.8k |
| < 5.0 | 4836 | 1.549 | 792,685 | 59.1% | −33.2k | −63.9k |
| none | 6331 | 1.467 | 970,707 | 57.2% | −42.8k | −95.4k |

**4.0** is the drawdown/PF sweet spot — it halves the max drawdown vs the loosest sane setting
while keeping PF at 1.64 and most of the P&L. (Numbers here are at the pre-pct_up-floor stage; the
final default adds the 10% entry-move floor on top, lifting filtered PF to 1.73.)

### Entry-day move — the surprise: bigger movers are *better*

Hypothesis going in was that stocks "up too much" on the day are bad. The data says the **opposite**
— the weak band is the *modest* movers (~8–11%, PF ~1.15); the explosive ≥20% names are the
strongest (decile 9, up 22–29%, PF **2.33**). Capping the top does nothing for PF and only discards
P&L. **Raising the floor**, by contrast, improves everything:

| move floor | trips | PF | net | % months + | worst mo | max DD |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| ≥ 5% (old) | 3357 | 1.636 | 627,079 | 57.2% | −23.9k | −40.8k |
| ≥ 8% | 2683 | 1.660 | 553,963 | 56.7% | −24.8k | −38.9k |
| **≥ 10%** ⭐ | 2260 | 1.734 | 520,641 | 58.7% | −24.6k | −35.8k |
| ≥ 12% | 1897 | 1.776 | 479,246 | 58.4% | −20.8k | −28.3k |
| ≥ 15% | 1401 | 1.858 | 420,585 | 60.1% | −18.0k | −26.4k |

PF, consistency, worst month and drawdown all improve as the floor rises — a rare dial that pays on
quality *and* risk at once. **10%** is the chosen balance: PF 1.73 at meaningful trade count;
higher floors keep paying if you want fewer/stronger trades.

#### …but most of the modest-move edge is really 52w-high proximity (2026-06-18)

The entry-day move and the 52-week-high gate are correlated (a big up-day is often *what makes* a new
high), so is "up≥10% > up≥5%" really a move effect, or a proxy for proximity? Test: require a **strict
new closing high** (52w = 1.0) and drop the move floor (up≥0), then bucket by the day's move *within*
that new-high population (15-day stop, tightness<4, rvol[6,20], breadth):

| pct_up band (all at a new high) | n | PF |
| --- | ---: | ---: |
| <3% | 937 | 1.576 |
| 3–5% | 330 | 1.322 |
| 5–8% | 448 | 1.537 |
| 8–10% | 322 | 1.158 |
| 10–15% | 710 | 1.311 |
| **15–20%** | 501 | **2.085** |
| **20%+** | 729 | **1.708** |

**Verdict: partly a proxy, but not entirely.** In the 3–15% range the move size barely matters once a
new high is required (flat, choppy PF 1.16–1.58) — so much of the basic up≥10%-vs-up≥5% effect *was*
the new-high edge. But the **explosive ≥15% moves carry real, independent signal** (PF 2.0+) even
among new-high names. Move floor and 52w gate are complementary, not redundant: the floor earns its
keep specifically by isolating the big-mover tail. (And with strict new-high, raising the move floor
still lifts PF 1.58→1.83 at ≥15%, for the same reason.)

#### Distance above the 52-week high — bimodal, not "closer = better" (2026-06-18)

Bucketing by **how far the close sits above the prior-252d closing high** (`pct_52w_at_entry =
close / hi_252_prior − 1`; 52w gate OFF so it spans both sides; up≥10, tightness<4, rvol[6,20],
15-day stop, breadth):

| distance vs 52w high | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| < −15% (well below) | 2333 | 28.5% | 1.214 | +384k |
| −15..−5% | 635 | 36.1% | 1.286 | +96k |
| −5..0% (just under) | 313 | 42.8% | 1.706 | +86k |
| **0..2% (fresh new high)** | 158 | 46.2% | **1.939** | +61k |
| 2..5% (dead zone) | 266 | 38.7% | **1.184** | +22k |
| 5..10% | 495 | 37.6% | 1.333 | +75k |
| **10..20% (extended run)** | 756 | 43.7% | **2.014** | +330k |
| 20%+ | 265 | 38.9% | 1.695 | +130k |

The signal is **bimodal**, with two distinct sweet spots and a trough between them:

1. **Right at the high (0–2%): PF 1.94** — the clean fresh breakout, highest win rate (46%).
2. **Extended 10–20% above: PF 2.01** — names already in a confirmed strong run; biggest P&L
   contributor ($330k).
3. A **dead zone at 2–5% (PF 1.18)** — past the clean breakout but not yet a confirmed run.

Names *well below* the high (< −15%, the early-stage breakouts) are the weakest on PF (1.21) but carry
a lot of P&L on volume. This refines the proximity story: it is not "closer to the high is better" —
it is "at the high OR clearly extended, but not the awkward 2–5% just-past zone."

#### The move × 52w-position interaction — "bigger is better" is conditional (2026-06-18, pure gainers)

Re-run on **pure gainers** (up≥0, no move floor — the up≥10 default had hidden the small-mover cells)
the bimodal 52w shape holds (peak at the new high, dead 2–10% trough, strong 10–20% extended). But
slicing the **dead 2–10% zone by entry-day move** overturns the earlier "not rescuable by move"
read — there IS a pattern, and it **inverts** the big-mover edge:

| pct_up (within 2–10% above high) | n | PF |
| --- | ---: | ---: |
| 2–4% | 151 | **2.03** |
| 4–6% | 216 | 1.372 |
| 6–8% | 224 | 1.275 |
| 8–10% | 271 | **1.094** |
| 14–20% | 235 | 1.528 |

The **smallest movers (2–4%) are the best** in this zone (PF 2.0), declining to a trough at 8–14%.
Mechanism: a stock 2–10% above its high on a *2–4% day* got there by **grinding** (quiet, steady
higher highs) — a clean continuation. A stock only 2–10% above the high on a *10%+ day* just **gapped
up to barely clear it** — overextended intraday relative to its structure, and underperforms.

So the move×position interaction is the real story (PF, cells with n≥40, pure gainers):

| 52w zone \ move | <5% | 5–10% | 10–15% | 15–20% | 20%+ |
| --- | ---: | ---: | ---: | ---: | ---: |
| below high | 1.53 | 1.23 | 0.81 | 1.06 | 1.64 |
| at-high 0–2% | 1.34 | **2.05** | 1.23 | **3.20** | — |
| dead 2–10% | **1.74** | 1.21 | 1.10 | 1.80 | 1.20 |
| ext 10–20% | — | — | 1.83 | **2.15** | 2.00 |
| ext 20%+ | — | — | — | — | 1.70 |

**"Bigger movers are better" is conditional on position:** explosive at a *fresh* high or in a
*confirmed run* is premium (PF 2–3); the *same* big move in the dead zone is mediocre (~1.1–1.2),
where instead the quiet small-mover grinder wins. (Grid is noisy per-cell at these sample sizes —
read it for the pattern, not the decimals.)

#### Is the dead zone a resistance artifact? (max-CLOSE vs max-HIGH 52w reference)

The 52w channel is `max(close)`, but the resistance a breakout must clear is the prior **intraday
high** (`max(high)`), which sits above the max-close. So a name "+2–10% above its 52w *close* high"
may still be **at/under its prior intraday high** — banging into real overhead. Added a parallel
252d high-channel and `pct_52w_high_at_entry = close / hi_252_high − 1`. Bucketing on it (pure
gainers, breadth, 15-day stop):

| band (vs 52w INTRADAY high) | n | PF | net |
| --- | ---: | ---: | ---: |
| < −15% (well below) | 7253 | 1.307 | +1.06M |
| −15..−5% | 3618 | 1.513 | +439k |
| **−5..0% (pressing into resistance)** | 3445 | **1.646** | +371k |
| 0..2% (clears high) | 870 | 1.403 | +97k |
| 2..5% | 660 | 1.351 | +86k |
| 5..10% | 683 | 1.357 | +108k |
| **10..20% (extended)** | 568 | **1.757** | +176k |
| 20%+ | 182 | 1.473 | +65k |

**Partly confirmed, with a sharper read.** The median intraday-high distance is ~1.3% below the
close-high distance, and **5.9% of trades are a new *closing* high yet still under the prior intraday
high.** On the intraday-high reference the dead zone is milder (the close-based +2–10% trough was
partly a resistance artifact) — but the standout flips to **−5..0%, i.e. *just below* the intraday
high (PF 1.65).** The best fresh-breakout setup is price *pressing into* intraday resistance from
below, not after it clears; once it poking through (0–10% above the intraday high) it enters the mild
dead zone, and only the well-extended 10–20% names are strong again. The close-based "fresh high"
sweet spot was conflating these two; the intraday-high reference cleanly separates them.

#### Rescuing the 0–10% dead zone with CLOSE-IN-RANGE — weak signal; the real effect is "don't pin the high" (2026-06-18)

Can the 0–10%-above-intraday-high dead zone be salvaged by *how the entry bar closes*? Close-in-range
`= (close − low)/(high − low)` (1.0 = closed at the day's high, 0 = at the low; from raw
`daily_prices` — a within-bar ratio, so adjustment cancels). **PURE GAINERS (up≥0)**, production
quality filters (tight<4, ATR%<0.11, rvol[6,20]), breadth, 2005+, 2,244 trips (total PF 1.29):

| close-in-range | n | PF | | era split | 2005–14 | 2015–26 |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| 0.0–0.2 | 123 | 1.27 | | weak/mid <0.6 | 1.35 | 1.32 |
| 0.2–0.4 | 194 | 1.51 | | strong 0.6–0.95 | 1.54 | 1.24 |
| 0.4–0.6 (mid) | 328 | 1.26 | | **at-high 0.95+** | 1.23 | **0.65** |
| 0.6–0.8 | 598 | 1.34 | | | | |
| 0.8–0.95 | 676 | 1.39 | | | | |
| **0.95–1.0 (at high)** | 325 | **0.88** | | | | |

⚠️ **Close-in-range is a WEAK signal once the move floor is removed.** An earlier cut with up≥10%
showed strong-close at PF 1.84 — but that was **the move floor leaking back in**, not the close
location. On *all* gainers the strong-close bands (0.6–0.95) are only ~1.34–1.39, barely above the
weak/mid closers (1.26–1.51), and there's no clean monotonic "stronger close = better" (the 0.2–0.4
bucket is actually the best small one). The strong-close group also fades across eras (1.54 → 1.24).

**The one robust, actionable effect is the NEGATIVE one: closing at the literal high (0.95+) is bad**
— PF 0.88 overall, and clearly era-damning (1.23 → **0.65**, net-losing recently). Pinning the close
to the absolute high is an exhaustion tell (mirrors the bimodal distance-above-high "pinned = no
buyers left above" pattern). So close-in-range does NOT meaningfully rescue the dead zone — the
takeaway is to **avoid the pinned-at-the-high closes**, not to chase strong closes.

#### …but the INTRADAY RECLAIM does rescue it — open below resistance, close through it (2026-06-18)

The real dead-zone refinement: did the entry bar **open below** the 52w intraday high (resistance)
and push **up through it** to close above (a live intraday reclaim), or did it **gap over** and just
hold (already-extended, you missed the break)? Reconstruct resistance = `hi_252_high =
entry_price/(1+pct_52w_high_at_entry)` and compare the (raw) open to it. Pure gainers, dead zone, 2,244 trips:

| dead-zone entry | n | win% | PF | net | | era | 2005–14 | 2015–26 |
| --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| **reclaim (open below → close above)** | 1142 | 44.6% | **1.43** | +137k | | reclaim | **1.69** | **1.26** |
| gap-over (open at/above) | 1102 | 39.4% | 1.09 | +19k | | gap-over | 1.15 | 1.04 |

**The intraday reclaim is far better and era-robust** — PF 1.43 (1.69 → 1.26 across eras), higher win
rate, and it carries essentially ALL the dead zone's P&L ($137k of $156k). The gap-over half is the
genuinely dead part (PF 1.09, near break-even both eras). Mechanism: a name that opens below its 52w
high and reclaims it through the session is a *live* breakout you're participating in; a gap-over is
one that broke before the open — you're buying it already extended, after the fact. Unlike
close-in-range (which evaporated on pure gainers), the reclaim-vs-gap split survives the era test
cleanly. **This is the dead zone's actual rescue: require an intraday reclaim of the prior high, not
a gap over it.**

Not a stop artifact: a gap-over's entry-day-low stop sits relatively tighter (open far above the
low), so the split could be stop-geometry. Re-ran with `--no-entry-day-stop` — the edge holds:
reclaim 1.435 (vs 1.43), gap-over 1.16 (vs 1.09; the stop *was* penalizing gap-overs slightly more,
a second-order effect), reclaim still wins both eras (1.65 / 1.29 vs 1.07 / 1.16). The reclaim
advantage is genuine, not a consequence of the initial stop.

#### ⭐ The reclaim edge GENERALIZES to the whole production system (2026-06-18)

> **Resistance = the prior 252-day max of INTRADAY HIGHS** (`hi_252_high`, the `hiHigh` MaxMa over
> `bar.high`, pre-push), NOT the max of closes. So **reclaim** = entry bar's `open < hi_252_high`
> **and** `close ≥ hi_252_high` (opened below the prior intraday high, pushed up through it during the
> session); **gap-over** = `open ≥ hi_252_high` (opened already above it). The intraday high is the
> real overhead level sellers defended, so a reclaim means price broke through actual supply intraday
> — the stronger of the two possible definitions. (Distinct from the production *entry gate*
> `Min52wPct`, which tests `close ≥ 0.95 × hi_252_CLOSE` — the close-channel. Two references, each
> correct for its role.)

Splitting the **full production default** (not just the dead zone) by reclaim vs gap-over — open
below the 52w intraday high and close through it, vs open already at/above it:

| whole system | n | win% | PF | net | | era | 2005–14 | 2015–26 |
| --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| **reclaim** | 1226 | 45.8% | **1.917** | +411k | | reclaim | 1.98 | **1.89** |
| gap-over | 1001 | 47.2% | 1.49 | +116k | | gap-over | 1.76 | **1.31** |

**"Open below the prior high, close through it" is a broadly better entry than buying any close** —
PF 1.92 vs 1.49, carrying **78% of the system's P&L** ($411k of $528k), steady across eras
(1.98 → 1.89). The gap-over half is the weaker one and **decaying** (1.76 → 1.31 — buying breakouts
that gapped over the high before the open has gotten worse over time, gap-and-fade / front-run).
Note the gap-over has a slightly *higher* win rate (47.2 vs 45.8) but lower PF — its winners are
smaller: the reclaim's edge is payoff size, consistent with entering at a better price than chasing
an extended gap.

**As a system filter (reclaim-only) it dominates the default:**

| | full default | reclaim-only |
| --- | ---: | ---: |
| trips | 2,227 | 1,226 |
| PF | 1.769 | **1.917** |
| net | $528k | $411k |
| max monthly DD | −$33.8k | **−$23.6k** |
| % months + | 58.1% | 53.9% |

+0.15 PF and **−30% max drawdown** for 78% of the P&L on 55% of the trips (only cost: fewer trades →
a few more flat months). Strongest single entry refinement found this session, era-robust, with a
sound mechanism (participate in a live intraday breakout vs chase an already-gapped one). **Candidate
production change** — would need an intraday/open data feed at entry time to act on live, but the
daily `open` + reconstructed `hi_252_high` is enough to backtest it cleanly.

#### ⭐ Trailing LIMIT entries rescue the dead zone — buy the pullback, not the close (2026-06-18)

The natural follow-on to the reclaim finding: instead of buying the signal-bar **close**, rest a
**buy limit at the trailing prior-window low** (the same `MinMa` the stop trails), drag it down each
bar, and fill only on a pullback to it; if it doesn't fill within a time cap, enter at the next open
(tagged `open_after_cap` so the forced-open fills are analyzable separately). This is a real resting
order that can miss — the mirror of the retired trailing-limit *exit*. Implemented in the engine as a
`PendingLimit` lifecycle (CLI: `--entry-limit --entry-trail-window N --entry-time-cap K`); the
`*_at_entry` metrics stay fixed at the **signal** bar, `entry_date`/`entry_price`/`entry_reason`
record the actual fill. A buy limit at `lvl` fills at `min(lvl, open)` (a gap-down opening below the
limit fills at the open, no top-tick credit). The CSV now carries `signal_date`, `entry_date`,
`entry_reason` (`close` | `limit` | `open_after_cap`).

**On the WHOLE production system limit entries LOSE** (breadth-filtered, default up≥10%, tw=4/cap=5):
PF **1.758 → 1.286**, and only **22%** of signals ever pulled back to the 4-day low within 5 bars —
momentum breakouts run, they don't retrace. The 78% `open_after_cap` fills (PF 1.215) bought ~5 bars
higher into a worse base; even the 456 genuine `limit` fills were PF 1.585, *below* the at-close
baseline. So for **strong, near/at-high breakouts, buying the close is correct** — chasing a pullback
either misses or buys weakness.

**But in the DEAD ZONE the opposite is true.** Re-run on pure gainers (`--up-threshold 0`) and
isolate the 0–10%-above-intraday-high band. The dead-zone **at-close** baseline is PF **1.275 /
$149.6k** (vs 1.757 for the rest of the system). Sweeping the limit params (dead-zone PF only):

| trail / cap | n | PF | net |
| --- | ---: | ---: | ---: |
| **at-close (up=0)** | **2284** | **1.275** | **$149.6k** |
| tw=1 cap=2 | 2260 | 1.199 | $74k |
| tw=1 cap=5 | 2292 | 1.326 | $109k |
| tw=1 cap=10 | 2290 | 1.39 | $128k |
| tw=2 cap=5 | 2232 | 1.258 | $86k |
| **tw=2 cap=10** | 2234 | **1.457** | $139k |
| tw=4 cap=10 | 2123 | 1.455 | $140k |

A **long cap is essential** (cap=2 is strictly worse on both axes — it forces the open too fast); with
cap=10 the dead-zone PF climbs to ~**1.45**. Unlike strong breakouts, **94.6% of dead-zone signals DO
pull back** to the 4-day low within 10 bars (only 5.4% time out) — a 0–10%-above-resistance name with
no big-move requirement is *exactly* the choppy, retracing kind that gives you the fill. Splitting the
best variant (tw=2/cap=10) by fill type:

| dead-zone fill | n | % | PF | net |
| --- | ---: | ---: | ---: | ---: |
| **limit** | 2113 | 94.6% | **1.502** | +$143k |
| open_after_cap | 121 | 5.4% | 0.759 | −$4k |

The limit fills are **PF 1.502 on the same trades** the at-close run booked at 1.275 — a genuine
+0.23 PF from entering a few % lower on the pullback. The small `open_after_cap` tail (the runaways
that never retrace) is the only loser, and it's tiny.

**Era split — robust in the right direction (the load-bearing check):**

| dead zone (0–10% above high) | 2005–14 | 2015–26 |
| --- | ---: | ---: |
| at-close PF | 1.402 | **1.175** |
| limit (tw=2, cap=10) PF | 1.422 | **1.493** |

The dead zone's weakness was concentrated in the **modern era** (2015–26 at-close PF 1.175). The
limit entry **lifts the modern era to 1.493** while leaving the early era ~unchanged (1.402→1.422) —
it repairs the exact regime where the dead zone was broken, the opposite of a regime artifact. (Early-
era P&L drops $96k→$65k because buying the close outright was already fine pre-2015; modern-era P&L is
flat-to-up, $53k→$74k, at a far higher PF.)

**Takeaway:** limit-entry is **position-dependent, mirroring everything else in this system.** For
strong near-high breakouts, buy the close (they run). For the **0–10%-above-resistance dead zone**, a
trailing buy limit with a *generous* cap (tw≈2, cap≈10) lifts PF from 1.275 to ~1.50 — because these
names reliably retrace, and the dead zone's weakness was a chase-the-close problem. This is the second
dead-zone rescue found this session (alongside the intraday reclaim); the two are complementary
(reclaim = *which* dead-zone bars to take; limit = *how* to enter them). **Candidate refinement for
dead-zone entries specifically — not the whole system.**

**The two refinements STACK — and the limit's benefit lives entirely in the reclaim half.** Splitting
the dead-zone limit run (tw=2/cap=10) by reclaim (signal-bar `adj_open < hi_252_high`) vs gap-over,
against the at-close reference:

| dead zone | at-close PF | limit PF |
| --- | ---: | ---: |
| **reclaim** (open below high) | 1.331 | **1.603** |
| gap-over (open ≥ high) | 1.14 | 1.15 |

On **reclaim** trades the limit adds a real **+0.27 PF** (1.331→1.603) at ~flat P&L — buying the
pullback on a name that opened below resistance and reclaimed it intraday is a clean improvement. On
**gap-over** trades the limit does nothing (1.14→1.15): a name that already gapped through resistance
either doesn't retrace to the 4-day low or the retrace means the gap is failing. The gap-over half is
dead weight under *both* entry methods (~$15–22k on ~870 trades). So the best dead-zone cell is
**reclaim + limit**, and gap-overs should be dropped regardless of entry style.

**DROP the timed-out (`open_after_cap`) trades — strictly dominant.** Splitting by fill type × reclaim:

| dead-zone fill | n | PF | net |
| --- | ---: | ---: | ---: |
| limit fill — reclaim | 1284 | **1.669** | +$128.3k |
| limit fill — gap-over | 829 | 1.158 | +$14.8k |
| **open_after_cap — reclaim** | 78 | **0.687** | **−$4.3k** |
| open_after_cap — gap-over | 43 | 0.987 | −$0.1k |

Both timed-out cells are sub-1.0; the reclaim ones are *especially* bad (PF 0.687) — they are exactly
the reclaim names that **failed to retrace within 10 bars** (ran away), so the cap forces you in at the
open chasing a faded breakout. Dropping all timeouts lifts the dead-zone limit run on **both axes at
once** — PF **1.457→1.502** AND net **$138.8k→$143.1k** (a rare no-tradeoff cut, because the dropped
trades are net-negative). So the correct policy is **expire-on-timeout, not enter-at-the-open**: if
the pullback doesn't come within the cap, the signal has invalidated itself.

**Best dead-zone configuration found:** `--up-threshold 0` + 0–10%-above-intraday-high + **reclaim
only** + **trailing buy limit (tw=2, cap=10) + drop timeouts** → **PF 1.669, +$128k on 1,284 trips**,
the cleanest dead-zone cell of the session. (Engine currently enters at the open on timeout, tagged
`open_after_cap`, so "drop timeouts" is a post-hoc filter today; an `--entry-expire-on-cap` flag would
make it native.)

**The limit entry is a dead-zone-ONLY tool — it damages the rest of the universe.** Splitting the
same up=0 runs into dead zone vs everything else (below the high, fresh 0% new highs, well-extended
10–20%+):

| up=0, breadth-filtered | at-close PF | limit PF (tw=2/cap=10) |
| --- | ---: | ---: |
| **dead zone (0–10% above high)** | 1.275 | **1.457** |
| **rest of universe** | **1.757** | 1.411 |

On the rest, the limit **drops PF 1.757→1.411 and net P&L $671k→$254k** — a big loss. Win rate actually
*rises* (44%→53%) while PF *falls*: the classic "many small pullback wins, but you miss the few huge
runners that carry the system" — the biggest winners are precisely the names that never retrace. It is
not a timeout problem here (limit fills alone are 1.438, ~95% of rest-of-universe names do retrace);
buying the pullback on a strong breakout is **just worse**, because the at-close entry catches the
continuation while the pullback entry sits through a dip that, on the winners, costs the entry into
the best part of the move.

So the limit entry helps *exactly* the band where chasing the close was the problem (the dead zone)
and hurts everywhere else — the same position-dependence that runs through this whole system. **If
shipped it must be a CONDITIONAL entry rule: trailing limit in the dead zone, buy-the-close
everywhere else** — never a global default. (Aside, untested: gap-overs might instead want an
EARLY-in-the-day open entry — gaps come from premarket news, so waiting for the close may be wrong —
but that needs premarket-volume data we don't have yet to model open entries.)

#### Gap-over baseline in the intraday-data window (mid-2021+) — setting up the open-entry test (2026-06-18 PM)

To prep the open-entry idea above we pulled the **bulk 1-minute aggregate universe** (Massive
`download-bulk-minute`). The subscription only reaches **5 years back**, so the data starts
**2021-06-17** (the pre-2021-06-17 dates 404/403 as out-of-window — expected, not retriable). That
fixes the analysis window: **mid-2021 onward**. Here is the at-close baseline the open-entry test must
beat, on the near-high universe (`pct_52w_high_at_entry ≥ 0`, pure gainers `up=0`, breadth-filtered):

| mid-2021+ (near-high, at close) | n | PF | net | win% |
| --- | ---: | ---: | ---: | ---: |
| **gap-over** (signal-bar `adj_open ≥ hi_252_high`) | 277 | **1.09** | +$5.3k | 42.2 |
| reclaim (open below → close above) | 336 | 1.22 | +$24.9k | 42.6 |

Two takeaways for the open-entry work:
- **Gap-overs are barely break-even at the close (PF 1.09)** in this window — a *low bar*, which is the
  point: the thesis is that a gap's move happens premarket/at-open and fades into the close, so the
  close is the wrong entry. Lots of headroom if entering at/near the open helps; the 277 gap-over
  trips are the test set.
- **Reclaims still beat gap-overs even here (1.22 vs 1.09)** — consistent with every prior split;
  reclaim is the genuinely better near-high entry, gap-over is the suspect class the intraday data is
  meant to rescue or kill.

⚠️ **Per-year gap-over PF is pure noise** — 26–81 trades/year, PF swinging 0.66 (2025) → 2.00 (2022) →
0.66 → 2.03 (2026) with no trend. **Pool the whole mid-2021+ window; do not slice gap-overs by year.**
The decision rule for tomorrow: if an open / early-morning entry materially beats the 1.09 close
baseline on these 277, gap-overs become tradeable and it's worth re-subscribing for older data;
otherwise gap-overs stay a skip and **reclaim + dead-zone limit** remains the answer. (Minute corpus
now on disk: `data/minute_aggs/{date}.parquet`, 2021-06-17 → 2026-04-17, 1,214 daily files.)

#### Breakouts FAR below the high (< −15%) — positive but weaker, and the structure inverts (2026-06-18)

With the quality filters MET (tightness<4, ATR%<0.11, rvol≥3) but the 52w gate OFF, what happens to
breakouts in stocks **far below their 52w intraday high** (`pct_52w_high_at_entry < −15%`)? They are
**still net positive but markedly weaker** than near-high breakouts (PF ~1.1–1.45 vs 1.5–1.8), and
the move/volume structure **inverts** vs the near-high regime.

⚠️ **Penny-stock caveat (load-bearing):** without a price floor this bucket is dominated by a sub-$1
artifact — the <$1 band alone was PF **14.6 / +$26.5M** (≈75% of the bucket's nominal P&L), because
fixed-$10k notional on a $0.30→$3 name books absurd P&L you could never actually deploy. **All
numbers below apply the $5 price floor**; the far-from-high analysis is meaningless without it.

| pct_up ($5+, <−15% below high) | n | PF | | rvol | n | PF |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| <2% | 11297 | 1.365 | | 3–5 | 23453 | 1.351 |
| **2–5%** | 8415 | **1.458** | | 5–8 | 7102 | 1.127 |
| 5–10% | 7548 | 1.161 | | 8–15 | 3136 | 1.399 |
| 10–20% | 5941 | 1.105 | | 15–30 | 1143 | 1.042 |
| 20–35% | 1861 | 1.20 | | **30+** | 860 | **0.397** |
| 35%+ | 632 | 1.13 | | | | |

**The inversion:** near/at the high, explosive moves are premium; *far below* the high, the **modest
movers (2–5%) are best** (PF 1.46) and bigger moves fade to ~1.1 — a big up-day far below the high is
just a dead-cat bounce in a downtrend, no edge. By volume the edge is flat-positive 3–15 then the
**extreme 30+ rvol bucket collapses to PF 0.40 (−$551k)** — the same "huge volume spike = blow-off
loser" pattern as near the high, just at a higher rvol threshold (30+ here vs 8+ near the high).
Position relative to the 52w high genuinely changes the breakout's character.

**rvol band [5,20] does NOT help far below the high** (PF 1.228 vs ~1.27 at rvol≥3): it discards the
**rvol 3–5 band, which is the best volume bucket here** (PF 1.35) — the opposite of the near-high
production system where [6,20] is right. Within [5,20] the move profile is the same (2–5% best at
1.54; the 5–20% bands go slightly negative). The rvol band is itself position-dependent.

**By PRICE LEVEL the far-below-high edge is monotonic — lower is better** ($5+, rvol[5,20], <−15%):

| price | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| $5–10 | 3481 | 30.2% | **1.379** | +630k |
| $10–20 | 3381 | 35.1% | 1.268 | +356k |
| $20–50 | 2542 | 30.3% | 1.305 | +335k |
| $50–100 | 713 | 28.8% | **0.882** | −44k |
| $100+ | 739 | 23.3% | **0.72** | −144k |

The $5–50 range carries the whole edge (PF 1.27–1.38); **>$50 flips negative** (a $100+ large-cap 15%
off its high doing a one-day volume breakout is mostly noise that fades, vs a $5–20 name where an
oversold-bounce/turnaround can run hard in % terms). So the far-below regime's profile is the
**opposite of near-high production**: lower price ($5–50), modest move (2–5%), lower-to-moderate
volume (rvol 3–5) — vs near-high's higher-priced, big-move, high-volume winners.

### Loose-base breakouts are a strong negative edge — and LINEAR tightness separates the tail better

Sanity check (2026-06-18): does buying breakouts from *loose* bases (high tightness) lose money, as
the old `momentum_v0` study found? **Yes — strongly, and v2 reproduces the old table trade-for-trade
once the gates are matched.** v2's linear `Tightness` = `range / ATR` (drops v0's `÷14`), so
v0_tier × 14 ≈ v2 scale.

| tier (old scale) | v2 n | v2 PF | old n | old PF |
| --- | ---: | ---: | ---: | ---: |
| <0.40 (tight) | 30,048 | **1.218** | 30,051 | 1.22 |
| 0.40–0.55 | 5,662 | 0.904 | 5,659 | 0.98 |
| 0.55–0.70 | 1,241 | **0.589** | 1,242 | 0.65 |
| 0.70–0.85 | 283 | **0.304** | 286 | 0.41 |
| **0.85+ (trend)** | 74 | **0.252** | 71 | **0.21** |

N matches to a handful of trades per tier; the loose tail collapses to PF ~0.25 (matching v0's 0.21).
**Loose-base breakouts are a confirmed money-loser, and the v2 engine is faithful to v0.**

**What had to match to reproduce it** (my first attempt diverged ~3× on N — these were the culprits):

1. **rvol ≥ 3.0** — the v0 study's floor. (v2 production uses the [6,20] band; a *fully*-unbanded
   run floods in low-rvol breakouts and ~triples N.)
2. **52-week proximity = 1.0** — v0 required a *strict new closing high*; v2's default is 0.95
   (within 5%), which is far more permissive. This is the biggest N lever.
3. **No breadth filter** — the v0 tightness study was raw (no `breadth_lag1 > 0.5`). Applying
   breadth drops N ~30% and is NOT how the old table was built.

Exact reproduction command (linear tightness, 15-day stop, old gates, no breadth, 2005+ post-hoc):

```bash
dotnet run --project TradingEdge.MomentumV2 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 \
  --stop-low-window 15 --tightness-mode linear \
  --min-price 0 --rvol-min 3 --rvol-max 999999 --min-52w-pct 1.0 \
  --up-threshold 0.05 --max-tightness 999999 --max-atr-pct 999999 \
  -o /tmp/v2_oldgates.csv
# then tier on tightness_14_at_entry with entry_date >= 2005-01-01 and NO breadth join.
```

**Linear vs log matters for the tail.** This collapse only shows up in LINEAR tightness. In LOG
space the same trend tier reads PF ~0.97 — log *compresses* the extreme blow-out region where the
worst losers live, masking the tail. Log and linear are functionally identical as an entry filter
*at the `<4.0` cutoff* (where both agree) but **diverge in the loose tail; linear is the sharper
discriminator there.** (The earlier "ATR%-denominator artifact" theory for the tail was wrong — the
v0 study predates the ATR filter and used a plain linear ATR.)

**ADOPTED (2026-06-18): linear tightness is now the v2 default** (`TightnessMode = Linear`). The
`< 4.0` cutoff carries over essentially unchanged — linear `< 4.0` = PF 1.758 / 2,253 trips vs the
old log default's 1.734 / 2,260, a marginal improvement with the bonus of the cleaner loose-tail
cut. Log mode stays reachable via `--tightness-mode log` for comparison only. The yearly/monthly
detail tables below were generated on the *log* default and differ by ~7 trades; the headline above
is the current (linear) default.

#### The loose-base negative edge is conditional on a VOLUME / MOVE spike (2026-06-18)

The loose-base loss isn't unconditional — it's overextension **combined with a same-day volume or
move spike**. Take *all* loose-base new-high breakouts (tightness > 7.5, new 52w close-high, every
other filter OFF — no rvol/price/ATR/move gates, ADV≥$100k liquidity floor only; 15-day stop, **no
entry-day stop**, breadth, 2005+) and break down by rvol and by the day's move:

| rvol at entry | n | PF | net | | pct_up | n | win% | PF | net |
| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: |
| <1 | 11212 | 1.157 | +780k | | <2% | 14718 | 50.0% | **1.342** | +1.62M |
| 1–2 | 6608 | 1.164 | +548k | | 2–5% | 4461 | 39.4% | 1.004 | +11k |
| 2–3 | 2196 | 1.007 | +10k | | 5–10% | 1933 | 39.1% | 1.071 | +128k |
| 3–5 | 1432 | 0.857 | −176k | | 10–20% | 980 | 33.6% | 0.808 | −272k |
| 5–8 | 619 | 0.898 | −66k | | 20–35% | 331 | 26.6% | 0.75 | −199k |
| 8–15 | 410 | **0.606** | −242k | | **35%+** | 343 | **19.2%** | **0.419** | −695k |
| 15+ | 289 | **0.51** | −260k | | | | | | |

**Both rvol and the daily move are monotonic killers, and the move is even cleaner.** A loose-base
name drifting to a new high on quiet volume / a small move (<2% day, PF 1.34, 50% win) is **fine**;
the *same* loose base ripping +35% on huge volume (rvol 15+, PF ~0.5) is the disaster — 19% win
rate, −$695k. This is the exhaustion signature in its purest form: a loose base is only dangerous
when it's *also* a climax spike that day. (The high-rvol/high-move negative edge holds with the
entry-day stop dropped too, so it is a real signal, not a stop artifact — though dropping the
entry-day stop did lift the whole population's PF 1.028 → 1.079, since the wide-range loose breakouts
trip the tight initial stop a lot.)

**Implication for exhaustion exits:** the v0 exhaustion exit fired on overextension alone, with no
volume/move condition — so it was a blunt instrument. Adding an rvol/move condition to the exhaustion
trigger should make it far more targeted (fade only the spike-blow-offs, not quiet drifters). This is
the next thing to test on the exit side. *(Reproduction: the 998k-trip `--no-entry-day-stop` run
above, tiered on `tightness_14_at_entry > 7.5` with `rvol_at_entry` / `pct_up_at_entry`.)*

##### …and the conditional exhaustion exit was BUILT and TESTED — it does not help (2026-06-19)

Implemented exactly that targeted trigger (`ExhaustionConfig`, CLI `--exhaustion-exit`): on each HELD
bar, exit at next open when **tightness > 7.5 AND ((rvol > 3 AND move > 5%) OR move > 10%)** — the
move-primary / rvol-sharpener rule the study above prescribed. Tested on three bases (loosened set,
up≥5% rvol≥3):

| base | exhaustion OFF | exhaustion ON |
| --- | ---: | ---: |
| window-low(4) | 1.352 | 1.332 |
| BE 10% + 20d | 1.40 | 1.393 |
| ATR k=4 | 1.421 | 1.412 |

**It slightly HURTS on every base.** The diagnosis (exit-reason mix on the BE+20d run) is decisive and
explains why: the exhaustion exit fired only **194 times (1.6%)**, on trades that were **93% winners,
median +41% return (best +2368%), median hold 3 bars**. I.e. on a *held* position a "loose-base
blow-off bar" only occurs **after the name has already exploded up** — so the trigger sells the
right-tail rockets a momentum system lives on, banking +41% where letting them ride to the BE/time-stop
earned more. **The asymmetry is the lesson:** the loose-base negative edge is about *entering* a base
that is *already* blown-off; once you're holding, a blow-off bar means *you are winning big*. The exact
same signal flips sign between entry-filter and held-exit *when applied unconditionally*. (This first
pass joined the position-relative expansion exit, fixed targets, and the gap-over time-stop as
upside-capping exits — but see the gain-gated refinement below, which rescues it to flat-positive.)
The signal's primary use remains an **entry** filter (which production already applies via
`MaxTightness`/rvol/move gates); the held-exit version works only when gated by extension (next).

###### Resolving the paradox: the loose-base ENTRY really is −EV, the held-exit just never sees those names (2026-06-19)

The objection was reasonable: if entering a loose-base spike is strongly −EV, exiting a held position on
the same condition "should" help. So we tested the entry directly — admit loose bases (tightness & ATR
gates off, 52w-high kept, up≥5% rvol≥3, window-low stop, long, breadth, 2005+) and tier:

| loose-base ENTRY | n | win% | PF | net | avg ret |
| --- | ---: | ---: | ---: | ---: | ---: |
| all (loose-admitting) | 25477 | 40.0 | 1.052 | +$552k | +0.2% |
| **loose (tight>7.5)** | 1121 | 29.6 | **0.536** | −$571k | **−5.1%** |
| loose + rvol>3 + up≥5% | 1121 | 29.6 | 0.536 | −$571k | −5.1% |
| **loose + up≥10% (rule B)** | 710 | 26.2 | **0.419** | −$603k | **−8.5%** |

**The premise is confirmed: buying the loose-base spike is a strong −EV pattern (PF 0.54 / −5%; the
up≥10% subset PF 0.42 / −8.5%).** (Rows 2 and 3 are identical — every tight>7.5 name in this run
already had rvol>3 & up≥5%, since the run required them to enter.) **So why doesn't the held-exit on
the same condition help?** Because they are **disjoint populations.** The −EV entry names are loose *at
the moment of entry* — you bought the top of someone else's run. A *held* position that trips the same
condition was entered from a TIGHT base (production entry) and only *became* loose later — i.e. it
already ran in your favor. Bucketing the 194 held-exit triggers by their gain when the blow-off fired:
only **13 were at a loss**; 181 were winners (avg +6% / +18% / +56% / +500% across the 0-10 / 10-30 /
30-100 / 100%+ buckets). The held-exit mostly fires on names that already paid you.

**…but the EXTENSION at the trigger is the discriminator, and gating on it salvages the exit
(2026-06-19).** Measuring the **20-day-forward return FROM the exhaustion-exit price** settles which
way it cuts — and it depends entirely on how extended the position is:

| post-exhaustion, by gain-at-trigger | n | avg fwd-20d | % up |
| --- | ---: | ---: | ---: |
| near entry (<5% gain) | 27 | **−4.58%** | 63% |
| 5–30% gain | 111 | −0.10% | 48% |
| >30% gain | 54 | **+11.91%** | 52% |

A blow-off **near entry reverts** (−4.6% fwd, the toppy chase — the same −EV pattern as the loose-base
entry); the same blow-off **after a 30%+ run keeps going** (+11.9%). The unconditional exit lumps both
and the rocket group dominates → net wash-to-drag. **Gating the exit on gain-from-entry** (new
`ExhaustionConfig.MaxGain`, CLI `--exhaustion-max-gain`) — fire only while not-yet-extended — flips it
from a drag to flat-positive on the BE 10% + 20d base:

| BE 10% + 20d | PF | net |
| --- | ---: | ---: |
| exhaustion OFF | 1.400 | $1.889M |
| + exhaustion, no gain cap | 1.393 | $1.845M |
| + exhaustion, gain cap 5% | **1.405** | $1.906M |
| + exhaustion, gain cap 10% | 1.404 | $1.897M |
| + exhaustion, gain cap 20% | 1.405 | $1.900M |

**Corrected conclusion** (supersedes the earlier "cannot be repurposed as a held exit"): the
exhaustion signal *can* be a held exit, but **only gated by extension** — it must fire on the
not-yet-extended subset (the part that overlaps the −EV entry), never on the rockets. With the cap it
beats the no-exit base on both PF and P&L. **But the effect is tiny and NOT era-robust** (pooled
+0.005 PF; era split 2005-14 1.489→1.480 *down*, 2015-26 1.340→1.351 *up* — a near-wash both ways),
because the near-entry blow-offs only revert ~−4.6% over 20d and there are few of them. So: the
held-exit is real and correctly-signed once gain-gated, but it is **not a meaningful PF lever** — the
edge in this signal still lives overwhelmingly on the **entry** side (buy the tight base, refuse the
loose one; production already does this via `MaxTightness`/rvol/move). `--exhaustion-max-gain` kept for
completeness.

###### Pushing the criteria HARD (tight>8, move>20% / move>10%+rvol) on the fixed-20% base — still no PF gain, and now we know WHY (2026-06-19)

Sharpened the trigger as far as the thesis suggests — tightness > 8, rule B move > 20%, rule A move >
10% with rvol > 3 — on the **fixed-20% ratchet** base (loosened set), the system that rides winners
hardest. Result: **still a wash.** No-cap 1.460 vs base 1.464; gain-capped 1.465 (cap 5/10/30% all
+0.001). The unconditional sharp exit lifts **win% 40.9→41.0** while **PF 1.464→1.460 and P&L
$5.14M→$5.07M** fall — the tell-tale mean-vs-median signature. The forward-20d-from-trigger study
explains it definitively, split by gain-at-trigger:

| sharp trigger, fwd-20d | n | avg | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% gain | 5 | −20.1% | −10.3% | 40% |
| 5–30% gain | 30 | −2.4% | −4.3% | 37% |
| 30–60% gain | 21 | +0.6% | +3.1% | 62% |
| 60–150% gain | 18 | **+13.1%** | **−10.6%** | 44% |
| 150%+ gain | 6 | **+4.9%** | **−5.2%** | 33% |

**The user's claim — "PF goes negative even for significant winners" — is half-right, and the half
that's wrong is decisive.** For the big winners (60%+ gain) the *median* forward return DOES go
negative (−10.6%, −5.2%) and most decline (33–44% up) — the typical extended blow-off reverts, exactly
as hypothesized. **But the *mean* stays positive** (+13.1%, +4.9%) because a few monster continuations
dominate the average. **PF and P&L are mean-driven, not median-driven** — so exiting the big-winner
blow-offs improves the median trade and the win rate but *loses* on the mean, because momentum's whole
edge is the fat right tail. There is no tightness/move/rvol/gain configuration that escapes this: the
near-entry triggers are genuinely −EV but too few (5–35 trades) to move a 12k-trade book, and the
extended triggers can't be cut without sacrificing the tail. **Definitive close of the exhaustion-exit
line: it is structurally incapable of raising PF on this system — not a tuning problem.** The signal is
an entry filter; on the exit side, manage downside + holding period only and let the tail run.

###### The condition is STRONGLY −EV universe-wide — it's an ENTRY signal we're already screening (2026-06-19)

The held-exit only ever saw ~80 of these bars. How many are there in the *entire tradeable universe*,
and what's their forward return? Scanned every (ticker, day) bar 2005+ (CS/ADRC, price≥$5, ADV≥$100k,
breadth lag1>0.5), reconstructing the same per-bar tightness / rvol / pct_up, and measured the 20-day
forward return on bars meeting **tightness>8 AND ((rvol>3 AND move>10%) OR move>20%)**:

| universe exhaustion bars | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| all (ADV≥$100k) | 1127 | **−14.5%** | −19.1% | 28.3% |
| ADV≥$1M, $5–10 | 230 | −15.7% | −21.3% | 23.9% |
| ADV≥$1M, $10–20 | 210 | −14.7% | −18.8% | 30.0% |
| ADV≥$1M, $20–50 | 206 | −13.3% | −13.7% | 31.1% |
| ADV≥$1M, $50+ | 270 | −21.1% | −29.5% | 22.2% |

**Universe-wide this is a powerful, robust negative-EV signal** — −14.5% avg / −19% median over 20
days, only 28% higher, and negative across *every* price bucket (no penny-stock artifact; $50+ is the
worst at −21%). So the user's instinct that the pattern is toxic is **completely correct** — on the
*mean*, not just the median, when measured over the whole universe.

**Reconciliation with the failed held-exit (the key insight):** the ~1,127 toxic bars and our ~80
held-exit triggers **barely overlap.** The toxic universe bars are overwhelmingly names that were
loose/extended for a long time — exactly the names the **tight entry gate already refuses to buy.** The
handful that surface on a *held* position are a biased subsample: names that passed a tight entry, ran
up into us, then spiked — skewed toward already-big-winners whose *mean* forward return stays positive
on the fat tail (≠ the −14.5% universe mean). So there's no contradiction: **the condition is toxic in
the wild, production already screens ~all of it on entry, and the residue reaching a held position is
the non-representative right tail.** The actionable edge is on the **entry side** (a hard avoid — which
`MaxTightness`/rvol/move largely encode — or, untested, a candidate SHORT entry: −19% median / 28% up
on 1,127 liquid occurrences is a far larger, cleaner signal than the held-exit could ever capture).

###### ATR% at the exit bar IS a clean discriminator — but the held sample is still too small (2026-06-19)

Can ATR% at the moment of the blow-off separate the held-exit winners from losers? Reconstructed the
log-ATR% at each of the ~80 exhaustion-exit bars and crossed it with the 20-day-forward return:

| ATR% at exit | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% | 12 | +5.8% | +2.3% | 67% |
| 5–8% | 16 | +7.8% | +3.6% | 56% |
| 8–12% | 26 | +8.2% | −4.6% | 42% |
| **12%+** | 26 | **−11.6%** | **−25.9%** | **31%** |

**ATR% is the cleanest discriminator found** — the >12%-ATR% bucket craters on *both* mean and median
(−11.6% / −25.9%, 31% up) while low-ATR% blow-offs keep grinding up (+6–8%). And it is **orthogonal to
gain-from-entry** (the 26 high-ATR% losers split 16 low-gain / 10 high-gain), so it catches reverting
big-winners the gain-cap can't. Added `ExhaustionConfig.MinAtrPct` (CLI `--exhaustion-min-atr-pct`):
firing the exit only when ATR% exceeds the floor removes the drag and finally edges past the base on
*both* PF and P&L, **in both eras** (the only gate that does so without hurting either):

| fixed 20% base | PF | net | 2005–14 PF | 2015–26 PF |
| --- | ---: | ---: | ---: | ---: |
| exhaustion OFF | 1.464 | $5.136M | 1.536 | 1.400 |
| + sharp exh, no ATR gate | 1.460 | $5.071M | — | — |
| + sharp exh, ATR%>0.15 | **1.465** | **$5.150M** | **1.538** | **1.402** |

**But it is still only +0.001 PF / +$14k on $5.1M** — a rounding-error gain. ATR% *correctly* isolates
the cratering subset (genuinely useful), but on the **held** population there are simply too few of
these bars (the entry gate already excludes nearly all of them — see the 1,127-bar universe scan
above). **So the conclusion stands and is now triple-confirmed (gain-cap, ATR%-floor, universe scan):
the exhaustion signal cannot be a meaningful HELD-EXIT PF lever; its payoff is on the ENTRY side.** The
ATR%-discriminator finding is most valuable *there* — a candidate sharper entry-avoid / short-entry
filter (high ATR% + loose + spike = the −26%-median cohort). `--exhaustion-min-atr-pct` retained.

###### ⭐ ATR% sorts the UNIVERSE exhaustion signal monotonically — a real short/avoid edge (2026-06-19)

Applying the ATR% breakdown to the **entire universe** of exhaustion bars (the 1,127), not just the
held exits, gives the cleanest gradient of the session — fwd-20d falls monotonically with ATR% on every
metric:

| ATR% at bar | n | avg fwd-20d | median | % up |
| --- | ---: | ---: | ---: | ---: |
| <5% | 115 | +2.7% | +0.2% | 52% |
| 5–8% | 112 | −1.7% | −3.7% | 39% |
| 8–12% | 171 | −6.4% | −11.6% | 33% |
| 12–18% | 218 | −10.7% | −19.5% | 29% |
| **18%+** | 511 | **−25.6%** | **−34.1%** | **18%** |

**ATR% is an independent monotonic sort within the exhaustion signal** (the <5% bucket is even mildly
*positive* — so it isn't a proxy for the condition itself). The toxic tail is large and clean: **511
bars at ATR%>18% → −25.6% avg / −34% median / only 18% higher over 20 days.** Era-robust on the high-
ATR% (≥12%) cohort: 2005–14 −18% median / 27% up (n=63), 2015–26 −30% median / 21% up (n=666) — both
strongly negative (modern era has far more high-ATR small/mid-caps and is worse, but the early era
confirms it). **This is the genuine edge the whole exhaustion thread was circling, and it is an ENTRY
signal:** high ATR% + loose base (tight>8) + spike (move>20%, or move>10% with rvol>3) is (a) a hard
long-AVOID, and (b) a candidate **short entry** — ~82% lower in 20 days, −34% median, large era-spanning
sample. Categorically different from the held-exit (which only ever saw ~80 of these, biased to the
big-winner tail). **Next: test it as a standalone short book** (`--side short` + these gates as an
entry filter — needs a `MinTightness` entry floor + ATR% floor, currently only present as exit knobs).
*(Decision 2026-06-19: shorting these is NOT being pursued — overnight borrow costs are significant for
these names and spike risk is severe; they'd have to be day-traded. Stay long-only; use this only to
sharpen the long exit/avoid.)*

###### Isolating the exact "holding is −EV" condition: loose base AND high ATR% (2026-06-19)

Rather than tune exit thresholds blind, scan the cost of *holding*: for every qualifying bar (>5%
move, near-high liquid universe, 2005+) take the 20-day-forward return — the EV of holding past that
bar — across the tightness × ATR% grid. The **mean** (the EV that drives PF):

| mean fwd-20d | ATR<8% | ATR 8–12% | ATR>12% |
| --- | ---: | ---: | ---: |
| tight ≤4 | +0.65% | +2.81% | +4.76% |
| 4–8 | +0.48% | +1.05% | +0.51% |
| **loose >8** | +1.37% | **−1.67%** | **−6.93%** |

**The negative-EV-to-hold region is sharply and uniquely the loose+high-ATR corner.** Both conditions
are necessary: a loose base on *low* ATR holds fine (+1.37%), and high ATR on a *tight* base is a
healthy breakout (+4.76%) — only the **conjunction** is toxic. (This also explains every failed exit:
the *median* is negative almost everywhere, but the *mean* is positive except here — exits chasing the
median fought the tail; this is the one cell where the mean itself is negative.) Finer ATR within the
loose bucket: the mean crosses zero at **ATR ≈ 6%** and is robustly negative on mean+median at **ATR ≥
~16%** (16–22%: −8.7% / −10.5%; 22%+: −11.9% / −16.4%; the lone 12–16% cell pops +8.9% mean on a
fat-tail outlier, median still −5.7%).

**So the condition is: `tightness > 8 AND ATR% ≳ 16%` (volatility blown up AND base gone loose) — the
user's exact framing, now pinned to numbers.** But on the LONG book the payoff is still negligible
(fixed-20% base 1.464 → best 1.465 at atr>16%/move>20%): the negative-EV region holds ~2,000 bars over
20 years of the *universe*, yet our long book — gated to TIGHT entries — almost never holds a position
into that state, and when it does it's the biased winner-tail. **Final word on exhaustion exits: the
−EV-to-hold condition is now exactly characterized (loose + high-ATR), the exit no longer hurts, but it
cannot add meaningful long PF because the entry gate keeps us out of that state-space. It is an
entry/avoid signal.** Production already avoids it via `MaxTightness 4.0` (well below the 8 threshold)
and `MaxAtrPct 0.11` — i.e. the long system structurally never enters the loose+high-ATR cell, which is
why holding-EV there is moot for it. The exhaustion-exit knobs are retained as the characterized
negative-result substrate.

###### NO-STOP system: exhaustion finally EARNS its keep — but a pure-ATR exit destroys the edge (2026-06-19)

The reason exhaustion never helped the stopped systems is that the **stop already exits most blow-offs
before exhaustion can fire** (they overlap). So strip the stop entirely (`StopMode.NoStop`, CLI
`--no-stop`; equivalently `--fixed-stop 1` → stop at 0, never reached — confirmed identical) and let
exhaustion be the *sole* exit. (Caveat: no-stop is not a usable strategy — these are 20-year holds at
fixed $10k notional, PF ~5.5 is the degenerate buy-and-hold-survivors scale. Only the *relative* effect
matters.) Loosened set:

| no-stop (loosened) | win% | PF | net | avg hold |
| --- | ---: | ---: | ---: | ---: |
| no exit (MTM only) | 53.6 | 5.553 | $146.1M | 1881 |
| **+ exhaustion (t8, atr16, mv5+rv3 / mv20)** | 53.5 | **5.642** | $147.0M | 1833 |
| + exhaustion (t8, atr12, mv5+rv3 / mv10) | 53.1 | 5.511 | $142.0M | 1766 |

**With no stop underneath, the SHARP exhaustion exit (loose + ATR>16 + spike) beats pure hold on both
PF and P&L** (5.642 vs 5.553) while exiting ~48 bars earlier — the first time exhaustion *adds* value,
because it's now the only thing catching the toxic loose+high-ATR bars. The looser ATR-12 version
hurts (5.511). Confirms the whole thread: exhaustion's value is real but was masked by the stop.

**But a PURE ATR%-threshold exit (hold until ATR% > x, no tightness/move condition) is a BAD exit** —
it monotonically destroys the edge:

| no-stop, exit when ATR% > x | win% | PF | net | avg hold |
| --- | ---: | ---: | ---: | ---: |
| 0.08 | 44.8 | 1.567 | $12.7M | 515 |
| 0.12 | 44.8 | 2.738 | $55.9M | 1027 |
| 0.16 | 48.7 | 4.221 | $109.7M | 1442 |
| 0.20 | 51.3 | 5.031 | $134.3M | 1649 |
| 0.30 | 53.3 | 5.577 | $144.5M | 1817 |
| (no exit) | 53.6 | 5.553 | $146.1M | 1881 |

The tighter the ATR threshold, the worse — exiting on ATR%>8% (PF 1.57) guts the system; it only
converges back to pure-hold near ATR%>0.30 (where it barely exits). **High ATR% ALONE is not toxic — a
volatile momentum name is exactly what you want to hold.** The toxicity requires the **conjunction**
with a loose base: the sharp exhaustion exit adds value *because* it demands both, while the pure-ATR
exit subtracts value because ATR% by itself just means "lively name." This is the cleanest proof yet
that the −EV signal is *loose AND high-ATR*, not either alone — and that a naive volatility-stop is a
mistake on a momentum book.

**rvol vs move: only moderately correlated, and the MOVE dominates.** Within the loose-base
population, rvol and pct_up have a **Spearman rank correlation of 0.38** (raw Pearson 0.04 is
outlier-scrambled and misleading; log–log 0.24). So they share information but are far from
redundant. The 2×2 shows the move is the primary signal and rvol a secondary confirmer:

| (loose base) | rvol < 5 | rvol ≥ 5 |
| --- | ---: | ---: |
| **move < 10%** | PF 1.18 (n=20392) | PF 1.35 (n=720) |
| **move ≥ 10%** | **0.76** (n=1056) | **0.51** (n=598) |

A big move kills the edge regardless of volume (0.76 / 0.51); small-move names are fine either way —
and *high rvol on a small move is the best cell* (1.35; high volume on a quiet day is just liquidity).
But both together is the worst (0.51): rvol adds incremental damage *conditional on* a big move
(0.76 → 0.51). So an exhaustion trigger should make the **daily move the primary condition with rvol
as a sharpener**, not weight them equally.

#### The inverse — buying loose-base capitulation at new 52w LOWS does NOT work (2026-06-18)

Is there a long-side mirror: buy the *down*-blowoff (loose base making a new 52w low) the way we
*avoid* the up-blowoff? Extended the engine with 252d **low** channels (min-close and min-intraday-low,
mirroring the high channels) and emitted `pct_52w_low_close_at_entry` / `pct_52w_low_at_entry`. Took
loose-base (tightness>7.5) names at/below the prior 252d closing low, **down days admitted**
(`--up-threshold -1`), rvol≥3, $5 floor, long-only, 15-day stop:

**Total: 1,797 trips, PF 0.833, −$154k — a net loser.** Breakdown:

| pct_up (capitulation severity) | n | PF | | rvol | n | PF |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| < −15% | 305 | **0.69** | | 3–5 | 1195 | 0.92 |
| −15..−8% | 324 | 0.91 | | 5–8 | 351 | 0.97 |
| −8..−4% | 412 | 0.78 | | 8–15 | 152 | 0.74 |
| −4..0% | 708 | 1.02 | | **15+** | 99 | **0.31** |

**Capitulation is NOT the clean mirror of exhaustion.** Fading euphoria works (avoid loose-base
up-blowoffs), but *buying* loose-base down-blowoffs loses — the deeper the down-day the worse
(< −15% → PF 0.69), only the mild −4..0% drift is ~breakeven, and falling knives keep falling on a
15-day hold. Breadth doesn't rescue it; it's actually **worse in risk-on regimes** (PF 0.65 vs 0.93
risk-off) — a fresh 52w low while the market is healthy is strong relative weakness, a broken laggard.

**Unifying insight:** the high-rvol cell is the worst in *both* directions (up-blowoff rvol 8+ → ~0.5;
down-blowoff rvol 15+ → 0.31). It is not "highs good / lows bad" — it is that **a violent,
high-participation move on an unstructured (loose) base is toxic to be long of, up OR down.** For the
up case you avoid/fade it; for the down case you simply don't buy it. There is no symmetric "buy the
panic" long edge here.

#### …so SHORT them instead? Only the high-volume capitulation (2026-06-18)

If buying the new-low loses, does *shorting* it win (trail the stop along the prior-window HIGH,
cover on a break up — the mirror system)? Added a `Side = Long | Short` mode (`--side short`): Short
trails the 15-day high and flips the P&L sign. On the same loose-base new-low population:

**SHORT total: 1,797 trips, PF 0.914, −$115k — also a loser.** So it's *not* a simple stop-trip
artifact (if it were, the short would profit). Both sides losing means these names **chop / mean-
revert** — you get stopped out long on the bounces AND stopped out short on them. A no-trend regime.

BUT the short edge concentrates exactly where the long was worst — **high volume** (2×2, loose-base
new-low, short):

| SHORT | rvol < 8 | rvol ≥ 8 |
| --- | ---: | ---: |
| big down (≤ −8%) | 0.67 (n=484) | **1.47** (n=145) |
| mild (> −8%) | 0.99 (n=1062) | **1.51** (n=106) |

**Shorting works only on a volume spike (rvol ≥ 8): PF ~1.5 in both move rows** (~250 trades, ~48%
win) — and loses/chops on normal volume; the big-down/low-volume cell is the worst (0.67, those
bounce and stop you out). It's **volume-gated, not move-gated**, and a modest low-frequency setup.
⚠️ **This full-sample short edge is a post-2015 REGIME artifact** — it lost badly pre-2015 (it's the
short half of a regime that flipped). See the regime-switching subsection below before trusting it.

**The symmetry (rvol is the key, both sides):** high-volume blow-offs *continue* — short the
down ones (PF ~1.5), fade/avoid the up ones (long PF ~0.5) — while **low-volume drifts mean-revert /
chop** and have no directional edge either way. The genuine inverse-exhaustion short edge exists but
is narrow: loose base + new 52w low + **rvol ≥ 8**.

**Deep dive: the rvol 3–5 segment by move size (2026-06-18, fixed prices).** The big losing bucket
(rvol 3–5, PF 0.74, 1,184 trades) split by the down-day magnitude looks U-shaped — profitable at the
extremes, deeply negative in the middle:

| pct_up (rvol 3–5 short) | n | PF | | era split | 2005–14 | 2015–26 |
| --- | ---: | ---: | --- | --- | ---: | ---: |
| < −20% (crash) | 63 | 1.36 | | crash < −12% | 0.23 | 0.79 |
| **−20..−12%** | 111 | **0.31** | | mid −12..−1% | 0.42 | 1.06 |
| −12..−4% | 442 | ~0.72 | | **flat −1..0%** | **1.25** | **1.51** |
| −4..−1% | 390 | 0.80 | | | | |
| **−1..0% (flat)** | 167 | **1.46** | | | | |

⚠️ **But the era split kills most of the U-shape.** Only the **flat −1..0% quiet-drift-to-a-new-low
is robust** (PF 1.25 then 1.51 — stable across both halves): a name barely setting a new low on
moderate volume has no bounce energy and bleeds lower. The "crash < −20% short works" cell is an
**era artifact** (PF 0.23 in 2005–14, only positive recently — don't trust it). The moderate-down
middle (−20..−12% = PF 0.31, spread across many names not one outlier) is reliably bad in both eras —
moderate down-days at a new low snap back violently. The whole rvol-3-5 segment was much worse pre-2015
(mid-band 0.42 vs 1.06), so its negativity is partly an old-era effect. **Takeaway: within this
segment the one trustworthy short is the near-flat quiet new low; the rest is mean-reversion or noise.**

#### ⚠️ The whole new-low setup is REGIME-SWITCHING — long↔short flipped at ~2015 (2026-06-18)

Era-splitting the loose-base new-low population by rvol exposes that this setup has **no stable
directional edge** — the two sides are near-perfect mirror images that swapped regimes mid-decade:

| rvol | LONG 2005–14 | LONG 2015–26 | SHORT 2005–14 | SHORT 2015–26 |
| --- | ---: | ---: | ---: | ---: |
| 3–5 | **1.31** | 0.70 | 0.43 | 0.94 |
| 5–8 | 1.15 | 0.92 | 0.59 | 1.20 |
| 8–15 | 0.92 | 0.81 | 0.69 | 1.33 |
| 15+ | **2.43** | 0.12 | 0.23 | **2.23** |

- **2005–2014 = mean-reversion regime:** new lows BOUNCED. Buying them worked (rvol 3–5 PF 1.31,
  rvol 15+ PF 2.43); shorting them lost (every band < 1).
- **2015–2026 = continuation regime:** new lows kept FALLING. Shorting worked (rvol 15+ PF 2.23);
  buying got crushed (rvol 15+ PF 0.12).

So the earlier "**high-rvol capitulation short works (PF 1.5–1.7)**" conclusion was a **single-regime
(post-2015) artifact** — full-sample PF hid that it lost badly pre-2015. Likewise the long
mean-reversion edge exists but **only in 2005–14**, and inverted after. Neither side is tradeable
forward on its own: the new-low setup is a regime bet, not a structural edge. (Lesson reinforced:
era-split any "edge" found by pooling 20 years — full-sample PF can be the average of two opposite
regimes.)

#### Shorting TIGHT-consolidation breakdowns to new lows — does NOT work (2026-06-18)

The above was all *loose*-base. Mirror thesis: a **tight** consolidation (tightness<4, ATR%<0.11 —
the production quality filters) breaking DOWN to a new 52w low on volume should be a clean
distribution short, the mirror of the winning long breakout. **It isn't.** Short, quality filters ON,
new 52w close-low, down days, rvol≥3, $5, 15-day stop — every rvol×era cell is < 1.0:

| rvol | SHORT 2005–14 | SHORT 2015–26 |
| --- | ---: | ---: |
| 3–5 | 0.97 | 0.71 |
| 5–8 | 0.50 | 0.86 |
| 8–15 | 0.98 | 0.65 |
| 15+ | 0.60 | 0.96 |

Total PF 0.81; **no volume band wins in either decade** (and unlike the loose-base setup it isn't
even regime-saved — it loses both halves). Tight bases breaking to new lows **bounce, they don't
cascade** (win rates 30–40% but net-losing — shorting into support that holds / failed breakdowns).

**Tightness is a LONG-ONLY signal — the asymmetry is the point:**

| base | breakout UP (long) | breakdown DOWN (short) |
| --- | ---: | ---: |
| **tight** | PF ~1.77 (the production edge) | **0.81 (loses both eras)** |
| **loose** | PF ~0.4 (avoid) | regime-switching (no stable edge) |

A tight consolidation is coiled-spring/accumulation structure biased to resolve UP (the whole
momentum premise); when it breaks down instead it's often a false breakdown that snaps back. So the
tight-base short direction is closed off — definitively (both eras), more firmly than the loose-base
new-low setup.

### Exits that *didn't* survive the realistic baseline

- **Trailing limit** (sell at the prior N-day high) — a ≤+1% PF refinement under honest fills;
  retired. The "PF 3.0 / 80% winning months" of the v0 era was a top-tick fill artifact (the limit
  filled at the recent high *every* bar). See the v0 doc's warning banner.
- **Expansion exit** — sell when the name "blows off". Tested three ways (2026-06-18), all dead;
  the trailing stop is the right exit. See the dedicated section below.

### Expansion exit — a thoroughly-tested dead end (2026-06-18)

The intuition is sound (take profits when a momentum name goes parabolic), but **no variant beats
just holding to the trailing stop.** Three attempts, each with a diagnostic that explains the
failure:

1. **Tightness blow-out, LOG space** (`tightness > thr`). Under the next-open baseline PF climbs
   monotonically as the threshold loosens and converges to "off" — every firing threshold is worse
   than off. (A `thr=8` "peak" seen earlier was an artifact of the old trailing-limit fills; it
   vanished at cap=0.)
2. **Tightness blow-out, LINEAR space.** Same shape, same conclusion. The diagnostic shows *why
   it can't work in any space*: tightness = `range₁₄ / ATR₁₄`, and a climax bar inflates the
   14-day range AND the 14-day ATR **together**, so a +30% day reads the *same* tightness (~3.8
   median) as a +10% day. Tightness is a **consolidation** detector, not a **spike** detector.
   (Bonus finding: as an *entry* filter, linear vs log tightness are functionally identical —
   PF 1.758 vs 1.734, same drawdown, same monotonic cutoff curve. The `range/ATR` ratio is nearly
   scale-invariant because the price normalization cancels top and bottom.)
3. **Position-relative range** (floor the range-low at the entry price:
   `range = rangeHigh − max(rangeLow, entryPrice)`). This *does* fire on multi-bar run-ups (273
   exits at thr=4), fixing attempt 2's blind spot. But it **only ever truncates winners**: the
   floored range can only be large when the stock is far ABOVE entry, so the exit never touches a
   loser. Counterfactual on the 273 fired trades: booked +$744k, would have made **+$811k** if
   held — it left **$67k on the table**. By construction it fights the edge ("let winners run").

**Conclusion:** this momentum edge is "hold to the trailing stop." Anything that exits *because* a
name is up a lot is selling the right tail, where these names keep running more often than they
revert. Expansion exit stays **off**. The `--tightness-mode log|linear` flag and the
position-relative `ExpansionTightness` member remain in the engine as the substrate for the tested
negative result (and any future single-bar exit work).

### Initial-stop distance vs risk:reward — tighter stops are NOT better (2026-06-19)

Question: does the **distance from entry to the initial trailing stop** predict a trade's
risk:reward, i.e. are trades with *tight* stops better? Tested on a deliberately **loosened** entry
set (more samples) with both the Qulla day-low stop and the trailing prior-window low, at window 15
**and** 4.

**Test parameters (printed for the record — both runs identical except `--stop-low-window`):**

| param | value |
| --- | --- |
| side | Long, at-close entry |
| tightness mode | Linear |
| up-threshold | **0.05** (loosened from prod 0.10) |
| rvol band | **[3, ∞)** (loosened from prod [6,20]) |
| ADV / price / 52w-prox | ≥ $100k / ≥ $5 / ≥ 0.95 |
| tightness / ATR% | < 4.0 / < 0.11 |
| stop-low-window | **15** and **4** (two runs) |
| trail N / exit cap / expansion | 1 / 0 (next-open exit) / off |
| date range | 2005-01-01 → 2026-05-13 |
| breadth | post-hoc lag1 > 0.5 |

19,701 entries per run (entry signal is identical; only the stop trail differs). Metrics:
**R-multiple** = `(exit − entry)/(entry − stop)` (realized return in units of initial risk);
**stop distance** = `(entry − stop)/entry`. Note the production stop-window is **4**, not 15 — the
15-day low is the older/looser "regular" trailing stop; both are tested here.

**Qulla day-low stop** (`entry_day_stop_ref`):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| <2% | 1048 | 39.2 | 0.83 | −0.50 | 1.089 |
| 2–4% | 1579 | 34.8 | 0.31 | −0.57 | 1.39 |
| 4–6% | 2586 | 36.5 | 0.24 | −0.51 | 1.39 |
| 6–9% | 3409 | 35.4 | 0.18 | −0.45 | 1.32 |
| **9–13%** | 2151 | 37.7 | 0.35 | −0.38 | **1.68** |
| 13–20% | 1077 | 39.8 | 0.19 | −0.28 | 1.41 |
| 20%+ | 391 | 36.6 | 0.16 | −0.30 | 1.18 |

**15-day-low stop** (structurally wide — a 15-day low sits far under a breakout):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4–6% | 43 | 30.2 | −0.28 | −0.43 | 0.45 |
| 6–9% | 816 | 37.3 | 0.09 | −0.19 | 1.24 |
| 9–13% | 2685 | 36.6 | 0.07 | −0.16 | 1.28 |
| **13–20%** | 4578 | 35.8 | 0.10 | −0.15 | **1.45** |
| 20%+ | 4144 | 37.9 | 0.10 | −0.11 | 1.44 |

**4-day-low stop** (tighter; higher win rate ~42–44%):

| stop dist | n | win% | avg R | med R | PF |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4–6% | 154 | 46.8 | 0.07 | −0.04 | 1.24 |
| 6–9% | 1995 | 41.7 | 0.06 | −0.10 | 1.26 |
| 9–13% | 3634 | 41.2 | 0.03 | −0.09 | 1.12 |
| **13–20%** | 3947 | 42.7 | 0.09 | −0.06 | **1.45** |
| 20%+ | 2536 | 44.3 | 0.09 | −0.03 | 1.46 |

**Finding: tighter stops do NOT give better risk:reward — the relationship is flat-to-inverted.**
- The Qulla <2% bucket has the highest *avg* R (0.83) but it's a **trap**: PF only 1.09 and median R
  −0.50, i.e. a few big-winner tails over many whipsaw losers (low win rate). High avg-R there is
  fragile, not an edge.
- Across all three stop definitions the genuinely best (highest-PF) cells are the **WIDE** stops
  (9–20%, PF 1.45–1.68), not the tight ones. The 15- and 4-day stops both improve monotonically out
  to 13–20%.
- The 4-day stop trades a **higher win rate** (~42–44% vs ~36%) for **lower avg-R per trade**; the
  15-day stop is wider, lower win rate, but its wide buckets carry the best PF. Neither makes tight
  stops pay.
- Practical read: a tight initial stop mostly buys whipsaw. The momentum edge wants **room** — the
  best R:R lives 9–20% below entry. Don't tighten the initial stop to chase R:R.

### Tight-stop trades — a 20-day TIME-STOP rescues them (the whipsaw, confirmed) (2026-06-19)

Follow-on to the above: for the **tight-stop bucket** (Qulla day-low < 2% below entry, where the
trailing stop whipsaws), what if we'd **replaced the trailing stop with a 20-day time-stop** — hold
exactly 20 trading bars and exit at that bar's open (MTM at last close if the ticker runs out of
bars)? Post-hoc over the same loosened run; forward prices from `split_adjusted_prices` (same
CS/ADRC universe + date range), per-trade fixed-$10k notional to match the engine.

**Parameters:** same run as the stop-distance analysis above (Long, at-close, Linear, up≥0.05,
rvol[3,∞), ADV≥$100k, price≥$5, 52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth
lag1>0.5, **stop-window 15** run — the Qulla day-low ref is window-independent). Bucket = Qulla
`(entry−day_low)/entry < 0.02`. Time-stop exit = open of the 20th bar after entry.

| tight-stop bucket (Qulla <2%) | n | win% | net | PF | avg/trade |
| --- | ---: | ---: | ---: | ---: | ---: |
| actual trailing stop | 1048 | 39.2 | $15.8k | 1.089 | $15 |
| **20-day time stop** | 1048 | **60.2** | **$163.9k** | **1.684** | **$156** |

**A ~10× improvement, era-robust:** the trailing stop sits <2% away, so any normal pullback triggers
it and turns would-be winners into small losers. Holding 20 days instead: win rate 39%→**60%**, P&L
**$16k→$164k**, PF 1.09→**1.68**. Era split (only 14/1048 trades truncated <20 bars, so MTM isn't
driving it):

| era | n | trailing-stop net / PF | 20d time-stop net / PF |
| --- | ---: | ---: | ---: |
| 2005–14 | 489 | $4.3k / 1.045 | $84.3k / **1.726** |
| 2015–26 | 559 | $11.5k / 1.14 | $79.6k / **1.645** |

**Control — it is SPECIFIC to tight stops, not "time-stops beat trailing everywhere".** The same
comparison on the **wide** bucket (13–20% stop distance) is a dead heat: trailing PF 1.405 / $320k vs
time-stop 1.407 / $298k (trailing slightly ahead on P&L). So the time-stop rescue fires *only* where
the trailing stop is too close to survive normal noise. Read: a tight initial stop is not merely
"no better" — it is **actively harmful**, and the fix is to **stop trailing close and just hold**
(time-stop) for those names, not to tighten further. (Trade construction lever for a future variant:
route tight-initial-stop entries to a time-stop exit, wide ones to the trailing stop.)

### ATR%-ratchet trailing stop beats the window-low rule (2026-06-19)

Given that the Qulla window-low / entry-day-low stops keep underperforming under the microscope, test
a different mechanism: an **up-only ATR%-chandelier off the latest close**. Each bar the candidate
stop = `close − k·ATR%·close` (ATR% = the log-ATR, a per-bar fractional volatility); the carried stop
**only ever rises** (`stop = max(prev_stop, candidate)`) — never loosens even as ATR expands or price
dips. New engine `StopMode = AtrRatchet k` (CLI `--atr-stop k`); it **fully replaces** the window-low
and entry-day-low geometry (no day-low floor — the ratchet starts from the entry bar's own candidate).
Immutable Position carry of the ratchet level, no lookahead (the level checked on bar B is set from
bars ≤ B-1, then updated from B's close for B+1).

**Parameters:** loosened set — Long, at-close, Linear, up≥0.05, rvol[3,∞), ADV≥$100k, price≥$5,
52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth lag1>0.5. Stop = ATR ratchet **only**
(entry-day-low floor OFF, window-low OFF). Sweep k=1..5 vs the window-low(4) baseline:

| stop | n | win% | PF | net |
| --- | ---: | ---: | ---: | ---: |
| window-low(4) baseline | 12266 | 42.5 | 1.352 | $1.29M |
| atr-ratchet k=1 | 12266 | 45.5 | 1.309 | $1.10M |
| atr-ratchet k=2 | 12266 | 43.2 | 1.349 | $1.87M |
| atr-ratchet k=3 | 12266 | 42.3 | 1.387 | $2.81M |
| **atr-ratchet k=4** | 12266 | 41.6 | **1.421** | $3.82M |
| atr-ratchet k=5 | 12266 | 40.6 | 1.387 | $4.24M |

**The ATR ratchet beats the window-low stop at k≥3 on PF and massively on P&L** (a looser stop rides
winners far longer; P&L grows monotonically with k while PF peaks at **k=4**, PF 1.421 vs 1.352
baseline). The pattern is the same lesson again: **tight = whipsaw** — k=1 has the *highest* win rate
(45.5%) but the *lowest* P&L and a sub-baseline PF (1.31), exactly the tight-stop trap; momentum wants
room. Era-robust (k=4 wins both halves):

| era | window-low(4) PF / net | atr k=4 PF / net |
| --- | ---: | ---: |
| 2005–14 | 1.489 / $0.66M | **1.568 / $2.03M** |
| 2015–26 | 1.272 / $0.63M | **1.325 / $1.79M** |

So the ATR%-ratchet is a strictly better trailing mechanism than the legacy window-low here — higher
PF, ~3× P&L, both eras. (Not yet swept on the *production* entry set; k=4 is the loosened-set sweet
spot. Candidate to test as the production stop next.) Production default remains window-low(4) until
that confirms.

**R:R by INITIAL ATR-stop distance (k=4) — tight stops hurt here too, inverted-U.** Same breakdown as
the window-low stop-distance analysis, now for the ATR ratchet. Initial stop distance = `k·ATR%` (the
first-bar candidate is `close·(1 − k·ATR%)`, so the distance is exactly `4 × atr_pct_14_at_entry` of
entry); it varies trade-to-trade because ATR% does, even at fixed k. R = `(exit−entry)/(k·ATR·entry)`.

| initial stop dist | n | win% | avg R | med R | PF | net |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| <4% | 47 | 44.7 | 0.95 | −0.09 | 1.62 | $13k |
| 4–7% | 849 | 44.5 | 0.07 | −0.14 | **1.11** | $39k |
| 7–10% | 2287 | 42.4 | 0.09 | −0.19 | **1.16** | $173k |
| **10–14%** | 3313 | 42.0 | 0.27 | −0.20 | **1.57** | $1.04M |
| **14–20%** | 2960 | 42.6 | 0.26 | −0.20 | **1.60** | $1.28M |
| 20–30% | 2002 | 39.8 | 0.21 | −0.28 | 1.46 | $1.02M |
| 30%+ | 808 | 35.4 | 0.09 | −0.37 | 1.18 | $0.26M |

**Tight stops hurt — inverted-U, damage on both tails:** the tight **4–10%** buckets are the *worst*
(PF 1.11–1.16) — low-ATR names where even 4×ATR lands close to entry and whipsaws out, the same trap
as the window-low <2% bucket. The **10–20%** zone is the sweet spot (PF 1.57–1.60) and carries the
bulk of the P&L ($2.3M of $3.8M). The wide **30%+** tail also fades (PF 1.18, median R −0.37 — too
loose to protect, gives a lot back). The `<4%` cell (n=47) is the same misleading high-avg-R /
negative-median tail-artifact seen before — ignore it. So across *both* stop mechanisms the verdict is
identical: **tight initial stops hurt.** The ATR ratchet's edge is that at k=4 it *naturally* parks
most trades in the healthy 10–20% band; where ATR is low enough to still produce a tight stop, it
degrades exactly as the whipsaw thesis predicts.

#### It's the DISTANCE, not the mechanism — the case for a fixed-% stop (2026-06-19)

Running the *initial-stop-distance* R:R breakdown for **every** stop system on the loosened set (Qulla
day-low, 4-day-low, 15-day-low, ATR-ratchet k=2/3/4/5) gives the same shape each time: low PF at tight
distances, best PF in a 10–20%+ middle, fade at the very wide tail. The per-system tables differ in
*where* their trades fall on the distance axis (k=1 is almost all <7%; the 4-day-low spans 4–30%; the
15-day-low is mostly 10%+) but not in the relationship. Pooling **all six systems** and reading PF
purely as a function of the **absolute initial stop distance** (independent of how the stop was
derived) makes it explicit:

| initial stop dist | n | win% | PF |
| --- | ---: | ---: | ---: |
| <4% | 4402 | 40.0 | **1.183** |
| 4–7% | 12936 | 41.1 | 1.306 |
| 7–10% | 15173 | 40.8 | 1.386 |
| 10–14% | 16145 | 40.6 | 1.429 |
| 14–20% | 13675 | 40.5 | 1.448 |
| **20–30%** | 8234 | 40.1 | **1.472** |
| 30%+ | 3006 | 37.9 | 1.219 |

**Absolute distance predicts PF monotonically up to ~20–30%, then fades — and the mechanism washes
out.** A trade whose stop sits ~4% away performs like every other ~4%-stop trade (PF ~1.18) whether
that 4% came from a day-low, a window-low, or an ATR ratchet; a ~20% stop performs like every other
~20% stop (PF ~1.47). The elaborate mechanisms are just different ways of *sampling* a distance
distribution — the ones that look best (ATR k=4, 4-day-low's wide buckets) are simply the ones that
tend to land stops in the 10–30% band. **This is the argument for a fixed-% stop**: if distance is
what matters, set it directly (e.g. a ~12–20% initial stop) rather than inferring it from price
structure and getting whipsawed whenever the structure happens to be tight.

Two honest caveats: (1) this is *initial* distance vs realized P&L; post-entry trailing still differs
by mechanism (the ATR ratchet's *up-only* trail is why it booked far more total P&L — it rides
winners), so "fixed-% beats ATR" is **not** proven here — what's proven is "initial distance
dominates, tight is bad." The thing to test next is a **fixed-% initial stop** (or a min-distance
floor on the existing stops) that then trails or time-stops. (2) The 30%+ fade is partly formula
survivorship (a 30%+ ATR stop only arises on very high-ATR, intrinsically wilder names).

#### Fixed-% ratchet stop — competitive with ATR, but PF-vs-width is a degenerate objective (2026-06-19)

Built the test the previous section called for: `StopMode = FixedPct p` (CLI `--fixed-stop p`) — the
**same up-only ratchet machinery as the ATR stop** but with a *constant* fractional distance:
`stop = max(prev_stop, close·(1−p))`. This isolates distance from mechanism — same trailing, distance
set directly. Same loosened set (Long, at-close, Linear, up≥0.05, rvol[3,∞), ADV≥$100k, price≥$5,
52w≥0.95, tight<4.0, ATR%<0.11, 2005-01-01→2026-05-13, breadth lag1>0.5):

| stop | win% | PF | net | avg hold | % exit via stop |
| --- | ---: | ---: | ---: | ---: | ---: |
| window-low(4) | 42.5 | 1.352 | $1.29M | — | — |
| atr k=4 | 41.6 | 1.421 | $3.82M | — | — |
| fixed p=0.08 | 42.7 | 1.233 | $1.26M | — | — |
| fixed p=0.10 | 42.2 | 1.329 | $2.06M | — | — |
| fixed p=0.12 | 41.8 | 1.325 | $2.36M | — | — |
| fixed p=0.15 | 41.4 | 1.394 | $3.43M | 66 | 94.6% |
| **fixed p=0.20** | 40.9 | **1.464** | $5.14M | — | — |
| fixed p=0.25 | 40.4 | 1.525 | $7.12M | 161 | 91.0% |
| fixed p=0.30 | 39.8 | 1.561 | $8.96M | — | — |
| fixed p=0.35 | 39.5 | 1.689 | $12.6M | — | — |
| fixed p=0.40 | 39.0 | 1.861 | $17.9M | — | — |
| fixed p=0.50 | 38.4 | 2.084 | $27.8M | 512 | 76.4% |

**Two findings:**
1. **In the tradeable range, fixed-% matches/beats the best ATR stop.** At p=0.20 PF 1.464 > ATR k=4's
   1.421 with more P&L; p=0.15 (1.394) is comparable. Confirms the previous section: you can set the
   distance *directly* and do at least as well as inferring it from ATR — distance is the lever.
2. **But PF rises monotonically with width all the way to p=0.50 (PF 2.08) — which is a DEGENERATE
   objective, not a result.** This is the same trap as the expansion-exit dead-end: *any* exit rule
   that fires fights the momentum edge, so looser always scores higher PF. The wide end isn't a better
   stop, it's the **absence** of one: at p=0.50 the avg hold is **512 bars (~2 yrs)**, only 76% of
   trades exit via the stop (24% MTM), and the worst single trade is −91% (vs −99% theoretical) — i.e.
   buy-and-hold-the-survivors with a catastrophe stop, untradeable on per-trade drawdown / capital at
   risk. At p=0.15 it's still a real system (94.6% stop exits, ~3-month avg hold).

So **PF alone cannot pick the stop width** (it just walks you to no-stop). The realistic sweet spot is
where the stop still does its job — roughly **p=0.15–0.25** — and there fixed-% is competitive with or
slightly better than ATR k=4. Net takeaway of the whole stop investigation: **the distance is the
edge-relevant lever, tight stops whipsaw, and a simple fixed-% ratchet in the 15–25% band is as good
as the fancier mechanisms** — pick the width by drawdown/hold tolerance, not by maximizing PF.

#### Is the dead-zone gap-over underperformance a stop-distance artifact? NO (2026-06-19)

Hypothesis: yesterday's finding that **gap-over** dead-zone trades (signal-bar `adj_open ≥ hi_252_high`)
badly underperform **reclaim** ones (`open < hi_252_high`, close above) might be entirely a *stop*
effect — gap-overs gap up, so a structure-based stop sits at a different distance and whipsaws them.
If so, a **uniform fixed 10% stop** on both should erase the gap. Tested on the up=0 dead zone
(matching yesterday), window-low(4) vs fixed p=0.10:

| up=0 dead zone | window-low(4) PF / net | fixed 10% PF / net |
| --- | ---: | ---: |
| gap-over | 1.159 / $100k | 1.096 / $180k |
| reclaim | 1.318 / $565k | **1.478 / $1.64M** |
| reclaim − gap-over (PF) | +0.16 | **+0.38** |

**The hypothesis is refuted — the gap WIDENS under a fixed stop.** The fixed 10% helps *reclaims* a lot
(1.318→1.478, P&L ~3×) but leaves *gap-overs* flat-to-worse (1.159→1.096). So equalizing the stop
distance does the opposite of closing the gap. Two reasons it isn't a stop artifact:
- Under the structure stop actually used (4-day-low), gap-overs and reclaims **already had near-
  identical stop distances** (median 12.2% vs 13.0%) — there was no distance difference for a fixed
  stop to neutralize. (The mechanism the hypothesis imagined IS real but for the *day-low* stop:
  gap-overs' median day-low distance is 3.83% vs reclaims' 7.78% — gap-overs open high so the day low
  is close. But the day-low stop wasn't the one in play here.)
- A reclaim is a live intraday breakout that **keeps running if given room**, so a wider/up-only stop
  captures more of the move; a gap-over has **front-loaded** its move on overnight news, so room
  doesn't help — there's less continuation left and it often fades.

So the gap-over deficit is a property of the **setup's continuation profile, not the stop.** It is
exactly what an open / early-morning *entry* (not a different stop) would need to address — reinforcing
the deferred intraday-entry test. Reclaims, separately, are the standout beneficiary of the wide
fixed/ratchet stop (the single best dead-zone cell: up=0 reclaim + fixed 10% = PF 1.478 / $1.64M).

**Follow-up: does a 20-day TIME-STOP rescue gap-overs (like it did the tight-stop bucket)? No.**
Applied the same time-stop method (hold 20 bars, exit at the 20th bar's open, MTM if the ticker runs
out) to the up=0 dead-zone **gap-over** universe (n=3665):

| up=0 dead-zone gap-overs (n=3665) | win% | PF | net |
| --- | ---: | ---: | ---: |
| window-low(4) stop | 41.4 | **1.159** | $100k |
| fixed 10% stop | 42.8 | 1.096 | $180k |
| 20-day time stop | 53.8 | 1.125 | $156k |

The time-stop lifts win rate hard (41%→54%, holding through noise instead of stopping out) and beats
window-low on P&L, but **PF stays stuck ~1.12** — no better than the price stops, *below* window-low's
1.159. Era-robust at that mediocre level (2005–14 1.058, 2015–26 1.198), so it's not a hidden regime.
Contrast with yesterday's tight-stop bucket where the time-stop was a ~10× rescue (PF 1.09→1.68): there
the trades were genuine winners being *whipsawed out*; here holding longer just converts more gap-overs
to *small* wins — there's no large continuation to capture (the move was front-loaded overnight). So
across window-low, fixed-%, ATR, AND time-stop the gap-over PF clusters ~1.1: **the exit/stop is not
the lever for gap-overs.** Only an earlier (open / pre-market) *entry* could plausibly help — you'd
have to participate before the overnight move is already priced in.

#### Break-even-capped fixed stop + 20-day time-stop — and it helps gap-overs (2026-06-19)

A combination stop: fixed-% initial stop that ratchets up **only to break-even** (entry), then locks,
paired with a 20-day time-stop to harvest the winner. `StopMode = FixedPctBE p` (CLI `--fixed-stop-be`)
= `stop = max(prev, min(close·(1−p), entry))`; `--max-hold-bars 20`. Rationale: don't give back open
profit beyond BE (unlike the full ratchet), and don't hold past the point where momentum decays
(time-stops >25d are known to fade). Loosened set, vs the full-ratchet comparators:

| loosened set | win% | PF | net |
| --- | ---: | ---: | ---: |
| window-low(4) | 42.5 | 1.352 | $1.29M |
| atr k=4 (full ratchet) | 41.6 | 1.421 | $3.82M |
| fixed p=0.10 (full ratchet) | 42.2 | 1.329 | $2.06M |
| **BE p=0.10 + 20d time-stop** | 47.2 | **1.40** | $1.89M |
| BE p=0.15 + 20d time-stop | 49.7 | 1.39 | $2.06M |

The combo reaches **PF ~1.40 at p=0.10** — beating the equivalent full-ratchet fixed-% (1.329) and
window-low (1.352), and ≈ the full-ratchet ATR k=4 (1.421) — but with a much **higher win rate**
(47–50% vs 42%) and a **modest, honest P&L** ($1.9M, not the wide-ratchet $3.8M). The lower P&L is the
point: it's banking winners at 20 days, not riding the degenerate fat tail, so this PF doesn't depend
on the "hold-forever" artifact that inflated the wide fixed-% sweep.

**Dead-zone split — this is the FIRST treatment that helps gap-overs:**

| up=0 dead zone | gap-over PF | reclaim PF |
| --- | ---: | ---: |
| full-ratchet fixed 10% | 1.096 | **1.478** |
| **BE 10% + 20d time-stop** | **1.224** | 1.385 |
| BE 15% + 20d time-stop | 1.189 | 1.432 |

Capping at break-even + harvesting at 20 days lifts **gap-overs 1.096 → 1.224** (win rate 43%→52%) —
the first thing to move them off ~1.1, because the BE cap stops them round-tripping into losers and the
time-stop banks the front-loaded move before it fades. The trade-off is symmetric: it slightly **lowers
reclaims** (1.478 → 1.385), which *want* room to keep running and get cut short by the BE cap + 20d
harvest. So the two setups have **opposite stop preferences**: reclaims → wide full-ratchet (run);
gap-overs → BE + time-stop (bank it). ⚠️ **Caveat (era split):** the gap-over lift is **modern-era
concentrated** — 2015–26 PF 1.381 vs 2005–14 1.091 — so it's a regime-tilted improvement, not a stable
cross-era edge. Better than the ~1.1 every other stop gave, but not robust; the open-entry idea remains
the cleaner potential fix for gap-overs.

#### Fixed profit TARGETS — do not help (truncate the right tail) (2026-06-19)

Added a fixed profit-target exit (`StopMode` independent; CLI `--profit-target t`): a resting sell
limit at `entry·(1+t)`, fills **intrabar** at `max(target, open)` (gap-up through the limit fills at
the better open, else at the limit). It wins over a same-bar stop (the stop only acts next-open).
Swept 15/20/30/50% on the BE 10% + 20d base (loosened set):

| BE 10% + 20d base | win% | PF | net |
| --- | ---: | ---: | ---: |
| no target | 47.2 | **1.40** | $1.89M |
| + target 15% | 50.6 | 1.352 | $1.57M |
| + target 20% | 49.2 | 1.380 | $1.73M |
| + target 30% | 48.0 | 1.374 | $1.74M |
| + target 50% | 47.5 | 1.390 | $1.83M |

**Every target level lowers both PF and P&L**, monotonically worse the tighter the target (15% →
1.352 / $1.57M); as the target widens toward "off" (50%) it converges back up to the no-target
baseline. Win rate rises (capping books more small wins) but that's the losing trade-off — **the
target truncates the right tail that carries a momentum system.** Same lesson as the expansion exit,
the gap-over time-stop, and the wide-stop PF degeneracy: any rule that caps the *upside* fights the
edge. Fixed targets are a confirmed non-starter for this system — let winners run; manage only the
downside (stop) and the holding period (time-stop).

**Fill convention doesn't matter.** Re-ran the sweep with `--target-next-open` (target hit → exit at
the NEXT bar's open, a signal rather than an intrabar limit): PF 1.350 / 1.379 / 1.363 / 1.375 at
15/20/30/50% — within ≤0.015 of the intrabar-limit version (1.352 / 1.380 / 1.374 / 1.390), and
next-open marginally *worse* (you give a little back on the open). So the negative result is robust to
how the target fills — it's the *capping*, not the fill, that costs. Both conventions stay below the
1.40 no-target base at every level. Engine retains `--profit-target` and `--target-next-open` as the
substrate for this tested negative result.

---

#### Post-exit path study — forward return by EXIT bucket, and ATR%-at-exit is the discriminator (2026-06-19)

The question: after a trade closes, does the *state it closed in* tell us anything about what the
stock does next — i.e. is the −10% stop throwing away a bounce (mean-reversion), and do big winners
keep winning? Measured as the **20-bar forward return from the EXIT price** (the recycle-vs-hold
decision), bucketed by realized gain-at-exit, then cross-cut by ATR%-at-exit.

**System parameters (this study):**
```
side = long              stop mode = fixed-pct ratchet p=0.100   entry-day-stop = true
entry = at-close         time-stop = off    profit target = off   exhaustion = off
entry gates: up>=0.05  rvol[3,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
trips = 18,310   win% 41.7   PF 1.377   net $3.41M     (LOOSENED set: rvol≥3, move≥5%)
```
Forward return = `close[exit_rn+20] / exit_price − 1`, joined per-ticker on `split_adjusted_prices`;
ATR%/tightness at exit reconstructed (log-ATR% and linear tightness over the prior 14 bars).

**By exit bucket** (realized gain at exit):

| exit bucket | n | avg gain@exit | fwd20 mean | fwd20 med | fwd20 PF | win% | tight@exit | ATR%@exit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 3,055 | −16.6% | −0.39% | −0.24% | **0.936** | 48.7 | 4.76 | 5.79 |
| −10..0% | 7,471 | −5.3% | +0.76% | +0.45% | 1.159 | 51.4 | 4.24 | 4.39 |
| 0..+10% | 3,839 | +4.3% | +1.20% | +1.11% | 1.252 | 53.9 | 4.38 | 4.62 |
| +10..+30% | 2,518 | +17.6% | +1.17% | +0.69% | 1.226 | 51.9 | 4.54 | 5.06 |
| +30%+ | 953 | +57.6% | +1.00% | **−0.68%** | 1.149 | 47.1 | 4.91 | 6.17 |

Two answers fall out: **(1) the −10% stop is neutral going forward** (PF 0.936, mean −0.39%, win
48.7%) — a name that has reverted ≥10% from entry has *no* forward edge, so recycling the capital is
correct and the stop is not sacrificing a bounce. **(2) Winners do NOT keep winning** — forward PF
*peaks in the middle* (0..+10%: 1.252) and decays with the gain; the +30%+ bucket has a **negative
forward median (−0.68%)** and sub-50% win rate. The extended names are the ones that give it back.
Note the loosest base (tight 4.76/4.91) and highest ATR% (5.79/6.17) sit in exactly the two
worst-forward buckets — the loose-base + high-vol exhaustion signature again.

**…but the gain bucket is mostly a proxy. ATR%-at-exit is the real axis** (standalone, ignoring gain):

| ATR%@exit | n | avg gain@exit | fwd20 mean | fwd20 med | fwd20 PF | win% |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| <2% | 731 | −0.2% | +1.38% | +1.70% | **1.551** | 59.1 |
| 2-3% | 3,910 | +1.4% | +1.12% | +1.15% | 1.343 | 55.9 |
| 3-4% | 3,888 | +1.9% | +0.77% | +0.83% | 1.196 | 53.7 |
| 4-5% | 2,672 | +1.5% | +0.66% | +0.53% | 1.136 | 52.0 |
| 5-6% | 2,002 | +0.1% | +0.43% | 0.00% | 1.077 | 50.0 |
| 6-8% | 2,408 | −1.1% | +0.85% | −0.88% | 1.125 | 47.3 |
| 8-10% | 1,176 | +1.2% | +0.66% | −1.65% | 1.084 | 46.3 |
| 10-12% | 562 | +1.9% | −1.34% | −4.03% | **0.879** | 42.9 |
| 12-16% | 312 | +13.0% | +0.40% | −8.04% | 1.032 | 36.5 |
| 16-20% | 42 | +18.0% | −6.66% | −11.79% | **0.622** | 33.3 |
| 20%+ | 23 | +176.5% | −2.76% | −10.95% | 0.871 | 34.8 |

**Forward edge decays monotonically with ATR%-at-exit and dies above ~10%.** Median and win% fall
in lockstep: the *typical* name stops advancing right at **5-6% ATR** (median 0.0, win 50.0) and is
outright negative-EV by **10%+** (PF 0.88, median −4%). The 12%+ cells carry a few fat-tail survivors
that prop up the mean/PF while the median bleeds −8% to −12% — read the median there, not the PF. The
mass is in the healthy 2-5% region (~10.5k of ~18.2k exits); the toxic 10%+ region is thin (~940).

**Cross-cut (gain bucket × ATR%) confirms ATR% dominates:** within *every* gain bucket the <4%-ATR row
is a healthy ~1.25–1.35 forward PF with a positive median, and the 12%+-ATR row collapses to a
negative median (−3% to −13%). The two genuinely toxic cells (PF<1, negative median, real n): **< −10%
& 12%+ ATR** (PF 0.422, median −11.95%, n=108 — a name down ≥10% *and* blown-out keeps falling) and
**+30%+ & 12%+ ATR** (PF 0.964, median −13.18%, n=94 — the extended-and-violent winner reverts hard).

**Takeaways.** The −10% stop is vindicated (neutral forward EV — recycle, don't hold for a bounce).
"Let winners run" is true on average but the *extended-and-high-vol* tail reverts — consistent with
every prior exhaustion finding. And the single most predictive post-exit variable is **ATR%, not the
gain**: low vol → continuation, high vol → reversion, the same volatility axis the entry gate
(atr% < 0.11) already screens on. This is a forward/diagnostic measurement, not a new exit rule.

---

#### ⭐ Regime-switched CHANDELIER stop — raises PF *and* passes the forward-EV test (2026-06-19)

**The principle (from the post-exit study).** Be long while forward-EV is positive; flat when it
turns. Forward-EV is a *decreasing function of ATR%* (≈ +1.7% median at 2% ATR → −4% at 10%+ ATR), so
the EV-zero exit point is a roughly fixed *ATR-dollar* move, not a fixed %. The acceptance test for any
exit: **a forward-PF breakdown of the stop exits should show no bucket above ~1.1** — if it does, we
sold something still trending.

**Two dead-ends first.** (a) A *proportional* ATR-multiple stop (`stop% = k·ATR%`, `--atr-stop`) widens
the leash as vol rises — backwards — already known to only modestly beat window-low. (b) An *inverse*-ATR
ratchet (`stop% = w·atrRef/ATR%`, new `--inv-atr-stop`) tightens continuously as vol rises, but made the
stop-exit forward-PF *worse* (1.21 vs flat-10%'s 1.14): tightening on *every* high-vol name shakes out
the noisy-pullback names that then bounce (the median reverts but there's a fat bounce tail). **A
price-pullback stop of any shape selects for mean-reverters** — it can't get below 1.1.

**The fix — regime-switched chandelier off the running MAX CLOSE** (new `--chandelier-regime wide tight
atrThr`, `StopMode.ChandelierRegime`). One high-water-mark `maxClose` per position; each bar
`width = (ATR% ≥ atrThr ? tight : wide)`, `stop = maxClose·(1−width)`. Quiet names get a *wide* leash
(noise can't shake them); the *instant* ATR% crosses the negative-EV threshold the width snaps tight off
the **peak** (above the old wide line → bites immediately). Path-independent — pure `f(maxClose, regime)`,
no ratchet bookkeeping, no lookahead (mark through B−1, ATR% = the pre-push log-ATR snapshot).

**System parameters:**
```
--chandelier-regime 0.20 0.10 0.10   (20% leash when quiet; 10% off the peak once ATR% ≥ 10%)
side = long   entry-day-stop = true   time-stop = off   profit/exhaustion = off
```

**Forward-PF acceptance test passes** (loosened set, rvol≥3 move≥5%, fwd-20d-from-exit):

| stop | book PF | book net | avg gain@exit | **stop-exit fwd-PF** | fwd-med |
| --- | ---: | ---: | ---: | ---: | ---: |
| flat 10% | 1.377 | $3.41M | +1.4% | 1.141 | +0.47% |
| **chandelier 20/10@10%** | **1.516** | **$8.18M** | **+3.3%** | **1.089** | +0.40% |

The chandelier pushes the stop-exit forward-PF **below the 1.1 neutral line** (1.089) — we stop selling
things that keep going — *while* lifting book PF and letting winners run further (+3.3% vs +1.4% avg gain
at exit). The wide leash holds quiet trenders through noise; the tight-off-peak arm cuts the violent
negative-EV names. First mechanism in this workstream to improve book PF and pass the forward-EV test
together.

**Production gate (rvol[6,20], move≥10%) + breadth (lag-1 pct_above_20 > 0.5), era-split:**

| stop | ALL PF | pre-2015 | post-2015 | ALL net |
| --- | ---: | ---: | ---: | ---: |
| flat 10% | 1.335 | 1.215 | 1.444 | $367k |
| **chandelier 20/10@10%** | **1.419** | **1.391** | 1.445 | **$774k** |

**+0.084 blended PF, 2.1× the P&L, and it RESCUES the weak pre-2015 era** (1.215 → 1.391), pulling the
two eras to near-parity (1.391 / 1.445) vs the flat stop's lopsided split. Robustifies across regime
rather than relying on one — the opposite of an artifact. (Without breadth: prod-gate ALL PF 1.554 vs
1.419; pre/post 1.428/1.440.) Confirmed the default and all pre-existing stop modes are byte-unchanged
(new code only adds match arms). **This is the live candidate to replace the flat/window-low stop.**

---

## Yearly breakdown (flat $10k/trip, filtered, by entry year)

| year | trips | win% | PF | net |
| ---: | ---: | ---: | ---: | ---: |
| 2005 | 100 | 50% | 1.38 | +9,277 |
| 2006 | 130 | 39% | 1.61 | +17,275 |
| 2007 | 109 | 43% | 1.87 | +24,381 |
| 2008 | 34 | 59% | 1.73 | +8,161 |
| 2009 | 51 | 53% | 3.71 | +27,346 |
| 2010 | 123 | 53% | 2.37 | +41,138 |
| 2011 | 82 | 43% | 1.42 | +9,226 |
| 2012 | 73 | 52% | 3.19 | +29,421 |
| 2013 | 181 | 47% | 1.69 | +34,559 |
| 2014 | 107 | 50% | 1.65 | +18,738 |
| 2015 | 86 | 51% | 2.45 | +27,830 |
| 2016 | 88 | 43% | 1.81 | +11,748 |
| 2017 | 138 | 46% | 1.35 | +13,744 |
| 2018 | 127 | 54% | 1.73 | +23,002 |
| 2019 | 93 | 49% | 4.80 | +103,596 |
| 2020 | 149 | 42% | 1.81 | +57,348 |
| 2021 | 191 | 36% | 1.09 | +9,529 |
| 2022 | 33 | 45% | 1.34 | +5,329 |
| 2023 | 71 | 51% | 1.59 | +12,368 |
| 2024 | 127 | 40% | 1.21 | +9,694 |
| 2025 | 119 | 39% | 0.97 | −1,570 |
| 2026 | 48 | 46% | 2.75 | +28,501 |

**21 of 22 years positive** (only 2025 fractionally red at −$1.6k). Crucially, the edge is now
*spread across years* rather than concentrated: 2021's COVID-bubble names — which dominated the old
system — are largely filtered out by the tight ATR%/tightness gates (2021 is only +$9.5k here),
and 2020 is solidly positive (+$57k) on the next-open exits getting clear of the March crash. The
two biggest years (2019 +$104k, 2020 +$57k) are real momentum regimes, not a single blow-off.

<details>
<summary>Full monthly breakdown — net P&L by year × month ($k), flat sizing, by entry month — click to expand</summary>

| year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec | **year** |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2005 | 747 | -2.5k | -876 | · | 796 | 3.2k | 9.3k | 97 | 702 | -378 | -1.1k | -632 | **9.3k** |
| 2006 | 150 | 584 | 6.0k | 2.9k | -6.4k | -1.4k | 96 | 1.5k | 49 | 2.6k | 7.0k | 4.2k | **17.3k** |
| 2007 | 1.4k | 4.8k | 1.7k | 10.5k | -2.1k | -1.3k | -2.9k | 213 | 15.7k | -2.2k | -620 | -896 | **24.4k** |
| 2008 | · | 969 | -1.0k | 3.0k | 10.5k | 1.7k | -2.2k | -4.6k | · | · | -429 | 246 | **8.2k** |
| 2009 | -593 | · | · | · | 4.2k | 297 | -346 | 16.9k | 691 | 475 | 4.8k | 882 | **27.3k** |
| 2010 | 418 | 4.9k | 13.7k | -2.4k | -1.1k | · | -94 | 2.8k | 5.5k | 145 | 13.7k | 3.6k | **41.1k** |
| 2011 | -5 | 7.6k | -1.0k | 3.9k | -371 | · | 320 | · | -706 | · | -1.1k | 631 | **9.2k** |
| 2012 | 6.9k | 1.8k | 3 | -3.2k | -711 | 13.7k | 266 | 6.7k | 2.5k | -616 | -105 | 2.2k | **29.4k** |
| 2013 | -864 | 3.5k | -390 | 3.5k | 13.7k | · | 5.5k | 632 | 681 | 8.3k | -177 | 204 | **34.6k** |
| 2014 | -1.2k | 4.3k | -1.3k | 956 | 6.4k | 7.7k | 63 | -1.4k | -1.7k | 5.1k | 2.5k | -2.7k | **18.7k** |
| 2015 | 15.4k | 7.6k | -899 | 481 | 980 | 536 | · | -1.2k | · | 4.8k | 1.4k | -1.3k | **27.8k** |
| 2016 | · | -36 | 15 | -559 | 837 | -594 | 4.0k | 9.4k | 474 | -1.0k | 42 | -803 | **11.7k** |
| 2017 | -495 | 2.5k | 3.6k | 7.4k | 3.9k | 3.5k | -45 | -802 | -3.2k | 3.8k | -3.3k | -3.3k | **13.7k** |
| 2018 | 359 | · | 277 | 4.2k | 9.2k | -3.7k | 7.0k | 7.0k | 428 | · | 1.8k | -3.6k | **23.0k** |
| 2019 | 2.7k | 99.6k | -2.9k | 6.2k | -35 | 954 | -2.2k | · | -1.4k | -1.1k | 7.1k | -5.4k | **103.6k** |
| 2020 | 2.7k | -12.9k | · | 1.5k | 8.6k | 4.8k | 2.3k | -9.5k | · | -280 | -354 | 60.5k | **57.3k** |
| 2021 | 32.3k | -24.6k | -3.0k | -4.1k | 3.8k | -7.5k | -357 | 15.5k | 1.2k | -95 | -3.1k | -431 | **9.5k** |
| 2022 | · | -665 | 2.2k | · | -1.3k | 596 | 1.0k | -338 | · | -276 | 2.6k | 1.5k | **5.3k** |
| 2023 | -1.1k | -583 | · | 170 | 1.3k | 7.1k | -4.0k | -1.1k | 364 | · | 2.4k | 7.8k | **12.4k** |
| 2024 | 5.1k | 3.1k | -1.4k | 3.9k | 272 | · | -744 | 51 | -1.2k | 6.0k | -3.7k | -1.6k | **9.7k** |
| 2025 | -1.1k | -2.5k | · | 26 | 380 | 4.8k | -5.6k | -714 | 12.8k | -7.3k | -171 | -2.0k | **-1.6k** |
| 2026 | 28.8k | -2.1k | -1.0k | 1.5k | 1.3k | · | · | · | · | · | · | · | **28.5k** |

`·` = no trades that month. 135 / 230 months positive (58.7%); worst month −$24.6k (Feb 2021,
the post-blow-off unwind); best month +$99.6k (Feb 2019). Outlier months are upside (Feb 2019,
Dec 2020 +$60k, Jan 2021/2026 ~+$30k), not catastrophic downside — the tight entry gate keeps the
left tail shallow.

</details>

---

## Reproduction

```bash
# v2 default: stop-window 4, next-open exit (cap=0, no trailing limit, expansion off),
# log-space entry filters (ATR% < 0.11, tightness < 4.0), entry-move floor 10%.
# Run from dataset start for the 252-day warmup; filter entries to >=2005 post-hoc.
dotnet run --project TradingEdge.MomentumV2 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 -o /tmp/v2.csv

# then apply, post-hoc:
#   breadth_lag1 > 0.5   — LAG(pct_above_20) by 1 trading day on
#                          data/equity/momentum_v0/breadth.parquet, joined on entry_date
#   entry_date >= 2005-01-01
```

All defaults live in `TradingEdge.MomentumV2/Backtest.fs` (`defaultConfig`). Threshold sweeps use
the CLI overrides `--max-atr-pct`, `--max-tightness`, `--expansion-thr`, `--exit-time-cap`,
`--stop-low-window`, `--trail-window`, `--rvol-min/--rvol-max`.

---

## Engine notes

- **One in-memory pass.** Per ticker, a `QullaSystem` folds each bar into rolling structures
  (monotonic-deque sliding max/min for the stop/trail/52w channels, invertible sums for the ATR and
  volume means, a calendar-day mean for the 28-day volume window). All reads use the prior-bars
  snapshot taken *before* the current bar is folded in — no lookahead.
- **Indicators are `voption`** throughout; a gate whose metric is `ValueNone` (insufficient history)
  fails the entry, matching v0's `COALESCE(..., FALSE)`.
- **Universe** = `split_adjusted_prices` JOIN `ticker_reference` WHERE `type IN ('CS','ADRC')`,
  streamed `ORDER BY ticker, date`, flushed at ticker boundaries.

## Data fix — split_adjusted_prices dividend adjustment (2026-06-18)

While drilling the new-52w-low study, found stocks with impossible `pct_52w_low = −162%`, traced to a
**negative adjusted close** (DOMH 2024-12-30: raw $0.8975 → adj −$0.0745). Root cause: the
`split_adjusted_prices` materialization adjusted dividends **subtractively** (`adj = price − Σ
dividend_dollars`), which on low-priced dividend payers drives the price ≤ 0. Confirmed it was
dividends not splits (DOMH has zero splits; raw prices are clean; `raw − adj` was a constant dollar
step per ex-date). Affected **209,701 rows across 563 tickers** (all dividend payers), and silently
mis-scaled *every* payer's history. Fixed to the correct **multiplicative** back-adjustment
(`f = 1 − div/close_on_exdate` per ex-date, reverse cumulative product); rebuilt → 0 non-positive
prices. **Impact on this system was negligible** (PF 1.758→1.769) because the production filters
exclude the low-priced names where the bug lived; the headline above is on the corrected data.

## Known TODO (in code)

- **Hard up-only stop ratchet** — the stop recomputes `max(prior-window low, entry-day low)` each
  bar and can tick *down* if a lower low slides into the window. A true Qulla trailing stop never
  loosens. Deferred; test as a strategy change on top of this baseline.

*(The old "ATR% denominator uses the current close" TODO is RESOLVED — that's exactly what the
log-space rewrite fixed.)*
