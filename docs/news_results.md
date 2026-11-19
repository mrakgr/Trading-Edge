# News trading — sources and the EDGAR pilot dataset

Started 2026-11-18. Goal: breaking news in micro and small caps, premarket and after-hours. This file records what
each news source turned out to allow, the EDGAR dataset collected for the pilot, and the pilot's results (filing
acceptance times × the 1s tape, 2026-11-19 — §Pilot results).

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

## Pilot results — EDGAR acceptance × the 1s tape (2026-11-19)

**Data:** 286,583 filings (8-K, 6-K, 424B*, 13D, S-1/F-1/S-3/F-3 and amendments), 2024-01-02 → 2026-11-16,
→ 283,035 events with tape (one per ticker × acceptance second, split days dropped). "Small" = mean daily dollar
volume of the 20 PRIOR days under \$20M (no market caps; day D's own volume never enters). Windows: `pre` = 30 min
before acceptance, `post` = 30 min after, `placebo` = the 30 min an hour before. Tables: `data/news/pilot/report.md`
and `logs/news_edgar_tape_pilot_followups.log`.

### 1. Filings do move small names — but half the time the tape moved first

Small names, outside regular hours (157,596 events):

| form | n | post ≥ 10% | pre ≥ 10% | placebo ≥ 10% |
|---|---|---|---|---|
| 8-K | 93,404 | 3.6% | 3.0% | 0.8% |
| 6-K | 42,312 | 2.7% | 2.3% | 1.0% |
| 424B5 | 4,041 | 4.9% | 4.6% | 2.1% |
| S-1 | 1,201 | 6.5% | 1.2% | 0.8% |
| SC 13D | 526 | 6.8% | 1.0% | 0.6% |

A move after acceptance is ~4× the placebo rate, but for 8-K / 6-K the 30 min BEFORE acceptance moves almost as
often: the news was already out (the wire, or the company's earlier release). Of the 5,134 events that moved ≥ 10%
after acceptance, **45% had a flat 30 min before (|pre| < 2%) — call these EDGAR-first**. This cannot separate
EDGAR-first from a wire release in the same minute; it says EDGAR was not LATE for the market's reaction.

EDGAR-first share among the big movers, by form: SC 13D / SCHEDULE 13D 71–74%, S-1 64%, F-1 65%, 424B5 51%,
8-K 44%, 6-K 40%. 8-K items show the same ~45% almost uniformly (2.02 earnings 46%, 7.01 37%, 8.01 42%,
1.01 45%); no item is mostly EDGAR-first.

Direction is item-specific (share of the big moves that go UP): 3.01 delisting notice 15%, 2.04 triggering event
12%, 1.03 bankruptcy 18%, 2.05 restructuring 26%, 3.03 holder-rights change 30%; 5.01 change of control 70%;
2.02 earnings 44%, 1.01 agreements 55%.

### 2. Speed is not the constraint — liquidity is

Among the EDGAR-first big moves (2,291): share of the 30-min move done by +h seconds:

| | +1 s | +10 s | +60 s | +2 min | +5 min | +10 min |
|---|---|---|---|---|---|---|
| p25 | 0 | 0 | 0 | 0 | 0 | 0.02 |
| median | 0 | 0 | 0 | 0 | 0.22 | 0.50 |
| p75 | 0 | 0 | 0.14 | 0.35 | 0.71 | 0.94 |

The first print after acceptance comes a median **66 s** later (p25 26 s, p75 311 s). These names barely trade out
of hours; the move takes minutes to happen. A pipeline that reads the filing and decides in ~30 s is early,
not late — the binding constraint is how much can be filled, not latency.

### 3. Most big out-of-hours moves have no filing at all

Premarket / after-hours sessions of small common stocks (session dollar volume ≥ \$1M) whose 1-minute VWAP crossed
±X% from the reference (pre: prior regular close; post: the day's close). Window for a filing of ANY form: from the
prior 16:00 (pre) / 09:30 (post) to the onset + 5 min.

| move | session | n | filing at onset (−15 / +5 min) | filing earlier | **no filing** |
|---|---|---|---|---|---|
| ±10% | pre | 16,492 | 5.8% | 22.9% | **71.3%** |
| ±10% | post | 10,841 | 18.3% | 12.0% | **69.8%** |
| ±20% | pre | 13,092 | 5.9% | 23.9% | **70.2%** |
| ±20% | post | 6,519 | 17.5% | 13.0% | **69.5%** |

Counting only the news forms changes little (76–78% none): the 10-Q adds ~3% at onset after hours. 26–34% of the
premarket movers were already across X at the first print (news in the dark hours).

**Reading:** EDGAR explains ~30% of the big out-of-hours moves in small names, and is AT the onset for only 6%
(premarket) to 18% (after hours). The other ~70% is the wire-only gap plus moves with no news (sympathy, sector,
social media). The Benzinga PR Feed trial is what splits those two — that is the measurement to make with it.

**Caveats:** current tickers only (a symbol renamed in the window loses its earlier tape); split days are dropped by
`data/splits.csv` (refreshed by `backfill-daily` the same day, through 2026-11-19 — a split Polygon does not list can
still pose as a premarket mover); a stale last price before acceptance in an
illiquid name makes `pre` and `post` noisy; the 2% / 10% thresholds are first cuts, not tuned.

### 4. EDGAR-first is NOT tradeable on the tape or the category alone (2026-11-19)

`scripts/news/edgar_first_trade.py` → `logs/news_edgar_first_trade.log`. Universe, all known at the acceptance second:
small names, pre/post-market, flat 30 min before (122,808 events). **Control:** the same names at the same clock time
5 trading days earlier with no filing that day (`edgar_tape_pilot.py --placebo-lag 5`, 124,214).

**Rule A — follow the tape's first reaction** (at +30/60/120 s, |move| ≥ 2% / 5% → take that side; exit 5–60 min):
gross mean −1.0% … +0.2% per trade, PF 0.75–1.08; **negative in every cell after a 1% round trip**. The placebo's
first moves REVERSE hard (mean −0.6% … −3.8%, PF 0.03–0.28): a filing-driven move does not revert like noise, but it
does not continue either. Shorts made money in 2024–25 premarket (PF 2.5–2.7) and lost in 2026 — not a rule.

**Rule B — a side fixed per category** (learned on 2024, tested 2025-01 → 2026-11, enter +60 s, exit +30 min): the
directions are REAL — all 9 categories with |train t| ≥ 2 kept their sign out of sample (3.01 delisting notice
short, S-1 / F-1 / 424B5 / S-3 short, 1.01 / 3.02 / 5.07 / SC 13D/A long; PF 1.2–5.4), while the placebo means are
~0. **But the edge lives in filings nobody could trade at entry:**

| dollar volume in the first minute after acceptance (known at entry) | n | mean | PF | net of 1% |
|---|---|---|---|---|
| any | 21,968 | +0.35% | 1.62 | −0.65% |
| ≥ \$10k | 867 | −0.14% | 0.94 | −1.14% |
| ≥ \$50k | 252 | +0.05% | 1.02 | −0.95% |
| ≥ \$250k | 44 | +1.03% | 1.67 | +0.03% |

⚠ A first cut split this by the volume from +1 to +5 min — AFTER the entry — and showed PF 3.2 at ≥ \$250k: a
lookahead (a volume surge after entry goes with the move). Caught before it was reported.

**Reading:** the form / item is too coarse to trade; the drift it carries happens in names too thin to fill. What is
left on the EDGAR side is the CONTENT (offering size against market cap, discount, warrant coverage, counterparty —
the LLM fact extraction), and the time it allows: the reaction takes minutes (§2). The EDGAR live latency — the
floor of any EDGAR-driven trade — is being measured (`scripts/news/edgar_live_latency.py`, from 2026-11-19).

### Scripts

```bash
cd research && python3 scripts/news/edgar_tape_pilot.py --from 2024-01-02 --to 2026-11-16 \
    > logs/news_edgar_tape_pilot.log 2>&1; echo "exit $?"        # ~8 min, resumes by day
cd research && python3 scripts/news/edgar_tape_pilot_report.py > data/news/pilot/report.md
cd research && python3 scripts/news/edgar_tape_pilot_followups.py > logs/news_edgar_tape_pilot_followups.log 2>&1
cd research && python3 scripts/news/edgar_tape_pilot.py --from 2024-01-02 --to 2026-11-16 --placebo-lag 5 \
    --out data/news/pilot_placebo5 > logs/news_edgar_tape_pilot_placebo5.log 2>&1   # the control, ~8 min
cd research && python3 scripts/news/edgar_first_trade.py > logs/news_edgar_first_trade.log 2>&1
cd research && python3 scripts/news/edgar_live_latency.py --until 20:05 > logs/news_edgar_live_latency_<date>.log 2>&1
```
