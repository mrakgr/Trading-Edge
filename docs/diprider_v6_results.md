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

> 🛑 **F5–F13 were run with the liquidity filter OFF (`--min-dv-0945 0`).** F14 shows that inflates PF —
> the low-liquidity cells are **penny stocks** (median entry **$1.13** below $250k of morning volume) whose
> spread would eat the whole edge. **The `dv_0945 >= $3M` floor is now the default.** Every F5–F13 number is
> therefore **optimistic**; the ones re-checked under the floor (F8, F10, F13) **survive with a haircut** —
> see F14. Treat any un-rechecked F5–F13 magnitude as provisional.

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

## Finding 3 — ⭐⭐ THE BLEEDER CELL IS TRADABLE: "wait for the Kth low", one position per leg

F1 showed the edge is in the bleeders but the best cell *looked* like it required averaging down. **It does
not.** Instead of taking every new low, **wait until the leg has made K consecutive new lows and take
EXACTLY ONE position** (the leg is then consumed until a 20m high resets it). That is a real mc=1-style
book — no averaging down, bounded capital.

**20m-low entry, the Kth-low sweep (one position/leg, 2020-26):**

| wait for the Kth low | n | win% | avg %/tr | **PF** | net |
|---|---|---|---|---|---|
| 0 (take the first dip) | 10,495 | 62.6 | 0.468 | 1.255 | $491,590 |
| 1 | 6,722 | 64.4 | 0.646 | 1.371 | $433,913 |
| 2 | 4,356 | 66.8 | 0.868 | 1.520 | $378,250 |
| 3 | 2,793 | 69.1 | 1.112 | 1.719 | $310,622 |
| 4 | 1,754 | 69.3 | 1.215 | 1.771 | $213,047 |
| **5** | **1,068** | **70.5** | **1.479** | **⭐ 1.968** | $157,958 |
| 6 | 666 | 71.0 | 1.380 | 1.875 | $91,895 |
| 8 | 246 | 72.4 | 1.203 | 1.639 | $29,582 |

**PF peaks at K=5 (1.968) — HIGHER than the sampler's 1.872 for the same cell**, because taking only the
FIRST qualifying entry avoids the deeper, worse fills. The hump sits in the same place as F1's independent
measurement: **two different selection rules agreeing on the optimum is real structure, not a fitting
artifact.**

**The economics improve as you wait:** avg/trade 0.47% → 1.48% (3×), win 62.6% → 70.5%. At +1.48% the
~0.1% round-trip cost falls from 21% of the edge to **7%**.

**⭐ ENGINE-VERIFIED (not just a post-hoc slice).** Wired as `--min-lows-into-leg` + a `legConsumed` latch
cleared by the same 20m-high reset. Engine vs post-hoc SQL at K=5:

| | trips | win% | avg %/tr | PF |
|---|---|---|---|---|
| post-hoc SQL | 1,068 | 70.5 | 1.479 | 1.968 |
| **ENGINE** (`--min-lows-into-leg 5 --max-concurrent 1`) | **1,071** | **70.5** | **1.477** | **1.967** |

Two independent implementations agree. (The 3-trip gap is the mc=1 slot interacting with legs that overlap
a 20m-high reset — correct engine behaviour the SQL proxy cannot model.)

**Per-year (20m-low K=5) — and it FIXES V5 F6's weak years:**

| yr | n | win% | avg %/tr | PF |
|---|---|---|---|---|
| 2020 | 149 | 71.8 | 1.725 | 2.244 |
| 2021 | 170 | 61.8 | 0.396 | **1.220** |
| 2022 | 141 | 70.9 | 1.566 | 2.484 |
| 2023 | 164 | 76.2 | 2.259 | **3.108** |
| 2024 | 217 | 72.4 | 1.782 | 1.991 |
| 2025 | 175 | 70.3 | 1.192 | 1.652 |
| 2026 | 52 | 69.2 | 1.322 | 1.812 |

V5 F6's mc=1 book had **2020 AND 2021 both at 1.077 and cost-negative** (+0.12%/tr). K=5 turns 2020 into
**2.244 / +1.73%**. Only 2021 stays soft (1.220 / +0.40% — marginal after costs). Trip counts are stable
across years (141–217), so the structure is not a regime artifact.

---

## Finding 4 — the 60m-low entry (user's idea): it RAISES PF at every K — but it is LESS ROBUST

Ran the sampler at `--entry-low-window 60` (a deeper, rarer dip), then sliced post-hoc. **The ungated 60m
book already beats the ungated 20m one: PF 1.719 / +1.22%/tr vs 1.410 / +0.73%.**

**60m beats 20m on PF at EVERY K:**

| K | 20m-low PF (n) | **60m-low PF (n)** |
|---|---|---|
| 0 | 1.255 (10,495) | **1.526** (977) |
| 1 | 1.371 (6,722) | **1.836** (547) |
| 3 | 1.719 (2,793) | **2.021** (203) |
| 4 | 1.771 (1,754) | **2.145** (116) |
| 5 | 1.968 (1,068) | **2.138** (65) |
| 6 | 1.875 (666) | **2.195** (44) |

**The mechanism is sound** — a 60m low is a deeper, rarer, more meaningful dip, so each entry is
higher-quality. **But the headline PF hides two problems:**

### ⚠ The candidate books, with the WORST YEAR shown

| cell | n | /yr | win% | avg % | **PF** | net | **worst yr PF** |
|---|---|---|---|---|---|---|---|
| 20m-low K=0 | 10,495 | 1615 | 62.6 | 0.468 | 1.255 | $491,590 | 1.018 |
| **20m-low K=3** | **2,793** | **430** | 69.1 | 1.112 | **1.719** | **$310,622** | **⭐ 1.296** |
| 20m-low K=5 | 1,068 | 164 | 70.5 | 1.479 | **1.968** | $157,958 | 1.220 |
| 60m-low K=0 | 977 | 150 | 70.6 | 0.953 | 1.526 | $93,086 | 1.029 |
| 60m-low K=1 | 547 | 84 | 71.5 | 1.273 | 1.836 | $69,612 | **0.892** ❌ |
| 60m-low K=3 | 203 | 31 | 69.5 | 1.468 | 2.021 | $29,794 | 1.027 |

1. **The 60m books are NOT all-weather.** 60m K=1 **LOSES money in 2021 (PF 0.892)**; 60m K=0 is 1.029 and
   K=3 is 1.027. Their higher PF is partly **regime concentration** — a smaller, sharper sample leaning
   harder on the good years — not purely better selection.
2. **31–84 trips/year is too thin** to separate skill from luck.

### ⭐ VERDICT: the production candidate is **20m-low K=3**, not the highest-PF cell

- **PF 1.719 / +1.11%/tr / 430 trips per year / worst year 1.296** — the **best worst-year in the table**.
  It never has a bad year.
- It nets **2× K=5's dollars** ($311k vs $158k): above PF ~1.7, **capacity matters more than PF**.
- At +1.11%/trade, costs (~0.1%) are **~9% of the edge** — comfortably cost-insensitive.

**K=5 has the prettier PF; K=3 has the better business.** Optimising PF blind would have picked 60m K=3
(PF 2.021) — the cell with 31 trips/yr and a 1.027 worst year.

⏭ **Owed:** the 60m/20m difference may be a *sizing* signal rather than a selection one (60m lows are rarer
AND better ⇒ size up on them within the 20m book). Test as an overlay, not a replacement.


---

## Finding 5 — 🛑 THE SAMPLER WAS NOT SAMPLING: three gates were baked in (and one of them was WRONG)

**User:** *"Wait, the VWAP filter was on? What other filter was on in addition to it? I thought the plan was
to apply all the filters posthoc."*

**Correct, and this invalidated the premise of the design.** V6 was declared a sampler, but it was still
GATING three things — so those rows were never emitted and could not be sliced:

