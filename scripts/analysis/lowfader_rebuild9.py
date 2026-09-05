"""LowFader REBUILD step 9 (2026-09-05): the efficiency gates. eff_10m < -0.7 and eff_20m < -0.3 (both fail NaN).
Substitution test: does eff_20m < -0.3 do anything INSIDE eff_10m < -0.7? mc=0, 40-100bp, PF / PF22, tkd counts.
Run: python3 -u scripts/analysis/lowfader_rebuild9.py data/lowfader_wl_base 0.010 > data/lowfader_rebuild_9.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, ret_exit AS ret, eff_20m, eff_10m,
  entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg_1d
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
e10=(df.eff_10m<-0.7).fillna(False).values; e20=(df.eff_20m<-0.3).fillna(False).values; c1=(df.chg_1d<=-0.08).fillna(False).values
e20_or_nan=((df.eff_20m<-0.3)|df.eff_20m.isna()).values
def table(base, name):
    rows=[]
    for lbl,m in [('base',base),('+ eff_10m<-0.7',base&e10),('+ eff_20m<-0.3',base&e20),('+ both',base&e10&e20),
                  ('+ eff_10m<-0.7 ∧ (eff_20m<-0.3 OR NaN)',base&e10&e20_or_nan),('+ eff_10m<-0.7 ∧ eff_20m NaN',base&e10&df.eff_20m.isna().values),
                  ('+ eff_10m<-0.7 ∧ eff_20m>=-0.3',base&e10&(df.eff_20m>=-0.3).fillna(False).values)]:
        rows.append(dict(spec=lbl, **st(df[m]), **yc(df[m])))
    print(f'\n--- {name} ---'); print(pd.DataFrame(rows).to_string(index=False))
    d=df[base&e10].copy(); d['b']=pd.cut(d.eff_20m,[-1.01,-0.7,-0.5,-0.4,-0.3,-0.2,-0.1,0,1.01])
    rows=[dict(band='NaN', **st(d[d.eff_20m.isna()]), **yc(d[d.eff_20m.isna()]))]+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: eff_20m ladder INSIDE eff_10m<-0.7 ---'); print(pd.DataFrame(rows).to_string(index=False))
    d=df[base&e20].copy(); d['b']=pd.cut(d.eff_10m,[-1.01,-0.9,-0.8,-0.7,-0.6,-0.5,-0.3,0,1.01])
    rows=[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: eff_10m ladder INSIDE eff_20m<-0.3 ---'); print(pd.DataFrame(rows).to_string(index=False))
table(np.ones(len(df),dtype=bool), '40-100bp base')
table(c1, '40-100bp ∧ chg_1d<=-8%')
print('\nDONE')
