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

**Recut by the SPREAD (volume added after 10:00), not end-of-day level, and FILLED
AT 3:30.** The end-of-day-rvol split above conflates "how big it got" with "did it
keep building"; the spread `diff = rvol_3:30 − rvol_10:00` isolates the latter (is it
still being accumulated). And the add-in is filled at the **3:30 price** (the moment
you see the spread qualify), not at MOC — the more faithful mechanic, since you can
just buy it at 3:30 rather than wait for the close. It's the sharper add-in trigger:

| volume ADDED after 10:00 (diff) | n | add-leg PF (full) | low-float PF |
|---|---|---|---|
| 2–4 | 907 | 1.48 | 2.68 |
| 4–6 | 666 | 2.29 | **4.20** |
| **6–9** | 394 | 2.31 | 4.16 |
| **9–15** | 278 | **3.21** | 3.41 |
| >15 (blowoff) | 521 | 1.74 | 1.93 |

(The two PF columns are the SAME add-leg return on two populations: "full" = all
10:00 names in the bucket; "low-float" = the <$300M subset of that same bucket. The
gap is the float double-duty stacking on top of the volume-spread edge — n drops
accordingly, e.g. 394 → 82.) The **3:30 fill is slightly better than the close fill**
tested first (e.g. 4–6 low-float 3.72→4.20, 9–15 full 2.98→3.21): buying at 3:30
instead of waiting for MOC captures ~20 min less upward drift on names that tend to
drift into the close. The ratio `rvol_3:30/rvol_10:00` was also tested but is
flatter/less discriminating (mostly PF 1.7–2.0 across the middle); **going forward we
use the spread DIFFERENCE, not the ratio.** Its one sharp signal worth keeping: ratio
`<1.5×` (barely built after 10:00) is the only outright LOSER — full 0.64, low-float
0.59 with a NEGATIVE avg. Confirms the diff read: a name that stalled after the
morning is a bad add even if it was big at 10:00.

**Add rule (spread form):** at 3:30, add a tranche only if the name added **6–15×
rvol since 10:00** (full 2.3–3.2 / low-float 3.4–4.2); fill it right there at 3:30.
Skip if it stalled (diff < 2, or ratio < 1.5 → negative) or went climactic (diff >
15). Same lesson as Run 22, now as a clean afternoon trigger with a 3:30 fill.

## Run 24 — when is a 10:00 entry TOO FAR UP to keep holding? (take-profit line)

Question (user): after entering at 10:00, if the stock runs a lot, when should you
sell — how much up is too much? The system holds a fixed 5 days regardless; this
tests whether being up a lot at a checkpoint kills the remaining hold. For each 10:00
entry (rvol_10≥1, move≥10%), measure how far up it is at a checkpoint vs the 10:00
fill, then the FORWARD leg from that checkpoint to the 5-day exit.

**Forward leg by how far up at the DAY-1 CLOSE:**

| up at day-1 close | n | fwd avg | fwd PF |
|---|---|---|---|
| <0 (red day 1) | 1,895 | +2.2% | 1.68 |
| 0–5% | 1,406 | +2.1% | 1.81 |
| 5–10% | 415 | +2.1% | 1.62 |
| **10–20%** | 148 | +3.1% | 1.65 |
| **20–35%** | 33 | −0.9% | **0.88** |
| 35–60% | 6 | −7.6% | 0.43 |
| >60% | 3 | −24.6% | 0.07 |

**Forward leg by how far up at 3:30** (same-day, actionable before MOC — same shape):

| up at 3:30 | n | fwd avg | fwd PF |
|---|---|---|---|
| <0 | 1,956 | +2.1% | 1.66 |
| 0–5% | 1,385 | +2.4% | 2.03 |
| 5–10% | 392 | +2.4% | 1.76 |
| **10–20%** | 140 | +3.2% | 1.58 |
| **20–35%** | 23 | +0.2% | 1.03 |
| 35–60% | 10 | −6.3% | 0.41 |

