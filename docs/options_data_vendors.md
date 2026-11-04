# Options data: ThetaData vs Massive (2026-11-04)

A comparison of the two vendors' options data, for research on options-derived features (gamma / open-interest levels
and the like) and for a possible live feed later.
- **Sources:** each vendor's own pages, plus third-party write-ups (listed at the end).
- **Prices and history start dates:** from the vendors' own pages.
- **Where sources conflicted:** noted in the table. Check the current pricing pages before buying.

## The offerings

| | **ThetaData** (individual tiers) | **Massive** (individual tiers) |
|---|---|---|
| Price per month | Value \$40 · Standard \$80 · Pro \$160 | Starter \$29 and Developer \$79 (15-minute delayed) · Advanced \$199 (real-time) · Business \$1,999 |
| Tick quotes: how far back | Standard from 2016 · Pro from 2012-06 | Advanced only. Massive's business page says quotes go back only to 2022 ("2.5 years"). |
| Tick trades: how far back | Standard from 2016 · Pro from 2012 | Developer and Advanced; "5+ years" on Advanced, 2016 on the business page |
| Value tier | 1-minute bars from 2020, real-time | — |
| Greeks / implied vol | Historical, 1st to 3rd order, plus open-interest history, from the API | Snapshots ("real-time Greeks and IV, daily open interest"); historical greeks are not advertised |
| How the data is accessed | A local "Theta Terminal" app; queries per contract or per expiry; 4 concurrent requests (Standard) or 8 (Pro) | S3 flat files (whole-market daily CSVs straight from OPRA: trades, top-of-book quotes, minute and day bars) plus a REST API with no call limit |
| Index options | Index data included; NDX needs Pro | OPRA carries the SPX and NDX options; the index values themselves need Massive's indices plan |
| Options on futures (CME) | Not offered | Not offered (Massive has CME futures, but no options on them) |
| Use terms | Individual / non-professional; commercial use \$1,600 a month | Individual use only; Business \$1,999 a month |

## Our take

- **For research, ThetaData.**
  - A study of gamma or open-interest levels needs years of quotes, open interest and greeks per strike, across
    several volatility regimes.
  - ThetaData's Standard tier (\$80) has tick quotes and trades from 2016, Pro (\$160) from 2012, with greeks and
    open interest stored historically.
  - Massive's Advanced tier (\$199) has quotes from only about 2022, roughly three years, and the greeks would have to
    be computed ourselves.
- **Massive's strength is bulk files.** Whole-market daily S3 dumps suit a pipeline that downloads everything.
  - The catch is size: full OPRA top-of-book quotes for one day run very large (likely hundreds of GB even
    compressed), so storing years of them is impractical anyway.
  - A study on a handful of underlyings (SPY, QQQ, SPX, a list of single names) fits ThetaData's per-root,
    per-expiry queries better, even with the concurrency limits.
- **For a live feed, either works.** Both have real-time data on their paid tiers.
- **Options on futures (ES / NQ gamma levels) are covered by neither.** They need CME data, for example Databento's
  GLBX feed, which carries the options chains.

**Suggested next step:** one month of ThetaData Standard (\$80), or Pro if the 2012–15 history matters, to pull the
chains and open interest for a first study. Decide on any live feed only after the study finds an edge.

## Caveats

- **Third-party comparisons conflict.** They disagree with each other on prices and tiers, notably FlashAlpha, which
  sells a competing product. The table uses the vendors' own pages where they exist.
- **Massive's quote history is stated two ways** on the same business page (2.5 years vs "back to 2022"). The two
  agree roughly, but should be confirmed with them for the individual tier.
- **User reviews of throttling** (ThetaData fast, Polygon/Massive slow on large historical pulls) are anecdotal and
  self-selected.

## Sources

- ThetaData: [subscriptions (docs)](https://thetadata.net/docs/Articles/Getting-Started/Subscriptions.html) ·
  [pricing](https://www.thetadata.net/pricing) · [commercial pricing](https://thetadata.net/commercial-use) ·
  [options data](https://www.thetadata.net/options-data)
- Massive: [options plans](https://massive.com/options) · [business options](https://massive.com/business-options) ·
  [options flat files](https://massive.com/docs/flat-files/options/overview) · [flat files blog](https://massive.com/blog/flat-files)
- Third party: [FlashAlpha, options data pricing compared](https://flashalpha.com/articles/options-data-pricing-comparison-flashalpha-thetadata-polygon-spotgamma-squeezemetrics) ·
  [FlashAlpha, best options data APIs 2026](https://flashalpha.com/articles/best-options-data-apis-2026) ·
  [ThetaData reviews (Trustpilot)](https://au.trustpilot.com/review/thetadata.net) ·
  [Polygon reviews (Trustpilot)](https://au.trustpilot.com/review/polygon.io)
