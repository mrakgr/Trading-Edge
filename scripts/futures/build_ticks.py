"""The futures tick tape in ONE clean layout, from both of Malcolm's batches (user 2026-10-28).

  <out>/<ROOT>/<expiryYear>_<CONTRACT>.parquet   (default out: /mnt/d/trading-edge-bulk/futures/ticks), one file per
  contract, NOT rolled, one row per FILL in the file's order (NOT sorted: a few fills are stamped about a second
  before the record ahead of them, 6AM12 145; sort on read where order matters), ZSTD, row groups of 1,048,576, the
  source ('scid' / 'parquet') in the file's metadata:
    timestamp  INT64    µs since the Unix epoch, UTC. Before 2020-08-21 whole seconds plus Sierra's row counter; real ms
                        plus a per-fill µs counter from 2020-08-23 (docs/futures_data.md)
    ticker     VARCHAR  the contract (ESZ25)
    price      FLOAT    the fill's price, Sierra's own float32 (87.9 is 87.90000153: round to the root's grid at use)
    size       INTEGER  contracts
    side       TINYINT  +1 the buyer aggressed, −1 the seller
    bid, ask   FLOAT    the quote just before the fill (no sizes: a tick file has none). Before mid-2014 bid = ask = the
                        price: no quote was recorded
    fill_pos   TINYINT  this fill's place in its aggressive order: 0 a single fill, 1 the first of several, 2 a middle
                        one, 3 the last. NULL where Sierra recorded no order structure: before 2017-07-25 05:21:20 UTC
                        (its first sub-trade code in any root), on a price-form record (below), and on a LAST with no
                        open order (its FIRST lost: 6NU19 2019-07-22 03:50:40.001, 6BU20 2020-08-28 20:59:00.001)

THE SOURCES. A contract with a .scid is built from it (all of them since the user's 2026-10-28 download: "I'll want that
column to be filled in exactly"); the first batch's parquet export is used only for a contract without one.
  1. Sierra Chart .scid (--scid; default /mnt/d/trading-edge-bulk/futures/sierra_scid). Sierra uses ONE 40-byte record
     for bars and ticks, so a tick file reuses the bar fields:
       header 56 bytes: "SCID", HeaderSize u32 = 56, RecordSize u32 = 40, ...
       record: DateTime i64 (µs since 1899-12-30) · Open f32 · High f32 = the ask · Low f32 = the bid · Close f32 = the
               price · NumTrades u32 · TotalVolume u32 = the size · BidVolume u32 · AskVolume u32
     A FILL is a record whose size is carried by exactly ONE of Bid/AskVolume (the aggressor's side); it must have
     NumTrades 1. Every other record is not a fill, is dropped, and is counted with its span:
       - no aggressor: Sierra's 1-minute bars (the whole of 72 files of 2008-10 and VXK21, which are left out; the start
         of the 2010-11 contracts; backfilled gaps such as NGU15 2015-07-06..08-27), the 2010-11 aggregates, and VX's
         zero-volume settlement marks (VXF25 2025-01-13 21:01:55, 18.5959);
       - both sides: the 2010-11 aggregates (6EH11, ZSX11).
     Open holds Sierra's sub-trade codes, absurd prices so they can never be read as one: 0 = a single fill (or a middle
     one), 0xfd709ef9 (−1.99900095e37, FIRST_SUB_TRADE_OF_UNBUNDLED_TRADE) = the first fill of one aggressive order that
     hit several resting orders, 0xfd709f01 (−1.99900197e37, LAST_SUB_TRADE...) = its last. The bit patterns are matched
     exactly. A 0 between a first and a last is a middle fill, so fill_pos is read in sequence; an order that does not
     open-then-close (a first inside an order, a last outside one) FAILS the file, except across a recording gap of more
     than a minute, where the open order is cut (ZTU26 stops 2026-07-22 15:00:09 in a sweep and resumes 09-01). A file
     may also END inside one (ZTZ25 stops 2025-09-23 01:46:35 in a sweep); those fills keep their 1 / 2 and the count
     is printed. Within an order every fill has the first's side and, on the real clock, its millisecond (checked on
     200,000 ES / ZT orders 2026-10-28; a break is counted and printed).
     THE PRICE FORM: Open = the price (O = H = L = C): every record before mid-2014 and a stray few later (ZNU20
     2020-05-17 22:00). No quote and no code: bid = ask = the price, fill_pos NULL.
  2. The first batch, already exported to parquet (--parquet; default /mnt/d/trading-edge-bulk/futures/level1_tick:
     trade_price / bid_price / ask_price = round(float64(f32) × 1e9) half away from zero, trade_size, trade_side,
     always-null bid_size / ask_size, event_type = 3). The prices go back to their float32, and every one is checked to
     round-trip exactly; fill_pos is NULL (the export dropped Open). A source without its parquet footer is named.

THE CHECK: every .scid output is compared with the first batch's export of the same contract, field by field (fill_pos
aside), after the export's rows of the records that are not fills are taken out. The export has one row per record,
except in a few 2010-11 files where it left some aggregates out (6EH11); there it is lined up with the records by
timestamp, and every record it left out must be one we dropped too. A difference fails the run (exit 1) and names the
file. It also accepts exactly two kinds of omission: a fill stamped before an earlier record (the export left those
out: 6AM12, 145 about a second back) and a fill after the export's last stamp (the .scid kept recording: MESM26).
Each file is written to .tmp and checked there; only a passing one is renamed into place (a differing one is kept as
.differ), so a kill leaves nothing half-written and a rerun, which skips existing outputs, never skips an unchecked
one. A parquet-derived output is rebuilt once its .scid arrives.

  cd research && python -u scripts/futures/build_ticks.py > logs/futures_build_ticks.log 2>&1; echo exit $?
  a check alone:  ... build_ticks.py --scid <dir of .scid the first batch has> --parquet '' --out <scratch dir>
"""
import argparse
import glob
import os
import re
import time

