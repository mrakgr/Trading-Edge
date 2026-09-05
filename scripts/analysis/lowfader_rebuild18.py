"""LowFader REBUILD step 18 (2026-09-05, user): the chg_1d > -4% cell as a SHORT (continuation) — sign-flipped returns, MOC.
Frame: volat (40,100] ∧ eff_ewma_10m<-0.7 ∧ lows_600>=40 ∧ rate_600>=0.15 (rr NOT floored here — rr is a dimension).
Run: python3 -u scripts/analysis/lowfader_rebuild18.py > data/lowfader_rebuild_18.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
df = duckdb.query("""SELECT t.symbol, t.trade_date, t.signal_sec, -t.ret_exit AS ret, t.volat_20m, t.vol_60/NULLIF(t.vol_0945_tape/15.0,0) AS rr, t.gap_adj_60, t.halts_today,
  t.lows_since_first_low_600 AS lows, t.lows_since_first_low_600/NULLIF(t.bars_since_first_low_600,0) AS rate, t.secs_since_first_low/60.0 AS leg_min, t.eff_ewma_10m,
  t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg_1d, t.entry_px/NULLIF(t.close_m3+coalesce(t.div_m3,0),0)-1 AS chg_3d, t.entry_px/NULLIF(t.close_m7+coalesce(t.div_m7,0),0)-1 AS chg_7d,
  t.entry_px/NULLIF(l.low_m1,0)-1 AS dlow_1, t.entry_px/NULLIF(l.low_m7,0)-1 AS dlow_7, t.entry_px/NULLIF(l.low_m20,0)-1 AS dlow_20,
  t.pct_chg_open AS chg_open
  FROM read_parquet('data/lowfader_wl_ewma/*.parquet') t LEFT JOIN 'data/equity/daily_lows_causal.parquet' l ON l.ticker=t.symbol AND l.date=CAST(t.trade_date AS DATE)
  WHERE t.volat_20m > 0.0039 AND t.volat_20m <= 0.010 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 > -0.04""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df['hour']=df.signal_sec/3600
df['gap_open']=(1+df.chg_1d)/(1+df.chg_open)-1
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
print(f"SHORT frame (chg_1d > -4%, no rr floor): n={len(df):,} tkd={df.tkd.nunique():,} PF {pf(df.ret):.3f} PF22 {pf(df[df.year>=2022].ret):.3f} (returns are SHORT: -ret_exit)")
for name,d in [('all rr',df),('rr>=1.5',df[df.rr>=1.5]),('rr>=2',df[df.rr>=2])]:
    print(f"\n===== {name}: n={len(d):,} tkd={d.tkd.nunique():,} PF {pf(d.ret):.3f} PF22 {pf(d[d.year>=2022].ret):.3f} =====")
    bands(d,'chg_1d',[-0.04,-0.02,0,0.02,0.05,0.1,0.2,10],name)
d=df[df.rr>=1.5]
print('\n########## inside rr>=1.5 (the working frame) ##########')
bands(d,'rr',[0,2,3,4,8,1e9],'rr>=1.5')
bands(d,'volat_20m',[0.0039,0.005,0.006,0.008,0.010],'rr>=1.5')
bands(d,'dlow_7',[-1,-0.05,-0.02,0,0.03,0.1,0.3,10],'rr>=1.5')
bands(d,'dlow_20',[-1,-0.05,0,0.05,0.15,0.3,10],'rr>=1.5')
bands(d,'chg_3d',[-1,-0.1,-0.04,0,0.05,0.15,10],'rr>=1.5')
bands(d,'chg_7d',[-1,-0.1,-0.04,0,0.05,0.15,0.4,10],'rr>=1.5')
bands(d,'gap_open',[-1,-0.04,-0.02,0,0.02,0.05,10],'rr>=1.5')
bands(d,'chg_open',[-1,-0.06,-0.04,-0.02,0,10],'rr>=1.5')
bands(d,'hour',[9.7,10,10.5,11,12,13,14,15,16],'rr>=1.5')
bands(d,'lows',[39,60,100,200,1e6],'rr>=1.5')
bands(d,'rate',[0.14,0.2,0.3,1.01],'rr>=1.5')
bands(d,'leg_min',[0,10,20,30,60,1e6],'rr>=1.5')
bands(d,'gap_adj_60',[-1,5,10,20,30,40,60],'rr>=1.5')
bands(d,'eff_ewma_10m',[-1.01,-0.9,-0.8,-0.7],'rr>=1.5')
print('\nDONE')
