# Mid-Cap Momentum v2 — Log-Space Volatility Filters

> **📚 ARCHIVE (as of 2026-06-21).** The **current** production state and all research under the clipped /
> cumulative methodology live in [`momentum_v3_results.md`](momentum_v3_results.md). This v2 document is the
> full historical research record — exit-mechanic sweeps, 52w-proximity studies, loose-base shorts,
> regime-switching, chandelier/time-stop derivation — kept intact for reference. Where a v2 finding was
> re-derived under the clip (notably the ATR% ceiling, now **0.10** not 0.11), v3 is authoritative.

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
| relative volume | **rvol ≥ 5** (no upper cap) | `volume / 28-day avg volume`. Floor 6→5 on 2026-06-20 (rvol 5 is enough to be significant). The upper cap was *removed* — the 30%-move cap below handles the blow-off tail instead |
| entry-day move | **10% ≤ move < 30%** | `close/prevClose − 1`. The 30% cap (added 2026-06-20) removes the single-day exhaustion blow-off; it makes an rvol cap redundant |
| ATR% (log) | **< 0.11** | mean log-true-range over 14 prior bars (see below) |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear; sharper loose-tail cut than log). Raised 4.0 → 4.5 on 2026-06-20 (clean capacity gain; < 4.0 = max-PF, < 5.5 = max-capacity alternatives) |
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

## Yearly breakdown — PRE-time-stop default (window-low stop-4), filtered (flat $10k/trip, by entry year)

> **Which system:** this is the production default *as it stood before the 2026-06-19 stop-mechanics
> rework* — **window-low trailing stop (stop-window 4), next-open exit, expansion off, ATR% < 0.11,
> log-tightness < 4.0, entry-move ≥ 10%**, + breadth (lag-1 pct_above_20 > 0.5) + entry ≥ 2005. It is the
> `≥ 10%` headline row above (**2,260 trips, PF 1.734, +$520,641, 58.7% positive months**). It is **not**
> the current default (5-day time-stop, no price stop, rvol [5,15] — see "The system in one screen"); the
> time-stop system's own yearly/monthly breakdown has not yet been generated. Kept here as the historical
> reference for the system's year-by-year robustness.

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

#### Chandelier LADDER (N-tier) — the high-ATR tiers calibrate; the quiet tier won't (2026-06-19)

Generalized the 2-tier chandelier to an N-tier ATR% ladder (new `StopMode.ChandelierLadder`, CLI
`--chandelier-ladder "thr:w,…,base:w"` — highest matching threshold wins; max-close anchored as before).
Tested the user's 4-tier ladder **8% @ ATR≥10%, 10% @ ≥8%, 12% @ ≥6%, 15% @ <6%**, plus the 2-tier
**12%/8% @ 10%** for reference.

**System parameters / book results:**
```
--chandelier-ladder "0.10:0.08,0.08:0.10,0.06:0.12,base:0.15"
side = long   entry-day-stop = true   time/profit/exhaustion = off
```

| stop | loose PF | loose net | prod PF | prod net |
| --- | ---: | ---: | ---: | ---: |
| chandelier 20/10 @10% | 1.516 | $8.18M | 1.554 | $1.60M |
| chandelier 12/8 @10% | 1.394 | $3.96M | 1.526 | $0.98M |
| ladder 8/10/12/15 | 1.421 | $4.95M | 1.479 | $1.02M |

The narrower-base ladders (15% / 12%) sit below the 20%-base version on book PF — same "wider base leash →
higher book PF" pattern (the wide leash lets winners run).

**Breakdown by WHICH stop fired** (loosened set, fwd-20d from exit; regime reconstructed from
ATR%-at-exit against the ladder thresholds):

| which stop fired | n | avg gain@exit | avg held | ATR%@exit | fwd med | **fwd PF** |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8% leash (ATR≥10%) | 1,001 | +9.7% | 17 | 12.2% | −4.37% | **0.946** |
| 10% leash (ATR 8-10%) | 1,355 | +2.1% | 12 | 8.9% | −1.50% | **1.083** |
| 12% leash (ATR 6-8%) | 2,819 | −0.5% | 25 | 6.9% | −0.76% | **1.090** |
| 15% leash (ATR<6%) | 12,473 | +2.1% | 82 | 3.7% | +0.94% | **1.219** |

(2-tier 12/8 ran the same way: TIGHT 8% → fwd-PF 0.911 / med −5.2%, n=1039; WIDE 12% → fwd-PF 1.193 /
med +0.52%, n=16732 — same split, coarser.)

**The decisive finding:** the **three high-ATR tiers are all calibrated** — fwd-PF 0.946 → 1.083 → 1.090,
each with a *negative* forward median, i.e. they sell names that then fall (the 8% tier catches violent
names at +9.7% avg gain right as they go negative-EV). The laddered tight side works exactly as designed,
monotone in ATR%. **The entire residual edge-leakage AND the entire drawdown live in the low-ATR 15%
tier**: fwd-PF **1.219**, positive median, **71% of all exits (12,473)** held a punishing **82 bars
(~4 months)** — we're stopping quiet trenders that keep going.

**This tier cannot be fixed by the price-stop width** — seen three ways now: 20% base → fwd-PF 1.19, 15%
base → 1.219, 12% → 1.193. The quiet leash trades book-PF against this number but never gets it under
1.1, because *any* price-pullback stop on a quiet trender sells into the bounce (the median reverts but
the bounce tail props the PF). The 82-bar hold confirms these are sideways/up grinders that occasionally
dip into the stop.

**Architecture conclusion:** high-ATR names want a *price* stop (the ladder works); the low-ATR majority
wants a *time/gain*-gated recycle, not a price stop. The right design is a **hybrid** — ladder price-stop
for the volatile tiers + the ATR/gain-gated time-stop (hold while ATR% < ~8% AND gain < +60%; recycle
otherwise) for the quiet names. That is the next build.

---

#### Time-stops with NO price stop — the "hold another 5 days?" map, gated on ATR% (2026-06-19)

The chandelier-stop geometry got complicated and its quiet-name holds ran **9 months** (the wide leash
lets a sideways drifter sit for ~189 bars). Cleaner idea: drop the price stop entirely, use a pure
**time-stop**, and decide *when* to recycle from the forward EV — at the exit, given ATR%, is holding
another 5 days still +EV? If yes, hold; if no, recycle the capital.

**System parameters:**
```
--no-stop --max-hold-bars N   (N ∈ {5,10,15,20,25,30}; the time-stop is the ONLY exit)
side = long   entry-day-stop = false (no-stop)   profit/exhaustion = off
entry gates: up>=0.05  rvol[3,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
LOOSENED set: 18,310 trips
```

**Time-stop sweep (no price stop):**

| hold | PF | net | win% |
| --- | ---: | ---: | ---: |
| 5d | **1.346** | $1.68M | 51.0 |
| 10d | 1.287 | $1.86M | 50.6 |
| 15d | 1.310 | $2.37M | 51.8 |
| 20d | 1.361 | $3.14M | 52.1 |
| 25d | 1.353 | $3.42M | 52.6 |
| 30d | 1.328 | $3.51M | 51.8 |

PF peaks at the short (5d, 1.346) and the 20d hold (1.361); net P&L climbs monotonically with hold
length (more trend captured per trade). No stop needed — the stop was mostly forcing rotation, which the
time-stop does more cleanly. Holds are now *bounded* by construction (the drawdown / 9-month-hold
complaint goes away).

**The "hold another 5 days?" map** — forward-5d return measured FROM each time-stop exit, bucketed by
ATR%-at-exit, one column per hold length. Read the **median** in the high-ATR cells (PF/mean there are
fat-tail-inflated by a few survivors).

Forward-5d **PF** (>1.1 → keep holding; ≤1.0 → recycle):

| ATR%@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| <4% | 1.073 | 1.231 | 1.148 | 1.120 |
| 4-6% | 1.062 | 1.168 | 1.050 | 0.919 |
| 6-8% | 1.021 | 1.174 | 0.940 | 1.072 |
| 8-10% | 1.141 | 1.038 | 1.100 | 1.045 |
| 10-14% | 0.877 | 0.974 | 0.847 | 1.077 |
| 14%+ | 1.141 | 0.994 | 1.008 | 1.406 |

Forward-5d **MEDIAN %** (the honest read):

| ATR%@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| <4% | +0.05 | +0.32 | +0.18 | +0.19 |
| 4-6% | +0.09 | +0.28 | 0.00 | −0.17 |
| 6-8% | −0.28 | +0.11 | −0.45 | −0.11 |
| 8-10% | −0.33 | −0.66 | −0.99 | −0.51 |
| 10-14% | −2.14 | −1.15 | −2.14 | −0.41 |
| 14%+ | −5.81 | −0.77 | −3.10 | −0.06 |

Forward-5d **MEAN %** (right-tail visible — diverges from median exactly where the cell is fat-tailed):

| ATR%@exit | 5d (n) | 10d (n) | 20d (n) | 30d (n) |
| --- | ---: | ---: | ---: | ---: |
| <4% | +0.11 (7457) | +0.31 (7104) | +0.24 (10285) | +0.20 (10232) |
| 4-6% | +0.15 (6014) | +0.38 (5864) | +0.14 (4341) | −0.24 (4459) |
| 6-8% | +0.08 (2615) | +0.58 (2750) | −0.24 (1946) | +0.28 (1920) |
| 8-10% | +0.60 (1147) | +0.17 (1276) | +0.51 (799) | +0.21 (766) |
| 10-14% | −0.78 (803) | −0.15 (885) | −1.02 (540) | +0.41 (488) |
| 14%+ | +1.38 (131) | −0.05 (261) | +0.07 (187) | +2.54 (179) |

**Findings:**
1. **Only the quiet <4% bucket is reliably worth holding at every horizon** — forward PF 1.07→1.23→1.15
   →1.12, positive median throughout. Quiet trenders keep grinding up; this is the core hold.
2. **Everything ≥10% ATR is negative-EV by median at essentially every hold length** (10-14% medians
   −2.14/−1.15/−2.14/−0.41; 14%+ medians −5.81/−0.77/−3.10). The occasional high PF/mean is one name
   ripping while the typical one bleeds. **Recycle high-ATR names regardless of the clock.**
3. **The mid buckets (4-10%) decay with hold length** — fine at 5-10d (PF 1.04-1.17), but the 4-6% and
   6-8% rows roll under 1.0 by 20-30d. The longer you've held, the lower the ATR% at which holding stops
   paying.

The clean separator across all horizons is **~8-10% ATR**: below it the median stays non-negative at
short holds and the <4% band stays positive everywhere; above it the median is negative everywhere. This
is the substrate for a **conditional / ATR-gated time-stop** — a base time-stop that extends only while
the name is quiet, and cuts early (independent of the clock) once ATR% is high. The second axis
(gain-from-entry, treated separately) is the next breakdown before designing that rule.

**The second marginal — gain-from-entry — is a MUCH weaker discriminator than ATR%** (same exits, same
forward-5d measure):

Forward-5d **PF** by gain-from-entry:

| gain@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| < −20% | 0.772 | 1.054 | 0.989 | 1.065 |
| −20..−10% | 1.019 | 1.091 | 0.918 | 1.073 |
| −10..0% | 1.120 | 1.129 | 1.144 | 1.059 |
| 0..+10% | 1.020 | 1.202 | 1.145 | 1.077 |
| +10..+30% | 0.972 | 1.084 | 0.950 | 1.014 |
| **+30..+60%** | **1.304** | **1.236** | **1.165** | 1.032 |
| +60%+ | 0.531 | 0.907 | 0.848 | 0.926 |

Forward-5d **MEDIAN %** by gain-from-entry:

| gain@exit | 5d | 10d | 20d | 30d |
| --- | ---: | ---: | ---: | ---: |
| < −20% | −1.02 | −0.66 | −0.68 | +0.13 |
| −20..−10% | −0.24 | +0.17 | −0.14 | +0.17 |
| −10..0% | +0.15 | +0.28 | +0.27 | +0.16 |
| 0..+10% | −0.01 | +0.26 | +0.13 | +0.16 |
| +10..+30% | −0.62 | −0.06 | −0.14 | −0.08 |
| +30..+60% | −1.18 | −0.02 | −0.35 | 0.00 |
| +60%+ | −4.42 | +0.52 | −1.56 | −1.04 |

**Findings on the gain axis:**
1. **The middle is flat & mildly positive** — the −10..+10% buckets hold ~80% of exits (≈15k of 18.3k)
   at PF 1.02-1.20, small positive medians; no actionable signal.
2. **The tails bite opposite to a naïve "cut losers / ride winners" rule.** The big-winner tail
   (**+60%+**) is *toxic* — PF 0.53/0.91/0.85/0.93, median −4.42% at 5d (extended names revert). The
   big-loser tail (**< −20%**) is *neutral*, even mildly positive by 30d (median +0.13) — no
   continued-bleed edge from cutting losers fast (echoes the "−10% stop is neutral" finding).
3. **The +30..+60% band is the one winner zone worth holding longer** — PF 1.304/1.236/1.165 with
   ~flat medians; a strong, durable continuation cohort distinct from the +60%+ blow-offs.

