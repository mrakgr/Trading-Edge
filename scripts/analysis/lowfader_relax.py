"""LowFader RELAXATION study (2026-09-06, user): with crf in place, loosen the FINAL v3.1 gates one at a time (and from an
eff<-0.3 base) to find a PF~2 system with many more trades. Whole-tape corpus joined to the old universe (barnum>=22), mc=1 MOC,
first qualifying signal per tkd, full size. Superset pulled once, variants filtered in pandas.
Run: python3 -u scripts/analysis/lowfader_relax.py > data/lowfader_relax.log
"""
import numpy as np, pandas as pd, duckdb, itertools
pd.set_option('display.width', 320); pd.set_option('display.max_rows', 500)
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
SUP="""t.volat_20m > 0.003 AND (t.eff_ewma_10m < 0 OR t.eff_ewma_10m IS NULL) AND t.lows_since_first_low_600 >= 20 AND t.lows_since_first_low_120 >= 10
    AND t.vol_60 >= 1.0*t.vol_0945_tape/15.0 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= 0.0
    AND t.gap_adj_60 <= 60 AND t.dollar_vol_60 >= 2.5e5 AND t.chg_since_run_first_low <= -0.001 AND t.dv_0945_tape < 2e7"""
df=c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.volat_20m, t.eff_ewma_10m AS eff, t.lows_since_first_low_600 AS l600,
    t.bars_since_first_low_600 AS b600, t.lows_since_first_low_120 AS l120, t.vol_60/(t.vol_0945_tape/15.0) AS rr, t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg1d,
    t.gap_adj_60 AS gap, t.dollar_vol_60 AS dv60, t.chg_since_run_first_low AS crf, t.dv_0945_tape AS dv0945, t.aux_hi_600_px, t.aux_hi_600_sec
  FROM read_parquet({G}, filename=true) t JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22
  WHERE {CUTW} AND {SUP}""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
df['rate']=df.l600/df.b600.replace(0,np.nan)
print(f"superset: {len(df):,} trips / {df.tkd.nunique():,} tkd")
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def first(d):   # mc=1: the first qualifying signal per tkd (MOC exits never overlap within a day)
    return d.groupby('tkd', sort=False).head(1)
BASE=dict(volat_lo=0.005, volat_hi=0.010, eff=-0.7, l600=40, rate=0.15, l120=30, rr=2.0, chg1d=-0.04, gap=30, dv60=1e6, crf=-0.002)
def mask(p):
    m=(df.volat_20m>p['volat_lo'])&(df.volat_20m<=p['volat_hi'])&(df.l600>=p['l600'])&(df.l120>=p['l120'])&(df.rr>=p['rr'])&(df.chg1d<=p['chg1d'])&(df.gap<=p['gap'])&(df.dv60>=p['dv60'])&(df.crf<=p['crf'])
    if p['eff'] is not None: m&=(df.eff<p['eff'])
    if p['rate'] is not None: m&=(df.rate>=p['rate'])
    return m
def row(lbl, p):
    b=first(df[mask(p)]); b2=b[b.year>=2022]
    return dict(variant=lbl, trades=len(b), tr_yr=round(len(b)/6.63,1), tkd22=len(b2), PF=round(pf(b.ret),2), PF22=round(pf(b2.ret),2) if len(b2) else None, win22=round((b2.ret>0).mean()*100) if len(b2) else None, avg22=round(b2.ret.mean()*100,2) if len(b2) else None, net22=round(b2.ret.sum()*100), worst=round(b.ret.min()*100,1), tail22=round((b2.ret<-0.1).mean()*100,1) if len(b2) else None)
print('\n########## A. ONE-AT-A-TIME relaxations from FINAL v3.1 (mc=1 MOC; PF not PF-1 here — the target is PF 2) ##########')
rows=[row('FINAL v3.1', BASE)]
for k,vals in [('eff',[-0.5,-0.3,-0.1,None]),('l600',[30,20]),('l120',[20,10]),('rate',[0.10,None]),('rr',[1.5,1.0]),('chg1d',[-0.02,0.0]),('gap',[45,60]),('dv60',[5e5,2.5e5]),('volat_lo',[0.004,0.003]),('volat_hi',[1.0]),('crf',[-0.001])]:
    for v in vals: rows.append(row(f'{k} -> {v}', {**BASE, k:v}))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## B. from eff -> -0.3: one-at-a-time on top ##########')
B3={**BASE,'eff':-0.3}; rows=[row('eff -0.3 base', B3)]
for k,vals in [('eff',[-0.1,None]),('l600',[30,20]),('l120',[20,10]),('rate',[0.10,None]),('rr',[1.5,1.0]),('chg1d',[-0.02,0.0]),('gap',[45,60]),('dv60',[5e5,2.5e5]),('volat_lo',[0.004,0.003]),('volat_hi',[1.0]),('crf',[-0.001])]:
    for v in vals: rows.append(row(f'eff -0.3 & {k} -> {v}', {**B3, k:v}))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## C. GREEDY expansion from eff -0.3: at each step relax the gate that adds the most 2022+ trades while PF22 >= 2.5 ##########')
STEPS={'eff':[-0.3,-0.1,None],'l600':[40,30,20],'l120':[30,20,10],'rate':[0.15,0.10,None],'rr':[2.0,1.5,1.0],'chg1d':[-0.04,-0.02,0.0],'gap':[30,45,60],'dv60':[1e6,5e5,2.5e5],'volat_lo':[0.005,0.004,0.003],'volat_hi':[0.010,1.0],'crf':[-0.002,-0.001]}
cur={**BASE,'eff':-0.3}; hist=[row('start (eff -0.3)',cur)]
for step in range(12):
    best=None
    for k,ladder in STEPS.items():
        i=ladder.index(cur[k])
        if i+1>=len(ladder): continue
        cand={**cur,k:ladder[i+1]}; r=row(f'+ {k} -> {ladder[i+1]}',cand)
        if r['PF22'] is not None and r['PF22']>=2.5 and (best is None or r['tkd22']>best[1]['tkd22']): best=(cand,r)
    if best is None: break
    cur=best[0]; hist.append(best[1])
print(pd.DataFrame(hist).to_string(index=False)); print('final params:', cur)
print('\n########## D. same greedy, floor PF22 >= 2.0 ##########')
cur={**BASE,'eff':-0.3}; hist=[row('start (eff -0.3)',cur)]
for step in range(14):
    best=None
    for k,ladder in STEPS.items():
        i=ladder.index(cur[k])
        if i+1>=len(ladder): continue
        cand={**cur,k:ladder[i+1]}; r=row(f'+ {k} -> {ladder[i+1]}',cand)
        if r['PF22'] is not None and r['PF22']>=2.0 and (best is None or r['tkd22']>best[1]['tkd22']): best=(cand,r)
    if best is None: break
    cur=best[0]; hist.append(best[1])
print(pd.DataFrame(hist).to_string(index=False)); print('final params:', cur)
b=first(df[mask(cur)]); print('\nyear table of the PF2 endpoint (mc=1 MOC):')
print(b.groupby('year').agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), net=('ret',lambda s: round(s.sum()*100)), win=('ret',lambda s: round((s>0).mean()*100)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1))).to_string())
print('\nDONE')
