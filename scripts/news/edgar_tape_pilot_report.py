#!/usr/bin/env python3
"""News pilot report — the tables out of `edgar_tape_pilot.py` (2026-11-19). Markdown to stdout.

Questions:
  1. EDGAR-first or wire-first?  For each filing: the 30 min BEFORE acceptance (pre = ln p_0/p_−1800) against the
     30 min AFTER (post = ln p_1800/p_0), with a PLACEBO 30 min an hour before (ln p_−1800/p_−3600). A category whose
     |post| beats its placebo is news the tape learns from EDGAR; one whose |pre| carries the move was out before it.
  2. How fast?  For filings that moved ≥ 10% in the 30 min after: the share of that move done by +1 s … +5 min.
  3. Coverage from the mover side: of the premarket / after-hours sessions that moved ≥ X% (common stock, no split),
     how many had a filing at the onset, earlier that session / the evening before, or none.

Liquidity bucket: mean daily dollar volume of the 20 PRIOR trading days (pre + regular + post), from the stats files —
day D's own volume never enters (a same-day liquidity gate is a lookahead, CLAUDE.md rule 2).

    cd research && python3 scripts/news/edgar_tape_pilot_report.py > data/news/pilot/report.md
"""
import argparse, glob, os
import duckdb, numpy as np, pandas as pd

RTH0, RTH1 = 34200, 57600
LIQ = [0, 1e6, 5e6, 20e6, 100e6, np.inf]
LIQ_LBL = ['<1M', '1-5M', '5-20M', '20-100M', '>100M']


def md(df, floatfmt='{:.3f}'):
    cols = list(df.columns)
    lines = ['| ' + ' | '.join(map(str, cols)) + ' |', '|' + '---|' * len(cols)]
    for r in df.itertuples(index=False):
        cells = []
        for v in r:
            if isinstance(v, float): cells.append('' if np.isnan(v) else floatfmt.format(v))
            else: cells.append(str(v))
        lines.append('| ' + ' | '.join(cells) + ' |')
    return '\n'.join(lines)


def liquidity(pilot):
    return duckdb.sql(f"""
        with s as (select ticker, cast(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) as varchar) as day,
                          dv_pre + dv_rth + dv_post as dv, close_px
                   from read_parquet('{pilot}/stats/*.parquet', filename = true))
        select ticker, day,
               avg(dv) over (partition by ticker order by day rows between 20 preceding and 1 preceding) as adv20_prior,
               lag(close_px) over (partition by ticker order by day) as close_prior
        from s""").df()


def bucket(x):
    return pd.cut(x, LIQ, labels=LIQ_LBL, right=False)