| gate | was | in the CSV? |
|---|---|---|
| `close > VWAP` | **ON** (inherited from V5 F6) | ❌ **NO** — every `dist_vwap` was positive-only |
| `log_atr_20 >= 0.013` | **ON** (inherited from V5) | ✅ recorded, but un-loosenable below 0.013 |
| `rvol_0945_honest >= 1` | **ON** (baked into the candidate table's WHERE) | ❌ **NO** — not even a column |

**Fixed:** `RequireAboveVwap = false` and `MinAtrPct = 0.0` are now the sampler defaults;
`rvol_0945_honest` is a recorded column; a new `diprider_v6_candidate` table is built with `--min-rvol 0`.
`--require-above-vwap` now turns the gate ON (for building a production book), rather than `--no-vwap-filter`
turning it off. **A sampler must RECORD, never GATE.**

**The true sampler: 10,725,547 trips** from 392,815 candidate-days (vs 27,629 gated). PF 1.283 / +0.116%/tr.
*That* is the scale the universe filters were operating at — and it answers the user's surprise at "only 977
60m lows in 6.5 years": the `dv_0945 ≥ $5M` + `rvol_0945_honest ≥ 1` universe was doing enormous work.

---

## Finding 6 — ⭐⭐ BUYING BELOW VWAP IS BETTER — V5 F6's "load-bearing" VWAP filter was an ARTIFACT

**User's question:** *"is buying above VWAP better than buying below?"*

**No. Below is better — on both PF and avg/trade — and it is 76% of the opportunity.**

| side | n | % of book | win% | avg %/tr | **PF** |
|---|---|---|---|---|---|
| ABOVE VWAP | 2,576,658 | 24.0 | 64.9 | 0.0822 | 1.244 |
| **BELOW VWAP** | **8,148,889** | **76.0** | 65.1 | **0.127** | **1.292** |

**🛑 V5 F6 was WRONG, and the error is instructive.** It reported `close > VWAP` as "LOAD-BEARING (PF
1.429 → 1.319)". But that test compared *above-VWAP* against *both sides MIXED* — **that is not an
ablation, it is a comparison against a diluted average.** The real A/B is above vs below, and **below
wins**. V6's default was built on a conclusion that had never been tested. This is precisely the failure the
sampler design exists to prevent, which is why F5 mattered.

### The sweep (R8 — the whole range, not the binary): a U-SHAPE the binary split HID

| dist_vwap | n | avg %/tr | **PF** |
|---|---|---|---|
| **< −5%** | 670,764 | **0.431** | 1.341 |
| **−5..−3%** | 916,655 | 0.221 | **⭐ 1.353** |
| −3..−2% | 1,128,085 | 0.143 | 1.304 |
| −2..−1% | 2,081,440 | 0.093 | 1.256 |
| −1..−0.5% | 1,547,321 | 0.063 | 1.225 |
| **−0.5..0%** | 1,804,623 | 0.050 | **1.227 ← the TROUGH** |
| 0..+0.5% | 1,172,027 | 0.059 | 1.243 |
| +0.5..1% | 584,814 | 0.079 | 1.255 |
| +1..2% | 503,577 | 0.103 | 1.266 |
| +2..5% | 281,007 | 0.133 | 1.231 |
| > +5% | 35,234 | 0.205 | 1.162 |

**PF is WORST exactly AT VWAP (1.227) and improves in BOTH directions.** The real signal is not "above vs
below" — it is **DISTANCE from VWAP, in either direction**. Being pinned at fair value is the dead zone;
**dislocation** is where reversion lives. The asymmetry favours the downside (below tops out at 1.353 /
+0.431%; above at 1.266).

---

## Finding 7 — ⭐⭐ THE Z-SCORE BEATS RAW DISTANCE (user's idea) — PF 1.580, cleanly monotone

`dist_vwap_z` = the session-cumulative z-score of `dist_vwap` (Welford, `CumStdMa`).

| z bucket | n | avg %/tr | **PF** |
|---|---|---|---|
| **z < −2.5** | 414,851 | 0.331 | **⭐ 1.580** |
| −2.5..−2 | 966,430 | 0.191 | 1.407 |
| −2..−1.5 | 1,962,794 | 0.147 | 1.328 |
| −1.5..−1 | 2,312,530 | 0.095 | 1.218 |
| −1..−0.5 | 1,918,684 | 0.083 | 1.206 |
| −0.5..0 | 1,458,718 | 0.087 | 1.242 |
| 0..+0.5 | 1,041,649 | 0.079 | 1.242 |
| +0.5..1 | 525,471 | 0.074 | 1.250 |
| z > +1 | 124,419 | 0.070 | 1.253 |

**PF 1.580 at z < −2.5 vs 1.353 for the best RAW-distance cell — and cleanly MONOTONE across the whole
negative range (1.206 → 1.580), where raw distance was U-shaped.** Normalising by the session's own
volatility is doing real work: a −3% dip means something very different in a quiet name than in a 15%-ATR
runner, and the z-score knows the difference. **`dist_vwap_z` supersedes `dist_vwap` as the location lever.**

### The two best levers STACK — and z partly SUBSTITUTES for waiting

PF by (lows into leg) × (z bucket):

| lows | **z<−2.5** | −2.5..−1.5 | −1.5..0 | z≥0 | n (z<−2.5) |
|---|---|---|---|---|---|
| **0 first** | **1.545** | 1.390 | 1.290 | 1.268 | 6,533 |
| 1–2 | 1.379 | 1.298 | 1.218 | 1.232 | 27,496 |
| 3–4 | 1.432 | 1.313 | 1.220 | 1.241 | 40,313 |
| 5–7 | **1.547** | 1.357 | 1.221 | 1.233 | 70,217 |
| 8+ | **1.653** | 1.374 | 1.180 | 1.239 | 270,292 |

- **z lifts PF at EVERY depth** (at 5–7 lows: 1.221 → 1.547).
- **⭐ z works even on the FIRST dip: 1.545 at `lows=0, z<−2.5`** — nearly the best deep cell. So **z is a
  SUBSTITUTE for waiting, not merely a complement.** A first-low entry at z<−2.5 is as good as a 5–7-low
  entry at ordinary z, **and it is far more available.** That is a much better basis for a book than "wait
  for the 5th low".
- ⚠ **The 8+ cell reads best here (1.653) but F1 measured 8–12 at 1.311 and 13+ at 0.513 on the GATED
  book.** Different populations (this sampler has no dv_0945/rvol/ATR filters) — **not a contradiction, but
  not comparable either.** Re-check the interaction on a gated book before believing "deeper is always
  better".

⏭ **Next:** ADX (the chop-vs-trend thesis), the six slopes, `chg_1d/3d`, and the volume family — all
recorded, all pure SQL. Then re-derive the production book from `z` + `K` + a re-swept universe.


---

## Finding 8 — ⭐⭐ ATR is the BIGGEST lever, and it INVERTS at the top (a hump with a cliff)

**User:** *"How does ATR % affect the results?"*

**Full 10.7M sampler, no gates (R8 — the whole range):**

| log-ATR20 | n | % of book | win% | avg %/tr | **PF** |
|---|---|---|---|---|---|
| < 0.002 | 4,118,622 | **38.4** | 64.4 | 0.0061 | **1.033 ← dead** |
| 0.002–0.004 | 3,291,766 | **30.7** | 64.0 | 0.0489 | 1.131 |
| 0.004–0.006 | 1,572,880 | 14.7 | 66.2 | 0.1707 | 1.338 |
| 0.006–0.009 | 984,708 | 9.2 | 67.9 | 0.3352 | 1.531 |
| 0.009–0.013 | 409,802 | 3.8 | 67.6 | 0.5303 | 1.572 |
| **0.013–0.02** | 217,387 | 2.0 | 67.0 | 0.7726 | **⭐ 1.583** |
| 0.02–0.03 | 86,342 | 0.8 | 65.3 | 0.8469 | 1.423 |
| 0.03–0.05 | 36,187 | 0.3 | 60.8 | 0.4566 | 1.142 |
| **≥ 0.05** | 7,853 | 0.1 | **52.4** | **−1.6588** | **0.755 ← LOSES MONEY** |

1. **⭐ The bottom 69% of the book is worthless.** ATR < 0.004 = PF 1.03–1.13 at **+0.006–0.049%/trade —
   BELOW transaction costs**. 7.4M of 10.7M trips produce essentially nothing. **This is the single biggest
   filter available.**
2. **V5's 0.013 floor lands on the peak** (0.013–0.02 → 1.583). Lucky: it was fitted on the *contaminated
   momentum* book, yet it hits the MR optimum. But the peak is **broad** (0.009–0.02 all ≈1.57–1.58), so the
   exact number is not load-bearing.
3. **⭐ HIGH ATR INVERTS AND LOSES MONEY.** ≥0.05 → PF 0.755 at **−1.66%/trade**, win 52.4%. **This is the
   MIRROR of V4's momentum finding**, where PF scaled *monotonically* with ATR (V4 F3 called it "THE MAIN
   LEVER"). **Momentum wanted the violent thrust; mean reversion wants RANGE but NOT CHAOS.** Past ~3% the
   name is not oscillating — it is broken.

### The ceiling, located precisely (within `z < −1.5`, `atr >= 0.009`)

