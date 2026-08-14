# FlushFader — projected order flow and commissions

Prepared 2026-08-14. Commission rate **$0.0015/share** (Cobra promo rate).
Source: `scripts/equity/flushfader_broker_volume.py` over the `v45_nextopen` trip set,
2020-01 → 2026-07 (7 calendar years), 28,614 candidate trips at a $1+ entry price.

## What the system is

US equities, **intraday long mean-reversion**. Buys a stock printing a new 20-minute
low, exits on a ~5-minute high reversion. Median hold is minutes, not hours; anything
unresolved at the close is held overnight and exited market-on-open the next session.

Orders rest **passively** — entries and exits are both limit fills, not marketable.
That matters for the fee side (see caveats).

**One trade = 2 executions** (entry + exit), same share count each way.

## Position sizing

Volatility-scaled fractional sizing:

    size_fraction = 1.0% x tier_multiplier x sqrt(99 / volat_20m_bp)
    tier multipliers: A 2.44 / B 1.80 / C 1.14 / D 1.00

Peak simultaneous exposure on the traded book is **1.23x equity** — within intraday
day-trading margin, no overnight leverage.

## ⭐ The traded book

`gap_60 < 4` universe with the 8-signal roster. **1,325 trades over 7 years ≈ 15.8
trades/month.**

| account equity | shares/month | notional/month | commission/year |
|---|---:|---:|---:|
| $100,000 | 13,219 | $0.05M | **$238** |
| $250,000 | 33,047 | $0.11M | **$595** |
| $500,000 | 66,095 | $0.23M | **$1,190** |

### Per year, at each equity level

| year | trades | shares @$100k | comm @$100k | shares @$250k | comm @$250k | shares @$500k | comm @$500k |
|---|---:|---:|---:|---:|---:|---:|---:|
| 2020 | 215 | 186,490 | $280 | 466,226 | $699 | 932,451 | $1,399 |
| 2021 | 264 | 183,440 | $275 | 458,601 | $688 | 917,202 | $1,376 |
| 2022 | 76 | 64,881 | $97 | 162,202 | $243 | 324,405 | $487 |
| 2023 | 110 | 94,310 | $141 | 235,774 | $354 | 471,549 | $707 |
| 2024 | 219 | 173,951 | $261 | 434,879 | $652 | 869,757 | $1,305 |
| 2025 | 299 | 267,010 | $401 | 667,526 | $1,001 | 1,335,051 | $2,003 |
| 2026 | 142 | 140,310 | $210 | 350,776 | $526 | 701,551 | $1,052 |

Activity is uneven year to year — 2022 ran at roughly a quarter of 2025's rate. The
system trades when its setup appears and sits out when it does not.

## Wider universes — upper bounds on flow, NOT tradeable size

The rows below relax the production filters. They describe **how much of the market
the system monitors**, not size the account could actually put on.

⚠ `max gross` is peak simultaneous exposure as a multiple of equity. Anything much
above 1.0 could not be traded as sized — those rows are order-flow ceilings only.

| cell | trades | trades/mo | max gross | shares/mo @$500k | comm/yr @$500k |
|---|---:|---:|---:|---:|---:|
| ⭐ `g60` × roster ON — **the book** | 1,325 | 15.8 | 1.23 | 66,095 | $1,190 |
| `g60` × roster minus deep-flush | 1,290 | 15.4 | 1.20 | 64,071 | $1,153 |
| `g60` × deep-flush alone | 267 | 3.2 | 0.44 | 13,752 | $248 |
| `g60` × roster OFF | 1,917 | 22.8 | 2.33 | 101,443 | $1,826 |
| complement × roster ON | 1,327 | 15.8 | 1.06 | 52,901 | $952 |
| complement × roster minus deep-flush | 1,256 | 15.0 | 0.96 | 49,127 | $884 |
| complement × deep-flush alone | 266 | 3.2 | 0.29 | 10,773 | $194 |
| complement × roster OFF | 3,703 | 44.1 | 4.16 | 158,665 | $2,856 |
| all × roster ON | 2,446 | 29.1 | 1.88 | 110,080 | $1,981 |
| all × roster OFF — every candidate trip | 5,246 | 62.5 | 4.96 | 242,421 | $4,364 |

Two things worth reading off this table:

- **The deep-flush signal is ~20% of the book's trades but only ~3% of its
  incremental flow** (1,325 → 1,290 without it). It is a quality filter, not a volume
  driver.
- **The `gap_60` door roughly halves the universe.** Its complement is a similar
  number of roster-qualified trades, so the monitored universe is about twice the
  traded one.

## Platform-fee thresholds

Cobra waives the monthly platform fee at **200,000–300,000 shares/month** depending
on platform. On the traded book that requires:

| target | equity needed | commission/yr at that equity |
|---|---:|---:|
| 200k shares/mo | **~$1.51M** | ~$3,600 |
| 300k shares/mo | **~$2.27M** | ~$5,400 |

Trading the full candidate set (all universes, no roster) reaches the same thresholds
at **~$825k** and **~$1.24M** — but at 4.96x peak gross exposure, which is not a real
configuration.

**Honest summary: at realistic starting size this is a low-commission account.** At
$250k it generates roughly **$600/year** in commission on ~16 trades/month. It becomes
platform-fee-relevant somewhere north of $1.5M in equity.

## What is NOT in these numbers

- **ECN / routing rebates and fees, SEC fees, TAF, clearing** — none are modelled.
  ⭐ FlushFader fills **passively on both legs**, so a live book would plausibly
  **earn add rebates** rather than pay take fees. That cuts the opposite way from
  commission and could be material relative to a $0.0015/share rate. This is the leg
  we would want priced.
- **Borrow** — not applicable; this system is long-only. A short system is planned
  but is not what these figures describe.
- **Slippage and partial fills.** Fills here are modelled at the next bar's VWAP.
  Passive orders that do not fill simply do not trade, which would reduce these
  counts rather than increase them.
- **Overnight positions.** A small fraction of trades (~0.5% of candidates) fail to
  reach their target intraday and are carried to the next open. No overnight margin
  is used.
