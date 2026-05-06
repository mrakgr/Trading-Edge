"""Per-day peak-notional-at-risk for the z-persist no-stop system.

For each UTC day, computes the maximum simultaneous notional deployed
across all open positions in the trips CSV. This is the peak capital
the account needed at any moment that day to support the strategy at
the backtested $1000-per-trade size.

Algorithm: build a sweep-line over (entry_us, +notional) and (exit_us,
-notional) events; sort; walk the events in order while tracking
running notional. For each day, the day's max is the largest running
value within the day's [00:00, 24:00) window.

Also renders the per-day peak as a Plotly chart (single pane) to
logs/z_persist_notional_at_risk.html.

Use:

    python scripts/crypto/notional_at_risk.py
"""

import os

import duckdb
import pandas as pd
import plotly.graph_objects as go


TRIPS_CSV = "data/crypto/cumsum_z_persistexit/backtest_results_trips_1m_th15_ls.csv"


def main():
    repo_root = os.path.abspath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    path = os.path.join(repo_root, TRIPS_CSV)

    con = duckdb.connect()
    # Build the (timestamp, signed-delta-notional) event stream and sort it.
    # Then walk the events in time order, tracking running notional. For each
    # day we want max(running_notional) at any moment in the day.
    #
    # Two passes:
    #   1. Compute running notional at each event timestamp.
    #   2. Bucket by day and report (max running, day boundary running).
    #
    # The "running notional carried into a day" is the value just BEFORE
    # the day's first event. For the day's max we max(carry, max(events
    # within day)).
    con.execute(f"""
        CREATE TEMP TABLE events AS
        WITH t AS (
            SELECT entry_us AS ts, effective_notional AS dn
            FROM read_csv_auto('{path}')
            UNION ALL
            SELECT exit_us AS ts, -effective_notional AS dn
            FROM read_csv_auto('{path}')
        )
        SELECT ts, dn,
               make_timestamp(ts) AS ts_ts,
               CAST(make_timestamp(ts) AS DATE) AS day,
               SUM(dn) OVER (ORDER BY ts ROWS UNBOUNDED PRECEDING) AS running
        FROM t
        ORDER BY ts;
    """)

    # Per-day max running. Take the max over all events within the day, AND
    # the running value at the start of the day (carried in from prior days).
    # The latter equals the running value of the last event strictly before
    # the day's first event — which is just the max running going into the
    # first event minus the first event's contribution? Simpler: also include
    # the value just before the first event of the day. We compute that as
    # the (previous-event) running.
    df = con.execute("""
        WITH e AS (
            SELECT day, running, dn,
                   LAG(running) OVER (ORDER BY ts) AS running_before
            FROM events
        )
        SELECT day,
               MAX(GREATEST(running, COALESCE(running_before, 0.0))) AS max_notional,
               COUNT(*) AS n_events
        FROM e
        GROUP BY day
        ORDER BY day;
    """).fetchdf()

    # Forward-fill days with no events (notional was constant across them).
    if len(df) > 0:
        df["day"] = pd.to_datetime(df["day"])
        full_index = pd.date_range(df["day"].min(), df["day"].max(), freq="D")
        full = pd.DataFrame({"day": full_index})
        df = full.merge(df, on="day", how="left")
        df["max_notional"] = df["max_notional"].ffill().fillna(0.0)
        df["n_events"] = df["n_events"].fillna(0).astype(int)

    print()
    print("Per-day peak-notional-at-risk (z-cumsum + persist, no stop, $1000/trade)")
    print("=" * 72)
    print()

    # Summary stats.
    print(f"Total days:        {len(df):,}")
    print(f"Median peak:       ${df['max_notional'].median():>12,.0f}")
    print(f"Mean peak:         ${df['max_notional'].mean():>12,.0f}")
    print(f"p25 peak:          ${df['max_notional'].quantile(0.25):>12,.0f}")
    print(f"p75 peak:          ${df['max_notional'].quantile(0.75):>12,.0f}")
    print(f"p95 peak:          ${df['max_notional'].quantile(0.95):>12,.0f}")
    print(f"p99 peak:          ${df['max_notional'].quantile(0.99):>12,.0f}")
    print(f"Max peak ever:     ${df['max_notional'].max():>12,.0f}")
    print()

    # Top-20 days by peak notional.
    top = df.nlargest(20, "max_notional")
    print("Top-20 days by peak notional:")
    print(f"  {'day':<12s}  {'peak $':>14s}  {'events':>8s}")
    for _, r in top.iterrows():
        print(f"  {str(r['day']):<12s}  ${r['max_notional']:>13,.0f}  {r['n_events']:>8d}")

    # Save to CSV for downstream analysis.
    out_csv = os.path.join(repo_root, "logs", "z_persist_notional_at_risk.csv")
    os.makedirs(os.path.dirname(out_csv), exist_ok=True)
    df.to_csv(out_csv, index=False)
    print()
    print(f"Per-day series written to {out_csv}")

    # Chart.
    fig = go.Figure()
    fig.add_trace(
        go.Scatter(
            x=df["day"], y=df["max_notional"],
            mode="lines",
            name="Peak notional",
            line=dict(color="#1f77b4", width=1.2),
            fill="tozeroy",
            fillcolor="rgba(31, 119, 180, 0.15)",
        )
    )
    median = df["max_notional"].median()
    p95 = df["max_notional"].quantile(0.95)
    maxv = df["max_notional"].max()
    fig.add_hline(y=median, line_width=1, line_dash="dash", line_color="grey",
                  annotation_text=f"median ${median:,.0f}",
                  annotation_position="bottom right")
    fig.add_hline(y=p95, line_width=1, line_dash="dot", line_color="orange",
                  annotation_text=f"p95 ${p95:,.0f}",
                  annotation_position="top right")
    fig.add_hline(y=maxv, line_width=1, line_dash="dot", line_color="red",
                  annotation_text=f"max ${maxv:,.0f}",
                  annotation_position="top right")

    fig.update_layout(
        title="Z-cumsum + persist (no stop) — peak daily notional at risk ($1000/trade)",
        height=500, width=1700,
        hovermode="x unified",
        showlegend=False,
    )
    fig.update_yaxes(title_text="Peak notional deployed ($)")
    fig.update_xaxes(title_text="Date (UTC)")

    out_html = os.path.join(repo_root, "logs", "z_persist_notional_at_risk.html")
    config = {"scrollZoom": True, "displayModeBar": True}
    chart_controls = os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "..", "visualization", "chart_controls.js")
    chart_controls = os.path.abspath(chart_controls)
    with open(chart_controls) as f:
        post_script = f.read()
    fig.write_html(out_html, config=config, post_script=post_script)
    print(f"Chart saved to {out_html}")


if __name__ == "__main__":
    main()