**The "too much up" line is ~+20–25% over your entry, at either checkpoint.** Up to
+20% the forward hold still pays (10–20% is actually the BEST forward bucket, +3%
still to come — a modest run is NOT a sell). Past ~+20% the forward return goes flat
then sharply negative. Sell into strength only when the name is STILL up ~+20–25% at
3:30 or the close. (Extreme cells are thin — 33/23/10/6/3 trips — trust the monotone
direction, not the −24.6%. This is rare: ~1% of trades are up >20% at day-1 close.)

**Crucial distinction — MAX SPIKE (day-1 high) is the OPPOSITE, it's BULLISH.**
Full-trade PF by the day-1 RTH high excursion (max favorable move vs entry):

| day-1 high vs entry | n | full-trade avg | full PF |
|---|---|---|---|
| <10% | 3,422 | +1.6% | 1.45 |
| **10–20%** | 377 | +8.5% | 3.62 |
| **20–35%** | 83 | +16.2% | 5.47 |
| **35–60%** | 17 | +23.7% | **7.24** |

**Touching a big high intraday is strength, not a fade signal** — names that spiked
20–60% above entry at some point have the BEST full-trade PF (5.5–7.2). So the sell
signal is not "it printed a big high"; it's "it's STILL SITTING up +20–25% at a
checkpoint AFTER the move." A name that ran +30% then settled to +8% by 3:30 is in the
healthy bucket — hold it. A name parked at +25% into the close is the one to ring the
register on. (For low float the "STILL parked up +20%" state is both MORE common and
LESS of a sell — quantified in the low-float slice below.)

**Exit rule:** hold the full 5 days by default; a spike to a big intraday high is not
a sell. Sell into strength ONLY if the name is still up ~+20–25% over entry at a
checkpoint (3:30 or the close) — that extended/parabolic-and-holding state is where
the remaining hold turns negative. Mirror image of the entry rule (don't BUY the
>+10%-off-open parabola; don't HOLD the >+20%-from-entry parabola).

**Low-float slice — the +20% sell line RELAXES for low float (same pattern as Run 26).**
Forward leg from 3:30, full vs low-float (<$300M dollar-float):

| up at 3:30 | n (full) | PF (full) | n (LF) | PF (LF) | avg fwd (LF) |
|---|---|---|---|---|---|
| <0 (red) | 1,956 | 1.66 | 479 | **2.36** | +5.1% |
| 0–5% | 1,385 | 2.03 | 219 | **3.73** | +5.9% |
| 5–10% | 392 | 1.76 | 102 | 2.44 | +5.2% |
| 10–20% | 140 | 1.58 | 54 | 2.06 | +6.6% |
| **20–35%** | 23 | **1.03** | 11 | **1.87** | +4.9% |
| >35% | 10 | 0.41 | 3 | — | (3 trips, no losers) |

**The extended state is a full-book sell but still a low-float HOLD.** At 20–35% up at
3:30, the full book is break-even (PF 1.03 = the take-profit trigger), but low-float
still forwards **+4.9% at PF 1.87** — the scarce-supply squeeze keeps paying past where
the full book tops. Every bucket lifts (LF PF 2.0–3.7 vs full 1.6–2.0), and the >35%
low-float cell is too thin to call (3 trips, PF undefined — no losers). This mirrors
Run 26 exactly: **for low float, the take-profit threshold shifts UP** — don't sell the
+20–35%-at-3:30 low-float name; only the deeper extension (>+35% here, matching Run 26's
>+60% cumulative) is the real climax.

**And low float reaches that extended state ~2× as often** — 1.61% of low-float trips
are >20% up at 3:30 vs 0.84% of the full book. So the correction to the earlier "low
float almost never reaches the extended state" note: it reaches it MORE often (scarce
float → more explosive intraday runs), and once there it HOLDS BETTER, not worse. The
take-profit rule is genuinely a full-book rule that low float largely overrides.

