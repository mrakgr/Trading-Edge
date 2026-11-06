"""Run 40 — HighFlyerV2 entered EARLIER: 08:30 / 09:00 (PREMARKET, user: "an hour before the open or half an hour"),
09:31 ("at the open") and 09:45 instead of 10:00 — and LATER, 15:00 / 15:30 (user: "30 or 60m before the close";
rvol >= 1.0 and the close entry's 5 scaled to the volume in by then, 82 % / 87.5 %: 4.1 / 4.4) (user 2026-11-06: "I wonder if 2026
started losing because people aping Qullamaggie's breakout strategy? I wonder if entering earlier, like 9:45 or even at the
open would do better? Also ... does the enter at the close strategy also lose in 2026?").
Books (highflyer_v2_port.py --scale causal, through 2026-10-30, the partial candles of build_partial_candles_multi.py):
  c1000_rv1.0   the CONTROL: the rebuilt 10:00 candles must reproduce v2port_partial_causal_to1030 (Run 34's) exactly
  c0945 / c0931 the same rules decided at the cutoff: the move, the fade and rvol on the candle to the cutoff, the fill at
                its close; rvol >= 1.0 (stricter: less volume is in) and scaled to the 10:00 book's median volume fraction
                by the cutoff (09:45 0.67, 09:31 0.16)
  close         the close entry (v2port_close_causal_to1030, rvol >= 5 on the daily bar)
Plus THE COST OF WAITING (descriptive — selected with 10:00 information): the 10:00 book's own trips priced from the 09:31 /
09:45 candle's close: (1 + ret) x close_1000 / close_cut − 1.
PF = clip PF (winners at +50 %); the proxy book = dv60_med < $2M and CS (Run 32).
    cd research && python3 scripts/equity/highflyer_v2_earlier.py > data/equity/highflyer_repro/v2_earlier.md
"""
import duckdb, numpy as np, pandas as pd
import data_v2

R = "data/equity/highflyer_repro/"
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='3GB'")
K = ['symbol', 'entry_date']


def load(f):
    T = pd.read_parquet(R + f); T['entry_date'] = pd.to_datetime(T.entry_date); return T


