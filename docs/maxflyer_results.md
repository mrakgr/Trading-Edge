# MaxFlyer — Intraday High-Volume Breakout Research Log

`TradingEdge.MaxFlyer` (branch `maxflyer_v0`). An intraday momentum-breakout engine
on 1-minute candles. The hypothesis going in (carried over from the swing
HighFlyer work) was that a **volume-confirmed breakout to a new intraday high**,
filtered to tight, consolidating, near-52-week-high names, would be a high-PF
day-trading long — the intraday analogue of the swing edge, with the tightness
filter as the OOS-collapse fix the old ORB never had.

**That hypothesis is rejected.** The setup has a persistent *negative* long edge,
both on the filtered universe it was designed for and on a broad gapper universe.
The numbers and a chart-by-chart visual inspection agree.

See [[project_maxflyer_engine_built_2026-06-28]] for the engine architecture. The
trip-chart visualizer is `scripts/visualization/maxflyer_charts.py`.

---

## The system under test

A three-gate funnel, all knowable at/before the day-D open (zero lookahead):

- **Gate 1 — daily quality** (`PassesDailyFilter`, measured on the day-D open):
  price ≥ $1, avg-dollar-volume ≥ $100k, consolidation tightness < 4.5, daily
  log-ATR% < 0.10, within 5% of the prior-252d high (`52w ≥ 0.95`), and a
  "past-runner" volatility-history floor (`MaxAtrLog ≥ 0.04`).
- **Gate 2 — premarket**: day-D gap ∈ [10%, 200%]; premarket volume ≥ 20% of the
  4-week average daily volume.
- **Gate 3 — intraday breakout** (1m, session from 08:30 ET, entries from 09:35):
  the bar closes above the running session high of strictly-prior bars AND the
  bar's volume **exceeds the running session 1m-volume high** (the
  volume-confirmation gate — the breakout bar must re-take the session volume
  high, which normally only prints at the open/close). Intraday tightness < 4.5.
  Long-only; multiple concurrent breakouts allowed; hold to MOC (16:00), with an
  optional 2-bar-low protective stop.

Window: 2021-06-17 → 2026-06-25. Notional $10k/trip. P&L is gross (no fees/slippage
— this is a *viability* test, not an execution model).

---

## Run 1 — filtered universe (the designed config), hold to MOC

```
dotnet run --project TradingEdge.MaxFlyer -c Release
```
Defaults: Gate1 as above; Gate2 gap[0.10,2.0] + premkt ≥20% of avg; Gate3 intraday
tightness < 4.5 + volume confirmation; no stop.

| metric | value |
|---|---|
| candidates (Gate 1 & 2 passed) | 897 |
| trips | 50 |
| win rate | **28.0%** (14 / 50) |
| net P&L | **−$15,192** |
| **profit factor** | **0.587** |

Unprofitable. 50 trips is a thin sample, so on its own this could be noise — hence
Run 2.

### With the 2-bar-low protective stop

```
dotnet run --project TradingEdge.MaxFlyer -c Release -- --intraday-stop
```

| metric | no stop | 2-bar-low stop |
|---|---|---|
| trips | 50 | 50 |
| win rate | 28.0% | **18.0%** |
| net P&L | −$15,192 | **−$6,862** |
| profit factor | 0.587 | **0.708** |

The stop halves the loss and lifts PF (0.587 → 0.708) by capping the
failed-breakout left tail: 39 of 50 trips stop out for small bounded losses
(worst ~−$1.5k vs ~−$3.5k unstopped), while the 11 that survive a *tight* stop all
the way to MOC are net **+$16k**. Win rate drops because the tight stop also shakes
out trades that would have recovered. The stop is a clear EV improvement, but the
setup is still a losing long.

---

## Run 2 — broad universe (the decisive test), hold to MOC

To check whether the negative edge is real or an artifact of the
consolidation-quality selection, the four **daily** selection filters were removed
(tightness, ATR%, 52w-proximity, MaxAtrLog), keeping only the price/ADV floors,
the gap & premarket gates, and the **intraday** tightness + volume-confirmation
gates. This broadens the universe from "tight consolidating names near their highs"
to "any liquid-enough gapper that breaks out on volume."

```
dotnet run --project TradingEdge.MaxFlyer -c Release -- \
  --max-tightness 1000000 --max-atr-pct 1000000 --min-52w-pct 0 --min-max-atr-log 0
```

| metric | filtered (Run 1) | **broad (Run 2)** |
|---|---|---|
| candidates | 897 | **15,624** (17×) |
| trips | 50 | **2,191** (44×) |
| win rate | 28.0% | **30.3%** (663 / 2,191) |
| net P&L | −$15,192 | **−$782,641** |
| **profit factor** | **0.587** | **0.663** |

**The negative edge holds on a 44× larger, far more representative sample.**
A 30% win rate on 2,191 high-volume breakout trips. This is not a small-sample
fluke and not a selection artifact.

Notably the broad PF (0.663) is *slightly less negative* than the filtered one
(0.587): the daily quality filters were, if anything, making the **long** edge
**worse** — the highest-quality-looking consolidations (tight, near-highs) faded
the hardest. The whole setup is a fade, and the "best" setups fade most.

### Where the loss lives (broad run, all hold-to-MOC)

Bucketed by holding time (entry → 16:00):

| held | trips | net P&L | avg/trip |
|---|---|---|---|
| < 1h | 34 | −$3,455 | −$102 |
| 1–2h | 46 | −$7,453 | −$162 |
| 2–3h | 56 | −$12,324 | −$220 |
| **> 3h** | **2,055** | **−$759,410** | **−$370** |

