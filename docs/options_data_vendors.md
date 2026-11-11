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

---

# Update 2026-11-11 — after the ThetaData cancellation (StraddleHarvester)

Context: sharing the trading partner's keys keeps colliding (he cancelled ThetaData mid-pull; Massive throttles his
~10 TB/month quote pull to ~5 MB/s while he builds 1-minute bars). The user wants a source of OUR OWN. Needs:
(1) EOD NBBO per strike for the IV-vs-RV backtests, as far back as possible; (2) later, 1-minute NBBO for fill sims and
intraday entries. Figures are from the vendors' own pages, fetched 2026-11-11; confirm before buying.

| vendor / product | what | history | delivery | individual price |
|---|---|---|---|---|
| ⭐ **ORATS Near-EOD** | every strike × expiry, ~6,000 symbols (delisted included): NBBO bid/ask for calls and puts at **~15:46 ET**, bid/mid/ask IV, smoothed IV, greeks | **2007 →** | one-time S3 download (~500 GB, 14-day window); daily FTP for new data | **\$599 one-time** history; \$99/mo for new days |
| ⭐ **ORATS 1-Minute** | every option, **every minute 09:30–16:15**: NBBO bid/ask + sizes, IVs, greeks, volume, OI | **2020-08 →** | Parquet in ORATS's S3 (query in place, or copy — **Requester Pays**, so a full copy out of AWS costs egress); hard drive available | **\$1,999 one-time** all symbols, then \$199/mo (1 symbol \$599, 10 symbols \$999) |
| Databento OPRA `cbbo-1m` | NBBO + last sale at 1-minute intervals | 2013-04 → | API / batch files | ⚠ L1 history (CBBO incl. cbbo-1m, trades) is **12 months on usage-based / Standard (\$199/mo)**, 3 years on Plus (\$1,750/mo, annual), 13 years only on **Unlimited (\$4,500/mo, annual)** |
| ThetaData Options Standard / Pro | per-root REST (EOD with closing NBBO, tick trades and quotes, IV, greeks) | 2016 / 2012-06 → | local Terminal, 4 / 8 concurrent; flat files Pro-only, 7 days (older = sales) | \$80 / \$160 per month |
| Massive Options Advanced | quotes REST per contract-day; quote flat files ~135 GB/day | quotes 2022-03-07 → | REST ~100 req/s ceiling (one contract-day per request); S3 throttled | \$199/mo |
| historicaloptiondata.com L1 HDALL | EOD prices with bid/ask at the close, no IV/greeks | 2002-02 → | download | price not confirmed |
| Cboe DataShop Option EOD Summary | NBBO snapshots at **15:45** and at the close, every series | 2004 → | files | quote-based (academic: 50% off, \$500 minimum) — likely the most expensive |

**Notes:**
- **ORATS's underlying price is derived from put-call parity** ("not stock prices"). Use our own stock data for the
  underlying.
- **ORATS sells an individual, single-user license.** Raw-data redistribution is not allowed, so the user and the
  partner each need their own — which is the point.
- **The 15:46 snapshot is a feature, not a flaw** for an EOD system: it is a time we can actually trade at, before the
  close's quote deterioration (Cboe uses 15:45 for the same reason).

**Recommendation:**

1. **ORATS Near-EOD history (\$599 one-time, 2007 →)** covers StraddleHarvester and the calendar study end to end:
   every strike and expiry, IV included, across ~19 years and several volatility regimes (2008, 2020, 2022).
2. **ORATS 1-Minute (\$1,999 one-time, 2020-08 →)** is the cheap answer to the partner's 1-minute-bar project. It is
   already 1-minute NBBO per option, so there is no 10 TB/month tick conversion. It is worth telling him about either
   way.
3. **Databento cbbo-1m** reaches 2013, but deep L1 history costs \$4,500/mo on an annual contract — not worth it at this
   stage.

Sources: [ORATS near-EOD](https://orats.com/near-eod-data) · [ORATS one-minute](https://orats.com/one-minute-data) ·
[Databento pricing](https://databento.com/pricing) · [Databento OPRA catalog](https://databento.com/catalog/opra/OPRA.PILLAR) ·
[Databento OPRA docs](https://databento.com/docs/datasets/opra-pillar) · [ThetaData pricing](https://www.thetadata.net/pricing) ·
[ThetaData flat files](https://docs.thetadata.us/Flat-Files/Getting-Started.html) ·
[Massive options quotes](https://massive.com/docs/rest/options/trades-quotes/quotes) ·
[historicaloptiondata.com](https://historicaloptiondata.com/historical-options-prices/) ·
[Cboe DataShop EOD summary](https://datashop.cboe.com/end-of-day-options-summary)
