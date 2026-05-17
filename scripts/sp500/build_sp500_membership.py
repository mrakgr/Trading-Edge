"""Build daily S&P 500 membership using:

  1) Wikipedia's "Selected changes to the list of S&P 500 components" table as
     the company-level membership timeline (reverse-walked from today's snapshot
     of the components table).
  2) ticker_events (Polygon /vX/.../events, ingested via TradingEdge.Massive)
     to resolve each company to the *symbol that was active on each date*,
     using the FIGI as the chain identity.
  3) A small manual override map for renames Polygon's /events endpoint does
     not have (e.g., CDAY→DAY, FLT→CPAY, WLTW→WTW, CBS→VIAC→PARA→PSKY chain).

Output: one row per (date, ticker) for every business day in the target range.

Validation: --validate compares boundary days against the live tickers found
in /mnt/d/trading-edge-bulk/trades/{date}.parquet — for each membership row,
"is this symbol actually trading on that date?"
"""
from __future__ import annotations

import argparse
import bisect
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path

import duckdb
import pandas as pd
import requests

WIKI_URL = (
    'https://en.wikipedia.org/w/index.php'
    '?title=List_of_S%26P_500_companies&action=raw'
)

DEFAULT_DB = '/home/mrakgr/Trading-Edge/data/trading.db'
DEFAULT_OUT = '/home/mrakgr/Trading-Edge/data/sp500_membership.parquet'
BULK_TRADES_DIR = '/mnt/d/trading-edge-bulk/trades'

MONTHS = {
    'January': 1, 'February': 2, 'March': 3, 'April': 4, 'May': 5, 'June': 6,
    'July': 7, 'August': 8, 'September': 9, 'October': 10,
    'November': 11, 'December': 12,
}

# Manual override map: renames Polygon's /events endpoint either misses
# entirely or contradicts the actual trading history (e.g., when-issued
# variants like AWD/MPWRE that Polygon records but were never the primary
# trading symbol).
#
# Each entry is keyed by the *current* (latest) ticker. Two shapes:
#   - list[(effective_date, historical_ticker)]: explicit historical chain.
#     The resolver returns the last entry whose date is <= the query date.
#     Use date(1900, 1, 1) as a sentinel for "since the dawn of time".
#   - empty list []: "trust the current ticker, ignore ticker_events for this
#     symbol". Used to suppress junk chains (FISV→FI→FISV when Polygon only
#     has the FISV→FI half, or A→AWD when AWD was never primary).
#
# Sourced from the fja05680 sp500 repo's sp500_changes_since_2019.csv plus
# manual verification via S&P press releases / Wikipedia historical edits.
MANUAL_OVERRIDES: dict[str, list[tuple[date, str]]] = {
    # ── Real renames missing from Polygon's /events ──
    # Dayforce was Ceridian (ticker CDAY) until 2024-02-01.
    'DAY':  [(date(1900, 1, 1), 'CDAY'), (date(2024, 2, 1), 'DAY')],
    # Corpay was FleetCor Technologies (FLT) until 2024-03-25.
    'CPAY': [(date(1900, 1, 1), 'FLT'),  (date(2024, 3, 25), 'CPAY')],
    # CBS → ViacomCBS (VIAC) Dec 2019; ViacomCBS → Paramount (PARA) Feb 2022;
    # Paramount → Paramount Skydance (PSKY) Aug 2025.
    'PSKY': [(date(1900, 1, 1), 'CBS'),
             (date(2019, 12, 5), 'VIAC'),
             (date(2022, 2, 17), 'PARA'),
             (date(2025, 8, 8),  'PSKY')],
    # Marsh & McLennan rebranded to "Marsh" with new ticker MRSH on 2026-01-14.
    'MRSH': [(date(1900, 1, 1), 'MMC'), (date(2026, 1, 14), 'MRSH')],
    # Smurfit WestRock merger 2024-07-08 — WRK (WestRock) folded into SW.
    'SW':   [(date(1900, 1, 1), 'WRK'), (date(2024, 7, 8),  'SW')],
    # Fiserv: FISV → FI on 2023-06-07, then reverted FI → FISV on 2025-11-11.
    # Polygon's /events only has the first leg (FISV→FI) and never recorded
    # the reversal. So we hand-write the full chain. NB: this entry covers the
    # CURRENT symbol FISV; the FI entry below suppresses the partial chain.
    'FISV': [(date(1900, 1, 1), 'FISV'),
             (date(2023, 6, 7),  'FI'),
             (date(2025, 11, 11), 'FISV')],
    # FI is no longer in the S&P after 2025-11-11; but during 2023-06-07 →
    # 2025-11-10 it WAS the active symbol for company-name "Fiserv". Suppress
    # the FIGI fallback to avoid the broken Polygon chain.
    'FI':   [(date(1900, 1, 1), 'FISV'), (date(2023, 6, 7), 'FI')],

    # ── Suppress junk /events chains (when-issued tickers, etc.) ──
    # Agilent has traded as "A" since 1999; Polygon records a bogus
    # A→AWD ticker_change for 2005-11-23 that has no corresponding trades.
    'A':    [],
    # Monolithic Power Systems: bogus MPWR→MPWRE record (when-issued artifact).
    'MPWR': [],
    # News Corp Class A: bogus NWSA→NWSAV record (when-distributed).
    'NWSA': [],
    # Casey's General Stores: bogus CASY→CASYV record.
    'CASY': [],

    # ── Spinoffs that ARE new listings (documented; no entry needed) ──
    # VLTO (Veralto, 2023-10-02 from Danaher); SOLV (Solventum, 2024-04-01
    # from 3M); GEV (GE Vernova, 2024-04-02 from GE); SNDK (Sandisk,
    # 2025-02-21 from Western Digital); SOLS (Solstice, 2025-10-30 from
    # Honeywell); Q (Qnity, 2025-11-03 from DuPont). None need entries —
    # the new ticker IS the only ticker for that security.
}


