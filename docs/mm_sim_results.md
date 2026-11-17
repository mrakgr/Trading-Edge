# Market-making simulator — results (2026-11-17)

The design is in `market_making_primer.md` §5. The scripts:
- `scripts/equity/mm_quotes_fetch.py`: the NBBO for each window.
- `scripts/equity/mm_sim.py`: the simulator.

**The data:**
- The 527 tier-B triggers of `mm_opportunity_results.md`, 2026-05-14 → 11-13.
- The NBBO comes from Massive, 28.2M quotes. The prints come from the bulk tape.
- The parameters are calibrated on the warmup, before the trigger. The maker trades for the 30 minutes after it.

**The settings:**
- A lot is about \$2,000 in round lots, with a cap of 3 lots of inventory.
- The fill model is the queue model.
- The latency is 150 ms. Live measures about 130 ms to the venue, plus our decision time.
- The fees: 0.2c commission, +0.20c add credit, −0.30c to take.
- Borrows are assumed free (user), so the maker quotes both sides from flat.

## Run 1 — static quoting rules lose everywhere

**The rules:**
- **join**: bid at the best bid, ask at the best ask.
- **penny**: one tick inside the touch on spreads of 3 ticks or more.
- **glft:1**: Guéant–Lehalle–Fernandez-Tapia (Avellaneda–Stoikov with an inventory bound) centred on the mid, γ = k.

**The variants**, each lifting one realistic friction:
- **base**: as above.
- **lat0**: a 1 ms latency.
- **post**: post-only, so an order that lands marketable is cancelled instead of taking.
- **front**: every joined order is first in line.
- **best**: all three together, the optimistic bound.

In the table, everything in bp is of the traded notional. "taker / maker" is the taken shares as a share of the maker
shares.

| variant | rule | net \$M | net bp | spread captured bp | adverse at 5 s bp | fees bp | taker / maker | windows won |
|---|---|---|---|---|---|---|---|---|
| base | join | −3.16 | −23.1 | +6.3 | −17.5 | 5.9 | 33 % | 4 % |
| base | penny | −3.53 | −20.9 | +7.0 | −16.3 | 5.8 | 34 % | 5 % |
| base | glft:1 | −3.44 | −23.6 | +3.0 | −15.8 | 5.4 | 31 % | 2 % |
| lat0 | join | −2.81 | −15.0 | +9.8 | −17.4 | 3.4 | 17 % | 4 % |
| post | join | −2.10 | −18.7 | +7.8 | −22.0 | 2.8 | 0 % | 6 % |
| front | join | −3.13 | −19.6 | +10.2 | −18.5 | 5.8 | 29 % | 5 % |
| best | join | −1.98 | −8.2 | +14.6 | −19.6 | 2.2 | 0 % | 10 % |
| best | penny | −2.24 | −7.6 | +12.5 | −17.4 | 2.1 | 0 % | 8 % |
| best | glft:1 | −2.08 | −9.3 | +10.0 | −16.5 | 2.1 | 0 % | 5 % |

(glft is calibrated on 479 windows, the others on 527. glft:0.1 and glft:10 lose about as much as glft:1: −23.7 and
−25.1 bp in base.)

**Findings:**
- **Every rule loses in every variant.** Even the optimistic bound loses 8 bp of notional, and only 5–10 % of windows
  win.
- **The loss is adverse selection.** The mid moves 16–22 bp against a fill within 5 s, more than the 6–15 bp of spread
  the fill captures. Fees are secondary.
- **The quoting formula barely matters.** join, penny and GLFT land within a few bp of each other. Without a forecast,
  A-S/GLFT only redistributes the same toxic fills.
- **The captured spread is far below the quoted half-spread.** The quoted spread is ~50 bp, so the half-spread is ~25 bp,
  but fills capture 6–15 bp. A queued maker is filled mostly when its level is swept or its quote is stale, which is
  when the price is moving through it.
