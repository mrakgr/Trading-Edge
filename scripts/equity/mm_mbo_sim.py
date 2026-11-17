r"""MARKET-MAKING SIM on Databento MBO (user 2026-11-17: "make a simulator that works on DataBento MBO data. We'll want to at
least get the market maker working on CBRS during its IPO day"). The cheap-data sim (mm_sim.py, trades + NBBO) needed a
queue MODEL; here the queue is EXACT: every venue's order book is rebuilt order by order.

THE BOOKS. Each venue's file (<mbo>/<TICKER>/<date>/<VENUE>.dbn.zst) is folded in ts_recv order (what a participant sees):
  A add · C cancel — FULL OR PARTIAL: the record's size is the amount removed. A fill's book effect is the C that follows
  it (T = the print, aggressor side, order_id 0; F = the passive order, informational; then C). Checked on CBRS 05-14:
  every venue, 96–100 % of the orders are removed by C's summing to their added size, and every filled order's C's
  sum to its full size.
  M modify (same price + smaller size keeps priority; else to the back) · R clear the venue.
Every order carries an arrival seq; our order's queue = the size AT OUR (venue, side, price) with seq < ours (exact).

OUR ORDERS (one per side, on --venue, after --lat; one action in flight per side, the old order live until it lands):
  OWN-VENUE fill: a print on our venue whose aggressor hits our side, at our price or through it, while nothing is ahead
    of us: we take min(remaining, the print) — the aggressor would have reached us before the order it hit.
  CROSS-VENUE fill (Reg NMS order protection): a print on another venue STRICTLY through
    our price while no real order rests at our price or better anywhere (the real NBB < our bid): with ours displayed,
    that flow would have been routed to us. At our price.
  OUTBOUND routing: an order landing MARKETABLE takes the real consolidated book across venues up to its limit (taker
    fee); the rest rests at the limit. --post-only: it is cancelled instead.
  Side-N prints (auction / hidden) never fill us. Our fills do not remove real orders (a small-size approximation).
FEES per share: 0.2c commission; the add credit of --venue (ARCA 0.20c, XNAS 0.15c, EDGX/BATS 0.16c); take 0.30c.
THE PROTECTED NBBO (what the rules quote against, and the SIP's): per venue the best price with >= --rl shares (100 on
CBRS 05-14: the rebuilt NBBO matches Massive's SIP 98 % of 1 s points, 82 % at the 2025 tier's 40).
THE RULES: join (the protected NBB / NBO), penny (one tick inside on a 3+ tick spread), with the inventory cap (--cap lots).
At the end of each window the inventory is closed taking the real book.

  cd research && python -u scripts/equity/mm_mbo_sim.py --ticker CBRS --date 2026-05-14 > logs/mm_mbo_sim_cbrs.log 2>&1; echo exit $?
"""
import argparse, datetime, glob, json, math, os, time, zoneinfo
import numpy as np, pandas as pd
from numba import njit
from numba import types as nbt
from numba.typed import Dict

ap = argparse.ArgumentParser()
ap.add_argument('--mbo', default='/mnt/c/Users/mrakg/.tapesim/data/databento/mbo')
ap.add_argument('--ticker', default='CBRS')
ap.add_argument('--date', default='2026-05-14')
ap.add_argument('--windows', default='13:05:02-13:35,13:08-13:38,13:05:02-16:00', help='ET start-end, comma-separated')
ap.add_argument('--rules', default='join,penny')
ap.add_argument('--venue', default='ARCX')
ap.add_argument('--lat', type=float, default=0.15)
ap.add_argument('--lot-usd', type=float, default=2000.0)
ap.add_argument('--cap', type=int, default=3)
ap.add_argument('--post-only', action='store_true')
ap.add_argument('--front', action='store_true', help='OPTIMISTIC: our order is first in line at its price')
ap.add_argument('--out', default='data/equity/mm_sim/mbo')
ap.add_argument('--signal', help='a JSON {"w": [6 feature weights + intercept], "thr": bp}: the predicted mid move (bp) of the '
                'order-book signal; the rules join+sig / penny+sig pull the bid below -thr and the ask above +thr')
