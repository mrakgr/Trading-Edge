"""MaxFader STUDY 2 (2026-09-04): k600 as the master voice, THE AVWAP RULE inside a
k600-gated mc=1 book (the real test — the bare book is weak), the HALT gate with the
disproportion test, entry-time inside the book, and the brv15>=40 rung by year.
Run: python3 -u scripts/analysis/maxfader_study2.py > data/maxfader_study2.log
⚠ mc views (feedback_three_mc_questions): SLICE = build the mc=1 book on MOC exits,
then apply the rule per trip (voice/sizing view). REPLAY-INSIDE = the rule's earlier
exit FREES the slot for a later entry on the same ticker-day (gate view). Both shown.
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd
import maxfader as M
pd.set_option('display.width', 250)
DIRP = sys.argv[1] if len(sys.argv) > 1 else M.DIR   # explicit dir: the glob would ingest a run in progress
df = M.load(cols=['secs_since_halt','halts_today'], dirpath=DIRP)
print(f"corpus {len(df):,} trips  {df.groupby(['symbol','trade_date']).ngroups:,} tkd  {df.trade_date.min()}..{df.trade_date.max()}")

def rule_exit_sec(g, h):
    """exit second under the rule: the post-check mark's second when switched, else exit_sec."""
    sw = g[f'switched_{h}']
    return np.where(sw, g[f'post_chk_lo300_sec_{h}'].fillna(g['exit_sec']), g['exit_sec'])

def rule_rows(g, label, replay=False):
    # the MOC baseline is always the mc=1 SLICE book (replay rows must be read against it)
    base = M.mc1(g) if replay else g
    rows=[dict(exit=f'MOC (hold to close), mc=1 slice n={len(base):,}', **M.stats(base['ret']))]
    for h in ('1h','2h','3h'):
        if replay:
            gg = g.copy(); gg['exit_sec'] = rule_exit_sec(gg, h); gg = M.mc1(gg)
            rows.append(dict(exit=f'RULE {h} REPLAY-INSIDE (n={len(gg):,})', **M.stats(gg[f'rule_{h}'])))
        else:
            rows.append(dict(exit=f'RULE {h} ({g[f"switched_{h}"].mean()*100:.0f}% switched)', **M.stats(g[f'rule_{h}'])))
            rows.append(dict(exit=f'  always switch at {h}', **M.stats(g[f'always_{h}'])))
    print(f'\n--- {label}: n={len(g):,} ---'); print(pd.DataFrame(rows).to_string(index=False))

print('\n\n########## 1. k600 — the master voice: ladder, year table, mc=1 ##########')
M.ladder(df, 'k600', [0,16,35,63,96,140,200], label='k600 = highs_since_first_high_600 (mc=0)')
for f in (35,63,96):
    m = df['k600']>=f
    M.year_table(df, m, name=f'k600>={f} mc=0')
    b = M.mc1(df[m]); M.year_table(b, name=f'k600>={f} mc=1 (n={len(b):,})')

print('\n\n########## 2. ⭐⭐ THE AVWAP RULE INSIDE THE k600 BOOK (mc=1) ##########')
for f in (35,63,96):
    b = M.mc1(df[df['k600']>=f])
    rule_rows(b, f'k600>={f}, mc=1 SLICE (book built on MOC, rule applied per trip)')
    rule_rows(df[df['k600']>=f], f'k600>={f}, mc=1 REPLAY-INSIDE (rule exit frees the slot)', replay=True)
print('\n--- rule 2h vs MOC BY YEAR inside k600>=63 mc=1 SLICE ---')
b = M.mc1(df[df['k600']>=63]); rows=[]
for y,g in b.groupby('year'):
    rows.append(dict(year=y, n=len(g), MOC=round(M.pfm1(g['ret']),3), rule_2h=round(M.pfm1(g['rule_2h']),3),
                     net_MOC=round(g['ret'].sum()*100), net_2h=round(g['rule_2h'].sum()*100),
                     tail_MOC=round((g['ret']<-.2).mean()*100,2), tail_2h=round((g['rule_2h']<-.2).mean()*100,2),
                     worst_MOC=round(g['ret'].min()*100,1), worst_2h=round(g['rule_2h'].min()*100,1)))
print(pd.DataFrame(rows).to_string(index=False))

print('\n\n########## 3. THE HALT GATE — disproportion test first ##########')
h = df['halts_today']>=1
tail = df['ret']<-0.20
print(f"halted-day trips: {h.sum():,} = {h.mean()*100:.2f}% of trips   share of tail trips: {(h&tail).sum()/tail.sum()*100:.1f}%   share of tail NET: {df[h&tail]['ret'].sum()/df[tail]['ret'].sum()*100:.1f}%   share of corpus net: {df[h]['ret'].sum()/df['ret'].sum()*100:.1f}%")
print("  (disproportion: a real tail lever moves the tail far more than its share of the book)")
for lab, m in [('halts_today = 0', ~h), ('halts_today >= 1', h)]:
    print(f'\n--- {lab} ---'); print(pd.DataFrame([M.stats(df[m]['ret'])]).to_string(index=False))
M.band(df[h], 'secs_since_halt', edges=[-1,0,60,300,900,1800,3600,100000], label='secs_since_halt among halted-day trips')
for f in (63,96):
    b = M.mc1(df[df['k600']>=f]); hb = b['halts_today']>=1
    print(f'\n--- k600>={f} mc=1: with vs without halted days (MOC and rule 2h) ---')
    rows=[]
    for lab, m in [('all', hb|~hb), ('no halted days', ~hb), ('halted days only', hb)]:
        g=b[m]; rows.append(dict(cut=lab, **M.stats(g['ret']))); rows.append(dict(cut=f'  {lab} + rule 2h', **M.stats(g['rule_2h'])))
    print(pd.DataFrame(rows).to_string(index=False))

print('\n\n########## 4. ENTRY TIME inside the k600>=63 book (the 1m finding inverted) ##########')
b = M.mc1(df[df['k600']>=63])
M.band(b, 'entry_min', edges=[585,600,630,660,720,780,840,900], label='k600>=63 mc=1 by entry minute (MOC)')
bb = b.copy(); bb['ret'] = bb['rule_2h']
M.band(bb, 'entry_min', edges=[585,600,630,660,720,780,840,900], label='k600>=63 mc=1 by entry minute (RULE 2h)')

print('\n\n########## 5. brv15>=40 — the clean rung, by year ##########')
M.year_table(df, df['brv15_tape']>=40, name='brv15>=40 mc=0')
M.year_table(df, (df['brv15_tape']>=40)&(df['k600']>=35), name='brv15>=40 & k600>=35 mc=0')
print('\nDONE')
