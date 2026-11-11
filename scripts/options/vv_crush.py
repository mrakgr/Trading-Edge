#!/usr/bin/env python3
"""VV earnings play, CRUSH exit: the EARN trades of vv_deciles.py (entry at the close the trading day before the
reaction), closed at the REACTION day's close NBBO instead of held to expiry.

  straddle  short the front ATM straddle; exit = buy it back at the reaction day's close
  calendar  short front / long back at the same K (call, put, both); exit = buy front, sell back at the reaction close
⚠ ThetaData EOD gives only the LAST NBBO of the day: "the morning after" is approximated by the reaction day's close.

Fill model: every leg, in and out, trades at mid -/+ f * half-spread (f=0 mid, f=1 natural = cross the spread).
Also the expiry hold re-priced under the same f (expiry legs settle at intrinsic -> no exit spread).
Returns: % of the credit (straddle) / debit (calendar), at the f-adjusted entry price; and bp of spot.
Extra feature: fwd_ratio = iv_front / forward vol between front and back, fwd var = (iv_b^2 t_b - iv_f^2 t_f)/(t_b - t_f).

    cd research && python3 scripts/options/vv_crush.py
writes data/options/vv/earn_trades.parquet  (one row per event: features, entry/exit quotes, P&L under f)
SH-18/19/19b in docs/straddleharvester_results.md
"""
import sys, os, duckdb, numpy as np, pandas as pd
sys.argv = ['x', 'NONE']
exec(open('scripts/options/vv_deciles.py').read().split('pd = __import__')[0])
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)

E = con.sql("""select c.root, c.ticker, c.date, c.exp, c.exp_b, c.dte, c.dte_b, c.strike, c.S, c.S_T, c.iv, c.iv_b,
      c.bid_c, c.ask_c, c.bid_p, c.ask_p, c.bbid_c, c.bask_c, c.bbid_p, c.bask_p, c.xbid_c, c.xask_c, c.xbid_p, c.xask_p,
      c.iv30_rv30, c.ts_slope_0_45, c.avg_vol30, f.next_react
    from C_EARN c join F f using (root, date)""").df()
print('events', len(E), flush=True)

# reaction-day close NBBO of the 4 legs (eod_universe chunks are per root-quarter: one join over all of them)
K = E[['root', 'next_react', 'strike', 'exp', 'exp_b']].copy()
Q = duckdb.sql("""select q.root, q.date, q.strike, q.expiration, q.right, q.bid, q.ask
    from read_parquet('data/options/eod_universe/*.parquet') q
    join (select root, next_react::date d, strike, exp::date e from K union select root, next_react::date, strike, exp_b::date from K) k
      on q.root = k.root and q.date = k.d and q.strike = k.strike and q.expiration = k.e""").df()
Q = Q.drop_duplicates(['root', 'date', 'strike', 'expiration', 'right'])
Q['date'] = pd.to_datetime(Q.date); Q['expiration'] = pd.to_datetime(Q.expiration)
E['next_react'] = pd.to_datetime(E.next_react); E['exp'] = pd.to_datetime(E.exp); E['exp_b'] = pd.to_datetime(E.exp_b)
for ecol, pre in [('exp', 'r'), ('exp_b', 'rb')]:
    for rt in ['C', 'P']:
        x = Q[Q.right == rt][['root', 'date', 'strike', 'expiration', 'bid', 'ask']].rename(columns={
            'date': 'next_react', 'expiration': ecol, 'bid': f'{pre}bid_{rt.lower()}', 'ask': f'{pre}ask_{rt.lower()}'})
        E = E.merge(x, on=['root', 'next_react', 'strike', ecol], how='left')
print('reaction quotes found', int(E.rbid_c.notna().sum()), int(E.rbbid_c.notna().sum()), flush=True)
E['exp_on_react'] = pd.to_datetime(E.exp) == pd.to_datetime(E.next_react)

def px(b, a, f, side):  # side +1 = we buy (pay up), -1 = we sell (give up)
    return (b + a) / 2 + side * f * (a - b) / 2

ok_q = lambda b, a: (b >= 0) & (a > 0) & (a >= b)
for f in [0, 0.25, 0.5, 1]:
    t = f'{int(f*100)}'
    cred = px(E.bid_c, E.ask_c, f, -1) + px(E.bid_p, E.ask_p, f, -1)
    E[f'st_cred_{t}'] = cred
    E[f'st_crush_{t}'] = cred - (px(E.rbid_c, E.rask_c, f, +1) + px(E.rbid_p, E.rask_p, f, +1))
    E[f'st_exp_{t}'] = cred - (E.S_T - E.strike).abs()
    for leg, (fb, fa, bb, ba, rfb, rfa, rbb, rba, xb, xa) in {
            'c': ('bid_c', 'ask_c', 'bbid_c', 'bask_c', 'rbid_c', 'rask_c', 'rbbid_c', 'rbask_c', 'xbid_c', 'xask_c'),
            'p': ('bid_p', 'ask_p', 'bbid_p', 'bask_p', 'rbid_p', 'rask_p', 'rbbid_p', 'rbask_p', 'xbid_p', 'xask_p')}.items():
        deb = px(E[bb], E[ba], f, +1) - px(E[fb], E[fa], f, -1)
        crush = px(E[rbb], E[rba], f, -1) - px(E[rfb], E[rfa], f, +1) - deb
        intr = np.maximum(E.S_T - E.strike, 0) if leg == 'c' else np.maximum(E.strike - E.S_T, 0)
        E[f'cal{leg}_deb_{t}'] = deb; E[f'cal{leg}_crush_{t}'] = crush
        E[f'cal{leg}_exp_{t}'] = px(E[xb], E[xa], f, -1) - intr - deb
    for k in ['deb', 'crush', 'exp']:
        E[f'cal_{k}_{t}'] = E[f'calc_{k}_{t}'] + E[f'calp_{k}_{t}']
