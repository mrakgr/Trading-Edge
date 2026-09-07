#!/usr/bin/env python3
"""LongHiker SHAKEOUT study (user thesis, 2026-09-06): the market breaks BELOW the
range to shake out weak hands, then promptly makes new highs. The sampler fires on
every strict new SESSION high; the features are anchored on the last new
{20,30,40,60}m low: how many session highs since it (k), how long ago (t), how
far below the standing high it went (depth), how far above it we now are (dist).

Step 1 builds a slim study slice (DuckDB, streaming) from the trip corpus.
Step 2 prints the tables: every cell is replayed mc=1 (greedy non-overlap on
entry_sec/exit_sec INSIDE the gate — the default of the three-mc rule), then
equal-weighted by ticker-day. Long side only.

  python3 scripts/equity/longhiker_shake_study.py --corpus data/longhiker_trips_shake --slice data/longhiker_study_shake.parquet
"""
import argparse, os, sys, time
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument('--corpus', default='data/longhiker_trips_shake')
ap.add_argument('--slice', default='data/longhiker_study_shake.parquet')
ap.add_argument('--rebuild', action='store_true')
ap.add_argument('--exit', default='ts30', help='primary exit: ts30|ts60|ts120|fwd300|fwd600|fwd1200')
args = ap.parse_args()

con = duckdb.connect()
con.execute("SET memory_limit='5GB'; SET threads=8; SET preserve_insertion_order=false")

SHK = ['20m','30m','40m','60m']
if args.rebuild or not os.path.exists(args.slice):
    t0 = time.time()
    shk_cols = ',\n  '.join(
        f"sess_hi_since_lo_{n} AS k_{n}, secs_since_lo_{n} AS t_{n}, bars_since_lo_{n} AS b_{n},\n  "
        f"CASE WHEN secs_since_lo_{n} >= 0 THEN 1e4*(signal_vwap/lo_px_{n}-1) END AS dist_{n},\n  "
        f"CASE WHEN secs_since_lo_{n} >= 0 THEN 1e4*(lo_px_{n}/sess_hi_at_lo_{n}-1) END AS depth_{n},\n  "
        f"CASE WHEN secs_since_lo_{n} >= 0 THEN 1e4*(signal_vwap/sess_hi_at_lo_{n}-1) END AS reclaim_{n}"
        for n in SHK)
    con.execute(f"""
    COPY (
      SELECT symbol AS ticker, trade_date::DATE AS date, year(trade_date::DATE) AS yr, hash(symbol || '|' || trade_date) AS tkd,
        signal_sec, entry_sec, exit_sec, signal_vwap, entry_px,
        1e4*ret_exit AS r_ts30,
        1e4*(ex_ts60b_px/entry_px-1) AS r_ts60, ex_ts60b_sec AS x_ts60,
        1e4*(ex_ts120b_px/entry_px-1) AS r_ts120, ex_ts120b_sec AS x_ts120,
        1e4*(fwd_vwap_300/entry_px-1) AS r_fwd300, 1e4*(fwd_vwap_600/entry_px-1) AS r_fwd600,
        1e4*(fwd_vwap_1200/entry_px-1) AS r_fwd1200,
        1e4*(ex_lo60_px/entry_px-1) AS r_lo60, ex_lo60_sec AS x_lo60,
        gap_60, volat_20m, dv_60, dv_1200, hi_rate_hl120, bars_present, dv_0945_tape, rvol_0945_honest,
        std_20m_lag1m/volat_20m_lag1m AS tight_lag, eff_open, highs_20m_since_lo_1200 AS reseat_1200,
        1e4*(signal_vwap/sess_lo-1) AS dist_sesslo, 1e4*(signal_vwap/open_px-1) AS dist_open,
        {shk_cols}
      FROM read_parquet('{args.corpus}/*.parquet')
      WHERE side = 1
    ) TO '{args.slice}' (FORMAT PARQUET, COMPRESSION 'zstd');
    """)
    print(f"slice built in {time.time()-t0:.0f}s -> {args.slice}", flush=True)

n_all = con.execute(f"SELECT count(*), count(DISTINCT tkd), min(date), max(date) FROM read_parquet('{args.slice}')").fetchone()
print(f"slice: {n_all[0]:,} trips / {n_all[1]:,} tkd / {n_all[2]} -> {n_all[3]}")

@njit(cache=True)
def greedy(tkd, ent, ext, ret, out_sum, out_n):
    # rows sorted by (tkd, ent); admit a trip when ent >= last admitted exit of the same tkd
    last_tkd = -1; last_ext = -1; j = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1; j += 1
        if ent[i] >= last_ext:
            out_sum[j] += ret[i]; out_n[j] += 1; last_ext = ext[i]
    return j + 1

EXITS = {'ts30': ('r_ts30', 'exit_sec'), 'ts60': ('r_ts60', 'x_ts60'), 'ts120': ('r_ts120', 'x_ts120'),
         'fwd300': ('r_fwd300', 'entry_sec + 300'), 'fwd600': ('r_fwd600', 'entry_sec + 600'),
         'fwd1200': ('r_fwd1200', 'entry_sec + 1200'), 'lo60': ('r_lo60', 'x_lo60')}
