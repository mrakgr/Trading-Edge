"""Run 32 — can a LIQUIDITY measure replace the low-float gate? (user 2026-11-05: "The only real problem with that system was
getting good quality low float data ... I wonder if we could replace the low float criteria via 60d median dollar volume or
some other indicator?")

The causal 10:00 book (Run 31: highflyer_v2_port.py --scale causal) with its causal dollar float (77 % covered). Candidates,
all known before the entry:
  dv60_med   median of the raw close · volume over the 60 sessions before the entry day (GapFader's definition; >= 20 needed)
  adv20      the engine's own: mean close · volume over the prior 20 bars
  price      the raw 10:00 close
A+ (Run 17) = low float + a red 10:00 candle −1..−6 % from the open; here the same candle with the proxy in place of float.
    cd research && python3 -u scripts/equity/highflyer_v2_float_proxy.py > data/equity/highflyer_repro/v2_float_proxy.md 2> logs/highflyer_v2_float_proxy.log
"""
import duckdb, numpy as np, pandas as pd

con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
con.execute("ATTACH 'data/equity/float/float.db' AS f (READ_ONLY)")
T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date); T["yr"] = T.entry_date.dt.year
con.register("t", T[["symbol", "entry_date"]])
con.execute("""CREATE TEMP TABLE s AS SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i
               FROM db.daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t)""")
