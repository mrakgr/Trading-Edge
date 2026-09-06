"""LowFader SPEC v3 (+l120>=30): MAGNITUDE SINCE THE N-LEG'S FIRST LOW for the 2m/3m/5m/10m legs (2026-09-06, user).
The engine records first_low_vwap only for the 20m leg. Reconstruct the sub-20m anchors POST-HOC: each N-leg's first low is a
new 20m low = a sampler signal row when the floors held, so within a tkd the anchor = ffill(signal_vwap where lows_N == 0).
Validity per row: the anchor row must be the leg's TRUE first low. Invalidate when lows_N decreases without passing through 0
(a reset + first low happened unseen). VALIDATED against the exact 20m truth (first_low_vwap) on the same construction.
mag_N = signal_vwap / anchor_N - 1 (<= 0; 0 on the first-low bar itself). mc=1 (MOC | 10m), mc=5 MOC, 2022+.
Run: python3 -u scripts/analysis/lowfader_specv3_legmag.py > data/lowfader_specv3_legmag.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
G=['data/lowfader_alltape_h1/*.parquet','data/lowfader_alltape_h1c/*.parquet','data/lowfader_alltape_h1b/*.parquet','data/lowfader_alltape_h2/*.parquet','data/lowfader_alltape_h2e/*.parquet','data/lowfader_alltape_h2b/*.parquet','data/lowfader_alltape_h2c/*.parquet','data/lowfader_alltape_h2d/*.parquet']
CUTW=' AND '.join(f"NOT (t.filename LIKE '%{k}%' AND CAST(t.trade_date AS DATE) >= DATE '{v}')" for k,v in {'_h1/':'2022-01-19','_h2/':'2024-12-06','_h2b/':'2025-09-23'}.items())
SPEC="""t.volat_20m > 0.005 AND t.volat_20m <= 0.010 AND t.eff_ewma_10m < -0.7 AND t.lows_since_first_low_600 >= 40 AND t.lows_since_first_low_120 >= 30
    AND t.lows_since_first_low_600 >= 0.15*t.bars_since_first_low_600 AND t.vol_60 >= 2.0*t.vol_0945_tape/15.0 AND t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 <= -0.04
    AND t.gap_adj_60 <= 30 AND t.dollar_vol_60 >= 1e6"""
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
# ALL signal rows of every tkd that has at least one spec row (the anchors live on non-spec rows)
LN={120:'lows_since_first_low_120',180:'lows_since_first_low_180',300:'lows_since_first_low_300',600:'lows_since_first_low_600',1200:'lows_since_first_low'}
df=c.execute(f"""WITH k AS (SELECT DISTINCT t.symbol, t.trade_date FROM read_parquet({G}, filename=true) t
    JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE) AND m.barnum >= 22 WHERE {CUTW} AND {SPEC})
  SELECT t.symbol, t.trade_date, t.signal_sec, t.signal_vwap, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.aux_hi_600_px, t.aux_hi_600_sec,
    t.first_low_vwap, t.chan_hi, t.chg_since_run_first_low, t.ols_slope_60, {', '.join('t.'+v for v in LN.values())},
    ({SPEC}) AS spec
  FROM read_parquet({G}, filename=true) t JOIN k ON k.symbol=t.symbol AND k.trade_date=t.trade_date WHERE {CUTW}""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
print(f"rows (all signals of spec tkds): {len(df):,}; spec rows {int(df.spec.sum()):,}; tkd {df.tkd.nunique()}")
def reconstruct(d, col, use_exact=False):
    """ffill anchor within tkd; valid only while the leg's first low was SEEN. With use_exact: when the N-leg has counted the SAME
    events as the 20m leg (lows_N == lows_1200) the two legs armed on the same bar and the engine's exact first_low_vwap applies;
    only an N-leg that re-armed after an N-bar high (lows_N < lows_1200) needs the ffill."""
    keys=d.tkd.to_numpy(); lows=d[col].to_numpy(); px=d.signal_vwap.to_numpy(); out=np.full(len(d), np.nan); prev=None; anchor=np.nan; plow=-1
    l12=d.lows_since_first_low.to_numpy(); ex=d.first_low_vwap.to_numpy()
    for i in range(len(d)):
        if keys[i]!=prev: prev=keys[i]; anchor=np.nan; plow=-1
        l=lows[i]
        if l==0: anchor=px[i]
        elif l<0 or l<plow or (np.isnan(anchor)): anchor=np.nan   # disarmed / reset-without-0 seen / never seen the first low
        out[i]=anchor; plow=l
        if use_exact and l>=0 and l==l12[i] and not np.isnan(ex[i]): out[i]=ex[i]
    return out
