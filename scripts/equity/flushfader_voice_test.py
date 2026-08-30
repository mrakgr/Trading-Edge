"""Test a CANDIDATE ROSTER VOICE against the incumbent 6 + S-tier.

The roster is a UNION at vote >= 1, so (S43-passim) **a voice's whole value is
its SOLO trips** — the ones no other voice admits. A candidate that only
re-admits trips the roster already has is free PF but adds nothing.

Reports, for each candidate threshold:
  * book with / without the candidate (traded n, PF, win, avg, worst)
  * the candidate's SOLO trips (admitted by it and by NO incumbent)
  * per-year and per-tier for the augmented book
  * equity sim at 1% D-base
  * LEAVE-ONE-OUT over all voices incl. the candidate — the only test that says
    whether a seat is EARNED rather than merely harmless

Usage:
  python scripts/equity/flushfader_voice_test.py \
      --cand "downticks_since_uptick >= 8" "downticks_since_uptick >= 10"
"""
import argparse, duckdb, numpy as np, warnings
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--cand", nargs="+", required=True, help="SQL predicate(s) to test as a voice")
ap.add_argument("--trips", default="data/equity/flushfader/v47_spec20/trips_p*.parquet")
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--mult", type=float, nargs=4, default=[2.44, 1.80, 1.14, 1.00])
ap.add_argument("--base", type=float, default=0.01)
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

VOICES = [
    ("v20",      "volat_20m*1e4 >= 140"),
    ("d20a",     "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28"),
    ("dslo",     "signal_vwap/sess_low - 1 >= 0.08"),
    ("vexp",     "(volat_slope_10m - volat_slope_20m)*2e4 > 12"),
    ("vcrush",   "volat_slope_5m*2e4 <= -24"),
    ("acneg",    "ac1_ewma < -0.1"),
    ("legage",   f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}"),
    ("dsu",      "downticks_since_uptick >= 8"),
    ("haltband", "secs_since_halt >= 1200 AND secs_since_halt < 4800"),
    ("Stier",    "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200"),
]
NAMES = [n for n, _ in VOICES] + [f"cand{i}" for i in range(len(args.cand))]
sel = ", ".join(f"COALESCE({e}, false) AS {n}" for n, e in VOICES) \
      + ", " + ", ".join(f"COALESCE({e}, false) AS cand{i}" for i, e in enumerate(args.cand))

con = duckdb.connect()
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
F = con.execute(f"""
SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit, volat_20m,
       substr(trade_date::VARCHAR,1,4) AS y,
       CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
            WHEN gap_adj_1200<15 THEN 'B'
            WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier, {sel}
FROM read_parquet('{args.trips}')
WHERE {RAWPX} >= 1 AND gap_60 < 4 AND volat_20m >= 0.004 AND signal_sec <= 54000
ORDER BY symbol, trade_date, signal_sec""").fetchdf()
V = {n: F[n].values for n in NAMES}
INC = np.logical_or.reduce([V[n] for n, _ in VOICES])


def mc1(f):
    keep, last, prev = np.zeros(len(f), bool), -1, None
    k = (f.symbol.values + "_" + f.trade_date.astype(str).values)
    e, x = f.entry_sec.values, f.exit_sec.values
    for i in range(len(f)):
        if k[i] != prev:
            prev, last = k[i], -1
        if e[i] >= last:
            keep[i] = True
            last = x[i]
    return f[keep]


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else g / l


def pfs(v):
    return "   inf" if np.isinf(v) else f"{v:6.3f}"


def line(lab, mask):
    b = mc1(F[mask])
    r = b.ret_exit.values
    if len(r) == 0:
        return f"{lab:<30}{int(mask.sum()):>7}{0:>8}"
    t = pf(r[r >= np.quantile(r, args.trim)]) if len(r) >= 20 else float("nan")
    return (f"{lab:<30}{int(mask.sum()):>7}{len(r):>8}{pfs(pf(r))}{t:>9.2f}"
            f"{(r > 0).mean()*100:>7.1f}{r.mean()*100:>+7.2f}{r.min()*100:>+8.1f}")


HDR = f"{'':<30}{'mc0':>7}{'traded':>8}{'PF':>7}{'trimPF':>9}{'win%':>7}{'avg%':>7}{'worst%':>8}"
print(HDR)
print(line("BOOK (6 voices + S-tier)", INC))
for i, c in enumerate(args.cand):
    N = V[f"cand{i}"]
    print(line(f"  + [{c}]", INC | N))
    print(line(f"    SOLO (no incumbent)", N & ~INC))

# ---- equity sim + per-year/per-tier for each augmented book -------------
MU = dict(zip("ABCD", args.mult))
for i, c in enumerate(args.cand):
    for lab, mask in [("INCUMBENT", INC), (f"+ {c}", INC | V[f'cand{i}'])]:
        b = mc1(F[mask]).sort_values(["trade_date", "entry_sec"])
        r = b.ret_exit.values
        size = np.array([args.base * MU[t] * np.sqrt(99.0 / (v * 1e4))
                         for t, v in zip(b.tier.values, b.volat_20m.values)])
        contrib = size * r
        eq = np.cumprod(1 + contrib)
        yrs = len(set(b.y))
        dd = (eq / np.maximum.accumulate(eq) - 1).min() * 100
        tiers = "  ".join(f"{t} {pf(r[b.tier.values==t]):.2f}({int((b.tier.values==t).sum())})"
                          for t in "ABCD")
        print(f"\n{lab}: n {len(r)}  PF {pf(r):.3f}  per-year {(eq[-1]**(1/yrs)-1)*100:+.2f}%"
              f"  maxDD {dd:.2f}%  worst trade {contrib.min()*100:+.2f}%")
        print(f"   tiers  {tiers}")
        print("   years  " + "  ".join(f"{y[2:]} {pf(r[b.y.values==y]):.2f}" for y in sorted(set(b.y))))

# ---- leave-one-out: does the seat get EARNED? ---------------------------
for i, c in enumerate(args.cand):
    full = NAMES[:len(VOICES)] + [f"cand{i}"]
    allm = np.logical_or.reduce([V[n] for n in full])
    b = mc1(F[allm]); base_pf = pf(b.ret_exit.values); base_n = len(b)
    print(f"\nLEAVE-ONE-OUT with candidate = [{c}]   full book {base_n} @ {base_pf:.3f}")
    print(f"   {'dropped':<12}{'traded':>8}{'PF':>8}{'dPF':>8}{'dn':>7}   (dPF > 0 => the voice HURTS)")
    for d in full:
        m = np.logical_or.reduce([V[n] for n in full if n != d])
        bb = mc1(F[m]); p = pf(bb.ret_exit.values)
        print(f"   {d:<12}{len(bb):>8}{p:>8.3f}{p-base_pf:>+8.3f}{len(bb)-base_n:>+7}")
