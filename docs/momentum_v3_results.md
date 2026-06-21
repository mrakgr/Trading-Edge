# Mid-Cap Momentum v3 — Clipped-Methodology Era

**Status: working long-only daily-momentum edge. Filtered (breadth + ≥2005): 4,314 trips, PF raw
1.923 / clip 1.575, +$1.07M. Honest next-open fills, 5-day time-stop.**

v3 is not a new engine or a new system — it is the **same `TradingEdge.MomentumV2` production system**,
carried forward into a research methodology that computes every PF/return figure on **clipped per-trade
returns** and decides on **cumulative** views. That shift (2026-06-21) was significant enough — it
re-derived the tightness and ATR% optima and exposed several prior "edges" as lottery-winner mirages —
to warrant a fresh document. The full historical research record (exit-mechanic sweeps, 52w-proximity
studies, loose-base shorts, regime-switching, chandelier/time-stop derivation) lives in
[`momentum_v2_results.md`](momentum_v2_results.md) and is **not** repeated here; v3 carries only the
*current production state* and the work done under the clipped methodology.

`TradingEdge.MomentumV2` is a ground-up F# rewrite: all indicators computed in a single in-memory pass
(no SQL window functions), one `QullaSystem` per ticker. A full 22-year scan runs in **~16s**, so the
parameter sweeps below are minutes, not hours.

---

## 📐 Methodology — the clipped, cumulative standard (PROJECT STANDARD as of 2026-06-21)

Two disciplines now govern every sweep in this document:

**1. Winner-clip: PF / mean-return are computed on each trade's RETURN clipped at +50% (`LEAST(ret, 0.50)`);
the loss side is left untouched.** PF is mean-driven and therefore hostage to lottery winners — a single
buyout pop or +800% runner can hand a bucket a gaudy PF that has nothing to do with its *reliable* edge, and
that contamination is exactly what makes bucketed/decile views go non-monotonic. Clipping the upside to a
sensible ceiling (+50% is above p99 of qualifying trades — generous) makes PF reflect the *typical* trade in
a bucket, which is what a floor/ceiling filter decision actually rests on. **Total P&L is understated by
design** (the conservative read); when raw total-$ matters it is reported separately as `PF raw` / `tot`.

**2. Decide on CUMULATIVE views** (`X ≥ thr` / `X < thr`), which are low-variance and monotone; use
**non-cumulative bands only diagnostically** to locate where edge changes — their edge-of-range cells are
noisy and must not drive a threshold choice on their own.

*Why this matters (evidence):* under the clip, `tight < 3.0` showed **PF raw 2.67 vs PF clip 1.388** — its
apparent edge was almost entirely a handful of monster winners. The clipped column is the honest read; the
raw column is seductive and non-monotone. Several v2-era "findings" that rested on raw PF have been re-checked
under this standard (and where they changed, the v3 entry says so).

---

## The system in one screen (current production state)

Long-only daily momentum on US common stocks / ADRs (`ticker_reference.type IN ('CS','ADRC')`),
2005–2026. One position per breakout signal, fixed $10k notional, uncapped concurrency, no
compounding (so net P&L is a raw edge-and-breadth measure, not an achievable equity curve).

**Entry** — on a daily bar, go long at the close when ALL hold (each indicator uses *prior* bars,
no lookahead):

