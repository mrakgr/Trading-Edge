import sys
import os
import duckdb
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from datetime import datetime, timedelta

def load_daily_data(db_path, ticker, end_date, years=3):
    """Load split-adjusted daily OHLCV data from DuckDB."""
    start_date = (datetime.strptime(end_date, '%Y-%m-%d') - timedelta(days=years * 365)).strftime('%Y-%m-%d')

    con = duckdb.connect(db_path, read_only=True)
    df = con.execute("""
        SELECT date, adj_open, adj_high, adj_low, adj_close, adj_volume
        FROM split_adjusted_prices
        WHERE ticker = ? AND date >= ? AND date <= ?
        ORDER BY date
    """, [ticker, start_date, end_date]).fetchdf()
    con.close()
    return df

def plot_daily_chart(df, ticker, end_date, output_html):
    """Create daily candlestick chart with volume."""
    fig = make_subplots(
        rows=2, cols=1,
        shared_xaxes=True,
        vertical_spacing=0.05,
        row_heights=[0.7, 0.3],
        subplot_titles=['Price', 'Volume']
    )

    # Build custom hover text with OHLCV
    hover_text = [
        f"Date: {row['date']}<br>"
        f"O: {row['adj_open']:.2f} H: {row['adj_high']:.2f}<br>"
        f"L: {row['adj_low']:.2f} C: {row['adj_close']:.2f}<br>"
        f"Volume: {int(row['adj_volume']):,}"
        for _, row in df.iterrows()
    ]

    fig.add_trace(go.Candlestick(
        x=df['date'],
        open=df['adj_open'],
        high=df['adj_high'],
        low=df['adj_low'],
        close=df['adj_close'],
        name='Price',
        text=hover_text,
        hoverinfo='text'
    ), row=1, col=1)

    fig.add_trace(go.Bar(
        x=df['date'],
        y=df['adj_volume'],
        name='Volume',
        marker_color='blue',
        opacity=0.5
    ), row=2, col=1)

    fig.update_layout(
        title=f'Daily Price Chart - {ticker} (Adjusted) ending {end_date}',
        height=700,
        width=1400,
        hovermode='x unified',
        showlegend=False,
        xaxis2_title='Date'
    )

    fig.update_xaxes(rangeslider_visible=False, row=1, col=1)
    fig.update_xaxes(rangebreaks=[dict(bounds=['sat', 'mon'])], row=1, col=1)
    fig.update_xaxes(rangebreaks=[dict(bounds=['sat', 'mon'])], row=2, col=1)
    fig.update_yaxes(title_text='Price', type='log', row=1, col=1)
    fig.update_yaxes(title_text='Volume', row=2, col=1)

    # Enable horizontal crosshair showing price at cursor
    fig.update_yaxes(showspikes=True, spikemode='across', spikesnap='cursor',
                     spikethickness=1, spikecolor='gray', spikedash='dot',
                     row=1, col=1)
    fig.update_xaxes(showspikes=True, spikemode='across', spikesnap='cursor',
                     spikethickness=1, spikecolor='gray', spikedash='dot',
                     row=1, col=1)

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
    ticker = sys.argv[1] if len(sys.argv) > 1 else 'LW'
    end_date = sys.argv[2] if len(sys.argv) > 2 else datetime.today().strftime('%Y-%m-%d')
    output_html = sys.argv[3] if len(sys.argv) > 3 else f'data/charts/{ticker}_daily.html'
    db_path = sys.argv[4] if len(sys.argv) > 4 else 'data/trading.db'

    print(f'Loading daily data for {ticker} ending {end_date}...')
    df = load_daily_data(db_path, ticker, end_date)
    print(f'Loaded {len(df)} records')

    if len(df) == 0:
        print(f'No data found for {ticker}')
    else:
        print(f'Plotting...')
        plot_daily_chart(df, ticker, end_date, output_html)
