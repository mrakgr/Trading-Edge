#!/usr/bin/env python3
"""LongHiker CONSOLIDATION study (user feature, 2026-09-07): the last {3,5,10,20}m
of ln(1s vwap) cut into 30-present-bar slots, consol = mean within-slot variance /
whole-window variance (SlotVarRatioMa; law of total variance → exactly [0,1]).
1 = a coil around one level, 0 = a trend; a random walk reads ≈ 1.6/k for k slots
(3m ≈ .26, 5m ≈ .18, 10m ≈ .08, 20m ≈ .04). `_lag1m` twins end 2 slots before
the signal so the breakout slot cannot deflate the score.

Sampler = v7 (S32): every strict new 20m high (side +1) / low (side −1), no eff
gate. Returns are put in TRADE convention here (side −1 negated). Every cell is
replayed mc=1 (greedy non-overlap inside the gate) and equal-weighted by
ticker-day, per side.

  python3 scripts/equity/longhiker_consol_study.py --corpus data/longhiker_trips_consol --slice data/longhiker_study_consol.parquet
"""
import argparse, os, sys, time
import numpy as np, pandas as pd, duckdb
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument('--corpus', default='data/longhiker_trips_consol')
ap.add_argument('--slice', default='data/longhiker_study_consol.parquet')
ap.add_argument('--rebuild', action='store_true')
ap.add_argument('--exit', default='ts30', help='primary exit: ts30|ts60|ts120|fwd300|fwd600|fwd1200')
ap.add_argument('--tables', default='all', help='comma list of T0..T6 or all')
args = ap.parse_args()

con = duckdb.connect()
con.execute("SET memory_limit='6GB'; SET threads=8; SET preserve_insertion_order=false")

WIN = ['3m', '5m', '10m', '20m']
if args.rebuild or not os.path.exists(args.slice):
    t0 = time.time()
    ccols = ', '.join(f"consol_{w}, consol_{w}_lag1m" for w in WIN)
    con.execute(f"""
    COPY (
      SELECT symbol AS ticker, trade_date::DATE AS date, year(trade_date::DATE) AS yr,
        hash(symbol || '|' || trade_date) AS tkd, side,
        signal_sec, entry_sec, exit_sec, signal_vwap, entry_px,
        side*1e4*ret_exit AS r_ts30,
        side*1e4*(ex_ts60b_px/entry_px-1) AS r_ts60, ex_ts60b_sec AS x_ts60,
        side*1e4*(ex_ts120b_px/entry_px-1) AS r_ts120, ex_ts120b_sec AS x_ts120,
        side*1e4*(fwd_vwap_300/entry_px-1) AS r_fwd300, side*1e4*(fwd_vwap_600/entry_px-1) AS r_fwd600,
        side*1e4*(fwd_vwap_1200/entry_px-1) AS r_fwd1200,
        gap_60, volat_20m, volat_20m_lag1m, dv_60, dv_1200, bars_present, dv_0945_tape, rvol_0945_honest,
        std_20m_lag1m/volat_20m_lag1m AS tight_lag, dv_ewma_1m/dv_ewma_20m AS vol_ratio, vol_ratio_max5m,
        eff_open, eff_ewma_20m, vr4_ewma, hi_rate_hl120,
        highs_20m_since_lo_1200 AS reseat_1200, signal_vwap AS px,
        {ccols}
      FROM read_parquet('{args.corpus}/*.parquet')
    ) TO '{args.slice}' (FORMAT PARQUET, COMPRESSION 'zstd');
    """)
    print(f"slice built in {time.time()-t0:.0f}s -> {args.slice}", flush=True)

n_all = con.execute(f"SELECT count(*), count(DISTINCT tkd), min(date), max(date) FROM read_parquet('{args.slice}')").fetchone()
print(f"slice: {n_all[0]:,} trips / {n_all[1]:,} tkd / {n_all[2]} -> {n_all[3]}")

