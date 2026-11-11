#!/usr/bin/env python3
"""StraddleHarvester — download Massive's whole-market OPTIONS day aggregates (one csv.gz per day, every OPRA contract:
volume, OHLC, VWAP-less, transactions; NO bid/ask). Key: ~/.config/massive-options/credentials (the options plan,
user 2026-11-10). The plan is a ROLLING 4-year window: measured 2026-11-10, the first allowed day = 2022-11-09
(minute aggs and trades too; quotes are not on the plan).
Use: the universe screen (contracts per root per day) for 2022-11-09 →; vs ThetaData's EOD on 2026-10-15 the root
totals are ~1% lower (91% of roots identical; Massive drops some trade conditions) — immaterial at a 25k threshold.

    cd research && python3 scripts/options/massive_options_day_aggs.py --start 2022-11-09 \
        > logs/options_massive_day_aggs.log 2>&1; echo "exit $?"
"""
import argparse, concurrent.futures as cf, json, os, sys, time
import boto3
from botocore.config import Config

ap = argparse.ArgumentParser()
ap.add_argument('--start', default='2022-11-09'); ap.add_argument('--end', default='2099-12-31')
ap.add_argument('--out', default='data/options/massive_day_aggs')
ap.add_argument('--workers', type=int, default=4)
a = ap.parse_args()
k = json.load(open(os.path.expanduser('~/.config/massive-options/credentials')))
s3 = boto3.client('s3', endpoint_url='https://files.massive.com', aws_access_key_id=k['massive_s3_access_key'],
                  aws_secret_access_key=k['massive_s3_secret_key'], config=Config(signature_version='s3v4'))
keys = []
for pg in s3.get_paginator('list_objects_v2').paginate(Bucket='flatfiles', Prefix='us_options_opra/day_aggs_v1/'):
    keys += [o['Key'] for o in pg.get('Contents', []) if a.start <= os.path.basename(o['Key'])[:10] <= a.end]
os.makedirs(a.out, exist_ok=True)
todo = [x for x in sorted(keys) if not os.path.exists(os.path.join(a.out, os.path.basename(x)))]
print(f"{len(keys)} days {a.start} → {a.end}, {len(todo)} to download -> {a.out}", flush=True)


def fetch(key):
    dst = os.path.join(a.out, os.path.basename(key))
    try:
        body = s3.get_object(Bucket='flatfiles', Key=key)['Body'].read()
    except Exception as e:
        return key, str(e)[:120]
    with open(dst + '.part', 'wb') as f:
        f.write(body)
    os.replace(dst + '.part', dst)
    return key, None


t0, bad = time.time(), 0
with cf.ThreadPoolExecutor(a.workers) as ex:
    for n, (key, err) in enumerate(ex.map(fetch, todo), 1):
        if err:
            bad += 1; print(f"  ! {key}: {err}", flush=True)
        if n % 100 == 0 or n == len(todo):
            print(f"{n}/{len(todo)}  errors {bad}  {(time.time() - t0) / 60:.1f} min", flush=True)
print(f"DONE {len(todo)} files, {bad} errors", flush=True)
sys.exit(1 if bad else 0)
