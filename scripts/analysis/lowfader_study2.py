"""LowFader STUDY 2 (2026-09-04): the 1m book's two missing levers — FLOAT < $300M and
BREADTH x3 — plus the two 1s inversions (no morning gate; chg_1d BAND [-30%,-8%]) and the
exit choice (MOC vs the 20m-high cover). Point-in-time float: SEC dei:EntityPublicFloat
(USD at period_end), known_date = filing deadline, ASOF <= trade_date; re-anchored to the
entry price CAUSALLY: shares = float_usd / (P_pe * n_pe) per original share,
float_usd_at_entry = shares * entry_px * n_D  (docs/price_adjustment.md; the split factor
cancels exactly as the adj_close ratio did). Breadth: LAG(pct_above_20) = D-1, size x3 >= 0.65.
Run: python3 -u scripts/analysis/lowfader_study2.py <dir> > data/lowfader_study2_<tag>.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 260)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/lowfader_wl_spec'
c = duckdb.connect('data/trading.db', read_only=True); c.execute("ATTACH 'data/equity/float/float.db' AS f (READ_ONLY)")
df = c.execute(f"""
WITH t AS (
  SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, signal_vwap, entry_px, exit_px, ret_exit, n AS n_d,
    volat_20m, vol_60, vol_60_prior_max, vwap_60_prev, vwap_1200, close_m1, div_m1, close_m3, div_m3, close_m7, div_m7,
    avgvol20_prior, rvol_0945_honest, aux_hi_1200_px, aux_hi_300_px,
    entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 AS chg_1d, entry_px/NULLIF(close_m3+coalesce(div_m3,0),0)-1 AS chg_3d,
    entry_px/NULLIF(close_m7+coalesce(div_m7,0),0)-1 AS chg_7d, signal_vwap/NULLIF(vwap_60_prev,0)-1 AS flush_1m,
    signal_vwap/NULLIF(vwap_1200,0)-1 AS chg_20m, vol_60/NULLIF(vol_60_prior_max,0) AS vol_vs_high, avgvol20_prior*close_m1 AS adv
  FROM read_parquet('{DIRP}/*.parquet')),
