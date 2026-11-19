# News trading — sources and the EDGAR pilot dataset

Started 2026-11-18. Goal: breaking news in micro and small caps, premarket and after-hours. This file records what
each news source turned out to allow, and the EDGAR dataset collected for the pilot. No results yet: the pilot
itself (filing acceptance times × the 1s tape) has not been run.

## Approach

1. **Pilot, free:** EDGAR filing metadata joined to the tape. Two measurements:
   - reaction speed: how much of the move happens in the first 1 s / 5 s / 30 s / 5 min after acceptance, and how
     often the price moved *before* the filing (the wire leading EDGAR);
   - coverage from the mover side: of the large premarket / after-hours moves in small caps, how many have a filing
     near them. The rest is the wire-only gap, which decides whether a paid wire feed is needed.
2. **Labels:** an LLM as a structured fact extractor (event type, offering size against market cap, discount,
   warrant coverage, counterparty) — never "is this good for the stock". Bucket tables before any model.
3. **Lookahead traps specific to news:**
   - the LLM may remember what happened after the event: extract only stated facts, run a ticker-anonymised
     control, and treat the window after the model's training cutoff as the only fully clean one;
   - a news timestamp is a signal gate: a vendor time earlier than true publication is a lookahead — validate every
     source's clock against the tape;
   - an 8-K can trail the press release by minutes to days.

## What each source allows (measured 2026-11-18)

| Source | History | Timestamps | Body | Verdict |
|---|---|---|---|---|
| SEC EDGAR `data.sec.gov` (submissions JSON) | complete | acceptance time, to the second, UTC | — | ✅ metadata; generic client name accepted |
| SEC EDGAR `www.sec.gov` (filing documents, daily index, live feed) | complete | — | full | ✅ needs a declared User-Agent with a contact (`$SEC_USER_AGENT`); 403 otherwise |
| Massive `/v2/reference/news` | yes | minute-rounded | ~450-char summary | ❌ a sample: ~100 GlobeNewswire items/day flat across 24 h (GlobeNewswire's own ID range shows 530–1,040/day); a quarter are law-firm ads |
| Massive Benzinga add-on | 2009+ | Benzinga's publish time | Benzinga's articles | ❌ not the releases: no wire name, no wire time (Massive's own knowledge base) |
| GlobeNewswire site / RSS | — | — | — | ❌ 403 to scripted clients, including `robots.txt` |
| ACCESSWIRE | — | — | — | ❌ `robots.txt` disallows `/rss` and `/api` |
| PR Newswire RSS | latest 20 items only | minute-rounded | ~300-char summary | ⚠ forward collection only |
| Benzinga direct API, standard news key | 2015+ (thin) | to the second | full | ❌ Benzinga editorial only; zero wire-authored items; 25 items/page |
| Benzinga press-release product | 2010+ | to the second (meaning unverified) | full | ⭐ the wire corpus; a separate licence — not yet tested |

No wire service sells reader access to its own archive: the issuer is the wire's customer, and history reaches
readers only through licensed aggregators.

## Sizing (sample of 400 of 5,548 listed issuers, 2026-05-18 → 2026-11-17)

- Issuers under \$2B: ~16k 8-Ks and ~9k 6-Ks in six months. **88% of their 8-K/6-K filings are accepted outside
  regular hours** (~70 premarket and ~105 after-hours per trading day).
- Top 8-K items (issuers under \$2B): 9.01, 7.01, 2.02, 8.01, 1.01, 5.02, 5.07, 3.02, 2.03, 5.03, 3.01.
- An 8-K is ~72 KB of raw html per filing (main document + EX-99), ~10 KB of text.

## The dataset — `data/news/edgar/` (not tracked)

| Path | Content | Size |
|---|---|---|
| `universe.parquet` | Massive CS + ADRC tickers: 5,676 active + 1,709 delisted since 2024-01-01 → 6,788 CIKs | |
| `filings/chunk_*.parquet` | **1,496,816 filings since 2024-01-01**, 6,785 issuers: form, filing date, acceptance time (UTC, to the second), 8-K items, submission size, primary document | 50 MB |
| `issuers/chunk_*.parquet` | SEC name, SIC, category, entity type, SEC tickers / exchanges per CIK | |
| `docs/<filing_date>/part_*.parquet` | raw html of the main document + every EX-99.* exhibit, forms 8-K, 6-K, 424B1/3/4/5/7, **2026-05-18 → 2026-11-17: 46,361 filings, 80,483 documents** | 12.9 GB raw, 903 MB zstd |

Not fetched in `docs/`, by design: 102 documents over 8 MB (mostly resale 424B3 prospectuses), 279 non-html
exhibits. One 424B3 answers HTTP 504 on every try. Text extraction from the raw html is a separate, repeatable step.

## Scripts — `scripts/news/`

```bash
cd research && python3 scripts/news/edgar_filings_index.py --out data/news/edgar --since 2024-01-01 \
    > logs/news_edgar_filings_index.log 2>&1; echo "exit $?"          # ~15 min
cd research && python3 scripts/news/edgar_filing_docs.py --meta data/news/edgar --from 2026-05-18 --to 2026-11-17 \
    > logs/news_edgar_filing_docs.log 2>&1; echo "exit $?"            # ~5 h at 8 requests/s; resumes by part
cd research && python3 scripts/news/edgar_filing_docs.py --meta data/news/edgar --from 2026-05-18 --to 2026-11-17 \
    --repair > logs/news_edgar_filing_docs_repair.log 2>&1; echo "exit $?"   # refetch error / index_error rows
```

⚠ The SEC answers 502/504 on every try for ~0.3% of filing index pages. The downloader gives up after four
tries (~70 s), records an `error` / `index_error` row, and for a dead index fetches the primary document named by the
metadata. The first run died at 135 min on a single 504; a later resume crawled at a quarter pace on silent
24-minute backoffs — both fixed, and every retry is now logged.
