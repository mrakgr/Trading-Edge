#!/usr/bin/env python3
"""Item 2.02 8-Ks for the point-in-time options universe, 2011 -> (user 2026-11-13: survey the systems over the whole
2012-2026 dataset; the md500k EDGAR file starts 2015-06). Input for earnings_scheduled.py (same columns as
edgar_earnings.py's output).

Ticker -> CIK = Massive's reference AS OF a date inside each (underlying, episode) (universe_full.py ->
data/options/root_pit_ref.tsv). A reused ticker's episodes split the timeline at the midpoint of the gap between them,
so each CIK's filings are labelled with the ticker only while it held it (DELL 2013 = Dell Inc, 2025 = Dell
Technologies). Filings: data/sec/filings_2011.parquet (sec_bulk.py --since 2011-01-01).
    cd research && python3 scripts/options/earnings_full.py > logs/options_earnings_full.log 2>&1
    python3 scripts/options/earnings_scheduled.py --earnings data/options/earnings_full_8k.parquet --spans none \
        --bulk data/sec/filings_2011.parquet --out data/options/earnings_sched_full.parquet
"""
import numpy as np, pandas as pd

u = pd.read_parquet('data/options/universe_daily_full.parquet')
ref = pd.read_csv('data/options/root_pit_ref.tsv', sep='\t', dtype={'cik': str})
in_ = u[u.in_universe].groupby(['underlying', 'episode']).date.agg(['min', 'max']).reset_index()
in_ = in_.merge(ref[['underlying', 'episode', 'cik']], on=['underlying', 'episode'])
in_ = in_[in_.cik.notna()].copy()
in_['cik'] = in_.cik.astype(float).astype(int)
# Massive's CIK is wrong / dead for a few US filers (0 filings under it): fixed by the SEC's company names incl. former
# names among 10-Q/10-K filers (2026-11-13). The rest without Item 2.02 are 6-K/40-F foreign filers, SPACs, FDIC banks.
CIK_FIX = {'CHK': 895126, 'ANR': 1301063, 'AKS': 918160, 'AHT': 1232582, 'SYMC': 849399, 'VRX': 885590,
           'CLWR': 1442505, 'ILG': 1434620, 'DISCA': 1437107}
for t, c in CIK_FIX.items():
    in_.loc[in_.underlying == t, 'cik'] = c
# the span each (ticker, episode, cik) owns: from the midpoint of the gap to the previous episode to the next midpoint
in_ = in_.sort_values(['underlying', 'min'])
prev_end = in_.groupby('underlying')['max'].shift(1); next_start = in_.groupby('underlying')['min'].shift(-1)
in_['lo'] = (prev_end + (in_['min'] - prev_end) / 2).fillna(pd.Timestamp('2000-01-01'))
in_['hi'] = (in_['max'] + (next_start - in_['max']) / 2).fillna(pd.Timestamp('2100-01-01'))
print(f'{len(in_)} (underlying, episode) with a CIK; {in_.underlying.nunique()} underlyings; {in_.cik.nunique()} CIKs')
# a ticker handed from one CIK to another WITHOUT a gap (AA 2016-11: Alcoa Inc -> Alcoa Corp; the old one became
# Arconic) is no episode break: pead.py's cik_spans (md500k, 2015 ->) split the span at the switch date
sp = pd.read_parquet('data/options/pead_md500k/cik_spans.parquet')
rows = []
for r in in_.itertuples():
    s = sp[(sp.ticker == r.underlying) & (sp.switch > r.lo) & (sp.switch < r.hi)]
    if len(s) == 1 and r.cik in (s.cik_old.iloc[0], s.cik_new.iloc[0]):
        s = s.iloc[0]
        rows.append(r._replace(cik=int(s.cik_old), hi=s.switch)); rows.append(r._replace(cik=int(s.cik_new), lo=s.switch))
    else:
        rows.append(r)
n0 = len(in_); in_ = pd.DataFrame(rows).drop(columns='Index', errors='ignore'); print(f'cik_spans split {len(in_) - n0} spans')
f = pd.read_parquet('data/sec/filings_2011.parquet')
f = f[f.form.isin(['8-K', '8-K/A']) & f['items'].fillna('').str.split(',').apply(lambda x: '2.02' in x)]
e = in_[['underlying', 'cik', 'lo', 'hi']].merge(f, on='cik')
fd = pd.to_datetime(e.filing_date)
e = e[(fd >= e.lo) & (fd < e.hi)].rename(columns={'underlying': 'ticker'})
e['accept_et'] = pd.to_datetime(e.accept_utc, utc=True).dt.tz_convert('America/New_York').dt.tz_localize(None)
hm = e.accept_et.dt.hour * 60 + e.accept_et.dt.minute
e['session'] = pd.cut(hm, [-1, 9 * 60 + 29, 15 * 60 + 59, 24 * 60], labels=['BMO', 'RTH', 'AMC']).astype(str)
e['et_date'] = pd.to_datetime(e.accept_et.dt.date)
e = e.drop(columns=['lo', 'hi', 'report_date']).drop_duplicates(['ticker', 'accession']).sort_values(['ticker', 'accept_et'])
e.to_parquet('data/options/earnings_full_8k.parquet', index=False)
print(f'{len(e):,} Item 2.02 filings, {e.ticker.nunique()} tickers; by year:',
      e.et_date.dt.year.value_counts().sort_index().to_dict())
miss = sorted(set(in_.underlying) - set(e.ticker))
print(f'{len(miss)} underlyings without any Item 2.02 filing (foreign filers file 6-K):', ' '.join(miss[:80]))
