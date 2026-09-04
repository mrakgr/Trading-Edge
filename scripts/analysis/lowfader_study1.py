"""LowFader STUDY 1 (2026-09-04): LowFlyer's production spec re-derived on the 1s tape.
LONG session-low flush fade, hold to MOC. ret = ret_exit = exit/entry - 1 (LONG sign).
LowFlyer's spec (docs/lowflyer_results.md PRODUCTION SPEC) mapped onto recorded columns:
  1m flush <= -0.7%   -> flush_1m = signal_vwap/vwap_60_prev - 1      (the 1s analog of close/prevClose)
  flush floor >= -12% -> the same, floored
  log-ATR < 0.02      -> volat_20m ceiling (the 1s volatility driver; record-first, banded)
  vol_vs_high >= 0.90 -> vol_60 / vol_60_prior_max
  chg_1d <= -8%       -> entry_px/(close_m1+div_m1) - 1
  chg_20m <= -3%      -> signal_vwap / vwap_1200 - 1   (⚠ vs the 20m VWMA, not the price 20m ago — the nearest recorded analog)
  chg_3d in [-3%,+30%]-> entry_px/(close_m3+div_m3) - 1
  chg_7d >= -5%       -> entry_px/(close_m7+div_m7) - 1
  ADV >= $500k        -> avgvol20_prior * close_m1
  rvol_0945 >= 0.1    -> rvol_0945_honest
  morning             -> entry 09:45-11:30
  float < $300M, breadth x3 -> NOT in v1 (joins to float.db / pct_above_20; noted)
Run: python3 -u scripts/analysis/lowfader_study1.py <dir> > data/lowfader_study1_<tag>.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 260)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl'
q = f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, signal_vwap, entry_px, exit_px, ret_exit, exit_reason,
  volat_20m, vol_60, vol_60_prior_max, vwap_60_prev, vwap_1200, close_m1, div_m1, close_m3, div_m3, close_m7, div_m7,
  avgvol20_prior, rvol_0945_honest, dv_0945_tape, halts_today, sess_high, sess_low, lows_since_first_low_600
  , entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg_1d
  , entry_px/NULLIF(close_m3+coalesce(div_m3,0),0)-1 AS chg_3d
  , entry_px/NULLIF(close_m7+coalesce(div_m7,0),0)-1 AS chg_7d
  , signal_vwap/NULLIF(vwap_60_prev,0)-1 AS flush_1m
  , signal_vwap/NULLIF(vwap_1200,0)-1 AS chg_20m
  , vol_60/NULLIF(vol_60_prior_max,0) AS vol_vs_high
  , avgvol20_prior*close_m1 AS adv
  FROM read_parquet('{DIRP}/*.parquet')"""
df = duckdb.query(q).df(); df['ret'] = df['ret_exit']; df['year'] = pd.to_datetime(df['trade_date']).dt.year; df['entry_min'] = df['entry_sec']//60
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
def year_table(d, name):
    rows=[dict(year=y, **st(g['ret'])) for y,g in d.groupby('year')]; print(f'\n--- YEAR: {name} ---'); print(pd.DataFrame(rows).to_string(index=False))
G = {
 'flush_1m<=-0.7%': df['flush_1m']<=-0.007, 'flush_1m>=-12%': df['flush_1m']>=-0.12, 'vol_vs_high>=0.9': df['vol_vs_high']>=0.9,
 'chg_1d<=-8%': df['chg_1d']<=-0.08, 'chg_20m<=-3%': df['chg_20m']<=-0.03, 'chg_3d in[-3,+30]%': (df['chg_3d']>=-0.03)&(df['chg_3d']<=0.30),
 'chg_7d>=-5%': df['chg_7d']>=-0.05, 'adv>=500k': df['adv']>=5e5, 'rvol_0945>=0.1': df['rvol_0945_honest']>=0.1, 'entry<=11:30': df['entry_min']<=690,
}
G = {k: v.fillna(False) for k,v in G.items()}
spec = np.logical_and.reduce([v.values for v in G.values()]); df['spec']=spec
print(f"corpus {len(df):,} trips  {df.groupby(['symbol','trade_date']).ngroups:,} tkd  {df.trade_date.min()}..{df.trade_date.max()}")
print('\n########## 1. BASE (session-low LONG, hold to MOC) ##########')
print('mc=0:'); print(pd.DataFrame([st(df['ret'])]).to_string(index=False)); b=mc1(df); print('mc=1:'); print(pd.DataFrame([st(b['ret'])]).to_string(index=False)); year_table(b,'BASE mc=1')
print('\n########## 2. GATE-BY-GATE: alone, and remove-one from the full spec (mc=0) ##########')
rows=[dict(gate='NONE', **st(df['ret']))]
for k,m in G.items(): rows.append(dict(gate=f'{k} alone ({m.mean()*100:.0f}% pass)', **st(df[m]['ret'])))
rows.append(dict(gate='FULL SPEC', **st(df[spec]['ret'])))
for k in G:
    m=np.logical_and.reduce([v.values for kk,v in G.items() if kk!=k]); rows.append(dict(gate=f'  drop {k}', **st(df[m]['ret'])))
print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 3. THE SPEC BOOK ##########')
s=df[spec]; print('mc=0:'); print(pd.DataFrame([st(s['ret'])]).to_string(index=False)); bs=mc1(s); print(f'mc=1 (n={len(bs):,}, tkd/yr {bs.groupby("year").size().to_dict()}):'); print(pd.DataFrame([st(bs['ret'])]).to_string(index=False)); year_table(bs,'SPEC mc=1')
print('\n########## 4. LowFlyer\'s sizing lever: the 1m-flush depth, inside the spec minus its flush gates (mc=1) ##########')
core=np.logical_and.reduce([v.values for kk,v in G.items() if not kk.startswith('flush')]); d=mc1(df[core]).copy(); d['b']=pd.cut(d['flush_1m'],[-1,-0.12,-0.07,-0.04,-0.02,-0.01,-0.007,0,1])
rows=[dict(band=str(k), **st(g['ret'])) for k,g in d.groupby('b',observed=True)]; print(pd.DataFrame(rows).to_string(index=False))
print('\n########## 5. bands of the daily gates inside the intraday-gated book (mc=1): where do the 1d/3d/7d floors sit on 1s? ##########')
intra=np.logical_and.reduce([v.values for kk,v in G.items() if kk in ('flush_1m<=-0.7%','flush_1m>=-12%','vol_vs_high>=0.9','adv>=500k','rvol_0945>=0.1','entry<=11:30')]); di=mc1(df[intra]).copy()
for col,edges in [('chg_1d',[-1,-0.5,-0.3,-0.2,-0.15,-0.1,-0.08,-0.05,0,10]),('chg_3d',[-1,-0.3,-0.15,-0.03,0.05,0.15,0.3,0.6,10]),('chg_7d',[-1,-0.3,-0.15,-0.05,0.05,0.2,0.5,10]),('chg_20m',[-1,-0.15,-0.08,-0.05,-0.03,-0.015,0,1]),('volat_20m',[0,0.004,0.006,0.008,0.012,0.02,1])]:
    di['b']=pd.cut(di[col],edges); rows=[dict(band=str(k), **st(g['ret'])) for k,g in di.groupby('b',observed=True)]; print(f'\n--- {col} (intraday-gated mc=1, n={len(di):,}) ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
