#!/usr/bin/env python3
"""LV-1 CHECK — an independent recomputation of TradingEdge.Breakout/LevelEngine.fs (the key-level trend) from the raw
1s bars, compared trip by trip with an engine run.

For each sampled (contract, td) of the run:
  · the 5 s volume EWMA V and its vwap P, the 30 s volume-weighted mean / σ — plain loops in CLOCK time (numpy);
  · the KEY LEVEL by BRUTE FORCE at sampled bars: max over EVERY bar j ≤ k of V_j·e^(−λL·(t_k − t_j)), ties to the
    newest — no ln-trick — and the running champion it implies everywhere else (checked against the brute force);
  · the signals (m vs level ± Kσ), the significance (typ_med = np.median of every V so far — brute force; level_now), the stop-and-reverse trips (next-bar fills, the cutoff's "close", eod) and their marks.
LV-4: --level vwap (the session VWAP) and --band ticks / atr (the ATR recomputed from scratch: the fronts, the previous
--vref sessions' 1-minute ranges). Every engine field must match to 1e-9 (relative for prices / volumes). CONTROL (--control): ties go to the OLDEST bar —
the check must then report mismatches (or say the sample had no tie).

  cd research && python -u scripts/futures/levels_check.py data/futures/levels/run_lv1_test > logs/lv1_check.log 2>&1; echo exit $?
"""
import argparse, math, sys
import duckdb, numpy as np

ap = argparse.ArgumentParser()
ap.add_argument('run')
ap.add_argument('--bars', default='data/futures/intraday_1s_ticks')
ap.add_argument('--sessions', type=int, default=8)
ap.add_argument('--seed', type=int, default=3)
ap.add_argument('--control', action='store_true')
ap.add_argument('--vol-hl', type=float, default=5.0)
ap.add_argument('--level-hl', type=float, default=300.0)
ap.add_argument('--bias-hl', type=float, default=30.0)
ap.add_argument('--k', type=float, default=2.0)
ap.add_argument('--warm', type=int, default=300)
ap.add_argument('--level', default='key', choices=['key', 'vwap'])
ap.add_argument('--band', default='sigma', choices=['sigma', 'ticks', 'atr'])
ap.add_argument('--vref', type=int, default=20)
ap.add_argument('--start', type=int, default=34200)
ap.add_argument('--entry-end', type=int, default=54000)
ap.add_argument('--flat', type=int, default=57600)
a = ap.parse_args()
MARKS = [5, 10, 30, 60, 120, 300, 600, 1200, 1800]
con = duckdb.connect()
con.execute("SET memory_limit='2GB'; SET threads=2")
T = con.execute(f"SELECT * FROM read_parquet('{a.run}/legs/*.parquet')").df()
keys = T[['contract', 'td']].drop_duplicates().sample(n=min(a.sessions, len(T[['contract', 'td']].drop_duplicates())), random_state=a.seed)
# the ATR reference, recomputed from scratch (--band atr): the fronts as the engine's query (the previous session's
# volume leader), then per root the mean 1-minute vwap range of its previous --vref sessions (RTH, >= 3 bars)
FRONTS = None
if a.band == 'atr':
    FRONTS = con.execute(rf"""
        WITH v AS (SELECT CAST(regexp_extract(filename, '(\d{{4}}-\d{{2}}-\d{{2}})\.parquet$', 1) AS DATE) d, bucket >= {17 * 3600} nxt,
                          ticker, sum(volume) vol FROM read_parquet('{a.bars}/*.parquet', filename = true) GROUP BY ALL),
             s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol FROM v GROUP BY ALL),
             l AS (SELECT td, ticker, left(ticker, length(ticker) - 3) root,
                          row_number() OVER (PARTITION BY left(ticker, length(ticker) - 3), td ORDER BY vol DESC, ticker) rk FROM s)
        SELECT root, td, lag(ticker) OVER (PARTITION BY root ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td, root""").df().dropna()


def minute_range(t, p):
    if len(t) == 0: return float('nan')
    m = t // 60
    out = []
    i = 0
    while i < len(t):
        j = i
        while j < len(t) and m[j] == m[i]: j += 1
        out.append(p[i:j].max() - p[i:j].min()); i = j
    return float(np.mean(out))


