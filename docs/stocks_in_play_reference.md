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
<iframe src="charts/LW_2025-12-19_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/LW_2025-12-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/NBIS_2025-09-10_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Jumpy
Play: Second Day

### Orderflow Analysis
<iframe src="charts/NBIS_2025-09-10.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/NBIS_2025-09-09_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis

<iframe src="charts/NBIS_2025-09-09.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/MSTR_2024-11-21_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Parabolic Reversal

### Orderflow Analysis
<iframe src="charts/MSTR_2024-11-21.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/OPEN_2025-09-11_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/OPEN_2025-09-11.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/SMCI_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Battleground
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/SMCI_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: PL Date: 2026-03-20
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 260.0m
Avg. Volume: 10.7m
Gap %: 24.44%
Premarket volume: 3.1m (29% avg, 1.2% float)
Short %: 13.2
Catalyst: Strong Earnings Surprise

### Technical Analysis
<iframe src="charts/PL_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Jumpy
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/PL_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/FDX_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/FDX_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/ARM_2026-03-20_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Neutral Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/ARM_2026-03-20.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: VG Date: 2026-03-19
### Big Picture

Market Momentum: Negative

### Intraday Fundamentals

Float: 477.0m
Avg. Volume: 24.1m
Gap %: 6.06%
Premarket volume: 3.7m (16% avg, 0.8% float)
Short %: 20.9%%
Catalyst: Breakout

### Technical Analysis
<iframe src="charts/VG_2026-03-19_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<iframe src="charts/VG_2026-03-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/NBIS_2026-03-16_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/NBIS_2026-03-16.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/NBIS_2026-03-17_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

### Orderflow Analysis
<iframe src="charts/NBIS_2026-03-17.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Second Day

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
<iframe src="charts/TME_2026-03-17_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Negative Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/TME_2026-03-17.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/TME_2026-03-18_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Negative Momentum
Play: Second Day

### Orderflow Analysis
<iframe src="charts/TME_2026-03-18.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/ULTA_2026-03-13_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Changing Fundamentals

### Orderflow Analysis
<iframe src="charts/ULTA_2026-03-13.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

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
<iframe src="charts/ULTA_2026-03-16_daily.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

Overall Pattern: Strong Positive Momentum
Play: Second Day

### Orderflow Analysis
<iframe src="charts/ULTA_2026-03-16.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>
