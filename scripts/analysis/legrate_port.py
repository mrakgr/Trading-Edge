"""rate_600 PORT (2026-09-06, user): the LowFader leg-rate feature (lows_600 / bars_600 over the 10m leg; LowFader spec floor 0.15)
read post-hoc on FlushFader (v49_spec20, production book frame from flushfader_book.py, mc=1 per tkd) and SpikeFader (s47, SPEC from
spikefader_zq.py, 9m exit, mc=1). Band tables with year columns + floor/ceiling sweeps + PF-1 (raw and bottom-5% trimmed).
Run: python3 -u scripts/analysis/legrate_port.py > data/legrate_port.log
"""
import numpy as np, pandas as pd, duckdb, sys
pd.set_option('display.width', 320)
sys.path.insert(0,'scripts/analysis'); from spikefader_zq import SPEC, DERIVED
sys.path.insert(0,'scripts/equity')
con=duckdb.connect()
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def tpf1(s, q=0.05):
    if len(s)<20: return np.nan
    k=s[s>=np.quantile(s,q)]; return pf(k)-1
def mc1(f):
    f=f.sort_values(['tkd','signal_sec']).reset_index(drop=True); keep=np.zeros(len(f),bool); last=-1; prev=None
    key=f.tkd.values; ent=f.entry_sec.values; ext=f.exit_sec.values
    for i in range(len(f)):
        if key[i]!=prev: prev=key[i]; last=-1
        if ent[i]>=last: keep[i]=True; last=ext[i]
    return f[keep].sort_values(['trade_date','entry_sec']).reset_index(drop=True)
def study(name, b, ycols):
    b=b.copy(); b['rate']=b.k600/b.b600.replace(0,np.nan); b['year']=pd.to_datetime(b.trade_date).dt.year
    print(f"\n==================== {name}: mc=1 book {len(b):,} trades; rate_600 quantiles {b.rate.quantile([.05,.25,.5,.75,.95]).round(3).tolist()}; NaN {b.rate.isna().mean()*100:.1f}% ====================")
    d=b.copy(); d['band']=pd.cut(d.rate,[0,0.05,0.10,0.15,0.20,0.30,0.50,1.01]); rows=[]
    for k,g in d.groupby('band',observed=True):
        if len(g)<20: continue
        r=dict(band=str(k), n=len(g), pct=round(len(g)/len(d)*100), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1))
        for y in ycols: gy=g[g.year==y]; r[str(y)]=round(pf(gy.ret)-1,2) if len(gy)>=10 else None
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
    print('\nfloors / ceilings (mc=1 book re-filtered; PF-1 raw / trimmed, n, net, worst):'); rows=[]
    for lbl,m in [('all',np.ones(len(b),bool))]+[(f'rate >= {t}',(b.rate>=t).values) for t in [0.05,0.10,0.15,0.20,0.30]]+[(f'rate < {t}',(b.rate<t).values) for t in [0.10,0.15,0.20]]:
        g=b[m]; r=dict(gate=lbl, n=len(g), pct=round(len(g)/len(b)*100), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), net=round(g.ret.sum()*100), worst=round(g.ret.min()*100,1))
        for y in ycols: gy=g[g.year==y]; r[str(y)]=round(pf(gy.ret)-1,2) if len(gy)>=10 else None
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
    print('rho(rate, k600):', round(b[['rate','k600']].corr().iloc[0,1],2), ' rho(rate, ret):', round(b[['rate','ret']].corr().iloc[0,1],3))
# ---- FlushFader: the production book frame (flushfader_book.py BOOK_WHERE, esf 450, g60 on, raw px >= $1)

FF='data/equity/flushfader/v49_spec20/trips_p*.parquet'
cols=[r[0] for r in con.execute(f"DESCRIBE SELECT * FROM read_parquet('{FF}')").fetchall()]
RAWPX = 'entry_px/adj_ratio' if 'adj_ratio' in cols else ('raw_px' if 'raw_px' in cols else 'entry_px')
print('FlushFader raw-px expr:', RAWPX)
BOOK_WHERE=f"""gap_60 < 4 AND {RAWPX} >= 1 AND volat_20m >= 0.004 AND signal_sec <= 54000 AND lows_since_first_low_180 >= 3 AND (
    COALESCE(volat_20m*1e4 >= 140, false) OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
    OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false) OR COALESCE((volat_slope_10m - volat_slope_20m)*2e4 > 12, false)
    OR COALESCE(volat_slope_5m*2e4 <= -24, false) OR COALESCE(ac1_ewma < -0.1, false)
    OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= 450, false) OR COALESCE(downticks_since_uptick >= 8, false)
    OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
    OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))"""
f=con.execute(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, lows_since_first_low_600 AS k600, bars_since_first_low_600 AS b600
  FROM read_parquet('{FF}') WHERE {BOOK_WHERE}""").df(); f['tkd']=f.symbol+'|'+f.trade_date.astype(str)
b=mc1(f); print(f"FlushFader production book: {len(b):,} trades (reference 1,369 @ PF {pf(b.ret):.3f})")
study('FlushFader v49 production book (lows_600 / bars_600)', b, [2020,2021,2022,2023,2024,2025,2026]) if '--skip-ff' not in sys.argv else None
# ---- SpikeFader: the S38e spec on the s47 corpus, 9m exit
SF='data/spikefader_s47/*.parquet'
f=con.execute(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, highs_since_first_high_600 AS k600, bars_since_first_high_600 AS b600 {DERIVED}
  FROM read_parquet('{SF}') WHERE {SPEC.replace('dlv >', 'signal_vwap / NULLIF(sess_low,0) - 1 >').replace('ols_slope_1200 >= 0.0030', 'ols_slope_1200 >= 5.0e-05')}""").df(); f['tkd']=f.symbol+'|'+f.trade_date.astype(str)
b=mc1(f); print(f"\nSpikeFader spec book: {len(b):,} trades (reference 3,573 @ PF-1 ~1.18)")
study('SpikeFader s47 spec book (highs_600 / bars_600)', b, [2020,2021,2022,2023,2024,2025,2026])
print('\nDONE')
