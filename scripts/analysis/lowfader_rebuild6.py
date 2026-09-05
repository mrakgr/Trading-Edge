"""LowFader REBUILD step 6 (2026-09-05): marginal vs CONDITIONAL — build LowFlyer's spec up in its own order
(intraday gates first, then the daily gates) on the volat-ceilinged base, and remove-one from the full spec.
No float. PF shown as PF (not PF-1) to match the 1m doc.
Run: python3 -u scripts/analysis/lowfader_rebuild6.py data/lowfader_wl_base 0.010 > data/lowfader_rebuild_6.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"
VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, exit_sec, ret_exit AS ret, volat_20m,
  entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg_1d, entry_px/NULLIF(close_m3+coalesce(div_m3,0),0)-1 AS chg_3d,
  entry_px/NULLIF(close_m7+coalesce(div_m7,0),0)-1 AS chg_7d, signal_vwap/NULLIF(vwap_60_prev,0)-1 AS flush_1m,
  signal_vwap/NULLIF(vwap_1200,0)-1 AS chg_20m, vol_60/NULLIF(vol_60_prior_max,0) AS vol_vs_high
  FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique())
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(s): return dict(n=len(s), PF=round(pf(s),2), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def mc1(d):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
G = [('flush_1m<=-0.7%', df.flush_1m<=-0.007), ('flush_1m>=-12%', df.flush_1m>=-0.12), ('vol_vs_high>=0.9', df.vol_vs_high>=0.9),
     ('chg_20m<=-3%', df.chg_20m<=-0.03), ('chg_1d<=-8%', df.chg_1d<=-0.08), ('chg_3d in[-3,30]', (df.chg_3d>=-0.03)&(df.chg_3d<=0.30)), ('chg_7d>=-5%', df.chg_7d>=-0.05)]
G = [(k, v.fillna(False).values) for k,v in G]
print(f"40-{VMAX*1e4:.0f}bp base: {len(df):,} trips")
print('\n########## A. CUMULATIVE build-up in LowFlyer\'s order (PF, year cols = PF) ##########')
for view in ['mc=0','mc=1']:
    rows=[]; m=np.ones(len(df),dtype=bool)
    d0=df if view=='mc=0' else None
    for k,v in [('none',np.ones(len(df),dtype=bool))]+G:
        m=m&v; d=df[m]; d=d if view=='mc=0' else mc1(d)
        rows.append(dict(step=('+ '+k if k!='none' else k), **st(d['ret']), **yc(d)))
    print(f'\n--- {view} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## B. REMOVE-ONE from the full spec (PF) ##########')
full=np.logical_and.reduce([v for _,v in G])
for view in ['mc=0','mc=1']:
    d=df[full]; d=d if view=='mc=0' else mc1(d); rows=[dict(gate='FULL', **st(d['ret']), **yc(d))]
    for k,_ in G:
        m=np.logical_and.reduce([v for kk,v in G if kk!=k]); d=df[m]; d=d if view=='mc=0' else mc1(d); rows.append(dict(gate='drop '+k, **st(d['ret']), **yc(d)))
    print(f'\n--- {view} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## C. the daily bands INSIDE the intraday-gated book (flush band ∧ vol_vs_high ∧ chg_20m), mc=1, PF ##########')
intra=np.logical_and.reduce([v for k,v in G if k in ('flush_1m<=-0.7%','flush_1m>=-12%','vol_vs_high>=0.9','chg_20m<=-3%')]); di=mc1(df[intra]).copy()
print(f'intraday-gated mc=1 n={len(di):,}  PF {pf(di.ret):.2f}')
for col,edges in [('chg_1d',[-1,-0.3,-0.2,-0.15,-0.1,-0.08,-0.05,0,10]),('chg_3d',[-1,-0.3,-0.15,-0.03,0.05,0.15,0.3,10]),('chg_7d',[-1,-0.3,-0.15,-0.05,0.05,0.2,0.5,10])]:
    di['b']=pd.cut(di[col],edges); print(f'\n--- {col} ---'); print(pd.DataFrame([dict(band=str(k), **st(g['ret']), **yc(g)) for k,g in di.groupby('b',observed=True)]).to_string(index=False))
print('\nDONE')
