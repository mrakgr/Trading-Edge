"""THE CAP SEAL (2026-09-11, docs/oms_results.md §O0) — the Scanner's systems manager against the §S49ag capped replay.

The OMS run to seal:  scan oms --systems flushfader --schedule off --entry-mode cross --exit-mode cross --no-queue --free-on-fill
(entries fill at the next bar's vwap = the engine's own fill, exits at the engine's own exit bar, the engines' MOC closes
the book, blocked trips are SKIPPED, units free when the exit FILLS — the replay's conventions).

This script re-runs the replay's kernel (flushfader_capped_replay.py `capped_replay`, re-entries allowed = a ticker is busy
iff it has an OPEN position) on the SAME trips the adapters saw — the sealed research reference trips
(broad_reference_trips.parquet = the Scanner's engine-level FlushFader trips, zero-diff) — with the OMS's conventions:
  · decide at the SIGNAL second (the OMS decides when the message arrives), sorted (trade_date, signal_sec, symbol)
  · a position frees its units at its exit FILL second (exit_sec <= now frees before the decision at now)
  · the $1 floor on the SIGNAL vwap (the OMS tests signalPx)
  · units per trip = the OMS's own `units` for that signal (signals.parquet) — sizing is sealed separately (Sizing_Test.fsx)
and compares: the taken set, the reject reason of every trip, and Σ ret × units per day (|Δ| < tol).

Run from research/:
  python scripts/equity/oms_cap_seal.py --trips data/flushfader_gate_review/broad_reference_trips.parquet \
      --oms ../TradingEdge.Scanner/out/oms_seal --start 2025-01-02 --end 2026-08-31 --cap 20
"""
import argparse, sys
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--trips", required=True)
ap.add_argument("--oms", required=True, help="the OMS out dir (signals/positions/daily.parquet)")
ap.add_argument("--start", required=True); ap.add_argument("--end", required=True)
ap.add_argument("--cap", type=float, default=20.0)
ap.add_argument("--tol", type=float, default=1e-9)
ap.add_argument("--show", type=int, default=20)
ap.add_argument("--secdir", default="data/intraday_1s_slim")
a = ap.parse_args()

con = duckdb.connect()
T = con.sql(f"""select symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, exit_px, signal_vwap
               from read_parquet('{a.trips}') where trade_date >= '{a.start}' and trade_date <= '{a.end}'
               order by trade_date, signal_sec, symbol""").df()
S = con.sql(f"""select trade_date, sec, symbol, units, outcome, reason from read_parquet('{a.oms}/signals.parquet')
               where system = 'flushfader' and msg = 'long_mr' and trade_date >= '{a.start}' and trade_date <= '{a.end}'""").df()
P = con.sql(f"""select trade_date, symbol, signal_sec, units, ret, entry_sec, exit_sec, entry_px, exit_px, exit_reason
               from read_parquet('{a.oms}/positions.parquet') where system = 'flushfader' and trade_date >= '{a.start}' and trade_date <= '{a.end}'""").df()
# ⚠ the reference audit trail carries the NEXT-OPEN REWRITE on MOC exits (SignalSink.resolvedExit: exit_sec stays the
# close, exit_px becomes the next session's open — the overnight book the OMS does NOT hold). Restore the close-bar
# vwap for those trips from the 1s bars, so both sides price the same exit.
moc = T[T.exit_sec >= 46800].copy()
if len(moc):
    days = sorted(moc.trade_date.unique())
    fixes = {}
    for d in days:
        rows = moc[moc.trade_date == d]
        syms = ",".join("'" + s_ + "'" for s_ in rows.symbol.unique())
        bars = con.sql(f"""select ticker, bucket, vwap::DOUBLE v from read_parquet('{a.secdir}/{d}.parquet')
                          where ticker in ({syms}) and bucket in ({",".join(str(int(x)) for x in rows.exit_sec.unique())}) and vwap > 0 and volume > 0""").fetchall()
        for tk, b, v in bars: fixes[(d, tk, int(b))] = v
    T["exit_px_ref"] = T.exit_px
    k = list(zip(T.trade_date, T.symbol, T.exit_sec))
    T["exit_px"] = [fixes.get(kk, px) if kk in fixes else px for kk, px in zip(k, T.exit_px)]
    n_rw = int(sum(1 for kk, px, px0 in zip(k, T.exit_px, T.exit_px_ref) if kk in fixes and px != px0))
    print(f"MOC exits in the window: {len(moc):,}; next-open rewrites undone: {n_rw:,}")
print(f"trips {len(T):,}  oms signal rows {len(S):,}  oms positions {len(P):,}   window {a.start}..{a.end}")

# units per trip from the OMS's own message row (one row per signal; a re-signal is impossible in --no-queue mode
# only if the want is resting — count duplicates to be honest)
S["key"] = list(zip(S.trade_date, S.symbol, S.sec))
dup = S.key.duplicated().sum()
units = dict(zip(S.key, S.units))
T["key"] = list(zip(T.trade_date, T.symbol, T.signal_sec))
T["units"] = T.key.map(units)
missing = T.units.isna().sum()
print(f"OMS message rows for trips: missing {missing:,}, duplicate keys {dup:,}")