# VALIDATION on the 20m leg: reconstruction vs the exact engine anchor
a=reconstruct(df,'lows_since_first_low'); ok=~np.isnan(a); exact=df.first_low_vwap.to_numpy()
print(f"\n20m VALIDATION: reconstructed on {ok.mean()*100:.1f}% of rows; of those, |recon/exact-1| < 1e-9 on {(np.abs(a[ok]/exact[ok]-1)<1e-9).mean()*100:.2f}%")
sp=df.spec.to_numpy(); print(f"   on SPEC rows: reconstructed {ok[sp].mean()*100:.1f}%; exact-match {(np.abs(a[ok&sp]/exact[ok&sp]-1)<1e-9).mean()*100:.2f}%; mismatch median |err| {np.median(np.abs(a[ok&sp]/exact[ok&sp]-1)):.2e}")
for N,col in LN.items():
    an=reconstruct(df,col,use_exact=True); df[f'mag_{N}']=df.signal_vwap/an-1
    same=(df[col]==df.lows_since_first_low)&(df[col]>=0); df[f'same_{N}']=same
print('spec rows where the N-leg == the 20m leg (same first low) %:', {N: round(df.loc[df.spec,f'same_{N}'].mean()*100,1) for N in LN})
df['mag_1200_exact']=df.signal_vwap/df.first_low_vwap-1
df['drop_1200']=df.signal_vwap/df.chan_hi-1   # drop from the 20m high the leg fell from (reset-side anchor, 20m only)
d0=df[df.spec].copy(); d0['ord']=d0.groupby('tkd').cumcount()+1
f=d0.aux_hi_600_px.notna(); d0['ret10']=np.where(f, d0.aux_hi_600_px/d0.entry_px-1, d0.ret); d0['x10']=np.where(f, d0.aux_hi_600_sec, d0.exit_sec)
print('\ncoverage on spec rows (mag_N non-NaN %):', {N: round(d0[f'mag_{N}'].notna().mean()*100,1) for N in LN})
print('quantiles (%, spec rows):'); print((d0[[f'mag_{N}' for N in LN]+['mag_1200_exact','drop_1200','chg_since_run_first_low']]*100).quantile([.05,.1,.25,.5,.75,.9]).round(2).to_string())
print('rho with ordinal:', {N: round(d0[[f'mag_{N}','ord']].corr().iloc[0,1],3) for N in LN}, ' rho(mag_N, crf):', {N: round(d0[[f'mag_{N}','chg_since_run_first_low']].corr().iloc[0,1],3) for N in LN})
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, x, k):
    ss=d['signal_sec'].to_numpy(); es=d[x].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
def row(lbl, m):
    d=d0[m]; r=dict(gate=lbl, mc0_n=len(d), tkd22=d[d.year>=2022].tkd.nunique(), med_ord=d.ord.median(), pct_ord1=round((d.ord==1).mean()*100,1))
    for ex,cc,x in [('MOC','ret','exit_sec'),('10m','ret10','x10')]:
        b=mck(d,x,1); b2=b[b.year>=2022]
        r.update({f'mc1_{ex}_tr_yr':round(len(b)/6.63,1), f'mc1_{ex}_PF1':round(pf(b2[cc])-1,2) if len(b2) else None, f'mc1_{ex}_net':round(b2[cc].sum()*100), f'mc1_{ex}_worst':round(b[cc].min()*100,1) if len(b) else None})
    b=mck(d,'exit_sec',5); b2=b[b.year>=2022]; r.update(dict(mc5_tr_yr=round(len(b)/6.63,1), mc5_PF1=round(pf(b2.ret)-1,2) if len(b2) else None, mc5_net=round(b2.ret.sum()*100), mc5_worst=round(b.ret.min()*100,1) if len(b) else None))
    return r