Entries concentrate in the first hour (1,265 at the 09:00 hour, 560 at 10:00), and
those morning entries are held >3h to MOC — carrying essentially all of the loss
(−$759k of −$782k), at −$370/trip. The longer a faded breakout long is held, the
worse it gets.

---

## Conclusion (as of 2026-06-29)

**Volume-confirmed intraday breakouts to new highs have a persistent negative long
edge** (~30% win rate, PF 0.59–0.66) across both the designed filtered universe and
a broad gapper universe. The intended thesis — that the tightness + volume-
confirmation combination would produce a high-PF day-trading long — is wrong; the
filters do not rescue it, and the quality filters make the long *worse*. Visual
inspection of all 50 filtered-run trips (charts in `data/charts/maxflyer/`)
confirms the numbers: the post-breakout patterns are random and volatile, and good
sustained trend days are extremely rare.

The result points the other way: a setup that loses as a long this consistently is
a **short candidate**. PF 0.66 long inverts to roughly PF ~1.5 gross before costs,
and the holding-time decomposition (the longer the hold, the bigger the loss)
suggests the short side may want a **time-stop** rather than hold-to-MOC.

---

## Run 3 — the short side + time-stops (broad universe)

The negative long edge is a positive short edge. Same broad-universe candidates, same
breakout entry signal, P&L sign flipped (`--short`), and a time-stop knob
(`--time-stop-min N`: flatten N minutes after entry, capped at MOC).

```
dotnet run --project TradingEdge.MaxFlyer -c Release -- \
  --max-tightness 1000000 --max-atr-pct 1000000 --min-52w-pct 0 --min-max-atr-log 0 \
  --short --time-stop-min {0,60,120,180}
```

| config | trips | win rate | net P&L | PF |
|---|---|---|---|---|
| **short, hold-to-MOC** | 2,191 | **69.0%** | **+$782,641** | **1.509** |
| short, 60m time-stop | 2,191 | 64.8% | +$344,338 | 1.300 |
| short, 120m time-stop | 2,191 | 69.0% | +$529,877 | 1.425 |
| short, 180m time-stop | 2,191 | 67.7% | +$543,642 | 1.391 |

- **The short is the exact mirror of the long** (PF 1.509 ≈ 1/0.663; net is the precise
  negation; 69% win = 100% − 30% − ties). The negative long edge is a genuine, large
  short edge: **PF 1.509 gross on 2,191 trips.**
- **The time-stop HURTS.** Every time-stopped variant is worse than hold-to-MOC. The
  faded-breakout move bleeds *all day* rather than reverting fast and bouncing, so
  cutting the short early leaves money on the table. (Consistent with Run 2's
  holding-time decomposition: the long loss grows monotonically with hold time, so the
  short profit does too.) Hold-to-MOC is the configuration.

### The practical long takeaway (independent of ever shorting)

A long who buys these breakouts would be **strictly better off doing nothing and
waiting ~2 hours**: at the <1h horizon the long loses −$102/trip, growing to −$370/trip
by >3h. High-volume breakouts to new intraday highs are a place to *avoid* as a long,
full stop.

## Run 4 — the real driver: extension, not the breakout

The breakout names are, by Gate-2 construction, already gapped ≥10% — and many have run
much further intraday before the breakout. So "the breakout fades" might really be
"*already-extended stocks* mean-revert," with the breakout incidental. Test: bucket the
broad short (hold-to-MOC) trips by **% change vs prior close at entry**
(`entry_price / prev_adj_close − 1`), post-hoc on the Run-3 trips CSV.

| % vs prev close at entry | n | win% | avg P&L/trip | PF |
|---|---|---|---|---|
| 10–25% | 470 | 63.8% | **+$65** | 1.121 |
| 25–50% | 859 | 67.5% | +$202 | 1.300 |
| 50–100% | 646 | 72.6% | +$492 | 1.592 |
| 100–200% | 188 | 74.5% | +$1,069 | **2.353** |
| **200%+** | 28 | 82.1% | **+$2,138** | **3.730** |

**The short edge scales monotonically with how extended the stock already is** — win
rate 64% → 82%, per-trip profit +$65 → +$2,138 (a 33× spread), and PF 1.12 → 3.73. The
least-extended names (up 10–25%) barely fade at all (PF 1.12 — sub-tradeable after
costs); the parabolic ones (up 100%+) fade hard and reliably (PF 2.35+). In PF terms the
tradeable region is roughly the **>50% band (PF ~1.6+)**, ideally **>100% (PF 2.35+)**.

**Conclusion: this is an *already-extended-stock* fade, not a breakout fade.** The
breakout is a timing trigger on top of the real driver — distance run from prior close.
The long-avoidance lesson sharpens accordingly: avoid buying breakouts *in extended
names* — a breakout in a stock up 5% is fine; a breakout in a stock up 80% is where the
damage is.

## Run 5 — mean-reversion targets (short, broad universe)

If the fade is a snap-back to a level, a take-profit target at that level should beat
holding to MOC. Three targets, swept (`--target vwap|ma|channel`, `--target-window N`):
session VWAP (running, bar-typical-price weighted), a fast SMA of closes, and the
Donchian channel low. For a short the target also doubles as the loss-cut — price that
never reverts rides to MOC. (Run via the candidate cache, see below.)

