"""GapFader — the LONG weak-premarket fade by PRICE × SPY's gap (user 2026-10-16: "the number of trades in various price
and market gap buckets"; the focus moves to long systems). Gap-DOWNs ≥ 4 % (the fade = buy the open), rvol_pm < 0.02,
headline universe (open ≥ $1, dv20_prior ≥ $1M, no early close). SPY's gap in the stock's direction as GF-3 (< 0 =
against the stock: SPY gapped UP). Open → official close, equal weight, no costs.
Run from research/:  python -u scripts/equity/gapfader_buckets.py > data/equity/gapfader/buckets.md; echo exit $?
"""
import numpy as np, pandas as pd, duckdb
S = duckdb.sql("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker='SPY' WINDOW w AS (ORDER BY date))
   SELECT date::VARCHAR date, open/(pcr*np_/n-(cum_div-cdp)/n)-1 spy_gap FROM d""").df()
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02)
      & (C.gap <= -0.04)].merge(S, on="date")
C["rel"] = C.spy_gap * np.sign(C.gap)
years = (pd.to_datetime(C.date).max() - pd.to_datetime(C.date).min()).days / 365.25
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
PRICE = [(1, 2, "$1–2"), (2, 3, "$2–3"), (3, 5, "$3–5"), (5, 10, "$5–10"), (10, 20, "$10–20"), (20, 50, "$20–50"), (50, 1e9, "≥ $50")]
SPY = [(-1, -0.01, "SPY up ≥ 1 % (against)"), (-0.01, -0.005, "SPY up 0.5–1 %"), (-0.005, 0.005, "quiet < 0.5 %"),
       (0.005, 0.01, "SPY down 0.5–1 %"), (0.01, 1, "SPY down ≥ 1 % (with)")]
GAPS = [(0.04, 0.06, "4–6 %"), (0.06, 0.10, "6–10 %"), (0.10, 0.15, "10–15 %"), (0.15, 9, "≥ 15 %")]
def cell(T, what):
    if len(T) == 0: return "–"
    if what == "n": return f"{len(T):,}"
    if what == "pf": return f"{pf(T.fade_oc.values):.2f}"
    return f"{T.fade_oc.mean()*100:+.2f}"
def table(title, rows, rowf, cols, colf, what):
    print(f"### {title}\n")
    print("| | " + " | ".join(c[2] for c in cols) + " | all |"); print("|---" * (len(cols) + 2) + "|")
    for r in rows:
        R = C[rowf(r)]
        print(f"| {r[2]} | " + " | ".join(cell(R[colf(c, R)], what) for c in cols) + f" | {cell(R, what)} |")
    print(f"| all | " + " | ".join(cell(C[colf(c, C)], what) for c in cols) + f" | {cell(C, what)} |\n")
pr = lambda r: (C.open >= r[0]) & (C.open < r[1])
sp = lambda c, D: (D.rel >= c[0]) & (D.rel < c[1])
gp = lambda c, D: (D.gap.abs() >= c[0]) & (D.gap.abs() < c[1])
print(f"# GapFader LONG — weak-premarket gap-downs ≥ 4 % by price × SPY's gap ({len(C):,} trades, {C.date.min()} … {C.date.max()}, {years:.1f} yr)\n")
for what, lab in (("n", "trades"), ("pf", "PF (open → close)"), ("mean", "mean % (open → close)")):
    table(f"price × SPY's gap — {lab}", PRICE, pr, SPY, sp, what)
for what, lab in (("n", "trades"), ("pf", "PF")):
    table(f"price × gap size — {lab}", PRICE, pr, GAPS, gp, what)
