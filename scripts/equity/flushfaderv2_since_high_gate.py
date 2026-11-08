"""V2-44: a GATE on the time since the last new 20m high (user 2026-11-08: "we should also consider adding a gate for the time
since last 20m high. Maybe adding a gate that the decline should last 3.5m would push some of those earlier marginal trades
into the stronger buckets and not even lower our net profit. We need to test out how the actual gates would affect the
results rather than just using the feature as a filter."). V2-39 banded the BOOK; here the gate acts on the SIGNALS:
  1. every V2 engine-level trip (the V2-43 reference trips, 196,474: the vlog model's inputs included)
  2. drop the signals the gate refuses -- `bars` = base_v24's bars_since_high (present 1s bars since the bar after the last
     vwap above the prior 1,200-present-bar max) < N, or `secs` = the WALL-CLOCK seconds from that bar to the signal < N
     (from the 1s bars: on a thin name 210 present bars can be 10 minutes)
  3. re-run the book exactly as the reference does (raw px >= $1, mc=1 greedy per ticker-day in signal order), so a refused
     early signal frees its ticker-day for a later one
  4. size every trade with the V2-43 model (models/flushfaderv2/v2_coefficients.json; young x 0.8) and score at production
     size: % eq = 15 x clip(PF−1, 0.125, 2) x (ret_exit + 2 x 0.001 / entry_px), V2-26's metric
CONTROLS: the ungated re-run = the V2-43 reference book (35,796, keys and pf1_fitted identical); a same-count RANDOM drop of
signals (40 draws) re-run the same way = the noise band every gate must clear.
Run from research/:  python3 -u scripts/equity/flushfaderv2_since_high_gate.py > logs/since_high_gate.log 2>&1; echo exit $?
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--trips', default='data/flushfaderv2/vlog_ref/fit_vlog/flushfaderv2_reference_trips.parquet')
ap.add_argument('--book', default='data/flushfaderv2/vlog_ref/fit_vlog/flushfaderv2_reference_book.parquet')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--base', default='data/equity/flushfader/base_v24/trips_*.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--secs-cache', default='data/flushfaderv2/since_high_secs.parquet')
ap.add_argument('--draws', type=int, default=40)
args = ap.parse_args()
T0 = time.time()
con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=4")
T = con.execute(f"""
  WITH t AS (SELECT * FROM '{args.trips}'),
       h AS (SELECT DISTINCT ON (symbol, trade_date::VARCHAR, signal_sec) symbol, trade_date::VARCHAR AS trade_date, signal_sec, bars_since_high
             FROM read_parquet('{args.base}', union_by_name = true) WHERE (symbol, trade_date::VARCHAR) IN (SELECT symbol, trade_date FROM t))
  SELECT t.*, h.bars_since_high FROM t LEFT JOIN h USING (symbol, trade_date, signal_sec) ORDER BY symbol, trade_date, signal_sec""").df()
print(f"{len(T):,} trips; bars_since_high joined {T.bars_since_high.notna().mean():.2%}", flush=True)

# ---- the wall-clock seconds from the 20m-high bar to the signal (cached: one pass over the 1s bars)
try:
    S = pd.read_parquet(args.secs_cache); assert len(S) == len(T)
    T = T.merge(S, on=['symbol', 'trade_date', 'signal_sec'], how='left'); print("secs from the cache", flush=True)
except Exception:
    out = []
    for k, (day, g) in enumerate(T.groupby('trade_date', sort=True)):
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            sec = bars.bucket.values[bars.ticker.values == sym].astype(np.int64)
            i = np.searchsorted(sec, gt.signal_sec.values); j = np.clip(i - gt.bars_since_high.values.astype(np.int64), 0, len(sec) - 1)
            out.append(pd.DataFrame({'symbol': sym, 'trade_date': day, 'signal_sec': gt.signal_sec.values, 'secs_since_high': gt.signal_sec.values - sec[j]}))
        if k % 300 == 0: print(f"  secs {day} {k + 1}  {time.time() - T0:.0f}s", flush=True)
    S = pd.concat(out); S.to_parquet(args.secs_cache, index=False)
    T = T.merge(S, on=['symbol', 'trade_date', 'signal_sec'], how='left')
T = T.sort_values(['symbol', 'trade_date', 'signal_sec']).reset_index(drop=True)
print(f"secs_since_high: median {T.secs_since_high.median():.0f}, bars median {T.bars_since_high.median():.0f}; "
      f"bars < 210 {np.mean(T.bars_since_high < 210):.1%} of signals, secs < 210 {np.mean(T.secs_since_high < 210):.1%}", flush=True)

# ---- the V2-43 sizing (flushfaderv2_reference.py's design and pf1_of, in its order)
C = json.load(open(args.coef)); assert 'vlog_lo' in C, "the V2-43 coefficients"
def zz(x, lo, hi, m, sd): return np.where(np.isnan(x), 0.0, (np.clip(x, lo, hi) - m) / sd)
gb = np.digitize(T.gap_60.values, [1, 4])
X = np.column_stack([np.ones(len(T)), gb == 1, gb == 2, np.log(np.clip(T.volat.values, *C['volat_clip_bp'])),
                     (np.nan_to_num(T.rate600.values, nan=0.0) >= 0.07), (T.ht.values >= 1) & (T.ssh.values >= 300) & (T.ssh.values < 2400),
                     np.nan_to_num(T.dslo.values >= 0.04),
                     zz(T.s20.values, C['s20_lo'], C['s20_hi'], C['s20_mean'], C['s20_sd']),
                     zz(T.cusr_dn_k025.values.astype(float), C['cus_lo'], C['cus_hi'], C['cus_mean'], C['cus_sd']),
                     zz(T.vlog.values.astype(float), C['vlog_lo'], C['vlog_hi'], C['vlog_mean'], C['vlog_sd'])]).astype(float)
p = 1 / (1 + np.exp(-(X @ np.array(C['beta_p'])))); lam = np.exp(X @ np.array(C['beta_l'])) / np.exp(X @ np.array(C['beta_w']))
pf1 = p / ((1 - p) * lam) - 1; pf1 = np.where(T.barnum.values < 22, 0.8 * pf1, pf1)
R = T.ret_exit.values + 2 * .001 / T.entry_px.values; PEQ = 15 * np.clip(pf1, .125, 2) * R
YR = T.trade_date.str[:4].astype(int).values
key = (T.symbol + '|' + T.trade_date).values; ent = T.entry_sec.values; ext = T.exit_sec.values; pxok = T.entry_px.values >= 1.0
starts = np.flatnonzero(np.r_[True, key[1:] != key[:-1]]); ends = np.r_[starts[1:], len(T)]

def greedy(allowed):
    keep = np.zeros(len(T), bool)
    for a, z in zip(starts, ends):
        last = -1
        for i in range(a, z):
            if allowed[i] and pxok[i] and ent[i] >= last: keep[i] = True; last = ext[i]
    return keep

def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def stats(k):
    r = R[k]; rs = np.sort(r); y = YR[k]; e = PEQ[k]
    return dict(n=int(k.sum()), mean=r.mean(), pf=pf(r), pft=pf(rs[int(.05 * len(rs)):]), big=1000 * (r <= -.2).mean(), eq=e.sum(), eu=r.sum(),
                **{f'pf_{lab}': pf(r[m]) for lab, m in [('a', y <= 2022), ('b', (y >= 2023) & (y <= 2024)), ('c', y == 2025), ('d', y == 2026)]},
                **{f'eq_{lab}': e[m].sum() for lab, m in [('a', y <= 2022), ('b', (y >= 2023) & (y <= 2024)), ('c', y == 2025), ('d', y == 2026)]})

base = greedy(np.ones(len(T), bool))
Bk = pd.read_parquet(args.book).sort_values(['symbol', 'trade_date', 'signal_sec']).reset_index(drop=True)
kb = T[base].reset_index(drop=True)
same = len(kb) == len(Bk) and (kb[['symbol', 'trade_date', 'signal_sec']].values == Bk[['symbol', 'trade_date', 'signal_sec']].values).all()
dp = np.abs(pf1[base] - Bk.pf1_fitted.values).max() if same else np.nan
print(f"CONTROL ungated re-run vs the V2-43 reference book: {base.sum():,} vs {len(Bk):,} trades, keys identical {same}, max |Δ pf1| {dp:.1e}", flush=True)
S0 = stats(base); rng = np.random.default_rng(20261108)
print(f"\n| gate | signals refused | book trades | mean | PF | PF trim 5 % | ≤ −20 %/1,000 | % eq (production size) | Δ % eq | random same-count drop: PF · Δ % eq (p5–p95) | 2020–22 PF · % eq | 2023–24 | 2025 | 2026 |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
def row(lab, refused, s, band):
    print(f"| {lab} | {refused:,} | {s['n']:,} | {100*s['mean']:+.3f} % | {s['pf']:.3f} | {s['pft']:.2f} | {s['big']:.1f} | {s['eq']:+,.0f} | {s['eq'] - S0['eq']:+,.0f} | {band} | "
          + " | ".join(f"{s['pf_' + x]:.2f} · {s['eq_' + x]:+,.0f}" for x in 'abcd') + " |", flush=True)
row('none (production)', 0, S0, '—')
for col, edges in [('bars_since_high', [120, 180, 210, 240, 300, 450]), ('secs_since_high', [120, 180, 210, 240, 300, 450])]:
    for N in edges:
        allowed = ~(T[col].values < N); refused = int((~allowed).sum()); s = stats(greedy(allowed))
        rp, re_ = [], []
        for _ in range(args.draws):
            al = np.ones(len(T), bool); al[rng.choice(len(T), refused, replace=False)] = False; r_ = stats(greedy(al))
            rp.append(r_['pf']); re_.append(r_['eq'] - S0['eq'])
        band = f"{np.quantile(rp, .05):.3f}–{np.quantile(rp, .95):.3f} · {np.quantile(re_, .05):+,.0f} … {np.quantile(re_, .95):+,.0f}"
        row(f"{col.split('_')[0]} ≥ {N}", refused, s, band)
print(f"\ndone {time.time() - T0:.0f}s")