| config | win% | net P&L | PF |
|---|---|---|---|
| **hold-to-MOC (no target)** | 69.0% | **+$782,641** | **1.509** |
| vwap | 73.6% | +$150,668 | 1.188 |
| ma (10) | 67.6% | +$61,495 | 1.118 |
| ma (20) | 69.8% | +$46,229 | 1.065 |
| ma (50) | 73.6% | +$188,274 | 1.216 |
| channel (10) | 67.7% | +$115,688 | 1.145 |
| channel (20) | 69.8% | +$228,397 | 1.243 |
| channel (50) | 71.9% | +$429,862 | 1.366 |

**Every target is worse than holding to the close**, on both PF and net P&L. A
take-profit target *caps the winners* while doing nothing about the losers (the
non-reverters ride to MOC regardless), and the tight targets (vwap, ma50) even *raise*
win rate to 73.6% by booking many small reversions — but PF collapses because the few
huge all-day faders get cut short. Within each family, **wider is strictly better**
(ma10→ma50 PF 1.12→1.22; channel10→channel50 PF 1.15→1.37): every target converges
toward hold-to-MOC, which is its limit.

**Structural conclusion: the fade is a slow all-day BLEED, not a snap-back to a level.**
The short's profit comes from the *persistence* of the de-rating, so the only good exit
is the close. Both early-exit families tested — time-stops (Run 3) and price targets
(Run 5) — trade away edge. The mean-reversion-target / retire-the-stop thesis is
rejected: there is no level the move reliably returns to; it just bleeds.

## Run 6 — down-gappers (the inverse universe)

Runs 1–5 selected stocks that gapped **up** ≥10% (Gate 2 `gap ∈ [0.10, 2.0]`). Since
buying breakouts in *up-extended* names is negative EV, the symmetric question is whether
a breakout in a stock that opened **down** hard is the opposite — a genuine
reversal/squeeze worth buying. Test: flip the gap band to `[−2.0, −0.10]` (opened down
≥10%), broad universe (daily quality filters off), everything else as Run 2/3.

12,728 candidate days gapped down ≥10%; 573 of them produced a breakout entry → 647 trips.

| universe | side | trips | win% | net P&L | PF |
|---|---|---|---|---|---|
| down-gappers (≥10% down) | long | 647 | 40.2% | −$86,067 | **0.760** |
| down-gappers (≥10% down) | short | 647 | 57.5% | +$86,067 | **1.316** |

**Buying the down-gapper breakout is still negative** (PF 0.76) — less negative than the
up-gapper long (0.59) but a loser all the same. The "down a lot → reversal bounce" thesis
does not hold at the aggregate. **No breakout-buying works** — extended-up or beaten-down.
The short generalizes (PF 1.32), weaker than on up-gappers (1.51) and far fewer trips
(647 vs 2,191): **the breakout-to-new-intraday-high is a fade regardless of gap direction.**

**At-entry extension breakdown (down-gapper short).** Note `gap_pct` is the *open*
snapshot; `entry_price/prev_adj_close−1` is the *entry* snapshot, taken hours later when
the new-intraday-high trigger fires. A stock can open −15% and rally to +50% by the time
it breaks out (e.g. ACON 2025-01-30: gap −10.4%, broke out at +99.7% vs prev close; OCG
2025-12-10: gap −30%, broke out at +52%). The entry trigger *selects for intraday
strength*, so even a down-gapper universe contains names far above prior close at entry.

| % vs prev close at entry | n | PF |
|---|---:|---:|
| < −10% (still capitulating) | 150 | 1.276 |
| −10..0% (down) | 307 | 1.322 |
| 0..10% (recovered to ~flat) | 128 | **0.975** |
| 10..25% (recovered up) | 45 | 1.875 |
| 25..50% | 10 | 1.610 |
| 50..100% | 5 | 7.373 |
| >100% | 2 | (n too small) |

**The fade works at both extremes and dies only in the recovery-to-flat zone.** The short
is profitable both when the stock is still deeply down (−10%+, PF ~1.3, the bulk of the
trips) and when it has recovered/squeezed up (>10%, PF 1.6–7+) — the same monotonic
extension effect as Run 4, now confirmed symmetric. The *only* dead band is **0–10% vs
prev close (PF 0.975)**: a down-gapper that has clawed back to roughly flat and then breaks
out is a coin flip. That recovery-to-flat zone is the one place buying the breakout is
defensible — but it is neutral, not an edge.

**Inside the still-down-at-entry region (`< 0%`), the edge is thin and non-monotonic** —
unlike the smooth up-side scaling of Run 4. Finer breakdown of the down-at-entry trades:

| at-entry band | n | PF | win% |
|---|---:|---:|---:|
| −10..0% | 307 | 1.322 | 57.0 |
| −15..−10% | 75 | 1.616 | 54.7 |
| −20..−15% | 25 | 0.707 | 52.0 |
| −30..−20% | 27 | 1.803 | 48.1 |
| −40..−30% | 10 | 0.977 | 40.0 |
| −50..−40% | 6 | 2.297 | 66.7 |
| < −50% | 7 | 0.602 | 28.6 |

The aggregate `< −10%` PF (1.276) is carried by the mild **−15..−10%** slice (75 trips,
PF 1.62); everything deeper is 6–27 trips and bounces between PF 0.60 and 2.30 with no
trend. Note the **win rate decays monotonically with depth** (57% → 29%) even where PF
stays >1 — the signature of a fat-tailed, continuation-dependent edge: shorting an
already-crushed name wins *less often* but the rare collapse-to-zero pays for it. This is
a different statistical character from the up-extension short (a steady all-day de-rating
bleed at high win rate). The tradeable down-gapper short edge is concentrated in the mild
**−15..0%** band (382 trips, PF ~1.4); the deep-down tail is too sparse to lean on.

