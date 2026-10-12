"""GapFader — GF-2: the MARKET adjustment (user 2026-10-12). Two views of the GF-1 census:

  1. idiosyncratic gap   gap_idio = (1 + gap) / (1 + spy_gap) − 1      the stock's own gap, net of the market's
     market-adjusted fade fade_adj = −sign(gap) × ((close/open − 1) − (spy_close/spy_open − 1))
     (the raw fade — what an unhedged trade earns — is shown beside it)
  2. the control        the same tables on QUIET-market days only (|spy_gap| < 0.5 %)

SPY's gap uses the same causal prev-close as the census (daily_adjusted: n, cum_div). Universe as GF-1's headline:
raw open ≥ $1, dv20_prior ≥ $1M, no early closes. Equal weight, NO costs.

Run from research/:
    python -u scripts/equity/gapfader_market.py > data/equity/gapfader/market.md 2>&1; echo exit $?
"""
import argparse
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--census", default="data/equity/gapfader/census.parquet")
ap.add_argument("--min-open", type=float, default=1.0)
ap.add_argument("--min-dv20", type=float, default=1e6)
ap.add_argument("--quiet", type=float, default=0.005)
args = ap.parse_args()

S = duckdb.sql("""
    WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
               FROM 'data/v2/daily_adjusted.parquet' WHERE ticker = 'SPY' WINDOW w AS (ORDER BY date))
    SELECT date::VARCHAR AS date, open / (pcr * np_ / n - (cum_div - cdp) / n) - 1 AS spy_gap, close / open - 1 AS spy_oc FROM d""").df()
C = pd.read_parquet(args.census)
C = C[(C.open >= args.min_open) & (C.dv20_prior >= args.min_dv20) & ~C.early_close & C.close.notna()].merge(S, on="date", how="left")
assert C.spy_gap.notna().all()
side = np.sign(C.gap)
C["gap_idio"] = (1 + C.gap) / (1 + C.spy_gap) - 1
C["fade_adj"] = -side * ((C.close / C.open - 1.0) - C.spy_oc)
C["yr"] = C.date.str[:4]
C["up"] = C.gap > 0
C["ag"] = C.gap.abs(); C["agi"] = C.gap_idio.abs()
C["weak"] = C.rvol_pm.fillna(0) < 0.02
C["quiet"] = C.spy_gap.abs() < args.quiet
GAP = [0.02, 0.04, 0.06, 0.10, 0.15, 0.20, 0.30, 0.50, 99]
GL = ["2–4 %", "4–6 %", "6–10 %", "10–15 %", "15–20 %", "20–30 %", "30–50 %", "≥ 50 %"]

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def f(x, d=2): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.{d}f}")

print(f"# GF-2 — the market adjustment ({len(C):,} gaps; quiet = |SPY gap| < {args.quiet:.1%})\n")
# ---- 0. how much of the census is the market
spy_dir = np.sign(C.spy_gap) == np.sign(C.gap)
print("## 0. The market's share: gaps by SPY's own gap on the day\n")
print("| SPY gap | days | gaps | gaps per day | same direction as SPY |\n|---|---|---|---|---|")
sd = S.set_index("date").spy_gap
for lo, hi, lab in ((0, 0.005, "< 0.5 %"), (0.005, 0.01, "0.5–1 %"), (0.01, 0.02, "1–2 %"), (0.02, 1, "≥ 2 %")):
    m = (C.spy_gap.abs() >= lo) & (C.spy_gap.abs() < hi)
    days = C.date[m].nunique()
    print(f"| {lab} | {days:,} | {m.sum():,} | {m.sum()/max(days,1):,.0f} | {spy_dir[m].mean()*100:.0f} % |")

# ---- 1. by the RAW gap vs the IDIOSYNCRATIC gap, all days vs quiet days
for up, name in ((True, "gap UP (fade = short)"), (False, "gap DOWN (fade = long)")):
    D = C[C.up == up] if True else None
    print(f"\n## {name}\n")
    print("| gap | all days: n / PF raw / PF adj | idiosyncratic gap: n / PF raw / PF adj | QUIET days: n / PF raw / win % | quiet ∧ weak pm (< 0.02): n / PF raw |")
    print("|---|---|---|---|---|")
    # bucket the idiosyncratic gap on its own sign (a stock can gap up with the market down: its idio gap is larger)
    Di = C[np.sign(C.gap_idio) == (1 if up else -1)]
    for i, g in enumerate(GL):
        lo, hi = GAP[i], GAP[i + 1]
        a = D[(D.ag >= lo) & (D.ag < hi)]
        b = Di[(Di.agi >= lo) & (Di.agi < hi)]
        q = a[a.quiet]; qw = q[q.weak]
        print(f"| {g} | {len(a):,} / {f(pf(a.fade_oc.values))} / {f(pf(a.fade_adj.values))} | {len(b):,} / {f(pf(b.fade_oc.values))} / {f(pf(b.fade_adj.values))} | "
              f"{len(q):,} / {f(pf(q.fade_oc.values))} / {(q.fade_oc > 0).mean()*100:.0f} | {len(qw):,} / {f(pf(qw.fade_oc.values))} |")
    # ---- 2. the year tables of the candidate cells, quiet days and all days
    yrs = sorted(C.yr.unique())
    print(f"\n### {name} — by year: fade PF raw (n)\n")
    print("| cell | days | " + " | ".join(yrs) + " | 2016–20 | 2021–26 |\n|---|---|" + "---|" * (len(yrs) + 2))
    cells = [("gap ≥ 15 %", D.ag >= 0.15), ("gap ≥ 15 %, weak pm", (D.ag >= 0.15) & D.weak), ("gap 6–15 %, weak pm", (D.ag >= 0.06) & (D.ag < 0.15) & D.weak),
             ("gap 6–15 %, pm ≥ 0.02", (D.ag >= 0.06) & (D.ag < 0.15) & ~D.weak)]
    for lab, m in cells:
        for dl, dm in (("all", np.ones(len(D), bool)), ("quiet", D.quiet.values)):
            T = D[m.values & dm]
            early = T[T.yr <= "2020"]; late = T[T.yr >= "2021"]
            print(f"| {lab} | {dl} | " + " | ".join(f"{f(pf(T[T.yr == y].fade_oc.values))} ({(T.yr == y).sum()})" for y in yrs)
                  + f" | {f(pf(early.fade_oc.values))} | {f(pf(late.fade_oc.values))} |")
