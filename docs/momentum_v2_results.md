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

**The symmetry (rvol is the key, both sides):** high-volume blow-offs *continue* — short the
down ones (PF ~1.5), fade/avoid the up ones (long PF ~0.5) — while **low-volume drifts mean-revert /
chop** and have no directional edge either way. The genuine inverse-exhaustion short edge exists but
is narrow: loose base + new 52w low + **rvol ≥ 8**.

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
