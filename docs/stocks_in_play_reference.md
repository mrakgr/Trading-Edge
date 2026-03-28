# Overview

The reference document for the stocks in play that will serve as the basis for the generative models. Here is a quick conversion script that should be run from the parent directory in order to convert this markdown file into .html for viewing in the browser.

```bash
pip install markdown
python3 -c "import markdown; print(markdown.markdown(open('docs/stocks_in_play_reference.md').read(), extensions=['extra', 'nl2br']))" > docs/stocks_in_play_reference.html
```

## What it has (reference characteristics):

- Historical examples of stocks that moved
- Intraday fundamentals (gap %, RVOL, volume, news)
- Categories for the catalysts and longer term price action
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

# Orderflow Analysis Chart Notes

The charts in the Orderflow Analysis section are volume charts. Unlike regular time based charts, these volume charts are built by grouping the trades into fixed size blocks and calculating their Volume Weighted Average Price and the Volume Weighted Standard Deviation. I found this to make analysis far easier than with time based charts. The bottom panel on the charts is the trade duration. When a trade goes over the block size limit, it is split and its remainder is passed into the next block. For large blocks this might entail splitting it up multiple times until the entire trade is consumed.

<script>
// This script replaces the placeholders with charts.
document.addEventListener('click', function(e) {
  if (e.target.classList.contains('chart-placeholder')) {
    const src = e.target.getAttribute('data-src');
    const iframe = document.createElement('iframe');
    iframe.src = src;
    iframe.width = '100%';
    iframe.height = '100%';
    iframe.style.border = '1px solid #ccc';
    e.target.replaceWith(iframe);
  }
});
</script>

## Ticker: LW Date: 2025-12-19
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 136.8m
Avg. Volume: 1.2m
Gap %: -14.39
Premarket volume: 338.0k (27% avg, 0.2% float)
Short %: 15.3
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/LW_2025-12-19_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/LW_2025-12-19.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

LW plunged 26% on underwhelming Q2 earnings and weak guidance, reflecting broader consumer sector weakness amid concerns over profits and declining demand.

## Ticker: NBIS Date: 2025-09-10
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 202.8m
Avg. Volume: 11.1m
Gap %: 51.73
Premarket volume: 7.9m (71% avg, 3.9% float)
Short %: 22.3
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2025-09-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Jumpy
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2025-09-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NBIS surged 136% over two days, crushing Nvidia and Palantir performance. The rally was driven by exceptional 545% revenue growth, expanding data center capacity, and strong demand for GPU-powered cloud infrastructure.

## Ticker: NBIS Date: 2025-09-09
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 202.8m
Avg. Volume: 14.5m
Gap %: -4.26
Premarket volume: 3.9m (27% avg, 1.9% float)
Short %: 7.5
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2025-09-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis

<div class="chart-placeholder" data-src="charts/NBIS_2025-09-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NBIS announced a multi-billion dollar agreement with Microsoft to provide dedicated GPU infrastructure, with the deal potentially worth up to $19.4 billion over five years. Stock surged 40-50% on the news, with 97.7% net profit margin demonstrating strong business fundamentals.

## Ticker: MSTR Date: 2024-11-21
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 313.4m
Avg. Volume: 30.3m
Gap %: 13.04
Premarket volume: 7.4m (24% avg, 2.4% float)
Short %: 17.8
Catalyst: Short Report

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MSTR_2024-11-21_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MSTR_2024-11-21.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Citron Research issued a short report on MSTR, causing a 16% drop in 90 minutes. Despite congratulating CEO Michael Saylor on his "visionary Bitcoin strategy," Citron argued MSTR's trading had become "completely detached from BTC fundamentals" and disclosed a short position. The stock had been trading as a leveraged proxy for Bitcoin.

## Ticker: OPEN Date: 2025-09-11
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 793.0m
Avg. Volume: 395.5m
Gap %: 29.86
Premarket volume: 77.0m (19% avg, 9.7% float)
Short %: 19.5
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/OPEN_2025-09-11_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/OPEN_2025-09-11.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

OPEN surged over 1,000% driven by meme-stock momentum and leadership changes. The company appointed Kaz Nejatian as new CEO and brought back co-founders Keith Rabois and Eric Wu to the board, signaling potential strategic shifts and positioning for potential interest rate cuts.

## Ticker: SMCI Date: 2026-03-20
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 514.0m
Avg. Volume: 24.6m
Gap %: -26.86
Premarket volume: 32.5m (132% avg, 6.3% float)
Short %: 15.8
Catalyst: Federal Indictment 

### Technical Analysis
<div class="chart-placeholder" data-src="charts/SMCI_2026-03-20_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Battleground
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/SMCI_2026-03-20.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

SMCI plunged 28-33% after co-founder Yin-Shyan Liaw was charged with smuggling $2.5 billion worth of Nvidia GPUs to China in violation of U.S. export controls. Despite strong fundamentals (123% sales growth), the federal indictment triggered securities investigations and massive selloff.

## Ticker: SMCI Date: 2026-03-23
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 514.0m
Avg. Volume: 35.4m
Gap %: -1.07
Premarket volume: 7.7m (22% avg, 1.5% float)
Short %: 17.9
Catalyst: Federal Indictment 

### Technical Analysis
<div class="chart-placeholder" data-src="charts/SMCI_2026-03-23_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Battleground
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/SMCI_2026-03-23.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the 2026-03-20 federal indictment of co-founder Yin-Shyan Liaw for smuggling $2.5 billion worth of Nvidia GPUs to China. The -1.07% gap reflects continued but lighter selling pressure compared to the initial -26.86% drop.

## Ticker: PL Date: 2026-03-20
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 260.0m
Avg. Volume: 10.7m
Gap %: 24.44
Premarket volume: 3.1m (29% avg, 1.2% float)
Short %: 13.2
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/PL_2026-03-20_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/PL_2026-03-20.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

PL rocketed 25-26% after reporting record Q4 revenue with 41% growth, achieving breakeven adjusted EPS, and issuing strong FY27 guidance of 39% sales growth. The company's 79% backlog growth and AI partnerships with Nvidia and Google drove investor optimism.

## Ticker: FDX Date: 2026-03-20
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 218.0m
Avg. Volume: 1.8m
Gap %: 6.94
Premarket volume: 216.9k (12% avg, 0.1% float)
Short %: 9.1
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/FDX_2026-03-20_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/FDX_2026-03-20.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

FDX surged 10% after reporting better-than-expected Q3 results with EPS of $5.25 (well above $4.01 consensus) and raising FY26 adjusted EPS guidance above estimates, demonstrating strong logistics demand.

## Ticker: ARM Date: 2026-03-20
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 1.1b
Avg. Volume: 3.9m
Gap %: 5.45
Premarket volume: 246.5k (6% avg, 0.0% float)
Short %: 20.6
Catalyst: Analyst Upgrade

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ARM_2026-03-20_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ARM_2026-03-20.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

HSBC upgraded ARM from 'reduce' to 'buy' with a $205 price target (up from $90), citing ARM's undervalued position as a major beneficiary of the AI server CPU market. Citi reiterated a 'Buy' rating with $190 target, noting ARM's v9 product commands 2x the royalty rate of older technology. Stock rose 3.81% to $134.76 on the analyst upgrades.

## Ticker: VG Date: 2026-03-19
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 477.0m
Avg. Volume: 24.1m
Gap %: 6.06
Premarket volume: 3.7m (16% avg, 0.8% float)
Short %: 20.9
Catalyst: Breakout

### Technical Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-19_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-19.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

VG traded higher driven by global LNG supply disruptions and rising energy prices following Iranian strikes on Qatari energy infrastructure. European natural gas prices surged 30% to above 70 euros per MWh, benefiting the LNG provider.

