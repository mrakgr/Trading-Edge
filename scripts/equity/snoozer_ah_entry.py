"""Snoozer AFTER-HOURS entries (user 2026-10-12): does leaving the 15:59 limit on for 10 minutes after the close help?

The Oms enters a Snoozer with a maker limit capped at the SIGNAL price (the decision bar's vwap, rounded onto the tick
grid against us: a buy floors, a short ceils — Broker.onGrid) and cancels it at 16:00. The research filled it at the
vwap of (15:59, 16:00] (px_lim_1559_1600), not at a limit. This measures, on the raw trades tape, per reference trade
(data/flushfader_gate_review/snoozer_reference_book.parquet, 1,587 trades 2016-08 .. 2026-08):

  1. dollar volume BETTER than / AT / WORSE than our limit (long: below / = / above; short: above / = / below) in
       pre    (signal second, 16:00:00)   continuous, the cross excluded
       cross  the closing auction prints (conditions 8 / 19), wherever stamped
       post   [16:00:00, 16:10:00)        after-hours continuous, the cross excluded
     LIT prints only (trf_id = 0 — a resting order fills on an exchange), the off-exchange total alongside;
     the price-discovery exclusions of trade_filters.py; condition 15 (the official-close REPORT, the cross's shares
     again) dropped.
  2. a conservative FILL MODEL — filled iff a lit print trades THROUGH the limit (strictly better) in the window,
     at the limit price — for windows (signal, 16:00) and (signal, 16:10), with and without the cross; the outcome
     ovn = (open_p1 + div_p1) / limit − 1 (short: negated) on filled trades, and the outcome of the MISSED ones at
     the research proxy (adverse selection: do we miss the ones that run away?).

Run from research/:
    python -u scripts/equity/snoozer_ah_entry.py > data/equity/snoozer_ah_entry.log 2>&1; echo exit $?
"""
import argparse, math, os, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfader_gate_review/snoozer_reference_book.parquet")
ap.add_argument("--trades", default="data/bulk/trades")
ap.add_argument("--out", default="data/equity/snoozer_ah_entry.parquet")
ap.add_argument("--post-end", type=int, default=58200, help="end of the after-hours window, ET seconds (58200 = 16:10)")
ap.add_argument("--limit", type=int, default=0, help="only the first N dates (a quick look)")
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, flush=True)

EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53]::UTINYINT[]"     # trade_filters.EXCLUDE_FOR_PRICE_DISCOVERY
CROSS = "[8, 19]::UTINYINT[]"                                        # closing prints
B = pd.read_parquet(args.book)
B["signal_sec"] = B.signal_sec.astype(int)
dates = sorted(B.trade_date.unique())
if args.limit: dates = dates[:args.limit]
log(f"{len(B):,} trades on {B.trade_date.nunique():,} dates; reading {len(dates):,}")

def on_grid(side_buy: bool, px: float) -> float:
    tick = 0.01 if px >= 1.0 else 0.0001
    steps = px / tick
    n = math.floor(steps + 1e-9) if side_buy else math.ceil(steps - 1e-9)
    return round(n * tick, 4)

con = duckdb.connect(); con.execute("SET memory_limit='6GB'"); con.execute("SET threads=6")
rows = []
for k, d in enumerate(dates):
    bk = B[B.trade_date == d]
    path = os.path.join(args.trades, f"{d}.parquet")
    if not os.path.exists(path):
        log(f"{d}: no tape"); continue
    tick_list = ", ".join(f"'{t}'" for t in bk.symbol.unique())
    T = con.execute(f"""
        WITH t AS (
          SELECT ticker, price, size, trf_id, conditions,
                 (epoch_ns(timezone('America/New_York', to_timestamp(sip_timestamp / 1e9)))
                    - epoch_ns(date_trunc('day', timezone('America/New_York', to_timestamp(sip_timestamp / 1e9))))) / 1e9 AS et
          FROM read_parquet('{path}')
          WHERE ticker IN ({tick_list}) AND size > 0)
        SELECT ticker, et, price, size, trf_id,
               list_has_any(conditions, {CROSS}) AS is_cross,
               list_has_any(conditions, [15]::UTINYINT[]) AS is_ocr,
               list_has_any(conditions, {EXCL}) AS is_excl
        FROM t WHERE et >= 57540 AND et < {args.post_end}""").df()
    for r in bk.itertuples():
        buy = r.side == "long"
        lim = on_grid(buy, r.p1559)
        x = T[(T.ticker == r.symbol)]
        x = x[~x.is_ocr & (x.is_cross | ~x.is_excl)]
        sigsec = x[(x.et >= r.signal_sec) & (x.et < r.signal_sec + 1) & ~x.is_cross]
        tape_sig_vwap = (sigsec.price * sigsec["size"]).sum() / sigsec["size"].sum() if len(sigsec) else np.nan
        after = x[x.et >= r.signal_sec + 1]
        better = (after.price < lim - 1e-9) if buy else (after.price > lim + 1e-9)
        at = (after.price - lim).abs() <= 1e-9
        dv = after.price * after["size"]
        lit = after.trf_id == 0
        win = {"pre": (~after.is_cross) & (after.et < 57600), "cross": after.is_cross, "post": (~after.is_cross) & (after.et >= 57600)}
        rec = {"symbol": r.symbol, "trade_date": d, "side": r.side, "cell": r.cell, "signal_sec": r.signal_sec,
               "p1559": r.p1559, "lim": lim, "tape_sig_vwap": tape_sig_vwap, "px_lim_1559_1600": r.px_lim_1559_1600,
               "open_p1": r.open_p1, "div_p1": r.div_p1, "ret": r.ret, "weight": r.weight}
        for w, m in win.items():
            for lab, b in (("better", better), ("at", at), ("worse", ~better & ~at)):
                rec[f"dv_{w}_{lab}"] = float(dv[m & b & lit].sum())
            rec[f"dv_{w}_offex"] = float(dv[m & ~lit].sum())
            thr = after[m & better & lit]
            rec[f"first_through_{w}"] = float(thr.et.min()) if len(thr) else np.nan
        # the first trade-through per post minute (16:00..16:09) for the decay table
        for mi in range(10):
            mm = win["post"] & better & lit & (after.et >= 57600 + 60 * mi) & (after.et < 57660 + 60 * mi)
            rec[f"dv_post_m{mi}_better"] = float(dv[mm].sum())
        rows.append(rec)
    if k % 100 == 0: log(f"{k+1}/{len(dates)} {d}: {len(bk)} trades, {len(T):,} prints")
