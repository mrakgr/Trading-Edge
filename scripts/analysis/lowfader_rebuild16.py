"""LowFader REBUILD step 16 (2026-09-05): the DAILY features inside the improved spec
SPEC = volat (40,100] ∧ eff_ewma_10m<-0.7 ∧ lows_600>=40 ∧ rate_600>=0.15 ∧ rr>=1.5. mc=0, PF / PF22, tkd.
chg_kd = entry/(close_mk+div_mk)-1 ; dlow_k = entry/low_mk-1 (NEGATIVE = broke below the k-day low).
Run: python3 -u scripts/analysis/lowfader_rebuild16.py > data/lowfader_rebuild_16.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
df = duckdb.query("""SELECT t.symbol, t.trade_date, t.ret_exit AS ret, t.signal_sec,
  t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg_1d, t.entry_px/NULLIF(t.close_m3+coalesce(t.div_m3,0),0)-1 AS chg_3d, t.entry_px/NULLIF(t.close_m7+coalesce(t.div_m7,0),0)-1 AS chg_7d,
  t.entry_px/NULLIF(l.low_m1,0)-1 AS dlow_1, t.entry_px/NULLIF(l.low_m3,0)-1 AS dlow_3, t.entry_px/NULLIF(l.low_m7,0)-1 AS dlow_7, t.entry_px/NULLIF(l.low_m20,0)-1 AS dlow_20,
  (t.close_m1+coalesce(t.div_m1,0))/NULLIF(t.close_m3+coalesce(t.div_m3,0),0)-1 AS chg_3d_to_m1
  FROM read_parquet('data/lowfader_wl_ewma/*.parquet') t LEFT JOIN 'data/equity/daily_lows_causal.parquet' l ON l.ticker=t.symbol AND l.date=CAST(t.trade_date AS DATE)
  WHERE t.volat_20m > 0.0039 AND t.volat_20m <= 0.010 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.vol_60 >= 1.5*t.vol_0945_tape/15.0""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), tkd=g.tkd.nunique(), tkd22=g.loc[g.year>=2022,'tkd'].nunique(), PF=round(pf(s),2), PF22=round(pf(s2),2) if len(s2) else None, win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def bands(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"SPEC: n={len(df):,} tkd={df.tkd.nunique():,} PF {pf(df.ret):.3f} PF22 {pf(df[df.year>=2022].ret):.3f}")
print('quantiles (%):'); print((df[['chg_1d','chg_3d','chg_7d','dlow_1','dlow_3','dlow_7','dlow_20']].quantile([.1,.25,.5,.75,.9])*100).round(1).to_string())
ec=[-1,-0.3,-0.2,-0.12,-0.08,-0.04,0,0.05,0.15,0.3,10]
for c in ['chg_1d','chg_3d','chg_7d','chg_3d_to_m1']: bands(df,c,ec,'SPEC')
ed=[-1,-0.3,-0.2,-0.12,-0.08,-0.05,-0.02,0,0.03,0.1,0.3,10]
for c in ['dlow_1','dlow_3','dlow_7','dlow_20']: bands(df,c,ed,'SPEC')
print('\n########## 2-D (PF22 / tkd22): dlow_7 rows x chg_3d cols ##########')
m22=df[df.year>=2022].copy(); m22['a']=pd.cut(m22.dlow_7,[-1,-0.2,-0.08,-0.02,0,0.1,10]); m22['c']=pd.cut(m22.chg_3d,[-1,-0.2,-0.08,-0.03,0.05,0.15,10])
print(m22.groupby(['a','c'],observed=True).apply(lambda g: f"{pf(g.ret):.2f}/{g.tkd.nunique()}").unstack().to_string())
print('\n########## 2-D (PF22 / tkd22): dlow_7 rows x chg_1d cols ##########')
m22['c1']=pd.cut(m22.chg_1d,[-1,-0.2,-0.12,-0.08,-0.04,0,10])
print(m22.groupby(['a','c1'],observed=True).apply(lambda g: f"{pf(g.ret):.2f}/{g.tkd.nunique()}").unstack().to_string())
print('\n########## the LowFlyer daily gates inside the spec ##########')
rows=[dict(gate='none', **st(df), **yc(df))]
for lbl,m in [('chg_1d<=-8%',df.chg_1d<=-0.08),('chg_3d>=-3%',df.chg_3d>=-0.03),('chg_3d in[-3,30]',(df.chg_3d>=-0.03)&(df.chg_3d<=0.3)),('chg_3d<-3%',df.chg_3d<-0.03),('chg_7d>=-5%',df.chg_7d>=-0.05),
              ('dlow_7 in[-20,-2]%',(df.dlow_7>=-0.2)&(df.dlow_7<=-0.02)),('dlow_7<-2%',df.dlow_7<-0.02),('dlow_7>=0 (holding above)',df.dlow_7>=0),
              ('chg_1d<=-8 ∧ chg_3d>=-3',(df.chg_1d<=-0.08)&(df.chg_3d>=-0.03)),('chg_1d<=-8 ∧ dlow_7 in[-20,-2]',(df.chg_1d<=-0.08)&(df.dlow_7>=-0.2)&(df.dlow_7<=-0.02))]:
    m=m.fillna(False); rows.append(dict(gate=lbl, **st(df[m]), **yc(df[m])))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