@njit
def greedy(tkd, ent, ext, ret, out_sum, out_n):
    last_tkd = -1; last_ext = -1; j = -1
    for i in range(tkd.shape[0]):
        if tkd[i] != last_tkd:
            last_tkd = tkd[i]; last_ext = -1; j += 1
        if ent[i] >= last_ext:
            out_sum[j] += ret[i]; out_n[j] += 1; last_ext = ext[i]
    return j + 1

EXITS = {'ts30': ('r_ts30', 'exit_sec'), 'ts60': ('r_ts60', 'x_ts60'), 'ts120': ('r_ts120', 'x_ts120'),
         'fwd300': ('r_fwd300', 'entry_sec + 300'), 'fwd600': ('r_fwd600', 'entry_sec + 600'),
         'fwd1200': ('r_fwd1200', 'entry_sec + 1200')}

def cell(where, ex=None, label=''):
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
    day = s / n
    mc0 = df.groupby('tkd', sort=False).ret.mean().values
    yrs = pd.Series(day).groupby(yr_first).mean()
    top = np.sort(day)[: max(1, int(len(day) * 0.95))]
    nyrs = len(yrs)
    return dict(label=label, tkd=m, tkd_yr=m / max(nyrs, 1), trips=len(df), adm=n.sum(),
                eqw=day.mean(), med=np.median(day), up=(day > 0).mean() * 100,
                yrs=f"{(yrs > 0).sum()}/{nyrs}", trim=top.mean(), mc0=mc0.mean(),
                y26=yrs.get(2026, np.nan), worst=day.min() / 100)

