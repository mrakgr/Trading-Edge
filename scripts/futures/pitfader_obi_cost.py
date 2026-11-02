"""What would historical CME order-book data cost for PitFader's episodes? (user 2026-11-02: Malcolm asks whether to pay
for MBO data; "$50 would be a cheap way to verify whether there is any benefit" — Rithmic serves no order-book history,
so this prices Databento's GLBX.MDP3 instead, with its FREE metadata.get_cost endpoint; nothing is bought.)

The episodes: each group's CORE (pitfader_dimb_groups.py CORES: k300 + the per-root absolute volat floor fit on 2015–22),
mc = 0 episodes (one root-session-side's trips exiting together). A window per episode = [its first signal − LEAD s, its
last exit]; windows of one contract-day merged. Schemas:
  mbp-10  the top 10 levels after every book event (self-contained; from 2010)
  mbp-1   the top of book with sizes after every event (from 2010)
  mbo     every order event; the book needs Databento's 00:00 UTC snapshot, so a window is priced FROM UTC MIDNIGHT (from 2017-05-21)
Cost: a stratified sample of windows per group × year, priced exactly; the total = per stratum the sampled $ per window
× the stratum's windows.

  cd research && python -u scripts/futures/pitfader_obi_cost.py > data/futures/pitfader/obi_cost.md 2> logs/pitfader_obi_cost.log; echo exit $?
"""
import argparse, datetime as dt, os, sys, time, warnings, zoneinfo
import numpy as np, pandas as pd, pyarrow.dataset as ds
import databento as db
from databento.common.error import BentoWarning

ap = argparse.ArgumentParser()
ap.add_argument('--lead', type=int, default=1200)
ap.add_argument('--per-stratum', type=int, default=5)
ap.add_argument('--seed', type=int, default=20261102)
a = ap.parse_args()
warnings.filterwarnings('ignore', category=BentoWarning)
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
NY, UTC = zoneinfo.ZoneInfo('America/New_York'), dt.timezone.utc
CORES = {'equity': (['ES', 'MES', 'NQ'], 75, 300, True), 'bonds': (['ZB', 'ZN', 'ZT'], 25, 600, False),
         'fx': (['6A', '6B', '6C', '6E', '6J', '6N'], 52, 300, True), 'metals': (['HG', 'MGC'], 52, 300, True),
         'ng': (['NG'], 75, 900, True)}
cols = ['root', 'contract', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600',
        'exit_px_600', 'exit_t_900', 'exit_px_900']

W = []
for g, (roots, K, n, use_floor) in CORES.items():
    t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=(ds.field('volat_20m') > 0) & ds.field('root').isin(roots))
    c = lambda x: t[x].to_numpy(zero_copy_only=False)
    root, td, s, epx, v, k3 = c('root'), pd.to_datetime(c('td')), c('side'), c('entry_px'), c('volat_20m'), c('k_300')
    ext, xpx = (c('exit_t'), c('exit_px')) if n == 300 else (c(f'exit_t_{n}'), c(f'exit_px_{n}'))
    RAW = 1e4 * s * (xpx - epx) / np.abs(epx); NET = RAW - 1e4 * c('tick') / np.abs(epx)
    FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
    rc = pd.Categorical(root).codes.astype(np.int64); day = pd.factorize(td)[0].astype(np.int64)
    key = (rc * 100_000 + day) * 2 + (s > 0)
    EP = pd.factorize(pd.Series(key * 100_000 + ext.astype(np.int64)))[0]
    base = (np.abs(epx) > 0) & (k3 >= K)
    if use_floor:                                    # PIT-7: the same floor as pitfader_dimb_groups.py
        VBP = 1e4 * v; FL = np.zeros(len(base), np.bool_)
        for r in sorted(set(root)):
            rr = base & (root == r); rf = rr & FIT
            if len(np.unique(EP[rf])) < 30:
                FL |= rr; continue
            best = None
            for q in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
                th = np.quantile(VBP[rf], q); m = rf & (VBP >= th)
                if len(np.unique(EP[m])) < 30:
                    break
                sc = NET[m].sum()
                if best is None or sc > best[0]:
                    best = (sc, th)
            FL |= rr & (VBP >= best[1])
        base = FL
    E = (pd.DataFrame({'root': root[base], 'contract': c('contract')[base], 'td': td[base], 'ep': EP[base],
                       'sig': c('sig_t')[base].astype(np.int64), 'ext': ext[base].astype(np.int64)})
         .groupby('ep').agg(root=('root', 'first'), contract=('contract', 'first'), td=('td', 'first'), lo=('sig', 'min'), hi=('ext', 'max')))
    E['lo'] -= a.lead; E['group'] = g
    log(f'{g}: {len(E):,} episodes ({E.td.dt.year.min()}–{E.td.dt.year.max()})')
    W.append(E)
    del t
E = pd.concat(W, ignore_index=True)
# merge the windows of one contract-day
rows = []
for (grp, con, d), x in E.sort_values('lo').groupby(['group', 'contract', 'td']):
    lo, hi, ne = None, None, 0
    for r in x.itertuples():
        if lo is None: lo, hi, ne = r.lo, r.hi, 1
        elif r.lo <= hi: hi, ne = max(hi, r.hi), ne + 1
        else: rows.append((grp, con, d, lo, hi, ne)); lo, hi, ne = r.lo, r.hi, 1
    rows.append((grp, con, d, lo, hi, ne))
