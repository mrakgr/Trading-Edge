"""S43az -- shared disaster atom, solved so tier A's exact Kelly = target.

Model per tier:  g(f) = (1-w) * mean_i log(1+f r_i)  +  w * log(1+f*r_dis)
   r_i    = tier's empirical returns (per-ticker-day mc=1 book, vol-normalised)
   r_dis  = -100% (configurable)
   w      = disaster probability, SHARED across tiers

Usage:
  python lnw.py                     # solve w so f*_A = 0.25, show all tiers
  python lnw.py --target-f 0.33     # different anchor for tier A
  python lnw.py --tier D            # anchor on a different tier
  python lnw.py --w 0.001           # skip solving; just show f* at this w
  python lnw.py --r-dis -0.5        # disaster is -50% instead of -100%
"""
import argparse, duckdb, numpy as np, warnings
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr
from scipy import optimize
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--target-f", type=float, default=0.25)
ap.add_argument("--tier", default="A")
ap.add_argument("--w", type=float, default=None)
ap.add_argument("--r-dis", type=float, default=-1.00)
ap.add_argument("--no-book", action="store_true",
                help="use the full v31_reference instead of the trading book")
args = ap.parse_args()

TRIPS = "/home/mrakgr/Trading-Edge/data/equity/flushfader/v43_legtick/trips_p*.parquet"
# the trading-book filter ($1+ raw x g60 x vote/S-tier). NB S-tier is
# halts_today >= 1 (with the engine cascade gate that means ht in {1,2}).
# ⭐ S43bg (user 2026-08-08): the leg-age voice is now `secs_since_first_low`,
# NOT `bars_since_first_low <= 390`. The bar count is era-relative — it meant
# 9.1 min in 2020 and 7.6 min in 2026, a 16% silent tightening as the tape
# densified (S43bf). Requires trips recorded by the S43bf engine or later
# (v41_secs and after).
# ⭐ S43bj (user 2026-08-11): threshold TIGHTENED 516 -> 450. 516 was
# selectivity-matched on the FULL $1+ trip set, but the book only ever trades
# the g60 universe — parity THERE is ~406s. 450 sits inside [406, 490] and
# drops CADL 2024-12-11 (secs 480), tier C's -21.7% outlier.
BOOK_WHERE = """
  gap_60 < 4 AND {RAWPX} >= 1 AND (
    COALESCE(volat_20m*1e4 >= 140, false)
    OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
    OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false)
    OR COALESCE((volat_slope_20m - volat_slope_10m)*2e4 < -12, false)
    OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= 450, false)
    OR COALESCE(downticks_since_uptick >= 8, false)
    OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
    OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))
"""
con = duckdb.connect()
RAWPX, SCHEMA = raw_px_expr(con, TRIPS)
BOOK_WHERE = BOOK_WHERE.replace("{RAWPX}", RAWPX)
f_ = con.execute(f"""
SELECT symbol, trade_date, entry_sec, exit_sec,
       ret_exit*sqrt(99.0/(volat_20m*1e4)) AS rn,
       CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
            WHEN gap_adj_1200<15 THEN 'B' WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier
FROM read_parquet('{TRIPS}')
{'' if args.no_book else 'WHERE ' + BOOK_WHERE}
ORDER BY symbol, trade_date, signal_sec
""").fetchdf()
keep, last, prev = np.zeros(len(f_), bool), -1, None
sym = (f_.symbol + "_" + f_.trade_date.astype(str)).values
ent, ext = f_.entry_sec.values, f_.exit_sec.values
for i in range(len(f_)):
    if sym[i] != prev: prev, last = sym[i], -1
    if ent[i] >= last: keep[i] = True; last = ext[i]
P = f_[keep]
R = {t: P.loc[P.tier == t, 'rn'].values for t in "ABCD"}

RD = args.r_dis
def g(f, r, w):
    return (1-w)*float(np.mean(np.log(np.maximum(1+f*r, 1e-300)))) + w*np.log(max(1+f*RD, 1e-300))