@dataclass
class Change:
    effective: date
    added: list[str]
    removed: list[str]


def parse_date(s: str) -> date:
    m = re.match(r'\s*([A-Z][a-z]+)\s+(\d{1,2}),\s+(\d{4})\s*$', s)
    if not m:
        raise ValueError(f'bad date: {s!r}')
    month, day, year = m.group(1), int(m.group(2)), int(m.group(3))
    return date(year, MONTHS[month], day)


def strip_wiki_link(cell: str) -> str:
    """Pull a ticker symbol out of a wikitext cell."""
    c = cell.strip()
    c = re.sub(r'<ref[^>]*>.*?</ref>', '', c, flags=re.DOTALL)
    c = re.sub(r'<ref[^/]*/>', '', c)
    c = re.sub(r'\{\{[^}]*\}\}', '', c)
    m = re.match(r'\[\[([^\]]+)\]\]$', c)
    if m:
        c = m.group(1).split('|')[-1]
    return c.strip()


def parse_changes_table(wikitext: str) -> list[Change]:
    """Extract all rows from the 'Selected changes' wikitable.

    Handles both layouts:
      single-line:  |Date || Tadd || Sadd || Trem || Srem || Reason
      multi-line:   |Date\n|Tadd\n|Sadd\n|Trem\n|Srem\n|Reason
    Single-sided rows (only add or only remove) leave the opposite ticker cell
    blank and are kept; the caller emits an empty list for the missing side.
    """
    start = wikitext.index('==Selected changes to the list of S&P 500 components==')
    tail = wikitext[start:]
    tbl_start = tail.index('{|')
    tbl_end = tail.index('\n|}', tbl_start)
    table = tail[tbl_start:tbl_end]

    changes: list[Change] = []
    for raw in re.split(r'\n\|-\s*\n', table):
        # Collect all cells in row order, splitting on `||` for single-line
        # layout and accepting `\n|` as a hard cell boundary.
        cells: list[str] = []
        for ln in raw.split('\n'):
            if not ln.startswith('|'):
                continue
            ln = ln[1:]  # drop leading `|`
            cells.extend(ln.split('||'))
        cells = [c.strip() for c in cells]
        while len(cells) < 5:
            cells.append('')
        if not cells[0]:
            continue
        try:
            d = parse_date(cells[0])
        except ValueError:
            continue

        add_t = strip_wiki_link(cells[1])
        rem_t = strip_wiki_link(cells[3])
        changes.append(Change(
            d,
            [add_t] if add_t else [],
            [rem_t] if rem_t else [],
        ))

    changes.sort(key=lambda c: c.effective)
    return changes


