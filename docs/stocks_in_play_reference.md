# Overview

The reference document for the stocks in play that will serve as the basis for the generative models. Here is a quick conversion script that should be run from the parent directory in order to convert this markdown file into .html for viewing in the browser.

```bash
pip install markdown
python3 -c "import markdown; print(markdown.markdown(open('docs/stocks_in_play_reference.md').read(), extensions=['extra', 'nl2br']))" > docs/stocks_in_play_reference.html
```

## What it has (reference characteristics):

- Historical examples of stocks that moved
- Intraday fundamentals (gap %, RVOL, volume, news)
- Charts showing what happened

## What it lacks (playbook characteristics):

- Entry criteria and triggers
- Stop loss placement rules
- Position sizing guidelines
- Exit strategies
- Risk management rules
- Specific patterns to trade (e.g., "bull flag breakout", "VWAP reclaim")
- Trade management rules (scaling in/out, trailing stops)

Those will be up to the actual automated systems that I will create. This document is intended to be representative of the kinds of stocks I will be trading in the future. You'll note these aren't random stocks, but the most volatile opportunities on any given day.

# Grading Criteria

These are adopted from SMB Capital. They use 4 criteria to judge the quality of their setups + 3 more criteria related to trade management, which we aren't going to be using in this document because we'll have our trades managed by computer agents. We'll be reviewing tier effectiveness, but that process will be different from discretionary decisions of human agents.

An additional criteria they mention is Intuition, but we'll skip that one.

The 4 criteria are:

* Big Picture

All the stocks in the market are correlated with each other. During market panics for example everything goes down all at once, and during bull markets the saying goes that the rising tide lifts all boats. In my experience though, bull markets don't really trend as well as one would hope, so we're going to downgrade the importance of this one relative to the rest. If we were swing trading for example, it would be particularly important that we're doing so on a rising market, but since we're daytrading, it's really important that we don't look for bullish setups when the market is in the middle of a panic. So I am not going to care if the market is down -1 to 1%. What's really important is that it's not spiking lower sharply by 2%. Volatility generally clusters, so market panics require a few weeks to shake off. The rule will be to turn bullish two weeks after a bottom.

The other aspect of the big picture is knowing what themes and sectors (as opposed to individual stocks) are in play, and what kind of news the market reacts to dramatically, either on the long or the short side. We should be trading stocks in play that are related to a particular market theme.

* Intraday Fundamentals

Intraday fundamentals primarily look at a stock's volume relative to its past volume, as well as the catalyst which is a piece of news that had come out recently. By catalyst, we mean a piece of news that reveals a sudden change in the fundamentals of a company. For example large increases in earnings for the year, increases in margin, increases in sales all of which should be beyond expectations either on the up or the downside. It can also be large deals that would show up on the balance sheet in the next report as positive revenue.

A strong catalyst leads to abnormal volume which leads to large trending moves during the day.

This just by itself gives us an edge.

* Technical Analysis

Is primarily concerned with key longer term levels. If we want to be going long, we need to know about whether the price will be running into longer term resistance or whether it is making all time highs. Having no supply ahead is a bullish sign, and the same principle applies on the short side. If we're shorting a stock, it's better if there is no long term support where the buyers could come in to spoil the fun.

The longer term 3-9 momentum is also a factor worth considering.

* Tape Reading

SMB's traders read the time & sales and the order book to hone their entries and exits. We're going to be substituting this with volume charts.

In terms of importance, the intraday fundamentals are the most important followed by tape reading, then the big picture and lastly technical analysis. We won't be doing tape reading, but order flow analysis in fact using volume charts, so we'll just call it volume analysis or orderflow analysis.

The importance of the big picture or techical analysis depends on how dominant they are relative to the setup we're trading.

Let's say we're trading a stock with no news or abnormal volume, in other words, a stock that has weak intraday fundamentals. Then the market action and it's own technicals will be much more dominant in the evaluation of the setup.

