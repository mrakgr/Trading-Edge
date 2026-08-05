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

## S40r — is gap a proxy for tc? NO — tc is the material, ARRANGEMENT is the signal (2026-08-01)

corr(g12, ln tc_1200) = −0.802 raised the proxy question. tc_1200
q05/q25/med/q75/q95 = 4,003/6,266/10,110/20,815/57,536. The coupling is
partly MECHANICAL (gap >= 1200 − tc: few trades force empty seconds; the
2D's empty corners show it), so the test is conditioning.

2D — tc_1200 band × gap_1200 band (n / PF):

| tc_1200 \ g12 | [0,5) | [5,15) | [15,90) | [90,360) | >= 360 |
|---|---|---|---|---|---|
| [1500,3000) | 0 | 0 | 0 | 0 | 100 / 1.7 |
| [3000,6000) | 0 | 0 | 0 | 110 / 26.87 | 5,758 / 2.34 |
| [6000,12000) | 0 | 0 | 143 / 1.81 | 3,985 / 1.77 | 4,942 / 2.01 |
| [12000,30000) | 269 / 5.93 | 675 / 3.43 | 3,902 / 2.81 | 2,395 / 2.7 | 213 / 1.03 |
| >= 30000 | 2,604 / 5.6 | 651 / 5.59 | 408 / 1.01 | 278 / 2.11 | 108 / 31.12 |

Matched-trip head-to-head + decomposition (matched tc threshold = 37,196):

| lens | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap_1200 < 5 (THE lens) | 2,873 | 5.63 | 82.2 | 11.96 | 7.14 | 4.27 | 5.62 | 4.82 | 3.9 | 5.97 |
| tc_1200 >= 37,196 (matched n) | 2,873 | 4.24 | 80.5 | 9.17 | 5.62 | 5.11 | 24.24 | 4.22 | 2.59 | 5.85 |
| gap<5 AND tc>=matched | 2,181 | 5.676 | 81.0 | 10.86 | 5.73 | 5.79 | 14.01 | 3.88 | 5.9 | 4.57 |
| gap<5 AND tc<matched | 692 | 5.476 | 86.0 | 17.25 | 17.41 | 0.13 | 2.14 | 57.39 | 1.25 | 19.04 |
| gap>=5 AND tc>=matched | 692 | 2.415 | 79.0 | 5.44 | 5.31 | 3.97 | 407.64 | 5.49 | 1.03 | 84.52 |

**VERDICT.** (a) Given tc, gap still separates HARD (within tc [12k,30k):
5.93 → 1.03 across gap bands; within tc >= 30k: 5.6 → 1.01 at [15,90)).
(b) Given gap < 5, tc adds NOTHING (5.68 vs 5.48 across the tc split).
(c) At matched trips the gap lens beats the tc lens (5.63/82.2 vs
4.24/80.5), and tc-without-continuity collapses to 2.42 — huge bursty
activity (news prints, halt-resume tape) is NOT the fadeable auction.
**Trade count is the raw material; UNINTERRUPTED ARRANGEMENT is the
signal. gap_1200 < 5 = "no tradeless second for twenty minutes" = a
continuous two-sided auction absorbing the flush — the −0.80 corr was
mechanical coupling, not signal redundancy.** Caveat: the gap<5 × modest-tc
sub-cell (692 @ 5.48/86%) is year-lumpy (2022 0.13, 2025 1.25 vs 17/17/57
tails) — the whole gap<5 lens stays THE form; tc adds robustness context,
not a seat.

## S40s — inside nh12 < 80: gap separates AGAIN, but INVERTED — the pure single-burst corner (2026-08-01)

User question: does gap add anything within the concentrated bucket?
The slice (1,187 trips / 291 tkds) is overwhelmingly GAPPY by construction
— g12 q25/med/q75 = 442/636/822; only 24 trips have gap < 90 (8 tkds, all
winners, anecdote) — concentrated volume ≈ a few huge seconds plus dead air.

| gap band within nh12<80 | n | tkds | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,90) combined | 24 | 8 | inf | 100.0 | +3.91 | - | inf | - | - | - | inf | inf |
| [90,180) | 66 | 12 | 19.877 | 84.8 | +1.3 | inf | inf | - | - | inf | 2.36 | inf |
| [180,360) | 81 | 22 | 2.556 | 70.4 | +1.92 | 3.58 | 1.11 | inf | inf | - | 1.88 | 1.58 |
| [360,700) | 520 | 138 | 2.608 | 75.2 | +2.27 | 12.65 | 23.76 | 1.62 | 17.25 | 0.54 | 1.45 | 4.1 |
| ⭐ [700,1200) | 496 | 123 | 11.878 | 82.5 | +2.94 | 6.39 | 2.6 | 19.92 | 8.85 | 72.56 | 12.17 | 16.29 |

**YES — and the sign FLIPS: within the concentrated bucket, MORE gap is
better.** The corner sharpens to **nh12 < 80 × gap_1200 >= 700 = 496 trips
/ 123 tkds (~19/yr, above the S38j census bar) @ 11.878 / 82.5%, ALL 7
years positive** (worst 2.6) — nearly the whole 20m window tradeless AND
the volume that did print crammed into a handful of seconds: the PURE
single-burst capitulation on an otherwise dead tape (violent flush-print
cluster / reopen-style bursts). Its complement ([360,700), the half-dead
tape) is the bucket's weak half (2.61, 2024 0.54). **The two 20m corners
are now maximally separated at OPPOSITE ends of rank 0: continuous-
everything (gap < 5, 5.63) and dead-tape-single-burst (gap >= 700 ×
nh12 < 80, 11.9)** — the bimodality is total, and the middle remains the
swamp. Overlay roster: the concentrated-torrent seat refines from
nh12 < 80 (4.75) to **nh12 < 80 × g12 >= 700 (11.9)**; census-real but
tail-shaped (mc=1 + sizing discipline before any pyramid promotion).

**S40s addendum — dollar-volume distributions of the two gap bands (user
liquidity concern):**

| quantile | band [360,700) | band [700,1200) | BOOK reference |
|---|---|---|---|
| dv_20m med (q05-q95) | $5.4M ($2.1-11.7M) | $4.5M ($1.6-14.0M) | $5.3M ($2.0-18.6M) |
| dv_5m med | $1.70M | $1.30M | $1.62M |
| dv_1m med (q05) | $443k ($139k) | $365k ($138k) | $451k ($141k) |
| dv_0945 med | $6.3M | $5.3M | $6.8M |
| entry px med | $2.6 | $3.0 | $2.8 |
| tc_60 med | 495 | 418 | 508 |

**The 11.9 corner is NOT the illiquid tail** — its dollar distributions sit
at ~80-85% of the book's medians and are IDENTICAL at q05 (the engine's own
floors — DvFloor60 $100k, TcFloor60 60, dv_0945 >= $3M, dvw — already force
real liquidity). These names did $3-14M in the 20m window; the gaps come
from the tape STOPPING BETWEEN BURSTS, not from the name being untradable.
A $10k clip ≈ 3% of the median signal-minute flow. **The real practical
caveat is INTERMITTENCY, not depth**: with 700+ dead seconds, the
next-present-bar fill assumption papers over wait-time and spread between
bursts — exactly what the queued TradeZero-fills/slippage work must stress,
with this corner as the primary test case.

**And the gap_1200 < 5 lens for contrast (user sanity check) — an order of
magnitude MORE liquid than the book:**

| quantile | gap < 5 lens | BOOK | ratio |
|---|---|---|---|
| dv_20m med (q05) | $55.6M ($19.2M) | $5.3M ($2.0M) | ~10× |
| dv_5m med | $11.3M | $1.6M | ~7× |
| dv_1m med (q05) | $3.0M ($747k) | $451k ($141k) | ~6.6× |
| dv_0945 med | $69.1M | $6.8M | ~10× |
| tc_60 med | 2,816 (~47/sec) | 508 | ~5.5× |

The continuous corner IS the honest-torrent population rediscovered from
the microstructure side (median morning dv $69M >> the old $30M threshold).
The bimodal pair is now fully characterized: **gap < 5 = the SIZE side**
(5.63 PF, worst-year 3.90, $3M/minute tape where six-figure clips are ~3%
of flow, maximal fill realism) **vs the burst corner = the EDGE side**
(11.9 PF on book-typical depth but intermittent tape, slippage-audit
required). Liquidity scaling WITH edge on the continuous side — the S38
sizing-pyramid principle holding at the microstructure level.

## S40t — the CLIMAX MAP: within-gap nh12 deciles (user's normalization method) (2026-08-01)

**Method (user): for every exact gap value, rank nh12 into deciles WITHIN
that gap group (NTILE(10) PARTITION BY g12), then aggregate deciles across
gap values** — removes the gap→N_eff mechanical dependence entirely; the
residual axis is pure arrangement-given-support. D1 = most concentrated,
D10 = most even.

Fine ABSOLUTE nh12 within gap <= 15 first (user request; 4,334 trips, nh12
med 389): everything 4.2-8.3 — elite regardless of nh12; only the most-even
extreme deteriorates ([580,640) = 4.87 but 2025/26 = 0.84/0.31; [640,1201)
= 3.19/55% on 40).

| nh12 within gap<=15 | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,250) | 678 | 4.183 | 73.7 | +2.43 | 3.24 | 37.74 | inf | - | 3.62 | 1.91 | 8.8 |
| [250,320) | 744 | 4.322 | 80.4 | +2.55 | 3.29 | 203.89 | 4.33 | inf | 1.89 | 4.33 | 5.31 |
| [320,380) | 626 | 6.086 | 82.7 | +2.68 | 7.83 | 4.11 | 6.68 | 6.95 | 3.84 | 7.25 | 12.98 |
| [380,430) | 719 | 5.138 | 79.0 | +2.71 | 17.41 | 4.41 | inf | 22.57 | 6.35 | 3.08 | 6.34 |
| [430,480) | 675 | 4.828 | 81.9 | +2.43 | 4.08 | 3.63 | inf | 5.54 | 2.53 | 22.23 | 7.16 |
| [480,530) | 413 | 6.424 | 80.4 | +3.01 | 2.96 | 12.68 | inf | inf | 2.78 | 169.52 | inf |
| [530,580) | 214 | 8.339 | 82.7 | +2.29 | 15.21 | 23.12 | 0.87 | inf | inf | 2.41 | - |
| [580,640) | 225 | 4.866 | 71.1 | +4.13 | 247.18 | 1.81 | - | - | 22.69 | 0.84 | 0.31 |
| [640,1201) | 40 | 3.185 | 55.0 | +0.75 | inf | 2.74 | - | - | - | - | - |

The decile grids (PF / win% / median ret% per band):

| PF | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 | D10 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap [0,15) | 4.13 | 2.9 | 11.39 | 18.37 | 4.73 | 5.53 | 3.64 | 7.05 | 3.45 | 6.02 |
| gap [15,90) | 6.23 | 3.78 | 4.27 | 3.67 | 1.26 | 1.95 | 1.31 | 1.78 | 2.79 | 2.17 |
| gap [90,360) | 2.15 | 1.85 | 1.88 | 1.97 | 2.2 | 1.92 | 1.88 | 2.04 | 2.54 | 2.73 |
| gap [360,700) | 2.04 | 1.81 | 2.31 | 2.0 | 2.13 | 1.97 | 1.68 | 1.92 | 1.95 | 2.79 |
| gap [700,1200) | 4.87 | 2.38 | 2.27 | 2.21 | 1.66 | 2.34 | 2.22 | 1.44 | 2.56 | 3.77 |

| win% | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 | D10 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap [0,15) | 77.8 | 71.8 | 85.4 | 89.4 | 80.0 | 80.4 | 78.5 | 83.1 | 72.9 | 75.4 |
| gap [15,90) | 84.6 | 80.4 | 76.8 | 81.0 | 73.4 | 72.0 | 64.7 | 70.1 | 76.7 | 74.7 |
| gap [90,360) | 72.1 | 71.1 | 71.4 | 71.5 | 71.2 | 71.7 | 73.8 | 73.1 | 76.6 | 73.3 |
| gap [360,700) | 70.4 | 69.8 | 74.5 | 72.2 | 70.0 | 73.0 | 70.1 | 72.1 | 77.0 | 76.7 |
| gap [700,1200) | 76.8 | 68.8 | 71.0 | 71.6 | 70.8 | 72.8 | 72.6 | 67.7 | 73.4 | 77.4 |

| med ret% | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 | D10 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap [0,15) | 3.01 | 1.91 | 2.81 | 3.64 | 2.45 | 2.21 | 3.13 | 2.83 | 2.17 | 2.33 |
| gap [15,90) | 2.64 | 2.7 | 2.01 | 2.02 | 1.79 | 1.86 | 1.44 | 1.61 | 2.04 | 2.14 |
| gap [90,360) | 2.05 | 1.82 | 1.82 | 1.85 | 1.82 | 2.02 | 2.11 | 2.01 | 2.17 | 2.13 |
| gap [360,700) | 1.89 | 1.8 | 1.9 | 1.86 | 1.78 | 2.11 | 1.85 | 2.28 | 2.37 | 2.26 |
| gap [700,1200) | 2.55 | 1.98 | 2.16 | 2.09 | 1.98 | 2.38 | 2.37 | 2.23 | 2.35 | 2.64 |

**READING — the climax map.** (a) **On continuous tape ([0,15)) arrangement
is IRRELEVANT** — deciles bounce 2.9-18.4 with no gradient (win% mid-hump);
a fully-alive tape is already a real auction, everything fades. (b) **The
concentration preference lives at [15,90)**: D1-D4 = 3.7-6.2 (win 81-85 at
the top) vs D5-D10 = 1.3-2.8 (win% monotone-ish, median too) — the
almost-continuous tape is where climax-vs-metronome separates hardest.
(c) The mid-gap swamp ([90,700)) is flat ~2 everywhere with a faint EVEN
uptick at D9-10 — neither lens rescues it. (d) The dead-tape band is
U-shaped: D1 = 4.87 (the S40s burst corner via the normalized method) and
a D10 echo (3.77, tail-shaped). **So "gap × rank 2 detects climaxes" holds
with precision: the conjunction matters at the two ends of the gap axis
([15,90) concentrated-side and [700,1200) D1), is unnecessary on perfect
tape, and rescues nothing in the swamp.** Method note: the per-group decile
normalization is a house tool from here on for any nested feature pair.

## S40u — the AWAKENING (dead 20m, alive 5m): a real story at ANECDOTE scale (2026-08-01)

User hypothesis: g12 high × g3/g6 low = activity picking up in an otherwise
inactive stock. 2D (n / PF):

| g12 \ g3 | [0,3) | [3,15) | [15,45) | [45,120) | [120,301) |
|---|---|---|---|---|---|
| [0,15) | 3,199 / 5.55 | 1,000 / 4.04 | 0 | 0 | 0 |
| [15,90) | 368 / 1.52 | 2,019 / 2.32 | 2,005 / 2.75 | 61 / 3.8 | 0 |
| [90,360) | 218 / 1.94 | 526 / 3.34 | 2,305 / 2.65 | 3,568 / 1.72 | 151 / 1.69 |
| [360,700) | 64 / inf | 30 / 7.42 | 152 / 2.24 | 2,813 / 2.03 | 3,274 / 1.89 |
| [700,1200) | 0 | 0 | 0 | 58 / 1.38 | 4,730 / 2.43 |

The awakening cells look SPECTACULAR — g12>=360 × g3<3 = 64 trips with
ZERO LOSSES (median +7.24%); × g3<15 = 94 @ 51.99/90.4% — **but the census
kills the feature: 94 trips = 14 tkds EVER (~2/yr)**, the classic
meme-squeeze roll (AYRO, FTFT, PHUN, GNPX, QSI, TARA, LIDR 21-trip
campaign, AEHL...), one losing event among them (VRAX 2026-07-09). S38j
discipline: count events before profiling features. **PLAYBOOK NOTE, not a
feature: a dead-for-20m name whose last 5m is suddenly gapless and
flushing = a violent-awakening campaign day (up to 21 trips/event, +30 to
+135 pts summed) — recognize it live, size it as a lottery, expect ~2/yr.**
The census-real neighbor (g12 [90,360) × g3<15 = 744 trips / 121 tkds @
2.63, all years positive) is only modestly above book — the marginal gap
rows dominate the 2D; recency structure adds little at scale. The
mechanical constraint also caps the pocket: g12 >= 700 × alive-5m is EMPTY
(not enough present seconds left to keep the last 5m gapless).

## S40v — HALT vs SPARSITY: gap_60's meaning FLIPS with halt context; the burst corner is NOT halts (2026-08-01)

**base_v5 = THE base** (2,217,950, zero-diff parity vs base_v4; new cols
max_gap_run_1200/300 = longest contiguous tradeless run attached to the
window's present bars, big_gap_runs_1200 = count of >= 60s runs; ⚠ 0.7% of
rows have max_run > gap total — a run ENDING in the window can START before
its calendar span; context feature, by design). Halt-context census on the
v2.0 residual: **CLEAN (max run < 60s) = 93.8%** @ 2.43; MIXED (60-250s) =
2.6% @ 5.15; HALTY (>= 250s, LULD-scale) = 3.6% @ 2.32.

gap_60 × halt context (n / PF):

| gap_60 \ context | CLEAN | MIXED | HALTY |
|---|---|---|---|
| [0,1) | 6,164 / 3.56 | 0 | 455 / 2.96 |
| [1,2) | 1,796 / 3.45 | 0 | 76 / 2.89 |
| [2,4) | 2,233 / 3.41 | 0 | 75 / 2.09 |
| [4,8) | 3,054 / 1.8 | 0 | 58 / 4.84 |
| [8,16) | 3,891 / 1.7 | 3 / 0.0 | 98 / 7.34 |
| [16,61) | 7,754 / 2.15 | 681 / 5.19 | 203 / 1.26 |

| slice | n | tkds | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| g1<4 x CLEAN | 10,193 | 1569 | 3.512 | 77.2 | +2.3 | 7.09 | 5.26 | 2.54 | 2.54 | 2.68 | 2.95 | 3.48 |
| g1<4 x HALTY | 606 | 93 | 2.849 | 73.3 | +3.16 | 7.43 | 7.76 | 0.16 | 2.62 | 6.41 | 1.44 | 19.23 |
| g1 [4,16) x CLEAN | 6,945 | 1528 | 1.744 | 70.8 | +1.84 | 3.92 | 3.46 | 0.79 | 1.47 | 2.16 | 1.12 | 1.38 |
| g1 [4,16) x HALTY | 156 | 28 | 5.992 | 77.6 | +2.32 | 1.97 | 3.04 | 19.57 | 13.29 | 25.41 | 1.52 | 10.99 |

**(a) The user's confound was REAL and productive: gap_60's meaning FLIPS
with halt context.** On clean tape the [4,16) trough is the true swamp
(1.74); in halt context the same bucket runs 5.99 (2022-24 = 19.6/13.3/
25.4 — post-reopen churn IS fadeable; 28 tkds ≈ 4/yr, borderline census).
Conversely a gapless last minute in halt context = the post-reopen
immediate knife (2.85 with 2022 = 0.16). The S40k gap_60 trough was a
MIXTURE all along. (b) **MIXED context (a single 60-250s hole) = 684 @
5.15** — halt-adjacent tape, overlay-note. (c) The production lens
sharpens marginally: g1 < 4 × CLEAN = 10,193 @ 3.51.

The S40s burst corner decomposed:

| slice | n | tkds | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| corner (nh12<80 x g12>=700) | 496 | 123 | 11.878 | 82.5 | 6.39 | 2.6 | 19.92 | 8.85 | 72.56 | 12.17 | 16.29 |
| corner x HALTY (mr>=250) | 0 | 0 | - | - | - | - | - | - | - | - | - |
| corner x mr [60,250) | 58 | 17 | 52.071 | 89.7 | 0.0 | inf | - | - | 58.38 | tail | inf |
| corner x SPARSE (mr<60) | 438 | 107 | 10.058 | 81.5 | 10.03 | 2.35 | 19.92 | 8.85 | 74.63 | 7.21 | 14.6 |

**(d) The "reopen-style bursts" narrative (S40s) was WRONG — the corner
contains ZERO LULD-scale runs.** 438/496 trips have max run < 60s: ~700
missing seconds accumulated as DOZENS of sub-minute holes — genuinely
sparse, intermittent tape with episodic volume avalanches, PF 10.06 on
107 tkds, all years. The corner survives the decomposition as the sparse
animal; the halt story dies. The intermittency caveat for the slippage
audit stands and is now precisely characterized: many short waits, not
halt reopens.

## S40w — TRUE HALTS vs DEAD TAPE (user definition: big run × high tc × high volat) (2026-08-01)

**User: max-run alone can't separate halts from illiquidity — a genuine
halt = high gaps AND high tc AND high volatility.** Confirmed by the
distributions: within mr >= 250 (965 trips), tc_1200 median = 27,507 (book:
10,110) and volat median = 140bp (book: 86) — the big-run slice is
MAJORITY-busy, i.e. mostly real halts, with a thin-tape minority mixed in.

| slice | n | tkds | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| TRUE HALT: mr>=250 x tc>=15k x volat>=150bp | 279 | 51 | 5.149 | 80.6 | +4.47 | 2.54 | 1.99 | 7.03 | 30.11 | 95.3 | 1.9 | 60.89 |
| mr>=250 x tc>=15k (busy, any volat) | 606 | 86 | 2.991 | 75.2 | +3.54 | 3.11 | 7.65 | 1.07 | 1.85 | 78.66 | 1.37 | 24.66 |
| ⚠ DEAD TAPE: mr>=250 x tc<15k x volat<150bp | 253 | 40 | 1.166 | 67.6 | +2.08 | 5.41 | inf | 0.48 | 26.93 | 2.57 | 0.93 | 0.67 |
| mr>=250 x tc<15k (thin, any volat) | 359 | 58 | 1.575 | 69.9 | +2.21 | 6.41 | 5.07 | 0.55 | 1.4 | 3.33 | 1.17 | 1.04 |

S40v cells re-split by the definition:

| slice | n | tkds | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| g1 [4,16) x mr>=250 x busy | 43 | 8 | 3.531 | 74.4 | 0.25 | - | - | inf | inf | 0.0 | inf |
| g1 [4,16) x mr>=250 x thin | 113 | 20 | 10.962 | 78.8 | inf | 3.04 | 19.57 | 6.02 | 8.23 | 8.69 | 0.84 |
| g1 < 4 x mr>=250 x busy | 524 | 76 | 3.105 | 75.2 | 6.4 | 7.65 | 0.16 | 1.35 | 62.38 | 1.53 | 18.27 |
| g1 < 4 x mr>=250 x thin | 82 | 19 | 1.635 | 61.0 | inf | inf | - | inf | 0.81 | 0.69 | inf |

**READING.** (a) The user's conjunction SEPARATES: true halts fade at 5.15
/ 80.6 / median +4.47% (51 tkds ≈ 8/yr) while **dead tape (big hole × thin
× quiet) = 1.166 — the worst liquidity slice of the whole program, a
genuine AVOID rule**. (b) The 2022-0.16 knife localizes to gapless-minute
× BUSY-halt tape (the true post-reopen knife). (c) The S40v [4,16)-halty
cell's 5.99 was carried by its THIN side (20 tkds) — the "post-reopen
churn" reading from S40v is retracted; at 8-20 tkds per sub-cell the whole
mr >= 250 interior is anecdote-scale. **Production output: (i) AVOID
mr>=250 × tc<15k × volat<150bp; (ii) the halt-fade (5.15) = playbook
pattern at ~8 events/yr; (iii) the CLEAN 94% of the book is untouched by
all of this.** A proper halt FLAG (exchange feed / LULD reconstruction)
would settle the residual ambiguity — deferred to the production data
work.

**S40w addendum — definitions + the total-gap version (user questions):**
tc >= 15k is the 20m present-bar trade count (~12.5 trades/sec; ~65th
percentile of the residual book, med 10,110 — moderately busy, below the
mr>=250 slice's own median of 27.5k). "run >= 250s" = max_gap_run_1200,
the longest CONTIGUOUS hole. **Using total gap_1200 instead DESTROYS the
result** — the two variables select nearly disjoint populations: mr>=250 =
965 trips, g12>=360 = 11,121, overlap only 424; **541 of the 965 big-run
trips have g12 < 360** (one 300s halt on a liquid tape leaves a modest
TOTAL; and a run may start before the calendar window). The total-gap
"busy" cell (g12>=360 × tc>=15k × volat>=150) is nearly EMPTY (56 trips —
high total gap and high tc are almost contradictory outside a contiguous
hole), and the total-gap "dead" cell = 10,608 @ 2.17 = just the ordinary
gappy book, no separation. **Total gap measures SPARSITY; max run measures
INTERRUPTION. Halts are interruptions, not sparsity — only the run
decomposition can see them.**

## S40x — the halt detector VERIFIED: adjusted gaps ≈ no-op (sparsity IS the poison), but the HALT CLOCK earns seats (2026-08-01, evening)

**Engine (0bc2525):** the user-designed causal detector — run >= 58s ×
pre-hole 5m range >= 4% × pre-hole ADJUSTED gap_60 < 2 (recursion chains
back-to-back halts) — with `gap_adj_{60,300,600,1200}` (= raw − halt-
interval overlap), `halts_today`, `secs_since_halt`; thresholds are config
flags. **base_v6 = THE base** (2,217,950, zero-diff vs base_v5; adj <= raw
on every row). Detector census: 10.2% of base trips sit on detected-halt
days (~350-500 halt tkds/yr — a plausible LULD rate); max 36 halts/day;
for most halted-day trips the halt is OUTSIDE the 20m window. On the v2.0
residual, 24.2% of trips are on halt days (flush days halt often — as
expected).

20m gap table, RAW vs ADJUSTED (n / PF):

| bucket | raw gap_1200 | adj gap_adj_1200 |
|---|---|---|
| [0,5) | 2,873 / 5.63 | 3,148 / 5.09 |
| [5,15) | 1,326 / 4.28 | 1,419 / 4.49 |
| [15,40) | 1,897 / 1.96 | 2,055 / 1.98 |
| [40,90) | 2,556 / 2.88 | 2,587 / 2.89 |
| [90,180) | 2,608 / 2.78 | 2,580 / 2.63 |
| [180,360) | 4,160 / 1.77 | 3,800 / 1.73 |
| [360,700) | 6,333 / 2.03 | 6,164 / 1.97 |
| [700,1400) | 4,788 / 2.41 | 4,788 / 2.41 |

(1m table moves even less — halts almost never sit inside the signal's
last minute, because the signal IS trading.)

**⭐ The NEGATIVE result: the swamp does NOT clean up.** The S40x
prediction (mid-gap buckets contaminated by halts) is REFUTED — bucket-
by-bucket the adjusted axis is within noise of the raw one, and the [0,5)
lens actually DILUTES slightly (5.63 → 5.09) as ex-halt windows re-enter
it. **Sparsity itself is the poison; halt contamination was negligible.
The raw gap family stands as built; the adjusted family = a cleaner
DEFINITION, not a better signal.**

**⭐ But the halt CLOCK is a real new axis:**

| slice | n | tkds | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| no halt today | 20,121 | 3219 | 2.414 | 73.7 | +2.07 | 5.11 | 3.57 | 1.73 | 1.77 | 2.27 | 1.82 | 1.86 |
| ⭐ halts_today = 1 | 3,118 | 382 | 4.103 | 76.6 | +2.49 | 3.6 | 6.72 | 1.95 | 1.24 | 7.35 | 4.95 | 4.8 |
| halts_today = 2-3 | 1,853 | 224 | 2.808 | 75.3 | +2.32 | 2.77 | 3.64 | 6.62 | 1.65 | 3.48 | 2.04 | 4.72 |
| ⚠ halts_today >= 4 | 1,449 | 156 | 1.397 | 70.7 | +2.45 | 1.0 | 10.1 | 0.39 | 2.29 | 1.48 | 1.31 | 14.18 |
| ssh [0,120) | 50 | 14 | 7.811 | 72.0 | +4.3 | 0.0 | - | 2.46 | 3.64 | inf | 4.81 | inf |
| ssh [120,600) | 129 | 21 | 2.512 | 72.1 | +2.75 | inf | inf | - | 0.58 | 42.02 | 0.88 | 31.57 |
| ssh [600,1800) | 992 | 137 | 3.431 | 76.4 | +2.76 | 2.6 | 2.12 | 2.79 | 7.69 | 34.89 | 2.15 | 5.52 |
| ssh >= 1800 | 5,249 | 654 | 2.467 | 74.7 | +2.36 | 2.31 | 7.09 | 0.82 | 1.39 | 2.82 | 2.79 | 5.46 |

**halts_today is MONOTONE-INVERTED: one halt = a great day-context (4.10
on 382 tkds — census-REAL, the S40w playbook slice at 7.5× the tkds), a
few halts = fine, a halt CASCADE (>= 4) = 1.40 with 2022 0.39 — the
death-spiral names are bad fades = a new AVOID rule.** The 10-30-min-
post-reopen band (3.43 on 137 tkds) is the tradable aftermath; the
immediate reopen ([0,120) = 14 tkds) stays anecdote. Overlay roster:
+halts_today = 1 (day-context 4.10), +avoid halts_today >= 4; the S40w
mr-based improvisation RETIRES in favor of the principled detector.

## S40y — leg-anchored OLS + the leg-age axes (user's night-cap idea) (2026-08-02, small hours)

**base_v7 = THE base** (2,217,950, zero-diff vs base_v6; bars_since_high >=
bars_since_first_low on every row). New cols: `ols_slope_since_high`/`_r_`/
`bars_since_high` (growing window from the bar after the last 20m high) and
`ols_slope_since_flow`/`_r_` (from the leg's first low, that bar inclusive).
Median windows: since-high 1,639 bars, since-flow 913 — genuinely leg-shaped.
**The correlation structure is the headline: slope_since_high ↔ slope_1200 =
0.747 (near-duplicate of the fixed clock), but slope_since_flow ↔ slope_1200
= 0.349 and r_since_flow ↔ {r_1200, eff_20m} = 0.344/0.125 — THE FLOW-
ANCHORED FAMILY IS MOSTLY NEW INFORMATION.** (bp/min = slope×6e5, house
convention.)

| slope since HIGH (bp/min) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-300,-200) | 111 | 6.296 | 82.0 | +4.25 | 1.4 | - | - | inf | inf | 31.59 | 0.69 |
| [-200,-140) | 553 | 3.962 | 73.2 | +3.8 | 4.42 | 1.27 | 2.68 | 5.01 | 6.92 | 2.88 | 5.5 |
| [-140,-100) | 1,813 | 3.657 | 79.1 | +2.72 | 13.72 | 10.78 | 3.59 | 2.98 | 1.94 | 2.61 | 4.81 |
| [-100,-70) | 4,401 | 2.135 | 73.5 | +2.3 | 4.5 | 2.09 | 1.1 | 1.08 | 2.01 | 2.44 | 2.07 |
| [-70,-50) | 6,159 | 2.552 | 74.6 | +2.13 | 5.56 | 3.82 | 0.92 | 1.29 | 2.46 | 2.54 | 4.11 |
| [-50,-35) | 6,728 | 2.537 | 73.7 | +2.1 | 4.09 | 4.31 | 1.55 | 3.83 | 3.19 | 1.63 | 1.75 |
| [-35,-25) | 3,925 | 2.158 | 72.9 | +1.99 | 1.81 | 4.06 | 3.95 | 2.82 | 2.98 | 1.23 | 2.74 |
| [-25,-15) | 2,253 | 2.147 | 73.5 | +1.82 | 6.26 | 7.06 | 1.72 | 0.73 | 2.18 | 1.63 | 2.81 |
| [-15,∞) | 592 | 1.938 | 68.6 | +1.6 | 3.74 | 5.48 | 1.27 | 3.22 | 7.71 | 2.74 | 0.17 |

| slope since FIRST LOW (bp/min) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -300 | 171 | 1.776 | 67.3 | +2.09 | 6.58 | 2.62 | - | 0.46 | 2.96 | 0.8 | 13.15 |
| [-300,-200) | 237 | 3.52 | 71.3 | +3.54 | 8.1 | 2.11 | inf | 1.16 | 69.58 | 1.62 | 32.28 |
| [-200,-140) | 756 | 2.735 | 76.6 | +2.88 | 3.59 | 3.49 | 0.66 | 1.55 | 4.78 | 4.33 | 3.07 |
| ⭐ [-140,-100) | 1,853 | 4.616 | 78.5 | +3.04 | 9.17 | 4.36 | 2.79 | 1.79 | 8.68 | 3.77 | 3.98 |
| [-100,-70) | 3,864 | 2.584 | 75.7 | +2.51 | 4.56 | 5.44 | 2.18 | 1.83 | 2.36 | 2.25 | 1.51 |
| [-70,-50) | 5,527 | 2.351 | 75.4 | +2.21 | 3.89 | 4.84 | 1.13 | 3.07 | 2.19 | 1.76 | 2.46 |
| [-50,-35) | 5,716 | 2.15 | 73.3 | +1.99 | 3.94 | 2.92 | 1.62 | 1.65 | 2.12 | 1.44 | 3.48 |
| [-35,-25) | 4,264 | 2.654 | 74.4 | +2.03 | 4.08 | 3.63 | 2.01 | 1.33 | 2.63 | 1.98 | 4.14 |
| [-25,-15) | 2,788 | 1.966 | 69.9 | +1.63 | 2.64 | 4.09 | 1.27 | 1.14 | 2.09 | 1.9 | 1.31 |
| [-15,-5) | 1,109 | 2.149 | 68.1 | +1.51 | 1.95 | 3.72 | 2.0 | 1.19 | 2.81 | 4.14 | 0.91 |
| >= -5 | 256 | 2.231 | 66.4 | +2.02 | 48.78 | 10.21 | 0.3 | 2.19 | 2.96 | 2.42 | 0.32 |

| r since FIRST LOW | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ⚠ [-1.01,-0.95) | 406 | 1.221 | 69.5 | +2.01 | 0.52 | 3.61 | inf | 1.87 | 1.17 | 1.04 | 9.01 |
| [-0.95,-0.9) | 3,855 | 2.474 | 76.3 | +2.31 | 7.42 | 4.22 | 1.47 | 1.46 | 2.68 | 2.09 | 2.1 |
| [-0.9,-0.8) | 10,080 | 2.348 | 74.3 | +2.18 | 4.65 | 3.7 | 1.55 | 1.7 | 2.3 | 1.57 | 3.45 |
| [-0.8,-0.7) | 5,428 | 2.714 | 75.2 | +2.23 | 3.34 | 3.22 | 1.17 | 3.04 | 3.58 | 2.39 | 2.38 |
| [-0.7,-0.5) | 4,897 | 2.748 | 73.3 | +2.04 | 6.67 | 6.17 | 2.11 | 1.45 | 2.45 | 2.21 | 1.74 |
| [-0.5,-0.3) | 1,217 | 2.008 | 65.7 | +1.42 | 2.09 | 2.18 | 0.81 | 0.88 | 1.61 | 5.65 | 1.61 |
| [-0.3,0) | 564 | 2.924 | 71.3 | +2.19 | 3.72 | 2.81 | 0.49 | 1.47 | 4.96 | 4.38 | 2.0 |
| [0,1.01) | 94 | 2.614 | 70.2 | +2.28 | inf | 8.05 | 0.4 | 3.37 | inf | 10.41 | 2.15 |

| bars since HIGH (leg age) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,1300) | 7,450 | 2.528 | 73.2 | +1.97 | 6.59 | 2.8 | 1.55 | 1.49 | 2.17 | 2.26 | 2.13 |
| [1300,1500) | 3,474 | 3.019 | 74.2 | +2.32 | 5.88 | 2.84 | 2.67 | 2.12 | 2.2 | 3.2 | 3.77 |
| [1500,1800) | 4,648 | 2.586 | 76.3 | +2.29 | 4.71 | 4.62 | 0.73 | 1.92 | 3.45 | 2.3 | 3.43 |
| [1800,2400) | 5,348 | 2.133 | 73.7 | +2.16 | 2.25 | 6.11 | 1.17 | 1.44 | 2.47 | 1.92 | 2.25 |
| [2400,3600) | 4,579 | 2.152 | 72.4 | +2.1 | 3.64 | 5.6 | 2.96 | 1.86 | 2.78 | 1.05 | 2.41 |
| [3600,6000) | 1,041 | 3.934 | 78.0 | +2.4 | 2.97 | inf | 5.54 | 2.44 | 6.07 | 18.16 | 1.57 |

| bars since FIRST LOW (flush age) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,300) | 1,193 | 3.59 | 78.9 | +2.51 | 22.3 | 2.65 | 3.27 | 0.81 | 6.55 | 2.43 | 4.98 |
| [300,600) | 5,415 | 2.851 | 73.7 | +2.21 | 6.97 | 4.48 | 0.96 | 2.16 | 2.75 | 2.37 | 2.79 |
| [600,900) | 6,414 | 2.466 | 73.8 | +2.01 | 5.17 | 2.39 | 1.86 | 2.35 | 1.83 | 2.66 | 1.85 |
| [900,1200) | 5,069 | 2.98 | 76.9 | +2.41 | 2.76 | 6.83 | 1.11 | 2.0 | 3.29 | 3.12 | 8.77 |
| [1200,1800) | 4,419 | 1.86 | 72.8 | +2.11 | 3.03 | 5.09 | 1.09 | 1.16 | 2.4 | 1.41 | 1.63 |
| [1800,3000) | 3,498 | 2.091 | 71.1 | +1.89 | 2.95 | 4.32 | 4.35 | 1.65 | 2.71 | 1.01 | 4.05 |
| [3000,6000) | 533 | 1.964 | 70.5 | +1.94 | 2.04 | inf | 2.23 | 1.19 | 6.84 | 12.48 | 0.68 |

**READING.** (a) **slope_since_flow ∈ [−140,−100) bp/min = 1,853 @ 4.616 /
78.5%, ALL years positive (worst 1.79)** — the leg's own decline rate in
the "fast but not vertical" band is a genuine new overlay, and at 0.349
corr with the 20m clock it's near-new information (the fixed windows blur
legs of different ages; the anchored window measures THE flush itself).
Too-vertical (< −300 = 1.78) and too-shallow (> −25 ≈ 2.0) both fade.
(b) slope_since_high ≈ the 20m slope (0.747) — steeper-is-better gradient,
no new seat. (c) **r_since_flow < −0.95 = 1.221 on 406 — the leg-scoped
PERFECT-LINE avoid sliver** (S38q's 10m-linearity lesson, now measured on
the leg's own shape; corr with eff_20m just 0.125). (d) The leg-age axes
are mild: flush-age [1200,1800) = 1.86 is the stale-flush trough (aged
20-30min unresolved), old-leg [3600,6000) = 3.93 tail-flavored; no gates
here. ⏭ overlay interaction pass: ssf-band × the S40 roster + mc=1.

## S40z — ⭐ SPEC v2.1 BAKED (the falling-knife gate) + the anchored eff pair (2026-08-02, night)

**SPEC v2.1 = v2.0 + `ols_r_since_flow >= -0.95`** (user: the quantitative
form of "don't catch falling knives" — a leg that is one clean regression
line since its first low is a drift, not a capitulation; unwarm fails;
`--min-r-since-flow`, <= -1 = off; the canonical base CLI gains
`--min-r-since-flow -1`). **base_v8 = THE base** (2,217,950, zero-diff vs
v7) with the anchored eff pair; **`v21_reference/` GRAND PARITY ✓: engine
42,868 = SQL 42,868. Book 26,135 @ 2.501 / 74.1%** (years 4.49/3.83/1.43/
1.72/2.62/2.00/2.45); **mc=1 3,829 @ 2.217** — flat vs v2.0's 2.218: the
gate is mc=0-accretive (+0.036 for −406 trips), slot-neutral — INSURANCE-
class, adopted on principle (ladder: … 2.211 → 2.218 → 2.217).

**The anchored eff pair (user, slot-based — `AnchoredEff` builds 30-bar
vwap slots internally, boundaries aligned to the anchor; sub-30s returns =
microstructure noise, F7):** esh q05/med/q95 = −0.63/−0.38/−0.21; esf =
−0.69/−0.33/−0.14. **corr with eff_20m: 0.196 / 0.175 — the anchored eff
is nearly ORTHOGONAL to the fixed-window eff family** (esf↔ssf 0.637,
esf↔rsf 0.34); the leg's own efficiency is different information from the
20m clock's.

| eff_since_high | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-1.01,-0.8) | 85 | 4.528 | 64.7 | +1.19 | 134.05 | inf | 0.0 | 0.89 | inf | 0.69 | 2.45 |
| [-0.8,-0.7) | 417 | 2.576 | 71.5 | +1.63 | inf | 9.49 | 1.87 | 0.83 | 1.77 | 1.37 | 2.94 |
| [-0.7,-0.6) | 1,154 | 2.538 | 75.1 | +1.86 | 28.67 | 10.14 | 0.81 | 1.61 | 2.42 | 3.99 | 1.31 |
| [-0.6,-0.5) | 3,105 | 3.439 | 76.1 | +2.2 | 9.16 | 3.11 | 2.36 | 2.77 | 2.86 | 4.18 | 2.4 |
| [-0.5,-0.4) | 6,855 | 2.645 | 74.3 | +2.19 | 4.21 | 3.24 | 2.38 | 1.71 | 2.55 | 2.04 | 3.22 |
| [-0.4,-0.3) | 8,154 | 2.259 | 74.7 | +2.16 | 5.45 | 3.63 | 0.79 | 1.26 | 2.78 | 1.85 | 3.43 |
| [-0.3,-0.2) | 5,200 | 2.227 | 72.5 | +2.27 | 2.62 | 5.39 | 5.14 | 2.31 | 2.31 | 1.55 | 1.61 |
| [-0.2,-0.1) | 1,161 | 3.171 | 71.1 | +1.86 | 7.88 | 10.27 | 1.42 | 2.38 | 3.89 | 2.37 | 1.84 |

| eff_since_flow | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-1.01,-0.8) | 698 | 4.757 | 80.1 | +2.59 | 61.04 | 7.15 | 3.38 | 0.55 | 5.62 | 3.79 | 31.32 |
| [-0.8,-0.7) | 564 | 1.945 | 69.0 | +2.38 | 2.73 | 1.06 | 0.32 | 0.71 | 6.78 | 2.55 | 8.48 |
| [-0.7,-0.6) | 1,070 | 3.313 | 77.1 | +2.09 | 9.71 | 9.04 | 1.37 | 3.18 | 3.18 | 1.82 | 3.03 |
| ⭐ [-0.6,-0.5) | 2,062 | 4.071 | 78.4 | +2.33 | 8.76 | 4.47 | 3.16 | 2.5 | 5.53 | 3.12 | 3.17 |
| [-0.5,-0.4) | 4,189 | 2.85 | 75.6 | +2.2 | 7.53 | 3.95 | 1.84 | 2.98 | 2.8 | 2.96 | 1.03 |
| [-0.4,-0.3) | 6,892 | 2.407 | 74.5 | +2.15 | 6.17 | 3.18 | 1.42 | 1.73 | 2.25 | 1.87 | 2.52 |
| [-0.3,-0.2) | 6,955 | 2.245 | 73.7 | +2.2 | 2.67 | 4.01 | 1.21 | 1.91 | 2.1 | 1.73 | 3.81 |
| [-0.2,-0.1) | 3,148 | 2.07 | 69.9 | +1.91 | 3.33 | 6.75 | 1.09 | 0.94 | 2.59 | 1.54 | 2.26 |
| [-0.1,0) | 530 | 2.182 | 61.9 | +1.09 | 6.44 | 2.84 | 4.44 | 1.22 | 3.77 | 2.31 | 0.85 |

**READING:** eff_since_flow is the live one — **[-0.6,-0.5) = 2,062 @
4.071 / 78.4, ALL years >= 2.5** (overlay candidate: an efficiently-but-
not-perfectly declining flush), with a [-0.8,-0.7) trough (1.95, 2022-23
= 0.32/0.71) and a 2023-warted ultra-efficient extreme. eff_since_high's
best band [-0.6,-0.5) = 3.44 is its softer echo. ⏭ TOMORROW (user's plan):
slope_since_flow [-140,-100) as a REPLACEMENT for the dvw + d20m-high
pair (test with both off); r_since_flow vs rngfront replacement; + the
esf band joins the overlay interaction matrix; then WRAP-UP (mc=1 passes,
pyramid re-cut).

## S41 — r_since_flow vs rngfront: NOT redundant, both keep their seats (2026-08-02)

**Question (user):** does the new falling-knife gate (`ols_r_since_flow >=
-0.95`) make `rng_300/rng_20m < 0.8` (rngfront, "a hack in my eyes")
obsolete? Recall: rngfront was mc=0-weak and earned its seat at mc=1.

**Method:** residual universe = v2.1 with BOTH gates off, SQL over base_v8
+ $1-$10 book cut = **27,377 trips** (v2.1 anchor reproduced exactly:
26,135 @ 2.501). Fine breakdowns of each axis on the residual universe,
overlap cross-table, then mc=1 greedy replay of all four on/off combos.

| rngfront bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0.1,0.2) | 358 | 2.42 | 73.2 | +1.74 | 2.01 | 7.64 | — | 1.4 | 0.72 | 54.09 | 0.98 |
| [0.2,0.3) | 2,910 | 2.744 | 75.5 | +2.02 | 6.22 | 3.31 | 4.35 | 2.71 | 2.07 | 2.5 | 1.38 |
| [0.3,0.4) | 6,129 | 2.592 | 74.4 | +2.14 | 3.68 | 2.73 | 1.67 | 2.03 | 3.38 | 2.04 | 3.02 |
| [0.4,0.5) | 6,909 | 2.182 | 72.1 | +2.04 | 3.03 | 3.95 | 1.31 | 2.4 | 1.62 | 1.64 | 3.74 |
| [0.5,0.6) | 5,351 | 2.487 | 75.3 | +2.32 | 5.06 | 3.8 | 1.17 | 1.55 | 3.25 | 1.94 | 2.39 |
| [0.6,0.7) | 3,199 | 2.776 | 73.9 | +2.28 | 5.91 | 7.78 | 1.12 | 1.78 | 2.75 | 2.68 | 1.77 |
| [0.7,0.8) | 1,685 | 2.312 | 74.2 | +2.32 | 4.37 | 6.17 | 1.04 | 0.52 | 8.57 | 1.39 | 3.03 |
| [0.8,0.9) | 665 | 1.714 | 69.0 | +2.44 | 7.0 | 2.12 | 0.52 | 1.13 | 1.93 | 0.99 | 8.02 |
| [0.9,1.0) | 144 | 5.508 | 84.0 | +2.66 | — | 30.07 | — | — | 0.66 | — | — |
| [1.0,1.1) | 27 | 1.546 | 51.9 | +0.11 | 0.22 | — | — | — | — | — | — |

| rsf bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [-0.99,-0.98) | 26 | 16.576 | 96.2 | +3.19 | — | — | — | — | — | — | — |
| [-0.98,-0.97) | 38 | 8.445 | 89.5 | +3.0 | — | — | — | 4.02 | 2.74 | — | 2.22 |
| [-0.97,-0.96) | 108 | 1.436 | 72.2 | +2.35 | 1.17 | 1.7 | — | 7.54 | 1.16 | 0.66 | — |
| [-0.96,-0.95) | 285 | 0.897 | 60.7 | +1.65 | 0.34 | 3.12 | — | 0.74 | 0.55 | 1.76 | 28.88 |
| [-0.95,-0.94) | 433 | 2.141 | 75.5 | +2.21 | 4.45 | 9.73 | 1.41 | 2.11 | 1.56 | 4.67 | 0.43 |
| [-0.94,-0.93) | 636 | 2.642 | 74.8 | +2.67 | 2.89 | 2.95 | 1.78 | 1.37 | 11.0 | 1.0 | 81.71 |
| [-0.93,-0.92) | 871 | 3.466 | 78.2 | +2.54 | 28.58 | 11.46 | 1.34 | 0.98 | 3.71 | 2.15 | 4.24 |
| [-0.92,-0.91) | 957 | 2.233 | 74.4 | +1.97 | 7.15 | 5.76 | 4.09 | 1.4 | 1.05 | 2.98 | 1.37 |
| [-0.91,-0.9) | 1,009 | 2.308 | 77.1 | +2.16 | 7.46 | 2.93 | 0.99 | 2.53 | 5.86 | 1.95 | 1.64 |
| [-0.9,-0.85) | 5,686 | 2.698 | 75.3 | +2.14 | 5.42 | 3.78 | 2.1 | 1.67 | 2.34 | 2.22 | 3.5 |
| [-0.85,-0.8) | 4,488 | 2.042 | 73.0 | +2.25 | 4.01 | 3.78 | 1.21 | 1.76 | 2.3 | 1.14 | 3.47 |
| [-0.8,-0.75) | 3,201 | 2.573 | 75.3 | +2.27 | 3.45 | 3.34 | 1.28 | 2.43 | 2.75 | 2.09 | 3.49 |
| [-0.75,-0.7) | 2,391 | 2.817 | 75.4 | +2.22 | 3.42 | 3.17 | 1.12 | 4.13 | 4.3 | 2.5 | 1.76 |
| [-0.7,-0.65) | 1,754 | 2.639 | 74.7 | +2.19 | 4.99 | 5.87 | 1.55 | 2.4 | 3.69 | 2.0 | 1.56 |
| [-0.65,-0.6) | 1,471 | 3.44 | 76.5 | +2.06 | 19.52 | 5.37 | 0.9 | 3.14 | 3.44 | 2.97 | 2.52 |
| [-0.6,-0.55) | 1,124 | 2.145 | 69.5 | +1.89 | 8.04 | 6.41 | 1.19 | 0.49 | 1.51 | 2.81 | 2.11 |
| [-0.55,-0.5) | 798 | 2.375 | 68.4 | +2.02 | 5.62 | 11.87 | 0.95 | 1.27 | 1.51 | 1.49 | 2.33 |
| [-0.5,-0.45) | 525 | 1.538 | 65.3 | +1.36 | 3.55 | 1.32 | 0.52 | 0.9 | 0.66 | 2.93 | 2.76 |
| [-0.45,-0.4) | 392 | 1.957 | 62.2 | +1.09 | 1.95 | 2.25 | 1.6 | 1.69 | 2.58 | 2.06 | 0.95 |
| [-0.4,-0.35) | 276 | 2.287 | 65.6 | +1.62 | 2.42 | 2.4 | 1.52 | 1.02 | 2.7 | 4.65 | 0.92 |
| [-0.35,-0.3) | 176 | 3.927 | 77.8 | +2.44 | 2.17 | 2.73 | 1.42 | 1.21 | 6.82 | 57.44 | — |
| [-0.3,-0.25) | 153 | 1.851 | 71.2 | +1.48 | 1.18 | 0.66 | 0.2 | 1.16 | 6.19 | 5.43 | — |
| [-0.25,-0.2) | 142 | 2.276 | 71.8 | +1.71 | 3.14 | 0.78 | 15.47 | 9.95 | 4.85 | 1.19 | 5.87 |
| [-0.2,-0.15) | 99 | 4.999 | 69.7 | +1.74 | 8.62 | 4.63 | 0.11 | 1.2 | 15.0 | — | — |
| [-0.15,-0.1) | 105 | 3.427 | 66.7 | +3.23 | 6.55 | 2.18 | — | 1.63 | 5.52 | 3.76 | — |
| [-0.1,-0.05) | 70 | 2.828 | 72.9 | +2.82 | 3.86 | — | — | 3.55 | 0.74 | 3.67 | — |
| [-0.05,0) | 53 | 1.248 | 66.0 | +1.81 | — | 14.83 | 0.03 | 1.22 | 0.78 | 13.21 | — |
| [0,0.05) | 54 | 1.931 | 64.8 | +1.45 | — | 7.78 | 0.23 | 2.12 | — | 6.31 | — |
| [0.05,0.1) | 32 | 3.929 | 84.4 | +3.06 | — | 8.1 | 0.58 | 0.37 | — | — | — |
| >= 0.1 | 24 | (mixed) | 87.5 | +4.35 | — | — | — | — | — | — | — |

(Rows >= 0.1 collapsed: 24 trips, mostly winners, pure anecdote. Fine
0.01 buckets only below -0.90; 0.05 bands elsewhere.)

**Overlap cross-table (the redundancy test):**

| slice | n | PF | win% | med |
|---|---|---|---|---|
| front-cut × rsf-cut (both remove) | 51 | 1.799 | 54.9 | +3.06 |
| front-cut × rsf-pass (only rngfront removes) | 785 | 2.1 | 72.1 | +2.43 |
| front-pass × rsf-cut (only rsf removes) | 406 | 1.221 | 69.5 | +2.01 |
| front-pass × rsf-pass (the v2.1 book) | 26,135 | 2.501 | 74.1 | +2.15 |

**The four combos, mc=0 and mc=1 (greedy replay on the $1-$10 book):**

| config | mc=0 n | mc=0 PF | mc=1 n | mc=1 PF |
|---|---|---|---|---|
| both OFF | 27,377 | 2.447 | 3,946 | 2.158 |
| rsf only | 26,920 | 2.483 | 3,907 | 2.164 |
| rngfront only (= v2.0) | 26,541 | 2.465 | 3,863 | 2.218 |
| both ON (= v2.1) | 26,135 | 2.501 | 3,829 | 2.217 |

**READING — NOT redundant; the gates are complements, not substitutes:**
(a) The cut slices are nearly DISJOINT: of ~1,240 removed trips only 51
are removed by both (Jaccard 0.04). rsf < -0.95 is not "rngfront in leg
coordinates" — it removes different trips. (b) **Marginal value at mc=1:
rngfront +0.053 WITH rsf already in the spec** (2.164 → 2.217; +0.060
without it) — the front-loaded-range gate still owns the slot-level
improvement, concentrated in 2022/2024/2025. **rsf's mc=1 marginal ≈ 0**
(2.158 → 2.164 alone; -0.001 on top of rngfront) — pure insurance, as
S40z measured. (c) At mc=0 the roles flip: rsf cuts the cleaner slice
(1.221 vs rngfront's 2.10) and adds +0.036 vs rngfront's +0.018. Each
gate carries the half the other doesn't. (d) Inside the rsf tail the
damage is concentrated in [-0.97,-0.95) (393 @ ~1.0); the sub--0.98
extreme is 64 trips @ ~11 — perfect-line-but-profitable anecdote, not
worth a carve-out (census rule). **VERDICT: rngfront STAYS. v2.1
unchanged.**

## S41b — esf as an eff_20m replacement: FIRST challenger to beat a baked gate at mc=1 (2026-08-02)

**Question (user):** can `eff_since_flow` replace the `|eff_20m| ∈
[0.3,0.5)` band? **Residual universe** = v2.1 with the eff band OFF (all
else on, incl. rngfront + rsf) = 45,686 @ 2.142.

| abs(eff_20m) bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,0.1) | 603 | 1.618 | 65.2 | +1.75 | 2.99 | 1.46 | 7.77 | 0.7 | 6.2 | 1.15 | 0.44 |
| [0.1,0.2) | 3,660 | 2.037 | 71.7 | +2.2 | 3.0 | 3.24 | 1.31 | 1.01 | 1.94 | 2.38 | 1.61 |
| [0.2,0.3) | 10,956 | 1.719 | 70.6 | +1.95 | 2.16 | 2.22 | 0.92 | 1.71 | 1.74 | 1.82 | 1.23 |
| [0.3,0.4) | 15,572 | 2.439 | 73.5 | +2.13 | 4.04 | 3.39 | 1.1 | 1.6 | 2.45 | 2.29 | 2.66 |
| [0.4,0.5) | 10,563 | 2.597 | 74.9 | +2.19 | 5.57 | 4.66 | 2.18 | 1.9 | 2.92 | 1.7 | 2.22 |
| [0.5,0.6) | 3,355 | 1.885 | 73.3 | +1.98 | 2.82 | 4.54 | 1.29 | 1.22 | 2.2 | 1.37 | 3.31 |
| [0.6,0.7) | 491 | 1.458 | 67.0 | +1.3 | 6.72 | 5.52 | 0.33 | 3.03 | 4.26 | 0.87 | 0.21 |
| [0.7,0.8) | 81 | 0.579 | 59.3 | +1.52 | — | 22.22 | 0.39 | — | 41.73 | 0.38 | — |
| [0.8,0.9) | 3 | — | 0.0 | −2.53 | — | — | — | — | — | — | — |
| NULL | 402 | 1.813 | 66.4 | +1.41 | 1.51 | 1.51 | 6.08 | 0.04 | 2.64 | 2.79 | 9.12 |

| eff_since_flow bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| = -1 (monotone) | 217 | 3.932 | 73.3 | +2.32 | 215.8 | 2.27 | 0.87 | — | 8.98 | 34.57 | 1.25 |
| [-1.0,-0.9) | 638 | 3.634 | 74.5 | +2.28 | 26.51 | 11.59 | 1.44 | 0.79 | 2.28 | 2.19 | 12.98 |
| [-0.9,-0.8) | 569 | 2.329 | 72.4 | +1.97 | 3.35 | 1.55 | 1.01 | 1.1 | 7.22 | 4.96 | 6.39 |
| [-0.8,-0.7) | 1,029 | 1.809 | 69.5 | +2.15 | 3.45 | 1.09 | 0.45 | 2.79 | 4.68 | 2.03 | 1.78 |
| [-0.7,-0.6) | 1,979 | 2.121 | 73.1 | +2.01 | 8.88 | 2.47 | 0.93 | 2.52 | 3.12 | 1.09 | 1.94 |
| [-0.6,-0.5) | 3,562 | 2.977 | 76.7 | +2.2 | 7.45 | 5.78 | 1.69 | 2.31 | 2.48 | 2.38 | 3.22 |
| [-0.5,-0.4) | 6,317 | 2.274 | 74.4 | +2.13 | 4.1 | 3.93 | 1.23 | 2.45 | 2.2 | 2.07 | 1.3 |
| [-0.4,-0.3) | 9,583 | 2.359 | 74.5 | +2.12 | 6.15 | 2.99 | 1.76 | 1.59 | 2.02 | 1.8 | 2.73 |
| [-0.3,-0.2) | 12,132 | 2.088 | 73.5 | +2.16 | 2.48 | 3.31 | 1.5 | 1.47 | 1.93 | 1.9 | 2.44 |
| [-0.2,-0.1) | 7,780 | 1.746 | 68.4 | +1.86 | 1.98 | 3.44 | 1.03 | 0.98 | 2.63 | 1.55 | 1.25 |
| [-0.1,0) | 1,751 | 1.634 | 63.2 | +1.34 | 4.39 | 1.47 | 0.37 | 1.7 | 2.36 | 2.74 | 0.71 |
| >= 0 | 91 | ~5.6 | 81 | +2.96 | anecdote (85 in [0,0.1) 2021-carried) | | | | | | |
| NULL | 38 | 0.687 | 52.6 | +1.65 | — | 1.33 | — | 0.3 | — | 0.51 | 3.44 |

(User audit: |eff| <= 1 by construction; the "= -1" row is float noise
at EXACTLY -1 — monotone flushes, every 30s slot below the last (max
deviation 2e-16; 0 of 2.2M base trips exceed |1| beyond epsilon). The
-1.0 bucket edge splits the monotone population: 217 epsilon-below + 17
exact + 245 within 1e-4 in the [-1.0,-0.9) row — ~480 monotone-flush
trips @ ~3.7, echoing the rsf sub--0.98 finding: the truly perfect line
snaps back; the almost-perfect keeps falling.)

**Conjunction check (why esf's S40z star band was so strong):** esf-star
[-0.6,-0.5) WITH the eff band = 4.071 (2,062) but WITHOUT it = **2.098**
(1,500) — the star is a CONJUNCTION, not a standalone. Consistent with
corr 0.175 (orthogonal ≠ substitute).

**The head-to-head (mc=0 on the residual, mc=1 greedy replay):**

| gate | mc=0 n | mc=0 PF | mc=1 n | mc=1 PF | mc=1 worst yr |
|---|---|---|---|---|---|
| neither | 45,686 | 2.142 | 5,325 | 1.986 | 1.51 (2022) |
| eff band [0.3,0.5) (= v2.1) | 26,135 | 2.501 | 3,829 | 2.217 | 1.58 (2023) |
| esf < -0.25 | 30,034 | 2.330 | **3,995** | **2.288** | 1.75 (2022) |
| esf ∈ [-0.7,-0.25) | 27,581 | 2.325 | 3,775 | 2.252 | — |
| esf < -0.3 | 23,894 | 2.390 | 3,347 | 2.271 | — |
| STACK eff band + esf < -0.2 | 22,430 | 2.591 | 3,422 | 2.292 | — |

esf < -0.25 mc=1 years: 3.42 / 2.46 / 1.75 / 1.85 / 2.68 / 1.92 / 2.15
(vs eff band 3.28 / 2.74 / 1.88 / 1.58 / 2.27 / 2.07 / 1.81 — esf wins
4/7 incl. both books' worst year).

**READING — the S39 slot-allocation theme INVERTED for the first time:**
every prior challenger won mc=0 and lost mc=1; **esf < -0.25 LOSES mc=0
(2.330 vs 2.501 at iso-n — the eff band's conjunction cells concentrate
PF) but DOMINATES mc=1 on BOTH axes: +166 trips AND +0.071 PF AND better
worst-year (1.75 vs 1.58)**. At mc=1 the eff band is the redundant one:
stacking it on esf < -0.2 gives +0.004 PF for −573 trips. The leg-native
efficiency spreads good trips across days better than the fixed 20m
window (slot efficiency), while the 20m band packs them into conjunction
cells (book PF). Trade-off is REAL: replace = mc=0 book 2.501 → ~2.33
(+3,899 trips), mc=1 2.217 → 2.288. Decision = user's (spec surgery at
wrap-up stage re-opens reference/parity/overlay work).

**VERDICT (user): NOT worth the mc=0 loss — esf will NOT be a filter;
the eff_20m band stays.** (esf remains overlay material via the S40z
star band, which is a conjunction WITH the eff band.)

## S41c — ssf < -25 CAN replace the dvw + d20m-high pair (2026-08-02)

**Question (user, from the S40y queue):** can `slope_since_flow` replace
BOTH `dvw < -5%` (dist from 20m vwap) and `d20 < -10%` (dist from 20m
high)? Also: do we have a vwap-since-flow feature? — YES, implicitly:
`dv_leg / vol_leg` = the leg's vwap since the first-low anchor;
`dlv = signal_vwap/(dv_leg/vol_leg)-1` needs no engine change.

**Residual universe** = v2.1 with BOTH distance gates OFF = 32,696 @
2.385 (pair keeps 26,135 @ 2.501 = the anchor, exact). Axes on it: dvw
and d20 = clean monotone deeper-is-better gradients (dvw [-3,-2) = 0.57
/ [-4,-3) = 1.33 shallow-end poison; d20 [-6,-4) = 0.68 / [-8,-6) =
1.58).

**The ssf axis (bp/min) on the residual:**

| ssf bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -400 | 54 | 0.581 | 48.1 | -0.35 | — | — | — | — | 6.41 | 1.35 | — |
| [-400,-375) | 23 | 0.625 | 52.2 | +0.12 | — | — | — | — | 0.36 | — | — |
| [-375,-350) | 25 | 12.3 | 84.0 | +2.68 | — | — | — | — | — | — | — |
| [-350,-325) | 13 | inf | 100.0 | +3.78 | — | — | — | — | — | — | — |
| [-325,-300) | 21 | 31.471 | 90.5 | +4.08 | — | 276.5 | — | — | — | 14.77 | — |
| [-300,-275) | 29 | 20.87 | 93.1 | +4.34 | — | — | — | 0.66 | — | — | — |
| [-275,-250) | 32 | 7.885 | 75.0 | +4.85 | — | 3.16 | — | 0.99 | — | — | 35.08 |
| [-250,-225) | 48 | 6.416 | 87.5 | +4.21 | 3.39 | 15.21 | — | — | — | — | 66.37 |
| [-225,-200) | 110 | 1.739 | 58.2 | +1.76 | — | 0.56 | — | 1.4 | 47.19 | 0.57 | 1.59 |
| [-200,-175) | 180 | 1.35 | 66.1 | +2.0 | 1.64 | 0.45 | 0.39 | 0.53 | 3.33 | 1.29 | 3.53 |
| [-175,-150) | 368 | 2.758 | 75.0 | +2.65 | 4.19 | 1.3 | 0.44 | 1.54 | 26.86 | 67.36 | 2.87 |
| [-150,-125) | 543 | 7.391 | 83.6 | +3.4 | 9.62 | 71.33 | 3.25 | 4.0 | 11.29 | 5.79 | 6.76 |
| [-125,-100) | 1,517 | 3.905 | 77.1 | +2.85 | 7.58 | 3.91 | 3.08 | 1.74 | 6.64 | 3.23 | 3.21 |
| [-100,-75) | 2,840 | 2.908 | 75.7 | +2.58 | 9.68 | 4.46 | 2.6 | 1.72 | 2.29 | 2.99 | 1.45 |
| [-75,-50) | 6,837 | 2.302 | 75.0 | +2.16 | 4.37 | 5.49 | 1.13 | 2.2 | 2.29 | 1.69 | 2.11 |
| [-50,-25) | 12,486 | 2.324 | 73.3 | +1.85 | 3.49 | 3.38 | 1.68 | 1.63 | 2.19 | 1.69 | 3.12 |
| [-25,0) | 7,416 | 1.955 | 69.9 | +1.39 | 2.56 | 2.85 | 1.2 | 1.45 | 2.09 | 1.98 | 1.12 |
| >= 0 | 154 | 0.965 | 57.1 | +1.14 | 0.43 | 0.69 | 0.62 | 3.37 | 4.37 | 4.6 | 2.11 |

Gradient: steeper is better down to ~-150; **[-25,0) = 1.955 and >= 0 =
0.965 = the shallow-drift poison; < -400 = 0.58 = the vertical poison**;
[-225,-175) trough (1.35-1.74) on small n with the [-375,-225) strong
zone above it also small-n (the -400 floor keeps both — only the
verified-toxic verticals go).

**The dlv axis (% from leg vwap) on the residual:**

| dlv bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -12 | 2,480 | 3.235 | 76.7 | +3.11 | 8.87 | 37.81 | 1.3 | 2.06 | 6.14 | 2.08 | 2.65 |
| [-12,-11) | 1,063 | 2.269 | 78.0 | +2.95 | 5.26 | 9.28 | 0.62 | 2.6 | 3.26 | 1.28 | 3.32 |
| [-11,-10) | 1,437 | 1.834 | 70.5 | +2.18 | 2.48 | 4.44 | 1.02 | 1.95 | 2.15 | 1.06 | 3.12 |
| [-10,-9) | 1,934 | 1.939 | 71.4 | +2.26 | 2.95 | 4.93 | 1.01 | 1.34 | 1.74 | 2.13 | 1.58 |
| [-9,-8) | 2,623 | 2.304 | 72.0 | +2.17 | 3.29 | 2.37 | 1.0 | 1.41 | 3.02 | 2.2 | 3.09 |
| [-8,-7) | 3,282 | 2.515 | 77.1 | +2.18 | 4.06 | 3.71 | 1.08 | 1.15 | 3.57 | 2.71 | 2.67 |
| [-7,-6) | 4,184 | 2.679 | 74.5 | +1.96 | 4.51 | 3.8 | 1.98 | 2.04 | 3.08 | 1.94 | 1.81 |
| [-6,-5) | 4,781 | 2.808 | 72.8 | +1.89 | 3.98 | 4.28 | 1.51 | 2.78 | 2.32 | 3.09 | 1.59 |
| [-5,-4) | 5,090 | 2.184 | 72.2 | +1.68 | 3.53 | 3.79 | 1.62 | 1.58 | 1.34 | 1.9 | 1.63 |
| [-4,-3) | 4,005 | 2.194 | 72.1 | +1.61 | 3.53 | 2.23 | 1.82 | 1.54 | 1.57 | 2.01 | 3.02 |
| [-3,-2) | 1,669 | 1.705 | 69.1 | +1.15 | 2.43 | 2.22 | 1.5 | 1.25 | 1.4 | 1.28 | 1.31 |
| [-2,-1) | 147 | 1.149 | 72.1 | +1.02 | 13.94 | 2.41 | 1.52 | 3.72 | 0.16 | 0.22 | 8.63 |
| [-1,0) | 1 | — | 100.0 | +3.02 | — | — | — | — | — | — | — |

dlv = HUMP at [-8,-5) ~2.5-2.8, soft [-11,-9) ~1.8-1.9, strong < -12
(3.24, 2021-flavored), **shallow poison [-3,0) = 1.15-1.71 all-years-
mediocre = what the -3 cut removes** — not gate-shaped alone, earns its
seat only next to ssf (corr 0.075).

**Head-to-head (mc=0 on residual, mc=1 greedy replay):**

| config | mc=0 n | mc=0 PF | mc=1 n | mc=1 PF | mc=1 worst yr |
|---|---|---|---|---|---|
| neither | 32,696 | 2.385 | 4,728 | 2.053 | 1.55 (2023) |
| PAIR dvw<-5 & d20<-10 (= v2.1) | 26,135 | 2.501 | 3,829 | 2.217 | 1.58 (2023) |
| ssf < -25 | 25,126 | 2.523 | 3,743 | 2.214 | 1.70 (2023) |
| ssf ∈ [-400,-25) | 25,072 | 2.535 | 3,738 | 2.227 | 1.73 (2023) |
| STACK pair + ssf<-25 | 21,982 | 2.593 | 3,287 | 2.279 | 1.77 (2023) |

mc=0 years, pair vs ssf<-25: 4.49/4.63, 3.83/3.71, 1.43/1.45, 1.72/1.75,
2.62/2.56, 2.00/1.97, 2.45/2.70 — statistically the same book. mc=1
years: ssf wins 2020-2023 (incl. worst yr 1.58→1.70), pair wins
2024/2026.

**Overlap:** both agree on 21,982 @ 2.593; pair-only-keeps 3,144 @ 1.916,
ssf-only-keeps 4,153 @ 2.03 — each cuts a below-book slice the other
misses (partial substitutes, unlike the S41 disjoint case).

**READING: the swap is essentially FREE — one leg-anchored feature
replaces two fixed-window distance features at the same frontier** (mc=0
2.523 vs 2.501 on -1,009 trips = iso-frontier; mc=1 dead heat 2.214 vs
2.217; worst-year better). The [-400 floor adds +0.013 at both levels
(the vertical-crash sliver, 54 trips @ 0.58). The STACK is the strongest
config at BOTH levels (2.593 / 2.279) if tightening is wanted. Decision
= user's: replace (simpler leg-native grammar), keep (status quo), or
stack.

## S41d — dlv joins: THE LEG-NATIVE PAIR {ssf, dlv} (2026-08-02)

**User: also study dist-from-vwap-since-flow (dlv) as a gate.** The
correlation matrix explains everything:

| | dvw | d20 | ssf | dlv |
|---|---|---|---|---|
| dvw | 1 | **0.946** | 0.526 | 0.558 |
| d20 | | 1 | 0.477 | 0.558 |
| ssf | | | 1 | **0.075** |
| dlv | | | | 1 |

**The incumbent "pair" is largely ONE feature counted twice (0.946).**
dlv = the same distance idea in leg coordinates (0.558 to both), but
nearly ORTHOGONAL to ssf (0.075) — so {ssf = leg speed, dlv = leg
stretch} spans two real dimensions where {dvw, d20} spans ~one.
Consistent with that: dlv adds +0.004 on top of the incumbent pair
(nothing), +0.032 on top of ssf (a real seat).

| config | mc=0 n | mc=0 PF | mc=1 n | mc=1 PF | mc=1 worst yr |
|---|---|---|---|---|---|
| PAIR dvw<-5 & d20<-10 (= v2.1) | 26,135 | 2.501 | 3,829 | 2.217 | 1.58 (2023) |
| dlv < -4 alone | 26,874 | 2.450 | 4,050 | 2.179 | 1.54 (2023) |
| ssf ∈ [-400,-25) alone | 25,072 | 2.535 | 3,738 | 2.227 | 1.73 (2023) |
| ⭐ LEG PAIR ssf band + dlv<-3 | 23,880 | 2.567 | 3,585 | 2.268 | 1.74 (2023) |
| LEG PAIR ssf band + dlv<-4 | 20,967 | 2.585 | 3,221 | 2.294 | 1.73 (2023) |
| triple (old pair + ssf<-25) | 21,982 | 2.593 | 3,287 | 2.279 | 1.77 (2023) |

**READING:** dlv alone is NOT gate-shaped (hump; mc=1 below the pair's
frontier). But **the leg-native pair {ssf ∈ [-400,-25), dlv < -3} beats
the incumbent pair at BOTH levels (+0.066 mc=0 / +0.051 mc=1) for -8.6%
trips, and beats the triple stack on trip-efficiency at mc=1 (2.268 @
3,585 vs 2.279 @ 3,287 — same PF class, +364 trips)** — the iso-PF house
test favors it over the triple. mc=1 years: 3.40/3.06/2.07/1.78/2.05/
2.12/1.74 — worst-year 1.58 → 1.78, gives back 2024 (2.27 → 2.05) and
2026. The falling-knife family would then be fully anchored: r (shape) ×
slope (speed) × dlv (stretch), one 20m-clock distance retired. Decision
= user's.

## S41e — ⭐⭐ SPEC v2.2 BAKED: THE LEG-NATIVE PAIR + the arming-drop family (2026-08-02)

**SPEC v2.2 = v2.1 with {dvw < -5%, d20m-high < -10%} REPLACED by
{ssf ∈ [-375, -25) bp/min, dlv < -3%}** (user; floor tightened -400 →
-375 off the fine table — [-400,-375) = 23 @ 0.63). Engine surgery:
`MaxDistVw20m`, `DistHiLo`, `DistHiHi` + their gates DELETED;
`SsfLoBpm`/`SsfHiBpm` (`--ssf-lo`/`--ssf-hi`) and `MaxDistLegVwap`
(`--max-dist-leg-vwap`) added — same slope/leg-cum sources as the
recorded `ols_slope_since_flow` and `dv_leg`/`vol_leg` (SQL twins).
The canonical base CLI swaps `--max-dist-vw20m 0 --dist-hi-lo -Infinity
--dist-hi-hi 0` for `--ssf-lo -Infinity --ssf-hi 0 --max-dist-leg-vwap 0`.

**NEW RECORD-ONLY FAMILY (user, S41e): the ARMING DROP** — captured at
the leg's first-low bar (chan_hi decays as bars roll out, so read-at-
signal would be wrong): `d_hi_flow` = first low / prior 20m high - 1;
`ols_slope_hi_flow` / `ols_r_hi_flow` = the drop segment's OLS
(olsSinceHigh at the arming bar spans exactly high -> first low — a free
snapshot). Reset with the leg.

**base_v9 = THE base** (2,217,950, zero-diff vs v8 + 3 new cols).
**`v22_reference/` = THE reference — GRAND PARITY ✓: engine 38,760 = SQL
38,760, zero diff both directions.** New cols sane: d_hi_flow populated
on all trips ∈ [-0.688, -0.0005]; slope/r nan only on 133 sub-3-bar
drops; ols_r_hi_flow ∈ [-0.996, +0.701] (positive r = rise-then-plunge
drop segments — study material).

**Book (v2.1 → v2.2): 26,135 @ 2.501 → 23,857 @ 2.569 / 74.7%**, years
4.71 / 3.75 / 1.47 / 1.84 / 2.64 / 1.99 / 2.72 (worst yr 2022 1.43 →
1.47; 2023 1.72 → 1.84; 2026 2.45 → 2.72; give-back 2025 2.00 → 1.99 ≈
flat and 2021). **mc=1: 3,582 @ 2.268** (matches the base_v8 forecast
exactly; ladder: 2.004 → 2.070 → 2.106 → 2.104 → 2.175 → 2.184 → 2.211
→ 2.218 → 2.217 → **2.268** — the biggest single-step mc=1 gain since
v1.7). ⏭ re-check the overlay roster on v2.2 (gap_1200<5, pco>=+2,
rp_vol band, ht=1, esf star band, nh12 corner) + the arming-drop family
study + pyramid re-cut.

## S41f — the arming-drop study + overlay roster on v2.2 + pyramid re-cut (2026-08-02)

**Base note (user question, answered from code):** the base book = volat_20m
>= 40bp (MinVolat20m default ON — never a CLI off-flag in the canonical base
run; NO volat max, MaxVolat20m = +Infinity always) × strict new 1200-bar low
× channelWarm × 09:45-15:00 × barnum >= 22 × mc=0 sampler. The S39j volat
PREPASS (max_slot_absr_bp trim) is still present but DERIVED from the live
MinVolat20m — it self-removes if the floor is ever set to 0. Every residual-
universe study is therefore conditioned on the 40bp floor: volat = part of
the SIGNAL definition (v1.1 decision), not a researched gate.

**1. THE ARMING DROP (d_hi_flow, % from 20m high to the leg's first low) on
the v2.2 book (23,857 @ 2.569):**

| d_hi_flow | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -30 | 206 | 13.593 | 84.5 | +4.57 | 4.31 | — | — | — | 15.44 | 26.66 | 16.83 |
| [-30,-28) | 86 | 6.74 | 88.4 | +4.23 | 6.02 | — | — | — | — | 30.58 | — |
| [-28,-26) | 197 | 6.319 | 75.1 | +1.84 | 13.19 | — | 0.08 | 3.44 | 4.9 | 8.82 | — |
| [-26,-24) | 254 | 8.741 | 80.3 | +3.28 | 3.66 | — | — | — | 5.63 | 114.8 | 1.88 |
| [-24,-22) | 315 | 1.346 | 62.2 | +2.43 | 18.03 | 2.89 | — | — | 0.87 | 0.54 | 9.84 |
| [-22,-20) | 397 | 3.555 | 74.8 | +2.59 | 3.44 | 1.05 | — | 1.76 | 10.67 | 1.84 | 6.77 |
| [-20,-18) | 729 | 3.03 | 75.3 | +2.9 | 190.8 | 1.27 | 1.68 | 2.91 | 1.53 | 15.01 | 0.32 |
| [-18,-16) | 1,108 | 2.99 | 79.2 | +3.16 | 15.02 | 5.3 | 0.35 | 0.57 | 5.51 | 1.88 | 17.11 |
| [-16,-14) | 1,824 | 2.564 | 75.2 | +2.64 | 3.02 | 5.04 | 0.52 | 3.01 | 4.26 | 3.19 | 3.71 |
| [-14,-12) | 2,127 | 2.343 | 78.0 | +2.5 | 11.41 | 4.06 | 0.68 | 1.03 | 4.08 | 1.83 | 2.3 |
| [-12,-10) | 3,270 | 2.528 | 72.8 | +2.26 | 3.56 | 5.14 | 5.21 | 1.25 | 1.79 | 4.24 | 1.57 |
| [-10,-8) | 4,293 | 2.06 | 72.3 | +1.94 | 3.76 | 5.37 | 1.92 | 1.68 | 2.23 | 1.24 | 1.72 |
| [-8,-6) | 5,127 | 2.374 | 74.4 | +2.06 | 5.62 | 3.48 | 2.38 | 2.71 | 1.82 | 1.16 | 3.92 |
| [-6,-4) | 3,259 | 3.534 | 76.1 | +1.74 | 3.6 | 3.24 | 2.13 | 5.24 | 5.38 | 2.87 | 4.41 |
| [-4,-2) | 553 | 1.809 | 73.8 | +1.59 | 1.77 | 1.7 | 5.87 | 0.32 | 7.63 | 3.04 | 3.28 |
| [-2,0) | 112 | 4.677 | 76.8 | +2.16 | 14.7 | 10.75 | — | — | 4.46 | 3.3 | 1.14 |

**⭐ dhf < -24 (the violent arming crash) = 743 @ 9.10 / 81.0** — census 97
tkds / 92 syms (~15 events/yr, A++-corner class: SIZE it, don't book it);
2022 = 0.94 the one wart; dhf < -16 = 3,292 @ 3.31 on 443 tkds = the
tradable-scale version. Corr: dhf↔|eff20| 0.013, ↔ssf 0.238, ↔dlv 0.429 —
mostly NEW information (the drop INTO the leg vs the leg itself).
slope_hi_flow = FLAT (~2.5 everywhere; the drop's SIZE matters, not its
speed; [-350,-300) = 12.0 is a 2026-602-carried anecdote). r_hi_flow mild:
[0,0.1) rise-then-plunge = 6.12 on 61 tkds (2020-carried), >= 0.2 = 1.60;
the straight-line DROP is fine (unlike the straight-line leg, rsf) — the
knife rule applies AFTER the first low, not before it.

**2. OVERLAY ROSTER RE-CHECK on the v2.2 book — every seat SURVIVES, most
strengthen:**

| overlay | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| BOOK (v2.2) | 23,857 | 2.569 | 74.7 | 4.71 | 3.75 | 1.46 | 1.84 | 2.64 | 1.99 | 2.72 |
| gap_1200 < 5 | 2,523 | 5.837 | 82.3 | 12.52 | 6.64 | 5.99 | 4.24 | 5.73 | 3.63 | 7.51 |
| pco >= +2% | 6,682 | 3.38 | 77.4 | 7.83 | 5.53 | 1.88 | 1.9 | 2.65 | 2.64 | 6.88 |
| gap_60 < 4 | 9,277 | 3.64 | 78.3 | 8.54 | 5.12 | 3.18 | 2.83 | 3.12 | 2.42 | 4.73 |
| O2×O3 (pco × gap_60) | 3,855 | 4.507 | 80.1 | 10.92 | 4.57 | 2.43 | 2.77 | 3.83 | 3.16 | 15.39 |
| rp_vol [0.8,1.0) | 2,565 | 4.429 | 79.1 | 11.59 | 5.78 | 1.51 | 1.84 | 3.04 | 9.56 | 16.09 |
| ht = 1 | 2,694 | 4.619 | 78.2 | 4.63 | 6.34 | 2.48 | 1.07 | 9.79 | 5.09 | 5.28 |
| esf [-0.6,-0.5) | 2,127 | 3.926 | 78.5 | 6.09 | 4.66 | 3.43 | 2.05 | 5.25 | 3.6 | 3.14 |
| ns12 >= 650 | 1,960 | 5.196 | 78.5 | 9.04 | 4.62 | 4.96 | 3.66 | 4.64 | 3.87 | 7.38 |
| nh12<80 × gap12>=700 | 440 | 9.508 | 80.2 | 5.08 | 3.56 | 5.77 | 5.05 | 72.55 | 12.01 | 13.5 |
| TORRENT dv12>=30M | 3,536 | 4.026 | 78.9 | 9.06 | 5.19 | 1.41 | 0.7 | 4.63 | 2.82 | 8.88 |
| CORNER 30M × 30M | 2,871 | 3.987 | 78.7 | 12.6 | 5.41 | 1.22 | 0.71 | 3.68 | 2.88 | 10.15 |
| A++ corner × dsv>=-3 | 413 | 9.702 | 88.4 | 42.74 | 3.11 | 0.26 | 19.41 | 81.86 | 1392 | — |
| ⭐ NEW: dhf < -24 | 743 | 9.1 | 81.0 | 5.19 | 15.37 | 0.94 | 9.65 | 8.68 | 32.03 | 10.08 |

gap_1200<5 = still THE lens (5.84, ALL years >= 3.6); ns12/ht/rp_vol/esf/
O2×O3 all hold or improve. Warts: the torrent family keeps its 2023 hole
(0.70) and the A++ cell (now 413 trips, ~30% smaller under the leg-native
pair) shows 2022 = 0.26 — the dv-defined A++ is fraying at v2.2; the
GAP-defined tiers (gap_1200, ns12, nh12-corner) are the year-robust
replacements.

**3. PYRAMID RE-CUT (mc=0 tier / mc=1 one-slot replay):**

| tier | mc=0 n @ PF | mc=1 n @ PF |
|---|---|---|
| book | 23,857 @ 2.569 | 3,582 @ 2.268 |
| A: gap_60<4 / pco>=2 | 9,277 @ 3.64 / 6,682 @ 3.38 | — |
| A+: O2×O3 / rp_vol / ht=1 | 3,855 @ 4.51 / 2,565 @ 4.43 / 2,694 @ 4.62 | O2×O3 643 @ 3.575 |
| A+: ns12>=650 / gap_1200<5 | 1,960 @ 5.20 / 2,523 @ 5.84 | 314 @ 3.740 / 383 @ 4.086 |
| A++: dhf<-24 / nh12-corner / corner×dsv | 743 @ 9.10 / 440 @ 9.51 / 413 @ 9.70 | dhf 104 @ 4.610 / A++ 68 @ 5.053 |

**Pyramid: 2.6 → 3.4-3.6 → 4.4-4.6 → 5.2-5.8 → ~9-10 (mc=1: 2.27 → 3.6 →
3.7-4.1 → 4.6-5.1).** The sizing ladder survives v2.2 intact with the
arming drop joining at the top tier; liquidity still scales WITH edge
(gap_1200<5 ≈ 10× book liquidity — the S40p finding carries).

## S41g — gap_60 < 4 as THE DEFAULT (user call) + calendar consistency (2026-08-02)

**User: bake gap_60 < 4 into the spec — "it's not worth our time to trade
illiquid stocks"; the other overlays become SIZING LEVERS on top.** The
roster recomputed INSIDE gap_60 < 4 (9,277 @ 3.64 / 1,440 tkds; the
removed 61% of trips = 2.104):

| overlay x gap_60<4 | n | tkds | PF | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| gap60-book | 9,277 | 1,440 | 3.64 | 8.54 | 5.12 | 3.18 | 2.83 | 3.12 | 2.42 | 4.73 |
| x pco >= +2% | 3,855 | 571 | 4.507 | 10.92 | 4.57 | 2.43 | 2.77 | 3.83 | 3.16 | 15.39 |
| x gap_1200 < 5 | 2,523 | 359 | 5.837 | 12.52 | 6.64 | 5.99 | 4.24 | 5.73 | 3.63 | 7.51 |
| ⭐ x rp_vol [0.8,1.0) | 934 | 197 | 9.612 | 9.58 | 16.69 | 21.65 | 19.11 | 1.89 | 19.41 | 15.19 |
| x ht = 1 | 1,518 | 209 | 6.369 | 14.26 | 11.74 | 5.7 | 1.65 | 11.39 | 4.27 | 11.37 |
| x esf [-0.6,-0.5) | 884 | 223 | 5.187 | 7.11 | 5.24 | 4.94 | 4.55 | 7.31 | 3.8 | 4.77 |
| x ns12 >= 650 | 1,935 | 296 | 5.224 | 8.97 | 4.71 | 4.33 | 4.18 | 4.64 | 3.87 | 7.38 |
| x nh12<80 x gap12>=700 | 0 | 0 | — | | | | | | | |
| x TORRENT dv12>=30M | 3,420 | 519 | 4.007 | 8.7 | 5.46 | 1.42 | 0.68 | 4.55 | 2.82 | 8.88 |
| x A++ corner x dsv>=-3 | 411 | 64 | 9.68 | 42.74 | 3.11 | 0.22 | 19.41 | 81.86 | 1392 | — |
| x dhf < -24 | 462 | 62 | 11.718 | 5.11 | 9.28 | 0.08 | 9.49 | 23.4 | 31.59 | 9.35 |
| x dhf < -16 | 1,990 | 279 | 5.654 | 10.63 | 1.63 | 5.35 | 14.22 | 4.45 | 4.25 | 19.03 |

**STRUCTURE:** (a) the liquidity family is NESTED — gap_1200<5 (identical
2,523) and ns12>=650 (1,935/1,960) are subsets of gap_60<4: one axis,
inner tiers. (b) the nh12 sparse-burst corner = EMPTY on continuous tape
— dies by design (and its slippage stress case leaves the production
queue). (c) ⭐ **rp_vol x gap60 = 934 @ 9.61 / 197 tkds = THE new star
stack** (the steady-tape flush was diluted by illiquid tape; double-digit
5 of 7 years, 2024 = 1.89 the wart); mc=1 = 202 @ 5.309. esf star x gap60
= 884 @ 5.19 (all years >= 3.8), mc=1 221 @ 4.119. (d) the dv family
keeps its 2022/23 warts on clean tape — fraying is not a liquidity
artifact. **gap60-book mc=1 = 1,564 @ 2.891 / 75.9 (worst yr 2023 1.78)**
vs full-book 3,582 @ 2.268: −56% slot-trips for +0.62 PF.

**CALENDAR CONSISTENCY (gap60 mc=1 book, equal-sized trips, pre-cost, %
points of one slot):**

| period | n | % positive | median | worst | best |
|---|---|---|---|---|---|
| months | 79 | **91.1** | +26.4 | **-16.6** (2023-03) | +101.1 |
| weeks | 316 | 84.5 | +6.2 | -33.4 (w/o 2025-12-01) | — |
| active days | 899 | 77.9 | +2.56 | -38.5 (2025-12-05) | — |

Month distribution: 2 months < −10 (2023-03 −16.6, 2023-11 −16.0), 5 in
[−10,0), 10 in [0,10), 62 of 79 >= +10. Yearly points: 545 / 344 / 148 /
127 / 458 / 462 / 313 — every year solidly positive (2022-23 thin, 97/142
trips, but green). Worst single trips = the no-stop tail: JFBR 2025-12-05
−38.5, VHC 2023-03-30 −27.7, CADL −21.7, NCT −21.3, NMHI −21.2 (~1/yr at
the −20..−38 scale; stops remain OFF per S24 — the tail is the cost of
the 91% months). The anti-lottery: unlike V2 (two months = half the
P&L), the median month +26.4 IS the system.

## S41h — the ILLIQUID BOOK (gap_60 >= 4) profiled; v2.3 bake CANCELLED (user) (2026-08-02)

**User reversal: gap_60 < 4 will NOT be baked — instead, profile the
complement as its own book.**

**The illiquid book = 14,580 trips / 2,704 tkds @ 2.104 / 72.5%** — years
3.67 / 3.23 / 1.25 / 1.55 / 2.37 / 1.68 / 1.57: positive every year but
DECAYING (2020-21 carried; 2022+ hugs 1.3-1.7). **mc=1 = 2,539 @ 1.963**
(years 2.89 / 2.87 / 1.85 / 1.63 / 1.80 / 1.81 / **1.25-in-2026**) — the
one-slot illiquid book is drifting toward break-even in the modern years,
PRE-cost, on the tape where real fills are worst.

**The gap_60 gradient over the whole v2.2 book is NOT monotone:**

| gap_60 | n | PF | win% | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| [0,2) | 7,313 | 3.672 | 78.8 | 2.75 | 2.82 | 2.52 | 5.25 |
| [2,4) | 1,964 | 3.504 | 76.1 | 5.56 | 2.84 | 1.93 | 3.19 |
| [4,6) | 1,481 | 2.046 | 73.9 | 0.7 | 1.94 | 1.15 | 1.84 |
| [6,8) | 1,269 | 1.862 | 71.4 | 0.51 | 1.38 | 1.3 | 1.25 |
| [8,10) | 1,012 | 2.107 | 71.6 | 1.89 | 2.3 | 1.53 | 2.87 |
| [10,15) | 2,186 | 1.772 | 68.3 | 0.8 | 1.45 | 1.06 | 1.53 |
| [15,20) | 1,687 | 2.099 | 71.3 | 1.57 | 1.44 | 1.52 | 1.02 |
| [20,30) | 2,992 | 2.371 | 73.8 | 1.65 | 1.72 | 2.04 | 2.2 |
| [30,40) | 2,216 | 2.188 | 74.0 | 2.3 | 1.64 | 2.47 | 2.08 |
| >= 40 | 1,737 | 2.27 | 74.8 | 1.59 | 0.98 | 1.23 | 3.39 |

The cliff sits exactly at 4 ([2,4) 3.50 -> [4,6) 2.05); the [4,15) zone
= THE SWAMP (1.77-2.11, worst [10,15)); the deep-sparse [20,40+) zone
partially recovers (2.2-2.4) — that's the burst world, structurally
different tape, not a liquidity gradient.

**Overlays DEFLATE on illiquid tape:** pco 2.39, rp_vol 3.17 (2022-23 ~
1.2), ht=1 3.19 (2023 0.76), esf 3.24; **dhf < -16 DIES (1.995; 2022-23
= 0.52 / 0.68)** — the violent-crash edge needs a continuous tape to
revert on. Dead-tape avoid confirmed: 1.052 on 191. The ONE jewel that
lives only here: **nh12<80 x gap12>=700 = 440 @ 9.508 / 116 tkds** (the
sparse-burst corner, definitionally excluded from clean tape).

**READING: liquid and illiquid are two different systems sharing a spec.**
Clean tape (63% of tkds): 3.64 / mc=1 2.89, every overlay amplifies.
Illiquid tape: 2.10 / mc=1 1.96 decaying, every overlay deflates except
the burst corner. The gap_60 axis stays POST-HOC (user), but the sizing
pyramid should live almost entirely on the clean side.

## S41i — pco vs d0945 (the first-15m vwap anchor): d0945 WINS standalone, converges under gap60 (2026-08-02)

**User: is distance from the FIRST-15M VWAP better than pco (green-from-
open)?** Engine: `vol_0945_tape` added (frozen at 09:45 like its dv twin;
record-only) — **base_v10 = THE base, `v22_vwap15/` = THE working parquet**
(zero-diff parity vs v22_reference, 38,760 exact). `d0945 =
signal_vwap/(dv_0945_tape/vol_0945_tape) - 1`. **corr(pco, d0945) = 0.924**
— same idea, different anchor (participation-weighted vs the open print).

d0945 axis on the v2.2 book: deep-below plateau ~2.0-2.7; **hover AT the
anchor [-2,+2) = 1.7-2.3 with 2023 = 0.44 (the weak zone)**; above-anchor
HUMP [4,12) = 4.2 / 4.0 / 9.1 / 5.8; >= 20 = 4.24. (Full tables above in
the run; pco's axis is the noisier cousin.)

| config | n | PF | 2022 | 2023 | 2025 | 2026 | mc=1 |
|---|---|---|---|---|---|---|---|
| pco >= 2 (incumbent O2) | 6,682 | 3.380 | 1.88 | 1.9 | 2.64 | 6.88 | 1,131 @ 2.596 |
| d0945 >= 2 | 4,475 | 3.903 | 2.67 | 2.3 | 2.84 | 14.1 | 754 @ 2.835 |
| d0945 >= 4 | 4,093 | 4.058 | 2.75 | 2.35 | 2.97 | 14.23 | — |
| pco>=2 AND NOT d0945>=2 | 2,484 | 2.852 | 1.42 | 1.37 | 2.35 | 2.89 | — |
| d0945>=2 AND NOT pco>=2 | 277 | 9.682 | 11.61 | — | 2.43 | 9.91 | — |
| O2xO3 incumbent (pco x g60) | 3,855 | 4.507 | | | | | — |
| d0945>=2 x g60 | 2,879 | 4.413 | | | | | 492 @ 3.477 |

**READING:** (a) **standalone, the vwap anchor strictly dominates**: what
pco keeps and d0945 rejects (green vs the open print but BELOW the 15m
vwap) = 2.85 with 1.4-ish 2022-23; what d0945 adds (red vs the print but
ABOVE the vwap) = 277 @ 9.68. The information is in the participation-
weighted anchor, not the first print. (b) **under gap_60 < 4 the two
CONVERGE** (4.41 vs 4.51 — incumbent even slightly ahead on more trips):
on continuous tape the open print ≈ the 15m vwap; the anchor only matters
where the print is noise — exactly the illiquid tape. **ROSTER: d0945 >= 2
replaces pco >= 2 as the standalone day-structure overlay** (pco stays
recorded); inside the gap60 stack either form works.

## S41j — the d0945 cutoff sweep + d0945 × pco grid (2026-08-02)

**User: does d0945 have to be >= 2?** Cumulative sweep on the v2.2 book:

| d0945 >= x | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| -6 | 7,444 | 2.929 | 77.3 | 9.08 | 4.59 | 1.49 | 1.5 | 2.35 | 2.03 | 8.2 |
| -4 | 6,500 | 3.092 | 77.9 | 9.1 | 5.45 | 1.87 | 1.45 | 2.39 | 2.12 | 10.74 |
| -2 | 5,759 | 3.329 | 78.5 | 10.24 | 5.68 | 1.91 | 1.65 | 2.44 | 2.37 | 12.58 |
| 0 | 5,042 | 3.524 | 78.5 | 9.63 | 5.34 | 2.13 | 2.02 | 2.42 | 2.58 | 12.05 |
| 2 | 4,475 | 3.903 | 79.0 | 8.91 | 4.92 | 2.67 | 2.3 | 2.97 | 2.84 | 14.1 |
| ⭐ 4 | 4,093 | 4.058 | 79.3 | 8.68 | 5.02 | 2.75 | 2.35 | 3.27 | 2.97 | 14.23 |
| 6 | 3,674 | 4.042 | 78.9 | 7.86 | 4.04 | 2.67 | 2.58 | 3.27 | 3.0 | 18.95 |
| 8 | 3,268 | 4.045 | 78.4 | 6.96 | 3.98 | 2.61 | 3.21 | 3.29 | 2.68 | 19.6 |
| 10 | 2,888 | 3.77 | 77.3 | 6.01 | 4.06 | 2.62 | 2.97 | 3.16 | 2.39 | 21.5 |
| 12 | 2,621 | 3.643 | 76.5 | 5.69 | 3.66 | 2.75 | 2.92 | 2.97 | 2.32 | 21.44 |

**PF climbs monotonically to a PLATEAU at >= 4 (4.06, flat through >= 8),
rolls off at >= 10. ⭐ d0945 >= 4 = THE overlay cutoff** (4,093 @ 4.058,
every year >= 2.35; the [2,4) increment = 382 @ ~2.6 marginal).

**The CUMULATIVE d0945 × pco grid (n @ PF for d0945 >= row AND pco >=
col; band-grid form rejected as unreadable — user):**

| d0945 \ pco | >=-6 | >=-2 | >=0 | >=2 | >=4 | >=6 | >=8 | >=10 | >=12 |
|---|---|---|---|---|---|---|---|---|---|
| >=-6 | 7034 @ 3.07 | 6383 @ 3.17 | 5985 @ 3.18 | 5608 @ 3.31 | 5135 @ 3.43 | 4691 @ 3.71 | 4314 @ 3.79 | 4005 @ 3.79 | 3633 @ 3.97 |
| >=-4 | 6299 @ 3.15 | 5914 @ 3.15 | 5639 @ 3.15 | 5333 @ 3.3 | 4907 @ 3.43 | 4531 @ 3.7 | 4202 @ 3.76 | 3922 @ 3.75 | 3564 @ 3.93 |
| >=-2 | 5667 @ 3.29 | 5416 @ 3.31 | 5243 @ 3.31 | 5022 @ 3.39 | 4686 @ 3.43 | 4359 @ 3.66 | 4050 @ 3.7 | 3794 @ 3.72 | 3476 @ 3.89 |
| >=0 | 4992 @ 3.49 | 4842 @ 3.45 | 4735 @ 3.44 | 4599 @ 3.43 | 4361 @ 3.49 | 4086 @ 3.67 | 3825 @ 3.65 | 3622 @ 3.62 | 3348 @ 3.83 |
| >=2 | 4443 @ 3.86 | 4358 @ 3.78 | 4295 @ 3.77 | 4198 @ 3.75 | 4023 @ 3.78 | 3841 @ 4.0 | 3638 @ 3.96 | 3453 @ 3.87 | 3224 @ 3.98 |
| ⭐ >=4 | 4069 @ 4.03 | 4015 @ 3.96 | 3977 @ 3.95 | 3902 @ 3.91 | 3772 @ 3.98 | 3644 @ 4.15 | 3486 @ 4.12 | 3335 @ 4.03 | 3141 @ 4.01 |
| >=6 | 3655 @ 4.02 | 3642 @ 4.0 | 3623 @ 4.0 | 3569 @ 3.97 | 3499 @ 4.03 | 3408 @ 4.11 | 3293 @ 4.11 | 3183 @ 4.02 | 3034 @ 3.98 |
| >=8 | 3249 @ 4.02 | 3240 @ 4.0 | 3230 @ 4.01 | 3203 @ 4.01 | 3174 @ 4.02 | 3121 @ 3.97 | 3054 @ 3.95 | 2971 @ 3.88 | 2871 @ 3.86 |
| >=10 | 2874 @ 3.75 | 2866 @ 3.74 | 2858 @ 3.75 | 2837 @ 3.75 | 2821 @ 3.78 | 2797 @ 3.76 | 2773 @ 3.75 | 2731 @ 3.71 | 2670 @ 3.76 |
| >=12 | 2609 @ 3.62 | 2606 @ 3.62 | 2598 @ 3.63 | 2584 @ 3.65 | 2573 @ 3.66 | 2562 @ 3.66 | 2548 @ 3.66 | 2520 @ 3.63 | 2481 @ 3.65 |

**READING:** the d0945 axis does ALL the work. Down the leftmost column
(pco unconstrained) PF climbs 3.07 → 4.03 at d>=4; **across the d>=4 row
the pco axis is FLAT (4.03 → 4.01)** — once d0945 >= 4 is set, pco adds
NOTHING. Conversely pco only "works" on shallow-d rows (3.07 → 3.97
along d>=-6) because it proxies d0945 there. The plateau: d ∈ [4,8] ×
any pco ≈ 4.0-4.15. **d0945 >= 4 alone = the whole day-structure edge;
pco is retired as a separate axis.**

**S41j addendum — the same pair INSIDE gap_60 < 4 (user):** universe 9,277
@ 3.64.

| d0945 >= x (g60<4) | n | PF | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| -6 | 3,911 | 4.02 | 11.15 | 6.46 | 2.33 | 2.52 | 3.76 | 2.21 | 14.61 |
| -4 | 3,623 | 4.153 | 12.12 | 6.54 | 2.08 | 2.54 | 3.78 | 2.39 | 14.9 |
| ⭐ -2 | 3,392 | 4.451 | 11.78 | 6.17 | 1.98 | 3.05 | 3.67 | 2.78 | 14.99 |
| 0 | 3,104 | 4.38 | 11.17 | 5.08 | 1.72 | 3.12 | 3.62 | 2.91 | 14.46 |
| 2 | 2,879 | 4.413 | 10.13 | 4.56 | 1.72 | 3.89 | 3.79 | 2.9 | 15.21 |
| 4 | 2,716 | 4.342 | 10.24 | 4.27 | 1.72 | 4.13 | 3.71 | 2.81 | 14.9 |
| 6 | 2,522 | 4.276 | 9.35 | 3.47 | 1.68 | 4.02 | 3.84 | 2.84 | 19.48 |
| 8 | 2,295 | 4.057 | 8.23 | 3.79 | 1.59 | 3.95 | 3.54 | 2.52 | 20.42 |

**The cumulative grid inside gap_60 < 4 (n @ PF, d0945 >= row AND pco >=
col):**

| d0945 \ pco | >=-6 | >=-2 | >=0 | >=2 | >=4 | >=6 | >=8 | >=10 | >=12 |
|---|---|---|---|---|---|---|---|---|---|
| >=-6 | 3807 @ 4.47 | 3619 @ 4.39 | 3539 @ 4.32 | 3380 @ 4.34 | 3220 @ 4.37 | 3017 @ 4.47 | 2840 @ 4.59 | 2697 @ 4.47 | 2510 @ 4.46 |
| >=-4 | 3583 @ 4.36 | 3478 @ 4.29 | 3412 @ 4.23 | 3273 @ 4.25 | 3125 @ 4.3 | 2955 @ 4.4 | 2796 @ 4.49 | 2656 @ 4.37 | 2476 @ 4.38 |
| >=-2 | 3372 @ 4.42 | 3288 @ 4.37 | 3243 @ 4.34 | 3135 @ 4.26 | 3014 @ 4.24 | 2867 @ 4.32 | 2716 @ 4.38 | 2592 @ 4.29 | 2424 @ 4.28 |
| >=0 | 3094 @ 4.37 | 3047 @ 4.32 | 3020 @ 4.29 | 2954 @ 4.28 | 2862 @ 4.26 | 2742 @ 4.23 | 2611 @ 4.24 | 2510 @ 4.18 | 2358 @ 4.18 |
| >=2 | 2873 @ 4.4 | 2840 @ 4.35 | 2819 @ 4.33 | 2765 @ 4.31 | 2697 @ 4.28 | 2622 @ 4.31 | 2526 @ 4.3 | 2434 @ 4.22 | 2297 @ 4.27 |
| >=4 | 2711 @ 4.34 | 2691 @ 4.29 | 2674 @ 4.29 | 2631 @ 4.26 | 2572 @ 4.23 | 2512 @ 4.25 | 2442 @ 4.25 | 2372 @ 4.2 | 2254 @ 4.27 |
| >=6 | 2521 @ 4.28 | 2515 @ 4.27 | 2506 @ 4.28 | 2474 @ 4.27 | 2443 @ 4.29 | 2393 @ 4.24 | 2340 @ 4.23 | 2282 @ 4.16 | 2195 @ 4.18 |
| >=8 | 2294 @ 4.06 | 2290 @ 4.05 | 2282 @ 4.07 | 2266 @ 4.09 | 2253 @ 4.13 | 2219 @ 4.11 | 2190 @ 4.11 | 2145 @ 4.05 | 2082 @ 4.11 |
| >=10 | 2074 @ 3.98 | 2070 @ 3.97 | 2062 @ 3.99 | 2048 @ 4.01 | 2040 @ 4.06 | 2026 @ 4.07 | 2024 @ 4.06 | 2004 @ 4.04 | 1957 @ 4.14 |
| >=12 | 1915 @ 3.92 | 1915 @ 3.92 | 1907 @ 3.93 | 1895 @ 3.96 | 1891 @ 3.99 | 1883 @ 4.01 | 1883 @ 4.01 | 1871 @ 3.98 | 1841 @ 4.02 |

The whole plane is FLAT at 3.9-4.6 — tightening either axis on clean
tape mostly sheds trips; the loosest corner (>=-6 × >=-6 = 3,807 @ 4.47)
is as good as anything.

**READING:** on clean tape the d0945 axis FLATTENS — the sweep sits at
4.15-4.45 across the whole [-4,+6] range (peak >= -2 = 4.451), the
overlay is cutoff-insensitive and worth only ~+0.7-0.8 PF over the 3.64
clean-tape base (vs +1.5 standalone), and 2022 WEAKENS with tighter cuts
(1.98 -> 1.72). The S41j plateau-at->=4 is mostly an illiquid-tape
phenomenon; under gap60 the day-structure information is largely absorbed
by tape continuity. Roster: **standalone d0945 >= 4; inside the gap60
stack use the loose >= -2 form (or skip the axis entirely)**.

**S41j addendum 2 — steep slope_since_flow as an overlay (user):** the
spec band is [-375,-25); the steep half as a sizing lens on the v2.2 book:

| overlay | n | tkds | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ssf < -50 | 11,974 | 1,839 | 2.763 | 76.0 | 5.96 | 4.31 | 1.29 | 2.02 | 2.89 | 2.24 | 2.49 |
| ⭐ ssf < -75 | 5,393 | 908 | 3.349 | 76.7 | 8.12 | 3.14 | 1.68 | 1.74 | 3.74 | 3.19 | 2.7 |
| ssf < -100 | 2,635 | 469 | 3.842 | 77.5 | 7.06 | 2.21 | 1.26 | 1.77 | 9.66 | 3.4 | 4.26 |
| ssf < -125 | 1,236 | 253 | 3.671 | 77.8 | 6.79 | 1.38 | 0.79 | 1.77 | 15.49 | 3.43 | 5.28 |
| g60 × ssf < -75 | 2,684 | 453 | 4.604 | 80.3 | 20.03 | 5.73 | 1.29 | 2.71 | 4.06 | 4.36 | 3.84 |
| g60 × ssf < -100 | 1,434 | 261 | 5.595 | 82.0 | 23.37 | 12.42 | 0.54 | 3.0 | 8.52 | 4.34 | 5.35 |

**ssf < -75 = the year-robust form (all years >= 1.68) — joins the A-
overlay roster**; steeper cuts raise PF but 2022 decays monotonically
(1.68 -> 1.26 -> 0.79; steep flushes in the bear regime keep falling) —
same for the g60 stacks (x < -100 = 5.6 but 2022 0.54). The steep-flush
lens SIZES, the -75 line is where it stays all-weather.

## S41k — flush SPEED as an overlay, split by tape (user) (2026-08-02)

**User: add the 1m flush speed (the v1.1 gate axis, spec < -2%) to the
overlay roster; break down on clean (gap_60 < 4) and illiquid tape.**

Clean tape (base 3.64): the axis is FLAT-mild through [-8,-2) (2.9-4.3,
[-5,-4) dip 2.88) — cumulative: < -5 = 3.98, < -6 = **4.273 (1,387 @ 311
tkds)**, < -8 = 4.41, < -10 = 6.62 (173/46 tkds), < -12 = 13.7 (69).
Illiquid tape (base 2.10): the mid-speeds are a SWAMP — [-8,-7) = 1.60,
[-4,-3) = 1.95, worse than the illiquid book itself — but the violent
extreme survives: **< -10 = 9.179 (142/50 tkds)**, < -12 = 18.9 (58).

| overlay | n | tkds | PF | note |
|---|---|---|---|---|
| speed < -6 x g60 | 1,387 | 311 | 4.273 | the tradable A form (clean tape) |
| speed < -8 (all) | 894 | 244 | 3.96 | |
| ⭐ speed < -10 (all) | 315 | 94 | 7.71 | A++-class; works on BOTH tapes (6.62 clean / 9.18 illiquid) |

**READING:** the speed axis is CONVEX on both tapes — moderate speeds add
nothing beyond the spec's -2 gate, the violent 1m collapse (< -10) is
the payload, ~15 events/yr, and it is THE ONE lens that also works on
holey tape (unlike dhf/rp_vol/ht which all deflate there) — a violent
enough 1m flush transcends the liquidity regime. Roster: speed < -10 =
A++ lens (both tapes); speed < -6 x g60 = A form.

## S41l — WHY the >= -6 corner works (100% runner-pullbacks) + pah + THE OVERLAY BOOK (2026-08-02)

**User Q1: why is "both d0945 AND pco >= -6" good** (7,034 @ 3.075 vs
complement 16,823 @ 2.403)? **ANSWER — FLUSH ARITHMETIC MAKES IT THE
RUNNER-PULLBACK SELECTOR:** the spec demands a deep flush (median -14.3%
from the 20m high); if price still sits within 6% of the open anchors
AFTER that flush, the stock must have been UP big before it. Measured:
`pah = day change at the pre-flush 20m high = (1+pco)/(1+d20)-1` —
**the corner is 100.0% green-before-flush, median pah = +33.5%** (the
complement: 59.2% / +2.2, median pco now -13.4 = daylong decliners).
Fading a spike-down in an UP day vs fading another leg of a decline —
the pco>=+2 grammar (S40k), fully generalized. Tape check: corner is
enriched clean (54% vs 33%) but genuinely additive to g60 (corner x g60
= 4.471 vs not-corner x g60 = 3.211); on illiquid tape it adds nothing
(2.071).

**pah as the PRINCIPLED unified lens (computable post-hoc from pco +
chan_hi; replaces the pco/d0945 pair semantically):**

| pah bucket | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| < -10 | 2,934 | 1.991 | 72.5 | 3.84 | 2.36 | 0.72 | 1.85 | 2.07 | 1.65 | 2.64 |
| [-10,-5) | 1,569 | 2.936 | 72.6 | 2.98 | 6.19 | 4.14 | 2.43 | 1.74 | 1.67 | 13.2 |
| [-5,0) | 2,364 | 2.398 | 75.2 | 2.75 | 3.78 | 2.31 | 1.73 | 2.61 | 2.07 | 1.92 |
| [0,10) | 6,699 | 2.325 | 72.9 | 4.69 | 3.01 | 2.16 | 1.9 | 2.26 | 1.84 | 1.33 |
| [10,20) | 3,548 | 2.304 | 76.0 | 2.88 | 5.23 | 0.96 | 1.15 | 6.3 | 2.19 | 1.83 |
| [20,30) | 2,131 | 3.033 | 75.8 | 14.35 | 4.66 | 9.35 | 3.33 | 3.21 | 1.03 | 3.36 |
| [30,40) | 1,321 | 3.094 | 78.0 | 22.18 | 18.14 | 0.56 | 1.99 | 3.29 | 2.52 | 3.5 |
| [40,50) | 865 | 4.37 | 77.1 | 10.37 | 2.7 | 1.7 | 1.83 | 3.87 | 5.29 | 7.79 |
| [50,100) | 1,731 | 3.848 | 80.0 | 8.28 | 2.52 | 1.16 | 2.58 | 2.5 | 5.47 | 16.96 |
| >= 100 | 695 | 3.238 | 72.8 | 6.18 | 1.99 | 45.38 | 1.79 | 2.66 | 2.1 | 23.73 |

Cumulative: pah >= 20 = 6,743 @ 3.406 (g60: 4,129 @ 4.414); >= 30 =
4,612 @ 3.578 (g60 4.637); >= 50 = 2,426 @ 3.641; rolloff >= 75. The
only avoid: pah < -10 = 1.99 (the true daylong decliner, 2022 = 0.72).

**User Q2 — combine pco and d0945? VERDICT: the day-structure family
gets ONE seat.** Tight standalone form: d0945 >= 4 (4,093 @ 4.058, the
S41j plateau). Semantic form: pah >= 20 (runner-pullback, more trips @
3.41). Conjunctions of pco x d0945 add nothing beyond either (S41j grid:
the d>=4 row is flat across pco). Under g60 ALL day-structure forms
flatten (~4.3-4.6 anywhere) — skip the axis there. pco RETIRED.

**User Q3 — ORGANIZING THE OVERLAYS: THE OVERLAY BOOK (proposal).**
Rules: (1) lenses group into FAMILIES of correlated/nested measures —
ONE representative per family may be active at a time; (2) each lens has
a TIER (PF-on-book multiple: A ~3-4x base-1, A+ ~4.5-6, A++ ~9+) = the
SIZING multiplier; (3) each lens is stamped with its TAPE REGIME (clean
/ both / illiquid) — clean-only lenses are void on holey tape; (4)
AVOIDs veto regardless of stack.

| family | lens (representative first) | n | tkds | PF | tier | tape |
|---|---|---|---|---|---|---|
| LIQUIDITY | gap_60 < 4 ⊃ gap_1200 < 5 ⊃ ns12 >= 650 | 9,277 / 2,523 / 1,960 | 1,440 / 359 / 296 | 3.64 / 5.84 / 5.20 | A / A+ / A+ | (defines regime) |
| PARTICIPATION | rp_vol [0.8,1.0) | 2,565 | — | 4.43 (x g60 9.61) | A+ (A++ on clean) | clean |
| DAY STRUCTURE | d0945 >= 4 (alt: pah >= 20; pco retired) | 4,093 | — | 4.06 | A | standalone only (flat under g60) |
| LEG SHAPE | esf [-0.6,-0.5) / ssf < -75 / dhf < -24 / speed < -10 | 2,127 / 5,393 / 743 / 315 | — / 908 / 97 / 94 | 3.93 / 3.35 / 9.10 / 7.71 | A / A / A++ / A++ | clean / clean / clean / BOTH |
| EVENT | ht = 1 | 2,694 | — | 4.62 (x g60 6.37) | A+ | clean |
| AVOID | halt cascade >= 4; dead tape; pah < -10 | — | — | 1.40 / 1.05 / 1.99 | veto | — |

⚠ intra-family members are NOT additive (nested or corr); cross-family
additivity MEASURED only for {g60 x rp_vol, g60 x ht, g60 x esf, g60 x
dhf, corner x g60} — the full cross-family interaction matrix at mc=1 =
the remaining wrap-up work. dv family (torrent/corner/A++ x dsv) on the
WATCH LIST (2022/23 fraying) — not in the book.

**S41l addendum — pah vs the SESSION LOW (user: "is this just a proxy for
being above the session low in g60?"): YES, LARGELY.** Structural fact
first: at a signal (a new 20m low), `breach_lo_sess` is effectively
BINARY — the [1m,10m) bucket is EMPTY (a session low set within the 20m
window would sit above the new 20m low, so the signal bar would breach it
→ 0). Either the flush IS the session low (44.7% of g60 trips) or the
session low predates the leg entirely.

| g60 universe | n | PF | win% |
|---|---|---|---|
| AT session low (breach_lo_sess < 1m) | 4,146 | 3.189 | 75.9 |
| ABOVE session low (>10m / never) | 5,131 | 4.083 | 80.1 |

Confounding with pah: pct-at-session-low by pah band = 85% (pah<0) /
71% ([0,20)) / 9.4% ([20,50)) / 0.2% (>=50) — **pah >= 20 ≈ above-
session-low** (91-99.8% of those trips). Mutual residuals: within
ABOVE, pah adds ~+0.3-0.7 (3.34 → 4.39 across bands); within AT, pah
[20,50) = 5.45 on 217 (small-n inversion); holding pah, the session-low
state adds ~+0.3-0.4. **VERDICT: on clean tape the day-structure family
collapses to the BINARY "the flush is NOT the day's first low" —
above-session-low = 5,131 @ 4.08 vs at-low 3.19 — live-trivial
(breach_lo_sess >= 600 OR -1); pah/d0945/pco = graded refinements of the
same fact, worth ~+0.3-0.5 residual on clean tape.** The runner-pullback
story survives (it IS why above-session-low works) but the simplest form
wins the seat.

## S41m — S11 REPEATED on v2.2 × gap_60 < 4: the 2022 inversion was an ILLIQUID-TAPE artifact (2026-08-02)

**User: repeat S11 (session lows vs higher-low legs) on the latest spec ×
the clean-tape universe.** S11 (v1.1, full book) found: 61% of trips =
new session lows @ 1.99 vs off-low 2.23 — but the off-low premium
COLLAPSED in 2022 (1.04 vs 1.65) → verdict "keep both, cutting session
lows = 2020-fitting".

**On v2.2 × g60:**

| session position | n | % | win | PF | avg% | med% |
|---|---|---|---|---|---|---|
| NEW session low (breach_lo_sess=0) | 4,146 | 44.7 | 75.9 | 3.189 | +1.82 | +2.25 |
| off-session-low (higher-low leg) | 4,817 | 51.9 | 79.9 | 4.017 | +2.11 | +2.47 |
| never broke a session low | 314 | 3.4 | 84.1 | 5.74 | +2.03 | +2.22 |

| by year | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| NEW session low | 8.73 | 4.64 | 3.33 | 2.61 | 2.56 | 2.64 | 2.85 |
| off-session-low | 8.16 | 6.01 | **2.94** | 2.9 | 3.74 | 2.22 | 15.21 |
| never | 21.31 | 2.65 | 23.69 | — | 40.17 | 3.74 | 5.21 |

Full-book reference (v2.2, no gap cut): sess-low 13,432 @ 2.404 (2022
2.01) vs off-low 9,583 @ 2.757 with **2022 = 0.97** — the S11 collapse,
still there.

**READING — S11's bear-year inversion RESOLVED: it was an illiquid-tape
artifact.** On clean tape the higher-low second leg is ALL-WEATHER (worst
year 2.22, 2022 = 2.94 ≈ par with session lows' 3.33) and carries a +0.83
PF premium at 52% of the universe; the 2022 collapse of the off-low
retest lives entirely on holey tape (full-book off-low 2022 = 0.97).
The mix also flipped vs S11 (61% session lows then, 44.7% now — the v2.2
gates already prefer the higher-low structure). The S41l day-structure
seat ("the flush is NOT the day's first low") is therefore REGIME-ROBUST
on clean tape — S11's caution no longer applies there; "never" (open
print held as day low, 314 @ 5.74 / 84.1%) = the purest runner-pullback,
census-thin but consistent.

## S41n — distance from first low (dsf): dlv's twin, compounds with the slope (2026-08-02)

**User: do we have dist-from-first-low? Breakdown + compare with the
slope.** Not directly — `first_low_vwap` baked (record-only, captured in
the arming block). **base_v11 = THE base; `v22_dflow/` = THE working
parquet** (zero-diff parity, 38,760 exact). `dsf = signal_vwap /
first_low_vwap - 1` (< 0 always; the leg's EXTENSION below its first low).

**Correlations: dsf ↔ dlv = 0.854 — dsf IS the leg-stretch dimension the
spec already seats via dlv < -3%** (the leg vwap sits between first low
and now, so the two distances track). dsf ↔ ssf = 0.174 (orthogonal to
speed, confirming the S41d two-axis structure), ↔ dhf 0.321, ↔ leg-age
-0.48.

| dsf bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -20 | 816 | 4.439 | 78.7 | +3.73 | 6.61 | 4.54 | 4.65 | 7.77 | 11.87 | 3.21 | 2.44 |
| [-20,-18) | 672 | 3.247 | 78.9 | +3.21 | 30.72 | 6.96 | 2.48 | 5.04 | 6.62 | 1.05 | 3.59 |
| [-18,-16) | 1,050 | 2.204 | 71.2 | +3.07 | 13.75 | 3.8 | 0.24 | 4.06 | 1.84 | 1.17 | 4.9 |
| [-16,-14) | 1,738 | 2.752 | 77.7 | +2.67 | 3.93 | 9.29 | 0.5 | 1.85 | 3.14 | 2.22 | 6.16 |
| [-14,-12) | 2,766 | 2.673 | 76.4 | +2.45 | 7.73 | 4.72 | 1.05 | 1.21 | 3.64 | 2.06 | 2.71 |
| [-12,-10) | 3,927 | 2.682 | 76.1 | +2.32 | 4.11 | 3.82 | 1.8 | 2.52 | 2.55 | 2.41 | 2.42 |
| [-10,-8) | 5,309 | 2.299 | 73.4 | +2.07 | 3.19 | 3.5 | 1.74 | 1.91 | 2.06 | 1.99 | 1.76 |
| [-8,-6) | 5,214 | 2.578 | 74.5 | +1.87 | 4.93 | 3.68 | 1.52 | 1.75 | 2.05 | 2.21 | 2.32 |
| [-6,-4) | 2,287 | 2.022 | 71.2 | +1.57 | 4.06 | 3.1 | 1.82 | 0.63 | 1.18 | 1.72 | 3.7 |
| [-4,-2) | 78 | 1.628 | 59.0 | +0.79 | 1.58 | 2.84 | 0.76 | 0.29 | 48.48 | 1.28 | 1.6 |

**The dsf × ssf cumulative grid (n @ PF; dsf < row AND ssf < col):**

| dsf< | ssf<-25 | ssf<-50 | ssf<-75 | ssf<-100 | ssf<-125 | ssf<-150 |
|---|---|---|---|---|---|---|
| -1 | 23857 @ 2.57 | 11974 @ 2.76 | 5393 @ 3.35 | 2635 @ 3.84 | 1236 @ 3.67 | 745 @ 2.63 |
| -6 | 21492 @ 2.62 | 11161 @ 2.8 | 5089 @ 3.38 | 2492 @ 3.93 | 1172 @ 3.8 | 701 @ 2.75 |
| -10 | 10969 @ 2.78 | 6316 @ 3.06 | 3052 @ 3.95 | 1538 @ 4.37 | 682 @ 3.97 | 393 @ 2.66 |
| -14 | 4276 @ 2.92 | 2795 @ 2.85 | 1429 @ 4.02 | **813 @ 6.02** | 352 @ 4.78 | 182 @ 3.06 |
| -18 | 1488 @ 3.85 | 1087 @ 3.44 | 618 @ 3.91 | **364 @ 8.25** | 185 @ 5.03 | 104 @ 2.99 |

**READING:** (a) dsf alone = a mild deeper-is-better gradient (< -20 =
4.44 on 179 tkds, ALL years >= 2.44; shallow [-6,-2) weak — the dlv
story restated at 0.854 corr; no new seat alone). (b) **the CONJUNCTION
with the slope is the find: deep × fast = dsf < -14 × ssf < -100 = 813 @
6.02 / 144 tkds (years 10.5 / 2.8 / 0.88 / 3.4 / 24.4 / 5.1 / 7.9);
tighter -18 = 364 @ 8.25 / 69 tkds** — an extended flush that is STILL
falling fast >= 100bp/min = the late-stage capitulation climax; 2022 =
0.88 the wart (bear-year knives). ssf < -150 fades in every row (the
vertical poison shadows the corner). Roster: **{dsf < -14 × ssf < -100}
joins as an A+ conjunction lens (2022-warted)**; dsf standalone = dlv
duplicate, no seat.

## S41o — % of bars above session vwap (pab): family member, not the seat (2026-08-02)

**User idea: % of present bars spent above the running session vwap as a
pah/d0945 substitute.** Engine: `bars_above_svwap` + `bars_present` baked
(both INTs — the ratio computed post-hoc, per user; **base_v12 = THE
base, `v22_bav/` = THE working parquet**, zero-diff 38,760). `pab =
100·bars_above_svwap/bars_present`.

**Correlations: pab ↔ pah 0.665 / d0945 0.716 / above-session-low 0.686 /
dsv 0.58** — squarely in the day-structure family, duplicate of none.

| pab bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,10) | 3,578 | 1.9 | 72.4 | +2.02 | 4.26 | 1.48 | 1.06 | 2.05 | 1.8 | 1.65 | 2.07 |
| [10,20) | 3,644 | 2.596 | 74.8 | +2.16 | 3.88 | 4.74 | 1.8 | 1.48 | 3.36 | 1.6 | 3.05 |
| [20,30) | 3,461 | 2.495 | 74.1 | +2.08 | 3.36 | 3.57 | 2.16 | 2.51 | 2.84 | 2.29 | 1.53 |
| [30,40) | 2,607 | 3.4 | 73.8 | +2.17 | 2.9 | 8.51 | 2.89 | 5.12 | 4.03 | 2.87 | 1.9 |
| [40,50) | 2,159 | 2.059 | 73.6 | +2.08 | 3.06 | 4.7 | 0.64 | 1.45 | 4.74 | 1.92 | 4.22 |
| [50,60) | 1,886 | 2.651 | 72.3 | +2.01 | 9.4 | 2.77 | 1.95 | 2.01 | 1.57 | 2.79 | 3.02 |
| [60,70) | 1,779 | 3.11 | 78.8 | +2.44 | 11.82 | 2.82 | 0.83 | 5.41 | 1.6 | 4.77 | 6.49 |
| [70,80) | 1,980 | 2.493 | 77.7 | +2.21 | 19.68 | 6.57 | 4.13 | 1.67 | 5.73 | 0.83 | 4.82 |
| ⭐ [80,90) | 1,604 | 5.134 | 78.2 | +2.35 | 8.11 | 12.72 | 5.39 | 2.31 | 4.98 | 5.1 | 4.17 |
| [90,100] | 1,159 | 2.416 | 75.4 | +2.28 | 5.41 | 2.39 | 14.93 | 0.56 | 2.08 | 2.43 | 29.19 |

(g60 version: same shape, [80,90) = 9.95 on 933 the standout.)

**The residual test vs the S41l/m binary (g60):** within ABOVE-session-
low, pab bands are FLAT (4.18 / 4.16 / 4.05) — no residual; within
AT-session-low, pab >= 60 = 15.4 on 93 (V-day anecdote). **VERDICT: pab
does NOT displace the binary seat; it's a graded family member on par
with pah/d0945.** The texture worth keeping: **[80,90) = 5.134 (all
years >= 2.31) vs [90,100] = 2.416 — the TESTED-AND-RECOVERED day beats
the UNTESTED day**: >90% above vwap means this flush is likely the day's
FIRST test of the vwap area (fails more, 2023 = 0.56); 80-90% = dipped
before and recovered = proven support. Same grammar as the S41j failed-
reclaim/hover cell.

## S41p — distance from the session low: THE U-SHAPE (retest zone = the trap) (2026-08-02)

**User: test dist-from-session-low.** Engine: raw `sess_low` + `sess_high`
baked (record-only; **base_v13 = THE base, `v22_slo/` = THE working
parquet**, zero-diff 38,760). `dslo = signal_vwap/sess_low - 1` (mass
point at 0 = the at-low trips); `dshi` rides along (corr -0.107 with
dslo — independent, unstudied).

**Correlations: dslo ↔ pah = 0.904** — dist-from-session-low IS the
runner-pullback measure (pah) in cleaner form; ↔ pab 0.559.

| dslo bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ~0 (AT low) | 13,734 | 2.402 | 73.1 | +2.06 | 3.8 | 3.23 | 2.04 | 2.1 | 2.42 | 1.88 | 1.93 |
| [0.5,2) | 766 | 1.979 | 74.0 | +2.12 | 6.16 | 2.14 | 1.8 | 1.03 | 2.88 | 0.97 | 1.91 |
| [2,4) | 861 | 1.766 | 74.1 | +2.13 | 1.97 | 4.46 | 0.72 | 0.67 | 4.69 | 1.58 | 1.9 |
| [4,6) | 760 | 1.954 | 74.9 | +2.49 | 7.09 | 10.56 | 0.28 | 0.8 | 7.72 | 2.35 | 4.33 |
| [6,9) | 1,106 | 2.239 | 75.2 | +2.22 | 5.56 | 5.58 | 0.73 | 2.06 | 3.2 | 1.47 | 3.24 |
| [9,13) | 1,228 | 2.262 | 79.1 | +2.02 | 5.41 | 7.01 | 0.68 | 2.35 | 1.9 | 1.39 | 3.29 |
| [13,18) | 1,169 | 3.325 | 77.2 | +2.23 | 40.72 | 5.15 | 4.87 | 1.06 | 1.99 | 2.4 | 2.66 |
| ⭐ [18,25) | 1,022 | 4.977 | 81.7 | +2.21 | 11.42 | 9.84 | 5.51 | 1.24 | 3.18 | 4.87 | 11.26 |
| ⭐ [25,40) | 1,353 | 4.788 | 76.5 | +2.46 | 6.57 | 1.98 | 3.27 | 1.61 | 6.53 | 5.71 | 19.65 |
| >= 40 | 1,858 | 3.47 | 77.1 | +2.75 | 6.67 | 4.11 | 1.41 | 4.45 | 2.43 | 2.34 | 16.89 |

**The g60 version (full years — user):**

| dslo (g60) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ~0 (at low) | 4,225 | 3.177 | 76.0 | +2.24 | 8.83 | 4.64 | 3.44 | 2.69 | 2.59 | 2.52 | 2.87 |
| [0.5,2) | 230 | 2.359 | 76.5 | +2.41 | 5.63 | 1.94 | — | 1.28 | 1.52 | 0.65 | 4.34 |
| [2,4) | 260 | 3.831 | 85.8 | +2.44 | 4.13 | 1706 | 27.36 | 0.42 | — | 1.31 | 2.44 |
| [4,6) | 205 | 3.778 | 73.7 | +2.44 | 2.58 | 89.7 | 3.56 | 1.14 | 18.57 | 2.14 | 1011 |
| [6,9) | 403 | 2.284 | 76.7 | +2.28 | 2.56 | 17.73 | 5.52 | 3.58 | 1.81 | 0.82 | 25.69 |
| [9,13) | 482 | 4.727 | 84.4 | +2.1 | 16.09 | 8.89 | — | 110.6 | 8.31 | 1.76 | 20.74 |
| [13,18) | 579 | 5.111 | 82.0 | +2.46 | 34.18 | 3.02 | 13.85 | 2.31 | 54.71 | 3.31 | 4.38 |
| ⭐ [18,25) | 656 | 7.817 | 86.1 | +2.22 | 27.53 | 11.72 | 2.79 | 0.48 | 25.09 | 7.65 | 13.66 |
| ⭐ [25,40) | 795 | 6.119 | 78.5 | +2.61 | 12.64 | 2.13 | 21.31 | 1.92 | 7.05 | 5.74 | 19.59 |
| >= 40 | 1,442 | 3.548 | 77.7 | +2.87 | 6.84 | 4.0 | 0.85 | 12.86 | 2.64 | 2.04 | 19.67 |

(⚠ the g60 retest zone [0.5,6) is 205-260-trip cells with lottery year
values — the retest TRAP is measured on the full book, where it is
2022-23-consistent; on clean tape it is small-n/mixed.)

**READING — THE U-SHAPE refines the S41l binary:** the binary "above the
session low = 4.08" HID a trap: **the RETEST ZONE [0.5,6) cushion =
1.77-1.98 (2022-23 = 0.28-1.03) — WORSE than being AT the low (2.40)**.
Flushing back toward the morning low with a thin cushion = the support
break in progress. The edge lives at [13,40) cushion (3.3-5.0, g60 6-8):
a flush that still sits WELL above the day's floor = the true runner
pullback; >= 40 rolls off (2022 1.41, over-extended names). dslo ≈ pah
(0.904) explains the whole family: pah/d0945/pab all proxy "how much
cushion above the day's floor". **DAY-STRUCTURE FAMILY FINAL FORM: seat
= dslo bands {AVOID [0.5,6), PREFER [13,40)}; at-low = book-neutral;
binary retired for the graded U.**

**S41p addendum — the at-low cell subdivided (user).** First: the [0,0.5)
cell is **97.9% EXACT zeros** (13,440/13,734 — the signal IS the session
low); the epsilon-above slivers are 26-96-trip anecdotes (their lows date
back a median ~50min — census-thin, nothing there). The REAL subdivision
needs a second axis; two work:

**AT-LOW × pab (day shape):** the more of the day the stock spent ABOVE
its vwap before crashing to a new low, the better the fade.

| at-low × pab, FULL BOOK | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| [0,10) grinder | 3,210 | 1.969 | 72.0 | +1.99 | 4.09 | 1.26 | 3.35 | 2.05 | 1.74 | 1.61 | 1.74 |
| [10,25) | 4,858 | 2.6 | 75.0 | +2.14 | 3.69 | 3.74 | 2.07 | 1.7 | 2.94 | 2.04 | 2.46 |
| [25,45) | 3,779 | 2.555 | 71.7 | +2.02 | 4.46 | 6.68 | 2.23 | 2.86 | 3.09 | 1.79 | 1.3 |
| [45,65) | 1,359 | 2.345 | 73.4 | +1.98 | 1.54 | 3.59 | 0.87 | 2.44 | 2.28 | 3.59 | 4.03 |
| >= 65 (V-day) | 234 | 3.567 | 70.9 | +2.32 | — | 0.52 | — | — | 2.04 | 16.4 | 4.2 |

| at-low × pab, g60 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| [0,10) grinder | 801 | 2.307 | 72.5 | 2.51 | 2.18 | 17.7 | 1.57 | 1.52 | 2.74 | 3.21 |
| [10,25) | 1,690 | 3.468 | 78.1 | 10.24 | 4.15 | 4.89 | 2.97 | 2.98 | 2.35 | 3.73 |
| [25,45) | 1,171 | 2.972 | 73.8 | 13.64 | 9.27 | 1.55 | 2.98 | 2.81 | 2.19 | 2.22 |
| ⭐ [45,65) | 423 | 4.765 | 80.6 | 77.97 | 9.15 | 2.98 | 2.61 | 6.91 | 8.23 | 2.3 |
| >= 65 (V-day) | 61 | 11.88 | 68.9 | — | 50.04 | — | — | — | 51.36 | — |

**AT-LOW × dshi (fall from the session high):** deep-but-not-cataclysmic
falls bounce best.

| at-low × dshi, FULL BOOK | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| >= -8 (tight day) | 4 | — | 100.0 | +1.1 | — | — | — | — | — | — | — |
| [-14,-8) | 1,099 | 1.993 | 70.2 | +1.46 | 3.29 | 1.76 | 0.77 | 2.95 | 11.72 | 1.38 | 2.73 |
| [-20,-14) | 3,258 | 2.084 | 71.0 | +1.69 | 2.92 | 3.23 | 1.97 | 2.71 | 1.27 | 2.07 | 0.79 |
| [-30,-20) | 4,945 | 2.412 | 72.7 | +2.21 | 3.77 | 3.87 | 2.47 | 2.6 | 2.18 | 2.04 | 1.83 |
| ⭐ [-45,-30) | 3,388 | 2.982 | 77.8 | +2.42 | 6.92 | 4.26 | 2.58 | 1.78 | 2.6 | 2.27 | 3.62 |
| < -45 (crash day) | 746 | 1.909 | 67.4 | +2.31 | 2.25 | 8.26 | **0.01** | 0.7 | 20.74 | 0.83 | 1.91 |

| at-low × dshi, g60 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| [-14,-8) | 136 | 3.637 | 86.0 | 52.05 | 1.76 | — | 1.8 | 12.81 | 20.49 | — |
| [-20,-14) | 808 | 2.403 | 72.4 | 18.11 | 5.77 | 1.96 | 2.12 | 1.45 | 2.6 | 0.57 |
| [-30,-20) | 1,759 | 2.671 | 74.7 | 5.99 | 11.86 | 8.9 | 3.01 | 2.37 | 1.93 | 2.12 |
| ⭐ [-45,-30) | 1,202 | 4.295 | 78.5 | 16.3 | 3.07 | 2.02 | 2.5 | 1.99 | 5.23 | 29.68 |
| < -45 (crash day) | 237 | 5.061 | 78.1 | 4.09 | — | — | 0.6 | 26.94 | 1.07 | 5.4 |

**READING:** the at-low mass (58% of book) = grinder (weak, 1.97) + V-
crash (strong: high-pab or deep-dshi at-low = 3-4.8). Same day-structure
grammar from the other side: what matters is what the day WAS before the
flush — a runner crashing to its first session low fades well even AT
the low; a bleeder grinding lower does not.

## S41q — |esf| > 0.25 revisited on gap_60 < 4: the S41b mc=1 edge was a LIQUIDITY proxy (2026-08-02)

**User: revisit the S41b challenger on the clean-tape universe — worth the
mc=1 tradeoff?** Residual = v2.2 minus the eff band, × g60 = 15,220 @
2.766 (base_v13).

| config | mc=0 n | mc=0 PF | mc=0 2022 | mc=1 n | mc=1 PF | mc=1 worst yr |
|---|---|---|---|---|---|---|
| neither | 15,220 | 2.766 | 1.43 | 2,217 | 2.376 | 1.72 (2025) |
| abs(esf) > 0.25 | 11,473 | 2.897 | 1.49 | 1,837 | 2.627 | 1.91 (2025) |
| eff band (= v2.2 g60 book) | 9,277 | 3.640 | 3.18 | 1,564 | 2.891 | 1.78 (2023) |
| ⭐ BOTH | 7,357 | 3.735 | 2.86 | 1,330 | **3.058** | 2.04 (2023) |

mc=1 years for BOTH: 5.84 / 3.15 / **4.91** / 2.04 / 3.14 / 2.24 / 3.36.

**READING:** (a) as a REPLACEMENT, |esf| > 0.25 now loses at BOTH levels
on clean tape (2.627 vs 2.891 at mc=1 — the S41b full-book mc=1
dominance is GONE). The challenger's slot-efficiency edge was largely a
LIQUIDITY effect — it spread trips onto cleaner days; once gap_60 < 4
does that job explicitly, the eff band's concentration wins again. Same
lesson as dhf/rp_vol/S11: apparent edges keep resolving into the
liquidity axis. (b) as a STACK on the clean book it now EARNS: +0.095
mc=0 / **+0.167 mc=1** for −234 slot-trips, all years positive, 2022 =
4.91 — in S41b the same stack added +0.004 (nothing). **|esf| > 0.25 =
a clean-tape STACKING lens (A-tier), not a spec gate.**

## S41r — vwap z-scores (session/5m/10m/20m, log vs normal): shape ratios, one family member, one avoid (2026-08-02)

**User: z-scores vs the session/5m/10m/20m vwaps; log or normal space?**
Engine: 12 raw-moment cols baked (Σv·p², Σv·ln p, Σv·(ln p)² per window;
record-only — z math in SQL; **base_v14 = THE base, `v22_z/` = THE working
parquet**, zero-diff 38,760). ⚠ HOUSE LESSON: **DuckDB `log()` is BASE-10
— use `ln()`**; the first pass mixed log10(price) against natural-log
moments and produced −30σ medians (caught by the sanity row: σ_l must ≈
σ_n/V on every trip).

**Findings:** (a) **log vs normal: corr 0.997/0.996 — the space choice is
IRRELEVANT** at intraday dispersions; log kept for convention. (b) The z
ranges are tame (session median −2.16σ ∈ [−5.7, 1.2]; z20 median −2.11 ∈
[−4.2, −0.7]) and **the windows' z's are mutually ~ORTHOGONAL (0.045-
0.05) and near-independent of the raw distances (z20 ↔ dvw = 0.075)** —
dividing by each window's own vw-σ turns correlated distances into
independent SHAPE ratios; z20 in particular ≈ (dist below 20m vwap)/
(flush size) — the spec's gates already pin it into a narrow band.
(c) **z_sess = another day-structure family member** (corr 0.696 with
dslo, 0.558 with dsv): the g60 gradient runs 2.29 (< −3, 2022 = 0.52) →
4.3-4.6 at [−2,−0.5) — the cushion story again, weaker than dslo's own
table; no new seat. (d) One avoid sliver: **z20 ∈ [−1.5,−1) = 429 @
1.673 / 153 tkds** (the flush that barely dented its own 20m dispersion).

| z_sess (log), g60 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| < -3 | 578 | 2.286 | 67.8 | 24.21 | 29.37 | 0.52 | 1.51 | 6.43 | 1.5 | 1.8 |
| [-3,-2.5) | 1,573 | 3.372 | 76.9 | 8.87 | 6.13 | 9.82 | 3.15 | 3.91 | 2.53 | 2.23 |
| [-2.5,-2) | 2,164 | 3.476 | 79.8 | 11.28 | 4.3 | 8.44 | 1.98 | 1.74 | 3.39 | 8.51 |
| [-2,-1.5) | 1,251 | 4.315 | 77.2 | 4.07 | 5.39 | 24.3 | 5.17 | 16.0 | 2.13 | 5.81 |
| [-1.5,-1) | 965 | 3.772 | 79.9 | 8.6 | 4.12 | 8.51 | 8.87 | 4.39 | 1.4 | 33.63 |
| [-1,-0.5) | 1,106 | 4.595 | 78.7 | 15.79 | 4.27 | 6.39 | 1.73 | 4.94 | 3.32 | 14.14 |
| [-0.5,0) | 709 | 3.874 | 83.1 | 187.4 | 11.88 | 0.58 | 3.21 | 9.78 | 1.75 | 23.46 |
| >= 0 | 931 | 3.935 | 79.1 | 4.9 | 4.14 | 3.16 | 6.97 | 1.9 | 3.92 | 15.18 |

**z_sess (log), FULL BOOK:**

| z_sess bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -3 | 1,989 | 2.732 | 74.9 | +2.23 | 4.06 | 4.83 | 1.36 | 3.38 | 16.27 | 2.19 | 1.83 |
| [-3,-2.5) | 4,982 | 2.349 | 73.7 | +2.18 | 3.6 | 4.79 | 1.73 | 2.46 | 2.4 | 1.78 | 1.59 |
| [-2.5,-2) | 6,899 | 2.328 | 73.4 | +2.02 | 3.98 | 2.57 | 2.42 | 1.25 | 2.04 | 2.13 | 2.79 |
| [-2,-1.5) | 3,584 | 2.336 | 73.1 | +2.05 | 4.14 | 3.67 | 0.98 | 1.61 | 2.48 | 1.91 | 2.44 |
| [-1.5,-1) | 2,064 | 2.91 | 78.4 | +2.37 | 9.7 | 5.28 | 0.82 | 3.6 | 4.42 | 1.43 | 10.98 |
| [-1,-0.5) | 1,950 | 3.529 | 77.1 | +2.29 | 16.77 | 4.45 | 1.68 | 1.99 | 3.3 | 2.44 | 7.28 |
| [-0.5,0) | 1,204 | 3.068 | 78.2 | +2.31 | 10.9 | 11.45 | 0.9 | 1.14 | 7.32 | 1.62 | 19.87 |
| >= 0 | 1,185 | 3.49 | 77.8 | +2.57 | 4.18 | 3.17 | 6.08 | 3.14 | 1.77 | 4.23 | 15.43 |

**z_sess (NORMAL), FULL BOOK (the 0.997 twin — kept for the log-vs-normal
record):**

| z_sess bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -3 | 1,124 | 3.054 | 75.2 | +2.34 | 3.11 | 3.63 | 1.53 | 3.72 | 24.49 | 2.9 | 2.94 |
| [-3,-2.5) | 3,709 | 2.157 | 73.6 | +2.16 | 3.25 | 5.06 | 1.48 | 2.66 | 3.04 | 1.83 | 0.98 |
| [-2.5,-2) | 7,561 | 2.717 | 74.3 | +2.06 | 3.74 | 3.04 | 2.71 | 2.17 | 2.11 | 2.41 | 3.98 |
| [-2,-1.5) | 4,764 | 2.022 | 72.0 | +2.05 | 5.05 | 3.1 | 0.95 | 0.82 | 2.32 | 1.63 | 2.12 |
| [-1.5,-1) | 2,454 | 3.086 | 78.6 | +2.38 | 10.06 | 5.44 | 1.01 | 3.36 | 5.83 | 1.33 | 10.34 |
| [-1,-0.5) | 2,064 | 3.115 | 76.1 | +2.29 | 13.18 | 3.89 | 1.22 | 2.03 | 2.67 | 2.03 | 6.97 |
| [-0.5,0) | 1,141 | 3.007 | 78.4 | +2.32 | 7.62 | 16.42 | 0.8 | 1.13 | 4.22 | 2.27 | 19.27 |
| >= 0 | 1,040 | 3.68 | 78.2 | +2.55 | 4.74 | 2.85 | 98.89 | 3.3 | 1.76 | 4.06 | 12.46 |

**z_20m (log), FULL BOOK:**

| z_20m bucket | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -3 | 329 | 3.062 | 65.0 | +2.23 | 6.43 | 4.99 | 0.76 | 3.17 | 6.45 | 1.88 | 2.83 |
| [-3,-2.5) | 3,080 | 3.013 | 76.0 | +2.39 | 6.07 | 4.94 | 1.03 | 1.9 | 5.73 | 2.05 | 3.34 |
| [-2.5,-2) | 11,536 | 2.507 | 75.4 | +2.2 | 3.68 | 3.68 | 1.29 | 1.57 | 2.47 | 2.42 | 2.9 |
| [-2,-1.5) | 8,479 | 2.522 | 74.4 | +2.09 | 7.63 | 3.5 | 2.29 | 2.29 | 2.35 | 1.54 | 2.48 |
| ⚠ [-1.5,-1) | 429 | 1.673 | 62.5 | +1.48 | 2.01 | 2.2 | 1.65 | 2.65 | 0.8 | 3.77 | 0.88 |
| >= -1 | 4 | — | 100.0 | +0.74 | — | — | — | — | — | — | — |

**VERDICT:
the σ-cloud z adds no new seat — its session form re-derives the cushion
axis, its window forms are spec-pinned shape ratios; keep the z20
[−1.5,−1) avoid + the raw moments (free for future normalizers).**

**S41r addendum — z_20m (log) on the g60 book (user):**

| z_20m (g60) | n | PF | win% | med | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| < -3 | 60 | 7.35 | 73.3 | +3.96 | — | 41.29 | 0.22 | — | 23.1 | 3.08 | — |
| [-3,-2.5) | 1,181 | 4.671 | 79.9 | +2.82 | 20.15 | 8.9 | 0.41 | 8.2 | 10.73 | 2.73 | 4.08 |
| [-2.5,-2) | 4,690 | 3.773 | 79.3 | +2.41 | 10.74 | 6.4 | 3.84 | 2.25 | 2.86 | 2.91 | 3.64 |
| [-2,-1.5) | 3,176 | 3.178 | 77.0 | +2.26 | 5.75 | 3.83 | 10.26 | 3.18 | 2.27 | 1.83 | 8.48 |
| ⚠ [-1.5,-1) | 170 | 1.803 | 62.4 | +1.5 | 0.79 | 1.57 | — | 0.99 | 3.31 | 1.86 | 2.94 |

On clean tape z_20m becomes a MONOTONE gradient (4.67 → 3.77 → 3.18 →
1.80; the weak-dip avoid confirms at 170/50 tkds) — but **2022 INVERTS
it** (deep bands 0.22/0.41, mild band 10.26): the deep-z flush on clean
tape = the bear-year knife. A graded clean-tape lens with a regime
caveat (overlay material, sized down in bear regimes), not a gate.

## S41s — ⭐⭐ SPEC v2.3 BAKED: z_20m < -1.5σ (the weak-dip trim) (2026-08-02)

**SPEC v2.3 = v2.2 + `z_20m(log) < -1.5`** (user: "we're just trimming the
edges" — the monotone g60 gradient earned it). Gate = (ln vwap − L)/σ_L on
the 1200-bar window's own vw ln-moments (same sums as the recorded
dlv_1200/dlv2_1200 — SQL twins); degenerate σ fails; `--max-z-20m`, >= 0 =
off; the canonical base CLI gains `--max-z-20m 0`. NO new record cols —
**base_v14 stays THE base** (predates the gate, identical universe).

**`v23_reference/` = THE reference — GRAND PARITY ✓: engine 38,069 = SQL
38,069, zero diff. Book 23,857 @ 2.569 → 23,424 @ 2.588** (years 4.79 /
3.78 / 1.46 / 1.84 / 2.70 / 1.97 / 2.82 — 2024 and 2026 up, rest ~flat);
**mc=1 3,548 @ 2.287** (2.268 → 2.287; ladder: … 2.217 → 2.268 →
**2.287**). Both mc levels improve for −433 trips — a true edge trim,
exactly as forecast from base_v14 (23,424 @ 2.588 / 3,548 @ 2.287 both
matched pre-computation EXACTLY).

## S41t — the price axis on v2.3: the $10 ceiling is NOT earning (2026-08-02)

**User: price breakdown on full + g60 raw v2.3, decide whether to bake
< $10.** (Full band tables above in the run; canonical rows:)

| band (FULL raw) | n | PF | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|
| < $0.50 | 3,840 | 2.114 | 2.08 | 2.49 | 1.79 | 2.02 |
| [$0.50,1) | 3,872 | 2.442 | 1.58 | 3.15 | 2.69 | 2.17 |
| [$1,2) | 6,545 | 3.094 | 3.27 | 1.85 | 2.21 | 4.21 |
| [$2,10) | 16,879 | ~2.4 | 0.39-2.47 | 1.07-3.1 | 1.46-2.52 | 1.35-6.06 |
| [$10,15) | 2,438 | 2.509 | 2.11 | 2.75 | 3.52 | 0.91 |
| [$15,25) | 2,202 | 2.37 | 1.77 | 0.86 | 2.43 | 2.07 |
| >= $25 | 2,293 | 2.151 | 3.45 | 14.11 | 2.2 | 3.81 |

| band (g60 raw) | n | PF | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|
| [$7,10) | 1,156 | 5.226 | 6.69 | 3.72 | 3.61 | 17.35 |
| [$10,15) | 937 | 3.624 | 2.53 | 2.04 | 3.1 | — |
| [$15,25) | 700 | 4.071 | 13.76 | 4.34 | 3.84 | 3.1 |
| >= $25 | 833 | 4.848 | — | 8.81 | 3.32 | 2.76 |

**The decision cells:**

| config | mc=0 | mc=1 | mc=1 slot-points |
|---|---|---|---|
| FULL $1-10 (current) | 23,424 @ 2.588 | 3,548 @ 2.287 | 4,329 |
| FULL $1+ | 30,357 @ 2.528 | 4,285 @ 2.109 | 4,756 |
| FULL $10+ slice | 6,933 @ 2.333 (2022 2.39, 2023 2.09, 2025 2.59) | — | — |
| g60 $1-10 | 9,107 @ 3.685 | 1,545 @ 2.924 | 2,395 |
| g60 $1+ | 11,577 @ 3.778 | 1,889 @ 2.771 | 2,871 |
| g60 $10+ slice | 2,470 @ 4.139 (2022 8.87) | — | — |

**READING:** the < $10 rule dates to v1.3; under v2.3 it is NOT earning.
The $10+ slice is book-class on the full universe (2.333, with the WEAK
years 2022/23/25 all BETTER than the sub-$10 book's) and STRONG on clean
tape (4.139, 2022 = 8.87) — and these are the most liquid, best-fill
names. mc=0: ceiling-off improves g60 (+0.093), ~flat full (−0.06). mc=1:
ceiling-off trades −0.15 PF for +20-22% trips → +10-20% total slot-
points. The ceiling is a slot-concentration device, not an edge
statement. Sub-$1 stays post-hoc-excluded on FEES (EU routes, v1.1),
with sub-$0.50 also the weakest band everywhere. **Decision = user's:
retire the ceiling (recommended — PF-per-slot gives way to points and
fills) or keep $1-10 as the concentrated book.**

**S41t VERDICT (user): the $10 CEILING IS RETIRED — THE BOOK = $1+.**
Rationale (user): the price bands OVERLAP each other in what they capture
— but the $10+ trades are completely NON-OVERLAPPING with the sub-$10
book, so they are a PURE increase to net (and the best-fill names).
**New canonical book (v2.3, $1+): 30,357 @ 2.528 / mc=1 4,285 @ 2.109 /
4,756 slot-points; g60: 11,577 @ 3.778 / mc=1 1,889 @ 2.771 / 2,871
slot-points.** (The mc=1 PF ladder was denominated on the $1-10 cut
through 2.287; the book redefinition resets the denominator — 2.109 on
$1+ is the new baseline, not a regression.) `flushfader_mc.fsx` default
cut updated to `>= 1`. ⏭ FUTURE (user): sub-$1 may become tradable via
a REBATE tier — enough limit-order traffic at a commission tier where
rebates pay the per-share fees; revisit with the slippage/production
work.

## S41u — ⭐ THE SIZING-LEVER ROSTER (user design) + speed × dhf at the low (2026-08-02)

**The distance levers are SETTLED (user): a REGIME-SWITCHED pair + speed.**

| lever | when | form | proven cells |
|---|---|---|---|
| ⭐ dslo (cushion above the day's floor) | ABOVE the session low | U-bands: PREFER [13,40) (g60 [18,25) = 7.82), AVOID [0.5,6) retest zone | S41p |
| ⭐ dhf (the leg's ARMING drop) | AT the session low (where dslo is silent; 7.51 vs 2.71 separation) | deeper = better; < -24 = the top cell (9.10/97 tkds) | S41f/S41u |
| ⭐ speed (the flush NOW) | always (BOTH tapes) | convex: < -6 × g60 = A (4.27/311 tkds); < -10 = A++ both tapes (7.71/94 tkds) | S41k |
| RETIRED | — | pco, d0945, dsv, pab (all = cushion proxies, dslo ↔ pah 0.904); d20 (spec shadow: corr 0.46-0.67 w/ ssf/dlv/dhf/dshi) | S41l/p |
| dshi (dist from session high) | NO lever seat | shards kept: crash-day AVOID (at-low × dshi < -45 = 2022 0.01); at-low subdivision refinement | S41p |

Correlation backbone: dhf ↔ dslo −0.15, dshi ↔ dslo −0.12, dhf ↔ dshi
0.43, dhf ↔ speed 0.28 — the three seated levers are mutually near-
orthogonal: NOW-violence × floor-CUSHION × BIRTH-violence.

**Speed × dhf AT the session low (g60, 5,030 trips; n @ PF):**

| dhf \ speed | [-4,-2) | [-6,-4) | [-10,-6) | < -10 |
|---|---|---|---|---|
| < -24 | 60 @ 17.96 | 100 @ 15.75 | 79 @ 30.05 | — |
| [-24,-16) | 347 @ 6.09 | 241 @ 4.96 | 188 @ 6.87 | 18 @ 0.42 |
| [-16,-10) | 824 @ 3.84 | 494 @ 2.7 | 290 @ 3.2 | 33 @ 11.61 |
| [-10,-6) | 1,144 @ 2.83 | 422 @ 1.8 | ⚠ 104 @ 1.06 | 45 @ 2.11 |
| >= -6 | 423 @ 2.23 | 132 @ 2.85 | 60 @ 24.42 | 5 @ 93.3 |

**READING:** at the low, dhf DOMINATES and speed is mostly flat within a
dhf band — the deep-birth rows (< -16) are strong at every speed (the
leg was born violent; extra now-speed adds nothing). Two exceptions:
(a) ⚠ **the mid-shallow band [-10,-6) INVERTS with speed** (2.83 → 1.06
at [-10,-6) speed) — a modest-birth leg now accelerating = the breakdown
gathering pace, not capitulating — a real avoid corner; (b) the shallow-
birth × violent-now corner (dhf >= -6 × speed < -6 = 60 @ 24.4 + 5 @
93) = the FLASH CRASH on a quiet day — spectacular but 65 trips, census-
thin. **Sizing rule at the low: size by dhf; ignore speed except to
AVOID the accelerating mid-shallow corner.**

## S41v — rp_vol [0.8,1.0) × the liquidity family: THE LADDER WITHIN (2026-08-02)

**User (+ the sizing pivot): 2D lever grids are census-starved — sizing
will take the MAX PF over individual lever features, not conjunction
cells. Price-based features paused. Study: rp_vol [0.8,1.0) stacked with
gap_60 / gap_1200 / n_eff_hhi.** On the v2.3 $1+ book the steady-tape
band = **3,314 @ 4.633 / 78.7**.

| rp × gap_60 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| [0,2) | 984 | 10.189 | 85.1 | 9.95 | 32.34 | 56.56 | 15.25 | 1.76 | 32.75 | 12.16 |
| [2,4) | 316 | 4.943 | 74.1 | 5.7 | 1.23 | 19.49 | 15.82 | 3.53 | 5.13 | — |
| [4,10) | 585 | 4.577 | 76.1 | 3.93 | 6.82 | 2.5 | 1.05 | 9.51 | 12.12 | 15.38 |
| [10,25) | 856 | 2.529 | 75.4 | 13.76 | 1.62 | 0.69 | 0.78 | 3.75 | 4.96 | 27.37 |
| >= 25 | 573 | 4.588 | 78.2 | 7.28 | 4.55 | 1.56 | 3.3 | 4.47 | 47.17 | — |

| rp × gap_1200 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| [0,5) | 367 | 17.464 | 86.9 | 15.53 | 49.3 | — | 5.43 | 4.13 | 11.37 | — |
| [5,60) | 477 | 5.37 | 80.7 | 6.24 | 7.99 | — | 5.61 | 1.46 | 13.99 | 9.12 |
| [60,180) | 482 | 4.986 | 78.8 | 3.58 | 10.58 | 15.04 | 4.92 | 2.78 | 17.53 | 64.74 |
| [180,400) | 725 | 3.5 | 78.9 | 39.64 | 2.61 | 0.77 | 0.81 | 11.34 | 17.4 | 56.12 |
| >= 400 | 1,263 | 4.046 | 75.5 | 10.1 | 3.48 | 1.55 | 3.05 | 3.11 | 6.03 | 25.41 |

| rp × n_eff_hhi_1200 | n | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| [0,80) | 89 | 5.052 | 83.1 | — | 0.08 | 0.68 | — | 7.91 | — | 55.9 |
| [80,200) | 1,470 | 3.581 | 78.2 | 4.85 | 3.09 | 1.31 | 1.81 | 3.74 | 9.86 | 39.9 |
| [200,400) | 1,485 | 5.416 | 78.5 | 10.05 | 10.53 | 2.47 | 1.99 | 2.57 | 11.54 | 11.09 |
| [400,650) | 263 | 20.942 | 83.7 | 12.39 | 130.8 | — | 3.49 | 60.58 | 12.65 | — |
| >= 650 | 7 | 0 (7 losers) | 0.0 | — | — | — | — | — | — | — |

Census: rp × g60 < 2 = **984 @ 10.19 / 194 tkds**; rp × g60 < 4 = 1,300
@ 8.39 / 264 tkds; rp × gap_1200 < 5 = 367 @ 17.46 / 65 tkds; rp × nh12
[400,650) = 263 @ 20.94 / 50 tkds.

**READING — the steady-tape flush scales MONOTONICALLY with tape
continuity:** 4.63 (band) → 8.39 (g60<4) → 10.19 (g60<2, 194 tkds — the
tradable-census form) → 17.5 / 20.9 at the ultra-continuous tier (65/50
tkds — SIZE territory). The three liquidity axes are the same nested
family (S41g), so these are deepening views of ONE conjunction:
UNREMARKABLE volume flushing on a PERFECT tape — the purest "sentiment
dislocation without participation change" the system has found. Warts:
the g60 [10,25) trough (2.53 w/ 2022-23 < 0.8) and 2024 = 1.76 in the
top cell (its known soft year). nh12 >= 650 in-band = 7/7 losers
(degenerate ultra-distributed corner). ⭐ the ladder for the max-PF
sizing scheme: rp-band 4.6 → ×g60 8.4-10.2 → ×g12<5 17.5.

## S41w — speed × the rp ladder: CROSS-FAMILY STACKS ARE CLEAN (2026-08-02)

**User: put flush speed on top of the rp × liquidity ladder.**

| speed within rp band (3,314) | n | tkds | PF | win% | 2024 |
|---|---|---|---|---|---|
| [-3.5,-2) | 1,812 | 508 | 3.548 | 77.6 | 2.35 |
| [-5,-3.5) | 932 | 266 | 4.484 | 77.8 | 5.0 |
| [-8,-5) | 462 | 142 | 9.008 | 84.8 | 2.76 |
| < -8 | 108 | 30 | 19.004 | 79.6 | 254.9 |

| speed within rp × g60<4 (1,300) | n | tkds | PF | win% |
|---|---|---|---|---|
| [-3.5,-2) | 617 | 173 | 5.454 | 79.7 |
| [-5,-3.5) | 390 | 115 | 8.637 | 83.6 |
| [-8,-5) | 217 | 65 | 16.728 | 88.0 |
| < -8 | 76 | 20 | 34.875 | 81.6 |

| speed within rp × g60<2 (984) | n | tkds | PF | win% |
|---|---|---|---|---|
| [-3.5,-2) | 465 | 126 | 5.561 | 81.3 |
| [-5,-3.5) | 276 | 86 | 18.649 | 90.2 |
| [-8,-5) | 177 | 48 | 15.006 | 87.6 |
| < -8 | 66 | 15 | 40.453 | 83.3 |

(Full year columns in the run tables; 2022-23 in the upper tiers are
mostly loss-free (NULL PF); the recurring soft pockets are 2024.)

**READING — WHY this stack is clean where the price-lever grids were
noise:** rp (participation shape) × gap (tape quality) × speed (violence)
are three DIFFERENT families, mutually near-orthogonal — their
conjunctions multiply (3.5 → 9.0 → 16.7 → 34.9 with censuses 508 → 142 →
65 → 20 tkds); the S41u dhf × speed grid was two PRICE-family levers
re-measuring the same drop geometry, hence the variance. **ARCHITECTURE
RULE for the max-PF sizing scheme: take MAX within a family, MULTIPLY
across families.** The tradable mid-rungs: rp × speed [-8,-5) = 462 @
9.0 / 142 tkds; rp × g60<4 × speed [-5,-3.5) = 390 @ 8.6 / 115 tkds.

## S41x — THE PRICE-FAMILY LEVER CALIBRATION: 16 features × deciles (2026-08-02)

**User plan: the sizing lever = MAX PF over the family members' buckets
(simple, robust to thin cells; right when members overlap heavily).
Selected DISTANCE: speed, d1m, dvw, d20, dlv, dsf, dhf, dslo, dshi, dsv,
pah (dvw/d20/pah UN-retired for this test). Selected SLOPE: slope20,
slope10, slope5, ssf, ssh. r-features excluded; esh/eff → the efficiency
family. Leaning: ONE combined distance+slope family.**

**FEATURE REFERENCE (the price family):** all measured AT the signal bar
(a fresh 20m low); distances in % (negative = below the anchor), slopes
in bp/min on OLS of ln(vwap).

| abbr | name | definition | plain English |
|---|---|---|---|
| speed | 1m flush speed | vwap / vwap_60_prev − 1 | how hard the LAST MINUTE fell (vs the 1m vwap one minute ago) |
| d1m | dist from 1m high | vwap / hi_60 − 1 | how far below the last minute's high the print is |
| dvw | dist from 20m vwap | vwap / vwap_1200 − 1 | how stretched below the 20-minute rolling vwap |
| d20 | dist from 20m high | vwap / chan_hi − 1 | the whole 20m leg's depth: current price vs the 20m channel high |
| d20a | dist from the ARMING high | (1+dsf)·(1+dhf) − 1 | the leg's TOTAL depth from its BIRTH high (frozen at arming — unlike d20's decaying channel) |
| dlv | dist from LEG vwap | vwap / (dv_leg/vol_leg) − 1 | how far below the average price paid DURING this leg (since its first low) |
| dsf | extension below first low | vwap / first_low_vwap − 1 | how much further the leg has fallen since its FIRST low printed |
| dhf | the ARMING drop | first_low / 20m-high at arming − 1 | how violent the drop that STARTED the leg was (high → first low; frozen at arming) |
| dslo | cushion above session low | vwap / sess_low − 1 | how far the flush still sits ABOVE the day's floor (0 = this IS the session low) |
| dshi | fall from session high | vwap / sess_high − 1 | how far below the day's top the print is |
| dsv | dist from session vwap | ln(vwap / sess_vwap) | above/below the day's volume-weighted average price |
| pah | day change at pre-flush high | (1+pco)/(1+d20) − 1 | how far UP on the day the stock was at its 20m high, BEFORE this flush (the runner detector) |
| slope20 | 20m OLS slope | ols on ln(vwap), 1200 bars | the 20-minute trend's steepness |
| slope10 | 10m OLS slope | same, 600 bars | the 10-minute trend's steepness |
| slope5 | 5m OLS slope | same, 300 bars | the last 5 minutes' steepness (the vertical-collapse detector) |
| ssf | slope since flow | OLS since the leg's first low | how fast the leg has been falling since ITS OWN start (growing window) |
| ssh | slope since high | OLS since the last 20m high | how fast price has fallen since the leg's top |

**⭐ THE d20-vs-SLOPE ANSWER (user question): d20 ↔ slope20 = 0.87, d20 ↔
ssh = 0.731, ssh ↔ slope20 = 0.749** — the distance from the 20m high IS
the 20m slope family (they'd only decouple via leg age, and d20 ↔ age =
0.091). The S41u note quoted d20 only vs ssf/dlv/dhf/dshi (0.46-0.67);
vs its own window's slopes it's a near-duplicate — supporting the ONE-
family design.

Decile tables (equal-n ≈ 3,036/bucket, v2.3 $1+ book 30,357 @ 2.53;
dec 1 = most extreme). ⚠ dslo's deciles 1-5 are the 45% mass at exactly
0 (ntile splits ties arbitrarily — year cells there are meaningless);
dslo's lever form stays the S41p band table.
**PF per decile (dec 1 = most extreme/negative; d20a added per user —
its dec-1 = 3.84 = THE best extreme cell in the family; corr d20a ↔ dhf
0.86 / d20 0.78 / dsf 0.78 — the composition of its parts, but the
frozen-anchor total depth ranks better than any of them):**

| d20a_d20 | d20a_dsf | d20a_dhf | d20a_ssh |
|---------:|---------:|---------:|---------:|
| 0.784    | 0.777    | 0.86     | 0.536    |

| dec | speed | d1m  | dvw  | d20  | d20a | dlv  | dsf  | dhf  | dslo | dshi | dsv  | pah  | slope20 | slope10 | slope5 | ssf  | ssh  |
|----:|------:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|--------:|--------:|-------:|-----:|-----:|
| 1   | 3.23  | 3.47 | 3.23 | 3.36 | 3.84 | 3.05 | 3.1  | 3.58 | 3.41 | 2.49 | 2.87 | 1.97 | 3.55    | 3.46    | 3.42   | 3.04 | 3.06 |
| 2   | 2.68  | 2.32 | 3.13 | 2.79 | 2.26 | 2.31 | 2.8  | 2.69 | 2.88 | 3.19 | 3.18 | 2.81 | 2.27    | 2.59    | 2.39   | 3.46 | 2.48 |
| 3   | 2.6   | 2.39 | 2.74 | 2.94 | 2.22 | 2.46 | 2.67 | 2.57 | 1.98 | 3.04 | 2.35 | 2.34 | 2.55    | 2.72    | 2.55   | 2.66 | 3.02 |
| 4   | 2.39  | 2.45 | 2.3  | 2.25 | 2.97 | 2.56 | 2.85 | 2.34 | 1.83 | 2.52 | 2.04 | 1.98 | 2.7     | 2.91    | 2.73   | 2.53 | 2.24 |
| 5   | 2.31  | 2.47 | 2.37 | 2.38 | 2.93 | 2.67 | 2.52 | 2.23 | 2.38 | 2.3  | 2.69 | 2.68 | 2.29    | 2.23    | 2.37   | 2.04 | 2.31 |
| 6   | 2.63  | 2.56 | 2.22 | 2.31 | 2.35 | 2.64 | 2.25 | 1.99 | 2.2  | 3.07 | 2.36 | 2.29 | 2.37    | 2.17    | 2.56   | 2.1  | 3.09 |
| 7   | 2.51  | 2.52 | 2.2  | 2.17 | 2.41 | 2.76 | 2.58 | 2.57 | 1.88 | 2.88 | 2.46 | 2.09 | 2.44    | 2.11    | 2.59   | 2.11 | 2.2  |
| 8   | 2.26  | 2.35 | 2.35 | 2.5  | 1.97 | 2.47 | 2.31 | 2.16 | 2.53 | 2.11 | 2.17 | 2.69 | 2.91    | 2.62    | 2.24   | 2.32 | 2.22 |
| 9   | 2.49  | 2.62 | 2.44 | 2.37 | 2.69 | 2.18 | 2.13 | 2.92 | 3.46 | 2.19 | 2.27 | 3.06 | 2.06    | 2.28    | 2.15   | 2.71 | 2.32 |
| 10  | 1.95  | 1.99 | 1.9  | 1.83 | 1.84 | 2.06 | 1.8  | 2.41 | 4.11 | 1.63 | 3.02 | 4.14 | 2.11    | 2.15    | 2.12   | 2.5  | 2.45 |

**Decile LOWER edges (dec k's hi = dec k+1's lo):**

| dec | speed |  d1m  |  dvw  |  d20  | d20a  |  dlv  |  dsf  |  dhf  | dslo | dshi  |  dsv  |  pah  | slope20 | slope10 | slope5 |  ssf   |  ssh   |
|----:|------:|------:|------:|------:|------:|------:|------:|------:|-----:|------:|------:|------:|--------:|--------:|-------:|-------:|-------:|
| 1   | -20.5 | -21.1 | -39.6 | -74.8 | -74.8 | -33.4 | -57.1 | -68.8 | 0.0  | -85.2 | -86.5 | -75.4 | -344.3  | -363.3  | -399.8 | -374.3 | -345.2 |
| 2   | -6.1  | -6.0  | -13.0 | -22.4 | -28.5 | -11.4 | -15.7 | -17.0 | 0.0  | -39.2 | -26.0 | -11.3 | -98.4   | -120.5  | -181.5 | -105.1 | -97.7  |
| 3   | -4.9  | -4.9  | -10.9 | -19.2 | -24.5 | -9.3  | -13.2 | -13.8 | 0.0  | -33.0 | -20.7 | -3.4  | -79.8   | -101.0  | -142.0 | -79.8  | -76.1  |
| 4   | -4.2  | -4.2  | -9.7  | -17.1 | -21.9 | -8.0  | -11.5 | -11.6 | 0.0  | -29.0 | -17.2 | 1.1   | -68.5   | -87.3   | -116.7 | -67.1  | -64.0  |
| 5   | -3.7  | -3.8  | -8.8  | -15.6 | -19.7 | -7.0  | -10.2 | -10.2 | 0.0  | -25.7 | -14.7 | 4.1   | -60.6   | -75.6   | -98.3  | -57.5  | -55.1  |
| 6   | -3.3  | -3.4  | -8.0  | -14.4 | -17.9 | -6.3  | -9.3  | -8.9  | 0.0  | -23.0 | -12.4 | 7.4   | -54.1   | -66.2   | -82.7  | -50.3  | -48.9  |
| 7   | -3.0  | -3.1  | -7.4  | -13.3 | -16.3 | -5.6  | -8.3  | -7.9  | 1.8  | -20.4 | -10.5 | 11.6  | -48.6   | -57.6   | -67.9  | -43.8  | -43.3  |
| 8   | -2.8  | -2.9  | -6.7  | -12.2 | -14.9 | -4.9  | -7.5  | -7.0  | 7.6  | -17.9 | -8.8  | 18.1  | -43.1   | -48.6   | -53.6  | -38.3  | -37.6  |
| 9   | -2.5  | -2.6  | -6.1  | -11.2 | -13.4 | -4.3  | -6.7  | -6.1  | 15.9 | -15.6 | -7.1  | 28.4  | -38.0   | -39.5   | -38.2  | -33.4  | -32.5  |
| 10  | -2.3  | -2.3  | -5.4  | -9.9  | -11.7 | -3.7  | -5.8  | -5.0  | 34.3 | -13.1 | -4.3  | 50.4  | -32.0   | -28.6   | -19.7  | -29.1  | -26.6  |

(Per-feature year columns available by rerun; the matrix is the canonical
lever-calibration view — user format call.)

**READING:** the family is ONE SHAPE, sixteen ways: extreme decile ≈
3.0-3.6, middle ≈ 2.2-2.6, shallowest decile ≈ 1.8-2.1 — massive
redundancy (the max design's justification). Per-feature notes: the
monotone cleans = slope20/slope10/slope5/dsf/d20/dvw (extreme good,
shallow bad); the U/hump family = ssf (dec2 [-105,-80) = 3.46 peak, sag,
shallow recovery), dlv, dsv, dhf (dec1 3.58 + dec9-10 recovery); the
UP-monotones = pah (deep-red-day 1.97 → mega-runner 4.14) and dslo; dshi
= mid-depth hump ([-39,-29) = 3.0-3.2) with BOTH tails weak (crash-day
2.49, shallow-fall 1.63). Best single cells: dslo-top 4.11, pah-top
4.14, dhf-dec1 3.58, slope20-dec1 3.55, d1m-dec1 3.47, ssf-dec2 3.46.
⏭ build the per-trip MAX-PF transform over these calibrations and
validate (rank realized PF by predicted tier; mc=1).

## S41y — the |d20a| + dslo composite (user curiosity) (2026-08-02)

score = |d20a| + dslo (both in %). **Scale imbalance: corr(score, dslo)
= 0.969 vs corr(score, |d20a|) = 0.381** — dslo's range (0..+600 for
mega-runners) dwarfs |d20a|'s (5..75), so the raw sum ≈ dslo with a
d20a tilt. Full-book deciles: top two (score >= 37.6) = 4.15 / 4.18,
middle sag, dec9 = 1.87 (the retest trap re-expressed). g60 deciles
noisy non-monotone.

**The one real gain: score >= 37.6 = 6,072 @ 4.16 (all years >= 1.8) vs
dslo-top-decile 3,035 @ 4.11 — SAME PF, DOUBLE the trips** (the d20a
tilt admits deep-leg trips with moderate cushion; iso-PF trip-efficiency
win). For honest composition the parts need a common scale first —
rank/decile space or PF space (the S41x calibration itself) rather than
raw %; the S41u regime-switch (dhf at the low / dslo above) remains the
clean two-feature form. Volatility noted as the missing family (volat_20m
recorded since v1.1 — queued for the lever program).

## S41z — the VOLATILITY family calibrated: it's the price family in disguise (2026-08-02)

Members (dec 1 = HIGHEST): v20 = volat_20m (bp/30s, the locked driver),
v10 twin, vr = v10/v20 (trajectory), v20p = volat_20m_prev (pre-window
level), vchg = v20/v20p (expansion), rng20/rngsess/rsl20 (ln-ranges %).
Intra-family corr: v20 ↔ v10 = 0.982, ↔ rng20 = 0.844, rng20 ↔ rsl20 =
0.969 — one axis + derived ratios.

**FEATURE REFERENCE (the volatility family):**

| abbr | name | definition | plain English |
|---|---|---|---|
| v20 | volat_20m | EmaHlMa (half-life 20 slots) of abs 30s-slot ln-returns, ×1e4 = bp/30s | how WILD the tape has been over the last ~20 minutes (THE locked vol measure, F7; the >= 40bp arming floor lives on this) |
| v10 | volat_10m | same, half-life 10 slots | the 10-minute twin (0.982 corr — same thing, faster) |
| vr | vol trajectory | volat_10m / volat_20m | is volatility RISING (> 1) or FADING (< 1) right now |
| v20p | pre-window vol | volat_20m as of 1200 present bars AGO | how wild the tape was BEFORE the current 20m window (the lagged normalizer, S39q) |
| vchg | vol expansion | volat_20m / volat_20m_prev | how much the current window EXPANDED vol vs the prior one (< 0.68 = the collapse avoid) |
| rng20 | 20m range | ln(chan_hi / chan_lo) × 100, % | how wide the 20m price channel is |
| rngsess | session range | ln(sess_high / sess_low) × 100, % | how wide the whole DAY's range is |
| rsl20 | 20m slot range | same on 30s slot vwaps | the microstructure-denoised twin of rng20 (0.969 corr) |

**PF per decile (dec 1 = highest):**

| dec | v20 | v10 | vr | v20p | vchg | rng20 | rngsess | rsl20 |
|----:|----:|----:|---:|-----:|-----:|------:|--------:|------:|
| 1 | 4.13 | 4.0 | 2.76 | 2.66 | 2.5 | 3.36 | 3.5 | 3.44 |
| 2 | 2.53 | 2.55 | 2.8 | 2.71 | 2.71 | 2.78 | 2.82 | 2.68 |
| 3 | 2.19 | 2.34 | 2.55 | 2.11 | 2.97 | 2.99 | 3.34 | 2.85 |
| 4 | 2.52 | 2.81 | 2.47 | 2.56 | 2.41 | 2.22 | 2.43 | 2.49 |
| 5 | 2.55 | 2.06 | 2.86 | 2.86 | 2.95 | 2.38 | 2.59 | 2.14 |
| 6 | 2.14 | 2.51 | 2.56 | 3.23 | 2.62 | 2.32 | 2.01 | 1.9 |
| 7 | 2.54 | 2.12 | 2.19 | 2.51 | 3.05 | 2.16 | 2.79 | 3.21 |
| 8 | 2.15 | 2.31 | 3.28 | 1.92 | 2.6 | 2.52 | 2.09 | 2.36 |
| 9 | 2.7 | 2.62 | 2.12 | 2.53 | 2.37 | 2.36 | 2.06 | 2.49 |
| 10 | 1.93 | 2.0 | 2.08 | 2.32 | 1.65 | 1.83 | 1.79 | 1.81 |

**Decile LOWER edges:**

| dec | v20 | v10 | vr | v20p | vchg | rng20 | rngsess | rsl20 |
|----:|----:|----:|---:|-----:|-----:|------:|--------:|------:|
| 1 | 140.2 | 130.3 | 1.04 | 186.9 | 1.09 | 25.3 | 69.4 | 21.9 |
| 2 | 115.7 | 108.8 | 1.01 | 146.5 | 1.01 | 21.3 | 53.8 | 18.3 |
| 3 | 101.5 | 95.2 | 0.98 | 121.7 | 0.96 | 18.8 | 43.9 | 15.9 |
| 4 | 91.3 | 86.5 | 0.97 | 107.4 | 0.93 | 17.0 | 36.8 | 14.4 |
| 5 | 83.3 | 79.1 | 0.95 | 95.1 | 0.89 | 15.5 | 31.7 | 13.1 |
| 6 | 76.6 | 72.8 | 0.93 | 84.0 | 0.85 | 14.2 | 27.4 | 11.9 |
| 7 | 69.4 | 66.5 | 0.92 | 74.4 | 0.81 | 13.0 | 23.4 | 10.8 |
| 8 | 61.8 | 59.7 | 0.89 | 64.9 | 0.76 | 11.8 | 19.5 | 9.8 |
| 9 | 53.5 | 52.4 | 0.87 | 54.8 | 0.68 | 10.5 | 15.9 | 8.5 |
| 10 | 40.0 | 35.6 | 0.73 | 7.9 | 0.17 | 5.5 | 7.3 | 4.0 |

**CROSS-FAMILY PLACEMENT: v20 ↔ d20a = 0.829** (↔ speed 0.40, ↔ dslo
0.23, ↔ rp_vol −0.014, ↔ gap_60 −0.24) — on a book of 20m flushes, the
20m |r|-EmaMa IS the flush's size: **volatility is NOT a separate lever
family; v20 joins the price family** (as arguably its best member — dec1
> 140bp = 4.13, the family's top extreme cell; dec10 hugging the 40bp
floor = 1.93 the drag). The derived members are DEAD (vr trajectory /
v20p pre-level = flat noise) except one avoid: **vchg dec10 (vol
COLLAPSED to < 0.68x its pre-window level) = 1.65**. Perfectly
orthogonal to participation (rp_vol −0.014) — the rp × v20-hot
conjunction is the natural next cross-family probe.

## S42 — inside the hot-tape cell (v20 >= 140bp): 3,049 @ 4.14 / 459 tkds (2026-08-02)

| lens within the cell | band | n | tkds | PF | win% |
|---|---|---|---|---|---|
| (the cell itself) | — | 3,049 | 459 | 4.14 | 78.8 |
| gap_60 | [0,2) | 1,689 | 276 | **5.88** | 82.2 |
| gap_60 | [2,25+) | 1,360 | 326 | 2.4-5.1 mixed | — |
| rp_vol | [0.8,1.0) | 336 | 55 | **9.35** | 77.7 |
| rp_vol | < 0.5 / >= 1.5 | 880 / 357 | 121 / 73 | 3.54 / 3.42 | — |
| cushion (dslo) | all bands | — | — | FLAT 3.1-4.5 | — |

Cell years: 5.84 / 3.49 / **1.16** / 5.74 / 4.63 / 4.05 / 3.37 — the
Achilles heel is 2022 (and the g60<2 sub-cell's 2022 = 0.69): hot tape
in the bear year = the knife regime (the z20-inversion pattern again —
extreme-violence cells flip sign in bear markets).

**READING — the architecture holds inside the cell:** liquidity still
separates (× g60<2 = 5.88 on 276 tkds = the tradable core, 56% of the
cell), participation still compounds (× rp star = 9.35 — the predicted
rp × v20 conjunction, orthogonal −0.014), and the price family adds
NOTHING (cushion flat; v20 ↔ d20a 0.83 already owns the depth info —
family-mates don't stack, S41w rule confirmed from the other side; even
the retest trap softens to 3.5 inside hot vol). Sizing voice: v20-dec1
enters the price-family max at 4.14, ×liquidity ×participation per the
cross-family product.

**S42 addendum — dslo inside the top v20 decile (user: v20 = THE longer-
term feature, speed = THE near-term feature; does above-low still win?):
NO — the premium VANISHES inside hot vol (mild inversion):**

| state (v20 >= 140) | n | tkds | PF | win% | med |
|---|---|---|---|---|---|
| AT the session low | 1,427 | 241 | 4.29 | 79.2 | +3.73 |
| ABOVE the session low | 1,622 | 238 | 4.01 | 78.5 | +3.31 |

vs the REST of the book (v20 < 140): at-low 15,467 @ 2.19 / above
11,841 @ 2.59 — the familiar premium lives ONLY outside the hot cell.
Fine dslo bands inside hot vol are census-thin noise (26-50 tkds cells;
the retest bands sit at 3.3-3.7, NOT below par; >= 70 = 7.59 on 42
tkds). **WHY: the at-low poison was the GRINDER (low-pab slow bleed,
S41p) — and grinders don't print 140bp/30s vol. Hot v20 already excludes
them, leaving the V-crash at-lows (the good kind) vs runner pullbacks
(also good): both ~4.1-4.3, nothing left for dslo to separate.**
Lever-design consequence: v20 and dslo need no conjunction — dslo is
the discriminator where vol is NOT extreme; v20-dec1 overrides it (the
max architecture again, from a third angle).

## S42b — strong vs weak flushes × hot vol: the CONTRAST grammar (2026-08-02)

**The 2×2 (strong = speed < -6%/1m):**

| | strong flush | weak flush |
|---|---|---|
| HOT v20 (>= 140bp) | 912 @ 3.75 / 217 tkds | **2,137 @ 4.43 / 359 tkds** |
| cool v20 | **2,209 @ 2.88 / 568 tkds** | 25,099 @ 2.30 / 4,337 tkds |

**The speed gradient INVERTS with the vol state.** Cool book: monotone
violence premium (2.19 → 2.63 → 3.05 → 6.82 at < -12). Inside hot v20:
a U — [-4,-3) = **5.89 / 206 tkds** (the peak: the exhaustion drip at
the end of a violent leg), **[-8,-6) = 2.87 with 2022 = 0.01** (the
re-acceleration: the crash still developing — THE knife cell), then the
terminal blow-off < -12 = 9.02 / 24 tkds (91.4% win, census-thin).

**READING — the grammar underneath all of it is CONTRAST:** violence
pays against a QUIET background (speed on cool tape; the S41u flash-
crash corner; rp-steady × violent price), and exhaustion pays after
violence (weak speed on hot tape = 4.43, the 2×2's best census cell).
Sustained violence — hot tape AND still accelerating, short of terminal
— is the developing crash (2.87, 2022 = 0.01). Lever consequence: the
near-term voice (speed) must be read AGAINST the long-term state (v20):
same number, opposite meaning — a signed handoff, not a max. v20-hot →
prefer weak/moderate speed (or terminal < -12); v20-cool → prefer
strong speed.

⏭ NEXT SESSION (user, 2026-08-02 close): the lever set is NARROWED TO
THREE — **v20 (volatility = the long-term state), speed (the flush =
the near-term voice, read AGAINST v20), dslo (distance from session low
= the discriminator on cool tape)**. FIRST ITEM: study **vchg**
(volat_20m / volat_20m_prev — the vol-expansion ratio; never examined
beyond its collapse-tail avoid at < 0.68 = 1.65, S41z).

## S42c — the DISJUNCTION test + d1s (breach depth) calibrated (2026-08-02/03)

**New feature (user): d1s = signal_vwap/chan_lo − 1** (the 1s bar's
undercut of the prior 20m low — the breach depth). Deciles nearly FLAT
(2.24-2.86, dec1 < −0.71%); corr speed 0.294 / v20 −0.096 — independent
but weak alone.

**The disjunction (user): spec ∧ (v20 >= 140 ∨ d20a < −28 ∨ speed < −6
∨ d1s < −1 ∨ dslo >= 6 ∨ pah >= 28):**

| | n | tkds | PF | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| DISJUNCTION | 14,188 | 2,474 | 3.06 | 76.8 | 5.61 | 3.87 | 1.42 | 2.52 | 3.31 | 2.25 | 3.81 |
| complement | 16,169 | 3,051 | 2.08 | 72.0 | 2.95 | 2.78 | 1.74 | 1.41 | 1.98 | 1.85 | 1.63 |

**mc=1 on the disjunction book: 2,489 @ 2.481 (all years >= 1.92; 2022
= 2.72)** vs the full $1+ book's 2.109 — as a one-slot priority filter
it adds +0.37 PF at 58% of slot-trips (3,808 slot-points vs 4,756).
Note the 2022 flip: mc=0 1.42 (the violence dilution) but mc=1 2.72 —
the slot naturally takes the better trips.

| disjunct | n total | PF total | n ONLY | PF only |
|---|---|---|---|---|
| v20 >= 140 | 3,049 | 4.14 | 279 | 2.49 |
| d20a < -28 | 3,284 | 3.82 | 552 | 2.31 |
| speed < -6 | 3,121 | 3.14 | 947 | 2.28 |
| d1s < -1 | 1,423 | 2.54 | 548 | 2.37 |
| dslo >= 6 | 9,893 | 3.16 | 3,357 | 2.14 |
| pah >= 28 | 6,225 | 3.55 | 81 | 1.22 |

**⭐ THE REAL FIND — the VOTE COUNT (nfire) is a HUMP:**

| voices firing | n | tkds | PF | win% |
|---|---|---|---|---|
| 0 | 16,169 | 3,051 | 2.08 | 72.0 |
| 1 | 5,764 | 1,527 | 2.20 | 74.5 |
| ⭐ 2 | 5,496 | 1,097 | **3.90** | 78.7 |
| ⭐ 3 | 1,790 | 487 | **4.29** | 79.4 |
| 4 | 844 | 236 | 3.57 | 76.5 |
| 5 | 271 | 86 | 2.98 | 69.7 |
| 6 | 23 | 19 | 2.02 | 82.6 |

**READING:** every single-voice slice is ≈ book-level (2.1-2.5) — the
disjunction's power is entirely in the OVERLAPS, and it peaks at 2-3
voices then DECLINES to 6/6 = 2.02: all-extremes-at-once = the
sustained-violence crash again (the contrast grammar as a vote count).
**nfire ∈ [2,4] = 8,130 @ ~3.9 / 1,600+ tkds — the vote count is the
composite the family has been looking for**: scale-free, census-fat,
one number, hump-shaped like everything real in this system. d1s = the
weakest voice (2.54 total); pah-only = 1.22 (its 81 orphans are noise).

**S42d — gap_60 < 4 added as the 7th voice (user):** union grows to
18,331 @ 3.00 / 3,016 tkds (60% of book); complement 12,026 @ 1.91.
**g60-only orphans = 4,143 @ 2.72 — the ONLY voice whose solo trips beat
book** (it's a background-QUALITY vote, not an extremity vote). The
hump SHIFTS RIGHT accordingly:

| voices (of 7) | n | tkds | PF | win% | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|
| 0 | 12,026 | 2,489 | 1.91 | 70.8 | 1.57 | 1.45 | 1.64 | 1.38 |
| 1 | 7,786 | 1,829 | 2.16 | 73.9 | 1.13 | 1.4 | 1.87 | 1.89 |
| 2 | 4,325 | 1,098 | 3.04 | 76.4 | 2.86 | 3.83 | 1.51 | 3.05 |
| ⭐ 3 | 3,883 | 792 | **4.50** | 80.2 | 4.29 | 2.62 | 3.15 | 5.92 |
| ⭐ 4 | 1,440 | 381 | **5.04** | 82.1 | 1.93 | 2.96 | 4.62 | 6.19 |
| 5 | 673 | 186 | 2.98 | 74.9 | **0.6** | 5.66 | 3.58 | 8.73 |
| 6 | 206 | 67 | 5.42 | 72.3 | — | — | 3.6 | 35.83 |
| 7 | 18 | 14 | 1.74 | 83.3 | — | — | — | 28.35 |

mc=1: any-of-7 = 3,017 @ 2.419; **nfire >= 2 = 1,812 @ 2.690 (+1.69
avg, THE best one-slot PF of any book-scale filter yet)**; >= 3 = 1,051
@ 2.597 / +1.79 avg. The all-extreme knife resurfaces at nfire = 5
(2022 = 0.6). **VERDICT: g60 belongs in the vote — peak moves to 3-4
(5,323 @ ~4.6 / 1,173 tkds combined); the count now mixes extremity
votes with one quality vote, and the mc=1 ladder gets its best rung
(2.69).**

**S42e — the CORRECTED design (user: g60 was meant as the UNIVERSE, not
a voice; dslo tightened >= 6 → >= 16):** 6 extremity voices {v20 >= 140,
d20a < -28, speed < -6, d1s < -1, dslo >= 16, pah >= 28}, tested inside
gap_60 < 4.

| universe | slice | n | tkds | PF | mc=1 |
|---|---|---|---|---|---|
| g60 < 4 | DISJUNCTION | 6,566 | 1,038 | **4.67** | 1,128 @ **3.097** (+1.88 avg) |
| g60 < 4 | complement | 5,011 | 962 | 2.77 | — |
| g60 < 4 | nfire >= 2 | 4,918 | 849 | ~5.3 | **821 @ 3.300 (+2.02 avg)** |
| full | DISJUNCTION | 11,585 | 2,040 | 3.33 | 2,087 @ 2.400 |
| full | nfire >= 2 | 7,580 | — | ~3.9 | 1,290 @ 2.603 |

nfire profile inside g60: 0 = 2.77 / 1 = 3.79 / **2 = 5.49 (571 tkds)**
/ **3 = 5.26 (278)** / 4 = 3.67 (2022 = 0.61 — the knife row) / 5 =
5.11 / 6 = 1.53. The hump peaks at 2-3 votes; the tightened dslo (>= 16
vs >= 6, riding the S41p [13,40) band) sharpened every tier vs S42c.

**⭐ VERDICT: the user's intended construction — extremity votes INSIDE
the clean-tape universe — beats the 7-voice mix everywhere: mc=1 nfire
>= 2 × g60 = 821 @ 3.300 = THE best one-slot book-scale number of the
program** (ladder of mc=1 books: full 2.109 → disjunctions 2.4-2.7 →
g60-book 2.891 → g60 × any-voice 3.097 → g60 × >= 2 votes 3.300).
Quality belongs in the UNIVERSE; extremity belongs in the VOTE.

## S42f — v20 \ d20a and d20a \ v20: the tails DIVERGE (2026-08-03)

**User (real-money intent declared on S42e): do the two main voices have
good independent trades?** Despite corr(v20, d20a) = 0.829 on the
CONTINUOUS features, at the voice thresholds (v20 >= 140 / d20a < -28)
the overlap is only partial — Jaccard 0.45:

| FULL BOOK | n | tkds | PF | win% | med | 2022 | 2023 |
|---|---|---|---|---|---|---|---|
| BOTH | 1,970 | 312 | 4.16 | 79.6 | +3.81 | 1.87 | 12.85 |
| v20 ONLY | 1,079 | 205 | 4.12 | 77.4 | +3.03 | **0.64** | 3.1 |
| d20a ONLY | 1,314 | 256 | 3.30 | 76.8 | +2.72 | 1.54 | 1.54 |
| neither | 25,994 | 4,342 | 2.31 | 73.6 | +2.0 | 1.63 | 1.72 |

| g60 < 4 | n | tkds | PF | win% | 2022 | 2023 |
|---|---|---|---|---|---|---|
| BOTH | 1,250 | 201 | 5.23 | 79.7 | 1.64 | 19.67 |
| v20 ONLY | 733 | 146 | 4.90 | 81.6 | **0.56** | 12.95 |
| ⭐ d20a ONLY | 630 | 123 | **6.76** | 83.0 | — (no losses) | 6.62 |
| neither | 8,964 | 1,494 | 3.33 | 77.9 | 7.6 | 2.14 |

**READING:** (a) both set-differences trade WELL — the voices are
genuinely two, not one: hot-vol-without-deep-leg (choppy violence) and
deep-leg-without-hot-vol (the long grind that has already cooled) are
different animals, both fadeable. (b) v20-only ≈ BOTH on the full book
(4.12 vs 4.16) — d20a adds NO conjunction bonus on top of v20
(family-mates don't stack, again); the value of keeping both is
COVERAGE, exactly what the vote structure uses. (c) on clean tape
**d20a-only = 6.76 / 123 tkds = the partition's best cell** (and
loss-free in 2022) while **v20-only carries the bear wart (2022 = 0.64
full / 0.56 g60)** — hot chop without a deep leg is the knife flavor;
deep-leg-cooled is the safe flavor. Sizing note for the vote: a v20
vote is worth less in bear regimes; a d20a vote is not.

## S42g — breach depth (d1s) revisited: < -1% overshoots; -0.75 is the voice (2026-08-03)

**User: is d1s < -1% too stringent?** Fine bands (full book):

| d1s band | n | tkds | PF | win% | med | 2022 | 2023 |
|---|---|---|---|---|---|---|---|
| [-0.05,0) | 6,440 | 2,942 | 2.31 | 74.0 | +1.92 | 1.62 | 2.07 |
| [-0.1,-0.05) | 3,832 | 2,251 | 2.37 | 73.6 | +1.99 | 2.58 | 1.24 |
| [-0.2,-0.1) | 5,600 | 2,793 | 2.52 | 73.9 | +2.08 | 1.49 | 2.32 |
| [-0.35,-0.2) | 5,535 | 2,856 | 2.7 | 75.2 | +2.15 | 1.91 | 1.97 |
| [-0.5,-0.35) | 3,325 | 2,143 | 2.59 | 74.1 | +2.24 | 1.71 | 1.92 |
| [-0.75,-0.5) | 2,878 | 1,877 | 2.55 | 73.4 | +2.25 | 1.19 | 1.52 |
| ⭐ [-1,-0.75) | 1,324 | 1,037 | **3.21** | 76.1 | +2.61 | 1.64 | 3.26 |
| [-1.5,-1) | 932 | 732 | 2.58 | 75.2 | +2.79 | **0.98** | 1.3 |
| [-2.5,-1.5) | 391 | 333 | 2.28 | 73.9 | +3.08 | **0.52** | 1.43 |
| < -2.5 | 100 | 85 | 3.33 | 74.0 | +3.2 | — | 9.58 |

Cumulative: < -0.35 = 2.65 / < -0.5 = 2.68 / **< -0.75 = 2.80 (2,747 @
1,612 tkds; g60 3.77)** / < -1 = 2.54 / < -1.5 = 2.47 / < -2.5 = 3.33
(85 tkds; g60 4.78/32). On g60 the cumulative plateau is FLAT 3.75-3.78
across [-0.75,-0.35].

**READING: YES, < -1 overshoots — it sits past the peak.** The axis is
the S42b speed-shape at the 1s scale: a drift up to the [-1,-0.75)
sweet band (3.21 / 1,037 tkds, all years >= 1.3), then the
1-1.5-2.5% smash-through bands SAG with bear warts (2022 = 0.98 / 0.52
— a bar crashing >1% through the prior 20m low = the crash
accelerating), then the terminal < -2.5 blow-off recovers (3.33/85).
**The d1s voice moves to < -0.75: +93% trips AND +0.26 PF vs < -1.**

**S42g addendum — d1s < -0.5 and its complement on g60 (user; real-money
universe): THE VOICE IS NULL ON CLEAN TAPE.**

| universe | side | n | tkds | PF | win% | 2022 |
|---|---|---|---|---|---|---|
| g60 < 4 | d1s < -0.5 | 1,957 | 908 | **3.78** | 79.3 | 1.05 |
| g60 < 4 | complement >= -0.5 | 9,620 | 1,711 | **3.78** | 78.5 | 7.19 |
| FULL | d1s < -0.5 | 5,625 | 2,645 | 2.68 | 74.4 | 1.15 |
| FULL | complement | 24,732 | 4,559 | 2.49 | 74.2 | 1.76 |

On the clean-tape book the voice and complement are IDENTICAL (3.78 =
3.78 = the g60 book PF — zero separation at ANY shallow threshold; the
S42g g60 plateau was book-level all along), and the voice CONCENTRATES
the bear wart (2022 = 1.05 vs 7.19). On the full book it's +0.19 at
best. **d1s DROPS from the g60 voice list** — the breach depth is
mildly informative on holey tape only; its S42c weakest-voice showing
is now fully explained. (The terminal < -2.5 g60 sliver 4.78/32 tkds =
playbook anecdote.)

## S42h — the 5-VOICE vote (d1s dropped) + 5s/10s flush speeds queued (2026-08-03)

**Dropping the null voice IMPROVED the vote** (g60 universe; voices =
{v20 >= 140, d20a < -28, speed < -6, dslo >= 16, pah >= 28}):

| votes (of 5) | n | tkds | PF | win% | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|
| 0 | 5,146 | 972 | 2.79 | 76.2 | 4.8 | 1.61 | 1.86 | 2.5 |
| 1 | 1,618 | 360 | 3.61 | 77.6 | 11.17 | 10.19 | 1.39 | 3.88 |
| ⭐ 2 | 3,024 | 530 | **5.91** | 83.1 | 12.89 | 3.53 | 5.51 | 9.13 |
| 3 | 1,124 | 230 | 4.87 | 81.1 | **0.54** | 3.19 | 4.83 | 8.38 |
| 4 | 509 | 107 | 3.68 | 76.4 | **0.66** | 6.97 | 2.47 | 95.65 |
| 5 | 156 | 37 | 4.12 | 69.2 | — | — | 4.35 | 22.82 |

**mc=1: nfire >= 1 = 1,051 @ 3.022; nfire >= 2 = 769 @ 3.622 / +2.13
avg — the program's best one-slot number again (3.300 → 3.622 just by
removing d1s's noise votes).** The knife moves to nfire >= 3 in 2022
(0.54/0.66). Engine: `vwap_5_prev`/`vwap_10_prev` baked (dv twins +
non-overlapping lags mirroring vwap_60_prev; record-only) for the 5s/
10s flush-speed study — base_v15 + v23_fast running.

**S42h addendum — the 5s/10s flush speeds: F7 REASSERTS (2026-08-03).**
`vwap_5_prev`/`vwap_10_prev` baked (**base_v15 = THE base, `v23_fast/` =
THE working parquet**, zero-diff 38,069). s5 ↔ s60 corr 0.49, s10 ↔ s60
0.61, s5 ↔ s10 0.85; medians −0.96 / −1.44%. **On g60 BOTH are FLAT
noise across deciles (s5 3.13-4.92, s10 3.2-5.32, no gradient) and the
fastest decile carries the bear wart (dec1 2022 = 0.48 / 0.29 — below
the 3.78 book)** — the sub-30s scale rule (F7: microstructure noise)
holds even for volume-weighted 5/10-bar vwap changes: no near-term voice
faster than the 1m clock. speed (1m) stays THE near-term feature.

| s5 dec (g60) | range % | n | tkds | PF | win% | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | -17.98..-2.19 | 1,158 | 423 | 3.13 | 75.6 | 0.48 | 4.06 | 1.75 | 3.71 |
| 2 | -2.19..-1.64 | 1,158 | 594 | 4.43 | 79.8 | 3.34 | 3.69 | 2.26 | 14.83 |
| 3 | -1.64..-1.33 | 1,158 | 629 | 3.54 | 78.4 | 5.72 | 1.94 | 2.69 | 4.64 |
| 4 | -1.33..-1.12 | 1,158 | 656 | 3.66 | 78.8 | 3.81 | 2.74 | 3.55 | 3.14 |
| 5 | -1.12..-0.95 | 1,158 | 689 | 3.69 | 79.2 | 7.83 | 1.57 | 3.05 | 3.7 |
| 6 | -0.95..-0.81 | 1,158 | 697 | 4.92 | 80.6 | 6.51 | 3.08 | 3.58 | 5.98 |
| 7 | -0.81..-0.67 | 1,158 | 702 | 3.44 | 76.9 | 16.23 | 2.39 | 2.84 | 3.66 |
| 8 | -0.67..-0.53 | 1,157 | 672 | 4.35 | 79.5 | 5.96 | 3.63 | 2.53 | 6.77 |
| 9 | -0.53..-0.38 | 1,157 | 618 | 4.44 | 79.7 | 10.07 | 6.82 | 2.34 | 5.21 |
| 10 | -0.38..-0.02 | 1,157 | 598 | 3.27 | 77.8 | 6.65 | 3.2 | 2.45 | 3.09 |

| s10 dec (g60) | range % | n | tkds | PF | win% | 2022 | 2023 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | -18.57..-3.11 | 1,158 | 347 | 3.46 | 76.9 | 0.29 | 4.04 | 1.73 | 4.83 |
| 2 | -3.11..-2.37 | 1,158 | 519 | 4.07 | 78.3 | 2.13 | 2.85 | 3.07 | 6.43 |
| 3 | -2.37..-1.98 | 1,158 | 591 | 3.75 | 78.3 | 5.78 | 1.58 | 2.9 | 4.05 |
| 4 | -1.97..-1.7 | 1,158 | 621 | 5.32 | 82.0 | 8.31 | 3.22 | 3.25 | 12.05 |
| 5 | -1.7..-1.47 | 1,158 | 643 | 3.88 | 79.8 | 5.83 | 3.67 | 2.8 | 4.85 |
| 6 | -1.47..-1.27 | 1,158 | 641 | 3.28 | 77.1 | 6.64 | 3.04 | 2.65 | 2.22 |
| 7 | -1.27..-1.07 | 1,158 | 651 | 3.36 | 76.5 | 8.64 | 2.26 | 2.36 | 3.41 |
| 8 | -1.07..-0.88 | 1,157 | 655 | 3.8 | 78.5 | 7.11 | 3.18 | 3.39 | 3.56 |
| 9 | -0.88..-0.67 | 1,157 | 599 | 4.35 | 81.9 | 7.54 | 3.18 | 2.44 | 6.03 |
| 10 | -0.67..-0.03 | 1,157 | 570 | 3.2 | 76.8 | 8.03 | 3.02 | 2.02 | 3.62 |

**--base-run VERIFIED:** one-month window (2024-01), flag vs the
17-flag hand CLI: **17,251 = 17,251, zero diff both directions.** The
canonical base command is now `FF_CANDIDATE_TABLE=flushfader_base_tkds
<binary> --base-run --out-dir ...` — new gates default OFF in the base
by construction.

**S42h coverage note (user):** on the g60 book (11,577 / 1,774 tkds) the
5-voice union fires on **6,431 trips = 55.5% (965 tkds = 54.4%),
capturing 69.1% of the book's net points**; the >= 2-vote core = 4,813 =
41.6% of trips (702 tkds) with 54.5% of net points. The zero-vote
remainder (5,146 @ 2.79) stays positive — the base-size floor under the
vote tiers.

**S42h coverage, FULL book:** union = 10,956 = 36.1% of trips (1,676
tkds = 35.3%) capturing 53.2% of net points; hump holds (0 = 2.08 / 1 =
2.51 / 2 = 4.21 on 838 tkds / 3 = 4.04 / 4 = 3.17 / 5 = 4.54 on 47
tkds). The votes fire mostly on clean tape — on the full book the union
doubles as a stealth liquidity filter; the g60-restricted form stays
the honest design.

**S42h final note — extremity IS liquidity-linked (user question):** the
v20 distribution on g60 = 75/95/126 (q25/med/q75) vs illiquid 62/78/98;
v20 >= 140 fires 17.1% vs 5.7% (3x), and EVERY voice fires 2-3.3x more
on clean tape (dslo 35.0 vs 10.5, pah 36.1 vs 10.9). Holey tape
mechanically suppresses extremity measures (EmaMa decay between sparse
slots, muted lagged-vwap speeds) and in-play names get continuity AND
violence together — the stealth-liquidity effect fully explained; the
g60 universe purifies the votes.


## S42i — volume-rate voice candidates: vol10rate no, quiet-volume (1m/20m < 1) = bull-regime sizing nudge (2026-08-03)

Question (user): does tightening `vol10rate` (10s/1m, spec floor 0.75)
warrant a 6th voice? Same for the 1m/20m volume rate. Universe = the
g60 book ($1+, gap_60 < 4) on `v23_fast`.

**vol10rate = (vol_10/10)/(vol_60/60) — fine bands:**

| band | n | tkds | pf | p21 | p22 | p23 | p24 | p25 | p26 | avg% | med% |
|------|--:|-----:|---:|----:|----:|----:|----:|----:|----:|-----:|-----:|
| [0.75,1.0) | 1,946 | 803 | 3.71 | 6.03 | 6.59 | 3.97 | 2.67 | 2.51 | 2.70 | 1.89 | 2.21 |
| [1.0,1.25) | 2,097 | 857 | 3.95 | 6.00 | 5.89 | 2.64 | 4.13 | 2.76 | 2.57 | 2.02 | 2.33 |
| [1.25,1.5) | 1,736 | 777 | 4.19 | 6.11 | 6.00 | 2.12 | 2.81 | 3.14 | 6.04 | 2.10 | 2.53 |
| [1.5,2.0) | 2,547 | 939 | **4.46** | 3.26 | 4.22 | 3.90 | 4.42 | 3.14 | 6.35 | 2.33 | 2.71 |
| [2.0,2.5) | 1,613 | 689 | 3.72 | 4.34 | 2.08 | 2.09 | 3.46 | 3.01 | 5.95 | 2.09 | 2.50 |
| [2.5,3.0) | 905 | 426 | 2.83 | 8.96 | 1.12 | 2.46 | 2.68 | 1.48 | 9.60 | 1.84 | 2.44 |
| [3.0,4.0) | 621 | 272 | 2.72 | 2.76 | 1.60 | 5.32 | 2.75 | 1.57 | 9.77 | 1.70 | 2.26 |
| [4.0,6.0) | 112 | 56 | 2.06 | 7.84 | NULL | 0.98 | NULL | 1.32 | 1.32 | 1.21 | 2.19 |

**NO VOICE.** Hump peaks mid-band [1.5,2.0) = 4.46 and the tail DECAYS
(2.83 / 2.72 / 2.06) with 2022 warts (1.12 / 1.60 / NULL). F7 again from
the volume side: a 10s burst faster than the 1m clock carries no
extremity premium. The 0.75 spec floor already sits at the right place.

**v1m20 = (vol_60/60)/(vol_1200/1200) — fine bands:**

| band | n | tkds | pf | p21 | p22 | p23 | p24 | p25 | p26 | avg% | med% |
|------|--:|-----:|---:|----:|----:|----:|----:|----:|----:|-----:|-----:|
| <0.75 | 1,270 | 367 | 4.47 | 2.86 | 7.14 | 3.61 | 5.01 | 4.50 | 4.66 | 2.29 | 2.98 |
| [0.75,1.0) | 1,637 | 515 | **5.23** | 4.08 | 3.68 | 5.55 | 3.66 | 4.20 | 8.33 | 2.45 | 2.77 |
| [1.0,1.25) | 1,740 | 599 | 3.40 | 4.51 | 1.27 | 4.97 | 3.15 | 2.54 | 6.90 | 1.92 | 2.52 |
| [1.25,1.5) | 1,683 | 589 | 3.05 | 5.81 | 2.24 | 3.89 | 2.66 | 1.66 | 3.68 | 1.60 | 1.97 |
| [1.5,2.0) | 2,607 | 697 | 3.53 | 4.70 | 11.00 | 2.73 | 4.31 | 1.78 | 3.91 | 1.96 | 2.44 |
| [2.0,2.5) | 1,411 | 437 | 3.80 | 4.45 | 4.93 | 1.19 | 3.08 | 2.89 | 13.98 | 2.08 | 2.42 |
| [2.5,3.0) | 612 | 221 | 3.96 | 7.32 | 23.26 | 3.46 | 2.65 | 3.62 | 3.91 | 2.14 | 2.43 |
| [3.0,4.0) | 499 | 151 | 3.98 | 21.53 | 3.76 | 1.54 | 2.27 | 2.72 | 3.57 | 2.37 | 2.23 |
| >=4.0 | 118 | 38 | 2.69 | NULL | NULL | 68.10 | 1.32 | 0.65 | 0.65 | 1.84 | 2.22 |

The signal lives at the **LOW** end: the last minute QUIETER than the
20m average = exhaustion-drip volume — the volume-side cousin of the
S42b exhaustion-drip speed cell. `f_vq = v1m20 < 1.0` fires 25.1% of
the g60 book, near-DISJOINT from the speed voice (Jaccard 4.0%;
20-30% vs the others), and lifts PF within every 5-vote level
(0: 2.66→3.65 / 2: 5.71→6.47 / 3: 4.27→6.37 / 5: 2.21→26.29 on 62).
The speed x quiet corner = 179 @ 7.43 / +4.38 avg — violent price on
quiet tape = pure air pocket.

**But it FAILS the mc=1 vote test.** 6-voice hump mc=1: nfire>=2 = 848
@ 3.289; nfire>=3 = 498 @ 3.439 w/ 2022 = 1.197 (the knife again).
Both below the 5-voice nfire>=2 = 769 @ 3.622. With six voices the
>=2 bar admits weaker pairs; >=3 over-concentrates into the 2022 knife.

**Its real role = sizing nudge.** Splitting the ACTUAL 5-voice mc=1
book (769) by f_vq: quiet = 285 @ 4.06 / +2.29 avg vs loud = 484 @
3.39 — but 2022 INVERTS (quiet 0.90 vs loud 4.69). In a bear the quiet
flush is the developing crash (contrast grammar, volume edition).
Treat f_vq like the v20 votes: a bull-regime up-size, discounted in
bear. **The 5-voice vote stands unchanged.**


## S42j — vchg (volat_20m / volat_20m_prev): the contrast grammar's third confirmation (2026-08-03)

First proper look at vchg (queued since the 08-02 close; only the S41z
<0.68 collapse-avoid = 1.65 was known). g60 book, 51 NULLs (cold prev
window) excluded.

**vchg fine bands, g60 book:**

| band | n | tkds | pf | p21 | p22 | p23 | p24 | p25 | p26 | avg% | med% |
|------|--:|-----:|---:|----:|----:|----:|----:|----:|----:|-----:|-----:|
| <0.68 | 1,224 | 238 | **2.47** | 2.11 | 3.44 | 3.15 | 2.85 | 1.96 | 1.52 | 1.52 | 2.03 |
| [0.68,0.85) | 3,865 | 695 | 3.83 | 5.49 | 1.86 | 3.83 | 6.07 | 2.54 | 2.63 | 2.03 | 2.54 |
| [0.85,1.0) | 4,173 | 755 | 4.14 | 7.27 | 12.45 | 1.74 | 2.63 | 2.01 | 24.15 | 2.18 | 2.55 |
| [1.0,1.2) | 1,828 | 349 | 4.04 | 3.09 | 11.01 | 8.19 | 2.15 | 6.29 | 4.50 | 2.10 | 2.40 |
| [1.2,1.5) | 334 | 72 | 3.59 | 4.90 | 2.59 | 1.82 | 4.88 | 24.53 | 3.31 | 2.14 | 2.27 |
| [1.5,2.0) | 82 | 14 | 7.08 | 7.96 | 1.20 | NULL | 26.61 | 29.36 | NULL | 3.03 | 2.01 |
| [2.0,3.0) | 16 | 4 | 40.09 | NULL | NULL | NULL | NULL | 18.60 | NULL | 3.67 | 4.19 |
| [3.0,5.0) | 4 | 2 | ∞ | NULL | NULL | NULL | NULL | NULL | NULL | 0.95 | 1.14 |

Mass sits in [0.68,1.2) (9,866/11,526). The expansion tail >=1.5 = 102
trips total — spectacular shards (7.08 / 40.09), NO VOICE possible.
The <0.68 collapse-avoid confirms on g60 (2.47, worst in 2025/26).

**vchg x v20 state — the derivative reads against the level:**

| v20 state | vchg band | n | tkds | pf | p22 | avg% |
|-----------|-----------|--:|-----:|---:|----:|-----:|
| cool <80 | collapse <0.85 | 819 | 208 | 2.91 | 2.16 | 1.30 |
| cool <80 | flat [0.85,1.2) | 2,511 | 509 | 4.16 | 8.40 | 1.68 |
| cool <80 | expand [1.2,2.0) | 234 | 46 | 3.88 | NULL | 1.72 |
| mid [80,140) | collapse | 2,847 | 500 | 2.78 | 6.77 | 1.54 |
| mid [80,140) | flat | 2,969 | 470 | 3.88 | 19.04 | 2.33 |
| mid [80,140) | expand | 160 | 31 | 5.38 | 0.21 | 3.37 |
| hot >=140 | collapse | 1,423 | 230 | **5.17** | **0.60** | 3.00 |
| hot >=140 | flat | 521 | 87 | 5.32 | 22.99 | 3.38 |
| hot >=140 | expand | 22 | 7 | 1.41 | 0.00 | 0.99 |

Cool/mid tape: collapsing vol = dead fade (2.78-2.91) — that's where
the S41z avoid lives. Hot tape: collapsing vol = exhaustion
confirmation (5.17) but carries the 2022 wart (0.60); hot + expand =
the developing crash (1.41). Same grammar as speed: **the sign of the
vol derivative flips value with the vol level.**

**On the traded 5-voice mc=1 book (769)** the collapse-avoid INVERTS:

| band | n | pf | p22 | p25 | p26 | avg% |
|------|--:|---:|----:|----:|----:|-----:|
| collapse <0.68 | 74 | 5.03 | 10.39 | 11.31 | 2.11 | 2.61 |
| flat | 663 | 3.73 | 1.57 | 2.86 | 6.03 | 2.11 |
| expand >=1.2 | 32 | 1.71 | 2.42 | 12.77 | 2.38 | 1.39 |

The vote book is already selected for extremity, so collapsing vol
there = exhaustion confirmation, not dead tape.

**Verdict: no spec change, no voice.** vchg is a regime lens: apply the
<0.68 avoid only on cool/mid tape; on the vote book leave collapse
alone (it's the good half) and treat expand >=1.2 as a mild down-size
(1.71, but 32 trips = shard). Third independent confirmation of the
contrast grammar (speed S42b, quiet-volume S42i, vol-derivative S42j).


## S42k — volat_slope: OLS of |30s slot return| — the self-contained vol trend (2026-08-03)

vchg's flaw (user): it needs a FULL prior 20m window (51 NULLs + cold
starts). Replacement: **OLS of |slot return| vs slot order over the
last 40 / 20 completed slot returns** — computed at signal from a
40-deep ring of the same |r| stream that feeds the F7 vol lock. New
record-only columns `volat_slope_20m` / `volat_r_20m` /
`volat_slope_10m` / `volat_r_10m` (nan below 3 returns; partial
windows allowed — filter on slot_count). Scale in SQL: **x2e4 = bp of
|r| per minute**. Baked as `v23_vs/` — **GRAND PARITY 38,069 exact,
zero ret_exit diff vs v23_fast; ZERO nulls** on the new columns
(v23_vs supersedes v23_fast as THE working parquet — same trips, more
columns). Quantiles (g60): s20 q05/med/q95 = -5.8/-0.1/+4.3; s10 =
-12.6/+1.7/+13.0 (the flush pumps the recent window).

**s20 fine bands, g60 book:**

| band | n | tkds | pf | p21 | p22 | p23 | p24 | p25 | p26 | avg% | med% |
|------|--:|-----:|---:|----:|----:|----:|----:|----:|----:|-----:|-----:|
| <-6 | 543 | 116 | **9.05** | 13.04 | **0.20** | 14.59 | 6.38 | 34.91 | 13.42 | 3.61 | 3.71 |
| [-6,-4) | 784 | 171 | 2.46 | 4.08 | 21.52 | NULL | 2.22 | 1.33 | 6.46 | 1.52 | 2.46 |
| [-4,-2) | 1,625 | 346 | 4.31 | 3.80 | NULL | 2.17 | 7.56 | 2.68 | 6.04 | 2.23 | 2.59 |
| [-2,0) | 2,958 | 625 | 4.03 | 3.91 | 7.11 | 4.64 | 3.89 | 1.91 | 6.06 | 1.89 | 2.21 |
| [0,2) | 3,362 | 717 | 3.21 | 8.49 | 2.81 | 1.19 | 1.83 | 2.54 | 3.35 | 1.82 | 2.41 |
| [2,4) | 1,594 | 361 | 4.05 | 7.03 | 2.89 | 6.95 | 5.18 | 2.29 | 6.52 | 2.00 | 2.31 |
| [4,6) | 511 | 110 | 3.24 | 1.40 | 3.87 | 1.83 | 13.82 | 4.39 | 3.22 | 2.36 | 3.00 |
| >=6 | 200 | 41 | 5.55 | NULL | **0.00** | 9.52 | 8.13 | 11.86 | 1.53 | 4.19 | 4.78 |

Both extremes are bull monsters with 2022 knives — because s20 is
v20-entangled (corr -0.45: deep contraction follows a vol spike).

**s10 fine bands:** milder version of the same (extremes 4.50 / 5.18,
middle 2.9-3.9). **sdiff = s20 - s10:** <-12 (the last 10m ramping much
faster than the 20m trend) = 6.09/737 robust (p21 67.4, p22 3.23);
[3,6) = 6.26 but its 2022 = 81.9 and [6,12)'s = 1624 are shards; middle
flat 3.3-3.5. Fine bands in run50.sql.

**THE table — s20 x v20 state (contrast grammar, 4th confirmation):**

| v20 state | s20 band | n | tkds | pf | p22 | avg% |
|-----------|----------|--:|-----:|---:|----:|-----:|
| cool <80 | contract <-2 | 213 | 63 | **2.45** | NULL | 1.13 |
| cool <80 | flat [-2,2) | 2,574 | 553 | 3.26 | 5.40 | 1.42 |
| cool <80 | expand >=2 | 809 | 195 | **8.20** | 5.39 | 2.27 |
| mid [80,140) | contract | 1,624 | 319 | 3.39 | 19.24 | 1.90 |
| mid [80,140) | flat | 3,087 | 545 | 3.60 | 10.75 | 2.00 |
| mid [80,140) | expand | 1,287 | 224 | 2.93 | 3.51 | 2.02 |
| hot >=140 | contract | 1,115 | 185 | **5.98** | **5.84** | 3.09 |
| hot >=140 | flat | 659 | 125 | 3.99 | 0.24 | 2.82 |
| hot >=140 | expand | 209 | 43 | 5.70 | 0.43 | 3.83 |

Two star cells, BOTH 2022-positive: **cool + expand = 8.20** (vol
igniting off a quiet base, 195 tkds) and **hot + contract = 5.98 w/
p22 5.84** (exhaustion after the storm). The hot 2022 knife lives in
flat/expand (0.24/0.43), NOT in contraction — the vchg hot+collapse
wart (p22 0.60) does not transfer (s20-vchg corr only 0.32; trend
WITHIN the window vs level ratio ACROSS windows are different facts).
Cool + contract = 2.45 = the dead-fade avoid.

**On the traded 5-voice mc=1 book (769):** contract <-2 = **247 @ 5.07**
(p22 3.40 / p25 3.98 / p26 9.95), flat = 368 @ 3.08, expand = 154 @
3.32. A clean all-weather up-size lens on the vote book — unlike
vchg-collapse it needs no bear discount.

**Verdict:** volat_slope_20m REPLACES vchg wholesale (self-contained,
zero NULLs, cleaner grammar, 2022-safe star cells). Roles: the
s20 x v20 contrast pair {cool+expand, hot+contract} = the vol-family
sizing lens; cool+contract = avoid; sdiff <-12 = robust secondary
(6.09/737). No spec change, no new voice (extreme bands carry 2022
knives at the tails where they'd bind).


## S42l — ⭐⭐ ramp ADOPTED: the 6-VOICE VOTE + the rising concurrency ladder (2026-08-03)

User: "we definitely want that. Who doesn't want more net?" — and mc=2/3
are in trading scope given the mc=0 → mc=1 gap. **THE VOTE is now SIX
voices on the g60 universe:**

    {v20 >= 140bp, d20a < -28%, speed < -6%, dslo >= +16%,
     pah >= +28%, ramp: (volat_slope_20m - volat_slope_10m)*2e4 < -12}

nfire >= 2 = the book. Expansion candidates s20 >= 2/4 REJECTED as
voices (invert with vote count — conditional info belongs to sizing);
ramp = the one voice-shaped expansion measure (recent 10m |r| trend
steeper than the 20m trend = the flush climaxing NOW — timing, not
state): lifts at every 5-vote level (0: 4.82 / 1: 5.10 / 2: 9.20 /
4: 19.17 / 5: 19.59), Jaccard <= 17 vs every sitting voice, robust
every year (p22 3.23 in its band).

**The concurrency ladder on the 6-voice >= 2 book (g60, $1+):**

| mc | n | PF | win% | avg% | slot pts | 2022 |
|---:|--:|---:|-----:|-----:|---------:|-----:|
| 1 | 807 | 3.570 | 77.9 | +2.11 | 1,703 | 2.150 |
| 2 | 1,497 | 3.712 | 78.5 | +2.17 | 3,248 | 2.180 |
| 3 | 2,078 | 3.922 | 79.1 | +2.25 | 4,676 | 2.272 |

PF and avg% RISE with depth (greedy mc=1 takes the chronologically
first trip, not the best; deeper slots breathe past it), 2022 stable,
every year >= 2.15 at every depth. mc=3 = 2.7x the mc=1 points at
HIGHER quality — the real-money case for multi-slot. (5-voice mc=1
reference was 769 @ 3.622; the 6th voice trades -0.05 PF at mc=1 for
+4% points and the cleaner ladder above.)


## S42m — ⭐⭐ THE 7-VOICE VOTE: |esf| >= 0.5 adopted (2026-08-03)

User revisited esf (eff_since_flow — Kaufman efficiency of the leg
since its first low, S41b's mc=1 star that was rejected in isolation)
as a VOICE. |esf| >= 0.4 tested first: voice-shaped (fires 40%,
Jaccard <= 24 vs all six voices — only 4.3 vs ramp; lifts within EVERY
6-vote level incl. 6-of-6+esf = 15.97/21) but cost -0.18 PF at every
mc depth for +10% points (marginal set 338 @ 3.32).

**|esf| fine bands, g60 book:**

| band | n | tkds | pf | p21 | p22 | p23 | p24 | p25 | p26 | avg% |
|------|--:|-----:|---:|----:|----:|----:|----:|----:|----:|-----:|
| <0.3 | 4,016 | 714 | 3.38 | 4.15 | 7.31 | 1.91 | 3.23 | 2.40 | 7.04 | 1.98 |
| [0.3,0.4) | 2,923 | 645 | 3.43 | 4.57 | 6.34 | 4.36 | 3.01 | 1.56 | 6.37 | 1.85 |
| [0.4,0.5) | 2,071 | 463 | 4.23 | 5.88 | 5.06 | 6.19 | 4.07 | 5.58 | **1.29** | 2.13 |
| [0.5,0.6) | 1,180 | 293 | **5.95** | 7.57 | 6.87 | 4.93 | 5.45 | 4.56 | 6.89 | 2.34 |
| [0.6,0.7) | 639 | 161 | 3.88 | 4.03 | 2.02 | 3.79 | 1.96 | 1.48 | 10.11 | 2.10 |
| [0.7,0.8) | 377 | 88 | **2.46** | 4.65 | 0.48 | 0.05 | 2.84 | 2.19 | 8.31 | 1.84 |
| >=0.8 | 369 | 98 | **12.84** | 27.43 | 4.54 | 5.69 | 4.00 | 9.19 | 306.41 | 3.05 |

Non-monotone: [0.5,0.6) peak (all years >= 4.5), [0.7,0.8) = the trap
band, >=0.8 = the virgin-efficiency star. **User: "What if we made it
0.5?" — 0.5 DOMINATES 0.4**: the [0.4,0.5) diluter drops out, the trap
band is too thin (377 book-wide) to dent the >= 2 arithmetic.

**The ladder (g60, >= 2 votes, $1+):**

| construction | mc=1 | mc=2 | mc=3 |
|---|---|---|---|
| 6-voice | 807 @ 3.570 / 1,703 pts | 1,497 @ 3.712 / 3,248 | 2,078 @ 3.922 / 4,676 |
| 7v esf>=0.4 | 921 @ 3.375 / 1,860 | 1,710 @ 3.558 / 3,591 | 2,368 @ 3.737 / 5,162 |
| **7v esf>=0.5** | **879 @ 3.570 / 1,846** | **1,629 @ 3.710 / 3,535** | **2,257 @ 3.880 / 5,056** |

esf >= 0.5 = **free volume**: +8% slot points at ZERO PF cost (mc=1
identical 3.570; mc=3 -0.042), 2022 BETTER at every depth (mc=3 2.370
vs 2.272). Marginal set at mc=3 = **196 @ 4.03 / p22 6.21 / +466 pts**
— the additions are better than the book they join (4.03 > 3.88).
(0.4's marginal set was 338 @ 3.32 — 0.5 keeps the best 60% of the
additions and drops exactly the dilution.) Avoid >= 3 as the bar
(esf's 2022 knife at high counts: 1.45 at mc=1).

**⭐⭐ THE VOTE (canonical, real-money construction):**

    g60 universe (gap_60 < 4), $1+, nfire >= 2 of SEVEN:
    {v20 >= 140bp, d20a < -28%, speed < -6%, dslo >= +16%, pah >= +28%,
     ramp (volat_slope_20m - volat_slope_10m)*2e4 < -12, |esf| >= 0.5}

    mc=1  879 @ 3.570 / +2.10   mc=2  1,629 @ 3.710 / +2.17
    mc=3  2,257 @ 3.880 / +2.24  — 2022 >= 2.17 at every depth.


## S42n — the HALT study: first-halt aftermath = the program's best cell; cascades knife only <20m (2026-08-03)

Question (user): is the ht=1 edge confined to the immediate 20m
aftermath (gap_1200 > gap_adj_1200 = a halt hole inside the 20m
window) or does it persist? Lookahead scare resolved first:
`halts_today` is the RUNNING counter, incremented at each resume
classification (Intraday.fs:1101) and recorded at signal (:1711) —
"exactly N halts so far" is knowable live; the ht=1 slices are CAUSAL.
(Empirically 2/260 ht=1 tkds ever produce a later ht>=2 trip —
cascade days rarely signal again under the spec.)

**ht=1, g60, decay by secs_since_halt:**

| since halt | n | tkds | pf | p22 | avg% | med% |
|---|--:|-----:|---:|----:|-----:|-----:|
| <10m | 53 | 9 | 25.12 | 0.0 | +3.44 | +3.53 |
| [10,20m) | 177 | 28 | **201.28** | — | +5.19 | **+5.24** |
| [20,40m) | 307 | 52 | 7.12 | 3.41 | +2.56 | +2.84 |
| [40,80m) | 510 | 79 | 4.98 | 1004 | +2.24 | +2.94 |
| [80,160m) | 404 | 76 | 6.73 | 2.08 | +2.24 | +2.17 |
| >=160m | 408 | 57 | 3.17 | 0.54 | +2.15 | +2.50 |

The "2022 = 0.0" in <10m = ONE ticker-day (BWV 2022-08-18, six trips
58-64 SECONDS after resume, -4.3 pts) — the re-halt instability strip,
not a bear effect. The [10,20m) golden window: 90-100% win every year,
~4 tkds/yr (A++ rarity class).

**<20m post-resume by halt NUMBER (the ht>=1 rerun's payoff):**

| tier | n | tkds | pf | p22 | avg% | med% | win% |
|---|--:|-----:|---:|----:|-----:|-----:|-----:|
| ht=1 (first) | 230 | 35 | **91.6** | 0.0 | +4.79 | +4.84 | 91.3 |
| ht=2 | 42 | 8 | 11.94 | NULL | +2.12 | +1.15 | 71.4 |
| ht=3 | 77 | 13 | **1.25** | 0.59 | +0.48 | +0.59 | 59.7 |
| ht>=4 | 389 | 51 | **2.09** | 0.08 | +1.91 | +3.31 | 70.4 |

**[20,80m):** ht=1 5.64 / ht=2 8.77 / ht=3 3.68 / ht>=4 5.54 — all
healthy. **THE GRAMMAR: the halt number matters ONLY in the immediate
aftermath.** First-resume fade = maximum-fear MR (wide LULD bands, the
crowd just watched it halt); third-plus-resume fade <20m = the
cascade knife (still in the elevator; the S41 ht>=4 = 1.40 avoid
decomposes into this zone). Clean lens: **ht=1 x ssh in [2,20m)**
(sub-2m = re-halt strip). ht=1 stays elevated (~5-7) out to ~160m —
the edge is front-loaded but persists ~2.5h.

Caveat for the halt book: exits assume our resting/MOC fills execute —
a position that re-halts INTO the close books its last-bar price; live,
size the <40m post-halt trades knowing a re-halt can trap the exit.


## S42o — ⭐⭐ SPEC v2.4 BAKED: the cascade-knife gate (2026-08-03)

User: "straight up just add this to the filters list as a negation of
this condition." **SPEC v2.4 = v2.3 + reject signal iff halts_today >=
3 AND secs_since_halt < 1200.** Engine gate `cascadeOk` mirrors the
recorded columns exactly (both causal running state); flags
`--cascade-halt-count` (default 3, 0 = off) / `--cascade-window-sec`
(default 1200); `--base-run` neutralizes it; banner = SPEC v2.4.

**GRAND PARITY exact:** forecast from v23_vs = 605 knife-zone trips →
`v24_reference/` = **37,464 = 37,464**, zero asymmetric rows, zero
ret_exit diff on survivors. **v24_reference/ = THE reference** (v23_vs
retains the removed trips for halt-zone research).

Also settled (user question): ht=1 ssh<2m = the six BWV trips on g60
(one tkd, 100% losers, -4.3 pts). ⚠ **The "[2,5m) is empty" claim made
here was WRONG as stated — see S42p §5 for the correction and the real
(and better) result: the emptiness is ht=1-CONDITIONAL and structural.**
The golden window is empirically **[5,20m): 47 @ 59.0 then 177 @ 201.3**.

**New canonical books ($1+):** full = 29,814 @ 2.542 (was 2.528);
g60 = 11,111 @ 3.977 / p22 3.98 (was 3.778).

**The 7-voice vote ladder, v2.3 → v2.4:**

| mc | v2.3 | v2.4 | 2022 |
|---:|------|------|-----:|
| 1 | 879 @ 3.570 / +2.10 | **829 @ 4.174 / +2.22** | 2.362 |
| 2 | 1,629 @ 3.710 / +2.17 | **1,534 @ 4.229 / +2.26** | 2.480 |
| 3 | 2,257 @ 3.880 / +2.24 | **2,118 @ 4.327 / +2.31** | 2.578 |

**The removed trips were SLOT THIEVES**: they held mc slots during
exactly the cascade windows and lost — PF +0.45 at mc=3 for -3% slot
points; win% +1.0-1.2 at every depth; 2022 +0.21-0.31. (Control logic
per the lookahead protocol: removing a bad-but-causal zone should
improve or leave flat — it improved everywhere.)


## S42p — the FORBIDDEN STATE: ht=1 has NO [2,5m) window; and the real halt risk is being CAUGHT HOLDING (2026-08-03)

User: "I think halt cascades have a very peculiar pattern so that 2m
might just be the boundary to clip sequential halt cascades."
**Hypothesis CONFIRMED and sharpened — but the boundary is 5m, not 2m,
and it is a boundary of EXISTENCE, not of quality.**

### 1. The literal question: ht=1 AND ssh < 2m

Seventeen trips on **three ticker-days in seven years** (BWV 2022-08-18
g60 6 @ -4.3 pts + illiq 2 @ -6.2; WORX 2020-04-15 1 @ -3.5; ZENA
2025-06-05 8 @ +40.1) — net ~ +3 pts. Gating it on top of v2.4:
mc=1 829 @ 4.174 -> **827 @ 4.183**. Two trips, +0.009 PF: below the
noise floor. **No gate** (the disproportion test in reverse — a filter
touching 0.02% of the book SHOULD NOT move PF; this one correctly
doesn't). The BWV cluster was small losses, not a cascade victim
(hold_gap 5s — the tape never went dark on them).

### 2. ⭐ THE FORBIDDEN STATE (the peculiar pattern)

Signals in the first 20m after a resume, FULL book, by halt number:

| zone | ht=1 | ht=2 | ht>=3 | ht=1 tkds |
|------|-----:|-----:|------:|----------:|
| <2m | 17 | 3 | 37 | 3 |
| **[2,5m)** | **0** | 6 | 33 | **0** |
| [5,10m) | 47 | 0 | 121 | 8 |
| [10,20m) | 187 | 57 | 352 | 31 |

**ht=1 x [2,5m) is EMPTY in BOTH books across 7 years.** Under any
smooth interpolation between the neighbouring bands the expected count
is 25-28 (P(0) ~ 1e-11) — this is structural, not sampling. The zone
itself is NOT empty: 39 trips live there, every one of them ht>=2.

**Mechanism — LULD re-trigger relabeling.** A stock still weak enough
to print new 20m lows 2-5 minutes after its first resume trips the
NEXT halt. That (a) blanks the tape, so no bar exists to signal on, and
(b) increments the counter, so anything after it is ht>=2 BY
CONSTRUCTION. There is no "first-halt stock still collapsing at 3
minutes" state in nature — it has already become a second-halt stock.
The [2,5m) survivors are exactly the cascade tiers riding between
halts (64.1% of them eat another >=300s hole during the hold).

**Consequence — the A++ golden window is a CASCADE-SURVIVORSHIP filter,
and a causal one.** ht=1 x ssh [5,20m) isn't magic timing: by 5 minutes
past resume with no second halt, the tape has ALREADY run the cascade
test and the stock passed. The market does the filtering; we just read
the clock. (This also explains the shape: 6 trips at <2m = before the
test, 0 in [2,5m) = during the test, 224 at [5,20m) = after it.)

### 3. Is the v2.4 cascade gate correctly SHAPED?

Post-hoc gate shapes on v23_vs, 7-voice vote, g60, mc=1
(shape (a) reproduces the baked engine gate exactly = control):

| shape | n | PF | 2022 |
|-------|--:|---:|-----:|
| **(a) ht>=3 AND <20m — BAKED** | 829 | **4.174** | 2.362 |
| (b) ht>=3 AND [5,20m) (spare <5m) | 833 | 4.137 | 2.362 |
| (c) ht>=3 AND <40m | 795 | 4.214 | **2.128** |
| (d) ht>=2 AND <20m | 823 | 4.148 | 2.353 |
| (e) (a) + clip all <2m | 827 | 4.183 | 2.406 |

Nothing beats it: (c) buys +0.04 PF by giving up 34 trips AND 0.23 of
2022. **The gate is at a local optimum and is not knife-edge tuned** —
every neighbouring shape lands within 0.05 PF.

### 4. ⭐⭐ THE REAL HALT RISK: caught holding, before the first halt

`hold_gap = (exit_sec - entry_sec) - bars_held` = missing seconds
DURING the hold. On g60 (continuous tape) a >=300s hole IS a halt.
**This is a post-hoc forensic, NOT a gate — it is pure lookahead.**

ht=0 population (before the day's first halt), g60:

| hold | n | % | PF | win% | avg% | worst% |
|------|--:|--:|---:|-----:|-----:|-------:|
| clean | 6,132 | 84.3 | 5.50 | 80.9 | +2.14 | -16.7 |
| pause (58-300s) | 923 | 12.7 | 2.11 | 68.9 | +1.15 | -27.7 |
| **re-halt (>=300s)** | **218** | **3.0** | **0.07** | **17.4** | **-6.06** | **-38.5** |

Inside the A++ golden window (ht=1, ssh [5,20m)), g60:

| hold | n | % | PF | win% | avg% | worst% |
|------|--:|--:|---:|-----:|-----:|-------:|
| clean | 216 | 96.4 | 139.7 | 93.5 | +5.01 | -1.2 |
| re-halt (>=300s) | 8 | 3.6 | ∞ | **100.0** | +2.81 | +0.1 |

**THE ASYMMETRY: it matters which DIRECTION the halt that catches you
is going.** Pre-first-halt, the halt that traps a long is the DOWN halt
the stock is collapsing into — 17% win, -6.06% avg, and it owns the
book's worst trips (-38.5% = the two-book view's worst trip). In the
post-resume bounce, the halt that catches you is the UP halt of the
snap-back — 100% win. **The golden window carries NO hidden re-halt
tax** (correcting the caution filed at S42n: size it on its merits).

3% of ht=0 trips carry -1,321 net points of damage. They are not
gateable directly, but they ARE predictable in principle: LULD bands
are a known, causal function of price tier and time of day, so
**distance-to-lower-LULD-band at signal** is a buildable feature.
Queued as the highest-value halt follow-up.

### 5. CORRECTION to S42o

S42o stated "[2,5m) contains ZERO trips in 7 years — the 20m-low
channel can't re-arm that fast post-resume." **Both halves were wrong:**
the claim was measured on g60 only (the full book holds 39 trips there)
and the mechanism is LULD re-trigger relabeling, not channel warmth.
The correct statement is the ht=1-conditional one in section 2 above.


## S42q — halts_today vs a 20m-WINDOWED halt count: the DAY count wins decisively (2026-08-03)

> ⚠ **METHOD CAVEAT (user caught, same day): the "halts in the window"
> split below is a PROXY, not a count.** It uses `gap_1200 -
> gap_adj_1200` = `haltOverlap 1200` = halt **SECONDS** in the trailing
> 20m, bucketed at 400s. Single halts range 0-599s (median 299 = the
> LULD pause), so `>=400s` does NOT mean two halts — 37 g60 trips with
> `halts_today = 1` (definitionally ONE halt) sit in that bucket.
> **Surviving:** the `<400s` direction (two halts would have to average
> <200s each; only 68/230 single halts are that short), which is what
> the verdict rests on. **Retracted:** the ">=400s = acute cascade"
> cell. Proper bar-indexed `halts_1200` / `halts_600` COUNT columns
> baked in **S42r — MEASURED, and the verdict HOLDS**: day count 142.58
> vs windowed 3.01 (47x). The proxy estimated 422 trips @ 1.55; measured
> is 383 @ 1.48 — close enough that the S42q conclusion was sound, but
> it could not be known so without the count. The retracted ">=400s"
> cell is also REHABILITATED there (acute cascade = 5.44, measured).


User: "whether we'd be better off using the ht features as we're doing
now, or whether we should consider only the halts in the last 20m.
We'll have to implement it into the engine. Before that I want to see
the profit factors." **The tables say don't build it** — the engine
change would destroy the A++ cell. No bake needed.

### The grid: PF by halt-recency x halt-count (g60, $1+)

| zone | n ht=1 | PF ht=1 | n ht=2 | PF ht=2 | n ht>=3 | PF ht>=3 | n all | PF all |
|------|-------:|--------:|-------:|--------:|--------:|---------:|------:|-------:|
| <2m | 6 | 0.00 | 2 | 0.00 | 6 | ∞ | 14 | 3.27 |
| [2,5m) | 0 | — | 6 | ∞ | 33 | 3.52 | 39 | 4.65 |
| [5,10m) | 47 | **59.01** | 0 | — | 116 | **1.67** | 163 | 2.29 |
| [10,20m) | 177 | **201.28** | 34 | 9.40 | 311 | **1.95** | 522 | 3.81 |
| [20,40m) | 307 | 7.12 | 244 | 6.76 | 259 | 4.13 | 810 | 5.73 |
| [40,80m) | 510 | 4.98 | 134 | 17.59 | 408 | 5.80 | 1,052 | 5.96 |
| >=80m | 812 | 4.18 | 272 | 4.15 | 620 | 3.47 | 1,704 | 3.86 |
| no halt today | 7,273 | 3.43 | — | — | — | — | 7,273 | 3.43 |

**Table convention (user, 2026-08-03):** `∞` = trips present but ZERO
losers, so the PF denominator is 0 — always read it against `n`; `—` =
no trips at all. Both `∞` cells here are ONE ticker-day apiece: the
<2m/ht>=3 cell is 6 trips on 1 tkd, and the [2,5m)/ht=2 cell is CCIV
2021-01-15, six signals on CONSECUTIVE SECONDS (46591-46596) all
exiting at target +6.04% to +6.94% — which mc=1 collapses to a single
trade. **Infinities of census, not of edge.** ⚠ NULL/∞ in the per-YEAR
columns (p21..p26) elsewhere in this doc stays ambiguous — those
tables carry no per-year `n`, so a blank there may mean "no trips that
year" OR "no losers that year". Check the census before quoting one.

Collapsed (panel O), g60: **halt >20m ago** = ht=1 4.80 / ht=2 6.05 /
ht>=3 4.15 — count-independent and ALL above the no-halt 3.43. **halt
within 20m** = ht=1 91.6 / ht=2 11.94 / ht>=3 1.94 — a 47x spread.
So the count only speaks in conjunction with a recent resume; a stale
halt is a mild positive whatever the count. (Illiquid tape differs:
there ht>=3 stays bad even when stale, 1.27 vs 2.03 baseline.)

### Why the windowed count LOSES

Split the blocked set (ht>=3 AND ssh<20m) by halt-seconds inside the
trailing 20m window (`gap_1200 - gap_adj_1200`), g60:

| local structure | n | tkds | PF | avg% | net pts |
|-----------------|--:|-----:|---:|-----:|--------:|
| ~one halt in window (<400s) | 407 | 60 | **1.49** | +1.00 | +406 |
| >=400s of halt in window | 59 | 7 | ∞ | +6.35 | +375 |

**The locally-SPARSE ones are the bad ones.** A stock that halted 3+
times today but only once in the last 20 minutes is a serial breaker
grinding down all session — exactly the population a windowed counter
would re-label as "ht=1" and hand to the golden-window logic.

**THE COUNTERFACTUAL** — inside the golden window (ssh [5,20m), ~one
halt in the trailing 20m), g60:

| | n | tkds | PF | win% | avg% | net pts |
|---|--:|-----:|---:|-----:|-----:|--------:|
| DAY-count ht=1 (what we trade now) | 187 | 30 | **106.46** | 92.5 | +4.40 | +823 |
| day-count ht>=2, ~1 halt in window (windowed ADDS) | 422 | 62 | **1.55** | 65.6 | +1.04 | +438 |
| **BLENDED = what a windowed counter trades** | 609 | 92 | **2.56** | 73.9 | +2.07 | +1,261 |

**A 20m-windowed halt count takes the A++ cell from PF 106 to PF 2.56**
by diluting 187 trips at 92.5% win with 422 trips at 1.55. The golden
window's edge IS the day-level statement "this stock has halted exactly
ONCE today" — a clean name that had one violent LULD event and is
snapping back. "Once in the last 20 minutes" is a completely different
and much weaker claim.

### Verdict

**Keep `halts_today` x `secs_since_halt`. Both dimensions are
load-bearing and neither is replaceable by the other:** the count is a
DAY-CHARACTER statement (clean name vs serial breaker), the clock is a
CASCADE-SURVIVORSHIP statement (S42p). No engine change.

Noted but NOT acted on: within the blocked set, the >=400s-in-window
cell (59 trips, no losers) suggests the knife is really "serial breaker
AND locally sparse". It is 7 ticker-days across 2023-2026 — far too
thin to carve a gate exception from, and doing so would be textbook
overfitting. Recorded for a future look if the cell grows.


## S42r — ⭐ the halt count MEASURED: bar-indexed windowed count built, day count wins by 47x (2026-08-03)

S42q answered the windowed-vs-day question with a PROXY (halt SECONDS
bucketed at 400s). User caught it, then specified the right design
twice: count halts in the last 1200 **BARS** (not seconds), and use a
**SumMa of a per-bar indicator** (not interval scanning).

**Implementation** (`halts_1200` / `halts_600`, record-only): 1 into a
`SumMa` on every bar that classifies a halt, 0 otherwise — the rolling
sum IS the count, O(1), the same shape as the existing
`big_gap_runs_1200`. **Bar-indexed is the load-bearing choice:** a halt
REMOVES seconds from the tape, so a wall-clock window shrinks exactly
when a cascade is running (a 300s pause eats 25% of a 1200s window) —
the first, wall-clock version degenerated to 0/1 everywhere. The bar
window stretches back THROUGH the holes, where the cascade lives.

Probe validation (every boundary independent): ssh <10m -> (1,1);
ssh [10,20m) -> (**1,0**) — inside the 1200-bar window, outside the
600; ssh [20,40m) -> (0,0); CYN 2025-06-26 with 15 halts but 1.8h
since resume -> 0. Bake `v23_hcount/` (--cascade-halt-count 0) =
**GRAND PARITY 38,069 exact vs v23_vs, zero ret diff.**

**The windowed count DOES reach >=2** (the wall-clock one never did):
191 trips / 32 tkds over 7 years — 109 at 2, 71 at 3, 11 at 4.

### THE MEASURED ANSWER — golden window (ssh [5,20m), g60)

| definition | n | tkds | PF | win% | avg% |
|------------|--:|-----:|---:|-----:|-----:|
| **DAY count = 1 (what we trade)** | 224 | 34 | **142.58** | 93.8 | +4.93 |
| WINDOWED count = 1 (the proposal) | 607 | 87 | **3.01** | 75.6 | +2.39 |
| the trips windowed-only ADDS | 383 | 53 | **1.48** | 65.0 | +0.90 |

**A 47x collapse.** (S42q's proxy estimated 422 trips @ 1.55 -> blended
2.56; measured is 383 @ 1.48 -> 3.01. The proxy verdict was right and
is now MEASURED.)

### Why — the windowed count cannot see the difference

Recently-resumed trips (ssh<20m, g60), by BOTH features:

| windowed | day count | n | tkds | PF | avg% |
|----------|-----------|--:|-----:|---:|-----:|
| = 1 | ht<=2 | 270 | 42 | **59.04** | +4.36 |
| = 1 | ht>=3 | 364 | 51 | **1.46** | +0.92 |
| >= 2 | ht<=2 | 2 | 1 | ∞ | +6.01 |
| >= 2 | ht>=3 | 102 | 15 | **5.44** | +4.36 |

`windowed = 1` contains BOTH the best population (59.04) and the worst
(1.46) — it cannot separate them; the DAY count does, perfectly. The
day count is a statement about the NAME ("clean stock that had one
event" vs "serial breaker"); the windowed count is a statement about
the last 20 minutes of tape, which both populations share.

Also measured: the **acute** cascade (windowed >= 2) is GOOD — 102 @
5.44 / +4.36%. ⚠ **This paragraph's attribution is WRONG — see S42s.**
The 102 is a blend of a 59-trip no-loser cell and a 43-trip 1.70 cell,
split by halt DURATION, not by the count; and the >=400s cell was never
contaminated in this population. The count is not the operative
variable.

### The refined gate — TESTED and REJECTED

Spare the acute cascades: block `ht>=3 AND ssh<20m AND halts_1200 = 1`
(the spread-out serial breaker) instead of all of `ht>=3 AND ssh<20m`.
7-voice vote, g60:

| gate | mc=1 | 2022 | mc=3 | 2022 |
|------|------|-----:|------|-----:|
| **v2.4 as baked** | **829 @ 4.174** | 2.362 | **2,118 @ 4.327** | 2.578 |
| refined (spare acute) | 843 @ 3.956 | 2.267 | 2,158 @ 4.185 | 2.496 |

Worse at both depths and in 2022 both times: the acute-cascade trips
look good in isolation (5.44) but are slot thieves in the vote book.
**v2.4 stands exactly as baked.**

### Verdict

**`halts_today` x `secs_since_halt` is the right encoding.** The
windowed counts stay as recorded columns — they cost nothing and are
now the honest way to ask cascade-DENSITY questions (the S42q proxy
never could). `v23_hcount/` = the research parquet (v2.3 population,
every column through S42r; apply the v2.4 gate in SQL). `v24_hcount/`
DELETED — its `halts_1200` held the wall-clock definition under the
same column name, a footgun for any future query.


## S42s — CORRECTION to S42r: it is halt DURATION, not halt COUNT (2026-08-03)

User: "What do you mean by this? What is the >=400s artifact?" —
auditing the claim, it does not hold. **Three errors in a row on one
cell, all from reasoning about it instead of measuring it:**

1. **S42q** split the gate-blocked set (`ht>=3 AND ssh<20m`) by
   `gap_1200 - gap_adj_1200` (halt SECONDS in the trailing wall-clock
   20m) at 400s and LABELLED the buckets "~one halt" / "~two halts".
   Labelling error — but the cell itself was real.
2. **The retraction** claimed the >=400s bucket was contaminated with
   single long halts. **Wrong for THIS population** — see below, there
   are zero such trips. The 37 contaminated trips were in the ht=1
   population; I generalised across populations without checking.
3. **S42r's "rehabilitation"** credited the true COUNT (>=2) for the
   cell's performance. **Also wrong** — duration is doing the work.

**The cross-tab that settles it** (blocked set, g60):

| proxy bucket | true count | n | tkds | PF | avg% |
|--------------|-----------|--:|-----:|---:|-----:|
| <400s | count = 1 | 364 | 51 | 1.46 | +0.92 |
| <400s | count >= 2 | 43 | 10 | **1.70** | +1.64 |
| >=400s | count >= 2 | 59 | 7 | **∞ (no losers)** | +6.35 |
| >=400s | count = 1 | **0** | — | — | — |

The >=400s cell is a strict SUBSET of the count>=2 cell (no single
long halts in this population at all). And **within** count>=2, the
duration split is 59 @ no-losers vs 43 @ 1.70 — the sub-400s half is
as bad as the count=1 group. **So S42r's "acute cascade = 102 @ 5.44"
is a BLEND of one excellent cell and one mediocre one, and the count
was never the operative variable.** The right statement: among serial
breakers recently resumed, what separates them is how much of the last
20 minutes the stock spent HALTED (>=1/3 of it = the acute elevator),
not how many times it halted.

**The duration-refined gate — tested, WINS, still NOT baked:**

| gate | mc=1 | 2022 | mc=3 | 2022 |
|------|------|-----:|------|-----:|
| v2.4 as baked | 829 @ 4.174 | 2.362 | 2,118 @ 4.327 | 2.578 |
| count-refined (S42r) | 843 @ 3.956 | 2.267 | 2,158 @ 4.185 | 2.496 |
| **duration-refined** | **835 @ 4.217** | 2.362 | **2,136 @ 4.378** | 2.578 |

Strictly better than v2.4 — more trips AND more PF, 2022 identical.
**Rejected anyway on census grounds:** the cell is 7 ticker-days in 4
years (2023 1 tkd / 2024 4 tkds supplying 28 of 59 trips / 2025 1 /
2026 1) with **zero presence in 2020, 2021, 2022**, and the +0.043 PF
at mc=1 sits INSIDE the +/-0.05 band that every neighbouring gate shape
occupies (S42p). That is a 4-ticker-day feature wearing a gate's
clothes. **v2.4 stands.** Re-test if the cell reaches ~25 tkds spanning
a bear year.

**House lesson (this whole S42q-S42s thread):** when a cell's identity
is defined by a derived quantity, MEASURE the quantity before naming
it — and when correcting an error, re-check the correction against the
same population, not a neighbouring one. The census-before-profiling
rule (S38j) exists for exactly this and I skipped it three times.


## S42t — ⭐⭐ SPEC v2.5: the REOPEN BLOCK (+ a --base-run bug found while wiring it) (2026-08-03)

User: "To the (ht >= 3 /\ ssh < 20m) filter let's also \/ (ht < 3 /\
ssh < 2m). For the first 2 halts we might as well avoid taking trades
in the first 2 minutes after the reopen to avoid getting caught in a
cascade. Let's bake it into the spec."

**SPEC v2.5 = v2.4 + `ReopenBlockSec = 120`:** reject any signal within
120s of ANY resume, the first 1-2 halts INCLUDED (ht = 0 has ssh = -1
and is never blocked). Implemented as its own knob rather than as the
literal disjunction because `(ht>=3 & ssh<1200) OR (ht<3 & ssh<120)`
is equivalent to `(ht>=3 & ssh<1200) OR (ht>=1 & ssh<120)` — the ht>=3
case is common to both — and the second form is independently
switchable (`--reopen-block-sec`, 0 = off) and independently
neutralised by `--base-run`.

**Rationale** is S42p's forbidden state: [2,5m) post-resume is where
the next LULD trigger decides itself, and for ht=1 that state is
structurally EMPTY (a first-halt name still printing new lows there
RE-HALTS). A sub-2m entry is therefore taken BLIND to a cascade test
the tape is about to run. Cost is trivial (20 trips at $1+, 3
ticker-days in 7 years — S42p §1) and the user's reasoning is a
risk-shape argument, not a PF argument.

### ⚠ BUGFIX found while wiring: --base-run never neutralised v2.4

The S42n edit that was supposed to add `CascadeHaltCount = 0` to the
`--base-run` block **silently no-op'd** — the python `str.replace` used
20-space indentation against a 16-space file and there was no assert.
So `--base-run` has been carrying a LIVE cascade gate since S42n.

**No data is affected:** `base_v15` (THE base) predates v2.4 entirely,
and every bake since passed its flags explicitly (`v23_hcount` used
`--cascade-halt-count 0`). But a base run is only a base run if EVERY
gate is off. Both `CascadeHaltCount = 0` and `ReopenBlockSec = 0` are
now in the block, **verified from the banner** rather than by
inspection: `--base-run` prints `cascade off`, the default prints
`cascade ht>=3&<1200s | reopen<120s`.

**House lesson:** every scripted source edit needs an `assert` on its
anchor. A no-op replace is invisible — it produces a clean build and a
silently wrong binary. (This is the same failure mode as the S42q-S42s
cell: an unverified assumption that looked like it worked.)

Forecast: 38,069 (v2.3) - 605 (v2.4 knife) - 20 (new reopen block)
= **37,444**.


## S42u — ⭐ the halt-band VOICE: ht>0 & ssh in [20,80m) — NOT a volatility proxy (2026-08-03)

User: "could the outperformance of the [20,80m) band be explained by
volatility, or would it be worth adding ht > 0 && ssh in [20, 80m) as
a voice filter?" **Answer: partly confounded, but the edge SURVIVES
normalisation, and it is COMPLEMENTARY to v20 rather than redundant.**
Population = the v2.5 spec, g60, $1+.

**A. The confound is real** — the band IS a high-vol population:

| group | n | tkds | v20 q25/med/q75 | % hot (v20>=140) | avg votes | PF |
|-------|--:|-----:|-----------------|-----------------:|----------:|---:|
| no halt today | 7,273 | 1,209 | 70 / 89 / 115 | 11.4 | 1.17 | 3.43 |
| halt <20m | 264 | 41 | 113 / 139 / 160 | 39.8 | 1.63 | 86.35 |
| **halt [20,80m)** | 1,862 | 288 | 101 / **126** / 159 | **39.5** | 2.17 | **5.86** |
| halt >=80m | 1,704 | 253 | 72 / **85** / 101 | 4.2 | 1.66 | 3.86 |

(Note the >=80m row: v20 median 85 is BELOW the no-halt 89 and only
4.2% hot — hours after a halt the vol has fully decayed, and the PF
decays with it to 3.86. The vol confound is real and it is what makes
the naive comparison suspect.)

**B. But it SURVIVES decile normalisation** (house rule) — v20 deciles
over the whole g60 book, band vs no-halt WITHIN each decile:

| v20 decile | v20 range (bp) | n band | PF band | n no-halt | PF no-halt |
|-----------:|----------------|-------:|--------:|----------:|-----------:|
| 1 | 40-58 | 11 | ∞ | 926 | 5.51 |
| 2 | 58-70 | 29 | **29.85** | 877 | 3.89 |
| 3 | 70-79 | 75 | **5.35** | 748 | 3.21 |
| 4 | 79-86 | 100 | **19.31** | 785 | 2.24 |
| 5 | 86-94 | 131 | **10.26** | 771 | 2.14 |
| 6 | 94-103 | 132 | 3.45 | 715 | 3.59 |
| 7 | 103-115 | 231 | **9.13** | 675 | 4.24 |
| 8 | 115-132 | 297 | **4.68** | 663 | 3.21 |
| 9 | 132-157 | 383 | **7.14** | 573 | 3.65 |
| 10 | 157-326 | 473 | 4.97 | 540 | 5.72 |

**The band beats no-halt in 8 of 10 deciles**, and — the important
part — **its edge is LARGEST where volatility is LOW** (deciles 2-5:
29.85 / 5.35 / 19.31 / 10.26 against 3.89 / 3.21 / 2.24 / 2.14) and
VANISHES in decile 10 (4.97 vs 5.72). A recent halt is information the
CURRENT volatility no longer carries: the name was in play, the crowd
is still watching, the LULD reference has reset — even though the tape
has since calmed. That makes it a complement to v20, not a proxy.

**C. It also adds WITHIN each vote level** — most where the other
voices are silent:

| votes (7v) | n band | PF band | n rest | PF rest |
|-----------:|-------:|--------:|-------:|--------:|
| 0 | 321 | **6.30** | 3,437 | 2.51 |
| 1 | 313 | **8.42** | 2,092 | 3.34 |
| 2 | 481 | 6.02 | 2,165 | 5.60 |
| 3 | 357 | 8.01 | 1,035 | 5.97 |
| 4 | 262 | 3.53 | 400 | 4.91 |
| 5 | 118 | 4.51 | 80 | 3.48 |
| 6 | 9 | 17.07 | 21 | 1.70 |

**D. Year robustness** (1,862 trips / 288 tkds): 2020 11.69 / 2021 3.99
/ 2022 **13.62** / 2023 2.81 / 2024 4.77 / 2025 5.73 / 2026 15.83 —
every year >= 2.81 and the BEST bear-year reading of any voice.

### The 8-voice ladder — ADOPTED

| construction | mc=1 | mc=2 | mc=3 |
|---|---|---|---|
| 7-voice | 827 @ 4.183 | 1,530 @ 4.242 | 2,113 @ 4.336 |
| **8-voice (+ halt band)** | **884 @ 4.075** | **1,630 @ 4.244** | **2,248 @ 4.371** |
| 2022, 7v -> 8v | 2.406 -> **2.526** | 2.517 -> **2.661** | 2.607 -> **2.778** |

+6.4-6.9% trips at every depth; PF flat at mc=2 and BETTER at mc=3;
only mc=1 pays a toll (-0.108). **2022 improves at every depth.**
Marginal set at mc=3 = **151 trips / 65 tkds / PF 6.19 / p22 57.4 /
+356 pts** — better than the book it joins and spread over 65
ticker-days, not concentrated.

**⭐⭐ THE VOTE = EIGHT VOICES** (g60, $1+, nfire >= 2):

    {v20 >= 140bp, d20a < -28%, speed < -6%, dslo >= +16%, pah >= +28%,
     ramp (vs20-vs10)*2e4 < -12, |esf| >= 0.5,
     halt band: ssh in [1200, 4800)}

### v25_reference BAKED and VERIFIED

**GRAND PARITY EXACT, three ways:** predicted 37,444 = SQL-emulated
37,444 = baked **37,444**, zero asymmetric rows, zero ret_exit diff.
`v25_reference/` = **THE reference**. (Every number in S42u above was
measured on the SQL emulation and is therefore confirmed by this.)

**Canonical books ($1+):** full = 29,794 @ 2.542 / p22 1.59 (4,697
tkds); **g60 = 11,103 @ 3.980 / p22 4.04** (1,724 tkds).

**THE CANONICAL LADDER — SPEC v2.5, 8-voice vote, g60, $1+, nfire>=2:**

| mc | n | PF | win% | avg% | med% | 2022 | slot pts |
|---:|--:|---:|-----:|-----:|-----:|-----:|---------:|
| 1 | 884 | 4.075 | 79.1 | +2.18 | +2.47 | 2.526 | 1,927 |
| 2 | 1,630 | 4.244 | 79.9 | +2.25 | +2.50 | 2.661 | 3,668 |
| 3 | 2,248 | 4.371 | 80.4 | +2.30 | +2.52 | 2.778 | 5,170 |

PF, win%, avg% and 2022 ALL rise with depth. Since this morning's
5-voice/v2.3 starting point (mc=1 769 @ 3.622) the mc=1 book is +15%
trips at +0.45 PF, and mc=3 now carries 5,170 slot points at 4.371.

### S42t addendum — the cascade gate rewritten as a CASE ANALYSIS (user, 2026-08-03)

User: "You're right that what you have is correct, but it's too hard to
understand. How about `(ht>=3 && ssh>=20m) || (ht>=1 && ht<3 &&
ssh>=2m) || ht=0`?" — **correct, and adopted.** The `ht<3` upper bound
is what makes it sound: the three cases now PARTITION ht, so none can
leak into another's territory. (Without it the middle clause is
satisfied by ht>=3 names and re-admits 568 serial-breaker trips at
ssh in [2m,20m) — the earlier attempt's bug.)

Implemented as the question the rule actually asks — *how long must the
tape run after a resume before we fade it?*

    let requiredWait =
        if haltsToday = 0 then 0
        elif cfg.CascadeHaltCount > 0 && haltsToday >= cfg.CascadeHaltCount
             then cfg.CascadeWindowSec
        else cfg.ReopenBlockSec
    requiredWait <= 0 || bar.etSec - lastHaltEnd >= requiredWait

Four lines, one wait per case, both knobs still independently
switchable. **GRAND PARITY vs v25_reference: 37,444 = 37,444, zero
asymmetric rows, zero diff on ret_exit / entry_px / exit_px.**

**Banner hazard fixed at the same time:** it printed raw config, so
`--cascade-halt-count 0` rendered as `cascade ht>=0&<1200s` and
`--reopen-block-sec 0` as `reopen<0s` — both READ LIKE LIVE RULES when
off. Since the S42t base-run bug was caught by reading a banner, a
lying banner is a live trap. Now:

| flags | banner |
|-------|--------|
| defaults | `cascade ht>=1 wait 120s, ht>=3 wait 1200s` |
| `--base-run` | `cascade off` |
| `--cascade-halt-count 0` | `cascade ht>=1 wait 120s, serial-breaker off` |
| `--reopen-block-sec 0` | `cascade ht>=1 off, ht>=3 wait 1200s` |

**House lesson:** a gate's code should read like the rule it enforces.
The De Morgan conjunction was correct but opaque, and opaque gates are
where the S42q-S42t errors bred. Prefer the case analysis; verify the
rewrite by parity, not by re-reading the boolean.


## S42v — WHERE WE STAND: the SPEC v2.5 matrix (universe x vote x concurrency) (2026-08-03)

All on `v25_reference`, $1+. Vote = the 8 voices, nfire >= 2.

| book | mc=0 (attribution) | mc=1 | mc=2 | mc=3 |
|------|--------------------|------|------|------|
| full, no vote | 29,794 @ **2.542** | 4,233 @ **2.147** | 7,922 @ **2.140** | 11,063 @ **2.186** |
| full, + vote | 8,290 @ **3.969** | 1,350 @ **2.960** | 2,491 @ **3.035** | 3,431 @ **3.143** |
| g60, no vote | 11,103 @ **3.980** | 1,830 @ **2.955** | 3,373 @ **3.033** | 4,658 @ **3.112** |
| **g60, + vote** | 5,253 @ **5.568** | 884 @ **4.075** | 1,630 @ **4.244** | **2,248 @ 4.371** |

avg% per trip (same order): mc=1 1.12 / 1.89 / 1.56 / **2.18**;
mc=3 1.17 / 2.00 / 1.65 / **2.30**. Win% at mc=3: 72.2 / 77.4 / 76.6 / **80.4**.

**Three things this table says:**

1. **The two filters are near-perfect twins and they STACK.** At mc=1
   the universe filter alone (g60, 2.955) and the vote filter alone
   (2.960) are worth almost exactly the same, and together they give
   4.075 — nearly DOUBLE the unfiltered 2.147. Independent evidence for
   the S42c design principle: quality in the universe, extremity in the
   vote, two different facts.

2. **Quality and quantity genuinely trade off.** Slot points
   (n x avg%) at mc=3: full/no-vote **12,944** @ 2.19 · full+vote
   6,862 @ 3.14 · g60/no-vote 7,686 @ 3.11 · g60+vote **5,170** @ 4.37.
   The unfiltered book earns 2.5x the points of the traded book at half
   the PF, because with more candidates the single slot is simply busy
   more often. g60+vote is the deliberate choice of PF over points
   (S41g/h two-book view); full+vote is the standing alternative if
   capital ever wants the volume at 3.14.

3. **⚠ In 2022 the VOTE COSTS on the clean book.** g60/no-vote 2022 =
   **5.059** at mc=1 vs g60+vote **2.526**. The extremity voices
   discount in a bear (S42f: v20-only 2022 = 0.56, d20a-only loss-free)
   — the vote is a bull-regime amplifier, and the universe filter is
   what actually carries 2022. A regime rule that drops the vote bar to
   >= 1 (or trades g60 flat) in a bear is the open design question.


## ⭐⭐ SPEC v2.6 — THE REFERENCE CARD (2026-08-03)

> 🛑 **SUPERSEDED BY SPEC v2.7, 2026-08-05 (S43al).** One gate added:
> **`eff_9ema_10m >= -0.10`** — the whipsaw knife. THE reference is now
> **`v31_reference` (35,778)**, an exact SUBSET of `v27_reference` (0
> `ret_exit` diffs, 0 orphans). mc=3 on the book = **3,250 @ 4.090 /
> +2.01%/trip**, worst year **2.860** (was 3,336 @ 3.899, worst 2.782).
> Everything else on this card stands.
> ⚠ **OFF FOR THIS GATE IS -INFINITY, NOT 0** — the bound is negative, so
> `0` is a live ceiling (the S43aj trap mirrored). Verify from the BANNER,
> which prints `eff9ema10 >= -0.10`.
> ⚠ The `$1+` floor is the RAW price `entry_px/adj_ratio >= 1`, NOT the
> adjusted `entry_px` (S43al: the adjusted form inflates the book to 9,924
> from 7,789 and does not reproduce the recorded mc=3 numbers).

> ⚠ **UPDATED 2026-08-04.** The universe gate is now a GAP COUNT, not
> `n_eff_shannon` (S43u): `mr_candidate_1s` = `dv_0945_tape >= $2M AND
> n_bars_1s >= 200` (gaps <= 700 of 900). THE base = **`base_v16`**
> (2,184,698); THE reference = **`v27_reference`** (37,214), grand parity
> exact. **THE BOOK = `g60 AND vote >= 1`** — the `chg_1d` gate is DROPPED
> (S43v: it carried a future-split lookahead). mc=3 with S-tier A =
> **3,330 @ 3.901 / +1.97%/trip**, worst year 2.78.
> ⚠ `flushfader_base_tkds` must be REGENERATED after any universe change —
> it carries the full candidate schema restricted to signal days.

Everything needed to reproduce the current system. Supersedes the v2.2
/ v2.3 cards.

### 1. The signal (unchanged since v1.x — this is the base)

    universe   dv_0945_tape >= $3.0M   (1s-bar-native honest dollars, S35)
               barnum >= 22            (early-episode slice cut, S40e)
    ENTRY      vwap < prior 1200-bar MIN (strict, new ~20m low)
               AND dv60 >= $100k AND tc60 >= 60      fill = NEXT bar vwap
    EXIT       vwap > prior 300-bar MAX (strict, ~5m high) | MOC
    leg        arm on first new low, reset on new 1200-bar high
    volat      volat_20m >= 40 bp/30s  (no ceiling)
    window     09:45-15:00 ET entries; features fold from 09:30
    stops      OFF (price-acceptance stops all disabled — V6: destructive)

### 2. The v2.5 spec gates (all ANDed on top of the signal)

| gate | value | since |
|------|-------|-------|
| flush speed | vwap/vwap_60_prev - 1 < -2%/1m | v1.2 |
| d1m | vwap/hi_60 - 1 < -2% | v1.9 |
| ssf | ols_slope_since_flow x 6e5 in [-375, -25) bp/min | v2.2 |
| dlv | vwap/(dv_leg/vol_leg) - 1 < -3% | v2.2 |
| rflow | ols_r_since_flow >= -0.95 | v2.1 |
| z20 | z_20m (LOG space, vw moments) < -1.5 sigma | v2.3 |
| **cascade** | **ht>=1: wait 120s · ht>=3: wait 1200s after resume** | **v2.4/v2.5** |
| halt detector | run >= 58s AND pre-hole 5m rng >= 4% AND **pre-hole adj 1m gap < 4** | v2.6 |
| K | lows_since_first_low in [26, 50] | v1.2 |
| eff20 | abs(eff_20m) in [0.30, 0.50) | v1.2 |
| eff10 | abs(eff_10m) >= 0.15 | v1.2 |
| vol10rate | (vol_10/10)/(vol_60/60) >= 0.75 | v1.2 |
| lows300 | lows_since_first_low_300 >= 6 | v1.4 |
| rngfront | rng_300/rng_20m < 0.80 | v1.5 |
| accel1020 | (slope_600 - slope_1200) x 6e5 >= -80 bp/min | v1.7 |
| slope20 | ols_slope_1200 x 6e5 < -10 bp/min | v1.7 |
| slope5 | ols_slope_300 x 6e5 >= -400 bp/min | v1.7 |

### 3. Canonical commands

    # THE BASE (every spec gate OFF — signal definition only)
    FF_CANDIDATE_TABLE=flushfader_base_tkds \
      ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
      --base-run --out-dir data/equity/flushfader/base_vNN

    # THE REFERENCE (full v2.5 spec = every default)
    FF_CANDIDATE_TABLE=flushfader_base_tkds \
      ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
      --out-dir data/equity/flushfader/v26_reference

    # mc replay (mc=1 is the decider; mc=3 is the trading target)
    dotnet fsi scripts/equity/flushfader_mc.fsx -- --mc 3 \
      --trips "data/equity/flushfader/v26_reference/trips_p*.parquet" \
      --where "<book cut>"

⚠ Verify gates from the BANNER, never by reading config (S42t: a
silent no-op edit left --base-run carrying a live gate). `--base-run`
must print `cascade off`.

### 4. THE VOTE — 8 voices, nfire >= 2

| voice | condition |
|-------|-----------|
| v20 | volat_20m x 1e4 >= 140 bp |
| d20a | (vwap/first_low_vwap)(1+d_hi_flow) - 1 < -28% |
| speed | vwap/vwap_60_prev - 1 < -6% |
| dslo | vwap/sess_low - 1 >= +16% |
| pah | (1+pct_chg_open) x chan_hi/vwap - 1 >= +28% |
| ramp | (volat_slope_20m - volat_slope_10m) x 2e4 < -12 |
| esf | abs(eff_since_flow) >= 0.5 |
| halt band | secs_since_halt in [1200, 4800) |

> 🛑 **CURRENT ROSTER (2026-08-05, S43an) — 6 voices, bar >= 1:**
> `{v20 >= 140bp, d20a < -28%, dslo >= +8%, ramp < -12,`
> `bars_since_first_low <= 390, haltband ssh in [20,80m)}`
> `speed` and `pah` were dropped earlier (S43g); **`|esf| >= 0.5` is
> replaced by `bars_since_first_low <= 390`** (S43an — esf was leg age in
> disguise). Applied POST-HOC in the mc replay's `--where`; no engine gate.

### 5. The books (v25_reference, $1+)

**TRADED = g60 (gap_60 < 4) + vote >= 2.** Per-year ladder:

| year | mc=1 n / PF | mc=2 n / PF | mc=3 n / PF |
|------|-------------|-------------|-------------|
| 2020 | 129 / 13.280 | 241 / 12.661 | 338 / 13.065 |
| 2021 | 130 / 3.478 | 237 / 3.742 | 325 / 3.810 |
| 2022 | 49 / 2.526 | 92 / 2.661 | 129 / 2.778 |
| 2023 | 82 / 3.556 | 146 / 3.857 | 198 / 4.047 |
| 2024 | 164 / 3.120 | 307 / 3.271 | 425 / 3.291 |
| 2025 | 219 / 3.491 | 404 / 3.617 | 551 / 3.843 |
| 2026 | 111 / 5.261 | 203 / 5.393 | 282 / 5.281 |
| **total** | **884 / 4.075** | **1,630 / 4.244** | **2,248 / 4.371** |

win% 79.1 / 79.9 / 80.4 · avg% +2.18 / +2.25 / +2.30 · med% +2.47 /
+2.50 / +2.52. **Every year positive at every depth; worst year 2022 =
2.53-2.78.** Full matrix incl. the other three books: S42v.

### 6. The S-TIER cell (user, 2026-08-03) — traded SEPARATELY

    ht = 1  AND  secs_since_halt in [120, 1200)      -- first halt, 2-20m post-resume

g60: [5,10m) 47 @ 59.0 · [10,20m) 177 @ 201.3 · 91-94% win · med
+5.2%/trip · ~4 tkds/yr. Causal (S42p): by 5m past resume with no
second halt the tape has ALREADY run the cascade test. NOT part of the
vote book — "the best trade in our entire playbook" (user).

### 7. Parquet inventory

| path | what |
|------|------|
| `base_v15/` | THE base (2,217,950 trips, every gate off) |
| **`v26_reference/`** | **THE reference — v2.6, 37,414 trips** |
| `v25_reference/` | v2.5 (37,444) — superseded, kept for the S42v/S42x tables |
| `v23_hcount/` | research parquet: v2.3 population (38,069) + halt counts, for halt-zone work the spec now gates away |
| `v23_vs/`, `v23_fast/`, `v22_*/` | superseded (kept for provenance) |

Grand-parity chain: base_v15 -> v23 (38,069) -> v24 (37,464, cascade
knife) -> v25 (37,444, + reopen block) -> **v26 (37,414, + detector
pre-gap 4)**, every step verified exact.


## S42w — halt detector pre-gap 2 -> 4: a wash (2026-08-03)

User: the detector demands `pre-hole adjusted 1m gap < 2` while the
traded universe is `gap_60 < 4` — inconsistent, so halts on legitimately
tradeable names go unclassified. Baked `--halt-max-pre-gap-60 4`
(`v25_hg4/`, everything else at v2.5 defaults).

**It barely moves anything.**

| | trips | halted trips | halted tkds | % halted |
|---|------:|-------------:|------------:|---------:|
| pre-gap < 2 | 29,794 | 6,917 | 859 | 23.22 |
| pre-gap < 4 | 29,764 | 7,153 | 880 | 24.03 |

+236 halted trips (+3.4%), +21 halted ticker-days, and the book itself
shrinks by 30 (the extra halts trip the cascade gate). Downstream:

| cell | pre-gap < 2 | pre-gap < 4 |
|------|-------------|-------------|
| **S-TIER (ht=1, ssh [2,20m), g60)** | 224 @ 142.58 / 34 tkds | **229 @ 144.38 / 35 tkds** |
| halt-band voice (ssh [20,80m), g60) | 1,862 @ 5.86 / p22 13.62 | 1,897 @ 6.02 / p22 14.34 |

**+5 trips and +1 ticker-day in the S-tier cell.** Everything moves in
the right direction and nothing moves materially — the 4%-range and
58s-run conditions are what actually bind, not the pre-gap. Verdict:
**adopt for consistency, not for yield** (the detector should agree
with the universe it feeds); no re-bake of the reference is warranted
on these numbers alone. Fold it in with the next spec change.


## S42x — ⭐⭐ WHICH VOICE BREAKS 2022? Not a voice — the VOTE COUNT (2026-08-03)

The g60 book does **5.059** in 2022 at mc=1 without the vote and
**2.526** with it (S42v). Diagnosis on v25_reference, g60, mc=0.

**A. Each voice when it fires, PF by year:**

| voice | n fires | all | 2020 | 2021 | **2022** | 2023 | 2024 | 2025 | 2026 |
|-------|--------:|----:|-----:|-----:|---------:|-----:|-----:|-----:|-----:|
| v20 | 1,742 | 5.44 | 10.90 | 1.80 | **0.87** | 15.70 | 6.06 | 4.92 | 12.95 |
| speed | 1,594 | 4.51 | 20.87 | 8.44 | **1.33** | 3.35 | 7.00 | 2.49 | 4.53 |
| d20a | 1,611 | 6.31 | 8.93 | 6.73 | **1.70** | 10.92 | 6.38 | 4.17 | 11.41 |
| esf | 2,498 | 4.91 | 19.29 | 6.21 | 2.12 | 2.63 | 3.50 | 3.54 | 8.57 |
| dslo | 3,812 | 4.70 | 12.25 | 3.86 | 2.90 | 3.55 | 3.90 | 3.62 | 11.00 |
| pah | 3,891 | 4.63 | 11.18 | 2.92 | 3.17 | 3.62 | 3.88 | 4.15 | 11.09 |
| ramp | 627 | 8.66 | 20.74 | 56.10 | **7.61** | 2.22 | 7.49 | 11.31 | 5.60 |
| haltband | 1,862 | 5.86 | 11.69 | 3.99 | **13.62** | 2.81 | 4.77 | 5.73 | 15.83 |

**B. The culprit test — 2022, fired vs quiet:**

| voice | n fires 22 | PF fired | PF quiet | net pts | avg fired | avg quiet |
|-------|-----------:|---------:|---------:|--------:|----------:|----------:|
| **v20** | 40 | **0.87** | 7.19 | **-22** | **-0.55** | +2.06 |
| speed | 46 | 1.33 | 5.42 | +37 | +0.81 | +1.97 |
| d20a | 17 | 1.70 | 4.34 | +27 | +1.59 | +1.88 |
| esf | 200 | 2.12 | 7.43 | +240 | +1.20 | +2.26 |
| dslo | 216 | 2.90 | 5.58 | +365 | +1.69 | +1.99 |
| pah | 227 | 3.17 | 5.21 | +417 | +1.84 | +1.89 |
| ramp | 26 | 7.61 | 3.89 | +89 | +3.40 | +1.79 |
| haltband | 113 | 13.62 | 3.27 | +312 | +2.76 | +1.63 |

**v20 is the only voice with NEGATIVE 2022 expectancy** (-0.55%/trip,
-22 pts). ramp and haltband are the only two that are ACCRETIVE.

**⚠ But removing v20 does NOT fix 2022** — it is identical to four
decimals: mc=1 2.526 -> 2.526, mc=3 2.778 -> 2.778 (it does lift the
overall book, mc=3 4.371 -> 4.531 on 92 fewer trips, by trimming
volume). Those 40 fires are mostly not the trips the mc selector takes.

**C. The real cause — the hump INVERTS in a bear:**

| votes | n all | PF all | n 2022 | **PF 2022** | net pts 22 |
|------:|------:|-------:|-------:|------------:|-----------:|
| 0 | 3,437 | 2.51 | 125 | 3.87 | +176 |
| 1 | 2,413 | 3.57 | 150 | 5.62 | +290 |
| 2 | 2,478 | 5.84 | 140 | **38.87** | +407 |
| 3 | 1,516 | 5.99 | 80 | 6.51 | +171 |
| 4 | 757 | 5.84 | 30 | **0.95** | **-5** |
| 5 | 342 | 3.51 | 7 | **0.47** | **-30** |
| 6 | 139 | 3.63 | 10 | 1.10 | +2 |

All-years the hump is flat-topped from 2 to 4 (5.84 / 5.99 / 5.84). In
2022 it **collapses after 3** (38.87 / 6.51 / 0.95 / 0.47). **In a bear,
many extremity voices agreeing simultaneously is a CRASH, not a
fadeable flush** — the S42b contrast grammar (sustained violence =
developing crash) reappearing at the vote level.

**D. The fix is a CAP, not a deletion.** mc=3, g60:

| construction | n | PF | 2020 | 2021 | **2022** | 2023 | 2024 | 2025 | 2026 |
|---|--:|---:|-----:|-----:|---------:|-----:|-----:|-----:|-----:|
| vote >= 2 (current) | 2,248 | 4.371 | 13.07 | 3.81 | **2.78** | 4.05 | 3.29 | 3.84 | 5.28 |
| vote in [2,4] | 2,093 | 4.382 | — | — | 2.78 | — | — | — | — |
| **vote in [2,3]** | 1,857 | **4.772** | 20.01 | 4.69 | **8.40** | 5.71 | **2.75** | 4.01 | 4.45 |
| drop v20, >= 2 | 2,156 | 4.531 | — | — | 2.78 | — | — | — | — |

Capping at 3 **triples 2022 (2.78 -> 8.40)** and improves 5 of 7 years,
for -17% trips. Note `[2,4]` does nothing (2.78) — the damage is
concentrated at exactly 4 votes, which is 30 of 2022's trips.

**⚠ HONEST CAVEAT — the cap does NOT raise the worst-year floor.** It
moves it: worst year goes from 2022 @ 2.778 to **2024 @ 2.754**. What
improves is total PF (4.371 -> 4.772) and the SHAPE (bear years stop
being the weak point). Whether -17% volume is worth +0.40 PF and a
flatter year profile is a portfolio call, not a statistical one —
LEFT FOR THE USER.


## S42y — ⭐ SPEC v2.6 BAKED: halt detector pre-gap 2 -> 4 (2026-08-03)

User: "we'll move the default pre-gap from < 2 to < 4. No reason not to
do it." Adopted on the CONSISTENCY argument from S42w (the detector
must agree with the `gap_60 < 4` universe it feeds), not on yield.

**GRAND PARITY, and a free control:** `v25_hg4/` was already baked with
`--halt-max-pre-gap-60 4` explicitly, so flipping the DEFAULT had to
reproduce it exactly — **37,414 = 37,414 = 37,414 predicted**, zero
asymmetric rows, zero diff on ret_exit AND on halts_today itself.
`v26_reference/` = THE reference.

**Also surfaced the detector in the BANNER:**
`halt detect = run >= 58s AND pre-hole 5m rng >= 4.0% AND pre-hole adj
1m gap < 4   (feeds the cascade gate)`. It stopped being record-only
the moment the cascade gate consumed it (v2.4), so it belongs where
gates get verified — the S42t lesson.

**v2.6 books ($1+):** full 29,764 @ 2.544 · g60 11,083 @ **4.003** ·
S-tier cell **229 @ 144.38** (35 tkds, was 224 / 34).

**The canonical ladder, v2.5 -> v2.6** (g60, 8-voice >= 2):

| mc | v2.5 | v2.6 | 2022 |
|---:|------|------|------|
| 1 | 884 @ 4.075 | **879 @ 4.121** | 2.526 -> 2.516 |
| 2 | 1,630 @ 4.244 | **1,621 @ 4.268** | 2.661 -> 2.653 |
| 3 | 2,248 @ 4.371 | **2,236 @ 4.391** | 2.778 -> 2.759 |

A wash exactly as S42w forecast: ~10 fewer trips and ~+0.02 PF at each
depth. Taken for correctness, and the g60 book crosses 4.00.


## S42z — the vote BAR: the first vote buys the FLOOR, the rest buy the MEAN (2026-08-03)

User: "What about votes >= 1 on the g60 universe. Is the worst year
still 2.5?" **Yes — 2.514 (2025), against the >= 2 book's 2.526
(2022).** Laying every construction out (g60, mc=1 unless noted):

| construction | n | PF | worst year | which year |
|--------------|--:|---:|-----------:|-----------|
| no vote | 1,830 | 2.955 | **1.773** | 2023 |
| votes >= 1 | 1,297 | 3.401 | **2.514** | 2025 |
| votes >= 2 (current) | 884 | 4.075 | **2.526** | 2022 |
| votes [2,3] (mc=3) | 1,857 | 4.772 | **2.754** | 2024 |

**The first vote buys the FLOOR; every vote after buys only the MEAN.**
no-vote -> >=1 lifts the worst year 1.77 -> 2.51 (+0.74). >=1 -> >=2
lifts it 0.01 while lifting the MEAN 3.40 -> 4.08. The cap adds another
0.40 of mean and nothing to the floor.

**And the worst year ROTATES** — 2023, 2025, 2022, 2024, never the same
twice. Four constructions, four floors in [2.45, 2.78]. That is not a
bear-regime weakness; it looks like the sampling noise of a 7-year book
at 100-500 trades/yr. **No vote engineering will raise it.**

votes >= 1 does buy CONSISTENCY: ex-2020 its year range is 2.51-3.32
(1.3x) vs >= 2's 2.53-5.26 (2.1x). So >=1 is the flatter book and >=2
the higher-expectancy one. With conviction sizing (S38: sizing, not
slot allocation, is the lever) the higher-expectancy book is the better
raw material — the bar stays at 2.

⏭ To move the FLOOR the levers are regime detection (the optimal vote
threshold is demonstrably regime-dependent in BOTH directions — S42x)
or position sizing. Not the vote bar.


## S43 — ⭐⭐ THE VOLUME FAMILY: rp_vol x gap_1200 = the sizing ladder (2026-08-03)

Two decisions from the user first: **the vote bar drops to >= 1** (2 and
3 reserved as a future sizing lever), and the three remaining volume
features get **separate treatment — NOT voices**, because they are
volume/liquidity facts, not extremity facts.

**THE NEW DEFAULT BOOK (v2.6, g60, votes >= 1, $1+):** mc=0 7,655 @
4.923 · **mc=1 1,294 @ 3.434 · mc=2 2,371 @ 3.527 · mc=3 3,261 @ 3.613**
(avg% +1.81/+1.87/+1.92, worst year 2025 @ 2.53-2.63).

### Are these three features ONE thing or THREE?

| pair | corr |
|------|-----:|
| gap_1200 vs n_eff_shannon_1200 | **-0.535** |
| gap_1200 vs rp_vol | 0.156 |
| n_eff_shannon_1200 vs rp_vol | **-0.062** |
| n_eff_shannon_1200 vs v20 | 0.471 |
| rp_vol vs v20 | -0.039 |

**gap_1200 and n_eff are family-mates** (-0.535 — holes and effective
trade count are the same fact twice); **rp_vol is independent of both
AND of volatility.** Per the S41w architecture rule (max WITHIN a
family, MULTIPLY across), that means: pick ONE of {gap, n_eff}, and
rp_vol multiplies with it.

### The three, each alone (mc=1 / mc=3 on the default book)

| lens | mc=1 | mc=3 |
|------|------|------|
| (none) | 1,294 @ 3.434 | 3,261 @ 3.613 |
| **rp_vol >= 0.8** | 457 @ **5.286** | 1,131 @ **6.174** |
| gap_1200 < 15 | 515 @ 4.431 | 1,331 @ 4.642 |
| n_eff extremes (dec 1-2 or 9-10) | 563 @ 3.879 | 1,389 @ 4.217 |
| **rp_vol >= 0.8 AND gap_1200 < 15** | **157 @ 14.957** | **398 @ 16.631** |

**n_eff loses on both counts** — weakest alone AND it is 0.471
correlated with v20 (a volatility proxy wearing a volume costume).
**Dropped.** gap_1200 keeps the family seat.

**rp_vol is the find.** Its band table is a step function, not a
gradient: <0.6 = 4.51 · [0.6,0.8) = **3.22** · [0.8,1.0) = **9.81** ·
[1.0,1.5) = 9.26 · [1.5,2.5) = 12.15. The threshold at 0.8 is sharp
and the mechanism is clean: **rp_vol >= 0.8 means volume is SUSTAINED
into the flush** (the leg trades at >= 80% of the pre-leg rate) rather
than drying up. A flush on evaporating volume is a different animal
from a flush the crowd is actually participating in.

gap_1200 is NON-monotone with a swamp: 0 = 6.60 · [1,5) = 6.61 ·
[5,15) = 5.31 · **[15,40) = 2.70** · [40,100) = 4.43 · >=100 = 5.90 —
the same cliff/swamp/recovery shape gap_60 shows (S41h).

### ⭐ The stack multiplies, and it is all-weather

mc=3, rp_vol >= 0.8 AND gap_1200 < 15: **398 @ 16.631**, +3.14% avg,
85.7% win — by year **20.10 / 23.74 / 127.56 / inf / 10.10 / 13.41 /
14.57 (2020..2026)**. Every year >= 10.

⚠ **But it is THIN and lumpy**: 157 tkds over 7 years at mc=0, with
2023 = 4 tkds / 10 trips and 2022 = 9 tkds; AIXI 2026-04-08 alone
supplies 25 trips and half of 2026's mc=0 points. It is a real edge on
a small population — which is exactly why it belongs in SIZING, not in
the entry logic.

### ⭐⭐ THE VOLUME SIZING LADDER (the deliverable)

Split the TRADED mc=3 book by the two surviving features:

| tier | n | tkds | % of book | PF | win% | avg% | 2022 | net pts |
|------|--:|-----:|----------:|---:|-----:|-----:|-----:|--------:|
| **A — both** (rp>=0.8 AND gap<15) | 357 | 143 | 10.9 | **15.29** | 85.7 | +3.09 | 127.56 | 1,102 |
| B — one of the two | 1,635 | 629 | 50.1 | 3.76 | 77.5 | +1.95 | 5.30 | 3,193 |
| C — neither | 1,269 | 486 | 38.9 | 2.69 | 76.5 | +1.56 | 2.55 | 1,976 |

A clean monotone ladder on 11% / 50% / 39% of the book, with PF 15.3 /
3.8 / 2.7 and 2022 holding at every tier. **This is the volume family's
job: it does not choose WHETHER to trade, it chooses HOW BIG.**

⏭ The two sizing dimensions are now independent and both calibrated:
**vote count** (1 / 2 / 3, S42z) and **volume tier** (A / B / C).
Crossing them is the natural next step — and per S41w they should
multiply, since extremity and liquidity are different families.


## S43b — n_eff settled (it INTERACTS, it does not compose) + lowdens joins the SIZING ladder (2026-08-03)

### n_eff conditioned on gap_1200 < 5 (user: "I think I saw that they
### do compose in one earlier test, but others say they don't")

Holding the gap confound fixed (2,298 trips, 341 tkds):

| pair | corr inside gap<5 |
|------|------------------:|
| **shannon vs hhi** | **0.955** |
| shannon vs gap_1200 | -0.324 |
| hhi vs gap_1200 | -0.213 |
| shannon vs v20 | 0.392 |
| **hhi vs v20** | **0.483** |
| shannon / hhi vs rp_vol | 0.104 / 0.036 |

**Shannon and HHI are the same feature** (0.955) — no choosing between
them. Conditioning removes some of the gap entanglement but the
VOLATILITY entanglement survives (0.39-0.48): n_eff is a v20 proxy
wearing a volume costume, in both flavours.

Neither is monotone inside gap<5 (shannon quintiles 5.83 / 4.66 /
**11.24** / 6.39 / 7.26; hhi 5.25 / 6.18 / 6.20 / **13.86** / 6.00),
and BOTH have their top quintile decaying in 2026 (0.53 each).

**⭐ Why the user remembers contradictory results — the SIGN FLIPS:**

| | hhi < 400 | hhi >= 400 |
|---|---|---|
| rp_vol < 0.8 | 4.03 | **6.01** (higher is better) |
| rp_vol >= 0.8 | **32.42** | 23.79 (higher is WORSE) |

That is an INTERACTION, not a composition. Against the clean
benchmark — **rp_vol >= 0.8 alone inside gap<5 = 623 @ 26.97** —
adding `hhi >= 400` LOWERS it to 23.79. n_eff earns no seat; it only
re-slices rp_vol with an unstable sign. **Both flavours retired.**

⚠ Note `gap_1200 < 5` is regime-selective: 11 tkds in 2022 and 8 in
2023 (vs 47-89 elsewhere). That is why its year columns read NULL —
census, not performance.

### lowdens (S40k) = lows_since_first_low / bars_since_first_low

| band | n | tkds | n_lose | PF | avg% |
|------|--:|-----:|-------:|---:|-----:|
| <0.10 | 6,633 | 1,077 | 1,302 | 4.61 | 2.35 |
| [0.10,0.15) | 646 | 156 | 133 | 5.86 | 2.57 |
| [0.15,0.20) | 237 | 57 | 29 | **12.55** | 2.97 |
| [0.20,0.30) | 111 | 26 | 7 | **28.10** | 4.45 |
| [0.30,0.45) | 28 | 4 | 0 | **∞** | 4.96 |

**Monotone and steep.** Correlations: v20 **-0.056**, speed 0.018,
dslo -0.035 — genuinely independent of the price/vol voices. But
**esf 0.783** and **bars_since_first_low -0.682**: lowdens is
essentially INVERSE LEG DURATION (K is gated to [26,50], so lows/bars
is ~1/bars), and it is an esf FAMILY-MATE.

**As a VOICE it fails three ways** (default book, mc=1 / mc=3):

| construction | mc=1 | mc=3 |
|---|---|---|
| 8-voice base | 1,294 @ 3.434 | 3,261 @ **3.613** |
| + lowdens as a 9th voice | 1,302 @ 3.422 | 3,284 @ 3.600 |
| family-OR (esf OR lowdens) | 1,295 @ 3.409 | 3,264 @ 3.584 |
| swap esf -> lowdens >= 0.15 | 1,105 @ 3.558 | 2,816 @ **3.804** |

ADDING it does nothing (redundant at corr 0.783). The FAMILY-OR is
worse — at a >= 1 bar, widening any voice only dilutes. Only the SWAP
improves (+0.19 PF, 5 of 7 years better, floor unchanged at 2.65) and
it costs 14% of trips. **Marginal; not taken** — the user chose >= 1
precisely for volume.

### ⭐⭐ Where lowdens DOES belong — the sizing ladder

lowdens is independent of the volume family (rp 0.147, gap -0.022, hhi
0.167), so it MULTIPLIES with it. The 3-way grid shows it rescues the
weak volume tiers: B 4.94 -> **23.20**, C 3.49 -> **10.71** (and, as
with n_eff, it INVERTS inside the strongest volume cell: A 35.84 ->
16.24 — high-lowdens adds nothing to tape that is already perfect).

**THE CONSOLIDATED SIZING LADDER (default book, mc=0):**

| tier | rule | n | tkds | % book | PF | win% | avg% | 2022 |
|------|------|--:|-----:|-------:|---:|-----:|-----:|-----:|
| **1 TOP** | lowdens >= 0.15 **OR** (rp>=0.8 AND gap<15) | 1,077 | 203 | 14.1 | **26.52** | 90.0 | +3.62 | 13.00 |
| **2 MID** | rp>=0.8 **OR** gap_1200<15 | 3,656 | 625 | 47.8 | **4.94** | 79.8 | +2.44 | 8.02 |
| **3 BASE** | neither | 2,922 | 509 | 38.2 | **3.49** | 78.6 | +1.98 | 2.86 |

26.5 / 4.9 / 3.5 across 14% / 48% / 38% of the book, monotone in PF,
win%, avg% AND 2022. Three independent families now feed it — price
extremity (the vote), liquidity (rp_vol x gap_1200), and leg geometry
(lowdens) — which is why the top tier reaches 26.5 without any single
feature doing the work.


## S43c — ⭐⭐ the ROSTER at a >=1 BAR: the vote became a UNION, and most voices stopped paying (2026-08-03)

This began as "does lowdens belong in the vote?" and ended somewhere
else entirely. Book throughout = v2.6, g60, $1+, mc=3 unless noted.
**Baseline: all 8 voices, votes >= 1 = 3,261 trips @ 3.613.**

### 1. lowdens is NOT a voice — at any threshold, in any form

Tested at **0.10 / 0.15 / 0.20 / 0.25** (fire rates 9.8 / 3.4 / 1.3 /
0.5% of the book), in three forms:

| form | ld>=0.10 | ld>=0.15 | ld>=0.20 | ld>=0.25 |
|------|---------:|---------:|---------:|---------:|
| ADD as a 9th voice | 3.600 | 3.584 | 3.584 | 3.613 |
| SWAP for esf | 3.775 | 3.804 | 3.776 | 3.801 |
| family-OR with esf | — | 3.584 | — | — |

("family-OR" = the S41w *max-within-family* rule for binary voices: the
family casts ONE vote, fired by EITHER member — as opposed to ADD,
which counts both and double-counts the shared information.)

**ADD never helps** (redundant — corr(lowdens, esf) = 0.783; lowdens is
essentially inverse leg duration, corr with bars_since_first_low =
-0.682). **family-OR is worse** — at a >= 1 bar, widening any voice
only admits more trips. Only SWAP improved... and the swap is
insensitive to the threshold, which was the tell.

### 2. ⚠ THE MISSING CONTROL — it was never lowdens

| construction | mc=1 | mc=3 |
|--------------|------|------|
| 8 voices (base) | 1,294 @ 3.434 | 3,261 @ 3.613 |
| **drop esf, add NOTHING** | **1,081 @ 3.572** | **2,748 @ 3.792** |
| swap esf -> lowdens >= 0.15 | 1,105 @ 3.558 | 2,816 @ 3.804 |
| swap esf -> lowdens >= 0.25 | — | 2,753 @ 3.801 |

**Dropping esf alone gets 3.792; adding lowdens on top adds 0.012 =
noise.** The whole "swap improves" result was esf being weak. Same
trap as S42x (v20 looked like the 2022 culprit until the control
showed removing it changed nothing) — **run the control before
crediting the new feature.**

### 3. Why the roster changed: >= 1 turns the vote into a UNION

At >= 2 a voice must be a good PARTNER. At >= 1 a voice can single-
handedly admit a trip, so **its entire contribution is the quality of
its SOLO trips**:

| voice | solo trips | tkds | solo PF | avg% | 2022 |
|-------|-----------:|-----:|--------:|-----:|-----:|
| **speed** | 239 | 76 | **1.79** | +1.34 | 2.55 |
| **pah** | 169 | 37 | **1.84** | +0.79 | — |
| esf | 1,094 | 266 | 4.07 | +1.75 | 3.85 |
| v20 | 108 | 27 | 6.13 | +2.66 | — |
| **haltband** | 323 | 61 | **9.28** | +2.44 | **16.8** |

(Book = 4.92 at mc=0.) esf dilutes by VOLUME — 1,094 solo trips just
below book — while speed and pah dilute by QUALITY.

**Leave-one-out, mc=3 (base 3,261 @ 3.613):**

| dropped | n | PF | delta |
|---------|--:|---:|------:|
| **halt band** | 3,143 | **3.539** | **-0.074** |
| dslo | 3,181 | 3.603 | -0.010 |
| ramp | 3,220 | 3.604 | -0.009 |
| v20 | 3,227 | 3.642 | +0.029 |
| d20a | 3,207 | 3.648 | +0.035 |
| pah | 3,207 | 3.702 | +0.089 |
| **esf** | 2,748 | **3.792** | **+0.179** |
| **speed** | 3,156 | **3.878** | **+0.265** |

**Only the halt band earns its seat at this bar.**

### 4. Tightening speed does not save it — it converges on deletion

| speed voice | n | PF |
|-------------|--:|---:|
| < -6% (current) | 3,261 | 3.613 |
| < -8% | 3,190 | 3.675 |
| < -10% | 3,169 | 3.820 |
| < -12% | 3,159 | **3.883** |
| **removed** | 3,156 | **3.878** |

Monotone, and -12% lands 3 trips from deletion. Speed's marginal
contribution as a VOICE is negative at every threshold. ⚠ This retires
speed **from the voice roster only** — it remains a first-class SIZING
feature (S42b: speed must be read AGAINST v20; its meaning inverts
with the vol state, which is exactly why a single one-sided threshold
cannot work as a voice).

### 5. The trimmed rosters

| roster | mc=1 | mc=3 | worst year (mc=3) |
|--------|------|------|------------------:|
| 8 voices | 1,294 @ 3.434 | 3,261 @ 3.613 | 2.63 (2025) |
| **6 voices (-speed, -pah)** | **1,224 @ 3.820** | **3,096 @ 4.023** | **3.11 (2021)** |
| 5 voices (-speed, -pah, -esf) | 972 @ 4.151 | 2,489 @ 4.408 | — |

**The 6-voice roster {v20, d20a, dslo, ramp, esf, haltband} is the
recommendation:** +0.41 PF at mc=3 for only **-5% trips** (3,261 ->
3,096), and it is the first construction all day to lift the WORST
YEAR off the ~2.5-2.8 floor — 2021 at **3.11**, with every year >= 3.11
(11.20 / 3.11 / 3.87 / 3.51 / 3.43 / 3.48 / 4.07). Recall S42z: no
vote-BAR change ever moved that floor. Trimming the ROSTER did.

Dropping esf too (5 voices) gives 4.408 but costs 24% of trips — the
usual concentration trade, and left as a user decision.

**House lesson (twice today):** when a candidate feature appears to
improve a construction, first run the construction WITHOUT the feature
and WITHOUT its predecessor. Both times, the credit belonged to a
deletion.


## S43d — the TANDEM test: speed and pah are REDUNDANT, not bad (user challenge) (2026-08-03)

User, twice: (1) "test whether speed actually contributes in tandem with
the other features", (2) "Pah is a deletion? Shouldn't it give a
benefit at >= 28%?" **Both challenges land. S43c's verdicts survive but
its REASONING was wrong — neither feature is bad; both are redundant.**

### speed IN TANDEM (g60, $1+, NO vote pre-filter)

| other voices | speed ON n / PF / avg% | speed OFF n / PF / avg% |
|-------------:|------------------------|-------------------------|
| 0 | 239 / **1.79** / +1.34 | 3,428 / 2.50 / +1.27 |
| 1 | 390 / **7.09** / +3.44 | 2,176 / 4.30 / +1.88 |
| 2 | 383 / 5.61 / +3.46 | 2,085 / 5.61 / +2.29 |
| 3 | 319 / 5.24 / +3.57 | 1,114 / 6.46 / +2.63 |
| 4 | 136 / 4.81 / +3.18 | 439 / 6.26 / +3.02 |
| 5 | 103 / 3.26 / +2.32 | 205 / 2.74 / +2.07 |

**Speed-on trips have a HIGHER AVERAGE RETURN at every single level**
(+50-80% at levels 1-3). "Speed dilutes" was wrong: it marks
higher-VARIANCE, higher-MEAN trades — bigger wins and bigger losses,
which raises avg% while often lowering PF. Pairwise it is a genuine
confirmer: **speed x haltband = 11.90** (378) vs haltband alone 5.16;
speed x d20a 6.95 vs 5.94.

**Why it still leaves the roster:** at a >= 1 bar a voice's ONLY
contribution is the trips it admits ALONE — those 239 at 1.79. The
tandem trips are already in the book via their other voices. **Keeping
speed in the roster does not buy the tandem benefit; you already have
it.** In the traded 6-voice book speed-on = 475 trips @ **+2.68% avg**
vs +1.92 (PF 3.77 vs 4.09, win 77.5 vs 79.1) — a legitimate but
AMBIGUOUS sizing input (size up for expectancy, down for smoothness).

### pah IN ISOLATION — the user was right, the threshold is book-wrong

| band | FULL book PF | g60 book PF |
|------|-------------:|------------:|
| <0 | 2.31 | 3.73 |
| [0,10) | 2.28 | 2.91 |
| **[10,20)** | 2.60 | **6.50** |
| [20,28) | 2.31 | 3.26 |
| [28,45) | **3.28** | 4.16 |
| [45,80) | **3.42** | 5.02 |
| >=80 | **4.14** | 4.72 |
| (book) | 2.544 | 4.003 |

On the FULL book pah is cleanly monotone above 28 (3.28/3.42/4.14 vs
book 2.544) — the >= 28 threshold is well calibrated THERE. **On g60 it
is not monotone**: the >= 28 cells sit barely above the 4.00 book while
the best cell is **[10,20) = 6.50** (1,538 trips, 297 tkds, every year
>= 2.79). The threshold was calibrated on a book we no longer trade.

**But re-thresholding does not rescue it as a VOICE** (mc=3; no-pah =
3,207 @ 3.702):

| variant | n | PF | solo trips | solo PF |
|---------|--:|---:|-----------:|--------:|
| pah >= 28 (current) | 3,261 | 3.613 | 169 | 1.84 |
| pah [10,20) | 3,435 | 3.576 | 590 | 3.67 |
| **pah >= 45** | **3,207** | **3.702** | **0** | — |
| **pah >= 80** | **3,207** | **3.702** | **0** | — |

⭐ **At >= 45 the voice NEVER FIRES ALONE — it is identical to deleting
it, to the trip.** Every high-pah trip is already admitted by another
voice. Below 45 the trips it uniquely adds are weak (1.84 at >= 28) or
merely below-book (3.67 at [10,20) vs the 4.92 mc=0 book).

**Corrected verdict: pah is REDUNDANT, not bad.** Its signal is real
and already captured; as an admitter it can only add what the others
declined. Out of the roster, feature retained (the g60 [10,20) cell is
a sizing candidate).

### The rule this establishes

**A voice earns its seat ONLY by the quality of the trips it admits
ALONE.** A feature can be genuinely predictive, genuinely confirming in
tandem, and still worth nothing as a voice — because at a >= 1 bar the
vote is a UNION and everything else it touches is already in the book.
That is the third time today a feature's apparent contribution
dissolved under the right control (v20/S42x, lowdens/S43c, and now
speed and pah).


## S43e — the DAY-STRUCTURE family is ONE feature; dslo keeps the seat (2026-08-03)

User: "instead of the distance to arming high from OPEN, can we try
distance to arming high from SESSION LOW? The g60 book is more volatile
so that might be throwing off the threshold." (The feature in question
uses `chan_hi` = the rolling 20m channel high — see the terminology fix
below.)

**First, an algebraic simplification worth recording:**

    pah = (1 + pct_chg_open) * chan_hi / signal_vwap - 1
        = (vwap/open) * (chan_hi/vwap) - 1
        = chan_hi / open - 1

The signal price CANCELS. pah was never a composite — it is purely
**the 20m CHANNEL HIGH above the day's open**. The proposed swap is
therefore exactly: change the anchor from `open` to `sess_low`.

    dhl = chan_hi / sess_low - 1

⚠ **TERMINOLOGY FIX (user caught, same day): `chan_hi` is NOT the
"arming high".** It is `ChanHi` = *the strictly-prior ENTRY-channel max
at the signal* (Intraday.fs:318), and `EntryChannelBars = 1200`, so it
is the **rolling ~20-minute channel high, recomputed every bar**. The
ARMING high is a different, FROZEN quantity — `d_hi_flow`, captured at
the leg's first low (S41e) — and that is what feeds `d20a`. The two are
related but not the same; the S43d/S43e prose said "arming high" where
it meant "20m channel high". The MEASUREMENTS are unaffected (every
query used the `chan_hi` column), only the label was wrong.

**Both halves of the simplification were verified, not just derived:**
`PctChgOpen = bar.vwap / openVwap - 1` where `openVwap` = the first-RTH-bar
vwap (Intraday.fs:1777), so `(1+pco) = signal_vwap/open`. And
`signal_vwap/(1+pct_chg_open)` is **constant within every one of 4,061
multi-trip ticker-days — max relative spread 0.0** — confirming the
anchor is a fixed session open rather than anything rolling.

### The family is one feature

| pair (g60) | corr |
|------------|-----:|
| pah vs dslo | 0.943 |
| pah vs dhl | 0.951 |
| **dhl vs dslo** | **0.975** |
| dbh (chan_hi/vwap-1) vs dslo | 0.102 |
| dbh vs d20a | **-0.767** |
| dbh vs v20 | **0.826** |

**dhl is not a pah replacement — it is a dslo TWIN (0.975).** And the
one piece of dhl that is NOT dslo (`dbh`) is already carried by d20a
(-0.767) and v20 (0.826). ⭐ **This is the deeper explanation for S43d:
pah could never earn a voice seat because `dslo` was already holding
the family's seat all along.**

### The user's intuition about the ANCHOR was right

dhl bands, g60, no vote filter — compare to pah's noisy
3.73 / 2.91 / 6.50 / 3.26 / 4.16 / 5.02 / 4.72:

| dhl band | n | tkds | PF | worst year |
|----------|--:|-----:|---:|-----------:|
| <15 | 1,480 | 364 | 2.56 | 0.82 |
| [15,22) | 2,451 | 478 | 2.71 | 1.18 |
| [22,30) | 1,778 | 372 | 4.27 | 2.38 |
| [30,45) | 1,969 | 359 | **5.49** | 2.79 |
| [45,70) | 1,644 | 271 | **7.17** | 2.48 |
| [70,120) | 1,163 | 165 | 3.22 | 1.23 |
| >=120 | 598 | 75 | 6.74 | 1.05 |

**Monotone 2.56 -> 7.17 where pah zig-zags.** The session low IS the
better anchor for a volatile book — exactly as predicted.

### But the better-behaved feature does NOT make the better voice

Family-seat contest, 6-voice roster, mc=3:

| day-structure voice | n | PF | solo trips | solo PF |
|---------------------|--:|---:|-----------:|--------:|
| **dslo >= 16 (incumbent)** | 3,096 | **4.023** | 1,773 | **4.70** |
| dhl >= 45 | 2,919 | 3.987 | 1,355 | 4.35 |
| dhl in [30,70) | 2,998 | 3.797 | 1,518 | 4.67 |
| dhl >= 30 | 3,240 | 3.691 | 2,179 | 3.96 |
| *(no day-structure voice)* | 2,388 | **4.078** | — | — |

**dslo keeps the seat.** dhl's cleaner band table reflects the
population it SELECTS — most of which the other voices already admit.
What decides a voice is the quality of what it admits ALONE, and there
dslo wins (4.70 vs 4.35/4.67/3.96). **Fourth time today that a feature
looked better in isolation than it performs as a voice.**

⏭ Open user decision: dropping the day-structure voice entirely is the
highest-PF construction tested (2,388 @ **4.078**) at -23% trips — the
same concentration trade as the 5-voice roster.


## S43f — dhla tested both ways; the day-structure family is CLOSED (2026-08-03)

User: "let's also try dhla = d20a x dslo", then: "One disadvantage of
that is that it wouldn't take the current price into account, but maybe
that could be fine..."

Two readings, both computed (the notation was ambiguous):

    dhla_ratio = arming_high / sess_low - 1   = (1+dslo)/(1+d20a) - 1
                 [the geometric analogue of dhl, anchored on the FROZEN
                  arming high instead of the rolling 20m channel high]
    dhla_prod  = |d20a| x dslo                [the literal product]

### Neither is new information

| | vs dslo | vs d20a | vs dhl |
|---|--------:|--------:|-------:|
| dhla_ratio | **0.955** | -0.365 | **0.981** |
| dhla_prod | **0.958** | -0.201 | — |

⚠ **`dhla_prod` is also DEGENERATE**: q10 = q25 = **0.0**, median 0.6 —
because ~45% of trips sit EXACTLY at the session low (the S41p mass
point), so `dslo = 0` and any product with it collapses to zero for the
largest single group in the book.

### ⭐ The user's own objection, answered empirically

The price-free forms (dhl, dhla_ratio) are ratios of two LEVELS — the
current price cancels. That does NOT buy independence: both still
correlate **0.955-0.975 with the price-based dslo**. The decomposition
says why — `(1+dhl) = (1+dbh)(1+dslo)` and the flush-depth factor
`dbh` (median 18.7%) varies far less than `dslo`, so the product tracks
dslo whichever high you anchor on.

**And discarding the price is the wrong trade anyway.** For a
mean-reversion ENTRY, "how far above the low am I *right now*" (dslo,
price-dependent) is the tradeable fact; "how big is the day's range"
(dhl / dhla, price-free) is background. The price-free forms throw away
precisely the part that decides the entry — which is why they lose:

| day-structure voice | n | PF |
|---------------------|--:|---:|
| **dslo >= 16 (incumbent)** | 3,096 | **4.023** |
| dhla_ratio >= 65 | 2,755 | 3.966 |
| dhla_ratio >= 45 | 3,003 | 3.932 |
| dhla_ratio >= 30 | 3,493 | 3.540 |
| dhl >= 45 (S43e) | 2,919 | 3.987 |
| dhl >= 30 (S43e) | 3,240 | 3.691 |

In isolation dhla_ratio is well-behaved like dhl (bands 2.34 / 3.23 /
4.88 / **6.28** / 4.44 / 4.10) — and, like dhl, that does not survive
into the voice test.

### ⛔ FAMILY CLOSED

Five formulations tested — **pah** (chan_hi/open), **dslo**
(vwap/sess_low), **dhl** (chan_hi/sess_low), **dhla_ratio**
(arming_high/sess_low), **dhla_prod** (|d20a| x dslo) — all
pairwise 0.94-0.98, all beaten by the incumbent. **`dslo >= 16` keeps
the day-structure seat; the family is closed.** Anchoring on a
different high, a different low, or removing the price entirely does
not produce a new feature — it produces dslo in a new costume.


## S43g — dslo TUNED on the g60 book: the threshold is not a sensitive parameter (2026-08-03)

User: "We'll remove pah from the family. We might also want to tune
dslo on the g60 book. I haven't done that properly before deciding."
**pah is OUT.** dslo's >= 16 threshold predates the g60 book, so it was
never calibrated where we trade.

**The mass point first (census before profiling):** **44.1%** of the g60
book (4,886 of 11,083) sits at `dslo = 0` — the flush IS the session
low. `min(dslo) = 0.0` exactly, so the feature is one-sided. Above it:
q60 = 11.3, q75 = 25.4, q90 = 54.6.

**dslo fine bands, g60, no vote filter** (book = 4.003):

| band | n | tkds | n_lose | PF | p20 | p21 | p22 | p23 | p24 | p25 | p26 | avg% |
|------|--:|-----:|-------:|---:|----:|----:|----:|----:|----:|----:|----:|-----:|
| AT low (=0) | 4,886 | 844 | 1,113 | 3.36 | 7.97 | 4.97 | 4.05 | 2.77 | 2.67 | 2.82 | 2.66 | 1.85 |
| (0,4) | 739 | 177 | 149 | 3.01 | 6.38 | 3.62 | 87.35 | 0.81 | 3.73 | 1.10 | 4.95 | 1.67 |
| [4,8) | 523 | 147 | 115 | 5.03 | 3.67 | 28.17 | 11.10 | 1.30 | 5.54 | 2.60 | 71.59 | 2.19 |
| **[8,12)** | 605 | 153 | 85 | **10.52** | 9.33 | 8.15 | 3.06 | 157.4 | 11.88 | 10.15 | 40.19 | 2.36 |
| [12,16) | 527 | 133 | 119 | 4.36 | 8.19 | 3.07 | 14.99 | NULL | 31.93 | 2.25 | 3.16 | 1.88 |
| [16,22) | 678 | 141 | 88 | 5.96 | 95.85 | 7.05 | NULL | 0.71 | 36.15 | 4.13 | 11.16 | 2.27 |
| [22,30) | 660 | 135 | 139 | 5.53 | 13.63 | 5.22 | NULL | 1.06 | 4.67 | 3.65 | 64.85 | 2.27 |
| [30,45) | 939 | 179 | 198 | 5.24 | 14.54 | 3.02 | 28.41 | 4.09 | 2.52 | 5.75 | 19.38 | 2.34 |
| >=45 | 1,526 | 203 | 305 | 3.95 | 7.65 | 3.19 | 1.23 | 20.72 | 3.81 | 2.72 | 6.07 | 2.54 |

The at-low group is the weakest large cell (3.36) — the S41n
"flush NOT the day's first low" structure, confirmed on this book.
[8,12) shows a spike (10.52, every year >= 3.06) but its NEIGHBOUR
[12,16) reads 4.36 — a 2.4x discontinuity between adjacent bands is
much more likely noise than structure, so it is NOT actioned.

**Solo-trip quality by threshold** (what a voice actually admits):

| threshold | solo trips | tkds | solo PF | avg% |
|-----------|-----------:|-----:|--------:|-----:|
| **>= 16 (current)** | 1,596 | 284 | **4.63** | +2.01 |
| >= 8 | 2,120 | 396 | 4.48 | +1.91 |
| >= 22 | 1,288 | 230 | 4.47 | +2.04 |
| >= 12 | 1,813 | 336 | 4.21 | +1.90 |
| > 0 | 2,855 | 547 | 3.98 | +1.81 |
| >= 30 | 968 | 171 | 3.84 | +2.04 |
| >= 45 | 573 | 97 | 3.38 | +2.06 |

**Book level, 7-voice roster (pah removed), mc=3:**

| dslo voice | n | PF | avg% |
|------------|--:|---:|-----:|
| > 0 | 3,746 | 3.468 | +1.84 |
| **>= 8** | **3,444** | 3.677 | +1.92 |
| >= 12 | 3,313 | 3.649 | +1.93 |
| **>= 16 (current)** | 3,207 | **3.702** | +1.96 |
| >= 22 | 3,091 | 3.689 | +1.97 |
| >= 30 | 2,977 | 3.694 | +2.01 |
| *[8,12) band only* | *2,757* | *3.770* | *+2.03* |
| *>= 8 excl [12,16)* | *3,361* | *3.739* | *+1.94* |

⭐ **The sweep is FLAT from 8 to 30 — 3.649 to 3.702, a 0.053 spread.**
The only threshold that clearly loses is `> 0` (3.468), i.e. admitting
the (0,8) sliver. **dslo's threshold is not a sensitive parameter**;
the incumbent 16 sits at the top of a plateau, not on a peak.

Two carved constructions (the [8,12) band, and >=8 excluding the
[12,16) dip) score higher — 3.770 and 3.739, the latter on MORE trips
than the incumbent. **Both are rejected as curve-fitting**: they exist
only because one adjacent band happened to read low, and the S42s
lesson (a 7-tkd cell dressed as a gate) applies directly.

**Verdict: keep dslo >= 16, or move to >= 8 for volume.** >= 8 buys
+237 trips (+7.4%, ~+5% slot points) for -0.025 PF. Given the user
chose the >= 1 bar explicitly for volume, **>= 8 is the consistent
choice**; >= 16 is the equally-defensible status quo. Either way this
is a coin-flip parameter, which is itself the useful finding — no
further dslo tuning is warranted.


## S43h — ⭐⭐ speed moved OUTSIDE the family: it is a REGIME SPLIT, not a filter (2026-08-03)

Two user decisions: **dslo -> >= 8%** (S43g), and **speed out of the
voice family**, tested instead as a CONJUNCTION and its complement.

⚠ **Framing (user):** the spec's `speed < -2%/1m` HARD GATE is
untouched — it is part of the signal definition. The `-6%` cut is a
SEPARATE, deeper marker for **deep flushes** *within* the already-gated
book. So the complement below is not "no speed filter"; it is
**moderate flush, speed in [-6%, -2%)**.

### THE NEW CANONICAL ROSTER — 6 voices, bar >= 1

> 🛑 **SUPERSEDED 2026-08-05 (S43an): `|esf| >= 0.5` IS REPLACED BY
> `bars_since_first_low <= 390`.** The esf voice was a LEG-AGE voice in
> disguise (corr -0.65 with leg span; a raw age voice reproduces it to
> within noise). Current roster:
> `{v20 >= 140bp, d20a < -28%, dslo >= +8%, ramp < -12,`
> `bars_since_first_low <= 390, haltband ssh in [20,80m)}`
> mc=3 = **2,997 @ 4.550 / +2.14%**, worst year **3.325** — better than the
> esf roster in ALL SEVEN YEARS.

    {v20 >= 140bp, d20a < -28%, dslo >= +8%,
     ramp < -12, |esf| >= 0.5, haltband ssh in [20,80m)}
    universe: v2.6 spec, g60 (gap_60 < 4), $1+

| mc | n | PF | win% | avg% | med% |
|---:|--:|---:|-----:|-----:|-----:|
| 1 | 1,325 | 3.737 | 77.6 | +1.87 | +2.24 |
| 2 | 2,433 | 3.821 | 78.1 | +1.93 | +2.30 |
| 3 | 3,347 | **3.939** | 78.4 | +1.98 | +2.35 |

By year (mc=3): 9.57 / 2.89 / 3.94 / 3.65 / 3.59 / 3.26 / 4.19 —
every year >= 2.89.

⚠ **Cost of the dslo 16 -> 8 move, stated plainly:** it buys +251 trips
(3,096 -> 3,347) but gives back 0.084 PF (4.023 -> 3.939) **and drops
the worst year from 3.11 to 2.89** — the floor lift reported in S43c
does not survive it. Volume was the user's stated priority; the trade
is real and should be re-examined if the floor matters more later.

### ⭐ The speed split — deep vs moderate flush (mc=3)

| book | n | PF | avg% | 2020 | 2021 | **2022** | 2023 | 2024 | 2025 | 2026 |
|------|--:|---:|-----:|-----:|-----:|---------:|-----:|-----:|-----:|-----:|
| all (baseline) | 3,347 | 3.939 | +1.98 | 9.57 | 2.89 | 3.94 | 3.65 | 3.59 | 3.26 | 4.19 |
| **deep: speed < -6%** | 751 | **4.204** | **+2.88** | 14.28 | 5.70 | **0.72** | 2.47 | 4.50 | 3.45 | 6.81 |
| **moderate: [-6%,-2%)** | 2,989 | 4.043 | +1.92 | 9.58 | 2.79 | **4.04** | 4.16 | 3.66 | 3.46 | 4.24 |

**This is the cleanest regime split in the system.**

- **Deep flush = the high-expectancy BULL book.** +2.88%/trip vs
  +1.92 — **50% more per trade** — and the best PF (4.204). But 2022 is
  a knife: **PF 0.717, -1.34%/trip, 61.9% win on 21 trips.**
- **Moderate flush = the ALL-WEATHER book.** Every year >= 2.79 and
  **2022 = 4.042**, its second-best year.

This is the S42b contrast grammar at book level: **violence pays in a
rising tape and is a developing crash in a falling one.** The -6% cut
does not separate good trades from bad — it separates
regime-CONDITIONAL trades from regime-ROBUST ones.

### Verdict on speed

| use | verdict |
|-----|---------|
| as a VOICE (union member) | **NO** — solo trips 1.79 (S43c/d) |
| as an always-on CONJUNCTION | **NO** — cuts to 751 trips AND buys the 2022 knife (0.717) |
| as a REGIME-CONDITIONAL SIZE lever | **YES** — +50% expectancy, explicitly discounted in a bear |

The `-2%` spec gate stays as the signal definition. The `-6%` marker
becomes a sizing flag with a regime rule attached — the same treatment
v20 votes already carry (S42f).


## S43i — DEEP FLUSHES in multi-day context: the LowFlyer chg_1d / chg_3d idea (2026-08-03)

> # 🛑🛑 INVALID — chg_1d LOOKAHEAD (found 2026-08-04, see S43v)
>
> **Every `chg_1d` / `chg_3d` number in S43i through S43u is WRONG.** The
> formula used was
>
> ```
> chg_1d = signal_vwap * adj_ratio / prev_adj_close - 1     # ← WRONG
> ```
>
> but 1s bars arrive from the loader **already split-adjusted**
> (`Intraday.fs`: *"vwap = raw × adj_ratio"*), so `signal_vwap` is ALREADY
> in the adjusted scale. Applying `adj_ratio` a second time left an
> **uncancelled future-split factor** in the filter — and `adj_ratio =
> adj_close/raw_close` is adjusted for **all splits AFTER day D**. The
> correct form, in which the adjusted basis cancels between numerator and
> denominator, is:
>
> ```
> chg_1d = signal_vwap / prev_adj_close - 1                 # ← CORRECT
> chg_3d = signal_vwap / close_3d       - 1
> ```
>
> **Consequences:** 43.6% of the book has `adj_ratio != 1` (p90 = 370, max
> = 6e7 — these are microcaps that later reverse-split). Median `chg_1d`
> was inflated 50% -> 166%. **`chg_1d >= 300%` was substantially a
> FUTURE-REVERSE-SPLIT DETECTOR**, and **71.4% of the "S-tier B" book was
> that artifact** (2,720 of 3,811 trips, median `adj_ratio` = 25).
>
> **VERDICT (S43v): the `chg_1d` gate is DROPPED entirely.** `chg_1d < 0`
> survives as a SIZING tier only. Read S43i-S43u for method and for the
> non-chg_1d findings (halts, costs, voices, the universe gate — all
> unaffected); do NOT quote their chg_1d numbers or the S-tier B book.


User: "LowFlyer really benefited from chg_1d and chg_3d in conjunction
with this feature... sharp flushes into support worked much better than
when the stock was sliding off. We want buying deep flushes to be into
support."

**Definitions (user-corrected: measured to the ENTRY, not to D-1):**

    chg_1d = signal_vwap * adj_ratio / prev_adj_close - 1     (D-1 close -> entry)
    chg_3d = signal_vwap * adj_ratio / close_3d        - 1     (D-3 close -> entry)

**Knowability:** `prev_adj_close` = D-1 close, `close_3d` =
`LAG(adj_close,3)` = D-3 close (build_mr_candidate_1s.fsx:154) — both
fixed before the open; `signal_vwap` is current. `day_close` and
`close_fwd_*` are NEVER touched. adj_ratio puts the prior closes in
today's raw scale.

**Census — this is a parabolic-runner population:**

| scope | chg_1d q25 / med / q75 | chg_3d q25 / med / q75 | n |
|-------|------------------------|------------------------|--:|
| g60 universe | 39.9 / **168.1** / 2611.2 | 69.1 / **258.4** / 3405.0 | 11,083 |
| deep-flush book | 55.0 / **203.8** / 2633.2 | 89.9 / **274.3** / 3330.1 | 1,363 |

The median deep flush is on a stock up **204% on the day** and **274%
over three days**. "Down to entry" therefore means a name that ran
earlier and is now RED on the session — not a quiet stock.

### chg_1d — DEEP-FLUSH book (6-voice >= 1, speed < -6%)

| band | n | tkds | n_lose | PF | win% | p22 | p25 | yrs | avg% |
|------|--:|-----:|-------:|---:|-----:|----:|----:|----:|-----:|
| **<0 (down to entry)** | 88 | 25 | 8 | **30.49** | 90.9 | NULL | 22.37 | 7 | +4.39 |
| [0,25) | 99 | 23 | 23 | 3.86 | 76.8 | 0.0 | 1.70 | 7 | +2.60 |
| **[25,60)** | 172 | 41 | 59 | **1.77** | 65.7 | NULL | 3.65 | 7 | +1.06 |
| [60,150) | 220 | 54 | 48 | 2.60 | 78.2 | 0.0 | 13.17 | 7 | +1.87 |
| [150,400) | 217 | 49 | 23 | 5.57 | 89.4 | 0.0 | 2.05 | 7 | +3.81 |
| **>=400** | 567 | 122 | 90 | **12.71** | 84.1 | 5.41 | 9.77 | 7 | +4.57 |

### chg_3d — DEEP-FLUSH book

| band | n | tkds | n_lose | PF | win% | p22 | p25 | yrs | avg% |
|------|--:|-----:|-------:|---:|-----:|----:|----:|----:|-----:|
| <0 (down to entry) | 47 | 11 | 8 | 11.93 | 83.0 | NULL | NULL | 7 | +3.75 |
| [0,25) | 51 | 14 | 5 | 12.78 | 90.2 | NULL | 42.68 | 6 | +3.64 |
| **[25,60)** | 124 | 33 | 34 | **2.76** | 72.6 | 1.63 | 35.70 | 7 | +2.01 |
| [60,150) | 260 | 60 | 47 | 4.26 | 81.9 | NULL | 25.08 | 6 | +2.15 |
| [150,400) | 273 | 60 | 53 | 4.52 | 80.6 | NULL | 2.07 | 6 | +3.43 |
| **>=400** | 608 | 134 | 104 | **7.56** | 82.9 | 0.82 | 3.82 | 7 | +4.20 |

### chg_1d — the WHOLE g60 UNIVERSE (the control: no vote, no speed cut)

| band | n | tkds | n_lose | PF | win% | p22 | p25 | yrs | avg% |
|------|--:|-----:|-------:|---:|-----:|----:|----:|----:|-----:|
| <0 (down to entry) | 873 | 174 | 219 | **3.38** | 74.9 | 4.96 | 10.64 | 7 | +1.71 |
| [0,25) | 1,231 | 213 | 280 | 2.97 | 77.3 | 28.41 | 2.28 | 7 | +1.68 |
| [25,60) | 1,490 | 285 | 338 | 3.51 | 77.3 | 5.58 | 3.43 | 7 | +1.69 |
| [60,150) | 1,774 | 272 | 373 | 2.92 | 79.0 | 3.18 | 2.68 | 7 | +1.75 |
| [150,400) | 1,127 | 135 | 276 | 3.36 | 75.5 | 0.0 | 1.83 | 7 | +2.18 |
| **>=400** | 4,588 | 719 | 825 | **5.91** | 82.0 | 8.42 | 3.72 | 7 | +2.47 |

### chg_3d — the WHOLE g60 UNIVERSE

| band | n | tkds | n_lose | PF | win% | p22 | p25 | yrs | avg% |
|------|--:|-----:|-------:|---:|-----:|----:|----:|----:|-----:|
| <0 (down to entry) | 492 | 100 | 125 | 2.43 | 74.6 | 10.76 | 3.55 | 7 | +1.45 |
| [0,25) | 655 | 136 | 175 | 2.52 | 73.3 | NULL | 1.01 | 7 | +1.33 |
| [25,60) | 1,311 | 261 | 265 | 4.17 | 79.8 | 7.65 | 5.49 | 7 | +1.85 |
| [60,150) | 1,914 | 331 | 448 | 2.99 | 76.6 | 2.85 | 2.97 | 7 | +1.66 |
| [150,400) | 1,779 | 220 | 396 | 3.52 | 77.7 | 14.40 | 3.02 | 7 | +2.11 |
| **>=400** | 4,932 | 757 | 902 | **5.42** | 81.7 | 3.22 | 2.93 | 7 | +2.43 |

### Reading

**Two different things, separated by the control:**

1. **An INTERACTION, deep-flush-specific.** `chg_1d < 0` reads **30.49**
   on deep flushes vs **3.38** on the universe — a 9x gap. This is the
   "into support" case and it exists ONLY when the flush is violent.
   Small: 88 trips / 25 tkds / all 7 years.
2. **A MAIN EFFECT, present everywhere.** `>= 400%` is the best band in
   all four tables (12.71 / 7.56 / 5.91 / 5.42). Extreme runners fade
   well regardless of flush depth; a deep flush merely amplifies it.
   And it carries real size — **567 trips / 122 tkds** on the deep book.
3. **A genuine AVOID.** Deep flush with `chg_1d` in [25,150) = 392 trips
   (29% of the deep book) at 1.77-2.60 and 65.7% win in the worst band.
   This is the user's "sliding off": up modestly, no trend bid, no
   support, just a break.

**chg_1d beats chg_3d as the axis** — its down-band is 30.49 vs 11.93
and its trough is deeper (1.77 vs 2.76). The extra two days of context
blur the signal rather than sharpen it. ⚠ Both are thin at the tails
and the 2022 columns are mostly NULL/0 — these are SIZING tiers, not
gates.


## S43j — the deep-flush book by YEAR: 2022 is ONE ticker-day, not a regime (2026-08-03)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


User: "2022 was negative for it, right? I imagine that in a bear market
like it, there weren't many large gainers that this method prefers."
**Half right on the fact, and the mechanism is REJECTED by the data.**

### 1. The deep-flush book by year (mc=0)

| yr | n | tkds | n_lose | PF | win% | avg% | med% | worst | net pts |
|----|--:|-----:|-------:|---:|-----:|-----:|-----:|------:|--------:|
| 2020 | 157 | 35 | 21 | 20.19 | 86.6 | +4.42 | +4.83 | -3.9 | +694 |
| 2021 | 161 | 37 | 37 | 7.06 | 77.0 | +3.24 | +2.96 | -7.9 | +522 |
| **2022** | **32** | **9** | 8 | **1.16** | 75.0 | **+0.48** | +4.30 | **-28.1** | **+16** |
| 2023 | 120 | 27 | 39 | 3.30 | 67.5 | +2.43 | +1.81 | -10.3 | +291 |
| 2024 | 291 | 65 | 46 | 6.15 | 84.2 | +3.74 | +3.37 | -21.9 | +1,087 |
| 2025 | 343 | 80 | 60 | 4.53 | 82.5 | +2.79 | +3.12 | -15.9 | +957 |
| 2026 | 259 | 52 | 40 | 7.42 | 84.6 | +4.21 | +3.68 | -10.3 | +1,091 |

⚠ **Correction to the question:** at mc=0 2022 is marginally POSITIVE
(1.16 / +0.48% / +16 pts), not negative. It reads negative at mc=3
(0.717 / -1.34%) because the slot selector concentrates into the bad
day. Note also the median trip in 2022 is **+4.30%** — the HIGHEST
median of any year. The mean is dragged below it.

### 2. ⭐ THE MIX TEST — the hypothesis is REJECTED

chg_1d band composition, % of each year's deep flushes:

| yr | %down | %[0,25) | %[25,150) AVOID | %[150,400) | **%>=400 BEST** | median chg_1d |
|----|------:|--------:|----------------:|-----------:|----------------:|--------------:|
| 2020 | 4.5 | 3.8 | 21.0 | 10.8 | **59.9** | 1,429 |
| 2021 | 9.3 | 7.5 | 30.4 | 8.1 | 44.7 | 197 |
| **2022** | 15.6 | 3.1 | 15.6 | 6.3 | **59.4** | **931** |
| 2023 | 11.7 | 11.7 | 25.8 | 8.3 | 42.5 | 163 |
| 2024 | 5.5 | 6.2 | 26.1 | 11.7 | 50.5 | 487 |
| 2025 | 2.6 | 7.9 | 30.9 | 21.9 | 36.7 | 210 |
| 2026 | 8.5 | 8.1 | 35.5 | 25.5 | **22.4** | 144 |

**2022 had the SECOND-HIGHEST share of >=400% runners (59.4%) and the
second-highest median chg_1d (931%)** — and the whole g60 universe
agrees, even more strongly:

| yr | universe n | %>=400 | median chg_1d |
|----|-----------:|-------:|--------------:|
| 2020 | 1,801 | 50.1 | 456 |
| 2021 | 2,203 | 49.9 | 385 |
| **2022** | **539** | **62.0** | **1,321** |
| 2023 | 881 | 48.0 | 243 |
| 2024 | 1,824 | 43.8 | 204 |
| 2025 | 2,546 | 31.1 | 111 |
| 2026 | 1,289 | 18.7 | 79 |

**2022 has the HIGHEST runner share and the HIGHEST median move of any
year in this universe.** The bear market shrank the universe hard (539
trips vs 1,800-2,500) but the names that DID qualify were the most
extreme of the whole sample — 2022 was the year of the violent microcap
squeeze (HKD/AMTD is in the book). **Fewer opportunities, not tamer
ones.** The scarcity is real; the "no large gainers" mechanism is not.

### 3. So where did 2022 actually go? ONE ticker-day.

Every 2022 ticker-day in the deep-flush book:

| symbol | date | n | med chg_1d | losers | worst | net pts |
|--------|------|--:|-----------:|-------:|------:|--------:|
| **ENSV** | 2022-03-08 | 3 | 156 | 3 | **-28.1** | **-81.2** |
| BSFC | 2022-01-21 | 2 | 234,768 | 2 | -6.8 | -13.4 |
| NRSN | 2022-06-29 | 1 | 0 | 1 | -3.3 | -3.3 |
| TMC | 2022-03-08 | 4 | 34 | 0 | +1.0 | +5.4 |
| KALA | 2022-12-28 | 5 | 16,633 | 2 | -0.7 | +8.5 |
| SPRC | 2022-11-07 | 2 | 584,142 | 0 | +4.1 | +8.8 |
| ATXI | 2022-09-23 | 2 | 4,037 | 0 | +9.5 | +21.3 |
| **HKD** | 2022-09-21 | 5 | **-8** | 0 | +5.0 | +30.8 |
| SST | 2022-04-08 | 8 | 932 | 0 | +4.1 | +38.7 |

| 2022 | n | tkds | PF | avg% | net pts |
|------|--:|-----:|---:|-----:|--------:|
| as-is | 32 | 9 | 1.16 | +0.48 | +16 |
| **minus ENSV 03-08 only** | 29 | 8 | **6.43** | **+3.33** | **+97** |

**ENSV 2022-03-08 alone (3 trips) is -81 points against a +16 year.**
Remove that one ticker-day and 2022 is 6.43 / +3.33%/trip — an ordinary
good year. Seven of nine 2022 ticker-days were profitable.

⭐ And note **HKD 2022-09-21**: median chg_1d **-8%** — the INTO-SUPPORT
case (S43i) — 5 trips, zero losers, +30.8 pts, the second-best day of
the year. The into-support cell worked in the bear too.

**Conclusion: 2022's deep-flush weakness is a 9-ticker-day sample with
one bad day in it, not a regime effect.** The honest statement is
SCARCITY (32 trips vs 157-343), and scarcity is exactly what should be
expected when the universe shrinks 70%.


## S43k — the two requested chg_1d tables, and WHERE to cut (2026-08-03)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


### g60 + DEEP SPEED (speed < -6%), NO vote

| band | n | tkds | n_lose | PF | win% | p20 | p21 | p22 | p23 | p24 | p25 | p26 | avg% | net pts |
|------|--:|-----:|-------:|---:|-----:|-----|-----|----:|----:|-----|----:|----:|-----:|--------:|
| **<0 (down to entry)** | 109 | 30 | 19 | **7.62** | 82.6 | NULL | NULL | 3.13 | 1.30 | 937.5 | 31.99 | 2.44 | +3.54 | +386 |
| [0,25) | 132 | 29 | 39 | 2.86 | 70.5 | NULL | NULL | 0.0 | 1.15 | 37.5 | 1.54 | 1.47 | +2.23 | +295 |
| **[25,60)** | 194 | 51 | 67 | **1.60** | 65.5 | 10.23 | 3.49 | NULL | NULL | 1.87 | 1.71 | 1.03 | +0.90 | +175 |
| [60,150) | 263 | 62 | 59 | 2.79 | 77.6 | 16.38 | 0.16 | 0.0 | 0.19 | 3.15 | 14.09 | 4.35 | +1.94 | +511 |
| [150,400) | 219 | 50 | 23 | 5.62 | 89.5 | NULL | 18.75 | 0.0 | 3.85 | 220.88 | 2.10 | NULL | +3.82 | +837 |
| **>=400** | 674 | 147 | 106 | **7.43** | 84.3 | 17.12 | 17.82 | 6.73 | 20.87 | 10.24 | 2.10 | 21.53 | +4.09 | +2,758 |

### g60 + VOTE (>= 1 of 6) + DEEP SPEED

| band | n | tkds | n_lose | PF | win% | p20 | p21 | p22 | p23 | p24 | p25 | p26 | avg% | net pts |
|------|--:|-----:|-------:|---:|-----:|-----|-----|-----|----:|-----|----:|-----|-----:|--------:|
| **<0 (down to entry)** | 88 | 25 | 8 | **30.49** | 90.9 | NULL | NULL | NULL | 1.30 | NULL | 22.37 | NULL | +4.39 | +386 |
| [0,25) | 99 | 23 | 23 | 3.86 | 76.8 | NULL | NULL | 0.0 | 1.15 | 15.13 | 1.70 | 19.85 | +2.60 | +257 |
| **[25,60)** | 172 | 41 | 59 | **1.77** | 65.7 | 10.23 | 3.50 | NULL | NULL | 1.78 | 3.65 | 0.87 | +1.06 | +183 |
| [60,150) | 220 | 54 | 48 | 2.60 | 78.2 | 19953 | 0.16 | 0.0 | 0.19 | 2.55 | 13.17 | 7.99 | +1.87 | +412 |
| [150,400) | 217 | 49 | 23 | 5.57 | 89.4 | NULL | 18.75 | 0.0 | 3.85 | 220.88 | 2.05 | NULL | +3.81 | +828 |
| **>=400** | 567 | 122 | 90 | **12.71** | 84.1 | 13.38 | 13.55 | 5.41 | 20.50 | 9.72 | 9.77 | 47.43 | +4.57 | +2,593 |

**What the YEAR columns add (invisible in the PF alone):** the `>=400`
band is the ONLY one with a complete healthy year row in both tables —
7/7 years populated, all >= 2.10 (and all >= 5.41 with the vote).
Everything else is riddled with NULLs and zeros. The `<0` cell's 30.49
rests on almost no year coverage (only 2023 and 2025 populated with the
vote). And the `[25,60)` avoid is the most TRUSTWORTHY reading of the
pair — 1.60 / 1.77 with 65.5-65.7% win and years 0.87 / 1.03 / 1.71 /
1.78 / 3.50 — because it is bad EVERYWHERE rather than spectacular
somewhere.

### ⭐ WHERE TO CUT — cumulative sweep (vote + deep book)

| chg_1d cut | n | tkds | n_lose | PF | win% | avg% | 2022 | net pts |
|------------|--:|-----:|-------:|---:|-----:|-----:|-----:|--------:|
| >=100 | 903 | 194 | 138 | 7.72 | 84.7 | +4.07 | **0.82** | +3,674 |
| >=150 | 784 | 170 | 113 | 9.50 | 85.6 | +4.36 | **1.11** | +3,420 |
| **>=200** | 688 | 153 | 108 | 10.22 | 84.3 | +4.53 | **5.41** | +3,118 |
| >=250 | 633 | 142 | 104 | 10.06 | 83.6 | +4.57 | 5.41 | +2,893 |
| **>=300** | 592 | 129 | 91 | **13.41** | 84.6 | **+4.73** | 5.41 | +2,803 |
| >=400 | 567 | 122 | 90 | 12.71 | 84.1 | +4.57 | 5.41 | +2,593 |
| >=600 | 527 | 116 | 90 | 10.95 | 82.9 | +4.18 | 5.41 | +2,202 |
| >=1000 | 458 | 99 | 81 | 10.69 | 82.3 | +4.15 | 2.73 | +1,903 |

The complement — what a cut would discard:

| below cut | n | tkds | PF | win% | avg% | net pts |
|-----------|--:|-----:|---:|-----:|-----:|--------:|
| <100 | 460 | 113 | 3.17 | 75.4 | +2.14 | +985 |
| <200 | 675 | 156 | 3.33 | 78.8 | +2.28 | +1,540 |
| <300 | 771 | 176 | 3.40 | 79.2 | +2.41 | +1,856 |
| <400 | 796 | 184 | 3.65 | 79.8 | +2.59 | +2,066 |

**Answer to "should it be exactly >= 400%?" — NO.**

1. **>= 300 strictly DOMINATES >= 400**: more trips (592 vs 567),
   higher PF (13.41 vs 12.71), higher avg (+4.73 vs +4.57), identical
   2022. There is no argument for 400 over 300.
2. **The real boundary is 200, and it is a 2022 boundary.** Below it the
   bear year collapses (0.82 at >=100, 1.11 at >=150); at >=200 it snaps
   to 5.41 and holds through >=600. That is the honest floor.
3. **>=200 vs >=300 is the volume/quality trade**: 688 @ 10.22 (3,117
   slot pts) vs 592 @ 13.41 (2,800). Consistent with votes>=1 and
   dslo>=8, **>=200 is the volume-coherent pick**; >=300 is the peak.
4. ⚠ The discarded complement is NOT junk — below 200 is 675 trips @
   3.33 / +2.28%. Cutting concentrates the book; it does not remove
   losers.


## S43l — ⭐⭐ THE TWO S-TIER SETUPS (user decision: chg_1d >= 300%) (2026-08-03)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


User set the cut at **>= 300%**, reasoning: ">= 200 vs >= 300 is 30% of
the PF for 10% of the net — not worth the tradeoff. Maybe >= 300 acts
as a regime filter for bear markets." (Supported: 2022 reads 5.41 at
both, but >= 300 is where the PF curve peaks — 13.41 — and >= 400 is
strictly dominated.)

**⭐ S-TIER A — the first-halt aftermath**

    ht = 1  AND  secs_since_halt in [120, 1200)      (g60, $1+)

**⭐ S-TIER B — the deep flush on a huge mover**

    speed < -6%  AND  chg_1d >= 300%  AND  vote >= 1  (g60, $1+)

### The tradeable ladder (mc replay, not attribution)

| setup | mc=1 | mc=3 |
|-------|------|------|
| A first-halt | 35 @ **49.59** / +3.97% | 92 @ **63.10** / +4.09% |
| B deep-mover | 139 @ **9.12** / +3.86% | 322 @ **9.87** / +4.09% |
| **A OR B** | **170 @ 10.53** / +3.87% | **405 @ 11.70** / +4.08% |

**They are near-DISJOINT** — 217 halt-only trips, 580 deep-only, and
only **12 in both**. Two independent edges, so the union is close to
additive. Union = **160 ticker-days = 22.9 per year.**

### ⚠ What these numbers do NOT yet include

1. **COSTS ARE NOT MODELLED.** The engine banner says so on every run.
   At +4.08%/trip a 0.5-1.0% round trip on sub-$10 microcaps takes
   12-25% of the edge. It does not kill it — but PF 11.7 is a
   pre-cost number and will not survive intact.
2. **FILLS are assumed.** Entry = next present bar's vwap, exit =
   next bar's vwap after the 5m-high cross. On names moving 300%+ in a
   day with LULD halts firing, real fills are the open question. The
   slippage study has been on the queue since S42 and is now the single
   highest-value remaining item — it is what stands between these
   numbers and a live decision.
3. **PF is unstable at this size.** A at mc=3 is 92 trips with ~9
   losers. A single bad ticker-day moves it enormously — exactly what
   ENSV 2022-03-08 did to the deep book (S43j).
4. **Frequency: ~23 ticker-days/year combined**, ~58 trades/year at
   mc=3. Real, tradeable, but a patient book — roughly two setups a
   month, not a daily grind.

**The honest summary: two genuinely independent, all-weather, high-PF
setups on ~23 ticker-days a year, at +4%/trip BEFORE costs.** That is a
very good result. Whether it is a great one depends entirely on the
slippage study.


## S43m — do deep flushes get HALTED while we hold? (user) (2026-08-03)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


User: "it's possible that the deep flushes result in halts. It's worth
checking out." **Right about the mechanism — a -6%/min move is exactly
what trips LULD — and the answer contains a bonus.**

Measure = `hold_gap = (exit_sec - entry_sec) - bars_held`. On g60
(continuous tape) a >= 300s hole during the hold IS a halt. ⚠ Post-hoc
forensic only, NEVER a gate (S42p).

### The halt-during-hold RATE

| population | n | % paused (>=58s) | **% HALTED (>=300s)** | med gap |
|------------|--:|-----------------:|----------------------:|--------:|
| whole g60 book | 11,083 | 15.5 | **2.8** | 7s |
| moderate flush [-6,-2) | 9,492 | 14.6 | **2.3** | 7s |
| **DEEP flush < -6%** | 1,591 | 20.9 | **5.6** | 9s |
| **S-TIER B (deep + chg1d>=300 + vote)** | 592 | 18.8 | **2.7** | 6s |

⭐ **Deep flushes DO have 2.4x the halt-trap rate of moderate ones
(5.6% vs 2.3%) — but the >= 300% filter removes the elevation
entirely (2.7% = the book baseline).**

Plausible mechanism: LULD bands reference a 5-minute rolling average,
so on a stock already up 300%+ the reference price is CHASING the
price and a -6% minute rarely breaches the band. A quiet name that
drops 6% in a minute breaches immediately. **The >= 300% cut is
therefore not only a PF filter — it is a halt-risk filter**, which is
an independent reason to prefer it over >= 200%.

### And when a halt DOES land, it is favourable

**S-TIER B:**

| hold | n | tkds | % | losers | PF | win% | avg% | worst |
|------|--:|-----:|--:|-------:|---:|-----:|-----:|------:|
| HALTED (>=300s) | 16 | 4 | 2.7 | 2 | 264.81 | 87.5 | **+9.26** | **-0.3** |
| paused (58-300s) | 95 | 21 | 16.0 | 15 | 12.41 | 84.2 | +5.57 | -6.1 |
| clean hold | 481 | 106 | 81.3 | 74 | 12.88 | 84.6 | +4.42 | -8.2 |

**S-TIER A:**

| hold | n | % | PF | avg% | worst |
|------|--:|--:|---:|-----:|------:|
| HALTED (>=300s) | 8 | 3.5 | ∞ (0 losers) | +2.81 | **+0.1** |
| clean hold | 221 | 96.5 | 141.5 | +4.96 | -1.2 |

Same asymmetry as S42p's golden window: **in both S-tier setups the
halt that catches us is the LULD-UP halt of the snap-back, not the
down halt** — worst cases -0.3% and +0.1%. Compare the S42p pre-first-
halt population, where a halt-during-hold was catastrophic (PF 0.07,
-6.06%/trip, worst -38.5%).

**Verdict: the risk is real for deep flushes as a class and absent from
the S-tier cells.** (⚠ 16 and 8 trips respectively — the RATE is solid
on 592 trips, the favourability is not. Do not lean on it; just do not
fear it either.) This does NOT retire the slippage question — halts
are one failure mode, ordinary spread and impact on 300%-movers is
another and is still unmeasured.


## S43n — S-tier B: prior-halt composition, and does < -10% help? (2026-08-03)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


### 1. How many S-tier B trades were ALREADY halted before entry? (user)

**53.5% of them** — against 35.0% for the g60 book. The setup is
strongly enriched in halted names, which follows: a stock up 300%+
usually got there through halts.

| prior halts | n | tkds | % | losers | PF | win% | avg% | net pts |
|-------------|--:|-----:|--:|-------:|---:|-----:|-----:|--------:|
| ht=0 never halted | 275 | 59 | 46.5 | 45 | 11.08 | 83.6 | +4.64 | +1,276 |
| ht=1 | 94 | 26 | 15.9 | 9 | **17.30** | 90.4 | +5.07 | +476 |
| ht=2 | 73 | 16 | 12.3 | 8 | **43.62** | 89.0 | +5.51 | +402 |
| **ht=3-5** | 97 | 19 | 16.4 | 24 | **7.12** | 75.3 | +3.00 | +291 |
| ht>=6 | 53 | 11 | 9.0 | 5 | 28.49 | 90.6 | +6.74 | +357 |

g60 baseline: ht=0 65.0% @ 3.44 · ht=1 17.2% @ 5.74 · ht=2 5.7% @ 5.86
· ht=3-5 7.7% @ 4.11 · ht>=6 4.3% @ 5.21.

**A prior halt is a POSITIVE, not a risk.** Never-halted is already
11.08, but ht=1/ht=2 run 17.30 / 43.62 at ~90% win. The soft tier is
**ht=3-5 (7.12, 75.3% win, 24 losers of 97)** — the serial-breaker
signature again (S42n). ht>=6 at 28.49 cuts against it but is 53 trips
on 11 tkds.

How recent was that halt (halted names only):

| last halt | n | tkds | % | PF | avg% |
|-----------|--:|-----:|--:|---:|-----:|
| <20m | 14 | 4 | 4.4 | ∞ (0 losers) | +6.75 |
| **[20,80m) <- the halt-band VOICE** | 203 | 44 | **64.0** | 12.46 | +3.94 |
| [80,160m) | 44 | 14 | 13.9 | 16.17 | +6.00 |
| >=160m | 56 | 13 | 17.7 | **31.54** | +6.59 |

Two-thirds sit in the halt-band voice window, so the vote is partly
HOW they got admitted. The OLDEST halts are the best (31.54) — by then
the halt is pure context: the name proved it can move violently, the
crowd is watching, and the LULD elevator is long over.

⭐ **The two S-tier setups are the same animal from different angles** —
A is timing off a fresh first halt, B is a deep flush on a huge mover
that has usually halted at some point. Their 12-trip overlap
understates how related they are.

### 2. Would speed < -10% improve it? **NO.**

Cumulative sweep inside S-tier B (chg_1d >= 300, vote >= 1):

| speed cut | n | tkds | losers | PF | win% | avg% | med% | yrs | 2022 | net pts |
|-----------|--:|-----:|-------:|---:|-----:|-----:|-----:|----:|-----:|--------:|
| **< -2 (spec base, NO deep cut)** | **3,408** | **541** | 588 | **8.17** | 82.7 | +2.87 | +2.84 | 7 | **10.54** | **+9,797** |
| < -4 | 1,529 | 302 | 235 | 11.27 | 84.6 | +3.76 | +3.69 | 7 | **15.57** | **+5,750** |
| **< -6 (current)** | 592 | 129 | 91 | **13.41** | 84.6 | +4.73 | +4.60 | 7 | 5.41 | +2,803 |
| < -8 | 233 | 57 | 52 | 12.87 | 77.7 | +5.84 | +5.01 | 7 | 1.59 | +1,361 |
| < -10 | 78 | 20 | 19 | 11.40 | 75.6 | +6.76 | +5.79 | 7 | **0.00** | +527 |
| < -12 | 28 | 9 | 3 | 169.79 | 89.3 | +8.58 | +6.01 | 4 | NULL | +240 |
| < -15 | 5 | 2 | 0 | ∞ | 100.0 | +14.62 | +19.55 | 2 | NULL | +73 |

**PF PEAKS at the current -6 and falls as you tighten** (13.41 -> 12.87
-> 11.40). Only avg% rises — that is just bigger moves on fewer trades,
not a better edge. And **2022 collapses: 5.41 -> 1.59 -> 0.00.** At -10
the cell is 78 trips / 20 tkds with a zero bear year.

The band view shows exactly why tightening loses:

| speed band | n | tkds | losers | PF | avg% | net pts |
|------------|--:|-----:|-------:|---:|-----:|--------:|
| [-6,-2) | 2,816 | 495 | 497 | 7.13 | +2.48 | +6,995 |
| **[-8,-6)** | 359 | 102 | 39 | **13.97** | +4.02 | +1,442 |
| **[-10,-8)** | 155 | 48 | 33 | **14.03** | +5.38 | +833 |
| [-15,-10) | 73 | 19 | 19 | 9.95 | +6.22 | +454 |
| < -15 | 5 | 2 | 0 | ∞ | +14.62 | +73 |

The edge sits in **[-10,-6)** — 514 trips at ~14 — and **[-8,-6) is the
LARGEST good band (359 trips)**. A -8 or -10 cut throws away the
biggest piece of the very thing it is trying to isolate. Below -10 the
PF actually falls (9.95).

⭐ **The other direction is where the money is.** With the spec baseline
now in the table, the full picture is a clean monotone trade of PF
against everything else:

| cut | trips | net pts | PF | 2022 |
|-----|------:|--------:|---:|-----:|
| < -2 | 3,408 | **9,797** | 8.17 | 10.54 |
| < -4 | 1,529 | 5,750 | 11.27 | **15.57** |
| < -6 | 592 | 2,803 | **13.41** | 5.41 |

**`< -2` — i.e. NO deep-flush cut at all — earns 3.5x the net points of
`< -6` at PF 8.17 and a 2022 of 10.54.** `< -4` sits between with the
BEST bear year of the three (15.57). The deep cut is buying PF with
net, and steeply: each step from -2 to -6 roughly halves the book.

On the volume-coherent logic used all session (votes>=1, dslo>=8,
chg_1d>=300 over >=400), **-4 has the strongest case** — 2.6x the trips
of -6, 2x the net, and the best 2022 of any cut. It is only "less
deep" by name. ⚠ Note also that `chg_1d >= 300` is doing most of the
work here: `< -2` with it still yields 8.17 on 3,408 trips. Left as a
user decision; the current -6 stands.


## S43o — ⭐⭐ S-TIER B: the chg_1d cut becomes a UNION of both tails (2026-08-04)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


User: "Since stocks down more than 0% on the day have a great edge, let's
make the chg_1d filter `>= 300% || < 0`." (Sent as `&&` first, which is
the empty set; corrected to `||` in the next message.)

**Adopted. It is a strictly dominating change — more trips, higher PF,
more net, AND it lifts the worst year.** The discarded middle is the
mushy `[0, 300)` band.

### ⚠ FIRST — a correction to S43k

S43k annotated the `<0` cell as resting on "almost no year coverage
(only 2023 and 2025 populated with the vote)". **That reading was
wrong.** The NULL PFs in that table were **zero-loser cells**, not
empty ones. Every one of the 7 years is populated:

| yr | n | losers | PF | net pts |
|----|--:|-------:|----|--------:|
| 2020 | 7 | 0 | **inf** | +47 |
| 2021 | 15 | 0 | **inf** | +66 |
| 2022 | 5 | 0 | **inf** | +31 |
| 2023 | 14 | 7 | 1.30 | +3 |
| 2024 | 16 | 0 | **inf** | +103 |
| 2025 | 9 | 1 | 22.37 | +60 |
| 2026 | 22 | 0 | **inf** | +76 |

**5 of 7 years are LOSS-FREE and all 7 are net positive.** That is the
opposite of a thin cell. ⭐ HOUSE RULE from this: **always render an
undefined PF as `inf`, never NULL** — `NULL` is visually identical to
"no data" and it cost us a good filter for a day.

### The cut sweep (g60, deep speed < -6%, vote >= 1, mc=0)

| cut | n | tkds | losers | PF | win% | avg% | med% | net pts |
|-----|--:|-----:|-------:|---:|-----:|-----:|-----:|--------:|
| >=300 (was) | 592 | 129 | 91 | 13.41 | 84.6 | +4.73 | +4.60 | +2,803 |
| **>=300 OR <0 (NEW)** | **680** | **154** | 99 | **14.35** | **85.4** | +4.69 | +4.53 | **+3,188** |
| <0 alone | 88 | 25 | 8 | 30.49 | 90.9 | +4.39 | +4.30 | +386 |
| >=200 OR <0 | 776 | 178 | 116 | 10.98 | 85.1 | +4.52 | +4.39 | +3,504 |
| no chg_1d cut | 1,363 | 305 | 251 | 5.66 | 81.6 | +3.42 | +3.38 | +4,658 |

+15% trips, +19% ticker-days, +14% net, and PF *rises* 13.41 -> 14.35.
There is no tradeoff to argue about — unlike >=200 vs >=300, which cost
PF for net.

### The tradeable ladder (mc replay)

| setup | mc=1 | mc=3 | tkds/yr (mc=3) |
|-------|------|------|---------------:|
| B old (>=300) | 139 @ 9.12 / +3.86% | 322 @ 9.87 / +4.09% | 18.3 |
| **B new (>=300 OR <0)** | **163 @ 9.98** / +3.96% | **384 @ 10.93** / +4.17% | **21.9** |
| A OR B old | 170 @ 10.53 / +3.87% | 405 @ 11.70 / +4.08% | 22.7 |
| **A OR B new** | **193 @ 11.15** / +3.93% | **464 @ 12.47** / +4.13% | **26.1** |

A and B stay near-disjoint: 15 shared trips of 894 (was 12 of 809).

### ⭐ It lifts the FLOOR — per-year, A OR B at mc=3

| yr | old n / PF | new n / PF |
|----|-----------|-----------|
| 2020 | 62 / 11.05 | 66 / **12.18** |
| 2021 | 58 / 8.59 | 67 / **9.95** |
| **2022** | 14 / **3.64** | 17 / **4.90** |
| 2023 | 33 / 10.22 | 39 / 6.11 ⚠ |
| 2024 | 91 / 9.58 | 103 / **11.41** |
| 2025 | 98 / 14.06 | 107 / **14.81** |
| 2026 | 49 / 47.45 | 65 / **56.03** |

**6 of 7 years improve and the WORST YEAR RISES 3.64 -> 4.90.** Note
against S42z's FLOOR LAW ("to move the floor use regime detection or
sizing, not the vote bar") — this is not the vote bar. It is a
*population* change, which is the category the floor law said would be
needed.

⚠ 2023 is the one degradation (10.22 -> 6.11) and it is **two ticker-
days**: TOP 2023-05-05 (3 trips, 3 losers, -7.5 pts) and SOUN
2023-02-23 (11 trips, 4 losers, but +10.6 pts net). One bad name.

### What the two tails actually ARE (they are not the same animal)

| band | n | med chg_1d | med chg_3d | n with chg_3d>0 | med px |
|------|--:|-----------:|-----------:|----------------:|-------:|
| >=300% | 592 | **+2,978%** | +3,796% | **592 / 592** | $4.00 |
| <0 | 88 | **-12%** | +22% | 50 / 88 | $5.95 |

The `>=300` tail is the parabolic runner mid-flight. The `<0` tail is a
**higher-priced, calmer name that is RED on the session** — 57% of them
still up over three days (a giveback), 43% down (a slide).

### Does the "into support" thesis refine it further? NO

Testing the user's own S43i thesis on the down-band — buy the giveback,
avoid the slide:

| 3d context | n | tkds | losers | PF | win% | avg% | worst | yrs |
|------------|--:|-----:|-------:|----|-----:|-----:|------:|----:|
| 3d >= +60% (big giveback) | 24 | 9 | 1 | 33.98 | 95.8 | +3.85 | -2.8 | 3 |
| 3d [0,60) (giveback) | 26 | 9 | 0 | **inf** | 100.0 | +5.84 | **+2.9** | 4 |
| 3d < 0 (SLIDING OFF) | 38 | 8 | 7 | 14.77 | 81.6 | +3.73 | -3.0 | 6 |

**All three work.** The slide group carries all 7 losers but still reads
14.77 on 38 trips — it is noisier, not bad. Adding a `chg_3d >= 0`
condition would cut 43% of the band to remove a sub-population that is
itself strongly profitable. **Rejected — keep the cut simple.** (The
2023 TOP case has chg_3d = -73.6%, so it fits the slide story, but one
ticker-day is not a class.)

### ⭐ THE UPDATED S-TIER B

    speed < -6%  AND  (chg_1d >= 300% OR chg_1d < 0)  AND  vote >= 1
                                                            (g60, $1+)

mc=3: **384 @ 10.93 / +4.17%/trip / 21.9 tkds per year.**
Union with S-TIER A: **464 @ 12.47 / +4.13%/trip / 26.1 tkds per year.**

⚠ Costs still unmodelled — see S43l §"What these numbers do NOT include".
The slippage study remains the gating item, and it now has to cover a
second, structurally different population: the `<0` band is a higher-
priced ($5.95 median vs $4.00) and less frenzied tape than the
parabolic runners, so its fills may well be BETTER, but that is a guess
until measured.


## S43p — ⭐⭐ S-TIER B goes VOLUME: the deep-flush cut is retired to a sizing lever (2026-08-04)

> 🛑 **INVALID — chg_1d lookahead; see the banner at S43i and the verdict in S43v.**
> The S-tier B construction in this section does not survive the fix.


User: "I think I'd prefer volume, and we'll leave the deep flush as a
sizing lever in the future."

**Confirmation of scope for S43o:** every S43o number was measured WITH
`speed < -6%` active. The spec's own `speed < -2%/1m` gate is always
underneath — "no deep cut" means falling back to it, never to no speed
condition at all.

### The speed sweep, with the S43o union (g60, vote >= 1, chg_1d >= 300 OR < 0)

| speed cut | n | tkds | losers | PF | win% | avg% | med% | 2022 | net pts |
|-----------|--:|-----:|-------:|----|-----:|-----:|-----:|------|--------:|
| **< -2 (spec base)** | **3,864** | **640** | 672 | **8.04** | 82.6 | +2.84 | +2.83 | 11.07 | **+10,955** |
| < -3 | 2,874 | 525 | 516 | 8.59 | 82.0 | +3.09 | +3.08 | 8.24 | +8,872 |
| < -4 | 1,782 | 353 | 267 | 11.23 | 85.0 | +3.68 | +3.60 | **15.33** | +6,563 |
| < -5 | 1,099 | 233 | 160 | 13.42 | 85.4 | +4.29 | +4.26 | 11.20 | +4,713 |
| < -6 (was) | 680 | 154 | 99 | 14.35 | 85.4 | +4.69 | +4.53 | 7.53 | +3,188 |
| < -8 | 258 | 66 | 55 | 13.40 | 78.7 | +5.78 | +5.00 | 1.59 | +1,492 |

Dropping to the spec base = **5.7x the trips, 4.2x the ticker-days,
3.4x the net** for PF 8.04 vs 14.35. ⭐ **2022 is healthy at every cut
from -2 to -5** (7.5-15.3) — the bear-year knife only appears at -8
(1.59), so the volume choice costs nothing on the floor. The union
also removes the -6 dip (7.53) that S43o inherited: -4 and -5 both
read better in 2022 than -6 does.

### The tradeable ladder (mc replay, g60 $1+)

| construction | mc=1 | mc=3 | tkds/yr | net pts @ mc=3 |
|--------------|------|------|--------:|---------------:|
| B deep (< -6) | 163 @ 9.98 / +3.96% | 384 @ 10.93 / +4.17% | 21.9 | — |
| **B volume (spec -2)** | **688 @ 5.35** / +2.25% | **1,727 @ 5.90** / +2.38% | — | — |
| A OR B deep | 193 @ 11.15 / +3.93% | 464 @ 12.47 / +4.13% | 26.1 | +1,917 |
| **A OR B volume** | **711 @ 5.61** / +2.32% | **1,791 @ 6.21** / +2.45% | **93.0** | **+4,396** |

**4.5x the ticker-days and 2.3x the net for roughly half the PF.**

### Per-year, A OR B volume at mc=3

| yr | n | PF | win% | avg% |
|----|--:|----|-----:|-----:|
| 2020 | 317 | 9.82 | 85.8 | +3.04 |
| **2021** | 405 | **3.81** | 74.3 | +1.54 |
| 2022 | 135 | 7.37 | 83.0 | +2.27 |
| 2023 | 150 | 4.51 | 80.7 | +2.07 |
| 2024 | 318 | 4.45 | 77.0 | +2.37 |
| 2025 | 347 | 9.35 | 82.4 | +2.82 |
| 2026 | 119 | 12.63 | 87.4 | +3.85 |
| **total** | **1,791** | **6.21** | 80.5 | +2.45 |

**Every year positive, worst = 3.81 (2021), 2022 = 7.37.** Compare the
main vote book's worst year of ~2.89 — this is still a materially
higher floor, on 93 ticker-days a year instead of 26.

### ⭐ THE BOOK AS IT NOW STANDS

    B (volume) :  g60  AND  vote >= 1  AND  (chg_1d >= 300% OR chg_1d < 0)
    A (S-tier) :  g60  AND  ht = 1  AND  ssh in [2, 20m)
                                                        ($1+, SPEC v2.6)

`speed < -6%` is **retired from the definition and becomes a SIZING
lever** — S43h already showed why it cannot be a one-sided gate (its
meaning inverts with vol state), and the sweep above prices the lever
exactly: each step from -2 to -6 roughly halves the book while adding
~0.5%/trip. That is a size ladder, not a filter.

⚠ This is a different animal from the S43l "two S-tier setups": 93
ticker-days a year is a real working book, not two setups a month. It
therefore inherits the MAIN book's cost exposure — at +2.45%/trip a
0.5-1.0% round trip is **20-40% of the edge**, not 12-25%. The
slippage study matters MORE under this choice, not less.


## S43q — ⭐⭐ THE SLIPPAGE STUDY: costs MEASURED, not assumed (2026-08-04)

> ⚠ **PARTIALLY SUPERSEDED (S43v).** The per-trip TAPE measurements here
> (spread, `step`/`roll`, delay by work window, sigma, day volume) are
> properties of the tape and STAND. But the book-level aggregates — the
> cost-adjusted PF ladder and the % -of-edge figures — were computed over
> the chg_1d-contaminated book and must be re-derived on the corrected
> book (`g60 AND vote>=1`, 3,330 @ 3.90 at mc=3). **Re-run queued.**


User: "the easiest thing we could do is just calculate the 1m dollar volume
quartiles for these trades... fill simulations are overkill. At 2.8%
average gain I am not that concerned about trading fees anymore."

**Agreed on fees. But the binding cost at these prices is the TICK, not
the fee** — a $0.01 spread on a $1.50 stock is 0.67% one way. So the study
is: capacity (the user's ask) + the spread actually paid.

Method note for the spread estimator: `docs/rolls_estimator.md`.

### 1. CAPACITY — entry-minute dollar volume (mc=3 book, 1,791 trips)

| | min | p10 | Q1 | **median** | Q3 | p90 | max |
|---|---:|---:|---:|---:|---:|---:|---:|
| $/min | $135k | $575k | $850k | **$1.49M** | $2.97M | $6.16M | $134M |

Median **1,394 trades** in the entry minute; median hold 335s (5.6m). The
$135k floor is the entry gate's own `dv60 >= $100k`. Position size at a
given participation rate:

| participation | p10 | Q1 | median | Q3 |
|---|---:|---:|---:|---:|
| 1% | $5.8k | $8.5k | $14.9k | $29.7k |
| 2% | $11.5k | $17.0k | $29.7k | $59.3k |
| 5% | $28.8k | $42.5k | $74.3k | $148k |
| 10% | $57.5k | $85k | $148k | $297k |

### 2. ⭐ THE EDGE DOES NOT LIVE IN THE ILLIQUID TRADES

| dv60 quintile | range | med px | losers | PF | avg% | net pts |
|---|---|---:|---:|---:|---:|---:|
| 1 | $135-744k | $1.98 | 76 | 6.69 | +2.53 | 909 |
| 2 | $745k-1.2M | $2.95 | 67 | 6.32 | +2.43 | 870 |
| 3 | $1.2-1.8M | $4.09 | 67 | 5.45 | +2.19 | 783 |
| 4 | $1.8-3.8M | $5.54 | 75 | 5.46 | +2.35 | 840 |
| 5 | $3.8M+ | $8.68 | 65 | **7.38** | +2.78 | 994 |

**Flat, and the MOST liquid quintile is the best.** Net points are nearly
even across all five (909/870/783/840/994). This is the opposite of the
usual microcap failure mode and it means **a liquidity floor is close to
free**: `dv60 >= $2M` leaves 1,621 trips @ PF 10.56 at a $6.64 median price.

### 3. THE SPREAD — measured on 506 days of raw prints

No quote data exists (trades only: price/size/`sip_timestamp`/`trf_id`), so
the spread is inferred from trade prices. Roll's estimator + the observed
price grid, lit prints only, entry/exit +/- 30s, 3,582 windows. ⚠ Parity:
1s vwaps reconstructed from trades match `intraday_1s_slim` to 4 decimals.

| bucket | n | med px | `step` $ | `roll` $ | step % | roll % | **assumed %** | roll in ticks | `p_rev` |
|---|--:|---:|---:|---:|---:|---:|---:|---:|---:|
| $1.00-1.50 | 226 | 1.24 | 0.005 | 0.0019 | 0.482 | 0.153 | 0.809 | 0.19 | 0.875 |
| $1.50-2 | 195 | 1.72 | 0.010 | 0.0021 | 0.504 | 0.120 | 0.581 | 0.21 | 0.857 |
| $2-3 | 284 | 2.45 | 0.010 | 0.0026 | 0.372 | 0.115 | 0.408 | 0.26 | 0.841 |
| $3-5 | 349 | 3.82 | 0.010 | 0.0037 | 0.247 | 0.096 | 0.262 | 0.37 | 0.788 |
| $5-10 | 371 | 6.74 | 0.010 | 0.0062 | 0.153 | 0.089 | 0.148 | 0.62 | 0.724 |
| $10+ | 366 | 17.32 | 0.020 | 0.0222 | 0.090 | 0.118 | 0.058 | 2.22 | 0.606 |

**Findings:**

1. **`step` = exactly 1 tick ($0.01) across $1.50-$10**, 2 ticks at $10+,
   and **half a tick ($0.005) at $1.00-1.50** — sub-penny prints, i.e.
   retail price improvement. The 1-cent assumption was the right order of
   magnitude and slightly CONSERVATIVE at the cheap end.
2. **Roll is biased LOW here, as predicted** — 0.19-0.62 ticks on sub-$10
   names is implausible. `p_flat` gives the mechanism: **83% of consecutive
   trades print at the same price** = the serial correlation in trade
   direction that breaks Roll's coin-flip assumption.
3. ⚠ **`p_rev` correction**: under pure bounce, consecutive non-zero
   changes alternate DETERMINISTICALLY, so the null is **1.0, not 0.5**
   (an earlier note of mine had this backwards). Measured 0.606-0.875 =
   strong bounce, **strongest at the cheap tick-constrained end**.
4. ⇒ Read the pair as a **bracket**: `roll <= true spread <= step`.

### 4. ⭐⭐ THE COST-ADJUSTED BOOK (mc=3, 1,791 trips)

| cost model | cost % of px | PF | win% | avg% | net pts | **% of edge lost** |
|---|---:|---:|---:|---:|---:|---:|
| gross | 0 | 6.21 | 80.5 | +2.45 | 4,396 | 0 |
| ROLL, half-spread each way (optimistic) | 0.122 | 5.72 | 79.6 | +2.33 | 4,177 | **5%** |
| **STEP, half-spread each way (central)** | 0.276 | **5.17** | 77.9 | **+2.18** | 3,902 | **11%** |
| STEP, FULL spread each way (pessimistic) | 0.552 | 4.25 | 75.6 | +1.90 | 3,408 | **22%** |
| (my earlier 1-cent assumption) | 0.326 | 4.98 | 77.4 | +2.13 | 3,813 | 13% |

⭐ **VERDICT: costs take 5-22% of the edge, most likely ~11%, and the book
survives the pessimistic case at PF 4.25 / +1.90% per trip.**

⚠ **This CORRECTS the S43p warning.** S43p said "at +2.45%/trip a 0.5-1.0%
round trip is 20-40% of the edge". The measured round trip is **0.28-0.55%**,
not 0.5-1.0% — so the true figure is roughly half what was feared, and the
volume choice in S43p is vindicated rather than endangered.

### 5. What this study still does NOT cover

1. **Impact is not modelled** — only the spread. The half/full-spread
   models price *crossing*, not the price move caused by our own size. At
   1-2% participation impact should be small; at 10% it is not, and this
   study cannot say how much.
2. **No quote data** ⇒ the spread is inferred, never observed. The bracket
   is honest but it is a bracket.
3. **Fills are still assumed at the next bar's vwap.** We now know what
   crossing the spread costs relative to that fill; we have not shown the
   vwap fill itself is attainable on a halted, 300%-mover tape.
4. **Entry is BUYING A COLLAPSE and exit is SELLING A BOUNCE** — both trade
   WITH available liquidity, which argues the real cost sits nearer the
   optimistic end. Unquantified, but it is the direction of the error.


## S43r — ⭐⭐ MARKET IMPACT: the size ladder, and why the EXIT pays us back (2026-08-04)

> ⚠ **PARTIALLY SUPERSEDED (S43v).** The per-trip TAPE measurements here
> (spread, `step`/`roll`, delay by work window, sigma, day volume) are
> properties of the tape and STAND. But the book-level aggregates — the
> cost-adjusted PF ladder and the % -of-edge figures — were computed over
> the chg_1d-contaminated book and must be re-derived on the corrected
> book (`g60 AND vote>=1`, 3,330 @ 3.90 at mc=3). **Re-run queued.**


User: "Let's model the market impact next." Two components, only one of
which is the textbook one.

### Component 1 — EXECUTION DELAY (measured, no model)

The backtest fills at the next 1s bar's vwap = instantaneous. A real order
of size Q at participation p takes `T = Q / (p * $rate)` seconds, over which
we pay the tape's VWAP. **For a system that BUYS a collapse this is adverse
by construction — the price is running away from us, and that move is the
very thing we are buying.** Measured from raw prints over 506 days:

| work window | med $vol in window | size @ 10% | **entry cost** | **exit cost** | net delay |
|---|---:|---:|---:|---:|---:|
| T =   5s | $68k | $6.8k | -0.017 | -0.002 | **-0.019** |
| T =  15s | $189k | $18.9k | +0.026 | -0.024 | **+0.002** |
| T =  30s | $352k | $35.2k | +0.061 | -0.042 | +0.019 |
| T =  60s | $588k | $58.8k | +0.129 | -0.090 | +0.039 |
| T = 120s | $1.02M | $102k | +0.305 | -0.127 | +0.177 |
| T = 300s | $2.02M | $202k | +0.589 | -0.140 | +0.449 |

⭐⭐ **THE EXIT RUNS THE OPPOSITE WAY AND PARTLY PAYS FOR THE ENTRY.** Entry
cost grows monotonically with T (0.026 -> 0.589) exactly as expected. But
**every exit number is NEGATIVE — working the sell order SLOWLY gets a
BETTER price**, improving from -0.024 to -0.140.

This is structural, not noise. The exit trigger is a **5-minute-high
cross**, and momentum persists past it — so the tape keeps rising while we
sell. The system is long a collapse (delay hurts) and short a continuation
(delay helps). The offset is real and it is worth ~25-30% of the entry cost
at every horizon.

⇒ **Net delay is essentially FREE below ~$35k and still only 0.04% at
$59k.** It only bites past ~$100k.

### Component 2 — OWN PRICE PRESSURE (square-root law)

`dP/P = Y * sigma_day * sqrt(Q/V)`; method note in `docs/rolls_estimator.md`
§ companion. Inputs measured per ticker-day from 1s bars: **median
sigma_day = 29.0%** (1m returns x sqrt(390)) and **median day dollar volume
= $330.6M**. These are enormous-volume names, so participation is trivial:

| position | % of DAY's volume | Y=0.25 | **Y=0.50** | Y=1.00 |
|---|---:|---:|---:|---:|
| $10k | 0.003% | 0.043 | **0.085** | 0.171 |
| $25k | 0.008% | 0.068 | **0.135** | 0.270 |
| $50k | 0.015% | 0.096 | **0.191** | 0.382 |
| $100k | 0.030% | 0.135 | **0.270** | 0.540 |
| $250k | 0.076% | 0.214 | **0.427** | 0.855 |

⚠ The numbers are driven by **sigma, not by size** — impact is linear in
sigma and these names run 29% daily vol. At 0.03% of the day's volume a
$100k order is a rounding error in participation terms.

### ⭐⭐ THE FULL COST LADDER (mc=3, 1,791 trips, Y=0.5, 10% participation)

Per trip: measured spread (`step`, half each way) + measured delay at the
smallest window that supports the size + square-root impact.

| position | spread | delay | impact | **total** | % oversize | **PF net** | **avg% net** | edge lost |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| gross | — | — | — | — | — | 6.21 | +2.45 | — |
| **$10k** | 0.276 | **-0.038** | 0.093 | **0.331** | 0.2% | **5.03** | **+2.12** | **13%** |
| **$25k** | 0.276 | -0.008 | 0.147 | 0.415 | 2.1% | 4.72 | +2.04 | **17%** |
| **$50k** | 0.276 | 0.147 | 0.208 | 0.631 | 5.7% | 4.23 | +1.82 | **26%** |
| $100k | 0.276 | 0.291 | 0.295 | 0.862 | 25.1% | 3.64 | +1.59 | 35% |
| $250k | 0.276 | 0.467 | 0.466 | 1.209 | 59.1% | 2.83 | +1.25 | 49% |

("% oversize" = trips where even a 300s window at 10% participation cannot
absorb the size — there the cost is understated, so the big rows are
optimistic.)

### ⭐ VERDICT

1. **The spread is the FIXED cost and it dominates at small size** (0.276%
   of a 0.331% total at $10k). Delay and impact are the size-dependent
   parts.
2. **$25k-$50k is the sweet spot** — 17-26% of the edge, PF still 4.2-4.7,
   +1.8-2.0% per trip. At mc=3 that is ~256 trades/year on ~$150k of
   deployed capital (3 concurrent slots).
3. **$100k+ degrades fast** and a quarter of trips can no longer be filled
   at 10% participation in 5 minutes. **$250k is where half the edge is
   gone** and 59% of trips are oversize — that is the practical ceiling of
   this book as constructed.
4. ⚠ **The square-root term is the weakest link.** It is calibrated on
   metaorders worked over HOURS, and uses the DAY's volume as denominator
   while we execute in seconds — which understates our impact. Pulling the
   other way, we trade a capitulation where volume is 10-100x normal.
   Y=1.00 doubles the impact column and is the conservative read.
5. ⭐ **The direction of the remaining error is favourable**: we buy a
   collapse and sell into a continuation, i.e. we trade WITH available
   liquidity on both legs. The measured exit credit is the first hard
   evidence for what was a hand-wave in S43q.


## S43s — z_20m as a 7th voice: REJECTED as a voice, ACCEPTED as a sizing lever (2026-08-04)

User: "Currently we're only trimming >= -1.5, but maybe it would be worth
adding it as a voice when it is < -2.5?"

**Reconstruction (exact):** z is not a recorded column but its inputs are —
`z20 = (ln(signal_vwap) - dlv_1200/vol_1200) / sqrt(dlv2_1200/vol_1200 -
(dlv_1200/vol_1200)^2)`. Parity: over 29,764 $1+ trips, **max z = -1.5000,
0 violations, 0 nulls** — it is the engine's gate to the digit.

### 1. The fine table (g60, year columns, per house rule)

| z band | n | tkds | PF | win% | avg% | 20 | 21 | 22 | 23 | 24 | 25 | 26 |
|---|--:|--:|---|--:|--:|--|--|--|--|--|--|--|
| [-1.75,-1.5) | 903 | 310 | 4.17 | 76.2 | +1.95 | 2.6 | 7.9 | 7.5 | 5.4 | 2.6 | 2.9 | 19.7 |
| [-2.0,-1.75) | 2,700 | 743 | 3.85 | 79.7 | +1.88 | 9.8 | 4.3 | 19.0 | 2.8 | 2.7 | 2.3 | 7.5 |
| [-2.25,-2.0) | 3,489 | 894 | 4.24 | 79.7 | +2.06 | 9.6 | 3.9 | 5.3 | 2.2 | 2.5 | 4.4 | 7.6 |
| **[-2.5,-2.25)** | 2,304 | 668 | **3.23** | 77.8 | +1.85 | 13.9 | 3.5 | 4.0 | 3.4 | 3.7 | 2.1 | 1.7 |
| [-2.75,-2.5) | 1,216 | 355 | 4.79 | 80.1 | +2.59 | 11.5 | 6.3 | 2.5 | 6.3 | 7.1 | 3.5 | 3.0 |
| [-3.0,-2.75) | 374 | 133 | 4.57 | 82.9 | +2.88 | 2.3 | 62.4 | 0.6 | 2.6 | 14.2 | 4.1 | 9.9 |
| [-3.5,-3.0) | 89 | 41 | 12.31 | 77.5 | +4.66 | inf | 87.1 | 1.0 | inf | 23.1 | 3.3 | inf |
| < -3.5 | 8 | 4 | 3.98 | 75.0 | +2.44 | inf | inf | 0.0 | inf | inf | inf | inf |

⚠ **Non-monotone, and the proposed -2.5 threshold sits immediately after
the WORST band in the table** ([-2.5,-2.25) = 3.23). Cumulatively:

| cut | n | tkds | PF | avg% | **2022** |
|---|--:|--:|---|--:|---|
| all g60 | 11,083 | 1,718 | 4.00 | +2.07 | **4.03** |
| < -2.25 | 3,991 | 836 | 3.89 | +2.24 | **1.83** |
| < -2.5 | 1,687 | 411 | 4.96 | +2.76 | **1.08** |
| < -2.75 | 471 | 151 | 5.38 | +3.21 | **0.57** |
| < -3.0 | 97 | 41 | 11.05 | +4.48 | **0.59** |

⭐ **The deep-z tail is BEAR POISON** — 2022 falls monotonically 4.03 ->
1.83 -> 1.08 -> 0.57 as the cut tightens. Same signature as `speed < -10`
(S43n), where PF rose while 2022 went to zero.

### 2. ⭐ THE SOLO TEST (S43c rule: a >=1 bar makes the vote a UNION, so a
voice is worth exactly what it admits ALONE)

g60 + the chg_1d filter, 6 voices vs `z < -2.5`:

| group | n | tkds | losers | PF | win% | avg% | net pts |
|---|--:|--:|--:|---|--:|--:|--:|
| both fire | 682 | 161 | 99 | 14.39 | 85.5 | +3.98 | 2,713 |
| existing 6 only (the book) | 3,182 | 584 | 573 | 7.09 | 82.0 | +2.59 | 8,241 |
| **⭐ z SOLO (what it ADDS)** | **220** | 65 | 57 | **2.41** | 74.1 | **+1.54** | 338 |
| neither (correctly excluded) | 1,559 | 332 | 383 | **2.48** | 75.4 | +1.22 | 1,894 |

**The trips z admits ALONE (2.41) are statistically indistinguishable from
the trips the vote correctly REJECTS (2.48).** And 682 of its 902 firings
(76%) already fire another voice. This is the exact redundancy trap that
retired speed and pah in S43d.

### 3. The book confirms it (mc=3)

| roster | n | PF | avg% |
|---|--:|---|--:|
| **6 voices (current)** | **1,727** | **5.903** | **+2.38** |
| + z < -2.5 | 1,836 | 5.513 | +2.32 |
| + z < -2.75 | 1,748 | 5.829 | +2.37 |

Both dilute. `-2.75` is near-neutral only because it barely fires.

### 4. ⭐ BUT IT IS A REAL SIZING LEVER — inside the book

| group | n | PF | avg% | 20 | 21 | **22** | 23 | 24 | 25 | 26 |
|---|--:|---|--:|--|--|--|--|--|--|--|
| z < -2.5 | 682 | **14.39** | **+3.98** | 47.3 | 6.7 | **3.9** | 5.8 | 16.6 | 54.9 | 47.1 |
| z >= -2.5 | 3,182 | 7.09 | +2.59 | 11.6 | 5.9 | **16.5** | 4.7 | 4.4 | 7.8 | 14.0 |

**2x the PF and +1.4pp per trip, better in 6 of 7 years — but it INVERTS in
2022** (3.9 vs 16.5). That is the CONTRAST GRAMMAR signature (S42b) for the
fifth time: speed, quiet-vol, vol-derivative, vote count, and now z. A
feature whose meaning flips with regime cannot be a one-sided voice, but it
can size.

### VERDICT

- ❌ **REJECTED as a voice** — its solo trips are junk, it is 76%
  redundant, it dilutes the book at mc=3, and its tail is bear poison.
- ✅ **ACCEPTED as a SIZING candidate**, joining `speed` in that role, with
  the 2022 inversion documented as the constraint.


## S43t — ⭐⭐ SETTLED: the rank-1/rank-2 Renyi measures add NOTHING over rank-0 (2026-08-04)

User: "I am a bit confused whether stacking the rank 1 and 2 Renyi measures
on top of rank 0 (the gap) gives a benefit. At first we concluded they
don't, and some tests showed that they did at the deep end. Let's settle
this once and for all."

**The user's framing is the right one and names the whole question:**

| Renyi order | measure | what it counts |
|---|---|---|
| alpha = 0 | `gap_1200` | Hartley / support size — how many of the 1200 seconds traded at all |
| alpha = 1 | `n_eff_shannon_1200` | Shannon — effective number of participants |
| alpha = 2 | `n_eff_hhi_1200` | collision / Herfindahl — `1 / sum(p^2)` |

### 1. Conditioning on gap<5 does NOT separate ranks 1 and 2

| scope | n | corr(sh,hhi) | corr(gap,sh) | corr(gap,hhi) | corr(sh,v20) | corr(hhi,v20) |
|---|--:|--:|--:|--:|--:|--:|
| all g60+vote | 7,782 | **0.959** | -0.532 | -0.460 | 0.480 | 0.526 |
| gap_1200 < 5 | 2,315 | **0.955** | -0.322 | -0.212 | 0.401 | 0.493 |

**Shannon and HHI remain 0.955 correlated INSIDE the conditioned set** —
they are one feature, so any finding for one is a finding for both. Both
also keep a 0.4-0.5 correlation with `volat_20m`: the v20-proxy problem
(S43) survives the conditioning.

### 2. The quintile tables are NON-MONOTONE on both books (gap<5 held)

| q | regular PF | S-tier B PF | med v20 (reg) |
|--:|---|---|--:|
| 1 | 7.07 | 15.34 | 86 |
| 2 | 4.54 | 31.46 | 110 |
| 3 | **11.61** | **43.61** | 134 |
| 4 | 6.45 | 7.13 | 135 |
| 5 | 7.38 | 11.21 | 158 |

A hump, not a ladder — and `med_v20` climbs monotonically through every
quintile on both books (86->158), which is the proxy showing through.

### 3. ⭐ THE 2x2 — and why the earlier tests DISAGREED

| variant | regular n / PF | **S-tier B n / PF** |
|---|---|---|
| baseline (neither) | 7,782 / 5.26 | 3,864 / 8.04 |
| **gap<5 ONLY** | 2,315 / 6.93 | **1,141 / 15.23** |
| shannon>=650 ONLY | 1,816 / 6.96 | 963 / 8.22 |
| gap<5 + shannon>=650 | 1,421 / **7.67** | 740 / **13.83** |
| hhi>=400 ONLY | 1,951 / 5.85 | 997 / **6.03** |
| gap<5 + hhi>=400 | 1,382 / 7.48 | 688 / 13.25 |

⭐ **THE ANSWER DIFFERS BY BOOK, which is exactly the source of the
confusion.** On S-tier B — *the deep end* — stacking is **strictly WORSE
than gap alone** (13.83 and 13.25 vs **15.23**), and `hhi` alone is worse
than the baseline (6.03 vs 8.04). Only on the broader regular book does
the stack appear to help (7.67 vs 6.93).

### 4. ⭐⭐ THE ISO-TRIP CONTROL KILLS THE REMAINING CASE

The stack cuts the regular book from 2,315 to 1,421 trips. So compare it
against simply tightening **gap alone** to a similar size:

| variant | n | tkds | losers | PF | **avg%** |
|---|--:|--:|--:|---|--:|
| gap<5 | 2,315 | 345 | 354 | 6.93 | +2.98 |
| **gap<5 + sh>=650** | **1,421** | 219 | 237 | **7.67** | **+3.08** |
| gap<3 (gap-only) | 1,825 | 270 | 270 | 7.13 | +3.14 |
| gap<2 (gap-only) | 1,606 | 229 | 242 | 7.05 | +3.21 |
| **gap<1 (gap-only)** | **1,285** | 182 | 200 | 7.30 | **+3.28** |

At a comparable trip count, **gap-only reads PF 7.30 vs the stack's 7.67 —
and BEATS it on average return per trip, +3.28% vs +3.08%.** The stack is
not finding better trades; it is cutting more trips and giving up per-trip
expectancy to do it. The +0.37 PF is inside the noise of the gap ladder
itself (7.05 / 7.13 / 7.30 is not monotone either).

### ⭐ VERDICT — SETTLED

**The rank-1 and rank-2 measures add NOTHING over rank-0.** `n_eff` stays
retired (S43).

- Shannon and HHI are **one feature** (0.955), before and after conditioning.
- Both are **partly v20 proxies** (0.4-0.5), before and after conditioning.
- On the deep book they are **actively harmful** when stacked on the gap.
- On the broad book their apparent gain **does not survive the iso-trip
  control**, and costs per-trip expectancy.

⚠ **Why the earlier reading said "they compose at the deep end":** it
compared the stacked PF (7.67) against the un-stacked one (6.93) **without
controlling for the fact that the stack simply cuts more trips**. That is
the same missing-control trap as lowdens/esf (S43), v20/2022 (S42x), and
speed/pah (S43d). ⭐ **The gap IS the volume feature. One measure per
family, and rank 0 is the one that earns the seat.**

### S43t addendum — the iso-trip control, quantified (user asked what it is)

**What the control is FOR:** PF rises mechanically when trips are cut, so
"added a feature, PF went up" is not evidence the feature found anything.
The stack took the book 2,315 -> 1,421 trips and PF 6.93 -> 7.67. The
control asks: does tightening the EXISTING feature alone, to the same trip
count, get there anyway?

Bootstrap (20,000 resamples) and a random-subsample null (5,000 draws):

| | n | PF | 95% CI |
|---|--:|---|---|
| candidate `gap<5 AND sh>=650` | 1,421 | 7.67 | **[6.34, 9.43]** |
| control `gap<1` (gap-only) | 1,285 | 7.30 | **[5.97, 9.08]** |
| **NULL: random 1,421-trip subsample of gap<5** | 1,421 | **6.93 median** | **[6.16, 7.92]** |

1. **The candidate's and the control's CIs overlap almost entirely** — they
   are statistically indistinguishable.
2. ⭐ **The candidate sits at the 93.6th percentile of the RANDOM-SUBSAMPLE
   null, i.e. INSIDE the 95% band.** Adding Shannon to the gap produces a PF
   change that cannot be distinguished from **randomly discarding the same
   number of trades**.
3. And it is contradicted by expectancy: +3.08%/trip vs +3.21% (gap<2) and
   +3.28% (gap<1). PF and avg% disagree, which a genuine selector would not
   do.

⭐ **RULE (generalise this):** whenever a new feature is stacked on an
existing one, report the candidate against (a) the existing feature
tightened to the same trip count and (b) a random subsample of the same
size. If the candidate does not clear BOTH, the "gain" is trip-count
arithmetic, not selection.


## S43u — ⭐⭐ THE UNIVERSE GATE: n_eff_shannon -> GAP COUNT (2026-08-04)

User: "Replace the n_eff_shannon threshold in the candidate table with a
gap count threshold instead. Likely the opening print could be skewing the
results." **Both halves of that were right, and the fix is now baked.**

### 1. ⭐ THE OPENING-PRINT SKEW — confirmed, and large

`n_eff_shannon` = exp(Shannon H) over per-second volume SHARES in
[09:30, 09:45). Measured over 91,524 ticker-days (2024 Q1, dv >= $2M):

| measure | value |
|---|---:|
| opening print as % of ALL 09:30-09:45 volume | **26.0% median**, 57.7% at p90 |
| median n_eff **with** the open | 27.9 |
| median n_eff **without** the open | **50.6** |
| ticker-days failing `n_eff>=25` that would PASS without the open | **22.0%** |

**Dropping ONE second nearly doubles the measure.** For a fifth of the
affected population the gate was reading *how big the opening auction print
was*, not how continuously the name traded.

### 2. Why the gap count is the right replacement

| family | measures | fails because |
|---|---|---|
| MAGNITUDE | volume, dollar volume, trade count | one monster print buys $11M of dv and says nothing about 10:23 |
| SHAPE (Renyi 1,2) | shannon, hhi | still functions of the volume distribution -> hostage to the same print |
| **OCCUPANCY (Renyi 0)** | **gap count** | **binary per second — invariant to HOW MUCH** |

The gap count measures the SUPPORT of the distribution, not its mass or
shape. And persistence is what this system actually needs: we hold ~5.6
minutes and exit on a 5m-HIGH CROSS, a trigger that cannot fire without
continuous price discovery. ⭐ **Three independent searches have now
converged on time-occupancy: `gap_60<4` (the g60 universe split, S41g-n),
`gap_1200` (the top overlay lens, rp_vol x gap_1200 = 28.97), and now the
universe gate itself.**

### 3. The bake

`build_mr_candidate_1s.fsx`: new `--min-bars` (default **200**, i.e. gaps
<= 700 of 900); `--min-neff` retired to 0 = off but n_eff STILL RECORDED
(both orders). Iso-universe threshold is 210; 200 keeps 56.8% vs n_eff's
54.0%. **Knowability unchanged** — both fold over [09:30,09:45), fully
determined by 09:45, EntryStartMin >= 09:45.

| | rows |
|---|---:|
| new universe | 1,145,230 |
| old universe | 1,121,785 |
| in both | 930,813 |
| **dropped** (n_eff kept, gap rejects) | **190,972** |
| **added** (gap keeps, n_eff rejected) | **214,417** |

⭐ **+2.1% net but ~19% of the universe EXCHANGED**, and the two sides are
not alike:

| group | med dv | med traded secs | med n_eff | med trades |
|---|---:|---:|---:|---:|
| DROPPED | $3.75M | 161 | 33.2 | 696 |
| **ADDED** | **$11.43M** | **262** | 17.6 | **1,076** |

**The days n_eff REJECTED have 3x the dollar volume, 1.6x the traded
seconds and 1.5x the prints of the days it KEPT** — because the richest
names open with the biggest auction prints. `n_eff >= 25` was a partial
INVERSE-liquidity filter.

### 4. The pipeline (all re-run, nothing reused)

| artifact | old | **new** |
|---|---|---|
| candidate table | 1,121,785 | **1,145,230** |
| base (full universe, every spec gate off) | `base_v15` 2,217,950 | **`base_v16` 2,184,698** / 56,312 tkds |
| reference (SPEC v2.6) | `v26_reference` 37,414 | **`v27_reference` 37,214** |

⚠ `flushfader_base_tkds` was NOT reused — it was derived from the OLD
universe and would have silently confined the spec to the days n_eff
admitted. Both runs went over the full 1,137,765 ticker-days (96 min and
69 min respectively). It has since been REGENERATED from `base_v16`
(56,312 rows, schema verified identical to `mr_candidate_1s`).

**GRAND PARITY: 37,214 v27 trips, 37,214 matched in `base_v16`, 0 orphans,
0 ret_exit mismatches.**

### 5. ⭐⭐ THE BOOKS — essentially UNCHANGED

| book | v27 (new) n / PF | v26 (old) n / PF |
|---|---|---|
| whole book $1+ | 29,574 / 2.56 | 29,463 / 2.55 |
| g60 | 10,990 / 3.99 | 10,983 / 3.99 |
| g60 + vote>=1 | 7,703 / 5.19 | 7,698 / 5.19 |
| **BOOK** | **3,811 / 7.91** | 3,806 / 7.89 |

Attribution of the difference:

| | trips | tkds |
|---|---:|---:|
| gained from NEW gap-only days | **5** | 2 |
| **LOST to days the gap gate rejects** | **0** | **0** |

⭐⭐ **A 19% universe swap moved the traded book by +0.13% and lost NOTHING.**

### VERDICT — a HYGIENE win, not an edge win

1. ✅ **SAFE**: zero trades lost, PF identical to 2 decimals at every cut.
2. ❌ **It does NOT improve the book.** Anyone reading §3 might expect a
   gain from admitting 214k richer ticker-days; there isn't one.
3. ⭐ **Because the universe gate is FULLY SUBSUMED DOWNSTREAM.** A day
   that fails a 09:30-09:45 gap count also fails `gap_60 < 4` at the
   signal, plus `dv60>=$100k`, `tc60>=60`, `volat>=40bp`. The 191k rejected
   days were never producing book trades.
4. ⭐ **This is the DISPROPORTION TEST PASSING** (CLAUDE.md rule 3): a
   filter changing 19% of the universe moved PF by ~0%. That is the
   signature of genuine plumbing — the exact inverse of the 2026-07-16
   collapse, where 0.8% of the universe moved PF -26%.
5. ✅ **Keep it anyway.** It is conceptually correct (persistence, not
   print size), it removes a live-trading fragility (n_eff would swing on
   any day with an unusual opening auction), and it costs nothing.

⚠ **A NOTE ON THE BASE BANNER.** `universe = dv_0945_tape >= $0.0M` on a
base run means the ENGINE's gate is off — it does NOT mean the universe is
unfiltered. The `$2M` floor and the gap gate were already applied when
`mr_candidate_1s` was built. The base is therefore: **20m low x volat >=
40bp x dv60>=$100k x tc60>=60 x barnum>=22, over a table that is already
dv>=$2M x gaps<=700.**


## S43v — 🛑⭐⭐ THE chg_1d LOOKAHEAD: the bug, the corrected tables, the new book (2026-08-04)

Found while pinning down raw-vs-adjusted price scales for the passive-fill
study. **It invalidates the S-tier B program (S43i-S43p) and shrinks the
system by roughly a third.**

### 1. The bug

```
chg_1d = signal_vwap * adj_ratio / prev_adj_close - 1     # WRONG (S43i-S43u)
chg_1d = signal_vwap                / prev_adj_close - 1  # CORRECT
```

1s bars arrive from the loader **already split-adjusted** —
`Intraday.fs` states it outright: *"volume ... arrives SPLIT-ADJUSTED (raw
shares / adj_ratio, mirroring **vwap = raw × adj_ratio**)"*. So
`signal_vwap` is already in the adjusted scale and multiplying by
`adj_ratio` applies it twice.

**Verified three independent ways** on AIMD 2024-01-05 (`adj_ratio` = 5.0):

| source | value |
|---|---|
| 1s tape bar vwap @ bucket 49177 | **2.6716** |
| `entry_px / adj_ratio` | **2.6716** ✓ |
| `signal_vwap` @ 49176 = 13.3256, tape = 2.6651 | 2.6651 × 5.0 ✓ |
| engine source comment | *"vwap = raw × adj_ratio"* ✓ |

**Why it is a LOOKAHEAD and not merely a scale error:** `adj_ratio =
adj_close/raw_close` is adjusted for **every split AFTER day D**. In the
correct form the adjusted basis appears in numerator AND denominator and
cancels, leaving a clean ratio. In the wrong form one factor survives, so
the filter reads future corporate actions.

⚠ **This is the S35 contamination class, recurring** — the engine already
carries `--min-dv-0945` marked *"💀 DEPRECATED (S35): real dollars ×
adj_ratio (future-split-dependent — 20% of the universe was inflated in)"*.
Same factor, same mechanism, a year later, in a filter I wrote myself.

### 2. The damage

| measure | value |
|---|---:|
| book trips with `adj_ratio != 1` | **43.6%** |
| p90 / max `adj_ratio` | **370 / 6.0e7** |
| median `chg_1d`, wrong -> correct | **166% -> 50%** |

`chg_1d >= 300%` therefore selected largely on **future reverse splits** —
the signature of a dying microcap — not on "up 300% today".

Decomposing the old S-tier B book (g60, vote>=1, wrong cut, 3,811 trips):

| group | n | % | med adj_ratio | PF | avg% |
|---|--:|--:|--:|---|--:|
| genuine (passes the correct cut too) | 1,091 | 28.6 | 1.0 | 9.59 | +3.55 |
| **⚠ ARTIFACT (wrong formula only)** | **2,720** | **71.4** | **25.0** | 7.22 | +2.53 |

**71.4% of the reported book was the artifact.**

### 3. The corrected tables — the axis INVERTS with tape quality

`chg_1d` bands, PF by universe (n / tkds / PF):

| band | full book $1+ | g60 | g60 + vote>=1 |
|---|---|---|---|
| **<0** | 8,182 / 1,568 / **2.05** | 1,276 / 250 / **5.00** | 669 / 132 / **10.16** |
| [0,20) | 7,341 / 1,463 / 2.60 | 1,637 / 311 / 3.18 | 765 / 168 / 7.51 |
| [20,40) | 4,180 / 790 / 2.28 | 1,779 / 357 / 3.21 | 1,233 / 258 / 3.98 |
| [40,70) | 3,769 / 608 / 2.82 | 2,115 / 377 / 4.98 | 1,542 / 286 / 5.69 |
| [70,120) | 3,047 / 431 / 2.90 | 1,836 / 297 / 3.58 | 1,401 / 236 / 6.14 |
| [120,200) | 1,771 / 228 / 3.36 | 1,274 / 177 / 3.73 | 1,111 / 159 / 3.38 |
| [200,400) | 1,059 / 123 / 4.31 | 868 / 108 / 4.03 | 788 / 100 / 3.74 |
| **>=400** | 225 / 22 / **8.71** | 205 / 21 / **20.11** | 194 / 21 / **19.43** |

⭐ **On the FULL book `chg_1d` is essentially MONOTONE INCREASING (2.05 ->
8.71) — the down-band is the WORST cell. On g60 it INVERTS: `<0` becomes
the best large band.** The low-end edge is conditional on clean tape and is
NEGATIVE on the illiquid side. Both cells are large (8,182 vs 1,276 trips),
so this is a real interaction, not noise.

`chg_3d` is the weak axis in both universes — full book 2.13-3.80 with no
trend, g60 2.37-6.37 and bumpy. **Its S43i "multi-day context" story was
the split artifact. DROPPED.**

⚠ The `>=400` cell is the SAME ~21 ticker-days in all three columns
(22 -> 21 -> 21) while trips go 225 -> 205 -> 194: neither g60 nor the vote
SELECTS anything there. **VERO 2026-01-16 and ATNF 2024-10-16 alone are 48%
of its P&L.** Treat 20.11 as meaningless.

### 4. ⭐⭐ WHY THE GATE IS DROPPED (user call)

**The headline gains are two years of near-zero losses.** The `<20%` cell
in 2020 is 261 trips with **4 losers and 2.4 gross loss points** (PF 463 =
1122/2.4). 2022 is 106 trips, 6 losers, 7.5 loss points.

| scope | baseline | `<0` | `<20%` |
|---|---|---|---|
| all years | 7,703 / **5.19** / +2.43% | 669 / 10.16 / +3.10% | 1,434 / 8.63 / +2.86% |
| ex-2020 | 6,515 / 4.57 / +2.27% | 555 / 7.81 / +2.76% | 1,173 / 6.57 / +2.54% |
| **ex-2020 & ex-2022** | 6,099 / **4.59** / +2.28% | 497 / **7.12** / +2.72% | 1,067 / **6.06** / +2.50% |

**On the five clean years the cut is worth 1.32x on PF and 1.10x on
expectancy, for discarding 82% of the trips.** Per-year ratios (cut PF /
baseline PF) for `<20%`: 1.43 / **0.78** / 1.97 / 1.90 / **0.68** — it
FAILS in 2023 and 2026, the two most recent clean years. The pooled 1.32
is a ratio of sums and is carried by 2024-25.

Random-subsample null on the five clean years:

| cut | n | PF | null 95% | pctile | avg% | null 95% | pctile |
|---|--:|---|---|--:|---|---|--:|
| `<0` | 497 | 7.12 | [3.49, 6.34] | **99.6th** | 2.72 | [1.96, 2.61] | **99.7th** |
| `<20%` | 1,067 | 6.06 | [3.84, 5.57] | 99.8th | 2.50 | [2.08, **2.50**] | 97.8th |

`<20%`'s EXPECTANCY sits exactly on the null's upper bound. `<0` clears
both decisively.

**DECISION (user): drop the `chg_1d` gate entirely; keep `chg_1d < 0` as a
SIZING tier**, alongside `speed` (S43p) and `z_20m` (S43s) — features that
are real but too small/conditional to define a book. Reasons: the gain is
two loss-free years; both cuts fail 2023 and 2026; the `<0` edge is a
sign-flipping interaction; and it contradicts the volume preference set in
S43p.

### 5. ⭐ THE CORRECTED BOOK

```
BOOK   =  g60 (gap_60 < 4)  AND  vote >= 1 of 6      ($1+, SPEC v2.6)
S-TIER =  ht = 1  AND  secs_since_halt in [120, 1200)
```

mc=3, A OR B, on `v27_reference`:

| yr | n | PF | win% | avg% |
|----|--:|---|--:|--:|
| 2020 | 521 | 9.399 | 85.0 | +2.84 |
| **2021** | 617 | **2.782** | 72.8 | +1.18 |
| 2022 | 211 | 3.937 | 84.4 | +1.83 |
| 2023 | 282 | 3.649 | 78.7 | +1.75 |
| 2024 | 554 | 3.562 | 78.3 | +2.07 |
| 2025 | 772 | 3.253 | 75.6 | +1.82 |
| 2026 | 373 | 4.222 | 79.9 | +2.44 |
| **total** | **3,330** | **3.901** | 78.3 | **+1.97** |

**Every year positive, worst 2.78.** Against the pre-fix claim of 1,791 @
6.21 / +2.45%: **1.9x the trips at 0.63x the PF and 0.80x the per-trip
return.** The system is real but materially smaller than reported this
morning — a third of that PF was the artifact.

### ⭐ LESSONS

1. **Check the SCALE of every price column against the raw tape before
   building a ratio on it.** One `duckdb` query against
   `/mnt/d/trading-edge-bulk/trades/` would have caught this on day one.
2. **`adj_ratio` is FUTURE-SPLIT-DEPENDENT. It must never appear
   un-cancelled in a gate.** In any ratio of two adjusted prices it
   cancels; if you find yourself multiplying by it, stop.
3. **The disproportion test would have flagged it**: a "what did the stock
   do yesterday" filter tripled a PF. Day-context should not do that.
4. It was found by an unrelated task (the passive-fill study) forcing a
   raw-vs-adjusted audit. **Cheap audits of "obvious" plumbing keep paying.**


## S43w — gap_1200 x volume participation: ROBUST, but CONDITIONAL on g60 (2026-08-04)

User: "whether they are robust features, both together and in isolation."
Run with the full S43v battery (three books, year columns with LOSER
counts, ex-artifact-year pooling, random-subsample null).

**Definitions** (`rp_vol` re-derived from S41, doc line 4761, and
structurally verified on 29,574 trips: **0 violations** of `vol_leg <=
cum_vol`, **0** of `legbars <= bars_present`, **0 nulls**):

```
leg rate     = vol_leg / (bars_since_first_low + 1)
pre-leg rate = (cum_vol - vol_leg) / (bars_present - (bars_since_first_low + 1))
rp_vol       = leg rate / pre-leg rate
```

⚠ Median `rp_vol` here is **0.554**, not S41's 0.73 — that table was the
v2.0 population. Bands recalibrated to v27. `corr(rp_vol, gap_1200) =
0.072` ⇒ genuinely independent, so a conjunction is near-multiplicative.

### 1. gap_1200 — the shape INVERTS on g60

| gap_1200 | full n / PF | g60 n / PF | g60+vote n / PF |
|---|---|---|---|
| <5 | 3,106 / 5.64 | **3,106 / 5.64** | 2,302 / 6.87 |
| [5,15) | 1,241 / 4.88 | 1,221 / 4.86 | 932 / 5.33 |
| [15,50) | 2,632 / 2.37 | 2,304 / 2.46 | 1,710 / 3.00 |
| [50,200) | 5,184 / 2.88 | 2,986 / 3.60 | 1,954 / 5.60 |
| [200,600) | 8,755 / **2.20** | 1,337 / **5.04** | 785 / **7.52** |
| >=600 | 8,656 / 2.06 | 36 / 3.39 | 20 / 1.60 |

⭐ **`gap_1200 < 5` is FULLY SUBSUMED by g60** — 3,106 trips in both
columns, identical. Zero gaps in 1200 bars implies few in 60.
⭐ **On the full book gap_1200 is monotone DECREASING** (5.64 -> 2.06:
fewer gaps = better). **On g60 it INVERTS at the high end** — `[200,600)`
reads 5.04 / 7.52 vs 2.20 on the full book. A name with `gap_1200` = 300
but `gap_60 < 4` was sparse 20 minutes ago and is dense NOW: **the tape
just woke up**, which is a different and good animal.

### 2. rp_vol — does NOTHING on the full book, strong on g60

| rp_vol | full n / PF | g60 n / PF | g60+vote n / PF |
|---|---|---|---|
| <0.35 | 4,893 / 1.95 | 1,755 / 4.03 | 1,249 / 5.86 |
| [0.35,0.5) | 7,380 / 2.76 | 3,232 / 2.89 | 2,112 / 3.81 |
| [0.5,0.65) | 6,286 / 2.68 | 2,461 / 3.35 | 1,684 / 4.07 |
| [0.65,0.8) | 4,226 / 3.02 | 1,300 / 4.80 | 880 / 5.37 |
| [0.8,1.0) | 3,547 / **2.41** | 1,013 / 7.69 | 770 / **14.88** |
| [1.0,1.3) | 2,008 / 2.59 | 696 / 7.56 | 563 / 7.65 |
| [1.3,2.0) | 1,033 / 2.85 | 419 / **14.20** | 360 / 12.91 |
| >=2.0 | 201 / 3.63 | 114 / 12.00 | 85 / 9.94 |

⚠ On the full book it is **FLAT** (1.95-3.63) and S41's star band
`[0.8,1.0)` reads **2.41, BELOW its neighbours**. On g60 it is strongly
increasing above 0.65.

### 3. ⭐⭐ THE ROBUSTNESS TEST THAT chg_1d FAILED

`rp_vol >= 0.8` vs each book's own baseline:

| book | all-yrs cut/base = ratio | **ex-2020 & 2022** |
|---|---|---|
| full | 2.57 / 2.56 = **1.00** | 2.27 / 2.44 = **0.93** |
| g60 | 8.74 / 3.99 = **2.19** | 7.46 / 3.55 = **2.10** |
| g60+vote | 11.10 / 5.19 = **2.14** | 9.24 / 4.59 = **2.01** |

⭐⭐ **The ratio barely moves when the two near-loss-free years are
removed (2.19 -> 2.10).** `chg_1d` collapsed 1.66 -> 1.32 under the same
test. And **every year has REAL losses** (40/60/7/29/45/49/35 losers) —
no zero-loss artifact anywhere.

Random-subsample null, g60+vote, **ex-2020 & 2022** (baseline 6,099 @ 4.59
/ +2.28%):

| cut | n | PF | null 95% | pctile | avg% | null 95% | pctile |
|---|--:|---|---|--:|---|---|--:|
| `rp_vol>=0.8` | 1,235 | 9.24 | [3.88, 5.52] | **100th** | 2.71 | [2.09, 2.47] | **100th** |
| `gap_1200<5` | 1,925 | 6.10 | [4.05, 5.22] | **100th** | 2.81 | [2.14, 2.43] | **100th** |
| **BOTH** | **396** | **23.39** | [3.37, 6.51] | **100th** | **3.31** | [1.90, 2.65] | **100th** |
| EITHER | 2,764 | 6.28 | [4.18, 5.06] | **100th** | 2.70 | [2.18, 2.39] | **100th** |

All four clear on **both** PF and expectancy — the axis that killed
`chg_1d < 20%` (which sat exactly on the null's upper bound).

### 4. The conjunction, per year (with loser counts)

| yr | base PF | n | tkds | losers | cell PF | **cell avg%** | base avg% |
|---|--:|--:|--:|--:|---|--:|--:|
| 2020 | 13.52 | 143 | 24 | 15 | 49.43 | **4.42** | 3.30 |
| 2021 | 4.02 | 210 | 38 | 23 | 31.94 | **3.05** | 1.72 |
| 2022 | 4.25 | 20 | 3 | 1 | 111.36 | **2.11** | 2.04 |
| 2023 | 4.34 | 21 | 3 | 0 | inf | **2.86** | 2.11 |
| 2024 | 4.31 | 74 | 15 | 10 | 13.22 | **4.04** | 2.50 |
| 2025 | 4.18 | 59 | 10 | 1 | 222.41 | **2.70** | 2.13 |
| 2026 | 7.62 | 32 | 6 | 7 | 13.00 | **4.72** | 3.37 |

⭐ **Expectancy beats baseline in 7 of 7 years.** The four years with
real loss counts read 49 / 32 / 13 / 13 against baselines of 13.5 / 4.0 /
4.3 / 7.6. ⚠ The `inf` / 111 / 222 cells are **3-10 ticker-day years** —
ignore those PFs. ⚠ **Frequency: 99 tkds over 7 years = 14/yr.**

### 4b. ⚠ IS rp_vol ROBUST EVERY YEAR? NO — 2023 FAILS (user question)

`rp_vol >= 0.8` vs each book's own baseline, per year:

| yr | g60 PF ratio | g60 **avg ratio** | g60+vote PF ratio | g60+vote **avg ratio** | cell losers (g60 / vote) |
|---|--:|--:|--:|--:|---|
| 2020 | 1.73 | 1.21 | 1.44 | 1.08 | 61 / 40 |
| 2021 | 3.82 | 1.47 | 4.25 | 1.53 | 69 / 60 |
| 2022 | 3.29 | **1.06** | 3.00 | **1.01** | 8 / 7 |
| **2023** | **0.66** | **0.55** | **0.69** | **0.55** | 35 / 29 |
| 2024 | 2.02 | 1.31 | 1.95 | 1.24 | 72 / 45 |
| 2025 | 5.58 | 1.39 | 3.80 | 1.13 | 64 / 49 |
| 2026 | **1.05** | 1.29 | **0.85** | 1.17 | 46 / 35 |

⚠ **2023 is a CLEAR failure on both books and both metrics — the cell
earned HALF the baseline per trip (avg ratio 0.55) with 35 and 29 REAL
losers.** Not thin-sample noise. ⚠ 2022's big PF ratios (3.29/3.00) are
near-washes on EXPECTANCY (1.06/1.01) — only 8 and 7 losers, so the PF is
the loss-tail, not the edge.

**Honest statement: 6 of 7 years on expectancy, ONE clear failure year** —
NOT "robust every year". ⭐ Note 2023 is also the year `chg_1d` failed
(S43v), so it may be a regime in which quality features generally invert
rather than anything specific to `rp_vol`.

### ⚠ NOTE ON THE NULL-TEST TABLES

The two `null 95%` columns in §3 are DIFFERENT quantities and should read
`null 95% (PF)` and `null 95% (avg%)`. **What the test does:** draw a
RANDOM subset of the book of exactly the candidate's size (without
replacement), compute its PF, repeat 4,000x. That is the distribution of
PFs a cell of that size produces BY CHANCE. Random sampling has the same
EXPECTED PF as the parent, so the null centres near the baseline and its
spread is pure sampling noise — which widens as the cell shrinks. A
candidate inside the band is indistinguishable from picking trades at
random, i.e. the feature selects nothing. It is the fix for "small cell,
high PF, looks impressive".

### ⭐ VERDICT

1. **Both are ROBUST — genuinely, unlike `chg_1d`.** They survive the
   ex-artifact-year pooling, the null on both axes, and per-year loser
   counts.
2. **Both are CONDITIONAL on g60.** `rp_vol` is worth **1.00x on the full
   book** (0.93 ex-artifact) and **2.0-2.2x on g60**. This is an
   AMPLIFICATION, not the sign INVERSION `chg_1d` had — a weaker form of
   conditionality, but conditional nonetheless.
3. **`rp_vol` is the stronger of the two** (2.01x vs `gap_1200<5`'s 1.33x
   ex-artifact) and is the one that is NOT already implied by g60.
4. **`gap_1200 < 5` is redundant with g60 at the low end** (identical
   3,106 trips) — what it adds on g60 lives at the HIGH end, where the
   "tape just woke up" cell reads 7.52.
5. ⭐ **The conjunction is a genuine TOP SIZING TIER**: 396 trips @ 23.39
   / +3.31%, expectancy-positive vs baseline in 7/7 years, at ~14 ticker-
   days a year. Independent inputs (corr 0.072) ⇒ near-multiplicative.
   **Not a gate — a size lever**, on the same footing as `speed`,
   `z_20m` and `chg_1d < 0`.


## S43x — COST STUDIES RE-DERIVED on the corrected book (2026-08-04)

Supersedes the book-level aggregates of S43q/S43r (their per-trip TAPE
measurements always stood). Re-measured from raw prints over **807 dates**
(was 506) for the corrected book: **3,330 trips / 1,191 tkds** at mc=3.

⚠ **MY PREDICTION WAS WRONG.** I expected costs to worsen ~25% because the
edge fell from +2.45% to +1.97%/trip. **Total cost FELL** (0.472% vs
0.631% at $50k) — the corrected book has a stronger exit credit and a
marginally tighter spread — so the two effects largely cancel.

### 1. Spread — unchanged (it is a property of the tape)

| bucket | n | med px | `step` $ | `roll` $ | step % | `p_rev` | `p_flat` |
|---|--:|--:|--:|--:|--:|--:|--:|
| $1.00-1.50 | 346 | 1.23 | 0.005 | 0.0019 | 0.482 | 0.875 | 0.927 |
| $1.50-2 | 327 | 1.73 | 0.009 | 0.0021 | 0.487 | 0.848 | 0.911 |
| $2-3 | 523 | 2.46 | 0.010 | 0.0026 | 0.368 | 0.844 | 0.889 |
| $3-5 | 705 | 3.91 | 0.010 | 0.0036 | 0.247 | 0.800 | 0.842 |
| $5-10 | 776 | 6.65 | 0.010 | 0.0061 | 0.153 | 0.727 | 0.764 |
| $10+ | 653 | 18.60 | 0.020 | 0.0210 | 0.087 | 0.610 | 0.583 |

Same as S43q: 1 tick across $1.50-$10, HALF a tick at $1.00-1.50 (retail
price improvement), 2 ticks at $10+. `p_flat` 0.58-0.93 ⇒ Roll still
biased low. Read `roll <= true <= step`.

### 2. ⭐ Delay — the exit credit got STRONGER

| work window | med $vol | size @10% | entry cost | **exit cost** | **net delay** |
|---|--:|--:|--:|--:|--:|
| T = 5s | $75k | $7.5k | -0.016 | -0.001 | **-0.018** |
| T = 15s | $200k | $20.0k | +0.013 | -0.018 | **-0.006** |
| T = 30s | $367k | $36.7k | +0.031 | -0.042 | **-0.012** |
| T = 60s | $637k | $63.7k | +0.093 | **-0.102** | **-0.009** |
| T = 120s | $1.06M | $106k | +0.232 | -0.110 | +0.122 |
| T = 300s | $2.05M | $205k | +0.450 | -0.055 | +0.395 |

⭐⭐ **Net delay is NEGATIVE — a CREDIT — all the way out to a 60-second
work window** (was +0.039 there). The exit pays -0.102 while the entry
costs +0.093. Execution delay is FREE up to roughly **$64k**. The S43r
mechanism is confirmed and strengthened: long a collapse (delay hurts),
short a continuation past the 5m-high cross (delay pays).

### 3. ⭐⭐ THE RE-DERIVED COST LADDER (mc=3, 3,330 trips, Y=0.5, 10% partic)

| position | spread | delay | impact | **total** | % oversize | **PF net** | **avg% net** | **edge lost** |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| gross | — | — | — | — | — | **3.90** | **+1.97** | — |
| **$10k** | 0.260 | **-0.059** | 0.091 | **0.292** | 0.2% | **3.29** | **+1.68** | **15%** |
| **$25k** | 0.260 | **-0.074** | 0.144 | 0.330 | 2.2% | **3.23** | **+1.64** | **17%** |
| **$50k** | 0.260 | 0.008 | 0.204 | 0.472 | 6.5% | **3.03** | **+1.50** | **24%** |
| $100k | 0.260 | 0.134 | 0.288 | 0.682 | 23.4% | 2.65 | +1.29 | 35% |
| $250k | 0.260 | 0.334 | 0.455 | 1.050 | 58.8% | 2.08 | +0.92 | 53% |

(Old contaminated-book ladder for reference: $50k = 0.631 total / PF 4.23
/ +1.82% / 26%.) **Sweet spot remains $25-50k. $250k is still the
practical ceiling** (59% of trips oversize).

### 4. Liquidity check — still passes, less emphatically

| dv60 quintile | n | range | med px | losers | PF | avg% | net pts |
|---|--:|---|--:|--:|--:|--:|--:|
| 1 | 666 | $135-750k | $1.98 | 146 | 4.62 | +2.09 | 1,393 |
| 2 | 666 | $750k-1.26M | $3.26 | 146 | 4.03 | +2.03 | 1,354 |
| 3 | 666 | $1.26-1.92M | $4.24 | 160 | 3.23 | +1.65 | 1,099 |
| 4 | 666 | $1.92-3.55M | $5.79 | 136 | 3.57 | +1.83 | 1,219 |
| 5 | 666 | $3.57M+ | $8.30 | 134 | 4.24 | +2.23 | 1,486 |

Flat-ish and mildly U-shaped (4.62 / 4.03 / 3.23 / 3.57 / 4.24) rather
than the contaminated book's clean "most liquid is best". Net points are
still even across all five. **A liquidity floor remains close to free.**

Capacity census: median entry-minute $vol **$1.55M** on **1,435 trades**
(min $135k = the `dv60 >= $100k` gate; Q1 $866k, Q3 $2.98M).

### VERDICT

**The system survives costs.** At the $25-50k sweet spot it nets **PF
3.0-3.2 and +1.5-1.6% per trip** after measured spread, delay and modelled
impact, on ~476 trades/year at mc=3. ⚠ Still not covered: the next-bar-vwap
FILL is assumed attainable, and the sqrt impact term remains the weakest
link (daily-V denominator vs seconds-long execution). ⏭ The passive-fill
study — whether resting bids are adversely selected — is the last open
cost question, and the biggest lever: earning the 0.26% spread rather than
paying it would roughly HALVE the $50k cost.


## S43y — ⭐⭐ THE PASSIVE-FILL STUDY: cross the ENTRY, rest the EXIT (2026-08-04)

The last open cost question, and the answer is an **asymmetry** — not the
uniform "use limit orders and capture the spread" the plan assumed.

**Method.** For every trip in the corrected mc=3 book (3,330 trips, 807
dates) measure from raw lit prints:
- entry side: the **MIN** print over (entry_sec, entry_sec+T], capped at
  `exit_sec` — a fill after the exit is a MISS, not a trade;
- exit side: the **MAX** print over [exit_sec, exit_sec+T].
A limit fills if the tape reaches it (`< L` to buy, `> L` to sell).
⚠ The two files are deliberately NOT symmetric: entry windows are capped
at the exit, exit windows extend PAST it.

### 1. Fill rates (limit at mid -/+ half the measured spread)

| side | T=30s | T=60s | T=300s |
|---|--:|--:|--:|
| BID at `px - step/2` | — | **87.4** | 92.5 |
| OFFER at `exit_px + step/2` | 85.1 | **88.8** | 95.3 |

Both sides fill readily. ⚠ `at` and `through` fill rules COINCIDE here
because `px -/+ step/2` sits off the penny grid, so the queue-priority
distinction does not bite.

### 2. ⭐⭐ ADVERSE SELECTION ON THE ENTRY IS REAL AND LARGE

`L = px - step/2`, 60s window:

| group | n | % | PF | **avg%** | med% | win% |
|---|--:|--:|--:|--:|--:|--:|
| FILLED | 2,909 | 87.4 | 3.54 | **+1.83** | +2.19 | 77.2 |
| **⭐ MISSED** | 421 | 12.6 | **8.72** | **+2.90** | +3.19 | 85.7 |

**The 12.6% you miss are far BETTER trades** — +2.90% vs +1.83% per trip,
PF 8.72 vs 3.54, 85.7% win vs 77.2%. Structural, not tunable: the signal
is a NEW 20-MINUTE LOW, so a resting bid fills exactly when the low keeps
extending and misses exactly when the reversal is immediate — which is the
trade you want.

### 3. ⭐⭐ THE HEAD-TO-HEAD (net points; exits that miss fall back to
crossing at the MEASURED 60s post-signal vwap, not an assumed price)

| strategy | n | PF | avg% | net pts | **vs crossing** |
|---|--:|--:|--:|--:|--:|
| **A: cross entry + cross exit** | 3,330 | 3.33 | +1.705 | 5,677 | **100%** |
| B: REST bid 60s + cross exit | 2,909 | 3.54 | +1.835 | 5,339 | **94%** |
| **⭐ C: cross entry + REST offer 60s** | 3,330 | 3.52 | +1.817 | **6,050** | **107%** |
| D: REST both 60s | 2,909 | 3.73 | +1.946 | 5,661 | **100%** |

⭐⭐ **PASSIVE ENTRY COSTS 6%. PASSIVE EXIT GAINS 7%. BOTH TOGETHER IS A
WASH.** The optimum is **C: cross the entry, rest the exit.**

⚠ **THE TRAP, in miniature:** rows B and D have HIGHER PF and HIGHER
per-trip return than crossing while earning LESS money. Reported on PF and
avg% alone, passive entry looks like a clear win. It is not — the better
per-trip stats come from having discarded the best trades. Same PF-vs-net
divergence as the S43t iso-trip control. **Always report NET.**

### ⭐ WHY — and it is the same mechanism S43x measured

The system is **long a collapse** on entry and **short a continuation** on
exit:

| leg | what the tape does | delay (S43x) | passive fill is... |
|---|---|--:|---|
| ENTRY | price still falling into a new 20m low | +0.093 @60s | **adversely** selected -> CROSS |
| EXIT | price keeps rising past the 5m-high cross | **-0.102** @60s | **favourably** selected -> REST |

The S43x exit credit and this exit gain are the same phenomenon measured
two ways.

### VERDICT

1. ❌ **The "limit orders everywhere, capture the spread + rebates" plan
   does NOT survive.** On the entry it is net-NEGATIVE.
2. ✅ **Cross the entry, rest the exit = +7% net** on top of the S43x
   ladder — at the $50k tier roughly +1.50% -> +1.60% per trip.
3. ⭐ Rebates are a bonus not modelled here: strategy C pays a taker fee
   on entry and earns a MAKER rebate on the exit fill.
4. ⚠ Not modelled: the exit fallback assumes we cross at the measured 60s
   vwap when the offer misses; queue position is not simulated (the
   off-grid limit makes `at`/`through` identical, which flatters slightly).


## S43z — SUB-$1: the filters break below $0.50, and the REBATE THESIS FAILS (2026-08-04)

User: "test the 3 books on < $1 stocks... those are where earning maker
rebates makes the most difference." **Both halves tested. The cost half is
confirmed; the thesis still fails, for a structural reason.**

### 1. ⭐ THE FILTER STACK BREAKS AT $0.50 (user asked: does g60 add
nothing on ALL sub-$1 buckets? NO — there is a clean break)

| price tier | full PF | g60 PF | **g60 lift** | vote PF | **total lift** |
|---|--:|--:|--:|--:|--:|
| <$0.10 | 3.25 | 3.13 | **0.96** | 2.61 | **0.80** |
| $0.10-0.25 | 2.31 | 2.41 | 1.04 | 2.24 | **0.97** |
| $0.25-0.50 | 1.85 | 1.60 | **0.86** | 1.75 | **0.95** |
| **$0.50-1.00** | 2.38 | 3.03 | **1.27** | **3.33** | **1.40** |
| $1-2 | 3.03 | 4.92 | 1.62 | 6.30 | 2.08 |
| $2-5 | 2.54 | 3.27 | 1.29 | 5.22 | 2.06 |
| $5+ | 2.36 | 4.30 | 1.82 | 4.77 | 2.02 |

**Below $0.50 the stack is INERT or HARMFUL** (total lift 0.80 / 0.97 /
0.95; g60 REDUCES PF in $0.25-0.50). **$0.50-1.00 works but at 1.40x vs the
~2.05x it delivers everywhere above $1.** The aggregate "sub-$1 filters
don't work" was masking two different populations.

### 2. But even the working tier has a NEGATIVE year

`$0.50-1.00`, g60 + (vote>=1 OR S-tier A), **mc=3: 557 @ 2.846 / +1.82%**

| yr | 2020 | **2021** | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|--:|--:|--:|--:|--:|--:|--:|
| PF | 1.364 | **0.628** | 23.672 | 7.092 | 5.024 | 4.091 | 1.542 |
| avg% | +0.69 | **-0.92** | +2.46 | +3.02 | +2.11 | +2.39 | +0.78 |

2021 is outright negative; 2020 and 2026 are 1.36 / 1.54 on 77 and 90
trips (NOT thin). 2022's 23.67 is 24 trips = meaningless. Whole sub-$1
book at mc=3: **1,160 @ 2.212 / +1.51%**, worst year 1.296. Compare the
$1+ book: 3.901, every year >= 2.78.

### 3. ⭐ THE COST HALF OF THE THESIS IS CONFIRMED

Sub-penny quoting is real below $1 (~32% of non-zero price changes are
sub-penny). Measured `step` on the sub-$1 book:

| tier | med px | step $ | **step %** | roll % | p_flat | p_rev |
|---|--:|--:|--:|--:|--:|--:|
| <$0.25 | 0.167 | 0.0001 | **0.110** | 0.111 | 0.774 | 0.620 |
| $0.25-0.50 | 0.381 | 0.0003 | **0.078** | 0.115 | 0.671 | 0.563 |
| $0.50-1.00 | 0.729 | 0.0005 | **0.065** | 0.102 | 0.632 | 0.532 |
| *($1+ book)* | *3.91* | *0.010* | ***0.260*** | — | — | — |

⭐ **Sub-$1 spread is ~4x CHEAPER in percentage terms than the $1+ book**,
and the $1-2 tier is the market's worst (0.352%) because a penny tick on a
$1.40 stock is huge. ⭐ Note `roll` and `step` now AGREE (0.102 vs 0.065)
where they diverged badly above $1 — the finer grid means less bid-ask
bounce (`p_rev` 0.53 vs 0.88), so Roll's assumptions fit worse but `step`
shrinks to meet it.

### 4. ⭐⭐ AND YET THE REBATE THESIS FAILS

| | sub-$1 | $1+ |
|---|--:|--:|
| bid fill @60s | 91.0% | 87.4% |
| offer fill @60s | 93.5% | 88.8% |
| **MISSED** trips avg% | **+3.64** | +2.90 |
| FILLED trips avg% | **+1.30** | +1.83 |
| **selection penalty** | **2.8x** | 1.6x |

| strategy | sub-$1 net | vs cross | ($1+) |
|---|--:|--:|--:|
| A: cross + cross | 1,621 | 100% | 100% |
| B: **REST bid** + cross exit | 1,376 | **85%** | 94% |
| **C: cross entry + REST offer** | 1,742 | **108%** | 107% |
| D: REST both | 1,483 | **92%** | 100% |

⭐⭐ **THE SAME ASYMMETRY HOLDS, AND ADVERSE SELECTION IS WORSE ON SUB-$1**
(passive entry costs **15%** vs 6%; missed trades run **2.8x** the filled
ones). **This is what kills the rebate thesis**: earning maker rebates on
BOTH legs requires resting the ENTRY, and resting the entry is precisely
what this system cannot afford. The only winning strategy (C) is a TAKER on
entry — so it earns a rebate on one leg only, exactly as at $1+.

⚠ **The fee side is also why sub-$1 was excluded originally** (SPEC v1.1:
"sub-$1 FEE-DEAD every EU route"): sub-$1 is typically billed as a
PERCENTAGE OF PRINCIPAL rather than per share. No schedule is assumed here
— but at a taker fee anywhere near 0.3% of principal the entry leg alone
eats >20% of a +1.40% gross edge, dwarfing the 0.065-0.11% spread saving.

### 5. gap_1200 on sub-$1 (user: "maybe that one will do better?") — NO

⚠ Counter to expectation, **sub-$1 tape is DENSER, not sparser**: median
`gap_1200` = 111-157 vs 281-313 above $1, and 49-60% pass g60 vs 36-37%.
The sub-$1 names that clear the universe gate are heavily-traded pennies.

| gap_1200 | <$0.25 | $0.25-0.50 | $0.50-1.00 | $1+ |
|---|--:|--:|--:|--:|
| <5 | 1.67 | 3.15 | **5.85** | **5.64** |
| [5,15) | 2.87 | 0.83 | 1.25 | 4.88 |
| [15,50) | 2.78 | 3.91 | 2.56 | 2.37 |
| [50,200) | 1.91 | 0.79 | 2.12 | 2.88 |
| [200,600) | 3.98 | 2.91 | 2.16 | 2.20 |
| >=600 | 2.16 | 2.67 | 2.32 | 2.06 |

**On $1+ it is a clean monotone ladder (5.64 -> 2.06). Below $0.50 it is
pure noise** (3.15 / 0.83 / 3.91 / 0.79 — alternating, no structure). In
$0.50-1.00 only the extreme `<5` corner fires (5.85 on 434 trips), with
everything else flat at 2.1-2.6 — **a corner, not a gradient.**

That corner clears the null decisively (PF 5.85 vs null95(PF) [1.87, 3.13];
avg 3.08% vs null95(avg) [1.08, 1.81]; both 100th pctile) but is
**practically unusable**:

| yr | 2020 | 2021 | 2022 | **2023** | 2024 | 2025 | 2026 |
|---|--:|--:|--:|--:|--:|--:|--:|
| n | 32 | 51 | 7 | **0** | 81 | 181 | 82 |
| PF | 2.55 | **1.09** | inf | **absent** | 32.8 | 9.59 | 5.76 |

**2023 has ZERO trips**, 2021 is breakeven, 2022 is 7 trips, and the money
is entirely 2024-2026. 58 tkds over 7 years = ~8/yr, recency-concentrated.

⭐ **THE DECIDER: the same cell exists at $1+ SEVEN TIMES LARGER and
equally good** — `gap_1200 < 5` is 3,106 trips @ 5.64 above $1 vs 434 @
5.85 here. There is no reason to reach into sub-$1 for a corner already
held with far better year coverage.

### 6. Are the $0.50-1.00 names FALLEN $1+ STOCKS? NO — and the fallen ones are DEAD

User hypothesis: "maybe $0.50-1.00 is strong because these were formerly
>$1 stocks which dipped below $1 during the day."

⭐ **THE ADJUSTMENT DIRECTION (user asked: multiply or divide?)** —
`adj_ratio = adj_close / raw_close`, so **raw = adjusted / adj_ratio**:
prior close in day-D raw scale = **`prev_adj_close / adj_ratio`
(DIVIDE)**. Confirmed by the engine's own gate, `Backtest.fs:216`:
`coalesce(prev_adj_close / nullif(adj_ratio,0), 0) >= $minprevclose`,
documented as *"PRIOR day's close in day-D raw (post-split) scale"*.
⚠ MULTIPLYING is what produced the S43v lookahead.

**Only 3.6% of $0.50-1.00 trips closed >= $1 the prior day** (median prior
close **$0.53** vs median entry **$0.752**). These are NOT fallen $1+
names — they are genuine sub-$1 stocks **up ~42% on the day**.

| $0.50-1.00 split | n | tkds | PF | avg% | med prev | med today | med day chg |
|---|--:|--:|--:|--:|--:|--:|--:|
| **was >= $1 yesterday** | 136 | 31 | **1.03** | **+0.07** | $1.16 | $0.931 | **-19%** |
| was sub-$1 yesterday | 3,670 | 505 | **2.49** | **+1.50** | $0.52 | $0.746 | **+39%** |

⭐⭐ **THE HYPOTHESIS INVERTS: the fallen-from-$1 names are the DEAD
subgroup — PF 1.03, +0.07%/trip, NO edge.** All of the bucket's strength
comes from pennies RUNNING (+39% on the day). Including the fallen names
would add the one dead population, not rescue anything.

Coherent with S43v: on the FULL book corrected `chg_1d` is monotone
INCREASING (2.05 -> 8.71), so a name down 19% on the day sits in the worst
part of that curve. Other tiers are too small to read (22 trips <$0.25, 13
in $0.25-0.50).

### VERDICT

**Sub-$1 does not earn a place.** (-1) the $0.50-1.00 strength is NOT fallen $1+ names and the fallen ones are dead (§6); (0) `gap_1200` does not rescue it either (§5); (a) the filter stack breaks below $0.50
and is only 1.40x effective in $0.50-1.00 vs ~2.05x above $1; (b) even that
tier has a NEGATIVE 2021 and two more years at ~1.4-1.5; (c) the cheap
spread is real but small in absolute terms next to percentage-of-principal
fees; (d) **the rebate case specifically requires the one thing this system
cannot do — rest the entry.** ⭐ The `cross entry / rest exit` rule from
S43y is CONFIRMED on an independent population, and more strongly.


## S43aa — the "TAPE JUST WOKE UP" cell (gap_1200 high x g60): NOT ROBUST (2026-08-04)

User: "you've shown that stocks that pass g60 and have a sparse g1200 have
good expectancy. Let's study the robustness of it." **It does not survive.**

### 1. Fine bands in the high-gap region (g60 book, $1+, v27)

| gap_1200 | n | tkds | losers | PF | avg% | med hr ET | med v20 |
|---|--:|--:|--:|--:|--:|--:|--:|
| <100 | 8,266 | 1,237 | 1,703 | 4.00 | +2.09 | 10:50 | 97 |
| [100,200) | 1,351 | 270 | 300 | 3.24 | +1.74 | 11:43 | 83 |
| [200,300) | 656 | 158 | 157 | 3.85 | +1.82 | 12:11 | 78 |
| **[300,450)** | 512 | 119 | 89 | **6.30** | +2.51 | 11:48 | 79 |
| **[450,600)** | 169 | 42 | 39 | **7.66** | +2.94 | 12:13 | 93 |
| [600,800) | 36 | 9 | 9 | 3.39 | +2.43 | 13:19 | 67 |

The rise is real in aggregate (3.24 -> 3.85 -> 6.30 -> 7.66) and collapses
past 600 on 36 trips. Strong zone = **[300,600), 681 trips / 161 tkds**.
⚠ Note `med_hr_et` climbs 10:50 -> 12:13 and `med_v20` FALLS: this is a
**midday, lower-volatility reactivation** population.

### 2. ⭐⭐ PER-YEAR KILLS IT

| yr | base PF / avg% | cell n | losers | lossPts | cell PF | cell avg% | **avg ratio** |
|---|---|--:|--:|--:|---|--:|--:|
| 2020 | 8.40 / 2.73 | 157 | 15 | 15.8 | 29.54 | 2.87 | **1.05** |
| 2021 | 4.64 / 1.78 | 60 | 23 | 32.1 | 3.04 | 1.09 | **0.61** |
| 2022 | 4.03 / 1.87 | 50 | 17 | 53.1 | 1.47 | 0.49 | **0.26** |
| 2023 | 3.03 / 1.58 | 58 | 4 | 11.4 | 20.75 | 3.88 | **2.46** |
| 2024 | 3.30 / 2.06 | 148 | 15 | 14.3 | 40.56 | 3.83 | **1.86** |
| 2025 | 3.04 / 1.77 | 144 | 34 | 81.5 | 5.04 | 2.29 | **1.29** |
| 2026 | 4.24 / 2.62 | 64 | 20 | 108.6 | 2.10 | 1.86 | **0.71** |

**It FAILS in 3 of 7 years** (2021, 2022, 2026), badly in 2022 (0.26).
⭐ **And the huge PFs sit exactly where losses are ABSENT**: 2020 (15
losers / 15.8 pts -> 29.54), 2023 (4 / 11.4 -> 20.75), 2024 (15 / 14.3 ->
40.56); the years with real losses read 1.47, 2.10, 3.04.

### 3. Pooling and the null

| scope | base | cell | PF ratio | avg ratio |
|---|---|---|--:|--:|
| all years | 10,990 / 3.99 / +2.06% | 681 / **6.62** / +2.61% | 1.66 | 1.27 |
| ex-2020 | 9,205 / 3.57 / +1.93% | 524 / 5.42 / +2.54% | 1.52 | 1.31 |
| **ex-2020/23/24** | 6,509 / 3.75 / +1.95% | 318 / **2.96** / +1.69% | **0.79** | **0.87** |

Random-subsample null:

| scope | cell PF | null95(PF) | pctile | cell avg% | null95(avg) | pctile |
|---|--:|---|--:|--:|---|--:|
| all years | 6.62 | [3.11, 5.32] | **99.9th** | 2.61 | [1.77, 2.34] | **100th** |
| ex-2020/23/24 | 2.96 | [2.57, 5.85] | **10.8th** | 1.69 | [1.51, 2.37] | **12.2th** |

### ⭐⭐ THE METHODOLOGICAL LESSON — the null test has a BLIND SPOT

**The random-subsample null draws from the POOLED population across all
years. It tests exchangeability, NOT stability.** A cell whose losses
happen to be absent in a few particular years clears it easily while being
a temporal artifact — which is exactly what happens here (99.9th pctile
pooled, 10.8th once three years are removed). Trip counts are spread evenly
across years (157/60/50/58/148/144/64); what varies wildly is the LOSS
profile inside the cell, and pooling hides that entirely.

⭐ **RULE: the null test and the per-year ratio table are COMPLEMENTS, not
substitutes.** The null catches "small cell, lucky draw"; the per-year
table catches "concentrated in good years". Report BOTH. (Every earlier
null in S43t/S43v/S43w was paired with a per-year table — this is the first
case where they DISAGREE, and the per-year table is right.)

⚠ Dropping the three best years is deliberately conservative and biases
downward, so 0.79 is not the honest expectation either. **The fair summary
is the per-year record: wins 4, fails 3, worst 0.26, with the aggregate
carried by PF explosions where losses were absent.**

### 4. ⭐⭐ THE HALT CONTROL (user: "make sure these datapoints aren't due to halts")

`gap_adj_*` EXCLUDES classified volatility halts (`Intraday.fs:628` — a
tradeless run counts as a halt iff run >= 58s, pre-hole 5m range >= 4%, and
pre-hole adjusted 1m gap < 4). So `gap_1200 - gap_adj_1200` = the halt
seconds inside the window. **The user's suspicion was right and it is most
of the effect.**

**gap_adj_1200 on the g60 universe** (halts excluded):

| band | n | tkds | losers | PF | avg% | 2022 | 2026 |
|---|--:|--:|--:|--:|--:|---|---|
| <5 | 3,250 | 468 | 545 | **6.10** | +2.75 | 7.3 | 9.3 |
| [5,15) | 1,289 | 232 | 280 | 5.26 | +2.28 | inf | 21.5 |
| [15,50) | 2,318 | 404 | 548 | 2.45 | +1.38 | 1.3 | 2.4 |
| [50,100) | 1,644 | 298 | 350 | 3.91 | +1.99 | 51.2 | 2.3 |
| [100,200) | 1,308 | 263 | 294 | 3.08 | +1.67 | 3.2 | 3.0 |
| [200,300) | 597 | 148 | 156 | 3.09 | +1.47 | 14.3 | 4.7 |
| [300,600) | 548 | 140 | 115 | **4.84** | +2.16 | **1.5** | **1.6** |
| >=600 | 36 | 9 | 9 | 3.39 | +2.43 | inf | inf |

⭐ **The high-gap cell WEAKENS once halts are removed: [300,600) goes 6.62
-> 4.84**, while the LOW end STRENGTHENS (`<5`: 5.64 -> 6.10). The
adjusted axis is cleaner: low gap good, high gap ordinary.

**Decomposition inside `gap_1200 [300,600)`:**

| group | n | % | tkds | losers | PF | avg% |
|---|--:|--:|--:|--:|--:|--:|
| no halt (pure sparseness) | 548 | 80.5 | 140 | 115 | **4.84** | +2.16 |
| **MOSTLY HALT (>=150s)** | 133 | 19.5 | **17** | 13 | **66.61** | +4.51 |

**19.5% of the cell is halt-driven and carries PF 66.6 on 17 ticker-days
— that is what inflated 4.84 into 6.62.**

**And halt-gap is spectacular across the WHOLE g60 book, not just here:**

| group | n | % | losers | PF | avg% |
|---|--:|--:|--:|--:|--:|
| no halt seconds | 10,725 | 97.6 | 2,273 | 3.84 | +2.00 |
| <150s halt | 60 | 0.5 | 10 | 47.45 | +3.76 |
| **>=150s halt** | 205 | 1.9 | 14 | **108.23** | +4.79 |

⭐⭐ **BUT IT IS S-TIER A REDISCOVERED — 179 of the 205 (87%) already ARE
S-tier A:**

| group | n | tkds | losers | PF | avg% | med ssh |
|---|--:|--:|--:|---|--:|--:|
| both (halt-gap AND S-tier A) | 179 | 25 | 5 | **280.21** | +5.05 | 691s |
| halt-gap ONLY | 26 | 5 | 9 | 14.07 | +2.97 | 663s |
| S-tier A ONLY | 50 | 12 | 9 | 47.98 | +4.29 | 1,145s |
| neither | 10,735 | 1,676 | 2,274 | 3.84 | +2.00 | -1 |

Median `secs_since_halt` = 691s (~11.5m), squarely inside S-tier A's
[2,20m) window. **The halt-gap lens adds only 26 genuinely new trips.**

⇒ The "tape just woke up" story was **two things wearing one label**: a
halt-reopen population we ALREADY trade (S-tier A), plus a residual
sparseness population that fails per-year 3/7.

### VERDICT

❌ **REJECTED.** `gap_1200 in [300,600)` on g60 is not a robust cell — and ~20% of it was HALT REOPEN (§4), 87% of which is S-tier A we already trade. Its
S43w appearance (5.04 / 7.52 in the `[200,600)` band) was the pooled view;
the per-year record does not support it. ⭐ Compare `rp_vol` (S43w), which
passed BOTH tests — ratio 2.19 -> 2.10 when the artifact years came out —
and remains the volume-family keeper.


## S43ab — ⭐⭐ `gap_adj_1200 < 15` on g60: THE BEST SIZING TIER FOUND (2026-08-04)

User: "should we consider the gap_1200 < 15 cells as a sizeup candidate on
the g60 universe? You said they are mostly redundant, but that isn't really
the case from what I can see." **The user is right and my S43w wording was
wrong.**

⚠ **THE CORRECTION.** S43w said `gap_1200 < 5` is "fully subsumed by g60".
That was a statement about **SET CONTAINMENT** — every `gap_1200 < 5` trip
already passes `gap_60 < 4` (3,106 trips in both columns). It is NOT a
claim that `gap_1200` carries no information WITHIN g60, and the table
plainly shows it does. Two different claims, run together.

### 1. Per-year — `gap_adj_1200 < 15` on g60 (the recommended form)

| yr | base PF / avg% | n | tkds | losers | cell PF | PF ratio | cell avg% | **avg ratio** |
|---|---|--:|--:|--:|--:|--:|--:|--:|
| 2020 | 8.40 / 2.73 | 713 | 107 | 138 | 7.94 | 0.95 | +2.91 | **1.07** |
| 2021 | 4.64 / 1.78 | 1,130 | 173 | 233 | 4.35 | 0.94 | +1.92 | **1.08** |
| 2022 | 4.03 / 1.87 | 132 | 26 | 13 | 12.14 | 3.01 | +2.54 | **1.36** |
| 2023 | 3.03 / 1.58 | 182 | 29 | 22 | 9.88 | 3.26 | +3.00 | **1.90** |
| 2024 | 3.30 / 2.06 | 708 | 105 | 128 | 5.51 | 1.67 | +2.75 | **1.34** |
| 2025 | 3.04 / 1.77 | 1,097 | 158 | 217 | 4.48 | 1.47 | +2.40 | **1.36** |
| 2026 | 4.24 / 2.62 | 577 | 75 | 74 | 11.20 | 2.64 | +3.74 | **1.43** |

⭐⭐ **EXPECTANCY BEATS BASELINE IN 7 OF 7 YEARS**, and **every year
carries real losses** (138/233/13/22/128/217/74) — no zero-loss inflation
anywhere. ⚠ PF ratio dips slightly below 1 in 2020/2021 (0.95/0.94) while
expectancy is above 1 — a mild PF-vs-avg divergence, but both near parity
in those years and strongly positive in the other five.

### 2. Pooled, and the artifact-year test

| scope | cut | n | PF | avg% |
|---|---|--:|--:|--:|
| all yrs | baseline | 10,990 | 3.99 | +2.06 |
| all yrs | `gap_1200<15` | 4,327 | 5.42 | +2.49 |
| all yrs | **`gap_adj_1200<15`** | **4,539** | **5.86** | **+2.62** |
| all yrs | `gap_1200<5` | 3,106 | 5.64 | +2.61 |
| ex-20&22 | baseline | 8,666 | 3.55 | +1.94 |
| ex-20&22 | `gap_1200<15` | 3,518 | 5.04 | +2.42 |
| ex-20&22 | **`gap_adj_1200<15`** | **3,694** | **5.48** | **+2.56** |
| ex-20&22 | `gap_1200<5` | 2,548 | 4.91 | +2.46 |

⭐ **THE RATIOS IMPROVE WHEN THE ARTIFACT YEARS COME OUT** — 1.47 -> 1.54
on PF, 1.27 -> 1.32 on expectancy. That is the opposite of the S43aa
wake-up cell (1.66 -> 0.79) and better even than `rp_vol` (2.19 -> 2.10).

### 3. Null test (both scopes, both axes)

| scope | cut | n | PF | null95(PF) | pctile | avg% | null95(avg) | pctile |
|---|---|--:|--:|---|--:|--:|---|--:|
| all | `gap_1200<15` | 4,327 | 5.42 | [3.68, 4.35] | **100th** | 2.49 | [1.97, 2.16] | **100th** |
| all | `gap_adj_1200<15` | 4,539 | 5.86 | [3.68, 4.35] | **100th** | 2.62 | [1.98, 2.15] | **100th** |
| ex-20&22 | `gap_1200<15` | 3,518 | 5.04 | [3.24, 3.90] | **100th** | 2.42 | [1.84, 2.04] | **100th** |
| ex-20&22 | `gap_adj_1200<15` | 3,694 | 5.48 | [3.26, 3.88] | **100th** | 2.56 | [1.84, 2.04] | **100th** |

### ⭐ VERDICT — ADOPT AS THE TOP SIZING TIER

1. ✅ **7/7 years on expectancy**, real losses every year, ratios that
   IMPROVE ex-artifact-years, 100th-percentile nulls on both axes in both
   scopes. It passes every test in the battery — including the per-year
   test that killed S43aa.
2. ⭐ **Use the ADJUSTED form.** `gap_adj_1200 < 15` DOMINATES
   `gap_1200 < 15` — **more trips AND higher PF** (4,539 @ 5.86 vs 4,327 @
   5.42). Consistent with S43aa: removing halt holes sharpens the low-gap
   end (`<5`: 5.64 -> 6.10) because a halt is not tape sparseness.
3. ⭐ **SIZE: 4,539 of 10,990 = 41% of the g60 book.** Unlike every other
   sizing lever found today (`rp_vol` 1,778; the rp x gap conjunction 396;
   S-tier A 229), this one is big enough to matter for portfolio
   construction rather than being a rare corner.
4. Ranking of the sizing levers by robustness: **`gap_adj_1200<15` (7/7
   years) > `rp_vol>=0.8` (6/7, fails 2023) > `chg_1d<0` (survives nulls
   but is a sign-flipping interaction) > `speed` / `z_20m` (invert with
   regime).**


## S43ac — ⭐⭐ THE 8-STATE SIZING MATRIX (user: book x gap_adj x deep flush) (2026-08-04)

User: "these 4 states in the book are what is going to determine sizing.
The only thing I'd like to throw in is deep flush (speed < -6%)."

Three binary dimensions on the g60 universe ($1+, v27):
`inbook` = `votes>=1 OR S-tier A` · `lowgap` = `gap_adj_1200 < 15` ·
`deep` = `signal_vwap/vwap_60_prev - 1 < -6%`.

### THE MATRIX

| state | n | tkds | losers | PF | win% | avg% | net pts |
|---|--:|--:|--:|--:|--:|--:|--:|
| **BOOK · gap<15 · DEEP** | 567 | 125 | 116 | **8.64** | 79.5 | **+4.25** | 2,411 |
| **BOOK · gap<15 · shal** | 2,879 | 476 | 462 | **6.67** | 84.0 | +2.68 | 7,726 |
| **BOOK · gap>=15 · DEEP** | 790 | 190 | 134 | **4.26** | 83.0 | +2.82 | 2,229 |
| **BOOK · gap>=15 · shal** | 3,540 | 721 | 759 | **4.15** | 78.6 | +1.86 | 6,601 |
| out · gap<15 · shal | 1,039 | 197 | 233 | 3.42 | 77.6 | +1.63 | 1,691 |
| out · gap>=15 · shal | 1,961 | 418 | 531 | 1.87 | 72.9 | +0.90 | 1,757 |
| out · gap<15 · DEEP | 54 | 15 | 14 | 1.75 | 74.1 | +0.90 | 48 |
| out · gap>=15 · DEEP | 160 | 55 | 48 | 1.63 | 70.0 | +1.34 | 214 |

**Clean monotone ladder inside the book: 8.64 / 6.67 / 4.26 / 4.15.**
⭐ **The GAP dimension DOMINATES the SPEED dimension** — `gap<15 shallow`
(6.67) beats `gap>=15 DEEP` (4.26). Size on gap first.

⚠ **DEEP INVERTS OUTSIDE THE BOOK**: 1.75 vs 3.42 at low gap, 1.63 vs 1.87
at high gap. Operationally irrelevant (we only trade book states) but it
confirms speed is a CONDITIONAL feature, never a standalone one — the S42b
contrast grammar and S43h ("speed = a regime split, not a filter").

### ⚠ PER-YEAR ON THE SPEED DIMENSION (deep/shallow expectancy ratio)

| yr | low-gap ratio | n(deep) | high-gap ratio | n(deep) |
|---|--:|--:|--:|--:|
| 2020 | 1.46 | 65 | 1.36 | 97 |
| 2021 | 1.54 | 95 | 3.16 | 69 |
| **2022** | **— (0 trips)** | 0 | **0.26** | 32 |
| 2023 | 2.26 | 23 | **0.77** | 97 |
| 2024 | 1.65 | 114 | 2.05 | 177 |
| 2025 | 1.17 | 141 | 1.69 | 197 |
| 2026 | 1.35 | 129 | 1.42 | 121 |

⭐ **Deep is RELIABLE where the tape is clean and UNRELIABLE where it is
not**: 6/6 populated years above 1.17 in the low-gap stratum, but it FAILS
2022 (0.26) and 2023 (0.77) at high gap. ⚠ **2022 has ZERO low-gap deep
trips** — the best state simply does not occur in a bear market. That is
self-limiting (we cannot be hurt by a state that never fires) but it also
means we have NO evidence for that state in a bear regime.

### ⭐ SIZING RECOMMENDATION

| tier | state | n | tkds | PF | avg% | confidence |
|---|---|--:|--:|--:|--:|---|
| **A (max)** | BOOK · gap<15 · DEEP | 567 | 125 | 8.64 | +4.25 | 6/6 yrs, but absent in 2022 |
| **B** | BOOK · gap<15 · shal | 2,879 | 476 | 6.67 | +2.68 | gap dim is 7/7 (S43ab) |
| **C** | BOOK · gap>=15 · DEEP | 790 | 190 | 4.26 | +2.82 | ⚠ deep fails 2022/2023 here |
| **D (min)** | BOOK · gap>=15 · shal | 3,540 | 721 | 4.15 | +1.86 | the base state |

⚠ **Tier C is the weak one** — its PF edge over D is only 4.26 vs 4.15
(+3%), and the speed dimension fails 2 of 7 years in that stratum. Its
expectancy edge is better (+2.82 vs +1.86) but that is the axis that
inverts. **Consider collapsing C into D** and sizing on the gap dimension
alone unless the low-gap condition also holds.

⭐ The robust core is the GAP split (S43ab: 7/7 years, ratios improve
ex-artifact). Speed is a MODIFIER on top of it, valid only in the low-gap
stratum.


## S43ad — THE FULL 8-STATE SIZING TABLE incl. the vote=0 states (2026-08-04)

User: "the vote=0 state (non-book) is worth trading, just with size
proportional to its PF." **Agreed for 2 of the 4 out-states; the other 2
should be dropped — evidence below.**

### THE TABLE — gross and COST-ADJUSTED (S43x ladder)

| tier | state | n | tkds | PF gross | avg gross | PF @$25k | avg @$25k | PF @$50k | avg @$50k |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|
| **A** | BOOK · gap<15 · DEEP | 567 | 125 | **8.64** | +4.25 | 7.25 | +3.92 | 6.73 | +3.78 |
| **B** | BOOK · gap<15 · shal | 2,879 | 476 | **6.67** | +2.68 | 5.45 | +2.35 | 4.99 | +2.21 |
| **C** | BOOK · gap>=15 · DEEP | 790 | 190 | **4.26** | +2.82 | 3.70 | +2.49 | 3.47 | +2.35 |
| **D** | BOOK · gap>=15 · shal | 3,540 | 721 | **4.15** | +1.86 | 3.30 | +1.53 | 2.98 | +1.39 |
| **E** | out · gap<15 · shal | 1,039 | 197 | **3.42** | +1.63 | 2.72 | +1.30 | 2.47 | +1.16 |
| **F** | out · gap>=15 · shal | 1,961 | 418 | **1.87** | +0.90 | 1.50 | +0.57 | 1.37 | +0.43 |
| ❌ G | out · gap>=15 · DEEP | 160 | 55 | 1.63 | +1.34 | 1.45 | +1.01 | 1.38 | +0.87 |
| ❌ H | out · gap<15 · DEEP | 54 | 15 | 1.75 | +0.90 | 1.44 | +0.57 | 1.32 | +0.43 |

**All 8 survive costs on a pooled basis even at $50k** — but pooling is
exactly what S43aa warned about. The per-year table decides.

### ⚠ PER-YEAR ON THE OUT-STATES (gross avg% per trip)

| state | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | verdict |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| **E** out·gap<15·shal | 2.38 | 2.09 | 1.34 | 0.68 | **-0.23** | 1.36 | 2.05 | ✅ one negative yr |
| **F** out·gap>=15·shal | 0.85 | 1.60 | 1.27 | **0.11** | 0.95 | 0.67 | 0.67 | ✅ thin but never negative |
| ❌ **G** out·gap>=15·DEEP | 4.27 | 4.42 | 1.54 | 1.83 | 5.49 | **-5.42** | 0.03 | ❌ UNSTABLE |
| ❌ **H** out·gap<15·DEEP | 6.17 | 2.87 | — | — | -0.09 | 2.81 | -0.58 | ❌ 1-2 trips most yrs |

(n per year for G: 29/16/14/3/35/37/26 · for H: 1/14/0/0/2/7/30.)

⭐ **DROP G AND H.** They are precisely where the SPEED INVERSION lives
(S43ac: deep is negative outside the book). G posts **-5.42% on 37 trips
in 2025** and +0.03% in 2026; H has 54 trips TOTAL with two years empty
and two more at 1-2 trips. Their pooled PFs of 1.63/1.75 are noise around
a coin flip. Together they are 214 trips = 6.6% of the out-population, so
dropping them costs almost nothing.

⚠ **E and F are genuinely marginal, not free money.** F runs +0.11% gross
in 2023 — **negative after any cost** — and +0.67-0.95% in four other
years, leaving +0.34-0.62% net at $25k. E has a negative 2024. They are
tradeable at SMALL size; they are not a second book.

### ⭐ PORTFOLIO EFFECT — trading the 6 viable states (mc=3)

| construction | n | PF | avg% | worst yr |
|---|--:|--:|--:|--:|
| THE BOOK only | 3,330 | **3.90** | +1.97 | 2.78 |
| THE BOOK + gap_adj<15 | 1,407 | **5.10** | +2.43 | 3.26 |
| **all 6 viable states** | **4,550** | **3.15** | +1.65 | **2.03** |

Adding E and F buys **+37% trips (3,330 -> 4,550)** for **-19% PF (3.90 ->
3.15)** and **-16% per-trip return**, and lowers the worst year from 2.78
to **2.03 (2023)**. That is the trade: more slots, thinner average.

### ⭐ SIZING RECOMMENDATION

Size ∝ PF is the user's rule; note **expectancy, not PF, is what
compounds** — the two disagree at C vs D (PF 4.26/4.15 nearly equal, avg
+2.82/+1.86 far apart) and at F vs G. Suggested relative units using the
$25k cost-adjusted expectancy:

| tier | state | rel. size | rationale |
|---|---|--:|---|
| A | BOOK·gap<15·DEEP | **3.0** | best cell; ⚠ absent in 2022 |
| B | BOOK·gap<15·shal | **2.0** | the workhorse, gap dim is 7/7 |
| C | BOOK·gap>=15·DEEP | **1.5** | ⚠ deep fails 2022/23 here |
| D | BOOK·gap>=15·shal | **1.0** | base unit |
| E | out·gap<15·shal | **0.6** | one negative year |
| F | out·gap>=15·shal | **0.3** | negative after cost in 2023 |
| G, H | out · DEEP | **0** | ❌ do not trade |

⚠ The out-states raise slot contention: at mc=3 they will displace book
trades. E and F should be taken only when no A-D signal is competing for
the slot, otherwise the +37% trips comes partly at the expense of the
better tiers.


## S43ae — ⭐⭐ FINAL SIZING SPEC: 6 tiers, expectancy-weighted (user decision) (2026-08-04)

**USER DECISION: drop G and H; size by EXPECTANCY.**

### THE SPEC

Universe `g60` (`gap_60 < 4`), `$1+`, SPEC v2.6, `v27_reference`.
Three binary dimensions:

```
inbook = votes >= 1 of 6   OR   S-tier A (ht=1 AND ssh in [120,1200))
lowgap = gap_adj_1200 < 15                    <- halt-ADJUSTED, S43ab
deep   = signal_vwap/vwap_60_prev - 1 < -6%
```

| tier | state | n | tkds | PF net | **exp net** | sd | exp/sd | worst | p05 | **SIZE** |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| **A** | BOOK · gap<15 · DEEP | 567 | 125 | 7.25 | **+3.92** | 5.47 | 0.717 | -14.2 | -4.29 | **2.56** |
| **B** | BOOK · gap<15 · shal | 2,879 | 476 | 5.45 | **+2.35** | 3.34 | 0.706 | -14.5 | -3.62 | **1.53** |
| **C** | BOOK · gap>=15 · DEEP | 790 | 190 | 3.70 | **+2.49** | 4.96 | 0.502 | **-28.4** | **-7.26** | **1.62** |
| **D** | BOOK · gap>=15 · shal | 3,540 | 721 | 3.30 | **+1.53** | 3.33 | 0.461 | -30.9 | -4.04 | **1.00** |
| **E** | out · gap<15 · shal | 1,039 | 197 | 2.72 | **+1.30** | 3.11 | 0.417 | -13.2 | -4.79 | **0.85** |
| **F** | out · gap>=15 · shal | 1,961 | 418 | 1.50 | **+0.57** | 4.04 | **0.140** | **-38.8** | -6.40 | **0.37** |
| ❌ | out · DEEP (either gap) | 214 | 70 | — | — | — | — | — | — | **0** |

`exp net` = per-trip return after the S43x $25k cost of 0.33%. SIZE =
expectancy relative to tier D. mc=3 portfolio over the 6 traded tiers:
**4,550 trips @ PF 3.15 / +1.65%, every year positive, worst 2.03.**

### ⚠ TWO THINGS EXPECTANCY-WEIGHTING DOES NOT SEE

**1. The deep premium is largely a VARIANCE premium.** A's expectancy is
**1.67x** B's — but its expectancy-per-unit-risk is only **1.02x**
(0.717 vs 0.706). The extra return is bought with proportionally more
dispersion, so expectancy weighting gives A 1.67x B's size where
risk-adjusted weighting would give it 1.02x. If position sizing is meant
to equalise RISK rather than maximise expected return, A and B belong at
the same size.

**2. Tier C is the weakest link in the scheme.** Expectancy ranks C ABOVE
B (1.62 vs 1.53), but C has:
- a **-28.4% worst trip and -7.26% p05** vs B's -14.5% / -3.62%;
- the speed dimension **failing 2022 (0.26) and 2023 (0.77)** in that
  stratum (S43ac);
- and only 3% more PF than D (3.70 vs 3.30).

So C gets the second-largest weight while being the only tier with BOTH a
fat left tail AND documented regime failure. **Judgment override
recommended: cap C at or below B (~1.3-1.5).**

⚠ **Tier F carries the book's worst tail** (-38.8% worst, exp/sd **0.14**)
on the thinnest edge (+0.57%). Its 0.37 weight is already small; the
risk-adjusted weight would be 0.30. Do not let it grow.

### ⭐ SLOT CONTENTION

At mc=3 the out-tiers (E, F) will displace book trades. **E and F should
yield to any competing A-D signal** — otherwise the +37% trips they add
(3,330 -> 4,550) comes partly out of the better tiers, and the measured
portfolio PF of 3.15 will not be achieved.


## S43af — ⚠ RETRACTION: slot "priority" is WRONG, and mc=3 is a PYRAMIDING book (2026-08-04)

User: "How could that work? At most we could increase the position sizes
if the stock continues falling... can't you label the trips, merge them,
and check if the earlier positions are taking up slots for the later ones?"

**Both points land. My S43ae advice ("E and F must yield to competing A-D
signals") was unimplementable AND wrong. Retracted.**

⚠ **Unimplementable**: at the moment an E/F signal fires you cannot know
whether an A-D signal will arrive later. You cannot hold a slot for a
hypothetical.

### 1. MEASURED DISPLACEMENT (tier-labelled merged replay, mc=3)

| tier | A-D-only book | merged 6-tier | displaced |
|---|--:|--:|--:|
| A | 203 | 196 | **-7** |
| B | 1,161 | 1,098 | **-63** |
| C | 286 | 275 | **-11** |
| D | 1,680 | 1,596 | **-84** |
| **total** | **3,330** | **3,165** | **-165 (5.0%)** |

E/F contribute **1,385** taken trips (E=454, F=931). Of **4,611** blocked
A-D signals, only **414 (9.0%)** had an E/F position holding a slot — the
other **91% is A-D blocking A-D**, inherent to mc=3 and nothing to do with
the out-tiers.

### 2. ⭐⭐ AND PRIORITY MAKES IT WORSE

Reserving slots is the live-implementable form of "yield":

| construction | n | PF | avg% | **net pts** | A-D taken |
|---|--:|--:|--:|--:|--:|
| A-D only (book) | 3,330 | 3.901 | +1.97 | 6,551 | 3,330 |
| **6-tier, FIRST-COME** | **4,550** | 3.147 | +1.65 | **7,510** | 3,165 |
| 6-tier, 1 slot reserved A-D | 4,232 | 3.246 | +1.70 | 7,215 | 3,223 |
| 6-tier, 2 slots reserved A-D | 3,827 | 3.463 | +1.80 | 6,884 | 3,278 |

**Reserving recovers A-D trips while LOSING money** (7,510 -> 7,215 ->
6,884). E/F average ~+1.03%/trip, so holding a slot empty against a signal
that may never arrive costs more than the upgrade is worth.
⭐ **FIRST-COME-FIRST-SERVED IS OPTIMAL.** No priority logic needed.

⚠ Note the shape: **PF RISES (3.147 -> 3.463) while NET FALLS.** Fourth
instance today of the PF-vs-net divergence (passive entry S43y, the
Shannon/gap stack S43t, the chg_1d bands S43v). **Always decide on NET.**

### 3. ⭐⭐⭐ mc=3 IS A PYRAMIDING BOOK, NOT A DIVERSIFIED ONE

| entry type | n | % |
|---|--:|--:|
| **pyramid ADD into a name already held** | **2,722** | **59.8%** |
| alongside a DIFFERENT symbol | 43 | **0.9%** |
| opened flat | 1,785 | 39.2% |

⭐ **60% of taken entries are adds into a position we already hold**, as it
makes another new 20-minute low — exactly the user's "increase position
size if the stock continues falling". **Genuine cross-name contention is
0.9%.**

**Two consequences:**

1. The 165 "displaced" A-D trips are NOT lost opportunities in other names
   — they are lost **PYRAMID DEPTH in the same name**. That is a much
   smaller loss than the phrase "displacement" implies.
2. ⚠ **RISK: the sizing tiers STACK WITHIN A NAME.** At mc=3 we are
   usually 2-3 positions deep in ONE stock, not spread across three. A
   tier-A add on top of a tier-A position is 2x concentration in a single
   microcap, not diversification. **The S43ae weights are per-trip; the
   per-NAME exposure is up to 3x that.** Position limits should be set on
   the aggregate name exposure, not the per-entry size.


## S43ag — ⭐⭐ DECISION: DROP E/F. Capacity expands through mc, not marginal tiers (2026-08-04)

**USER DECISION:** *"is the increase in net profit only 14% for a 36%
increase in trips? The out book isn't worth trading, we'd be better off
concentrating on our best trades. Marginal trades are something you take
only when your account is so large that you're opportunity-constrained."*

**Adopted, and the data says it is not even close.**

### The trade E/F offered

| | n | PF | avg% | net pts |
|---|--:|--:|--:|--:|
| A-D book only | 3,330 | **3.901** | +1.97 | 6,551 |
| + E/F (6-tier) | 4,550 | 3.147 | +1.65 | 7,510 |
| **delta** | **+36.6%** | **-19%** | **-16%** | **+14.6%** |

### ⭐⭐ THE CAPACITY LADDER — A-D BOOK ALONE

| mc | n | trades/yr | PF | avg% | net pts | % of attribution |
|---:|--:|--:|--:|--:|--:|--:|
| 1 | 1,318 | 188 | 3.698 | +1.85 | 2,443 | 16.9% |
| 2 | 2,420 | 346 | 3.782 | +1.91 | 4,617 | 31.1% |
| **3** | **3,330** | **476** | **3.901** | **+1.97** | **6,551** | 42.8% |
| 4 | 4,101 | 586 | 4.066 | +2.03 | 8,333 | 52.7% |
| 5 | 4,758 | 680 | 4.197 | +2.09 | 9,927 | 61.2% |
| 6 | 5,295 | 756 | 4.291 | +2.13 | 11,253 | 68.1% |
| 8 | 6,131 | 876 | 4.516 | +2.21 | 13,524 | 78.8% |

⭐⭐ **PF RISES WITH mc (3.698 -> 4.516) and so does per-trip return.**
That is the OPPOSITE of normal concurrency compression, and it follows
from S43af's pyramiding structure: the trips added by raising mc are
DEEPER ADDS INTO NAMES STILL FALLING, which are BETTER than average. The
ladder converges upward toward the mc=0 attribution of 5.25.

### ⭐ THE TWO EXPANSION ROUTES ARE NOT COMPARABLE

| route | marginal trip | effect |
|---|---|---|
| **raise mc** | **BETTER** than average | PF and expectancy both RISE |
| add E/F | WORSE than average | PF -19% for +14.6% net |

At mc=8 the A-D book alone does **13,524 net pts @ PF 4.516** — **80% more
net than the 6-tier mc=3 book (7,510) at a HIGHER PF**. E/F is dominated
on every axis. **Capacity expands through mc, never through marginal
tiers.**

⚠ **CAVEAT — the capacity ladder is a CONCENTRATION ladder.** With 59.8%
of entries being same-name adds (S43af), raising mc mostly buys DEPTH in
one name, not more names. mc=8 can mean 8 positions in a single microcap.
**What governs how far up this ladder you can go is the aggregate
per-NAME exposure limit, not the slot count** — and the S43x cost ladder
applies to the AGGREGATE name position, so a 3-deep $50k-per-entry stack
is a $150k position facing the $100k-$250k cost tier (35-53% of edge), not
the $50k tier (24%).

### ⭐ FINAL BOOK

```
BOOK   = g60 (gap_60<4) AND (votes >= 1 of 6  OR  S-tier A)   $1+, SPEC v2.6
SIZING = 4 tiers on {gap_adj_1200 < 15} x {speed < -6%}, expectancy-weighted
         A 2.56 · B 1.53 · C 1.62 (cap at <= B) · D 1.00
EXEC   = cross the entry, rest the exit (S43y)
mc     = 3 today; expand via mc, subject to per-name exposure limits
```


## S43ah — FLOAT: was the chg_1d lookahead a float proxy? NO — and float itself does not survive (2026-08-04, late)

User: "it is weird that future split adjustment would influence intraday
mean reversion. What sort of stocks would have significant future
adjustment? Large caps. Maybe the double-counted split adjustment was a
silent proxy for float."

**Half right — but the half that mattered runs the other way.**

Data: `data/equity/float/float.db`, concept `dei:EntityPublicFloat`
(public float in DOLLARS), ASOF-joined on `known_date <= trade_date` (the
filing date = no lookahead). Coverage **72.5%** of the g60 book, median
float **$22.5M**, median staleness **176 days**.

### 1. adj_ratio vs float — the direction is backwards

| adj_ratio | n | % | med float | med px | med dv60 |
|---|--:|--:|--:|--:|--:|
| **<1 (future FORWARD split)** | 295 | 2.7 | **$244.4M** | $16.59 | $5.25M |
| =1 (no future split) | 6,217 | 56.6 | $22.7M | $4.99 | $1.73M |
| 1-10 (small reverse) | 1,175 | 10.7 | $24.1M | $3.37 | $1.68M |
| 10-100 | 1,960 | 17.8 | $20.6M | $2.88 | $1.44M |
| **>=100 (massive reverse)** | 1,343 | 12.2 | **$16.6M** | $2.90 | $1.38M |

⭐ **Forward-split names ARE large caps** (float $244M vs the book's $22M)
— the user's intuition is correct for `adj_ratio < 1`. **But they are only
2.7% of the book, and the buggy formula MULTIPLIED by adj_ratio**, so
`chg_1d >= 300` selected the HIGH end: reverse splitters (40.7% of the
book) with LOWER float, falling monotonically $24.1M -> $20.6M -> $16.6M.

`corr(ln adj_ratio, ln float) = -0.124` — real but weak.
⇒ **The lookahead was a "WILL REVERSE-SPLIT SOON" detector (distressed
microcap), not a float proxy.** Reverse splits are the listing-compliance
manoeuvre of dying small caps; that is the population it silently picked.

### 2. Float on its own — a U-shape that does NOT survive per-year

| float band | n | tkds | losers | PF | avg% | med px |
|---|--:|--:|--:|--:|--:|--:|
| **<$5M** | 1,348 | 172 | 233 | **6.67** | +2.84 | $3.75 |
| $5-15M | 1,987 | 311 | 416 | 4.45 | +2.21 | $3.92 |
| $15-40M | 1,670 | 267 | 396 | 3.30 | +1.77 | $3.75 |
| $40-100M | 1,001 | 170 | 178 | 3.19 | +1.77 | $3.68 |
| $100-300M | 1,130 | 167 | 230 | 3.78 | +1.89 | $5.73 |
| **>=$300M** | 828 | 150 | 183 | **4.96** | +2.11 | $6.61 |
| no filing | 3,026 | 465 | 661 | 3.51 | +1.93 | $4.03 |

**Per-year expectancy ratio vs the g60 baseline:**

| cell | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | record |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| float <$5M | **0.84** | 1.50 | **0.13** | **0.79** | 1.77 | 1.50 | 1.41 | ❌ 4/7 |
| float >=$300M | 1.06 | 0.94 | 1.11 | **0.35** | **0.79** | 1.14 | **0.77** | ❌ 3/7 |

⭐ **THE SMALL-FLOAT CELL IS A REGIME ARTIFACT.** The universe's float
composition shifted hard:

| yr | coverage | med float | **% under $5M** |
|---|--:|--:|--:|
| 2020 | 86% | $26.7M | 10.4 |
| 2021 | 76% | $29.0M | 3.0 |
| 2022 | 73% | $73.2M | 4.3 |
| 2023 | 57% | $47.0M | 10.4 |
| 2024 | 64% | $20.6M | 17.9 |
| 2025 | 75% | $9.0M | **34.8** |
| 2026 | 63% | $9.1M | 25.0 |

**79% of the `<$5M` cell's trips fall in 2024-2026**, exactly when such
names became common — and it FAILS in the years when they were rare
(2022 ratio 0.13). Same shape as the S43aa wake-up cell: a pooled table
that looks strong because a regime, not a feature, produced it.

⚠ Also note **median staleness 176 days**: `EntityPublicFloat` is a 10-K
cover-page figure, so the DOLLAR float is measured at the last filing. On
a name that has since run 300%, the current dollar float is far larger
than recorded — so this feature partly encodes "was small at last filing",
i.e. past appreciation, not present size.

### VERDICT

❌ **Float is not usable on this book.** Neither tail survives per-year;
the pooled U-shape is regime composition plus noise.
❌ **And it does not rehabilitate the chg_1d lookahead** — that selected
future reverse-splitters, which is only weakly (and negatively) related to
float. The bug remains a bug with no salvageable signal behind it.
⭐ The one durable fact: **forward-split (large-cap) names are 2.7% of the
book and carry 11x the median float** — if a size dimension is ever
wanted, `adj_ratio < 1` is a clean post-hoc label for it, though it is
NOT live-knowable and cannot be traded.


## S43ai — MaxMaMeta + eff AT / FROM the arming high: built, parity-clean, NO usable cell yet (2026-08-04, late)

User: "extend the MaxMa structure so it holds metadata values in addition
to the maximums... store the current eff_20m and eff_10m. That will allow
us to record the eff at the arming highs."

### 1. THE MECHANISM (built, committed)

`MaxMaMeta<'M>` in `RollingMa.fs` — the MaxMa monotonic deque holding
`(value, barIdx, meta)`, exposing `State` and **`StateMeta`** = the payload
of the bar that SET the current max.

Two deliberate deviations from the literal request, both for safety:
- **A sibling type, not a retrofit of `MaxMa`.** Making MaxMa generic would
  change the signature at all 15 call sites; a slip there alters the signal
  path invisibly.
- **Run PARALLEL to `entryMax`, not replacing it.** Parity then holds BY
  CONSTRUCTION. Cost is one extra amortized-O(1) deque.

⚠ **Three ordering traps, all resolved deliberately:**
1. **Push position.** The slot chain (`slotLag`/`slotAbsSum`) updates AFTER
   `max1200.Push`. Pushing metadata there would store the PREVIOUS slot's
   eff. It is pushed after the slot chain instead, matching signal-time
   semantics. (Position is otherwise irrelevant — one push per bar in bar
   order gives a deque identical to `entryMax`'s.)
2. **Snapshot alignment.** `sMaxMeta` is captured at the same instant as
   `sMax1200`, so it describes the same high `priorEntryMax` reports and
   that `dropToFlow` is computed against.
3. **Ties.** Back-pop is `<=`, so among equal maxima the LATEST bar's
   metadata survives — MaxMa's own convention.

**Three new RECORD-ONLY columns** (`v28_armeff/`):

| column | meaning |
|---|---|
| `arm_hi_eff_20m` / `arm_hi_eff_10m` | eff as of the bar that SET the arming high — the trend INTO the high |
| `eff_hi_flow` | `effSinceHigh.Eff` at the arming bar = the drop segment high -> first low |

⭐ **`eff_hi_flow` closes a gap the user spotted**: the drop segment had
`ols_slope_hi_flow` / `ols_r_hi_flow` recorded but NOT its eff twin, even
though `effSinceHigh` already shares the anchor. It completes the family —
the drop was the only segment measured by OLS alone.

**PARITY EXACT vs `v27_reference`: 37,214 trips, all joined, 0 ret_exit
differences, 0 orphans.**

### 2. WHAT THE FEATURE LOOKS LIKE

g60: cold 26.2% (eff20) / 17.4% (eff10); p10 **0.007**, median **0.191**,
p90 0.406; `corr(arm20, arm10) = 0.45`; ⭐ **`corr(arm_hi_eff_20m,
eff_20m) = 0.004`** — genuinely orthogonal to the signal-time eff, and the
sign flips as it should (median **+0.19** at the high vs **-0.38** at the
signal). The distribution is almost entirely POSITIVE — a 20m high is by
construction the top of a rise — so the feature grades HOW CLEANLY the
run-up got there.

| band | g60 n / PF | g60+vote n / PF |
|---|---|---|
| **<0 (fell into the high)** | 688 / **7.06** | 496 / **8.01** |
| [0,0.10) choppy | 1,583 / 3.81 | 1,099 / 4.16 |
| [0.10,0.20) | 1,944 / 3.55 | 1,353 / 3.81 |
| [0.20,0.30) | 1,676 / 6.14 | 1,274 / 5.91 |
| [0.30,0.40) | 1,356 / 3.62 | 1,063 / 5.22 |
| >=0.40 smooth climb | 861 / 4.50 | 764 / 6.92 |
| COLD | 2,882 / 3.33 | 1,654 / 5.79 |

⚠ The middle zigzags (3.81 / 3.55 / **6.14** / 3.62 on g60) — no clean
structure. ⚠ And COLD is BELOW baseline on g60 (3.33 vs 3.99) but ABOVE it
on g60+vote (5.79 vs 5.19); a relationship that flips between books is
noise, not a warm-up penalty.

### 3. ❌ THE `<0` CELL FAILS PER-YEAR

| yr | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|--:|--:|--:|--:|--:|--:|--:|
| n | 147 | 100 | 55 | 69 | 87 | 138 | 92 |
| losers | 20 | 21 | 5 | 32 | **1** | 17 | 7 |
| cell PF | 9.78 | 2.00 | 18.81 | 1.76 | **52.19** | 18.23 | 8.92 |
| **avg ratio** | **0.99** | **0.54** | 1.37 | **0.48** | 1.76 | 1.90 | **1.00** |

**Beats baseline in only 3 of 7 years**, and the headline PF is the usual
absent-loss artifact — 2024 reads 52.19 on ONE loser and 2022 18.81 on
five, while the years with real loss counts (2021: 21 -> 2.00; 2023: 32 ->
1.76) sit BELOW baseline. Same signature as S43aa.

### 4. ❌ THE eff10 FALLBACK FOR COLD BARS — REJECTED (SCALE MISMATCH)

User: "we could just substitute the arm_hi_eff_10m early in the session."

| source | n | % | **med eff** | **% negative** | PF |
|---|--:|--:|--:|--:|--:|
| native20 | 8,108 | 73.8 | **0.191** | **8.5** | 4.30 |
| fallback10 | 974 | 8.9 | **0.337** | **4.1** | 4.92 |
| still cold | 1,908 | 17.4 | — | — | 2.85 |

⭐ **eff over 20 slots runs systematically HIGHER than over 40** (median
0.337 vs 0.191) — a shorter window gives chop less time to accumulate — so
a fixed threshold means different things depending on which source filled
it, and the negative rate HALVES (8.5% -> 4.1%). It also buys almost
nothing: the `<0` cell goes 688 -> 728 trips, PF 7.06 -> 6.96. And
**eff10's own `<0` cell reads 2.94** vs native eff20's 7.06 — it is not
measuring the same thing.

⚠ **The "accept partial warmth at >= 20 slots" variant has the same defect
in continuous form**: `slotAbsSum` would sum over fewer slots, shrinking
the denominator and inflating eff. If partial warmth is wanted, the honest
fix is to NORMALISE BY THE ACTUAL SLOT COUNT, not to substitute a different
window.

### VERDICT

✅ **The mechanism is built, parity-clean and committed** — `MaxMaMeta` is
reusable for any "feature state at the extreme" question, and
`eff_hi_flow` closes a real gap in the S41e family.
❌ **No usable cell yet.** `arm_hi_eff_20m` is orthogonal to everything
(corr 0.004) which is promising, but its one interesting band fails
per-year, and the cold-bar fallback is a scale mismatch.
⏭ The untested angle: `eff_hi_flow` itself (the drop-segment eff) has not
been broken down at all yet.


## S43aj — partial-window eff + recorded spans, and the no-eff-gate run (2026-08-04, late)

User: "if the feature isn't fully warm we should just get the eff ratio as
it is. But along the ratios we might as well keep records of how many slots
they were computed from." Then: a run with the eff_20m / eff_10m gates OFF.

### 1. The build

`LagMa.Oldest` added (the earliest value still held — `Lagged` once full, the
first push before that). The metadata payload widened to
`(eff20, eff10, span20, span10)`; eff is now formed over WHATEVER span has
accumulated, guarded by `Count >= 2` (at least one slot return) and
positive cur/old/sum. Four columns: `arm_hi_eff_20m/10m` +
**`arm_hi_slots_20m/10m`**.

⚠ Below 20 slots the two measures are IDENTICAL by construction — both
`LagMa` queues hold every value pushed, so `Oldest` and the return sums
coincide. The recorded spans make that visible instead of silent.

**Run:** `v29_noeff/` — `--abs-eff20-lo 0 --abs-eff20-hi 1000
--min-abs-eff-10m 0`, **63,629 trips** vs the gated 37,214 (the eff band was
trimming 41% of the book).

⚠ **A FIRST ATTEMPT PRODUCED ZERO TRIPS**: `--abs-eff20-hi 0` reads as a
CEILING of 0.00, not "off" — the banner prints `off` for that bound only at
positive infinity (`Program.fs:286`). Caught from the BANNER, not the
flags. |eff| <= 1 by construction (numerator = sum of slot log-returns,
denominator = sum of their absolute values), so 1000 is definitively off.

### 2. ⭐⭐ THE SPAN CONFOUND IS SEVERE — AND IT INVERTS

Coverage on g60: **70.0% full 40 slots · 9.0% span 20-39 · 16.2% span 2-19
· 4.8% none.** Partial-warm recovers 25.2% of previously-cold trips.

**But eff drifts UP as the span shrinks** — median `arm_hi_eff_20m`:

| span | median eff |
|---|--:|
| full 40 | **0.196** |
| 20-39 | **0.284** |
| 2-19 | **0.412** |

A shorter window gives chop less time to accumulate in the denominator.
Consequence — the eff→return relationship **INVERTS by span group**:

| span group | n | avg all | avg lo-eff (<0.20) | avg hi-eff (>=0.20) | % hi-eff |
|---|--:|--:|--:|--:|--:|
| full 40 | 12,888 | +1.72 | +1.57 | **+1.88** | 48.8 |
| span 20-39 | 1,664 | +1.88 | **+2.87** | +1.49 | 71.5 |
| span 2-19 | 2,985 | +1.66 | +0.89 | **+1.91** | 75.4 |

⇒ **The raw feature CANNOT be used without conditioning on span.** This is
exactly what recording the spans was for.

### 3. The clean read (full span only, g60) — and why the bands alternate

| band | n | tkds | losers | PF | avg% |
|---|--:|--:|--:|--:|--:|
| **<0** | 1,092 | 153 | 199 | **4.43** | +2.18 |
| [0,.10) | 2,399 | 333 | 594 | 2.34 | +1.46 |
| [.10,.20) | 3,113 | 446 | 767 | 2.33 | +1.43 |
| **[.20,.30)** | 2,553 | 367 | 570 | **4.27** | +1.99 |
| [.30,.40) | 2,239 | 296 | 502 | 2.52 | +1.49 |
| >=.40 | 1,492 | 224 | 285 | **4.49** | +2.28 |

**SPLIT-HALF (H1 2020-22 vs H2 2023-26):**

| band | H1 PF/avg | H2 PF/avg | stable? |
|---|---|---|---|
| **<0** | 4.42 / +2.00 | **4.43 / +2.30** | ✅ |
| [0,.10) | 1.79 / +1.01 | 2.82 / +1.76 | ✗ |
| [.10,.20) | 2.97 / +1.53 | 2.06 / +1.37 | ✗ |
| **[.20,.30)** | 4.18 / +1.72 | **4.32 / +2.18** | ✅ |
| [.30,.40) | 3.82 / +1.75 | 1.90 / +1.22 | ✗ |
| >=.40 | **13.40** / +2.79 | **2.43** / +1.66 | ✗ collapses |

`>=.40`'s pooled 4.49 was entirely H1. But `<0` replicates almost exactly.

### 4. ❌ PER-YEAR KILLS BOTH SURVIVORS

Expectancy ratio vs the full-span g60 baseline:

| band | 2020 | 2021 | 2022 | **2023** | 2024 | 2025 | 2026 | record |
|---|--:|--:|--:|--:|--:|--:|--:|---|
| `<0` | 1.02 | 0.90 | 3.22 | **-0.15** | 1.97 | 2.15 | 0.87 | ❌ 3/7 |
| `[.20,.30)` | 0.59 | 1.24 | 2.17 | **0.54** | 0.76 | 1.73 | 1.15 | ❌ 4/7 |

⚠ The `<0` band has a **NEGATIVE 2023** — 134 trips, 53 losers, PF 0.91.
And 2023 fails for BOTH — the same year that broke `chg_1d` (S43v),
`rp_vol` (S43w) and the wake-up cell (S43aa).

⭐⭐ **METHODOLOGICAL: SPLIT-HALF IS A WEAKER TEST THAN PER-YEAR.** Both
bands PASSED the split-half and FAILED per-year, because each half pools
3-4 years and 2023's failure is diluted by 2024-26. Same blind spot as the
pooled null in S43aa. **Per-year remains the binding test; split-half only
adds an out-of-sample flavour on top of it.**

### VERDICT

✅ The mechanism is complete and correct — partial spans recover 25.2% of
coverage, and recording the span is what exposed the confound.
✅ ⭐ **The span confound is itself the finding**: eff over a short window
is NOT comparable to eff over a long one (0.412 vs 0.196 median), and the
return relationship inverts between them. Any future eff-like feature
measured at a variable-age anchor needs its span recorded.
❌ **No usable cell.** Neither surviving band clears per-year.
⏭ Still untested: `eff_hi_flow` (the drop-segment eff, S43ai), and a
span-NORMALISED eff (dividing by the span rather than conditioning on it).


## S43ak — `eff_hi_flow` (the drop-segment eff): a DURATION PROXY, rejected (2026-08-04, late)

The last untested piece of the S43ai family — eff over the arming high ->
first low segment.

### 1. Census — good coverage, and a STRONG confound

g60, spec book (`v28_armeff`), n = 10,990:

| | value |
|---|--:|
| cold | **4.7%** (vs `arm_hi_eff_20m`'s 26.2% — much better) |
| p10 / median / p90 | -0.721 / **-0.355** / -0.152 |
| median drop span | **760 bars** (~12.7 min) |
| **`corr(eff_hi_flow, drop_bars)`** | **+0.714** |

Entirely NEGATIVE, as a drop must be. ⚠ But the **0.714 correlation with
the drop's own length** is far stronger than the arming-high feature's span
issue — a fast drop is a clean directed move (eff -> -1), a drawn-out one
accumulates chop (eff -> 0). Drop span = `bars_since_high -
bars_since_first_low`, both recorded.

### 2. The raw bands SORT BY DURATION

| band | n | tkds | PF | avg% | **med drop bars** |
|---|--:|--:|--:|--:|--:|
| <-.60 steep clean | 1,892 | 365 | 3.74 | +1.92 | **341** |
| [-.60,-.45) | 1,730 | 288 | 3.40 | +1.78 | **576** |
| [-.45,-.35) | 1,698 | 286 | 4.51 | +2.45 | **759** |
| [-.35,-.25) | 2,105 | 336 | **5.31** | +2.36 | **945** |
| [-.25,-.15) | 2,026 | 319 | 3.26 | +1.88 | **1,120** |
| >=-.15 choppy | 1,019 | 174 | 4.03 | +2.00 | **1,380** |
| cold | 520 | 105 | 4.64 | +1.90 | 46 |

`med_drop_bars` climbs monotonically 341 -> 1,380 across the bands. The
"eff" ordering IS a duration ordering.

### 3. ⭐⭐ WITHIN DROP-SPAN TERCILES THE DIRECTION FLIPS

| drop span | steepest eff | mid | choppiest |
|---|---|---|---|
| short (med 412 bars) | **4.09** / +2.10 | 3.71 / +2.02 | 3.11 / +1.88 |
| medium (783) | 2.77 / +1.40 | **6.89** / +2.72 | 3.61 / +1.95 |
| long (1,218) | 3.91 / +2.17 | 3.44 / +1.97 | **6.96** / +2.43 |

Steepest wins in short drops, the MIDDLE in medium, choppiest in long —
**three strata, three different orderings.** A real feature would point the
same way in each. ⇒ **`eff_hi_flow` carries no information once duration is
held constant.**

### 4. The variable that DID emerge — drop duration — is weak

| drop bars | n | losers | PF | avg% |
|---|--:|--:|--:|--:|
| <200 | 1,018 | 207 | 3.40 | +1.90 |
| 200-500 | 1,979 | 364 | 3.25 | +1.76 |
| 500-900 | 3,867 | 856 | 4.11 | +2.10 |
| 900-1300 | 2,776 | 638 | 4.09 | +2.09 |
| **>=1300** | 1,350 | 232 | **5.53** | +2.47 |

Roughly monotone — a longer, slower slide into the flush fades better than
a fast one. But per-year:

| yr | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|--:|--:|--:|--:|--:|--:|--:|
| ratio | 1.07 | 1.27 | **0.86** | **0.93** | 1.13 | 1.92 | 1.03 |
| losers | 27 | 45 | 35 | 38 | 47 | **22** | 18 |

**5/7, but two are marginal (1.03, 1.07), it fails 2022 and 2023, and the
pooled 5.53 leans on 2025's 46.59 on just 22 losers.** Margins of 3-13% in
most years — not in the class of `gap_adj_1200 < 15` (7/7 at 1.07-1.90).

### VERDICT

❌ **`eff_hi_flow` REJECTED** — a duration proxy (corr 0.714) that carries
nothing once duration is controlled; the direction of its effect is not
even consistent across strata.
❌ **Drop duration itself: too weak to adopt** (5/7 with small margins,
fails 2022 AND 2023).
⭐ **This closes the S43ai/aj family.** All three arming-high measures —
eff INTO the high, eff OUT to the first low, and the partial-span variants
— produced no usable cell. The mechanism (`MaxMaMeta`, recorded spans) is
sound and reusable; the hypothesis that trend smoothness around the arming
high predicts the fade is NOT supported.
⭐ ⚠ **AND THE SAME LESSON A THIRD TIME**: `arm_hi_eff` was confounded by
its measurement span (S43aj), `eff_hi_flow` by the segment's duration
(here). **Any eff/OLS-style ratio taken over a VARIABLE-LENGTH window needs
its length recorded and controlled — the ratio is not comparable across
lengths.**

## ⭐⭐ S43al — the eff_9ema FAMILY + SPEC v2.7: `eff_9ema_10m >= -0.10` BAKED (2026-08-05)

**User design.** An efficiency ratio with the same denominator as `eff_20m`
but a different NUMERATOR: instead of the two-endpoint displacement
`ln(V_last/V_first)`, a **TREND-SIGNED sum of the very same slot returns**

    eff_9ema = SUM s_t * r_t / SUM |r_t|      r_t = ln(V_t / V_{t-1})
    s_t = +1 if V_{t-1} > EMA9(V)_{t-1} else -1

The sign is fixed by the PREVIOUS slot's position against its own 9-slot
EMA — strictly-prior information. It is the P&L of a trend-following
overlay normalised by total path length. Kaufman's eff sees only the two
ends, so a V-shape and a straight slide that finish alike read alike; this
credits every leg that agreed with the prevailing trend and debits every
leg that fought it.

### 1. BUILD

`EmaMa 9` (already existed, alpha = 2/(period+1)) + `SumMa 40` / `SumMa 20`
for the signed sums, pushed in the SAME slot-emission branch as
`slotAbsSum`, from ONE computed `r`. `slotEma9.Push v` sits AFTER the sign
read so the EMA has absorbed `pv` but not `v`. Two record-only columns
`eff_9ema_20m` / `eff_9ema_10m`; warmth guard identical to `eff_20m`.

**PARITY EXACT.** `v30_eff9ema` (eff gates OFF) = 63,629 trips; its
eff-band subset reproduces `v27_reference` at 37,214 with 0 `ret_exit`
diffs — which simultaneously verifies the record-only additions AND the
superset property of the ungated run.

### 2. NOT A RELABELLED `-eff_20m`

The numerator telescopes when `s_t` never flips, giving `eff_9ema = s *
eff_20m` exactly. **Measured: the sign flips in 99.79% of windows** (only
0.21% telescope). `corr(eff_9ema_20m, eff_20m) = -0.336` in the book,
-0.536 ungated. Median +0.215, 10.9% negative. Orthogonal to every
existing lever: `gap_adj_1200` +0.084 · speed -0.019 · `volat_20m` -0.169 ·
`eff_since_flow` -0.202 · `n_eff_ret_20m` -0.097.

### 3. IT DOES NOT REPLACE `eff_20m` — but it is real

Head-to-head, ungated g60+vote book (16,750), at MATCHED trip count:

| cut | n | PF | avg% |
|---|---:|---:|---:|
| baseline, no eff gate | 16,750 | 2.97 | +1.89 |
| **incumbent** \|eff20\| in [.3,.5) & \|eff10\| >= .15 | 9,924 | **4.10** | **+2.31** |
| `e9_20m >= 0.16` (iso-trip) | 10,609 | 3.42 | +2.11 |
| `e9_10m >= 0.30` (iso-trip) | 9,990 | 3.15 | +2.02 |

Random-null at n=9,924: PF 95% [2.87, 3.12], avg% [1.84, 1.94]. Both
variants sit OUTSIDE it — genuine selection, just weaker than Kaufman's.

⚠ These four rows use the ADJUSTED `entry_px >= 1` floor (caught and
corrected mid-session — the project convention is the RAW
`entry_px/adj_ratio >= 1`; the corrected baseline reproduces the recorded
mc=3 book at 3,336 @ 3.899 vs 3,330 @ 3.901). Rows below are all RAW.

### 4. ⭐ CONDITIONING ON e9 CONFIRMS THE INCUMBENT BAND

`|eff_20m|` bands GIVEN `eff_9ema_20m >= 0.20` (g60+vote, no eff gate).
`*` = inside today's band; year columns = expectancy ratio vs the book:

| \|eff_20m\| | n | PF | avg% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| <.10 | 31 | inf (0 losers) | +3.63 | 1.93 | 2.73 | 0.34 | 1.07 | 1.04 | - | - |
| [.10,.20) | 286 | 3.42 | +2.02 | 1.24 | 2.04 | 1.54 | 0.25 | 0.22 | 0.42 | 0.89 |
| [.20,.30) | 1,404 | 3.04 | +1.79 | 0.88 | 0.94 | 1.09 | 1.27 | 0.54 | 0.47 | 0.92 |
| **[.30,.40)\*** | 3,191 | **4.59** | **+2.54** | 1.07 | 1.27 | 0.83 | 1.04 | 1.08 | 1.09 | 1.10 |
| **[.40,.50)\*** | 2,746 | **5.14** | **+2.64** | 1.29 | 1.29 | 1.18 | 1.02 | 0.66 | 1.28 | 1.38 |
| [.50,.60) | 1,132 | 1.89 | +1.24 | 0.43 | 1.38 | **-0.64** | 1.17 | 0.70 | **0.03** | 1.34 |
| [.60,.70) | 227 | 1.64 | +1.03 | 1.16 | 0.45 | **-2.15** | 1.92 | 1.11 | **-0.14** | 1.19 |

⭐ **The `[0.30, 0.50)` band is confirmed and e9 does NOT substitute for
it.** High e9 does not rescue the `>= 0.50` tail (1.89 / 1.64, with 2022 at
-0.64/-2.15 and 2025 at 0.03/-0.14) — **the upper bound is load-bearing.**
Nor does it justify widening down ([.20,.30) reads 3.04 vs the book's
4.10). The two features MULTIPLY: same eff band, e9 high vs low, 4.59 vs
3.13 and 5.14 vs 3.25 — a near-constant ~+45% lift = an orthogonal second
dimension. `eff_20m` is negative in **100.0%** of these trips, so `abs()`
is cosmetic.

### 5. ⭐⭐ THE KNIFE IS ON THE 10m TWIN AT -0.10, NOT THE 20m AT 0

User proposed `eff_9ema_20m >= 0` ("strip the trash"). **The data says the
threshold and the twin are both wrong.**

`eff_9ema_10m` fine bands inside the book:

| band | n | losers | PF | avg% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **<-.10** | **211** | **80** | **1.23** | **+0.35** | -0.13 | 0.33 | 0.95 | 0.42 | -0.10 | -0.25 | 0.65 |
| [-.10,0) | 301 | 52 | **8.06** | +2.64 | 1.16 | 0.90 | 1.95 | 1.34 | 1.48 | 0.24 | 1.05 |
| [0,.10) | 524 | 89 | 5.58 | +2.51 | 0.90 | 1.13 | 1.32 | -0.09 | 1.31 | 0.97 | 0.31 |
| [.10,.20) | 734 | 141 | 5.18 | +2.48 | 1.15 | 0.26 | 1.41 | 1.50 | 1.25 | 1.13 | 0.52 |
| [.20,.30) | 1,057 | 204 | 5.41 | +2.23 | 0.71 | 0.81 | 1.55 | 1.08 | 0.66 | 1.42 | 0.77 |
| [.30,.40) | 1,344 | 282 | 4.12 | +2.37 | 0.95 | 0.90 | 0.93 | 1.53 | 1.03 | 0.96 | 0.76 |
| [.40,.50) | 1,293 | 218 | 7.41 | +2.85 | 0.97 | 1.30 | 1.02 | 1.18 | 1.24 | 1.39 | 1.05 |
| [.50,.65) | 1,725 | 301 | 7.30 | +2.54 | 1.13 | 1.27 | 0.82 | 0.66 | 1.06 | 0.87 | 1.34 |
| >=.65 | 600 | 113 | 3.77 | +2.28 | 1.51 | 1.04 | 0.25 | 0.38 | 0.06 | 0.65 | 1.99 |

**The trash is below -0.10.** `[-.10, 0)` is one of the BEST cells (PF
8.06, 6/7 years) — a `>= 0` knife throws it away. Same on the 20m: its
`[-.05, 0)` band is 270 trips @ PF **6.07** / +2.69%, above the book.

Knives compared, mc=0 on the book (7,789 @ 5.25 / +2.43% / net 18,966):

| gate | n | PF | avg% | net | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `e9_20m >= 0` (proposed) | 7,227 | 5.34 | +2.46 | 17,766 **(-6.3%)** | 1.00 | 1.01 | **0.89** | **0.92** | 1.06 | 1.03 | 1.00 |
| `e9_20m >= -0.10` | 7,666 | 5.28 | +2.45 | 18,747 (-1.2%) | 1.00 | 1.00 | 0.95 | 1.00 | 1.01 | 1.03 | 0.99 |
| **`e9_10m >= -0.10`** | **7,578** | **5.55** | **+2.49** | **18,893 (-0.4%)** | 1.02 | 1.02 | **1.00** | 1.01 | 1.03 | 1.04 | 1.01 |

**At mc=3, which decides it:**

| book | n | PF | avg% | worst year |
|---|---:|---:|---:|---|
| baseline | 3,336 | 3.899 | +1.96 | 2021 = 2.782 |
| `e9_20m >= 0` | 3,115 | **3.846** (down) | +1.96 | 2022 = **2.698** (down) |
| **`e9_10m >= -0.10`** | **3,250** | **4.090** | **+2.01** | 2021 = **2.860** |

⭐ **BAKED AS SPEC v2.7.** First feature this session with a 7/7 per-year
record (every ratio in 1.00-1.04). Removes 211 trips at PF 1.23 / +0.35%
for 0.4% of mc=0 net; mc=3 PF 3.899 -> 4.090, worst year 2.782 -> 2.860.

**Parity chain: `v27_reference` (37,214) -> `v31_reference` (35,778)**,
verified as an exact SUBSET — 35,778 matched, 0 `ret_exit` diffs, 0
orphans.

⚠ **OFF IS -INFINITY, NOT 0.** The bound is NEGATIVE, so `0` is a LIVE
ceiling — the S43aj `--abs-eff20-hi 0` trap in mirror image. Encoded in the
field doc, the flag help, and the banner (`eff9ema10 >= -0.10`). **Verify
from the BANNER.**

## S43am — the ANCHORED eff_9ema twins: REJECTED (2026-08-05)

**User:** "calculate the eff_9ema since the arming high and first low."

**BUILD.** `AnchoredEff` extended in place with `.Eff9Ema` — same
anchor-aligned slots as `.Eff` by construction, so `eff9_since_high/flow`
describe EXACTLY the same path as `eff_since_high/flow`; only the numerator
differs. Internal `EmaMa 9` seeded at the anchor. Spans recorded as
`slots_since_high/flow`. `v32_eff9anch` = 35,778, parity by construction.

### ⚠ THE SPAN CONFOUND IS NOT SOLVED — a claim I wrote into the source and had to retract

I asserted in the code comment that this ratio is "structurally
span-normalised" because it is a weighted mean of +/-1. **The data
refutes it.** `corr(eff9_since_high, slots) = -0.589`; median drifts
**0.771** (10-20 slots) -> 0.477 -> 0.245 -> **0.139** (>= 80). A longer
segment gives price more chances to cross its own EMA, so the signed terms
cancel more. That is the SAME drift magnitude as Kaufman's `.Eff` (-0.771
-> -0.255) reached by a different mechanism. **Bounding to [-1,1] is not
span-normalising.** Comment corrected in `Intraday.fs`.

### THE VOTE WAS CONTAMINATING THE FIRST READ (user catch)

User: *"test this on the g60 universe without the voice family because the
voice family has an esf feature already in it."* Correct — the roster
carries `|eff_since_flow| >= 0.5`, the Kaufman twin at the SAME anchor
(`corr(eff9_since_flow, eff_since_flow) = -0.699`).

⭐ On the vote book `eff9_since_high < 0` read 343 @ PF 13.20 / +2.86% and
survived a span control. **On g60 alone it reads 3.01 / +1.68% — BELOW the
4.27 / +2.13% baseline, with 2026 at -0.91.** Entirely a vote interaction.
**Always strip a roster of any voice sharing the candidate's anchor before
reading the candidate.**

### `eff9_since_flow in [0, .10)` — the one cell, and it dies on the control

On g60 it looked genuinely strong: 1,347 @ **6.78** / +2.51% vs 4.27 /
+2.13%; **survived span control in ALL THREE terciles** (11.15/4.51 ·
5.11/4.07 · 7.31/3.55 — the only anchored feature to do so); cleared the
random null (n=1,347: PF 95% [3.59, 5.11], avg% [1.93, 2.30]); nearly
DISJOINT from the `esf` voice (73 of 1,347 overlap = 5.4%); and per-year
0.99/0.95/**1.19**/**1.82**/1.60/1.10/1.27 — working in 2022 and 2023, the
two killer years. ⚠ But an INTERIOR band with a hole beside it: [.10,.15)
collapses to 2.96, below both neighbours.

**THE ROSTER TEST KILLS IT** (mc=3, g60, $1+ raw, `v32`):

| roster variant | n | PF | avg% | net pts | worst year |
|---|---:|---:|---:|---:|---|
| **V6 baseline** | 3,250 | 4.090 | +2.01 | 6,533 | 2.860 (2021) |
| V7 = V6 **+** e9flow | 3,472 | 3.928 | +1.95 | 6,770 | 2.769 |
| V6' = esf **->** e9flow | 3,017 | 4.298 | +2.08 | 6,275 | 3.149 |
| **CONTROL-A: keep esf, tighten to \|esf\| >= 0.7** | **2,925** | **4.433** | **+2.15** | 6,289 | **3.594** (2023) |
| CONTROL-B: dslo >= 16 (pre-S43g roster) | 3,007 | 4.137 | +2.06 | 6,192 | 3.542 |

As a 7th voice: +222 trips buys +3.6% net but costs 0.16 PF and makes every
year but 2026 worse — the marginal-trade pattern already rejected. As a
REPLACEMENT it beats the baseline — **but CONTROL-A beats IT on PF,
expectancy AND worst year at 92 fewer trips.** Turning the existing `esf`
knob from 0.5 to 0.7 does everything the new voice does and more. The
apparent gain was trip-count arithmetic.

⭐ **VERDICT: rejected. The anchored eff_9ema family is CLOSED**, like the
arming-high family before it. **The pattern now has a name: an anchored
ratio at a VARIABLE-AGE anchor keeps producing cells that clear pooled
nulls AND span controls but die against a knob on an existing feature.**
The iso-trip control is the test that catches it — a pooled null and a
span control together are still not enough.

⏭ **INCIDENTAL LEAD (from the CONTROL, not the hypothesis):** `|esf| >=
0.7` reads better than the current roster on every axis but volume —
-10% trips, PF 4.090 -> **4.433**, expectancy +2.01 -> +2.15, **worst year
2.860 -> 3.594**, net -3.7%. Needs its own fine-band/per-year/iso-trip
scrutiny before it goes near the spec.

## ⭐⭐ S43an — the `esf` VOICE IS LEG AGE IN DISGUISE; replaced by `bars_since_first_low <= 390` (2026-08-05)

Chased as an INCIDENTAL LEAD out of the S43am iso-trip control, which
showed `|esf| >= 0.7` beating the roster. Following it produced a better
answer than the lead itself.

### 1. `|esf|` IS A SPAN PROXY

`corr(|eff_since_flow|, slots_since_flow) = -0.65`. Median leg span **8
slots at `|esf| >= 0.8` vs 31 at `< 0.5`**. The decisive test — swap in a
RAW LEG-AGE voice with no efficiency computation anywhere:

| voice | n | PF | avg% | net | worst yr |
|---|---:|---:|---:|---:|---:|
| `\|esf\| >= 0.8` | 2,846 | 4.636 | +2.19 | 6,233 | 3.433 |
| `slots_since_flow <= 8` | 2,853 | 4.606 | +2.19 | 6,248 | **3.487** |

Same trips, same expectancy, same net, same PF within noise. **The eff
computation contributes nothing.** The voice's actual content is "the first
low was under ~4 minutes ago".

### 2. WHY THE THRESHOLD MATTERED: EXCLUSION, NOT SELECTION

Among trips the `esf` voice UNIQUELY admits (no other voice firing), every
tier below 0.80 is below-book (ratios vs the book, per year):

| uniquely admitted | n | PF | avg% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| [.50,.60) | 467 | 4.64 | +1.82 | 0.87 | 0.81 | 0.52 | 0.55 | 0.46 | 1.08 | 0.68 |
| [.60,.70) | 242 | 2.24 | +1.12 | 0.68 | 0.51 | 0.74 | 1.12 | -0.15 | 0.03 | 0.99 |
| [.70,.80) | 178 | 3.51 | +2.30 | 1.31 | 0.85 | 1.21 | -2.32 | 0.67 | 0.13 | 0.97 |
| >= .80 | 155 | 26.45 | +3.20 | 1.31 | 1.46 | 0.74 | 0.49 | 1.84 | 1.55 | 1.04 |

`esf >= 0.5` was admitting **709 trips that lose to the book in six years
of seven.** Raising the threshold helped by NOT ADMITTING those — not
because high-esf trips are good.

### 3. ⭐ VOICE, NOT GATE — and the difference is large

| form | n | PF | avg% | net | worst yr |
|---|---:|---:|---:|---:|---:|
| baseline (esf voice) | 3,250 | 4.090 | +2.01 | 6,532 | 2.860 |
| **voice `bars <= 390`** | 2,997 | **4.550** | +2.14 | **6,414** | **3.325** |
| gate `bars <= 1800` | 2,511 | 4.466 | +2.17 | 5,449 | 3.292 |
| gate `bars <= 1200` | 2,099 | 4.449 | +2.16 | 4,534 | 2.897 |
| gate `bars <= 900` | 1,633 | 4.847 | +2.25 | 3,674 | 3.329 |
| gate `bars <= 600` | 996 | 5.088 | +2.40 | 2,390 | **2.261** |

**Every gate form gives less money at no better PF**; the 600 gate buys PF
5.088 by discarding 63% of the net AND has a WORSE worst year than the
incumbent. Structural: as a gate, leg age applies to every trip and kills
good ones the strong voices caught on older legs. **Leg age is a REASON TO
TAKE a trade, not a REQUIREMENT OF one.**

### 4. THE THRESHOLD IS A PLATEAU

| voice threshold (bars) | 240 | 300 | 388 | 480 | 600 |
|---|---:|---:|---:|---:|---:|
| PF | 4.597 | 4.572 | 4.569 | 4.329 | 3.736 |
| net | 6,224 | 6,257 | 6,437 | 6,506 | 6,444 |

Flat 240-390, decaying after. **390 chosen over the measured 388** (user:
"388 would look like we're overfitting if anybody looked at these params").
The difference is 22 candidate trips -> **3 mc=3 trips, all in 2021**: PF
4.569 -> 4.550, net -0.4%. Noise. ⭐ **390s = exactly 6.5 minutes**, while
388 is an artifact of the 30-bar slot grid (`slots <= 12` <=> `bars <= 388`,
verified 0 disagreements / 1,344 trips; `slots = 12` spans bars [359, 388]).
State the rule in SECONDS, not in slot-boundary units.

### 5. THE NEW ROSTER — better in ALL SEVEN YEARS

    {v20 >= 140bp, d20a < -28%, dslo >= +8%, ramp < -12,
     bars_since_first_low <= 390, haltband ssh in [20,80m)}

| year | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| esf roster | 10.138 | 2.860 | 3.870 | 3.680 | 3.639 | 3.662 | 4.158 | 4.090 |
| **age roster** | **10.406** | **3.325** | **4.130** | **3.939** | **4.091** | **3.877** | **5.242** | **4.550** |

**-7.8% trips, -1.8% net, PF +11.2%, worst year +16.3%, and better in
every single year.**

**Controls passed.** ISO-TRIP (in the `esf>=0.8` form): cutting the same
~400 trips through `dslo >= 24` (n = 2,835) gives PF **4.090** and worst
year 2.899 — literally no better than the incumbent. NOT REDUNDANT with the
`K` gate (`corr(slots_since_flow, lows_since_first_low) = 0.274` — leg
DURATION and leg LOW-COUNT are near-independent). Keeping BOTH voices is
worse than either alone (3,280 @ 4.064).

### 6. SIZING-MATRIX INTERACTION: CLEAN

`corr(bars_since_first_low, gap_adj_1200) = 0.054`, `corr(..., speed) =
-0.041`. Tier shares barely move (gap_adj<15 40.2% -> 41.5%; deep 14.3% ->
15.0%). mc=3:

| tier | esf: n / PF / avg% | age: n / PF / avg% |
|---|---|---|
| A gap_adj<15 & deep | 188 / 7.95 / +3.67 | **188 / 8.39 / +3.90** |
| B gap_adj<15 only | 1,118 / 5.06 / +2.30 | 1,054 / **5.60** / **+2.38** |
| C deep only | 277 / 2.70 / +2.04 | 261 / **3.35** / **+2.34** |
| D neither | 1,667 / 3.58 / +1.63 | 1,491 / **3.88** / **+1.73** |

Every tier improves on both axes. **Tier A is IDENTICAL in size (188)** —
the change never reaches the top tier; all 256 removed trips come from B
(-64) and D (-176), the dilutive tiers. Tier C — the fragile one — improves
in 6 of 7 years (2024 +1.49% -> +2.42%).

⚠ **THE RECORDED MULTIPLIERS ARE STALE** (A 2.56 / B 1.53 / C 1.62 / D
1.00). Measured on the v2.7 book: **A 2.25 / B 1.41 / C 1.25 / D 1.00**
(esf) and A 2.25 / B 1.37 / C 1.35 / D 1.00 (age). B and C have SWAPPED
versus the recorded spec, which retires the "cap C <= B" rule instead of
needing it. ⭐ **USER: the correct denominator is PF / VOLATILITY, not
average trade % — expectancy-weighting silently sizes up whatever tier has
the fattest tail.** Deferred; re-derive properly.

⚠ **Tier A is thin and has a hole:** 188 trips over 6.5 years (~29/yr) with
**ZERO trips in 2022**; tier B's 2022/2023 PFs are absent-loss artifacts
(281.6 and `inf` = zero losers). For thin tiers, per-year PF is not
stability evidence — read EXPECTANCY.

⚠ **NO ENGINE CHANGE.** The roster is applied post-hoc in the mc replay's
`--where`. `bars_since_first_low` has been recorded since long before this
session — this is a REDISCOVERY of an existing column, not a new feature.
(`slots_since_flow`, added in S43am, is that / 30 — corr 0.9999.)

## ❌ S43ao — ANCHORED z + the LAGGED CHANNEL LOW: all rejected; the deep-flush axis is ONE-DIMENSIONAL (2026-08-05)

**User:** *"Can we look at z score since the arming high and also the first
low? ... Maybe we should change how we're calculating the deep flush as
well. One candidate would be to use the distance from 1m high. Another
would be to create a new 20m low feature lagged by 1m. These measures are
all just a little off and don't behave the way that I'd like."*

### BUILD (`v33_anchz`, 35,778 trips — parity with v31/v32)

1. **`AnchoredEff.Z(px)`** — volume-weighted log-price moments (Σv, Σ v·ln p,
   Σ v·(ln p)²) accumulated PER PRESENT BAR since the anchor, so `z_since_high`
   / `z_since_flow` are directly comparable to `z_20m` (whose `dlv_1200` /
   `dlv2_1200` are per-bar SumMa). Same anchors and same reset as `.Eff`.
2. **`entryMinLag = LagMa<float> 60`** pushed with `priorEntryMin` each present
   bar → **`chan_lo_prev`** = the entry-channel min AS OF 60 BARS AGO (mirrors
   `vwap60Lag` → `vwap_60_prev`). `dlo1m = signal_vwap/chan_lo_prev - 1`.

### ⚠ `d1s` REDISCOVERED UNDER A NEW NAME — a process failure

I derived `signal_vwap/chan_lo - 1`, called it `dlo`, and reported an
iso-trip cell (188 dense-tape trips, PF 11.88, positive all seven years) as
a promising find. **User: "I think we were calling the dlo here d1s
elsewhere. When we checked the d1s feature previously, we couldn't find any
benefit."** Correct — S42c defines `d1s = signal_vwap/chan_lo − 1` and
**S42g DROPPED it**: on g60, `d1s < -0.5` and its complement both read
**PF 3.78 = 3.78**, zero separation, with the voice concentrating the 2022
wart (1.05 vs 7.19).

**Why my table looked good:** the threshold was **not chosen** — it was
derived post-hoc to hit 188 trips so it would match the incumbent's count
(implied cut `d1s < -0.6026%` = the 15th percentile of the stratum), inside
a sub-stratum S42g never conditioned on. Move off that point and it breaks:
at `d1s < -1%` **2022 goes NEGATIVE in both strata** (-0.38 dense / -6.17
sparse) — which is exactly S42g's finding that the >1% smash-through bands
"SAG with bear warts (2022 = 0.98 / 0.52)". The two analyses agree; my
headline was the artifact. ⭐ **LESSON: grep the log for a feature's name
AND its formula before presenting it as new.**

### ⭐⭐ THE REAL FINDING: THE DEEP-FLUSH AXIS IS ONE-DIMENSIONAL

| measure | corr with `speed` |
|---|---:|
| `d1m` (distance from 1m high) | **0.911** |
| `dlo1m` (below the 1m-LAGGED 20m low) | **0.888** |
| `z_since_flow` | 0.412 |
| `z_20m` | 0.406 |
| `d1s` (breach depth) | 0.355 |

**The three "different" depth measures are one feature with three names**,
and none of the genuinely distinct ones beats `speed` at its own job. That
is the answer to "these measures are all just a little off": they are
shadows of a single axis, and `speed` is its best available expression.

### THE VERDICTS

| candidate | verdict |
|---|---|
| `z_10m` | ❌ NON-MONOTONE — its good band splits at finer resolution ([-3.0,-2.75) is 3/7 years and NEGATIVE in 2022/2026; [-2.75,-2.5) is 5/7). Where it acts alone it lifts PF 4.08 → 4.88 with expectancy FLAT (+2.00 → +2.09) = loss-tail reshuffling, not selection. |
| `z_since_high` | ❌ SPAN ARTIFACT — pooled gradient is clean (3.25 → 1.84 %/trip) but within span terciles expectancy is FLAT (1.98/2.04/1.96 and 2.21/2.25/2.11) and only sorts in T3. |
| `z_since_flow` | ❌ REAL BUT DOMINATED — see below. |
| `d1m`, `dlo1m` | ❌ speed twins (0.911 / 0.888). |
| `d1s` | ❌ already rejected in S42g. |

**`z_since_flow` is the near-miss and deserves its record.** It is the ONLY
anchored feature all session to hold its direction under span control — the
most-negative tercile wins in ALL THREE span strata (+2.83 / +2.32 / +2.59
vs middles 2.10 / 1.76 / 1.58). Its `[-3.0,-2.5)` band is 2,048 trips (19%
of g60) at PF 5.11 / +2.53% with **6/7 years above 1.0 and the seventh at
0.98**. It clears the random null (n=2,279: PF 95% [3.74, 4.80], avg%
[1.98, 2.26]).

**❌ AND IT STILL DIES ON THE ISO-TRIP CONTROL:**

| cut | n | PF | avg% |
|---|---:|---:|---:|
| g60 baseline | 10,666 | 4.27 | +2.13 |
| candidate `z_since_flow < -2.5` | 2,279 | 5.29 | +2.65 |
| ctrl `z_20m` tightened | 2,279 | 4.30 | +2.54 |
| ctrl `\|esf\|` tightened | 2,279 | 4.71 | +2.31 |
| ctrl leg age tightened | 2,279 | 5.78 | +2.49 |
| **ctrl `speed` tightened** | 2,279 | **5.42** | **+3.16** |

Tightening `speed` to the same trip count wins on BOTH axes.

### ⭐⭐⭐ THE PATTERN, NOW FOUR FOR FOUR

Arming-high eff (S43ai) → `eff_hi_flow` (S43ak) → anchored `eff9` (S43am) →
anchored `z` (here). **Every one cleared the pooled null; three of four
cleared span control; ALL FOUR died against a knob on a feature already in
the spec.** The pooled null and the span control are necessary and not
sufficient — **the iso-trip control against an existing knob is the test
that actually decides.** The only feature to survive the full battery today
was LEG AGE (S43an), and it survived precisely because it is NOT on this
axis (`corr` with speed = -0.041).

⏭ **OBSERVATION from the control column, not a proposal:** `speed`
tightened to the top 21% yields **+3.16%/trip**. There may be more in the
speed axis than `< -6%` extracts. ⚠ Check S43ac and the earlier speed work
FIRST — that is the step skipped on `d1s`.

## ⭐⭐⭐ S43ap — 2m/3m FLUSH FEATURES, and THE mc=0 TAIL ILLUSION (2026-08-05)

**User:** *"let's try 2m flush features... the 20m low lagged by 2m and the 2m
high"*, then 3m — *"as far as we'd want to go for quick flushes"*.

### BUILD (`v34_flush2m`, `v35_flush3m` — both 35,778, parity)

`entryMinLag120/180 = LagMa<float>` on `priorEntryMin` -> `chan_lo_prev_120/180`;
`max180 = MaxMa 180` -> `hi_180` (`max120` already existed). Four derived
measures: `d2m/d3m = signal_vwap/hi_120|180 - 1`, `dlo2m/dlo3m =
signal_vwap/chan_lo_prev_120|180 - 1`.

### ⭐ LENGTHENING THE HORIZON DECORRELATES FROM `speed`

| | 1m | 2m | 3m |
|---|---:|---:|---:|
| distance-from-high | 0.913 | 0.912 | **0.774** |
| below-lagged-20m-low | 0.888 | 0.751 | **0.629** |

The 1m family is one axis three times over; the 3m pair is genuinely separate.

### THE THRESHOLD-RULE LADDER (g60, mc=0), ratios vs g60 per-year

| rule | n | PF | avg% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `speed < -6%` (incumbent) | 1,520 | 5.34 | +3.30 | 1.62 | 1.79 | 0.43 | 1.56 | 1.84 | 1.37 | 1.29 |
| **`d3m < -9%`** | 1,763 | **5.64** | **+3.47** | 1.85 | 1.55 | **-0.59** | 1.60 | 1.94 | 1.48 | 1.51 |
| `dlo3m < -5%` | 3,144 | 4.84 | +2.88 | 1.61 | 1.68 | 0.57 | 1.29 | 1.49 | 1.17 | 1.16 |
| **`dlo3m < -7%`** | 1,333 | 5.51 | **+3.60** | 2.15 | 2.23 | 0.17 | 1.52 | 1.53 | 1.57 | 1.56 |
| `dlo3m < -9%` | 497 | 6.14 | +4.66 | 2.93 | 3.10 | **-1.22** | 1.87 | 2.10 | 1.97 | 2.05 |

⭐ **`d3m < -9%` is the FIRST depth candidate all session to survive the
threshold re-expression** (16% more trips than the incumbent at higher PF AND
expectancy), and its iso-trip and threshold views AGREE on 2022 (-1.20 / -0.59)
where `dlo1m`'s contradicted. `dlo3m < -7%` has the highest expectancy of any
rule tested (+3.60%) and no negative year.

### ⭐⭐⭐ THE FINDING: 2022 WAS A CONCURRENCY ARTIFACT ALL ALONG

**User: *"The reason why ENSV is so bad is because the system just kept adding
down constantly."*** Correct, and decisive.

2022's entire loss in the deep cell was **ONE ticker-day** — ENSV 2022-03-08,
-56.0% of a -59.2% year, from 5 tkds / 12 trips. At **mc=1 ENSV takes exactly
ONE trip that day: the +0.2% one.** All four -25%..-31% entries are excluded,
because `NINE` entered at 43283 — 43s before ENSV's first signal — and held the
single slot to 44144. (⚠ So mc=1's protection here was INCIDENTAL, not a
property of ENSV.)

**Every "2022 is negative" reading on the deep axis this session was an mc=0
artifact:**

| rule, on the mc=1 book | n | PF | avg% | 2022 ratio |
|---|---:|---:|---:|---:|
| `speed < -6%` | 168 | 3.93 | +2.76 | **1.67** |
| `d3m < -9%` | 193 | **4.17** | **+2.79** | **1.42** |
| `dlo3m < -5%` | 305 | 3.89 | +2.39 | 1.09 |
| `dlo3m < -7%` | 128 | 3.71 | +2.62 | 0.70 |

All positive; `speed` and `d3m` ABOVE baseline — versus +0.43 and **-0.59** at
mc=0. **The depth axis does not have a 2022 problem; the mc=0 attribution view
does.** The problem year at mc=1 is 2023 (0.87-0.92 for all four = a property of
the book, not of any measure).

**And the fat left tail is pyramiding, confirmed:** mc=1 worst trip **-15.5%**
vs mc=3 **-30.6%**, while p1 (-9.2 vs -9.8) and p5 (-4.0 vs -3.7) are
identical. Only the extreme tail differs — and at mc=3 that -30.6% sits in
**tier D**, not in a deep tier.

⚠ **USER CAUGHT (PF, not expectancy):** no deep rule beats the mc=1 book on PF
(book 4.17; best rule 4.17, two below). They lift EXPECTANCY (+2.39..+2.79 vs
+2.00) while leaving PF flat. **Bigger swings, not better ones.** Right metric
for a sizing lever, but it deflates the mc=0 story.

### ⭐⭐ THE SIZING MATRIX AT mc=1 — never done before (it was built at mc=0)

S43ac derived the matrix on the **mc=0 sampler** (top state 567 trips; mc=3
gives 188, mc=1 gives 73). Re-derived:

| tier | mc=1 n / PF / avg% / rel | mc=3 n / PF / avg% / rel |
|---|---|---|
| A gap_lo & deep | 73 / 6.89 / +3.75 / **2.29** | 188 / 8.39 / +3.90 / **2.26** |
| B gap_lo only | 403 / 5.15 / +2.24 / **1.37** | 1,055 / 5.57 / +2.37 / **1.37** |
| C deep only | 95 / **2.70** / +2.01 / 1.22 | 261 / 3.35 / +2.34 / 1.36 |
| D neither | 607 / 3.69 / +1.64 / 1.00 | 1,493 / 3.86 / +1.73 / 1.00 |

⭐ **The weights are STABLE across concurrency** (A 2.29/2.26, B 1.37/1.37 exact,
D 1.00) — the matrix was derived at the wrong mc but survives it.
⚠ **Except C: at mc=1 its PF (2.70) is BELOW tier D's (3.69)** while carrying
higher expectancy and the worst tail (-15.5%). The `cap C <= B` rule turns out
to be well-founded, not a fudge.
⚠ **Tier A at mc=1 is 73 trips (11/yr), NO 2022 at all, and a 2021 (0.71) BELOW
tier D's (0.81).** The largest position size rests on four good years of six.
**B is the only tier steady everywhere** (1.62-2.80 every year, 403 of 1,178).

### ⭐⭐ TIER C RE-CHECKED AT mc=1: the z-refinement REPLICATES

| | n | losers | PF | avg% | worst |
|---|---:|---:|---:|---:|---:|
| mc=1 C = `gapHI & deep` | 95 | 23 | **2.70** | +2.01 | -15.5 |
| mc=1 **C\* = `+ z<-2.5`** | 40 | 8 | **4.47** | +2.94 | **-10.3** |
| mc=1 demoted (`deep, no z`) | 55 | 15 | **1.94** | +1.33 | -15.5 |
| mc=1 D (reference) | 607 | 141 | 3.69 | +1.64 | -13.6 |
| mc=3 C\* | 119 | 20 | 6.33 | +3.52 | -10.3 |
| mc=3 demoted | 142 | 35 | 2.06 | +1.35 | -15.9 |

**The demoted cell is stable garbage at BOTH concurrencies** (PF 1.94 vs 2.06,
avg +1.33 vs +1.35, and it owns the tail: -15.5/-15.9 vs C\*'s -10.3 at both).
⭐ **The z condition is what makes C a real tier**: unrefined C sits BELOW D on
PF at mc=1 (2.70 vs 3.69); C\* sits above it (4.47).

8 states at mc=1 reproduce the mc=3 structure: sparse tape deep-alone **0.82**
(below baseline), z-alone 1.10, **both 1.82** (mc=3: 0.77 / 0.81 / 1.99); dense
tape `z-deep+` and `z+deep+` are **both exactly 2.32**, so z adds NOTHING where
tier A lives. **A stays speed-only at both concurrencies.**

⚠ **C\* is 40 trips at mc=1 (~6/yr) — the POOLED replication is the evidence;
the per-year rows are NOT readable** (the demoted cell's 2022 = 9.06 and 2025 =
-0.51 are a handful of trips each).

### THE CHART RIG — `scripts/visualization/flushfader_loser_charts.py`

1s vwap + volume, with the two ROLLING levels the engine uses: the prior 5m MAX
(= the exit target) and the prior 20m MIN (= the entry trigger). ⭐ **The 5m max
RATCHETS DOWN with a crash**, so on a sustained slide a `"target"` exit prints
far below entry — ENSV exits at $4.612 against a $6.645 entry, reason `target`.
The system has no stop; it waits for a bounce scored against a collapsed
reference. **That is an EXIT-design property, not a signal one.**

⚠ **BUG THE USER CAUGHT — the stored 1s slim parquet is RAW.** `Intraday.fs`
multiplies by `adj_ratio` at LOAD time, so the adjustment lives in the ENGINE,
not the file. Trip prices (`entry_px`/`exit_px`/`signal_vwap`) ARE adjusted and
must be divided; **the bars must NOT be.** I divided both and shrank BSFC 1000x
and CING 12x. Verified: BSFC 2022-01-21 bars 4.26-5.14 vs `entry_px` 4455.61
(adj 1000); CING 2023-12-28 bars 8.15-12.54 vs 106.3 (adj 12).

## ❌ S43aq — `dlo3m` AS THE DEEP DIMENSION: rejected (2026-08-05)

**User:** *"Let's try the same table except this time with dlo3m < -5%
instead of speed."* It buys volume and loses the structure.

| tier | mc=1 n / PF / rel  (speed's in brackets) | mc=3 n / PF / rel |
|---|---|---|
| A gap_lo & deep | 107 / **4.97** / **1.96**  (73 / 6.89 / 2.29) | 310 / 6.61 / **2.08**  (188 / 8.39 / 2.26) |
| B gap_lo only | 369 / **5.67** / 1.49  (403 / 5.15 / 1.37) | 933 / 5.75 / 1.42  (1,055 / 5.57 / 1.37) |
| C deep only | 198 / 3.36 / 1.31  (95 / 2.70 / 1.22) | 541 / 3.57 / 1.35  (261 / 3.35 / 1.36) |
| D neither | 504 / 3.52 / 1.00 | 1,213 / 3.86 / 1.00 |

Tiers A and C get ~65% bigger. But:

❌ **At mc=1 tier A's PF (4.97) falls BELOW tier B's (5.67)** — the top tier
stops being the best tier on PF. With `speed` it is 6.89 vs 5.15, clearly
ordered. Separation drops too (rel 1.96 vs 2.29).

❌ **It makes the z-score MORE necessary, not less** (the user's hypothesis was
that `dlo3m` might let us dispense with it). Dense-tape cells at mc=1:

| deep measure | `z-deep+` | `z+deep+` |
|---|---:|---:|
| `speed < -6%` | 2.32 | 2.32 (**exactly equal — z is pure cost in tier A**) |
| `dlo3m < -5%` | 1.74 | **2.49** (**z is what separates the top cell**) |

❌ **THE TIER-C REFINEMENT STOPS REPLICATING ACROSS CONCURRENCY:**

| | n | PF | avg% | worst |
|---|---:|---:|---:|---:|
| mc=1 C = `gapHI & dlo3m` | 198 | 3.36 | +2.04 | -15.5 |
| mc=1 C\* = `+ z<-2.5` | 69 | **4.10** | +2.45 | -10.3 |
| mc=1 demoted | 129 | 3.02 | +1.81 | -15.5 |
| mc=3 C | 541 | 3.57 | +2.21 | -30.6 |
| mc=3 C\* | 191 | **3.27** | +2.45 | -30.6 |
| mc=3 demoted | 350 | **3.80** | +2.08 | -15.9 |

At mc=3 it **INVERTS** — C\* (3.27) is worse than the cell it would demote
(3.80), and C\* KEEPS the -30.6% tail instead of shedding it. With `speed` the
refinement held at both concurrencies (C\* 4.47/6.33 vs demoted 1.94/2.06,
shedding the tail at both). **A refinement that works at one concurrency and
reverses at the other is not a real effect.**

⭐ **VERDICT: `speed < -6%` STAYS the deep dimension.** `dlo3m < -5%` gives ~65%
more trips in the sizing tiers, but costs the A/B ordering at mc=1, makes the
z-score more necessary rather than less, and breaks the tier-C refinement's
replication across mc. **The volume is real; the structure it buys is worse.**

⏭ **USER OBSERVATION, open:** *"It's very weird that there are this many deep
flushes and yet the z score is not negative"* — 129 mc=1 trips are `dlo3m < -5%`
with `z_20m >= -2.5`. Next: 1m/2m/3m OLS SLOPES with ceilings set to exclude
~25% of the book.
