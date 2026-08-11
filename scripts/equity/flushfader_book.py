"""FlushFader trading-book stats at a parameterised leg-age threshold.

The book = $1+ RAW x g60 x (>=1 of 6 roster voices OR S-tier A), replayed at
PER-TICKER-DAY mc=1 (S43ay: the GLOBAL mc=1 book is a luck artifact).

Prints, for each `--esf` value given:
  * headline n / PF / win% / avg%
  * per-tier n / PF / win% / avg%
  * per-year n / PF / avg%
  * bottom-5%-TRIMMED PF-1 multipliers (S43bi: the adopted sizing metric --
    NOT empirical Kelly, which pins at 1/|worst kept loss|; see S43bh)
  * equity sim at 1% D-base:  size = BASE x tier_mult x sqrt(99 / volat_bp)

Usage:
  python scripts/equity/flushfader_book.py                  # 516 vs 450
  python scripts/equity/flushfader_book.py --esf 390 450 516
  python scripts/equity/flushfader_book.py --esf 450 --mult 2.44 1.80 1.14 1.00
"""
import argparse, duckdb, numpy as np, warnings
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--esf", type=int, nargs="+", default=[450])   # SPEC v2.9 (S43bj)
ap.add_argument("--trips", default="data/equity/flushfader/v41_secs/trips_p*.parquet")
ap.add_argument("--mult", type=float, nargs=4, default=[2.44, 1.80, 1.14, 1.00],
                help="tier multipliers A B C D used by the equity sim (S43bi set)")
ap.add_argument("--base", type=float, default=0.01, help="D-tier size at reference vol")
ap.add_argument("--trim", type=float, default=0.05, help="bottom-quantile trim for PF-1")
ap.add_argument("--no-g60", action="store_true")
args = ap.parse_args()

# The trading-book filter. NB S-tier is halts_today >= 1 (the engine cascade
# gate means ht in {1,2}). {ESF} is substituted per run.
BOOK_WHERE = """
  {G60} entry_px/adj_ratio >= 1 AND (
    COALESCE(volat_20m*1e4 >= 140, false)
    OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
    OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false)
    OR COALESCE((volat_slope_20m - volat_slope_10m)*2e4 < -12, false)
    OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= {ESF}, false)
    OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
    OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))
"""
con = duckdb.connect()


def book(esf):
    where = BOOK_WHERE.replace("{ESF}", str(esf)).replace(
        "{G60}", "" if args.no_g60 else "gap_60 < 4 AND")
    f = con.execute(f"""
    SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit, volat_20m,
           ret_exit*sqrt(99.0/(volat_20m*1e4)) AS rn,
           CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
                WHEN gap_adj_1200<15 THEN 'B'
                WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier
    FROM read_parquet('{args.trips}') WHERE {where}
    ORDER BY symbol, trade_date, signal_sec""").fetchdf()
    # per-ticker-day mc=1: greedy non-overlapping inside each ticker-day
    keep, last, prev = np.zeros(len(f), bool), -1, None
    key = (f.symbol + "_" + f.trade_date.astype(str)).values
    ent, ext = f.entry_sec.values, f.exit_sec.values
    for i in range(len(f)):
        if key[i] != prev:
            prev, last = key[i], -1
        if ent[i] >= last:
            keep[i] = True
            last = ext[i]
    return f[keep].sort_values(["trade_date", "entry_sec"]).reset_index(drop=True)


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else g / l


def stat(r):
    return len(r), pf(r), (r > 0).mean() * 100, r.mean() * 100


def trimmed_pf1(r, q):
    """PF-1 on the distribution with its bottom q-quantile removed."""
    if len(r) < 20:
        return float("nan")
    kept = r[r >= np.quantile(r, q)]
    return pf(kept) - 1


for esf in args.esf:
    P = book(esf)
    R = P.ret_exit.values
    print(f"\n{'='*78}\nsecs_since_first_low <= {esf}   (per-ticker-day mc=1, "
          f"{'no g60' if args.no_g60 else 'g60'}, $1+ raw)\n{'='*78}")
    n, p, w, a = stat(R)
    print(f"BOOK   n {n:>5}   PF {p:6.3f}   win {w:5.1f}%   avg {a:+6.2f}%   "
          f"worst {R.min()*100:+6.1f}%   trimPF {pf(R[R >= np.quantile(R, args.trim)]):6.3f}")

    print(f"\n{'tier':<6}{'n':>6}{'PF':>8}{'win%':>7}{'avg%':>8}{'worst%':>8}"
          f"{'PF-1 trim':>11}{'mult (D=1)':>12}")
    m = {}
    for t in "ABCD":
        r = P.loc[P.tier == t, "rn"].values          # rn = vol-normalised basis
        m[t] = trimmed_pf1(r, args.trim)
    for t in "ABCD":
        rr = P.loc[P.tier == t, "ret_exit"].values
        n, p, w, a = stat(rr)
        print(f"{t:<6}{n:>6}{p:>8.3f}{w:>7.1f}{a:>+8.2f}{rr.min()*100:>+8.1f}"
              f"{m[t]:>11.3f}{m[t]/m['D']:>12.2f}")
    print("  adopted-metric multipliers (PF-1, rn, bottom-"
          f"{args.trim:.0%} trimmed): "
          + "  ".join(f"{t} {m[t]/m['D']:.2f}" for t in "ABCD"))

    yr = P.trade_date.astype(str).str[:4]
    print(f"\n{'year':<6}{'n':>6}{'PF':>8}{'avg%':>8}{'acct%':>8}{'tA':>5}{'tB':>5}{'tC':>5}{'tD':>5}")
    MU = dict(zip("ABCD", args.mult))
    size = np.array([args.base * MU[t] * np.sqrt(99.0 / (v * 1e4))
                     for t, v in zip(P.tier.values, P.volat_20m.values)])
    contrib = size * R
    eq, e = np.empty(len(P)), 1.0
    for i in range(len(P)):
        e *= 1 + contrib[i]
        eq[i] = e
    for y in sorted(yr.unique()):
        s = yr.values == y
        acct = (np.prod(1 + contrib[s]) - 1) * 100
        tc = P.tier[s].value_counts()
        print(f"{y:<6}{s.sum():>6}{pf(R[s]):>8.3f}{R[s].mean()*100:>+8.2f}{acct:>+8.2f}"
              + "".join(f"{tc.get(t, 0):>5}" for t in "ABCD"))
    yrs = len(np.unique(yr))
    dd = (eq / np.maximum.accumulate(eq) - 1).min() * 100
    print(f"\nequity sim  mult {dict(zip('ABCD', args.mult))}  base {args.base:.1%} on D @99bp vol")
    print(f"  total {(eq[-1]-1)*100:+.1f}%   per-year {(eq[-1]**(1/yrs)-1)*100:+.2f}%"
          f"   maxDD {dd:.2f}%   worst trade {contrib.min()*100:+.2f}%"
          f"   max size {size.max()*100:.2f}%")
