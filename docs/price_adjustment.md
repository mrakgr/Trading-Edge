# Price adjustment — splits and dividends

**Status:** `daily_adjusted` built and validated 2026-08-12 (S43br). **FlushFader
is migrated** — engine, universe (`mr_candidate_1s_v2`) and analysis tools all run
on the causal scheme, and the control passed (see §8). `split_adjusted_prices`
still exists for the other systems. Do not mix the two in one calculation.

> ✅ **Split errors are CORRECTED** (S43bs, 2026-08-12). Roughly 1 split date in 20
> was contradicted by the price tape; `split_corrections` now resolves them
> automatically on every materialize. **Zero splits with a diagnostic ratio
> (≥ 1.25×) remain contradicted.** This table is no longer intraday-only — see §6
> for what remains and why it is not damage.

---

## 1. The short version

A price series is not directly comparable across a split or a dividend, so every
backtest needs *some* adjustment. There are two ways to do it, and the industry
default is the wrong one for us:

| | back-adjustment (charting default) | **forward adjustment (ours)** |
|---|---|---|
| anchor | **today** — the right edge is truth, the past is bent | **the first bar** — the past is truth, later bars carry the events |
| a value at time `t` depends on | every split/dividend **after** `t` | only events **up to** `t` |
| causal? | ❌ **no — it is future information** | ✅ **yes** |
| dividend factor | `1 − div/price` → divides by price | `+ cash` → pure addition |
| blows up when | a dividend approaches the price | never |

Charting applications back-adjust because they need one continuous line whose
right edge equals today's quoted price. **A backtest has the opposite
requirement:** the left edge is where you enter, and nothing after your entry may
touch the numbers you traded on. Same data, opposite anchor.

## 2. The scheme

Everything is defined per **one share held from the ticker's first row** in the
table. Three columns in `daily_adjusted`, all causal:

| symbol | column | meaning |
|---|---|---|
| `P(t)` | `open/high/low/close` | the raw price the tape printed — untouched |
| `n(t)` | `n` | Π `split_ratio` over `execution_date ≤ t` — how many shares your one share has become |
| `C(t)` | `cum_div` | Σ `cash_amount × n(ex−1)` over `ex_date ≤ t` — cash received per original share |

**Both events land at the OPEN of day `t`;** the last pre-event trading is
`t−1`'s close. AAPL closed 499.23 on 2020-08-28 and opened 127.58 on 2020-08-31
(4:1), so `n(t)` *includes* the split dated `t`. VISN closed 19.53 on 2026-04-27
and opened 9.64 on 2026-04-28 (−$10.00 special), so `C(t)` *includes* the
dividend dated `t`. This is exactly why the scheme is causal: by the time day `t`
opens, both events have already happened and are readable off the tape.

### How to use it

**Never store or gate on a fused `P·n + C` level.** Combine the components at the
point of use:

| you want | expression |
|---|---|
| an absolute price floor ($1 / $2 / $5) | **`P(t)` — raw, directly** |
| to compare prices across time (channels, MAs, gap %) | `P(t)·n(t)` |
| **the return of a position held t1 → t2** | `[ (P2·n2 − P1·n1) + (C2 − C1) ] / (P1·n1)` |
| a prior/forward value expressed in day D's raw scale | `P(t) · n(t)/n(D)` |
| a total-return index (research only) | `P(t)·n(t) + C(t)` |

The sanity property that back-adjustment never had: **with no split and no
dividend inside the window, the return collapses to `(P2 − P1)/P1`** — the plain
raw return. That is true of nearly every FlushFader trade.

## 3. Why the old approach was wrong

### 3a. Back-adjustment is a lookahead

`split_adjusted_prices` stores `adj_close`, and the derived
`adj_ratio = adj_close/raw_close` folds in **every split after day D**. That is
future information sitting in a column that reads like plumbing.

