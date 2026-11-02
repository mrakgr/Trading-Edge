# Swing MR — z scores and efficiency ratios on stocks above the 200d SMA (research journal)

Dataset: `scripts/equity/swingmr_build.py` → `data/equity/swingmr/rows` (one row per universe ticker-day). Check:
`scripts/equity/swingmr_check.py`. Report: `scripts/equity/swingmr_report.py` → `data/equity/swingmr/report.md`.

## SW-1 — the sampler and the first look (user 2026-11-02)

**The idea (user):** "revisit equity swing mean reversion and perhaps try using the efficiency ratio and z score on
stocks above the 200d MA and with the 60d median dollar volume above 1m ... I don't think we ever tried z scores and eff
ratios of varying lengths and thresholds." TideFlyer (2026-07) was a crash-capitulation washout book with no trend
filter; its prior-2-day gate removed exactly the uptrend-pullback cohort (TideFlyer Run 11). This is that cohort.

User rulings:
- the signal at D's close; entries recorded from BOTH D's close (live: a 15:59 decision) and D+1's open;
- a raw \$5 floor;
- fit 2005–2018, holdout 2019–2026.

**The universe** — every value knowable at D's close:
- close·n above its 200-session SMA (D included);
- the MEDIAN raw dollar volume of the PRIOR 60 sessions ≥ \$1M (D excluded; numpy, not a DuckDB window quantile);
- raw close ≥ \$5, CS / ADRC, 2005+;
- causal prices per `docs/price_adjustment.md`; episodes never share a window.

**The features:**
- z_N of ln(close·n) vs its last N closes, N = 3, 5, 10, 20, 50;
- the signed Kaufman efficiency er_N, N = 2, 3, 5, 10, 20;
- rsi2 (Wilder), the 1/3/5-day returns, dist200 / dist50, atr20, rvol.

**The outcomes**, dividends included:
- fixed holds of 1–10 days;
- x1 = the first close above the 5-day SMA (Connors);
- x2 = the first close above the previous day's high;
- x1 and x2 are capped at 10 days.

**Verified.** The check recomputes 229 rows (random ones, plus the AAPL 2020 4:1 and NVDA 2024 10:1 splits) with plain
loops from `daily_adjusted`: 7,314 values, 0 mismatches (tolerance 1e-6; pandas' rolling std is online, ~1e-8 noise).
The universe SET is exact for all 6 test tickers. Control: a 60-day median that includes D gives 132 mismatches (FAIL).

**The run.** 8,292,517 universe rows (2.3 GB), 2 min 39 s, 4.8 GB peak.

### Result: a real, stable edge — the stretched 20- and 50-day z scores

PF raw, x1 exit, entry at D's close (fit → **holdout**, holdout mean bp per trade; "−10" = holdout net of 10 bp per
round trip):

| cell | rows/yr holdout | x1 close entry | x1 next-open entry | −10 |
|---|---|---|---|---|
| every universe day (the control) | 395,081 | 1.17 → **1.13** (+15) | 1.13 → **1.07** | 1.04 |
| RSI(2) < 10 (Connors) | 35,619 | 1.37 → **1.26** (+40) | 1.32 → **1.23** | 1.19 |
| RSI(2) < 5 | 17,124 | 1.41 → **1.31** (+48) | 1.35 → **1.26** | 1.24 |
| z_10 bottom 5 % (≤ −1.75) | 19,291 | 1.39 → **1.28** (+46) | 1.34 → **1.26** | 1.22 |
| ⭐ z_20 bottom 2 % (≤ −2.19) | 7,313 | 1.49 → **1.38** (+61) | 1.43 → **1.34** | 1.31 |
| ⭐ z_20 bottom 1 % (≤ −2.48) | 3,452 | 1.49 → **1.38** (+64) | 1.42 → **1.33** | 1.32 |
| ⭐ z_50 bottom 1 % (≤ −2.26) | 3,422 | 1.67 → **1.40** (+63) | 1.61 → **1.37** | 1.33 |
| er_5 bottom 1 % (≤ −1: five straight down closes) | 7,215 | 1.39 → **1.33** (+48) | 1.33 → **1.29** | 1.25 |
| z_10 bottom 10 % × er_20 bottom 10 % | 10,972 | 1.50 → **1.36** | | |
| z_50 bottom 10 % × er_5 bottom 10 % | 11,164 | 1.47 → **1.35** | | |

