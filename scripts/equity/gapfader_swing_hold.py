"""GapFader GF-62 (user 2026-11-01): the gap-up swing HELD PAST THE CLOSE — "test whether holding these trades for a few
days would be better than exiting at the close. We should take into account winners and losers as separate categories."

The book: GF-60/61's calm book's 10:00 unit (2,705 names). Exits: day D's close (GF-59) · D+1's open · the close of
D+1 / D+2 / D+3 / D+5 (trading days of the ticker). Returns are the HOLDER'S return (docs/price_adjustment.md):
[(P2·n2 − P1·n1) + (C2 − C1)] / (P1·n1) on daily_adjusted's raw P, causal n and cum_div — splits and dividends inside the
hold are counted, nothing is back-adjusted; net 0.7c / share once. The categories are known at the decision: a WINNER /
LOSER is the trade's net return at D's close (> 0 / <= 0). For each: what the hold ADDS after D's close (the holder's
return close(D) → the exit), and the policies hold-all / hold the winners only / hold the losers only.
⭐ Control: daily_adjusted's close on D == the census's close. A name with fewer rows than the exit needs (a delisting,
the data's end) exits at its last close and is counted; a hold whose n changes (a split inside it) is counted too.

  cd research && python -u scripts/equity/gapfader_swing_hold.py > data/equity/gapfader/swing_hold.md 2> logs/gapfader_swing_hold.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & (C.rvol_30m >= 1)
      & (C.volat_clk < 40)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True); C["i"] = np.arange(len(C)); N = len(C)

con = duckdb.connect(); con.execute("set memory_limit='2GB'; set threads=4; set enable_progress_bar=false")
bk = C[["i", "ticker", "date"]].copy(); bk["date"] = pd.to_datetime(bk.date); con.register("bk", bk)
D = con.execute("""
    SELECT bk.i, row_number() OVER (PARTITION BY bk.i ORDER BY a.date) - 1 AS k, a.date, a.open, a.close, a.n, a.cum_div
    FROM bk JOIN 'data/v2/daily_adjusted.parquet' a ON a.ticker = bk.ticker AND a.date >= bk.date AND a.date <= bk.date + INTERVAL 20 DAY
    ORDER BY bk.i, a.date""").df()
K = 6
O = np.full((N, K), np.nan); CLs = np.full((N, K), np.nan); NN = np.full((N, K), np.nan); CD = np.full((N, K), np.nan)
d = D[D.k < K]; O[d.i, d.k] = d.open; CLs[d.i, d.k] = d.close; NN[d.i, d.k] = d.n; CD[d.i, d.k] = d.cum_div
assert np.isfinite(CLs[:, 0]).all() and (pd.to_datetime(D[D.k == 0].date).dt.strftime("%Y-%m-%d").values == C.date.values).all()
err = np.max(np.abs(CLs[:, 0] / C.close.values - 1)); assert err < 1e-9, err
log(f"control: daily_adjusted's close on D == the census close (max rel err {err:.1e})")
# a missing later row: carry the last available close (counted)
miss = np.isnan(CLs).sum(axis=0)
for kk in range(1, K):
    for A in (CLs, NN, CD):
        A[:, kk] = np.where(np.isnan(A[:, kk]), A[:, kk - 1], A[:, kk])
    O[:, kk] = np.where(np.isnan(O[:, kk]), CLs[:, kk - 1], O[:, kk])

E0 = C.entry_30.values; n0 = NN[:, 0]; c0 = CD[:, 0]
def hold(P2, n2, c2, P1=E0, n1=n0, c1=c0): return ((P2 * n2 - P1 * n1) + (c2 - c1)) / (P1 * n1)
cost = 0.007 / E0
EXITS = [("D close (GF-59)", CLs[:, 0], NN[:, 0], CD[:, 0]), ("D+1 open", O[:, 1], NN[:, 1], CD[:, 1])] + \
        [(f"D+{kk} close", CLs[:, kk], NN[:, kk], CD[:, kk]) for kk in (1, 2, 3, 5)]
R = {nm: hold(p, n, c) - cost for nm, p, n, c in EXITS}                         # the whole trade, entry → exit
ADD = {nm: hold(p, n, c, CLs[:, 0], NN[:, 0], CD[:, 0]) for nm, p, n, c in EXITS}  # the hold alone, close(D) → exit
split_in = {nm: (n != NN[:, 0]) for nm, p, n, c in EXITS}
base = R["D close (GF-59)"]; WIN = base > 0
YR = C.date.str[:4].values; EARLY = YR <= "2020"
def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
def yrs_lt1(r): return int((pd.Series(r).groupby(YR).apply(pf) < 1).sum())

print("# GF-62 — the gap-up swing held past the close (calm · held at 10:00 · rvol30 ≥ 1; bought at 10:00)\n")
print(f"{N:,} names, {C.date.min()}..{C.date.max()}; the holder's return on daily_adjusted (raw P, causal n and cum_div); "
      f"net 0.7c / share once. Winners at D's close: {WIN.sum():,} ({100 * WIN.mean():.0f} %), losers {(~WIN).sum():,}. "
      f"Names short of rows (exit at the last close) by exit D+1..D+5: {', '.join(str(int(x)) for x in miss[1:])}.\n")
print("## 1. What the hold ADDS after D's close (close(D) → the exit), by the trade's state at the close\n")
print("| exit | all: mean % (PF) | WINNERS at the close: mean % · median % · PF · win % | LOSERS at the close: mean % · median % · PF · win % | holds with a split |\n|---|---|---|---|---|")
for nm, *_ in EXITS[1:]:
    a = ADD[nm]; w, l = a[WIN], a[~WIN]
    print(f"| {nm} | {100 * a.mean():+.2f} ({pf(a):.2f}) | {100 * w.mean():+.2f} · {100 * np.median(w):+.2f} · {pf(w):.2f} · {100 * (w > 0).mean():.0f} | "
          f"{100 * l.mean():+.2f} · {100 * np.median(l):+.2f} · {pf(l):.2f} · {100 * (l > 0).mean():.0f} | {int(split_in[nm].sum())} |")
print("\n## 2. The policies: the whole trade (10:00 → the exit), net\n")
print("| policy | exit | mean % | net PF | net PF clip +50 % | 2016–20 / 2021–26 | years < 1 | p1 % | worst % |\n|---|---|---|---|---|---|---|---|---|")
def prow(pol, nm, r):
    print(f"| {pol} | {nm} | {100 * r.mean():+.3f} | **{pf(r):.3f}** | {pf(np.minimum(r, 0.5)):.3f} | {pf(r[EARLY]):.2f} / {pf(r[~EARLY]):.2f} | "
          f"{yrs_lt1(r)}/11 | {100 * np.quantile(r, 0.01):+.1f} | {100 * r.min():+.1f} |")
prow("all out at D's close", "—", base)
for nm, *_ in EXITS[1:]:
    prow("hold ALL", nm, R[nm])
for nm, *_ in EXITS[1:]:
    prow("hold the WINNERS, losers out at D's close", nm, np.where(WIN, R[nm], base))
for nm, *_ in EXITS[1:]:
    prow("hold the LOSERS, winners out at D's close", nm, np.where(~WIN, R[nm], base))
print("\n## 3. The winners by size at the close: what holding to D+1's open / D+3's close adds (mean % · PF)\n")
print("| net at D's close | n | D+1 open | D+1 close | D+3 close |\n|---|---|---|---|---|")
for a_, b_ in ((-1, -0.05), (-0.05, -0.02), (-0.02, 0), (0, 0.02), (0.02, 0.05), (0.05, 0.10), (0.10, 9)):
    s = (base > a_) & (base <= b_)
    if s.sum() < 20: continue
    lab = f"({100 * a_:g}, {100 * b_:g}] %" if b_ < 9 else f"> {100 * a_:g} %"
    if a_ == -1: lab = f"≤ {100 * b_:g} %"
    print(f"| {lab} | {s.sum():,} | " + " | ".join(f"{100 * ADD[nm][s].mean():+.2f} · {pf(ADD[nm][s]):.2f}" for nm in ("D+1 open", "D+1 close", "D+3 close")) + " |")
log("done")
