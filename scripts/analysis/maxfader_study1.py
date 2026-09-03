"""MaxFader STUDY 1 (2026-09-03, unattended): the base corpus + MaxFlyerV2's
levers re-derived causally, with the squeeze tail on every table.
Run: python3 -u scripts/analysis/maxfader_study1.py > data/maxfader_study1.log
"""
import sys; sys.path.insert(0, 'scripts/analysis')
import numpy as np, pandas as pd
import maxfader as M
pd.set_option('display.width', 250)

df = M.load()
tkd = df.groupby(['symbol','trade_date']).ngroups
print(f"corpus {len(df):,} trips  {tkd:,} tkd  {df.trade_date.min()}..{df.trade_date.max()}")
print(f"cols: {df.shape[1]}   volhigh60 rate {df['volhigh60'].mean()*100:.1f}%   split-touched (n!=1) {(abs(df['n']-1)>1e-9).mean()*100:.1f}%")

print('\n\n########## 1. BASE — the bare session-high short (mc=0 attribution) ##########')
print(pd.DataFrame([M.stats(df['ret'])]).to_string(index=False))
b = M.mc1(df)
print('\n--- mc=1 (one position per ticker-day, first signal) ---')
print(pd.DataFrame([M.stats(b['ret'])]).to_string(index=False))
M.year_table(df, name='BASE mc=0')
M.year_table(b, name='BASE mc=1')

print('\n\n########## 2. THE VOLUME LADDERS (MaxFlyerV2: rvol is the master gate) ##########')
M.ladder(df, 'brv15_tape', [0,2,4,8,12,20,40,100], label='brv15_tape = vol_60/(vol_0945_tape/15)  [PRIMARY, split-immune]')
M.ladder(df, 'brv20d_prior', [0,4,12,40,100,200,400], label='brv20d_prior = vol_60/(avgvol20_prior/390)  [SECONDARY]')
M.ladder(df, 'brv20d_prior', [0,4,12,40,100,200,400], label='brv20d_prior, SPLIT-GUARDED (n == 1 only)', extra_mask=(abs(df['n']-1)<1e-9))
M.band(df, 'brv15_tape', edges=[0,1,2,4,8,12,20,40,1e9], label='brv15_tape BANDS (not floors)')
M.band(df, 'vol_vs_high', edges=[0,0.25,0.5,0.75,1.0,1.5,2,4,1e9], label='vol_vs_high = vol_60 / vol_60_prior_max (1.0 = volhigh60)')

print('\n\n########## 3. volhigh60 — MaxFlyerV2 STRICT volume-high gate ##########')
for name, m in [('volhigh60 = TRUE', df['volhigh60']==True), ('volhigh60 = FALSE', df['volhigh60']==False)]:
    print(f'\n--- {name} ---'); print(pd.DataFrame([M.stats(df[m]['ret'])]).to_string(index=False))
M.ladder(df, 'brv15_tape', [0,4,8,12,20,40], label='brv15_tape ladder WITHIN volhigh60', extra_mask=(df['volhigh60']==True))
M.ladder(df, 'brv15_tape', [0,4,8,12,20,40], label='brv15_tape ladder WITHOUT volhigh60', extra_mask=(df['volhigh60']==False))

print('\n\n########## 4. YEAR TABLES for the candidate tiers (2026 FIRST — regime check) ##########')
for lab, m in [('brv15>=4', df['brv15_tape']>=4), ('brv15>=8', df['brv15_tape']>=8), ('brv15>=12', df['brv15_tape']>=12),
               ('volhigh60', df['volhigh60']==True), ('brv20d_prior>=100 (n==1)', (df['brv20d_prior']>=100)&(abs(df['n']-1)<1e-9))]:
    M.year_table(df, m, name=lab)

print('\n\n########## 5. EXTENSION (chg_1d) and the other MaxFlyerV2 levers ##########')
M.band(df, 'chg_1d', edges=[-1,0,0.1,0.3,0.5,1.0,1.5,10], label='chg_1d = entry/close_m1 - 1 (extension into the fade)')
M.band(df, 'chg_1d', edges=[-1,0,0.1,0.3,0.5,1.0,1.5,10], label='chg_1d WITHIN brv15>=8')  if False else None
d8 = df[df['brv15_tape']>=8]
if len(d8) > 200: M.band(d8, 'chg_1d', edges=[-1,0,0.1,0.3,0.5,1.0,1.5,10], label='chg_1d WITHIN brv15>=8')
M.band(df, 'entry_min', edges=[585,600,630,660,720,780,840,900], label='ENTRY TIME (ET minute) — MaxFlyerV2: robust all day')
M.band(df, 'volat_20m', label='volat_20m deciles')
M.band(df, 'dlv', label='dlv = dist above session low, deciles')
M.band(df, 'k600', label='k600 (SpikeFader\'s master voice) deciles')

