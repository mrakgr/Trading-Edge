"""Breakout BO-6 — an INDEPENDENT re-implementation of TradingEdge.Breakout --window absorption, compared trip for trip.

Independent parts: every window's volume / delta / vwap / high / low DIRECTLY on its slice (no prefix sums, no deques),
the session medians and the causal references (volume per present bar, |N-bar move|, |N-bar imb|) rebuilt from the bars,
the exits and stops by scanning, d30 / d60 on slices. The detection, the refractory rule, the FADE and LEVEL families and
the bar order (open trips → armed levels → detections) are re-coded from the rules (AbsEngine.fs header).

  cd research && ./TradingEdge.Breakout/bin/Release/net10.0/TradingEdge.Breakout --window absorption --roots ES --limit-days 40 -o data/futures/breakout/test_abs
  cd research && python -u scripts/futures/breakout_check_abs.py --run data/futures/breakout/test_abs --root ES --limit-days 40 > logs/breakout_check_abs.log 2> logs/breakout_check_abs.err; echo exit $?
"""
import argparse
import sys

import duckdb
import numpy as np
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--run', required=True)
ap.add_argument('--root', required=True)
ap.add_argument('--bars', default='data/futures/intraday_1s_ticks')
ap.add_argument('--ns', default='30,60,120,300')
ap.add_argument('--min-imb-ratio', type=float, default=2.0)
ap.add_argument('--min-rv', type=float, default=1.0)
ap.add_argument('--max-move-ratio', type=float, default=0.5)
ap.add_argument('--level-sec', type=int, default=900)
ap.add_argument('--nref', type=int, default=20)
ap.add_argument('--limit-days', type=int, required=True)
a = ap.parse_args()
NS = [int(x) for x in a.ns.split(',')]
H = [30, 60, 300, 900]
S0, ENTRY_END, FLAT = 34200, 54000, 57600

con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4")
got = con.execute(f"SELECT * FROM read_parquet('{a.run}/legs/*.parquet')").df()
fr = con.execute(f"""
    WITH v AS (SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})\\.parquet$', 1) AS DATE) d, bucket >= 61200 nxt,
                      ticker, sum(volume) vol
               FROM read_parquet('{a.bars}/*.parquet', filename = true) WHERE left(ticker, length(ticker) - 3) = '{a.root}' GROUP BY ALL),
    s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol FROM v GROUP BY ALL),
    l AS (SELECT td, ticker, row_number() OVER (PARTITION BY td ORDER BY vol DESC, ticker) rk FROM s)
    SELECT td, lag(ticker) OVER (ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td""").df().dropna().head(a.limit_days)
tick = dict(con.execute("SELECT contract, tick FROM read_parquet('data/futures/grids.parquet') WHERE tick IS NOT NULL").fetchall())


