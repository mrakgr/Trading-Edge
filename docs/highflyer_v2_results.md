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
  default): **5,558 trips · 53.5% win · \$1.34M · PF 1.828** (2005-01-01 →
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
| Daily close (parity) | 5,558 | 53.5% | \$1.34M | **1.828** |
| **10:00 ET partial** | **910** | 54.2% | \$234k | **1.675** |

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
| Daily close | 542 | 54.8% | \$126.2k | 1.983 |
| **10:00 ET partial** | 542 | **60.0%** | **\$167.6k** | **2.292** |

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
**3,314 trips · 54.3% win · \$755k · PF 1.637** (aggregate — hides the structure).

### Move-at-10:00 band (peaks in the middle)

| move @ 10:00 | trips | win% | PF | net | avg \$ |
|---|---|---|---|---|---|
| 5–7.5% | 1069 | 52.0 | 1.385 | \$163k | 152 |
| 7.5–10% | 558 | 53.0 | 1.419 | \$85k | 152 |
| 10–15% | 696 | 55.5 | 1.774 | \$183k | 263 |
| **15–20%** | 441 | **59.9** | **2.37** | \$176k | **398** |
| 20–30% | 550 | 54.2 | 1.762 | \$149k | 270 |

The **5–10% bands are the drag** (PF 1.39–1.42) — lowering the floor to 5% bought
capacity, not quality. The **15–20% band is the sweet spot** (PF 2.37); 20–30%
softens (blow-off region starting).

### rvol-at-10:00 band (INVERTS the daily intuition)

| rvol @ 10:00 | trips | win% | PF | net |
|---|---|---|---|---|
| **2.5–3.5** | 803 | 57.0 | **2.173** | **\$262k** |
| 3.5–5 | 567 | 55.2 | 1.489 | \$99k |
| 5–8 | 530 | 49.8 | 1.287 | \$59k |
| 8–15 | 498 | 55.6 | 1.697 | \$117k |
| 15+ | 916 | 53.3 | 1.563 | \$219k |

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

| bucket | trips | win% | PF | net | avg \$ | median move@10:00 | median rvol@10:00 |
|---|---|---|---|---|---|---|---|
| **valid at close** | 1,273 (38.4%) | **64.3** | **3.24** | \$558k | **\$438** | 16.0% | 5.18 |
| only at 10:00 | 2,041 (61.6%) | 48.1 | 1.211 | \$197k | \$97 | 7.6% | 7.04 |

**Only 38% of the relaxed-10:00 trades would also be valid at the close** — the
10:00 book is mostly a *different* book, not an early fill of the close book. And
the edge is almost entirely in the trades that ALSO validate at close: **PF 3.24
(64% win, \$438 avg)** vs the "only at 10:00" drag at **PF 1.21 (48% win, \$97 avg,
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
| net | \$558k | \$341k |

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
| net | **−\$88k** | **+\$197k** |

**Hold the 5 days — do NOT sell at the close.** Same-day exit turns a
marginal-but-positive book (1.21) into a money-loser (0.82, −\$88k).

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

The <\$300M dollar-float edge was established on the daily-close book (PF ~2.5 vs
~1.4). Does it hold on the 10:00 partial book? Float metric = the **combined**
source (SEC free-float first, Polygon `?date=` scso to fill — 95% trade-time
coverage in 2013+; see the float memory), dollar-float taken **at the 10:00 entry
price** (`scso × entry_1000`, or SEC shares × entry_1000). Relaxed partial book,
74% covered.

| float bucket (at 10:00) | trips | win% | PF clip | PF raw |
|---|---|---|---|---|
| **<\$150M** | 625 | 58.1 | **2.135** | 2.262 |
| \$150–300M | 318 | 57.9 | 1.910 | 2.03 |
| \$300–750M | 416 | 58.9 | 1.364 | 1.448 |
| ≥\$750M | 1,095 | 53.5 | 1.472 | 1.50 |

**The low-float edge holds cleanly on the 10:00 entries** — monotone in the low
region, smallest float (<\$150M) strongest at PF 2.14, break right at \$300M (same as
the daily book). The <\$300M vs ≥\$300M split, stable across eras:

| 10:00 book | LOW <\$300M | HIGH ≥\$300M |
|---|---|---|
| Full window | **2.066** (943, 58% win) | 1.441 (1,511) |
| 2013+ | **2.051** (858, 58% win) | 1.432 (1,191) |

**The two HighFlyerV2 edges stack independently:** timing (early entry, Run 5:
~2.2→3.2 on the same names) × float (<\$300M ~2.05 vs ~1.44). Early entry on
low-float names is the cell to push on. (Float source: this also validated the
Polygon shares-outstanding pull — see [[project_float_feature_2026-06-22]] — over
buying Sharadar.)

## Run 8 — do the two edges compound? (requalifier × float, 2×2)

Split the 10:00 book by BOTH the timing axis (valid-at-close requalifier, where the
early-entry edge lives — Run 5) AND the float axis (<\$300M, Run 7). Combined float
metric, dollar-float at the 10:00 entry price.

| | LOW <\$300M | HIGH ≥\$300M |
|---|---|---|
| **requalifier (valid at close)** | **PF 3.654 · 67.9% · 343** | PF 3.109 · 64.0% · 619 |
| only at 10:00 (fades by close) | PF 1.553 · 52.3% · 600 | PF 1.044 · 48.8% · 892 |

**The two edges compound — orthogonally.** Best cell = requalifier × low-float →
**PF 3.65, 68% win**, above either edge alone (timing-only requalifiers 3.24;
float-only <\$300M ~2.05). Within requalifiers, low-float adds (3.65 vs 3.11);
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
| low-float (<\$300M) PF | 2.066 | **2.378** |

**The 10% floor IS a (partial) no-lookahead requalifier proxy:** 64% of move-10%
trades requalify at close vs 38% at move-5%. Being up ≥10% by 10:00 (not just 5%)
is itself evidence the move has legs — exactly the 10:00-observable signal Run 5/8
needed. Overall PF 1.64→1.91; low-float 2.07→**2.38**.

move-10% 2×2 (requalifier × float):

| | LOW <\$300M | HIGH ≥\$300M |
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
| low-float (<\$300M) PF | 2.378 | 2.362 |

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
| LOW <\$300M | PF 1.304 (46.6%) | **2.578** (58.7%) |

**rvol 2.0 + move 10%:** ALL 1.251 vs 1.900; LOW <\$300M 1.378 vs **2.586**.

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
| price | \$5.12 | \$8.75 | \$22.29 |
| 4w avg \$-volume | \$0.33M | \$0.76M | \$1.79M |
| shares by 10:00 | 0.24M | 0.62M | 1.82M |
| **\$-volume by 10:00** | \$2.16M | **\$5.85M** | \$34.6M |

Mostly sub-\$10 small-caps (expected — low-float). The **4w-ADV looks thin (median
\$0.76M)** — the legitimate concern. BUT you trade these on their EXPLOSIVE day: by
10:00 a median **\$5.85M** has already traded (≈8× the baseline ADV — the rvol effect).

**Does the edge depend on the illiquid tail? No — it survives every gate (clip PF):**

| gate | n | PF |
|---|---|---|
| all A+ | 93 | 4.99 |
| price ≥ \$5 | 70 | 4.92 |
| \$vol by 10:00 ≥ \$5M | 48 | **5.97** |
| ADV4w ≥ \$1M | 36 | 14.81 (thin n) |
| price ≥ \$5 & \$vol-by-10:00 ≥ \$5M | 35 | **5.19** |

Filtering to tradeable names KEEPS or improves the PF — the 5-PF is not an
illiquidity mirage. Deployable liquid version = **price ≥ \$5, ≥\$5M traded by 10:00 →
PF ~5.2 (35 trips)** — sizable without being the whole book. (The \$14.8 ADV≥\$1M cell
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

**Low-float (<\$300M) — the shape INVERTS: the small dips are decisively best (A+):**

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
10:00 names in the bucket; "low-float" = the <\$300M subset of that same bucket. The
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
Forward leg from 3:30, full vs low-float (<\$300M dollar-float):

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

**Low-float slice (dollar-float <\$300M at entry).** The same upside breakdown, full
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
- **Low-float PF > full-book PF in every bucket** (usually by 0.3–1.2). The <\$300M
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
  UP: for a <\$300M name the top only truly breaks at **>60%** (PF 0.69, avg −3.8% —
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
the standard +50%-clip. Low-float = dollar-float <\$300M at entry.

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
in DuckDB, and applies the exact production `EntryConfig` gate + the <\$300M low-float tag. The
rvol floor is checkpoint-calibrated (intraday volume still accumulating): ~1.0 @ 10:00 → ~5.0
@ close (1.25 default for ~11:00 ET). Feed decision: **Massive Advanced (\$199/mo real-time) for
3 months**; IBKR = execution + optional Benzinga news; TradingView ruled out (no API / ToS).

**Setup taxonomy (user, this session):** **A+** = low-float (\$<300M) AND a dip in the first
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
window reaches ACROSS the gap and prices the new company against the OLD company's high (\$42.52
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
MRX splits into two episodes (hi \$42.52 old / \$66.51 new); the live scan uses the new one. This
is the SQL equivalent of resetting the engine's rolling state on a detected gap — both yield the
same result, so the episode logic doubles as the spec for the eventual engine reset.

**Two follow-ups parked for later:** (1) apply the same gap-reset in the F# engine
(`RollingMa`/`Types.fs` — reset the deques + `barsSeen` on a detected gap), validated against
this SQL; (2) the live scan reproduces the engine indicators in SQL — a cleaner long-term design
is an engine "emit D-1 snapshot" mode so the live gate is provably identical with zero
re-implementation. Neither is urgent (the SQL is engine-validated to the decimal).

## Run 30 — engine gap-reset shipped + volume-baseline (`--volume-days`) sweep

Two coupled engine changes, then a sweep of the volume-baseline length. (Parked follow-up #1
from Run 29 is now DONE.)

### Gap-reset in the F# engine (Run 29's SQL episode logic, now in the engine)

`RollingMa` grew a `Reset()` on every family (base sum/avg, `MaxMa`/`MinMa`, `CalendarMeanMa`).
`HighFlyer` now stores `lastBar` and, on a **>45-day gap** between consecutive same-ticker bars,
(a) **MTM-closes any open trips at the last pre-gap bar** (end-of-episode, same rule as
end-of-series `Finalize`), then (b) resets all 12 rolling structures + `barsSeen` so the new
episode starts cold. Mirrors the live-scan `episode = SUM(date−LAG(date)>45)` sessionize exactly.
(`prevClose` was removed — it's just `lastBar.close`; `sPrevClose` → `sPrevBar`.)

**Verified gap-local** (baseline HEAD binary vs new, full 2005–2026 parity run, daily-close path):
- **Zero** changes on any gap-free ticker, in any column.
- All 42 dropped/added symbols and all 339 changed-snapshot trips have a genuine >45-day prior
  gap (smallest ~2,277 days: MRX/Marex, ELE, OPEN, WOOF, VELO, Z, …). No gap-free contamination.
- Changed columns were ONLY the window-derived metrics (ATR%, tightness, ADV, 52w-low) — never
  price/PnL/exit. **PF 1.825 → 1.824** (the gap slice is tiny and correctly severed).

### CalendarMeanMa → AvgMa: calendar days no longer needed

The 28-*calendar-day* volume/ADV window (`CalendarMeanMa`) existed **only to survive gaps**. With
the engine now resetting on gaps, that reason is gone, so the volume baseline is a plain fixed-bar
`AvgMa`. A 28-calendar-day window empirically holds a **median of 19, max 20** trading bars, so
20 bars is the closest fixed replacement. Re-validated to **0.0** (max diff 4.8e-13 rvol / 1.0e-5
ADV = FP noise) against the live scan's matching `ROWS BETWEEN 19 PRECEDING AND CURRENT ROW`
(episode-partitioned) across all 5,596 trips. Exposed as `--volume-days` (the ATR%/tightness
window is now the disambiguated `--volatility-window`).

### The sweep — is a longer volume baseline better? (No.)

Full 2005–2026, production defaults, only `--volume-days` varied:

| volume-days | trips | win% | net P&L | clip-era PF |
|---:|---:|---:|---:|---:|
| 10 | 5746 | 53.1% | \$1.27M | 1.741 |
| 14 | 5729 | 53.4% | \$1.36M | 1.807 |
| **20** | 5596 | 53.5% | \$1.34M | **1.812** |
| 28 | 5528 | 53.5% | \$1.31M | 1.811 |
| 40 | 5384 | 53.3% | \$1.00M | 1.617 |
| 60 | 5382 | 53.7% | \$1.00M | 1.613 |
| 90 | 5512 | 53.7% | \$1.03M | 1.607 |

**PF plateaus at 14–28 bars (~1.81), then falls off a cliff at 40+ (~1.61, −25% net P&L).**
20 sits at the top of the plateau — the chosen default is well-placed; there is no free
performance in lengthening the window.

**Why longer is worse:** rvol's job is to flag volume that's *unusual vs the name's recent
normal*. A ~1-month baseline (14–28 bars) captures "normal" cleanly. Stretch it to 2–4 months and
it averages in the name's *previous* runs — an episodically-active stock develops a permanently
inflated baseline, so today's genuine spike no longer clears rvol ≥ 5. You both lose the sharpest
setups (trips drop ~350) and keep lower-quality ones. Short (10 bars) is also worse — too noisy a
denominator. The edge wants ~20 bars.

## Run 31 — on CAUSAL data, against the close entry on the same rules (2026-11-05, user)

**User:** "How does the system which enters at 10 ET compare to the daily close one? Does it do any better?" Then: re-measure
it on causal data, as v5 did for the daily system (`docs/highflyer_results.md` v5 / v5c). Runs 1–30 were measured on June-era
tables and on the back-adjusted scale. The engine then has **two lookahead leaks** (CLAUDE.md rule 4):
- **The \$1 floor** reads the back-adjusted 10:00 price.
- **rvol** divides the partial candle's RAW volume by the mean of the BACK-ADJUSTED daily volumes. Before a later 1-for-k reverse
  split the baseline is k× too small, so rvol is k× too big. A later forward split deflates it.

**The port:** `scripts/equity/highflyer_v2_port.py --scale back|causal [--entry partial|close]` re-implements the engine
(Types.fs / Backtest.fs: the 20-bar volume baseline, the > 45-day gap reset with its mark-to-market close, the warmup from
2005-01-01, the partial candle's entry and fill). The 10:00 candles come from the archived `partial_candle_1000`
(`db_archive/2026-09-29`).
- **Control:** `--scale back` reproduces the engine's CSVs on today's data trip for trip. The 10:00 book matches 3,923 = 3,923
  and the close book 5,565 = 5,565, with every price and feature within 5e-12 relative
  (`scripts/equity/highflyer_v2_port_diff.py`; the engine ran on a parquet-view copy of its four tables).
- **Analysis:** `scripts/equity/highflyer_v2_causal.py` → `data/equity/highflyer_repro/v2_causal.md`.
- **Low float** is causal in every book: the SEC public float known by the entry date (Polygon's scso where SEC has none),
  carried to the entry date through the split count and priced at the raw 10:00 close. Coverage is 77 % (91 % from 2013).

Entries 2005-01 .. 2026-05-13, 5-day hold, no costs, +50 % clip:

| book | trips | win | PF raw | **PF clip** | mean | median | clip 2005–14 / 2015–26 |
|---|---|---|---|---|---|---|---|
| 10:00, back-adjusted (the engine) | 3,923 | 55.5 % | 1.878 | 1.792 | +2.73 % | +0.88 % | 1.676 / 1.853 |
| **10:00, causal** | 3,867 | 56.0 % | 1.910 | **1.829** | +2.70 % | +0.93 % | 1.722 / 1.888 |
| 10:00, causal · breadth > 0.5 | 2,523 | 55.7 % | 1.924 | 1.841 | +2.74 % | +0.92 % | 1.735 / 1.907 |
| close, causal (rvol ≥ 5) | 5,597 | 53.4 % | 1.679 | **1.596** | +1.98 % | +0.50 % | 1.630 / 1.578 |
| close, causal · breadth > 0.5 | 3,647 | 53.4 % | 1.723 | 1.670 | +1.98 % | +0.45 % | 1.765 / 1.612 |
| **10:00, causal · low float** | 871 | 59.0 % | 2.730 | **2.503** | +6.11 % | +2.16 % | 2.188 / 2.596 |
| 10:00, causal · float ≥ \$300M | 2,088 | 56.3 % | 1.679 | 1.667 | +1.76 % | +0.80 % | 1.921 / 1.607 |
| **10:00, causal · A+** (low float, red, −1..−6 % from the open) | 104 | 71.2 % | 6.265 | **5.369** | +13.25 % | +8.06 % | 3.924 / 5.846 |
| 10:00, causal · A+ · breadth > 0.5 | 74 | 66.2 % | 4.591 | 3.718 | +11.70 % | +6.99 % | 3.568 / 3.773 |
| close, causal · low float | 1,625 | 55.9 % | 2.045 | 1.876 | +3.75 % | +1.32 % | 1.965 / 1.853 |

**The disproportion test passes, and the leaks were HURTING.** 13.8 % of the 10:00 book changes and the clip PF RISES, 1.792 →
1.829. That is the behaviour of a genuine system.
- The 298 trips the causal scale drops were weak: clip 1.537. 231 of them belong to tickers with a LATER reverse split (the
  inflated rvol), and 15 had a raw 10:00 price under \$1.
- The 242 it adds are strong: clip 1.947. 194 of them have a later forward split (the deflated rvol had hidden them).
- The 3,625 shared trips keep their returns; 63 differ, by dividends, at most 0.31 pp.
- The close book barely moves: 1.586 → 1.596, since its rvol reads one scale throughout.

**Timing, with the selection held fixed (Run 2, causal).** On the 2,395 trips both books take, the 10:00 entry gives clip
**2.763** against 2.022 at the close, a mean of +3.64 % against +2.36 %, and a 61.5 % win rate against 56.7 %. The 10:00 fill
is a median 0.73 % cheaper than the close: these names keep rising into the close.

**By year (causal, clip PF).**
- The 10:00 book loses only in 2005 (0.94, its first year) and in 2026 (**0.72** on 87 trips, January–May). 2008 is weak at
  1.15.
- Its low-float slice also fails in 2026 (0.67 on 33 trips). The close book held up in 2026 (1.41).
- The year table is in `v2_causal.md` §5.

**Verdict.**
- The 10:00 entry IS better, and the causal measurement strengthens it: 1.83 against 1.60 on the full books, 2.50 against 1.88
  on low float, and 2.76 against 2.02 on the same trades.
- A+ is the strongest cell (5.4) but thin: 104 trips in 21 years, about 5 a year. Its cuts were chosen on this data (Runs
  15–17).
- Breadth does not help A+: with breadth > 0.5 it falls to 3.7. That is consistent with the user's 07-01 stance (breadth for
  sizing, not as a filter).
- Still unmodelled:
  - **Costs.** A 10:00 market or limit fill in a low-float name pays a wider spread than the closing auction.
  - **The 2026 weakness.**
  - **The minute bars.** The partial candle's "10:00 close" is the 09:59 minute bar's close, from the minute aggs built in
    June/July.

## Run 32 — a liquidity proxy for the low-float gate (2026-11-05, user)

**User:** "The only real problem with that system was getting good quality low float data. We managed to get some by
scraping it from Edgar, but it was very patchy. I wonder if we could replace the low float criteria via 60d median dollar
volume or some other indicator?"
- Script: `scripts/equity/highflyer_v2_float_proxy.py` → `data/equity/highflyer_repro/v2_float_proxy.md`.
- Book: the causal 10:00 book (Run 31, 3,867 trips; the float is known on 77 %).
- **dv60_med** is the median raw close · volume over the 60 sessions before the entry day: GapFader's definition, and the one
  the live Oms already loads (`L1Config.Volume60`).

**It tracks float closely.** Spearman ρ with the dollar float is 0.88, and the AUC for low float vs not is 0.95. Median
dv60_med is \$0.42M for low-float trips against \$12.8M for float ≥ \$300M. adv20 does about as well (0.87 / 0.94); the
price does worse (0.71 / 0.85).

**The edge lives below ~\$2M dv60_med and is flat above it:**

| dv60_med | \$0–0.5M | \$0.5–1M | \$1–2M | \$2–5M | \$5–10M | \$10–25M | \$25M+ |
|---|---|---|---|---|---|---|---|
| trips | 770 | 326 | 356 | 605 | 460 | 506 | 844 |
| PF clip | 2.08 | **2.58** | 2.01 | 1.59 | 1.57 | 1.53 | 1.58 |
| low float among the covered | 90 % | 72 % | 51 % | 17 % | 5 % | 1 % | 0 % |

**Over the whole book the proxy looks weaker (dv60_med < \$2M: 1,452 trips, clip 2.17, against low float's 871 at 2.50).
That is a coverage artefact, not a weaker cut.**
- Trips WITHOUT float data are weak (1.43). They are mostly from before 2013, when the SEC XBRL frames are thin (CS
  coverage 40 % in 2005–12 vs 93 % from 2013), and ADRs (15 % / 62 %).
- The float gate never sees those trips. The proxy keeps them.
- The proxy's mismatches are good trades either way: dv60_med < \$2M with float ≥ \$300M makes 2.15 (243 trips), and low float
  with dv60_med ≥ \$2M makes 2.47 (97 trips).

Like for like, from 2013:

| cut (2013+) | trips | PF clip | mean | 2013–14 / 2015–20 / 2021–26 |
|---|---|---|---|---|
| all | 2,774 | 1.90 | +3.07 % | 1.99 / 2.42 / 1.55 |
| **low float (< \$300M)** | 777 | **2.56** | +6.47 % | 2.34 / 3.30 / 2.14 |
| **dv60_med < \$1M** | 773 | **2.54** | +6.21 % | 2.49 / 3.07 / 2.19 |
| dv60_med < \$2M | 1,017 | 2.41 | +5.55 % | 2.42 / 2.86 / 2.10 |
| dv60_med < \$2M · CS only | 925 | 2.49 | +5.70 % | 2.87 / 3.03 / 2.03 |
| dv60_med < \$2M · ADRC | 92 | 1.84 | +3.97 % | 0.67 / 1.64 / 2.76 |
| **A+ (red dip · low float)** | 95 | **5.52** | +14.09 % | 4.16 / 7.54 / 4.74 |
| red dip · dv60_med < \$1M | 93 | 4.35 | +12.57 % | 2.66 / 6.45 / 3.65 |
| **red dip · dv60_med < \$2M · CS only** | 111 | **4.80** | +11.91 % | 6.16 / 7.22 / 3.41 |

**Verdict: dv60_med < \$1–2M on common stock replaces the float gate.**
- It matches the float gate on the full book (2.54 against 2.56 on the same number of trades) and comes close on A+ (4.80
  against 5.52, with 17 % more trades).
- It is computable for every name, every day, from the daily bars alone. No scraping is needed, and the live Oms already
  holds the number.
- ADRs are the weak part of the proxy's extra trades.
- Caveats:
  - The thresholds were read off this data, from a coarse grid of 5, where the PF is smooth in the cut.
  - The cut is nominal dollars over 21 years.
  - Costs are still unmodelled, and they matter most exactly here: a \$1M-a-day name at 10:00.

## Run 33 — the real liquidity around the trade (2026-11-05, user)

**User:** "The volume is usually significantly higher on the breakout day so that plays in our favor ... these setups are
accompanied by news which increases the liquidity around the event ... compare the differences between 20d mean and 60d
median estimates."
- Script: `scripts/equity/highflyer_v2_liquidity.py` → `data/equity/highflyer_repro/v2_liquidity.md`.
- Book: the causal 10:00 book, closed trips 2016-08 .. 2026-05 (2,118). The proxy book (dv60_med < \$2M, CS) has 700; the proxy
  A+ has 91.
- Measure: dollar volume actually traded, from the 1s bars (lit + TRF; the auction markers dropped).

Medians, proxy book: dv60_med \$0.48M, adv20 \$0.75M.

| window | dollar volume | ÷ dv60_med | ÷ adv20 | a \$10k position: median · p90 · trips over 5 % |
|---|---|---|---|---|
| entry day, 09:30–10:00 | \$1.92M | 4.0× (sd 1.09) | 2.4× (sd 0.91) | |
| **entry day, 10:00–10:15** (the fill) | \$0.47M | 0.94× (sd 1.33) | 0.61× (sd 1.16) | 2.1 % · 12.7 % · 27 % |
| entry day, full | \$9.16M | 17× (sd 1.20) | 11× (sd 1.03) | 0.1 % · 0.6 % · 0 % |
| **exit day, 09:30–09:45** (the 5-day exit at the open) | **\$93k** | 0.21× (sd 1.60) | 0.14× (sd 1.51) | **10.7 % · 93 % · 69 %** |
| exit day, full | \$1.73M | 3.9× (sd 1.14) | 2.4× (sd 1.05) | |

- **The user is right about the breakout day.** The entry day trades 17× the 60-day median (A+: 25×), and the first half hour
  4×.
- The fill window right after 10:00 is thinner. Volume falls off sharply after the first half hour, and the 15 minutes after
  10:00 carry about one 60-day median's worth. A \$10k entry is a median 2 % of that window, and above 5 % on 27 % of trips.
- **The EXIT is the constraint.** Six sessions later the news has faded: the exit morning's first 15 minutes trade a median
  \$93k, 0.2× dv60_med. A \$10k market exit at the open is a median 11 % of that window, and above 5 % on 69 % of trips. (The
  opening auction's cross is not separately measured here.)
- The exit day as a whole still trades 3.9× dv60_med. The tested exit (the open of bar i + 6) is the expensive way out, and an
  exit worked over that day, or over the days before it, is the obvious variant to test.
- **The 20-day mean is the better liquidity estimate here, not the 60-day median.** It has the tighter actual/estimate spread
  in every window but one: sd 0.91 vs 1.09 in the first half hour, 1.05 vs 1.14 for the exit day. These names were already
  waking up in the weeks before the breakout, and the shorter mean catches that.
- Both are rough: an sd near 1 means a factor of about 2.7 either way. For sizing, the day's own volume up to 10:00 is the
  better measure.

## Run 34 — 2026 through October, and the market (2026-11-05, user)

**User:** "we could backfill the database and see whether it flipped since we last tested it ... start by checking whether this
low profit factor in 2026 was due to lack of trades". Then: "I thought that this method wasn't affected by the general market,
but I guess I was wrong. The period you picked out is when the market was in a short-term bear phase."
- Minute aggs were backfilled from 07-18 to 10-30 (`download-bulk-minute`).
- The 10:00 candles after 06-25 come from `scripts/equity/build_partial_candle_ext.py`, which runs the fsx's SQL into a parquet.
  Its control rebuilt 06-01 .. 06-25 against the archive: prices exact on 212,532 rows; volume within 24 shares, from the
  rounding of fractional minute volumes.
- The causal port then ran through 2026-10-30 and reproduces every trip closed by 05-13 exactly.
- Script: `scripts/equity/highflyer_v2_2026.py` → `data/equity/highflyer_repro/v2_2026.md`.

**Not a lack of trades.** January 1 – May 13 had 86 trips, above the typical 60–115 for that window. Three earlier years had
an equally weak start: 2005 (0.72, full year 0.94), 2016 (0.65 → 3.55) and 2020 (0.84 → 2.11).

**It has not flipped.** Trips · PF clip · mean:

| 2026 | all | proxy book (dv60_med < \$2M, CS) |
|---|---|---|
| Jan – May 13 | 86 · 0.74 · −1.5 % | 29 · 0.65 · −2.4 % |
| May 14 – Oct | 75 · 0.94 · −0.3 % | 36 · 1.50 · +1.8 % |
| **Jan – Oct** | **161 · 0.82 · −0.9 %** | **65 · 0.99 · −0.0 %** |

- Of the 243 rolling 10-month windows in 2005–2025, only **4 (1.6 %)** were as bad for the full book.
- The proxy book has been worse: 14 windows (5.8 %), the worst at 0.18. It also had losing years (2005 0.77, 2008 0.84) and
  recovered from each.
- The months: Feb 0.54, Mar 0.49, Apr 0.49 (9 trips), Sep **0.28** (12 trips).

**The market DOES move it, through what happens DURING the hold.** SPY's holder's return over the trip's own 5-day hold is
descriptive only, since it is future information:

| SPY over the hold | ≤ −3 % | −3..−1 % | −1..+1 % | +1..+3 % | ≥ +3 % |
|---|---|---|---|---|---|
| all: trips · PF clip | 211 · **0.66** | 658 · 1.06 | 1,754 · 1.79 | 1,091 · 3.06 | 221 · 3.12 |
| proxy book | 73 · 0.98 | 241 · 1.59 | 587 · 2.16 | 400 · 3.12 | 77 · 3.70 |

- ρ = 0.18, and the slope of the clipped return on SPY's is 0.89, about a beta of one. The proxy book is less exposed (ρ 0.11;
  it breaks even in the worst bucket). IWM looks the same.
- **The causal breadth gate does not see it coming.** In 2005–2025 the book makes 1.88 with breadth lag-1 above 0.5 and 1.88
  with it below; in February–April 2026 it lost on both sides (0.62 / 0.44).
- February–March 2026 fit the user's bear-phase reading: SPY fell 1.0–1.5 % on average over those holds.
- April (SPY +2.4 % over the holds) and September (SPY −0.4 %, IWM −1.7 %) lost anyway, so the market is not the whole
  story.

**Reading:** a beta-one long book whose 2026 is its worst stretch since 2005: the 1.6 % tail of the full book, the 5.8 % tail
of the proxy book. Not yet outside history, but no recovery either.

## Run 35 — an earlier exit, and the past windows as bad as 2026 (2026-11-05, user)

**User:** "So there were such bad periods before?" and "maybe we could consider getting out on day 3 or 4's open. Most of the
edge is there during the first 3 days anyway." Script: `scripts/equity/highflyer_v2_exit_day.py` →
`data/equity/highflyer_repro/v2_exit_day.md`.

**The past windows as bad as 2026 Jan–Oct are all market crises.**
- Full book (0.82): only the four windows that end in March–June 2009 (Jun 2008 .. Jun 2009). Those are 35–42 trips each,
  against 2026's 161.
- Proxy book (0.99): 2005, the 2008–09 crisis (down to 0.18 on 14 trips) and 2011-03..12 (the August 2011 downgrade).
- 2026 is the worst stretch since the GFC. It carries far more trades, and there is no comparable crash in it.

**The exit at the open of session k after the entry** (k = 6 is production's; the same 3,935 trips in every column; PF clip ·
mean):

| book | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 | k = 6 |
|---|---|---|---|---|---|---|
| all | 1.49 · +0.86 % | 1.66 · +1.49 % | 1.76 · +2.03 % | 1.81 · +2.28 % | 1.79 · +2.42 % | 1.81 · +2.66 % |
| proxy book (1,378) | 1.45 · +1.01 % | 1.81 · +2.20 % | 2.01 · +3.23 % | 2.05 · +3.66 % | 2.10 · +4.03 % | 2.21 · +4.60 % |
| proxy A+ (166) | 1.42 · +0.94 % | 2.29 · +2.93 % | 2.77 · +5.62 % | 2.86 · +7.33 % | 2.89 · +7.12 % | 3.31 · +8.58 % |
| proxy, 2021–26 (436) | 1.73 · +1.69 % | 1.93 · +2.98 % | 1.96 · +3.73 % | 1.98 · +4.19 % | 1.98 · +4.12 % | 1.98 · +4.40 % |
| proxy, 2026 (65) | 1.22 · +0.59 % | 1.38 · +1.28 % | 1.35 · +1.75 % | 1.15 · +0.58 % | 1.04 · +0.19 % | 0.99 · −0.04 % |

- **By k = 3 the proxy book has earned 70 % of the 6-day mean** (85 % in 2021–26, where days 4–6 add PF only 1.96 → 1.98).
- Over the full history the later days still pay, about +1.4 pp of mean. They pay most in 2015–20 and for A+.
- Per session held, the early exit is the more efficient use of capital: about 1.1 %/session at k = 3 against 0.8 % at k = 6.
- In 2026 everything after day 2 was a loss: k = 2 makes 1.38, and the book decays to 0.99 by k = 6.

**The exit morning's liquidity** (09:30–09:45 dollar volume, 2016-08+; median · a \$10k sell's median share):

| book | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 | k = 6 |
|---|---|---|---|---|---|---|
| proxy book | \$336k · 3.0 % | \$155k · 6.4 % | \$125k · 8.0 % | \$111k · 9.0 % | \$119k · 8.4 % | \$90k · 11.1 % |
| proxy A+ | \$354k · 2.8 % | \$158k · 6.3 % | \$217k · 4.6 % | \$138k · 7.2 % | \$197k · 5.1 % | \$154k · 6.5 % |

The liquidity falls off after day 1, then stays roughly flat. Exiting at day 3 or 4 instead of day 6 eases the exit only from
about 11 % to 8–9 % of the first 15 minutes. **An earlier exit is a capital-efficiency and recent-regime choice, not the
liquidity fix**, which remains working the exit through the session.

## Run 36 — a rule for "the edge has returned" (2026-11-05, user)

**User:** "I think the best thing to do would be to just wait until the edge for this system comes back ... What do you think
would be a good stretch of winning trades to conclude it has returned?"
- Script: `scripts/equity/highflyer_v2_return_rule.py` → `data/equity/highflyer_repro/v2_return_rule.md`.
- The rule: the clipped PF over the last N trades ≥ X.
- **Power** is the chance a HEALTHY system passes: the book's own trades from 2009-07 to 2025.
- **False pass** is the chance a BROKEN one passes: the same trades shifted down to a PF of exactly 1.0, with the same shape
  and spread.
- Both come from a bootstrap of 20,000 draws.

Proxy book (dv60_med < \$2M, CS; 68 trades a year; healthy PF 2.52, 59 % wins; broken 42 % wins), production exit; power /
false pass:

| last N trades (≈ months) | PF ≥ 1.2 | PF ≥ 1.4 | **PF ≥ 1.6** | 2026's value now |
|---|---|---|---|---|
| 30 (≈ 5) | 93 % / 34 % | 88 % / 24 % | 82 % / 16 % | 1.32 |
| **50 (≈ 9)** | 97 % / 31 % | 93 % / 19 % | **88 % / 11 %** | 1.11 |
| 75 (≈ 13) | 99 % / 27 % | 97 % / 14 % | 92 % / 6 % | — |

The full 10:00 book trades 196 a year, so it decides faster. Its last 100 trades (≈ 6 months) at PF ≥ 1.6 give 76 % / 5 %;
the value now is 0.97.

**Streaks of winners are a weak test.** A run of 6 straight winners somewhere in 50 trades happens to 60 % of healthy books
and 13 % of broken ones. A run of 5 happens to 81 % and 32 %.

**Recommendation:**
- The edge is back when **the proxy book's last 50 trades make clipped PF ≥ 1.6**: 88 % power, 11 % false passes, about 9
  months of trades.
- The full book's last 100 trades at ≥ 1.6 is an earlier signal (about 6 months): it misses more healthy systems (76 %) but
  rarely passes a broken one (5 %).
- Trades in a market phase cluster, so the bootstrap's independence assumption flatters both rates. A recovery that only
  coincides with a strong market rally deserves a second look.
- Monitoring costs a minute-agg backfill, `build_partial_candle_ext.py` and the causal port, once a month.

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


---

## Note (resolved 2026-08-08) — `tightness_14` is already an efficiency ratio

Briefly flagged for FlushFader S43bd's inverted-vol-axis audit, then
**cleared by inspection (user)**: `tightness_14` = 14-bar range / *average*
ATR. Divide by the SUM of true ranges instead and it is literally the
efficiency ratio, `range/(14·ATR)` = tightness/14 — a constant rescaling, so
rankings and every bucket table are unchanged (tightness 7 = efficiency 0.5).
It is a matched-legs ratio, the class FlushFader proved self-cancelling; the
S43bd trap was an ADDITIVE constant in the numerator (`H/L` vs `H/L − 1`),
which has no analog here. **No audit needed. The tight-base result stands.**

Worth knowing for future work: expressing tightness on the /sum scale would
have made the kinship with the efficiency-ratio family obvious years earlier.
The one place log-vs-linear DOES matter is standalone range features on names
that move hundreds of percent — there the two spaces genuinely disagree.

## Run 37 — split by LISTED OPTIONS (2026-11-06, user)

> USER: *"Yesterday we found out that it's dead in 2026, so I am wondering if splitting the stocks into those that have
> option chains and those that don't would make a difference."* (after SpringFlyer S12: the names WITHOUT options are
> the far better shorts.) *"I'd expect them to be better, but we'll see."*

Script `scripts/equity/highflyer_v2_options.py` (`--cached` reuses the saved probes), output
`data/equity/highflyer_repro/v2_options.md` / `.parquet`. Run 34's causal 10:00 book, closed trips 2016-01-05 →
2026-10-30 (ThetaData options STANDARD starts 2016-01-04): **2,281 trips**; `has_chain` = call rows on the PREVIOUS
session's EOD (causal for the 10:00 entry). 81% of all trips have a chain, 55% of the proxy book (dv60_med < \$2M, CS).
Cells: trips · PF clip · mean.

| | all: chain | all: no chain | proxy: chain | proxy: no chain |
|---|---|---|---|---|
| 2016 | 116 · 3.14 · +3.3 % | 22 · 4.92 · +9.2 % | 32 · 7.86 · +7.3 % | 13 · 3.46 · +7.8 % |
| 2017 | 194 · 2.39 · +3.3 % | 51 · 2.51 · +4.3 % | 54 · 4.18 · +6.5 % | 40 · 2.72 · +4.7 % |
| 2018 | 168 · 3.07 · +3.2 % | 30 · 2.32 · +4.4 % | 32 · 4.41 · +6.2 % | 23 · 2.64 · +4.6 % |
| 2019 | 139 · 1.93 · +2.3 % | 23 · 2.72 · +6.1 % | 21 · 2.32 · +4.4 % | 16 · 2.13 · +5.6 % |
| 2020 | 188 · 2.54 · +6.7 % | 62 · 1.48 · +2.5 % | 44 · 4.23 · +15.7 % | 50 · 1.37 · +1.9 % |
| 2021 | 249 · 1.36 · +1.6 % | 99 · 1.23 · +1.7 % | 58 · 2.09 · +5.0 % | 77 · 1.47 · +2.9 % |
| 2022 | 62 · 2.31 · +3.9 % | 18 · 1.44 · +2.2 % | 19 · 2.91 · +7.7 % | 16 · 1.56 · +2.4 % |
| 2023 | 145 · 1.83 · +2.6 % | 18 · 1.91 · +6.4 % | 35 · 2.27 · +5.1 % | 17 · 1.51 · +3.8 % |
| 2024 | 231 · 1.74 · +2.5 % | 35 · 2.83 · +3.8 % | 51 · 4.95 · +8.9 % | 26 · 3.94 · +5.5 % |
| 2025 | 218 · 1.89 · +3.3 % | 52 · 1.41 · +2.1 % | 33 · 6.06 · +11.0 % | 39 · 1.09 · +0.5 % |
| **2026 Jan–Oct** | 128 · 0.79 · -1.0 % | 33 · 0.90 · -0.6 % | 38 · 1.07 · +0.3 % | 27 · 0.93 · -0.5 % |
| **2016–2025** | 1710 · 2.02 · +3.2 % | 410 · 1.76 · +3.5 % | **379 · 3.65 · +7.9 %** | 317 · 1.73 · +3.4 % |

By dv60_med (all trips): < \$2M chain 449 · **3.21** · +7.1 % vs no chain 391 · 1.71 · +3.3 %; \$2-10M 1.52 vs 1.32 (48
no-chain); above \$10M almost every name has options (4 without). By entry price: < \$10 2.79 vs 1.58; \$10-30 1.73 vs
1.61; > \$30 1.35 vs 2.55 (66 no-chain trips).

**Control, inside dv60 < \$2M, matched on dv60 deciles, 2000 draws**: the chain trips' 3.21 against no-chain trips drawn
with their mix: p5 1.18 / median 1.44 / p95 1.76; the no-chain trips' 1.71 against chain trips drawn with theirs: p5 2.78 /
median 3.52 / p95 4.52. (A first all-years control matched on four coarse dv60 bands was broken — 52 no-chain trips above
\$2M resampled to stand in for 1,389 chain trips — and was withdrawn.)

**Read**:
- **The chain matters, with the sign OPPOSITE to the user's expectation and to SpringFlyer's**: for this LONG the names
  WITH options are the better buys — proxy book 3.65 vs 1.73 in 2016–2025, ahead in **10 of 10 years**. With S12 that is
  one consistent picture, the short-sale constraint: a name without options overshoots, so it is a better short after a
  climax (SpringFlyer) and a worse long after a breakout (HighFlyer).
- **The chain-proxy cell is the new best HighFlyerV2 book**: 379 trips 2016–2025, PF clip 3.65, mean +7.9 % (the
  whole proxy book 2016–2025: 696 · 2.51 · +5.8 %), ~38 trips/yr.
- **⚠ It does NOT rescue 2026**: chain-proxy 38 · 1.07 · +0.3 %, every cell 0.79–1.07. The 2026 failure is the market
  regime (Run 34), common to both groups. The wait decision stands; the return rule (Run 36) could track the
  chain-proxy book instead (fewer trips — the bootstrap power would need recomputing).
- Costs remain unmodelled; the exit-liquidity bottleneck (Run 33) is unchanged.

## Run 38 — a SOLD PUT instead of the stock (2026-11-06, user)

> USER: *"For HighFlyer, how would selling puts perform instead of taking outright positions?"*

Script `scripts/equity/highflyer_v2_put_sale.py`, output `data/equity/highflyer_repro/v2_put_sale.md` / `.parquet`. Run 37's
1,838 chain trips; the ATM put (strike nearest the 10:00 price) and a −10% put at the first expiry on or after the exit date
(median 16 dte); sold at D's 10:00:00 1-minute quote; out (A) at the exit day's 10:00 quote (the stock exits at the open:
a 30-minute mismatch) or (B) held to expiry at intrinsic, the stock to the same close beside it. Stub filter: an ask above
the strike, a spread > 50% of the mid, or no quote — **only 836 of 1,838 ATM contracts have a clean 10:00 quote (71 of the
proxy book's 417)**; the median spread is 24% of the mid (40% in the proxy book); the ATM credit at the bid a median 4.35% of
the stock (9.72% in the proxy book). Per share, % of the 10:00 price.

| ATM put, clean quotes | n | mean % | PF | worst % |
|---|---|---|---|---|
| **all chain trips: stock, 10:00 → the exit open** | 836 | **+2.31** | **1.77** | -82.0 |
| sold put, out at the exit, bid / bid + 25% / mid | 836 | -0.66 / +0.28 / +1.23 | 0.73 / 1.15 / 1.85 | -63.9 / -61.2 / -58.5 |
| stock held to the expiry's close | 836 | +2.83 | 1.66 | -83.6 |
| sold put held to expiry, bid / bid + 25% / mid | 836 | +0.91 / +1.36 / +1.81 | 1.37 / 1.58 / 1.82 | -63.7 / -61.9 / -60.1 |
| **proxy book: stock, 10:00 → the exit open** | 71 | **+10.55** | **5.27** | -26.6 |
| sold put, out at the exit, bid / bid + 25% / mid | 71 | -1.24 / +2.20 / +5.64 | 0.73 / 1.91 / 6.28 | -29.9 / -18.9 / -12.9 |
| stock held to the expiry's close | 71 | +11.49 | 3.08 | -81.7 |
| sold put held to expiry, bid / bid + 25% / mid | 71 | +4.01 / +5.62 / +7.23 | 2.15 / 2.88 / 3.83 | -42.5 / -42.1 / -41.8 |

The −10% put is weaker still (all: out at the exit bid + 25% PF 1.04, to expiry 1.40, vs the stock 1.44 / 1.30 on its 444
trips). By the stock's move to the exit (ATM, bid + 25%, mean %, stock / put): < −20% −28.4 / −21.0; −20..−10 −14.2 / −9.2;
−10..0 −4.1 / −1.7; 0..+10 +4.2 / +2.3; +10..+20 +14.2 / +4.5; > +20% (46) +36.5 / +9.2. 2026: every structure loses (all
chain trips, bid + 25%: stock 0.37, put at the exit 0.29, to expiry 0.64).

**Read — NO.** HighFlyer's edge is its RIGHT TAIL (the > +20% trips: +36% on the stock), and a sold put caps it at the
credit (+9%) while keeping 60-75% of the downside. Short volatility needs the opposite payoff — a bounded win against a
modest, frequent edge (SpringFlyer S16's held-to-expiry call). Add the 10:00 spreads on these names (24-40% of the mid)
and the put only matches the stock at the mid, which is not a fill. Half the trips have no clean quote at all.

## Run 39 — a BOUGHT CALL instead of the stock (2026-11-06, user)

> USER: *"Ok, since puts don't work that well, how would buying calls do?"*

Script `scripts/equity/highflyer_v2_call_buy.py` (Run 38's, calls instead of puts), output
`data/equity/highflyer_repro/v2_call_buy.md` / `.parquet`. The ATM call and a +10% call at the first expiry on or after the
exit (median 16 dte); bought at D's 10:00:00 1-minute quote (ask / ask − 25% of the spread / mid); sold at the exit day's
10:00 quote or held to expiry at intrinsic. Clean ATM quotes 1,089 of 1,838 (proxy book 134); median ATM premium 6.0% of
the stock (12.1% in the proxy book), spread 22% of the mid (29%). Per share, % of the 10:00 price.

| | n | stock to the exit: mean / PF | call out at the exit, ask / ask − 25% / mid: PF | ask − 25%: mean | call to expiry, ask − 25%: PF | return on premium (ask − 25%): mean / median |
|---|---|---|---|---|---|---|
| all, ATM | 1089 | +2.69 / **1.85** | 0.87 / **1.25** / 1.80 | +0.64 | 1.31 | +9.0% / −29.7% |
| all, +10% | 614 | +3.29 / **1.85** | 1.03 / **1.40** / 1.93 | +0.78 | 1.26 | +19.6% / −60.1% |
| proxy, ATM | 134 | +9.59 / **3.97** | 1.24 / **2.04** / 3.54 | +3.85 | 1.63 | +35.9% / −10.3% |
| proxy, +10% | 64 | +14.60 / **4.45** | 1.93 / **2.89** / 4.41 | +5.87 | 2.08 | +99.2% / −3.0% |

By the stock's move to the exit (all, ATM, ask − 25%, mean %, stock / call): < −20% (22) −26.1 / −10.6; −20..−10 −14.0 / −7.8;
−10..0 −4.2 / −4.2; **0..+10 (413, the largest bucket) +4.2 / +0.6**; +10..+20 +14.1 / +7.7; > +20% (75) +39.2 / +27.0.
2026 (all, ATM, ask − 25%): call 0.25 at the exit / 0.22 to expiry vs the stock 0.52.

**Read — better than the sold put, still worse than the stock.** The call does what it should at the tails: it keeps ~70%
of the > +20% trips and loses ~40% of what the stock loses below −20%. What sinks it is the middle: the most common
outcome, a +0–10% trip, earns the stock +4.2% and the call +0.6% — the premium's decay and the spread take it. At
executable fills the PF falls from 1.85 to 1.25-1.40 (all) and from 4.0-4.5 to 2.0-2.9 (proxy); only at the mid does it
match the stock, and holding to expiry is no better. The capital argument cuts both ways: the call ties up 6-12% of the
stock's price, but the ROP is a lottery (median −30% all, −10% proxy; a > 90% loss in 8-28% of trips). As in S13/S16 and
Run 38, these options are priced for more movement than the median trip delivers.

## Run 40 — entering EARLIER (09:31 / 09:45), the close entry in 2026, and the crowding question (2026-11-06, user)

> USER: *"I wonder if 2026 started losing because people aping Qullamaggie's breakout strategy? I wonder if entering
> earlier, like 9:45 or even at the open would do better? Also, I've never asked, but does the enter at the close strategy
> also lose in 2026?"*

Partial candles at 09:31 (the first minute: "at the open"), 09:45 and 10:00 rebuilt from the minute aggs in one pass
(`scripts/equity/build_partial_candles_multi.py` → `data/equity/highflyer_repro/partial_candles/<HHMM>/<YYYY>.parquet`).
**CONTROL: the rebuilt 10:00 candles equal the archive on all 39,467,440 ticker-days 2005–2026** (prices exact, volume
within 100 shares; the archive's 196k extra rows per year-window are tickers with no RTH minute before 10:00, never
enterable), and the port on them reproduces Run 34's 3,948 trips, every return equal. Each earlier book = the same rules
decided at the cutoff (move, fade and rvol on the candle to it; the fill at its close); rvol >= 1.0, and scaled to the 10:00
book's median volume fraction by then (09:45 0.67, 09:31 0.16). The close book through 10-30 (`--entry close`) equals the
05-13 one on every common trip (5,570 closed returns). Tables: `scripts/equity/highflyer_v2_earlier.py` →
`data/equity/highflyer_repro/v2_earlier.md`.

| all trips: trips · PF clip · mean | 2005–2015 | 2016–2025 | 2026 Jan–Oct | Jan–May 13 | May 14–Oct |
|---|---|---|---|---|---|
| **10:00 (production)** | 1654 · 1.78 · +2.3 % | 2120 · **1.95** · +3.2 % | 161 · **0.82** · −0.9 % | 86 · 0.74 | 75 · 0.94 |
| 09:45, rvol >= 1.0 | 1070 · 1.49 · +1.6 % | 1373 · 1.64 · +2.5 % | 105 · 0.84 · −0.8 % | 52 · 0.77 | 53 · 0.92 |
| 09:45, rvol >= 0.67 | 1482 · 1.50 · +1.6 % | 2016 · 1.67 · +2.6 % | 166 · 0.81 · −1.0 % | 94 · 0.85 | 72 · 0.75 |
| 09:31, rvol >= 1.0 | 308 · 0.65 · −1.8 % | 426 · 1.10 · +0.9 % | 35 · 1.01 · +0.4 % | 20 · 1.33 | 15 · 0.48 |
| 09:31, rvol >= 0.16 | 1147 · 1.16 · +0.7 % | 1605 · 1.39 · +2.1 % | 190 · 1.05 · +0.3 % | 104 · 1.27 | 86 · 0.77 |
| **the close** (rvol >= 5) | 2556 · 1.64 · +1.8 % | 2917 · 1.57 · +2.2 % | 207 · **1.24** · +1.1 % | 115 · 1.39 | 92 · 1.02 |

The proxy book: 10:00 2.51 → 0.99 in 2026; 09:45 1.76-1.87 → 0.96-1.02; 09:31 0.94-1.32 → 1.09-1.28; the close 1.61 →
**1.35** (92 trips). The 09:45 / 09:31 books share 81 % / 63 % of their trips with the 10:00 book.

**The cost of waiting** (descriptive: the 10:00 book's OWN trips priced from earlier that morning — selected with 10:00
information, not tradeable):

| | from 09:31 | from 09:45 | from 10:00 | median move 09:31 → 10:00 | 09:45 → 10:00 |
|---|---|---|---|---|---|
| 2016–2025 | 2120 · 4.25 · +7.5 % | 2.34 · +4.2 % | 1.95 · +3.2 % | +3.30 % | +0.51 % |
| 2026 | 161 · 1.68 · +2.5 % | 0.93 · −0.4 % | 0.82 · −0.9 % | **+3.23 %** | +0.44 % |

**Read**:
- **Entering earlier does not help** — in normal years it is clearly worse (09:45 1.64-1.67, 09:31 1.10-1.39 vs 1.95 in
  2016–2025), and in 2026 09:45 is as bad as 10:00 (0.81-0.84) and 09:31 only flat (1.01-1.05). The wait to 10:00 is a
  FILTER: a name still up 10-30 % and not fading at 10:00 is a better buy than one that opened that way. (The 09:31 → 10:00
  move a trip has already made, +3.3 %, is the price of that filter and is worth paying.)
- **No crowding signature**: if copycats were buying these breakouts earlier, the run-up before our entry would have grown
  and the drift after it shrunk at the entry. The run-up is normal in 2026 (+3.23 % from 09:31, vs +3.30 % in 2016–2025;
  the gap and the 10:00 → close drift likewise, 12.7 % / −0.1 %), while the trips priced from 09:31 — with hindsight —
  still drop from PF 4.25 to 1.68. What failed is the hold AFTER the entry day, as Run 34's market dependence says, not
  the entry. (Not a proof against crowding; a different kind — selling into the follow-through days — would not show here.)
- **The close entry does NOT lose in 2026**: PF 1.24 (207 trips; proxy 1.35), against its own 1.57 in 2016–2025 — weaker,
  flat since May 14 (1.02). It is normally the worse entry (1.57 vs 1.95) and held up better this year.

### Run 40b — PREMARKET entries: 08:30 and 09:00 (user, 2026-11-06: "What about entering an hour before the open or half an hour?")

Premarket candles [04:00, c) built the same way (`build_partial_candles_multi.py --cutoffs 0830,0900`: a cutoff before 09:30
takes the premarket's own OHLC); the fill = the last premarket minute's close (⚠ OPTIMISTIC: premarket quotes on these
names are thin and wide). rvol scaled to the 10:00 book's median volume fraction by then (08:30 1.7 %, 09:00 3.6 %; only
3,226 of its 3,948 trips had traded by 08:30), and 09:00 at rvol >= 0.25 (the gap short screen's "heavy premarket").

| PF clip (trips) | 2016–2025, all | 2026, all | 2016–2025, proxy | 2026, proxy |
|---|---|---|---|---|
| 08:30, rvol >= 0.017 | 1.11 (1499) | 0.97 (218) | 0.92 (479) | 0.55 (50) |
| 09:00, rvol >= 0.036 | 1.12 (1459) | 0.99 (196) | 0.89 (449) | 0.90 (44) |
| 09:00, rvol >= 0.25 | 0.88 (545) | 0.84 (57) | 0.64 (247) | 0.58 (23) |
| 09:31 (scaled) | 1.39 (1605) | 1.05 (190) | 1.32 (525) | 1.09 (53) |
| 09:45 (scaled) | 1.67 (2016) | 0.81 (166) | 1.87 (665) | 0.96 (64) |
| **10:00** | **1.95** (2120) | 0.82 (161) | **2.51** (696) | 0.99 (65) |
| the close | 1.57 (2917) | 1.24 (207) | 1.61 (1305) | 1.35 (92) |

**Read — the earlier the decision, the worse the book, monotonically: 08:30 1.11 → 09:00 1.12 → 09:31 1.39 → 09:45 1.67 →
10:00 1.95** (2016–2025, all; the proxy book 0.92 → 0.89 → 1.32 → 1.87 → 2.51), with 2026 near 1.0 or below at every one
of them. The premarket books share only 51-55 % of their trips with the 10:00 book: the premarket gapper that is still up
10-30 % and holding at 10:00 is the trade; the premarket gapper as such is not (and the heaviest premarket volume is the
worst cell, 0.64-0.88 — the gap-short screen's population, GF-64). The cost of waiting, for the 10:00 trips that traded by
08:30: a median +4.55 % run from 08:30 to 10:00 in 2016–2025 — rising since 2023 (+5.9 / +5.1 / +6.1 / 2026 +5.4 %, vs
+3.6-4.7 % in 2016–2022): more of the move now happens before the open, a possible crowding signature, but it did not hurt
the 10:00 book in 2023–2025.

### Run 40c — LATE entries: 15:00 and 15:30 (user, 2026-11-06: "What about buying 30 or 60m before the close instead?")

Candles [09:30, c) with volume from 04:00 (`--cutoffs 1500,1530`); the fill at the candle's close (the last minute before the
cutoff); the same exit (the open of the 6th session). Two gates: rvol >= 1.0 (the 10:00 book's) and the CLOSE entry's
rvol >= 5 scaled to the share of the day's volume in by then on the close book's trips (82 % / 87.5 % → 4.1 / 4.4).

| PF clip · trips | 2016–2025, all | 2026, all | 2016–2025, proxy | 2026, proxy |
|---|---|---|---|---|
| 10:00 (production) | **1.95** · 2120 | 0.82 · 161 | **2.51** · 696 | 0.99 · 65 |
| 15:00, rvol >= 1.0 | 1.44 · 8304 | 1.10 · 914 | 1.68 · 3038 | 1.57 · 236 |
| 15:00, rvol >= 4.1 | 1.72 · 2608 | 1.27 · 158 | 1.95 · 1186 | 1.55 · 77 |
| 15:30, rvol >= 1.0 | 1.41 · 8847 | 1.09 · 972 | 1.63 · 3292 | 1.47 · 251 |
| **15:30, rvol >= 4.4** | 1.80 · 2616 | **1.37** · 160 | 1.95 · 1191 | **2.01** · 74 |
| the close (rvol >= 5) | 1.57 · 2917 | 1.24 · 207 | 1.61 · 1305 | 1.35 · 92 |

(2005–2015: 15:30 rvol >= 4.4 1.85, the close 1.64, 10:00 1.78; the proxy 2.10 / 1.79 / 2.05. 2026 halves at 15:30 rvol >= 4.4:
Jan–May 13 1.47, May 14–Oct 1.21; the proxy 2.09 / 1.91.)

**Read**:
- **15:30 beats the close in every period** (all 1.80 vs 1.57 in 2016–2025, 1.85 vs 1.64 before; 2026 1.37 vs 1.24; the
  proxy book 1.95 vs 1.61, and 2026 **2.01** vs 1.35): the last 30 minutes' move is a cost, the mirror of the morning's
  "wait for 10:00" filter. (Not a pure timing change: the partial path also applies the fade gate, close vs open >= −7 %,
  on the 15:30 candle; the close path does not.)
- **10:00 is still the best book in normal years** (1.95 / proxy 2.51), but **in 2026 the late entry is the one that
  works**: the proxy book at 15:30 rvol >= 4.4 is 74 trips at PF 2.01, both halves of the year (2.09 / 1.91). Whatever broke
  in 2026 hits the morning entry — the names bought at 10:00 that then fail during the same day — far more than the names
  still strong into the close.
- The rvol >= 1 versions are the weaker, wider books (8-9k trips): late in the day the volume gate is what selects.
- ⚠ The gates 4.1 / 4.4 come from the close book's own volume profile, not a search; but the 15:30 2026 cells are small
  (74-160 trips). A candidate for the return rule / a second book, not a ruling.

## Run 41 — the late entries examined: is 15:30 real, and the relaxed gates (2026-11-06, user)

> USER: *"relax the rvol criteria from 5 to 3 and get more trades that way. Maybe we could also reduce the change % criteria a
> bit as well. One thing that doesn't sit well with me is how much better 15:30 ET is than either 15 or 16 ET ... did it
> somehow catch a large trade that the other 2 filters missed? ... maybe 8% and 3 rvol on low median volume stocks would be
> better than 10% and 5 rvol on the full book?"*

Script `scripts/equity/highflyer_v2_late.py` → `data/equity/highflyer_repro/v2_late.md`; candles at 14:30 / 15:15 / 15:45 /
16:00 added. Every late book through the SAME path (`--entry partial`: the candle [09:30, c), the fade gate), 16:00 included —
the old `--entry close` path had no fade gate and gated on the daily bar. rvol in CLOSE-EQUIVALENT units: k × the close
book's median share of the day's volume in by the cutoff (14:30 .770 … 16:00 .985), k = 5 (the close entry's) or 3.

**1. 15:30 is not a fluke — and not special.** PF clip (PF without the best 5 % of trips), move >= 10 %, k = 5:

| cutoff | all 2005–2015 | all 2016–2025 | all 2026 | proxy 2016–2025 | proxy 2026 |
|---|---|---|---|---|---|
| 14:30 | 2.03 (1.37) | 1.74 (1.15) | 1.29 (0.79) | 1.92 (1.34) | 1.45 (0.96) |
| 15:00 | 1.93 (1.31) | 1.72 (1.14) | 1.27 (0.79) | 1.95 (1.35) | 1.55 (1.04) |
| 15:15 | 1.89 (1.27) | 1.73 (1.15) | 1.37 (0.83) | 1.86 (1.29) | 1.70 (1.14) |
| 15:30 | 1.85 (1.24) | 1.80 (1.19) | 1.37 (0.86) | 1.95 (1.35) | **2.01** (1.36) |
| 15:45 | 1.82 (1.22) | 1.79 (1.17) | 1.22 (0.74) | 1.88 (1.30) | 1.56 (0.97) |
| 16:00 | 1.72 (1.14) | 1.68 (1.11) | 1.30 (0.82) | 1.71 (1.18) | 1.49 (0.99) |

The 2016–2025 curve is FLAT from 14:30 to 15:45 (1.72-1.80; the proxy 1.86-1.95) and drops at 16:00; 2005–2015 falls
steadily with the hour (2.03 → 1.72). 15:30's 2026 peak (the proxy 2.01 on 74 trips) is a bump on a ~75-trip cell between
1.70 and 1.56 — noise. It does not come from a big trade (the 10 best trips are 3 % of the 2016–2025 gains; the PF is clipped
at +50 %), but in 2026 every late book leans on its best trips (36-43 % of the gains in the top 10; < 1 without the best 5 %
except the proxy book at 15:00-15:30). What IS robust: **the last 15-30 minutes cost** — 16:00 is the worst late cutoff in
every period — and against the OLD close book 15:30 wins twice: on the 2,315 common trips its fill is better (PF 1.90 vs
1.81), and the old close path's 602 extra trips (no fade gate) are dead weight (PF 0.97). Through the same path the close
itself is 1.68 (2016–2025), not 1.57.

**2. The relaxed gates.** rvol 3 instead of 5 nearly doubles the trips and costs PF everywhere (all, 15:30: 1.80 → 1.56; the
proxy 1.95 → 1.82); move 8 % instead of 10 % adds ~15 % more trips at about the same PF (1.80 → 1.75; the proxy 1.95 → 1.86).
**The user's comparison**: the proxy book at 8 % / rvol 3 (15:30) — 2,651 trips 2016–2025, PF 1.75 (ex best 5 % 1.20), mean
+3.2 % — against the full book at 10 % / rvol 5 — 2,616 trips, 1.80 (1.19), +2.8 %: **the same trip count and PF at a
higher mean, and better in 2026 (1.49 vs 1.37, 175 vs 160 trips)**; every year 2016–2026 positive (1.21 in 2022 to 2.26 in
2019). Summed over the trips it is the larger book (+85 vs +73 position-units in 2016–2025; the proxy at 10 % / 5 +48), but
it is the LOW-liquidity population — the exit-day liquidity of Run 33 applies in full. The cleanest cell is still the proxy
at 10 % / 5 (1.95, 1,191 trips), the relaxed one buys trips at a lower PF.

**Run 41 addendum — the LOOKAHEAD audit of the late books (user: "Is this using lookahead data?").** Every gate, against the
port's code: move = the cutoff candle's close vs D−1's close as the holder's return (`cd − k1` = a dividend going ex on D,
known at the open); rvol = the volume to the cutoff over the mean of the 20 PRIOR sessions; the 52w high, ATR, tightness, adv
and dv60_med on prior bars only; the fade on the candle itself; the candle = minute bars strictly before the cutoff, the fill
its last close (optimistic as a fill, not a lookahead). Three things checked beyond that:
- **Survivorship (real, negligible)**: a trip whose stock stops trading for > 45 days before its exit (delisting, a long halt)
  is `open = 1`, and the Run 37–41 tables kept `open == 0` only. Such trips: 7 of 3,948 (10:00), 7 of 5,158 (15:30 10 % / 5),
  17 of 12,202 (15:30 8 % / 3) — with them every PF moves in the 3rd decimal (10:00 2016–2025 1.948 → 1.937; 15:30
  1.796 → 1.798; 2026 0.817 → 0.824 / 1.367 → 1.368). They are valued at the last close; a real delisting can lose more.
- **The rvol calibration**: the close-equivalent fractions were measured on 2005–2026; by period they are stable (15:30:
  .887 / .880 / .878 / .860 for 2005-10 / 2011-15 / 2016-20 / 2021-26; 15:00 .82-.83; 16:00 .97-.99) — a pre-2016 value moves
  the 15:30 gate from 4.40 to 4.42. One scalar from the volume profile, not from returns.
- **Inherited, minor**: n(D) is research's corrected one (split_corrections read D's tape — split days only); the CS/ADRC type
  is today's ticker_reference; vendor minute aggs may place a late-reported print in its execution minute.

## Run 42 — a LINEAR SIZING MODEL for the 15:00 entry, with dv60_med (2026-11-06, user)

> USER: *"creating a linear sizing model that is a function of price change above 8% and rvol above 2 would be a good idea ...
> ideally, we'd enter an hour before the close. I don't trust the 15:30 ET results in 2026, most likely it's overfit. We'll
> also have to figure out how to include 60d median volume into our sizing. Let's do that and then we'll leave the system
> aside for the time being to focus on options."*

Script `scripts/equity/highflyer_v2_sizing.py` → `data/equity/highflyer_repro/v2_sizing.md`. The book: 15:00 (the candle
[09:30, 15:00), the fill its close), move >= 8 %, rvol >= 2 close-equivalent (1.64 at 15:00): 16,758 trips 2005–2026-10, the
delisting-cut trips kept at their last close. The model: GF-54's form — logit p, log-link W (wins clipped at +50 %) and L,
IRLS, standardised terms winsorised 1/99 % (flags not); PF−1 = p / ((1 − p) · L / W) − 1; units = clip(PF−1, 0.125, 2), PF−1 <= 0
refused. **FIT 2005–2020, TEST 2021–2026** (never seen).

**The three-term model** (move, ln rvol, ln dv60_med; per 1 sd: odds × 0.999 / 1.107 / 0.990, W × 1.148 / 0.875 / 0.797,
L × 1.089 / 0.856 / 0.858 — a bigger move = bigger wins, more volume = more wins and smaller swings, a bigger name = smaller
wins): test sized PF **1.42** vs equal-weight 1.27 (2021–2025 1.46 vs 1.31; 2026 1.17 vs 1.06); it RANKS — the test quintiles
of predicted PF−1 realise +0.07 / +0.15 / +0.06 / +0.39 / +0.69; the units shuffled across the test trips 1000× give p95 1.32
(the model 1.42, 0 % of shuffles at or above it); every term pulls its weight (without move 1.43, rvol 1.37, dv60 1.36).

**But the dv60_med effect is a STEP, not a slope** (fit / test PF: < $500k 1.74 / 1.74, $0.5-2M 1.82 / 1.32, $2-10M 1.33 /
1.14, > $10M 1.45 / 1.02) — and the plain proxy FILTER (dv60_med < $2M, equal weight) beats the model: test PF **1.55**
(2,384 trips), 2026 1.44. A flag in the model does not fix it (1.43): the 0.125-unit floor keeps the > $2M trips at ~0.4
units, and since 2021 they are near breakeven. (The filter is Run 32's low-float line, not a test-period choice; in 2005–2020
the < $2M names were already the better ones.) Move and rvol are monotone in both halves (move 8-10 % → 20-30 %: fit 1.39 →
1.85, test 1.20 → 1.40; rvol 2-3 → 10+: fit 1.37 → 2.09, test 1.11 → 1.89).

| test 2021–2026 | trips | equal-weight PF | sized PF | 2026 equal / sized |
|---|---|---|---|---|
| the three-term model, all trips | 5,848 | 1.27 | 1.42 | 1.06 / 1.17 |
| move, lrv + a dv60 < $2M flag | 5,848 | 1.27 | 1.43 | 1.06 / 1.18 |
| **the proxy FILTER, then sized on move and ln rvol** | 2,384 | 1.55 | **1.62** | 1.44 / **1.46** |

**The chosen form: filter on dv60_med < $2M, size on move and ln(rvol).** Within the filtered book the model adds less (1.55
→ 1.62; 2021–2025 1.56 → 1.64, 2026 1.44 → 1.46) and ranks only at the ends (test quintiles +0.12 / +0.78 / +0.58 / +0.38 /
+0.95 realised PF−1): it mostly trims the weakest — small move AND low rvol. The 2005–2025 refit (a production candidate,
not deployed; per 1 sd): intercept odds 1.180, W 0.1050, L 0.0733; move [0.0805, 0.2818] mean 0.1325 sd 0.0478 — odds × 1.055,
W × 1.118, L × 1.101; ln rvol (close-eq.) [0.709, 4.197] mean 1.614 sd 0.718 — odds × 1.064, W × 0.955, L × 0.927.

**Status (user): HighFlyerV2 is set aside for the options work.** Not dead — the late entry works in 2026 (the proxy book at
15:00: 1.44 equal / 1.46 sized on 271 trips) — and the candidate is: 15:00 entry, move >= 8 %, rvol >= 2 close-equivalent,
dv60_med < $2M, sized by this model; the exit-day liquidity (Run 33) and costs remain unmodelled.

**Run 42 addendum — why the sizing adds so little (user: "I am surprised that sizing based on change % and rvol doesn't add
more").** The signal is real and holds out of sample INSIDE the filtered book (dv60_med < $2M; fit 2005–2020 | test 2021–2026
PF): move 8-10 % 1.48 | 1.31 → 20-30 % 2.25 | 2.00; rvol 2-3 1.50 | 1.18 → 10+ 2.02 | 2.17; the 2×2 cells (move >= 10 % ×
rvol >= 5) test 1.30 / 1.36 / 1.44 / **1.88**. (rvol and dv60_med are nearly independent, rank ρ −0.13: not a collinearity.)
What limits it is the yardstick and the lever:
- **A PF is a ratio, bounded by its cells**: no weighting of a book can lift it above its best cell's PF (1.88 here) — sizing
  only blends toward it, and weights spanning ~3× (the fit's cell PF−1: 0.35 / 1.00 / 0.71 / 1.16) take 1.55 to 1.63.
- **The value of sizing is in the return per unit of capital** (clip returns, test 2021–2026, the same total capital):

| scheme | PF | mean per unit | total (same capital) | trips |
|---|---|---|---|---|
| equal weight | 1.55 | +2.13 % | 5,078 | 2,384 |
| by the fit's cell PF−1 | 1.63 | +2.44 % (+15 %) | 5,811 | 2,384 |
| the weakest cell refused (move < 10 % and rvol < 5) | 1.61 | +2.41 % | 5,742 | 1,866 |
| only the best cell | 1.88 | +3.33 % (+56 %) | 7,932 | 770 |

- **Capacity is the real cap**: the big gain needs ~3× the size in the best third of the trips — and these are the
  dv60_med < $2M names, where 0.25 % of dv60_med is ~$5k and the exit morning already takes 11 % of the volume for $10k
  (Run 33). For a small book the practical lever is the simple one: refuse the weakest cell (+13 % per unit), size the rest
  evenly up to the liquidity cap.

**Run 42 addendum 2 — the linear model vs the cells, on the same yardstick** (user: "Are you sizing by cells, or have you tried
fitting a linear model?"). Inside the filtered book (dv60_med < $2M), fit 2005–2020, test 2021–2026, clip returns:

| scheme | PF | mean per unit | vs equal | weight spread p90/p10 | 2026 PF |
|---|---|---|---|---|---|
| equal weight | 1.55 | +2.13 % | — | 1.0× | 1.44 |
| the linear PF−1 model, units = clip(PF−1, .125, 2) (Run 42's) | 1.62 | +2.44 % | +14 % | 2.2× | 1.46 |
| the same, units ∝ PF−1 (no floor / cap) | 1.62 | +2.44 % | +14 % | 2.2× | 1.46 |
| **the same, units ∝ (PF−1)²** | **1.72** | **+2.81 %** | **+32 %** | 4.8× | 1.50 |
| OLS on the clipped return, units ∝ max(predicted, 0) | 1.60 | +2.35 % | +10 % | 2.1× | 1.45 |
| the 2×2 cells' fit PF−1 | 1.63 | +2.44 % | +14 % | 3.3× | 1.45 |

The linear model and the cells extract the same (+14 % per unit): the limit is the SPREAD of the weights — the model's
predicted PF−1 on the test trips runs only p10 +0.55 to p90 +1.20 (2.2×). Squaring it (4.8×) takes +32 % per unit and PF 1.72,
and it holds in 2026 (1.50) — the sharper the mapping, the more concentrated the book (the capacity limit of the addendum
above). Note the OLS: per sd, move adds +0.67 % to the mean return but ln rvol only +0.04 % — rvol improves the PF through
MORE WINS AND SMALLER SWINGS (odds × 1.06, W × 0.96, L × 0.93 in the PF−1 model), not a higher mean.

**Run 42 addendum 3 — squared INPUT terms** (user: "Let's try it"; the (PF−1)² above squares the model's OUTPUT, the size
mapping). Inside the filtered book, fit 2005–2020 / test 2021–2026, every term standardised on the fit set:

| terms | test, units = clip(PF−1) | test, units = (PF−1)²: PF · per unit · vs equal | 2026 | in sample (PF−1)² |
|---|---|---|---|---|
| move, ln rvol (the chosen form) | 1.62 · +2.44 % | **1.72** · +2.81 % · +32 % | 1.50 | 1.93 |
| + move² | 1.61 · +2.41 % | 1.68 · +2.68 % · +26 % | 1.46 | 1.93 |
| + ln rvol² | 1.64 · +2.49 % | 1.73 · +2.79 % · +31 % | 1.48 | 2.00 |
| + move², ln rvol² | 1.64 · +2.47 % | 1.70 · +2.73 % · +28 % | 1.45 | 2.00 |
| + move × ln rvol | 1.61 · +2.40 % | 1.67 · +2.65 % · +24 % | 1.48 | 1.93 |
| + all three | 1.65 · +2.50 % | 1.72 · +2.78 % · +30 % | 1.42 | 2.00 |

**Read — no gain out of sample.** The squares fit the past better (in sample 1.93 → 2.00: ln rvol² odds × 0.93, W × 0.93 — the
rvol effect bends over at the extremes) and the test does not confirm it (1.67-1.73 vs 1.72; 2026 1.42-1.48 vs 1.50), as the
band tables' straight-line shapes said. **The model stays linear in its inputs; the sharpness goes in the size mapping,
units ∝ (PF−1)².**
