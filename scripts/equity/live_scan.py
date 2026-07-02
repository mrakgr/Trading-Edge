#!/usr/bin/env python3
"""
LIVE A+ scanner — HighFlyerV2 production gate applied to Massive's real-time
full-market snapshot, joined against the D-1 daily factor base in trading.db.

This is the first cut of the real-time detection process (2026-07-01). It:
  1. Pulls the Massive full-market snapshot (all ~13k tickers, one call): each
     ticker's today OHLC/volume + prevDay + todaysChange.
  2. Joins the D-1 (last completed close) daily factors computed in DuckDB to
     match the engine's indicators (Types.fs): 252d high close, 14-bar log/lin
     ATR, 14-bar tightness range, 20-bar mean volume + dollar volume,
     126-bar max log-ATR.
  3. Applies the exact production EntryConfig gate + the low-float ($<300M) join.

Gate (Types.fs ShouldEnter, production defaults):
  move = day.c/prevClose-1 in [0.10, 0.30)   rvol = day.v/avgVol4w >= 5.0
  avgDolVol4w >= 100k   close >= 0.95*hiClose252   close >= $1
  tightness < 4.5   atrPctLog < 0.10   close/open-1 >= -0.07   maxAtrLog126 >= 0.04

NOTE: this uses the LIVE partial day (intraday), so move/rvol are as-of-now, not
the daily close. Per the checkpoint calibration the *close* rvol floor is 5.0;
intraday the effective floor is lower (1.0 at 10:00). We surface rvol so the
operator can judge; the --rvol-min flag defaults to a lenient 1.0 for a live
intraday read (pass --rvol-min 5 to see only names already at close-equivalent).

Usage:
  python scripts/equity/live_scan.py                 # full A+ gate, rvol>=1
  python scripts/equity/live_scan.py --rvol-min 3    # stricter
  python scripts/equity/live_scan.py --show-near      # also show near-misses
"""
import argparse, json, sys, urllib.request, urllib.parse
from pathlib import Path
import duckdb

ROOT = Path(__file__).resolve().parents[2]
DB = ROOT / "data" / "trading.db"
FLOAT_DB = ROOT / "data" / "equity" / "float" / "float.db"
KEY = json.load(open(ROOT / "api_key.json"))["massive_api_key"]

# production EntryConfig defaults (Types.fs)
# rvol_min is the CHECKPOINT-calibrated floor, not the daily-close 5.0: intraday
# volume is still accumulating, so the equivalent floor rises through the session
# (~1.0 @ 10:00 -> ~5.0 @ close). Default set to 1.25 for a ~11:00 ET read.
G = dict(up_lo=0.10, up_hi=0.30, rvol_min=1.25, adv_min=100_000.0, p52=0.95,
         price_min=1.0, tight_max=4.5, atr_max=0.10, intraday_floor=-0.07,
         maxatrlog_min=0.04, float_max=300e6)