def atr_ref(root, td):
    F = FRONTS[(FRONTS.root == root) & (FRONTS.td.astype(str).str[:10] < td)].sort_values('td')
    vals = []
    for _, fr in F.iloc[::-1].iterrows():
        d = str(fr.td)[:10]
        try:
            X = con.execute(f"""SELECT bucket t, vwap::DOUBLE p FROM read_parquet('{a.bars}/{d}.parquet')
                                WHERE ticker = '{fr.front}' AND bucket >= {a.start} AND bucket < {a.flat} ORDER BY bucket""").df()
        except duckdb.IOException:
            continue
        if len(X) >= 3:
            vals.append(minute_range(X.t.to_numpy(), X.p.to_numpy()))
            if len(vals) == a.vref: break
    return float(np.mean(vals)) if len(vals) == a.vref else float('nan')


LN2 = math.log(2.0)
lv, lL, lb = LN2 / a.vol_hl, LN2 / a.level_hl, LN2 / a.bias_hl
bad = 0; checked = 0; brute_pts = 0; ties = 0


def close(x, y, rel=1e-9):
    if x is None or y is None or (isinstance(x, float) and math.isnan(x)) or (isinstance(y, float) and math.isnan(y)):
        return (x is None or (isinstance(x, float) and math.isnan(x))) == (y is None or (isinstance(y, float) and math.isnan(y)))
    return abs(x - y) <= rel * max(1.0, abs(x), abs(y))


