"""Annualized return on deployed capital — adjudicate V1 vs Qullamaggie exit.

The momentum_v0 backtest is uncapped, non-compounding, fixed-$10k-notional with
unlimited concurrent positions, so net P&L is a raw edge measure, not an achievable
equity curve. PF favors V1 (20-day time stop, 1.64) over Qulla (entry-day-low stop,
1.50), but Qulla recycles capital faster (median 17-day hold vs 20). The metric that
adjudicates is ANNUALIZED RETURN ON DEPLOYED CAPITAL — PF undersells fast turnover.

Two stages, both post-hoc over the trips CSVs (no engine rerun), on the IDENTICAL
5-filter final system so V1-vs-Qulla is apples-to-apples:

  Stage A — return on realized concurrent demand (no cap, no portfolio rule):
    Sweep-line over (entry, +notional) / (exit, -notional) events -> daily concurrent
    deployed notional. Capital base = peak / p99 / p95 / mean of that series. Ann RoC =
    total_pnl / capital_base / trading-day-years. Headline p95 (robust to a few extreme
    2021 cluster-days you'd realistically not fully fund).

  Stage B — fixed-$100k NON-compounding book, drop-overflow, min-Kelly sizing:
    Walk entries in date order against a fixed $100k book (profits swept, not
    reinvested -> flat yield, not CAGR). Size each trade by its min(breadth_t, rvol_t)
    half-Kelly weight; if it doesn't fit in remaining capacity, SKIP it (no eviction).
    Outputs annualized return on the $100k book + trade-capture rate.

Reuses the sweep-line pattern from scripts/crypto/notional_at_risk.py (adapted from
microsecond timestamps to YYYY-MM-DD dates).

Use:
    python scripts/equity/return_on_capital.py
"""

import os

import duckdb
import pandas as pd
import plotly.graph_objects as go


REPO_ROOT = os.path.abspath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# The two systems on the identical filtered population. `band_in_csv` = whether the
# 0.85*hiclose_52w term must be applied here (V1) or is already baked in-engine (Qulla,
# run with --min-pct-of-52w-high 0.85 so hiclose_52w is empty in its CSV).
SYSTEMS = [
    ("V1 (20d time stop)",
     "data/equity/momentum_v0/trips_v1_structure.csv", True),
    ("Qulla (entry-day-low)",
     "data/equity/momentum_v0/trips_variantB_qulla.csv", False),
]

BREADTH = "data/equity/momentum_v0/breadth.parquet"
NOTIONAL = 10_000.0           # backtest's fixed per-trip notional
BOOK = 100_000.0              # Stage B fixed book (non-compounding)
PER_POSITION_CAP = 20_000.0  # Stage B hard per-position cap (no single name > this)
TRADING_DAYS_PER_YEAR = 252.0

# min(breadth_tercile, rvol_tercile) -> half-Kelly fraction (fit earlier on this data,
# in-sample). Normalized to mean weight 1 across the *filtered* population at sim time.
HALF_KELLY = {1: 0.074, 2: 0.108, 3: 0.176}


def load_filtered(con: duckdb.DuckDBPyConnection, path: str, band_in_csv: bool):
    """Filtered final-system trips with breadth lag-1 and the min-of-indices bucket.

    Returns a pandas DataFrame: entry_date, exit_date, entry_price, exit_price, ret,
    pnl, bt, rt, min_bucket. Filter = the 5-filter system; for Qulla the 0.85-band is
    already in-engine so that term is dropped (hiclose_52w is empty there).
    """
    abspath = os.path.join(REPO_ROOT, path)
    band_term = ("AND r.entry_price >= 0.85 * CAST(r.hiclose_52w AS DOUBLE)"
                 if band_in_csv else "")
    sql = f"""
        WITH raw AS (
            SELECT
                CAST(entry_date AS DATE)             AS entry_date,
                CAST(exit_date  AS DATE)             AS exit_date,
                CAST(entry_price AS DOUBLE)          AS entry_price,
                CAST(exit_price  AS DOUBLE)          AS exit_price,
                CAST(rvol_at_entry AS DOUBLE)        AS rvol,
                CAST(tightness_14_at_entry AS DOUBLE) AS tightness,
                hiclose_52w
            FROM read_csv_auto('{abspath}', header=true)
        ),
        b AS (
            SELECT date,
                   LAG(pct_above_20) OVER (ORDER BY date) AS breadth_lag1
            FROM read_parquet('{os.path.join(REPO_ROOT, BREADTH)}')
        ),
        f AS (
            SELECT r.entry_date, r.exit_date, r.entry_price, r.exit_price, r.rvol,
                   b.breadth_lag1 AS breadth,
                   r.exit_price / r.entry_price - 1.0 AS ret,
                   {NOTIONAL} / r.entry_price * (r.exit_price - r.entry_price) AS pnl,
                   CASE WHEN b.breadth_lag1 < 0.61 THEN 1
                        WHEN b.breadth_lag1 < 0.70 THEN 2 ELSE 3 END AS bt,
                   CASE WHEN r.rvol < 7.2 THEN 1
                        WHEN r.rvol < 9.6 THEN 2 ELSE 3 END AS rt
            FROM raw r
            JOIN b ON b.date = r.entry_date
            WHERE r.entry_price >= 5.0
              {band_term}
              AND b.breadth_lag1 > 0.5
              AND r.rvol >= 6.0 AND r.rvol <= 20.0
              AND r.tightness < 0.30
        )
        SELECT *, LEAST(bt, rt) AS min_bucket
        FROM f
        ORDER BY entry_date
    """
    return con.execute(sql).fetchdf()