def fetch_current_members(wikitext: str) -> set[str]:
    """Pull tickers from the 'S&P 500 component stocks' table.

    Wikipedia wraps each ticker in `{{NyseSymbol|MMM}}` or `{{NasdaqSymbol|...}}`
    templates. We pull the symbol out of those, plus accept a bare ticker as
    a fallback for any future template variants.
    """
    m = re.search(r'==\s*S&P 500 component stocks\s*==', wikitext)
    if not m:
        raise RuntimeError('component-stocks header not found')
    tail = wikitext[m.end():]
    tbl_start = tail.index('{|')
    tbl_end = tail.index('\n|}', tbl_start)
    table = tail[tbl_start:tbl_end]

    members: set[str] = set()
    for raw in re.split(r'\n\|-\s*\n', table):
        first_line = next(
            (ln for ln in raw.split('\n') if ln.startswith('|') and not ln.startswith('|-')),
            None,
        )
        if not first_line:
            continue
        first = first_line[1:].split('||')[0].strip()

        # Try the {{NyseSymbol|TICK}} / {{NasdaqSymbol|TICK}} template first.
        m_tmpl = re.search(r'\{\{(?:Nyse|Nasdaq|Bats)Symbol\|([A-Z][A-Z0-9.\-]*)', first)
        if m_tmpl:
            members.add(m_tmpl.group(1))
            continue

        # Pipe-link form: [[Foo|TICK]]
        m_pipe = re.search(r'\|([A-Z][A-Z0-9.\-]*)\]\]', first)
        if m_pipe:
            members.add(m_pipe.group(1))
            continue

        # Bare ticker fallback
        t = strip_wiki_link(first)
        if re.fullmatch(r'[A-Z][A-Z0-9.\-]*', t):
            members.add(t)
    return members


def build_company_snapshots(
    today_members: set[str],
    changes: list[Change],
    start: date,
    end: date,
) -> dict[date, set[str]]:
    """Reverse-walk the changes table to build a per-business-day snapshot of
    company-level membership.

    snapshots[d] = the set of members in force *as of* date d. We key only at
    change dates and the current date; a lookup for an arbitrary day uses
    "largest change_date <= d".
    """
    snapshots: dict[date, set[str]] = {}
    current = set(today_members)
    snapshots[date.today()] = set(current)
    for ch in reversed(changes):
        snapshots[ch.effective] = set(current)
        # Undo this change to get membership *strictly before* ch.effective.
        current -= set(ch.added)
        current |= set(ch.removed)

    change_dates = sorted(snapshots.keys())
    out: dict[date, set[str]] = {}
    for d in pd.bdate_range(start, end).date:
        idx = bisect.bisect_right(change_dates, d) - 1
        if idx < 0:
            continue
        out[d] = snapshots[change_dates[idx]]
    return out


def load_figi_chains(db_path: str) -> dict[str, list[tuple[date, str]]]:
    """For each FIGI, return its (event_date, ticker) chain, oldest first.

    Returned mapping: figi -> [(d0, t0), (d1, t1), ...] such that for any
    query date q, the ticker active under that FIGI is the last entry whose
    date is <= q.
    """
    con = duckdb.connect(db_path, read_only=True)
    df = con.execute(
        """
        SELECT figi, event_date, event_ticker
        FROM ticker_events
        WHERE figi IS NOT NULL
          AND event_ticker IS NOT NULL
          AND event_type = 'ticker_change'
        ORDER BY figi, event_date
        """
    ).fetchdf()
    con.close()

    chains: dict[str, list[tuple[date, str]]] = defaultdict(list)
    for figi, ed, tk in df.itertuples(index=False):
        # event_date arrives as datetime/Timestamp; normalize to date.
        d = ed.date() if hasattr(ed, 'date') else ed
        chains[figi].append((d, tk))
    return chains


def ticker_to_figi(db_path: str) -> dict[str, str]:
    """Mapping current_ticker -> figi via ticker_events.

    A ticker that has appeared in any chain maps to the FIGI of that chain.
    If a ticker is reused across FIGIs (recycled, e.g. FB), the most recent
    chain wins. This is what we want: today's S&P member 'FB' should resolve
    to today's FB-the-ETF FIGI if that ever became an S&P member — but in
    practice the current S&P list contains only the latest assignments.
    """
    con = duckdb.connect(db_path, read_only=True)
    df = con.execute(
        """
        SELECT te.event_ticker, te.figi, te.event_date
        FROM ticker_events te
        WHERE te.figi IS NOT NULL
          AND te.event_ticker IS NOT NULL
        ORDER BY te.event_ticker, te.event_date DESC
        """
    ).fetchdf()
    con.close()
    m: dict[str, str] = {}
    for tk, figi, _ in df.itertuples(index=False):
        m.setdefault(tk, figi)
    return m


