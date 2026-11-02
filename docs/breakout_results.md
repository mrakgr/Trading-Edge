# Breakout — consolidation breakouts on futures (research journal)

Engine: `TradingEdge.Breakout` (Engine.fs + Program.fs; PitFader's session, contract and sealed feature blocks).
Check: `scripts/futures/breakout_check.py` (an independent re-implementation, trip for trip). Report:
`scripts/futures/breakout_report.py` → `data/futures/breakout/report.md`.

## BO-1 — the indicator, both triggers, the first look (user 2026-11-02)

**The idea (user):** "an indicator that calculates variable length windows with volume-weighted mean and
volume-weighted standard deviation. The left side of the window is floating, and the right side is anchored to the
current bar. The goal is to find the maximum such window such that the standard deviation is under a certain threshold
... Every time we add an element to the back of it, we can pop off until the standard deviation goes below the
threshold." Tight, high-volume consolidations are "one of the rare times where the market isn't random ... large players
have picked a spot to battle it out, and once one side wins, the market should go in a certain direction." Exit 1 minute
after the entry; also try a stop at the other end of the consolidation. A test of the SMB Capital tape-reading
principles.

**The engine.** Each new 1s bar is pushed on the back, then bars are popped off the front while the volume-weighted σ of
the bar vwaps exceeds the threshold. Running Σv, Σv·x, Σv·x² keep that O(1) per bar, and monotonic deques keep the
window's high and low. ⚠ σ is not monotone in the window's length, so the greedy left edge is not always the longest
qualifying suffix. It is causal and never moves back.

User rulings (2026-11-02):
- σ in **ticks**: a grid of 1, 2, 3, 4, 6, 8.
- **Both triggers**, as separate families:
  - **ARM:** the first bar at which the window holds the volume floor. The side is that bar's vwap vs the window's
    mean.
  - **BRK:** the previous bars' window holds the floor, and this bar's vwap is strictly above its high (long) or below
    its low (short).
- **One trip per consolidation:** no new signal for a family (BRK: for that side) until every bar of the window has
  left the queue.

The volume floor is 2, 5, 10 or 20 minutes of `vref_min`: the root's mean RTH volume per minute over its previous 20
sessions (causal). Fills are at the next bar's vwap. Exits are at the first bar at or after entry + 60, 120, 300 or
600 s. The stop is recorded: the window's low for a long and its high for a short, signalled when a vwap goes strictly
through it, filled at the next bar's vwap. RTH 09:30–16:00 ET, with entries until 15:00.

**Verified.** The independent check (σ from prefix sums, no incremental subtraction) matches the engine on all 14,981
trips of 20 ES sessions: the trip set and every window, exit and stop field. Control: moving BRK's consumed mark by one
bar gives 189 + 140 unmatched trips (FAIL).

**The run.** 34,969,679 trips over 30 roots and 2,995 sessions (2015–2026), 48 families per trigger side. It took
14 min with a 4.0 GB peak. The output is 1.6 GB of trips plus 11 GB of features, one row per signal bar.

### Result: no edge as a family. Breakouts slightly REVERT; the stop changes nothing at 60 s.

Pooled over every root, at the 60 s exit (PF raw, fit 2015–22 → **holdout 2023–26**):

| σ ticks | BRK floor 5 min | BRK floor 20 min | ARM floor 5 min | ARM floor 20 min |
|---|---|---|---|---|
| 1 | 0.99 → **0.96** | 0.97 → **0.89** | 1.00 → **0.99** | 1.00 → **0.98** |
| 2 | 0.98 → **0.98** | 0.98 → **0.93** | 0.99 → **0.99** | 0.99 → **1.00** |
| 3 | 0.97 → **0.97** | 1.01 → **0.95** | 0.98 → **0.96** | 0.98 → **0.97** |
| 4 | 0.98 → **0.97** | 1.01 → **0.96** | 0.97 → **0.97** | 0.99 → **0.97** |
| 6 | 0.98 → **0.97** | 0.99 → **0.97** | 0.98 → **0.97** | 0.97 → **0.98** |
| 8 | 1.00 → **0.98** | 1.01 → **0.94** | 0.98 → **0.98** | 0.97 → **0.99** |

- **All 96 pooled cells** (2 triggers × 6 σ × 4 floors × fit/holdout) fall between 0.89 and 1.02. Net of one tick
  they are 0.10–0.73.
- **BRK is below 1 in nearly every cell**, and below 1 more often the more volume the window held. The first minute
  after a breakout out of a tight high-volume range goes slightly AGAINST it (holdout mean −0.01 to −0.11 ticks). That
  is the PitFader finding seen from the other side: at the 1s scale the futures tape mean-reverts.
- **The longer exits (120–600 s) do not rescue it.** The holdout PF stays at 0.92–0.98 for BRK and 0.95–1.02 for ARM
  (floor 10 min).
- **The stop at the window's other end is inert at 60 s.** Stopped and unstopped PFs are within 0.01 almost
  everywhere. 6–39 % of trips are stopped, and they exit about where the clock would have taken them.
- **By group (floor 10 min):** nothing holds in both periods.
  - METALS' BRK is 0.95–0.98 fit and 1.06–1.37 holdout (2.04 stopped at σ = 1 on 1,332 trips): a holdout-only
    effect.
  - BONDS' BRK is 0.84–0.99 in both periods: the consistent fade.
  - FX is 0.93–0.97 in both.
  - SOFR / VX / M6J are thin and swing either way.

⚠ **The tick grid misses equity.** ES/NQ/MES ticks are 0.25 index points, and a 10-minute-volume window rarely stays
within σ ≤ 2 ticks: equity has 0–141 holdout trips at σ 1–2, and only reaches about 20k at σ 8. Equity needs a wider
grid (12, 16, 24, 32 ticks) before its verdict is in. The same is true, less severely, for energy at σ 1–2.

Next candidates (not chosen):
- the tape-reading conditions at the break: the breakout bar's volume and delta WITH the break, from the features
  table;
- the equity grid;
- conditioning on the window's length and its volume per bar;
- the fade of BRK, which is what the table actually shows (bonds and FX). It is far below cost at 60 s, as PitFader
  found before its depth gates.

## BO-2 — the stop-and-reverse system: flip at the consolidation's other end, then trail the 1m low / high (user 2026-11-02)

**The user's rules:** "a 1m trend following system. When it is in a tight consolidation it should go long above the
consolidation with a stop at the consolidation low. When it is below the consolidation, it should go short below the
consolidation low. Inside the consolidation, it should be flipping between long and short until the release and then it
should trail stop at the 1m low or high." The reason (user): "Maybe holding the trade for just 1m and trading only once
per consolidation is no better than random."

The engine (`TradingEdge.Breakout --window sigma`, Engine.fs header):
- **Start:** BO-1's BRK. The consolidation's high, low and last bar are frozen there.
- **Flip:** before the release, a long reverses to short when a vwap goes strictly under the frozen low; a short
  reverses to long over the frozen high. One fill closes the old leg and opens the new one.
- **Release:** every bar of the consolidation has been popped from the σ window.
- **Trail:** after the release, a long exits flat strictly under the prior 60 s low (the bar itself excluded); a short
  over the prior 60 s high.

Fills are at the next bar's vwap. One row per LEG; the EPISODE (all legs from one start) is the trade. "−1t" = one tick
per leg.

**Verified.** `scripts/futures/breakout_check.py` re-implements the rules independently: σ from prefix sums, the 1m
low/high by brute force. It matches all 6,311 legs on 20 ES sessions. Control: `>` → `>=` in the release test gives
16 + 17 unmatched legs and 621 `released_t` mismatches (FAIL).

**The run.** 14,857,750 legs (`data/futures/breakout/run_bo2`), 9.9 min, 3.2 GB peak. The report is
`scripts/futures/breakout_report_bo2.py` → `data/futures/breakout/report_bo2.md`.

### Result: still no edge. The whipsaw costs what the trend pays.

Pooled episode PF raw (fit → **holdout**), every cell: fit 0.93–1.01, holdout **0.90–0.98**. Net of one tick per leg
it is 0.43–0.81. Holdout mean −0.12 to −0.41 ticks per episode. 1.1–1.6 legs per episode, 78–99 % of episodes reach
the release, median hold 3–39 min.

Where the P&L comes from (floor 10 min, holdout, mean bp per episode):

| σ ticks | flip legs Σ | last leg (trailed / eod) | trailed last leg PF | episode |
|---|---|---|---|---|
| 1 | −1.87 | +1.33 | 1.30 | −0.54 |
| 3 | −3.67 | +3.45 | 1.71 | −0.21 |
| 8 | −8.21 | +7.88 | 2.02 | −0.33 |

- **The trailed leg does make money (PF 1.3–2.0), and the wider the consolidation, the more.** The flip legs give all
  of it back and more: each failed break costs the full width of the range.
- ⚠ **The "0 flips" episodes show PF 1.26–1.77**, but that is OUTCOME SELECTION. Whether an episode flips is only known
  afterwards, and an episode with no flip is one whose first break worked. It is not a gate. What it does show: 79–90 %
  of episodes never flip, and the 10–21 % that do decide the result.
- **By group, floor 10:** nothing holds in both periods. Equity at σ 8 is 0.99 → 1.01 (16,450 holdout episodes, −1t
  0.91), the best of the financials. Metals at σ 1 is 0.99 → 1.73 on 1,075 episodes, holdout only again. FX
  0.89–0.97 and bonds 0.86–1.07 everywhere.
- **By year, σ 3 / σ 8, floor 10:** 0.91–1.03 every year. No regime carries it.

## BO-3 — the FIXED-VOLUME window: σ of the last V contracts, BO-2's stop-and-reverse (user 2026-11-02)

**The user's idea:** "perhaps we did the wrong thing by focusing on the sigma thresholds as our goal. Instead of trying
to find the window at which the sigma is below a certain threshold we should instead be using total volume as the
threshold ... the past 1k-10k contracts ... and then calculate the sigma of that volume. That would be easier to deal
with."

The engine (`--window volume`, VolEngine.fs header):
- **The window** is the most recent bars holding EXACTLY V contracts; the oldest bar is weighted by the remainder.
- **V** = 5, 10, 20 or 40 minutes of the root's average RTH volume (user ruling, instead of a hand-picked count per
  instrument).