| gate | threshold | meaning |
| --- | --- | --- |
| entry-day move | **10% ≤ move < 30%** | `close/prevClose − 1`. The breakout must announce itself; the 30% cap removes the single-day exhaustion blow-off (and makes an rvol upper cap redundant) |
| relative volume | **rvol ≥ 5** (no upper cap) | `volume / 28-day avg volume`. 5 is enough to be significant; the move cap handles the toxic high-rvol blow-off tail |
| ATR% (log) | **< 0.10** | mean log-true-range over 14 prior bars. Tightened 0.11 → 0.10 on 2026-06-21 (the 0.10–0.11 band was dead) |
| tightness (linear) | **< 4.5** | `(14d range) / ATR` — prior consolidation must be tight (linear scale; sharper loose-tail cut than log) |
| 52-week proximity | **close ≥ 0.95 × hi_252** | near the 1-year closing high (closing-high channel beats the intraday-high channel) |
| price floor | **≥ $1** | lowered 5 → 1 on 2026-06-21 (sub-$5 is real edge under the clip, not a lottery; $1–3 kept for the future past-runner floor to rescue) |
| liquidity | **avg dollar volume ≥ $100k** | 28-day average |
| breadth (market-wide) | **`pct_above_20` lagged 1 day > 0.5** | applied post-hoc; risk-on regime only |
| heat (market-wide) | **`h10 < 0.25`** | skip entries when the top-1%-gainer froth measure is hot (Sykes-inspired); applied post-hoc |

**Exit** — **5-day time-stop, NO price stop** (the current default; the v2 doc's header still describes the
older Qullamaggie trailing stop — superseded). Sell at the next bar's open on the 5th day. The "disaster
exit" tested in v2 turned out to be a SHORT signal, not a long exit, and is off. Open positions at the data's
end are marked-to-market at the final close.

**Headline (filtered: breadth + 2005-start, 21.4 trading-day-years; ATR% 0.10):**

| | value |
| --- | ---: |
| trips | 4,314 |
| win rate | 52.6% |
| profit factor (raw) | **1.923** |
| profit factor (clip +50%) | **1.575** |
| net P&L | +$1,071,336 |

*(Unfiltered engine run at production defaults: 6,749 trips / PF 1.820 / +$1.55M. With heat<0.25 added:
3,195 trips / PF raw 2.164 / clip 1.662 / +$879k.)*

---

## Entry-filter geometry — the clipped re-derivation (2026-06-21)

All three sweeps below use the **wide dump** (both volatility gates opened so the full range is visible):

```bash
dotnet run -c Release --project TradingEdge.MomentumV2 -- \
  --out /tmp/v2_wide_tight_atr.csv --max-tightness 1000 --max-atr-pct 1000   # ~9,090 trips
```

then sub-filter in SQL with breadth lag1 > 0.5, ≥2005, closed trips, clip +50%.

### Tightness ceiling — a HUMP peaking exactly at 4.5

The naive intuition ("tighter = safer = better") is **wrong**: clipped PF climbs off the ultra-tight floor,
peaks in the 3.5–4.5 zone, and falls off a cliff above 4.5. Script:
[`scripts/equity/tightness_atr_cum_sweep.sql`](../scripts/equity/tightness_atr_cum_sweep.sql).

| tight ceiling | n | PF clip | clip post | | tight ceiling | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 2.5 | 298 | 1.219 | 1.499 | | **< 4.5** | 3,713 | **1.571** ← peak | 1.478 |
| < 3.0 | 1,082 | 1.388 | 1.247 | | < 5.0 | 4,301 | 1.495 | 1.386 |
| < 3.5 | 2,047 | 1.447 | 1.338 | | < 5.5 | 4,727 | 1.475 | 1.389 |
| < 4.0 | 3,003 | 1.568 | 1.447 | | < 7.0 | 5,365 | 1.390 | 1.291 |

Non-cumulative bands locate the source — best band is **3.5–4.0 (clip 1.864)**; **< 2.0 is a loser (0.836)**;
edge collapses above 4.5 (**5.5–7.0 = 1.015, post 0.886**). `tight<3.0` raw PF 2.67 → clip 1.388 is the clearest
lottery-mirage in the data. `MaxTightness = 4.5` is the optimum of the entire 2.5–7 range.

### ATR% ceiling — peaks at 0.10; the edge lives in the 0.06–0.10 band, NOT the quiet bulk

Same lesson on the other axis. Cumulative PF peaks at **< 0.10**; the non-cumulative bands show the edge is
concentrated in **0.06–0.10 (PF ~1.9–2.5)**, not the quiet `< 0.04` bulk (1.42):