## Ticker: NBIS Date: 2026-03-16
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 202.0m
Avg. Volume: 13.4m
Gap %: 10.45
Premarket volume: 3.7m (28% avg, 1.8% float)
Short %: 17.2
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2026-03-16_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2026-03-16.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NBIS secured a massive $27 billion five-year agreement with Meta for AI infrastructure, with $12 billion in base commitment and up to $15 billion in additional capacity. Combined with previous Microsoft ($17.4B) and Nvidia ($2B investment) deals, NBIS now has $46 billion in total contracts. Stock surged 11-15% on the announcement.

## Ticker: NBIS Date: 2026-03-17
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 202.0m
Avg. Volume: 14.1m
Gap %: -7.32
Premarket volume: 3.4m (24% avg, 1.7% float)
Short %: 17.2
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2026-03-17_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NBIS_2026-03-17.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### News Summary

No new catalyst. Second day continuation following the previous day's $27 billion Meta deal announcement. The -7.32% gap down reflects profit-taking after the 11-15% surge on 2026-03-16.

## Ticker: TME Date: 2026-03-17
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 575.0m
Avg. Volume: 6.1m
Gap %: -15.35
Premarket volume: 1.7m (28% avg, 0.3% float)
Short %: 23.3
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TME_2026-03-17_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TME_2026-03-17.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TME tumbled despite solid Q4 earnings (15.9% revenue growth, matching EPS estimates) due to a concerning 5% decline in monthly active users to 528 million. The decline was driven by competition from short-form video platforms like ByteDance's Douyin and Qishui Music.

## Ticker: TME Date: 2026-03-18
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 575.0m
Avg. Volume: 9.2m
Gap %: -0.18
Premarket volume: 799.0k (9% avg, 0.1% float)
Short %: 40.2
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TME_2026-03-18_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TME_2026-03-18.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the previous day's earnings disappointment. Continued selling pressure from the 5% MAU decline reported on 2026-03-17.

## Ticker: ULTA Date: 2026-03-13
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 44.2m
Avg. Volume: 549.5k
Gap %: -9.35
Premarket volume: 45.6k (8% avg, 0.1% float)
Short %: 20.3
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ULTA_2026-03-13_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ULTA_2026-03-13.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

ULTA plunged 11.3% despite beating earnings estimates. The company cut FY2027 net sales growth guidance to 6%-7%, citing consumer pullback in discretionary beauty spending amid broader economic concerns.

## Ticker: ULTA Date: 2026-03-16
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 44.2m
Avg. Volume: 660.3k
Gap %: -0.52
Premarket volume: 28.7k (4% avg, 0.1% float)
Short %: 24.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ULTA_2026-03-16_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ULTA_2026-03-16.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the 2026-03-13 earnings selloff. Continued volatility from the weak FY2027 guidance despite earnings beat.

## Ticker: BYND Date: 2025-10-20
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 438.0m
Avg. Volume: 57.4m
Gap %: 57.99
Premarket volume: 178.0m (310% avg, 40.6% float)
Short %: 38.6
Catalyst: Short Squeeze

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-20_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>
<div class="chart-placeholder" data-src="charts/BYND_2025-10-20_intraday_candle.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-20.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BYND surged 137% in 24 hours due to a coordinated short squeeze on social media platforms, triggered by a convertible notes tender offer. With 54% short interest, the stock became a prime target despite weak fundamentals: falling revenue, deep losses, and bankruptcy risk. Stock was down 99% over five years before the rally.

## Ticker: BYND Date: 2025-10-21
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 438.0m
Avg. Volume: 117.5m
Gap %: 57.14
Premarket volume: 244.4m (208% avg, 55.8% float)
Short %: 22.1
Catalyst: Short Squeeze

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-21_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>
<div class="chart-placeholder" data-src="charts/BYND_2025-10-21_intraday_candle.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-21.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Stock climbed another 170% over two days as part of a broader "meme stock" rally. Investors piled into unprofitable companies, showing market enthusiasm for growth potential over immediate profitability despite ongoing operational losses.

## Ticker: BYND Date: 2025-10-22
### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 438.0m
Avg. Volume: 220.9m
Gap %: 70.44
Premarket volume: 501.9m (227% avg, 114.6% float)
Short %: 26.2
Catalyst: Short Squeeze

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-22_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>
<div class="chart-placeholder" data-src="charts/BYND_2025-10-22_intraday_candle.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BYND_2025-10-22.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Martin Shkreli announced a short position, citing negative gross margins. Stock experienced extreme swings with over 63% of tradable shares shorted. Fundamentals deteriorated: Q2 revenue down 20%, $29.2M net loss, margins declining from 14.7% to 11.5%. Analysts bearish with $0.80 price target. Massive trading volume represented the peak of the speculative frenzy.

## Ticker: MOS Date: 2026-03-12

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 316.0m
Avg. Volume: 8.0m
Gap %: 6.57
Premarket volume: 737.4k (9% avg, 0.2% float)
Short %: 17.9
Catalyst: Deal 

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MOS_2026-03-12_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Changing Fundamentals 

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MOS_2026-03-12.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MOS announced a joint project development agreement with Rainbow Rare Earths Limited for the Uberaba rare earths facility in Brazil. The project aims to extract rare earth elements from phosphogypsum, with initial production targeted for 2030. Stock rose 6.50% on the announcement, supported by concerns over Strait of Hormuz closures affecting Middle East producers.

## Ticker: CF Date: 2026-03-12

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 152.0m
Avg. Volume: 4.3m
Gap %: 6.57
Premarket volume: 195.0k (5% avg, 0.1% float)
Short %: 29.8
Catalyst: Breakout

### Technical Analysis
<div class="chart-placeholder" data-src="charts/CF_2026-03-12_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Positive Momentum
Play: Changing Fundamentals 

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/CF_2026-03-12.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

CF Industries rose 6.57% alongside other fertilizer stocks on concerns over Strait of Hormuz closures affecting Middle East fertilizer producers. The sector-wide move was amplified by CF's high short interest of 29.8%, with no company-specific catalyst.

## Ticker: ORCL Date: 2026-03-11

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 1.7b
Avg. Volume: 26.4m
Gap %: 11.40
Premarket volume: 5.2m (20% avg, 0.3% float)
Short %: 15.4
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ORCL_2026-03-11_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Negative Momentum
Play: Changing Fundamentals 

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ORCL_2026-03-11.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

ORCL surged 10% after beating Q3 earnings with EPS of $1.79 (vs $1.70 consensus) and revenue of $17.19B (+22% YoY). Cloud revenue grew 44% with infrastructure up 84%, driven by AI demand. The company reported a massive $553B backlog including a $300B OpenAI contract and raised FY2027 revenue guidance to $90B. JPMorgan upgraded the stock citing proof of AI strategy success.

## Ticker: BNTX Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 250.0m
Avg. Volume: 620.0k
Gap %: -18.94
Premarket volume: 1.4m (232% avg, 0.6% float)
Short %: 14.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BNTX_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Battleground
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BNTX_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BNTX plunged 17-19% after co-founders Ugur Sahin and Özlem Türeci announced plans to exit by end of 2026 to launch a new mRNA venture. The company also lowered 2026 revenue guidance to $2.33-$2.68B (vs $3.12B consensus) due to declining COVID-19 vaccine demand and reported a €305M net loss in Q4 2025. Despite maintaining €16B in cash, investors are concerned about leadership continuity and the multi-year capital-intensive transition to oncology.

## Ticker: BNTX Date: 2026-03-11

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 250.0m
Avg. Volume: 1.2m
Gap %: 2.85
Premarket volume: 316.0k (26% avg, 0.1% float)
Short %: 15
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BNTX_2026-03-11_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Battleground
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BNTX_2026-03-11.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the previous day's 17-19% selloff on co-founder departure and weak guidance. The +2.85% gap reflects a modest bounce as traders assess the oversold conditions.

## Ticker: CRSP Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 89.0m
Avg. Volume: 1.5m
Gap %: -8.23
Premarket volume: 140.8k (9% avg, 0.2% float)
Short %: 21.2
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/CRSP_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/CRSP_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

