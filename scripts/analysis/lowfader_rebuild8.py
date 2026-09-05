"""LowFader REBUILD step 8 (2026-09-05): FlushFader/SpikeFader-inspired features on the 40-100bp base, mc=0.
eff_20m/eff_10m (+ |.| and the 9-EMA), the leg/breakout counters, gap_adj_60. PF with year cols + a 2022+ column.
Run: python3 -u scripts/analysis/lowfader_rebuild8.py data/lowfader_wl_base 0.010 > data/lowfader_rebuild_8.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else "data/lowfader_wl_base"; VMAX = float(sys.argv[2]) if len(sys.argv) > 2 else 0.010
COLS='eff_20m, eff_10m, eff_9ema_20m, eff_9ema_10m, lows_since_first_low_120, lows_since_first_low_180, lows_since_first_low_300, lows_since_first_low_600, bars_since_first_low_600, breach_lo_300, breach_lo_600, breach_lo_1200, breach_lo_sess, gap_60, gap_adj_60, gap_300, gap_adj_300, halts_today'
df = duckdb.query(f"""SELECT trade_date, ret_exit AS ret, {COLS} FROM read_parquet('{DIRP}/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= {VMAX}""").df()
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); m22=df.year>=2022
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g):
    s=g['ret']; s2=g.loc[g.year>=2022,'ret']
    return dict(n=len(s), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, n22=len(s2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1) if len(s2) else None, avg=round(s.mean()*100,2), tail=round((s<-.2).mean()*100,2))
def yc(g): return {str(y): round(pf(g[g.year==y]['ret']),2) for y in YEARS}
def bands(col, edges, note=''):
    d=df.copy(); d['b']=pd.cut(d[col],edges)
    rows=[dict(band='NaN', **st(d[d[col].isna()]), **yc(d[d[col].isna()]))] if d[col].isna().any() else []
    rows+=[dict(band=str(k), **st(g), **yc(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {col} {note} ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"40-{VMAX*1e4:.0f}bp base mc=0: {len(df):,} trips, 2022+ {m22.sum():,}; base PF {pf(df.ret):.3f} / 2022+ {pf(df[m22].ret):.3f}")
print('quantiles:'); print(df[['eff_20m','eff_10m','eff_9ema_10m','lows_since_first_low_300','lows_since_first_low_600','breach_lo_600','gap_60','gap_adj_60']].quantile([.05,.25,.5,.75,.95]).round(3).to_string())
print('\n########## A. EFFICIENCY (signed: negative = a directional DOWN move, the long fades it) ##########')
e=[-1.01,-0.9,-0.7,-0.5,-0.3,-0.15,0,0.15,0.3,0.5,1.01]
bands('eff_20m',e); bands('eff_10m',e); bands('eff_9ema_20m',e); bands('eff_9ema_10m',e)
df['abs_eff_20m']=df.eff_20m.abs(); df['abs_eff_10m']=df.eff_10m.abs()
bands('abs_eff_20m',[-0.01,0.15,0.3,0.4,0.5,0.6,0.7,0.85,1.01],'(FlushFader band [0.3,0.5))'); bands('abs_eff_10m',[-0.01,0.15,0.3,0.5,0.7,0.85,1.01],'(FlushFader floor >= 0.15)')
print('\n########## B. LEG / BREAKOUT COUNTERS ##########')
ci=[-1,0,1,2,3,5,8,12,18,26,40,60,100,1e6]
for c in ['lows_since_first_low_120','lows_since_first_low_180','lows_since_first_low_300','lows_since_first_low_600','breach_lo_300','breach_lo_600','breach_lo_1200','breach_lo_sess']: bands(c,ci)
bands('bars_since_first_low_600',[-1,0,10,30,60,120,300,600,1e6])
print('\n########## C. GAPS (sparsity of the tape) ##########')
gi=[-1,0,5,10,20,30,40,50,55,58,60]
bands('gap_60',gi,'(missing seconds in the last 60 wall-clock s)'); bands('gap_adj_60',gi,'(halt-adjusted)')
bands('gap_adj_300',[-1,0,30,60,120,180,240,270,290,300])
bands('halts_today',[-1,0,1,2,5,100])
print('\nDONE')