| log-ATR20 | n | win% | avg %/tr | PF |
|---|---|---|---|---|
| < 0.025 | 298,972 | 68.5 | 0.868 | **1.795** |
| 0.025–0.030 | 15,228 | 65.8 | 1.135 | 1.510 |
| 0.030–0.035 | 8,684 | 65.0 | 1.305 | 1.529 |
| 0.035–0.040 | 5,306 | 62.3 | 0.799 | 1.249 |
| 0.040–0.050 | 5,390 | 59.4 | 0.353 | 1.090 |
| 0.050–0.070 | 3,424 | 55.4 | **−0.173** | 0.966 |
| ≥ 0.070 | 1,163 | **46.2** | **−5.829** | **0.541** |

**`log_atr_20 < 0.035` is the natural CEILING** — monotone degradation above it, negative avg/trade by 0.05,
catastrophic at 0.07. The win rate is the cleanest read: 68.5% → 46.2%. **V5 had `MaxAtrPct = infinity`
(no ceiling at all) — that is now a known defect.**

---

## Finding 9 — ⭐⭐ ATR × Z ARE INDEPENDENT AND THEY MULTIPLY → PF 2.116, ALL-WEATHER

Neither feature is a proxy for the other — each lifts PF at **every** level of the other:

| ATR | **z<−2.5** | −2.5..−1.5 | z≥−1.5 |
|---|---|---|---|
| < 0.004 (69% of book) | 1.158 | 1.104 | 1.087 |
| 0.004–0.009 | 1.744 | 1.454 | 1.381 |
| **0.009–0.02 (PEAK)** | **⭐ 2.116** (n=47,096) | 1.734 | 1.414 |
| 0.02–0.03 | 1.868 | 1.588 | 1.222 |
| ≥ 0.03 (danger) | 1.028 | 1.142 | **0.915** |

**The joint cell `ATR ∈ [0.009, 0.02) × z < −2.5` = PF 2.116 on 47,096 trips**, from a 1.283 baseline.
**And the danger compounds too: ATR ≥ 0.03 is bad at EVERY z (0.915–1.142) — a deep z-score does NOT rescue
a chaotic name.**

### ⭐ PER-YEAR — and it FIXES 2021, the year that killed every other config

`log_atr_20 ∈ [0.009, 0.035) AND dist_vwap_z < −2.5`:

| yr | n | win% | avg %/tr | **PF** |
|---|---|---|---|---|
| 2020 | 9,193 | 74.7 | 1.835 | **3.043** |
| **2021** | 10,251 | 73.7 | 1.555 | **⭐ 2.943** |
| 2022 | 6,455 | 68.7 | 0.848 | 1.560 |
| 2023 | 7,319 | 70.5 | 1.261 | 1.890 |
| 2024 | 8,831 | 70.5 | 1.537 | 2.192 |
| 2025 | 10,712 | 68.0 | 0.969 | 1.608 |
| 2026 | 4,120 | 66.4 | 0.812 | 1.433 |

**Worst year 1.433; EVERY year ≥ 1.43.** **2021 — which was 1.220 in the K=5 book (F3) and an outright
LOSER (0.892) in the 60m book (F4) — is now 2.943, the second-BEST year.** The ATR×z pair is finding
something genuinely regime-independent that the counter-based selection could not.

**⭐ This supersedes the F3/F4 book direction.** `ATR × z` beats "wait for the Kth low" on PF (2.116 vs
1.968), on robustness (worst year 1.433 vs 1.220), AND on capacity (47k vs 1k sampler trips). The counters
remain a real lever (F1/F3) — but as a *secondary* one, and F7 already showed **z partly substitutes for
waiting**.

⚠ **All mc=0 attribution.** The production book must be re-derived and re-run at mc=1.


---

## Finding 10 — rvol_0945_honest: monotone-ish alone, but it INVERTS against ATR (they are NOT independent)

**User:** *"How does rvol_0945_honest affect results? Does a higher value give bigger gains when controlling
for ATR%?"*

⚠ **Scale reminder:** `rvol_0945_honest` = (premarket-inclusive volume through 09:45) ÷ (prior-20d mean
**daily** volume). So **1.0 means "by 09:45 it has already traded a full normal DAY's volume"** — a very
high bar, not a mild one.

**Marginal (10.7M sampler, no gates):**

| rvol | n | % | win% | avg %/tr | **PF** |
|---|---|---|---|---|---|
| 0.1–0.25 | 5,820,370 | 54.3 | 64.1 | 0.0499 | 1.169 |
| 0.25–0.5 | 1,476,819 | 13.8 | 64.6 | 0.0834 | 1.212 |
| 0.5–1 | 850,265 | 7.9 | 65.4 | 0.1238 | 1.267 |
| **1–2** (the old gate) | 664,236 | 6.2 | 66.3 | 0.1865 | 1.397 |
| **2–4** | 475,303 | 4.4 | 66.8 | 0.2377 | **⭐ 1.460** |
| 4–8 | 317,576 | 3.0 | 67.6 | 0.2782 | 1.455 |
| ≥ 8 | 1,120,978 | 10.5 | 68.1 | **0.3590** | 1.418 |

Monotone to ~2–4, then a plateau/slight roll-off. Note **avg %/trade keeps climbing to ≥8 (0.359%) while PF
flattens** — the high-rvol tail has bigger wins *and* bigger losses.

### ⭐ CONTROLLING FOR ATR — the answer is "only in the mid bands; it FLIPS at high ATR"

PF by (ATR band) × (rvol band):

| ATR band | rvol<0.5 | 0.5–2 | **2–8** | rvol≥8 |
|---|---|---|---|---|
| < 0.004 (dead) | 1.086 | 1.112 | 1.103 | 1.130 |
| **0.004–0.009** | 1.294 | 1.442 | 1.586 | **⭐ 1.622** |
| **0.009–0.02 (peak)** | 1.512 | 1.524 | **⭐ 1.724** | 1.581 |
| **0.02–0.035** | **⭐ 1.891** | 1.487 | 1.622 | 1.286 |
| **≥ 0.035** | **1.416** | 0.985 | 0.850 | 0.930 |

**Where higher rvol HELPS:**
- **ATR 0.004–0.009**: monotone 1.294 → **1.622**. Straightforwardly better.
- **ATR 0.009–0.02**: peaks at rvol 2–8 (**1.724**), then falls at ≥8.

**⭐ Where it INVERTS:**
- **ATR 0.02–0.035**: the BEST cell is **rvol < 0.5 (1.891)**, and PF FALLS as rvol rises (→ 1.286).
- **ATR ≥ 0.035**: same, harder — rvol<0.5 = 1.416, everything above 0.5 LOSES (0.85–0.99).

**The reading: HIGH VOLATILITY *plus* HIGH VOLUME is the failure mode.** A name both moving violently AND
being hammered on volume is not mean-reverting — that is a **real repricing event** (news / halt / offering)
and the dip keeps going. A high-ATR name on **quiet** volume is merely noisy, and it reverts beautifully.
**ATR < 0.004 is flat (~1.10) at EVERY rvol** — confirming F8: that band is dead and no feature rescues it.

### Sample-size honesty (the inversion is real in one band, thin in the other)

| ATR band | rvol band | n | n_syms | win% | avg %/tr | PF |
|---|---|---|---|---|---|---|
| 0.009–0.02 | rvol<0.5 | 141,807 | 3,176 | 65.9 | 0.516 | 1.512 |
| 0.009–0.02 | 0.5–2 | 114,434 | 3,281 | 66.7 | 0.537 | 1.524 |
| 0.009–0.02 | **≥2** | 370,948 | 3,257 | 68.2 | 0.675 | **1.615** |
| 0.02–0.035 | **rvol<0.5** | **8,699** | **1,122** | 67.9 | **1.438** | **⭐ 1.891** |
| 0.02–0.035 | 0.5–2 | 11,834 | 1,441 | 65.9 | 0.959 | 1.487 |
| 0.02–0.035 | ≥2 | 82,238 | 2,447 | 64.3 | 0.729 | 1.331 |
| ≥0.035 | **rvol<0.5** | **1,280** | **304** | 61.3 | 1.482 | **1.416 ⚠ THIN** |
| ≥0.035 | 0.5–2 | 2,049 | 443 | 58.7 | −0.069 | 0.985 |
| ≥0.035 | ≥2 | 24,282 | 1,650 | 57.2 | −0.367 | 0.919 |

- **`ATR 0.02–0.035 × rvol<0.5` (PF 1.891, avg +1.438%/tr) is BELIEVABLE** — 8,699 trips across **1,122
  symbols**, and the highest avg/trade in the table.