CRSP fell 12% after announcing a $350M convertible debt offering to fund drug development. The market reacted negatively to potential shareholder dilution, though analysts maintain a bullish $81.21 price target (50% upside). The company has successful gene therapy approvals and five ongoing clinical trials, suggesting the capital raise was anticipated and the decline may be an overreaction.

## Ticker: NIO Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 2.1b
Avg. Volume: 36.4m
Gap %: 6.48
Premarket volume: 10.5m (29% avg, 0.5% float)
Short %: 20.8
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NIO_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NIO_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NIO surged 10% after reporting its first-ever quarterly net profit of $40.4M in Q4, exceeding guidance. The Chinese EV maker posted record quarterly revenue of 34.6B yuan and provided strong Q1 guidance with expected revenue doubling YoY and vehicle deliveries increasing over 90%. Expanding gross margins and declining battery costs support the bullish outlook, though investors should monitor a $1.2B stock incentive plan for CEO William Li.

## Ticker: NIO Date: 2026-03-11

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 2.1b
Avg. Volume: 41.5m
Gap %: -0.18
Premarket volume: 3.8m (9% avg, 0.2% float)
Short %: 16.4
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NIO_2026-03-11_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NIO_2026-03-11.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the previous day's 10% surge on first quarterly profit announcement. The -0.18% gap reflects consolidation after the strong earnings-driven rally.

## Ticker: HIMS Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 207.0m
Avg. Volume: 37.6m
Gap %: 47.49
Premarket volume: 24.9m (66% avg, 12.0% float)
Short %: 14.1
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/HIMS_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/HIMS_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

HIMS surged 48-49% after resolving its legal dispute with Novo Nordisk and announcing a partnership to sell Wegovy pills and Ozempic injections through its telehealth platform. The company also beat earnings expectations with 8 cents EPS vs 3 cents estimated. The deal removes major legal uncertainty and provides significant revenue growth opportunities, though Q1 and 2026 revenue guidance fell short of analyst estimates. The stock remains attractively valued at under 20x forward P/E.

## Ticker: HIMS Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 207.0m
Avg. Volume: 44.0m
Gap %: 8.30
Premarket volume: 5.9m (13% avg, 2.8% float)
Short %: 31.1
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/HIMS_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/HIMS_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the previous day's 48-49% surge on the Novo Nordisk partnership announcement. The +8.30% gap reflects continued buying momentum.

## Ticker: USO Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 9.5m
Gap %: 6.92
Premarket volume: 2.6m (28% avg)
Short %: 13
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

USO surged 6.92% as crude oil prices jumped approximately 8% following coordinated US and Israeli military strikes on Iran over the weekend. The attacks, which included the killing of Iran's supreme leader, triggered the 2026 Strait of Hormuz crisis. Iran's IRGC warned against vessel passage through the strait, effectively halting shipping traffic and threatening 20% of global daily oil supply. WTI crude traded around $72.52/barrel and Brent at $79.04/barrel. The closure represented the largest disruption to energy supply since the 1970s energy crisis.

Note that in USO specifically around 20:16 to 20:18 there was a flash crash that didn't happen in the oil futures and was a great arbitrage opportunity.

## Ticker: USO Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 11.0m
Gap %: 7.93
Premarket volume: 3.5m (32% avg)
Short %: 9.7
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Second day continuation of the Strait of Hormuz crisis. USO gained 7.93% as maritime transit through the strait dropped by approximately 70%, with over 150 ships anchoring outside. Oil prices continued climbing with WTI reaching $90.20 and trading between $87.33-$94.37. Iran made multiple attacks on merchant ships, and crude oil tanker rates from the Middle East to Asia reached multi-decade highs. The crisis projected a global oil supply plunge of 8 million barrels per day in March.

## Ticker: USO Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 13.1m
Gap %: 0.02
Premarket volume: 2.4m (18% avg)
Short %: 10.3
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Consolidation day with minimal gap (0.02%) as the market digested the ongoing Strait of Hormuz closure. Commercial shipping through the strait remained near zero, with Iran continuing attacks on merchant vessels. Oil prices remained elevated as the crisis entered its fifth day, with Asian economies (China, India, Japan, South Korea) particularly affected due to heavy reliance on Gulf oil imports through the strait.

## Ticker: USO Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 14.0m
Gap %: 3.17
Premarket volume: 1.5m (11% avg)
Short %: 7.5
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

USO gained 3.17% as oil prices continued climbing toward the $100/barrel milestone. The Strait of Hormuz remained effectively closed, with oil prices up more than 40% compared to pre-crisis levels. Saudi Arabia and UAE attempted to divert oil through alternative pipelines (East-West Pipeline to Yanbu, Abu Dhabi Pipeline to Fujairah), but bypass capacity was insufficient to replace the lost strait volume. OPEC+ pledged increased output to mitigate shortages.

## Ticker: USO Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 15.5m
Gap %: 9.42
Premarket volume: 5.4m (35% avg)
Short %: 13.7
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Positive Momentum
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

USO surged 9.42% as oil prices accelerated toward the $100/barrel breakout. Brent crude was approaching $120/barrel as the crisis intensified. The strait remained closed with commercial shipping at near-zero levels. Global energy markets experienced severe shockwaves affecting aluminum, fertilizer, and helium markets. Iran had made 21 confirmed attacks on merchant ships by this point, with no resolution in sight.

## Ticker: USO Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: Not available
Avg. Volume: 18.6m
Gap %: 9.79
Premarket volume: 9.0m (48% avg)
Short %: 17.9
Catalyst: Major Supply Distruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Postive momentum
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/USO_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

USO surged 9.79% as oil prices reached peak crisis levels. Brent crude hit $110.79/barrel (up 19.4% from prior day) while WTI reached $110.54/barrel (up 21.5%). Some intraday spikes saw WTI touch $119.48 and Brent approach $103-110 range. The EIA reported Brent settled at $94/barrel, representing a 50% increase from the beginning of the year. This marked the climax of the Strait of Hormuz crisis before the US Armed Forces began military operations on March 19 to reopen the strait.

## Ticker: MRVL Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 869.0m
Avg. Volume: 14.2m
Gap %: 11.95
Premarket volume: 2.1m (15% avg, 0.2% float)
Short %: 11.9
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MRVL_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MRVL_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MRVL surged 18.4% following exceptional Q4 FY2026 earnings reported March 5. Revenue hit $2.22B (up 22% YoY) and EPS $0.80 (up 33.3% YoY), both beating estimates. Data center business grew 47% YoY, crossing $6B driven by AI demand. The company issued aggressive guidance: FY2027 revenue ~$11B (30% growth) and FY2028 ~$15B (40% growth). Multiple analyst upgrades followed on March 6, with Benchmark upgrading to Buy ($130 target), Citigroup raising to $118, UBS to $120, and Craig-Hallum to $164. The AI data center momentum and multi-year visibility drove the rally.

## Ticker: MRVL Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 869.0m
Avg. Volume: 17.8m
Gap %: -1.84
Premarket volume: 493.2k (3% avg, 0.1% float)
Short %: 18
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MRVL_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Neutral
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MRVL_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the March 5 earnings beat and 18.4% surge on March 6. The -1.84% gap reflects modest profit-taking after the explosive rally, with traders consolidating gains while maintaining the bullish AI data center narrative.

## Ticker: TPET Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 1.8m
Gap %: 150.00
Premarket volume: 117.8m (6612% avg, 471.0% float)
Short %: 22.1
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TPET exploded 150% as traders piled into small-cap oil and gas stocks following the Strait of Hormuz crisis. The US/Israeli strikes on Iran and Tehran's closure of the strait sent Brent crude toward $100/barrel, triggering massive speculation in micro-cap energy names. With only 25m float and 22.1% short interest, TPET became a momentum target despite burning cash quickly. Premarket volume hit 471% of float (117.8m shares), signaling extreme speculative frenzy rather than fundamental value.