def fstar(r, w):
    cap = 0.999999/max(-min(r.min(), RD), 1e-9)
    o = optimize.minimize_scalar(lambda f: -g(f, r, w), bounds=(1e-6, cap), method='bounded')
    return o.x

if args.w is not None:
    w = args.w
elif RD >= 0:
    w = 0.0
    print(f"⚠ r_dis = {RD} >= 0: the disaster atom is INERT (log(1+f*r_dis) can't punish f),")
    print(f"  w has no effect and no target can be solved. Showing PURE EMPIRICAL KELLY (w=0).")
    print(f"  NB: these f* pin against 1/|worst observed loss| -- they measure sample size.")
else:
    t = args.tier
    lo, hi = 1e-9, 0.5          # f*(w) is decreasing in w -> bisect
    if fstar(R[t], lo) < args.target_f:
        print(f"⚠ even w≈0 gives f*_{t} = {fstar(R[t], lo):.3f} < target {args.target_f} -- unreachable")
        raise SystemExit
    for _ in range(80):
        mid = np.sqrt(lo*hi)     # geometric bisection, w spans orders of magnitude
        if fstar(R[t], mid) > args.target_f: lo = mid
        else: hi = mid
    w = np.sqrt(lo*hi)
    got = fstar(R[t], w)
    if abs(got - args.target_f) > 0.01*max(args.target_f, 0.01):
        print(f"⚠ TARGET NOT REACHED: best w = {w:.6f} gives f*_{t} = {got:.3f}, wanted {args.target_f}")
    else:
        print(f"solved: w_disaster = {w:.6f}  (1 in {1/w:,.0f} trades)  so that f*_{t} = {args.target_f}")

wtxt = f"{w:.6f} (1 in {1/w:,.0f})" if w > 0 else "0 (no disaster atom)"
base = "FULL v31_reference" if args.no_book else "TRADING BOOK ($1+ x g60 x vote/S-tier)"
print(f"disaster: r = {RD*100:.0f}%  w = {wtxt}   base = {base}, per-tkd mc=1, {len(P):,} trips\n")
print(f"{'tier':<6}{'n':>7}{'mean%':>8}{'worst%':>9}{'f*':>9}{'rel D':>8}{'rel A':>8}{'g(f*)%':>9}")
o = {t: fstar(R[t], w) for t in "ABCD"}
for t in "ABCD":
    r = R[t]
    print(f"{t:<6}{len(r):>7}{r.mean()*100:>8.2f}{r.min()*100:>9.1f}{o[t]:>9.3f}"
          f"{o[t]/o['D']:>8.2f}{o[t]/o['A']:>8.2f}{g(o[t], r, w)*100:>9.4f}")

print(f"\nsizing multipliers (f* scaled so D = 1):  "
      + "  ".join(f"{t} {o[t]/o['D']:.2f}" for t in "ABCD"))

# break-even: g'(0) = (1-w) E[r] + w r_dis = 0  ->  w = E[r] / (E[r] - r_dis)
print(f"\nbreak-even w per tier (above this the tier's f* = 0):")
for t in "ABCD":
    mu = R[t].mean(); wbe = mu/(mu - RD)
    print(f"  {t}: w = {wbe:.6f}  (1 in {1/wbe:,.0f})")

print(f"\n{'='*88}\nSWEEP over shared w  (f* per tier; 0.000 = tier not worth betting)\n{'='*88}")
print(f"{'w':>10}{'1 in':>10}" + "".join(f"{('f* '+t):>10}" for t in "ABCD")
      + f"{'A/D':>8}{'B/D':>8}{'C/D':>8}")
for wv in (0.0001, 0.0005, 0.001, 0.002, 0.003, 0.005, 0.0075, 0.010, 0.0125, 0.015, 0.020):
    s = {t: fstar(R[t], wv) for t in "ABCD"}
    dead = s['D'] < 1e-3
    print(f"{wv:>10.4f}{1/wv:>10,.0f}" + "".join(f"{s[t]:>10.3f}" for t in "ABCD")
          + ("".join(f"{s[t]/s['D']:>8.2f}" for t in "ABC") if not dead else "    (D dead -- ratios n/a)"))
