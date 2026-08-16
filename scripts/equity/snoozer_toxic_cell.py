import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 60)
con = duckdb.connect(config={'memory_limit': '8GB', 'threads': 8})
con.execute('SET enable_progress_bar=false')
con.execute('''CREATE TEMP TABLE S AS
SELECT s.ticker,s.date,year(s.date) yr, -(s.ovn_from_lim59) r, 3540-s.nb60k59 gaps,
 s.chg60k59 chg, s.dv_over_open15 i15, s.dv_over_open30 i30, s.dv_over_open60 i60,
 s.dv_over_rest irest, s.bar_over_open30 pers, s.close_d, s.dv_lh,
 v.volat_open30 volat, v.volat_lh vlh
FROM read_parquet("data/equity/flushfader/snoozer_shape.parquet") s
JOIN read_parquet("data/equity/flushfader/snoozer_volat.parquet") v
  ON v.ticker=s.ticker AND v.date=s.date
WHERE s.chg60k59 > 0.06 AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh>0
  AND s.dv_over_open15 IS NOT NULL''')
A = con.execute('SELECT * FROM S').fetchdf()
r = A.r.values; yrs = sorted(A.yr.unique()); rng = np.random.default_rng(101)
v = A.volat.values*1e4; g = A.gaps.values; y20 = A.yr.values == 2020

TOX = (v >= 40) & (v < 100) & (g < 2000) & (A.chg.values > 0.08)
rT = r[TOX]; nT = int(TOX.sum())


def pf(x):
    a, b = x[x > 0].sum(), -x[x < 0].sum()
    return float('inf') if b == 0 else a/b


def line(m, lbl, base=None):
    x = r[m]; k = int(m.sum()); mx = m & ~y20
    neg = [y for y in yrs if (m & (A.yr.values == y)).sum() >= 5
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {'rule': lbl, 'n': k, 'PF': round(pf(x), 3),
         'mean%': round(x.mean()*100, 2), 'med%': round(np.median(x)*100, 2),
         'win%': round((x > 0).mean()*100), 'p5%': round(np.percentile(x, 5)*100, 1),
         'worst5%': round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
         'WORST%': round(x.min()*100), 'loss%': round((x < 0).mean()*100),
         'yrs<1': len(neg), 'PF ex20': round(pf(r[mx]), 3) if mx.sum() >= 15 else np.nan}
    if base is not None:
        rb = r[base]; nb = len(rb)
        if 20 <= k < nb:
            null = np.array([pf(rb[rng.choice(nb, k, replace=False)]) for _ in range(2500)])
            null = null[np.isfinite(null)]
            d['pctile'] = round(float((null < pf(x)).mean()*100), 1)
    return d


print('THE TOXIC CELL: volat[40,100)bp x gaps<2000 x chg>+8%')
print(pd.DataFrame([line(TOX, 'the cell')]).fillna('').to_string(index=False))

print('\n' + '='*230)
print('1. INTENSITY DECILES inside the toxic cell (dv_over_open30)')
print('='*230)
iv = A.i30.values
qs = np.nanquantile(iv[TOX], np.linspace(0, 1, 6))
rows = []
for i in range(5):
    m = TOX & (iv >= qs[i]) & ((iv <= qs[i+1]) if i == 4 else (iv < qs[i+1]))
    if m.sum() < 20:
        continue
    rows.append(line(m, f'Q{i+1}  [{qs[i]:.2f}, {qs[i+1]:.2f})', base=TOX))
print(pd.DataFrame(rows).fillna('').to_string(index=False))

print('\n' + '='*230)
print('2. EVERY CANDIDATE at the cell median, both directions')
print('='*230)
rows = [line(TOX, 'the cell (no filter)')]
for col, lab in (('i15', 'intensity 15m'), ('i30', 'intensity 30m'),
                 ('i60', 'intensity 60m'), ('irest', 'intensity rest-of-day'),
                 ('pers', 'persistence 30m'), ('gaps', 'gaps (within <2000)'),
                 ('volat', 'volat_open30 (within band)'), ('vlh', 'last-hour volat'),
                 ('close_d', 'price'), ('dv_lh', 'last-hour dollars')):
    vv = A[col].values.astype(float)
    t = np.nanquantile(vv[TOX], .5)
    for op in ('<=', '>'):
        m = TOX & ((vv <= t) if op == '<=' else (vv > t)) & ~np.isnan(vv)
        rows.append(line(m, f'  {lab} {op} median', base=TOX))
D = pd.DataFrame(rows).fillna('')
print(D.to_string(index=False))

print('\n' + '='*230)
print('3. IS INTENSITY JUST PROXYING GAPS HERE? (they are 67% collinear overall)')
print('='*230)
t = np.nanquantile(iv[TOX], .5)
lo = TOX & (iv <= t)
print('  intensity-LOW half: median gaps %.0f   vs cell median gaps %.0f'
      % (np.median(g[lo]), np.median(g[TOX])))
tg = np.median(g[TOX])
print('  overlap(intensity<=med, gaps>=med) = %.0f%%   (50%% = independent)'
      % (100*(lo & (g >= tg)).sum()/lo.sum()))
for glab, gm in (('gaps < cell median', TOX & (g < tg)), ('gaps >= cell median', TOX & (g >= tg))):
    tt = np.nanquantile(iv[gm], .5)
    a = gm & (iv <= tt); b = gm & (iv > tt)
    print('  inside %-20s n=%3d PF %.3f  ->  intensity LOW n=%3d PF %.3f | HIGH n=%3d PF %.3f'
          % (glab, int(gm.sum()), pf(r[gm]), int(a.sum()), pf(r[a]), int(b.sum()), pf(r[b])))

print('\n' + '='*230)
print('4. RAISING chg INSIDE THE TOXIC CELL')
print('='*230)
rows = []
for c in (0.08, 0.10, 0.12, 0.15, 0.20):
    m = (v >= 40) & (v < 100) & (g < 2000) & (A.chg.values > c)
    if m.sum() < 25:
        continue
    rows.append(line(m, f'chg > {c*100:g}%'))
print(pd.DataFrame(rows).fillna('').to_string(index=False))
