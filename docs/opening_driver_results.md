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

Exit-reason split (2020-26):

| stop mode | exit=stop | exit=moc | win rate (net>0) | near-flat (\|ret\|<0.1%) |
|---|---|---|---|---|
| `vwap` (TIGHT) | 94.0% | 6.0% | 37.6% | 14.5% |
| `sess-ema-low` (LOOSE) | 64.9% | 35.1% | 33.5% | 6.9% |

Note "stopped" ≠ "loss": on the VWAP book **94% exit via the stop but 37.6% are still net-positive** — the
9-EMA often ticks below VWAP only AFTER the trade is already up, so a large share of stop-exits lock in a
gain (only ~14.5% are true near-flat scratches). The tight VWAP stop still caps the winners hard (only 6%
reach MOC), which is why its PF/net lag. The loose session-min stop lets 35% ride to MOC — a lower win rate
but **3.2× the net and a higher PF**: it lets the opening drive actually run. **`sess-ema-low` is the study's
base stop.** (The firehose — every window bar becomes a trip — is intentional; the point is to find WHICH
entries survive, F2.)

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

---

## F3 — 09:45 ET entries only: the same hierarchy holds on the pure opening-drive minute

Restricting to `entry_time = 09:45` (open+15, the first window minute) on the sess-ema-low book, 2020-26:
**n=21,277, PF 1.309, +0.47%/trade, win 32.9%.** The full-window feature ranking survives the restriction.

**log_atr_20 (ATR%) — monotone, top decile dominates:**

| dec | range | n | win% | PF | avg% | net |
|---|---|---|---|---|---|---|
| 10 | ≥ 0.0300 | 2127 | 34 | **1.78** | +3.50 | **$745k** |
| 9 | 0.0206–0.0300 | 2127 | 33 | 1.15 | +0.37 | $78k |
| 1–8 | < 0.0206 | — | ~32 | ~1.0–1.2 | ~0 | small |

Decile 10 alone = **75% of the whole 09:45 book's net** from 10% of the trips.

**chg_1d — the single strongest cut, and a DEAD ZONE in the middle:**

| dec | range | n | PF | avg% | net |
|---|---|---|---|---|---|
| 10 | ≥ +19.9% | 2125 | **2.05** | +4.10 | **$870k** |
| 9 | +7.3–19.9% | 2125 | 1.14 | +0.34 | $72k |
| 7 | +1.8–3.7% | 2125 | 1.30 | +0.39 | $83k |
| **5–6** | **−1.0% to +1.8%** | 4250 | **0.74 / 0.91** | **−0.25 / −0.10** | **−$75k** |
| 1–4 | red on the day | — | ~1.0 | ~0 | flat |

Decile 10's net ($870k) ≈ the WHOLE book ($997k) — the rest is net breakeven. And a stock going NOWHERE on
the day (chg_1d flat, deciles 5-6) is a **losing** 09:45 entry. Buy strength or don't buy.

**chg_3d — top two deciles carry it:**

| dec | range | n | PF | avg% | net |
|---|---|---|---|---|---|
| 10 | ≥ +43.9% | 2113 | 1.60 | +2.43 | $514k |
| 9 | +19.7–43.9% | 2113 | **1.82** | +1.64 | $346k |
| 1–8 | < +19.7% | — | ~1.0 | ~0 | small (dec 3 = 0.81 dead) |

**vol_slope_20 — monotone, cleaner at 09:45 than the full window:**

| dec | range | n | PF | avg% | net |
|---|---|---|---|---|---|
| 10 | ≥ +0.025 | 2127 | **1.68** | +1.73 | $367k |
| 8 | −0.024 to −0.006 | 2127 | 1.44 | +0.66 | $140k |
| 1–7 | falling vol | — | ~1.1–1.3 | ~0.1–0.4 | modest |

**price_slope_20 — a sharp TWO-SIDED (U-shaped) signal at 09:45:**

| dec | range | n | PF | avg% | net |
|---|---|---|---|---|---|
| 10 | steep UP (≥ +0.0032) | 2127 | **1.51** | +2.48 | $528k |
| 1 | steep DOWN (≤ −0.0031) | 2128 | **1.81** | +0.68 | $145k |
| 3 | −0.0018 to −0.0011 | 2128 | 1.80 | +0.53 | $112k |
| **6** | **flat (~0)** | 2128 | **0.93** | −0.07 | −$16k |

BOTH ends are +EV, the flat middle is dead. At 09:45 the drive works when price is ramping (up-slope) AND
when it's snapping back off a steep early drop (down-slope) — a genuine two-sided edge, not a pure momentum
lever. The up-slope decile carries the bigger avg% (+2.48); the down-slope side is a lower-vol reversion pocket.

**09:45 verdict:** ATR% (main lever) + chg_1d ≥ +20% (buy strength, avoid the flat-day dead zone) are the
two headline cuts; chg_3d ≥ +20%, rising vol_slope, and BOTH tails of price_slope stack on top. Next: build
the graded 09:45 book from ATR% × chg_1d × chg_3d and check the other entry minutes (09:46…10:30) the same way.
