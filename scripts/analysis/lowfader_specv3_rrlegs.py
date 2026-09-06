"""LowFader SPEC v3 FINAL (v3 + l120>=30 + crf<=-0.2%): the rr-QUALIFIED leg counts (2026-09-06, user).
Corpus data/lowfader_wl_spec3 (engine rerun on the 195 spec-v3 tkds; 2026-09-06 columns lows_rr{1,2,3}_{120,180,300,600,1200},
reset_hi_N, first_low_px_N; Ord gates now include l120>=30 and crf<=-0.2%). Validates spec_ord vs the post-hoc spec, and the
§L15 post-hoc first-low reconstruction vs the engine's stamps. mc=1 (MOC | 10m), mc=5 MOC, 2022+.
Run: python3 -u scripts/analysis/lowfader_specv3_rrlegs.py > data/lowfader_specv3_rrlegs.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
SPEC="""volat_20m > 0.005 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40 AND lows_since_first_low_120 >= 30
    AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04
    AND gap_adj_60 <= 30 AND dollar_vol_60 >= 1e6"""
RR=[f'lows_rr{k}_{N}' for N in [120,180,300,600,1200] for k in [1,2,3]]
df=c.execute(f"""SELECT symbol, trade_date, signal_sec, signal_vwap, entry_sec, exit_sec, entry_px, ret_exit AS ret, aux_hi_600_px, aux_hi_600_sec,
    spec_ord, chg_since_run_first_low AS crf, first_low_vwap, chan_hi, lows_since_first_low AS lows_1200, lows_since_first_low_120 AS lows_120, lows_since_first_low_180 AS lows_180,
    lows_since_first_low_300 AS lows_300, lows_since_first_low_600 AS lows_600, bars_since_first_low_600, vol_60/(vol_0945_tape/15.0) AS rr,
    reset_hi_120, reset_hi_180, reset_hi_300, reset_hi_600, reset_hi_1200, first_low_px_120, first_low_px_180, first_low_px_300, first_low_px_600, {', '.join(RR)},
    ({SPEC}) AS spec3, ({SPEC} AND chg_since_run_first_low <= -0.002) AS spec
  FROM read_parquet('data/lowfader_wl_spec3/*.parquet')""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True)
print(f"rows {len(df):,}; spec3 {int(df.spec3.sum()):,}; spec (+crf) {int(df.spec.sum()):,}; tkd {df.tkd.nunique()}")
# --- validation 1: engine spec_ord vs the post-hoc spec (the chg_1d boundary: engine = signal vwap, post-hoc = fill px)
e=df.spec_ord>0; p=df.spec
print(f"spec_ord>0: {int(e.sum())}; post-hoc spec: {int(p.sum())}; both {int((e&p).sum())}; engine-only {int((e&~p).sum())}; posthoc-only {int((~e&p).sum())}")
x=df[e&~p]; print('  engine-only rows: chg_1d(signal) vs chg_1d(fill) boundary?', ((x.signal_vwap/(df.loc[x.index,'entry_px']/(1+0))).describe() if len(x) else 'none'))
# --- validation 2: the §L15 reconstruction vs the engine stamps
def reconstruct(d, col):
    keys=d.tkd.to_numpy(); lows=d[col].to_numpy(); px=d.signal_vwap.to_numpy(); out=np.full(len(d), np.nan); prev=None; anchor=np.nan; plow=-1
    l12=d.lows_1200.to_numpy(); ex=d.first_low_vwap.to_numpy()
    for i in range(len(d)):
        if keys[i]!=prev: prev=keys[i]; anchor=np.nan; plow=-1
        l=lows[i]
        if l==0: anchor=px[i]
        elif l<0 or l<plow or np.isnan(anchor): anchor=np.nan
        out[i]=anchor; plow=l
        if l>=0 and l==l12[i] and not np.isnan(ex[i]): out[i]=ex[i]
    return out
