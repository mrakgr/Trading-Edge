"""LowFader REBUILD step 12 (2026-09-05, user): distance to the PRIOR 1d/3d/7d/20d LOWS instead of the % changes.
low_mk = min(low over the k prior sessions) in D's raw scale (total-return-index convention, data/equity/daily_lows_causal.parquet).
dlow_k = entry_px / low_mk - 1: NEGATIVE = the session low has broken below the k-day low; POSITIVE = it is holding above it.
mc=0, 40-100bp, PF / PF22, tkd counts, year cols.
Run: python3 -u scripts/analysis/lowfader_rebuild12.py data/lowfader_wl_base 0.010 > data/lowfader_rebuild_12.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
df = duckdb.query(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.ret_exit AS ret, t.eff_10m, t.eff_20m,
  t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg_1d, t.entry_px/NULLIF(t.close_m3+coalesce(t.div_m3,0),0)-1 AS chg_3d,
  t.entry_px/NULLIF(l.low_m1,0)-1 AS dlow_1, t.entry_px/NULLIF(l.low_m3,0)-1 AS dlow_3, t.entry_px/NULLIF(l.low_m7,0)-1 AS dlow_7, t.entry_px/NULLIF(l.low_m20,0)-1 AS dlow_20
  FROM read_parquet('{DIRP}/*.parquet') t LEFT JOIN 'data/equity/daily_lows_causal.parquet' l ON l.ticker=t.symbol AND l.date=CAST(t.trade_date AS DATE)
  WHERE t.volat_20m > 0.0039 AND t.volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def bands(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"40-{VMAX*1e4:.0f}bp base: {len(df):,} trips {df.tkd.nunique():,} tkd  PF {pf(df.ret):.3f} PF22 {pf(df[df.year>=2022].ret):.3f}")
print('NaN: ' + '  '.join(f"{c} {df[c].isna().mean()*100:.1f}%" for c in ['dlow_1','dlow_3','dlow_7','dlow_20']))
print('quantiles (%):'); print((df[['dlow_1','dlow_3','dlow_7','dlow_20','chg_1d','chg_3d']].quantile([.05,.1,.25,.5,.75,.9,.95])*100).round(1).to_string())
print('rho:'); print(df[['dlow_1','dlow_3','dlow_7','dlow_20','chg_1d','chg_3d']].corr().round(3).to_string())
e=[-1,-0.5,-0.3,-0.2,-0.15,-0.1,-0.07,-0.05,-0.03,-0.01,0,0.01,0.03,0.05,0.1,0.2,10]
print('\n########## A. ladders on the base ##########')
for c in ['dlow_1','dlow_3','dlow_7','dlow_20']: bands(df,c,e,'base')
print('\n########## B. 2-D mc=0: dlow_3 rows x dlow_7 cols — PF22 / n22 (2022+) ##########')
e2=[-1,-0.2,-0.1,-0.05,0,0.05,10]; d=df[df.year>=2022].copy(); d['a']=pd.cut(d.dlow_3,e2); d['c']=pd.cut(d.dlow_7,e2)
print(d.groupby(['a','c'],observed=True)['ret'].apply(lambda s: f"{pf(s):.2f}/{len(s)}").unstack().to_string())
print('\n########## C. the working intraday spec (eff_10m<-0.7 ∧ (eff_20m<-0.3|NaN)) x the lows ##########')
spec=((df.eff_10m<-0.7)&((df.eff_20m<-0.3)|df.eff_20m.isna())).fillna(False).values; ds=df[spec]
print(f"spec: n={len(ds):,} tkd={ds.tkd.nunique():,} PF {pf(ds.ret):.3f} PF22 {pf(ds[ds.year>=2022].ret):.3f}")
for c in ['dlow_3','dlow_7']: bands(ds,c,e,'spec')
print('\nDONE')