This is not hypothetical. It is the entire reason `CLAUDE.md` rule 4 exists, and
on 2026-08-04 a `chg_1d` filter multiplied by `adj_ratio` a second time and
became a **future-reverse-split detector**: 43.6% of the book had
`adj_ratio ≠ 1` (p90 370, max 6e7) and **71.4% of the resulting "S-tier" book was
artifact** (§S43v). The same class had already killed three systems in July 2026.

Worse, it contaminates *before the fact*: a split announced for 2026-09-11 is
already in Polygon's reference data today, so under back-adjustment it is already
dividing every current price. Under forward adjustment an unexecuted split
matches nothing and changes nothing.

**Under the new scheme this bug class is structurally impossible, not merely
audited for.**

### 3b. The dividend factor divided by price

The 2026-06-18 rewrite replaced a subtractive dividend adjustment
(`price − Σdiv`, which drove 209,701 rows non-positive) with a multiplicative one.
Correct in form — but it used the wrong price:

```sql
f = 1 - adj_div / close_ON_the_ex_date      -- WRONG: already ex-dividend
f = 1 - adj_div / close_on_the_LAST_DAY_BEFORE_ex   -- correct (cum-dividend)
```

The denominator is short by exactly the dividend, so it explodes whenever a
dividend exceeds ~half the price. **VISN** paid a **$10.00 special** on a **$19.53**
close (ex 2026-04-28, opened $9.90): `f = 1 − 10/9.90 = −0.0101`, which hit the
`GREATEST(…, 1e-6)` clamp and multiplied VISN's entire prior history by 1e-6 —
`adj_close = $0.000020` against a raw $19.53. **92,749 rows across 72 tickers.**
The correct factor is `1 − 10/19.53 = 0.488`.

⚠ The clamp's own comment called `div ≥ price` *"an economic absurdity / bad
data"*. It is neither — it is the **expected output of the wrong denominator**.
A defensive guard that fires on real data is a bug report, not a guard.

Additively, `raw + 10` handles this with no clamp, no sign flip, and no special
case. **The failure mode cannot exist.**

### 3c. Why we do NOT fuse the components

The obvious formulation — one column, `adj = P·n + C` — is wrong in a subtle way,
and it is worth being precise because it looks so reasonable.

`P·n + C` is total wealth: shares plus the dividends in the mattress. But a trade
opened at `t1` buys **only the shares**. Its return is

```
ret = [ (P2·n2 − P1·n1) + (C2 − C1) ] / (P1·n1)
```

whereas the fused ratio `adj(t2)/adj(t1) − 1` has the **identical numerator** but
divides by `P1·n1 + C1`. It charges the trade for cash it never invested.

Worked example — a $100 stock that has paid $50 of dividends over 20 years, no
splits. You buy at $100, it goes to $110, no dividend during your trade:

| | calculation | answer |
|---|---|---|
| truth | in $100, out $110 | **+10%** |
| fused | `(110+50)/(100+50) − 1` | **+6.7%** ❌ |
| components | `(110 − 100 + 0)/100` | **+10%** ✅ |

This is measurable in our own data: AAPL's 2020-08-31 split reads **+3.27%** on
the fused level against a true **+3.39%**, the gap being the `cum_div = 269.9`
sitting on both sides. **67% of universe ticker-days are on names with dividend
history**, so this is not an edge case.

It also degenerates numerically: `n` spans **3.7e-18 → 1.1e7** across this table
(serial reverse-splitting shells), so `P·n` can be ~1e-17 while `C` is dollars —
the cash term swallows the price entirely.

⚠ Corollary: **the absolute level of `n` is arbitrary per ticker** (splits
predating the first price row are included). Only *ratios* of `n` mean anything —
which is all any expression in §2 uses. Do not eyeball `P·n` as a price.

## 4. The `n(ex−1)` rule

**A dividend is scaled by the share count at the last cum-dividend close, not on
the ex-date.** Entitlement is fixed at the `t−1` close, *before* any split dated
`ex` lands, so `cash_amount` is quoted per **pre-split** share.