NYRS = None

def cell(where, ex=None, label=''):
    """mc=1 replay inside `where`, equal-weighted by tkd. Returns a stats dict."""
    ex = ex or args.exit
    rcol, xcol = EXITS[ex]
    df = con.execute(f"""
      SELECT tkd, yr, entry_sec AS ent, {xcol} AS ext, {rcol} AS ret
      FROM read_parquet('{args.slice}') WHERE ({where}) AND {rcol} IS NOT NULL AND NOT isnan({rcol})
      ORDER BY tkd, entry_sec""").df()
    if len(df) == 0:
        return dict(label=label, tkd=0)
    tkd = df.tkd.values.astype(np.int64); ent = df.ent.values.astype(np.int64)
    ext = df.ext.values.astype(np.int64); ret = df.ret.values.astype(np.float64)
    s = np.zeros(len(df)); n = np.zeros(len(df))
    m = greedy(tkd, ent, ext, ret, s, n)
    s, n = s[:m], n[:m]
    yr_first = df.yr.values[np.r_[0, np.flatnonzero(np.diff(tkd)) + 1]]
    day = s / n                       # per-tkd mean bp of the admitted trades
    mc0 = df.groupby('tkd', sort=False).ret.mean().values
    yrs = pd.Series(day).groupby(yr_first).mean()
    top = np.sort(day)[: max(1, int(len(day) * 0.95))]  # drop the best 5% of days
    nyrs = len(yrs)
    return dict(label=label, tkd=m, tkd_yr=m / max(nyrs, 1), trips=len(df), adm=n.sum(),
                eqw=day.mean(), med=np.median(day), up=(day > 0).mean() * 100,
                yrs=f"{(yrs > 0).sum()}/{nyrs}", trim=top.mean(), mc0=mc0.mean(),
                y26=yrs.get(2026, np.nan), worst=day.min() / 100)

