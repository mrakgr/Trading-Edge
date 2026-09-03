"""SpikeFader S44 head-to-head: SMA breakout counters vs the raw ladder,
and the NEW axis -- reset/leg MAGNITUDE and RATE (2026-09-03).

The question, in order:
  1. Do the SMA-based breakout counters beat the raw ladder the spec already
     uses (k300/k600/k180)? The synthetic bake-off says NO -- d' 2.267 (sma30)
     vs 2.245 (raw) is a dead heat, and smoothing only moves the operating
     point (null breakout fraction 0.078 -> 0.26). This measures whether the
     real book agrees.
  2. Does MAGNITUDE since reset add anything? Prior: mostly NO -- rho 0.70-0.86
     with dlv / k600, i.e. a re-derivation of distance features already gated.
  3. Does RATE (distance / elapsed) add anything? This is the ONE genuinely
     orthogonal axis: rho +0.06 to dlv, -0.215 to k600, -0.146 to its own
     magnitude. Big legs are SLOW legs.

⚠ Method (feedback_three_controls_before_a_feature + high_correlation_proves_nothing):
  a decile ramp is NOT a finding. Every candidate must clear
    (i)   the SAME-N TIGHTENED CONTROL -- tighten an incumbent voice to the same
          n and see if it does as well (kills redundancy dressed as signal);
    (ii)  the YEAR TABLE -- a voice that lives in one year is an anecdote;
    (iii) the TICKER-DAY PERMUTATION NULL -- trips clump within days, so a
          trip-level null is anticonservative.
  rho is reported for context only: it lives in the BULK, a gate lives in the TAIL.
"""
import numpy as np, pandas as pd, duckdb, sys
sys.path.insert(0, 'scripts/analysis')
from spikefader_zq import pf, pfm1, mc1, SPEC, DERIVED, band, year_table, tkd_resample

DIR = 'data/spikefader_s44'
EXIT_CH = 540

# ⚠ the S44 columns. `raw_brlo_*_bars` duplicates breach_lo_* by construction --
# that equality is this block's substitution test, re-run here on the full corpus.
S44_COLS = [
    'sma_px','sma_dist',
    'sma_brlo_300_bars','sma_brlo_600_bars','sma_brlo_1200_bars',
    'sma_brlo_300_mag','sma_brlo_600_mag','sma_brlo_1200_mag',
    'sma_brlo_300_rate','sma_brlo_600_rate','sma_brlo_1200_rate',
    'sma_highs_300','sma_highs_600','sma_highs_1200','sma_bars_1200',
    'sma_leg_mag_300','sma_leg_mag_600','sma_leg_mag_1200',
    'sma_leg_rate_300','sma_leg_rate_600','sma_leg_rate_1200',
    'raw_brlo_300_bars','raw_brlo_600_bars','raw_brlo_1200_bars',
    'raw_brlo_300_mag','raw_brlo_600_mag','raw_brlo_1200_mag',
    'raw_brlo_300_rate','raw_brlo_600_rate','raw_brlo_1200_rate',
    'raw_leg_mag','raw_leg_mag_300','raw_leg_mag_600',
    'raw_leg_rate','raw_leg_rate_300','raw_leg_rate_600',
    'breach_lo_300','breach_lo_600','breach_lo_1200',
    'highs_since_first_high_300','highs_since_first_high_600',
    'highs_since_first_high_180','ols_slope_300','ols_slope_1200',
    'eff_10m','ac1_ewma','gap_adj_60','vwap_ewp_6030_be','sess_high',
    'first_high_vwap','d_lo_flow','vol_60','vol_0945_tape','signal_vwap',
]
BASE = ['symbol','trade_date','signal_sec','entry_px','exit_px','exit_sec',
        'ret_exit','volat_20m','halts_today','secs_since_halt','dlv',
        f'aux_lo_{EXIT_CH}_px', f'aux_lo_{EXIT_CH}_sec']

def load(spec=True, dirpath=DIR):
    cols = BASE + [c for c in S44_COLS if c not in BASE]
    where = 'WHERE ' + (SPEC if spec is True else spec) if spec else ''
    q = f"SELECT {', '.join(cols)} {DERIVED} FROM read_parquet('{dirpath}/*.parquet') {where}"
    df = duckdb.query(q).df()
    px  = df[f'aux_lo_{EXIT_CH}_px'].where(lambda s: ~s.isna(), df['exit_px'])
    sec = df[f'aux_lo_{EXIT_CH}_sec'].where(lambda s: ~s.isna(), df['exit_sec'])
    df['ret'] = -(px / df['entry_px'] - 1.0)     # SHORT sign already applied
    df['exit_sec'] = sec
    df['year'] = pd.to_datetime(df['trade_date']).dt.year
    return df

def substitution_test(dirpath=DIR):
    print('=== 0. SUBSTITUTION TEST: raw_brlo_*_bars == breach_lo_* ===')
    ok = True
    for w in (300, 600, 1200):
        r = duckdb.query(f"""SELECT count(*) n, sum(CASE WHEN raw_brlo_{w}_bars
            IS NOT DISTINCT FROM breach_lo_{w} THEN 1 ELSE 0 END) eq
            FROM read_parquet('{dirpath}/*.parquet')""").fetchone()
        good = r[0] == r[1]
        ok &= good
        print(f'  breach_lo_{w:<5} n={r[0]:9,}  identical={r[1]:9,}  '
              f'{"OK" if good else "*** MISMATCH -- the block is miswired ***"}')
    return ok

def spearman(df, a, b):
    d = df[[a, b]].dropna()
    if len(d) < 100: return np.nan
    return d[a].rank().corr(d[b].rank())

def redundancy(df, cands, incumbents):
    print('\n=== 1. REDUNDANCY (rho lives in the BULK -- context, not a verdict) ===')
    print(f"  {'candidate':22s} " + ' '.join(f'{i:>16s}' for i in incumbents))
    for c in cands:
        if c not in df.columns: continue
        print(f'  {c:22s} ' + ' '.join(f'{spearman(df,c,i):>+16.3f}' for i in incumbents))

def same_n_control(df, cand, incumbents, hi=True, qs=(0.5, 0.75, 0.9)):
    """Control (i): at the SAME n, does the candidate beat a tightened incumbent?"""
    print(f'\n=== SAME-N CONTROL: {cand} ({"top" if hi else "bottom"} slice) ===')
    d = df[~df[cand].isna()]
    rows = []
    for q in qs:
        thr = d[cand].quantile(1 - q if hi else q)
        m = d[cand] >= thr if hi else d[cand] <= thr
        n = int(m.sum())
        row = {'q': q, 'n': n, cand: round(pfm1(d[m]['ret']), 3)}
        for inc in incumbents:
            di = d[~d[inc].isna()].sort_values(inc, ascending=False)
            row[inc] = round(pfm1(di.head(n)['ret']), 3) if n <= len(di) else np.nan
        row['ALL'] = round(pfm1(d['ret']), 3)
        rows.append(row)
    print(pd.DataFrame(rows).to_string(index=False))
