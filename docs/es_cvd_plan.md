# ES CVD imbalance — research plan

Status: **CLOSED 2026-11-15, negative result** (opened 2026-10). Three passes on the fit sample found nothing tradeable;
the holdout (2024+) was never read. See "Close-out" at the end.

## Idea

A discretionary order-flow swing on ES, as footprint traders teach it: a selloff builds a large negative cumulative volume delta
(CVD), the market exhausts, CVD flips positive, enter long *off the bottom* and ride to a
longer-horizon mean. Closest existing system: FlowSwing (crypto; price below the 72h MA +
4h CVD flip, cover at the MA). CVD windows here must be shorter than 4h; the style trades mostly
around the open.

## Measured before design (ES RTH, 2025 H1, 125 days)

| window | corr(price change, delta) | std of delta/volume | std of delta (contracts) |
|---|---|---|---|
| 1m | 0.56 | 0.108 | 411 |
| 5m | 0.57 | 0.061 | 1075 |
| 30m | 0.57 | 0.031 | 3052 |
| 2h | 0.52 | 0.021 | 5951 |

- CVD is mostly price restated, so a raw "CVD very negative" feature is largely "price fell".
- Imbalance scale shrinks with window length: every threshold must be z-scored per window.
- Prior (PitFader PIT-8/PIT-14): on equity indexes heavy with-the-flush delta at entry fails
  holdout; delta >= 0 at the new low holds. Argues for arm-then-trigger, not one feature.

## Features — first pass (agreed)

Arming ("CVD is very negative"):
1. CVD drawdown z: CVD minus its running max since an anchor (Globex open or prior RTH
   close), over the typical delta scale for that much volume.
2. Volume-clock windows: window length as a fraction of 20-day ADV (about 0.5% to 25%).
3. Absorption residual: delta minus what the price move predicts.

Trigger ("it has flipped"):
1. Retracement from trough: CVD recovered k sigma (or a fraction of the drawdown) from its
   running min. Causal; enters off the bottom by construction.
2. Fast/slow cross: slow window latched below -2z recently, fast window crosses above zero.
3. Trapped-seller reclaim: price back above the VWAP of sell-aggressor volume in the
   decline (sell volume = (volume - delta) / 2).

Distance / target: distance from the 24/48/72h VWAP in volatility units; prior-day
volume-profile mode as a second target.

## Parked — operator suggestions, add after the first pass

- **20m-high anchor reset.** Reset the CVD drawdown anchor on a new 20-minute high, the way
  the leg counters reset in the Fader systems. A *separate* feature from the
  session-anchored drawdown, not a replacement.
- **OLS slopes** of price, of volume, and of the CVD itself over the window.
- **Open interest.** Operator is asking Malcolm whether he has OI data anywhere.

## Method

Event study before any backtest, with a price-only twin as the control (same stretch
condition, price reclaim of equal size, no CVD). Fit 2020-2023, hold out 2024 onward.

## First pass — FIT sample only (2020-07 .. 2023-12, 897 sessions); holdout 2024+ NOT looked at

Built: `scripts/futures/escvd_bars.py` (continuous front-month 1m bars, forward-adjusted, dense
1380-minute session grid -> `data/futures/escvd/es_1m.parquet`), `escvd_lib.py`, `escvd_events.py`
(flip events + price twin -> `events.parquet`), `escvd_report.py head`, `escvd_surface.py` (bar level).
Logs: `logs/escvd_*.log`. Usable range 2020-06-12 .. 2026-03-13 (before: ESM20 is missing, ESU20 is
not yet the front; after: ESM26 is missing, ESH26 is dying).

Operator's prior: the style holds losers ~20m and winners 40-60m, so the edge should sit inside 1h.

**Result: no edge in either construction; the CVD flip does not beat the price twin, and neither
beats zero.** Returns in bps from the next bar's vwap, session-clustered t.

Flip events (drawdown >= a day-sigmas from the running peak since 09:30, retraced 25%):

| event | n | 30m | 60m |
|---|---|---|---|
| every RTH bar | 349,830 | +0.17 (+0.8) | +0.38 (+0.9) |
| CVD flip, 1.0 sigma | 512 | -0.05 (-0.0) | -0.92 (-0.5) |
| CVD flip, 1.5 sigma | 164 | -3.36 (-1.8) | -5.72 (-2.0) |
| CVD flip, 2.0 sigma | 62 | -5.12 (-1.8) | -2.25 (-0.4) |
| price twin, 0.58 sigma | 990 | -0.33 (-0.3) | +0.88 (+0.5) |
| price twin, 0.97 sigma | 363 | -1.02 (-0.5) | -1.12 (-0.4) |
| price twin, 1.30 sigma | 158 | -2.35 (-0.7) | -0.12 (-0.0) |

