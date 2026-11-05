"""GF-66 — GapFader on CROWDED gap-down days: should the gap floor deepen to −10 % when many names gap down? (user 2026-11-05,
a sell-off morning: "I bet today we'll have a lot of stocks gapping down over 4%. We'll have to look into whether tightening
up the requirement so that we're buying stocks down 10% on days like this would be better.")
THE BOOK: production's (GF-54, data/equity/gapfader/model_v2.parquet): gap and gap_est <= −4 %, rvol60 < 0.02, dv60_med >=
$500k, PF−1 > 0; r1 = the trade's return in % (the 50 %-gap-back target before 10:00, else the 10:00 vwap; net of 0.4c).
Sized = Σ units × r1. Knowable at the open, both day measures:
  crowd     the number of the book's names that day (the LOOs the Oms would place)
  spy_gap   SPY's open vs the previous close (daily_adjusted: the holder's return to the open)
    cd research && python3 scripts/equity/gapfader_crowded_days.py > data/equity/gapfader/crowded_days.md
"""
import duckdb, numpy as np, pandas as pd
B = pd.read_parquet("data/equity/gapfader/model_v2.parquet")
B = B[(B.rvol60 < 0.02) & (B.dv60_med >= 5e5) & (B.gap <= -0.04) & (B.gap_est <= -0.04) & (B.pf1_new > 0)].copy()
B["crowd"] = B.groupby("date").ticker.transform("size")
con = duckdb.connect(); con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
S = con.execute("""SELECT strftime(date, '%Y-%m-%d') date, ((open * n - lag(close * n) OVER w) + (cum_div - lag(cum_div) OVER w)) / lag(close * n) OVER w spy_gap
                   FROM db.daily_adjusted WHERE ticker = 'SPY' WINDOW w AS (ORDER BY date)""").df()
B = B.merge(S, on="date", how="left"); B["yr"] = B.date.str[:4].astype(int)
def pf(x):
    x = np.asarray(x); g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
def cell(X):
    if len(X) < 20: return f"— ({len(X)})"
    return f"{f2(pf(X.r1))} · {X.r1.mean():+.2f} % · {len(X):,}"
DEPTH = (("−4..−6 %", -0.06, -0.04), ("−6..−10 %", -0.10, -0.06), ("≤ −10 %", -9, -0.10))
print("# GF-66 — GapFader on crowded gap-down days (scripts/equity/gapfader_crowded_days.py)\n")
print(f"Production book: {len(B):,} trades on {B.date.nunique():,} days, {B.date.min()} .. {B.date.max()}. Cells: PF · mean r1 · trades.\n")
for name, col, bands in (
        ("the day's crowd (names in the book that day)", "crowd", ((1, 3), (4, 9), (10, 19), (20, 39), (40, 9999))),
        ("SPY's opening gap", "spy_gap", ((-9, -0.01), (-0.01, -0.003), (-0.003, 0.003), (0.003, 9)))):
    print(f"## By {name} × the stock's gap depth\n")
    print("| day | days | all depths | " + " | ".join(l for l, _, _ in DEPTH) + " | **≤ −10 % only, sized Σ** vs all sized Σ |\n|---|---|---|" + "---|" * len(DEPTH) + "---|")
    for lo, hi in bands:
        X = B[(B[col] >= lo) & (B[col] <= hi)] if col == "crowd" else B[(B[col] > lo) & (B[col] <= hi)]
        lab = (f"{lo}–{hi}" if hi < 9999 else f"{lo}+") if col == "crowd" else f"{100 * lo:+.1f} .. {100 * hi:+.1f} %".replace("-900.0", "−∞").replace("+900.0", "+∞")
        deep = X[X.gap <= -0.10]
        print(f"| {lab} | {X.date.nunique():,} | {cell(X)} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) +
              f" | {(deep.units_new * deep.r1).sum():,.0f} vs {(X.units_new * X.r1).sum():,.0f} |")
    print()
print("## The crowded days (20+ names) by era\n")
print("| era | all depths | " + " | ".join(l for l, _, _ in DEPTH) + " |\n|---|---|" + "---|" * len(DEPTH))
for a, b in ((2016, 2019), (2020, 2022), (2023, 2026)):
    X = B[(B.crowd >= 20) & B.yr.between(a, b)]
    print(f"| {a}–{b} | {cell(X)} | " + " | ".join(cell(X[(X.gap > lo) & (X.gap <= hi)]) for _, lo, hi in DEPTH) + " |")
