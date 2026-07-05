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

## Run 3 — 1d-return depth: an INVERTED-U (deeper dip better, then a falling knife at −40%)

Broke the tuning population down by `pct_up_at_entry` (the 1d return, all < −5% by prune; median
−7.1%, p10 −13.5%, worst −99.3%). PF from `net_pnl`; avg% from `exit/entry−1`.

| 1d band | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| −5..−8% | 399,074 | 51.1 | 1.135 | +0.53 |
| −8..−12% | 155,110 | 50.2 | 1.142 | +0.72 |
| −12..−18% | 57,263 | 49.0 | 1.162 | +1.02 |
| −18..−25% | 17,904 | 48.3 | 1.219 | +1.59 |
| −25..−40% | 8,894 | 46.3 | **1.241** | +2.05 |
| **<−40%** | 3,159 | 39.8 | **0.976** | −0.29 |

**Same inverted-U as the other systems:** PF climbs monotonically with dip depth (1.135 → 1.241) and
avg% ~quadruples (+0.53 → +2.05) — the harder the 1d fall, the better the bounce — UNTIL `<−40%`,
which is a **FALLING KNIFE** (PF 0.976, a loss; 39.8% win). Past ~−40% a one-day collapse isn't a dip
to fade, it's a genuine breakdown (delisting/fraud/bankruptcy) that keeps falling. Win% falls as PF
rises (51→46%) → deeper dips win LESS often but BIGGER = a **sizing lever, not just a gate.**

**Band beats a one-sided floor** — flooring the knife at −40% lifts every ceiling:

| band | n | PF | avg% |
|---|---:|---:|---:|
| −40% ≤ 1d ≤ −8% | 239,488 | 1.16 | +0.91 |
| −40% ≤ 1d ≤ −12% | 84,251 | 1.186 | +1.25 |
| **−40% ≤ 1d ≤ −18%** | 26,824 | **1.229** | +1.76 |

−40% floor beats −30% at every ceiling (≤−18%: 1.229 vs 1.218) — keeps the strong −25..−40% cell,
cuts only the broken <−40% names. **Working 1d encoding: band `−40% ≤ 1d ≤ −8%`** (PF 1.16, 239k) with
**size-up on depth toward −40%** (avg% ~2× across the band); ≤−18% ceiling is the high-PF/low-capacity
tier (1.229, 27k).

**⚠ Honest caveat:** PFs are still MODEST (1.16–1.23) — TideFlyer is a real but THIN edge here, well
below LowFlyer/HighFlyer. The 1d lever helps but isn't transformative alone; the volume-fraction and
rvol levers (next) must do real work for this to be a keeper. **NEXT = volume-fraction (entry_vol /
vol_max_7d) breakdown.**

**−40% FLOOR LOCKED into the engine** (`UpThreshold = -0.40`, reusing the move-band floor) — the
falling-knife cut is now a default gate, not post-hoc.

## Run 4 — volume-fraction (entry_vol / 7d vol max): an INVERTED-U, the MIRROR of the short

Does a new 7d volume HIGH (vol-fraction > 1.0) improve PF? **Direct answer: NO** — new-vol-high PF
1.139 vs not-new-high 1.15. But the finer breakdown reveals an inverted-U centered near 1.0 (1d≥−40%
book; median volfrac 0.78, p90 2.1, 35% make a new 7d vol high):

| volfrac band | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| 0–0.5 (very quiet) | 170,752 | 48.1 | 1.09 | +0.48 |
| 0.5–0.8 | 158,176 | 51.5 | 1.19 | +0.88 |
| **0.8–1.0** | 83,902 | 52.2 | **1.222** | +0.98 |
| 1.0–1.5 (new high) | 113,283 | 52.5 | 1.211 | +0.88 |
| 1.5–2.5 | 65,347 | 50.7 | 1.139 | +0.54 |
| **≥2.5 (huge spike)** | 47,699 | 47.9 | **0.958** | −0.17 |