R = pd.DataFrame(rows); os.makedirs(os.path.dirname(args.out), exist_ok=True); R.to_parquet(args.out, index=False)
log(f"wrote {args.out} ({len(R):,} rows)")

# ---------------------------------------------------------------- the report
def pf(x):
    x = x[~np.isnan(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
sgn = np.where(R.side == "long", 1.0, -1.0)
R["ovn_lim"] = sgn * ((R.open_p1 + R.div_p1) / R.lim - 1.0)
R["ovn_proxy"] = sgn * ((R.open_p1 + R.div_p1) / R.px_lim_1559_1600 - 1.0)
scale = (R.tape_sig_vwap / R.p1559).dropna()
print(f"\nscale check: tape vwap of the signal second / p1559: median {scale.median():.6f}, p1 {scale.quantile(.01):.4f}, p99 {scale.quantile(.99):.4f}, n {len(scale):,} of {len(R):,}")
print("\n## 1. Lit dollar volume relative to our limit, median per trade (and the share of trades with any)\n")
print("| side | n | window | better (fills us) p50 / any | at the limit p50 / any | worse p50 | off-exchange p50 |")
print("|---|---|---|---|---|---|---|")
for side in ("long", "short"):
    S = R[R.side == side]
    for w in ("pre", "cross", "post"):
        print(f"| {side} | {len(S):,} | {w} | {S[f'dv_{w}_better'].median():,.0f} / {(S[f'dv_{w}_better'] > 0).mean()*100:.0f} % | "
              f"{S[f'dv_{w}_at'].median():,.0f} / {(S[f'dv_{w}_at'] > 0).mean()*100:.0f} % | {S[f'dv_{w}_worse'].median():,.0f} | {S[f'dv_{w}_offex'].median():,.0f} |")
print("\n## 2. Fill model: filled iff a lit print trades THROUGH the limit in the window; outcome at the limit\n")
print("| side | model | filled | PF filled (weighted) | mean % filled | missed: n / PF at the proxy / mean % | all at the proxy: PF |")
print("|---|---|---|---|---|---|---|")
for side in ("long", "short"):
    S = R[R.side == side]; w = S.weight.values
    models = {"until 16:00": S.first_through_pre.notna(),
              "until 16:00 + the cross": S.first_through_pre.notna() | S.first_through_cross.notna(),
              "until 16:10 (no cross)": S.first_through_pre.notna() | S.first_through_post.notna(),
              "until 16:10 + the cross": S.first_through_pre.notna() | S.first_through_cross.notna() | S.first_through_post.notna()}
    for lab, f in models.items():
        f = f.values; o = S.ovn_lim.values; px = S.ovn_proxy.values
        print(f"| {side} | {lab} | {f.mean()*100:.1f} % ({f.sum():,}) | {pf(o[f] * w[f]):.2f} | {np.nanmean(o[f])*100:+.2f} | "
              f"{(~f).sum():,} / {pf(px[~f] * w[~f]):.2f} / {np.nanmean(px[~f])*100:+.2f} | {pf(px * w):.2f} |")
print("\n## 3. After-hours trade-through by minute (lit $ strictly better than the limit), share of trades with any\n")
print("| side | " + " | ".join(f"16:0{m}" for m in range(10)) + " |\n|---|" + "---|" * 10)
for side in ("long", "short"):
    S = R[R.side == side]
    print(f"| {side} | " + " | ".join(f"{(S[f'dv_post_m{m}_better'] > 0).mean()*100:.0f} %" for m in range(10)) + " |")
print("\n## 4. By year: fill rate until 16:00 vs until 16:10 (no cross)\n")
R["yr"] = R.trade_date.str[:4]
print("| side | year | n | until 16:00 | until 16:10 | gain |\n|---|---|---|---|---|---|")
for (side, yr), S in R.groupby(["side", "yr"]):
    a = S.first_through_pre.notna(); b = a | S.first_through_post.notna()
    print(f"| {side} | {yr} | {len(S)} | {a.mean()*100:.0f} % | {b.mean()*100:.0f} % | {(b.mean() - a.mean())*100:+.0f} pp |")
log("done")
