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

---

## Findings 10–13 (the LIQUIDITY + EXIT re-work, driven by looking at the actual charts)

Built `scripts/visualization/vwap_reclaim_charts.py` (1m candles + session VWAP + 9-EMA + below-VWAP
shading + entry/exit/stop/target markers) to judge the trades QUALITATIVELY — and it immediately exposed
problems the aggregates hid.

### Finding 10 — ⭐ the $1M ADV floor was ILLIQUID JUNK; $100M is the sweet spot

The charts of the $1M-ADV book were sub-dollar / thin-float trash (AEMD, AGEN, AKAN, ARQQ, ALLR…) with
garbage 1m bars and unrealistic fills. Raised **ADV = avgvol20 × day_close from $1M → $100M** in
`vwap_reclaim_candidate` (universe: 19% → **2.7%** of mr_candidate, 23,324 rows). The edge STRENGTHENED on
genuinely liquid names (ABNB, DDOG, CHPT, LCID, LAZR, FRC…): whole book 0.836 → **1.023**; production cell
(morning × rb[11,30] + filters) **1.032 → 1.151 / 49.4% win / 713 trips (~140/yr).** Edge improving on
liquid names is the right sign from a real setup — junk names are where backtests lie.

**But ADV higher than $100M HURTS** (cohort sweep on the production cell):

| ADV floor | n | win% | PF |
|---|---:|---:|---:|
| **≥$100M** | 713 | 49.4 | **1.151** |
| ≥$500M | 99 | 43.4 | 0.898 |
| ≥$1B | 36 | 38.9 | 0.726 |
| ≥$2B | 12 | 33.3 | 0.533 |

Monotone decay past $100M. Mechanism: a high-ADV mega-cap on a normal day is too EFFICIENT / VWAP-anchored
— it chops around VWAP without committing, so the reclaim doesn't follow through. **The sweet spot is the
$100M–500M liquid-story-stock zone: liquid enough for clean fills, still MOVING enough to trend.** $100M is
the settled floor.

### Finding 11 — CLOSE-based stop (ignore noise wicks) → 1.151 → 1.199

The stop triggered on any bar whose LOW touched the level, so a single-print wick down that immediately
recovered stopped you out. Changed the default to **CLOSE-based** (the bar must CLOSE at/below the stop,
fills at that close; `--wick-stop` reverts). Same entries, better exits — production cell:

| stop mode | n | win% | PF | stop-rate | target | moc |
|---|---:|---:|---:|---:|---:|---:|
| CLOSE (new) | 713 | 50.9 | **1.199** | 41.9 | 40.1 | 18.0 |
| WICK (old) | 713 | 49.4 | 1.151 | 44.7 | 39.0 | 16.3 |