- **Every lever ranks the same way in both periods.** Above the 200d SMA, a stretched dip reverts. The control (every
  universe day, 1.13) is already positive: an uptrend universe plus an exit that waits for an up close. Read every
  cell against it.
- **The longer z is the lever.** z_3 / z_5 stay at 1.17–1.21 at every depth. z_10 peaks at 1.28 (bottom 5 %). z_20 and
  z_50 keep climbing to the 1 % tail (1.38–1.40 holdout, +61–64 bp per trade).
- **The efficiency ratio helps at short lengths:** er_5 ≤ −0.98 (2 %) gives 1.32; er_10 / er_20 add less alone
  (1.21–1.29). As a second gate it lifts a z cell by about 0.05–0.10 (the best 1.36). er_2 is discrete (−1 = two down
  closes for 20 % of rows).
- **The open entry keeps most of it** (1.33–1.37 vs 1.38–1.40): the edge is not only the overnight.
- **⚠ The bad years are the market's.** For the top three combinations by FIT PF (z_20 × er_20, z_20 × er_10,
  z_50 × er_10):
  - the bad years are 2008 (1.07–1.09), 2020 (0.65–0.79), 2018 (1.04–1.22) and 2022 (0.97–1.07);
  - every other year is 1.11–2.63;
  - the control is under 1 in the same years (2008 0.83, 2011 0.97, 2018 0.88, 2022 0.86).

  A market-regime gate is the natural next lever. The "above its own 200d" filter does not stop a stock-level dip in a
  falling market.
- ⚠ **These are SAMPLER rows, not a book.** The same name on consecutive days counts each day, and nothing caps the
  positions held at once. The trade counts, the drawdown and the concurrency are open.

## SW-2 — the market-breadth gate and the distance from the moving averages (user 2026-11-02)

**The ideas (user):** "market breadth works better than other measures ... how many stocks in our dataset that pass the
liquidity filters are above their 20d MA in percentage terms", and "Distance from the 72h MA worked really well in crypto
perpetuals on the long side ... try a variety of them".

The build now writes `data/equity/swingmr/breadth.parquet`, one row per date. Of the LIQUID names that day (med60 ≥ \$1M,
≥ \$5, any trend), it holds the share closing above their 20 / 50 / 200-session SMA, and the previous day's values. It
also writes ma_N = cn ÷ the N-session SMA − 1, N = 3 … 200 (% distance, not volatility-scaled). The check now also
recomputes every ma_N, the exit dates, and the breadth counts on all 5,763 dates of the test build: 8,917 values,
0 mismatches. Control: a 21-bar breadth SMA gives 617 dates differing (FAIL). (A `>` → `≥` control was INERT: a close
never equals its SMA exactly.) Script: `scripts/equity/swingmr_report2.py` → `data/equity/swingmr/report2.md`.

### Breadth is not a "strong market" gate; the danger is a CRASH, and the best context is moderate weakness

x1 close-entry PF raw, fit → **holdout** (holdout mean bp):

| setup | br20 0–20 % | 20–40 % | 40–60 % | 60–80 % |
|---|---|---|---|---|
| z_20 bottom 2 % | 2.49 → **1.49** (+82) | 1.37 → **1.54** (+80) | 1.22 → **1.25** (+42) | 1.08 → **1.16** (+26) |
| z_50 bottom 1 % | 2.59 → **1.09** (+23) | 1.51 → **1.90** (+109) | 1.29 → **1.26** (+42) | 1.26 → **1.33** (+46) |

- A dip while the market is moderately WEAK (20–40 % above the 20d SMA) is the best cell in both periods. In strong
  markets (60–80 %) the edge thins.
- **At the extreme the two periods disagree.** br50 < 20 % is the fit's best cell (2.78 / 2.95: the 2009 capitulation
  bottoms) and the holdout's worst (0.91 / 0.72: 2020, 2022). It is TideFlyer's crash problem: the same reading is a
  capitulation bottom or a falling knife, and a level cannot tell them apart.
- **br200 high is better in the holdout** (80–100 %: z_20 2.47, z_50 3.38, on 300–660 rows/yr), but the fit is flat.
- **No simple breadth floor fixes the bad years.** By year, br20 ≥ 40 %, br50 ≥ 50 % or br200 ≥ 50 % all leave 2020
  at 0.83–1.09 and 2022 at 0.21–1.01, and give up good low-breadth years.

