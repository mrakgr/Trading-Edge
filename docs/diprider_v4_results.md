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

## Finding 5 — vol_climb is a clean A+ lever; the A+ book (skip·exhaust-ON·vc0.8) is 2021-robust

`vol_climb = (volEma − volEmaMin)/volEma` is bounded [0,1); `vc = n/(n+1)` means "volEma is (n+1)× its 20m
floor". Swept on the interpretable n/(n+1) ladder (multiples of the floor), rolling·mc0, **raw AND clip PF**:

**skip·exhaust-ON** (the A+ book — the cleanest response):

| vol_climb | mult | trips | win% | net | raw PF | clip PF |
|---|---|---:|---:|---:|---:|---:|
| 0.5 | 2× | 1213 | 41.1% | $915k | 2.73 | 1.87 |
| 0.6 | 2.5× | 806 | 43.1% | $688k | 3.04 | 2.05 |
| 0.667 | 3× | 567 | 43.2% | $560k | 3.35 | 2.13 |
| 0.75 | 4× | 322 | 46.6% | $331k | 3.63 | 2.47 |
| **0.8** | **5×** | **203** | **50.7%** | **$237k** | **4.34** | **3.12** |
| 0.833 | 6× | 148 | 49.3% | $147k | 3.97 | 3.06 |

**Monotonic in BOTH PFs up to 5×, then rolls over at 6×** (raw 4.34→3.97, net halves — too sparse). So 5×
is a genuine peak, not "tighter is always better". All four books climb; **skip·exhaust-ON dominates**
(skip·ON 5× = 4.34/3.12 beats gate·ON 6× = 3.85/2.63). vol_climb + exhaustion cut are **complementary** — at
every rung exhaust-ON beats exhaust-OFF (both filter late/exhausted entries via different signals: climb =
volume-vs-floor, rvol cut = blow-off-vs-20d-pace). gate responds less than skip (in skip, a higher vc makes
more triggers skip-and-disarm, concentrating the reported book — a sharper filter than gate's straight AND).

⭐ **THE A+ BOOK — `--re-arm rolling-ema-low --min-vol-climb 0.8` (skip mode, exhaust cut ON, mc 0):**
**raw PF 4.34 / clip PF 3.12 / win 50.7% / 203 trips / $237k over 2020-2026.** Comparable to VwapReclaim's
A+ cell (PF 4.03/184 trips). The raw-vs-clip gap (~1.4×) is HEALTHY and even narrows as vc tightens — the
edge is broad-based, NOT a handful of monster winners inflating raw PF.

### 2021-robustness (the decisive test) — A+ book, raw / clip PF per year

| year | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 2020 | 16 | 56.2% | $16k | 3.21 | 2.33 |
| **2021** | 64 | 45.3% | $30k | **2.37** | **2.37** |
| 2022 | 26 | 61.5% | $30k | 6.28 | 5.53 |
| 2023 | 13 | 53.8% | $48k | 13.47 | 6.53 |
| 2024 | 25 | 44.0% | $57k | 5.58 | 2.62 |
| 2025 | 33 | 45.5% | $23k | 2.72 | 2.37 |
| 2026 | 26 | 61.5% | $35k | 5.86 | 4.77 |

⭐ **Positive EVERY year, and 2021 — the chronic adverse regime — HOLDS at raw 2.37 / clip 2.37.** The vc≥0.8
requirement FIXES 2021 (every other DRV4 book sags to ~1.1–1.4 clip there). Demanding genuine 5× volume
expansion filters exactly the low-conviction breakouts that fail when momentum isn't rewarded (the
cross-system vol_climb theme). **2021 raw == clip (2.37):** zero winners exceeded the +50% clip that year, so
2021's edge is entirely broad-based, zero tail dependence — the most reassuring possible signal for the
hardest year. Thin years (2023 @ 13 trips) can't carry weight alone, but the aggregate + the 2021 stress
test both hold.

### Tail characterization — A+ book (skip·exhaust-ON·vc0.8, 2020-2026)

| metric | value |
|---|---|
| worst day | 2024-09-19 −$3,411 |
| worst week | 2021-W26 −$3,962 |
| worst month | 2025-09 −$3,953 |
| max drawdown (daily equity curve) | **−$5,384** (trough 2026-04-09) |
| profitable months | 43/65 active (66%); 13 of 78 months had NO A+ trade |
| profitable days | 88/177 active (50%) |
| total net | $237,389 |

