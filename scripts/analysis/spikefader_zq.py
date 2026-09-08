"""SpikeFader z-quantile analysis harness (2026-09-02).

SPEC as of 2026-09-01 (docs/spikefader_results.md §S38e):
  volat_20m>=40bp & be6030>2% & eff_10m>=0.3 & k300>=40 & k600>=90 & k180>=15
  & gap_adj_60<10 & dlv>3% & slope_5m>=0 & slope_20m>=30bp/min & signal<15:30
  & ac1_ewma>=-0.1,  exit = 540-bar (9m) channel low.
  Book: 3,717 @ PF-1 1.181, net 7,399%, win 74.4%.
"""
import numpy as np, pandas as pd, duckdb

DIR = 'data/spikefader_zq'
EXIT_CH = 540

def pf(s):
    w = s[s > 0].sum(); l = -s[s < 0].sum()
    return w / l if l > 0 else np.nan

def pfm1(s):
    return pf(s) - 1.0

def mc1(df):
    """SLICE view: greedy one-position-per-ticker-day, in signal order."""
    df = df.sort_values(['symbol', 'trade_date', 'signal_sec'])
    keep, busy = [], {}
    for r in df.itertuples():
        k = (r.symbol, r.trade_date)
        if r.signal_sec >= busy.get(k, -1):
            keep.append(r.Index); busy[k] = r.exit_sec
    return df.loc[keep].copy()

SPEC = """
  volat_20m >= 0.0040 AND signal_vwap / vwap_ewp_6030_be - 1 > 0.02
  AND eff_10m >= 0.3
  AND highs_since_first_high_300 >= 40
  AND highs_since_first_high_600 >= 90
  AND highs_since_first_high_180 >= 15
  AND gap_adj_60 < 10
  AND dlv > 0.03
  AND ols_slope_300 >= 0 AND ols_slope_1200 >= 0.0030
  AND signal_sec < 55800
  AND ac1_ewma >= -0.1
  AND signal_vwap >= 1.0
"""
# ⭐ §S49 (user, 2026-09-08): the $1 floor — signal vwap (RAW, causal `n` schema) >= $1 at the signal bar.
# Sub-$1 stock: no rebates and 30 bp take fees at Lightspeed, no free limit orders at TradeZero. The floor
# IMPROVES the book (3,573 @ 2.213 → 3,067 @ 2.325; sub-$1 slice was 544 @ 1.93). The prior-close variant
# (`--min-prev-close 1`, 2,879 @ 2.185) is WORSE: it also drops the sub-$1 names that spike THROUGH $1 on
# day D, which are among the best shorts. Gate on the signal price, not the prior close.

# derived (post-hoc) features — the doc's shorthand, spelled out
DERIVED = """
  , signal_vwap / vwap_ewp_6030_be - 1            AS be6030
  , highs_since_first_high_300                    AS k300
  , highs_since_first_high_600                    AS k600
  , highs_since_first_high_180                    AS k180
  , ols_slope_300                                 AS slope_5m
  , ols_slope_1200                                AS slope_20m
  , vol_60 / NULLIF(vol_0945_tape * 60.0/900.0,0) AS rr
  , signal_vwap / NULLIF(sess_high,0) - 1         AS dslo
  , signal_vwap / NULLIF(first_high_vwap,0) * (1 + d_lo_flow) - 1 AS d20a
"""

BASE = ['symbol','trade_date','signal_sec','entry_px','exit_px','exit_sec',
        'ret_exit','volat_20m','halts_today','secs_since_halt','dlv',
        f'aux_lo_{EXIT_CH}_px', f'aux_lo_{EXIT_CH}_sec']

def load(extra_cols=None, spec=True, ch=EXIT_CH):
    cols = list(BASE)
    if ch != EXIT_CH:
        cols += [f'aux_lo_{ch}_px', f'aux_lo_{ch}_sec']
    if extra_cols: cols += [c for c in extra_cols if c not in cols]
    where = 'WHERE ' + (SPEC if spec is True else spec) if spec else ''
    q = f"SELECT {', '.join(cols)} {DERIVED} FROM read_parquet('{DIR}/*.parquet') {where}"
    df = duckdb.query(q).df()
    # ⚠ RETURN CONVENTION: ret = -(exit/entry - 1). The SHORT sign is ALREADY
    # applied. Never use exit/entry-1, and never entry/exit-1 (inflates by r^2/(1+r)).
    px  = df[f'aux_lo_{ch}_px']
    sec = df[f'aux_lo_{ch}_sec']
    # unresolved mark -> MOC at the recorded exit
    px  = px.where(~px.isna(), df['exit_px'])
    sec = sec.where(~sec.isna(), df['exit_sec'])
    df['ret'] = -(px / df['entry_px'] - 1.0)
    df['exit_sec'] = sec
    df['year'] = pd.to_datetime(df['trade_date']).dt.year
    return df

def band(df, col, qs=(0, .1, .25, .5, .75, .9, 1.0), label=None):
    """Decile/quantile band table with YEAR columns (journal_tables_not_prose)."""
    d = df[~df[col].isna()].copy()
    if d.empty:
        print(f'  {col}: all-NaN'); return
    edges = d[col].quantile(list(qs)).values
    edges[0] -= 1e-12; edges[-1] += 1e-12
    d['b'] = pd.cut(d[col], np.unique(edges), duplicates='drop')
    rows = []
    for b, g in d.groupby('b', observed=True):
        rows.append({'band': str(b), 'n': len(g), 'PF-1': round(pfm1(g['ret']), 3),
                     'net%': round(g['ret'].sum() * 100, 0),
                     'win%': round((g['ret'] > 0).mean() * 100, 1),
                     'worst%': round(g['ret'].min() * 100, 1)})
    print(f'\n--- {label or col} ---')
    print(pd.DataFrame(rows).to_string(index=False))

def year_table(df, mask, name):
    """Control 1: leave-one-year-out / per-year breakdown."""
    rows = []
    for y, g in df.groupby('year'):
        gm = g[mask.loc[g.index]]
        rows.append({'year': y, 'n_all': len(g), 'PF-1_all': round(pfm1(g['ret']), 3),
                     'n': len(gm), 'PF-1': round(pfm1(gm['ret']), 3) if len(gm) else np.nan})
    print(f'\n--- YEAR TABLE: {name} ---')
    print(pd.DataFrame(rows).to_string(index=False))

def tkd_resample(df, mask, reps=2000, seed=0):
    """Control 2: permutation null sampling whole TICKER-DAYS (trips clump)."""
    rng = np.random.default_rng(seed)
    obs = pfm1(df[mask]['ret'])
    k = int(mask.sum())
    tkd = df.groupby(['symbol', 'trade_date']).indices
    keys = list(tkd)
    hits = 0
    for _ in range(reps):
        rng.shuffle(keys)
        idx, tot = [], 0
        for key in keys:
            idx.extend(tkd[key]); tot += len(tkd[key])
            if tot >= k: break
        if pfm1(df.iloc[idx[:k]]['ret']) >= obs: hits += 1
    return obs, (hits + 1) / (reps + 1)