### The % distance from an MA is weaker than z; as a second gate the long MAs help a little

- Alone, ma_N's bottom tails are 1.16–1.23 holdout for every N (3, 5, 10, 20, 50, 100), against z_20 / z_50's
  1.38–1.40. The volatility scaling in z is the lever. The short MAs (3 / 5 days, the crypto 72 h analogue) do not stand
  out.
- On top of z_20 bottom 2 %: + ma_100 bottom 5 % gives 1.42 → **1.51** (+96 bp, 1,686 rows/yr); + ma_50 bottom 5 %
  gives 1.60 → **1.43**; the short MAs add nothing.

## SW-3 — the BOOK: one position per name, K slots, equal size, marked to market (user 2026-11-02)

`scripts/equity/swingmr_book.py`:
- **Entry and exit:** entry at D's close, the x1 exit, one position per name.
- **Slots:** at most K positions. When candidates outnumber the free slots, the lowest `--rank` goes first.
- **Size and cost:** each position is 1/K of the REALIZED equity at entry, and 10 bp per round trip.
- **Marking:** every open position is marked at every close (daily_episodes_causal; dividends as cash), so the drawdown
  includes open positions.

The setup averages 31 signals a day (median 14), so with 10–20 slots THE RANKING DECIDES THE BOOK.

**The first books ranked by the most negative z, and that selected the KNIVES.** Within z_20 ≤ −2.19:
- **by depth:** −2.19…−2.5 gives 1.51 → 1.38; −2.5…−3.5 gives 1.44–1.51 → 1.37–1.41; **−3.5…−4.25 gives 1.10 →
  0.86** (a 20-day z cannot go below −√19 = −4.36);
- **by the day's own return:** −10…−20 % gives 1.19 → 1.08 (news).

So z_20, K = 10, net PF was 1.02, max drawdown −77 %, and the worst trade −79 %.

**⭐ Liquidity is the strongest lever inside the setup** (visible in the fit too). By med60:
- \$1–5M: 1.49 → 1.19 (+38 bp);
- \$5–20M: 1.38 → 1.33;
- \$20–100M: 1.51 → 1.38;
- **≥ \$100M: 1.80 → 1.68 (+84 bp)**.

The books, ranked by the MOST LIQUID first (`--rank=-med60`), excluding z < −3.5 and a day's drop worse than −10 %
(both fit-visible), 10 bp per round trip:

| book | trades/yr | win | PF net | mean net | CAGR | max DD (marked to market) | losing years |
|---|---|---|---|---|---|---|---|
| z_20 ≤ −2.19, K = 10 | 492 | 62.6 % | 1.06 | +14.5 bp | +5.3 % | −61.2 % (2020-03) | 8 of 22 |
| z_20 ≤ −2.19, K = 20 | 885 | 62.8 % | 1.09 | +17.3 bp | +6.6 % | −56.3 % | 9 of 22 |
| z_20 ≤ −2.19, med60 ≥ \$100M, K = 10 | 288 | 62.8 % | 1.12 | +21.1 bp | +5.5 % | −30.4 % (2023-08) | 6 of 22 |
| ⭐ **z_50 ≤ −2.26, K = 10** | **434** | **65.3 %** | **1.23** | **+35.5 bp** | **+15.2 %** | **−43.7 % (2020-03)** | **2 of 22** |

The ⭐ z_50 book by year:
- 2005 +51 % · 2006 +51 % · 2007 +3 % · 2008 +9 % · 2009 +16 % · 2010 +15 % · 2011 +3 % · 2012 +5 % · 2013 +33 % ·
  2014 +12 % · 2015 +19 % · 2016 +10 % · 2017 +19 %;
- **2018 −8 %** · 2019 +11 % · **2020 −19 %** · 2021 +57 % · 2022 +23 % · 2023 +6 % · 2024 +22 % · 2025 +16 % ·
  2026 (to 10-30) +30 %;
- 6.7 positions open on average, of 10.

- **The z_50 book holds up where the z_20 one does not.** The sampler PF looked alike (1.38 vs 1.40), but with
  concurrency, the ranking and the costs, z_20's bulk thins to 1.06–1.12. z_50's survives at 1.23. Its holdout years
  2019–26 are all positive except 2020.
- **The drawdown is the COVID crash** (−43.7 % on 2020-03-18). At 10 % a position, a market-wide crash takes the whole
  book. A crash brake (the br50 < 20 % knife of SW-2) and position sizing are the next levers. The −3.5 / −10 %
  exclusions were checked on z_20 and borrowed for z_50.
