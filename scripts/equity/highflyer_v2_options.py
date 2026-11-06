"""Run 37 — HighFlyerV2 split by whether the stock HAS LISTED OPTIONS (user 2026-11-06: "Yesterday we found out that it's
dead in 2026, so I am wondering if splitting the stocks into those that have option chains and those that don't would
make a difference" — after SpringFlyer S12: the no-chain names are the far better SHORTS, in every stratum).
⚠ HighFlyerV2 is a LONG (10:00 entry, ~5-session hold): the short-sale-constraint reading predicts the OPPOSITE sign here
(no options = more overpriced = worse to buy).
Book: Run 34's causal 10:00 port through 2026-10-30 (v2port_partial_causal_to1030.parquet), closed trips from 2016-01-05;
the proxy book = dv60_med < $2M and CS (Run 32). has_chain = ThetaData returned call rows on the PREVIOUS session's EOD
(causal for a 10:00 entry; options STANDARD history starts 2016-01-04). PF = Run 34's clip PF (winners clipped at +50 %).
Needs the Theta Terminal running (127.0.0.1:25503).
    cd research && python3 -u scripts/equity/highflyer_v2_options.py > data/equity/highflyer_repro/v2_options.md
writes data/equity/highflyer_repro/v2_options.parquet
"""
import concurrent.futures as cf, json, sys, urllib.error, urllib.parse, urllib.request
import duckdb, numpy as np, pandas as pd
import data_v2

BASE = 'http://127.0.0.1:25503/v3/option'


def log(*a):
    print(*a, file=sys.stderr, flush=True)


