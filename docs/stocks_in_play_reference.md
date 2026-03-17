# Overview

The reference document for the stocks in play that will serve as the basis for the generative models. Here is a quick conversion script that should be run from the parent directory in order to convert this markdown file into .html for viewing in the browser.

```bash
pip install markdown
python3 -c "import markdown; print(markdown.markdown(open('docs/stocks_in_play_reference.md').read(), extensions=['extra']))" > docs/stocks_in_play_reference.html
```

# List

The charts are volume charts. Unlike regular time based charts, these volume charts are built by grouping the trades into fixed size blocks and calculating their Volume Weighted Average Price and the Volume Weighted Standard Deviation. I found this to make analysis far easier than with time based charts. The bottom panel on the charts is the trade duration. When a trade goes over the block size limit, it is split and its remainder is passed into the next block. For large blocks this might entail splitting it up multiple times until the entire trade is consumed.

## Ticker: LW Date: 2025-12-19
<iframe src="charts/LW_2025-12-19.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: MSTR Date: 2024-11-21
<iframe src="charts/MSTR_2024-11-21.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2025-09-09
<iframe src="charts/NBIS_2025-09-09.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: NBIS Date: 2025-09-10
<iframe src="charts/NBIS_2025-09-10.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>

## Ticker: OPEN Date: 2025-09-11
<iframe src="charts/OPEN_2025-09-11.html" width="100%" height="100%" style="border: 1px solid #ccc;"></iframe>