M = con.execute("""SELECT t.symbol, t.entry_date, median(p.dv) dv60_med, count(*) k
    FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
    JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
T = T.merge(M, on=["symbol", "entry_date"], how="left")
T.loc[T.k < 20, "dv60_med"] = np.nan
# the causal float, as Run 31 (highflyer_v2_causal.py)
F = con.execute("""
  WITH fm AS (SELECT tc.ticker, s.known_date, s.period_end, s.value fu FROM f.float_sec s JOIN f.ticker_cik tc ON tc.cik = s.cik WHERE s.value > 0),
  sec AS (SELECT t.symbol, t.entry_date, arg_max(fm.fu, fm.known_date) fu, arg_max(fm.period_end, fm.known_date) pe
          FROM t JOIN fm ON fm.ticker = t.symbol AND fm.known_date <= t.entry_date GROUP BY 1, 2),
  sec_px AS (SELECT sec.*, (SELECT arg_max(close, date) FROM db.daily_adjusted a WHERE a.ticker = sec.symbol AND a.date <= sec.pe) p_pe,
                           (SELECT arg_max(n, date) FROM db.daily_adjusted a WHERE a.ticker = sec.symbol AND a.date <= sec.pe) n_pe FROM sec),
  pol AS (SELECT t.symbol, t.entry_date, arg_max(ps.scso, ps.date) scso, max(ps.date) pd FROM t
          JOIN f.polygon_shares ps ON ps.ticker = t.symbol AND ps.date <= t.entry_date AND ps.scso > 0 GROUP BY 1, 2),
  pol_n AS (SELECT pol.*, (SELECT arg_max(n, date) FROM db.daily_adjusted a WHERE a.ticker = pol.symbol AND a.date <= pol.pd) n_pd FROM pol)
  SELECT t.symbol, t.entry_date, s.fu / s.p_pe / s.n_pe sec_sh, p.scso / p.n_pd pol_sh FROM t
  LEFT JOIN sec_px s USING (symbol, entry_date) LEFT JOIN pol_n p USING (symbol, entry_date)""").df()
T = T.merge(F, on=["symbol", "entry_date"], how="left")
T["dollar_float"] = T.sec_sh.where(T.sec_sh > 0, T.pol_sh) * T.n * T.raw_close_1000
T["lowf"] = T.dollar_float < 300e6
T["red_dip"] = (T.raw_close_1000 / T.raw_open_1000 - 1).between(-0.06, -0.01)
T["price"] = T.raw_close_1000

def pf(r):
    r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, 0.5)[r > 0].sum() / l
def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
def row(lab, X):
    r = X.ret.values
    if len(r) < 15: return f"| {lab} | {len(r)} | — | — | — | — | — |"
    a, b, c = (X.ret[(X.yr >= lo) & (X.yr <= hi)].values for lo, hi in ((2005, 2014), (2015, 2020), (2021, 2026)))
    cov = X.dollar_float.notna()
    return (f"| {lab} | {len(r):,} | **{f2(pf(r))}** | {100 * r.mean():+.2f} % | {f2(pf(a))} / {f2(pf(b))} / {f2(pf(c))} | "
            f"{100 * (X.yr >= 2021).mean():.0f} % | {100 * X.lowf[cov].mean():.0f} % ({cov.sum():,}) |")
H = ("| cut | trips | PF clip | mean | 2005–14 / 2015–20 / 2021–26 | share 2021+ | low float among the covered |\n"
     "|---|---|---|---|---|---|---|")

print("# Run 32 — a liquidity proxy for the low-float gate (scripts/equity/highflyer_v2_float_proxy.py)\n")
print(f"The causal 10:00 book: {len(T):,} trips; dv60_med known on {T.dv60_med.notna().sum():,}; float known on "
      f"{T.dollar_float.notna().sum():,} ({100 * T.dollar_float.notna().mean():.0f} %).\n")
print("## 1. How well each proxy tracks low float (the covered trips)\n")
C = T[T.dollar_float.notna() & T.dv60_med.notna()]
print("| proxy | Spearman ρ with dollar float | median, low float | median, float ≥ \\$300M | AUC (low float vs not) |\n|---|---|---|---|---|")
for col, lab, fmt in (("dv60_med", "dv60_med", lambda x: f"\\${x / 1e6:.2f}M"), ("adv", "adv20", lambda x: f"\\${x / 1e6:.2f}M"),
                      ("price", "price", lambda x: f"\\${x:.2f}")):
    rho = C[col].rank().corr(C.dollar_float.rank())
    lo, hi = C[col][C.lowf], C[col][~C.lowf]
    r = pd.concat([lo, hi]).rank(); auc = (r[:len(lo)].sum() - len(lo) * (len(lo) + 1) / 2) / (len(lo) * len(hi))
    print(f"| {lab} | {rho:.2f} | {fmt(lo.median())} | {fmt(hi.median())} | {1 - auc:.2f} |")
print("\nAUC = the chance a random low-float trip has a LOWER value than a random high-float one.\n")

print("## 2. PF by dv60_med band (the whole book, float or not)\n"); print(H)
bands = [0, 0.5e6, 1e6, 2e6, 5e6, 10e6, 25e6, np.inf]
for lo, hi in zip(bands[:-1], bands[1:]):
    print(row(f"\\${lo / 1e6:g}–{hi / 1e6:g}M", T[(T.dv60_med >= lo) & (T.dv60_med < hi)]))
print(row("dv60_med unknown", T[T.dv60_med.isna()]))
print()
print("## 3. Cumulative cuts vs the float gate\n"); print(H)
print(row("all", T)); print(row("**low float (< \\$300M)**", T[T.lowf == True])); print(row("float unknown", T[T.dollar_float.isna()]))
for x in (1e6, 2e6, 3e6, 5e6, 10e6):
    print(row(f"dv60_med < \\${x / 1e6:g}M", T[T.dv60_med < x]))
for x in (1e6, 2e6, 5e6):
    print(row(f"adv20 < \\${x / 1e6:g}M", T[T.adv < x]))
for x in (5, 10, 20):
    print(row(f"price < \\${x}", T[T.price < x]))
for x in (2e6, 5e6):
    print(row(f"dv60_med < \\${x / 1e6:g}M · float unknown", T[(T.dv60_med < x) & T.dollar_float.isna()]))
    print(row(f"dv60_med < \\${x / 1e6:g}M · float ≥ \\$300M", T[(T.dv60_med < x) & (T.dollar_float >= 300e6)]))
    print(row(f"dv60_med ≥ \\${x / 1e6:g}M · low float", T[(T.dv60_med >= x) & (T.lowf == True)]))
print()
print("## 4. A+ with the proxy in place of float (red 10:00 candle, −1..−6 % from the open)\n"); print(H)
R = T[T.red_dip]
print(row("red dip, any liquidity", R)); print(row("**A+ (red dip · low float)**", R[R.lowf == True]))
for x in (1e6, 2e6, 3e6, 5e6, 10e6):
    print(row(f"red dip · dv60_med < \\${x / 1e6:g}M", R[R.dv60_med < x]))
print(row("red dip · dv60_med ≥ \\$5M", R[R.dv60_med >= 5e6]))
print()
print("## 5. Like for like: where the float data exists (2013+, the SEC frames' era) and by listing type\n")
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM db.ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(ty, on="symbol", how="left")
print("float coverage by type and era: " + ", ".join(
    f"{t} {e}: {100 * T[(T.typ.str.contains(t)) & m].dollar_float.notna().mean():.0f} % of {int(((T.typ.str.contains(t)) & m).sum())}"
    for t in ("CS", "ADRC") for e, m in (("2005–12", T.yr <= 2012), ("2013+", T.yr >= 2013))) + "\n")
print(H)
L = T[T.yr >= 2013]
print(row("2013+ · all", L)); print(row("2013+ · **low float**", L[L.lowf == True])); print(row("2013+ · float unknown", L[L.dollar_float.isna()]))
for x in (1e6, 2e6, 3e6, 5e6):
    print(row(f"2013+ · dv60_med < \\${x / 1e6:g}M", L[L.dv60_med < x]))
    print(row(f"2013+ · dv60_med < \\${x / 1e6:g}M · float known", L[(L.dv60_med < x) & L.dollar_float.notna()]))
print(row("2013+ · CS only · dv60_med < \\$2M", L[(L.dv60_med < 2e6) & (L.typ == "CS")]))
print(row("2013+ · ADRC · dv60_med < \\$2M", L[(L.dv60_med < 2e6) & L.typ.str.contains("ADRC")]))
R = L[L.red_dip]
print(row("2013+ · **A+ (red dip · low float)**", R[R.lowf == True]))
for x in (1e6, 2e6, 3e6):
    print(row(f"2013+ · red dip · dv60_med < \\${x / 1e6:g}M", R[R.dv60_med < x]))
print(row("2013+ · red dip · CS · dv60_med < \\$2M", R[(R.dv60_med < 2e6) & (R.typ == "CS")]))
