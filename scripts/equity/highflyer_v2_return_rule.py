"""Run 36 — when has HighFlyerV2's edge RETURNED? (user 2026-11-05: "just wait until the edge for this system comes back ...
What do you think would be a good stretch of winning trades to conclude it has returned?")
The rule tested: the clipped PF over the LAST N trades of the book (in entry order) >= X. Its two error rates, by bootstrap
(i.i.d. resampling of N trades, 20,000 draws):
  power       a HEALTHY system passes: the book's trades 2009-07 .. 2025-12 (after the GFC, before 2026) as they are
  false pass  a BROKEN system passes: the same trades shifted down by a constant so the clipped PF is exactly 1.0 (no edge,
              same shape and spread)
plus the 2026 book's own rolling value, and consecutive-winner streaks for comparison. Books: the proxy book (dv60_med < $2M,
CS) and the full 10:00 book; production exit (k = 6) and the day-3 exit (k = 3).
    cd research && python3 scripts/equity/highflyer_v2_return_rule.py > data/equity/highflyer_repro/v2_return_rule.md
"""
import duckdb, numpy as np, pandas as pd
from scipy.optimize import brentq
rng = np.random.default_rng(20261105)
con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal_to1030.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date); T = T[T.open == 0].reset_index(drop=True); T["tid"] = T.index
con.register("t", T[["tid", "symbol", "entry_date", "raw_close_1000"]])
X = con.execute("""
  WITH s AS (SELECT ticker, date, open, close, volume, n, cum_div cd, row_number() OVER (PARTITION BY ticker ORDER BY date) i
             FROM db.daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.tid, ((y.open * y.n - t.raw_close_1000 * x.n) + (y.cd - x.cd)) / (t.raw_close_1000 * x.n) ret3,
         (SELECT median(p.close * p.volume) FROM s p WHERE p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1) dv60_med
  FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date JOIN s y ON y.ticker = t.symbol AND y.i = x.i + 3""").df()
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM db.ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(X, on="tid", how="left").merge(ty, on="symbol", how="left").sort_values(["entry_date", "symbol"])
T["proxy"] = (T.dv60_med < 2e6) & (T.typ == "CS")

def pf(r, axis=None):
    r = np.asarray(r, float)
    g = np.where(r > 0, np.minimum(r, .5), 0).sum(axis=axis); l = -np.where(r < 0, r, 0).sum(axis=axis)
    return np.where(l == 0, np.inf, g / np.where(l == 0, 1, l))
NS, XS = (30, 50, 75, 100), (1.2, 1.4, 1.6)
print("# Run 36 — a rule for 'the edge has returned' (scripts/equity/highflyer_v2_return_rule.py)\n")
print("The rule: clipped PF over the last N trades >= X. **Power** = the chance a HEALTHY system (the book's own 2009-07 .. 2025 "
      "trades) passes; **false pass** = the chance a BROKEN one (the same trades shifted to PF exactly 1.0) passes. Bootstrap, "
      "20,000 draws of N trades.\n")
for book, sel in (("proxy book (dv60_med < \\$2M, CS)", T.proxy), ("full 10:00 book", T.proxy == T.proxy)):
    B = T[sel]
    for col, lab in (("ret", "exit k = 6 (production)"), ("ret3", "exit k = 3")):
        H = B[(B.entry_date >= "2009-07-01") & (B.entry_date < "2026-01-01")][col].dropna().values
        sh = brentq(lambda c: pf(H - c) - 1.0, -0.5, 0.5); Z = H - sh
        per_yr = len(H) / 16.5
        now = B[B.entry_date >= "2026-01-01"][col].dropna().values
        print(f"## {book}, {lab}\n")
        print(f"Healthy 2009-07..2025: {len(H):,} trades ({per_yr:.0f} a year), PF {float(pf(H)):.2f}, win {100 * (H > 0).mean():.0f} %; "
              f"broken (shifted {100 * sh:+.2f} pp): win {100 * (Z > 0).mean():.0f} %. 2026: {len(now)} trades, PF {float(pf(now)):.2f}.\n")
        print("| last N trades (≈ months) | " + " | ".join(f"PF ≥ {x}: power / false pass" for x in XS) + " | 2026's last N |\n|---|" + "---|" * (len(XS) + 1))
        for n in NS:
            h = pf(rng.choice(H, (20000, n)), axis=1); z = pf(rng.choice(Z, (20000, n)), axis=1)
            last = f"{float(pf(now[-n:])):.2f}" if len(now) >= n else "—"
            print(f"| {n} (≈ {12 * n / per_yr:.0f}) | " + " | ".join(f"{100 * (h >= x).mean():.0f} % / {100 * (z >= x).mean():.0f} %" for x in XS) + f" | {last} |")
        # streaks: the chance of at least one run of s straight winners within N trades
        print("\nConsecutive winners for comparison — the chance of a run of s straight winners somewhere in 50 trades:\n")
        print("| s | healthy | broken |\n|---|---|---|")
        for s_ in (4, 5, 6, 8):
            def has(R):
                w = (R > 0).astype(int); run = np.zeros(len(R), int); best = np.zeros(len(R), int)
                for j in range(R.shape[1]):
                    run = np.where(w[:, j] == 1, run + 1, 0); best = np.maximum(best, run)
                return (best >= s_).mean()
            print(f"| {s_} | {100 * has(rng.choice(H, (5000, 50))):.0f} % | {100 * has(rng.choice(Z, (5000, 50))):.0f} % |")
        print()