T = pd.read_parquet("data/equity/highflyer_repro/v2port_partial_causal_to1030.parquet")
T["entry_date"] = pd.to_datetime(T.entry_date)
T = T[(T.open == 0) & (T.entry_date >= "2016-01-05")].reset_index(drop=True)
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='4GB'")
con.register("t", T[["symbol", "entry_date"]].drop_duplicates())
M = con.execute("""
  WITH s AS (SELECT ticker, date, close * volume dv, LAG(date) OVER (PARTITION BY ticker ORDER BY date) prev_date,
                    row_number() OVER (PARTITION BY ticker ORDER BY date) i
             FROM daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
  SELECT t.symbol, t.entry_date, any_value(x.prev_date) prev_date, median(p.dv) dv60_med
  FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
  JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
M["entry_date"] = pd.to_datetime(M.entry_date); M["prev_date"] = pd.to_datetime(M.prev_date)
T = T.merge(M, on=["symbol", "entry_date"], how="left")
ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
T = T.merge(ty, on="symbol", how="left")
T["proxy"] = (T.dv60_med < 2e6) & (T.typ == "CS")
T["yr"] = T.entry_date.dt.year

syms = set()
with urllib.request.urlopen(f"{BASE}/list/symbols?format=ndjson", timeout=120) as r:
    syms = {json.loads(l)["symbol"] for l in r.read().decode().splitlines() if l.strip()}


def probe(key):
    sym, day = key
    if sym not in syms or pd.isna(day):
        return key, False
    d = day.strftime("%Y%m%d")
    url = f"{BASE}/history/eod?" + urllib.parse.urlencode(dict(symbol=sym, expiration="*", right="call", start_date=d, end_date=d, format="ndjson"))
    try:
        with urllib.request.urlopen(url, timeout=180) as r:
            return key, any(l.strip() for l in r.read().decode().splitlines())
    except urllib.error.HTTPError:                      # 472 = no data: no chain that day
        return key, False


CACHED = "--cached" in sys.argv                         # reuse the saved has_chain (no API calls)
keys = [] if CACHED else list({(s, d) for s, d in zip(T.symbol, T.prev_date)})
log(f"{len(T)} closed trips 2016-01-05 →, {len(keys)} (symbol, previous session) probes, {sum(k[0] in syms for k in keys)} symbols listed today")
got = {}
with cf.ThreadPoolExecutor(4) as ex:
    for i, (k, v) in enumerate(ex.map(probe, keys), 1):
        got[k] = v
        if i % 100 == 0:
            log(f"  {i} / {len(keys)}")
if CACHED:
    C0 = pd.read_parquet("data/equity/highflyer_repro/v2_options.parquet")[["symbol", "entry_date", "has_chain"]]
    T = T.merge(C0.assign(entry_date=pd.to_datetime(C0.entry_date)), on=["symbol", "entry_date"], how="left")
else:
    T["has_chain"] = [got[(s, d)] for s, d in zip(T.symbol, T.prev_date)]
    T.to_parquet("data/equity/highflyer_repro/v2_options.parquet", index=False)


def pf(r):
    r = np.asarray(r); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, .5)[r > 0].sum() / l


def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
def cell(S): return f"{len(S)} · {f2(pf(S.ret))} · {100 * S.ret.mean():+.1f} %" if len(S) else "—"


print("# Run 37 — HighFlyerV2 by listed options (scripts/equity/highflyer_v2_options.py)\n")
print(f"Closed trips 2016-01-05 → 2026-10-30: {len(T):,}; with a call chain on the previous session: {int(T.has_chain.sum()):,} "
      f"({100 * T.has_chain.mean():.0f} %); proxy book {int(T.proxy.sum())} ({100 * T[T.proxy].has_chain.mean():.0f} % with a chain). "
      "Cells: trips · PF clip · mean.\n")
print("## 1. By year\n\n| year | all: chain | all: no chain | proxy: chain | proxy: no chain |\n|---|---|---|---|---|")
for y, S in T.groupby("yr"):
    P = S[S.proxy]
    print(f"| {y} | {cell(S[S.has_chain])} | {cell(S[~S.has_chain])} | {cell(P[P.has_chain])} | {cell(P[~P.has_chain])} |")
for lab, S in (("**2016–2025**", T[T.yr <= 2025]), ("**2026 Jan–Oct**", T[T.yr == 2026]), ("**all**", T)):
    P = S[S.proxy]
    print(f"| {lab} | {cell(S[S.has_chain])} | {cell(S[~S.has_chain])} | {cell(P[P.has_chain])} | {cell(P[~P.has_chain])} |")

print("\n## 2. Is it a disguised dv60_med / price / type? (all trips; chain | no chain)\n\n| stratum | chain | no chain |\n|---|---|---|")
T["dvb"] = pd.cut(T.dv60_med, [0, 2e6, 1e7, 5e7, 1e13], labels=["dv60 < $2M", "$2-10M", "$10-50M", "> $50M"])
T["pxb"] = pd.cut(T.entry_price, [0, 10, 30, 1e9], labels=["entry < $10", "$10-30", "> $30"])
for col in ("dvb", "pxb", "typ"):
    for k, S in T.groupby(col, observed=True):
        print(f"| {k} | {cell(S[S.has_chain])} | {cell(S[~S.has_chain])} |")

print("\n## 3. Iso-trip control INSIDE dv60 < $2M (where both groups are plentiful): matched on dv60 deciles, 2000 draws\n")
print("(The all-years control matched on the four dv60 bands is BROKEN: 52 no-chain trips above $2M get resampled to stand in "
      "for 1,389 chain trips — a 2026-11-06 first draft did exactly that, p95 2.42 vs 1.88, and was withdrawn.)\n")
rng = np.random.default_rng(37); L = T[T.dv60_med < 2e6].copy()
L["dec"] = pd.qcut(L.dv60_med, 10, labels=False)
for lab, nm, other in (("chain", True, False), ("no chain", False, True)):
    A, B = L[L.has_chain == nm], L[L.has_chain == other]; draws = []
    for _ in range(2000):
        s_ = pd.concat([B[B.dec == d].sample(int((A.dec == d).sum()), replace=True, random_state=rng.integers(1 << 31))
                        for d in range(10) if (A.dec == d).any() and (B.dec == d).any()])
        draws.append(pf(s_.ret))
    draws = np.array(draws)
    print(f"- {lab} trips PF {f2(pf(A.ret))} ({len(A)}); the OTHER group drawn with its dv60-decile mix: p5 {np.quantile(draws, .05):.2f} / "
          f"median {np.median(draws):.2f} / p95 {np.quantile(draws, .95):.2f}")
