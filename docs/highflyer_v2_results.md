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

## Run 13 — 10:30 ET checkpoint: are the late bloomers worse?

Built a 10:30 ET partial table (`partial_candle_1030`, cutoff 630; engine
`--cutoff-min 630`). Question (the checkpoint analog of Run 4/6): do names that
qualify at 10:30 but NOT at 10:00 ("late bloomers") have poor PF? Same deployable
config (rvol≥1.0, move 10%). 10:30 book = 5,681 trips, PF 1.671 (vs the 10:00
book's 1.866 — already a hint that waiting dilutes).

| 10:30 cohort | trips | win% | PF all | requal@close | PF low-float |
|---|---|---|---|---|---|
| **early** (qual@10:00) | 3,277 | 56.4 | **1.818** | 65.5% | **2.75** |
| **late bloomer** (only@10:30) | 2,404 | 51.5 | **1.354** | 31.7% | 1.743 |

**Yes — late bloomers are markedly worse.** The 10:00-qualifiers carry the edge
(PF 1.82 / low-float 2.75); the names that cross 10% only between 10:00 and 10:30
are weak (PF 1.35 / low-float 1.74) with half the close-requalification rate (32%
vs 66%). Same pattern as Run 4/6 on the checkpoint axis: **earlier is better** — a
name up ≥10% by 10:00 is a stronger signal than one that gets there only by 10:30
(slower, weaker moves that fade more).

This explains why the 10:30 book's aggregate PF is LOWER than 10:00's: waiting to
10:30 dilutes the good 10:00 population with 2,404 inferior late-bloomers.
**Don't move the checkpoint later — 10:00 beats 10:30.** If anything this argues for
an EARLIER checkpoint (9:45?), not later. (Low-float still salvages the late
bloomers to 1.74 — float's double duty again — but they're strictly worse.)

## Run 14 — 9:45 ET checkpoint: earlier is NOT strictly better; 10:00 is a sweet spot

Run 13 suggested "earlier is better" — test it by going to 9:45 ET
(`partial_candle_0945`, cutoff 585; only 15 min of RTH). Same config (rvol≥1.0,
move 10%).

9:45 standalone book = 2,565 trips, **PF 1.497** (low-float 1.961) — WORSE than
10:00's 1.866 (low-float 2.412). So pushing earlier HURT. The 10:00-book split by
9:45 is also gentle, not a cliff:

| 10:00 book by 9:45 | trips | PF all | requal@close | PF low-float |
|---|---|---|---|---|
| early (qual@9:45) | 2,161 | 1.798 | 69.2 | 2.518 |
| late (only@10:00) | 1,745 | 1.762 | 45.6 | 2.292 |

The 9:45→10:00 late bloomers are barely worse (1.762 vs 1.798) — nothing like the
10:00→10:30 cliff (1.35 vs 1.82).

**Why 9:45 underperforms — the fade-prone spike.** Split the 9:45 book by whether it
HOLDS ≥10% to 10:00:

| 9:45 names | trips | win% | PF |
|---|---|---|---|
| **faded by 10:00** (gave it back in 15 min) | 404 | 40.3 | **0.644** |
| held to 10:00 | 2,161 | 57.4 | 1.843 |

16% of the 9:45 book is **toxic early spikes (PF 0.64)** — pop ≥10% in the first 15
min, immediately give it back. The 10:00 checkpoint screens these out by waiting
another 15 minutes.

**10:00 ET is a LOCAL OPTIMUM, not the start of a monotone.** Earlier (9:45) admits
fade-prone spikes that haven't proven they'll hold; later (10:30) admits weak
laggards that crossed 10% too slowly. 30 min is the Goldilocks window — long enough
to filter fakeouts, short enough to keep the cheap early fill. **Keep the 10:00
checkpoint.**

## Run 15 — candle shape: does buying near the top of the range help?

The partial bar carries full OHLC, so we can test where in the day's range the fill
sits — all no-lookahead, scale-free ratios off the partial candle:
- **position-in-range** = (close−low)/(high−low): 1=at the high (strength), 0=at the low.
- **green/red** = close ≥ open (Qullamaggie's "holding the gain").

**(a) Salvage attempt on 9:45 — FAILS.** Green > red on the 9:45 aggregate (1.594 vs
1.249) — the red 9:45 candles are the fade-prone spikes. But the shape filters lift
the 9:45 book only modestly and **never reach 10:00's level**, and barely move the
deployable low-float book:

| 9:45 filter | PF all | PF low-float |
|---|---|---|
| all 9:45 | 1.497 | 1.961 |
| green | 1.594 | 1.978 |
| top ≥0.8 | 1.654 | 1.822 |
| **10:00 baseline** | **1.866** | **2.412** |

Buying near the top of the 9:45 range does NOT make it competitive with 10:00 — 15
min is fundamentally too little time; a within-candle position filter can't recover
it. The Goldilocks conclusion (Run 14) stands.

**(b) On the 10:00 book the hypothesis INVERTS — pullback entry wins, esp. low-float:**

| 10:00, green/red | PF all | PF low-float |
|---|---|---|
| green | 1.803 | 2.18 |
| **red** | 1.692 | **4.179** (144) |

position-in-range, low-float: `<0.2` band **PF 7.30** (63) — the *lowest* position,
the *highest* PF. **On low-float 10:00 names, buying the RED / low-of-range candle
(a pullback) beats buying strength.** Mechanism: these are already ≥10% up by 10:00;
a red/low-of-range partial = gapped up huge then **pulled back into 10:00** → you're
buying the DIP on a strong low-float runner, not chasing the spike extension (less
room, more reversal risk). The Qullamaggie "buy strength" intuition reverses for
low-float momentum continuation.

"Red" here = pulled back from a bigger intraday high (still ≥10% up on the day), not
down on the day.

**Is the 4.18 a winner-skew artifact? No — checked three ways:**
- **Already clipped.** Every PF above uses `LEAST(ret,0.5)`. Red low-float raw PF
  4.78 → clip 4.18 (max winner +229% → capped +50%); the clip deflates it only
  mildly, so it's not one uncapped monster.
- **Not concentrated.** Top-5 clipped winners = only 16.5% of the cell's gross win
  (a single-trade artifact would be 50%+). Win rate 63.9%, median +2.4% — a broad
  momentum-continuation profile leaning on a fat right tail, not a fluke.
- **Holds in BOTH eras** (clipped, low-float): red 3.61 vs green 1.98 (2005–2014);
  red 4.30 vs green 2.24 (2015–2026). The pullback-entry edge is persistent, not a
  one-regime effect.

⚠️ Residual caveat = sample thinness in the EXTREME cells (2005–2014 red = 35
trips), so don't over-read the magnitude (3.6 vs 4.3) — but the DIRECTION (red >
green low-float) is solid across eras.

## Run 16 — is the red-low-float edge intraday or multi-day? (it's MULTI-DAY)

Decompose the red-low-float cell's return into the intraday leg (10:00 entry →
same-day close) and the swing leg (day-D close → 5d exit):

