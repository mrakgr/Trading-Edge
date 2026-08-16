import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 60)
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
r = A.r.values; yrs = sorted(A.yr.unique()); rng = np.random.default_rng(21)
v = A.volat.values*1e4; g = A.gaps.values.astype(float)
dv = A.dv_lh.values; px = A.px.values; y20 = A.yr.values == 2020


def pf(x):
    a, b = x[x > 0].sum(), -x[x < 0].sum()
    return float('inf') if b == 0 else a/b


def st(m, base):
    x = r[m]; k = int(m.sum()); mx = m & ~y20
    rb = r[base]; nb = len(rb)
    p = np.nan
    if 20 <= k < nb:
        null = np.array([pf(rb[rng.choice(nb, k, replace=False)]) for _ in range(2000)])
        null = null[np.isfinite(null)]
        p = round(float((null < pf(x)).mean()*100), 1)
    neg = [y for y in yrs if (m & (A.yr.values == y)).sum() >= 5
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    return {'n': k, 'PF': round(pf(x), 3),
            'worst5%': round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
            'WORST%': round(x.min()*100), '>100%': int((x < -1).sum()),
            'yrs<1': len(neg), 'med px': round(float(np.nanmedian(px[m])), 2),
            'PF ex20': round(pf(r[mx]), 3), 'pctile': p}


print('GAPS vs ABSOLUTE LAST-HOUR DOLLARS, matched n, in all three volatility regions')
print('and repeated inside a $2 and $5 price floor (the borrow-tradeable subset)')
for rlab, rm in (('volat < 40bp', v < 40), ('volat [40,100)bp', (v >= 40) & (v < 100)),
                 ('volat >= 100bp', v >= 100)):
    for plab, pm in (('all prices', np.ones(len(A), bool)),
                     ('px >= $2', px >= 2), ('px >= $5', px >= 5)):
        base = rm & pm
        if base.sum() < 100:
            continue
        rows = []
        for lab, col, op in (('GAPS  high', g, '>'), ('DOLLARS low', dv, '<=')):
            t = np.nanquantile(col[base], .5)
            m = base & ((col >= t) if op == '>' else (col <= t))
            d = {'region': rlab, 'price': plab, 'base n': int(base.sum()),
                 'base PF': round(pf(r[base]), 3), 'filter': lab}
            d.update(st(m, base))
            rows.append(d)
        print()
        print(pd.DataFrame(rows).to_string(index=False))
