# HighFlyerV2 — Partial-Candle Intraday-Entry Research

`TradingEdge.HighFlyerV2` (branch `highflyer_v2`). A fork of MaxFlyer's debloated
daily selection skeleton, brought back to byte-parity with the original HighFlyer
daily swing system, then extended to test **intraday entries via partial daily
candles**: instead of deciding + filling at the daily close, decide + fill on a
*checkpoint snapshot* of how the day's candle looked at, e.g., 10:00 ET.

The question: **can we get a better PF by entering early in the day instead of at
the close?**

## Engine / data

- **Parity baseline** (full daily-close entry, the locked HighFlyer production
  default): **5,558 trips · 53.5% win · $1.34M · PF 1.828** (2005-01-01 →
  2026-05-13). Reproduced byte-for-byte from the original engine (sha256
  `c6380dfc…`). This is the comparison anchor.
- **Partial candle** = `partial_candle_1000` table in `trading.db`, one row per
  (ticker, date), built by `scripts/equity/build_partial_candle.fsx` from
  `data/minute_aggs`. As of **10:00 ET, no peek past it**:
  - `open` = first RTH bar's open (09:30 ET)
  - `high`/`low`/`close` = extremes / last close over RTH bars **09:30–09:59**
    (`et_min ∈ [570, 600)` — the 10:00 bar covers 10:00:00+ and is excluded)
  - `volume` = **premarket-inclusive**, 04:00 → 09:59 ET (`et_min ∈ [240, 600)`)
  - Verified on NVDA 2024-06-10: open 120.37, premkt-vol 12,148,397 (both match
    the `premarket` table), 30 RTH bars before 10:00.
  - 48.4M rows / 35,793 tickers / 5,734 days; ~7M rows have no RTH bar before
    10:00 (halted/illiquid) → emitted as `ValueNone` → not tradeable that day.
- **Experiment wiring** (`--partial-entry`): the partial candle's own OHLCV drive
  the entry decision (move band, rvol, 52w-proximity close, price floor,
  intraday-ret close/open) **and** the fill price. The prior-day snapshots (rvol
  baseline, 252d channels, tightness, ATR%, ADV, past-runner) are unaffected — they
  measure state going *into* the day. **Exits stay on the daily series** (5d
  time-stop / MTM) so only the entry variable changes. Partial OHLC is
  split-adjusted to the daily scale via `adj_close/raw_close`.

## Run 1 — raw 10:00 ET entry, production move band unchanged

Defaults (`up[0.10,0.30)`, rvol≥5, …) but the move/rvol/fill read the 10:00 candle.

| Entry basis | trips | win% | net P&L | PF |
|---|---|---|---|---|
| Daily close (parity) | 5,558 | 53.5% | $1.34M | **1.828** |
| **10:00 ET partial** | **910** | 54.2% | $234k | **1.675** |

At face value the partial entry is **worse** (PF 1.675 < 1.828) and trades **6×
fewer names**. But this is NOT an apples-to-apples "same trades, earlier fill" —
the move threshold is a **moving target intraday**:

- The `≥10%` floor measured at 10:00 selects only the names **already up ≥10% in
  the first 30 minutes**: median move-by-10:00 = **+16.7%**, q75 +22.4%. A rare,
  violent subset.
- Only **542 of the 910** partial trips also appear in the daily-close book. The
  other **368 faded back under 10% (or past 30%) by the close** — the daily engine
  never took them. Conversely the daily book has ~5,000 names that cross 10% only
  *later* in the day, which the 10:00 floor misses.

So raw-10:00 is a **different, smaller strategy**, not a better-timed one. The
threshold-as-a-moving-target confounds the timing question.

## Run 2 — entry timing, selection held FIXED (the real test)

Restrict to the **542 names both engines trade** (same signal, only the fill time
differs). This isolates entry *timing* from *selection*:

