"""GapFader: FREEZE GF-45's auction-price noise to a table keyed by (ticker, date) (user 2026-11-01).

GF-45 drew its rows' noise SEQUENTIALLY in the pool's row order (seed 20261020), so any change to the census pool shifted
every later draw: the 10-31 rebuild reshuffled ~9 % of the book (GF-56). This replays that draw ONCE on the census
production's coefficients were fitted on (data/equity/gapfader/pre_holder_20261031/census.parquet, the census of
10-21..10-30) and writes it out; gapfader_model_v2.py reads the table instead of drawing. Run once; the output is an input.

  cd research && python -u scripts/equity/gapfader_freeze_noise.py; echo exit $?
"""
import numpy as np, pandas as pd
D = "data/equity/gapfader/"
P = pd.read_parquet(D + "pre_holder_20261031/census.parquet")
P = P[(P.open >= 1) & ~P.early_close & P.close.notna() & (P.gap <= -0.035)].copy()
L = pd.read_parquet(D + "listing_2026-10-17.parquet")
P = P.merge(L, on="ticker", how="left"); P = P[P.primary_exchange != "XNYS"].copy()
V = pd.read_parquet(D + "sizing_tape_cut.parquet")
G = P[(P.dv20_prior >= 1e6) & (P.rvol_pm < 0.05)].merge(V, on=["ticker", "date"], how="inner")
G = G[G.x_36000.notna()]
rng = np.random.default_rng(20261020)
eG = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(G))) * rng.choice([-1, 1], len(G))
N = pd.DataFrame({"ticker": G.ticker.values, "date": G.date.values, "e": eG})
assert not N.duplicated(["ticker", "date"]).any()
# the check: the noise recovered from that census's book (gap_est = open·(1+e)/pc_total − 1) is the same draw
B = pd.read_parquet(D + "pre_holder_20261031/model_v2.parquet").merge(P[["ticker", "date", "pc_total"]], on=["ticker", "date"])
M = B.merge(N, on=["ticker", "date"], how="inner")
err = np.abs(M.e - ((M.gap_est + 1) * M.pc_total / M.open - 1)).max()
assert err < 1e-12 and B.in_gf45.sum() <= len(M), err
N.to_parquet(D + "auction_noise_gf45.parquet")
print(f"wrote {D}auction_noise_gf45.parquet: {len(N):,} rows; {len(M):,} of the book's {len(B):,} checked, max |Δe| {err:.1e}")