import numpy as np
import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.parquet as pq

LEVEL1 = '/mnt/d/trading-edge-bulk/futures/level1_tick'
ap = argparse.ArgumentParser()
ap.add_argument('--scid', default='/mnt/d/trading-edge-bulk/futures/sierra_scid', help="a dir of .scid ('' = none)")
ap.add_argument('--parquet', default=LEVEL1, help="the first batch's root dir ('' = none)")
ap.add_argument('--out', default='/mnt/d/trading-edge-bulk/futures/ticks')
ap.add_argument('--no-check', action='store_true', help='skip comparing .scid outputs with the same contract in --parquet-ref')
ap.add_argument('--parquet-ref', default=LEVEL1)
a = ap.parse_args()

ROWS = 1 << 20
EPOCH_US = 25569 * 86400 * 1_000_000          # 1899-12-30 -> 1970-01-01
FIRST, LAST = 0xfd709ef9, 0xfd709f01
GAP_US = 60_000_000                           # a recording gap: no order spans a minute
MARKED_FROM = 1_500_960_080_000_000 + EPOCH_US  # 2017-07-25 05:21:20 UTC: Sierra's first sub-trade code in any root (6NU17;
                                              # every root starts within minutes). Before it fill_pos is NULL: no codes
CLOCK_FROM = 1_598_140_800_000_000 + EPOCH_US   # 2020-08-23 00:00 UTC: a real ms clock (the row counter before)
REC = np.dtype([('t', '<i8'), ('o', '<u4'), ('h', '<f4'), ('l', '<f4'), ('c', '<f4'),
                ('n', '<u4'), ('v', '<u4'), ('bv', '<u4'), ('av', '<u4')])     # Open read as its bits
SCID_NAME = re.compile(r'^(?P<c>(?P<root>.+?)[FGHJKMNQUVXZ](?P<yy>\d{2}))(?:-|_FUT_)\w+\.scid$')
SCHEMA = pa.schema([
    pa.field('timestamp', pa.int64(), nullable=False), pa.field('ticker', pa.string(), nullable=False),
    pa.field('price', pa.float32()), pa.field('size', pa.int32()), pa.field('side', pa.int8()),
    pa.field('bid', pa.float32()), pa.field('ask', pa.float32()), pa.field('fill_pos', pa.int8())])


class Bad(Exception): pass


class Differ(Exception): pass


def us(t):
    """a Sierra DateTime as UTC text"""
    return time.strftime('%Y-%m-%d %H:%M', time.gmtime((t - EPOCH_US) / 1e6))