Ideally, we should be trading stocks when their intraday fundamentals are the dominant factors, and let the big picture play into that. So when we're evaluating a stock, the focus should be not on just noting down how the market is acting, but how is its action playing into the stock's intraday fundamentals.

As for longer term support and resistance and momentum, that isn't necessarily what is important to us.

Rather what we want to see on a chart is that the stock is very volatile and bipolar, with extremes in volume. Something that indicates that it has potential to both go up and down a lot. We also need to look at the longer term chart to get a sense for liquidity.

In our strategy we'd ideally trade low flows with high short interest in order to catch explosive moves, and they have certain sorts of price action that large caps don't. The chart should be confirming that.

# List

The charts are volume charts. Unlike regular time based charts, these volume charts are built by grouping the trades into fixed size blocks and calculating their Volume Weighted Average Price and the Volume Weighted Standard Deviation. I found this to make analysis far easier than with time based charts. The bottom panel on the charts is the trade duration. When a trade goes over the block size limit, it is split and its remainder is passed into the next block. For large blocks this might entail splitting it up multiple times until the entire trade is consumed.

## Ticker: LW Date: 2025-12-19

### Big Picture

Rangebound price action in the market for the past month. Not related to the dominant themes of the day which are AI and crypto related. The company deals in frozen potato products. Irrelevant to this setup.

### Intraday Fundamentals

Avg. Volume: 1.35m
Float: 136.76m
Short %: 15.3% (relatively high)
Gap %: -14% (large)
Premarket volume: 330k (24% avg, 0.24% float)
Catalyst: 4/10 (weak)

Minor analyst beat on earnings release that had a massively negative reaction from the market possibly due to the analyst questions. A **Changing Fundamentals** play.

Quality: 7.5/10.

The premarket volume is there, but the catalyst is weak, liquidity is low and the float is high.

### Technical Analysis

Support: 47, 49.

<iframe src="charts/LW_2025-12-19_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/LW_2025-12-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The open was delayed for 2m, but the volume came in strong after that. The opening prints were block trades totaling around 600k which was followed by a hold above 50, during which laster 100k on the volume chart. The volume was very fast. After which there was a breakout below 50 that never looked back and the stock trended for the rest of the day.

A noteworthy event was an unusual hold at 47 that lasted for 300k shares of volume. There was a fakeout above that level after it broke down, but a second short would have caught the majority move down.

All in all, this was a great trend trade throughout the day. When the volume was fast during morning, the trending action was in progress, and there was a counter trend consolidation when the volume was slow. Towards the end it speed up again.

The 3 major entry opportunities were near the open after the hold above 50 broke, below 47 after the unusual hold was broken the second time, and once the volume's pace picked up again an hour before the close.

I will certainly be looking that my systems enter during those periods, but exits will be harder and less certain. Two possible exits are during midday when the volume slowed and near the close. The stock had a few counter trend rallies during this period, but they weren't out of the ordinary.

### Extra

LW is noteworthy to me because it is the stock I created the volume charts for the first time. The candlestick charts for it are very noisy, but the key levels and holds are visible to the naked eye on the volume chart. It's not possible to see on the chart, but the stock had very high bid/ask spreads during the day. Near the open they were as much as 5-10%. This created large spikes on the OHLC candlestick chart from the occassional market orders hitting the ask which motivated the development of volume charts.

Initially, I wanted to learn tape reading, but the LW's open had 800 transactions in a 3s span, which made me realize that there are a lot of situations where it would be outright impossible to read the tape for a human. Though my initial intetion was to throw away charts, the solution was always to find a middle ground between tape reading and regular time based charts. Volume charts are that middle ground. May they serve us well.

## Ticker: NBIS Date: 2025-09-10

### Big Picture

Rangebound indices for the past month. AI mania is in progress and this is an AI cloud stock so news can be expected to cause large reactions.

### Intraday Fundamentals

