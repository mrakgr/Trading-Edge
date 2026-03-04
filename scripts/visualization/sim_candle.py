import csv
import sys
import plotly.graph_objects as go

def load_trades(csv_path):
    trades = []
    with open(csv_path) as f:
        r = csv.DictReader(f)
        for row in r:
            trades.append({
                'time': float(row['time']),
                'price': float(row['price']),
                'size': int(row['size'])
            })
    return trades

def create_time_bars(trades, seconds_per_bar):
    if not trades:
        return []

    bars = []
    start_time = trades[0]['time']
    current_bar_start = start_time
    current_bar_end = current_bar_start + seconds_per_bar
    bar_trades = []

    for trade in trades:
        timestamp = trade['time']

        while timestamp >= current_bar_end:
            if bar_trades:
                bars.append(compute_bar_ohlc(bar_trades, current_bar_start))
                bar_trades = []
            current_bar_start = current_bar_end
            current_bar_end = current_bar_start + seconds_per_bar

        bar_trades.append(trade)

    if bar_trades:
        bars.append(compute_bar_ohlc(bar_trades, current_bar_start))

    return bars

def compute_bar_ohlc(bar_trades, bar_start_time):
    prices = [t['price'] for t in bar_trades]
    volumes = [t['size'] for t in bar_trades]

    return {
        'timestamp': bar_start_time,
        'open': bar_trades[0]['price'],
        'high': max(prices),
        'low': min(prices),
        'close': bar_trades[-1]['price'],
        'volume': sum(volumes),
        'num_trades': len(bar_trades)
    }

def plot_candlesticks(bars, output_html, seconds_per_bar):
    fig = go.Figure()

    x_vals = [b['timestamp'] / 60.0 for b in bars]
    open_vals = [b['open'] for b in bars]
    high_vals = [b['high'] for b in bars]
    low_vals = [b['low'] for b in bars]
    close_vals = [b['close'] for b in bars]

    hover_text = [
        f"Time: {b['timestamp']/60:.2f}m<br>"
        f"O: {b['open']:.4f} H: {b['high']:.4f}<br>"
        f"L: {b['low']:.4f} C: {b['close']:.4f}<br>"
        f"Volume: {b['volume']:,}<br>"
        f"Trades: {b['num_trades']}"
        for b in bars
    ]

    fig.add_trace(go.Candlestick(
        x=x_vals,
        open=open_vals,
        high=high_vals,
        low=low_vals,
        close=close_vals,
        name='Price',
        text=hover_text,
        hoverinfo='text'
    ))

    fig.update_layout(
        title=f'Candlestick Chart ({seconds_per_bar}s bars) - Simulation Data',
        xaxis_title='Time (minutes)',
        yaxis_title='Price',
        height=800,
        width=1400,
        hovermode='x unified',
        xaxis_rangeslider_visible=False
    )

    config = {
        'scrollZoom': True,
        'displayModeBar': True
    }

    post_script = """
    document.addEventListener('mousedown', function(e) {
        if (e.button === 1) {
            e.preventDefault();
            var gd = document.querySelector('.plotly-graph-div');
            var currentMode = gd.layout.dragmode;
            var newMode = currentMode === 'zoom' ? 'pan' : 'zoom';
            Plotly.relayout(gd, {'dragmode': newMode});
        }
    });
    document.addEventListener('keydown', function(e) {
        var gd = document.querySelector('.plotly-graph-div');
        if (e.key === 'a') {
            Plotly.relayout(gd, {'dragmode': 'zoom'});
        } else if (e.key === 's') {
            Plotly.relayout(gd, {'dragmode': 'pan'});
        } else if (e.key === 'd') {
            Plotly.relayout(gd, {'dragmode': 'select'});
        }
    });
    """

    fig.write_html(output_html, config=config, post_script=post_script)
    print(f'Saved to {output_html}')

if __name__ == '__main__':
    input_csv = sys.argv[1] if len(sys.argv) > 1 else 'data/test_hmm.csv'
    seconds_per_bar = int(sys.argv[2]) if len(sys.argv) > 2 else 60
    output_html = sys.argv[3] if len(sys.argv) > 3 else 'data/charts/sim_candle.html'

    print(f'Loading trades from {input_csv}...')
    trades = load_trades(input_csv)
    print(f'Loaded {len(trades)} trades')

    print(f'Creating {seconds_per_bar}s time bars...')
    bars = create_time_bars(trades, seconds_per_bar)
    print(f'Created {len(bars)} time bars')

    print(f'Plotting...')
    plot_candlesticks(bars, output_html, seconds_per_bar)
