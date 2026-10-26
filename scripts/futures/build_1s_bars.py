"""The futures 1-second bars, in the EQUITY corpus's shape (data/intraday_1s_slim) plus the volume delta (2026-10-26).

Input: /mnt/d/trading-edge-bulk/futures/level1_tick/<ROOT>/<year>_<CONTRACT>.parquet, Sierra Chart's trade-by-trade
tape, one file per contract, NOT rolled. Each row is one FILL: trade_price / bid / ask × 1e9 from a float32 (87.9 is
stored as 87900001526, so prices are rounded here to the root's GRID); trade_size; trade_side (+1 = the buyer
aggressed, −1 = the seller; never 0); the bid / ask just before the fill (no sizes); a µs UTC timestamp. Before
2020-08-21 that timestamp is whole seconds plus a row counter, and real ms only from 2020-08-23
(docs/futures_data.md), so a second is the finest real unit in both eras. From 2015-01-01 only (user 2026-10-26: the
quotes start mid-2014). ⚠ Futures prices can be NEGATIVE: CLK20 traded down to −40.32 on 2020-04-20/21 (5,481 fills),
which a `trade_price > 0` filter silently dropped from the first build. Only a null price is dropped.

Output: data/futures/intraday_1s/YYYY-MM-DD.parquet, one file per ET calendar date, every contract, sorted by
(ticker, bucket):
  ticker       VARCHAR  the contract (NQZ25)
  bucket       INTEGER  seconds since midnight ET (the equity corpus's clock; Globex is shut over every DST change,
                        02:00 Sunday, so no second repeats)
  vwap         FLOAT    Σ px·size / Σ size, px on the grid
  volume       FLOAT    contracts
  trade_count  INTEGER  fills
  delta        FLOAT    buy − sell volume. A CROSS fill (bid = ask: the open's match after the daily break, which Sierra
                        labels with one side although an auction has no aggressor) counts half buy and half sell, so it
                        adds 0 (user 2026-10-26)
and data/futures/intraday_1s_manifest.parquet (BESIDE the corpus, so a `*.parquet` glob over it reads only bars), one row per contract: the rows in / kept, the bars, the first / last
second, the share of fills with a quote (bid < ask), the cross share of the volume, the measured tick, and the worst
rounding residual in grid units. A grid coarser than the real tick shows up as ~0.5, and the run fails above 0.05.

Two passes:
  1. per contract → data/futures/.contracts/<ROOT>/<CONTRACT>.parquet. ONE copier reads the HDD sequentially into a
     stage dir on the SSD (DuckDB reads the 9P mount at ~40 MB/s, a sequential copy at ~140: build_all_1s_bars.fsx's
     --stage-dir), and DuckDB workers convert the staged copies. A finished contract is skipped on a rerun.
  2. by date: the contracts are split into the per-date files, each sorted, and the per-contract files deleted. Pass 2
     always rebuilds the WHOLE corpus, so adding contracts later (ES, MNQ) means rerunning pass 1 for everything.
A source without its parquet footer (a truncated transfer: ZTU26 2026-10-26) is skipped and named.

  cd research && python -u scripts/futures/build_1s_bars.py > logs/futures_intraday_1s.log 2>&1; echo exit $?
"""
import argparse
import glob
import os
import queue
import shutil
import threading
import time

import duckdb
import pandas as pd

# The rounding GRID per root (price units as quoted). It may be FINER than the real tick (a divisor of it): the manifest's
# measured tick says what the market used. ZN / ZB / ZT are fractions of 1/32 (ZN 1/64, ZB 1/32, ZT 1/256 today).
# ⚠ Prices are on SIERRA's scale, not always CME's: 6J is stored as 0.6916 = 100 × CME's 0.006916 (tick 0.00005, CME's
# 0.0000005). A grid finer than float32's precision at the price (6J at 1e-7: residual 0.5) fails the residual check.
GRID = {
    'MES': 0.25, 'NQ': 0.25,
    'CL': 0.01, 'MCL': 0.01, 'NG': 0.001, 'RB': 0.0001,
    'HG': 0.0005, 'MHG': 0.0005, 'MGC': 0.1,
    'HE': 0.025, 'ZC': 0.25, 'ZS': 0.25,
    'ZN': 1 / 128, 'ZB': 1 / 64, 'ZT': 1 / 512, 'SR3': 0.0025,
    '6A': 0.00001, '6B': 0.00001, '6C': 0.00001, '6E': 0.00001, '6N': 0.00001, '6S': 0.00001,
    '6J': 0.000005, '6Z': 0.0000005,
}
FROM_US = 1_420_070_400_000_000          # 2015-01-01 00:00 UTC in µs
MAX_RESIDUAL = 0.05                       # grid units: a wrong grid gives ~0.5; float32 noise at 1e-5 FX grids ~0.02, and
                                          # ZTM17 holds 2,497 prints at 107.8438034 (0.027 off 1/512: a feed artifact)


