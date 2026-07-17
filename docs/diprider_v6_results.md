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
