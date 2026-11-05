"""GF-65 — GapFader's exit at the SHORT-TERM HIGH instead of the 10:00 time stop (user 2026-11-05: "we should consider
exiting at the 1m-5m highs. That way we'd have a justification for having resting orders at the short term highs instead
of chasing down ... a GapFader variant that tries to exit at the 5m high after 10 ET. Let's also try it after 9:45 ET").

THE BOOK: production's (GF-54, data/equity/gapfader/model_v2.parquet): gap and the noisy auction gap <= −4 %, rvol60 <
0.02, dv60_med >= $500k, the model's PF−1 > 0; every year 2016-08 .. 2026-09 (the model was fitted on 2021–26, so 2016–20
is out of sample). Entry at the OFFICIAL OPEN (the cross). Sized trades use the production units (clip(PF−1, 0.125, 2)).

THE EXITS, all on the 1s bars (data/intraday_1s_slim, raw prices, present bars 09:30:00 .. 15:59:59), every return net
of 0.4c / share (r1's cost):
  base        the production model (GF-54's r1): the TARGET (50 % of the gap back to the flat price, close_m1 + div_m1) on
              a trade-through (a 1s vwap above it, filled AT the target) before 10:00, else the first 1s vwap at/after 10:00.
              (Live it is worked by the maker from 10:00 — the execution that lags.)
  hiN@S       the target stays, resting all day; from S (09:45 or 10:00) a resting sell also sits at the N-minute HIGH — the
              max of the 1s vwaps in the last N × 60 s, the bar itself excluded — following it as it moves, and it FILLS AT
              THAT LEVEL when a 1s vwap trades above it. Whichever fills first. Neither by 15:59:59: out at the last 1s
              vwap. N = 1, 2, 3, 5. This is what resting orders would get: the model IS the execution (no chasing).
  close       the target, else the last 1s vwap (the hold-to-close reference).
    cd research && python3 -u scripts/equity/gapfader_exit_highs.py > data/equity/gapfader/exit_highs.md 2> logs/gapfader_exit_highs.log
"""
import sys, time
import duckdb, numpy as np, pandas as pd

T0 = time.time()
def log(*a): print(f"[{time.time() - T0:7.1f}s]", *a, file=sys.stderr, flush=True)
OPEN, S945, S1000, END = 34200, 35100, 36000, 57600
NS = (1, 2, 3, 5)
STARTS = (("09:45", S945), ("10:00", S1000))
B = pd.read_parquet("data/equity/gapfader/model_v2.parquet")
B = B[(B.rvol60 < 0.02) & (B.dv60_med >= 5e5) & (B.gap <= -0.04) & (B.gap_est <= -0.04) & (B.pf1_new > 0)].copy()
B["flat"] = B.close_m1 + B.div_m1
B["target"] = B.open + 0.5 * (B.flat - B.open)
log(f"book {len(B):,} trades, {B.date.nunique():,} days")