## Run 25 — where in the day-1 range does the close sit? (high-spread, low-spread, position-in-range)

Follow-up to Run 24. First a WARNING: Run 24's "day-1 HIGH excursion → full PF 7"
table is partly **MFE selection bias** — a stock only *reaches* a +40% intraday high
if it went up a lot, so sorting on the max favorable excursion partly just re-selects
winners. To test whether the SHAPE ("spiked then settled") is real over-and-above
that, condition on WHERE IT CLOSED first, then split by the high↔close↔low geometry.

Population fixed to **close up 0–20% from the 10:00 entry** (the region where holding
still pays — Run 24 — and ~95% of the book; note "up from ENTRY", so the whole [10,20]
close band is only ~5% of trips because you're already long from 10:00 and most names
go sideways-to-down on day 1 relative to your fill — this is a MULTI-DAY edge, Run 16).

**High↔close spread (% the close sits BELOW the day-1 high):**

| high−close spread | n | fwd avg | fwd PF |
|---|---|---|---|
| at high (<3% off) | 1,344 | +1.6% | 1.64 |
| 3–8% off | 511 | +2.9% | 1.81 |
| **8–15% off** | 75 | +5.9% | **2.49** |
| 15–25% off | 17 | +6.8% | 1.78 |

**Low↔close spread (% the close sits ABOVE the day-1 low):** weak, barely
discriminates (1.6–1.9 across the range) — the low side carries little info.

**Position-in-range (0 = closed at the low, 1 = closed at the high) — the cleanest
summary of both, well-sampled:**

| where in day-1 range it closed | n | fwd PF |
|---|---|---|
| 0.0–0.2 (near low) | 13 | 3.92 *(thin)* |
| 0.2–0.4 | 71 | 1.69 |
| 0.4–0.6 (mid) | 253 | 2.01 |
| **0.6–0.8** | 616 | **1.98** |
| **0.8–1.0 (near high)** | 998 | **1.51** |

**Honest verdict: this is a MODEST tilt, not a new setup.** The one robust,
well-sampled signal is the NEGATIVE: **closing pinned at the day-1 high (position
0.8–1.0, ~1,000 trips) is the WEAKEST multi-day hold (PF 1.51)** — a parabolic close
with no digestion. Anything with a bit of pullback (upper-mid range 0.4–0.8, or 8–15%
off the high) holds modestly better (~2.0–2.5). The low-spread adds nothing; the
"spike then settle" magnitude buckets from Run 24 were largely MFE selection bias.
Net for manual trading: don't treat a strong high-to-close pullback as an A+ signal to
size into — but a name that closes *jammed at its high* is a slightly worse hold than
one that closed strong-but-not-parabolic. Low-float holds well across the board (every
range bucket ~2.3–2.6), so this range-geometry read matters least exactly where the
core edge already lives.

## Run 26 — path-dependent exit: does "too far up" hold for days 1–5, or just day 1?

Run 24 found "up +20% from entry → sell" on the ENTRY day. Does that standing rule
apply across the whole 5-day hold? Reconstructed each trade's hold path (adj closes
for hold-days k=1..5 via a window over `split_adjusted_prices`), and for each day-k
computed **cumulative gain from entry** (day-k close / entry − 1) and the **forward
leg** (day-k close → final 5d exit). Forward PF of CONTINUING to hold, bucketed by how
far up you are at the day-k close (**cumulative from entry, NOT the single-day move**):

| up at day-k close (cumulative) | day 1 | day 2 | day 3 | day 4 |
|---|---|---|---|---|
| 0–10% | 1.76 | 1.51 | 1.39 | 1.31 |
| 10–20% | 1.65 | 1.49 | 1.20 | 1.40 |
| **20–35%** | **0.88** | **1.80** | **1.65** | **1.54** |
| 35–60% | 0.43 | 0.78 | 0.95 | 0.78 |
| >60% | 0.07 | 0.00 | 1.05 | 0.49 |

