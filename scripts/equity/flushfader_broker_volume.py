"""FlushFader — projected BROKER VOLUME and commissions (for the Cobra rep, 2026-08-14).

Answers: how many shares, executions and dollars would this system actually send, and
what commission does that generate at $0.0015/share (Cobra's promo rate)?

## What is being counted

One TRADE = one round trip = **2 executions** (entry + exit), same share count each
way, so shares routed = 2 x position shares.

Position size follows the production sizing rule exactly:

    size_fraction = base x tier_multiplier x sqrt(99 / volat_20m_bp)
    shares        = equity x size_fraction / entry_px

with base 1% on tier D at 99bp vol and multipliers A 2.44 / B 1.80 / C 1.14 / D 1.00.
`entry_px` is a RAW price (v44+ causal schema), so shares are real share counts.

## The cells

    universe   g60          gap_60 < 4     the production door
               complement   gap_60 >= 4    everything the door rejects
               all          both
    roster     ON           the 8-voice OR that defines the book
               OFF          no voice filter — every trip in that universe
    deepflush  d20a = (signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28
               IN / OUT of the roster, and ALONE

⚠⚠ ONLY `g60 x roster ON` IS A REAL BOOK. The wider cells have no concurrency cap and
no capital constraint — they are UPPER BOUNDS ON ORDER FLOW, not tradeable strategies.
`max_gross` reports peak simultaneous exposure as a multiple of equity; where that is
far above 1.0 the cell could not be traded as sized and its share count is
hypothetical. Send those to a broker as "the universe we monitor", never as "the size
we will do".

⚠ ECN / routing / SEC / TAF fees are NOT modelled — only the per-share commission.
FlushFader fills PASSIVELY (next-bar vwap; the spec records "passive fills = free"),
so a live book would plausibly EARN add rebates rather than pay take fees, which cuts
the opposite way from commission. The rep should price that leg.

Usage:  python scripts/equity/flushfader_broker_volume.py [--rate 0.0015]
"""
import argparse
import os
import sys

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/v45_nextopen/trips_p*.parquet")
ap.add_argument("--rate", type=float, default=0.0015, help="$/share commission")
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--base", type=float, default=0.01)
ap.add_argument("--mult", type=float, nargs=4, default=[2.44, 1.80, 1.14, 1.00])
ap.add_argument("--equities", type=float, nargs="+", default=[100_000, 250_000, 500_000])
args = ap.parse_args()

con = duckdb.connect()
RAWPX, SCHEMA = raw_px_expr(con, args.trips)

VOICES = {
    "v20":      "volat_20m*1e4 >= 140",
    "d20a":     "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28",
    "dslo":     "signal_vwap/sess_low - 1 >= 0.08",
    "ramp":     "(volat_slope_20m - volat_slope_10m)*2e4 < -12",
    "legage":   f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}",
    "dsu":      "downticks_since_uptick >= 8",
    "haltband": "secs_since_halt >= 1200 AND secs_since_halt < 4800",
    "Stier":    "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200",
}
MU = dict(zip("ABCD", args.mult))

sel = ", ".join(f"COALESCE({e}, false) AS {n}" for n, e in VOICES.items())
F = con.execute(f"""
SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, volat_20m,
       gap_60, {sel},
       CASE WHEN gap_adj_1200<15 AND ols_slope_60*6e5<=-350 THEN 'A'
            WHEN gap_adj_1200<15 THEN 'B'
            WHEN ols_slope_60*6e5<=-350 THEN 'C' ELSE 'D' END AS tier
FROM read_parquet('{args.trips}')
WHERE {RAWPX} >= 1 AND entry_px > 0 AND volat_20m > 0
ORDER BY symbol, trade_date, signal_sec""").fetchdf()
print(f"trips with a $1+ raw entry: {len(F):,}   (schema: {SCHEMA})")


def mc1(d):
    """Per-ticker-day greedy non-overlapping — the production mc=1 rule."""
    keep, last, prev = np.zeros(len(d), bool), -1, None
    key = (d.symbol + "_" + d.trade_date.astype(str)).values
    ent, ext = d.entry_sec.values, d.exit_sec.values
    for i in range(len(d)):
        if key[i] != prev:
            prev, last = key[i], -1
        if ent[i] >= last:
            keep[i] = True
            last = ext[i]
    return d[keep]


def max_gross(d, size):
    """Peak simultaneous exposure as a multiple of equity — the capacity reality check.
    Sweep entries as +size and exits as -size on a merged timeline per DAY."""
    if not len(d):
        return 0.0
    ev = []
    for day, g in d.groupby(d.trade_date.astype(str)):
        s = size[g.index.values] if isinstance(size, np.ndarray) else size
        ev.append(pd.DataFrame({"t": np.r_[g.entry_sec.values, g.exit_sec.values],
                                "d": np.r_[s, -s]}))
    e = pd.concat(ev)
    # ⚠ ties: process exits before entries at the same second, else a same-second
    # handover double-counts and inflates peak exposure.
    e = e.sort_values(["t", "d"])
    return float(np.maximum.accumulate(e.d.cumsum()).max())


