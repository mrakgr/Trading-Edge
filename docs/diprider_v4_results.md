# DipRiderV4 — debloated momentum engine, explicit arm/re-arm state machine

**Project:** `TradingEdge.DipRiderV4` (forked from `TradingEdge.DipRiderV3Backside`, branch `dip-rider-v4`,
2026-07-12). A clean-sheet debloat of the SMB-backside momentum engine, rebuilt around a single immutable
`State` snapshot and an **explicit `armed` boolean** re-arm state machine instead of V3Backside's
shadow-position/concurrency-slot machinery.

## Design changes vs DipRiderV3Backside

- **Fold-then-snapshot** (not snapshot-then-push): features INCLUDE the current bar. The current bar has
  closed by the time we act on it, so using it is not lookahead. Only the true-range prior close and the
  closes-above-EMA reference read strictly-prior state. (This was the whole point of the fork — V3's
  trailing-only features were the recurring frustration.)
- **`RatioMa`** added to `TradingEdge.RollingMa` — a cumulative `Σnum/Σden` accumulator that backs the
  session VWAP (`ValueNone` until a positive denominator accumulates).
- **EMA-triggered stop off the CURRENT 20m-min-of-EMA** (`emaLow.State`, this-bar-inclusive), fires when
  the live 9-EMA drops below the frozen level. Entry requires **room** (`stop` finite AND `stop < ema`) —
  no-room setups are SKIPPED (+disarmed), so a stopless / NaN-stop trade is structurally impossible.
- **Price/volume split:** `PricePatternFires` (time window, price-slope, log-ATR, sum6≥5, chg1d/chg3d,
  ema-vs-vwap) drives arm/disarm; `VolumePasses` (vol_climb≥0.5) gates only the real position. A
  vol-failed price trigger still disarms (consumes the setup).
- **Debloat:** `IntradayConfig` trimmed to the surviving gates; `Trip`/CSV cut ~68→30 columns (a compact
  entry snapshot captured on the position); `Candidate`+SQL trimmed; `Program.fs` Argu DU/banner trimmed.
  Net −323 lines across Backtest+Program (commit `13e1ffb`).

## Finding 1 — V4 does NOT reproduce the V3Backside book (2020+): 2215 vs ~660 trips

Test window **2020-01-01 → 2026-06-25** (the modern period — pre-2020 has too few of these setups to be
worth the runtime). All three share the identical entry gates (log-ATR≥0.013, price-slope>0, sum6≥5,
chg1d≥10%, chg3d≥0%, ema-vs-vwap≥−2%, vol_climb≥0.5).

| Engine | Stop | trips | win % | net P&L | PF (MOC) |
|---|---|---:|---:|---:|---:|
| DipRiderV3Backside | geom (sess-min-close, ⅔) | 653 | 45.6% | $613,167 | 2.946 |
| DipRiderV3Backside | `--ema-stop` (frozen 20m-min-9EMA) | 662 | 44.9% | $623,318 | 2.956 |
| **DipRiderV4** | ema (CURRENT 20m-min-9EMA) | **2215** | 39.6% | $1,254,445 | 2.201 |

**V4 fires ~3.3× as many trips.** This is NOT the "almost identical" result expected — the entry SET
diverges, not just the stop. Diagnostics:

- V4 trades **1720 unique symbol-days** vs V3Backside's **633** (~2.7×). So V4 is not merely re-entering
  intraday — it opens on many symbol-days V3Backside never touches.
- Of V4's 495 non-first same-day entries, **413 overlap a still-open prior position** (entry before the
  prior exit). V4 is NOT holding the concurrency slot for a position's lifetime.

### Root cause (hypothesis, fix pending)

The two engines enforce "one setup at a time" differently:

- **V3Backside:** the arm state IS the concurrency slot. A price trigger (real OR a vol-failed **shadow**)
  opens a position that occupies the slot and runs full exit logic. The slot frees only when that position
  **exits** (stop/MOC). No new entry until the prior position closes → thin book.