def show(rows, title):
    print(f"\n### {title}  [exit={args.exit}, mc=1 replay-inside-gate, eqw = per-tkd mean bp, TRADE convention]")
    print("| cell | tkd/yr | trips | eqw | med | up% | yrs | trim-top5 | mc0 | 2026 | worst day % |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for r in rows:
        if r.get('tkd', 0) == 0:
            print(f"| {r['label']} | 0 | | | | | | | | | |"); continue
        print(f"| {r['label']} | {r['tkd_yr']:,.0f} | {r['trips']:,} | {r['eqw']:+.2f} | {r['med']:+.2f} | {r['up']:.1f} | {r['yrs']} | {r['trim']:+.2f} | {r['mc0']:+.2f} | {r['y26']:+.2f} | {r['worst']:.1f} |")
    sys.stdout.flush()

def want(t): return args.tables == 'all' or t in args.tables.split(',')

FRAME = "gap_60 < 30 AND volat_20m >= 0.002 AND signal_sec >= 35100"
print(f"\nFRAME = {FRAME}  (S33's dense rung + the universe-knowability floor)")
SIDES = [(1, 'LONG'), (-1, 'SHORT')]

# the bands — anchored on the random-walk reference (≈1.6/k), one coil band above it
BANDS = {
  '3m':  [(0, .15, '<.15'), (.15, .30, '.15-.30'), (.30, .45, '.30-.45'), (.45, .60, '.45-.60'), (.60, .75, '.60-.75'), (.75, 1.01, '.75+')],
  '5m':  [(0, .10, '<.10'), (.10, .20, '.10-.20'), (.20, .30, '.20-.30'), (.30, .45, '.30-.45'), (.45, .60, '.45-.60'), (.60, 1.01, '.60+')],
  '10m': [(0, .05, '<.05'), (.05, .10, '.05-.10'), (.10, .15, '.10-.15'), (.15, .25, '.15-.25'), (.25, .40, '.25-.40'), (.40, 1.01, '.40+')],
  '20m': [(0, .03, '<.03'), (.03, .06, '.03-.06'), (.06, .10, '.06-.10'), (.10, .15, '.10-.15'), (.15, .25, '.15-.25'), (.25, 1.01, '.25+')],
}

if want('T0'):
    rows = []
    for s, nm in SIDES:
        rows.append(cell(f"side={s}", label=f'{nm}: all new 20m extremes (no frame)'))
        rows.append(cell(f"side={s} AND {FRAME}", label=f'{nm}: FRAME'))
        rows.append(cell(f"side={s} AND {FRAME} AND NOT isnan(consol_20m_lag1m)", label=f'{nm}: FRAME, consol_20m warm'))
    show(rows, "T0 — the v7 rung, both sides")

if want('T1'):
    for w in WIN:
        for s, nm in SIDES:
            show([cell(f"side={s} AND {FRAME} AND consol_{w}_lag1m >= {a} AND consol_{w}_lag1m < {b}", label=f'{nm} consol_{w}_lag1m {lab}')
                  for a, b, lab in BANDS[w]], f"T1 — consol_{w}_lag1m bands, {nm}")

if want('T2'):
    # the unlagged score at the signal bar, 5m and 20m — the contamination check
    for w in ['5m', '20m']:
        for s, nm in SIDES:
            show([cell(f"side={s} AND {FRAME} AND consol_{w} >= {a} AND consol_{w} < {b}", label=f'{nm} consol_{w} (UNLAGGED) {lab}')
                  for a, b, lab in BANDS[w]], f"T2 — unlagged consol_{w} bands, {nm}")

if want('T3'):
    # non-redundancy: consol × tight (v7's std/volat) — is the coil the SAME coil?
    TB = [(0, 4, 'tight<4'), (4, 8, 'tight 4-8'), (8, 11, 'tight 8-11'), (11, 1e9, 'tight 11+')]
    for s, nm in SIDES:
        rows = []
        for a, b, lab in BANDS['20m'][1:6:2]:
            for ta, tb, tl in TB:
                rows.append(cell(f"side={s} AND {FRAME} AND consol_20m_lag1m >= {a} AND consol_20m_lag1m < {b} AND tight_lag >= {ta} AND tight_lag < {tb}",
                                 label=f'{nm} consol_20m {lab} × {tl}'))
        show(rows, f"T3 — consol_20m_lag1m × tight_lag, {nm}")

if want('T4'):
    # the user's v7 spec with consol standing in for tight; both sides
    V7 = "px > 1 AND vol_ratio > 2 AND gap_60 < 4 AND volat_20m > 0.002 AND signal_sec >= 35100"
    for s, nm in SIDES:
        rows = [cell(f"side={s} AND {V7}", label=f'{nm} v7 spec frame (px>1, vol_ratio>2, gap_60<4, volat>20bp)'),
                cell(f"side={s} AND {V7} AND tight_lag < 3.5", label=f'{nm}   × tight_lag<3.5 (the v7 spec)')]
        for w in ['5m', '20m']:
            for a, b, lab in BANDS[w][2:]:
                rows.append(cell(f"side={s} AND {V7} AND consol_{w}_lag1m >= {a} AND consol_{w}_lag1m < {b}", label=f'{nm}   × consol_{w}_lag1m {lab}'))
        show(rows, f"T4 — the v7 spec frame with consol in place of tight, {nm}")

if want('T5'):
    # exit sweep on the top two bands of each side, 5m and 20m
    for w in ['5m', '20m']:
        top = BANDS[w][-2:]
        a = top[0][0]
        for s, nm in SIDES:
            show([cell(f"side={s} AND {FRAME} AND consol_{w}_lag1m >= {a}", ex=e, label=f'{nm} consol_{w}_lag1m >= {a} @ {e}')
                  for e in ['ts30', 'ts60', 'ts120', 'fwd300', 'fwd600', 'fwd1200']], f"T5 — exit sweep, coil band consol_{w} >= {a}, {nm}")

if want('T6'):
    # volatility interaction on the coil (S6b: extension × violent tape flips)
    VB = [(0.002, 0.004, '20-40bp'), (0.004, 0.008, '40-80bp'), (0.008, 1, '80bp+')]
    for s, nm in SIDES:
        rows = []
        for a, b, lab in BANDS['5m'][1:6:2]:
            for va, vb, vl in VB:
                rows.append(cell(f"side={s} AND {FRAME} AND consol_5m_lag1m >= {a} AND consol_5m_lag1m < {b} AND volat_20m >= {va} AND volat_20m < {vb}",
                                 label=f'{nm} consol_5m {lab} × volat {vl}'))
        show(rows, f"T6 — consol_5m_lag1m × volat_20m, {nm}")