for N in [120,180,300,600]:
    a=reconstruct(df,f'lows_{N}'); ex=df[f'first_low_px_{N}'].to_numpy(); ok=~np.isnan(a)&~np.isnan(ex); s=df.spec3.to_numpy()
    print(f"  §L15 recon vs engine first_low_px_{N} on spec3 rows: recon coverage {ok[s].mean()*100:.1f}%, exact-match {(np.abs(a[ok&s]/ex[ok&s]-1)<1e-9).mean()*100:.2f}%, engine coverage {(~np.isnan(ex[s])).mean()*100:.1f}%")
d0=df[df.spec].copy(); d0['ord']=d0.groupby('tkd').cumcount()+1
d3=df[df.spec3].copy(); d3['ord']=d3.groupby('tkd').cumcount()+1
for d in (d0,d3):
    f=d.aux_hi_600_px.notna(); d['ret10']=np.where(f, d.aux_hi_600_px/d.entry_px-1, d.ret); d['x10']=np.where(f, d.aux_hi_600_sec, d.exit_sec)
    for N in [120,180,300,600,1200]:
        for k in [1,2,3]: d[f'frac_rr{k}_{N}']=d[f'lows_rr{k}_{N}']/(d[f'lows_{N}']+1)
        d[f'drop_reset_{N}']=d.signal_vwap/d[f'reset_hi_{N}']-1
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mck(d, x, k):
    ss=d['signal_sec'].to_numpy(); es=d[x].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),bool); open_ex=[]; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: open_ex=[]; prev=keys[i]
        open_ex=[e for e in open_ex if e>ss[i]]
        if len(open_ex)<k: keep[i]=True; open_ex.append(es[i])
    return d[keep]
def row(base, lbl, m):
    d=base[m]; r=dict(gate=lbl, mc0_n=len(d), tkd22=d[d.year>=2022].tkd.nunique(), med_ord=d.ord.median())
    for ex,cc,x in [('MOC','ret','exit_sec'),('10m','ret10','x10')]:
        b=mck(d,x,1); b2=b[b.year>=2022]
        r.update({f'mc1_{ex}_tr_yr':round(len(b)/6.63,1), f'mc1_{ex}_PF1':round(pf(b2[cc])-1,2) if len(b2) else None, f'mc1_{ex}_net':round(b2[cc].sum()*100), f'mc1_{ex}_worst':round(b[cc].min()*100,1) if len(b) else None})
    b=mck(d,'exit_sec',5); b2=b[b.year>=2022]; r.update(dict(mc5_tr_yr=round(len(b)/6.63,1), mc5_PF1=round(pf(b2.ret)-1,2) if len(b2) else None, mc5_net=round(b2.ret.sum()*100), mc5_worst=round(b.ret.min()*100,1) if len(b) else None))
    return r
