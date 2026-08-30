"""Generic FlushFader feature breakdown — buckets x YEAR columns.

House rules this encodes (see the feedback memories):
  * the FINE bucket table WITH year columns is shown before any cutoff is
    recommended — coarse buckets have lied before (S39o);
  * raw AND bottom-5%-trimmed PF side by side (the trim is a COMPARISON device:
    the top losers are what's uncertain, not the median trade);
  * infinite PF is printed `inf`, never NULL — zero-loser != no-data.

Bases (NO voice-family / deep-flush conditioning is ever applied except `book`):
  full  = every SPEC v2.7 trip, no filters at all
  spec  = full AND $1+ raw                          (tradability floor)
  g60   = full AND gap_60 < 4                       (continuous-tape slice)
  g60p  = g60 AND $1+ raw
  book  = the full roster/S-tier book               (the only conditioned base)

--mc 0  every sampler trip in the bucket (ATTRIBUTION; trips inside a ticker-day
        are NOT independent -- do not read these as tradeable)
--mc 1  per-ticker-day mc=1 REPLAYED INSIDE each bucket, i.e. "what if I traded
        only this bucket". This is the tradeable reading for a candidate GATE.

Usage:
  python scripts/equity/flushfader_breakdown.py --expr downticks_since_uptick \
      --edges 1 2 3 4 5 6 8 --base full g60 --mc 1
"""
import argparse, duckdb, numpy as np, warnings
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--expr", required=True, help="SQL expression over the trips table")
ap.add_argument("--edges", type=float, nargs="+", required=True,
                help="LEFT edges; bucket i = [e_i, e_{i+1}), plus a final [e_last, inf)")
ap.add_argument("--base", nargs="+", default=["g60p"],
                choices=["full", "spec", "g60", "g60p", "book"])
ap.add_argument("--trips", default="data/equity/flushfader/v47_spec20/trips_p*.parquet")
ap.add_argument("--esf", type=int, default=450, help="leg-age voice threshold (SPEC v2.9)")
ap.add_argument("--trim", type=float, default=0.05)
ap.add_argument("--mc", type=int, default=0, choices=[0, 1, 2],
                help="0 = every trip (attribution); 1 = mc=1 replayed INSIDE each bucket "
                     "(rows do NOT partition -- each bucket is its own book); "
                     "2 = mc=1 on the base ONCE, then bucket the survivors (rows DO partition)")
args = ap.parse_args()

BOOK_VOICES = """(
    COALESCE(volat_20m*1e4 >= 140, false)
    OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
    OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false)
    OR COALESCE((volat_slope_10m - volat_slope_20m)*2e4 > 12, false)
    OR COALESCE(volat_slope_5m*2e4 <= -24, false)
    OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= {ESF}, false)
    OR COALESCE(downticks_since_uptick >= 8, false)
    OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
    OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))"""

WHERE = {
    "full": "1=1",
    "spec": "{RAWPX} >= 1",
    "g60":  "gap_60 < 4",
    "g60p": "{RAWPX} >= 1 AND gap_60 < 4 AND volat_20m >= 0.004 AND signal_sec <= 54000",
    "book": "{RAWPX} >= 1 AND gap_60 < 4 AND volat_20m >= 0.004 AND signal_sec <= 54000 AND "
            + BOOK_VOICES.replace("{ESF}", str(args.esf)),
}
con = duckdb.connect()
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
WHERE = {k: v.replace("{RAWPX}", RAWPX) for k, v in WHERE.items()}


def mc1(f):
    """Greedy non-overlapping selection inside each ticker-day."""
    keep, last, prev = np.zeros(len(f), bool), -1, None
    key = (f.symbol.values + "_" + f.trade_date.astype(str).values)
    ent, ext = f.entry_sec.values, f.exit_sec.values
    for i in range(len(f)):
        if key[i] != prev:
            prev, last = key[i], -1
        if ent[i] >= last:
            keep[i] = True
            last = ext[i]
    return f[keep]


def load(base):
    f = con.execute(f"""
    SELECT symbol, trade_date, entry_sec, exit_sec, signal_sec, ret_exit,
           ({args.expr}) AS x
    FROM read_parquet('{args.trips}') WHERE {WHERE[base]}
    ORDER BY symbol, trade_date, signal_sec""").fetchdf()
    return mc1(f) if base == "book" else f


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else g / l


def pfs(v):
    return "   inf" if np.isinf(v) else f"{v:6.3f}"


E = list(args.edges)
LABELS = [f"[{E[i]:g},{E[i+1]:g})" for i in range(len(E) - 1)] + [f"[{E[-1]:g},inf)"]

def row(lab, g):
    """One table row from a (already mc-resolved) frame."""
    rr = g.ret_exit.values
    if len(rr) == 0:
        return f"{lab:<12}{0:>6}"
    tp = pf(rr[rr >= np.quantile(rr, args.trim)]) if len(rr) >= 20 else float("nan")
    y2 = g.trade_date.astype(str).str[:4].values
    cells = ""
    for y in YEARS:
        q = rr[y2 == y]
        cells += ("      . " if len(q) < 5 else f"{pfs(pf(q))}"[-6:].rjust(8))
    return (f"{lab:<12}{len(rr):>6}{pfs(pf(rr))}{tp:>8.2f}{(rr > 0).mean()*100:>7.1f}"
            f"{rr.mean()*100:>+7.2f}{rr.min()*100:>+8.1f}  " + cells)


NOTE = {0: "every trip -- ATTRIBUTION, trips in a ticker-day are not independent",
        1: "mc=1 replayed INSIDE each bucket -- rows do NOT partition (each is its own book)",
        2: "mc=1 on the base once, then bucketed -- rows DO partition the traded book"}

for base in args.base:
    P = load(base)
    if args.mc == 2 and base != "book":
        P = mc1(P)
    x = P.x.values.astype(float)
    YEARS = sorted(set(P.trade_date.astype(str).str[:4]))
    resolve = mc1 if (args.mc == 1 and base != "book") else (lambda g: g)
    A = resolve(P)
    print(f"\n{'='*(54 + 8*len(YEARS))}")
    print(f"{args.expr}   base = {base}   mc = {args.mc}   "
          f"trips {len(P):,} -> rows {len(A):,}"
          f"   (below [{E[0]:g}): {(x < E[0]).sum():,})")
    print(f"  [{NOTE[args.mc]}]")
    print(f"{'='*(54 + 8*len(YEARS))}")
    print(f"{'bucket':<12}{'n':>6}{'PF':>7}{'trimPF':>8}{'win%':>7}{'avg%':>7}{'worst%':>8}  "
          + "".join(f"{y[2:]+' PF':>8}" for y in YEARS))
    for i, lab in enumerate(LABELS):
        hi = E[i + 1] if i + 1 < len(E) else np.inf
        print(row(lab, resolve(P[(x >= E[i]) & (x < hi)])))
    print(row("ALL", A))
    yv = A.trade_date.astype(str).str[:4].values
    print(f"  (year cells with n < 5 shown as '.';  per-year n: "
          + " ".join(f"{y[2:]}={int((yv==y).sum())}" for y in YEARS) + ")")
