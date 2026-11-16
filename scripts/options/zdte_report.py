#!/usr/bin/env python3
"""First 0DTE report (user 2026-11-16) on zdte_trades.py's every-minute trades (data/options/zdte/trades/*.parquet).
Short at the BID, held to the close; % of the option's mid at entry (premium >= $0.05).
  naked  = (bid - settle) / mid
  hedged = (bid - settle + hd - turn x S x 0.5bp) / mid    (delta rebalanced every minute, 0.5bp of S per share traded)
STR = AP + AC at the same minute (summed). Groups: index = SPY QQQ SPXW IWM, etf = GLD SLV TLT, stock = the 6 names.
Statistics are DAY-CLUSTERED: every minute of a day holds to the same close, so t = mean of the daily means / se over days;
PF on the trades; 'days+' = share of days whose mean is positive.
Features (at the entry minute): skew (P25: skew_p, C25: skew_c, STR: skew_p), fslope = iv_atm / fwd - 1 (0DTE vs the
forward to the next expiry), ivrv_open = iv_atm / rv_open, ivrv30 = iv_atm / rv30, ret_open, from_hi, from_lo, gap,
vol_put_share, mins_left. In-sample deciles (exploration) + OUT-OF-SAMPLE: decile edges from 2023-11..2024-12, applied to
2025-01..2026-11.
    cd research && python3 scripts/options/zdte_report.py > logs/options_zdte_report.log
"""
import glob
import numpy as np, pandas as pd
pd.set_option('display.width', 260); pd.set_option('display.max_columns', 40); pd.set_option('display.max_rows', 400)
GROUP = {**{r: 'index' for r in ['SPY', 'QQQ', 'SPXW', 'IWM']}, **{r: 'etf' for r in ['GLD', 'SLV', 'TLT']},
         **{r: 'stock' for r in ['TSLA', 'NVDA', 'AAPL', 'AMZN', 'META', 'MSFT']}}
D = pd.concat([pd.read_parquet(f) for f in glob.glob('data/options/zdte/trades/*.parquet')], ignore_index=True)
D['mid'] = (D.bid + D.ask) / 2
# 2026-11-16 v2: (a) ETFs / stocks settle on the 17:30 price (holders may exercise on after-hours prices until 17:30; the
# last minute's hedge is carried to then); SPXW: the 16:00 close (s1730 = close). (b) hedge cost per SHARE traded:
# $0.006 (half a penny of spread + ~0.1c fees); SPXW hedged with ES ~ the same per SPY-equivalent unit (S / 10).
CPS = np.where(D.root == 'SPXW', 0.06, 0.006)
D['settle17'] = np.where(D.right == 'C', np.maximum(D.s1730 - D.strike, 0), np.maximum(D.strike - D.s1730, 0))
D['hd17'] = D.hd + D.dlast.fillna(0) * (D.s1730 - D.close)
D['hcost'] = D.turn * CPS
D['static17'] = D.dl * (D.s1730 - D.S) - 2 * D.dl.abs() * CPS
# v4 (2026-11-16): the AFTER-HOURS delta hedge 16:00 -> 17:30 (zdte_trades.py hd_ah / turn_ah); the day hedge is not closed
# at 16:00 but rolled into it; AH shares cost $0.015 each
D['hd_ah'] = D.hd_ah.fillna(0); D['turn_ah'] = D.turn_ah.fillna(0)
D['cost_ah'] = (D.turn - D.dlast.abs().fillna(0)) * CPS + D.turn_ah * 0.015
k = ['root', 'date', 'm']
A = D[D.inst == 'AP'].set_index(k); C = D[D.inst == 'AC'].set_index(k)
j = A.index.intersection(C.index)
S = A.loc[j].copy()
for c in ['bid', 'ask', 'mid', 'settle', 'hd', 'turn', 'settle17', 'hd17', 'hcost', 'static17', 'ask_x', 'hd_x', 'hd_ah', 'cost_ah']: S[c] = A.loc[j, c] + C.loc[j, c]
S['inst'] = 'STR'; S['iv'] = (A.loc[j, 'iv'] + C.loc[j, 'iv']) / 2
D = pd.concat([D[D.inst.isin(['P25', 'C25'])], S.reset_index()], ignore_index=True)
del A, C, S
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid
D['grp'] = D.root.map(GROUP); D['year'] = D.date.dt.year
D['naked16'] = (D.bid - D.settle) / D.mid                         # v1: settled on the 16:00 close
D['naked'] = (D.bid - D.settle17) / D.mid
D['static'] = (D.bid - D.settle17 + D.static17) / D.mid
D['hedged'] = (D.bid - D.settle17 + D.hd17 - D.hcost) / D.mid
# EXIT BEFORE THE BELL (v3): buy the short back at the contract's 15:59 ask (last quote 15:50..15:59), the hedge closed then
D['naked_x'] = (D.bid - D.ask_x) / D.mid
D['hedged_x'] = (D.bid - D.ask_x + D.hd_x - D.hcost) / D.mid
# the MAIN measure: SPXW held to its cash settlement (16:00 close), ETFs / stocks bought back at 15:59 (minute-hedged)
D['main'] = np.where(D.root == 'SPXW', D.hedged, D.hedged_x)
# the user's question (2026-11-16): ALL treated as cash-settled on the 16:00 close (no after-hours exercise), minute-hedged
D['hedged16'] = (D.bid - D.settle + D.hd - D.hcost) / D.mid
D['hedged_ah'] = np.where(D.root == 'SPXW', D.hedged, (D.bid - D.settle17 + D.hd + D.hd_ah - D.cost_ah) / D.mid)
import sys
if len(sys.argv) > 1:                                              # e.g. `zdte_report.py hedged16`: that measure for every root
    D['main'] = D[sys.argv[1]]
    print(f'MAIN MEASURE = {sys.argv[1]} for every root')
