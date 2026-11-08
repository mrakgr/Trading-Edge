"""V2-51: the VOLATILITY (volat_20m) and COIL (consol_5m_lag1m) slots — present-bar vs wall-clock GRID, complete vs
+ the PARTIAL current slot (user 2026-11-08: "What I am wondering isn't only whether we should include partial bars, but
also whether we should grid align them much like we'll be doing the price slope.").
Production (the engine, research Intraday.fs): SLOTS of 30 PRESENT bars from the session's first bar (SlotVwapMa); slot
vwap = Σ vwap·vol / Σ vol; r = ln(V / V_prev) per completed slot; volat_20m = the bias-corrected EWMA of |r|, half-life 40
slots (EmaHlMa). coil = SlotVarRatioMa(30, 12).Ratio(10, 2): Σ n_i·var_i / (N · total var) of ln vwap (per bar,
unweighted) over the 10 completed slots ending 2 slots ago (needs 12 completed). Both are ENGINE GATES (volat >= 40 bp,
rule140: no trade when volat >= 140 bp and gap_60 >= 4; coil <= 0.22) and volat is a sizing term (ln clip(volat, 40, 250)).
Constructions, each for both features:
  bar      = production: 30-present-bar slots, completed only            (the CONTROL: = base_v24's columns)
  bar+p    = + the current PARTIAL slot as the newest (volat: one more EWMA step with its return; coil: the slot list
             ends at the partial, the same k = 10, lag = 2)
  grid     = 30-second WALL-CLOCK slots g = (sec − 34200) // 30, zero-volume (empty) slots absent; completed = before the
             signal's slot (or the signal's own when the signal second ends it)
  grid+p   = + the signal's partial grid slot
Candidates: base_v24's trips (every V2 engine-level trip at the 20 bp engine floor) passing EVERY other production gate
(the reference's mask without volat >= 40, rule140 and coil) — the signals a new definition could let in or refuse.
(1) SIZING ONLY: B's book (the V2-49 reference, 35,519), ln volat replaced by each construction's; V2-42's holdout.
(2) THE SYSTEM: each construction's gates (volat >= T, rule140 at 140/40 × T, coil <= C) on the candidates, the greedy
    book ($1 floor, mc=1 per ticker-day in signal order), the B design refit out of sample (fwd / mirror) with that
    construction's volat; thresholds RAW (40 bp / 0.22) and QUANTILE-MATCHED (T, C passing the same candidate count as
    production's). The price / volume slopes for trips outside the reference: two-pass (V2-47's), = the canonical to ~1e-9.
Run from research/:  python3 -u scripts/equity/flushfaderv2_slot_clock.py --compute > logs/slot_clock_compute.log 2>&1
                     python3 -u scripts/equity/flushfaderv2_slot_clock.py > logs/slot_clock.log 2>&1
"""
import argparse, json, time, duckdb, numpy as np, pandas as pd
from scipy.signal import lfilter
ap = argparse.ArgumentParser()
ap.add_argument('--base', default='data/equity/flushfader/base_v24/*.parquet')
ap.add_argument('--cands', default='data/v2_candidates/mr_candidate_1s_v2/*/*.parquet')
ap.add_argument('--book', default='data/flushfaderv2/pgrid_ref/fit_b/flushfaderv2_reference_book.parquet')
ap.add_argument('--bars', default='data/intraday_1s_slim')
ap.add_argument('--coef', default='models/flushfaderv2/v2_coefficients.json')
ap.add_argument('--out', default='data/flushfaderv2/slot_clock.parquet')
ap.add_argument('--compute', action='store_true')
ap.add_argument('--limit-days', type=int, default=0, help='smoke test: the first N days only')
args = ap.parse_args()
CREDIT, YOUNG_SCALE, SLOT, HL, W = 0.001, 0.8, 30, 40.0, 1200
ALPHA = 1.0 - 0.5 ** (1.0 / HL)
EARLY = {"2020-11-27", "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24", "2024-07-03", "2024-11-29", "2024-12-24",
         "2025-07-03", "2025-11-28", "2025-12-24", "2026-11-27", "2026-12-24"}
CONS = ['bar', 'barp', 'grid', 'gridp']

