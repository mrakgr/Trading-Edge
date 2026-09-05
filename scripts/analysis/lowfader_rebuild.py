"""LowFader REBUILD (2026-09-05): rebuild the spec from scratch on the base corpus, starting from volatility.
Views: mc=0 (every session-low trip, sampler attribution) first; mc=1 as the control line.
Step 1: the bare book (every session low, hold to MOC) overall + by year; then by volat_20m tier x year.
Run: python3 -u scripts/analysis/lowfader_rebuild.py <dir> [step] > data/lowfader_rebuild_<step>.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 300)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl_base'
STEP = sys.argv[2] if len(sys.argv) > 2 else '1'
q = f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit, volat_20m
  FROM read_parquet('{DIRP}/*.parquet')"""
df = duckdb.query(q).df(); df['ret'] = df['ret_exit']; df['year'] = pd.to_datetime(df['trade_date']).dt.year
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(s): return dict(n=len(s), PF1=round(pf(s)-1,3), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), p5=round(s.quantile(.05)*100,1), tail=round((s<-.2).mean()*100,2))
def mc1(d):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
YEARS = sorted(df['year'].unique())
def year_cols(g):
    """PF-1 per year, as columns."""
    return {str(y): (round(pf(gg['ret'])-1,2) if len(gg) else None) for y,gg in ((y, g[g['year']==y]) for y in YEARS)}
def band_table(d, col, edges, name):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=[dict(band=str(k), **st(g['ret']), **year_cols(g)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} bands (year columns = PF-1) ---'); print(pd.DataFrame(rows).to_string(index=False))
print(f"corpus {len(df):,} trips  {df.groupby(['symbol','trade_date']).ngroups:,} tkd  {df.trade_date.min()}..{df.trade_date.max()}")
if STEP == '1':
    print('\n########## 1a. THE BARE BOOK: every session low, hold to MOC ##########')
    rows=[dict(view='mc=0', **st(df['ret']), **year_cols(df))]; b=mc1(df); rows.append(dict(view='mc=1', **st(b['ret']), **year_cols(b)))
    print(pd.DataFrame(rows).to_string(index=False))
    print('\n--- mc=0 by year ---'); print(pd.DataFrame([dict(year=y, **st(g['ret'])) for y,g in df.groupby('year')]).to_string(index=False))
    print('\n########## 1b. volat_20m distribution (trips) ##########')
    v=df['volat_20m']; print(f"NaN {v.isna().mean()*100:.1f}%"); print((v.quantile([.01,.05,.1,.25,.5,.75,.9,.95,.99])*1e4).round(1).rename('bp').to_string())
    print('\n########## 1c. mc=0 by volat_20m tier (bp) ##########')
    edges=[0,0.001,0.002,0.003,0.004,0.006,0.008,0.010,0.015,0.020,0.030,0.050,1.0]
    band_table(df, 'volat_20m', edges, 'mc=0')
    band_table(b, 'volat_20m', edges, 'mc=1 (control)')
print('\nDONE')