- **Latency costs about 8 bp** (base → lat0). Most of it is bids landing on an ask that has already come down to them:
  17–33 % of the volume is taken by accident in hot tape. Post-only removes the taking, but it does not remove the
  toxicity.
- **The segments.**
  - **By price:** ≥ \$20 is the least bad: −6 bp in base, +0.3 bp in best. Sub-\$1 is the worst, at −83 to −106 bp.
  - **By spread:** wider-spread windows lose more, not less. At > 100 bp quoted spread the adverse selection is
    −42 to −50 bp.
  - **By spread / σ:** a higher ratio of the spread to the 1 s volatility does not rescue it.

**What this means.**
- On trades + NBBO, quoting without a view of the next few seconds loses money in exactly the hot windows the
  opportunity scan finds.
- The academic models handle inventory, not toxicity. What is missing is §2.3 of the primer (Cartea–Jaimungal): a
  short-term direction signal that pulls or moves the side about to be run over.
- The next question is whether such a signal exists on this data, and what it would be worth:
  - an oracle bound: pull the side the mid moves against in the next few seconds;
  - then real features, such as signed order flow over the last N seconds, quote imbalance, the micro-price, and
    sweep detection;
  - and how much of the 5 s adverse selection they predict.

## Run 2 — what a direction signal would have to be worth

**The perfect oracle (LOOKAHEAD, a bound only).** `mm_sim.py --oracle H:bp` pulls the bid while the mid `H` s ahead is
more than `bp` below now, and pulls the ask while it is more than `bp` above. With perfect foresight every variant turns
strongly positive, as any lookahead would:

| oracle | base join | best join |
|---|---|---|
| 1 s, 5 bp | +30 bp | +34 bp |
| 5 s, 5 bp | +71 bp | +61 bp |
| 30 s, 20 bp | +85 bp | +59 bp |

**The noisy oracle: the accuracy needed.** `--oracle-rho ρ` adds noise to the future move, held for a second at a time,
so the signal correlates with the true 5 s move at ρ. Base settings, join:

| ρ with the 5 s move | net bp | adverse at 5 s bp | windows won |
|---|---|---|---|
| 0.1 | −18.1 | −13.0 | 13 % |
| 0.2 | −11.1 | −7.8 | 23 % |
| 0.3 | −4.2 | −2.5 | 44 % |
| 0.5 | +9.6 | +7.9 | 75 % |
| 0.7 | +23.3 | +17.9 | 87 % |

The break-even is **ρ ≈ 0.35**. The threshold (5 or 20 bp) barely matters.

**What causal features reach** (`scripts/equity/mm_signal_corr.py` → `data/equity/mm_sim/signal_corr.md`):
- the data: 519 windows on a 100 ms grid, 9.1M points;
- each feature uses only the data strictly before the grid time.

| feature | corr with the 1 s move | corr with the 5 s move | median per window, 5 s |
|---|---|---|---|
| signed lit flow, last 1 s | +0.104 | +0.072 | +0.086 |
| signed lit flow, last 5 s | +0.047 | +0.042 | +0.056 |
| mid return, last 1 s | +0.053 | +0.020 | +0.006 |
| mid return, last 5 s | +0.019 | +0.001 | −0.003 |
| NBBO size imbalance | +0.011 | +0.004 | +0.005 |
| micro-price − mid | +0.002 | −0.002 | +0.005 |
| OLS of all, ×spread interactions; fit on half the windows, scored on the other half | +0.108 | +0.082 | +0.097 |

**The cheap-data verdict:**
- The best causal signal reaches ρ ≈ 0.1. The break-even needs ≈ 0.35, and the noisy oracle at ρ = 0.1 still loses
  18 bp.
- **Trades + NBBO cannot support a maker in these windows.**
- The NBBO size imbalance carries nothing. Its size is one venue's size at the best price, not the book's depth. Depth
  and queue information exist only in the MBO data. That is the next step: an MBO simulator on CBRS 2026-05-14 (user).

## CBRS 2026-05-14 in run 1

