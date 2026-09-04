"""MaxFader STUDY 3 (2026-09-04): SpikeFader's gates on MaxFader's entries (user).
Same trips, three exits: SpikeFader's own 9m cover (recorded aux_lo_540), MOC, the 2h rule.
Then gate-by-gate marginal value on top of k600 (tail block + same-n), and the voices.
⚠ side-flip law: a transplanted spec is a HYPOTHESIS. dslo is identically 0 here.
Run: python3 -u scripts/analysis/maxfader_study3.py <dir> > data/maxfader_study3_<tag>.log
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd
import maxfader as M
pd.set_option('display.width', 250)
DIRP = sys.argv[1] if len(sys.argv) > 1 else M.DIR
df = M.load(cols=['highs_since_first_high_180','signal_sec','halts_today','secs_since_halt'], dirpath=DIRP)
df['k180'] = df['highs_since_first_high_180']
df['slope_5m'] = df['ols_slope_300']; df['slope_20m'] = df['ols_slope_1200']
df['rr'] = df['brv15_tape']   # identical column (vol_60 / (vol_0945_tape/15))
# SpikeFader's own exit on these entries (S1's ret_540): the 9m-low cover, MOC if unresolved
px = df['aux_lo_540_px'].where(~df['aux_lo_540_px'].isna(), df['exit_px'])
df['ret_540'] = -(px / df['entry_px'] - 1.0)
print(f"corpus {len(df):,} trips  {df.trade_date.min()}..{df.trade_date.max()}")

# ---- the SpikeFader SPEC (units-fixed slope_20m = 5e-5) ----
G = {
 'volat>=40bp': df['volat_20m'] >= 0.004,
 'be6030>2%':   df['be6030'] > 0.02,
 'eff10>=0.3':  df['eff_10m'] >= 0.3,
 'k300>=40':    df['k300'] >= 40,
 'k600>=90':    df['k600'] >= 90,
 'k180>=15':    df['k180'] >= 15,
 'gap60<10':    df['gap_adj_60'] < 10,
 'dlv>3%':      df['dlv'] > 0.03,
 'slope5m>=0':  df['slope_5m'] >= 0,
 'slope20m>=30bp/min': df['slope_20m'] >= 5.0e-5,
 'ac1>=-0.1':   df['ac1_ewma'] >= -0.1,
}
spec = np.logical_and.reduce([g.fillna(False).values for g in G.values()])
df['spec'] = spec

def exits_table(g, label):
    rows=[dict(exit="SpikeFader's 9m cover (aux_lo_540)", **M.stats(g['ret_540'])),
          dict(exit='MOC (hold to close)', **M.stats(g['ret'])),
          dict(exit=f'RULE 2h ({g["switched_2h"].mean()*100:.0f}% switched)', **M.stats(g['rule_2h'])),
          dict(exit='always switch at 2h', **M.stats(g['always_2h']))]
    print(f'\n--- {label}: n={len(g):,} ---'); print(pd.DataFrame(rows).to_string(index=False))

print('\n\n########## 1. THE FULL SPIKEFADER SPEC on MaxFader entries — three exits ##########')
s = df[df['spec']]
exits_table(s, 'SPEC, mc=0')
b = M.mc1(s); exits_table(b, 'SPEC, mc=1 (slice)')
M.year_table(b, name='SPEC mc=1, MOC')
bb = b.copy(); bb['ret'] = bb['rule_2h']; M.year_table(bb, name='SPEC mc=1, RULE 2h')
bb = b.copy(); bb['ret'] = bb['ret_540']; M.year_table(bb, name='SPEC mc=1, 9m cover')

print('\n\n########## 2. GATE-BY-GATE on top of k600>=63 (mc=0): does each SpikeFader gate ADD? ##########')
base = df['k600'] >= 63
rows=[dict(gate='k600>=63 alone', **M.stats(df[base]['ret']))]
rows.append(dict(gate='  + rule 2h', **M.stats(df[base]['rule_2h'])))
for k, g in G.items():
    if k.startswith('k600'): continue
    m = base & g.fillna(False)
    rows.append(dict(gate=f'+ {k}', **M.stats(df[m]['ret'])))
    rows.append(dict(gate=f'  + {k} + rule 2h', **M.stats(df[m]['rule_2h'])))
print(pd.DataFrame(rows).to_string(index=False))
print('\n--- REMOVE-ONE from the full spec (mc=0): which gate is load-bearing? ---')
rows=[dict(gate='FULL SPEC', **M.stats(df[spec]['ret']))]
for k in G:
    m = np.logical_and.reduce([g.fillna(False).values for kk, g in G.items() if kk != k])
    rows.append(dict(gate=f'drop {k}', **M.stats(df[m]['ret'])))
print(pd.DataFrame(rows).to_string(index=False))

print('\n\n########## 3. SAME-N: the spec vs a tightened k600, and vs random ##########')
M.same_n(df, df['spec'], [('k600', True), ('brv15_tape', True), ('dlv', True)], 'FULL SPIKEFADER SPEC')

print('\n\n########## 4. THE VOICES inside the k600>=63 book (dslo is 0 by construction) ##########')
bk = df[base]
for lab, m in [('rr<0.5 (SpikeFader voice: QUIET)', bk['rr']<0.5), ('rr>=4 (loud)', bk['rr']>=4), ('rr>=40 (climax)', bk['rr']>=40),
               ('volat>=100bp', bk['volat_20m']>=0.01), ('halts_today>=1 & secs_since_halt<=300', (bk['halts_today']>=1)&(bk['secs_since_halt']<=300))]:
    rows=[dict(cut='k600>=63 all', **M.stats(bk['ret'])), dict(cut=f'  ∧ {lab}', **M.stats(bk[m]['ret'])),
          dict(cut=f'  ∧ {lab} + rule 2h', **M.stats(bk[m]['rule_2h']))]
    print(f'\n--- {lab}: n={int(m.sum()):,} ---'); print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