| Entry basis | shared trips | win% | net P&L | PF |
|---|---|---|---|---|
| Daily close | 542 | 54.8% | $126.2k | 1.983 |
| **10:00 ET partial** | 542 | **60.0%** | **$167.6k** | **2.292** |

**Holding selection fixed, entering at 10:00 ET beats the close: PF 2.29 vs 1.98,
win-rate +5.2pts, +33% net P&L.** And the 10:00 fill is on average **0.57%
cheaper** than the close (`avg(p_px/d_px − 1) = −0.0057`) — these names drift up
further into the close, so entering early captures more of the move. The timing
edge is real and mechanism-consistent.

## Run 3 — relaxed entry (rvol 5→2.5, move floor 10%→5%) + post-hoc breakdown

The full-day production gates are too strict for a 10:00 ET snapshot: only
premarket + 30 min of RTH volume has accumulated, so rvol≥5 is a high early bar,
and 10% by 10:00 already excludes the names that *will* run to 10–30% by close.
Relax both on the partial-entry run, then break down to find the sweet spots.

`--partial-entry --rvol-min 2.5 --up-threshold 0.05`:
**3,314 trips · 54.3% win · $755k · PF 1.637** (aggregate — hides the structure).

### Move-at-10:00 band (peaks in the middle)

| move @ 10:00 | trips | win% | PF | net | avg $ |
|---|---|---|---|---|---|
| 5–7.5% | 1069 | 52.0 | 1.385 | $163k | 152 |
| 7.5–10% | 558 | 53.0 | 1.419 | $85k | 152 |
| 10–15% | 696 | 55.5 | 1.774 | $183k | 263 |
| **15–20%** | 441 | **59.9** | **2.37** | $176k | **398** |
| 20–30% | 550 | 54.2 | 1.762 | $149k | 270 |

The **5–10% bands are the drag** (PF 1.39–1.42) — lowering the floor to 5% bought
capacity, not quality. The **15–20% band is the sweet spot** (PF 2.37); 20–30%
softens (blow-off region starting).

### rvol-at-10:00 band (INVERTS the daily intuition)

| rvol @ 10:00 | trips | win% | PF | net |
|---|---|---|---|---|
| **2.5–3.5** | 803 | 57.0 | **2.173** | **$262k** |
| 3.5–5 | 567 | 55.2 | 1.489 | $99k |
| 5–8 | 530 | 49.8 | 1.287 | $59k |
| 8–15 | 498 | 55.6 | 1.697 | $117k |
| 15+ | 916 | 53.3 | 1.563 | $219k |

The **lowest band (2.5–3.5) is the BEST** (PF 2.17, most net P&L) — it wouldn't
exist at the old rvol floor of 5. At the *close*, high rvol marked the big movers;
at **10:00, LOW rvol is cleaner** — up nicely on the day but trading calmly beats
a name already in a volume frenzy by 10:00 (those carry blow-off risk; the 5–8
band is the worst, PF 1.287).

### 2D grid — move × rvol (the joint sweet-spot)

| move ＼ rvol | 2.5–3.5 | 3.5–8 | 8+ |
|---|---|---|---|
| 5–10% | 1.85 (324) | **1.12** (568) | 1.49 (735) |
| 10–15% | 1.90 (205) | 1.63 (211) | 1.80 (280) |
| **15–20%** | **3.13** (142) | 1.96 (131) | 2.09 (168) |
| 20–30% | 2.33 (132) | 1.85 (187) | 1.45 (231) |

*(PF (trips) per cell.)*

- **Standout cell: 15–20% move × 2.5–3.5 rvol → PF 3.13** (142 trips). Up a lot by
  10:00 but calm volume = the best continuation bet.
- The **low-rvol column (2.5–3.5) wins in EVERY move band** (1.85 / 1.90 / 3.13 / 2.33).
- The **3.5–8 column is the dead middle** (worst in every band). The big weak cell
  `5–10% × 3.5–8` (PF 1.12, 568 trips) is the single largest drag on the aggregate.