| atr% ceiling | n | PF clip | clip post | | atr% band | n | PF clip | clip post |
|---|--:|--:|--:|---|---|--:|--:|--:|
| < 0.04 | 2,254 | 1.420 | 1.236 | | < 0.04 | 2,254 | 1.420 | 1.236 |
| < 0.06 | 3,158 | 1.438 | 1.355 | | 0.06–0.07 | 222 | 1.925 | 1.867 |
| < 0.08 | 3,524 | 1.529 | 1.470 | | 0.07–0.08 | 144 | 2.013 | 1.853 |
| < 0.09 | 3,615 | 1.569 | 1.501 | | **0.08–0.09** | 91 | **2.460** | **2.016** |
| **< 0.10** | 3,678 | **1.590** ← peak | **1.520** | | 0.09–0.10 | 63 | 2.208 | 1.903 |
| < 0.11 (old) | 3,713 | 1.571 | 1.478 | | 0.10–0.11 | 35 | 0.984 | 0.633 ← dead |

**✅ DECISION (2026-06-21): `MaxAtrPct` 0.11 → 0.10.** Engine-verified: raw PF **1.774 → 1.802**, P&L flat at
**+$1.19M**, trips 5,883 → 5,827 (−56). The 0.10–0.11 band was net-neutral on $ but PF-dilutive.

### 2D joint ceiling — the production corner IS the joint optimum

The joint cumulative grid (each cell = `tight < T` AND `atr% < A`, clip +50%) rules out a hidden off-diagonal
sweet-spot. Script: [`scripts/equity/tightness_atr_2d_sweep.sql`](../scripts/equity/tightness_atr_2d_sweep.sql).

**PF post-2015 (the era that matters); production corner `(4.5, 0.10)` is the grid max:**

| tight \ atr% | < .06 | < .07 | < .08 | < .09 | < .10 | < .11 |
|---|--:|--:|--:|--:|--:|--:|
| < 4.0 | 1.371 | 1.384 | 1.412 | 1.453 | 1.483 | 1.447 |
| **< 4.5** | 1.355 | 1.425 | 1.470 | 1.501 | **1.520** | 1.478 |
| < 5.0 | 1.314 | 1.387 | 1.398 | 1.429 | 1.427 | 1.386 |
| < 5.5 | 1.375 | 1.433 | 1.433 | 1.438 | 1.434 | 1.389 |
| < 7.0 | 1.276 | 1.304 | 1.322 | 1.305 | 1.318 | 1.291 |

The axes **reinforce** (no trade-off — moving toward the corner along either axis helps); both cliffs (quiet
`< 0.05` ATR%, loose `tight < 7.0`) hold across the other axis. **`(MaxTightness 4.5, MaxAtrPct 0.10)` is the
joint optimum — nothing to change.** *(Full-sample grid and trip-count grid: see the script output / v2 doc
equivalents.)*

### 2D joint FLOOR — entry-day move% × rvol: both floors reinforce; the move floor may be too LOW (2026-06-21)

The volatility *ceilings* (tightness, ATR%) define which bases qualify; the move% and rvol *floors* define how
hard the breakout must announce itself. Joint cumulative grid, each cell = `move ≥ M` **AND** `move < 0.30`
(the blow-off cap always on) **AND** `rvol ≥ R`, clip +50%. Wide dump with both gates opened
(`--up-threshold 0 --max-up-threshold 1000 --rvol-min 0 --rvol-max 100000`); rest production. Script:
[`scripts/equity/move_rvol_2d_sweep.sql`](../scripts/equity/move_rvol_2d_sweep.sql). Production corner
`(move ≥ 10%, rvol ≥ 5)` is **bolded**.