- **TIGHTNESS** = σ ÷ the root's reference σ at that V: the mean of its previous 20 sessions' median warm-bar σ
  (causal). The engine's loose pre-filter is ratio ≤ 1, the tighter half (user ruling).
- **Trading:** BO-2's rules. The RELEASE becomes "V more contracts have traded since the consolidation's last bar".

**Verified.** `scripts/futures/breakout_check_vol.py` is independent: the left bar by binary search, the mean / σ /
high / low directly on each slice, the medians and the reference rebuilt. It matches all 1,520 legs on 20 ES sessions
(after 40 warm-up sessions). Control: weighting the oldest bar in full gives 253 + 333 unmatched legs and 1,181 σ
mismatches (FAIL).

**The run.** 4,643,155 legs (`data/futures/breakout/run_bo3`), 9.4 min, 3.8 GB peak. The report is
`scripts/futures/breakout_report_bo3.py` → `data/futures/breakout/report_bo3.md`.

### Result: no edge, and tightness does not order the outcome

- **Pooled episode PF raw** (fit → **holdout**): V 5 0.97 → **0.92** · V 10 0.97 → **0.92** · V 20 0.98 → **0.93** ·
  V 40 0.99 → **0.96**. Net of one tick per leg: 0.51–0.73.
- **The tightness ladder is flat.** Holdout PF by ratio bucket is 0.90–0.99 in every V and every bucket, with no
  ordering. The tightest bucket (ratio < 0.5) is slightly better in the fit (1.01–1.06) and not in the holdout
  (0.92–0.96). A tighter-than-usual window does not break any better.