**Joint read:** the early-morning edge is *"meaningfully up by 10:00 (≥10%, ideally
15–20%) AND not yet in a volume frenzy (rvol < ~3.5)."* Both relaxations were
vindicated but in opposite directions: dropping rvol→2.5 opened the best column;
dropping the move floor→5% mostly added the weak 5–10% rows (only the calm
sub-cell there is worth keeping).

## Run 4 — how many 10:00 trades would be valid at the close?

Cross-reference the 3,314 relaxed-partial trades against the production close-book
(the golden CSV) on (symbol, signal_date): would the close-engine also have taken
this name?

| bucket | trips | win% | PF | net | avg $ | median move@10:00 | median rvol@10:00 |
|---|---|---|---|---|---|---|---|
| **valid at close** | 1,273 (38.4%) | **64.3** | **3.24** | $558k | **$438** | 16.0% | 5.18 |
| only at 10:00 | 2,041 (61.6%) | 48.1 | 1.211 | $197k | $97 | 7.6% | 7.04 |

**Only 38% of the relaxed-10:00 trades would also be valid at the close** — the
10:00 book is mostly a *different* book, not an early fill of the close book. And
the edge is almost entirely in the trades that ALSO validate at close: **PF 3.24
(64% win, $438 avg)** vs the "only at 10:00" drag at **PF 1.21 (48% win, $97 avg,
near breakeven)**.

The relaxation (rvol→2.5, move→5%) mostly let in junk: the "only at 10:00" bucket
has a *lower* median move (7.6%) and *higher* median rvol (7.04) — the
"frantic-but-hasn't-moved-much" profile the Run-3 grid flagged as the weak cells.
**The different book is mediocre; the win is to take the SAME close-qualifying
names but enter them at 10:00.**

## Run 5 — timing vs selection: the "valid at close" book, head-to-head

Is the "valid at close" bucket's PF 3.24 because the **early entry is better (lower
price)**, or because these 1,273 names are just a **good subset** that would run
high PF entered at the close too? Test directly: take the same 1,273 (symbol,
signal_date) pairs and compare their **10:00-fill** P&L (relaxed-partial book) vs
their **close-fill** P&L (golden book). Same names, only the fill differs; exits
identical (daily 5d-stop / MTM). Both books are unique on (symbol, signal_date) —
no fan-out.

| | 10:00 entry | close entry |
|---|---|---|
| PF | **3.24** | 2.20 |
| win% | 64.3 | 56.7 |
| net | $558k | $341k |

**It's timing, not selection.** Entered at the *close*, these exact names run PF
2.20 (a good subset — above the full close-book's 1.83 — but no more). Entered at
**10:00, the same names run PF 3.24**: +1.04 PF, +7.6pts win, **+64% net P&L**.

Mechanism: `avg(p_px/g_px − 1) = −1.47%` — the **10:00 fill is on average 1.47%
cheaper** than the close fill. These names drift UP into the close, so buying at
10:00 gets in below the close-engine's price and captures that extra ~1.5% plus the
rest of the day's drift (`avg_exit_diff = 0.0` confirms identical exits → the entire
P&L gap is the entry price).

**Answer to both halves:** the subset *would* be high-PF at the close (2.20), but
the early entry is the dominant lever (2.20 → 3.24 on identical names, ~1.47%
cheaper fill).

## Run 6 — the "only at 10:00" cohort: sell at close or hold 5 days?