- ⚠ Not yet modelled: the 15:59 decision vs the official close, the fill of a MOC entry, borrow-free longs only (fine),
  and the slippage of 434 trades/yr at \$100M+ names (small at 10 bp?).

## SW-4 — the volume on the signal day: QUIET declines win, LOUD ones are news (user 2026-11-02)

**The user's question:** "This profit factor is honestly pathetic. The net per trade is less than for our intraday system.
What if we filter our decliners on strong volume? Strong volume usually means news." rvol = D's dollar volume ÷ the
prior 20 sessions' mean.

Within the z_50 book setup (z_50 ≤ −2.26, > −3.5, ret_1 > −10 %), x1 close-entry PF raw, fit → **holdout** (holdout mean
bp):

| rvol | all liquidity | med60 ≥ \$100M | share (≥ \$100M) |
|---|---|---|---|
| < 0.75 | 1.59 → **1.53** (+74) | 2.97 → **2.42** (+129) | 11 % |
| 0.75 – 1 | 1.86 → **1.48** (+67) | 2.30 → **1.89** (+83) | 23 % |
| 1 – 1.5 | 1.74 → **1.34** (+53) | 2.16 → **1.53** (+68) | 41 % |
| 1.5 – 2 | 1.82 → **1.46** (+71) | 1.87 → **1.77** (+94) | 15 % |
| 2 – 3 | 1.46 → **1.32** (+55) | 1.48 → **1.57** (+79) | 7 % |
| ≥ 3 | 1.32 → **1.24** (+44) | 0.93 → **1.15** (+26) | 2 % |

- **The user's intuition holds at the loud end** (≥ 2–3× is the weakest, and on large caps the fit is 0.93: news).
  **The bigger lever is the QUIET end:** on the liquid names the fit ladder is monotone (2.97 → 0.93), and a stretched
  dip on a below-average-volume day is 2.42 in the holdout. That matches TideFlyer's finding (moderate volume wins; a
  high-volume breakdown is a catalyst that keeps falling). z_20 shows the same loud tail (≥ 3×: 1.14 → 1.24).

The books (ranked by the most liquid first, 10 bp per round trip, marked to market):

| book | trades/yr | win | PF net | mean net | CAGR | max DD | slots used | losing years |
|---|---|---|---|---|---|---|---|---|
| SW-3 z_50, K = 10 | 434 | 65.3 % | 1.23 | +35.5 bp | +15.2 % | −43.7 % | 6.7 / 10 | 2 / 22 |
| + rvol < 2 | 423 | 65.5 % | 1.24 | +35.5 bp | +14.9 % | −39.7 % | 6.3 / 10 | 2 / 22 |
| + med60 ≥ \$100M, rvol < 1.5 | 160 | 68.4 % | 1.44 | +51.7 bp | +8.2 % | −29.6 % | 2.4 / 10 | 2 / 22 |
| ⭐ **+ med60 ≥ \$100M, rvol < 1.0, K = 10** | **108** | **71.3 %** | **1.81** | **+75.8 bp** | **+8.3 %** | **−17.5 %** | **1.4 / 10** | **1 / 22** |
| the same, K = 5 | 84 | 70.2 % | 1.65 | +61.7 bp | +10.6 % | −27.5 % | 1.1 / 5 | 2 / 22 |

The ⭐ book by year:
- 2005 +6 % · 2006 +3 % · 2007 +10 % · 2008 +10 % · 2009 +8 % · 2010 +10 % · 2011 +0.4 % · 2012 +3 % · 2013 +4 % ·
  2014 +9 % · 2015 +5 % · 2016 +4 % · 2017 +13 % · 2018 +14 %;
- 2019 +9 % · **2020 −0.6 %** · 2021 +12 % · 2022 +2 % · 2023 +6 % · 2024 +12 % · 2025 +30 % · 2026 (to 10-30) +28 %.

- **Per trade it is now a different system:** +76 bp net, 71 % win, PF 1.81 net, and 2008 / 2020 / 2022 all
  non-negative. It survived COVID at −17.5 %.
- **The cost is CAPACITY.** 108 trades a year use 1.4 of 10 slots on average: the book is in cash most of the time,
  hence 8 % CAGR at 10 % a position. It is a SATELLITE alongside the intraday systems, or it takes a larger size per
  position. Sizing is the open question, and 2020's −17.5 % is at 10 % a slot.
