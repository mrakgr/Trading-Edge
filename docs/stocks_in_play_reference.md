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
Short %: 15.3%
Gap %: -14%
Premarket volume: 330k (24%)
Catalyst: 4/10

Minor analyst beat on earnings release that had a massively negative reaction from the market possibly due to the analyst questions. 

### Technical Analysis

Support: 47, 49.

### Orderflow Analysis
<iframe src="charts/LW_2025-12-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

The open was delayed for 2m, but the volume came in strong after that. The opening prints were block trades totaling around 600k which was followed by a hold above 50, during which laster 100k on the volume chart. The volume was very fast. After wich there was a breakout below 50 that never looked back and the stock trended for the rest of the day.

A noteworthy event was an unusual hold at 47 that lasted for 300k shares of volume. There was a fakeout above that level after it broke down, but a second short would have caught the majority move down.

TODO...

### Extra

LW is noteworthy to me because it is the stock I created the volume charts for the first time. The candlestick charts for it are very noisy, but the key levels and holds are visible to the naked eye on the volume chart. It's not possible to see on the chart, but the stock had very high bid/ask spreads during the day. Near the open they were as much as 5-10%. This created large spikes on the OHLC candlestick chart from the occassional market orders hitting the ask which motivated the development of volume charts.

Initially, I wanted to learn tape reading, but the LW's open had 800 transactions in a 3s span, which made me realize that there are a lot of situations where it would be outright impossible to read the tape for a human. Though my initial intetion was to throw away charts, the solution was always to find a middle ground between tape reading and regular time based charts. Volume charts are that middle ground. May they serve us well.

## Ticker: MSTR Date: 2024-11-21

### Big Picture



### Intraday Fundamentals

### Technical Analysis

### Orderflow Analysis
<iframe src="charts/MSTR_2024-11-21.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2025-09-09
<iframe src="charts/NBIS_2025-09-09.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2025-09-10
<iframe src="charts/NBIS_2025-09-10.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: OPEN Date: 2025-09-11
<iframe src="charts/OPEN_2025-09-11.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>