This matters because `splits.execution_date = dividends.ex_dividend_date`
collides on **3,171 rows across ~1,000 tickers** — scrip/stock dividends paid
alongside a cash dividend, mostly foreign ADRs (ratios 1.005–1.05). Worked
example, **SCCO 2026-08-11** (ratio 1.012 + $1.10 cash): 100 shares held at the
08-10 close pay **$110 and become 101.2 shares**. Using `n(ex)` would understate
the cash.

`n(ex−1) = n(ex)` whenever there is no collision, so this is **one uniform rule**,
not a special case. It is implemented by the strict `>` in the dividend ASOF join
in `03_daily_adjusted.sql`.

## 5. The ingest keys (fixed 2026-08-12)

Both reference tables were keyed on `(ticker, <event date>)` and upserted with
`ON CONFLICT … DO UPDATE`. That key **cannot physically hold more than one
corporate action per ticker per day**, so the second record overwrote the first.

**Splits.** A reverse/forward **pair** is the standard odd-lot squeeze-out — force
out small holders, then deregister — and nets to *no change in share count*. We
kept one leg and were left with a naked ratio:

| ticker | date | Polygon has | we stored | effect |
|---|---|---|---|---|
| TTSH | 2025-12-16 | `3000:1` + `1:3000` (net **1.0**) | `1:3000` | `n` ×3000 forever |
| PMD | 2024-12-04 | `5000:1` + `1:5000` (net **1.0**) | `1:5000` | `n` ×5000 |
| RELV, SFE, BTX, MDRR, STCN | — | pairs | one leg | same |

TTSH is the clean illustration: price $6.43 with 223k shares and 2,304 trades —
a normal, liquid stock whose whole history spans $2.77–$8.69. A real 1:3000 split
would have put it near $19,000. Under **back**-adjustment the same defect divided
TTSH's *entire prior history* down to **$0.002147**.

**Dividends.** One ex-date routinely carries several payments — a regular plus a
special (the Dec-2012 tax-change wave; AAON paid two $0.12 distributions), or ADRs
paying multiple components (ABEV 2025-12-22 = `[0.0145, 0.0833]`).

**Fix: `PRIMARY KEY(id)` on Polygon's own stable record id**, with an index on
`(ticker, <event date>)` for the natural query path.