| leg | win% | avg ret | PF clip |
|---|---|---|---|
| intraday (10:00→close) | 53.5 | +1.61% | 2.121 |
| **swing (close→5d exit)** | 61.1 | **+7.96%** | **3.924** |
| FULL (10:00→5d exit) | 63.9 | +9.53% | 4.179 |

**It's a multi-day edge, not an intraday one.** ~84% of the full move (+7.96 of
+9.53%) accrues AFTER the day-D close. The intraday leg is real but modest (PF 2.12)
— the dip recovers into the close, but that's the small part; the swing leg (PF
3.92) is where the edge lives.

So the red/pulled-back low-float name at 10:00 is **a swing entry with a smart
intraday fill**, NOT a day-trade. The pullback buys a cheaper cost basis into a
multi-day continuation; the intraday recovery confirms it isn't breaking; the 5-day
hold harvests the move. Reconciles with Run 11 (same-day-close exit halves the edge)
and Run 5 (the 10:00 FILL is better; the PAYOFF is in the following days). Trade it
as: buy the 30-min dip on a low-float runner, hold ~5 days — do NOT flip intraday.

## Run 17 — anatomy of the A+ dip: how up, how deep, which dips work

Dissect the red-low-float cell on three no-lookahead dimensions from the partial OHLC.

**How much up on the day at entry?** Median **+14.4%**, q25–q75 **+12% to +20%**, range
+10..+29%. These are STRONG runners pulling back a slice, not marginal movers — closer
in spirit to "a dip from 25→20" than "15→10". The dip itself is **shallow**: avg
decline from the open **2.4%**, from the 10:00 high **5.2%**. Typical setup = ran to a
~+17% intraday high, pulled back ~5% off it to sit +14% on the day at 10:00.

**Does the decline-from-open magnitude matter? Yes — clear sweet spot:**

