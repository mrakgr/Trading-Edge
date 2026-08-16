"""S43cf — do VOLATILITY features add anything to the Snoozer systems?

⭐ USER (2026-08-16): "first 15m, 30m, 60m and the entire day's volatility. I want to
see whether ALONG WITH VOLUME that makes a difference to the trade."

The emphasis is the whole test. Both Snoozer sides already run on PARTICIPATION —
`gaps` (persistence) x `dv_over_open15` (last-hour dollars vs the opening burst),
§S43cb. Volatility is a different axis: how far price travels per unit of activity
rather than how much activity there is. So the question is not "does volatility sort
the returns" — a feature can do that and still be redundant — but "does it survive on
top of the two levers already in the spec".

Sections:
  §1  decile tables per feature, per year          — is there a gradient at all?
  §2  substitution test at MATCHED SELECTIVITY     — vs the incumbents + RANDOM
  §3  ⭐ STACKING on the incumbent 2-lever cell    — with the iso-trip controls
  §4  overlap matrix                               — is a "new" pick actually new?

⚠ CONTROLS, because PF rises mechanically whenever trades are cut:
  * a RANDOM same-n subsample is printed beside every filtered row (§2);
  * §3 tightens the INCUMBENT to the same n as the stacked cell, and also draws a
    random same-n subsample of the incumbent cell (feedback_iso_trip_control).
⚠ Never ρ — ρ 0.008 measures read PF 3.41 vs 1.28 in this very family (§S43ca).
  §4 reports pick OVERLAP, which is what actually decides redundancy.

⚠ Cuts are QUANTILES here because the ladder's levels are not comparable across
windows. A live spec needs an ABSOLUTE threshold, so the chosen cell's absolute value
is printed in §3.

Usage:
    python scripts/equity/snoozer_volat_test.py --side short
    python scripts/equity/snoozer_volat_test.py --side long
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--side", choices=["long", "short"], default="short")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--q", type=float, default=0.25, help="selectivity for §2/§4")
ap.add_argument("--bands", type=int, default=10)
args = ap.parse_args()

sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"
# incumbent spec directions (§S43cb): the sign FLIPS between the two systems
gap_op, shp_op = ("<=", ">=") if args.side == "long" else (">=", "<=")
GAP_T = 760 if args.side == "long" else 2000
SHP_Q = 0.75 if args.side == "long" else 0.25

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.*, v.volat_open15, v.volat_open30, v.volat_open60, v.volat_day,
       v.volat_lh, v.volat_dayfull, v.nsl_lh, v.nsl_open15,
       -- ⭐ the RELATIVE framing, which is what worked for dollars and bars.
       -- ALREADY RATE-FREE: a mean per-slot |r| does not scale with window length,
       -- so unlike bar_over_* these need no normalisation and 1.0 is meaningful.
       v.volat_lh/nullif(v.volat_open15, 0) AS volat_over_open15,
       v.volat_lh/nullif(v.volat_open30, 0) AS volat_over_open30,
       v.volat_lh/nullif(v.volat_open60, 0) AS volat_over_open60,
       v.volat_lh/nullif(v.volat_day, 0)    AS volat_over_day,
       3540 - s.nb60k59 AS gaps, s.dv_over_open15 AS shape,
       year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
N = con.execute("SELECT count(*) FROM S").fetchone()[0]
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM S ORDER BY 1").fetchall()]
PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")
b = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    avg(CASE WHEN r>0 THEN 1.0 ELSE 0 END)*100 w, min(r)*100 wo,
    count(*) FILTER (volat_lh IS NULL) nullv FROM S""").fetchdf()
print(f"side={args.side}   population {N:,}   ({cond})")
print(f"  baseline  PF {b.pf[0]:.3f}  mean {b.m[0]:+.2f}%  med {b.md[0]:+.2f}%  "
      f"win {b.w[0]:.0f}%  worst {b.wo[0]:.0f}%   |  volat_lh NULL on {int(b.nullv[0]):,}")

FEATS = ["volat_open15", "volat_open30", "volat_open60", "volat_day", "volat_dayfull",
         "volat_lh", "volat_over_open15", "volat_over_open30", "volat_over_open60",
         "volat_over_day"]


