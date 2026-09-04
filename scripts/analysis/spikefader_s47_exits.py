"""SpikeFader S47 (2026-09-04): the EXIT GRID re-read with every mark resolved — 9m..1h.
The S37f grid stopped at 20m and its wide rungs were partly reading the 9m exit (the
retire-guard gap, commit 5049621). MaxFader's 30m cover was the best exit found there.
Views: SLICE (book built on the 9m exit, each trip re-exited at channel N) and
REPLAY-INSIDE (the channel's exit second replaces exit_sec, then the mc=1 replay).
Roster v3.4 sizing (A rr<0.5 · B dslo<=-5% · X rr>=12 · C halts∧fresh · D volat>=100bp).
Run: python3 -u scripts/analysis/spikefader_s47_exits.py > data/spikefader_s47_exits.log
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd, duckdb
from spikefader_zq import pfm1, SPEC, DERIVED
pd.set_option('display.width', 260)
DIR = 'data/spikefader_s47'
CH = [540, 600, 720, 900, 1200, 1800, 2400, 3600]
cols = ['symbol','trade_date','signal_sec','entry_px','exit_px','exit_sec','ret_exit','volat_20m','halts_today','secs_since_halt','sess_high','sess_low','signal_vwap'] \
     + [f'aux_lo_{n}_{y}' for n in CH for y in ('px','sec','moc')]
sp = SPEC.replace('dlv >', 'signal_vwap / NULLIF(sess_low,0) - 1 >').replace('ols_slope_1200 >= 0.0030', 'ols_slope_1200 >= 5.0e-05')
df = duckdb.query(f"SELECT {', '.join(cols)} {DERIVED} FROM read_parquet('{DIR}/*.parquet') WHERE {sp}").df()
df['year'] = pd.to_datetime(df['trade_date']).dt.year
df['dslo'] = df['signal_vwap']/df['sess_high'] - 1
for n in CH:
    px = df[f'aux_lo_{n}_px'].where(~df[f'aux_lo_{n}_px'].isna(), df['exit_px'])
    df[f'ret_{n}'] = -(px/df['entry_px'] - 1.0)
    df[f'sec_{n}'] = df[f'aux_lo_{n}_sec'].fillna(df['exit_sec'])
def mc1(d, exit_col='exit_sec'):
    d = d.sort_values(['symbol','trade_date','signal_sec'])
    ss = d['signal_sec'].to_numpy(); es = d[exit_col].to_numpy()
    keys = (d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy()
    keep = np.zeros(len(d), dtype=bool); busy = -1.0; prev = None
    for i in range(len(d)):
        if keys[i] != prev: busy = -1.0; prev = keys[i]
        if ss[i] >= busy: keep[i] = True; busy = es[i]
    return d[keep]
def st(s): return dict(n=len(s), PF1=round(pfm1(s),3), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), p5=round(s.quantile(.05)*100,1), tail=round((s<-.2).mean()*100,2))
def grade_w(d):
    A=d['rr']<0.5; B=d['dslo']<=-0.05; X=d['rr']>=12; C=(d['halts_today']>=2)&(d['secs_since_halt']>=60)&(d['secs_since_halt']<300); D=d['volat_20m']>=0.01
    w=np.full(len(d),0.19); w=np.where(D,0.48,w); w=np.where(C,0.75,w); w=np.where(X,0.74,w); w=np.where(B,0.99,w); w=np.where(A,1.00,w)
    return w
print(f"spec slice {len(df):,} trips  {df.trade_date.min()}..{df.trade_date.max()}")
for n in CH:
    print(f"  {n:>5} ({n//60:>2}m): moc-resolved {df[f'aux_lo_{n}_moc'].fillna(False).astype(bool).mean()*100:5.1f}%   unfilled {df[f'aux_lo_{n}_px'].isna().mean()*100:4.1f}%")

b = mc1(df); w = grade_w(b)
print(f"\n########## SLICE: the mc=1 book on the 9m exit (n={len(b):,}), re-exited at each channel ##########")
rows=[]
for n in CH:
    r=dict(exit=f'{n//60}m', **st(b[f'ret_{n}'])); rr=b[f'ret_{n}'].to_numpy()*w; r['sized_net_per_exp']=round(rr.sum()*100/w.mean()); r['sized_worst']=round(rr.min()*100,1); rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print(f"\n########## REPLAY-INSIDE: the channel's exit frees the slot, then mc=1 ##########")
rows=[]
for n in CH:
    bb = mc1(df, f'sec_{n}'); ww=grade_w(bb); r=dict(exit=f'{n//60}m', **st(bb[f'ret_{n}'])); rr=bb[f'ret_{n}'].to_numpy()*ww; r['sized_net_per_exp']=round(rr.sum()*100/ww.mean()); rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print(f"\n########## BY YEAR (slice, PF-1): 9m vs 12m vs 20m vs 30m vs 40m vs 1h; tail<-20% 9m -> 30m ##########")
rows=[]
for y,g in b.groupby('year'):
    r=dict(year=y,n=len(g))
    for n in (540,720,1200,1800,2400,3600): r[f'{n//60}m']=round(pfm1(g[f'ret_{n}']),3)
    r['net_9m']=round(g['ret_540'].sum()*100); r['net_30m']=round(g['ret_1800'].sum()*100); r['tail_9m']=round((g['ret_540']<-.2).mean()*100,2); r['tail_30m']=round((g['ret_1800']<-.2).mean()*100,2); r['worst_9m']=round(g['ret_540'].min()*100); r['worst_30m']=round(g['ret_1800'].min()*100)
    rows.append(r)
t=pd.DataFrame(rows); print(t.to_string(index=False)); print(f"30m beats 9m on PF-1 in {(t['30m']>t['9m']).sum()} of {len(t)} years; net higher in {(t['net_30m']>t['net_9m']).sum()}; tail smaller in {(t['tail_30m']<t['tail_9m']).sum()}")
print(f"\n########## BY GRADE (slice): 9m vs 30m ##########")
A=b['rr']<0.5; B=b['dslo']<=-0.05; X=b['rr']>=12; C=(b['halts_today']>=2)&(b['secs_since_halt']>=60)&(b['secs_since_halt']<300); D=b['volat_20m']>=0.01
g=np.full(len(b),'E',dtype=object); g=np.where(D,'D',g); g=np.where(C,'C',g); g=np.where(X,'X',g); g=np.where(B,'B',g); g=np.where(A,'A',g); b=b.assign(grade=g)
rows=[]
for k in ['A','B','X','C','D','E']:
    gg=b[b['grade']==k]
    if len(gg): rows.append(dict(grade=k, n=len(gg), PF1_9m=round(pfm1(gg['ret_540']),2), PF1_30m=round(pfm1(gg['ret_1800']),2), net_9m=round(gg['ret_540'].sum()*100), net_30m=round(gg['ret_1800'].sum()*100), worst_9m=round(gg['ret_540'].min()*100), worst_30m=round(gg['ret_1800'].min()*100), tail_9m=round((gg['ret_540']<-.2).mean()*100,1), tail_30m=round((gg['ret_1800']<-.2).mean()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
