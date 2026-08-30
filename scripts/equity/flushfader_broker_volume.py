"""FlushFader — order flow and commissions by SEGMENT, for the broker (2026-08-14).

Four DISJOINT segments (user 2026-08-14), so the four rows sum to the whole candidate
set and nothing is double-counted:

    A++   gap_60 < 4   AND      voice roster     the current production book
    A+    gap_60 < 4   AND NOT  voice roster
    B++   gap_60 >= 4  AND      voice roster
    B+    gap_60 >= 4  AND NOT  voice roster

`voice roster` = the 8-signal OR that currently defines the book. `gap_60 < 4` = the
liquidity door (fewer than 4 of the last 60 seconds without a print at the signal).

One trade = 2 executions (entry + exit), same share count each way.
Account equity $100,000. Commission $0.0015/share.

## ⚠ SIZING — the base is a live constraint, not a free parameter

    size_fraction = BASE x tier_multiplier x sqrt(99 / volat_20m_bp)
    tier multipliers A 2.44 / B 1.80 / C 1.14 / D 1.00

The user's target is BASE = 10%. That is checked, not assumed: `--report-capacity`
prints the distribution of simultaneous exposure so the base can be set against real
day-trading buying power (typically 4x intraday) rather than against a peak that may
occur twice in seven years.

Usage:
    python scripts/equity/flushfader_broker_volume.py --base 0.10
    python scripts/equity/flushfader_broker_volume.py --base 0.10 --report-capacity
"""
import argparse
import os
import sys

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr

pd.set_option("display.width", 200)
pd.set_option("display.max_columns", 40)

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/v47_spec20/trips_p*.parquet")
ap.add_argument("--rate", type=float, default=0.0015)
ap.add_argument("--equity", type=float, default=100_000)
ap.add_argument("--base", type=float, default=0.10)
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--mult", type=float, nargs=4, default=[2.44, 1.80, 1.14, 1.00])
ap.add_argument("--report-capacity", action="store_true")
args = ap.parse_args()

con = duckdb.connect()
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
VOICES = {
    "v20":      "volat_20m*1e4 >= 140",
    "d20a":     "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28",
    "dslo":     "signal_vwap/sess_low - 1 >= 0.08",
    "vexp":     "(volat_slope_10m - volat_slope_20m)*2e4 > 12",
    "vcrush":   "volat_slope_5m*2e4 <= -24",
    "legage":   f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}",
    "dsu":      "downticks_since_uptick >= 8",
    "haltband": "secs_since_halt >= 1200 AND secs_since_halt < 4800",
    "Stier":    "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200",
}
MU = dict(zip("ABCD", args.mult))
sel = ", ".join(f"COALESCE({e}, false) AS {n}" for n, e in VOICES.items())
F = con.execute(f"""
SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, volat_20m, gap_60,
       {sel},
       CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
            WHEN gap_adj_1200<15 THEN 'B'
            WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier
FROM read_parquet('{args.trips}')
WHERE {RAWPX} >= 1 AND entry_px > 0 AND volat_20m >= 0.004 AND signal_sec <= 54000
ORDER BY symbol, trade_date, signal_sec""").fetchdf()
VOICE = np.logical_or.reduce([F[n].values for n in VOICES])
G60 = (F.gap_60 < 4).values
F = F.assign(seg=np.select(
    [G60 & VOICE, G60 & ~VOICE, ~G60 & VOICE, ~G60 & ~VOICE],
    ["A++", "A+", "B++", "B+"]))


def mc1(d):
    """One position per ticker-day at a time (the production concurrency rule)."""
    d = d.sort_values(["symbol", "trade_date", "signal_sec"]).reset_index(drop=True)
    keep, last, prev = np.zeros(len(d), bool), -1, None
    key = (d.symbol + "_" + d.trade_date.astype(str)).values
    ent, ext = d.entry_sec.values, d.exit_sec.values
    for i in range(len(d)):
        if key[i] != prev:
            prev, last = key[i], -1
        if ent[i] >= last:
            keep[i] = True
            last = ext[i]
    return d[keep].reset_index(drop=True)


