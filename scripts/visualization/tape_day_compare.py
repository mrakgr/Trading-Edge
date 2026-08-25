"""Side-by-side 1s-tape chart for one ticker across several days.

⭐ Reads `data/intraday_1s_slim/` — the SAME corpus every FlushFader / Snoozer /
LongHiker number is computed on. That is the point: a second with no trade is a
second that is genuinely absent from the tape, so halts and thin patches show up
as real voids rather than being interpolated away by a bar builder.

Three rows, one column per day:

  1. vwap            line + markers; log y when the day's range exceeds `--log-ratio`
  2. volume          per-second bars
  3. present seconds per minute (0-60)  — ⭐ THE GAP STRIP

Row 3 is the one that matters for universe/spec work: `n_bars_1s` (the
`mr_candidate_1s_v2` gate, >= 200 of the 900 seconds in [09:30, 09:45)) and the
Snoozer `gaps` feature (3600 - nb60k59 over (15:00, 15:59]) are both just areas
under this strip. Reference lines are drawn at both windows.

Usage:
    python scripts/visualization/tape_day_compare.py -t ZJYL -d 2023-12-15,2023-12-18
    python scripts/visualization/tape_day_compare.py -t GME -d 2021-01-26 --out /tmp/gme.html
"""
import argparse
import os

import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots

ET_OPEN, ET_0945, ET_1500, ET_1559, ET_CLOSE = 34200, 35100, 54000, 57540, 57600

ap = argparse.ArgumentParser()
ap.add_argument("-t", "--ticker", required=True)
ap.add_argument("-d", "--dates", required=True, help="comma-separated YYYY-MM-DD")
ap.add_argument("--bars1s", default="data/intraday_1s_slim")
ap.add_argument("--out", default=None)
ap.add_argument("--log-ratio", type=float, default=4.0,
                help="use a log price axis when hi/lo exceeds this (default 4)")
ap.add_argument("--rth-only", action="store_true", help="clip to [09:30, 16:00]")
args = ap.parse_args()

dates = [d.strip() for d in args.dates.split(",") if d.strip()]
out = args.out or f"/tmp/{args.ticker}_{'_'.join(dates)}.html"

con = duckdb.connect()
days = {}
for d in dates:
    path = os.path.join(args.bars1s, f"{d}.parquet")
    if not os.path.exists(path):
        raise SystemExit(f"no 1s corpus for {d} ({path})")
    lo, hi = (ET_OPEN, ET_CLOSE) if args.rth_only else (0, 86400)
    df = con.execute(
        "SELECT bucket, vwap, volume, trade_count FROM read_parquet(?) "
        "WHERE ticker = ? AND bucket >= ? AND bucket <= ? ORDER BY bucket",
        [path, args.ticker, lo, hi]).fetchdf()
    if df.empty:
        raise SystemExit(f"{args.ticker} has no 1s bars on {d}")
    days[d] = df

fig = make_subplots(
    rows=3, cols=len(dates), shared_xaxes=True,
    row_heights=[0.56, 0.20, 0.24], vertical_spacing=0.035, horizontal_spacing=0.05,
    subplot_titles=[f"{args.ticker}  {d}" for d in dates] + [""] * (2 * len(dates)))

def hhmm(sec):
    return f"{sec // 3600:02d}:{sec % 3600 // 60:02d}"

