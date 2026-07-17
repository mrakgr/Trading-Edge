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
