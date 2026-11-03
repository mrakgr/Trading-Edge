"""LV-4 — every level/band variant side by side: trips/day, % reversals, median hold, PF raw fit → holdout and the
holdout net of one tick per trip, for all roots, the equity group and NQ (the QQQ analogue of the VWAP paper).
  cd research && python -u scripts/futures/levels_summary.py run_lv3 run_lv4_ka05 ... > data/futures/levels/report_lv4.md
"""
import sys, duckdb
con = duckdb.connect(); con.execute("set memory_limit='3GB'; set threads=4")
def pf(c, f): return f"sum(greatest({c},0)) FILTER ({f}) / nullif(sum(greatest(-({c}),0)) FILTER ({f}), 0)"
def ff(x): return '—' if x is None else f'{x:.2f}'
print('| run | trips/day/root | % reversals | median hold min | all: PF fit → holdout | −1t | equity | −1t | NQ | −1t | NQ mean ticks |')
print('|---|---|---|---|---|---|---|---|---|---|---|')
for run in sys.argv[1:]:
    p = f'data/futures/levels/{run}/legs/*.parquet'
    q = con.execute(f"""WITH t AS (SELECT root, td, year(td) <= 2022 fit, reason, exit_t - entry_t hold_s,
            root IN ('ES','MES','NQ') eq, root = 'NQ' nq,
            1e4 * side * (exit_px - entry_px) / abs(entry_px) r, 1e4 * side * (exit_px - entry_px) / abs(entry_px) - 1e4 * tick / abs(entry_px) rn,
            side * (exit_px - entry_px) / tick tk FROM read_parquet('{p}') WHERE entry_px <> 0)
        SELECT count(*) / count(DISTINCT (root, td)), 100 * avg((reason = 'flip')::INT), median(hold_s) / 60.0,
               {pf('r', 'fit')}, {pf('r', 'NOT fit')}, {pf('rn', 'NOT fit')},
               {pf('r', 'fit AND eq')}, {pf('r', 'NOT fit AND eq')}, {pf('rn', 'NOT fit AND eq')},
               {pf('r', 'fit AND nq')}, {pf('r', 'NOT fit AND nq')}, {pf('rn', 'NOT fit AND nq')}, avg(tk) FILTER (NOT fit AND nq) FROM t""").fetchone()
    print(f'| {run} | {q[0]:.1f} | {q[1]:.0f} | {q[2]:.1f} | {ff(q[3])} → **{ff(q[4])}** | {ff(q[5])} | {ff(q[6])} → **{ff(q[7])}** | {ff(q[8])} | '
          f'{ff(q[9])} → **{ff(q[10])}** | {ff(q[11])} | {q[12] or 0:+.2f} |', flush=True)