def compute_first_seen(changes: list[Change], end: date) -> dict[str, date]:
    """For each ticker mentioned in the Wikipedia changes table (as an
    addition), record the earliest date on which it appears. Tickers that
    are *only* removed never had a "first seen" event (they were already in
    the index before our changes-table coverage started). For practical
    purposes, treat their first_seen as `end` so back-resolution never fires.
    """
    first_seen: dict[str, date] = {}
    seen_adds: set[str] = set()
    for ch in changes:
        for t in ch.added:
            seen_adds.add(t)
            if t not in first_seen or ch.effective < first_seen[t]:
                first_seen[t] = ch.effective
        # If a ticker is REMOVED in this change and we've never seen it added,
        # then it must have been a member from before our changes table starts
        # (or the rename rendered it untrackable). Use the removal date as a
        # weak upper bound on "first seen" so the resolver knows it existed
        # on this date.
        for t in ch.removed:
            if t not in seen_adds:
                if t not in first_seen or ch.effective < first_seen[t]:
                    first_seen[t] = ch.effective
    return first_seen


def resolve_ticker_on_date(
    current_ticker: str,
    on_date: date,
    chains: dict[str, list[tuple[date, str]]],
    ticker_to_figi_map: dict[str, str],
    first_seen: dict[str, date],
) -> str:
    """Return the ticker symbol that was active on `on_date` for the company
    currently known as `current_ticker`.

    Trust order:
      1) Manual override map. This is highest priority because it handles
         (a) renames Polygon's /events misses (CDAY→DAY, FLT→CPAY, CBS chain),
         (b) FI→FISV reversal that's neither in Polygon nor in Wikipedia's
             "Selected changes" log (Wikipedia treats it as a pure ticker
             change and excludes it by policy), and
         (c) suppression of junk /events entries (A→AWD, MPWR→MPWRE, etc.)
             via empty-list sentinels.
      2) The Wikipedia changes table: if the ticker was explicitly mentioned
         as added on or before `on_date`, return it as-is. This is the
         dominant case for most current members.
      3) FIGI chain from ticker_events.
      4) Identity.
    """
    if current_ticker in MANUAL_OVERRIDES:
        chain = MANUAL_OVERRIDES[current_ticker]
        if not chain:
            # Suppress sentinel: empty list means "ignore ticker_events for
            # this symbol, just keep the current ticker as-is".
            return current_ticker
        active = chain[0][1]
        for eff, tk in chain:
            if eff <= on_date:
                active = tk
            else:
                break
        return active

    fs = first_seen.get(current_ticker)
    if fs is not None and on_date >= fs:
        return current_ticker

    figi = ticker_to_figi_map.get(current_ticker)
    if figi and figi in chains:
        chain = chains[figi]
        active = chain[0][1]
        for eff, tk in chain:
            if eff <= on_date:
                active = tk
            else:
                break
        return active

    return current_ticker


def build_membership(
    company_snapshots: dict[date, set[str]],
    chains: dict[str, list[tuple[date, str]]],
    ticker_to_figi_map: dict[str, str],
    first_seen: dict[str, date],
) -> list[tuple[date, str]]:
    rows: list[tuple[date, str]] = []
    for d, members in sorted(company_snapshots.items()):
        for current_ticker in sorted(members):
            resolved = resolve_ticker_on_date(
                current_ticker, d, chains, ticker_to_figi_map, first_seen)
            rows.append((d, resolved))
    return rows


