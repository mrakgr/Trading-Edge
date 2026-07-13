# OpeningDriver — results

An **opening-drive study** (long-only, branch `opening-driver`), forked from DipRiderV4 for its clean
feature-folding but with the arm/re-arm state machine and breakout timers deleted. The engine is a pure
recorder: for each candidate day it emits **every bar in the entry window [open+15, open+60] = [09:45, 10:30]
as a trip**, held to MOC or a 9-EMA stop. All gate/feature analysis is done POST-HOC on the emitted CSV
(analyzed via DuckDB column projection — never loading the full multi-hundred-MB CSV into memory).

**Recorded feature levers:** 20m log-ATR (ATR%), 20m OLS log-price slope, 20m OLS log-volume slope, chg_1d
(entry vs prev daily close), chg_3d (entry vs close 3d ago), and `rvol_cum` (cumulative session volume vs
the 20d-average pace through the entry bar). Universe = `vwap_reclaim_candidate` (ADV ≥ $1M & rvol_0945 > 1).
$10k notional/trip. Era slices (run separately to keep each CSV manageable); the MODERN era is 2020-01 → 2026-06.

**The 9-EMA stop** fires when the live 9-EMA drops below a chosen reference (`--stop-mode`):
- `vwap` — the live session VWAP (dynamic; the TIGHT stop).
- `sess-ema-low` — the 9-EMA session-min frozen at entry (the LOOSE stop).

---

## F1 — The loose (sess-ema-low) stop dominates the tight (VWAP) stop

Both books take the identical 980,708 window-bar trips (2020-26); only the stop differs.

| stop mode | trips | win % | net | PF (MOC) |
|---|---|---|---|---|
| `vwap` (9-EMA < live VWAP; TIGHT) | 980,708 | 37.6% | $18.5M | 1.239 |
| **`sess-ema-low` (9-EMA < frozen session-min; LOOSE)** | 980,708 | 33.5% | **$58.5M** | **1.373** |

The tight VWAP stop scratches ~93% of entries at the first sub-VWAP tick (entries fire at every window bar
regardless of price-vs-VWAP, so most are born ~at VWAP). The loose session-min stop has a lower win rate but
**3.2× the net and a higher PF** — it lets the opening drive actually run. **`sess-ema-low` is the study's
base stop.** (The firehose / 93%-scratch on VWAP is intentional — the point is to find WHICH entries survive.)

---

## F2 — Feature attribution: ATR%, chg_1d, chg_3d dominate; rvol_cum is dead

Decile buckets on the sess-ema-low book (2020-26, n=980,708, whole-book PF 1.373 / +0.60%/trade). Every
lever is monotone and the **top decile carries the book**; the ranking is STOP-INVARIANT (identical order on
the VWAP book, just smaller magnitudes — so the edge is in the ENTRY features, not the stop).

**The three dominant levers (top-decile PF ~2):**

| feature | decile-10 range | n | win% | PF | avg%/tr | net |
|---|---|---|---|---|---|---|
| **chg_1d** (day-move into entry) | ≥ +20.7% | 97,951 | 38 | **2.15** | +4.96 | $48.6M |
| **log_atr_20** (ATR%) | ≥ 0.0218 | 98,070 | 34 | **1.94** | +4.61 | $45.3M |
| **chg_3d** (3-day move) | ≥ +45% | 97,391 | 34 | **1.93** | +4.14 | $40.4M |

Each top decile alone ≈ 70-83% of the whole book's net. This is the DipRiderV4 "ATR% is the main lever" law,
plus the day/3-day momentum being already-way-up (buy strength, not the bounce).

**Secondary levers (real, weaker):**

| feature | decile-10 | PF | avg%/tr | net | note |
|---|---|---|---|---|---|
| price_slope_20 | ≥ +0.0019 | 1.68 | +3.04 | $30M | ⚠ U-SHAPED: the BOTTOM decile (steep down-slope) is ALSO +EV (PF 1.58) — a mean-reversion pocket. The middle is dead. |
| vol_slope_20 | ≥ +0.033 | 1.68 | +1.48 | $14M | mild but monotone (rising volume better). |

**Dead lever:**
- `rvol_cum` (cumulative session volume vs 20d pace) — NON-monotone and weak. On the VWAP book its top
  decile is actually WORSE (PF 1.18 vs 1.33 for the low-rvol decile). The bucket-1 "signal" on the loose book
  (PF 1.77) is a small-denominator artifact (rvol_cum ranges into the billions — a bad ratio near the open).
  **Cumulative-volume-vs-20d is NOT a useful opening-drive lever.** (Contrast: ATR%/chg1d/chg3d are.)

**Read:** the opening-drive edge is **high-ATR names already up big on the day/3-day, held on a loose 9-EMA
session-min stop.** chg_1d ≥ +20% is the single strongest cut. Next: stack the top levers (ATR% × chg_1d ×
chg_3d) and check the entry-time profile within the window (does open+15 beat open+60?).