def concurrent_series(con: duckdb.DuckDBPyConnection, df: pd.DataFrame):
    """Daily concurrent deployed notional via a date-keyed sweep-line.

    A position is held over [entry_date, exit_date) — released the morning it exits
    (fills at the exit_date open) — so same-day churn isn't double-counted. Returns a
    DataFrame day, max_notional (ffilled over gap days). Mirrors notional_at_risk.py.
    """
    con.register("trips", df)
    con.execute(f"""
        CREATE OR REPLACE TEMP TABLE events AS
        WITH t AS (
            SELECT entry_date AS d, {NOTIONAL} AS dn FROM trips
            UNION ALL
            SELECT exit_date  AS d, -{NOTIONAL} AS dn FROM trips
        )
        SELECT d,
               SUM(dn) OVER (ORDER BY d ROWS UNBOUNDED PRECEDING) AS running
        FROM t
        ORDER BY d
    """)
    daily = con.execute("""
        WITH e AS (
            SELECT d, running,
                   LAG(running) OVER (ORDER BY d) AS running_before
            FROM events
        )
        SELECT d AS day,
               MAX(GREATEST(running, COALESCE(running_before, 0.0))) AS max_notional
        FROM e GROUP BY d ORDER BY d
    """).fetchdf()
    if len(daily) > 0:
        daily["day"] = pd.to_datetime(daily["day"])
        full = pd.DataFrame({"day": pd.date_range(
            daily["day"].min(), daily["day"].max(), freq="D")})
        daily = full.merge(daily, on="day", how="left")
        daily["max_notional"] = daily["max_notional"].ffill().fillna(0.0)
    return daily


def stage_a(df: pd.DataFrame, daily: pd.DataFrame):
    """Return-on-realized-demand stats. years = span / 252 (trading-day-years)."""
    total_pnl = float(df["pnl"].sum())
    span_days = (df["exit_date"].max() - df["entry_date"].min()).days
    years = span_days / 365.25  # calendar span; annualization base below uses it directly
    bases = {
        "peak": float(daily["max_notional"].max()),
        "p99": float(daily["max_notional"].quantile(0.99)),
        "p95": float(daily["max_notional"].quantile(0.95)),
        "mean": float(daily["max_notional"].mean()),
    }
    roc = {k: (total_pnl / v / years if v > 0 else float("nan"))
           for k, v in bases.items()}
    return total_pnl, years, bases, roc


def stage_b(df: pd.DataFrame):
    """Fixed-$100k NON-compounding book, min-Kelly sizing, drop-the-new overflow.

    Per-trade target $ = BOOK * w / mean(w) / EXP_SLOTS, where w is the trade's
    half-Kelly weight; capped at PER_POSITION_CAP. EXP_SLOTS scales the mean trade so
    the book holds a sensible number of concurrent names (set so mean deployment ~ book).
    Walk entries in date order; release capital on exit_date; skip a trade if its sized
    notional exceeds remaining capacity. Returns (annual_return, capture_rate, equity_df).
    """
    w = df["min_bucket"].map(HALF_KELLY).to_numpy()
    mean_w = w.mean()
    # Target average concurrent deployment ~= BOOK. Estimate average concurrency from the
    # data (avg positions open) so per-trade $ * avg_concurrency ~= BOOK at mean weight.
    # avg concurrency = total holding-days / span-days.
    hold_days = (df["exit_date"] - df["entry_date"]).dt.days.clip(lower=1)
    span_days = (df["exit_date"].max() - df["entry_date"].min()).days
    avg_concurrency = float(hold_days.sum()) / span_days
    base_dollar = BOOK / max(avg_concurrency, 1.0)
    sized = (df["min_bucket"].map(HALF_KELLY) / mean_w * base_dollar).clip(
        upper=PER_POSITION_CAP).to_numpy()

    # Event-ordered sim: process entries chronologically; maintain a heap of (exit_date,
    # notional) releases. Simpler: iterate days, but the trip set is small (~4.9k) so a
    # straightforward scan with a running list is fine.
    rows = df.assign(sized=sized).sort_values("entry_date").reset_index(drop=True)
    deployed = 0.0
    open_pos = []  # list of (exit_date, notional)
    accepted_pnl = 0.0
    n_accept = 0
    equity_dates, equity_vals = [], []
    realized = 0.0
    for r in rows.itertuples(index=False):
        # release any positions that exited on/before this entry date
        still = []
        for ex, nt, pl in open_pos:
            if ex <= r.entry_date:
                deployed -= nt
                realized += pl
            else:
                still.append((ex, nt, pl))
        open_pos = still
        # try to accept this trade
        size = float(r.sized)
        if deployed + size <= BOOK:
            deployed += size
            # P&L scales with sized notional (vs the $10k backtest notional)
            trade_pnl = r.pnl * (size / NOTIONAL)
            open_pos.append((r.exit_date, size, trade_pnl))
            n_accept += 1
            accepted_pnl += trade_pnl
        equity_dates.append(r.entry_date)
        equity_vals.append(realized)
    # flush remaining
    for ex, nt, pl in open_pos:
        realized += pl
    annual_return = realized / BOOK / (span_days / 365.25)
    capture = n_accept / len(rows)
    eq = pd.DataFrame({"day": pd.to_datetime(equity_dates), "realized": equity_vals})
    return realized, annual_return, capture, n_accept, len(rows), eq