- ⚠ **Selection.** Both cuts (med60 ≥ \$100M, rvol < 1) are monotone in the FIT, but they were chosen today after many
  tables. The holdout cells (2.42 / 1.89) are read alongside, not untouched. The −3.5 / −10 % exclusions came from z_20.

## SW-5 — volume < 1.5×, the crash knives, and relaxing the other filters (user 2026-11-02)

**The user's questions:** "What if we used volume < 1.5x? ... Once we put volume < 1.5x we could look into relaxing some
of the other features. I think thanks to volume we won't need to worry so much about crash knives. It's worth testing
whether that's true." (Also: "filtered our" in SW-4 meant "filtered OUT".)

**⭐ The user is right: the knives are LOUD.** z_50 ≤ −2.26, x1 close-entry PF raw, fit → **holdout** (holdout mean bp):

| bucket | rvol < 1.5 | rvol ≥ 1.5 |
|---|---|---|
| z_50 ≤ −3.5 (the extreme) | 1.90 → **1.27** (+59) | 1.42 → **1.09** (+21) |
| ret_1 −10 … −20 % | 1.70 → **1.60** (+228) | 1.11 → **1.13** (+39) |
| med60 ≥ \$100M, z_50 ≤ −3.5 | 5.75 → **3.14** (+190) | 1.65 → **2.24** (+139) |
| med60 ≥ \$100M, ret_1 ≤ −10 % | 1.46 → **3.98** (+629) | 0.88 → **1.89** (+199) |

A quiet −15 % day reverts; a loud one is news. The SW-3 exclusions (z > −3.5, ret_1 > −10 %) are REDUNDANT under the
volume filter: the \$100M + rvol < 1.5 book is 160 vs 161 trades/yr, PF net 1.44 either way, and rvol < 1 is PF 1.81
either way.

**Relaxing the other filters trades quality for count, nearly one for one** (K = 10, the most liquid first, 10 bp, no
exclusions):

| book | trades/yr | win | PF net | net/trade | CAGR | max DD | losing years |
|---|---|---|---|---|---|---|---|
| ⭐ **z_50 ≤ −2.26, med60 ≥ \$100M, rvol < 1** | **108** | **71.2 %** | **1.81** | **+77.0 bp** | **+8.5 %** | **−16.6 %** | **1 (2020 −0.6 %)** |
| z_50 ≤ −2.26, ≥ \$100M, rvol < 1.5 | 161 | 68.4 % | 1.44 | +52.4 bp | +8.3 % | −29.6 % | 2 |
| z_50 ≤ −2.26, ≥ \$20M, rvol < 0.75 | 133 | 70.4 % | 1.58 | +61.4 bp | +8.3 % | −18.1 % | 3 |
| z_50 ≤ −2.26, ≥ \$20M, rvol < 1 | 226 | 68.3 % | 1.39 | +43.9 bp | +9.9 % | −29.4 % | 3 |
| z_50 ≤ −2.0, ≥ \$100M, rvol < 1 | 181 | 69.0 % | 1.36 | +47.5 bp | +8.5 % | −32.3 % | 2 |
| z_50 ≤ −2.26, any liquidity, rvol < 1 | 351 | 66.8 % | 1.29 | +40.7 bp | +14.2 % | −43.8 % | 2 |
| z_50 ≤ −2.26, ≥ \$20M, rvol < 1.5 | 290 | 66.2 % | 1.26 | +36.5 bp | +10.3 % | −36.2 % | 3 |
| z_50 ≤ −2.26, any liquidity, rvol < 1.5 | 410 | 65.6 % | 1.22 | +33.5 bp | +13.4 % | −44.0 % | 3 |
| z_20 ≤ −2.19, ≥ \$100M, rvol < 1 | 153 | 66.0 % | 1.16 | +28.5 bp | +4.0 % | −38.8 % | 7 |
| z_50 OR z_20, ≥ \$100M, rvol < 1 | 199 | 67.3 % | 1.21 | +33.6 bp | +6.3 % | −32.9 % | 5 |
| z_50 OR z_20 OR er_5 ≤ −0.98, ≥ \$100M, rvol < 1 | 333 | 67.8 % | 1.14 | +23.0 bp | +7.1 % | −33.0 % | 6 |