| decline from open | n | win% | PF |
|---|---|---|---|
| <1% (barely red) | 42 | 54.8 | 2.705 |
| **1–3%** | 57 | 71.9 | **4.872** |
| **3–6%** | 38 | 68.4 | **5.198** |
| >6% (deep) | 7 | 28.6 | **0.285** ← breaks |

A **1–6% pullback is the gold** (~5 PF, ~70% win). Barely-red (<1%) is just a flat
candle (2.7); >6% is the dip becoming a DUMP — the move is failing, not resting (0.28,
n=7 so noisy but directionally a failure). Decline-from-HIGH echoes it: 3–10% off the
peak best (3.8–5.1), <3% weakest (3.2).

**Bigger runners pull back better** (by move-on-day): 10–13% → PF 3.88, 22–30% → 5.34.
The deeper-into-the-run names taking a breather are the strongest.

**Refined A+ gate — red + low-float + decline-from-open ∈ [1%, 6%]:**
**95 trips · 70.5% win · PF 4.998 clip / 5.842 raw.** Trimming the flat (<1%) and
broken (>6%) tails lifts the cell from 4.18 → 5.00 clipped. The A+ setup crystallized:
**a low-float name up ~12–20% on the day that has pulled back 1–6% off its open into
10:00 — buy it, hold ~5 days.**

## Run 18 — A+ forward path 10:00→10:30 + checkpoint robustness (manual-trading window)

For manual trading: how do the A+ setups behave over the next 30 min, and are they
still valid if you only see them at 10:30?

**30-min forward path (10:00→10:30):** they go QUIET — avg +0.32%, median −0.09%,
49.5% higher at 10:30 (coin flip). No imminent rip. Consistent with the multi-day
(not intraday) edge (Run 16): the 10:00 entry buys a resting runner, not a launch.

**Still valid at 10:30?** YES, robustly. Of the 95 A+ setups re-measured at the 10:30
candle: **82 (86%) are still a red candle**, holding PF **4.817 / 71% win** (vs 5.00
at 10:00); the 13 that turned green are even better (5.85). The setup is a **stable
structure across the 10:00–10:30 window**, not a fleeting snapshot.

**SMB "first-hour high/low" check** (these A+ names): **67% set their RTH HIGH in the
first hour** (≤10:30) — supports SMB on the high side; only **46% set the RTH LOW**
that early. The asymmetry fits the cohort: we selected the PULLBACK names, so the high
(morning spike) is mostly in, you're buying the dip, and the low often comes later —
which is exactly why it's a SWING entry (you're not catching the bottom tick; the low
can come hours later but it recovers over days).

**What if it dipped at 10:00 but RECOVERED green by 10:30 — still a buy?** Yes, but you
pay up. Split by the 10:00→10:30 path, measuring 5d PF from the ACTUAL 10:30 entry price:

| 10:30 status | n | 10:00→10:30 | PF from 10:00 | PF from 10:30 | win@10:30 |
|---|---|---|---|---|---|
| still red @10:30 | 82 | −0.54% | 4.817 | **5.442** | 69.5 |
| recovered green @10:30 | 13 | +5.74% | 5.848 | **3.40** | 69.2 |

- **Still red at 10:30** → buy it at 10:30, you get the FULL ~5.4 PF (a touch better than
  10:00 — some dipped a bit more = cheaper basis). You missed nothing.
- **Already bounced green** → still a good buy at **PF 3.40 / 69% win**, but the +5.74%
  recovery means you pay ~5–6% more than the dip price, cutting 5.85→3.40. The
  "chased-the-bounce" tax — a downgrade, not a disqualifier.

The lesson reinforces the thesis: **the edge is buying the pullback cheap.** Catch the
red dip → 5+ PF; late and chasing the recovery → still 3.4. Better than skipping.

**⚠️ DO NOT AVERAGE DOWN.** The deeper-is-cheaper instinct is WRONG here. Run 17: the
1–6% dip is the gold (PF ~5), but once the decline breaks past **−6% off the open the
PF collapses to 0.285** (28.6% win) — decisively negative. Beyond 6% the character
flips from *pullback* (healthy rest) to *breakdown* (failing move). Adding to a loser
that has broken −6% drives size INTO the one bucket where the edge inverts. The −6%
break is a STOP signal, not a discount.

**Manual-trading upshot: you have a real ~30-min window.** No need to fire at exactly
10:00:00 — spot a low-float runner up 10–20% pulled back 1–6% off its open anytime in
the 10:00–10:30 window and it's the same ~5-PF setup (full edge if still red; ~3.4 if it
already bounced).

