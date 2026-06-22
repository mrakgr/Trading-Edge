#!/usr/bin/env python3
"""Download point-in-time public float (dei:EntityPublicFloat) from SEC's BULK frames API.

Float is the truer "tradeable share base" feature for HighFlyer (the daily swing-momentum
system): low-float names move violently on the same volume that barely budges a high-float
stock. dei:EntityPublicFloat is the 10-K cover-page USD market value of non-affiliate shares
(true free float, annual, back to ~2009).

WHY FRAMES, NOT PER-COMPANY: SEC's xbrl/frames endpoint returns EVERY company's value for one
concept+period in a SINGLE call, keyed by CIK:
    https://data.sec.gov/api/xbrl/frames/dei/EntityPublicFloat/USD/CY2023Q2I.json
Public float is measured as of the last business day of the issuer's 2nd fiscal quarter, so the
annual values land across the four "instant" frames CYyyyyQ{1..4}I (Q2I is the big calendar-FY
bucket). A full 2009->today sweep is ~70 calls / a few minutes -- vs ~12k per-company get_facts()
calls / ~80 min. The frames payload is keyed by CIK, so we map CIK -> our tickers via the
ticker_cik table (built by resolve_cik.py).

NO-LOOKAHEAD: frames gives period_end (measurement date) + accn (accession) but not filing_date.
The 10-K carrying the float is filed within the SEC deadline (<=90 days after fiscal year-end),
so we store known_date = period_end + 90 days as a conservative "available to a live trader"
key. accn is kept so an exact filing date can be recovered later if ever needed. (We hold ~5
days, so a generous lag costs nothing.)

Storage: dedicated DuckDB file data/equity/float/float.db.
  - float_sec     : one row per (cik, period_end); ticker(s) attached via ticker_cik
  - frame_status  : per-frame fetch log (resumable / re-runnable)
Re-running is idempotent (upsert by cik, period_end).

Usage:
  python scripts/equity/download_float.py                 # sweep all frames 2009..current
  python scripts/equity/download_float.py --start 2009 --end 2026
  python scripts/equity/download_float.py --status         # coverage summary only
"""
import argparse
import datetime as dt
import os
import time

import duckdb
import requests

TRADING_DB = "data/trading.db"
FLOAT_DB = "data/equity/float/float.db"
HEADERS = {"User-Agent": "Marko Grdinic mrakgr@gmail.com"}
FRAME_URL = "https://data.sec.gov/api/xbrl/frames/dei/EntityPublicFloat/USD/{period}.json"
CONCEPT = "dei:EntityPublicFloat"
LOOKAHEAD_LAG_DAYS = 90  # period_end + this = conservative "known to a live trader" date


def ensure_schema(con):
    con.execute("""
        CREATE TABLE IF NOT EXISTS float_sec (
            cik          BIGINT  NOT NULL,
            concept      VARCHAR NOT NULL,
            period_end   DATE    NOT NULL,   -- float measurement ("as-of") date
            known_date   DATE,               -- period_end + 90d: no-lookahead availability key
            value        DOUBLE,             -- USD (EntityPublicFloat)
            entity_name  VARCHAR,
            accession    VARCHAR,            -- recover exact filing date later if needed
            frame        VARCHAR,            -- CYyyyyQnI this came from
            PRIMARY KEY (cik, concept, period_end)
        );
    """)
    con.execute("""
        CREATE TABLE IF NOT EXISTS frame_status (
            frame       VARCHAR PRIMARY KEY,
            n_rows      INTEGER,
            http_status INTEGER,
            updated_at  TIMESTAMP DEFAULT current_timestamp
        );
    """)


def frame_periods(start_year, end_year):
    """All CYyyyyQnI instant frames in [start_year, end_year], not past today."""
    today = dt.date.today()
    out = []
    for yr in range(start_year, end_year + 1):
        for q in (1, 2, 3, 4):
            # quarter-end month for a rough "is this frame in the past" guard
            qend_month = q * 3
            if dt.date(yr, qend_month, 1) > today:
                continue
            out.append(f"CY{yr}Q{q}I")
    return out


def fetch_frame(session, period):
    """Return (http_status, list[row]) for one frame; [] on non-200 (e.g. frame not yet built)."""
    r = session.get(FRAME_URL.format(period=period), headers=HEADERS, timeout=60)
    if r.status_code != 200:
        return r.status_code, []
    return 200, r.json().get("data", [])


