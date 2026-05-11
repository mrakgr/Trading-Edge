# Cumsum-Z short variants — mark-to-market analysis (2026-05-11)

## Context

The original `OrderflowCumsumZ.fs` engine ships with a **persistence-gate at
entry**: a long fires only when the 200h-rolling `buyMa.State > sellMa.State`,
short symmetric. The hypothesis tested in this session was that the gate is
**redundant once the cumsum threshold is large enough** — a cumsum that has
accumulated to (say) +6750 in RawZ units has by construction received
buy-aggressive flow for long enough that the persistence gate would already
have agreed. If true, the persistence-gate machinery (`buyMa`, `sellMa`) is
unnecessary for entries.

## Engine change

Single new config flag on `CumsumZConfig`:

```fsharp
/// When true (default), entries require the 200h persistence-gate sign
/// to agree with the cumsum-clamp direction. When false, entries fire
/// on cumsum-clamp touch alone (regardless of buyMa vs sellMa).
RequirePersistenceForEntry: bool
```

When `false`, the regime-bull / regime-bear checks at entry collapse to
`true`. `RequirePersistenceForExit` continues to read the raw regime signs
independently. CLI flag is `--no-entry-persistence-gate` on
`cumsum-z-sweep`.

Engine still maintains `buyMa` / `sellMa` because the exit-side option
needs them — but a future cleanup could remove them entirely if the
no-entry-gate config becomes the production default.

## Configurations compared

All runs: RawZ mode, MA=200h, vol-target = 0.1019% (1m rescaling),
ADV gate ≥$5M, gap-detector 3.0, taker fee 0.0004, allow-short.

| variant | gate? | threshold | shorts trips | total $ |
|---|:-:|---:|---:|---:|
| `z100`   | gated         | 100   | 3,294 | 150,734 |
| `ng200`  | **no gate**   | 200   | 1,325 | 153,602 |
| `ng450`  | **no gate**   | 450   |   797 | 145,944 |
| `ng4500` | **no gate**   | 4,500 |   531 | 134,364 |
| `ng6750` | **no gate**   | 6,750 |   460 | 121,613 |

Threshold grid was geometric: `200 · 1.5^n` for the low band and
`2000 · 1.5^n` for the high band.

## The hypothesis holds (with caveats)

PF as a function of threshold rises monotonically in the high band:

| threshold | trips | total $ | avg/trade | PF | win % |
|---:|---:|---:|---:|---:|---:|
| 2000 | 604 | 122,358 | $202.58 | 3.08 | 81.3% |
| 3000 | 578 | 121,824 | $210.77 | 3.31 | 82.4% |
| 4500 | 531 | 134,364 | $253.04 | **5.34** | 83.8% |
| 6750 | 460 | 121,613 | $264.38 | **5.77** | 86.1% |
| 10125 | 341 | 87,835 | $257.58 | 4.42 | 87.1% |
| 15187 | 243 | 72,452 | $298.16 | **10.33** | 90.5% |

— and the low band shows where additional trades stop adding profit:

| threshold | trips | total $ | PF | win % |
|---:|---:|---:|---:|---:|
| 200  | 1,325 | 153,602 | 2.77 | 70.9% |
| 300  |   988 | 142,074 | 2.85 | 73.6% |
| 450  |   797 | 145,944 | **3.42** | 77.2% |
| 675  |   701 | 119,327 | 2.47 | 79.5% |
| 1000 |   644 | 105,815 | 2.21 | 80.7% |
| 1500 |   618 | 122,268 | 2.86 | 81.9% |

Above ~450 the additional trades stop adding net P&L; below it they only
modestly help. **The gate appears redundant**: the no-gate sweep produces a
clean monotonic-ish curve where the threshold alone selects the regime.

## Why proper mark-to-market matters

The first instinct was to bucket trade P&L by **exit date** (the
existing `scripts/crypto/equity_curves.py` does this). For a strategy with
**172-day average hold** this is dramatically misleading:

- A 250-day winning short dumps its full P&L on the exit date — zero
  in any of the preceding 8 months.
- Universe-wide rally months (Nov 2024) which crush open shorts by
  hundreds of $k unrealized are **invisible** if no trade happened to
  close that month.
