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

| % vs prev close at entry | n | win% | avg P&L/trip |
|---|---|---|---|
| 10–25% | 470 | 63.8% | **+$65** |
| 25–50% | 859 | 67.5% | +$202 |
| 50–100% | 646 | 72.6% | +$492 |
| 100–200% | 188 | 74.5% | +$1,069 |
| **200%+** | 28 | 82.1% | **+$2,138** |

**The short edge scales monotonically with how extended the stock already is** — win
rate 64% → 82%, and per-trip profit from +$65 to +$2,138 (a 33× spread). The
least-extended names (up 10–25%) barely fade at all; the parabolic ones (up 100%+) fade
hard and reliably.

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