Avg. Volume: 5.8m
Float: 202.8m
Short %: 22.3% (high)
Gap %: -4.3% (small)
Premarket volume: 3.9m (75% avg, 1.9% float)
Catalyst: 10/10 (best)

The catalyst is a 17b AI deal with Microsoft which is huge for a company that had a market cap less than that amount when it was announced. This was 2 days ago, so this is a **Second Day** play on this stock.

Quality: 8.5/10.

Very strong catalyst and volume premarket. Highly liquid stock. A very good second day play. It had some time to digest yesterday's rise and build some support.

### Technical Analysis

Support: 86, 90.

<iframe src="charts/NBIS_2025-09-10_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/NBIS_2025-09-10.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The premarket levels were 90 and 92. The open was decently strong, and the break above the hold at 92 was a good long entry, but it didn't go far and the volume slowed down after that.

The stock was essentially rangebound for 20m after the open, but what is really remarkable after that is the breakout above 95. On a 1m candlestick chart this move would have been over in a few bars, but the volume charts here show a smoothly trending move that could have been good for at least 4 to 5 points. Then the volume became slow and the stock essentially gave up all the gains that it had made throughout the rest of the day on increasigly anemic volume. I wouldn't consider the failed breakout above 100 a good short, because the volume on the downside was so slow. The best trades have fast volume rather than a lot of it over a long period of time.

The breakout above 95 was an A+ trade.

### Extra

LW might have motivated the initial creation of volume charts, but it is this chart of NBIS on this particular day that convinced me that trading could be profitable, so I wanted it in second place on this document. Take a look at the following chart and contrast it with the previous to get a feel for the difference.

<iframe src="charts/NBIS_2025-09-10_intraday_candle.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The candlestick charts would always lead you to missing the most profitable opportunities, and don't illustrate key levels well at all. On a candlestick chart, by the time the huge upbar appeared, the move was essentially over, and only the but of the cigarete was left. It's a very common pattern on these kinds of charts, which is why we're not going to be using them.

## Ticker: NBIS Date: 2025-09-09

### Big Picture

Rangebound indices for the past month. AI mania is in progress and this is an AI cloud stock so positive news can be expected to cause large reactions.

### Intraday Fundamentals

Avg. Volume: 5.8m
Float: 202.8m
Short %: 7.5% (low)
Gap %: 50.5% (huge)
Premarket volume: 7.9m (136%, 3.9%)
Catalyst: 10/10 (best)

The catalyst is a 17b AI deal with Microsoft which is huge for a company that had a market cap less than that amount when it was announced. This was done the previous day and today is the day of the catalyst. A **Changing Fundamentals** play.

Quality: 8/10.

The volume and the catalyst quality is amazing, but I really wish the opening gap wasn't 50%. I guess it doesn't matter to a daytrader, but in today's markets the swing are position traders never seem to get easy plays. Usually this kind of news should be a huge long opportunity, but due to the huge price increase it was actually a short opportunity in the end. If this was a low float stock, even 50% might have been bearable, but not on a large float stock like this one.

Had it had 20m float, this would be 10/10 instead 8/10.

### Technical Analysis

Support: 60 (far from the current prices)

<iframe src="charts/NBIS_2025-09-09_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis

<iframe src="charts/NBIS_2025-09-09.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The chart of this was pretty messy, and while this was a decent short trade after the open, the trade can be considered difficult. The ideal way of handling this stock would have been to go short below the open or below the premarket low at 96 and holding for 25m to 35m shares of trading when the volume slowed down considerably from it's initial frenzy. The huge gap up led to a lot of profit taking.

The premarket high was at 100, but the trade never came close to it.

In fact, it bears highlighting that after the selloff exhausted itself there was a reversal that lasted for a long time, but it was on very slow volume, so I wouldn't touch that kind of trade. As a rule, I'd want to be in when the volume is fast and out when it is slow, and I am going to design my trading systems around that principle.

## Ticker: MSTR Date: 2024-11-21