**PF (clipped +50%), rows = move ≥ M, cols = rvol ≥ R:**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.048 | 1.192 | 1.269 | 1.361 | 1.506 | 1.483 |
| ≥ 5% | 1.199 | 1.288 | 1.435 | 1.576 | 1.790 | 1.638 |
| ≥ 10% | 1.364 | 1.425 | **1.590** | 1.641 | 1.777 | 1.552 |
| ≥ 15% | 1.414 | 1.493 | 1.650 | 1.621 | 1.740 | 1.463 |
| ≥ 20% | 1.487 | 1.593 | 1.728 | 1.727 | **2.054** | 1.779 |
| ≥ 25% | 1.430 | 1.490 | 1.768 | 1.617 | 1.981 | 1.240 |

**PF post-2015 (the decision surface):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 1.060 | 1.169 | 1.280 | 1.411 | 1.574 | 1.454 |
| ≥ 5% | 1.205 | 1.262 | 1.428 | 1.593 | 1.728 | 1.558 |
| ≥ 10% | 1.317 | 1.343 | **1.520** | 1.604 | 1.628 | 1.405 |
| ≥ 15% | 1.312 | 1.386 | 1.622 | 1.646 | 1.512 | 1.219 |
| ≥ 20% | 1.455 | 1.560 | 1.754 | 1.758 | 1.755 | 1.473 |
| ≥ 25% | 1.394 | 1.471 | **1.807** | 1.647 | 1.610 | 1.142 |

**Trip counts (n):**

| move \ rvol | ≥1 | ≥3 | ≥5 | ≥7 | ≥10 | ≥15 |
|---|--:|--:|--:|--:|--:|--:|
| ≥ 0% | 493,741 | 39,872 | 14,464 | 7,758 | 4,038 | 2,078 |
| ≥ 5% | 40,622 | 14,181 | 6,076 | 3,187 | 1,550 | 755 |
| ≥ 10% | 9,724 | 6,384 | **3,678** | 2,149 | 1,125 | 578 |
| ≥ 15% | 3,468 | 2,850 | 1,994 | 1,327 | 758 | 418 |
| ≥ 20% | 1,271 | 1,150 | 926 | 679 | 442 | 267 |
| ≥ 25% | 438 | 410 | 359 | 269 | 184 | 121 |

**Findings:**
1. **Both floors monotonically reinforce up the diagonal.** Along rvol (any move row) PF climbs `≥1 → ≥10` then
   dips at `≥15` (the toxic pump tail — bad in *every* row). Along move (any rvol col) PF climbs `≥0 → ≥20–25`.
   The two floors stack: the rich cells are the high-move × high-rvol diagonal, the poor cells the low-low corner
   (`move≥0 × rv≥1 = 1.048` — essentially no edge).
2. **The production move floor of 10% looks too LOW.** The shipped corner `(move≥10, rv≥5)` is post-2015 **1.520**,
   but raising the move floor to **15–20%** at the same rvol lifts it to **1.62 → 1.75** with still-usable size
   (`move≥15 × rv≥5` = 1.622 / n 1,994; `move≥20 × rv≥5` = 1.754 / n 926; `move≥20 × rv≥7` = 1.758 / n 679). The
   `move≥25 × rv≥5` cell is the post-2015 PF max of the whole grid at usable size (**1.807**, n 359).
3. **The `rv≥15` column is a wall** — PF falls off in every move row (toxic blow-off), confirming the high-rvol
   tail is genuinely bad, not merely thin. The deep corner (`move≥25 × rv≥15`, n 121) is noise.

**✅ DECISION (2026-06-21): KEEP the move floor at 10% (`UpThreshold = 0.10`, unchanged).** Raising it would lift
PF (`move≥15` → post-2015 1.622, `move≥20` → 1.754) but at a steep capacity cost — `move≥15` roughly halves the
book (3,678 → 1,994 at rv≥5) and `move≥20` quarters it (→ 926). The 10% floor is the **capacity anchor**; the PF
gain isn't worth that much lost size while sizing/capacity is unmodeled (the backtest is uncapped, non-compounding,
so PF-per-trip is the relevant axis only once a real book constrains concurrency). The finding stands as documented
upside to revisit *after* capital efficiency is modeled — at that point a higher move floor is the first lever to
pull. The prior notch work (25–30% best, 30–40% worst, *which is why the 30% cap exists*) is consistent: within
the [0,30] window the edge keeps rising toward the cap.

