# Market-maker buildup as a mean-reversion signal (2026-11-17)

**The idea (user, 2026-11-17).** The market-making study (`mm_sim_results.md`) found that a maker's losses come from the
inventory it is forced to hold through trends. *"Maybe if we focused on creating a signal to measure the potential market
maker build up for a longer term mean reversion system we could come up with something good."*

**The literature:**
- Grossman & Miller (1988): liquidity providers absorb one-sided flow for a price concession.
- Hendershott & Menkveld (2014), *Price Pressures*: NYSE market makers' inventory causes price pressure that reverts with a
  half-life of about a day.

## The signal: signed aggressor volume from the 1 s bars

**The proxy.**
- The buildup is the aggressor volume that the liquidity providers absorbed, signed.
- The 1 s bars (`data/intraday_1s_slim`) carry no aggressor side, so each bar is signed by the **tick rule** on its vwap:
  the sign of the change from the ticker's previous bar, with an unchanged vwap keeping the last sign.

**The validation** (`scripts/equity/buildup_validate.py` → `data/equity/buildup/validate.md`) is against the TRUE
aggressor side of the Databento MBO trade records, lit venues, on all 16 MBO name-days:

| | median of 16 | range |
|---|---|---|
| correlation per minute | 0.66 | 0.21 (CYTK) … 0.85 |
| over 15 min rolling | 0.75 | 0.17 (DDOG) … 0.92 |
| over 60 min rolling | 0.79 | 0.12 (DDOG) … 0.97 |

DDOG is the outlier, probably large off-exchange prints, which the 1 s bars include and the lit MBO does not.

**The scan** (`scripts/equity/buildup_scan.py` → `/mnt/d/trading-edge-bulk/buildup_5m/`, 14 GB):
- 2,582 days, 2016-08-09 → 2026-11-13, 33 min.
- 5-minute RTH bars: volume, signed volume, \$ volume, trades, and the first / last / high / low 1 s vwap.
- **The universe is causal:** the previous day's RTH \$ volume ≥ \$10M and its vwap ≥ \$1 (about 3,000 tickers a day,
  ETFs included).
- B = the window's signed volume as a % of the previous day's volume. R = the window's price move.
- Entry is at the next bar's first 1 s vwap. All forward returns are GROSS.

## 1. On ordinary bars the buildup predicts nothing (`buildup_report.py` → `data/equity/buildup/report.md`)

