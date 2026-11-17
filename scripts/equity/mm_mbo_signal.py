r"""MARKET-MAKING SIM, step 4: do the ORDER-BOOK features predict the next seconds of the mid? (user 2026-11-17: "I want
to include the order book signals into the market maker".) Reads the 100 ms feature grids that
`mm_mbo_sim.py --dump-features` writes (imb1, imb5, ofi_1, ofi_5, tf_1, tf_5; causal: the state before the grid time).

Target y_H = the mid H s ahead vs now, bp. Reported per name-day: the correlation of each feature and of an OLS fit on
the OTHER name-days (leave-one-out: every number is out of sample). The model fit on all non-CBRS name-days is written
to --model for `mm_mbo_sim.py --signal` (CBRS stays out of the fit).

  cd research && python -u scripts/equity/mm_mbo_signal.py > data/equity/mm_sim/mbo_signal.md 2> logs/mm_mbo_signal.log; echo exit $?
"""
import argparse, glob, json, os
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--features', default='data/equity/mm_sim/mbo_features')
ap.add_argument('--model', default='data/equity/mm_sim/mbo_signal_model.json')
ap.add_argument('--horizon', type=float, default=1.0, help='the horizon (s) of the saved model')
ap.add_argument('--test', default='CBRS')
ap.add_argument('--clip', type=float, default=50.0, help='winsorize y at ±clip bp for the FIT (the halts / jumps)')
a = ap.parse_args()
F = ['imb1', 'imb5', 'ofi_1', 'ofi_5', 'tf_1', 'tf_5']
HS = (1, 5)

parts = []
for f in sorted(glob.glob(f'{a.features}/*_features.parquet')):
    G = pd.read_parquet(f); nm = '_'.join(os.path.basename(f).split('_')[:2])
    t = G.t.to_numpy(); m = G.mid.to_numpy()
    for H in HS:
        j = np.searchsorted(t, t + int(H * 1e9), 'left')
        ok = (j < len(t)) & (m > 0)
        y = np.full(len(t), np.nan)
        y[ok] = 1e4 * (m[np.clip(j, 0, len(t) - 1)][ok] / m[ok] - 1)
        G[f'y_{H}'] = y
    G['name'] = nm
    parts.append(G.dropna())
D = pd.concat(parts, ignore_index=True)
names = sorted(D.name.unique())
print(f'# Order-book features vs the next H s of the mid — {len(names)} name-days, {len(D):,} grid points (100 ms)\n')
print('The cheap-data break-even (mm_sim noisy oracle, 527 windows): a correlation of ~0.35 with the 5 s move. '
      'Trades + NBBO features reached ~0.1.\n')


def fit(X, y):
    Xc = np.column_stack([X, np.ones(len(X))])
    return np.linalg.lstsq(Xc, np.clip(y, -a.clip, a.clip), rcond=None)[0]


def pred(w, X): return X @ w[:-1] + w[-1]


for H in HS:
    print(f'## H = {H} s\n')
    print('| name-day | points | ' + ' | '.join(F) + ' | OLS, leave-one-out |')
    print('|---|---|' + '---|' * (len(F) + 1))
    allc = []
    for nm in names:
        te = D.name == nm
        X = D.loc[te, F].to_numpy(); y = D.loc[te, f'y_{H}'].to_numpy()
        w = fit(D.loc[~te, F].to_numpy(), D.loc[~te, f'y_{H}'].to_numpy())
        c = [np.corrcoef(X[:, k], y)[0, 1] if X[:, k].std() > 0 else np.nan for k in range(len(F))]
        co = np.corrcoef(pred(w, X), y)[0, 1]
        allc.append(co)
        print(f'| {nm} | {te.sum():,} | ' + ' | '.join(f'{x:+.3f}' for x in c) + f' | **{co:+.3f}** |')
    print(f'\nMedian out-of-sample OLS correlation: {np.median(allc):+.3f}\n')

w = fit(D.loc[D.name.str.split("_").str[0] != a.test, F].to_numpy(),
        D.loc[D.name.str.split("_").str[0] != a.test, f'y_{int(a.horizon)}'].to_numpy())
json.dump(dict(w=[float(x) for x in w], thr=2.0, horizon=a.horizon, features=F, fit_on=[n for n in names if not n.startswith(a.test)]),
          open(a.model, 'w'), indent=1)
print(f'Model (H = {a.horizon} s, fit without {a.test}) → `{a.model}`: ' + ', '.join(f'{f} {x:+.2f}' for f, x in zip(F + ["const"], w)))
