#!/usr/bin/env python3
"""Resolve ticker -> CIK for the full CS+ADRC universe, including DELISTED names.

Problem: SEC's public ticker maps (and edgartools) only contain CURRENTLY-LISTED tickers, so
~54% of our cumulative 2005-2026 universe -- the delisted/acquired/renamed names where momentum
runners often end up -- resolve to no CIK. Their float exists in SEC data keyed by CIK; we just
need the bridge.

Two-source resolution, cached into float.db's ticker_cik table:
  1. edgartools live company-tickers map (fast, in-bulk, still-listed names).
  2. Polygon ticker-details for the rest -- queried WITH A DATE inside the ticker's listing
     window (delisted tickers 404 on the bare endpoint but resolve with ?date=<active day>).
     We use the ticker's last trading date from split_adjusted_prices as that active day.

Idempotent + resumable: a ticker already in ticker_cik with a non-null cik is skipped. Re-run to
fill gaps (e.g. after a transient Polygon error). Run before download_float.py.

Usage:
  python scripts/equity/resolve_cik.py            # resolve everything still missing
  python scripts/equity/resolve_cik.py --limit 200
  python scripts/equity/resolve_cik.py --status   # coverage summary only
"""
import argparse
import json
import os
import time

import duckdb
import requests
from edgar import set_identity
from edgar.reference.tickers import get_company_tickers

IDENTITY = "Marko Grdinic mrakgr@gmail.com"
TRADING_DB = "data/trading.db"
FLOAT_DB = "data/equity/float/float.db"
POLY_BASE = "https://api.polygon.io/v3/reference/tickers/"


def load_polygon_key():
    cfg = json.load(open("api_key.json")) if os.path.exists("api_key.json") else {}
    return cfg.get("massive_api_key") or os.getenv("POLYGON_API_KEY")


def ensure_schema(con):
    con.execute("""
        CREATE TABLE IF NOT EXISTS ticker_cik (
            ticker      VARCHAR PRIMARY KEY,
            cik         BIGINT,            -- NULL = resolution attempted but failed
            source      VARCHAR,           -- 'edgar' | 'polygon' | 'none'
            sec_name    VARCHAR,
            updated_at  TIMESTAMP DEFAULT current_timestamp
        );
    """)


def upsert_cik(con, ticker, cik, source, name):
    con.execute("""
        INSERT INTO ticker_cik (ticker, cik, source, sec_name, updated_at)
        VALUES (?, ?, ?, ?, current_timestamp)
        ON CONFLICT (ticker) DO UPDATE
          SET cik=excluded.cik, source=excluded.source,
              sec_name=excluded.sec_name, updated_at=excluded.updated_at
    """, [ticker, cik, source, name])


def poly_cik(session, key, ticker, active_date):
    """Polygon ticker-details CIK. Try bare endpoint, then with ?date=<active day>."""
    for params in ({"apiKey": key}, {"apiKey": key, "date": active_date}):
        if "date" in params and active_date is None:
            continue
        try:
            r = session.get(POLY_BASE + ticker, params=params, timeout=20)
        except requests.RequestException:
            continue
        if r.status_code == 200:
            res = r.json().get("results", {}) or {}
            cik = res.get("cik")
            if cik:
                return int(cik), res.get("name")
        elif r.status_code == 429:
            time.sleep(2.0)  # rate limited -- back off and let the next ticker retry
    return None, None


def main():
    ap = argparse.ArgumentParser(description="Resolve ticker->CIK incl. delisted via Polygon.")
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--status", action="store_true")
    ap.add_argument("--sleep", type=float, default=0.0)
    args = ap.parse_args()

    set_identity(IDENTITY)
    os.makedirs("data/equity/float", exist_ok=True)
    con = duckdb.connect(FLOAT_DB)
    con.execute(f"ATTACH '{TRADING_DB}' AS trading (READ_ONLY)")
    ensure_schema(con)

    if args.status:
        for src, n in con.execute(
                "SELECT source, COUNT(*) FROM ticker_cik GROUP BY 1 ORDER BY 2 DESC").fetchall():
            print(f"  {src or 'NULL':8s} {n}")
        got = con.execute("SELECT COUNT(*) FROM ticker_cik WHERE cik IS NOT NULL").fetchone()[0]
        print(f"  resolved (cik not null): {got}")
        return

    # universe + last active date per ticker (used as the Polygon ?date= for delisted names)
    rows = con.execute("""
        SELECT u.ticker, p.last_date
        FROM (SELECT DISTINCT ticker FROM trading.ticker_reference
              WHERE type IN ('CS','ADRC')) u
        LEFT JOIN (SELECT ticker, MAX(date) AS last_date
                   FROM trading.split_adjusted_prices GROUP BY 1) p
          ON p.ticker = u.ticker
        ORDER BY u.ticker
    """).fetchall()
    universe = {t: (str(d) if d is not None else None) for t, d in rows}

    # source 1: edgar live map -- bulk, free, covers still-listed names
    ct = get_company_tickers()
    live = dict(zip(ct["ticker"].astype(str), ct["cik"].astype(int)))

    already = set(r[0] for r in con.execute(
        "SELECT ticker FROM ticker_cik WHERE cik IS NOT NULL").fetchall())

    # First pass: stamp every edgar-live hit cheaply (no network per ticker).
    n_edgar = 0
    for t in universe:
        if t in already:
            continue
        if t in live:
            upsert_cik(con, t, live[t], "edgar", None)
            already.add(t)
            n_edgar += 1
    print(f"edgar live map resolved {n_edgar} (total resolved now {len(already)})")

    # Second pass: Polygon for the rest (delisted / not in live map).
    key = load_polygon_key()
    if not key:
        print("No Polygon key -> stopping after edgar pass.")
        return
    todo = [t for t in universe if t not in already]
    if args.limit:
        todo = todo[:args.limit]
    print(f"Polygon resolving {len(todo)} unresolved tickers...")

    sess = requests.Session()
    n_poly = n_none = 0
    t0 = time.time()
    for i, t in enumerate(todo, 1):
        cik, name = poly_cik(sess, key, t, universe[t])
        if cik:
            upsert_cik(con, t, cik, "polygon", name)
            n_poly += 1
        else:
            upsert_cik(con, t, None, "none", None)
            n_none += 1
        if args.sleep:
            time.sleep(args.sleep)
        if i % 200 == 0 or i == len(todo):
            rate = i / max(time.time() - t0, 1e-9)
            print(f"  [{i}/{len(todo)}] poly_hit={n_poly} none={n_none} "
                  f"| {rate:.1f} tk/s | ETA {(len(todo)-i)/max(rate,1e-9)/60:.1f}m", flush=True)

    tot = con.execute("SELECT COUNT(*) FROM ticker_cik WHERE cik IS NOT NULL").fetchone()[0]
    print(f"\nfinished: edgar={n_edgar} polygon={n_poly} unresolved={n_none} "
          f"| total CIKs resolved={tot}/{len(universe)} ({100*tot/len(universe):.1f}%)")


if __name__ == "__main__":
    main()
