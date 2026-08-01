# FlushFader — 1s LONG mean reversion (production tier)

**Started 2026-07-28.** The first system of the **productionization arc**: porting the four
1m mean-reversion systems to 1-second bars, one by one, into real-money-tier systems.
FlushFader is the 1s successor to **DipRiderV6** (`docs/diprider_v6_results.md`), built on
the SurgeRider engine chassis (`TradingEdge.SurgeRiderV2` fork, heavily debloated).

**The Fader family** (naming, user 2026-07-28): the MR production systems leave the
Rider/Flyer lineage behind — **FlushFader** (long flush-buyer), **SpikeFader** (short,
future), **MinFader / MaxFader** (hold-to-close, future).

## The system

| piece | rule |
|---|---|
| universe | `diprider_v6_candidate`, `dv_0945 >= $3M` (V6 F14's MANDATORY floor — below it the PF rise is a penny-stock artifact) |
| entry | vwap prints **strictly under the prior 1200-bar (~20m) MIN** of vwaps (present-bar window, strictly-prior snapshot) |
| entry gates | 60-bar dollar-volume ≥ $100k AND 60-bar trade count ≥ 60; window 10:00-13:30 ET (V6's research window; hard floor 09:45 = knowability) |
| fill | NEXT present bar's vwap (both sides) |
| exit | vwap **strictly over the prior 300-bar (~5m) MAX** (V6 F16's direction) — else MOC. **NO STOP** (V6 F6: a stop is destructive here, PF 1.429→1.164) |
| leg machine | `NewLowCounters` ported verbatim from V6: armed by the leg's first new low; `bars_since_first_low` / `lows_since_first_low`; **reset on a new 1200-bar HIGH** (welded to the ENTRY channel — V6 reset on its exit window, which at 20m/20m coincided; with a 5m exit that would end legs mid-flush) |
| K-gate | `--min-lows-into-leg K` = V6 F3's "wait for the Kth low", one trip per leg via the `legConsumed` latch (pair with `--max-concurrent 1`) |
| mode | mc=0 sampler default (averages down; PF = attribution) |

**House rule (2026-07-28): STRICT inequalities on every trigger** — entry `<`, exit `>`,
breach counters, leg events. V6's F21 found `<=` re-firing on round-number pinning ties;
the exit deliberately deviates from V6's inclusive `>=` for the same reason.

## The feature vector (93 parquet columns)

Replacements for V6's regime levers, all **recorded, not gated**:

- **`volat_20m` / `volat_10m`** — slot-EmaHl volatility (30-present-bar slot vwaps →
  |ln return| → EmaHl hl=40/20 slots). Replaces `log_atr_20`. ⚠ **NAMING: `volat_*` =
  volatility, `vol_*` = volume** (user 2026-07-28 — the old engines called both "vol").
- **`eff_20m` / `eff_10m`** — signed drift t-stat ∈ [−1,1]. Replaces `adx_14` (V6 F19:
  ADX monotone UP for MR — |eff| gets the 1s version of that test). Expect eff<0 at flushes.
- **Depth family** (the `bar_pct` analogs): `chan_lo`/`chan_hi` = prior entry-channel
  extremes at signal (depth-below-break, depth-into-leg); **`exit_chan_hi`** = the target
  level at signal (distance-to-target = how much reversion is being asked — new, V6 never
  recorded it); `vwap_60`/`vwap_60_prev` = rolling 1m vwap now / one minute ago
  (**flush speed** = `signal_vwap/vwap_60_prev − 1`; replaces the noisy two-point
  `vwap_60_ago`); `chg_1d` (`prev_adj_close`), `chg_3d` (`close_3d`, new in Candidate).
- **Both-side breach counters** for 30/60/120/300/600/1200/session (`breach_lo_sess = 0`
  ≡ V6's `is_new_sess_low`). The 10m (600) window was completed for this fork: `min600`,
  `breach_600`/`breach_lo_600`, `rng_600`, `vol_600`/`tc_600`, `fwd_vwap_600`.
- **Forward marks** `fwd_vwap_60/300/600/1200` (drift term structure) and **aux-HIGH
  marks** `aux_hi_{120,300,600,1200}` — retargeted as the free post-hoc **exit-window
  sweep** ("what if the target were the N-bar high"): `aux_hi_300` ≈ the real exit at
  defaults (verified: 293,710 of 293,711 target exits within ±2s).
- Activity: `vol_/tc_{15,30,60,600,1200}`, `vol_60_prev`/`tc_60_prev`, `dollar_vol_60`,
  `cum_vol`/`cum_tc`, `bar_vol`/`bar_tc`, gaps, `sess_vwap`/`dist_sess_vwap`/`pct_chg_open`,
  `vwap_1200`, rng family, `slot_count` (warmth — `eff` needs 40 slot returns ≈ 20m).
- Leg context: `bars_since_first_low`, `lows_since_first_low`, `trade_idx` (index of the
  SIGNAL within the leg — diverges from lows wherever a low fired no trade), `open_at_signal`.

**Debloated away** (vs SurgeRiderV2): the whole arm/confirm pullback state machine, all 12
z-scores + z-exits, OLS slopes, the exhaust exit, the channel-stop, windows 20/25/45,
vol/tc 5/10s sums. 946 → 668 lines of Intraday.fs.

## D1 (2026-07-28) — engine verified: build, invariants, hand audit

Smoke run June 2026 (3,568 tkd, 89s, 293,755 trips):

| check | result |
|---|---|
| 12 DuckDB invariants (signal<chan_lo, breach_lo_1200=0, entry>signal, exit reasons, counter ordering, floors, window, …) | **0 violations each** |
| hand recompute (AAOI 2026-06-16 37996): prior 1200-min, prior 300-max at signal AND at exit signal, both fills, no earlier missed target | **exact match to the last decimal** |
| target-vs-aux300 near-equality | 293,710 / 293,711 |
| K-gate smoke (K=5, mc=1) | all trips lows≥5, no leg fires twice; 6,310 trips, PF 1.041 |

First-glance exit mix (June 2026, mc=0, ⚠ attribution): `target` n=293,711 avg +0.024% /
median **+0.118%**; `moc` n=44 avg **−15.3%**. The V6 shape reproduces at 1s: nearly
everything reverts to the 5m high for a small win; the rare never-reverts carry
catastrophic tails. The whole game will be (a) which flushes to buy (depth/regime cells)
and (b) the tail — same as V6, now with 1s-native levers.

## D2 (2026-07-28) — the full 2020-2026 baseline: every V6 lever reproduces on 1s

Full run `data/equity/flushfader/base20_e1200_x300/` (defaults: e=1200, x=300,
dv_0945≥$3M, 10:00-13:30, mc=0): **328,258 tkd → 16,353,468 trips, 80 min, 3.5GB.
Baseline PF 1.106 / win 62.5% / +$61.7M @10k** (⚠ ATTRIBUTION, costs unmodeled).
Superb breadth: top-3 tkd = **0.3%** of gross.

**Year audit (the broad sampler is regime-y — gates must fix this):**

| yr | n | win | pf | avg% | med% |
|---|---|---|---|---|---|
| 2020 | 2,253,608 | 63.1 | 1.250 | 0.091 | 0.161 |
| 2021 | 2,945,609 | 63.6 | 1.273 | 0.088 | 0.145 |
| 2022 | 2,557,406 | 61.3 | **0.969** | −0.011 | 0.134 |
| 2023 | 1,808,310 | 62.0 | 1.027 | 0.010 | 0.109 |
| 2024 | 1,965,632 | 62.7 | 1.132 | 0.051 | 0.116 |
| 2025 | 2,994,051 | 62.6 | 1.038 | 0.015 | 0.131 |
| 2026 | 1,828,852 | 62.0 | 1.029 | 0.010 | 0.127 |

**Exit mix — the no-stop tail quantified:** target n=16.33M PF 1.118 med +0.132% med-hold
404 bars; **moc n=21,157 PF 0.052 avg −2.98% (−$6.3M)** — 0.13% of trips eat 9% of gross.

**The lever ladder (all monotone the V6 way, now 1s-native):**

| lever | dead zone | best zone | shape |
|---|---|---|---|
| change from 20m low | [−0.1,0)% → 1.044 | [−5,−2)% → **1.427**, <−5% 1.386 | monotone deeper=better; NO tail inversion at this cut |
| distance from 20m high | [0,−2)% → 0.94 | <−10% → **1.41-1.42** (+$43.7M combined) | monotone |
| flush speed (1m move) | slower than −0.5% → ≤0.97 | <−2%/1m → **1.371** (+$50M, half the net) | monotone |
| **volat_20m** | **<20bp → 0.95-0.99 (11.5M trips, 70%, net NEGATIVE)** | ≥80bp → **1.329**; [40,80) 1.243 | monotone UP — ⭐ user's prediction confirmed: low-volat days are safely skippable |
| eff_20m | [−0.1,0.1) → 1.014 | **[−0.5,−0.3) → 1.199** (+$33.7M) | hump — moderate downtrend fades best (ADX-analog works) |
| lows into leg (K) | 0-2 → ~1.00 | **16-50 → 1.165**; >50 decays 1.084 | hump like V6 F3 |
| chg_1d | **<−10% → 0.926** (V6's un-fadeable repricing) | ≥+30% → **1.463**; [10,30) 1.365; [−10,−5) 1.199 | V6's wings again |
| **distance to target** (new) | **<2% → net NEGATIVE at every cut** (0.62-0.99) | **≥2% → 1.250, +$70.6M = >100% of net** | ⭐ THE new lever: only enter when the reversion prize is big |
| price (raw) | ≥$20 → ~1.00-1.02 | **$1-2 → 1.473, $2-5 → 1.275** | V6's low-price edge, but $2-5 is BROKER-TRADABLE (med +0.47%/trip vs the sub-$1 cost wall) |

Cost-reality first look: the tradable $2-5 bucket runs mean +0.21% / **median +0.47%**
(n=952k) — unlike the momentum program, the MEDIAN trade clears plausible $2-5 costs.
Median-trade discipline stays mandatory on every stacked cell.

### D2 full lever tables (net = $ @10k/trip, mc=0 attribution)

**Change from 20m low** — `signal_vwap/chan_lo − 1`: how far the signal bar printed below
the prior 20m min (the overshoot through the floor being broken):

| depth | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 14,603,982 | 62.3 | 1.044 | 0.013 | 0.121 | 19,173,458 |
| [−0.5,−0.1)% | 1,546,342 | 64.0 | 1.253 | 0.197 | 0.473 | 30,483,735 |
| [−1,−0.5)% | 149,858 | 65.6 | 1.371 | 0.523 | 1.155 | 7,843,018 |
| [−2,−1)% | 42,728 | 65.6 | 1.373 | 0.712 | 1.599 | 3,042,062 |
| [−5,−2)% | 9,805 | 66.3 | 1.427 | 1.061 | 2.090 | 1,040,257 |
| <−5% | 753 | 70.9 | 1.386 | 1.889 | 2.493 | 142,277 |

**Distance from 20m high** — `signal_vwap/chan_hi − 1` (the whole down-leg's size —
closest analog of V6's flush-depth lever):

| depth | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−1,0)% | 4,165,912 | 62.2 | 0.938 | −0.007 | 0.065 | −2,887,574 |
| [−2,−1)% | 4,836,683 | 61.8 | 0.944 | −0.013 | 0.136 | −6,075,954 |
| [−4,−2)% | 4,306,103 | 62.3 | 1.013 | 0.005 | 0.245 | 2,028,521 |
| [−6,−4)% | 1,414,557 | 63.5 | 1.094 | 0.055 | 0.441 | 7,733,961 |
| [−10,−6)% | 960,153 | 64.6 | 1.202 | 0.180 | 0.757 | 17,261,836 |
| [−15,−10)% | 415,353 | 66.7 | 1.418 | 0.521 | 1.367 | 21,630,825 |
| <−15% | 254,707 | 67.8 | 1.413 | 0.865 | 2.180 | 22,033,192 |

**Flush speed** — `signal_vwap/vwap_60_prev − 1` (move vs the previous non-overlapping
minute's rolling vwap — VELOCITY over the last ~1.5-2 minutes):

| speed | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.2,·)% | 3,873,624 | 63.0 | 0.930 | −0.009 | 0.070 | −3,292,779 |
| [−0.5,−0.2)% | 6,069,335 | 62.0 | 0.965 | −0.008 | 0.136 | −4,900,732 |
| [−1,−0.5)% | 3,711,897 | 61.8 | 1.019 | 0.007 | 0.243 | 2,765,611 |
| [−2,−1)% | 1,716,037 | 63.2 | 1.150 | 0.100 | 0.475 | 17,187,331 |
| <−2% | 982,575 | 65.7 | 1.371 | 0.509 | 1.209 | 49,965,376 |

**volat_20m band (bp/30s)** — the ATR% replacement:

| volat | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <7bp | 3,837,204 | 62.3 | 0.947 | −0.005 | 0.063 | −2,097,203 |
| [7,20) | 7,629,847 | 62.1 | 0.989 | −0.003 | 0.157 | −2,065,842 |
| [20,40) | 3,222,243 | 62.6 | 1.064 | 0.032 | 0.356 | 10,284,631 |
| [40,80) | 1,174,561 | 64.5 | 1.243 | 0.228 | 0.812 | 26,736,207 |
| ≥80bp | 489,613 | 65.7 | 1.329 | 0.590 | 1.702 | 28,867,015 |

**eff_20m signed band** — the ADX replacement:

| eff | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−0.5 | 1,150,835 | 63.1 | 1.066 | 0.030 | 0.153 | 3,501,216 |
| [−0.5,−0.3) | 4,791,831 | 63.5 | 1.199 | 0.070 | 0.146 | 33,715,472 |
| [−0.3,−0.1) | 7,369,489 | 62.4 | 1.090 | 0.031 | 0.127 | 22,684,256 |
| [−0.1,0.1) | 2,950,463 | 61.2 | 1.014 | 0.005 | 0.116 | 1,536,197 |
| ≥0.1 | 43,302 | 60.4 | 1.037 | 0.020 | 0.129 | 87,666 |
| cold | 47,548 | 61.0 | 1.068 | 0.042 | 0.222 | 200,000 |

**Lows into leg (V6's K lever):**

| k | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 0 (first low) | 357,646 | 61.1 | 0.994 | −0.002 | 0.117 | −82,799 |
| 1-2 | 685,017 | 61.2 | 1.007 | 0.003 | 0.118 | 182,154 |
| 3-5 | 953,911 | 61.5 | 1.037 | 0.014 | 0.121 | 1,347,306 |
| 6-15 | 2,670,009 | 62.1 | 1.099 | 0.036 | 0.126 | 9,591,754 |
| 16-50 | 5,771,703 | 62.7 | 1.165 | 0.057 | 0.132 | 32,979,808 |
| >50 | 5,915,182 | 63.0 | 1.084 | 0.030 | 0.139 | 17,706,584 |

**chg_1d (signal vs prev adjusted close, %):**

| chg1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 1,361,038 | 60.4 | 0.926 | −0.060 | 0.266 | −8,170,320 |
| [−10,−5)% | 2,280,223 | 63.4 | 1.199 | 0.065 | 0.197 | 14,862,277 |
| [−5,0)% | 6,014,000 | 62.5 | 1.047 | 0.011 | 0.105 | 6,539,755 |
| [0,10)% | 5,539,607 | 62.2 | 1.050 | 0.014 | 0.116 | 7,981,687 |
| [10,30)% | 774,817 | 65.0 | 1.365 | 0.239 | 0.413 | 18,518,722 |
| ≥30% | 383,783 | 66.3 | 1.463 | 0.573 | 1.136 | 21,992,687 |

**Distance from the 5m high at signal** — `exit_chan_hi/signal_vwap − 1` (the NEW lever;
⚠ arithmetically ≈ the 5m-horizon fall, a cousin of flush speed — cross-tab before
treating as independent):

| dist | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.1% | 36,882 | 63.8 | 0.619 | −0.022 | 0.027 | −79,968 |
| [0.1,0.2)% | 477,768 | 63.5 | 0.900 | −0.006 | 0.045 | −307,523 |
| [0.2,0.5)% | 3,349,955 | 62.4 | 0.935 | −0.007 | 0.073 | −2,510,043 |
| [0.5,1)% | 4,840,529 | 62.0 | 0.953 | −0.010 | 0.132 | −4,835,221 |
| [1,2)% | 4,407,003 | 61.9 | 0.993 | −0.003 | 0.230 | −1,137,059 |
| ≥2% | 3,241,331 | 64.3 | 1.250 | 0.218 | 0.601 | 70,594,621 |

**VOLUME 1m/20m rate ratio** — `(vol_60/60)/(vol_1200/1200)` (user, 2026-07-28):

| r | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 575,822 | 61.8 | 1.039 | 0.015 | 0.130 | 837,902 |
| [0.5,1)× | 6,273,505 | 62.4 | 1.070 | 0.023 | 0.124 | 14,215,813 |
| [1,2)× | 7,689,585 | 62.7 | 1.114 | 0.041 | 0.134 | 31,358,215 |
| [2,4)× | 1,662,587 | 62.6 | 1.185 | 0.084 | 0.161 | 13,940,694 |
| [4,8)× | 145,000 | 62.6 | 1.178 | 0.103 | 0.176 | 1,488,239 |
| ≥8× | 6,969 | 60.4 | **0.871** | −0.167 | 0.155 | −116,055 |

**TC 1m/20m rate ratio** — `(tc_60/60)/(tc_1200/1200)`:

| r | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 87,038 | 63.9 | **1.365** | 0.207 | 0.219 | 1,802,443 |
| [0.5,1)× | 6,109,864 | 62.4 | 1.060 | 0.020 | 0.125 | 12,209,693 |
| [1,2)× | 8,985,551 | 62.5 | 1.103 | 0.037 | 0.132 | 33,017,102 |
| [2,4)× | 1,103,901 | 63.0 | 1.257 | 0.122 | 0.175 | 13,478,025 |
| [4,8)× | 64,996 | 63.6 | 1.279 | 0.188 | 0.200 | 1,222,656 |
| ≥8× | 2,118 | 57.6 | 0.987 | −0.024 | 0.199 | −5,112 |

**VOLUME 1s/1m rate ratio** — `bar_vol/(vol_60/60)` (the signal bar's own burst):

| r | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 6,819,629 | 62.2 | 1.057 | 0.020 | 0.127 | 13,360,237 |
| [0.5,1)× | 2,291,207 | 62.6 | 1.124 | 0.043 | 0.129 | 9,838,852 |
| [1,2)× | 2,418,444 | 62.8 | 1.136 | 0.049 | 0.131 | 11,865,948 |
| [2,4)× | 2,070,512 | 62.9 | 1.141 | 0.052 | 0.135 | 10,759,590 |
| [4,8)× | 1,422,699 | 62.8 | 1.136 | 0.053 | 0.139 | 7,473,121 |
| ≥8× | 1,330,977 | 63.0 | 1.155 | 0.063 | 0.156 | 8,427,060 |

**TC 1s/1m rate ratio** — `bar_tc/(tc_60/60)`:

| r | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 6,027,723 | 62.2 | 1.069 | 0.023 | 0.130 | 14,156,151 |
| [0.5,1)× | 2,784,130 | 62.6 | 1.116 | 0.041 | 0.128 | 11,346,987 |
| [1,2)× | 2,637,655 | 62.7 | 1.125 | 0.045 | 0.129 | 11,798,566 |
| [2,4)× | 2,260,969 | 62.7 | 1.113 | 0.041 | 0.130 | 9,305,720 |
| [4,8)× | 1,491,278 | 62.8 | 1.122 | 0.047 | 0.139 | 6,951,823 |
| ≥8× | 1,151,713 | 63.1 | 1.171 | 0.071 | 0.160 | 8,165,560 |

Participation reading: the information lives at the **1m/20m horizon** — moderate
acceleration 2-8× = the capitulation confirmation (vol 1.18-1.19, tc 1.26-1.28), the ≥8×
volume extreme INVERTS (0.871 — the momentum program's "≥4× crowd is toxic" and V6's
loud-extreme break, reproduced), and the tc <0.5× QUIET-minute flush is a small but
strong wing (1.365, n=87k — thin-tape drifts to a low fade well). The **1s/1m
instantaneous ratios are nearly FLAT** (1.06→1.17) — the single-second print carries
almost no information; don't spend a gate on it.

**Price bucket (raw scale):**

| px | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 608,169 | 61.5 | 1.106 | 0.123 | 0.465 | 7,452,061 |
| $1-2 | 301,801 | 65.9 | 1.473 | 0.486 | 0.855 | 14,657,313 |
| $2-5 | 952,021 | 63.9 | 1.275 | 0.209 | 0.472 | 19,852,988 |
| $5-20 | 4,069,040 | 62.6 | 1.101 | 0.043 | 0.221 | 17,589,578 |
| $20-100 | 6,629,760 | 62.2 | 1.005 | 0.001 | 0.119 | 933,706 |
| ≥$100 | 3,792,677 | 62.7 | 1.019 | 0.003 | 0.084 | 1,239,160 |

⏭ Cell stacking under user direction (volat × distance × depth × price look natural),
then mc=1 + K-gate reruns of chosen cells, year-audit + median at every step.

## S1 (2026-07-28) — the first stack: `volat_20m >= 40bp` (user's call)

**n=1,664,174 (10.2% of the sampler) / win 64.8 / PF 1.281 / avg +0.334% / MEDIAN
+0.96% / +$55.6M (90% of the baseline net on 10% of the trips).** Top-3 tkd = 0.5%.
Positive EVERY year, though still regime-spread: **2020 1.780 / 2021 1.555 / 2022 1.047 /
2023 1.150 / 2024 1.276 / 2025 1.116 / 2026 1.127**.

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 258,941 | 67.8 | 1.780 | 0.700 | 1.108 | 18,113,806 |
| 2021 | 328,897 | 66.5 | 1.555 | 0.479 | 0.959 | 15,743,159 |
| 2022 | 191,706 | 63.5 | 1.047 | 0.059 | 0.824 | 1,131,766 |
| 2023 | 170,541 | 62.9 | 1.150 | 0.208 | 0.924 | 3,548,830 |
| 2024 | 254,336 | 64.0 | 1.276 | 0.371 | 1.016 | 9,443,222 |
| 2025 | 327,581 | 63.5 | 1.116 | 0.162 | 0.916 | 5,291,111 |
| 2026 | 132,172 | 64.3 | 1.127 | 0.176 | 0.914 | 2,331,328 |

Exit mix: target n=1,654,765 PF 1.309 med +0.972%; **moc n=9,409 PF 0.062 avg −4.45%
(−$4.19M = 7% of gross)** — the no-stop tail is proportionally BIGGER inside the
high-volat band; a tail treatment (or SpikeFader-style hedge) will matter for the book.

**⭐ NEW under S1 — the volat CEILING appears** (fine bands inside ≥40bp):

| volat | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 805,708 | 64.1 | 1.203 | 0.171 | 0.717 | 13,763,514 |
| [60,80) | 368,853 | 65.3 | 1.307 | 0.352 | 1.123 | 12,972,693 |
| [80,120) | 330,952 | 65.9 | 1.373 | 0.557 | 1.561 | 18,418,149 |
| [120,200) | 139,045 | 65.9 | 1.385 | 0.808 | 2.121 | 11,234,849 |
| **≥200bp** | 19,616 | 60.9 | **0.914** | −0.401 | 2.431 | **−785,984** |

Rises through [120,200) then INVERTS ≥200bp — V6's MaxAtrPct ceiling, reproduced in
slot-volat units. The working band is **[40,200)bp**.

**Levers inside S1 (full tables):**

| dist from 20m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−2,−1)% | 60 | 61.7 | 0.481 | −0.659 | 0.380 | −3,953 |
| [−4,−2)% | 41,197 | 59.3 | 0.857 | −0.115 | 0.400 | −473,143 |
| [−6,−4)% | 281,392 | 61.5 | 0.990 | −0.008 | 0.551 | −229,328 |
| [−10,−6)% | 680,638 | 64.3 | 1.191 | 0.188 | 0.846 | 12,780,596 |
| [−15,−10)% | 406,481 | 66.8 | 1.426 | 0.530 | 1.382 | 21,524,059 |
| <−15% | 254,406 | 67.8 | 1.413 | 0.865 | 2.181 | 22,004,991 |

⭐ High volat + SHALLOW leg (>−6%) is now NEGATIVE/flat — volatile name, small dip =
don't buy. The S1 edge lives at leg depth ≥6%, no tail inversion through <−15%.

| flush speed (1m) | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.2,·)% | 1,771 | 54.0 | 0.416 | −0.851 | 0.177 | −150,649 |
| [−0.5,−0.2)% | 27,472 | 61.4 | 0.783 | −0.196 | 0.501 | −537,623 |
| [−1,−0.5)% | 191,308 | 63.0 | 0.974 | −0.022 | 0.625 | −424,463 |
| [−2,−1)% | 582,845 | 64.3 | 1.177 | 0.164 | 0.799 | 9,559,120 |
| <−2% | 860,778 | 65.7 | 1.379 | 0.548 | 1.332 | 47,156,838 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 848,401 | 64.2 | 1.204 | 0.220 | 0.840 | 18,650,909 |
| [−0.5,−0.1)% | 642,623 | 65.4 | 1.332 | 0.396 | 1.049 | 25,460,636 |
| [−1,−0.5)% | 123,761 | 65.8 | 1.384 | 0.596 | 1.429 | 7,372,256 |
| [−2,−1)% | 39,637 | 65.4 | 1.372 | 0.736 | 1.704 | 2,918,300 |
| [−5,−2)% | 9,138 | 65.9 | 1.442 | 1.127 | 2.342 | 1,029,951 |
| <−5% | 614 | 69.7 | 1.578 | 2.788 | 3.704 | 171,170 |

| eff_20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−0.5 | 108,311 | 65.6 | 1.137 | 0.253 | 1.200 | 2,741,659 |
| [−0.5,−0.3) | 499,235 | 67.6 | **1.455** | 0.514 | 1.141 | 25,684,179 |
| [−0.3,−0.1) | 729,536 | 64.3 | 1.264 | 0.295 | 0.891 | 21,521,185 |
| [−0.1,0.1) | 307,927 | 61.7 | 1.136 | 0.165 | 0.758 | 5,068,453 |
| ≥0.1 | 6,620 | 61.8 | 1.259 | 0.415 | 0.992 | 274,666 |
| cold | 12,545 | 63.8 | 1.198 | 0.250 | 0.881 | 313,080 |

| lows into leg | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 0 (first low) | 43,540 | 61.0 | 1.097 | 0.120 | 0.725 | 522,112 |
| 1-2 | 82,866 | 61.5 | 1.125 | 0.152 | 0.759 | 1,257,927 |
| 3-5 | 113,876 | 62.5 | 1.178 | 0.209 | 0.810 | 2,381,092 |
| 6-15 | 310,431 | 64.2 | 1.295 | 0.331 | 0.911 | 10,264,512 |
| 16-50 | 621,878 | 66.4 | **1.447** | 0.474 | 1.034 | 29,486,214 |
| >50 | 491,583 | 64.8 | 1.172 | 0.238 | 0.980 | 11,691,365 |

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 401,893 | 62.2 | **0.997** | −0.004 | 0.811 | −175,181 |
| [−10,−5)% | 149,234 | 67.0 | 1.499 | 0.423 | 0.880 | 6,308,358 |
| [−5,0)% | 156,407 | 62.6 | 1.169 | 0.167 | 0.694 | 2,605,649 |
| [0,10)% | 301,896 | 65.2 | 1.320 | 0.322 | 0.880 | 9,728,365 |
| [10,30)% | 315,476 | 66.2 | 1.481 | 0.494 | 1.075 | 15,572,283 |
| ≥30% | 339,268 | 66.5 | 1.475 | 0.636 | 1.423 | 21,563,748 |

| dist from 5m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.1% | 3 | 66.7 | 1.413 | 0.319 | 1.114 | 96 |
| [0.1,0.2)% | 4 | 100.0 | — | 0.921 | 0.927 | 369 |
| [0.2,0.5)% | 34 | 41.2 | 0.259 | −0.815 | −0.043 | −2,771 |
| [0.5,1)% | 949 | 49.2 | 0.258 | −1.146 | −0.019 | −108,798 |
| [1,2)% | 57,085 | 60.1 | 0.790 | −0.175 | 0.455 | −998,800 |
| ≥2% | 1,606,099 | 65.0 | 1.294 | 0.353 | 0.992 | 56,713,127 |

⭐ volat≥40bp nearly IMPLIES dist≥2% (96.5% of S1) — the distance lever mostly
dissolves into the volat floor; its sub-2% remnant is still negative (drop it or floor it).

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 356,723 | 63.3 | 1.173 | 0.269 | 1.073 | 9,594,613 |
| $1-2 | 190,118 | 67.2 | 1.537 | 0.689 | 1.388 | 13,101,071 |
| $2-5 | 414,968 | 65.5 | 1.374 | 0.422 | 1.046 | 17,500,484 |
| $5-20 | 510,399 | 64.9 | 1.250 | 0.256 | 0.850 | 13,058,684 |
| $20-100 | 178,975 | 63.9 | 1.125 | 0.123 | 0.711 | 2,208,084 |
| ≥$100 | 12,991 | 61.1 | 1.121 | 0.108 | 0.549 | 140,286 |

| VOLUME 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 71,817 | 65.6 | 1.268 | 0.301 | 0.963 | 2,162,442 |
| [0.5,1)× | 564,336 | 65.1 | 1.265 | 0.294 | 0.926 | 16,611,481 |
| [1,2)× | 791,884 | 64.7 | 1.272 | 0.322 | 0.952 | 25,500,393 |
| [2,4)× | 220,525 | 64.6 | 1.351 | 0.473 | 1.082 | 10,424,152 |
| [4,8)× | 15,026 | 63.7 | 1.295 | 0.629 | 1.246 | 944,577 |
| ≥8× | 586 | 59.2 | 0.883 | −0.680 | 1.001 | −39,822 |

| TC 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 29,392 | 68.4 | **1.576** | 0.642 | 1.317 | 1,886,007 |
| [0.5,1)× | 565,328 | 64.9 | 1.239 | 0.269 | 0.913 | 15,227,497 |
| [1,2)× | 900,711 | 64.6 | 1.264 | 0.313 | 0.944 | 28,168,460 |
| [2,4)× | 160,617 | 65.5 | 1.438 | 0.595 | 1.190 | 9,557,049 |
| [4,8)× | 7,902 | 64.9 | 1.455 | 1.076 | 1.642 | 850,423 |
| ≥8× | 224 | 47.3 | **0.588** | −3.849 | −1.469 | −86,214 |

(1s/1m volume and tc: still FLAT inside S1 — 1.22-1.33 across all bands, no gate.)

**S1 reading:** the quiet-tc wing SURVIVES the volat floor (tc<0.5× → 1.576 — not the
volat lever in disguise); the participation ceiling at ≥8× confirms; shallow legs and
slow flushes turn outright negative inside high-volat (they were merely mediocre
unconditionally). Natural S2 candidates: volat ceiling 200bp, leg depth ≤−6%, speed
≤−1%, chg_1d ≥−10%, participation <8×.

## S2 (2026-07-28) — stack 2: `volat_20m >= 40bp AND flush speed < −2%` (user's call)

**n=860,778 (5.3% of the sampler) / win 65.7 / PF 1.379 / avg +0.548% / MEDIAN +1.332% /
+$47.2M.** Top-3 tkd = 0.8%.

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 138,092 | 69.5 | 2.084 | 1.082 | 1.541 | 14,940,790 |
| 2021 | 149,199 | 67.7 | 1.774 | 0.754 | 1.261 | 11,243,148 |
| 2022 | 89,338 | 63.1 | **1.030** | 0.050 | 1.065 | 446,238 |
| 2023 | 95,615 | 64.6 | 1.275 | 0.445 | 1.354 | 4,250,454 |
| 2024 | 148,997 | 64.9 | 1.381 | 0.604 | 1.412 | 9,002,566 |
| 2025 | 172,153 | 64.0 | 1.182 | 0.312 | 1.287 | 5,379,558 |
| 2026 | 67,384 | 64.8 | 1.153 | 0.281 | 1.320 | 1,894,083 |

2022 stays the weak year (1.030) — every stack so far amplifies the good years more than
it fixes 2022. Exit mix: target 854,600 @ 1.412 med +1.353%; moc 6,178 @ 0.082 avg
−4.55% (−$2.8M = 5.6% of gross).

| speed (fine, within <−2%) | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−3,−2)% | 381,762 | 65.6 | 1.338 | 0.373 | 1.094 | 14,249,409 |
| [−5,−3)% | 312,460 | 66.1 | **1.421** | 0.590 | 1.465 | 18,447,215 |
| [−10,−5)% | 145,708 | 65.4 | 1.403 | 0.815 | 1.930 | 11,869,140 |
| <−10% | 20,848 | 64.5 | 1.289 | 1.243 | 3.271 | 2,591,074 |

| dist from 20m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−4,·)% | 3,449 | 59.2 | 0.924 | −0.073 | 0.485 | −25,068 |
| [−6,−4)% | 56,052 | 61.2 | 1.041 | 0.041 | 0.612 | 228,214 |
| [−10,−6)% | 284,692 | 63.9 | 1.247 | 0.275 | 0.978 | 7,829,968 |
| [−15,−10)% | 287,351 | 66.9 | 1.482 | 0.627 | 1.515 | 18,029,635 |
| [−25,−15)% | 187,694 | 68.4 | **1.588** | 1.016 | 2.203 | 19,074,988 |
| **<−25%** | 41,540 | 64.6 | **1.119** | 0.486 | 2.950 | 2,019,101 |

⭐ **V6's deep-tail inversion appears at <−25%** (1.588 → 1.119) — the "un-fadeable
repricing" boundary sits deeper on 1s than V6's 1m −15%, but it exists.

| volat (fine) | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 233,231 | 65.6 | 1.419 | 0.399 | 0.959 | 9,297,145 |
| [60,80) | 217,684 | 65.6 | 1.403 | 0.484 | 1.243 | 10,541,736 |
| [80,120) | 262,426 | 66.2 | 1.434 | 0.657 | 1.649 | 17,237,037 |
| [120,200) | 128,143 | 65.9 | 1.398 | 0.846 | 2.149 | 10,847,183 |
| ≥200bp | 19,294 | 60.9 | **0.915** | −0.397 | 2.449 | −766,263 |

| eff_20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−0.5 | 63,386 | 66.2 | 1.154 | 0.373 | 1.708 | 2,362,100 |
| [−0.5,−0.3) | 258,369 | 68.7 | **1.572** | 0.785 | 1.605 | 20,284,212 |
| [−0.3,−0.1) | 363,451 | 65.2 | 1.388 | 0.514 | 1.233 | 18,666,227 |
| [−0.1,0.1) | 164,537 | 62.2 | 1.227 | 0.324 | 1.003 | 5,334,254 |
| ≥0.1 | 5,513 | 62.5 | 1.273 | 0.484 | 1.200 | 266,810 |
| cold | 5,522 | 65.8 | 1.246 | 0.440 | 1.313 | 243,235 |

| lows into leg | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 0 (first low) | 21,175 | 61.3 | 1.173 | 0.263 | 0.953 | 557,180 |
| 1-2 | 43,049 | 61.8 | 1.195 | 0.287 | 0.990 | 1,234,371 |
| 3-5 | 59,902 | 62.8 | 1.251 | 0.354 | 1.044 | 2,118,585 |
| 6-15 | 164,426 | 65.0 | 1.412 | 0.542 | 1.227 | 8,905,115 |
| 16-50 | 319,646 | 67.4 | **1.594** | 0.744 | 1.436 | 23,777,346 |
| >50 | 252,580 | 65.8 | 1.236 | 0.418 | 1.425 | 10,564,241 |

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 196,991 | 63.8 | 1.077 | 0.151 | 1.239 | 2,980,834 |
| [−10,−5)% | 55,088 | 69.0 | **1.703** | 0.763 | 1.337 | 4,205,335 |
| [−5,0)% | 58,835 | 62.8 | 1.243 | 0.316 | 0.927 | 1,856,296 |
| [0,10)% | 130,813 | 66.7 | 1.501 | 0.606 | 1.282 | 7,924,089 |
| [10,30)% | 172,450 | 66.8 | 1.589 | 0.694 | 1.354 | 11,966,969 |
| ≥30% | 246,601 | 65.9 | 1.510 | 0.739 | 1.546 | 18,223,317 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 342,442 | 65.3 | 1.329 | 0.440 | 1.204 | 15,078,408 |
| [−0.5,−0.1)% | 367,452 | 66.1 | 1.412 | 0.575 | 1.357 | 21,111,074 |
| [−1,−0.5)% | 102,793 | 66.0 | 1.405 | 0.669 | 1.562 | 6,872,482 |
| [−2,−1)% | 38,339 | 65.5 | 1.377 | 0.755 | 1.739 | 2,893,752 |
| [−5,−2)% | 9,138 | 65.9 | 1.442 | 1.127 | 2.342 | 1,029,951 |
| <−5% | 614 | 69.7 | 1.578 | 2.788 | 3.704 | 171,170 |

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 231,494 | 64.6 | 1.285 | 0.489 | 1.449 | 11,321,648 |
| $1-2 | 130,584 | 67.8 | **1.615** | 0.872 | 1.648 | 11,386,517 |
| $2-5 | 225,498 | 65.7 | 1.450 | 0.600 | 1.307 | 13,531,698 |
| $5-20 | 209,632 | 65.9 | 1.341 | 0.444 | 1.177 | 9,308,966 |
| $20-100 | 59,166 | 65.2 | 1.182 | 0.253 | 1.089 | 1,499,731 |
| ≥$100 | 4,404 | 61.0 | 1.205 | 0.246 | 0.735 | 108,278 |

| VOLUME 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 19,992 | 66.9 | 1.360 | 0.584 | 1.709 | 1,168,340 |
| [0.5,1)× | 207,702 | 66.3 | 1.409 | 0.595 | 1.477 | 12,349,094 |
| [1,2)× | 445,398 | 65.6 | 1.361 | 0.504 | 1.281 | 22,442,968 |
| [2,4)× | 173,701 | 65.2 | 1.405 | 0.591 | 1.269 | 10,261,611 |
| [4,8)× | 13,470 | 64.2 | 1.324 | 0.726 | 1.435 | 978,302 |
| ≥8× | 515 | 56.5 | **0.871** | −0.844 | 1.097 | −43,477 |

| TC 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 11,095 | 69.2 | **1.600** | 0.898 | 2.025 | 995,796 |
| [0.5,1)× | 198,328 | 66.1 | 1.370 | 0.560 | 1.475 | 11,113,152 |
| [1,2)× | 505,363 | 65.5 | 1.352 | 0.493 | 1.267 | 24,911,816 |
| [2,4)× | 138,097 | 65.8 | 1.475 | 0.679 | 1.327 | 9,378,180 |
| [4,8)× | 7,675 | 64.9 | 1.455 | 1.099 | 1.711 | 843,810 |
| ≥8× | 220 | 46.8 | **0.589** | −3.905 | −1.514 | −85,916 |

(1s/1m volume and tc: FLAT again — 1.32-1.44 across every band. Third confirmation:
the instantaneous ratio carries nothing.)

**S2 reading:** speed's own sweet spot is [−10,−3) (1.40-1.42); the <−10% waterfall
softens (1.289). The 20m-high distance lever sharpens into a BAND [−25,−6) with V6's
deep-tail inversion reappearing at <−25%. Participation stays band-shaped (≥8× toxic,
quiet-tc wing 1.60). chg_1d <−10% recovers to 1.077 inside S2 but is still the floor.
2022 remains unfixed (1.030) — the flush cells are regime-amplified, not regime-neutral.

## S3 (2026-07-28) — participation ceilings at entry + THE PRICE-ACCEPTANCE STOPS

**S3 post-hoc spec = S2 + `vr_1m20m < 8` + `tcr_1m20m < 8` at the signal: n=860,182 /
win 65.7 / PF 1.381 / avg +0.550% / median +1.332% / +$47.3M.** The ceiling removed just
596 trips at PF 0.736 / −1.89%/trip — a free cleanup. Year audit unchanged (2020 2.083 …
2022 1.030 … 2026 1.157).

**⭐ ENGINE v2 — the price-acceptance stops (user, 2026-07-28; Lance's "price
acceptance"):** while holding, a **NEW entry-channel low** made
(a) on `(vol_60/60)/(vol_1200/1200) >= 8` → `vol_stop`,
(b) on the tc ratio ≥ 8 → `tc_stop`, or
(c) at `vwap/vwap_60_prev − 1 < −1%` → `speed_stop`
exits the position (next-bar fill). These are NOT price-level stops (V6 F6: destructive)
— they fire only on qualified fresh lows: the market accepting the lower price = the
flush continuing, not snapping. Flags `--vol-stop-ratio 8 / --tc-stop-ratio 8 /
--speed-stop-pct -0.01` (inf/0 = off). Defaults ON.

One-week smoke (2026-06-01..05, ungated sampler): exit mix becomes target 86,149
(med +0.117%) / speed_stop 16,095 (med −0.213%) / vol_stop 341 (−0.641%) / tc_stop 110
(−0.883%) / **moc 0 — the catastrophic never-revert tail is GONE**, converted into many
small controlled losses. Broad-sampler PF dips (0.949 that week) because the ungated
population's stopped trips often limped back to target — the stops' verdict belongs to
the S3 cell + mc=1 book at the next full rerun (**DEFERRED**, ~80 min).

⚠ The parquet on disk (`base20_e1200_x300/`) predates the stops — its trips are
no-stop trips. Post-hoc stop analysis on it is NOT possible (stops change the path);
only the rerun answers it.

## S4 (2026-07-28) — stack 4: + `lows_since_first_low ∈ [16,50]` + the K×eff verdict

**n=319,306 / win 67.4 / PF 1.599 / avg +0.746% / MEDIAN +1.437% / +$23.8M.** Top-3 tkd
= 0.5%. **⭐ 2022 finally lifts: every year ≥ 1.227.**

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 52,583 | 71.1 | 2.378 | 1.231 | 1.673 | 6,472,387 |
| 2021 | 56,443 | 70.7 | 2.176 | 0.943 | 1.396 | 5,320,874 |
| 2022 | 32,657 | 64.6 | **1.227** | 0.322 | 1.142 | 1,050,709 |
| 2023 | 35,388 | 66.1 | 1.357 | 0.544 | 1.459 | 1,925,869 |
| 2024 | 54,613 | 65.8 | 1.537 | 0.750 | 1.499 | 4,094,051 |
| 2025 | 62,951 | 65.2 | 1.396 | 0.570 | 1.355 | 3,586,567 |
| 2026 | 24,671 | 66.8 | 1.353 | 0.553 | 1.405 | 1,364,905 |

**⭐ THE K×eff CROSS-TAB (on the S3 base — the redundancy question, user):**

| k | pf eff<−0.3 | pf [−0.3,−0.1) | pf ≥−0.1 | n_hi | n_mid | n_lo |
|---|---|---|---|---|---|---|
| 0-5 | 1.103 | 1.157 | **1.249** | 2,713 | 36,939 | 83,872 |
| 6-15 | 1.481 | 1.402 | 1.405 | 19,265 | 94,837 | 49,486 |
| 16-50 | **1.746** | 1.556 | 1.143 | 134,715 | 157,737 | 24,417 |
| >50 | 1.293 | 1.191 | **0.781** | 164,752 | 73,735 | 12,192 |

**NOT redundant — they INTERACT.** The populations are correlated (mature legs ≈ strong
20m downtrend ≈ eff-negative: n_hi piles into k≥16), but the information is independent:
inside k 16-50 eff still separates 1.746 / 1.556 / 1.143, and inside eff-hi K still
separates 1.10 → 1.75. More: **eff INVERTS in young legs** (k 0-5: eff-flat 1.249 beats
eff-hi 1.103) — a fresh dip in a still-trendless tape fades fine; a mature leg needs the
established downtrend to be worth fading. Peak cell = the AND: k 16-50 × eff<−0.3 →
**1.746**. (And k>50 × eff-flat = 0.781 — a stale leg with no trend is the worst cell.)

**S4 lever tables:**

| eff_20m (inside S4) | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−0.5 | 13,222 | 68.1 | 1.364 | 0.639 | 1.660 | 844,602 |
| [−0.5,−0.3) | 121,493 | 69.6 | **1.808** | 0.953 | 1.630 | 11,582,092 |
| [−0.3,−0.1) | 157,737 | 66.8 | 1.556 | 0.678 | 1.347 | 10,701,808 |
| [−0.1,0.1) | 24,172 | 60.6 | 1.143 | 0.208 | 0.874 | 503,422 |
| ≥0.1 | 245 | 56.3 | 1.099 | 0.160 | 0.669 | 3,929 |
| cold | 2,437 | 65.1 | 1.532 | 0.737 | 1.254 | 179,509 |

| flush speed | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−3,−2)% | 144,990 | 67.1 | 1.518 | 0.516 | 1.188 | 7,487,815 |
| [−5,−3)% | 117,069 | 67.9 | **1.680** | 0.829 | 1.604 | 9,704,252 |
| [−10,−5)% | 51,060 | 67.1 | 1.643 | 1.116 | 2.074 | 5,696,872 |
| <−10% | 6,187 | 66.2 | 1.422 | 1.497 | 3.410 | 926,423 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 129,147 | 66.8 | 1.537 | 0.628 | 1.299 | 8,110,426 |
| [−0.5,−0.1)% | 137,054 | 67.7 | 1.614 | 0.751 | 1.458 | 10,291,773 |
| [−1,−0.5)% | 37,387 | 68.4 | 1.678 | 0.943 | 1.725 | 3,523,796 |
| [−2,−1)% | 12,735 | 68.2 | 1.662 | 1.099 | 1.951 | 1,399,572 |
| <−2% | 2,983 | 67.6 | 1.784 | 1.642 | 2.590 | 489,796 |

| dist from 20m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−6,·)% | 16,676 | 62.2 | 1.112 | 0.102 | 0.625 | 169,841 |
| [−10,−6)% | 102,849 | 65.2 | 1.367 | 0.372 | 1.029 | 3,820,946 |
| [−15,−10)% | 111,245 | 68.2 | 1.654 | 0.760 | 1.571 | 8,450,962 |
| [−25,−15)% | 74,370 | 70.2 | **1.878** | 1.278 | 2.289 | 9,504,489 |
| <−25% | 14,166 | 68.6 | 1.456 | 1.319 | 3.299 | 1,869,125 |

| volat_20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 85,642 | 66.4 | 1.478 | 0.432 | 0.981 | 3,698,372 |
| [60,80) | 80,373 | 67.6 | 1.649 | 0.672 | 1.368 | 5,397,376 |
| [80,120) | 98,114 | 67.7 | 1.625 | 0.841 | 1.757 | 8,248,938 |
| [120,200) | 49,015 | 68.3 | 1.689 | 1.194 | 2.320 | 5,851,816 |
| ≥200bp | 6,162 | 66.1 | 1.307 | 1.004 | 3.244 | 618,859 |

(⭐ the ≥200bp bucket is RESCUED inside the K band — 0.915 → 1.307; a mature leg makes
even chaos-volat fadeable. The ceiling may be conditional, not absolute.)

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 57,286 | 65.9 | 1.327 | 0.504 | 1.356 | 2,886,098 |
| [−10,−5)% | 22,077 | 69.9 | **1.856** | 0.846 | 1.344 | 1,866,944 |
| [−5,0)% | 25,248 | 63.9 | 1.377 | 0.452 | 1.010 | 1,141,206 |
| [0,10)% | 52,204 | 68.1 | 1.674 | 0.747 | 1.391 | 3,900,080 |
| [10,30)% | 66,877 | 68.6 | 1.795 | 0.850 | 1.455 | 5,686,240 |
| ≥30% | 95,614 | 67.5 | 1.651 | 0.872 | 1.660 | 8,334,793 |

(chg_1d <−10% also rescued: 1.077 → 1.327.)

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 85,798 | 66.3 | 1.606 | 0.834 | 1.572 | 7,159,546 |
| $1-2 | 50,807 | 69.6 | 1.795 | 1.005 | 1.772 | 5,107,968 |
| $2-5 | 85,388 | 67.7 | 1.685 | 0.782 | 1.408 | 6,673,945 |
| $5-20 | 75,920 | 67.4 | 1.470 | 0.562 | 1.266 | 4,267,389 |
| $20-100 | 19,896 | 65.5 | 1.230 | 0.294 | 1.083 | 584,682 |
| ≥$100 | 1,497 | 64.9 | 1.119 | 0.146 | 0.918 | 21,833 |

| VOLUME 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 6,542 | 70.3 | 1.692 | 0.967 | 2.024 | 632,451 |
| [0.5,1)× | 72,903 | 68.7 | 1.685 | 0.873 | 1.673 | 6,365,648 |
| [1,2)× | 164,834 | 67.4 | 1.591 | 0.713 | 1.387 | 11,748,293 |
| [2,4)× | 69,349 | 66.0 | 1.516 | 0.647 | 1.284 | 4,489,582 |
| [4,8)× | 5,678 | 65.1 | 1.578 | 1.020 | 1.456 | 579,387 |

| TC 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 3,809 | 72.8 | 1.812 | 1.103 | 2.284 | 420,251 |
| [0.5,1)× | 69,340 | 68.6 | 1.699 | 0.904 | 1.704 | 6,270,118 |
| [1,2)× | 186,486 | 67.3 | 1.565 | 0.686 | 1.373 | 12,784,246 |
| [2,4)× | 56,645 | 66.2 | 1.552 | 0.696 | 1.302 | 3,940,072 |
| [4,8)× | 3,026 | 65.2 | 1.754 | 1.324 | 1.630 | 400,675 |

Exit mix: target 317,343 @ 1.640 med +1.453%; moc 1,963 @ 0.066 avg −5.02% (−$985k =
3.8% of gross — the acceptance stops target exactly these).

**S4 reading:** the K band is the 2022 FIX (1.030 → 1.227) — leg maturity is the
regime-robust ingredient the volat/speed gates lacked. It also rescues the marginal
wings (≥200bp volat, chg_1d <−10%, <−25% dist). Inside S4 the levers still ranked:
eff [−0.5,−0.3) 1.808 > dist [−25,−15) 1.878 > speed [−5,−3) 1.680 > quiet-tc 1.812.

## S5 (2026-07-28) — stack 5: + `eff_20m ∈ [−0.5,−0.3)`

**Spec: volat≥40bp × speed<−2% × vr<8 × tcr<8 × K∈[16,50] × eff∈[−0.5,−0.3).
n=121,493 / win 69.6 / PF 1.808 / avg +0.953% / MEDIAN +1.63% / +$11.6M.**
Breadth: 75 trips/day (mc=0 — same-leg adds included), 2,786 symbols, 1,619 days,
top-3 tkd 0.5%. **Worst year 1.363.**

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 19,809 | 73.4 | 2.974 | 1.543 | 1.905 | 3,057,517 |
| 2021 | 21,658 | 72.5 | 2.416 | 1.082 | 1.543 | 2,343,192 |
| 2022 | 12,374 | 67.0 | 1.363 | 0.491 | 1.376 | 607,614 |
| 2023 | 14,273 | 67.6 | 1.470 | 0.689 | 1.601 | 984,117 |
| 2024 | 20,381 | 69.3 | 1.853 | 1.064 | 1.741 | 2,168,935 |
| 2025 | 23,835 | 67.7 | 1.570 | 0.776 | 1.572 | 1,848,445 |
| 2026 | 9,163 | 66.9 | 1.365 | 0.625 | 1.607 | 572,271 |

Exit mix: target 120,929 @ 1.846 med +1.645%; moc 564 @ 0.069 avg −5.01% (−$283k =
2.3% of gross — each stack shrinks the tail's weight).

| K fine [16,50] | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 16-25 | 34,822 | 68.1 | 1.603 | 0.757 | 1.545 | 2,637,500 |
| 26-35 | 38,225 | 70.0 | 1.908 | 1.023 | 1.645 | 3,908,953 |
| 36-50 | 48,446 | 70.4 | 1.891 | 1.039 | 1.681 | 5,035,639 |

| flush speed | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−3,−2)% | 54,521 | 68.8 | 1.672 | 0.650 | 1.346 | 3,546,298 |
| [−5,−3)% | 44,936 | 70.5 | **1.933** | 1.052 | 1.786 | 4,726,274 |
| [−10,−5)% | 19,538 | 69.8 | 1.868 | 1.424 | 2.408 | 2,781,920 |
| <−10% | 2,498 | 69.3 | 1.673 | 2.112 | 4.010 | 527,601 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 48,837 | 68.9 | 1.722 | 0.806 | 1.490 | 3,934,671 |
| [−0.5,−0.1)% | 52,538 | 69.9 | 1.822 | 0.952 | 1.647 | 5,003,765 |
| [−1,−0.5)% | 14,412 | 70.4 | 1.885 | 1.166 | 1.930 | 1,680,477 |
| <−1% | 5,706 | 71.0 | **2.084** | 1.688 | 2.455 | 963,179 |

| dist from 20m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−6,·)% | 1,059 | 65.1 | 1.272 | 0.211 | 0.778 | 22,398 |
| [−10,−6)% | 28,994 | 66.8 | 1.480 | 0.431 | 1.050 | 1,250,735 |
| [−15,−10)% | 47,422 | 69.9 | 1.827 | 0.855 | 1.621 | 4,054,482 |
| [−25,−15)% | 37,002 | 71.3 | **2.025** | 1.368 | 2.323 | 5,061,600 |
| <−25% | 7,016 | 71.2 | 1.662 | 1.700 | 3.565 | 1,192,878 |

| volat_20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 31,773 | 68.0 | 1.609 | 0.532 | 1.107 | 1,689,924 |
| [60,80) | 30,912 | 69.6 | 1.862 | 0.855 | 1.567 | 2,643,013 |
| [80,120) | 37,644 | 70.2 | **1.901** | 1.102 | 1.970 | 4,146,589 |
| [120,200) | 18,912 | 71.1 | 1.855 | 1.454 | 2.638 | 2,749,862 |
| ≥200bp | 2,252 | 69.3 | 1.529 | 1.566 | 3.990 | 352,704 |

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 21,761 | 68.7 | 1.577 | 0.818 | 1.636 | 1,781,104 |
| [−10,−5)% | 8,944 | 71.1 | **2.120** | 1.065 | 1.515 | 952,344 |
| [−5,0)% | 9,996 | 66.0 | 1.551 | 0.644 | 1.280 | 643,839 |
| [0,10)% | 19,802 | 69.8 | 1.842 | 0.905 | 1.493 | 1,791,744 |
| [10,30)% | 24,904 | 70.3 | 1.953 | 0.980 | 1.586 | 2,440,441 |
| ≥30% | 36,086 | 70.2 | 1.876 | 1.101 | 1.896 | 3,972,619 |

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 31,732 | 69.1 | 1.867 | 1.104 | 1.788 | 3,503,359 |
| $1-2 | 21,234 | 72.1 | **2.157** | 1.276 | 1.937 | 2,710,003 |
| $2-5 | 33,537 | 69.9 | 1.952 | 0.992 | 1.567 | 3,326,144 |
| $5-20 | 27,400 | 68.4 | 1.484 | 0.620 | 1.447 | 1,698,195 |
| $20-100 | 7,127 | 66.8 | 1.337 | 0.426 | 1.239 | 303,564 |
| ≥$100 | 463 | 77.1 | 2.008 | 0.882 | 1.465 | 40,826 |

Participation inside S5: FLAT (vol 1.65-1.91, tc 1.50-1.95 across all bands) — fully
consumed by the upstream gates.

**S5 reading:** the eff band buys +0.21 PF and +0.19% median across the board without
damaging any year (2022 1.227 → 1.363). Remaining gradients: K's own interior favors
26-50 (1.9) over 16-25 (1.6); dist [−25,−15) at 2.025 and speed [−5,−3) at 1.933 are
still live; the price sweet spot $1-5 persists (2.16/1.95) with $5-20 fading to 1.48.
The cell is BROAD (2,786 syms / 75 trips/day) and unconcentrated (0.5%) — no lottery.

## S6 (2026-07-28) — stack 6: + `dist from 20m high < −10%`

**Spec: volat≥40bp × speed<−2% × vr<8 × tcr<8 × K∈[16,50] × eff∈[−0.5,−0.3) ×
dist<−10%. n=91,440 / win 70.6 / PF 1.886 / avg +1.127% / MEDIAN +1.94% / +$10.3M.**
Breadth: 57 trips/day, 2,352 syms, 1,602 days, top-3 tkd 0.6%. Worst year 1.419.

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 13,833 | 74.9 | 3.482 | 1.920 | 2.300 | 2,655,750 |
| 2021 | 13,488 | 74.0 | 2.637 | 1.339 | 1.877 | 1,806,394 |
| 2022 | 9,046 | 68.4 | 1.419 | 0.618 | 1.667 | 559,491 |
| 2023 | 11,750 | 68.6 | 1.505 | 0.787 | 1.819 | 924,769 |
| 2024 | 17,088 | 70.4 | 1.953 | 1.226 | 2.034 | 2,094,673 |
| 2025 | 18,990 | 68.2 | 1.598 | 0.880 | 1.826 | 1,670,821 |
| 2026 | 7,245 | 68.2 | 1.456 | 0.824 | 2.038 | 597,062 |

**⭐ The un-fadeable boundary refines to −35%** (fine bands inside <−10%):

| d20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−15,−10)% | 47,422 | 69.9 | 1.827 | 0.855 | 1.621 | 4,054,482 |
| [−20,−15)% | 25,910 | 70.6 | 1.953 | 1.219 | 2.149 | 3,158,710 |
| [−25,−20)% | 11,092 | 73.0 | **2.171** | 1.716 | 2.765 | 1,902,890 |
| [−35,−25)% | 6,127 | 72.6 | 1.909 | 2.004 | 3.619 | 1,227,831 |
| **<−35%** | 889 | 61.8 | **0.922** | −0.393 | 3.325 | −34,954 |

| speed | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−3,−2)% | 32,075 | 70.4 | 1.802 | 0.835 | 1.675 | 2,678,179 |
| [−5,−3)% | 37,670 | 71.1 | **1.994** | 1.151 | 1.953 | 4,334,102 |
| [−10,−5)% | 19,197 | 70.0 | 1.877 | 1.442 | 2.431 | 2,769,078 |
| <−10% | 2,498 | 69.3 | 1.673 | 2.112 | 4.010 | 527,601 |

| K fine | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 16-25 | 25,631 | 69.0 | 1.627 | 0.875 | 1.833 | 2,243,195 |
| 26-35 | 28,996 | 70.8 | 1.990 | 1.201 | 1.963 | 3,482,600 |
| 36-50 | 36,813 | 71.4 | 2.009 | 1.245 | 1.994 | 4,583,165 |

| volat | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 8,691 | 71.8 | 2.046 | 0.924 | 1.493 | 803,127 |
| [60,80) | 24,677 | 70.1 | 1.925 | 0.927 | 1.656 | 2,288,029 |
| [80,120) | 36,915 | 70.3 | 1.911 | 1.115 | 1.987 | 4,114,795 |
| [120,200) | 18,905 | 71.1 | 1.856 | 1.455 | 2.640 | 2,750,305 |
| ≥200bp | 2,252 | 69.3 | 1.529 | 1.566 | 3.990 | 352,704 |

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 15,544 | 70.3 | 1.649 | 1.023 | 2.058 | 1,589,797 |
| [−10,−5)% | 6,065 | 71.2 | 2.179 | 1.258 | 1.824 | 762,882 |
| [−5,0)% | 6,123 | 66.0 | 1.615 | 0.838 | 1.681 | 512,926 |
| [0,10)% | 12,788 | 71.1 | 1.953 | 1.140 | 1.896 | 1,457,267 |
| [10,30)% | 18,422 | 71.7 | 2.104 | 1.186 | 1.863 | 2,184,638 |
| ≥30% | 32,498 | 70.5 | 1.905 | 1.170 | 2.025 | 3,801,451 |

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$1 | 26,194 | 69.9 | 1.939 | 1.251 | 2.043 | 3,275,778 |
| $1-2 | 16,895 | 72.3 | **2.186** | 1.413 | 2.222 | 2,387,578 |
| $2-5 | 25,677 | 70.9 | 2.037 | 1.146 | 1.826 | 2,942,359 |
| $5-20 | 18,486 | 69.6 | 1.514 | 0.753 | 1.785 | 1,392,331 |
| $20-100 | 3,941 | 69.4 | 1.479 | 0.692 | 1.724 | 272,788 |
| ≥$100 | 247 | 84.2 | 2.641 | 1.544 | 2.345 | 38,125 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 34,137 | 70.1 | 1.817 | 0.990 | 1.803 | 3,379,827 |
| [−0.5,−0.1)% | 39,803 | 70.8 | 1.901 | 1.119 | 1.946 | 4,455,944 |
| [−1,−0.5)% | 12,241 | 70.5 | 1.902 | 1.261 | 2.128 | 1,543,679 |
| <−1% | 5,259 | 71.3 | 2.098 | 1.767 | 2.619 | 929,510 |

Exit mix: target 90,976 @ 1.929 med +1.956%; moc 464 @ 0.075 avg −5.40% (−$250k = 2.3%
of gross).

**S6 reading:** the dist floor buys +0.08 PF and +0.31% median. The deep tail's
un-fadeable boundary refines inward to **<−35%** (0.922, n=889 — small but the only
negative bucket left in ANY table). K interior still favors 26-50; speed still peaks
[−5,−3); the price ridge $1-5 persists. The stack is running out of levers with big
spreads — remaining candidates are trims (<−35% dist cut, 16-25 K trim, $5+ price
question) rather than new axes. ⏭ Validation next: mc=1 + K-gate + acceptance-stop
rerun of this cell; distinct-leg count vs adds; cost model.

## S7 (2026-07-28) — stack 7: dist band [−35%, −10%) — ⭐ THE SAMPLER SPEC

**n=90,551 / win 70.6 / PF 1.924 / avg +1.142% / MEDIAN +1.935% / +$10.3M.** Breadth
56.5 trips/day, 2,349 syms, top-3 tkd 0.6%. Removing the 889-trip <−35% tail cleaned
the whole grid: **every bucket of every lever table is now ≥ 1.41.**

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 13,764 | 74.9 | 3.507 | 1.912 | 2.291 | 2,631,347 |
| 2021 | 13,447 | 74.0 | 2.638 | 1.338 | 1.877 | 1,799,148 |
| 2022 | 9,017 | 68.3 | **1.410** | 0.607 | 1.662 | 547,015 |
| 2023 | 11,601 | 68.8 | 1.578 | 0.854 | 1.824 | 990,548 |
| 2024 | 16,890 | 70.5 | 1.974 | 1.221 | 2.023 | 2,061,825 |
| 2025 | 18,698 | 68.3 | 1.609 | 0.879 | 1.811 | 1,642,789 |
| 2026 | 7,134 | 68.4 | 1.567 | 0.941 | 2.035 | 671,241 |

⭐ The trim RESCUED the extreme buckets that looked capped: volat ≥200bp 1.529 → **2.098**
and speed <−10% 1.673 → **2.103** — the earlier "ceilings" were <−35% disasters wearing
volat/speed clothing. Post-trim, MORE extreme = MORE edge on every axis (K 36-50 2.069,
speed <−10% 2.10, volat ≥200 2.10) with no inversion anywhere in range. Exit mix:
target 90,099 @ 1.970 med +1.952%; moc 452 @ 0.056 (−$253k = 2.3% of gross).

### S7c (2026-07-28) — the commission test: 1¢/share round trip (½¢ per side)

Fee as % of a $10k clip = `0.01 / raw_price` (−2% at $0.50, −1% at $1, −5bp at $20).
TradeZero context: free non-marketable limits only ≥$1, so this flat-1¢ model is a
STRESS test for the ≥$1 book and roughly the floor for sub-$1.

| px | n | avg gross% | avg net% | med gross% | med net% | win net | pf net | $ net |
|---|---|---|---|---|---|---|---|---|
| <$0.5 | 15,636 | 1.340 | **−6.544** | 2.127 | −3.230 | 24.9 | 0.086 | −10,232,610 |
| $0.5-1 | 10,250 | 1.193 | **−0.218** | 1.920 | 0.527 | 55.4 | 0.881 | −223,426 |
| $1-2 | 16,722 | 1.415 | 0.712 | 2.217 | 1.516 | 67.0 | 1.518 | 1,191,322 |
| $2-5 | 25,437 | 1.122 | 0.787 | 1.809 | 1.464 | 67.7 | 1.659 | 2,002,110 |
| $5-20 | 18,332 | 0.817 | 0.695 | 1.794 | 1.663 | 68.9 | 1.486 | 1,273,717 |
| $20-100 | 3,927 | 0.684 | 0.650 | 1.723 | 1.689 | 68.9 | 1.445 | 255,244 |
| ≥$100 | 247 | 1.544 | 1.536 | 2.345 | 2.337 | 84.2 | 2.632 | 37,950 |

**⭐ VERDICT: sub-$1 is DEAD at any per-share commission** (<$0.50 = −6.5%/trip net,
the fee is 2-4× the edge; $0.5-1 marginally negative). **The ≥$1 book survives the
stress test whole: n=64,665 / win 68.0 / PF 1.557 / avg +0.736% / median +1.555% /
+$4.76M net of fees.** Net-of-fee year audit (≥$1): 2020 2.666 / 2021 2.228 / **2022
1.061 / 2023 1.105** / 2024 1.525 / 2025 1.316 / 2026 1.302 — the thin years get THIN
(1.06-1.11) but stay positive; medians stay +1.2-1.9% every year. ⚠ Slippage/spread
still unmodeled — fills are next-bar vwap; the limit-order question is production work.

### S7d (2026-07-28) — the TIERED commission test: 0.2¢/side (IBKR tiered >300k sh/mo) = 0.4¢/share RT

| px | n | avg gross% | avg net% | med net% | win net | pf net | $ net |
|---|---|---|---|---|---|---|---|
| <$0.25 | 8,215 | 1.155 | **−3.827** | −1.902 | 33.0 | 0.177 | −3,144,250 |
| $0.25-0.5 | 7,421 | 1.545 | 0.415 | 1.171 | 62.4 | 1.265 | 308,176 |
| $0.5-1 | 10,250 | 1.193 | **0.628** | 1.344 | 64.1 | **1.421** | 644,154 |
| $1-2 | 16,722 | 1.415 | 1.134 | 1.932 | 70.7 | 1.911 | 1,896,674 |
| $2-5 | 25,437 | 1.122 | 0.988 | 1.676 | 69.8 | 1.874 | 2,514,005 |
| $5-20 | 18,332 | 0.817 | 0.768 | 1.741 | 69.4 | 1.545 | 1,408,101 |
| $20-100 | 3,927 | 0.684 | 0.670 | 1.706 | 69.2 | 1.461 | 263,156 |
| ≥$100 | 247 | 1.544 | 1.541 | 2.342 | 84.2 | 2.637 | 38,055 |

Floor sweep (net PF / $): ≥$0.25 → 1.653/$7.07M · ≥$0.5 → 1.700/$6.76M · **≥$1 →
1.752/$6.12M** · ≥$2 → 1.698/$4.22M. Net-of-fee year audit (≥$0.5): 2020 2.978 / 2021
2.407 / **2022 1.170 / 2023 1.202** / 2024 1.723 / 2025 1.440 / 2026 1.459.

**Reading:** tiered pricing revives the $0.5-1 band (0.88 → 1.42, +0.63%/trip net) and
even $0.25-0.5 goes positive-thin (1.265); only <$0.25 stays structurally dead (fee
1.6-4%+ RT). The volume tier is self-fulfilling: the ≥$0.5 book at $10k/trip trades
**median 9.4M shares/month at mc=0** (p90 14.5M) — even a 5-10% mc=1 book clears 300k
easily. ⚠ Commission-only model: IBKR tiered adds exchange/clearing/regulatory
pass-throughs (remove-liquidity fees can exceed the commission on thin names; passive
adds earn rebates) and sub-$1 names may price under the %-of-value cap — the exact
all-in sub-$1 schedule needs a direct check (S8b flagged IBKR %-caps before). Practical
floor at tiered: **$0.50 (PF 1.700) or $1 (PF 1.752)**, decided by the all-in check.

## S8 (2026-07-28) — probe: K floor raised to 26 → [26,50]

**n=65,082 / win 71.3 / PF 2.051 / avg +1.243% / MEDIAN +1.974% / +$8.09M.** Breadth
41.5 trips/day, 2,198 syms, top-3 tkd 0.7%. Net-of-fee (≥$1, n=46,294): marketable
bound **1.669**, tiered mix **1.875**.

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 10,208 | 75.7 | 3.875 | 2.032 | 2.348 | 2,074,333 |
| 2021 | 9,844 | 75.0 | 2.838 | 1.437 | 1.962 | 1,414,314 |
| 2022 | 6,440 | 68.0 | **1.361** | 0.553 | 1.681 | 356,064 |
| 2023 | 8,199 | 69.4 | 1.727 | 0.986 | 1.867 | 808,529 |
| 2024 | 12,126 | 70.8 | 2.091 | 1.316 | 2.026 | 1,596,356 |
| 2025 | 13,191 | 69.0 | 1.733 | 1.002 | 1.825 | 1,321,097 |
| 2026 | 5,074 | 69.1 | 1.617 | 1.026 | 2.103 | 520,498 |

| K fine | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 26-30 | 14,580 | 69.8 | 1.901 | 1.121 | 1.897 | 1,634,153 |
| 31-35 | 14,168 | 72.0 | 2.174 | 1.311 | 2.010 | 1,857,156 |
| 36-42 | 18,030 | 71.8 | 2.125 | 1.291 | 1.998 | 2,327,921 |
| 43-50 | 18,304 | 71.4 | 2.018 | 1.241 | 1.981 | 2,271,961 |

| speed | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−3,−2)% | 23,123 | 70.6 | 1.855 | 0.868 | 1.681 | 2,006,241 |
| [−5,−3)% | 27,101 | 71.8 | 2.084 | 1.215 | 1.982 | 3,293,867 |
| [−10,−5)% | 13,378 | 71.3 | 2.158 | 1.717 | 2.560 | 2,296,631 |
| <−10% | 1,480 | 72.4 | **2.497** | 3.341 | 4.744 | 494,452 |

| dist from 20m high | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−15,−10)% | 33,860 | 70.6 | 1.941 | 0.931 | 1.668 | 3,150,680 |
| [−20,−15)% | 18,685 | 70.8 | 2.066 | 1.303 | 2.133 | 2,433,800 |
| [−25,−20)% | 8,049 | 73.7 | **2.334** | 1.837 | 2.832 | 1,478,806 |
| [−35,−25)% | 4,488 | 73.6 | 2.074 | 2.290 | 3.781 | 1,027,905 |

| volat | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [40,60)bp | 6,809 | 71.9 | 2.041 | 0.928 | 1.511 | 632,072 |
| [60,80) | 17,894 | 70.6 | 2.019 | 0.995 | 1.705 | 1,779,976 |
| [80,120) | 26,131 | 71.1 | 2.043 | 1.223 | 2.041 | 3,197,040 |
| [120,200) | 13,198 | 71.9 | 2.040 | 1.631 | 2.699 | 2,152,284 |
| ≥200bp | 1,050 | 75.4 | **2.591** | 3.141 | 4.657 | 329,818 |

| eff fine [−0.5,−0.3) | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.5,−0.45) | 10,118 | 73.8 | **2.477** | 1.516 | 2.136 | 1,534,074 |
| [−0.45,−0.4) | 15,537 | 71.8 | 2.123 | 1.271 | 1.994 | 1,975,334 |
| [−0.4,−0.35) | 19,140 | 71.4 | 2.054 | 1.273 | 2.000 | 2,437,213 |
| [−0.35,−0.3) | 20,287 | 69.5 | 1.829 | 1.057 | 1.854 | 2,144,570 |

| change from 20m low | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−0.1,0)% | 24,697 | 70.9 | 1.934 | 1.074 | 1.832 | 2,652,294 |
| [−0.5,−0.1)% | 28,477 | 71.4 | 2.058 | 1.230 | 1.987 | 3,502,905 |
| [−1,−0.5)% | 8,474 | 71.5 | 2.131 | 1.440 | 2.203 | 1,220,085 |
| <−1% | 3,434 | 72.5 | **2.536** | 2.085 | 2.715 | 715,907 |

| price | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <$0.5 | 11,356 | 71.2 | 2.148 | 1.414 | 2.127 | 1,605,498 |
| $0.5-1 | 7,432 | 69.8 | 2.073 | 1.314 | 1.975 | 976,542 |
| $1-2 | 11,607 | 72.6 | **2.332** | 1.522 | 2.274 | 1,766,944 |
| $2-5 | 18,062 | 71.9 | 2.206 | 1.241 | 1.863 | 2,241,617 |
| $5-20 | 13,477 | 70.5 | 1.708 | 0.930 | 1.862 | 1,253,848 |
| ≥$20 | 3,148 | 69.9 | 1.573 | 0.784 | 1.764 | 246,742 |

| chg_1d | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <−10% | 11,929 | 71.5 | 1.885 | 1.237 | 2.142 | 1,475,104 |
| [−10,−5)% | 4,680 | 71.8 | **2.366** | 1.341 | 1.843 | 627,733 |
| [−5,0)% | 4,569 | 67.0 | 1.756 | 0.964 | 1.724 | 440,384 |
| [0,10)% | 8,814 | 71.0 | 2.018 | 1.173 | 1.864 | 1,034,026 |
| [10,30)% | 12,688 | 72.0 | 2.174 | 1.234 | 1.863 | 1,565,373 |
| ≥30% | 22,402 | 71.6 | 2.117 | 1.316 | 2.080 | 2,948,570 |

| VOLUME 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 1,373 | 72.5 | 2.374 | 1.445 | 2.192 | 198,380 |
| [0.5,1)× | 17,175 | 71.5 | 2.000 | 1.189 | 2.027 | 2,041,972 |
| [1,2)× | 33,623 | 71.5 | 2.024 | 1.190 | 1.926 | 4,000,432 |
| [2,4)× | 11,952 | 70.4 | 2.107 | 1.352 | 1.961 | 1,615,901 |
| [4,8)× | 959 | 70.6 | 2.628 | 2.445 | 3.104 | 234,505 |

| TC 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <0.5× | 834 | 72.1 | 1.674 | 0.912 | 1.909 | 76,083 |
| [0.5,1)× | 17,220 | 71.5 | 2.135 | 1.303 | 2.074 | 2,242,917 |
| [1,2)× | 36,886 | 71.5 | 2.000 | 1.167 | 1.923 | 4,305,172 |
| [2,4)× | 9,487 | 70.2 | 2.123 | 1.405 | 1.970 | 1,332,780 |
| [4,8)× | 655 | 69.3 | 2.166 | 2.049 | 2.700 | 134,239 |

Exit mix: target 64,814 @ 2.092 med +1.989%; moc 268 @ 0.074 avg −5.40% (−$145k =
1.7% of gross — down from 9% at S1).

**S8 trade-off vs S7:** +0.13 PF / +0.10% avg for −28% n and −$2.2M net — and **2022 is
the one year that WEAKENS (1.410 → 1.361)**: the toughest year's edge partly lives in
the K 16-25 band the raise discards. Interior now flat (31-50 ≈ 2.0-2.17, 26-30 at
1.90). Judgment: the raise is defensible but not free — it trades breadth and 2022
robustness for concentration-year magnitude. DECISION PENDING (user).

## S9 (2026-07-28) — ⭐ THE FROZEN SAMPLER SPEC: ceilings dropped, stops deferred-off

**The sustained-dip test (user): under S8's conditioning, are the toxic extremes still
toxic? NO — they no longer exist.** S8-minus-ceilings deep tails:

| VOLUME 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <2× | 52,180 | 71.5 | 2.021 | 1.194 | 1.964 | 6,231,508 |
| [2,4)× | 11,964 | 70.3 | 2.098 | 1.345 | 1.959 | 1,609,703 |
| [4,8)× | 963 | 70.7 | 2.654 | 2.475 | 3.123 | 238,319 |
| [8,16)× | 37 | 70.3 | 6.200 | 9.412 | 6.718 | 34,823 |

| TC 1m/20m | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| <2× | 54,943 | 71.5 | 2.036 | 1.206 | 1.969 | 6,626,206 |
| [2,4)× | 9,490 | 70.2 | 2.123 | 1.405 | 1.971 | 1,333,522 |
| [4,8)× | 662 | 69.6 | 2.271 | 2.210 | 2.763 | 146,285 |
| [8,16)× | 49 | 34.7 | 1.376 | 1.702 | −5.121 | 8,340 |

| speed deep tail | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| [−5,−2)% | 50,245 | 71.2 | 1.980 | 1.053 | 1.839 | 5,289,262 |
| [−10,−5)% | 13,387 | 71.3 | 2.156 | 1.715 | 2.561 | 2,296,212 |
| [−15,−10)% | 1,292 | 72.6 | 2.353 | 3.076 | 4.647 | 397,413 |
| [−20,−15)% | 183 | 75.4 | 5.310 | 6.686 | 7.018 | 122,351 |
| <−20% | 37 | 45.9 | 1.613 | 2.464 | −0.117 | 9,115 |

The would-be-removed ≥8× population = **62 trips** (win 48.4, median −4.2%, years =
coin-toss). **The early-stack toxicity of loud/fast lows was a YOUNG-LEG phenomenon**
— by the 26th low of an established leg, another loud flush is exhaustion, not
information. Ceilings are provably vestigial → DROPPED. Stops: with the never-revert
tail at 268 trips / 0.4% / −$145k vs +$8.2M target profit, the acceptance stops have
almost nothing to save in this cell → **rerun runs stops OFF** (machinery stays in the
engine; a stops-on comparison is one flag-flip away if the mc=1 book shows tail pain).

**⭐ THE FROZEN SAMPLER SPEC (5 conditions + price floor):**
`volat_20m >= 0.0040 AND speed_1m < -0.02 AND lows_since_first_low IN [26,50] AND
eff_20m IN [-0.5,-0.3) AND dist_20m_high IN (-0.35,-0.10]` + **raw price ≥ $1**.

**n=65,144 / win 71.3 / PF 2.051 / avg +1.246% / MEDIAN +1.973% / +$8.11M** (gross,
all prices; 41.5 trips/day, 2,198 syms, top-3 tkd 0.7%).

| yr | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| 2020 | 10,219 | 75.7 | 3.840 | 2.023 | 2.345 | 2,067,636 |
| 2021 | 9,877 | 75.0 | 2.868 | 1.467 | 1.963 | 1,449,239 |
| 2022 | 6,440 | 68.0 | 1.361 | 0.553 | 1.681 | 356,064 |
| 2023 | 8,199 | 69.4 | 1.727 | 0.986 | 1.867 | 808,529 |
| 2024 | 12,126 | 70.8 | 2.091 | 1.316 | 2.026 | 1,596,356 |
| 2025 | 13,203 | 69.0 | 1.725 | 0.994 | 1.824 | 1,312,563 |
| 2026 | 5,080 | 69.1 | 1.621 | 1.031 | 2.108 | 523,967 |

**The ≥$1 production book, net of fees (n=46,311):**

| schedule | avg net% | med net% | pf net | $ net |
|---|---|---|---|---|
| 1.0¢/sh RT (marketable bound) | 0.852 | 1.610 | 1.674 | 3,947,996 |
| 0.4¢/sh RT (tiered mix) | 1.059 | 1.812 | **1.881** | 4,902,996 |

≥$1 year audit at the tiered mix: 2020 3.360 / 2021 2.765 / **2022 1.201 / 2023 1.324**
/ 2024 1.763 / 2025 1.627 / 2026 1.502 — positive every year net of fees; the passive
(limit-lane) bound sits near the gross numbers.

## S10 (2026-07-28) — + `|eff_10m| >= 0.15` → THE SPEC v1.1

eff_10m breakdown (in S9): same hump as e20, peak [−0.6,−0.45) @ 2.323; the FLAT 10m
tape is the weak zone ([−0.15,0) = 1.574; [0,0.15) = 0.971, the only sub-1 bucket in
the spec — the 10m already turned = late print). e20×e10 cross-tab: inside the e20 band
deeper e10 is better (2.15/2.01/1.48); at e20<−0.5 it INVERTS (deep 1.56, flat 2.25) —
the SHORTER horizon should show exhaustion relative to the longer (the K×eff grammar
again). User: floor `|eff_10m| >= 0.15` (cuts the flat middle, n −3,499).

**SPEC v1.1: volat≥40bp × speed<−2% × K∈[26,50] × eff_20m∈[−0.5,−0.3) × |eff_10m|≥0.15
× dist∈(−35,−10] + ≥$1. n=61,645 / win 71.6 / PF 2.088 / avg +1.281% / MEDIAN +2.01% /
+$7.90M gross.** Years: 2020 4.036 / 2021 2.859 / 2022 1.368 / 2023 1.729 / 2024 2.185
/ 2025 1.748 / 2026 1.641. **≥$1 book (n=43,894) net: PF 1.707 @1¢ marketable / 1.916
@0.4¢ tiered, med net +1.64/+1.84%.**

## S11 (2026-07-28) — session lows vs higher-low legs (user's closing question)

| session position | n | win | pf | avg% | med% | net |
|---|---|---|---|---|---|---|
| NEW session low (breach_lo_sess=0) | 37,373 (61%) | 70.1 | 1.986 | 1.200 | 1.898 | 4,484,865 |
| off-session-low (>20m ago, mechanically) | 21,992 | 73.9 | 2.232 | 1.380 | 2.142 | 3,034,874 |
| never broke a session low | 2,280 | 74.2 | 2.517 | 1.647 | 2.415 | 375,422 |

Both prior intuitions confirmed at once: the MAJORITY (61%) are new session lows (deep
pullbacks ⇒ expected), AND they perform worse (the V6/MaxRider pattern) — though
"worse" = PF 1.99 here. Structure note: the 1-1200-bar buckets are EMPTY by
construction (a session low <20m old IS the 20m min ⇒ every new 20m low is a session
low) — so off-low ≡ **higher-low second legs** (flushed earlier, bounced, new 10-35%
leg above the morning low); "never" = the open print held as the day's low.

**⭐ BUT the off-low premium is a GOOD-YEAR phenomenon** (by year, sesslow vs off):
2020 3.35/5.75 · 2021 2.89/2.82 · **2022 1.65/1.04** · 2023 1.72/1.75 · 2024 1.97/2.60
· 2025 1.54/2.10 · 2026 1.59/1.72. In the bear year the higher-low retest collapses to
break-even while session lows hold 1.65 — the session-low majority is the REGIME-ROBUST
half. Verdict: keep both; no spec change (cutting session lows = 2020-fitting).

## S12 (2026-07-28) — the depth×volume DISPROPORTION grid (user's closing experiment)

Hypothesis: 1m flush depth and volume intensity should be rank-correlated; the BREAKS in
that pattern should be the A+ trades (deep flush on quiet volume). **Spearman(depth, vr)
= 0.4005** — correlated, loosely; the pattern-break corners are 4-5× depleted (deep+quiet
n=1,645, shallow+loud n=1,260 vs concordant 6-7k). Quartile bounds: depth 2.67/3.50/4.82%,
vr 0.96/1.33/1.86×.

**PF grid** (dq4 = deepest 1m flush; vq1 = quietest):

| dq \ vq | 1 quiet | 2 | 3 | 4 loud |
|---|---|---|---|---|
| 1 shallow | 1.858 | 1.960 | 1.963 | **1.551** |
| 2 | 2.113 | 1.971 | 2.143 | 1.878 |
| 3 | 2.011 | 2.337 | 2.051 | 2.059 |
| 4 deep | 2.113 | 2.157 | 1.919 | **2.519** |

**Median-ret grid (%)** — quiet wins EVERY row, monotone:

| dq \ vq | 1 quiet | 2 | 3 | 4 loud |
|---|---|---|---|---|
| 1 | 1.80 | 1.67 | 1.55 | 1.38 |
| 2 | 2.05 | 1.90 | 1.71 | 1.70 |
| 3 | 2.53 | 2.24 | 2.03 | 1.80 |
| 4 | **3.20** | 2.47 | 2.63 | 2.72 |

**Verdict — the hypothesis is confirmed in MEDIANS and mirrored in the anti-break:**
(a) deep+quiet = the best TYPICAL trade (+3.20% median; a 5% 1m drop without the volume
to justify it is the purest mispricing); (b) shallow+loud = the grid's WORST cell (1.551
— absorbed-but-not-exhausted selling); the DISPROPORTION carries the information both
ways. (c) Dollar-weighted PF crowns deep+LOUD (2.519) — volume capitulation carries the
fat right tail, while deep+quiet's thin tape occasionally produces ugly losers (PF 2.11
vs its own +3.2% median). Deep+quiet = best median; deep+loud = best tail. No spec
change today — both deep cells live inside SPEC v1.1; a size-by-cell overlay (bigger
clips on dq4, either vq wing) is future book-construction work.

**⏭ THE PLANNED RERUN — SUPERSEDED by S19 (2026-07-29):** the whole stack (and more:
SPEC v1.2) is now BAKED into the engine with stops off by default, and prev-close
gating was rejected in favor of post-hoc `entry_px/adj_ratio >= 1` (S19). The
full-universe production rerun is now just `bin/Release/net10.0/TradingEdge.FlushFader
-o <dir>` (~80 min on all tkds; 75 s on the `FF_CANDIDATE_TABLE=flushfader_spec_v11_tkds`
restricted table). Still pending from this list: (1) mc=1 + `--min-lows-into-leg 26`
book run; (2) distinct-leg count vs adds; (3) the TradeZero limit-fill question.

⏭ **Future runtime optimization (user, 2026-07-28): per-tkd volatility prefilter.**
Precompute `(ticker, date, max intraday volat_20m)` once, then skip ticker-days whose max
never clears the run's `--min-volat-20m` floor. ⚠ Lookahead discipline: the day-max is
day-D data, so this is legal ONLY as a speed shortcut welded to an equal-or-higher
signal-time volat floor (day-max ≥ signal-bar volat ⇒ provably drops zero qualifying
signals, bit-identical output). As a STANDALONE day filter it would be a "day got
volatile later" lookahead — the exact 2026-07-16 bug class. V6 precedent: the ATR floor
was load-bearing (sub-0.004 dead-below-costs) — most of the 328k tkds are dead weight.

---

## S13 — the exit-window sweep off the aux-high marks (2026-07-29)

The whole point of keeping the aux marks: test alternative exits in SQL without a
re-run. All tables on **SPEC v1.1 + raw ≥$1 (n=43,894)**. First, validation: the
`aux_hi_300` counterfactual reproduces the live engine to the third decimal (PF 2.068
vs 2.065, identical win/avg/median) — mark machinery confirmed end-to-end.

**⚠ Censoring caveat discovered:** positions retire once exited AND `fwd_vwap_1200`
fills, so aux marks only observe ~max(actual exit, entry+20m). The PURE "exit at 10m/20m
high, however long it takes" strategy is NOT evaluable from this parquet (hit% within
the window: 10m = 41%, 20m = 10%). What IS exact: **"target N-bar high, time-stop at
entry+20m"** — take the mark only when `aux_sec ≤ entry_sec+1200`, else exit at
`fwd_vwap_1200` (always recorded).

| exit | hit% | win | PF | avg % | med % | p25 % | p75 % |
|---|---|---|---|---|---|---|---|
| 2m high, ts 20m | 94.6 | 70.3 | 1.807 | 0.67 | 1.16 | −0.37 | 2.38 |
| 5m high, ts 20m | 73.5 | 69.6 | 2.028 | 1.13 | 1.75 | −0.66 | 3.60 |
| 10m high, ts 20m | 40.0 | 64.4 | 1.712 | 1.07 | 1.62 | −1.42 | 4.30 |
| 20m high, ts 20m | 9.8 | 60.8 | 1.595 | 0.97 | 1.16 | −1.80 | 4.08 |
| hold 20m flat (`fwd_1200`) | — | 60.7 | 1.544 | 0.89 | 1.14 | −1.81 | 3.91 |
| **ACTUAL (5m high, no ts)** | 99.7 | **71.9** | **2.065** | **1.23** | **1.97** | −0.43 | 3.78 |

(Full-day pure exits, also exact since hit ≈ 100%: 2m high = 1.813 / +0.69% — strictly
worse than 5m at every stat.)

**Verdict — the 5m high is the sweet spot and the current exit is UNBEATEN:**
- The hump peaks exactly at 5m: 2m exits too early (gives up ~45% of the avg), 10m/20m
  degrade toward raw drift (1.71 → 1.60 → 1.54 = hold-20m-flat).
- The ratcheting 5m-high target is doing real work over pure drift: +0.34%/trip and
  +0.52 PF over holding 20m flat, +11pp win rate.
- Even the 20m time-stop HURTS the 5m exit (2.028 vs 2.065): the 26.5% of trades that
  take >20m to print a 5m high are still worth waiting for. Echoes the V6/MaxFlyer
  lesson: exits lose to patience; selection does the work.

## S14 — rng_300/rng_20m (front-loadedness) + distance-from-5m-high (2026-07-29)

Same book (SPEC v1.1 + ≥$1). `rng_300/rng_20m` = share of the 20m log-range printed in
the last 5m (median 0.46 on the book — these flushes are front-loaded by construction).

| rng_300/rng_20m | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <0.25 | 1,938 | 74.0 | 2.362 | 1.20 | 1.94 |
| [0.25,0.35) | 7,903 | 72.2 | 2.084 | 1.07 | 1.71 |
| [0.35,0.45) | 11,134 | 71.5 | 2.303 | 1.30 | 1.98 |
| [0.45,0.55) | 9,849 | 71.8 | 2.005 | 1.21 | 1.96 |
| [0.55,0.65) | 6,581 | 73.5 | 2.131 | 1.34 | 2.19 |
| [0.65,0.80) | 4,830 | 70.4 | 1.662 | 1.08 | 2.14 |
| ≥0.80 | 1,659 | 69.3 | 2.098 | 1.68 | 2.41 |

**No lever here** — PF wobbles 1.66–2.36 with no monotone structure; the spec's
speed × volat × eff stack has already consumed whatever front-loadedness knew. Medians
drift up with the ratio (1.94 → 2.41) but tails pay for it; the one soft cell
([0.65,0.80) = 1.66) is not edge-adjacent enough to gate on.

Distance from 5m high at signal = `exit_chan_hi/signal_vwap − 1` = how much reversion
the trade is asking for (median 7.5% on the book — these are violent flushes):

| dist from 5m high | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <4% | 2,348 | 70.2 | 1.858 | 0.68 | 1.23 |
| [4,5) | 4,396 | 70.8 | 1.973 | 0.84 | 1.50 |
| [5,6) | 5,828 | 70.4 | 2.209 | 1.01 | 1.65 |
| [6,7) | 6,322 | 72.5 | 2.171 | 1.08 | 1.82 |
| [7,8) | 5,529 | 72.3 | 2.149 | 1.16 | 1.95 |
| [8,10) | 8,158 | 72.8 | 2.092 | 1.27 | 2.24 |
| [10,13) | 6,240 | 73.8 | 2.249 | 1.63 | 2.68 |
| [13,17) | 3,033 | 73.8 | 2.175 | 1.92 | 3.26 |
| ≥17% | 2,040 | 65.3 | 1.524 | 1.59 | 3.42 |

**The dist-from-20m-high shape reproduced at the 5m horizon:** avg/median rise
monotonically with the ask (0.68% → 1.92% avg, 1.23% → 3.26% med) — the more reversion
being asked, the more gets paid — until the wall: **≥17% breaks** (win 65.3, PF 1.52,
the only sub-1.9 cell). Same grammar as the −35% un-fadeable boundary, one horizon
down. A <17% ceiling would shave 4.6% of trips at PF 1.52 (positive, just weakest) —
optional polish, not urgent; noted for the spec-freeze discussion.

## S15 — spec trips per ticker-day + the mc=1 preview (2026-07-29)

SPEC v1.1 + ≥$1 (43,894 trips) collapses to **5,560 ticker-days** out of the sampler's
263,805 (2.1%). Per-tkd signal counts: **597 tkds (10.7%) have exactly ONE spec trip**;
1,973 have 2–5; 2,666 have 6–20; 324 have >20 (max 66); median 6, mean 7.9. The
multiplicity is mc=0 same-flush adds — the K∈[26,50] band alone admits up to 25
signals per leg.

First-signal-per-tkd (the cheap mc=1 proxy) vs the adds:

| cut | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| first signal of tkd | 5,560 | 70.9 | 1.954 | 1.04 | 1.75 |
| later adds | 38,334 | 72.0 | 2.080 | 1.26 | 2.01 |

**First-signal compression is mild (PF 2.088 → 1.954, −6%)** — the PlungeRider mc=1
pattern (0–12% compression) repeats: the first qualifying signal already carries the
edge; adds are slightly better (deeper into the same flush) but nothing depends on
averaging down. ⚠ This proxies per-TKD-first; the real mc=1 + K-gate book is one trade
per LEG (a tkd can have several legs) — the engine run remains the ground truth.

Book cadence per year (spec tkds / active days): 2020 916/232 (3.95/day) · 2021
1,209/229 (5.28) · 2022 605/208 (2.91) · 2023 574/229 (2.51) · 2024 900/240 (3.75) ·
2025 982/246 (3.99) · 2026 374/114 (3.28). **~2.5–5.3 candidate names per trading day,
active ~90% of days** — a genuinely tradable manual-or-algo cadence, thinnest exactly
in the thin-PF years (2022/23).

---

## S16 — the acceptance-stop A/B: stops are DEAD on this book (2026-07-29)

Method: restricted candidate table `flushfader_spec_v11_tkds` (the 5,560 SPEC v1.1
tkds) in trading.db — the engine now sweeps in **117 s instead of ~80 min** (47×).
Run: stops ON at engine defaults (vr≥8× | tcr≥8× | 1m pace <−1% on fresh entry-channel
lows), `--min-volat-20m 0.004`. Signal-set identity confirmed: exactly 43,894 spec
trips, bit-matched to the no-stop baseline on (symbol, date, signal_sec).

| cut | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| spec book, stops ON | 43,894 | 38.4 | 1.228 | 0.07 | −0.09 |
| spec book, stops OFF | 43,894 | 71.9 | 2.065 | 1.23 | 1.97 |
| stopped trades — stopped outcome | 41,148 | 34.3 | 0.311 | −0.23 | −0.12 |
| stopped trades — no-stop outcome | 41,148 | 70.0 | 1.817 | 1.01 | 1.76 |

Exit mix with stops ON: **speed_stop 93.7%** (41,131), target 6.2%, vol_stop 13 trips,
tc_stop 4, moc 3. Median time-to-stop = **2 seconds**.

**Verdict — self-refuting by construction:** the spec REQUIRES a ≥2%/1m flush at
entry, so the very next new low almost always prints at pace <−1% and the speed stop
fires instantly; the vol/tc arms never fire at all inside K∈[26,50] (the ≥8× lows are
young-leg phenomena the K band already excludes). The trades the stop "protects" win
70% for +1.01% without it. This is the empirical A/B behind the S9 stops-OFF verdict —
the acceptance-stop idea would need a threshold well past the entry speed (or a
pace-worse-than-entry form) to even be testable, and nothing here motivates trying.
V6's "stops destructive" lesson survives at 1s resolution.

## S17 — the last-seconds fade test: quiet tails REJECT the exhaustion form (2026-07-29)

User hypothesis: big 1m/20m volume where the LAST 1–10s run below the 1m per-second
rate = seller exhaustion → best fades. Engine gained `vol_5`/`vol_10` (SumMa 5/10,
schema now 95 cols) and the restricted universe re-ran stops-OFF in 130 s
(`spec_tkds_nostops`, 385,401 trips, spec book = 43,894 bit-identical). All ratios =
per-second averages: `(vol_N/N)/(vol_60/60)`; the signal bar itself = `bar_vol/(vol_60/60)`.

| last-N-s vs 1m rate | <0.5 | [0.5,0.75) | [0.75,1) | [1,1.25) | [1.25,1.5) | [1.5,2) | [2,3) | ≥3 |
|---|---|---|---|---|---|---|---|---|
| 1s (sig bar) PF* | 1.88 | 1.97 | 2.26/2.06 | — | — | 2.14 | 2.28 | 2.23/2.04 |
| 5s PF | 1.798 | 1.865 | 1.995 | 1.907 | 2.108 | **2.486** | 2.157 | 2.045 |
| 10s PF | **1.619** | **1.670** | 1.872 | 2.354 | 2.198 | 2.321 | 2.063 | 2.139 |
| 15s PF | 1.662/1.602 | (same buckets) | 2.064 | 1.969 | 2.318 | 2.226 | 2.201 | 2.003 |

*1s row uses its own finer edges (<0.25, [0.25,0.5), [0.5,0.75), [0.75,1), [1,1.5),
[1.5,2), [2,3), [3,5), ≥5) — full table in the session query; pattern identical.
Medians track PF: quiet tails 1.5–1.8%, the 1.5–2× band 2.1–2.2%.

**Verdict — the hypothesis INVERTS at every horizon (1s, 5s, 10s, 15s):** a new 20m
low printed on a QUIET final tape (<0.75× the minute rate) is the *worst* config
(PF 1.60–1.87, win 68–70) — the seller pausing, not finished. The best cells sit at
**1–2× the minute rate** (5s: 2.49 at [1.5,2); 10s: 2.35 at [1,1.25)) — the flush
still printing at full pace into the low, i.e. capitulation-in-progress, consistent
with S12 (deep+LOUD carries the dollar tail; shallow+quiet-signal weakest). Exhaustion
on this book is expressed in the eff grammar (e20×e10) and the depth-vs-volume
disproportion — NOT in a last-seconds volume fade-off. No spec change; the quiet-tail
zone is weak (1.6–1.8), not toxic, and partially overlaps the S12 deep+quiet median
story (quiet DAYS good, quiet final SECONDS bad — different objects).

## S17b — the tc twins (2026-07-29)

Engine gained `tc_5`/`tc_10` (schema 97 cols) + **acceptance stops now default OFF**
(`--vol-stop-ratio`/`--tc-stop-ratio` default Infinity, `--speed-stop-pct` default 0;
rerun bit-identical to the flag-spelled version). Same ratios as S17 on trade counts:
`(tc_N/N)/(tc_60/60)`.

| last-N-s tc vs 1m rate | <0.5 | [0.5,0.75) | [0.75,1) | [1,1.25) | [1.25,1.5) | [1.5,2) | [2,3) | ≥3 |
|---|---|---|---|---|---|---|---|---|
| 1s (sig bar) PF | 1.899 | 2.205 | 2.069 | 2.382 | 2.086 | 2.119 | 2.007 | 2.134 |
| 5s PF | 1.979 | 1.877 | 1.921 | 2.094 | 2.186 | 2.140 | 2.172 | 2.044 |
| 10s PF | **1.762** | 1.906 | 1.924 | 2.097 | 1.901 | **2.357** | 2.060 | 2.243 |
| 15s PF | **1.663** | 1.811 | 1.900 | 2.002 | **2.405** | 2.093 | 2.219 | 1.811 |

Win rates in the quiet tails: 10s <0.5 = 69.5, 15s <0.5 = 66.7 (vs book 71.9).

**Verdict — tc confirms the S17 inversion, at lower contrast than volume.** Quiet-tc
tails are the weakest cells everywhere ≥10s; the best cells again sit at 1–2× the
minute rate. But the spread is milder (5s tc: 1.88–2.19 vs 5s volume: 1.80–2.49) —
VOLUME is the sharper short-horizon discriminator. Reading: capitulation is a few
LARGE commitments — the volume rate captures the size of the final panic better than
the print count; tc fading only matters once the whole tape thins (10–15s). Still no
spec change — weak, not toxic — but if a tail gate is ever added, it should be a
volume-rate floor (last-5s ≥ ~0.75× the 1m rate), not tc.

## S18 — ⭐ SPEC v1.2: the last-10s volume-rate floor (2026-07-29)

User decision off S17: add **`(vol_10/10)/(vol_60/60) ≥ 0.75`** — the tape must still
be printing at ≥75% of the minute's pace through the low (no quiet-tail drift-downs).

```sql
volat_20m >= 0.0040 AND vwap_60_prev IS NOT NULL AND signal_vwap/vwap_60_prev-1 < -0.02
AND lows_since_first_low BETWEEN 26 AND 50
AND eff_20m >= -0.5 AND eff_20m < -0.3
AND signal_vwap/chan_hi-1 < -0.10 AND signal_vwap/chan_hi-1 >= -0.35
AND abs(eff_10m) >= 0.15
AND (vol_10/10.0)/(vol_60/60.0) >= 0.75      -- v1.2: last-10s volume-rate floor
-- production: + entry_px/adj_ratio >= 1
```

| book (≥$1) | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| SPEC v1.1 | 43,894 | 71.9 | 2.065 | 1.23 | 1.97 |
| **SPEC v1.2** | 37,322 | 72.4 | **2.153** | **1.29** | **2.02** |
| v1.2 net marketable 1.05¢/sh RT | 37,322 | 69.3 | 1.758 | 0.93 | 1.67 |

Year audit (kept vs cut): the gate improves EVERY year — the cut cell's PF is below
the kept cell's in all 7 years:

| yr | n v1.2 | PF v1.2 | avg % | med % | n cut | PF cut |
|---|---|---|---|---|---|---|
| 2020 | 6,306 | 3.798 | 2.02 | 2.39 | 1,155 | 3.549 |
| 2021 | 7,427 | 3.033 | 1.52 | 2.00 | 1,351 | 2.606 |
| 2022 | 3,676 | 1.301 | 0.47 | 1.67 | 707 | 1.254 |
| 2023 | 3,955 | 1.515 | 0.76 | 1.82 | 656 | 1.085 |
| 2024 | 6,394 | 2.220 | 1.38 | 2.12 | 1,055 | 1.239 |
| 2025 | 6,829 | 1.889 | 1.12 | 1.89 | 1,179 | 1.279 |
| 2026 | 2,735 | 1.682 | 1.10 | 2.15 | 469 | 1.403 |

Notes: (a) the gate's value concentrates 2023-2025 (cut PF 1.09–1.28 — near-dead
weight); in 2020/21 even quiet-tail lows bounced (cut 3.5/2.6), so it costs a little
wild-year P&L for regime robustness. (b) 15% of trips cut. (c) ⚠ v1.2 post-hoc needs
`vol_10` → run it off `spec_tkds_nostops/` (or any engine-v3 output), NOT the original
`base20_e1200_x300/` parquet. (d) The tkd list `flushfader_spec_v11_tkds` remains a
superset — v1.2 lives inside it.

## S19 — SPEC v1.2 BAKED INTO THE ENGINE + parity proof (2026-07-29)

Engine v4: the S18 stack is now first-class entry gates with production defaults —
`--max-speed-1m -0.02`, `--k-band-lo 26 --k-band-hi 50`, `--eff20-lo -0.5 --eff20-hi
-0.3`, `--min-abs-eff-10m 0.15`, `--dist-hi-lo -0.35 --dist-hi-hi -0.10`,
`--min-vol10-rate 0.75`, plus `--min-volat-20m` default 0.004. Every gate individually
disable-able; gate expressions mirror the recorded columns exactly. Banner prints the
full stack. A bare `TradingEdge.FlushFader` run is now the SPEC v1.2 reversal sampler.

Two convention decisions (both deliberate):
- **Cold `eff_20m` PASSES the band** (user) — a warm-channel flush whose 40-slot eff
  isn't yet computable is still a valid signal; 190 such trips exist on the restricted
  universe (gappy tapes). Cold `eff_10m`/volat/speed still FAIL their gates.
- **The ≥$1 cut stays POST-HOC on raw entry price** (user): `--min-prev-close`
  default stays 0. A prev-close gate was tried first (knowable pre-open, 5,560 →
  5,230 tkds) and REJECTED — this book buys flushes, so a name falling DOWN through
  $1 intraday would be dropped by prev-close exactly when it signals; conversely a
  sub-$1 name popping over $1 would be missed. Filter trips by `entry_px/adj_ratio
  >= 1` in SQL instead (the S7c fee wall is an execution constraint, not a signal
  one).

**Parity proof** (73.8 s on the restricted table; run under the interim prev-close
variant): engine = 35,014 trips / PF 2.074 / win 72.0 / avg +1.22 / med +1.95;
NULL-aware post-hoc SQL on `spec_tkds_nostops` under identical conventions =
**35,014 / 2.074 / 72.0 / +1.22 / +1.95 — zero set difference on (symbol, date,
signal_sec)**. The gate stack is proven; the $1 convention is orthogonal to it.

⚠ Post-hoc gotcha for the record: the appender writes cold features as **NULL, not
NaN** — `isnan(eff_20m)` misses them; use `eff_20m IS NULL OR ...`. (DuckDB NaN
comparisons are total-order — `NaN >= x` is TRUE — so NaN-lenient clauses silently
flip meaning; NULL semantics are the safe ones.)

**The final `v12_reversals/` book (record-first, all 5,560 tkds): 37,778 trips / PF
2.155 / win 72.4** — sub-$1 entries recorded, cut post-hoc. This is the production
reversal book the continuation trades hang off (S20 filters parents to
`entry_px/adj_ratio >= 1` in the join).

## S20 — ⭐ RIGHT-SIDE-OF-V CONTINUATIONS: built, verified, first grid (2026-07-29)

Engine v5 (user design, Lance's right-side-of-the-V): every reversal fill arms 3
watchers; the first STRICT break above the prior {1m,2m,5m}-bar max strictly after
the fill bar (aux discipline) opens that window's continuation at the next bar's
vwap — one per (parent, window). Each row carries 3 counterfactual trailing stops:
strict break below the prior {1m,2m,5m}-bar rolling MIN (ratchets up, fill next bar),
MOC backstop, NO target. Second parquet stream `cont_trips_p*.parquet` (18 cols;
parent join on symbol+date+parent_signal_sec). Parents bit-unchanged (37,778, same
P&L). 113,216 cont rows ≈ 3/parent (118 never fired).

**Verification: 8/8 invariants at 0 violations** — incl. the sharp one: for
target-exited parents, cont-300's entry ≡ the parent's exit fill (same event, same
bar, same px to 1e-12). The 5m-high continuation IS "flip instead of exit".

**The 9-cell grid** (≥$1 parent book; ret vs each stop, MOC included):

| enter on | stop | n | win | PF | avg % | med % | med hold | moc% |
|---|---|---|---|---|---|---|---|---|
| 1m high | 1m trail | 37,512 | 45.5 | **1.580** | **+0.37** | −0.16 | 2.4m | 0.0 |
| 1m high | 2m trail | 37,512 | 44.5 | 1.374 | +0.33 | −0.32 | 4.7m | 0.1 |
| 1m high | 5m trail | 37,512 | 37.7 | 1.164 | +0.21 | −0.88 | 9.6m | 1.0 |
| 2m high | 1m trail | 37,504 | 44.1 | 1.335 | +0.22 | −0.23 | 2.5m | 0.0 |
| 2m high | 2m trail | 37,504 | 41.6 | 1.133 | +0.12 | −0.46 | 5.0m | 0.2 |
| 2m high | 5m trail | 37,504 | 37.8 | 0.974 | −0.04 | −0.95 | 11.0m | 1.2 |
| 5m high | 1m trail | 37,402 | 40.2 | 1.001 | 0.00 | −0.40 | 2.5m | 0.2 |
| 5m high | 2m trail | 37,402 | 35.4 | 0.796 | −0.22 | −0.72 | 4.9m | 0.6 |
| 5m high | 5m trail | 37,402 | 33.3 | 0.726 | −0.46 | −1.32 | 12.0m | 2.2 |

**Monotone in BOTH axes: earlier entry better, tighter stop better.** 1m×1m is the
grid's corner and only strong cell. The 5m-high row ≈ 0-to-negative — the parent's
exit at the 5m high is exactly where the pop dies; **"flip instead of exit" LOSES,
the reversal exit is vindicated a second time** (after S13).

Year audit, 1m×1m: 2.553 / 1.870 / 1.352 / 1.170 / 1.289 / 1.261 / 1.633 (2020→26)
— **positive every year** (more regime-robust than anything the momentum program
produced), but median negative 5 of 7 years: a tail-carried trend profile.

Fee overlay (1m×1m): gross +0.374%/trip PF 1.58 → **net marketable 1.05¢ RT: +0.012%,
PF 1.014 — DEAD as a taker strategy**; net tiered commission-only 0.4¢ RT: +0.236%,
PF 1.326. The continuation buys the break (taker-natural) — unlike the reversal legs,
passive entry is not free here. Nov 2026 access-fee cut (30→10 mils) moves taker
all-in to ~0.65¢ RT ≈ +0.15%/trip net — marginal, not dead. ⏭ candidate next steps:
condition the 1m×1m cell on parent quality (S12 depth×volume cells, dist bands),
speed-of-break features, or delay-from-parent-entry; a filtered sub-cell clearing
~0.4%/trip average would trade net.

## S21 — composed exits: arm-a-trail-at-the-Nth-high vs the target (2026-07-29)

User idea: reversal entry, but once the first {1m,2m,5m} high prints, switch to a
{1m,2m,5m} trailing stop. Composable exactly from the two parquets: ret =
`coalesce(cont_W.stop_M_px, parent.exit_px) / parent.entry_px − 1` (a parent with no
W-high break necessarily exited MOC — a 5m-high break implies a 1m-high break).
≥$1 book, n = 37,512 each:

| arm at | 1m trail | 2m trail | 5m trail |
|---|---|---|---|
| 1m high | 1.945 / +0.76 / med 0.68 | 1.749 / +0.71 / 0.43 | 1.481 / +0.59 / −0.16 |
| 2m high | 2.005 / +0.93 / 1.06 | 1.782 / +0.83 / 0.81 | 1.483 / +0.66 / 0.12 |
| 5m high | 2.031 / +1.27 / 1.78 | 1.761 / +1.04 / 1.44 | 1.479 / +0.79 / 0.75 |
| **ACTUAL 5m target** | **2.137 / +1.28 / med 2.01 / win 72.3** | | |

**The target survives its THIRD challenge of the day** (S13 sweep, S20 flip, S21
trails). Monotone: the earlier the trail arms, the more reversion it forfeits — after
the first 1m-high pop, price routinely dips through the 1m low before resuming, so
the trail scratches wins-in-progress. The near-miss: **arm@5m-high × 1m-trail** ("ride
past the target with a trail") is average-NEUTRAL (+1.27 vs +1.28) but median-worse
(1.78 vs 2.01) and slower — the continuation tail exactly pays for the give-back
(consistent with S20's 5m×1m ≈ PF 1.00). No upside is hiding past the target.
Verdict: the right side of the V is a real but SEPARATE (and taker-cost) edge — it
does not improve the reversal's exit.

## S22 — flush entry vs breakout entry, identical 5m target (2026-07-29)

User question: is it better to enter on the flush (the reversal) or on the 1m/2m-high
break (the right side), both exiting at the SAME 5m-high target? Composable exactly:
the first 5m break after a 1m/2m-break entry IS the parent's exit event (a 5m breach
implies a 1m breach, so the 1m break can never come later), so composed ret =
`parent.exit_px / cont_W.entry_px − 1`, MOC fallback inherited. ≥$1 book, identical
subsets:

| strategy | n | win | PF | avg % | med % | med hold |
|---|---|---|---|---|---|---|
| FLUSH entry (reversal) | 37,512 | 72.3 | **2.137** | **1.28** | **2.01** | 10.2m |
| 1m-high break entry | 37,512 | 74.0 | 1.917 | 0.90 | 1.48 | 7.8m |
| 2m-high break entry | 37,504 | 75.5 | 1.688 | 0.57 | 1.08 | 5.1m |

**The flush entry wins; the confirmation premium is now priced: ~0.38%/trip for the
1m break, ~0.71% for the 2m break** — more than the risk it removes, though win rate
genuinely rises with confirmation (72.3 → 75.5) and holds shorten. The V6 asymmetry
("long buys WEAKNESS") survives measurement on identical trades with an identical
exit. Execution compounds it: the flush entry is passive-limit-natural (fee ≈ 0), the
break entry taker-natural (~1.05¢/sh RT). The 1m-break variant (1.92 / 74% / +0.90%)
remains a respectable standalone shape for discretionary use — it is just strictly
dominated here.

## S23 — MA / VWMA exit sweep: the target survives challenge #4 (2026-07-29)

Engine v6: 24 MA-EXIT MARKS per trip — first STRICT cross of vwap above the
strictly-prior {10,20,30,40,50,60}m mean after the fill bar (aux discipline, fill
next bar), simple price MA + VWMA, PARTIAL-TOLERANT early-session windows (a young
60m window = the session-so-far mean), MOC/day-end fallback baked in. Schema 121
cols. Hand-audit: ADIL 2024-04-12 ma_10m cross recomputed from the slim tape —
exact to the bar and the digit.

⚠ **Shadowing bug caught mid-flight:** the new `dvSum1200` binding shadowed the
engine's existing one (which feeds `vwap_1200`) and its double-push made the
1200-window cover 600s with doubled sums — `vwma_20m` held 1.2 minutes and
`vwap_1200` was corrupted in the interim parquet. The anomaly (a non-monotone hold
time) exposed it; fixed by reusing the original object; final run verified
bit-identical on the book. House rule reinforced: **an off-pattern cell in a sweep
is a bug until proven a finding.**

Sweep (≥$1 book, n=37,512; MA ≈ VWMA at every window — table shows MA):

| exit | win | PF | avg % | med % | med hold | ≥15:50 |
|---|---|---|---|---|---|---|
| ma_10m | 72.4 | 2.119 | 1.16 | 1.86 | 8.8m | 0.1% |
| ma_20m | 69.6 | 1.837 | **1.34** | **2.40** | 20.8m | 1.8% |
| ma_30m | 66.6 | 1.640 | 1.32 | 2.56 | 35.5m | 5.2% |
| ma_40m | 65.5 | 1.484 | 1.22 | 2.76 | 50.6m | 9.8% |
| ma_50m | 64.9 | 1.437 | 1.23 | 2.97 | 64.0m | 14.0% |
| ma_60m | 64.9 | 1.382 | 1.17 | 3.17 | 79.1m | 18.2% |
| **ACTUAL 5m-high target** | **72.3** | **2.137** | 1.28 | 2.01 | 10.2m | 0.4% |

ma_10m ≈ the target (same event family: the first 5m-high break and the 10m-mean
cross nearly coincide). **ma_20m is the first exit all day to beat the target on
avg AND median** (+1.34/2.40 vs +1.28/2.01) — but the year audit kills it:

| yr | PF actual | PF ma_20m | avg act | avg ma20 |
|---|---|---|---|---|
| 2020 | 3.79 | 2.87 | 2.01 | 2.17 |
| 2021 | 2.93 | 2.56 | 1.49 | 1.64 |
| **2022** | **1.30** | **1.02** | **0.46** | **0.04** |
| 2023 | 1.52 | 1.40 | 0.76 | 0.80 |
| 2024 | 2.22 | 2.16 | 1.38 | 1.70 |
| 2025 | 1.86 | 1.60 | 1.10 | 1.05 |
| 2026 | 1.68 | 1.44 | 1.10 | 0.96 |

**Verdict: the ma_20m avg/median premium is a GOOD-YEAR artifact** (2020/21/24 pay
for it; 2022 goes to breakeven — the far mean stops getting reached in grind
regimes and losers bleed to the close). Longer MAs are the same trade-off amplified:
medians climb to +3.17% (patience conditioning) while PF decays to 1.38. MA vs VWMA:
a wash everywhere (≤0.03 PF). **The 5m-high target survives its FOURTH challenge**
(S13 windows, S20 flip, S21 trails, S23 means) — fast reversion capture + regime
robustness beats every patience variant. Exits are CLOSED for this system.

## S24 — e1m continuation: adding a 5m target to the 1m trail (2026-07-29)

User idea: e1m×1m-trail continuation (S20's corner cell) PLUS the 5m-high target —
first event wins. Composable exactly (the 5m-target-after-cont-entry ≡ the parent's
exit event per S22; trigger-bar ties impossible — one bar cannot break above the 5m
max and below the 1m min; 0 edge-case exclusions).

| e1m exit | win | PF | avg % | med % | p95 % |
|---|---|---|---|---|---|
| 1m trail only (S20) | 45.5 | 1.580 | +0.37 | −0.16 | 4.75 |
| 5m target + 1m trail | 46.3 | 1.658 | +0.42 | −0.13 | 4.59 |
| 5m target only (S22 shape) | **74.1** | **1.957** | **+0.92** | **+1.48** | 5.29 |

**The trail fires first 78.6% of the time — the target only gets a say in 21.4% of
trades.** Where it does fire first, locking the 5m-high price beats the trail's
give-back (+0.08 PF, small tail cost), but the stop remains the dominant and
damage-doing term. The user's prediction confirmed: targets improve a stopped
variant, and neither approaches target-without-stop. ⭐ General principle, now
measured: in any multi-exit design the FASTEST trigger owns the outcome
distribution, and the fastest trigger is the one nearest to price — a stop
near price converts the win-rate into a coin flip before slower exits can act
(the S16/S20/S24 lottery mechanism in one line).

## S25 — the OR-of-5 A+ union + the eff-cell reconciliation (2026-07-29)

**The 2.5-PF campaign opens.** User candidate: v1.2 + ANY of {vol 1m/20m ≥4,
speed <−10%, chg-from-20m-low <−1%, eff_20m <−0.45, volat ≥200bp}.
≥$1 v1.2 book (37,512 @ 2.137):

| cut | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| + ANY of the 5 | 8,493 | 73.2 | 2.285 | 1.63 | 2.33 |
| + ANY of 4 (eff dropped) | 3,141 | 73.2 | 2.380 | 2.28 | 3.20 |
| none of the 5 | 29,019 | 72.0 | 2.087 | 1.18 | 1.93 |

Singles: vr≥4 = 3.88 (n 535) · speed<−10% = 2.29/avg 3.10 (732) · volat≥200 =
2.36/avg 2.85 (543) · break<−1% = 2.45 (1,860) · eff<−0.45 = 2.33 (5,810 — dominates
the union). Year audit: OR-5 2022 = 1.13; OR-4 2022 = **0.60** — the extremes are
wild-year features; the eff term is the only regime-robust member and the only thing
keeping 2022 positive. **Verdict (user): not worth it.**

**The eff-interior reconciliation** — the remembered "10,118 @ ~2.5" traced exactly:

| stage (full universe) | n | PF |
|---|---|---|
| S9-era eff [−0.5,−0.45) cell | 10,133 | 2.453 |
| + \|eff_10m\| ≥ 0.15 | 9,892 | 2.481 |
| + ≥$1 | 7,025 | 2.317 |
| + vol10 gate (v1.2 form) | 5,810 | 2.325 |

**The $1 floor deflated the cell** (the eff10 floor actually helped it): 2,867 of its
trips were sub-$1 — where the extra PF lived — and sub-$1 is fee-dead (S7c). The
~2.5 version was never tradable; its tradable form runs 2.32-2.33.

⚠ **METHOD RULE (new):** the restricted `flushfader_spec_v11_tkds` universe contains
only tkds with ≥1 v1.1+$1 trip → post-hoc there is EXACT only for TIGHTENINGS of
v1.1+$1; any RELAXATION (drop a floor, widen a band, sub-$1) silently undercounts
(the S9 cell read 7,246 vs the true 10,133 — 28% missing). Relaxations run against
`base20_e1200_x300/` (⚠ no vol_5/10, tc_5/10 columns there) or need a full-universe
engine rerun.

## S26 — the full lever battery on SPEC v1.2 (2026-07-29)

All breakdowns on the engine-gated v1.2 ≥$1 book (n=37,512 / PF 2.137 / win 72.3 /
avg +1.28 / med +2.01).

**01 volat_20m (bp):**

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| [40,60) | 4,316 | 73.7 | 2.322 | 1.09 | 1.61 |
| [60,80) | 10,914 | 71.3 | 2.121 | 1.04 | 1.76 |
| [80,120) | 15,096 | 72.5 | 2.209 | 1.33 | 2.09 |
| [120,200) | 6,643 | 72.5 | 1.957 | 1.55 | 2.84 |
| ≥200 | 543 | 72.4 | 2.360 | 2.85 | 4.22 |

**02 flush speed (%):**

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| [−3,−2) | 14,313 | 71.8 | 2.019 | 0.95 | 1.75 |
| [−4,−3) | 9,892 | 73.0 | 2.146 | 1.21 | 1.98 |
| [−6,−4) | 8,794 | 72.7 | 2.233 | 1.48 | 2.27 |
| [−10,−6) | 3,781 | 71.8 | 2.171 | 1.90 | 2.89 |
| <−10 | 732 | 70.6 | 2.292 | 3.10 | 4.81 |

**03 K (lows_since_first_low):**

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| 26-29 | 6,911 | 71.5 | 2.075 | 1.22 | 1.96 |
| **30-34** | 8,470 | 73.0 | **2.400** | 1.43 | 2.10 |
| **35-39** | 7,637 | 73.7 | **2.347** | 1.42 | 2.09 |
| 40-44 | 6,962 | 71.0 | 1.892 | 1.10 | 1.90 |
| 45-50 | 7,532 | 72.1 | 1.986 | 1.19 | 1.94 |

**04 eff_20m** — monotone, deeper better:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| [−.50,−.45) | 5,810 | 73.8 | 2.325 | 1.40 | 2.11 |
| [−.45,−.40) | 9,141 | 73.1 | 2.249 | 1.33 | 2.00 |
| [−.40,−.35) | 11,163 | 72.5 | 2.153 | 1.33 | 2.06 |
| [−.35,−.30) | 11,398 | 70.7 | 1.958 | 1.14 | 1.90 |

**05 eff_10m** — the hump peaks at [−.6,−.45):

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <−0.75 | 1,627 | 71.1 | 2.202 | 1.31 | 1.87 |
| [−.75,−.6) | 6,966 | 73.6 | 2.024 | 1.30 | 2.12 |
| **[−.6,−.45)** | 12,438 | 73.9 | **2.368** | 1.46 | 2.16 |
| [−.45,−.3) | 10,665 | 71.6 | 2.002 | 1.16 | 1.98 |
| [−.3,−.15) | 5,783 | 68.9 | 2.055 | 1.09 | 1.65 |
| positive side | 33 | — | — | — | noise |

**06 dist from 20m high (%)** — the wall creeps in from −25, not −35:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| (−15,−10] | 20,491 | 72.0 | 2.123 | 1.03 | 1.76 |
| (−20,−15] | 10,489 | 72.0 | 2.233 | 1.42 | 2.19 |
| **(−25,−20]** | 4,345 | 74.8 | **2.305** | 1.87 | 2.98 |
| (−35,−25] | 2,187 | 71.3 | **1.764** | 1.81 | 3.44 |

**07 chg from 20m low (%)** — decisive breaks better:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| ≥−0.1 | 12,824 | 72.1 | 2.011 | 1.10 | 1.86 |
| [−.25,−.1) | 9,252 | 72.7 | 2.231 | 1.28 | 1.97 |
| [−.5,−.25) | 8,222 | 72.2 | 2.153 | 1.29 | 2.08 |
| [−1,−.5) | 5,354 | 71.7 | 2.117 | 1.42 | 2.22 |
| **<−1** | 1,860 | 73.9 | **2.454** | 2.09 | 2.94 |

**08 dist from 5m high (%)** — S14's ≥17 wall confirmed on v1.2:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <5 | 5,805 | 71.2 | 1.998 | 0.82 | 1.42 |
| [5,7) | 10,357 | 71.5 | 2.183 | 1.05 | 1.76 |
| [7,9) | 8,574 | 73.2 | 2.199 | 1.26 | 2.10 |
| [9,12) | 7,059 | 73.6 | 2.210 | 1.48 | 2.46 |
| [12,17) | 3,952 | 74.9 | 2.336 | 2.00 | 3.28 |
| ≥17 | 1,765 | 65.2 | **1.690** | 1.86 | 3.38 |

**09 vol 1m/20m:**

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <0.5 | 872 | 72.1 | 2.485 | 1.39 | 1.98 |
| [0.5,1) | 10,208 | 72.9 | 2.067 | 1.23 | 2.04 |
| [1,2) | 19,568 | 72.2 | 2.113 | 1.22 | 1.94 |
| [2,4) | 6,329 | 71.3 | 2.138 | 1.38 | 2.09 |
| **[4,8)** | 522 | 74.5 | **3.433** | 2.81 | 3.49 |
| ≥8 | 13 | 100.0 | all-win | 20.54 | 19.90 |

**10 tc 1m/20m:**

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <0.5 | 532 | 73.7 | 1.667 | 0.90 | 2.00 |
| [0.5,1) | 10,187 | 72.6 | 2.257 | 1.34 | 2.03 |
| [1,2) | 20,995 | 72.4 | 2.048 | 1.18 | 1.97 |
| [2,4) | 5,356 | 71.0 | 2.204 | 1.44 | 2.08 |
| [4,8) | 425 | 73.4 | 2.862 | 2.46 | 3.01 |
| ≥8 | 17 | 100.0 | all-win | 17.95 | 19.01 |

**11 vol10 rate** — the zone just above the 0.75 floor is the gate's weakest kept slice:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| [.75,1) | 5,465 | 72.6 | **1.867** | 1.10 | 1.98 |
| [1,1.25) | 5,720 | 73.3 | 2.341 | 1.43 | 2.00 |
| [1.25,1.5) | 5,157 | 72.3 | 2.188 | 1.32 | 2.00 |
| [1.5,2) | 7,941 | 73.2 | 2.291 | 1.40 | 2.13 |
| ≥2 | 13,229 | 71.2 | 2.070 | 1.20 | 1.95 |

**12 raw price ($)** — clean monotone DOWN in price:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| **[1,2)** | 9,513 | 73.6 | **2.598** | 1.67 | 2.37 |
| [2,5) | 14,725 | 72.3 | 2.229 | 1.28 | 1.89 |
| [5,10) | 6,836 | 72.9 | 2.012 | 1.18 | 2.05 |
| [10,25) | 4,737 | 69.8 | **1.682** | 0.91 | 1.79 |
| ≥25 | 1,701 | 69.6 | **1.335** | 0.53 | 1.85 |

**13 session low** (⚠ S11: the off-low premium was a good-year artifact on v1.1 — year-audit before acting):

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| new sess low | 22,462 | 70.7 | 1.947 | 1.14 | 1.90 |
| **higher-low leg** | 15,050 | 74.7 | **2.476** | 1.49 | 2.16 |

**14 time of day** — fades into lunch:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| 10:00-10:30 | 9,512 | 71.6 | 2.302 | 1.33 | 1.95 |
| 10:30-11:00 | 7,489 | 73.6 | 2.259 | 1.31 | 1.98 |
| 11:00-12:00 | 10,354 | 72.8 | 2.172 | 1.28 | 1.95 |
| 12:00-13:30 | 10,157 | 71.5 | 1.919 | 1.22 | 2.13 |

**15 dist to session vwap (%)** — ⭐ the battery's standout new cell:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <−15 | 11,282 | 70.7 | **1.846** | 1.31 | 2.27 |
| [−15,−10) | 11,356 | 72.9 | 2.302 | 1.32 | 2.06 |
| [−10,−6) | 10,379 | 71.4 | 2.222 | 1.10 | 1.72 |
| [−6,−3) | 2,398 | 73.6 | 2.221 | 1.07 | 1.74 |
| **≥−3** | 2,097 | **80.3** | **3.409** | 2.01 | 2.50 |

**16 chg vs prev close (%)** — the V2 wings pattern: flat-day and crashed-day both weak:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <−20 | 3,314 | 70.1 | **1.571** | 0.94 | 2.11 |
| [−20,−5) | 5,784 | 73.6 | 2.472 | 1.49 | 2.12 |
| [−5,5) | 5,457 | 68.9 | **1.671** | 0.89 | 1.74 |
| [5,20) | 6,413 | 73.4 | 2.295 | 1.26 | 1.87 |
| ≥20 | 16,544 | 73.0 | 2.339 | 1.41 | 2.09 |

**17 chg_3d (%)** — two peaks: FRESH flushes (mildly down 3d) and parabolic runners:

| bucket | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <−30 | 1,814 | 72.6 | 2.072 | 1.20 | 1.87 |
| [−30,−20) | 947 | 69.4 | 1.912 | 1.06 | 1.52 |
| [−20,−10) | 1,207 | 70.8 | 2.140 | 1.27 | 1.91 |
| **[−10,0)** | 1,940 | 74.3 | **3.039** | 1.76 | 2.25 |
| [0,10) | 3,208 | 73.1 | **1.778** | 0.96 | 1.78 |
| [10,30) | 6,851 | 71.0 | 2.244 | 1.22 | 1.77 |
| [30,75) | 9,614 | 71.6 | 1.957 | 1.10 | 1.94 |
| [75,150) | 5,976 | 73.3 | 2.139 | 1.36 | 2.21 |
| **≥150** | 5,340 | 74.0 | **2.620** | 1.80 | 2.45 |

[−10,0) = the battery's #2 cell (the flush is FRESH, not day 3 of a collapse) — same
semantic family as dist-sess-vwap ≥−3% (#1); check jointly. ≥150% = the parabolic
snapback (a seventh of the book at 2.62). Weak: flat-3d [0,10) 1.78, mid-collapse
[−30,−20) 1.91 — the chg_1d wings pattern at the 3d horizon.

## S27 — robustness audits: the two A+ cells + the eff_20m loosening test (2026-07-29)

**dist-sess-vwap ≥ −3% by year** (the S26 #1 cell; tightening → restricted book exact):

| yr | n | PF | win | med % |
|---|---|---|---|---|
| 2020 | 435 | 4.96 | 77.9 | 2.19 |
| 2021 | 400 | 4.16 | 81.8 | 1.99 |
| 2022 | 136 | 2.09 | 76.5 | 1.69 |
| 2023 | 268 | 1.79 | 72.0 | 2.44 |
| 2024 | 352 | 6.50 | 89.8 | 3.46 |
| 2025 | 402 | 6.88 | 82.3 | 2.63 |
| 2026 | 104 | 1.17 | 71.2 | 3.91 |

**HOLDS: beats the whole-book PF in 6 of 7 years, including the grind years** (2022
2.09 vs book 1.30; 2023 1.79 vs 1.52). One caveat: 2026 = 1.17 on 104 trips (win
still 71, median +3.9 — a few large tail losers; small-n noise or early regime signal,
watch it). Win rate never below 71%. Genuine A+ cell.

**vol 1m/20m [4,8) by year**: 3.14 / 8.75 / 3.05 / 108.5 / 21.4 / **1.85 / 1.21**
(n = 143/76/15/36/68/112/72). Early years spectacular, but **the two most RECENT
years are the weakest** and n is lottery-sized throughout. NOT robust as a gate —
at best a size-up flag, and even that is suspect while 2025-26 run 1.2-1.9.

**eff_20m loosening to −0.6** (relaxation → FULL parquet, v1.2-minus-vol10 approx —
the vol10 column doesn't exist there; S25 method rule):

| increment [−0.6,−0.5) | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| [−0.60,−0.55) | 1,822 | 70.5 | 2.296 | 1.39 | 1.78 |
| [−0.55,−0.50) | 3,895 | 73.1 | 1.840 | 1.06 | 1.95 |

Increment year audit: 5.77 / 3.99 / 1.61 / **0.96** / 1.76 / 1.43 / 2.61 — **2023
NEGATIVE, 2025 thin (1.43)**. The blended increment (~1.9-2.0) dilutes the 2.14 book
and imports a losing year. **Verdict: do NOT loosen eff_20m** — the −0.5 floor
stands. (Clarification: the "−0.6 hump" from S26 was eff_10M's peak bucket
[−0.6,−0.45), not an eff_20m result — eff_20m is monotone within its band and the
v1.1 grammar already flagged e20 < −0.5 as the zone where deep e10 inverts.)

## S28 — price × dollar volume: price is NOT a liquidity proxy (2026-07-29)

User question: is the S26 raw-price monotone (cheap = better) just DipRiderV6's
illiquidity lever in disguise? Grid on the v1.2 ≥$1 book (PF per cell):

| dv_0945 \ price | $1-2 | $2-5 | $5-10 | ≥$10 |
|---|---|---|---|---|
| $3-10M | 2.65 | 2.00 | 1.78 | 1.49 |
| $10-30M | 2.78 | 2.51 | 2.27 | 1.76 |
| $30-100M | 1.79 | 1.80 | 1.64 | 1.74 |
| ≥$100M | **2.96** | 2.53 | 2.36 | 1.45 |

Row marginals (n / PF / avg / med / med px): 3-10M 7,296/2.018/1.16/1.83/$3.68 ·
10-30M 7,514/2.329/1.34/1.95/$3.58 · 30-100M 7,795/1.753/0.99/1.89/$3.81 ·
≥100M 14,907/2.352/1.47/2.18/$3.07.

**Verdicts:** (a) the price gradient SURVIVES within every dv row (monotone in 3 of
4, steepest in the most liquid) — price is its own lever (structural: a $1.50 name's
flush→5m-high cycle is a bigger % move; tick-size friction favors the fader);
(b) **V6's illiquidity lever does NOT reproduce — inverted here**: dv has no clean
gradient controlling for price and dv≥100M is the BEST row. v1.2 already forces an
in-play tape, so "illiquid" barely exists on this book; within in-play, headline
names revert best. (c) Bonus cell: **dv≥100M × $1-2 = 2.96 on 4,079 trips** (sub-$2
name printing $100M+ by 09:45 = maximal in-play) — dsv-cell-class strength at 2× the
size. (d) Oddity: the $30-100M row is uniformly weak (~1.75) at every price — no
mechanism story; watch-item, not signal.

## S29 — cell year audits PASS + the 20m dollar-flow lever + a mixed-scale engine wart (2026-07-29)

**Year audits — both pending cells HOLD (positive every year):**

| yr | dv0945≥100M × $1-2 (n/PF) | chg_3d [−10,0) (n/PF) |
|---|---|---|
| 2020 | 699 / 5.21 | 233 / 4.56 |
| 2021 | 565 / 6.80 | 550 / 7.54 |
| 2022 | 519 / **1.56** | 228 / **1.55** |
| 2023 | 550 / 2.01 | 212 / 2.39 |
| 2024 | 903 / 2.66 | 272 / 3.90 |
| 2025 | 666 / 3.22 | 327 / 1.77 |
| 2026 | 177 / 3.30 | 118 / 2.47 |

Both beat the book in every year incl. 2022 (1.56/1.55 vs book 1.30). The A+ roster:
dist-sess-vwap ≥−3 (3.41) · dv0945≥100M×sub-$2 (2.96, n 4,079) · chg_3d[−10,0) (3.04).

**⚠ MIXED-SCALE DISCOVERY:** the emitter adjusts PRICE (`vwap × adj_ratio`) but
leaves VOLUME raw → every vwap·volume product (`dvSum60`/DvFloor gate,
`dollar_vol_60` column) is adj_ratio × real dollars. vwap_60/vwap_1200/VWMA are
UNAFFECTED (ratio cancels), volume RATIOS unaffected. Exposure: **808 trips (2.15%
of the book) passed the $100k/60s floor only via inflation** (median-scale reverse-
split names get a floor relaxed by their ratio — and adj_ratio embeds FUTURE splits,
so gate strength is subtly future-dependent: the 2026-07-16 bug class in miniature).
⏭ FIX CANDIDATE (user decision, spec-affecting): emitter divides volume by
adj_ratio → all dollar quantities honest, volume ratios unchanged, absolute vol_*
columns move to today's-share units. Post-hoc honest dollars meanwhile:
`dollar_vol_60/adj_ratio`, `(entry_px/adj_ratio)*vol_N`.

**The 20m dollar-flow grid** (honest: `(entry_px/adj_ratio)·vol_1200`; book median
$8.7M/20m). PF by row × price:

| dv-20m \ price | $1-2 | $2-5 | $5-10 | ≥$10 | row n / PF |
|---|---|---|---|---|---|
| <$3M | 3.08 | 2.28 | 1.02 | (16) | 4,108 / 2.644 |
| [3,10M) | 1.96 | 1.92 | 1.64 | 1.73 | 16,684 / **1.866** |
| [10,30M) | **5.44** | 2.44 | 1.79 | 1.25 | 10,918 / 2.038 |
| [30,100M) | **6.74** | 3.01 | 3.64 | 1.75 | 4,638 / 2.628 |
| ≥$100M | (17) | 40.9 | 8.69 | 3.04 | 1,164 / **5.459** |

**The signal-time dollar FLOW is a far bigger lever than morning liquidity:**
U-shaped rows (quiet <3M = 2.64; the mid bulk [3,10M) = 1.87 weakest; then
monotone UP to ≥100M/20m = 5.46 — $5M+/minute through a 20m-low flush = mass
capitulation, the S12 deep+loud story in dollars). The price gradient still holds
within rows (steepest where flow is big: sub-$2 × [10,30M) = 5.44 on n 1,553;
× [30,100M) = 6.74 on 341). ⏭ candidates: year-audit the flow rows; consider a
dv20-based size/selection overlay; decide the volume-adjustment engine fix.

**S29b — THE FIX (engine v7, 2026-07-29):** the emitter now divides volume by the
same adj_ratio it multiplies price by (`volume = raw / r`) — price and shares share
one scale, every vwap·volume product is honest dollars. Verified on the rerun:
**zero trips below the honest $100k floor** (min dollar_vol_60 = $100,051). Book
impact: ≥$1 book 37,512 → **36,717** (−795, the inflated-floor class), PF 2.137 →
**2.141**, win/avg/median unchanged, year profile unchanged (2022 1.29, 2023 1.47,
all positive). The contamination class is gone; the edge didn't move — the 808
trips were floor-noise, not signal. All dollar-denominated post-hoc work
(dv20 flow grids, fee math via entry_px/adj_ratio) needs no correction factor on
engine-v7 parquets; `bar_vol`/`vol_*` columns are now in today's-share units
(ratios unchanged).

## S30 — the float study: price is NOT a float proxy either (2026-07-29)

Join: trips → `ticker_cik` → ASOF latest `dei:EntityPublicFloat` (dollars, as-filed)
with `known_date <= trade_date` (no lookahead; value up to ~1y stale). Coverage:
67.6% of the ≥$1 v7 book (11,914 trips uncovered — delisted/foreign/non-XBRL; that
slice runs 1.82).

| float (as-filed $) | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <25M | 12,215 | 73.8 | 2.460 | 1.53 | 2.26 |
| [25,75M) | 5,135 | 72.8 | 2.032 | 1.16 | 2.03 |
| [75,300M) | 4,411 | 70.9 | 2.239 | 1.19 | 1.78 |
| [300M,1B) | 1,676 | 67.1 | **1.814** | 0.83 | 1.31 |
| ≥1B | 1,366 | 76.5 | **5.059** | 1.87 | 2.06 |
| no data | 11,914 | 71.6 | 1.818 | 1.09 | 1.99 |

Float × price grid (PF): within fl<75M the price gradient SURVIVES monotone
(3.26 / 2.27 / 1.93 / 1.78 for $1-2/$2-5/$5-10/≥$10); within 75-300M it survives
flatter (2.60/2.29/2.04/2.06); within ≥300M it INVERTS into the "fallen giant" hump
($2-5 = 4.19, $5-10 = 3.52).

**Year audits kill the big-float glamour:** ≥1B = half-2020 (672/1,366 trips @ 8.66;
2026 = 1.15 on 40); fallen-giants ($2-10 × ≥300M) = 7.29 in 2020, fading to 1.92 /
0.39 in 2025/26. Real-company flushes are a CRASH-REGIME phenomenon — real but not
a durable overlay.

**Verdicts:** (a) **price is its own lever** — survives controlling for float, as it
did for dollar volume (S28); (b) nano-float <$25M = a third of the covered book at
2.46, the V6/LowFlyer small-float story alive but mild inside v1.2; (c) the 300M-1B
mid-cap band (1.81) + the no-data slice (1.82) are the weak zones; (d) big-float
strength = 2020-carried, ignore. No spec change.

## S31 — THE FULL-WINDOW BOOK (full universe) + the v1.3 candidate (2026-07-29)

Engine v7 defaults now 09:45–15:50 (the 10:00–13:30 window was a VwapReclaim-era
throwback). Full-universe run: 328,258 candidate tkds, 57 min, 69,456 trips
(`v12_full_universe/`). New fast-iteration table: **`flushfader_v12_fullwin_tkds`
(6,990 tkds)** — same tightenings-only caveat as before (S25).

**⚠ PREVIEW-BIAS LESSON (extends the S25 method rule):** the restricted-table
preview didn't just undercount the new windows — it BIASED them. Preview said
09:45–10:00 = PF 3.81 (n 872); the honest full-universe number is **1.90 (n 2,551)**.
The old tkd list selected days that ALSO had mature mid-day signals — a
systematically better morning population. The restricted table biases any
out-of-scope slice, not merely shrinks it.

Honest window breakdown (≥$1):

| window | n | win | PF | avg % | med % | moc% |
|---|---|---|---|---|---|---|
| 09:45-10:00 | 2,551 | 71.3 | 1.901 | 1.06 | 1.79 | 0.0 |
| 10:00-13:30 | 37,014 | 72.4 | 2.132 | 1.27 | 2.01 | 0.3 |
| 13:30-15:00 | 8,239 | 70.2 | 1.759 | 1.06 | 2.01 | 4.2 |
| 15:00-15:50 | 5,544 | 65.8 | **1.315** | 0.55 | 1.66 | **21.6** |

The morning slice ≈ book-level (keep), 13:30-15:00 mildly dilutive (keep for
breadth), **15:00+ = the weak tail** (no room to revert; 21.6% forced MOC).

**The v1.3 candidate — window 09:45-15:00 + price < $10:**

| variant | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| full window, no ceiling | 53,348 | 71.3 | 1.937 | 1.15 | 1.96 |
| + entries end 15:00 | 47,804 | 71.9 | 2.044 | 1.22 | 2.00 |
| **+ price < $10** | **39,541** | 72.2 | **2.135** | 1.29 | 2.03 |

Year audit of the candidate: 3.93 / 2.93 / **1.47** / **1.64** / 2.04 / 1.84 / 1.67
— positive every year, and the thin years IMPROVE on the old narrow-window book
(2022 1.47 vs 1.29; 2023 1.64 vs 1.47). **Net: +8% trips (39,541 vs 36,717) at the
same PF (2.135 vs 2.141) with better worst years** — the window expansion and the
price ceiling pay for each other. ⏭ decide v1.3 (bake ceiling + 15:00 end), then
mc=1 + K-gate book.

**S31b — the 15:00+ tail drilled (10-min slots):** 1.212 / 1.504 / 1.296 / 1.152 /
1.431 (n 848-1,339 each; win 63-71; moc 6.6% → 36.7%). Uniform weakness, NO gradient
— [15:00,15:30) ≈ 1.34 vs [15:30,15:50) ≈ 1.29, and the final slot isn't the worst,
so it's not purely time-to-revert: the late-afternoon flush population is weaker
throughout (EOD-liquidation flavor). No finer boundary earned — **15:00 stands as
the v1.3 entry ceiling.**

---

## ⭐ THE SPEC v1.3 — reference card (2026-07-29)

**Mechanics (unchanged since D1):** ENTRY = bar vwap STRICTLY below the prior
1200-present-bar (~20m) MIN, fill next present bar's vwap, sampler mc=0. EXIT =
vwap STRICTLY above the prior 300-bar (~5m) MAX, fill next bar; else MOC 16:00.
**NO stop of any kind** (S16/S24). Universe: dv_0945 ≥ $3M. Liquidity floors at
signal: ≥$100k and ≥60 trades in the trailing 60 present bars (honest dollars, v7).

**The signal stack (all at signal time, engine-gated):**

| # | condition | value | meaning |
|---|---|---|---|
| 1 | volat_20m | ≥ 40bp | day volatile enough to fade |
| 2 | flush speed (vwap/vwap_60_prev − 1) | < −2% | falling fast over the last minute |
| 3 | K = lows_since_first_low | ∈ [26, 50] | mature leg — THE 2022 fix |
| 4 | eff_20m (signed) | ∈ [−0.5, −0.3) | trending down, not un-fadeably (cold PASSES) |
| 5 | \|eff_10m\| | ≥ 0.15 | 10m tape not flat (cold fails) |
| 6 | dist from 20m high (vwap/chan_hi − 1) | ∈ (−35%, −10%] | deep into the leg, inside the fadeable zone |
| 7 | (vol_10/10)/(vol_60/60) | ≥ 0.75 | last-10s tape ≥75% of the minute's pace (S17/S18) |
| 8 | entry window | **09:45 – 15:00** | S31/S31b/S31c: 15:00+ = quality and completion-room degrade together |
| 9 | raw price | **< $10** | S26/S28/S30/S31: price is its own lever; ≥$10 loses 2022-23 |

**Post-hoc (not a gate):** raw entry price ≥ $1 (the S7c fee wall; kept post-hoc so
names crossing $1 intraday stay recorded).

**The book:** n = 39,541 / PF 2.135 / win 72.2 / avg +1.29% / med +2.03%; positive
all 7 years, worst 2022 = 1.47. **UPDATE (S35c, engine v8):** #8 (09:45-15:00) now
BAKED; the universe floor is now `dv_0945_tape ≥ $3M` (1s-bar-native honest dollars
— the candidate dv_0945 gate is DEPRECATED, S35 scale bug); #9 (<$10) remains
post-hoc alongside the ≥$1 cut. Honest-floor book ≈ 33,780 @ ~2.17 (S35b);
definitive `v13_reference/` run supersedes prior parquets.

**Validated A+ overlays (sizing, NOT gates):** dist-sess-vwap ≥ −3% (PF 3.41, 6/7
yrs beat book) · dv_0945 ≥ $100M × sub-$2 (2.96, every yr) · chg_3d ∈ [−10, 0)
(3.04, every yr) · 20m dollar-flow extremes (U-shape; ≥$100M/20m = 5.46).

**S31c — the cliff search (30-min slots, <$10 book):** 13:00→15:50 = 1.895 / 1.860 /
1.588 / 1.795 / 1.553 / 1.393; moc% 0.7 → 31.3. No cliff — a gentle sag with a
noise wobble (14:00-14:30 dips, 14:30-15:00 RECOVERS to 1.80 / med +2.17). The only
joint quality+completion break is 15:00 (PF < 1.56 for good, moc 6.6 → 12.4 → 31.3).
**14:30 rejected; 15:00 confirmed as the v1.3 ceiling.**

## S32 — rvol_0945: the QUIET-OPEN cell is the best overlay yet (2026-07-29)

First-ever breakdown of the long-recorded rvol_0945_honest (premkt-incl vol thru
09:45 / prior-20d avg). Distribution on the v1.3 book is extreme (median 47×, p95
45,000× — the wreckage denominator). Buckets:

| rvol_0945 | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| **<1×** | 5,558 | 74.4 | **2.996** | 1.53 | 1.91 |
| [1,3) | 3,845 | 71.8 | 1.729 | 0.95 | 1.96 |
| [3,10) | 4,469 | 72.6 | 2.044 | 1.24 | 2.08 |
| [10,50) | 6,183 | 71.6 | 2.298 | 1.42 | 2.06 |
| [50,250) | 6,853 | 73.3 | 2.556 | 1.58 | 2.29 |
| [250,2.5k) | 6,938 | 70.8 | 1.794 | 1.09 | 1.99 |
| ≥2.5k× | 5,695 | 71.3 | 1.860 | 1.07 | 1.89 |

| yr | n <1× | PF <1× | med <1× | n [50,250) | PF [50,250) |
|---|---|---|---|---|---|
| 2020 | 1,046 | 5.94 | 2.44 | 1,204 | 4.69 |
| 2021 | 969 | 4.66 | 2.28 | 1,135 | 4.68 |
| 2022 | 615 | 2.63 | 1.82 | 609 | 2.37 |
| 2023 | 434 | 2.77 | 1.76 | 736 | 1.50 |
| 2024 | 833 | 2.23 | 1.81 | 1,113 | 2.34 |
| 2025 | 1,108 | 2.50 | 1.49 | 1,417 | 1.80 |
| 2026 | 553 | 1.55 | 1.27 | 639 | 2.82 |

**Answer to the user's question: NO — high initial rvol is not better.** Twin-peaked:
quiet-open (<1×) and heavy-but-sane ([50,250)); the "appeared-from-nowhere" extreme
wing (≥250×) runs below book (2026 = 0.75, 2022 = 1.33 at the edges).

**⭐ rvol < 1× year audit: 5.94 / 4.66 / 2.63 / 2.77 / 2.23 / 2.50 / 1.55 — beats
the book ALL 7 years, hardest in the grind years (2022 2.63 vs 1.47; 2023 2.77 vs
1.64).** With dv_0945 ≥ $3M still required, rvol<1 = a NORMALLY-LIQUID name having a
quiet-for-itself morning that then flushes — no overnight story, no positioned
crowd. The purest "fresh local dislocation in an unbroken name" cell; family:
dist-sess-vwap ≥−3%, chg_3d [−10,0). ⏭ family-overlap study (are the three one
cell?); ⏭ user idea: drop dv_0945 floor / raise the signal-time liquidity floor —
would WIDEN exactly this best slice (quiet-morning names currently excluded by the
$3M floor).

## S33 — the dv_0945 floor is LOAD-BEARING: the sub-$3M complement study (2026-07-29)

Method: the ≥$3M side already exists (`v12_full_universe`) — only the complement
needed a run (`flushfader_sub3m_tkds`, 64,557 tkds, 5 min → `v13_sub3m_complement`,
8,062 trips). Merged = clean union (disjoint universes). All numbers on the v1.3 cut.

| slice | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| dv_0945 ≥ 3M (current) | 39,541 | 72.2 | 2.135 | 1.29 | 2.03 |
| dv_0945 < 3M (NEW) | 6,386 | 68.9 | **1.706** | 0.96 | 1.83 |
| merged open universe | 45,927 | 71.8 | 2.065 | 1.24 | 2.00 |

New-slice year audit: 1.52 / 3.03 / **1.03** / 1.72 / 2.03 / 1.45 / 2.41 — 2022 ≈
breakeven. Dilutive everywhere it matters.

**Two falsifications, both instructive:**
1. **rvol<1 does NOT extend to illiquid names** — sub-$3M × rvol<1: 2022 0.86, 2020
   1.16, 2025 1.25. The S32 A+ cell is "LIQUID name having a quiet morning", not
   "low rvol" per se. Quiet morning + small ADV = just a thin name.
2. **The dv_20m replacement INVERTS on the new slice**: within sub-$3M-morning
   names, big signal-time flow is the WORST cell (≥10M/20m = 1.314) and modest flow
   the best ([1,3M) = 2.285) — the exact opposite of the old book (≥10M = 2.633).
   The dollar-flow lever's sign is CONDITIONAL on baseline liquidity: on liquid
   names big flow = capitulation (fade it); on illiquid names big flow = the whole
   day IS the event (pump-collapse), still falling.

**Verdict: dv_0945 ≥ $3M stays.** It is not merely a liquidity filter — it
certifies a real two-sided market existed BEFORE the flush, which is what makes
both the flow lever and the quiet-open lever mean what they mean. A signal-time
floor cannot replace it because the floor's meaning flips with the baseline.
(The one salvageable sliver — new × [1,3M)/20m = 2.29 on 2,151 trips — is not worth
restructuring the universe for.) Total experiment cost: 5 min of compute.

**S33b — dv_20m WITHIN the sub-$3M-morning slice (fine buckets):**

| dv_20m | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <1M | 29 | 62.1 | 3.673 | 1.19 | 0.51 |
| [1,1.5M) | 174 | 77.0 | **3.718** | 2.17 | 2.50 |
| [1.5,2M) | 474 | 69.4 | 2.315 | 1.69 | 2.20 |
| [2,3M) | 1,503 | 70.7 | 2.163 | 1.38 | 2.00 |
| [3,5M) | 2,139 | 67.9 | 1.337 | 0.54 | 1.72 |
| [5,10M) | 1,401 | 70.6 | 1.819 | 0.96 | 1.78 |
| [10,20M) | 469 | 62.3 | 1.527 | 0.75 | 1.57 |
| ≥20M | 197 | 61.9 | **0.918** | −0.15 | 1.27 |

MONOTONE INVERTED vs the established book: on quiet-morning names, less signal-time
flow = better; ≥$20M/20m goes NEGATIVE (pure pump-collapse). Boundary ≈ $3M/20m.
[1,3M) year audit: 1.64/8.67/1.66/1.83/2.56/2.08/3.82 — positive all 7 (2022 1.66 >
main book 1.47). **Principle refined: fadeable flow must be PROPORTIONATE to the
name's baseline.** Candidate satellite extension: `(dv_0945 ≥ 3M) OR (dv_0945 < 3M
AND dv_20m ∈ [1,3M))` = +2,151 trips (+5.4%) @ 2.29. ⚠ Held back for now: first-hour
cell on a fresh population (overfit risk) + thinnest-fill names in the system
(next-bar-vwap works hardest there). Revisit after the mc=1 book.

## S34 — the dv_0945 × dv_20m grid + THE FLOW-RATIO LEVER (2026-07-29)

Merged universe (v12_full_universe + v13_sub3m_complement), v1.3 cut. PF per cell
(n in parens):

| dv_0945 \ dv_20m | <1M | [1,3M) | [3,10M) | [10,30M) | ≥30M |
|---|---|---|---|---|---|
| <3M (new) | 3.67 (29) | **2.29** (2,151) | 1.49 (3,540) | 1.28 (615) | 1.87 (51) |
| [3,10M) | — (9) | **2.48** (1,377) | 1.80 (4,978) | 1.76 (1,005) | 5.61 (136) |
| [10,30M) | 3.09 (13) | **2.87** (951) | 2.32 (3,800) | 2.69 (2,310) | 1.95 (375) |
| [30,100M) | 1.82 (10) | 1.82 (807) | 1.41 (3,405) | 2.25 (2,660) | **3.74** (1,324) |
| ≥100M | — (14) | **1.56** (1,660) | 1.97 (7,237) | 2.16 (4,642) | **4.68** (2,828) |

**A PROPORTIONALITY DIAGONAL**: quiet-morning names fade best on quiet flushes
(top-left band), heavy-morning names on torrents (bottom-right); the ANTI-diagonal
corners are the weak cells — quiet-morning×torrent (1.28-1.49, pump-collapse) and
heavy-morning×quiet-flush (1.56, drift without capitulation — the S17 quiet-tail at
day scale).

**The unifying lever — flush flow as a fraction of morning flow,
`fr = dv_20m/dv_0945`** (the user's `dv_0945/dv_20m` = its reciprocal):

| fr = dv_20m/dv_0945 | n | win | PF | avg % | med % |
|---|---|---|---|---|---|
| <0.03 | 11,939 | 73.1 | 2.086 | 1.28 | 2.12 |
| [0.03,0.1) | 5,441 | 70.2 | 1.821 | 1.09 | 2.00 |
| [0.1,0.3) | 6,579 | 70.0 | 1.942 | 1.14 | 1.90 |
| **[0.3,1)** | 10,767 | 73.5 | **2.439** | 1.46 | 2.10 |
| [1,3) | 8,002 | 71.7 | 2.158 | 1.27 | 1.88 |
| [3,10) | 2,575 | 68.6 | 1.586 | 0.83 | 1.80 |
| ≥10 | 624 | 63.1 | 1.341 | 0.50 | 1.24 |

Hump at [0.3,1) — the 20m flush carrying 30-100% of the whole morning's dollars =
peak capitulation-in-context. Year audit of the hump: 4.98/2.98/**1.30**/1.66/2.82/
2.10/2.49 — positive all 7 (2022 = book-level). The ≥3 tail is erratic
(0.73-4.36 across years) — a caution zone, not a clean gate. Left side non-monotone
(<0.03 = 2.09 decent — very-slow-bleed days differ from the [0.03,0.3) dip). The
ratio doesn't fully replace the grid (the corners are sharper 2-D) but it's the
best single-number summary. ⏭ overlay/sizing candidate alongside the A+ roster; the
[1,3M)-flush satellite (S33b) is the fr∈[0.3,1] band restricted to quiet mornings.

**S34b — stacking fr < 3 on v1.3: NO-OP, skipped.** On the ≥$3M-morning book only
614 trips (1.6%) have fr ≥ 3 and they aggregate to PF 2.30 (yearly wild: 45.4 in
2020 on 84, 0.22 in 2022 on 57 — noise scale). Book 2.135 → 2.132, win/avg/med
unchanged; 2022 1.47 → 1.53 but 2020 loses its mini-jackpot. **The dv_0945 floor
already does the fr filter's job** (the toxic fr≥3 mass was the sub-$3M slice S33
excluded) — near-redundant conditions, confirming the floor-certifies-baseline
story. NOT added to v1.3; becomes MANDATORY iff the S33b quiet-morning satellite
universe is ever adopted.

## S35 — ⚠ THE dv_0945 SCALE BUG: the universe floor was future-split-dependent (2026-07-29)

User request: compute dv_0945 from OUR 1s bars for live-scanner consistency (engine
v8: `dv_0945_tape` = Σ vwap·volume strictly before 09:45, honest dollars, recorded +
`--min-dv-0945-tape` record-first gate). The comparison EXPOSED A BUG:

**`diprider_v6_candidate.dv_0945 = vol_0945(raw shares) × avgprice(raw) × adj_ratio`
— real dollars × adj_ratio.** Verified: `dv_0945_tape × adj_ratio / dv_0945` median
1.023, log-corr 0.9999 (the 2% = mean-1m-close vs true vwap). Same mixed-scale sin
as S29, at the UNIVERSE level, present in the ENTIRE DipRiderV6 lineage: a name that
later 1:25-reverse-split entered the $3M floor at $120k real morning dollars.

**Universe impact:** 328,258 → honest 261,888 tkds (−20%); 68,883 inflated IN,
only 2,513 unfairly excluded (forward splits rare here).

**Book impact (v1.3 cut, by honest tape dv_0945):** tape ≥3M = 33,693 @ 2.168;
the inflated-in slice = 5,848 trips (14.8%) @ 2.05 blended — **carried by 2020-21
(3.6/5.5), toxic in the current regime: 2025 = 1.08, 2026 = 0.36 (LOSING)**.
Removing it raises the book's PF and its recent-year quality simultaneously.

⚠ Collateral: every dv_0945-axis breakdown (S28 grid rows, S34 flow-ratio
denominator) mixes scales for reverse-splitters — post-hoc fix available
(`dv_0945/adj_ratio`); qualitative conclusions survive (dv20-based results were
honest), magnitudes need re-checks where dv_0945 was an axis. rvol_0945 is a pure
volume ratio — unaffected.

⏭ decision: wire the honest floor (options: SQL `dv_0945/adj_ratio >= 3M` ≈ tape
within 2%, zero runtime cost; or SQL safety-margin prefilter + exact engine tape
gate); forward-split complement run in flight for the missing 2,513 tkds.

**S35b — the forward-split complement (the 2,513 wrongly-excluded tkds):** 140 trips
(22 s isolated pass → `v13_fwdsplit_complement/`); v1.3 + tape≥3M cut = **87 trips @
PF 3.229 / win 83.9 / med +2.45** — healthy but negligible scale. **The honest v1.3
book = 33,693 (tape≥3M within the old universe) + 87 = 33,780 @ ~2.17**, fully on
disk without a full rerun. ⏭ pending the user's gate-wiring choice (SQL honest floor
vs SQL prefilter + exact engine tape gate) + then rebuild `flushfader_v13_tkds` and
re-check the dv_0945-axis breakdowns (S28/S34) with honest denominators.

## S36 — year audits of the heavy-morning × torrent cells (2026-07-29)

The S34 diagonal's bottom-right cells, v1.3 cut. [30,100M)×dv20≥30M (S34: 3.74) and
≥100M×dv20≥30M (S34: 4.68), + the ≥100M row recomputed on the HONEST morning
measure (dv_0945/adj_ratio):

| yr | [30,100M)×torrent | ≥100M×torrent | [30,100M) HONEST×torrent | ≥100M HONEST×torrent |
|---|---|---|---|---|
| 2020 | 201 / 29.56 | 534 / 5.23 | 472 / 8.07 | 202 / 11.52 |
| 2021 | 308 / 2.47 | 1,046 / 6.10 | 553 / 5.89 | 591 / 4.41 |
| 2022 | 33 / 5.15 | 167 / 3.73 | 104 / 5.96 | 48 / 4.18 |
| 2023 | 56 / 5.14 | 73 / **0.50** | 95 / **0.98** | 33 / **1.55** |
| 2024 | 228 / 3.40 | 312 / 4.62 | 397 / 4.03 | 124 / 3.73 |
| 2025 | 370 / 2.86 | 521 / 4.67 | 556 / 3.75 | 280 / 3.72 |
| 2026 | 128 / 1.94 | 175 / 6.20 | 175 / 3.27 | 118 / 5.26 |
| **TOTAL** | 1,324 / 3.74 | 2,828 / 4.68 | **2,352 / 4.37** | 1,396 / 4.56 |

**Both robust** — cell 1 positive all 7 (floor 1.94; 2020 = a 29× jackpot); cell 2's
one losing year (2023 = 0.50) is largely FAKE-$100M mornings (reverse-split-inflated
rows) — the honest axis repairs it to 1.55 with all other years 3.7-11.5. ⭐ The S35
bug was polluting even the headline-liquidity cells; the honest universe fixes cells
it never even targeted. Heavy-morning × torrent joins the A+ overlay roster
("fade the capitulation of a genuinely headline name").

**S36b — [30,100M) HONEST × torrent:** 2,352 @ **PF 4.37 / win 78.6 / avg +2.15 /
med +2.40** (vs 1,324 @ 3.74 inflated — the honest axis GREW the cell). Years:
8.07 / 5.89 / 5.96 / **0.98** / 4.03 / 3.75 / 3.27 — six of seven ≥3.27, 2022 = 5.96.
2023 ≈ breakeven is now the SHARED weak year of both honest heavy-morning cells
(0.98 / 1.55): a coherent regime statement — 2023 was bad for fading headline-name
torrents, period. Combined honest ≥30M-morning × ≥30M-torrent corner ≈ 3,700 trips
@ ~4.3 — the largest high-PF structure of the campaign, fully visible only on the
honest axis.

## S37 — ⭐⭐ THE STACK: torrent corner × dist-sess-vwap (2026-07-29)

User question: does dsv ≥ −3% stack with the honest ≥30M×torrent corner? 2×2 on the
v1.3 book:

| cell | vwap dist | n | win | PF | avg % | med % |
|---|---|---|---|---|---|---|
| torrent corner | < −3% | 3,105 | 76.6 | 3.915 | 2.06 | 2.36 |
| **torrent corner** | **≥ −3%** | **643** | **83.7** | **9.189** | **3.11** | **2.85** |
| rest | < −3% | 34,064 | 71.5 | 1.980 | 1.17 | 1.97 |
| rest | ≥ −3% | 1,729 | 75.0 | 2.510 | 1.52 | 2.12 |

**MULTIPLICATIVE, near-disjoint (17% overlap): headline name + $30M torrent + still
AT session vwap = the most violent local capitulation inside the most intact day.**
Year audit: 7.75 / 3.75 / 3.57 / 28.96 / 70.8 / 8.83 / ∞(42-0) — positive ALL 7;
**the dsv condition RESCUES the corner's 2023** (bear-market torrents that kept
falling were already below vwap; the intact-day subset held — user's regime frame:
2023 belongs to a future SHORT system).

⚠ **CONCENTRATION: 643 trips = 89 ticker-days (~14 events/yr).** This is an A++
SETUP (recognize + size hard, ~monthly), not a book — the mc=0 adds inflate n.
Playbook entry: the size-up pyramid is now dsv-prime alone (2.51) → torrent corner
(3.9) → the stack (9.2), with clip size scaling with cell liquidity (the stack =
the system's most liquid fills by construction).

**S35d — v13_reference VERIFIED (the definitive honest-universe book):** 392,815
candidates streamed, 59 min, 50,910 trips. v1.3 cut (≥$1, <$10): **33,954 / PF 2.152
/ win 72.4 / avg +1.28 / med +2.00**. Reconciliation vs the S35b assembly: assembly
fully contained (0 assembly-only), +174 reference-only trips (0.5% — the S25
tkd-list relaxation bias, now closed). Tape floor exact: min dv_0945_tape =
$3,000,074. New workhorse: **`flushfader_v13_tkds`** (from the reference run's trip
tkds) — all prior tkd tables (spec_v11, v12_fullwin, sub3m, fwdsplit) are STALE/
scaffolding. All post-hoc now runs off `v13_reference/` (127 cols incl.
dv_0945_tape).

## S38 — POST-HOC mc=1: the single-account book (2026-07-30)

**Method (user's design, replacing an engine mc=1 rerun which would distort the
continuation counterfactuals):** replay the v1.3 book chronologically (sorted by
trade_date, entry_sec, symbol), keep a min-heap of open exit times, take a trip
iff fewer than `mc` earlier-taken positions are still open at its entry (strict:
exit == entry still blocks; same-second entries can't double-fill). Script:
`scripts/equity/flushfader_mc.fsx` (`--mc N`; writes selected keys to
`v13_reference/mc{N}_selected.parquet` for SQL joins). Verified: 0 overlapping
pairs, 0 duplicate entry-seconds in the mc=1 selection.

**mc=1 by year** (of the 33,954-trip v1.3 book):

| year | n | PF | win% | avg% | med% |
|------|---|----|------|------|------|
| 2020 | 719 | 2.833 | 73.6 | +1.50 | +1.96 |
| 2021 | 730 | 2.320 | 71.9 | +1.08 | +1.65 |
| 2022 | 471 | 1.566 | 68.8 | +0.65 | +1.59 |
| 2023 | 507 | 1.449 | 66.7 | +0.60 | +1.44 |
| 2024 | 764 | 1.912 | 72.5 | +1.09 | +2.01 |
| 2025 | 899 | 2.069 | 71.0 | +1.14 | +1.89 |
| 2026 | 371 | 1.919 | 72.8 | +1.14 | +2.04 |
| **total** | **4461** | **2.004** | **71.2** | **+1.07** | **+1.80** |

**The concurrency curve is FLAT** — capacity buys trips, not per-trip edge:

| mc | n | % of book | PF | win% | med% |
|----|---|-----------|----|------|------|
| 1 | 4,461 | 13.1 | 2.004 | 71.2 | +1.80 |
| 2 | 8,406 | 24.8 | 1.994 | 71.2 | +1.81 |
| 3 | 11,832 | 34.8 | 1.993 | 71.4 | +1.81 |
| 5 | 17,397 | 51.2 | 2.022 | 71.8 | +1.87 |
| ∞ (mc=0) | 33,954 | 100 | 2.152 | 72.4 | +2.00 |

**Findings:** (1) mc=1 compresses PF only 2.152 → 2.004 (−7%) and stays positive
all 7 years — same 0-12% compression band as PlungeRider; the edge is NOT an
mc=0 attribution artifact. (2) ~2.6 taken trips/day at mc=1. (3) The trips
greedy SKIPS run HOTTER than the ones it takes (2.173 vs 2.004, med +2.04 vs
+1.80): flush clusters — moments when you're already busy — carry the best
fades (the proportionality story at portfolio scale). First-come-first-served
costs selection, not just capacity → an A+-priority overlay (prefer
dsv ≥ −3% / torrent-corner arrivals over marginal ones) should beat naive
greedy at fixed mc. 2022-2023 remain the soft years in every column.

### S38b — mc=1 on THE A++ STACK: save the bullet for the 4th flush (2026-07-30)

On v13_reference the stack (≥$30M tape morning × ≥$30M/20m flush × dsv ≥ −3%,
$1≤px<$10) = **662 trips / 91 tkds / PF 9.125 / win 82.8** (S37's 643 was the
pre-reference assembly).

**Naive mc=1 greedy** (take the first arrival, re-enter after exit): 102 trips
(~1.1/event), **PF 6.216 / win 79.4 / avg +2.50** — compression −32%, far above
the full book's −7%. The cell IS the averaging-down cell (7.3 trips/event) and
greedy spends its one bullet on the FIRST flush.

**Within-event entry rank** (causal — rank = # of cell signals fired so far that
ticker-day; user's "EV improves as the stock continues to decline" CONFIRMED):

| entry rank | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| 1st | 91 | 8.261 | 82.4 | +2.53 | +2.46 |
| 2nd-3rd | 153 | 8.737 | 85.6 | +2.77 | +2.66 |
| 4th-6th | 174 | **15.132** | **90.2** | +3.34 | +2.78 |
| 7th+ | 244 | 7.517 | 75.8 | +3.29 | +2.89 |

Median monotone 2.46 → 2.89; the 4th-6th flush is the sweet spot.

**mc=1 restricted to rank ≥ 4** ("save the bullet"): 75 trips (~11/yr),
**PF 8.528 / win 85.3 / avg +3.01 / med +2.66**, positive all 7 years
(2022 4.86 on n=3; 2023 ∞ on n=3). A single slot recovers ~93% of the mc=0
cell PF. Playbook: on an A++ event, do NOT clip the first flush — either run
the averaging-down campaign (the mc=0 measurement), or if one clip, wait for
the 4th cell signal of the day.

**S38b addendum — decomposing the 6.216 (user: "why is mc=1 at 6 when rank-1 is
8?"):** the 102 taken = ALL 91 rank-1 trips (PF 8.261, the bucket untouched) +
11 re-entries at PF 2.44 (IBO −9.2%, EYES −4.5% among them; all 11 fill after
~11:20, most after 13:00 — the slot frees on a quick target and greedy chases
the next signal on the same falling name; the −9.2% "target" exit is the 5m-high
drifting below entry on a deep crash). Arithmetic: 261.9/31.7 = 8.26 →
+42.0 win / +17.2 loss → 303.9/48.9 = 6.22 — eleven trips add 16% to the
numerator, 54% to the denominator. **CORRECTION to the framing: real compression
of take-the-first mc=1 is ~−10% (8.26 vs the cell's 9.13); the rest of the −32%
is small-n re-entry tail noise, NOT evidence that first flushes are weak.** The
robust findings stand: the rank table (n=153-244/bucket, 4th-6th = 15.1) and
rank≥4 "save the bullet" (8.53) — which wins both by entering at better ranks
AND by not chaining afternoon re-entries.

### S38c — A++ cell: reset-window study + exit sweep (2026-07-30)

**Q1 (user): tighten the leg reset to 5m/10m?** Legs recomputed post-hoc via the
breach counters (semantics note: `breach_N` = BARS SINCE the last strict N-bar-high
breach, so leg anchor = `entry_sec − breach_N`). On the cell the **20m and 10m
resets yield IDENTICAL partitions** (every mid-crash bounce that takes the 10m
high takes the 20m high too) and 5m differs by 2 trips — tightening the window is
a NO-OP on A++ events. What DOES matter is counting within the leg at all:

| rank (within 20m-leg) | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| 1st | 105 | 6.202 | 80.0 | +2.48 | +2.46 |
| 2nd-3rd | 169 | 6.892 | 83.4 | +2.72 | +2.63 |
| 4th-6th | 186 | 10.672 | 88.2 | +3.09 | +2.84 |
| 7th+ | 202 | **12.732** | 78.7 | **+3.68** | +2.94 |

vs the all-day count where 7th+ DECAYED to 7.5: within-leg rank repairs the tail —
"7+ signals with no intervening bounce" = the relentless flush = the fattest fades
(PF/avg now MONOTONE in leg depth; win% dips at 7th+ but payoffs fatten).
"Save the bullet" mc=1 on leg-rank ≥ 4: 71 trips @ 8.532 / win 85.9 — identical
to the all-day version (8.528); the one-clip discipline is insensitive to the
reset choice.

**Q2 (user): exit at 10m/20m highs instead of 5m?** S13 sweep (aux marks;
censoring ⇒ exact form = "target N-high, time-stop 20m", unresolved → `fwd_1200`)
restricted to the cell:

| exit | hit% | win | PF | avg% | med% |
|---|---|---|---|---|---|
| 2m high, ts 20m | 100.0 | 87.2 | 3.259 | +1.52 | +1.95 |
| 5m high, ts 20m | 94.9 | 82.5 | 4.664 | +2.71 | +2.86 |
| 10m high, ts 20m | 67.8 | 71.1 | 2.822 | +2.49 | +3.07 |
| 20m high, ts 20m | 30.1 | 58.3 | 1.842 | +1.74 | +1.09 |
| hold 20m flat | — | 58.3 | 1.912 | +1.89 | +1.09 |
| **ACTUAL (5m, no ts)** | 100.0 | **82.8** | **9.125** | **+3.08** | +2.86 |

**THE EXIT IS UNBEATEN ×5** — and by MORE on the A++ cell than on the book: the
10m target's +0.2% median premium is swallowed by the 32% of trades that never
print a 10m high inside the window (win collapses 83→71→58); even the 5m target
with a 20m time-stop halves the cell (4.66) — the 5% of trades needing >20m are
precisely the monsters. Wider targets convert the highest-win cell in the system
back into drift.

**S38c addendum — shorter than 5m (user):** the only sub-5m mark is the 2m high
(`aux_hi_120`), EXACT on the cell (hit 100%): PF 3.259 / win 87.2 / avg +1.52 —
higher win, half the payoff. By year it is UNSTABLE: **2020 = 1.031 (avg +0.08!)
vs 5m's 7.746** — in relentless waterfalls the prior-2m max drifts below entry
within seconds and the first micro-bounce locks a loss the 5m exit rides through
(S24's "fastest trigger owns the distribution", target edition). 2021 the lone
inversion (2m 7.37 > 5m 3.75). The exit hump peaks at 5m from BOTH sides,
year-audited: 2m 3.26 (unstable) → **5m 9.13 (all years +)** → 10m 2.82 → 20m
1.84. Sub-2m marks don't exist (engine change needed); the gradient + the S26
1m-fee-death verdict say don't bother.

### S38d — THE VIRGIN FLUSH: the first bounce ENDS the setup (2026-07-30)

User hypothesis off the S38b re-entry autopsy: "once the exits have been hit
once, the re-entry trades are a fundamentally different setup" — so reset the
leg at 5m/10m instead of 20m. Two findings:

**1. The reset WINDOW is unfixable-by-construction:** on the full book the
5m/10m/20m leg partitions differ by ~60 legs in 12,100 (and ⚠ `breach_N = −1`
is a NEVER-BREACHED-this-session sentinel — anchor SQL must special-case it, it
manufactures one fake leg per trip otherwise). The rolling 20m max ages out in
≤20 minutes, so any bounce that pauses long enough re-takes ALL the highs at
once — no reset window can separate re-entries from virgins.

**2. The DIRECT test separates them perfectly.** Define virgin = no prior
target-exit bounce in this (cell) tkd before this signal (causal; live-scanner
form: "no 5m-high breach since the day's first cell signal"):

| slice | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| A++ virgin flush | 569 | **14.101** | **88.0** | +3.18 | +2.88 |
| A++ post-bounce | 93 | 3.028 | **50.5** | +2.46 | **+0.29** |
| book virgin | 26,913 | 2.148 | 72.5 | +1.27 | +1.99 |
| book post-bounce | 6,255 | 1.928 | 70.6 | +1.13 | +1.96 |

**USER CONFIRMED: post-bounce trades are a COIN FLIP with a tail (win 50.5,
med +0.29) — the lottery shape.** On the book the effect is mild; it is a cliff
in the A++ cell. This also reinterprets S38c: "leg-rank-1 was the worst bucket"
because it MIXED event-starts with post-bounce re-entries.

**Rank within the virgin flush** (monotone, pristine): 1st 8.26 / 2nd-3rd 9.82 /
4th-6th **20.54 (win 92.6)** / 7th+ 18.07.

**⭐⭐ THE DISCIPLINE: mc=1 × virgin × rank≥4** ("wait for the 4th signal; once
the first bounce prints, done with the name"): **64 trips (~10/yr) @ PF 16.018 /
win 90.6 / avg +3.12** — all 7 years positive (2020 6.29 worst; 2021 23.1, 2025
29.7, three years loss-free). One slot now BEATS the unconstrained cell (9.13).
⚠ n=64 — but every refinement was hypothesis-driven and confirmed at scale
first (rank monotonicity n=662; virgin cliff n=569/93). Sizing pyramid final
rung: 2.1 book → 2.5 dsv → 4.4 corner → 9.2 stack → **16.0 virgin-rank≥4-mc1**.

**S38d method note (user: "how is a post-hoc reset even possible? we only record
ONE leg counter"):** the engine's `NewLowCounters` (20m reset) was never used.
The reconstruction uses the seven recorded high-side `BreachCounter`s
(`Intraday.fs:110-120`): `breach_N` = bars since the last strict N-high breach
(−1 = never this session; breach bar = 0). On 1s bars that recency converts to
an absolute anchor `entry_sec − breach_N` = the second of the last breach;
same-anchor trips share a leg ⇒ `row_number() PARTITION BY (tkd, anchor)` =
within-leg rank under an N-reset, no engine change. ⚠ Caveats: (1) the −1
sentinel; (2) `Step()` counts PRESENT bars vs wall-clock `entry_sec` — missing
tape seconds shift the anchor late and can over-split legs on illiquid names
(exact on A++ torrent tapes; common-mode across the three reset variants). The
VIRGIN test is independent of all this: it reads other same-tkd trips' recorded
`exit_reason='target', exit_sec < signal_sec` — the mc=0 sampler holds positions
from every signal second, so a prior target exit IS the bounce evidence.

## S38e — engine v9: TRUE lows-into-leg twins at 5m/10m resets (2026-07-30)

User (correcting S38d's proxy): the post-hoc "rank" counted RECORDED TRIPS, not
new-low events — the engine's `LowsSinceFirstLow` counts every strict new 20m
low including ones that fail spec gates. **Engine v9:** two more `NewLowCounters`
(`counters300`/`counters600`) — same arming event (strict new 1200-bar low),
disarmed by a strict new 300/600-bar high instead of 1200 (`br300.BarsSinceBreach
= 0` on the breach bar; nested windows ⇒ a 20m breach resets all three). Four
record-only columns: `bars/lows_since_first_low_300/_600`. Also DELETED (user):
`MinLowsIntoLeg` + the `legConsumed` latch — the V6 F3 one-trip-per-leg engine
book mode is dead machinery now that books are built post-hoc by mc-replay
(behavior-neutral at the K=0 default). Restricted rerun on `flushfader_v13_tkds`
→ `data/equity/flushfader/v13_legs/` (**THE working parquet**, 131 cols):
50,910 trips, ZERO set-diff vs v13_reference, invariant lows_300 ≤ lows_600 ≤
lows clean on all trips.

**lows_since_first_low_300 on the A++ cell** (the 20m counter is spec-pinned to
[26,50] by the K-band; the 10m twin barely discriminates — 10m ≈ 20m resets):

| lows_300 | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| 0-5 (fresh 5m leg) | 32 | **0.606** | **50.0** | **−0.74** | −0.07 |
| 6-15 | 76 | 14.384 | 90.8 | +2.92 | +2.87 |
| 16-25 | 63 | 83.446 | 95.2 | +4.10 | +3.81 |
| 26-40 | 377 | 9.556 | 84.4 | +3.24 | +2.66 |
| 41+ | 114 | 13.625 | 74.6 | +3.17 | +2.88 |

**The virgin finding is READABLE OFF ONE ENGINE COLUMN**: a fresh 5m leg =
just-bounced = the losing slice. Crossed with the virgin flag, the toxic core is
the conjunction: post-bounce × fresh-5m-leg = **PF 0.114 / win 22.2 (n=18)** —
actively harmful; post-bounce × deep = the lottery (5.70, win 56.3); virgin ×
deep = the setup (14.19, win 87.6).

**Discipline menu (mc=1 on the A++ cell), simplicity vs peak:**

| discipline | n | PF | win% | avg% |
|---|---|---|---|---|
| naive greedy | 102 | 6.216 | 79.4 | +2.50 |
| lows_300 ≥ 6 (ONE engine feature) | 96 | 8.188 | 82.3 | +2.68 |
| cell-signal rank ≥ 4 | 75 | 8.528 | 85.3 | +3.01 |
| virgin × rank ≥ 4 | 64 | 16.018 | 90.6 | +3.12 |

## S38f — K-band counter experiments: stack vs wholesale replacement (2026-07-30)

**Stacking (user) — lows_300 ≥ 6 on top of the v1.3 spec** is EXACT post-hoc
(pure tightening). Book buckets: lows_300 0-5 = 1.706 (n=2,120), 6-15 = 2.105,
16-25 = 2.111, 26-40 = 2.296, 41+ = 1.995 (10m ctr: 0-5 = 1.142 on n=213, rest
flat). Stacked book = 31,834 @ 2.184 vs 2.152 — but the year audit is a WASH
(2020/21/25 up, 2023/24/26 down, 2022 flat). **Verdict: NOT a book gate — the
fresh-leg poison is an A++-cell phenomenon** (0.61 there vs 1.71 on the book);
stays a playbook overlay. Same lesson as the torrent corner: the signal
concentrates where the crowd is.

**Wholesale replacement (user) — point the K band at lows_300/lows_600 instead
of the 20m counter:** mixed tightening+relaxation (lows_300 ≤ lows_20m: drops
fresh-leg trips, ADMITS deep-5m-leg trips with 20m count > 50 that no existing
parquet contains) ⇒ per the S25/S31 method rule needs the full universe.
`v13_koff/` baking: full 392,815-tkd run, `--k-band-lo 0 --k-band-hi 0`, all
other v1.3 gates intact, ~3x trip rate. Post-hoc plan: (1) parity — lows_20m ∈
[26,50] must reproduce the v13 book; (2) bands on lows_300/lows_600; (3) year
audits + A++ interplay.

**S38f completed — the wholesale verdict (v13_koff landed: 150,817 trips @
1.683, 59 min; ⭐ PARITY: lows_20m ∈ [26,50] on it reproduces the v13 book
EXACTLY — 33,954 / 2.152 / 72.4):**

| K band [26,50] on | n | PF | win% | med% | 2022 | worst recent |
|---|---|---|---|---|---|---|
| lows_20m (baseline) | 33,954 | **2.152** | 72.4 | +2.00 | **1.432** | 1.678 |
| lows_10m | 38,599 | 1.936 | 72.4 | +2.02 | 1.383 | 1.449 |
| lows_5m | 34,978 | 1.915 | 73.0 | +2.15 | **1.014** | 1.474 |

**The 20m-reset counter IS the right K clock.** Mechanism: the band's CEILING
(≤50) rejects stale over-extended legs — with a 5m reset any small bounce wipes
the count, so the ceiling stops seeing leg age and re-admits exactly the
exhausted 2022-style declines the band was built to exclude (K5's 2022 = 1.014
vs 1.432). The 5m counter's job is the OTHER end: fresh-leg exclusion in the
A++ playbook (S38e). Division of labor: **K-band on the 20m clock = spec;
lows_300 ≥ 6 = A++ overlay.** `v13_koff/` (131 cols, K-free) is the reusable
universe for any future K experiment.

### S38g — combining lows_20m × lows_5m (2026-07-30)

2D grid on `v13_koff` ($1≤px<$10): the K20 [26,50] row dominates EVERY lows_300
column (2.1-2.2 vs 1.0-1.8 in all other rows) — the 20m clock's primacy is 2D.
Inside the spec row lows_300 is mildly monotone: 0-5 = 1.706 / 6-15 = 2.105 /
16-25 = 2.111 / 26+ = 2.219 (the S38f stacking wash).

**The equality flag `lows_300 = lows_20m` = NEVER-BOUNCED LEG** (engine-native:
counts agree iff no 5m-high breach since the leg began). On the spec band:
16,042 @ 2.337 vs bounced 17,912 @ 1.917 — and it RESCUES the weak years
(2022: 1.74 vs 1.21; 2023: 1.88 vs 1.53; 2025: 2.78 vs 1.56) but INVERTS 2024
(1.83 vs 2.26) and 2026 (1.64 vs 2.20). Fails the every-year bar → a regime
lens, not a gate. On the A++ cell it does NOT reproduce the virgin cliff
(never-bounced 408 @ 8.25 vs bounced 254 @ 11.01 — no separation): leg-scoped
purity ≠ day-scoped "no target exit yet"; the cliff belongs to the DAY's first
capitulation episode. **Division of labor stands: K-band on the 20m clock =
spec; lows_300 = recency (fresh-leg exclusion, A++ overlay); the day-scoped
virgin flag = the discipline.**

**S38g addendum — why never-bounced can't rescue mc=1 (user Q):** mc=1 ×
`lows_300 = lows_20m` on the A++ cell = 58 @ 6.472 ≈ naive greedy (6.216), far
below the virgin discipline (16.0). Cross-tab virgin(day) × never-bounced(leg):

| virgin | nb | n | PF | win% |
|---|---|---|---|---|
| yes | yes | 373 | 11.109 | 83.1 |
| yes | no | 207 | **25.881** | **95.7** |
| no | yes | 35 | **1.841** | **28.6** |
| no | no | 47 | 3.991 | 63.8 |

**LEG AMNESIA:** on A++ events exit-triggering bounces almost always take the
20m high (S38c) → the leg RESETS → both counters restart together → the flag
reads pure again on second-episode re-entries (no-virgin × nb = 1.84 @ 28.6 =
the toxic re-entries WEARING the purity flag). It also penalizes the best trips:
pre-first-signal mid-leg bounces make virgin days read nb=false, and those are
the monsters (25.9 @ 95.7, n=207). The two flags run different CLOCKS — leg vs
day — and the poison is day-scoped. The day-scoped virgin flag stays THE
discipline.

**S38g synthesis — THE TWO RE-ENTRY TYPES (user's mechanism confirmed):**

| type | mechanism | counter signature | n | PF / win |
|---|---|---|---|---|
| fast chase | 5m bounce WITHOUT 20m reset → K still satisfied from old lows → re-signal in seconds | post-bounce × lows_300 ≤ 5 (leg intact by arithmetic: a reset would have zeroed K) | 18 | **0.114 / 22%** |
| second episode | 20m bounce → leg reset → 26-low cooling-off → new episode matures | post-bounce × lows_300 ≥ 6 (often nb=true — amnesia) | 64 | 5.70 / **56%** (lottery) |

User: "re-entries are bad because they landed before the reset had the chance
to trigger" — EXACTLY type 1, the actively toxic kind; `lows_300 ≥ 6` plugs
precisely that gap (mc=1: 6.22 → 8.19). Type 2 slips every leg-scoped counter
(after a full reset a second capitulation is indistinguishable from a first on
leg clocks — the K-band is structurally episode-blind); only the day-scoped
virgin flag excludes it (8.19 → 16.0, win 82 → 91). The discipline ladder is
now MECHANISM-NAMED: naive 6.22 → +fresh-leg gate 8.19 (kills the fast chase)
→ +day-virgin × rank≥4 16.0 (kills the second-episode lottery).

**S38g correction (user) — the lows_300 shape is a HUMP, not monotone:** finer
buckets, K-band ON (spec book) / OFF (whole koff universe): 0-5 = 1.706/1.437,
6-15 = 2.105/1.641, 16-25 = 2.111/1.769, **26-40 = 2.296/2.007 (peak)**, 41-60 =
1.995/1.655, 61+ = —/1.289 (impossible inside the band). Earlier "monotone" came
from the grid's coarse 26+ bucket blending peak and dip. The hump SURVIVES the
K-band (same shape, gentler — the band absorbs part of it), and both clocks
agree on the ~26-40 sweet zone incl. the over-extension roll-off past ~40-50;
inside the band the 41-60 bucket = the near-equality (never-bounced deep)
region, dipping to 1.995 — the S38g regime lens seen from another angle.

## S38h — SPEC v1.4 BAKED: + lows_300 ≥ 6 (engine v10, 2026-07-30)

**SPEC v1.4 = v1.3 + `lows_since_first_low_300 >= 6`** (user decision: book-level
wash but it kills the FAST-CHASE re-entry — the actively toxic slice — and a live
scanner shouldn't chase 5m-bounce re-signals). Engine v10: `MinLows300` config
(default 6, 0 = off), `--min-lows-300` flag, gate `lows300Ok` in specOk mirrors
the recorded column exactly. Restricted rerun (pure tightening ⇒ `flushfader_
v13_tkds` exact) → **`data/equity/flushfader/v14_reference/`** = THE working
parquet (47,909 raw trips; ⭐ ZERO-DIFF parity vs the SQL cut on v13_legs).

| book (≥$1, <$10) | n | PF | win% | med% |
|---|---|---|---|---|
| v1.3 | 33,954 | 2.152 | 72.4 | +2.00 |
| **v1.4** | **31,834** | **2.184** | **72.7** | **+2.04** |
| A++ cell v1.3 | 662 | 9.125 | 82.8 | +2.86 |
| **A++ cell v1.4** | **630** | **11.795** | **84.4** | **+2.93** |

⏭ OPEN: how to handle the SECOND EPISODE (post-reset re-entries, the 5.70/56%
lottery — needs day-scoped state; the virgin flag is the post-hoc form).

## S38i — the virgin feature on the whole book: it's a CONJUNCTION effect (2026-07-30)

**The differentiator (user Q): prior-target-exit count** = # of distinct
`exit_sec` with `exit_reason='target'` in the same tkd strictly before this
trip's signal. Causal, blotter-native ("was I paid out on this name today?"),
and ≈ tape-native (the sampler holds from every signal, so first target exit ≈
first 5m-high breach after the day's first signal).

**On the v1.4 book: FLAT** — 0 bounces 2.203 / 1 → 2.12 / 2 → 1.845 / 3+ →
2.757 (n=74). Not a book feature. By liquidity tier, still flat-to-inverted
([10,30M) even inverts: post-bounce 2.53 > virgin 2.04). The cliff only
assembles in the FULL A++ conjunction:

| torrent (≥30M×≥30M) | intact (dsv ≥ −3%) | virgin | n | PF | win% |
|---|---|---|---|---|---|
| yes | yes | yes | 558 | **15.051** | **88.2** |
| yes | yes | no | 72 | 4.636 | **55.6** |
| yes | no | yes | 2,678 | 3.983 | 76.7 |
| yes | no | no | 234 | 4.171 | 78.2 |

**Below vwap the episode count means NOTHING (3.98 vs 4.17); at vwap it is the
whole game (15.1 vs 4.6, win 88 vs 56).** Mechanism: on an intact day the first
violent capitulation is the one the crowd buys; a stock that already bounced
and is flushing AGAIN while still near vwap = sellers reloading — a different
trade. Below vwap it's all the same grind regardless of episode.

**Verdict: virgin/second-episode stays a PLAYBOOK rule for the A++ setup — not
a spec gate.** (A day-scoped `targets_today` engine column would still make the
scanner self-contained; queued as nice-to-have.)

## S38j — SECOND-EPISODE CENSUS: it's 6 events. Claims downgraded (2026-07-30)

Before feature-hunting the virgin/second-episode divide (user: dist-20m-high?
volatility?), the event count: **the A++ post-bounce population = 61 trips from
SIX ticker-days in 6.5 years** — EYES 2021-03-05 (25 trips, −52.9%, THE
disaster), VERO 2026-01-16 (17, +174.3%), SERV 2024-07-19 (7, +38.8), GTEC
2020-11-10 (5, +18.9), PPSI 2020-10-06 (4, +16.6), IBRX 2024-10-25 (3, −1.0).

**Downgrades:** (1) "the 5.70/56% second-episode lottery" is NOT a population —
it is 6 events, and the win-rate collapse is mostly EYES; (2) the feature
autopsy (2nd episodes: volat 138 vs 93bp, chg_open +82 vs +37%, dsv +7.0 vs
+3.6 — hot/extended/high) describes those same 6 days — anecdote, not signal;
(3) the striking hot/cool interaction (post-bounce×hot = 0.67/26% vs
post-bounce×cool = 206/89%, with hot ≡ extended as identical partitions) is
3 events vs 3 — DO NOT TRADE ON IT. House rule applied: count events before
profiling features.

**What stands:** virgin side = 569 trips / 90 events / win 87.7 (fully
powered); book-level episode count = FLAT (S38i, well-powered); **"first
episode only" survives as CHEAP INSURANCE, not measured edge** — ~1 second
episode/yr, ex-EYES they were fine, but the rule costs ~nothing and caps a
demonstrated −53% tail day. Risk argument, not alpha argument. The "defining
feature between episodes" question is UNANSWERABLE at A++ scale.

**S38j footnote (user: "how does a 5m-high target pay +174%?"):** sumret = Σ over
17 mc=0 averaging-down trips (per-trip +2..+13%). Per-trip double-digits are the
5m target's WATERFALL BEHAVIOR: VERO flushed 9.16→8.68 in ~100s, so the
strictly-prior 300-bar max still remembered ~9.9 — `exit_chan_hi` ask at signal
= +9..+14.5% (recorded in tgt_dist) — and the V-snap paid all 17 fills at ONE
shared exit second (3 bounces = 3 cluster exits for the day's 30 trips). The 5m
target is ADAPTIVE: quiet tape → small ask, waterfall → the whole cliff. This is
why the exit is unbeaten ×5 — it scales its ask with flush speed automatically.

## S38k — rng_300/rng_20m refreshed on v1.4 (user recall of S14; 2026-07-30)

S14 (v1.1 book): no lever. On the v1.4 book a gradient has EMERGED: <0.25 =
3.027 (n=1,516) → 0.25-0.45 = 2.382 → 0.45-0.65 = 1.999 → 0.65-0.80 = 1.763,
≥0.80 = 2.099 — **back-loaded flushes (the conclusion of a longer 20m decline)
fade better than front-loaded ones (the flush IS the decline)**; rhymes with
S14's ≥17%-ask wall since fr ≈ how much of the cliff the 5m target spans. Not
gated (last bucket breaks monotone; overlay candidate at best). On the A++
cell: all buckets 10-58 PF except ≥0.80 = 0.384/38% — but census = 5 events
(IBIO 2020-02-28 −39.9 drives it) ⇒ ANECDOTE per the S38j rule, noted not acted.

**S38k tables (user; full data). v1.4 book, mc=0:**

| rng_300/rng_20m | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| <0.25 (back-loaded) | 1,516 | 3.027 | 76.8 | +1.45 | +2.12 |
| 0.25-0.45 | 13,057 | 2.382 | 72.4 | +1.28 | +1.90 |
| 0.45-0.65 | 11,767 | 1.999 | 72.8 | +1.22 | +2.09 |
| 0.65-0.80 | 3,577 | 1.763 | 70.9 | +1.18 | +2.10 |
| ≥0.80 (pure cliff) | 1,287 | 2.099 | 69.5 | +1.65 | +2.36 |

By year (PF): the gradient is a 2021-2023 phenomenon and INVERTS in 2024:

| yr | <0.25 | 0.25-0.45 | 0.45-0.65 | 0.65-0.80 | ≥0.80 |
|---|---|---|---|---|---|
| 2020 | 4.93 | 4.81 | 4.04 | 2.84 | 4.01 |
| 2021 | 6.17 | 2.74 | 2.40 | 2.38 | 3.11 |
| 2022 | 2.39 | 2.01 | 1.31 | 1.08 | 0.59 |
| 2023 | 5.65 | 2.60 | 1.31 | 1.07 | 1.11 |
| 2024 | 1.67 | 1.87 | 1.91 | 2.40 | 2.85 |
| 2025 | 2.77 | 2.10 | 2.03 | 1.64 | 2.78 |
| 2026 | 2.00 | 2.30 | 1.67 | 1.60 | 2.43 |

A++ cell (630):

| ratio | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| <0.25 | 32 | 58.302 | 90.6 | +2.19 | +2.24 |
| 0.25-0.45 | 243 | 23.798 | 91.4 | +2.90 | +2.82 |
| 0.45-0.65 | 214 | 10.249 | 77.1 | +3.75 | +4.35 |
| 0.65-0.80 | 107 | 33.468 | 96.3 | +4.86 | +4.15 |
| ≥0.80 | 34 | 0.384 | 38.2 | −1.02 | −1.07 |

A++ ≥0.80 census (ANECDOTE): IBIO 2020-02-28 18/−39.9 · KIDZ 2025-05-02 5/−13.4
· NURO 2021-07-20 9/+12.8 · BBAI 1/+2.1 · AIRI 1/+3.7.

**mc=1 on v1.4 (greedy): 4,248 @ 2.070 / win 71.8 / med +1.85** — ⭐ the
lows_300 ≥ 6 gate lifts the SINGLE-SLOT book more than the mc=0 book (v1.3 mc=1
was 2.004): it kills exactly the chained fast-chase re-entries greedy used to
take. fr buckets at mc=1 turn CLEANLY MONOTONE incl. the last bucket:

| ratio | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| <0.25 | 266 | 2.374 | 69.5 | +1.10 | +1.70 |
| 0.25-0.45 | 1,819 | 2.244 | 71.4 | +1.09 | +1.70 |
| 0.45-0.65 | 1,514 | 2.003 | 73.4 | +1.15 | +2.05 |
| 0.65-0.80 | 479 | 1.957 | 71.8 | +1.21 | +1.93 |
| ≥0.80 | 170 | 1.508 | 64.7 | +0.84 | +1.78 |

The mc=0 ≥0.80 bounce-back (2.099) was AVERAGING-DOWN TAIL RESCUE — first-trip-
only (greedy) shows the pure cliff at 1.508/64.7. Still regime-caveated by the
year table; overlay candidate, not a gate.

## S38l — SPEC v1.5 BAKED: + rng_300/rng_20m < 0.8 (engine v11, 2026-07-30)

**SPEC v1.5 = v1.4 + `rng_300/rng_20m < 0.8`** (user: reject the pure cliff; also
clips the A++ ≥0.80 bucket). Gate `frontOk` mirrors `chanRng max300 min300 /
chanRng max1200 min1200` — NaN/zero-range denominators FAIL, matching SQL
`nullif` semantics. `MaxRngFront` config (default 0.8, Infinity = off),
`--max-rng-front`. Restricted rerun → **`v15_reference/` = THE working parquet**
(45,730 raw; ⭐ ZERO-DIFF parity vs the SQL cut on v14_reference).

| book (≥$1, <$10) | n | PF | win% | med% |
|---|---|---|---|---|
| v1.4 | 31,834 | 2.184 | 72.7 | +2.04 |
| **v1.5** | **30,513** | **2.191** | **72.9** | **+2.03** |
| A++ cell v1.4 | 630 | 11.795 | 84.4 | +2.93 |
| **A++ cell v1.5** | **596** | **16.548** | **87.1** | **+3.13** |

Year audit v1.5 (vs v1.4): 2020 4.097 (4.094) / 2021 2.635 (2.665) / **2022
1.564 (1.442)** / **2023 1.685 (1.643)** / 2024 1.956 (1.997) / 2025 2.023
(2.052) / 2026 1.829 (1.851) — buys back the weak years, gives a little back
in 2024-26 (as the S38k year table predicted). mc=1 book: **4,137 @ 2.106**
(v1.4 mc=1 was 2.070; v1.3 2.004 — each spec rung lifts the single-slot book).

**S38l mechanics note (user: what if the 20m window isn't full?):** unreachable —
the entry gate requires the 1200-bar channel FULLY WARM (a "new 20m low" is
undefined before 1,200 present bars), so frontOk always sees full windows;
verified: earliest signal in the book = 09:49:59 (= 34,200 + 1,199s, the first
possible warm second), zero NaN/degenerate ranges. If the NaN branch somehow
fired, NaN comparisons are false ⇒ trip REJECTED (fail-safe). Corollaries:
(1) the 09:45-09:50 entry-window slice is empty BY CONSTRUCTION (inherent to
the 20m-low trigger, predates v1.5); (2) windows count PRESENT bars — on gappy
tapes both windows stretch together, so the ratio always means "share of the
window's range in its last quarter" by bar count.

## S38m — THE COLD-PERIOD STUDY (user: should 09:45-09:50 be tradable?; 2026-07-30)

Clarification first: **the blocker is the ENTRY DEFINITION, not a gate** — "new
1200-bar low" cannot exist before ~09:50 (earliest signal ever: 09:49:59), so
the eff_20m cold-pass never enabled 09:45-09:50; it enables cold-EFF trips
after 09:50 (thin tapes whose slot features warm late). rngfront adds NO extra
early blocking (channelWarm ⇒ full rng windows).

Early book (v1.5): 09:50-09:55 = 1.837 / 09:55-10:00 = 1.728 (THE two weakest
slices of the day) vs 10:00-10:15 = **3.005** / 10:15-10:30 = 2.185 / 10:30+ =
2.140. The cold-pass carries 471 trips @ 1.777 / win 67.1 / med +1.52 (median
~09:53) vs warm 30,042 @ 2.199 — the weak fringe, positive, harmless, not
treasure. **The A++ cell has ZERO trips before 10:00** (6 in 10:00-10:30, rest
10:30+): the torrent needs the day to develop.

**Verdict: leave it.** Opening 09:45-09:50 needs a session-low warm-up channel
(+ cold semantics for K/dist/rngfront — a redesign) aimed at the extrapolation
of the day's WEAKEST gradient (honest 09:45-10:00 was 1.90 in S31). The eff
cold-pass stays as-is (positive fringe, deliberate user decision, A++-irrelevant).

**S38m addendum — WHY eff_20m warms after the 1200-bar channel (user):** same
present-bar clock, different thresholds. Channel: 1,200 present bars. eff_20m:
40 completed slot RETURNS; slots complete every 30 PRESENT bars (`SlotBars` =
30 counts bars, not wall seconds) and the first slot yields no return ⇒ 41
slots ≈ **1,230 present bars**. The cold-pass therefore governs a ONE-SLOT
(30-present-bar) warm-up gap: dense tape = 09:50:00→09:50:30; gappy tape = the
gap wall-clock-dilates (median cold-eff trip ~09:53 = names sitting at ~1,200-
1,229 present bars then). volat_20m is immune (EWMA emits from the 2nd slot;
~39 returns by channel-warm).

## S38n — SPEC v1.6 BAKED: cold eff_20m FAILS, 40-interval eff KEPT (engine v13, 2026-07-30)

The 39-interval alignment variant (engine v12) was built, run on the full
universe (`v16_39interval_rejected/`, kept as scaffolding), and REJECTED:
30,686 @ 2.139, worse in 5/7 years (2022 1.419 — giving back the rngfront
gain), churn in @ 1.749 vs churn out @ 2.106 — the shifted eff values admit a
worse marginal population through the [−0.5,−0.3) band. User verdict: the cold
pass was a mental-model error (thought it guarded 09:45+; it guarded a 30-bar
slot warm-up gap) — drop the fringe, keep the estimator.

**SPEC v1.6 = v1.5 + cold eff_20m FAILS** (40-interval eff untouched; the
one-slot warm-up gap stays, documented). Pure tightening ⇒ restricted rerun
exact → **`v16_reference/` = THE working parquet** (44,995 raw; ⭐ ZERO-DIFF
parity vs `eff_20m IS NOT NULL` on v15).

| | n | PF | win% | med% |
|---|---|---|---|---|
| book (≥$1,<$10) | 30,042 | **2.199** | 73.0 | +2.04 |
| A++ cell | 596 | 16.548 | 87.1 | +3.13 |
| mc=1 book | 4,062 | 2.104 | 72.0 | +1.84 |

Years: 4.109 / 2.674 / 1.546 / **1.761** / 1.944 / 2.018 / 1.820 — the fringe
drop HELPS 2023 (1.685 → 1.761); A++ cell identical (cold-eff never touched it).

**Mechanics note (user Q): what stops degenerate channels?** `MinMa`/`MaxMa` are
greedy (State from the first push) — the ONLY guard is the explicit
`channelWarm = entryMin.Count = entryMin.WindowSize` (`Intraday.fs:995`) in the
entry gate. The 300/600 windows need no own check (⊂ the 1200 window ⇒ warm by
implication at any signal); record-only paths handle cold via NaN. One line,
load-bearing.

**SPEC v1.6 REFERENCE CARD (user request — the full filter list in one place):**

*Universe funnel:* `mr_candidate` preconditions (CS/ADRC; 09:30-09:45 median
1m-bar volume ≥ 10k with ≥10 of 15 bars present) → engine streams the tkd's 1s
slim bars → **`dv_0945_tape` ≥ $3M** (Σ vwap·volume over own 1s bars strictly
before 09:45 — the honest, 1s-native morning-dollars floor, S35).

*Mechanics:* ENTRY = vwap strictly < prior 1200-bar MIN (needs 1,200 present
bars ⇒ earliest signal ~09:50), fill NEXT bar vwap; EXIT = vwap strictly >
prior 300-bar MAX, else MOC; NO stops; entry window 09:45-15:00 ET; per-bar
floors dv_60 ≥ $100k AND tc_60 ≥ 60 at the signal.

*SPEC gates (all at the signal bar):*
1. `volat_20m` ≥ 40 bp/30s (slot-EmaHl vol floor)
2. speed: vwap/vwap_60_prev − 1 < −2% (1m flush speed)
3. K-band: `lows_since_first_low` ∈ [26, 50] (20m-reset leg; THE 2022 fix)
4. `eff_20m` ∈ [−0.5, −0.3) — **COLD FAILS (v1.6)**
5. |`eff_10m`| ≥ 0.15 (no flat 10m tape)
6. dist-20m-high: vwap/chan_hi − 1 ∈ [−35%, −10%)
7. vol10rate: (vol_10/10)/(vol_60/60) ≥ 0.75 (tape prints through the low)
8. `lows_since_first_low_300` ≥ 6 (v1.4 — kills the fast-chase re-entry)
9. `rng_300/rng_20m` < 0.8 (v1.5 — no pure cliffs)

*Post-hoc:* $1 ≤ entry_px/adj_ratio < $10 (raw-price band; ceiling not baked).

## S38o — A+ family overlap + priority-mc=1 (2026-07-30, autonomous while user away)

**1. FAMILY OVERLAP (v1.6 book): the four A+ overlays are NEARLY DISJOINT —
"one cell wearing four hats" is WRONG.** Jaccards 0.03-0.16. Refresh on v16:

| overlay | n | PF | win% | verdict |
|---|---|---|---|---|
| A dsv ≥ −3% | 2,053 | **4.069** | 79.5 | ⭐ THE overlay — all 7 yrs (2.77 worst, 14.35 in 2026) |
| B rvol_0945 < 1× | 4,650 | 2.916 | 74.6 | DECAYING: 2024-26 = 2.14/2.48/**1.64** → demote to regime lens |
| C chg_3d [−10,0) | 5,896 | 2.185 | 73.1 | ABSORBED by the spec ladder (≈ book) → retire |
| D flow [0.3,1) (honest denom) | 15,662 | 2.181 | 73.1 | ABSORBED (was 2.44 on the contaminated denominator) → retire; the queued honest dv-axis re-check is hereby DONE for the hump |

Pairwise intersections: A&B = 5.62 (n=284), A&C = 5.85 (n=494) — but the A&B
year audit has n = 6-91/yr and **2023 = 0.68** → anecdote-tier (S38j rule), not
promotable. **The A+ roster collapses to: dsv ≥ −3% (the one robust overlay) +
the A++ conjunction.** Pyramid simplifies: book 2.2 → dsv 4.1 → torrent corner →
A++ 16.5.

**2. PRIORITY-mc=1: "keep the powder dry" LOSES.** mc=1 restricted by grade
(v1.6 book, greedy within grade):

| slot policy | n | PF | avg%/trip | Σret pts/yr |
|---|---|---|---|---|
| naive greedy (all book) | 4,062 | 2.104 | +1.12 | **~696** |
| dsv-only | 369 | 2.908 | +1.47 | ~83 |
| torrent-only | 484 | 3.581 | +1.87 | ~138 |
| A++-stack-only | 92 | 8.169 | +2.71 | ~38 |

Selectivity buys 1.4-2.4× per-trip quality at 5-18× fewer trips — the slot
idles ~85-95% of its opportunity. AND the naive slot already attends **71 of
86 A++ events (83%)**; what it cannot do is extract the event's DEPTH (captures
207 of the cell's 2,072 mc=0 pct-pts — the cell's value is the averaging-down
CAMPAIGN, ~7 trips/event). **Verdict: the priority lever is not slot
allocation, it is SIZING — trade naive greedy at base size and size up the
A++ arrivals (the pyramid), exactly the playbook structure. Priority-mc=1
CLOSED.**

## S38p — PRODUCTION PREP: retiring the 1m-bar funnel (2026-07-30, prep for next session)

User direction: move everything to 1s bars (the live scanner builds those);
experiment with relaxing/replacing the `mr_candidate` (A) precondition (1m-bar
medians) — maybe trade counts; find a use for the gap counters. Groundwork laid:

**1. What the funnel's 1m/daily dependencies actually are.** The ONLY 1m-bar
condition that GATES today is precondition (A): median 09:30-09:45 1m-bar
volume ≥ 10k AND ≥10/15 bars present. Everything else gating is already
1s-native (`dv_0945_tape` ≥ $3M) or daily-corpus (CS/ADRC, D-1 adj_close ≥ $1,
episode warm >21d, adj_ratio/prev_adj_close/close_3d). `rvol_0945_honest` +
forward closes are record-only (research), not live-needed. Daily-corpus fields
are live-trivial (any EOD feed); (A) is the one production-parity risk.

**2. The (A) blind spot, quantified (2025 sample, minute_aggs, CS/ADRC):**
246,239 tkds had ≥$3M morning dollars; **146,845 (60%) FAIL (A)** — the
share-count median is structurally a tax on EXPENSIVE names. But split by
price: blind spot = 146,415 at ≥$10 vs **430 sub-$10 (2.1% of the 20,165
sub-$10 dollar-qualified)**. For THE SYSTEM (the $1-$10 band) (A) is nearly
harmless — and the 430 it drops are the thinnest of the band (median bar-vol
7,937 = just under the line; median morning trade count 1,147 vs passers'
3,538; 113/430 fail on bar presence).

**3. The 1s-native replacement proposal (next session's experiment):** rebuild
`mr_candidate` with (A) replaced by a DIRECT dollar prefilter (Σ vol·close ≥
~$2M in SQL, deliberately below the engine's exact $3M tape gate) + optionally
a trade-count floor (`tc_0945_tape` = Σ trade_count pre-09:45 — subsumes
bar-presence: user's "replace medians with trade counts"). Sweep the tc floor
post-hoc off recorded cum_tc/tc features. Gap counters (`gap_60/30/15`,
recorded) = the live per-signal tape-thinness analog of bar-presence — candidate
use: record-first study vs the 430-type marginal tapes. Universe growth is
bounded: sub-$10 adds ~430 tkds/yr; the $10+ flood only matters if the price
ceiling is ever lifted.

## S38q — OLS r + slope features (10m/20m), the beach idea (2026-07-30, last run of the day)

**Engine (record-only):** RollingMa's existing `OlsSlopeMa` (user caught the
duplicate before I rebuilt it) on ln(vwap) per present bar, 600/1200 windows —
warm exactly with the channels, no new cold semantics. 4 new columns:
`ols_slope_600/_1200` (ln/bar; ×6e5 = bp/min), `ols_r_600/_1200` (signed
Pearson r = sign(slope)·√R²). **`v16_ols/` = THE working parquet** (44,995;
zero-diff trip parity vs v16_reference). Sanity: r ∈ [−1,1] everywhere, 100%
negative at signals (every signal IS a 20m low), medians −0.88/−0.84. ⭐ Low
correlation with eff (r20↔eff20 = 0.24 band-attenuated, r10↔eff10 = 0.56) —
genuinely new information.

**Book breakdowns ($1-$10):**

| ols_r_20m | n | PF | win% | med% | | ols_r_10m | n | PF | win% | med% |
|---|---|---|---|---|---|---|---|---|---|---|
| <−0.95 | 1,862 | **2.927** | 74.3 | +2.20 | | <−0.95 | 1,638 | **1.435** | 68.2 | +1.48 |
| [−0.95,−0.90) | 9,669 | 2.464 | 74.8 | +2.09 | | [−0.95,−0.90) | 6,544 | 2.019 | 72.8 | +2.28 |
| [−0.90,−0.80) | 11,938 | 2.071 | 72.7 | +2.06 | | [−0.90,−0.80) | 9,924 | 2.435 | 74.2 | +2.17 |
| [−0.80,−0.60) | 5,717 | 1.962 | 70.8 | +1.93 | | [−0.80,−0.60) | 7,576 | 2.425 | 74.3 | +1.99 |
| ≥−0.60 | 856 | 2.091 | 68.6 | +1.73 | | ≥−0.60 | 4,360 | 2.050 | 70.1 | +1.75 |

**TWO-SCALE GRAMMAR: 20m linearity GOOD (monotone 2.93→1.96), 10m perfect
linearity BAD (worst bucket)** — the long decline should be orderly, the recent
leg should NOT be a clean straight slide (straight-recent = ongoing drift, not
capitulation; the S17 quiet-tail at path level). 2D: lin20 × NOT-lin10 = 2.599
on n=11,067 (a third of the book!); r_10m < −0.95 is bad regardless (1.42-1.48).
Slopes: 20m < −100bp/min = 3.186/win 79.1 (steep = good, the flush-speed grammar);
the ≥−10bp/min corner = 0.118 on n=31 (anecdote). **A++ cell INVERTS lin20:
non-linear 20m = 22.58 (381) vs linear = 9.28 (211)** — the cell is the chaotic
everything-at-once crash, not the orderly slide; same sign-flips-with-context
pattern as the flow ratio. ⏭ overlay candidacy (r_10m ≥ −0.95 book overlay;
cell anti-linearity) + year audits = tomorrow, alongside the mr_candidate work.

**S38q full tables (user). Slopes (bp/min), v1.6 book:**

| slope_20m | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| <−100 | 2,751 | **3.186** | 79.1 | +2.50 | +3.40 |
| [−100,−50) | 15,308 | 2.068 | 72.5 | +1.25 | +2.10 |
| [−50,−25) | 11,111 | 2.157 | 72.3 | +1.10 | +1.81 |
| [−25,−10) | 841 | 2.601 | 71.6 | +1.35 | +1.75 |
| ≥−10 | 31 | 0.118 | 25.8 | −8.28 | −4.29 (n=31 anecdote) |

| slope_10m | n | PF | win% | avg% | med% |
|---|---|---|---|---|---|
| <−100 | 7,354 | 1.972 | 71.4 | +1.46 | +2.49 |
| [−100,−50) | 13,460 | 2.414 | 74.7 | +1.37 | +2.10 |
| [−50,−25) | 6,340 | 2.318 | 72.5 | +1.17 | +1.78 |
| [−25,−10) | 2,183 | 1.812 | 70.8 | +0.83 | +1.59 |
| ≥−10 | 705 | 2.066 | 67.2 | +1.02 | +1.62 |

**2D cross (lin20 = r_20m < −0.9; lin10 = r_10m < −0.95):**

| lin20 | lin10 | n | PF | win% | med% |
|---|---|---|---|---|---|
| yes | no | 11,067 | **2.599** | 74.8 | +2.12 |
| yes | yes | 464 | 1.484 | 72.6 | +1.66 |
| no | no | 17,337 | 2.090 | 72.3 | +2.04 |
| no | yes | 1,174 | 1.416 | 66.4 | +1.40 |

**A++ cell:** linear-20m 211 @ 9.276/85.3 vs **non-linear 381 @ 22.584/87.9**
— the cell inverts.

**⏭ PLAN (user): after the S38p mr_candidate 1s-native rebuild, do a FULL RUN
WITH THE SPEC FILTERS OFF and test whether ols_r_600/_1200 can REPLACE the
efficiency ratios** (eff_20m band + |eff_10m| floor) — the low eff↔r correlation
(0.24/0.56) says they measure different things; the honest comparison needs the
gate-free universe (the current book is eff-band-conditioned, so r's full range
is censored).

**S38q footnote (user: how can slope ≥ −10bp/min coexist with dist ≤ −10%?):**
slopes are computed on the **1s present bars** (600/1200-bar windows, pushed in
step 1 — NOT the 30s slot stream), warming with the channels. The flat-slope
bucket is near-definitional: dist ≤ −10% with slope ≈ 0 forces the decline into
the window's TAIL — an L-SHAPE (plateau → late cliff), not a pump-and-dump
(autopsy: chg_open −19%, breach_1200 ~1691 = no recent 20m high, front-loadedness
73% = just under the 0.8 rngfront gate, r_20m −0.28). It is the RESIDUAL
pure-cliff slice rngfront missed, and it is toxic the same way (0.118/25.8%).
Census: 31 trips / 12 tkds, BJDX 2026-06-02 = −242 of the −257 total →
anecdote, no gate — but the natural watch-item for tomorrow's filters-off run
where slope gets its uncensored range.

**⏭ addendum to the plan (user, end of day):** on the filters-off run also test
**ols_slope_1200 as a replacement for rng_300/rng_20m** — the flat-slope
L-shape autopsy suggests slope is the sharper pure-cliff detector (rngfront
missed the 73%-front-loaded BJDX-type collapses); candidates to compare as the
anti-cliff gate: rngfront < 0.8 (current) vs slope_20m < −X vs their union.

## S39 — mr_candidate goes 1s-native: N_eff_shannon replaces the median gate (2026-07-31)

**The rebuild (user design, refined from S38p):** precondition (A) — median
09:30-09:45 1m-bar volume ≥ 10k AND ≥ 10/15 bars, the last 1m-bar dependency —
is replaced by two floors computed on OUR 1s bars (the feed the live scanner
will actually build):

- **`dv_0945_tape ≥ $2M`** — Σ vwap·volume 09:30-09:45, raw dollars ($1M of
  headroom under the spec's $3M engine gate);
- **`n_eff_shannon ≥ 25`** — exp(Shannon H) of the window's 1s volume
  distribution = the effective number of equally-weighted seconds carrying the
  volume, in [1, 900]. Replaces "median + bar count" with one principled
  number. (The user also added rolling `NEffShannon`/`NEffHhi` monoid
  accumulators + an inverse-free two-stack `SlidingAgg` to RollingMa —
  verified against brute force to ~1e-12, commit `d50e581` — for future live
  rolling use; the SQL build uses the same monoid identity
  `H = ln Σv − (Σ v·ln v)/Σv`, no window functions.)

(B) — CS/ADRC × adj_close ≥ $1 × episode warmup — is unchanged. The old
table's `rvol_0945 ≥ 0.1` prune is DROPPED (the $2M absolute floor subsumes
it; rvol stays recorded). New table = **`mr_candidate_1s`** (2020+, the 1s
corpus span) in trading.db — now the ENGINE DEFAULT (`300dba9`);
`FF_CANDIDATE_TABLE` stays as the research override (old universe, restricted
tkd tables). Build: `scripts/equity/build_mr_candidate_1s.fsx`. Layering:
table prefilter dv >= $2M x neff >= 25, engine spec gate dv_0945_tape >= $3M
on top — the $1M margin is post-hoc relaxation room.

**Sanity:** the scan's dv matches the engine's in-stream `dv_0945_tape` on all
6,091 v1.6 tkds — 0 mismatches ≥ 0.1%. Same definition, two implementations.

**Where the floor comes from — the corpus calibration (15.86M tkd, 2020+):**

n_eff_shannon quantiles:

| slice | q01 | q05 | q10 | q25 | med | q75 |
|---|---|---|---|---|---|---|
| old-pass tkds | 6.6 | 18.1 | 25.4 | 42.4 | 72.7 | 125.9 |
| old-FAIL, dv ≥ $1M (blind-spot zone) | 1.4 | 3.3 | 5.3 | 10.5 | 21.2 | 41.5 |
| v1.6 book tkds | 41.3 | 86.2 | 122.1 | 207.9 | 338.6 | — |

So **25 ≈ the old gate's own q10** — on the distribution axis the new floor is
as strict as the old gate's tail; the opening is on the DOLLAR axis (thin-bar
tapes with real money). By dv band, median n_eff rises 15 → 24 → 41 → 87
($1-3M → $3-10M → $10-30M → $30M+): the two floors are correlated but far from
redundant.

**Blind-spot recovery (old-FAIL × dv ≥ $3M × avg px < $10, n = 66,510):**
neff ≥ 10 keeps 96%, **≥ 25 keeps 89%**, ≥ 50 keeps 65%, ≥ 100 keeps 26%.

**Book cost:** neff < 25 touches **14 of 4,138 book tkds** (70 trips, +30.6
pts net — noise), zero A++ members, and one of the casualties is PMT
2020-03-18 **−38.4%** (COVID mortgage-REIT collapse on a 17-neff tape) — the
floor deletes a bomb. Half the dropped tkds are March-2020 REIT/financial
tapes (CIM/PMT/MFA/RWT) — exactly the "handful of prints own the window"
shape the metric exists to flag.

**Metric validation anecdote:** the highest-dv LOW-neff days corpus-wide are
megacap quad-witching Fridays — AAPL/MSFT/NVDA on 2023-06-16, 2023-12-15,
2024-06-21, 2025-12-19, 2026-06-18 with $6-8B windows at neff 2-9: one
expiration print owns the window. The alarm rings where it should.

**Universe size (A' × B), ticker-days/yr (old ≈ 49-74k):**

| year | old | dv≥2M | +neff≥10 | **+neff≥25** | +neff≥50 |
|---|---|---|---|---|---|
| 2020 | 62,327 | 274,469 | 244,053 | **172,057** | 93,993 |
| 2021 | 74,203 | 325,403 | 280,865 | **189,630** | 103,211 |
| 2022 | 60,123 | 287,417 | 252,907 | **177,487** | 100,181 |
| 2023 | 49,336 | 260,286 | 220,671 | **146,108** | 72,994 |
| 2024 | 51,373 | 275,893 | 226,186 | **137,426** | 64,663 |
| 2025 | 62,896 | 324,430 | 281,643 | **181,658** | 89,577 |
| 2026 | 32,557 | 202,323 | 177,163 | **114,755** | 57,214 |

Chosen: **dv ≥ $2M × neff ≥ 25** ≈ 2.5-3× the old streaming load. Both floors
are recorded columns — tightenable post-hoc, and the rebuild is cheap (~2 min
scan) if the 10-25 band ever looks interesting. ⚠ The old-vs-new universes
ROTATE, they don't nest: 70.2% of old-pass tkds survive the new gate (the 30%
casualty is almost all the DV axis — 102k fail $2M vs only 15k failing neff),
while **75.4% of the new universe is newly admitted** — the honest comparison
of anything downstream is a fresh full run, not a diff.

**BUILT:** `mr_candidate_1s` = **1,119,121 rows / 6,891 tickers / 1,641 days**
(2020-01-02 → 2026-07-17), 97s build. The .fsx per-year counts match the
python calibration scan EXACTLY (two independent implementations of the
entropy — window-share form vs monoid-identity form — same answer, free
cross-check). The one corpus day with NO candidates: **2020-03-16**, the
COVID limit-down Monday — the circuit breaker consumed the 09:30-09:45 window
(day's max-dv tape: $867M at n_eff 2.8 = one halted-auction print). The old
gate also produced zero candidates that day; both agree the window was
untradable.

## S39b — OLS overlay YEAR AUDITS on the v1.6 book (queued item 3; run while the reference run streams)

Book = v16_ols, $1 ≤ raw < $10. Four overlay candidates from the S38q grammar,
now with the year columns that decide their fate (PF | excluded-slice PF):

**A: lin-20m `ols_r_1200 < −0.95`** (S38q headline 2.93):

| year | n | PF | win% | avg | excl PF |
|---|---|---|---|---|---|
| 2020 | 245 | 6.136 | 80.0 | +2.16 | 4.047 |
| 2021 | 375 | 3.670 | 77.6 | +1.59 | 2.614 |
| 2022 | 175 | 4.374 | 70.3 | +1.54 | 1.481 |
| 2023 | 224 | 2.602 | 75.0 | +1.47 | 1.713 |
| 2024 | 283 | 6.181 | 84.8 | +2.33 | 1.860 |
| 2025 | 368 | **1.692** | 65.8 | +0.86 | 2.040 |
| 2026 | 192 | **1.477** | 64.1 | +0.68 | 1.845 |
| TOTAL | 1,862 | 2.927 | 74.3 | | |

**DECAYING** — 2025-26 fall BELOW the complement. Same demotion as rvol<1
(S38o): a 2020-24 regime lens, not an overlay. **C: lin20 × not-lin10**
(1,754 @ 2.903) is A with 108 trips shaved — identical decay (2025 1.60,
2026 1.49). **D: anti-lin10 `ols_r_600 ≥ −0.95`** (28,404 @ 2.259) flips
sign year-to-year (2022 complement 0.456 = toxic; 2023/25/26 invert) — a
regime lens at best, no gate.

**B: steep-20m `ols_slope_1200 < −100bp/min`** (⭐ the one that holds):

| year | n | PF | win% | avg | excl PF |
|---|---|---|---|---|---|
| 2020 | 479 | 16.524 | 87.5 | +4.15 | 3.632 |
| 2021 | 232 | 2.474 | 79.7 | +1.67 | 2.688 |
| 2022 | 127 | 3.882 | 77.2 | +2.48 | 1.472 |
| 2023 | 306 | 1.784 | 70.3 | +1.37 | 1.757 |
| 2024 | 594 | 2.075 | 79.3 | +1.77 | 1.921 |
| 2025 | 717 | 3.175 | 76.8 | +2.39 | 1.878 |
| 2026 | 296 | 3.910 | 80.4 | +3.36 | 1.622 |
| TOTAL | 2,751 | **3.186** | 79.1 | | |

Positive every year, worst 1.78, STRENGTHENING into 2025-26 — joins
dsv ≥ −3% (4.07) in the year-robust overlay roster. **The r-vs-slope split
previews the gates-off test: the durable information in the OLS features is
the DESCENT RATE (slope), not the goodness-of-fit (r).** The S38q A++
anti-linearity inversion (non-linear 22.6 vs 9.3) still stands separately —
cell-level, not book-level.

## S39c — rolling 10m/20m N_eff baked into the engine (record-only) (2026-07-31)

**User:** the same effective-count lens, but ROLLING — `n_eff_shannon_600` /
`n_eff_hhi_600` / `n_eff_shannon_1200` / `n_eff_hhi_1200` on the trip record:
the NEff monoid pair inside a `SlidingAgg` (product monoid, the inverse-free
two-stack queue from RollingMa, commit `d50e581`) over 1s-bar VOLUME,
600/1200 PRESENT-bar windows, snapshotted at the signal bar, nan until warm
(same discipline as the OLS features; per-ticker-day instance = no reset
path needed). Commit `c6ed959`.

**Parity:** 8 random spot-check trips × both windows vs brute-force SQL over
the 1s files — worst |diff| = 1.1e-11, and the window is confirmed to end AT
the signal bar inclusive. The candidate-table N_eff (fixed 09:30-09:45
window, S39) and these rolling twins are now the same math at three scopes:
opening-15m, last-10m, last-20m.

**Interpretation guide:** n_eff_1200 ∈ [1, 1200] = effective seconds carrying
the last 20m of volume. Ratio to window size = distributedness; shannon/hhi
ratio > 1 = a few dominant prints on a broad base (concentration alarm).
Breakdown queued on the v16_1s_reference run (in flight: v1.6 spec ×
mr_candidate_1s universe, ~2.4h, the candidate-swap CONTROL).

## S39d — 🛑 LOOKAHEAD CAUGHT (user): the inherited (B) gates were contaminated (2026-07-31)

**User, on my "(B) unchanged" note:** *"you shouldn't have done that. That
would introduce lookahead much like volume did."* Correct — TWO of the
inherited `mr_candidate` (B) conditions fail the knowability clock, and both
were copied into the first `mr_candidate_1s` build:

1. **`day_close >= $1`** — D's OWN close (knowable only at 16:00, gate applies
   at 09:45), and it is the ADJUSTED close, i.e. future-split-dependent — the
   S35 disease in a second home: a $0.30 raw penny name with a future 1:10
   reverse split has adj_close ×10 → ADMITTED because of a corporate action
   that hasn't happened yet, while a name that crashes through $1 and closes
   below is EXCLUDED because of D's own outcome — survivorship in exactly the
   direction that flatters a long-MR book. **REMOVED — no price floor in the
   candidate table at all.** The $1 cut stays where it always belonged:
   POST-HOC on raw `entry_px` (S7c fee wall), knowable at the fill.
2. **`nbars > 21` warmup** — `COUNT(*) OVER (PARTITION BY ticker, episode)` =
   the episode's TOTAL length INCLUDING FUTURE DAYS: bar #5 of an
   eventually-100-day episode passed, bar #15 of a 20-day episode (delisting
   soon) failed — membership conditioned on how long the ticker will SURVIVE.
   **REPLACED with `ROW_NUMBER() > 21`** (D has ≥ 21 prior bars, prior-only,
   live-knowable).

⚠ **The old `mr_candidate` (2003+) carries BOTH of these** — every consumer
(LowFlyer post-hoc SQL, the diprider_v6_candidate copy the older engines
read) has been sampling a universe conditioned on D's close and on episode
survival. Flagged here for the record; those books need a control rerun
before their numbers are quoted again.

**Rebuild:** 1,119,121 → **1,114,792 rows (−0.4% net)** — dropping the price
floor ADDS sub-$1 tkds, the honest warmup REMOVES early-episode days, and
they nearly cancel. A lookahead removal that moves the universe 0.4% is the
disproportion test passing: this is what real plumbing looks like. Reference
run relaunched on the clean table (banner now echoes the candidate table).

**S39d addendum — the sub-$1 anatomy (user: "how did we have $0-1 trades at
all if the table had a $1 floor?"):** the floor was on D's ADJUSTED CLOSE,
never on the entry price — the engine records every trip on an admitted day.
Decomposing the v16_ols raw trips' sub-$1-raw slice (7,710 trips / 879 tkds):

| admission door | tkds | PF |
|---|---|---|
| future reverse split (raw close < $1; in only because adj_ratio ≫ 1) | 787 | 2.164 |
| **bounce (raw close back ≥ $1 — day in because the flush RECOVERED)** | 92 | **8.546** |

The bounce door's 8.5 = pure outcome selection ("buy the flush, given the
day closed back above $1"), and the split door's 2.16 rides on
survived-to-reverse-split names. The $1-$10 BOOK was insulated (post-hoc raw
entry_px cut), but the "sub-$1 = fee-dead" verdict was measured on this
flattered sample — it is even deader than stated. ⚠ **The momentum engines
(SurgeRider/V2, DipRiderV6, PlungeRider, MaxRiderV1) read
`diprider_v6_candidate` = a copy of `mr_candidate` — same two lookaheads.**
Their sub-$1/sub-$2 slices (incl. the 52w-inversion "sub-$2 wreckage best"
corner) were sampled with the bounce door open — inflated exactly where those
systems looked best. Program already closed (2026-07-27), but do NOT quote
its low-price numbers. PlungeRider's shorts are the one place the bias runs
conservative.

## S39e — THE CONTROL RUN: v1.6 on the clean universe (2026-07-31)

Full v1.6 spec × `mr_candidate_1s` (the candidate-swap control, rule 5 of the
lookahead protocol). 51,675 raw trips (~3h). $1-$10 book by year (old = v16_ols):

| year | n old | PF old | n new | PF new |
|---|---|---|---|---|
| 2020 | 5,198 | 4.109 | 5,514 | 3.762 |
| 2021 | 4,948 | 2.674 | 4,944 | 2.884 |
| 2022 | 2,812 | 1.546 | 2,628 | 1.439 |
| 2023 | 3,265 | 1.761 | 3,352 | 1.539 |
| 2024 | 5,270 | 1.944 | 5,597 | 2.044 |
| 2025 | 6,023 | 2.018 | 6,439 | 1.899 |
| 2026 | 2,526 | 1.820 | 3,000 | 1.894 |
| **TOTAL** | **30,042** | **2.199** | **31,474** | **2.148** |

**DISPROPORTION TEST PASSES:** a 75%-new universe moved the book −2.3%.
Trip decomposition (key = symbol,date,signal_sec): **28,550 shared** (95% of
the old book), 2,924 added @ 1.411 (blind-spot thin tapes — modest, positive),
1,492 lost @ 1.262 (below-average — the bugs leaving). The system is
indifferent to the plumbing swap = the plumbing is real this time.

**A++ cell: 596 @ 18.087/88.1** (old 596 @ 16.548 — the equal n is
coincidence: 590 shared + 6 added − 6 lost). The swap's anatomy in miniature:
- **LOST: KIDZ 2025-05-02** — six losing trips (−1.4…−2.4%). The tkd had
  **19 prior bars** (admitted by the OLD warmup via its episode's FUTURE
  length) and old-table day_close **$3,590** on a ~$2 raw name (future
  reverse splits, S35 disease). Both lookaheads on one specimen, holding
  losers.
- **ADDED: EXPR 2021-01-27** (meme-squeeze peak, $101M morning, neff 90.6) —
  the OLD gate rejected it with **8/15 one-minute bars present**: volatility
  HALTS gutted the window and the bar-COUNT requirement read halts as
  illiquidity. The old gate wasn't just blind to thin tapes — it excluded the
  most VIOLENT tapes, i.e. FlushFader's home turf. All added A++ trips won
  (EXPR +1.5…+1.9%, MLGO +3.5%).

## S39f — rolling N_eff BREAKDOWN (user request): a new year-robust axis (2026-07-31)

Book = v16_1s_reference $1-$10 (31,474 trips; zero null n_eff — warm
everywhere the channels are). `n_eff_shannon_1200` buckets:

| bucket | n | PF | win% | avg | med |
|---|---|---|---|---|---|
| [100,200) | 650 | 2.477 | 71.1 | +1.56 | +1.92 |
| [200,300) | 5,337 | 1.925 | 69.6 | +1.10 | +1.90 |
| [300,400) | 9,099 | **1.614** | 71.0 | +0.86 | +1.87 |
| [400,500) | 7,152 | 2.198 | 73.5 | +1.31 | +2.09 |
| [500,600) | 5,001 | 2.349 | 74.7 | +1.38 | +2.04 |
| [600,700) | 2,618 | **4.764** | 79.9 | +2.25 | +2.45 |
| ≥ 700 | 1,616 | **4.537** | 77.9 | +2.29 | +2.76 |

(10m twin: same shape — trough [150,200) 1.747, top ≥300 = 4.322 on 3,710.)

**⭐ `n_eff_1200 ≥ 600` (13% of book, 4,234 trips @ ~4.7) year audit — beats
its complement EVERY year:**

| year | n | in | out |
|---|---|---|---|
| 2020 | 721 | 11.807 | 3.294 |
| 2021 | 1,105 | 4.935 | 2.580 |
| 2022 | 259 | 6.045 | 1.324 |
| 2023 | 333 | 1.968 | 1.491 |
| 2024 | 756 | 4.264 | 1.857 |
| 2025 | 772 | 3.389 | 1.756 |
| 2026 | 288 | 8.662 | 1.690 |

**NOT trade count in disguise:** within the TOP tc_1200 tercile the neff
bands still separate 1.593 → 2.818 → **4.703**; tc_1200 buckets alone are
non-monotone (5-10k = 1.555 is the WORST). N_eff measures how the volume is
DISTRIBUTED across seconds, not how much of it there is.

**Orthogonality (corr on book):** eff_20m 0.08 / eff_10m 0.05 / ols_r ~0 /
vol_1200 0.005 — a NEW axis; volat 0.49, dv_0945 0.41, tc_1200 0.73 (the one
family relative). shannon↔hhi = 0.965 (near-duplicates — one suffices; the
concentration ratio adds little: hump at [1.4,1.6) = 3.31, mild). 20m↔10m =
0.955. A++ × neff: <600 = 14.394/90.7 vs ≥600 = **19.464/87.1** — same
direction inside the cell. Joins dsv ≥ −3% and slope_1200 < −100bp/min in
the year-robust overlay roster; interaction study queued after the gates-off
run.

## S39g/h/i — THE FAST LOOP: base-pass workflow, parallel engine, smoothness N_eff (2026-07-31)

**User redirect: "3h runs are too much to wait."** Three changes, each
zero-diff-verified:

- **S39g — continuation machinery DELETED.** Right-side-of-V closed at S26
  (taker-fee-dead); the 9-counterfactual tracking was pure per-bar drag.
  `ContPosition`/`ContSink`/`cont_trips` gone (d37f3f2). Parity: all shared
  spot-check trips bit-identical.
- **S39h — parallel day loop** (a04b803): N day-workers (default cores−2,
  `--workers`) each with a private in-memory DuckDB connection, finished tkds
  through an unbounded channel into the single sink consumer. A day is the
  natural isolation unit — the trip SET is identical at any worker count
  (full-row EXCEPT parity, both directions), only parquet row order varies.
  Gotcha worth remembering: `task{}` hot-start — with an already-completed
  work channel, `ReadAllAsync` never awaits and worker #1 drains everything
  synchronously; `do! Task.Yield()` first.
- **S39i — `n_eff_ret_20m`/`n_eff_ret_10m`** (user, beach-grade idea #2):
  Shannon N_eff of the |30s-slot log returns| — THE SAME 40/20-return stream
  the eff ratios consume — as a trend-SMOOTHNESS measure and candidate
  REPLACEMENT for the Kaufman ratios. High (→ window size) = movement spread
  evenly across slots; low = gap-and-chop. Scale-invariant, direction-blind,
  same warmth as eff. Parity vs SQL slot reconstruction: 2.7e-13 (1874af6).

**The workflow (user design): ONE base pass, then SQL.** Base pass =
volat_20m ≥ 40bp × new-20m-low entry definition (+ per-bar dv60/tc60 floors),
ALL spec gates off, dv-tape floor off, on the full clean universe. Every
planned spec study (eff↔r, rngfront↔slope, N_eff overlays, K/dist/speed
variants) is a TIGHTENING of that base, and mc=0 trips are independent —
so every spec variant book = a SQL filter over the base parquet, no engine
rerun. `flushfader_base_tkds` (distinct signal tkds × mr_candidate_1s) then
becomes the restricted streaming table for any engine change that DOES need a
rerun. ⚠ Only relaxations of volat≥40 or the entry channel itself escape the
base — those need a fresh base pass (~45 min parallel), nothing else does.

## S39j — the volat PREPASS (user): a lookahead used correctly (2026-07-31)

**User: "group 1s bars into 30s slots in SQL, compute the day's max volatility
as a prepass, trim the low-vol days without running the engine."** And the
user's framing of the principle: *the important thing isn't to avoid
lookaheads — it's to avoid using them inadvertently and incorrectly.*

`max_slot_absr_bp` = the day's MAX |30s-slot log return| (bp), engine slot
definition (30 PRESENT bars, volume-weighted, SecEmitter's bar filter),
now a BAKED column of `mr_candidate_1s` (filled by a per-day loop in the
build script — the one-shot corpus window OOMs a 15GB VM).

**Why the trim is provably trip-preserving:** volat_20m is an `EmaHlMa` =
Σwᵢxᵢ/Σwᵢ over that same |r| stream — a CONVEX COMBINATION — so
volat(t) ≤ day-max |slot r| at every bar. A day with max < 40bp can NEVER
open the volat gate: skipping it changes nothing but wall-clock. The engine
derives the trim from its LIVE `MinVolat20m` (never diverges from the gate;
0.01bp margin absorbs the ~1e-13 slot-reconstruction float noise) and
applies it only when the column exists (override tables may predate it).

**⚠⚠ THE COLUMN IS A WHOLE-SESSION LOOKAHEAD — COMPUTE-ONLY.** Never gate a
book, slice a table, or build a feature on it: that is the avgvol20-class
"how volatile did today turn out" oracle. Labeled as such in the build
script, here, and the engine comment.

**Numbers:** coverage 1,114,792/1,114,792 (every candidate has ≥1 slot
return). Trim at ≥39.99bp keeps 797,030 (**71.5%**; per-year 60.7-78.9%) —
and the trimmed 28.5% skew toward dense quiet tapes, the expensive ones to
stream. **Bound validation: ZERO violations** on the v16_1s_reference trips
(6,811 trip-tkd joins, all volat-gate-passed causally); the MINIMUM day-max
on any actual trip tkd is **131.7bp** — the provable bound is conservative
by >3× (observation only; the trim stays at the provable 40).

**S39j postscript (user): prepass RETIRED from the build script.** The provable
trim bought only ~28.5% of streaming (one ≥40bp slot ≠ a sustained EMA ≥ 40 at
a 20m low — the bound is necessarily loose, see the 131.7bp empirical margin)
while the per-day fill costs ~7 min per rebuild — not worth the maintenance.
The REAL trim is the engine-derived `flushfader_base_tkds` signal-day table.
The column stays in the CURRENT trading.db table (harmless; still ⚠
compute-only) and the engine's auto-trim clause is column-existence-guarded,
so future rebuilds without it just skip the trim.

## S39k — THE BASE PASS LANDS + GRAND PARITY (2026-07-31, autonomous stretch)

Base pass (volat_20m ≥ 40bp × new-20m-low entry, ALL spec gates off, dv-tape
floor off, clean universe): **2,195,361 raw trips / 796,541 tkds streamed /
31 min** at 8 workers (RSS flat ~5GB). The maximal-sampler floor: PF 1.288 /
64.9% win — every gate's value is now measured against this.

Getting it to land took four crashes, each instructive:
- attempts 1-2 (uncapped): true OOM at 82-86% — the straggler tail is the
  heaviest crash days all in flight at once; fixed by RowsPerPart 2M→250k +
  bounded results channel + per-conn DuckDB memory caps.
- attempts 3-4 (capped): deterministic abort/segfault at 14% — **the caps
  made heavy days SPILL, and DuckDB's default spill path is the shared
  relative `.tmp/`; 8 workers collided in the same temp files.** Fixed with a
  private `temp_directory` per connection (ada99c1). ⚠ Worth remembering for
  every future multi-connection DuckDB embedder in this repo.

**⭐⭐ GRAND PARITY: the v1.6 spec applied as PURE SQL over the base parquet
reproduces the engine reference run EXACTLY — 51,675 = 51,675 trips, zero
diff both directions** (key = symbol, date, signal_sec). The workflow is
proven: every spec variant that tightens the base = a SQL query, no engine
rerun. **`flushfader_base_tkds` = 57,208 signal tkds** (5.1% of the candidate
table) is THE restricted streaming table — future engine changes rerun in
~2-3 min.

## S39l — HEAD-TO-HEAD 1: the eff ratios vs their challengers (2026-07-31)

Universe = base + all v1.6 gates EXCEPT the eff pair, $1-$10 book: 57,788
trips @ 1.956. Variants (V1 = current spec):

| year | V0 eff-off | V1 eff pair | V2 band only | V3 ols_r [-.95,-.85) | V4 neffret≥28 | V5 V1+neffret≥28 |
|---|---|---|---|---|---|---|
| 2020 | 3.019 | 3.762 | 3.850 | 4.074 | 3.031 | 4.197 |
| 2021 | 2.824 | 2.884 | 2.914 | 3.135 | 2.643 | 2.732 |
| 2022 | 1.272 | 1.439 | 1.446 | 1.391 | 1.524 | 1.619 |
| 2023 | 1.580 | 1.539 | 1.558 | 1.824 | 1.334 | 1.193 |
| 2024 | 1.918 | 2.044 | 1.917 | 1.809 | 1.856 | 2.025 |
| 2025 | 1.668 | 1.899 | 1.838 | 1.698 | 1.835 | 2.418 |
| 2026 | 1.671 | 1.894 | 1.888 | 2.169 | 1.749 | 2.086 |
| n / PF | 57,788 / 1.956 | 31,474 / 2.148 | 32,544 / 2.120 | 26,647 / 2.130 | 32,111 / 2.015 | 16,114 / 2.311 |

**VERDICT: EFF STAYS.** (a) The whole eff machinery = +0.19 PF for −45% of
trips, and nearly all of it is the eff20 BAND — the |eff10| floor is close to
a no-op (V2 ≈ V1 every year except 2024's +0.13; SIMPLIFICATION candidate,
user call). (b) V3 (OLS-r) ties aggregate but LOSES 2024 AND 2025 — the S39b
recency decay, now confirmed uncensored: r does not replace eff. (c) V4
(smoothness alone) strictly worse. (d) **V5 = eff + n_eff_ret_20m ≥ 28
overlay: 2.311 on half the book, wins 5/7 years, 2025 = 2.418 — but 2023 =
1.193.** Overlay candidate with a wart, not spec material yet. Corr
(neffret20, eff20) = 0.319 — related, distinct. In the S38q two-scale
grammar's uncensored form, perfect 20m linearity ([-1,-.95) = 1.609) is the
WORST negative band — "orderly slide" was an artifact of the censored book.

## S39m — HEAD-TO-HEAD 2: rngfront vs slope as the anti-cliff gate (2026-07-31)

Universe = base + all v1.6 gates EXCEPT rngfront (eff ON): 32,938 trips @
2.149. **mc=0 attribution says rngfront is a NO-OP on the clean universe**
(2.148 with vs 2.149 without, every year within noise) — but **mc=1 replay
says it EARNS ITS SEAT: 2.079 (on) vs 2.025 (off)**, with the gains in the
lean years (2022 1.774/1.650, 2024 1.967/1.835, 2026 1.771/1.709). The
gate's value lives in SLOT ALLOCATION, invisible to mc=0 — the sharpest
example yet of the S38 lesson that books are decided at mc=1.

**Slope is NOT a rngfront replacement — it is not an anti-cliff gate at
all:** slope < −100bp/min keeps only 3,007 trips @ 2.967/med +3.45 (a
steep-flush QUALITY overlay, the S39b axis) and craters 2022 (0.994) at gate
strength; slope < −150 = 418 @ 5.857 with 2021 = 0.041 (tail-bomb). Union
with rngfront = rngfront (overlap ~total). The S38q L-SHAPE residual
(rngfront-kept × slope ≥ −10bp/min) = **32 trips @ 0.118/28.1%** — real,
toxic, and anecdote-sized, exactly as the S38j census discipline predicted.

**NET OF THE DAY'S TWO HEAD-TO-HEADS: SPEC v1.6 SURVIVES UNCHANGED.** The
OLS axes and the N_eff family are overlay/sizing material (slope < −100 =
year-robust 3.19 overlay per S39b; neffret ≥ 28 = 2025-strong overlay with a
2023 wart; volume-N_eff ≥ 600 = the 4.7 all-years overlay per S39f), not
gate replacements. ⏭ user decisions queued: drop |eff10| (V2 simplification)?
adopt any overlay into the sizing pyramid? single-reader engine architecture.

## S39n — THE OVERLAY SCORE: count-of-overlays as the sizing dial (2026-07-31, closing)

The four year-robust overlays on the clean v1.6 book (base-derived, 31,474):
dsv ≥ −3% (2,095 @ 3.615) · slope_1200 < −100bp/min (2,989 @ 2.972) ·
volume-N_eff_1200 ≥ 600 (4,234 @ 4.673) · n_eff_ret_20m ≥ 28 (16,114 @
2.311). Pairwise Jaccard 0.034-0.184 — NEARLY DISJOINT (the S38o lesson
repeats: the A-list is a family of different lenses, not one cell).

**Count of overlays satisfied = a clean additive score:**

| overlays | n | PF | win% | med |
|---|---|---|---|---|
| 0 | 11,858 | 1.746 | 69.7 | +1.83 |
| 1 | 14,893 | 2.161 | 73.6 | +2.09 |
| 2 | 3,717 | 3.148 | 78.3 | +2.36 |
| **3** | **919** | **8.089** | **84.5** | **+3.17** |
| 4 | 87 | 3.339 | 71.3 | +3.57 |

(4-of-4 = 87-trip noise, not a signal.) Year audit of score ≥ 3 (~140/yr):
2020 10.6 · 2021 4.4 · 2022 5.3 · **2023 0.911 (n=27 — the 2023 wart
again)** · 2024 24.4 · 2025 4.8 · 2026 20.7. Six of seven years far above
the <3 remainder. ⏭ THE SIZING-PYRAMID CANDIDATE for the user: overlay-count
as the size dial (base size at 0-2, size up at 3+), replacing per-cell
special-casing; needs the mc=1 replay + A++-interaction pass before adoption.

## S39o — slope ACCELERATION (user: slope_10m − slope_20m) breakdown (2026-07-31)

Universe = v1.6 minus rngfront, $1-$10 book (32,938). `accel` = (ols_slope_600
− ols_slope_1200) × 6e5 bp/min: negative = the decline STEEPENS into the
signal, positive = decelerating. Quantiles: q05 −95.5 / med −15.3 / q95 +47.5.
corr(accel, rngfront) = −0.477 (rngfront's OLS cousin), corr(accel, slope20)
= −0.205. The L-sliver is invisible on this axis too (med −32.6).

| accel bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −150 | 281 | 1.974 | 76.2 | +4.11 | 13.79 | 86.63 | 0.32 | 7.94 | 0.29 | 21.40 | 3.35 |
| [−150,−120) | 447 | 1.086 | 60.9 | +1.48 | −inf | 0.23 | 5.05 | 1.12 | 0.90 | 1.30 | 1.42 |
| [−120,−100) | 668 | 1.320 | 69.8 | +3.03 | 5.05 | 0.64 | 1.04 | 1.32 | 1.28 | 2.51 | 0.79 |
| [−100,−80) | 1,319 | 1.275 | 67.9 | +1.88 | 1.33 | 1.12 | 0.48 | 0.66 | 2.60 | 1.59 | 1.05 |
| [−80,−60) | 2,426 | 2.858 | 73.3 | +2.45 | 5.00 | 2.75 | 1.46 | 0.76 | 4.08 | 3.55 | 4.30 |
| [−60,−40) | 4,030 | 2.309 | 73.2 | +2.08 | 2.92 | 4.91 | 1.01 | 1.39 | 2.27 | 2.27 | 2.58 |
| [−40,−20) | 5,819 | 2.181 | 72.9 | +2.23 | 3.37 | 5.01 | 0.65 | 1.89 | 2.48 | 2.24 | 1.75 |
| [−20,0) | 6,317 | 2.293 | 74.1 | +1.97 | 3.77 | 3.98 | 2.09 | 1.68 | 2.35 | 1.49 | 1.84 |
| [0,20) | 5,380 | 2.072 | 71.5 | +1.79 | 3.64 | 3.36 | 2.10 | 1.33 | 1.63 | 1.74 | 1.72 |
| [20,40) | 3,788 | 2.431 | 74.0 | +1.95 | 4.92 | 3.53 | 1.40 | 2.29 | 2.69 | 1.47 | 3.40 |
| [40,60) | 1,522 | 2.590 | 76.9 | +2.19 | 6.81 | 3.47 | 30.07 | 3.08 | 1.57 | 1.28 | 3.07 |
| [60,80) | 579 | 3.108 | 75.0 | +2.46 | 19.71 | 1.38 | 22.82 | 2.62 | 2.44 | 2.25 | 14.32 |
| ≥ 80 | 362 | 2.115 | 65.7 | +1.99 | 2.59 | 0.08 | 0.06 | 4.50 | 5.36 | 2.98 | 0.47 |

**The table's structure:** everything below −80 is bad-or-lottery — [−150,−80)
= three consecutive toxic buckets (1.09/1.32/1.28; 2,434 trips) and < −150 is
a 281-trip LOTTERY band (win-big-or-bleed: 86.6 in 2021 vs 0.29-0.32 in
2022/24). The clean boundary is **−80**, not the −100 first proposed off the
coarse buckets. [−80,−60) = 2.858 immediately above the line. Deceleration
[20,80) is good (2.43-3.11) but with small-n year spikes; ≥ 80 erratic.
Cutoff sensitivity (keep accel ≥ X, mc=0): −120 → 2.191, −100 → 2.236, **−80
→ 2.320**, −60 → 2.271. Prior context: accel ≥ −100 alone = mc=1 2.068;
+rngfront = 2.138; the TRIPLE (+slope<−10) = 2.139 (year table in prior
message; −80 variant pending user cutoff choice).

## S39q — volatility-NORMALIZED slopes (user question): possible, trivial, and it WASHES OUT (2026-07-31)

**User:** rngfront is volatility-invariant, the OLS slopes aren't (S39o figures
= bp/min, ols_slope × 6e5) — can we normalize by volat? Yes: `nslope = slope ×
30 / volat_20m` (trend-per-slot in units of typical slot movement,
dimensionless). Universe = v1.6 minus rngfront, $1-$10 book.

nslope q05/med/q95 = −0.52/−0.32/−0.15; naccel = −0.51/−0.09/+0.26.

| nslope bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −0.6 | 427 | 1.590 | 69.3 | +1.76 | 14.13 | 0.35 | 0.73 | 9.83 | 1.00 | 3.67 | 2.19 |
| [−0.6,−0.45) | 4,286 | 2.004 | 73.0 | +1.83 | 3.08 | 4.87 | 1.37 | 1.31 | 1.89 | 1.55 | 2.25 |
| [−0.45,−0.35) | 8,525 | 2.163 | 74.4 | +2.16 | 4.83 | 2.13 | 2.32 | 2.13 | 2.26 | 1.99 | 1.04 |
| [−0.35,−0.25) | 11,206 | 2.438 | 74.3 | +2.19 | 3.71 | 3.47 | 1.47 | 1.92 | 1.67 | 2.62 | 2.64 |
| [−0.25,−0.18) | 5,452 | 1.775 | 69.1 | +1.92 | 3.40 | 2.41 | 0.67 | 0.90 | 2.82 | 1.08 | 2.49 |
| [−0.18,−0.12) | 2,366 | 2.413 | 70.2 | +1.94 | 2.69 | 8.47 | 0.68 | 1.26 | 2.19 | 2.73 | 6.47 |
| [−0.12,−0.07) | 559 | 1.748 | 70.1 | +1.88 | 4.02 | 5.05 | 0.54 | 1.51 | 2.11 | 1.67 | 1.33 |
| [−0.07,−0.03) | 90 | 1.747 | 71.1 | +2.45 | 8.15 | −inf | 1.62 | −inf | 11.57 | 33.15 | 0.16 |
| [−0.03,0) | 17 | 106.4 | 88.2 | +6.56 | −inf | - | - | - | 96.89 | - | - |
| ≥ 0 | 10 | 1.069 | 30.0 | −1.17 | - | - | - | - | −inf | 0.00 | - |

| naccel bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −0.5 | 1,754 | 1.689 | 66.7 | +1.82 | 1.91 | 1.01 | 2.63 | 2.34 | 0.99 | 4.65 | 1.58 |
| [−0.5,−0.35) | 2,735 | 1.617 | 72.2 | +2.26 | 3.13 | 2.27 | 0.93 | 1.32 | 2.08 | 1.52 | 1.03 |
| [−0.35,−0.25) | 3,876 | 2.365 | 74.5 | +2.26 | 3.23 | 4.67 | 1.03 | 1.04 | 2.01 | 3.18 | 3.48 |
| [−0.25,−0.15) | 4,694 | 2.280 | 72.8 | +2.10 | 4.86 | 4.73 | 0.86 | 1.36 | 2.20 | 2.29 | 1.82 |
| [−0.15,−0.05) | 5,565 | 1.957 | 71.8 | +2.04 | 2.81 | 3.30 | 1.02 | 1.22 | 2.45 | 1.78 | 1.78 |
| [−0.05,0.05) | 5,324 | 2.486 | 74.5 | +2.15 | 4.11 | 3.45 | 2.29 | 2.29 | 2.36 | 1.86 | 1.99 |
| [0.05,0.15) | 4,345 | 2.218 | 72.6 | +1.94 | 4.67 | 3.36 | 1.02 | 1.49 | 2.22 | 1.64 | 2.87 |
| [0.15,0.25) | 2,734 | 2.416 | 74.1 | +1.97 | 6.93 | 3.54 | 3.61 | 3.63 | 2.24 | 1.06 | 2.15 |
| ≥ 0.25 | 1,911 | 2.597 | 73.5 | +1.77 | 6.41 | 2.43 | 6.66 | 2.49 | 1.91 | 2.36 | 1.26 |

**Reading: the RAW axes carry the signal; normalization dilutes it.** The
crisp raw toxic band [−150,−80)bp/min (1.09/1.32/1.28) smears into two
mildly-bad mixed-year buckets; the raw L-shape knife (slope < −10) has no
clean normalized analog (flat end = 17-trip noise cells). **Mechanism:**
volat_20m's EMA is contaminated by the very recent bars the slope measures —
numerator and denominator share the move, so dividing self-normalizes the
signal away. rngfront escapes via range-over-range on nested windows. The
log-slopes are already price-level-invariant; the per-name volatility scale
that remains is apparently PART of the information. A clean normalizer would
need a LAGGED volat (pre-window) — not recorded; not worth a column unless
the raw axes disappoint. **Verdict: keep slope/accel raw (bp/min).**

## S39r — accel5 (slope_5m − slope_10m) breakdown: the 5m scale is NOISE (2026-07-31)

`base_v2/` = base rerun on `flushfader_base_tkds` with the new ols_slope_300
(single-reader engine, 14.7 min, TRIP-SET PARITY vs base_v1 exact — the
restricted-table workflow proven end to end). accel5 = (ols_slope_300 −
ols_slope_600)×6e5 bp/min; q05/med/q95 = −153.7/−27.5/+79.6 (~2× wider than
the 10m−20m axis). Universe = v1.6 minus rngfront, $1-$10 book.

| accel5 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −300 | 170 | 1.525 | 57.1 | +1.49 | 41.27 | 0.00 | 0.81 | 0.45 | 5.16 | 18.95 | 1.99 |
| [−300,−200) | 509 | 2.324 | 66.4 | +2.24 | 25.33 | 9.69 | 0.29 | 1.46 | 2.25 | 2.60 | 1.48 |
| [−200,−150) | 1,084 | 2.177 | 73.9 | +2.56 | 4.14 | 3.74 | 0.32 | 1.59 | 1.75 | 3.75 | 2.28 |
| [−150,−100) | 2,925 | 2.003 | 72.5 | +2.42 | 12.87 | 6.25 | 2.29 | 1.04 | 1.38 | 1.40 | 2.14 |
| [−100,−60) | 5,495 | 2.032 | 72.4 | +2.10 | 4.36 | 2.94 | 0.93 | 1.43 | 2.37 | 1.53 | 2.01 |
| [−60,−30) | 5,826 | 2.393 | 74.7 | +2.16 | 2.64 | 2.65 | 1.91 | 1.66 | 2.33 | 2.68 | 2.42 |
| [−30,0) | 5,898 | 2.183 | 73.0 | +1.93 | 2.74 | 3.21 | 1.53 | 2.11 | 1.90 | 1.81 | 2.57 |
| [0,30) | 5,166 | 2.094 | 72.7 | +1.84 | 2.84 | 3.58 | 1.27 | 2.75 | 2.97 | 1.74 | 1.01 |
| [30,60) | 3,080 | 2.005 | 71.9 | +1.85 | 3.82 | 2.29 | 2.64 | 1.46 | 1.86 | 1.49 | 1.63 |
| [60,100) | 1,898 | 2.050 | 72.0 | +2.14 | 4.79 | 0.98 | 0.86 | 1.55 | 1.87 | 3.76 | 2.98 |
| [100,150) | 658 | 3.513 | 72.6 | +2.32 | 19.40 | 2.47 | 2.04 | 1.29 | 6.69 | 4.91 | 1.73 |
| ≥ 150 | 229 | 2.715 | 85.6 | +3.36 | 38.87 | 1234.32 | −inf | 6.17 | 0.63 | 3.37 | 3.06 |

**Reading:** no consistent bleed band anywhere — negative extremes alternate
lottery years, the belly is flat 2.0-2.4, decelerating extremes are small-n
spike cells. Mechanism: the signal bar IS a fresh 20m low, so the last 5m is
near-definitionally falling — accel5 measures noise around that certainty.
The informative contrast is the 10m-vs-20m one (S39o). accel5 = record-only,
NO gate.

**S39r addendum — the −80 TRIPLE mc=1 (user request):** SPEC v1.7 candidate
= v1.6 + accel(10m−20m) ≥ −80 + slope_20m < −10 (rngfront retained):
**mc=0 2.352 (29,258) / mc=1 2.173 (4,053) — improves EVERY year at BOTH mc
levels vs v1.6 (2.148/2.079)**, the first clean sweep of the program. mc=1
years: 3.288/2.689/1.799/1.556/2.209/1.983/1.858. Caveat: cutoffs chosen
in-sample today (table boundaries, plateau-stable, 7-year × 2-mc audits).
Awaiting user bake decision.

## S39s — ⭐ SPEC v1.7 BAKED + slope_5m table + accel5 ≥ 100 = THE ABSORPTION OVERLAY (user) (2026-07-31)

**SPEC v1.7 (user decision) = v1.6 + `accel(10m−20m) ≥ −80bp/min` +
`slope_20m < −10bp/min`**, rngfront retained; raw bp/min axes (S39q). Engine
defaults + flags baked; banner = SPEC v1.7. Reference run on the full clean
universe in flight; parity vs the SQL cut on base_v2 to follow.

**User correction on my S39r "accel5 = noise" verdict (accepted):** the
DECELERATION side has signal — **`accel5 ≥ 100` = the ABSORPTION overlay**
(the stock decelerating while still printing new 20m lows = someone is
absorbing the selling): [100,150) = 3.513 positive ALL SEVEN YEARS, ≥150 =
85.6% win/med +3.36. Combined ≥100 ≈ 887 trips. Joins the year-robust overlay
roster (dsv, slope<−100 quality, vol-N_eff≥600, neffret≥28, now absorption) —
ties back to the absorption-at-key-levels thesis. `accel5 < −300` = coinflip
(1.53/57%) but LEFT IN (user: small n, no conclusions from 170 trips).

**Raw slope_5m table (user request; universe = v1.6 minus rngfront):**
q05/med/q95 = −265.5/−93.5/−9.0.

| slope5 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −400 | 341 | 1.061 | 55.1 | +1.19 | −inf | 1.66 | 0.25 | 0.37 | 0.69 | 13.29 | 1.12 |
| [−400,−300) | 652 | 2.269 | 71.0 | +3.36 | 8.92 | 14.08 | 0.65 | 2.18 | 1.15 | 3.18 | 1.57 |
| [−300,−200) | 2,951 | 2.188 | 72.6 | +2.72 | 7.56 | 1.23 | 1.74 | 1.07 | 2.04 | 1.86 | 3.50 |
| [−200,−150) | 3,985 | 1.848 | 72.2 | +2.44 | 3.38 | 2.85 | 0.51 | 1.39 | 3.01 | 2.31 | 1.11 |
| [−150,−100) | 7,286 | 2.211 | 75.0 | +2.24 | 2.92 | 3.21 | 1.27 | 2.22 | 2.07 | 1.87 | 2.29 |
| [−100,−70) | 5,922 | 2.559 | 74.7 | +2.11 | 4.20 | 3.70 | 3.46 | 2.19 | 3.20 | 1.45 | 1.92 |
| [−70,−40) | 5,856 | 2.260 | 72.6 | +1.81 | 3.49 | 2.60 | 1.35 | 1.62 | 2.02 | 2.21 | 3.54 |
| [−40,−20) | 3,116 | 2.237 | 70.3 | +1.57 | 2.85 | 3.64 | 1.94 | 2.05 | 2.45 | 1.86 | 1.35 |
| [−20,0) | 1,905 | 1.866 | 68.7 | +1.41 | 3.40 | 2.81 | 1.72 | 1.19 | 1.40 | 1.62 | 1.24 |
| [0,30) | 849 | 2.137 | 73.4 | +2.23 | 6.50 | 4.17 | 2.28 | 1.04 | 1.40 | 1.86 | 1.95 |
| ≥ 30 | 75 | 1.631 | 76.0 | +2.90 | −inf | 91.51 | 2.49 | −inf | 0.19 | 4.58 | −inf |

Body flat 1.85-2.56, no bleed band; < −400 = near-coinflip extreme
(overlapping accel5 < −300 territory); [−100,−70) = the sweet band. Gate-
hostile, record-only.

## S39t — n_eff_ret tables on the v1.7 book + the slope5 cut's marginal anatomy (2026-07-31)

Universe = the full v1.7 gate stack (pre-slope5), $1-$10 book, 29,258 trips.

**`n_eff_ret_20m` (40 slot-returns; high = smooth):**

| bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < 16 | 2 | −inf | 100.0 | +2.89 | - | - | - | −inf | - | - | - |
| [16,20) | 103 | 2.007 | 60.2 | +1.12 | −inf | 0.99 | 11.82 | 10.30 | −inf | 1.01 | 0.18 |
| [20,22) | 317 | 1.782 | 72.6 | +1.89 | 4.73 | 15.71 | 0.98 | 5.62 | 12.95 | 1.57 | 0.66 |
| [22,24) | 1,307 | 2.432 | 73.9 | +2.21 | 6.73 | 7.72 | 0.88 | 1.34 | 6.90 | 2.75 | 1.99 |
| [24,26) | 4,072 | 2.022 | 72.4 | +1.97 | 2.22 | 7.42 | 2.49 | 2.94 | 5.54 | 0.81 | 1.69 |
| [26,28) | 8,196 | 2.045 | 71.0 | +1.98 | 3.43 | 2.84 | 1.00 | 1.73 | 1.65 | 2.12 | 2.24 |
| [28,30) | 9,289 | 2.668 | 74.9 | +2.02 | 4.31 | 3.26 | 1.90 | 1.49 | 2.23 | 2.64 | 3.01 |
| [30,32) | 5,043 | 2.765 | 75.9 | +2.22 | 4.45 | 3.40 | 1.45 | 1.26 | 2.86 | 2.70 | 7.39 |
| ≥ 32 | 929 | 2.507 | 71.9 | +2.11 | 4.70 | 5.77 | 4.25 | 0.36 | 2.11 | 2.06 | 0.83 |

**`n_eff_ret_10m` (20 slot-returns):**

| bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < 8 | 0 | - | - | - | - | - | - | - | - | - | - |
| [8,10) | 237 | 1.296 | 62.9 | +1.51 | 5.76 | 4.26 | 1.68 | 0.47 | 5.31 | 1.22 | 0.61 |
| [10,11) | 637 | 2.736 | 76.0 | +2.22 | 2.94 | 11.60 | 5.58 | 1.65 | 15.07 | 1.26 | 1.21 |
| [11,12) | 1,573 | 3.637 | 75.8 | +2.08 | 3.56 | 7.17 | 3.77 | 3.16 | 3.68 | 2.79 | 3.58 |
| [12,13) | 3,183 | 2.604 | 74.7 | +2.28 | 3.53 | 9.03 | 1.43 | 1.43 | 3.15 | 1.95 | 4.51 |
| [13,14) | 6,110 | 1.879 | 71.2 | +1.94 | 2.98 | 2.65 | 0.86 | 1.78 | 1.74 | 1.49 | 2.89 |
| [14,15) | 8,077 | 2.227 | 72.8 | +1.99 | 4.60 | 3.38 | 1.16 | 1.59 | 2.62 | 1.60 | 1.72 |
| [15,16) | 6,248 | 2.419 | 73.1 | +1.89 | 3.51 | 2.48 | 2.23 | 1.33 | 2.44 | 2.83 | 1.68 |
| ≥ 16 | 3,193 | 3.248 | 77.7 | +2.29 | 4.96 | 8.68 | 2.95 | 1.66 | 1.76 | 3.52 | 6.08 |

Structure: the 10m has TWO all-years-positive zones — **[11,12) = 3.637
(worst year 2.79)** and **≥16 = 3.248 (worst 1.66)** — with a [13,14) sag
between; the 20m's good zone is **≥28 (2.67/2.77/2.51)**. Overlay-shaped,
not gates; user to adjudicate.

**The slope5 < −400 slice UNDER the other v1.7 gates (the cut that was baked):**

| year | n | PF |
|---|---|---|
| 2020 | 14 | −inf (all losers) |
| 2021 | 0 | - |
| 2022 | 8 | 0.00 |
| 2023 | 14 | 0.00 |
| 2024 | 10 | −inf (all losers) |
| 2025 | 4 | 0.00 |
| 2026 | 16 | 0.00 |
| TOTAL | 66 | 0.706 / 36.4% win |

(PF 0.00 = zero gross wins that year; −inf cells = degenerate small-n
denominators.) Book effect of the cut: 2.352 → 2.367. This is the v1.7
`slope_5m ≥ −400bp/min` gate's full evidentiary base.

**⚠ Why 66 here vs 341 in the S39s table (user question):** different
conditioning. S39s = the < −400 slice on the BREAKDOWN universe (v1.6 minus
rngfront, no v1.7 gates) = 341 @ 1.061/55.1%. Here = the slice under the FULL
v1.7 stack: accel ≥ −80 already rejects ~80% of it (a −400bp/min 5m collapse
drags the 10m slope with it → very negative accel), and it preferentially
removed the capitulation-blow-off WINNERS (the lottery wins live at extreme
acceleration) — leaving 66 near-pure losers. The marginal view is the honest
measure of what a gate removes GIVEN the rest of the spec; always evaluate a
prospective gate on the residual universe, not the breakdown universe.

## S39u — dist-from-20m-high vs slope_20m: REPLACEMENT AT PARITY, NOT TAKEN (user question) (2026-07-31)

Universe = v1.7 minus the dist band minus the slope20 insurance gate (37,689
trips; slope must compete alone). **corr(dist, slope20) = 0.879** and the 2D
is GEOMETRICALLY NESTED: slope < −70 × dist ≥ −10 is EMPTY (a steep 20m slope
forces a deep drawdown); the converse exists (slow-but-deep: dist-in ×
slope ≥ −25 = 863 @ 2.005).

**dist (%) uncensored** (current gate [−35,−10)):

| bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −35 | 129 | 11.21 | 80.6 | +5.62 | 1.62 | −inf | −inf | - | 155.46 | 31.32 | −inf |
| [−35,−25) | 1,501 | 3.610 | 77.5 | +4.00 | 11.16 | 1.51 | 0.56 | 2.58 | 3.57 | 3.23 | 4.76 |
| [−25,−18) | 5,775 | 2.647 | 76.7 | +2.83 | 5.29 | 5.38 | 1.13 | 1.96 | 3.36 | 1.95 | 2.52 |
| [−18,−14) | 8,323 | 2.208 | 71.7 | +2.04 | 2.83 | 3.65 | 2.10 | 1.30 | 2.01 | 2.15 | 2.13 |
| [−14,−10) | 13,625 | 2.105 | 72.7 | +1.74 | 3.24 | 3.51 | 1.47 | 1.68 | 1.93 | 1.53 | 1.80 |
| [−10,−7) | 7,374 | 1.932 | 70.1 | +1.24 | 2.32 | 2.70 | 1.25 | 1.18 | 1.84 | 2.14 | 1.26 |
| [−7,−5) | 953 | 1.146 | 67.9 | +0.88 | 2.34 | 1.02 | 1.74 | 4.10 | 0.73 | 0.57 | 1.35 |
| [−5,−3) | 9 | 0.375 | 77.8 | +0.99 | 0.06 | - | - | −inf | −inf | - | −inf |

**slope_20m (bp/min) uncensored, same universe:**

| bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −250 | 26 | 2.582 | 76.9 | +2.58 | 0.24 | −inf | - | - | −inf | 3.15 | −inf |
| [−250,−150) | 432 | 9.345 | 83.8 | +4.71 | 936.46 | 0.04 | 3.49 | −inf | 25.59 | 7.77 | 11.67 |
| [−150,−100) | 2,297 | 3.173 | 80.0 | +3.24 | 3.57 | 9.85 | 0.73 | 2.32 | 3.87 | 3.57 | 3.18 |
| [−100,−70) | 5,627 | 2.160 | 73.0 | +2.27 | 7.81 | 2.74 | 1.73 | 2.12 | 1.90 | 1.48 | 1.76 |
| [−70,−50) | 8,942 | 2.217 | 72.6 | +1.98 | 2.92 | 3.47 | 1.88 | 1.26 | 2.22 | 2.22 | 2.27 |
| [−50,−35) | 9,939 | 2.375 | 73.2 | +1.74 | 3.39 | 3.25 | 1.53 | 1.73 | 2.29 | 1.94 | 2.61 |
| [−35,−25) | 6,250 | 2.018 | 70.6 | +1.49 | 2.33 | 4.14 | 1.32 | 1.03 | 1.84 | 1.38 | 2.27 |
| [−25,−15) | 3,464 | 1.957 | 70.8 | +1.24 | 2.99 | 2.20 | 0.86 | 1.97 | 2.47 | 1.80 | 1.56 |
| [−15,−10) | 526 | 1.057 | 62.0 | +0.72 | 1.25 | 1.21 | 0.44 | 4.95 | 1.65 | 0.45 | 0.67 |
| ≥ −10 | 186 | 0.695 | 57.0 | +1.32 | 1.70 | 2.34 | 0.27 | 8.55 | 1.75 | 1.49 | 0.09 |

**Replacement grid (mc=0 total | mc=1 total):** dist (current) 2.345 |
**2.175** · slope<−25 2.338 | 2.093 · slope<−35 **2.402** | 2.173 ·
dist+slope<−25 2.356 | 2.190 · neither 2.269 | —.

**VERDICT: dist STAYS.** slope<−35 replaces it AT PARITY (mc=0 +0.06, mc=1
−0.002, mixed years, no recency edge) — and parity does not pay for replacing
a long-audited gate; slope already holds two v1.7 seats. Notes for later:
(a) the dist < −35 "un-fadeable wall" slice = 129 trips @ 11.2 on this
universe — the S7-era wall deserves a small-n revisit someday; (b) the
shallow margin [−10,−7) = 1.93 is not toxic — the −10 boundary is quality-
motivated, not damage-motivated. **Bonus: the dist replay = FINAL v1.7 mc=1 =
2.175 on 4,056** (ladder 2.004 → 2.070 → 2.106 → 2.104 → 2.175).

## S39v — TRIP EFFICIENCY frontiers (user method) + ⭐ the accel × depth INVERSION (2026-07-31)

**User: find equivalent-PF points and compare trip counts — which feature is
more trip-efficient?** Universe = v1.7 minus dist minus slope20 gate, accel ON.

| dist: keep < X | n | PF | | slope: keep < Y | n | PF |
|---|---|---|---|---|---|---|
| −5 | 37,680 | 2.270 | | −10 | 37,503 | 2.286 |
| −7 | 36,727 | 2.300 | | −15 | 36,977 | 2.308 |
| −8 | 35,095 | 2.312 | | −20 | 35,608 | 2.321 |
| −10 | 29,353 | 2.369 | | −25 | 33,513 | 2.338 |
| −12 | 22,068 | 2.419 | | −30 | 30,724 | 2.390 |
| −14 | 15,728 | 2.559 | | −35 | 27,263 | 2.402 |
| −16 | 10,908 | 2.681 | | −40 | 23,714 | 2.421 |
| −18 | 7,405 | 2.926 | | −50 | 17,324 | 2.414 |
| −20 | 4,974 | 2.961 | | −60 | 12,089 | 2.505 |
| −22 | 3,180 | 3.181 | | −70 | 8,382 | 2.598 |
| −25 | 1,630 | 3.950 | | −80 | 5,916 | 2.731 |
| | | | | −100 | 2,755 | 3.648 |

**The frontier CROSSES:** slope is more trip-efficient shallow (at PF ≈
2.37-2.42 it keeps ~1-2k more trips than dist), **dist dominates the middle**
(PF ≈ 2.68: dist 10,908 vs slope ~6k — near 2×), ~tied deep. At the MATCHED
mc=1 point (2.175 vs 2.173): dist = 4,056 mc=1 trips vs slope = 3,794 —
**dist ~7% more trip-efficient where the book operates.** Verdict of S39u
stands, now with the efficiency argument: dist stays.

**⭐⭐ THE ACCEL × DEPTH INVERSION (user hypothesis check, answer inverted):**

| dist < −35 slice | n | PF | win% | med |
|---|---|---|---|---|
| accel gate ON (what v1.7 keeps) | 129 | 11.21 | 80.6 | +5.62 |
| accel gate OFF (all) | 204 | 15.089 | 84.3 | +5.45 |
| **accel FAILS (< −80) — what v1.7 REJECTS** | **75** | **45.713** | **90.7** | **+5.23** |

The accel gate did NOT create the sub-−35% goodness — it is REMOVING its best
part: past the old un-fadeable wall, extreme acceleration + extreme depth =
the true capitulation monsters (the S39o "lottery band" is DEPTH-DEPENDENT:
toxic at moderate depth, spectacular past −35%). v1.7 currently discards this
75-trip / ~11-per-year / 90.7%-win cell. ⏭ SPEC QUESTION FOR THE USER: exempt
deep capitulations from the accel gate (apply accel only when dist ≥ −35)?
S38j census note: 75 trips = A++-cell-family size — real but small; year
spread of the 45.7 cell not yet audited.

**S39v census postscript (the S38j discipline, applied before deciding):**
the rejected 45.7 cell = **16 tkds EVER** (by year: 2020×2, 2021×1, 2022×0,
2023×2, 2024×1, **2025×7**, 2026×3), PF carried by the 2025 cluster (38
trips @ 185 across 7 tkds). Anecdote, not edge — same shape as the S38j
EYES story. **NO accel exemption** (16 events don't buy a spec branch);
logged instead as a PLAYBOOK note: deep capitulation (dist < −35%) on
extreme acceleration = an A++-family recognition/sizing moment when it
occurs live, not a gate.

## S39w — ⭐ v1.7 REFERENCE + PARITY; eff10 REVERSAL; the wall; dist-from-20m-VWAP (2026-07-31)

**v1.7 REFERENCE RUN (full clean universe, engine defaults): 47,847 raw trips
@ 2.314/72.8% — GRAND PARITY ✓ (engine ≡ SQL-on-base_v2, 47,847 = 47,847,
zero diff).** `v17_reference/` = THE reference parquet. Book ($1-$10): 29,258
@ 2.367 mc=0 / 4,056 @ 2.175 mc=1.

**eff10 REVERSAL (user asked to remove; the residual universe says KEEP):**
my earlier V2 "near-no-op" reading was on the PRE-v1.7 universe (S39t
conditioning lesson). Under v1.7, the gate's marginal removes |eff10| < 0.15
= 1,063 @ 1.474/66.8% (recent years bad: 2024 0.56, 2025 0.67), and removal
drops mc=1 2.175 → 2.127. eff_10m signed, uncensored (dist wall on):

| bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −0.6 | 5,999 | 2.552 | 76.3 | +2.09 | 4.25 | 4.98 | 1.29 | 1.28 | 3.05 | 2.04 | 3.59 |
| [−0.6,−0.45) | 10,266 | 2.558 | 74.3 | +2.14 | 3.01 | 5.55 | 1.40 | 1.92 | 3.08 | 2.28 | 2.12 |
| [−0.45,−0.3) | 8,804 | 2.261 | 72.9 | +2.03 | 4.07 | 3.12 | 1.62 | 1.84 | 1.94 | 1.84 | 2.12 |
| [−0.3,−0.15) | 4,123 | 1.938 | 68.6 | +1.77 | 4.75 | 1.76 | 1.98 | 1.39 | 1.62 | 1.29 | 2.67 |
| [−0.15,−0.05) | 869 | 1.390 | 66.1 | +1.53 | 9.69 | 6.92 | 1.66 | 1.79 | 0.56 | 0.67 | 1.01 |
| [−0.05,0.05) | 187 | 1.781 | 71.1 | +1.78 | 6.33 | 0.91 | 0.43 | 111.83 | 0.87 | 0.47 | 36.81 |
| [0.05,0.15) | 7 | 35.34 | 42.9 | −0.03 | - | - | −inf | - | - | 0.00 | - |

Monotone in |magnitude| on the negative side; positive side near-empty at a
20m-low signal. **NOT baked out — recommendation: KEEP.**

**The −35 wall (user question):** census = 129 trips / 28 tkds (~4/yr; 2024×8
+ 2025×8 tkds carry PF 155/31; 2020 = 1.62; every observable year clean).
Removal: mc=0 +129 trips @ 11.2, **mc=1 2.175 → 2.184 (+0.009)** — mildly
positive AND a simplification (drops a parameter whose S7-era "un-fadeable"
rationale no longer holds here). **Recommendation: REMOVE (user to confirm);
lottery-shaped caveat on record.**

**dist-from-20m-VWAP (user: never tested):** corr(dist_hi, dist_vw) = 0.948
— the third depth-family member. Monotone, clean shallow-tail cut
([−3.5,−2) = 1.036):

| dist_vw bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < −20 | 302 | 5.427 | 78.5 | +4.82 | 4.62 | −inf | −inf | 2.30 | 24.80 | 3.21 | 4.60 |
| [−20,−15) | 1,153 | 3.695 | 77.0 | +4.05 | 9.73 | 2.28 | 0.28 | 2.41 | 5.03 | 4.87 | 4.58 |
| [−15,−12) | 2,576 | 2.908 | 78.1 | +3.16 | 14.71 | 4.25 | 0.81 | 1.92 | 3.77 | 2.33 | 2.56 |
| [−12,−9) | 6,624 | 2.334 | 74.6 | +2.41 | 3.09 | 5.20 | 1.34 | 1.48 | 2.44 | 2.01 | 2.48 |
| [−9,−7) | 8,969 | 2.117 | 71.5 | +1.93 | 3.61 | 2.68 | 1.85 | 1.36 | 2.14 | 1.67 | 2.32 |
| [−7,−5) | 12,143 | 2.154 | 72.8 | +1.60 | 2.71 | 3.88 | 1.82 | 1.72 | 1.57 | 1.63 | 1.76 |
| [−5,−3.5) | 5,233 | 1.858 | 69.5 | +1.21 | 2.46 | 2.17 | 1.24 | 1.54 | 1.78 | 1.55 | 1.66 |
| [−3.5,−2) | 503 | 1.036 | 63.8 | +0.64 | 0.88 | 1.38 | 1.79 | 9.79 | 0.79 | 0.94 | 0.40 |

Frontier (keep < X): −4 → 35,903/2.325 · −5 → 31,767/2.356 · −6 →
25,587/2.406 · −7 → 19,624/2.451 · −8 → 14,594/2.556 · −10 → 7,765/3.026 ·
−12 → 4,031/3.313 · −15 → 1,455/4.030. On the mc=0 frontier dist_vw beats
dist_hi at most matched-PF points — but at mc=1: dist_vw@−5 = 2.114,
**dist_vw@−6 = 2.170 on 3,618 vs dist_hi's 2.175 on 4,056 (dist_hi keeps
~12% more trips at equal book PF)**. Same verdict as slope: NO replacement.
**THE THEME OF THE DAY, third confirmation: the baked gates' value lives in
SLOT ALLOCATION — mc=0 frontiers cannot see it, and every challenger that
wins mc=0 efficiency loses at the mc=1 operating point.** dist_vw joins the
overlay/record roster (deep bands 3.7-5.4 with clean years).

# S40 — 2026-08-01: base_v3 (hi_60 + slot-range eff twins), speed-vs-1m-high, eff_rng verdict, warmup removal

## S40 — base_v3: five new record-only columns, PARITY EXACT (2026-08-01)

Engine additions (record-only, no gate changes):
- `hi_60` — the raw 60-bar vwap MAX at the signal (post-push, same convention
  as `rng_60`; `rng_60` = ln(hi/lo) could never recover the high itself).
  dist-from-1m-high = `signal_vwap/hi_60 - 1`.
- `rng_slots_20m` / `rng_slots_10m` — ln(hi/lo) of the last 41 / 21 30s-slot
  vwaps (`MaxMa 41`/`MinMa 41` + 21 twins pushed at slot emission): the SAME
  vwap spans eff_20m's 40 / eff_10m's 20 returns cover.
- `eff_rng_20m` / `eff_rng_10m` — that range over the SAME Σ|r| denominator
  the eff pair uses (user idea: range replaces |net| in the numerator).
  Direction-blind, ∈ (0,1], eff_rng ≥ |eff| by construction; warms exactly
  with the eff pair (the 41st slot emission fills the max/min windows AND
  completes Σ40|r|).

**`base_v3/` = THE working base parquet: 2,195,361 = 2,195,361 trips, zero
diff vs base_v2 both directions** (key = symbol/trade_date/signal_sec).
Invariants verified on all rows: eff_rng ∈ (0,1], eff_rng ≥ |eff|,
hi_60 ≥ signal_vwap.

⚠ **First attempt SILENTLY LOST 540k trips (strict subset)**: the base-pass
flag list dated from BEFORE the v1.7 evening bake, so the three new gates
(accel/slope20/slope5) ran at their spec defaults. **The base-pass CLI must
be extended every time a spec gate is baked.** Canonical base pass (ALL 15
gates off; entry window 09:45-15:00 and volat >= 40bp stay baked):

```
FF_CANDIDATE_TABLE=flushfader_base_tkds \
./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --max-speed-1m 0 --k-band-lo 0 --k-band-hi 0 \
  --eff20-lo -Infinity --eff20-hi Infinity --min-abs-eff-10m 0 \
  --dist-hi-lo -Infinity --dist-hi-hi 0 --min-vol10-rate 0 \
  --min-lows-300 0 --max-rng-front Infinity --min-dv-0945-tape 0 \
  --min-accel-1020 -Infinity --max-slope-20m 0 --min-slope-5m -Infinity \
  --out-dir data/equity/flushfader/base_v3
```

Bookkeeping note: the v1.7 residual book on base_v3 = **29,192** trips; the
"29,258 @ 2.367" quoted at yesterday's close predates the slope5 bake — the
66-trip difference is exactly the slope5 >= -400 residual cut (S39t).

## S40a — flush speed (1m) vs distance from 1m HIGH (user request, 2026-08-01)

Axes: `speed = 100*(signal_vwap/vwap_60_prev - 1)` (the gated flush-speed
axis, spec < -2; vwap_60_prev = 60-bar rolling vwap lagged 1m) vs
`d1m = 100*(signal_vwap/hi_60 - 1)` (depth below the 1m HIGH). Universe =
v1.7 residual, $1-$10 book: 29,192 trips. speed q05/med/q95 =
-7.4/-3.33/-2.12; d1m = -7.23/-3.37/-1.84; **corr(speed, d1m) = 0.914** —
same 1m-collapse information, max-anchored vs lagged-mean-anchored.

| speed bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-8) | 1,110 | 3.44 | 73.4 | +3.46 | 13.22 | 11.19 | 1.19 | 1.72 | 8.17 | 2.19 | 2.96 |
| [-8,-6) | 1,972 | 2.528 | 72.4 | +2.51 | 5.34 | 6.36 | 0.65 | 2.04 | 3.0 | 1.62 | 2.84 |
| [-6,-5) | 2,407 | 2.889 | 74.8 | +2.61 | 6.85 | 3.6 | 2.15 | 1.31 | 3.91 | 2.45 | 2.41 |
| [-5,-4.5) | 1,830 | 2.132 | 71.6 | +2.24 | 8.3 | 3.75 | 1.18 | 1.39 | 2.37 | 1.43 | 1.25 |
| [-4.5,-4) | 2,513 | 2.631 | 75.2 | +2.24 | 6.05 | 4.76 | 1.3 | 2.11 | 2.56 | 1.87 | 2.82 |
| [-4,-3.5) | 3,371 | 2.082 | 73.2 | +2.04 | 3.18 | 3.72 | 1.13 | 0.92 | 2.29 | 1.87 | 3.02 |
| [-3.5,-3) | 4,432 | 2.226 | 73.9 | +1.88 | 3.48 | 3.49 | 1.78 | 1.68 | 1.83 | 1.89 | 2.01 |
| [-3,-2.75) | 2,487 | 2.257 | 73.5 | +1.96 | 2.75 | 4.06 | 1.5 | 1.3 | 1.96 | 2.4 | 2.52 |
| [-2.75,-2.5) | 2,917 | 2.355 | 74.8 | +1.97 | 2.93 | 3.51 | 1.95 | 2.59 | 1.95 | 1.81 | 2.35 |
| [-2.5,-2.25) | 3,021 | 2.144 | 72.0 | +1.86 | 2.09 | 2.82 | 2.29 | 1.98 | 1.47 | 2.32 | 2.32 |
| [-2.25,-2) | 3,132 | 2.095 | 73.0 | +1.7 | 2.24 | 2.92 | 1.52 | 2.68 | 1.66 | 1.71 | 3.16 |

| d1m bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-12) | 125 | 5.344 | 82.4 | +7.48 | - | - | - | 0.14 | 284.14 | 20.84 | 2.04 |
| [-12,-10) | 216 | 5.73 | 76.9 | +4.32 | 6.73 | 14.98 | 5.64 | 3.78 | 25.23 | 5.09 | 2.34 |
| [-10,-8) | 623 | 2.612 | 70.9 | +2.8 | 8.0 | 13.97 | 0.24 | 1.78 | 9.35 | 1.24 | 4.65 |
| [-8,-7) | 657 | 3.723 | 76.4 | +3.44 | 9.75 | 15.58 | 2.42 | 1.38 | 6.05 | 1.7 | 4.65 |
| [-7,-6) | 1,216 | 2.551 | 72.6 | +2.07 | 6.09 | 3.96 | 0.78 | 1.14 | 3.94 | 1.46 | 3.83 |
| [-6,-5) | 2,531 | 2.133 | 71.1 | +2.29 | 4.67 | 3.45 | 1.24 | 1.93 | 2.01 | 1.52 | 2.1 |
| [-5,-4) | 4,492 | 2.277 | 73.4 | +2.39 | 3.91 | 3.25 | 1.61 | 1.8 | 2.76 | 1.73 | 1.73 |
| [-4,-3) | 8,173 | 2.423 | 74.6 | +2.16 | 3.85 | 3.66 | 1.28 | 1.44 | 2.36 | 2.47 | 2.35 |
| [-3,-2.5) | 4,991 | 2.265 | 73.4 | +1.9 | 2.81 | 3.76 | 2.0 | 2.36 | 1.67 | 1.94 | 1.94 |
| [-2.5,-2) | 3,907 | 2.488 | 75.3 | +1.82 | 4.13 | 4.02 | 1.56 | 2.21 | 1.9 | 1.97 | 2.73 |
| [-2,-1.5) | 1,837 | 1.691 | 69.4 | +1.44 | 1.82 | 3.01 | 1.84 | 0.83 | 1.02 | 1.73 | 3.48 |
| [-1.5,0.01) | 424 | 1.436 | 68.9 | +1.18 | 0.91 | 1.68 | 5.57 | 0.95 | 0.93 | 1.38 | 5.62 |

2D, n / PF:

| speed \ d1m | [-100,-8) | [-8,-6) | [-6,-4) | [-4,-2.5) | [-2.5,0.01) |
|---|---|---|---|---|---|
| [-100,-5) | 958 / 3.42 | 1,758 / 3.09 | 2,510 / 2.55 | 257 / 2.51 | 6 / 0.49 |
| [-5,-4) | 6 / - | 82 / 1.64 | 2,788 / 2.27 | 1,412 / 2.79 | 55 / 1.86 |
| [-4,-3) | 0 | 20 / 1.2 | 1,448 / 2.02 | 5,554 / 2.25 | 781 / 1.91 |
| [-3,-2.5) | 0 | 11 / 2.56 | 200 / 0.95 | 3,536 / 2.53 | 1,657 / 2.35 |
| [-2.5,-2) | 0 | 2 / - | 77 / 1.62 | 2,405 / 2.17 | 3,669 / 2.1 |

**Reading.** (a) d1m is the cleaner axis: monotone into the depths (≤ -10% =
5.3-5.7 PF) and with a genuinely TOXIC shallow end — trips still within 2% of
their own 1m high (2,261 trips @ 1.69/1.44, weak in 5 of 7 years each) are
"new 20m low without a fresh 1m leg": the last minute already bounced/stalled.
(b) speed adds little once d1m is known (corr 0.914; within-column gradients
in the 2D are mixed while within-row gradients follow d1m). (c) A
`d1m < -1.5%` (or -2%) residual gate is the candidate — cutoff = USER
decision from the d1m table; trip-efficiency + mc=1 to follow before any
bake, per the slot-allocation discipline (S39u/v).

## S40b — eff_20m vs eff_rng_20m (range numerator): REPLACEMENT REJECTED, the SIGN is the information (2026-08-01)

Universe = v1.7 minus the eff pair, $1-$10 book: 52,405 trips @ 2.046.
eff_rng_20m q05/med/q95 = 0.276/0.413/0.566; corr(eff_rng_20m, eff_20m) =
-0.831 (= +0.831 vs |eff_20m| — on this universe eff is negative nearly
everywhere, so eff_rng ≈ shifted |eff|); corr(eff_rng_10m, |eff_10m|) = 0.894.

| eff_rng_20m bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.3) | 4,710 | 1.647 | 66.8 | +1.88 | 1.97 | 2.52 | 0.82 | 1.1 | 1.84 | 1.65 | 1.49 |
| [0.3,0.35) | 7,451 | 1.814 | 70.3 | +1.86 | 2.69 | 3.57 | 0.99 | 1.37 | 1.74 | 1.59 | 1.23 |
| [0.35,0.4) | 11,064 | 2.232 | 72.7 | +2.09 | 3.02 | 2.54 | 1.32 | 2.08 | 1.77 | 2.51 | 2.32 |
| [0.4,0.45) | 11,563 | 2.358 | 73.5 | +1.97 | 4.1 | 2.77 | 1.63 | 1.74 | 2.44 | 1.86 | 2.32 |
| [0.45,0.5) | 8,802 | 2.15 | 73.3 | +2.07 | 2.68 | 3.66 | 1.32 | 2.24 | 2.33 | 1.82 | 1.79 |
| [0.5,0.55) | 5,135 | 2.096 | 72.4 | +1.88 | 5.94 | 4.21 | 1.33 | 1.22 | 1.7 | 1.69 | 2.66 |
| [0.55,0.6) | 2,355 | 2.139 | 74.6 | +1.87 | 10.19 | 4.65 | 1.1 | 0.8 | 2.67 | 1.6 | 4.62 |
| [0.6,0.65) | 874 | 1.615 | 72.2 | +1.8 | 21.43 | 8.67 | 1.62 | 1.78 | 2.14 | 0.63 | 0.96 |
| [0.65,0.7) | 263 | 0.749 | 63.1 | +1.17 | 16.4 | 2.96 | 0.25 | 1.94 | 5.71 | 0.14 | 0.03 |
| [0.7,0.8) | 165 | 0.493 | 54.5 | +0.82 | - | 1.85 | 0.53 | 1159.26 | 14.13 | 0.39 | 0.04 |
| [0.8,1.01) | 23 | 2.925 | 69.6 | +1.16 | - | - | 0.0 | - | - | 5.43 | - |

| eff_rng_10m bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.3) | 2,180 | 1.27 | 64.7 | +1.36 | 2.55 | 2.65 | 1.17 | 0.83 | 0.87 | 1.08 | 1.11 |
| [0.3,0.35) | 2,965 | 1.748 | 67.3 | +1.69 | 2.71 | 1.74 | 4.54 | 1.66 | 1.29 | 1.66 | 1.39 |
| [0.35,0.4) | 4,514 | 2.148 | 70.8 | +1.75 | 3.03 | 2.71 | 3.3 | 2.41 | 2.39 | 1.31 | 1.79 |
| [0.4,0.45) | 6,408 | 2.181 | 71.8 | +1.91 | 4.46 | 2.83 | 0.93 | 3.07 | 2.49 | 1.64 | 1.3 |
| [0.45,0.5) | 7,131 | 1.972 | 70.2 | +1.85 | 2.92 | 2.95 | 1.24 | 1.56 | 1.54 | 2.01 | 1.84 |
| [0.5,0.55) | 7,741 | 2.183 | 73.3 | +2.19 | 2.39 | 3.53 | 1.65 | 1.92 | 2.63 | 1.59 | 2.34 |
| [0.55,0.6) | 6,869 | 2.189 | 74.0 | +2.09 | 2.72 | 4.43 | 0.96 | 1.52 | 2.38 | 2.47 | 1.72 |
| [0.6,0.65) | 5,871 | 2.289 | 75.2 | +2.15 | 4.74 | 2.92 | 1.69 | 1.75 | 2.08 | 2.17 | 1.67 |
| [0.65,0.7) | 3,932 | 1.881 | 73.3 | +2.04 | 3.34 | 3.46 | 1.17 | 0.74 | 2.58 | 1.84 | 3.09 |
| [0.7,0.8) | 3,786 | 1.896 | 72.0 | +1.91 | 3.28 | 3.6 | 0.78 | 1.73 | 1.8 | 1.51 | 2.59 |
| [0.8,1.01) | 1,008 | 2.87 | 76.5 | +1.95 | 26.11 | 5.61 | 1.56 | 14.37 | 3.16 | 0.62 | 12.5 |

2D — the toxicity eff_rng sees is ALREADY excluded by the signed band (n / PF):

| band \ eff_rng_20m | [0,0.4) | [0.4,0.5) | [0.5,0.6) | [0.6,0.7) | [0.7,1.01) |
|---|---|---|---|---|---|
| eff-band IN | 8,853 / 2.26 | 17,463 / 2.35 | 3,838 / 2.33 | 101 / 2.79 | 0 |
| eff-band OUT | 14,372 / 1.79 | 2,902 / 1.81 | 3,652 / 1.91 | 1,036 / 1.21 | 188 / 0.54 |

Replacement frontier (all keep |eff10| >= 0.15):

| variant | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| CURRENT: eff20 [-.5,-.3) | 29,192 | 2.367 | 73.5 | 3.78 | 3.68 | 1.48 | 1.64 | 2.39 | 1.92 | 2.44 |
| R1: eff_rng [0.35,0.50) | 30,405 | 2.267 | 73.3 | 3.26 | 2.92 | 1.43 | 1.95 | 2.13 | 2.08 | 2.1 |
| R2: eff_rng [0.35,0.55) | 35,475 | 2.241 | 73.2 | 3.44 | 3.06 | 1.41 | 1.8 | 2.07 | 2.01 | 2.17 |
| R3: eff_rng [0.35,0.45) | 21,775 | 2.312 | 73.3 | 3.45 | 2.62 | 1.51 | 1.87 | 2.06 | 2.2 | 2.3 |
| R4: eff_rng [0.38,0.48) | 21,082 | 2.3 | 73.8 | 3.23 | 2.99 | 1.51 | 2.1 | 2.06 | 2.04 | 2.29 |
| R5: eff_rng [0.40,0.50) | 19,837 | 2.28 | 73.6 | 3.41 | 3.2 | 1.46 | 1.91 | 2.37 | 1.92 | 2.0 |
| A1: CURRENT + eff_rng < 0.65 | 29,188 | 2.367 | 73.5 | 3.78 | 3.68 | 1.48 | 1.64 | 2.39 | 1.92 | 2.44 |

**VERDICT: eff_rng does NOT replace the signed eff20 band.** (a) The signed
band DOMINATES the whole frontier — at matched trips (R1) it gives +0.10 PF,
and no eff_rng window reaches 2.367 at ANY trip count (the frontier is
one-sided, no iso-PF crossing to arbitrate). (b) eff_rng is hump-shaped with
a genuinely toxic high end ([0.65,0.8) = 0.75/0.49, 2025-26 near-zero — the
S38q "perfect linearity is bad" grammar on the range axis), but the 2D shows
the signed band already excludes ALL of it (band-IN row flat 2.26-2.35;
eff_rng >= 0.65 inside the band = 101 trips @ 2.79, and the insurance trim
cuts 4 trips = no-op). (c) Mechanism: direction-blindness is the defect —
eff_rng cannot distinguish an orderly 20m decline (fadeable, band-IN) from a
V that already recovered (range large, net ~0, band-OUT-right) or a straight
cliff (band-OUT-left). **The band's SIGN + both edges carry exactly the
exclusions eff_rng approximates, plus direction it cannot see. eff stays,
5th consecutive defense** (S39l ols_r/neffret, S39u slope vs dist, S39v/w
frontier + dist_vw, S39t eff10 reversal, now eff_rng).

## S40c — WARMUP REMOVED from `mr_candidate_1s`: the IPO/early-listing slice is BELOW-BOOK (2026-08-01)

**User:** the `barnum > 21` warmup is vestigial — nothing in the spec or (A')
needs any prior day — remove it to admit IPOs. Done: `mr_candidate_1s`
rebuilt (92.8s) WITHOUT the warmup; **`barnum` (ROW_NUMBER over the episode,
prior-only, live-knowable) is now a recorded COLUMN**, so the early slice
stays identifiable and post-hoc-cuttable. 1,121,785 rows (+6,993 early tkds,
+0.62%; only 236 are true listing-day rows — the rest are days 2-21 of fresh
episodes, which mixes IPOs with episode RESTARTS after listing gaps).
`flushfader_early_tkds` = the 6,993-row delta table; base pass over it =
95,695 trips on 2,086 signal tkds (`base_early_v1/`). **The full clean-
universe base = `base_v3/` ∪ `base_early_v1/`** (disjoint, same schema — glob
both). `flushfader_base_tkds` regenerated to the union: **59,294 tkds**
(57,208 + 2,086; now carries `barnum`).

The early slice under v1.7 ($1-$10 book):

| slice | trips | tkds | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| v1.7 early slice (barnum<=21) | 1,441 | 196 | 1.467 | 67.4 | +1.78 | 1.03 | 1.61 | 2.23 | 2.25 | 0.45 | 3.38 | 0.91 |
| barnum 1 (listing day) | 0 | 0 | - | - | - | - | - | - | - | - | - | - |
| barnum 2-5 | 583 | 80 | 1.929 | 70.7 | +1.80 | 0.18 | - | 2.42 | 2.74 | 0.90 | 7.37 | 0.14 |
| barnum 6-21 | 858 | 116 | 1.263 | 65.2 | +1.75 | 1.55 | 0.99 | 1.95 | 2.05 | 0.35 | 2.63 | 7.71 |

**Reading.** (a) Listing day itself NEVER signals through v1.7 (0 of 236
day-1 candidates) — the IPO-day flush is not in this book's grammar. (b) The
slice is positive but clearly BELOW-BOOK: 1.467 vs 2.367, under-book in 4 of
7 years, no stable barnum sub-band (2-5 vs 6-21 flips year to year). 196 tkds
≈ 28/yr = a real population, not an anecdote (S38j census discipline). (c)
Adding it to the book dilutes: 29,192 @ 2.367 → 30,633 @ ~2.31. **Decision
for the user:** accept the honest expanded universe (v1.7 reference numbers
shift slightly on the next engine rerun — the current `v17_reference/` was
cut on the warmed universe), or keep the universe and cut `barnum <= 21`
POST-HOC (now a legitimate, prior-only, recorded-column cut — NOT the old
lookahead). Either way the table build stays warmup-free.

## S40d — ⭐ SPEC v1.8 BAKED = v1.7 MINUS the −35% wall (user, 2026-08-01)

`DistHiLo` default → −Infinity (S39w evidence: under the accel/slope gates
the sub-−35 slice shows no cliff — 129 trips/28 tkds, all-clean-years, mc=1
2.184 vs 2.175; one parameter fewer). `DistHiHi = −0.10` (deep-enough) stays.
Reference `v18_reference/` running on the regenerated 59,294-tkd
`flushfader_base_tkds` (now INCLUDES the 2,086 early tkds — numbers below
split warmed vs early until the user decides that slice). Parity + book +
mc=1 to follow.

**eff_rng_20m on the v1.8 residual universe, BOTH eff gates OFF** (user
request; base_v3 = warmed universe, $1-$10 book): 52,676 trips @ 2.058.

| eff_rng_20m bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.3) | 4,710 | 1.647 | 66.8 | +1.88 | 1.97 | 2.52 | 0.82 | 1.1 | 1.84 | 1.65 | 1.49 |
| [0.3,0.35) | 7,474 | 1.84 | 70.4 | +1.87 | 2.69 | 3.6 | 0.99 | 1.37 | 1.83 | 1.6 | 1.25 |
| [0.35,0.4) | 11,082 | 2.242 | 72.7 | +2.09 | 3.02 | 2.59 | 1.33 | 2.08 | 1.77 | 2.53 | 2.32 |
| [0.4,0.45) | 11,641 | 2.354 | 73.6 | +1.98 | 4.13 | 2.81 | 1.65 | 1.74 | 2.3 | 1.87 | 2.41 |
| [0.45,0.5) | 8,859 | 2.189 | 73.4 | +2.08 | 2.75 | 3.66 | 1.33 | 2.24 | 2.38 | 1.87 | 1.83 |
| [0.5,0.55) | 5,185 | 2.055 | 72.2 | +1.88 | 3.57 | 4.21 | 1.33 | 1.22 | 1.71 | 1.81 | 2.66 |
| [0.55,0.6) | 2,377 | 2.155 | 74.6 | +1.89 | 8.05 | 4.65 | 1.1 | 0.87 | 2.7 | 1.65 | 4.62 |
| [0.6,0.65) | 883 | 1.644 | 72.5 | +1.81 | 21.43 | 8.67 | 1.62 | 1.83 | 2.21 | 0.66 | 0.96 |
| [0.65,0.7) | 269 | 0.79 | 63.9 | +1.41 | 16.4 | 2.96 | 0.25 | 2.26 | 5.71 | 0.18 | 0.03 |
| [0.7,0.8) | 172 | 0.648 | 55.2 | +0.88 | - | 1.85 | 0.53 | 1159.26 | 87.56 | 0.39 | 0.03 |
| [0.8,1.01) | 24 | 2.319 | 66.7 | +1.15 | - | - | 0.0 | - | - | 5.43 | - |

(Same hump as S40b — the wall removal barely perturbs it. The 2023 outlier
1159 in [0.7,0.8) is one tail trip; the bucket's other years are toxic.)

**v1.8 REFERENCE LANDED — GRAND PARITY ✓: engine 50,954 = SQL 50,954, zero
diff both directions** (SQL = v1.8-as-SQL over base_v3 ∪ base_early_v1;
`v18_reference/` = THE reference parquet, run on the 59,294-tkd union table,
so it CONTAINS the early slice — split below).

| slice | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| v1.8 book FULL universe | 30,797 | 2.321 | 73.2 | +2.04 | 3.59 | 3.42 | 1.56 | 1.68 | 2.21 | 1.98 | 2.4 |
| v1.8 book warmed only | 29,321 | 2.392 | 73.5 | +2.05 | 3.74 | 3.7 | 1.49 | 1.64 | 2.45 | 1.96 | 2.49 |
| v1.8 book early only | 1,476 | 1.425 | 66.7 | +1.72 | 1.03 | 1.45 | 2.23 | 2.34 | 0.44 | 2.8 | 0.91 |
| wall-restored slice (warmed, dist < -35) | 129 | 11.21 | 80.6 | +5.62 | 1.62 | - | - | - | 155.46 | 31.32 | - |

mc=1 (greedy replay, $1-$10 book):
- **warmed: 4,062 @ 2.184, ALL 7 YEARS POSITIVE** (2020 3.22 / 2021 2.74 /
  2022 1.80 / 2023 1.56 / 2024 2.22 / 2025 2.01 / 2026 1.88) — exactly the
  S39w projection. **The mc=1 ladder: 2.004 (v1.4) → 2.070 → 2.106 (v1.5) →
  2.104 (v1.6) → 2.175 (v1.7) → 2.184 (v1.8).**
- full universe (early in): 4,232 @ 2.144 — the early slice costs −0.040 at
  the operating point (dilutive at mc=1 in 5 of 7 years) for ~24 extra
  slot-trips/yr. Strengthens the case for the post-hoc `barnum <= 21` cut
  (user decision still open).

**⭐ SPEC v1.8 = v1.7 minus the −35% wall. Book (warmed) 29,321 @ 2.392 mc=0
/ 4,062 @ 2.184 mc=1. One parameter fewer; the recovered slice is the
129-trip deep-capitulation lottery (11.2 PF, tail-carried — S39v/w), now
simply part of the book.**

## S40e — EARLY SLICE CUT (user decision) + the |eff_20m| reference table (2026-08-01)

**User: cut the early slice from the long book.** Baked as **engine gate
`MinBarnum = 22`** (`--min-barnum`, 0 = off; column-guarded like the volat
prepass, so legacy tables skip it): candidate `barnum` >= 22 = the old
warmup's boundary, now prior-only and deliberate. The 6,993 early rows STAY
in `mr_candidate_1s` (and `flushfader_base_tkds`) — the future SHORT system
revisits them; the long engine just won't stream them.

**The survivorship subtlety the user spotted:** the OLD (pre-S39d)
`COUNT(*)-over-episode > 21` warmup did NOT actually exclude early-episode
days — any day of an episode that EVENTUALLY ran 22+ days qualified from day
1. What it excluded was early days of episodes that died young. So the old
momentum-era books were unknowingly trading the early slice — but only its
SURVIVORS (episode-length conditioning = the S39d lookahead, survivorship
flattering the slice). The prior-only rewrite silently excluded all of it;
today it was measured honestly (1,476 trips @ 1.425 mc=0, −0.040 at mc=1,
weak in 4-5 of 7 years) and is now excluded on the merits. **The IPO-flush
long thesis is dead: listing day never even signals through the spec, and
days 2-21 fade WORSE than seasoned tape — fresh listings don't mean-revert
like the established book does. Noted as a candidate SHORT-side asymmetry.**

**|eff_20m| reference table** (user request; v1.8-minus-eff universe = both
eff gates off, warmed base_v3, $1-$10 book, 52,676 trips): the sign is a
NON-EVENT on this universe — **99.9% of trips have eff_20m < 0** (30 trips
positive), so signed ≈ −|eff| and the signed convention stays (the band
[-0.5,-0.3) ≡ |eff| ∈ [0.3,0.5) here). Same hump-and-toxic-tail grammar as
eff_rng_20m (S40b/d):

| abs(eff_20m) bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.05) | 161 | 1.556 | 68.3 | +2.43 | 4.1 | 1.87 | 7.71 | 0.35 | 3.2 | - | 0.29 |
| [0.05,0.1) | 522 | 1.511 | 62.1 | +1.53 | 2.41 | 1.72 | 9.56 | 0.66 | 4.21 | 0.99 | 0.59 |
| [0.1,0.15) | 1,389 | 2.099 | 70.5 | +2.12 | 3.05 | 2.32 | 2.36 | 0.72 | 3.57 | 1.61 | 6.32 |
| [0.15,0.2) | 2,837 | 1.961 | 70.8 | +2.1 | 3.12 | 3.56 | 0.82 | 1.36 | 1.38 | 3.3 | 1.28 |
| [0.2,0.25) | 5,303 | 1.786 | 70.2 | +1.79 | 2.02 | 1.98 | 2.22 | 2.16 | 1.64 | 1.75 | 1.22 |
| [0.25,0.3) | 7,303 | 1.708 | 70.6 | +1.95 | 2.2 | 2.47 | 0.71 | 1.7 | 1.77 | 1.8 | 1.33 |
| [0.3,0.35) | 8,937 | 2.217 | 71.9 | +1.97 | 3.21 | 3.13 | 1.35 | 1.47 | 2.07 | 2.03 | 2.16 |
| [0.35,0.4) | 9,156 | 2.368 | 73.6 | +2.04 | 4.38 | 3.51 | 1.17 | 1.72 | 2.06 | 2.16 | 2.89 |
| [0.4,0.45) | 7,465 | 2.616 | 74.5 | +2.06 | 4.22 | 3.58 | 2.41 | 2.2 | 2.6 | 1.98 | 2.47 |
| [0.45,0.5) | 4,826 | 2.183 | 73.4 | +2.08 | 4.01 | 6.99 | 1.6 | 1.29 | 2.57 | 1.25 | 2.21 |
| [0.5,0.6) | 4,025 | 1.853 | 72.5 | +1.86 | 3.03 | 4.32 | 1.15 | 1.27 | 2.01 | 1.39 | 3.36 |
| [0.6,0.75) | 714 | 0.812 | 63.2 | +0.97 | 6.5 | 4.27 | 0.33 | 2.36 | 1.58 | 0.38 | 0.22 |
| [0.75,1.01) | 38 | 5.296 | 78.9 | +1.98 | - | - | 0.38 | - | - | - | - |

(The [0.75,1.01) 38-trip cell is tail-carried — its only populated clean year
is 0.38; anecdote, not a rescue of the extreme end. [0.6,0.75) is the real
toxic tail, mirroring eff_rng's [0.65,0.8).)

## S40a-addendum — FULL-RANGE speed table (user catch, 2026-08-01)

The S40a speed table stopped at −2% because the residual universe still
carried the spec's own `speed < -2%` gate (d1m is ungated, so its table ran
to 0). Apples-to-apples redo: **both axes on the v1.8-MINUS-speed universe**
(all other gates on, warmed base_v3, $1-$10 book): 40,926 trips @ 2.252.

| speed bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-8) | 1,162 | 3.825 | 74.4 | +3.73 | 11.64 | 13.41 | 1.32 | 1.72 | 9.44 | 2.41 | 3.34 |
| [-8,-6) | 2,000 | 2.575 | 72.6 | +2.58 | 4.97 | 6.36 | 0.65 | 2.04 | 3.05 | 1.76 | 2.94 |
| [-6,-5) | 2,417 | 2.9 | 74.8 | +2.61 | 6.8 | 3.6 | 2.15 | 1.31 | 3.94 | 2.46 | 2.43 |
| [-5,-4) | 4,357 | 2.392 | 73.6 | +2.24 | 6.71 | 4.27 | 1.24 | 1.79 | 2.5 | 1.66 | 1.81 |
| [-4,-3) | 7,819 | 2.157 | 73.6 | +1.95 | 3.28 | 3.59 | 1.44 | 1.27 | 2.03 | 1.89 | 2.44 |
| [-3,-2.5) | 5,408 | 2.308 | 74.2 | +1.96 | 2.84 | 3.74 | 1.71 | 1.82 | 1.96 | 2.04 | 2.42 |
| [-2.5,-2) | 6,158 | 2.124 | 72.5 | +1.78 | 2.16 | 2.87 | 1.88 | 2.29 | 1.57 | 1.97 | 2.74 |
| [-2,-1.5) | 5,825 | 1.827 | 72.4 | +1.63 | 2.53 | 2.71 | 1.2 | 1.8 | 1.51 | 1.31 | 2.73 |
| [-1.5,-1) | 4,186 | 1.88 | 72.0 | +1.49 | 2.36 | 2.66 | 1.17 | 1.91 | 1.46 | 1.61 | 2.79 |
| [-1,-0.5) | 1,495 | 1.768 | 71.9 | +1.22 | 1.4 | 2.85 | 1.73 | 1.26 | 1.29 | 1.86 | 1.95 |
| [-0.5,0) | 99 | 2.335 | 76.8 | +1.15 | 9.31 | 7.23 | 0.58 | 2.37 | 18.12 | 3.18 | 0.19 |
| [0,0.5) | 0 | | | | — structurally empty: a new 20m low with a positive |
| [0.5,100) | 0 | | | | 1m change is (near-)impossible by construction |

| d1m bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-12) | 152 | 6.837 | 84.9 | +8.07 | 13.96 | - | - | 0.14 | 472.55 | 27.05 | 2.19 |
| [-12,-10) | 230 | 5.81 | 77.8 | +4.32 | 4.28 | 22.81 | 6.12 | 3.78 | 27.13 | 5.71 | 2.39 |
| [-10,-8) | 634 | 2.696 | 71.1 | +2.81 | 8.0 | 14.96 | 0.24 | 1.78 | 9.49 | 1.24 | 5.09 |
| [-8,-6) | 1,898 | 3.0 | 74.2 | +2.55 | 7.15 | 5.73 | 1.16 | 1.22 | 4.76 | 1.58 | 4.41 |
| [-6,-5) | 2,551 | 2.169 | 71.3 | +2.32 | 4.63 | 3.45 | 1.24 | 1.93 | 2.03 | 1.59 | 2.17 |
| [-5,-4) | 4,544 | 2.27 | 73.2 | +2.38 | 3.83 | 3.23 | 1.62 | 1.76 | 2.77 | 1.74 | 1.74 |
| [-4,-3) | 8,579 | 2.416 | 74.6 | +2.17 | 3.77 | 3.8 | 1.22 | 1.42 | 2.34 | 2.52 | 2.37 |
| [-3,-2.5) | 6,082 | 2.127 | 72.7 | +1.9 | 2.69 | 3.35 | 1.76 | 2.07 | 1.56 | 1.83 | 2.2 |
| [-2.5,-2) | 6,628 | 2.208 | 74.1 | +1.79 | 3.05 | 3.28 | 1.49 | 2.0 | 1.87 | 1.67 | 3.16 |
| [-2,-1.5) | 5,694 | 1.728 | 71.9 | +1.5 | 2.06 | 3.06 | 1.28 | 1.45 | 1.14 | 1.39 | 2.73 |
| [-1.5,-1) | 3,226 | 1.836 | 70.7 | +1.28 | 2.01 | 2.18 | 1.79 | 1.93 | 1.71 | 1.56 | 1.62 |
| [-1,-0.5) | 686 | 2.179 | 74.6 | +1.06 | 2.72 | 2.47 | 2.63 | 0.9 | 1.5 | 4.22 | 1.76 |
| [-0.5,0.01) | 22 | 3.493 | 68.2 | +0.79 | - | 0.72 | 1.86 | 1.24 | 117.52 | 6.39 | - |

**Reading.** (a) The speed gate earns its seat plainly here: the relaxed
region speed ∈ [-2, 0) = 11,605 trips @ ~1.83 (mediocre EVERY year, never
toxic, never good) — cutting it is exactly book 40,926 @ 2.252 → 29,321 @
2.392. (b) With speed ungated, d1m's shallow-end dip moves to [-2,-1) (1.73/
1.84) and the extreme-shallow buckets RECOVER (2.18/3.49 on small n) — those
are "20m low with a flat last minute" trips, a different animal from the
speed-gated shallow slice. The two axes remain 0.91-correlated; d1m's deep
end is still the cleaner monotone story.

**v18_reference REGENERATED with the barnum gate (warmed-only) — GRAND
PARITY ✓: engine 48,108 = SQL-over-base_v3 48,108, zero diff both
directions.** Book 29,321 @ 2.392 mc=0 / 4,062 @ 2.184 mc=1 (identical trip
set to the S40d warmed split — the gate exactly reproduces the post-hoc
cut). **`v18_reference/` = THE reference parquet; the v1.8 production stack
is: `mr_candidate_1s` (warmup-free, barnum recorded) → engine `MinBarnum=22`
+ SPEC v1.8 gates → post-hoc $1-$10.**

## S40f — experiment: eff_rng_20m band [0.35,0.6) x abs(eff_20m) breakdown → eff_rng DROPPED (user, 2026-08-01)

Universe = v1.8-minus-eff × `eff_rng_20m ∈ [0.35,0.6)` (warmed base_v3,
$1-$10 book): 39,144 trips @ 2.230.

The [0.6,0.75) and [0.75,1.01) buckets are **empty BY CONSTRUCTION**:
eff_rng >= |eff| on every row, so the eff_rng < 0.6 cap deletes the |eff|
toxic tail automatically.

| abs(eff_20m) bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.05) | 5 | inf | 100.0 | +0.16 | - | - | inf | - | - | - | - |
| [0.05,0.1) | 48 | 4.666 | 75.0 | +3.18 | 4.16 | 0.57 | inf | inf | 7.96 | 1.53 | inf |
| [0.1,0.15) | 306 | 2.221 | 73.9 | +2.15 | 52.45 | 2.3 | 13.89 | 0.22 | 10.68 | 0.8 | 3.47 |
| [0.15,0.2) | 730 | 1.835 | 72.2 | +2.16 | 2.75 | 4.64 | 1.54 | 1.71 | 0.82 | 4.24 | 2.01 |
| [0.2,0.25) | 2,308 | 1.701 | 68.8 | +1.65 | 4.22 | 1.59 | 1.8 | 2.77 | 1.18 | 3.4 | 0.55 |
| [0.25,0.3) | 4,210 | 1.89 | 72.7 | +2.06 | 2.24 | 1.78 | 1.04 | 3.06 | 2.11 | 1.55 | 1.99 |
| [0.3,0.35) | 6,661 | 2.413 | 73.2 | +2.01 | 2.81 | 2.88 | 1.21 | 1.72 | 2.38 | 2.91 | 2.49 |
| [0.35,0.4) | 9,150 | 2.367 | 73.6 | +2.04 | 4.37 | 3.51 | 1.17 | 1.72 | 2.06 | 2.16 | 2.89 |
| [0.4,0.45) | 7,444 | 2.609 | 74.5 | +2.05 | 4.2 | 3.57 | 2.41 | 2.2 | 2.6 | 1.98 | 2.42 |
| [0.45,0.5) | 4,751 | 2.183 | 73.3 | +2.09 | 3.84 | 6.94 | 1.56 | 1.29 | 2.52 | 1.27 | 2.38 |
| [0.5,0.6) | 3,531 | 1.897 | 72.6 | +1.83 | 2.55 | 4.26 | 1.1 | 1.2 | 1.76 | 1.74 | 5.26 |
| [0.6,0.75) | 0 | - | - | - | - | - | - | - | - | - | - |
| [0.75,1.01) | 0 | - | - | - | - | - | - | - | - | - | - |

**Reading:** (a) the eff_rng floor guts the flat-net low end ([0,0.15): 161/
522/1,389 → 5/48/306 — low-|eff| tape is overwhelmingly also low-range
tape); (b) the |eff| HUMP SURVIVES inside the band ([0.2,0.3) still 1.70/
1.89, [0.3,0.5) still peaks) — eff_rng's edges are a cheaper implementation
of both eff cutoffs but cannot replicate the interior hump; stacking
|eff| ∈ [0.3,0.5) inside the band lands at ~28,006 @ ~2.40 ≈ the current
pair's operating point via two features instead of one.

**DECISIONS (user):** `eff_rng_20m` is DROPPED as a gate candidate (stays a
recorded column). The eff pair stays AS-IS in the spec; follow-up tests run
with the |eff_20m|-band and |eff_10m| filters ENABLED. (Signed-vs-unsigned
is a non-event — 99.9% of the residual universe has eff_20m < 0 — so the
band [-0.5,-0.3) and |eff| ∈ [0.3,0.5) are interchangeable to within 30
trips; the spec keeps the existing signed-band implementation.)

## S40g — d1m x speed COMBINED (user direction): the AND-gate wins at BOTH mc levels (2026-08-01)

**User insight:** the S40a-addendum tables showed the speed gate cutting off
d1m's weak shallow end as pure benefit → don't choose between the 1m axes,
COMBINE them. Universe = v1.8-minus-speed, eff pair ON (warmed base_v3,
$1-$10 book): 40,926 trips @ 2.252.

2D (n / PF) — the DIAGONAL carries the book; both-deep is where the edge
lives, and each axis's shallow slice is weak REGARDLESS of the other:

| speed \ d1m | [-100,-8) | [-8,-6) | [-6,-4) | [-4,-3) | [-3,-2.5) | [-2.5,-2) | [-2,-1.5) | [-1.5,0.01) |
|---|---|---|---|---|---|---|---|---|
| [-100,-6) | 995 / 3.7 | 1,414 / 3.16 | 699 / 2.08 | 48 / 3.07 | 4 / 0.47 | 2 / 0.0 | 0 | 0 |
| [-6,-4) | 21 / 107.68 | 449 / 2.73 | 4,623 / 2.49 | 1,458 / 2.93 | 164 / 1.66 | 50 / 1.87 | 6 / 0.75 | 3 / inf |
| [-4,-3) | 0 | 21 / 1.14 | 1,457 / 2.02 | 4,189 / 2.23 | 1,371 / 2.3 | 600 / 2.15 | 156 / 1.18 | 25 / 4.75 |
| [-3,-2.5) | 0 | 12 / 2.54 | 203 / 0.96 | 1,688 / 2.33 | 1,848 / 2.77 | 1,152 / 3.01 | 420 / 1.4 | 85 / 1.87 |
| [-2.5,-2) | 0 | 2 / inf | 77 / 1.62 | 806 / 2.83 | 1,604 / 1.9 | 2,103 / 2.4 | 1,255 / 1.94 | 311 / 1.21 |
| [-2,-1.5) | 0 | 0 | 28 / 3.12 | 314 / 2.35 | 839 / 1.55 | 1,813 / 1.94 | 1,897 / 1.75 | 934 / 1.87 |
| [-1.5,-1) | 0 | 0 | 6 / 0.95 | 69 / 1.81 | 215 / 1.95 | 767 / 1.72 | 1,559 / 1.78 | 1,570 / 2.14 |
| [-1,0.01) | 0 | 0 | 2 / inf | 7 / 2.44 | 37 / 1.83 | 141 / 2.03 | 401 / 1.59 | 1,006 / 1.83 |

Combined-gate frontier (mc=0):

| gate | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| A: speed<-2 (CURRENT v1.8) | 29,321 | 2.392 | 73.5 | 3.74 | 3.7 | 1.49 | 1.64 | 2.45 | 1.96 | 2.49 |
| B: d1m<-2 only | 31,298 | 2.372 | 73.6 | 3.72 | 3.62 | 1.4 | 1.69 | 2.43 | 1.91 | 2.55 |
| C: d1m<-1.5 only | 36,992 | 2.28 | 73.4 | 3.49 | 3.51 | 1.38 | 1.66 | 2.25 | 1.84 | 2.57 |
| D: speed<-2 AND d1m<-1.5 | 28,897 | 2.406 | 73.6 | 3.84 | 3.76 | 1.47 | 1.65 | 2.46 | 1.96 | 2.48 |
| E: speed<-2 AND d1m<-2 | 27,060 | 2.453 | 73.9 | 4.0 | 3.84 | 1.44 | 1.74 | 2.56 | 1.97 | 2.46 |
| F: speed<-2 AND d1m<-2.5 | 23,153 | 2.449 | 73.7 | 3.98 | 3.81 | 1.42 | 1.68 | 2.65 | 1.97 | 2.43 |
| G: speed<-1.5 AND d1m<-2 | 30,054 | 2.395 | 73.7 | 3.8 | 3.66 | 1.42 | 1.71 | 2.48 | 1.92 | 2.52 |
| H: speed<-2 OR d1m<-2.5 | 30,838 | 2.36 | 73.4 | 3.67 | 3.65 | 1.45 | 1.61 | 2.37 | 1.95 | 2.53 |

E is the knee: −2 on both axes; tightening d1m further (F) buys nothing.
Either gate ALONE (B/C) is worse than the current spec — the value is the
CONJUNCTION (fast last minute AND a real 1m leg below the 1m high).

**⭐ mc=1 (greedy replay, E vs current): 3,936 @ 2.211 vs 4,062 @ 2.184 —
+0.027 AT THE OPERATING POINT, better in 6 of 7 years** (2020 3.26/3.22,
2021 2.77/2.74, 2022 1.82/1.80, 2023 1.59/1.56, 2024 2.29/2.22, 2025
2.03/2.01; only 2026 gives back 1.85/1.88). **The FIRST challenger of the
S39-S40 arc to win at BOTH mc levels** — the slot-allocation curse hit
REPLACEMENTS (slope-for-dist, dist_vw-for-dist, rngfront); this is a
TIGHTENING, and the trips it removes (the d1m-shallow slice) are slot
thieves, not slot payers. Bake candidate: `d1m < -2%` gate (engine:
`vwap/max60 - 1 < -0.02` at the signal, hi_60 already maintained). USER
DECISION pending.

## S40h — ⭐ SPEC v1.9 BAKED = v1.8 + d1m < −2% (user, 2026-08-01) + dist-20m-VWAP under v1.9

**SPEC v1.9 = v1.8 + `vwap/hi_60 − 1 < −0.02`** (the 1m-leg conjunction,
S40g; cutoff −2 from the fine table — the boundary is SHARP: [−2.25,−2) =
2.37, [−2,−1.75) = 1.71, no recovery shallower). Engine: `MaxDist1mHi`
(`--max-dist-1m`, >= 0 = off; post-push max60, identical to recorded hi_60,
gate evaluated after the bar folds like speedOk). ⚠ The canonical base-pass
CLI (S40) gains `--max-dist-1m 0`.

**`v19_reference/` GRAND PARITY ✓: engine 44,502 = SQL 44,502, zero diff
both directions. Book 27,060 @ 2.453 / 73.9% mc=0; mc=1 3,936 @ 2.211, all
7 years positive** (3.26/2.77/1.82/1.59/2.29/2.03/1.85) — both EXACTLY the
S40g projections. **The mc=1 ladder: 2.004 (v1.4) → 2.070 → 2.106 (v1.5) →
2.104 (v1.6) → 2.175 (v1.7) → 2.184 (v1.8) → 2.211 (v1.9).**

**dist-20m-VWAP breakdown** (user request; d20m-high held at the spec's
< −10%; v1.9 residual universe, warmed base_v3, $1-$10 book: 27,060 @
2.453). dvw = signal_vwap/vwap_1200 − 1; q05/med/q95 = −15.22/−8.12/−5.41;
corr(dvw, d20m-high) = 0.932. Buckets above −3% are structurally empty (the
d20m gate forces the VWAP distance deep); [−4,−3) = 14 trips, all winners.

| dist-20m-vwap bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-14) | 2,076 | 3.606 | 78.0 | +3.84 | 10.19 | 2.84 | 0.52 | 2.57 | 5.02 | 4.05 | 3.52 |
| [-14,-12) | 1,910 | 2.998 | 78.2 | +3.16 | 11.99 | 4.9 | 1.0 | 1.74 | 4.64 | 2.17 | 2.85 |
| [-12,-10) | 3,620 | 2.69 | 77.3 | +2.77 | 3.58 | 8.06 | 1.38 | 1.91 | 3.04 | 2.09 | 2.78 |
| [-10,-9) | 2,749 | 2.066 | 72.2 | +2.05 | 2.54 | 4.15 | 1.48 | 1.21 | 2.13 | 1.87 | 2.0 |
| [-9,-8) | 3,680 | 2.201 | 70.8 | +1.95 | 3.26 | 2.59 | 2.63 | 1.47 | 1.86 | 2.1 | 2.08 |
| [-8,-7) | 4,607 | 2.119 | 72.1 | +1.99 | 5.5 | 2.9 | 1.26 | 1.48 | 2.49 | 1.35 | 2.5 |
| [-7,-6) | 4,859 | 2.406 | 75.4 | +1.88 | 3.37 | 5.61 | 2.7 | 2.05 | 1.55 | 1.65 | 1.73 |
| [-6,-5) | 3,040 | 2.188 | 71.0 | +1.54 | 2.72 | 2.97 | 1.62 | 2.5 | 1.97 | 1.46 | 2.55 |
| [-5,-4) | 505 | 1.67 | 66.3 | +1.25 | 1.25 | 4.68 | 1.15 | 5.13 | 1.03 | 1.48 | 1.32 |
| [-4,-3) | 14 | inf | 100.0 | +2.32 | - | - | inf | inf | inf | inf | inf |

**Reading:** monotone DEEP end (≤ −10% = 2.69/3.00/3.61, strong 2024-26)
with a clear **2022 inversion in the two deepest buckets (0.52 / 1.00)** —
deep-below-20m-VWAP in a bear regime is the falling knife (the bear-regime
theme landing on 2022 here). Shallow [−5,−4) mildly weak (1.67) but 505
trips and not year-consistent; middle flat 2.1-2.4 (0.93 corr with the
already-gated d20m-high leaves little there). **Verdict: NOT a gate — the
deep end (≤ −12%, ~4.0k trips @ ~3.2) joins the overlay/sizing roster with
an explicit 2022 regime caveat** (same family as dsv ≥ −3, but on the 20m
clock and bear-fragile where dsv is bear-robust).

## S40i — ⭐ SPEC v2.0 BAKED = v1.9 + dvw < −5% + the ABS eff20 redesign; ols_r tables (2026-08-01)

**SPEC v2.0 = v1.9 + `vwap/vwap_1200 − 1 < −0.05`** (user: trim the shallow
dvw tail — the [−5,−4) 1.67 bucket; `MaxDistVw20m`/`--max-dist-vw20m`,
>= 0 = off, cold vwap_1200 fails) **+ the eff20 REDESIGN (user): the gate is
now an ABSOLUTE band `|eff_20m| ∈ [0.3, 0.5)`** (`AbsEff20Lo/Hi`,
`--abs-eff20-lo/--abs-eff20-hi`) — one |·| convention for both eff measures,
mirroring |eff10|. The vestigial `MinAbsEff20m` sweep field/flag/gate is
DELETED (the abs floor subsumes it), and the banner's "COLD FAILS" label is
dropped (behavior unchanged and uniform since S38n: an unwarm feature fails
any active gate; the v1.2 cold-pass special case was the only switch that
ever existed and S38n deleted it). Canonical eff SQL from here on:
`abs(eff_20m) >= 0.3 AND abs(eff_20m) < 0.5`. ⚠ Canonical base-pass CLI:
`--eff20-lo/-hi/--min-abs-eff-20m` are GONE; use `--abs-eff20-lo 0
--abs-eff20-hi Infinity --max-dist-1m 0 --max-dist-vw20m 0` plus the S40
list.

**`v20_reference/` GRAND PARITY ✓: engine 43,587 = SQL 43,587, zero diff.
Book 26,541 @ 2.465 / 74.0%** (years 4.08/3.82/1.45/1.72/2.59/1.98/2.48);
**mc=1 3,863 @ 2.218, all 7 years positive** (3.32/2.72/1.89/1.58/2.29/
2.04/1.83). **Ladder: 2.004 → 2.070 → 2.106 → 2.104 → 2.175 → 2.184 (v1.8)
→ 2.211 (v1.9) → 2.218 (v2.0).** The abs-band redesign is a true no-op on
the book: 26,541 @ 2.465 EXACTLY matches the signed-band cut — the slope
gates already exclude every positive-eff trip on this universe.

**ols_r breakdowns** (user request — never shown yesterday; v2.0 residual
universe, warmed base_v3, $1-$10 book, 26,541 @ 2.465). r = sign(slope) ×
sqrt(R²) of the OLS on ln(vwap); r20 q05/med/q95 = −0.953/−0.885/−0.676;
r10 = −0.947/−0.828/−0.371; **corr(r10, r20) = 0.146** — the slope/accel
gates have DECORRELATED the two scales.

| ols_r_1200 (20m) bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-1.005,-0.99) | 0 | - | - | - | - | - | - | - | - | - | - |
| [-0.99,-0.97) | 140 | 3.7 | 75.0 | +2.3 | inf | 5.59 | inf | 1.52 | 7.73 | 0.74 | 3.69 |
| [-0.97,-0.95) | 1,620 | 2.387 | 72.9 | +2.23 | 1.64 | 3.27 | 7.19 | 3.82 | 4.91 | 1.83 | 1.51 |
| [-0.95,-0.9) | 9,142 | 2.506 | 75.8 | +2.24 | 5.82 | 3.44 | 1.94 | 2.27 | 2.3 | 1.74 | 2.4 |
| [-0.9,-0.85) | 6,977 | 2.239 | 73.7 | +2.07 | 4.11 | 4.15 | 1.04 | 1.69 | 2.01 | 1.86 | 2.46 |
| [-0.85,-0.8) | 3,681 | 2.573 | 73.0 | +2.24 | 5.23 | 4.17 | 0.89 | 1.43 | 4.11 | 2.98 | 1.04 |
| [-0.8,-0.7) | 3,275 | 2.357 | 71.9 | +2.0 | 2.66 | 3.21 | 2.06 | 0.99 | 2.57 | 2.09 | 7.5 |
| [-0.7,-0.6) | 1,088 | 3.541 | 72.5 | +2.11 | 3.23 | 7.16 | 0.88 | 1.81 | 5.27 | 2.39 | 23.71 |
| [-0.6,-0.5) | 444 | 3.239 | 71.8 | +1.75 | 2.29 | 10.27 | 1.0 | 2.29 | 3.19 | 2.65 | 7.34 |
| [-0.5,-0.3) | 172 | 4.15 | 78.5 | +3.5 | 47.73 | inf | 1.07 | 0.0 | 3.87 | 4.11 | 2.68 |
| [-0.3,0) | 2 | 1.077 | 50.0 | +0.02 | 1.08 | - | - | - | - | - | - |
| [0,1.005) | 0 | - | - | - | - | - | - | - | - | - | - |

| ols_r_600 (10m) bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-1.005,-0.99) | 2 | inf | 100.0 | +1.95 | inf | - | - | - | - | - | - |
| [-0.99,-0.97) | 144 | 1.914 | 68.8 | +1.75 | 1.05 | 77.34 | 15.37 | 1.02 | 0.89 | 3.32 | 4.45 |
| [-0.97,-0.95) | 912 | 2.202 | 73.5 | +1.84 | 10.49 | 4.05 | 0.18 | 1.71 | 1.43 | 5.21 | 149.53 |
| [-0.95,-0.9) | 5,080 | 2.67 | 75.0 | +2.35 | 3.16 | 3.58 | 1.1 | 2.42 | 2.34 | 3.21 | 3.56 |
| [-0.9,-0.85) | 5,167 | 2.293 | 75.2 | +2.25 | 2.96 | 4.29 | 1.24 | 1.32 | 3.91 | 1.63 | 2.78 |
| [-0.85,-0.8) | 3,962 | 2.97 | 75.5 | +2.3 | 4.72 | 4.27 | 2.43 | 1.58 | 3.82 | 2.18 | 2.89 |
| [-0.8,-0.7) | 4,849 | 2.436 | 73.9 | +2.06 | 6.31 | 4.78 | 2.63 | 1.66 | 2.41 | 1.43 | 2.02 |
| [-0.7,-0.6) | 2,361 | 3.046 | 75.6 | +2.06 | 10.81 | 5.53 | 4.25 | 2.6 | 2.37 | 2.24 | 1.49 |
| [-0.6,-0.5) | 1,546 | 1.986 | 70.7 | +1.82 | 3.55 | 5.23 | 1.28 | 0.75 | 1.94 | 1.79 | 1.92 |
| [-0.5,-0.3) | 1,580 | 2.149 | 70.5 | +1.84 | 4.69 | 2.26 | 0.92 | 1.76 | 2.53 | 1.67 | 2.27 |
| [-0.3,0) | 761 | 1.583 | 64.1 | +1.77 | 2.62 | 1.03 | 4.17 | 2.25 | 2.1 | 1.31 | 0.58 |
| [0,1.005) | 177 | 2.492 | 68.9 | +1.82 | 4.73 | 0.5 | 68.98 | 6.59 | 0.99 | 4.36 | 1.58 |

**Reading: the S38q two-scale grammar has been EATEN by the slope/accel
gates.** r20's old "linearity good" gradient is gone; the residual hot spot
is the NON-linear end ([−0.7,−0.5) = 3.2-4.2, the A++ lin20 inversion:
chaotic crash > orderly slide) but 2022 is weak in every one of those cells
and 2026's 23.7 is tail-carried. r10's weakest cell is the flat-fit end
[−0.3,0) (1.583, 761 trips) but 2022 = 4.17 breaks year-consistency. The
near-zero r10↔r20 correlation is the real news — post-gates the two scales
are independent axes, yet neither offers a year-robust cutoff. **Verdict:
overlay-grade at best (r20 ∈ [−0.7,−0.5) as a chaos lens with a 2022
caveat); NOT gates.**

## S40j — relaxing the two 20m distance gates: NO FREE TRIPS (user question, 2026-08-01)

Can d20 < −10% or dvw < −5% be relaxed to admit trips without hurting PF?
Universe = v2.0 minus the gate under study (other held), warmed base_v3,
$1-$10 book.

d20 relaxation region (dvw < −5 held):

| d20 bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-10,-9) | 1,826 | 1.928 | 71.4 | +1.33 | 2.36 | 2.57 | 1.23 | 2.4 | 2.03 | 1.72 | 0.98 |
| [-9,-8) | 643 | 2.178 | 75.3 | +1.54 | 1.69 | 5.74 | 2.14 | 1.15 | 0.68 | 4.72 | 1.28 |
| [-8,-7) | 72 | 1.291 | 73.6 | +1.17 | 21.99 | inf | 0.5 | 0.03 | 1.18 | 1.0 | inf |
| shallower | 0 | | | | (empty — the other gates cap how shallow d20 can run) |

dvw relaxation region (d20 < −10 held): [−5,−4.5) = 385 @ 1.672,
[−4.5,−4) = 120 @ 1.665, below −4 near-empty (14 trips). The both-off 2D
shallow region has NO cell at or above book PF (best 2.05; most 0.5-2.0).

Frontier (mc=0): every relaxation is monotone-dilutive — R1 d20<−9 = 28,367
@ 2.435 (−0.030); R2 d20<−8 = 29,010 @ 2.431 (−0.034); R3 dvw<−4 = 27,046 @
2.452 (−0.013); R6 both = 31,274 @ 2.404 (−0.061). (Full frontier table in
the S40j analysis run; CURRENT = 26,541 @ 2.465.)

**⭐ The mc=1 idle-slot hypothesis TESTED** (could below-book trips fill
empty slots for free?): R2 (d20<−8) at mc=1 = **4,210 @ 2.159 vs current
3,863 @ 2.218** — +347 slot-trips (+9%) buys only ~+120 points (+2.6% over
6.5y) at −0.059 PF, equal-or-worse in 6 of 7 years. The added trips earn
~1/3 of the book's per-trip average even when slots are free. **VERDICT: the
distance floors sit AT the efficient frontier — no relaxation is PF-neutral
at either mc level; v2.0 stands.** (Symmetric to the S40g finding: the d1m/
dvw tightenings removed slot thieves; their reverse admits slot beggars.)

## S40k — the volume-shape / day-structure sweep: THREE new overlays, one new A+ stack (2026-08-01)

Six features derivable from RECORDED columns (no engine change), all on the
v2.0 residual universe (26,541 @ 2.465, warmed base_v3, $1-$10 book). The
spec to date is almost entirely PRICE-shape; this is the volume/structure
side. Quantiles: cresc q05/med/q95 = 0.64/0.97/1.32; psize 0.76/1.02/1.39;
lowdens 0.015/0.040/0.113; rs60 0.12/0.23/0.42; pco −32.7/−8.8/+45.4.

**A. Volume crescendo (vol_600/600)/(vol_1200/1200): WEAK.** Mild inverse
gradient (quiet-5m 2.5-2.8 vs loud-5m 2.1-2.5) — the S13 quiet-inversion at
a slower scale, but year-mixed; no knife.

| cresc bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.7) | 2,378 | 2.542 | 75.3 | +2.16 | 2.83 | 3.35 | 0.69 | 1.65 | 2.69 | 3.14 | 9.7 |
| [0.7,0.85) | 5,349 | 2.775 | 74.0 | +2.26 | 7.37 | 3.15 | 1.78 | 2.48 | 2.11 | 2.65 | 2.0 |
| [0.85,1.0) | 7,216 | 2.51 | 74.7 | +2.14 | 3.71 | 3.45 | 1.49 | 2.7 | 2.13 | 1.99 | 2.94 |
| [1.0,1.15) | 6,465 | 2.149 | 72.2 | +2.11 | 3.24 | 3.77 | 1.37 | 1.64 | 3.2 | 1.36 | 1.86 |
| [1.15,1.3) | 3,607 | 2.472 | 74.5 | +2.09 | 4.18 | 6.21 | 3.27 | 0.89 | 4.73 | 1.61 | 1.95 |
| [1.3,1.5) | 1,396 | 2.56 | 75.0 | +2.21 | 4.96 | 12.49 | 0.72 | 0.61 | 1.77 | 2.51 | 12.49 |
| [1.5,1.8) | 125 | 3.909 | 74.4 | +1.69 | inf | inf | inf | 17.91 | 10.05 | 2.35 | 1.51 |

**B. Print-size compression (avgsize_60/avgsize_1200): MILD hump** at
[1.0,1.3) (2.65-2.80 — bigger prints at the low = institutional
participation); tails weak but small-n. Not a gate.

| psize bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.5) | 39 | 0.993 | 51.3 | +0.41 | - | 6.87 | 0.0 | - | 0.0 | 4.82 | - |
| [0.5,0.7) | 570 | 2.48 | 74.7 | +2.62 | 1.88 | 4.04 | 1.32 | 3.85 | 2.59 | 2.23 | 3.64 |
| [0.7,0.85) | 2,865 | 2.264 | 73.5 | +2.15 | 4.45 | 5.01 | 0.87 | 2.31 | 2.04 | 2.47 | 3.54 |
| [0.85,1.0) | 8,237 | 2.275 | 72.9 | +2.15 | 2.69 | 3.72 | 1.36 | 2.53 | 2.57 | 1.86 | 2.09 |
| [1.0,1.15) | 8,354 | 2.8 | 75.9 | +2.2 | 5.32 | 4.1 | 1.73 | 1.9 | 3.23 | 2.23 | 2.05 |
| [1.15,1.3) | 4,127 | 2.654 | 74.2 | +2.19 | 5.2 | 2.92 | 2.63 | 1.13 | 2.45 | 1.96 | 4.43 |
| [1.3,1.5) | 1,681 | 2.274 | 70.6 | +1.8 | 3.72 | 3.38 | 2.36 | 0.55 | 2.3 | 2.15 | 3.94 |
| [1.5,2.0) | 606 | 1.84 | 74.6 | +1.9 | 7.3 | 9.4 | 2.92 | 0.63 | 2.53 | 0.66 | 1.47 |

**C. ⭐ LOW DENSITY (lows/bars since first low): THE new axis.** Monotone
rising — the WATERFALL (relentless low-making) beats the GRIND; the
[0.08,0.12) band = 3.725 on 1,994 (worst year 1.66); the [0,0.03) grind =
8,276 @ 2.075, a third of the book below-book.

| lowdens bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.03) | 8,276 | 2.075 | 73.0 | +2.05 | 3.78 | 4.74 | 1.51 | 1.07 | 2.91 | 1.42 | 2.19 |
| [0.03,0.05) | 9,055 | 2.403 | 73.7 | +2.17 | 2.74 | 4.15 | 1.45 | 2.41 | 2.14 | 2.19 | 2.47 |
| [0.05,0.08) | 6,068 | 2.82 | 74.7 | +2.19 | 8.2 | 3.17 | 1.22 | 2.54 | 2.72 | 2.54 | 2.53 |
| [0.08,0.12) | 1,994 | 3.725 | 77.1 | +2.24 | 4.6 | 5.18 | 3.84 | 1.66 | 2.92 | 4.59 | 3.78 |
| [0.12,0.18) | 770 | 3.287 | 74.9 | +2.42 | 21.77 | 1.73 | 1.1 | 1.24 | 4.88 | 2.44 | 9.15 |
| [0.18,0.25) | 260 | 3.298 | 76.9 | +2.09 | 38.79 | 4.06 | 14.63 | 0.87 | 5.13 | 0.62 | 5.86 |
| [0.25,0.35) | 83 | 2.034 | 69.9 | +3.47 | inf | 4.27 | inf | 0.39 | 0.65 | 16.23 | 33.59 |
| [0.35,0.5) | 31 | 1.58 | 77.4 | +4.23 | 0.25 | - | - | inf | inf | 1.21 | - |

**D. 1m range share rng_60/rng_20m: U-shaped**, both extremes strong
([0.08,0.12) = 3.97/80.2%; ≥0.55 = 4.44 tail-carried), middle flat.
Overlay-grade curiosity.

| rs60 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.08) | 105 | 3.704 | 69.5 | +1.43 | 1.39 | 5.69 | 0.53 | 9.01 | 9.51 | 4.48 | inf |
| [0.08,0.12) | 1,067 | 3.968 | 80.2 | +2.62 | 9.77 | 5.02 | 1.19 | 2.66 | 2.27 | 8.72 | 5.13 |
| [0.12,0.16) | 3,095 | 2.821 | 76.8 | +2.33 | 4.99 | 2.75 | 1.17 | 3.11 | 2.73 | 3.53 | 1.71 |
| [0.16,0.2) | 5,326 | 2.469 | 75.3 | +2.23 | 4.71 | 4.73 | 1.42 | 2.23 | 2.07 | 1.8 | 2.47 |
| [0.2,0.25) | 6,682 | 2.17 | 73.2 | +2.02 | 2.45 | 3.48 | 1.87 | 1.7 | 2.32 | 1.71 | 2.1 |
| [0.25,0.3) | 4,463 | 2.171 | 72.3 | +1.99 | 5.53 | 3.16 | 0.95 | 1.14 | 2.24 | 1.7 | 3.19 |
| [0.3,0.4) | 4,218 | 2.562 | 72.7 | +2.1 | 5.47 | 4.49 | 1.69 | 1.5 | 3.35 | 1.49 | 3.09 |
| [0.4,0.55) | 1,369 | 2.742 | 71.5 | +2.18 | 5.89 | 5.69 | 1.6 | 1.59 | 6.02 | 1.83 | 2.13 |
| [0.55,100) | 216 | 4.436 | 75.9 | +4.12 | 1.84 | 542.85 | 13.35 | 0.86 | 40.22 | 13.57 | 1.53 |

**E. ⭐ GREEN-FROM-OPEN (pct_chg_open): the day-structure axis.** Flushes on
names still UP ≥2% from the open (a morning spike crashing back but still
green) = 7,340 @ 3.488 — 28% of the book, all 7 years ≥ 1.84. The [−3,−1)
slightly-red slice = 1.36. pco ↔ dsv corr 0.576 — the big-slice cousin of
the dsv overlay.

| pco bucket (%) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-100,-20) | 5,013 | 2.236 | 74.1 | +2.39 | 3.09 | 4.61 | 1.26 | 1.87 | 2.22 | 1.58 | 3.49 |
| [-20,-15) | 3,198 | 2.095 | 71.0 | +1.96 | 3.61 | 5.63 | 1.08 | 1.57 | 2.51 | 1.7 | 1.29 |
| [-15,-10) | 4,092 | 2.694 | 74.0 | +2.09 | 5.05 | 3.09 | 2.87 | 1.98 | 2.42 | 2.84 | 1.46 |
| [-10,-7) | 2,471 | 2.286 | 72.6 | +1.99 | 6.05 | 2.82 | 1.66 | 2.2 | 3.13 | 1.21 | 1.72 |
| [-7,-5) | 1,395 | 1.994 | 69.6 | +1.78 | 4.53 | 2.4 | 2.02 | 1.07 | 1.88 | 1.35 | 2.15 |
| [-5,-3) | 901 | 2.003 | 72.3 | +2.22 | 1.5 | 4.04 | 0.81 | 2.09 | 4.78 | 2.06 | 1.2 |
| [-3,-1) | 810 | 1.359 | 68.6 | +1.44 | 1.36 | 4.21 | 0.28 | 2.01 | 5.62 | 1.39 | 1.44 |
| [-1,0) | 346 | 2.268 | 66.8 | +1.61 | 4.04 | 2.68 | 1.99 | 8.7 | 2.1 | 1.25 | 0.64 |
| [0,2) | 711 | 1.887 | 78.5 | +2.51 | 4.48 | 3.84 | 1.69 | 0.31 | 3.43 | 3.29 | 1.73 |
| [2,100) | 7,340 | 3.488 | 77.6 | +2.26 | 7.08 | 5.62 | 1.84 | 2.06 | 2.74 | 2.95 | 6.31 |

**F. ⭐ TAPE CONTINUITY (gap_60): continuous tape = the best fades.** 0-3
missing seconds in the last minute = 10,799 @ ~3.44; the [4,16) trough =
7,104 @ ~1.78 (27% of book); very-gappy partially recovers (2.23). Liquid
real-time panic fades best; the S38p gap counters finally pay off.

| gap_60 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,1) | 6,619 | 3.497 | 77.3 | +2.45 | 6.14 | 7.04 | 2.32 | 1.92 | 2.83 | 2.53 | 4.42 |
| [1,2) | 1,872 | 3.421 | 78.7 | +2.23 | 7.62 | 3.88 | 1.63 | 4.4 | 2.33 | 3.81 | 2.53 |
| [2,4) | 2,308 | 3.332 | 74.9 | +1.97 | 12.26 | 2.77 | 4.43 | 2.61 | 3.64 | 2.29 | 2.56 |
| [4,8) | 3,112 | 1.831 | 72.1 | +1.93 | 5.86 | 2.69 | 0.65 | 1.52 | 2.8 | 1.21 | 1.43 |
| [8,16) | 3,992 | 1.734 | 69.9 | +1.79 | 2.96 | 4.31 | 1.1 | 1.49 | 1.92 | 1.05 | 1.41 |
| [16,61) | 8,638 | 2.23 | 72.8 | +2.13 | 2.8 | 2.97 | 1.77 | 1.42 | 2.6 | 2.09 | 1.81 |

**⭐⭐ THE STACK: the three are NEAR-ORTHOGONAL** (corr lowdens↔pco 0.101,
lowdens↔gap −0.083, pco↔gap −0.225; gap↔dv_0945 −0.356 = only mildly the
liquidity axis; pco↔dsv 0.576):

| overlay / stack | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| BOOK (v2.0) | 26,541 | 2.465 | 74.0 | +2.15 | 4.08 | 3.82 | 1.45 | 1.72 | 2.59 | 1.98 | 2.48 |
| O1: lowdens >= 0.05 | 9,210 | 3.006 | 75.3 | +2.22 | 7.68 | 3.26 | 1.39 | 1.95 | 2.88 | 2.72 | 3.05 |
| O2: pco >= 2 | 7,604 | 3.411 | 77.2 | +2.26 | 6.68 | 5.04 | 1.96 | 2.03 | 2.94 | 2.76 | 5.96 |
| O3: gap_60 < 4 | 10,799 | 3.452 | 77.0 | +2.33 | 7.11 | 5.37 | 2.43 | 2.55 | 2.86 | 2.66 | 3.66 |
| O4: dsv >= -3% (known A+) | 1,802 | 3.326 | 78.1 | +2.3 | 5.24 | 3.9 | 2.42 | 2.58 | 1.66 | 4.08 | 14.37 |
| O1 x O2 | 2,884 | 3.399 | 76.6 | +2.17 | 8.43 | 3.62 | 1.22 | 2.46 | 2.66 | 2.85 | 6.68 |
| O1 x O3 | 4,336 | 4.114 | 77.9 | +2.41 | 13.85 | 3.57 | 1.46 | 2.92 | 3.24 | 4.41 | 4.13 |
| ⭐ O2 x O3 | 4,495 | 4.347 | 79.2 | +2.4 | 7.32 | 4.64 | 2.49 | 2.53 | 4.05 | 3.71 | 7.81 |
| O1 x O2 x O3 | 1,907 | 4.57 | 78.7 | +2.35 | 9.12 | 3.1 | 0.99 | 3.37 | 8.33 | 3.73 | 9.48 |
| O2 x O3 x O4 | 1,278 | 4.089 | 80.5 | +2.54 | 5.35 | 3.97 | 1.7 | 8.09 | 2.03 | 4.71 | 13.69 |

**⭐ O2 x O3 (green-from-open × continuous tape) = THE new A+ stack: 4,495
trips (17% of book) @ 4.347 / 79.2%, WORST YEAR 2.49 (2022!)** — a
worst-year profile no prior overlay matches at 2.5× dsv's trip count. The
triple adds PF but reintroduces a 2022 wart (0.99) and halves n — the pair
is the keeper. These are SIZING-pyramid members, not gates (each cuts
60-83% of the book). ⏭ next: mc=1 + A++-cell interaction pass for O2 x O3
before promoting it into the official pyramid.

## S40l — the Rényi/volume engine bake + pco REPLACES dsv (2026-08-01, evening)

**User's Rényi framing:** gap_60 is a VOLUME requirement in disguise (real-
world liquidity benefit backtests can't even see) — and it's the rank-0
Rényi measure of the window's volume distribution, with n_eff_shannon =
rank 1 and n_eff_hhi = rank 2. The study program: volume, dollar volume,
trade count, gaps, and Rényi ranks 0/1/2 on the 1m/5m/10m/20m windows.

**ENGINE (18 new record-only columns, present-bar convention like the rest;
bars + gaps = calendar span, so rank 0 lives in the gap counters):**
- `gap_300/600/1200` (rank-0 family completed; gap_60/30/15 existed)
- `vol_300`, `tc_300` (the 5m window joins the vol/tc family)
- `dollar_vol_300/600/1200` (the torrent axis recorded at every window)
- `n_eff_shannon_60/300`, `n_eff_hhi_60/300` (ranks 1-2 at 1m/5m; 600/1200 existed)
- Tier-2: `vol_leg`/`tc_leg`/`dv_leg` (Σ since the 20m leg's FIRST low, that
  bar inclusive; anchors snapshot at first-low, clear on leg reset),
  `cum_dv` (pre-leg = cum − leg, for vol/tc/dv alike), `targets_today`
  (target exits FILLED before the bar — the S38i day-scoped virgin clock,
  0 = virgin), `volat_20m_prev` (volat as of 1200 present bars ago — the
  S39q lagged normalizer). Leg-rate denominators for the participation
  ratios (user): session average = cum_*/(signal_sec − 34200) (reader feeds
  RTH-only bars, verified — no premarket in cum) and initial-15m average =
  vol/tc/dv_0945_tape (trips + mr_candidate_1s join). ⚠ leg rate is per
  PRESENT BAR, denominators per calendar second — gap counters can bridge.
`base_v4/` running (all 17 gates off); trip-set parity vs base_v3 to follow.

**pco vs dsv (user: could pco replace dsv?): YES — dsv RETIRES.** corr =
0.576, and the 2D shows dsv ≥ −3 is a NEAR-SUBSET of pco ≥ 2:

| overlay | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| pco >= 2 | 7,604 | 3.411 | 77.2 | +2.26 | 6.68 | 5.04 | 1.96 | 2.03 | 2.94 | 2.76 | 5.96 |
| dsv >= -3 | 1,802 | 3.326 | 78.1 | +2.3 | 5.24 | 3.9 | 2.42 | 2.58 | 1.66 | 4.08 | 14.37 |
| pco >= 2 AND dsv >= -3 | 1,679 | 3.321 | 78.1 | +2.3 | 5.04 | 3.53 | 2.27 | 3.1 | 1.62 | 3.99 | 13.89 |
| pco >= 2 AND dsv < -3 | 5,925 | 3.439 | 77.0 | +2.25 | 7.45 | 5.56 | 1.89 | 1.81 | 3.9 | 2.54 | 4.89 |
| pco < 2 AND dsv >= -3 | 123 | 3.409 | 78.0 | +2.29 | 138.39 | 20.9 | 3.37 | 1.09 | - | 16.42 | - |
| pco >= 2 OR dsv >= -3 | 7,727 | 3.411 | 77.2 | +2.26 | 6.74 | 5.13 | 2.0 | 1.98 | 2.95 | 2.78 | 6.02 |

93% of dsv's trips sit inside pco ≥ 2 (1,679/1,802); dsv's exclusive
marginal = 123 trips; and pco's exclusive region (dsv < −3) is just as good
as the overlap (3.439 vs 3.321) — dsv added NOTHING beyond day-structure
that pco doesn't capture with 4.2× the trips at the same PF. **The overlay
roster's day-structure seat: dsv ≥ −3 → pco ≥ +2 (recorded since v1.2 as
pct_chg_open; dsv stays a recorded column).** The 2D grid: within pco ≥ 10,
every dsv column runs 3.3-3.8 — dsv adds no gradient inside pco's deep
region either.

## S40m — base_v4 PARITY (the cold-eff un-hiding) + the {vol,tc,dv} LEG ANALYSIS (2026-08-01)

**base_v4 parity resolved:** first check showed +22,589 extra trips vs
base_v3 (strict superset). Root cause: the OLD signed-band eff gate had
`ValueNone -> false` BEFORE the off-checks — **cold eff failed even with the
band disabled**, so every prior base pass silently dropped cold/degenerate-
eff signal bars (Σ40|r| = 0 dead-tape windows + the one-slot warmup gap).
The S40i abs-band redesign made "off" truly off. The extras are EXACTLY the
NULL-eff bars (appender writes NaN as NULL — `isnan()` misses them, filter
on `eff_20m IS NULL`): **base_v4 minus NULL-eff ≡ base_v3, zero diff; v2.0
SQL over base_v4 = 43,587 = the engine reference exactly** (NULL fails the
abs band in SQL). **`base_v4/` = THE working base (2,217,950 trips).** ⚠
eff-OFF universes on v1-v3 bases were ~1% undercounted (the S39l/S40b
"eff OFF" baselines excluded cold-eff bars); spec-cut studies unaffected.

**The {vol,tc,dv} leg analysis** (user priority; v2.0 residual, base_v4,
26,541 trips). Leg rate = *_leg/(bars_since_first_low+1) per present bar
(the signal bar is a low, so legbars >= 1 always; anchors verified: 0
violations of leg <= cum on 2.2M rows; median leg VWAP/signal_vwap = 1.034).
Denominators: session avg = cum_*/(signal_sec−34200) (RTH-clean), pre-leg =
(cum−leg)/(elapsed−legbars), initial-15m = *_0945_tape/900 (bars-as-seconds
approximation on the leg, S38c precedent). Medians: rs_vol 0.79, rp_vol
0.73, r15_vol 0.18 — the typical leg runs QUIETER than the session and far
below the opening rotation.

| rs_vol = leg rate / session avg | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.4) | 1,894 | 1.914 | 71.8 | +2.0 | 4.37 | 23.5 | inf | 0.51 | 2.8 | 1.16 | 6.23 |
| [0.4,0.55) | 4,439 | 2.04 | 72.2 | +2.01 | 3.21 | 3.81 | 0.53 | 1.64 | 2.63 | 2.07 | 2.32 |
| [0.55,0.7) | 4,572 | 2.807 | 75.8 | +2.12 | 3.12 | 3.55 | 2.97 | 2.55 | 2.22 | 3.56 | 1.89 |
| [0.7,0.85) | 3,767 | 2.477 | 75.8 | +2.33 | 4.43 | 3.48 | 2.65 | 1.9 | 3.03 | 1.62 | 1.62 |
| [0.85,1.0) | 2,726 | 4.001 | 77.9 | +2.17 | 11.77 | 5.86 | 2.02 | 1.61 | 2.57 | 4.98 | 16.77 |
| [1.0,1.25) | 3,212 | 2.582 | 72.9 | +2.1 | 3.55 | 3.78 | 0.93 | 2.96 | 2.19 | 3.12 | 2.16 |
| [1.25,1.6) | 2,277 | 2.899 | 72.7 | +2.32 | 4.19 | 2.94 | 4.58 | 5.18 | 2.29 | 2.05 | 2.19 |
| [1.6,2.5) | 2,370 | 2.01 | 71.8 | +2.13 | 5.7 | 4.39 | 1.05 | 1.44 | 5.33 | 0.73 | 1.26 |
| >= 2.5 | 1,284 | 2.107 | 73.4 | +2.16 | 2.09 | 3.31 | 1.36 | 0.84 | 3.03 | 2.22 | 20.59 |

| rp_vol = leg rate / PRE-leg rate | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.35) | 2,240 | 1.755 | 70.5 | +1.88 | 3.42 | 3.66 | 4.41 | 1.07 | 3.19 | 1.19 | 2.13 |
| [0.35,0.5) | 4,838 | 2.133 | 72.4 | +2.04 | 4.85 | 4.09 | 0.47 | 1.34 | 2.02 | 2.34 | 3.65 |
| [0.5,0.65) | 4,347 | 2.679 | 76.4 | +2.14 | 2.48 | 3.66 | 3.15 | 2.53 | 2.95 | 2.2 | 2.72 |
| [0.65,0.8) | 3,151 | 2.559 | 75.3 | +2.27 | 4.31 | 3.49 | 2.78 | 1.87 | 2.36 | 2.19 | 1.54 |
| ⭐ [0.8,1.0) | 2,822 | 4.469 | 79.0 | +2.27 | 12.07 | 5.87 | 1.9 | 1.76 | 3.67 | 6.15 | 17.3 |
| [1.0,1.3) | 2,901 | 2.894 | 74.8 | +2.3 | 3.69 | 3.9 | 1.1 | 2.52 | 3.4 | 3.36 | 2.14 |
| [1.3,2.0) | 3,102 | 2.283 | 70.9 | +2.17 | 5.29 | 3.89 | 1.04 | 2.64 | 1.71 | 1.15 | 2.35 |
| [2.0,3.5) | 1,936 | 2.231 | 72.6 | +2.11 | 4.07 | 3.82 | 1.11 | 0.96 | 4.8 | 1.67 | 1.54 |
| >= 3.5 | 1,204 | 2.044 | 71.8 | +2.16 | 1.76 | 2.38 | 4.86 | 4.79 | 2.09 | 1.36 | 1.81 |

| rp_tc (trade-count twin) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.35) | 2,430 | 1.739 | 71.2 | +1.89 | 3.19 | 28.61 | 1.34 | 0.44 | 3.47 | 1.25 | 4.42 |
| [0.35,0.5) | 4,742 | 2.363 | 74.0 | +2.1 | 4.67 | 4.65 | 0.86 | 2.02 | 2.8 | 1.97 | 2.22 |
| [0.5,0.65) | 4,456 | 2.219 | 73.3 | +2.02 | 2.33 | 3.44 | 1.25 | 3.7 | 1.89 | 2.14 | 2.2 |
| [0.65,0.8) | 2,781 | 3.361 | 78.1 | +2.37 | 6.01 | 2.99 | 4.1 | 1.93 | 2.86 | 3.6 | 2.54 |
| [0.8,1.0) | 2,744 | 3.53 | 78.2 | +2.43 | 8.4 | 5.67 | 1.47 | 1.36 | 3.69 | 5.05 | 3.47 |
| [1.0,1.3) | 2,802 | 2.551 | 72.5 | +1.86 | 3.35 | 3.0 | 1.61 | 2.69 | 2.24 | 2.67 | 2.11 |
| [1.3,2.0) | 3,086 | 2.879 | 74.8 | +2.39 | 6.01 | 5.28 | 0.98 | 3.05 | 2.91 | 1.66 | 2.92 |
| [2.0,3.5) | 2,107 | 2.227 | 70.7 | +2.07 | 5.73 | 3.13 | 1.49 | 1.0 | 2.73 | 1.39 | 1.31 |
| >= 3.5 | 1,393 | 1.876 | 71.2 | +2.06 | 1.65 | 2.75 | 2.27 | 3.33 | 1.67 | 1.43 | 1.71 |

| rp_dv (dollar twin) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.35) | 3,456 | 2.062 | 73.2 | +2.03 | 5.17 | 5.46 | 9.38 | 1.12 | 3.28 | 1.53 | 2.09 |
| [0.35,0.5) | 5,092 | 2.141 | 72.2 | +2.03 | 2.33 | 3.16 | 0.67 | 1.64 | 2.42 | 2.31 | 3.52 |
| [0.5,0.65) | 4,051 | 2.487 | 75.0 | +2.11 | 4.22 | 5.09 | 2.64 | 1.8 | 1.76 | 2.27 | 1.95 |
| [0.65,0.8) | 2,954 | 3.37 | 77.9 | +2.4 | 6.85 | 3.13 | 2.28 | 2.77 | 4.42 | 2.12 | 5.78 |
| [0.8,1.0) | 2,672 | 2.857 | 75.6 | +2.1 | 3.26 | 4.92 | 1.06 | 1.29 | 3.81 | 4.54 | 5.36 |
| [1.0,1.3) | 2,625 | 3.384 | 74.6 | +2.29 | 7.27 | 4.79 | 2.29 | 5.2 | 2.18 | 3.41 | 1.67 |
| [1.3,2.0) | 2,918 | 2.398 | 73.2 | +2.33 | 6.6 | 3.45 | 1.14 | 1.91 | 1.99 | 1.46 | 2.32 |
| [2.0,3.5) | 1,732 | 2.296 | 73.0 | +2.15 | 5.63 | 3.71 | 1.12 | 1.19 | 8.18 | 1.01 | 1.19 |
| >= 3.5 | 1,041 | 1.785 | 69.3 | +2.02 | 1.26 | 2.16 | 4.1 | 4.27 | 1.92 | 1.35 | 1.66 |

| r15_vol = leg rate / initial-15m rate | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.05) | 9,347 | 2.842 | 75.3 | +2.27 | 5.3 | 6.83 | 1.92 | 1.99 | 2.03 | 1.99 | 6.33 |
| [0.05,0.1) | 1,885 | 1.605 | 73.7 | +2.07 | 2.08 | 2.39 | 1.82 | 0.85 | 3.18 | 0.83 | 3.34 |
| [0.1,0.18) | 2,028 | 2.458 | 72.5 | +1.99 | 3.19 | 5.84 | 2.65 | 1.36 | 2.52 | 2.38 | 2.38 |
| [0.18,0.3) | 2,235 | 2.487 | 74.0 | +2.25 | 4.25 | 2.39 | 3.28 | 2.13 | 3.61 | 1.85 | 2.06 |
| [0.3,0.5) | 3,394 | 2.064 | 70.6 | +1.89 | 3.7 | 3.35 | 0.63 | 1.4 | 2.6 | 2.8 | 1.54 |
| [0.5,0.8) | 3,785 | 2.418 | 74.3 | +2.22 | 2.75 | 1.98 | 1.92 | 3.54 | 2.64 | 2.05 | 2.71 |
| [0.8,1.35) | 2,552 | 3.107 | 75.5 | +2.0 | 4.14 | 2.13 | 0.66 | 2.89 | 6.87 | 4.91 | 3.5 |
| >= 1.35 | 1,315 | 2.244 | 72.9 | +2.1 | 16.59 | 4.63 | 1.81 | 0.46 | 2.11 | 1.33 | 1.84 |

| ps_leg = leg avg print / pre-leg avg print | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.75) | 1,153 | 2.037 | 71.8 | +1.95 | 6.42 | 2.85 | 2.64 | 2.1 | 0.97 | 1.32 | 3.05 |
| [0.75,0.85) | 2,445 | 2.686 | 73.8 | +2.01 | 5.82 | 2.72 | 1.98 | 2.34 | 2.36 | 2.65 | 2.55 |
| [0.85,0.95) | 5,708 | 2.529 | 73.4 | +2.22 | 3.7 | 4.99 | 0.93 | 2.59 | 2.58 | 2.61 | 1.95 |
| [0.95,1.05) | 8,193 | 2.79 | 75.3 | +2.25 | 3.64 | 4.01 | 1.5 | 2.11 | 3.19 | 2.8 | 2.36 |
| [1.05,1.15) | 5,438 | 2.105 | 72.5 | +2.05 | 3.64 | 4.18 | 1.5 | 0.91 | 2.17 | 1.58 | 2.99 |
| [1.15,1.3) | 2,796 | 2.696 | 76.6 | +2.21 | 5.11 | 3.64 | 1.71 | 1.74 | 3.7 | 1.52 | 3.57 |
| >= 1.3 | 808 | 1.619 | 69.9 | +1.84 | 7.36 | 1.5 | 1.99 | 0.84 | 4.82 | 0.54 | 4.47 |

**⭐ THE STEADY-TAPE FLUSH: rp_vol ∈ [0.8, 1.0) = 2,822 trips @ 4.469 /
79.0%, ALL 7 YEARS POSITIVE (worst 1.76)** — the leg trading at just-under
its own pre-leg pace. The same hump appears in all three lenses (rs_vol
[0.85,1.0) = 4.00; rp_tc [0.65,1.0) = 3.4-3.5) and at BOTH tails the edge
fades: a DRYING leg (< 0.5: interest gone, 1.7-2.1, 2022-toxic in spots)
and an ACCELERATING leg (> 1.3-2: real news/panic driving the tape, 1.8-
2.3). **Mechanism: a capitulation-grade price move on UNREMARKABLE volume
is mechanical/flow-driven selling — nothing happened — and snaps back;
volume acceleration means something actually happened.** The r15 axis is
noisy (the opening-rotation denominator mixes name types); ps_leg is mild
with a weak >= 1.3 tail (prints fattening into the leg = 1.62). Overlay-
roster candidate: rp_vol [0.8,1.0) joins as the participation lens; ⏭
interaction pass vs the S40k trio (near-1.0 steady tape vs gap_60
continuity — plausibly related) before pyramid promotion.

## S40n — the Rényi rank comparison at 1m: rank 0 DOMINATES, rank 1 has a gap-dependent SIGN, rank 2 is a duplicate (2026-08-01)

User's framing test (gap_60 = rank-0 Rényi of the 1m volume distribution =
"a volume requirement in disguise"; n_eff_shannon = rank 1, n_eff_hhi =
rank 2). Universe = v2.0 residual on base_v4 (26,541). ns60 q05/med/q95 =
10.1/22.7/40.0; nh60 = 4.7/13.8/29.9. Correlations: gap↔ns60 −0.554,
gap↔ln(tc_60) = **−0.732 (gap IS largely the trade-count/liquidity proxy —
the user's disguise claim confirmed)**, ns60↔nh60 = **0.969 (ranks 1-2 =
near-duplicates, matching S39f)**, ns60↔ln(tc_60) 0.589.

| n_eff_shannon_60 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,10) | 1,300 | 2.506 | 73.3 | +2.13 | 2.35 | 4.99 | 4.12 | 0.67 | 4.23 | 2.04 | 2.61 |
| [10,15) | 3,485 | 2.286 | 71.9 | +2.04 | 4.95 | 2.97 | 1.48 | 2.18 | 2.44 | 1.66 | 2.2 |
| [15,20) | 5,412 | 2.061 | 72.3 | +1.92 | 4.04 | 3.08 | 0.87 | 1.22 | 2.47 | 1.91 | 2.07 |
| [20,25) | 5,602 | 2.358 | 73.8 | +2.17 | 3.9 | 3.36 | 1.88 | 1.68 | 2.38 | 1.63 | 2.31 |
| [25,30) | 4,573 | 2.5 | 73.9 | +2.21 | 3.85 | 4.33 | 1.57 | 1.9 | 2.63 | 2.26 | 1.9 |
| [30,35) | 3,017 | 2.797 | 76.4 | +2.22 | 3.08 | 5.25 | 1.04 | 2.15 | 3.29 | 2.34 | 5.2 |
| [35,40) | 1,832 | 3.259 | 80.0 | +2.51 | 5.71 | 8.59 | 2.57 | 3.05 | 2.01 | 3.37 | 2.68 |
| [40,45) | 961 | 3.538 | 75.9 | +2.33 | 5.37 | 7.19 | 4.27 | 5.92 | 2.33 | 2.03 | 6.35 |
| [45,50) | 313 | 3.259 | 71.6 | +2.82 | 124.88 | 1.67 | inf | 0.59 | 17.51 | 1.39 | 8.8 |
| [50,61) | 46 | 19.96 | 80.4 | +5.42 | 91.44 | 7.32 | - | - | - | inf | inf |

| n_eff_hhi_60 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,7) | 3,683 | 2.424 | 72.7 | +2.05 | 3.87 | 3.62 | 2.14 | 1.19 | 3.4 | 1.7 | 2.66 |
| [7,10) | 3,967 | 2.228 | 72.8 | +2.04 | 6.12 | 2.95 | 1.07 | 1.7 | 2.14 | 1.93 | 2.22 |
| [10,14) | 5,881 | 2.203 | 73.0 | +2.04 | 4.13 | 3.51 | 0.97 | 1.18 | 2.49 | 2.21 | 1.64 |
| [14,18) | 4,965 | 2.567 | 74.6 | +2.24 | 3.23 | 4.09 | 3.05 | 2.15 | 2.6 | 1.73 | 2.76 |
| [18,23) | 3,945 | 2.595 | 74.7 | +2.19 | 4.87 | 4.86 | 1.1 | 1.82 | 2.85 | 2.13 | 2.85 |
| [23,28) | 2,272 | 2.519 | 76.9 | +2.3 | 2.75 | 5.07 | 2.27 | 2.51 | 2.48 | 2.05 | 2.37 |
| [28,34) | 1,227 | 3.386 | 75.5 | +2.37 | 3.72 | 7.75 | 2.11 | 5.09 | 2.01 | 3.03 | 6.33 |
| [34,42) | 536 | 3.726 | 77.1 | +2.79 | 14.17 | 2.29 | 6.41 | 4.79 | 4.02 | 2.06 | 6.35 |
| [42,61) | 65 | 6.061 | 63.1 | +4.99 | 97.39 | 3.78 | - | 0.0 | - | inf | inf |

2D — gap band × ns60 (n / PF), the key structure:

| gap \ ns60 | [0,15) | [15,25) | [25,35) | [35,45) | [45,61) |
|---|---|---|---|---|---|
| [0,1) | 94 / 3.94 | 907 / 3.28 | 2,849 / 3.92 | 2,410 / 3.1 | 359 / 3.96 |
| [1,4) | 273 / 3.52 | 1,502 / 2.91 | 2,086 / 3.28 | 319 / 12.18 | 0 |
| [4,16) | 1,182 / 2.18 | 4,025 / 1.87 | 1,840 / 1.43 | 57 / 1.46 | 0 |
| [16,61) | 3,236 / 2.3 | 4,580 / 2.18 | 815 / 2.2 | 7 / - | 0 |

**VERDICTS.** (a) **Rank 0 wins as THE overlay**: at matched trips, gap_60 <
4 (10,799 @ ~3.45) beats ns60 >= 25 (10,742 @ 2.85) decisively — the
"disguised volume requirement" is the stronger form, and its −0.73 corr
with ln(tc_60) says most of its content IS liquidity (which the user noted
also carries un-backtestable real-world benefits). (b) **Rank 1 has a
gap-DEPENDENT sign** — within continuous tape (gap < 4) high ns60 helps
(the [1,4)×[35,45) cell = 12.18 on 319); within GAPPY tape it INVERTS
([4,16) row falls 2.18 → 1.43 as ns60 rises — on a broken tape, evenly-
spread volume means NO burst at all, a dead drift-down, not capitulation).
(c) **Rank 2 adds nothing** (0.969 corr with rank 1 — Shannon stays the
family's representative, S39f confirmed at the 1m window). (d) ns60 >= 35 =
a rarer sharper lens (3,152 @ ~3.4, both bulk buckets positive every year)
— roster note, subordinate to gap. ⏭ the same rank family at 5m/10m/20m +
the dv-vs-N_eff torrent disentangling.

## S40o — ⭐⭐ THE TORRENT IS N_eff IN DISGUISE — and the N_eff form FIXES the bear years (2026-08-01)

User hypothesis: dv >= $30M/20m might be n_eff_shannon_1200 in disguise
(liquid = distributed volume; illiquid = sparse spiky volume). Universe =
v2.0 residual on base_v4 (26,541). corr(ln dv12, ns12) = 0.795 — tightly
coupled, as predicted. dv12 q05/med/q95 = $2.9M/$9.1M/$69M; ns12 =
232/406/701.

| dollar_vol_1200 ($M) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [1,2) | 307 | 1.773 | 71.3 | +2.98 | 8.84 | - | inf | 0.46 | 15.9 | 0.98 | 1.86 |
| [2,4) | 3,503 | 2.178 | 70.5 | +2.11 | 3.99 | 4.35 | 2.93 | 1.27 | 2.77 | 1.7 | 1.53 |
| [4,8) | 8,050 | 2.242 | 73.8 | +2.12 | 3.69 | 2.79 | 2.59 | 2.22 | 2.35 | 1.48 | 1.36 |
| [8,15) | 6,050 | 2.371 | 72.1 | +1.97 | 3.1 | 3.29 | 0.94 | 1.34 | 2.95 | 2.37 | 3.81 |
| [15,30) | 4,543 | 2.363 | 75.5 | +2.09 | 4.82 | 4.17 | 0.88 | 3.06 | 1.73 | 1.96 | 6.22 |
| [30,60) | 2,509 | 3.82 | 78.8 | +2.31 | 5.77 | 4.93 | 0.82 | 0.5 | 3.39 | 5.28 | 12.42 |
| [60,1000) | 1,579 | 4.286 | 78.8 | +2.87 | 13.55 | 7.26 | 3.81 | inf | 5.19 | 1.87 | 2.87 |

| n_eff_shannon_1200 | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,150) | 62 | 13.074 | 85.5 | +2.77 | 35.14 | inf | - | inf | inf | 10.59 | 5.74 |
| [150,250) | 1,967 | 2.617 | 73.6 | +2.26 | 6.22 | 6.48 | 3.21 | 1.39 | 10.63 | 1.29 | 3.69 |
| [250,350) | 6,568 | 2.051 | 70.7 | +1.9 | 4.03 | 3.83 | 0.98 | 1.99 | 2.49 | 1.75 | 1.15 |
| [350,450) | 7,575 | 2.276 | 73.3 | +2.12 | 3.04 | 2.89 | 2.57 | 1.41 | 2.06 | 2.15 | 2.67 |
| [450,550) | 5,066 | 2.326 | 74.8 | +2.08 | 3.64 | 3.86 | 0.68 | 1.85 | 2.79 | 2.22 | 3.97 |
| [550,650) | 3,056 | 2.919 | 77.8 | +2.33 | 6.83 | 3.69 | 5.97 | 1.92 | 1.97 | 1.97 | 7.11 |
| ⭐ [650,750) | 1,615 | 5.135 | 81.5 | +2.68 | 6.31 | 9.69 | 10.03 | 2.9 | 2.7 | 6.12 | 7.9 |
| [750,850) | 547 | 5.389 | 75.9 | +3.03 | 14.18 | 4.47 | 2.15 | inf | 46.7 | 1.48 | 0.86 |
| [850,1201) | 85 | 4.704 | 57.6 | +2.85 | inf | 1.31 | - | - | - | - | - |

2D (n / PF):

| dv12 $M \ ns12 | [0,250) | [250,450) | [450,650) | [650,850) | [850,1201) |
|---|---|---|---|---|---|
| [1,4) | 805 / 3.65 | 2,926 / 1.83 | 79 / 5.33 | 0 | 0 |
| [4,15) | 1,172 / 2.05 | 10,004 / 2.24 | 2,904 / 2.6 | 20 / 2.63 | 0 |
| [15,30) | 44 / 29.08 | 1,041 / 2.41 | 3,211 / 2.16 | 247 / 5.33 | 0 |
| [30,1000) | 8 / 1.77 | 172 / 5.58 | 1,928 / 3.09 | 1,895 / 5.2 | 85 / 4.7 |

The decomposition of the torrent corner:

| slice | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| torrent: dvm>=30M x dv12>=30M | 3,273 | 3.924 | 78.3 | 9.54 | 6.16 | 1.11 | 0.74 | 3.28 | 3.16 | 7.84 |
| dv12>=30M only | 4,088 | 4.01 | 78.8 | 7.82 | 5.94 | 1.25 | 0.75 | 4.04 | 3.14 | 6.83 |
| ⭐ dv12>=30M x ns12>=650 | 1,980 | 5.176 | 78.9 | 11.11 | 6.18 | 3.87 | 6.53 | 3.61 | 3.76 | 5.99 |
| dv12>=30M x ns12<650 | 2,108 | 3.22 | 78.7 | 5.35 | 5.76 | 0.72 | 0.35 | 5.16 | 2.84 | 8.8 |
| ns12>=650 x dv12<30M | 267 | 5.168 | 81.3 | 4.26 | 1.54 | 9.1 | 2.96 | 22.34 | inf | 16.87 |
| ns12>=850 (any dv) | 85 | 4.704 | 57.6 | inf | 1.31 | - | - | - | - | - |

**VERDICT: CONFIRMED AND UPGRADED.** (a) Splitting the dv-torrent by ns12
at 650: ALL of its bear-year damage lives in the low-N_eff half (2022 0.72
/ 2023 0.35 there vs **3.87 / 6.53** in the high-N_eff half) — the dollar
threshold was a noisy proxy for distribution. (b) The N_eff effect holds
WITHOUT the dollars (ns>=650 × dv<30M = 5.17 on 267). (c) **The new form
of the corner: `n_eff_shannon_1200 >= 650` ≈ 2,247 trips @ ~5.1 — beats
the dv-defined torrent (3.92) AND repairs its 2022-23 inversion.** A 20m
window where 650+ of 1,200 seconds carry effective volume = a genuinely
continuous institutional-grade panic — the thing the $30M floor was
groping for. Overlay roster: the torrent tier is now **ns12 >= 650 (the
DISTRIBUTED torrent)**; dv12 stays recorded. Footnote: the OPPOSITE
extreme (ns12 < 150 / the 44-trip 29.08 cell) is a concentrated-burst
anecdote — census first (S38j) before anyone chases it. ⏭ sizing-pyramid
re-cut with ns12 tiers + the S40k/S40m overlay interaction matrix.

## S40p — the rank family at 20m: TWO good corners (continuous AND concentrated), the sign-flip reproduces (2026-08-01)

Completing the 20m rank family (rank 1 = S40o). g12 q05/q25/med/q75/q95 =
0/48/268/594/937; nh12 q05/med/q95 = 82/203/454. corr(g12,ns12) = −0.666,
corr(g12,nh12) = −0.589, corr(ns12,nh12) = 0.965, **corr(g12,ln tc_1200) =
−0.802** (the liquidity-proxy story again).

| gap_1200 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ⭐ [0,5) | 2,873 | 5.63 | 82.2 | +2.87 | 11.96 | 7.14 | 4.27 | 5.62 | 4.82 | 3.9 | 5.97 |
| [5,15) | 1,326 | 4.28 | 73.5 | +2.22 | 2.43 | 3.56 | 233.4 | 11.33 | 2.35 | 3.97 | 13.71 |
| [15,40) | 1,897 | 1.964 | 74.5 | +1.82 | 7.49 | 4.67 | 2.35 | 0.92 | 1.83 | 1.41 | 2.16 |
| [40,90) | 2,556 | 2.884 | 76.4 | +2.25 | 10.71 | 3.25 | 2.19 | 2.17 | 1.73 | 5.26 | 1.61 |
| [90,180) | 2,608 | 2.783 | 75.6 | +2.19 | 4.16 | 3.66 | 0.88 | 4.23 | 2.48 | 2.35 | 3.38 |
| [180,360) | 4,160 | 1.766 | 70.5 | +1.82 | 3.35 | 3.03 | 0.81 | 1.26 | 2.33 | 1.44 | 1.64 |
| [360,700) | 6,333 | 2.03 | 72.3 | +1.97 | 3.06 | 3.7 | 1.7 | 1.44 | 2.4 | 1.2 | 2.55 |
| [700,1400) | 4,788 | 2.41 | 72.2 | +2.26 | 3.64 | 3.24 | 1.56 | 1.83 | 4.23 | 1.94 | 1.4 |

| n_eff_hhi_1200 bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ⭐ [0,80) | 1,187 | 4.747 | 78.9 | +2.52 | 8.68 | 7.06 | 8.3 | 11.27 | 4.32 | 3.15 | 5.78 |
| [80,140) | 5,275 | 2.137 | 70.8 | +1.95 | 5.12 | 3.52 | 1.53 | 2.37 | 4.09 | 1.43 | 1.3 |
| [140,200) | 6,536 | 1.9 | 72.2 | +2.01 | 3.27 | 3.35 | 0.97 | 1.16 | 2.13 | 1.61 | 2.02 |
| [200,270) | 5,935 | 2.45 | 73.7 | +2.1 | 3.01 | 4.64 | 1.47 | 1.78 | 2.24 | 1.91 | 6.6 |
| [270,350) | 3,691 | 2.737 | 76.8 | +2.23 | 4.09 | 3.55 | 1.51 | 2.25 | 2.72 | 2.22 | 3.78 |
| [350,450) | 2,523 | 3.255 | 77.4 | +2.33 | 12.55 | 3.33 | 5.46 | 1.61 | 1.83 | 3.56 | 5.45 |
| [450,560) | 1,053 | 5.549 | 80.5 | +2.79 | 3.54 | 7.39 | 6.36 | 3.28 | 4.14 | 14.05 | 18.19 |
| [560,700) | 340 | 5.125 | 71.8 | +3.3 | 137.73 | 2.19 | 0.0 | - | 34.74 | 1.44 | 0.31 |

2D — gap_1200 band × ns12 (n / PF):

| g12 \ ns12 | [0,250) | [250,450) | [450,650) | [650,1201) |
|---|---|---|---|---|
| [0,15) | 0 | 179 / 6.96 | 2,016 / 5.15 | 2,004 / 5.05 |
| [15,90) | 11 / - | 1,201 / 3.67 | 3,126 / 2.11 | 115 / 1.76 |
| [90,360) | 167 / 5.76 | 4,165 / 2.17 | 2,343 / 1.74 | 93 / 21.85 |
| [360,100000) | 1,851 / 2.56 | 8,598 / 1.99 | 637 / 4.24 | 35 / 24.8 |

**READING.** (a) **`gap_1200 < 5` = 2,873 @ 5.63 / 82.2%, worst year 3.90 —
the strongest large-n liquidity lens of the program**, beating even the
distributed torrent (ns12 >= 650 ≈ 5.1): twenty minutes of literally
uninterrupted tape. (b) **The 1m sign-flip REPRODUCES and sharpens**: on
continuous tape all ns12 levels are good (5.0-7.0); on gappy tape high
entropy is the WORST cell family (1.7-2.1) while LOW entropy recovers
(5.76 on 167; 2.56 on 1,851) — evenly-spread volume on a broken tape =
drift-down, concentrated volume = a real burst. (c) **Rank 2 finally earns
a distinct seat: nh12 < 80 = 1,187 @ 4.75, positive ALL years incl 2022
8.3 / 2023 11.27** — the CONCENTRATED torrent (a handful of seconds
carrying the 20m volume = single-burst capitulation), the S40o ns<150
anecdote at real scale because HHI's square-weighting sees concentration
that Shannon smooths over (their 0.965 corr hides exactly this tail). The
20m distribution axis is BIMODAL-GOOD: institutional continuous panic
(gap≈0) AND single-burst capitulation (nh12<80) both fade beautifully; the
gappy-evenly-spread middle is the swamp. ⏭ 5m/10m to complete the matrix.

## S40q — the rank family at 5m/10m: THE MATRIX COMPLETE (2026-08-01)

corr(g3,ns3) = −0.618, corr(g6,ns6) = −0.643; rank1↔rank2 = 0.963/0.961
(duplicates everywhere EXCEPT the concentration tail — see S40p).

| gap_300 (rank 0 @ 5m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,1) | 2,468 | 4.427 | 79.1 | +3.02 | 7.23 | 8.39 | 3.81 | 3.59 | 4.18 | 2.12 | 8.74 |
| [1,3) | 1,381 | 4.683 | 79.5 | +2.51 | 4.3 | 4.87 | 0.71 | 0.66 | 12.06 | 7.05 | 8.19 |
| [3,8) | 1,861 | 3.567 | 79.9 | +2.45 | 15.09 | 3.77 | 14.28 | 4.77 | 1.79 | 2.8 | 5.26 |
| [8,20) | 2,702 | 2.564 | 74.4 | +1.93 | 3.88 | 4.67 | 1.73 | 2.83 | 2.65 | 1.81 | 2.19 |
| [20,45) | 3,474 | 2.497 | 74.5 | +2.02 | 7.15 | 2.83 | 1.2 | 2.15 | 2.16 | 3.03 | 1.38 |
| [45,90) | 4,218 | 1.809 | 70.6 | +1.89 | 3.6 | 3.36 | 0.63 | 1.42 | 2.2 | 1.48 | 1.9 |
| [90,180) | 6,425 | 1.96 | 71.3 | +1.92 | 3.73 | 3.26 | 2.01 | 1.41 | 2.23 | 1.17 | 1.48 |
| >= 180 | 4,012 | 2.419 | 73.5 | +2.25 | 2.51 | 3.21 | 1.6 | 1.79 | 3.69 | 2.59 | 1.76 |

| n_eff_shannon_300 (rank 1 @ 5m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,40) | 328 | 6.454 | 75.0 | +2.8 | 3.77 | 12.09 | 1.54 | 44.77 | 14.78 | 10.98 | 2.76 |
| [40,70) | 4,041 | 2.267 | 71.3 | +1.97 | 4.66 | 3.75 | 1.94 | 1.97 | 3.2 | 1.42 | 2.24 |
| [70,100) | 8,752 | 2.097 | 72.7 | +2.03 | 5.25 | 3.3 | 1.16 | 1.3 | 2.19 | 1.73 | 1.74 |
| [100,130) | 7,304 | 2.292 | 73.7 | +2.08 | 3.02 | 3.19 | 1.85 | 1.64 | 2.49 | 2.14 | 1.55 |
| [130,160) | 3,762 | 3.335 | 78.0 | +2.38 | 3.5 | 6.37 | 1.05 | 3.36 | 2.53 | 3.39 | 13.26 |
| [160,190) | 1,856 | 3.578 | 78.3 | +2.47 | 6.0 | 4.6 | 4.42 | 2.23 | 2.92 | 2.24 | 12.99 |
| [190,220) | 483 | 4.612 | 76.6 | +3.26 | 11.86 | 4.23 | 4.71 | 0.69 | 15.03 | 1.93 | 0.45 |

| n_eff_hhi_300 (rank 2 @ 5m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,20) | 1,413 | 2.966 | 72.3 | +2.03 | 5.23 | 5.93 | 2.0 | 3.63 | 6.56 | 1.79 | 2.17 |
| [20,40) | 6,576 | 2.372 | 72.8 | +2.1 | 6.86 | 2.96 | 0.92 | 1.83 | 2.9 | 1.96 | 2.14 |
| [40,60) | 7,833 | 2.072 | 72.8 | +2.0 | 4.02 | 4.3 | 1.85 | 0.97 | 1.95 | 1.66 | 1.69 |
| [60,85) | 6,234 | 2.415 | 74.7 | +2.18 | 2.42 | 3.63 | 1.25 | 2.58 | 2.87 | 2.17 | 2.69 |
| [85,110) | 2,751 | 2.986 | 77.2 | +2.31 | 4.78 | 4.83 | 2.18 | 3.01 | 2.0 | 2.09 | 9.47 |
| [110,140) | 1,407 | 4.801 | 78.9 | +2.6 | 7.2 | 3.74 | 5.18 | 1.86 | 4.63 | 4.75 | 12.98 |
| [140,180) | 322 | 3.871 | 74.2 | +3.22 | 14.48 | 4.72 | 1.5 | - | 11.63 | 1.35 | 0.2 |

| gap_600 (rank 0 @ 10m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,2) | 2,316 | 5.12 | 81.4 | +3.12 | 10.93 | 7.96 | 3.51 | 4.46 | 4.16 | 2.92 | 7.75 |
| [2,6) | 1,512 | 5.463 | 80.1 | +2.56 | 3.91 | 3.84 | 11.77 | 103.24 | 16.63 | 4.67 | 4.98 |
| [6,15) | 1,568 | 3.383 | 73.2 | +1.91 | 6.12 | 5.12 | 58.1 | 0.67 | 3.02 | 3.0 | 6.64 |
| [15,40) | 2,844 | 2.106 | 76.6 | +2.15 | 8.13 | 4.81 | 1.11 | 3.06 | 1.89 | 1.56 | 1.23 |
| [40,90) | 3,416 | 2.412 | 72.9 | +1.91 | 6.41 | 2.83 | 1.06 | 1.51 | 1.92 | 2.7 | 3.38 |
| [90,180) | 4,161 | 1.922 | 71.5 | +1.92 | 2.97 | 3.01 | 0.8 | 1.92 | 1.89 | 2.02 | 1.59 |
| [180,350) | 6,015 | 2.014 | 72.1 | +1.96 | 4.03 | 3.78 | 1.6 | 1.37 | 2.52 | 1.09 | 2.47 |
| >= 350 | 4,709 | 2.399 | 72.7 | +2.24 | 2.61 | 3.1 | 1.79 | 1.75 | 4.03 | 2.47 | 1.49 |

| n_eff_shannon_600 (rank 1 @ 10m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,80) | 218 | 9.429 | 78.0 | +3.24 | 3.6 | inf | 0.82 | 10.67 | 94.9 | 23.38 | 3.96 |
| [80,130) | 2,683 | 2.195 | 71.3 | +1.97 | 5.18 | 4.18 | 5.92 | 1.72 | 3.5 | 1.27 | 2.27 |
| [130,180) | 7,189 | 2.152 | 71.4 | +1.98 | 6.31 | 2.84 | 1.19 | 1.79 | 2.11 | 2.04 | 1.3 |
| [180,230) | 7,396 | 2.018 | 73.4 | +2.06 | 2.42 | 4.41 | 1.52 | 1.19 | 2.4 | 1.69 | 2.45 |
| [230,280) | 4,760 | 2.751 | 75.7 | +2.19 | 3.93 | 3.79 | 0.79 | 3.97 | 2.82 | 2.45 | 4.33 |
| [280,330) | 2,630 | 4.119 | 80.0 | +2.43 | 11.02 | 5.99 | 11.95 | 1.73 | 2.42 | 3.3 | 25.7 |
| [330,400) | 1,405 | 4.073 | 78.4 | +2.77 | 5.24 | 4.45 | 86.92 | inf | 5.03 | 2.25 | 5.49 |
| [400,601) | 260 | 6.569 | 72.7 | +3.52 | inf | 2.96 | 0.26 | inf | inf | inf | 0.31 |

| n_eff_hhi_600 (rank 2 @ 10m) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,40) | 1,413 | 3.136 | 74.7 | +2.33 | 5.36 | 5.05 | 6.47 | 5.43 | 4.43 | 1.83 | 3.15 |
| [40,70) | 5,153 | 2.349 | 72.3 | +2.1 | 7.29 | 5.33 | 1.19 | 1.9 | 3.2 | 1.62 | 1.53 |
| [70,100) | 6,713 | 1.99 | 71.9 | +1.94 | 3.84 | 2.79 | 1.22 | 0.98 | 2.04 | 2.06 | 1.77 |
| [100,140) | 6,458 | 2.234 | 73.9 | +2.12 | 2.71 | 4.51 | 1.42 | 1.71 | 2.46 | 1.58 | 3.58 |
| [140,180) | 3,675 | 3.052 | 77.2 | +2.19 | 3.61 | 3.21 | 1.28 | 3.6 | 2.6 | 3.79 | 4.5 |
| [180,230) | 2,037 | 3.092 | 77.2 | +2.41 | 12.7 | 3.49 | 9.28 | 1.85 | 1.62 | 2.25 | 17.29 |
| ⭐ [230,300) | 955 | 6.024 | 78.1 | +3.12 | 5.89 | 4.28 | 4.95 | 8.07 | 14.19 | 4.24 | 7.41 |
| [300,601) | 137 | 10.558 | 80.3 | +2.91 | inf | 9.51 | 1.23 | - | inf | inf | 0.53 |

**⭐⭐ THE MATRIX SYNTHESIS (all four windows).** One grammar everywhere:
(1) **Rank-0-perfect (continuous tape) = the best large-n lens at EVERY
window, and it STRENGTHENS with window length**: 1m gap<4 ≈ 3.45 → 5m
gap<8 ≈ 4.1 → 10m gap<6 ≈ 5.2 → 20m gap<5 = 5.63/82.2%, worst-year 3.90.
SUSTAINED continuity is the real signal; short-window continuity is
common and regime-fragile (5m [1,3): 2022 0.71/2023 0.66), twenty
uninterrupted minutes is rare and robust. (2) **Distributed (high rank-1/2)
= good at every window**, converging to the same trips as gap≈0 at 20m.
(3) **Concentrated (low rank-1/2) = the second corner, only reaching real
scale at 20m** (nh12<80 = 1,187 @ 4.75 all-years; at 5m/10m it's
200-300-trip tails with 2022 warts). (4) **The swamp = moderately-gappy ×
evenly-spread, at every window** — the drift-down that merely looks like
capitulation. (5) rank1↔rank2 ≈ 0.96 everywhere; HHI's only distinct
value = the concentration tail (square-weighting). **The overlay-roster
liquidity seat: `gap_1200 < 5` (THE continuous-20m lens, 5.63) with
`nh12 < 80` (the concentrated torrent, 4.75) as its disjoint partner;
ns12 >= 650 (S40o) largely coincides with the first.** ⏭ overlay
interaction matrix + mc=1 + pyramid re-cut with the rank-family tiers.