**Net:** gain-from-entry is a blunt knife — it mostly re-expresses ATR% (a stock can't reach +60% without
being volatile), plus a real "+30-60% = keep holding" pocket. **ATR% stays the primary gate;** the gain
axis adds a "cut if +60%+ / hold if +30-60%" nuance. Whether the +60% toxicity is independent of ATR% or
just ATR% in disguise is settled by the 2D gain×ATR cross (next).

**2D gain×ATR cross** (all four hold lengths pooled for cell counts; forward-5d-from-exit):

PF — rows gain-from-entry, cols ATR%@exit:

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 1.25 | 0.94 | 1.02 | 1.00 | 0.89 | 1.26* |
| −10..0% | 1.15 | 1.14 | 1.07 | 1.02 | 0.96 | 0.63 |
| 0..+10% | 1.13 | 1.08 | 1.03 | 1.29 | 0.99 | 1.49* |
| +10..+30% | 1.03 | 0.94 | 1.16 | 1.04 | 0.88 | 0.70 |
| **+30..+60%** | **1.42** | 1.15 | 0.88 | 1.30 | 1.05 | 1.53* |
| +60%+ | 0.08* | 0.41 | 0.96 | 0.89 | 0.93 | 0.81 |

MEDIAN % — same axes:

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | +0.44 | 0.00 | −0.20 | −0.58 | −1.96 | 0.00 |
| −10..0% | +0.23 | +0.31 | −0.09 | −0.78 | −0.72 | −5.92 |
| 0..+10% | +0.15 | +0.11 | 0.00 | −0.13 | −1.37 | +0.29 |
| +10..+30% | +0.03 | −0.36 | 0.00 | −1.28 | −1.33 | −6.65 |
| +30..+60% | +0.30 | +0.04 | −0.86 | −0.12 | −1.35 | −1.28 |
| +60%+ | +0.12 | −0.62 | −2.17 | −1.04 | −0.60 | −2.79 |

n — same axes (`*` cells above are the small-n ones):

| gain ↓ / ATR → | <4% | 4-6% | 6-8% | 8-10% | 10-14% | 14%+ |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| < −10% | 2300 | 3224 | 2348 | 1254 | 950 | 312 |
| −10..0% | 14119 | 6822 | 2382 | 815 | 417 | 51 |
| 0..+10% | 15084 | 6784 | 2167 | 769 | 385 | 68 |
| +10..+30% | 3396 | 3374 | 1832 | 739 | 501 | 90 |
| +30..+60% | 173 | 448 | 423 | 316 | 294 | 111 |
| +60%+ | 6 | 26 | 79 | 95 | 169 | 126 |

**The cross resolves it:**
1. **ATR% is a genuine, independent discriminator** — the PF gradient falls left→right within almost
   every gain row (cleanest in −10..0%: 1.15→1.14→1.07→1.02→0.96→0.63). Not a gain proxy.
2. **+30..+60% is the strongest hold cohort** and confirmed separable — PF 1.42 at <4% ATR, staying
   ≥1.05 out to 10-14%. A stock up 30-60% that is *still quiet* is the best thing to keep in the table.
3. **+60%+ toxicity is its OWN signal, not ATR% in disguise** — that row is sub-1 across *every* ATR
   column (0.41/0.96/0.89/0.93/0.81), so "up +60%" carries reversion beyond volatility.

**Combined map → the rule:** **hold while ATR% < ~8-10% AND gain < +60%; recycle otherwise** (ATR% ≥ 10%
or gain ≥ +60%). The +30-60% band rides *because* it is usually still quiet (it lives in the low-ATR
cells), not as a separate clause. This is the spec for the conditional time-stop to build next.

---

#### ⭐ On HIGH-EDGE breakouts the edge is concentrated in the first ~5 days (2026-06-19)

Hypothesis: the ATR%<6% quiet names are just low-edge grinders, and the real edge of this system is the
breakout *pop* in the first week. Test it on the high-edge population where the signal is strongest —
**production gate (rvol[6,20], move≥10%) + breadth (lag-1 pct_above_20 > 0.5)**, no price stop.

**System parameters:**
```
--no-stop --max-hold-bars N   (N ∈ {5,10,15,20,25,30})
entry gates: up>=0.10  rvol[6,20]  adv>=100000  price>=5  52w>=0.95  tight<4.00  atr%<0.11
+ breadth lag-1 pct_above_20 > 0.5 (post-hoc)   ≈ 2,230 trips
```

**Breadth-filtered time-stop sweep:**

| hold | n | PF | net | win% |
| --- | ---: | ---: | ---: | ---: |
| **5d** | 2,233 | **1.859** | $501k | 53.2 |
| 10d | 2,227 | 1.847 | $658k | 53.5 |
| 15d | 2,224 | 1.668 | $626k | 53.9 |
| 20d | 2,222 | 1.654 | $700k | 54.6 |
| 25d | 2,216 | 1.596 | $713k | 53.7 |
| 30d | 2,208 | 1.606 | $787k | 52.7 |

**Segment decomposition** — split each 30d-hold trade's P&L into its first-5-bar segment (entry → close
at +5) vs the remainder (+5 → exit), PF each:

| segment | n | net $ | PF | share of P&L |
| --- | ---: | ---: | ---: | ---: |
| **first 5 bars** | 2,208 | $477k | **1.827** | **61%** |
| rest (+5 → exit) | 2,208 | $309k | 1.235 | 39% |
| whole trade | 2,208 | $787k | 1.606 | 100% |

**Confirmed — and the contrast with the loosened set is the whole point:**

| population | first-5 PF | rest PF | spread | first-5 share |
| --- | ---: | ---: | ---: | ---: |
| **high-edge** (rvol≥6, move≥10%, breadth) | **1.827** | 1.235 | **0.59** | 61% |
| loosened (rvol≥3, move≥5%) | 1.319 | 1.169 | 0.15 | 48% |

On the loosened set the first-5 and the rest are barely separable (1.32 vs 1.17) — which is why that
time-stop sweep was flat (1.35→1.36). On the **high-edge** breakouts the first 5 days are PF **1.83** and
the remainder collapses to **1.235** — a 0.59 spread, with the first week carrying **61% of the P&L on
~17% of the holding time**. The edge is the breakout *pop*; everything after is the low-edge grind (the
ATR%<6% quiet names held ~82 bars are exactly that). The "rest" isn't negative — just dilutive (drags the
blend from 1.86 → 1.61). So a **5-day time-stop captures the concentrated edge (PF 1.86) and recycles
capital ~6× faster** than a 30-day hold; holding longer earns more total dollars at a worse rate (the
capture-vs-efficiency tradeoff, now quantified on the population that matters).

---

#### ⭐ NEW DEFAULT — 5-day time-stop, NO price stop; the disaster exit is a SHORT setup (2026-06-19)