ap.add_argument('--thr', type=float, help='override the signal JSON threshold (bp)')
ap.add_argument('--momentum', action='store_true', help='SECONDARY-SYSTEM TEST (user 11-17): each time the maker inventory reaches the cap, '
                'a taker enters AGAINST it (maker long -> sell at the bid) after --mom-lat and exits by taking after H s')
ap.add_argument('--mom-lat', type=float, default=0.15)
ap.add_argument('--dump-features', action='store_true', help='write the 100 ms feature grid of each window (the first rule)')
ap.add_argument('--rl', type=int, default=100, help='the round lot (default: the 2025 tier by price; an IPO with no prior month may be 100)')
ap.add_argument('--check-nbbo', default='data/equity/realized_spread/quotes', help='the Massive NBBO cache, to validate the rebuilt book')
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
ADD_CREDIT = {'ARCX': 0.0020, 'XNAS': 0.0015, 'EDGX': 0.0016, 'BATS': 0.0016, 'EDGA': 0.0027}
COMM, TAKE = 0.002, 0.003
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:6.1f}s]', *x, flush=True)


def ns_at(d, hms):
    y, m, dd = map(int, d.split('-')); p = [float(x) for x in hms.split(':')]
    return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int((p[0] * 3600 + p[1] * 60 + (p[2] if len(p) > 2 else 0)) * 1e9)


def load():
    import databento as db
    files = sorted(glob.glob(f'{a.mbo}/{a.ticker}/{a.date}/*.dbn.zst'))
    venues = [os.path.basename(f).split('.')[0] for f in files]
    parts = []
    for vi, f in enumerate(files):
        df = db.DBNStore.from_file(f).to_df(map_symbols=False, pretty_ts=False, price_type='float')
        parts.append(pd.DataFrame(dict(ts=df.index.to_numpy(np.int64), ven=np.full(len(df), vi, np.int8),
                                       act=df.action.to_numpy('S1').view(np.uint8), side=df.side.to_numpy('S1').view(np.uint8),
                                       px=df.price.to_numpy(float), size=df['size'].to_numpy(np.float64),
                                       oid=df.order_id.to_numpy(np.uint64).astype(np.int64))))
    D = pd.concat(parts, ignore_index=True).sort_values('ts', kind='stable').reset_index(drop=True)
    return D, venues


A_, C_, M_, R_, T_, F_ = (ord(c) for c in 'ACMRTF')
SB, SA = ord('B'), ord('A')
INF = 1e18