**The +20% sell rule is a DAY-1 phenomenon, not a standing rule.** Key reads:
- **Day 1:** up +20–35% → forward PF **0.88** (negative avg) = SELL. A parabolic move
  on the ENTRY day is a crowded gap-and-go top that gives back. (Confirms Run 24, and
  matches its independent 3:30 cut.)
- **Days 2–4:** the SAME +20–35% level FLIPS to a good HOLD (PF **1.80 / 1.65 /
  1.54**). Reaching +25% *spread over 2–4 sessions* is a trend working, not a climax —
  the velocity is gentler. Do NOT apply the day-1 sell rule here.
- **+35%+ stays a sell at every horizon** (0.43 / 0.78 / 0.95 / 0.78) — that much
  extension gives back regardless of when it's reached. **>60% is a sell everywhere**
  (the day-3 1.05 is a 9-trip noise cell).
- Day 5 is the exit day itself → forward leg ≈ 0 by construction; ignore for decisions.

**Same level, different velocity:** +20% *in the first session* is a blow-off; +20%
*built over several days* is a trend. The "extended" threshold scales UP as the move
earns more time. (Day-1 20–35%/35%+ cells are thin — 33/6 — but agree with Run 24's
independent 3:30 cut; the days 2–4 20–35% cells are better sampled at 66/112/118.)

**Contrast — the SINGLE-DAY move barely discriminates** (checked separately, pooled
days 2–5): down/flat/up-to-+20% on a given day all sit ~1.3–1.4 forward PF; only the
tails talk — a **single-day DOWN >5% is the BEST hold (1.67)** (a sharp red day mid-
hold is a shakeout that reverts, not a scare), and a **single-day UP >35% is a sell
(0.67)** (vertical mid-hold session = climax). So the exit signal lives in **how
extended the POSITION is (cumulative), not how much it moved TODAY** — except: don't
panic-sell a sharp down-day, and do take a vertical +35% single-session blow-off.

**Exit rule (path-dependent):** (1) Day 1 up +20%+ → sell (entry-day parabola). (2)
Days 2–4, up +20–35% is fine to KEEP holding (trend); only +35%+ is a sell. (3) Any
day: a +35% single-session spike → take it; a sharp single-day down move → hold, it's
a shakeout.

**Low-float slice (dollar-float <$300M at entry).** The same upside breakdown, full
vs low-float side by side (positive cumulative buckets, per hold-day; PF = forward leg
day-k close → exit):

| up at day-k close | k | n (full) | PF (full) | n (low-float) | PF (low-float) |
|---|---|---|---|---|---|
| 0–10% | 1 | 1,821 | 1.76 | 348 | **2.49** |
| 10–20% | 1 | 148 | 1.65 | 58 | **2.88** |
| **20–35%** | 1 | 33 | **0.88** | 15 | **1.43** |
| 0–10% | 2 | 1,748 | 1.51 | 320 | **2.70** |
| 10–20% | 2 | 322 | 1.49 | 96 | 1.98 |
| **20–35%** | 2 | 66 | **1.80** | 35 | **2.78** |
| 35–60% | 2 | 24 | 0.78 | 11 | 2.13 |
| 0–10% | 3 | 1,640 | 1.39 | 292 | 1.58 |
| 10–20% | 3 | 376 | 1.20 | 128 | 1.44 |
| **20–35%** | 3 | 112 | **1.65** | 50 | **2.33** |
| 0–10% | 4 | 1,571 | 1.31 | 277 | 1.41 |
| 10–20% | 4 | 426 | 1.40 | 124 | 1.87 |
| **20–35%** | 4 | 118 | **1.54** | 59 | **2.11** |

