"""Generic FlushFader feature breakdown — buckets x YEAR columns.

House rules this encodes (see the feedback memories):
  * the FINE bucket table WITH year columns is shown before any cutoff is
    recommended — coarse buckets have lied before (S39o);
  * raw AND bottom-5%-trimmed PF side by side (the trim is a COMPARISON device:
    the top losers are what's uncertain, not the median trade);
  * infinite PF is printed `inf`, never NULL — zero-loser != no-data.

Bases:
  spec  = every SPEC v2.7 trip, $1+ raw            (breadth)
  g60   = spec AND gap_60 < 4                       (what the book actually trades)
  book  = the full roster/S-tier book, per-ticker-day mc=1

Usage:
  python scripts/equity/flushfader_breakdown.py --expr downticks_since_uptick \
      --edges 0 1 2 3 5 8 12 20 --base g60
  python scripts/equity/flushfader_breakdown.py --expr "dn_60 - up_60" \
      --edges -20 -10 -5 0 5 10 20 --base g60 spec
"""
import argparse, duckdb, numpy as np, warnings
warnings.filterwarnings("ignore")

ap = argparse.ArgumentParser()
ap.add_argument("--expr", required=True, help="SQL expression over the trips table")
ap.add_argument("--edges", type=float, nargs="+", required=True,
                help="LEFT edges; bucket i = [e_i, e_{i+1}), plus a final [e_last, inf)")
ap.add_argument("--base", nargs="+", default=["g60"], choices=["spec", "g60", "book"])
ap.add_argument("--trips", default="data/equity/flushfader/v42_ticks/trips_p*.parquet")
ap.add_argument("--esf", type=int, default=450, help="leg-age voice threshold (SPEC v2.9)")
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

BOOK_VOICES = """(
    COALESCE(volat_20m*1e4 >= 140, false)
    OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
    OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false)
    OR COALESCE((volat_slope_20m - volat_slope_10m)*2e4 < -12, false)
    OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= {ESF}, false)
    OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
    OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))"""

WHERE = {
    "spec": "entry_px/adj_ratio >= 1",
    "g60":  "entry_px/adj_ratio >= 1 AND gap_60 < 4",
    "book": "entry_px/adj_ratio >= 1 AND gap_60 < 4 AND "
            + BOOK_VOICES.replace("{ESF}", str(args.esf)),
}
con = duckdb.connect()


def load(base):
    f = con.execute(f"""
    SELECT symbol, trade_date, entry_sec, exit_sec, signal_sec, ret_exit,
           ({args.expr}) AS x
    FROM read_parquet('{args.trips}') WHERE {WHERE[base]}
    ORDER BY symbol, trade_date, signal_sec""").fetchdf()
    if base != "book":
        return f
    keep, last, prev = np.zeros(len(f), bool), -1, None
    key = (f.symbol + "_" + f.trade_date.astype(str)).values
    ent, ext = f.entry_sec.values, f.exit_sec.values
    for i in range(len(f)):
        if key[i] != prev:
            prev, last = key[i], -1
        if ent[i] >= last:
            keep[i] = True
            last = ext[i]
    return f[keep]


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else g / l


def pfs(v):
    return "   inf" if np.isinf(v) else f"{v:6.3f}"


E = list(args.edges)
LABELS = [f"[{E[i]:g},{E[i+1]:g})" for i in range(len(E) - 1)] + [f"[{E[-1]:g},inf)"]

for base in args.base:
    P = load(base)
    x = P.x.values.astype(float)
    r = P.ret_exit.values
    yr = P.trade_date.astype(str).str[:4].values
    years = sorted(set(yr))
    print(f"\n{'='*(46 + 8*len(years))}")
    print(f"{args.expr}   base = {base}   n = {len(P):,}"
          f"   (below [{E[0]:g}): {(x < E[0]).sum():,} trips)")
    print(f"{'='*(46 + 8*len(years))}")
    print(f"{'bucket':<12}{'n':>6}{'PF':>7}{'trimPF':>8}{'win%':>7}{'avg%':>7}  "
          + "".join(f"{y[2:]+' PF':>8}" for y in years))
    for i, lab in enumerate(LABELS):
        lo = E[i]
        hi = E[i + 1] if i + 1 < len(E) else np.inf
        m = (x >= lo) & (x < hi)
        if m.sum() == 0:
            print(f"{lab:<12}{0:>6}")
            continue
        rr = r[m]
        tp = pf(rr[rr >= np.quantile(rr, args.trim)]) if len(rr) >= 20 else float("nan")
        cells = ""
        for y in years:
            s = m & (yr == y)
            cells += ("      . " if s.sum() < 5 else f"{pfs(pf(r[s]))}"[-6:].rjust(8))
        print(f"{lab:<12}{m.sum():>6}{pfs(pf(rr))}{tp:>8.2f}{(rr > 0).mean()*100:>7.1f}"
              f"{rr.mean()*100:>+7.2f}  " + cells)
    tp = pf(r[r >= np.quantile(r, args.trim)])
    print(f"{'ALL':<12}{len(r):>6}{pfs(pf(r))}{tp:>8.2f}{(r > 0).mean()*100:>7.1f}"
          f"{r.mean()*100:>+7.2f}  "
          + "".join(f"{pfs(pf(r[yr == y]))}"[-6:].rjust(8) for y in years))
    print(f"  (year cells with n < 5 shown as '.';  per-year n: "
          + " ".join(f"{y[2:]}={int((yr==y).sum())}" for y in years) + ")")
