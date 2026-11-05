"""Run 31 — HighFlyerV2 (the 10:00 ET entry) on CAUSAL data vs its back-adjusted engine, and against the daily-close
entry on the same rules (user 2026-11-05). Inputs: highflyer_v2_port.py's four books (data/equity/highflyer_repro/
v2port_{partial,close}_{back,causal}.parquet; the back ones reproduce the engine's CSVs exactly), the June breadth parquet.

Low float (Runs 7–28): dollar float < $300M at the 10:00 price. Here CAUSAL for every book: the latest SEC public float
($, float_sec) KNOWN by the entry date, turned into shares at its period-end raw close and carried to the entry date
through the split count n (shares · n(D) / n(pe)), times the RAW 10:00 close; Polygon's scso (latest on/before the
entry date, split-carried the same way) where SEC has none. A+ (Run 17) = low float + a RED 10:00 candle that declined
1–6 % from the open (10:00 close / open − 1 ∈ [−6 %, −1 %]).
    cd research && python3 -u scripts/equity/highflyer_v2_causal.py > data/equity/highflyer_repro/v2_causal.md 2> logs/highflyer_v2_causal.log
"""
import duckdb, numpy as np, pandas as pd

con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
con.execute("ATTACH 'data/equity/float/float.db' AS f (READ_ONLY)")
B = {k: pd.read_parquet(f"data/equity/highflyer_repro/v2port_{k}.parquet") for k in
     ("partial_back", "partial_causal", "close_back", "close_causal")}
br = con.execute("SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b1 FROM 'data/equity/momentum_v0/breadth.parquet'").df()
br["date"] = pd.to_datetime(br.date)

# the causal dollar float for every (symbol, entry_date) in any book, priced at that book's raw entry price
keys = pd.concat([pd.DataFrame({"symbol": t.symbol, "entry_date": pd.to_datetime(t.entry_date)}) for t in B.values()]).drop_duplicates()
con.register("k", keys)
F = con.execute("""
  WITH fm AS (SELECT tc.ticker, s.known_date, s.period_end, s.value fu FROM f.float_sec s JOIN f.ticker_cik tc ON tc.cik = s.cik WHERE s.value > 0),
  sec AS (SELECT k.symbol, k.entry_date, arg_max(fm.fu, fm.known_date) fu, arg_max(fm.period_end, fm.known_date) pe
          FROM k JOIN fm ON fm.ticker = k.symbol AND fm.known_date <= k.entry_date GROUP BY 1, 2),
  sec_px AS (SELECT sec.*, (SELECT arg_max(close, date) FROM db.daily_adjusted a WHERE a.ticker = sec.symbol AND a.date <= sec.pe) p_pe,
                           (SELECT arg_max(n, date) FROM db.daily_adjusted a WHERE a.ticker = sec.symbol AND a.date <= sec.pe) n_pe FROM sec),
  pol AS (SELECT k.symbol, k.entry_date, arg_max(ps.scso, ps.date) scso, max(ps.date) pd FROM k
          JOIN f.polygon_shares ps ON ps.ticker = k.symbol AND ps.date <= k.entry_date AND ps.scso > 0 GROUP BY 1, 2),
  pol_n AS (SELECT pol.*, (SELECT arg_max(n, date) FROM db.daily_adjusted a WHERE a.ticker = pol.symbol AND a.date <= pol.pd) n_pd FROM pol)
  SELECT k.symbol, k.entry_date, (SELECT arg_max(n, date) FROM db.daily_adjusted a WHERE a.ticker = k.symbol AND a.date <= k.entry_date) n_d,
         s.fu / s.p_pe / s.n_pe AS sec_shares_orig, p.scso / p.n_pd AS pol_shares_orig
  FROM k LEFT JOIN sec_px s USING (symbol, entry_date) LEFT JOIN pol_n p USING (symbol, entry_date)""").df()
con.unregister("k")
F["shares_orig"] = F.sec_shares_orig.where(F.sec_shares_orig > 0, F.pol_shares_orig)   # SEC first, Polygon fills