- **`ATR ≥0.035 × rvol<0.5` (1.416) is NOT** — 1,280 trips / 304 symbols. It *suggests* F8's "ATR≥0.035 is
  dangerous" holds only for the NOISY subset, but that is not established.
- **The `rvol≥2` crossover IS solid on both sides** (helps at 0.009–0.02 → 1.615 on 371k; hurts at
  0.02–0.035 → 1.331 on 82k; and 0.919 on 24k at ≥0.035).

**⭐ Verdict on the old `rvol_0945_honest >= 1` universe gate: crude but directionally right in the ATR bands
we actually trade — and the WRONG SHAPE.** The right rvol depends on ATR: **want high rvol at low/mid ATR;
want LOW rvol at high ATR.** A flat floor cannot express that.

### Engine change (user, 2026-07-17)

**`MinAtrPct` default 0.0 → 0.004** — a **CAPACITY** floor, not a quality one: purely to cut the sampler to
a manageable size. F8 measured sub-0.004 as 69% of the book at PF 1.03–1.13 / +0.006–0.049%/trade (below
costs), so **nothing of value is discarded**. The QUALITY floor is ~0.009 and the ceiling (~0.035) is still
**not wired** — both stay post-hoc, since `log_atr_20` is recorded.

⏭ Also owed: **parquet output** (user) — 10.7M rows × 41 cols is 6.9 GB of CSV.


---

## Finding 11 — ⭐⭐ chg_1d INVERTS V4: buy the names already DOWN on the day (peak −10..−5%, PF 1.820)

**User:** *"How does chg_1d affect results in the [0.004, 0.035] band? Let's make that band the default."*

**Engine change:** the ATR band `[0.004, 0.035)` is now the default — `MinAtrPct = 0.004` (capacity, F8) and
**`MaxAtrPct = 0.035` (quality, F8 — V5 had NO ceiling, `infinity`)**. All numbers below are inside it.

**chg_1d swept over the WHOLE range, both signs (R8):**

| chg_1d | n | % | win% | avg %/tr | **PF** |
|---|---|---|---|---|---|
| **< −20%** | 163,386 | 5.0 | 63.7 | 0.113 | **1.079 ← a falling knife** |
| −20..−10% | 324,600 | 9.9 | 66.5 | 0.392 | 1.464 |
| **−10..−5%** | 563,151 | **17.1** | **69.2** | **0.428** | **⭐ 1.820** |
| **−5..−2%** | 505,931 | **15.4** | **69.4** | 0.355 | **1.767** |
| −2..0% | 302,403 | 9.2 | 67.3 | 0.268 | 1.525 |
| 0..+2% | 317,741 | 9.7 | 67.4 | 0.278 | 1.511 |
| +2..5% | 343,427 | 10.4 | 65.7 | 0.258 | 1.427 |
| +5..10% | 329,265 | 10.0 | 64.7 | 0.281 | 1.405 |
| **+10..20%** | 254,751 | 7.7 | 64.5 | 0.330 | **1.374 ← where V4 forced every trade** |
| +20..50% | 139,443 | 4.2 | 64.5 | 0.408 | 1.317 |
| ≥ +50% | 43,450 | 1.3 | 63.3 | 0.239 | 1.105 |

**A hump: peak at −10..−5% (1.820), monotone decline all the way to +50% (1.105), and a collapse below
−20% (1.079).**

**🛑 THIS DIRECTLY INVERTS V4's `MinChg1d = +10%`** — which V4 called *"An ESSENTIAL entry requirement
(user)"*. That gate forces the **+10..20% bucket at PF 1.374 — one of the WORST cells** — and excludes the
1.820 peak entirely. **Another V4 "essential" lever that reverses on an honest universe under mean
reversion.** (The tally so far: `vol_climb` inverted (V5 F4), ATR inverted (F8), `chg_1d` inverts, `chg_3d`
inverts.)

**The reading is coherent:** buy dips in names **already down moderately on the day** — that is genuine
intraday selling being overdone. Names UP big are in momentum mode (the dip keeps going); names down >20%
are **broken, not dipping** — the same falling-knife boundary the counters found at 13+ lows (F1).

**Capacity is excellent: `chg_1d ∈ [−10%, −2%]` is 32.5% of the book at PF ≈ 1.79** — a large,
well-sampled region, not a corner.

### chg_1d × ATR — the hump holds in EVERY band (an independent lever), and the peak SHIFTS

| chg_1d | atr .004–.009 | atr .009–.02 | atr .02–.035 |
|---|---|---|---|
| **< −20%** | **0.871** | 1.229 | 1.253 |
| −20..−10% | 1.237 | 1.856 | **⭐ 2.043** |
| **−10..−5%** | 1.728 | **⭐⭐ 2.148** | 1.874 |
| −5..−2% | **1.758** | 1.823 | 1.630 |
| −2..+2% | 1.513 | 1.533 | 1.570 |
| +2..10% | 1.327 | 1.636 | 1.644 |
| **≥ +10%** | 1.185 | 1.399 | 1.281 |

- **⭐ THE BEST CELL IN THE STUDY: `chg_1d ∈ [−10%,−5%] × ATR ∈ [0.009,0.02]` = PF 2.148.**
- **The deeper the daily drop, the more ATR you want with it** (−20..−10% peaks at high ATR: 2.043). Sensible
  — a −15% day *should* carry high ATR; if it does not, something is off.
- **`< −20%` is the WORST cell in EVERY ATR band** (0.871 at low ATR — an outright loser). The falling-knife
  floor is real, not noise.
- **`≥ +10%` is worst-but-one everywhere** (1.185–1.399) — exactly where V4's gate lived.

---

## Finding 12 — chg_3d: same shape, and V4's `chg_3d >= 0` gate was ALSO backwards

| chg_3d | n | win% | avg %/tr | **PF** |
|---|---|---|---|---|
| **< −30%** | 169,741 | 63.2 | **−0.027** | **0.980 ← loses money** |
| −30..−15% | 361,861 | 65.9 | 0.262 | 1.347 |
| **−15..−5%** | 659,100 | 69.1 | 0.370 | **1.773** |
| **−5..0%** | 392,667 | **69.3** | 0.370 | **⭐ 1.825** |
| 0..+10% | 696,683 | 67.7 | 0.373 | 1.724 |
| +10..30% | 644,984 | 65.5 | 0.348 | 1.489 |
| **≥ +30%** | 353,243 | 63.7 | 0.293 | **1.219** |

Same hump, peaking at **−5..0% (1.825)**. **V4 gated `chg_3d >= 0`, which EXCLUDES the two best cells
(−15..0%, PF 1.77–1.83) and ADMITS the worst (≥+30%, 1.219).** And `< −30%` loses money — the multi-day
falling-knife twin of F11's −20% floor.

**⭐ The emerging shape of the system: buy a name that is MODERATELY down (1d −10..−2%, 3d −15..0%), in the
mid-ATR band, dislocated from VWAP (z < −2.5) — but NOT one that is collapsing (1d < −20%, 3d < −30%) and
NOT one that is ripping (1d ≥ +10%, 3d ≥ +30%).**

⏭ **The rvol loosening (user):** F10 justifies dropping the old `rvol_0945_honest >= 1` universe gate — it
should be an ATR-dependent region, not a flat floor. That alone should multiply tradable capacity.


---

## Finding 13 — ⭐ CAN A DEEP z RESCUE AN UP-BIG STOCK? Mostly YES (+2..25%) — and it is the ESCAPE FROM LOWFLYER'S TURF

**User:** *"The problem is that right now we're going on the same path that LowFlyer is on. What if we look
at stocks which are up on the day, maybe even strongly, and break down on the z score. Can a deep negative
z score fix the mean reversion in a stock that is up a lot on the day?"*

**A real strategic objection:** F11's peak (`chg_1d ∈ [−10%,−2%]`) IS LowFlyer's territory (its production
book gates `chg_1d ≤ −8%`). Building a second system there buys **correlated books, not diversification**.

**PF by chg_1d × z (in the ATR band):**

| chg_1d | **z<−3** | −3..−2 | −2..−1 | z≥−1 | n(z<−3) |
|---|---|---|---|---|---|
| < −10% | 1.873 | 1.647 | 1.239 | 1.117 | 12,779 |
| **−10..−2%** (LowFlyer land) | **⭐ 2.388** | 2.024 | 1.717 | 1.755 | 19,307 |
| −2..+2% | 1.925 | 1.551 | 1.426 | 1.609 | 9,529 |
| **+2..10%** | **1.922** | 1.569 | 1.383 | 1.363 | 8,649 |
| **+10..25% UP** | **1.786** | 1.639 | 1.342 | 1.282 | 2,383 |
| +25..50% UP BIG | 1.498 | 1.443 | 1.329 | 1.304 | 567 |
| **≥ +50% RIPPING** | **1.059** | 1.261 | 1.165 | 1.010 | 212 |

