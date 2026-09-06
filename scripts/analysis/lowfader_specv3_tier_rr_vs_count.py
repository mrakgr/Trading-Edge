"""SIZING TIER: signal-bar rr >= 5 vs lows_rr3_120 >= 10/20 on the SPEC FINAL book (2026-09-06, user). Both halves of each split
(upper tier / lower remainder), the 2x2 between them, Jaccard, year tables. Corpus data/lowfader_wl_spec3 (spec_ord > 0 = FINAL).
Run: python3 -u scripts/analysis/lowfader_specv3_tier_rr_vs_count.py > data/lowfader_specv3_tier_rr_vs_count.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
c=duckdb.connect('data/trading.db', read_only=True)
df=c.execute("""SELECT symbol, trade_date, signal_sec, exit_sec, entry_px, ret_exit AS ret, aux_hi_600_px, aux_hi_600_sec, spec_ord,
    vol_60/(vol_0945_tape/15.0) AS rr, lows_rr3_120, lows_rr2_120, lows_rr3_600, gap_adj_60, reset_hi_120, signal_vwap
  FROM read_parquet('data/lowfader_wl_spec3/*.parquet') WHERE spec_ord > 0""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret); df['x10']=np.where(f, df.aux_hi_600_sec, df.exit_sec)
df['drop_reset_120']=df.signal_vwap/df.reset_hi_120-1
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, x, k):
    ss=d['signal_sec'].to_numpy(); es=d[x].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
def cell(lbl, m):
    d=df[m]; r=dict(cell=lbl, n=len(d), pct=round(len(d)/len(df)*100), tkd22=d[d.year>=2022].tkd.nunique())
    for ex,cc,x,k in [('mc1MOC','ret','exit_sec',1),('mc1_10m','ret10','x10',1),('mc5','ret','exit_sec',5)]:
        b=mck(d,x,k); b2=b[b.year>=2022]
        r.update({f'{ex}_tr_yr':round(len(b)/6.63,1), f'{ex}_PF1':round(pf(b2[cc])-1,2) if len(b2) else None, f'{ex}_net':round(b2[cc].sum()*100), f'{ex}_avg':round(b2[cc].mean()*100,2) if len(b2) else None, f'{ex}_worst':round(b[cc].min()*100,1) if len(b) else None})
    return r
def split(name, m):
    return [cell(f'{name}  [UPPER]', m), cell(f'NOT {name}  [lower]', ~m)]
# NOTE: a tier is applied to the mc=0 rows and the replay is run INSIDE each cell (slice-replay). For the sizing use the cells are
# read jointly: the book is the union, the tier decides the size. mc=5 inside a cell = up to 5 concurrent positions from that cell.
rows=[cell('FINAL (all)', np.ones(len(df),bool))]
for name,m in [('rr >= 3',(df.rr>=3).values),('rr >= 5',(df.rr>=5).values),('rr >= 8',(df.rr>=8).values),('rr >= 10',(df.rr>=10).values),
               ('rr3_120 >= 10',(df.lows_rr3_120>=10).values),('rr3_120 >= 20',(df.lows_rr3_120>=20).values),('rr3_600 >= 20',(df.lows_rr3_600>=20).values)]:
    rows+=split(name,m)
print('########## 1. each tier: UPPER cell and the LOWER remainder (FINAL book, 2022+ PF-1/net/avg; worst all years) ##########')
print(pd.DataFrame(rows).to_string(index=False))
a=(df.rr>=5).values; b=(df.lows_rr3_120>=10).values; b2=(df.lows_rr3_120>=20).values
print(f"\nJaccard(rr>=5, rr3_120>=10) = {(a&b).sum()/(a|b).sum():.2f};  Jaccard(rr>=5, rr3_120>=20) = {(a&b2).sum()/(a|b2).sum():.2f};  rho(rr, lows_rr3_120) = {df[['rr','lows_rr3_120']].corr().iloc[0,1]:.2f}")
print('\n########## 2. the 2x2: rr >= 5  x  rr3_120 >= 10 ##########')
rows=[cell('rr>=5 & rr3_120>=10', a&b), cell('rr>=5 & rr3_120<10', a&~b), cell('rr<5 & rr3_120>=10', ~a&b), cell('rr<5 & rr3_120<10', ~a&~b)]
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 3. the 2x2: rr >= 5  x  rr3_120 >= 20 ##########')
rows=[cell('rr>=5 & rr3_120>=20', a&b2), cell('rr>=5 & rr3_120<20', a&~b2), cell('rr<5 & rr3_120>=20', ~a&b2), cell('rr<5 & rr3_120<20', ~a&~b2)]
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 4. rr ladder INSIDE the FINAL book (the tier as bands) ##########')
d=df.copy(); d['b']=pd.cut(d.rr,[2,3,4,5,8,10,20,1e9]); rows=[]
for k,g in d.groupby('b',observed=True):
    b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
    rows.append(dict(rr_band=str(k), n=len(g), tkd22=g[g.year>=2022].tkd.nunique(), mc1_PF1=round(pf(b12.ret)-1,2), mc1_net=round(b12.ret.sum()*100), mc1_avg=round(b12.ret.mean()*100,2), mc1_win=round((b12.ret>0).mean()*100), mc5_PF1=round(pf(b52.ret)-1,2), mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 5. YEAR tables (mc=1 MOC) for the two upper tiers + their lower cells ##########')
for lbl,m in [('rr >= 5',a),('rr < 5',~a),('rr3_120 >= 10',b),('rr3_120 < 10',~b)]:
    d=df[m]; b1=mck(d,'exit_sec',1); rows=[]
    for y in sorted(df.year.unique()):
        g=b1[b1.year==y]; rows.append(dict(year=y, n=len(g), PF=round(pf(g.ret),2) if len(g) else None, net=round(g.ret.sum()*100), win=round((g.ret>0).mean()*100) if len(g) else None, worst=round(g.ret.min()*100,1) if len(g) else None))
    print(f'\n--- {lbl} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
