"""LowFader SPEC v3.1 FINAL (= v3 FINAL + dv_0945_tape < $20M; NO averaging down — full position on the first signal, mc=1 MOC)
+ LowFlyer's BREADTH (D-1 pct_above_20, lagged) and FLOAT (SEC public float ASOF known_date <= trade_date, revalued to the entry
day via a ratio of two adjusted closes) as candidate sizing features (2026-09-06, user).
Corpus data/lowfader_wl_spec3 (spec_ord > 0 = FINAL v3.1). mc=1 MOC = the production book; mc=0 rows shown for banding breadth.
Run: python3 -u scripts/analysis/lowfader_final_breadth_float.py > data/lowfader_final_breadth_float.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
c=duckdb.connect('data/trading.db', read_only=True); c.execute("ATTACH 'data/equity/float/float.db' AS f (READ_ONLY)")
c.execute("""CREATE TEMP TABLE flt AS SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0""")
c.execute("""CREATE TEMP TABLE br AS SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'""")
df=c.execute("""WITH t AS (SELECT symbol, CAST(trade_date AS DATE) AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit AS ret, spec_ord,
      vol_60/(vol_0945_tape/15.0) AS rr, lows_rr3_120, dv_0945_tape, gap_adj_60, volat_20m
    FROM read_parquet('data/lowfader_wl_spec3/*.parquet') WHERE spec_ord > 0),
  wf AS (SELECT t.*, fl.float_usd, fl.period_end AS flt_pe FROM t ASOF LEFT JOIN flt fl ON fl.ticker = t.symbol AND fl.known_date <= t.trade_date)
  SELECT wf.*, b.b_lag1 AS breadth,
    CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0 THEN wf.float_usd * ap_en.adj_close / ap_pe.adj_close END AS fentry
  FROM wf ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker = wf.symbol AND ap_pe.date <= wf.flt_pe
  LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker = wf.symbol AND ap_en.date = wf.trade_date
  LEFT JOIN br b ON b.date = wf.trade_date""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True); df['X']=(df.rr>=5)|(df.lows_rr3_120>=20)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d,k):
    ss=d.signal_sec.to_numpy(); es=d.exit_sec.to_numpy(); keys=d.tkd.to_numpy(); keep=np.zeros(len(d),bool); oe=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: oe=[]; prev=keys[i]
        oe=[e for e in oe if e>ss[i]]
        if len(oe)<k: keep[i]=True; oe.append(es[i])
    return d[keep].copy()
b1=mck(df,1); b1=b1.sort_values(['trade_date','entry_sec']).reset_index(drop=True)
print(f"FINAL v3.1: mc=0 {len(df)} trips / {df.tkd.nunique()} tkd / {df[df.year>=2022].tkd.nunique()} tkd22 | mc=1 book {len(b1)} trades ({len(b1)/6.63:.1f}/yr)")
def stats(g, lbl):
    g2=g[g.year>=2022]
    return dict(cell=lbl, n=len(g), n22=len(g2), PF1=round(pf(g.ret)-1,2), PF1_22=round(pf(g2.ret)-1,2) if len(g2) else None, win=round((g.ret>0).mean()*100) if len(g) else None, avg=round(g.ret.mean()*100,2) if len(g) else None, net22=round(g2.ret.sum()*100), worst=round(g.ret.min()*100,1) if len(g) else None, pct_X=round(g.X.mean()*100) if len(g) else None)
print('\n########## 1. THE PRODUCTION BOOK (mc=1 MOC, first signal, full position) ##########')
print(pd.DataFrame([stats(b1,'FINAL v3.1 mc=1'), stats(b1[b1.X],'  grade X (rr>=5 | rr3_120>=20)'), stats(b1[~b1.X],'  grade E (none)')]).to_string(index=False))
rows=[]
for y in sorted(b1.year.unique()):
    g=b1[b1.year==y]; rows.append(dict(year=y, n=len(g), PF=round(pf(g.ret),2), net=round(g.ret.sum()*100), win=round((g.ret>0).mean()*100), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1), n_X=int(g.X.sum()), net_X=round(g[g.X].ret.sum()*100), net_E=round(g[~g.X].ret.sum()*100)))
print(pd.DataFrame(rows).to_string(index=False))
# ladder re-fit on v3.1
def wE(d):
    px=min(pf(d.ret[d.X])-1,10); pe=min(pf(d.ret[~d.X])-1,10); return max(pe,0)/max(px,1e-9) if px>0 else np.nan
print(f"\nladder weight E = min(PF-1,10)/max: all years {wE(b1):.2f}; 2022+ {wE(b1[b1.year>=2022]):.2f}; LOYO folds:", [round(wE(b1[b1.year!=y]),2) for y in sorted(b1.year.unique())])
num_l=den_l=num_e=den_e=0.0; wins=0
for y in sorted(b1.year.unique()):
    tr=b1[b1.year!=y]; te=b1[b1.year==y]; w=np.where(te.X,1.0,wE(tr)); l=(te.ret*w).sum()/w.sum(); e=te.ret.mean(); wins+=l>e; num_l+=(te.ret*w).sum(); den_l+=w.sum(); num_e+=te.ret.sum(); den_e+=len(te)
print(f"held-out sized vs equal: {wins}/7 years; pooled {num_l/den_l*100:.2f}% vs {num_e/den_e*100:.2f}% per unit exposure -> {num_l/den_l/(num_e/den_e):.2f}x")
w=np.where(b1.X,1.0,wE(b1)); x=b1.ret*w; dd=(x.cumsum()-x.cumsum().cummax()).min(); xe=b1.ret; dde=(xe.cumsum()-xe.cumsum().cummax()).min()
print(f"sized book (all yrs, in-sample E={wE(b1):.2f}): net {x.sum()*100:.0f} exposure {w.mean():.2f} net/exp {x.sum()*100/w.mean():.0f} worst {x.min()*100:.1f} maxDD {-dd*100:.0f} | equal: net {xe.sum()*100:.0f} maxDD {-dde*100:.0f}")
print('\n########## 2. BREADTH (D-1 pct_above_20, lagged) ##########')
print(f"coverage: mc=1 {b1.breadth.notna().mean()*100:.0f}% (breadth.parquet ends 2026-06-18); mc=0 {df.breadth.notna().mean()*100:.0f}%")
print('breadth quantiles on the book:', b1.breadth.quantile([.1,.25,.5,.75,.9]).round(2).tolist())
for lbl,d in [('mc=1 book',b1),('mc=0 rows',df)]:
    d=d.copy(); d['b']=pd.cut(d.breadth,[0,0.2,0.35,0.5,0.65,0.8,1.0]); rows=[stats(d[d.b==k], str(k)) | {'tkd': d[d.b==k].tkd.nunique()} for k in d.b.cat.categories if (d.b==k).sum()>0]
    rows.append(stats(d[d.breadth.isna()],'NaN (no breadth)')); rows.append(stats(d[d.breadth>=0.65],'>= 0.65 (LowFlyer size-up)')); rows.append(stats(d[d.breadth<0.65],'< 0.65'))
    print(f'\n--- {lbl} ---'); print(pd.DataFrame(rows).to_string(index=False))
rows=[]
for y in sorted(b1.year.unique()):
    g=b1[b1.year==y]; hi=g[g.breadth>=0.65]; lo=g[g.breadth<0.65]
    rows.append(dict(year=y, n_hi=len(hi), PF1_hi=round(pf(hi.ret)-1,2) if len(hi) else None, avg_hi=round(hi.ret.mean()*100,2) if len(hi) else None, n_lo=len(lo), PF1_lo=round(pf(lo.ret)-1,2) if len(lo) else None, avg_lo=round(lo.ret.mean()*100,2) if len(lo) else None))
print('\n--- breadth >= 0.65 vs < 0.65 by year (mc=1) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n--- breadth x grade (mc=1) ---')
print(pd.DataFrame([stats(b1[b1.X&(b1.breadth>=0.65)],'X & breadth>=0.65'), stats(b1[b1.X&(b1.breadth<0.65)],'X & breadth<0.65'), stats(b1[~b1.X&(b1.breadth>=0.65)],'E & breadth>=0.65'), stats(b1[~b1.X&(b1.breadth<0.65)],'E & breadth<0.65')]).to_string(index=False))
print('\n########## 3. FLOAT (dollar float at entry; LowFlyer gate was < $300M) ##########')
print(f"coverage: mc=1 {b1.fentry.notna().mean()*100:.0f}%, mc=0 {df.fentry.notna().mean()*100:.0f}%; 2022+ mc=1 {b1[b1.year>=2022].fentry.notna().mean()*100:.0f}%")
print('float quantiles ($M, mc=1):', (b1.fentry.quantile([.1,.25,.5,.75,.9])/1e6).round(0).tolist())
for lbl,d in [('mc=1 book',b1),('mc=0 rows',df)]:
    d=d.copy(); d['b']=pd.cut(d.fentry/1e6,[0,100,300,1000,3000,1e9]); rows=[stats(d[d.b==k], str(k)+' $M') | {'tkd': d[d.b==k].tkd.nunique()} for k in d.b.cat.categories if (d.b==k).sum()>0]
    rows.append(stats(d[d.fentry.isna()],'NaN (no float)')); rows.append(stats(d[d.fentry<300e6],'< $300M (LowFlyer gate)')); rows.append(stats(d[d.fentry>=300e6],'>= $300M'))
    print(f'\n--- {lbl} ---'); print(pd.DataFrame(rows).to_string(index=False))
rows=[]
for y in sorted(b1.year.unique()):
    g=b1[b1.year==y]; lo=g[g.fentry<300e6]; hi=g[g.fentry>=300e6]; na=g[g.fentry.isna()]
    rows.append(dict(year=y, n_lt300=len(lo), PF1_lt300=round(pf(lo.ret)-1,2) if len(lo) else None, avg_lt300=round(lo.ret.mean()*100,2) if len(lo) else None, n_ge300=len(hi), PF1_ge300=round(pf(hi.ret)-1,2) if len(hi) else None, avg_ge300=round(hi.ret.mean()*100,2) if len(hi) else None, n_nan=len(na)))
print('\n--- float < $300M vs >= $300M by year (mc=1) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n--- float x grade (mc=1) ---')
print(pd.DataFrame([stats(b1[b1.X&(b1.fentry<300e6)],'X & float<300M'), stats(b1[b1.X&(b1.fentry>=300e6)],'X & float>=300M'), stats(b1[~b1.X&(b1.fentry<300e6)],'E & float<300M'), stats(b1[~b1.X&(b1.fentry>=300e6)],'E & float>=300M')]).to_string(index=False))
print('\nrho(fentry, dv_0945_tape) mc=1:', round(np.log(b1.fentry).corr(np.log(b1.dv_0945_tape)),2), '| rho(breadth, ret):', round(b1.breadth.corr(b1.ret),2), '| rho(log float, ret):', round(np.log(b1.fentry).corr(b1.ret),2))
print('\nDONE')