E['crush_quote_ok'] = ok_q(E.rbid_c, E.rask_c) & ok_q(E.rbid_p, E.rask_p) & ok_q(E.rbbid_c, E.rbask_c) & ok_q(E.rbbid_p, E.rbask_p)
tf, tb = E.dte / 365, E.dte_b / 365
fv = (E.iv_b**2 * tb - E.iv**2 * tf) / (tb - tf)
E['fwd_vol'] = np.sqrt(fv.where(fv > 0))
E['fwd_ratio'] = E.iv / E.fwd_vol
E['gate'] = (E.avg_vol30 >= 1.5e6) & (E.iv30_rv30 >= 1.25) & (E.ts_slope_0_45 <= -0.00406)
E.to_parquet(f'{D}/earn_trades.parquet', index=False)
print('crush quotes ok', int(E.crush_quote_ok.sum()), 'of', len(E), '| front expires on reaction day', int(E.exp_on_react.sum()))

V = E[E.crush_quote_ok & (E.cal_deb_0 > 0.005 * E.S)]
def tab(V, label):
    out = []
    for hold in ['crush', 'exp']:
        for f in ['0', '25', '50', '100']:
            s = V[f'st_{hold}_{f}']; c = V[f'cal_{hold}_{f}']
            out.append(dict(hold=hold, f=f, n=len(V),
                st_ret=100 * (s / V[f'st_cred_{f}']).mean(), st_ret_pooled=100 * s.sum() / V[f'st_cred_{f}'].sum(),
                st_bp=1e4 * (s / V.S).mean(), st_win=100 * (s > 0).mean(),
                cal_ret=100 * (c / V[f'cal_deb_{f}']).where(V[f'cal_deb_{f}'] > 0).mean(),
                cal_ret_pooled=100 * c.sum() / V[f'cal_deb_{f}'].sum(),
                cal_bp=1e4 * (c / V.S).mean(), cal_win=100 * (c > 0).mean()))
    print(f'\n=== {label} (ret = % of credit/debit, per-trade mean; pooled = sum/sum; f = fraction of half-spread paid per leg) ===')
    print(pd.DataFrame(out).round(1).to_string(index=False))
tab(V, 'ALL earnings events')
tab(V[V.gate], 'VV gate')
tab(V[~V.gate], 'rejected by VV gate')
for feat in ['ts_slope_0_45', 'fwd_ratio', 'iv30_rv30']:
    W = V.dropna(subset=[feat]).copy(); W['d'] = pd.qcut(W[feat], 5, labels=False) + 1
    g = W.groupby('d').apply(lambda x: pd.Series(dict(lo=x[feat].min(), hi=x[feat].max(), n=len(x),
        st_ret_mid=100 * (x.st_crush_0 / x.st_cred_0).mean(), st_ret_q=100 * (x.st_crush_25 / x.st_cred_25).mean(),
        cal_ret_mid=100 * (x.cal_crush_0 / x.cal_deb_0).mean(), cal_ret_q=100 * (x.cal_crush_25 / x.cal_deb_25).mean())))
    print(f'\n--- crush exit, quintiles of {feat} (q = f 0.25) ---'); print(g.round(4).to_string())

# ---- SH-19b: single-side calendars (call / put / both), both legs' debit > 0.25% of S ----
V2 = E[E.crush_quote_ok & (E.calc_deb_0 > 0.0025 * E.S) & (E.calp_deb_0 > 0.0025 * E.S)].copy()
out = []
for hold in ['crush', 'exp']:
    for f in ['0', '25', '50', '100']:
        d = dict(hold=hold, f=f)
        for k, lab in [('cal', 'both'), ('calc', 'call'), ('calp', 'put')]:
            d[lab + '_ret'] = 100 * (V2[f'{k}_{hold}_{f}'] / V2[f'{k}_deb_{f}'])[V2[f'{k}_deb_{f}'] > 0].mean()
        out.append(d)
print(f'\n=== single-side calendars, n={len(V2)} (% of debit) ==='); print(pd.DataFrame(out).round(1).to_string(index=False))
V2['q'] = pd.qcut(V2.fwd_ratio, 5, labels=False) + 1
print(V2.groupby('q')[V2.columns].apply(lambda x: pd.Series({'lo': x.fwd_ratio.min(), 'n': len(x),
    **{f'{k}_{f}': 100 * (x[f'{k}_crush_{f}'] / x[f'{k}_deb_{f}']).mean() for k in ['cal', 'calc', 'calp'] for f in ['0', '25']}})).round(1).to_string())
V2['call_otm'] = V2.strike >= V2.S
print(V2.groupby('call_otm')[V2.columns].apply(lambda x: pd.Series({'n': len(x),
    'call_cost': 100 * ((x.calc_deb_100 - x.calc_deb_0) / x.calc_deb_0).median(),
    'put_cost': 100 * ((x.calp_deb_100 - x.calp_deb_0) / x.calp_deb_0).median()})).round(1).to_string())