- 60m MFE and MAE after a flip are symmetric (about +20 / -20 bps, +32 / -34 in 2022): a coin flip.
  No year is consistently positive; 2022 is not the culprit.
- Bar level (296k RTH bars, entries 09:30-15:00): the correlation of the 60m forward return with
  delta over 5/15/30/60/120m is +0.008 .. +0.029, with price change over the same windows
  +0.005 .. +0.027. In a joint regression no delta term reaches |t| 2 at 30m or 60m, i.e. delta
  holding price fixed (the linear absorption residual) adds nothing. Same in the 09:30-11:30 window.
- The pattern itself (below the 24h VWAP, 60m delta in its lowest decile, 15m delta turned up, top
  quintile) is NEGATIVE over the next 30m: -4.0 bps (t -1.6, n 724) at any stretch, -7.1 (t -2.0,
  n 215) at >= 0.5 sigma, -14.4 (t -1.8, n 56) at >= 1 sigma. The price twin shows the same sign.
  Thin, opposite to the hypothesis.
- Weak continuation: bars deep in a CVD drawdown (>= 1.5 sigma since 09:30) drift -3.3 bps over 60m
  (t -2.4).
- The flip as defined is rare and late: median 13:31 ET, 179 bars after the CVD peak. It is not an
  "around the open" event.
- Sanity: contemporaneous corr(delta, price change) 0.58-0.65 at 1-60m; the 5m price reversal shows
  up at 10m (t -3.1). The pipeline sees known effects.
- Power: sd of a 60m return is 32 bps, so a once-a-day signal needs about 2 bps to reach t 2 here.
  A small edge is not excluded; a 70%-winrate signature from these mechanical definitions is.

## Second pass — the method as taught, the leg engine, the ceiling test (FIT only; holdout still sealed)

**The method as taught** (from two public footprint-trading tutorials). In the trader's words:
- CONTEXT first, before any chart: a directional bias for the day/week from macro and from the multi-day volume
  profile (where the volume clumps are, "tested the buyers from the previous day"). He then trades ONE side only
  ("I'm only getting long. Even when I lose... I get back long").
- EXECUTION: in a range, at the bottom, he wants "aggression from the other side" — footprint imbalances (89 sold vs
  0 bought at a price), a candle with delta -3169 (5m delta sigma is ~1,075). He marks the PRICE ZONE where that
  selling happened (5353-55 on 2024-08-12) and buys when price LEAVES THE ZONE UPWARD — the sellers are now offside
  and their exit is the fuel. Stop just below. "It can take 20 to 60 minutes for the order flow to clear."
- So the trigger is a price reclaim of the sellers' zone, not a CVD sign flip; the arming is concentrated sell
  aggression at a range low; and the bias filter comes from outside the tape.
- The two worked examples (2024-08-12 / 13) are in OUR HOLDOUT. Not looked at.

**The leg engine** (`escvd_legs.py` -> `data/futures/escvd/legs.parquet`, report `escvd_legs_report.py`). The
operator's anchor: a new N-minute high resets the leg (N = 20, 60); armed by a new N-minute low. The leg carries
cumD, its peak/trough, the SELLERS' vwap (sell volume = (vol - delta)/2), the worst 5-minute delta window (climax),
OLS slopes of price / CVD / volume. Six triggers, once per trough, entry next bar's vwap: sv (close > sellers'
vwap), cx (close > climax-window high), p25 / p50 (price retrace: the twins), c25 / c50 (CVD retrace). Both sides.
A bracket outcome too: stop under the leg low, 1R / 2R target, 60m cap.
- RTH entries 09:30-15:00, 81,328 events, 897 sessions. Pooled both sides, N=20: sv f30 -0.04 bps (t -0.2), f60
  +0.05 (+0.2); p25 -0.08 / -0.14; every trigger's long side is slightly positive and its short side slightly
  negative by about the same amount — the 2020-23 drift, not a signal. The 1R bracket is NEGATIVE for every
  trigger (sv pooled -0.04 R, t -4.4; win rate 0.46-0.52): the stop under the leg low gets hit more than its share.
- No feature sorts the sv events: quintiles of leg depth, leg delta z, CVD drawdown, climax z, slopes, VWAP
  distance, 5d/20d trend, prior-day POC / value area, session position — none monotone, none |t| > 3 at an end.