def table(ts, contract, price, size, side, bid, ask, fill_pos):
    n = len(ts)
    return pa.table([pa.array(ts, pa.int64()), pa.array(np.full(n, contract, dtype=object), pa.string()),
                     price, size, side, bid, ask, fill_pos], schema=SCHEMA)


def write(dst, batches, kind, verify):
    """batches yields tables; written to .tmp with the source kind in the file's metadata, then verify(tmp) (the check):
    renamed to dst only when it passes (None), so a rerun never skips an unchecked or differing file; a differing one
    is kept as dst.differ and raises Differ. Returns the rows (0: nothing is written)."""
    tmp, n = dst + '.tmp', 0
    try:
        with pq.ParquetWriter(tmp, SCHEMA.with_metadata({'source': kind}), compression='zstd') as w:
            for t in batches:
                w.write_table(t, row_group_size=ROWS); n += t.num_rows
        bad = verify(tmp) if n else None
    except BaseException:
        if os.path.exists(tmp): os.remove(tmp)
        raise
    if not n: os.remove(tmp)
    elif bad: os.replace(tmp, dst + '.differ'); raise Differ(bad)
    else: os.replace(tmp, dst)
    return n


def from_scid(src, contract, stats, dropped):
    """dropped collects the file indices of the records that are not fills (for the check)"""
    with open(src, 'rb') as f: h = f.read(56)
    if h[:4] != b'SCID' or int.from_bytes(h[4:8], 'little') != 56 or int.from_bytes(h[8:12], 'little') != 40:
        raise Bad('not a 56/40 SCID file')
    recs = np.memmap(src, dtype=REC, offset=56, mode='r')
    stats['records'] = len(recs)
    depth, prev_side, prev_ms, prev_t, last_first = 0, 0, -1, -1, -1   # carried across chunks: inside an order?, the last
                                                                        # fill's side / ms / µs, the latest first fill's row
    for i in range(0, len(recs), ROWS):
        r = np.asarray(recs[i:i + ROWS])
        buy, sell = r['av'] > 0, r['bv'] > 0
        fill = buy ^ sell                              # one aggressor: a fill. None = a minute bar, an aggregate, a VX
        if not fill.all():                             # settlement mark; both = an aggregate (2010-11). Not fills.
            k = np.flatnonzero(~fill)
            stats['no aggressor'] += int((~buy[k] & ~sell[k]).sum()); stats['both sides'] += int((buy[k] & sell[k]).sum())
            stats['dropped first'] = stats['dropped first'] or int(r['t'][k[0]]); stats['dropped last'] = int(r['t'][k[-1]])
            dropped.append(i + k); r = r[fill]
            if not len(r): continue
        o = r['o']
        priced = o == r['c'].view('<u4')               # the PRICE form (O = H = L = C, no quote, no codes): before mid-2014,
        coded = np.isin(o, (0, FIRST, LAST))           # and a stray few later (ZNU20 2020-05-17 22:00)
        if not (coded | priced).all(): raise Bad(f'{int((~coded & ~priced).sum()):,} fills with an unknown Open code')
        if (r['n'] != 1).any(): raise Bad('a fill with a NumTrades other than 1')
        if (r['v'] != r['bv'] + r['av']).any(): raise Bad('a fill whose Bid/AskVolume do not carry exactly its size')
        stats['price form'] += int(priced.sum())
        t = r['t']
        delta = (o == FIRST).astype(np.int64) - (o == LAST)
        after = depth + np.cumsum(delta)
        gap = np.diff(np.concatenate([[prev_t], t])) > GAP_US
        orphan = np.zeros(len(r), bool)
        while True:                                    # in time order: an order still open across a recording gap is cut
            cut = np.flatnonzero(gap & (after - delta == 1) & ~orphan)   # at the gap (ZTU26 stops 2026-07-22 15:00:09 in a sweep, back
            lost = np.flatnonzero(after < 0)                   # 09-01); a LAST with no open order lost its FIRST (6NU19
            c, l = (cut[0] if len(cut) else len(r)), (lost[0] if len(lost) else len(r))   # 2019-07-22 03:50:40.001: the
            if c == l == len(r): break                                                    # .000 record is missing): its
            if c < l: after[c:] -= 1; stats['cut orders'] += 1                            # fill_pos is NULL
            else: after[l:] += 1; orphan[l] = True; stats['orphan lasts'] += 1           # (an orphan right after a gap,
                                                                                          # 6NU19's, is never then read as
                                                                                          # a cut: that looped, 2026-10-29)
        if ((after < 0) | (after > 1)).any():
            j = int(np.flatnonzero((after < 0) | (after > 1))[0])
            raise Bad(f'fill {i + j:,}: a {"first" if o[j] == FIRST else "last"} code out of sequence')
        fill_pos = np.where(o == FIRST, 1, np.where(o == LAST, 3, np.where(after == 1, 2, 0))).astype(np.int8)
        unknown = priced | (t < MARKED_FROM) | orphan
        if ((o != 0) & ~priced & (t < MARKED_FROM)).any():
            raise Bad('a sub-trade code before 2017-07-25 05:21:20 UTC')
        stats['unmarked'] += int((~priced & (t < MARKED_FROM)).sum())
        side = np.where(r['av'] > 0, 1, -1).astype(np.int8)
        ms = t // 1000
        inner = (fill_pos >= 2) & ~unknown             # a middle or last fill: its previous fill is in the same order
        ps, pm = np.concatenate([[prev_side], side[:-1]]), np.concatenate([[prev_ms], ms[:-1]])
        stats['side breaks'] += int((inner & (side != ps)).sum()); stats['ms breaks'] += int((inner & (ms != pm) & (t >= CLOCK_FROM)).sum())
        for k in range(4): stats[f'pos{k}'] += int((fill_pos[~unknown] == k).sum())
        firsts = np.flatnonzero(o == FIRST)
        if len(firsts): last_first = i + int(firsts[-1])
        depth, prev_side, prev_ms, prev_t = int(after[-1]), int(side[-1]), int(ms[-1]), int(t[-1])
        yield table(t - EPOCH_US, contract, pa.array(r['c']), pa.array(r['v'].astype(np.int32)), pa.array(side),
                    pa.array(r['l']), pa.array(r['h']), pa.array(fill_pos, mask=unknown if unknown.any() else None))
    if depth: stats['open tail'] = len(recs) - last_first   # the recording stopped mid-order (ZTZ25 2025-09-23)


