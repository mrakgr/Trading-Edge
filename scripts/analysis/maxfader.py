"""MaxFader analysis harness (2026-09-03) — the MaxFlyerV2 -> 1s port.

Corpus: data/maxfader_wl (the --base-run whitelist rerun: SESSION-high entry,
HOLD-TO-CLOSE exit, SpikeFader's speed/eff stack OFF, volat >= 40bp, dv60/tc60
floors, barnum >= 22, entries 09:45-15:00). RECORD-FIRST: nothing here gates in
the engine; every lever below is a recorded column.

MaxFlyerV2's levers, re-derived CAUSALLY on the tape:
  brv15_tape   = vol_60 / (vol_0945_tape / 15)     the opening-15m baseline (same-day,
                                                    split-immune; MaxFlyerV2's bar_rvol_15m)
  brv20d_prior = vol_60 / (avgvol20_prior / 390)   the 20d baseline, `20 PRECEDING AND 1
                                                    PRECEDING`, day-D raw shares. ⚠ straddles
                                                    a reverse split -> near-zero denominator
                                                    (S39d hazard): SECONDARY, split-guarded.
  volhigh60    = vol_60 > vol_60_prior_max         MaxFlyerV2's STRICT "new session volume
                                                    high" gate (the F10/F12 mirror)
  chg_1d       = entry_px / close_m1 - 1           extension into the fade (both day-D raw)
⚠ RETURN CONVENTION: ret_exit = -(exit_px/entry_px - 1) — the SHORT sign is ALREADY
applied. The exit is MOC; the recorded per-minute lo marks give any channel cover
post-hoc (`aux_lo_540_px` = SpikeFader's 9m cover on MaxFader's entry).
⚠ The squeeze tail is THE design problem (MaxFlyerV2: worst -839%, 3% < -20%).
Report worst / p1 / p5 / %<-20% on EVERY table, not just PF.
"""
import numpy as np, pandas as pd, duckdb, sys

DIR = 'data/maxfader_wl'

BASE = ['symbol','trade_date','signal_sec','signal_vwap','entry_sec','entry_px','exit_sec','exit_px',
        'ret_exit','exit_reason','volat_20m','volat_10m','eff_10m','eff_20m','bars_present',
        'vol_60','vol_60_prior_max','vol_0945_tape','dv_0945_tape','avgvol20_prior','rvol_0945_honest',
        'dollar_vol_60','tc_60','close_m1','div_m1','close_m3','div_m3','close_d','n',
        'sess_high','sess_low','chan_hi','chan_hi_prev','gap_60','gap_adj_60','halts_today',
        'highs_since_first_high','highs_since_first_high_300','highs_since_first_high_600',
        'highs_since_first_high_180','ols_slope_300','ols_slope_1200','ac1_ewma',
        'vwap_ewp_6030_be','first_high_vwap','aux_lo_540_px','aux_lo_540_sec',
        'fwd_vwap_60','fwd_vwap_300','fwd_vwap_600','fwd_vwap_1200']

DERIVED = """
  , vol_60 / NULLIF(vol_0945_tape / 15.0, 0)                 AS brv15_tape
  , vol_60 / NULLIF(avgvol20_prior / 390.0, 0)               AS brv20d_prior
  , (vol_60 > vol_60_prior_max)                               AS volhigh60
  , vol_60 / NULLIF(vol_60_prior_max, 0)                      AS vol_vs_high
  , entry_px / NULLIF(close_m1, 0) - 1                        AS chg_1d
  , signal_vwap / NULLIF(sess_low, 0) - 1                     AS dlv
  , signal_vwap / NULLIF(vwap_ewp_6030_be, 0) - 1             AS be6030
  , highs_since_first_high_600                                AS k600
  , highs_since_first_high_300                                AS k300
"""

def pf(s):
    w = s[s > 0].sum(); l = -s[s < 0].sum()
    return w / l if l > 0 else np.inf
def pfm1(s): return pf(s) - 1.0

def load(where=None, cols=None, dirpath=DIR):
    cols = BASE + [c for c in (cols or []) if c not in BASE]
    q = f"SELECT {', '.join(cols)} {DERIVED} FROM read_parquet('{dirpath}/*.parquet')" + (f" WHERE {where}" if where else "")
    df = duckdb.query(q).df()
    df['ret'] = df['ret_exit']                      # MOC exit; SHORT sign already applied
    df['year'] = pd.to_datetime(df['trade_date']).dt.year
    df['entry_min'] = df['entry_sec'] // 60
    return df

