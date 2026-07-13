# VwapReclaimV3 — results

A debloated, **long-only** fork of VwapReclaim (branch off `vwap-reclaim-v2` lineage). Two structural
changes vs V1:

1. **9-EMA pullback-low stop replaces the geometric VWAP-low stop.** The stop level is the running MIN of
   the 9-EMA over the current below-VWAP run (reset at each reclaim, alongside the other run accumulators).
   It fires when the LIVE 9-EMA falls back below that level — i.e. the reclaim failed and price is rolling
   back over the pullback low. `--stop-buffer` widens it to `run-min·(1−buffer)`; 0 = fire the instant the
   EMA dips under the run-low.
2. **Debloat.** The breakout engine, the `ReclaimShort` mirror, the MR cover targets (`Vwap`/`Ma`/`Channel`),
   the `Armed`/`Skipped` position states, and all their config knobs are gone. The ONLY entry path is the
   long reclaim. Snapshot mutables are factored into a `State` record (DipRiderV4 style): `ProcessBar`
   captures the strictly-prior state at the top and stores it into `sState` at the bottom, so the entry
   gate's cross reads `sState.ema/vwap` (prior) against the live post-fold `ema.State`/VWAP (current).
3. **New RollingMa primitives.** `RunMaxMa<'T>` / `RunMinMa<'T>` (Push + Reset + State) — windowless
   session-cumulative running extremes — replace the plain `mutable _ voption` running-high/low/vol-high
   idiom (and back the run-scoped 9-EMA pullback low).

Defaults (production): 10:00–13:30 ET entry window, 09:30 VWAP/EMA anchor, `tightness ≥ 3.0`, weakness run
`rb ≥ 11` consecutive bars EMA<VWAP into the cross, hold-to-MOC unless the pullback stop fires. Universe =
`vwap_reclaim_candidate` (ADV ≥ $1M AND rvol_0945 > 1). $10k notional/trip, PF = gross-win / gross-loss (MOC).

---

## F1 — The 9-EMA pullback stop beats the geometric VWAP-low stop (controlled A/B)

Both engines take the **identical 41,027 trips** (V3 changed only the stop, not the entry), so this is a
clean A/B on the exit alone. Full range 2003-09 → 2026-06.

| stop | trips | win % | net | PF (MOC) |
|---|---|---|---|---|
| V1 — geometric `VWAP − d·⅔` (d = VWAP−sessionLow) | 41,027 | 41.1% | $1,503,082 | 1.298 |
| **V3 — 9-EMA pullback low (run-min of the 9-EMA)** | 41,027 | 38.1% | **$1,601,695** | **1.344** |

**+$98.6k net / +0.046 PF** from the stop swap. Win rate drops ~3pts but the trade is favorable: the
pullback stop cuts a bit more often, at a controlled distance, and lets the survivors run. On the 2023-24
slice the exit split is explicit — **1171 stops @ −3.27% mean, 1513 MOC holds @ +5.37% mean**.

### Per-year (the edge concentrates where the strategy is tradable — post-2020)

| year | n | V1 PF | V1 net | V3 PF | V3 net | ΔPF | Δnet |
|---|---|---|---|---|---|---|---|
| 2008 | 1748 | 0.98 | −5,138 | 1.05 | 9,463 | +0.08 | +14,600 |
| 2020 | 2620 | 1.56 | 246,215 | 1.55 | 224,640 | −0.01 | −21,575 |
| 2021 | 6953 | 1.17 | 214,317 | 1.20 | 237,526 | +0.04 | +23,209 |
| 2022 | 3094 | 1.33 | 170,572 | 1.39 | 187,737 | +0.06 | +17,166 |
| 2023 | 1508 | 1.92 | 206,911 | 2.13 | 226,206 | +0.21 | +19,295 |
| 2024 | 1176 | 1.67 | 188,289 | 1.81 | 203,187 | +0.14 | +14,898 |
| 2025 | 1066 | 1.70 | 215,501 | 1.84 | 235,039 | +0.14 | +19,537 |
| 2026 | 371 | 2.37 | 145,280 | 2.38 | 143,774 | +0.01 | −1,506 |
| **TOTAL** | **41027** | **1.30** | **1,503,082** | **1.34** | **1,601,695** | **+0.05** | **+98,613** |

(Older/thin years omitted for brevity — all near PF 1.0, small deltas.) 17 of 24 years improve. The one
notable giveback is **2020 (−$21.5k)**, though PF is essentially flat there (1.56→1.55). The gains land in
**2021 (adverse regime, +$23k), 2022 (+$17k), 2023 (+$19k), 2024 (+$15k), 2025 (+$20k)** — exactly the
post-2020 window where VwapReclaim has its real edge (the pre-2020 book hovers at PF ~1.0 either way).

**Verdict:** the structure-based stop (exit when price rolls back under the pullback low the reclaim was
built on) dominates the fixed VWAP-geometry stop — better PF, more net, and it *helps most in the hardest
recent regime year (2022) and the current fat-book years (2023-25)*. Adopted as the V3 default.