def cell(mask, label):
    d = mc1(F[mask].copy()).reset_index(drop=True)
    if not len(d):
        return None
    size = np.array([args.base * MU[t] * np.sqrt(99.0 / (v * 1e4))
                     for t, v in zip(d.tier.values, d.volat_20m.values)])
    yrs = d.trade_date.astype(str).str[:4]
    n_years = yrs.nunique()
    months = n_years * 12.0
    out = {"cell": label, "trades": len(d), "trades/mo": len(d) / months,
           "max_gross": max_gross(d, size)}
    for eq in args.equities:
        sh = eq * size / d.entry_px.values            # shares per leg
        routed = 2.0 * sh.sum()                        # both legs
        notional = 2.0 * (eq * size).sum()
        out[f"sh/mo @{int(eq/1000)}k"] = routed / months
        out[f"$comm/yr @{int(eq/1000)}k"] = routed / n_years * args.rate
        out[f"notional/mo @{int(eq/1000)}k"] = notional / months
    out["_years"] = n_years
    return out


G60 = F.gap_60 < 4
ROSTER = np.logical_or.reduce([F[n].values for n in VOICES])
D20A = F["d20a"].values
ROSTER_NO_D20A = np.logical_or.reduce(
    [F[n].values for n in VOICES if n != "d20a"])

CELLS = [
    (G60 & ROSTER,               "⭐ g60 x roster ON            [THE BOOK]"),
    (G60 & ROSTER_NO_D20A,       "   g60 x roster minus d20a"),
    (G60 & D20A,                 "   g60 x deep-flush (d20a) ALONE"),
    (G60,                        "   g60 x roster OFF (all g60 trips)"),
    (~G60 & ROSTER,              "   complement x roster ON"),
    (~G60 & ROSTER_NO_D20A,      "   complement x roster minus d20a"),
    (~G60 & D20A,                "   complement x deep-flush ALONE"),
    (~G60,                       "   complement x roster OFF"),
    (ROSTER,                     "   ALL x roster ON"),
    (np.ones(len(F), bool),      "   ALL x roster OFF (every trip)"),
]
rows = [cell(m, l) for m, l in CELLS]
rows = [r for r in rows if r]

print(f"\n{'='*200}")
print(f"FLUSHFADER — PROJECTED BROKER VOLUME   |   commission ${args.rate}/share  "
      f"|   1 trade = 2 executions   |   sizing: {args.base:.0%} base on D @99bp vol")
print("=" * 200)
core = pd.DataFrame(rows)[["cell", "trades", "trades/mo", "max_gross"]]
core["trades/mo"] = core["trades/mo"].round(1)
core["max_gross"] = core["max_gross"].round(2)
print(core.to_string(index=False))
print("  max_gross = peak simultaneous exposure as a multiple of equity.")
print("  ⚠ > ~1.0 means the cell CANNOT be traded as sized — treat its volume as an")
print("    upper bound on flow, not as business the account could actually do.")

for eq in args.equities:
    k = int(eq / 1000)
    t = pd.DataFrame(rows)[["cell", f"sh/mo @{k}k", f"notional/mo @{k}k",
                            f"$comm/yr @{k}k"]].copy()
    t.columns = ["cell", "shares/mo", "notional/mo", "commission/yr"]
    t["shares/mo"] = t["shares/mo"].map(lambda v: f"{v:,.0f}")
    t["notional/mo"] = t["notional/mo"].map(lambda v: f"${v/1e6:,.2f}M")
    t["commission/yr"] = t["commission/yr"].map(lambda v: f"${v:,.0f}")
    print(f"\n--- at ${k:,}k account equity ---")
    print(t.to_string(index=False))

print(f"\n{'='*200}\n⭐ THE BOOK, PER YEAR (g60 x roster ON — the only tradeable cell)\n{'='*200}")
d = mc1(F[G60 & ROSTER].copy()).reset_index(drop=True)
size = np.array([args.base * MU[t] * np.sqrt(99.0 / (v * 1e4))
                 for t, v in zip(d.tier.values, d.volat_20m.values)])
d = d.assign(yr=d.trade_date.astype(str).str[:4], size=size)
rows = []
for y, g in d.groupby("yr"):
    r = {"year": y, "trades": len(g)}
    for eq in args.equities:
        sh = 2.0 * (eq * g["size"].values / g.entry_px.values).sum()
        r[f"shares @{int(eq/1000)}k"] = f"{sh:,.0f}"
        r[f"comm @{int(eq/1000)}k"] = f"${sh*args.rate:,.0f}"
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print(f"\n  ⚠ Cobra zeroes the monthly platform fee at 200-300k shares/month "
      f"(depending on platform).")
print(f"  ⚠ ECN/routing/SEC/TAF NOT modelled. FlushFader fills PASSIVELY, so a live")
print(f"    book plausibly EARNS add rebates rather than paying take fees.")