def show(rows, title):
    print(f"\n### {title}  [exit={args.exit}, mc=1 replay-inside-gate, eqw = per-tkd mean bp]")
    print("| cell | tkd/yr | trips | eqw | med | up% | yrs | trim-top5 | mc0 | 2026 | worst day % |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        if r.get('tkd', 0) == 0:
            print(f"| {r['label']} | 0 | | | | | | | | | |"); continue
        print(f"| {r['label']} | {r['tkd_yr']:,.0f} | {r['trips']:,} | {r['eqw']:+.2f} | {r['med']:+.2f} | {r['up']:.1f} | {r['yrs']} | {r['trim']:+.2f} | {r['mc0']:+.2f} | {r['y26']:+.2f} | {r['worst']:.1f} |")
    sys.stdout.flush()

FRAME = "gap_60 < 30 AND volat_20m >= 0.002 AND signal_sec >= 35100"
print(f"\nFRAME = {FRAME}  (S33's dense rung + the universe-knowability floor)")

# T0 — the rung itself, and the reseat family as the reference feature
show([cell("TRUE", label='all session highs (no frame)'),
      cell(FRAME, label='FRAME'),
      cell(FRAME + " AND reseat_1200 BETWEEN 1 AND 4", label='FRAME × reseat 1-4 (S3e ref)')],
     "T0 — the session-high rung")

# T1 — the anchor horizon: first session high after the low (k=1) vs later vs never
rows = []
for n in SHK:
    rows.append(cell(FRAME + f" AND t_{n} < 0", label=f'{n}: no low yet this session'))
    rows.append(cell(FRAME + f" AND k_{n} = 1", label=f'{n}: k=1 (FIRST sess high after the low)'))
    rows.append(cell(FRAME + f" AND k_{n} BETWEEN 2 AND 4", label=f'{n}: k=2-4'))
    rows.append(cell(FRAME + f" AND k_{n} >= 5", label=f'{n}: k>=5'))
show(rows, "T1 — session highs since the last N-m low (k)")

# T2 — time from the low to the first new session high (the SPEED of the reversal)
TB = [(0, 120, '<2m'), (120, 300, '2-5m'), (300, 600, '5-10m'), (600, 1200, '10-20m'), (1200, 2400, '20-40m'), (2400, 10**6, '40m+')]
for n in ['20m', '60m']:
    show([cell(FRAME + f" AND k_{n} = 1 AND t_{n} >= {a} AND t_{n} < {b}", label=f'{n} k=1, t {lab}') for a, b, lab in TB],
         f"T2 — {n} low, k=1: time from the low to this new high")

# T3 — depth of the shakeout (how far below the standing session high the low went)
DB = [(-30, 0, '0 to -30bp'), (-60, -30, '-30 to -60'), (-100, -60, '-60 to -100'), (-200, -100, '-1 to -2%'), (-400, -200, '-2 to -4%'), (-10**6, -400, '<-4%')]
for n in ['20m', '60m']:
    show([cell(FRAME + f" AND k_{n} = 1 AND depth_{n} >= {a} AND depth_{n} < {b}", label=f'{n} k=1, depth {lab}') for a, b, lab in DB],
         f"T3 — {n} low, k=1: depth of the break below the standing high")

# T4 — the thesis cross: QUICK × DEEP (20m)
print("\n### T4 — 20m low, k=1: time × depth, eqw (tkd/yr)")
print("| depth \\ t | <5m | 5-10m | 10-20m | 20-40m | 40m+ |")
print("|---|---|---|---|---|---|")
T4 = [(0, 300, '<5m'), (300, 600, '5-10m'), (600, 1200, '10-20m'), (1200, 2400, '20-40m'), (2400, 10**6, '40m+')]
for a, b, lab in DB:
    out = []
    for ta, tb, _ in T4:
        r = cell(FRAME + f" AND k_20m = 1 AND depth_20m >= {a} AND depth_20m < {b} AND t_20m >= {ta} AND t_20m < {tb}")
        out.append(f"{r['eqw']:+.1f} ({r['tkd_yr']:,.0f}) {r['yrs']}" if r.get('tkd', 0) else '—')
    print(f"| {lab} | " + " | ".join(out) + " |")
sys.stdout.flush()

# T5 — controls on the thesis cell: exits, years, clock
THESIS = FRAME + " AND k_20m = 1 AND t_20m < 600 AND depth_20m < -60"
print(f"\nTHESIS cell = {THESIS}")
rows = [cell(THESIS, ex=e, label=f'exit {e}') for e in ['ts30', 'ts60', 'ts120', 'fwd300', 'fwd600', 'fwd1200', 'lo60']]
show(rows, "T5a — the thesis cell across exits")
rows = [cell(THESIS + f" AND yr = {y}", label=f'{y}') for y in range(2020, 2027)]
show(rows, "T5b — the thesis cell by year")
CLK = [(35100, 36000, '09:45-10:00'), (36000, 39600, '10:00-11:00'), (39600, 46800, '11:00-13:00'), (46800, 54000, '13:00-15:00'), (54000, 57600, '15:00-16:00')]
rows = [cell(THESIS + f" AND signal_sec >= {a} AND signal_sec < {b}", label=lab) for a, b, lab in CLK]
rows += [cell(FRAME + f" AND signal_sec >= {a} AND signal_sec < {b}", label=f'FRAME {lab} (control)') for a, b, lab in CLK]
show(rows, "T5c — the thesis cell by clock, with the frame as the time control")

# T6 — the cell the cross actually picked: DEEP pullback × SLOW reversal (and its k=2-4 twin)
DEEP_SLOW = FRAME + " AND k_20m = 1 AND t_20m >= 2400 AND depth_20m < -400"
print(f"\nDEEP×SLOW cell = {DEEP_SLOW}")
show([cell(DEEP_SLOW, ex=e, label=f'exit {e}') for e in ['ts30', 'ts60', 'ts120', 'fwd300', 'fwd600', 'fwd1200', 'lo60']],
     "T6a — deep×slow across exits")
show([cell(DEEP_SLOW + f" AND yr = {y}", label=f'{y}') for y in range(2020, 2027)], "T6b — deep×slow by year")
show([cell(DEEP_SLOW + f" AND signal_sec >= {a} AND signal_sec < {b}", label=lab) for a, b, lab in CLK], "T6c — deep×slow by clock")
show([cell(FRAME + " AND k_20m BETWEEN 2 AND 4 AND t_20m >= 2400 AND depth_20m < -400", label='k=2-4 × deep × slow'),
      cell(FRAME + " AND k_20m BETWEEN 1 AND 4 AND t_20m >= 2400 AND depth_20m < -400", label='k=1-4 × deep × slow'),
      cell(FRAME + " AND k_20m BETWEEN 1 AND 4 AND depth_20m < -400", label='k=1-4 × deep (any t)'),
      cell(FRAME + " AND k_20m BETWEEN 1 AND 4 AND t_20m >= 2400", label='k=1-4 × slow (any depth)'),
      cell(FRAME + " AND k_20m BETWEEN 1 AND 4", label='k=1-4 (the state alone)')],
     "T6d — widening the cell")
# T7 — does the volatility band matter (S6b said the reseat sign flips with volat)?
VB = [(0.002, 0.004, '20-40bp'), (0.004, 0.008, '40-80bp'), (0.008, 9, '80bp+')]
show([cell(FRAME + f" AND k_20m BETWEEN 1 AND 4 AND volat_20m >= {a} AND volat_20m < {b}", label=f'k=1-4 × volat {lab}') for a, b, lab in VB]
     + [cell(FRAME + f" AND k_20m >= 5 AND volat_20m >= {a} AND volat_20m < {b}", label=f'k>=5 × volat {lab}') for a, b, lab in VB],
     "T7 — the state by volatility band")