con = duckdb.connect(); con.execute("SET memory_limit='2GB'"); con.execute("SET threads=4")
out = []
for k, (d, g) in enumerate(B.groupby("date")):
    con.register("g", g[["ticker"]])
    X = con.execute(f"""SELECT ticker, bucket, vwap::DOUBLE v FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
                        WHERE ticker IN (SELECT ticker FROM g) AND bucket >= {OPEN} AND bucket < {END} AND volume > 0
                        ORDER BY ticker, bucket""").df()
    con.unregister("g")
    by = {t: x for t, x in X.groupby("ticker")}
    for r in g.itertuples():
        x = by.get(r.ticker)
        if x is None or len(x) == 0: continue
        t, v = x.bucket.values, x.v.values
        res = {"ticker": r.ticker, "date": d, "units": r.units_new, "open": r.open}
        tgt_hit = np.flatnonzero(v > r.target)
        ti = tgt_hit[0] if len(tgt_hit) else None
        def ret(px): return (px / r.open - 1 - 0.004 / r.open) * 100
        # base: the target before 10:00, else the first vwap at/after 10:00
        i10 = np.searchsorted(t, S1000)
        if ti is not None and t[ti] < S1000: res.update(base=ret(r.target), base_kind="target", base_sec=t[ti])
        elif i10 < len(t): res.update(base=ret(v[i10]), base_kind="10:00", base_sec=t[i10])
        else: res.update(base=ret(v[-1]), base_kind="end", base_sec=t[-1])
        # close: the target, else the last vwap
        if ti is not None: res.update(close=ret(r.target), close_kind="target", close_sec=t[ti])
        else: res.update(close=ret(v[-1]), close_kind="end", close_sec=t[-1])
        s = pd.Series(v, index=pd.to_datetime(t, unit="s"))
        for n in NS:
            hi = s.rolling(f"{60 * n}s", closed="left").max().values     # the prior N minutes' max, this bar excluded
            for lab, S in STARTS:
                cand = np.flatnonzero((t >= S) & (v > hi))                # a trade-through of the resting high
                hi_i = cand[0] if len(cand) else None
                key = f"hi{n}@{lab}"
                choices = []
                if ti is not None: choices.append((t[ti], r.target, "target"))
                if hi_i is not None: choices.append((t[hi_i], hi[hi_i], "high"))
                if choices:
                    sec, px, kind = min(choices, key=lambda c: (c[0], c[2] != "target"))
                else: sec, px, kind = t[-1], v[-1], "end"
                res.update({key: ret(px), key + "_kind": kind, key + "_sec": sec})
        out.append(res)
    if k % 200 == 0: log(f"  {k + 1} days, {d}, {len(out):,} trades")
R = pd.DataFrame(out); R["yr"] = R.date.str[:4].astype(int)
R.to_parquet("data/equity/gapfader/exit_highs.parquet")
log(f"done: {len(R):,} trades -> data/equity/gapfader/exit_highs.parquet")

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def hms(s): s = int(s); return f"{s // 3600:02d}:{s % 3600 // 60:02d}"
VAR = ["base", "close"] + [f"hi{n}@{lab}" for lab, _ in STARTS for n in NS]
print("# GF-65 — GapFader's exit at the short-term HIGH instead of the 10:00 time stop\n")
print(f"Production book (GF-54 gates, PF−1 > 0), {len(R):,} trades {R.date.min()} .. {R.date.max()}; entry at the official open; "
      "net of 0.4c / share; sized = Σ units × r (% · unit). The target (50 % of the gap) rests all day in every variant.\n")
print("| exit | PF | 2016–20 | 2021–26 | mean % | median % | win % | sized Σ | by target | by the high | at the end | median exit | held past 12:00 | worst % |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for k in VAR:
    x = R[k].values; kind = R[k + "_kind"]; sec = R[k + "_sec"]
    e, l = R.yr <= 2020, R.yr >= 2021
    print(f"| {k} | {pf(x):.3f} | {pf(x[e]):.3f} | {pf(x[l]):.3f} | {np.mean(x):+.2f} | {np.median(x):+.2f} | {100 * (x > 0).mean():.0f} | "
          f"{(R.units * x).sum():,.0f} | {100 * (kind == 'target').mean():.0f} % | {100 * kind.isin(['high', '10:00']).mean():.0f} % | "
          f"{100 * (kind == 'end').mean():.0f} % | {hms(np.median(sec))} | {100 * (sec >= 43200).mean():.0f} % | {np.min(x):+.1f} |")
print("\n## PF by year\n")
print("| exit | " + " | ".join(str(y) for y in sorted(R.yr.unique())) + " |\n|---|" + "---|" * R.yr.nunique())
for k in VAR:
    print(f"| {k} | " + " | ".join(f"{pf(R[R.yr == y][k].values):.2f}" for y in sorted(R.yr.unique())) + " |")
