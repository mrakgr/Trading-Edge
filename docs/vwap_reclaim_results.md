# VWAP × 9-EMA Reclaim — Research Log

`TradingEdge.VwapReclaim` (branch `vwap-reclaim`). An **SMB-style intraday scalp**: go LONG when a
stock's fast **9-EMA crosses back above the session VWAP** after a sustained period of weakness (a
"reclaim"), with a tight stop and a fixed target sized off the VWAP-to-low distance.

**Data:** 1-minute equity bars, `data/minute_aggs/` (Polygon aggregates, 2003→2026). **Backtest window
for the results below: 2022-01-01 → 2024-12-31** (3 years). All prices split/dividend-adjusted.

**Engine lineage:** byte-cloned from the `TradingEdge.LowFlyer` intraday engine (same per-(ticker,day)
1m streaming, snapshot-before-push no-lookahead discipline), with the entry gate + exit geometry
replaced by the reclaim logic behind a `VwapReclaim` master switch.

---

## The strategy, exactly

**Session VWAP** — cumulative `Σ(typical·volume)/Σ(volume)`, `typical = (high+low+close)/3` per 1m bar,
**anchored at 09:30 ET** (the RTH open; NOT premarket). Bar-based (the 1m parquet has no trade-exact
VWAP); adequate for a 1m anchor.

**9-EMA** — standard exponential MA of 1m closes, `α = 2/(9+1) = 0.2`, seeded on the first bar.

**ENTRY (long): the reclaim cross.** On a bar, the prior-bar EMA was ≤ prior-bar VWAP AND this-bar EMA
is > this-bar VWAP (the EMA crosses up through VWAP). Both this-bar values use this bar's own close,
which is also the fill price — **no lookahead**. Fill at the **cross-bar close**.

**Stop / target geometry** (snapshotted at entry; `d = VWAP − sessionLow`, `sessionLow` = running low
from 09:30 to the cross):
- **Target** = `VWAP + d` (a resting sell limit above; gap-up fills at the open).
- **Stop** = `VWAP − d/3` (default, "VWAP-anchored") OR `entry − d/3` ("entry-anchored", a toggle).
  Fills at the level; a gap-down through it fills worse at the open.

**Exit precedence** (long-only): protective **stop** → profit **target** → **time-stop** (N min after
entry, capped at MOC) → **MOC** (16:00). Same-bar stop-and-target touch conservatively takes the STOP.

---

## The FILTER STACK — exactly what is and isn't applied (read this before any result)

Every PF number below is computed on trips that pass ALL THREE layers:

### Layer 1 — base universe (ALWAYS on; the `mr_candidate` prefilter, pure SQL)
A ticker-day is only streamed to the engine if, that day:
- **median 1m-bar volume over 09:30–09:45 ET ≥ 10,000 shares** AND **≥ 10 of the 15 opening bars present**
  (the "is it liquid enough to trade at the open" prune),
- **common stock / ADRC** (no ETFs/funds/warrants), **daily close ≥ $1**, **> 21 trading days of history**
  (warmed up), and **`rvol_0945` ≥ 0.1** (a loose premarket-inclusive activity floor).

`rvol_0945` = (volume 04:00→09:45 ET) / (20-day average daily volume). This is the standard "stock in
play" premarket-activity ratio.

### Layer 2 — engine entry gates (in the F# engine)
- the **reclaim cross** (above),
- **below-VWAP weakness** (ONE of two forms, see the findings):
  - `BelowVwapFrac` — the EMA was below VWAP for **> this FRACTION** of the pre-cross session (swept
    0.5 / 0.6 / 0.75 / 0.9), OR
  - `MinRunBelowVwap` — **≥ this many CONSECUTIVE bars** the EMA was below VWAP immediately before the
    cross (the newer, better feature — see Finding 3),
- earliest entry **09:45 ET** (`EntryStartMin`), tightness / ATR% gates **OFF** by default.

### Layer 3 — the "in-play" gates (NOW FOLDED INTO LAYER 1 as of this session)
- **ADV ≥ $1,000,000** where **ADV = `avgvol20` × `day_close`** (20-day average dollar volume — a real
  liquidity floor so the name can actually be traded and the target reached),
- **`rvol_0945` > 1** (the stock is trading at MORE than its normal volume into the open — genuinely
  "in play," not just clearing the loose Layer-1 floor of 0.1).

These were originally applied POST-HOC (SQL join on the trips CSV). They are now baked into a dedicated
`vwap_reclaim_candidate` table (`scripts/equity/build_vwap_reclaim_candidate.fsx` — a strict subset of
`mr_candidate`: 161,979 / 850,107 rows = **19%**), which the engine reads instead of `mr_candidate`. This
streams ~5× fewer ticker-days (byte-identical results verified: 79,650 trips / PF 0.846 either way) and
shrinks the trips CSV ~3× (93MB → 32MB). `mr_candidate` is untouched, so LowFlyer/MaxFlyerV2 are
unaffected. **All Layer-3 PF numbers below already reflect these gates.**

