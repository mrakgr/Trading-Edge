"""List which Binance USDM perp symbols Crypto Lake has order-book data for.

Calls `lakeapi.available_symbols(table='book', exchanges=['BINANCE_FUTURES'])`,
which returns a Series counting available days per (exchange, symbol). Writes
the per-symbol day counts to a tagged JSON manifest so the downloader and
backtest cohort-split can read from a single source of truth.

Usage:
    # Free sample bucket (anonymous AWS, no key required)
    python scripts/crypto/lake_universe.py --output data/crypto/lake_book_universe.json

    # Production bucket (requires AWS creds in env or ~/.aws/credentials)
    python scripts/crypto/lake_universe.py --paid \\
        --output data/crypto/lake_book_universe.json
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import lakeapi


def _format_symbol(s: str) -> str:
    """Crypto Lake uses 'BTC-USDT' (hyphenated). Our universe JSON elsewhere
    uses 'BTCUSDT' for Binance Vision. Carry both so consumers can pick the
    matching format for their data source."""
    flat = s.replace("-", "")
    return flat


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--output", default="data/crypto/lake_book_universe.json",
                   help="Output JSON path. Default: data/crypto/lake_book_universe.json")
    p.add_argument("--table", default="book",
                   choices=["book", "book_delta", "trades", "candles", "book_1m", "level_1",
                            "funding", "open_interest", "liquidations", "trades_mpid"],
                   help="Lake table to enumerate. Default: book")
    p.add_argument("--exchange", default="BINANCE_FUTURES",
                   help="Exchange tag. Default: BINANCE_FUTURES (Binance USDM perps).")
    p.add_argument("--paid", action="store_true",
                   help="Hit the paid bucket (requires AWS creds). Default uses sample.crypto.lake.")
    args = p.parse_args()

    if not args.paid:
        # Anonymous access against the sample bucket — no AWS credentials needed.
        # Coverage is a curated slice (~1 year, four data types per the website).
        lakeapi.use_sample_data(anonymous_access=True)
        print("Mode: free sample bucket (anonymous access)", file=sys.stderr)
    else:
        print("Mode: paid bucket (requires AWS credentials)", file=sys.stderr)

    print(f"Querying available_symbols(table={args.table!r}, exchanges=[{args.exchange!r}])...",
          file=sys.stderr)
    series = lakeapi.available_symbols(table=args.table, exchanges=[args.exchange])
    if series is None or len(series) == 0:
        print(f"No symbols returned. The {args.table!r} table likely isn't in the sample bucket.",
              file=sys.stderr)
        return 1

    # series is multi-indexed (exchange, symbol) -> day count.
    entries = []
    for (exchange, symbol), n_days in series.items():
        entries.append({
            "exchange": exchange,
            "symbol_lake": symbol,           # 'BTC-USDT' format
            "symbol_binance": _format_symbol(symbol),  # 'BTCUSDT' format
            "n_days_available": int(n_days),
        })
    entries.sort(key=lambda e: (-e["n_days_available"], e["symbol_binance"]))

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    with open(args.output, "w") as f:
        json.dump(entries, f, indent=2)
    print(f"Wrote {len(entries)} symbols to {args.output}", file=sys.stderr)

    print(f"\nTop 10 by day count:")
    for e in entries[:10]:
        print(f"  {e['symbol_binance']:20s} {e['n_days_available']:6d} days")
    if len(entries) > 10:
        print(f"  ... and {len(entries) - 10} more")
    return 0


if __name__ == "__main__":
    sys.exit(main())