**Low float lifts every cell and sharpens the day-1 divide.** Reads:
- **Low-float PF > full-book PF in every bucket** (usually by 0.3–1.2). The <$300M
  float edge (Run 19 / A+ setup) compounds through the hold, not just at entry — a
  low-float name up modestly is a *scarcer* supply squeeze, so continuation is stronger.
- **The day-1 vs days-2–4 flip at +20–35% survives — and is sharper.** Day-1 +20–35%
  low-float is **1.43** (barely above break-even, still the worst upside cell on day 1);
  the SAME level on days 2–4 is **2.78 / 2.33 / 2.11** — a clean, well-sampled
  (35/50/59) trend signal, not noise. The "same level, different velocity" rule holds
  more crisply on the core names than the full book.
- **Early modest-gain cells are where the money is:** low-float up 0–20% on day 1–2
  runs **2.5–2.9** — the fat, well-populated part of the book. The A+ dip name that's
  quietly up <20% a day or two in is the highest-conviction hold in the whole study.
- **The +35% take-profit line SOFTENS for low float.** Pooling days 2–4 for sample
  (per-day cells were too thin): the 35–60% bucket is where full-book and low-float
  split hardest —

  | up at day-k (days 2–4 pooled) | n (full) | PF (full) | n (LF) | PF (LF) | avg fwd (LF) |
  |---|---|---|---|---|---|
  | 20–35% | 296 | 1.65 | 144 | **2.37** | +4.6% |
  | **35–60%** | 111 | **0.83** | 61 | **1.46** | +3.3% |
  | >60% | 29 | 0.48 | 18 | **0.69** | −3.8% |

  In the full book **35–60% is a sell** (PF 0.83, avg forward −0.2% = the "give it back
  above +35%" rule). But on the **low-float core 35–60% is still a mild HOLD** (PF 1.46,
  **56% win**, avg +3.3%, median +0.9%; n=61, worst −34% / best +134%) — a right-skewed
  *continuation*, not a round-trip. The scarce-supply squeeze pushes the climax threshold
  UP: for a <$300M name the top only truly breaks at **>60%** (PF 0.69, avg −3.8% —
  same cliff as the full book, just reached later). So the +35%+ take-profit is a
  **full-book rule; for low float, let winners run to ~+60% before taking.**

**Net:** the path-dependent exit rule is unchanged, but on the low-float core the HOLD
side is stronger everywhere and the only genuine day-1 sell (parabola +20%+) is the
lone weak upside cell — reinforcing that for these names, *patience through the modest-
gain band is the edge*.

## Run 27 — the DOWNSIDE half: when a hold is underwater, cut or hold?

Run 26 did the upside; this does the drawdown. Same hold-path reconstruction, forward
PF of CONTINUING to hold, bucketed by how far UNDERWATER you are at the day-k close
(cumulative from entry). Pooled days 1–4:

| cumulative at day-k close | n (full) | fwd PF (full) | n (low-float) | fwd PF (low-float) |
|---|---|---|---|---|
| **< −20%** | 116 | **0.93** | 41 | 0.84 |
| **−20..−10%** | 809 | **1.78** | 251 | **2.05** |
| −10..−5% | 1,694 | 1.45 | 398 | 1.95 |
| −5..0% | 4,437 | 1.57 | 885 | 1.93 |
| ≥0 (up) | 8,530 | 1.46 | 1,884 | 1.99 |

**A moderate drawdown is NOT a sell — it's the BEST forward bucket.** Down −10..−20%
mid-hold has a HIGHER forward PF (1.78 full / 2.05 low-float) than being flat or up.
The buy-the-dip thesis operating INSIDE the hold: these are mean-reverting momentum
names, so a pullback during the hold is a discount that reverts, not deterioration.
Do NOT cut a −10..−20% loser — that's exactly where the recovery edge is strongest.