The marginal book (2,041 trades, PF 1.21) doesn't qualify at the close — would it
be better to flatten it same-day (exit at day-D's close, a ~6h hold) rather than
run the planned 5-day time-stop? Post-hoc: all 2,033 of these currently run the
full 5d hold (`bars_held=6`), so the cohort P&L already IS the 5d-hold result.
Compute the same-day-close counterfactual = `qty·(dayD_adj_close − entry_1000)`.

| only-at-10:00 (2,041) | sell at day-D close (1-day) | hold 5 days (current) |
|---|---|---|
| PF | **0.817** | **1.211** |
| win% | 41.6 | 48.1 |
| net | **−$88k** | **+$197k** |

**Hold the 5 days — do NOT sell at the close.** Same-day exit turns a
marginal-but-positive book (1.21) into a money-loser (0.82, −$88k).

Mechanism: these names are up ~5–7% at 10:00 but **fade through the rest of the
day** — that fade is *why* they fail to qualify at close (they slip back under
10%). Exiting at day-D's close sells into that fade (near the intraday low); the
5-day hold gives them time to recover/continue, which on average is enough to turn
the cohort positive.

This completes a clean symmetry:
- **good book (valid-at-close)** drifts UP into the close → early entry wins (buy
  ~1.5% cheaper, Run 5).
- **marginal book (only-at-10:00)** drifts DOWN into the close → must NOT sell at
  the close; the 5-day hold rescues it.

## Run 7 — does the low-float edge survive on the 10:00 entries?

The <$300M dollar-float edge was established on the daily-close book (PF ~2.5 vs
~1.4). Does it hold on the 10:00 partial book? Float metric = the **combined**
source (SEC free-float first, Polygon `?date=` scso to fill — 95% trade-time
coverage in 2013+; see the float memory), dollar-float taken **at the 10:00 entry
price** (`scso × entry_1000`, or SEC shares × entry_1000). Relaxed partial book,
74% covered.

| float bucket (at 10:00) | trips | win% | PF clip | PF raw |
|---|---|---|---|---|
| **<$150M** | 625 | 58.1 | **2.135** | 2.262 |
| $150–300M | 318 | 57.9 | 1.910 | 2.03 |
| $300–750M | 416 | 58.9 | 1.364 | 1.448 |
| ≥$750M | 1,095 | 53.5 | 1.472 | 1.50 |

**The low-float edge holds cleanly on the 10:00 entries** — monotone in the low
region, smallest float (<$150M) strongest at PF 2.14, break right at $300M (same as
the daily book). The <$300M vs ≥$300M split, stable across eras:

| 10:00 book | LOW <$300M | HIGH ≥$300M |
|---|---|---|
| Full window | **2.066** (943, 58% win) | 1.441 (1,511) |
| 2013+ | **2.051** (858, 58% win) | 1.432 (1,191) |

**The two HighFlyerV2 edges stack independently:** timing (early entry, Run 5:
~2.2→3.2 on the same names) × float (<$300M ~2.05 vs ~1.44). Early entry on
low-float names is the cell to push on. (Float source: this also validated the
Polygon shares-outstanding pull — see [[project_float_feature_2026-06-22]] — over
buying Sharadar.)

## Run 8 — do the two edges compound? (requalifier × float, 2×2)

Split the 10:00 book by BOTH the timing axis (valid-at-close requalifier, where the
early-entry edge lives — Run 5) AND the float axis (<$300M, Run 7). Combined float
metric, dollar-float at the 10:00 entry price.

| | LOW <$300M | HIGH ≥$300M |
|---|---|---|
| **requalifier (valid at close)** | **PF 3.654 · 67.9% · 343** | PF 3.109 · 64.0% · 619 |
| only at 10:00 (fades by close) | PF 1.553 · 52.3% · 600 | PF 1.044 · 48.8% · 892 |

**The two edges compound — orthogonally.** Best cell = requalifier × low-float →
**PF 3.65, 68% win**, above either edge alone (timing-only requalifiers 3.24;
float-only <$300M ~2.05). Within requalifiers, low-float adds (3.65 vs 3.11);
within low-float, requalifying adds (3.65 vs 1.55). Neither cannibalizes the other
(contrast: float×breadth/heat overlapped on the daily book).

Float also does double duty: it **rescues the marginal book** too — the
fade-out (only-at-10:00) names go from PF 1.044 (high-float, near breakeven, the
junk the relaxation let in) to 1.553 (low-float, a real edge).

**The strongest HighFlyerV2 setup = a low-float, close-qualifying name entered at
10:00: PF 3.65, 68% win.** The open no-lookahead problem stays the same (Run 5):
"valid at close" isn't knowable at 10:00 — need a 10:00-observable proxy for it.

## Run 9 — move floor 5%→10% (rvol stays 2.5): a no-lookahead requalifier proxy

Run 5/8 said the timing edge needs a 10:00-observable proxy for "valid at close"
(unknowable at 10:00). Test the move floor as that proxy: keep rvol relaxed at 2.5,
push the move floor back 5%→10% (`--rvol-min 2.5 --up-threshold 0.10`).

| | move 5% (Run 3/8) | **move 10%** |
|---|---|---|
| trips | 3,314 | 1,687 |
| **requalifier rate** | 38.4% | **63.9%** |
| aggregate PF | 1.637 | **1.906** |
| low-float (<$300M) PF | 2.066 | **2.378** |

**The 10% floor IS a (partial) no-lookahead requalifier proxy:** 64% of move-10%
trades requalify at close vs 38% at move-5%. Being up ≥10% by 10:00 (not just 5%)
is itself evidence the move has legs — exactly the 10:00-observable signal Run 5/8
needed. Overall PF 1.64→1.91; low-float 2.07→**2.38**.

move-10% 2×2 (requalifier × float):

| | LOW <$300M | HIGH ≥$300M |
|---|---|---|
| requalifier | PF 3.07 (271) | 2.80 (561) |
| fade-out | 1.89 (216) | 1.05 (253) |

Tradeoff: the requalifier cells are a touch BELOW the move-5% book's (low-float
requalifier 3.07 vs 3.65) — the move-5% book's requalifiers included late-crossers
that ran hard, which the blunt 10% floor can't single out. But those needed the
lookahead label anyway. **The deployable win is the overall low-float book at PF
2.38, traded with NO lookahead** (move ≥10% + low-float, both knowable at/before
10:00). rvol 2.5 + move 10% is the more tradeable configuration.