D['skew'] = np.where(D.inst == 'C25', D.skew_c, D.skew_p)
D['fslope'] = D.iv_atm / D.fwd - 1
D['ivrv_open'] = D.iv_atm / D.rv_open; D['ivrv30'] = D.iv_atm / D.rv30
D.replace([np.inf, -np.inf], np.nan, inplace=True)
print(f'{len(D):,} trades, {D.groupby(["root", "date"]).ngroups:,} root-days')


def st(x, col='main'):
    v = x[col]
    if len(v) < 200: return pd.Series({'n': len(v)})
    dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return pd.Series({'n': len(v), 'days': len(dm), 'mean%': 100 * v.mean(), 'med%': 100 * v.median(), 'PF': w / l,
                      't_day': dm.mean() / dm.std() * np.sqrt(len(dm)), 'days+%': 100 * (dm > 0).mean(),
                      'worst_day%': 100 * dm.min(), 'hs%': 100 * x.hs.median()})


print('\n== 1) baseline, sold at the bid; naked16 = settled at 16:00; naked / static / hedged = settled at 17:30 (ETFs, stocks)')
for col in ['naked16', 'hedged16', 'naked_x', 'hedged_x', 'naked', 'hedged', 'hedged_ah']:
    t = D.groupby(['grp', 'root', 'inst'])[[col, 'date', 'hs']].apply(lambda x: st(x, col)).unstack(-1)
    print(f'-- {col}: mean% / PF / t_day by root x inst')
    print(pd.concat({m: t[m] for m in ['mean%', 'PF', 't_day']}, axis=1).round(2).to_string())
print('\n-- hedged, by group x inst x year (mean% / PF)')
t = D.groupby(['grp', 'inst', 'year'])[['main', 'date', 'hs']].apply(st)
print(t[['n', 'days', 'mean%', 'PF', 't_day', 'days+%']].round(2).unstack('year').to_string())

print('\n== 2) by time of day (hedged)')
D['tod'] = pd.cut(D.m, [574, 600, 660, 720, 780, 840, 900, 930, 956],
                  labels=['09:35-10', '10-11', '11-12', '12-13', '13-14', '14-15', '15-15:30', '15:30-55'])
t = D.groupby(['grp', 'inst', 'tod'], observed=True)[['main', 'date', 'hs']].apply(st)
print(t[['n', 'mean%', 'PF', 't_day', 'days+%', 'hs%']].round(2).unstack('tod').to_string())

FEATS = ['skew', 'fslope', 'ivrv_open', 'ivrv30', 'ret_open', 'from_hi', 'from_lo', 'gap', 'vol_put_share', 'mins_left']
print('\n== 3) in-sample deciles (hedged mean %, d1 .. d10) per group x inst')
for f in FEATS:
    print(f'-- {f}')
    for (g, i), x in D.groupby(['grp', 'inst']):
        x = x.dropna(subset=[f])
        if len(x) < 5000: continue
        dec = pd.qcut(x[f].rank(method='first'), 10, labels=False) + 1
        mm = x.main.groupby(dec).mean() * 100
        pf = x.main.groupby(dec).apply(lambda v: v[v > 0].sum() / -v[v < 0].sum())
        print(f'   {g:5s} {i:4s} ' + ' '.join(f'{v:+6.1f}' for v in mm.values) + f'  | PF d1 {pf.iloc[0]:.2f} d10 {pf.iloc[-1]:.2f}'
              + f'  | edges d1<{x[f][dec == 1].max():.3g} d10>{x[f][dec == 10].min():.3g}')

print('\n== 4) OUT OF SAMPLE: decile edges from 2023-11..2024-12, results 2025-01..2026-11 (hedged)')
TR, TE = D[D.date < '2025-01-01'], D[D.date >= '2025-01-01']
rows = []
for f in FEATS:
    for (g, i), te in TE.groupby(['grp', 'inst']):
        tr = TR[(TR.grp == g) & (TR.inst == i)][f].dropna()
        if len(tr) < 5000: continue
        lo, hi = tr.quantile(.1), tr.quantile(.9)
        te = te.dropna(subset=[f])
        for lab, m in [('bottom10', te[f] <= lo), ('top10', te[f] >= hi), ('all', te[f].notna())]:
            s = st(te[m])
            rows.append({'feat': f, 'grp': g, 'inst': i, 'side': lab, **s.to_dict()})
O = pd.DataFrame(rows)
print(O.pivot_table(index=['feat', 'grp', 'inst'], columns='side', values=['mean%', 'PF', 't_day']).round(2).to_string())