- **The same anatomy as BO-2.** The flip legs give −2.6 to −6.5 bp per episode, the trailed last leg +2.2 to +6.1 bp,
  and about 78 % of episodes never flip.
- **By group, nothing reaches 1 in the holdout.**
  - Ags at ratio < 0.7 is 1.06 → 1.03 at V 20 and 1.09 → 1.02 at V 40 (9,733 / 4,577 holdout episodes), 0.67–0.73
    after one tick.
  - Bonds (0.77–0.83), SOFR (0.70–0.78) and VX (0.81–0.93) are the most consistently BELOW 1: their breakouts fail
    the most.
- **By year:** 0.91–1.06, with 2023–26 the weakest (0.91–0.99).

### Across BO-1..3

Three formulations — a σ-threshold window, the stop-and-reverse, and a fixed-volume window with a causal tightness
measure — give the same answer on 1s futures bars: a break out of a tight, high-volume consolidation is followed by a
slight REVERSION. The cost is the failed breaks. That is consistent with PitFader, where the 1s futures tape
mean-reverts and the edge sits in the deep, fast moves.

## BO-4 — tape reading at the break: the break bar's volume and the delta with the break (user 2026-11-02)

**The test (user: "Maybe that could be worth it? Let's give it a try"):** does a break on HIGH VOLUME, with the
aggressive flow pushing in the break's direction, follow through? The episodes of BO-3 (every V) and BO-2 (floor 10,
σ 3 and 8) are joined to the feature row of their break bar. It is read inclusive and the entry fills at the next bar,
so it is causal. Quintile edges come from the fit (2015–22), per family.
- **Volume:** the break bar's volume vs the root's average per second, and vs the consolidation's mean per bar; the
  last 5 and 30 bars' volume vs average.