def fetch_snapshot():
    url = ("https://api.polygon.io/v2/snapshot/locale/us/markets/stocks/tickers"
           f"?apiKey={urllib.parse.quote(KEY)}")
    with urllib.request.urlopen(url, timeout=60) as r:
        d = json.load(r)
    if d.get("status") not in ("OK", "DELAYED"):
        sys.exit(f"snapshot error: {d.get('status')} {d.get('error','')}")
    rows = []
    for t in d.get("tickers", []):
        day = t.get("day") or {}
        prev = t.get("prevDay") or {}
        o, h, l, c, v = day.get("o"), day.get("h"), day.get("l"), day.get("c"), day.get("v")
        pc = prev.get("c")
        if not (o and c and v and pc):  # need a real intraday print + prior close
            continue
        rows.append((t["ticker"], o, h, l, c, v, pc, t.get("updated")))
    return rows, d.get("status")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rvol-min", type=float, default=G["rvol_min"])
    ap.add_argument("--show-near", action="store_true",
                    help="also list names failing exactly one gate")
    args = ap.parse_args()

    snap, status = fetch_snapshot()
    print(f"snapshot: {status}, {len(snap)} tickers with an intraday print", file=sys.stderr)

    con = duckdb.connect(":memory:")
    con.execute(f"ATTACH '{DB}' AS db (READ_ONLY)")
    con.execute(f"ATTACH '{FLOAT_DB}' AS f (READ_ONLY)")
    con.execute("CREATE TEMP TABLE snap(ticker VARCHAR, o DOUBLE, h DOUBLE, l DOUBLE, "
                "c DOUBLE, v DOUBLE, pc DOUBLE, updated BIGINT)")
    con.executemany("INSERT INTO snap VALUES (?,?,?,?,?,?,?,?)", snap)

    # D-1 factor base: compute the engine's indicators as of the LAST completed
    # close, per ticker, over CS/ADRC names. Window-frame math mirrors Types.fs.
    #
    # GAP-SEVERING: a recycled ticker (e.g. MRX = old co. through 2012, then Marex
    # from 2024) is stored under one ticker string with a multi-year gap between the
    # two listings. A naive rolling window reaches across the gap and contaminates
    # the 52w high with the PRIOR company's prices. We assign a running `episode` id
    # that increments on every >45-day gap between consecutive bars, then PARTITION
    # every rolling window by (ticker, episode) — so no window can span a gap. This
    # is the SQL equivalent of resetting the engine's rolling state on a detected
    # gap; both yield the same result (validated: MRX -> two episodes, hi $42.52 old
    # / $66.51 new). GAP_DAYS=45 (>1 calendar month of no trading = a new listing).
    con.execute(f"""
    CREATE TEMP TABLE feat AS
    WITH base AS (
      -- episode partitioning comes from the shared `daily_episodes` view (CS/ADRC
      -- universe, gap-severed at >45d). See scripts/equity/build_daily_episodes_view.sql.
      -- NOTE: window over the FULL history (no date floor here) so the 252/126/20-bar
      -- windows are fully warmed — a date floor in this CTE would STARVE the windows
      -- (validated: floor-in-base drops avgvol28 accuracy; full-warm == engine to 0.0).
      -- Recency is applied at the FINAL selection instead (see the closing WHERE).
      SELECT ticker, date, adj_open o, adj_high h, adj_low l, adj_close c,
             adj_volume v, episode
      FROM db.daily_episodes
    ),
    tr AS (
      -- prior close, EPISODE-partitioned (NULL at each episode's first bar) so the
      -- true range is not computed across a gap — matches the engine, which resets
      -- prevClose to ValueNone on a gap (lastBar cleared in ResetIndicators).
      SELECT *,
        LAG(c) OVER (PARTITION BY ticker, episode ORDER BY date) AS pc
      FROM base
    ),
    trx AS (
      SELECT *,
        CASE WHEN pc>0 AND h>0 AND l>0 THEN greatest(h,pc)-least(l,pc) END AS tr_lin,
        CASE WHEN pc>0 AND h>0 AND l>0 THEN ln(greatest(h,pc)/least(l,pc)) END AS tr_log
      FROM tr
    ),
    roll AS (
      SELECT ticker, date, c, o, episode,
        AVG(tr_lin) OVER w14 AS atr_lin,
        AVG(tr_log) OVER w14 AS atr_log,
        MAX(h) OVER w14 AS rng_hi,
        MIN(l) OVER w14 AS rng_lo,
        MAX(c) OVER w252 AS hi_close252,
        -- 20-BAR trailing mean volume + dollar-volume, episode-partitioned, over the
        -- 20 COMPLETED bars ending at this row inclusive (ROWS 19 PRECEDING AND CURRENT
        -- ROW). This row is the last completed close (rn_desc=1) = bar D-1 for a live
        -- scan on the still-forming bar D, so the frame is [D-20 .. D-1] — exactly the
        -- 20 prior bars the engine's AvgMa(20) holds when it reads .State pre-push on D.
        -- Replaces the old 28-CALENDAR-day window to match the engine (Types.fs).
        -- Episode partition severs recycled-ticker gaps like the engine.
        AVG(v)   OVER wvol AS avgvol28,
        AVG(c*v) OVER wvol AS avgdolvol28,
        ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date DESC) AS rn_desc,
        -- bars in the CURRENT episode only (warmup gate must not count old-listing bars)
        COUNT(*) OVER (PARTITION BY ticker, episode) AS nbars
      FROM trx
      -- every rolling window is partitioned by (ticker, episode): it cannot span a gap.
      WINDOW w14  AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 13 PRECEDING AND CURRENT ROW),
             w252 AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 251 PRECEDING AND CURRENT ROW),
             wvol AS (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 19 PRECEDING AND CURRENT ROW)
    ),
    -- 126-bar max of the 14-bar log-ATR (also episode-partitioned)
    maxatr AS (
      SELECT ticker, date,
        MAX(atr_log) OVER (PARTITION BY ticker, episode ORDER BY date ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS maxatrlog126
      FROM roll
    )
    SELECT r.ticker,
      r.atr_lin, r.atr_log AS atr_pct, r.rng_hi, r.rng_lo,
      (r.rng_hi - r.rng_lo) AS range_abs,
      CASE WHEN r.atr_lin>0 THEN (r.rng_hi-r.rng_lo)/r.atr_lin END AS tightness,
      r.hi_close252, m.maxatrlog126, r.avgvol28, r.avgdolvol28, r.nbars
    FROM roll r
    JOIN maxatr m ON m.ticker=r.ticker AND m.date=r.date
    -- last completed close per ticker, warmed up, and RECENT (a currently-listed
    -- name — the recency floor lives HERE, on the selected row, not in `base` where
    -- it would starve the rolling windows).
    WHERE r.rn_desc = 1 AND r.nbars > 21 AND r.date >= CURRENT_DATE - INTERVAL 10 DAY
    """)

    # float at today's live price (dollar-float = shares * price).
    # Step 1: latest known SEC public-float ($) per snapshot ticker, as-of today.
    con.execute("""
    CREATE TEMP TABLE fmap AS
    SELECT tc.ticker, fsx.known_date, fsx.period_end, fsx.value fu
    FROM f.float_sec fsx JOIN f.ticker_cik tc ON tc.cik=fsx.cik WHERE fsx.value>0
    """)
    con.execute("""
    CREATE TEMP TABLE fsrow AS
    SELECT s.ticker, fr.fu, fr.period_end
    FROM snap s
    LEFT JOIN LATERAL (
      SELECT fu, period_end FROM fmap m
      WHERE m.ticker=s.ticker AND m.known_date<=CURRENT_DATE
      ORDER BY m.known_date DESC LIMIT 1
    ) fr ON TRUE
    """)
    # Step 2: convert the SEC $-float to shares via its period-end adj_close, then
    #   revalue at today's price; fall back to polygon SCSO * today's price.
    con.execute("""
    CREATE TEMP TABLE flt AS
    SELECT s.ticker,
      COALESCE(
        CASE WHEN fr.fu IS NOT NULL AND pe.adj_close>0 THEN fr.fu*s.c/pe.adj_close END,
        ps.scso*s.c) AS dollar_float
    FROM snap s
    LEFT JOIN fsrow fr ON fr.ticker=s.ticker
    LEFT JOIN LATERAL (
      SELECT adj_close FROM db.split_adjusted_prices p2
      WHERE p2.ticker=s.ticker AND p2.date<=fr.period_end
      ORDER BY p2.date DESC LIMIT 1
    ) pe ON TRUE
    LEFT JOIN LATERAL (
      SELECT scso FROM f.polygon_shares p3
      WHERE p3.ticker=s.ticker ORDER BY p3.date DESC LIMIT 1
    ) ps ON TRUE
    """)

    # apply the gate. move/rvol/intraday from the LIVE snapshot; the rest from D-1 feat.
    q = f"""
    WITH scored AS (
      SELECT s.ticker,
        s.c AS price, s.v AS vol_today,
        s.c/s.pc - 1.0 AS move,
        s.c/s.o - 1.0 AS intraday_ret,
        s.v/NULLIF(ft.avgvol28,0) AS rvol,
        ft.avgdolvol28, ft.tightness, ft.atr_pct, ft.hi_close252, ft.maxatrlog126,
        fl.dollar_float,
        s.c >= {G['price_min']} AS g_price,
        (s.c/s.pc-1.0) >= {G['up_lo']} AND (s.c/s.pc-1.0) < {G['up_hi']} AS g_move,
        s.v/NULLIF(ft.avgvol28,0) >= {args.rvol_min} AS g_rvol,
        ft.avgdolvol28 >= {G['adv_min']} AS g_adv,
        s.c >= {G['p52']}*ft.hi_close252 AS g_52w,
        ft.tightness < {G['tight_max']} AS g_tight,
        ft.atr_pct < {G['atr_max']} AS g_atr,
        (s.c/s.o-1.0) >= {G['intraday_floor']} AS g_intra,
        ft.maxatrlog126 >= {G['maxatrlog_min']} AS g_maxatr
      FROM snap s
      JOIN feat ft ON ft.ticker=s.ticker
      LEFT JOIN flt fl ON fl.ticker=s.ticker
    )
    SELECT *,
      (g_price::INT+g_move::INT+g_rvol::INT+g_adv::INT+g_52w::INT+g_tight::INT
       +g_atr::INT+g_intra::INT+g_maxatr::INT) AS gates_passed,
      (dollar_float IS NOT NULL AND dollar_float < {G['float_max']}) AS g_lowfloat
    FROM scored
    """
    con.execute(f"CREATE TEMP TABLE res AS {q}")

    ncols = "9"  # number of core gates (float is separate)
    pass_rows = con.execute(f"""
      SELECT ticker, ROUND(price,2) px, ROUND(100*move,1) move_pct, ROUND(rvol,1) rvol,
             ROUND(100*intraday_ret,1) intra_pct, ROUND(tightness,2) tight,
             ROUND(atr_pct,3) atr, ROUND(dollar_float/1e6,0) float_mm, g_lowfloat
      FROM res WHERE gates_passed={ncols} ORDER BY g_lowfloat DESC, move DESC
    """).fetchall()

    print("\n================ A+ CANDIDATES (all 9 core gates pass) ================")
    if not pass_rows:
        print("  (none right now)")
    else:
        print(f"  {'TICKER':<8}{'PX':>8}{'MOVE%':>7}{'RVOL':>7}{'INTRA%':>8}{'TIGHT':>7}{'ATR':>7}{'FLOAT$M':>9}  LOW-FLOAT")
        for r in pass_rows:
            lf = "  *** LOW FLOAT ***" if r[8] else ""
            fm = f"{r[7]:.0f}" if r[7] is not None else "  ?"
            print(f"  {r[0]:<8}{r[1]:>8}{r[2]:>7}{r[3]:>7}{r[4]:>8}{r[5]:>7}{r[6]:>7}{fm:>9}{lf}")

    if args.show_near:
        near = con.execute(f"""
          SELECT ticker, ROUND(price,2) px, ROUND(100*move,1) move_pct, ROUND(rvol,1) rvol,
                 ROUND(100*intraday_ret,1) intra_pct, ROUND(tightness,2) tight, ROUND(atr_pct,3) atr,
                 ROUND(dollar_float/1e6,0) float_mm,
                 concat_ws(',',
                   CASE WHEN NOT g_move THEN 'move' END, CASE WHEN NOT g_rvol THEN 'rvol' END,
                   CASE WHEN NOT g_52w THEN '52w' END, CASE WHEN NOT g_tight THEN 'tight' END,
                   CASE WHEN NOT g_atr THEN 'atr' END, CASE WHEN NOT g_intra THEN 'intra' END,
                   CASE WHEN NOT g_adv THEN 'adv' END, CASE WHEN NOT g_maxatr THEN 'maxatr' END,
                   CASE WHEN NOT g_price THEN 'price' END) AS failed
          FROM res WHERE gates_passed={int(ncols)-1} AND g_move ORDER BY move DESC LIMIT 25
        """).fetchall()
        print("\n---------------- NEAR-MISSES (fail exactly 1 gate, move OK) ----------------")
        for r in near:
            fm = f"{r[7]:.0f}" if r[7] is not None else "?"
            print(f"  {r[0]:<8} px={r[1]:<7} move={r[2]}%  rvol={r[3]}  intra={r[4]}%  "
                  f"tight={r[5]} atr={r[6]} float${fm}M  FAILED: {r[8]}")


if __name__ == "__main__":
    main()