### Big Picture


*- S&P 500 gained 0.53%, closing at 5,948.71 — extending a 4-day winning streak as part of a strong post-election rally (S&P finished November up 5%+, best month of 2024)*
*- Bitcoin hit a record high above $99,000 that day, driving crypto-related names*
*- The dominant themes were the post-Trump-election rally, AI, and crypto*

### Intraday Fundamentals

Avg. Volume: 20.59m
Float: 313.41m
Short %: 17.8% (relatively high)
Gap %: 12.9% (large)
Premarket volume: 7.4m (35.9%)
Catalyst: 7/10 (thesis busting)

*- Convertible note offering: On Nov 18, MicroStrategy announced a $1.75B convertible note offering (later upsized to $3B at 0% coupon) to buy more Bitcoin. Between Nov 18-24 they bought 55,500*
*BTC for ~$5.4B*
*- The stock had rallied ~59% in November heading into this day, riding Bitcoin's surge and the company's aggressive BTC acquisition strategy*
*- MSTR hit an all-time high of ~$543 intraday on Nov 21*
*- Then Citron Research (Andrew Left) disclosed a short position, arguing the stock was "overheated" and disconnected from Bitcoin fundamentals (while remaining bullish on BTC itself)*
*- The stock reversed ~16% intraday, closing around $397 after being up 11% earlier in the session*
*- This marked the beginning of a prolonged decline — MSTR fell ~35% within a week and eventually ~70% from this peak*

*In summary: Strong intraday fundamentals (massive volume, Bitcoin proxy at ATH, $3B convertible offering), but the Citron short announcement mid-day was the catalyst for the reversal. A classic blow-off top.*

Quality: 8/10.

This stock was covered in Lance's Magnum Opus course, so I decided to review it and add it to this reference book. Not sure how many blow off setups I'll be trading, but this one was close to ideal for a large float stock such as this. It went up from 3 straight days on very high volume and the gap up open was a sign to become bearish.

A **Parabolic Reversal** play.

### Technical Analysis

Support: 400

<iframe src="charts/MSTR_2024-11-21_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/MSTR_2024-11-21.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The stock had a very difficult initial 20 minutes, and I am looking forward to seeing how my systems will handle it. It took 20 minutes and 10m shares traded for the downtrend to properly develop, so trying to catch the opening drive would have gotten us stopped out a few times. The question we'll be answering in the future is whether the stock would have had liquidy to keep the losses small.

The ideal way of handling this stock would have been to short after the first 20m when the volume started to get fast and holding it either until the second exhaustion point or until the end of the day risking a full stop out. But rather than aim for ideal, the realistic way would have been to short 20m in once the volume started to get fast, then hold until the first exhaustion nearly 30m later. That would have been an A+ trade. Trying to short again once the volume became fast would have led to stop outs.

Very messy chart, LW was much better. It makes me wonder whether trying to play the upside on the previous days would have gone better, but I'll want to fill this document with low floats rather than stocks like MSTR. I see no real point in rating the chart quality in hindsight, but if most high short floats act like this, I'd rather avoid these kinds of trades.

## Ticker: OPEN Date: 2025-09-11

### Big Picture

*All three major indexes closed at record highs. S&P 500 gained 0.85% to ~6,532. CPI inflation report kept Fed rate cut hopes alive. The dominant themes were AI/tech and a broad post-summer rally. OPEN is a real estate iBuyer — not directly tied to the AI theme, but lower rates are bullish for housing. The stock had been on a massive retail-driven meme rally — up ~900% from late June through early September 2025, fueled by high short interest creating short squeeze dynamics. The broader bullish market was supportive of the rally.*

### Intraday Fundamentals

Avg. Volume: 301m
Float: 793m
Short %: 19.5%
Gap %: 29.7%
Premarket volume: 77m (25% avg, 9.7% float)
Catalyst: 5/10 (meme rally)

