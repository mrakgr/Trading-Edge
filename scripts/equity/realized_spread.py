"""THE REALIZED SPREAD — does quoting pay? (user 2026-11-03: "it might be worth trying out in stocks with very high spreads,
but also high volume. Hot IPOs come to mind" — "I am especially interested in the CBRS IPO. I traded a market making
strategy manually in that stock in TapeSim and made 50% that day").

For every trade (the cap sim's condition exclusions; size > 0): the NBBO MID M0 of the last quote strictly before it
(a locked / crossed / one-sided quote gives no mid: the trade is skipped), the aggressor q = +1 above the mid (a buyer
lifted the offer: the MAKER SOLD), −1 below, a trade AT the mid skipped. Then, in bp of M0:
    effective half-spread  q·(P − M0)          what the aggressor paid over the mid = the maker's take at the fill
    impact at Δ            q·(M_Δ − M0)        the mid's move in the aggressor's direction = the maker's adverse selection
    REALIZED SPREAD at Δ   q·(P − M_Δ)         = effective − impact: what the maker keeps unwinding at the mid Δ later
M_Δ = the mid of the last quote at or before t + Δ. Averages are SHARE-weighted (what a maker filled pro rata would get).
An UPPER BOUND for a back-of-the-queue maker: the queue gives the front the benign fills and the back the sweeps; the
mid is not a price one can trade at; inventory risk is not in an average. Lit = not FINRA TRF (exchange 4): the trades a
lit-venue maker can be on the other side of. Fees per share (default 0.2 c commission, 0.2 c ARCA add rebate) are
shown as bp at the price.

  cd research && python -u scripts/equity/realized_spread.py --names CBRS:2026-05-14 > data/equity/realized_spread/report_CBRS.md 2> logs/realized_spread.log; echo exit $?
"""
import argparse, datetime, json, math, os, sys, time, urllib.request, zoneinfo
import duckdb, numpy as np, pandas as pd
from numba import njit


@njit(cache=True)
def ewma_before(t, P, S, hl):
    """the share-weighted EWMA of the trade prices BEFORE each trade, decayed in clock time (ns) with half-life hl s"""
    lam = math.log(2.0) / hl
    out = np.full(len(t), np.nan)
    w = 0.0; wp = 0.0
    for i in range(len(t)):
        if i > 0:
            dec = math.exp(-lam * (t[i] - t[i - 1]) / 1e9); w *= dec; wp *= dec
        if w > 0: out[i] = wp / w
        w += S[i]; wp += S[i] * P[i]
    return out

ap = argparse.ArgumentParser()
ap.add_argument('--names', default='', help='TICKER:YYYY-MM-DD, comma-separated')
ap.add_argument('--trades', default='data/bulk/trades')
ap.add_argument('--quotes', default='data/equity/realized_spread/quotes')
ap.add_argument('--commission', type=float, default=0.002)
ap.add_argument('--rebate', type=float, default=0.002)
ap.add_argument('--save', help='write each name-day\'s classified trades to <save>/<ticker>_<date>.parquet (the pooled report)')
ap.add_argument('--names-file', help='a parquet with ticker, date columns (instead of / beside --names)')
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
NY = zoneinfo.ZoneInfo('America/New_York')
def ns_at(d, sec): y, m, dd = map(int, d.split('-')); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
DELTAS = [1, 5, 30, 60, 300]
MA_HL = [10, 30]
E_BINS = [(-1e9, -20), (-20, -5), (-5, 0), (0, 5), (5, 20), (20, 1e9)]
os.makedirs(a.quotes, exist_ok=True)


