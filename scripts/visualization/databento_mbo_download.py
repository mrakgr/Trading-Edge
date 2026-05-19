"""Download MBO (Market By Order) data from Databento for one ticker-day
across one or more lit US-equity exchanges.

MBO is the lowest-level order-book feed: every Add, Cancel, Modify, Trade,
Fill, and start-of-day clear/snapshot. The first MBO record each session is
an `R` (clear book) followed by `A` (add) records that reconstitute the
resting book, so MBO alone is enough to rebuild the full book without a
separate MBP feed.

Default venue set is every lit US-equity exchange that ships MBO on
Databento (13 venues). IEX is excluded because it only ships top-of-book
(IEXG.TOPS); NYSE National (XCIS.PILLAR) is excluded because its MBO
schema isn't published. The remaining 13 cover ~99% of lit US-equity
volume with a uniform MBO schema.

Output layout (one file per venue):
    data/databento/mbo/<SYMBOL>/<DATE>/<VENUE_CODE>.dbn.zst

Read back with:
    import databento as db
    store = db.DBNStore.from_file("path/to/file.dbn.zst")
    for rec in store: ...

Cost is gated: prints per-venue and aggregate estimates and refuses to
download if the aggregate exceeds --cost-limit. Per-venue downloads are
idempotent (skipped if the output file already exists).
"""

import argparse
import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import databento as db


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_COST_LIMIT_USD = 2.0
DEFAULT_OUT_DIR = REPO_ROOT / "data" / "databento" / "mbo"

# Every lit US-equity exchange registered with the SEC, with its Databento
# venue-specific dataset. Whether MBO is actually available is probed at
# runtime via list_schemas — venues without MBO today (IEX, NYSE National)
# are skipped automatically, and the downloader will pick them up if/when
# Databento adds MBO coverage.
LIT_VENUES = [
    ("XNAS",  "XNAS.ITCH"),     # Nasdaq
    ("ARCX",  "ARCX.PILLAR"),   # NYSE Arca
    ("EDGX",  "EDGX.PITCH"),    # Cboe EDGX
    ("XNYS",  "XNYS.PILLAR"),   # NYSE
    ("BATS",  "BATS.PITCH"),    # Cboe BZX
    ("MEMX",  "MEMX.MEMOIR"),   # MEMX
    ("EPRL",  "EPRL.DOM"),      # MIAX Pearl
    ("EDGA",  "EDGA.PITCH"),    # Cboe EDGA
    ("BATY",  "BATY.PITCH"),    # Cboe BYX
    ("XBOS",  "XBOS.ITCH"),     # Nasdaq Boston/Texas
    ("XPSX",  "XPSX.ITCH"),     # Nasdaq PSX
    ("XASE",  "XASE.PILLAR"),   # NYSE American
    ("XCHI",  "XCHI.PILLAR"),   # NYSE Chicago/Texas
    ("IEXG",  "IEXG.TOPS"),     # IEX  (no MBO today — TOPS only)
    ("XCIS",  "XCIS.PILLAR"),   # NYSE National  (no MBO today)
]


def load_api_key():
    with open(REPO_ROOT / "api_key.json") as f:
        return json.load(f)["databento_api_key"]


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--symbol", required=True, help="Ticker, e.g. EOSE")
    ap.add_argument("--date", required=True,
                    help="UTC day to download (YYYY-MM-DD). Pulls [date 00:00, date+1 00:00) UTC.")
    ap.add_argument("--venues", default=None,
                    help="Comma-separated venue codes to fetch. Default: every lit US-equity "
                         "exchange whose dataset currently lists `mbo` as a schema. "
                         f"Known venues: {','.join(v for v,_ in LIT_VENUES)}")
    ap.add_argument("--out-dir", default=None,
                    help=f"Override output directory. Default: {DEFAULT_OUT_DIR}/<SYMBOL>/<DATE>/")
    ap.add_argument("--stype-in", default="raw_symbol",
                    help="Symbology type for input symbols (default 'raw_symbol')")
    ap.add_argument("--cost-limit", type=float, default=DEFAULT_COST_LIMIT_USD,
                    help=f"Refuse to download if AGGREGATE cost exceeds this (USD). Default ${DEFAULT_COST_LIMIT_USD:g}.")
    ap.add_argument("--force", action="store_true",
                    help="Skip the cost-limit check and download regardless.")
    return ap.parse_args()