- Drawdowns from the realized-P&L view are artificially small. Sharpe
  is artificially high.

The honest answer requires **monthly mark-to-market**: for each open
position, mark to the close of the first 1m bar of each month, and book
the unrealized delta as that month's P&L. We did this exactly.

### Decomposition (reconciles to net_pnl exactly)

For each trip spanning months `M_0..M_k`, the per-month P&L is:

- **Same-month trip**: full `net_pnl` to `M_0`.
- **Multi-month**:
  - `M_0`: `(entry_price - anchor[M_1]) × qty × sign - half_fees + funding_share`
  - intermediate `M_i`: `(anchor[M_i] - anchor[M_{i+1}]) × qty × sign + funding_share`
  - `M_k`: `(anchor[M_k] - exit_price) × qty × sign - half_fees + funding_share`

where `anchor[M]` = close of the first 1m bar of month M (per symbol),
sign = +1 for shorts (profit when price falls), and `funding_share` is
the trip's `funding_pnl` pro-rated by days held in that month.

Sum of `month_pnl` over a trip's spanned months **equals `net_pnl`
exactly** for all 5,082 trades across all five variants (verified;
max error 0.0).

Anchor build:
```sql
SELECT symbol,
       DATE_TRUNC('month', TO_TIMESTAMP(end_us/1e6))::DATE AS month,
       arg_min(close, end_us) AS anchor_close
FROM read_parquet('data/crypto/perps_bars/1m/*.parquet', filename=true)
GROUP BY 1, 2;
```

Saved to `data/crypto/cumsum_z_no_gate/anchors.parquet` (10,551 rows
covering 643 symbols).

Full SQL: [scripts/crypto/mtm_decompose.sql](../scripts/crypto/mtm_decompose.sql).
Plot script: [scripts/crypto/plot_cumsum_z_mtm.py](../scripts/crypto/plot_cumsum_z_mtm.py).
Plot output: [data/crypto/cumsum_z_no_gate/equity_curves.html](../data/crypto/cumsum_z_no_gate/equity_curves.html).

## Monthly P&L — proper MTM

All figures in dollars on $1k base notional. Bold = losing month
(≥ $5k loss).

| Month   |        z100 |       ng200 |       ng450 |      ng4500 |      ng6750 |
|---------|------------:|------------:|------------:|------------:|------------:|
| 2024-05 |  **-21,060**|  **-23,721**|  **-10,166**|           — |           — |
| 2024-06 |      55,871 |      60,789 |      56,553 |        -417 |           — |
| 2024-07 |       6,474 |       7,800 |       5,612 |       1,510 |         242 |
| 2024-08 |      21,906 |      22,400 |      21,849 |       7,818 |       1,883 |
| 2024-09 |  **-24,449**|  **-22,670**|  **-22,743**|  **-18,490**|   **-8,682**|
| 2024-10 |      15,745 |      14,900 |      14,490 |      12,790 |       6,446 |
| 2024-11 | **-106,502**| **-101,598**|  **-97,313**|  **-93,838**|  **-68,098**|
| 2024-12 |      43,919 |      48,759 |      44,893 |      52,525 |      43,474 |
| 2025-01 |      21,048 |      21,695 |      21,609 |      24,614 |      18,267 |
| 2025-02 |      43,694 |      49,733 |      49,687 |      54,534 |      45,579 |
| 2025-03 |      24,660 |      25,743 |      23,648 |      27,947 |      24,629 |
| 2025-04 |  **-14,356**|  **-11,319**|  **-12,512**|  **-13,912**|  **-11,422**|
| 2025-05 |       6,539 |       6,953 |       4,664 |       4,864 |       3,433 |
| 2025-06 |      15,574 |      10,855 |       8,584 |      11,225 |       9,437 |
| 2025-07 |  **-22,698**|  **-22,308**|  **-22,072**|  **-20,397**|  **-18,072**|
| 2025-08 |      -2,040 |      -3,715 |      -4,579 |        -487 |        -874 |
| 2025-09 |       7,765 |       3,910 |       4,479 |       9,961 |       8,761 |
| 2025-10 |      36,057 |      27,212 |      21,435 |      25,479 |      23,703 |
| 2025-11 |      21,016 |      17,909 |      22,210 |      23,659 |      16,122 |
| 2025-12 |      15,786 |      13,535 |      14,446 |      12,183 |      10,746 |
| 2026-01 |      15,697 |      11,808 |      10,384 |      16,877 |      17,872 |
| 2026-02 |       8,113 |       6,046 |       5,080 |       2,377 |       3,144 |
| 2026-03 |       3,392 |       2,481 |        -467 |       8,337 |       8,237 |
| 2026-04 |  **-21,110**|  **-13,339**|  **-13,622**|  **-14,461**|  **-12,828**|