**The window:** 13:08–13:38 ET, from the trigger 3 minutes after the reopen.
- \$368 a share, so a lot is 100 shares, about \$37k. The cap is 3 lots.
- The median quoted spread was 33 bp.

| rule | base: net \$ | spread captured \$ | adverse at 5 s \$ | best: net \$ |
|---|---|---|---|---|
| join | −27,787 | +43,261 | −58,278 | +173 |
| penny | −10,091 | +73,043 | −66,936 | +9,691 |
| glft:1 | −6,381 | +28,323 | −33,823 | +7,590 |

- With the realistic frictions it loses.
- Only the optimistic bound (0 latency, front of the queue, post-only) breaks even or makes money.
- The second CBRS window (07-23) loses in every variant.

## Run 3 — the MBO simulator on CBRS 2026-05-14 (`scripts/equity/mm_mbo_sim.py`)

**The book.** All 13 lit venues are rebuilt order by order: 2.3M Databento MBO records, a few seconds per run.
- **C records reduce the book; F records do not.** A fill's book effect is the C that follows it. The C's size is the
  amount removed, and it can be partial. On every venue, 96–100 % of the orders are removed by C's summing to exactly
  their added size, and every filled order's C's sum to its full size.
- **The protected NBBO** is, per venue, the best price with ≥ 1 round lot displayed.
  - With a round lot of **100 shares**, the rebuilt NBBO equals Massive's SIP NBBO at **98.2 % / 98.3 %** of 1 s points
    (bid / ask). The rest is consistent with timing.
  - With the 2025 tier's 40 shares it is 82 %, and the book is always tighter than the SIP.
  - So CBRS was on 100-share round lots on its IPO day. There was no prior month to assign a price tier from.
  - Odd lots inside the protected quote still count for the queue, for taking, and for the "level is clear" test.

**Our orders:**
- **The queue is exact.** It is the size ahead of us at our venue and price, by arrival order.
- **Own-venue fills:** an aggressor reaching our price, or trading through it, once nothing is ahead of us.
- **Cross-venue fills (Reg NMS order protection):** a print on another venue strictly through our price, while no real
  order is at our price or better anywhere.
- **Outbound routing:** an order landing marketable takes the real consolidated book, or is cancelled with
  `--post-only`.
- **The settings:** 100-share lots (about \$37k), a cap of 3 lots, quoting against the protected NBBO.

**The variants at 150 ms, on ARCA** (Nasdaq is within a few \$k everywhere):

| window | rule | base (150 ms) | post-only | front of the queue | 1 ms | 1 ms + post-only + front |
|---|---|---|---|---|---|---|
| 13:05–13:35 (from the reopen) | join | −\$38.1k (−11.5 bp) | −\$30.9k | −\$29.4k | −\$10.9k | **+\$15.2k (+3.0 bp)** |
| 13:05–13:35 | penny | −\$20.2k (−3.7 bp) | −\$12.7k | −\$17.5k | **+\$43.9k (+4.8 bp)** | **+\$38.7k (+4.4 bp)** |
| 13:08–13:38 | join | −\$26.0k | −\$23.8k | −\$22.8k | −\$21.2k | −\$11.5k |
| 13:08–13:38 | penny | −\$22.7k | −\$19.1k | −\$21.6k | −\$9.7k | −\$9.5k |
| 13:05–16:00 | join | −\$86.4k | −\$75.5k | −\$79.7k | −\$56.6k | −\$29.1k |
| 13:05–16:00 | penny | −\$71.8k | −\$60.8k | −\$67.9k | −\$5.0k | −\$10.9k |

**Latency is the lever.** Penny on ARCA, net \$:

| latency | 13:05–13:35 | 13:35–16:00 |
|---|---|---|
| 1 ms | **+43.9k** | −49.6k |
| 5 ms | +24.5k | −49.7k |
| 10 ms | +21.0k | −50.6k |
| 25 ms | +8.1k | −51.3k |
| 50 ms | +8.5k | −52.9k |
| 100 ms | −5.6k | −50.7k |
| 150 ms (≈ ours) | −20.2k | −53.2k |
| 250 ms | −33.7k | −54.7k |