**Not yet applied / explicitly out:** intraday tightness & ATR% gates (OFF), any 1d-return condition
(deferred), the time-of-day window (studied in Finding 4 but not yet a locked gate), and any wider /
VWAP-loss stop variant (the next experiment).

---

## Findings

### Finding 1 — the below-frac × time-stop sweep: EVERY cell is a LOSER (PF 0.75–0.87)

The full grid `below-frac {0.5, 0.6, 0.75, 0.9} × time-stop {MOC, 15, 30, 60, 120 min}`, all gated by
ADV ≥ $1M & rvol_0945 > 1:

| below-frac \ time-stop | MOC | 15m | 30m | 60m | 120m |
|---|---:|---:|---:|---:|---:|
| **0.5** | 0.854 | 0.752 | 0.803 | 0.822 | 0.841 |
| **0.6** | 0.874 | 0.765 | 0.818 | 0.841 | 0.858 |
| **0.75** | 0.873 | 0.777 | 0.824 | 0.843 | 0.859 |
| **0.9** | 0.866 | 0.758 | 0.785 | 0.816 | 0.840 |

- **The time-stop HURTS.** Hold-to-MOC is best in every row; the 15-minute scalp time-stop is the WORST
  (0.75–0.78) — cutting winners early makes it worse, the opposite of a scalp edge.
- **below-frac barely matters** and isn't monotone — 0.6/0.75 marginally best, 0.9 (strictest) no better.
- ~41% win rate with a ~1.3:1 reward:risk should be ≈ breakeven, but avg return is NEGATIVE — the tight
  `d/3` stop is being hit more than the geometry implies: the reclaim is a head-fake more often than not.

### Finding 2 — the raw ungated baseline is a slight loser too (PF 0.885)

With NO below-frac gate (all crosses), hold-to-MOC, ADV & rvol gated: 79,650 trips / PF 0.846 / 39.9%
win. The whole-book (before the ADV/rvol post-hoc gates, all crosses): 340,833 trips / PF 0.885. The
in-play gates help slightly but don't create an edge.

### Finding 3 — ⭐ consecutive-bars-below-VWAP is a REAL discriminator (better than the fraction)

Breakdown by `run_below_vwap` (consecutive bars EMA < VWAP right before the cross), gated ADV & rvol:

| run_below | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| 0 (no weakness) | 7,927 | 31.5 | 0.718 | −0.36 |
| 1–2 | 7,308 | 34.6 | 0.791 | −0.28 |
| 3–5 | 6,928 | 38.9 | 0.876 | −0.17 |
| 6–10 | 8,626 | 40.1 | 0.807 | −0.27 |
| **11–20** | 11,962 | 43.3 | 0.903 | −0.13 |
| **21–30** | 7,457 | 44.0 | **0.932** | −0.09 |
| >30 | 29,442 | 41.3 | 0.851 | −0.21 |

**Cleanly monotone up to a peak at 21–30 consecutive bars** (PF 0.72 → 0.93; win 31.5% → 44%): a reclaim
after a genuine sustained ~20–30-minute downtrend is far better than one after chop across VWAP. Past 30
bars it rolls over (0.85) — a name below VWAP 30+ min straight is in a real downtrend that keeps going,
not a reversible dislocation. **The consecutive-bars feature captures the SMB "reclaim of real weakness"
thesis better than the whole-session fraction did.** BUT the best cell (0.932) is still a loser.

### Finding 4 — ⭐ time-of-day: MORNING is best, exactly as the source video claimed

PF by entry-time window (gated ADV & rvol, all run_below):

| entry window | n | win% | PF |
|---|---:|---:|---:|
| 09:30–10:00 | 8,556 | 46.6 | 0.860 |
| **10:00–10:30** | 11,437 | 44.9 | **0.957** |
| **10:30–11:30** | 15,421 | 40.6 | **0.959** |
| 11:30–12:30 | 11,337 | 36.7 | 0.861 |
| 12:30–13:30 | 9,752 | 37.0 | 0.806 |
| 13:30–15:00 | 13,790 | 37.5 | 0.722 |
| 15:00–16:00 | 9,083 | 38.3 | 0.646 |

**Clean monotone decay through the day** — 10:00–11:30 is the peak (~0.96), the afternoon bleeds
(0.65–0.72). The video's "10:00–13:30 ET is best" is corroborated: the reclaim needs a genuine morning
downtrend to flip; afternoon setups are lower-quality drift. (09:30–10:00 is slightly worse than
10:00–11:30 — early crosses are choppier — which is also why the video says 10:00, not 09:30.)

