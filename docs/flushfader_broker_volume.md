# Projected order flow — US equities intraday strategy

Prepared 2026-08-14. **$100,000 account. Commission assumed $0.0015/share.**
Figures are backtested over 2020-01 → 2026-07 (7 years) and expressed as monthly
averages.

## The strategy in one paragraph

US equities, **long only, intraday**. It buys a stock making a new short-term low and
sells it back out on a small bounce, typically within minutes. Both the entry and the
exit are **resting limit orders** — the strategy adds liquidity rather than taking it.
A small number of positions (well under 1%) do not reach their exit target during the
session and are closed market-on-open the next morning. No leverage is held overnight.

**One trade = 2 executions** (one to open, one to close), same share count each way.
Average order is roughly **3,300–4,900 shares / $11,000–14,500 notional**.

⭐ **The median entry price is about $4.** These are low-priced, liquid, actively
traded names, so per-share pricing is relatively expensive against notional: at
$0.0015/share a round trip costs **~0.074% of position value**. That makes the
routing and rebate treatment (below) materially important to us.

## The four segments

The strategy ranks its candidates on two independent checks: a **liquidity filter**
(is the stock trading continuously enough at the moment of entry) and a **setup
quality filter**. That gives four tiers, which are mutually exclusive — every
candidate trade lands in exactly one:

| tier | liquidity filter | quality filter |
|---|---|---|
| **A++** | pass | pass |
| **A+** | pass | fail |
| **B++** | fail | pass |
| **B+** | fail | fail |

A++ is what is traded today. **All four tiers are profitable, so the intention is to
trade all of them** — backtested results per tier, 2020-01 → 2026-07:

| tier | trades | profit factor | win rate | avg return/trade | worst trade | losing years |
|---|---:|---:|---:|---:|---:|---:|
| **A++** | 1,325 | 4.09 | 78.1% | +1.96% | −30.6% | 0 of 7 |
| **A+** | 717 | 2.10 | 72.8% | +1.03% | −27.7% | 1 of 7 |
| **B++** | 1,327 | 1.93 | 71.5% | +1.12% | −36.7% | 0 of 7 |
| **B+** | 2,636 | 1.80 | 69.5% | +0.83% | −41.1% | 0 of 7 |

Profit factor = gross profit / gross loss. Returns are per trade on position notional,
gross of commissions and fees. The lower tiers are less profitable per trade but far
more numerous, and none of them loses money — which is why the account is intended to
run the full set rather than A++ alone.

## Order flow by segment, each traded on its own

| segment | trades/month | avg shares/order | avg $/order | **shares/month** | **commission/year** |
|---|---:|---:|---:|---:|---:|
| A++ | 15.8 | 4,190 | $14,331 | 132,190 | $2,379 |
| A+ | 8.5 | 4,889 | $14,420 | 83,454 | $1,502 |
| B++ | 15.8 | 3,349 | $11,229 | 105,803 | $1,904 |
| B+ | 31.4 | 3,728 | $12,196 | 233,953 | $4,211 |

## If segments are combined

The strategy holds only one position per stock at a time, so combining segments is
**not** additive — a trade in one tier can displace a later one in another on the same
stock and day.

| book | trades/month | **shares/month** | **commission/year** |
|---|---:|---:|---:|
| A++ only (today) | 15.8 | 132,190 | $2,379 |
| A++ and A+ | 22.8 | 202,887 | $3,652 |
| A++ and B++ | 29.1 | 220,160 | $3,963 |
| **all four** | **62.5** | **484,843** | **$8,727** |

The intended direction is to widen toward the full set, which puts monthly share
volume near **half a million shares** on a $100k account.

## Points we would like priced

1. **ECN / routing economics.** Both legs rest passively, so the strategy should
   predominantly be **adding liquidity**. We would like to understand the rebate
   pass-through and how add vs remove is billed, since at $0.0015/share that leg could
   plausibly be larger than the commission itself.
2. **Platform fee waiver.** We understand the monthly platform fee is waived somewhere
   in the 200,000–300,000 shares/month range depending on platform. The combined book
   clears that; A++ alone does not.
3. **Per-execution or per-ticket minimums**, if any, given an average order of roughly
   3,300–4,900 shares.
4. **API access.** We route programmatically and would want FIX.

## Notes on the figures

- Backtested, not live. Fills are modelled at the next second-bar VWAP. Passive orders
  that would not have filled simply would not trade, so these counts are more likely to
  overstate than understate activity.
- Activity is uneven year to year — the strategy trades when its setup appears. The
  quietest year ran at roughly a quarter of the busiest year's rate.
- Long only. No borrow required. A short strategy is in research but is not included
  in any figure here.
- SEC fees, TAF and clearing are not modelled.