def main():
    con = duckdb.connect()
    results = {}
    for name, path, band in SYSTEMS:
        df = load_filtered(con, path, band)
        daily = concurrent_series(con, df)
        total_pnl, years, bases, roc = stage_a(df, daily)
        b_realized, b_annual, b_capture, b_acc, b_tot, eq = stage_b(df)
        results[name] = dict(df=df, daily=daily, total_pnl=total_pnl, years=years,
                             bases=bases, roc=roc, b_realized=b_realized,
                             b_annual=b_annual, b_capture=b_capture,
                             b_acc=b_acc, b_tot=b_tot, eq=eq)

    print()
    print("=" * 78)
    print("ANNUALIZED RETURN ON DEPLOYED CAPITAL — V1 vs Qullamaggie")
    print("=" * 78)
    for name, R in results.items():
        df = R["df"]
        med_hold = int((df["exit_date"] - df["entry_date"]).dt.days.median())
        pf = (df.loc[df.ret > 0, "ret"].sum()
              / -df.loc[df.ret < 0, "ret"].sum())
        print(f"\n### {name}")
        print(f"  filtered trips:   {len(df):,}")
        print(f"  total P&L:        ${R['total_pnl']:>14,.0f}")
        print(f"  PF:               {pf:>6.3f}   median hold: {med_hold} cal-days")
        print(f"  span (years):     {R['years']:.2f}")
        print(f"  --- Stage A: concurrent capital base & annualized RoC ---")
        for k in ("peak", "p99", "p95", "mean"):
            print(f"    {k:>5s} concurrent ${R['bases'][k]:>12,.0f}  ->  "
                  f"ann RoC {R['roc'][k]*100:>7.1f}%")
        print(f"  --- Stage B: fixed $100k book (non-compounding) ---")
        print(f"    realized P&L:   ${R['b_realized']:>14,.0f}")
        print(f"    ann return:     {R['b_annual']*100:>7.1f}%   "
              f"capture {R['b_capture']*100:.1f}% ({R['b_acc']:,}/{R['b_tot']:,})")

    # Side-by-side headline (p95 RoC + Stage B).
    print()
    print("-" * 78)
    print(f"{'metric':<28s}{'V1 (20d time)':>22s}{'Qulla (day-low)':>22s}")
    print("-" * 78)
    v1 = results["V1 (20d time stop)"]
    qu = results["Qulla (entry-day-low)"]
    def row(label, vf, qf):
        print(f"{label:<28s}{vf:>22s}{qf:>22s}")
    row("p95 concurrent capital", f"${v1['bases']['p95']:,.0f}", f"${qu['bases']['p95']:,.0f}")
    row("ann RoC @ p95", f"{v1['roc']['p95']*100:.1f}%", f"{qu['roc']['p95']*100:.1f}%")
    row("ann RoC @ mean", f"{v1['roc']['mean']*100:.1f}%", f"{qu['roc']['mean']*100:.1f}%")
    row("Stage B ann return", f"{v1['b_annual']*100:.1f}%", f"{qu['b_annual']*100:.1f}%")
    row("Stage B capture", f"{v1['b_capture']*100:.1f}%", f"{qu['b_capture']*100:.1f}%")
    print("-" * 78)

    # Chart: concurrent-capital series for both systems.
    out_dir = os.path.join(REPO_ROOT, "logs")
    os.makedirs(out_dir, exist_ok=True)
    fig = go.Figure()
    for name, R in results.items():
        fig.add_trace(go.Scatter(x=R["daily"]["day"], y=R["daily"]["max_notional"],
                                 mode="lines", name=name, line=dict(width=1.0)))
    fig.update_layout(
        title="Daily concurrent deployed notional ($10k/trip) — V1 vs Qullamaggie",
        height=500, width=1700, hovermode="x unified")
    fig.update_yaxes(title_text="Concurrent notional ($)")
    fig.update_xaxes(title_text="Date")
    cc = os.path.join(REPO_ROOT, "scripts", "visualization", "chart_controls.js")
    with open(cc) as fh:
        post_script = fh.read()
    out_html = os.path.join(out_dir, "momentum_return_on_capital.html")
    fig.write_html(out_html, config={"scrollZoom": True, "displayModeBar": True},
                   post_script=post_script)
    print(f"\nChart saved to {out_html}")


if __name__ == "__main__":
    main()