print("\n## The worst crowded days (20+ names): the day's PF and what ≤ −10 % alone would have made\n")
D = B[B.crowd >= 20].groupby("date").apply(lambda g: pd.Series({"n": len(g), "spy_gap": g.spy_gap.iloc[0], "sized": (g.units_new * g.r1).sum(),
        "mean": g.r1.mean(), "n_deep": int((g.gap <= -0.10).sum()), "sized_deep": (g.units_new * g.r1)[g.gap <= -0.10].sum()}), include_groups=False)
print("| date | names | SPY gap | mean r1 | sized Σ | ≤ −10 %: names · sized Σ |\n|---|---|---|---|---|---|")
for d, r in D.sort_values("sized").head(12).iterrows():
    print(f"| {d} | {int(r.n)} | {100 * r.spy_gap:+.2f} % | {r['mean']:+.2f} % | {r.sized:,.1f} | {int(r.n_deep)} · {r.sized_deep:,.1f} |")
print(f"\n20+-name days: {len(D)}; their sized Σ {D.sized.sum():,.0f} (≤ −10 % only: {D.sized_deep.sum():,.0f}); "
      f"{100 * (D.sized < 0).mean():.0f} % of them lose.")
print("\n## Is depth better ONLY on market gap-down days? (user: \"Didn't you find out that deeper gaps don't really improve the PF\")\n")
print("| SPY's opening gap | era / sample | days | " + " | ".join(l for l, _, _ in DEPTH) + " |\n|---|---|---|" + "---|" * len(DEPTH))
def rowd(lab, era, X):
    return f"| {lab} | {era} | {X.date.nunique():,} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) + " |"
print(rowd("all days", "2016–26", B))
for lab, lo, hi in (("≤ −1 %", -9, -0.01), ("−1 .. −0.3 %", -0.01, -0.003), ("> −0.3 %", -0.003, 9)):
    X = B[(B.spy_gap > lo) & (B.spy_gap <= hi)]
    print(rowd(lab, "2016–26", X))
    if lo == -9:
        print(rowd(lab, "without 2020", X[X.yr != 2020]))
        print(rowd(lab, "without 2020-03", X[~X.date.str.startswith("2020-03")]))
        for a, b in ((2016, 2019), (2020, 2022), (2023, 2026)): print(rowd(lab, f"{a}–{b}", X[X.yr.between(a, b)]))
        # day-clustered: each day weighs once — the share of days where the deep names beat the shallow ones
        g = X.groupby("date").apply(lambda d: pd.Series({"deep": d.r1[d.gap <= -0.10].mean(), "shallow": d.r1[d.gap > -0.06].mean()}), include_groups=False).dropna()
        print(f"\n(SPY ≤ −1 %: on {len(g)} days with both, the ≤ −10 % names' mean beats the −4..−6 % names' on {100 * (g.deep > g.shallow).mean():.0f} %; "
              f"median day difference {g.deep.sub(g.shallow).median():+.2f} pp)\n")
print("\n## The same with QQQ's opening gap (user: \"It wasn't SPY so much that opened down, but Nasdaq\" — 2026-11-05: SPY −0.17 %, QQQ −1.28 %)\n")
Q = con.execute("""SELECT strftime(date, '%Y-%m-%d') date, ((open * n - lag(close * n) OVER w) + (cum_div - lag(cum_div) OVER w)) / lag(close * n) OVER w qqq_gap
                   FROM db.daily_adjusted WHERE ticker = 'QQQ' WINDOW w AS (ORDER BY date)""").df()
B = B.merge(Q, on="date", how="left")
print("| QQQ's opening gap | days | all depths | " + " | ".join(l for l, _, _ in DEPTH) + " |\n|---|---|---|" + "---|" * len(DEPTH))
for lab, lo, hi in (("≤ −1 %", -9, -0.01), ("−1 .. −0.3 %", -0.01, -0.003), ("> −0.3 %", -0.003, 9)):
    X = B[(B.qqq_gap > lo) & (B.qqq_gap <= hi)]
    print(f"| {lab} | {X.date.nunique():,} | {cell(X)} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) + " |")
X = B[(B.qqq_gap <= -0.01) & (B.spy_gap > -0.003)]
print(f"| **QQQ ≤ −1 % but SPY > −0.3 %** (today's kind) | {X.date.nunique():,} | {cell(X)} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) + " |")
X = B[(B.qqq_gap <= -0.01) & (B.spy_gap > -0.01)]
print(f"| QQQ ≤ −1 %, SPY > −1 % | {X.date.nunique():,} | {cell(X)} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) + " |")
X = B[(B.qqq_gap <= -0.01) & ~B.date.str.startswith("2020-03")]
print(f"| QQQ ≤ −1 %, without 2020-03 | {X.date.nunique():,} | {cell(X)} | " + " | ".join(cell(X[(X.gap > a) & (X.gap <= b)]) for _, a, b in DEPTH) + " |")
