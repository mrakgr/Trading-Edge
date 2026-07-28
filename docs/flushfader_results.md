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

**⏭ THE PLANNED RERUN (deferred; engine v2):**
```
bin/Release/net10.0/TradingEdge.FlushFader \
  --min-prev-close 1 --min-volat-20m 0.004 \
  --vol-stop-ratio inf --tc-stop-ratio inf --speed-stop-pct 0 \
  -o data/equity/flushfader/v3_p1_volat40_nostops
```
Baked: prev-close ≥$1, volat floor 40bp; stops OFF (S9 verdict). Post-hoc: speed<−2%,
K∈[26,50], eff band, dist band. Then: (1) mc=1 + `--min-lows-into-leg 26` book run;
(2) distinct-leg count vs adds; (3) the TradeZero limit-fill question (production).

⏭ **Future runtime optimization (user, 2026-07-28): per-tkd volatility prefilter.**
Precompute `(ticker, date, max intraday volat_20m)` once, then skip ticker-days whose max
never clears the run's `--min-volat-20m` floor. ⚠ Lookahead discipline: the day-max is
day-D data, so this is legal ONLY as a speed shortcut welded to an equal-or-higher
signal-time volat floor (day-max ≥ signal-bar volat ⇒ provably drops zero qualifying
signals, bit-identical output). As a STANDALONE day filter it would be a "day got
volatile later" lookahead — the exact 2026-07-16 bug class. V6 precedent: the ATR floor
was load-bearing (sub-0.004 dead-below-costs) — most of the 328k tkds are dead weight.
