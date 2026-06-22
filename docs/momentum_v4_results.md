# HighFlyer — Mid-Cap Momentum v4 (Float-Feature Era)

**System name: HighFlyer** (named 2026-06-21) — long-only daily swing momentum on US common stocks / ADRs:
the tight-consolidation breakout to new 52-week highs on a volume + move spike. Engine project
`TradingEdge.MomentumV2` (legacy working name); HighFlyer is the user-facing strategy name.

v4 is **not a new engine or a new system** — it is the same `TradingEdge.MomentumV2` production system,
carried forward into a new research thread centered on a structural feature that v3 lacked: **public
float**. The v4 line opens because float turned out to be a clean, first-order edge axis (small
dollar-float < $300M at entry → PF ~2.5), worth its own document and its own filter/sizing decisions.

The full prior research record lives in [`momentum_v3_results.md`](momentum_v3_results.md) (clipped
methodology, current production spec, past-runner studies, regime filters) and
[`momentum_v2_results.md`](momentum_v2_results.md) (exit mechanics, 52w-proximity, the original
derivations). v4 carries only the **float work** and anything it forces us to re-derive.

---

## 📐 Methodology — unchanged from v3 (the clipped, cumulative standard)

Every PF / return figure is computed on each trade's **return clipped at +50% (`LEAST(ret, 0.50)`)**, loss
side untouched — PF then reflects the *typical* trade, not a lottery tail. Decisions are made on
**cumulative** views (`X < thr` / `X ≥ thr`); non-cumulative bands are diagnostic only. Era split
pre/post-2015. Production population = v3 engine defaults + breadth (lag-1 `pct_above_20` > 0.5) + heat
(`h10` < 0.25) + ≥2005, unless a section explicitly relaxes a filter (and says so).

**Current production spec (carried from v3, the engine `defaultConfig`):** entry move ∈ [10,30)%,
rvol ≥ 5 (no cap), log-ATR < 0.10, tightness < 4.5, 52w-close ≥ 0.95, price ≥ $1, ADV ≥ $100k,
close/open−1 ≥ −0.07, **max log ATR ≥ 0.04**; exit = 5-day time-stop, no price stop. Engine PF 1.922 /
5,617 trips (pre breadth+heat); filtered ~2,625 trips.

---

## The float feature

### Source & pipeline

Public float from SEC `dei:EntityPublicFloat` — the 10-K cover-page **USD market value of non-affiliate
shares** (true free float, annual, back to ~2009). Downloaded via SEC's bulk XBRL **frames API**
(`https://data.sec.gov/api/xbrl/frames/dei/EntityPublicFloat/USD/CY{yyyy}Q{n}I.json` — every company's
value for one period in a single CIK-keyed call; a 2009→2026 sweep is 70 frames in ~46s) into
`data/equity/float/float.db` (`float_sec`). Scripts: `scripts/equity/download_float.py`.

The hard part was the **ticker→CIK bridge for delisted names** (where momentum runners end up). SEC and
edgartools public ticker maps contain only currently-listed tickers (~46% of our cumulative 2005-2026
CS+ADRC universe). The rest were recovered via **Polygon ticker-details queried with a `?date=` inside
each ticker's listing window** (delisted tickers 404 on the bare endpoint, resolve with an active-day
date). Result: **12,002 / 12,380 (96.9%)** CIKs resolved into the `ticker_cik` table
(`scripts/equity/resolve_cik.py`). Float coverage: 100,164 datapoints 2009-2026, **9,124 / 12,380
(73.7%)** of the universe with ≥1 float observation.

### Conversion (the crux) — dollar float re-anchored to the entry price

`EntityPublicFloat` is a **dollar** value anchored to the close on the issuer's 2nd-fiscal-quarter-end
(`period_end`). A raw dollar float conflates company *size* with float *tightness* and is anchored to a
stale price. So we convert to a share count and re-anchor to the **entry-day price**:

```
float_usd_at_entry = float_usd × adj_close[entry] / adj_close[period_end]
```

A 1M-share float is $1M behind a $1 stock but $100M behind a $100 stock — so we bucket on
`float_usd_at_entry`, not the reported dollar value. This is **split-SAFE in adjusted space**: the split
adjustment factor cancels in the ratio (verified on SMCI across its 2024 10:1 split — raw-consistent and
adj-consistent prices both yield the same $54.2B entry float). No-lookahead via an ASOF join on
`known_date = period_end + 90d` (the 10-K filing deadline) ≤ `entry_date`.

> **Caveat for a real share count:** if you ever want the literal non-affiliate share *count* (for
> display), divide the dollar float by the **RAW** (`daily_prices.close`) period_end price, not
> `adj_close` — adjusted prices inflate the count by the cumulative split factor (SMCI 2011 reads 296M
> adj vs the true ~30M raw). The dollar-at-entry bucketing avoids this because the factor cancels.

Script: `scripts/equity/float_breakdown.sql`.

---

## ⭐ FINDING — small dollar-float (< $300M at entry) is a clean +0.5 PF edge (2026-06-22)

Population: v3 production trips (PF 1.922) + breadth + heat + ≥2005, **+50% clip**, **rvol ≥ 5** (the
production floor), **max-log-ATR floor ≥ 0.04 ON** (it is baked into the engine defaults, so these trips
already passed it). Coverage 47.5% overall is a **pre-2010 XBRL artifact** (pre-2010 = 5/659 covered;
**2013+ = 75% covered**).

Per-bucket PF by `float_usd_at_entry` (full sample, diagnostic):