- **V4:** the `armed` boolean re-arms as soon as the live 9-EMA drops below `sState.emaLow` — but
  `sState.emaLow` is the **ROLLING 20m-min-of-EMA**, which keeps sliding UP as old bars fall out of the
  window. So the re-arm condition is met far too easily and re-fires while a prior position is still open;
  the slot is never held.

⭐ **Fix to try (user, 2026-07-12):** the re-arm level must be **FROZEN** at disarm/entry (snapshot the
20m-min-of-EMA when the setup is consumed) and re-arm only when the live 9-EMA breaks below that frozen
level — instead of comparing against the ever-sliding rolling min. This is the memory's validated lesson:
*to emulate a post-hoc filter of an mc-capped book LIVE, hold the slot with a shadow that runs real exit
logic — a single-condition proxy for "exited" fires at the wrong time.* The current single-boolean re-arm
is that wrong proxy.

Whether freezing the level ALONE closes the gap, or whether V4 also needs the vol-failed **shadow position**
(so vol-fail-heavy symbol-days produce NO reported trade, as in V3Backside), is the open question for the
next session.

### Artifacts

- `/tmp/drv4.csv`, `/tmp/drv3bs_geom.csv`, `/tmp/drv3bs_ema.csv` (+ `.log` summaries) — the 2020+ runs above.
- V4 smoke run (Q1 2024): 442 candidates → 43 trips, PF 3.65, **0 NaN stops** (the room-guard holds),
  exits = stop|moc only.

## Finding 2 — the gap was TWO MISSING GATES, dominated by the exhaustion cut (not the re-arm mechanism)

The re-arm-level fix (F1's hypothesis) was implemented (3 re-arm modes: rolling/session/stop-level, all
frozen-capable) plus a `--max-concurrent` cap — but **none of it closed the gap to V3Backside** (~660 trips).
The tightest V4 variant still ran ~2× the trips. Diagnosis by direct engine diff found the real cause:
**the debloat DROPPED two entry gates that V3Backside runs by default.**

| # | V3Backside (default) | V4 (as debloated) | |
|---|---|---|---|
| Exhaustion cut | `MaxRvol5m20d = 100` **ON** (docs: "cuts ~half the book") | **absent** | ⭐ the dominant driver |
| Tightness | `MinTightness = 3.0` **ON** | **absent** | minor (see F3) |
| Snapshot | snapshot-then-push (**prior** bar) | fold-then-snapshot (**current** bar) | residual |
| Stop | geometry (sess-min-close × ⅔) | EMA-stop (current 20m-min-EMA) | residual |
| Re-arm | slot-only (exit = re-arm, via shadow) | slot + explicit re-arm level | residual |

**Restoring both gates** (stop-level·skip·mc1, the closest V3Backside analogue) collapsed the book and lifted PF:

| config | trips | win% | net | raw PF |
|---|---:|---:|---:|---:|
| V4 stop·skip·mc1 (gates missing) | 1469 | 41.5% | $893k | 2.23 |
| **V4 stop·skip·mc1 (gates restored)** | **814** | 42.9% | $668k | **2.80** |
| V3Backside (geom, ref) | 653 | 45.6% | $613k | 2.95 |

So the missing gates were **~80% of the gap** (1469→814 of the 816-trip excess). The residual ~160 trips /
0.15 PF is the mechanism trio (snapshot / stop / re-arm) — real but secondary. The user's instinct was
right: a **missing condition**, not just snapshotting. Restored the exhaustion-cut plumbing (`vol5avg`,
`permin20d` ctor arg, `avgvol20` candidate col) + tightness (`rangeHigh`/`rangeLow`, `Tightness` member).

## Finding 3 — tightness is DEAD WEIGHT (redundant with the log-ATR floor); default OFF