**The one exception — the −20% cliff.** Below −20% from entry, continuing to hold
turns negative (PF 0.93 full / 0.84 low-float, ~15% win at day 1). The move has
genuinely broken; mean-reversion stops. **Hold-time cut-loss line = −20% from entry**
— the multi-day analog of the −6%-from-open entry rule (a pullback is a discount until
it becomes a breakdown), wider because it's over 5 days not 30 minutes. (−20% cells
thin — 116/41 — but unambiguous and consistent day-by-day: day-1 0.05, day-3 0.94.)

**This is WHY stop-losses never worked for this system.** Any stop tight enough to
fire lands in the −10..−20% band = the single best forward-hold cell; a stop there
doesn't cut losers, it ejects you from winners mid-recovery. The only drawdown that
predicts further damage is beyond −20%, and by then the "stop" is just the natural
hold-time cut-loss line, not a protective stop. The edge lives THROUGH the drawdown.

**Full multi-day exit rule (Runs 24/26/27 combined):** hold through the **−20% … +35%**
band. Exit early only when the position BREAKS out of it:
- **Down side:** cut if below **−20%** from entry (broken; mean-reversion gone).
- **Up side:** take profit if up **+35%+** (any day), or up **+20%+ on DAY 1** only
  (entry-day parabola); +20–35% on days 2–4 is a healthy trend — keep holding.
- **Single-day tells:** a sharp down day is a shakeout (hold, best bucket); a vertical
  +35% single-session spike is a climax (take it).
- Otherwise ride the 5-day time-stop. No tight protective stop — it only ejects you
  from the recovery.

## Run 28 — yearly & monthly breakdown: does the edge hold in every regime?

Robustness check on the production 10:00 book (rvol≥1, move∈[10,30%], the A+ gates;
3,906 trips, 2005-02 → 2026-05). Per-trip return = `exit_price/entry_price − 1`; PF is
the standard +50%-clip. Low-float = dollar-float <$300M at entry.

**Yearly — FULL BOOK vs LOW-FLOAT:**

| year | n | win% | PF (full) | n (LF) | PF (LF) |
|---|---|---|---|---|---|
| 2005 | 128 | 54 | **0.98** | 6 | 1.76 |
| 2006 | 136 | 57 | 1.82 | 7 | 2.82 |
| 2007 | 136 | 51 | 1.38 | 1 | — |
| 2008 | 68 | 62 | 1.70 | 1 | — |
| 2009 | 117 | 53 | 1.67 | 12 | 0.77 |
| 2010 | 172 | 58 | 1.90 | 18 | 6.76 |
| 2011 | 156 | 53 | 1.48 | 14 | 0.81 |
| 2012 | 139 | 49 | 1.51 | 31 | 1.00 |
| 2013 | 239 | 57 | 2.24 | 67 | 2.63 |
| 2014 | 185 | 57 | 1.76 | 55 | 2.50 |
| 2015 | 146 | 58 | 2.21 | 32 | 3.72 |
| 2016 | 138 | 59 | 3.65 | 26 | 8.50 |
| 2017 | 243 | 59 | 2.18 | 77 | 2.74 |
| 2018 | 211 | 63 | 2.35 | 51 | 2.84 |
| 2019 | 159 | 59 | 2.30 | 29 | 3.91 |
| 2020 | 264 | 55 | 2.05 | 87 | 2.37 |
| 2021 | 382 | 51 | 1.39 | 117 | 1.63 |
| 2022 | 79 | 59 | 2.38 | 24 | 4.87 |
| 2023 | 169 | 56 | 1.53 | 43 | 2.70 |
| 2024 | 269 | 55 | 1.93 | 69 | 3.61 |
| 2025 | 277 | 51 | 1.63 | 64 | 2.21 |
| 2026* | 93 | 47 | **0.89** | 37 | 1.07 |

**20 of 22 years are profitable on the full book (PF > 1).** The two exceptions:
- **2005** — PF 0.98, essentially break-even, the very first year of coverage.
- **2026** — PF 0.89, but it's a **partial year** (only through mid-May, 93 trips).