def candidates(con):
    D = con.execute(f"""
      SELECT symbol, trade_date::VARCHAR AS trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit, signal_vwap,
             volat_20m * 1e4 AS volat, consol_5m_lag1m AS coil, gap_60, halts_today AS ht, secs_since_halt AS ssh,
             chg_since_run_first_low AS crf, lows_since_first_low_600 AS l600, bars_since_first_low_600 AS b600, abs(eff_10m) AS eff10a,
             bars_since_high, vol_10_bar, vol_60_bar, vol_1200_bar, dlv_1200, dlv2_1200, sess_low, cusr_dn_k025, c.barnum
      FROM read_parquet('{args.base}', union_by_name = true) t
      LEFT JOIN (SELECT ticker, date, barnum FROM read_parquet('{args.cands}', hive_partitioning = true)) c ON c.ticker = t.symbol AND c.date = t.trade_date
      WHERE gap_60 < 40 AND lows_since_first_low_300 >= 6 AND lows_since_first_low_180 >= 3 AND signal_sec <= 54000
        AND abs(eff_10m) >= 0.15 AND chg_since_run_first_low <= -0.002 AND bars_since_high >= 300 AND ret_exit IS NOT NULL
        AND NOT (halts_today >= 4 AND secs_since_halt < 300)
      ORDER BY symbol, trade_date, signal_sec""").df()
    with np.errstate(divide='ignore', invalid='ignore'):
        v10r = (D.vol_10_bar.values / 10.0) / (D.vol_60_bar.values / 60.0)
        mean = D.dlv_1200.values / D.vol_1200_bar.values; var = D.dlv2_1200.values / D.vol_1200_bar.values - mean * mean
        z20 = np.where(var > 0, (np.log(D.signal_vwap.values) - mean) / np.sqrt(np.where(var > 0, var, 1.0)), np.nan)
        dslo = D.signal_vwap.values / D.sess_low.values - 1.0
    nz = lambda m: np.where(np.isnan(m.astype(float)), False, m).astype(bool)
    early = D.trade_date.isin(EARLY).values
    M = (np.where(early, D.signal_sec.values <= 43200, True) & nz(v10r >= 0.75) & nz(z20 < -1.5)
         & ~(nz(dslo > 0.0) & nz(dslo <= 0.03)) & D.barnum.notna().values)
    D = D[M].reset_index(drop=True); D['dslo'] = dslo[M]
    D['rate600'] = D.l600 / D.b600.where(D.b600 > 0)
    return D

def ewma_last(x):
    """bias-corrected EWMA after each push (EmaHlMa): num/den"""
    num = lfilter([ALPHA], [1.0, -(1.0 - ALPHA)], x); den = lfilter([ALPHA], [1.0, -(1.0 - ALPHA)], np.ones(len(x)))
    return num, den

def coil_of(n, s1, s2, end):
    """SlotVarRatioMa.Ratio(10, 2) over slot stats arrays (oldest first), the newest slot at index `end`"""
    hi = end - 2; lo = hi - 9
    if lo < 0 or end - 11 < 0: return np.nan
    nn, a1, a2 = n[lo:hi + 1], s1[lo:hi + 1], s2[lo:hi + 1]
    m = a1 / nn; w = np.maximum(0.0, a2 - nn * m * m).sum(); sn = nn.sum(); mt = a1.sum() / sn; tot = a2.sum() / sn - mt * mt
    return np.nan if tot <= 0 else min(1.0, max(0.0, (w / sn) / tot))

def ols(x, y):
    x = x - x.mean(); return (x @ (y - y.mean())) / (x @ x)