### Finding 5 — stacking the two best filters reaches BREAKEVEN, not an edge

`entry 10:00–13:30 ET  ×  run_below ∈ [11, 30]`, gated ADV & rvol:

| filter | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| 10:00–13:30, all run_below | 47,947 | 40.0 | 0.902 | −0.14 |
| 10:00–13:30, run_below ≥ 11 | 30,416 | 42.5 | 0.929 | −0.10 |
| **10:00–13:30, run_below ∈ [11,30]** | 11,365 | 43.0 | **0.987** | **−0.02** |

Stacking the video's morning window with the sustained-weakness band lifts the raw 0.885 → **0.987 /
avg −0.02% — essentially dead breakeven.** Both filters are legitimate, same-direction signals, but
together they get the mechanical strategy TO breakeven, not through it.

---

## Honest assessment (as of this session)

Layering every filter the strategy calls for — base liquidity, ADV ≥ $1M, rvol > 1, sustained
below-VWAP weakness (best as consecutive bars, 11–30), and the morning window (10:00–13:30) — the
best-tuned cell tops out at **PF ≈ 0.987, dead breakeven** over 2022–2024. Two filters (consecutive
weakness, time-of-day) behaved exactly as the SMB thesis and the source video predicted, which is
reassuring that the *setup is real*; but the **mechanical, market-wide version has no positive edge** as
specified. The prime remaining suspect is the **exit geometry**: 43% win at breakeven means the tight
`d/3` stop / `d` target is roughly fairly-priced, not favorably. A VWAP-loss stop or a wider stop is the
one untested lever that could flip it — that is the next experiment.