flt AS (SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0),
tf AS (SELECT t.*, fl.float_usd, fl.period_end AS flt_pe FROM t ASOF LEFT JOIN flt fl ON fl.ticker = t.symbol AND fl.known_date <= CAST(t.trade_date AS DATE)),
tfp AS (SELECT tf.*, da.close AS p_pe, da.n AS n_pe FROM tf ASOF LEFT JOIN daily_adjusted da ON da.ticker = tf.symbol AND da.date <= tf.flt_pe),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS breadth_lag1 FROM read_parquet('data/equity/momentum_v0/breadth.parquet'))
SELECT tfp.*, CASE WHEN float_usd > 0 AND p_pe > 0 AND n_pe > 0 THEN float_usd / (p_pe * n_pe) * entry_px * n_d END AS float_usd_at_entry, br.breadth_lag1
FROM tfp LEFT JOIN br ON br.date = CAST(tfp.trade_date AS DATE)""").df()
df['ret']=df['ret_exit']; df['year']=pd.to_datetime(df['trade_date']).dt.year; df['entry_min']=df['entry_sec']//60
px=df['aux_hi_1200_px'].where(~df['aux_hi_1200_px'].isna(), df['exit_px']); df['ret_20m']=px/df['entry_px']-1
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def st(s): return dict(n=len(s), PF1=round(pf(s)-1,3), net=round(s.sum()*100), win=round((s>0).mean()*100,1), avg=round(s.mean()*100,2), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2))
def mc1(d):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
def years(d, col='ret'): return {y: round(pf(g[col])-1,2) for y,g in d.groupby('year')}
G1 = {'flush>=-12%': df['flush_1m']>=-0.12, 'vol_vs_high>=0.9': df['vol_vs_high']>=0.9, 'chg_1d<=-8%': df['chg_1d']<=-0.08,
      'chg_20m<=-3%': df['chg_20m']<=-0.03, 'chg_3d in[-3,+30]': (df['chg_3d']>=-0.03)&(df['chg_3d']<=0.30), 'chg_7d>=-5%': df['chg_7d']>=-0.05,
      'entry<=11:30': df['entry_min']<=690}
G1={k:v.fillna(False) for k,v in G1.items()}
spec1 = np.logical_and.reduce([v.values for v in G1.values()])                                   # the 1m spec (adv/rvol enforced by the whitelist)
spec2 = np.logical_and.reduce([v.values for k,v in G1.items() if k not in ('entry<=11:30','chg_7d>=-5%')]) & (df['chg_1d']>=-0.30).fillna(False).values   # + the 1s inversions
print(f"corpus {len(df):,}   float covered {df['float_usd_at_entry'].notna().mean()*100:.1f}%   breadth covered {df['breadth_lag1'].notna().mean()*100:.1f}%")
def block(mask, label):
    s=df[mask]; b=mc1(s)
    print(f"\n########## {label} ##########")
    rows=[dict(view='mc=0', **st(s['ret'])), dict(view='mc=1', **st(b['ret'])), dict(view='mc=1, 20m-high cover', **st(b['ret_20m']))]
    print(pd.DataFrame(rows).to_string(index=False)); print("  mc=1 by year:", years(b), "  tkd/yr:", b.groupby('year').size().to_dict())
    return s,b
s1,b1=block(spec1, "1m SPEC as-is (no float, no breadth)")
s2,b2=block(spec2, "1s SPEC: drop morning gate, drop chg_7d, chg_1d BAND [-30,-8]")
print("\n########## FLOAT: the 1m book's biggest lever, on 1s ##########")
for lab,sm in [('1m spec',spec1),('1s spec',spec2)]:
    s=df[sm]; b=mc1(s).copy(); b['fb']=pd.cut(b['float_usd_at_entry']/1e6,[0,50,150,300,1000,3000,1e9])
    print(f"\n--- {lab}, mc=1 by float band ($M at entry) ---"); rows=[dict(band=str(k), **st(g['ret'])) for k,g in b.groupby('fb',observed=True)]
    rows.append(dict(band='float NULL', **st(b[b['float_usd_at_entry'].isna()]['ret']))); print(pd.DataFrame(rows).to_string(index=False))
    m=(b['float_usd_at_entry']<3e8); print(f"  float<300M: {st(b[m]['ret'])}   years {years(b[m])}")
    print(f"  float>=300M: {st(b[~m & b['float_usd_at_entry'].notna()]['ret'])}")
print("\n########## BREADTH x3 (D-1 pct_above_20 >= 0.65): a size-up, not a gate ##########")
for lab,sm in [('1m spec',spec1),('1s spec',spec2),('1s spec & float<300M',spec2 & (df['float_usd_at_entry']<3e8).fillna(False).values)]:
    b=mc1(df[sm]).copy(); hi=b['breadth_lag1']>=0.65
    w=np.where(hi,3.0,1.0); r=b['ret'].to_numpy()*w
    print(f"\n--- {lab} (mc=1 n={len(b)}) ---")
    print(f"  breadth>=0.65: {st(b[hi]['ret'])}\n  breadth<0.65:  {st(b[~hi]['ret'])}")
    print(f"  equal weight: net {b['ret'].sum()*100:.0f}  PF-1 {pf(b['ret'])-1:.3f}   |  x3 on breadth: net {r.sum()*100:.0f}  net/exposure {r.sum()*100/w.mean():.0f}  PF-1 {pf(pd.Series(r))-1:.3f}  worst {r.min()*100:.1f}")
    bb=b.copy(); bb['ret']=bb['ret_20m']; r2=bb['ret'].to_numpy()*w; print(f"  same with the 20m-high cover: equal PF-1 {pf(bb['ret'])-1:.3f} net {bb['ret'].sum()*100:.0f} | x3 net/exposure {r2.sum()*100/w.mean():.0f} PF-1 {pf(pd.Series(r2))-1:.3f}")
print('\nDONE')
