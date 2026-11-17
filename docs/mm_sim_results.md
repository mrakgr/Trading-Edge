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
