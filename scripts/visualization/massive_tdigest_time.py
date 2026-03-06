import json
import sys
import os
import numpy as np
import plotly.graph_objects as go
from tdigest import TDigest
from datetime import datetime, timezone

def load_trades(json_path):
    with open(json_path) as f:
        trades = json.load(f)
    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def filter_market_hours(trades, market_open, market_close):
    """Filter trades to only include regular market hours (UTC)."""
    filtered = []
    for t in trades:
        dt = datetime.fromtimestamp(t['participant_timestamp'] / 1e9, timezone.utc)
        hour = dt.hour + dt.minute / 60.0
        if market_open <= hour <= market_close:
            filtered.append(t)
    return filtered

def plot_tdigest(json_path, output_html, market_open, market_close):
    trades = load_trades(json_path)
    trades = filter_market_hours(trades, market_open, market_close)
    trades = [t for t in trades if t['size'] > 0]

    # Calculate time deltas in seconds
    deltas = []
    for i in range(1, len(trades)):
        delta_ns = trades[i]['participant_timestamp'] - trades[i-1]['participant_timestamp']
        delta_s = delta_ns / 1e9
        deltas.append(delta_s)

    # Build t-digest
    digest = TDigest(delta=0.00022, K=1024)
    for d in deltas:
        digest.update(d)

    # Get quantiles for plotting
    quantiles = np.linspace(0, 1, 1000)
    values = [digest.percentile(q * 100) for q in quantiles]

    # Get median and range
    median = digest.percentile(50)
    min_val = max(min(deltas), 1e-10)
    max_val = max(deltas)

    fig = go.Figure()

    # Area chart of distribution
    fig.add_trace(go.Scatter(
        x=values,
        y=quantiles,
        fill='tozeroy',
        name='CDF',
        line=dict(color='blue', width=2)
    ))

    # Mark median
    fig.add_vline(x=median, line_dash="dash", line_color="red",
                  annotation_text=f"Median: {median:.3f}s")

    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()

    fig.update_layout(
        title=f'T-Digest of Time Deltas ({len(deltas):,} intervals)',
        xaxis_title='Time Delta (seconds)',
        yaxis_title='Cumulative Probability',
        xaxis_type='log',
        xaxis_range=[np.log10(min_val), np.log10(max_val)],
        hovermode='x',
        template='plotly_white'
    )

    fig.write_html(output_html, post_script=post_script, config={'scrollZoom': True})

    # Calculate statistics from centroids
    centroids = digest.centroids_to_list()
    total_count = sum(c['c'] for c in centroids)
    overall_mean = sum(c['m'] * c['c'] for c in centroids) / total_count

    # Calculate means for bottom and top 50%
    bottom_sum = 0
    bottom_count = 0
    top_sum = 0
    top_count = 0

    for c in centroids:
        if c['m'] < median:
            bottom_sum += c['m'] * c['c']
            bottom_count += c['c']
        else:
            top_sum += c['m'] * c['c']
            top_count += c['c']

    bottom_mean = bottom_sum / bottom_count if bottom_count > 0 else 0
    top_mean = top_sum / top_count if top_count > 0 else 0

    print(f"Saved to {output_html}")
    print(f"Centroids: {len(digest.C)}")
    print(f"Average: {overall_mean:.4f}s")
    print(f"Median: {median:.4f}s")
    print(f"Bottom 50% avg: {bottom_mean:.4f}s")
    print(f"Top 50% avg: {top_mean:.4f}s")
    print(f"P25: {digest.percentile(25):.4f}s")
    print(f"P75: {digest.percentile(75):.4f}s")
    print(f"P95: {digest.percentile(95):.4f}s")

if __name__ == '__main__':
    json_path = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'
    market_open = float(sys.argv[2]) if len(sys.argv) > 2 else 14.5
    market_close = float(sys.argv[3]) if len(sys.argv) > 3 else 21.0

    if len(sys.argv) > 4:
        output_html = sys.argv[4]
    else:
        ticker = os.path.basename(os.path.dirname(json_path))
        date = os.path.splitext(os.path.basename(json_path))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/tdigest_time.html'

    plot_tdigest(json_path, output_html, market_open, market_close)