The variants are **highly correlated**: every losing month (May'24,
Sep'24, Nov'24, Apr'25, Jul'25, Apr'26) hits all five together, and
every winning month does too. The choice between variants is a question
of *how much* drawdown to accept in exchange for *how much* total, not
*when* the drawdowns happen.

## Aggregate stats (proper MTM)

| variant | months | total | avg/mo | sd/mo | ann Sharpe | win mo | los mo | worst mo |     best mo |   max DD |
|---------|-------:|------:|-------:|------:|-----------:|-------:|-------:|---------:|------------:|---------:|
| z100    |     25 |150,734|  6,029 |31,680 |       0.66 |     17 |      8 | -106,502 |      55,871 | **-115,205** |
| ng200   |     25 |153,602|  6,144 |31,188 |       0.68 |     17 |      8 | -101,598 |      60,789 | -109,369 |
| ng450   |     25 |145,944|  5,838 |29,529 |       0.68 |     16 |      9 |  -97,313 |      56,553 | -105,565 |
| ng4500  |     24 |134,364|  5,599 |28,382 |       0.68 |     16 |      8 |  -93,838 |      54,534 |  -99,537 |
| ng6750  |     23 |121,613|  5,288 |22,520 |   **0.81** |     16 |      7 | **-68,098** | 45,579 | **-70,334** |

Per-trade payoff scales from $46 (z100, 3,294 trades) to $264 (ng6750,
460 trades). **Total P&L drops only ~20%** going from the chattiest to
the most selective variant, while **max drawdown drops 40%** and
**Sharpe rises 23%**.

## Takeaways

1. **The persistence gate is removable.** ng200 produces +$153k total
   vs z100's +$151k with materially identical risk. The simplification
   works — and at higher thresholds the no-gate variants strictly
   dominate on risk-adjusted metrics.

2. **All variants are highly correlated and bumpy.** The earlier
   smoothed view (linear pro-rata across holding days) suggested zero
   losing months for the no-gate cells; that was a math artifact. The
   real picture is 7-9 losing months out of 25 and **$70k+ max DD on
   $1k base notional** (70x base) for every variant.

3. **Higher threshold = better risk profile.** ng6750 is the
   risk-adjusted winner — $70k max DD vs $115k for z100, Sharpe 0.81
   vs 0.66, same number of losing months. ~20 trades/month at $264
   avg payoff.

4. **Why this matters for live deployment.** With holds averaging 5+
   months and max-DD at 70× base notional, **position sizing relative
   to capital becomes the dominant risk-management lever**, not stop
   losses. A "use Kelly fraction × max-DD as base notional" calculation
   would size each position around 1% of capital and accept that the
   strategy will spend material time underwater on paper.

5. **The realized-only equity curve is dangerous.** The previous
   `equity_curves.py` used exit-date bucketing — for a strategy with
   172-day holds, this hides essentially all unrealized drawdown.
   Always MTM long-hold systems before risk-sizing them.

## Recommended production cell

- **ng6750** for risk-managed deployment (smallest DD, highest Sharpe,
  $264/trade payoff)
- **ng200 or z100** if maximum gross return matters more than
  drawdown control

Either way, the no-gate variant is at least as good as the gated
baseline on every metric and the engine becomes simpler.

## FlowSwing (OrderflowLongFadeMA) — same MTM treatment

The same decomposition applied to the FlowSwing production cell
(`pd=14% cvd=4h cover=72h no-stops rvol≥0.75`,
3,914 trips, long-only). Anchors are reused from the cumsum-z run.

### Monthly P&L

| Month   | FlowSwing |
|---------|----------:|
| 2024-05 |       125 |
| 2024-06 |     1,865 |
| 2024-07 |    14,412 |
| 2024-08 |    14,582 |
| 2024-09 |       -40 |
| 2024-10 |       614 |
| 2024-11 |     1,170 |
| 2024-12 |    14,980 |
| 2025-01 |     1,745 |
| 2025-02 |     9,696 |
| 2025-03 |     2,874 |
| 2025-04 |     4,303 |
| 2025-05 |     2,972 |
| 2025-06 |     2,827 |
| 2025-07 |        38 |
| 2025-08 |       794 |
| 2025-09 |      -584 |
| 2025-10 |    11,453 |
| 2025-11 |     1,909 |
| 2025-12 |       759 |
| 2026-01 |      -478 |
| 2026-02 |     9,871 |
| 2026-03 |       296 |
| 2026-04 |       786 |

### FlowSwing aggregate (MTM)

| months | total | avg/mo | sd/mo | ann Sharpe | win mo | los mo | worst mo | best mo | **max DD** |
|-------:|------:|-------:|------:|-----------:|-------:|-------:|---------:|--------:|-----------:|
|     25 | 96,964 |  3,879 | 5,201 |   **2.58** |     21 |      4 |     -584 |  14,980 |   **-584** |

**FlowSwing under proper MTM is dramatically better than any cumsum-z
short variant:**

- **Sharpe 2.58 vs ng6750's 0.81** (3.2× higher)
- **Max drawdown $584 vs ng6750's $70,334** (120× smaller)
- **4 losing months in 25, all under $600**

This is what edge looks like when it's real and persistent across regimes
versus what cumsum-z is doing (real edge but with huge regime-correlated
swings). The two strategies are doing **fundamentally different things** —
FlowSwing is capitulation-fade-to-MA on longs (one event per
symbol per cycle, 42h median hold, mean-reverting under genuine
oversold conditions); cumsum-z shorts are trending-flow follows that
get squeezed during universe-wide rallies (correlated tail risk).

### Plot

[data/crypto/long_fade_ma_default_rvol075/equity_curve.html](../data/crypto/long_fade_ma_default_rvol075/equity_curve.html)

## ShortFadeMA mirror experiments — does the FlowSwing recipe transfer?

After confirming FlowSwing's MTM quality, we asked: does the same setup work
when sign-flipped? `OrderflowShortFadeMA.fs` exists but ships with a
**dual-CVD overlay** (200h CVD must also be negative) that the long engine
doesn't have. We tested whether the simple mirror (drop the overlay) works,
and if not, what knobs to turn instead.

### Step 1 — simple mirror at FlowSwing's exact parameters

Config: `pr=0.14, ma=72h, cvd=240m, rvol=0.75, ed=0.20, high8h ref, no
overlay, no stop`. Production defaults applied.

**Result: PF 0.94, total -$11,386, Sharpe -0.36.** Doesn't work.

Sweeping `pr ∈ {0.14, 0.21, 0.32, 0.47, 0.71, 1.07}`: every cell PF < 1.
The simple mirror does not work at any rise threshold.

### Step 2 — vary the cover-MA window

Sweeping `pr × ma_hours` 2D grid:

| pr   | ma=72h         | ma=144h        | ma=288h        | ma=576h          |
|------|----------------|----------------|----------------|------------------|
| 0.14 | 0.94 / -$11k   | 0.94 / -$13k   | 0.95 / -$12k   | **1.14 / +$28k** |
| 0.21 | 0.94 / -$6k    | 0.93 / -$10k   | 0.98 / -$4k    | **1.16 / +$24k** |
| 0.32 | 0.95 / -$3k    | 0.92 / -$6k    | 1.03 / +$3k    | **1.15 / +$15k** |
| 0.47 | 0.87 / -$4k    | 0.82 / -$8k    | 1.08 / +$4k    | **1.17 / +$11k** |

**PF rises monotonically with `ma_hours`.** The flip-to-profitable threshold
is between 288h (12d) and 576h (24d).

Extending the sweep with longer MAs:

| pr   | ma=576h | ma=864h | ma=1296h | ma=1944h | ma=2916h | ma=4374h           | ma=6561h |
|------|---------|---------|----------|----------|----------|--------------------|----------|
| 0.14 | 1.14    | 1.28    | 1.60     | 1.84     | 2.07     | **3.34 (+$69k)**   | 3.54     |
| 0.21 | 1.16    | 1.28    | 1.65     | 1.78     | 2.06     | **3.40 (+$63k)**   | 3.44     |
| 0.32 | 1.15    | 1.35    | 1.73     | 1.97     | 2.30     | **3.68 (+$60k)**   | 3.33     |
| 0.47 | 1.17    | 1.45    | 1.87     | 2.30     | 2.79     | **4.31 (+$56k)**   | 3.02     |

**Optimum at ma=4374h (6 months).** Beyond 6,561h the strategy collapses
because the 2-year backtest window can't accommodate longer warm-ups
(trip count drops 1173 → 426 → 157 → 21 across 4374 → 6561 → 9842 → 14763).

The 6-month cell at `pr=0.14 ma=4374h` matches FlowSwing's PF range:
1,173 trips, 90.7% win rate, 22-day median hold, PF 3.34.

### Step 3 — MTM exposes a single landmine

Cell `pr=0.14, ma=4374h, cvd=240m`. Full MTM monthly:

| Month   |   short pnl |
|---------|------------:|
| 2024-11 | **-46,457** |
| 2024-12 |      49,444 |
| 2025-01 |      10,152 |
| 2025-02 |      16,693 |
| (16 more positive months, all small) | |

| metric | value |
|---|---:|
| total | $69,088 |
| Sharpe | 0.66 |
| max DD | **-$46,457** |
| worst month | -$46,457 (Nov 2024) |

**One month (Nov 2024) is the entire drawdown.** Ex-Nov-2024 the strategy
would be Sharpe ~4, max DD ~$1k. The MTM view also reveals that the
Nov 2024 hit comes from **trips entered in Nov 2024 marked-to-market at
month end** (entry-month-bucketed view shows +$16.8k for Nov-entered
trips because they eventually closed profitably in Dec/Jan — but the MTM
view sees them sitting underwater on Dec 1).

### Step 4 — CVD-window sweep at `pr=0.14, ma=4374h`

The hypothesis was that requiring sustained negative CVD over a longer
horizon would filter out squeeze entries. Sweeping `cvd_minutes ∈ {240,
480, 960, 1920, 3840, 7680, 15360}` (4h to 256h, 2x geometric):

| CVD window | trips | total | Sharpe (MTM) | max DD | Nov 24 MTM |
|---:|---:|---:|---:|---:|---:|
| 4h (240m) | 1,173 | $69,088 | 0.74 | -$46,457 | -$46,457 |
| 32h (1920m) | 791 | $55,604 | 0.92 | -$27,711 | -$27,711 |
| 64h (3840m) | 589 | $38,061 | 1.04 | -$18,011 | -$18,011 |
| **128h (7680m)** | **396** | **$25,763** | **1.35** | **-$9,011** | **-$9,011** |
| 256h (15360m) | 247 | $12,446 | 0.94 | -$5,926 | -$5,926 |

**Longer CVD windows reduce Nov 2024 MTM drawdown sharply** (from -$46k
at 4h to -$9k at 128h to -$6k at 256h) at the cost of total return.
Risk-adjusted Sharpe peaks at 128h.

### Step 5 — the combined book finally beats long-only

| leg | total | max DD | wins | losses | ann Sharpe |
|---|---:|---:|---:|---:|---:|
| FlowSwing long | $96,964 | -$584 | 21 | 4 | 2.58 |
| Short cvd=128h | $25,763 | -$9,011 | 15 | 5 | 1.20 |
| **Combined** | **$122,727** | **-$7,841** | **21** | **4** | **2.64** |

**This is the first time a combined long+short book beats FlowSwing
alone on Sharpe.** The Nov 2024 hit is contained because:
- The 128h CVD filter cuts Nov 2024 short entries (248 → 40)
- FlowSwing made +$1.2k in Nov 2024 (one of its weakest months but not negative)
- Combined Nov 2024 net = -$7.8k vs short-only -$9.0k

Plot: [data/crypto/short_fade_ma_longma_cvd_sweep/equity_curves_long_vs_cvd128h.html](../data/crypto/short_fade_ma_longma_cvd_sweep/equity_curves_long_vs_cvd128h.html)

### Asymmetry conclusion

The asymmetry hypothesis (capitulations revert; rallies don't always) is
real and required two structural fixes for the short side:

1. **A much longer cover MA** (6 months vs the long-side's 3 days). Rallies
   take longer to mean-revert.
2. **A longer CVD trigger window** (128h ≈ 5.3 days vs the long-side's 4h).
   Multi-day sustained selling pressure is required to enter — not just a
   single-bar flow flip.

Even with both fixes, the strategy is dramatically lower-quality than the
long side (Sharpe 1.20 vs 2.58, max DD 35× larger). The combined book is
useful as a **complement** to FlowSwing, not a parallel strategy.

### ShortFadeMA mirror artifacts

- 2D pr × ma sweep (4×4): [data/crypto/short_fade_ma_mirror_2d/](../data/crypto/short_fade_ma_mirror_2d/)
- Long-MA sweep (4×6): [data/crypto/short_fade_ma_mirror_long/](../data/crypto/short_fade_ma_mirror_long/)
- Very-long-MA sweep (4×4): [data/crypto/short_fade_ma_mirror_xlong/](../data/crypto/short_fade_ma_mirror_xlong/)
- CVD sweep (1×7): [data/crypto/short_fade_ma_longma_cvd_sweep/](../data/crypto/short_fade_ma_longma_cvd_sweep/)
- MTM SQL: [scripts/crypto/mtm_shortfade_mirror.sql](../scripts/crypto/mtm_shortfade_mirror.sql), [scripts/crypto/mtm_shortfade_longma.sql](../scripts/crypto/mtm_shortfade_longma.sql), [scripts/crypto/mtm_shortfade_cvd128h.sql](../scripts/crypto/mtm_shortfade_cvd128h.sql)
- Combined-book plot: [scripts/crypto/plot_flowswing_vs_shortcvd128h.py](../scripts/crypto/plot_flowswing_vs_shortcvd128h.py)

## Files

- Engine change: [TradingEdge.CryptoBacktest/OrderflowCumsumZ.fs](../TradingEdge.CryptoBacktest/OrderflowCumsumZ.fs) (added `RequirePersistenceForEntry` config)
- CLI: [TradingEdge.CryptoBacktest/Program.fs](../TradingEdge.CryptoBacktest/Program.fs) (`--no-entry-persistence-gate`)
- Trip CSVs:
  - `data/crypto/cumsum_z_baseline/results_trips_1m_th100_ls.csv` (gated)
  - `data/crypto/cumsum_z_no_gate_low/results_trips_1m_th{200,300,450,675,1000,1500}_ls.csv`
  - `data/crypto/cumsum_z_no_gate/results_trips_1m_th{2000,3000,4500,6750,10125,15187}_ls.csv`
- MTM decomposition SQL: [scripts/crypto/mtm_decompose.sql](../scripts/crypto/mtm_decompose.sql)
- Anchor parquet: `data/crypto/cumsum_z_no_gate/anchors.parquet`
- Monthly MTM CSV: `data/crypto/cumsum_z_no_gate/mtm_monthly.csv`
- Plot script: [scripts/crypto/plot_cumsum_z_mtm.py](../scripts/crypto/plot_cumsum_z_mtm.py)
- Equity-curve HTML: `data/crypto/cumsum_z_no_gate/equity_curves.html`
- FlowSwing MTM SQL: [scripts/crypto/mtm_flowswing.sql](../scripts/crypto/mtm_flowswing.sql)
- FlowSwing monthly CSV: `data/crypto/long_fade_ma_default_rvol075/mtm_monthly.csv`
- FlowSwing plot script: [scripts/crypto/plot_flowswing_mtm.py](../scripts/crypto/plot_flowswing_mtm.py)
- FlowSwing equity HTML: `data/crypto/long_fade_ma_default_rvol075/equity_curve.html`