- **Every relaxation dilutes** — liquidity (\$100M → \$20M → any), depth (z_50 −2.26 → −2.0 → −1.75), volume (< 1 → < 1.5)
  and extra signals (z_20, er_5) alike. The edge is CONCENTRATED: a 50-day-stretched dip, on a quiet day, in a large
  liquid name.
- **The 20-day z does not carry over** under the same filters (1.16, 7 losing years). In SW-1's sampler it looked
  equal to z_50 (1.38 vs 1.40); in every book it is the weaker signal.
- **Capacity is the cost.** The ⭐ book is in a position 15 % of the time (1.5 of 10 slots). More trades come only at a
  lower PF. The capacity question is sizing, not relaxing the filters.

**THE CANDIDATE SPEC** — all at D's close:
- close above the 200-session SMA;
- the prior 60 sessions' median dollar volume ≥ \$100M;
- raw close ≥ \$5;
- z_50 ≤ −2.26 (the fit's bottom 1 %);
- D's dollar volume < its prior 20-day mean.

Enter at the close; exit at the first close above the 5-day SMA (≤ 10 days); the most liquid first if more than K.

## SW-6 — ranking inside the tight tier by the distance from the MAs (user 2026-11-02)

**The user's question:** "Would it be possible to use the distance from MA to rank the trades within the ⭐ tight tier?"

The tier is z_50 ≤ −2.26, med60 ≥ \$100M, rvol < 1: 2,211 fit / 2,352 holdout rows.

**Inside the tier, by the FIT quintiles** (x1 close-entry PF raw, fit → **holdout**, holdout mean bp):

| distance | Q1 (most stretched) | Q2 | Q3 | Q4 | Q5 |
|---|---|---|---|---|---|
| ma_5 | 2.93 → **2.81** (+238) | 2.43 → 1.42 | 1.95 → 1.73 | 2.70 → 1.88 | 2.34 → 1.43 |
| ma_20 | 3.05 → **2.75** (+233) | 2.44 → 1.30 | 1.99 → 1.76 | 2.19 → 2.08 | 2.58 → 1.64 |
| ma_50 | 2.68 → **3.02** (+250) | 3.08 → 1.09 | 1.75 → 2.21 | 2.52 → 1.55 | 2.57 → 1.52 |
| ma_100 | 2.57 → **2.99** (+230) | 1.89 → 1.34 | 2.55 → 1.73 | 3.13 → 1.82 | 2.92 → 1.77 |
| ma_200 | 1.80 → 1.70 (+59) | 2.94 → 1.47 | 2.54 → 1.47 | 3.63 → 2.15 | 2.27 → **3.16** (+201) |

- **The most stretched fifth below the 5–100-day MA is the tier's best cell in both periods** (holdout 2.75–3.02,
  +230–250 bp, against roughly +30–75 for the rest). The other quintiles are noisy (about 470 rows each).
- **ma_200 points the other way.** The names FARTHEST above their 200-day SMA (the strongest uptrends) are the best,
  2.27 → 3.16.

**In the books** the ranking only acts when the slots bind. The tier averages 2.5 signals on a signal day (90th
percentile 5, max 24); at K = 10 it uses 1.5 slots, so the ranking never decides. At K = 3 and 5 (lowest rank first,
10 bp):

| K | rank | trades/yr | PF net | net/trade | CAGR | max DD | losing years |
|---|---|---|---|---|---|---|---|
| 3 | the most liquid (−med60) | 66 | 1.50 | +52.3 bp | +11.4 % | −24.2 % | 5 |
| 3 | ⭐ ma_50 (the most stretched) | 66 | **1.67** | +62.2 bp | +13.8 % | −24.0 % | 2 |
| 3 | z_50 | 65 | 1.56 | +51.5 bp | +11.1 % | −24.2 % | 3 |
| 3 | ma_5 / ma_20 / −ma_200 | 65 | 1.49–1.55 | +54–56 bp | +11.7–12.1 % | −28.4 … −30.8 % | 2–3 |
| 5 | the most liquid (−med60) | 85 | 1.64 | +62.5 bp | +10.7 % | −26.0 % | 2 |
| 5 | ⭐ ma_50 | 85 | **1.75** | +69.6 bp | +12.1 % | −26.7 % | 2 |
| 5 | z_50 / −ma_200 | 84–85 | 1.68–1.70 | +64 bp | +10.9–11.1 % | −26 % | 1–2 |
| 5 | ma_5 / ma_20 | 84 | 1.60–1.61 | +63–65 bp | +10.7–10.9 % | −28.5 … −29.2 % | 2 |
| 10 | (anything: the slots never bind) | 108 | 1.81 | +77.0 bp | +8.5 % | −16.6 % | 1 |

