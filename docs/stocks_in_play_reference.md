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
Gap %: -14.39%
Premarket volume: 338.0k (27% avg, 0.2% float)
Short %: 15.3%
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
Gap %: 51.73%
Premarket volume: 7.9m (71% avg, 3.9% float)
Short %: 22.3%
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
Gap %: -4.26%
Premarket volume: 3.9m (27% avg, 1.9% float)
Short %: 7.5%
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
Gap %: 13.04%
Premarket volume: 7.4m (24% avg, 2.4% float)
Short %: 17.8%
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
Gap %: 29.86%
Premarket volume: 77.0m (19% avg, 9.7% float)
Short %: 19.5%
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
Gap %: -26.86%
Premarket volume: 32.5m (132% avg, 6.3% float)
Short %: 15.8%
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
Gap %: -1.07%
Premarket volume: 7.7m (22% avg, 1.5% float)
Short %: 17.9%
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
Gap %: 24.44%
Premarket volume: 3.1m (29% avg, 1.2% float)
Short %: 13.2%
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
Gap %: 6.94%
Premarket volume: 216.9k (12% avg, 0.1% float)
Short %: 9.1%
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
Gap %: 5.45%
Premarket volume: 246.5k (6% avg, 0.0% float)
Short %: 20.6%
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
Gap %: 6.06%
Premarket volume: 3.7m (16% avg, 0.8% float)
Short %: 20.9%
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
Gap %: 10.45%
Premarket volume: 3.7m (28% avg, 1.8% float)
Short %: 17.2%
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
Gap %: -7.32%
Premarket volume: 3.4m (24% avg, 1.7% float)
Short %: 17.2%
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
Gap %: -15.35%
Premarket volume: 1.7m (28% avg, 0.3% float)
Short %: 23.3%
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
Gap %: -0.18%
Premarket volume: 799.0k (9% avg, 0.1% float)
Short %: 40.2%
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
Gap %: -9.35%
Premarket volume: 45.6k (8% avg, 0.1% float)
Short %: 20.3%
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
Gap %: -0.52%
Premarket volume: 28.7k (4% avg, 0.1% float)
Short %: 24.8%
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
Gap %: 57.99%
Premarket volume: 178.0m (310% avg, 40.6% float)
Short %: 38.6%
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
Gap %: 57.14%
Premarket volume: 244.4m (208% avg, 55.8% float)
Short %: 22.1%
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
Gap %: 70.44%
Premarket volume: 501.9m (227% avg, 114.6% float)
Short %: 26.2%
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
Gap %: 6.57%
Premarket volume: 737.4k (9% avg, 0.2% float)
Short %: 17.9%
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
Gap %: 6.57%
Premarket volume: 195.0k (5% avg, 0.1% float)
Short %: 29.8%
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
Gap %: 11.40%
Premarket volume: 5.2m (20% avg, 0.3% float)
Short %: 15.4%
Catalyst: Earnings Surprise

### Technical Analysis

<div class="chart-placeholder" data-src="charts/ORCL_2026-03-11_daily.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

Overall Pattern: Negative Momentum
Play: Changing Fundamentals 

### Orderflow Analysis
<div class="chart-placeholder" data-src="charts/ORCL_2026-03-11.html" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;">Click to load chart</div>

### News Summary

ORCL surged 10% after beating Q3 earnings with EPS of $1.79 (vs $1.70 consensus) and revenue of $17.19B (+22% YoY). Cloud revenue grew 44% with infrastructure up 84%, driven by AI demand. The company reported a massive $553B backlog including a $300B OpenAI contract and raised FY2027 revenue guidance to $90B. JPMorgan upgraded the stock citing proof of AI strategy success.