for _, kr in keys.iterrows():
    c, td = kr.contract, str(kr.td)[:10]
    B = con.execute(f"""SELECT bucket t, vwap::DOUBLE p, volume::DOUBLE v FROM read_parquet('{a.bars}/{td}.parquet')
                        WHERE ticker = '{c}' AND bucket >= {a.start} AND bucket < {a.flat} ORDER BY bucket""").df()
    t, p, v = B.t.to_numpy(), B.p.to_numpy(), B.v.to_numpy()
    n = len(t)
    # the EWMAs in clock time
    V = np.zeros(n); PV = np.zeros(n); W = np.zeros(n); W1 = np.zeros(n); W2 = np.zeros(n)
    for i in range(n):
        if i == 0:
            V[i], PV[i], W[i], W1[i], W2[i] = v[i], v[i] * p[i], v[i], v[i] * p[i], v[i] * p[i] ** 2
        else:
            dt = t[i] - t[i - 1]
            av, ab = math.exp(-lv * dt), math.exp(-lb * dt)
            V[i] = V[i - 1] * av + v[i]; PV[i] = PV[i - 1] * av + v[i] * p[i]
            W[i] = W[i - 1] * ab + v[i]; W1[i] = W1[i - 1] * ab + v[i] * p[i]; W2[i] = W2[i - 1] * ab + v[i] * p[i] ** 2
    # the running champion: index of the max decayed priority (ties newest; the control: oldest)
    cumV = np.cumsum(v); cumPV = np.cumsum(v * p)
    champ = np.full(n, -1); best = -math.inf; bi = -1
    for i in range(n):
        if V[i] > 0:
            s = math.log(V[i]) + lL * t[i]
            if (s >= best) if not a.control else (s > best):
                if s == best: ties += 1
                best, bi = s, i
        champ[i] = bi
    # brute force at sampled bars: max over every j <= k of V_j * exp(-lL (t_k - t_j))
    rng = np.random.default_rng(a.seed)
    for k in (sorted(set(rng.integers(0, n, size=min(300, n)).tolist())) if a.level == 'key' else []):
        val = V[:k + 1] * np.exp(-lL * (t[k] - t[:k + 1]))
        m = val.max()
        cand = np.nonzero(val >= m * (1 - 1e-12))[0]
        jb = cand[-1] if not a.control else cand[0]
        brute_pts += 1
        if jb != champ[k] and not np.isclose(val[jb], val[champ[k]], rtol=1e-12, atol=0):
            bad += 1; print(f'  {c} {td} bar {k}: brute champion {jb} vs running {champ[k]}')
    # the signals and the trips
    root = T[(T.contract == c)].root.iloc[0]
    tick = float(T[(T.contract == c)].tick.iloc[0])
    atr = atr_ref(root, td) if a.band == 'atr' else float('nan')
    if a.band == 'atr':
        eatr = T[(T.contract == c) & (T.td.astype(str).str[:10] == td)].atr.iloc[0]
        checked += 1
        if not close(float(eatr), atr):
            bad += 1; print(f'  {c} {td}: ATR engine {eatr} vs recomputed {atr}')
    trips = []; held = None; exiting = None
    for i in range(n):
        if exiting is not None:
            exiting.update(exit_t=int(t[i]), exit_px=p[i]); trips.append(exiting); exiting = None
        if held is not None and held['entry_t'] < 0:
            held.update(entry_i=i, entry_t=int(t[i]), entry_px=p[i])
        j = champ[i] if a.level == 'key' else i
        if W[i] > 0 and t[i] >= a.start + a.warm and i < n - 1 and j >= 0:
            level = PV[j] / V[j] if a.level == 'key' else cumPV[i] / cumV[i]
            mean = W1[i] / W[i]; sigma = math.sqrt(max(0.0, W2[i] / W[i] - mean * mean))
            band = a.k * (sigma if a.band == 'sigma' else (tick if a.band == 'ticks' else atr))
            want = 1 if mean > level + band else (-1 if mean < level - band else 0)
            pos = held['side'] if held is not None else 0
            if want != 0 and want != pos:
                if held is not None:
                    held.update(exit_sig_t=int(t[i]), reason='flip' if t[i] <= a.entry_end else 'close'); exiting = held; held = None
                if t[i] <= a.entry_end:
                    held = dict(side=want, sig_t=int(t[i]), level=level, level_t=int(t[j]), level_vol=V[j] if a.level == 'key' else cumV[i],
                                mean=mean, sigma=sigma, band=band,
                                vol_now=V[i], typ_med=float(np.median(V[:i + 1])), level_now=V[j] * math.exp(-lL * (t[i] - t[j])),
                                entry_i=-1, entry_t=-1, entry_px=float('nan'))
    if held is not None and held['entry_t'] >= 0:
        held.update(exit_sig_t=int(t[-1]), exit_t=int(t[-1]), exit_px=p[-1], reason='eod'); trips.append(held)
    for tr in trips:
        jj = tr['entry_i']
        for m in MARKS:
            while jj < n and t[jj] < tr['entry_t'] + m: jj += 1
            tr[f't_{m}'], tr[f'px_{m}'] = (int(t[jj]), p[jj]) if jj < n else (None, float('nan'))
    E = T[(T.contract == c) & (T.td.astype(str).str[:10] == td)].sort_values(['exit_t', 'sig_t']).to_dict('records')
    mine = sorted(trips, key=lambda x: (x['exit_t'], x['sig_t']))
    if len(E) != len(mine):
        bad += 1; print(f'  {c} {td}: {len(E)} engine trips vs {len(mine)} recomputed'); continue
    fields = ['side', 'sig_t', 'level', 'level_t', 'level_vol', 'mean', 'sigma', 'vol_now'] \
             + (['typ_med'] if 'typ_med' in T.columns else []) + (['level_now'] if 'typ_med' in T.columns and a.level == 'key' else []) \
             + (['band'] if 'band' in T.columns else []) + ['entry_t', 'entry_px', 'exit_sig_t',
              'exit_t', 'exit_px', 'reason'] + [f'{x}_{m}' for m in MARKS for x in ('t', 'px')]
    for e, r in zip(E, mine):
        for f in fields:
            ev, rv = e[f], r[f]
            if isinstance(ev, float) and isinstance(rv, (int, float)) or isinstance(rv, float):
                ok = close(None if ev is None else float(ev), None if rv is None else float(rv))
            else:
                ok = (None if (ev is None or (isinstance(ev, float) and math.isnan(ev))) else (int(ev) if not isinstance(ev, str) else ev)) == rv
            checked += 1
            if not ok:
                bad += 1
                if bad <= 20: print(f'  {c} {td} trip sig {r["sig_t"]}: {f} engine {ev} vs {rv}')
    print(f'{c} {td}: {n:,} bars, {len(mine)} trips', flush=True)

print(f'\n{checked:,} trip fields, {brute_pts:,} brute-force champion points, ties seen {ties}: {"PASS" if bad == 0 else f"FAIL ({bad})"}'
      + (' [CONTROL: ties to the oldest]' if a.control else ''))
sys.exit(0 if bad == 0 else 1)