- **ma_50 is the best ranking at both K** (+0.11–0.17 PF over the liquidity order). It is the quintile table's
  strongest Q1 (2.68 → 3.02). The margin is small next to the noise of about 65 trades a year.
- **⭐ A small K COSTS more than any ranking gains.** K = 10's PF 1.81 beats every K = 3 / K = 5 ranking (1.49–1.75).
  The trades lost when the slots bind are the cluster days, many quiet dips at once, and those are good trades.
  Capacity, again, is the binding constraint, not selection.

## SW-7 — sizing up the strong cells (user 2026-11-02: "By ranking I really meant whether there are any quantiles we could size up on ... q1 for the ma_5 and q5 for the ma_200")

**K, for the record:** the book's number of slots. At most K positions are open, and each is 1/K of the realized
equity, so K = 10 = 10 % per position. In this tier the slots almost never fill, so K = 10 simply means 10 % a trade.

**The cells**, disjoint, on the tier's FIT edges: ma_5 Q1 = at least 3.34 % below the 5-day SMA; ma_200 Q5 = at least
8.06 % above the 200-day SMA. PF − 1 of the x1 close entry, raw:

| cell | fit rows | fit PF−1 | fit PF−1, worst 5 % trimmed | holdout PF−1 | holdout trimmed | holdout mean bp |
|---|---|---|---|---|---|---|
| base (neither) | 1,479 | 1.48 | 5.36 | 0.48 | 1.98 | +38 |
| ma_5 Q1 only | 289 | **1.94** | 5.03 | **1.02** | 2.92 | +151 |
| ma_200 Q5 only | 289 | **0.74** | 3.47 | 0.93 | 3.78 | +71 |
| both | 154 | 1.93 | 7.72 | 3.11 | 9.27 | +343 |

- **ma_5 Q1 is better in both periods (raw)**, but on the trimmed body it matches the base in the fit (5.03 vs 5.36).
  Its edge is the SIZE of the moves (+151 vs +38 bp), not a better ratio of wins to losses.
- **ma_200 Q5 is holdout-only:** alone it is WORSE than the base in the fit (0.74 vs 1.48). Sizing up on it would be
  choosing on the holdout.

**The books** (`--size`: position = size × equity ÷ K; `--max-gross`: the open notionals (at entry) are capped at that ×
the realized equity, and a new position is cut to the room left). Marked to market, 10 bp:

| size (× 10 %) | max gross | PF net | CAGR | max DD | CAGR ÷ \|DD\| | losing years |
|---|---|---|---|---|---|---|
| 1 (equal) | 1× | 1.81 | +8.5 % | −16.6 % | 0.51 | 0 |
| 1.25 (equal) | 1× | 1.77 | +9.4 % | −20.3 % | 0.46 | 1 |
| ma_5 Q1 ×2, else 1 | 1× | 1.90 | +10.7 % | −22.9 % | 0.47 | 1 |
| 2 (equal) | 2× | 1.81 | +17.2 % | −32.6 % | **0.53** | 2 (−0.1 / −0.6 %) |
| 2.5 (equal) | 2× | 1.79 | +18.9 % | −39.7 % | 0.48 | 1 |
| ma_5 Q1 ×4, else 2 | 2× | 2.01 | +21.6 % | −44.5 % | 0.49 | 1 |
| ma_5 Q1 ×6, else 2 | 2× | 2.07 | +22.8 % | −51.3 % | 0.44 | 5 |

- **Sizing up ma_5 Q1 raises the PF (1.81 → 1.90–2.07) and the CAGR, but the drawdown grows just as fast.** The return
  per unit of drawdown stays at 0.44–0.53 in every variant, and EQUAL weight at 2 × 10 % is the best of them (0.53). It is
  the trimmed table's message in book form: the ma_5 Q1 trades are bigger moves in BOTH directions, and their losses land
  in the same crash (2020-03). Sizing them up is leverage, not selection.
- ⚠ The cap acts at entry against the REALIZED equity; the marked-to-market gross can still exceed it in a crash
  (274 % of the marked equity in 2020-03 at a 2× cap).