Stop-out rate 44.7 → 41.9% (noise wicks no longer trigger); the rescued trades reallocate to target/MOC.
More realistic too (you wouldn't fill at a spike low that recovers). Locked CLOSE-based.

### Finding 12 — FLOAT: bigger float wins, cleanly monotone (a sizing/selection tilt)

Float breakdown on the $100M close-stop production cell (coverage 67%; float re-anchored to entry price,
no-lookahead ASOF on filing known_date — the canonical method):

| float$ | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| <300M | 94 | 51.1 | 0.987 | −0.03 |
| 300M–1B | 82 | 48.8 | 1.073 | +0.10 |
| 1–3B | 166 | 53.6 | 1.228 | +0.23 |
| 3–10B | 88 | 53.4 | 1.386 | +0.35 |
| **>10B** | 48 | 58.3 | **2.157** | +0.61 |

Cumulative: `<1B → 1.017`, **`≥3B → 1.571`** (55% win). Monotone big-float-wins. NOTE this is float (a
COMPANY's size), NOT ADV (Finding 10, dollar VOLUME) — they're different axes: you want a big, REAL company
(high float) that is UNUSUALLY ACTIVE today (rvol>1) but is NOT a hyper-efficient always-liquid mega-cap
(low-ish ADV). Float picks out "big legitimate company in play" — exactly the SMB thesis. **Kept as a
sizing/selection TILT, not a hard gate** (float coverage only 67%; the no-data bucket is actually the best
at 1.245; SEC float data is spotty — favor big float, source better data before live). Same discipline as
the flyer/TideFlyer float findings.

### Finding 13 — ⭐ REMOVE THE TARGET: let winners run → PF 1.199 → 1.478 (the user's chart read)

Looking at the charts, the fixed `VWAP+d` target looked like it was cutting winners short. Tested a
`--no-target` mode (exits = stop → time-stop → MOC only). Production cell:

| exit model | n | win% | PF | avg% | stop | target | moc |
|---|---:|---:|---:|---:|---:|---:|---:|
| WITH target (old default) | 713 | 50.9 | 1.199 | +0.27 | 41.9 | 40.1 | 18.0 |
| **NO target (run to MOC)** | 713 | 37.3 | **1.478** | **+0.84** | 54.7 | 0.0 | 45.3 |

**PF 1.199 → 1.478, avg return per trade TRIPLES (+0.27 → +0.84%).** Win rate DROPS 50.9 → 37.3% — the
classic "let winners run" signature: the target was converting small winners but CAPPING the big ones; the
40% that used to hit target now ride to MOC, and the runners more than pay for the extra losers (some
round-trip back to the stop, raising stop-rate to 54.7%). Whole-book hold-to-MOC PF 1.023 → **1.401** too,
so it helps everywhere, not just the cell. **The reclaim is a momentum-continuation play, not a
mean-reversion scalp — you want the whole move, not a fixed target.** Big result from the user's visual
read. Kept `UseTarget=false` as a strong candidate default (pending by-year stability).

**Current best book: $100M ADV · morning 10:00–13:30 · rb[11,30] · tight≥4.5 · min-stop-dist≥1% ·
close-stop · NO target → PF ~1.48 on the production cell.** NEXT = by-year stability (does 1.48 hold
across years or is it a couple of years?); stack the float/extreme-mover tilts; render the >$1B charts to
confirm the "mega-caps chop" mechanism visually.

### Finding 14 — ⭐ WIDEN the stop to d·2/3: no-target + wide stop go together → PF 1.478 → 1.689

With the target off (Finding 13) the stop is the ONLY downside cut, so its width matters more. Swept
`StopDistFrac` (stop = VWAP − d·frac) on the no-target $100M production cell:

| stop width | n | win% | PF | avg% | stop-rate | net $k |
|---|---:|---:|---:|---:|---:|---:|
| d/3 (video's rule, old) | 713 | 37.3 | 1.478 | +0.84 | 54.7 | 60 |
| d/2 | 809 | 42.0 | 1.645 | +1.10 | 45.7 | 89 |
| **d·2/3** | 847 | 44.7 | **1.689** | **+1.19** | 37.9 | **101** |
| d (full) | 862 | 47.9 | 1.612 | +1.13 | 24.8 | 97 |

**Inverted-U, peak at d·2/3.** Two effects, both pushing up: (1) the wider stop clears the 1% min-stop
filter (Finding 7) on more setups (713 → 847 trips); (2) stop-rate collapses 55% → 38% — the reclaim
survives its initial pullback and runs to MOC instead of getting shaken out. **The tight d/3 was fighting
the "let it run" thesis** — once you commit to riding the whole move (no target), you must give the trade
room to breathe. Full-d gives a little back (losers too expensive). **Locked `StopDistFrac = 2/3`.**
(NOTE: min-stop-distance filter = 1% is STILL ON, unchanged — it's a separate lever, skipping trades whose
stop is too tight; the stop WIDTH is what changed here.) Best book now: $100M · morning · rb[11,30] ·
tight≥4.5 · min-stop≥1% · close-stop · no-target · **stop d·2/3 → PF 1.689 / +1.19% avg / 847 trips**.
NEXT = by-year stability of the ~1.69 book.

### Finding 15 — min-stop CLAMP vs SKIP: ~identical at d·2/3 (clamp is the principled default now)

When the geometric stop is tighter than the 1% minimum (Finding 7), the original behavior SKIPPED the
trade. The user's mental model was CLAMP — keep the trade, widen the stop to exactly 1%. Tested both on the
best book (no-target, stop d·2/3), production cell:

| min-stop mode | n | win% | PF | avg% | net $k |
|---|---:|---:|---:|---:|---:|
| SKIP (original) | 847 | 44.7 | 1.689 | +1.19 | 101 |
| **CLAMP to 1%** | 868 | 44.5 | 1.690 | +1.18 | 102 |

**A dead heat** — clamp adds only 21 trades and PF is unchanged (1.689 vs 1.690). The reason: once the stop
is d·2/3 WIDE (Finding 14), almost every setup already clears the 1% floor, so the disputed zone is nearly
empty and the 1% min-stop filter is now largely INERT (it did real work back at d/3 when stops were tight).
Clamp is the more principled behavior and is harmless-to-marginally-better (the 21 clamped trades perform
in line with the book), so **made CLAMP the default** (`--skip-tight-stop` reverts). No material effect
either way at the current stop width.

### Finding 16 — NO stop at all is WORSE: the d·2/3 stop cuts a real disaster tail (asymmetry confirmed)

The logical endpoint of "widen the stop" (Finding 14): remove it entirely (pure hold-to-MOC). Tested on
the best book (no-target), production cell:

| exit | n | win% | PF | avg% | net $k | worst trade |
|---|---:|---:|---:|---:|---:|---:|
| **stop d·2/3 (current)** | 847 | 44.7 | **1.689** | +1.19 | **101** | −21.2% |
| NO STOP (hold to MOC) | 868 | 50.3 | 1.541 | +1.06 | 92 | −36.8% |

**No stop is WORSE** (PF 1.689 → 1.541, net $101k → $92k) — the OPPOSITE signature of removing the target
(Finding 13). Removing the target: win↓ PF↑ (it capped winners). Removing the stop: **win↑ (50.3%) but
PF↓** — without a stop more dips recover green by MOC (+5.6% win), BUT the ones that don't run to a
**−36.8% catastrophic MOC loss** vs −21% cut. The fat left tail eats the extra small wins. **So the exit
structure is a clean ASYMMETRY, exactly right for a momentum-continuation play: KEEP the stop (cut the
disaster tail, d·2/3 width) + NO target (let winners run).** The d·2/3 stop beats tighter stops (Finding
14) AND no stop — a genuine local optimum. Confirmed; no change.

### Finding 17 — the d·2/3 GEOMETRY beats every fixed-% stop (the stop must adapt to the name's range)

Does the stop's edge come from the VWAP-to-low geometry, or just from having a cut at a sensible distance?
The d·2/3 stop distance is a WIDE distribution (production cell: p25 1.9% / med 2.9% / p75 4.6%) — it
adapts per name. Swept a FIXED %-below-entry stop against it (no-target book, production cell):

| stop | n | win% | PF | avg% | net $k |
|---|---:|---:|---:|---:|---:|
| **GEOMETRY d·2/3** | 847 | 44.7 | **1.689** | +1.19 | **101** |
| fixed 2% | 868 | 38.9 | 1.400 | +0.51 | 45 |
| fixed 3% | 868 | 44.1 | 1.318 | +0.47 | 40 |
| fixed 4% | 868 | 46.5 | 1.379 | +0.60 | 52 |
| fixed 5% | 868 | 48.3 | 1.404 | +0.68 | 59 |

**The geometry WINS decisively** — the best fixed % (5% → 1.404) is far below 1.689, and the geometry earns
~2× the net P&L ($101k vs $40–59k). No fixed value is competitive. **Validates the core SMB mechanic: the
stop must ADAPT to the name's own intraday range.** A fixed % is too TIGHT for volatile names (stopped out
of good trades — fixed-3% PF 1.32) and too WIDE for calm names (gives back too much on losers). `VWAP −
sessionLow` is a per-name volatility proxy that sizes the stop correctly — same spirit as an ATR stop but
anchored to the setup's actual VWAP structure. The geometry isn't arbitrary; it does real adaptive work no
fixed number replicates. **Kept d·2/3 geometry.**

### Finding 18 — the 1% min-stop filter is fully INERT at d·2/3 → dropped it

Removed the 1% minimum entirely (pure d·2/3, no floor). Production cell: **byte-identical** — 868 trips,
PF 1.690 (clamp) vs 1.693 (no min), same net P&L. The reason is in the distribution: at d·2/3 even the
**p5 stop distance is 1.17%** — i.e. NOTHING lands under 1%, so the filter has nothing to act on. It was
load-bearing at d/3 (Finding 7, where the sub-1% tail was real chop), but widening the stop (Finding 14)
fully subsumed its job. Three tests now converge: clamp≈skip (Finding 15), no-min≈clamp (here), all dead
heats. **Dropped `MinStopDistPct` to 0 to SIMPLIFY** (one fewer knob to explain; `--min-stop-dist-pct`
re-enables). No performance change. The exit is now just: close-based d·2/3 stop + no target + MOC.

**FINAL production cell (2020-07→2025-06): $100M ADV · morning 10:00–13:30 · rb[11,30] · tight≥4.5 ·
close-stop · NO target · stop d·2/3 → PF 1.69 / +1.19% avg / 868 trips (~170/yr).** The exit structure is
fully stress-tested: d·2/3 beats tighter (F14), no-stop (F16), and fixed-% (F17); close-trigger beats wick
(F11); no-target beats target (F13). Every choice is a confirmed local optimum. NEXT (the make-or-break) =
by-year stability of this ~1.69 book.

### Finding 19 — tightness ≥ 4.5 RE-CONFIRMED under the new exits (still load-bearing)

Re-ran the tightness breakdown on the CURRENT exit structure (no-target, d·2/3, close-stop) — tightness was
locked (Finding 6) back when we still had a target + d/3 stop, so worth re-checking. Morning × rb[11,30],
tightness gate OFF:

| tightness | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| 2-3 | 135 | 35.6 | 1.041 | +0.06 |
| 3-4.5 | 1,174 | 41.0 | 1.499 | +0.83 |
| **4.5-6** | 750 | 43.2 | **1.740** | +1.26 |
| 6-9 | 118 | 52.5 | 1.387 | +0.65 |

Cumulative: no gate 1.562 → **`≥4.5` 1.693** (peak) → `≥6` 1.387 (over-filtered, thin). **tight ≥ 4.5
still correct** — lifts the book ~0.13 PF by cutting the weak 2-4.5 slow-movers, keeping the 4.5-6 sweet
spot; ≥6 over-filters. If anything it matters MORE now that winners run to MOC — you need names that KEEP
MOVING all day, not ones that reclaim then die flat. No change.

### Finding 20 — the $100M ADV floor was OVER-cutting: $30M–$1B nearly TRIPLES the book at ~flat PF

**The big one.** The $100M ADV floor (Finding 10) was set from illiquid-junk CHARTS back under the OLD
exit (target + d/3 stop). Now that the exit is fixed (close-stop d·2/3, NO target), re-ran the ADV
breakdown over the WIDE universe (`vwap_reclaim_candidate_wide` = mr_candidate WHERE `rvol_0945>1`, NO
ADV floor — 183,590 ticker-days vs 23,324; engine env `VWR_CANDIDATE_TABLE` override). Production cell
slices re-imposed in SQL (morning 10:00–13:30, rb[11,30], tight≥4.5). PF = raw `ret_moc` (no clip).

**ADV per-bucket (production cell):**

| ADV bucket | n | win% | PF | avg% | net$k |
|---|---:|---:|---:|---:|---:|
| <10M | 2407 | 35.5 | **0.695** | −0.82 | **−197** |
| 10–30M | 940 | 39.1 | 1.183 | +0.39 | +36 |
| **30–100M** | 596 | 43.6 | **2.003** | +1.71 | **+102** |
| **100–300M** | 237 | 38.0 | **2.402** | +2.38 | +56 |
| 300M–1B | 63 | 46.0 | 1.456 | +0.72 | +5 |
| 1–5B | 10 | 40.0 | 0.957 | — | −0 |
| >5B | 1 | 0.0 | — | — | −2 |

**Inverted-U, and the peak sits ON the $30–300M band the $100M floor was cutting in half.** Below $30M is
a graveyard (PF 0.70–1.18, `<10M` loses $197k — that IS the illiquid junk the charts flagged, but it's
the sub-$30M names, not the $30–100M ones). Above ~$1B the pattern dies (mega-caps too VWAP-anchored,
Finding 10's other half — confirmed, just at a higher threshold than we'd set).

**ADV floor sweep (band vs current $100M), morning production cell:**

| floor | n | win% | PF | avg% | net$k |
|---|---:|---:|---:|---:|---:|
| **CURRENT ≥100M** | 311 | 39.5 | **2.100** | +1.89 | 59 |
| ≥50M (–1B cap) | 607 | 41.8 | 2.175 | +1.95 | 118 |
| **≥30M (–1B cap)** | 896 | 42.3 | **2.073** | +1.82 | **163** |
| ≥20M (–5B) | 1224 | 41.2 | 1.753 | +1.37 | 167 |
| 30–300M | 833 | 42.0 | 2.116 | +1.90 | 158 |

**Lowering the floor $100M → $30M (with a soft ~$1B ceiling) gives 2.9× the trips (311→896) and 2.8× the
net P&L ($59k→$163k) for a trivial PF give-up (2.10→2.07).** Holds all-day too (≥100M PF 2.06/558 →
30M–1B PF 1.87/1597, 2.4× net). **ACTION: drop the candidate ADV floor to $30M** (keep a $1B-ish soft cap
as a documented tilt — the ≥$1B tail is thin & choppy). The $100M number was a chart-eyeballing artifact
under the wrong exit; the data now says $30M. This is the largest single improvement since no-target.

### Finding 21 — float is a WEAK, ADV-dominated lever here (NOT a low-float edge; 150–300M is the only bump)

Same population, float re-anchored to entry-day price (canonical `tideflyer_float.sql` method, ASOF
known_date ≤ entry, no-lookahead). Coverage ~69% (NO-DATA bucket = 1,333/4,254).

| float bucket | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| NO DATA | 1333 | 35.3 | 0.802 | −0.53 |
| <150M | 1731 | 37.3 | 1.025 | +0.07 |
| **150–300M** | 326 | 43.9 | **1.853** | +1.27 |
| 300–750M | 351 | 39.0 | 1.237 | +0.37 |
| 750M–2B | 275 | 39.3 | 1.091 | +0.11 |
| 2–10B | 171 | 41.5 | 0.962 | −0.05 |
| >10B | 67 | 46.3 | 1.144 | +0.21 |

**Float is NOT the clean lever it is on the LowFlyer long / HighFlyer.** Low float (<150M) is ~breakeven
(PF 1.03), NOT a win — the opposite of what a "small float squeezes harder" prior would predict. The only
real bump is **150–300M (PF 1.85)**, and it's almost certainly the ADV effect in disguise: the profitable
$30–300M ADV names cluster in the 150–300M float band, while the <150M-float / NO-DATA buckets are packed
with the sub-$30M-ADV junk that Finding 20 already cuts. Cumulative float-floor `≥150M` (PF 1.35) barely
beats `all` — a mild lift, fully explained by excluding the no-data junk. **Verdict: don't add a float
gate; ADV already does its work. Float stays a documented non-lever for this system** (unlike float being
a headline lever for the multi-day longs — an intraday reclaim just isn't a float-squeeze play). Contrast
with Finding 12's "big wins at >10B" tilt, which shrinks to PF 1.14 here — also not worth wiring.

### Finding 22 — the SHORT mirror (loss-of-VWAP) is STRUCTURALLY WEAK: PF ~1.25 best vs 2.07 long

Mirrored the whole system to the short side (`--reclaim-short`): enter when the 9-EMA crosses BELOW VWAP
after sustained STRENGTH (EMA above VWAP for the run), geometry flipped — d = sessionHIGH − VWAP, target
VWAP−d (below), stop VWAP+d·frac (above), P&L short. All gates apply symmetrically (the rb-band now reads
the ABOVE-VWAP run). Engine change verified P&L-BYTE-NEUTRAL for the long (every entry/exit/reason/net_pnl
identical; only the `stop_dist_pct` diagnostic went sign-consistent). Same production cell (morning
10:00–13:30, rb[11,30], tight≥4.5), same wide universe, same settled exit (no-target, close-stop d·2/3).

**Short ADV per-bucket (production cell):**

| ADV bucket | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| <10M | 2036 | 43.7 | 1.008 | +0.81 |
| 10–30M | 815 | 43.8 | 0.897 | +0.24 |
| **30–100M** | 543 | 49.9 | **1.273** | +0.76 |
| 100–300M | 213 | 44.1 | 1.070 | +0.42 |
| 300M–1B | 54 | 33.3 | 1.076 | +1.23 |
| >1B | 22 | 27.3 | 0.571 | −0.58 |

Best cell **30M–1B → PF 1.206** (vs the LONG's **2.073** on the byte-identical cell). Every ADV tier is
~breakeven-to-1.27 — there is no tier where the short has a real edge. Exit-tuning doesn't rescue it:

| short exit variant (30M–1B) | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| **baseline: no-target, d·2/3** | 810 | 47.3 | **1.206** | +0.70 |
| d/2 stop | 810 | 44.8 | 1.249 | +0.73 |
| d stop | 810 | 50.1 | 1.222 | +0.83 |
| WITH target | 810 | 57.3 | **1.000** | +0.20 |

No stop width lifts it past ~1.25, and adding a target KILLS it (PF→1.00 — same asymmetry as the long:
the target caps the momentum-continuation move). **This is a SIGNAL problem, not an exit problem.**
Mechanism: the long reclaim is a momentum-continuation play that works because intraday DIPS in liquid
in-play names get BOUGHT (reclaim-of-VWAP rides the long-side drift). The short mirror fights that same
drift — "loss of VWAP after strength" is a much noisier, weaker edge because the dip-buying that powers the
long is exactly what fades the short. **Consistent with the LowFlyer(long)/MaxFlyerV2(short) lesson: longs
and shorts are DIFFERENT books, not mirror images.** VERDICT: **do NOT trade the reverse-reclaim short.**
The `--reclaim-short` path stays in the engine as a documented negative result (and for future asymmetric
short research), but VwapReclaim remains a LONG-only system.

### Note — `UseTarget` default flipped to FALSE (matches the settled system)

Since Finding 13 (no-target wins) all research has been run with `--no-target`, but the engine DEFAULT
still had `UseTarget=true` — so a bare `dotnet run` printed the WRONG (target-on) book, a footgun for
sharing/repro. Flipped: `UseTarget=false` is now the default (a bare run reproduces the settled no-target
system); the flag inverted to `--use-target` to re-enable the VWAP±d target for testing. No research
numbers change (every finding already used no-target).

### Finding 23 — THE FAT BOOK: drop the rb≤30 cap → 6.5× the trips at PF 1.48, positive every modern year

The by-year stability check (the make-or-break gate) on the thin production cell ($30M-1B, morning,
**rb[11,30]**, tight≥4.5) revealed a fatal trip-count collapse even though PF>1 every year:

| year | rb[11,30] n | rb[11,30] PF | | rb≥11 no-cap n | rb≥11 PF |
|---|---:|---:|---|---:|---:|
| 2020 | 90 | 3.07 | | 673 | 1.72 |
| 2021 | 391 | 1.52 | | 2716 | 1.13 |
| 2022 | 215 | 1.20 | | 1264 | 1.23 |
| 2023 | 101 | 5.62 | | 590 | 2.81 |
| 2024 | 64 | 1.82 | | 501 | 1.86 |
| 2025 | **35** | 3.74 | | 200 | 1.92 |

**The `rb ≤ 30` UPPER CAP was the strangler** — it threw away the deep-weakness names (rb 30–390, the bulk
of the book) for a PF premium that does NOT survive the trip-count trade. Removing it (just **rb ≥ 11**, no
cap): **896 → 5,829 trips (6.5×), PF 2.07 → 1.48, net $163k → $526k (3.2×), positive EVERY year (worst
1.13), and 2025's 35 trips → 196** (a real sample, not a coin-flip). Lowering the floor further (rb≥5/≥3)
adds almost nothing — the entire lever is REMOVING the cap, not lowering the floor. The book is BROADLY
earned: top 1% of trips = 32% of gross wins (right-skewed, as a "run-to-MOC" momentum play should be), and
every year stays strongly positive after removing its single biggest winner (2025 $57k→$32k). This is the
sizing-thesis proof: **a PF-1.48 book with ~6k trips beats a PF-2.07 book with 896 for ~3× the dollars AND
more year-to-year robustness.** Also dropping the now-redundant `BelowVwapFrac>0.6` gate (rb≥11 supersedes
it) recovered ~2,700 more trips for free. **WIRED into the engine as defaults** (rb≥11, frac OFF) + a new
`EntryEndMin` gate (10:00–13:30 morning window, previously post-hoc SQL) so a bare run prints the real book.

### Finding 24 — tight ≥ 3 (down from 4.5) on the fat book: 1.7× trips, 1.6× net, still positive every year

Finding 6/19 locked tight≥4.5 on the THIN book. On the fat book the 3–4.5 tightness band is +EV too:

| tight floor | trips | PF | net$k |
|---|---:|---:|---:|
| ≥4.5 | 8,694 | 1.390 | 635 |
| **≥3** | 14,907 | 1.376 | **1,021** |
| ≥0 (off) | 16,306 | 1.367 | 1,065 |

**tight≥3 → 1.7× the trips and 1.6× net ($635k→$1.02M) for a trivial PF give-up (1.390→1.376).** Below 3 is
nearly dead (≥0 barely adds over ≥3). By-year (2020-25): positive every year, worst 1.17 (2021), 2025 =
507 trips. **Made tight≥3 the default.** Settled 5y fat book: **14,907 trips / PF 1.376 / $1.02M**.

### Finding 25 — ⚠ THE 22-YEAR TEST: a POST-2020 edge, essentially FLAT before COVID (regime-dependent)

Ran the full history (2003-09 → 2026-06) on the default fat book. **41,027 trips / PF 1.298 / $1.50M** in
aggregate — but the by-year decomposition is the real story:

| era | trips | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| **2003–2019** (17y) | 24,239 | **1.061** | +0.05% | +116 |
| **2020–2026** (modern) | 16,788 | **1.441** | +0.83% | **+1,387** |

**92% of the P&L comes from 2020 onward, on FEWER trips.** Pre-2020 is essentially flat — PF 1.06, +0.05%
per trip (~$5/trade before costs), with **6 losing years of 17** (2004/07/08/10/14/17). The modern era is
strong and STRENGTHENING every year (2020 1.56 → 2023 1.92 → 2026 2.37). Critically, the pre-2020 flatness
CANNOT be gated away: rb≥20 leaves it at 1.065; even tight≥6 only reaches 1.17 (and guts the count). There
is no sub-slice where a pre-2020 edge hides — it genuinely wasn't there.

**Interpretation:** VwapReclaim is a **regime-dependent MODERN edge**, same signature as the momentum
systems — it works because of post-2020 intraday dynamics (retail flow watching VWAP as a level, meme-era
low-float churn, fast dip-buying of in-play names). The identical mechanical setup was a coin flip
pre-2020. **NOT disqualifying** (6+ modern years, every one positive, improving), but it reframes the
honest pitch from "22-year-robust" to **"a post-2020 intraday edge, flat before COVID — trade it as a
modern-regime system, with the standing risk that a reversion to pre-2020 dynamics takes the edge with
it."** This is the key caveat for any live deployment or external share.

### Finding 26 — rvol_0945 floor sweep: PF is MONOTONE in rvol; loosening below 1.0 adds uneconomic trips

Tested loosening the candidate `rvol_0945 > 1` gate to ≥0.5 and ≥0.25 (built `vwap_reclaim_candidate_rvol025`
= $30M ADV + rvol≥0.25, 173k rows; ran the default fat book over it, 5y). Bucketed on `rvol_0945` (the
09:45 15-min relative volume — the candidate gate, NOT the per-trip `bar_rvol_15m`):

| rvol_0945 bucket | n | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| 0.25–0.5 | 17,895 | 1.090 | +0.089 | 159 |
| 0.5–0.75 | 5,511 | 1.228 | +0.271 | 149 |
| 0.75–1.0 | 3,356 | 1.154 | +0.204 | 68 |
| 1.0–2.0 | 6,010 | 1.222 | +0.311 | 187 |
| 2.0–5.0 | 4,233 | **1.713** | +1.153 | 488 |
| ≥5.0 | 4,664 | 1.291 | +0.743 | 346 |

**PF rises ~monotonically with rvol** (more "in play" = cleaner reclaim; the 2–5 bucket is the star at PF
1.71). Cumulative floors: ≥0.25 → PF 1.25 / $1398k; ≥0.5 → 1.33 / $1239k; ≥1.0 (current) → **1.38 /
$1021k**; ≥2.0 → 1.45 / $835k. Loosening adds NET DOLLARS but at falling quality. By-year: both ≥0.5 and
≥0.25 stay positive EVERY year (no regime hole), and even the 0.25–0.5 band alone is positive every year
(PF 1.02–1.11) — but **thin and eroding** (1.09 in 2020 → 1.02 in 2025).

**The cost test settles it.** The 0.25–0.5 band's +0.089% gross avg is SMALLER than realistic execution
friction:

| bucket | gross avg% | net @10bps | net @20bps |
|---|---:|---:|---:|
| **0.25–0.5** | +0.089 | **−0.011** | **−0.111** |
| 0.5–0.75 | +0.271 | +0.171 | +0.071 |
| 0.75–1.0 | +0.204 | +0.104 | +0.004 |
| ≥1.0 | +0.685 | +0.585 | +0.485 |

The 0.25–0.5 slice goes NET-NEGATIVE under a modest 10bps round-trip cost — gross-positive but uneconomic.
0.5–1.0 survives 10bps (marginal at 20bps); ≥1.0 has a large cushion. **DECISION: keep `rvol > 1` as the
default gate** (cleanest, best cost-adjusted, and rvol is monotone so per-trade edge peaks here). ≥0.5 is a
defensible loosening if raw volume is wanted (PF 1.33, +9k trips, positive every year), but ≥0.25 is too
far. **Better use of rvol = a SIZING lever** (size UP the 2–5 bucket at PF 1.71, DOWN the 0.5–1.0 band)
rather than a binary gate. No change to defaults.

### Finding 27 — VOLUME DURING THE CONVERGENCE (Jeff's cue): trailing-20m rvol, inverted-U on the 15m baseline

Jeff flagged in the video that volume should be RISING during the EMA→VWAP convergence. Implemented two
trailing-20m volume features (mean 1m volume over the last 20 bars incl. the cross bar — "volume during the
convergence", vs the LowFlyer single-BAR rvols):
- `rvol20m_20d` = vol20m_avg / (avgvol20/390)  — the 20m window vs the 20-DAY per-minute baseline ("hot vs normal")
- `rvol20m_15m` = vol20m_avg / (vol_0945/nbar_0945) — the 20m window vs the OPENING-15m per-minute avg (ACCELERATION)
Recorded-only (no gating); engine folds a new `vol20 = AvgMa(20)`. Fat book (5y, 14,907 trips).

**`rvol20m_20d` — WEAK lever.** PF ~1.3–1.5 across all buckets ≥2, no monotone; cumulative floors flat
(≥2 → 1.38, ≥8 → 1.385). It just re-expresses "the name is in play", which the `rvol_0945>1` gate already
captures. Not worth wiring.

**`rvol20m_15m` — THE signal, and it's an INVERTED-U (Jeff's cue, quantified):**

| rvol20m_15m | n | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| <0.25 | 5,551 | 1.287 | +0.46 | 257 |
| 0.25–0.5 | 5,975 | 1.282 | +0.53 | 319 |
| **0.5–1** | 2,904 | **1.713** | +1.35 | 393 |
| **1–2** | 404 | **1.691** | +1.74 | 70 |
| 2–4 | 55 | 0.424 | −2.37 | −13 |
| ≥4 | 18 | 0.732 | −2.68 | −5 |

The sweet spot is **[0.5, 2]× the opening-15m tempo** = "volume sustained/rising into the convergence"
(PF ~1.70, avg +1.4–1.7%) — exactly Jeff's cue. Below 0.5 (volume died off since the open) is mediocre
(PF ~1.28). Above 2× it **INVERTS to a loss** (PF 0.42–0.73): a violent 20m volume re-acceleration is a
blow-off/EXHAUSTION spike, not a healthy convergence (same exhaustion signature the LowFlyer short fades).

**The [0.5, 2] BAND cut: 3,308 trips / PF 1.709 / +1.40% avg — beats the full fat book (1.376) EVERY YEAR:**

| yr | band n | band PF | full PF | | >2 tail PF | >2 avg% |
|---|---:|---:|---:|---|---:|---:|
| 2020 | 260 | 2.31 | 1.67 | | 0.13 | −3.3 |
| 2021 | 1501 | 1.31 | 1.17 | | 0.22 | −6.5 |
| 2022 | 800 | 1.39 | 1.33 | | 0.42 | −1.5 |
| 2023 | 385 | 2.40 | 1.92 | | 0.79 | −0.7 |
| 2024 | 260 | 2.60 | 1.67 | | 8.28 | +14.4 (8 trips, noise) |
| 2025 | 102 | 3.25 | 1.39 | | 0.00 | −17.1 |

The band beats the book every year (often ~2×) and STRENGTHENS recently. The **>2 exhaustion tail is
reliably toxic** (PF 0.13–0.79 in 5 of 6 years, only 2024's 8 trips positive = noise) — a genuine GATE, not
just a size-down. **Recommended use: (a) EXCLUDE rvol20m_15m > 2 (exhaustion gate), (b) SIZE UP the [0.5,2]
band, size down <0.5.** Kept recorded-only for now (the hard gate needs the 15m baseline threaded into the
IntradaySystem constructor — the features live in `toTrip`, not the entry predicate — deferred until we
commit to gating). This is the best new selection lever since the fat-book work.

### Finding 28 — RUN DEPTH: bigger %-runs ARE better, and it's the VOLATILE deep ones (dist/ATR is a contra-indicator)

Three new features measured OVER the pre-cross consecutive below-VWAP run (reset at each cross, snapshotted
at entry): `run_max_dist` = max (VWAP−EMA)/VWAP during the run (the depth); `run_atr` = mean per-bar log-TR
over the RUN bars (the run's own volatility, not the trailing window); `run_dist_per_atr` = depth ÷ run-ATR
(depth in ATR-units). Fat book, 5y, recorded-only. Dist median 1.35% / p95 6.18%; dist/ATR mean ~3.

**A) `run_max_dist` — bigger runs ARE better (MONOTONE), confirming the hypothesis:**

| depth | n | PF | avg% |
|---|---:|---:|---:|
| <0.5% | 2205 | 1.196 | +0.16 |
| 1–2% | 4552 | 1.300 | +0.44 |
| 3.5–6% | 1344 | 1.538 | +1.74 |
| **≥6%** | 805 | **1.689** | **+3.84** |

The deeper the 9-EMA fell below VWAP before reclaiming, the bigger the bounce — classic MR depth effect
(deeper dip → bigger reversion). ≥6% runs average +3.8%/trade hold-to-MOC.

**B) `run_atr` (raw run volatility) — flat until a violent tail.** PF ~1.2 for the first 5 buckets, then
≥2% explodes to PF 2.13 / +8.0% (699 trips). Not a clean standalone lever (confounded with depth), but it
flags that VOLATILITY, not depth per se, is the driver — see the synthesis.

**C) `run_dist_per_atr` (depth normalized by vol) — INVERTED / a CONTRA-indicator:**

| dist/ATR | n | PF | avg% |
|---|---:|---:|---:|
| <1 | 1256 | 1.414 | +0.84 |
| **1–2** | 4437 | **1.536** | +0.96 |
| 2–3 | 3457 | 1.432 | +0.82 |
| 3–4.5 | 2926 | 1.242 | +0.45 |
| ≥7 | 727 | 1.024 | +0.04 |

Edge DECAYS as depth/ATR rises — the opposite of "normalize the depth" intuition. A run that's ≥7 ATR-units
deep is dead (PF 1.02). **Synthesis (the real insight):** A and C point opposite because the GOOD deep runs
got deep through HIGH VOLATILITY, not a long quiet grind. Splitting deep runs (≥3.5%) by dist/ATR proves it:

| deep-run kind | n | PF | avg% |
|---|---:|---:|---:|
| **volatile (dist/ATR < 3)** | 556 | **2.262** | **+7.41** |
| grind (dist/ATR ≥ 3) | 1593 | 1.234 | +0.82 |

**A volatile 6% dip is a great trade; a slow-grind 6% dip is mediocre.** Normalizing volatility OUT
(dist/ATR) removes the good part — so dist/ATR is a contra-indicator (want the LOW end). The raw depth +
raw run-ATR are the levers; the ratio is not.

`run_max_dist ≥ 3.5%` alone is robust every year (PF 1.20–2.78, avg +0.8–7.2%). **STACKED with Jeff's
volume band (Finding 27): `run_max_dist ≥ 3.5% AND rvol20m_15m ∈ [0.5,2]` → PF 2.536 / +7.0% avg / 461
trips / $323k** — the deepest-conviction cell found: deep VOLATILE dips reclaiming on healthy sustained
volume. All three features recorded-only (selection/sizing levers, not yet gates). Prime sizing candidates:
size UP deep (run_max_dist high) × volatile (dist/ATR low) × healthy-vol (rvol20m_15m∈[0.5,2]).

### Finding 29 — the run-depth cuts on the FULL 22 years: modern era ELITE, pre-2020 lifts off the floor but stays thin/unreliable

Ran the run-depth cuts (Finding 28) over all 22 years. The era split, vs the plain band (Finding 27) which
could NOT budge pre-2020:

| cut | pre-2020 PF (n) | modern PF (n) |
|---|---:|---:|
| full book | 1.061 (24,239) | 1.441 (16,788) |
| plain vol band [0.5,2] | 1.071 (8,547) | 1.874 (3,615) |
| run_max_dist ≥ 3.5% | **1.213** (592) | 1.781 (2,600) |
| volatile-deep (dist≥3.5% & dist/ATR<3) | **1.515** (68) | **2.516** (687) |
| deep + vol band ([0.5,2]) | 1.452 (144) | **2.945** (541) |

**Run depth is the FIRST feature to put positive AGGREGATE numbers on pre-2020** — the volume band left it
flat (1.07), but volatile-deep lifts it to PF ~1.5. This hints run-depth taps a more TIMELESS effect: a
deep volatile capitulation that reclaims VWAP is a mean-reversion setup that had some edge even in the old
market, unlike "healthy convergence volume" which is purely a modern-regime tell.

**BUT it does NOT make pre-2020 tradable — the sample is too thin & year-to-year unreliable.** Volatile-deep
pre-2020 = 1–10 trips/year (the 1.52 aggregate rests on a handful; 2015 is +46% on 2 trips). The looser
`run_max_dist≥3.5%` (10–108 trips/yr) is a MIXED bag: good years (2011 4.13, 2018 2.17, 2015 1.88) but real
losers persist (2003–06 all <0.5, 2010 0.07, 2017 0.38) — NOT reliably positive. Suggestive, not conclusive.

**The modern era, by contrast, is ELITE and ROBUST.** Volatile-deep by year 2020→2026: PF 3.07 / 1.73 /
2.17 / 3.40 / 1.72 / **4.59** / 3.28, averaging +4% to +17%/trade. 2025 = PF 4.59 / +17.4% on 102 trips.
**Verdict unchanged & sharper: a POST-2020 system.** Run depth makes the modern edge extraordinary but
can't be sold as 22-year-robust; the pre-2020 lift is a real hint of timelessness, gated by sample size.

### Finding 30 — Up/Down VOLUME SPLIT by 9-EMA slope: the cleanest convergence-volume measure (beats the time proxy)

Better than the time-window rvol proxy (Finding 27): measure convergence volume DIRECTLY by EMA-slope
regime. Two running means, RESET at each VWAP cross — every bar whose CLOSE is above the (strictly-prior)
9-EMA feeds the UP bucket (rising / accumulation), below feeds DOWN (falling / distribution). `run_updn_ratio`
= UpVol/DnVol: >1 = volume flowing into the RISING side (buying into the convergence back toward VWAP).
(9-EMA is on CLOSES; VWAP on typical price (h+l+c)/3.) Recorded-only. Ratio med 0.86 / p10 0.55 / p90 1.50.

| run_updn_ratio | n | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| <0.5 | 882 | 1.037 | +0.05 | 5 |
| 0.5–0.8 | 5470 | 1.128 | +0.19 | 105 |
| 0.8–1.0 | 3443 | 1.387 | +0.66 | 228 |
| 1.0–1.3 | 2763 | 1.309 | +0.65 | 179 |
| **1.3–2.0** | 1716 | **1.790** | +2.06 | 354 |
| **≥2.0** | 633 | **1.983** | +2.40 | 152 |

**Cleanly MONOTONE** (small 1.0–1.3 dip aside): reclaims that come with real buying volume DURING the
convergence (ratio ~2 = 2× more vol on the rising side) are PF ~2.0 / +2.4%; those where volume is on the
FALLING side (<0.8) are ~breakeven (PF 1.04–1.13). Confirms the accumulation-vs-distribution thesis exactly.

**More EFFICIENT than the F27 time proxy** — captures ~2× the trips & net $ at a still-strong PF:

| conviction cut | n | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| **updn ≥ 0.8 (NEW)** | 8,555 | 1.515 | +1.07 | **911** |
| rvol20m_15m ∈ [0.5,2] (F27) | 3,308 | 1.709 | +1.40 | 463 |
| updn ≥ 1.3 (conviction) | 2,349 | 1.839 | +2.15 | 505 |

`updn≥0.8` keeps 57% of the book at PF 1.515 (vs the band halving it) — a much gentler quality/volume trade,
and it's positive EVERY year (2020-25: 2.00 / 1.23 / 1.38 / 2.30 / 1.95 / 1.57). The EMA-slope split beats
the time-window proxy because it measures accumulation directly instead of through a noisy 20m-vs-15m window.

**STACKS with run-depth into the best cell yet:**
- updn≥1.0 & run_max_dist≥3.5% → PF 1.95 / +5.6% / 718 trips
- **updn≥1.3 & run_max_dist≥3.5% & dist/ATR<3 → PF 3.893 / +18.84% avg / 190 trips / $358k**

The full thesis assembled: DEEP + VOLATILE capitulations that reclaim VWAP WITH volume flowing into the
rising side average +18.8% hold-to-MOC. `run_updn_ratio` is now the top convergence-volume lever (prefer it
over rvol20m_15m); recorded-only, a prime gate/sizing candidate.

### Finding 31 — rvol20m_15m is NOT redundant vs updn: keep it as a pure EXHAUSTION GATE (rvol15m>2)

With `run_updn_ratio` (Finding 30) as the primary convergence-volume lever, is `rvol20m_15m` (Finding 27)
now redundant? Tested whether its toxic >2 tail still bites INSIDE the updn book. It does — they measure
DIFFERENT things:

- **Overlap:** exhaustion names (rvol15m≥2) skew high-updn (avg updn 3.12 vs 0.99) — a violent volume
  re-acceleration piles volume onto BOTH the up- and down-EMA sides, so it looks like "accumulation" and
  62 of 73 exhaustion trips sail through the `updn≥0.8` gate. updn does NOT catch them.
- **Still toxic inside updn≥0.8:** the rvol15m≥2 tail = PF 0.619 / −2.24% (62 trips). Cutting it: updn≥0.8
  1.515 → **updn≥0.8 & rvol15m<2 = 1.534** (8,493 trips). Free improvement, costs ~60 trips.
- **Worse on the A+ book:** inside updn≥1.3 the tail is PF 0.40 / −3.70% (51 trips). Cutting it: updn≥1.3
  1.839 → **updn≥1.3 & rvol15m<2 = 1.919 / +2.28%** (2,298 trips).

**Division of labor:** `run_updn_ratio` = which SIDE volume flows to (accumulation vs distribution, the
primary monotone lever); `rvol20m_15m > 2` = a DIFFERENT failure mode (the violent blow-off/EXHAUSTION spike
that explodes volume on both sides — slips past updn but reliably fails). **Keep rvol20m_15m ONLY as the
exhaustion gate (cut > 2, always-on trash filter)**; the healthy [0.5,2] band framing is superseded by updn
for selection. A+ book is now `updn≥1.3 & rvol15m<2` → PF 1.919 / +2.28%.

### Finding 32 — NEGATIVE: rvol20m_20d does NOT work as an exhaustion cut (its high tail is PROFITABLE)

Tested adding a `rvol20m_20d` high-end ceiling as a second exhaustion gate on top of `rvol15m>2`, inside
the A+ book (updn≥1.3 & rvol15m<2). It BACKFIRES — the high rvol20d tail is not toxic, it's the OPPOSITE:

| rvol20m_20d (inside A+) | n | PF | avg% |
|---|---:|---:|---:|
| <4 | 142 | 2.857 | +2.91 |
| 20–50 | 521 | 2.238 | +2.70 |
| 50–150 | 299 | 1.319 | +0.85 |
| **≥150** | 404 | **1.852** | **+3.45** |

The tail being "cut" stays strongly profitable: rvol20d≥50 = PF 1.677/+2.35%, rvol20d≥150 = 1.852/**+3.45%
(the HIGHEST avg in the book)**. Adding the ceiling THROWS AWAY money: A+ base $524k → `<150` $384k → `<50`
$359k (PF nudges 1.92→2.10 but net drops $165k — a bad trade, paying dollars for a cosmetic PF bump).

**Why it's the wrong tool (the distinction that matters):**
- `rvol15m > 2` = 20m vol ≫ *THIS MORNING's* opening tempo → a sudden within-day RE-ACCELERATION =
  blow-off/EXHAUSTION → toxic (PF 0.4). ✓ good gate.
- `rvol20m_20d` high = 20m vol ≫ the name's *20-DAY normal* → simply a huge-volume in-play low-float day →
  those are GREAT reclaims that run hard. ✗ NOT exhaustion.

High rvol20d measures "unusual vs the stock's own history" (what you WANT — an in-play mover); high rvol15m
measures "accelerating vs today's open" (a within-day spike). They look similar but point OPPOSITE (consistent
with Finding 26, where rvol_0945 was monotone POSITIVE). **VERDICT: no rvol20d cut — rvol15m>2 stays the
sole exhaustion gate.** A+ book stays `updn≥1.3 & rvol15m<2` (PF 1.919 / +2.28% / 2,298 trips).

### Finding 33 — the A+ cell + exhaustion cut, and the SMB d/3-stop+d-target exits FAIL on it (target caps +19% runners)

Added the `rvol15m<2` exhaustion gate (Finding 31) to the A+ stack — a free lift:

**A+ CELL = `updn≥1.3 & run_max_dist≥3.5% & dist/ATR<3 & rvol20m_15m<2` → PF 4.027 / +19.50% avg / 184 trips**
(vs 3.893 / +18.84% / 190 without the cut — the 6 exhaustion trips were a drag).

**Then tested SMB's original exits (d/3 stop + d target) on this cell — they FAIL, and it's the clearest
demonstration of the exit thesis yet.** Selection is entry-time, so the same 184 trips, only exits change:

| exit config | win% | PF | avg% | net$k |
|---|---:|---:|---:|---:|
| **SMB original: d/3 stop + target** | 54.9 | **1.32** | +1.29 | 24 |
| d/3 stop, no target | 37.0 | 3.25 | +13.85 | 255 |
| d·2/3 stop + target | 64.1 | 1.52 | +2.17 | 40 |
| **BASE: d·2/3 stop, NO target** | 48.4 | **4.027** | **+19.50** | **359** |

The SMB geometry is 3× WORSE (PF 1.32 vs 4.03). The mechanism is stark: adding the target RAISES win rate
(54.9%, 64.1%) but COLLAPSES avg return (+19.5% → +1.3–2.2%) and PF. These A+ deep-volatile-accumulation
reclaims run ENORMOUSLY to MOC (avg +19.5% held); a fixed d target amputates exactly the monster runners
that ARE the edge — ~90% of it. Both levers point the same way independently: target OFF ≫ ON (4.03 vs 1.52
at d·2/3), and d·2/3 ≫ d/3 (too tight, stops out early). No-target stop-width sweep on the A+ cell confirms
a broad plateau — d/2 4.16, **d·2/3 4.03, d·0.85 4.16 (peak), d 3.94** — d/3 (3.25) is the only clear loser.

**VERDICT: SMB's d/3+d does NOT work as a MECHANICAL hold-to-MOC rule** (it likely works in their
DISCRETIONARY hands — scaling out, tape-reading, shorter horizon). The edge is momentum-continuation, so any
fixed target fights it. Keep d·2/3 + no target; d·0.85 is a noise-level alternative not worth changing for.

### Finding 34 — the new features do NOT rescue the SHORT side (they confirm longs≠shorts)

Re-tested the reverse-reclaim short (Finding 22, then PF ~1.25 best) now that we have run-depth, dist/ATR,
updn-ratio and the exhaustion cut. All features are direction-aware (short uses the ABOVE-VWAP run; updn is
close-vs-EMA either way). Short book over the fat-book universe (5y): 14,769 trips, **PF 1.026** (near
breakeven, as before). The features FAIL to lift it — and they explain WHY:

**run_max_dist (strength depth) — DEAD/INVERTED.** Long: deeper dip → bigger reversion (monotone up to PF
1.69). Short: deeper up-run → WORSE (3.5-6% bucket PF **0.848 / −$67k**, ≥6% 0.968). A big rise into VWAP is
STRONG MOMENTUM that keeps climbing, not a fade. The single most powerful LONG feature is a NEGATIVE for
shorts. **run_updn_ratio — FLAT** (all buckets 0.98–1.17): no symmetric "distribution-into-the-breakdown"
edge, just noise. **run_dist_per_atr — weak, wrong-way, all ~1.0.**

Best stack findable anywhere: shallow(<2%) & volatile(dist/atr<1) = **PF 1.21** (1,313 trips); the triple
stack thins to PF 1.13 / 299 trips. vs the LONG's PF 4.03 on the identical feature stack. Nothing reaches a
cost-viable PF (and a short carries extra locate/borrow cost).

**VERDICT: no short side.** This REINFORCES Finding 22 — longs and shorts are NOT mirror images. VwapReclaim
is a LONG dip-buying/mean-reversion edge specific to the post-2020 regime (beaten-down names reclaiming VWAP
get BOUGHT); there is no clean short analogue, and sophisticated features don't manufacture one — they just
measure, precisely, that the edge isn't there on the short. `--reclaim-short` stays a documented negative.

### Finding 32 — per-year breakdowns of the two A-cells (Finding 30): 2021 is the weakest AND highest-trip year, but stays POSITIVE

Per-year (full-history fat book, hold-to-MOC) for the two Finding-30 cells. Requested to see how the run-depth
/ updn cells hold up year-by-year, especially in 2021.

**Cell 1 — `updn ≥ 1.0 & run_max_dist ≥ 3.5%`** (modern 2020-25: 801 trips / PF 2.19 / +6.8% avg):

| yr | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| 2020 | 92 | 51.1 | 3.52 | +11.4 | 104,566 |
| **2021** | 298 | 36.2 | **1.26** | +1.6 | 47,156 |
| 2022 | 131 | 45.0 | 1.60 | +2.8 | 36,864 |
| 2023 | 58 | 43.1 | 3.38 | +13.6 | 78,598 |
| 2024 | 103 | 40.8 | 2.51 | +10.0 | 102,882 |
| 2025 | 119 | 51.3 | 3.53 | +14.5 | 172,604 |
| 2026 | 64 | 54.7 | 4.39 | +14.2 | 90,698 |

**Cell 2 — `updn ≥ 1.3 & run_max_dist ≥ 3.5% & dist/ATR < 3`** (modern 2020-25: 212 trips / PF 4.04 / +19.3% avg):

| yr | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| 2020 | 24 | 66.7 | 8.25 | +36.2 | 86,902 |
| **2021** | 63 | 33.3 | **2.48** | +11.1 | 69,942 |
| 2022 | 27 | 55.6 | 4.09 | +16.1 | 43,521 |
| 2023 | 18 | 50.0 | 5.05 | +31.2 | 56,199 |
| 2024 | 40 | 40.0 | 2.92 | +14.0 | 55,828 |
| 2025 | 40 | 70.0 | 6.36 | +24.0 | 95,886 |
| 2026 | 23 | 47.8 | 5.97 | +23.8 | 54,739 |

**Both cells: 2021 is the WEAKEST year (PF 1.26 / 2.48, win 33-36%) AND the HIGHEST-trip year** (298 / 63 —
more than any other year). The by-now-familiar chop signature: 2021 over-trades and wins less. **But both cells
stay solidly POSITIVE in 2021** — cell 2's 2021 is PF 2.48 / +11% avg (a GOOD year, just the weakest of a
spectacular set). Every other modern year is 2.5-8.3 PF.

**Cross-system note vs DipRiderV3:** the *identical* 2021-is-weakest-and-most-traded signature appears in V3
(its worst modern year, its most trips). But VwapReclaim's A-cells hold up BETTER in 2021 (PF 1.26/2.48
positive) than V3's A+ (slightly negative clipped). VwapReclaim is a capitulation-reclaim FADE (buys beaten
names reclaiming VWAP) — structurally less exposed to the buy-the-strength-that-fades trap that punishes V3's
momentum-continuation entries in 2021's two-sided chop. Same regime, opposite entry logic, and the fade
survives it better. (Reconciliation: modern totals here — 801/212 trips — run ~10-12% above Finding 30's
718/190, same PF ballpark; a candidate-table / date-boundary difference vs when F30 was written. Shapes and
per-year robustness are unaffected and if anything slightly stronger.)