**The ceiling test** (`escvd_gbm.py`): LightGBM on every 3rd RTH bar (98,670 bars), leave-one-half-year-out, fixed
parameters. P = 41 price / volume / context features (returns, slopes, VWAP distance, leg depth and rebound, both
sides, N 20 and 60); D = 28 delta features (dz_W, CVD slopes, leg cumD / drawdown / rebound / sellers'-vwap gap,
5m delta extremes, overnight and day-so-far delta). Out-of-fold correlation with the forward return:

| horizon | P | P + D | D alone |
|---|---|---|---|
| 10m | +0.026 (t +4.2) | +0.012 (+1.9) | -0.013 (-2.4) |
| 30m | +0.016 (+1.7) | +0.011 (+1.1) | -0.016 (-2.2) |
| 60m | +0.028 (+2.4) | +0.024 (+2.0) | -0.005 (-0.5) |

(session-constant features left out; with them every number is lower.) Open only, 09:30-11:30: the same picture
(60m: P +0.030, PD +0.032, D -0.010).
- **Delta adds nothing to price** at 10-60 minutes: P + D never beats P, and D alone is negative out of fold.
- Price alone has a faint signal: top decile of the prediction +2.3 bps over 60m (t 2.4), bottom -1.0. Under a
  round trip's cost by itself.
- Three independent looks now agree (day-scale flips, leg triggers, the ceiling test): 1-minute BAR delta on ES
  has no 10-60m information beyond price in 2020-2023.

**Still open** — what bar delta cannot see: the footprint (per-price imbalance, WHERE in the bar the selling sat),
delta split by trade size (the tick tape has it), and the outside-the-tape bias. Other markets (the engine is
root-agnostic; FlowSwing's edge was in crypto, PitFader's in the volatile roots, not the equity indexes).

## Data

`data/futures/intraday_1s_ticks/` — ES 2020-05-29 to 2026-03-20, 24h Globex, per-second
`delta` from aggressor flags. No continuous contract yet: a front-month roll is the first
build step (see `futures_data.md`).

## Third pass (2026-11-15): the per-price footprint and the trapped-seller zone

Fit sample only (2020-06-12 .. 2023-12-31); the holdout is still unread, the tutorial's 2024-08-12/13 included.

**Built**
- `scripts/futures/escvd_fp.py`: the tick tape (`/mnt/d/trading-edge-bulk/futures/ticks/ES`) into
  `data/futures/escvd/fp_price.parquet` (session, minute, pt = price * 4, bv, sv: 14.3M rows) and `fp_min.parquet`
  (per minute: volume by aggressor-BURST size <5 / 5-19 / 20-99 / 100+, sweeps, counts). A burst = consecutive fills,
  one side, one millisecond (Sierra's sweep markers plus the same-millisecond rule). Volume equals the 1-minute bars on
  every bar; delta on 99.9 %.
  - ⚠ It reads 6 sessions at a time. Reading a whole contract (77M fills) took 12.5-13.4 GB and was OOM-killed twice,
    taking the editor with it. Peak now 3.1 GB.
- `escvd_zones.py`: the zone engine, both sides. Trough = a low below the lows of the N bars before (N 20 / 60); zone =
  the K ticks up from it (K 2 / 4 / 6 / 8 / 12) with the footprint of the 3 bars ending at the trough; trigger = the first
  close at or above L + K within 30 bars; entry the next bar's vwap; stop L - 2 ticks. Every trigger is kept (the geometry
  is the control) with the zone's features -> `zones.parquet`.
