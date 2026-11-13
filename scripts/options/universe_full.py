#!/usr/bin/env python3
"""The point-in-time options universe, 2012-06 -> 2026-11, from our own ThetaData EOD pull (user 2026-11-13: survey
the three VV systems over the whole dataset; today's 530 names would be survivorship-biased).

Same rule as SH-5 (universe_screen.py): an underlying is IN on day D when its option volume (every expiry, every
standard root) averaged >= 25,000 contracts a day over the 20 trading days BEFORE D (a day without contracts = 0), and
it is a single stock (type CS or ADRC). Adjusted roots (a digit: CMCS1, WOLF1, VXX2) are dropped; BRKB -> BRK.B.

TICKER REUSE: a root's dates are cut into EPISODES at a > 45-calendar-day gap (the daily_episodes convention: a
recycled symbol cannot contaminate its successor). trading.db ticker_reference holds today's entity per ticker (FB =
an ETF today, Facebook 2012-22), so the type AND the CIK of every (root, episode) that ever passes the volume bar come
from MASSIVE's reference AS OF a date inside the episode (/v3/reference/tickers/X?date=D; delisted tickers 404 there,
then the ticker search with active=false, choosing the entity delisted on/after D). Cached in
data/options/root_pit_ref.tsv (rerun = no requests).
    cd research && python3 scripts/options/universe_full.py > logs/options_universe_full.log 2>&1
writes data/options/universe_daily_full.parquet
  (date, root, underlying, episode, volume, avg20_prior, type, cik, name, in_universe)"""
import concurrent.futures as cf, json, os, time, urllib.error, urllib.request
import numpy as np, pandas as pd

MIN, WIN, GAP = 25_000, 20, 45
SPECIAL = {'BRKB': 'BRK.B', 'BFB': 'BF.B'}
CACHE = 'data/options/root_pit_ref.tsv'
t0 = time.time()
rv = pd.read_parquet('data/options/root_volume_full.parquet', columns=['root', 'date', 'volume'])
rv['date'] = pd.to_datetime(rv.date)
adj = rv.root.str.contains(r'\d')
print(f'{rv.root.nunique():,} roots; adjusted roots dropped: {rv[adj].root.nunique()}', flush=True)
rv = rv[~adj].sort_values(['root', 'date'])
# the trading-day calendar: drop the holidays where a handful of roots carry stray rows (4-8 roots vs ~6,000)
cnt = rv.groupby('date').root.nunique()
cnt = cnt[cnt >= 0.5 * cnt.rolling(21, center=True, min_periods=5).median()]
days = np.array(cnt.index)
rv = rv[rv.date.isin(days)]
# ⚠ KNOWN VENDOR HOLE (checked 2026-11-13 on the Terminal: EOD, quote, trade and OI all 472): 186 roots, almost all
# L* / M* (LLY, MCD, MA, LOW, LEN ...), have NO data 2019-12-12 -> 2020-03-06. A gap that spans it is not a break.
HOLE = (pd.Timestamp('2019-12-12'), pd.Timestamp('2020-03-06'))


def gap_days(prev, cur):
    g = (cur - prev).dt.days
    ov = (cur.clip(upper=HOLE[1] + pd.Timedelta(days=1)) - prev.clip(lower=HOLE[0] - pd.Timedelta(days=1))).dt.days
    return g - ov.clip(lower=0)