## Ticker: TPET Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 23.7m
Gap %: 69.64
Premarket volume: 74.7m (316% avg, 298.8% float)
Short %: 22.4
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Second day parabolic move with another 69.64% gap as oil prices continued surging on the Strait of Hormuz closure. TPET's stock surged over 400% in the week as Brent crude approached $100/barrel. Premarket volume remained extreme at 298.8% of float. The rally was purely momentum-driven with no company-specific catalyst, riding the wave of oil price speculation and short covering in micro-cap energy stocks.

## Ticker: TPET Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 39.0m
Gap %: -7.69
Premarket volume: 27.9m (71% avg, 111.5% float)
Short %: 19.4
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

First pullback with -7.69% gap as profit-taking hit the parabolic move. TPET announced an amendment to its ATM equity offering program, increasing the maximum aggregate offering amount to $13.38M - a dilution signal that pressured the stock. Despite the pullback, premarket volume remained elevated at 111.5% of float. Analysis noted extreme volatility and rapid cash burn, warning of fundamental weakness beneath the speculative rally.

## Ticker: TPET Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 44.9m
Gap %: 17.21
Premarket volume: 14.6m (32% avg, 58.3% float)
Short %: 19.4
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TPET bounced 17.21% as oil prices continued climbing toward $100/barrel and reports emerged of attacks on US-owned tankers in the Strait of Hormuz. The stock traded 245.8% above its 20-day SMA and 157.3% above its 100-day SMA, showing extreme overextension. Despite the bounce, the ATM offering announcement from March 4 signaled management's intent to capitalize on the speculative rally by diluting shareholders. Earnings scheduled for March 17.

## Ticker: TPET Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 64.7m
Gap %: 32.08
Premarket volume: 53.5m (83% avg, 214.0% float)
Short %: 30.9
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TPET surged 32.08% on a combination of operational progress and continued oil rally momentum. The company transitioned two wells in Alberta into production with expected yield of 30-40 barrels/day and retired $1.2M in convertible notes. Filed SEC amendment making $4M of shares available, generating investor interest despite dilution concerns. Short interest spiked to 30.9% as the parabolic move attracted both momentum traders and skeptics. Premarket volume hit 214% of float as oil prices approached $100/barrel on Strait of Hormuz fears.

## Ticker: TPET Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 25.0m
Avg. Volume: 77.7m
Gap %: 17.10
Premarket volume: 25.2m (32% avg, 100.7% float)
Short %: 22.9
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TPET_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TPET extended Friday's surge with another 17.10% gap as "Hormuz Disruption Fears" intensified. Oil prices hit peak crisis levels with Brent approaching $110/barrel. Stock opened $2.26, reached high $2.33, crashed to low $1.60, and closed $1.70 - showing extreme intraday volatility of 46%. Volume exploded to 121.6M shares as the penny stock became a speculative vehicle for oil price exposure. Listed as "penny stock to watch" despite fundamental weakness and rapid cash burn. Short interest dropped to 22.9% as shorts covered into the parabolic move.

## Ticker: IOT Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 357.0m
Avg. Volume: 8.7m
Gap %: 8.82
Premarket volume: 355.2k (4% avg, 0.1% float)
Short %: 11.3
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Samsara (IOT) surged 14.5% after reporting exceptional Q4 results on March 5. Revenue hit $444.3M (up 28.3% YoY), crushing estimates, with non-GAAP EPS of $0.18 vs $0.13 consensus. The IoT solutions provider issued strong FY2027 guidance with EPS projected at $0.65-$0.69, exceeding Wall Street expectations. BMO Capital raised price target from $40 to $44, maintaining Outperform rating. Strong ARR growth and large customer acquisitions demonstrated the company's leadership in connected operations platforms for physical operations.

## Ticker: IOT Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 357.0m
Avg. Volume: 9.8m
Gap %: -0.45
Premarket volume: 62.3k (1% avg, 0.0% float)
Short %: 21
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 6 earnings-driven 14.5% surge. The -0.45% gap reflects modest profit-taking after the stock rallied nearly 20% from earnings. Analysts maintained positive outlook, noting concerns about AI disruption are "overblown" for Samsara. The company's 30% ARR growth to $1.89B and second consecutive quarter of GAAP profitability continued to support the bullish narrative.

## Ticker: IOT Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 357.0m
Avg. Volume: 10.2m
Gap %: -0.15
Premarket volume: 15.0k (0% avg, 0.0% float)
Short %: 19
Catalyst: Product Launch

### Technical Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/IOT_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Samsara unveiled new automated AI coaching features to transform fleet safety: AI Role Play and AI Guided Coaching. These innovations provide personalized feedback to managers and drivers, helping reduce crashes without increasing administrative workload. The company's AI-powered dashcams are a significant revenue driver, with the platform processing over 25 trillion data points annually. The -0.15% gap reflects continued consolidation as the market digested both the strong earnings and new product announcements. Insider Dominic Phillips conducted tax-withholding transaction on vested RSUs.

## Ticker: TTD Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 429.0m
Avg. Volume: 18.9m
Gap %: 25.11
Premarket volume: 5.2m (28% avg, 1.2% float)
Short %: 17
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TTD_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TTD_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

TTD surged 25% following exceptional Q4 2025 earnings reported February 25. Revenue hit $847M (up 14% YoY, 19% excluding political ads) with adjusted EPS of $0.59 crushing the $0.34 estimate. Full year 2025 revenue reached $2.9B (up 18%). Connected TV accounted for 50% of Q4 business, demonstrating strong CTV adoption. The company maintained 95%+ customer retention and authorized $500M in additional share buybacks. Q1 2026 guidance of $678M revenue (10% growth) exceeded expectations, driving the delayed rally on March 5.

## Ticker: TTD Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 429.0m
Avg. Volume: 22.1m
Gap %: -2.10
Premarket volume: 629.6k (3% avg, 0.1% float)
Short %: 22.7
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/TTD_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/TTD_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the March 5 earnings-driven 25% surge. The -2.10% gap reflects modest profit-taking after the explosive rally, with short interest increasing to 22.7% as traders took positions against the extended move.

## Ticker: VSCO Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 69.0m
Avg. Volume: 1.7m
Gap %: -6.02
Premarket volume: 257.5k (15% avg, 0.4% float)
Short %: 19.2
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/VSCO_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/VSCO_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

VSCO dropped 6% despite beating Q4 2025 revenue and EPS estimates. The selloff was triggered by a 4.7% decline in net income, a $116.9M impairment charge related to Adore Me assets, and announcement of a strategic review for DailyLook (non-core asset). Multiple law firms announced securities fraud investigations into the company. The market reacted negatively to the asset impairment and strategic uncertainty despite solid top-line performance.

## Ticker: VSCO Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 69.0m
Avg. Volume: 2.1m
Gap %: -4.84
Premarket volume: 8.8k (0% avg, 0.0% float)
Short %: 25.7
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/VSCO_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/VSCO_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day continuation following the March 5 earnings disappointment. The stock dropped another 4.84% as investors continued to exit positions following the $116.9M Adore Me impairment charge and securities fraud investigation announcements. Short interest spiked to 25.7% as the two-day selloff totaled over 22%.

## Ticker: BATL Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 6.4m
Gap %: 23.13
Premarket volume: 9.7m (153% avg, 62.8% float)
Short %: 32.8
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL surged 40% in premarket following announcement of a $60M asset sale that allowed the company to pay down $40M in debt and address operational bottlenecks, adding 1,200 barrels/day of production. Despite the positive news, the company remained deeply troubled with $31M quarterly loss, negative free cash flow, and $216M debt with delisting risk. The 261% YTD gain and extreme premarket volume (62.8% of float) signaled speculative frenzy rather than fundamental turnaround, with 32.8% short interest reflecting skepticism.

## Ticker: BATL Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 8.5m
Gap %: 93.84
Premarket volume: 15.1m (177% avg, 97.2% float)
Short %: 27
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL exploded 113.77% in regular session plus 32% after-hours as the Strait of Hormuz crisis sent oil prices soaring. US/Israeli strikes on Iran and Tehran's closure of the strait triggered massive speculation in micro-cap energy stocks. With only 15.5m float and premarket volume hitting 97.2% of float, BATL became a momentum target despite burning cash and carrying $216M debt. Investors bet that higher oil prices would improve cash flow and enable balance sheet repair after years of losses.

