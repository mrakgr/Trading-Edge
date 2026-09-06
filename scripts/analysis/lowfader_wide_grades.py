"""LowFader WIDE spec + STRICT-membership as a sizing voice (2026-09-06, user): the FINAL v3.1 spec (volat>50bp, gap<=30, crf<=-0.2%)
becomes the MIDDLE tier of the WIDE book. Orderings: (a) literal 3-grade X > STRICT > none; (b) 4-cell STRICT x X; (c) merged middle;
(d) STRICT-only. Weights = min(PF-1,10)/max, LOYO held-out. Corpus data/lowfader_wl_wide.
Run: python3 -u scripts/analysis/lowfader_wide_grades.py > data/lowfader_wide_grades.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320)
c=duckdb.connect('data/trading.db', read_only=True)
df=c.execute("""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, volat_20m, gap_adj_60 AS gap, chg_since_run_first_low AS crf, vol_60/(vol_0945_tape/15.0) AS rr, lows_rr3_120
  FROM read_parquet('data/lowfader_wl_wide/*.parquet')
  WHERE volat_20m > 0.003 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40 AND lows_since_first_low_120 >= 30
    AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04
    AND gap_adj_60 <= 60 AND dollar_vol_60 >= 1e6 AND chg_since_run_first_low <= -0.001 AND dv_0945_tape < 2e7""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
b=df.groupby('tkd', sort=False).head(1).copy()
b['X']=(b.rr>=5)|(b.lows_rr3_120>=20); b['S']=(b.volat_20m>0.005)&(b.gap<=30)&(b.crf<=-0.002)
b=b.sort_values(['trade_date','entry_sec']).reset_index(drop=True)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g,lbl):
    g2=g[g.year>=2022]; return dict(cell=lbl, n=len(g), pct=round(len(g)/len(b)*100), n22=len(g2), PF1=round(pf(g.ret)-1,2), PF1_22=round(pf(g2.ret)-1,2) if len(g2) else None, win=round((g.ret>0).mean()*100), avg=round(g.ret.mean()*100,2), net22=round(g2.ret.sum()*100), worst=round(g.ret.min()*100,1))
print(f"WIDE book: {len(b)} trades; STRICT members {int(b.S.sum())}; X {int(b.X.sum())}")
print('\n########## the 2x2: STRICT x X ##########')
print(pd.DataFrame([st(b[b.S&b.X],'STRICT & X'), st(b[b.S&~b.X],'STRICT & not X'), st(b[~b.S&b.X],'added & X'), st(b[~b.S&~b.X],'added & not X')]).to_string(index=False))
print('\n########## the voices solo ##########')
print(pd.DataFrame([st(b[b.X],'X'), st(b[~b.X],'not X'), st(b[b.S],'STRICT'), st(b[~b.S],'added')]).to_string(index=False))
def fitw(d, col):
    p={k: min(pf(d.ret[d[col]==k])-1,10.0) for k in d[col].unique()}; p={k:max(v,0) for k,v in p.items()}; m=max(p.values()); return {k:v/m for k,v in p.items()}
def run(name, gr, order):
    d=b.copy(); d['g']=gr(d)
    print(f"\n==================== {name} ====================")
    print(pd.DataFrame([st(d[d.g==k],k) for k in order]).to_string(index=False))
    W=fitw(d,'g'); W22=fitw(d[d.year>=2022],'g'); print('weights all-years:', {k: round(W.get(k,0),2) for k in order}, '| 2022+:', {k: round(W22.get(k,0),2) for k in order})
    num_l=den_l=num_e=den_e=0.0; wins=0; folds=[]
    for y in sorted(d.year.unique()):
        tr=d[d.year!=y]; te=d[d.year==y]; Wy=fitw(tr,'g'); w=te.g.map(Wy).fillna(0).to_numpy(); l=(te.ret*w).sum()/w.sum(); e=te.ret.mean(); wins+=l>e; num_l+=(te.ret*w).sum(); den_l+=w.sum(); num_e+=te.ret.sum(); den_e+=len(te); folds.append({k: round(Wy.get(k,0),2) for k in order})
    w=d.g.map(W).to_numpy(); x=d.ret*w; dd=(x.cumsum()-x.cumsum().cummax()).min(); dde=(d.ret.cumsum()-d.ret.cumsum().cummax()).min(); t=x.mean()/x.std()*np.sqrt(len(x)); te_=d.ret.mean()/d.ret.std()*np.sqrt(len(d))
    print(f"held-out sized vs equal: {wins}/7 years; pooled {num_l/den_l*100:.2f}% vs {num_e/den_e*100:.2f}%/unit -> {num_l/den_l/(num_e/den_e):.2f}x")
    print(f"sized in-sample: net {x.sum()*100:.0f} exposure {w.mean():.2f} net/exp {x.sum()*100/w.mean():.0f} t {t:.2f} worst {x.min()*100:.1f} maxDD {-dd*100:.0f} | equal: net {d.ret.sum()*100:.0f} t {te_:.2f} maxDD {-dde*100:.0f}")
    print('fold weights:', folds)
run('(a) literal: X > STRICT > none', lambda d: np.where(d.X,'X',np.where(d.S,'S','E')), ['X','S','E'])
run('(b) 4-cell: STRICT&X > STRICT&notX > added&X > added&notX', lambda d: np.where(d.S&d.X,'A',np.where(d.S,'B',np.where(d.X,'C','D'))), ['A','B','C','D'])
run('(c) merged middle: STRICT&X > (STRICT&notX | added&X) > added&notX', lambda d: np.where(d.S&d.X,'A',np.where(d.S|d.X,'M','D')), ['A','M','D'])
run('(d) STRICT-only: STRICT > added', lambda d: np.where(d.S,'S','E'), ['S','E'])
print('\nDONE')
