"""GapFader GF-49 (user 2026-10-26, after the NIVF trade: "we need to avoid trading stocks that are down more than 95 % in
the past year"): the DEATH-SPIRAL rule on the GF-45 book.

THE BOOK: GF-45's (scripts/equity/gapfader_sizing_model.py: the same census, tape cache, listing map, gates and auction
noise, seed 20261020), with the exit the Oms trades: ONE target at 50 % of the gap (trade-through) until 10:00, else the
10:00 1s vwap; 0.40c a share round trip. Sizing = production's units = clip(PF−1, 0.125, 2), PF−1 from
gf_coefficients.json (the GF-46 refit), as Engine/GapFader.fs's Model.size.

THE FEATURE (knowable before the open; docs/price_adjustment.md): the 1-year change of the price on D's share scale,
  chg_1y = close(D−1)·n(D−1) / (close(Y)·n(Y)) − 1,   Y = the last session on or before D−1 − 365 days,
inside the ticker's EPISODE (a > 45-day gap between rows starts a new listing: the census's rule, so a recycled ticker
never reaches into an earlier company). A ticker with less than a year of history in its episode has no chg_1y; for it
chg_list = the change since the episode's first close is reported apart (≥ 60 sessions).

  cd research && python -u scripts/equity/gapfader_spiral.py > data/equity/gapfader/spiral.md 2> logs/gapfader_spiral.log; echo exit $?
"""
import json, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)

# ---------------------------------------------------------------- the GF-45 book (gapfader_sizing_model.py, verbatim gates)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap <= -0.035) & (C.rvol_pm < 0.05)].copy()
L = pd.read_parquet("data/equity/gapfader/listing_2026-10-17.parquet")
C = C.merge(L, on="ticker", how="left"); C = C[C.primary_exchange != "XNYS"].copy()
V = pd.read_parquet("data/equity/gapfader/sizing_tape_cut.parquet")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x_36000.notna()].copy()
C["pmvolc"] = np.where(C.primary_exchange == "XASE", C.pmvol_50, C.pmvol_25)
C["rvolc"] = C.pmvolc / C.avgvol20_prior
rng = np.random.default_rng(20261020)
e = np.exp(rng.normal(np.log(0.001), np.log(6.5) / 1.2816, len(C))) * rng.choice([-1, 1], len(C))
C["gap_est"] = C.open * (1 + e) / C.pc_total - 1
C = C[(C.gap <= -0.04) & (C.gap_est <= -0.04) & (C.rvolc < 0.02)].copy()
t50 = C.tf_50.astype("float64").fillna(np.inf)
C["r"] = (np.where(t50 < 36000, C.open + 0.5 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open) * 100

# production sizing: Model.size (Engine/GapFader.fs) from gf_coefficients.json
J = json.load(open("data/equity/gapfader/gf_coefficients_pre_gf54.json"))
raw = {"ldv": np.log(C.dv20_prior.values), "lrv": np.log(C.rvolc.values + 0.001), "rv5": (C.rvolc.values < 0.005) * 1.0}
X = [np.ones(len(C))]
for s in J["standardise"]:
    x = raw[s["term"]]
    lo, hi = s["winsor"]
    if lo is not None: x = np.clip(x, lo, hi)
    X.append((x - s["mean"]) / s["sd"])
X = np.column_stack(X)
p = 1 / (1 + np.exp(-(X @ np.array(J["beta_p"]))))
C["pf1"] = p / ((1 - p) * np.exp(X @ np.array(J["beta_l"])) / np.exp(X @ np.array(J["beta_w"]))) - 1
C["units"] = np.clip(C.pf1, 0.125, 2.0)
C["date"] = pd.to_datetime(C.date)
C["prev_date"] = pd.to_datetime(C.prev_date)
log(f"book {len(C):,} trades")

# ---------------------------------------------------------------- chg_1y, causal, inside the episode
con = duckdb.connect()
con.execute("set memory_limit='4GB'; set threads=6; set enable_progress_bar=false; set temp_directory='logs/duckdb_tmp_spiral'")
con.register("bk", C[["ticker", "date", "prev_date"]])
F = con.execute("""
    WITH e0 AS (SELECT ticker, date, close * n AS adj,
                       CASE WHEN date - lag(date) OVER (PARTITION BY ticker ORDER BY date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' WHERE ticker IN (SELECT DISTINCT ticker FROM bk)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode,
                  row_number() OVER (PARTITION BY ticker ORDER BY date) AS rn FROM e0),
    first AS (SELECT ticker, episode, arg_min(adj, date) AS adj0, min(rn) AS rn0 FROM ep GROUP BY ALL),
    b AS (SELECT bk.ticker, bk.date, p.adj AS adj_prev, p.episode, p.rn, (bk.prev_date - INTERVAL 365 DAY)::DATE AS y_date
          FROM bk JOIN ep p ON p.ticker = bk.ticker AND p.date = bk.prev_date)
    SELECT b.ticker, b.date, b.adj_prev, y.adj AS adj_1y, y.date AS d_1y, f.adj0, b.rn - f.rn0 + 1 AS sessions
    FROM b ASOF LEFT JOIN ep y ON y.ticker = b.ticker AND y.episode = b.episode AND y.date <= b.y_date
    JOIN first f ON f.ticker = b.ticker AND f.episode = b.episode""").df()
F["date"] = pd.to_datetime(F.date)
C = C.merge(F, on=["ticker", "date"], how="left")
C["chg_1y"] = C.adj_prev / C.adj_1y - 1
C["chg_list"] = np.where(C.adj_1y.isna() & (C.sessions >= 60), C.adj_prev / C.adj0 - 1, np.nan)
log(f"chg_1y known {C.chg_1y.notna().mean() * 100:.1f} %, <1y history {C.adj_1y.isna().mean() * 100:.1f} %")

# ---------------------------------------------------------------- tables
R = C.r.values; U = C.units.values; YR = C.date.dt.year.values
def pf(r, w=None):
    r = np.asarray(r) * (1 if w is None else np.asarray(w)); g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l
def fmt(x): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.2f}")
def row(name, m):
    r, u, y = R[m], U[m], YR[m]
    yrs = " | ".join(fmt(pf(r[y == k])) + f" [{(y == k).sum()}]" for k in range(2016, 2027))
    return (f"| {name} | {m.sum():,} | {fmt(pf(r))} | {fmt(pf(r, u))} | {r.mean():+.2f} | {np.median(r):+.2f} | {r.min():+.1f} | "
            f"{(r * u).sum() / 100:+.1f} | {yrs} |")