## Run 19 — A+ liquidity: too illiquid to trade? (No — the edge survives liquidity gates)

Liquidity profile of the A+ cell (n=95; ignore the max column — adj-price glitch rows):

| metric | q25 | median | q75 |
|---|---|---|---|
| price | $5.12 | $8.75 | $22.29 |
| 4w avg $-volume | $0.33M | $0.76M | $1.79M |
| shares by 10:00 | 0.24M | 0.62M | 1.82M |
| **$-volume by 10:00** | $2.16M | **$5.85M** | $34.6M |

Mostly sub-$10 small-caps (expected — low-float). The **4w-ADV looks thin (median
$0.76M)** — the legitimate concern. BUT you trade these on their EXPLOSIVE day: by
10:00 a median **$5.85M** has already traded (≈8× the baseline ADV — the rvol effect).

**Does the edge depend on the illiquid tail? No — it survives every gate (clip PF):**

| gate | n | PF |
|---|---|---|
| all A+ | 93 | 4.99 |
| price ≥ $5 | 70 | 4.92 |
| $vol by 10:00 ≥ $5M | 48 | **5.97** |
| ADV4w ≥ $1M | 36 | 14.81 (thin n) |
| price ≥ $5 & $vol-by-10:00 ≥ $5M | 35 | **5.19** |

Filtering to tradeable names KEEPS or improves the PF — the 5-PF is not an
illiquidity mirage. Deployable liquid version = **price ≥ $5, ≥$5M traded by 10:00 →
PF ~5.2 (35 trips)** — sizable without being the whole book. (The $14.8 ADV≥$1M cell
is sample-thin; trust direction, not magnitude.)

**Tradeoff = frequency.** Gating to liquid names halves an already-thin cell (~35–48
trips / 21y ≈ 1.5–2.5/yr). A real, size-able edge but RARE — a patience setup, not a
daily grind. Fits discretionary manual trading: wait for the clean liquid one, size up.

## Run 20 — move-from-open at 10:00: full book wants a modest push, low-float wants the dip

Breakdown of the deployable 10:00 book (rvol≥1.0, move≥10%; 3,906 trips) by
**intraday move from the 10:00 open** = `partial_close/partial_open − 1` (raw
ratio off `partial_candle_1000`, scale-free). Clipped PF (+50% cap). Question: are
names that moved significantly from the open better than those that didn't?

**Full book (float unfiltered) — an inverted-U; the sweet spot is a MODEST push:**

| Move from open (10:00) | trips | win% | avg | clip PF |
|---|---|---|---|---|
| < −6% (deep dip) | 24 | 33.3 | −2.1% | **0.64** |
| −6..−3% | 179 | 52.0 | +2.8% | 1.72 |
| −3..−1% | 269 | 56.1 | +3.9% | 1.86 |
| −1..+1% (flat) | 623 | 56.8 | +1.3% | 1.64 |
| **+1..+3%** | 541 | 57.9 | +2.7% | **2.10** |
| **+3..+6%** | 835 | 55.7 | +3.2% | **2.13** |
| +6..+10% | 849 | 57.2 | +2.9% | 1.74 |
| > +10% (strong push) | 586 | 50.0 | +3.0% | **1.49** |

**Moving significantly from the open is WORSE, not better.** Best = a modest green
push **+1..+6%** (PF ~2.1): up on the day, orderly, not parabolic. Both tails bleed:
the **>+10% strong-push tail is the worst green bucket** (1.49, 50% win, median +0.05%)
— you're chasing the vertical blow-off / buying the first-hour high (SMB effect); the
**<−6% deep-dip tail is the outright loser** (0.64), reconfirming the no-average-down
−6% line on the full book, not just low-float.

**Low-float (<$300M) — the shape INVERTS: the small dips are decisively best (A+):**

| Move from open (10:00), low-float | trips | win% | avg | clip PF |
|---|---|---|---|---|
| < −6% (deep dip) | 7 | 28.6 | −2.3% | **0.29** |
| **−6..−3%** | 38 | 68.4 | +11.9% | **5.20** |
| **−3..−1%** | 57 | 71.9 | +13.9% | **4.87** |
| −1..+1% (flat) | 87 | 56.3 | +3.2% | 2.61 |
| +1..+3% | 85 | 50.6 | +5.2% | 2.82 |
| +3..+6% | 157 | 61.1 | +6.1% | 2.79 |
| +6..+10% | 228 | 61.0 | +4.4% | 1.96 |
| > +10% (strong push) | 209 | 51.2 | +6.1% | 1.92 |

