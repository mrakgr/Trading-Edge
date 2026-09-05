"""LowFader REBUILD step 5 (2026-09-05): the DAILY change features inside the 40-60bp volat band.
chg_kd = entry_px / (close_mk + div_mk) - 1  (causal, docs/price_adjustment.md), k = 1, 3, 7.
Run: python3 -u scripts/analysis/lowfader_rebuild5.py data/lowfader_wl_base > data/lowfader_rebuild_5.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"
VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.006   # volat_20m CEILING (a ceiling like the 1m ATR gate)
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, exit_sec, ret_exit AS ret,
  entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg_1d,
  entry_px/NULLIF(close_m3+coalesce(div_m3,0),0)-1 AS chg_3d,
  entry_px/NULLIF(close_m7+coalesce(div_m7,0),0)-1 AS chg_7d
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique())
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(s): return dict(n=len(s), PF1=round(pf(s)-1,3), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), p5=round(s.quantile(.05)*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret'])-1,2) for y in YEARS}
def mc1(d):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
b1=mc1(df)
print(f"40-{VMAX*1e4:.0f}bp band: {len(df):,} trips"); print('NaN: ' + '  '.join(f"{c} {df[c].isna().mean()*100:.1f}%" for c in ['chg_1d','chg_3d','chg_7d']))
print('quantiles (%):'); print((df[['chg_1d','chg_3d','chg_7d']].quantile([.05,.1,.25,.5,.75,.9,.95])*100).round(1).to_string())
print('rho: 1d-3d %.3f  1d-7d %.3f  3d-7d %.3f' % tuple(df[['chg_1d','chg_3d','chg_7d']].corr().values[[0,0,1],[1,2,2]]))
def bands(col, edges):
    for name,d in [('mc=0',df),('mc=1 control',b1)]:
        d=d.copy(); d['b']=pd.cut(d[col],edges)
        rows=[dict(band='NaN', **st(d[d[col].isna()]['ret']), **yc(d[d[col].isna()]))]+[dict(band=str(k), **st(g['ret']), **yc(g)) for k,g in d.groupby('b',observed=True)]
        print(f'\n--- {name}: {col} bands (40-{VMAX*1e4:.0f}bp) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## A. chg_1d (prior close -> entry: gap + intraday) ##########')
bands('chg_1d',[-1,-0.5,-0.3,-0.2,-0.15,-0.12,-0.10,-0.08,-0.06,-0.04,-0.02,0,0.02,0.05,0.1,0.2,10])
print('\n########## B. chg_3d ##########')
bands('chg_3d',[-1,-0.5,-0.3,-0.2,-0.1,-0.05,-0.03,0,0.03,0.05,0.10,0.15,0.30,0.6,10])
print('\n########## C. chg_7d ##########')
bands('chg_7d',[-1,-0.5,-0.3,-0.2,-0.1,-0.05,0,0.05,0.1,0.2,0.5,1,10])
print('\n########## D. 2-D mc=0: chg_1d rows x chg_3d cols (PF-1 / n) — all years, then 2022+ ##########')
e1=[-1,-0.3,-0.15,-0.08,-0.04,0,10]; e3=[-1,-0.15,-0.03,0.05,0.15,0.30,10]
for name,d in [('ALL',df),('2022+',df[df.year>=2022])]:
    d=d.copy(); d['a']=pd.cut(d.chg_1d,e1); d['c']=pd.cut(d.chg_3d,e3)
    print(f'\n--- {name} ---'); print(d.groupby(['a','c'],observed=True)['ret'].apply(lambda s: f"{pf(s)-1:.2f}/{len(s)}").unstack().to_string())
print('\n########## E. the LowFlyer daily conjunction on 1s: chg_1d<=-8% ∧ chg_3d in [-3,+30] (∧ chg_7d>=-5%), mc=0 + mc=1, by year ##########')
for name,d in [('mc=0',df),('mc=1',b1)]:
    rows=[]
    for lbl,m in [('none',pd.Series(True,index=d.index)),('1d<=-8',d.chg_1d<=-0.08),('1d in[-30,-8]',(d.chg_1d<=-0.08)&(d.chg_1d>=-0.30)),
                  ('3d in[-3,30]',(d.chg_3d>=-0.03)&(d.chg_3d<=0.30)),('1d<=-8 ∧ 3d',(d.chg_1d<=-0.08)&(d.chg_3d>=-0.03)&(d.chg_3d<=0.30)),
                  ('1d[-30,-8] ∧ 3d',(d.chg_1d<=-0.08)&(d.chg_1d>=-0.30)&(d.chg_3d>=-0.03)&(d.chg_3d<=0.30)),
                  ('1d[-30,-8] ∧ 3d ∧ 7d>=-5',(d.chg_1d<=-0.08)&(d.chg_1d>=-0.30)&(d.chg_3d>=-0.03)&(d.chg_3d<=0.30)&(d.chg_7d>=-0.05))]:
        m=m.fillna(False); rows.append(dict(gate=lbl, **st(d[m]['ret']), **yc(d[m])))
    print(f'\n--- {name} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