HDR = ("| slice | n | PF flat | PF units | mean % | median % | worst % | Σ units·r | " + " | ".join(str(k) for k in range(2016, 2027)) + " |\n"
       + "|---|---|---|---|---|---|---|---|" + "---|" * 11)

print("# GF-49 — the death-spiral rule on the GapFader book (user 2026-10-26, after NIVF)\n")
print(f"Book: {len(C):,} trades {C.date.min():%Y-%m}..{C.date.max():%Y-%m}, the Oms's exit (one target at 50 % to 10:00) and units "
      f"(clip(PF−1, 0.125, 2), Model.size). chg_1y known for {C.chg_1y.notna().sum():,}; {C.adj_1y.isna().sum():,} have < 1 year of "
      f"history in their episode.\n")

print("## chg_1y buckets (years: PF flat [n])\n")
print(HDR)
cuts = [(-1.01, -0.99, "≤ −99 %"), (-0.99, -0.95, "−99 … −95 %"), (-0.95, -0.90, "−95 … −90 %"), (-0.90, -0.75, "−90 … −75 %"),
        (-0.75, -0.50, "−75 … −50 %"), (-0.50, 0.0, "−50 … 0 %"), (0.0, 1.0, "0 … +100 %"), (1.0, np.inf, "> +100 %")]
k = C.chg_1y.values
for lo, hi, nm in cuts:
    print(row(nm, (k > lo) & (k <= hi)))
print(row("< 1 year of history", np.isnan(k)))
print(row("ALL", np.ones(len(C), bool)))

print("\n## < 1 year of history: the change since the listing's first close (≥ 60 sessions)\n")
print(HDR)
kl = C.chg_list.values
for lo, hi, nm in [(-1.01, -0.95, "≤ −95 % since listing"), (-0.95, -0.75, "−95 … −75 %"), (-0.75, 0.0, "−75 … 0 %"), (0.0, np.inf, "> 0 %")]:
    print(row(nm, (kl > lo) & (kl <= hi)))
print(row("< 60 sessions", np.isnan(k) & np.isnan(kl)))

print("\n## THE RULE: refuse chg_1y ≤ threshold (threshold sweep; < 1 year of history kept)\n")
print("| rule | refused n (%) | refused PF flat / units | refused Σ units·r | kept n | kept PF flat | kept PF units | Δ PF units | kept Σ units·r | Δ Σ |")
print("|---|---|---|---|---|---|---|---|---|---|")
base_pf, base_sum = pf(R, U), (R * U).sum() / 100
for th in (-0.80, -0.90, -0.95, -0.98, -0.99):
    ref = ~np.isnan(k) & (k <= th)
    for also_list in (False, True):
        rr = ref | (also_list & ~np.isnan(kl) & (kl <= th))
        kp = ~rr
        nm = f"chg_1y ≤ {th * 100:.0f} %" + (" (+ since listing)" if also_list else "")
        print(f"| {nm} | {rr.sum():,} ({rr.mean() * 100:.2f} %) | {fmt(pf(R[rr]))} / {fmt(pf(R[rr], U[rr]))} | {(R[rr] * U[rr]).sum() / 100:+.1f} | "
              f"{kp.sum():,} | {fmt(pf(R[kp]))} | {fmt(pf(R[kp], U[kp]))} | {pf(R[kp], U[kp]) - base_pf:+.3f} | {(R[kp] * U[kp]).sum() / 100:+.1f} | "
              f"{(R[kp] * U[kp]).sum() / 100 - base_sum:+.1f} |")

print("\n## The refused trades at −95 %, by regime (flat PF [n])\n")
ref95 = ~np.isnan(k) & (k <= -0.95)
for nm, m in [("2016–19", YR <= 2019), ("2020", YR == 2020), ("2021–23", (YR >= 2021) & (YR <= 2023)), ("2024–26", YR >= 2024)]:
    print(f"- {nm}: refused {fmt(pf(R[ref95 & m]))} [{(ref95 & m).sum()}] vs kept {fmt(pf(R[~ref95 & m]))} [{(~ref95 & m).sum()}]")

print("\n## The 25 worst refused trades at −95 %\n")
W = C[ref95].sort_values("r").head(25)
print("| date | ticker | open | gap % | chg_1y % | pf1 | r % |\n|---|---|---|---|---|---|---|")
for _, x in W.iterrows():
    print(f"| {x.date:%Y-%m-%d} | {x.ticker} | {x.open:.2f} | {x.gap * 100:.1f} | {x.chg_1y * 100:.2f} | {x.pf1:.2f} | {x.r:+.1f} |")
C[["ticker", "date", "open", "gap", "r", "pf1", "units", "chg_1y", "chg_list", "sessions"]].to_parquet("data/equity/gapfader/spiral.parquet")
log("done")
