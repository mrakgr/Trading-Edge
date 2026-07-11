# VwapReclaimV2 — feature-revision research log

Fork of VwapReclaim (branch `vwap-reclaim-v2`, project `TradingEdge.VwapReclaimV2`), created 2026-07-11 to
revise the FEATURE SET with the experience gained on BreakoutTimer + MaxFlyerV3. V1 is settled and lives on
`main` as the baseline (`docs/vwap_reclaim_results.md`, Findings 1–34).

**Standing conventions (inherited):** long-only intraday VWAP × 9-EMA reclaim scalp. Fat book = the current
default config (10:00–13:30 ET, rb≥11, tight≥3, close-stop d·2/3, NO target, $30M-ADV precondition). Raw MOC PF.
A+ features are RECORDED-only CSV columns (`run_updn_ratio`, `run_max_dist`, `run_dist_per_atr`, `rvol20m_15m`),
applied as a post-hoc SQL filter — NOT engine gates. Judge on PF + avg-ret% + yearly stability. Journal every
finding as a bucket TABLE. The edge is POST-2020 (flat pre-COVID).

---

## Finding 1 — ✅ BASELINE: the A+ cell replicates V1 to the decimal (fork is faithful)

Ran the V2 fat book over the full history (`dotnet run --project TradingEdge.VwapReclaimV2 -c Release`) → 41,027
trips / PF 1.298 / net $1.50M. Then applied the V1 A+ cell filter (Finding 33):

**A+ CELL = `run_updn_ratio ≥ 1.3 & run_max_dist ≥ 0.035 & run_dist_per_atr < 3 & rvol20m_15m < 2`**

Over V1's exact 5-year window (2020-07-01 → 2025-06-30) it reproduces Finding 33 **to the decimal**:

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| V1 Finding 33 (doc) | 184 | 48.4 | 4.027 | +19.50 |
| **V2 replication (this)** | **184** | **48.4** | **4.027** | **+19.50** |

Fork verified faithful (also byte-identical on the 2024-Q1 smoke test at fork time). The full-history and
modern-era cuts:

| window | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| fat book (all 22y) | 41,027 | 41.1 | 1.298 | +0.37 | 1,503,082 |
| A+ cell (all 22y) | 232 | 50.4 | 4.376 | +20.26 | 470,059 |
| fat book 2020+ | 16,788 | 41.8 | 1.441 | +0.83 | 1,387,085 |
| A+ cell 2020+ | 227 | 49.8 | 4.341 | +20.46 | 464,441 |

### A+ cell yearly (full history)

| Year | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| 2020 | 22 | 68.2 | 8.78 | +39.7 | 87,372 |
| 2021 | 62 | 33.9 | 2.54 | +11.4 | 70,966 |
| 2022 | 27 | 55.6 | 4.09 | +16.1 | 43,521 |
| 2023 | 17 | 52.9 | 5.76 | +34.1 | 57,917 |
| 2024 | 38 | 36.8 | 2.76 | +13.5 | 51,402 |
| 2025 | 39 | 71.8 | 7.05 | +25.0 | 97,627 |
| 2026* | 22 | 50.0 | 6.50 | +25.3 | 55,637 |

\*2026 partial. Pre-2020 years are 1-trip curiosities (edge is POST-2020, as documented in V1). Every
meaningful-n year is positive, PF 2.5–8.8. This is the baseline the feature revisions build on — any change
from here is judged against A+ = PF ~4.0 / +19.5% avg / ~184 trips (5y window).