**⭐ YES, in the +2..25% range.** A `z<−3` lifts `+2..10%` from **1.363 → 1.922 (+41%)** and `+10..25%` from
**1.282 → 1.786 (+39%)**. Those are large lifts into genuinely tradable territory. **The z-score does most
of its work exactly where the user hoped.**

**Three honest limits:**
1. **The rescue is INCOMPLETE.** Even at z<−3, `+10..25%` (1.786) still trails `−10..−2%` (2.388). The
   deep-z up-day cell is *good*, not *better*.
2. **It BREAKS past +25%.** `+25..50%` reaches only 1.498; **`≥+50%` is 1.059 — NO rescue at all.**
   The falling-knife logic inverted: a name ripping +50% that dips 3σ is not mean-reverting, it is the
   **start of the unwind**.
3. **Capacity is thin where it is most novel** — `+10..25% × z<−3` is 2,383 sampler trips; `+25..50%` is 567.

### ⭐⭐ THE REAL ANSWER: the two books are COMPLEMENTARY, not redundant — run BOTH

`z < −3`, ATR band, `chg_1d < +25%`:

| yr | UP-DAY n | **UP-DAY PF** | **DOWN-DAY PF** |
|---|---|---|---|
| 2020 | 1,916 | **3.179** | 2.661 |
| 2021 | 1,972 | 2.403 | **4.118** |
| 2022 | 1,412 | **1.594** | 1.502 |
| 2023 | 1,303 | **1.713** | 1.698 |
| 2024 | 1,624 | 2.100 | **2.407** |
| 2025 | 1,864 | **1.470** | 1.413 |
| **2026** | 941 | **1.128 ⚠** | 1.800 |

**The up-day book (`chg_1d ≥ +2% & z < −3`) is positive EVERY year and BEATS the down-day book in 2020,
2022, 2023 and 2025.** ~1,400 sampler trips/yr. **They take turns** (2021: down 4.118 vs up 2.403; 2020: up
3.179 vs down 2.661) — **that is the diversification the user was after, and it argues for running BOTH as
separate books rather than choosing.**

⚠ **BUT: 2026 is 1.128 at +0.18%/trade — BELOW costs**, and the trend 3.18 → 2.40 → 1.59 → 1.71 → 2.10 →
1.47 → **1.13** looks like **decay, not noise**. The down-day book does NOT share it (2026 = 1.800). One
partial year is not proof, but it is the wrong direction. **Watch this.**

⏭ **Next:** re-run the up-day cell at mc=1 to get a tradable number; check whether the decay survives the
`dv_0945`/rvol loosening (F10) or is specific to this cell.


---

## Finding 14 — 🛑 THE LIQUIDITY FILTER WAS OFF, AND PF RISES AS LIQUIDITY FALLS (the low-dv "edge" is PENNY STOCKS)

**User:** *"the problem with the tests here is that we've essentially disabled the liquidity filter. We need
to bring it back up to something reasonable like 3m in the first 15m."*

**Correct — and it matters more than a caveat.** F5–F13 ran with `--min-dv-0945 0`, i.e. on names we could
never fill.

**dv_0945 sweep (in the ATR band):**

| dv_0945 | n | % | win% | avg %/tr | **PF** | **median entry px** |
|---|---|---|---|---|---|---|
| **< $250k** | 6,569 | 0.2 | 75.2 | 0.823 | **3.291** | **$1.13** |
| **$250k–1M** | 262,520 | 8.0 | 69.8 | 0.480 | **1.885** | **$1.50** |
| $1M–3M | 449,778 | 13.7 | 67.0 | 0.358 | 1.537 | $2.98 |
| **$3M–10M** | 678,094 | 20.6 | 66.8 | 0.320 | 1.470 | $8.58 |
| $10M–30M | 632,386 | 19.2 | 66.7 | 0.310 | 1.464 | $19.44 |
| ≥ $30M | 1,258,201 | 38.3 | 66.3 | 0.288 | 1.372 | $85.40 |

**⭐ PF rises MONOTONICALLY as liquidity FALLS — and the median entry price gives it away.** The PF 3.291
cell is **$1.13 stocks**; the 1.885 cell is **$1.50 stocks**. Sub-$2 penny names with a quarter-million
dollars of morning volume: **the spread alone eats the entire edge**, before any market impact. The
sub-$3M bands were 21.9% of the book and were inflating every prior finding.

**`dv_0945 >= $3M` is now the DEFAULT** (user). It is conservative — even $3M leaves a **$8.58** median
entry. Above the floor the liquidity effect is mild (1.470 → 1.464 → 1.372), so the findings survive.

### The re-checks — everything important SURVIVES, with a haircut

**F8 (ATR), now `dv >= $3M`:**

| log-ATR20 | n | avg %/tr | PF (was) | **PF ($3M)** |
|---|---|---|---|---|
| < 0.004 | 6,250,941 | 0.015 | 1.033 | **1.057** |
| 0.004–0.009 | 1,970,980 | 0.216 | ~1.4 | **1.384** |
| 0.009–0.013 | 329,408 | 0.486 | 1.572 | **1.515** |
| **0.013–0.02** | 179,782 | 0.712 | 1.583 | **⭐ 1.523** |
| 0.02–0.035 | 88,511 | 0.704 | ~1.42 | **1.318** |
| 0.035–0.05 | 17,329 | 0.168 | 1.142 | **1.045** |
| **≥ 0.05** | 6,813 | **−2.128** | 0.755 | **0.705** |

**The hump AND the inversion both hold.** Peak 1.583 → 1.523. **The ceiling is confirmed and if anything
SHARPER** (≥0.05 → PF 0.705 at −2.13%/trade).

**F10 (rvol × ATR), now `dv >= $3M` — ⭐ THE INVERSION SURVIVES INTACT:**

| ATR band | rvol<0.5 | 0.5–2 | rvol≥2 | n |
|---|---|---|---|---|
| 0.004–0.009 | 1.170 | 1.427 | **1.595** | 1,970,980 |
| 0.009–0.02 | 1.368 | 1.442 | **1.573** | 509,190 |
| **0.02–0.035** | **⭐ 1.679** | 1.432 | **1.288** | 88,511 |

Monotone UP at low/mid ATR; **INVERTED at high ATR** (1.679 → 1.288). The "high volatility + high volume =
a real repricing, not a dip" reading holds on fillable names.

**F13 (the up-day / z rescue), now `dv >= $3M`:**

| chg_1d | z<−3 (was) | **z<−3 ($3M)** | base (z≥−1) | n |
|---|---|---|---|---|
| < −10% | 1.873 | **1.779** | 1.104 | 10,296 |
| −10..−2% (LowFlyer) | 2.388 | **2.321** | 1.677 | 15,431 |
| −2..+2% | 1.925 | **1.794** | 1.575 | 7,603 |
| **+2..10%** | 1.922 | **⭐ 1.863** | 1.304 | 6,528 |
| **+10..25% UP** | 1.786 | **1.576** | 1.246 | 1,862 |
| ≥ +25% | ~1.3 | **1.333** | 1.145 | 724 |

**The core finding SURVIVES: z<−3 still lifts `+2..10%` from 1.304 → 1.863 (+43%).** The user's up-day
thesis holds on fillable names.

⚠ **But the up-BIG cell took the biggest hit: `+10..25%` fell 1.786 → 1.576** on 1,862 trips — that was
already the thin, novel part of the idea, and the floor makes it thinner. **`+2..10%` (6,528 trips, PF
1.863) is now the better expression of the user's idea than `+10..25%`.**


---

## Finding 15 — the EXIT WINDOW: shorter targets = higher PF, longer targets = (slightly) higher return. No free lunch.

**User:** *"How would entering or exiting on the 60m levels affect the results... Would longer targets help
expectancy or hurt it?"*

Swept the exit target with **identical entries** (2,568,681 trips every row — only the target changes), on
the production filters (`dv_0945 >= $3M`, ATR band):

