#!/usr/bin/env python3
"""News pilot — EDGAR acceptance times × the 1s tape (2026-11-19).

One pass per trading day D over `data/intraday_1s_slim/<D>.parquet` (raw prices, 04:00–20:00 ET, bucket = seconds
since 00:00 ET). Three outputs per day, under <out>/:

  events/<D>.parquet  one row per (filing, ticker) whose first tradable second falls on D: the tape around the
                      acceptance time t0 — last price before t0 + each offset (−1 h … +1 h), dollar volume per window,
                      the extremes after t0, the age of the last print before t0, the delay to the first print after.
  movers/<D>.parquet  every premarket / after-hours session of every ticker that moved ≥ 5% from its reference
                      (pre: D−1's last regular-hours price; post: D's last regular-hours price), on 1-minute VWAPs:
                      the first minute crossing ±5/10/20%, the extremes and when, the peak-volume minute. This is the
                      MOVER side — matched to filings afterwards to measure how many big moves have a filing nearby.
  stats/<D>.parquet   per ticker: first / open / close / last price, dollar volume pre / regular / post — the liquidity
                      buckets and day-level returns are built from these.

The time axis around t0 is SESSION TIME: D−1's after-hours (16:00–20:00) is mapped onto [0, 4 h) just before D's 04:00,
so an early-premarket or overnight filing still sees the last prints before it (the 8 dark hours count as zero).
A filing accepted while the tape is dark (20:00–04:00, weekends, holidays) gets t0 = 04:00 of the next trading day,
`overnight = True`.

Tickers: the CIK → ticker map of `edgar_filings_index.py` (Massive's CURRENT symbols, active + delisted since 2024):
a ticker renamed inside the window misses its earlier tape — the `n_bars` = 0 share measures that.
Splits: `split_today` flags a split executed on D (data/splits.csv) — the pre-session reference and any overnight
event's p_0 then sit on the old share scale.

    cd research && python3 scripts/news/edgar_tape_pilot.py --from 2024-01-02 --to 2026-11-16 \
        > logs/news_edgar_tape_pilot.log 2>&1; echo "exit $?" >> logs/news_edgar_tape_pilot.log
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd

FORMS = ['8-K', '8-K/A', '6-K', '6-K/A', '424B1', '424B3', '424B4', '424B5', '424B7',
         'SC 13D', 'SC 13D/A', 'SCHEDULE 13D', 'SCHEDULE 13D/A', 'S-1', 'F-1', 'S-3', 'F-3']
PRE0, RTH0, RTH1, POST1 = 14400, 34200, 57600, 72000
OFFS = [-3600, -1800, -600, -300, -60, -10, 0, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600]
DVW = [(-3600, -300), (-300, -60), (-60, 0), (0, 10), (10, 60), (60, 300), (300, 1800), (1800, 3600)]
TAPE = 'data/intraday_1s_slim'


def oname(h): return f'm{-h}' if h < 0 else str(h)


def load_events(meta, days):
    fl = duckdb.sql(f"""select cik, accession, form, items, acceptance_utc from read_parquet('{meta}/filings/*.parquet')
                        where form in ({','.join("'" + f + "'" for f in FORMS)}) and acceptance_utc is not null""").df()
    # a filing lists every co-registrant / 13D party: one row per (accession, cik), never per accession alone
    # (2026-11-19: dedup on accession kept an arbitrary cik — the event count changed from run to run)
    fl = fl.drop_duplicates(['accession', 'cik'])
    uni = pd.read_parquet(f'{meta}/universe.parquet')[['cik', 'ticker']].drop_duplicates()
    ev = fl.merge(uni, on='cik')
    et = ev.acceptance_utc.dt.tz_convert('America/New_York')
    ev['accept_et'] = et.dt.tz_localize(None)
    sec = (et.dt.hour * 3600 + et.dt.minute * 60 + et.dt.second).to_numpy()
    date = et.dt.strftime('%Y-%m-%d').to_numpy()
    td = np.array(sorted(days))
    tdset = set(days)
    on_day = np.array([d in tdset for d in date]) & (sec >= PRE0) & (sec < POST1)
    nxt_ge = td[np.minimum(np.searchsorted(td, date, 'left'), len(td) - 1)]     # first trading day >= date
    nxt_gt = td[np.minimum(np.searchsorted(td, date, 'right'), len(td) - 1)]    # first trading day > date
    ev['day'] = np.where(on_day, date, np.where(sec < PRE0, nxt_ge, nxt_gt))
    ev['t0'] = np.where(on_day, sec, PRE0)
    ev['overnight'] = ~on_day
    beyond = np.where(sec < PRE0, np.searchsorted(td, date, 'left'), np.searchsorted(td, date, 'right')) >= len(td)
    return ev[~(beyond & ~on_day)]


def event_features(ev_d, bars):
    out = []
    g = {t: x for t, x in bars.groupby('ticker', sort=False)}
    for r in ev_d.itertuples(index=False):
        row = dict(accession=r.accession, ticker=r.ticker)
        x = g.get(r.ticker)
        if x is None or len(x) == 0:
            row['n_bars'] = 0; out.append(row); continue
        b = x.bucket.to_numpy(); p = x.vwap.to_numpy(np.float64); dv = p * x.volume.to_numpy(np.float64)
        cdv = np.concatenate([[0.0], np.cumsum(dv)])
        t0 = r.t0
        ix = lambda t: int(np.searchsorted(b, t, 'left'))                     # bars with bucket < t
        i0 = ix(t0)
        row['n_bars'] = len(b)
        for h in OFFS:
            k = ix(t0 + h)
            row[f'p_{oname(h)}'] = p[k - 1] if k > 0 else np.nan
        for lo, hi in DVW:
            row[f'dv_{oname(lo)}_{oname(hi)}'] = cdv[ix(t0 + hi)] - cdv[ix(t0 + lo)]
        row['age0'] = t0 - b[i0 - 1] if i0 > 0 else np.nan
        row['first_after'] = b[i0] - t0 if i0 < len(b) else np.nan
        for H in (300, 1800, 3600):
            s = p[i0:ix(t0 + H)]
            row[f'hi_{H}'] = s.max() if len(s) else np.nan
            row[f'lo_{H}'] = s.min() if len(s) else np.nan
            if H == 1800 and len(s):
                bb = b[i0:i0 + len(s)]
                row['t_hi_1800'] = bb[s.argmax()] - t0; row['t_lo_1800'] = bb[s.argmin()] - t0
        out.append(row)
    return pd.DataFrame(out)


MOVERS_SQL = """
with m as (
  select ticker, case when bucket < {RTH0} then 'pre' else 'post' end as sess, bucket // 60 * 60 as m,
         sum(vwap * volume) / nullif(sum(volume), 0) as px, sum(vwap * volume) as dv
  from read_parquet('{fD}') where bucket < {RTH0} or bucket >= {RTH1}
  group by all
), ref as (
  select ticker, 'pre' as sess, arg_max(vwap, bucket) as ref from read_parquet('{fP}') where bucket < {RTH1} group by 1
  union all
  select ticker, 'post', arg_max(vwap, bucket) from read_parquet('{fD}') where bucket >= {RTH0} and bucket < {RTH1} group by 1
), j as (select m.*, m.px / ref.ref - 1 as r from m join ref using (ticker, sess) where m.px > 0)
select ticker, sess, count(*) as n_min, sum(dv) as dv, min(m) as first_min, arg_min(r, m) as r_first,
       max(r) as r_max, arg_max(m, r) as t_max, min(r) as r_min, arg_min(m, r) as t_min,
       arg_max(m, dv) as t_peak_dv, max(dv) as peak_dv,
       min(case when r >= 0.05 then m end) as t_up5,  min(case when r >= 0.10 then m end) as t_up10,
       min(case when r >= 0.20 then m end) as t_up20, min(case when r <= -0.05 then m end) as t_dn5,
       min(case when r <= -0.10 then m end) as t_dn10, min(case when r <= -0.20 then m end) as t_dn20
