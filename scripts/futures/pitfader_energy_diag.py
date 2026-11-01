"""PitFader PIT-13 — the energy diagnostic (user 2026-11-01: "Can we really not find any edge in them [commodities]?"):
CL / MCL / NG / RB at k300 >= 52, exit 300, mc = 0 raw PF by year, side, hour, weekday, and the scheduled-report hour
(EIA crude / products Wed 10:30–11:30 ET; NG storage Thu 10:30–11:30) vs the rest.

  cd research && python -u scripts/futures/pitfader_energy_diag.py > data/futures/pitfader/energy_diag.md 2> logs/pitfader_energy_diag.log
"""
import numpy as np, pandas as pd, pyarrow.dataset as ds
t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=['root', 'td', 'side', 'sig_t', 'entry_px', 'exit_px', 'exit_t', 'k_300', 'volat_20m'],
    filter=ds.field('root').isin(['CL', 'MCL', 'NG', 'RB']) & (ds.field('volat_20m') > 0))
d = t.to_pandas(); d = d[d.entry_px.abs() > 0]
d['raw'] = 1e4 * d.side * (d.exit_px - d.entry_px) / d.entry_px.abs(); d['td'] = pd.to_datetime(d.td)
d['yr'] = d.td.dt.year; d['dow'] = d.td.dt.dayofweek; d['hr'] = d.sig_t // 3600; d['m'] = d.sig_t // 60
d['ep'] = d.groupby(['root', 'td', 'side', 'exit_t']).ngroup()
def pf(r): g, l = r[r > 0].sum(), -r[r < 0].sum(); return g / l if l > 0 else np.inf
for root in ['CL', 'MCL', 'NG', 'RB']:
    x = d[(d.root == root) & (d.k_300 >= 52)]
    print(f'\n== {root} k300>=52 exit 300, mc0 raw PF (episodes)')
    print('  by year:', ' '.join(f'{y}:{pf(g.raw.values):.2f}({g.ep.nunique()})' for y, g in x.groupby('yr')))
    for nm, part in (('fit', x[x.yr <= 2022]), ('holdout', x[x.yr > 2022])):
        print(f'  {nm} by side: long {pf(part[part.side > 0].raw.values):.2f} / short {pf(part[part.side < 0].raw.values):.2f};',
              'by hour:', ' '.join(f'{h}:{pf(g.raw.values):.2f}({g.ep.nunique()})' for h, g in part.groupby('hr')), ';',
              'by weekday:', ' '.join(f'{"MTWTF"[w]}:{pf(g.raw.values):.2f}({g.ep.nunique()})' for w, g in part.groupby('dow')))
    if root in ('CL', 'MCL', 'RB', 'NG'):
        ev_day = 2 if root != 'NG' else 3                         # EIA crude/products Wed 10:30; NG storage Thu 10:30
        win = (x.dow == ev_day) & (x.sig_t >= 10 * 3600 + 30 * 60) & (x.sig_t < 11 * 3600 + 30 * 60)
        for nm, part in (('fit', x.yr <= 2022), ('holdout', x.yr > 2022)):
            print(f'  {nm}: the report hour ({"Wed" if ev_day == 2 else "Thu"} 10:30–11:30) {pf(x[win & part].raw.values):.2f} ({x[win & part].ep.nunique()}) vs the rest {pf(x[~win & part].raw.values):.2f} ({x[~win & part].ep.nunique()})')