**On the low-float core, BOTH full-book losers flip green** — 2005 lf 1.76, 2026 lf
1.07 — so **the tradeable A+ cohort is profitable in 22/22 years** (the only lf misses
are tiny-sample noise cells: 2007 n=1, 2009 n=12 at 0.77, 2012 n=31 at exactly 1.00,
2011 n=14 at 0.81 — all thin, none the real book). Every well-sampled low-float year is
≥1.6, most 2.5–8.5. **The float filter isn't just a PF booster — it's the regime-
robustness fix.** No year of the 22 broke it; the weakest full-book years (2005, 2007,
2011, 2021 growth-unwind, 2025) all still clear 1.0, and their low-float slices clear
it comfortably.

**Regime spot-checks:** 2008 (GFC, 68 trips) PF 1.70 — the setup survives a crash
because it's a *pullback-and-hold on names already running*, not a beta bet. 2020 (264
trips, +5.9% avg) PF 2.05 — the COVID-mania year is the highest trip count and richest
average but NOT the highest PF (the mania inflates losers too). 2021 (382 trips, PF
1.39) is the softest well-sampled year — the growth-stock unwind — yet still positive.

**Calendar-month seasonality (all years pooled):**

| month | n | PF (full) | PF (LF) |  | month | n | PF (full) | PF (LF) |
|---|---|---|---|---|---|---|---|---|
| Jan | 305 | 2.01 | 1.96 |  | Jul | 287 | 2.12 | 2.91 |
| Feb | 514 | 1.62 | 1.84 |  | Aug | 420 | 1.54 | 2.88 |
| **Mar** | 363 | **1.13** | 1.36 |  | Sep | 219 | 1.50 | 4.60 |
| Apr | 291 | 1.47 | 1.70 |  | Oct | 310 | 1.69 | 2.06 |
| **May** | 380 | **2.47** | 3.07 |  | **Nov** | 394 | **2.70** | 4.65 |
| Jun | 185 | 2.15 | 2.19 |  | Dec | 238 | 1.83 | 2.87 |

**No dead month — all 12 clear PF 1.0.** Weakest is **March (1.13)**; strongest **Nov
(2.70) and May (2.47)**. Low-float lifts every month (Sep 1.50→4.60, Nov 2.70→4.65).
No obvious "sell in May" / seasonal hole to avoid.

**Year-month cells:** 182/253 (72%) are PF≥1; of the 71 losing cells, 26 are thin
(<5 trips). Individual months scatter (~15 trips/month → high variance), which is *why*
the system holds ~5 days across many names rather than concentrating in a window — the
edge is a law-of-large-numbers effect over the trip population, not a monthly timing
bet. The yearly aggregation (where each year pools ~150 trips) is the honest robustness
unit, and there it's 20/22 full / 22/22 low-float.

*(\*2026 partial — through 2026-05-13 only.)*

## Run 29 — live scanner: engine-validated gate + the recycled-ticker gap fix

First cut of the **real-time detection process** (`scripts/equity/live_scan.py`). It pulls
Massive's full-market snapshot (`/v2/snapshot/locale/us/markets/stocks/tickers`, ~11k tickers
in one call — the SAME vendor as the backtest data), joins the D-1 daily factor base computed
in DuckDB, and applies the exact production `EntryConfig` gate + the <$300M low-float tag. The
rvol floor is checkpoint-calibrated (intraday volume still accumulating): ~1.0 @ 10:00 → ~5.0
@ close (1.25 default for ~11:00 ET). Feed decision: **Massive Advanced ($199/mo real-time) for
3 months**; IBKR = execution + optional Benzinga news; TradingView ruled out (no API / ToS).

**Setup taxonomy (user, this session):** **A+** = low-float ($<300M) AND a dip in the first
30m off the open (the red-pullback entry); **A** = passes all gates incl. float, no early dip;
**B** = passes all gates EXCEPT float (mid/large float) → tradeable at smaller size.

