import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 260); pd.set_option('display.max_columns', 60)
con = duckdb.connect(config={'memory_limit': '8GB', 'threads': 8})
con.execute('SET enable_progress_bar=false')
con.execute('''CREATE TEMP TABLE S AS
SELECT s.ticker,s.date,year(s.date) yr, -(s.ovn_from_lim59) r, 3540-s.nb60k59 gaps,
 s.px_lim_1559_1600 px, s.dv_lh, v.volat_open30 volat
FROM read_parquet("data/equity/flushfader/snoozer_shape.parquet") s
JOIN read_parquet("data/equity/flushfader/snoozer_volat.parquet") v
  ON v.ticker=s.ticker AND v.date=s.date
WHERE s.chg60k59 > 0.08 AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh>0
  AND s.dv_over_open15 IS NOT NULL''')
A = con.execute('SELECT * FROM S').fetchdf()
r = A.r.values; yrs = sorted(A.yr.unique()); rng = np.random.default_rng(4242)
v = A.volat.values*1e4; g = A.gaps.values.astype(float)
dv = A.dv_lh.values; px = A.px.values; y20 = A.yr.values == 2020


def pf(x):
    a, b = x[x > 0].sum(), -x[x < 0].sum()
    return float('inf') if b == 0 else a/b


def st(m, base, lbl):
    x = r[m]; k = int(m.sum()); mx = m & ~y20; rb = r[base]; nb = len(rb)
    p = np.nan
    if 20 <= k < nb:
        null = np.array([pf(rb[rng.choice(nb, k, replace=False)]) for _ in range(2500)])
        null = null[np.isfinite(null)]
        p = round(float((null < pf(x)).mean()*100), 1)
    neg = [y for y in yrs if (m & (A.yr.values == y)).sum() >= 5
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    return {'filter': lbl, 'n': k, 'keep%': '{:.0f}%'.format(100*k/nb),
            'PF': round(pf(x), 3),
            'worst5%': round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
            'WORST%': round(x.min()*100), '>100%': int((x < -1).sum()),
            'yrs<1': len(neg), 'med px': round(float(np.nanmedian(px[m])), 2),
            'PF ex20': round(pf(r[mx]), 3), 'pctile': p}


print('MEDIAN gaps in each base (so the two conventions can be compared):')
for rlab, rm in (('volat < 40bp', v < 40), ('volat [40,100)', (v >= 40) & (v < 100)),
                 ('volat >= 100bp', v >= 100)):
    for plab, pm in (('all', np.ones(len(A), bool)), ('px>=$2', px >= 2), ('px>=$5', px >= 5)):
        b = rm & pm
        print('  {:<15} {:<7} n={:>4}  median gaps = {:>5.0f}s   share with gaps>=2000 = {:.0f}%'
              .format(rlab, plab, int(b.sum()), np.median(g[b]), 100*(g[b] >= 2000).mean()))

print('\n' + '='*230)
print('FAIR TEST: gaps >= 2000 (the spec threshold) vs DOLLARS cut to THE SAME n')
print('='*230)
for rlab, rm in (('volat < 40bp', v < 40), ('volat [40,100)', (v >= 40) & (v < 100)),
                 ('volat >= 100bp', v >= 100)):
    for plab, pm in (('all prices', np.ones(len(A), bool)), ('px >= $2', px >= 2),
                     ('px >= $5', px >= 5)):
        base = rm & pm
        if base.sum() < 100:
            continue
        mg = base & (g >= 2000)
        K = int(mg.sum())
        if K < 25 or K >= base.sum():
            continue
        t = np.nanquantile(dv[base], K/base.sum())
        md = base & (dv <= t)
        rows = [st(mg, base, 'gaps >= 2000'),
                st(md, base, 'dollars <= ${:.2f}M (matched n)'.format(t/1e6))]
        for d in rows:
            d['region'] = rlab; d['price'] = plab
            d['base n'] = int(base.sum()); d['base PF'] = round(pf(r[base]), 3)
        print()
        print(pd.DataFrame(rows)[['region', 'price', 'base n', 'base PF', 'filter', 'n',
                                  'keep%', 'PF', 'worst5%', 'WORST%', '>100%', 'yrs<1',
                                  'med px', 'PF ex20', 'pctile']].to_string(index=False))
