"""GapFader GF-55 — CONSISTENCY of the system as deployed since 2026-10-27 (user 2026-10-28: "how well does it do in
backtesting on a consistency basis — its % of profitable days, weeks and months?").

GF-48's method (gapfader_consistency.py) on GF-54's book (gapfader_model_v2.py -> data/equity/gapfader/model_v2.parquet):
  the gates dv60_med ≥ $500k · rvol60 < 0.02 · gap and the noisy auction gap ≤ −4 % · open ≥ $1 · not NYSE · no early close
  the trade: ONE target at 50 % of the gap (trade-through) to 10:00, else the 10:00 1s vwap, 0.40c a share (r1, in %)
  pf1: GF-54's model (gf_coefficients.json; fitted on 2021–26, so IN-SAMPLE there)
THE SIZE AS THE OMS SIZES IT, at a FIXED equity E (the Oms sizes on 8,000; no compounding; results in % of E):
  units = clip(pf1, 0.125, 2); notional = min(units × 15 % × E, 0.25 % × dv60_med)
  the day's signals ranked by pf1, each takes what the book has left: Σ units ≤ 10, Σ notional × req ≤ 100 % × E (req 50 %,
  100 % under $3); below the minimum notional min(2.5 % E, 1k) refused; shares = floor(notional / (pc × 0.96)), filled at
  the open.
A period with no trade is not counted (neither a win nor a loss). Weeks = ISO weeks, months = calendar months.
Run from research/:
  python -u scripts/equity/gapfader_consistency_v2.py > data/equity/gapfader/consistency_v2.md 2> logs/gapfader_consistency_v2.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
E = float(sys.argv[1]) if len(sys.argv) > 1 else 8000.0
C = pd.read_parquet("data/equity/gapfader/model_v2.parquet")
C["pc"] = C.open / (1 + C.gap)
C["r"] = C.r1 / 100
C["pf1"] = C.pf1_new
log(f"{len(C):,} trades {C.date.min()} .. {C.date.max()}")
UNIT, MAXU, MINN = 0.15 * E, 10.0, min(0.025 * E, 1000.0)
C = C.sort_values(["date", "pf1", "ticker"], ascending=[True, False, True]).reset_index(drop=True)
shares = np.zeros(len(C)); refused = 0; bound = {"units": 0, "margin": 0, "dv60cap": 0}
pf1, dv60, pc = C.pf1.values, C.dv60_med.values, C.pc.values
for d, idx in C.groupby("date").indices.items():
    used_u, used_m = 0.0, 0.0
    for i in idx:
        u = np.clip(pf1[i], 0.125, 2.0)
        want = min(u * UNIT, 0.0025 * dv60[i])
        if want < u * UNIT: bound["dv60cap"] += 1
        req = 1.0 if pc[i] < 3 else 0.5
        take = min(want, (MAXU - used_u) * UNIT, (E - used_m) / req)
        if take < want: bound["units" if (MAXU - used_u) * UNIT <= (E - used_m) / req else "margin"] += 1
        if take < MINN: refused += 1; continue
        n = np.floor(take / (pc[i] * 0.96))
        if n < 1: refused += 1; continue
        shares[i] = n; used_u += take / UNIT; used_m += take * req
C["shares"] = shares
C["pnl"] = C.shares * C.open * C.r
T = C[C.shares > 0].copy()
T["d"] = pd.to_datetime(T.date)
log(f"taken {len(T):,}, refused {refused:,}; bound {bound}")


def periods(T_):
    return {f: T_.groupby(T_.d.dt.to_period(f)).pnl.sum() / E * 100 for f in ("D", "W", "M")}
def mdd(s):
    cum = np.concatenate([[0.0], s.cumsum().values]); dd = np.maximum.accumulate(cum) - cum
    return dd.max()
def streak(s):
    best = cur = 0
    for v in s.values:
        cur = cur + 1 if v < 0 else 0; best = max(best, cur)
    return best
def row(nm, T_):
    p = periods(T_); day, wk, mo = p["D"], p["W"], p["M"]
    pos, neg = T_.pnl[T_.pnl > 0].sum(), -T_.pnl[T_.pnl < 0].sum()
    yrs = max((T_.d.max() - T_.d.min()).days / 365.25, 1 / 12)
    share = lambda s: f"**{(s > 0).mean() * 100:.0f} %** ({(s > 0).sum():,}/{len(s):,})"
    print(f"| {nm} | {len(T_):,} | {share(day)} | {share(wk)} | {share(mo)} | {pos / neg:.2f} | {day.sum() / yrs:+.0f} % | "
          f"{mdd(day):.1f} % | {day.min():+.1f} % | {wk.min():+.1f} % | {mo.min():+.1f} % | {streak(day)} / {streak(wk)} / {streak(mo)} |")


print(f"# GF-55 — GapFader consistency as deployed (dv60_med ≥ $500k, rvol60, GF-54's model; equity {E:,.0f} fixed)\n")
print(f"`scripts/equity/gapfader_consistency_v2.py` on GF-54's book (`model_v2.parquet`). {len(T):,} trades taken of {len(C):,} "
      f"({refused:,} refused under the minimum notional {MINN:,.0f}); bound by the book's units {bound['units']:,}, by margin "
      f"{bound['margin']:,}, by the 0.25 %-of-dv60_med cap {bound['dv60cap']:,}.\n")
print("| period | trades | **profitable days** | **profitable weeks** | **profitable months** | net PF | P&L / yr (% of E) | "
      "max drawdown | worst day | worst week | worst month | losing streak days / weeks / months |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
Y = T.d.dt.year
for nm, m in [("all 2016-08 .. 2026-09", Y > 0), ("2016–19 (out of sample)", Y <= 2019), ("2020", Y == 2020),
              ("2021–23", (Y >= 2021) & (Y <= 2023)), ("2024–26", Y >= 2024)]:
    row(nm, T[m])
print("\n## By year\n\n| year | trades | profitable days | profitable weeks | profitable months | net PF | P&L (% of E) | max drawdown |\n|---|---|---|---|---|---|---|---|")
for y in sorted(Y.unique()):
    T_ = T[Y == y]; p = periods(T_)
    pos, neg = T_.pnl[T_.pnl > 0].sum(), -T_.pnl[T_.pnl < 0].sum()
    print(f"| {y} | {len(T_):,} | {(p['D'] > 0).mean() * 100:.0f} % | {(p['W'] > 0).mean() * 100:.0f} % | {(p['M'] > 0).mean() * 100:.0f} % "
          f"({(p['M'] > 0).sum()}/{len(p['M'])}) | {pos / neg:.2f} | {p['D'].sum():+.0f} % | {mdd(p['D']):.1f} % |")
day = periods(T)["D"]
print(f"\nTrading days with a GapFader trade: {len(day):,} (of ~{np.busday_count(T.d.min().date(), T.d.max().date()):,} weekdays); "
      f"a median of {T.groupby('date').size().median():.0f} trades on such a day.")
log("done")
