"""A 5-DAY ATR % filter on every production system (user 2026-10-18, after WFF 10-14: a FlushFader long carried
overnight from 2.05 into a 0.15 open, -92.7 %). Is the prior week's range a usable gate?

ATR5% of ticker-day D, causal (only the 5 sessions BEFORE D; knowable before the open), in the causal scale P·n
(docs/price_adjustment.md: P(t)·n(t)/n(D) puts any day in D's scale, so ratios of P·n are split-proof):
    TR_t   = max(H_t, C_{t-1}) - min(L_t, C_{t-1})          (P·n units)
    ATR5%  = mean(TR_t, t = D-5 .. D-1) / C_{D-1}             (needs 6 prior rows; NaN otherwise)
    ADR5%  = mean(H_t / L_t - 1, t = D-5 .. D-1)             (the user, 2026-10-18: the day's OWN range, no gaps —
                                                              WFF's ranges were enormous, unlike a catalyst breakout)
Joined to each system's reference book; bands = the book's own deciles + the top 5 / 2 / 1 % + fixed tails.
Run from research/:  python -u scripts/equity/atr5_filter.py > data/equity/atr5/atr5.md 2> data/equity/atr5/atr5.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, file=sys.stderr, flush=True)
os.makedirs("data/equity/atr5", exist_ok=True)
BOOKS = {
    "FlushFader (long)": ("data/flushfaderv2/flushfaderv2_reference_book.parquet", "ret_exit", "multiplier"),
    "LowFader (long)": ("data/flushfader_gate_review/lowfader_reference_book.parquet", "ret_exit", "weight"),
    "SpikeFader (short)": ("data/flushfader_gate_review/spikefader_reference_book.parquet", "ret_exit", "weight"),
    "Snoozer long": ("data/flushfader_gate_review/snoozer_reference_book.parquet", "ret", "weight"),
    "Snoozer short": ("data/flushfader_gate_review/snoozer_reference_book.parquet", "ret", "weight"),
}
con = duckdb.connect(); con.execute("SET memory_limit='12GB'"); con.execute("SET threads=8")
keys = pd.concat([con.execute(f"SELECT DISTINCT symbol, trade_date::DATE AS d FROM '{p}'").df() for p, _, _ in BOOKS.values()]
                 + [pd.DataFrame({"symbol": ["WFF", "AEHL"], "d": pd.to_datetime(["2026-10-14", "2026-10-14"])})]).drop_duplicates()
con.register("keys", keys)
A = con.execute("""
    WITH a AS (SELECT ticker, date, high * n AS h, low * n AS l, close * n AS c, CASE WHEN low > 0 THEN high / low - 1 END AS dr
               FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT symbol FROM keys)),
         t AS (SELECT ticker, date, c, dr, greatest(h, lag(c) OVER w) - least(l, lag(c) OVER w) AS tr FROM a
               WINDOW w AS (PARTITION BY ticker ORDER BY date)),
         -- for day D: the 5 TRs and the close of the rows strictly BEFORE D
         p AS (SELECT ticker, date, avg(tr) OVER w5 AS atr5_prior, count(tr) OVER w5 AS n_tr, lag(c) OVER w1 AS c_prior,
                      avg(dr) OVER w5 AS adr5_prior, count(dr) OVER w5 AS n_dr
               FROM t WINDOW w5 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING),
                             w1 AS (PARTITION BY ticker ORDER BY date))
    SELECT k.symbol, k.d, CASE WHEN p.n_tr = 5 THEN p.atr5_prior / p.c_prior END AS atr5,
           CASE WHEN p.n_dr = 5 THEN p.adr5_prior END AS adr5
    FROM keys k LEFT JOIN p ON p.ticker = k.symbol AND p.date = k.d""").df()
log(f"ATR5 for {len(A):,} ticker-days ({A.atr5.isna().sum():,} without 5 prior sessions)")
A["d"] = pd.to_datetime(A.d).dt.strftime("%Y-%m-%d")
MEAS = {"ATR5": dict(zip(zip(A.symbol, A.d), A.atr5)), "ADR5": dict(zip(zip(A.symbol, A.d), A.adr5))}
print("ρ(ATR5, ADR5) over the ticker-days (Spearman): %.3f" % A[["atr5", "adr5"]].rank().corr().iloc[0, 1])

def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l
def line(nm, r, w):
    if len(r) == 0: return f"| {nm} | 0 | | | | | | |"
    return (f"| {nm} | {len(r):,} | {pf(r) - 1:.2f} | {pf(np.clip(r, -.5, .5)) - 1:.2f} | {pf(r * w) - 1:.2f} | "
            f"{100 * r.mean():+.2f} | {100 * np.median(r):+.2f} | {100 * r.min():.1f} |")
HDR = "| band | trades | PF−1 | PF−1 clip ±50 % | sized PF−1 | avg % | median % | worst % |\n|---|---|---|---|---|---|---|---|"

for name, (path, rcol, wcol) in BOOKS.items():
    B = con.execute(f"SELECT *, trade_date::DATE::VARCHAR AS d FROM '{path}'").df()
    if name.startswith("Snoozer"): B = B[B.side == ("long" if "long" in name else "short")]
    B["r"] = B[rcol].astype(float); B["w"] = B[wcol].astype(float); B["yr"] = B.d.str[:4]
    print(f"\n## {name} — {len(B):,} trades, {B.d.min()} … {B.d.max()}\n\n{HDR}")
    print(line("all", B.r.values, B.w.values))
    for meas, look in MEAS.items():
        B["x"] = [look.get((s_, d_), np.nan) for s_, d_ in zip(B.symbol, B.d)]
        X = B[B.x.notna()]
        q = np.quantile(X.x, np.linspace(0, 1, 11)); top = np.quantile(X.x, [0.95, 0.98, 0.99])
        print(f"| **{meas}** ({B.x.isna().sum()} without one) | | | | | | | |")
        for i in range(10):
            m = (X.x >= q[i]) & ((X.x < q[i + 1]) if i < 9 else True)
            print(line(f"{meas} D{i + 1}: {100 * q[i]:.1f}–{100 * q[i + 1]:.1f}", X.r[m].values, X.w[m].values))
        for p_, t in zip([5, 2, 1], top):
            m = X.x >= t; print(line(f"{meas} top {p_} % (≥ {100 * t:.1f})", X.r[m].values, X.w[m].values))
        for lo, hi in [(0.5, 1), (1, 2), (2, 4), (4, np.inf)]:
            m = (X.x >= lo) & (X.x < hi); print(line(f"{meas} {100 * lo:.0f}–{'' if hi == np.inf else f'{100 * hi:.0f}'} %", X.r[m].values, X.w[m].values))
        m = X.x >= 1.0
        print(f"| {meas} ≥ 100 % by year: " + " · ".join(f"{y} {(X.yr[m] == y).sum()} / {pf(X.r[m & (X.yr == y)].values) - 1:.2f}" for y in sorted(B.yr.unique())) + " | | | | | | | |")
        B[meas] = B.x
    worst = B.nsmallest(5, "r")
    print("\nworst five: " + "; ".join(f"{r.symbol} {r.d} {100 * r.r:+.1f} % (ATR5 {100 * r.ATR5:.0f} %, ADR5 {100 * r.ADR5:.0f} %)" for r in worst.itertuples()))
    if name.startswith("FlushFader"):
        for s_ in ["WFF", "AEHL"]:
            a_, d_ = MEAS["ATR5"][(s_, "2026-10-14")], MEAS["ADR5"][(s_, "2026-10-14")]
            print(f"\n**{s_} 2026-10-14: ATR5 {100 * a_:.1f} % (above {100 * (B.ATR5 < a_).mean():.2f} % of the book), "
                  f"ADR5 {100 * d_:.1f} % (above {100 * (B.ADR5 < d_).mean():.2f} %)**")
log("done")