> **Superseded ceiling (2026-06-20):** the PF figures in this section (1.775 no-breadth; 1.859/1.881/1.848
> filtered, 2,233 trips) are the **tight < 4.0** numbers, which was the default *when this was written*. The
> tightness ceiling was later raised to **4.5** (the clean half of the capacity gain — see "The gate
> matters" in the filter-ceiling sweep below): the current default is **2,780 trips, PF 1.795, +$580k**
> (pre 1.769 / post 1.808). The 5d-time-stop / no-price-stop / ATR% < 0.11 facts in this section all still
> hold; only the tightness ceiling and the headline trip/PF numbers changed.

Acting on the whole stop-mechanics arc: **the new production default is a 5-bar time-stop with no
trailing price stop** (`defaultConfig`: `StopMode = NoStop`, `MaxHoldBars = 5`). Rationale, all
established above: moving stops around doesn't help; too-tight stops hurt; the edge is the breakout pop in
the first ~5 days; price stops only earn their keep at ATR% > ~8-10%. Simple, legible, and the best PF of
anything tested.

**New default (no flags), production gate:** stop = none, time-stop = 5d, **PF 1.775**, $738k, win 53.7%.
**+ breadth (lag-1 pct_above_20 > 0.5), era-split:**

| era | n | PF | net |
| --- | ---: | ---: | ---: |
| ALL | 2,233 | **1.859** | $501k |
| pre-2015 | 991 | 1.881 | $172k |
| post-2015 | 1,242 | 1.848 | $329k |

Near-identical across eras — no regime dependence, and well above every stop-based variant.

**The conditional DISASTER exit — tested, OFF by default, but a real SHORT signal.** Added
`StopMode`-independent `DisasterConfig` (CLI `--disaster-exit --disaster-atr --disaster-loss`): close at
next open when a held bar is BOTH volatile (**current-bar** log-ATR% > 0.10) AND under water (gain <
−0.10) — the one outright-negative-EV corner from the 2D gain×ATR grid. (Current-bar ATR% is fair: it's a
close-of-bar decision filling at the next open, no lookahead.)

As a *long exit it is redundant* under the 5d hold (PF 1.775 → 1.770 at ATR>10%, 1.765 at ATR>8% — all
slightly worse). It fires 128× and every one is a loss *by construction* (it only triggers on losers). The
real test is the **forward return of the names it cut**:

| disaster exits | n | fwd mean | fwd med | fwd PF |
| --- | ---: | ---: | ---: | ---: |
| +5d | 127 | −0.16% | −0.63% | 0.974 |
| +10d | 127 | −2.51% | −4.91% | **0.703** |
| +20d | 127 | +0.70% | −4.13% | 1.072 |

So the disaster names **do keep falling** — fwd-10d PF **0.703**, median **−4.9%**. The signal is correct;
it's just redundant as a *long* exit because the 5d time-stop already caps the exposure window before the
−4.9% continuation compounds (at +5d it's only ~neutral, 0.974 — the bar or two we'd save isn't worth it).
**But fwd-10d PF 0.70 / −4.9% median is a genuine SHORT setup** — a blown-out, under-water ex-momentum
name. Caveat (per the short-side policy): borrow cost + spike risk make these a daytrade/short-horizon
idea, not an overnight hold. The disaster-exit code is kept off-by-default as the substrate for that study.

**Engine note:** flipping `defaultConfig.StopMode → NoStop` also required the CLI no-stop-flag fallback to
read `defaultConfig.StopMode` (was hardcoded `WindowLow`); confirmed the no-flag run now prints
`stop mode = none` and matches the pure-timestop-5 number (PF 1.775).

---

#### ⭐ ATR% × tightness grid under the TIME-STOP — the quiet/tight corner is the book, not a dead zone (2026-06-20)

> **Why this matters.** Under the old trailing-stop systems (window-low, ATR%-ratchet) we concluded that
> very-low-ATR% / very-low-tightness names "had no edge beyond a certain point." This re-test shows that
> conclusion was an **artifact of the stop being too tight on quiet names** — the stop sat *inside* their
> own noise band and chopped them out before the move played. Swap the trailing stop for the 5-day
> time-stop (current default) and those same quiet/tight names become the **core profit engine**. The
> entry filters should cut the *opposite* corner (high-ATR% + loose base), not the quiet one.

**Population:** default exit (5d time-stop, NO price stop), entry filters *loosened to populate the grid*
(`--rvol-min 3 --rvol-max 20 --up-threshold 0.05 --max-atr-pct 100 --max-tightness 100`), breadth applied
post-hoc (`LAG(pct_above_20) > 0.5`). 35,858 raw trips → ~26.5k after breadth. ATR%/tightness are the
engine's **log-space** values (ATR% median 0.038, tightness median 3.9). PF = Σwin/|Σloss| over net P&L.

**ATR% marginal** — edge is *highest* at the bottom and decays monotonically; only the `14%+` tail is negative:

| ATR% (log) | n | win% | mean $ | med $ | PF | total $ |
|---|--:|--:|--:|--:|--:|--:|
| <4%    | 12,631 | 50.2 | 73 | 4 | **1.371** | +928k |
| 4–6%   | 5,448 | 50.9 | 117 | 18 | **1.375** | +640k |
| 6–8%   | 2,374 | 46.6 | 106 | −87 | 1.223 | +253k |
| 8–10%  | 1,262 | 47.3 | 172 | −91 | 1.259 | +216k |
| 10–14% | 1,114 | 41.6 | 47 | −368 | 1.05 | +52k |
| **14%+** | 784 | 30.9 | −694 | −1273 | **0.615** | **−544k** |

> This **directly reverses** the trailing-stop-era read. The quiet `<6%` ATR% names (73% of all trips) are
> the strongest cells, not the weakest. They were never edge-less — they were stop-victims.

**Tightness marginal** — tighter is better, monotonically; edge dies above ~5.5 (looser = worse):

| tightness (log) | n | win% | mean $ | med $ | PF | total $ |
|---|--:|--:|--:|--:|--:|--:|
| <2.5    | 1,123 | 46.9 | 22 | −31 | 1.087 | +24k |
| 2.5–3.5 | 6,824 | 50.9 | 133 | 12 | **1.481** | +910k |
| 3.5–4.5 | 6,655 | 50.8 | 102 | 13 | 1.353 | +679k |
| 4.5–5.5 | 4,386 | 48.9 | 105 | −10 | 1.282 | +459k |
| 5.5–7   | 3,117 | 45.1 | −15 | −83 | 0.969 | −47k |
| **7+**  | 1,508 | 39.7 | −320 | −228 | **0.654** | **−482k** |

> Peak is the **tight** `2.5–3.5` cell (PF 1.481). The ultra-tight `<2.5` column is weak/noisy (small n,
> barely-moved names) — don't over-read it. The real loser is the loose `7+` tail.

**2D PF grid — ATR% (rows) × tightness (cols):**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 1.01 | **1.47** | 1.18 | **1.83** | 1.17 | 1.15 |
| **4–6**   | 1.18 | 1.38 | **1.58** | 1.40 | 1.26 | 0.97 |
| **6–8**   | 1.29 | 1.38 | **1.68** | 1.03 | 0.88 | 0.89 |
| **8–10**  | 2.37* | 2.18* | 1.36 | 1.12 | 0.96 | 0.65 |
| **10–14** | 0.52* | 1.19 | 1.34 | 0.92 | 0.92 | 1.03 |
| **14+**   | 0.24* | 1.70* | 0.93 | 0.93 | 0.63 | **0.38** |

`*` = thin cell (n < 50); ignore. Cell **n** and **total $** below.

**n grid:**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 769 | 4308 | 3893 | 2149 | 1174 | 338 |
| **4–6**   | 235 | 1530 | 1527 | 1061 | 790 | 305 |
| **6–8**   | 82 | 560 | 624 | 520 | 392 | 196 |
| **8–10**  | 20 | 243 | 294 | 287 | 293 | 125 |
| **10–14** | 12 | 143 | 235 | 231 | 283 | 210 |
| **14+**   | 5 | 40 | 82 | 138 | 185 | 334 |

**total $ grid:**

| ATR% ↓ \ tight → | <2.5 | 2.5–3.5 | 3.5–4.5 | 4.5–5.5 | 5.5–7 | 7+ |
|---|--:|--:|--:|--:|--:|--:|
| **<4**    | 2k | **+395k** | +133k | **+338k** | +48k | +12k |
| **4–6**   | +13k | +183k | **+252k** | +125k | +70k | −4k |
| **6–8**   | +8k | +95k | **+178k** | +8k | −24k | −12k |
| **8–10**  | +10k | +176k | +59k | +23k | −8k | −42k |
| **10–14** | −4k | +21k | +66k | −20k | −18k | +6k |
| **14+**   | −5k | +40k | −9k | −14k | −114k | **−442k** |

**Verdict:**
- **The mass and the money live in the quiet/tight top-left quadrant** (ATR% < 6%, tightness 2.5–4.5):
  `<4% × 2.5–3.5` (n=4308, PF 1.47, +$395k) and `<4% × 4.5–5.5` (PF 1.83, +$338k) are the two biggest
  P&L cells; `4–6% × 3.5–4.5` (+$252k) and `6–8% × 3.5–4.5` (PF 1.68) round it out. This is precisely the
  region the trailing stops killed.
- **The loss engine is the high-ATR% + loose-base corner.** The single `14%+ × 7+` cell is **−$442k / PF
  0.38** — essentially the entire system loss. The whole bottom row (ATR% 14+) and right column
  (tightness 7+) are where the cut belongs.
- **Implication for the filters:** the current `MaxAtrPct = 0.11` cap is roughly right (it trims the
  worst of the 14%+ row); a `tightness < ~7` cap would shed the loose-base loser without touching the
  core. There is **no case for an ATR%-floor or tightness-floor** — the quiet/tight cells are the edge.
- **Why it reverses the old read:** a trailing stop on a 3% -ATR name triggers on a single average down-day;
  the time-stop simply holds the 5-day window and lets the breakout resolve. Quiet names were never
  edge-less — the exit was eating them.

**Era split (pre/post 2015-01-01) — the pattern is era-robust, not a single-regime artifact:**

| ATR% | n pre | PF pre | n post | PF post |   | tightness | n pre | PF pre | n post | PF post |
|---|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| <6%    | 9011 | **1.528** | 9068 | 1.241 |   | <3.5    | 3565 | **1.613** | 4382 | 1.323 |
| 6–10%  | 1048 | 1.307 | 2588 | 1.215 |   | 3.5–5.5 | 4940 | 1.528 | 6101 | 1.218 |
| 10–14% | 220 | 1.10 | 894 | 1.04 |   | 5.5–7   | 1318 | 1.224 | 1799 | 0.866 |
| 14%+   | 107 | **0.485** | 677 | **0.63** |   | 7+      | 563 | **0.714** | 945 | **0.635** |

Both eras: monotone decay with ATR%, monotone decay with looseness, the quiet/tight cells strongest and
the high-ATR%/loose tail the only sub-1.0 cell. The reversal of the old trailing-stop conclusion is not a
regime fluke.

---

#### Filter-ceiling sweep — the current ATR% < 0.11 / tight < 4.0 ceilings are already on the post-2015 optimum (2026-06-20)

> Follow-up to the grid above: given that the quiet/tight corner is the book, *should we move the entry
> ceilings?* Tested candidate ATR% and tightness ceilings on the **loose gate** (price% ≥ 0.05, rvol [3,20],
> breadth on — same population as the grid). The answer is **no — the current ceilings are the optimum.**
> Both axes were swept; mean-$/trade is the quality tell (PF can be lifted by a few fat-tail survivors).

**ATR% ceiling sweep (holding tight < 5.5)** — total P&L *peaks at 0.11* and declines on both sides:

| ATR% ceiling | n | PF | total $ | mean $ |
|---|--:|--:|--:|--:|
| < 0.08 | 17,258 | 1.406 | +1.73M | 100 |
| < 0.10 | 18,102 | 1.418 | +2.00M | 110 |
| **< 0.11** | 18,349 | **1.423** | **+2.09M** | **114** |
| < 0.12 | 18,539 | 1.403 | +2.07M | 112 |
| < 0.13 | 18,648 | 1.394 | +2.06M | 110 |
| < 0.14 | 18,723 | 1.386 | +2.06M | 110 |

Below 0.11 you cut the productive 8–10% ATR band (P&L drops); above 0.11 you add net-negative 12–14% names
(PF, total $ AND mean-$ all drop — going 0.11→0.12 adds 190 trips but *loses* $22k). **0.11 is a clean
interior optimum on both sides.** Consistent with the grid: 10–14% is break-even, 14%+ is the −$442k loss
engine.

**Tightness ceiling sweep (holding ATR% < 0.11)** — *non-monotonic*, and that's the trap:

| tight ceiling | n | PF | total $ | mean $ |
|---|--:|--:|--:|--:|
| **< 4.0 (current)** | 11,375 | **1.441** | +1.29M | 114 |
| < 4.5 | 14,245 | 1.432 | +1.59M | 111 |
| < 5.0 | 16,573 | 1.399 | +1.75M | 105 |
| < 5.5 | 18,349 | 1.423 | +2.09M | 114 |
| < 6.0 | 19,602 | 1.400 | +2.16M | 110 |

The < 5.5 ceiling *looks* like a free +62% P&L at flat mean-$ (1.423 vs 1.441). But it's non-monotone — PF
dips at 5.0 then recovers at 5.5 — which means a sub-band is carrying it. The **non-cumulative tightness
bands** (ATR% < 0.11) expose the lump:

| tightness band | n | PF | mean $ |
|---|--:|--:|--:|
| < 4.0 | 11,375 | 1.441 | 114 |
| 4.0–4.5 | 2,870 | 1.395 | 102 |
| **4.5–5.0** | 2,328 | **1.231** | **70** ← weak |
| **5.0–5.5** | 1,776 | **1.608** | **194** ← spike |
| 5.5–6.0 | 1,253 | 1.141 | 49 |
| 6.0+ | 2,547 | 0.978 | −9 |

The entire < 5.5 case rests on the **5.0–5.5 spike (PF 1.608, mean $194)** — sandwiched next to the *worst*
qualifying band (4.5–5.0). To reach the spike you must swallow the dip.

**⛔ Era split kills the spike — it's a pre-2015 artifact:**

| tightness band | n pre | PF pre | mean pre | n post | PF post | mean post |
|---|--:|--:|--:|--:|--:|--:|
| < 4.0 | 5228 | 1.564 | 123 | 6147 | **1.363** | 105 |
| 4.0–4.5 | 1342 | 1.386 | 83 | 1528 | 1.401 | 119 |
| 4.5–5.0 | 1087 | 1.273 | 70 | 1241 | 1.203 | 70 |
| **5.0–5.5** | 784 | **2.205** | **336** | 992 | **1.232** | **82** |
| 5.5–6.0 | 554 | 1.363 | 106 | 699 | 1.012 | 5 |
| 6.0+ | 1169 | 1.10 | 35 | 1378 | 0.908 | −47 |

The 5.0–5.5 spike is **PF 2.205 / mean $336 pre-2015** but reverts to **PF 1.232 / mean $82 post-2015** —
*below* the core < 4.0 band's post-2015 PF of 1.363. Cumulatively, loosening 4.0 → 5.5 leaves pre-2015 PF
flat (1.564 → 1.568) but **degrades post-2015 PF (1.363 → 1.331)** — it makes the system worse in the only
era that matters for live trading. Every loose band (4.5–5.0, 5.5–6.0, 6.0+) is sub-1.4 or negative
post-2015; there is **no robust loose-tightness edge in the modern era.**

> **⚠️ This loose-gate verdict does NOT carry to the production gate — see below.** On the loose gate the
> 5.0–5.5 spike reverts to junk post-2015, so loosening *there* is a mirage. But the production move/rvol
> floor cleans up exactly those names, and on the gate that actually ships the loosening holds.

**The gate matters — on the PRODUCTION gate, tight < 5.5 holds out-of-sample (2026-06-20).** Re-ran the
tightness decision on the real default gate (price% ≥ 0.10, rvol [6,20], ATR% < 0.11, breadth, ≥2005):

| tightness | n | PF | total $ | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|--:|
| **< 4.0** (old default) | 2,233 | **1.859** | +501k | 225 | 1.881 | **1.848** |
| **< 4.5** (NEW default) | 2,780 | 1.795 | +580k | 209 | 1.769 | **1.808** |
| < 5.5 (max-capacity) | 3,550 | 1.711 | +701k | 197 | 1.775 | 1.678 |

Non-cumulative bands, **production gate**, with era split — the 5.0–5.5 band that reverted to junk on the
loose gate stays *healthy* here:

| tightness band | n | PF | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|
| < 4.0 | 2,233 | 1.859 | 225 | 1.881 | 1.848 |
| 4.0–4.5 | 547 | 1.538 | 143 | 1.355 | 1.643 |
| **4.5–5.0** | 446 | 1.343 | 117 | 1.589 | 1.222 ← soft spot |
| **5.0–5.5** | 324 | 1.660 | 212 | 2.106 | **1.454** ← still good post-2015 |
| 5.5+ | 681 | 1.121 | 58 | 1.206 | 1.080 |

Compare the 5.0–5.5 band across gates: **loose gate** PF 2.205 pre → **1.232** post (reverts); **production
gate** PF 2.106 pre → **1.454** post (holds). The move/rvol floor is what keeps the loose-tightness names
clean. The 4.5–5.0 band is the soft spot (post 1.222) we swallow to reach the good 5.0–5.5 band, but the
*aggregate* < 5.5 survives out-of-sample (post-2015 PF 1.678, mean $197).

**✅ DECISION (2026-06-20): RAISE the default tightness ceiling 4.0 → 4.5 — the clean half of the capacity
gain.** The bands above show the increments aren't uniform: the **4.0–4.5 band is clean** (post-2015 PF
1.808 ≈ the < 4.0 core's 1.848) but the **next band 4.5–5.0 is the soft spot** (post-2015 PF 1.222). So 4.5
takes **+24% trips (2,233 → 2,780) and +$79k P&L for almost no quality loss** (PF 1.859 → 1.795, post-2015
1.848 → **1.808**, mean $225 → $209) — and *stops before the drag*. `MaxTightness = 4.5` in `defaultConfig`
(engine verified: 2,780 filtered trips / PF 1.795 / +$579,714).
**Why not 5.5:** it adds more capacity (3,550 trips, +$701k) but pays for it — post-2015 PF 1.678, mean $197
— by carrying the 4.5–5.0 soft band *plus* the spiky 5.0–5.5 one (PF 1.45 post, but pre 2.11 = mostly an
early-era artifact even on the prod gate). 5.5 stays documented as the **max-capacity** alternative; **< 4.0**
as the **max-PF / min-drawdown** alternative (PF 1.859, post 1.848).
**ATR% < 0.11 stays put** — clean interior optimum; tightening it (toward 0.06–0.08) is strictly worse
(cuts the time-stop-rescued 8–10% band).

> **⏭️ MOVED TO v3.** The clipped re-derivation of the tightness & ATR% ceilings (2026-06-21) — the
> tightness *hump* peaking at 4.5, the ATR% tightening 0.11 → **0.10**, and the 2D joint-ceiling grid —
> now lives in [`momentum_v3_results.md`](momentum_v3_results.md) § *Entry-filter geometry*, computed under
> the +50% winner-clip / cumulative standard. The v2 tables above are the raw-`net_pnl` originals; the
> clipped versions superseded the ATR% decision (default is now **0.10**, not 0.11).

---

#### ⭐ Past-runner personality — a 6-month volatility/momentum HISTORY predicts the next breakout (2026-06-20)

> **Thesis (Tim Sykes / chart-reading lore):** momentum stocks keep being momentum stocks; boring stocks
> stay boring. Test it directly — for each entry, measure the stock's volatility/momentum *personality*
> over the **trailing 6 months (126 trading days)**, then break the default-system trips down by it. The
> measures are the trailing-126d **max of a 14-day window stat**, sampled **as-of the signal date and
> lagged 1 bar** (no lookahead — the signal bar's own range is excluded). Three measures:
> - **max ADR 6mo** = max over 126d of `mean₁₄(high/low − 1)` (range-based, the literal Sykes ADR)
> - **max ATR% 6mo** = max over 126d of `mean₁₄(true_range/close)` (gap-aware cousin of ADR)
> - **max ret 6mo** = max over 126d of `close_t/close_{t−14} − 1` (directional 14d burst — the "slope")
>
> Population: default trips (5d time-stop, tight < 4.5, ATR% < 0.11), breadth on, ≥2005 (~2,580 trips).

**All three sort the book monotonically — strongly. The verdict: yes, past runners run again.**

| max ADR 6mo | n | win% | mean $ | PF |   | max ret 6mo | n | win% | mean $ | PF |
|---|--:|--:|--:|--:|---|---|--:|--:|--:|--:|
| <4%    | 498 | 49.8 | −8 | **0.955** |   | <15%    | 417 | 52.3 | 77 | 1.428 |
| 4–6%   | 796 | 52.6 | 76 | 1.345 |   | 15–30%  | 1100 | 53.4 | 106 | 1.481 |
| 6–8%   | 569 | 54.7 | 187 | 1.821 |   | 30–50%  | 705 | 54.8 | 194 | 1.671 |
| 8–11%  | 427 | 55.3 | 221 | 1.703 |   | 50–80%  | 354 | 52.0 | 276 | 1.832 |
| 11–15% | 237 | 53.6 | 382 | 1.978 |   | 80–130% | 118 | 55.1 | 520 | 2.548 |
| **15%+** | 253 | 53.4 | **918** | **3.126** |   | **130%+** | 86 | 41.9 | **1,579** | **3.599** |

(max ATR% 6mo is near-identical to ADR: PF 1.11 → 1.26 → 1.66 → 1.75 → 2.19 → **2.91**.)

- The **bottom ADR/ATR bucket is dead** (PF 0.955 / mean −$8) — stocks with no volatile fortnight in the
  prior 6 months are untradeable under our system. The top bucket is **PF 3.13 / $918 a trade**.
- **max ret (the slope) is the most interesting measure** — even its *bottom* bucket is profitable
  (PF 1.428: a clean directional run leaves edge even when the range was tame), and its top bucket is the
  strongest of all (**PF 3.60 / $1,579**). It captures directional momentum personality specifically.
- **Fat-tail caveat:** the top buckets win on magnitude, not frequency — max-ret `130%+` has win% **41.9**
  but PF 3.60 (few winners, huge). Real edge, but lumpy/higher-variance to trade. n is also thin up top
  (86–253) — directional, not precise.

**Era split — holds in both eras (not a regime artifact):**

| max ret 6mo | PF pre | PF post |   | max ADR 6mo | PF pre | PF post |
|---|--:|--:|---|---|--:|--:|
| <15%    | — | 1.28 |   | <4%    | 1.197 | **0.721** |
| 15–30%  | 1.813 | 1.277 |   | 4–6%   | 1.829 | 1.033 |
| 30–50%  | 1.768 | 1.619 |   | 6–8%   | 1.742 | 1.880 |
| 50–80%  | 1.278 | 2.056 |   | 8–11%  | 1.441 | 1.835 |
| 80–130% | 2.749 | 2.489 |   | 11–15% | 1.529 | 2.131 |
| 130%+   | **4.676** | **3.467** |   | 15%+   | **5.247** | **2.856** |

Monotone in both eras; the ADR `<4%` bottom is actually *worse* post-2015 (PF 0.72 — boring names have
gotten even less tradeable).

**⭐ It's INDEPENDENT of the entry-ATR% filter — the two are complementary, not redundant.** The obvious
worry is that "past runner" just re-discovers "volatile entry." It does not — max-ret sorts *within* fixed
entry-ATR% strata:

| within entry-ATR% < 0.06 (quiet entries, n=2,277) | n | PF |   | within entry-ATR% ≥ 0.08 (volatile entries, n=186) | n | PF |
|---|--:|--:|---|---|--:|--:|
| max-ret < 30% | 1453 | 1.501 |   | max-ret < 30% | 21 | **0.983** |
| max-ret 30–80% | 764 | 1.376 |   | max-ret 30–80% | 87 | 2.451 |
| max-ret > 80% | 60 | **1.701** |   | max-ret > 80% | 78 | **4.875** |

The volatile-entry row is the punchline: **a volatile entry with NO prior run is break-even junk (PF 0.98,
mean −$12); a volatile entry WITH a prior run is PF 4.88 ($2,025/trade).** Today's volatility only pays
when the stock has a *history* of running — a momentary spike on an otherwise-boring name is a fakeout. The
entry-ATR% filter and the past-runner signal stack.

**Implication / next step:** a **max-ret-6mo (or max-ADR) entry floor or sizing input** is a strong
candidate — it adds orthogonal signal to the current ATR%/tightness gates, especially as a *partner* to
entry-ATR% (cut volatile-but-no-history entries, which are pure fakeouts). max-ret looks like the better measure.
**It is a genuine edge under the full default** — monotone and era-robust, strongest at the top, on both
the production AND the loose gate once the entry ATR%/tightness caps are applied (see the gate-amplified
result below; the earlier "loose-gate inversion" was a caps-off artifact). Not yet wired into the engine —
to be designed and swept next; mind the thin top-decile n and the win%-vs-PF fat tail.

#### The past-runner edge is GATE-AMPLIFIED, monotone on BOTH gates — with the ATR%/tightness caps ON (2026-06-20)

> ⚠️ **Corrected.** The first version of this section read the loose-gate deciles off the loose CSV with
> the entry **ATR%/tightness caps OFF** (disabled to populate the grid), and wrongly concluded the loose
> top decile "inverts to a net loser." That was an artifact: the `ATR% < 0.11` cap removes the violent-
> history blow-off cohort, and *with the cap on* the inversion vanishes. Re-ran with even-sized **deciles**
> (NTILE 10) and the **real entry caps applied on both gates** (ATR% < 0.11, tight < 4.5). Keep the two
> stronger measures — **max ATR% 6mo** (true-range) and **max ret/slope** — dropping range-based max ADR.

**PRODUCTION gate (caps on) — clean monotone rise, top decile strongest:**

| decile | max ATR% 6mo: PF | post | | max ret/slope: PF | post |
|---|--:|--:|---|--:|--:|
| D1 (lowest) | 1.155 | 0.949 | | 1.48 | 1.554 |
| D5 | 1.825 | 1.651 | | 1.825 | 1.668 |
| D6 | 1.797 | 1.756 | | 2.047 | 1.995 |
| D9 | 1.965 | 2.100 | | 1.989 | 2.328 |
| **D10 (highest)** | **2.834** (mean $822) | **2.647** | | **2.697** (mean $749) | **2.676** |

**LOOSE gate (caps on) — also monotone-up; top decile is the STRONGEST, NOT a loser:**

| decile | max ATR% 6mo: PF | post | | max ret/slope: PF | post |
|---|--:|--:|---|--:|--:|
| D1 (lowest) | 1.112 | 1.274 | | 1.297 | 1.239 |
| D5 | 1.219 | 0.995 | | 1.264 | 1.217 |
| D6 | 2.111 | 1.261 | | 1.232 | 1.192 |
| D9 | 1.538 | 1.497 | | 1.563 | 1.703 |
| **D10 (highest)** | **1.874** (mean $380) | **1.777** | | **1.706** (mean $314) | **1.692** |

**It's gate-AMPLIFIED, not gate-dependent.** With the caps on, the top decile is the best cell on *both*
gates and never a loser — the production gate just sharpens it (D10 ~2.7–2.8 vs loose ~1.7–1.9). The earlier
"loose-gate inversion" was entirely the missing ATR% cap letting the violent-history fakeout tail back in.
The `ATR% < 0.11` entry cap and the past-runner signal point the same way: both want a stock with a real
volatility history but *not* the uncapped blow-off extreme.

**Upshot:** max-ATR%/max-ret is a genuine **edge under the full default** (caps + gate), monotone and
era-robust, strongest at the top. The production gate amplifies it but the loose gate doesn't kill it — so a
max-ret floor is a sound signal to build *on top of* the existing entry. (Note a stray **D6 spike** in a
couple of cells, PF ~2 driven *pre-2015* — a concentration; don't over-read single deciles.)

#### Which half of the gate matters? → both floors help and STACK — with the ATR%/tightness caps ON (2026-06-20)

> ⚠️ **Correction.** An earlier version of this section ran the move/rvol decomposition on the loose CSV
> with the entry **ATR%/tightness caps OFF** (they'd been disabled to populate the volatility grid). That
> was wrong — the `ATR% < 0.11` cap already removes the violent-history fakeout tail, so the caps-off run
> mismeasured both the level *and the shape* of the gate effect (it falsely showed the move floor doing
> nothing, the top quintile losing, and production *worse* than rvol-only). **Redone here with the real
> caps applied throughout** (ATR% < 0.11, tight < 4.5); only move/rvol are varied. Quintiles (NTILE 5),
> breadth + ≥2005.

**Whole-system PF by gate (ATR%/tightness caps ON):**

| gate | n | PF |
|---|--:|--:|
| loose: move ≥ 5%, rvol [3,20] | 14,245 | 1.432 |
| move-only: move ≥ 10%, rvol [3,20] | 6,524 | **1.681** |
| rvol-only: move ≥ 5%, rvol [6,20] | 4,194 | **1.643** |
| **PROD**: move ≥ 10%, rvol [6,20] | 2,780 | **1.795** |

**Top-quintile (Q5) PF of the slope measure, across the four gates:**

| gate | Q5 PF | Q5 post | Q5 mean $ |
|---|--:|--:|--:|
| loose: move ≥ 5%, rvol [3,20] | 1.645 | 1.697 | +250 |
| move-only: move ≥ 10%, rvol [3,20] | 1.882 | 1.854 | +373 |
| rvol-only: move ≥ 5%, rvol [6,20] | 2.107 | 2.246 | +413 |
| **PROD**: move ≥ 10%, rvol [6,20] | **2.404** | **2.547** | **+528** |

**Findings (caps on — the real system):**
1. **Both floors help and they STACK cleanly.** Whole-system: 1.432 → move-only 1.681 / rvol-only 1.643 →
   prod 1.795. Slope Q5: 1.645 → 1.882 / 2.107 → **2.404**. Each tightening step raises PF; no inversion.
   The move and rvol floors contribute comparably (rvol a touch more at the top), and combine additively.
2. **The top quintile is NEVER a loser with the caps on** — even on the loose gate it's PF 1.645 (+$250),
   vs the broken caps-off run's −$125. The `ATR% < 0.11` cap is what removes the violent-history blow-off
   cohort that made the top decile invert; once it's gone, "more past-runner is better" holds at the top.
3. **The full PROD slope ladder is monotone and era-robust:** Q1 1.30 → Q3 1.94 → Q5 **2.40** (post-2015
   Q5 **2.55**). Under the real default, the past-runner edge is strongest at the extreme — opposite of the
   caps-off artifact.

**Design implication (revised):** a max-ret/max-ATR signal works *on top of* the full production entry
(both floors + ATR%/tightness caps), and **"higher is better" holds** — a floor on max-ret is sensible, no
need for a band/cap. The rvol and move floors are both pulling their weight; neither is redundant.

#### Full quintile tables — both measures × all three sub-systems (caps + breadth ON, 2026-06-20)

> The full picture behind the Q5-only summary above. Quintiles (NTILE 5) of each measure, **breadth applied
> throughout** (lag-1 pct_above_20 > 0.5), ATR% < 0.11, tight < 4.5, ≥2005. PF / pre-2015 / post-2015.

**LOOSE (move ≥ 5%, rvol [3,20], ~2,849/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.054 | 1.126 | | 1.280 | 1.278 |
| Q2 | 1.017 | 0.963 | | 1.230 | 1.063 |
| Q3 | 1.668 | 1.130 | | 1.246 | 1.203 |
| Q4 | 1.345 | 1.395 | | 1.519 | 1.229 |
| **Q5** | **1.719** | 1.657 | | **1.645** | 1.697 |

**MOVE-ONLY (move ≥ 10%, rvol [3,20], ~1,305/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.009 | 0.843 | | 1.271 | 1.038 |
| Q2 | 1.095 | 0.946 | | 1.248 | 1.114 |
| Q3 | 2.237 | 1.281 | | 2.330 | 1.455 |
| Q4 | 1.569 | 1.635 | | 1.487 | 1.539 |
| **Q5** | **2.052** | 1.950 | | **1.882** | 1.854 |

**RVOL-ONLY (move ≥ 5%, rvol [6,20], ~839/quintile):**

| Q | max ATR%: PF | post | | max slope: PF | post |
|---|--:|--:|---|--:|--:|
| Q1 | 1.067 | 1.122 | | 1.255 | 1.241 |
| Q2 | 1.200 | 1.096 | | 1.486 | 1.142 |
| Q3 | 1.521 | 1.227 | | 1.559 | 1.484 |
| Q4 | 1.617 | 1.691 | | 1.444 | 1.429 |
| **Q5** | **2.182** | **2.108** | | **2.107** | **2.246** |

Q5 is the strongest quintile in all six. **max ATR% edges max slope at Q5** in every gate (mildly, within
noise). **rvol-only is the most durable partner** — its Q5 is strongest *and* holds best post-2015 (ATR%
2.11, slope 2.25), while move-only leans pre-2015 (its Q3 spike is pre-2015 PF ~3.8–4.2). The bottom
quintiles diverge: move-only Q1–Q2 go *negative* post-2015 (0.84/0.95 — modest-history names clearing 10%
on noise); rvol-only keeps them positive (1.12/1.10). rvol confirmation cleans the low end better.

#### ⭐ Within-Q5 breakdown — the edge KEEPS rising into the top 4%: use a FLOOR, not a band (2026-06-20)

> Re-split *just the top quintile* of each measure into 5 sub-quantiles (so sub-Q5 ≈ the top 4% of the whole
> population) — to decide floor-vs-band. If the edge saturates/reverts inside Q5, a band/cap is right; if it
> keeps climbing, a floor is right.

Each table = the top quintile (Q5) of that measure, re-split into 5 even sub-quantiles. `range` = the
measure's lo–hi within the sub-bucket. mean $ / PF / pre-2015 / post-2015.

**LOOSE — within-Q5 of max ATR% (n≈570/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.095–0.105 | 172 | 1.491 | 1.952 | 1.315 |
| 2 | 0.105–0.119 | 153 | 1.379 | 1.270 | 1.434 |
| 3 | 0.119–0.142 | 408 | 2.108 | 2.451 | 2.002 |
| 4 | 0.142–0.188 | 229 | 1.586 | 2.454 | 1.386 |
| **5** | 0.188–2.74 | **500** | **1.964** | 1.900 | **1.979** |

**LOOSE — within-Q5 of max slope (n≈570/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.442–0.497 | 264 | 1.925 | 1.538 | 2.196 |
| 2 | 0.497–0.575 | 73 | 1.193 | 0.892 | 1.363 |
| 3 | 0.575–0.700 | 242 | 1.676 | 1.624 | 1.697 |
| 4 | 0.701–0.985 | 170 | 1.376 | 1.462 | 1.341 |
| **5** | 0.986–1654 | **503** | **2.077** | 2.453 | 1.990 |

**MOVE-ONLY — within-Q5 of max ATR% (n≈261/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.111–0.123 | 236 | 1.521 | 1.292 | 1.610 |
| 2 | 0.123–0.140 | 391 | 2.028 | 2.412 | 1.930 |
| 3 | 0.140–0.165 | 433 | 2.199 | 5.145 | 1.839 |
| 4 | 0.165–0.214 | 352 | 1.835 | 2.302 | 1.723 |
| **5** | 0.215–2.74 | **872** | **2.579** | 3.689 | **2.425** |

**MOVE-ONLY — within-Q5 of max slope (n≈261/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.538–0.604 | 189 | 1.580 | 1.687 | 1.540 |
| 2 | 0.604–0.706 | 337 | 1.872 | 1.607 | 1.959 |
| 3 | 0.707–0.857 | 151 | 1.346 | 1.633 | 1.266 |
| 4 | 0.857–1.293 | 375 | 1.934 | 2.893 | 1.727 |
| **5** | 1.297–656 | **816** | **2.441** | 2.373 | **2.453** |

**RVOL-ONLY — within-Q5 of max ATR% (n≈168/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.097–0.107 | 165 | 1.458 | 1.442 | 1.465 |
| 2 | 0.107–0.122 | 293 | 1.769 | 0.945 | 2.266 |
| 3 | 0.123–0.145 | 496 | 2.513 | 3.599 | 2.180 |
| 4 | 0.146–0.192 | 445 | 2.288 | 4.025 | 2.069 |
| **5** | 0.192–2.00 | **975** | **2.645** | 6.741 | **2.326** |

**RVOL-ONLY — within-Q5 of max slope (n≈168/sub):**

| sub-Q | range | mean $ | PF | pre | post |
|---|---|--:|--:|--:|--:|
| 1 | 0.449–0.500 | 191 | 1.581 | 1.250 | 1.920 |
| 2 | 0.501–0.575 | 179 | 1.557 | 0.870 | 1.960 |
| 3 | 0.576–0.700 | 351 | 2.185 | 1.843 | 2.286 |
| 4 | 0.701–0.960 | 298 | 1.752 | 1.219 | 1.923 |
| **5** | 0.962–656 | **1,051** | **3.003** | 4.720 | **2.693** |

In all six, **sub-Q5 is the highest-PF and highest-mean-$ sub-bucket** — no saturation, no extreme
reversion. Mean P&L roughly *doubles* sub-Q1 → sub-Q5 in the conviction-gated systems ($165 → $975 ATR%
rvol-only; $191 → $1,051 slope rvol-only). **"More is better" holds into the top 4% → a FLOOR is the right
structure, not a band/cap; the higher the floor, the better the per-trade quality (trading count for PF).**
Caveats: the within-Q5 middle is lumpy (sub-Q2 sags in a few — sample noise at n~170–570); rvol-only sub-Q5
is thin (n=167, ~100+ post-2015) so a real floor must be re-validated on a fresh full-history run, not this
grid CSV; read the post-2015 column (pre-2015 sub-Q5 PFs of 4–7 are small-n).

**Net design call:** pair a **max-ATR%-6mo (or max-slope-6mo) FLOOR** with **rvol ≥ 6** — rvol-only Q5 is
both the strongest and the most post-2015-durable, and the within-Q5 edge is monotone there. max ATR% is
the marginally better sort variable; max slope is close and more intuitive. Next: wire one measure into the
engine as an entry floor and sweep the threshold (same count-vs-PF trade as the tightness ceiling).

#### ⭐ Entry-day move analysis — a SWEET-SPOT NOTCH: 25–30% is the best band, 30–40% the worst (2026-06-20)

> Does raising the entry-day move threshold (default 10%) help, and where does it stop? Swept the floor
> post-hoc on the loose CSV holding everything else at default (ATR% < 0.11, tight < 4.5, breadth, ≥2005),
> for **both rvol gates** (≥6 production, ≥3 loose). PF / mean-$ / pre / post-2015; watch n thinning.

**Production rvol [6,20]:**

| move floor | n | PF | mean $ | total $ | PF post |
|---|--:|--:|--:|--:|--:|
| **0.10 (current)** | 2,780 | 1.795 | 209 | 580k | 1.808 |
| 0.125 | 2,202 | 1.847 | 235 | 517k | 1.916 |
| 0.15 | 1,698 | 1.887 | 260 | 442k | 1.977 |
| 0.175 | 1,301 | 1.930 | 290 | 378k | 2.034 |
| 0.20 | 979 | 1.978 | 331 | 324k | 2.109 |
| 0.25 | 593 | 2.156 | 422 | 250k | 2.274 |
| 0.275 | 470 | **2.306** | **479** | 225k | **2.458** |
| 0.30 | 364 | **1.536** | 209 | 76k | **1.460** |
| 0.40 | 142 | 1.942 | 404 | 57k | 1.639 (pre 4.03) |

**Loose rvol [3,20]:**

| move floor | n | PF | mean $ | total $ | PF post |
|---|--:|--:|--:|--:|--:|
| 0.10 | 6,524 | 1.681 | 195 | 1.27M | 1.508 |
| 0.15 | 3,061 | 1.676 | 218 | 666k | 1.625 |
| 0.20 | 1,427 | 1.832 | 306 | 436k | 1.870 |
| 0.25 | 771 | 1.904 | 371 | 286k | 1.985 |
| 0.275 | 582 | **2.037** | **415** | 242k | **2.217** |
| 0.30 | 436 | **1.493** | 209 | 91k | **1.488** |
| 0.40 | 163 | 1.770 | 354 | 58k | 1.600 (pre 2.62) |

The cumulative floor (above) shows where to set a *threshold*, but it can't locate the actual cliff —
a 0.30 floor pools *all* ≥30% names, so its low PF is dominated by the many 30–40% trades and hides what
happens *at* each move level. **Non-cumulative bands (below) are the honest read** of where the edge lives.

**Non-cumulative move bands (production gate, move ≥ 10% applied, bucketed WITHIN):**

| band | n | PF | mean $ | pre | post |
|---|--:|--:|--:|--:|--:|
| 10–15% | 1,082 | 1.595 | 127 | 1.904 | 1.387 |
| 15–20% | 719 | 1.708 | 165 | 1.779 | 1.661 |
| 20–25% | 386 | 1.642 | 190 | 1.438 | 1.745 |
| **25–30%** | 229 | **3.342** | **761** | 1.626 | **4.206** |
| **30–40%** | 222 | **1.230** | 84 | **0.929** | 1.311 |
| 40–55% | 98 | 2.514 | 550 | 4.562 | 2.128 |
| 55%+ | 44 | 1.140 | 81 | 2.587 | 1.011 |

**Same bands on the loose rvol [3,20] gate — the notch is NOT an artifact of the rvol ≥ 6 filter:**

| band | n | PF | mean $ | pre | post |
|---|--:|--:|--:|--:|--:|
| 10–15% | 3,463 | 1.687 | 175 | 2.287 | 1.358 |
| 15–20% | 1,634 | 1.499 | 141 | 1.939 | 1.329 |
| 20–25% | 656 | 1.723 | 229 | 1.836 | 1.683 |
| **25–30%** | 335 | **2.481** | **581** | 1.700 | **2.751** |
| **30–40%** | 273 | **1.304** | 122 | **0.874** | 1.409 |
| 40–55% | 115 | 2.174 | 476 | 3.709 | 1.884 |
| 55%+ | 48 | 1.108 | 64 | 1.046 | 1.122 |

Identical shape: 25–30% is the peak (PF 2.481, post 2.751), 30–40% is the worst non-tail band (1.304, pre-
2015 losing 0.874), the far tail is pre-2015-driven. rvol ≥ 6 *sharpens* the peak (3.34 vs 2.48) but the
structure is a property of the move distribution itself, present on both gates.

**Findings (corrected — the cliff is a NOTCH at 30%, not a smooth ramp):**
1. **The 25–30% band is the single best cell in the whole move distribution — PF 3.342, mean $761,
   post-2015 4.206.** These are the highest-conviction clean breakouts. The cumulative sweep buried this
   (adding 30%+ junk on top dragged the running average down to 1.54 at the 0.30 floor).
2. **The real cliff is a NOTCH at 30–40%: PF 1.230, mean $84, pre-2015 outright losing (0.929)** — the
   worst non-trivial band in the sweep, sitting *directly above* the best one. The mean winner collapses
   $761 → $84 across the 30% line: 30–40% single-day moves are exhaustion / blow-off gaps that revert.
3. **It is NOT monotone up to the notch.** 20–25% (1.642) is actually *weaker* than 15–20% (1.708); the
   edge is lumpy with a sharp spike specifically at **25–30%**. So "bigger is better" was wrong — there's a
   discrete sweet-spot band, not a ramp.
4. **The far tail (40–55%, 55%+) is thin and pre-2015-driven** — 40–55% PF 2.514 is pre 4.562 / post 2.128
   on n=98; 55%+ is dead post-2015 (1.011, n=44). Don't lean on it.
5. **rvol ≥ 6 stays ahead at every cumulative move floor** (move doesn't substitute for volume — rvol
   cleans the low end, move sharpens the high end), but rvol≥3 + move≥0.25 still beats the rvol≥6 + move≥0.10
   default — a different frontier point.

**Practical takeaway (revised):** this is a **notch, not a ceiling.** Don't cap at 30% in the naïve sense —
instead **size up the 25–30% band** (the best clean-breakout cohort) and **de-weight / avoid 30–40%** (the
exhaustion zone). Keep the move *default* floor near 0.10 for capacity (each band still has positive edge up
to 30%); the ≥30% blow-off is the one region to actively exclude.

**✅ ADOPTED (2026-06-20): MaxUpThreshold = 0.30 cap, and the rvol upper cap REMOVED (move cap supersedes
it).** The 30%+ blow-off and the rvol >15 toxicity are the *same trades* from two angles (the >15 cohort
averages a +47% move). So a 30%-move cap **mends the rvol >15 bucket directly** — rvol >15 goes from PF 0.73
(mean −1.8%) to **PF 1.54** (mean +1.2%) once the ≥30% moves are removed. Head-to-head (rvol ≥ 5, breadth,
≥2005):

| gate | n | PF | total $ | PF post |
|---|--:|--:|--:|--:|
| A: rvol [5,15], move uncapped (old) | 3,437 | 2.004 | 918k | 1.748 |
| **B: rvol ≥ 5 uncapped, move < 30% (NEW DEFAULT)** | 3,713 | 1.991 | 917k | 1.716 |
| C: both caps | 3,132 | 2.068 | 849k | 1.780 |

A and B are **interchangeable** (PF ~identical, same $917k) — they remove the same blow-offs. B is chosen:
it keeps **+276 well-behaved high-rvol trades** the volume cap discarded (a 3% PF gain wasn't worth 20%
fewer trades — option C), and it's the *more principled* rule: a 30% single-day move is what a blow-off
**is**; high rvol merely correlates. Rationale also includes that the surviving high-rvol names are
manually triageable (skip the deal-locked/pump ones on a news check), which a blind rvol cap can't do.
The move filter is now a **band [10%, 30%)** and rvol is **[5, ∞)**.

#### ⭐ rvol sweep (1→15, move held at 10%) — rvol ALSO has a toxic blow-off tail; cap it ~15 (2026-06-20)

> **Superseded conclusion:** this section concluded "add an upper rvol cap ~15." That cap was briefly the
> default, then **removed** — the 30%-move cap supersedes it (see the move-notch section above: the >15
> toxicity and the 30%+ moves are the same blow-off trades; capping the move mends the rvol bucket and
> keeps +276 well-behaved high-rvol trades). The rvol *analysis* below still stands; only the cap was dropped.

> Symmetric question to the move analysis: hold move ≥ 10%, vary rvol. Regenerated trips with a wide rvol
> gate (`--rvol-min 1 --rvol-max 1000`) since the standard CSV is rvol ∈ [3,20]; caps + breadth + ≥2005.
> Non-cumulative bands first (where the edge lives), then the cumulative floor.

**rvol DECILES — median return added (move ≥ 10%, caps on, ~1,100/decile).** PF is mean-driven, so it's
tail-sensitive; the **median return** shows what the *typical* trade does:

| decile | rvol range | median ret | mean ret | win% | PF | post med |
|---|---|--:|--:|--:|--:|--:|
| 1 | 1.0–1.7 | +0.19% | 1.29% | 50.5 | 1.286 | +0.38% |
| 2 | 1.7–2.3 | +0.73% | 1.77% | 53.3 | 1.489 | +0.59% |
| 3 | 2.3–2.8 | **−0.49%** | 0.81% | 46.2 | 1.215 | −0.51% |
| 4 | 2.8–3.5 | −0.11% | 1.31% | 49.2 | 1.374 | −0.10% |
| 5 | 3.5–4.2 | −0.07% | 1.18% | 49.2 | 1.374 | −0.24% |
| 6 | 4.2–5.2 | 0.00% | 0.55% | 49.7 | 1.188 | −0.12% |
| 7 | 5.2–6.5 | +0.03% | **3.57%** | 50.0 | 2.263 | −0.41% |
| 8 | 6.5–8.8 | +0.24% | 2.64% | 51.9 | 1.979 | +0.12% |
| **9** | 8.8–15.6 | **+0.67%** | 2.01% | **55.2** | 1.802 | **+0.91%** |
| 10 | 15.6+ | 0.00% | **−0.34%** | 49.9 | 0.923 | −0.08% |

**The median reframes the rvol story — the edge is almost ALL right-tail, not the typical trade:**
1. **Low rvol (D1–D6, rvol ~1–5) is a fragile, tail-carried edge.** The *median* trade there is slightly
   negative-to-flat (−0.49% to 0.00%); the positive PF comes entirely from a few big winners. High variance,
   mean-dependent — confirms the "1–3 rvol breakouts are barely distinguishable" read. Not a reliable edge.
2. **Median EXPOSES decile 7 (rvol 5.2–6.5) as a mirage.** Median +0.03% but mean +3.57% — its PF 2.26 is
   one or two monster trades, not a broadly good cohort (and post-2015 median is −0.41%). PF flattered it.
3. **⭐ Decile 9 (rvol ~9–15.6) is the genuine sweet spot — the ONLY decile where all three agree:** highest
   win rate (55.2%), highest median (+0.67%), best post-2015 median (+0.91%). Broad-based edge, not tail-
   driven. By median this is *the* rvol cohort to want — clearer than the PF view (which spread the edge
   across D7–D9).
4. **Decile 10 (15.6+) confirmed toxic from a new angle:** median 0.00%, mean *negative* (−0.34%) — the
   typical extreme-volume trade goes nowhere and the average loses (reverting blow-offs dominate).

So a cap ~15 severs the dead top decile, and the real conviction lives in **rvol ~9–15** (D9), where the
*typical* trade — not just the average — is positive and wins >55%.

**Cumulative rvol floor (move ≥ 10%):**

| floor | n | PF | total $ | post |
|---|--:|--:|--:|--:|
| ≥1 | 11,000 | 1.436 | 1.62M | 1.341 |
| ≥3 | 7,397 | 1.516 | 1.19M | 1.349 |
| **≥5 (cum. peak)** | 4,587 | **1.622** | 885k | 1.382 |
| ≥6 (current) | 3,653 | 1.426 | 496k | 1.364 |
| ≥8 | 2,496 | 1.260 | 221k | 1.138 |
| ≥10 | 1,843 | 1.204 | 135k | 1.086 |
| ≥15 | 1,150 | **0.934** | −33k | **0.846** |

**rvol BAND gates (cap the tail):**

| gate | n | total $ | PF | pre | post |
|---|--:|--:|--:|--:|--:|
| **rvol [6,20] (CURRENT)** | 2,779 | 580k | 1.796 | 1.769 | 1.810 |
| rvol [5,15] | 3,437 | 918k | 2.004 | 2.492 | 1.748 |
| **rvol [6,15]** | 2,503 | 529k | 1.807 | 1.705 | **1.862** |
| rvol [3,15] | 6,247 | 1.22M | 1.681 | 2.066 | 1.513 |

**Findings — rvol is NOT a monotone floor; it mirrors the move%-notch (healthy middle, toxic blow-off tail):**
1. **The top decile (rvol 15.6+) is an outright LOSER: PF 0.923, mean −$34, post-2015 0.844** — the only
   losing decile. Extreme volume = climax/exhaustion, exactly like 30%+ single-day moves. This is the
   cleanest, most robust signal in the sweep.
2. **The cumulative floor peaks at ~5 then DECLINES:** ≥5 (1.622) > ≥6 (1.426) > ≥8 (1.260) > ≥15 (0.934).
   Raising the floor past ~5 actively hurts because it loads up on that bad top decile. **The current rvol ≥ 6
   floor is past the cumulative peak** — but most of the ≥5 advantage is *pre-2015* (decile 7, rvol 5.2–6.5,
   is pre 4.52 / post 1.28), so this is era-fragile; post-2015 the floor barely matters between 5 and 6.
3. **The one durable, actionable change is an UPPER CAP ~15.** `rvol [6,15]` vs current `[6,20]`: **post-2015
   PF rises 1.810 → 1.862** with almost no trade loss (2,779 → 2,503) — cutting the 15+ losers helps the
   modern era for free. (Lowering the floor to 5, `[5,15]`, is the pre-2015 mirage: overall PF 2.004 but
   post-2015 1.748 < current 1.810.)
4. **By median (see decile table above), the edge is even more concentrated than PF suggests** — low rvol
   (D1–D6) has a near-zero/negative *median* (tail-carried, fragile), decile 7's PF is a fat-tail mirage, and
   the genuine broad-based sweet spot is **decile 9 (rvol ~9–15.6)**: win 55%, median +0.67%, post +0.91%.

**rvol [1,15) QUARTILES (less noisy than deciles, ~2,463/quartile) — the typical trade only wins in Q4:**

| quartile | rvol range | n | median ret | mean ret | win% | PF | med pre | med post |
|---|---|--:|--:|--:|--:|--:|--:|--:|
| 1 | 1.0–2.4 | 2,463 | +0.24% | 1.36% | 51.0 | 1.335 | +0.26% | +0.24% |
| 2 | 2.4–3.8 | 2,463 | **−0.23%** | 1.17% | 48.2 | 1.329 | −0.21% | −0.25% |
| 3 | 3.8–6.0 | 2,462 | +0.03% | 2.09% | 50.1 | 1.731 | +0.52% | **−0.19%** |
| **4** | 6.0–15.0 | 2,462 | **+0.37%** | 2.12% | **52.8** | 1.807 | +0.45% | **+0.30%** |

The quartile view (cleaner than the deciles) shows the median is **flat-to-weak across Q1–Q3 then steps up
in Q4**: +0.24 → −0.23 → +0.03 → +0.37. No smooth ramp — Q1 (rvol 1–2.4) is as good as Q3 (3.8–6) on the
median. **Only Q4 (rvol 6–15) stands out, and it's the only era-robust quartile** (post-2015 median +0.30%,
win 52.8%). Q3's PF 1.731 was flattered — its post-2015 median is *negative* (−0.19%, a pre-2015 edge). Q2
(rvol 2.4–3.8) is the weak spot (negative median both eras). **This vindicates the rvol ≥ 6 floor on a
median basis** — Q4 (≥6) is exactly where the typical trade turns durably positive, contradicting the
cumulative-PF "peak at 5" read (which was tail/pre-2015-driven). The conviction is in rvol ~6–15.

**SUB-1 rvol — the floor DOES matter, at ~1 (regenerated with `--rvol-min 0`):** the "1–5 is indistinct"
read was a *within-≥1* observation; below average volume the median turns sharply negative.

| rvol band | n | median ret | mean ret | win% | PF |
|---|--:|--:|--:|--:|--:|
| <0.5 | 170 | **−1.38%** | −0.93% | 43.5 | 0.859 |
| 0.5–0.75 | 129 | −1.06% | 1.06% | 47.3 | 1.239 |
| 0.75–1 | 198 | **−1.88%** | 0.88% | 43.4 | 1.175 |
| 1–1.5 | 767 | −0.17% | 0.92% | 49.4 | 1.187 |
| 1.5–2 | 931 | +0.64% | 1.64% | 52.8 | 1.443 |

**Sub-1 vs ≥1:** median **−1.30% vs +0.10%**, win **44.5% vs 50.5%** (n=497 vs 11,000). A breakout on
*below-average* volume is a fakeout — the typical one loses ~1.5% and wins <45%. By PF the sub-1 bands look
almost respectable (0.86–1.24, an occasional winner drags the mean up); **only the median exposes them.**

**So rvol has THREE regimes, and the median is the only metric that draws all three:**
- **rvol < 1 = fakeout / fail** (median ≈ −1.5%, win <45%) — no real demand confirming the breakout.
- **rvol ~1.5–15 = the edge** — a mild ramp (median +0.6% at 1.5–2, +0.57% at 9–15), peaking broadly at
  decile 9 (rvol ~9–15.6); the 1–5 sub-range is positive-but-indistinct, the conviction is ~9–15.
- **rvol > 15 = toxic blow-off** (median 0%, mean negative) — climax/exhaustion.

**Practical takeaway:** rvol is **not** a "more is better" dial. It's a **gate at ~1** (below = fakeout) +
a mild edge ramp to ~15 + a **toxic tail above 15.** Keep the rvol ≥ 6 floor (it clears the sub-1 junk with
margin; the ≥5 "improvement" is pre-2015 only) and **add an upper cap ~15.** Mirrors move%: healthy middle,
bad on both extremes — but for rvol the *lower* bad zone is sub-1, not merely low.

> ### 📐 Winner-clip convention (PROJECT STANDARD as of 2026-06-21)
> **All PF / mean-return figures are now computed on each trade's RETURN clipped at +50% (`LEAST(ret, 0.50)`);
> the loss side is left untouched.** PF is mean-driven and therefore hostage to lottery winners — a single
> buyout pop or +800% runner can hand a bucket a gaudy PF that has nothing to do with its *reliable* edge, and
> that contamination is exactly what makes bucketed/decile views go non-monotonic. Clipping the upside to a
> sensible ceiling (+50% is above p99 of qualifying trades — generous) makes PF reflect the *typical* trade in
> a bucket, which is what a floor/ceiling filter decision actually rests on. **Total P&L is understated by
> design** (the conservative read); when raw total-$ matters it is reported separately as `PF raw` / `tot`.
> Corollary discipline (re-confirmed 2026-06-21): **decide on CUMULATIVE views** (`X ≥ thr` / `X < thr`), which
> are low-variance and monotone; use **non-cumulative bands only diagnostically** to locate where edge changes
> — their edge-of-range cells are noisy and must not drive a threshold choice on their own.

#### Cumulative rvol floor with RETURN CLIPPING — the high-floor edge is NOT a tail artifact (2026-06-20)

> Cumulative `rvol ≥ X` floor (capped at 15 to drop the toxic tail, move ≥ 10%, caps on). Raw PF is
> tail-sensitive — at rvol ≥ 6 the max single-trade return is **+1,202%** (p95 +19%, p99 +41%; 12 trades
> >+50%, 2 >+100%). To test whether the floor's PF gains are real or carried by a few monsters, recomputed
> PF with each trade's **upside clipped at +50%** (generous — above p99; loss side untouched).

| floor | n | PF raw | PF clipped | clip pre | clip post |
|---|--:|--:|--:|--:|--:|
| ≥1 | 9,850 | 1.515 | 1.353 | 1.453 | 1.318 |
| ≥3 | 6,247 | 1.681 | 1.425 | 1.589 | 1.354 |
| ≥5 | 3,437 | 2.004 | **1.581** | 1.702 | 1.517 |
| ≥6 | 2,503 | 1.807 | 1.604 | 1.683 | 1.561 |
| ≥7 | 1,825 | 1.971 | 1.689 | 1.689 | 1.688 |
| ≥8 | 1,346 | 1.749 | 1.721 | 1.872 | 1.644 |
| ≥9 | 968 | 1.964 | 1.950 | 1.984 | 1.933 |
| ≥10 | 693 | 2.101 | 2.079 | 2.152 | 2.042 |
| ≥12 | 319 | 2.721 | 2.670 | 2.079 | 3.037 |

**Findings:**
1. **The monster winners inflated the LOW floors, not the high ones.** The clip haircut shrinks as the floor
   rises: ≥1 loses 0.16 PF, ≥5 loses **0.42** (the biggest mirage — that gaudy raw 2.004 was a couple of
   monsters), but ≥9 loses only 0.014 and ≥12 only 0.05. The high-rvol PFs are tail-robust; their edge is
   broad, not carried by outliers.
2. **The monotone trend SURVIVES clipping and is cleaner than raw.** Clipped PF still rises 1.35 → 1.60 (≥6)
   → 1.95 (≥9) → 2.67 (≥12), and **post-2015 clipped is the cleanest monotone of all** (1.318 → 1.561 → 1.688
   → 1.933 → 2.042) — no spikes, no reversals. The raw-table lumpiness (the ≥5 and ≥12 bumps) was tail noise.
3. **Conclusion (tail-robust):** higher rvol genuinely buys better trades, monotonically, all the way to 15
   — and it is NOT a tail artifact at the high end. This agrees with the median view; raw PF obscured it. The
   earlier raw "floor peaks at ≥5" was a monster-winner mirage (clipped, ≥5 is 1.58, *below* ≥9–12).

#### Why rvol >15 is "neutral" — it's the 2020-21 pump cohort, NOT buyouts (2026-06-20)

> Investigated the toxic rvol >15 tail (decile 10, median 0% / mean −$34 / PF 0.92). Hypothesis: deal-locked
> buyout/merger arbs (huge volume, price pinned → flat). **The data rejects buyouts and points to catalyst
> blow-offs concentrated in the 2020-21 pump mania.**

- **Not deal-flat.** The >15 cohort has lower stddev (16% vs 26% for 6–15) and more flat trades (39% within
  ±2% vs 26%), *but* a fatter LEFT tail (p10 −14.7% vs −8.3%). A buyout pins price near a fixed offer; these
  don't — they make violent two-sided moves that net to ~0.
- **The names are explosions, not deals.** Avg entry-day move **+47%**, avg rvol 70 (max 986), across 1,019
  distinct symbols. Top names: SCKT +538% → −50%, GLSI +998% → −18%, OBLN +414% → −44%, IINN +308% → −61%,
  YGMZ +333% → −33% — small-cap squeezes, biotech binary readouts, SPAC/meme pumps. They spike then crater.
- **⭐ It's a 2020-21 regime artifact, not a structural dead zone:**

  | rvol >15 cohort | n | % | median ret | mean ret | PF |
  |---|--:|--:|--:|--:|--:|
  | 2020H2–2021 (pump era) | 238 | 20.7% | **−2.27%** | **−4.75%** | **0.477** |
  | all other years | 912 | 79.3% | +0.18% | +0.87% | 1.273 |

  The toxicity is ~entirely the 21% of the cohort from the 2020-21 meme/SPAC/micro-cap mania (PF 0.48). The
  other 79%, across normal years, is an ordinary **PF 1.273**. Averaged together → the deceptive ~0 neutral.

**Implication for the [5,15] cap:** the upper cap is mainly **regime insurance against a repeat of 2021's
pump blow-offs**, not the pruning of a permanently-bad cohort — in normal regimes rvol >15 is fine (PF 1.27).
A reasonable robustness measure (don't be long the next meme-stock climax), but characterize it honestly as
tail-regime defense. (Breadth lag-1 > 0.5 should have caught much of 2021's churn but let these through on
the up-days — the blow-offs happen *into* strength.)

#### Breadth (pct_above_20) cumulative floor — higher breadth is better up to ~0.70, then rolls over (2026-06-20)

> We *gate* on breadth (lag-1 `pct_above_20 > 0.5`) but had never swept the level. Breadth = the fraction of
> liquid CS/ADRC stocks above their own **20-day** MA across the ~3,000-name universe (`breadth.parquet`,
> stores pct_above_20/50/100), lagged 1 day. (Note: v0 once concluded the **50-day** breadth was best — that
> is **stale**; v1/v2 settled on the **20-day** and that is the decided measure.) Cumulative `≥ X` floor
> (default trips, ≥2005), the clean view (deciles are too noisy):
>
> **Reverse-engineered universe (the builder wasn't committed; recovered 2026-06-20 by matching the parquet's
> `n` across dates):** `type IN ('CS','ADRC')` (common stock + ADRs — **NOT** ETF/ETN/funds; those are ~43%
> of the raw price table, the main gap) **AND 30-calendar-day average dollar volume ≥ $1,000,000** (the
> project-standard `avg_dollar_volume_4w` liquidity convention; **not** same-day, **not** $100k). Matched the
> parquet `n` to ~2-3% on 2010/2015/2020/2026 (2,593 vs 2,531; 3,112 vs 3,028; 3,364 vs 3,289; 3,934 vs
> 3,859 — consistently a hair over, likely a point-in-time ticker-reference nuance). $100k overshoots 25-35%.
> `pct_above_N` = fraction of that daily universe with `close > N-day SMA of close`.

| floor | n | median | win% | PF | total $ | PF post |
|---|--:|--:|--:|--:|--:|--:|
| ≥0.0 (no gate) | 5,858 | +0.34% | 52.8 | 1.781 | 1.20M | 1.644 |
| ≥0.4 | 4,677 | +0.32% | 52.8 | 1.899 | 1.06M | 1.690 |
| **≥0.5 (CURRENT)** | 3,717 | +0.28% | 52.3 | 1.991 | 917k | 1.717 |
| ≥0.6 | 2,520 | +0.30% | 52.9 | 2.249 | 776k | 1.909 |
| ≥0.65 | 1,919 | +0.34% | 53.1 | 2.385 | 663k | 1.936 |
| **≥0.70** | 1,272 | +0.38% | 53.5 | **2.822** | 583k | **2.150** |
| ≥0.75 | 662 | +0.89% | 57.1 | 1.942 | 153k | 1.776 |
| ≥0.80 | 288 | +0.98% | 58.3 | 2.124 | 80k | 1.967 |

**Findings:**
1. **PF rises monotonically with breadth up to 0.70, then rolls over.** Peak at ≥0.70: PF **2.822**,
   post-2015 **2.150** — vs the current ≥0.5 (1.991 / 1.717). The ≥0.75/≥0.80 rollback (~1.9–2.1) is the
   familiar froth signature (extreme breadth = late-cycle euphoria) but n is thin (288–662) — don't over-read.
2. **Unlike the noisy deciles, the floor view shows median AND win-rate also rise** (median +0.28% → +0.38%,
   win 52.3% → 53.5% from ≥0.5 to ≥0.70) — so higher breadth improves the *typical* trade, not just the
   tail. A trustworthy lever.
3. **Faster breadth (10/15-day MA) does NOT beat the 20-day**: the 10-day was *worse* at every floor,
   especially post-2015; 15-day ≈ 20-day. The current 20-day window is confirmed; a shorter, more-reactive
   breadth just adds noise. (Caveat: this test built pct_above_10/15/20 on an *approximate* universe — before
   the universe was reverse-engineered, it omitted the CS/ADRC filter so ~43% ETFs leaked in. The relative
   window ranking should hold, but re-confirm on the correct CS/ADRC + $1M-dv universe if it ever matters.)
4. **Available upgrade:** raising the breadth gate 0.5 → 0.70 lifts PF 1.991 → 2.822 (post-2015 → 2.150) at
   the usual capacity cost (3,717 → 1,272 trips, −66%). A steep quality-vs-capacity dial, same family as the
   tightness/move levers; the *direction* (higher breadth = better, to 0.70) is clean and era-robust. Not
   adopted as default yet — the −66% capacity is a big ask; candidate for a sizing tilt rather than a hard gate.

> **Universe:** the table above is the **standard $1M universe** (CS/ADRC + 30-cal-day ADV ≥ $1M, decided
> 2026-06-20 for both filters); the build below reproduces the production `breadth.parquet` to ~1-2%.

**Alternative — same sweep on a looser $100k universe** (CS/ADRC + 30-cal-ADV ≥ $100k), for reference only —
the optimum shifts down and the rollover is earlier/sharper (the looser universe is noisier at the extreme):

| floor | n | PF | PF post | (vs $1M-standard PF) |
|---|--:|--:|--:|--:|
| ≥0.5 | 3,542 | 2.044 | 1.759 | (1.991) |
| ≥0.6 | 2,278 | 2.294 | 1.941 | (2.249) |
| **≥0.65** | 1,630 | **2.484** | 2.002 | (2.385) |
| ≥0.70 | 972 | 1.776 | 1.574 | (2.822) |

On $100k the peak is ≥0.65 (then a sharp thin-n rollover at ≥0.70); on the standard **$1M the peak is ≥0.70
(PF 2.822)**. The optimum is universe-dependent, but "higher breadth helps to a mid-high optimum then froths
over" is robust to the definition. **We use $1M.**

> **Build script:** both the breadth and heat parquets are built by
> **`scripts/equity/build_breadth_and_heat.sql`** (runnable DuckDB; writes `breadth_1m.parquet` —
> reproducing the production `breadth.parquet` — and `heat.parquet`). It encodes the shared universe
> (30-cal-day ADV ≥ **$1M**; CS/ADRC for breadth only) and the load-bearing +1000% heat clip — the canonical
> reference for how both regime filters are computed.

#### ⭐ "Top-gainer HEAT" — froth timing measure; CHOSEN: skip entries when heat-10d ≥ 25% (Sykes-inspired) (2026-06-20)

> A new market-timing measure, orthogonal to the %-above-MA breadth we already gate on. It measures the
> *speculative temperature* of the tape — how hot the day's hottest names are running.
>
> **How the heat filter is calculated (exact, reproducible — canonical builder:
> `scripts/equity/build_breadth_and_heat.sql`; this section's breakdown SQL: `scripts/equity/heat_breakdown.sql`):**
> 1. **Per-stock daily return** = `adj_close / prev_adj_close − 1`, from `split_adjusted_prices`.
> 2. **Qualifying universe each day:** **30-calendar-day average dollar volume ≥ $1M** (the project-
>    standard `avg_dollar_volume_4w` window, $1M bar — NOT same-day dollar volume) AND a non-null return.
> 3. **⚠️ CLIP each per-stock return at +1000% (×10) BEFORE aggregating.** `split_adjusted_prices` contains
>    rare corrupted split/price rows that produce absurd returns; because heat is a mean of the *top* tail,
>    even one such row destroys that day's value. **The $1M + CS/ADRC constraints do NOT remove these** —
>    verified 2026-06-20: on the $1M universe, un-clipped, **2,390 rows still exceed +1000%** (max
>    **199,999,989,900%** = `ZXZZT` on 2014-10-22, price 0.0001→199,999.99). The culprits are mostly **NASDAQ
>    test tickers** (`ZXZZT`, `ZWZZT`, `AAZST`, `TESTA`, `ZVV`, `CGZST`, a bare `Z`, …). **CORRECTION (2026-06-21):
>    these have NO `ticker_reference` row at all** (verified — the ref-table query returns 0 rows for every test
>    symbol; an earlier note here wrongly said they were "tagged `CS`"). They reach the HEAT universe only because
>    heat uses a **LEFT** join and is intentionally not CS/ADRC-restricted, so a ref-less ticker survives with a
>    NULL type and its fake $1M+ volume clears the liquidity bar. The remainder are real micro-caps with genuine-but-extreme reverse-split moves that
>    *should* still be capped for a top-tail mean. **A 5-letter-ticker exclusion does NOT work either** (tested):
>    only 1,747 of the 2,390 corrupt rows are 5-letter; **642 are shorter** — both more synthetic symbols
>    (`ZVV`, a bare `Z`) *and* legitimate volatile micro-caps with real blowups (`TOPS`, `INPX`, `GBSN`,
>    `SDRL +180%`, `MULN +177%`, `XELA`). So a 5-letter rule simultaneously *misses* short-ticker junk **and**
>    would wrongly drop the many real 5-letter names (foreign ADRs etc.) — leaky and over-broad. Any name-based
>    filter is the wrong tool; the clip is **return-based** (caps the symptom regardless of ticker) and remains
>    the robust catch-all — even with all synthetic names gone, the real `SDRL`/`MULN`-type extremes still need
>    capping for a top-tail mean. It doesn't touch genuine gainers (a real one-day move tops out well under
>    1000%). Without it the whole measure is garbage. Do NOT drop it when porting to a live precompute.
>    **Belt-and-suspenders (2026-06-20):** the test tickers are also excluded by a **hardcoded blocklist** in
>    `build_breadth_and_heat.sql` (`is_test_ticker` = a literal list: NASDAQ `Z?ZZT`/`ZYxxx` test series, NYSE
>    `NTEST.*`, `AAZST`/`CGZST`/`YJZST`/`ZVV`), applied to BOTH universes. *Why a blocklist and not just the type
>    filter (clarified 2026-06-21):* for **breadth** the `type IN ('CS','ADRC')` inner join ALREADY removes them
>    for free (they have no ref row — verified 0 survive the join), exactly as in the engine; the blocklist there
>    is redundant. But **heat deliberately reads the whole liquid tape, NOT just CS/ADRC** (top-gainer froth lives
>    in warrants/units/recent-IPOs/foreign listings that legitimately lack a clean CS/ADRC row) — there are
>    **16,400 ref-less tickers** in the price table, so switching heat to a CS/ADRC inner join would silently drop
>    ~16k real-ish names to kill ~16 synthetic ones. The blocklist removes *only* the known-bad synthetic symbols
>    while keeping the broad tape — which is why it, not the type filter, is the right tool for heat. *Why a
>    hardcoded list and not a pattern/API:* the test symbols are a fixed, finite, published set that doesn't grow,
>    and every alternative proved leaky —
>    **Polygon doesn't carry them in its reference master at all** (`type=OTHER` and a direct `?ticker=ZXZZT`
>    query both return 0 rows, verified), a high-price rule false-positives on real reverse-split micro-caps,
>    and a bare `name~"test"` hits Whitestone/inTEST. The list catches all 16 synthetic tickers found in the
>    data; the real ticker `Z` (Zillow, one corrupt $200k row) is deliberately *kept* and handled by the clip.
>    This cleans synthetic names out of breadth too (where they'd silently count as "stocks"). The clip stays
>    as the backstop for residual real-ticker ratio-glitches (LU, EPIX).
> 4. **Daily heat** = mean of the clipped returns of the **top 1% of the qualifying universe by return**
>    (per-day `PERCENT_RANK() ≥ 0.99`).
> 5. **Smooth:** trailing mean over the chosen window — **`h10` = mean of daily heat over the prior 10
>    days**, `ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING` (the `1 PRECEDING` **lags it one day** → as-of the
>    prior close, no lookahead). (5/10/15/20 were swept; h10 chosen.)
> 6. **Gate:** at entry, exclude (or downsize) when **`h10 ≥ 0.25`** (the Q4/Q5 boundary = 80th percentile,
>    on the 30-cal-ADV ≥ $1M universe).
>
> Series ~5,700 days (2003-09→2026-05); daily heat median ~18% after clipping. (Robustness: the froth-cut
> result barely moves across universe definitions — same-day-$100k gave threshold 27% / kept-PF 2.245;
> 30-cal-$100k gave 24% / 2.243; **30-cal-$1M (chosen) gives 25% / kept-PF 2.268 / post-2015 1.955** — so the
> signal does not depend on the liquidity-filter choice; we standardize on 30-cal-day ADV ≥ $1M.)

**Heat quintiles — high heat is BAD for our breakouts (median return, every window):**

| quintile | h5 | **h10 (chosen)** | h15 | h20 |
|---|--:|--:|--:|--:|
| Q1 (coolest) | +0.39% | **+0.67%** | +0.70% | +0.72% |
| Q2 | +0.58% | **+0.45%** | +0.44% | +0.38% |
| Q3 | +0.37% | **+0.39%** | +0.34% | +0.56% |
| Q4 | 0.00% | **+0.23%** | +0.11% | −0.08% |
| **Q5 (hottest)** | −0.29% | **−0.47%** | −0.41% | −0.32% |

*(This window-comparison table is on the original same-day-$100k universe; the chosen-window numbers were
re-confirmed on the corrected 30-cal-ADV ≥ $100k universe — the conclusion and h10 choice are unchanged, the
exact medians shift ~0.1pt. The h10 ladder/threshold below are the corrected, authoritative ones.)*

**Findings:**
1. **The froth hypothesis wins over risk-on.** The hottest-heat quintile is the *only* one with a negative
   median return — in **all four windows**. When the top 1% of gainers have run hot over the prior 1–4
   weeks, our momentum breakouts buy into a crowded, late-stage tape and the typical trade loses. Cool tape
   (Q1–Q2) → breakouts run. This generalizes the 2021-pump finding to a continuous regime measure.
2. **Froth cut across all windows (exclude top quintile, keep Q1–4) — h10/h15 best on the kept book, h20
   best post-2015, h5 worst:**

   | window | n keep | PF keep | total $ | PF keep post | Q5 median (cut) | Q5 PF |
   |---|--:|--:|--:|--:|--:|--:|
   | h5 | 2,971 | 2.167 | 780k | 1.776 | −0.29% | 1.532 |
   | **h10 (CHOSEN)** | 2,971 | **2.245** | 810k | 1.885 | **−0.47%** | **1.388** |
   | h15 | 2,971 | 2.244 | 808k | 1.902 | −0.41% | 1.394 |
   | h20 | 2,971 | 2.233 | 803k | **1.934** | −0.32% | 1.414 |

   h5 is too noisy (cuts the *least* toxic Q5). h10/h15 tie for best kept-PF and sharpest froth separation
   (cut the most toxic cohort); h20 wins post-2015 by ~0.05 PF. **Chose h10** — fastest regime response (a
   timing signal should step aside sooner) and the sharpest bad-cohort separation; the window choice is a
   minor optimization (window-insensitivity 10→20 is itself evidence the signal is real, not fitted).
3. **⭐ The h10 filter — exclude entries when trailing-10d heat ≥ ~24%** (Q4/Q5 boundary = 80th percentile,
   on the 30-cal-ADV ≥ $1M universe). Concrete ladder (quintile boundaries): Q1 9.6–14.2% (calm) · Q2
   14.2–16.6% · Q3 16.6–19.2% (typical, daily-heat median ≈18%) · Q4 19.2–25.6% (warming) · **Q5 25.6–49.2%
   (frothy — the cut)**. A 10-day *average* ≥25% means sustained two-week froth, not a single hot day.
   Filter effect:

   | | n | PF | total $ | PF post |
   |---|--:|--:|--:|--:|
   | baseline (all heat) | 3,713 | 1.991 | 917k | — |
   | keep heat-10d < 25% | 2,971 | **2.268** | 825k | **1.955** |
   | excluded (heat-10d ≥ 25%) | 742 | 1.334 | 92k | 1.403 |

   Cutting the frothy tape lifts PF **1.991 → 2.268** for −20% trips / −10% P&L — a better quality-per-
   capacity trade than most filters tested.
4. **The excluded Q5 is a low-win-rate coin-flip, not outright poison.** Its **median is −0.57%** but its
   **mean is +1.24%** (win rate the lowest) — froth tape produces enough occasional monsters to keep
   the mean barely positive even as the *typical* trade loses. So it's a **downsize/skip** candidate, not a
   hard "never trade" exclusion like the rvol-15+ pump cohort (which had a negative mean). **Orthogonal** to
   breadth/trend (speculative temperature, not direction). Not yet wired into the engine; the heat series is
   a post-hoc DuckDB build (`scripts/equity/heat_breakdown.sql`) — to go live it needs precomputing into a
   parquet like `breadth.parquet` (then gate `heat10 < 0.25` as-of the prior close).
5. **The mirror measure "COLD" (bottom-1% losers) is the SAME regime axis as heat — redundant, don't wire
   it.** Built cold identically (mean return of the *bottom* 1% by `PERCENT_RANK ≤ 0.01`, same $1M universe,
   −100%/+1000% clip, trailing-10d lagged `c10`). Standalone quintile breakdown: the **coldest** quintile
   (c10 −24% to −15%, a tape whose laggards are in freefall) is the *worst* for our breakouts — median
   **−0.34%**, win 46.7%, PF 1.24 — i.e. **same direction as heat** (extremes are bad / risk-off), **not** a
   contrarian capitulation-bounce signal. But it's a weaker tool: **non-monotone** (only the extreme-cold
   tail matters; Q2–Q5 are PF 1.5–2.0 with no ordering, plus a fat-tail Q3 spike), and **`corr(c10, h10) =
   −0.646`** — strongly anti-correlated with heat (frothy tape ⇒ shallow losers; fearful tape ⇒ deep losers
   + tame gainers), so it's largely the *same* signal from the other tail. Decisive test — *within* the
   heat-kept book (h10 < 0.25), the coldest cohort's badness mostly vanishes (median flips to **+0.59%**, PF
   1.67 vs 2.49 for the rest): heat already removes the overlapping bad regime. **Keep heat; cold adds
   little.** (Useful confirmation that the froth/extreme axis is real from both tails — momentum-long systems
   just read the *froth* tail more cleanly than the *fear* tail.)

#### 52w-proximity gate: intraday-HIGH channel is WORSE than the closing-high channel (2026-06-20)

> The 52w-proximity gate (`close ≥ 0.95 × prior-252d high`) used the **closing-high** channel. Tested
> gating on the **intraday-high** channel instead (`Use52wHigh = true`, CLI `--use-52w-high`) — a stricter
> "above *true* resistance" condition (price must clear the prior year's intraday spike, not just its closing
> high). Hypothesis: cleaner resistance detection → better entries. **Result: it's worse on every metric.**

| gate | n | PF | total $ | mean $ | PF pre | PF post |
|---|--:|--:|--:|--:|--:|--:|
| **closing-high (DEFAULT)** | 2,780 | **1.795** | 580k | 209 | 1.769 | **1.808** |
| intraday-high (`--use-52w-high`) | 2,555 | 1.511 | 343k | 134 | 1.752 | **1.384** |

The intraday-high gate lowers PF (1.795 → 1.511), cuts P&L 41%, drops mean-$ (209 → 134), and **degrades
post-2015 specifically** (1.808 → 1.384; pre-2015 is ~flat). The ~225 trips it removes were *better than
average* (P&L falls more than proportionally). **Why:** the intraday-high condition is satisfied *later* in
a breakout — by the time price closes above the prior year's intraday wick, the high-edge early part of the
move is gone. The closing-high gate enters *earlier* (above the prior closing high but still under the old
intraday wick — the 0–10% "dead zone above the high"), which is where the edge is. Cleaner-resistance ≠
better-entry. **Decision: keep the closing-high default;** `--use-52w-high` stays as an opt-in research flag.

## Reproduction

```bash
# v2 default (2026-06-20): NO price stop, 5-day time-stop, next-open exit (cap=0, no
# trailing limit, expansion off); entry filters ATR% < 0.11 (log), tightness < 4.5
# (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap).
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

---

## Next session — INTRADAY entries near the open (planned 2026-06-21)

The whole system so far enters **at the close**. Next: test entering the same high-conviction setups
**near the open** and holding a few days, to capture the part of the move that happens *during* the
signal day instead of waiting for the close.

**Why retry this — it failed before, but under different conditions.** Months ago we tested opening-range
breakouts on high-rvol stocks and got nowhere. **But** that was: (a) essentially *every* rvol stock, with
(b) **no tightness gate, no ATR% gate, no move band, no breadth/heat regime filter** — i.e. none of the
edge-concentrating conditions discovered since. Those filters might have been the difference; an intraday
entry on the *filtered* population is a genuinely different experiment. The hypothesis: enter the
production setup (move [10%,30%), rvol [5,∞), ATR% < 0.11, tight < 4.5, 52w ≥ 0.95, breadth, heat < 25%)
**near the open** and hold ~5 days.

**Open questions to design around:**
- **Entry timing:** at the open? after an opening-range (first 5/15/30 min)? The old ORB framing is one
  option but not the only one — "buy near the open if the setup is intact" may beat waiting for a range break.
- **Which signal is known pre-close?** rvol, the entry-day move, and tightness/ATR% are partly intraday —
  need to define them as-of the entry *moment* (e.g. rvol projected from morning volume), not the full-day
  close, to avoid lookahead. This is the main correctness risk.
- **Data:** needs intraday bars (we have the equity bulk; the `intraday_10s` dataset exists). The current
  engine is daily-bar only — an intraday entry path is a real engine change, not a post-hoc SQL study.
- **Special case to revisit:** the **very-high-rvol names** (the 15+ / 30%+-move blow-offs that the
  current close-entry system *excludes* as exhaustion) might behave differently entered *early in the day* —
  noted as a deferred idea. Worth a separate look once the intraday harness exists.

The bar is high: the close-entry system is now PF ~2.0 (filtered, post-2015 ~1.8) with multiple
independent edges stacked. Intraday only earns its complexity if it adds materially on top.