def group_table(ev, key):
    rows = []
    for k, g in ev.groupby(key, observed=True):
        if len(g) < 30: continue
        big = g[g.post.abs() >= 0.10]
        rows.append(dict(group=k, n=len(g),
                         post_med=g.post.abs().median(), pre_med=g.pre.abs().median(), plc_med=g.plc.abs().median(),
                         post10=(g.post.abs() >= 0.10).mean(), pre10=(g.pre.abs() >= 0.10).mean(),
                         plc10=(g.plc.abs() >= 0.10).mean(),
                         n_big=len(big),
                         **{f'done_{h}': (big[f'r_{h}'] / big.post).clip(-1, 2).median() if len(big) else np.nan
                            for h in ('1', '10', '60', '300')}))
    return pd.DataFrame(rows, columns=['group', 'n', 'post_med', 'pre_med', 'plc_med', 'post10', 'pre10', 'plc10', 'n_big',
                                       'done_1', 'done_10', 'done_60', 'done_300'])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pilot', default='data/news/pilot')
    ap.add_argument('--meta', default='data/news/edgar')
    a = ap.parse_args()
    liq = liquidity(a.pilot)
    ev = pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{a.pilot}/events/*.parquet'))], ignore_index=True)
    n_all, n_fil = len(ev), ev.accession.nunique()
    ev['split_today'] = ev.split_today.fillna(False).astype(bool)          # empty days concat as float
    ev = ev[(ev.n_bars > 0) & ev.p_0.notna() & ~ev.split_today]
    # one event per (ticker, day, t0): an 8-K and its 424B5 accepted the same second are one piece of news
    ev = ev.sort_values('form').groupby(['ticker', 'day', 't0'], as_index=False).agg(
        forms=('form', lambda s: ','.join(sorted(set(s)))), items=('items', lambda s: ','.join(sorted(set(
            i for x in s for i in (x or '').split(',') if i)))),
        **{c: (c, 'first') for c in ev.columns if c not in ('ticker', 'day', 't0', 'form', 'items')})
    ev = ev.merge(liq, on=['ticker', 'day'], how='left')
    ev['liq'] = bucket(ev.adv20_prior)
    ev['pre'] = np.log(ev.p_0 / ev.p_m1800)
    ev['post'] = np.log(ev.p_1800 / ev.p_0)
    ev['plc'] = np.log(ev.p_m1800 / ev.p_m3600)
    for h in (1, 10, 60, 300): ev[f'r_{h}'] = np.log(ev[f'p_{h}'] / ev.p_0)
    ev['sess'] = np.select([ev.overnight, ev.t0 < RTH0, ev.t0 < RTH1], ['overnight', 'pre', 'rth'], 'post')
    ev = ev[ev.pre.notna() & ev.plc.notna() & ev.post.notna()]

    print('# News pilot — EDGAR acceptance × the 1s tape\n')
    print(f'{n_all} (filing, ticker) rows, {n_fil} filings → {len(ev)} events with tape on both sides '
          f'(one per ticker × acceptance second; split days dropped). '
          f'{ev.day.min()} → {ev.day.max()}.\n')
    print('Columns: `post` = |ln p(+30m)/p(0)|, `pre` = |ln p(0)/p(−30m)|, `plc` = |ln p(−30m)/p(−60m)| (placebo); '
          '`*_med` medians, `*10` = share ≥ 10%; `done_h` = median share of the 30-min move done by +h s, '
          'among the n_big events with |post| ≥ 10%.\n')

    print('## 1. By liquidity (prior-20-day mean dollar volume) × session\n')
    ev['liq_sess'] = ev.liq.astype(str) + ' / ' + ev.sess
    t = group_table(ev, 'liq_sess')
    print(md(t), '\n')

    small = ev[ev.adv20_prior < 20e6]
    print('## 2. Small names (prior ADV < \\$20M), outside regular hours — by form\n')
    xs = small[small.sess != 'rth']
    print(md(group_table(xs, 'forms').sort_values('n', ascending=False).head(25)), '\n')

    print('## 3. Same, 8-K / 8-K/A by item (an event counts once per item)\n')
    k8 = xs[xs.forms.str.contains('8-K')].copy()
    k8['item'] = k8['items'].str.split(',')
    k8 = k8.explode('item')
    k8 = k8[k8.item.notna() & (k8.item != '') & (k8.item != '9.01')]
    print(md(group_table(k8, 'item').sort_values('n', ascending=False)), '\n')

    # ---- mover side ----
    uni = pd.read_parquet(f'{a.meta}/universe.parquet')
    mv = pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{a.pilot}/movers/*.parquet'))], ignore_index=True)
    mv = mv[mv.ticker.isin(set(uni.ticker)) & ~mv.split_today.fillna(False).astype(bool)]
    mv = mv.merge(liq, on=['ticker', 'day'], how='left')
    days = sorted(mv.day.unique()); prevd = dict(zip(days[1:], days[:-1]))
    allev = pd.concat([pd.read_parquet(f, columns=['ticker', 'day', 't0', 'form', 'items'])
                       for f in sorted(glob.glob(f'{a.pilot}/events/*.parquet'))], ignore_index=True)
    by_td = {k: g.t0.to_numpy() for k, g in allev.groupby(['ticker', 'day'])}

    print('## 4. Coverage from the mover side\n')
    print('A mover = a premarket / after-hours session of a common stock whose 1-minute VWAP crossed ±X% from its '
          'reference (pre: the prior regular-hours close; post: the day\'s close). Onset = the first minute across X. '
          '`at_onset` = a filing accepted from 15 min before to 5 min after the onset; `earlier` = before that, the '
          'same session (pre: also the prior evening); `none` = no filing of the forms studied.\n')
    rows, examples = [], []
    for X, col_up, col_dn in ((0.10, 't_up10', 't_dn10'), (0.20, 't_up20', 't_dn20')):
        for dvmin in (1e5, 1e6):
            m = mv[(mv.dv >= dvmin) & (mv.adv20_prior < 20e6)].copy()
            up = m[col_up].astype('float'); dn = m[col_dn].astype('float')
            m['onset'] = np.fmin(up, dn)
            m = m[m.onset.notna()]
            for sess in ('pre', 'post'):
                ms = m[m.sess == sess]
                cat = []
                for r in ms.itertuples(index=False):
                    t = by_td.get((r.ticker, r.day), np.array([]))
                    lo_sess = 0 if sess == 'pre' else RTH0
                    at = ((t >= r.onset - 900) & (t <= r.onset + 300)).any()
                    earlier = ((t < r.onset - 900) & (t >= lo_sess)).any()
                    if sess == 'pre' and not earlier:
                        tp = by_td.get((r.ticker, prevd.get(r.day)), np.array([]))
                        earlier = (tp >= RTH1 - 1800).any()
                    cat.append('at_onset' if at else 'earlier' if earlier else 'none')
                    if X == 0.20 and dvmin == 1e6:
                        examples.append(dict(day=r.day, ticker=r.ticker, sess=sess, onset=int(r.onset), cat=cat[-1],
                                             r_max=r.r_max, r_min=r.r_min, dv=r.dv))
                cat = pd.Series(cat, dtype='object')
                at04 = (ms.onset == 14400).to_numpy() if sess == 'pre' else np.zeros(len(ms), bool)
                rows.append(dict(move=f'±{X:.0%}', sess_dv=f'≥{dvmin / 1e6:g}M', sess=sess, n=len(ms),
                                 at_onset=(cat == 'at_onset').mean(), earlier=(cat == 'earlier').mean(),
                                 none=(cat == 'none').mean(), onset_0400=at04.mean() if len(ms) else np.nan,
                                 none_excl_0400=(cat[~at04] == 'none').mean() if (~at04).any() else np.nan))
    print(md(pd.DataFrame(rows)), '\n')
    print('`onset_0400` = the session\'s first print was already across X (news in the dark hours — the onset minute is '
          'then the open of the tape, not the news time); `none_excl_0400` = the `none` share among the rest.\n')
    exd = pd.DataFrame(examples)
    exd.to_parquet(os.path.join(a.pilot, 'movers_matched.parquet'), index=False)


if __name__ == '__main__':
    main()
