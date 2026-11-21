"""GH-2 — the REVERSE analysis: what the high-volume variants' biggest winners and losers looked like at the decision
(user 2026-11-21). events.parquet (gaphv_events.py) + features.parquet (gaphv_features.py). Every trade = the SHORT to the
official close, gross; big winner / loser = the variant's top / bottom 10 % of r (as GH-1).
Per feature: its median among the big winners / all / the big losers, the short's PF by the feature's quintile (within the
variant), the winners' share of the big moves in Q1 and Q5, and Spearman ρ(feature, r).
Only features KNOWN at the variant's decision are scored (09:30 variants: the premarket to 09:25 + the daily context; 10:00
variants: those + the first 30 minutes). Then "how will we know ahead of time" — what at 09:25 (and at 09:35) predicts the
WILD gap-up open (volat30 >= U0930's top decile, the squeeze-first trades of GH-1).
    cd research && python3 scripts/equity/gaphv_reverse.py > data/equity/gaphv/reverse.md
"""
import numpy as np, pandas as pd

X = pd.read_parquet('data/equity/gaphv/events.parquet').merge(pd.read_parquet('data/equity/gaphv/features.parquet'), on=['ticker', 'd'])
X['yr'] = pd.to_datetime(X.d).dt.year
X['r0930'] = 1 - X.close / X.open
X['r1000'] = 1 - X.close / X.px10
X['r_first'] = 1 - X.px10 / X.open
X['ldv60'] = np.log10(X.dv60)
X['gap_abs'] = X.gap.abs()

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'

PRE = [('gap_abs', '|gap|', '{:.1%}'), ('rvpm', 'rvol_pm (60d median)', '{:.2f}'), ('ldv60', 'log10 dv60_med', '{:.2f}'),
       ('open', 'price (open)', '\\${:.2f}'), ('pm_nb', 'premarket traded seconds', '{:.0f}'),
       ('pm_rate', 'pm rate 08:55–09:25', '{:.2f}'), ('pm_inten', 'pm intensity ($ last 30 m / earlier $)', '{:.2f}'),
       ('pm_volat', 'pm volat 08:55–09:25 bp', '{:.0f}'), ('pm_trend', 'pm trend (last / pm vwap − 1)', '{:+.1%}'),
       ('pm_pos', 'pm position in its range', '{:.2f}'), ('pm_tsize', 'pm shares / trade', '{:.0f}'),
       ('pm_retail', 'pm retail share', '{:.2f}')]
OPEN30 = [('rv30', 'rvol30', '{:.2f}'), ('volat30', 'volat30 bp', '{:.0f}'), ('nb30', 'density 30 m (traded s / 1800)', '{:.2f}'),
          ('inten30', 'intensity 30 m ($ 09:45–10 / 09:30–45)', '{:.2f}'), ('ret30', 'ret30 (10:00 vs the open)', '{:+.1%}'),
          ('range30', 'range30', '{:.1%}'), ('pos30', 'position in the 30 m range', '{:.2f}'), ('er30', 'efficiency 30 m', '{:.2f}'),
          ('hi30_min', 'minutes to the 30 m high', '{:.1f}'), ('tsize30', 'shares / trade 30 m', '{:.0f}'),
          ('retail30', 'retail share 30 m', '{:.2f}')]
V = [('U0930', 'u0930', 'r0930', PRE), ('D0930', 'd0930', 'r0930', PRE),
     ('U1000', 'u1000', 'r1000', PRE + OPEN30), ('D1000', 'd1000', 'r1000', PRE + OPEN30)]

def table(K, feats):
    lo, hi = np.quantile(K.r, .1), np.quantile(K.r, .9)
    bw, bl = K.r >= hi, K.r <= lo
    print('| feature | big winners | all | big losers | PF Q1 | Q2 | Q3 | Q4 | Q5 | winner share of big moves Q1 → Q5 | ρ(·, r) |')
    print('|---|---|---|---|---|---|---|---|---|---|---|')
    for c, lab, fmt in feats:
        s = K[c]; ok = np.isfinite(s)
        if ok.sum() < 500: continue
        q = pd.qcut(s[ok].rank(method='first'), 5, labels=False)
        pfs = [f2(pf(K.r[ok][q == i].values)) for i in range(5)]
        sh = []
        for i in (0, 4):
            m = q == i; w, l = (bw[ok] & m).sum(), (bl[ok] & m).sum()
            sh.append(f'{100 * w / max(w + l, 1):.0f} %')
        rho = s[ok].rank().corr(K.r[ok].rank())
        print(f'| {lab} | {fmt.format(s[bw].median())} | {fmt.format(s.median())} | {fmt.format(s[bl].median())} | '
              + ' | '.join(pfs) + f' | {sh[0]} → {sh[1]} | {rho:+.2f} |')
    print()

print('# GH-2 — the reverse analysis: the high-volume variants\' big winners and losers at the decision\n')
print('Every trade = the SHORT to the close, gross. Big winner / loser = the variant\'s top / bottom 10 % of r. Quintiles '
      'within the variant (Q1 = the lowest value). Only features known at the decision.\n')
for name, flag, rc, feats in V:
    K = X[X[flag] & np.isfinite(X[rc])].copy(); K['r'] = K[rc]
    print(f'## {name} (n {len(K):,}, PF {f2(pf(K.r.values))})\n')
    table(K, feats)

print('## How will we know ahead of time? — the WILD gap-up open (U0930)\n')
K = X[X.u0930 & np.isfinite(X.r0930) & np.isfinite(X.volat30) & np.isfinite(X.px10)].copy()
cut = np.quantile(K.volat30, .9); K['wild'] = K.volat30 >= cut
print(f'Wild = volat30 ≥ {cut:.0f} bp (U0930\'s top decile, {K.wild.mean():.0%}). Per feature: the wild rate by quintile, '
      'ρ with volat30, and the short\'s PF open → close / 10:00 → close in Q1 and Q5.\n')
print('| feature (known at) | wild % Q1 | Q2 | Q3 | Q4 | Q5 | ρ(·, volat30) | PF open→close Q1 / Q5 | PF 10:00→close Q1 / Q5 |')
print('|---|---|---|---|---|---|---|---|---|')
for c, lab, fmt in PRE + [('volat5', 'volat 09:30–09:35 bp (09:35)', ''), ('ret5', 'ret 09:30–09:35 (09:35)', '')]:
    s = K[c]; ok = np.isfinite(s)
    q = pd.qcut(s[ok].rank(method='first'), 5, labels=False)
    w = [f'{100 * K.wild[ok][q == i].mean():.0f}' for i in range(5)]
    a = [f2(pf(K.r0930[ok][q == i].values)) for i in (0, 4)]; b = [f2(pf(K.r1000[ok][q == i].values)) for i in (0, 4)]
    print(f'| {lab}{"" if "09:35" in lab else " (09:25)"} | ' + ' | '.join(w) + f' | {s[ok].rank().corr(K.volat30[ok].rank()):+.2f} | '
          f'{a[0]} / {a[1]} | {b[0]} / {b[1]} |')
print()