## Ticker: BATL Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 12.9m
Gap %: 109.83
Premarket volume: 14.5m (113% avg, 93.8% float)
Short %: 22
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL announced a $15M private placement at $5.50/share (net proceeds $14.1M) for working capital as the parabolic move continued with another 109.83% gap. The offering announcement came as oil prices continued surging on the Strait of Hormuz closure. Premarket volume remained extreme at 93.8% of float. Short interest dropped to 22% as shorts covered into the vertical move. The capital raise signaled management's intent to capitalize on the speculative rally by diluting shareholders.

## Ticker: BATL Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 16.2m
Gap %: -36.98
Premarket volume: 2.9m (18% avg, 18.7% float)
Short %: 19
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

First major pullback with -36.98% gap as the parabolic move collapsed. The $15M private placement closed on March 4, locking in dilution at $5.50/share. Profit-taking accelerated as traders who rode the 1,200%+ rally from $1 to multi-year highs began exiting. Despite the crash, premarket volume remained elevated at 18.7% of float, showing continued speculative interest.

## Ticker: BATL Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 17.6m
Gap %: -8.89
Premarket volume: 2.1m (12% avg, 13.3% float)
Short %: 23.8
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Continued selloff with -8.89% gap as the post-parabolic collapse extended. Oil prices remained elevated with the Strait of Hormuz still closed, but BATL's speculative rally had exhausted itself. Short interest increased to 23.8% as traders positioned for further downside. The stock remained highly volatile with premarket volume at 13.3% of float.

## Ticker: BATL Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 20.1m
Gap %: 32.92
Premarket volume: 4.4m (22% avg, 28.4% float)
Short %: 24.6
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL bounced 32.92% as oil prices accelerated toward $100/barrel and the company announced a $15M private placement closing. The stock surged 15.79% in after-hours to $22 following the broader oil sector rally from Strait of Hormuz tensions. Despite the bounce, the company's 7-day return exceeded 3x and YTD return showed extreme momentum. Market cap reached $312.67M with RSI at 69.85, signaling overbought conditions. Premarket volume hit 28.4% of float as speculation continued.

## Ticker: BATL Date: 2026-03-09

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 21.8m
Gap %: 11.14
Premarket volume: 2.6m (12% avg, 16.6% float)
Short %: 23.3
Catalyst: Sector Momentum

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-09_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-09.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL gained 11.14% as oil prices hit peak crisis levels with Brent approaching $110/barrel. The company completed its $15M private placement, raising capital during the speculative rally. Stock surged 130% over the week as the Strait of Hormuz crisis intensified. Despite the rally, fundamental weakness remained with negative cash flow and heavy debt load. Premarket volume at 16.6% of float showed continued speculative trading.

## Ticker: BATL Date: 2026-03-10

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 15.5m
Avg. Volume: 21.4m
Gap %: 1.39
Premarket volume: 1.6m (8% avg, 10.6% float)
Short %: 24.8
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-10_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/BATL_2026-03-10.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

BATL announced acquisition of 7,090 net acres in Ward County, Texas from RoadRunner Resource Holding for 485,000 shares (all-stock transaction). The deal added ~30 high-quality drilling locations and expanded the Monument Draw position. Combined with the completed $15M private placement, the company executed capital raise and asset acquisition during the oil price spike. The modest 1.39% gap reflected consolidation as the speculative frenzy cooled despite the strategic acquisition.

## Ticker: MOBX Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 86.2m
Avg. Volume: 25.8m
Gap %: 158.64
Premarket volume: 167.1m (647% avg, 193.8% float)
Short %: 30
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MOBX exploded 141% (premarket gains as high as 191%) following announcement of a U.S. Navy production order for high-reliability filtering components used in Tomahawk cruise missiles. The components protect sensitive onboard electronics from electromagnetic interference. CEO Phil Sansone stated the order reflects "active and ongoing production demand" within an operational Navy weapons platform. Despite the defense contract, MOBX remained a micro-cap penny stock ($18.24M market cap) facing Nasdaq delisting by April 27 if unable to maintain $1 minimum bid. Premarket volume hit 193.8% of float (647% of average) with 30% short interest reflecting extreme skepticism about the chronically unprofitable company's fundamentals.

## Ticker: MOBX Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 86.2m
Avg. Volume: 94.9m
Gap %: 8.93
Premarket volume: 95.1m (100% avg, 110.3% float)
Short %: 30
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Second day continuation following March 3 announcement of U.S. Navy production order for high-reliability filtering components used in Tomahawk cruise missiles. Stock had surged 141% on March 3 and rallied sixfold during the week. Despite the defense contract, MOBX remained a micro-cap penny stock ($18.24M market cap) with chronic unprofitability, delisting risk (must maintain $1 minimum bid by April 27), and cash burn issues. Premarket volume hit 110.3% of float with 30% short interest reflecting extreme skepticism beneath the speculative rally.

## Ticker: MOBX Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 86.2m
Avg. Volume: 117.5m
Gap %: 21.95
Premarket volume: 87.4m (74% avg, 101.4% float)
Short %: 30.2
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MOBX extended the parabolic move with another 21.95% gap as momentum traders piled into the Navy contract story. Premarket volume remained extreme at 101.4% of float. TipRanks' AI analyst rated MOBX as "Neutral" citing "very weak financial performance" and absence of Wall Street analyst coverage. The company faced Nasdaq delisting deadline of April 27 to maintain $1 minimum bid. Short interest held at 30.2% as skeptics bet against the fundamentally weak micro-cap despite the defense contract.

## Ticker: MOBX Date: 2026-03-06

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 86.2m
Avg. Volume: 113.7m
Gap %: 18.95
Premarket volume: 27.5m (24% avg, 31.9% float)
Short %: 30.2
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-06_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MOBX_2026-03-06.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MOBX continued the speculative rally with another 18.95% gap, completing a multi-day parabolic move on the Navy Tomahawk contract. Premarket volume cooled to 31.9% of float (down from 100%+ on prior days), signaling waning momentum. The stock remained fundamentally weak with short-term obligations exceeding liquid assets and rapid cash burn. CEO Phil Sansone emphasized the order reflected "active and ongoing production demand," but analysts warned of the micro-cap's chronic unprofitability and imminent delisting risk.

## Ticker: UGRO Date: 2026-03-23

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 671.0k
Avg. Volume: 75.6k
Gap %: 64.22
Premarket volume: 28.1m (37106% avg, 4182.2% float)
Short %: 19.3
Catalyst: Merger

### Technical Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-23_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-23.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

UGRO exploded 63-68% following announcement that it regained full Nasdaq compliance on March 9, removing delisting risk. The rally was amplified by the company's February merger with Flash Sports & Media, pivoting from controlled-environment agriculture to sports/media/live events in the T20 cricket ecosystem. With only 671k float after a 1-for-25 reverse stock split, premarket volume hit an astronomical 4182.2% of float (28.1M shares), signaling extreme speculative frenzy and short covering in the micro-cap name.

## Ticker: UGRO Date: 2026-03-24

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 671.0k
Avg. Volume: 8.2m
Gap %: 31.54
Premarket volume: 7.4m (90% avg, 1098.5% float)
Short %: 20.5
Catalyst: Merger

### Technical Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-24_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-24.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Second day parabolic move with another 31.54% gap as momentum traders continued piling into the Nasdaq compliance and Flash Sports & Media merger story. Premarket volume remained extreme at 1098.5% of float. The ultra-low 671k float amplified price movements as retail investors chased the speculative rally despite the company's negative profit margins and recent 1-for-25 reverse stock split.

## Ticker: UGRO Date: 2026-03-25

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 671.0k
Avg. Volume: 9.6m
Gap %: 15.10
Premarket volume: 3.4m (35% avg, 503.6% float)
Short %: 19.6
Catalyst: Merger

### Technical Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-25_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-25.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