def stat(where, extra=""):
    d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
        avg(CASE WHEN r>0 THEN 1.0 ELSE 0 END)*100 w, min(r)*100 wo
        FROM S WHERE {where} {extra}""").fetchdf()
    return d


print(f"\n{'='*200}\n§1 QUINTILE SUMMARY — PF by feature quintile (Q1 = lowest)\n{'='*200}")
rows = []
for f in FEATS:
    qs = con.execute(f"SELECT quantile_cont({f}, [0,.2,.4,.6,.8,1.0]) FROM S "
                     f"WHERE {f} IS NOT NULL").fetchone()[0]
    row = {"feature": f}
    for i in range(5):
        hi = f"{f} <= {qs[i+1]}" if i == 4 else f"{f} < {qs[i+1]}"
        d = stat(f"{f} >= {qs[i]} AND {hi}")
        row[f"Q{i+1}"] = (f"{d.pf[0]:.2f} ({int(d.n[0]):,})" if int(d.n[0]) >= 30
                          else ".")
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))

for f in FEATS:
    qs = con.execute(f"SELECT quantile_cont({f}, {list(i/args.bands for i in range(args.bands+1))}) "
                     f"FROM S WHERE {f} IS NOT NULL").fetchone()[0]
    print(f"\n{'='*200}\n⭐ {f}   |   side={args.side}\n{'='*200}")
    rows = []
    for i in range(args.bands):
        hi = f"{f} <= {qs[i+1]}" if i == args.bands-1 else f"{f} < {qs[i+1]}"
        w = f"{f} >= {qs[i]} AND {hi}"
        d = stat(w)
        if int(d.n[0]) < 30:
            continue
        sc = 1e4 if f.startswith("volat_") and "over" not in f else 1.0
        u = "bp" if sc > 1 else ""
        row = {"band": f"D{i+1}",
               "range": f"[{qs[i]*sc:.1f}, {qs[i+1]*sc:.1f}){u}",
               "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.2f}",
               "mean%": f"{d.m[0]:+.2f}", "med%": f"{d.md[0]:+.2f}",
               "win%": f"{d.w[0]:.0f}", "worst%": f"{d.wo[0]:.0f}"}
        for y in yrs:
            e = stat(w + f" AND yr={y}")
            row[str(y)] = ("." if int(e.n[0]) < 10 else
                           ("inf" if pd.isna(e.pf[0]) else f"{e.pf[0]:.2f}"))
        rows.append(row)
    print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 10 trades that year;  'inf' = ZERO LOSERS, not missing)")

print(f"\n{'='*190}\n⭐ §2 SUBSTITUTION TEST — every filter cut to the SAME n "
      f"({args.q:.0%} of the population)\n{'='*190}")
rows = []
rows.append({"filter": "— no filter (baseline)", "n": f"{N:,}",
             "PF": f"{b.pf[0]:.3f}", "mean%": f"{b.m[0]:+.2f}",
             "med%": f"{b.md[0]:+.2f}", "worst%": f"{b.wo[0]:.0f}", "yrs<1": ""})
CANDS = [(f"gaps {gap_op} q  (INCUMBENT persistence)", "gaps",
          "<=" if gap_op == "<=" else ">="),
         (f"shape {shp_op} q (INCUMBENT dollars)", "shape", shp_op)]
for f in FEATS:
    for op in ("<=", ">="):
        CANDS.append((f"{f} {op} q", f, op))
for lbl, col, op in CANDS:
    qq = args.q if op == "<=" else 1 - args.q
    w = (f"{col} {op} (SELECT quantile_cont({col}, {qq}) FROM S WHERE {col} IS NOT NULL)"
         f" AND {col} IS NOT NULL")
    d = stat(w)
    if int(d.n[0]) < 30:
        continue
    neg = sum(1 for y in yrs
              if int(stat(w + f' AND yr={y}').n[0]) >= 10
              and pd.notna(stat(w + f' AND yr={y}').pf[0])
              and stat(w + f' AND yr={y}').pf[0] < 1.0)
    rows.append({"filter": lbl, "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.3f}",
                 "mean%": f"{d.m[0]:+.2f}", "med%": f"{d.md[0]:+.2f}",
                 "worst%": f"{d.wo[0]:.0f}", "yrs<1": neg})
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    min(r)*100 wo FROM (SELECT * FROM S ORDER BY hash(ticker || date::VARCHAR)
    LIMIT (SELECT CAST(count(*)*{args.q} AS INT) FROM S))""").fetchdf()
rows.append({"filter": "⭐ RANDOM same-n control", "n": f"{int(d.n[0]):,}",
             "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
             "med%": f"{d.md[0]:+.2f}", "worst%": f"{d.wo[0]:.0f}", "yrs<1": ""})
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*200}\n⭐⭐ §3 STACKING — does volatility survive ON TOP of the "
      f"incumbent 2-lever cell?\n{'='*200}")