**Unified conclusion across Runs 1–6:** the breakout-to-new-intraday-high is a **fade**,
and its strength is governed by **distance from prior close at entry, in either direction**
— extended up (strongest) or still capitulating down. The breakout is a timing trigger on
the real driver (intraday extension), not the edge itself. There is no version of *buying*
this breakout that is positive EV.

## Run 7 — the downside breakout (the mirror)

Runs 1–6 traded the breakout to a new session **high**. The symmetric question: what does
a breakout to a new session **low** do — close below the running session low (08:30-onward),
same volume confirmation? Engine support added as `--downside` (independent of `--short`:
`--downside` picks the *signal*, `--short` picks the *P&L sign*; the protective stop flips
to the 2-bar high). Run on both gap universes, both sides.

| universe | dir | side | trips | win% | net P&L | PF |
|---|---|---|---|---|---|---|
| up-gappers | up (high) | short | 2,191 | 69.0% | +$782,641 | **1.509** |
| up-gappers | up (high) | long | 2,191 | 28.0% | −$14,766 | 0.587 |
| up-gappers | **down (low)** | **long** | 1,247 | 51.6% | +$165,911 | **1.342** |
| up-gappers | down (low) | short | 1,247 | 46.8% | −$165,911 | 0.745 |
| down-gappers | **down (low)** | **long** | 1,095 | 56.6% | +$147,032 | **1.320** |
| down-gappers | down (low) | short | 1,095 | 42.5% | −$147,032 | 0.758 |

**The breakout is a fade in *both* directions.** Break to a new high → short it (PF 1.51).
Break to a new low → **long it** (PF 1.32–1.34). The new-low fade long works on *both* gap
universes nearly identically (up-gappers 1.342, down-gappers 1.320), so it is independent
of which way the stock gapped. Economic reading: a volume-confirmed thrust to a fresh
session extreme is an **exhaustion/climax**, not a continuation — buyers exhaust at new
highs (fade short), sellers exhaust at new lows (fade long).

**At-entry extension breakdown — but the long is an INVERTED-U, not monotonic.** Unlike the
short (which scales monotonically — deeper extension = stronger), the new-low-fade long
peaks in the *middle* and dies at *both* tails (combined across both universes):

| % vs prev close at entry | n | PF | win% |
|---|---:|---:|---:|
| < −50% (death-spiral) | 118 | **0.399** | 35.6 |
| −50..−25% | 364 | 1.742 | 60.7 |
| −25..−10% | 657 | 1.689 | 58.4 |
| −10..0% | 282 | 1.748 | 61.7 |
| 0..10% | 591 | 1.686 | 52.6 |
| 10..25% | 225 | 1.308 | 40.0 |
| 25..50% | 67 | **0.555** | 43.3 |
| > +50% (failed runner) | 38 | **0.681** | 34.2 |

