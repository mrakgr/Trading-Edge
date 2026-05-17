# S&P 500 Daily Membership Pipeline

How `data/sp500_membership.parquet` is built and why each stage exists.
Output is one row per `(date, ticker)` for every business day in the
target window, where `ticker` is **the symbol that was actually trading
on that date** — not the company's current name. This makes the parquet
joinable directly against Polygon's bulk trade parquets
(`/mnt/d/trading-edge-bulk/trades/{date}.parquet`), which themselves use
point-in-time tickers.

The script is `scripts/sp500/build_sp500_membership.py`. There is no
F# CLI command — it's a one-shot Python utility because the inputs (the
Wikipedia revision, the `ticker_events` table) settle slowly and the
script is short.

## Why this is hard

The S&P 500 turns over ~25 names per year. On top of that, several
companies rename their ticker without the company itself changing
(FB → META, FISV → FI → back to FISV, CDAY → DAY, FLT → CPAY,
the CBS → ViacomCBS → Paramount → PSKY chain). The *index* membership
tracks the company; the *trade data* uses whichever symbol was active
on a given day. A naive "current list" approach fails on backtests that
cross a rename boundary.

There is no single authoritative free source that gets both correct.
Each candidate has gaps:

| Source                                | What it knows                                       | What it misses                                                  |
|---------------------------------------|-----------------------------------------------------|-----------------------------------------------------------------|
| Wikipedia "Selected changes" table    | Company-level index adds/removes since 1976         | Pure ticker renames (editor policy excludes them)               |
| Polygon `/vX/.../events`              | FIGI-anchored ticker_change events                  | Coverage gaps; bogus when-issued tickers (AWD, NWSAV)           |
| Polygon bulk trade parquets           | Authoritative ticker per trading day                | No index-membership marker; can't tell who's in S&P             |
| `fja05680/sp500` GitHub repo          | Curated ticker-level membership through ~Jan 2026   | Needs manual top-up; updates lag a few months                   |
| Paid (Norgate, CRSP, etc.)            | Both correct                                        | $$$                                                             |

The pipeline below stitches the **free** sources together with a small
hand-maintained override map to bridge their gaps.

## Stage map

```
Wikipedia: List_of_S&P_500_companies          Polygon: /vX/reference/tickers/{T}/events
        │                                              │
        │  (parse changes table +                      │  (one-shot bulk fetch per ticker)
        │   current components table)                  │
        ▼                                              ▼
company-level timeline                          data/tickers/events/{ticker}.json
{change_date: members_after_change}             (19,198 files, ~76 MB)
        │                                              │
        │                                              │  (ingest-ticker-events)
        │                                              ▼
        │                                       data/tickers/events.parquet
        │                                       + ticker_events DuckDB table
        │                                       (11,956 rows, 9,880 FIGIs)
        │                                              │
        └─────────────────────┬────────────────────────┘
                              │
                              │  resolver per (date, current_ticker):
                              │    1) MANUAL_OVERRIDES (highest priority)
                              │    2) Wikipedia first-seen check (no change needed)
                              │    3) FIGI chain from ticker_events
                              │    4) identity fallback
                              ▼
                  data/sp500_membership.parquet
                  (date, ticker) - one row per index member per business day
```

## Prerequisites

1. **`ticker_events` populated.** Run `download-ticker-events` followed by
   `ingest-ticker-events` from `TradingEdge.Massive`. This populates the
   Polygon rename chains and is normally done once per Polygon subscription
   period. See `TradingEdge.Massive/README.md` for the commands; raw JSON
   is preserved under `data/tickers/events/` so the data survives DB
   resets and subscription expiration.

2. **Python dependencies**: `pandas`, `requests`, `pyarrow`, `duckdb`.
   All standard. No virtualenv required for current usage.

3. **Optional but recommended**: `/mnt/d/trading-edge-bulk/trades/*.parquet`
   for `--validate` mode (compares membership against actual trading data).

## Running it

```bash
# Default: 2024-01-01 → today, writes data/sp500_membership.parquet
python3 scripts/sp500/build_sp500_membership.py

# Custom window with validation:
python3 scripts/sp500/build_sp500_membership.py \
    --start 2024-01-01 --end 2026-05-17 --validate

# Dump parsed changes for audit:
python3 scripts/sp500/build_sp500_membership.py \
    --changes-out /tmp/sp500_changes.csv
```

Validation samples ~15 dates (the rename-boundary dates plus a few
ordinary midweek days) and reports, for each, what fraction of the
membership tickers actually appear in the bulk parquet that day. Healthy
output is 100.0% on every date except known acquisition gaps (e.g.,
CTLT in late Dec 2024).

## The resolver, end to end

For each `(date, current_ticker)` pair from the company-level snapshot:

**Step 1 — MANUAL_OVERRIDES.** If `current_ticker` has an entry in the
override map, use it. Two flavors:

