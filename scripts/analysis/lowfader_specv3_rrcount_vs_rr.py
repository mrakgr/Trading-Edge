"""Can the rr-QUALIFIED LEG COUNTS replace rr itself? (2026-09-06, user, breakfast handover)
Frame NORR = SPEC v3 FINAL minus the signal-bar `rr >= 2` gate, on corpus data/lowfader_wl_spec3norr (engine rerun on the 163 tkds
that have a NORR row; every signal row of those days is present, so rr < 2 rows exist). FINAL = NORR & rr >= 2.
Questions: (1) the rr ladder itself on NORR; (2) count gates on NORR vs FINAL at matched n; (3) the RESCUED trips (rr < 2 at the
signal but a high count) — do they pay?; (4) count on top of rr. mc=1 (MOC | 10m), mc=5 MOC, 2022+.
Run: python3 -u scripts/analysis/lowfader_specv3_rrcount_vs_rr.py > data/lowfader_specv3_rrcount_vs_rr.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'")
NORR="""volat_20m > 0.005 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40 AND lows_since_first_low_120 >= 30
    AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04
    AND gap_adj_60 <= 30 AND dollar_vol_60 >= 1e6 AND chg_since_run_first_low <= -0.002"""
RR=[f'lows_rr{k}_{N}' for N in [120,600,1200] for k in [1,2,3]]
df=c.execute(f"""SELECT symbol, trade_date, signal_sec, signal_vwap, entry_sec, exit_sec, entry_px, ret_exit AS ret, aux_hi_600_px, aux_hi_600_sec, spec_ord,
    vol_60/(vol_0945_tape/15.0) AS rr, lows_since_first_low_120 AS lows_120, lows_since_first_low_600 AS lows_600, lows_since_first_low AS lows_1200, {', '.join(RR)}
  FROM read_parquet('data/lowfader_wl_spec3norr/*.parquet') WHERE {NORR}""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True); df['ord']=df.groupby('tkd').cumcount()+1
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret); df['x10']=np.where(f, df.aux_hi_600_sec, df.exit_sec)
for N in [120,600,1200]:
    for k in [1,2,3]: df[f'frac_rr{k}_{N}']=df[f'lows_rr{k}_{N}']/(df[f'lows_{N}']+1)
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
    d=df[m]; r=dict(gate=lbl, mc0_n=len(d), tkd22=d[d.year>=2022].tkd.nunique())
    for ex,cc,x in [('MOC','ret','exit_sec'),('10m','ret10','x10')]:
        b=mck(d,x,1); b2=b[b.year>=2022]
        r.update({f'mc1_{ex}_tr_yr':round(len(b)/6.63,1), f'mc1_{ex}_PF1':round(pf(b2[cc])-1,2) if len(b2) else None, f'mc1_{ex}_net':round(b2[cc].sum()*100), f'mc1_{ex}_worst':round(b[cc].min()*100,1) if len(b) else None})
    b=mck(d,'exit_sec',5); b2=b[b.year>=2022]; r.update(dict(mc5_tr_yr=round(len(b)/6.63,1), mc5_PF1=round(pf(b2.ret)-1,2) if len(b2) else None, mc5_net=round(b2.ret.sum()*100), mc5_worst=round(b.ret.min()*100,1) if len(b) else None))
    return r
def bands(col, edges, title, base=None):
    d=(df if base is None else df[base]).copy(); d['b']=pd.cut(d[col],edges); rows=[]
    for k,g in d.groupby('b',observed=True):
        if len(g)<15: continue
        b1=mck(g,'exit_sec',1); b12=b1[b1.year>=2022]; b5=mck(g,'exit_sec',5); b52=b5[b5.year>=2022]
        rows.append(dict(band=str(k), n=len(g), tkd22=g[g.year>=2022].tkd.nunique(), mc1_PF1=round(pf(b12.ret)-1,2) if len(b12) else None, mc1_net=round(b12.ret.sum()*100), mc1_win=round((b12.ret>0).mean()*100) if len(b12) else None, mc5_PF1=round(pf(b52.ret)-1,2) if len(b52) else None, mc5_net=round(b52.ret.sum()*100), mc5_worst=round(b5.ret.min()*100,1) if len(b5) else None))
    print(f'\n--- {title} ---'); print(pd.DataFrame(rows).to_string(index=False))