print('\n\n########## 6. SAME-N CONTROLS for the headline gate(s) ##########')
inc = [('k600', True), ('dlv', True), ('volat_20m', True), ('be6030', True), ('chg_1d', True)]
for lab, m in [('brv15>=8', df['brv15_tape']>=8), ('brv15>=12', df['brv15_tape']>=12), ('volhigh60', df['volhigh60']==True)]:
    if m.sum() >= 100: M.same_n(df, m, inc, lab)

print('\n\n########## 7. THE TAIL — where do the squeezes live? ##########')
w = df[df['ret'] < -0.20]
print(f"trips < -20%: {len(w):,} of {len(df):,} ({100*len(w)/len(df):.2f}%)   net of those: {w['ret'].sum()*100:,.0f}%   vs corpus net {df['ret'].sum()*100:,.0f}%")
for col, edges in [('brv15_tape',[0,2,4,8,12,20,40,1e9]), ('chg_1d',[-1,0,0.1,0.3,0.5,1.0,1.5,10]), ('entry_min',[585,600,630,660,720,780,840,900])]:
    d = df[~df[col].isna()].copy(); d['b'] = pd.cut(d[col], edges)
    g = d.groupby('b', observed=True)['ret']
    t = pd.DataFrame({'n': g.size(), '<-20%': (g.apply(lambda s: (s<-0.2).mean()*100)).round(2),
                      'worst%': (g.min()*100).round(1), 'tail_net%': g.apply(lambda s: s[s<-0.2].sum()*100).round(0)})
    print(f'\n--- squeeze share by {col} ---'); print(t.to_string())
print('\n--- the 30 worst trips ---')
print(df.nsmallest(30, 'ret')[['symbol','trade_date','entry_min','entry_px','ret','brv15_tape','brv20d_prior','volhigh60','chg_1d','halts_today','volat_20m']].to_string(index=False))

print('\n\n########## 8. EXIT COMPARISON: MOC vs SpikeFader\'s 540-bar (9m) cover on the SAME entries ##########')
px = df['aux_lo_540_px'].where(~df['aux_lo_540_px'].isna(), df['exit_px'])
df['ret_540'] = -(px / df['entry_px'] - 1.0)
for lab, m in [('ALL', df['ret']==df['ret']), ('brv15>=8', df['brv15_tape']>=8), ('volhigh60', df['volhigh60']==True)]:
    g = df[m]
    print(f'\n--- {lab}: n={len(g):,}   540-cover resolved {(~g["aux_lo_540_px"].isna()).mean()*100:.1f}% ---')
    print(pd.DataFrame([dict(exit='MOC (hold to close)', **M.stats(g['ret'])), dict(exit='540-bar cover else MOC', **M.stats(g['ret_540']))]).to_string(index=False))
print('\n\n########## 9. ⭐⭐ THE AVWAP RISK RULE — MOC vs rule vs always-switch, per horizon ##########')
def rule_table(g, label):
    rows=[dict(exit='MOC (hold to close)', **M.stats(g['ret']))]
    for h in ('1h','2h','3h'):
        rows.append(dict(exit=f'RULE {h}: switch to 5m-low iff AVWAP>entry ({g[f"switched_{h}"].mean()*100:.0f}% switched)', **M.stats(g[f'rule_{h}'])))
        rows.append(dict(exit=f'  control: ALWAYS switch at {h}', **M.stats(g[f'always_{h}'])))
    print(f'\n--- {label}: n={len(g):,} ---'); print(pd.DataFrame(rows).to_string(index=False))
rule_table(df, 'ALL (mc=0)')
rule_table(b, 'ALL (mc=1)')
for lab, m in [('brv15>=4', df['brv15_tape']>=4), ('brv15>=12', df['brv15_tape']>=12), ('k600>=63 (top 2 deciles)', df['k600']>=63),
               ('volhigh60', df['volhigh60']==True), ('chg_1d>=0.5', df['chg_1d']>=0.5)]:
    if m.sum() >= 500: rule_table(df[m], lab)
print('\n--- the RULE by year (1h / 2h / 3h) vs MOC ---')
rows=[]
for y,g in df.groupby('year'):
    r=dict(year=y, n=len(g), MOC=round(M.pfm1(g['ret']),3))
    for h in ('1h','2h','3h'): r[f'rule_{h}']=round(M.pfm1(g[f'rule_{h}']),3); r[f'tail<-20%_{h}']=round((g[f'rule_{h}']<-0.2).mean()*100,2)
    r['tail<-20%_MOC']=round((g['ret']<-0.2).mean()*100,2); rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print('\n--- where does the rule EXIT land? (switched trips only, 1h) ---')
sw=df[df['switched_1h']]
print(f"switched {len(sw):,}   post-check 5m-low printed {(~sw['post_chk_lo300_moc_1h'].astype(bool)).mean()*100:.1f}%   resolved at MOC {sw['post_chk_lo300_moc_1h'].astype(bool).mean()*100:.1f}%")
print(pd.DataFrame([dict(exit='switched trips: MOC', **M.stats(sw['ret'])), dict(exit='switched trips: RULE 1h', **M.stats(sw['rule_1h']))]).to_string(index=False))
print('\nDONE')
