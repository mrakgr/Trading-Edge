"""Simulate an intraday CATASTROPHE STOP on the MaxFlyerV3 A-book (short pop-fade).

The V3 short holds to MOC with no stop, so a squeeze runs unbounded. This sim adds a
per-trade stop: for a SHORT, exit at the FIRST 1m bar (at/after entry) whose HIGH
reaches entry*(1+f). Fill = that level (entry*(1+f)) — a stop-market lifting into the
spike (conservative: assumes we get filled AT the trigger, not worse). If no bar ever
pierces, the trade rides to its actual MOC exit (ret_moc).

For a short, per-trade return on the underlying:
    stopped  ->  ret = -f            (we bought back f above entry: a -f loss on the short)
    no stop  ->  ret = -ret_moc      (the book's convention: short P&L = -underlying move)
net_pnl scales linearly with return (qty*entry_price*ret), so we recompute net from ret.

Reads /tmp/mfv3_abook.parquet (symbol, trade_date, adj_ratio, entry_price, qty, entry_min,
ret_moc, net_pnl) and the per-date minute parquets. Prints PF / win / net / worst-loss /
fire-rate for no-stop and each stop level, overall and by year.

Usage: python scripts/equity/mfv3_stop_sim.py [--stops 0.15 0.25 0.40 0.60]
"""
import argparse, os, glob
import duckdb

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
MINUTE_DIR = os.path.join(ROOT, 'data/minute_aggs')


def load_abook(con):
    q = "SELECT * FROM read_parquet(?)"
    cols = [d[0] for d in con.execute(q, ['/tmp/mfv3_abook.parquet']).description]
    return [dict(zip(cols, r)) for r in con.execute(q, ['/tmp/mfv3_abook.parquet']).fetchall()]


def first_pierce_min(con, date, ticker, entry_min, level):
    """First et_min >= entry_min whose HIGH (adj) reaches level. None if never."""
    pq = os.path.join(MINUTE_DIR, f'{date}.parquet')
    if not os.path.exists(pq):
        return None
    q = """
    WITH bars AS (
      SELECT CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
           + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
             high FROM read_parquet(?) WHERE ticker=? AND close>0)
    SELECT MIN(et_min) FROM bars WHERE et_min >= ? AND high >= ?
    """
    r = con.execute(q, [pq, ticker, int(entry_min), float(level)]).fetchone()
    return r[0] if r and r[0] is not None else None


def pf(rets_qtys_px):
    g = sum(q*p*r for r, q, p in rets_qtys_px if r > 0)
    l = sum(-q*p*r for r, q, p in rets_qtys_px if r < 0)
    return g / l if l > 0 else float('inf')


def stats(trades):
    # trades: list of (short_ret, qty, entry_px, year)  where short_ret = P&L-sign return
    n = len(trades)
    w = sum(1 for r, _, _, _ in trades if r > 0)
    net = sum(q*p*r for r, q, p, _ in trades)
    worst = min(q*p*r for r, q, p, _ in trades)
    return n, 100*w/n, pf([(r, q, p) for r, q, p, _ in trades]), net, worst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--stops', type=float, nargs='+', default=[0.15, 0.25, 0.40, 0.60])
    args = ap.parse_args()
    con = duckdb.connect(os.path.join(ROOT, 'data/trading.db'), read_only=True)
    ab = load_abook(con)

    # Precompute, per trade, the earliest pierce minute for the LARGEST stop only is not enough
    # (each level fires at its own minute); but since fill = level regardless of WHEN, we only
    # need to know IF each level is ever pierced intraday. A short that pierces +f also pierces
    # every smaller +f'. So compute the session MAX adverse high once, then compare per level.
    for t in ab:
        pq = os.path.join(MINUTE_DIR, f"{t['trade_date']}.parquet")
        mx = None
        if os.path.exists(pq):
            q = """
            WITH bars AS (
              SELECT CAST(date_part('hour', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT)*60
                   + CAST(date_part('minute', to_timestamp(window_start/1e9) AT TIME ZONE 'America/New_York') AS INT) AS et_min,
                     high FROM read_parquet(?) WHERE ticker=? AND close>0)
            SELECT MAX(high) FROM bars WHERE et_min >= ?
            """
            r = con.execute(q, [pq, t['symbol'], int(t['entry_min'])]).fetchone()
            mx = r[0] if r else None
        t['max_high_adj'] = (mx * float(t['adj_ratio'])) if mx is not None else None

    years = sorted(set(int(str(t['trade_date'])[:4]) for t in ab))

    def build(stop_f):
        out = []
        for t in ab:
            yr = int(str(t['trade_date'])[:4])
            qty = float(t['qty']); epx = float(t['entry_price'])
            if stop_f is None:
                sr = -float(t['ret_moc'])           # no stop: short P&L return
            else:
                lvl = epx * (1.0 + stop_f)
                pierced = t['max_high_adj'] is not None and t['max_high_adj'] >= lvl
                sr = (-stop_f) if pierced else (-float(t['ret_moc']))
            out.append((sr, qty, epx, yr))
        return out

    print(f"A-book trips: {len(ab)}\n")
    header = f"{'config':>12s} {'n':>5s} {'win%':>6s} {'PF':>7s} {'net':>12s} {'worst':>10s} {'fire%':>6s}"
    print(header); print('-'*len(header))
    configs = [('no-stop', None)] + [(f'stop +{int(f*100)}%', f) for f in args.stops]
    for name, f in configs:
        tr = build(f)
        n, wp, p, net, worst = stats(tr)
        if f is None:
            fire = 0.0
        else:
            fire = 100.0*sum(1 for t in ab if t['max_high_adj'] is not None and t['max_high_adj'] >= float(t['entry_price'])*(1+f))/len(ab)
        pstr = f"{p:7.2f}" if p != float('inf') else "    inf"
        print(f"{name:>12s} {n:>5d} {wp:>5.1f}% {pstr} {net:>12,.0f} {worst:>10,.0f} {fire:>5.1f}%")

    # by-year for no-stop vs the middle stop
    print("\n=== by-year: no-stop  vs  each stop (PF / net) ===")
    mids = [None] + args.stops
    cols = ['no-stop'] + [f'+{int(f*100)}%' for f in args.stops]
    print(f"{'yr':>5s} " + ' '.join(f"{c:>16s}" for c in cols))
    for yr in years:
        cells = []
        for f in mids:
            tr = [t for t in build(f) if t[3] == yr]
            if not tr:
                cells.append(f"{'--':>16s}"); continue
            _, _, p, net, _ = stats(tr)
            pstr = f"{p:.2f}" if p != float('inf') else "inf"
            cells.append(f"{pstr:>7s}/{net/1000:>7.0f}k")
        print(f"{yr:>5d} " + ' '.join(cells))


if __name__ == '__main__':
    main()