**The sweet spot is NORMAL-to-slightly-elevated volume (0.8–1.5), NOT a spike** — a dip on ordinary
volume mean-reverts best. **The extreme spike (≥2.5×) is a FALLING KNIFE (PF 0.958, a loss) — the
MIRROR-OPPOSITE of the short:** there, a volume spike into a new HIGH = exhaustion blow-off (great
short); here, a volume spike into a 7d LOW = panic/forced selling that KEEPS going (a dip-buy trap).
Volume-spike-into-weakness ≠ volume-spike-into-strength. The very-quiet tail (0–0.5×) is also weak
(1.09) — a dip nobody's trading is a slow bleed, not a sharp reversible dislocation.

**Encoding = a BAND `0.5 ≤ volfrac ≤ 1.5`, and it STACKS with the 1d band (complementary levers):**

| filter (on 1d[−40,−8]) | n | PF | avg% |
|---|---:|---:|---:|
| any volfrac | 239,328 | 1.161 | +0.91 |
| **volfrac 0.5–1.5** | 118,775 | **1.248** | +1.40 |
| vf[0.8,1.5] & 1d≤−12% | 21,398 | 1.291 | +2.05 |
| **vf[0.8,1.5] & 1d≤−18%** | 5,665 | **1.474** | +4.09 |

The volume band alone lifts 1.161 → 1.248 (~half the trips); stacking deeper 1d ceilings keeps climbing
to **PF 1.474 (+4.1% avg)** — the two levers REINFORCE, not overlap. PF 1.47 is finally respectable
(still sub-LowFlyer, but the levers ARE doing real work — answers the thin-edge concern). **Working
stack: 1d ∈ [−40,−8]% (size on depth) × volfrac ∈ [0.5,1.5].** NEXT = rvol (entry_vol / avg), then 7d
return, then the exit-model A/B now that entries are gated.

**DEFAULTS LOCKED into the engine:** 1d band `[−40%, −5%]` (`UpThreshold -0.40`/`MaxUpThreshold -0.05`)
+ **volfrac band `[0.5, 1.5]`** (`VolFracMin`/`VolFracMax`, new gate). New base book = **355,436 trips,
PF 1.204, 52% win** (`/tmp/tide_base.csv`). Multi-day returns (3d/7d/15d) joined post-hoc from
`daily_episodes` lagged closes (`tideflyer_multiday.sql`).

## Run 5 — 3d return: a DEEP-WASHOUT amplifier (deeper = better, NO knife), not a pullback filter

3d return = entry_close / close-3-bars-ago − 1 (episode-partitioned LAG, no-lookahead; 100% coverage).
Distribution median −9.7%, p90 −4.1% — **almost every trip is DOWN over 3d** (structural: a new 7d low
+ down ≥5% today ⇒ down over 3d too; the `0..+10%` bucket is ~empty, 522 trips).

| 3d band | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **<−40%** | 2,386 | 53.1 | **1.946** | +10.05 |
| −40..−25 | 13,431 | 54.7 | 1.665 | +4.84 |
| −25..−15 | 61,302 | 53.3 | 1.306 | +1.74 |
| −15..−8 | 147,547 | 52.0 | 1.136 | +0.58 |
| −8..0 | 130,248 | 51.1 | 1.084 | +0.30 |

**3d is a DEEPER-IS-BETTER amplifier — monotone, and NO falling knife at the extreme** (opposite of
1d): PF climbs 1.08 (−8..0) → **1.95 (<−40%, avg +10%)**. A 3-day −40% is a sharp multi-day WASHOUT
that snaps back; a 1-day −40% is a single-bar catastrophe that keeps falling — so the knife-floor is a
1d-only phenomenon. **On the SHORT windows (1d, 3d), depth is the edge.** A 3d floor (≤−15% → PF 1.31,
77k; ≤−25% → 1.68, 16k) is a strong clean lever — really a **sizing lever** (size up as the 3d washout
deepens). **Sets up the 7d/15d test:** 3d says "recent washout = good"; the user's hypothesis is the
LONGER windows should invert — flat-to-UP over 7d/15d (a pullback in an uptrend, not a sustained
multi-week decliner) = the better setup. Testing next.