- `escvd_zones_report.py`, `escvd_zones_checks.py`, `escvd_zones_size.py` (logs/escvd_zones*.log);
  `escvd_zone_charts.py` (data/charts/escvd/*.html).

**Results**
- **The geometry alone is flat in returns and loses with a tight stop.** N 20 K 4 long: f30 +0.34 bps (t +0.9), f60
  +0.36; short -0.25 / -1.03. The 1R bracket with the stop 2 ticks under the zone: -0.07 to -0.11 R, win 0.44-0.47, in
  all 8 cells (t -6 to -14). The same bracket on random entries: about 0 at R 6-12 bps (long +0.01, short -0.03), but
  -0.06 / -0.09 at R 3 bps (a bar touching both counts as the stop), so part of it is the bracket's own bias. What is
  left says a fresh 20-minute low is retested after a 1-2 point bounce more often than the bounce extends.
- **zratio (the share of the zone's volume that hit the bid) sorts the outcome only at K = 4.** Top quintile (>= 0.67):
  f10 +0.76 (t +3.2), f30 +1.12 (t +2.9), f60 +0.88 (t +1.4) at N 20; +0.95 / +1.15 / +0.84 at N 60. Positive in all four
  years (N 20: +1.40, +0.76, +1.08, +1.33). But K = 2 gives f30 +0.13, K = 6 +0.19, K = 8 +0.07, K = 12 -0.15: a peak
  with flat neighbours, and N 20 / N 60 share most of their events, so it is one observation.
- **With a bias it grows but stays thin.** Trade side on the right side of the 3-day vwap and the 5-day trend, zratio top
  quintile: f30 +2.13 (t +2.3, n 2,083) at N 20 K 4, +3.03 (t +2.1, n 1,093) at N 60 K 4; before 11:30 too: +2.55 (t +1.8,
  n 888), +3.92 (t +2.0, n 479). The same cells at K 8: +0.88 (t +1.0), +1.26 (t +0.9).
- **The clearer signal is the bad zone.** Zone NOT one-sided (zratio lowest quintile, about 0.50) at K 8: f30 -1.06
  (t -2.9), f60 -1.61 (t -2.9). By burst size: small lots hitting into the extreme while 20+ lots take the other side
  (lowest quintile of small-minus-big delta): f60 -1.65 (t -2.8) N 20 K 4, -1.95 (t -2.5) N 60 K 4, mostly the short
  side and 09:30-10:30 (-2.4 to -3.6 bps).
- **Burst size has no clean story.** f ~ d0..d3: the 5-19 lot delta toward the trade is + (f60 +0.7 to +1.4 bps/sd, t 2-3),
  20-99 lots - (t -1 to -1.9), 100+ and <5 nothing.
- Costs: one ES tick is 0.5-0.6 bps, so a round trip is about 0.6-0.7 bps. The best cells clear that by 1-3 bps at
  t about 2, out of several dozen cells looked at.

**Read:** the footprint points the way the method claims (a heavy one-sided zone at the low bounces better than a balanced
one), which the bar-level delta never did, but at 1-3 bps over 30 minutes it is not a system. Next (user 2026-11-15): look
at the signals on a chart.

## Footprint charts (reading the tape by eye)

The operator asked for real footprint charts instead of more statistics: `scripts/futures/escvd_footprint.py --date D [--bar 5] [--png]`
writes `data/charts/escvd/footprint_<date>_<bar>m.html` (and a static PNG of 09:30-10:30). One column per bar, one cell per price,
SELL x BUY (hit the bid x lifted the ask), shaded by delta; bold coloured number = diagonal imbalance (>= 3x the opposite side one
tick away, 25+ lots), coloured frame = 3+ stacked imbalances, black frame = bar POC; 24h/72h vwap, prior-day RTH POC and value
area, the day's profile, and per bar volume / delta / delta % / CVD. The zone engine's triggers are in the legend, hidden until
clicked. The tape's volume and delta match the 1s bars exactly on all six days rendered. Holdout dates are refused.

Plotly notes (cost an hour): a second single-row heatmap on the same subplot throws in plotly.js 3.3; a heatmap's auto text size
is computed from the raw string, markup included, so the script sizes the font to the cell in its post-script on every relayout.
A headless Chrome for checking a chart: `~/.local/share/choreographer/deps/chrome-linux64/chrome --headless=new --no-sandbox
--screenshot=out.png --window-size=1900,1200 --virtual-time-budget=20000 file://...` (installed by `plotly_get_chrome`).

## Close-out (2026-11-15)

The futures avenue is closed (user). What stands, all on the fit sample (2020-06-12 .. 2023-12-31), holdout never read:

- **Bar-level delta adds nothing to price on ES at 10-60 minutes.** Three independent looks agree: the CVD-flip event study
  against its price-only twin, the leg engine's six triggers, and the gradient-boosted ceiling test (delta-only features score
  a negative out-of-sample correlation at every horizon).
- **The per-price footprint zone carries 1-3 bps over 30 minutes at best**, in one narrow configuration (4-tick zone, the most
  one-sided fifth, with a multi-day bias), at t about 2 among several dozen cells, against a round trip of 0.6-0.7 bps. A stop
  under the zone loses in every cell, because fresh lows get retested.
- The published track record that motivated the study could not be verified for futures, so the 70% win rate it was meant to
  explain is not evidence of anything.

Reusable: the continuous front-month ES 1m series (`escvd_bars.py`), the per-price footprint and burst-size tables
(`escvd_fp.py`, which reads the tick tape six sessions at a time: a whole contract does not fit in 15 GB), and the footprint
chart (`escvd_footprint.py`).
