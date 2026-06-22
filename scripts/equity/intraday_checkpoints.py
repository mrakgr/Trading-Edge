#!/usr/bin/env python3
"""Stage 1 of the intraday-entry pipeline: reduce 25GB of minute bars to a compact
per-(ticker, day, checkpoint) snapshot table for the HighFlyer intraday-entry study.

The intraday-triggered signal (the only LIVE-TRADEABLE form) selects a name by its state
AS OF a morning checkpoint — never peeking at the full-day close. So for each (ticker, day)
and each checkpoint time T we capture, using ONLY bars at/before T (no lookahead):
  - cum_vol   : cumulative RTH volume from 09:30 through T
  - px        : last close at/before T (the price you could enter at)
  - hi_sofar  : session high through T   (how extended within the day)
  - lo_sofar  : session low through T
  - n_bars    : bars seen (sanity / liquidity)

Downstream (stage 2, separate) attaches: prior-day close (-> intraday move %), a same-time-of-day
historical volume baseline (-> intraday rvol), the pre-open-knowable daily filters (tightness,
ATR%, 52w proximity, dollar float, price), and the 5-day-forward exit. Then we sweep
checkpoint x move x rvol x float for the forward-PF surface.

Source: data/minute_aggs/{date}.parquet — bulk market-wide 1m bars (OHLCV+vol), window_start =
epoch NANOSECONDS UTC. Coverage 2021-06-17 .. present, incl. premarket. We keep only RTH
(>= 09:30 ET) bars up to each checkpoint.

Output: data/equity/intraday/checkpoints.parquet (one row per ticker/day/checkpoint).
Bounded universe: only tickers whose day had >= MIN_DAY_DOLLARVOL traded (keeps it to plausible
HighFlyer candidates; the full daily filter is applied later in SQL). Idempotent per-day via a
done-set; --force re-does all.

Run:  python scripts/equity/intraday_checkpoints.py            # full 2021-06-17..latest
      python scripts/equity/intraday_checkpoints.py --limit 5  # first 5 days (smoke test)
"""
import argparse
import glob
import os

import duckdb

MINUTE_DIR = "data/minute_aggs"
OUT_DIR = "data/equity/intraday"
OUT_PARQUET = f"{OUT_DIR}/checkpoints.parquet"
PARTS_DIR = f"{OUT_DIR}/parts"   # one parquet per day, globbed into the final table

# Checkpoint times, ET (minutes since midnight).
#   9:15  = PREMARKET (test acting before the open on premarket state)
#   9:30  = at the open (opening print only)
#   9:31  = directly after the open (first minute)
#   ...dense in the first 90 min (the entry zone)...
#   15:45 = the MOC cutoff (orders due by 15:50); 15:00/15:30 to see the run-in.
CHECKPOINTS_ET = [
    (9, 15), (9, 30), (9, 31), (9, 35), (9, 45), (10, 0), (10, 15), (10, 30),
    (11, 0), (11, 30), (12, 0), (14, 0), (15, 0), (15, 30), (15, 45),
]
RTH_OPEN_MIN = 9 * 60 + 30          # 09:30 ET
PREMKT_OPEN_MIN = 4 * 60            # 04:00 ET — earliest premarket bar
MIN_DAY_DOLLARVOL = 1_000_000.0     # keep only names with >= $1M traded that day (liquidity floor)


def checkpoint_label(h, m):
    return f"{h:02d}{m:02d}"


def day_from_path(p):
    return os.path.basename(p).replace(".parquet", "")