def sizes(d):
    """⭐ TIER MULTIPLIERS APPLY TO A++ ONLY (user 2026-08-14).

    The ladder A 2.44 / B 1.80 / C 1.14 / D 1.00 was fitted on the A++ book, and
    re-deriving it per segment shows it does NOT transfer — same method, scaled to
    D = 1.00:

        A++   A 2.52 (147)  B 1.83 (402)  C 1.14 (225)  D 1.00 (551)
        A+    A 1.19  (29)  B 1.18 (172)  C 1.46  (89)  D 1.00 (427)
        B++   A   .   (0)   B   .    (4)  C 1.28 (270)  D 1.00 (1053)
        B+    A   .   (0)   B   .    (3)  C 0.77 (281)  D 1.00 (2352)

    A++ reproduces the incumbent ladder. Outside it the ordering flattens (A+: C
    above both A and B) or INVERTS (B+: C at 0.77 underperforms D). Tier A requires
    `gap_adj_1200 < 15`, a tight-tape condition highly correlated with the gap_60
    door, so B++/B+ are ~90% D-tier and the question is nearly moot there anyway.

    Everything outside A++ therefore sizes FLAT at the base, still
    volatility-normalised."""
    mult = np.where(d.seg.values == "A++",
                    np.array([MU[t] for t in d.tier.values]), 1.0)
    return args.base * mult * np.sqrt(99.0 / (d.volat_20m.values * 1e4))


def exposure_curve(d, size):
    """Simultaneous exposure through time, as a multiple of equity.
    ⚠ Exits sort before entries at equal timestamps — otherwise a same-second
    handover double-counts and overstates the peak."""
    ev = pd.DataFrame({
        "day": np.r_[d.trade_date.astype(str).values, d.trade_date.astype(str).values],
        "t": np.r_[d.entry_sec.values, d.exit_sec.values],
        "d": np.r_[size, -size]})
    ev = ev.sort_values(["day", "t", "d"])
    return ev.groupby("day")["d"].cumsum().values


def row(d, label):
    if not len(d):
        return None
    size = sizes(d)
    sh = args.equity * size / d.entry_px.values           # shares per leg
    months = d.trade_date.astype(str).str[:4].nunique() * 12.0
    routed = 2.0 * sh.sum()
    return {"segment": label, "trades": len(d),
            "trades/mo": round(len(d) / months, 1),
            "avg shares/order": f"{sh.mean():,.0f}",
            "shares/mo": f"{routed/months:,.0f}",
            "$/order (avg)": f"${(args.equity*size).mean():,.0f}",
            "commission/yr": f"${routed/(months/12)*args.rate:,.0f}"}


print(f"FlushFader order flow   |   ${args.equity:,.0f} account   |   "
      f"${args.rate}/share   |   base {args.base:.0%}   |   1 trade = 2 executions")
print(f"trips considered: {len(F):,}  ({SCHEMA})\n")

print("=" * 130)
print("EACH SEGMENT TRADED ON ITS OWN")
print("=" * 130)
rows = [row(mc1(F[F.seg == s]), s) for s in ["A++", "A+", "B++", "B+"]]
print(pd.DataFrame([r for r in rows if r]).to_string(index=False))

print(f"\n{'='*130}\nSEGMENTS COMBINED (one position per stock at a time across the whole book)\n{'='*130}")
combos = [("A++", ["A++"]), ("A++ and A+", ["A++", "A+"]),
          ("A++ and B++", ["A++", "B++"]),
          ("all four", ["A++", "A+", "B++", "B+"])]
rows = []
for lbl, segs in combos:
    d = mc1(F[F.seg.isin(segs)])
    r = row(d, lbl)
    if r:
        rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print("  ⚠ combining is NOT additive — a trade in one segment can crowd out a later")
print("    trade in another on the same stock and day.")

if args.report_capacity:
    print(f"\n{'='*130}\n⚠ CAPACITY CHECK — is base {args.base:.0%} actually reachable?\n{'='*130}")
    print("simultaneous exposure as a multiple of account equity, measured across the")
    print("whole history. Intraday day-trading buying power is typically 4x.\n")
    out = []
    for lbl, segs in combos:
        d = mc1(F[F.seg.isin(segs)])
        c = exposure_curve(d, sizes(d))
        feas = args.base * 4.0 / max(c.max(), 1e-9)
        out.append({"book": lbl, "median": f"{np.median(c):.2f}x",
                    "p95": f"{np.percentile(c, 95):.2f}x",
                    "p99": f"{np.percentile(c, 99):.2f}x",
                    "PEAK": f"{c.max():.2f}x",
                    "base for 4x peak": f"{feas:.1%}"})
    print(pd.DataFrame(out).to_string(index=False))
    print("\n  median/p95 are over ACTIVE moments only (times with a position on).")
    print("  'base for 4x peak' = the base whose worst-ever pile-up just fits 4x margin.")