- **Delta:** side × delta_N ÷ vol_N, N = 30, 60, 120 (> 0 = the aggressors push WITH the break).

Script: `scripts/futures/breakout_tape.py` → `data/futures/breakout/tape.md`. No rerun: 3.68M BO-3 and 634k BO-2
episodes, 9 s.

### Result: no tape-reading condition separates the breaks

BO-3 pooled, episode PF raw (fit → **holdout**):

| feature | Q1 (lowest) | Q3 | Q5 (highest) |
|---|---|---|---|
| break-bar volume vs average | 0.95 → **0.91** | 0.99 → **0.93** | 0.97 → **0.94** |
| break-bar volume vs the consolidation's bars | 0.95 → **0.90** | 0.99 → **0.95** | 0.97 → **0.93** |
| last 5 bars' volume | 0.97 → **0.93** | 0.98 → **0.94** | 0.96 → **0.91** |
| delta WITH the break, 30 bars | 0.99 → **0.93** | 0.96 → **0.94** | 0.98 → **0.91** |
| delta WITH the break, 120 bars | 0.98 → **0.92** | 0.97 → **0.95** | 0.97 → **0.91** |

(All episodes: 0.97 → **0.93**.)

- **The volume at the break does nothing.** The quintiles span 0.90–0.95 holdout, with no order. The fit-Q5 break
  bar trades 24× the average second and 4× the consolidation's average bar, and it still follows through no better.
- **The delta does nothing either.** Most breaks already print with the aggressors behind them (78 % have
  dimb_30 > 0). The most one-sided breaks (Q5) are among the WORST (0.89–0.91 holdout).
