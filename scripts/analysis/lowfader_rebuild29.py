"""LowFader REBUILD step 29 (2026-09-05, user): the FLUSH features inside spec v2 — the principled replacement for the ordinal.
speed_1m = signal_vwap/vwap_60_prev - 1 ; d1m = signal_vwap/hi_60 - 1 (FlushFader's pair, both < -2%); 30s twins; s5/s10 (vs 5/10-bar-ago vwap).
mc=0 ladders (PF-1 22+, tkd), floor sweeps, the same-n rr control, and the mc=1 replay per gate (MOC | 10m cover) + which ordinals the gate selects.
Run: python3 -u scripts/analysis/lowfader_rebuild29.py > data/lowfader_rebuild_29.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
df = duckdb.query("""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit, aux_hi_600_px, aux_hi_600_sec, breach_1200, vol_60/NULLIF(vol_0945_tape/15.0,0) AS rr,
  signal_vwap/NULLIF(vwap_60_prev,0)-1 AS speed_1m, signal_vwap/NULLIF(hi_60,0)-1 AS d1m, signal_vwap/NULLIF(vwap_30_prev,0)-1 AS speed_30s, signal_vwap/NULLIF(hi_30,0)-1 AS d30s,
  signal_vwap/NULLIF(vwap_5_prev,0)-1 AS s5, signal_vwap/NULLIF(vwap_10_prev,0)-1 AS s10, signal_vwap/NULLIF(vwap_1200,0)-1 AS chg_20m, signal_vwap/NULLIF(vwap_60,0)-1 AS d_vwap60
  FROM read_parquet('data/lowfader_wl_spec2/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40
  AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04 AND gap_adj_60 <= 30""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df=df.sort_values(['tkd','signal_sec']).reset_index(drop=True); df['ord']=df.groupby('tkd').cumcount()+1
prev=df.groupby('tkd')['breach_1200'].shift(); df['leg']=((prev.notna())&(df.breach_1200<=prev)).groupby(df.tkd).cumsum(); df['leg_ord']=df.groupby(['tkd','leg']).cumcount()+1
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret_exit); df['x10']=np.where(f, df.aux_hi_600_sec, df.exit_sec)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mc1(d, exit_col):
    d=d.sort_values(['tkd','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d[exit_col].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
def st(g,c='ret_exit'):
    s=g[c]; s2=g.loc[g.year>=2022,c]
    return dict(n=len(s), tkd=g.tkd.nunique(), tkd22=g.loc[g.year>=2022,'tkd'].nunique(), PF1=round(pf(s)-1,2), PF1_22=round(pf(s2)-1,2), win22=round((s2>0).mean()*100,1), avg22=round(s2.mean()*100,2), net22=round(s2.sum()*100), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
print(f"spec v2: {len(df):,} trips {df.tkd.nunique()} tkd  PF-1 22+ MOC {pf(df[df.year>=2022].ret_exit)-1:.2f} / 10m {pf(df[df.year>=2022].ret10)-1:.2f}")
print('NaN: ' + '  '.join(f"{c} {df[c].isna().mean()*100:.1f}%" for c in ['speed_1m','d1m','speed_30s','d30s','s5','s10','chg_20m']))
print('quantiles (%):'); print((df[['speed_1m','d1m','speed_30s','d30s','s5','s10','chg_20m','d_vwap60']].quantile([.05,.25,.5,.75,.95])*100).round(2).to_string())
print('rho with ord:', {c: round(df[[c,'ord']].corr().iloc[0,1],3) for c in ['speed_1m','d1m','speed_30s','s5','chg_20m']})
def bands(col, edges):
    d=df.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()]), c10_PF1_22=round(pf(d[d[col].isna()&(d.year>=2022)].ret10)-1,2))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g), c10_PF1_22=round(pf(g[g.year>=2022].ret10)-1,2), med_ord=g.ord.median()) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {col} (mc=0) ---'); print(pd.DataFrame(rows).to_string(index=False))
e=[-1,-0.08,-0.05,-0.035,-0.025,-0.02,-0.015,-0.01,-0.005,0,1]
for c in ['speed_1m','d1m','speed_30s','d30s','s5','s10']: bands(c,e)
bands('chg_20m',[-1,-0.15,-0.1,-0.07,-0.05,-0.03,-0.02,0,1])
print('\n########## FLOOR sweeps: mc=0 (with same-n rr control) | mc=1 replay (MOC | 10m) | ordinals selected ##########')
rows=[]
gates=[('none',np.ones(len(df),dtype=bool))]
for t in [-0.01,-0.015,-0.02,-0.025,-0.03,-0.04]:
    gates+= [(f'speed_1m<{t*100:g}%',(df.speed_1m<t).values),(f'd1m<{t*100:g}%',(df.d1m<t).values),(f'PAIR<{t*100:g}%',((df.speed_1m<t)&(df.d1m<t)).values)]
for t in [-0.01,-0.015,-0.02,-0.03]: gates.append((f'speed_30s<{t*100:g}%',(df.speed_30s<t).values))
for t in [-0.01,-0.02,-0.03]: gates.append((f's5<{t*100:g}%',(df.s5<t).values))
for lbl,m in gates:
    m=np.nan_to_num(m.astype(float)).astype(bool); d=df[m]
    if len(d)<30: continue
    thr=np.sort(df.rr.values)[::-1][len(d)-1]; cc=df[df.rr>=thr]
    a=st(d); b=mc1(d,'exit_sec'); b10=mc1(d,'x10')
    rows.append(dict(gate=lbl, **{'mc0_'+k:v for k,v in a.items() if k in ('n','tkd22','PF1_22','win22','net22')}, mc0_c10=round(pf(d[d.year>=2022].ret10)-1,2), rr_ctrl=round(pf(cc[cc.year>=2022].ret_exit)-1,2),
                     mc1_n=len(b), mc1_moc=round(pf(b[b.year>=2022].ret_exit)-1,2), mc1_moc_win=round((b[b.year>=2022].ret_exit>0).mean()*100,1), mc1_c10=round(pf(b10[b10.year>=2022].ret10)-1,2), mc1_c10_win=round((b10[b10.year>=2022].ret10>0).mean()*100,1),
                     med_ord=d.ord.median(), pct_ord1=round((d.ord==1).mean()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