out = []
for root, g in rv.groupby('root', sort=False):
    if g.volume.max() < MIN:                     # can never average 25k
        continue
    ep = (gap_days(g.date.shift(1), g.date) > GAP).cumsum().to_numpy()
    for e in np.unique(ep):
        ge = g[ep == e]
        d = days[(days >= ge.date.iloc[0]) & (days <= ge.date.iloc[-1])]
        # a listed root has an EOD row for every listed contract every day, zero volume or not: a MISSING day is a data
        # hole (vendor / halt), so it is unknown, not 0 contracts; the average runs over the days that have data
        v = ge.set_index('date').volume.reindex(d).astype(float)
        a20 = v.shift(1).rolling(WIN, min_periods=WIN // 2).mean()
        v = v.fillna(0)
        keep = a20 >= MIN * 0.5
        if keep.any():
            out.append(pd.DataFrame({'date': d[keep.to_numpy()], 'root': root, 'episode': int(e),
                                     'volume': v[keep].to_numpy(), 'avg20_prior': a20[keep].to_numpy()}))
u = pd.concat(out, ignore_index=True)
u['underlying'] = u.root.map(lambda r: SPECIAL.get(r, r))
print(f'screened {time.time() - t0:.0f}s: {len(u):,} rows >= half the bar, {u.root.nunique()} roots', flush=True)

# point-in-time reference per (root, episode) that passes the full bar: the median passing date
p = u[u.avg20_prior >= MIN]
q = p.groupby(['underlying', 'episode']).date.apply(lambda s: s.iloc[len(s) // 2]).reset_index()
key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']


def get(url):
    for att in range(4):
        try:
            with urllib.request.urlopen(url + f'&apiKey={key}', timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            time.sleep(2 * (att + 1))
        except Exception:
            time.sleep(2 * (att + 1))
    return None


def lookup(t, d):
    ds = d.strftime('%Y-%m-%d')
    j = get(f'https://api.massive.com/v3/reference/tickers/{t}?date={ds}')
    if j and j.get('results'):
        x = j['results']; how = 'pit'
    else:
        res = []
        for act in ('false', 'true'):
            jj = get(f'https://api.massive.com/v3/reference/tickers?ticker={t}&active={act}&limit=100')
            res += (jj or {}).get('results', [])
        if not res:
            return dict(type='?', cik=None, name=None, how='none')
        def dl(x):
            s = x.get('delisted_utc')
            return pd.Timestamp(s).tz_localize(None) if s else pd.Timestamp('2100-01-01')
        after = [x for x in res if dl(x) >= d]
        x = min(after, key=dl) if after else max(res, key=dl); how = 'search'
    return dict(type=x.get('type', '?'), cik=x.get('cik'), name=x.get('name'), how=how)


cache = pd.read_csv(CACHE, sep='\t', dtype={'cik': str}) if os.path.exists(CACHE) else pd.DataFrame(
    columns=['underlying', 'episode', 'qdate', 'type', 'cik', 'name', 'how'])
done = set(zip(cache.underlying, cache.episode))
todo = [(r.underlying, r.episode, r.date) for r in q.itertuples() if (r.underlying, r.episode) not in done]
print(f'{len(q)} (underlying, episode) pass the bar; {len(todo)} to look up', flush=True)
rows = []
with cf.ThreadPoolExecutor(8) as ex:
    for (t, e, d), res in zip(todo, ex.map(lambda x: lookup(x[0], x[2]), todo)):
        rows.append(dict(underlying=t, episode=e, qdate=d.date(), **res))
if rows:
    cache = pd.concat([cache, pd.DataFrame(rows)], ignore_index=True)
    cache.to_csv(CACHE, sep='\t', index=False)
cache = cache.merge(q[['underlying', 'episode']], on=['underlying', 'episode'])      # only the current episodes
print(f'lookups done {time.time() - t0:.0f}s; how: {cache.how.value_counts().to_dict()}', flush=True)

# Massive's OLD records often carry no type (YHOO, DELL 2013, BRCM): with a CIK they are companies -> by the name:
# funds out, ADRs = ADRC, else CS. Without a CIK (indices, test symbols) they stay out.
fund = cache.name.fillna('').str.upper().str.contains(r'\bETF\b|\bETN\b|FUND|PROSHARES|ISHARES|SPDR|DIREXION|IPATH|VELOCITYSHARES|\bTRUST\b')
adr = cache.name.fillna('').str.upper().str.contains(r'\bADR|ADS\b|DEPOSITARY')
fix = (cache.type.fillna('?') == '?') & cache.cik.notna()
cache['type_src'] = np.where(fix, 'name', 'massive')
cache.loc[fix, 'type'] = np.where(fund[fix], 'FUND?', np.where(adr[fix], 'ADRC', 'CS'))
print('typed by name:', ' '.join(f'{r.underlying}:{r.type}' for r in cache[fix].itertuples()))
u = u.merge(cache[['underlying', 'episode', 'type', 'cik', 'name']], on=['underlying', 'episode'], how='left')
u['type'] = u.type.fillna('?')
u['in_universe'] = (u.avg20_prior >= MIN) & u.type.isin(['CS', 'ADRC'])
u.to_parquet('data/options/universe_daily_full.parquet', index=False)

print('\ntypes of the passing (underlying, episode):', cache.type.value_counts().to_dict())
unk = cache[~cache.type.isin(['CS', 'ADRC', 'ETF', 'ETN', 'ETV', 'ETS', 'FUND'])]
print(f'not a stock / fund type ({len(unk)}):', ' '.join(f'{r.underlying}:{r.type}' for r in unk.itertuples()))
re = cache[cache.duplicated('underlying', keep=False)].sort_values(['underlying', 'episode'])
print(f'\nreused / multi-episode tickers ({re.underlying.nunique()}):')
print(re[['underlying', 'episode', 'qdate', 'type', 'cik', 'name', 'how']].to_string(index=False))
in_ = u[u.in_universe]
pd_ = in_.groupby('date').size()
print(f'\nUNIVERSE: {in_.underlying.nunique()} names ever; per day min {pd_.min()} median {pd_.median():.0f} max {pd_.max()}')
by = in_.assign(y=in_.date.dt.year).groupby('y').agg(names=('underlying', 'nunique'))
by['per_day'] = in_.assign(y=in_.date.dt.year).groupby(['y', 'date']).size().groupby('y').median()
print(by.to_string())
print(f'wrote {len(u):,} rows  {time.time() - t0:.0f}s')
