# LowFlyer SHORT-BREAKDOWN — Momentum Continuation of the New-Low Flush

`TradingEdge.LowFlyer --short-breakdown` (branch `highflyer_v2`). The **fourth quadrant**:
`Downside=true, Short=true` — the SAME new-session-LOW-on-high-volume breakout the default LONG
system fades, but taken **SHORT** instead. A momentum/continuation bet: the flush KEEPS going.

**Thesis (user):** the usual flush-fade (long the breakdown to a new low, betting on a bounce)
is +EV market-wide (that's the production long). But **when the name is extended enough (high 1d
return), the pattern INVERTS** — a high-volume breakdown to a new session low becomes a
continuation, not a reversal, and shorting it is the positive trade. This is the down-side
analog of the (up-side) new-high-pop short, but a DIFFERENT setup: here we short a name that's
already up a lot on the day and is now breaking to new intraday lows (the parabolic starting to
crack), rather than shorting a fresh pop to new highs.

**Relationship to the other three books:**
- **LONG new-low flush** (default): fade the flush → bounce. Low-float mean-reversion. PF 3.45.
- **SHORT new-high pop** (`--short`): fade the pop → exhaustion. rvol is the master gate. PF ~4+ at high rvol.
- **SHORT new-low breakdown** (`--short-breakdown`, THIS doc): ride the flush DOWN when the name
  is extended. The long-fade INVERTED.
- (LONG new-high pop: untested, the trend-continuation-up quadrant.)

**Engine:** identical breakout signal to the default (new-session-low close on high volume, from
`--short-breakdown`), only the P&L sign flips (`toTrip`, verified on the pop-short). Same recorded
features: `chg_1d/3d/7d/20m`, `bar_rvol_15m`, 1m return (`entry_price/prev_bar_close-1`, NEGATIVE
here — a flush candle), `intraday_atr_pct_at_entry`, `intraday_tightness_at_entry`, forward
returns. **METRIC = raw PF** (shorts are +100%-bounded, no unbounded winner-skew — same rationale
as the pop-short).

**Gates DISABLED to start** (ungated CSV = `--short-breakdown` only): study the full population
first, then dial. Hold to MOC (same-day), as with the other books.

---

## Run 1 — baseline + the 1d inversion (thesis CONFIRMED, but a rare extension-driven tail)

**Whole ungated book: PF 0.788, 43.5% win, −0.35% avg, 110,479 trips** — the inverse of the long
(same trips, flipped sign: long ungated 1.27 → 0.79). As a blanket strategy, shorting the flush
LOSES — the market-wide edge is the FADE. Median 1d = −4% (a new-low breakdown usually means a
red day).

**1d breakdown — the inversion is REAL but only at the extreme:**

| 1d | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| <−10% (down day) | 15,572 | 41.3 | 0.74 | −1.0 |
| −10..0% | 86,903 | 43.7 | 0.81 | −0.2 |
| 0–10% | 7,554 | 45.9 | 0.82 | −0.3 |
| 10–30% | 299 | 44.8 | 0.92 | −0.3 |
| **30–100% (valley)** | 94 | ~45 | **0.41–0.56** | −5.5 |
| 100–150% | 4 | 50.0 | 1.76 | +5.4 |
| **≥150%** | 7 | 71.4 | **9.50** | +11.4 |

Floor sweep: PF stays <1 (a loss) through 1d≥50% (0.83), then **flips: 1d≥75% → PF 4.95** (19
trips, +9.5% avg, 68% win), ≥100% → 3.69 (11), ≥150% → 9.50 (7). **The thesis is correct in
DIRECTION** — an extended name breaking to new intraday lows continues down (the parabolic
cracking), shorting flips from loss to strong gain. **But the flip only kicks in at 1d≥75–100%
and the sample is TINY (~19 trips in 23 years).**

**The 30–100% "valley" is the WORST cell (PF 0.41–0.56, −5% avg)** — a name up MODERATELY that
breaks to new lows is still in an uptrend; the low-break is noise that bounces (you get run over).
The edge is specifically the EXTREME extension where the breakdown signals the parabolic is done.

**rvol is NOT the gate here — the setup is EXTENSION-driven, not volume-driven (mirror-opposite
of the pop-short).** 1d × rvol cross: at high 1d (≥100%) the trips are almost ALL LOW rvol (57 @
rvol<12, ~0 @ rvol≥40). Mechanism: a new-HIGH pop's volume spike coincides with the up-move (high
rvol + high 1d together — pop-short); but a new-LOW breakdown of an EXTENDED name happens AFTER
the run-up — the big volume was earlier, and the breakdown itself is often LOWER volume (buyers
exhausted, price rolling over quietly). So high-1d breakdowns are low-rvol at the breakdown, and
**the extension + making-new-lows IS the signal, no volume spike needed.** Confirms these are
genuinely different setups.

**Status after Run 1: real edge, correct thesis, but seemingly a rare low-capacity TAIL setup
(~19 trips at 1d≥75%).** → but Run 2 reframes this: 1d was partly a PROXY for intraday ATR%.

## Run 2 — intraday ATR% is the REAL gate (not 1d); flips the book +EV with 11× the capacity

Tested intraday ATR% (`intraday_atr_pct_at_entry`, 1m log-ATR) as a lever. It's monotone and far
stronger than 1d:

| i-ATR% ≥ | n | win% | raw PF | avg% |
|---|---:|---:|---:|---:|
| any | 110,479 | 43.5 | 0.79 | −0.4 |
| 0.03 | 1,035 | 51.2 | **1.11** | +1.0 |
| **0.05** | 218 | 58.3 | **1.45** | +6.0 |
| 0.08 | 58 | 62.1 | 1.20 | +4.9 |

**ATR% ≥ 0.05 flips the whole book positive: PF 1.45, 58% win, +6.0% avg, 218 trips** — 11× the
capacity of the 1d≥75% cell (19), and NO extreme-extension requirement.

**ATR% works INDEPENDENTLY of 1d — the 2×2 proves ATR% is the gate, 1d was a proxy:**

| 1d | i-ATR% | n | raw PF | avg% |
|---|---|---:|---:|---:|
| <50% | <0.05 | 110,187 | 0.78 | −0.4 |
| **<50%** | **≥0.05** | 204 | **1.45** | +6.0 |
| ≥50% | <0.05 | 74 | **0.56** | −2.9 |
| ≥50% | ≥0.05 | 14 | 1.58 | +5.7 |

The un-extended `1d<50% × ATR≥0.05` cell is PF 1.45 (204 trips) — as good as the extended cell,
far more capacity. And `1d≥50% × ATR<0.05` is a LOSS (0.56). **So intraday ATR% is the real gate,
not 1d.** Run 1's "extension-driven" read was partly an artifact — the 1d≥75% names happened to
ALSO be high-ATR (chaotic), and ATR% was doing the work.

**Reframed thesis:** the breakdown-short doesn't need the name EXTENDED — it needs the breakdown
VIOLENT/CHAOTIC intraday (high ATR%). High intraday ATR% = the name is in a genuine intraday
freefall, and THAT continues down. The momentum-continuation signal is **intraday chaos, not the
multi-day run-up.** Same ATR% role as the pop-short (both momentum shorts key off intraday
violence) — consistent, and it upgrades this from "rare tail" to a **small-but-real book (218
trips at PF 1.45)**. NEXT: is 1d still additive on TOP of ATR≥0.05? by-year; 1m-flush depth
(the down analog of the pop's 1m); does the down-move need to be a continuation vs first break.
