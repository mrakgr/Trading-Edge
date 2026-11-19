#!/usr/bin/env python3
"""News pilot — can the EDGAR-first case be TRADED? (2026-11-19). Markdown to stdout.

Universe (all causal — known at the acceptance second t0): small names (mean daily dollar volume of the 20 PRIOR days
< $20M), filing accepted premarket or after hours, and a FLAT tape in the 30 min before (|ln p_0/p_−1800| < 2%). The
size of the move afterwards never selects anything.

Rule A — follow the tape's first reaction: at t0 + L (L = 30 / 60 / 120 s), if |ln p_L/p_0| ≥ θ, take the side of that
move at p_L; exit at p_X (X = 5 / 10 / 30 / 60 min). Fill = the last print before t0+L — OPTIMISTIC (a taker pays
the spread; out-of-hours spreads in these names are wide): the cost columns charge 1% and 2% round trip.
Rule B — a side fixed in advance per category (8-K item, or form): learned on 2024 (sign of the mean
ln p_1800/p_60 over ALL the category's events), tested on 2025-01 → 2026-11 (enter at p_60, exit at p_1800).

CONTROL (`--placebo`): the same names at the same clock time 5 trading days earlier (`edgar_tape_pilot.py
--placebo-lag 5`), dropping any placebo whose ticker filed on that earlier day. A rule that also "works" on the
placebo measures how illiquid names move, not news.

    cd research && python3 scripts/news/edgar_first_trade.py > logs/news_edgar_first_trade.log 2>&1
"""
import argparse, glob
import duckdb, numpy as np, pandas as pd

RTH0, RTH1 = 34200, 57600


def md(df, fmt='{:.4f}'):
    cols = list(df.columns)
    out = ['| ' + ' | '.join(map(str, cols)) + ' |', '|' + '---|' * len(cols)]
    for r in df.itertuples(index=False):
        out.append('| ' + ' | '.join('' if isinstance(v, float) and np.isnan(v) else fmt.format(v) if isinstance(v, float)
                                     else str(v) for v in r) + ' |')
    return '\n'.join(out)