**Findings:**
- **On CBRS a maker works only in the first ~30 minutes after the reopen** (median spread 47 bp), and only at a
  latency under ~75 ms.
  - Pennying the touch makes +\$44k at 1 ms and about +\$8k at 25–50 ms.
  - At our ~150 ms it loses \$20k.
- **After 13:35 every rule loses about \$50k at any latency** (median spread 26 bp). That loss is the price trend and
  adverse selection, not speed.
- **Cross-venue fills are 2/3 of penny's fills.** Pennying puts us alone at the best price, and flow arriving at the
  other venues is routed to us. That fill path is load-bearing, and it is also the optimistic part of the model: it
  assumes the routers see our quote the instant it lands.
- **The 3-minute delay of the trigger costs the best part.** 13:08–13:38 loses in every variant, while 13:05–13:35
  makes money at low latency.

**The cheap simulator on the same window (13:05–13:35)** shows the MBO advantage is not a signal. Neither simulator
uses an order-book signal; join and penny are the same rules in both.

| rule | latency | cheap (trades + NBBO) | MBO |
|---|---|---|---|
| join | 150 ms | −\$30.5k | −\$38.1k |
| penny | 150 ms | +\$7.8k | −\$20.2k |
| join | 1 ms | +\$10.7k | −\$10.9k |
| penny | 1 ms | +\$135.3k | +\$43.9k |
| penny | 1 ms, post-only, front | +\$133.7k | +\$38.7k |

- **The cheap simulator is MORE optimistic on this window, not less.** Its queue is the one-venue SIP size. It fills on
  every lit print through our price, with no check that the real book has cleared our level.
- **What made run 3 look better than runs 1–2:**
  - The window starts at the 13:05 reopen. Runs 1–2 start at the 13:08 trigger, which loses in every variant of both
    simulators.
  - The latency.
- **The exact book takes about 2/3 off the low-latency result.** That is the MBO data's value here: accuracy, not
  extra edge.
- An order-book imbalance signal has not been tested yet.

## Run 4 — order-book signals in the maker (`mm_mbo_sim.py --signal`, `scripts/equity/mm_mbo_signal.py`)

**The features**, computed in the simulator's event loop from the rebuilt 13-venue book. Each is causal (the state
before the grid time) and lies in [−1, 1]:
- the L1 size imbalance at the protected best prices (`imb1`) and over 5 ticks (`imb5`);
- the Cont–Kukanov–Stoikov L1 order-flow imbalance, EWMA τ = 1 s and 5 s (`ofi_1`, `ofi_5`);
- the signed aggressor volume of the T records, τ = 1 s and 5 s (`tf_1`, `tf_5`).

The data is the 100 ms grid of 16 name-days: every MBO day on disk, RTH (CBRS from the 13:05 reopen), 3.6M points.

**Predictive power.** The correlation with the next H s of the mid, by an OLS fit on the other name-days
(leave-one-out, so all out of sample):

| | H = 1 s | H = 5 s |
|---|---|---|
| median of the 16 name-days | +0.071 | +0.083 |
| range | +0.04 … +0.16 | +0.04 … +0.19 |
| CBRS | +0.110 | +0.141 |
| the best single features | imb1 on AAPL / SMCI / AIXI (0.14–0.18); tf_1 elsewhere | the same |

This is the same level as the trades + NBBO features (about 0.1), and far from the cheap-data break-even (about 0.35).

**In the maker.** The model is fit on the 15 non-CBRS name-days, H = 1 s, so CBRS is out of sample. `join+sig` /
`penny+sig` pull the bid while the predicted move is below −thr, and the ask while it is above +thr. On CBRS the
predicted move spans −1.4 … +1.3 bp at its 1st / 99th percentiles.