def prep(T, entry):
    T = T.copy(); T["entry_date"] = pd.to_datetime(T.entry_date)
    T = T.merge(br, left_on="entry_date", right_on="date", how="left").merge(F, on=["symbol", "entry_date"], how="left")
    raw_px = T.raw_close_1000 if entry == "partial" else T.entry_price * (1 if "causal" in name else np.nan)
    T["dollar_float"] = T.shares_orig * T.n_d * raw_px
    T["lowf"] = T.dollar_float < 300e6
    T["red_dip"] = (T.raw_close_1000 / T.raw_open_1000 - 1).between(-0.06, -0.01)
    T["yr"] = T.entry_date.dt.year
    return T
for name in B: B[name] = prep(B[name], name.split("_")[0])
# the close books' entry price is raw only in causal; give the back close book the causal float of the same trip
cc = B["close_causal"].set_index(["symbol", "entry_date"]).dollar_float
B["close_back"]["dollar_float"] = B["close_back"].set_index(["symbol", "entry_date"]).index.map(cc)
B["close_back"]["lowf"] = B["close_back"].dollar_float < 300e6

def pf(r, clip=True):
    r = r[np.isfinite(r)]; g = np.minimum(r, 0.5) if clip else r
    l = -r[r < 0].sum(); return np.inf if l == 0 else g[r > 0].sum() / l
def f3(x): return "inf" if np.isinf(x) else f"{x:.3f}"
def row(lab, T):
    r = T.ret.values
    if len(r) < 10: return f"| {lab} | {len(r)} | — | — | — | — | — | — |"
    e, lt = T.yr <= 2014, T.yr >= 2015
    return (f"| {lab} | {len(r):,} | {100 * (r > 0).mean():.1f} % | {f3(pf(r, False))} | **{f3(pf(r))}** | {100 * r.mean():+.2f} % | "
            f"{100 * np.median(r):+.2f} % | {f3(pf(T.ret.values[e.values]))} / {f3(pf(T.ret.values[lt.values]))} |")
H = "| book | trips | win | PF raw | PF clip | mean | median | clip 2005–14 / 2015–26 |\n|---|---|---|---|---|---|---|---|"

print("# Run 31 — HighFlyerV2 on causal data (scripts/equity/highflyer_v2_causal.py)\n")
print("Entries 2005-01 .. 2026-05-13, 5-day hold, no costs, +50 % clip. The back books = the engine's CSVs exactly.\n")
print("## 1. The books, back-adjusted vs causal\n"); print(H)
for name, lab in (("partial_back", "10:00 entry, back-adjusted (the engine)"), ("partial_causal", "**10:00 entry, causal**"),
                  ("close_back", "close entry, back-adjusted"), ("close_causal", "**close entry, causal**")):
    T = B[name]
    print(row(lab, T)); print(row(lab + " · breadth > 0.5", T[T.b1 > 0.5]))
print()
print("## 2. The disproportion test (10:00 book): what the causal scale changes\n")
P, C = B["partial_back"], B["partial_causal"]
k = ["symbol", "entry_date"]
m = P.merge(C, on=k, how="outer", suffixes=("_b", "_c"), indicator=True)
dropped, added, both = m[m._merge == "left_only"], m[m._merge == "right_only"], m[m._merge == "both"]
print(f"back {len(P):,} · causal {len(C):,} · shared {len(both):,} · dropped {len(dropped):,} · added {len(added):,} "
      f"({100 * (len(dropped) + len(added)) / len(P):.1f} % of the book changes)\n")
# the leaks' signature: a LATER split (the ticker's last n vs n at the entry) — a later reverse split inflates the engine's
# rvol k-fold (and lifts the back-adjusted price over $1); a later forward split deflates it
nl = con.execute("SELECT ticker symbol, arg_max(n, date) n_last FROM db.daily_adjusted GROUP BY 1").df()
def later(T):
    x = T[["symbol", "n_d"]].merge(nl, on="symbol", how="left"); q = x.n_last / x.n_d
    return int((q < 0.999).sum()), int((q > 1.001).sum())