for i, d in enumerate(days, start=1):
    df = days[d]
    x = df.bucket / 3600.0                       # ET hours — a linear wall clock, so voids are voids
    px_lo, px_hi = df.vwap.min(), df.vwap.max()
    uselog = px_hi / max(px_lo, 1e-9) > args.log_ratio

    fig.add_trace(go.Scatter(
        x=x, y=df.vwap, mode="lines+markers", name=f"vwap {d}",
        line=dict(width=1, color="#1f77b4"), marker=dict(size=2.5, color="#1f77b4"),
        hovertemplate="%{customdata}  $%{y:,.2f}<extra></extra>",
        customdata=[hhmm(int(b)) for b in df.bucket], showlegend=False), row=1, col=i)

    fig.add_trace(go.Bar(
        x=x, y=df.volume, name="vol", marker=dict(color="#7f7f7f"),
        hovertemplate="%{customdata}  %{y:,.0f} sh<extra></extra>",
        customdata=[hhmm(int(b)) for b in df.bucket], showlegend=False), row=2, col=i)

    # ⭐ the gap strip: how many of each minute's 60 seconds actually printed
    dens = con.execute(
        "SELECT bucket // 60 AS m, count(*) AS n FROM df GROUP BY 1 ORDER BY 1").fetchdf()
    fig.add_trace(go.Bar(
        x=dens.m / 60.0, y=dens.n, name="present s/min",
        marker=dict(color="#2ca02c"), width=1 / 60.0,
        hovertemplate="%{customdata}  %{y:.0f}/60 s<extra></extra>",
        customdata=[hhmm(int(m) * 60) for m in dens.m], showlegend=False), row=3, col=i)

    for r in (1, 2, 3):
        for sec, colr, dash in ((ET_OPEN, "#000000", "solid"), (ET_0945, "#d62728", "dot"),
                                (ET_1500, "#9467bd", "dot"), (ET_1559, "#d62728", "dash"),
                                (ET_CLOSE, "#000000", "solid")):
            fig.add_vline(x=sec / 3600.0, line=dict(color=colr, width=1, dash=dash),
                          opacity=0.45, row=r, col=i)
    fig.update_yaxes(title_text="vwap ($)" if i == 1 else None,
                     type="log" if uselog else "linear", row=1, col=i)
    fig.update_yaxes(title_text="volume" if i == 1 else None, row=2, col=i)
    fig.update_yaxes(title_text="present s/min" if i == 1 else None,
                     range=[0, 62], row=3, col=i)
    fig.update_xaxes(title_text="ET hour", row=3, col=i)

    n0945 = int(((df.bucket >= ET_OPEN) & (df.bucket < ET_0945)).sum())
    gaps_lh = 3600 - int(((df.bucket > ET_1500) & (df.bucket <= ET_1559)).sum())
    rth = df[(df.bucket >= ET_OPEN) & (df.bucket <= ET_CLOSE)]
    rng = f"{rth.vwap.min():,.2f} - {rth.vwap.max():,.2f}" if not rth.empty else "n/a"
    fig.add_annotation(
        text=(f"<b>n_bars_1s = {n0945}/900</b> (universe gate >= 200) &nbsp;|&nbsp; "
              f"<b>last-hour gaps = {gaps_lh:,}/3600</b> (Snoozer wants >= 1500)<br>"
              f"RTH vwap range {rng}"
              + ("  &nbsp;<b>[log axis]</b>" if uselog else "")),
        xref=f"x{'' if i == 1 else i} domain", yref=f"y{'' if i == 1 else i} domain",
        x=0.02, y=0.98, showarrow=False, align="left",
        font=dict(size=11), bgcolor="rgba(255,255,255,0.75)", row=1, col=i)

fig.update_layout(
    title=(f"{args.ticker} — 1s tape "
           f"(black 09:30/16:00 · red dot 09:45 universe cut · purple 15:00 · red dash 15:59)"),
    height=880, width=760 * len(dates), bargap=0, template="plotly_white",
    margin=dict(t=90, b=50, l=70, r=30))

script_dir = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(script_dir, "chart_controls.js")) as f:
    post_script = f.read()
fig.write_html(out, config={"scrollZoom": True, "displayModeBar": True},
               post_script=post_script)
print(f"wrote {out}")
for d, df in days.items():
    print(f"  {d}: {len(df):,} present seconds, "
          f"vwap {df.vwap.min():,.2f} - {df.vwap.max():,.2f}, "
          f"volume {df.volume.sum():,.0f}")