Remarkably shallow tail for the net: **max DD −$5.4k on $237k net (~2.3% of total P&L)**, worst day/week/month
all ≈ −$3.4–4.0k. The book is THIN (≈31 trips/yr, only 65/78 months active) — monthly hit-rate (66%) is
lower than the base books (78–87%) purely because sparse months swing, but the losing months are TINY. This
is a low-frequency, high-quality overlay, not a standalone capacity book. For capacity stay at vc 0.5–0.6;
for the A+ tier, vc 0.8.

<details>
<summary>Monthly breakdown — A+ book (skip·exhaust-ON·vc0.8, 203 trips, $237k, raw 4.34 / clip 3.12)</summary>

```
month    trips   win%         net  rawPF clipPF
2021-01      9  55.6%      10,108   6.06   6.06
2021-02     10  40.0%       7,939   3.55   3.55
2021-03     10  50.0%       4,624   2.33   2.33
2021-08      5  80.0%       7,007  77.65  77.65
2021-12      2   0.0%      -2,584   0.00   0.00
2022-08      5  80.0%      10,968  15.20  15.20
2023-12      2 100.0%      31,664    nan    nan
2024-12      4  75.0%      41,512  47.68  12.00
2025-05      7  71.4%      15,781  18.70  14.43
2025-09      6  33.3%      -3,953   0.12   0.12
2026-05     11  72.7%      26,807  31.76  23.81
2026-06      6  83.3%       6,179   4.29   4.29
```
(notable months; sparse-month PFs read `nan` when all-winners. Full CSV: `/tmp/vc_skip_on_vc0.8.csv`)
</details>

### Artifacts (F5)

- vol_climb ladder (0.4–0.833 × 4 books): `/tmp/vc_*.csv` / `.log`; summary `scratchpad/vc_ladder.py`.
- A+ book: `/tmp/vc_skip_on_vc0.8.csv`. Config: `--re-arm rolling-ema-low --min-vol-climb 0.8` (defaults
  otherwise: skip mode, exhaust cut ON, mc 0, tightness OFF).

## Finding 6 — 5m-MAX exhaustion numerator ≈ 5m-AVG (WASH; default stays AVG)

Idea (user): the exhaustion cut uses the trailing-5m **avg** 1m-vol; try the trailing-5m **MAX** instead —
the spiky 1m-vol signal is the dominant feature in the SHORT book, so it might reject blow-offs better.
Implemented as `--rvol-use-max` (numerator = `MaxMa(5)` of 1m vol vs the default `AvgMa(5)`). Since max ≥ avg
always, the same threshold cuts MORE with max — so the fair comparison is at **matched breadth** (matched trip
count), sweeping the threshold, NOT at a fixed threshold.

Sweep (skip·mc0·vc0.5, 2020+), raw / clip PF at aligned trip counts:

| ~trips | AVG (thr) | MAX (thr) |
|---|---|---|
| ~1010 | 2.70 / 1.89 (t75) | 2.63 / 1.88 (t100) |
| ~1210 | 2.73 / 1.87 (t100) | 2.73 / 1.87 (t150) |
| ~1360 | 2.66 / 1.84 (t150) | 2.67 / 1.83 (t200) |
| ~1450 | 2.56 / 1.78 (t200) | 2.56 / 1.78 (t300) |

**Identical at matched breadth** — where trip counts align, both numerators produce the same raw & clip PF
(e.g. AVG-t100/1213 trips vs MAX-t150/1177 trips: both raw 2.73, clip 1.87). Both peak at the same quality
(AVG clip 1.885 @ t75, MAX clip 1.875 @ t100). No edge either way; no new A+ tier.