**The bounce long needs a *flush*, not a *trend*.** It works as a broad plateau from −50%
to +10% (PF ~1.7), decays through +10..25% (1.31), and loses at both extremes: a stock
already down >50% that makes a new low is **going to zero** (PF 0.40 — keep falling, no
bounce), and a stock still up >25% that breaks to a new session low is a **failed runner**
rolling over (PF 0.55–0.68 — keep falling). This is the key asymmetry vs the short: the
short fades *extension* (more is better, either tail); the long fades a *flush* (moderate
distance only — too little is no edge, too much is a trend you don't want to catch).

**Unified conclusion across Runs 1–7 — the breakout is a two-sided fade.** A volume-
confirmed break to a new intraday extreme reverts:
- new **high** → **short**, edge ∝ distance-up (monotonic, strongest at the parabolic tail);
- new **low** → **long**, edge in the *moderate* flush band (inverted-U: −50%..+10%),
  killed at the death-spiral (<−50%) and failed-runner (>+25%) tails.
Buying a new-high breakout or shorting a new-low breakout is negative EV in every cut. The
breakout is a timing trigger; the edge is exhaustion reversion off a session extreme.

## Run 8 — the wick-breakout trigger (`--wick-breakout`)

Runs 1–7 fire only when a bar **closes** through the prior session extreme (a confirmed
breakout). The alternative is to fire the moment the bar's **high/low wick pierces** the
extreme, even if it closes back inside — `--wick-breakout` (close trigger stays the
default). This admits the weaker "pierce-but-reject" breakouts and fires more often and
earlier. Does the two-sided-fade edge survive the looser, noisier trigger? Same broad gap
universes (quality filters off), hold-to-MOC.

| universe | dir | fade side | trips (close → wick) | PF (close → wick) |
|---|---|---|---|---|
| up-gappers | up (high) | short | 2,191 → **4,172** | 1.509 → **1.427** |
| down-gappers | up (high) | short | 647 → **855** | 1.316 → **1.324** |
| up-gappers | down (low) | long | 1,247 → **1,591** | 1.342 → **1.192** |
| down-gappers | down (low) | long | 1,095 → **1,432** | 1.320 → **1.233** |

**The edge survives, slightly softened.** The wick trigger roughly doubles the up-breakout
short count (4,172 trips) and the short stays strongly profitable (PF 1.427, net **+$1.29M**
gross) — the extra "pierce-but-close-inside" new highs *also* fade. PFs drop a little
(1.51→1.43, 1.34→1.19) because the looser trigger admits marginal breakouts that revert
less cleanly, but every cell keeps its sign and the wrong-side trades stay losers
(up-breakout long 0.70–0.76, down-breakout short 0.81–0.84). **More signal, modestly lower
quality per trade** — the classic confirmation trade-off.

### At-entry extension breakdowns (wick trigger)

**(A) Up-breakout SHORT, up-gapper universe — still monotonic.** Deeper extension at entry
= stronger fade, exactly as Run 4 (close trigger), now on 4,143 trips:

| % vs prev close at entry | n | shortPF | win% |
|---|---:|---:|---:|
| 10–25% | 978 | 1.008 | 63.2 |
| 25–50% | 1,545 | 1.273 | 67.9 |
| 50–100% | 1,179 | 1.604 | 72.9 |
| 100–200% | 380 | 1.818 | 72.1 |
| **200%+** | 61 | **3.576** | 80.3 |

**(B) Up-breakout SHORT, down-gapper universe.** Works at both ends, weakest in the
recovery-to-flat band (mirrors Run 6 — the 0–10% slice is the softest, PF 1.06):

| % vs prev close at entry | n | shortPF | win% |
|---|---:|---:|---:|
| < −10% (still capitulating) | 272 | 1.382 | 53.3 |
| −10..0% | 372 | 1.218 | 57.5 |
| 0..10% (recovered to ~flat) | 138 | **1.064** | 65.2 |
| 10..25% | 52 | 2.120 | 71.2 |
| 25..50% | 12 | 1.978 | 91.7 |
| >50% | 9 | 1.960 | 66.7 |

**(C) Down-breakout LONG, combined universes — still an inverted-U.** Plateau across the
moderate flush band, dies at both tails (death-spiral and failed-runner), exactly as Run 7:

| % vs prev close at entry | n | longPF | win% |
|---|---:|---:|---:|
| < −50% (death-spiral) | 145 | **0.376** | 35.9 |
| −50..−25% | 457 | 1.568 | 57.5 |
| −25..−10% | 867 | 1.557 | 56.6 |
| −10..0% | 318 | 1.634 | 59.7 |
| 0..10% | 755 | 1.488 | 49.4 |
| 10..25% | 331 | 1.159 | 40.2 |
| 25..50% | 97 | **0.685** | 40.2 |
| > +50% (failed runner) | 53 | **0.505** | 34.0 |

All three shapes from the close trigger survive intact — the wick trigger just adds trips
without changing the *structure* of the edge.

**Takeaway:** the close trigger is cleaner per-trade (higher PF); the wick trigger trades
quality for ~1.5–2× the sample. For the profitable short book the wick variant is arguably
better in absolute terms (far more trips at still-strong PF); for the inverted-U long the
close trigger's tighter selection is preferable. Either way the two-sided-fade thesis is
robust to the trigger definition.

## Run 9 — trail-entry: short the rollover, not the thrust (`--trail-entry`)

Shorting *straight into* a high-volume breakout invites the run-over: occasionally the name
doubles again before it fades, and a stopless hold-to-MOC eats a catastrophic single loss.
The trail-entry model addresses this. The breakout no longer enters — it **arms** a signal;
a trailing 2-bar low then ratchets *up* with the move, and the short fills only when a bar
**closes back below** that trail (the rollover off the top). A protective stop sits at the
**session high** at fill — if price runs back to a new high, we're out. (Opt-in
`--trail-entry`; default behaviour unchanged. Mirror for the long: trail a 2-bar high
ratcheting down, enter on a close above, stop at the session low.) Up-gapper universe,
close trigger, hold-to-MOC after fill.

| | direct (stopless, hold-to-MOC) | **trail-entry (session-high stop)** |
|---|---|---|
| trips | 2,962 | 2,949 |
| net P&L | +$880,677 | +$311,135 |
| PF | 1.397 | 1.208 |
| win% | 67.7% | 39.6% |
| mean / trip | +$297 | +$106 |
| **std / trip** | **$3,107** | **$1,469** |
| per-trip Sharpe (mean/std) | 0.096 | 0.072 |
| **worst single trade** | **−$52,819** | **−$4,381** |

**Trail-entry fixes the run-over but at a real cost to the edge.** It halves per-trade
volatility ($3,107 → $1,469) and **caps the worst trade 12× ($52.8k → $4.4k)** — the
session-high stop means you are *never* run over. But it gives up ~⅔ of the net P&L and a
chunk of per-trip Sharpe (0.096 → 0.072), because waiting for the rollover misses the
names that fade immediately and the session-high stop shakes you out of names that wiggle
to a fresh high before bleeding all day. **The aggregate verdict: the run-overs were
*priced in* — the all-day bleed more than pays for the occasional −$50k hit, so on a
per-trade basis the stopless direct short is still better risk-adjusted.** (Caveat: with a
bounded ~$4.4k worst case you can size the trail book far larger per unit of capital-at-
risk, and a −$4.4k tail is survivable where a −$53k tail is account-ending — neither of
which the per-trade Sharpe captures.)

**Extension breakdown — and trail-entry hurts *most* exactly where the direct edge is
strongest.** PF and worst-trade by at-entry %-vs-prev-close:

| extension @ entry | direct PF | trail PF | direct worst | trail worst |
|---|---:|---:|---:|---:|
| 10–25% | 1.147 | 1.150 | −$42,607 | −$2,805 |
| 25–50% | 1.216 | 0.967 | −$52,819 | −$3,974 |
| 50–100% | 1.418 | 1.319 | −$47,375 | −$3,848 |
| 100–200% | 1.891 | 1.598 | −$17,144 | −$3,560 |
| **200%+** | **3.706** | **1.371** | −$7,327 | −$4,381 |

Two findings stand out:

1. **The trail kills the high-extension jackpot.** Direct PF scales monotonically to **3.71**
   at 200%+ (the parabolic fade); the trail collapses that to **1.37**. The session-high stop
   keeps shaking you out of the most-extended names right before they finally roll over —
   and those are precisely the names with the biggest, most reliable all-day de-rating. The
   direct short's clean monotonic 1.15→3.71 becomes a flat, non-monotonic ~1.0–1.6.
2. **The catastrophic run-overs are NOT in the high-extension tail — they're in the mid
   bands.** Direct worst-trade is −$53k at 25–50% and −$47k at 50–100%, but only −$7k at
   200%+. A stock already up 200% is exhausted and fades; a stock up only 40% still has room
   to double on you. So trail-entry's tail-protection is well-targeted at 25–100% but its
   edge-cost lands hardest at 200%+ (where run-over risk is already lowest). The mis-match
   suggests the real play is **trail-entry in the mid bands, direct in the 200%+ tail** —
   a follow-up.

**Conclusion: better risk management ≠ better risk-adjusted return here.** The trail-entry
stop does exactly what it was designed to (bounded loss, no run-over, half the volatility),
but the fade edge is strong and *persistent* enough that giving up the immediate-drop and
the high-extension bleed costs more edge than the tail-protection is worth on a per-trade
basis. It is a *survivability / sizing* tool, not an alpha improvement.

### Frontside vs backside, both stopless — isolating the entry timing

Run 9's trail-entry bundled two changes: *backside* entry (wait for the rollover) AND the
session-high stop. To isolate the timing, run trail-entry **stopless** (the stop is now
opt-in via `--intraday-stop` in both entry models) and compare to the frontside stopless
short, both hold-to-MOC:

| variant | trips | net P&L | PF | win% |
|---|---|---|---|---|
| frontside, stopless | 2,962 | +$880,677 | **1.397** | 67.7% |
| **backside, stopless** | 2,949 | +$702,467 | **1.329** | 65.3% |
| backside + session stop (Run 9) | 2,949 | +$311,135 | 1.208 | 39.6% |