UGRO extended the parabolic move with another 15.10% gap, though momentum was clearly slowing from prior days. Premarket volume cooled to 503.6% of float (down from 1098.5% and 4182.2% on prior days). The stock remained fundamentally weak with negative profit margins despite the strategic pivot to sports/media. The declining gap percentage and volume signaled exhaustion of the speculative rally.

## Ticker: UGRO Date: 2026-03-26

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 671.0k
Avg. Volume: 13.5m
Gap %: -30.28
Premarket volume: 1.2m (9% avg, 186.2% float)
Short %: 24.6
Catalyst: Merger

### Technical Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-26_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Pump And Dump
Play: Parabolic Reversal

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/UGRO_2026-03-26.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

The parabolic move collapsed with a -30.28% gap as profit-taking accelerated. Short interest spiked to 24.6% as traders positioned against the fundamentally weak micro-cap. Despite the three-day rally on Nasdaq compliance and the Flash Sports & Media merger, the company's negative profit margins and recent reverse stock split caught up with the speculative frenzy. Premarket volume remained elevated at 186.2% of float as the pump-and-dump pattern completed.

## Ticker: ARM Date: 2026-03-25

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 1.1b
Avg. Volume: 4.8m
Gap %: 9.85
Premarket volume: 828.3k (17% avg, 0.1% float)
Short %: 22.1
Catalyst: Product Launch

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ARM_2026-03-25_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Changing Fundamentals
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ARM_2026-03-25.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

ARM announced a major business model shift, moving beyond IP licensing to produce and sell its own AGI CPUs designed for agentic AI workloads. Meta became the first customer for the new processors, with ARM projecting $15B in annual chip revenue by fiscal 2031. The stock surged 16%+ following the announcement as analysts upgraded their outlooks, with Guggenheim raising its target to $240 and Raymond James upgrading to "outperform."

## Ticker: DOCN Date: 2026-03-25

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 67.5m
Avg. Volume: 3.6m
Gap %: -7.21
Premarket volume: 386.0k (11% avg, 0.6% float)
Short %: 29.9
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/DOCN_2026-03-25_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/DOCN_2026-03-25.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

DOCN announced an upsized public offering of 10.4M shares at $74.40, increasing the raise from $700M to $800M. The company plans to use proceeds for infrastructure capacity expansion to support AI platform demand, pay down Term Loan A debt, and general corporate purposes. The stock fell over 7% on dilution concerns before recovering to close at $87.00.

## Ticker: AGX Date: 2026-03-27

### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 13.3m
Avg. Volume: 549.5k
Gap %: 23.06
Premarket volume: 7.4k (1% avg, 0.1% float)
Short %: 20.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AGX_2026-03-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AGX_2026-03-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

AGX surged 38% to an all-time high of $566.62 after reporting Q4 FY2026 earnings of $3.47 per share, crushing the $1.99 consensus. Revenue hit $262.1M with a record $2.9B project backlog driven by data center and AI power infrastructure demand. JPMorgan upgraded the stock from Neutral to Overweight with a $550 target, citing the company's debt-free balance sheet and strong positioning in the power infrastructure buildout.

## Ticker: U Date: 2026-03-27

### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 348.0m
Avg. Volume: 13.7m
Gap %: 18.07
Premarket volume: 1.6m (11% avg, 0.4% float)
Short %: 7.4
Catalyst: Strong Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/U_2026-03-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Changing Fundamentals
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/U_2026-03-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Unity surged 12.8% after raising preliminary Q1 2026 revenue guidance to $505-508M (vs $480-490M prior) and adjusted EBITDA to $130-135M (vs $105-110M prior). The beat was driven by Unity Vector, the company's AI-powered advertising platform. Unity announced strategic streamlining: shutting down ironSource Ads Network (April 30), exploring sale of Supersonic game publishing business, and potential $1B+ sale of China division. Bank of America raised price targets following the operational simplification.

## Ticker: MDB Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 77.8m
Avg. Volume: 1.9m
Gap %: -27.57
Premarket volume: 354.2k (18% avg, 0.5% float)
Short %: 11.9
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MDB_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MDB_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

MDB plunged 27.57% despite beating Q4 FY2026 estimates with $1.65 EPS and $695.1M revenue (+27% YoY). Atlas revenue grew 29% and the company added 2,700 customers. The severe selloff despite strong results suggests investors reacted negatively to guidance, valuation concerns, or forward-looking commentary. Multiple analysts adjusted price targets following the volatile reaction.

## Ticker: MDB Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 77.8m
Avg. Volume: 2.5m
Gap %: 1.19
Premarket volume: 37.3k (1% avg, 0.0% float)
Short %: 24.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/MDB_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/MDB_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day following the March 3 earnings-driven 27.57% selloff. The modest +1.19% gap reflects consolidation after the severe decline. Short interest spiked to 24.8% (from 11.9%) as traders positioned against the stock following the negative market reaction to earnings.

## Ticker: VG Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 477.3m
Avg. Volume: 11.1m
Gap %: 16.90
Premarket volume: 2.0m (18% avg, 0.4% float)
Short %: 18.7
Catalyst: Major Supply Disruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Venture Global (VG) surged 16.90% as the Strait of Hormuz crisis intensified, driving LNG prices sharply higher. With maritime transit through the strait dropping 70% and oil prices climbing toward $90-94/barrel, global LNG demand spiked as buyers sought alternative energy sources. As a major U.S. LNG producer and exporter, VG benefited directly from the supply disruption affecting Middle East energy flows.

## Ticker: VG Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 477.3m
Avg. Volume: 13.1m
Gap %: -4.46
Premarket volume: 516.5k (4% avg, 0.1% float)
Short %: 16.6
Catalyst: Major Supply Disruption

### Technical Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/VG_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 3 LNG rally driven by the Strait of Hormuz crisis. The -4.46% gap reflects profit-taking after the 16.90% surge, though the Strait remained closed and oil prices stayed elevated. Short interest dropped to 16.6% as shorts covered into the energy sector rally.

## Ticker: SE Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 297.2m
Avg. Volume: 6.3m
Gap %: -23.58
Premarket volume: 1.3m (20% avg, 0.4% float)
Short %: 13.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

SE plunged 23.58% after reporting Q4 2025 earnings with a significant EPS miss ($0.63 vs $0.91 expected) despite beating revenue estimates ($6.85B vs $6.42B, +38.4% YoY). Full-year revenue hit $23B (+36% YoY) with $1.6B net income. The severe selloff reflected investor disappointment with profitability metrics despite strong top-line growth across Shopee, Garena, and Monee segments.

## Ticker: SE Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 297.2m
Avg. Volume: 7.7m
Gap %: 0.20
Premarket volume: 103.6k (1% avg, 0.0% float)
Short %: 13.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 3 earnings-driven 23.58% selloff. The minimal +0.20% gap reflects stabilization after the severe decline, with the stock hitting a new 52-week low. Management's FY2026 guidance targeting 25% GMV growth while maintaining EBITDA levels provided some support.

## Ticker: SE Date: 2026-03-05

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 297.2m
Avg. Volume: 7.7m
Gap %: 1.33
Premarket volume: 60.7k (1% avg, 0.0% float)
Short %: 8.1
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-05_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/SE_2026-03-05.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Third day following the March 3 earnings disappointment. The modest +1.33% gap reflects continued consolidation after the two-day selloff. Short interest dropped sharply to 8.1% (from 13.8%) as shorts covered following the extended decline.

## Ticker: NRG Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 185.2m
Avg. Volume: 2.3m
Gap %: -7.25
Premarket volume: 401.6k (17% avg, 0.2% float)
Short %: 18.5
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NRG_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NRG_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NRG fell 7.7% after LS Power affiliates priced an upsized secondary offering of 14.3M shares at $164 (7% discount to prior close of $175.58). The shares were part of consideration from NRG's $12B acquisition of LS Power's portfolio that closed January 30. NRG announced a concurrent $300M share repurchase at the offering price to signal confidence and mitigate dilution impact.

