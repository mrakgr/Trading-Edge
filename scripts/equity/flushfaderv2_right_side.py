"""RIGHT SIDE OF THE V on the efficiency variant (user 2026-11-07, Lance's concept; research S22 did it on the v1.x book:
PF 2.137 -> 1.917): every production book trade (V2-25 reference 2020-01..2026-08-21, and the parity book 08-24..11-06)
entered instead at the FIRST NEW 60-present-bar HIGH made STRICTLY AFTER the reversal's entry-fill bar, filled at the
FOLLOWING present bar's vwap (the engine's aux-mark discipline, Intraday.fs AuxHi60 — recomputed here from the 1s bars
because the engine stops tracking it 20 min after the entry). Exit = the production exit unchanged (a 5m-high break
implies a 1m-high break, so the first 5m break after the confirmation entry IS the original exit — S22's composition).
No break before the original exit (a trade held to the next open that never bounced to a 1m high) = NO TRADE.
Control: the recomputed parent fill (the first present bar after the signal) = entry_px; the recomputed break = the
engine's aux_hi_60 wherever the engine recorded it.
Run from research/ (defaults = the production reference on base_v24; ~4 min):
    python3 -u scripts/equity/flushfaderv2_right_side.py [--book B] [--trips GLOB] [--out O]
then python3 scripts/equity/flushfaderv2_right_side.py --report (prints V2-27's tables from --out)."""
import argparse, sys, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--book', default='data/flushfaderv2/flushfaderv2_reference_book.parquet')
ap.add_argument('--trips', default='data/equity/flushfader/base_v24/*.parquet', help='the base corpus (aux_hi_60 for the control)')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--out', default='data/flushfaderv2/right_side.parquet')
ap.add_argument('--report', action='store_true')
ap.add_argument('--window', default=None, help='--report: a second result file (a later window) reported beside the history')
args = ap.parse_args()
BOOK, TRIPS, SET = args.book, f"['{args.trips}']", 'book'
if args.report:
    exec(open(__file__.replace('.py', '_report.py')).read()); sys.exit(0)
con = duckdb.connect(); con.execute("SET memory_limit='3GB'"); con.execute("SET threads=4")
B = con.execute(f"""
  WITH b AS (SELECT symbol, trade_date::VARCHAR AS td, signal_sec, entry_sec, exit_sec, entry_px, exit_px, ret_exit, pf1_fitted FROM '{BOOK}'),
       t AS (SELECT DISTINCT ON (symbol, trade_date::VARCHAR, signal_sec) symbol, trade_date::VARCHAR AS td, signal_sec,
                    aux_hi_60_px, aux_hi_60_sec FROM read_parquet({TRIPS}, union_by_name = true)
             WHERE (symbol, trade_date::VARCHAR) IN (SELECT symbol, td FROM b))
  SELECT b.*, t.aux_hi_60_px, t.aux_hi_60_sec FROM b LEFT JOIN t USING (symbol, td, signal_sec) ORDER BY td, symbol, signal_sec""").df()
B['td'] = B.td.str[:10]
print(f"{SET}: {len(B):,} book trades, {B.td.nunique():,} days; engine aux_hi_60 recorded on {(B.aux_hi_60_sec >= 0).mean():.1%}", flush=True)
out = []; T0 = time.time()
for k, (day, g) in enumerate(B.groupby('td', sort=True)):
    tick = list(g.symbol.unique())
    bars = con.execute(f"SELECT ticker, bucket, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                       "AND volume > 0 ORDER BY ticker, bucket", tick).df()
    for sym, gt in g.groupby('symbol'):
        bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64); vw = bt.vwap.values.astype(np.float64)
        for r in gt.itertuples():
            o = dict(symbol=sym, td=day, signal_sec=r.signal_sec, fill_sec=-1, fill_px=np.nan, brk_sec=-1, brk_px=np.nan)
            f = np.searchsorted(sec, r.signal_sec, side='right')          # the first present bar after the signal = the fill bar
            if f < len(sec):
                o['fill_sec'], o['fill_px'] = int(sec[f]), float(vw[f])
                for i in range(f + 1, len(sec) - 1):
                    if sec[i + 1] > r.exit_sec: break                         # the confirmation would fill after the original exit
                    if vw[i] > vw[max(0, i - 60):i].max():                    # a new high over the prior 60 present bars
                        o['brk_sec'], o['brk_px'] = int(sec[i + 1]), float(vw[i + 1]); break
            out.append(o)
    if k % 100 == 0: print(f"  {day}  {k + 1}/{B.td.nunique()}  {time.time() - T0:.0f}s", flush=True)
R = B.merge(pd.DataFrame(out), on=['symbol', 'td', 'signal_sec'], how='left')
R.to_parquet(args.out, index=False)
e = R[R.fill_sec >= 0]
print(f"CONTROL fill: recomputed fill bar = entry_sec on {(e.fill_sec == e.entry_sec).mean():.2%}, |fill_px/entry_px − 1| max {np.abs(e.fill_px / e.entry_px - 1).max():.2e}")
a = R[(R.aux_hi_60_sec >= 0)]
print(f"CONTROL break: vs the engine's aux_hi_60 on {len(a):,} trades: same second {(a.brk_sec == a.aux_hi_60_sec).mean():.2%}, "
      f"same px {(np.abs(a.brk_px / a.aux_hi_60_px - 1) < 1e-6).mean():.2%}")
print(f"done {time.time() - T0:.0f}s")
