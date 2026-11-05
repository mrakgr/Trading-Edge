"""GF-64 — THE GAP SHORT COLLECTOR'S THRESHOLDS (user 2026-11-05: "What should the minimum 60d median volume be and what
should the premarket rvol and first 30m rvol be?"). The collector asks the borrow desk about heavy-volume names, twice:
  09:25   premarket volume / vol60_med (rvol_pm60) — volume only, either gap direction (the low-volume gap-ups are out:
          their imbalance data competes with the live GapFader's IBKR lines)
  10:00   09:30–10:00 volume / vol60_med (rvol30_60)
vol60_med / dv60_med = the median of the 60 prior sessions (in D's shares: median(volume / n) · n(D); dollar: close ·
volume), GapFader's definitions (gapfader_model_v2.py) but NOT episode-partitioned (a recycled ticker's old listing can
leak in — small here). ≥ 20 prior sessions required.

The trades scored (no costs, no borrow fee — the collector is what will price the borrow):
  09:25 short at the official open, cover at the official close (census fade_oc, signed for the short)
  10:00 short at the first 1s vwap at/after 10:00 (entry_30), cover at the close; split by the open HELD / FAILED at 10:00
        (px_30 vs the open)
The census (data/equity/gapfader/census.parquet) holds only gaps ≥ 2 % — a heavy-premarket stock that does not gap is
missing (see the live-day share printed first). The 10:00 tapes: gapdown_tape.parquet = every gap-down ≤ −4 %;
tape_persistence.parquet = gap-ups ≥ ~4 % with the 20-day-mean rvol_pm ≥ 0.2 only (GF-10's sample).
    cd research && python3 scripts/equity/gapshort_thresholds.py > data/equity/gapfader/gapshort_thresholds.md
"""
import duckdb, numpy as np, pandas as pd

con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
# the census rows worth scoring (open >= $1, 2016-08+), and their 60-session medians from a join on the prior sessions
con.execute("""CREATE TEMP TABLE c AS SELECT ticker, CAST(date AS DATE) d, gap, open, close, pm_vol, vol_30m, fade_oc, early_close
               FROM 'data/equity/gapfader/census.parquet' WHERE open >= 1 AND NOT early_close AND close IS NOT NULL""")
con.execute("""CREATE TEMP TABLE s AS SELECT ticker, date d, volume / n AS vn, close * volume AS dv, n,
               row_number() OVER (PARTITION BY ticker ORDER BY date) i FROM 'data/v2/daily_adjusted.parquet'
               WHERE ticker IN (SELECT DISTINCT ticker FROM c)""")
con.execute("""CREATE TEMP TABLE m AS
    SELECT c.ticker, c.d, median(p.vn) * any_value(x.n) AS vol60, median(p.dv) AS dv60, count(*) k
    FROM c JOIN s x ON x.ticker = c.ticker AND x.d = c.d
    JOIN s p ON p.ticker = c.ticker AND p.i BETWEEN x.i - 60 AND x.i - 1
    GROUP BY c.ticker, c.d""")
C = con.execute("""SELECT c.*, m.vol60, m.dv60, m.k, c.pm_vol / m.vol60 AS rvpm, c.vol_30m / m.vol60 AS rv30
                   FROM c JOIN m USING (ticker, d) WHERE m.k >= 20 AND m.vol60 > 0""").df()
C['yr'] = pd.to_datetime(C.d).dt.year
NY = (pd.to_datetime(C.d).max() - pd.to_datetime(C.d).min()).days / 365.25

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'
def cell(r, yr):
    if len(r) < 30: return f'— ({len(r)})'
    a, b = pf(r[yr <= 2020]), pf(r[yr >= 2021])
    return f'{f2(pf(r))} · {100 * np.nanmean(r):+.2f} % · {len(r) / NY:.0f}/yr ({f2(a)} / {f2(b)})'

