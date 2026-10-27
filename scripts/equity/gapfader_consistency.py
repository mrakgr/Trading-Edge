"""GapFader CONSISTENCY (user 2026-10-23: "how consistent is this system on paper? Max drawdown, worst daily, weekly and monthly
loss; the worst consecutive number of losing days, weeks and months").

THE TRADES: gapfader_sizing_model.py's (GF-45c), its cached tape (data/equity/gapfader/sizing_tape_cut.parquet) and the same
gates and auction-price noise (the same seed): gap ≤ −4 % and gap_est ≤ −4 % · open ≥ $1 · dv20_prior ≥ $1M · no early close ·
not NYSE-listed · rvolc < 0.02. ⭐ THE EXIT AS BUILT IN THE OMS (user 2026-10-23): ONE target at 50 % of the gap (a resting sell
limit, filled on a trade-through, until 10:00), else the 10:00 1s vwap; 0.40c a share round trip.
THE SIZE AS THE OMS SIZES IT (Sizer.fs, GapFader.fs), at a FIXED equity E (no compounding; results in % of E):
  pf1 = the production model (gf_coefficients.json, GF-45d: fitted on 2021–26, so in-sample there)
  units = clip(pf1, 0.125, 2); notional = min(units × 15 % × E, 0.25 % × dv20_prior)
  the day's signals all come at the cutoff: ranked by pf1 (L2.rank), each takes what the book has left —
  Σ units ≤ 10 (a capped row counts the units its notional is) and Σ notional × req ≤ 100 % × E (req 50 %, 100 % under $3);
  a remainder below the minimum notional (min(2.5 % E, 1k)) is refused; shares = floor(notional / (pc × 0.96)) (L2 sizes at
  the LOO's limit), filled at the open.
Daily P&L = Σ shares × open × r (a day = the trades of that date). Weeks = ISO weeks, months = calendar months; a period with
no trade is not a loss (it breaks a losing streak). Max drawdown on the cumulative P&L (simple sum, % of E).
Run from research/:  python -u scripts/equity/gapfader_consistency.py > data/equity/gapfader/consistency.md 2> logs/gapfader_consistency.log; echo exit $?
"""
import json, sys, time
import numpy as np, pandas as pd
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
E = float(sys.argv[1]) if len(sys.argv) > 1 else 8000.0
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.035) & (C.rvol_pm < 0.05)].copy()
L = pd.read_parquet("data/equity/gapfader/listing_2026-10-17.parquet")
C = C.merge(L, on="ticker", how="left"); C = C[C.primary_exchange != "XNYS"].copy()
V = pd.read_parquet("data/equity/gapfader/sizing_tape_cut.parquet")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x_36000.notna()].copy()
C["pmvolc"] = np.where(C.primary_exchange == "XASE", C.pmvol_50, C.pmvol_25)
C["rvolc"] = C.pmvolc / C.avgvol20_prior
rng = np.random.default_rng(20261020)                        # the sizing model's seed: the same noisy gaps, the same trades
e = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(C))) * rng.choice([-1, 1], len(C))
C["gap_est"] = C.open * (1 + e) / C.pc_total - 1
C = C[(C.gap <= -0.04) & (C.gap_est <= -0.04) & (C.rvolc < 0.02)].copy()
t = C.tf_50.astype("float64").fillna(np.inf)
C["r"] = np.where(t < 36000, C.open + 0.5 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
log(f"{len(C):,} trades {C.date.min()} .. {C.date.max()}")
# ---- pf1 from the production coefficients
J = json.load(open("data/equity/gapfader/gf_coefficients_pre_gf54.json"))
raw = {"ldv": np.log(C.dv20_prior.values), "lrv": np.log(C.rvolc.values + 0.001), "rv5": 1.0 * (C.rvolc.values < 0.005)}
X = [np.ones(len(C))] + [((np.clip(raw[s["term"]], *s["winsor"]) if s["winsor"][0] is not None else raw[s["term"]]) - s["mean"]) / s["sd"] for s in J["standardise"]]
X = np.column_stack(X); p = 1 / (1 + np.exp(-X @ J["beta_p"]))
C["pf1"] = p / ((1 - p) * np.exp(X @ J["beta_l"]) / np.exp(X @ J["beta_w"])) - 1
# ---- the Oms's sizing, day by day
UNIT, MAXU, MINN = 0.15 * E, 10.0, min(0.025 * E, 1000.0)
C = C.sort_values(["date", "pf1", "ticker"], ascending=[True, False, True]).reset_index(drop=True)
shares = np.zeros(len(C)); refused = 0; bound = {"units": 0, "margin": 0, "dv20cap": 0}
for d, idx in C.groupby("date").indices.items():
    used_u, used_m = 0.0, 0.0
    for i in idx:
        row = C.iloc[i]
        want = min(np.clip(row.pf1, 0.125, 2.0) * UNIT, 0.0025 * row.dv20_prior)
        if want < np.clip(row.pf1, 0.125, 2.0) * UNIT: bound["dv20cap"] += 1
        req = 1.0 if row.pc_total < 3 else 0.5
        take = min(want, (MAXU - used_u) * UNIT, (E - used_m) / req)
        if take < want: bound["units" if (MAXU - used_u) * UNIT <= (E - used_m) / req else "margin"] += 1
        if take < MINN: refused += 1; continue
        n = np.floor(take / (row.pc_total * 0.96))
        if n < 1: refused += 1; continue
        shares[i] = n; used_u += take / UNIT; used_m += take * req
C["shares"] = shares
C["pnl"] = C.shares * C.open * C.r
T = C[C.shares > 0].copy()
T["d"] = pd.to_datetime(T.date)
log(f"taken {len(T):,}, refused {refused:,}; bound {bound}")
# ---- the series
def series(freq):
    s = T.groupby(T.d.dt.to_period(freq)).pnl.sum() / E * 100
    return s
def streak(s):
    best, cur, end = 0, 0, None
    for k, v in s.items():
        cur = cur + 1 if v < 0 else 0
        if cur > best: best, end = cur, k
    return best, end
def mdd(s):
    cum = np.concatenate([[0.0], s.cumsum().values]); dd = np.maximum.accumulate(cum) - cum
    j = int(np.argmax(dd)); i = int(np.argmax(cum[:j + 1])); idx = ["start"] + list(s.index)
    return dd[j], idx[i], idx[j]
def report(nm, T_):
    day = T_.groupby(T_.d.dt.to_period("D")).pnl.sum() / E * 100
    wk = T_.groupby(T_.d.dt.to_period("W")).pnl.sum() / E * 100
    mo = T_.groupby(T_.d.dt.to_period("M")).pnl.sum() / E * 100
    dd, pk, tr = mdd(day)
    pos, neg = T_.pnl[T_.pnl > 0].sum(), -T_.pnl[T_.pnl < 0].sum()
    ds, de = streak(day); ws, we = streak(wk); ms, me = streak(mo)
    yrs = (T_.d.max() - T_.d.min()).days / 365.25
    print(f"| {nm} | {len(T_):,} | {len(day):,} | {pos / neg:.2f} | {day.sum() / yrs:+.1f} % | **{dd:.1f} %** ({pk} → {tr}) | "
          f"{day.min():+.1f} % ({day.idxmin()}) | {wk.min():+.1f} % ({wk.idxmin()}) | {mo.min():+.1f} % ({mo.idxmin()}) | "
          f"{ds} (to {de}) | {ws} (to {we}) | {ms} (to {me}) | {(day < 0).mean() * 100:.0f} / {(wk < 0).mean() * 100:.0f} / {(mo < 0).mean() * 100:.0f} % |")
print(f"# GapFader consistency (equity {E:,.0f}, fixed; one target at 50 %; the Oms's sizing)\n")
print(f"`scripts/equity/gapfader_consistency.py`. {len(T):,} trades taken of {len(C):,} ({refused:,} refused under the minimum notional "
      f"{MINN:,.0f}); a row bound by the book's units {bound['units']:,}, by margin {bound['margin']:,}, by the 0.25 %-of-dv20 cap {bound['dv20cap']:,}.\n")
print("| period | trades | days | net PF | P&L / yr (% of E) | max drawdown (peak → trough) | worst day | worst week | worst month | "
      "losing days in a row | weeks in a row | months in a row | losing days / weeks / months |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|")
Y = T.d.dt.year
for nm, m in [("all 2016-08 .. 2026-09", Y > 0), ("2016–19", Y <= 2019), ("2020", Y == 2020), ("2021–23", (Y >= 2021) & (Y <= 2023)),
              ("2024–26", Y >= 2024), ("2021–26 (the fit's years)", Y >= 2021)]:
    report(nm, T[m])
print("\n## By year\n\n| year | trades | net PF | P&L (% of E) | max drawdown | worst day | worst week | worst month | losing months |\n|---|---|---|---|---|---|---|---|---|")
for y in sorted(Y.unique()):
    T_ = T[Y == y]
    day = T_.groupby(T_.d.dt.to_period("D")).pnl.sum() / E * 100
    wk = T_.groupby(T_.d.dt.to_period("W")).pnl.sum() / E * 100
    mo = T_.groupby(T_.d.dt.to_period("M")).pnl.sum() / E * 100
    pos, neg = T_.pnl[T_.pnl > 0].sum(), -T_.pnl[T_.pnl < 0].sum()
    print(f"| {y} | {len(T_):,} | {pos / neg:.2f} | {day.sum():+.1f} % | {mdd(day)[0]:.1f} % | {day.min():+.1f} % | {wk.min():+.1f} % | {mo.min():+.1f} % | {(mo < 0).sum()} / {len(mo)} |")
day = T.groupby(T.d.dt.to_period("D")).pnl.sum() / E * 100
print("\n## The 10 worst days\n\n| day | P&L (% of E) | trades | worst trade |\n|---|---|---|---|")
for k in day.nsmallest(10).index:
    g = T[T.d.dt.to_period("D") == k]; w = g.loc[g.pnl.idxmin()]
    print(f"| {k} | {day[k]:+.2f} % | {len(g)} | {w.ticker} {w.r * 100:+.1f} % ({w.pnl / E * 100:+.2f} % of E) |")
log("done")