def fetch(ticker, d):
    """the day's NBBO 04:00–20:00 ET from Massive /v3/quotes, cached; the key is read from the credentials file, never printed"""
    path = os.path.join(a.quotes, f'{ticker}_{d}.parquet')
    if os.path.exists(path): return pd.read_parquet(path)
    key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
    url = (f'https://api.massive.com/v3/quotes/{ticker}?timestamp.gte={ns_at(d, 4 * 3600)}&timestamp.lt={ns_at(d, 20 * 3600)}'
           f'&limit=50000&sort=timestamp&order=asc')
    rows, pages = [], 0
    while url:
        for attempt in range(5):
            try:
                req = urllib.request.Request(url, headers={'Authorization': f'Bearer {key}'})
                dd = json.load(urllib.request.urlopen(req, timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f'{ticker} {d}: {type(e).__name__}')
                time.sleep(2 * (attempt + 1))
        rows += [(q.get('sip_timestamp'), q.get('bid_price'), q.get('bid_size'), q.get('ask_price'), q.get('ask_size')) for q in dd.get('results', [])]
        url = dd.get('next_url'); pages += 1
        if pages % 20 == 0: log(f'  {ticker} {d}: {pages} pages, {len(rows):,} quotes')
    Q = pd.DataFrame(rows, columns=['ts', 'bid', 'bid_size', 'ask', 'ask_size'])
    Q.to_parquet(path + '.tmp', index=False); os.replace(path + '.tmp', path)
    log(f'{ticker} {d}: {len(Q):,} quotes in {pages} pages')
    return Q


con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
BUCKETS = [(0, 30, '0–30 s'), (30, 60, '30–60 s'), (60, 120, '1–2 min'), (120, 300, '2–5 min'), (300, 600, '5–10 min'),
           (600, 1800, '10–30 min'), (1800, 3600, '30–60 min'), (3600, 3 * 3600, '1–3 h'), (3 * 3600, 99 * 3600, '3 h +')]
specs = [x for x in a.names.split(',') if x]
if a.names_file:
    specs += [f'{r.ticker}:{str(r.date)[:10]}' for r in pd.read_parquet(a.names_file).itertuples()]
if a.save: os.makedirs(a.save, exist_ok=True)
for spec in specs:
    ticker, d = spec.split(':')
    Q = fetch(ticker, d).dropna().sort_values('ts', kind='stable')
    X = con.execute(f"""SELECT sip_timestamp ts, price, size, exchange <> 4 lit FROM read_parquet('{a.trades}/{d}.parquet')
                        WHERE ticker = '{ticker}' AND size > 0 AND NOT list_has_any(conditions, {EXCL})
                        AND sip_timestamp >= {ns_at(d, 9.5 * 3600)} AND sip_timestamp < {ns_at(d, 16 * 3600)} ORDER BY ts""").df()
    # a VALID quote: two-sided, not locked / crossed, and not a stub (the spread under 10 % of the mid: 0.9999 × 2500.01 at
    # CBRS's open). Only valid quotes give a mid; a HALT (an LULD pause) shows as a gap of > 30 s between valid quotes
    # holding locked ones — a mid due inside it is the REOPENING mid (the maker's inventory is stuck through the pause)
    qa = Q.ts.to_numpy(np.int64); bid = Q.bid.to_numpy(); ask = Q.ask.to_numpy()
    valid = (bid > 0) & (ask > bid) & ((ask - bid) < 0.10 * 0.5 * (ask + bid))
    locked = (bid > 0) & (ask > 0) & (ask <= bid)
    qt = qa[valid]; mid = 0.5 * (bid + ask)[valid]; spr = (ask - bid)[valid]
    nl = np.cumsum(locked)                                         # locked quotes up to each raw index
    vi = np.nonzero(valid)[0]
    halt_after = np.zeros(len(qt), bool)                           # the gap after valid quote j is a halt
    halt_after[:-1] = ((qt[1:] - qt[:-1]) > 30e9) & (nl[vi[1:]] - nl[vi[:-1]] > 0)
    halts = [(qt[j], qt[j + 1]) for j in np.nonzero(halt_after)[0]]
    t = X.ts.to_numpy(np.int64); P = X.price.to_numpy(); S = X['size'].to_numpy(); lit = X.lit.to_numpy()
    i0 = np.searchsorted(qt, t, 'left') - 1                       # the last VALID quote strictly before the trade
    i0c = np.clip(i0, 0, None)
    # no mid: none yet, the last one older than 30 s, or the last one before a HALT the trade is past (a reopening
    # print before the first valid quote after the pause must not be measured against the pre-halt mid)
    ok = (i0 >= 0) & ((t - qt[i0c]) <= 30e9) & ~halt_after[i0c]
    M0 = np.where(ok, mid[np.clip(i0, 0, None)], np.nan); SP = np.where(ok, spr[np.clip(i0, 0, None)], np.nan)
    q = np.sign(P - M0); keep = np.isfinite(M0) & (q != 0)
    first = t[0]
    MAS = {hl: ewma_before(t, P, S, float(hl)) for hl in MA_HL}       # on EVERY trade, before the classification's cut
    k = np.nonzero(keep)[0]                                          # the classified trades only, from here on (8.6M on SPCX)
    t, P, S, lit, q, M0, SP = t[k], P[k], S[k], lit[k], q[k], M0[k], SP[k]
    MAS = {hl: m[k] for hl, m in MAS.items()}
    f32 = lambda x: np.asarray(x, np.float32)
    D = pd.DataFrame({'sec': f32((t - first) / 1e9), 'P': P, 'S': f32(S), 'lit': lit, 'q': q.astype(np.int8), 'M0': M0,
                      'qs_bp': f32(1e4 * SP / M0), 'eff_bp': f32(1e4 * q * (P - M0) / M0)})
    # ⭐ THE MEAN-REVERSION BIAS (user 2026-11-03: "a very short term mean reversion system that tries to capture the
    # spread around a 10 to 30s moving average ... bought the bid and sold the offer"): each fill against a CAUSAL EWMA of
    # the trade prices (a time-decayed half-life, share-weighted, the trades BEFORE this one; a halt decays it like any
    # time). The maker's edge vs the MA, e = q·(P − MA)/MA in bp: > 0 = the maker SOLD ABOVE the average or BOUGHT BELOW
    # it (the idea's fills); < 0 = against it.
    for hl in MA_HL:
        D[f'e_{hl}'] = f32(1e4 * q * (P - MAS[hl]) / MAS[hl])
    for dl in DELTAS:
        j = np.searchsorted(qt, t + int(dl * 1e9), 'right') - 1
        jc = np.clip(j, 0, len(qt) - 2)
        j = np.where((j >= 0) & halt_after[jc] & (j == jc), j + 1, j)  # due inside a halt: the reopening quote
        MD = mid[np.clip(j, 0, None)]
        D[f'imp_{dl}'] = f32(1e4 * q * (MD - M0) / M0)
        D[f'rs_{dl}'] = f32(1e4 * q * (P - MD) / M0)
    D = D[np.isfinite(D[[f'rs_{dl}' for dl in DELTAS]]).all(axis=1)].reset_index(drop=True)
    if len(D) == 0:
        log(f'{ticker} {d}: nothing classified'); continue
    if a.save:
        D.assign(ticker=ticker, date=d).to_parquet(os.path.join(a.save, f'{ticker}_{d}.parquet'), index=False)
    fee_bp = 1e4 * (a.commission - a.rebate) / np.average(D.M0, weights=D.S)
    t_first = datetime.datetime.fromtimestamp(first / 1e9, NY).strftime('%H:%M:%S')
    print(f'# The realized spread — {ticker} {d}\n')
    for h0, h1 in halts:
        f = lambda ns: datetime.datetime.fromtimestamp(ns / 1e9, NY).strftime('%H:%M:%S')
        print(f'- HALT (no valid quote, locked ones only): {f(h0)} → {f(h1)} ET ({(h1 - h0) / 1e9:.0f} s); a mid due inside it is the reopening mid')
    if halts: print()
    print(f'{len(X):,} trades after the condition exclusions (first at {t_first} ET), {len(Q):,} NBBO quotes; {len(D):,} trades '
          f'classified ({100 * len(D) / max(1, len(X)):.0f} %: no valid mid before it or at the mid are left out), {D.S.sum():,.0f} shares, '
          f'lit {100 * D.S[D.lit].sum() / D.S.sum():.0f} % of the shares. (Skipped too: a trade whose last valid quote is > 30 s old or '
          f'before a halt it is past.) Share-weighted bp of the mid. Fees: commission '
          f'{100 * a.commission:g} c − rebate {100 * a.rebate:g} c a share = {fee_bp:+.3f} bp at the day\'s average price.\n')
    for lab, sub in [('all trades', D), ('lit trades only (a lit maker\'s fills)', D[D.lit])]:
        print(f'## {lab}\n')
        print('| since the first trade | trades | shares | quoted spread bp | effective half bp | '
              + ' | '.join(f'impact {dl}s' for dl in DELTAS) + ' | ' + ' | '.join(f'**realized {dl}s**' for dl in DELTAS) + ' |')
        print('|---|---|---|---|---|' + '---|' * (2 * len(DELTAS)))
        rows = [(lab2, sub[(sub.sec >= lo) & (sub.sec < hi)]) for lo, hi, lab2 in BUCKETS] + [('**the day**', sub)]
        for lab2, g in rows:
            if len(g) == 0: continue
            w = g.S
            av = lambda c: np.average(g[c], weights=w)
            print(f'| {lab2} | {len(g):,} | {w.sum():,.0f} | {np.median(g.qs_bp):.1f} | {av("eff_bp"):.2f} | '
                  + ' | '.join(f'{av(f"imp_{dl}"):+.2f}' for dl in DELTAS) + ' | '
                  + ' | '.join(f'**{av(f"rs_{dl}"):+.2f}**' for dl in DELTAS) + ' |')
        print()
    print('## The mean-reversion bias: lit fills by the maker\'s edge against a causal EWMA of the trade prices\n')
    print('e = q·(P − MA) in bp: **> 0 = the maker sold ABOVE the average or bought BELOW it** (the idea\'s fills). From 5 min '
          'after the open (the run into the halt left out). Realized spread in bp, share-weighted.\n')
    L5 = D[D.lit & (D.sec >= 300)]
    for hl in MA_HL:
        print(f'### the MA: a {hl} s half-life EWMA\n')
        print('| maker edge vs the MA | share of lit shares | effective half | ' + ' | '.join(f'realized {dl}s' for dl in DELTAS) + ' |')
        print('|---|---|---|' + '---|' * len(DELTAS))
        for lo, hi in E_BINS:
            g = L5[(L5[f'e_{hl}'] > lo) & (L5[f'e_{hl}'] <= hi)]
            if len(g) == 0: continue
            lab = f"({'−∞' if lo <= -1e8 else f'{lo:g}'}, {'∞' if hi >= 1e8 else f'{hi:g}'}] bp"
            print(f'| {lab} | {100 * g.S.sum() / L5.S.sum():.0f} % | {np.average(g.eff_bp, weights=g.S):.1f} | '
                  + ' | '.join(f'**{np.average(g[f"rs_{dl}"], weights=g.S):+.1f}**' for dl in DELTAS) + ' |')
        for lab, flt in [('**with the idea (e > 0)**', L5[f'e_{hl}'] > 0), ('**against it (e ≤ 0)**', L5[f'e_{hl}'] <= 0)]:
            g = L5[flt]
            print(f'| {lab} | {100 * g.S.sum() / L5.S.sum():.0f} % | {np.average(g.eff_bp, weights=g.S):.1f} | '
                  + ' | '.join(f'**{np.average(g[f"rs_{dl}"], weights=g.S):+.1f}**' for dl in DELTAS) + ' |')
        print()
    # dollars: what a maker on the other side of every lit share would have kept at 30 s, per bucket
    print('## A lit maker\'s dollars, pro rata (an upper bound): realized spread at 30 s × the dollars traded, less fees\n')
    print('| since the first trade | lit $ traded | realized 30 s bp | maker $ per $1M traded | lit maker $ total (every share) |\n|---|---|---|---|---|')
    L = D[D.lit]
    for lo, hi, lab2 in BUCKETS + [(0, 1e9, '**the day**')]:
        g = L[(L.sec >= lo) & (L.sec < hi)]
        if len(g) == 0: continue
        dollars = (g.P * g.S).sum(); rs = np.average(g.rs_30, weights=g.S) - fee_bp
        print(f'| {lab2} | {dollars / 1e6:,.1f} M | {rs:+.2f} | {rs * 100:+,.0f} | {rs * dollars / 1e4:+,.0f} |')
    print()
log('done')