def parse():
    p = argparse.ArgumentParser()
    p.add_argument('--src', default='/mnt/d/trading-edge-bulk/futures/level1_tick')
    p.add_argument('--out', default='data/futures/intraday_1s')
    p.add_argument('--contracts-dir', default='data/futures/.contracts')
    p.add_argument('--stage-dir', default='data/futures/.stage')
    p.add_argument('--workers', type=int, default=3)
    p.add_argument('--threads', type=int, default=4, help='DuckDB threads per worker')
    p.add_argument('--memory-limit', default='4GB', help='DuckDB memory per worker')
    return p.parse_args()


def has_footer(path: str) -> bool:
    with open(path, 'rb') as f:
        f.seek(-4, os.SEEK_END)
        return f.read(4) == b'PAR1'


def copy_staged(src: str, dst: str):
    """one sequential copy in 4 MB blocks, retried (the 9P mount's ENOMEM on big reads, 2026-10-24)"""
    for attempt in range(5):
        try:
            with open(src, 'rb') as fi, open(dst + '.part', 'wb') as fo:
                shutil.copyfileobj(fi, fo, 4 << 20)
            os.replace(dst + '.part', dst)
            return
        except OSError:
            if attempt == 4:
                raise
            time.sleep(2 ** attempt)


def connect(threads: int, memory_limit: str, temp_dir: str):
    con = duckdb.connect()
    # an in-memory DuckDB spills only with a temp_directory (40 MES / NQ files ran out of memory without one) — ONE PER
    # CONNECTION: DuckDB names its spill files the same in every connection, and a shared dir corrupted 33 builds
    con.execute(f"set threads = {threads}; set memory_limit = '{memory_limit}'; set enable_progress_bar = false; "
                f"set preserve_insertion_order = false; set temp_directory = '{temp_dir}'")
    return con


def convert(con, root: str, contract: str, staged: str, out_path: str) -> dict:
    g = round(GRID[root] * 1e9)          # the grid in the file's ×1e9 units (an integer for every root)
    con.execute(f"""
        create or replace temp view t as
        select timestamp ts, trade_side side, trade_size sz,
               round(trade_price / {g}) * {g} / 1e9 px,
               abs(trade_price - round(trade_price / {g}) * {g}) / {g} resid,
               bid_price = ask_price cross_fill, bid_price < ask_price quoted
        from read_parquet('{staged}')
        where event_type = 3 and trade_size > 0 and trade_price is not null and timestamp >= {FROM_US}""")
    tmp = out_path + '.tmp'
    con.execute(f"""
        copy (
          select (et::date)::VARCHAR date, '{contract}' ticker,
                 (extract(epoch from et::time))::INTEGER bucket,
                 (sum(px * sz) / sum(sz))::FLOAT vwap,
                 sum(sz)::FLOAT volume,
                 count(*)::INTEGER trade_count,
                 sum(case when cross_fill then 0 else side * sz end)::FLOAT delta
          from (select *, to_timestamp(ts // 1000000) at time zone 'America/New_York' et from t)
          group by et
        ) to '{tmp}' (format parquet, compression zstd)""")
    os.replace(tmp, out_path)
    st = con.execute("""
        select count(*), max(resid), avg(quoted::int), sum(case when cross_fill then sz else 0 end) / sum(sz),
               min(ts) // 1000000, max(ts) // 1000000
        from t""").fetchone()
    tick = con.execute("""
        select min(d) from (select p - lag(p) over (order by p) d from (select distinct round(px * 1e9) p from t))
        where d > 0""").fetchone()[0]
    rows_in = con.execute(f"select count(*) from read_parquet('{staged}')").fetchone()[0]
    bars = con.execute(f"select count(*) from read_parquet('{out_path}')").fetchone()[0]
    return dict(root=root, contract=contract, rows_in=rows_in, rows_kept=st[0], bars=bars,
                first_sec=st[4], last_sec=st[5], quoted=st[2], cross_share=st[3],
                tick=None if tick is None else tick / 1e9, max_residual=st[1])