def session(td, c, vbar, rmove, rimb, emit):
    b = con.execute(f"""SELECT bucket t, vwap::DOUBLE p, volume::DOUBLE v, delta::DOUBLE d FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet')
                        WHERE ticker = '{c}' AND bucket >= {S0} AND bucket < {FLAT} ORDER BY bucket""").df()
    t, p, v, dl = b.t.to_numpy(), b.p.to_numpy(), b.v.to_numpy(), b.d.to_numpy()
    n, tk = len(b), tick[c]
    # per N, per bar: the window (i-N, i] on its slice
    W = {}
    for N in NS:
        rows = [None] * n
        for i in range(N, n):
            sv = v[i - N + 1:i + 1]; vol = sv.sum()
            rows[i] = (vol, dl[i - N + 1:i + 1].sum() / vol if vol > 0 else np.nan, (p[i] - p[i - N]) / tk,
                       (sv * p[i - N + 1:i + 1]).sum() / vol if vol > 0 else np.nan, p[i - N + 1:i + 1].max(), p[i - N + 1:i + 1].min())
        W[N] = rows
    med_move = [np.median([abs(r[2]) for r in W[N] if r is not None]) if n > N else np.nan for N in NS]
    med_imb = [np.median([abs(r[1]) for r in W[N] if r is not None and r[0] > 0]) if n > N else np.nan for N in NS]
    out = []
    if not emit:
        return out, med_move, med_imb, v.sum() / n
    opened, armed = [], []
    nxt = {(N, s): 0 for N in NS for s in (1, -1)}
    def new(fam, det, side, sig_t, stop):
        return dict(fam=fam, n_win=det['N'], aggr=det['aggr'], det_t=det['det_t'], side=side, sig_t=sig_t, stop_px=stop,
                    imb=det['imb'], imb_ratio=det['imbr'], rv=det['rv'], move_ticks=det['move'], move_ratio=det['ratio'],
                    win_vwap=det['vwap'], win_hi=det['hi'], win_lo=det['lo'], win_vol=det['vol'], entry_i=None)
    for i in range(n):
        keep = []
        for o in opened:
            if o['entry_i'] is None:
                o['entry_i'], o['entry_t'], o['entry_px'] = i, t[i], p[i]
            elif o.get('pend'):
                o['stop_t'], o['stop_fill_px'], o['pend'] = t[i], p[i], False
            done = True
            for h in H:
                if f'exit_t_{h}' not in o:
                    if t[i] >= o['entry_t'] + h:
                        o[f'exit_t_{h}'], o[f'exit_px_{h}'], o[f'eod_{h}'] = t[i], p[i], False
                    else:
                        done = False
            if not done and 'stop_sig_t' not in o and o['side'] * (p[i] - o['stop_px']) < 0:
                o['stop_sig_t'], o['pend'] = t[i], True
            (out if done and not o.get('pend') else keep).append(o)
        opened = keep
        can = t[i] <= ENTRY_END and i < n - 1
        keep = []
        for d in armed:
            if t[i] <= d['det_t'] + a.level_sec:
                s = 1 if p[i] > d['hi'] else (-1 if p[i] < d['lo'] else 0)
                if s:
                    if can:
                        opened.append(new('level', d, s, t[i], d['lo'] if s > 0 else d['hi']))
                else:
                    keep.append(d)
        armed = keep
        for k, N in enumerate(NS):
            r = W[N][i]
            if r is None or not r[0] > 0:
                continue
            vol, imb, mv, vw, hi, lo = r
            s = 1 if imb > 0 else (-1 if imb < 0 else 0)
            if s == 0 or i < nxt[(N, s)] or not abs(imb) / rimb[k] >= a.min_imb_ratio:
                continue
            rv = vol / (N * vbar); move = s * mv; ratio = move / rmove[k]
            if rv >= a.min_rv and ratio <= a.max_move_ratio:
                det = dict(N=N, aggr=s, det_t=t[i], imb=imb, imbr=abs(imb) / rimb[k], rv=rv, move=move, ratio=ratio, vwap=vw, hi=hi, lo=lo, vol=vol)
                nxt[(N, s)] = i + N
                if can:
                    opened.append(new('fade', det, -s, t[i], lo if -s > 0 else hi))
                    armed.append(det)
    for o in opened:
        if o['entry_i'] is not None:
            for h in H:
                if f'exit_t_{h}' not in o:
                    o[f'exit_t_{h}'], o[f'exit_px_{h}'], o[f'eod_{h}'] = t[n - 1], p[n - 1], True
            if o.get('pend'):
                o['stop_t'], o['stop_fill_px'] = t[n - 1], p[n - 1]
            out.append(o)
    for o in out:
        i0 = o['entry_i']
        for s, nm in [(30, 'd30'), (60, 'd60')]:
            j = np.searchsorted(t, o['entry_t'] + s, 'right')
            vv = v[i0:j].sum()
            o[nm] = o['side'] * dl[i0:j].sum() / vv if vv > 0 else np.nan
        o['td'] = td
    return out, med_move, med_imb, v.sum() / n


hist_b, hist_m, hist_i, rows = [], [], [], []
for td, c in zip(fr.td, fr.front):
    if con.execute(f"SELECT count(*) FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet') WHERE ticker = '{c}' AND bucket >= {S0} AND bucket < {FLAT}").fetchone()[0] < 3:
        continue
    warm = len(hist_b) >= a.nref
    ref = lambda h: [np.nanmean([m[k] for m in h[-a.nref:]]) if warm else np.nan for k in range(len(NS))]
    o, mm, mi, vb = session(td, c, np.mean(hist_b[-a.nref:]) if warm else np.nan, ref(hist_m), ref(hist_i), warm)
    rows += o; hist_b.append(vb); hist_m.append(mm); hist_i.append(mi)
    print(f'{td} {c} refs {len(hist_b)} trips so far {len(rows)}', file=sys.stderr, flush=True)

exp = pd.DataFrame(rows)
key = ['fam', 'n_win', 'aggr', 'det_t', 'td']
got['td'] = pd.to_datetime(got.td); exp['td'] = pd.to_datetime(exp.td)
print(f'engine {len(got):,} trips · reference {len(exp):,}')
m = got.merge(exp, on=key, how='outer', suffixes=('', '_x'), indicator=True)
print(m._merge.value_counts().to_string())
both = m[m._merge == 'both']
bad = 0
COLS = (['side', 'sig_t', 'entry_t', 'entry_px', 'stop_px', 'imb', 'imb_ratio', 'rv', 'move_ticks', 'move_ratio', 'win_vwap', 'win_hi',
         'win_lo', 'win_vol'] + [f'{k}_{h}' for h in H for k in ('exit_t', 'exit_px', 'eod')] + ['stop_sig_t', 'stop_t', 'stop_fill_px', 'd30', 'd60'])
for col in COLS:
    g, x = both[col], both[col + '_x']
    if col.startswith('eod'):
        ne = g.astype(bool) != x.astype(bool)
    else:
        g, x = pd.to_numeric(g, errors='coerce').astype(float), pd.to_numeric(x, errors='coerce').astype(float)
        ne = ~(np.isclose(g, x, rtol=1e-7, atol=1e-9) | (g.isna() & x.isna()))
    if ne.any() or col in ('side', 'entry_px', 'imb_ratio', 'd30'):
        print(f'{col:14s} mismatches {int(ne.sum()):,}')
    bad += int(ne.sum())
print('PASS' if bad == 0 and (m._merge != 'both').sum() == 0 else 'FAIL')
