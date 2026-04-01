#!/usr/bin/env python3
"""Visualize volume bars from CSV."""

import pandas as pd
import plotly.graph_objects as go
from plotly.subplots import make_subplots
import sys
import os

def plot_volume_bars(csv_path, output_html):
    df = pd.read_csv(csv_path)

    # Parse timestamps
    df['start_time'] = pd.to_datetime(df['start_time'])
    df['end_time'] = pd.to_datetime(df['end_time'])
    df['duration_s'] = (df['end_time'] - df['start_time']).dt.total_seconds()

    # Calculate session VWAP
    session_vwap = (df['vwap'] * df['volume']).sum() / df['volume'].sum()

    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['VWAP ±2σ', 'Time Duration (seconds)']
    )

    # Calculate bar heights and colors
    upper_2sigma = df['vwap'] + 2 * df['stddev']
    lower_2sigma = df['vwap'] - 2 * df['stddev']

    # Apply minimum height scaled to session VWAP
    min_height = min(0.01, 0.001 * session_vwap)
    bar_heights = []
    bar_bases = []

    for u, l, vwap in zip(upper_2sigma, lower_2sigma, df['vwap']):
        height = u - l
        actual_height = max(height, min_height)
        bar_heights.append(actual_height)
        bar_bases.append(vwap - actual_height / 2)

    colors = ['green' if u >= l else 'red' for u, l in zip(upper_2sigma, lower_2sigma)]

    # Plot bars
    fig.add_trace(go.Bar(
        x=df['cumulative_volume'],
        y=bar_heights,
        base=bar_bases,
        name='VWAP ±2σ',
        marker_color=colors,
        marker_line_width=0,
        width=df['volume'] * 0.8,
        hovertemplate='<b>Volume:</b> %{x:,.0f}<br>' +
                      '<b>VWAP:</b> %{customdata[0]:.2f}<br>' +
                      '<b>StdDev:</b> %{customdata[1]:.6f}<br>' +
                      '<b>Duration:</b> %{customdata[2]:.3f}s<extra></extra>',
        customdata=df[['vwap', 'stddev', 'duration_s']].values
    ), row=1, col=1)

    # Add VWAP line
    fig.add_trace(go.Scatter(
        x=df['cumulative_volume'],
        y=df['vwap'],
        mode='lines',
        name='VWAP',
        line=dict(color='blue', width=1),
        hoverinfo='skip'
    ), row=1, col=1)

    # Add session VWAP line
    fig.add_hline(y=session_vwap, line_dash="dash", line_color="orange",
                  annotation_text=f"Session VWAP: {session_vwap:.2f}", row=1, col=1)

    # Plot time duration
    fig.add_trace(go.Scatter(
        x=df['cumulative_volume'],
        y=df['duration_s'],
        fill='tozeroy',
        mode='lines',
        name='Duration',
        line=dict(color='blue', width=1),
        fillcolor='rgba(0, 0, 255, 0.3)',
        hovertemplate='Volume: %{x:,.0f}<br>Duration: %{y:.3f}s<extra></extra>'
    ), row=2, col=1)

    fig.update_layout(
        height=700,
        width=1400,
        hovermode='closest',
        xaxis2_title='Cumulative Volume',
        showlegend=False
    )

    fig.update_yaxes(title_text='Price', row=1, col=1)
    fig.update_yaxes(title_text='Seconds', row=2, col=1, autorange='reversed')

    config = {
        'scrollZoom': True,
        'displayModeBar': True
    }

    script_dir = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(script_dir, 'chart_controls.js')) as f:
        post_script = f.read()

    fig.write_html(output_html, config=config, post_script=post_script)
    print(f'Saved to {output_html}')

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python plot_volume_bars.py <csv_path> [output_html]")
        sys.exit(1)

    csv_path = sys.argv[1]
    output_html = sys.argv[2] if len(sys.argv) > 2 else csv_path.replace('.csv', '.html')

    plot_volume_bars(csv_path, output_html)