print("| cohort | trips | PF raw | PF clip | mean | a later reverse / forward split | raw 10:00 close < \\$1 |\n|---|---|---|---|---|---|---|")
for lab, T, sfx in (("dropped (back only)", dropped, "_b"), ("added (causal only)", added, "_c"), ("shared", both, "_c")):
    r = T["ret" + sfx].values; T2 = T.rename(columns={"n_d" + sfx: "n_d"}) if ("n_d" + sfx) in T.columns else T
    rv, fw = later(T2)
    print(f"| {lab} | {len(r):,} | {f3(pf(r, False))} | {f3(pf(r))} | {100 * np.nanmean(r):+.2f} % | {rv} / {fw} | {int((T['raw_close_1000' + sfx] < 1).sum())} |")
print(f"\nshared trips: back and causal returns differ on {int(((both.ret_b - both.ret_c).abs() > 1e-6).sum())} (dividends; max "
      f"{100 * (both.ret_b - both.ret_c).abs().max():.2f} pp)")
print()
print("## 3. Low float (< \\$300M at the 10:00 price, causal float) and A+\n")
print(f"float coverage: 10:00 back {100 * B['partial_back'].dollar_float.notna().mean():.0f} %, causal {100 * B['partial_causal'].dollar_float.notna().mean():.0f} % "
      f"(2013+: {100 * B['partial_causal'][B['partial_causal'].yr >= 2013].dollar_float.notna().mean():.0f} %)\n")
print(H)
for name, lab in (("partial_back", "10:00 back"), ("partial_causal", "10:00 causal"), ("close_back", "close back"), ("close_causal", "close causal")):
    T = B[name]
    print(row(f"{lab} · low float", T[T.lowf == True]))
    print(row(f"{lab} · float ≥ \\$300M", T[T.dollar_float >= 300e6]))
    if name.startswith("partial"):
        print(row(f"{lab} · low float · RED 10:00 candle", T[(T.lowf == True) & (T.raw_close_1000 < T.raw_open_1000)]))
        print(row(f"{lab} · **A+** (low float · red, −1..−6 % from the open)", T[(T.lowf == True) & T.red_dip]))
        print(row(f"{lab} · A+ · breadth > 0.5", T[(T.lowf == True) & T.red_dip & (T.b1 > 0.5)]))
print()
print("## 4. Timing with the selection held fixed (Run 2, causal): the trips BOTH books take\n")
S = B["partial_causal"].merge(B["close_causal"], on=k, suffixes=("_p", "_c"))
print("| entry | shared trips | win | PF raw | PF clip | mean |\n|---|---|---|---|---|---|")
for lab, col in (("10:00", "ret_p"), ("close", "ret_c")):
    r = S[col].values
    print(f"| {lab} | {len(r):,} | {100 * (r > 0).mean():.1f} % | {f3(pf(r, False))} | {f3(pf(r))} | {100 * r.mean():+.2f} % |")
print(f"\n10:00 fill vs the close, the same trips: median {100 * np.median(S.entry_price_p / S.entry_price_c - 1):+.2f} %, "
      f"mean {100 * np.mean(S.entry_price_p / S.entry_price_c - 1):+.2f} %")
print("\n## 5. By year, causal (clip PF · trips)\n")
yrs = sorted(B["partial_causal"].yr.unique())
print("| book | " + " | ".join(str(y) for y in yrs) + " |\n|---|" + "---|" * len(yrs))
for lab, T in (("10:00", B["partial_causal"]), ("10:00 low float", B["partial_causal"][B["partial_causal"].lowf == True]),
               ("10:00 A+", B["partial_causal"][(B["partial_causal"].lowf == True) & B["partial_causal"].red_dip]), ("close", B["close_causal"]),
               ("close low float", B["close_causal"][B["close_causal"].lowf == True])):
    cells = []
    for y in yrs:
        r = T[T.yr == y].ret.values
        cells.append(f"{f3(pf(r))[:4]} · {len(r)}" if len(r) >= 5 else (f"— · {len(r)}" if len(r) else "—"))
    print(f"| {lab} | " + " | ".join(cells) + " |")