def mc1(d):
    """Greedy one-position-per-ticker-day in signal order (the SLICE view)."""
    d = d.sort_values(['symbol','trade_date','signal_sec'])
    ss = d['signal_sec'].to_numpy(); es = d['exit_sec'].to_numpy()
    keys = (d['symbol'].astype(str) + '|' + d['trade_date'].astype(str)).to_numpy()
    keep = np.zeros(len(d), dtype=bool); busy = -1.0; prev = None
    for i in range(len(d)):
        if keys[i] != prev: busy = -1.0; prev = keys[i]
        if ss[i] >= busy: keep[i] = True; busy = es[i]
    return d[keep]

def tail(s):
    """The squeeze-tail block: worst, p1, p5, share below -20%."""
    return {'worst%': round(s.min()*100, 1), 'p1%': round(s.quantile(.01)*100, 1),
            'p5%': round(s.quantile(.05)*100, 1), '<-20%': round((s < -0.20).mean()*100, 2)}

def stats(s, label=''):
    r = {'n': len(s), 'PF-1': round(pfm1(s), 3), 'net%': round(s.sum()*100, 0),
         'win%': round((s > 0).mean()*100, 1), 'avg%': round(s.mean()*100, 2)}
    r.update(tail(s)); return r

def band(df, col, qs=(0,.1,.25,.5,.75,.9,1.0), label=None, edges=None):
    """Quantile (or explicit-edge) bands with the tail block; YEAR columns via year_table."""
    d = df[~df[col].isna()].copy()
    if d.empty: print(f'  {col}: all-NaN'); return
    e = np.array(edges, float) if edges is not None else d[col].quantile(list(qs)).values
    e[0] -= 1e-12; e[-1] += 1e-12
    d['b'] = pd.cut(d[col], np.unique(e), duplicates='drop')
    rows = [dict(band=str(b), **stats(g['ret'])) for b, g in d.groupby('b', observed=True)]
    print(f'\n--- {label or col} ---'); print(pd.DataFrame(rows).to_string(index=False))

def ladder(df, col, floors, label=None, extra_mask=None):
    """Monotone floor ladder (MaxFlyerV2's rvol tiers) with the tail block."""
    rows = []
    for f in floors:
        m = df[col] >= f
        if extra_mask is not None: m &= extra_mask
        g = df[m]
        if len(g): rows.append(dict(floor=f, **stats(g['ret'])))
    print(f'\n--- LADDER {label or col} ---'); print(pd.DataFrame(rows).to_string(index=False))

def year_table(df, mask=None, name='all'):
    rows = []
    for y, g in df.groupby('year'):
        gm = g if mask is None else g[mask.loc[g.index]]
        rows.append(dict(year=y, **stats(gm['ret'])) if len(gm) else dict(year=y, n=0))
    print(f'\n--- YEAR TABLE: {name} ---'); print(pd.DataFrame(rows).to_string(index=False))

def same_n(df, cand_mask, incumbents, label):
    """Control: tighten each incumbent to the candidate's n (mc=0 slice view)."""
    n = int(cand_mask.sum()); rows = [dict(gate=label, n=n, **stats(df[cand_mask]['ret']))]
    for inc, hi in incumbents:
        di = df[~df[inc].isna()].sort_values(inc, ascending=not hi).head(n)
        rows.append(dict(gate=f'tighten {inc} ({"top" if hi else "bottom"})', n=len(di), **stats(di['ret'])))
    rng = np.random.default_rng(0)
    rs = [pfm1(df.iloc[rng.choice(len(df), n, replace=False)]['ret']) for _ in range(30)]
    rows.append(dict(gate='RANDOM same-n mean(30)', n=n, **{'PF-1': round(float(np.mean(rs)), 3)}))
    rows.append(dict(gate='  random max', n=n, **{'PF-1': round(float(np.max(rs)), 3)}))
    print(f'\n--- SAME-N CONTROL: {label} ---'); print(pd.DataFrame(rows).to_string(index=False))

if __name__ == '__main__':
    df = load()
    print(f"corpus {len(df):,} trips, {df.groupby(['symbol','trade_date']).ngroups:,} tkd, {df.trade_date.min()}..{df.trade_date.max()}")
    print('\n=== BASE (mc=0 sampler, ATTRIBUTION) ==='); print(pd.DataFrame([stats(df['ret'])]).to_string(index=False))
    b = mc1(df); print('\n=== BASE mc=1 ==='); print(pd.DataFrame([stats(b['ret'])]).to_string(index=False))
    year_table(df, name='mc=0 base')
