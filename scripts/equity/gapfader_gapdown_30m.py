"""GapFader GF-12 (user 2026-10-16): the LONG weak-premarket gap-DOWN fade (rvol_pm < 0.02, gap ≤ −4 %, bought at the open) —
at 10:00, do the first-30-min VOLATILITY and TRADE COUNT tell us whether to HOLD to the close or SELL at 10:00?
The decision's value is the 10:00 → close leg: hold where it is positive, sell where it is negative.
Features at 10:00 (raw 1s bars): volat_clk (30 s clock slot vwaps, mean |ln r|, bp), tc30 (trades 09:30–10:00), act30
(active seconds), pos30 = the last 1s vwap before 10:00 / the open − 1. x30 = the first 1s vwap at/after 10:00 (the sale).
Legs: open → x30, x30 → official close; whole = open → close. Headline universe; SPY's gap as GF-3. Equal weight, no costs.
Writes data/equity/gapfader/gapdown_30m.parquet.
Run from research/:  python -u scripts/equity/gapfader_gapdown_30m.py > data/equity/gapfader/gapdown_30m.md 2> data/equity/gapfader/gapdown_30m.log; echo exit $?
"""
import os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
S = duckdb.sql("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker='SPY' WINDOW w AS (ORDER BY date))
   SELECT date::VARCHAR date, open/(pcr*np_/n-(cum_div-cdp)/n)-1 spy_gap FROM d""").df()
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].merge(S, on="date")
OUT = "data/equity/gapfader/gapdown_30m.parquet"
if os.path.exists(OUT):
    V = pd.read_parquet(OUT)
else:
    con = duckdb.connect(); parts = []
    days = sorted(C.date.unique())
    for i, d in enumerate(days):
        tl = ",".join("'" + t.replace("'", "''") + "'" for t in C[C.date == d].ticker)
        V = con.execute(f"""
            WITH b AS (SELECT ticker, bucket, vwap::DOUBLE v, volume::DOUBLE vol, trade_count tc
                       FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                       WHERE ticker IN ({tl}) AND bucket >= 34200 AND bucket < 57600 AND volume > 0),
            w AS (SELECT * FROM b WHERE bucket < 36000),
            c AS (SELECT ticker, (bucket - 34200) // 30 cslot, sum(v * vol) / sum(vol) sv FROM w GROUP BY ALL),
            cr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY cslot))) r FROM c),
            vo AS (SELECT ticker, avg(r) * 1e4 volat_clk FROM cr GROUP BY ticker),
            f AS (SELECT ticker, sum(tc) FILTER (bucket < 36000) tc30, count(*) FILTER (bucket < 36000) act30,
                         arg_max(v, bucket) FILTER (bucket < 36000) px30, arg_min(v, bucket) FILTER (bucket >= 36000) x30
                  FROM b GROUP BY ticker)
            SELECT f.*, vo.volat_clk FROM f LEFT JOIN vo USING (ticker)""").df()
        V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i+1}/{len(days)} {d}")
    V = pd.concat(parts, ignore_index=True); V.to_parquet(OUT); log(f"wrote {OUT}: {len(V):,}")
C = C.merge(V, on=["ticker", "date"], how="inner")
C = C[C.x30.notna() & C.px30.notna()].copy()
C["yr"] = C.date.str[:4]; C["rel"] = C.spy_gap * np.sign(C.gap)
C["whole"] = C.close / C.open - 1; C["leg1"] = C.x30 / C.open - 1; C["leg2"] = C.close / C.x30 - 1
C["pos30"] = C.px30 / C.open - 1
log(f"{len(C):,} gap-downs with a 10:00 price")
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def line(lab, F):
    if len(F) < 30: return f"| {lab} | {len(F)} | | | | | | |"
    yb = sum(pf(g.leg2.values) < 1 for _, g in F.groupby("yr"))
    return (f"| {lab} | {len(F):,} | {F.whole.mean()*100:+.2f} ({pf(F.whole.values):.2f}) | {F.leg1.mean()*100:+.2f} | "
            f"**{F.leg2.mean()*100:+.2f}** | {F.leg2.median()*100:+.2f} | **{pf(F.leg2.values):.2f}** ({pf(F.leg2.clip(upper=0.5).values):.2f}) | {yb}/{F.yr.nunique()} |")
HDR = ("| bucket | n | open → close: mean % (PF) | open → 10:00 mean % | **10:00 → close mean %** | 10:00 → close median % | "
       "**10:00 → close PF** (clip +50 %) | years 10:00→close PF < 1 |")
print("# GF-12 — weak-premarket gap-DOWN longs: hold to the close or sell at 10:00? By the first-30-min tape\n")
print(f"{len(C):,} trades (rvol_pm < 0.02, gap ≤ −4 %, headline universe), {C.date.min()} … {C.date.max()}. The 10:00 → close leg is the "
      "value of HOLDING past 10:00 (> 0: hold; < 0: sell at 10:00). No costs.\n")
for name, P in (("all days", C), ("ex with-market (SPY down ≥ 1 % removed)", C[C.rel < 0.01]), ("with-market only (SPY down ≥ 1 %)", C[C.rel >= 0.01])):
    print(f"## {name} — {len(P):,} trades\n")
    print(HDR); print("|---" * 8 + "|")
    print(line("all", P))
    for feat, lab, fmt in (("volat_clk", "volatility (bp)", "{:.0f}"), ("tc30", "trades 09:30–10:00", "{:.0f}"), ("pos30", "price at 10:00 vs the open", "{:+.1%}")):
        F0 = P[P[feat].notna()]
        q = pd.qcut(F0[feat].rank(method="first"), 5, labels=False)
        for b in range(5):
            F = F0[q == b]
            print(line(f"{lab} Q{b+1} ({fmt.format(F[feat].min())} … {fmt.format(F[feat].max())})", F))
    print()
# volatility × trade count on the ex-with-market set: the 10:00 → close PF
P = C[(C.rel < 0.01) & C.volat_clk.notna()]
qv = pd.qcut(P.volat_clk.rank(method="first"), 3, labels=["low volat", "mid volat", "high volat"])
qt = pd.qcut(P.tc30.rank(method="first"), 3, labels=["few trades", "mid trades", "many trades"])
print("## ex with-market: volatility × trade count (terciles) — 10:00 → close PF / mean % (n)\n")
print("| | few trades | mid trades | many trades |"); print("|---|---|---|---|")
for v in ["low volat", "mid volat", "high volat"]:
    cells = [f"{pf(P[(qv==v)&(qt==t)].leg2.values):.2f} / {P[(qv==v)&(qt==t)].leg2.mean()*100:+.2f} ({((qv==v)&(qt==t)).sum()})" for t in ["few trades", "mid trades", "many trades"]]
    print(f"| {v} ({P[qv==v].volat_clk.min():.0f}–{P[qv==v].volat_clk.max():.0f} bp) | " + " | ".join(cells) + " |")
print()
# price at 10:00 × volatility, ex with-market
qp = pd.qcut(P.pos30.rank(method="first"), 3, labels=["below the open", "near", "above the open"])
print("## ex with-market: price at 10:00 vs the open × volatility (terciles) — 10:00 → close PF / mean % (n)\n")
print("| | low volat | mid volat | high volat |"); print("|---|---|---|---|")
for p in ["below the open", "near", "above the open"]:
    cells = [f"{pf(P[(qp==p)&(qv==v)].leg2.values):.2f} / {P[(qp==p)&(qv==v)].leg2.mean()*100:+.2f} ({((qp==p)&(qv==v)).sum()})" for v in ["low volat", "mid volat", "high volat"]]
    print(f"| {p} ({P[qp==p].pos30.min():+.1%} … {P[qp==p].pos30.max():+.1%}) | " + " | ".join(cells) + " |")
