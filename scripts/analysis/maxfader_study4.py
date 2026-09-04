"""MaxFader STUDY 4 (2026-09-04): the rr>=40 CLIMAX system (user) — a separate book on the
MaxFader engine: session-high SHORT gated on brv15_tape (== rr) >= 40, held with the 2h
AVWAP rule. Bare and under the SpikeFader core (k600>=90 & slope_20m & be6030); exits
MOC / rule 2h / 9m cover; mc=0 and mc=1; year table; tail block; the rr floor ladder.
Run: python3 -u scripts/analysis/maxfader_study4.py <dir> > data/maxfader_study4_<tag>.log
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd
import maxfader as M
pd.set_option('display.width', 250)
DIRP = sys.argv[1] if len(sys.argv) > 1 else M.DIR
df = M.load(dirpath=DIRP)
px = df['aux_lo_540_px'].where(~df['aux_lo_540_px'].isna(), df['exit_px']); df['ret_540'] = -(px/df['entry_px']-1)
core = ((df['k600']>=90)&(df['ols_slope_1200']>=5e-5)&(df['be6030']>0.02)).fillna(False)
print(f"corpus {len(df):,}  {df.trade_date.min()}..{df.trade_date.max()}")
def exits(g, label):
    rows=[dict(exit='MOC', **M.stats(g['ret'])), dict(exit=f'RULE 2h ({g["switched_2h"].mean()*100:.0f}% sw)', **M.stats(g['rule_2h'])),
          dict(exit='always 2h', **M.stats(g['always_2h'])), dict(exit='9m cover', **M.stats(g['ret_540']))]
    print(f'\n--- {label}: n={len(g):,} ---'); print(pd.DataFrame(rows).to_string(index=False))
for thr in (20, 40, 60, 100):
    for lab, m in [('BARE', df['brv15_tape']>=thr), ('SPIKEFADER CORE', (df['brv15_tape']>=thr)&core)]:
        g = df[m]
        if len(g) < 20: print(f'\n{lab} rr>={thr}: n={len(g)} — too few'); continue
        print(f'\n\n########## rr>={thr}, {lab} ##########')
        exits(g, 'mc=0'); b = M.mc1(g); exits(b, 'mc=1')
        M.year_table(b, name=f'rr>={thr} {lab} mc=1, MOC')
        bb = b.copy(); bb['ret'] = bb['rule_2h']; M.year_table(bb, name=f'rr>={thr} {lab} mc=1, RULE 2h')
        print(f"  tkd/yr: {b.groupby('year').size().to_dict()}   entry minute p50 {b['entry_min'].median():.0f}   chg_1d p50 {b['chg_1d'].median()*100:.0f}%   halted-day share {(b['halts_today']>=1).mean()*100:.0f}%")
print('\nDONE')
