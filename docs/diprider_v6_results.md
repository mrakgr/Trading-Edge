# DipRiderV6 — results

`TradingEdge.DipRiderV6` (branch `diprider-v5-scalp`). **Intraday MEAN REVERSION.** Forked from V5 and
debloated to a bare MR core plus a post-hoc feature recorder, 2026-07-17.

> **User:** *"Momentum trading just keeps failing us persistently intraday, so we'll refocus on intraday
> mean reversion."*

**Read `docs/lookahead_protocol.md` first.** V6 inherits V5's live-safe universe (`dv_0945 ≥ $5M` AND
`rvol_0945_honest ≥ 1`). Every V4/V5 momentum threshold is treated as VOID — they were fitted for momentum
against a contaminated universe, and V5 F4 proved at least one V4 "dominant lever" **inverts** once the
leak is removed.

**Standing conventions:** LONG only. **2020-01-01 → 2026-06-30.** Raw MOC PF. $10k notional/trip.

---

## The system — all of it

| | |
|---|---|
| **ENTRY** | close ≤ the **strictly-prior** N-minute MIN of 1m-bar **CLOSES** (N=20 default), AND close > VWAP |
| **EXIT** | close ≥ the **strictly-prior** 20m MAX of closes (`target`), or MOC. **NO STOP.** |
| **GATE** | 20m log-ATR ≥ 0.013. That is the only one. |
| **UNIVERSE** | `dv_0945 ≥ $5M` AND `rvol_0945_honest ≥ 1` (both live-safe, both complete at 09:45) |

**What was DELETED (not disabled):** the arm/re-arm state machine, all three EMA-high breakout timers, the
per-window `vol_climb` floors, `sum6`, the price-slope gate, the stop-distance floor, the vol-stop scalp
exit, `VolAsGate`, and every V4-fitted entry gate (`chg1d`/`chg3d`/`ema-vs-vwap`/`tightness`).
**V5: 1,365 lines → V6: 1,065**, with a far richer feature set *added*.

**CLOSES, not high/low wicks (user):** a wick low is noise a limit may never trade at; a close is a price
the tape printed and held. ⚠ Both windows are read **strictly-prior** — if the current close were inside
its own window, `close <= N-min` would be **trivially true on every bar**.

**Knowability guard (R3):** `--entry-start-min < 585` (09:45) now **fails loudly**. `dv_0945` and
`permin15m` are only determined at 09:45; entering earlier silently makes both lookaheads.

---

## ⭐ V6 IS A SAMPLER, NOT A BOOK

`MaxConcurrent = 0` by design (user). With no arm/re-arm throttle, every consecutive new low opens another
position — **it averages down**, which is not tradable (unbounded capital).

**The point is that it removes PATH DEPENDENCY.** Every (bar, feature-vector) that fires becomes an
**independent row** with its own forward outcome, so the whole study is post-hoc SQL over one CSV instead
of an engine re-run per parameter change.

> ⚠ **PF/net on the raw V6 book are ATTRIBUTION numbers, NOT portfolio numbers.** Any cell the analysis
> selects MUST be re-run at `--max-concurrent 1` before it is believed as a tradable result.

---

## Finding 1 — ⭐⭐ THE EDGE IS IN THE BLEEDERS, NOT THE FIRST DIP