from j group by 1, 2
having (max(r) >= 0.05 or min(r) <= -0.05) and sum(dv) >= 10000
"""

STATS_SQL = """
select ticker, arg_min(vwap, bucket) as first_px,
       arg_min(vwap, bucket) filter (where bucket >= {RTH0} and bucket < {RTH1}) as open_px,
       arg_max(vwap, bucket) filter (where bucket >= {RTH0} and bucket < {RTH1}) as close_px,
       arg_max(vwap, bucket) as last_px,
       coalesce(sum(vwap * volume) filter (where bucket < {RTH0}), 0) as dv_pre,
       coalesce(sum(vwap * volume) filter (where bucket >= {RTH0} and bucket < {RTH1}), 0) as dv_rth,
       coalesce(sum(vwap * volume) filter (where bucket >= {RTH1}), 0) as dv_post
from read_parquet('{fD}') group by 1
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--meta', default='data/news/edgar')
    ap.add_argument('--out', default='data/news/pilot')
    ap.add_argument('--from', dest='lo', required=True)
    ap.add_argument('--to', dest='hi', required=True)
    ap.add_argument('--memory', default='3GB')
    ap.add_argument('--placebo-lag', type=int, default=0,
                    help='CONTROL: measure day D\'s events on the tape of the trading day N days EARLIER (same ticker, same '
                         'clock time, no filing); writes events only. Run with its own --out.')
    a = ap.parse_args()
    days = sorted(os.path.basename(f)[:10] for f in glob.glob(f'{TAPE}/*.parquet'))
    prev = {d: days[i - 1] for i, d in enumerate(days) if i > 0}
    ev = load_events(a.meta, days)
    splits = pd.read_csv('data/splits.csv', usecols=['ticker', 'execution_date'])
    split_set = set(zip(splits.ticker, splits.execution_date))
    run = [d for d in days if a.lo <= d <= a.hi and d in prev]
    print(f'{len(ev)} (filing, ticker) events, {ev.accession.nunique()} filings; {len(run)} days {a.lo}..{a.hi}')
    sys.stdout.flush()
    for k in ('events', 'movers', 'stats'): os.makedirs(os.path.join(a.out, k), exist_ok=True)
    con = duckdb.connect(); con.execute(f"set memory_limit='{a.memory}'; set threads=4")
    t_start, done = time.time(), 0
    pos = {d: i for i, d in enumerate(days)}
    for d in run:
        fe = os.path.join(a.out, 'events', f'{d}.parquet')
        if os.path.exists(fe): continue
        tape_d = days[pos[d] - a.placebo_lag] if pos[d] - a.placebo_lag >= 1 else None
        if tape_d is None: continue
        fD, fP = f'{TAPE}/{tape_d}.parquet', f'{TAPE}/{prev[tape_d]}.parquet'
        mv = []
        if not a.placebo_lag:
            st = con.execute(STATS_SQL.format(fD=fD, RTH0=RTH0, RTH1=RTH1)).df()
            st.to_parquet(os.path.join(a.out, 'stats', f'{d}.parquet'), index=False)
            mv = con.execute(MOVERS_SQL.format(fD=fD, fP=fP, RTH0=RTH0, RTH1=RTH1)).df()
            mv['day'] = d
            mv['split_today'] = [(t, d) in split_set for t in mv.ticker]
            mv.to_parquet(os.path.join(a.out, 'movers', f'{d}.parquet'), index=False)

        ev_d = ev[ev.day == d]
        w = ev_d.groupby('ticker').t0.agg(['min', 'max']).reset_index()
        w['lo'] = w['min'] - 3600; w['hi'] = w['max'] + 3600
        con.register('w', w[['ticker', 'lo', 'hi']])
        bars = con.execute(f"""
            select b.ticker, b.bucket - {RTH1} as bucket, b.vwap, b.volume from read_parquet('{fP}') b join w using (ticker)
              where w.lo < {PRE0} and b.bucket >= {RTH1}
            union all
            select b.ticker, b.bucket, b.vwap, b.volume from read_parquet('{fD}') b join w using (ticker)
              where b.bucket between w.lo and w.hi
            -- ⚠ zero-volume ANCHORS for "the last price before t0+h": the window starts at t0 − 1 h, so the last print
            -- before it must be added explicitly (2026-11-19: without them p_m3600 was empty for 90% of events).
            union all
            select b.ticker, max(b.bucket), arg_max(b.vwap, b.bucket), 0 from read_parquet('{fD}') b join w using (ticker)
              where b.bucket < w.lo group by 1
            union all   -- D−1's last print, on the session-time axis (its after-hours map to [0, 4 h), earlier = −1)
            select b.ticker, greatest(max(b.bucket) - {RTH1}, -1), arg_max(b.vwap, b.bucket), 0
              from read_parquet('{fP}') b join w using (ticker) group by 1
            order by 1, 2""").df()
        con.unregister('w')
        feat = event_features(ev_d, bars)
        # feat is row-aligned with ev_d (one row per event, same order) — concat, not merge: an empty day has no
        # columns to merge on (2025-12-26), and one ticker under two CIKs of one filing would fan a merge out
        res = pd.concat([ev_d.drop(columns=['acceptance_utc']).reset_index(drop=True),
                         feat.drop(columns=['accession', 'ticker'], errors='ignore')], axis=1)
        if 'n_bars' not in res: res['n_bars'] = 0
        res['split_today'] = [(t, d) in split_set or (t, tape_d) in split_set for t in res.ticker]
        res['tape_day'] = tape_d
        res.to_parquet(fe + '.tmp', index=False); os.replace(fe + '.tmp', fe)
        done += 1
        el = time.time() - t_start
        print(f'{d}: {len(res)} events ({int((res.n_bars > 0).sum())} with tape), {len(mv)} movers, '
              f'{len(bars)} bars | {done}/{len(run)} days, {el / 60:.1f} min, ETA {el / done * (len(run) - done) / 60:.0f} min')
        sys.stdout.flush()
    print('done')


if __name__ == '__main__':
    main()
