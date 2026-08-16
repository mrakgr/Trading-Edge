import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 260); pd.set_option('display.max_columns', 60)
con = duckdb.connect(config={'memory_limit': '8GB', 'threads': 8})
con.execute('SET enable_progress_bar=false')
con.execute('''CREATE TEMP TABLE S AS
SELECT s.ticker,s.date,year(s.date) yr, -(s.ovn_from_lim59) r, 3540-s.nb60k59 gaps, s.chg60k59 chg,
 s.close_d, s.dv_lh, v.volat_open30 vo30, v.volat_lh vlh
FROM read_parquet("data/equity/flushfader/snoozer_shape.parquet") s
JOIN read_parquet("data/equity/flushfader/snoozer_volat.parquet") v
  ON v.ticker=s.ticker AND v.date=s.date
WHERE s.chg60k59 > 0.06 AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh>0
  AND s.dv_over_open15 IS NOT NULL''')
A = con.execute('SELECT * FROM S').fetchdf()
r = A.r.values; yrs = sorted(A.yr.unique()); rng = np.random.default_rng(77)


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float('inf') if l == 0 else g / l


v = A.vo30.values * 1e4
G = A.gaps.values >= 1000
print('QUIET CELL:  gaps>=1000 x volat_open30 < 40bp, across signal depths')
rows = []
for c in (0.06, 0.08, 0.10, 0.12, 0.15):
    ctx = G & (A.chg.values > c); m = ctx & (v < 40)
    k = int(m.sum()); x = r[m]; nb = int(ctx.sum())
    null = np.array([pf(r[ctx][rng.choice(nb, k, replace=False)]) for _ in range(2000)])
    null = null[np.isfinite(null)]
    neg = [y for y in yrs if (m & (A.yr.values == y)).sum() >= 8
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    rows.append({'chg >': f'{c*100:g}%', 'ctx n': nb, 'ctx PF': round(pf(r[ctx]), 3),
                 'ctx worst%': round(r[ctx].min()*100),
                 'cell n': k, 'PF': round(pf(x), 3),
                 'pctile': round(float((null < pf(x)).mean()*100), 1),
                 'mean%': round(x.mean()*100, 2), 'med%': round(np.median(x)*100, 2),
                 'win%': round((x > 0).mean()*100),
                 'p5%': round(np.percentile(x, 5)*100, 1),
                 'worst5%': round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
                 'WORST%': round(x.min()*100), '>100% losses': int((x < -1).sum()),
                 'loss%': round((x < 0).mean()*100), 'yrs<1': len(neg)})
print(pd.DataFrame(rows).to_string(index=False))

m = G & (A.chg.values > 0.08) & (v < 40)
print('\nper-year, gaps>=1000 x chg>+8% x volat_open30<40bp:')
row = {}
for y in yrs:
    mm = m & (A.yr.values == y)
    row[str(y)] = ('.' if mm.sum() < 3 else f'{pf(r[mm]):.2f}({int(mm.sum())})')
print(pd.DataFrame([row]).to_string(index=False))

print('\nWHAT ARE THESE NAMES? quiet cell vs the rest of the gate (chg>+8%)')
ctx = G & (A.chg.values > 0.08)
for lab, mm in (('QUIET <40bp', ctx & (v < 40)), ('rest >=40bp', ctx & (v >= 40))):
    print('  %-13s n=%4d  med price %8.2f  med last-hr dollars %7.2fM  med chg %5.1f%%  '
          'med volat_lh %5.1fbp  med gaps %5.0fs'
          % (lab, int(mm.sum()), np.nanmedian(A.close_d.values[mm]),
             np.nanmedian(A.dv_lh.values[mm])/1e6, np.nanmedian(A.chg.values[mm])*100,
             np.nanmedian(A.vlh.values[mm])*1e4, np.nanmedian(A.gaps.values[mm])))

print('\nIS IT JUST A PRICE/LIQUIDITY PROXY? matched-n substitutes inside chg>+8% gate')
rows = []
nb = int(ctx.sum()); k = int((ctx & (v < 40)).sum())
for lab, arr, op in (('volat_open30 < 40bp', v, 'lt'),
                     ('close_d HIGH', A.close_d.values, 'hi'),
                     ('dv_lh HIGH', A.dv_lh.values, 'hi'),
                     ('volat_lh LOW', A.vlh.values*1e4, 'lt'),
                     ('gaps LOW', A.gaps.values.astype(float), 'lt')):
    vals = arr.copy()
    t = np.nanquantile(vals[ctx], k/nb if op == 'lt' else 1-k/nb)
    mm = ctx & ((vals < t) if op == 'lt' else (vals > t))
    x = r[mm]; kk = int(mm.sum())
    null = np.array([pf(r[ctx][rng.choice(nb, kk, replace=False)]) for _ in range(2000)])
    null = null[np.isfinite(null)]
    rows.append({'filter': lab, 'n': kk, 'PF': round(pf(x), 3),
                 'pctile': round(float((null < pf(x)).mean()*100), 1),
                 'mean%': round(x.mean()*100, 2),
                 'worst5%': round(np.sort(x)[:max(1, kk//20)].mean()*100, 1),
                 'WORST%': round(x.min()*100)})
print(pd.DataFrame(rows).to_string(index=False))

print('\n2020 CONCENTRATION CHECK — 79 of 200 trades are 2020')
ctx = G & (A.chg.values > 0.08)
m = ctx & (v < 40)
for lab, mm in (('full cell', m), ('EXCLUDING 2020', m & (A.yr.values != 2020)),
                ('2020 only', m & (A.yr.values == 2020))):
    x = r[mm]; k = int(mm.sum())
    print('  %-16s n=%4d  PF %6.3f  mean %+5.2f%%  med %+5.2f%%  win %2.0f%%  '
          'p5 %5.1f%%  worst5%% %5.1f%%  WORST %4.0f%%  loss %2.0f%%'
          % (lab, k, pf(x), x.mean()*100, np.median(x)*100, (x > 0).mean()*100,
             np.percentile(x, 5)*100, np.sort(x)[:max(1, k//20)].mean()*100,
             x.min()*100, (x < 0).mean()*100))

print('\nTRADES PER YEAR at chg>+8% x volat<40bp (frequency check)')
for y in yrs:
    mm = m & (A.yr.values == y)
    print('  %d  n=%3d' % (y, int(mm.sum())), end='')
    if mm.sum() >= 3:
        print('  PF %6.2f  mean %+6.2f%%  worst %5.0f%%'
              % (pf(r[mm]), r[mm].mean()*100, r[mm].min()*100))
    else:
        print('')