| float$ at entry | n | win% | PF (clip) | post-2015 |
| --------------- | --- | ---- | --------- | --------- |
| < $50M          | 80  | 60.0 | 2.50      | 2.13      |
| $50–150M        | 224 | 59.4 | 2.29      | 1.98      |
| $150–300M       | 191 | 59.7 | 2.72      | 2.99      |
| $300–750M       | 247 | 54.3 | 1.35      | 1.49      |
| $750M–2B        | 263 | 52.1 | 1.27      | 1.09      |
| > $2B           | 241 | 54.8 | 1.49      | 1.58      |

Cumulative CEILING (the decision lens) — keep float below N:

| keep float < | trips | % covered | PF (clip) | post-2015 | 2013+ era |
| ------------ | ----- | --------- | --------- | --------- | --------- |
| < $150M      | 304   | 24.4      | 2.36      | 2.03      | 2.05      |
| < $300M      | 495   | 39.7      | **2.47**  | 2.26      | **2.32**  |
| < $750M      | 742   | 59.6      | 2.13      | 2.07      | 2.04      |
| (all covered)| 1246  | 100       | 1.87      | —         | 1.81      |
| float ≥ $750M| 504   | 40.4      | 1.37      | 1.30      | —         |

**Read:** monotone and sharp — `float < $300M at entry → PF ~2.4–2.5` vs ~1.8 covered baseline; **big
float (≥ $750M) is the drag at PF ~1.37**. NOT the lottery-tail problem: it survives the +50% clip, the
**win rate RISES** in the low buckets (57–60% vs 52% for big float), and it holds in the modern 2013+
window (PF 2.32 at < $300M). The `no-data` bucket is benign — 2013+ no-data is PF 1.88 / win 57%
(average), so a float filter wouldn't silently dump a profitable cohort.

**Status: NOT yet wired into the engine.** Strong candidate for a production filter (`float_usd_at_entry
< ~$300M`) or a sizing tilt. Open questions: (a) how a LIVE filter treats pre-2013 / no-data names;
(b) whether float subsumes the max-log-ATR floor (both proxy "small frothy name"); (c) float × rvol and
float × move interactions.

---

## ⭐ FINDING — the float edge OVERLAPS breadth/heat/rvol; it is NOT independent (2026-06-22)

To test whether float's lift is *independent* of the regime gates or partly captured by them, we
**removed breadth + heat and lowered rvol 5 → 3** (more samples; looser entries). Trips regenerated at
`--rvol-min 3` (max-log-ATR floor ≥ 0.04 still ON): **10,254 trips, engine PF 1.625**. Only ≥2005 kept;
no breadth/heat. Coverage 58.9% (no pre-2010 cliff because the population is much larger and later-skewed).

Per-bucket PF by `float_usd_at_entry`:

| float$ at entry | n | win% | PF (clip) | post-2015 |
| --------------- | ---- | ---- | --------- | --------- |
| NO DATA         | 4217 | 51.9 | 1.47      | 1.58      |
| < $50M          | 435  | 54.3 | 1.61      | 1.48      |
| $50–150M        | 954  | 54.7 | 1.59      | 1.42      |
| $150–300M       | 870  | 52.2 | 1.62      | 1.61      |
| $300–750M       | 1234 | 48.5 | 1.20      | 1.15      |
| $750M–2B        | 1118 | 49.0 | 1.17      | 1.15      |
| > $2B           | 1426 | 52.2 | 1.21      | 1.23      |

Cumulative keep float < N:

| keep float < | trips | PF (clip) | post-2015 | 2013+ |
| ------------ | ----- | --------- | --------- | ----- |
| < $150M      | 1389  | 1.59      | 1.44      | 1.55  |
| < $300M      | 2259  | **1.60**  | 1.49      | 1.59  |
| < $750M      | 3493  | 1.48      | 1.39      | 1.48  |
| (all covered)| 6037  | 1.38      | —         | —     |

**Read — float is complementary/redundant, not orthogonal.** Side-by-side with the production run:

| | rvol≥5 + breadth + heat (prod) | rvol≥3, NO breadth/heat |
| --- | --- | --- |
| float < $300M | PF **2.47** | PF **1.60** |
| covered baseline | 1.81 | 1.38 |
| **float-edge uplift** (< $300M − baseline) | **+0.66** | **+0.22** |
| float ≥ $750M (drag) | 1.37 | ~1.19 |

Two separate things happened. (1) Absolute PF fell everywhere — *expected, not about float*: rvol 5→3
plus dropping breadth/heat admits weaker entries (baseline 1.81 → 1.38). (2) The float **uplift shrank
from +0.66 to +0.22** — roughly a **third** of its production magnitude. The low-float ordering *survives*
(< $300M at 1.60 still beats ≥ $750M at ~1.19), so float is a genuine signal — but most of its standalone
power in the production run was breadth + heat + rvol acting on the **same small-frothy-breakout names**.
Strip those gates and float points the right way with far less force; the < $300M bucket (1.60) barely
clears the *full-production covered baseline* (1.81).

**Implication:** float is **not a free +0.6 PF bolt-on** for a stripped-down system — it earns its keep
largely *in conjunction with* the regime filters (they select overlapping populations). As a production
filter on top of the existing gates it is still worthwhile (the ordering is real and the production-run
uplift is large), but its incremental value over breadth+heat+rvol is modest, not additive. This also
flags that the next overlap test (float × max-log-ATR) is likely to show similar redundancy — they all
proxy "small, frothy, in-a-healthy-tape."

---

## Open experiments (v4 queue)

- Float × max-log-ATR overlap (regenerate trips at `--min-max-atr-log 0`) — expected to show similar
  redundancy per the finding above.
- Float × rvol and float × move interaction grids.
- Engine wiring of the float filter / sizing tilt.