FLOORS = [0, 2.5e5, 5e5, 1e6, 5e6]
print('# GF-64 — the gap short collector\'s thresholds (rvol on the 60-session MEDIAN volume)\n')
print(f'Census rows scored: {len(C):,} (gaps ≥ 2 %, open ≥ \\$1, ≥ 20 prior sessions), {NY:.1f} years. Cells: PF · mean · '
      'trades a year (PF 2016–20 / 2021–26), gross, no borrow fee.\n')

print('## 09:25 — premarket rvol, short the open → the close (either gap direction, |gap| ≥ 2 %)\n')
BANDS = [(0.1, 0.25), (0.25, 0.5), (0.5, 1), (1, 2), (2, np.inf)]
for side, sel in (('gap-UP (fade)', C.gap > 0), ('gap-DOWN (follow)', C.gap < 0)):
    print(f'### {side}\n')
    print('| dv60_med floor | ' + ' | '.join(f'rvol_pm {lo:g}–{hi:g}' for lo, hi in BANDS) + ' | **rvol_pm ≥ 0.1** | **≥ 0.25** | **≥ 0.5** |')
    print('|---|' + '---|' * (len(BANDS) + 3))
    for fl in FLOORS:
        K = C[sel & (C.dv60 >= fl)]
        r, yr = K.fade_oc.values if side.startswith('gap-UP') else -K.fade_oc.values, K.yr.values
        cells = [cell(r[(K.rvpm >= lo).values & (K.rvpm < hi).values], yr[(K.rvpm >= lo).values & (K.rvpm < hi).values]) for lo, hi in BANDS]
        cum = [cell(r[(K.rvpm >= t).values], yr[(K.rvpm >= t).values]) for t in (0.1, 0.25, 0.5)]
        print(f'| \\${fl:,.0f} | ' + ' | '.join(cells + cum) + ' |')
    print()

print('## 10:00 — first-30-minute rvol, short at the first 1s vwap at/after 10:00 → the close\n')
D = C.merge(con.execute("SELECT ticker, CAST(date AS DATE) d, px_30, entry_30 FROM 'data/equity/gapfader/gapdown_tape.parquet'").df(), on=['ticker', 'd'])
U = C.merge(con.execute("SELECT ticker, CAST(date AS DATE) d, px_30, entry_30 FROM 'data/equity/gapfader/tape_persistence.parquet'").df(), on=['ticker', 'd'])
B30 = [(0, 0.25), (0.25, 0.5), (0.5, 1), (1, 2), (2, np.inf)]
for name, T in (('gap-DOWN ≤ −4 % (every rvol_pm)', D[D.gap <= -0.04]), ('gap-UP ≥ 4 % (only rvol_pm ≥ 0.2 on the 20-day mean: GF-10\'s sample)', U[U.gap >= 0.04])):
    T = T[np.isfinite(T.entry_30) & np.isfinite(T.px_30) & (T.entry_30 > 0)].copy()
    T['s'] = 1 - T.close / T.entry_30
    print(f'### {name}\n')
    print('| dv60_med floor | open at 10:00 | ' + ' | '.join(f'rvol30 {lo:g}–{hi:g}' for lo, hi in B30) + ' | **rvol30 ≥ 0.5** | **≥ 1** |')
    print('|---|---|' + '---|' * (len(B30) + 2))
    for fl in (0, 5e5, 1e6, 5e6):
        for lab, w in (('failed', lambda K: K.px_30 < K.open), ('held', lambda K: K.px_30 >= K.open), ('all', lambda K: K.px_30 == K.px_30)):
            K = T[(T.dv60 >= fl) & w(T)]
            r, yr = K.s.values, K.yr.values
            cells = [cell(r[(K.rv30 >= lo).values & (K.rv30 < hi).values], yr[(K.rv30 >= lo).values & (K.rv30 < hi).values]) for lo, hi in B30]
            cum = [cell(r[(K.rv30 >= t).values], yr[(K.rv30 >= t).values]) for t in (0.5, 1)]
            print(f'| \\${fl:,.0f} | {lab} | ' + ' | '.join(cells + cum) + ' |')
    print()
