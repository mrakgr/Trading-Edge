"""LowFader EXPANDED UNIVERSE (2026-09-05, user): the whole tape (no dv_0945 / n_bars / barnum floors, volat floor 20bp).
Questions: (1) how big is the book beyond the old candidate table and what is it worth; (2) the time-clock dollar_vol_60 as
the liquidity axis (the user's replacement for the first-15m floors); (3) the TODO: does the rr < 0.5 outperformance live in
illiquid names (rr x dv_60 cross); (4) the 20-40bp volat band; (5) spec v2 (+10m cover) on the new names.
mc=0 sampler attribution with tkd counts and PF22; mc=1 replay for the spec book. Corpus data/lowfader_alltape.
Run: python3 -u scripts/analysis/lowfader_alltape.py > data/lowfader_alltape_study.log 2>&1
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_alltape'
c=duckdb.connect('data/trading.db', read_only=True); c.execute("SET memory_limit='8GB'; SET threads=8")
df = c.execute(f"""SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.entry_px, t.ret_exit AS ret, t.aux_hi_600_px, t.aux_hi_600_sec, t.breach_1200,
  t.volat_20m, t.dollar_vol_60, t.dollar_vol_60_bar, t.vol_60, t.dv_0945_tape, t.vol_0945_tape, t.gap_adj_60, t.eff_ewma_10m,
  t.lows_since_first_low_600 AS lows, t.bars_since_first_low_600 AS bars, t.vol_60/NULLIF(t.vol_0945_tape/15.0,0) AS rr,
  t.entry_px/NULLIF(t.close_m1+coalesce(t.div_m1,0),0)-1 AS chg_1d, t.entry_px AS px,
  (m.ticker IS NOT NULL) AS in_old_cand, (w.ticker IS NOT NULL) AS in_old_wl, coalesce(m.barnum, -1) AS barnum_old
  FROM read_parquet('{DIRP}/*.parquet') t
  LEFT JOIN mr_candidate_1s_v2 m ON m.ticker=t.symbol AND m.date=CAST(t.trade_date AS DATE)
  LEFT JOIN lowfader_whitelist w ON w.ticker=t.symbol AND w.date=CAST(t.trade_date AS DATE)""").df()
df['year']=pd.to_datetime(df.trade_date).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
df['rate']=df.lows/df.bars.replace(0,np.nan)
f=df.aux_hi_600_px.notna(); df['ret10']=np.where(f, df.aux_hi_600_px/df.entry_px-1, df.ret); df['x10']=np.where(f, df.aux_hi_600_sec, df.exit_sec)
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(g,cc='ret'):
    s=g[cc]; s2=g.loc[g.year>=2022,cc]
    return dict(n=len(s), tkd=g.tkd.nunique(), tkd22=g.loc[g.year>=2022,'tkd'].nunique(), PF=round(pf(s),3), PF22=round(pf(s2),3) if len(s2) else None, win22=round((s2>0).mean()*100,1) if len(s2) else None, avg22=round(s2.mean()*100,2) if len(s2) else None, net22=round(s2.sum()*100) if len(s2) else None, worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def yc(g,cc='ret'): return {str(y): round(pf(g[g.year==y][cc]),2) for y in YEARS}
def bands(d, col, edges, name, cc='ret'):
    d=d.copy(); d['b']=pd.cut(d[col],edges)
    rows=([dict(band='NaN', **st(d[d[col].isna()],cc))] if d[col].isna().any() else [])+[dict(band=str(k), **st(g,cc), **yc(g,cc)) for k,g in d.groupby('b',observed=True)]
    print(f'\n--- {name}: {col} ---'); print(pd.DataFrame(rows).to_string(index=False))
def mc1(d, exit_col):
    d=d.sort_values(['tkd','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d[exit_col].to_numpy(); keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
print(f"ALL-TAPE corpus: {len(df):,} trips, {df.tkd.nunique():,} tkd, {df.trade_date.min()}..{df.trade_date.max()}")
print('\n########## 1. WHERE THE TRIPS ARE: old candidate universe vs the added names ##########')
rows=[dict(slice='all', **st(df), **yc(df)), dict(slice='in OLD candidate table', **st(df[df.in_old_cand]), **yc(df[df.in_old_cand])),
      dict(slice='  ...and in the old 40bp whitelist (= the base corpus population)', **st(df[df.in_old_wl]), **yc(df[df.in_old_wl])),
      dict(slice='  ...in old cand but NOT the old whitelist (the 20-40bp names)', **st(df[df.in_old_cand&~df.in_old_wl]), **yc(df[df.in_old_cand&~df.in_old_wl])),
      dict(slice='NEW names (not in the old candidate table)', **st(df[~df.in_old_cand]), **yc(df[~df.in_old_cand]))]
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 2. LIQUIDITY on the tape\'s own clock: dollar_vol_60 (time-clock $ in the last 60 tradeable s) ##########')
print('quantiles ($k): old', (df[df.in_old_cand].dollar_vol_60.quantile([.05,.25,.5,.75,.95])/1e3).round(0).to_dict(), ' new', (df[~df.in_old_cand].dollar_vol_60.quantile([.05,.25,.5,.75,.95])/1e3).round(0).to_dict())
e=[-1,1e3,5e3,10e3,25e3,50e3,100e3,250e3,500e3,1e6,2.5e6,1e12]
bands(df,'dollar_vol_60',e,'ALL'); bands(df[~df.in_old_cand],'dollar_vol_60',e,'NEW names'); bands(df,'dv_0945_tape',[-1,1e4,1e5,5e5,1e6,2e6,5e6,2e7,1e12],'ALL (the OLD first-15m floor axis)')
bands(df,'px',[0,0.5,1,2,5,10,20,50,1e6],'ALL: raw entry price')
print('\n########## 3. THE TODO: rr x liquidity — is the quiet-rr edge an illiquidity premium? ##########')
d=df.copy(); d['r']=pd.cut(d.rr,[0,0.25,0.5,1,2,4,8,1e9]); d['l']=pd.cut(d.dollar_vol_60,[-1,5e3,25e3,100e3,500e3,1e12])
for yr,lab in [(2020,'ALL years'),(2022,'2022+')]:
    m=d[d.year>=yr]; print(f'\n--- rr rows x dollar_vol_60 cols, PF / tkd ({lab}) ---')
    print(m.groupby(['r','l'],observed=True).apply(lambda g: f"{pf(g.ret):.2f}/{g.tkd.nunique()}", include_groups=False).unstack().to_string())
bands(df[df.dollar_vol_60<25e3],'rr',[0,0.25,0.5,1,2,4,8,1e9],'ILLIQUID (dv_60 < $25k)'); bands(df[df.dollar_vol_60>=250e3],'rr',[0,0.25,0.5,1,2,4,8,1e9],'LIQUID (dv_60 >= $250k)')
print('\n########## 4. VOLATILITY with the 20-40bp band now visible ##########')
bands(df,'volat_20m',[0.0019,0.003,0.004,0.005,0.006,0.008,0.010,0.015,0.02,1],'ALL'); bands(df[df.in_old_cand],'volat_20m',[0.0019,0.003,0.004,0.005,0.006,0.008,0.010,0.015,0.02,1],'OLD universe')
print('\n########## 5. SPEC v2 on the expanded universe ##########')
spec=((df.volat_20m>0.0039)&(df.volat_20m<=0.010)&(df.eff_ewma_10m<-0.7)&(df.lows>=40)&(df.rate>=0.15)&(df.rr>=2)&(df.chg_1d<=-0.04)&(df.gap_adj_60<=30)).fillna(False).values
s=df[spec]
rows=[dict(slice='spec v2 ALL', **st(s), **yc(s)), dict(slice='  old universe', **st(s[s.in_old_cand]), **yc(s[s.in_old_cand])), dict(slice='  NEW names', **st(s[~s.in_old_cand]), **yc(s[~s.in_old_cand]))]
spec2040=((df.volat_20m>0.0019)&(df.volat_20m<=0.0039)&(df.eff_ewma_10m<-0.7)&(df.lows>=40)&(df.rate>=0.15)&(df.rr>=2)&(df.chg_1d<=-0.04)&(df.gap_adj_60<=30)).fillna(False).values
rows.append(dict(slice='spec v2 gates in the 20-40bp band', **st(df[spec2040]), **yc(df[spec2040])))
print(pd.DataFrame(rows).to_string(index=False))
bands(s,'dollar_vol_60',e,'spec v2'); bands(s,'px',[0,0.5,1,2,5,10,20,50,1e6],'spec v2: raw entry price')
print('\n--- spec v2 mc=1 replay (MOC | 10m), by universe ---')
rows=[]
for lbl,d in [('all',s),('old universe',s[s.in_old_cand]),('NEW names',s[~s.in_old_cand]),('NEW ∧ dv_60>=$25k',s[(~s.in_old_cand)&(s.dollar_vol_60>=25e3)]),('NEW ∧ dv_60>=$100k',s[(~s.in_old_cand)&(s.dollar_vol_60>=100e3)])]:
    for ex,cc,x in [('MOC','ret','exit_sec'),('10m','ret10','x10')]:
        b=mc1(d,x); rows.append(dict(slice=lbl, exit=ex, n=len(b), PF1=round(pf(b[cc])-1,2), PF1_22=round(pf(b[b.year>=2022][cc])-1,2), win22=round((b[b.year>=2022][cc]>0).mean()*100,1), net22=round(b[b.year>=2022][cc].sum()*100), worst=round(b[cc].min()*100,1), **{str(y): round(pf(b[b.year==y][cc])-1,2) for y in YEARS if y>=2022}))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