### Sub-$5 stocks under the clip — the $5 floor was a raw-PF over-correction (2026-06-21)

The $5 price floor was set in the raw-PF era because the sub-$5 bucket showed a gaudy PF driven by a few lottery
winners. With the clip we can finally see the region honestly. Wide dump with the price floor dropped
(`--min-price 0`, rest production); script [`scripts/equity/sub5_price_sweep.sql`](../scripts/equity/sub5_price_sweep.sql).

**The whole sub-$5 region is NOT a lottery mirage** — its raw PF (1.592) is barely above its clipped PF (1.526),
i.e. *under the production filters there is no fat tail to clip*.

**Where did the original "PF ~6" sub-$5 lottery go? It was never in THIS population — and the cause was NOT the
return clip.** Tracing it back to `momentum_v0_results.md` (the deep-discount structure study), the old inflation
had two compounding causes, both orthogonal to the +50% clip:
1. **It was dollar-P&L-weighted, not return-weighted.** Old PF was computed on dollar `net_pnl`. At fixed $10k
   notional a sub-$5 name buys a huge share count, so a split/adjustment glitch became a giant *dollar* number —
   the v0 doc records **a single trade at +$25M (a ~2,500× return) that was 84.6% of its tier**. The v3 sweeps
   compute PF on per-trade **return** (`exit/entry−1`), which is structurally immune to the share-count explosion
   (a 2,500× glitch is `ret=2500`, not `+$25M`). *This* — return-weighting, not the +50% price-gain clip — is the
   real reason "PF 6" became "PF 1.59".
2. **The lottery lived in a universe we now exclude.** Those glitch trades were **deep-discount, >50%-below-52w-high
   weakness breakouts** (54% sub-$5, median $4.22 per the v0 doc). Production now requires `52w ≥ 0.95` — *near
   highs* — so we never enter the beaten-down penny names where the corrupted rows were.

**Proof (verified 2026-06-21):** the single largest sub-$5 trade in the production population is **+161%** (a clean
$3.34 → $8.71 move) — no monster glitch exists; and `pf_dollar == pf_return == 1.592` *identically* (they only
diverge when a tail glitch is present). The v0 clean test even used a **+500% clip** (10× looser than ours) and
*still* found deep-discount was a loser. So the 1.526 clipped sub-$5 PF is honest and propped up by nothing.

| region | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| sub-$5 [0,5) | 667 | 1.526 | 1.592 | 1.402 |
| ≥ $5 [5,∞) | 3,678 | 1.590 | 2.021 | 1.520 |

Sub-$5 is only **modestly** worse than ≥$5 — not the disaster the floor implied. The price BANDS show the edge is
strongly **non-monotone in price**, and the floor is cutting good names:

| price band | n | PF clip | PF raw | clip post |
|---|--:|--:|--:|--:|
| < $1 | 31 | 1.582 | 1.582 | 0.551 ← broken post-2015, thin |
| $1–2 | 121 | 1.442 | 1.460 | 1.422 |
| $2–3 | 173 | 1.296 | 1.316 | 1.452 |
| **$3–5** | 342 | **1.698** | 1.820 | **1.501** ← better than $20+ |
| **$5–10** | 677 | **2.246** | 2.316 | **2.488** ← best band in the book |
| $10–20 | 920 | 1.780 | 2.854 | 1.608 |
| $20+ | 2,081 | 1.236 | 1.491 | 1.151 ← *worst* band |

Cumulative price FLOOR — **peaks at ~$3, not $5:**

| floor | n | PF clip | clip post |
|---|--:|--:|--:|
| ≥ $0 | 4,345 | 1.575 | 1.492 |
| ≥ $1 | 4,314 | 1.575 | 1.509 |
| ≥ $2 | 4,193 | 1.581 | 1.513 |
| **≥ $3** | 4,020 | **1.604** ← peak | 1.517 |
| ≥ $5 | 3,678 | 1.590 | 1.520 |
| ≥ $10 | 3,001 | 1.417 | 1.297 |