**The reset counters (user's idea) — the whole reason V6 exists.** Adopted from the old DipRider reset
machinery but **inverted**: the original reset on every new LOW; V6 **arms on a new low and resets on a new
20m HIGH**.

- `bars_since_first_low` — increments **every bar** while a leg is open.
- `lows_since_first_low` — increments **only on each further new low**. `0` = this IS the first low of the
  leg; `N` = the (N+1)th low, i.e. **averaged down N times**.

Together they separate *how deep into the averaging-down sequence* a trade is from *how stale the leg is* —
different questions a single counter conflates. **This makes the averaging-down MEASURABLE instead of a
bias to suppress.**

**The result (27,629 trips, 2020-2026):**

| lows into the leg | n | win% | avg %/tr | **PF** | net |
|---|---|---|---|---|---|
| **0 — the FIRST low** | 10,113 | 62.3 | 0.447 | **1.239** | +$451,714 |
| 1 | 6,465 | 64.2 | 0.630 | 1.354 | +$407,415 |
| 2 | 4,167 | 66.6 | 0.861 | 1.505 | +$358,906 |
| 3–4 | 4,298 | 69.0 | 1.143 | 1.715 | +$491,444 |
| **5–7** | **2,010** | **71.2** | **1.435** | **⭐ 1.872** | +$288,467 |
| 8–12 | 524 | 71.4 | 0.738 | 1.311 | +$38,684 |
| **13+** | 52 | 65.4 | **−3.121** | **0.513** | −$16,227 |

**A HUMP, not a ramp** — monotone up to 5–7, then it rolls over and **dies** at 13+. (R8: sweep the whole
range; three monotone points are not a gradient. The smoke test's 8+ collapse was real, not noise.)

**The reading:** **one new low is noise; five consecutive new lows in a name STILL ABOVE VWAP is forced
selling, and that snaps back.** Past ~12 new lows it is not a dip — it is a real downtrend, and you are
catching a falling knife at −3.1%/trade. That is the natural boundary between "dip" and "broken".

**⭐ This reframes V5 F6 entirely.** `mc=1` takes the **first** low of each leg and blocks the rest — i.e.
it **systematically selects the WORST cell (PF 1.239)**. So V5 F6's 1.429 → 1.285 drop was NOT "the extra
trips were junk"; it was **"the good trips were the ones being blocked."**

⚠ **The tradability tension:** the best cell is the one that requires holding a loser while adding to it,
and there is still no stop (V5 F6: p01 = −17.8%). **But a 5–7-lows entry is reachable WITHOUT averaging
down** — simply wait for the 5th low and take one position. That is the obvious next test.

---

## Finding 2 — the V6 baseline (the sampler)

| | value |
|---|---|
| candidates | 86,054 |
| trips | 27,629 |
| win rate | 65.3% |
| avg %/trip | **+0.731%** |
| net | +$2,020,404 |
| PF | **1.410** ⚠ attribution only (mc=0) |

---

## New primitives (both shared, in `TradingEdge.RollingMa`)

**`CumStdMa`** — session-cumulative mean/σ via **Welford's online algorithm**, for the VWAP-distance
z-score (user's idea). Welford rather than Σx/Σx²: the naive form loses precision catastrophically when the
mean is large relative to the variance — **exactly the `dist_vwap` case** (values cluster tightly near 0).
Verified: z mean **−0.21**, range [−1.9, +1.4], 0 nulls. *Correctly negative — MR buys dips, so entries sit
below the session's average VWAP-distance.*

**`AdxMa`** — Wilder ADX(14) + `+DI`/`−DI` on 1m bars. **Direction-AGNOSTIC trend STRENGTH.** Every prior
trend feature in this codebase (price slope, sum6) was **directional**; nothing measured *how strongly
trending* the tape is. **The MR thesis it exists to test:** reversion should work BEST in chop (low ADX)
and WORST in a genuine trend (high ADX), where "the dip" is just the start of more downside.
Verified: median **24.6**, range 7.7–61.5, 0 nulls (textbook: ADX>25 conventionally = trending).

**Counter verification:** 0 negatives; `bars ≥ lows` in **all** rows (the invariant holds); lows 0–12,
bars 0–48 on the smoke test.

---

## Recorded features (none of them gate — slice these in SQL)

`bars_since_first_low`, `lows_since_first_low`, `dist_vwap`, `dist_vwap_z`, `chg_1d`, `chg_3d`,
`pct_chg_since_open`, `log_atr_20`, `adx_14`, `plus_di_14`, `minus_di_14`,
`price_slope_{open,60,20}`, `vol_slope_{open,60,20}`, `bar_vol`, `brv_15m`, `rvol_5m`, `vol_climb`,
`cum_vol`, `dv_0945`.

---

## Status / next

⏭ **In order:**
1. **The 5-lows-without-averaging-down test** — F1's best cell (PF 1.872) reachable as a real mc=1 book:
   wait for the 5th consecutive new low, take ONE position.
2. **`--entry-low-window 60`** (buy the 60m low, sell the 20m high) — the user's PF-raising idea, untested.
3. **The rest of the attribution**: ADX (the low-ADX/chop thesis), the z-score, the six slopes, `chg_1d/3d`,
   volume. All recorded; all pure SQL.
4. **A real stop.** p01 = −17.8% with none. The 9-EMA stop is structurally wrong here (V5 F6: PF
   1.429 → 1.164) — MR wants a stop BELOW the entry, not an EMA that sits above it.
5. **2020/2021 were cost-negative at mc=1** in V5 F6 (+0.12%/tr). Re-check under the F1 cell selection —
   the bleeder cells may not share that weakness.

**Cross-system context (2026-07-17):**

| system | verdict |
|---|---|
| **LowFlyer** | ✅ **CLEAN + TRADABLE** — production PF **3.093** at mc=1, +3.13%/tr, 767 trips (~118/yr). Rare, fat, **cost-insensitive**. |
| **DipRiderV6 (MR)** | ✅ real but **thin** — PF 1.24–1.87 by cell, +0.4–1.4%/tr, 27,629 trips. Frequent, **cost-sensitive**. |
| MaxFlyerV3 | ⚠️ unconfirmed (`brv20d`) |
| VwapReclaimV3 / OpeningDriverV2 / DipRiderV4 | ❌ dead |

**LowFlyer is the better SYSTEM; the MR book is the better CAPACITY.**