@njit(cache=True)
def run(ts, ven, act, side, tk, size, oid, nven, ntick, t0, t1, mode, myv, lot, cap, lat, front, post_only, credit, tick_usd, base, rl,
        wsig, thr, gstep):
    """one window: the books are folded from the first record; we quote in [t0, t1). Returns the fills and the real NBBO
    (in ticks, −1 = none) after each record from t0 on."""
    lv = np.zeros((nven, 2, ntick))                       # side 0 = bid, 1 = ask
    best = np.full((nven, 2), -1, np.int64)
    okey = Dict.empty(key_type=nbt.int64, value_type=nbt.int64)
    o_ven = np.zeros(len(ts) + 1, np.int64); o_side = np.zeros(len(ts) + 1, np.int64); o_tk = np.zeros(len(ts) + 1, np.int64)
    o_sz = np.zeros(len(ts) + 1); o_seq = np.zeros(len(ts) + 1, np.int64)
    nord = 0; seq = 0
    # our orders
    live = np.zeros(2, np.bool_); lpx = np.zeros(2, np.int64); rem = np.zeros(2); ahead = np.zeros(2); myseq = np.zeros(2, np.int64)
    pend = np.zeros(2, np.bool_); ptime = np.zeros(2); ppx = np.zeros(2, np.int64); pcan = np.zeros(2, np.bool_)
    q = 0.0
    nmax = 500000
    ft = np.zeros(nmax); fs = np.zeros(nmax); fp = np.zeros(nmax); fq = np.zeros(nmax); fk = np.zeros(nmax, np.int64)  # kind 0 own 1 cross 2 take 3 flatten
    nf = 0; nact = 0
    a0 = np.zeros(2); lvl0 = np.zeros(2); fa0 = np.zeros(nmax); flv0 = np.zeros(nmax)   # the queue ahead / the level size at placement
    rec_t = np.zeros(len(ts)); rec_b = np.full(len(ts), -1, np.int64); rec_a = np.full(len(ts), -1, np.int64); nr = 0
    # THE ORDER-BOOK FEATURES (causal: the state after the previous record), all in [-1, 1]:
    #  0 imb1   (B − A)/(B + A), all sizes at the protected best prices, all venues
    #  1 imb5   the same over 5 ticks from each best
    #  2, 3 ofi the Cont–Kukanov–Stoikov L1 order-flow imbalance, EWMA(e)/EWMA(|e|), half-life-ish τ = 1 s, 5 s
    #  4, 5 tf  the signed aggressor volume (T records), EWMA(signed)/EWMA(total), τ = 1 s, 5 s
    NF = 6
    feat = np.zeros(NF); have = False
    taus = np.array([1e9, 5e9]); e_ofi = np.zeros(2); a_ofi = np.zeros(2); e_tf = np.zeros(2); a_tf = np.zeros(2); tl = 0.0
    pb = -1; pa = -1; pbs = 0.0; pas = 0.0
    ng = int((t1 - t0) / gstep) + 2 if gstep > 0 else 1
    gt = np.zeros(ng); gm = np.zeros(ng); gf = np.zeros((ng, NF)); ngr = 0; gnext = t0
    mid_last = 0.0

    def nbb():
        b = -1
        for v in range(nven):
            if best[v, 0] > b: b = best[v, 0]
        return b

    def nbo():
        x = -1
        for v in range(nven):
            if best[v, 1] >= 0 and (x < 0 or best[v, 1] < x): x = best[v, 1]
        return x

    def pnbb():                                          # the PROTECTED best bid: a level with >= one round lot (the SIP's)
        b = -1
        for v in range(nven):
            k = best[v, 0]
            while k > b and lv[v, 0, k] < rl: k -= 1
            if k > b: b = k
        return b

    def pnbo():
        x = -1
        for v in range(nven):
            k = best[v, 1]
            if k < 0: continue
            while k < ntick and (x < 0 or k < x) and lv[v, 1, k] < rl: k += 1
            if k < ntick and (x < 0 or k < x): x = k
        return x

    for i in range(len(ts)):
        t = ts[i]
        if t >= t1: break
        v = ven[i]; ac = act[i]
        while gstep > 0 and t >= gnext and gnext < t1:      # the grid samples the state BEFORE this record
            if have and ngr < ng:
                gt[ngr] = gnext; gm[ngr] = mid_last
                for f in range(NF): gf[ngr, f] = feat[f]
                ngr += 1
            gnext += gstep
        # 1. our actions in flight landing before this record
        if t >= t0:
            for s in range(2):
                if pend[s] and ptime[s] <= t:
                    pend[s] = False
                    if pcan[s]:
                        live[s] = False; continue
                    p = ppx[s]; need = lot
                    ob = nbo() if s == 0 else nbb()
                    marketable = ob >= 0 and ((s == 0 and p >= ob) or (s == 1 and p <= ob))
                    if marketable and post_only:
                        live[s] = False; continue
                    if marketable:                       # route out: take the real book up to the limit
                        k = ob
                        while need > 1e-9 and ((s == 0 and k <= p) or (s == 1 and k >= p)) and 0 <= k < ntick:
                            avail = 0.0
                            for w in range(nven): avail += lv[w, 1 - s, k]
                            got = min(need, avail)
                            if got > 0 and nf < nmax:
                                ft[nf] = ptime[s]; fs[nf] = 1 if s == 0 else -1; fp[nf] = (k + base) * tick_usd; fq[nf] = got; fk[nf] = 2; nf += 1
                                q += got if s == 0 else -got; need -= got
                            k += 1 if s == 0 else -1
                    if need > 1e-9:
                        live[s] = True; lpx[s] = p; rem[s] = need; myseq[s] = seq; seq += 1
                        ahead[s] = 0.0 if front else lv[myv, s, p]
                        a0[s] = ahead[s]
                        lvl0[s] = 0.0
                        for w_ in range(nven): lvl0[s] += lv[w_, s, p]
                    else:
                        live[s] = False
        # 2. the record
        if ac == T_ and t >= t0:
            sa = side[i]; p_t = tk[i]; z = size[i]
            if sa == SA or sa == SB:
                s = 0 if sa == SA else 1                 # a sell aggressor hits bids (our side 0)
                if live[s]:
                    p = lpx[s]
                    through = p_t < p if s == 0 else p_t > p
                    at = p_t == p
                    got = 0.0; kind = 0
                    if v == myv:
                        if (through or at) and ahead[s] <= 1e-9: got = min(rem[s], z); kind = 0
                    elif through:
                        rb = nbb() if s == 0 else nbo()
                        clear = rb < p if s == 0 else (rb < 0 or rb > p)
                        if clear: got = min(rem[s], z); kind = 1
                    if got > 0:
                        sd = 1 if s == 0 else -1
                        if nf < nmax:
                            ft[nf] = t; fs[nf] = sd; fp[nf] = (p + base) * tick_usd; fq[nf] = got; fk[nf] = kind; fa0[nf] = a0[s]; flv0[nf] = lvl0[s]; nf += 1
                        q += sd * got; rem[s] -= got
                        if rem[s] <= 1e-9: live[s] = False
        elif ac == A_:
            sd = 0 if side[i] == SB else 1
            k = tk[i]
            if 0 <= k < ntick:
                okey[oid[i] * 16 + v] = nord
                o_ven[nord] = v; o_side[nord] = sd; o_tk[nord] = k; o_sz[nord] = size[i]; o_seq[nord] = seq; seq += 1; nord += 1
                lv[v, sd, k] += size[i]
                if sd == 0 and k > best[v, 0]: best[v, 0] = k
                if sd == 1 and (best[v, 1] < 0 or k < best[v, 1]): best[v, 1] = k
        elif ac == C_ or ac == M_:
            key = oid[i] * 16 + v
            if key in okey:
                j = okey[key]; sd = o_side[j]; k = o_tk[j]
                if ac == C_:
                    dz = min(size[i], o_sz[j])
                    newk = k; newsz = o_sz[j] - dz; keep = True
                else:
                    newk = tk[i]; newsz = size[i]; dz = o_sz[j]
                    keep = (newk == k and newsz <= o_sz[j])
                    if keep: dz = o_sz[j] - newsz
                # our queue: an order AHEAD of ours at our level shrinking / leaving / losing priority
                for s in range(2):
                    if live[s] and s == sd and v == myv and k == lpx[s] and o_seq[j] < myseq[s]:
                        ahead[s] -= dz if keep else o_sz[j]
                        if ahead[s] < 0: ahead[s] = 0.0
                lv[v, sd, k] -= dz if keep else o_sz[j]
                if lv[v, sd, k] < 1e-9: lv[v, sd, k] = 0.0
                if keep:
                    o_sz[j] = newsz
                    if newsz <= 1e-9: del okey[key]
                else:
                    del okey[key]
                    if ac == M_ and newsz > 0 and 0 <= newk < ntick:
                        okey[key] = nord
                        o_ven[nord] = v; o_side[nord] = sd; o_tk[nord] = newk; o_sz[nord] = newsz; o_seq[nord] = seq; seq += 1; nord += 1
                        lv[v, sd, newk] += newsz
                        if sd == 0 and newk > best[v, 0]: best[v, 0] = newk
                        if sd == 1 and (best[v, 1] < 0 or newk < best[v, 1]): best[v, 1] = newk
                # repair the venue's best
                if sd == 0 and best[v, 0] == k and lv[v, 0, k] == 0:
                    kk = k
                    while kk >= 0 and lv[v, 0, kk] == 0: kk -= 1
                    best[v, 0] = kk
                if sd == 1 and best[v, 1] == k and lv[v, 1, k] == 0:
                    kk = k
                    while kk < ntick and lv[v, 1, kk] == 0: kk += 1
                    best[v, 1] = kk if kk < ntick else -1
        elif ac == R_:
            for sd in range(2):
                for k in range(ntick): lv[v, sd, k] = 0.0
                best[v, sd] = -1
        # the feature state
        if t > tl:
            for h in range(2):
                dec = math.exp(-(t - tl) / taus[h]); e_ofi[h] *= dec; a_ofi[h] *= dec; e_tf[h] *= dec; a_tf[h] *= dec
            tl = t
        if ac == T_:
            sg = 1.0 if side[i] == SB else (-1.0 if side[i] == SA else 0.0)
            if sg != 0:
                for h in range(2):
                    e_tf[h] += sg * size[i]; a_tf[h] += size[i]
        if t < t0 - 600e9: continue
        b = pnbb(); x = pnbo()
        if b >= 0 and x > b:
            bs = 0.0; as_ = 0.0
            for w_ in range(nven):
                bs += lv[w_, 0, b]; as_ += lv[w_, 1, x]
            if pb >= 0:
                e = 0.0
                if b >= pb: e += bs
                if b <= pb: e -= pbs
                if x <= pa: e -= as_
                if x >= pa: e += pas
                for h in range(2):
                    e_ofi[h] += e; a_ofi[h] += abs(e)
            pb = b; pa = x; pbs = bs; pas = as_
            if t >= t0:
                b5 = 0.0; a5 = 0.0
                for k5 in range(5):
                    for w_ in range(nven):
                        if b - k5 >= 0: b5 += lv[w_, 0, b - k5]
                        if x + k5 < ntick: a5 += lv[w_, 1, x + k5]
                feat[0] = (bs - as_) / max(bs + as_, 1.0); feat[1] = (b5 - a5) / max(b5 + a5, 1.0)
                for h in range(2):
                    feat[2 + h] = e_ofi[h] / a_ofi[h] if a_ofi[h] > 0 else 0.0
                    feat[4 + h] = e_tf[h] / a_tf[h] if a_tf[h] > 0 else 0.0
                mid_last = 0.5 * (b + x + 2 * base) * tick_usd; have = True
        if t < t0: continue
        rec_t[nr] = t; rec_b[nr] = b; rec_a[nr] = x; nr += 1
        if b < 0 or x < 0 or x <= b: continue
        # 3. the desired quotes
        ql = q / lot
        sig = wsig[NF]
        for f in range(NF): sig += wsig[f] * feat[f]
        for s in range(2):
            if pend[s]: continue
            allowed = ql < cap - 1e-9 if s == 0 else ql > -cap + 1e-9
            if mode >= 2 and ((s == 0 and sig < -thr) or (s == 1 and sig > thr)): allowed = False   # the signal pulls the side about to be run over
            want = -1
            if allowed:
                if mode == 0 or mode == 3: want = b if s == 0 else x
                else:
                    if x - b >= 3: want = b + 1 if s == 0 else x - 1
                    else: want = b if s == 0 else x
            if allowed:
                if live[s] and lpx[s] == want: continue
                pend[s] = True; pcan[s] = False; ppx[s] = want; ptime[s] = t + lat; nact += 1
            elif live[s]:
                pend[s] = True; pcan[s] = True; ptime[s] = t + lat; nact += 1
    # flatten: take the real book
    s = 1 if q > 0 else 0
    k = nbb() if q > 0 else nbo()
    while abs(q) > 1e-9 and 0 <= k < ntick and nf < nmax:
        avail = 0.0
        for w in range(nven): avail += lv[w, 0 if q > 0 else 1, k]
        got = min(abs(q), avail)
        if got > 0:
            ft[nf] = t1; fs[nf] = -1 if q > 0 else 1; fp[nf] = (k + base) * tick_usd; fq[nf] = got; fk[nf] = 3; nf += 1
            q += -got if q > 0 else got
        k += -1 if q > 0 else 1
    return ft[:nf], fs[:nf], fp[:nf], fq[:nf], fk[:nf], fa0[:nf], flv0[:nf], nact, rec_t[:nr], rec_b[:nr], rec_a[:nr], gt[:ngr], gm[:ngr], gf[:ngr]