## Ticker: NRG Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 185.2m
Avg. Volume: 2.8m
Gap %: 0.01
Premarket volume: 3.1k (0% avg, 0.0% float)
Short %: 11.5
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NRG_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NRG_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 3 secondary offering selloff. The flat +0.01% gap reflects stabilization after the 7.7% decline. Short interest dropped to 11.5% (from 18.5%) as the offering completed and the $300M share repurchase provided support.

## Ticker: ONON Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 220.3m
Avg. Volume: 4.7m
Gap %: -12.23
Premarket volume: 392.2k (8% avg, 0.2% float)
Short %: 32.4
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ONON_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ONON_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

ONON plunged 12%+ after reporting Q4 2025 results with strong performance (CHF 743.8M revenue, +23% YoY, 63.9% gross margin) but disappointing FY2026 guidance. The company projected 23% net sales growth to CHF 3.44B, below consensus estimates of CHF 3.67-3.75B. Despite surpassing CHF 3B in annual sales, investors reacted negatively to signs of cooling growth momentum in the premium athletic footwear brand.

## Ticker: ONON Date: 2026-03-04

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 220.3m
Avg. Volume: 5.4m
Gap %: -1.57
Premarket volume: 20.8k (0% avg, 0.0% float)
Short %: 18
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/ONON_2026-03-04_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ONON_2026-03-04.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day following the March 3 earnings disappointment. The -1.57% gap reflects continued selling pressure after the initial 12%+ decline. Short interest dropped to 18% (from 32.4%) as shorts covered following the extended selloff. Many analysts maintained Buy ratings, viewing the guidance miss as potentially conservative.

## Ticker: AAOI Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 70.9m
Avg. Volume: 5.4m
Gap %: 22.80
Premarket volume: 369.4k (7% avg, 0.5% float)
Short %: 15.4
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AAOI_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AAOI_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

AAOI surged 44.7% following Q4 2025 earnings reported February 26. The company beat estimates with -$0.01 EPS (vs -$0.12 expected) and $134.3M revenue (+34% YoY). Strong Q1 2026 guidance of $150-165M (vs $145.6M consensus) driven by datacenter demand for 400G/800G/1.6T optical transceivers. Needham raised price target from $43 to $80, B. Riley upgraded from Sell to Neutral, reflecting optimism around AI infrastructure buildout.

## Ticker: AAOI Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 70.9m
Avg. Volume: 6.2m
Gap %: 27.69
Premarket volume: 2.1m (34% avg, 3.0% float)
Short %: 12.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AAOI_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AAOI_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

AAOI extended the parabolic move with another 27.69% gap as momentum traders piled into the AI infrastructure story. Premarket volume hit 3.0% of float (34% of average) as the stock continued rallying on datacenter demand for high-speed optical transceivers. Short interest dropped to 12.8% as shorts covered. The rally was part of broader momentum that would peak at $127.01 on March 11 before major contract announcements later in the month.

## Ticker: AVAV Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 70.9m
Avg. Volume: 1.1m
Gap %: 12.68
Premarket volume: 150.9k (13% avg, 0.2% float)
Short %: 19.5
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AVAV_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AVAV_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

AVAV initially rallied 20.1% on weekend Iran attack news as defense stocks surged, but reversed sharply to close down 17.42% after Space News reported the Pentagon was reopening the $1.4B SCAR satellite communications program for bidding. The contract had been awarded to AVAV's BlueHalo subsidiary. Raymond James downgraded from Strong Buy to Underperform, triggering the selloff as investors feared losing the major contract.

## Ticker: AVAV Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 70.9m
Avg. Volume: 1.8m
Gap %: 3.05
Premarket volume: 177.1k (10% avg, 0.2% float)
Short %: 26.2
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AVAV_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AVAV_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Second day following the March 2 SCAR contract reopening news and Raymond James downgrade. The modest +3.05% gap reflects consolidation after the 17.42% decline. Short interest spiked to 26.2% (from 19.5%) as traders positioned against the stock amid uncertainty over the $1.4B BlueHalo contract.

## Ticker: AES Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 708.5m
Avg. Volume: 9.9m
Gap %: 5.04
Premarket volume: 640.6k (6% avg, 0.1% float)
Short %: 12
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/AES_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/AES_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

AES surged 6.3% to a 52-week high of $17.65 on takeover speculation. Market rumors indicated interest from a consortium led by Global Infrastructure Partners (GIP) and EQT AB. Trading volume exceeded 26M shares as investors bet on a buyout premium. The company held its Q4 2025 earnings call that morning. On March 2, a definitive agreement was announced at $15.00/share to take the company private.

## Ticker: KTOS Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 183.4m
Avg. Volume: 3.9m
Gap %: 7.24
Premarket volume: 619.4k (16% avg, 0.3% float)
Short %: 11.1
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/KTOS_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/KTOS_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

KTOS rallied 7.24% as the company closed its underwritten public offering of 14.3M shares at $84.00/share, raising $1.17B in net proceeds. Funds earmarked for scaling operations, National Security Systems development, acquisitions (Nomad and Orbit), and strengthening the balance sheet for contract opportunities. SSC Space deployed Kratos' OpenSpace platform for LEO missions. Defense sector momentum from Iran tensions provided additional support.

## Ticker: KTOS Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 183.4m
Avg. Volume: 4.7m
Gap %: -1.67
Premarket volume: 137.1k (3% avg, 0.1% float)
Short %: 18
Catalyst: Offering

### Technical Analysis
<div class="chart-placeholder" data-src="charts/KTOS_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/KTOS_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 2 public offering close. The -1.67% gap reflects modest profit-taking after the 7.24% rally. Short interest increased to 18% (from 11.1%) as traders positioned against the stock following the dilutive offering despite strong use of proceeds.

## Ticker: RCAT Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 106.4m
Avg. Volume: 9.4m
Gap %: 8.93
Premarket volume: 631.5k (7% avg, 0.6% float)
Short %: 28.9
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/RCAT_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/RCAT_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

RCAT rallied 8.93% as the company announced Allen Control Systems (ACS) joined the "Red Cat Futures" initiative to advance autonomous counter-drone and precision defense capabilities. Defense sector momentum from Iran tensions provided additional support. Needham maintained Buy rating with $16 target, while Ladenburg Thalmann issued Buy with $20 target on March 3. High short interest of 28.9% amplified the move.

## Ticker: RCAT Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 106.4m
Avg. Volume: 10.8m
Gap %: 6.88
Premarket volume: 918.1k (8% avg, 0.9% float)
Short %: 16.3
Catalyst: Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/RCAT_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/RCAT_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Second day continuation following the March 2 ACS partnership announcement. The stock extended gains with another 6.88% gap as Ladenburg Thalmann issued a Buy rating with $20 target. Short interest dropped to 16.3% (from 28.9%) as shorts covered into the defense sector rally driven by Iran tensions and the Red Cat Futures initiative momentum.

## Ticker: LASR Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 51.6m
Avg. Volume: 1.7m
Gap %: -11.04
Premarket volume: 20.6k (1% avg, 0.0% float)
Short %: 27
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

LASR gapped down 11.04% despite beating Q4 2025 estimates with $0.14 EPS (vs $0.11 expected) and $81.19M revenue (vs $76.7M, +71.3% YoY). The selloff reflected concerns over negative net margins and negative ROE despite strong top-line growth. Needham raised target to $70 and Cantor Fitzgerald to $62.50, but high short interest (27%) and profitability concerns drove the decline. The company announced participation in investor events to increase visibility.

## Ticker: LASR Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 51.6m
Avg. Volume: 1.8m
Gap %: 20.15
Premarket volume: 243.5k (13% avg, 0.5% float)
Short %: 18.3
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

LASR bounced 20.15% following the February 27 earnings selloff. The sharp reversal reflected short covering (short interest dropped to 18.3% from 27%) and bargain hunting after the oversold decline. Analyst upgrades (Needham to $70, Cantor Fitzgerald to $62.50) provided support as investors focused on the strong 71.3% YoY revenue growth rather than profitability concerns.

