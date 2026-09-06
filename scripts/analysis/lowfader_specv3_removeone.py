"""LowFader SPEC v3 remove-one (2026-09-05 late night, user: 'try removing some of the old gates starting with eff').
Views: mc=0 sampler, mc=1 first bar, mc=5 averaging down (MOC). Old universe (= n_bars_1s>=200, dv_0945>=$2M, barnum>=22).
Run: python3 -u scripts/analysis/lowfader_specv3_removeone.py > data/lowfader_specv3_removeone.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
# pull the OLD universe with the two hard-to-relax gates kept loose enough to test: volat > 39bp (floor tested), everything else as columns
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.aux_hi_600_px, t.aux_hi_600_sec,
  t.volat_20m, t.eff_ewma_10m, t.lows_since_first_low_600 AS lows, t.lows_since_first_low_600/NULLIF(t.bars_since_first_low_600,0) AS rate,
  t.vol_60/NULLIF(t.vol_0945_tape/15.0,0) AS rr, t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg_1d, t.gap_adj_60, t.dollar_vol_60
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22
  WHERE {CUTW} AND t.volat_20m > 0.0039 AND t.volat_20m <= 0.010 AND t.dollar_vol_60 >= 1e6""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, x, k):
    ss=d['signal_sec'].to_numpy(); es=d[x].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),dtype=bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
G_={'volat>50bp':df.volat_20m>0.005, 'eff_ewma_10m<-0.7':df.eff_ewma_10m<-0.7, 'lows_600>=40':df.lows>=40, 'rate_600>=0.15':df.rate>=0.15, 'rr>=2':df.rr>=2, 'chg_1d<=-4%':df.chg_1d<=-0.04, 'gap_adj_60<=30':df.gap_adj_60<=30}
G_={k:v.fillna(False).values for k,v in G_.items()}
def views(m, label):
    d=df[m]; s2=d[d.year>=2022]; r=dict(spec=label, mc0_tkd22=s2.tkd.nunique(), mc0_PF1_22=round(pf(s2.ret)-1,2), mc0_net22=round(s2.ret.sum()*100))
    for k,lab in [(1,'mc1'),(5,'mc5')]:
        b=mck(d,'exit_sec',k); b2=b[b.year>=2022]
        r.update({f'{lab}_trades_yr':round(len(b)/6.63,1), f'{lab}_PF1_22':round(pf(b2.ret)-1,2), f'{lab}_win22':round((b2.ret>0).mean()*100,1), f'{lab}_net22':round(b2.ret.sum()*100), f'{lab}_worst':round(b.ret.min()*100,1), f'{lab}_tail':round((b.ret<-.2).mean()*100,2)})
        if k==5: r.update({str(y): round(b[b.year==y].ret.sum()*100) for y in YEARS if y>=2022})
    return r
full=np.logical_and.reduce(list(G_.values()))
rows=[views(full,'SPEC v3 (full)')]
for k in G_:
    m=np.logical_and.reduce([v for kk,v in G_.items() if kk!=k]); rows.append(views(m,f'  drop {k}'))
rows.append(views(np.logical_and.reduce([v for kk,v in G_.items() if kk not in ('eff_ewma_10m<-0.7','lows_600>=40','rate_600>=0.15')]),'  drop eff + lows + rate (the leg block)'))
rows.append(views(np.logical_and.reduce([v for kk,v in G_.items() if kk not in ('eff_ewma_10m<-0.7','chg_1d<=-4%')]),'  drop eff + chg_1d'))
print('--- SPEC v3 REMOVE-ONE (old universe, dv_60>=$1M kept); mc=5 = MOC averaging down; year cols = mc=5 net % ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n--- eff_ewma_10m ladder INSIDE the rest of spec v3 (mc=0 | mc=5 MOC) ---')
rest=np.logical_and.reduce([v for kk,v in G_.items() if kk!='eff_ewma_10m<-0.7']); d=df[rest].copy(); d['b']=pd.cut(d.eff_ewma_10m,[-1.01,-0.9,-0.8,-0.7,-0.6,-0.5,-0.3,0,1.01])
rows=[]
for k,g in d.groupby('b',observed=True):
    s2=g[g.year>=2022]; b=mck(g,'exit_sec',5); b2=b[b.year>=2022]
    rows.append(dict(band=str(k), n=len(g), tkd22=s2.tkd.nunique(), mc0_PF1_22=round(pf(s2.ret)-1,2), mc0_win22=round((s2.ret>0).mean()*100,1), mc5_n=len(b), mc5_PF1_22=round(pf(b2.ret)-1,2), mc5_net22=round(b2.ret.sum()*100), worst=round(g.ret.min()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
rows=[]
for t in [-0.9,-0.8,-0.7,-0.6,-0.5,-0.4,-0.3,-0.2,0.0]:
    m=rest&(df.eff_ewma_10m<t).fillna(False).values; rows.append(views(m, f'eff_ewma_10m<{t:g}'))
print('\n--- eff floor sweep on the rest of spec v3 ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