def upsert_frame(con, period, rows):
    """Set-based upsert for one frame: stage the frame's rows in a temp relation, then a single
    DELETE + INSERT keyed by (cik, period_end). One round-trip per frame instead of two per row
    -- far faster, and it avoids the native allocator corruption that the row-by-row loop hit."""
    recs = []
    for x in rows:
        pe = dt.date.fromisoformat(x["end"])
        recs.append((int(x["cik"]), CONCEPT, pe,
                     pe + dt.timedelta(days=LOOKAHEAD_LAG_DAYS),
                     float(x["val"]), x.get("entityName"), x.get("accn"), period))
    # Register the staged rows as a DuckDB relation via a parameterized VALUES table.
    con.execute("""
        CREATE OR REPLACE TEMP TABLE _stage
          (cik BIGINT, concept VARCHAR, period_end DATE, known_date DATE,
           value DOUBLE, entity_name VARCHAR, accession VARCHAR, frame VARCHAR)
    """)
    con.executemany("INSERT INTO _stage VALUES (?,?,?,?,?,?,?,?)", recs)
    con.execute("""
        DELETE FROM float_sec f
        USING _stage s
        WHERE f.cik=s.cik AND f.concept=s.concept AND f.period_end=s.period_end
    """)
    con.execute("INSERT INTO float_sec SELECT * FROM _stage")


def print_status(con):
    print("\n=== frame_status ===")
    fr = con.execute("""
        SELECT COUNT(*) frames, SUM(n_rows) datapoints,
               COUNT(*) FILTER (WHERE http_status<>200) non200
        FROM frame_status""").fetchone()
    print(f"  frames fetched={fr[0]}  datapoints={fr[1]}  non-200={fr[2]}")
    print("\n=== float_sec ===")
    s = con.execute("""
        SELECT COUNT(*) n_rows, COUNT(DISTINCT cik) ciks,
               MIN(period_end) min_pe, MAX(period_end) max_pe FROM float_sec""").fetchone()
    print(f"  rows={s[0]}  distinct CIKs={s[1]}  period_end {s[2]}..{s[3]}")
    # how many map to OUR universe tickers?
    mapped = con.execute("""
        SELECT COUNT(DISTINCT tc.ticker)
        FROM float_sec f JOIN ticker_cik tc ON tc.cik = f.cik""").fetchone()[0]
    uni = con.execute("""SELECT COUNT(DISTINCT ticker) FROM trading.ticker_reference
                         WHERE type IN ('CS','ADRC')""").fetchone()[0]
    print(f"  -> tickers in CS+ADRC universe with >=1 float row: {mapped} / {uni} "
          f"({100*mapped/uni:.1f}%)")


def main():
    ap = argparse.ArgumentParser(description="Download SEC public float via the bulk frames API.")
    ap.add_argument("--start", type=int, default=2009, help="first calendar year (default 2009)")
    ap.add_argument("--end", type=int, default=dt.date.today().year, help="last calendar year")
    ap.add_argument("--status", action="store_true", help="print coverage summary and exit")
    ap.add_argument("--refetch", action="store_true", help="re-fetch frames already logged 200")
    ap.add_argument("--sleep", type=float, default=0.12, help="polite sleep between frame calls")
    args = ap.parse_args()

    os.makedirs("data/equity/float", exist_ok=True)
    con = duckdb.connect(FLOAT_DB)
    con.execute(f"ATTACH '{TRADING_DB}' AS trading (READ_ONLY)")
    ensure_schema(con)

    if args.status:
        print_status(con)
        return

    periods = frame_periods(args.start, args.end)
    # Resume: skip frames already fetched OK (http 200), unless --refetch.
    if not args.refetch:
        done = set(r[0] for r in con.execute(
            "SELECT frame FROM frame_status WHERE http_status=200").fetchall())
        skip = [p for p in periods if p in done]
        periods = [p for p in periods if p not in done]
        if skip:
            print(f"resuming: {len(skip)} frames already done, {len(periods)} to fetch")
    print(f"sweeping {len(periods)} frames CY{args.start}..CY{args.end} "
          f"(dei:EntityPublicFloat/USD)")

    sess = requests.Session()
    total_rows = 0
    t0 = time.time()
    for i, p in enumerate(periods, 1):
        status, rows = fetch_frame(sess, p)
        if rows:
            upsert_frame(con, p, rows)
            total_rows += len(rows)
        con.execute("""
            INSERT INTO frame_status (frame, n_rows, http_status, updated_at)
            VALUES (?, ?, ?, current_timestamp)
            ON CONFLICT (frame) DO UPDATE
              SET n_rows=excluded.n_rows, http_status=excluded.http_status,
                  updated_at=excluded.updated_at
        """, [p, len(rows), status])
        print(f"  [{i}/{len(periods)}] {p:10s} http={status} rows={len(rows):5d} "
              f"(cum {total_rows})", flush=True)
        time.sleep(args.sleep)

    print(f"\nfinished in {time.time()-t0:.0f}s, {total_rows} datapoints")
    print_status(con)


if __name__ == "__main__":
    main()