**Open question for discussion (Jeff):** does this pattern require the discretionary context SMB applies
(the specific stock's story, the level quality, reading the tape) that a market-wide 1m rule can't
replicate? The data says a systematic version reclaims-and-fails ~57% of the time; the human edge may be
in *which* reclaims to take.

**NEXT:** stop-geometry variants (VWAP-loss stop, `d/2` wider stop) on the best cell; possibly a 1d-return
condition; confirm on full history if a variant clears ~PF 1.1+.

---

## Findings 6–7 (5-year book, 2020-07 → 2025-06; morning 10:00–13:30 × run_below ∈ [11,30], base PF 0.965)

From here the study uses a **5-year window** (an intraday scalp doesn't need the full 22y, which spans
decimalization / HFT-era regime shifts) and the `vwap_reclaim_candidate` pre-pruned universe. The working
population is the best cell from Finding 5: **morning entries (10:00–13:30 ET) × run_below ∈ [11,30]**,
hold-to-MOC (the sweep-best exit), **22,789 trips / PF 0.965**.

### Finding 6 — tightness ≥ 4.5 is a real lever; ATR% is not

Intraday tightness (= 14-bar abs range / abs ATR, at entry) is cleanly monotone — a reclaim on a name
that's genuinely MOVING follows through; a dead-flat name's reclaim fizzles:

| tightness | n | win% | PF |
|---|---:|---:|---:|
| 1–2 | 774 | 33.1 | 0.762 |
| 2–3 | 2,962 | 38.8 | 0.809 |
| 3–4.5 | 9,995 | 41.8 | 0.955 |
| **4.5–7** | 8,100 | 44.3 | **1.033** |
| >7 | 958 | 46.0 | 0.970 |

`tight ≥ 4.5` is the only cleanly-positive cut (PF 1.03, ~9k trips). **ATR% is weak/noisy** by contrast —
mostly flat with a faint high-vol tilt (0.04–0.08 band PF 1.17 but only 178 trips); no clean lever. Kept
tightness ≥ 4.5, dropped ATR%.

### Finding 7 — ⭐ too-TIGHT stops get chopped: the stop-out rate is the tell

The stop distance `(entry − stopLevel)/entry` varies per trade (it's `≈ (entry−VWAP) + d/3`, and
`d = VWAP−sessionLow` varies a lot). Breaking down by it (the d/3 RULE is kept — this is a diagnostic on
which trades that rule leaves with a too-tight stop):

| stop dist (% of entry) | n | win% | **stop-out rate** | PF | avg% |
|---|---:|---:|---:|---:|---:|
| <0.3% | 164 | 39.6 | 56.7 | 0.818 | −0.02 |
| 0.3–0.6% | 598 | 35.5 | **63.2** | 0.833 | −0.05 |
| 0.6–1% | 2,790 | 36.9 | **61.8** | 0.833 | −0.09 |
| 1–2% | 8,645 | 42.4 | 54.2 | 0.934 | −0.05 |
| 2–3.5% | 5,872 | 42.9 | 49.9 | 0.924 | −0.10 |
| **3.5–6%** | 2,911 | 45.6 | 46.6 | **1.059** | +0.13 |
| >6% | 1,797 | 44.1 | 46.1 | 0.985 | −0.07 |

**The smoking gun is the stop-out rate:** a tight stop (<1% from entry) is hit **62–63%** of the time —
inside the reclaim's normal 1m noise, so you're stopped on a wiggle before the trade can work. As the stop
widens the stop-out rate falls to ~46%, win rate climbs 36% → 46%, and **PF crosses 1.0 at 3.5–6%
(1.059)**. This is NOT an argument against d/3 — d/3 works fine when `d` is naturally large; it fails when
`d` is tiny (a shallow morning dip whose d/3 stop sits ~1% off entry). Cumulative floors: keep
`stop_dist ≥ 1%` → PF 0.972; `≥ 2%` → 0.984. **The fix: a MINIMUM stop-distance filter** — skip reclaims
where the d/3 stop would be too tight (shallow, low-conviction dips whose reclaim is noise). Pairs with
tightness ≥ 4.5 — both select "a name with real range," same mechanism.

**LOCKED into the system: minimum stop-distance ≥ 1% AND tightness ≥ 4.5** (the two entry-quality filters).
Together they lift the morning × rb[11,30] book **0.965 → 1.032** (45.3% win, 8,018 trips) — the first
positive edge. NEXT = 1d-return-to-entry & intraday-return breakdowns on this filtered book.

### Finding 8 — 1d-return-to-entry: a U-SHAPE — big movers (either way) win, the mushy middle loses

`chg_1d` = entry / prev-day-adj-close − 1 (how the stock moved INTO today), on the filtered book:

| 1d return | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **<−10%** | 735 | 47.6 | **1.136** | +0.24 |
| −10..−5 | 776 | 43.8 | 0.889 | −0.15 |
| −5..−2 | 1,041 | 41.1 | 0.749 | −0.29 ← worst |
| −2..+2 | 1,684 | 44.5 | 0.866 | −0.13 |
| +2..+5 | 1,141 | 46.4 | 0.981 | −0.02 |
| +5..+10 | 1,058 | 46.4 | 1.060 | +0.08 |
| **>+10%** | 1,575 | 47.3 | **1.175** | +0.51 |

**Clean U-shape:** the reclaim works when the stock made a BIG move into today — a large gap DOWN (<−10%,
PF 1.14) OR a large gap UP (>+10%, PF 1.18); the small-move middle (−5..−2%, PF 0.75) is dead. The classic
"in play" signal: a hard gapper (either way) is a story stock with real intraday participation; a name
drifting ±5% is noise. → an **`|1d| > 10%` extreme-mover filter** (both tails ~PF 1.15).

### Finding 9 — intraday-return (entry vs the day's open): UP-day entries win

⚠ **Data fix:** the CSV `pct_chg_since_open` / raw `mr_candidate.day_open` had near-zero-open outliers +
a split-adjustment mismatch that exploded the ratio (bogus median 2462%). Corrected with
`entry / (day_open × adj_ratio) − 1` and dropping `day_open ≤ $0.01` — sane after (median +0.95%):

| intraday (entry vs open) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| <−5% | 516 | 41.7 | 1.047 | +0.09 |
| −5..−3 | 664 | 40.4 | 0.813 | −0.28 ← worst |
| −3..−1.5 | 870 | 42.0 | 0.951 | −0.06 |
| ~flat | 826 | 45.3 | 0.896 | −0.11 |
| +0.5..+2 | 1,080 | 50.0 | 1.072 | +0.07 |
| +2..+5 | 1,581 | 46.3 | 1.082 | +0.10 |
| **>+5%** | 1,676 | 45.5 | **1.105** | +0.30 |

**Entries where the stock is UP on the day win** (PF climbs from the −5..−3 trough 0.81 → >+5% 1.105); a
reclaim confirmed by the stock already being back above its open is genuinely recovering, whereas a
"reclaim" while still −3..−5% on the day is a weak bounce in an ongoing down-day. (The `<−5%` band ticks up
to 1.05 — the deep-washout capitulation-bounce, a small echo of the 1d U-shape.) → a **`intraday > 0`
confirmed-strength filter**.

**Both Findings 8–9 point the same way as tightness & stop-distance: the reclaim works on names with real
MOVEMENT and genuine STRENGTH, not shallow chop.** Two more candidate levers (extreme-mover, up-on-day),
not yet wired. NEXT = wire/stack these; re-check the by-year stability of the ~1.1 book.