def pass1(a, jobs) -> tuple[list, list]:
    """per contract, staged; returns (results, failures)"""
    staged_q: queue.Queue = queue.Queue(maxsize=a.workers * 2)   # bounds the SSD stage
    results, failures = [], []
    lock = threading.Lock()
    t0 = time.time()

    def copier():
        for root, contract, f, out_path in jobs:
            dst = f'{a.stage_dir}/{root}_{contract}.parquet'
            try:
                copy_staged(f, dst)
                staged_q.put((root, contract, dst, out_path))
            except Exception as e:
                with lock:
                    failures.append((contract, f'copy: {e}'))
                    print(f'FAILED {contract}: copy: {e}', flush=True)
        for _ in range(a.workers):
            staged_q.put(None)

    def worker(wid: int):
        con = connect(a.threads, a.memory_limit, f'{a.stage_dir}/duckdb_tmp_{wid}')
        while (job := staged_q.get()) is not None:
            root, contract, staged, out_path = job
            try:
                r = convert(con, root, contract, staged, out_path)
                with lock:
                    results.append(r)
                    print(f'{len(results):5}/{len(jobs)} {time.time() - t0:7.1f}s  {contract:8} rows {r["rows_kept"]:>11,} '
                          f'bars {r["bars"]:>10,}  tick {r["tick"]}  resid {r["max_residual"] or 0:.4f}  '
                          f'quoted {r["quoted"] or 0:.3f}  cross {r["cross_share"] or 0:.5f}', flush=True)
            except Exception as e:
                if os.path.exists(out_path + '.tmp'):
                    os.remove(out_path + '.tmp')
                with lock:
                    failures.append((contract, str(e)))
                    print(f'FAILED {contract}: {e}', flush=True)
            finally:
                os.remove(staged)

    threads = [threading.Thread(target=copier)] + [threading.Thread(target=worker, args=(i,)) for i in range(a.workers)]
    for th in threads:
        th.start()
    for th in threads:
        th.join()
    print(f'pass 1: {len(results)} contracts in {time.time() - t0:.0f}s, failures {len(failures)}', flush=True)
    return results, failures


def pass2(a):
    """by ET date: partition every contract file by date, then sort each date's file by (ticker, bucket)"""
    t0 = time.time()
    part = f'{a.stage_dir}/by_date'
    shutil.rmtree(part, ignore_errors=True)
    con = connect(a.workers * a.threads, '10GB', f'{a.stage_dir}/duckdb_tmp_p2')
    con.execute(f"""
        copy (select * from read_parquet('{a.contracts_dir}/*/*.parquet'))
        to '{part}' (format parquet, partition_by (date), compression zstd)""")
    dates = sorted(d.split('=', 1)[1] for d in os.listdir(part))
    os.makedirs(a.out, exist_ok=True)
    for old in glob.glob(f'{a.out}/????-??-??.parquet'):     # a rerun replaces the whole corpus
        os.remove(old)
    rows = 0
    for d in dates:
        out_path = f'{a.out}/{d}.parquet'
        con.execute(f"""
            copy (select ticker, bucket, vwap, volume, trade_count, delta from read_parquet('{part}/date={d}/*.parquet')
                  order by ticker, bucket)
            to '{out_path}.tmp' (format parquet, compression zstd)""")
        os.replace(out_path + '.tmp', out_path)
        rows += con.execute(f"select count(*) from read_parquet('{out_path}')").fetchone()[0]
    shutil.rmtree(part)
    print(f'pass 2: {len(dates)} dates, {rows:,} bars in {time.time() - t0:.0f}s', flush=True)
    return rows


def main():
    a = parse()
    os.makedirs(a.stage_dir, exist_ok=True)
    os.makedirs(a.out, exist_ok=True)
    files = sorted(glob.glob(f'{a.src}/*/*.parquet'))
    jobs, truncated = [], []
    for f in files:
        root = os.path.basename(os.path.dirname(f))
        year, contract = os.path.basename(f)[:-len('.parquet')].split('_', 1)
        if int(year) < 2015:             # the file's year is the contract's expiry: nothing from 2015 on
            continue
        if root not in GRID:
            raise SystemExit(f'no grid for root {root}')
        if not has_footer(f):
            truncated.append(f)
            continue
        out_path = f'{a.contracts_dir}/{root}/{contract}.parquet'
        if os.path.exists(out_path):
            continue
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        jobs.append((root, contract, f, out_path))
    print(f'files {len(files)}  to do {len(jobs)}  workers {a.workers}  truncated (skipped): {truncated}', flush=True)

    results, failures = pass1(a, jobs)
    man_path = f'{a.out.rstrip("/")}_manifest.parquet'
    new = pd.DataFrame(results)
    man = pd.concat([pd.read_parquet(man_path), new]) if os.path.exists(man_path) and len(new) else new
    if len(man):
        man = man.drop_duplicates('contract', keep='last').sort_values(['root', 'first_sec'])
        man.to_parquet(man_path, index=False)
    bad = man[man.max_residual > MAX_RESIDUAL] if len(man) else man
    for c, e in failures:
        print('  FAILED', c, e)
    if len(bad):
        print(f'residual > {MAX_RESIDUAL}:')
        print(bad[['contract', 'max_residual', 'tick']].to_string(index=False))
    if failures or len(bad):
        raise SystemExit(1)

    bars = pass2(a)
    if bars != int(man.bars.sum()):
        raise SystemExit(f'pass 2 wrote {bars:,} bars, the contracts hold {int(man.bars.sum()):,}')
    shutil.rmtree(a.contracts_dir)
    for d in glob.glob(f'{a.stage_dir}/duckdb_tmp_*'):
        shutil.rmtree(d, ignore_errors=True)
    print('done', flush=True)


if __name__ == '__main__':
    main()