**Findings:**
1. **The edge is low-priced-but-not-penny.** `$5–10` is the single best band in the entire system (clip 2.246,
   **post-2015 2.488**); `$3–5` (clip 1.698, post 1.501) is *better than the `$20+` band the system currently
   trades* (clip 1.236, post 1.151). High-priced large-caps are the WORST band — same "energetic middle beats the
   safe extreme" theme as tightness/ATR%.
2. **The $5 floor is slightly too high.** The cumulative floor peaks at **≥$3** (clip 1.604 / post 1.517), which
   recovers the strong `$3–5` band while still excluding the weak `$1–3` zone and the broken-post-2015 penny band
   (`<$1` post 0.551). Below $3 the $2–3 + penny bands drag PF down.
3. **The aggregate gain is small but it is FREE trips in a GOOD band** (≥$3 vs ≥$5: clip 1.590 → 1.604, post 1.520
   → 1.517 ≈ flat, +342 trips in the 1.70-PF $3–5 band). Not a PF play — a capacity play that doesn't cost quality.

**✅ DECISION (2026-06-21): drop `MinPrice` 5 → 1** (not 3). The cumulative-floor *optimum* is $3, but we
deliberately keep the weak `$1–3` zone IN rather than floor at $3, because those low-priced names are the prime
candidates to be **rescued by the past-runner max-ADR% / max-slope FLOORS** (deferred, not yet in engine — see
below). A flat $3 floor would throw them away before that filter gets a chance; a $1 floor keeps them while still
excluding only the broken-post-2015 sub-$1 penny band (clip_post 0.551). Engine-verified end-to-end at $1:

| filter | trips | PF raw | PF clip | net P&L |
|---|--:|--:|--:|--:|
| breadth, ≥2005 ($5 floor, prior) | 3,678 | 2.021 | 1.590 | +$915k |
| **breadth, ≥2005 ($1 floor, NEW)** | **4,314** | 1.923 | **1.575** | **+$1.07M** |
| breadth + heat<0.25 ($1 floor) | 3,195 | 2.164 | 1.662 | +$879k |

Adding `$1–5` is **+636 breadth-filtered trips (+17%) / +$156k** at **flat clip PF** (1.590 → 1.575 — the honest
measure barely moves; raw dips 2.021 → 1.923 only because the `$1–3` names are weaker, exactly the band the
past-runner floor should lift). Unfiltered engine run: 6,749 trips / PF 1.820 / +$1.55M. `MinPrice = 1.0` in
`defaultConfig`.

---

## Active production-defining findings (carried from v2, still live)

These define the current entry/exit and remain in force. Full derivation and era-split tables are in
[`momentum_v2_results.md`](momentum_v2_results.md) at the linked sections; summarized here as the live spec.

- **5-day time-stop, NO price stop** — on high-edge breakouts the move is concentrated in the first ~5 days;
  moving stops around doesn't help. The "disaster exit" is a SHORT setup, not a long exit (off).
  *(v2: "5-day time-stop, NO price stop"; "edge concentrated in first ~5 days".)*
- **Entry-day move is a NOTCH, not a ramp** — 25–30% is the best band, 30–40% the worst (exhaustion). Cap at 30%.
  ⏳ *Re-confirm under the clip — see "next" below.* *(v2: "Entry-day move — SWEET-SPOT NOTCH".)*
- **rvol three regimes** — <1 fakeout, ~6–15 durable edge, >15 toxic blow-off (the 2020-21 pump cohort, not
  buyouts). Floor at 5; the 30%-move cap supersedes an rvol upper cap. *(v2: "rvol sweep"; "why >15 is neutral".)*