- **The combination** (high volume × the delta with the break) is 0.87–0.93 at V 10 min. The only cells above 1 in
  both periods are small and scattered: V 40, last-5-bars Q5 × dimb_30 Q5 is 1.04 → 1.04 on 6,207 episodes; BO-2's
  last-5-bars Q5 is 0.99 → 1.04. Out of about 200 cells, that is the noise floor.

### Across BO-1..4

The SMB-style tape-reading rules (a tight high-volume consolidation, the break, volume and aggression at the break, a
stop at the other end, trailing the move) give no follow-through edge on 1s futures, 2015–2026, in any group or year.

## BO-5 — the volume delta DURING the trades (user 2026-11-02: "Was the volume delta in our favor for the duration of the trades or against us?")

⚠ **HINDSIGHT, not a gate:** the delta over a trade is known only once the trade is over.

Every leg of BO-3 at V 10 min (1,315,601 legs, 2015–2026). dimb = side × Σ delta ÷ Σ volume over the bars from entry to
exit (> 0 = the aggressors pushed OUR way: asks lifted while long, bids hit while short), and the same over the first
30 s and 60 s. Script: `scripts/futures/breakout_trade_delta.py` → `data/futures/breakout/trade_delta.md` (83 s).

| legs | n | mean bp | first 30 s | first 60 s | the whole leg |
|---|---|---|---|---|---|
| winners | 566,312 | +10.9 | +0.095 / 60 % | +0.092 / 63 % | +0.081 / **77 %** |
| losers | 673,013 | −9.6 | +0.000 / 49 % | −0.011 / 47 % | −0.063 / **32 %** |
| big winners (top 10 %) | 131,561 | +31.4 | +0.113 / 64 % | +0.111 / 67 % | +0.106 / **85 %** |
| big losers (bottom 10 %) | 131,562 | −28.3 | −0.010 / 47 % | −0.033 / 44 % | −0.102 / **19 %** |
| flip legs (failed breaks), losers | 265,523 | −13.9 | −0.048 / 43 % | −0.067 / 39 % | −0.130 / **17 %** |

(Cells: median dimb / share of legs with dimb > 0. The holdout alone is the same to two decimals.)

- **Over the whole trade the delta is IN OUR FAVOR for the winners and AGAINST US for the losers:** 77 % / 85 % of
  (big) winners vs 32 % / 19 % of (big) losers. By the whole-leg quintile the mean goes from −8.4 bp (win 11 %) to
  +7.4 bp (win 69 %). ⚠ That is largely mechanical: at the 1s scale price moves BECAUSE asks are lifted or bids hit,
  so the delta and the same-time price change go together.
- **The failed breaks are aggression THROUGH the consolidation:** the flip losers have the most one-sided adverse
  delta (17 % positive). The other side does not merely fail to follow; it hits back through the range.
- **The informative part is EARLY:** in the first 30 s the winners already have the flow with them (median +0.095,
  60 % positive), while the losers are balanced (0.000, 49 %). It is still hindsight as a split of whole trades, but
  dimb over the first 30 s IS known at entry + 30 s. That makes it testable as a causal CONFIRMATION EXIT: at
  entry + 30 s, keep the leg only when the flow has been with it. The test must use the return from entry + 30 s
  on, since the flow in those 30 s also moved the price.

## BO-6 — ABSORPTION: strong delta that the price did not follow (user 2026-11-02)

**The idea (user):** "Maybe instead of looking at consolidations, we should be looking for strong delta that isn't
confirmed by price movement?" It is also the user's 2026-04 thesis: one-sided flow while the price holds marks a level
where a passive order absorbs the aggression; "Short below those key levels, long above them." Both families (user
ruling), plus the user's confirmation idea: the delta "as confirmation to enter into trades or to cut them quickly ... we
could even flip the trade".