### Engine-vs-SQL validation (do the SQL indicators match the backtest to the decimal?)

Ran the engine in parity mode over 2023-2026 (1,151 trips), diffed each indicator the live
scan reproduces in SQL against the engine's own CSV columns:

| indicator | result |
|---|---|
| **ATR% (log 14-bar)** | ✅ exact — max diff 0.0 |
| **tightness (14-bar range / linear ATR)** | ✅ exact — max diff 0.0 |
| **rvol (28-cal-day mean vol)** | ✅ **exact after fix** — 0.0 across 651 warm trips |
| **pct_52w (252-bar high)** | ✅ matches after gap-fix; residual diffs were engine warmup artifacts |

- **rvol fix:** the window must be `[entry−28 days, entry)` anchored on the ENTRY date, not a
  `RANGE PRECEDING` off the D-1 row (which mis-bounds the calendar span). Traced on MSGM
  (engine 6.14 = 394412 / mean(19 bars)=64191). Now 0.0 diff.

### The recycled-ticker contamination (and why the engine is *incidentally* safe)

Investigating the pct_52w mismatches surfaced a real, if cosmetic, issue. Some tickers are
**recycled**: e.g. `MRX` = an old company (2003→2012) whose ticker went dark for **4,154 days**,
then relisted as **Marex Group plc** (CIK 1997464, IPO 2024-04). A naive "last 252 bars" 52w
window reaches ACROSS the gap and prices the new company against the OLD company's high ($42.52
from 2012) — garbage.

- **Why the ENGINE mostly avoids it — by accident, not design.** The engine has NO gap logic.
  Its `MaxMa(252)` is a fixed 252-*bar* deque, same as the naive SQL. It's protected only
  because (a) its feed SQL filters `WHERE p.date >= $start` (the `--start-date` CLI arg,
  default 2005-01-01), so pre-start bars are never streamed to it, and (b) for MRX the gap is
  >252 bars, so old bars decay out of the window anyway. **A ticker that recycles WITHIN ~252
  bars of an entry, inside the backtest window, would still contaminate the engine's 52w high.**
  The safety is incidental; the latent bug is real (and for the live scan, which queries ALL
  history with no start floor, it's fully exposed).
- **How much did it touch the production book?** Scanned all 3,906 A+ trips: **29 (0.74%)** have
  a >45-day gap inside their 52w window; only **2 (0.05%)** are actually contaminated, and BOTH
  in the SAFE direction (an old high leaked in → pct_52w too NEGATIVE → gate STRICTER, not
  looser). The bug **cannot have created false positives**; it can only have silently rejected a
  few valid setups. **The 22/22-year result is untouched** — this is cosmetic, not
  result-distorting.

### The fix — gap-episode partitioning (applied to the live scan)

Sever every rolling window at a >45-day gap. Pure SQL, no state machine, via the "sessionize"
idiom: (1) `is_break = date − LAG(date) > 45`; (2) `episode = SUM(is_break) OVER (PARTITION BY
ticker ORDER BY date)` — a running total that ticks up at each gap; (3) `PARTITION BY ticker,
episode` on the 52w / ATR / tightness / maxAtrLog windows, so no window can span a gap. Verified:
MRX splits into two episodes (hi $42.52 old / $66.51 new); the live scan uses the new one. This
is the SQL equivalent of resetting the engine's rolling state on a detected gap — both yield the
same result, so the episode logic doubles as the spec for the eventual engine reset.

**Two follow-ups parked for later:** (1) apply the same gap-reset in the F# engine
(`RollingMa`/`Types.fs` — reset the deques + `barsSeen` on a detected gap), validated against
this SQL; (2) the live scan reproduces the engine indicators in SQL — a cleaner long-term design
is an engine "emit D-1 snapshot" mode so the live gate is provably identical with zero
re-implementation. Neither is urgent (the SQL is engine-validated to the decimal).

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
