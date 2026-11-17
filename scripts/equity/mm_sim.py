r"""MARKET-MAKING SIM, step 1 (user 2026-11-17: "try implementing a real market making system and seeing whether it works";
"let's see if we can get it to work on the cheap data"; borrows assumed FREE, so both sides are quoted from flat).
The design is docs/market_making_primer.md §5.

Per trigger of triggers_B3.csv: the NBBO (mm_quotes_fetch.py) and the prints (cached here from the bulk tape) from
window_start − 15 min to the trigger + 35 min. WARMUP = before the trigger (the calibration: σ, A, k); TRADING = the trigger
to the trigger + --minutes; at its end the inventory is closed at the touch (taking).

THE BOOK MODEL (an event loop over the merged quotes and prints, the quotes first at equal times):
  * a VALID quote: two-sided, not locked / crossed, the spread under 10 % of the mid; an invalid one is ignored (halts)
  * each side has at most one order of --lot-usd worth of round lots; an order / replace / cancel lands --lat s after it is
    decided, ONE in flight per side (the next decision waits for it), the old order stays live meanwhile
  * the queue at landing: above the bid (improving) 0; AT the bid the displayed size (the QUEUE model — the one that
    matched live 5-0 on fills, §S49ap-3); below the bid "deep" until the bid falls to our price, then the displayed size then
  * a LIT print at our price eats the queue first, the rest fills us; a print through our price fills us (at our price) up
    to its size; a bid landing at or above the ask TAKES at the ask
  * the NBBO in the data never contains us: "the bid" is the others' bid
FEES: 0.2c/share commission on every fill; +0.20c add credit on maker fills at \$1+; −0.30c take fee on taker fills.
THE RULES (--rules, all with the inventory cap: past --cap lots the side that adds is not quoted):
  join      bid = the best bid, ask = the best ask
  penny     one tick inside the touch when the spread is 3 ticks or more, else the touch
  glft:g    Guéant–Lehalle–Fernandez-Tapia, centred on the mid, γ = g·k (dimensionless g):
              δ0 = ln(1+g)/(g·k), c = σ·sqrt(g·(1+g)^(1+1/g) / (2A)), δᵇ = δ0 + (2q+1)/2·c, δᵃ = δ0 − (2q−1)/2·c (q in lots)
            rounded AWAY from the mid to the tick, never crossing (bid ≤ ask − tick, ask ≥ bid + tick)
P&L DECOMPOSITION per window: spread captured = Σ side·(mid − px)·qty at the fill; adverse at Δ = Σ side·(mid_Δ − mid)·qty
(side +1 buy); inventory = net − spread − fees (the residual: the marks between fills and the flatten).

  cd research && python -u scripts/equity/mm_sim.py --out data/equity/mm_sim/run1 > logs/mm_sim_run1.log 2>&1; echo exit $?
"""
import argparse, datetime, math, os, time, types, zoneinfo
from multiprocessing import Pool
import duckdb, numpy as np, pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument('--triggers', default='data/equity/mm_opportunity/triggers_B3.csv')
ap.add_argument('--quotes', default='data/equity/mm_sim/quotes')
ap.add_argument('--prints', default='data/equity/mm_sim/prints')
ap.add_argument('--trades', default='data/bulk/trades')
ap.add_argument('--out', required=True)
ap.add_argument('--rules', default='join,penny,glft:0.1,glft:1,glft:10')
ap.add_argument('--minutes', type=float, default=30.0)
ap.add_argument('--lat', type=float, default=0.15)
ap.add_argument('--lot-usd', type=float, default=2000.0)
ap.add_argument('--cap', type=int, default=3, help='inventory cap in lots')
ap.add_argument('--workers', type=int, default=8)
ap.add_argument('--front', action='store_true', help='OPTIMISTIC: a joined order is first in line (queue 0)')
ap.add_argument('--post-only', action='store_true', help='an order landing marketable is cancelled instead of taking')
ap.add_argument('--oracle-rho', type=float, default=1.0, help='a NOISY oracle: the signal = the future move + noise held per second, '
                'scaled so its correlation with the move is rho (the accuracy a real signal would need)')
ap.add_argument('--oracle', default='', help='H:bp — LOOKAHEAD BOUND: pull the bid while the mid H s ahead is more than bp below now, the ask '
                'while it is more than bp above (what a perfect short-term direction signal would be worth)')
