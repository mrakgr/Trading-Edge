# Overnight and next-day returns by gap direction × CIR bucket

> Extracted from [`orb_session_findings.md`](orb_session_findings.md) (section 18). Same analysis, broken out as a standalone reference.

With `next_open_vs_close_pct` and `next_close_vs_close_pct` in the augmented dataset, we can directly measure post-close behavior. All tables below are on day-0 RVOL≥3 entries. CIR bins are disjoint 20%-wide buckets. Returns are reported as-is (positive = stock went up), so a long holder wants positive and a short holder wants negative.

Three return windows:
- **Overnight**: close → next day's open (what you capture if you hold overnight and sell at next-day open).
- **Next-day intraday**: next day's open → next day's close (if you bought at next-day open and held through next-day close).
- **Full next day**: close → next day's close (overnight + next-day intraday combined; what you capture if you hold through the entire next trading day).

## Gap-up breakouts (gap_pct > 0)

| CIR bin | n | Overnight | Next-day intraday | Full next day |
|---|---|---|---|---|
| [0.00, 0.20) | 418 | +0.58%  (58% hit) | -0.62%  (45% hit) | -0.17%  (47% hit) |
| [0.20, 0.40) | 376 | +0.46%  (54% hit) | +0.45%  (49% hit) | +0.98%  (48% hit) |
| [0.40, 0.60) | 387 | +0.32%  (55% hit) | -1.37%  (43% hit) | -1.17%  (43% hit) |
| [0.60, 0.80) | 435 | **+1.55%**  (49% hit) | -1.03%  (48% hit) | +0.51%  (45% hit) |
| [0.80, 1.00) | 574 | +0.34%  (50% hit) | -0.66%  (46% hit) | -0.35%  (46% hit) |

## Gap-down breakouts (gap_pct < 0)

| CIR bin | n | Overnight | Next-day intraday | Full next day |
|---|---|---|---|---|
| [0.00, 0.20) | 354 | **+0.91%**  (**67% hit**) | -0.68%  (46% hit) | +0.13%  (54% hit) |
| [0.20, 0.40) | 266 | +0.04%  (59% hit) | -0.15%  (49% hit) | -0.11%  (50% hit) |
| [0.40, 0.60) | 225 | +0.04%  (51% hit) | -0.34%  (50% hit) | -0.33%  (50% hit) |
| [0.60, 0.80) | 222 | +0.18%  (62% hit) | -0.65%  (43% hit) | -0.48%  (45% hit) |
| [0.80, 1.00) | 247 | -0.24%  (46% hit) | -0.46%  (45% hit) | -0.69%  (42% hit) |

## What this tells us

**Long-side overnight hold** (buy at day-0 close, sell at next-day open):

- **Gap-up + CIR 60-80%: +1.55% mean** is the single best cell in the matrix. Strong-ish closes that haven't hit the "everyone sees the perfect chart" zone.
- **Gap-down + CIR 0-20%: +0.91% mean, 67% hit rate** — the highest hit rate in any cell. These are stocks that gapped down *and* closed weak; bottom-fishers pile in overnight.
- **Gap-down + CIR 60-80%: +0.18% mean but 62% hit rate** — a high-frequency, low-magnitude bounce play. The stock rejected the gap-down to close strong, and that rejection continues overnight more often than not.
- **Gap-up + CIR 80-100%** (the "perfect continuation chart"): only +0.34%, 50% hit. Profit-taking on the strongest closers keeps the overnight gain muted.
- **Gap-down + CIR 80-100%** (the "gap-down that fully recovered"): **-0.24% overnight, 46% hit — losing**. Stocks that reversed a gap-down all the way up to close at the high give back overnight.

**Short-side overnight hold**:

- Every cell in both tables has **positive** overnight mean (except gap-down CIR 80-100% at -0.24%), meaning every cell is a *loss* for a short holder. Short overnight is not tradeable on this universe.

**Next-day intraday** (buy at next-day open, sell at next-day close):

- Negative in 9 of 10 cells. The only positive is gap-up CIR 20-40% (+0.45%, 49% hit), which is thin.
- The negativity is strongest on the "extended" cells (gap-up + strong close, gap-down + strong reversal).

**Full next day** (close-to-close):

- Only two cells are clearly positive: gap-up + CIR 20-40% (+0.98%) and gap-up + CIR 60-80% (+0.51%).
- Gap-up + CIR 40-60% is the worst close-to-close cell (-1.17%) — a "middling close after a gap-up" gets faded hard.
- Gap-down + CIR 0-20% is roughly flat close-to-close (+0.13%) despite the strong overnight (+0.91%) because the next-day intraday fades (-0.68%). The bounce is real but short-lived.

## Tradeable overnight setups

All assume exiting at next-day open.

1. **Gap-up, CIR 60-80%**: +1.55% mean, 49% hit. Highest expected return.
2. **Gap-down, CIR 0-20%**: +0.91% mean, 67% hit. Highest hit rate. Oversold-bounce play.
3. **Gap-down, CIR 60-80%** (optional): +0.18% mean, 62% hit. Small expected return but frequent; better as a size-up signal than a standalone trade.

## Cells to avoid

- **Gap-up, CIR 80-100%**: +0.34%, 50% hit. Looks obvious but isn't — profit-taking kills it.
- **Gap-down, CIR 80-100%**: -0.24%, 46% hit. "Full rejection" closes *lose* overnight.
- Any short overnight, any bucket.
- Any full-next-day hold (intraday grind is consistently negative).