def nanos(f32):
    """round(float64(f32) x 1e9) as the first batch's export rounds it: half AWAY from zero (MGCJ26: 4640.2001953125)"""
    v = f32.astype(np.float64) * 1e9
    return np.sign(v) * np.floor(np.abs(v) + 0.5)


def f32_of_nanos(col, what):
    """the first batch's ×1e9 int64 back to its float32; every value must round-trip exactly"""
    x = col.to_numpy(zero_copy_only=False)
    valid = ~np.isnan(x) if x.dtype.kind == 'f' else np.ones(len(x), bool)
    f = (x / 1e9).astype(np.float32)
    back = nanos(f)
    if (back[valid] != x[valid]).any(): raise Bad(f'{what}: {int((back[valid] != x[valid]).sum()):,} prices do not round-trip')
    return pa.array(f, pa.float32(), mask=~valid if not valid.all() else None)


def from_parquet(src, contract, stats):
    pf = pq.ParquetFile(src)
    for g in range(pf.num_row_groups):
        t = pf.read_row_group(g)
        if pc.any(pc.not_equal(t['event_type'], 3)).as_py(): raise Bad('an event_type other than a trade')
        if pc.any(pc.is_valid(t['bid_size'])).as_py() or pc.any(pc.is_valid(t['ask_size'])).as_py():
            raise Bad('a quote size: the layout assumed none')
        if pc.any(pc.not_equal(t['ticker'], contract)).as_py(): raise Bad('a ticker other than the file name')
        sz = t['trade_size'].to_numpy(zero_copy_only=False)
        if (sz[~np.isnan(sz)] != np.rint(sz[~np.isnan(sz)])).any(): raise Bad('a fractional size')
        stats['null prices'] += t['trade_price'].null_count
        yield table(t['timestamp'].to_numpy(), contract, f32_of_nanos(t['trade_price'], 'trade_price'),
                    pc.cast(t['trade_size'], pa.int32()), t['trade_side'],
                    f32_of_nanos(t['bid_price'], 'bid_price'), f32_of_nanos(t['ask_price'], 'ask_price'),
                    pa.nulls(t.num_rows, pa.int8()))


