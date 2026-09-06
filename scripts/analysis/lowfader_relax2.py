"""LowFader RELAXATION study, pass 2 (2026-09-06): (1) eff bands inside FINAL-minus-eff; (2) the MARGINAL trades each relaxation
adds (variant minus FINAL) judged on their own; (3) TAIL-GUARDED greedy expansions (worst >= -20 and 2022+ tail(<-10%) <= 1.5%)
at PF22 floors 4 / 3 / 2.5; (4) year tables of the endpoints. Same superset as lowfader_relax.py.
Run: python3 -u scripts/analysis/lowfader_relax2.py > data/lowfader_relax2.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320); pd.set_option('display.max_rows', 500)
exec(open('scripts/analysis/lowfader_relax.py').read().split("print('\\n########## A.")[0].split('c=duckdb.connect')[0])  # imports only
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
SUP="""t.volat_20m > 0.003 AND (t.eff_ewma_10m < 0 OR t.eff_ewma_10m IS NULL) AND t.lows_since_first_low_600 >= 20 AND t.lows_since_first_low_120 >= 10
    AND t.vol_60 >= 1.0*t.vol_0945_tape/15.0 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= 0.0
    AND t.gap_adj_60 <= 60 AND t.dollar_vol_60 >= 2.5e5 AND t.chg_since_run_first_low <= -0.001 AND t.dv_0945_tape < 2e7"""
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.volat_20m, t.eff_ewma_10m AS eff, t.lows_since_first_low_600 AS l600,
    t.bars_since_first_low_600 AS b600, t.lows_since_first_low_120 AS l120, t.vol_60/(t.vol_0945_tape/15.0) AS rr, t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg1d,
    t.gap_adj_60 AS gap, t.dollar_vol_60 AS dv60, t.chg_since_run_first_low AS crf, t.dv_0945_tape AS dv0945
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22
  WHERE {CUTW} AND {SUP}""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
df['rate']=df.l600/df.b600.replace(0,np.nan)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def first(d): return d.groupby('tkd', sort=False).head(1)
BASE=dict(volat_lo=0.005, volat_hi=0.010, eff=-0.7, l600=40, rate=0.15, l120=30, rr=2.0, chg1d=-0.04, gap=30, dv60=1e6, crf=-0.002)
def mask(p):
    m=(df.volat_20m>p['volat_lo'])&(df.volat_20m<=p['volat_hi'])&(df.l600>=p['l600'])&(df.l120>=p['l120'])&(df.rr>=p['rr'])&(df.chg1d<=p['chg1d'])&(df.gap<=p['gap'])&(df.dv60>=p['dv60'])&(df.crf<=p['crf'])
    if p['eff'] is not None: m&=(df.eff<p['eff'])
    if p['rate'] is not None: m&=(df.rate>=p['rate'])
    return m
def st(b, lbl):
    b2=b[b.year>=2022]
    return dict(variant=lbl, trades=len(b), tr_yr=round(len(b)/6.63,1), tkd22=len(b2), PF=round(pf(b.ret),2) if len(b) else None, PF22=round(pf(b2.ret),2) if len(b2) else None, win22=round((b2.ret>0).mean()*100) if len(b2) else None, avg22=round(b2.ret.mean()*100,2) if len(b2) else None, net22=round(b2.ret.sum()*100), worst=round(b.ret.min()*100,1) if len(b) else None, tail22=round((b2.ret<-0.1).mean()*100,1) if len(b2) else None)
def row(lbl,p): return st(first(df[mask(p)]), lbl)
fin=first(df[mask(BASE)]); fin_tkd=set(fin.tkd)
print('########## 1. eff bands inside FINAL-minus-eff (mc=1 = first qualifying signal per tkd within the band) ##########')
d=df[mask({**BASE,'eff':None})].copy(); d['b']=pd.cut(d.eff,[-1.01,-0.9,-0.8,-0.7,-0.6,-0.5,-0.4,-0.3,-0.2,-0.1,0]); rows=[]
for k,g in d.groupby('b',observed=True):
    if len(g): rows.append(st(first(g), str(k)))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 2. the MARGINAL trades each relaxation ADDS (variant tkds not in FINAL), judged on their own ##########')
rows=[]
for k,vals in [('eff',[-0.5,-0.3]),('l600',[30]),('l120',[20,10]),('rate',[0.10,None]),('rr',[1.5,1.0]),('chg1d',[0.0]),('gap',[45,60]),('dv60',[5e5,2.5e5]),('volat_lo',[0.004,0.003]),('volat_hi',[1.0]),('crf',[-0.001])]:
    for v in vals:
        b=first(df[mask({**BASE,k:v})]); add=b[~b.tkd.isin(fin_tkd)]; rows.append(st(add, f'ADDED by {k} -> {v}'))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 3. TAIL-GUARDED greedy: maximize 2022+ trades s.t. PF22 >= floor AND worst >= -20 AND tail22 <= 1.5% ##########')
STEPS={'eff':[-0.7,-0.5,-0.3,None],'l600':[40,30],'l120':[30,20,10],'rate':[0.15,0.10,None],'rr':[2.0,1.5,1.0],'chg1d':[-0.04,-0.02,0.0],'gap':[30,45,60],'dv60':[1e6,5e5,2.5e5],'volat_lo':[0.005,0.004,0.003],'volat_hi':[0.010,1.0],'crf':[-0.002,-0.001]}
ends={}
for floor in [4.0,3.0,2.5]:
    cur=dict(BASE); hist=[row('FINAL v3.1',cur)]
    for step in range(14):
        best=None
        for k,ladder in STEPS.items():
            i=ladder.index(cur[k])
            if i+1>=len(ladder): continue
            cand={**cur,k:ladder[i+1]}; r=row(f'+ {k} -> {ladder[i+1]}',cand)
            if r['PF22'] is not None and r['PF22']>=floor and r['worst']>=-20 and r['tail22']<=1.5 and (best is None or r['tkd22']>best[1]['tkd22']): best=(cand,r)
        if best is None: break
        cur=best[0]; hist.append(best[1])
    print(f'\n--- floor PF22 >= {floor} ---'); print(pd.DataFrame(hist).to_string(index=False)); print('endpoint:', {k:v for k,v in cur.items() if v!=BASE[k]}); ends[floor]=cur
print('\n########## 4. YEAR tables of the endpoints (mc=1 MOC) ##########')
for floor,cur in ends.items():
    b=first(df[mask(cur)]); print(f'\n--- endpoint PF22 >= {floor}: {len(b)} trades ({len(b)/6.63:.1f}/yr) ---')
    print(b.groupby('year').agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), net=('ret',lambda s: round(s.sum()*100)), win=('ret',lambda s: round((s>0).mean()*100)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1))).to_string())
print('\nDONE')