## Run 10 — rvol floor 2.5→2.0 (move stays 10%): capacity vs purity

`--rvol-min 2.0 --up-threshold 0.10`. Does dropping the rvol floor another notch
add usable trades or just dilution?

| | rvol 2.5 (Run 9) | rvol 2.0 |
|---|---|---|
| trips | 1,687 | 2,062 (+375) |
| aggregate PF | 1.906 | 1.900 |
| requalifier rate | 63.9% | 65.6% |
| low-float (<$300M) PF | 2.378 | 2.362 |

The new **2.0–2.5 slice (375 trips)** is solid but a touch below the 2.5+ core:
overall PF 1.738 vs 1.809; low-float 2.239 vs 2.378. So rvol 2.0 is a **near-wash —
pure capacity at negligible quality cost**: +22% trips, the deployable low-float
book essentially flat (2.36 vs 2.38), requalifier rate unchanged. The 2.0–2.5 names
are good-but-not-better than the core (consistent with Run 3's best band being
2.5–3.5). Capacity-vs-purity dial: 2.0 for more trades at the same edge, 2.5 for the
cleanest PF; the dilution is tiny either way.

## Run 11 — exit the WHOLE 10:00 book at the same-day close vs the 5-day hold

Run 6 tested the same-day-close exit only on the marginal "only at 10:00" cohort.
Here: exit EVERY 10:00 trade at day-D's close (~6h intraday round-trip) vs the
current 5-day hold. Post-hoc, move-10% books.

**rvol 2.5 + move 10%:**
| | same-day close exit | 5-day hold |
|---|---|---|
| ALL | PF 1.223 (50.3% win) | **1.906** (56.2%) |
| LOW <$300M | PF 1.304 (46.6%) | **2.578** (58.7%) |