ap.add_argument('--pre', type=float, default=15.0)
ap.add_argument('--post', type=float, default=35.0)
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
COMM, ADD, TAKE = 0.002, 0.002, 0.003


def ns_at(d, sec):
    y, m, dd = map(int, d.split('-'))
    return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)


def sec_of(hhmm): h, m = map(int, hhmm.split(':')); return h * 3600 + m * 60


def window(r):
    s0 = max(9.5 * 3600, sec_of(r.window_start_et) - 60 * a.pre); s1 = min(16 * 3600, sec_of(r.trigger_et) + 60 * a.post)
    return ns_at(r.date, s0), ns_at(r.date, s1)


def name(r): return f'{r.ticker}_{r.date}_{r.trigger_et.replace(":", "")}'


def cache_prints(R):
    """the lit + TRF prints of every window from the bulk tape, one DuckDB pass per day"""
    os.makedirs(a.prints, exist_ok=True)
    todo = [r for r in R if not os.path.exists(f'{a.prints}/{name(r)}.parquet')]
    for d, g in pd.DataFrame([vars(r) for r in todo]).groupby('date') if todo else []:
        con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
        rs = list(g.itertuples())
        tks = ', '.join(sorted({f"'{r.ticker}'" for r in rs}))  # the file is sorted by ticker: the IN prunes row groups (50 s -> 0.3 s a day)
        cond = ' OR '.join(f"(ticker = '{r.ticker}' AND sip_timestamp >= {window(r)[0]} AND sip_timestamp < {window(r)[1]})" for r in rs)
        X = con.execute(f"""SELECT ticker, sip_timestamp ts, price, size, exchange <> 4 lit FROM read_parquet('{a.trades}/{d}.parquet')
                            WHERE ticker IN ({tks}) AND size > 0 AND NOT list_has_any(conditions, {EXCL}) AND ({cond}) ORDER BY ts""").df()
        con.close()
        for r in rs:
            w0, w1 = window(r)
            Y = X[(X.ticker == r.ticker) & (X.ts >= w0) & (X.ts < w1)].drop(columns='ticker')
            Y.to_parquet(f'{a.prints}/{name(r)}.parquet', index=False)
        print(f'prints {d}: {len(rs)} windows, {len(X):,} prints', flush=True)


INF = 1e18