| exit = prior N-min HIGH of closes | win% | **avg %/tr** | **PF** | med hold |
|---|---|---|---|---|
| **5m** | **68.6** | 0.256 | **⭐ 1.723** | 5 min |
| 10m | 68.5 | 0.291 | 1.575 | 11 min |
| **20m (default)** | 66.5 | 0.302 | **1.417** | 24 min |
| 30m | 65.3 | 0.310 | 1.355 | 38 min |
| 45m | 64.2 | **⭐ 0.314** | 1.299 | 58 min |
| 60m | 63.5 | 0.307 | 1.257 | 79 min |

**⭐ THE TWO METRICS POINT IN OPPOSITE DIRECTIONS — that IS the finding:**
- **PF falls MONOTONICALLY** with target length: **1.723 → 1.257 (−27%)**.
- **avg %/trade RISES then plateaus**: 0.256 → **0.314 at 45m**, then rolls over at 60m.
- **Win rate falls monotonically**: 68.6% → 63.5%.

**A longer target earns ~23% more per trade (0.256 → 0.314) at the cost of ~25% of the PF.** You are paid a
little more to carry a lot more risk. For a mean-reversion book — whose whole appeal is a high win rate and
a tight distribution — **that is a bad trade.**

**Mechanism:** the edge IS the snap-back, and it is **spent** once price reclaims the 20m high. Holding for
the 60m high means sitting through the post-reversion drift, which is directionless — **variance without
drift**.

### ⚠ But the 5m cell is NOT the answer either — costs invert the ranking

At a ~0.1% round-trip cost (spread on `dv_0945 >= $3M` names + commissions):

| exit | gross avg %/tr | **net of ~0.1%** | cost as % of edge |
|---|---|---|---|
| 5m | 0.256 | **0.156** | **39%** |
| 20m | 0.302 | **0.202** | 33% |
| 45m | 0.314 | **0.214** | **32%** |

**The highest-PF cell (5m) has the WORST net return; the worst-PF cell (45m) has the BEST.** Net of costs
the ranking compresses to near-indifference.

### 🛑 Verdict SUPERSEDED BY F16 — this section's conclusion was WRONG (see F16)

### ~~Verdict: 20m is a defensible middle~~ — and the EXIT is NOT where the edge lives

Keep `--exit-high-window 20`. Shorter = better risk-adjusted, longer = marginally better per-trade
pre-cost; **neither is a free lunch, and that is itself the point.** Compare the levers:

| lever | PF range it spans | does avg %/tr follow? |
|---|---|---|
| **entry selection** (ATR band × z × chg_1d) | **1.26 → 2.15** | ✅ **YES** (0.29 → 1.44) |
| exit window | 1.26 → 1.72 | ❌ **NO** — it moves the OPPOSITE way |

**Entry selection buys PF *and* return together. The exit window only trades one for the other.** Effort
belongs on selection.

(F4 already covered the 60m *entry*: it raises PF at every K but is LESS ROBUST — 60m K=1 loses money in
2021 — and is capacity-thin. The 20m→20m default survives both sweeps.)


---

## Finding 16 — 🛑 F15 WAS WRONG: the 5m target DOMINATES. PF *is* the profit number.

**User:** *"No, you don't get it. A pf of 1.75 is 3x more profit than a pf of 1.25 if we hold the losses
constant. This table is making the 5m target look really good. High pfs have a lot more consistency than
low pfs."*

**Correct. F15's verdict ("20m is a defensible middle") was WRONG, and the error was framing.**

**The arithmetic:** normalise gross loss to 1. PF 1.723 ⇒ net **0.723**; PF 1.257 ⇒ net **0.257**.
That is **2.8× the profit per unit of loss taken.** F15 compared `avg %/trade` as if trade count were fixed
— but it is not: the 5m target frees capital in **5 minutes vs 79** (~16× the turnover on the same
capital). **Higher PF *and* faster recycling wins on profit AND consistency.** F15's cost analysis
(cost ÷ avg-return-per-trade) was the misleading step; the right denominator is **capital-time**, where 5m
dominates too.

### chg_1d × exit window (dv ≥ $3M, ATR band) — 5m wins in EVERY bucket

| chg_1d | **PF 5m** | PF 20m | PF lift | avg% 5m | avg% 20m | n |
|---|---|---|---|---|---|---|
| < −10% | **1.415** | 1.262 | +12% | 0.225 | 0.275 | 385,674 |
| **−10..−2%** | **⭐ 2.140** | 1.735 | **+23%** | 0.284 | 0.372 | 829,314 |
| −2..+2% | **1.916** | 1.467 | **+31%** | 0.233 | 0.251 | 482,472 |
| **+2..10%** | **1.727** | 1.371 | **+26%** | 0.230 | 0.247 | 511,553 |
| +10..25% | **1.563** | 1.314 | +19% | 0.262 | 0.300 | 240,183 |
| **≥ +25%** | **1.435** | 1.203 | +19% | 0.350 | 0.347 | 119,485 |

In net-profit-per-unit-of-loss the `−10..−2%` cell goes **0.735 → 1.140 (+55%)**, and recycles capital 5×
faster. **⭐ Also: `≥ +25%` becomes respectable at 5m (1.435 vs 1.203)** — the snap-back in a ripping name
is BRIEF, and a 20m target gives it back. That partially rehabilitates F13's up-big cell.

### ⭐⭐ z × exit window — the 5m target partly DISSOLVES the z-score's advantage

| z | **PF 5m** | PF 20m | PF lift | avg% 5m | win% 5m | n |
|---|---|---|---|---|---|---|
| **z < −3** | **⭐ 2.152** | 1.907 | +13% | 0.704 | **73.7** | 42,444 |
| −3..−2 | **1.915** | 1.646 | +16% | 0.355 | 69.8 | 385,674 |
| −2..−1 | 1.579 | 1.374 | +15% | 0.219 | 68.1 | 1,140,987 |
| **−1..0** | **1.785** | 1.320 | **+35%** | 0.246 | 68.8 | 723,378 |
| **z ≥ 0** | **1.839** | 1.416 | **+30%** | 0.229 | 67.7 | 276,198 |

**At 20m, z was cleanly MONOTONE (1.907 → 1.320, a 45% spread). At 5m the U-shape returns and the spread
COLLAPSES (2.152 vs 1.579) — and `z >= 0` (1.839) now BEATS `z ∈ [−2,−1]` (1.579).**

**The reading: a fast target captures the immediate bounce REGARDLESS of how dislocated the entry was.**
The z-score was largely a proxy for **how far price had to travel back** — which only mattered because the
20m target made you wait for it. Take the first snap and the setup's depth matters far less.

**⭐ The capacity implication is large:** at 20m you needed `z < −3` (42k trips) to reach PF ~1.9. At 5m the
**entire `z >= −1` region (≈1M trips) sits at PF 1.79–1.84** — **near-peak PF on ~25× the trade count.**

### Revised verdict (supersedes F15)

**`--exit-high-window 5` is the new default direction.** The exit is NOT a "trade PF for return" dial as
F15 claimed — **it is a genuine improvement** once profit is measured per unit of risk and per unit of
capital-time. ⏭ Sweep 2m/3m/5m/7m to find the true peak; confirm at mc=1; and re-check the F8/F10/F11
levers under a 5m target, since F16 shows the exit window **changes which entry features matter**.


---

## Finding 17 — the ATR lever under a 5m exit: the F8 CEILING WAS LARGELY AN ARTIFACT of the slow target

**User:** *"Let's check the ATR lever next."* → then: *"we'll keep the floor at 0.004, but we'll uncap the
ceiling from here on out... I know that the <0.004 cell won't have any surprises, but the other tail might."*

**The other tail did have a surprise.**

### Inside the band: 5m wins everywhere, and the lift GROWS with ATR

| ATR | **PF 5m** | PF 20m | **PF lift** | avg% 5m | avg% 20m | win% 5m | n |
|---|---|---|---|---|---|---|---|
| 0.004–0.006 | 1.510 | 1.294 | +17% | 0.128 | 0.151 | 66.9 | 1,204,601 |
| 0.006–0.009 | 1.811 | 1.499 | +21% | 0.255 | 0.317 | 69.6 | 766,379 |
| 0.009–0.013 | 1.875 | 1.515 | +24% | 0.407 | 0.486 | 70.9 | 329,408 |
| **0.013–0.020** | **⭐ 1.912** | 1.523 | **+26%** | 0.608 | 0.712 | **71.2** | 179,782 |
| 0.020–0.027 | 1.781 | 1.393 | +28% | 0.737 | 0.777 | 70.6 | 60,697 |
| 0.027–0.035 | 1.561 | 1.200 | **+30%** | **0.722** | *0.544* | 68.9 | 27,814 |

