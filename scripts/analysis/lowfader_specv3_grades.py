"""LowFader SIZING LADDER (2026-09-06, user): SpikeFader S42 rule on the SPEC FINAL book — voices rr >= 5, lows_rr3_120 >= 20,
lows_rr3_120 >= 10, none; grade = the STRONGEST voice firing (max, not sum); weight = (PF-1)/max(PF-1); linear vs sqrt vs equal;
leave-one-year-out held-out test. mc=1 MOC and mc=5 MOC books. Corpus data/lowfader_wl_spec3 (spec_ord > 0 = FINAL).
Run: python3 -u scripts/analysis/lowfader_specv3_grades.py > data/lowfader_specv3_grades.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
c=duckdb.connect('data/trading.db', read_only=True)
df=c.execute("""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit AS ret, vol_60/(vol_0945_tape/15.0) AS rr, lows_rr3_120
  FROM read_parquet('data/lowfader_wl_spec3/*.parquet') WHERE spec_ord > 0""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, k):
    ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep].copy()
VOICES=[('A','rr >= 5', lambda d: d.rr>=5), ('B','rr3_120 >= 20', lambda d: d.lows_rr3_120>=20), ('C','rr3_120 >= 10', lambda d: d.lows_rr3_120>=10)]
def grade(d):
    g=pd.Series('E', index=d.index)
    for k,_,fn in reversed(VOICES): g[fn(d)]=k   # later (stronger) voices overwrite: A wins over B wins over C
    return g
EST='pf'
def edge(s):
    if EST=='pf': v=pf(s)-1; return v if np.isfinite(v) else 1e9
    if EST=='pfcap': return min(pf(s)-1, 10.0)
    if EST=='avg': return s.mean()
    if EST=='avgtrim': return s.sort_values().iloc[max(1,int(len(s)*0.05)):].mean() if len(s)>=10 else s.mean()
def fit_weights(d):
    """edge/max over grades, fitted on d; negative -> 0."""
    p={k: edge(d.ret[d.grade==k]) for k in d.grade.unique()}
    fin=[v for v in p.values() if v<1e8]; cap=max(fin) if fin else 1.0
    p={k:(cap if v>=1e8 else max(v,0.0)) for k,v in p.items()}
    m=max(p.values()) if p else 1.0
    return {k: v/m for k,v in p.items()}
def scheme_stats(d, w):
    x=d.ret*d.w_eq*0+d.ret*w; n=len(d); t=x.mean()/x.std()*np.sqrt(n) if n>1 and x.std()>0 else np.nan
    dd=(x.cumsum()-x.cumsum().cummax()).min()
    return dict(net=round(x.sum()*100), avg_exp=round(w.mean(),3), net_per_exp=round(x.sum()*100/w.mean()), t=round(t,2), worst=round(x.min()*100,1), maxDD=round(-dd*100))
