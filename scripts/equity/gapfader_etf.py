"""GapFader GF-28 (user 2026-10-17, curiosity — the account's owner cannot trade US ETFs): the gap-down long on ETFs. Do thin
opening auctions dislocate ETFs the way they do common stocks (an ETF is tied to its basket by creation / redemption)?
Universe: ticker_reference type ETF (ETN / ETV / ETS left out), LEVERAGED / INVERSE funds removed by name (user). Gaps as gapfader_census.py (raw open vs the prev close in D's
terms, daily_adjusted), 2016-08-08 … 2026-09-04, gap <= -4 %, open >= $1, dv20_prior >= $1M, no early close. From the 1s
bars: premarket volume (04:00–09:30) -> rvol_pm, active bars 08:30–09:30, and GF-21's exit (75 % of the gap until 10:00 on
a trade-through, else the first 1s vwap at/after 10:00; 0.40c). Leveraged / inverse by the fund's name. Listing = Massive's
reference API (active ETFs today). Cached in data/equity/gapfader/etf.parquet.
Run from research/:  python -u scripts/equity/gapfader_etf.py > data/equity/gapfader/etf.md 2> data/equity/gapfader/etf.log; echo exit $?
"""
import json, os, re, sys, time, urllib.parse, urllib.request
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
OUT = "data/equity/gapfader/etf.parquet"
EARLY = set(pd.read_parquet("data/equity/gapfader/census.parquet", columns=["date", "early_close"]).query("early_close").date)
con = duckdb.connect()
if os.path.exists(OUT):
    C = pd.read_parquet(OUT)
else:
    ref = con.execute("SELECT ticker, any_value(coalesce(name, '')) AS name FROM 'data/v2/ticker_reference.parquet' WHERE type = 'ETF' GROUP BY ticker").df()
    con.register("uni", ref)
    G = con.execute("""
        WITH d AS (SELECT a.ticker, a.date, a.open, a.close, a.n, a.cum_div,
                          lag(a.date) OVER w AS prev_date, lag(a.close) OVER w AS pcr, lag(a.n) OVER w AS np_, lag(a.cum_div) OVER w AS cdp,
                          avg(a.volume / a.n) OVER (w ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) * a.n AS avgvol20_prior,
                          avg(a.volume * a.close) OVER (w ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS dv20_prior,
                          count(*) OVER (w ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS n_prior
                   FROM 'data/v2/daily_adjusted.parquet' a JOIN uni USING (ticker) WINDOW w AS (PARTITION BY a.ticker ORDER BY a.date))
        SELECT ticker, strftime(date, '%Y-%m-%d') AS date, open, close, avgvol20_prior, dv20_prior,
               pcr * np_ / n - (cum_div - cdp) / n AS pc_total
        FROM d WHERE date BETWEEN '2016-08-08' AND '2026-09-04' AND prev_date IS NOT NULL AND date - prev_date <= 4
          AND n_prior = 20 AND dv20_prior >= 1e6 AND open >= 1""").df()
    G["gap"] = G.open / G.pc_total - 1
    G = G[(G.gap <= -0.04) & ~G.date.isin(EARLY)].merge(ref, on="ticker")
    log(f"{len(G):,} ETF gap-downs >= 4 %")
    parts = []
    for i, d in enumerate(sorted(G.date.unique())):
        path = f"data/intraday_1s_slim/{d}.parquet"
        if not os.path.exists(path): continue
        gd = G[G.date == d][["ticker", "open", "pc_total"]]
        con.register("gd", gd)
        V = con.execute(f"""SELECT s.ticker, sum(volume::DOUBLE) FILTER (bucket < 34200) AS pm_vol,
                   count(*) FILTER (bucket >= 30600 AND bucket < 34200) AS bars_60,
                   min(bucket) FILTER (bucket >= 34200 AND v > gd.open + 0.75 * (gd.pc_total - gd.open)) AS tf_75,
                   arg_min(v, bucket) FILTER (bucket >= 36000) AS x_36000
            FROM (SELECT ticker, bucket, vwap::DOUBLE v, volume FROM read_parquet('{path}')
                  WHERE ticker IN (SELECT ticker FROM gd) AND bucket < 57600 AND volume > 0) s
            JOIN gd ON gd.ticker = s.ticker GROUP BY s.ticker""").df()
        con.unregister("gd"); V["date"] = d; parts.append(V)
        if i % 400 == 0: log(f"{i + 1} {d}")
    C = G.merge(pd.concat(parts, ignore_index=True), on=["ticker", "date"], how="inner")
    C.to_parquet(OUT, index=False); log(f"wrote {OUT}: {len(C):,}")