**The lift grows monotonically with ATR: +17% → +30%.** Coherent — **high-ATR names snap back FASTER and
HARDER, so a slow target gives back more.** Note the last row: 0.027–0.035 is the only band where the 5m
target earns MORE per trade than 20m (0.722 vs 0.544) — in the most volatile names **waiting is actively
destructive**.

**⭐ The peak does NOT move: 0.013–0.020 (PF 1.912, win 71.2%).** Unlike the z-score — which F16 showed the
fast exit largely DISSOLVES — **ATR survives the exit change intact. It is a genuinely independent lever.**

### ⭐⭐ THE TAIL (ceiling uncapped, floor kept at 0.004 for disk)

| ATR | n | win% | avg %/tr | **PF 5m** | *(PF 20m, F8/F14)* |
|---|---|---|---|---|---|
| 0.004–0.013 | 2,300,388 | 68.4 | 0.210 | 1.695 | ~1.4 |
| **0.013–0.020** | 179,782 | **71.2** | 0.608 | **⭐ 1.912** | 1.523 |
| 0.020–0.027 | 60,697 | 70.6 | 0.737 | 1.781 | 1.393 |
| **0.027–0.035** (old ceiling) | 27,814 | 68.9 | 0.722 | **1.561** | 1.200 |
| **0.035–0.045** | 13,898 | 68.2 | 0.778 | **1.478** | *~1.045* |
| **0.045–0.06** | 7,084 | 65.8 | 0.867 | **1.432** | *~0.705* |
| **0.06–0.08** | 2,241 | 66.5 | **1.215** | **1.458** | *~0.705* |
| **≥ 0.08** | 919 | **50.6** | **−2.919** | **0.618** | — |

**⭐ The 0.035–0.08 range is PROFITABLE under a 5m exit** — PF 1.43–1.48, win 66–68%, and avg/trade RISING
to **1.215%** at 0.06–0.08. Under a 20m exit those same names were PF ~0.7–1.05 (worthless to losing).
**The F8/F14 ceiling at 0.035 was mostly the SLOW TARGET failing to bank a fast bounce, not a property of
the names.**

**But the cliff at 0.08 is REAL, not an artifact:** PF **0.618**, win **50.6%**, avg **−2.92%/trade**. Past
~8% ATR the name is not oscillating **at any timescale** — it is being repriced.

⚠ **Capacity honesty:** 0.035–0.08 is ~23k trips total (~3,500/yr in the sampler). A genuine slice, not a
capacity story. And the hump still holds — **uncapping does not move the optimum (0.013–0.020), it recovers
a profitable TAIL that was being discarded.**

### Engine change

