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

## Run 6 — 7d & 15d returns: the pullback setup EMERGES on the long window (15d is a U-shape)

7d and 15d returns joined the same way. The setup's character SHIFTS with the window length:

**7d — still mostly deeper-is-better (like 3d), faint flat-uptick:** <−40% PF 1.87, then declines to a
trough at −15..−8 (1.09), with a small rebound at −8..0 (1.14). Mostly a washout amplifier; the flat
bucket beating the mild-down bucket is the first hint of the pullback signal.

**15d — U-SHAPE: the user's pullback hypothesis CONFIRMED.** Distribution median −13.6%, p90 +2.7%:

| 15d band | n | PF | avg% |
|---|---:|---:|---:|
| **<−40%** | 21,514 | **1.76** | +5.72 |
| −25..−15 | 84,468 | 1.082 | +0.37 ← trough |
| −8..0 | 66,927 | 1.091 | +0.31 |
| **0..+15** | 41,246 | **1.225** | +0.79 |
| **+15..+40** | 7,310 | **1.287** | +1.26 ★ |
| >+40% | 1,219 | 1.064 | +0.40 |

Two winning cohorts, opposite ends: **(a) deep 15d washout (<−40%, PF 1.76)** and **(b) a HEALTHY
UPTREND PULLBACK (up 0..+40% over 15d, PF 1.23–1.29)** — a name up over 15 days that dipped hard today
into a new 7d low = an uptrend shakeout that reverts. The dead middle is the mild multi-week decliner
(−25..−8%, PF ~1.08). Carve-out confirms: up-15d PF 1.229 (50k) & deep-washout PF 1.403 (79k) both beat
the mushy −25..0 middle (PF 1.089, 227k). 15d floor sweep: ≥0% → 1.229, ≥+5% → 1.259, ≥+15% → 1.244.

**Coherent structure across windows:** SHORT windows (1d, 3d) = depth is the edge (1d has a knife-floor,
3d doesn't); 7d = transitional; 15d = a genuine U (deep washout OR healthy-uptrend pullback, exclude the
mushy middle). The user's instinct was right — the LONG window rewards being flat-to-UP. 15d encoding =
a carve-out (up-pullback and/or deep-washout), NOT yet locked.

## Run 7 — 3d ≤ −15% LOCKED as a default (engine gate via LagMa)

Wired a `LagMa<'T>(3)`-based 3d-return gate (reused LowFlyer's generic LagMa + lagPctChange; added a
Reset for the gap-sever). **Default `Max3dReturn = -0.15`** (require a real 3-day washout). **Verified
byte-parity vs post-hoc SQL: 77,135 / PF 1.415 (engine) vs 77,119 / PF 1.415 (SQL)** — identical PF,
16-trip warmup-edge difference, no lookahead. **The 3d≤−15% default lifts the book PF 1.204 → 1.415**
(355k → 77k trips, 53.6% win). New base book. NEXT = 15d pullback carve-out, rvol, exit A/B.

## Run 8 — 1d loss-ceiling & the volfrac "frozen" mystery (both settled on the base book)

Two checks on `/tmp/tide_base.csv` (the pre-3d-gate 355k book) via `tideflyer_ceiling_volfrac.sql`:

**Q1 — does tightening the 1d down-day ceiling from −5% to −10% help?** In ISOLATION yes (monotone,
same shape as Run 3's depth lever): ≤−5% PF 1.204 → ≤−10% 1.279 → ≤−15% 1.416. **But under the
production `3d≤−15%` gate it goes DEAD FLAT:** ≤−5% 1.415, ≤−8% 1.413, ≤−10% 1.411, ≤−12% 1.409 — only
the tiny ≤−15% tail (14k) lifts to 1.497. **Verdict: keep the −5% ceiling.** The 3d washout gate already
captures the depth the 1d ceiling was reaching for; tightening 1d only halves trip count for nothing.

**Q2 — why did volfrac stop moving PF?** NOT a table error — the base CSV **already has volfrac[0.5,1.5]
baked in** (it was an engine default when generated), so slicing it left nothing for volfrac to move.
Going back to the wider `tide_low_5pct.csv` (no volfrac applied), volfrac[0.5,1.5]'s marginal lift is
**bigger** after the 3d gate, not smaller: +0.058 (no 3d gate: 1.146→1.204) vs **+0.151** (3d gate:
1.264→1.415). The inverted-U fully survives (≥2.5 spike still a knife at PF 0.983; 0–0.5 quiet still weak
1.123; sweet spot 1.40–1.43). **Both 3d and volfrac are genuinely load-bearing — KEPT.**

## Run 9 — `3d < 1d` (already sliding into today) — the direct "prior-2d fall" lever

**User idea: require 3d-return < 1d-return** — i.e. the name was ALREADY down over the prior 2 days
before today's flush (today CONTINUES an existing slide) vs `3d ≥ 1d` where today's 1d drop is the WORST
bar of the window (a bolt-from-the-blue crack). `tideflyer_3d_vs_1d.sql`.

**Within the production 3d≤−15% book the split is ~fully absorbed** (almost everything −15% over 3d is
already sliding): 3d<1d PF 1.426 (75k) vs 3d≥1d 1.056 (1.9k) — a tiny junk tail, only +0.011 to the book.

**But WITHOUT the 3d floor it's a strong, capacity-rich lever on the whole base book:**

| split (base, no 3d floor) | n | PF | avg% |
|---|---:|---:|---:|
| all base | 355k | 1.204 | +0.90 |
| **3d < 1d** (already sliding) | 254k | **1.247** | +1.13 |
| 3d ≥ 1d (cracked today) | 102k | 1.081 | +0.33 |

Keeps 71% of trips (much gentler than 3d≤−15%'s 22%). **Counterintuitive & important: the bounce works
when today's flush is part of an EXISTING slide, and fails when today is an isolated crack.** Not
"buy the isolated dip" — buy the name that was *already* bleeding.

**The real structure = a continuous depth lever on `(3d − 1d)` = the prior-2-day fall** (within 3d≤−15%):

| prior-2d fall (3d−1d) | n | PF | avg% |
|---|---:|---:|---:|
| **< −20%** | 4.9k | **1.745** | +6.31 |
| −20..−10% | 30k | **1.609** | +3.48 |
| −10..−2% | 38k | 1.250 | +1.50 |
| ~flat ±2% | 3.2k | 1.084 | +0.59 |
| +2..+10% | 0.8k | 1.044 | +0.37 |
| **>+10%** (bounced then flushed) | 68 | **0.563** | −4.69 (a bull-trap) |

Monotone: the more the prior 2 days had ALREADY fallen, the better the bounce (PF 1.61–1.75 for
already-down-10–20%+), collapsing to a LOSS (0.56) for names that BOUNCED then flushed. **The clean
story: it's not 3-day depth per se, it's that the name was already falling before today.** The 3d≤−15%
gate was a crude proxy for exactly this. Rather than the boolean `3d < 1d` (which only cuts a junk tail
once 3d≤−15% is on), the lever is the CONTINUOUS `(3d − 1d)` depth. **Default LOCKED: `(3d − 1d) ≤ −10%`**
(`MaxPrior2dReturn = -0.10` — the prior 2 days already fell ≥10%), the −20..−10%/<−20% bands are the PF
1.61–1.75 core. Wired as an engine gate.

**ENGINE default now = base + `3d ≤ −15%` + `(3d − 1d) ≤ −10%` → 35,339 trips / PF 1.635 / 54.8% win /
+3.9% avg / $13.7M** (`/tmp/tide_prior2d.csv`). **Verified byte-parity vs SQL: 35,342 / PF 1.635**
(3-trip warmup edge, no lookahead). The prior-2d gate is the biggest PF jump since the 3d gate:
**1.415 → 1.635** (77k → 35k trips).

## Run 10 — 1d floor/ceiling RE-CONFIRMED under the prior-2d gate (both hold)

Re-swept the 1d band on the new production book (`tide_1d_resweep.sql` / `tide_ceiling_sweep.sql`) to
check the [−40%, −5%) band still holds after the prior-2d gate reshaped the population.

**−40% FLOOR still holds — but it's now a much THINNER, DEEPER knife.** The prior-2d gate pre-empted 96%
of the old `<−40%` tail (3,159 → 120 trips). What remains splits: `<−50%` is still a clear knife (PF
0.613, −9.8% avg, 20% win — a 1-day −50% collapse is genuine death the prior-2d context can't save), but
`−50..−40` actually SURVIVES (PF 1.435) — the gate rescued the shallower part by confirming the name was
already sliding. Removing the floor entirely is ≈neutral (PF 1.635 → 1.630, only 120 trips). **Kept −40%
as-is** (harmless, rounder line; the recovered −50..−40 trips are a rounding error). Structural takeaway:
**the prior-2d gate ABSORBED the falling-knife problem the 1d floor was built for** — the floor is now
redundant insurance, not a live lever.

**−5% CEILING still holds — the [−5%, 0] bucket is positive but a distinct lower tier.** Admitting shallow
down-days (relaxing the ceiling): `[−5, 0)` is PF **1.256** (24k) vs the core's 1.635 — above breakeven but
a sharp step down right at the −5% line (1.635 → 1.268). Flat-to-up (`[0, +2%)`) is nearly empty (599
trips, 1.157) — structural: a name at a *new 7d low* that's *up today* barely exists (only reachable if the
prior closes were even lower). So on the **1d** window there's no positive-day pullback population (unlike
15d). The −5% ceiling adds ~0.37 PF over a shallow today, and the prior-2d "already sliding" gate is NOT
the same signal — today being a real down-day is independent confirmation. **Kept −5%.** `[−5,0]` (24k @
1.26) is a capacity-expansion reserve, not core.

**1d-depth is a SIZING lever, not a tighter gate (re-confirmed, knife now gone).** Cumulative ceiling
sweep — every retained band is a WINNER (no knife anywhere in [−40,−5), even −40..−30 is PF 2.8):

| ceiling (floor −40) | n | PF | avg% | net $M |
|---|---:|---:|---:|---:|
| **≤−5% (default)** | 35.3k | 1.635 | +3.9 | 13.69 |
| ≤−8% | 18.8k | 1.758 | +5.1 | 9.70 |
| ≤−10% | 12.1k | 1.855 | +6.3 | 7.60 |
| ≤−12% | 8.1k | 1.897 | +7.0 | 5.68 |
| ≤−15% | 4.5k | 2.125 | +9.5 | 4.26 |
| ≤−18% | 2.6k | 2.339 | +11.8 | 3.08 |

Tightening −5%→−10% ~doubles avg% (+3.9→+6.3) and lifts PF +0.22 but HALVES net P&L ($13.7M→$7.6M) — a
pure capacity-vs-quality dial, no free lunch. For a daily-swing MR book breadth compounds better than
per-trade PF, so **the default stays −5% (throughput) and depth is captured by SIZING** (avg% is the
size signal: a −18% name returns 3× a −5% name), NOT by a tighter gate. −10% is the natural "concentrated
book" alternative (the elbow — −10→−12 adds only +0.04 PF) if capacity ever ceases to be the constraint.

**⚠ `(3d − 1d)` is a PROXY, not the literal prior-2-day return.** It's the arithmetic difference of two
returns anchored to the SAME endpoint (`chg_3d − d1`), not `close[t−1]/close[t−3] − 1`. They differ by
the compounding cross-term (≈ `d1 × prior2d`): e.g. 100→90→81→76.95 gives (3d−1d) = −18.05% vs the true
prior-2d = −19%. It's a monotone transform in the same direction, so it's a valid gate/lever — and the
−10% default was tuned ON this exact quantity (the engine computes the identical `Chg3d − PctUp`), so
research & engine agree. Kept as-is rather than re-sweeping the true 2-day return.

## Run 11 — multi-day pullback study: the thesis FLIPS → TideFlyer is a WASHOUT book. 60d gate locked.

Studied 7d/15d/30d/60d returns on the NEW production book (prior-2d gate, `tideflyer_pullback.sql`) to
test the user's thesis: is buying INTO a pullback (a name in a longer-term UPTREND, dipping today) better
than the alternatives (mild decliner / deep washout)? **The prior-2d "already sliding" gate RESHAPED the
population and the thesis FLIPS vs Run 6.** Distributions are deeply negative (median 30d −34%, 60d −39%;
even p90 is only −1.5%/+17%) — the gate selects freefall names, so the uptrend-pullback cohort barely
exists. Direct three-way test (30d trend proxy):

| setup | n | PF | avg% |
|---|---:|---:|---:|
| **deep WASHOUT (30d < −25%)** | 23.1k | **1.726** | +4.90 |
| uptrend PULLBACK (30d ≥ 0) | 3.7k | 1.401 | +2.20 |
| mild DECLINER (30d ∈ [−25,0)) | 8.2k | 1.384 | +1.74 |

**Deep washout wins OUTRIGHT — the pullback is the WEAKEST, not the strongest** (opposite of Run 6 on the
old book). Every horizon is monotone deeper-is-better once the prior-2d gate is on: 7d `<−40%` PF 2.02,
15d `<−40%` 2.08, 30d `<−40%` 1.90, 60d `<−40%` 1.96. The pullback signal isn't dead — it's INCOMPATIBLE
with the "already sliding" selection (those names don't survive the gate); it would need its own uptrend-
filtered book, a separate system. **Decision: TideFlyer stays a WASHOUT book.**

**60d `<−40%` LOCKED as the default** (`Max60dReturn = -0.40`, new `LagMa<float>(60)` gate) — chosen for
the best trips-per-PF: 60d has the FATTEST high-PF cell (16.7k trips @ PF ~1.96) and keeps the most net
P&L relative to the ~2.0 PF. Book progression: 1.415 (3d) → 1.635 (prior-2d) → **1.957 (60d washout)**.
NEXT = rvol, exit-model A/B, sizing-on-depth, the separate pullback book as a candidate.

## Run 12 — prior-2d switched to the PRINCIPLED true return `close[t−1]/close[t−3]−1`

Replaced the `(3d − 1d)` diff-of-ratios PROXY with the LITERAL prior-2-day sub-period return
`close[t−1]/close[t−3] − 1` (the user's call — more principled). `tideflyer_prior2d_true.sql`. The proxy
systematically OVERSTATED the fall by the compounding cross-term: median gap +0.89pp (p90 +2.28pp,
always positive), so the proxy's −10% cut was really ≈ a true −11%. True-metric band breakdown (within
3d≤−15%) is cleaner & better-separated than the proxy: monotone deeper=better, the `−5..0` band is dead
breakeven (PF 1.055) and `>+5%` (bounced-then-flushed) is an outright LOSS (0.960) — the bull-trap is
sharper on the true metric. Cumulative ceiling: `true ≤ −10%` is the elbow (≤−5%→≤−10% = +0.13 PF, the
biggest step; −10→−15 adds only +0.14 but halves net P&L).

**Switched the engine to the true metric (`Prior2dReturn` member: `sPrevBar.close / close3d.Lagged − 1`,
read post-push so close3d = {t−3,t−2,t−1,t}) at the SAME −10% ceiling.** The true −10% is LOOSER than the
proxy −10% (it's ≈ proxy −11%), so it recovers ~20% more trips for a ~4% PF give-back — a good trade for a
capacity-hungry swing book (user: "worth increasing the trips by 20% for a 4% reduction in PF"):

| prior-2d metric (+ 3d≤−15% only) | n | PF | net $M |
|---|---:|---:|---:|
| PROXY (3d−1d) ≤ −10% | 35.3k | 1.635 | ~13.7 |
| **TRUE c1/c3−1 ≤ −10%** | 42.7k | 1.597 | 15.5 |

**NEW PRODUCTION DEFAULT (base + 3d≤−15% + TRUE-prior2d≤−10% + 60d≤−40%): 19,587 trips / PF 1.924 /
57.2% win / +6.3% avg / $12.3M.** The true-metric switch gave MORE net ($12.3M vs the proxy book's $10.9M)
AND more trips (19.6k vs 16.7k) at ~equal PF (1.924 vs 1.957) — looser as intended. **Verified byte-parity
vs SQL: 19,603 / PF 1.923** (16-trip warmup edge, no lookahead). Book progression now: 1.415 (3d) → 1.622
(true-prior2d) → **1.924 (60d washout)**. NEXT = rvol, exit-model A/B, sizing-on-depth, the pullback book.

## Run 13 — FLOAT: the lever INVERTS — TideFlyer is a BIG-float book (low float is a trap here)

Canonical float method (`tideflyer_float.sql`, mirroring `float_breakdown.sql`): SEC `dei:EntityPublicFloat`
re-anchored to the entry price split-safe (`float_usd * adj_close[entry]/adj_close[period_end]`),
no-lookahead ASOF on `known_date` (= period_end + 90d). Population = the production book (`/tmp/tide_true.csv`),
RAW PF. Coverage 49% (the no-data half sits at PF 1.939 ≈ book avg — not systematically different).

**The low-float lever RUNS BACKWARDS vs every other system** — cleanly monotone, but BIG float wins:

| float$ at entry | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **<50M** | 3,569 | 47.1 | **1.249** | +2.3 |
| 50–150M | 1,938 | 55.3 | 1.832 | +4.8 |
| 150–300M | 1,100 | 59.2 | 2.284 | +7.6 |
| 300–750M | 1,123 | 65.2 | 2.692 | +9.5 |
| 750M–2B | 946 | 69.7 | 3.540 | +11.8 |
| **>2B** | 941 | 71.0 | **3.708** | +12.0 |

Cumulative: keep **<$300M → PF 1.515** (BELOW the 1.924 book avg — a DRAG); keep **≥$300M → PF 3.228**
(3,010 trips, 68% win); keep **≥$750M → PF 3.622**. The usual winning <$300M cut is a *loser* here.

**Why it inverts (mechanically coherent):** LONG-momentum/squeeze books (HighFlyer/LowFlyer) want TIGHT
float — a small float rips UP on a catalyst (the squeeze accelerant they buy). TideFlyer buys a name in a
deep 60-day washout (−40%+) that's ALREADY been sliding — and a low-float microcap in that state is a
FALLING KNIFE that keeps dying (delisting/dilution/fraud, no institutional support to catch it: 47% win /
PF 1.25 on <$50M). A BIG-float name in a −40% washout is the REAL mean-reversion setup: a large liquid
company oversold on macro/sector fear that SNAPS BACK because there's a deep buyer base (PF 3.71 / 71% on
>$2B). **The low-float lever is NOT universal — it's a function of strategy DIRECTION: squeeze books want
tight floats, deep-washout MR books want BIG floats.** A durable, generalizable insight.

**Kept as a DOCUMENTED signal, NOT wired** (user's call): the SEC float data is spotty (49% coverage) and
the big-float winners are thin (≥$750M ≈ 95 trips/yr). Present-day float is far easier to source than
historical, and this experiment settles WHICH DIRECTION to source for (favor ≥$300M, avoid <$150M) before
going live. Use as a SIZING/selection tilt (size up ≥$300M, down/skip <$150M), no-data names at baseline.

## Run 14 — ATR% & tightness: ATR% INVERTS again (chaos wins) → band [0.08,0.25] + tight<9 LOCKED

The HighFlyer quality gates, recorded but OFF by default here (`tideflyer_atr_tightness.sql`).

**ATR% (log) INVERTS HighFlyer — HIGH volatility is the edge** (inverted-U, median 0.103):

| ATR% band | n | PF | avg% |
|---|---:|---:|---:|
| 0.05–0.08 | 4,143 | 1.354 | +2.1 |
| 0.08–0.10 | 4,711 | 1.631 | +4.0 |
| **0.10–0.15** | 7,117 | **2.225** | +8.0 |
| **0.15–0.25** | 2,923 | **2.391** | +11.9 |
| >0.25 | 433 | 1.519 | +7.6 (44.8% win — a knife) |

HighFlyer's `atr%<0.10` CEILING is a DRAG here (PF 1.501, below book); the FLOOR wins (`≥0.10 → 2.221`).
**Same mechanism as float:** momentum wants a calm coiled spring; a washout-MR book wants a VIOLENT
dislocation (the quiet slow-bleed <0.08 limps at PF ~1.3; the violent oversold name snaps back hardest).
The `>0.25` extreme is a falling-knife (genuine death). **LOCKED: ATR% band [0.08, 0.25]** (`MinAtrPct=0.08`
FLOOR — inverts HighFlyer — + `MaxAtrPct=0.25` ceiling; kept 0.08 not 0.10 for capacity, per user).

**Tightness = a NON-lever** (whole middle 1.83–2.14, no gradient), except a far-out `9–15` knife (PF 0.907,
217 trips: a name TRENDING hard, range≫ATR, isn't an MR setup). **LOCKED: `tight < 9`** — a loose sanity cap.

**NEW PRODUCTION DEFAULT (+ atr[0.08,0.25) + tight<9): 14,645 trips / PF 2.105 / 58.2% win / +6.5% avg /
$11.0M** — kept $11.0M of the $12.3M net at 75% of trips. **Byte-parity vs SQL: 14,645 / PF 2.105** (exact,
the slice runs on the already-warm book). Book progression: 1.924 (60d) → **2.105 (ATR band + tight cap)**.

**⭐ KEY INTERACTION — depth is NOT orthogonal to ATR% (`tideflyer_lowatr_depth.sql`).** Asked whether the
depth levers could rescue the weak [0.08,0.10) bucket (PF 1.646). They CAN'T meaningfully — and the
prior-2d lever runs BACKWARDS there. The same prior-2d deepening splits the two ATR cohorts oppositely:

| prior-2d cut | [0.08,0.10) low-ATR | [0.10,0.25) high-ATR |
|---|---:|---:|
| ≤−10% (all) | 1.646 | 2.296 |
| ≤−15% | **1.506** ↓ | **2.435** ↑ |
| ≤−20% | **1.456** ↓ | **2.461** ↑ |

**Deepening the multi-day fall helps HIGH-ATR names but HURTS low-ATR ones** — because a deep fall on a
CALM name is an orderly slow-bleed that keeps bleeding, while a deep fall on a VIOLENT name is a panic
dislocation that snaps back. Depth only means "reversible dislocation" when volatility is high. (Only 1d
depth weakly helps the low-ATR bucket: −12% → 1.836, −18% → 2.197 but 410 trips; 3d flat, prior-2d
inverted.) So the [0.08,0.10) bucket is weak *because it's low-ATR* — kept at 0.08 for capacity, knowing
depth won't lift it. NEXT = rvol, exit-model A/B, sizing-on-depth, the pullback book.

## Run 15 — 20d rvol: confirms the catalyst thesis (inverted-U) → `rvol < 3` ceiling LOCKED

**User thesis (the WHY behind moderate-volume):** a high-volume breakdown signifies a FUNDAMENTAL CATALYST
(real news that keeps falling), not panic/technical selling that reverts. Tested `rvol_20d` = entry_vol /
20d-avg-vol (a DIFFERENT denominator than volfrac's 7d-rolling-MAX). `tideflyer_rvol.sql`, on the book with
VOLFRAC DISABLED (28,186 / PF 1.711) so rvol is isolated.

**rvol_20d is an INVERTED-U — thesis CONFIRMED** (median 1.25, p99 14.8):

| rvol_20d | n | PF | avg% |
|---|---:|---:|---:|
| <0.5 (dead quiet) | 3,529 | 1.224 | +1.8 |
| 0.5–1 | 6,858 | 1.703 | +5.0 |
| 1–1.5 | 6,680 | 1.997 | +6.4 |
| **1.5–2** | 4,005 | **2.142** | +7.3 |
| 2–3 | 3,545 | 1.770 | +6.0 |
| 3–5 | 2,067 | 1.475 | +4.3 |
| **>10** | 494 | **1.095** | +1.1 (catalyst-trap, 42.5% win) |

Peaks at ~1.5–2× normal volume, collapses at the spike — a >10× volume day into a washout is a fundamental
catalyst that KEEPS falling, exactly the mechanism. The dead-quiet <0.5 tail is also weak (a dip nobody
trades = a slow bleed) — same both-tails-bad shape as volfrac.

**rvol ≈ volfrac (corr 0.805) — the SAME lever, but rvol adds a spike-tail cut volfrac misses.** The
4-quadrant test: when both agree "moderate" (rvol<2 & volfrac[0.5,1.5]) PF 2.289 vs 1.32 when they
disagree — mostly overlap, not additive. rvol[0.5,2] standalone (1.906) is slightly BELOW the volfrac book
(2.105), so volfrac stays the primary single lever (don't replace). BUT rvol bands WITHIN the production
(volfrac-on) book still show the inverted-U — the peak SHARPENS to PF 2.574 at 1.5–2× and the 3–10×
catalyst-spike still drags (1.25–1.45). So a light rvol CEILING stacks on top of volfrac:

| filter | n | PF |
|---|---:|---:|
| volfrac[0.5,1.5] (production) | 14,645 | 2.105 |
| **+ rvol < 3** | **13,122** | **2.237** |
| + rvol < 2 | 10,980 | 2.289 |

**LOCKED `rvol < 3`** (`RvolMax = 3.0`) — the highest-coverage, capacity-cheapest lift (+0.13 PF for −1.5k
trips), directly encoding the catalyst cut volfrac's 7d denominator misses. rvol<2 is the tighter
alternative (2.289, but −3.7k trips). The rvol FLOOR left off (the dead-quiet tail is ~absorbed by volfrac).
**Byte-parity: engine 13,122 / PF 2.237 == SQL slice exactly.** Book: 2.105 (ATR band) → **2.237 (rvol<3)**.

**⏭ TODO (cross-system): wire volfrac into HighFlyer.** volfrac/rvol are a strong lever HERE (washout-MR);
worth testing whether the same moderate-volume band helps the HighFlyer momentum book too, OR inverts like
float/ATR% did (momentum may WANT the high-volume catalyst breakout). A clean A/B on the HighFlyerV2 book.
NEXT = exit-model A/B, sizing-on-depth, the pullback book, + the HighFlyer-volfrac test.

## Run 16 — washout reference: is 60d-point-return the best, or would 90d/120d/an MA beat it? (NEGATIVE)

**User question: raw point returns could be noisy — would a 120d/90d point return, or a 60d/90d/120d MOVING
AVERAGE of the close, be a better washout-depth reference?** Tested on the pre-washout-depth book
(`/tmp/tide_pre60.csv`, all other gates ON, 22,881 / PF 1.890), 6 references (`tideflyer_washout_ref.sql`):
point return `entry/close[t−N]−1` and MA depth `entry/mean(prior-N closes)−1` for N∈{60,90,120}. Compared
at **matched selectivity** (deepest 13,120 by each ref = the production trip count):

| washout reference | PF | win% | avg% |
|---|---:|---:|---:|
| **pt60 (current, 60d point return)** | **2.238** | 59.4 | +8.0 |
| ma60 (60d MA depth) | 2.195 | 59.2 | +8.0 |
| ma90 | 2.180 | 58.7 | +7.8 |
| ma120 | 2.165 | 58.5 | +7.7 |
| pt90 | 2.128 | 58.0 | +7.3 |
| pt120 | 2.102 | 57.6 | +7.2 |

**NEGATIVE result — the current `pt60 ≤ −40%` is (narrowly) the BEST; keep it.** Two patterns: (1) LONGER
windows are WORSE (pt60 2.238 → pt120 2.102; ma60 2.195 → ma120 2.165) — the edge is in the RECENT 60d
washout; 90–120d dilutes by admitting names whose collapse is older/staler. (2) The MA is NOT
smoother-is-better here — ma60 (2.195) slightly UNDERperforms pt60 (2.238). **Why the MA hurts:** a name
falling THROUGH the 60d window has its 60d mean dragged DOWN by the recent decline, so MA depth UNDERSTATES
the peak-to-now drop; the point return `entry/close[t−60]` captures the full "how deep from where it was 3
months ago" signal the gate wants. The 1d/3d/prior-2d gates already handle recent smoothness — the 60d gate's
only job is long-term depth, and the raw endpoint captures that best. **All 6 within ~0.07 PF (roughly
interchangeable) → kept the simplest already-wired one (pt60). No engine change.** NEXT = exit-model A/B,
sizing-on-depth, the pullback book, the HighFlyer-volfrac test.

## Run 17 — EXIT MODEL: target-exit ON (sell at the 7d high) → PF 2.237→2.295. Run 1's prediction held.

Run 1 tested target-exit on the RAW whole-universe book and found it ≈ time-stop (a wash) — but predicted
**"expect the exit to matter more once entries are gated to higher-quality dips."** Now, on the fully-gated
PF 2.237 book, re-ran the A/B (`--target-exit` = sell at the opposite 7d extreme, 5d time-stop fallback):

| exit model | PF | win% | net $M | (same 13,122 entries) |
|---|---:|---:|---:|---|
| 5d time-stop (old default) | 2.237 | 59.4 | 10.49 | |
| **target-exit (7d high, ON)** | **2.295** | 59.5 | **10.96** | +0.058 PF / +$0.46M, ZERO capacity cost |

**Mechanism:** 21.9% of trips (2,874) HIT the target — the name round-tripped washout→recovery to a fresh
7d high within the hold (99.9% win, +42.8% avg on those). The other 77.7% still hit the 5d time-stop
(the fallback catches names that don't recover, PF 0.85 there). The lift = exiting the fast recoverers at
the PEAK of the bounce (the 7d high) instead of an arbitrary day-5 mark.

**Robustness (NOT winner-skew):** drop the top 50 winners and target still leads (2.152 vs time-stop
2.117); MEDIAN return higher (+5.47 vs +5.31); +50%-clipped avg higher (6.57 vs 6.30) — the edge is
distribution-WIDE, not a tail. **Era-robust:** target wins or ties ~15 of 22 years, never blows up, and
helps MOST in the recent weaker years (2024: 1.83 vs 1.52; 2023: 1.14 vs 1.00; 2025: 0.85 vs 0.74).

**LOCKED target-exit ON** (`ExitMode`, default `NextOpenClose 1.0`). Modest but FREE (same entries, no
capacity cost) and confirms Run 1's gated-exit prediction. **FINAL production book: 13,122 / PF 2.295 /
59.5% win / $11.0M.** Full arc: 1.415(3d)→1.635→1.622(true prior2d)→1.924(60d)→2.105(ATR band)
→2.237(rvol<3)→**2.295(target-exit)**. NEXT = exit fill-model A/B (Run 18), sizing-on-depth, pullback book.

## Run 18 — exit FILL-MODEL & halfway-target A/B: the Run-17 default (next-open @ full) wins all 6

Generalized the exit to an `ExitMode` DU {Off | NextOpenClose f | Moc f | Limit f} × a `targetFrac` scaling
the exit level between entry (0.0) and the full 7d high (1.0). **Refactor byte-neutral** (off == 2.237,
next-open@1.0 == 2.295 exactly). Two questions: (a) is the favorable OVERNIGHT GAP real — does filling
intraday-at-the-level (limit) or on that close (moc) beat next-open? (b) does exiting HALFWAY (frac 0.5)
beat the full round-trip? Full matrix:

| fill model | @ frac 1.0 | @ frac 0.5 | win% (1.0/0.5) |
|---|---:|---:|---|
| **next-open (fill next open)** | **2.295** | 2.215 | 59.5 / 62.6 |
| moc (fill that close) | 2.284 | 2.098 | 59.5 / 62.9 |
| limit (fill AT the level) | 2.151 | 1.925 | 60.6 / 65.4 |

**Two clean principles, both confirming the mechanism:**
1. **Don't exit early — the full 7d-high round-trip beats halfway at EVERY fill model** (frac 1.0 > 0.5
   across the board). Halfway raises win rate (62–65% vs 59–60%) but CLIPS the fat right tail that carries
   the PF — the washout-recovery runs are the whole edge.
2. **The favorable overnight gap is REAL and sizable.** next-open > limit by +0.144 at frac 1.0, and the
   gap DOUBLES to +0.290 at frac 0.5 — the earlier you exit (mid-recovery, most momentum left), the more
   upside filling intraday-at-the-level throws away. A name closing at a fresh 7d high tends to GAP UP
   further next morning; the target exit isn't "sell when recovered," it's "sell into a recovery with
   overnight momentum" — consistent with MR snapping back hard.

**MOC @ 1.0 (2.284) ≈ next-open (2.295) — the LIVE-EXECUTION EQUIVALENT.** Only −0.011 PF, and operationally
clean (come in ~10min before the bell, see the close is above the 7d high, submit MOC — no overnight hold on
the exit). **Kept next-open @ 1.0 as the theoretical default; MOC is the tradeable stand-in.** Ranking:
next-open@1.0 (2.295) > moc@1.0 (2.284) > next-open@0.5 (2.215) > limit@1.0 (2.151) > moc@0.5 (2.098) >
limit@0.5 (1.925). NEXT = BREADTH breakdown (expect it to LIFT PF, unlike float — but test direction
empirically), sizing-on-depth, the pullback book, the HighFlyer-volfrac test.

## Run 19 — ⭐ BREADTH: the biggest lever in the build. INVERTS again → capitulation buyer. PF 2.30→5.31

Breadth = `pct_above_20` (fraction of the universe above its 20d MA), LAG-1 (yesterday's — the market
state going INTO the day, no-lookahead). `tideflyer_breadth.sql`. **User hypothesis: strong breadth should
LIFT PF (a dip in a healthy tape reverts). The data INVERTS it — WEAK breadth is the edge, dramatically:**

| breadth (lag-1) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **<0.05** | 2,752 | 73.5 | **5.654** | +17.9 |
| **0.05–0.10** | 2,068 | 76.1 | **4.889** | +16.5 |
| 0.10–0.15 | 620 | 48.7 | 1.088 | +0.8 |
| 0.15–0.20 | 785 | 62.5 | 2.303 | +7.7 |
| 0.5–0.65 | 1,402 | 49.9 | 1.185 | +1.5 |
| ≥0.5 (strong tape, the hypothesis) | 2,554 | 50.0 | **1.302** | +2.3 |

Cumulative: **<0.10 → PF 5.307** (4,820 trips, 74.6% win); <0.20 → 4.005; **≥0.5 → 1.302** (a DRAG). The
strong-tape hypothesis is BACKWARDS. Fine sweep: the elbow is SHARP at **<0.10** (the <0.05/0.05–0.10 zone
is PF 4.9–5.7 @ ~75% win, clearly distinct; two noisy small-bucket notches at 0.10–0.15 & 0.25–0.30). Net
P&L is ~flat $8.3–9.4M across <0.10..<0.30, so staying TIGHT buys ~same dollars at far higher PF.

**Why it inverts (the FOURTH factor to flip vs HighFlyer — completes the picture): TideFlyer is a
CAPITULATION buyer.** A deep 60-day washout WHEN THE BROAD TAPE IS ALSO PUKING (<10% above 20d MA) is a
SYSTEMIC oversold — the name got dragged down with everything else, and rips back when the (oversold)
market bounces. A deep washout in a HEALTHY tape is IDIOSYNCRATIC — everything's fine but THIS name
collapsed = a red flag (fraud/dilution/guidance), which doesn't revert (PF 1.30). Same logic as float
(big/liquid dragged-down names revert; lone-broken microcaps don't) and ATR% (violent broad dislocation
reverts). **TideFlyer wants the baby thrown out with the bathwater — a good name in a bad tape, and the
WORSE the tape the better.** Polar opposite of momentum (which needs a healthy tape to sustain a breakout).

**LOCKED `breadth < 0.10`** (`BreadthMax = 0.10`, wired in-engine as a lag-1 gate — 100% coverage, unlike
float; user: "quality over quantity", picked <0.10 over <0.20 for the PF gap). **Byte-parity: engine 4,820
/ PF 5.307 == SQL slice exactly; --breadth-max 100 reproduces the 13,122/2.295 book.** ⚠ Caveat: at <0.10
trips CLUSTER in deep-puke regimes (2008, Mar-2020, 2022) — high time-concentration, P&L comes in bursts.
**FINAL production book: 4,820 / PF 5.307 / 74.6% win / $8.3M.** Full arc: …2.237(rvol)→2.295(exit)→
**5.307(breadth<0.10)**. NEXT = close-sequence monotonicity, sizing-on-depth, pullback book, HighFlyer-volfrac.
