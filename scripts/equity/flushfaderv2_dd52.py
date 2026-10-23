# V2-18: FlushFaderV2 vs the DRAWDOWN FROM THE 1-YEAR HIGH (user 2026-10-22, after AMOD 2026-10-21: bought at 1.84,
# down ~90 % from its high of the year, halted 13:58 -> reopened 1.10, -912 live).
#
#   cd research && python scripts/equity/flushfaderv2_dd52.py > logs/flushfaderv2_dd52.log 2>&1
#
# dd = 1 - signal_vwap / hi365, hi365 = max(high * n) over the ticker's daily_adjusted rows in [D - 365 d, D) of D's
# EPISODE (a > 45-day gap between rows starts a new one: build_daily_episodes_causal_view.sql, GF-46 — a recycled
# ticker does not borrow an earlier listing's high), divided by n(D): the prior high in D's raw scale
# (docs/price_adjustment.md §2). The book's prices are day D's raw prints (checked on split days), so the ratio is
# causal: every input is known at the signal. `hist_days` = the days since the first row in that window (< 300 = the
# window does not span a year: its own column).
# Return = ret_exit net of the $0.001/sh/side credit (V2-2); sized = x the book's multiplier (the Oms's sizing).
import argparse
import duckdb
import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfaderv2/flushfaderv2_reference_book.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--out", default="data/flushfaderv2/dd52.md")
ap.add_argument("--parquet", default="data/flushfaderv2/dd52_book.parquet")
args = ap.parse_args()

con = duckdb.connect(args.db, read_only=True)
D = con.execute(f"""
with da as (
    select ticker, date, high, n,
           case when lag(date) over w is null or date - lag(date) over w > 45 then 1 else 0 end as e0
    from daily_adjusted window w as (partition by ticker order by date)),
ep as (select *, sum(e0) over (partition by ticker order by date) as episode from da),
hi as (
    select ticker, date, n,
           max(high * n) over w as hin,
           date - min(date) over w as hist_days
    from ep
    window w as (partition by ticker, episode order by date
                 range between interval 365 days preceding and interval 1 day preceding))
select b.*, h.hin / h.n as hi365, h.hist_days, year(b.trade_date::DATE) as yr
from '{args.book}' b join hi h on h.ticker = b.symbol and h.date = b.trade_date::DATE
""").df()
nb = con.execute(f"select count(*) from '{args.book}'").fetchone()[0]
D["dd"] = 1 - D.signal_vwap / D.hi365
D["r"] = (D.ret_exit + 2 * args.credit / D.entry_px) * 100
D["rs"] = D.r * D.multiplier
D.to_parquet(args.parquet)

def pf(r):
    w, l = r[r > 0].sum(), -r[r < 0].sum()
    return float("inf") if l == 0 else w / l

EDGES = [-np.inf, 0.5, 0.7, 0.8, 0.9, 0.95, np.inf]
NAMES = ["< 50 %", "50-70 %", "70-80 %", "80-90 %", "90-95 %", ">= 95 %"]
full = D[D.hist_days >= 300]
young = D[D.hist_days.isna() | (D.hist_days < 300)]
full = full.assign(band=np.digitize(full.dd, EDGES[1:-1]))

out = [f"# V2-18 FlushFaderV2 vs the drawdown from the 1-year high\n",
       f"book `{args.book}`: {nb:,} trades, {len(D):,} matched to daily_adjusted; "
       f"{len(full):,} with a window spanning >= 300 days, {len(young):,} without (own row). "
       f"r = ret_exit net of the ${args.credit}/sh/side credit, %; sized = r x multiplier.\n",
       "## By band (window >= 300 days)\n",
       "| dd from 1y high | n | share | PF flat | PF sized | mean r % | mean mult | worst r % | p05 r % |",
       "|---|---|---|---|---|---|---|---|---|"]
def row(name, X):
    return (f"| {name} | {len(X):,} | {len(X)/len(D):.1%} | {pf(X.r.values):.3f} | {pf(X.rs.values):.3f} | "
            f"{X.r.mean():+.3f} | {X.multiplier.mean():.2f} | {X.r.min():+.1f} | {np.percentile(X.r, 5):+.2f} |")
for i, nm in enumerate(NAMES):
    X = full[full.band == i]
    if len(X): out.append(row(nm, X))
out.append(row("window < 300 days", young))
out.append(row("**all**", D))

out += ["\n## By year: PF flat (n) — the >= 90 % group vs the rest (window >= 300 days)\n",
        "| year | < 90 % | 90-95 % | >= 95 % | >= 90 % |", "|---|---|---|---|---|"]
for y, Y in full.groupby("yr"):
    a, b, c, d = Y[Y.dd < 0.9], Y[(Y.dd >= 0.9) & (Y.dd < 0.95)], Y[Y.dd >= 0.95], Y[Y.dd >= 0.9]
    f = lambda X: f"{pf(X.r.values):.3f} ({len(X)})" if len(X) else "-"
    out.append(f"| {y} | {f(a)} | {f(b)} | {f(c)} | {f(d)} |")

out += ["\n## Raw price inside the >= 90 % group (window >= 300 days)\n",
        "| entry px | n | PF flat | PF sized | mean r % |", "|---|---|---|---|---|"]
G = full[full.dd >= 0.9]
for lo, hi_ in [(1, 2), (2, 3), (3, 5), (5, 1e9)]:
    X = G[(G.entry_px >= lo) & (G.entry_px < hi_)]
    if len(X):
        out.append(f"| {lo}-{hi_ if hi_ < 1e9 else '+'} | {len(X):,} | {pf(X.r.values):.3f} | {pf(X.rs.values):.3f} | {X.r.mean():+.3f} |")

# the control: is dd only volatility in disguise? (the sizing model already prices volat)
out += ["\n## Inside the sizing model's volat bands (window >= 300 days)\n",
        "| volat band | PF flat < 90 % (n) | PF flat >= 90 % (n) | mean mult < 90 % | mean mult >= 90 % |",
        "|---|---|---|---|---|"]
for b, X in full.groupby("volat_band"):
    a, c = X[X.dd < 0.9], X[X.dd >= 0.9]
    out.append(f"| {b} | {pf(a.r.values):.3f} ({len(a):,}) | {pf(c.r.values):.3f} ({len(c):,}) | "
               f"{a.multiplier.mean():.2f} | {c.multiplier.mean():.2f} |")
lo20 = lambda X: f"{(X.r < -20).mean():.2%}"
out.append(f"\nlosses worse than -20 %: {lo20(full[full.dd < 0.9])} of the < 90 % trades, "
           f"{lo20(full[full.dd >= 0.9])} of the >= 90 %.")

open(args.out, "w").write("\n".join(out) + "\n")
print("\n".join(out))