*New CEO announced on Sep 10 — Kaz Nejatian (ex-Shopify COO) named CEO effective Sep 15. Co-founders Keith Rabois (as Chairman) and Eric Wu rejoined the board. Khosla Ventures and Wu invested $40M in PIPE financing. Nejatian's compensation package could net ~$2.8B if ambitious stock targets are met (82.4M share inducement grant). The stock was near its 52-week high of $10.52 and at the peak of the meme/squeeze rally before a subsequent ~54% decline.*

Quality: 9/10.

The premarket volume might be only 25% of the average dialy volume, but the average daily volume has been growing steadily for the past 2 months, if it wasn't for that inflation, the average should be decently lower. The only reason to even look at this stock is because of the huge gap that is supported by volume. On the day of this climax top, the stock churned through more than its entire float. Maybe we should be looking at the premarket volume relative to its % of float rather than relative to its average. At this point I am wondering whether the catalyst quality even matters for an intraday trader. We should be just looking at volume either way and this stock meets that criterion in spades.

Given how the strong the stock was prior to this large gap up and the fact that it has been going parabolic for weeks leading up to it, this could very well have been a **Parabolic Reversal** trade, but it ended up being a continuation intraday simply due to how overwhelming the volume was on this day. That having said the date did end up being the absolute top in the stock.

### Technical Analysis

Support: 7.

<iframe src="charts/OPEN_2025-09-11_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/OPEN_2025-09-11.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

This stock had messy initial action. Once the premarket high of 8 was broken, there was a breakout, but the momentum had no follow through and would have lead to a stop out. On the chart you can see very clear holds at 8, but it is too bad the trend didn't take hold properly. The breakout above 8.5 had much faster volume and worked better in contrast and would have bee a good Move2Move trade. After that the stock slowly grided up for the rest of the day. What is noteworthy is the breakout above 10 near the end of the day. The volume uncharacteristically speed up, but if you look at the durations you'll see that unless you got in around 9.9 mark, you wouldn't get a chance to input the limit orders afterwards. I am guessing somebody panic bought this stock.

The morning breakouts above 8 and 8.5 were A+ trades either way. The second break above 8 would have given at least a 10% gain assuming you held it to the end of the morning hours.

My personal preference is for my systems to go long above 8.5 when the volume started to become faster and sell once the momentum started vaning at around 9.2. It's not so much about getting the meat of the move, but simply the best part of the move and leaving the volatility to others.

The stock was so liquid that it would have been no trouble to scale into this trade.

## Ticker: SMCI Date: 2026-03-20
### Big Picture

Market context unavailable for this date. AI infrastructure and data center themes remain dominant in tech sector. SMCI is a major AI server manufacturer with strong ties to NVIDIA's GPU platforms.

### Intraday Fundamentals

