"""Run 41 — HighFlyerV2's LATE entries examined (user 2026-11-06): "relax the rvol criteria from 5 to 3 and get more trades ...
reduce the change % criteria a bit as well"; "how much better 15:30 ET is than either 15 or 16 ET ... did it somehow catch a
large trade that the other 2 filters missed?"; "maybe 8% and 3 rvol on low median volume stocks would be better than 10% and
5 rvol on the full book?"
Every late book through the SAME path (highflyer_v2_port.py --entry partial, the candle [09:30, c), volume from 04:00, the fade
gate) — 16:00 included, so the close is compared like for like (the old --entry close path had no fade gate and gated on the
daily bar). rvol in CLOSE-EQUIVALENT units: the gate = k x the close book's median share of the day's volume in by the
cutoff (14:30 .770, 15:00 .819, 15:15 .846, 15:30 .875, 15:45 .914, 16:00 .985), k = 5 (the close entry's) or 3.
  1. the smoothness: 14:30 .. 16:00 at move >= 10 %, k = 5
  2. the 15:30 decomposition: vs the old close book — the trips only one of them takes, and the common trips' two fills
  3. the relaxed gates at 15:00 / 15:30 / 16:00: move >= 8 / 10 %, k = 3 / 5 — the full book and the proxy book
PF = clip PF (winners at +50 %); robustness: PF without the best 5 % of trips; the proxy book = dv60_med < $2M and CS.
    cd research && python3 scripts/equity/highflyer_v2_late.py > data/equity/highflyer_repro/v2_late.md
"""
import duckdb, numpy as np, pandas as pd
import data_v2

R = "data/equity/highflyer_repro/"
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='3GB'")
K = ['symbol', 'entry_date']
FR = {'1430': .770, '1500': .819, '1515': .846, '1530': .875, '1545': .914, '1600': .985}
GATE = {('1430', 5): '3.85', ('1500', 5): '4.1', ('1515', 5): '4.23', ('1530', 5): '4.4', ('1545', 5): '4.57', ('1600', 5): '4.92',
        ('1500', 3): '2.46', ('1530', 3): '2.63', ('1600', 3): '2.95'}
TY = con.execute("SELECT ticker symbol, string_agg(DISTINCT type, '/') typ FROM ticker_reference WHERE type IN ('CS','ADRC') GROUP BY 1").df()


def enrich(T):
    T = T[T.open == 0].copy(); T['entry_date'] = pd.to_datetime(T.entry_date)
    con.register('t', T[K].drop_duplicates())
    M = con.execute("""WITH s AS (SELECT ticker, date, close * volume dv, row_number() OVER (PARTITION BY ticker ORDER BY date) i
                                  FROM daily_adjusted WHERE ticker IN (SELECT DISTINCT symbol FROM t))
      SELECT t.symbol, t.entry_date, median(p.dv) dv60_med FROM t JOIN s x ON x.ticker = t.symbol AND x.date = t.entry_date
      JOIN s p ON p.ticker = t.symbol AND p.i BETWEEN x.i - 60 AND x.i - 1 GROUP BY 1, 2""").df()
    M['entry_date'] = pd.to_datetime(M.entry_date)
    T = T.merge(M, on=K, how='left').merge(TY, on='symbol', how='left')
    T['proxy'] = (T.dv60_med < 2e6) & (T.typ == 'CS'); T['yr'] = T.entry_date.dt.year
    return T


def book(c, up, k):
    return enrich(pd.read_parquet(f"{R}v2port_partial_causal_c{c}_up{up}_rv{GATE[(c, k)]}.parquet"))


def pf(r):
    r = np.asarray(r, float); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    return np.inf if l == 0 else np.minimum(r, .5)[r > 0].sum() / l


def exbest(r):
    r = np.sort(np.asarray(r, float)); return pf(r[:int(len(r) * .95)])


def cell(S): return f"{len(S)} · {pf(S.ret):.2f} · {100 * S.ret.mean():+.1f} %" if len(S) else "—"
def cell2(S): return f"{len(S)} · {pf(S.ret):.2f} (ex best 5 % {exbest(S.ret):.2f})" if len(S) else "—"


