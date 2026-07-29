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
