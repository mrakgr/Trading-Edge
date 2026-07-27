# SurgeRiderV2 — pullback entries (the entry redesign)

Branch `plunge-rider`. Fork of `TradingEdge.SurgeRider` (2026-07-27), motivated by the V1/V2
breakout verdict (surgerider_results.md F31d/F31i): **breakout entries are zero-drift lottery
tickets** — hold-to-20m = +0.03%/trip, the whole edge lives in the exit asymmetry, monthly P&L is
lottery-shaped (median month +2.8% vs mean +8.1%). The user's close of 2026-07-26: *"buying
breakouts doesn't work… we'll try waiting on pullbacks instead of buying into the highs"* — the
buy-weakness DNA that works everywhere else in this program (LowFlyer, DipRider), applied to the
1s momentum substrate.

## P0 — the design (2026-07-27)

**Entry (edge-triggered pullback, user's formulation):**

| component | definition |
|---|---|
| trend test | `bsl > bsh` on the entry channel: bars-since-LOW-breach > bars-since-HIGH-breach — the channel's last event was a breakout, not a breakdown. `bsl = -1` (never broken down) with `bsh >= 0` also qualifies. |
| pullback level | `lo·(1−α) + hi·α` of the **strictly-prior** N-bar vwap channel; α = `--entry-alpha`, default 0.5 (the midpoint). |
| trigger | **edge, not state**: the signal is the bar whose vwap first crosses BELOW the level ("the pullback touches the limit"). Residence in the zone does not re-signal; the state must exit and re-enter. |
| floors | unchanged from V1: dv60 ≥ $100k, tc60 ≥ 60, vol_20m floor 7bp (ceiling OFF for breadth in prototyping), entry window 09:45–13:30. |
| fill | next present bar's vwap, as always. |

**Stop/exit:** unchanged — vwap < prior exit-channel MIN ("the 1m channel lows as usual"), MOC.
Defaults now `-c 60 -x 60` (the 1m channel both sides).

**New recorded features:** `breach_lo_{sess,300,120,60,30}` (the bsl mirror; 1200 =
`bars_since_low_1200`), `chan_hi`/`chan_lo` (the strictly-prior entry-channel extremes at the
signal — `pos_in_range = (signal_vwap − chan_lo)/(chan_hi − chan_lo)` in SQL, so deeper-than-α
pullbacks slice post-hoc; shallower need a rerun).

**Prototyping protocol (user):** eff gate NOT baked in (it doesn't speed the backtest — the cost
is bar-scanning, not trip-writing); prototype on **2026 data only**, ≥$2 universe
(`--min-prev-close 2`), full-history runs only for survivors.

Engine notes: bsh/bsl counters update BEFORE the entry check each bar, so a bar that itself
breaks the 1m low reads bsl=0 → trend test fails → no entry on breakdown bars. A zone entered
before 09:45 (or before the channel is warm) consumes the edge — no mid-zone chase entries.

Also added 2026-07-27: per-tkd progress reporting (`tkd N/M (x%) … ~Nm left`, throttled 1/s;
skipped no-tape days count as processed so the ETA is honest).

---

## P1 — the 1m-channel midpoint pullback: NO EDGE (2026-07-27)

Run: `surgev2_26_pb_a50` — 2026-01-02→07-17, ≥$2, dv_0945≥$10M, vol floor 7bp, ceiling OFF,
eff not gated, α=0.5, channels e60/x60. **3,068,467 trips / 18,712 tkd / 11min. Raw attribution:
PF 1.063, win 34.0%, +0.007%/trip — the same zero as unfiltered breakouts.**

**The folded breakout spec does NOT transfer** (breakouts in this state made PF 2.90 in 2026):

| fold (cumulative) | n | win% | PF | avg %/trip |
|---|---|---|---|---|
| all (pullback α=0.5) | 3,068,467 | 34.0 | 1.063 | +0.007 |
| + vol_20m ≥ 40bp | 198,324 | 32.4 | 0.987 | −0.004 |
| + quiet minute (tcr<0.5) | 28,185 | 30.4 | 0.917 | −0.030 |
| + size (vr 0.5-1) | 4,389 | 30.0 | 0.961 | −0.014 |
| + \|eff20m\| ≥ 0.4 | 198 | 34.3 | 1.555 | +0.191 |
| + coil < 0.5 | 189 | 33.9 | 1.492 | +0.176 |
| + chg1d wings | 108 | 25.0 | 1.070 | +0.033 |

**Forward drift is ZERO everywhere** (the F31d disease, unchanged) and every feature ladder is
flat-to-mush with negative tails:

| vol band | n | fwd 20m % | PF |
|---|---|---|---|
| [7,20)bp | 2,204,604 | +0.005 | 1.064 |
| [20,40)bp | 665,539 | +0.030 | **1.114** |
| [40,80)bp | 164,670 | −0.056 | 1.033 |
| ≥80bp | 33,654 | −0.424 | 0.882 |

| feature | best bucket | PF | worst bucket | PF |
|---|---|---|---|---|
| eff (×sign) | FLAT — eff does nothing here | 1.04-1.12 | eff-up ≥0.4 slightly worst | 1.038 |
| bsh (1m trend freshness) | [0,15) | 1.092 | ≥120 | 0.880 |
| 1m channel width | [50,100)bp | 1.096 | ≥200bp | 0.903 |
| dist sess VWAP | [0,2)% | 1.078 | ≥5% | 0.876 |
| pct chg open | [0,5)% | 1.081 | ≥15% | 0.881 |
| rvol_0945 | <2 | 1.081 | 10-20 | **0.792 — INVERTS again (F30)** |
| hour | 13-13:30 | 1.181 | 12-13 | 0.995 |

**Verdict: the 1m-channel midpoint is ~15-25bp deep — microstructure noise, not a pullback.
eff — THE load-bearing breakout feature — is completely dead on pullback bars. Next: the same
entry on the 5m and 20m channels (a structural retreat), stop still at the 1m low.**

---

## P2 — larger channels (5m/20m) + the HIGH-VOLUME pullback question + the STOP diagnosis (2026-07-27)

Runs: `surgev2_26_pb_a50_c300` (5m channel, 1m stop: 1.32M trips, PF 1.056) and
`surgev2_26_pb_a50_c1200` (20m channel, 1m stop: 549k trips, PF 1.046). Both mush at the
headline.

**High-volume pullbacks (user question): NO.** The 1m/20m activity ratios on the HIGH side:

| substrate | feature | best high bucket | PF | worst high bucket | PF |
|---|---|---|---|---|---|
| 1m chan | vol 1m/20m | ≥8 (n=329 only) | 1.458 | [2,4) | 1.031 |
| 1m chan | tc 1m/20m | [1,2) | 1.084 | [2,4) | 0.990 |
| 20m chan | vol 1m/20m | [0.5,1) | 1.058 | **≥8 = 0.608, [4,8) = 0.761** |  |
| 20m chan | tc 1m/20m | [1,2) | 1.075 | **≥8 = 0.386, [4,8) = 0.781** |  |

vol-band × vr grid on the 1m chan: flat, max 1.13 anywhere; win% rises with vr (33.9→37.3)
but PF doesn't follow. On the 20m channel loud dips are DISTRIBUTION, not absorption.
(One positive note on 20m: the [40,80)bp vol band = 1.122 with +0.065 drift — the loud-band
sign flips vs the 1m substrate, worth rechecking under the proper stop.)

**⭐ THE STOP DIAGNOSIS (user, mid-session): a 1m-low stop under a 5m/20m pullback entry is
self-refuting — the retreat that creates the entry keeps printing fresh 1m lows.** Measured:
c1200 run = 100.0% channel exits, median hold 32 BARS (~half a minute); c300 = median 30. Every
larger-structure trade died to the retreat's own noise before the structure could play out. All
three first-round runs are therefore INVALID as tests of the structural-pullback idea.

Reruns launched with `-x 120` (2m stop): `surgev2_26_pb_a50_c300_x120`,
`surgev2_26_pb_a50_c1200_x120`.

---

## P3 — the 2m stop, the 1m drift term structure, and oas=0: THE TOUCH IS EARLY (2026-07-27)

**2m stop (user's fix): mechanics barely change.** c1200_x120 = PF 1.054 (was 1.046), c300_x120 =
1.045 (was 1.056); still **100% channel exits**, median hold only doubles (32→59 bars). The
retreat that triggers the entry runs over the 2m low as well.

**The drift term structure (user asked for the 1m horizon; forward marks are exit-independent so
round-1 runs are valid for this):**

| substrate | bucket | fwd 1m % | fwd 5m % | fwd 20m % |
|---|---|---|---|---|
| 1m chan | ALL | −0.004 | −0.004 | +0.003 |
| 1m chan | [20,40)bp | −0.002 | +0.002 | +0.030 |
| 1m chan | [40,80)bp | −0.022 | −0.047 | −0.056 |
| 1m chan | ≥80bp | −0.089 | −0.209 | −0.424 |
| 20m chan | [20,40)bp | −0.004 | +0.002 | +0.043 |
| 20m chan | [40,80)bp | −0.010 | +0.037 | +0.066 |

**1m drift is NEGATIVE everywhere — the knife is still falling for 1-5m after the touch.** Where
recovery exists (mid-vol bands) it accrues AFTER the entry: the payoff belongs to whoever buys
the TURN, not the touch.

**oas=0 (user's dilution hypothesis): FLAT.** All three substrates: oas 0 ≈ 1 ≈ 2-4 ≈ 5-9
(1.04-1.08); only oas≥10 mildly worse (0.99, negative drift). Unlike breakouts (F31i: oas 0 =
best bucket 2.78), the first pullback touch carries NO premium over re-touches. The best cross:
c1200_x120 [40,80)bp × oas=0 = **PF 1.136, fwd_20m +0.147% (n=5,872)** — the largest positive
drift on any pullback cell so far, and still nowhere near a system.

| c1200_x120 | oas=0 PF / fwd20m | oas≥1 PF / fwd20m |
|---|---|---|
| [7,20)bp | 1.039 / −0.006 | 1.034 / −0.007 |
| [20,40)bp | 1.098 / +0.065 | 1.093 / +0.035 |
| [40,80)bp | **1.136 / +0.147** | 1.110 / +0.036 |
| ≥80bp | 0.880 / +0.032 | 0.933 / −0.567 |

**P1-P3 VERDICT: buying the falling touch has no edge at any channel scale (1m/5m/20m), any stop
(1m/2m), any volume/eff/oas conditioning — the retreat keeps going past the entry. The one
consistent structure: mid-loud vol bands on larger channels show real but small POST-1-5m
recovery. The natural next design: PULLBACK + CONFIRMATION — after the touch arms the state, enter
on the vwap reclaiming a short (30s/1m) high — buy the turn, not the knife.**

---

## P4 — ⭐ PULLBACK + CONFIRMATION: THE FIRST POSITIVE-DRIFT ENTRY OF V2 (2026-07-27)

Engine variant (commit d4caa34): ARM on the touch (trend-up + cross below the α-level), ENTER on
the vwap exceeding the strictly-prior `--confirm-bars` (30) max — the micro-breakout out of the
dip. A low breach while armed does NOT disarm; `arm_lo_breaches` records it (0 = the strict
higher-low rule, ≥1 = V-washout). New features: `arm_sec/arm_vwap/arm_min_vwap/arm_bars/
arm_lo_breaches`. Runs: `surgev2_26_pbc_c1200_x120_cf30` (209k trips), `_c300_` (501k).

**Drift turns POSITIVE at every horizon — the confirmation fixes the falling-knife problem:**

| substrate | entry | n | PF | fwd 1m % | fwd 5m % | fwd 20m % |
|---|---|---|---|---|---|---|
| 20m chan | touch (P2/P3) | 549k | 1.054 | −0.002 | −0.002 | +0.003 |
| 20m chan | **confirm** | 209k | **1.115** | **+0.007** | **+0.009** | **+0.017** |
| 5m chan | touch | 1,316k | 1.045 | — | — | — |
| 5m chan | **confirm** | 501k | **1.111** | **+0.009** | | +0.012 |

**The structure (c1200_x120_cf30), all replicated directionally on c300:**

| cut | bucket | n | PF | fwd 20m % |
|---|---|---|---|---|
| washout | strict (=0) | 200,422 | **1.119** | +0.018 |
| washout | washout (≥1) | 8,653 | 1.007 | −0.009 |
| vol band | [7,20)bp | 156,751 | 1.087 | −0.001 |
| vol band | [20,40)bp | 41,660 | 1.151 | +0.079 |
| vol band | [40,80)bp | 8,729 | **1.248** | **+0.141** |
| vol band | ≥80bp | 1,935 | 0.938 | −0.401 |
| turn latency | <30s | 130,598 | 1.119 | +0.015 |
| turn latency | ≥5m | 82 | 0.719 | −0.160 |
| dip depth | [100,200)bp | 3,813 | 1.162 | +0.146 |

The user's strict disarm instinct adjudicated: **higher-low turns carry the drift; washout turns
are dead** — but as a recorded split, both populations came from one run.

**The composed cell — strict × vol[20,80)bp on the 20m channel: PF 1.187 / +0.05%/trip /
n=48,450 / EVERY month of 2026 positive** (01-06: 1.081/1.075/1.049/1.080/1.499/1.118).
⚠ May = 68% of the cell's net — month-concentration flag. eff is IRRELEVANT here (95% of the
population sits <0.2 — mechanically: a pullback inside the 20m window kills 20m net drift; the
eff gate must NOT be applied to this system).

**Honest scale check: best cells are PF 1.19-1.25 at +0.05%/trip vs the breakout folded spec's
2.13 at +0.62%/trip — an order of magnitude less per trip, BUT with only 2 conditions applied and
α/confirm-bars/channel-scale entirely unswept. This is the first entry in the whole V2 campaign
where the tape drifts up AFTER the fill. ⏭ sweep α (deeper arms), confirm bars, dip-depth ×
latency composition; year audit needs the 2023-2025 run.**

---

## P5 — THE FILTERS INVERT ON THE TURN: moderate participation confirms (2026-07-27)

User: "what if we added the eff and the tc and the vol filters?" Ladders on the strict confirm
population (c1200_x120_cf30):

| feature | bucket | n | PF | fwd 20m % |
|---|---|---|---|---|
| eff | <0.1 | 126,362 | 1.109 | +0.018 |
| eff | [0.1,0.2) | 63,939 | 1.134 | +0.018 |
| eff | [0.2,0.3) | 8,988 | 1.155 | +0.028 |
| eff | [0.3,0.4) | 349 | **0.806** | **−0.311** |
| tcr (1m/20m) | **<0.5 (the breakout spec's quiet gate)** | 7,611 | **1.072** | −0.000 |
| tcr | [1,2) | 58,967 | **1.165** | +0.027 |
| tcr | [2,4) | 2,574 | 1.122 | +0.064 |
| tcr | ≥4 | 122 | 0.564 | −0.100 |
| vr (1m/20m) | [1,2) | 50,150 | 1.167 | +0.030 |
| vr | [2,4) | 5,761 | **1.180** | +0.029 |
| vr | ≥4 | 428 | 0.974 | +0.010 |

**Every breakout gate INVERTS at the turn: quiet-minute is the worst normal bucket, eff≥0.3 is
poison, and MODERATE loudness (1-4× the 20m rate) is the confirmation.** At the high the crowd is
the top; at the higher-low turn the crowd is the proof. (The user's "high-volume pullback" idea
was right — at the TURN, not the touch.)

**THE STACK (all on c1200_x120_cf30):**

| fold | n | win% | PF | avg %/trip | fwd 20m % |
|---|---|---|---|---|---|
| strict | 200,422 | 33.9 | 1.119 | +0.019 | +0.018 |
| + vol[20,80)bp | 48,450 | 34.8 | 1.187 | +0.050 | +0.092 |
| + tcr [1,4) | 13,497 | 36.2 | 1.274 | +0.075 | +0.112 |
| + vr [1,4) | 9,351 | 36.6 | **1.305** | **+0.084** | +0.116 |

**Audits: concentration is THE BEST OF THE PROGRAM — top-3 tkd = 5.2% of gross (3,789 tkd, 980
syms; breakout cells ran 17-47%). NO lottery profile. BUT months split: 01-03 = 0.98/1.03/0.98
(flat), 04-06 = 1.34/1.81/1.38 — the 2026 edge is Apr-Jun only.** Regime or noise — 6.5 months
cannot say. Full-history run launched: `surgev2_23_pbc_c1200_x120_cf30` (2023→2026, the year
audit arbiter).

---

## P6 — THE YEAR AUDIT VERDICT: real, stable, and TOO SMALL (2026-07-27)

Full history `surgev2_23_pbc_c1200_x120_cf30`: 809k trips / 97,642 tkd / 24min.

**The stacked cell was ¾ regime.** By year: **1.014 / 1.041 / 1.051 / 1.305** — 2023-2025 flat,
all the magnitude was 2026 (and Apr-Jun 2026 at that). The fwd-20m drift itself: +0.006 / +0.010 /
**−0.033** / +0.116 — the positive drift that motivated P4 was a 2026-regime phenomenon. Every
magnitude amplifier fails the year audit:

| amplifier | by-year PF | verdict |
|---|---|---|
| vol [20,40)bp | 1.04 / 1.10 / 1.02 / 1.16 | 2026-tilted |
| vol [40,80)bp | 0.97 / 1.09 / 0.99 / 1.26 | 2026-only |
| dip [100,200)bp | 0.93 / 1.06 / 0.98 / 1.22 | 2026-only |
| dip ≥200bp | 0.79 / 0.89 / 0.91 / 1.12 | negative ex-2026 |

**What IS year-stable — the quiet shallow core:**

| cut | 2023 | 2024 | 2025 | 2026 | avg %/trip |
|---|---|---|---|---|---|
| vol [7,20)bp | 1.098 | 1.089 | 1.078 | 1.090 | +0.010 |
| [7,20)bp × tcr+vr[1,4) | 1.100 | 1.112 | 1.099 | 1.119 | +0.013 |
| dip <20bp | 1.052 | 1.084 | 1.048 | 1.125 | +0.013 |

**VERDICT: the pullback+confirmation entry is the first V2 entry with a REAL, year-stable,
breadth-carried positive expectancy (PF ~1.10 every year, no lottery, no concentration) — and it
is ~1.3bp/trip, several times below any cost floor. The magnitude levers (loud bands, deep dips,
participation) are all 2026-regime. Where the magnitude is, the years aren't; where the years
are, the magnitude isn't. The 1s-momentum drift disease on ≥$2 stocks applies to pullbacks as it
did to breakouts.**

---

## P7 — CAMPAIGN CLOSE: the median-trade postscript (2026-07-27)

User: "This system is useless. Let's wrap up the momentum experiments here." Final requested
numbers — the median trade of the confirmed cells at mc=0.

**Sub-$1 V1 map (in-play banded universe, 2023→2026):**

| cell | n | win% | PF | mean %/trip | median %/trip | p25/p75 | median $ @10k |
|---|---|---|---|---|---|---|---|
| A — sess-high ×30s × ignition | 1,660 | 58.1 | 3.064 | +0.463 | **+0.139** | −0.267/+0.992 | +$13.88 |
| B — off-high ×1m × ignition × eff | 3,748 | 52.0 | 2.660 | +0.371 | **+0.033** | −0.322/+0.726 | +$3.26 |

**⭐ The ≥$2 folded spec (user's requested subset — surge23_v2c, 2023→2026):**

| metric | value |
|---|---|
| n / win% / PF | 1,554 / 44.1% / 2.128 |
| mean %/trip | +0.617 |
| **median %/trip** | **−0.177 (−$17.69 @10k)** |
| p25 / p75 / p90 | −0.852 / +1.019 / +3.231 |
| median stock price | $5.78 |

**The ≥$2 flagship's MEDIAN trade LOSES 18bp before any costs — the +0.62% mean lives entirely
above p75. The sub-$1 cells' medians (+3 to +14bp) sit under their own 39-57bp cost floor. Every
momentum edge this program found was tail-carried: the typical trade loses, the book is a
tail-harvester. The 1s momentum program closes: V1 = priced out + sub-cost median; V2 breakouts =
negative-median lottery; V2 pullbacks = stable but 1bp. THE MOMENTUM EXPERIMENTS END HERE — the
mean-reversion core (LowFlyer/DipRiderV6/MaxRiderV1) remains the program's foundation.**