| CBRS, net \$ | 150 ms, 13:05–13:35 | 150 ms, 13:35–16:00 | 1 ms, 13:05–13:35 | 1 ms, 13:35–16:00 |
|---|---|---|---|---|
| join | −38.1k | −48.6k | −10.9k | −47.1k |
| join+sig (thr 0.3 bp) | −16.6k | −27.2k | +0.2k | −14.4k |
| penny | −20.2k | −53.2k | **+43.9k** | −49.6k |
| penny+sig (thr 0.3 bp) | −15.7k | −31.8k | +30.6k | −23.0k |
| penny+sig (thr 0.6 bp) | −14.6k | −41.0k | **+45.9k** | −33.6k |

Four training name-days, RTH. These are in-sample for the model, so optimistic:

| | 150 ms: rule → +sig (net bp) | 1 ms: rule → +sig (net bp) |
|---|---|---|
| AKAN (a wide spread) | penny −17.2 → −11.6 | join +4.4 → **+16.2**; penny +1.0 → **+13.0** |
| QUBT, SMCI, AIXI (one-tick spreads) | −3 to −70 → about the same | about the same |

**Findings:**
- **The signal cuts the losses mostly by trading less.** It pulls a side 30–50 % of the time.
- **Per unit traded it helps only where the spread is wide and the latency is low** (AKAN at 1 ms: +4 → +16 bp).
- **On one-tick names nothing helps.** Their spread is one tick and the maker is queue-bound.
- **At our ~150 ms nothing is positive anywhere.**
- **The order-book signals are not what makes the MBO run work.** Latency and the reopen window are.

## Run 5 — the cheap simulator on 40 IPO first days, quoting from the opening print

**The setup.**
- The 40 IPO first days of the realized-spread study (`ipo_top40.parquet`; 2020–2026; COIN was a direct listing).
- Each window starts at the minute of the IPO's first lit print (the event), not at a scan trigger. List:
  `data/equity/mm_sim/ipo40_triggers.csv`.
- The NBBO comes from Massive (`quotes_ipo/`), the prints from the bulk tape.
- The same queue model, fees and inventory cap as before. GLFT has no warmup before an open, so it was not run.

| latency | minutes from the open | rule | total | without SPCX & CBRS | windows won | median window | net bp |
|---|---|---|---|---|---|---|---|
| 1 ms | 30 | join | −\$148k | −\$345k | 4/40 | −\$3.7k | −1.1 |
| 1 ms | 30 | **penny** | **+\$659k** | **−\$54k** | **19/40** | −\$0.2k | +2.8 |
| 1 ms | 60 | penny | +\$503k | −\$205k | 12/40 | −\$2.2k | +1.7 |
| 1 ms | 120 | penny | +\$284k | −\$413k | 8/40 | −\$5.2k | +0.8 |
| 150 ms | 30 | join | −\$377k | −\$389k | 2/40 | −\$4.7k | −4.6 |
| 150 ms | 30 | penny | −\$247k | −\$294k | 4/40 | −\$3.6k | −2.3 |
| 150 ms | 120 | penny | −\$616k | −\$610k | 2/40 | −\$9.0k | −2.4 |

**Penny, the first 30 minutes, by name** (\$k):

| | 1 ms | 150 ms |
|---|---|---|
| SPCX 2026-06-12 (2.1M lit prints) | **+578** | **+53** |
| CBRS 2026-05-14 | +136 | −7 |
| RBLX, BLSH, SNOW, CRCL, CVAC, RKT, PLTR, U, KLAR | +4 to +16 each | −42 to +3 |
| 8 more | 0 to +2 | −6 to 0 |
| the other 21 | −0.2 to −34 (COIN −34, DASH −26, AI −20) | −0.3 to −51 |

**Findings:**
- **At 1 ms, half of the IPO opens are break-even or better** for pennying the touch over the first 30 minutes (19/40,
  median −\$0.2k).