⭐ **Why:** over a 5-bar window the max and avg of 1m volumes are highly correlated (a spike lifts both), so
as an *exhaustion* (late-entry reject) filter they rank entries almost identically. The max's edge in the
short book is as an *entry* signal (one violent bar); here it's a *reject*, where smoothed magnitude is what
matters — and avg captures that just as well. **Default stays AVG.** Plumbing + `--rvol-use-max` flag kept
(cheap; recorded as a settled negative so we don't retry).

### Artifacts (F6)

- avg-vs-max threshold sweep: `/tmp/rx_{avg,max}_t*.csv`; summary `scratchpad/rx_summary.py`.

## Finding 7 — BREAKOUT fusion: BreakoutTimer's structure on V4's 20m-low reset — a strong all-weather book

Fused BreakoutTimer's breakout structure into V4, but with V4's **rolling-20m-low re-arm as the reset**
(instead of BreakoutTimer's bars-since-9EMA-high count). New features (all recorded; the 20m-low re-arm
resets the cycle):
- **`bars_since_breakout`**: −1 = re-armed/waiting; **0** = the bar the 9-EMA FIRST made a strict new
  session-EMA-high after the reset; **+1/bar** after (latches ONCE per reset — later new highs don't re-latch).
  Gate: `0 <= bars_since_breakout < N`. `--max-bars-since-breakout N` (BreakoutTimer used 10).
- **`sess_ema_high`**: session-max 9-EMA (the breakout reference).
- **`lagged_sess_ema_high_10m`**: recorded-only, for a later post-hoc breakout-continuation study.
- Flags `--no-price-slope` / `--no-sum6` (BreakoutTimer used neither).

⚠ **Config note:** "breakout book" = the **settled DipRiderV4 defaults** (skip mode, exhaustion cut ON @100
5m-avg, rolling re-arm, mc 0, tightness OFF, chg1d≥10% / chg3d≥0 / ema-vs-vwap≥−2%) **PLUS** breakout gate
(bars<10), vol_climb lowered to 0.1, price-slope + sum6 dropped. NOT a stripped BreakoutTimer clone — the
exhaustion cut and the day-trend/VWAP floors are still active.

### The breakout gate is a huge quality lever; price-slope & sum6 are dead/counterproductive under it

2020+, rolling·mc0·skip·exhaust-ON, vol_climb 0.1, breakout bars<10:

| config | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| breakout, price-slope ON, sum6 ON | 968 | 46.9% | $845k | 2.83 | 1.95 |
| breakout, price-slope OFF, sum6 ON | 968 | 46.9% | $846k | 2.83 | 1.95 |
| breakout, price-slope ON, sum6 OFF | 1051 | 46.1% | $934k | 2.90 | 1.98 |
| **breakout, both OFF** | **1053** | 46.2% | $942k | **2.91** | **1.99** |
| no breakout, vc0.1 (ref) | 3236 | 32.3% | $1.40M | 2.16 | 1.53 |

- **price-slope is PURE dead weight** — ON vs OFF is byte-identical (968/$845k/1.95). A 9-EMA making a new
  session high already implies a positive slope; the gate is fully redundant. `--no-price-slope` is free.
- **sum6 is mildly COUNTERPRODUCTIVE** — dropping it ADDS net ($845k→$942k) and lifts PF (2.83→2.91).
- vs the no-breakout vc0.1 baseline (a weak book, PF 2.16/win 32%): the breakout gate lifts clip PF
  **1.53→1.99**, win **32%→47%**, on ⅓ the trips. **The breakout STRUCTURE is the edge** — it subsumes the
  momentum-quality gates (exactly the BreakoutTimer lesson). ⭐ Best book: **both OFF**.

### Yearly — breakout both-OFF book (all-weather, 2021 holds)

| year | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 2020 | 118 | 53.4% | $109k | 3.00 | 2.03 |
| **2021** | 258 | 38.4% | $118k | **1.98** | **1.48** |
| 2022 | 108 | 44.4% | $58k | 2.25 | 1.84 |
| 2023 | 69 | 59.4% | $131k | 7.89 | 5.13 |
| 2024 | 138 | 50.0% | $168k | 3.49 | 2.18 |
| 2025 | 242 | 49.6% | $299k | 3.74 | 2.34 |
| 2026 | 120 | 39.2% | $58k | 1.78 | 1.40 |

Positive EVERY year; 2021 at raw 1.98 / clip 1.48 — weaker but comfortably positive and BETTER than the base
rolling books' 2021 (~1.12–1.14 clip). The breakout structure helps the adverse regime (BreakoutTimer echo).

### Knob sweeps (both-OFF book)

**bar window** (vc0.1) — a clean capacity/quality dial:

| bars< | trips | net | raw PF | clip PF |
|---|---:|---:|---:|---:|
| 5 | 881 | $779k | 2.85 | **2.02** |
| 10 | 1053 | $942k | **2.91** | 1.99 |
| 15 | 1205 | $996k | 2.81 | 1.96 |
| 20 | 1330 | $1.10M | 2.81 | 1.92 |

Tighter = higher clip PF / less net; looser = more net / lower clip. **10 = the raw-PF peak** (BreakoutTimer's
choice). The edge decays GRACEFULLY out to 20 bars (later-breakout entries progressively weaker but +EV).

**vol_climb** (bars<10) — FLAT over 0.1–0.5 (⚠ see F8: this was a too-narrow range; the edge is in the TAIL):

| vc | trips | net | raw PF | clip PF |
|---|---:|---:|---:|---:|
| 0.1 | 1053 | $942k | 2.91 | 1.99 |
| 0.3 | 944 | $872k | 2.92 | 2.00 |
| 0.5 | 684 | $661k | 3.00 | 2.01 |

0.1→0.5 moves clip PF 1.99→2.01 (flat) while net drops. ⚠ **This turn's conclusion "vol_climb is irrelevant
under the breakout gate" was PREMATURE** — corrected in F8 (extending to the n/(n+1) ladder shows vc bites
hard from 0.5 up; there's a breakout A+ book at vc0.8). Also: vc0.1 vs vc0 (F8) shows 0 is marginally BETTER
— the V4 breakout book's exhaustion cut already removes what BreakoutTimer's vc0.1 floor caught. **Base
breakout book = vc 0 (OFF)**, not 0.1.

⭐ **Settled base breakout book:** `--max-bars-since-breakout 10 --no-price-slope --no-sum6` (vc OFF, otherwise
defaults). clip PF 2.01 / raw 2.93 / win 46% / $977k / 1097 trips, all-weather. A THIRD V4 book alongside the
base momentum book and the vol_climb A+ book — highest clip PF of any BROAD (non-A+) V4 book.

### Artifacts (F7)

- breakout price-slope/sum6 matrix: `/tmp/bo2_*.csv`; knob sweep: `/tmp/bk_*.csv`.
- The `lagged_sess_ema_high_10m` column is recorded for a future post-hoc breakout-continuation study.

## Finding 8 — breakout vol_climb: 0 beats 0.1 (base); but vc0.8 is a 2021-robust breakout A+ book

Extended the breakout vol_climb sweep to include **vc=0 (off)** and the higher **n/(n+1) ladder** (F5's
rungs), on BOTH skip and gate. This corrects two F7 claims. Breakout book (bars<10, no-price-slope, no-sum6,
rolling·mc0), 2020+:

| vol_climb | skip trips | skip rawPF | skip clipPF | gate trips | gate rawPF | gate clipPF |
|---|---:|---:|---:|---:|---:|---:|
| **0 (off)** | 1097 | 2.93 | **2.01** | 1097 | 2.93 | **2.01** |
| 0.1 | 1053 | 2.91 | 1.99 | 1067 | 2.92 | 2.00 |
| 0.333 (1.5×) | 917 | 2.93 | 1.99 | 986 | 2.91 | 2.01 |
| 0.5 (2×) | 684 | 3.00 | 2.01 | 845 | 3.06 | 2.11 |
| 0.667 (3×) | 400 | 3.21 | 2.11 | 557 | 2.94 | 1.97 |
| 0.75 (4×) | 237 | 3.55 | 2.31 | 363 | 3.56 | 2.31 |
| **0.8 (5×)** | 158 | 4.50 | 2.84 | **244** | **4.59** | **2.90** |

**The shape is a shallow U: a flat/slightly-negative DEAD ZONE at 0.1–0.333, crossing back above the vc=0
baseline (2.005) at 0.5 (2×), then climbing hard to 5×.** The volume edge is real but only bites once you
demand ≥2× the floor. (Minor wobble: gate·0.667 dips to 1.97 — arm/re-arm reallocation at that breadth; skip
is smoother. Doesn't change the trend.)

**1. vc=0 > vc=0.1 (base book) — INVERTS the BreakoutTimer F25 finding.** In BreakoutTimer vc≥0.1 lifted clip
PF 1.41→1.67. Here vc=0 is marginally BETTER than 0.1–0.333 (skip clip 2.01/$977k vs 1.99). The reason is the
config difference: V4's breakout book STILL runs the exhaustion cut + day-trend/VWAP floors, which already
remove the low-conviction breakouts BreakoutTimer's vc0.1 was catching → the loose vc floor is redundant here.
**Base breakout book: vc OFF.**

**2. F7's "vol_climb irrelevant under the breakout gate" was WRONG — too-narrow a range (0.1–0.5).** The dead
zone ends at 0.333; from 0.5 up vol_climb bites hard: **vc0.8 (5×) → clip PF 2.84 (skip) / 2.90 (gate), win
54%.** A genuine breakout A+ book. (Consistent with F5 after all — the vol_climb A+ tail just starts at ~2×.)

**3. gate ≈ skip at the A+ end, gate slightly better** — at vc0.8, gate (2.90/244 trips) edges skip
(2.84/158) with 55% more capacity at equal quality. Unlike the non-breakout books (skip dominated), here
**gate is preferable** for the breakout A+ book.

### ⭐ Breakout A+ book — gate·vc0.8 (bars<10, no-ps, no-s6): the BEST 2021 of any V4 A+ book

| year | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 2020 | 23 | 52.2% | $22k | 2.82 | 1.62 |
| **2021** | 57 | 49.1% | $45k | **2.89** | **2.32** |
| 2022 | 17 | 64.7% | $20k | 5.09 | 4.84 |
| 2023 | 21 | 57.1% | $71k | 10.44 | 4.86 |
| 2024 | 40 | 55.0% | $72k | 5.15 | 2.75 |
| 2025 | 45 | 60.0% | $79k | 7.56 | 4.12 |
| 2026 | 41 | 48.8% | $50k | 3.26 | 2.58 |

TOTAL: **244 trips / win 54.1% / $360k / raw PF 4.59 / clip PF 2.90.** Positive EVERY year; **2021 at raw 2.89
/ clip 2.32 is the best 2021 of any V4 A+ book** (beats the vol_climb A+ book's 2.37, and the base breakout
book's 1.48). Less tail-dependent than the vol_climb A+ (only 2020 sub-2.0 clip) and less thin (244 vs 203
trips, healthier per-year distribution, no 13-trip year). Config:
`--vol-as-gate --min-vol-climb 0.8 --max-bars-since-breakout 10 --no-price-slope --no-sum6`.

### Artifacts (F8)

- breakout vc ladder incl 0, skip+gate: `/tmp/bv_{skip,gate}_vc*.csv`.

## Finding 9 — feature attribution: volume-acceleration dominates; breakout adds a real premium; slope/sum6 are dead weight

Isolated what carries the edge by stripping features. Three books at matched vol_climb (all GATE, rolling·mc0,
2020+), raw / clip PF:
- **vol-only**: breakout OFF, price-slope OFF, sum6 OFF (only ATR% + day-floors + exhaustion cut remain).
- **breakout**: breakout gate ON (bars<10), price-slope OFF, sum6 OFF.
- **full-price**: price-slope ON + sum6 ON, breakout OFF (the F5-family settled book).

| vol_climb | vol-only trips/raw/clip | breakout trips/raw/clip | full-price trips/raw/clip |
|---|---|---|---|
| 0 (off) | 5583 / 2.04 / 1.45 | 1097 / 2.93 / 2.01 | 3961 / 2.14 / 1.53 |
| 0.333 (1.5×) | 3492 / 2.25 / 1.58 | 986 / 2.91 / 2.01 | 2865 / 2.34 / 1.63 |
| 0.5 (2×) | 2495 / 2.36 / 1.67 | 845 / 3.06 / 2.11 | 2159 / 2.47 / 1.72 |
| 0.667 (3×) | 1368 / 2.63 / 1.79 | 557 / 2.94 / 1.97 | 1246 / 2.69 / 1.82 |
| 0.75 (4×) | 794 / 3.19 / 2.09 | 363 / 3.56 / 2.31 | 743 / 3.20 / 2.08 |
| 0.8 (5×) | 524 / 3.71 / 2.43 | 244 / 4.59 / 2.90 | 496 / 3.69 / 2.39 |

**Net P&L (same books):**

| vol_climb | vol-only | breakout | full-price |
|---|---:|---:|---:|
| **0 (off)** | **$1,900,804** | $976,760 | $1,848,766 |
| 0.5 (2×) | $1,483,895 | $820,307 | $1,457,369 |
| 0.8 (5×) | $611,747 | $360,021 | $596,635 |

⭐ **The hierarchy: volume-acceleration (dominant) > breakout-structure (real premium) > ATR% (necessary floor)
>> price-slope / sum6 (dead weight).**
1. **Volume acceleration alone carries most of the edge.** Stripped to just ATR%+volume, vol-only climbs
   1.45→2.43 clip PF as vc rises; at vc0.8 it reaches **2.43** — beating the full-price book (2.39).
2. **The breakout (new-session-high) structure adds a large, breadth-independent premium** (~+0.3–0.5 clip PF
   at every rung; vc0.8: 2.90 vs 2.43) on FAR fewer trips — a spike into a NEW HIGH beats a spike into a
   failing move. This is the one price feature that earns its place beyond ATR%.
3. **price-slope + sum6 add ~nothing** — vol-only ≈ full-price at every rung, and vol-only even EDGES
   full-price on clip PF at the tight end. ⚠ **NOT free to drop, though:** full-price's ~1600 fewer trips
   (vs vol-only vc0) cost only ~$52k net ($1.85M vs $1.90M) = **~$32/trip** — those extra vol-only trades are
   near-zero-EV CHURN. So slope+sum6 trim churn without lifting quality (they're not harmful, just weak).
4. **Net decreases monotonically with vc in all three** (mirror of PF) — the two corners of the study:
   max-net/capacity = **vol-only vc0 ($1.90M, clip 1.45)**; max-PF/A+ = **breakout vc0.8 (clip 2.90, $360k)**.

## Finding 10 — breakout timers refactored to a class; 20m-EMA-high feature; timer-length sweeps

Extracted a `BreakoutTimer` class (Start latches −1→0 once per reset; Step +1 while ≥0; Reset →−1) and
reimplemented the session-high breakout on it (byte-identical). Added a **second timer for the 20m-EMA-high
breakout**: `emaHigh = MaxMa(20)` of the 9-EMA; the timer Starts on a fresh trailing-20m EMA high, same reset
cycle (the 20m-low re-arm). Recorded as `bars_since_20m_breakout`; gate `--max-bars-since-20m-breakout`.

### 20m-EMA breakout vs session-high breakout vs full-price (gate, vc0, no-ps, no-sum6)

| book | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| session-high, bars<10 | 1097 | 46.0% | $977k | 2.93 | **2.01** |
| 20m-EMA, bars<1 | 1478 | 34.2% | $950k | 2.48 | 1.64 |
| full-price (replaced) | 3961 | — | $1.85M | 2.14 | 1.53 |

The 20m-EMA breakout is a **MIDDLE tier**: it BEATS full-price (clip 1.64 vs 1.53, on ~half the trips — a
cleaner simplification), but is well SHORT of the session-high breakout (1.64 vs 2.01, win 34% vs 46%). A new
SESSION high = strongest all day (rare, strong); a new 20m high = just above 20m ago (frequent, weak).

### Timer-length sweeps — the new-high effect DISSIPATES past ~10 bars for BOTH

**Session-high breakout:**

| bars< | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 10 | 1097 | 46.0% | $977k | 2.93 | **2.01** |
| 15 | 1281 | 45.3% | $1.03M | 2.78 | 1.95 |
| 20 | 1456 | 44.8% | $1.14M | 2.73 | 1.88 |
| 25 | 1630 | 44.6% | $1.22M | 2.67 | 1.85 |
| 30 | 1861 | 43.0% | $1.27M | 2.50 | 1.76 |

**20m-EMA breakout:**

| bars< | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 1 | 1478 | 34.2% | $950k | 2.48 | 1.64 |
| 5 | 1886 | 35.0% | $1.18M | 2.47 | 1.65 |
| 10 | 2263 | 35.7% | $1.42M | 2.49 | 1.65 |
| 15 | 2553 | 35.5% | $1.51M | 2.39 | 1.61 |
| 20 | 2803 | 36.1% | $1.62M | 2.36 | 1.62 |
| 25 | 3066 | 36.1% | $1.70M | 2.30 | 1.59 |
| 30 | 3376 | 36.0% | $1.80M | 2.24 | 1.57 |

⭐ **For BOTH books, clip PF is roughly FLAT through ~10 bars, then decays** — the new-high effect dissipates
past ~10 bars regardless of which high. The books differ only in their PLATEAU LEVEL (session ~2.0, 20m ~1.65)
and how fast they decay after (session steeper: 2.01→1.76 over 10→30; 20m gentler: 1.65→1.57). So the extra
net from a longer window is bought with real PF in BOTH — **bars<10 is the right cap** (past it you chase
stale breaks). The earlier "20m gives free net" impression was the flat plateau THROUGH 10 (net rises there at
constant PF); past 10 it decays like the session book. Net-max corners: session $1.27M@30 (clip 1.76), 20m
$1.80M@30 (clip 1.57) — both worse PF than their bars<10, so not worth it.

### Artifacts (F9–F10)

- 3-way attribution: `/tmp/{vp,bv_gate,fp}_vc*.csv`. Timer sweeps: `/tmp/bs_n*.csv` (session), `/tmp/b20_n*.csv` (20m).
- `BreakoutTimer` class in `Intraday.fs`; `bars_since_20m_breakout` recorded in the CSV.

## Finding 11 — 60m-EMA-high breakout: the trailing-high WINDOW is a quality dial (20m < 60m < session)

Added a third breakout feature: `emaHigh60 = MaxMa(60)` of the 9-EMA + a third `BreakoutTimer` (Starts on a
fresh trailing-60m EMA high, same reset cycle). `bars_since_60m_breakout` recorded; gate
`--max-bars-since-60m-breakout`. Sweep (gate, vc0, no-ps, no-sum6, 2020+):

| bars< | trips | win% | net | raw PF | clip PF |
|---|---:|---:|---:|---:|---:|
| 1 | 1060 | 43.2% | $961k | 2.95 | **1.96** |
| 3 | 1173 | 43.4% | $1.02M | 2.90 | 1.97 |
| 5 | 1283 | 43.2% | $1.05M | 2.81 | 1.94 |
| 10 | 1532 | 43.0% | $1.23M | 2.80 | 1.90 |
| 15 | 1764 | 42.2% | $1.29M | 2.67 | 1.85 |
| 20 | 1980 | 42.1% | $1.43M | 2.66 | 1.82 |
| 25 | 2177 | 41.8% | $1.50M | 2.58 | 1.78 |
| 30 | 2428 | 40.8% | $1.54M | 2.43 | 1.70 |

### The breakout family, ordered by high-window (bars<10)

| feature | win% | clip PF | trips |
|---|---:|---:|---:|
| 20m high | 35.7% | 1.65 | 2263 |
| **60m high** | 43.0% | **1.90** | 1532 |
| session high | 46.0% | 2.01 | 1097 |

⭐ **The trailing-high WINDOW is a QUALITY DIAL: the rarer/stronger the "high", the better the book.** And it's
NONLINEAR with diminishing returns: 20m→60m gains +0.25 clip PF, but 60m→session (a full-day window) gains
only +0.11 — **~85% of the 20m→session quality gap is captured by 60m.** At bars<1 the 60m raw PF (2.95) even
MATCHES the session book (2.93), on comparable capacity (1060 vs 1097 trips) — so the 60m high at bars<1–3 is
a genuine session-quality alternative with slightly different selection.

**Universal ~10-bar dissipation confirmed.** All THREE books (20m/60m/session) show the same profile: clip PF
roughly FLAT through ~10 bars, then decays (60m: 1.96→1.90→1.70 at 1/10/30). The new-high freshness matters
for ~10 bars then goes stale — independent of which trailing window defines the high. **bars<10 is the cap for
all three.**

### Artifacts (F11)

- 60m-breakout timer sweep: `/tmp/b60_n*.csv`. Third `BreakoutTimer` in `Intraday.fs`;
  `bars_since_60m_breakout` recorded in the CSV.

## Finding 12 — vc × breakout-window matrix: the two dials, with raw PF, clip PF, net, and yearly stability

Full sweep: the 3 breakout windows × the vc = n/(n+1) ladder (n∈{0,½,1,2,3,4} → vc {0, .333, .5, .667, .75,
.8}), all at bars<10 (the confirmed cap), gate, no-ps, no-sum6, 2020+. Reporting raw PF / clip PF / net.

**SESSION high:**

| vc | trips | net | raw PF | clip PF |
|---|---:|---:|---:|---:|
| 0 | 1097 | $977k | 2.93 | 2.01 |
| 0.333 | 986 | $887k | 2.91 | 2.01 |
| 0.5 | 845 | $820k | 3.06 | 2.11 |
| 0.667 | 557 | $546k | 2.94 | 1.97 |
| 0.75 | 363 | $445k | 3.56 | 2.31 |
| **0.8** | 244 | $360k | **4.59** | **2.90** |

**60m high:**

| vc | trips | net | raw PF | clip PF |
|---|---:|---:|---:|---:|
| 0 | 1532 | $1.23M | 2.80 | 1.90 |
| 0.333 | 1391 | $1.17M | 2.87 | 1.94 |
| 0.5 | 1161 | $1.08M | 3.02 | 2.03 |
| 0.667 | 746 | $715k | 2.94 | 1.91 |
| 0.75 | 464 | $549k | 3.56 | 2.26 |
| **0.8** | 304 | $423k | **4.11** | **2.52** |

**20m high:**

| vc | trips | net | raw PF | clip PF |
|---|---:|---:|---:|---:|
| 0 | 2263 | $1.42M | 2.49 | 1.65 |
| 0.333 | 1617 | $1.21M | 2.66 | 1.73 |
| 0.5 | 1124 | $952k | 2.84 | 1.78 |
| 0.667 | 553 | $598k | 3.28 | 1.95 |
| 0.75 | 287 | $444k | 4.40 | 2.51 |
| **0.8** | 181 | $333k | **5.04** | **2.71** |

**Reads:**
1. **Window ordering (session > 60m > 20m) holds at every vc on CLIP PF, but compresses as vc rises** — the
   trailing-high window matters MOST at low vc; at high vc, volume dominates and the window matters less.
2. ⭐ **On RAW PF the 20m high WINS the tail (vc0.8: 20m 5.04 > session 4.59 > 60m 4.11) — INVERTING the clip
   ordering.** The 20m+high-vc book has BIGGER but FEWER winners the +50% clip caps away: raw 5.04 → clip 2.71
   (1.86× gap, the widest cell), vs session 4.59 → 2.90 (1.58×, tightest). So **session = broadest/most robust
   edge; 20m = most tail-dependent (explosive winners).** This is exactly why we track raw AND clip.
3. **Same shallow-U in vc for all three** (dead zone 0.333–0.667, climbs hard to 0.8); net decreases
   monotonically with vc everywhere.

### Yearly stability (raw / clip PF) — all 6 headline cells are ALL-WEATHER (positive every year)

**2021 (adverse regime), clip PF:** session-A+ **2.32** > 60m-A+ 1.72 > 20m-A+ 1.48. The window ordering holds
MOST STRONGLY in the hard year — the session high is the most 2021-robust, the 20m the least.

| cell | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL clip / raw / net |
|---|--|--|--|--|--|--|--|--|
| **session·vc0.8** | 1.62 | **2.32** | 4.84 | 4.86 | 2.75 | 4.12 | 2.58 | 2.90 / 4.59 / $360k |
| 60m·vc0.8 | 1.31 | 1.72 | 5.78 | 9.71 | 2.28 | 3.05 | 2.33 | 2.52 / 4.11 / $423k |
| 20m·vc0.8 | 2.59 | 1.48 | 3.11 | 6.43 | 1.99 | 3.62 | 3.44 | 2.71 / 5.04 / $333k |
| session·vc0 | 2.03 | 1.56 | 1.85 | 5.32 | 2.04 | 2.37 | 1.40 | 2.01 / 2.93 / $977k |
| 60m·vc0 | 1.77 | 1.49 | 1.92 | 4.83 | 1.73 | 2.20 | 1.73 | 1.90 / 2.80 / $1.23M |
| 20m·vc0 | 1.95 | 1.31 | 1.31 | 3.79 | 1.44 | 1.93 | 1.39 | 1.65 / 2.49 / $1.42M |

(clip PF shown per year.) The **20m·vc0.8 raw-PF lead (5.04) is TAIL-DRIVEN & concentrated** — mostly 2020
(raw 5.94) and 2023 (12.53); its 2021 (2.13) and 2024 (4.19) are ordinary. **session·vc0.8 is the steadiest
year-to-year** and best in the adverse regime → the most trustworthy A+ despite a slightly lower raw PF. The
`n/(n+1)` monotonicity + all-weather stability across BOTH the window and vc dials makes these features easy
to trust.

### Artifacts (F12)

- vc × 3-window matrix: `/tmp/vw_{sess,b60,b20}_vc*.csv`. Yearly source via `scratchpad/breakdown.py`.