def source(dst):
    """'scid' or 'parquet': the file's metadata; the first run (2026-10-28) wrote none, and its parquet-derived files are
    the ones with fill_pos NULL throughout (from the footer's statistics)"""
    md = pq.read_schema(dst).metadata or {}
    if b'source' in md: return md[b'source'].decode()
    m = pq.ParquetFile(dst).metadata; i = m.schema.names.index('fill_pos')
    return 'parquet' if sum(m.row_group(g).column(i).statistics.null_count for g in range(m.num_row_groups)) == m.num_rows else 'scid'


def check(dst, ref, src, dropped, stats):
    """the export has one row per RECORD, except in a few 2010-11 files, where it left some of the aggregates out (6EH11:
    NumTrades > 1 and both-sided records, yet 6AH11 kept its NumTrades-0 bars). There the export is lined up with the
    records as a subsequence by timestamp, and every record it left out must be one we dropped too. Its rows of the
    records that are not fills are taken out, then it must equal ours field by field."""
    if not os.path.exists(ref): return 'no reference'
    try: pq.ParquetFile(ref)
    except Exception: return 'no reference (an empty or truncated export)'
    A, B = pq.read_table(dst), pq.read_table(ref)
    recs = np.memmap(src, dtype=REC, offset=56, mode='r')
    keep = np.ones(len(recs), bool)
    if dropped: keep[np.concatenate(dropped)] = False
    if B.num_rows != len(recs):
        rt, bt, i, j = (np.asarray(recs['t']) - EPOCH_US).tolist(), B['timestamp'].to_numpy().tolist(), 0, 0
        exported = np.zeros(len(rt), bool)
        while i < len(rt) and j < len(bt):
            if rt[i] == bt[j]: exported[i] = True; j += 1
            i += 1
        if j < len(bt): return f'ROWS DIFFER: {len(bt) - j:,} exported rows match no record'
        t = np.asarray(recs['t'])                      # the export also left out the fills stamped before an earlier
        late = np.concatenate([[False], t[1:] < np.maximum.accumulate(t)[:-1]])   # record (6AM12: 145, about a second
        after_end = t - EPOCH_US > bt[-1]                                         # back), and the .scid holds fills
        unexplained = keep & ~exported & ~late & ~after_end                       # recorded after the export (MESM26)
        if unexplained.any(): return f'ROWS DIFFER: {int(unexplained.sum()):,} of our fills are not in the export'
        stats['late'] += int((keep & ~exported & late).sum()); stats['after export'] += int((keep & ~exported & after_end).sum())
        A = A.filter(pa.array((keep & exported)[keep]))   # ours: only the fills the export has
        keep = keep[exported]
    B = B.filter(pa.array(keep))
    same = {'timestamp': A['timestamp'].equals(B['timestamp']), 'ticker': A['ticker'].equals(B['ticker']),
            'price': (nanos(A['price'].to_numpy()) == B['trade_price'].to_numpy()).all(),
            'bid': (nanos(A['bid'].to_numpy()) == B['bid_price'].to_numpy()).all(),
            'ask': (nanos(A['ask'].to_numpy()) == B['ask_price'].to_numpy()).all(),
            'size': (A['size'].to_numpy() == B['trade_size'].to_numpy()).all(),
            'side': (A['side'].to_numpy() == B['trade_side'].to_numpy()).all()}
    bad = [c for c, ok in same.items() if not ok]
    return 'IDENTICAL' if not bad else f'DIFFER: {bad}'


jobs = []                                              # (kind, src, root, contract, out name)
if a.scid:
    for src in sorted(glob.glob(os.path.join(a.scid, '*.scid'))):
        m = SCID_NAME.match(os.path.basename(src))
        if not m: print(f'{os.path.basename(src)}: not a contract file name, left out', flush=True); continue
        if os.path.getsize(src) <= 56: print(f'{os.path.basename(src)}: header only, left out', flush=True); continue
        jobs.append(('scid', src, m['root'], m['c'], f'{2000 + int(m["yy"])}_{m["c"]}.parquet'))
if a.parquet:
    for src in sorted(glob.glob(os.path.join(a.parquet, '*', '*.parquet'))):
        name = os.path.basename(src)
        jobs.append(('parquet', src, os.path.basename(os.path.dirname(src)), name[5:-8], name))

