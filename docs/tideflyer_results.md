# TideFlyer — Daily Swing Mean-Reversion Research Log

`TradingEdge.TideFlyer` (branch `tideflyer`). A **daily** swing mean-reversion system. **Thesis
(user):** buying new **7-day LOWS** should beat buying new **7-day highs** — a dip is a better long
than strength, on a multi-day swing horizon. Named for the tide: it goes out (buy the 7d low) and
comes back in (sell the 7d high).

**Lineage:** copied from `TradingEdge.HighFlyerV2` (the daily engine skeleton) — NOT LowFlyer (which is
an *intraday* minute-bar system). HighFlyerV2 is a daily MOMENTUM book (buy a same-day +10–30% move near
the 52w high); TideFlyer INVERTS the entry to a 7-day-low channel. The skeleton (no-lookahead snapshot
convention, >45d gap-sever, streaming `split_adjusted_prices`, config/CLI/CSV) carries over verbatim.

**Scaffold verified (2026-07-06):** the renamed clone is byte-parity with HighFlyerV2 before any logic
change (895 trips / 50.8% win / PF 1.611 on 2024-01-01..2026-05-13, CSVs identical). Commit `42994bb`.

---

## Design (decisions locked with user)

- **Entry = a rolling 7-day CLOSE channel** (reuses the existing `MinMa`/`MaxMa` deque primitives in
  `RollingMa.fs`; HighFlyerV2 already had a 252d channel, we add a short one). Two modes:
  - **Long-MR (default): buy the new 7d LOW** — `close ≤ min(prior 7 closes)`, sell at the 7d high.
  - **Mirror: buy the new 7d HIGH** — `close ≥ max(prior 7 closes)`, sell at the 7d low. The momentum
    control, ≈ HighFlyer's logic. (A `--mirror` flag.)
- **Channel convention = PRIOR-7 (today excluded)** — `close ≤ min of the PRIOR 7 closes`, matching the
  codebase's snapshot-before-push convention (not Donchian-with-today). Window is a knob (`--low-window`,
  default 7).
- **Exit = both paths behind `--target-exit`:**
  - ON: long-MR sells when `close ≥ 7d high` (the round-trip: buy low, ride to high); mirror sells at
    the 7d low. Fills at next open. N-day time-stop is the fallback if the target never hits.
  - OFF: the inherited fixed N-day time-stop only (`--max-hold-bars`).
- **Momentum-specific gates NEUTRALIZED by default** so the raw baseline is PURELY the 7d channel: the
  same-day move-band (`UpThreshold`/`MaxUpThreshold`) and the 52w-high proximity gate are OFF at the
  start. Other gates (rvol, ATR%, tightness, price floor, ADV, intraday-ret) are kept but will be tuned
  post-hoc via slicing, the same way the other systems were built (raw signal first, then layer gates).

## Tuning plan (user)

1. **Two modes head-to-head** (buy-7d-low/sell-high vs buy-7d-high/sell-low) — establish the thesis.
2. Then tune, one at a time: **1d-return threshold**, **volume-fraction thresholds**, and other features.

---

## Runs

## Run 1 — two-mode baseline (2×2: entry direction × exit model). THESIS CONFIRMED.

Raw whole-universe signal (channel ON, EVERY momentum gate neutralized; only price≥$1 + ADV≥$100k
liquidity floors). Full history 2005-01-01 → 2026-05-13. 2×2 = {buy 7d low, buy 7d high} × {5d
time-stop, target-exit}.

| entry | exit | trips | win% | PF |
|---|---|---:|---:|---:|
| **buy 7d LOW** | time-stop | 4,335,517 | 52.2 | **1.149** |
| **buy 7d LOW** | target (sell 7d high) | 4,335,517 | 53.7 | **1.151** |
| buy 7d HIGH | time-stop | 4,662,284 | 49.6 | 1.051 |
| buy 7d HIGH | target (sell 7d low) | 4,662,284 | 48.2 | 1.051 |

**Findings:**
1. **THESIS CONFIRMED — buy-7d-LOW beats buy-7d-HIGH on both exits** (PF 1.15 vs 1.05; win 52–54% vs
   48–50%). A dip is a better long than strength, even raw & unfiltered across the whole universe. The
   mean-reversion direction is the correct one.
2. **Exit model barely matters raw** — for buy-low, target (1.151) ≈ time-stop (1.149); the target lifts
   win rate (53.7 vs 52.2) but not PF. Most raw 7d-low names don't rally to a fresh 7d high within the
   hold, so the target rarely fires and the time-stop dominates. Expect the exit to matter more once
   entries are gated to higher-quality dips.
3. **Both low-PF raw (1.05–1.15)** — expected for a whole-universe signal; the edge is real but thin
   until gated. buy-7d-HIGH at 1.05 is barely above breakeven → **buying strength blind is ~a coin
   flip**, confirming HighFlyer's edge comes from its OTHER gates (+move / 52w / rvol), not new-highs
   per se.

**⚠ Scale problem for tuning:** the raw signal fires on ~4.3M ticker-days (a 1.3 GB CSV per mode) —
"any name closing at a 7d low" is too common to slice efficiently. Like every other system here
(HighFlyer prunes to the +move; LowFlyer to `mr_candidate`), TideFlyer needs a **base prune** before
tuning. Candidate prune (to decide): require the 7d low to also be a real DOWN-move (a negative
1d-return floor — down ≥ X% into the low) + the liquidity floors. Cuts 4.3M → sliceable while keeping
the thesis. **NEXT = pick the base prune, then tune 1d-return / volume-fraction / other features.**

## Run 2 — base prune: 1d ≤ −5% into the low (down-day requirement)

**Base prune locked: require a real DOWN day into the 7d low** — `close/prevClose-1 < −5%`
(`MaxUpThreshold = -0.05`, reusing the move-band ceiling; `--max-up-threshold` tunes it). Plus the
kept liquidity floors (price ≥ $1, ADV ≥ $100k). Long-MR, 5d time-stop.

- **4,335,517 → 642,671 trips** (6.7× cut, 190 MB — sliceable), **PF 1.144 / 50.5% win** vs raw
  1.149 / 52.2%. The prune removes marginal-drift "technically a new low" noise **without killing the
  edge** — the −5% names carry the same signal.
- **Win% dips (52.2 → 50.5) but PF HOLDS (1.144)** — the −5% down-day names win LESS often but bounce
  HARDER (deeper dip → bigger reversion), keeping PF flat. Encouraging for the depth lever (a steeper
  1d/7d down-move should raise PF).

**Volume feature wired:** a 7d rolling volume-MAX (`vol_max_7d_at_entry`) is now recorded on every
trip (like LowFlyer's `vol_vs_high`). So **rvol** (entry_vol / avg) and the **volume-fraction**
(entry_vol / 7d-vol-max) are both post-hoc levers.

**Tuning population = `/tmp/tide_low_5pct.csv`** (642,671 trips, long-MR + 1d≤−5%, 5d time-stop).
Further base prunes available if needed (7d-down floor, both-down). **NEXT = tune, one lever at a
time: 1d-return depth, 7d-return, volume-fraction, rvol.**