import itertools
for k_mc,(GS,EST) in itertools.product([1,5],[('ABCE','pf'),('ABCE','pfcap'),('ABCE','avg'),('XE','avg'),('XE','pfcap')]):
    b=mck(df,k_mc); b['grade']=grade(b)
    if GS=='XE': b['grade']=b.grade.replace({'A':'X','B':'X','C':'E'})   # merged: X = rr>=5 OR rr3_120>=20; E = rest
    b=b.sort_values(['trade_date','entry_sec']).reset_index(drop=True); b['w_eq']=1.0
    print(f"\n#################### grade scheme {GS} | edge estimator = {EST} ####################")
    print(f"\n==================== mc={k_mc} MOC book: {len(b)} trades ({len(b)/6.63:.1f}/yr) ====================")
    rows=[]
    for k,lbl,fn in (VOICES if (GS,EST)==('ABCE','pf') else []):
        m=fn(b); g=b[m]; g2=g[g.year>=2022]
        rows.append(dict(voice=f'{k}: {lbl}', n=len(g), PF1=round(pf(g.ret)-1,2), win=round((g.ret>0).mean()*100), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1), n22=len(g2), PF1_22=round(pf(g2.ret)-1,2) if len(g2) else None))
    if rows: print('\n--- SOLO cells ---'); print(pd.DataFrame(rows).to_string(index=False))
    print('\n--- GRADES = the strongest voice firing (A > B > C > E none) ---')
    rows=[]
    for k in (['A','B','C','E'] if GS=='ABCE' else ['X','E']):
        g=b[b.grade==k]; g2=g[g.year>=2022]
        rows.append(dict(grade=k, rule={'A':'rr>=5','B':'rr<5 & rr3_120>=20','C':'rr<5 & rr3_120 in [10,20)','E':'none','X':'rr>=5 OR rr3_120>=20'}[k], n=len(g), pct=round(len(g)/len(b)*100), PF1=round(pf(g.ret)-1,2), win=round((g.ret>0).mean()*100), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1), n22=len(g2), PF1_22=round(pf(g2.ret)-1,2) if len(g2) else None, net22=round(g2.ret.sum()*100)))
    print(pd.DataFrame(rows).to_string(index=False))
    W=fit_weights(b); Wsq={k: np.sqrt(v) for k,v in W.items()}
    KS=['A','B','C','E'] if GS=='ABCE' else ['X','E']
    print(f'\nweights {EST}/max, fitted on ALL years:', {k: round(W.get(k,0),2) for k in KS}, ' sqrt:', {k: round(Wsq.get(k,0),2) for k in KS})
    W22=fit_weights(b[b.year>=2022]); print('weights fitted on 2022+ only            :', {k: round(W22.get(k,0),2) for k in KS})
    print('\n--- SIZED BOOK (all years; weights fitted in-sample on all years) ---')
    rows=[]
    for lbl,w in [('equal weight', pd.Series(1.0,index=b.index)), ('linear (PF-1)/max', b.grade.map(W)), ('sqrt', b.grade.map(Wsq))]:
        rows.append(dict(scheme=lbl, **scheme_stats(b,w)))
    print(pd.DataFrame(rows).to_string(index=False))
    print('\n--- HELD-OUT (leave-one-year-out): weights fitted on the other 6 years, applied to the held-out year; net/exposure per trade ---')
    rows=[]; wins_lin=0; wins_sq=0
    for y in sorted(b.year.unique()):
        tr=b[b.year!=y]; te=b[b.year==y]; Wy=fit_weights(tr); Wys={k: np.sqrt(v) for k,v in Wy.items()}
        eq=te.ret.mean()*100; wl=te.grade.map(Wy).fillna(0); ws=te.grade.map(Wys).fillna(0)
        lin=(te.ret*wl).sum()/wl.sum()*100 if wl.sum()>0 else np.nan; sq=(te.ret*ws).sum()/ws.sum()*100 if ws.sum()>0 else np.nan
        wins_lin+=lin>eq; wins_sq+=sq>eq
        rows.append(dict(year=y, n=len(te), eq_avg=round(eq,2), linear_avg_per_exp=round(lin,2), sqrt_avg_per_exp=round(sq,2), **{'w'+k: round(Wy.get(k,0),2) for k in KS}))
    print(pd.DataFrame(rows).to_string(index=False)); print(f'linear beats equal in {wins_lin}/7 years; sqrt in {wins_sq}/7')
    # pooled held-out ratio
    num_l=0; den_l=0; num_e=0; den_e=0
    for y in sorted(b.year.unique()):
        tr=b[b.year!=y]; te=b[b.year==y]; Wy=fit_weights(tr); wl=te.grade.map(Wy).fillna(0)
        num_l+=(te.ret*wl).sum(); den_l+=wl.sum(); num_e+=te.ret.sum(); den_e+=len(te)
    print(f'pooled held-out: linear {num_l/den_l*100:.3f}%/trade per unit exposure vs equal {num_e/den_e*100:.3f}% -> {num_l/den_l/(num_e/den_e):.2f}x')
print('\nDONE')