- **Explicit chain**: `'DAY': [(date(1900,1,1), 'CDAY'), (date(2024,2,1), 'DAY')]`
  — pick the last entry whose date is `<= on_date`. Used for renames
  Polygon either misses (CDAY → DAY) or for which Polygon has only a
  partial chain (FISV → FI → FISV: the second flip isn't in Polygon).
- **Suppress sentinel**: `'A': []` — empty list means "trust the
  current ticker as-is; ignore the FIGI chain even if one exists."
  Used to suppress Polygon's bogus when-issued entries like A → AWD,
  MPWR → MPWRE, NWSA → NWSAV, CASY → CASYV.

The override map is the only piece of human-curated data in the pipeline,
and intentionally small (~10 chains, ~4 suppressions). It lives in
`MANUAL_OVERRIDES` at the top of `build_sp500_membership.py`.

**Step 2 — Wikipedia first-seen shortcut.** For each ticker added as a
new index member in the Wikipedia changes table, record the date it
first appeared. If `on_date >= first_seen[ticker]`, return the ticker
as-is. This is the dominant case for current members — most tickers
have been the active symbol since well before our 2024 window starts.

**Step 3 — FIGI chain.** Look up the current ticker's FIGI in
`ticker_events`, then walk the chain of `(event_date, event_ticker)`
events for that FIGI. Return the last event whose date is `<= on_date`.
This handles the well-behaved Polygon-recorded renames (PEAK → DOC,
FB → META, FISV → FI on the outbound leg).

**Step 4 — Identity fallback.** If none of the above fire, return
`current_ticker` unchanged. Covers tickers with clean histories that
predate our data sources.

## What goes in MANUAL_OVERRIDES

Whenever validation reports `missing: SOMETICKER` on a known
non-acquisition date, that's the trigger to investigate. Check
`ticker_events` for the chain Polygon has, compare against
the fja05680 `sp500_changes_since_2019.csv` and/or S&P press releases,
and either:

- Add an explicit override chain if a real rename is missing or only
  partially recorded.
- Add a suppress sentinel `[]` if Polygon recorded a chain that points
  to a non-primary symbol (when-issued, when-distributed, distribution
  variants — typically with `V`, `W`, `I`, `E` single-letter suffixes).

After each `download-ticker-events` re-fetch, re-run `--validate` and
expand the override map if new false-positives surface.

## Wikipedia changes table parsing notes

Wikipedia's source is at
`https://en.wikipedia.org/w/index.php?title=List_of_S%26P_500_companies&action=raw`.
The page returns abridged content to bare User-Agent strings; set a
real UA. Two wikitable layouts coexist in the changes table:

- Single-line: `|Date || Tadd || Sadd || Trem || Srem || Reason`
- Multi-line: `|Date\n|Tadd\n|Sadd\n|Trem\n|Srem\n|Reason`

The parser splits on `||` and concatenates `\n|` cells in row order,
then pads short rows to 5 cells so single-sided rows (only an add or
only a remove) survive. Cell content goes through `strip_wiki_link`
to handle `[[Foo|TICK]]` link syntax and `<ref>...</ref>` /
`{{...}}` markup.

The current-components table uses a `{{NyseSymbol|MMM}}` or
`{{NasdaqSymbol|...}}` template per row — the regex
`\{\{(?:Nyse|Nasdaq|Bats)Symbol\|([A-Z][A-Z0-9.\-]*)` pulls the
symbol out. Fallback to `[[Foo|TICK]]` pipe-link syntax and finally
to bare ticker text.

## Reverse-walking the snapshot

We don't trust the Wikipedia "current components" table as
historical truth — Wikipedia editors sometimes lag the changes table
by days. Instead, we **reverse-apply** the changes from today's
component-list snapshot to reconstruct each past snapshot:

```
snapshots[today] = current_members
for change in reversed(changes):
    snapshots[change.effective] = current  # in force ON the effective date
    current = current - change.added + change.removed
```

Lookup for an arbitrary date D = `snapshots[max(change_date <= D)]`.
By construction the "today" snapshot is consistent with Wikipedia's
current components table (within a few days of lag), and every prior
snapshot is exact under the assumption that the changes table is
complete at the company level.

## Known limitations

1. **CTLT-like acquisition gaps.** S&P sometimes leaves a soon-to-be-
   acquired stock in the index for several trading days after it
   stops trading. The membership file faithfully reflects S&P's
   notion of membership, so you'll get a row for CTLT on
   2024-12-18..2024-12-22 even though the stock had zero trades. For
   breadth/screener use, an inner join with `split_adjusted_prices`
   or the bulk parquet naturally drops these rows. Do not "fix" this
   in the membership file.

2. **Pure-ticker-only renames pre-dating override coverage.** If a
   rename happens that we haven't added to `MANUAL_OVERRIDES` and
   Polygon also missed it, the resolver falls back to the
   `current_ticker` — which will not be present in the bulk parquet
   on dates before the rename. Validation catches this immediately
   (date-specific `missing:` line); the fix is a new override entry.

3. **Recycled tickers.** Polygon's reference data shows that `FB` is
   now classified as an ETF (after Meta dropped the symbol). The
   resolver currently picks the most-recent FIGI chain for any
   ticker, which is correct for the current S&P composition but
   would be wrong if a recycled symbol ever became an S&P member
   under its new identity. None currently do.

4. **Coverage starts 2024-01-01 by default.** The pipeline works
   for earlier windows but `MANUAL_OVERRIDES` is curated for
   2024+ events only. Pre-2024 renames (e.g., the BBT/STI → TFC
   merger in 2019) would need additional entries.

## When to re-run

- **Once a week** during a tape-reading session, to capture the
  latest index changes. Wikipedia is updated within a few days of
  any S&P add/remove announcement.
- **After re-running `download-ticker-events`** (rare; typically only
  when a Polygon subscription is being refreshed and new renames
  may have happened since the last bulk fetch).
- **If validation surfaces a new `missing:` ticker**, audit the
  rename → add an override → re-run.

## Output schema

`data/sp500_membership.parquet`:

| Column | Type    | Notes                                                  |
|--------|---------|--------------------------------------------------------|
| date   | DATE    | Business day (`pd.bdate_range`, no holidays filter)    |
| ticker | VARCHAR | Symbol active on that date — joinable to bulk parquet  |

Typical size: ~311k rows × 2 cols for 620 business days ≈ a few MB.

A row exists for every business day, including market holidays — the
joiner is responsible for intersecting with `trading_calendar` if a
holiday-aware view is needed. Keeping the membership pure-calendar
makes it cheap to recompute and easy to reason about.