⚠ **Why not an auto-increment surrogate.** This key's job is *deduplication on
re-ingest* — deciding whether an incoming record is the same event as a stored
one — not joining. Nothing joins on it. A counter assigns a fresh value every
download, so `ON CONFLICT` would never fire and each backfill would append
another full copy of the table. Making the ingest delete-then-insert instead
would turn the existing narrow-range footgun (*"REWRITES splits.csv from ONLY the
rows in [startDate, endDate]"*) into outright loss of the database.

**Recovered:** splits 27,833 → **28,007** (+174); dividends 1,982,531 →
**2,043,178** (+60,647, **3.1%**).

## 6. The vendor contradicts itself — and what we do about it

**Polygon's splits reference data and Polygon's own flat-file tape disagree on
~5.5% of split dates.** Both of our copies are faithful — `daily_prices` matches
the S3 CSVs exactly and `splits` matches `/v3/reference/splits`. Established
2026-08-12 (S43bs), it is:

- **not** ours — ingest fidelity is exact;
- **not** symbol reuse — 0 of 174 flagged tickers have multiple FIGIs;
- **not** a low-price artifact — contradicted splits have **median price $16.20**
  and 76% are $5+, while sub-10¢ splits are the *most* reliable (0.6% contradicted
  vs 12.3% at $5–20);
- **not** a fund artifact — ETFs are under-represented (4.7% vs 34.7% baseline).

A split ratio makes a **testable prediction**: the day's move should be explained
by it. Where it is not, applying the ratio *creates* a discontinuity the market
never had.

### `split_corrections` — computed, never curated

`sql/schema/materialized/02_split_corrections.sql` derives one row per
contradicted split date from `splits` + `daily_prices`, on **every materialize**.

⭐ It is **recomputed, not hand-maintained**, and that is deliberate: a curated
overlay *rots*. If Polygon later fixes a record upstream, a stale `SHIFT` would
relocate a now-correct date, and the only guard would be remembering to re-run a
diff. A derived table cannot go stale against its own inputs.

`splits` itself is **never edited** — it stays a faithful vendor mirror so we can
always re-derive.

| outcome | n | what it means |
|---|---:|---|
| **SHIFT** | 30 | The split is real; the recorded date is not. `execution_date` moves to the date the tape actually jumps. |
| **REJECT** | 141 | No day within ±40 trading days is explained by the ratio (or only loosely, residual ≥ 5%). The split is dropped. |
| *(no row)* | 172 | Ratio < 1.25× — **not errors**, see below. |

**⭐ Exactness, not proximity, is the evidence.** The far-offset cases are the
*most* exact, on large stable names: FLIC residual 0.0008, BMI 0.0027 ($66), HEI
0.0030 ($94), HBI 0.0103 ($114), JEF 0.0121 — at offsets of 11–40 trading days. A
2:1 landing on 0.5014 thirteen days out is not coincidence. The mechanism shows in
the pattern — HEI recorded 2018-01-02, tape jump 2018-01-18; CGNX 2017-11-16 →
2017-12-04 — **Polygon logged an announcement/declaration date instead of the
ex-date.** Worked check: IAU's 10:1 is recorded 2010-06-17 but the tape divides by
exactly 10 on **2010-06-24** (121.06 → 12.14).

**REJECT, not repair.** Across those dates the tape is internally *self-consistent*
— no discontinuity anywhere in the window — so dropping the split preserves that,
while applying would manufacture a jump. Repairing the prices instead would need an
external source of truth we do not have.

Two clauses in the detector are load-bearing and easy to get wrong:

- **The candidate day must be a genuine discontinuity** (`|ln m| ≥ ½|ln r|`).
  Without it, any ordinary 1% day "matches" a 1.01 ratio; an early pass lacking it
  reported 60.6% "found" that was almost entirely noise.
- **The candidate day must not already carry a split of its own.** Otherwise its
  move is double-counted. FSBK has two 3-for-2 splits — 2004-03-22 (phantom, tape
  +1.0%) and 2004-04-26 (real, 37.01 → 24.85) — and without this clause the phantom
  shifted onto the real one, making the date's product 1.5 × 1.5 = **2.25×** against
  a tape showing 1.5×.

### The 172 that remain are scrip dividends, not damage

Ratios under 1.25× are **not corrected**. 49.4% of them are co-dated with a
dividend against a **10.7% baseline** (4.6× enriched) and their ratios cluster at
1.01–1.05 — the signature of **scrip / stock dividends**, which genuinely change
share count. A one-day test cannot resolve a 3% ratio against daily noise, so they
stay applied by design.

### What is left, honestly

After corrections, **zero splits with a diagnostic ratio (≥ 1.25×) are still
contradicted** — that is the acceptance test. Residual quality of the applied set,
measured as how well each split explains its own day:

| residual after applying | n | share | median price |
|---|---:|---:|---:|
| < 5% (clean) | 3,718 | 61.0% | $25.58 |
| 5–20% | 1,765 | 28.9% | $0.45 |
| 20–100% | 582 | 9.5% | $0.26 |
| 1–7× off | 34 | 0.6% | $0.15 |
| **> 7× off (reorg-class)** | **1** | 0.02% | $0.10 |

The residual is concentrated in **penny-stock reverse splits**, where a same-day
squeeze on top of the split is ordinary rather than anomalous — note how the median
price collapses as the residual grows.

⚠ **The one reorg-class case, and the limitation it exposes.** `SDRL` 2018-07-03 is
recorded as a **1-for-26,777** reverse split — Seadrill's Chapter 11 emergence,
where old equity was largely cancelled and reissued. The tape moved ×181, not
×26,777. It survives because **the corroboration test is *relative***: applying a
wildly wrong ratio is still marginally "better" than applying nothing. No
price-based test can separate "wrong ratio" from "huge real move" when both are
large, so bankruptcy reorganisations encoded as extreme splits are a known blind
spot. One case in 6,100.

### Impact

Correcting this changed **201 split dates**, touching **22 universe ticker-days**
and **0 trips** in the FlushFader book. It is correctness work that unblocks
overnight returns and future multi-day systems — not a P&L change, and it was not
expected to be one.

## 7. Files

| file | role |
|---|---|
| `.../materialized/02_split_corrections.sql` | resolves tape-contradicted splits (SHIFT / REJECT) |
| `.../materialized/03_daily_adjusted.sql` | builds the table; consumes the corrections. ⚠ The `02_`/`03_` prefixes are load-bearing — the folder is executed in NAME order |
| `TradingEdge.Database/sql/schema/tables/{splits,dividends}.sql` | `PRIMARY KEY(id)` + the rationale |
| `TradingEdge.Massive/{SplitDownload,DividendDownload}.fs` | capture Polygon's `id`; `VendorId.require` throws if absent |
| `TradingEdge.Massive/Types.fs` | `Split` / `Dividend` records |
| `scripts/equity/validate_daily_adjusted.py` | acceptance tests — run after any rebuild |
| `docs/flushfader_results.md` §S43bq | the overnight study that exposed the dividend bug |

**Rebuild:** `dotnet run --project TradingEdge.Database -c Release -- ingest-data`
then `python scripts/equity/validate_daily_adjusted.py` (exit 0 = clean).

## 8. The FlushFader control — and a silent universe deletion

Reference `v43_legtick` (adjusted tape) vs `v44_causal`, identical span and spec:
**1,318 -> 1,325 book trades, PF 4.077 -> 4.085, per-year +5.65% -> +5.68%**, with
maxDD, worst trade, win% and avg% unchanged. All 35,782 v43 trips reappear in v44
with `ret_exit` identical to 2.2e-16 and **0 lost** — v44 is a strict superset.
Indifferent and marginally better, which is what CLAUDE.md rule 6 says a genuine
system should do.

**The lookahead was real but INERT in the signal path**, because every intraday
feature is a same-day ratio (common factors cancel) or a dollar quantity (already
correct — the S29 fix divided volume by the same `adj_ratio` it multiplied price
by). The migration's value is that the bug class is now impossible, not that it
was costing P&L.

🛑 **What it did find:** `adj_volume = CAST(raw_volume * split_adj_factor AS BIGINT)`
truncates to **0** on extreme future reverse-splitters (MULN 2022-03-08:
214,891,790 shares x 1/1.35e12 -> 0). That made `avgvol20_prior = 0`, hence
`rvol_0945_honest = NULL`, hence `NULL >= 0` = NULL in the engine's candidate
filter — **876 universe ticker-days (0.061%) were being silently deleted**, all
extreme-dilution shells, exactly the class a long MR book wants. Raw DOUBLE volume
fixes it by construction: 0 NULLs in `mr_candidate_1s_v2`.

Separately, `avgvol20` was future-scaled on 134,203 ticker-days (9.37%) — AAPL
2020-08-27 recorded 155.3M against a raw 38.8M. Dormant (MinRvol0945 defaults to
0) but load-bearing if anyone had used `--min-rvol-0945`. See
`docs/flushfader_results.md` §S43br for the full write-up.

⚠ Every acceptance test compares the table's implied return against an
**independent raw calculation**, so a bug in the builder cannot hide behind its
own arithmetic. Keep it that way when adding cases.