if __name__ == '__main__':
    D, venues = load()
    log(f'{a.ticker} {a.date}: {len(D):,} records, venues {venues}')
    tick_usd = 0.01
    tr = D[(D.act == T_) & (D.px > 0)]
    p_ref = float(tr.px.median())
    base = int(round(p_ref * 0.3 / tick_usd)); top = int(round(p_ref * 3 / tick_usd)); ntick = top - base
    tk = np.round(D.px.fillna(0).to_numpy() / tick_usd).astype(np.int64) - base
    off = (D.px.fillna(0) * 100 % 1 > 1e-6) & (D.act.isin([A_, M_]))
    log(f'price ref {p_ref:.2f}, ticks {ntick:,}; sub-penny adds/modifies {int(off.sum())} (rounded to the cent)')
    myv = venues.index(a.venue)
    rl = a.rl   # 100: CBRS 05-14 (an IPO: no prior month for the 2025 price tiers) matches the SIP NBBO 98 % at 100, 82 % at 40
    log(f'round lot {rl} shares (the protected quote; odd lots still count for the queue, taking and the cross-venue clear test)')
    os.makedirs(a.out, exist_ok=True)
    rows = []
    wsig = np.zeros(7); thr = 1e9
    if a.signal:
        S = json.load(open(a.signal)); wsig = np.array(S['w'], float); thr = S['thr']
    if a.thr is not None: thr = a.thr
    for w in a.windows.split(','):
        s0, s1 = w.split('-'); t0 = ns_at(a.date, s0); t1 = ns_at(a.date, s1)
        for rule in a.rules.split(','):
            mode = {'join': 0, 'penny': 1, 'penny+sig': 2, 'join+sig': 3}[rule]
            lot = max(100, 100 * round(a.lot_usd / p_ref / 100))
            ft, fs, fp, fq, fk, fa0, flv0, nact, rt, rb, ra, gt, gm, gf = run(D.ts.to_numpy(np.int64).astype(np.float64), D.ven.to_numpy(np.int64), D.act.to_numpy(np.int64),
                                                       D.side.to_numpy(np.int64), tk, D['size'].to_numpy(), D.oid.to_numpy(np.int64),
                                                       len(venues), ntick, float(t0), float(t1), mode, myv, float(lot), a.cap, a.lat * 1e9,
                                                       a.front, a.post_only, ADD_CREDIT.get(a.venue, 0.0), tick_usd, base, float(rl),
                                                       wsig, thr, 1e8 if (a.dump_features and rule == a.rules.split(',')[0]) else 0.0)
            if len(gt):
                G = pd.DataFrame(gf, columns=['imb1', 'imb5', 'ofi_1', 'ofi_5', 'tf_1', 'tf_5']); G.insert(0, 'mid', gm); G.insert(0, 't', gt.astype(np.int64))
                G.to_parquet(f'{a.out}/{a.ticker}_{a.date}_{w.replace(":", "")}_features.parquet', index=False)
                log(f'  features: {len(G):,} grid points')
            ok = (rb >= 0) & (ra > rb)
            mt = rt[ok]; mid = 0.5 * (rb[ok] + ra[ok] + 2 * base) * tick_usd
            j = np.clip(np.searchsorted(mt, ft, 'right') - 1, 0, None); m0 = mid[j]
            maker = fk <= 1
            cash = -(fs * fp * fq).sum()
            fee = (fq * COMM).sum() - (fq * ADD_CREDIT.get(a.venue, 0.0) * maker).sum() + (fq * TAKE * ~maker).sum()
            spread = (fs * (m0 - fp) * fq)[maker].sum()
            adv = {}
            for Dl in (1, 5, 30):
                jd = np.clip(np.searchsorted(mt, ft + Dl * 1e9, 'right') - 1, 0, None); adv[Dl] = (fs * (mid[jd] - m0) * fq)[maker].sum()
            net = cash - fee
            rows.append(dict(window=w, rule=rule, lot=lot, own=int((fk == 0).sum()), cross=int((fk == 1).sum()), take=int((fk == 2).sum()),
                             maker_sh=float(fq[maker].sum()), taker_sh=float(fq[~maker].sum()), actions=nact, net=round(net), spread=round(spread),
                             adv1=round(adv[1]), adv5=round(adv[5]), adv30=round(adv[30]), fees=round(fee),
                             net_bp=round(1e4 * net / max((fp * fq).sum(), 1), 1), med_spr_bp=round(float(np.median(1e4 * (ra[ok] - rb[ok]) * tick_usd / mid)), 1)))
            mk = {D_: mid[np.clip(np.searchsorted(mt, ft + D_ * 1e9, 'right') - 1, 0, None)] for D_ in (1, 5, 30)}
            pd.DataFrame(dict(t=ft, side=fs, px=fp, qty=fq, kind=fk, ahead0=fa0, level0=flv0, mid0=m0, mid1=mk[1], mid5=mk[5], mid30=mk[30])).to_parquet(f'{a.out}/{a.ticker}_{a.date}_{w.replace(":", "")}_{rule}_fills.parquet', index=False)
            log(rows[-1])
            if a.momentum:
                o_ = np.argsort(ft, kind='stable'); qq = np.cumsum((fs * fq)[o_]); tq = ft[o_]
                capq = a.cap * lot - 1e-9
                prev = np.concatenate([[0.0], qq[:-1]])
                ev = np.nonzero(((qq >= capq) & (prev < capq)) | ((qq <= -capq) & (prev > -capq)))[0]
                evt = tq[ev]; evd = np.where(qq[ev] > 0, -1.0, 1.0)          # maker long -> the trade is SHORT
                okq = (rb >= 0) & (ra > rb); qt_ = rt[okq]; qb_ = (rb[okq] + base) * tick_usd; qa_ = (ra[okq] + base) * tick_usd
                def at(tt): return np.clip(np.searchsorted(qt_, tt, 'right') - 1, 0, None)
                te = evt + a.mom_lat * 1e9; je = at(te)
                pe = np.where(evd > 0, qa_[je], qb_[je]); me = 0.5 * (qa_[je] + qb_[je])
                for H in (10, 30, 60, 300, 900, 1800):
                    keep = te + H * 1e9 < min(t1, qt_[-1])
                    jx = at(te + H * 1e9)
                    px_ = np.where(evd > 0, qb_[jx], qa_[jx]); mx = 0.5 * (qa_[jx] + qb_[jx])
                    fee = 2 * (COMM + TAKE)
                    netbp = 1e4 * (evd * (px_ - pe) - fee) / pe; midbp = 1e4 * evd * (mx - me) / me
                    if keep.sum():
                        log(f'  MOMENTUM cap={a.cap} H={H}s: {int(keep.sum())} events, mid move with the trade {midbp[keep].mean():+.1f} bp '
                            f'(median {np.median(midbp[keep]):+.1f}), net of the spread + fees {netbp[keep].mean():+.1f} bp, win {np.mean(netbp[keep] > 0):.0%}, '
                            f'$ at {lot} sh {np.sum(netbp[keep] / 1e4 * pe[keep] * lot):+,.0f}')
            if rule == a.rules.split(',')[0] and a.check_nbbo and os.path.exists(f'{a.check_nbbo}/{a.ticker}_{a.date}.parquet'):
                Q = pd.read_parquet(f'{a.check_nbbo}/{a.ticker}_{a.date}.parquet').dropna().sort_values('ts')
                g = np.arange(t0, min(t1, rt[-1]), int(1e9))
                jq = np.searchsorted(Q.ts.to_numpy(np.int64), g, 'right') - 1; jm = np.searchsorted(rt, g, 'right') - 1
                okk = (jq >= 0) & (jm >= 0)
                sb = Q.bid.to_numpy()[jq[okk]]; sa_ = Q.ask.to_numpy()[jq[okk]]
                mb = (rb[jm[okk]] + base) * tick_usd; ma = (ra[jm[okk]] + base) * tick_usd
                log(f'  NBBO check vs Massive SIP, 1 s grid ({okk.sum()} points): bid equal {np.mean(np.abs(sb - mb) < 0.005):.1%}, '
                    f'ask equal {np.mean(np.abs(sa_ - ma) < 0.005):.1%}, |mid diff| median {np.median(np.abs((sb + sa_) / 2 - (mb + ma) / 2)):.4f}')
                log(f'  MBO bid above SIP {np.mean(mb > sb + 0.005):.1%} / below {np.mean(mb < sb - 0.005):.1%}; MBO ask below SIP {np.mean(ma < sa_ - 0.005):.1%} / above {np.mean(ma > sa_ + 0.005):.1%}; '
                    f'median |bid diff| {np.median(np.abs(mb - sb)):.2f}, p90 {np.quantile(np.abs(mb - sb), 0.9):.2f}')
    R = pd.DataFrame(rows)
    print(R.to_string(), flush=True)
    R.to_csv(f'{a.out}/{a.ticker}_{a.date}_summary.csv', index=False)