- **Past-runner floor** (deferred, not in engine) — max-over-6mo of 14d ATR%/slope; top decile PF ~2.7, monotone,
  era-robust, gate-amplified. "Higher is better" → use a FLOOR. *(v2: "Past-runner personality".)* **Now also the
  designated rescue for the weak `$1–3` price band** admitted by the 2026-06-21 floor drop (5 → 1): the hypothesis
  is that a low-priced name that has *recently been a runner* (high max-ADR%/slope) carries the breakout edge that
  a generic $1–3 name lacks. First thing to test when this floor goes into the engine.

### Regime filters (post-hoc, not yet in engine)

- **HEAT** (Sykes-inspired, ⭐) — daily mean return of the **top 1% gainers** (**CS/ADRC** + 30-cal-day ADV ≥ $1M
  universe; **+1000% per-stock return clip is LOAD-BEARING**), trailing-10d lagged = `h10`. **Skip entries when
  h10 ≥ 0.25** (80th pctile). Froth is bad for breakouts. *(Heat universe unified to CS/ADRC on 2026-06-21 — see
  next subsection; under the clip the gate gives clip-PF 1.702 / post-2015 1.686 at h10<0.25 vs 1.590/1.520 ungated.)*
- **BREADTH** — `pct_above_20` on CS/ADRC + 30-cal-day ADV ≥ $1M (reproduces the production `breadth.parquet`).
  PF rises with breadth to a peak at ≥0.70, then rolls over; current gate 0.5.
- **COLD** (bottom-1% losers) — same axis as heat (corr −0.65), redundant; not wired.
- Both built by **`scripts/equity/build_breadth_and_heat.sql`**.

#### Heat universe unified to CS/ADRC — drops non-frothy CEFs/preferreds, gates STRONGER, no blocklist (2026-06-21)

Originally the heat universe read the **whole liquid tape** (any ticker ≥ $1M ADV) + a hardcoded `is_test_ticker`
blocklist, on the rationale that top-gainer froth lives in names that lack a clean CS/ADRC ref row. **That
rationale was wrong** (caught by inspection): a recent IPO *is* CS and a foreign listing *is* ADRC — both get ref
rows. The ~16,400 ref-less tickers in the price table break down as ~5,600 genuine non-equity (warrants `…W`,
units `…U`, rights `…R`, dotted class/`.WS`) and the rest mostly **closed-end funds** (MHD, NCV, NRK, DHY, …) and
**preferreds** (lowercase-`p` tickers). CEFs and preferreds **structurally cannot be top-1% gainers** — a muni CEF
doesn't pop 50% — so including them only **dilutes** the froth mean.

Empirically the two universes are **0.815-correlated** but materially different (mean h10 0.172 whole-tape vs
0.179 CS/ADRC — the CS/ADRC version runs *hotter*, confirming the dilution). And the CS/ADRC heat **gates
strictly better** under the clip (production trips, breadth on, ≥2005; script
[`scripts/equity/heat_csadrc_gate_sweep.sql`](../scripts/equity/heat_csadrc_gate_sweep.sql)):

| gate | n | PF clip | clip post | (old whole-tape clip post) |
|---|--:|--:|--:|--:|
| no heat gate | 3,678 | 1.590 | 1.520 | — |
| h10 < 0.20 | 2,476 | 1.778 | **1.819** | 1.722 |
| h10 < 0.25 | 2,772 | 1.702 | **1.686** | 1.620 |
| h10 < 0.30 | 3,051 | 1.689 | 1.666 | 1.527 |

**✅ DECISION (2026-06-21): heat is now built on the CS/ADRC inner join** (unified with breadth + the engine).
The new 80th pctile of h10 is **0.251 ≈ the old 0.25**, so the **gate threshold is unchanged**; it just gates a
cleaner series (post-2015 1.686 vs 1.620 at h10<0.25). `build_breadth_and_heat.sql` heat block switched to
`JOIN ticker_reference ... WHERE type IN ('CS','ADRC')`. The `is_test_ticker` blocklist is now **removed entirely**
(2026-06-21): with the CS/ADRC filter on BOTH universes it never fired — *proven* no-op, 0 test-ticker rows survive
CS/ADRC + $1M ADV on breadth — and a filter that can't fire only misleads a reader into thinking test tickers are a
live threat. The **+1000% return clip stays** (still needed for residual real-CS/ADRC glitches like LU/EPIX).

