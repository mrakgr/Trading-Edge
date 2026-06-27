# Massive Aggregate Provenance — Findings

**Goal:** know exactly how Massive constructs its daily and 1m bar aggregates from raw trades, so the
MaxFlyer intraday system stands on correctly-understood data (gap %, premarket volume, running high,
MOC). Method: empirical-first (rebuild from raw trades + diff vs Massive's published aggregates),
confirmed against Massive/Polygon docs. **Volume first, then timestamp.**

Status: **IN PROGRESS.** This doc records what is firmly established and what still needs a targeted
multi-name/multi-day verification pass. Probe day so far: AAPL 2022-01-03.

---

## Data & tools

- **Raw trades (ground truth):** `/mnt/d/trading-edge-bulk/trades/{date}.parquet`, 2022-01-03 →
  2026-05-15 (1,096 days). Schema incl. `participant_timestamp, sip_timestamp, trf_timestamp, trf_id,
  exchange, tape, conditions (UTINYINT[]), correction, size, price, sequence_number`.
- **Massive published 1m:** `data/minute_aggs/{date}.parquet` (`window_start` = epoch-ns UTC).
- **Massive published daily:** `daily_prices` / `split_adjusted_prices` in `trading.db`.
- **Authoritative condition rules:** Massive is a Polygon reseller; the same key hits
  `https://api.polygon.io/v3/reference/conditions?asset_class=stocks` → each condition has
  `type`, `data_types`, and `update_rules.consolidated.{updates_volume,updates_high_low,updates_open_close}`.
  Key is in `api_key.json` (`massive_api_key`); base-url pattern is already used in `SplitDownload.fs`.

## Docs (Massive)
- `https://massive.com/docs/llms.txt` — the machine-readable doc index (use this, the HTML pages are JS shells).
- Flat-file stock docs (`.../flat-files/stocks/{day-aggregates,minute-aggregates,trades,quotes}.md`)
  define **only the column schema**, NOT the construction rules. `window_start` = "Unix nanosecond
  timestamp for the start of the aggregate window." No prose on volume/condition/timestamp rules.
- Timestamp field definitions (verbatim from `flat-files/stocks/trades.md`):
  - `participant_timestamp` = "when the trade was actually generated at the exchange."
  - `sip_timestamp` = "when the SIP received this trade from the exchange."
  - `trf_timestamp` = "when the trade reporting facility received this trade."
  - **No doc guidance on which timestamp buckets a trade into a bar** → empirical question.

---

## Q1 — Which trades count toward VOLUME?

### Authoritative rule (from the conditions endpoint)
Filtering to `type == 'sale_condition'` (the 40 real trade conditions — this cleanly separates them
from quote/SSR/financial-status conditions that **share numeric IDs**, e.g. id 1 = "Acquisition"
[sale] vs "Regular Two-Sided Open" [quote]), the **only** sale conditions with
`updates_volume = false` are:

| id | name | updates_volume |
|---:|------|:---:|
| 15 | Market Center Official Close | **false** |
| 16 | Market Center Official Open  | **false** |
| 38 | Corrected Consolidated Close | **false** |

Every other sale condition — including odd lots (37), extended hours (12/13), average price (2),
derivatively priced (10), contingent (52/53), all the auction/opening/closing prints — has
`updates_volume = true`. **This confirms the hypothesis in `docs/trade_conditions.md`** (its
"For volume analysis: exclude 15, 16, 38" line is correct per the consolidated tape).

### BUT — Massive's published aggregate volume ≠ a naive "exclude {15,16,38}" sum
Empirical rebuild on **AAPL 2022-01-03** (all sessions, all venues incl. TRF, `sip_timestamp` bucket):

| rebuild rule | volume |
|---|---:|
| all trades, no filter | 112,512,025 |
| **exclude conditions {15,16,38}** | 104,746,136 |
| Massive **daily** (`daily_prices`) | **102,597,860** |
| Massive **minute total** (Σ `minute_aggs`) | **95,619,809** |
| lit-only (`trf_id=0`) | 56,868,611 |
| RTH-only (09:30–16:00), excl {15,16,38} | 83,518,707 |

Findings:
1. **Massive daily INCLUDES off-exchange/TRF volume** (lit-only 56.9M ≪ daily 102.6M). Not lit-only.
2. **Massive daily INCLUDES after-hours** (before-16:00 = 83.5M ≪ daily 102.6M). Not RTH-only.
3. **Excluding {15,16,38} is necessary but NOT sufficient** — a **~2.08M-share residual** remains
   (104.75M rebuilt vs 102.60M daily). `correction != 0` explains almost none of it (~69k).
4. **Daily ≠ Σ(minute bars):** daily 102.60M vs minute-sum 95.62M (~7M apart). They are **separately
   constructed products**, not one rolled up from the other. The MaxFlyer plan must NOT assume
   Σ1m == daily.

### RESIDUAL RESOLVED — Massive also excludes condition 22 (Prior Reference Price)
Attributing the AAPL residual by condition-combo: **condition 22 (Prior Reference Price) carries
exactly 2,079,610 shares**, and adding it to the exclusion set collapses the residual:

| name (2022-01-03) | residual excl {15,16,38} | residual excl **{15,16,38,22}** |
|---|---:|---:|
| AAPL | 2,148,276 | **68,666** (0.067%) |
| TSLA | 18,116 | **1,624** (0.005%) |

Cross-day: AAPL 2022-01-04 rebuild (excl {15,16,38,22}) = 99,419,379 vs Massive 99,018,709 (~0.4%).

**Massive's daily VOLUME rule = exclude sale-conditions {15, 16, 38, 22}.** Condition 22 is excluded
**despite** its consolidated `updates_volume=true` flag — so Massive's published volume is **more
aggressive than the bare consolidated-tape rule** (this is exactly the ~1.8% gap below Yahoo). The
remaining 0.067% on AAPL is a tiny secondary effect (possibly cond 10 derivatively-priced, ~154k, or a
boundary/rounding artifact) — negligible for our use.

**Late-reporting theory RULED OUT.** The residual prints (the 6.95M closing cross `[8,9,41]` and the
after-hours Form-T `[12]`/`[12,22]` TRF prints) ALL have participant_timestamp == sip_timestamp ==
trf_timestamp on the **same day** (2022-01-03, 16:00–17:20 ET). Nothing is deferred to the next day; the
exclusion is purely condition-based (cond 22), not an execution-vs-report-day booking effect.

**Odd lots (37) are INCLUDED** — removing them overshoots to −9.46M vs Massive daily. Do NOT filter odd lots.

### Where the residual lives (per-minute diff, rebuild vs `minute_aggs`)
Largest divergences cluster at the **close and after-hours**:
- **et_min 960 (16:00):** one print `conditions=[8,9,41]`, exchange 12, size **6,948,189** — the
  official **Closing Print (8)**. It's in our rebuild but the 16:00 *minute* bar shows only ~156k →
  Massive routes the closing-auction cross differently between the daily and the minute product.
- **et_min 1012 / 976 (after-hours):** prints `conditions=[12,22]` (Form-T Extended Hours + Prior
  Reference Price), exchange 4 / `trf_id=12`, sizes 1.70M / 312k — `updates_volume=true` on the
  consolidated tape, yet Massive's **minute** product nearly drops them.

**Interpretation (preliminary):** Massive's published aggregates apply filtering BEYOND the bare
consolidated `updates_volume` flag — at least different handling of the closing-auction print and of
certain late/extended-hours TRF prints — and they differ between the daily and minute products.
The exact rule for the 2.08M daily residual is **the open question for the Phase-2 verification pass**.

---

### Third-party cross-check (Yahoo Finance) — Massive filters MORE than the common reference
Independent daily volume vs Massive (note: **Yahoo/TradingView volumes are SPLIT-ADJUSTED**, so they
only compare cleanly to Massive *raw* daily for names with **no split** between the date and today):

| name (2022-01-03) | our rebuild (excl {15,16,38}) | Massive daily | Yahoo (adj) | residual (Massive vs rebuild) |
|---|---:|---:|---:|---:|
| **AAPL** (no split since 2020) | 104,746,136 | 102,597,860 | 104,487,900 | **−2,148,276 (−1.8%)** |
| **TSLA** (3:1 split Aug-2022) | 34,896,969 | 34,878,853 | 100,248,300¹ | **−18,116 (−0.05%)** |

¹ Yahoo TSLA is split-adjusted (×3); not comparable to the raw 2022 share basis. The raw rebuild and
Massive daily agree to 0.05% — so TSLA is **not** a clean Yahoo cross-check, but IS a clean
rebuild-vs-Massive check.

Findings:
- **For AAPL, Yahoo (104.49M) ≈ our naive exclude-{15,16,38} rebuild (104.75M), ~0.25% apart — and
  Massive daily (102.60M) sits ~1.8% BELOW Yahoo.** So the residual is **Massive-specific extra
  filtering**, not a standard rule everyone applies; Massive drops ~1.9M shares the common reference keeps.
- **The residual size varies by name** (AAPL −1.8% vs TSLA −0.05%) → it is tied to **specific
  excluded trades** (AAPL's gap was dominated by the single 6.9M-share closing-cross print
  `conditions=[8,9,41]`), NOT a flat percentage. TSLA on this day lacked a comparably large excluded print.
- **Caveat for future cross-checks:** use split-FREE names (or compare on an adjusted basis) when
  validating against Yahoo/TradingView. The clean unadjusted third party is **Stooq** (retry — first
  fetch returned empty) or Nasdaq/SIP official.

## Q2 — Which timestamp buckets a trade? (sip vs participant) — ANSWERED: **sip_timestamp**

Rebuilt per-minute volume under each clock (volume set {excl 15,16,38,22}) and diffed vs `minute_aggs`:

| timestamp | mismatched minutes (full day) | mismatched minutes (clean RTH 09:31–15:59) |
|---|---:|---:|
| **sip** | **110** | **14 / 389** (AAPL) · **4 / 389** (TSLA) |
| participant | 380 | 207 (AAPL) · 247 (TSLA) |

**`sip_timestamp` is decisively Massive's bucketing clock** — participant misplaces ~15–60× more
minutes on both names. (The SIP receive-time, not the exchange generation-time, assigns a trade to its
minute.) Confirms the user's prior; it's one clock, not a mix.

The 14 leftover RTH-minute mismatches on AAPL = the same ~0.07% secondary residual as the daily
leftover (sip RTH abs-diff = 68,666 = the daily residual exactly). NOT a timestamp effect.

## Q1 epilogue — the last ~0.07% is NOT a clean per-condition rule
The 68,666 daily / 14-minute leftover traces to a few specific block prints, chiefly two
`conditions=[53,41]` (Qualified Contingent Trade) blocks of 38,000 and 19,000. **But excluding
condition 53 OVERSHOOTS** (daily → −1.12M under Massive; RTH mismatches jump 14 → 161), because the
*smaller* `[53,...]` prints ARE counted. So 53 is excluded only contextually (large QCT blocks?), not
uniformly — likely a block-trade / late-correction nuance, not a simple condition flag. **Stop here:**
the rule {exclude 15,16,38,22} already reconstructs daily to **99.93%** and clean-RTH minutes to
**14/389**, which is more than enough for MaxFlyer. Chasing the last 0.07% would overfit two block
prints on one day.

## consolidated vs market_center update rules (the two-tier flag system)
Each condition's `update_rules` has TWO sub-objects: `consolidated` and `market_center`. Source:
Massive blog "Understanding Trade Eligibility" (https://massive.com/blog/understanding-trade-eligibility):
- **`consolidated`** = eligibility for **total-market / SIP-tape** aggregates ("If your goal is to
  create aggregates representing total market volume, follow the Consolidated Processing Guidelines").
  ⇒ **This is the one WE use** (we want whole-market volume).
- **`market_center`** = eligibility for **per-exchange** aggregates (each venue's own official open/close).

Comparing all 40 sale_conditions, the two tiers differ on **only 3 conditions, and ONLY in the
high/low & open/close flags — NEVER in volume**:

| id | name | consolidated v/hl/oc | market_center v/hl/oc |
|---:|------|:---:|:---:|
| 15 | Market Center Official Close | F/F/F | F/**T/T** |
| 16 | Market Center Official Open  | F/F/F | F/**T**/F |
| 38 | Corrected Consolidated Close | F/**T/T** | F/F/F |

So **the consolidated-vs-market_center distinction does NOT affect volume** — both agree the only
no-volume sale-conditions are {15, 16, 38}. It therefore does **not** explain Massive's exclusion of
condition 22 from aggregate volume (22 = volume-`true` in BOTH tiers). **Condition 22's exclusion is a
Massive aggregate-specific rule beyond the published flags** (confirmed empirically; not a flag artifact).

The blog also notes a multi-condition precedence rule — *"if a trade record includes multiple sale
conditions and any one indicates 'no', that 'no' takes precedence."* This matters for OHLC eligibility
on multi-condition trades (evaluate the array, not one condition), but does NOT rescue the 22 mystery
since no condition in the 22-bearing arrays is volume-`false`.

## Decision (2026-06-27): trust Massive's DAILY as-is; reconstruct the MINUTE product ourselves
Massive support (chatbot only — no engineering reply) went in circles on condition 22 and never
confirmed the timestamp. Its claims contradicted our reproducible measurements (it insisted daily
*keeps* 22; our exact rebuild shows daily *excludes* it). We stop trying to get prose confirmation:
**we take Massive's daily aggregates at face value** (we don't rebuild daily), and we focus effort on
**reconstructing the 1m `minute_aggs` product** from raw trades, since that's what MaxFlyer reads
bar-by-bar. The minute reconstruction is verified empirically below, independent of support.

## MINUTE-BAR construction rule — RECONSTRUCTED (verified AAPL + TSLA, 2022-01-03)

Massive's 1m `minute_aggs` is built by a rule **distinct from** the daily product:

**Bar membership:** a minute bar is emitted **iff that minute contains ≥1 ROUND-LOT (non-odd-lot)
trade.** Minutes whose only trades are odd lots (condition 37) get **no bar at all** (this is why our
naive rebuild had 94 phantom premarket odd-lot-only minutes; removing them gave exactly Massive's 865
minutes for AAPL / 893 for TSLA).

**Volume within a bar:** Σ size of all volume-eligible trades, **INCLUDING odd lots** — excluding only
conditions **{15, 16, 38, 22}** (same volume-exclusion set as daily). (Removing odd lots from the
*volume* overshoots negative — Massive counts odd-lot shares in bars that exist; odd-lots only affect
bar *existence*, not the volume sum.)

**Bucketing clock:** `sip_timestamp`.

Verification under this exact rule (exclude {15,16,38,22} from volume; emit a minute only if it has a
non-37 trade; sip clock):

| name | bar count match | mismatched minutes | abs-diff excl. open/close auctions |
|---|:---:|---:|---:|
| AAPL | 865 == 865 ✓ | 16 | **68,666** (~0.07%) |
| TSLA | 893 == 893 ✓ | 6 | **1,632** (~0.005%) |

**Remaining mismatches** fall into three small, explainable buckets (none structural):
1. **et_min 960 (close, +6.95M on AAPL):** the **closing-auction cross** (`[8,9,41]`, cond 8) is
   **excluded from the 16:00 minute bar** — it's in the daily total but not the minute product. The
   single largest special case.
2. **et_min 570 (open auction, +14k):** small opening-auction handling diff.
3. **A few QCT/ISW block prints** (630 `[53,41]` +38k, 616 +19k, …): the same contextual condition-53
   nuance as the daily 0.07% residual — not a clean flag rule, negligible.

**For MaxFlyer:** this reconstructs the minute volume essentially exactly. The only material caveat is
that **the closing-auction cross is not in the 16:00 minute bar** — relevant if the engine ever reads
16:00 minute volume as "the close"; the MOC exit uses the close *price*, which is unaffected.

## RESOLVED RULES (for the engine)
- **Daily/minute VOLUME** = Σ size over sale-trades **excluding conditions {15, 16, 38, 22}**
  (markers 15/16, correction 38, Prior-Reference-Price 22). Odd lots (37), extended-hours (12/13),
  average-price (2), derivatively-priced (10) ARE included. Reconstructs Massive daily to ~99.93%.
- **Bucketing clock = `sip_timestamp`** (epoch ns UTC → ET minute).
- **Venue:** all venues, incl. off-exchange/TRF (`trf_id != 0`, exchange 4). Daily includes after-hours.
- **Daily ≠ Σ(minute bars)** — separately constructed; the closing-auction cross (cond 8) and some AH
  TRF prints are placed differently in the minute product. For premarket-volume / gap, rebuild from the
  rule above rather than summing `minute_aggs`.

---

## What's established vs open

**Established:**
- Volume excludes (consolidated tape) exactly sale-conditions {15, 16, 38} — confirms `trade_conditions.md`.
- Massive daily includes TRF/off-exchange AND after-hours volume.
- `trf_id = 0` ⇒ lit-exchange trade (real venue in `exchange`); `trf_id != 0` ⇒ off-exchange/TRF print
  (exchange = 4, the FINRA facility).
- **Daily and minute are separately constructed** (daily ≠ Σ minute).

**Open (Phase-2 verification targets):**
1. The ~2% daily residual beyond excluding {15,16,38}: identify the exact extra rule (closing-print
   routing? specific extended-hours/TRF condition combos? a size/lot rule?). Attribute it by
   condition-combo across multiple names/days, not just AAPL.
2. Minute-vs-daily divergence: characterize how the closing auction (cond 8) and AH TRF prints (12,22)
   are placed in each product.
3. Q2 timestamp (sip vs participant) — after Q1.
4. Confirm all of the above on the Phase-1 panel (mega-cap, TRF-heavy small-cap, ETF, a split day,
   ≥3 dates) — single-name AAPL results could be idiosyncratic.

## Reusable probes (to formalize as scripts)
- conditions pull: `curl .../v3/reference/conditions` + filter `type=sale_condition`, read
  `update_rules.consolidated.updates_volume`.
- daily rebuild + residual attribution: `SUM(size)` excluding condition set, grouped by
  condition-combo / correction / session, diffed vs `daily_prices`.
- per-minute diff: rebuild minute volume (chosen ts) vs `minute_aggs`, order by `ABS(diff)`.
(Formalize as `scripts/equity/agg_rebuild_diff.py`, `agg_condition_attribution.sql`,
`agg_edge_probes.sql` per the plan.)
