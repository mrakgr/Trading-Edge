"""LowFader WIDE variant (2026-09-06, user: "a PF 2 system with a lot more trades"): FINAL v3.1 with the tail-guarded relaxations
volat > 30bp, gap_adj_60 <= 60, crf <= -0.1% (from lowfader_relax*.py: the greedy endpoint at PF22 >= 4 with worst >= -20 and
tail <= 1.5%). Corpus data/lowfader_wl_wide (whitelist lowfader_wide_whitelist, 360 tkd, engine floor --min-volat-20m 0.003).
mc=1 MOC, first qualifying signal; the X/E ladder re-fitted. Run: python3 -u scripts/analysis/lowfader_wide_book.py > data/lowfader_wide_book.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 320)
c=duckdb.connect('data/trading.db', read_only=True)
df=c.execute("""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit AS ret, volat_20m, eff_ewma_10m AS eff, lows_since_first_low_600 AS l600, bars_since_first_low_600 AS b600,
    lows_since_first_low_120 AS l120, vol_60/(vol_0945_tape/15.0) AS rr, entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg1d, gap_adj_60 AS gap, dollar_vol_60 AS dv60,
    chg_since_run_first_low AS crf, dv_0945_tape AS dv0945, lows_rr3_120, spec_ord, aux_hi_600_px, aux_hi_600_sec
  FROM read_parquet('data/lowfader_wl_wide/*.parquet')
  WHERE volat_20m > 0.003 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.6 AND lows_since_first_low_600 >= 40 AND lows_since_first_low_120 >= 30
    AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04
    AND gap_adj_60 <= 60 AND dollar_vol_60 >= 1e6 AND chg_since_run_first_low <= -0.001 AND dv_0945_tape < 2e7""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str); df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
df['X']=(df.rr>=5)|(df.lows_rr3_120>=20)
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def first(d): return d.groupby('tkd', sort=False).head(1)
def st(b,lbl):
    b2=b[b.year>=2022]; return dict(variant=lbl, trades=len(b), tr_yr=round(len(b)/6.63,1), n22=len(b2), PF=round(pf(b.ret),2), PF22=round(pf(b2.ret),2) if len(b2) else None, win22=round((b2.ret>0).mean()*100) if len(b2) else None, avg22=round(b2.ret.mean()*100,2) if len(b2) else None, net22=round(b2.ret.sum()*100), worst=round(b.ret.min()*100,1), tail22=round((b2.ret<-0.1).mean()*100,1) if len(b2) else None, pct_X=round(b.X.mean()*100))
V={'FINAL v3.1': lambda d: (d.volat_20m>0.005)&(d.eff<-0.7)&(d.gap<=30)&(d.crf<=-0.002),
   'WIDE (volat>30bp, gap<=60, crf<=-0.1%)': lambda d: (d.eff<-0.7),
   'WIDE-40 (volat>40bp)': lambda d: (d.volat_20m>0.004)&(d.eff<-0.7),
   'WIDE + eff<-0.6': lambda d: np.ones(len(d),bool)}
books={}; rows=[]
for k,fn in V.items():
    b=first(df[fn(df)]); books[k]=b; rows.append(st(b,k)); rows.append(st(b[b.X],'   grade X')); rows.append(st(b[~b.X],'   grade E'))
print('########## WIDE variants (engine rerun at 30bp floor; mc=1 MOC, first qualifying signal) + the X/E ladder ##########'); print(pd.DataFrame(rows).to_string(index=False))
print('(cross-check FINAL = 84 trades; spec_ord>0 tkd =', df[df.spec_ord>0].tkd.nunique(), ')')
def wE(d):
    px=min(pf(d.ret[d.X])-1,10); pe=min(pf(d.ret[~d.X])-1,10); return max(pe,0)/px
for k in ['WIDE (volat>30bp, gap<=60, crf<=-0.1%)','WIDE + eff<-0.6']:
    b=books[k].sort_values(['trade_date','entry_sec']).reset_index(drop=True)
    print(f"\n--- {k}: ladder --- E weight all-years {wE(b):.2f}, 2022+ {wE(b[b.year>=2022]):.2f}, LOYO folds {[round(wE(b[b.year!=y]),2) for y in sorted(b.year.unique())]}")
    num_l=den_l=num_e=den_e=0.0; wins=0
    for y in sorted(b.year.unique()):
        tr=b[b.year!=y]; te=b[b.year==y]; w=np.where(te.X,1.0,wE(tr)); l=(te.ret*w).sum()/w.sum(); e=te.ret.mean(); wins+=l>e; num_l+=(te.ret*w).sum(); den_l+=w.sum(); num_e+=te.ret.sum(); den_e+=len(te)
    w=np.where(b.X,1.0,wE(b)); x=b.ret*w; dd=(x.cumsum()-x.cumsum().cummax()).min(); dde=(b.ret.cumsum()-b.ret.cumsum().cummax()).min()
    print(f"held-out sized vs equal: {wins}/7 years, pooled {num_l/den_l*100:.2f}% vs {num_e/den_e*100:.2f}%/unit -> {num_l/den_l/(num_e/den_e):.2f}x | sized in-sample: net {x.sum()*100:.0f} exposure {w.mean():.2f} net/exp {x.sum()*100/w.mean():.0f} worst {x.min()*100:.1f} maxDD {-dd*100:.0f} vs equal net {b.ret.sum()*100:.0f} maxDD {-dde*100:.0f}")
    print(b.groupby('year').agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), net=('ret',lambda s: round(s.sum()*100)), win=('ret',lambda s: round((s>0).mean()*100)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1)), n_X=('X','sum'), net_X=('ret',lambda s: round(s[b.loc[s.index,'X']].sum()*100)), net_E=('ret',lambda s: round(s[~b.loc[s.index,'X']].sum()*100))).to_string())
b=books['WIDE (volat>30bp, gap<=60, crf<=-0.1%)']
print('\nWIDE by volat band:'); d=b.copy(); d['vb']=pd.cut(d.volat_20m*1e4,[30,40,50,60,80,100]); print(d.groupby('vb',observed=True).agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1)), pct_X=('X',lambda s: round(s.mean()*100))).to_string())
print('\nWIDE by gap band:'); d['gb']=pd.cut(d.gap,[-1,10,20,30,45,60]); print(d.groupby('gb',observed=True).agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1)), pct_X=('X',lambda s: round(s.mean()*100))).to_string())
print('\nWIDE by crf band:'); d['cb']=pd.cut(d.crf*100,[-100,-1,-0.5,-0.2,-0.1]); print(d.groupby('cb',observed=True).agg(n=('ret','size'), PF=('ret',lambda s: round(pf(s),2)), avg=('ret',lambda s: round(s.mean()*100,2)), worst=('ret',lambda s: round(s.min()*100,1))).to_string())
print('\nWIDE: 10m cover vs MOC (2022+):', dict(MOC_PF22=round(pf(b[b.year>=2022].ret),2), MOC_net22=round(b[b.year>=2022].ret.sum()*100), cover10_PF22=round(pf(b[b.year>=2022].ret10),2), cover10_net22=round(b[b.year>=2022].ret10.sum()*100), cover10_worst=round(b.ret10.min()*100,1)))
fin=set(books['FINAL v3.1'].tkd); add=b[~b.tkd.isin(fin)]
print('\nWIDE minus FINAL — the ADDED trades on their own:'); print(pd.DataFrame([st(add,'added'), st(add[add.X],'  added X'), st(add[~add.X],'  added E')]).to_string(index=False))
print('\nDONE')