**The entry-timing change alone costs only ~0.07 PF (1.397 → 1.329); the session stop costs
another ~0.12 (→ 1.208) — the stop is ~2× more damaging than the backside timing.** And
backside-stopless keeps a high win rate (65%, vs the stop's 40%): waiting for the rollover
fills at a slightly worse price but still captures the same all-day bleed, whereas the stop
shakes you out of the winners.

But the aggregate hides a **reshaping of the edge across extension** — backside isn't a
uniform shift, it has a sweet spot:

| extension @ entry | frontside PF | backside PF | front worst | back worst |
|---|---:|---:|---:|---:|
| 10–25% | 1.147 | **0.954** | −$42,607 | −$43,411 |
| 25–50% | 1.216 | 1.102 | −$52,819 | −$56,683 |
| 50–100% | 1.418 | 1.429 | −$47,375 | −$20,801 |
| **100–200%** | 1.891 | **2.380** | −$17,144 | −$15,312 |
| 200%+ | **3.706** | 2.040 | −$7,327 | −$9,175 |

- **Low extension (10–50%): backside is worse** — at 10–25% it goes *negative* (0.95). Mild
  movers have little rollover to wait for; the close-below confirmation just enters late.
- **High-mid (50–200%): backside is BETTER**, clearly so at **100–200% (1.89 → 2.38)**. On a
  strongly-extended name the rollover confirmation earns its keep — it skips the names still
  grinding up and enters the ones actually turning. This is the "short the backside" thesis
  working.
- **200%+ tail: backside is worse (3.71 → 2.04)** — the most parabolic names fade so
  violently that any delay forfeits much of the drop; frontside catches the whole move.

Worst-trade is ~unchanged frontside vs backside (the timing alone doesn't help the tail —
only the stop does). **Net: backside entry has a real sweet spot in the 50–200% band; it is
worse at the mild-mover and the extreme-parabolic ends.** The strongest single configuration
for the 100–200% band is backside-stopless (PF 2.38); for the 200%+ tail it is
frontside-stopless (PF 3.71).

## Run 10 — wide catastrophe stops (`--pct-stop`)

If the session-high stop is too tight (it shakes you out of winners), maybe a *very* wide
stop — 50% to 100% adverse from entry — catches only the true account-killers (a short that
doubles on you) while letting normal noise bleed to MOC. `--pct-stop X` covers a short if
price rises X above the entry. Frontside short, up-gapper universe, hold-to-MOC otherwise.

| variant | net P&L | PF | std/trip | worst | # stopped |
|---|---|---|---|---|---|
| stopless | +$880,677 | **1.397** | $3,107 | −$52,819 | — |
| +50% stop | +$453,066 | 1.181 | $2,443 | −$9,036 | 378 |
| +75% stop | +$538,948 | 1.214 | $2,700 | −$11,079 | 207 |
| +100% stop | +$562,038 | 1.224 | $2,896 | −$29,133 | 129 |

**No — even very wide stops do not preserve the edge.** PF drops materially (1.40 → 1.18–1.22)
at every level. Two reasons:

1. **A 50–100% adverse move is not rare here — it's regular.** The 50% stop fires on **378
   trades** (still 129 at 100%). A high-volume gapper running 50%+ past your short before it
   fades is an ordinary event in this universe, not a once-a-year black swan.
2. **The stopped-out names are the ones that fade hardest.** A stock up 80% that runs to
   +130% (tripping the 50% stop) is exactly the parabolic name whose all-day de-rating pays
   the most — the stop realizes the loss at the blow-off top, right before the bleed. Same
   mechanism that killed the session-high stop, just at a wider threshold.

Extension breakdown — the damage concentrates where the edge is richest:

| band | stopless PF | +50% stop | +100% stop |
|---|---:|---:|---:|
| 10–25% | 1.147 | 0.968 | 1.103 |
| 25–50% | 1.216 | 1.052 | 1.058 |
| 50–100% | 1.418 | 1.276 | 1.314 |
| **100–200%** | **1.891** | 1.329 | 1.372 |
| **200%+** | **3.706** | 2.526 | 2.496 |

The 100–200% band is gutted (1.89 → 1.33) and the 200%+ jackpot drops (3.71 → ~2.5) — the
parabolic faders routinely run 50–100% against you *before* rolling over, so any stop at
that level clips them at the top. Even 10–25% goes negative under the 50% stop (the rare
mild-mover that runs 50% is a catastrophe the stop *realizes* instead of letting recover).

**Wider is monotonically better** (1.18 → 1.22 as 50% → 100%; +100% beats +50% in every mid
band), converging toward stopless — the same pattern as the Run-5 targets and the Run-9
trail stop. There is no sweet-spot stop level that beats holding; the limit (infinitely
wide = stopless) is the best. And the tail trade-off is poor: the 50% stop cuts the worst
trade −$53k → −$9k but costs ~$430k of net P&L to do it.

**The frontside fade edge is inseparable from sitting through the adverse excursion.** The
profit comes from holding the parabolic faders through their run-up to the eventual
de-rating; a stop tight enough to bound the loss is also tight enough to forfeit the win.
Every exit mechanism tested — tight stops (Run 1), time-stops (Run 3), price targets
(Run 5), the session-high stop (Run 9), and now wide %-stops (Run 10) — loses to
hold-to-MOC. Risk control on this setup is a *position-sizing* problem, not a *stop*
problem.

## Run 11 — wait for the parabolic move (`--rise-entry`)

Every *exit* mechanism failed. The opposite lever is *entry selection*: instead of shorting
the breakout, **wait for the name to actually go parabolic and short *that*.** The 10–25%
band is barely above breakeven (PF 1.15) precisely because those names haven't extended —
the edge lives in the extended names. `--rise-entry 0.5` arms on the breakout and enters the
short only once price has run a further **+50% past the breakout price** (fills at the +50%
level; gap-through at the bar open). Combine with `--trail-entry` to instead wait for the
rollover *after* the +50%. Frontside, up-gapper universe, hold-to-MOC.

| variant | trips | PF | win% | net P&L |
|---|---|---|---|---|
| short on the breakout (stopless) | 2,962 | 1.397 | 67.7% | +$880,677 |
| **rise +50%, immediate** | 378 | **1.726** | 70.1% | +$284,848 |
| rise +50% + trail (rollover after) | 373 | 1.659 | 70.2% | +$251,009 |

**Waiting for the +50% parabolic move lifts PF 1.40 → 1.73** — a large jump in trade
*quality*. Only ~13% of breakouts ever run +50% (2,962 → 378 trips), and those are exactly
the extended names where the fade is rich; the mild movers are skipped. The immediate fill
slightly beats the rollover (1.726 vs 1.659): once you've already waited for +50%, the name
often starts fading right there, so the extra rollover delay forfeits the first leg down
(consistent with Run 9b — backside timing is marginally worse in the extended zone).

**This is the first thing that improves risk-adjusted return, not just caps loss:**

| | short on breakout | rise +50% |
|---|---:|---:|
| mean / trip | +$297 | **+$754** |
| **per-trip Sharpe (mean/std)** | 0.096 | **0.181** |
| worst trade | −$52,819 | **−$31,880** |

Per-trip Sharpe nearly **doubles** (0.096 → 0.181) and the worst trade shrinks (−$53k →
−$32k) — **with no stop at all**. By letting the name run +50% before shorting, you simply
*don't take* the trades that would have kept running; the ones left are exhausted and fade.
Extension breakdown (no sub-25% entries exist by construction — all have run +50%):

| at-entry band | n | PF | win% | worst |
|---|---:|---:|---:|---:|
| 50–100% | 89 | 2.336 | 73.0 | −$25,072 |
| 100–200% | 218 | 1.359 | 67.4 | −$31,880 |
| 200%+ | 71 | 2.899 | 74.6 | −$8,096 |

The mild-mover band is gone; every trade sits in the rich part of the curve. The 100–200%
band is weakest (PF 1.36 — ran +50% to land merely *strong*, not yet exhausted); the 200%+
band (already exhausted) is best (PF 2.90), suggesting a *larger* rise gate may push further
into exhaustion.

**Key reframing: risk control here is a *selection* problem, not a *stop* problem.** Every
exit that tries to cut the bad trades short also cuts the good ones (the edge is the
persistent bleed). But *not taking* the trade until the name has proven extended both raises
the edge and improves risk-adjusted return. Don't stop the bad trades — don't enter them.

## Run 12 — conditional day-extension gate (`--ext-gate`)

Run 11's `--rise-entry` measured the move *from the breakout price*, which is restrictive — a
stock that breaks out *already* at +60% has to run to +140% to qualify. The more natural gate
is an **absolute day-extension** threshold (% vs prev close), applied conditionally on where
the name was at the breakout:

- **≥ 50% at the breakout** → enter **direct** (it's parabolic now; short the breakout close).
- **< 50% at the breakout** → arm a **rollover** (trail-entry style) and take it **only if**
  the stock has reached ≥ 50% by the time it rolls over; if it rolls over still < 50%, skip.

So every fill is a name that is ≥ 50% on the day, but the entry *style* adapts: direct for
the already-extended, rollover-confirmed for the climbers. (`--ext-gate 0.5`; threshold vs
prev close; `prevClose` threaded into the intraday engine.)

| strategy | trips | PF | net P&L | per-trip Sharpe | worst |
|---|---|---|---|---|---|
| frontside (short every breakout) | 2,962 | 1.397 | +$880,677 | 0.096 | −$52,819 |
| **ext-gate 50% (conditional)** | 1,278 | **1.573** | **+$682,631** | 0.148 | −$47,375 |
| rise +50% from breakout (Run 11) | 378 | 1.726 | +$284,848 | 0.181 | −$31,880 |

**The conditional gate is the sweet spot between the two.** It keeps ~77% of the
all-breakouts net P&L (+$682k vs +$880k) while lifting PF 1.40 → 1.57 and improving per-trip
Sharpe ~50% (0.096 → 0.148). It keeps far more trips than Run 11 (1,278 vs 378) because the
threshold is absolute day-extension, not +50% *from the breakout* — a name breaking out
already at +60% qualifies for an immediate direct entry (Run 11 would have made it run to
+140%). So you capture all the already-extended breakouts *plus* the climbers that confirm a
rollover past 50%.

Extension breakdown — the `< 50%` band is empty by construction (the gate refuses it), and
the rest is the clean monotonic curve with the mild-mover drag removed:

| at-entry band | n | PF | win% | worst |
|---|---:|---:|---:|---:|
| < 50% | 1 | — | — | (rounding edge) |
| 50–100% | 930 | 1.363 | 70.9 | −$47,375 |
| 100–200% | 296 | 1.965 | 73.6 | −$17,144 |
| 200%+ | 51 | 3.557 | 78.4 | −$7,327 |

**Trade-off vs Run 11:** the conditional gate's worst trade (−$47k) is larger than Run 11's
(−$32k), because the *direct* leg shorts names already ≥ 50% at the breakout and some keep
running before fading (the run-over), whereas Run 11's "+50% from breakout" implicitly waited
longer and entered more-exhausted names. So the conditional trades a bit more tail risk for
~2.4× the net P&L. **Ranking by purpose: most net + good PF → ext-gate; best PF + lowest tail
→ rise-entry; raw size → frontside-all.** All three beat every *exit*-based variant.

### Addendum — layering a rise/rollover race on the `< 50%` branch (no effect)

The finer ext-gate breakdown shows the edge is weak/fat-tailed in the **50–100%** band
(PF ~1.36, the bulk — 930 trips — and the catastrophic −$47k/−$21k worst trades) and clean
above 100% (PF 1.88 → 3.87 by 200–300%, worst only −$7k). The 50–100% trips are *direct*
entries (names already ≥ 50% at the breakout, shorted straight in). A proposed refinement:
in the `< 50%`-at-breakout branch, race two confirmations — if price runs **+50% from the
breakout price** first, take it; else wait for the **rollover** (close back through the
2-bar trail) and fill only if extension ≥ 50% by then, else skip (`--ext-gate 0.5
--rise-entry 0.5`).

| variant | trips | PF | net | per-trip Sharpe | worst |
|---|---|---|---|---|---|
| ext-gate 0.5 (rollover-gated only) | 1,278 | 1.573 | +$682,631 | 0.148 | −$47,375 |
| ext-gate 0.5 + rise 0.5 (race) | 1,278 | 1.573 | +$686,778 | 0.149 | −$47,375 |

**The rise leg is mechanically correct but economically inert.** Same trips, same PF, same
worst trade; only **68 entries re-timed** (rise fired before the rollover) for a ~$4k net
difference. The reason is structural: a name in the `< 50%` branch (broke out below +50% on
the day) needs a *further* +50% from its breakout price to trigger the rise — a large move
that almost always carries it past the rollover point first, so the rollover wins the race.
The rollover-gated path already captures these names; the rise race just occasionally
re-prices the same fill.

**And the −$47k tail is untouched — because it is not in this branch.** The worst trades are
in the *direct* leg (≥ 50% at the breakout), which neither the rise nor the rollover logic
governs. Attacking that tail would require confirming the *direct* leg too (never enter
straight in), which thins the 50–100% band — or, more simply, **capping position size in the
50–100% band** rather than adding entry confirmation. Layering confirmation on the `< 50%`
branch is a dead end.

## Reproducibility — candidate cache

The daily scan (pipeline 1) is invariant to every intraday/exit/target knob, so it is
cached once and the intraday experiments replay from it:

```
# collect the broad candidate set once (~5s):
dotnet run --project TradingEdge.MaxFlyer -c Release -- \
  --max-tightness 1000000 --max-atr-pct 1000000 --min-52w-pct 0 --min-max-atr-log 0 \
  --candidates-out /tmp/maxflyer_broad_cands.csv
# every intraday experiment then replays (~20s) instead of re-scanning (~25s):
dotnet run --project TradingEdge.MaxFlyer -c Release -- \
  --candidates-in /tmp/maxflyer_broad_cands.csv --short --target channel --target-window 50
```

Verified: cached and full runs agree to the cent (net P&L `782,641.41` both ways).

### Next experiments
- **Extension-conditioned entries** — the Run-4 breakdown says the edge lives in the
  >50%-from-prev-close band; condition entries on extension. The volume-confirmation
  trigger may even be loosenable — extension is the driver, not the breakout shape.
- **Side-correct protective stop** for the short (a 2-bar-HIGH stop) — not yet tested;
  every short run above is stopless (the target was meant to be the loss-cut, but the
  targets lose, so a real stop is the open question for risk control).