t0, done, skipped, failed, differ, barred = time.time(), 0, 0, [], [], []
has_scid = {(root, name) for kind, _, root, _, name in jobs if kind == 'scid'}
for k, (kind, src, root, contract, name) in enumerate(jobs, 1):
    dst, at = os.path.join(a.out, root, name), f'[{k}/{len(jobs)}] {root}/{name}'
    if kind == 'parquet' and (root, name) in has_scid: skipped += 1; continue   # the .scid is the source when it exists
    if os.path.exists(dst):
        if kind == 'scid' and source(dst) == 'parquet': print(f'{at}: the parquet-derived file is rebuilt from its .scid', flush=True)
        else: skipped += 1; continue
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if kind == 'parquet':
        try: pq.ParquetFile(src)
        except Exception:
            print(f'{at}: unreadable parquet ({os.path.getsize(src):,} bytes: an empty or truncated export), left out', flush=True)
            continue
    s, stats, dropped, checked = time.time(), __import__('collections').Counter(), [], []
    def verify(tmp):
        if kind != 'scid' or a.no_check: return None
        c = check(tmp, os.path.join(a.parquet_ref, root, name), src, dropped, stats); checked.append(c)
        return c if c.startswith(('DIFFER', 'ROWS')) else None
    try:
        n = write(dst, from_scid(src, contract, stats, dropped) if kind == 'scid' else from_parquet(src, contract, stats),
                  kind, verify)
    except Bad as e:
        failed.append(f'{root}/{name}'); print(f'{at}: ⚠ FAILED ({kind}): {e}', flush=True); continue
    except Differ as e:
        differ.append(f'{root}/{name}'); print(f'{at}: ⚠ check: {e}, kept as {name}.differ', flush=True); continue
    if n == 0:                                         # minute bars only (2008-10, VXK21): no ticks in the file
        if os.path.exists(dst): os.remove(dst)         # (the first run's output, from the bars' export)
        barred.append(f'{root}/{name}')
        print(f'{at}: no fills in {stats["records"]:,} records (minute bars or aggregates only), left out', flush=True); continue
    done += 1
    msg = f'{at} from {kind}: {n:,} fills in {time.time() - s:.1f} s'
    if kind == 'scid':
        msg += (f'; {stats["pos0"]:,} single, {stats["pos1"]:,} multi-fill orders holding '
                f'{stats["pos1"] + stats["pos2"] + stats["pos3"]:,} fills')
        if stats['price form']: msg += f'; {stats["price form"]:,} in the price form (no quote, fill_pos NULL)'
        if stats['unmarked']: msg += f'; {stats["unmarked"]:,} before the codes began (fill_pos NULL)'
        if stats['no aggressor'] or stats['both sides']:
            msg += (f'; dropped {stats["no aggressor"]:,} records with no aggressor and {stats["both sides"]:,} with both '
                    f'(bars / aggregates / settlement marks, {us(stats["dropped first"])} .. {us(stats["dropped last"])})')
        if stats['side breaks'] or stats['ms breaks']:
            msg += f'; ⚠ inside an order: {stats["side breaks"]:,} side breaks, {stats["ms breaks"]:,} ms breaks'
        if stats['cut orders']: msg += f'; {stats["cut orders"]} orders cut by a recording gap'
        if stats['orphan lasts']: msg += f'; {stats["orphan lasts"]} last codes with no open order (their first lost; NULL)'
        if stats['open tail']: msg += f'; the file ends inside an order ({stats["open tail"]} fills, no last code)'
        if checked:
            msg += f'; check: {checked[0]}'
            if stats['late'] or stats['after export']:
                msg += f' (the export lacks {stats["late"]:,} back-stamped fills, {stats["after export"]:,} recorded after it)'
    elif stats['null prices']: msg += f'; {stats["null prices"]:,} null prices'
    print(msg, flush=True)
print(f'done: {done} written, {skipped} already there or superseded by a .scid, {len(barred)} without fills {barred}, '
      f'{len(failed)} failed {failed}, {len(differ)} differing from the first batch {differ} in {time.time() - t0:.0f} s',
      flush=True)
if failed or differ: raise SystemExit(1)