rr2=(df.rr>=2).values
print(f"NORR: {len(df):,} trips / {df.tkd.nunique()} tkd / {df[df.year>=2022].tkd.nunique()} tkd22; FINAL (& rr>=2): {int(rr2.sum())} trips / {df[rr2].tkd.nunique()} tkd")
print('\n########## 1. the rr LADDER itself on NORR ##########')
bands('rr', [0,0.5,1,1.5,2,3,5,10,1e9], 'rr at the signal (NORR)')
print('\n########## 2. reference + COUNT gates on NORR (rr gate OFF) ##########')
rows=[row('NORR (no rr gate), 1st bar', np.ones(len(df),bool)), row('FINAL = NORR & rr >= 2', rr2), row('NORR & rr >= 1.5', (df.rr>=1.5).values), row('NORR & rr >= 1', (df.rr>=1).values), row('NORR & rr >= 3', (df.rr>=3).values)]
for N in [120,600,1200]:
    for k in [1,2,3]:
        for th in [1,3,5,10,20,40]:
            rows.append(row(f'NORR & lows_rr{k}_{N} >= {th}', (df[f'lows_rr{k}_{N}']>=th).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 3. FRACTION gates on NORR ##########')
rows=[]
for N in [120,600]:
    for k in [1,2,3]:
        for th in [0.25,0.5,0.75,0.9]:
            rows.append(row(f'NORR & frac_rr{k}_{N} >= {th}', (df[f'frac_rr{k}_{N}']>=th).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 4. the RESCUED trips: rr < 2 at the signal but a high count — do they pay? ##########')
rows=[row('NORR & rr < 2 (all the rescue pool)', ~rr2)]
for N in [120,600]:
    for k in [2,3]:
        for th in [5,10,20,40]:
            rows.append(row(f'rr < 2 & lows_rr{k}_{N} >= {th}', (~rr2)&(df[f'lows_rr{k}_{N}']>=th).values))
for th in [0.5,0.75]: rows.append(row(f'rr < 2 & frac_rr2_600 >= {th}', (~rr2)&(df.frac_rr2_600>=th).values))
rows.append(row('rr in [1.5,2) & lows_rr3_120 >= 10', ((df.rr>=1.5)&(df.rr<2)&(df.lows_rr3_120>=10)).values))
rows.append(row('rr in [1,2) & lows_rr3_120 >= 10', ((df.rr>=1)&(df.rr<2)&(df.lows_rr3_120>=10)).values))
print(pd.DataFrame(rows).to_string(index=False))
bands('rr', [0,0.5,1,1.5,2,3,5,10,1e9], 'rr at the signal, given lows_rr3_120 >= 10', base=(df.lows_rr3_120>=10).values)
bands('rr', [0,0.5,1,1.5,2,3,5,10,1e9], 'rr at the signal, given lows_rr2_600 >= 20', base=(df.lows_rr2_600>=20).values)
print('\n########## 5. COUNT on top of rr (FINAL & count) vs count alone, matched ##########')
rows=[row('FINAL', rr2)]
for N in [120,600]:
    for k in [2,3]:
        for th in [10,20]:
            rows.append(row(f'FINAL & lows_rr{k}_{N} >= {th}', rr2&(df[f'lows_rr{k}_{N}']>=th).values))
            rows.append(row(f'NORR & lows_rr{k}_{N} >= {th} (rr gate off)', (df[f'lows_rr{k}_{N}']>=th).values))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 6. YEAR tables: FINAL vs the best count-only gate ##########')
def years(lbl, m):
    d=df[m]; b1=mck(d,'exit_sec',1); b5=mck(d,'exit_sec',5); rows=[]
    for y in sorted(df.year.unique()):
        g1=b1[b1.year==y]; g5=b5[b5.year==y]
        rows.append(dict(year=y, mc1_n=len(g1), mc1_PF=round(pf(g1.ret),2) if len(g1) else None, mc1_net=round(g1.ret.sum()*100), mc5_n=len(g5), mc5_PF=round(pf(g5.ret),2) if len(g5) else None, mc5_net=round(g5.ret.sum()*100), worst=round(g5.ret.min()*100,1) if len(g5) else None))
    print(f'\n--- {lbl} ---'); print(pd.DataFrame(rows).to_string(index=False))
years('FINAL (rr >= 2)', rr2)
years('NORR & lows_rr3_120 >= 10 (no rr gate)', (df.lows_rr3_120>=10).values)
years('NORR & lows_rr2_600 >= 20 (no rr gate)', (df.lows_rr2_600>=20).values)
years('NORR & rr >= 1 (relaxed rr)', (df.rr>=1).values)
print('\nDONE')