print(f"\nSPEC v3 + l120>=30: {len(d0):,} trips, {d0.tkd.nunique()} tkd, {d0[d0.year>=2022].tkd.nunique()} tkd22")
print('reference:'); print(pd.DataFrame([row('all (1st bar at mc=1)',np.ones(len(d0),bool)), row('ordinal>=4',(d0.ord>=4).values), row('crf <= -0.1%',(d0.chg_since_run_first_low<=-0.001).values), row('crf <= -0.2%',(d0.chg_since_run_first_low<=-0.002).values)]).to_string(index=False))
print('\n########## A. mag_N BANDS (magnitude since the N-leg first low, %; 0 = this IS the first low): mc=1 MOC | mc=5 ##########')
for col in [f'mag_{N}' for N in LN]+['mag_1200_exact','drop_1200']:
    d=d0.copy(); v=d[col]*100
    if col=='drop_1200': edges=[-1e9,-30,-20,-15,-12,-10,-8,-6,-4,1e9]
    else: edges=[-1e9,-30,-20,-15,-10,-8,-6,-4,-2,-1,-1e-9,1e9]
    d['b']=pd.cut(v,edges); rows=[]
    for k,g in d.groupby('b',observed=True):
        if len(g)<20: continue
        b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
        rows.append(dict(band=str(k), n=len(g), med_ord=g.ord.median(), mc1_PF1=round(pf(b12.ret)-1,2) if len(b12) else None, mc1_net=round(b12.ret.sum()*100), mc5_PF1=round(pf(b52.ret)-1,2) if len(b52) else None, mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1) if len(b5) else None))
    print(f'\n--- {col} ({int(d[col].isna().sum())} NaN of {len(d)}) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## B. CEILINGS as which-bar gates (NaN fails) ##########')
rows=[]
for col in [f'mag_{N}' for N in LN]+['mag_1200_exact']:
    for th in [-0.02,-0.04,-0.06,-0.08,-0.10,-0.15]:
        rows.append(row(f'{col} <= {th*100:.1f}%', (d0[col]<=th).fillna(False).values))
for th in [-0.06,-0.08,-0.10,-0.15,-0.20]: rows.append(row(f'drop_1200 <= {th*100:.0f}%', (d0.drop_1200<=th).fillna(False).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## C. combos with crf and with each other ##########')
rows=[]
for N in [120,300,600,1200]:
    for th in [-0.06,-0.10]:
        rows.append(row(f'mag_{N} <= {th*100:.0f}% & crf <= -0.1%', ((d0[f'mag_{N}']<=th)&(d0.chg_since_run_first_low<=-0.001)).fillna(False).values))
for th in [-0.08,-0.10,-0.15]:
    rows.append(row(f'drop_1200 <= {th*100:.0f}% & crf <= -0.1%', ((d0.drop_1200<=th)&(d0.chg_since_run_first_low<=-0.001)).fillna(False).values))
rows.append(row('mag_120 <= -6% & mag_1200 <= -10%', ((d0.mag_120<=-0.06)&(d0.mag_1200<=-0.10)).fillna(False).values))
rows.append(row('mag_600 <= -8% & drop_1200 <= -10%', ((d0.mag_600<=-0.08)&(d0.drop_1200<=-0.10)).fillna(False).values))
rows.append(row('same_120 (2m leg == 20m leg)', d0.same_120.values)); rows.append(row('NOT same_120 (re-armed after a 2m high)', (~d0.same_120).values))
rows.append(row('same_600', d0.same_600.values)); rows.append(row('NOT same_600', (~d0.same_600).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## D. YEAR table for the best two (mc=1 MOC / mc=5) ##########')
for lbl,m in [('mag_1200 <= -10%',(d0.mag_1200<=-0.10).fillna(False).values),('drop_1200 <= -10%',(d0.drop_1200<=-0.10).fillna(False).values),('mag_120 <= -6%',(d0.mag_120<=-0.06).fillna(False).values),('crf <= -0.1%',(d0.chg_since_run_first_low<=-0.001).values)]:
    d=d0[m]; b1=mck(d,'exit_sec',1); b5=mck(d,'exit_sec',5); rows=[]
    for y in sorted(d0.year.unique()):
        g1=b1[b1.year==y]; g5=b5[b5.year==y]
        rows.append(dict(year=y, mc1_n=len(g1), mc1_PF=round(pf(g1.ret),2) if len(g1) else None, mc1_net=round(g1.ret.sum()*100), mc5_n=len(g5), mc5_PF=round(pf(g5.ret),2) if len(g5) else None, mc5_net=round(g5.ret.sum()*100)))
    print(f'\n--- {lbl} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