def validate(rows: list[tuple[date, str]], sample_dates: list[date]) -> None:
    """For each sample date, count how many membership tickers are actually
    present (have trades) in the bulk parquet for that day. Flags anything
    missing — those are tickers we got wrong (rename misses, recycled symbols,
    or genuine non-trading days)."""
    by_date: dict[date, set[str]] = defaultdict(set)
    for d, tk in rows:
        by_date[d].add(tk)

    con = duckdb.connect(':memory:')
    print(f'\nValidation across {len(sample_dates)} sample dates:', file=sys.stderr)
    for d in sample_dates:
        tickers = by_date.get(d)
        if not tickers:
            print(f'  {d}: no membership rows', file=sys.stderr)
            continue
        path = f'{BULK_TRADES_DIR}/{d.isoformat()}.parquet'
        if not Path(path).exists():
            print(f'  {d}: no parquet at {path}', file=sys.stderr)
            continue
        present = set(con.execute(
            f"SELECT DISTINCT ticker FROM read_parquet('{path}') WHERE ticker IN "
            f"({','.join(repr(t) for t in tickers)})"
        ).fetchdf()['ticker'])
        missing = sorted(tickers - present)
        coverage = 100.0 * len(present) / len(tickers)
        print(f'  {d}: {len(present)}/{len(tickers)} ({coverage:.1f}%) present in bulk parquet',
              file=sys.stderr)
        if missing:
            preview = ', '.join(missing[:15])
            tail = f' (+{len(missing) - 15} more)' if len(missing) > 15 else ''
            print(f'           missing: {preview}{tail}', file=sys.stderr)
    con.close()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument('--start', default='2024-01-01', help='inclusive (yyyy-mm-dd)')
    p.add_argument('--end', default=None, help='inclusive (yyyy-mm-dd); default today')
    p.add_argument('--db', default=DEFAULT_DB, help=f'DuckDB (default: {DEFAULT_DB})')
    p.add_argument('--out', default=DEFAULT_OUT, help=f'Output parquet (default: {DEFAULT_OUT})')
    p.add_argument('--changes-out', default=None,
                   help='Optional CSV of parsed (date, add, remove) changes for audit.')
    p.add_argument('--validate', action='store_true',
                   help='Sample-validate against the bulk trade parquets.')
    args = p.parse_args()

    start = datetime.strptime(args.start, '%Y-%m-%d').date()
    end = datetime.strptime(args.end, '%Y-%m-%d').date() if args.end else date.today()

    print(f'Fetching {WIKI_URL}', file=sys.stderr)
    headers = {'User-Agent': 'TradingEdge-sp500-membership/1.0 (mrakgr@gmail.com)'}
    wikitext = requests.get(WIKI_URL, headers=headers, timeout=30).text

    changes = parse_changes_table(wikitext)
    print(f'Parsed {len(changes)} change rows '
          f'({changes[0].effective} → {changes[-1].effective})', file=sys.stderr)

    members = fetch_current_members(wikitext)
    print(f'Current Wikipedia component table: {len(members)} tickers', file=sys.stderr)
    if len(members) < 400:
        raise RuntimeError(
            f'Component table parse looks broken ({len(members)} tickers found)')

    if args.changes_out:
        rows = [{'date': c.effective.isoformat(),
                 'add': ','.join(c.added),
                 'remove': ','.join(c.removed)} for c in changes]
        pd.DataFrame(rows).to_csv(args.changes_out, index=False)
        print(f'Wrote audit CSV: {args.changes_out}', file=sys.stderr)

    print(f'Loading FIGI chains from {args.db}', file=sys.stderr)
    chains = load_figi_chains(args.db)
    t2f = ticker_to_figi(args.db)
    print(f'  {len(chains)} chains; {len(t2f)} ticker→FIGI mappings', file=sys.stderr)

    snapshots = build_company_snapshots(members, changes, start, end)
    print(f'Built {len(snapshots)} per-day snapshots', file=sys.stderr)

    first_seen = compute_first_seen(changes, end)
    print(f'  {len(first_seen)} tickers with first-seen-in-wiki dates',
          file=sys.stderr)

    rows = build_membership(snapshots, chains, t2f, first_seen)
    df = pd.DataFrame(rows, columns=['date', 'ticker'])
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    df.to_parquet(out_path, index=False)
    n_days = df['date'].nunique()
    print(f'Wrote {out_path}: {len(df):,} rows, {n_days} days, '
          f'avg {len(df) / n_days:.1f} tickers/day', file=sys.stderr)

    if args.validate:
        # Sample: change-effective dates (where renames matter most) + a few
        # ordinary days for baseline. Limit to dates in [start, end].
        boundary = [c.effective for c in changes
                    if start <= c.effective <= end]
        # Take every Nth boundary + a few mid-month ordinary days.
        step = max(1, len(boundary) // 12)
        sample = sorted(set(boundary[::step]))
        # Add a couple of recent ordinary days for baseline.
        sample.append(start)
        sample.append(min(end, date(2025, 6, 16)))
        sample.append(min(end, date(2024, 6, 17)))
        sample = sorted(set(d for d in sample if start <= d <= end))
        validate(rows, sample)

    return 0


if __name__ == '__main__':
    sys.exit(main())
