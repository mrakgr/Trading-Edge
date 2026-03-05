import json
import sys
import os
import numpy as np
import plotly.graph_objects as go
from tdigest import TDigest

def load_trades(json_path):
    with open(json_path) as f:
        trades = json.load(f)
    trades.sort(key=lambda t: t['participant_timestamp'])
    return trades

def plot_tdigest(json_path, output_html):
    trades = load_trades(json_path)
    sizes = [t['size'] for t in trades if t['size'] > 0]

    # Build t-digest
    digest = TDigest(delta=0.00022, K=1024)
    for size in sizes:
        digest.update(size)

    # Get quantiles for plotting
    quantiles = np.linspace(0, 1, 1000)
    values = [digest.percentile(q * 100) for q in quantiles]

    # Get median and range
    median = digest.percentile(50)
    min_val = max(min(sizes), 1e-10)
    max_val = max(sizes)

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
                  annotation_text=f"Median: {median:.0f}")

    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()

    fig.update_layout(
        title=f'T-Digest of Trade Sizes ({len(trades):,} trades)',
        xaxis_title='Trade Size',
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
    print(f"Average: {overall_mean:.2f}")
    print(f"Median: {median:.2f}")
    print(f"Bottom 50% avg: {bottom_mean:.2f}")
    print(f"Top 50% avg: {top_mean:.2f}")
    print(f"P25: {digest.percentile(25):.2f}")
    print(f"P75: {digest.percentile(75):.2f}")
    print(f"P95: {digest.percentile(95):.2f}")

if __name__ == '__main__':
    json_path = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'

    if len(sys.argv) > 2:
        output_html = sys.argv[2]
    else:
        ticker = os.path.basename(os.path.dirname(json_path))
        date = os.path.splitext(os.path.basename(json_path))[0]
        output_dir = f'data/charts/massive/{ticker}_{date}'
        os.makedirs(output_dir, exist_ok=True)
        output_html = f'{output_dir}/tdigest_volume.html'

    plot_tdigest(json_path, output_html)
