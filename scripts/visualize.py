import sys
import pyarrow.parquet as pq
import plotly.graph_objects as go
from plotly.subplots import make_subplots

def load_day(path, day_id):
    t = pq.read_table(path).to_pandas()
    return t[t['day_id'] == day_id].sort_values('time').reset_index(drop=True)

def make_chart(df, day_id):
    timeframes = [('1s', '1s'), ('1m', '1m'), ('5m', '5m')]
    fig = make_subplots(
        rows=6, cols=1, shared_xaxes=True,
        row_heights=[3, 1, 3, 1, 3, 1],
        vertical_spacing=0.02,
        subplot_titles=[t for tf, _ in timeframes for t in [f'VWAP {tf}', f'Volume {tf}']]
    )

    times = df['time'] / 60.0  # minutes

    periods = {'1s': 1, '1m': 60, '5m': 300}

    for i, (tf, label) in enumerate(timeframes):
        price_row = i * 2 + 1
        vol_row = i * 2 + 2
        period = periods[tf]
        mask = df['time'] % period == period - 1
        t = times[mask]
        vwap = df[f'vwap_{tf}'][mask]
        std = df[f'stddev_{tf}'][mask]
        vol = df[f'volume_{tf}'][mask]

        # S/R levels (only on 1s chart since they're per-second)
        if tf == '1s':
            fig.add_trace(go.Scatter(
                x=t, y=df['support'][mask], mode='lines', name='Support',
                line=dict(color='green', width=1, dash='dot')
            ), row=price_row, col=1)
            fig.add_trace(go.Scatter(
                x=t, y=df['resistance'][mask], mode='lines', name='Resistance',
                line=dict(color='red', width=1, dash='dot')
            ), row=price_row, col=1)

        # VWAP line
        fig.add_trace(go.Scatter(
            x=t, y=vwap, mode='lines', name=f'VWAP {label}',
            line=dict(color='blue', width=1)
        ), row=price_row, col=1)

        # StdDev band
        fig.add_trace(go.Scatter(
            x=t, y=vwap + std, mode='lines', name=f'+1σ {label}',
            line=dict(width=0), showlegend=False
        ), row=price_row, col=1)
        fig.add_trace(go.Scatter(
            x=t, y=vwap - std, mode='lines', name=f'-1σ {label}',
            line=dict(width=0), fill='tonexty', fillcolor='rgba(0,100,255,0.15)', showlegend=False
        ), row=price_row, col=1)

        # Volume bars
        fig.add_trace(go.Bar(
            x=t, y=vol, name=f'Vol {label}',
            marker_color='rgba(100,100,100,0.5)'
        ), row=vol_row, col=1)

    fig.update_layout(
        title=f'Day {day_id} - VWAP / StdDev / Volume',
        height=1800, showlegend=False,
        xaxis6=dict(title='Time (minutes)')
    )
    return fig

if __name__ == '__main__':
    path = sys.argv[1] if len(sys.argv) > 1 else 'data/viz_sample.parquet'
    day_id = int(sys.argv[2]) if len(sys.argv) > 2 else 0

    t = pq.read_table(path).to_pandas()
    available = sorted(t['day_id'].unique())
    if day_id not in available:
        day_id = available[0]
        print(f"Day not found, using day {day_id}")

    df = load_day(path, day_id)
    fig = make_chart(df, day_id)
    out = f'data/viz_day_{day_id}.html'
    fig.write_html(out)
    print(f"Saved to {out}")
