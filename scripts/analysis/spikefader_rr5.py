"""SpikeFader rr5 / rr8 / rr12 leg counts (2026-09-06, user, §S48 addendum 5): the loud rungs of the rr-qualified count on the spec
book (s49_rr5 = s47 frame + highs_rr{5,8,12}_{120,600,1200}). Bands, floors, unique cell (grade E) + same-n control, roster LOYO.
Run: python3 -u scripts/analysis/spikefader_rr5.py > data/spikefader_rr5.log
"""
import numpy as np, pandas as pd, duckdb, sys
pd.set_option('display.width', 340)
sys.path.insert(0,'scripts/analysis'); from spikefader_zq import SPEC, DERIVED
con=duckdb.connect()
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mc1(f):
    f=f.sort_values(['tkd','signal_sec']).reset_index(drop=True); keep=np.zeros(len(f),bool); last=-1; prev=None
    key=f.tkd.values; ent=f.entry_sec.values; ext=f.exit_sec.values
    for i in range(len(f)):
        if key[i]!=prev: prev=key[i]; last=-1
        if ent[i]>=last: keep[i]=True; last=ext[i]
    return f[keep].sort_values(['trade_date','entry_sec']).reset_index(drop=True)
NEW='data/spikefader_s49_rr5/*.parquet'; REF='data/spikefader_s47/*.parquet'
n_new=con.execute(f"SELECT count(*) FROM read_parquet('{NEW}')").fetchone()[0]; n_ref=con.execute(f"SELECT count(*) FROM read_parquet('{REF}')").fetchone()[0]
n_both=con.execute(f"SELECT count(*) FROM (SELECT symbol, trade_date, signal_sec FROM read_parquet('{NEW}') INTERSECT SELECT symbol, trade_date, signal_sec FROM read_parquet('{REF}'))").fetchone()[0]
print(f"trip-set identity: s49_rr5 {n_new:,} vs s47 {n_ref:,}, common {n_both:,}")
sp=SPEC.replace('dlv >', 'signal_vwap / NULLIF(sess_low,0) - 1 >').replace('ols_slope_1200 >= 0.0030', 'ols_slope_1200 >= 5.0e-05')
C=[f'highs_rr{k}_{N}' for N in [120,600,1200] for k in [5,8,12]]
f=con.execute(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, volat_20m, halts_today, secs_since_halt, highs_rr3_120, {', '.join(C)} {DERIVED}
  FROM read_parquet('{NEW}') WHERE {sp}""").df(); f['tkd']=f.symbol+'|'+f.trade_date.astype(str)
b=mc1(f); b['year']=pd.to_datetime(b.trade_date).dt.year
b['grade']=np.where(b.rr<0.5,'A',np.where(b.dslo<=-0.05,'B',np.where(b.rr>=12,'X',np.where((b.halts_today>=2)&(b.secs_since_halt>=60)&(b.secs_since_halt<300),'C',np.where(b.volat_20m>=0.010,'D','E')))))
print(f"spec book {len(b):,} trades PF-1 {pf(b.ret)-1:.3f}; grades {b.grade.value_counts().to_dict()}")
print('\nquantiles:'); print(b[['highs_rr3_120']+C].quantile([.5,.75,.9,.95,.99]).to_string())
YE=range(2020,2027)
def st(g,lbl): return dict(cell=lbl, n=len(g), PF1=round(pf(g.ret)-1,2), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1), tail20=round((g.ret<-.2).mean()*100,1), **{str(y): (round(pf(g[g.year==y].ret)-1,2) if (g.year==y).sum()>=8 else None) for y in YE})
for col in ['highs_rr5_120','highs_rr8_120','highs_rr12_120','highs_rr5_600','highs_rr12_600','highs_rr5_1200']:
    d=b.copy(); d['band']=pd.cut(d[col],[-1,0,2,5,10,20,40,60,1e9]); rows=[]
    for k,g in d.groupby('band',observed=True):
        if len(g)>=8: rows.append(st(g,str(k)) | {'grades': g.grade.value_counts().to_dict()})
    print(f'\n--- {col} bands ---'); print(pd.DataFrame(rows).to_string(index=False))
E=b.grade=='E'; e=b[E].ret.to_numpy(); rng=np.random.default_rng(0)
print('\n########## floors as candidate voices: unique cell (grade E) + same-n control ##########'); rows=[]
for col in ['highs_rr5_120','highs_rr8_120','highs_rr12_120','highs_rr5_600','highs_rr12_600']:
    for th in [1,5,10,20,40]:
        m=b[col]>=th; g=b[m]; u=b[m&E]; n=len(u)
        if len(g)<8: continue
        r=st(g,f'{col} >= {th}')
        if n>=6:
            sims=np.array([pf(pd.Series(rng.choice(e,n,replace=False)))-1 for _ in range(1000)]); obs=pf(u.ret)-1
            r.update(unique_n=n, unique_PF1=round(obs,2), unique_worst=round(u.ret.min()*100,1), samen_p95=round(np.quantile(sims,.95),2), pctile=round((sims<obs).mean()*100,1))
        r.update(grades=g.grade.value_counts().to_dict()); rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
V0=[('A',lambda d: d.rr<0.5),('B',lambda d: d.dslo<=-0.05),('X',lambda d: d.rr>=12),('C',lambda d: (d.halts_today>=2)&(d.secs_since_halt>=60)&(d.secs_since_halt<300)),('D',lambda d: d.volat_20m>=0.010)]
def grade_w(d,V):
    g=pd.Series('E',index=d.index)
    for name,m in reversed(V): g[m(d)]=name
    return g
def fitw(d,g):
    p={k: min(pf(d.ret[g==k])-1,10) for k in g.unique()}; p={k:max(v,0) for k,v in p.items()}; m=max(p.values()); return {k:v/m for k,v in p.items()}
print('\n########## roster LOYO with the candidate seated after B (before X) — v3.4 = 2.640 ##########'); rows=[]
for col,th in [('highs_rr5_120',10),('highs_rr5_120',20),('highs_rr5_120',40),('highs_rr8_120',10),('highs_rr8_120',20),('highs_rr12_120',5),('highs_rr12_120',10),('highs_rr12_120',20),('highs_rr5_600',40),('highs_rr12_600',20)]:
    L=('L',lambda d,col=col,th=th: d[col]>=th); V=V0[:2]+[L]+V0[2:]; g=grade_w(b,V); num=den=0.0; per=[]
    for y in sorted(b.year.unique()):
        tr=b[b.year!=y]; te=b[b.year==y]; W=fitw(tr,g[tr.index]); w=g[te.index].map(W).fillna(0).to_numpy(); num+=(te.ret*w).sum(); den+=w.sum(); per.append(round((te.ret*w).sum()/w.sum()*100,2))
    Wi=fitw(b,g); rows.append(dict(voice=f'{col} >= {th}', held_out=round(num/den*100,3), L_n=int((g=='L').sum()), L_weight=round(Wi.get('L',0),2), A_weight=round(Wi.get('A',0),2), X_weight=round(Wi.get('X',0),2), X_n=int((g=='X').sum()), per_year=per))
print(pd.DataFrame(rows).to_string(index=False)); print('\nDONE')