INC = (f"gaps {gap_op} {GAP_T} AND shape {shp_op} "
       f"(SELECT quantile_cont(shape, {SHP_Q}) FROM S)")
inc = stat(INC)
n_inc = int(inc.n[0])
print(f"incumbent cell:  gaps {gap_op} {GAP_T}  AND  shape {shp_op} q{SHP_Q:.0%}"
      f"   ->  n = {n_inc:,}   PF {inc.pf[0]:.3f}   worst {inc.wo[0]:.0f}%\n")
rows = []


def addrow(lbl, w, absnote=""):
    d = stat(w)
    if int(d.n[0]) < 30:
        return
    row = {"variant": lbl, "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.3f}",
           "mean%": f"{d.m[0]:+.2f}", "med%": f"{d.md[0]:+.2f}",
           "win%": f"{d.w[0]:.0f}", "worst%": f"{d.wo[0]:.0f}"}
    neg = 0
    for y in yrs:
        e = stat(w + f" AND yr={y}")
        if int(e.n[0]) < 5:
            row[str(y)] = "."
        elif pd.isna(e.pf[0]):
            row[str(y)] = "inf"
        else:
            row[str(y)] = f"{e.pf[0]:.2f}"
            neg += e.pf[0] < 1.0
    row["yrs<1"] = neg
    row["abs threshold"] = absnote
    rows.append(row)


addrow("incumbent (gaps x shape)", INC)
for f in FEATS:
    for op, qq in (("<=", 0.50), (">=", 0.50)):
        t = con.execute(f"SELECT quantile_cont({f}, {qq}) FROM S WHERE {INC} "
                        f"AND {f} IS NOT NULL").fetchone()[0]
        if t is None:
            continue
        sc = 1e4 if "over" not in f else 1.0
        addrow(f"+ {f} {op} median", f"{INC} AND {f} {op} {t}",
               f"{f} {op} {t*sc:.3f}{'bp' if sc > 1 else ''}")
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'-'*200}\n⚠ ISO-TRIP CONTROLS — a half-cut cell must beat these, not "
      f"the incumbent\n{'-'*200}")
ctl = []
half = n_inc // 2
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, min(r)*100 wo
    FROM (SELECT * FROM S WHERE {INC} ORDER BY hash(ticker || date::VARCHAR)
          LIMIT {half})""").fetchdf()
ctl.append({"control": "⭐ RANDOM half of the incumbent cell", "n": f"{int(d.n[0]):,}",
            "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
            "worst%": f"{d.wo[0]:.0f}"})
for col, op in (("gaps", gap_op), ("shape", shp_op)):
    qq = 0.5 if op in ("<=",) else 0.5
    t = con.execute(f"SELECT quantile_cont({col}, {qq if op=='<=' else 1-qq}) "
                    f"FROM S WHERE {INC}").fetchone()[0]
    d = stat(f"{INC} AND {col} {op} {t}")
    ctl.append({"control": f"tighten INCUMBENT {col} {op} its own median",
                "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.3f}",
                "mean%": f"{d.m[0]:+.2f}", "worst%": f"{d.wo[0]:.0f}"})
print(pd.DataFrame(ctl).to_string(index=False))

print(f"\n{'='*190}\n§4 OVERLAP — of the {args.q:.0%} each filter picks, what "
      f"fraction is also picked by the others?\n{'='*190}")
COLS = ["gaps", "shape", "volat_lh", "volat_open30", "volat_day",
        "volat_over_open30", "volat_over_day"]


def qexpr(col):
    op = ("<=" if col in ("shape", "volat_lh", "volat_open30", "volat_day",
                          "volat_over_open30", "volat_over_day") else ">=")
    if col == "gaps":
        op = gap_op
    elif col == "shape":
        op = shp_op
    qq = args.q if op == "<=" else 1 - args.q
    return (f"{col} {op} (SELECT quantile_cont({col}, {qq}) FROM S "
            f"WHERE {col} IS NOT NULL)")


rows = []
for a in COLS:
    row = {"picks": a}
    for bcol in COLS:
        if a == bcol:
            row[bcol] = "—"
        else:
            v = con.execute(f"""SELECT 100.0*count(*) FILTER ({qexpr(a)} AND {qexpr(bcol)})
                / nullif(count(*) FILTER ({qexpr(a)}), 0) FROM S""").fetchone()[0]
            row[bcol] = "." if v is None else f"{v:.0f}%"
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print(f"  (a random pair would overlap ~{args.q:.0%}; 100% = the same trades)")
