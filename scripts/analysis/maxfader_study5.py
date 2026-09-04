"""MaxFader STUDY 5 (2026-09-04): THE ARMED STOPS on the rr>=8 corpus (user).
Exit at the k-th stop hit (k=1..5; MOC if fewer hits) per arming channel {5m,10m,20m},
vs MOC, the 2h AVWAP rule and the 9m cover — mc=0 and mc=1, by year, tail block,
pull-up count distribution, and the rr>=40 climax slice.
Run: python3 -u scripts/analysis/maxfader_study5.py <dir> > data/maxfader_study5_<tag>.log
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd
import maxfader as M
pd.set_option('display.width', 260)
DIRP = sys.argv[1] if len(sys.argv) > 1 else 'data/maxfader_rr8'
C = (300, 600, 1200)
extra = [f'as_{c}_{x}' for c in C for x in ['hits','first_arm_sec'] + [f'm{k}_{y}' for k in range(1,6) for y in ('px','sec')]]
LONG = (1800, 3600, 7200, 10800)
extra += [f'aux_lo_{n}_{y}' for n in LONG for y in ('px','sec','moc')]
df = M.load(cols=extra, dirpath=DIRP)
px = df['aux_lo_540_px'].where(~df['aux_lo_540_px'].isna(), df['exit_px']); df['ret_540'] = -(px/df['entry_px']-1)
for n in LONG:   # ⭐ the LONG exits (user): cover at the first new {30m,1h,2h,3h}-bar low after entry, MOC if none
    pxn = df[f'aux_lo_{n}_px'].where(~df[f'aux_lo_{n}_px'].isna(), df['exit_px']); df[f'ret_L{n}'] = -(pxn/df['entry_px']-1)
for c in C:
    for k in range(1,6):
        mp = df[f'as_{c}_m{k}_px']
        df[f'stop{k}_{c}'] = np.where(mp.isna(), df['ret'], -(mp/df['entry_px']-1))   # exit at the k-th hit, else MOC
        # the rule + stop: the 2h rule, but the k-th stop hit overrides if it comes first
        rs = df['rule_2h'].to_numpy()
        sw = df['switched_2h'].fillna(False).astype(bool).to_numpy()
        rule_sec = df['post_chk_lo300_sec_2h'].fillna(10**9).to_numpy()   # unresolved rule exit = never
        stop_sec = df[f'as_{c}_m{k}_sec'].fillna(10**9).to_numpy()
        stop_ret = -(mp.fillna(df['entry_px'])/df['entry_px']-1).to_numpy()
        # rule + stop: whichever fires FIRST; neither -> MOC (rule_2h already falls back to MOC)
        df[f'rule2h_stop{k}_{c}'] = np.where(mp.isna().to_numpy(), rs, np.where(sw & (rule_sec < stop_sec), rs, stop_ret))
print(f"corpus {len(df):,} trips  {df.groupby(['symbol','trade_date']).ngroups:,} tkd  {df.trade_date.min()}..{df.trade_date.max()}   rr>=8 trips {(df['brv15_tape']>=8).sum():,}")

def table(g, label):
    rows=[dict(exit='MOC', **M.stats(g['ret'])), dict(exit='RULE 2h', **M.stats(g['rule_2h'])), dict(exit='9m cover', **M.stats(g['ret_540']))]
    for n in LONG:
        moc_share = g[f'aux_lo_{n}_moc'].fillna(False).astype(bool).mean()*100 if f'aux_lo_{n}_moc' in g else float('nan')
        rows.append(dict(exit=f'{n//60}m-low cover ({moc_share:.0f}% moc-resolved)', **M.stats(g[f'ret_L{n}'])))
    for c in C:
        for k in range(1,4):
            rows.append(dict(exit=f'STOP {k} @ {c//60}m-low arm', **M.stats(g[f'stop{k}_{c}'])))
        rows.append(dict(exit=f'  RULE 2h + STOP 1 @ {c//60}m', **M.stats(g[f'rule2h_stop1_{c}'])))
    print(f'\n--- {label}: n={len(g):,} ---'); print(pd.DataFrame(rows).to_string(index=False))

for lab, m in [('ALL (whitelist days)', df['ret']==df['ret']), ('rr>=8 signals', df['brv15_tape']>=8), ('rr>=12', df['brv15_tape']>=12), ('rr>=40 climax', df['brv15_tape']>=40)]:
    g = df[m]
    if len(g) < 50: continue
    print(f'\n\n########## {lab} ##########')
    table(g, 'mc=0'); b = M.mc1(g); table(b, 'mc=1')
    print('\n  pull-up count distribution (mc=1), hits per channel:')
    for c in C: print(f'    {c//60:>3}m: ' + str(b[f'as_{c}_hits'].value_counts().sort_index().to_dict()))
    print('\n  by year, mc=1: MOC / rule 2h / stop1@5m / stop2@5m / stop1@10m  (PF-1)  and tail<-20% MOC -> stop1@5m')
    rows=[]
    for y, gy in b.groupby('year'):
        rows.append(dict(year=y, n=len(gy), MOC=round(M.pfm1(gy['ret']),2), rule2h=round(M.pfm1(gy['rule_2h']),2),
                         s1_5m=round(M.pfm1(gy['stop1_300']),2), s2_5m=round(M.pfm1(gy['stop2_300']),2), s1_10m=round(M.pfm1(gy['stop1_600']),2),
                         tail_MOC=round((gy['ret']<-.2).mean()*100,1), tail_s1_5m=round((gy['stop1_300']<-.2).mean()*100,1),
                         worst_MOC=round(gy['ret'].min()*100), worst_s1_5m=round(gy['stop1_300'].min()*100)))
    print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