@njit(cache=True)
def sim(et, ek, eb, ebs, ea, eas, ex, esz, elit, t0, t1, mode, g, sig, A, k, lot, cap, lat, tick, front, post_only, efut, othr):
    """returns the fills (t, side, px, qty, maker) and the count of order actions"""
    nmax = 200000
    ft = np.zeros(nmax); fs = np.zeros(nmax); fp = np.zeros(nmax); fq = np.zeros(nmax); fm = np.zeros(nmax)
    nf = 0; nact = 0
    lb = 0.0; lbs = 0.0; la = 0.0; las = 0.0; valid = False
    live = np.zeros(2, np.bool_); lpx = np.zeros(2); rem = np.zeros(2); qa = np.zeros(2)
    pend = np.zeros(2, np.bool_); ptime = np.zeros(2); ppx = np.zeros(2); pcan = np.zeros(2, np.bool_)
    q = 0.0
    if mode == 2:
        d0 = math.log(1 + g) / (g * k); c = sig * math.sqrt(g * (1 + g) ** (1 + 1 / g) / (2 * A))
    else:
        d0 = 0.0; c = 0.0
    for i in range(len(et)):
        t = et[i]
        if t >= t1: break
        # 1. the actions in flight that land before this event
        for s in range(2):
            if pend[s] and ptime[s] <= t:
                pend[s] = False
                if pcan[s]:
                    live[s] = False
                    continue
                p = ppx[s]
                if post_only and valid and ((s == 0 and p >= la - 1e-9) or (s == 1 and p <= lb + 1e-9)):
                    live[s] = False
                elif s == 0 and valid and p >= la - 1e-9:      # a marketable bid: takes at the ask
                    if nf < nmax:
                        ft[nf] = ptime[s]; fs[nf] = 1; fp[nf] = la; fq[nf] = lot; fm[nf] = 0; nf += 1
                    q += lot; live[s] = False
                elif s == 1 and valid and p <= lb + 1e-9:
                    if nf < nmax:
                        ft[nf] = ptime[s]; fs[nf] = -1; fp[nf] = lb; fq[nf] = lot; fm[nf] = 0; nf += 1
                    q -= lot; live[s] = False
                else:
                    live[s] = True; lpx[s] = p; rem[s] = lot
                    if s == 0: qa[s] = 0.0 if p > lb + 1e-9 else ((0.0 if front else lbs) if abs(p - lb) < 1e-9 else INF)
                    else: qa[s] = 0.0 if p < la - 1e-9 else ((0.0 if front else las) if abs(p - la) < 1e-9 else INF)
        # 2. the event
        if ek[i] == 0:
            b = eb[i]; aa = ea[i]
            if b > 0 and aa > b and (aa - b) < 0.1 * 0.5 * (aa + b):
                lb = b; lbs = ebs[i]; la = aa; las = eas[i]; valid = True
                if live[0]:
                    if lb < lpx[0] - 1e-9: qa[0] = 0.0
                    elif qa[0] >= INF and abs(lb - lpx[0]) < 1e-9: qa[0] = 0.0 if front else lbs
                if live[1]:
                    if la > lpx[1] + 1e-9: qa[1] = 0.0
                    elif qa[1] >= INF and abs(la - lpx[1]) < 1e-9: qa[1] = 0.0 if front else las
            else:
                continue
        else:
            if elit[i] and t >= t0:
                x = ex[i]; z = esz[i]
                for s in range(2):
                    if not live[s]: continue
                    p = lpx[s]; got = 0.0
                    through = x < p - 1e-9 if s == 0 else x > p + 1e-9
                    if through:
                        got = min(rem[s], z)
                    elif abs(x - p) < 1e-9 and qa[s] < INF:
                        qa[s] -= z
                        if qa[s] < 0:
                            got = min(rem[s], -qa[s]); qa[s] = 0.0
                    if got > 0:
                        sd = 1 if s == 0 else -1
                        if nf < nmax:
                            ft[nf] = t; fs[nf] = sd; fp[nf] = p; fq[nf] = got; fm[nf] = 1; nf += 1
                        q += sd * got; rem[s] -= got
                        if rem[s] <= 0: live[s] = False
            if not valid: continue
        if t < t0 or not valid: continue
        # 3. the desired quotes
        ql = q / lot
        for s in range(2):
            if pend[s]: continue
            allowed = ql < cap - 1e-9 if s == 0 else ql > -cap + 1e-9
            if othr >= 0 and ((s == 0 and efut[i] < -othr) or (s == 1 and efut[i] > othr)): allowed = False
            want = 0.0
            if allowed:
                if mode == 0:
                    want = lb if s == 0 else la
                elif mode == 1:
                    if la - lb >= 3 * tick - 1e-9: want = lb + tick if s == 0 else la - tick
                    else: want = lb if s == 0 else la
                else:
                    mid = 0.5 * (lb + la)
                    if s == 0:
                        want = math.floor((mid - (d0 + (2 * ql + 1) / 2 * c)) / tick + 1e-6) * tick
                        if want > la - tick: want = la - tick
                    else:
                        want = math.ceil((mid + (d0 - (2 * ql - 1) / 2 * c)) / tick - 1e-6) * tick
                        if want < lb + tick: want = lb + tick
                    if want <= 0: allowed = False
            if allowed:
                if live[s] and abs(lpx[s] - want) < 1e-9: continue
                pend[s] = True; pcan[s] = False; ppx[s] = want; ptime[s] = t + lat; nact += 1
            elif live[s]:
                pend[s] = True; pcan[s] = True; ptime[s] = t + lat; nact += 1
    # the end: flatten at the touch
    if q > 1e-9 and nf < nmax:
        ft[nf] = t1; fs[nf] = -1; fp[nf] = lb; fq[nf] = q; fm[nf] = 0; nf += 1
    elif q < -1e-9 and nf < nmax:
        ft[nf] = t1; fs[nf] = 1; fp[nf] = la; fq[nf] = -q; fm[nf] = 0; nf += 1
    return ft[:nf], fs[:nf], fp[:nf], fq[:nf], fm[:nf], nact