Wd = pd.DataFrame(rows, columns=['group', 'contract', 'td', 'lo', 'hi', 'n_ep'])
Wd['year'] = Wd.td.dt.year
def utc(d, sec): return (dt.datetime(d.year, d.month, d.day, tzinfo=NY) + dt.timedelta(seconds=int(sec))).astimezone(UTC)
def raw(con):                                   # 6AH15 -> 6AH5 (Databento: product, month, one-digit year)
    return con[:-2] + con[-1]
log(f'{len(Wd):,} windows ({int(Wd.n_ep.sum()):,} episodes), mean {(Wd.hi - Wd.lo).mean() / 60:.1f} min')

key = open(os.path.expanduser('~/.config/databento/credentials')).read().split('KEY=', 1)[1].split()[0].strip().strip('"')
C = db.Historical(key)
MBO0 = pd.Timestamp('2017-05-22')
S = Wd.groupby(['group', 'year'], group_keys=False).apply(lambda x: x.sample(min(len(x), a.per_stratum), random_state=a.seed))
log(f'pricing {len(S):,} sampled windows × 3 schemas')
out = []
for i, r in enumerate(S.itertuples()):
    s0, s1 = utc(r.td, r.lo), utc(r.td, r.hi + 2)
    rec = dict(group=r.group, year=r.year, contract=r.contract, td=r.td)
    for sch in ('mbp-10', 'mbp-1', 'mbo'):
        if sch == 'mbo' and r.td < MBO0: rec[sch] = np.nan; continue
        st = s0.replace(hour=0, minute=0, second=0) if sch == 'mbo' else s0
        for att in range(4):
            try:
                rec[sch] = C.metadata.get_cost(dataset='GLBX.MDP3', schema=sch, symbols=[raw(r.contract)], stype_in='raw_symbol',
                                               start=st.isoformat(), end=s1.isoformat()); break
            except Exception as e:
                if att == 3: log(f'  {r.contract} {r.td.date()} {sch}: {type(e).__name__}'); rec[sch] = np.nan
                time.sleep(2 * (att + 1))
    out.append(rec)
    if (i + 1) % 50 == 0: log(f'  priced {i + 1:,}/{len(S):,}')
P = pd.DataFrame(out)
N = Wd.groupby(['group', 'year']).size().rename('n_win')
M = P.groupby(['group', 'year'])[['mbp-10', 'mbp-1', 'mbo']].mean().join(N)
for sch in ('mbp-10', 'mbp-1', 'mbo'): M[f'{sch} $'] = M[sch] * M.n_win
zero = (P[['mbp-10', 'mbp-1']] == 0).all(axis=1).sum()
print('# PitFader episodes: what CME order-book history would cost on Databento (GLBX.MDP3, priced, nothing bought)\n')
print(f'{int(Wd.n_ep.sum()):,} core episodes in {len(Wd):,} contract-day windows ([first signal − {a.lead // 60} min, last exit], '
      f'mean {(Wd.hi - Wd.lo).mean() / 60:.0f} min); {len(P):,} windows priced (≤ {a.per_stratum} per group-year); '
      f'{zero} priced at $0 on both MBP schemas (a symbol mismatch would show here). MBO priced from 00:00 UTC (its snapshot), 2017-05-22 on.\n')
print('| group | episodes | windows | MBP-10 $ | MBP-1 $ | MBO $ (from 2017-05-22, from midnight) | MBP-10 $ per window |\n|---|---|---|---|---|---|---|')
G = M.groupby('group')
for g in CORES:
    x = M.loc[g]; ne = int(Wd[Wd.group == g].n_ep.sum())
    print(f'| {g} | {ne:,} | {int(x.n_win.sum()):,} | {x["mbp-10 $"].sum():,.0f} | {x["mbp-1 $"].sum():,.0f} | {x["mbo $"].sum():,.0f} | '
          f'{x["mbp-10 $"].sum() / x.n_win.sum():.2f} |')
tot = M[['mbp-10 $', 'mbp-1 $', 'mbo $']].sum()
print(f'| **all** | {int(Wd.n_ep.sum()):,} | {len(Wd):,} | **{tot["mbp-10 $"]:,.0f}** | **{tot["mbp-1 $"]:,.0f}** | **{tot["mbo $"]:,.0f}** | |')
print('\n## By year (all groups, $)\n')
Y = M.groupby(level='year')[['n_win', 'mbp-10 $', 'mbp-1 $', 'mbo $']].sum()
print('| year | windows | MBP-10 | MBP-1 | MBO |\n|---|---|---|---|---|')
for y, r in Y.iterrows():
    print(f'| {y} | {int(r.n_win):,} | {r["mbp-10 $"]:,.0f} | {r["mbp-1 $"]:,.0f} | {r["mbo $"]:,.0f} |')
P.to_parquet('data/futures/pitfader/obi_cost_sample.parquet', index=False)
log('done')