**On low-float the small dips are the best cell (PF ~5) — the exact OPPOSITE of the
full book**, and a clean re-derivation of yesterday's A+ setup from a different angle.
The monotone runs downhill from dip → push: the harder a low-float name is still
pushing off its 10:00 open, the worse (5.2 at −6..−3% → 1.9 at >+10%). Reading:

- **Full book** wants a *modest orderly push* (+1..+6%). The average name up ≥10% that
  keeps grinding green is fine; the parabolic one tops out.
- **Low-float** wants the *pullback* (−1..−6%). A low-float name already +10% that's
  still pushing higher at 10:00 is the crowded/chased one; the one that pulled back 1–6%
  is quiet accumulation — the swing entry that pays (avg forward ret +12–14%).
- **Both agree on the −6% floor** (0.29 low-float / 0.64 full): below −6% off the open
  the pullback has become a breakdown regardless of float. No-average-down holds
  universally. (Low-float dip cells are thin — 38/57 trips — trust direction over
  magnitude; consistent with Run 17's [1,6%] A+ band.)

**Manual-trading takeaway:** the "% from open" read is float-conditional. If it's a
low-float runner, the −1..−6% RED pullback is the A+ buy (don't wait for green). If
float is larger/unknown, prefer a modest green push (+1..+6%) and avoid the >+10%
parabola. In neither case buy the >+10% strong push or anything below −6%.

## Run 21 — late-day checkpoints (3:00 & 3:30 ET) + the rvol-lookahead calibration

Motivation (user): to trade this manually you must place MOC orders by ~3:50 ET, so
a *late* entry decision happens ~3:30. Two things to settle: (1) the production
`rvol≥5` is measured at the DAILY CLOSE — a 3:30 entrant can't know final daily
volume, so that gate is lookahead; find the as-of-checkpoint threshold equivalent to
"will close ≥5 rvol". (2) Does late entry get a morning-like PF?

Built `partial_candle_1500` (cutoff 900) and `partial_candle_1530` (cutoff 930).
Ran each checkpoint's book with rvol UNCAPPED (`--rvol-min 0`, move≥10%); the CSV's
`rvol_at_entry` is then the **as-of-checkpoint rvol using the engine's real 4w-daily
denominator** (partial cumulative volume ÷ CalendarMeanMa) — no SQL replication.
Joined each to the close book on (symbol, entry_date).

**Volume accrual for names that close rvol≥5** (2,614 of them): a median of **24% in
by 10:00, 83% by 3:00, 88% by 3:30**. So a name destined for ≥5 has done most of its
volume by mid-afternoon — the late thresholds can be high without losing recall.

**Calibration** (precision = % of passers that really close ≥5; recall = % of ≥5
closers caught). The equivalent-to-"closes ≥5" threshold at each checkpoint:

| checkpoint | threshold | precision | recall |
|---|---|---|---|
| 10:00 | **1.0** | 80.9% | 84.4% |
| 3:00 | **3.5** | 75.8% | 93.8% |
| 3:30 | **4.0** | 78.5% | 93.1% |
| close | 5.0 | (def) | (def) |

A clean monotone as the day fills: **1.0 → 3.5 → 4.0 → 5.0**. (User's estimate of
"north of 4" at 3:30 was right — 4.0 is the sweet spot; 4.5 buys precision but sheds
recall, 5.0 actually *drops* precision while losing recall.) These are the honest,
no-lookahead rvol floors to use at each entry time.

**Does late entry match the morning?** Books at their calibrated thresholds:

| checkpoint | calibrated rvol | book PF | trips | low-float PF | high-float PF |
|---|---|---|---|---|---|
| 10:00 | 1.0 | **1.87** | 3,906 | ~2.4 | — |
| 3:00 | 3.5 | 1.77 | 6,641 | 1.92 | 1.41 |
| 3:30 | 4.0 | 1.75 | 6,492 | 1.90 | 1.42 |

**Close, but the morning still wins by a hair, and the low-float edge shrinks late**
(2.4 → 1.9). The low-float double-duty PERSISTS at 3:00/3:30 (1.9 vs 1.4 high-float)
— it just isn't as pronounced as at 10:00. Per-setup the A+ dip is as strong late
(the clean −1..−6% low-float pullback still prints PF ~5–6 at 3:30), but such setups
are RARER late: by 3:30 the SMB "first-hour high" has played out and the low-float
move-from-open population has shifted hard to the right — the >+10%-off-open bucket
holds 1,109 trips at 3:30 vs 209 at 10:00, while the dip cells shrink from ~95 to ~78
trips. **Late entry works (PF ~1.9 low-float), but you fish a smaller pond of pristine
dips through a big pile of extended names.** For a manual trader: 10:00 is still the
prime window; 3:00/3:30 is a viable fallback with rvol floors 3.5/4.0 and the same
float + dip discipline.

## Run 22 — volume-distribution shape: sustained vs front-loaded (ratio AND diff)

Question (user): among names that are genuinely high-volume by 3:30 (rvol_3:30 ≥ 4),
does the *shape* of the day's volume matter — a stock busy all day vs one that
spiked early and went quiet (or was dead early and woke up late)? Two metrics on the
shared population, returns from the close book (fixed 5d hold, so only volume-shape
varies):
- **RATIO** = rvol_3:30 / rvol_10:00 (relative shape; median 3.61, q25–q75 2.88–4.82).
- **DIFF** = rvol_3:30 − rvol_10:00 (absolute late volume added; median 5.31, 3.81–10.34).

**RATIO — best in the MIDDLE, both extremes bad:**

| ratio (full book) | n | clip PF | | low-float PF |
|---|---|---|---|---|
| <2× (front-loaded) | 98 | 1.69 | | 2.14 |
| **2–3×** | 697 | **2.24** | | 2.95 |
| **3–4×** | 873 | 1.86 | | **3.80** |
| 4–6× | 705 | 1.79 | | 2.06 |
| 6–9× | 252 | **1.06** | | 1.68 |
| >9× (woke up late) | 100 | **1.06** | | 1.16 |

**DIFF — monotone up into the middle-high band, then fades:**

| diff (full book) | n | clip PF | | low-float PF |
|---|---|---|---|---|
| 2–4 | 794 | 1.41 | | 2.57 |
| 4–6 | 753 | 1.78 | | 2.79 |
| 6–9 | 403 | 2.12 | | 3.29 |
| **9–15** | 271 | **3.09** | | 3.34 |
| >15 (blowoff) | 501 | 1.68 | | 1.69 |

**They reconcile to one signal: busy EARLY *and* busier LATE = genuine all-day
accumulation, and that's what pays.** You want a name that added a lot of absolute
volume (diff 6–15) but was already meaningfully active at 10:00 (ratio 2–4×). The two
traps both show up as PF ~1.1–1.7: the **"woke up late"** name (huge ratio >6×, dead
at 10:00) and the **blowoff** (diff >15, climactic late volume). A very high ratio
means it wasn't participating early — those don't hold. Front-loaded (low diff) is
mediocre too. The accumulation signature (diff 6–15, ratio 2–4×) is the same profile
the swing hold rewards.

## Run 23 — should you ADD at the close to a 10:00 name that got more active?

Question (user): a stock that's already active at 10:00 (rvol_10 ≥ 1) and ends the
day *even more* active — is it worth adding to at the close? Took the 10:00 book
(rvol_10 ≥ 1) and measured the return of shares **added at the close price**
(close→5d exit = the add-in leg), split by end-of-day rvol:

| end-of-day rvol | n | add-leg avg ret | add-leg PF | low-float add-leg PF |
|---|---|---|---|---|---|
| <3 | 67 | +4.3% | 2.15 | 3.43 |
| 3–5 | 475 | +1.1% | **1.30** | 1.49 |
| 5–8 | 974 | +1.9% | 1.71 | 2.73 |
| **8–15** | 816 | **+3.3%** | **2.52** | **4.53** |
| >15 (blowoff) | 501 | +1.7% | 1.91 | 2.02 |

**Yes — but only in a BAND.** A 10:00 name that builds to **8–15× rvol by close** is
worth pyramiding into at MOC: the add-in leg pays PF 2.52 / **4.53 low-float** (+3.3
to +7.2% avg). That's confirmed all-day accumulation. But **don't add to the two
tails**: the tepid 3–5× (didn't confirm, weakest add at 1.30) and the parabolic >15×
(climax, 1.91/2.02). Same lesson as Run 22 — the add rule is "confirmed accumulation
(8–15×), not a fizzle and not a blowoff." Practically: enter the A+ dip at 10:00,
then if by ~3:30 the name has built to 8–15× rvol (and hasn't gone vertical), add a
second tranche at MOC.

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