def enrich(T):
    con.register('t', T[K].drop_duplicates())
    M = con.execute("""WITH s AS (SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i
                                  FROM daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
      SELECT t.symbol, t.entry_date, median(p.dv) dv60_med FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
      JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
    M['entry_date'] = pd.to_datetime(M.entry_date)
    ty = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()
    T = T.merge(M, on=K, how='left').merge(ty, on='symbol', how='left')
    T['proxy'] = (T.dv60_med < 2e6) & (T.typ == 'CS'); T['yr'] = T.entry_date.dt.year
    return T


def pf(r):
    r = np.asarray(r, float); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, .5)[r > 0].sum() / l


def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"
def cell(S, col='ret'): return f"{len(S)} · {f2(pf(S[col]))} · {100 * S[col].mean():+.1f} %" if len(S) else "—"


ref = load("v2port_partial_causal_to1030.parquet"); ctl = load("v2port_partial_causal_c1000_rv1.0.parquet")
m = ref.merge(ctl, on=K, how='outer', suffixes=('_r', '_c'), indicator=True)
both = m[m._merge == 'both']
print("# Run 40 — HighFlyerV2 entered earlier (scripts/equity/highflyer_v2_earlier.py)\n")
print(f"CONTROL: the rebuilt 10:00 candles -> {len(ctl):,} trips vs Run 34's {len(ref):,}; only one side: {int((m._merge != 'both').sum())}; "
      f"equal returns {int(np.isclose(both.ret_r, both.ret_c, atol=1e-12, equal_nan=True).sum()):,} of {len(both):,}\n")
books = {"08:30 premarket, rvol >= 0.017": load("v2port_partial_causal_c0830_rv0.017.parquet"),
         "09:00 premarket, rvol >= 0.036": load("v2port_partial_causal_c0900_rv0.036.parquet"),
         "09:00 premarket, rvol >= 0.25": load("v2port_partial_causal_c0900_rv0.25.parquet"),
         "10:00 (production)": ref, "09:45, rvol >= 1.0": load("v2port_partial_causal_c0945_rv1.0.parquet"),
         "09:45, rvol >= 0.67": load("v2port_partial_causal_c0945_rv0.67.parquet"),
         "09:31, rvol >= 1.0": load("v2port_partial_causal_c0931_rv1.0.parquet"),
         "09:31, rvol >= 0.16": load("v2port_partial_causal_c0931_rv0.16.parquet"),
         "15:00, rvol >= 1.0": load("v2port_partial_causal_c1500_rv1.0.parquet"),
         "15:00, rvol >= 4.1": load("v2port_partial_causal_c1500_rv4.1.parquet"),
         "15:30, rvol >= 1.0": load("v2port_partial_causal_c1530_rv1.0.parquet"),
         "15:30, rvol >= 4.4": load("v2port_partial_causal_c1530_rv4.4.parquet"),
         "the close (rvol >= 5)": load("v2port_close_causal_to1030.parquet")}
B = {k: enrich(v[v.open == 0].copy()) for k, v in books.items()}
for lab, sel in (("all", lambda T: T), ("the proxy book", lambda T: T[T.proxy])):
    print(f"## {lab} — trips · PF clip · mean\n")
    print("| book | 2005–2015 | 2016–2025 | 2026 Jan–Oct | 2026 Jan–May 13 | 2026 May 14–Oct |\n|---|---|---|---|---|---|")
    for k, T in B.items():
        T = sel(T)
        print(f"| {k} | {cell(T[T.yr <= 2015])} | {cell(T[T.yr.between(2016, 2025)])} | {cell(T[T.yr == 2026])} | "
              f"{cell(T[(T.yr == 2026) & (T.entry_date <= '2026-05-13')])} | {cell(T[(T.yr == 2026) & (T.entry_date > '2026-05-13')])} |")
    print()
print("## by year, all trips — PF clip (trips)\n")
print("| year | " + " | ".join(B) + " |\n|---|" + "---|" * len(B))
for y in range(2016, 2027):
    print(f"| {y} | " + " | ".join(f"{f2(pf(T[T.yr == y].ret))} ({int((T.yr == y).sum())})" for T in B.values()) + " |")
# overlap of the earlier books with the 10:00 book
for k in ("08:30 premarket, rvol >= 0.017", "09:00 premarket, rvol >= 0.036", "09:45, rvol >= 0.67", "09:31, rvol >= 0.16"):
    T = B[k]; o = T.merge(B["10:00 (production)"][K], on=K, how='inner')
    print(f"\n{k}: {len(T):,} trips, {len(o):,} ({100 * len(o) / len(T):.0f} %) also in the 10:00 book")

# the cost of waiting
P = B["10:00 (production)"].copy()
for c in ("0830", "0900", "0931", "0945"):
    con.register('p', P[['symbol', 'entry_date']])
    X = con.execute(f"""SELECT p.symbol, p.entry_date, c.close AS close_{c} FROM p JOIN
        read_parquet('{R}partial_candles/{c}/*.parquet') c ON c.ticker = p.symbol AND c.date = CAST(p.entry_date AS DATE)""").df()
    X['entry_date'] = pd.to_datetime(X.entry_date); P = P.merge(X, on=K, how='left')
    P[f'ret_{c}'] = (1 + P.ret) * P.raw_close_1000 / P[f'close_{c}'] - 1
print("\n## the COST OF WAITING: the 10:00 book's trips priced from earlier in the same morning (selected with 10:00 information)\n")
print("(the premarket columns: the trips that traded by then — a trip with no premarket print has no price to start from)\n")
print("| year | from 08:30 | from 09:00 | from 09:31 | from 09:45 | from 10:00 | median move 08:30 -> 10:00 | 09:31 -> 10:00 | 09:45 -> 10:00 |\n|---|---|---|---|---|---|---|---|---|")
for y in list(range(2016, 2027)) + ['2016–2025']:
    G = P[P.yr.between(2016, 2025)] if y == '2016–2025' else P[P.yr == y]
    g8, g9 = G[G.close_0830.notna()], G[G.close_0900.notna()]
    print(f"| {y} | {cell(g8, 'ret_0830')} | {cell(g9, 'ret_0900')} | {cell(G, 'ret_0931')} | {cell(G, 'ret_0945')} | {cell(G)} | "
          f"{100 * (g8.raw_close_1000 / g8.close_0830 - 1).median():+.2f} % | {100 * (G.raw_close_1000 / G.close_0931 - 1).median():+.2f} % | "
          f"{100 * (G.raw_close_1000 / G.close_0945 - 1).median():+.2f} % |")