def load(pilot_dir, liq, drop_keys=None):
    ev = pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{pilot_dir}/events/*.parquet'))], ignore_index=True)
    ev['split_today'] = ev.split_today.astype('boolean').fillna(False).astype(bool)
    ev = ev[(ev.n_bars > 0) & ev.p_0.notna() & ~ev.split_today]
    if drop_keys is not None and 'tape_day' in ev:
        k = pd.MultiIndex.from_arrays([ev.ticker, ev.tape_day])
        ev = ev[~k.isin(drop_keys)]
    ev = ev.sort_values('form').groupby(['ticker', 'day', 't0'], as_index=False).agg(
        forms=('form', lambda s: ','.join(sorted(set(s)))),
        items=('items', lambda s: ','.join(sorted({i for x in s for i in (x or '').split(',') if i}))),
        **{c: (c, 'first') for c in ev.columns if c not in ('ticker', 'day', 't0', 'form', 'items')})
    ev = ev.merge(liq, on=['ticker', 'day'], how='left')
    ev['pre'] = np.log(ev.p_0 / ev.p_m1800)
    ev['sess'] = np.select([ev.overnight, ev.t0 < RTH0, ev.t0 < RTH1], ['overnight', 'pre', 'rth'], 'post')
    return ev[(ev.adv20_prior < 20e6) & ev.sess.isin(['pre', 'post']) & (ev.pre.abs() < 0.02)].copy()


def stats(r):
    r = r.dropna()
    if len(r) == 0: return dict(n=0)
    pos, neg = r[r > 0].sum(), -r[r < 0].sum()
    return dict(n=len(r), mean=r.mean(), median=r.median(), win=(r > 0).mean(), pf=pos / neg if neg > 0 else np.inf,
                net1=(r - 0.01).mean(), net2=(r - 0.02).mean())


def rule_a(ev, tag):
    rows = []
    for L in (30, 60, 120):
        sig = np.log(ev[f'p_{L}'] / ev.p_0)
        for th in (0.02, 0.05):
            m = sig.abs() >= th
            side = np.sign(sig[m])
            for X in (300, 600, 1800, 3600):
                r = side * np.log(ev.loc[m, f'p_{X}'] / ev.loc[m, f'p_{L}'])
                rows.append(dict(set=tag, L=L, theta=th, exit=X, **stats(r)))
    return pd.DataFrame(rows)


def explode_cats(ev):
    k8 = ev[ev.forms.str.contains('8-K')].assign(cat=lambda d: d['items'].str.split(',')).explode('cat')
    k8 = k8[k8.cat.notna() & ~k8.cat.isin(['', '9.01'])]
    k8['cat'] = 'item ' + k8.cat
    fo = ev[~ev.forms.str.contains('8-K')].assign(cat=lambda d: d.forms.str.split(',')).explode('cat')
    fo['cat'] = fo.cat.str.replace('SCHEDULE 13D', 'SC 13D')
    return pd.concat([k8, fo], ignore_index=True)


def rule_b(ev, plc):
    c, cp = explode_cats(ev), explode_cats(plc)
    c['r'] = np.log(c.p_1800 / c.p_60); cp['r'] = np.log(cp.p_1800 / cp.p_60)
    train, test = c[c.day < '2025-01-01'], c[c.day >= '2025-01-01']
    rows = []
    for cat, g in train.groupby('cat'):
        g = g.r.dropna()
        if len(g) < 30: continue
        side = np.sign(g.mean()); t = g.mean() / (g.std() / np.sqrt(len(g)))
        te = test.loc[test.cat == cat, 'r'] * side
        pl = cp.loc[(cp.cat == cat) & (cp.day >= '2025-01-01'), 'r'] * side
        s, sp = stats(te), stats(pl)
        rows.append(dict(cat=cat, train_n=len(g), side='long' if side > 0 else 'short', train_mean=g.mean(), train_t=t,
                         test_n=s.get('n', 0), test_mean=s.get('mean', np.nan), test_win=s.get('win', np.nan),
                         test_pf=s.get('pf', np.nan), plc_n=sp.get('n', 0), plc_mean=sp.get('mean', np.nan)))
    return pd.DataFrame(rows).sort_values('train_t', key=abs, ascending=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pilot', default='data/news/pilot')
    ap.add_argument('--placebo', default='data/news/pilot_placebo5')
    a = ap.parse_args()
    liq = duckdb.sql(f"""
        with s as (select ticker, regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) as day,
                          dv_pre + dv_rth + dv_post as dv from read_parquet('{a.pilot}/stats/*.parquet', filename = true))
        select ticker, day, avg(dv) over (partition by ticker order by day rows between 20 preceding and 1 preceding)
               as adv20_prior from s""").df()
    allk = duckdb.sql(f"select distinct ticker, day from read_parquet('{a.pilot}/events/*.parquet')").df()
    filed = pd.MultiIndex.from_frame(allk)
    ev = load(a.pilot, liq)
    plc = load(a.placebo, liq, drop_keys=filed)
    print('# EDGAR-first — can it be traded?\n')
    print(f'Universe: small (prior ADV < \\$20M), pre/post-market, flat 30 min before acceptance. '
          f'Events {len(ev)} ({ev.day.min()} → {ev.day.max()}); placebo (same name, same clock time, 5 trading days '
          f'earlier, no filing that day) {len(plc)}.\n')
    print('Returns are log, per trade, before costs unless `net1` / `net2` (1% / 2% round trip). '
          'Entry fill = the last print before t0+L (optimistic).\n')

    print('## Rule A — follow the first reaction\n')
    ta, tp = rule_a(ev, 'filing'), rule_a(plc, 'placebo')
    both = ta.merge(tp, on=['L', 'theta', 'exit'], suffixes=('', '_plc'))
    cols = ['L', 'theta', 'exit', 'n', 'mean', 'median', 'win', 'pf', 'net1', 'net2', 'n_plc', 'mean_plc', 'win_plc', 'pf_plc']
    print(md(both[cols]), '\n')

    best = (60, 0.05, 1800)
    sig = np.log(ev[f'p_{best[0]}'] / ev.p_0); m = sig.abs() >= best[1]
    t = ev[m].assign(r=np.sign(sig[m]) * np.log(ev.loc[m, f'p_{best[2]}'] / ev.loc[m, f'p_{best[0]}']),
                     year=lambda d: d.day.str[:4], side=np.where(sig[m] > 0, 'long', 'short'))
    print(f'### Rule A at L={best[0]} s, θ={best[1]:.0%}, exit {best[2] // 60} min — by year × session × side\n')
    rows = [dict(year=y, sess=s_, side=sd, **stats(g.r)) for (y, s_, sd), g in t.groupby(['year', 'sess', 'side'])]
    print(md(pd.DataFrame(rows)), '\n')
    print('Capacity: dollar volume traded from +1 to +5 min after acceptance on these trades: median '
          f'\\${t.dv_60_300.median():,.0f}, p75 \\${t.dv_60_300.quantile(.75):,.0f}; +5 to +30 min: median '
          f'\\${t.dv_300_1800.median():,.0f}.\n')

    print('## Rule B — a side fixed per category (train 2024, test 2025-01 → 2026-11; enter +60 s, exit +30 min)\n')
    rb = rule_b(ev, plc)
    print(md(rb), '\n')

    # ⚠ 2026-11-19: a first cut split this by the volume from +1 to +5 min — AFTER the +60 s entry, a lookahead (a volume
    # surge after entry goes with the move) that showed PF 3.2. The split must use only what is known at entry.
    print('### Rule B on what could be traded — the categories with |train t| ≥ 2, test period, by the dollar volume of '
          'the FIRST MINUTE after acceptance (known at the +60 s entry)\n')
    sides = {r.cat: (1 if r.side == 'long' else -1) for r in rb.itertuples() if abs(r.train_t) >= 2}
    c = explode_cats(ev)
    c = c[c.cat.isin(sides) & (c.day >= '2025-01-01')].copy()
    c['rs'] = np.log(c.p_1800 / c.p_60) * c.cat.map(sides)
    c['dv1m'] = c.dv_0_10 + c.dv_10_60
    rows = [dict(first_min_dv=lab, **stats(c.loc[m, 'rs'])) for lab, m in
            (('any', c.rs.notna()), ('≥ $10k', c.dv1m >= 1e4), ('≥ $50k', c.dv1m >= 5e4), ('≥ $250k', c.dv1m >= 2.5e5))]
    print(f'categories: {", ".join(f"{k} {"long" if v > 0 else "short"}" for k, v in sides.items())}\n')
    print(md(pd.DataFrame(rows)), '\n')


if __name__ == '__main__':
    main()