**rvol 2.0 + move 10%:** ALL 1.251 vs 1.900; LOW <$300M 1.378 vs **2.586**.

**Keep the 5-day hold — it's decisively better across the WHOLE book**, not just the
bottom half. Same-day-close roughly halves the low-float edge (2.58→1.30) and drops
the win rate ~59%→47%. The win-rate drop is the tell: >half the names are BELOW
their 10:00 entry at the close but recover over the 5-day hold — the intraday path
is choppier than the multi-day drift.

This generalizes Run 6: these names continue up after 10:00 both intraday AND over
the following days, and the real payoff accrues over the days. Reconciles with Run 5
without contradiction: "10:00 entry beats close entry on the same names" means the
10:00 price is a good ENTRY — the close is still mid-flight, NOT a good exit.
**HighFlyerV2 is a multi-day continuation trade with an early intraday entry, not an
intraday scalp.** The 10:00 entry improves the fill; the hold makes the money.

## Run 12 — rvol floor to 1.5 and 1.0 (move stays 10%): low-rvol is NOT noise

Rationale (Bonde / the user): 1× ADV in the first 30 min is already a heavy day
pace — premarket + 30 min hitting full-day average ⇒ a ~2–3× rvol day by close. So
a 10:00 rvol floor of 1.0–1.5 is not actually loose in full-day terms.

Aggregate (move 10%): rvol 1.5 → 2,735 trips PF 1.858; rvol 1.0 → 3,906 trips PF
1.866. The edge does NOT collapse — dropping the floor 2.5→1.0 costs only ~0.04 PF
while >2× the trips. **The deployable low-float book IMPROVES: PF 2.412 at rvol≥1.0**
(vs 2.378 @2.5), 868 low-float trips (vs 487).

rvol-band detail (rvol≥1.0 book) — the low-float edge is **bimodal**, not monotone:

| rvol @ 10:00 | PF all | requal% | PF low-float |
|---|---|---|---|
| **1.0–1.5** | 1.789 | 43.6 | **3.106** |
| 1.5–2.0 | 1.71 | 63.6 | 1.672 ← soft spot |
| 2.0–2.5 | 1.738 | 73.3 | 2.239 |
| **2.5–3.5** | 2.212 | 71.2 | **3.156** |
| 3.5–8 | 1.76 | 63.5 | 2.362 |
| 8+ | 1.632 | 59.1 | 2.138 |

- **1.0–1.5 is genuinely strong on low-float (PF 3.11)** — quiet-but-moving low-float
  names. Low requalifier rate (43.6% — they often don't rip to the close) but the
  low-float ones still pay over the 5-day hold. Bonde's early-entry thesis vindicated.
- Peak stays **2.5–3.5 (3.156)** (echoes Run 3); a soft spot at **1.5–2.0 (1.672)**
  between the two strong bands. Bimodal — worth not over-fitting, but suggests the
  cleanest low-float book might skip the 1.5–2.0 dead zone.

**rvol≥1.0 is the better floor for the low-float book** (2.41, ~2× the trips). Note
the all-trades aggregate peaks at 2.5–3.5; it's specifically the FLOAT-conditioned
book where 1.0–1.5 shines.

## Takeaways

1. **Early entry helps when the name is the same** (PF 2.29 vs 1.98 on the shared
   542). The *timing* is favorable.
2. **The standalone raw-10:00 run is worse** (PF 1.675) only because the fixed 10%
   floor selects a different, smaller, more violent population (368 fade-outs) and
   drops the ~5,000 later-crossers.
3. **Implication / next:** the move floor should NOT be the same 10% at 10:00 as at
   the close. The promising design is a **lower intraday move floor at 10:00**
   (catch names that are only up ~5% at 10:00 but will continue to 10–30% by close),
   entering early to capture the rest of the move. That's the next experiment —
   sweep the 10:00 move floor and compare PF + trip count to the close baseline.