Ablation (stop·skip·mc1, 2020+): tightness ON→OFF changed the book by **26 trips (3.2%), PF 2.804→2.800,
win rate identical**. A name clearing `log-ATR ≥ 0.013` already has real range, so the `tightness ≥ 3` gate
is almost never the binding constraint — exactly the documented V3 conclusion ("OFF, redundant with ATR")
that was accidentally left ON. **Default flipped to `MinTightness = 0.0`** (`--min-tightness 3` restores).

⚠ **Non-monotonic in the arm/re-arm machine:** turning a gate OFF does NOT uniformly ADD trips. Full 24-cell
sweep (3 re-arm × 2 vol × 2 mc × 2 tight), tightness ON→OFF trip delta:

| vol mode | Δ trips (off − on) | why |
|---|---|---|
| GATE | **+9 to +32** (adds, intuitive) | gate never disarms on a fail → extra passes = extra entries |
| SKIP | **−26 to −55** (REMOVES, counterintuitive) | a looser gate lets an EARLIER bar consume+disarm the setup (often a vol-skip that opens nothing) → burns it before a later, better bar can fire |

So in SKIP mode a gate is not a pure filter — it also controls *when* the setup is consumed. Every delta is
≤5% of trips and ≤0.02 clip PF either way: tightness is noise. (Same class as the F34/F26 "gate reallocates
the slot" lesson.)

## Finding 4 — the exhaustion cut is a REAL quality lever: trades ~⅓ the net for +0.35 PF

The rolling-ema-low book is the standout (best net; gate & skip both strong). mc=0, 2020+, exhaust ON vs OFF:

| variant | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| rolling·**gate**·exhaust-ON | 2159 | 40.7% | $1.46M | 2.47 | 1.72 |
| rolling·**gate**·exhaust-OFF | 3846 | 39.1% | **$2.12M** | 2.11 | 1.53 |
| rolling·**skip**·exhaust-ON | 1213 | 41.1% | $915k | 2.73 | 1.87 |
| rolling·**skip**·exhaust-OFF | 2215 | 39.6% | $1.25M | 2.20 | 1.58 |

The **$2.12M "standout" is the exhaust-OFF gate book** — it buys +$660k net (+45%) but PAYS raw PF 2.47→2.11
and clip PF 1.72→1.53, on 78% more trips. Unlike tightness, the exhaustion cut is a genuine risk-adjusted
filter (it rejects late blow-off entries). It stays **ON by default** — the extra net isn't worth the PF/DD.
gate ≈ skip on risk-adjusted quality here too (clip 1.72 vs 1.87 ON) but gate carries far more net, so gate
is the higher-capacity book, skip the higher-PF one.

### Per-year — rolling·mc0, exhaust ON vs OFF (clip PF)

**2021 is the adverse-regime watch year** — every book sags there, as expected; the exhaustion cut lifts it.

| year | gate·ON | gate·OFF | skip·ON | skip·OFF |
|---|--:|--:|--:|--:|
| 2020 | 1.79 | 1.59 | 1.43 | 1.60 |
| 2021 | 1.33 | 1.14 | 1.39 | 1.12 |
| 2022 | 1.72 | 1.32 | 1.81 | 1.37 |
| 2023 | 3.15 | 2.44 | 4.36 | 2.87 |
| 2024 | 1.74 | 1.90 | 1.79 | 1.79 |
| 2025 | 1.94 | 1.75 | 2.31 | 1.88 |
| 2026 | 1.53 | 1.57 | 2.02 | 2.19 |

Exhaust-ON wins the clip PF in almost every year (esp. 2021/2022/2023) — the cut earns its keep in the harder
regimes.

### Worst day / week / month (net P&L, $10k notional) — rolling·mc0

The tail lives in **2021** (the adverse regime) for every book except skip·ON. Skip·ON — the highest-PF,
lowest-net book — also has the shallowest drawdowns by far (worst month −$5.1k vs −$15–17k for the others).

| book | worst day | worst week | worst month | profitable months |
|---|---|---|---|---|
| rolling·gate·ON | 2021-03-24 −$7,780 | 2021-W12 −$11,044 | 2021-03 −$17,296 | 61/78 (78%) |
| rolling·gate·OFF | 2021-12-10 −$9,445 | 2021-W07 −$18,129 | 2022-05 −$16,029 | 66/78 (85%) |
| **rolling·skip·ON** | 2024-09-30 −$5,629 | 2020-W30 −$8,826 | **2025-04 −$5,134** | 61/78 (78%) |
| rolling·skip·OFF | 2021-10-28 −$8,146 | 2021-W43 −$11,260 | 2021-12 −$15,362 | 68/78 (87%) |

(78 months = 2020-01 … 2026-06.) Two things cut against each other:
- The exhaustion cut (ON) **shrinks the worst month for the skip book dramatically** (−$5.1k vs −$15.4k OFF)
  — it cuts the fat left tail, not just the middle. For gate, ON/OFF worst-months are comparable
  (−$17.3k / −$16.0k) but ON reaches it on ~⅓ fewer trips.
- BUT exhaust-**OFF has MORE profitable months** (85–87% vs 78%). More trades → fewer thin/empty months that
  tip slightly negative. So OFF wins on hit-rate-of-months, ON wins on tail depth (and on PF). The ON book's
  17 losing months are shallower; the OFF book has fewer losing months but they're deeper. Consistent with
  "ON = higher risk-adjusted quality, OFF = higher raw capacity."

<details>
<summary>Monthly breakdown — rolling·gate·exhaust-ON (2159 trips, $1.46M, clip 1.72)</summary>

```
month    trips   win%         net  rawPF clipPF
2020-01      3  66.7%       3,144  57.59  57.59
2020-02      6  50.0%       1,281   1.69   1.69
2020-03     17  41.2%        -630   0.91   0.91
2020-04     11  54.5%       8,540   3.73   3.73
2020-05     24  37.5%       3,444   1.29   1.29
2020-06     24  50.0%      23,907   4.44   3.65
2020-07     33  33.3%      54,526   3.34   1.48
2020-08     16  43.8%      11,056   2.35   2.10
2020-09     16  62.5%      56,003  14.75   6.42
2020-10     16  50.0%       4,916   1.63   1.48
2020-11     38  31.6%      16,431   1.76   1.30
2020-12     40  47.5%       2,115   1.13   1.13
2021-01     93  43.0%      90,086   3.60   2.11
2021-02    105  37.1%        -963   0.98   0.98
2021-03     84  23.8%     -17,296   0.67   0.65
2021-04     15  26.7%        -794   0.84   0.84
2021-05     16  37.5%        -133   0.98   0.98
2021-06     68  44.1%      32,301   2.15   1.88
2021-07     38  18.4%       5,027   1.23   0.90
2021-08     26  46.2%       6,340   1.66   1.66
2021-09     54  38.9%      40,100   3.32   2.86
2021-10     29  44.8%      19,251   2.79   1.84
2021-11     20  40.0%         877   1.10   1.10
2021-12     31  25.8%       2,512   1.16   0.76
2022-03     46  30.4%      22,551   1.86   1.33
2022-05     14  21.4%      -5,499   0.18   0.18
2022-08     32  53.1%      24,994   3.07   2.82
2022-12     11  54.5%      34,344  12.75   4.78
2023-01     30  40.0%      27,916   3.10   2.25
2023-09     14  57.1%      18,910   7.31   7.19
2023-11      8  25.0%      34,957   9.64   2.47
2023-12      7  71.4%      48,500  45.58  16.60
2024-04     24  62.5%      62,011  11.47   6.90
2024-12     67  37.3%      60,085   2.93   1.39
2025-04     14  21.4%     -11,595   0.19   0.19
2025-05     55  52.7%     101,627   7.95   3.92
2025-08     37  70.3%     108,671  15.05   9.40
2025-10     70  61.4%      87,490   4.59   3.96
2026-04     43  18.6%      -2,984   0.91   0.63
2026-05     62  53.2%     162,133   8.76   3.74
2026-06     35  42.9%       9,565   1.37   1.07
```
(abbreviated to the notable months; full CSV: `/tmp/br_rolling_gate_mc0_rvolon.csv`)
</details>

<details>
<summary>Monthly breakdown — rolling·gate·exhaust-OFF (3846 trips, $2.12M, clip 1.53)</summary>

```
month    trips   win%         net  rawPF clipPF
2021-01    202  44.1%     137,782   2.79   2.05
2021-03    152  27.0%      -8,256   0.91   0.88
2021-07     85  22.4%      -7,878   0.84   0.70
2021-11     68  36.8%      -8,814   0.80   0.74
2021-12     89  23.6%     -13,531   0.75   0.58
2022-05     31  16.1%     -16,029   0.10   0.10
2023-12     10  80.0%      72,698  67.82  30.39
2024-10     44  34.1%     -12,890   0.44   0.44
2024-12     91  42.9%     106,945   3.45   2.24
2025-05     72  51.4%     108,911   5.32   2.90
2025-08     53  54.7%     105,720   7.33   4.71
2025-10     88  56.8%     100,303   3.79   3.22
2026-05     64  51.6%     159,109   7.65   3.26
```
(notable months; full CSV: `/tmp/br_rolling_gate_mc0_rvoloff.csv`)
</details>

<details>
<summary>Monthly breakdown — rolling·skip·exhaust-ON (1213 trips, $915k, clip 1.87)</summary>

```
month    trips   win%         net  rawPF clipPF
2020-05      8  25.0%      -3,529   0.16   0.16
2021-01     48  39.6%      52,808   4.11   2.18
2021-12     16  31.2%      -2,995   0.66   0.66
2023-12      4  75.0%      42,732  73.91  20.11
2024-04     14  71.4%      38,201  15.22   9.31
2024-06     15  20.0%      -4,956   0.43   0.43
2024-10     16  31.2%      -4,372   0.42   0.42
2025-02     16  25.0%      -5,006   0.48   0.48
2025-04      8  25.0%      -5,134   0.29   0.29
2025-05     31  61.3%      92,681  14.20   6.15
2025-08     16  62.5%      68,626  17.60   8.96
2025-10     29  69.0%      56,054  12.76  10.63
2026-05     36  50.0%      85,367   7.79   3.58
```
(notable months; full CSV: `/tmp/br_rolling_skip_mc0_rvolon.csv`)
</details>

<details>
<summary>Monthly breakdown — rolling·skip·exhaust-OFF (2215 trips, $1.25M, clip 1.58)</summary>

```
month    trips   win%         net  rawPF clipPF
2021-01    108  40.7%      71,118   2.76   1.86
2021-07     54  20.4%      -9,216   0.74   0.53
2021-10     38  39.5%      -6,374   0.73   0.73
2021-12     52  25.0%     -15,362   0.53   0.48
2022-05     16  25.0%      -6,636   0.21   0.21
2023-12      5  80.0%      48,083  83.04  28.64
2024-05     17  52.9%      51,696   5.96   2.71
2024-12     49  34.7%      58,355   3.10   1.51
2025-05     43  58.1%      99,052   8.92   4.25
2025-08     27  48.1%      68,681   7.80   4.26
2025-10     38  63.2%      64,974   8.33   6.82
2026-05     36  50.0%      85,367   7.79   3.58
```
(notable months; full CSV: `/tmp/br_rolling_skip_mc0_rvoloff.csv`)
</details>

### Artifacts (F2–F4)

- 24-cell matrix (3 re-arm × 2 vol × 2 mc × 2 tight): `/tmp/mx_*.csv` / `.log`.
- rolling·mc0 exhaust on/off (yearly+monthly source): `/tmp/br_rolling_*.csv`.
- Summary scripts: `scratchpad/mx_summary.py`, `scratchpad/breakdown.py`, `scratchpad/sweep_summary.py`.