def main():
    args = parse_args()
    day = datetime.strptime(args.date, "%Y-%m-%d").date()
    start = datetime.combine(day, datetime.min.time(), tzinfo=timezone.utc)
    end = start + timedelta(days=1)

    # Resolve which venues to consider (filtering by MBO availability comes next)
    if args.venues is None:
        candidates = list(LIT_VENUES)
    else:
        wanted = {v.strip().upper() for v in args.venues.split(",") if v.strip()}
        known = {v: ds for v, ds in LIT_VENUES}
        unknown = wanted - known.keys()
        if unknown:
            print(f"Unknown venue codes: {sorted(unknown)}. Allowed: {sorted(known)}",
                  file=sys.stderr)
            sys.exit(2)
        candidates = [(v, known[v]) for v in sorted(wanted)]

    out_dir = Path(args.out_dir) if args.out_dir else DEFAULT_OUT_DIR / args.symbol / args.date
    out_dir.mkdir(parents=True, exist_ok=True)

    client = db.Historical(load_api_key())

    # Build the plan: per-venue path, cost, and skip-if-cached flag.
    # Skip venues without MBO and venues whose history doesn't cover the date.
    plan = []
    aggregate_cost = 0.0
    print(f"Quoting {args.symbol} {args.date} MBO across {len(candidates)} candidate venues...")
    print(f'{"venue":<6} {"dataset":<14} {"status":<14} {"cost":>8}')
    print("-" * 50)
    for venue, dataset in candidates:
        out_path = out_dir / f"{venue}.dbn.zst"
        if out_path.exists():
            print(f"{venue:<6} {dataset:<14} {'cached':<14} {'-':>8}")
            plan.append((venue, dataset, out_path, 0.0, True))
            continue
        # Availability probe — schema set and active date range
        try:
            schemas = set(client.metadata.list_schemas(dataset=dataset))
        except Exception as exc:
            msg = str(exc).split("\n")[0][:60]
            print(f"{venue:<6} {dataset:<14} {'schemas-err':<14} {msg}")
            continue
        if "mbo" not in schemas:
            print(f"{venue:<6} {dataset:<14} {'no-mbo':<14} {'-':>8}")
            continue
        try:
            rng = client.metadata.get_dataset_range(dataset=dataset)
            ds_start = datetime.strptime(rng["start"][:10], "%Y-%m-%d").date()
            ds_end = datetime.strptime(rng["end"][:10], "%Y-%m-%d").date()
        except Exception as exc:
            msg = str(exc).split("\n")[0][:60]
            print(f"{venue:<6} {dataset:<14} {'range-err':<14} {msg}")
            continue
        if not (ds_start <= day <= ds_end):
            print(f"{venue:<6} {dataset:<14} {'out-of-range':<14} ({ds_start} → {ds_end})")
            continue
        try:
            cost = client.metadata.get_cost(
                dataset=dataset, start=start, end=end,
                symbols=[args.symbol], schema="mbo", stype_in=args.stype_in,
            )
        except Exception as exc:
            msg = str(exc).split("\n")[0][:60]
            print(f"{venue:<6} {dataset:<14} {'cost-err':<14} {msg}")
            continue
        aggregate_cost += cost
        print(f"{venue:<6} {dataset:<14} {'pending':<14} ${cost:>6.4f}")
        plan.append((venue, dataset, out_path, cost, False))

    print("-" * 50)
    print(f"Aggregate new-download cost: ${aggregate_cost:.4f} USD")
    if not args.force and aggregate_cost > args.cost_limit:
        print(f"Refusing: aggregate cost ${aggregate_cost:.4f} exceeds limit "
              f"${args.cost_limit:.2f}. Re-run with --force or raise --cost-limit.",
              file=sys.stderr)
        sys.exit(2)

    # Execute downloads
    total_bytes = 0
    n_ok = 0
    n_skip = 0
    n_err = 0
    for venue, dataset, out_path, cost, cached in plan:
        if cached:
            n_skip += 1
            total_bytes += out_path.stat().st_size
            continue
        try:
            store = client.timeseries.get_range(
                dataset=dataset, start=start, end=end,
                symbols=[args.symbol], schema="mbo", stype_in=args.stype_in,
            )
            store.to_file(out_path)
            size = out_path.stat().st_size
            total_bytes += size
            n_ok += 1
            print(f"  {venue}: wrote {size/(1024*1024):.2f} MB")
        except Exception as exc:
            print(f"  {venue}: download error: {exc}", file=sys.stderr)
            n_err += 1

    print()
    print(f"Done. {n_ok} downloaded, {n_skip} cached, {n_err} errors. "
          f"Total on disk: {total_bytes/(1024*1024):.2f} MB at {out_dir}")


if __name__ == "__main__":
    main()
