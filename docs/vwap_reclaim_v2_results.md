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

## Finding 2 — plain trailing-20m ATR% is ADDITIVE to the depth features, NOT a replacement — and the edge lives in HIGH ATR% (inverts the `dist/atr` intuition)

**Question:** can we replace `run_dist_per_atr < 3` (and maybe also `run_max_dist`) with a plain trailing-20m
ATR% floor — the volatility lever that carries DipRiderV3 (ATR≥0.013 = its main gate) and BreakoutTimer?
`intraday_atr_pct_at_entry` IS that feature — verified `AvgMa(20)` of the 1m LOG true range, same construction
as the other two systems (`Intraday.fs:225,505`; `VolWindow=20`). No NaNs; range 0.002–0.068, median 0.0224.

**Answer: NO — ATR% is a COMPLEMENT, not a substitute. Replacing the depth features LOSES ~1 PF point;
STACKING ATR% on top of them holds PF and adds robustness.** Three tests (all on the 22y fat book, filtered):

**(a) Drop BOTH depth features, ATR% alone** (base = `updn≥1.3 & rvol15m<2`):

| filter | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| INCUMBENT (updn & dist≥3.5% & d/atr<3 & rvol) | 232 | 50.4 | **4.38** | 20.3 | 470k |
| ATR% ≥ 0.020 (both dropped) | 334 | 43.1 | 2.89 | 14.1 | 470k |
| ATR% ≥ 0.025 (both dropped) | 219 | 44.7 | 3.32 | 18.7 | 410k |
| ATR% ≥ 0.030 (both dropped) | 126 | 54.0 | 5.15 | 29.3 | 369k |

At matched breadth (~230 trips, ATR%≥0.025) the both-dropped book is PF 3.32 vs the incumbent 4.38. And the
`ATR% [0,0.010)` bucket is n=**5,860** at PF 1.2 — without a depth OR ATR floor the base is flooded with
low-vol junk. `run_max_dist` carries real signal ATR% doesn't reproduce.

**(b) Keep `run_max_dist`, SWAP `d/atr` → ATR% floor:** tops out at PF 3.42–3.95 across all floors — worse than
the incumbent 4.38 at every matched breadth. So `d/atr` is NOT redundant with ATR% either.

**(c) STACK both (`d/atr<3 AND ATR%≥X`) — this is the win:**

| book | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| INCUMBENT (`d/atr<3`, no ATR floor) | 232 | 50.4 | 4.38 | 20.3 | 470k |
| **`d/atr<3 & ATR%≥0.015`** | 214 | 49.5 | **4.28** | 20.6 | 441k |
| **`d/atr<3 & ATR%≥0.020`** | 178 | 49.4 | 4.34 | 22.3 | 397k |
| **`d/atr<3 & ATR%≥0.025`** | 134 | 52.2 | **4.97** | 27.3 | 366k |

**Mechanism — the cuts are ORTHOGONAL.** `d/atr<3` removes "quiet grinds that got deep on LOW volatility"
(V1 Finding 28's contra-indicator). Plain ATR% removes "low-absolute-volatility names" outright. A name can be
low-`d/atr` (volatile vs its own depth) yet still low-absolute-ATR%, and vice versa — so stacking compounds
rather than duplicates. The single strongest ATR% effect is the HIGH-END inversion: `ATR% [0.030,0.040)` = PF
5.6/+30%, `≥0.040` = PF 4.8/+28% (n=126 hold $369k of $714k net). **Volatility loves volatility — the same
lesson as DipRiderV3/BreakoutTimer, and the exact OPPOSITE of what a naive reading of `dist/atr` suggests.**

**Yearly stability of the mild stack** (`incumbent + ATR%≥0.015`, 2020+): every year positive, PF 2.4–8.6,
trims only ~18 trips over 6y. Only soft spot is 2022 (4.09→2.42, still profitable). NOT front-loaded into
2020/2025.

**VERDICT:** do not replace the depth features with ATR%. **ADD ATR% as a third quality gate.** Candidate A+v2 =
`updn≥1.3 & run_max_dist≥3.5% & d/atr<3 & rvol15m<2 & ATR%≥0.015–0.020`. Open: whether to promote these post-hoc
filters to real ENGINE gates (per the BreakoutTimer methodology — gate≠post-hoc once concurrency matters), and
whether a higher ATR% floor (0.025–0.030) trading breadth for PF 5+ is the better production cell.
