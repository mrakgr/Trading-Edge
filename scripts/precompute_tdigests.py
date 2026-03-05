import json
import sys
import pickle
from tdigest import TDigest

def load_and_merge_trades(json_path, merge_threshold_ns=100000):
    """Load trades and merge those within threshold nanoseconds."""
    with open(json_path) as f:
        trades = json.load(f)

    trades.sort(key=lambda t: t['participant_timestamp'])

    # Merge trades
    merged = []
    current_group = [trades[0]]

    for trade in trades[1:]:
        if trade['participant_timestamp'] - current_group[0]['participant_timestamp'] < merge_threshold_ns:
            current_group.append(trade)
        else:
            merged.append(merge_group(current_group))
            current_group = [trade]

    merged.append(merge_group(current_group))
    return merged

def merge_group(group):
    """Merge a group of trades into one with VWAP."""
    if len(group) == 1:
        return group[0]

    total_size = sum(t['size'] for t in group)
    vwap = sum(t['price'] * t['size'] for t in group) / total_size

    return {
        'participant_timestamp': group[0]['participant_timestamp'],
        'price': vwap,
        'size': total_size
    }

def build_tdigests(trades):
    """Build t-digests for sizes and time gaps."""
    # Filter out zero-sized trades
    trades = [t for t in trades if t['size'] > 0]

    # Build size t-digest
    size_digest = TDigest(delta=0.00022, K=1024)
    for t in trades:
        size_digest.update(t['size'])

    # Calculate time gaps in seconds
    gaps = []
    for i in range(1, len(trades)):
        gap_ns = trades[i]['participant_timestamp'] - trades[i-1]['participant_timestamp']
        gap_s = gap_ns / 1e9
        gaps.append(gap_s)

    # Build gap t-digest
    gap_digest = TDigest(delta=0.00022, K=1024)
    for g in gaps:
        gap_digest.update(g)

    return size_digest, gap_digest

if __name__ == '__main__':
    input_json = sys.argv[1] if len(sys.argv) > 1 else 'data/trades/LW/2025-12-19.json'
    output_path = sys.argv[2] if len(sys.argv) > 2 else 'data/tdigests/LW_2025-12-19.pkl'

    print(f'Loading and merging trades from {input_json}...')
    trades = load_and_merge_trades(input_json)
    print(f'After merging: {len(trades)} trades')

    print('Building t-digests...')
    size_digest, gap_digest = build_tdigests(trades)

    print(f'Size digest: {len(size_digest.C)} centroids')
    print(f'Gap digest: {len(gap_digest.C)} centroids')

    # Save to pickle
    import os
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, 'wb') as f:
        pickle.dump({'size': size_digest, 'gap': gap_digest}, f)

    print(f'Saved to {output_path}')