Avg. Volume: ~20m (estimated)
Float: ~600m (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: 7/10

Recent federal indictment against co-founder for allegedly smuggling AI servers to China introduced governance risks. Stock experiencing volatility due to margin compression (6.3% in Q2 FY26 vs 11.8% prior year) despite record revenue growth of 123% YoY. Company projecting $40B revenue for FY26 driven by AI server demand.

Quality: TBD (pending volume data)

### Technical Analysis

<iframe src="charts/SMCI_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/SMCI_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: PL Date: 2026-03-20
### Big Picture

Market context unavailable. AI and satellite imaging themes driving growth. PL partners with NVIDIA and Google for AI-enhanced satellite imagery processing.

### Intraday Fundamentals

Avg. Volume: ~15m (estimated)
Float: ~300m (estimated)
Short %: Unknown
Gap %: 26.1% (huge)
Premarket volume: TBD
Catalyst: 9/10 (strong earnings beat)

Q4 FY26 earnings released March 20: Revenue $86.8M (41% YoY growth), achieved break-even non-GAAP EPS beating estimates. First full-year Adjusted EBITDA profitability ($15.5M) and FCF positive. FY27 guidance $415-440M (39% growth). Backlog surged 79% to $900.4M. Stock hit all-time high at $33.83, up 70% YTD and 242% since IPO. A **Changing Fundamentals** play.

Quality: 9/10.

Exceptional catalyst with strong earnings beat, positive guidance, and massive backlog growth. High volatility stock (beta 3.64) with extreme valuation (P/S 28.92).

### Technical Analysis

<iframe src="charts/PL_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/PL_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: FDX Date: 2026-03-20
### Big Picture

Market context unavailable. FDX hit all-time high of $386.91 on Feb 20, 2026. Stock up 55.8% over past year. Logistics and shipping sector showing strength.

### Intraday Fundamentals

Avg. Volume: ~3m (estimated)
Float: ~250m (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: 6/10

No specific catalyst for this date. Stock trading near highs around $358-359. Federal Express segment (86% of revenue) saw 8% YoY revenue increase and 48.7% EBIT rise to $1.56B in FY25. Positive outlook with 10% revenue growth projected for FY26. Analysts maintain "Moderate Buy" with avg target $393.85.

Quality: TBD (pending volume data)

### Technical Analysis

<iframe src="charts/FDX_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/FDX_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: ARM Date: 2026-03-20
### Big Picture

Market context unavailable. AI and data center themes dominant. ARM positioned as key player in AI chip design ecosystem with strong NVIDIA partnerships.

### Intraday Fundamentals

Avg. Volume: ~10m (estimated)
Float: ~1B (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: 8/10 (analyst upgrade)

HSBC upgraded ARM to "Buy" with $205 price target on March 20, citing "game-changing" shift to AI server CPUs. Citi reiterated "Buy" with $190 target. Q3 FY26 showed 26% YoY revenue growth to $1.24B. v9 architecture adoption driving higher royalty rates. Meta-NVIDIA collaboration on ARM-based data centers announced. "Arm Everywhere" event scheduled March 24. A **Thesis Busting** play on AI infrastructure.

Quality: 8/10.

Strong catalyst with major analyst upgrades and strategic partnerships. Large float but high institutional interest in AI infrastructure plays.

### Technical Analysis

<iframe src="charts/ARM_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/ARM_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: VG Date: 2026-03-19
### Big Picture

Market context unavailable. VG (Venture Global) is an LNG infrastructure company listed on NYSE. Energy sector activity.

### Intraday Fundamentals

Avg. Volume: TBD
Float: ~2.4B (estimated from $38.7B market cap)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: 6/10

Stock showed 10.29% increase in 24-hour period around this date. Price moved from $12.28 on March 16 to $15.79-15.89 range by March 20-21. Raymond James raised price target to $13 from $11, RBC Capital raised to $14 from $11. Analyst avg target $11.40 (range $5.05-$17.85).

Quality: TBD (pending volume data)

### Technical Analysis

<iframe src="charts/VG_2026-03-19_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/VG_2026-03-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2026-03-16
### Big Picture

Market context unavailable. AI cloud infrastructure theme remains dominant. NBIS previously had major Microsoft AI deal catalyst in 2025.

### Intraday Fundamentals

Avg. Volume: TBD
Float: ~200m (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: TBD

No specific catalyst identified for this date. Likely continuation or follow-through from previous AI infrastructure momentum.

Quality: TBD (pending volume data)

### Technical Analysis

<iframe src="charts/NBIS_2026-03-16_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/NBIS_2026-03-16.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2026-03-17
### Big Picture

Market context unavailable. AI cloud infrastructure theme remains dominant. NBIS previously had major Microsoft AI deal catalyst in 2025.

### Intraday Fundamentals

Avg. Volume: TBD
Float: ~200m (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: TBD

No specific catalyst identified for this date. Likely **Second Day** continuation from March 16 activity.

Quality: TBD (pending volume data)

### Technical Analysis

<iframe src="charts/NBIS_2026-03-17_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/NBIS_2026-03-17.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: TME Date: 2026-03-17
### Big Picture

Market context unavailable. Chinese tech and streaming music sector. TME is Tencent Music Entertainment.

### Intraday Fundamentals

Avg. Volume: ~30m (estimated)
Float: ~1.6B (estimated)
Short %: Unknown
Gap %: -24.65% (huge)
Premarket volume: TBD
Catalyst: 8/10 (earnings miss on guidance)

Q4 2025 earnings released March 17: Revenue beat at RMB 8.64B (+15.9% YoY), music subscription revenue +13.2% to RMB 4.56B. However, subscription growth slowed (13% Q4 vs 17% Q3), online music MAU declined 5%, and company discontinued reporting quarterly KPIs. Multiple analyst downgrades: JPM to neutral ($12 target from $30), Benchmark to hold, UBS to neutral, Macquarie to neutral ($14.10). Concerns over competition from Soda Music and reduced transparency. A **Changing Fundamentals** play (negative).

Quality: 8/10.

Strong negative catalyst with earnings disappointment and multiple downgrades. Stock down 24.65% on day.

### Technical Analysis

<iframe src="charts/TME_2026-03-17_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/TME_2026-03-17.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: TME Date: 2026-03-18
### Big Picture

Market context unavailable. Chinese tech and streaming music sector. TME is Tencent Music Entertainment.

### Intraday Fundamentals

Avg. Volume: ~30m (estimated)
Float: ~1.6B (estimated)
Short %: Unknown
Gap %: TBD (continued decline)
Premarket volume: TBD
Catalyst: 7/10 (continued selloff)

**Second Day** continuation from March 17 earnings selloff. Stock on track to lose nearly a third of value since earnings report. Analyst downgrades continuing to pressure stock. Concerns over slowing subscription growth, MAU decline, and reduced transparency weighing on sentiment.

Quality: 7/10.

Strong continuation setup on negative catalyst. High volume expected as selloff continues.

### Technical Analysis

<iframe src="charts/TME_2026-03-18_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/TME_2026-03-18.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: ULTA Date: 2026-03-13
### Big Picture

Market context unavailable. Retail/beauty sector. ULTA is Ulta Beauty.

### Intraday Fundamentals

Avg. Volume: ~2m (estimated)
Float: ~50m (estimated)
Short %: Unknown
Gap %: TBD (post-earnings decline)
Premarket volume: TBD
Catalyst: 7/10 (earnings beat with selloff)

Q4 FY25 earnings released March 12: EPS $8.01 beat est. $7.93, revenue $3.90B (+11.8% YoY) beat est. $3.81B. Full year EPS $25.64, sales $12.4B (+9.7%). FY26 guidance: 6-7% sales growth, EPS $28.05-28.55 (+9.4-11.4%). Despite beat, stock declined post-earnings. JPMorgan viewed dip as buying opportunity with $750 target. A **Changing Fundamentals** play with post-earnings volatility.

Quality: 7/10.

Solid earnings beat but market reaction negative. Potential reversal or continuation setup.

### Technical Analysis

<iframe src="charts/ULTA_2026-03-13_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/ULTA_2026-03-13.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: ULTA Date: 2026-03-16
### Big Picture

Market context unavailable. Retail/beauty sector. ULTA is Ulta Beauty.

### Intraday Fundamentals

Avg. Volume: ~2m (estimated)
Float: ~50m (estimated)
Short %: Unknown
Gap %: TBD
Premarket volume: TBD
Catalyst: 6/10 (post-earnings continuation)

**Second Day** or continuation from March 12-13 earnings reaction. Stock potentially finding support or continuing post-earnings move. JPMorgan $750 price target suggests upside potential from post-earnings dip.

Quality: 6/10.

Follow-through setup from earnings volatility. Lower catalyst strength as initial reaction has passed.

### Technical Analysis

<iframe src="charts/ULTA_2026-03-16_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/ULTA_2026-03-16.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>