def calibrate(qt, mid, pt, px, plit, t0, w0):
    """σ (price/√s, the 1 s-sampled mid), A, k (λ(δ) = A·e^(−kδ) per side per s, fit on the lit prints' distance to the mid)"""
    sel = qt < t0
    if sel.sum() < 100: return np.nan, np.nan, np.nan
    grid = np.arange(w0, t0, 1e9)
    j = np.searchsorted(qt[sel], grid, 'right') - 1
    m1 = mid[sel][j[j >= 0]]
    sig = np.std(np.diff(m1)) if len(m1) > 30 else np.nan
    tsel = (pt < t0) & plit
    i = np.searchsorted(qt, pt[tsel], 'left') - 1
    ok = i >= 0
    dl = np.abs(px[tsel][ok] - mid[i[ok]])
    dl = dl[dl > 0]
    T = (t0 - w0) / 1e9
    if len(dl) < 50: return sig, np.nan, np.nan
    lo, hi = np.quantile(dl, [0.5, 0.95])
    if hi <= lo: return sig, np.nan, np.nan
    dg = np.linspace(lo, hi, 12)
    lam = np.array([(dl >= d).sum() for d in dg]) / (2 * T)
    k_, lnA = np.polyfit(dg, np.log(lam), 1)
    if not (np.isfinite(lnA) and lnA < 300): return sig, np.nan, np.nan   # a degenerate fit (a near-constant distance)
    return sig, math.exp(lnA), -k_