- **The money is two days.** Without SPCX and CBRS the 38 others lose \$54k in total.
- **Longer windows are worse.** The edge is the first minutes after the open: 30 min → +2.8 bp, 120 min → +0.8 bp.
- **At 150 ms only 4/40 win.** SPCX is the only big winner, at +\$53k. That makes the latency verdict of run 3 general.
- **The cheap simulator overstated CBRS 3× against MBO** (+\$136k vs +\$44k at 1 ms), so these totals are an upper
  bound.
- **Market-making profit here is episodic.** It sits in a few giant, liquid, wide-spread IPO openings, not in a steady
  daily flow.

## Run 6 — microsecond latency on all 16 MBO name-days

**Zero latency is invalid.** Databento writes a print, its F and the C that removes the liquidity as separate records
with the same timestamp. An order at `--lat 0` lands between them and takes liquidity that has already traded (AKAN:
+\$431k on 485k taker shares). Any positive latency lands after all the records that share a timestamp.

**Microseconds bring a second artifact: latency arbitrage.** Without `--post-only`, at 1 µs AKAN makes +\$201k (+\$70k at
100 µs, +\$4k at 1 ms). Of that, 232k shares are TAKES and only 26k are maker shares. In fast moves one venue's bids
still stand above another venue's fresh ask. The simulator lets our order win every one of those races, at full size
and with no competitors. That is crossed-market arbitrage, which real HFTs race for by the nanosecond. It is not market
making, and it is not credible here.

**Market making alone (post-only)**, net \$k. Full RTH; CBRS split at 13:35:

| | join 1 µs | join 100 µs | join 1 ms | join 150 ms | penny 1 µs | penny 100 µs | penny 1 ms | penny 150 ms |
|---|---|---|---|---|---|---|---|---|
| CBRS 13:05–13:35 | +8.7 | +4.3 | −5.2 | −30.9 | **+62.6** | +54.4 | +42.9 | −12.7 |
| CBRS 13:35–16:00 | −28.7 | −42.4 | −47.5 | −46.4 | −57.9 | −52.8 | −50.2 | −47.5 |
| the 15 other name-days, total | −36.1 | −47.7 | −80.0 | −84.9 | −123.4 | −116.9 | −112.7 | −100.3 |
| total | −56.1 | −85.8 | −132.7 | −162.2 | −118.7 | −115.3 | −120.0 | −160.5 |
| windows won (of 17) | 3 | 2 | 0 | 0 | 1 | 1 | 1 | 0 |

**Findings:**
- **Even at 1 µs a pure maker loses on every ordinary session.** The best are CYTK +\$0.5k and SKYQ +\$0.1k on join.
- **Speed shrinks join's losses** (−\$162k → −\$56k) but never crosses zero.
- **Penny barely improves with speed.** At any latency its losses are adverse selection.
- **The only positive window remains CBRS's first 30 minutes after the reopen.**
- **What a fast simulator "earns" beyond that is crossed-market taking, not making.**

### Run 6b — queue position at 1 µs, and where the money goes

`mm_mbo_sim.py` now writes, for each fill, the queue ahead of the order and the level size (all venues) at its
placement, and the mid at the fill and 1 / 5 / 30 s later. Settings: join, 1 µs, post-only, ARCA.

**We are mostly at the FRONT of our venue's queue.**
- 54–73 % of the maker fills (CBRS, DDOG, QUBT) and 28 % (AKAN) came from orders with nothing ahead of them on ARCA.
- The median level across all venues was 127–623 shares at the same price. Flow routed to the other venues does not
  reach us.

**Front-of-queue fills mark out no better than queued ones.** At 5 s, net of the captured spread: CBRS +3.3 vs −2.5 bp,
AKAN −1.8 vs +0.8, DDOG −0.3 vs +2.9, QUBT −1.2 vs −2.4.

**The per-fill markouts are about break-even. The day loses on INVENTORY.** In bp of maker notional:

| name-day | spread captured | net at 1 s | net at 5 s | net at 30 s | the simulation's net |
|---|---|---|---|---|---|
| AKAN | +35.2 | +8.2 | −0.1 | +2.1 | **−24.6** |
| CBRS | +12.7 | +3.7 | +0.9 | −5.3 | −3.1 |
| DDOG | +3.4 | +1.5 | +0.6 | +0.8 | −2.4 |
| QUBT | +2.3 | −1.2 | −1.7 | −1.7 | −2.0 |

The gap between the 30 s markout and the net is the inventory held for minutes through trends, plus the exit.

**A tighter inventory cap (1 lot instead of 3)** cuts the loss:

| | join, cap 3 → cap 1 (net bp) | penny, cap 3 → cap 1 (\$) |
|---|---|---|
| AKAN | −24.4 → −5.4 | |
| DDOG | −2.4 → −0.5 | |
| CBRS (13:05–16:00) | | +\$4.8k → **+\$36.3k** |
| QUBT | −2.0 → −2.0 | |

It still does not make a typical day positive. QUBT, a one-tick name, loses on short-term adverse selection.

### Run 6c — a larger inventory cap, and the capped inventory as a momentum signal (user 2026-11-17)

**The maker's net by cap** (join, 1 µs, post-only, 16 MBO name-days, \$k):

| cap (lots) | 1 | 3 | 5 | 10 |
|---|---|---|---|---|
| total | −28.5 | −53.5 | −68.6 | −87.9 |

- **A larger cap loses more.**
- A few range-bound days improve with a larger cap (cap 10: CYTK +4.5, RVMD +5.5, SKYQ +1.3, EOSE +0.4).
- The trend days blow up (CBRS −42.7).

**The momentum test (`--momentum`).**
- **The event:** the join maker's inventory reaches its cap.
- **The trade:** a taker enters AGAINST the inventory 150 ms later (maker long → sell at the bid), and exits by taking
  after H s. The cost is the full spread plus 2 × (0.3c + 0.2c) per share.

| cap | H | events | mid move with the trade, bp | net, bp | net \$ | name-days positive |
|---|---|---|---|---|---|---|
| 1 | 10 s / 60 s / 300 s | 12.7k | +1.3 / +2.4 / +4.6 | −19 / −18 / −16 | −\$125k / −\$119k / −\$93k | 0 / 0 / 1 of 16 |
| 3 | 10 s / 60 s / 300 s | 7.4k | +1.0 / +3.3 / +9.2 | −20 / −18 / −12 | −\$84k / −\$65k / −\$26k | 0 / 0 / 5 |
| 10 | 60 s / 300 s / 900 s / 1800 s | 4.5k | +5 / +17 / +27 / +13 | −15 / −3 / **+8** / −7 | −\$29k / −\$16k / +\$6k / +\$13k | 2 / 4 / 5 / 6 |
| 20 | 60 s / 300 s / 900 s / 1800 s | 3.6k | +4 / +22 / +27 / +11 | −13 / +4 / **+10** / −6 | −\$24k / −\$4k / +\$13k / +\$24k | 2 / 5 / 5 / 7 |
| 40 | 300 s / 900 s | 3.0k | +23 / +20 | +7 / +4 | +\$1k / −\$8k | 5 / 5 of 14 |

**Findings:**
- **The signal has the right sign.** After the inventory caps, the price keeps moving against it, and more so the larger
  the cap and the longer the horizon.
- **It only clears the ~20 bp round-trip cost at 5–15 minutes**, and only on very persistent one-sided flow (caps of
  10–20 lots).
- ⚠ **The events overlap heavily.** Many same-direction entries per name-day mean the effective sample is the 16
  name-days, and only 5–7 of them are positive.
- **It is carried by the trend days.** Cap 20 at 1800 s: CBRS +\$51k, QUBT +\$9k, XNDU +\$3k, against SMCI −\$14k and
  DDOG −\$11k.
- This is the same shape as the closed momentum program (2026-07-27): a few days carry it, and the median loses.
- **A real test needs hundreds of days with one position per name at a time.** The trades + NBBO version (signed
  aggressor volume) can be computed on the 128-day scan.