PER = (("2005–2015", lambda T: T[T.yr <= 2015]), ("2016–2025", lambda T: T[T.yr.between(2016, 2025)]), ("2026", lambda T: T[T.yr == 2026]))
print("# Run 41 — HighFlyerV2's late entries examined (scripts/equity/highflyer_v2_late.py)\n")
print("## 1. smoothness across the cutoff (move >= 10 %, rvol = the close's 5 in close-equivalent units) — trips · PF clip (ex best 5 %)\n")
for lab, sel in (("all", lambda T: T), ("the proxy book", lambda T: T[T.proxy])):
    print(f"**{lab}**\n\n| cutoff | rvol gate | 2005–2015 | 2016–2025 | 2026 |\n|---|---|---|---|---|")
    for c in FR:
        T = sel(book(c, '0.10', 5))
        print(f"| {c[:2]}:{c[2:]} | {GATE[(c, 5)]} | " + " | ".join(cell2(f(T)) for _, f in PER) + " |")
    print()

print("## 2. the 15:30 book against the OLD close book (--entry close: no fade gate, rvol >= 5 on the daily bar)\n")
A = book('1530', '0.10', 5); C = enrich(pd.read_parquet(R + "v2port_close_causal_to1030.parquet")); N = book('1600', '0.10', 5)
for lab, f in PER[1:]:
    a, c, n = f(A), f(C), f(N)
    m = a.merge(c[K + ['ret']], on=K, how='outer', suffixes=('', '_c'), indicator=True)
    oa, oc, both = m[m._merge == 'left_only'], m[m._merge == 'right_only'], m[m._merge == 'both']
    mn = n.merge(c[K], on=K, how='left', indicator=True)
    print(f"- {lab}: 15:30 {len(a)} trips; in both {len(both)} — 15:30's fill PF {pf(both.ret):.2f} vs the close's {pf(both.ret_c):.2f}; "
          f"ONLY at 15:30 {len(oa)} (PF {pf(oa.ret):.2f}); ONLY in the old close book {len(oc)} (PF {pf(oc.ret_c):.2f}, mean "
          f"{100 * oc.ret_c.mean():+.2f} %). The 16:00 book through the same path: {cell(n)}; its trips the old close book lacks "
          f"{int((mn._merge == 'left_only').sum())}, the old close book's it lacks {len(c) - int((mn._merge == 'both').sum())}")
print()

print("## 3. the relaxed gates (move >= 8 / 10 %, rvol = 3 or 5 close-equivalent) — trips · PF clip · mean\n")
for lab, sel in (("all", lambda T: T), ("the proxy book (dv60_med < $2M, CS)", lambda T: T[T.proxy])):
    print(f"**{lab}**\n\n| cutoff | move | rvol (close-eq.) | 2005–2015 | 2016–2025 | 2026 | 2016–2025 ex best 5 % |\n|---|---|---|---|---|---|---|")
    for c in ('1500', '1530', '1600'):
        for up in ('0.10', '0.08'):
            for k in (5, 3):
                T = sel(book(c, up, k)); m = PER[1][1](T)
                print(f"| {c[:2]}:{c[2:]} | {int(float(up) * 100)} % | {k} ({GATE[(c, k)]}) | " + " | ".join(cell(f(T)) for _, f in PER)
                      + f" | {exbest(m.ret):.2f} |")
    print()
P8 = book('1530', '0.08', 3); F10 = book('1530', '0.10', 5)
for lab, f in PER[1:]:
    p, q = f(P8[P8.proxy]), f(F10)
    print(f"- the user's comparison, {lab}: the proxy book at 8 % / rvol 3 {cell(p)} vs the full book at 10 % / rvol 5 {cell(q)}")
print("\n### the proxy book at 15:30, 8 % / rvol 3, by year\n\n| year | trips · PF · mean |\n|---|---|")
for y, g in P8[P8.proxy & (P8.yr >= 2016)].groupby('yr'):
    print(f"| {y} | {cell(g)} |")
