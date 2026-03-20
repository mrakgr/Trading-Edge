# Overview

The reference document for the stocks in play that will serve as the basis for the generative models. Here is a quick conversion script that should be run from the parent directory in order to convert this markdown file into .html for viewing in the browser.

```bash
pip install markdown
python3 -c "import markdown; print(markdown.markdown(open('docs/stocks_in_play_reference.md').read(), extensions=['extra']))" > docs/stocks_in_play_reference.html
```

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
Premarket volume: 330k (24%)
Catalyst: 4/10 (weak)

Minor analyst beat on earnings release that had a massively negative reaction from the market possibly due to the analyst questions. A **Changing Fundamentals** play.

Quality: 7.5/10.

The premarket volume is there, but the catalyst is weak, liquidity is low and the float is high.

### Technical Analysis

Support: 47, 49.

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
Short %: 22.3% (relatively high)
Gap %: -4.3% (small)
Premarket volume: 3.9m (75%)
Catalyst: 10/10 (best)

The catalyst is a 17b AI deal with Microsoft which is huge for a company that had a market cap less than that amount when it was announced. This was 2 days ago, so this is a **Second Day** play on this stock.

Quality: 8.5/10.

Very strong catalyst and volume premarket. Highly liquid stock. A very good second day play. It had some time to digest yesterday's rise and build some support.

### Technical Analysis

Support: 86, 90.

### Orderflow Analysis
<iframe src="charts/NBIS_2025-09-10.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The premarket levels were 90 and 92. The open was decently strong, and the break above the hold at 92 was a good long entry, but it didn't go far and the volume slowed down after that.

The stock was essentially rangebound for 20m after the open, but what is really remarkable after that is the breakout above 95. On a 1m candlestick chart this move would have been over in a few bars, but the volume charts here show a smoothly trending move that could have been good for at least 4 to 5 points. Then the volume became slow and the stock essentially gave up all the gains that it had made throughout the rest of the day on increasigly anemic volume. I wouldn't consider the failed breakout above 100 a good short, because the volume on the downside was so slow. The best trades have fast volume rather than a lot of it over a long period of time.

The breakout above 95 was an A+ trade.

### Extra

LW might have motivated the initial creation of volume charts, but it is this chart of NBIS on this particular day that convinced me that trading could be profitable, so I wanted it in second place on this document. Take a look at the following chart and contrast it with the previous to get a feel for the difference.

<iframe src="charts/NBIS_2025-09-10_candle.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The candlestick charts would always lead you to missing the most profitable opportunities, and don't illustrate key levels well at all. On a candlestick chart, by the time the huge upbar appeared, the move was essentially over, and only the but of the cigarete was left. It's a very common pattern on these kinds of charts, which is why we're not going to be using them.

## Ticker: NBIS Date: 2025-09-09

### Big Picture

Rangebound indices for the past month. AI mania is in progress and this is an AI cloud stock so positive news can be expected to cause large reactions.

### Intraday Fundamentals

Avg. Volume: 5.8m
Float: 202.8m
Short %: 7.5% (low)
Gap %: 50.5% (huge)
Premarket volume: 7.9m (136%)
Catalyst: 10/10 (best)

The catalyst is a 17b AI deal with Microsoft which is huge for a company that had a market cap less than that amount when it was announced. This was done the previous day and today is the day of the catalyst. A **Changing Fundamentals** play.

Quality: 8/10.

The volume and the catalyst quality is amazing, but I really wish the opening gap wasn't 50%. I guess it doesn't matter to a daytrader, but in today's markets the swing are position traders never seem to get easy plays. Usually this kind of news should be a huge long opportunity, but due to the huge price increase it was actually a short opportunity in the end. If this was a low float stock, even 50% might have been bearable, but not on a large float stock like this one.

Had it had 20m float, this would be 10/10 instead 8/10.

### Technical Analysis

Support: 60 (far from the current prices)

### Orderflow Analysis

<iframe src="charts/NBIS_2025-09-09.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The chart of this was pretty messy, and while this was a decent short trade after the open, the trade can be considered difficult. The ideal way of handling this stock would have been to go short below the open or below the premarket low at 96 and holding for 25m to 35m shares of trading when the volume slowed down considerably from it's initial frenzy. The huge gap up led to a lot of profit taking.

The premarket high was at 100, but the trade never came close to it.

In fact, it bears highlighting that after the selloff exhausted itself there was a reversal that lasted for a long time, but it was on very slow volume, so I wouldn't touch that kind of trade. As a rule, I'd want to be in when the volume is fast and out when it is slow, and I am going to design my trading systems around that principle.

## Ticker: MSTR Date: 2024-11-21

<iframe src="charts/MSTR_2024-11-21.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: OPEN Date: 2025-09-11
<iframe src="charts/OPEN_2025-09-11.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>