Over about 170M signal bars, the mean forward return across all 11 B bins is −0.3 … −2 bp, for windows of 15 / 30 / 60
min and holds of 30 min, 60 min, 120 min and to the close.
- The extreme bins (|B| > 10 % of the previous day's volume) differ from the middle by ≤ 1 bp.
- The year table is flat.
- The small negative everywhere is the measurement: the mean of log returns, vwap to vwap.

**There is no Hendershott–Menkveld reversal to trade in the broad market at intraday horizons.**

## 2. Inside BIG price moves the buildup matters (`buildup_events.py` → `data/equity/buildup/events.parquet`)

**The events:** the first 30-minute window of a ticker-day with |R| ≥ 300 bp, one per direction: 561k events. "Flow
confirmation" = the window's aggressor imbalance (signed volume / volume) in the direction of the move, in quintiles.
The table shows the fade's mean return to the close, in bp:

| move | q1 (weakest flow) | q2 | q3 | q4 | q5 (strongest flow) |
|---|---|---|---|---|---|
| UP 600–1000 bp (fade = short) | +55 | +37 | +23 | +6 | +15 |
| UP 1000–2000 bp | **+171** | **+180** | +102 | +87 | +35 |
| UP ≥ 2000 bp | +510 | +523 | +317 | +496 | +350 |
| DOWN 600–1000 bp (fade = long) | −15 | −59 | −50 | −31 | −12 |
| DOWN 1000–2000 bp | −79 | **−193** | −158 | −73 | **−25** |
| DOWN ≥ 2000 bp | +246 (n 92) | −404 | −447 | −254 | −41 |

**By year** (2016–2026, fade to the close, mean bp):
- **UP ≥ 1000 bp, weak flow (q1–q2):** +72 … +439, **positive in all 11 years**. It beats strong flow (q5) in 8 of 11.
  The medians are +163 against +83.
- **DOWN ≥ 600 bp:** fading the moderate-flow moves (q2–q3) loses in 10 of 11 years (−85 … −235). The strongest-selling
  moves (q5) sit near zero (−98 … +60, median +17) and **beat q2–q3 in all 11 years**.

## Findings

1. **The broad Hendershott–Menkveld reversal is absent intraday.** The buildup on an ordinary bar predicts nothing.
2. **On big moves the flow decides the reversal, in opposite ways on the two sides:**
   - **Up spikes:** WEAK buying flow reverts most. A price spike not backed by aggressive buying is a liquidity vacuum,
     and it gives back roughly twice as much as a strongly bought spike. This is a candidate FILTER for the spike-fade
     short systems (SpikeFader; shorts are on hold live while the CART issue is open).
   - **Down moves:** fading them (long) loses on average. It loses least, around break-even with a median of +17 bp, when
     the selling was the MOST aggressive. That is a capitulation the market makers absorbed, the buildup case. Moderate
     selling keeps going. This is a candidate filter for long flush-fades. ⚠ Steady path: no FlushFader tinkering, so it
     is a research note, not a change.
3. **Every number is gross**, on a broad universe that includes ETFs. A system needs costs, borrow, the existing
   systems' gates, and a lookahead audit before any claim.

⏭ The natural next test is whether flow confirmation adds to the PRODUCTION books: SpikeFader's spikes and FlushFader's
flushes, measured on their own trips, with the three controls.

## 3. The production books by the pre-signal order flow (user: "the signed order flow for 1200 bars prior to every signal")

**The method.**
- `scripts/equity/buildup_trips.py` takes every trip of the two reference books: FlushFader `broad_reference_book`
  (37,254 long fades of flushes) and SpikeFader `spikefader_reference_book` (3,067 short fades of spikes, 2020 → 2026-07).
- For each trip it reads the ticker's last 60 / 300 / 600 / 1,200 one-second bars up to and including the signal
  second, signed by the tick rule, and computes imb = signed volume / volume.
- "Move flow" is the imbalance in the direction of the faded move: selling for FlushFader, buying for SpikeFader.
- The report is `scripts/equity/buildup_trips_report.py` → `data/equity/buildup/trips_report.md`. The returns are the
  books' own `ret_exit`, at the production exits.

**Profit factor by move-flow quintile:**

| book | window | q1 (weakest) | q2 | q3 | q4 | q5 (strongest) |
|---|---|---|---|---|---|---|
| FlushFader (book PF 1.480) | 300 bars (~7 min) | **1.308** | 1.526 | 1.577 | 1.581 | 1.421 |
| FlushFader | 600 bars | **1.295** | 1.622 | 1.525 | 1.509 | 1.461 |
| SpikeFader (book PF 2.327) | 60 bars | 2.027 | 3.253 | 2.755 | 2.072 | **1.785** |
| SpikeFader | 1,200 bars (~23 min) | 2.693 | 2.187 | 2.656 | 2.797 | **1.629** |

**The worst quintile against the controls:**

| | FlushFader: the WEAKEST selling (q1, 300 bars) | SpikeFader: the STRONGEST buying (q5, 1,200 bars) |
|---|---|---|
| the book without it | PF 1.480 → **1.523** | PF 2.327 → **2.547** |
| random same-n removal (2,000 draws) | p50 1.479, p99 1.512, so beyond p99 | p50 2.320, p99 2.533, so beyond p99 |
| by year | worse in **7/7** (2020–2026) | worse in **6/7** (2022: n 43; 2023: n 27, an anecdote) |
| time of day (4 buckets) | worse in **all 4** | worse in **all 4** |
| corr with the existing features | ≤ 0.18 (crf −0.14, v10r +0.18, pf1_fitted −0.11) | ≤ 0.24 (volat_20m −0.24); roster weight 0.27 in q5 vs 0.34 in q1 (partly priced) |

**Findings:**
- **FlushFader: a flush WITHOUT aggressive selling behind it is the weak part of the book.** The weakest-selling fifth
  runs at PF 1.31 against about 1.53 for the rest, in every year and at every time of day. The sizing model does not
  price it (pf1_fitted is 0.52 in q1 against 0.45 in q5).
- **SpikeFader: a spike WITH strong aggressive buying behind it is the weak part of the book.** That is the buildup
  study's up-spike finding seen inside the production book.
- **Both say the same thing:** a fade works when the move was liquidity-driven, and fails when it was not:
  - a flush the market makers had to absorb real selling for;
  - a spike with no real buying behind it.
- The best quintiles are not monotone. The effect is one weak tail per book, so it is a FILTER or SIZING voice, not a
  ranking.
- ⚠ **Steady path.** These are research notes, not changes. Any adoption would mean a live-parity port of the 1 s
  tick-rule flow (the Oms already has the 1 s bars) and the user's decision.

## 4. Which trade classification? (`scripts/equity/buildup_validate2.py` → `data/equity/buildup/validate2.md`)

**The setup.**
- Scored against the TRUE aggressor side (Databento MBO, lit) on the 16 name-days.
- Minutes 1–388 only, because the opening and closing crosses are unsigned in the truth. A single auction print had
  dominated the first scoring.
- The NBBO was fetched per (ticker, date) by REST.

Median correlation over the name-days (the minimum in brackets):

| algorithm | prints | per minute | per minute, Spearman | 15 min | 60 min |
|---|---|---|---|---|---|
| tick rule on the 1 s bars (the current proxy) | all | 0.70 (0.22) | 0.67 | 0.71 | 0.79 (0.08) |
| tick rule, trade by trade | lit | 0.83 (0.48) | 0.79 | 0.85 | 0.93 (0.19) |
| **Lee–Ready** (the NBBO mid; the tick rule at the mid) | lit | **0.92 (0.68)** | 0.89 | **0.95** | **0.95 (0.51)** |
| EMO (at the ask / bid; the tick rule inside) | lit | 0.92 (0.64) | 0.90 | 0.95 | 0.94 (0.18) |
| quote rule only (mid trades unsigned) | lit | 0.94 (0.67) | 0.91 | 0.94 | 0.95 (0.73) |
| tick rule, trade by trade | all | 0.72 (0.11) | 0.66 | 0.74 | 0.85 |
| Lee–Ready | all | 0.79 (0.33) | 0.71 | 0.83 | 0.89 |

**Findings:**
- **Lee–Ready on lit prints is the best reconstruction:** 0.92 per minute and 0.95 over 60 min. The quote-only rule is
  as good and the most robust (its worst day is 0.73 at 60 min, against 0.51).
- **The 1 s bars lose mostly because they include the TRF prints, not because they are bars.**
  - The trade-level tick rule on ALL prints (0.72) is about the 1 s bars (0.70).
  - The same rule on LIT prints is 0.83.
  - The truth is lit-only, so part of the "all" gap is TRF flow that is real but unmeasured here. For a measure of what
    the exchange market makers absorbed, the lit prints are the right population anyway.
- **The cheap upgrade is the tick rule on lit prints:** the bulk tape is local, no quotes are needed, and it scores 0.83
  per minute and 0.93 over 60 min. Lee–Ready adds the rest (0.92 / 0.95), but needs the NBBO for every trip's window
  (REST, per trip).

## 5. The production books re-measured with the LIT trade-by-trade tick rule (`buildup_trips_lit.py`, 55 min)

**The method.** The same clock windows as §3, ending at the signal second. Only the classification changes. The new
measure correlates 0.50–0.69 with the 1 s-bar version, so it is a genuinely different measurement. Report:
`data/equity/buildup/trips_report_lit.md`.

**FlushFader: the effect HOLDS and is sharper at 600–1,200 lit bars.** PF of the WEAKEST-selling quintile against the
rest:

| window | q1 (1 s-bar tick) | q1 (lit trade tick) | the book without q1 (lit) | random same-n removal, p99 | q1 worse, by year |
|---|---|---|---|---|---|
| 300 bars | 1.308 | 1.298 | 1.523 | 1.510 | 6/7 |
| 600 bars | 1.295 | **1.260** | **1.534** | 1.515 | **7/7** |
| 1,200 bars | 1.407 | **1.305** | 1.523 | 1.510 | **7/7** |

**SpikeFader: the effect VANISHES.** On lit prints the strongest-buying quintile is no longer the worst (1,200 bars:
PF 2.19 against 2.16–2.50 for the others). The §3 SpikeFader result came from the TRF prints that the 1 s bars include.
That is off-exchange flow, which is mostly retail order flow internalized by wholesalers.

**Findings:**
- **FlushFader: a flush without real aggressive selling on the exchanges is the weak fifth of the book.** That is now
  confirmed with two different classifications. It is worse in 7 of 7 years at 600 and 1,200 lit bars, and beyond the
  99th percentile of random removal.
- **SpikeFader: the signal was off-exchange buying, not exchange buying.** The literature has a measure for exactly
  that: the BJZZ retail classification (Boehmer, Jones, Zhang & Zhang 2021). A TRF print with a sub-penny price just
  below a round cent is a retail BUY (the wholesaler gives price improvement), and one just above is a retail SELL. The
  natural follow-up is "do spikes driven by retail buying revert more?"

## 6. RETAIL flow by the BJZZ rule (`buildup_trips_retail.py`, 54 min)

**The rule.** Boehmer, Jones, Zhang & Zhang (2021): a TRF print at a sub-penny price is a wholesaler's fill of a retail
order. With Z = (100·price) mod 1, Z in (0.6, 1) is a retail BUY and Z in (0, 0.4) a retail SELL. Per window:
- the retail imbalance;
- the retail SHARE of all volume (lit + TRF).

The median retail share in the 1,200 bars before the signal is 24.5 % (SpikeFader) and 22.6 % (FlushFader).

**SpikeFader: weak.** The strongest retail-buying fifth has PF 1.87–2.02 at every window, against a book of 2.33. That
is the direction of §3's 1.63, but much smaller, and with about 614 trips per fifth it cannot be told from noise. The
§3 effect came mostly from other TRF flow (dark pools, blocks), or from chance. **Parked.**

**FlushFader: the RETAIL SHARE is the strongest flow measure yet.** PF by quintile of the retail share of volume:

| window | q1 (least retail) | q2 | q3 | q4 | q5 (most retail) |
|---|---|---|---|---|---|
| 300 bars | **1.16** | 1.41 | 1.60 | 1.69 | 1.56 |
| 600 bars | **1.11** | 1.42 | 1.60 | 1.68 | 1.61 |
| 1,200 bars | **1.06** | 1.47 | 1.58 | 1.65 | 1.67 |

The retail SELL imbalance points the same way at its strong end: the heaviest retail selling has PF 1.28–1.30, against
1.41–1.65.

**The least-retail fifth (600 bars) against the controls:**

| control | result |
|---|---|
| the book without it | 1.480 → **1.578** (random same-n removal p99 1.510) |
| by year, that fifth vs the rest | 2020 1.23 vs 1.96 · 2021 1.25 vs 1.74 · 2022 0.96 vs 1.43 · 2023 0.85 vs 1.34 · 2024 0.95 vs 1.47 · 2025 1.18 vs 1.43 · 2026 1.01 vs 1.57: **worse in 7/7, below 1.0 in 3** |
| time of day | worse in all 4 buckets (1.02–1.30 vs 1.49–1.71) |
| price (the share correlates −0.47 with price) | worse in all 5 price buckets (\$0–2 … \$20+: 1.05–1.34 vs 1.46–1.73) |
| the existing features | the sizing model PARTLY prices it (multiplier 0.79 in that fifth vs 0.98–1.09; pf1_fitted ρ +0.27); volat +0.31, lit flow −0.24 |
| the SIZED book (ret × multiplier) | PF 1.576 → **1.663** without it. That fifth carries 15.9 % of the sized notional, 4.3 % of the sized P&L, PF 1.147 |

**Finding.** A flush in which retail barely took part does not revert: PF about 1.1, at break-even or below in 2022–2024.
That is institutional, informed selling. A flush with normal-to-heavy retail participation is where FlushFader's edge
lives. This is the market-maker-buildup idea in its sharpest form: the reversal comes from price pressure by uninformed
flow, and retail flow is the cleanest observable of "uninformed".
⚠ **Steady path.** This is research; adopting it would mean a live-parity port of the TRF sub-penny classification and the
user's decision.

**User decision (2026-11-17).** Gate the bottom quintile of the **1,200-bar** retail share (< ~0.153; the gated book PF
1.592); maybe size the 2nd quintile (0.153–0.206, PF 1.469) down in the model. The work starts 11-18: controls on the 1,200
version, then the live-parity port.

## 7. LEE–READY on lit prints (`buildup_trips_lr.py`, 109 min; the NBBO by Massive REST per ticker-day window)

Every trip had a prevailing quote (median quoted share 100 %). FlushFader PF by flow quintile (q1 = the most net selling,
q5 = the most net buying), 37,254 trips, book PF 1.480:

| classification (1,200 bars) | q1 | q2 | q3 | q4 | q5 |
|---|---|---|---|---|---|
| tick rule on 1 s bars | 1.469 | 1.512 | 1.509 | 1.503 | 1.407 |
| tick rule on lit prints | 1.499 | 1.497 | 1.538 | 1.561 | **1.305** |
| Lee–Ready | 1.492 | 1.519 | 1.562 | 1.476 | **1.346** |
| quote-only (mid prints unsigned) | 1.462 | 1.544 | 1.552 | 1.498 | **1.341** |
| Lee–Ready, 600 bars | 1.420 | 1.573 | 1.693 | 1.454 | 1.280 |
| Lee–Ready, 300 bars | 1.494 | 1.432 | 1.642 | 1.542 | 1.299 |

Lee–Ready agrees with the lit tick rule (ρ 0.93) and adds nothing: its better accuracy against the MBO truth does not buy
a better split. Against the retail gate (Spearman −0.15 with rshare_1200):

| retail share < 0.153 | Lee–Ready q5 | trips | PF |
|---|---|---|---|
| no | no | 25,010 | 1.596 |
| yes | no | 4,793 | 1.096 |
| no | yes | 4,781 | 1.571 |
| yes | yes | 2,670 | 0.993 |

Among the retail-gate survivors the Lee–Ready q5 is worse in only **4 of 7 years** (2020 2.33 vs 1.95, 2024 1.52 vs 1.46 and
2026 1.92 vs 1.62 go the other way). **Verdict: the aggressor-flow effect is almost entirely the retail effect seen through
a noisier lens. No second gate, no sizing term.**