## Ticker: LASR Date: 2026-03-03

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 51.6m
Avg. Volume: 2.0m
Gap %: -0.07
Premarket volume: 147.8k (7% avg, 0.3% float)
Short %: 23.8
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-03-03_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/LASR_2026-03-03.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Consolidation day following the March 2 bounce from the earnings selloff. The flat -0.07% gap reflects stabilization after the volatile two-day swing. Short interest increased to 23.8% (from 18.3%) as traders re-established short positions following the 20% bounce, betting on continued profitability concerns despite strong revenue growth.

## Ticker: DUOL Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 28.9m
Avg. Volume: 3.0m
Gap %: -21.45
Premarket volume: 1.4m (48% avg, 4.9% float)
Short %: 15.7
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/DUOL_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/DUOL_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

DUOL gapped down 21.45% despite beating Q4 2025 earnings estimates. The company reported strong results but announced a strategic shift to prioritize user growth over short-term financial metrics. This pivot concerned investors focused on near-term profitability, triggering the selloff despite the earnings beat. The 48% premarket volume (4.9% of float) reflected heavy institutional repositioning ahead of the open.

## Ticker: DUOL Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 28.9m
Avg. Volume: 3.8m
Gap %: -2.57
Premarket volume: 68.3k (2% avg, 0.2% float)
Short %: 14
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/DUOL_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/DUOL_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Continued weakness following the February 27 earnings selloff. The -2.57% gap reflects ongoing selling pressure as investors digested the strategic shift toward user growth over profitability. Short interest declined slightly to 14% (from 15.7%) as some shorts took profits after the 21% initial drop.

## Ticker: DELL Date: 2026-02-27

### Big Picture

Market Momentum: Positive

### Intraday Fundamentals

Float: 296.5m
Avg. Volume: 8.1m
Gap %: 13.18
Premarket volume: 977.2k (12% avg, 0.3% float)
Short %: 13.7
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/DELL_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/DELL_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

DELL surged 13.18% on Q4 FY2026 earnings beat driven by explosive AI server demand. The company reported 342% YoY growth in AI-optimized servers with a $43B AI backlog. Strong results across infrastructure and client solutions validated Dell's positioning in the AI infrastructure buildout. The 12% premarket volume reflected institutional accumulation ahead of the gap up.

## Ticker: DELL Date: 2026-03-02

### Big Picture

Market Momentum: Positive

### Intraday Fundamentals

Float: 296.5m
Avg. Volume: 9.4m
Gap %: -0.90
Premarket volume: 238.1k (3% avg, 0.1% float)
Short %: 15.9
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/DELL_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/DELL_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Minor consolidation following the February 27 earnings surge. The -0.90% gap reflects profit-taking after the 13% rally, but the AI server momentum story remained intact. Short interest increased to 15.9% (from 13.7%) as traders bet on a pullback after the sharp move.

## Ticker: XYZ Date: 2026-02-27

### Big Picture

Market Momentum: Unknown

### Intraday Fundamentals

Float: 524.4m
Avg. Volume: 9.7m
Gap %: 15.70
Premarket volume: 1.3m (13% avg, 0.2% float)
Short %: 10.6
Catalyst: Unknown

### Technical Analysis
<div class="chart-placeholder" data-src="charts/XYZ_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Unknown
Play: Unknown

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/XYZ_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Note: XYZ (ExamWorks) is not a valid publicly traded stock as of this date. The company was acquired by Leonard Green & Partners in 2016 and taken private. This entry may represent data quality issues or a different ticker symbol.

## Ticker: XYZ Date: 2026-03-02

### Big Picture

Market Momentum: Unknown

### Intraday Fundamentals

Float: 524.4m
Avg. Volume: 11.4m
Gap %: -5.79
Premarket volume: 222.3k (2% avg, 0.0% float)
Short %: 22.9
Catalyst: Unknown

### Technical Analysis
<div class="chart-placeholder" data-src="charts/XYZ_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Unknown
Play: Unknown

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/XYZ_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

Note: XYZ (ExamWorks) is not a valid publicly traded stock as of this date. The company was acquired by Leonard Green & Partners in 2016 and taken private. This entry may represent data quality issues or a different ticker symbol.

## Ticker: NFLX Date: 2026-02-27

### Big Picture

Market Momentum: Positive

### Intraday Fundamentals

Float: 4.2b
Avg. Volume: 46.3m
Gap %: 11.47
Premarket volume: 4.8m (10% avg, 0.1% float)
Short %: 14.6
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NFLX_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NFLX_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

NFLX surged 11.47% after walking away from its proposed acquisition of Warner Bros Discovery and collecting a $2.8B breakup fee. The market viewed this as a win-win: Netflix avoided integration risks and debt burden while gaining substantial cash. The decision signaled management discipline and strengthened the balance sheet, driving the rally.

## Ticker: NFLX Date: 2026-03-02

### Big Picture

Market Momentum: Positive

### Intraday Fundamentals

Float: 4.2b
Avg. Volume: 54.2m
Gap %: -1.02
Premarket volume: 1.9m (3% avg, 0.0% float)
Short %: 19.8
Catalyst: Major Deal

### Technical Analysis
<div class="chart-placeholder" data-src="charts/NFLX_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/NFLX_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Minor consolidation following the February 27 breakup fee rally. The -1.02% gap reflects profit-taking after the 11% surge, but the positive sentiment around the deal termination remained. Short interest increased to 19.8% (from 14.6%) as traders bet on a pullback.

## Ticker: RUN Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 226.0m
Avg. Volume: 8.2m
Gap %: -8.96
Premarket volume: 352.3k (4% avg, 0.2% float)
Short %: 17.9
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/RUN_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/RUN_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

RUN gapped down 8.96% despite beating Q4 2025 estimates with $1.16B revenue and $0.38 EPS. The selloff was driven by cautious 2026 guidance announcing a strategic pivot toward margin-focused growth, including plans to reduce affiliate partner volumes by over 40% and a 30% decrease in net subscriber value. The market reacted negatively to the volume reduction despite the profitability focus.

## Ticker: RUN Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 226.0m
Avg. Volume: 10.6m
Gap %: -2.64
Premarket volume: 204.1k (2% avg, 0.1% float)
Short %: 28.5
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/RUN_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/RUN_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Continued weakness following the February 27 earnings selloff. The -2.64% gap reflects ongoing concerns about the 40% reduction in affiliate partner volumes. Short interest surged to 28.5% (from 17.9%) as traders bet on further downside from the strategic pivot away from growth.

## Ticker: FLUT Date: 2026-02-27

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 142.3m
Avg. Volume: 4.8m
Gap %: -10.63
Premarket volume: 186.7k (4% avg, 0.1% float)
Short %: 15.1
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/FLUT_2026-02-27_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/FLUT_2026-02-27.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

FLUT gapped down 10.63% after Q4 2025 earnings missed expectations with $4.74B revenue and $1.74 EPS both below analyst estimates. The company provided tempered 2026 guidance projecting $18.4B revenue and $2.97B adjusted EBITDA (at midpoints), falling short of market expectations. Management cited moderation in market growth and bookmaker-friendly sports results as headwinds despite maintaining strong U.S. market leadership with FanDuel.

## Ticker: FLUT Date: 2026-03-02

### Big Picture

Market Momentum: Neutral

### Intraday Fundamentals

Float: 142.3m
Avg. Volume: 5.8m
Gap %: -2.38
Premarket volume: 20.6k (0% avg, 0.0% float)
Short %: 15.9
Catalyst: Earnings Surprise

### Technical Analysis
<div class="chart-placeholder" data-src="charts/FLUT_2026-03-02_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/FLUT_2026-03-02.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

No new catalyst. Continued weakness following the February 27 earnings miss. The -2.38% gap reflects ongoing selling pressure as investors digested the disappointing Q4 results and tempered 2026 guidance. Short interest increased slightly to 15.9% (from 15.1%) as traders maintained bearish positions.

