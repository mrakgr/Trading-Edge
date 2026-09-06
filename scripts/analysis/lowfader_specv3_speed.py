"""LowFader SPEC v3: replace rr with a SPEED feature? + LowFlyer's -12% depth floor (2026-09-05 late night, user).
Frame = spec v3 without rr (old universe, dv_60>=$1M, volat (50,100], eff<-0.7, lows>=40, rate>=0.15, chg_1d<=-4%, gap<=30).
speed_1m = signal_vwap/vwap_60_prev-1 ; d1m = signal_vwap/hi_60-1 ; 30s twins ; s5/s10 = vs the vwap 5/10 bars ago.
Run: python3 -u scripts/analysis/lowfader_specv3_speed.py > data/lowfader_specv3_speed.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret,
  t.vol_60/NULLIF(t.vol_0945_tape/15.0,0) AS rr, t.signal_vwap/NULLIF(t.vwap_60_prev,0)-1 AS speed_1m, t.signal_vwap/NULLIF(t.hi_60,0)-1 AS d1m,
  t.signal_vwap/NULLIF(t.vwap_30_prev,0)-1 AS speed_30s, t.signal_vwap/NULLIF(t.hi_30,0)-1 AS d30s, t.signal_vwap/NULLIF(t.vwap_5_prev,0)-1 AS s5, t.signal_vwap/NULLIF(t.vwap_10_prev,0)-1 AS s10
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22
  WHERE {CUTW} AND t.volat_20m > 0.005 AND t.volat_20m <= 0.010 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= -0.04
    AND t.gap_adj_60 <= 30 AND t.dollar_vol_60 >= 1e6""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, k):
    ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),dtype=bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
def views(m, label):
    d=df[m]; s2=d[d.year>=2022]; r=dict(spec=label, mc0_tkd22=s2.tkd.nunique(), mc0_PF1_22=round(pf(s2.ret)-1,2), mc0_win22=round((s2.ret>0).mean()*100,1))
    b=mck(d,5); b2=b[b.year>=2022]
    r.update(dict(mc5_trades_yr=round(len(b)/6.63,1), mc5_PF1_22=round(pf(b2.ret)-1,2), mc5_win22=round((b2.ret>0).mean()*100,1), mc5_net22=round(b2.ret.sum()*100), mc5_worst=round(b.ret.min()*100,1), mc5_tail=round((b.ret<-.2).mean()*100,2), **{str(y): round(b[b.year==y].ret.sum()*100) for y in YEARS if y>=2022}))
    return r
print(f"frame (spec v3 minus rr): {len(df):,} trips, {df.tkd.nunique()} tkd, {df[df.year>=2022].tkd.nunique()} tkd22")
print('quantiles (%):'); print((df[['speed_1m','d1m','speed_30s','d30s','s5','s10']].quantile([.05,.25,.5,.75,.95])*100).round(2).to_string())
print('rho with rr:', {c_: round(df[[c_,'rr']].corr().iloc[0,1],3) for c_ in ['speed_1m','d1m','speed_30s','s5']})
rows=[views(np.ones(len(df),dtype=bool),'frame (no rr, no speed)'), views((df.rr>=2).values,'rr>=2 (SPEC v3)'), views((df.rr>=1.5).values,'rr>=1.5')]
for col,ts in [('speed_1m',[-0.01,-0.015,-0.02,-0.03,-0.04]),('d1m',[-0.01,-0.015,-0.02,-0.03,-0.04]),('speed_30s',[-0.005,-0.01,-0.015,-0.02]),('s5',[-0.003,-0.005,-0.01,-0.02]),('s10',[-0.005,-0.01,-0.02])]:
    for t in ts: rows.append(views((df[col]<t).fillna(False).values, f'{col}<{t*100:g}%'))
for t in [-0.01,-0.02]: rows.append(views(((df.speed_1m<t)&(df.d1m<t)).fillna(False).values, f'PAIR<{t*100:g}%'))
rows.append(views(((df.rr>=2)&(df.speed_1m<-0.02)).fillna(False).values,'rr>=2 ∧ speed_1m<-2%')); rows.append(views(((df.rr>=2)&(df.s5<-0.005)).fillna(False).values,'rr>=2 ∧ s5<-0.5%'))
print('\n--- rr vs SPEED as the loudness gate (mc=0 | mc=5 MOC; year cols = mc=5 net %) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n--- speed_1m ladder inside rr>=2 (spec v3) ---')
d=df[df.rr>=2].copy(); d['b']=pd.cut(d.speed_1m,[-1,-0.12,-0.06,-0.04,-0.03,-0.02,-0.015,-0.01,-0.005,0,1])
rows=[]
for k,g in d.groupby('b',observed=True):
    s2=g[g.year>=2022]; b=mck(g,5); b2=b[b.year>=2022]
    rows.append(dict(band=str(k), n=len(g), tkd22=s2.tkd.nunique(), mc0_PF1_22=round(pf(s2.ret)-1,2) if len(s2) else None, mc0_win22=round((s2.ret>0).mean()*100,1) if len(s2) else None, mc5_n=len(b), mc5_PF1_22=round(pf(b2.ret)-1,2) if len(b2) else None, mc5_net22=round(b2.ret.sum()*100), worst=round(g.ret.min()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print("\n--- LowFlyer's -12% depth floor on spec v3 (flush_1m = speed_1m here) ---")
d=df[df.rr>=2]; print(f"spec v3 trips with speed_1m < -12%: {int((d.speed_1m<-0.12).sum())} of {len(d)} ({d[d.speed_1m<-0.12].tkd.nunique()} tkd); their ret: n={int((d.speed_1m<-0.12).sum())}, PF-1 {pf(d[d.speed_1m<-0.12].ret)-1 if (d.speed_1m<-0.12).any() else float('nan'):.2f}, worst {d[d.speed_1m<-0.12].ret.min()*100 if (d.speed_1m<-0.12).any() else float('nan'):.1f}")
rows=[views((df.rr>=2).values,'spec v3'), views(((df.rr>=2)&(df.speed_1m>=-0.12)).fillna(False).values,'spec v3 ∧ speed_1m>=-12%'), views(((df.rr>=2)&(df.speed_1m>=-0.06)).fillna(False).values,'spec v3 ∧ speed_1m>=-6%')]
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