The engine (`--window absorption`, AbsEngine.fs header):
- **Detection,** per window N = 30, 60, 120, 300 present bars:
  - imb = Σ delta ÷ Σ volume; its sign is the aggressors' side;
  - imb ratio = |imb| ÷ the root's usual |imb| at N (the mean of the previous 20 sessions' medians). The raw scale
    differs by root (ZN's median 30-bar |imb| 0.30, ES's 0.095) and shrinks with N;
  - rv = the window's volume ÷ (N × the root's mean volume per bar);
  - move ratio = the move in the aggressors' direction ÷ the root's usual N-bar |move|.
- **Pre-filter:** imb ratio ≥ 2, rv ≥ 1, move ratio ≤ 0.5. No overlapping windows per N and side.
- **FADE:** against the aggressors at the detection. The stop is beyond the window's extreme on the aggressors' side.
- **LEVEL:** the window's range is armed for 15 min. The first vwap above it goes long, below it goes short; the stop is
  the range's other end.
- **Exits** at 30, 60, 300 and 900 s. Every trip records d30 / d60, the delta over its first 30 / 60 s.

**Verified.** `scripts/futures/breakout_check_abs.py` computes every window directly on its slice and rebuilds the
references. It matches all 910 trips on 20 ES sessions, every column. Control: shortening the no-overlap rule by one
bar gives 14 + 6 unmatched trips (FAIL).

**The run.** 2,146,884 trips (`data/futures/breakout/run_bo6`), 9.8 min, 3.9 GB peak. The report is
`scripts/futures/breakout_report_bo6.py` → `data/futures/breakout/report_bo6.md`.

### Result: absorption is not a signal at these scales, and the early delta does not predict the rest of the trade

- **Both families, every window and horizon, are 0.94–1.06 fit and 0.95–1.04 holdout,** before costs.
  - FADE: the best cell is N 300 at 60 s, 0.98 → 1.04 (25,055 holdout trips).
  - LEVEL: the best is N 300 at 60 s, 1.02 → 1.03. After one tick, everything is 0.62–0.66.
- **Who won the level does not matter.** A break WITH the aggressors (they ate through the absorber) is 0.94–1.01
  holdout; AGAINST them (the absorber won), 0.92–1.11, with no consistent side.
- **The absorption ladders are flat.** By the fit quintiles of the imbalance ratio, the move ratio (low = the most
  absorbed) and the volume, every cell is 0.95–1.01 holdout.
- **⭐ THE CONFIRMATION TEST: the flow in the first 30 s says nothing about the rest.** The decision uses d30; the rest
  of the trade is scored from the 60 s mark, past the decision's bars:

  | d30 | FADE rest to 300 s | FADE rest to 900 s | LEVEL rest to 300 s | LEVEL rest to 900 s |
  |---|---|---|---|---|
  | strongly against (≤ −0.3) | 0.98 → **1.00** | 0.99 → **0.97** | 1.01 → **0.97** | 1.00 → **0.96** |
  | balanced | 1.01 → **0.97** | 1.01 → **0.97** | 0.99 → **0.97** | 0.99 → **1.00** |
  | strongly with us (≥ 0.3) | 0.99 → **0.99** | 0.99 → **0.98** | 0.96 → **0.95** | 0.96 → **0.98** |

  Cutting on adverse flow saves nothing, and flipping earns nothing. The BO-5 split (winners had the flow with them
  from the first 30 s) was the CONCURRENT move: the price moves because of the aggression, but the aggression does not
  carry the price further once it has moved.
- **By group (N 60 + 120, 300 s):** metals FADE 1.02 → 1.11, and LEVEL against the aggressors 1.02 → 1.10 (~9.7k–17.7k
  holdout). Energy LEVEL with the aggressors 1.05 → 1.04. Everything else is under 1.0 in the holdout.
- **By year (N 60):** 0.90–1.07, no regime.

## BO-7 — the 1m breakout: every new 60-bar high / low, the flow before it, and the path of its first minute (user 2026-11-02)

