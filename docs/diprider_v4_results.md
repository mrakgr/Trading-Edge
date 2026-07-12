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
