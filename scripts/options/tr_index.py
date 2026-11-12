"""Causal total-return index from daily_adjusted (docs/price_adjustment.md section 2), user 2026-11-12.

⚠ NOT `close * n + cum_div`: that fused level's day-over-day ratio divides by (P n + C) instead of P n, so every
return is shrunk by P n / (P n + C) - for long-history dividend payers C is most of the level (AGNC: realized vol
0.24x the truth, MPW 0.41x, RIG 0.37x, T 0.49x). The documented return of a position held t-1 -> t is
    r_t = [(P_t n_t - P_{t-1} n_{t-1}) + (C_t - C_{t-1})] / (P_{t-1} n_{t-1})
and the index is TR_t = prod(1 + r) (TR = 1 at the ticker's first row in the query).
"""
import numpy as np, pandas as pd


def add_tr(d, by='ticker'):
    """d: one row per (ticker, date), sorted, with close, n, cum_div (open optional). Adds r (close-to-close total
    return), V (the total-return index), gap (open vs the previous close, same share basis + the dividend paid at the
    open) when open exists, and vol_adj (volume / n, in original shares) when volume exists."""
    g = d.groupby(by, sort=False)
    pn = d.close * d.n; pn1 = pn.groupby(d[by]).shift(1); dC = d.cum_div - g.cum_div.shift(1)
    d['r'] = (pn - pn1 + dC) / pn1
    d['V'] = (1 + d.r.fillna(0)).groupby(d[by]).cumprod()
    if 'open' in d: d['gap'] = (d.open * d.n + dC - pn1) / pn1
    if 'volume' in d: d['vol_adj'] = d.volume / d.n
    return d