**`MaxAtrPct = infinity` is now the default** (user) — the tail stays SAMPLED and sliceable; `log_atr_20` is
recorded. **`MinAtrPct = 0.004` stays** (user: *"the 0.004–0.006 cells are dull, so we might as well save
the hard drive space"*). **The practical ceiling is ~0.08, not 0.035.**

### F10 re-check under the 5m exit — the rvol inversion SURVIVES, and shifts

| ATR band | rvol<0.5 | 0.5–2 | rvol≥2 | n |
|---|---|---|---|---|
| 0.004–0.009 | 1.242 | 1.725 | **⭐ 2.083** | 1,970,980 |
| 0.009–0.020 | 1.525 | 1.716 | **⭐ 2.028** | 509,190 |
| **0.020–0.035** | **⭐ 1.890** | 1.689 | 1.685 | 88,511 |

**The inversion holds** (low ATR wants HIGH rvol; high ATR wants LOW rvol) — but the fast exit **lifts the
high-rvol cells sharply**: `rvol>=2 × low ATR` goes **1.595 (20m) → 2.083 (5m)**, the best cell in the
table. The crossover has moved: at 20m it sat between the 0.009–0.02 and 0.02–0.035 bands; at 5m the
high-rvol advantage persists further up the ATR range before flipping.


---

## Finding 18 — `dist_vwap_z_log` added, and it is INDISTINGUISHABLE from the linear z (see MaxRiderV1 F7)

Added `dist_vwap_z_log` = z of `log(close/vwap)` (user, "calculate the VWAP z in log space for both
systems"). On the long book it tracks the linear `dist_vwap_z` within ~0.02 at every level (e.g. `< −3`:
2.152 linear vs 2.161 log; the whole U-shape preserved). **No measurable difference.** Full analysis +
the WHY (input scale: log matters for heavy-tailed volume, not for ±3% VWAP distance) is in
`docs/maxrider_v1_results.md` F7. Keep the log column as canonical; the choice is immaterial here.


---

## Finding 19 — ADX: the MR "loves chop" thesis is WRONG — higher ADX is BETTER, and it is INDEPENDENT of ATR

`adx_14` (Wilder ADX(14), direction-agnostic trend STRENGTH) has been recorded since the sampler was built
(F2's `AdxMa` class); this is its first analysis. **The textbook MR thesis — best in chop (low ADX), worst
in a trend (high ADX) — is FALSE here:**

| ADX | n | win% | avg% | **PF** |
|---|---|---|---|---|
| < 15 (dead chop) | 303,512 | 67.8 | 0.219 | **1.596 ← WORST** |
| 15–20 | 474,516 | 68.0 | 0.237 | 1.671 |
| 20–25 | 476,972 | 68.5 | 0.253 | 1.725 |
| 25–30 | 385,910 | 68.9 | 0.269 | **1.778** |
| 30–40 | 490,502 | 69.1 | 0.275 | 1.777 |
| ≥ 40 (strong trend) | 436,491 | 69.3 | 0.274 | 1.764 |

**Monotone-increasing, plateauing at 30+. The LOWEST ADX (dead chop) is the WORST cell.**

**Why (this reframes ADX for a conditioned MR book):** the entry ALREADY requires a mean-reversion setup (a
dip to a 20m low). So ADX here is not "a trend that runs THROUGH the reversion" — it is **energy in the
name**. A dead-chop name (ADX<15) barely moves, so its dips are NOISE with nothing to snap back FROM. A
high-ADX name makes real thrusts, and the counter-thrust IS the reversion trade. ADX measures ENERGY, not
directional-persistence-that-fights-us.

**⭐ AND IT IS INDEPENDENT OF ATR** — `corr(ATR, ADX) ≈ 0` in every band (0.06 / −0.02 / 0.00). ATR = bar
RANGE size; ADX = directional CONSISTENCY. A name can be high-ATR/low-ADX (wild swings going nowhere) or
low-ATR/high-ADX (steady grind). Orthogonal — and ADX lifts PF WITHIN every ATR band:

| ATR | adx<20 | adx 20–30 | adx≥30 |
|---|---|---|---|
| .004–.009 | 1.594 | 1.624 | **1.709** |
| **.009–.02** | 1.788 | **⭐ 1.966** | 1.905 |
| .02–.035 | 1.499 | **1.863** | 1.702 |

**Best cell in the study: `ATR 0.009–0.02 × ADX 20–30` = PF 1.966** — two independent levers. ⚠ Nuance: in
the MID-ATR bands the peak is ADX **20–30**, not ≥30 (a slight inverted-U — do not fade a STRONG trend in an
ALREADY-volatile name); only in the lowest-ATR band does ADX help monotonically to ≥30. **ADX is a genuine
NEW dimension for the long book, not an ATR proxy.**

(Short side mirrors this — see MaxRiderV1 F16: monotone-increasing, peak at ADX ≥ 40.)


---

## Finding 20 — the short's F9/F10/F11 features on the LONG book: session extremes MIRROR, but VOLUME does NOT

**User:** *"try these features on the long book — not-session-low instead of not-session-high... First a 2×2
on session extremes, then break down on the volume z."* Added `vol_z_log`/`vol_z_lin`, `is_new_sess_low`,
`is_new_sess_vol_high` to the long engine (the mirrors; volume has no directional "low", so vol-HIGH is the
same flag on both sides). Sanity: vol z mean −0.15 / max 5.87 / 0 nulls; 34.2% session-low (short: 29.7%
sess-high); 0.9% session-vol-high (short: 1.4%).

### not-session-low MIRRORS the short (F9) — don't buy the name at its absolute worst point

| entry | n | avg% | p1 | PF |
|---|---|---|---|---|
| **not-low (20m-only)** | 1,689,681 | 0.263 | −4.65 | **1.808** |
| SESSION LOW (free-fall) | 879,000 | 0.242 | −5.68 | 1.593 |

Clean mirror: a non-session-low dip beats a session-low free-fall (1.808 vs 1.593), smaller tail too.

### The session-extremes 2×2 MIRRORS the short's F10 exactly — same two-mode structure

| cell | n | avg% | p1 | **PF** |
|---|---|---|---|---|
| **not-low × not-vol** | 1,683,919 | 0.263 | −4.65 | **1.809** (clean core, 66%) |
| not-low × volHIGH | 5,762 | 0.338 | −6.14 | 1.754 |
| sessLOW × not-vol | 860,823 | 0.236 | −5.63 | **1.583 ← worst** |
| **sessLOW × volHIGH** | 18,177 | **0.529** | **−8.17** | **⭐ 1.951** |

- `sessLOW × volHIGH` = **the CAPITULATION CLIMAX** (PF 1.951, best, fattest tail −8.17) — a volume spike AT
  the session low = a washout bottom, the best dip to buy but the falling-knife risk. (Long mirror of the
  short's blow-off, F10.)
- `sessLOW × not-vol` = the worst (1.583) — a QUIET grind to a new session low, no capitulation, just steady
  bleeding. (Long mirror of the short's reversal-up trap.)
- The quiet session-extreme is the TRAP; the volume-spike session-extreme is the CLIMAX worth trading.
  **Clean structural symmetry with the short.**

### ⭐⭐ BUT VOLUME z DOES NOT MIRROR — the long is a U-SHAPE, not the short's monotone

| vol_z_log | n | avg% | p1 | **PF** |
|---|---|---|---|---|
| **quiet (<−0.5)** | 944,665 | 0.224 | −4.56 | **1.723** |
| −0.5..0 | 491,665 | 0.223 | −4.97 | **1.627 ← trough** |
| 0..0.5 | 457,222 | 0.251 | −5.19 | 1.671 |
| 0.5..1 | 345,235 | 0.292 | −5.38 | 1.755 |
| **1..2** | 292,051 | 0.358 | −5.71 | **⭐ 1.888** |
| ≥ 2 (flush) | 37,843 | 0.416 | −6.94 | 1.854 |

**The SHORT was strictly MONOTONE (F6): fade the QUIET pop, PF 1.769 → 1.281 as volume rose. The LONG is a
U: quiet is decent (1.723), the MIDDLE is the trough (1.627), and LOUD is the BEST (1.888 @ vol_z 1–2).**

**⭐ The long book has TWO winning regimes; the short has one:**
- **buy the QUIET dip** — an orderly pullback in a strong name (1.723).
- **buy the PANIC FLUSH** — a capitulation washout (1.888).
- the DEAD ZONE is the MIDDLE — normal volume, no signal.

**This reconciles the earlier quiet-vs-loud-by-ADX split** (quiet won at high ADX, loud won at low ADX):
they are TWO DIFFERENT TRADES — the orderly-pullback trade (quiet, works in TRENDING/high-ADX names) and the
capitulation trade (loud flush, works in CHOPPY/low-ADX names). The U-shape is those two trades side by side.

**⚠ The flush trade's cost is the TAIL:** p1 degrades monotonically with volume (−4.56 → −6.94) — the flush
that does NOT bottom is the falling knife. So volume controls the tail on BOTH sides (structural mirror of
F11), even though the PF direction differs.

### ⭐ THE KEY LONG/SHORT ASYMMETRY OF THE STUDY

**SHORT: fade only the QUIET pop (F6/F11 monotone) — one regime, volume-avoidant.
LONG: buy the quiet dip OR the panic flush (U-shape) — two regimes.** Both fade/buy session extremes only on
a volume SPIKE (climax), both have volume-driven tails. But the short is one-sided on volume and the long is
two-sided. A short is fading STRENGTH (which a volume spike confirms → avoid); a long is buying WEAKNESS
(which a volume flush EXHAUSTS → embrace). That is the real mechanism, and it is why the naive "just mirror
the short" fails on the volume lever.


---

## Finding 21 — ⭐⭐ THE FLUSH (bar % change) is the STRONGEST long lever — deeper = better, and it STACKS with everything

**User:** *"do a breakdown on the bar % change? That was really effective on the LowFlyer system."*

Added `bar_pct` = `close / prev-bar-close − 1` (the entry bar's single-bar move; for a long dip-buy it is the
FLUSH depth — LowFlyer's main sizing lever) to both engines. Sanity: 86.8% of long entries are DOWN bars,
median −0.38%, p99 = 0.0 (a 20m-low entry can barely close up), 0 nulls.

**⭐ Deeper flush = better dip, MONOTONE — and the avg% climbs HARD (a rare quality+size lever):**

| flush (bar %) | n | win% | avg% | p1 | **PF** |
|---|---|---|---|---|---|
| **< −3% (violent)** | 41,450 | **74.0** | **1.302** | −13.87 | **⭐ 2.326** |
| −3..−2% | 67,080 | 72.5 | 0.832 | −9.72 | 2.209 |
| −2..−1% | 279,853 | 70.2 | 0.480 | −7.12 | 1.915 |
| −1..−0.5% | 604,108 | 68.7 | 0.259 | −4.98 | 1.700 |
| −0.5..−0.2% | 775,280 | 69.1 | 0.164 | −4.04 | 1.543 |
| −0.2..0% | 462,487 | 68.7 | 0.110 | −4.15 | **1.345 ← trough** |

**⭐ The ramp is clean and MONOTONE: PF 1.345 → 2.326 as the flush deepens; avg% +0.11% → +1.30% (12×
spread).** LowFlyer confirmed — deeper panic = bigger bounce, so LowFlyer SIZED on flush depth. The single
strongest long lever found. The trough is `−0.2..0%` (1.345) — a lazy drift to a new low, no conviction.
**All rows above are `bar_pct < 0`; `bar_pct == 0` is EXCLUDED — see the box below.**

### ⚠ `bar_pct == 0` is EXCLUDED — a tape-microstructure quirk, not a setup (user caught this)

The user asked: if we trigger on CLOSES, how can a long entry have a close-UP bar? **It cannot, and it does
not** — the original "≥ 0%" row was **100% `bar_pct == 0` exactly** (338,423 rows, 0 positive). The trigger
`bar.close <= priorLow` re-fires when this close **TIES** the prior 20m-low-of-closes: two consecutive bars
at an IDENTICAL close.

**What they are (the user pushed until it was nailed down):** NOT halts (0-volume bars never enter the
engine), NOT cheap stocks (0% sub-$1; the ties skew HIGHER-priced, p90 $977 vs $577). The tell is
**round-number clustering: whole-dollar closes are 4.09% of tie bars vs 1.99% of the rest; half-dollar 5.9%
vs 2.97%.** These are **thin-tape bars on HIGH-priced names in a quiet minute** — a $300 stock trading a few
hundred shares prints the identical close twice, pinned to a round number. 13% prevalence is real (round-
number pinning is common), not garbage.

**Why it is EXCLUDED, not reported as a 2.124 setup:** a "flat re-test" cannot be distinguished LIVE from any
other new-low tie, and you cannot reliably FILL at the exact tie price. Its PF is a property of the tape's
QUIETNESS, not a signal about the stock — a sampler cell that evaporates on contact. **⛔ Two earlier drafts
FABRICATED explanations for this cell ("gap between bars", "v-bottom mid-turn") without checking; both were
wrong. The lesson: characterize the cell before narrating it.** The flush finding is the monotone ramp on
`bar_pct < 0`, full stop — verified unchanged when the tie bars are dropped entirely.

**⚠ Tail is the flip side:** p1 = −13.87% at the violent flush — best PF/expectancy, fattest tail (the flush
that keeps flushing). The biggest edge carries the biggest knife, as everywhere in this book.

### ⭐ It STACKS — the flush is not redundant with ADX or volume

Deep flush (< −2%) × ADX × volume:

| ADX | PF quiet | PF loud |
|---|---|---|
| < 30 | 2.417 | **2.571** |
| 30–40 | **2.868** | 2.345 |
| ≥ 40 | 2.173 | 2.137 |

Every cell PF 2.1–2.9, above the deep-flush marginal (2.2). **And it SHARPENS F20's two regimes on the
deep-flush subset:**
- **capitulation flush** = deep bar × LOUD volume × CHOPPY (low ADX) → 2.571.
- **controlled pullback** = deep bar × QUIET volume × TRENDING (mid/high ADX) → 2.868.

Both ~2.5–2.9. The flush is the AMPLITUDE; volume+ADX say WHICH KIND of flush. Together they define the long
production core the way F18 defined the short's.

⏭ Like LowFlyer, flush depth is a natural SIZING lever (size up on the deeper flush), not just a filter —
but it imports the tail, so size-up must be paired with the position-level risk of the −13.87% p1. Confirm at
mc=1.


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