def compute():
    con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=3")
    D = candidates(con)
    if args.limit_days: D = D[D.trade_date.isin(sorted(D.trade_date.unique())[:args.limit_days])].reset_index(drop=True)
    print(f"{len(D):,} candidate trips (every gate but volat >= 40, rule140, coil), {D.trade_date.nunique():,} days", flush=True)
    con.execute("SET memory_limit='1GB'")   # 2026-11-08: a 3G scope OOM-killed the first run at 78 % (DuckDB's cache + the rows)
    import os, glob; pdir = args.out + '.parts'; os.makedirs(pdir, exist_ok=True)
    done_days = set()
    for f in glob.glob(f'{pdir}/*.parquet'): done_days |= set(pd.read_parquet(f, columns=['trade_date']).trade_date.unique())
    print(f"resume: {len(done_days)} days already in {pdir}", flush=True)
    out = []; T0 = time.time(); part = len(glob.glob(f'{pdir}/*.parquet'))
    def flush():
        nonlocal out, part
        if out: pd.DataFrame(out).to_parquet(f'{pdir}/part_{part:03d}.parquet', index=False); part += 1; out = []
    for k, (day, g) in enumerate(D.groupby('trade_date', sort=True)):
        if day in done_days: continue
        tick = list(g.symbol.unique())
        bars = con.execute(f"SELECT ticker, bucket, volume, vwap FROM '{args.bars}/{day}.parquet' WHERE ticker IN ({','.join('?' * len(tick))}) "
                           "AND volume > 0 AND bucket >= 34200 ORDER BY ticker, bucket", tick).df()
        for sym, gt in g.groupby('symbol'):
            bt = bars[bars.ticker == sym]; sec = bt.bucket.values.astype(np.int64)
            v = bt.volume.values.astype(np.float64); px = bt.vwap.values.astype(np.float64); pv = px * v
            ly = np.log(px); y = ly - ly[0]; nb = len(sec)
            if nb == 0: continue
            slot_of = {'bar': np.arange(nb) // SLOT, 'grid': (sec - 34200) // SLOT}
            S_ = {}
            for clk, sid in slot_of.items():
                u, first = np.unique(sid, return_index=True)            # non-empty slots, in order
                inv = np.searchsorted(u, sid)
                sv = np.bincount(inv, weights=v); sp = np.bincount(inv, weights=pv)
                cn = np.bincount(inv).astype(float); c1 = np.bincount(inv, weights=y); c2 = np.bincount(inv, weights=y * y)
                cv = np.cumsum(v); cpv = np.cumsum(pv); cy = np.cumsum(y); cyy = np.cumsum(y * y)
                S_[clk] = (u, inv, first, sv, sp, cn, c1, c2, cv, cpv, cy, cyy)
            for row in gt.itertuples():
                S = int(row.signal_sec); i = int(np.searchsorted(sec, S)); o = dict(symbol=sym, trade_date=day, signal_sec=S)
                if not (i < nb and sec[i] == S): out.append(o); continue
                for clk in ('bar', 'grid'):
                    u, inv, first, sv, sp, cn, c1, c2, cv, cpv, cy, cyy = S_[clk]
                    j = inv[i]                                          # the signal bar's slot
                    done = (i + 1) % SLOT == 0 if clk == 'bar' else (S - 34200) % SLOT == SLOT - 1
                    nc = j + 1 if done else j                           # completed slots
                    V = sp[:nc] / sv[:nc]
                    # partial slot = the signal's slot up to (and including) bar i, when not complete
                    a = first[j]
                    if not done:
                        pv_p = cpv[i] - (cpv[a - 1] if a > 0 else 0.0); v_p = cv[i] - (cv[a - 1] if a > 0 else 0.0)
                        n_p = float(i - a + 1); s1_p = cy[i] - (cy[a - 1] if a > 0 else 0.0); s2_p = cyy[i] - (cyy[a - 1] if a > 0 else 0.0)
                    for withp in (False, True):
                        lab = clk + ('p' if withp else '')
                        VV = np.append(V, pv_p / v_p) if (withp and not done) else V
                        if len(VV) >= 2:
                            r = np.abs(np.log(VV[1:] / VV[:-1])); num, den = ewma_last(r); o[f'volat_{lab}'] = num[-1] / den[-1] * 1e4
                        nn, a1, a2 = cn[:nc], c1[:nc], c2[:nc]
                        if withp and not done: nn, a1, a2 = np.append(nn, n_p), np.append(a1, s1_p), np.append(a2, s2_p)
                        o[f'coil_{lab}'] = coil_of(nn, a1, a2, len(nn) - 1)
                if i >= W - 1:   # the B slopes, two-pass (V2-47)
                    ss, sv1, sp1 = sec[i - W + 1:i + 1], v[i - W + 1:i + 1], pv[i - W + 1:i + 1]
                    gi = (ss - 34200) // 60; jj = gi - gi.min(); n = int(jj.max()) + 1
                    gv = np.bincount(jj, weights=sv1, minlength=n); gp = np.bincount(jj, weights=sp1, minlength=n); m = gv > 0
                    o['pgrid60'] = ols((np.arange(n)[m] * 60).astype(float), np.log(gp[m] / gv[m]))
                    o['vrate_1200'] = ols((ss - 34200).astype(float), np.log(sv1))
                out.append(o)
        if k % 100 == 0: flush(); print(f"  {day}  {k + 1}/{D.trade_date.nunique()}  {time.time() - T0:.0f}s", flush=True)
    flush()
    P = pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{pdir}/*.parquet'))], ignore_index=True)
    R = D.merge(P, on=['symbol', 'trade_date', 'signal_sec'], how='left')
    for f, ref in (('volat_bar', 'volat'), ('coil_bar', 'coil')):
        ok = R[f].notna() & R[ref].notna(); d = np.abs(R[f][ok] - R[ref][ok])
        print(f"CONTROL {f} vs base_v24 {ref}: max |Δ| {d.max():.2e}, median {d.median():.2e}; NaN agree {(R[f].isna() == R[ref].isna()).mean():.4%}")
    R.to_parquet(args.out, index=False); print(f"wrote {args.out}: {len(R):,} rows; done {time.time() - T0:.0f}s")

def fit_logistic(X, y, it=60):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12
        bn = np.linalg.solve((X * w[:, None]).T @ X, (X * w[:, None]).T @ (eta + (y - mu) / w))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def fit_loglink(X, y, it=60):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta)
        bn = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ (eta + (y - mu) / mu))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def pf(r): l = -r[r < 0].sum(); return np.inf if l == 0 else r[r > 0].sum() / l
def zs(x, ref=None):
    ref = x if ref is None else ref
    lo, hi = np.nanquantile(ref, [0.01, 0.99]); c = np.clip(ref, lo, hi); return np.nan_to_num((np.clip(x, lo, hi) - np.nanmean(c)) / np.nanstd(c))

def holdout(B, volcol):
    """V2-42's harness on book B with its own volat column: OOS units·% per trade (x) — fwd 2020–23 -> 2024–26, mirror"""
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.str[:4].astype(int).values; young = (B.barnum.values < 22)
    gb = np.digitize(B.gap_60.values, [1, 4])
    X = np.column_stack([np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B[volcol].values, 40.0, 250.0)),
                         np.nan_to_num(B.rate600.values, nan=0.0) >= 0.07, (B.ht.values >= 1) & (B.ssh.values >= 300) & (B.ssh.values < 2400),
                         np.nan_to_num(B.dslo.values >= 0.04), zs(B.pgrid60.values * 6e5), zs(B.cusr_dn_k025.values.astype(float)),
                         zs(B.vrate_1200.values * 1200.0)]).astype(float)
    x = np.zeros(len(B))
    for fm in (YR <= 2023, YR > 2023):
        f = fm & ~young; app = ~fm
        bp = fit_logistic(X[f], WIN[f] * 1.0); bw = fit_loglink(X[f & WIN], R[f & WIN]); bl = fit_loglink(X[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(X @ bp))); mm = p * np.exp(X @ bw) / ((1 - p) * np.exp(X @ bl)) - 1; mm = np.where(young, YOUNG_SCALE * mm, mm)
        x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]
    return x, R, YR

def greedy(T, allowed):
    key = (T.symbol + '|' + T.trade_date).values; ent = T.entry_sec.values; ext = T.exit_sec.values; pxok = T.entry_px.values >= 1.0
    keep = np.zeros(len(T), bool); starts = np.flatnonzero(np.r_[True, key[1:] != key[:-1]]); ends = np.r_[starts[1:], len(T)]
    for a, z in zip(starts, ends):
        last = -1
        for i in range(a, z):
            if allowed[i] and pxok[i] and ent[i] >= last: keep[i] = True; last = ext[i]
    return keep

def report():
    T = pd.read_parquet(args.out).sort_values(['symbol', 'trade_date', 'signal_sec']).reset_index(drop=True)
    print(f"{len(T):,} candidates; coverage " + ", ".join(f"{c} {T[c].notna().mean():.2%}" for c in [f'{f}_{k}' for f in ('volat', 'coil') for k in CONS] + ['pgrid60']))
    print("Spearman with production: " + ", ".join(f"volat_{k} {T[f'volat_{k}'].corr(T.volat_bar, method='spearman'):.3f} / coil_{k} {T[f'coil_{k}'].corr(T.coil_bar, method='spearman'):.3f}" for k in CONS[1:]))
    nz = lambda m: np.where(np.isnan(m.astype(float)), False, m).astype(bool)
    def gate(k, Tv, Cc):
        vol = T[f'volat_{k}'].values; co = T[f'coil_{k}'].values
        return nz(vol >= Tv) & ~(nz(vol >= 140.0 * Tv / 40.0) & nz(T.gap_60.values >= 4)) & nz(co <= Cc)
    prod = gate('bar', 40.0, 0.22); keep0 = greedy(T, prod)
    REF = pd.read_parquet(args.book, columns=['symbol', 'trade_date', 'signal_sec']).assign(trade_date=lambda d: d.trade_date.astype(str).str[:10])
    kb = T[keep0][['symbol', 'trade_date', 'signal_sec']].reset_index(drop=True)
    same = len(kb) == len(REF) and (kb.sort_values(['symbol', 'trade_date', 'signal_sec']).values == REF.sort_values(['symbol', 'trade_date', 'signal_sec']).values).all()
    print(f"CONTROL production gates on the candidates: {prod.sum():,} trips (reference 194,598), book {keep0.sum():,} (reference {len(REF):,}), keys identical {same}\n")
    # (1) sizing only, on B's book
    Bk = T[keep0].reset_index(drop=True)
    base, R, YR = holdout(Bk, 'volat_bar'); years = sorted(set(YR))
    ud, inv = np.unique(Bk.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261108)
    picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
    pfw = lambda x: (picks @ np.where(x > 0, x, 0)) / -(picks @ np.where(x < 0, x, 0)) - 1
    print("**(1) SIZING ONLY** — B's book, the ln volat term from each construction (OOS sized PF−1; Δ vs production's volat, paired day bootstrap)\n")
    print("| volat construction | OOS sized PF−1 | " + " | ".join(map(str, years)) + " | Δ [90 %] · share > 0 |\n|---|---|" + "---|" * (len(years) + 1))
    for k in CONS:
        x, _, _ = holdout(Bk.assign(_v=Bk[f'volat_{k}'].fillna(Bk.volat_bar)), '_v'); d = pfw(x) - pfw(base)
        print(f"| {k} | {pf(x) - 1:.4f} | " + " | ".join(f"{pf(x[YR == y]) - 1:.3f}" for y in years) + f" | {pf(x) - pf(base):+.4f} [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}] · {100 * (d > 0).mean():.0f} % |", flush=True)
    # (2) the system
    print("\n**(2) THE SYSTEM** — each construction's gates on the candidates, the greedy book, the B design refit out of sample\n")
    print("| construction | thresholds | engine trips | book | book PF (equal size) | OOS sized PF−1 | OOS net units·% | " + " | ".join(map(str, years)) + " (OOS net) |\n|---|---|---|---|---|---|---|" + "---|" * len(years))
    n_v = int(nz(T.volat_bar.values >= 40.0).sum()); n_c = int(nz(T.coil_bar.values <= 0.22).sum())
    for k in CONS:
        vol = T[f'volat_{k}'].values; co = T[f'coil_{k}'].values
        Tq = float(np.sort(vol[~np.isnan(vol)])[::-1][n_v - 1]); Cq = float(np.sort(co[~np.isnan(co)])[n_c - 1])
        for lab, Tv, Cc in (('raw 40 bp / 0.22', 40.0, 0.22), (f'matched {Tq:.1f} bp / {Cq:.3f}', Tq, Cq)):
            if k == 'bar' and lab.startswith('matched'): continue
            al = gate(k, Tv, Cc); kp = greedy(T, al); Bk2 = T[kp].reset_index(drop=True).assign(_v=lambda d: d[f'volat_{k}'])
            x, R2, Y2 = holdout(Bk2, '_v')
            print(f"| {k} | {lab} | {al.sum():,} | {kp.sum():,} | {pf(R2):.3f} | {pf(x) - 1:.4f} | {x.sum():+,.0f} | " + " | ".join(f"{x[Y2 == y].sum():+,.0f}" for y in years) + " |", flush=True)

compute() if args.compute else report()