def build_day(con, date_str):
    """Extract all checkpoint snapshots for one day into a parts parquet.

    window_start is epoch-NANOSECONDS UTC. We derive ET minutes-since-midnight per bar, keep RTH
    bars (>= 09:30 ET), and for each checkpoint aggregate the bars with et_min <= checkpoint_min.
    A bar's volume is counted in cum_vol; px = the close of the latest bar <= checkpoint.
    """
    src = f"{MINUTE_DIR}/{date_str}.parquet"
    # Build the checkpoint list as a VALUES table for the cross join.
    cp_values = ",".join(
        f"({h*60+m}, '{checkpoint_label(h,m)}')" for h, m in CHECKPOINTS_ET
    )
    out = f"{PARTS_DIR}/{date_str}.parquet"
    con.execute(f"""
        COPY (
            WITH bars AS (
                SELECT
                    ticker,
                    -- epoch ns UTC -> ET minutes since midnight
                    CAST(date_part('hour', to_timestamp(window_start/1e9)
                            AT TIME ZONE 'America/New_York') AS INT) * 60
                      + CAST(date_part('minute', to_timestamp(window_start/1e9)
                            AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                    open, high, low, close, volume
                FROM read_parquet('{src}')
                WHERE close > 0
            ),
            sess AS (  -- premarket (04:00) through the close (16:00); covers the 9:15 checkpoint
                SELECT * FROM bars WHERE et_min >= {PREMKT_OPEN_MIN} AND et_min < 16*60
            ),
            -- liquidity floor: keep only tickers with >= $1M traded in RTH that day
            liquid AS (
                SELECT ticker FROM sess
                WHERE et_min >= {RTH_OPEN_MIN}
                GROUP BY ticker
                HAVING SUM(close*volume) >= {MIN_DAY_DOLLARVOL}
            ),
            cps(cp_min, cp_label) AS (VALUES {cp_values}),
            joined AS (
                SELECT s.ticker, c.cp_label, c.cp_min, s.et_min,
                       s.close, s.high, s.low, s.volume
                FROM sess s
                JOIN liquid l ON l.ticker = s.ticker
                CROSS JOIN cps c
                WHERE s.et_min <= c.cp_min          -- ONLY bars at/before the checkpoint
            )
            SELECT
                DATE '{date_str}' AS date,
                ticker,
                cp_label AS checkpoint,
                -- cum_vol = session-to-date (incl. premarket); rth_vol = from the 09:30 open only.
                SUM(volume)                                          AS cum_vol,
                SUM(CASE WHEN et_min >= {RTH_OPEN_MIN} THEN volume ELSE 0 END) AS rth_vol,
                MAX(high)  AS hi_sofar,
                MIN(low)   AS lo_sofar,
                COUNT(*)   AS n_bars,
                -- px = close of the latest bar <= checkpoint (the enterable price)
                arg_max(close, et_min) AS px
            FROM joined
            GROUP BY ticker, cp_label
        ) TO '{out}' (FORMAT parquet);
    """)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=None, help="process only the first N days")
    ap.add_argument("--force", action="store_true", help="rebuild days even if a part exists")
    ap.add_argument("--threads", type=int, default=8)
    args = ap.parse_args()

    os.makedirs(PARTS_DIR, exist_ok=True)
    days = sorted(day_from_path(p) for p in glob.glob(f"{MINUTE_DIR}/*.parquet"))
    if args.limit:
        days = days[:args.limit]

    con = duckdb.connect()
    con.execute(f"PRAGMA threads={args.threads}")

    done = set() if args.force else {
        day_from_path(p) for p in glob.glob(f"{PARTS_DIR}/*.parquet")
    }
    todo = [d for d in days if d not in done]
    print(f"{len(days)} days in range | {len(done)} already done | {len(todo)} to build")

    for i, d in enumerate(todo, 1):
        try:
            build_day(con, d)
        except Exception as e:
            print(f"  [{i}/{len(todo)}] {d} ERROR: {type(e).__name__}: {str(e)[:120]}")
            continue
        if i % 50 == 0 or i == len(todo):
            print(f"  [{i}/{len(todo)}] {d} done", flush=True)

    # Glob all parts into the final compact table.
    print("consolidating parts -> checkpoints.parquet ...")
    con.execute(f"""
        COPY (SELECT * FROM read_parquet('{PARTS_DIR}/*.parquet') ORDER BY date, ticker, checkpoint)
        TO '{OUT_PARQUET}' (FORMAT parquet);
    """)
    s = con.execute(f"""
        SELECT COUNT(*) n, COUNT(DISTINCT ticker) tk, COUNT(DISTINCT date) n_days,
               MIN(date) mind, MAX(date) maxd FROM read_parquet('{OUT_PARQUET}')
    """).fetchone()
    print(f"DONE: {s[0]} rows, {s[1]} tickers, {s[2]} days, {s[3]}..{s[4]}")


if __name__ == "__main__":
    main()
