# Price adjustment — splits and dividends

**Status:** `daily_adjusted` built and validated 2026-08-12 (S43br).
`split_adjusted_prices` still exists and is still what every engine reads;
migration is in progress. Do not mix the two in one calculation.

> 🛑 **INTRADAY ONLY.** Roughly **1 split in 20** is wrong in the source data and
> we have deliberately chosen not to correct it — safe here because an intraday
> hold cannot straddle a split (contamination: **2 trips of 35,782**), but **not**
> safe for anything held overnight or using long lookbacks. See §6 before reusing
> this table in a swing or position system.

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
in `02_daily_adjusted.sql`.

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

## 6. Known residual: the vendor contradicts itself

**343 of 6,241 split dates (5.5%) are asserted by Polygon's reference data but
show no corresponding move in Polygon's own flat-file tape.** This is *not* our
bug, and it damages `split_adjusted_prices` too. Established 2026-08-12:

- `daily_prices` matches the original S3 CSVs **exactly**;
- local `splits` matches `/v3/reference/splits` for 172 of 188 sampled;
- **not** symbol reuse (0 of 174 flagged tickers have multiple FIGIs);
- **not** a low-price artifact — contradicted splits have **median price $16.20**
  and 76% are $5+, while penny stocks are the *most* reliable (0.6% contradicted
  under 10¢ vs 12.3% at $5–20);
- **not** a fund artifact — ETFs are under-represented (4.7% vs 34.7% baseline).

Two mechanisms are identified:

- **Date misalignment (~32 cases).** The split is real, the date is off — usually
  by exactly **+1 trading day**. `IAU`'s 10:1 shows in the tape on **2010-06-24**
  (121.06 → 12.14, an exact 10× divide) but `splits` records **2010-06-17**.
- **Spinoff offset.** `EXPE` 2011-12-21 is a *genuine* 1:2 reverse whose price
  effect was cancelled by the simultaneous TripAdvisor spinoff. Spinoffs are not
  modelled by either scheme.

After the §5 fix the **worst remaining contradicted ratio is 165×** (was
32,000×), and `HON`'s phantom 2:1 reverse at $232 — a stale row Polygon no longer
publishes — is gone.

### 🛑 DECISION (user, 2026-08-12): accept the errors — INTRADAY ONLY

**Roughly 1 split in 20 is wrong, and we are knowingly living with it.** For an
intraday system that is defensible; **for anything holding overnight or longer it
is not, and those splits would need manual intervention.** Read this section
before reusing `daily_adjusted` in a swing or position system.

**Why it is safe for us.** FlushFader enters and exits inside one session, so no
split or dividend can fall *inside* a holding window. `n` and `cum_div` are
constant within a day, so they cancel out of every intraday return exactly —
a wrong `n` cannot move an intraday P&L at all.

The residual exposure is only via the **cross-day context columns**
(`close_m1/m3/m7`, `close_p1/p3/p5`, `open_p1`), and because splits are rare
events while those windows are days rather than years, it is minute:

| | count |
|---|---:|
| contradicted split dates | **343** of 6,241 (5.5%) |
| universe ticker-days with one in `[D−7, D+5]` | **76** of 1,431,802 (**0.005%**) |
| **trips in the traded book within ±7 days of one** | **2** of 35,782 |

So the headline 5.5% is a rate *per split event*; our actual contamination is
**two trips**. Nothing is worth building on top of that.

**Why a longer-horizon system cannot make the same call.** The exposure scales
with how much calendar a calculation touches:

- a multi-day or multi-week **hold** can straddle a split date, so a wrong `n`
  lands directly in the trade's return rather than cancelling;
- long **lookbacks** (52-week channels, multi-year ATR, drawdown series) sweep up
  many split dates each, so the 5.5% per-event rate compounds toward "most long
  histories contain at least one";
- under back-adjustment the damage is *unbounded backwards* — one bad ratio
  rewrites the entire prior history (TTSH → $0.002147).

For such a system the fix is not automatic: either adopt the corroboration test
below and accept that it rejects genuine splits in spinoff cases, or curate the
343 by hand against a second vendor. **Do not assume this table is clean for
multi-day work simply because it validates clean for intraday.**

### The corroboration test (designed, NOT adopted)

Apply a split only when it *reduces* the implied day's move —
`|ln(m·ratio)| < |ln(m)|` where `m = close(t)/close(t−1)`. It is principled (a
split factor's only job is cross-event comparability, so if the tape is already
continuous, applying the factor *creates* a discontinuity), symmetric, and has no
tuned threshold. It accepts AAPL 4:1 (0.033 < 1.353) and rejects TTSH
(7.986 > 0.020), keeping 5,898 and rejecting 343.

It is **not enabled**: it would reject genuine splits wherever a spinoff offset
the price move (EXPE 2011-12-21), which is the right trade for a price-ratio
backtest but wrong if `n` must track true share count. Pending a decision, we
apply every split as recorded, and
`scripts/equity/validate_daily_adjusted.py` reports the count as an inventory
with a regression baseline so it cannot grow silently.

## 7. Files

| file | role |
|---|---|
| `TradingEdge.Database/sql/schema/materialized/02_daily_adjusted.sql` | builds the table; auto-registers via the folder glob (runs after `01_`) |
| `TradingEdge.Database/sql/schema/tables/{splits,dividends}.sql` | `PRIMARY KEY(id)` + the rationale |
| `TradingEdge.Massive/{SplitDownload,DividendDownload}.fs` | capture Polygon's `id`; `VendorId.require` throws if absent |
| `TradingEdge.Massive/Types.fs` | `Split` / `Dividend` records |
| `scripts/equity/validate_daily_adjusted.py` | acceptance tests — run after any rebuild |
| `docs/flushfader_results.md` §S43bq | the overnight study that exposed the dividend bug |

**Rebuild:** `dotnet run --project TradingEdge.Database -c Release -- ingest-data`
then `python scripts/equity/validate_daily_adjusted.py` (exit 0 = clean).

⚠ Every acceptance test compares the table's implied return against an
**independent raw calculation**, so a bug in the builder cannot hide behind its
own arithmetic. Keep it that way when adding cases.