# ---- the replay with the OMS's conventions
take = np.zeros(len(T), np.int8); reason = np.full(len(T), -1, np.int8)
day = T.trade_date.values; sym = T.symbol.values; sig = T.signal_sec.values.astype(np.int64); ext = T.exit_sec.values.astype(np.int64)
svw = T.signal_vwap.values; u = T.units.fillna(0.0).values
i = 0; n = len(T)
while i < n:
    d = day[i]; j = i
    while j < n and day[j] == d: j += 1
    open_ext = []; open_sym = []; open_u = []
    for t in range(i, j):
        now = sig[t]
        keep = [(e, s_, uu) for e, s_, uu in zip(open_ext, open_sym, open_u) if e > now]
        open_ext = [k[0] for k in keep]; open_sym = [k[1] for k in keep]; open_u = [k[2] for k in keep]
        if sym[t] in open_sym:
            reason[t] = 1; continue                     # ticker busy (already_open) — the OMS tests this first
        if not (svw[t] >= 1.0):
            reason[t] = 4; continue                     # sub_dollar (signal vwap)
        if sum(open_u) + u[t] > a.cap + 1e-9:
            reason[t] = 2; continue                     # cap
        take[t] = 1; reason[t] = 0
        open_ext.append(ext[t]); open_sym.append(sym[t]); open_u.append(u[t])
    i = j
T["take"] = take; T["reason"] = reason
RN = {0: "taken", 1: "busy", 2: "cap", 4: "sub_dollar"}

# ---- 1. the taken set
P["key"] = list(zip(P.trade_date, P.symbol, P.signal_sec))
taken_replay = set(T.key[T["take"] == 1]); taken_oms = set(P.key)
only_r = sorted(taken_replay - taken_oms); only_o = sorted(taken_oms - taken_replay)
print(f"\nTAKEN: replay {len(taken_replay):,}  oms {len(taken_oms):,}  replay-only {len(only_r):,}  oms-only {len(only_o):,}")
for k in only_r[: a.show]: print("  replay-only", k, RN.get(int(T.reason[T.key == k].iloc[0]), "?"))
for k in only_o[: a.show]:
    r = S[S.key == k]
    print("  oms-only   ", k, list(zip(r.outcome, r.reason))[:3])

# ---- 2. the reasons, trip by trip
omap = {}
for k, o, r in zip(S.key, S.outcome, S.reason):
    if k in omap: continue
    if o == "entry_placed" or o == "reentry_placed": omap[k] = "taken"
    elif r in ("already_open", "ticker_busy"): omap[k] = "busy"
    elif r == "cap": omap[k] = "cap"
    elif r == "sub_dollar": omap[k] = "sub_dollar"
    else: omap[k] = f"{o}:{r}"
T["oms_reason"] = T.key.map(omap).fillna("no_row")
T["replay_reason"] = T.reason.map(RN)
ct = pd.crosstab(T.replay_reason, T.oms_reason)
print("\nREASONS (rows = replay, cols = OMS):"); print(ct.to_string())
mism = T[T.replay_reason != T.oms_reason]
print(f"reason mismatches: {len(mism):,} of {len(T):,}")
for _, r in mism.head(a.show).iterrows(): print("  ", r.symbol, r.trade_date, r.signal_sec, "replay", r.replay_reason, "oms", r.oms_reason)
# an OMS entry_placed that never became a position = a signal on the ticker's last bar (the engine has no trip either)
placed_unfilled = len([k for k, v in omap.items() if v == "taken" and k not in taken_oms])
print(f"OMS entries placed but never filled (last-bar signals, cancelled eod): {placed_unfilled:,}")

# ---- 3. P&L: Σ ret × units per day
T["ret"] = T.exit_px / T.entry_px - 1.0
rep = T[T["take"] == 1].assign(ru=lambda d: d.ret * d.units).groupby("trade_date").ru.sum()
oms = P.assign(ru=lambda d: d.ret * d.units).groupby("trade_date").ru.sum()
D = pd.concat([rep.rename("replay"), oms.rename("oms")], axis=1).fillna(0.0)
D["diff"] = (D.replay - D.oms).abs()
bad = D[D["diff"] > a.tol]
print(f"\nP&L (Σ ret × units per day): {len(D)} days, max |Δ| {D['diff'].max():.3e}, days over tol {len(bad)}; totals replay {D.replay.sum():+.6f} oms {D.oms.sum():+.6f}")
if len(bad): print(bad.head(a.show).to_string())
# per-position: entry/exit second and price must be the engine's own
m = T[T["take"] == 1].merge(P, on=["trade_date", "symbol", "signal_sec"], suffixes=("_t", "_o"))
dpx = np.maximum((m.entry_px_t - m.entry_px_o).abs().max() if len(m) else 0, (m.exit_px_t - m.exit_px_o).abs().max() if len(m) else 0)
dsec = int(max((m.entry_sec_t != m.entry_sec_o).sum(), (m.exit_sec_t != m.exit_sec_o).sum())) if len(m) else 0
print(f"matched positions {len(m):,}: max |Δ entry/exit px| {dpx:.3e}, entry/exit second mismatches {dsec}")

ok = (not only_r) and (not only_o) and len(mism) == 0 and len(bad) == 0 and dsec == 0 and dpx <= a.tol
print("\n" + ("✅ CAP SEAL: taken set, reasons, fills and P&L identical" if ok else "⚠ CAP SEAL: DIFFERENCES"))
sys.exit(0 if ok else 1)