- **TEST-TICKER mechanics (verified 2026-06-21).** NASDAQ test symbols (ZXZZT etc.) have corrupt 0.0001→$200k
  prices but **NO `ticker_reference` row at all** (an earlier note wrongly called them "tagged CS" — corrected).
  Every CS/ADRC **inner join** therefore drops them for free: the engine (`Backtest.fs`), breadth, and now heat.
  Confirmed 0 test-ticker trips in every dump this session (the 3 `ZY*` names — ZYBT/ZYME/ZYXI — are *real* CS).
  Do **not** remove the engine's CS/ADRC inner join thinking a blocklist covers it — no blocklist runs in the engine.

---

## Reproduction

```bash
# v3 default (2026-06-21): NO price stop, 5-day time-stop, next-open exit; entry filters
# ATR% < 0.10 (log), tightness < 4.5 (linear), entry-move band [10%, 30%), rvol >= 5 (no upper cap),
# price >= $1, ADV >= $100k, 52w-close >= 0.95.
# Run from dataset start for the 252-day warmup; filter entries to >=2005 post-hoc.
dotnet run --project TradingEdge.MomentumV2 -c Release -- \
  --start-date 2003-09-10 --end-date 2026-05-13 -o /tmp/v3.csv

# then apply, post-hoc:
#   breadth_lag1 > 0.5   — LAG(pct_above_20) by 1 trading day on
#                          data/equity/momentum_v0/breadth.parquet, joined on entry_date
#   heat h10 < 0.25      — join data/equity/momentum_v0/heat.parquet on entry_date
#   entry_date >= 2005-01-01
#
# PF/return figures: clip per-trade return at +50% (LEAST(ret,0.50)); decide on cumulative views.
```

All defaults live in `TradingEdge.MomentumV2/Backtest.fs` (`defaultConfig`). Threshold sweeps use the CLI
overrides `--max-atr-pct`, `--max-tightness`, `--rvol-min/--rvol-max`, `--up-threshold/--max-up-threshold`,
`--exit-time-cap`, `--stop-low-window`, `--trail-window`.

For engine internals, the dividend-adjustment data fix, and the in-code TODO, see
[`momentum_v2_results.md`](momentum_v2_results.md) (§ Engine notes / Data fix / Known TODO) — unchanged in v3.

---

## Next session — INTRADAY entries near the open (planned 2026-06-21)

The whole system so far enters **at the close**. Next: test entering the same high-conviction setups **near the
open** and holding a few days, to capture the part of the move that happens *during* the signal day.

**Why retry — it failed before, but under different conditions.** The old opening-range-breakout test used
essentially *every* rvol stock with **no tightness/ATR%/move/breadth/heat filter** — none of the edge-concentrating
conditions discovered since. An intraday entry on the *filtered* population is a genuinely different experiment.

**Open questions to design around:**
- **Entry timing:** at the open? after an opening range (first 5/15/30 min)? "Buy near the open if the setup is
  intact" may beat waiting for a range break.
- **Which signal is known pre-close?** rvol / move / tightness / ATR% are partly intraday — define them as-of the
  entry *moment* (e.g. rvol projected from morning volume), not the full-day close. **Main correctness risk.**
- **Data:** needs intraday bars (`intraday_10s` exists). The engine is daily-bar only — an intraday entry path is
  a real engine change, not a post-hoc SQL study.
- **Special case:** the very-high-rvol blow-offs the close-entry system *excludes* might behave differently entered
  early in the day — deferred, worth a separate look once the harness exists.

The bar is high: the close-entry system is PF ~2.0 raw (filtered, post-2015 clip ~1.5) with multiple independent
edges stacked. Intraday only earns its complexity if it adds materially on top.