def bands(base, col, edges, title):
    d=base.copy(); d['b']=pd.cut(d[col],edges); rows=[]
    for k,g in d.groupby('b',observed=True):
        if len(g)<15: continue
        b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
        rows.append(dict(band=str(k), n=len(g), tkd22=g[g.year>=2022].tkd.nunique(), med_ord=g.ord.median(), mc1_PF1=round(pf(b12.ret)-1,2) if len(b12) else None, mc1_net=round(b12.ret.sum()*100), mc5_PF1=round(pf(b52.ret)-1,2) if len(b52) else None, mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1) if len(b5) else None))
    print(f'\n--- {title} ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"\nSPEC v3 FINAL (+crf<=-0.2%): {len(d0):,} trips, {d0.tkd.nunique()} tkd, {d0[d0.year>=2022].tkd.nunique()} tkd22 | spec3 (no crf): {len(d3):,} trips")
print('reference:'); print(pd.DataFrame([row(d3,'spec3 (no crf), 1st bar',np.ones(len(d3),bool)), row(d0,'SPEC FINAL (crf<=-0.2%)',np.ones(len(d0),bool)), row(d0,'FINAL & spec_ord>=1 (engine)',(d0.spec_ord>=1).values), row(d0,'FINAL & spec_ord>=3',(d0.spec_ord>=3).values)]).to_string(index=False))
print('\nquantiles on FINAL rows:'); print(d0[RR+[f'frac_rr2_{N}' for N in [120,600,1200]]+['rr']].quantile([.1,.25,.5,.75,.9]).round(2).to_string())
print('rho with ordinal:', {x: round(d0[[x,'ord']].corr().iloc[0,1],2) for x in RR})
print('\n########## A. rr-qualified COUNT bands (FINAL frame): mc=1 MOC | mc=5 ##########')
for N in [120,300,600,1200]:
    for k in [1,2,3]:
        bands(d0, f'lows_rr{k}_{N}', [-1,0,2,5,10,20,40,1e9], f'lows_rr{k}_{N} (FINAL)')
print('\n########## B. FRACTION bands (rr-qualified lows / all lows in the leg) ##########')
for N in [120,600,1200]:
    for k in [1,2]:
        bands(d0, f'frac_rr{k}_{N}', [-0.01,0.1,0.25,0.5,0.75,0.9,1.01], f'frac_rr{k}_{N} (FINAL)')
print('\n########## C. FLOORS as gates on the FINAL frame, and on spec3 (no crf) to see if a count can SUBSTITUTE for crf ##########')
rows=[]
for N in [120,300,600,1200]:
    for k in [1,2,3]:
        for th in [1,3,5,10,20]:
            rows.append(row(d0, f'FINAL & lows_rr{k}_{N} >= {th}', (d0[f'lows_rr{k}_{N}']>=th).values))
print(pd.DataFrame(rows).to_string(index=False))
rows=[]
for N in [120,600,1200]:
    for k in [2,3]:
        for th in [3,5,10,20]:
            rows.append(row(d3, f'spec3 & lows_rr{k}_{N} >= {th}', (d3[f'lows_rr{k}_{N}']>=th).values))
for k in [1,2]:
    for th in [0.25,0.5,0.75]:
        rows.append(row(d3, f'spec3 & frac_rr{k}_600 >= {th}', (d3[f'frac_rr{k}_600']>=th).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## D. the RESET-HIGH anchors (engine-exact): drop from the last N-high that reset the leg ##########')
print(d0[[f'drop_reset_{N}' for N in [120,180,300,600,1200]]].describe().round(4).to_string())
for N in [120,300,600,1200]:
    bands(d0, f'drop_reset_{N}', [-1e9,-0.30,-0.20,-0.15,-0.10,-0.08,-0.06,-0.04,-0.02,1e9], f'drop_reset_{N} (FINAL; NaN = no N-high yet today)')
rows=[row(d0,'FINAL & no 2m-high reset yet (NaN)', d0.drop_reset_120.isna().values), row(d0,'FINAL & had a 2m-high reset', d0.drop_reset_120.notna().values)]
for N in [120,300,600]:
    for th in [-0.04,-0.08,-0.15]: rows.append(row(d0, f'FINAL & drop_reset_{N} <= {th*100:.0f}%', (d0[f'drop_reset_{N}']<=th).fillna(False).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## E. YEAR table: SPEC FINAL (mc=1 MOC / 10m / mc=5) ##########')
b1=mck(d0,'exit_sec',1); b10=mck(d0,'x10',1); b5=mck(d0,'exit_sec',5); rows=[]
for y in sorted(d0.year.unique()):
    g1=b1[b1.year==y]; g10=b10[b10.year==y]; g5=b5[b5.year==y]
    rows.append(dict(year=y, n=len(g1), MOC_PF=round(pf(g1.ret),2), MOC_net=round(g1.ret.sum()*100), MOC_win=round((g1.ret>0).mean()*100), cover10_PF=round(pf(g10.ret10),2), cover10_net=round(g10.ret10.sum()*100), mc5_n=len(g5), mc5_PF=round(pf(g5.ret),2), mc5_net=round(g5.ret.sum()*100), worst=round(g5.ret.min()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
