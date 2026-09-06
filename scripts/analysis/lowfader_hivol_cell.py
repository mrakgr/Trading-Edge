"""LowFader: the volat_20m > 100bp cell (2026-09-06, user): profitable but tail-heavy; test the channel-high covers (5m..3h) for risk
control, and the sizing that trades it at reduced exposure vs an equivalent-PF cell. Frame = SPEC v4 without the ceiling, whole-tape
corpus joined to the old universe (barnum>=22), mc=1 = first qualifying bar per tkd. Run: python3 -u scripts/analysis/lowfader_hivol_cell.py > data/lowfader_hivol_cell.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320)
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
cols=[r[0] for r in c.execute(f"DESCRIBE SELECT * FROM read_parquet('{G[0]}')").fetchall()]
AUX=[n for n in [300,600,1200,2400,3600,7200,10800] if f'aux_hi_{n}_px' in cols]; print('covers available:', AUX)
SPEC="""t.volat_20m > 0.003 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40 AND t.lows_since_first_low_120 >= 30
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.vol_60 >= 2.0*t.vol_0945_tape/15.0 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= -0.04
    AND t.gap_adj_60 <= 60 AND t.dollar_vol_60 >= 1e6 AND t.chg_since_run_first_low <= -0.002 AND t.dv_0945_tape < 2e7"""
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.volat_20m, t.gap_adj_60 AS gap, t.chg_since_run_first_low AS crf, t.vol_60/(t.vol_0945_tape/15.0) AS rr,
    {', '.join(f't.aux_hi_{n}_px AS hi{n}_px, t.aux_hi_{n}_sec AS hi{n}_sec' for n in AUX)}
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22 WHERE {CUTW} AND {SPEC}""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
b=df.groupby('tkd',sort=False).head(1).copy(); b['vb']=b.volat_20m*1e4
for n in AUX:
    f=b[f'hi{n}_px'].notna(); b[f'ret_{n}']=np.where(f, b[f'hi{n}_px']/b.entry_px-1, b.ret)   # cover at the N-bar channel high, else MOC
b['ret_MOC']=b.ret
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
hi=b[b.vb>100]; lo=b[b.vb<=100]
print(f"SPEC v4 frame without the ceiling: {len(b)} trades; <=100bp {len(lo)} (the v4 book), >100bp {len(hi)} ({len(hi)/6.63:.1f}/yr)")
def st(g, col, lbl):
    g2=g[g.year>=2022]; r=g[col]; r2=g2[col]
    return dict(cell=lbl, exit=col.replace('ret_',''), n=len(g), PF=round(pf(r),2), PF22=round(pf(r2),2) if len(g2) else None, win=round((r>0).mean()*100), avg=round(r.mean()*100,2), avg22=round(r2.mean()*100,2) if len(g2) else None, net22=round(r2.sum()*100), worst=round(r.min()*100,1), p5=round(r.quantile(.05)*100,1), tail10=round((r<-.10).mean()*100,1), tail20=round((r<-.20).mean()*100,1), sd=round(r.std()*100,1))
print('\n########## 1. the >100bp cell vs the v4 book, per exit ##########')
rows=[]
for col in ['ret_MOC']+[f'ret_{n}' for n in AUX]:
    rows.append(st(hi,col,'>100bp')); 
print(pd.DataFrame(rows).to_string(index=False)); rows=[]
for col in ['ret_MOC']+[f'ret_{n}' for n in AUX]: rows.append(st(lo,col,'<=100bp (v4)'))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 2. the >100bp cell by volat band (MOC | 10m | 20m | 1h) ##########')
d=hi.copy(); d['band']=pd.cut(d.vb,[100,125,150,200,300,1e9]); rows=[]
for k,g in d.groupby('band',observed=True):
    if len(g)<5: continue
    r=dict(band=str(k), n=len(g), n22=int((g.year>=2022).sum()))
    for col in ['ret_MOC']+[f'ret_{n}' for n in AUX if n in (600,1200,3600)]:
        e=col.replace('ret_',''); r[f'PF_{e}']=round(pf(g[col]),2); r[f'net22_{e}']=round(g[g.year>=2022][col].sum()*100); r[f'worst_{e}']=round(g[col].min()*100,1)
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 3. year table, >100bp cell: MOC vs 10m vs 20m vs 1h ##########'); rows=[]
for y in sorted(hi.year.unique()):
    g=hi[hi.year==y]; r=dict(year=y, n=len(g))
    for col in ['ret_MOC']+[f'ret_{n}' for n in AUX if n in (600,1200,3600)]:
        e=col.replace('ret_',''); r[f'PF_{e}']=round(pf(g[col]),2); r[f'net_{e}']=round(g[col].sum()*100); r[f'worst_{e}']=round(g[col].min()*100,1)
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 4. the >100bp cell\'s worst trades (MOC) and what each cover would have done ##########')
w=hi.nsmallest(8,'ret')[['symbol','trade_date','volat_20m','rr','gap','ret']+[f'ret_{n}' for n in AUX if n in (300,600,1200,3600)]].copy()
for col in ['ret']+[f'ret_{n}' for n in AUX if n in (300,600,1200,3600)]: w[col]=(w[col]*100).round(1)
w['volat_20m']=(w.volat_20m*1e4).round(0); w['rr']=w.rr.round(1); print(w.to_string(index=False))
print('\n########## 5. SIZING the cell: PF-based weight vs tail-matched weight (relative to the v4 book at MOC) ##########')
# PF-based: min(PF-1,10)/max as in the ladder, with the v4 book's PF-1 as the reference cell. Tail-matched: scale so the cell's p5 (or worst) loss per unit equals the book's.
ref=lo['ret_MOC']; rows=[]
for col in ['ret_MOC']+[f'ret_{n}' for n in AUX if n in (300,600,1200,3600)]:
    r=hi[col]; w_pf=min(pf(r)-1,10)/min(pf(ref)-1,10); w_p5=ref.quantile(.05)/r.quantile(.05); w_worst=ref.min()/r.min(); w_sd=ref.std()/r.std()
    rows.append(dict(exit=col.replace('ret_',''), cell_PF1=round(pf(r)-1,2), book_PF1=round(pf(ref)-1,2), w_PF=round(w_pf,2), w_p5=round(w_p5,2), w_worst=round(w_worst,2), w_sd=round(w_sd,2), w_PF_x_p5=round(w_pf*min(w_p5,1),2),
                     cell_net22_at_wPFxp5=round(hi[hi.year>=2022][col].sum()*100*w_pf*min(w_p5,1)), cell_sized_worst=round(r.min()*100*w_pf*min(w_p5,1),1), book_worst=round(ref.min()*100,1), book_p5=round(ref.quantile(.05)*100,1), cell_p5=round(r.quantile(.05)*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