**The plan (user):** "a simple 1m breakout system. 1m channels could determine momentum. Then on top of that we could
have various 1-60s volume and volume delta features. Then after the trade has been entered we could have marks at 1-60s
to see what the price change and the delta was. That would clearly tell us whether entering when the delta is
lopsided, holding the trade when it continues being strong, and exiting when it gets weak makes sense."

User rulings:
- a fixed 60 s exit;
- every root;
- REPEAT ENTRIES: every new-extreme bar is a trip ("It will make analysis easier ... average out the signal in the
  volume delta"), filtered post hoc.

The engine (`--window momentum`, MomEngine.fs header):
- **Entry:** PitFader's channel rule mirrored at 60 bars.
- **Before entry:** volume and dimb of the last 1–60 bars, plus PitFader's 236 sealed features stored as REAL. A long
  break reads PitFader's SHORT leg instance (its EXT = new 20m highs, the move being joined).
- **After entry:** marks at 1, 2, 3, 5, 10, 15, 20, 30, 45 and 60 s, each with the price, the dimb since entry and the
  volume since entry.

**Verified.** `scripts/futures/breakout_check_mom.py` re-derives the channel, the fills, every mark and the pre-entry
windows on slices. It matches all 35,853 trips of 20 ES sessions on 62 columns. Control: starting the marks' window one
bar late fails 35k+ rows.

**The run.** 33,279,556 trips, 17 GB (`data/futures/breakout/run_bo7`), 24 min, 3.0 GB peak. Most of the time is the
appender writing about 300 values per trip. The report is `scripts/futures/breakout_report_bo7.py` →
`data/futures/breakout/report_bo7.md`.

### Result: a 1m breakout REVERTS from its first second, and the flow works the opposite way to the textbook

- **Baseline:** PF raw at 60 s is 0.97 → **0.95** pooled. Net of one tick 0.56. Every group is under 1 except SOFR
  (1.02 → 1.04, 17k holdout trips, 0.40 after one tick).
- **The average path (holdout mean ticks)** falls MONOTONICALLY from the first second: −0.04 (1 s) · −0.11 (5 s) ·
  −0.15 (10 s) · −0.21 (30 s) · −0.23 (60 s). Equity: −0.08 → −0.39. A new 1m high is, on average, the top of the next
  minute.
- **1. Entering with lopsided flow is WORSE, monotonically.**

  | pre-entry dimb_30 (with the break) | ≤ −0.3 | −0.3 … −0.1 | −0.1 … 0.1 | 0.1 … 0.3 | ≥ 0.3 |
  |---|---|---|---|---|---|
  | PF raw 60 s | 1.02 → **1.01** | 1.01 → **0.96** | 0.99 → **0.96** | 0.96 → **0.95** | 0.95 → **0.93** |

  The same holds in every window from 5 to 60 bars. A break made WITHOUT aggressive buyers (or against them) is the
  better one. That is PIT-8's divergence again; the textbook confirmation is the worst bucket.
- **The exception is VERY HEAVY volume.** A 60-bar volume ≥ 4× usual gives 1.04 → **1.05** (101,766 holdout trips,
  +0.46 ticks). ≥ 4× with dimb_30 ≥ 0.3 gives 1.06 → **1.08** (55,231). But by year it swings from 0.79 to 1.35:
  2020 1.35 and 2025 1.22 against 2023 0.79 and 2017–18 0.84–0.86. These are the event days, not a steady effect.
  Net of one tick it is 0.79–0.90. By group, equity's ≥ 8× (2.63 holdout on 879 trips) and metals' ≥ 4× (1.13) are
  concentrated the same way.
- **2. Holding the strong and cutting the weak is BACKWARDS.** At every decision mark (5, 10, 20, 30 s), scored from
  the next mark:
  - strong flow WITH us since entry → the rest is WORSE: 0.94–0.98;
  - flow against us → the rest is 0.98–1.00;
  - the worst cell is "the trade is UP at the mark but the flow has turned against us": 0.89–0.96;
  - "the trade is DOWN and the flow is against us" is 1.00–1.02: the price comes back.

  At this scale whatever the flow just did, the price partly undoes. Strong flow with us is a reason to take the
  profit, not to hold.
- **By year:** 0.93–0.99 every year. With dimb_30 ≥ 0.3 it is 0.91–0.98.

### Across BO-1..7

On 1s futures bars, intraday momentum entries (a break of a consolidation, a 1m channel, a break of an absorption
level) revert, and aggressive flow behind the move makes the reversion WORSE. The only positive pocket is breaks on
extreme volume (≥ 4–8× usual), which lives in the event years (2020, 2025). That is the intraday shadow of the user's
experience: "the only thing that ever worked when trading breakouts were high volume daily breakouts."

## BO-8 — the LONG 1m breakout in context: the z score, the leg counters, a bounce from a deep decline (user 2026-11-02)

**The questions (user):** "For long does the trade having a low z score improve things? What about the leg counters?
Maybe aggressive buying on a bounce from a deep decline could be a workable setup?"

BO-7's 13.68M long trips with the 20m vwap warm (5.50M holdout). A long break carries PitFader's SHORT leg instance, so:
- z_since_opp = the z since the last new 20m LOW;
- k / k_300 / k_60 = the new 20m highs in the bounce;
- breach_opp_1200 = bars since the last new 20m low.

The decline is measured by `ols_r_1200` (the 20m regression's signed r) and d20 = ln(vwap_60 / vwap_1200). Quintile
edges from a 2 % fit sample. Script: `scripts/futures/breakout_bounce.py` → `data/futures/breakout/bounce.md` (15 s).

### Result: no — and aggressive buying on the bounce is the WORST version

(All long breaks: 0.96 → **0.94**. Cells: PF raw at 60 s, fit → **holdout**.)

- **The z score:** a LOW z since the 20m low is no help (Q1 0.97 → **0.94**). A HIGH z is the worst, monotonically at
  the top: Q4 0.93 and Q5 0.92 → **0.91**. The break of a move already stretched from its low reverts the most.
- **The leg counters:** about 60 % of the long breaks are not inside a 20m up-leg (k = −1). Inside one, k / k_300 /
  k_60 are 0.94 at every depth. They do nothing.
- **The decline itself:** the 20m regression r and the distance below the 20m vwap give 0.92–0.95 in every quintile.
- **⭐ Aggressive buying on a bounce from a deep decline (holdout):**

  | decline | dimb_30 ≤ 0 | 0 … 0.3 | ≥ 0.3 (aggressive buying) | ≥ 0.3 and volume ≥ 2× |
  |---|---|---|---|---|
  | 20m r ≤ −0.8, low ≤ 120 bars ago | 1.03 → **0.99** | 1.02 → **0.97** | 1.02 → **0.90** | 1.50 → **0.86** |
  | ≥ 0.3 % under the 20m vwap, low ≤ 120 bars ago | 0.99 → **0.99** | 0.95 → **0.98** | 0.97 → **0.86** | 1.31 → **0.82** |

  The more aggressive and heavier the buying on the bounce, the worse the holdout (0.82–0.90). The FIT liked the
  heavy-volume corner (1.22–1.50 on 3–12k trips), and the holdout broke it. That is the trap this table exists to
  catch. The best cells are the quiet bounces (dimb_30 ≤ 0) at 0.99 — still not above 1.
- **By group:** equity is the clearest — aggressive and heavy gives **0.69**. Metals' heavy corner is 1.38 → 1.11 on
  1,414 trips (thin). Bonds' quiet bounces are 0.86 → 1.28 on 1,569 trips (fit below 1).
- **By year:** the aggressive-buying bounce is under 1 in 10 of 12 years (0.78 in 2026).