def one(r):
    nm = name(r)
    Q = pd.read_parquet(f'{a.quotes}/{nm}.parquet').dropna().sort_values('ts', kind='stable')
    P = pd.read_parquet(f'{a.prints}/{nm}.parquet')
    w0, w1 = window(r)
    t0 = ns_at(r.date, sec_of(r.trigger_et)); t1 = min(w1, t0 + int(a.minutes * 60e9))
    nq = len(Q); npr = len(P)
    et = np.concatenate([Q.ts.to_numpy(np.int64), P.ts.to_numpy(np.int64)])
    ek = np.concatenate([np.zeros(nq, np.int8), np.ones(npr, np.int8)])
    o = np.lexsort((ek, et))
    z = np.zeros(npr); zq = np.zeros(nq)
    eb = np.concatenate([Q.bid.to_numpy(float), z])[o]; ebs = np.concatenate([Q.bid_size.to_numpy(float), z])[o]
    ea = np.concatenate([Q.ask.to_numpy(float), z])[o]; eas = np.concatenate([Q.ask_size.to_numpy(float), z])[o]
    ex = np.concatenate([zq, P.price.to_numpy(float)])[o]; esz = np.concatenate([zq, P['size'].to_numpy(float)])[o]
    elit = np.concatenate([np.zeros(nq, np.bool_), P.lit.to_numpy(np.bool_)])[o]
    et = et[o].astype(np.float64); ek = ek[o]
    # the valid mids (for the calibration and the decomposition)
    b = Q.bid.to_numpy(float); aq = Q.ask.to_numpy(float)
    v = (b > 0) & (aq > b) & ((aq - b) < 0.05 * (aq + b))
    qt = Q.ts.to_numpy(np.int64)[v]; mid = 0.5 * (b + aq)[v]; spr = (aq - b)[v]
    if v.sum() < 100: return []
    sig, A, k = calibrate(qt, mid, P.ts.to_numpy(np.int64), P.price.to_numpy(float), P.lit.to_numpy(bool), t0, w0)
    px0 = float(np.median(mid[(qt >= t0 - 60e9) & (qt < t0)])) if ((qt >= t0 - 60e9) & (qt < t0)).any() else float(mid[-1])
    tick = 0.01 if px0 >= 1 else 0.0001
    lot = max(100, 100 * round(a.lot_usd / px0 / 100))
    tr = (qt >= t0) & (qt < t1)
    othr = -1.0; efut = np.zeros(len(et))
    if a.oracle:
        H, othr = map(float, a.oracle.split(':'))
        j0 = np.searchsorted(qt, et, 'right') - 1; j1 = np.searchsorted(qt, et + H * 1e9, 'right') - 1
        ok = j0 >= 0
        efut[ok] = 1e4 * (mid[j1[ok]] / mid[j0[ok]] - 1)
        if a.oracle_rho < 1:
            sec = ((et - et[0]) // 1e9).astype(np.int64)
            rng = np.random.default_rng(abs(hash(nm)) % 2**32)
            sd = np.std(efut[(et >= t0) & (et < t1)]) * math.sqrt(1 / a.oracle_rho ** 2 - 1)
            efut = efut + rng.normal(0, sd, sec.max() + 1)[sec]
    spr_bp = float(np.median(1e4 * spr[tr] / mid[tr])) if tr.any() else np.nan
    lit_tr = P[(P.ts >= t0) & (P.ts < t1) & P.lit]
    out = []
    for rule in a.rules.split(','):
        mode, g = (0, 0.0) if rule == 'join' else (1, 0.0) if rule == 'penny' else (2, float(rule.split(':')[1]))
        if mode == 2 and not (np.isfinite(sig) and np.isfinite(A) and np.isfinite(k) and sig > 0 and A > 0 and k > 0):
            continue
        ft, fs, fp, fq, fmk, nact = sim(et, ek, eb, ebs, ea, eas, ex, esz, elit, float(t0), float(t1), mode, g, sig, A, k,
                                        float(lot), a.cap, a.lat * 1e9, tick, a.front, a.post_only, efut, othr)
        cash = -(fs * fp * fq).sum()
        fee = (fq * COMM).sum() - (fq * ADD * ((fmk == 1) & (fp >= 1))).sum() + (fq * TAKE * (fmk == 0)).sum()
        j = np.clip(np.searchsorted(qt, ft, 'right') - 1, 0, None)
        m0 = mid[j]
        mk = fmk == 1
        spread = (fs * (m0 - fp) * fq)[mk].sum()
        adv = {}
        for D in (1, 5, 30):
            jd = np.clip(np.searchsorted(qt, ft + D * 1e9, 'right') - 1, 0, None)
            adv[D] = (fs * (mid[jd] - m0) * fq)[mk].sum()
        net = cash - fee
        out.append(dict(name=nm, date=r.date, ticker=r.ticker, trigger=r.trigger_et, rule=rule, px=px0, lot=lot, tick=tick,
                        spr_bp=spr_bp, sig_bp=1e4 * sig / px0 if sig == sig else np.nan, A=A, k_inv_bp=1e4 / k / px0 if k == k and k > 0 else np.nan,
                        lit_prints=len(lit_tr), lit_sh=int(lit_tr['size'].sum()),
                        fills=int(mk.sum()), maker_sh=float(fq[mk].sum()), buys=float(fq[mk & (fs > 0)].sum()),
                        sells=float(fq[mk & (fs < 0)].sum()), taker_sh=float(fq[~mk].sum()), actions=nact,
                        net=net, spread=spread, adv1=adv[1], adv5=adv[5], adv30=adv[30], fees=fee,
                        inventory=net - spread + fee, notional=float((fp * fq).sum()), h_next30=r.h_next30))
    return out


if __name__ == '__main__':
    T0 = time.time()
    R = [types.SimpleNamespace(**x) for x in pd.read_csv(a.triggers).to_dict('records')]  # picklable for the pool
    cache_prints(R)
    print(f'prints cached in {time.time() - T0:.0f}s', flush=True)
    os.makedirs(a.out, exist_ok=True)
    rows = []
    with Pool(a.workers) as pool:
        for k_, res in enumerate(pool.imap_unordered(one, R, chunksize=2)):
            rows += res
            if (k_ + 1) % 50 == 0: print(f'{k_ + 1}/{len(R)} windows, {time.time() - T0:.0f}s', flush=True)
    D = pd.DataFrame(rows)
    D.to_parquet(f'{a.out}/sim.parquet', index=False)
    print(f'done: {len(D):,} rows in {time.time() - T0:.0f}s', flush=True)
    G = D.groupby('rule')
    S = pd.DataFrame(dict(windows=G.size(), net=G.net.sum(), spread=G.spread.sum(), adv5=G.adv5.sum(), adv30=G.adv30.sum(),
                          inventory=G.inventory.sum(), fees=G.fees.sum(), maker_sh=G.maker_sh.sum(), taker_sh=G.taker_sh.sum(),
                          notional=G.notional.sum(), win_windows=G.net.apply(lambda x: (x > 0).mean()),
                          med_window=G.net.median()))
    S['net_bp'] = 1e4 * S.net / S.notional
    print(S.round(2).to_string(), flush=True)
