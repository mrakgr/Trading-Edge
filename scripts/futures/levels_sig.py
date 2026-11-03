"""LV-3 — the key level's SIGNIFICANCE (user 2026-11-03): the champion's priority against the session's typical one.
  sig  = level_vol / typ_med   (the level's own 5 s volume EWMA ÷ the session-to-date median priority)
  sigN = level_now / typ_med   (the same, decayed to the signal: what the level is worth now)
Deciles with edges from the FIT years only (2015–22), PF raw fit → holdout and net of one tick per trip.
  cd research && python -u scripts/futures/levels_sig.py data/futures/levels/run_lv3 > data/futures/levels/report_lv3.md
"""
import sys, duckdb, numpy as np
run = sys.argv[1]
con = duckdb.connect(); con.execute("set memory_limit='3GB'; set threads=4")
con.execute(f"""CREATE TEMP TABLE t AS SELECT root, year(td) <= 2022 fit, side, level_vol / typ_med sig, level_now / typ_med sign_,
    1e4 * side * (exit_px - entry_px) / abs(entry_px) r, 1e4 * side * (exit_px - entry_px) / abs(entry_px) - 1e4 * tick / abs(entry_px) rn,
    side * (exit_px - entry_px) / tick tk FROM read_parquet('{run}/legs/*.parquet') WHERE entry_px <> 0 AND typ_med > 0""")
def pf(c, f): return f"sum(greatest({c},0)) FILTER ({f}) / nullif(sum(greatest(-({c}),0)) FILTER ({f}), 0)"
def ff(x): return '—' if x is None else f'{x:.2f}'
print('# LV-3 — the significance of the key level (1 min bias)\n')
for col, lab in [('sig', 'level_vol / typ_med'), ('sign_', 'level_now / typ_med (decayed to the signal)')]:
    S = con.execute(f"SELECT {col} FROM t WHERE fit USING SAMPLE 200000 ROWS (reservoir, 7)").fetchnumpy()[col]
    e = np.quantile(S, np.linspace(0, 1, 11)); e[0], e[-1] = -np.inf, np.inf
    print(f'## {lab}: deciles of the fit\n\n| decile | range | PF raw fit → holdout | holdout −1t | holdout trips | holdout mean ticks |\n|---|---|---|---|---|---|')
    for k in range(10):
        flt = f'{col} > {e[k]} AND {col} <= {e[k + 1]}' if np.isfinite(e[k]) else f'{col} <= {e[k + 1]}'
        if not np.isfinite(e[k + 1]): flt = f'{col} > {e[k]}'
        q = con.execute(f"SELECT {pf('r', f'fit AND {flt}')}, {pf('r', f'NOT fit AND {flt}')}, {pf('rn', f'NOT fit AND {flt}')}, count(*) FILTER (NOT fit AND {flt}), avg(tk) FILTER (NOT fit AND {flt}) FROM t").fetchone()
        rng = f"{e[k]:.1f}–{e[k + 1]:.1f}".replace('-inf', '0').replace('inf', '∞')
        print(f'| D{k + 1} | {rng} | {ff(q[0])} → **{ff(q[1])}** | {ff(q[2])} | {q[3]:,} | {q[4] or 0:+.2f} |')
    print()