C = C[C.x_36000.notna()].copy()
C["pm_vol"] = C.pm_vol.fillna(0); C["bars_60"] = C.bars_60.fillna(0)
C["rvol_pm"] = C.pm_vol / C.avgvol20_prior
C["tf_75"] = C.tf_75.astype("float64").fillna(np.inf)
C["r"] = np.where(C.tf_75 < 36000, C.open + 0.75 * (C.pc_total - C.open), C.x_36000) / C.open - 1 - 0.004 / C.open
C["yr"] = C.date.str[:4]
# LEVERAGED / INVERSE funds are OUT (user 2026-10-17): by the name — 2X / 3X / -1X / 1.5X, Ultra, Leveraged, Inverse, Bear,
# Bull, Short (not a short-DURATION bond fund), Roundhill WeeklyPay (1.2x), YieldBOOST (options on leveraged funds), Daily Target
LEV = re.compile(r"\b-?[123](?:\.\d+)?x\b|ultra|leverag|inverse|\bbear\b|\bbull\b|weeklypay|yieldboost|daily target"
                 r"|\bshort\b(?!.{0,12}(?:term|duration|maturity|high yield|muni|treasury bond|corporate))", re.I)
C["lev"] = C.name.str.contains(LEV)
NLEV = (~C.lev).sum()
print_lev = f"Leveraged / inverse funds REMOVED: {C.lev.sum():,} of {len(C):,} gaps ({C[C.lev].ticker.nunique():,} funds); {NLEV:,} gaps left in {C[~C.lev].ticker.nunique():,} plain funds."
C = C[~C.lev].copy()
S = con.execute("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker = 'SPY' WINDOW w AS (ORDER BY date))
   SELECT strftime(date, '%Y-%m-%d') date, open / (pcr * np_ / n - (cum_div - cdp) / n) - 1 AS spy_gap FROM d""").df()
C = C.merge(S, on="date", how="left")
k = json.load(open(os.path.expanduser("~/.config/massive/credentials")))["massive_api_key"]
rows, url = [], "https://api.massive.com/v3/reference/tickers?" + urllib.parse.urlencode({"market": "stocks", "type": "ETF", "active": "true", "limit": 1000})
while url:
    j = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={"Authorization": "Bearer " + k})))
    rows += [(r["ticker"], r.get("primary_exchange")) for r in j.get("results", [])]; url = j.get("next_url")
C["ex"] = C.ticker.map(dict(rows)).fillna("not active now")
def pf(v):
    v = v[np.isfinite(v)]; g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
EX = C.yr != "2020"; yrs = C.yr.nunique()
def table(title, groups):
    print(f"## {title}\n\n| | trades / yr | PF−1 | avg % | PF−1 ex-2020 | avg % ex-2020 | median % ex-2020 | PF clip ex-2020 |\n|---|---|---|---|---|---|---|---|")
    for nm, m in groups:
        x = m & EX
        print(f"| {nm} | {m.sum() / yrs:,.0f} | {pf(C.r[m]) - 1:.2f} | {C.r[m].mean():+.2%} | **{pf(C.r[x]) - 1:.2f}** | {C.r[x].mean():+.2%} | {C.r[x].median():+.2%} | {pf(C.r[x].clip(-.5, .5)):.2f} |")
    print()
W = C.rvol_pm < 0.02
print(f"# GF-28 — the gap-down long on PLAIN ETFs ({len(C):,} ETF gap-downs ≥ 4 %, {C.ticker.nunique():,} funds, {yrs} years)\n")
print(print_lev + f" SPY's own gap ≤ −1 % on {(C.spy_gap <= -0.01).mean():.0%} of them.\n")
table("by premarket volume", [("all", C.r.notna()), ("rvol_pm < 0.005", C.rvol_pm < 0.005), ("0.005–0.02", (C.rvol_pm >= 0.005) & W.eq(True)), ("0.02–0.2", (C.rvol_pm >= 0.02) & (C.rvol_pm < 0.2)), ("≥ 0.2", C.rvol_pm >= 0.2)])
table("WEAK premarket (rvol < 0.02) — the trade", [("all", W),
      ("SPY gap > −1 % (the fund's own gap)", W & (C.spy_gap > -0.01)), ("SPY gap ≤ −1 % (the market's)", W & (C.spy_gap <= -0.01)),
      ("0 premarket bars 08:30–09:30", W & (C.bars_60 == 0)), ("1–9 bars", W & (C.bars_60 >= 1) & (C.bars_60 < 10)), ("≥ 10 bars", W & (C.bars_60 >= 10))])
table("WEAK premarket, by listing (active funds today)", [(e, W & (C.ex == e)) for e in C.ex.value_counts().index[:5]])
print("## WEAK premarket by year\n\n| year | trades | PF−1 | avg % |\n|---|---|---|---|")
for y in sorted(C.yr.unique()):
    m = W & (C.yr == y); print(f"| {y} | {m.sum():,} | {pf(C.r[m]) - 1:.2f} | {C.r[m].mean():+.2%} |")
log("done")
