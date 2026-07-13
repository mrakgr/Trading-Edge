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

> NOTE: `log_atr_20` / `vol_slope_20` are 20m TRAILING windows. At 09:45 (open+15) a 20m window and a
> session-long window cover nearly the same bars (09:30→09:45 ≈ 15 bars; the 20m tail reaches ~09:25,
> pre-open), so the distinction is immaterial HERE. It will matter for later entry minutes — revisit
> (session-long ATR%/vol-slope) when sweeping 09:46…10:30.

---

## F4 — Building the 09:45 book, one lever at a time (ATR% → +vol_slope)

Stacking features on the 09:45 sess-ema-low book (2020-26). Each addition is chosen at its knee.

**Step 1 — `log_atr_20` floor (the main lever). Monotone; knee at ≥ 0.02:**

| floor | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| 0 (all) | 21,277 | 33 | 1.31 | +0.47 | $998k |
| 0.013 | 8,342 | 34 | 1.44 | +1.13 | $941k |
| **0.020** | **4,435** | **34** | **1.55** | **+1.87** | **$831k** |
| 0.030 | 2,125 | 34 | 1.78 | +3.51 | $745k |
| 0.040 | 1,152 | 35 | 1.89 | +4.91 | $565k |

`ATR% ≥ 0.02` is the base: PF 1.55 / +1.87%/trade, retaining 83% of book net on 21% of trips.

**Step 2 — `+ vol_slope_20` floor (ADDITIVE, not redundant). Knee at ≥ 0.01:**

| + vol_slope | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| (base, none) | 4,435 | 34 | 1.55 | +1.87 | $831k |
| ≥ 0.0 | 972 | 39 | 1.71 | +3.68 | $358k |
| **≥ 0.01** | **817** | **41** | **1.80** | **+4.26** | **$348k** |
| ≥ 0.025 | 644 | 41 | 1.83 | +4.59 | $296k |
| ≥ 0.05 | 399 | 42 | 1.79 | +4.36 | $174k |

**They stack additively** — vol_slope alone tops out at PF 1.68, ATR alone at 1.55; TOGETHER they reach 1.80.
Each is independent signal (ATR% = move size; vol_slope = volume confirming it's real). Win rate climbs
monotonically 34%→41% as vol_slope tightens (the volume confirm lifts the HIT rate, not just the tail).
`vol_slope ≥ 0.01` is the knee: +0.25 PF over the ATR base for ~half the trips; ≥0.025 buys only +0.03 more,
≥0.05 rolls over. **Running 09:45 book: `ATR% ≥ 0.02 & vol_slope ≥ 0.01` → PF 1.80 / +4.26%/tr / 817 trips /
$348k.**

---

## F5 — price_slope is SUBSUMED by ATR% + vol_slope — dropped as a lever

The raw-book (F3) U-shape said price_slope was a two-sided edge (steep-down PF 1.81, steep-up 1.51, dead
middle). Tested WITHIN the running base book (`ATR% ≥ 0.02 & vol_slope ≥ 0.01`, PF 1.80) — the U-shape
COLLAPSES. No price_slope cut beats the base:

| price_slope cut (within base) | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| (base, all) | 817 | 41 | **1.80** | +4.26 | $348k |
| ≥ 0.0 (up-slope) | 681 | 39 | 1.77 | +4.80 | $327k |
| ≥ 0.005 | 516 | 40 | 1.67 | +4.65 | $240k |
| ≥ 0.01 (steep up) | 242 | 43 | 1.72 | +6.01 | $146k |
| ≤ −0.004 (steep DOWN tail) | 82 | 51 | 1.84 | **+0.55** | $5k |
| both tails (\|steep\|) | 598 | 41 | 1.67 | +4.09 | $244k |
| flat-ish middle (exclude tails) | 219 | 38 | **2.44** | +4.72 | $103k |

**Every cut lands at or below the base's 1.80.** The raw U-shape was an ARTIFACT of ATR%/volume covarying
with slope — once conditioned on high ATR% and rising volume, price_slope carries no residual signal:
- The steep-DOWN tail (raw-book star, PF 1.81) is now 1.84 ≈ book, and its avg% is only **+0.55** (near-flat
  chop, not real winners despite 51% win) — it was an ATR/vol phenomenon, not a slope one.
- The "flat middle" (raw-book DEAD zone) is now the BEST cell (2.44) — a full inversion, i.e. noise, on 219
  trips. No monotone gradient survives.

**Verdict: DROP price_slope.** Adding it only shrinks the sample for zero PF gain — the exact subsumption
risk flagged going in. The book stays `ATR% ≥ 0.02 & vol_slope ≥ 0.01`. Next lever to test: chg_1d (the
day-strength cut — likely NOT subsumed, since it's a day-scale feature orthogonal to the 20m intraday ones).

---

## F6 — chg_1d is NOT subsumed — a strong, additive day-scale lever (concentrates quality at ~flat net)

Broke down chg_1d WITHIN the base book (`ATR% ≥ 0.02 & vol_slope ≥ 0.01`, PF 1.80). Unlike price_slope, it
LIFTS the base — a day-scale feature orthogonal to the 20m intraday ones carries independent signal.

**Deciles within base — monotone in the upper half:**

| dec | chg_1d range | n | win% | PF | avg% | net |
|---|---|---|---|---|---|---|
| 1–4 | red → +13% | 328 | ~40 | 1.3–1.5 | +0.6 to +1.7 | modest |
| 5 | +13–18% | 82 | 32 | 0.81 | −1.00 | −$8k (lone neg pocket, noise) |
| 6 | +18–27% | 82 | 39 | 2.28 | +7.14 | $59k |
| 7–8 | +27–50% | 162 | ~41 | 1.60 / 2.35 | +4.2 / +8.2 | $100k |
| 9 | +50–79% | 81 | 42 | 2.38 | +10.38 | $84k |
| 10 | ≥ +79% | 81 | 48 | 1.96 | +9.02 | $73k |

**Floor sweep — PF rises monotonically while net stays ~FLAT (pure quality concentration):**

| chg_1d floor | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| (base) | 817 | 41 | 1.80 | +4.26 | $348k |
| ≥ 0.03 | 650 | 40 | 1.83 | +5.11 | $332k |
| ≥ 0.10 | 523 | 40 | 1.90 | +6.00 | $314k |
| ≥ 0.15 | 455 | 42 | 2.00 | +6.92 | $315k |
| **≥ 0.20** | **396** | **43** | **2.11** | **+7.97** | **$315k** |

The ideal lever shape: base → `chg_1d ≥ 0.20` lifts **PF 1.80 → 2.11** and nearly DOUBLES avg%/trade
(+4.26 → +7.97) while **net barely moves** ($348k → $315k) — it sheds ~half the trips, but they were the
near-zero-EV ones (no dead weight lost). PF keeps rising to the top of the range (no rollover). Confirms
chg_1d is day-scale signal ATR%/vol_slope can't capture.

**Running 09:45 book: `ATR% ≥ 0.02 & vol_slope ≥ 0.01 & chg_1d ≥ 0.20` → PF 2.11 / +7.97%/tr / 396 trips /
$315k.** Next lever: chg_3d (also day-scale — check whether it adds on top of chg_1d or is collinear with it).

---

## F7 — chg_3d adds a real cut despite collinearity: reject the 3-day DOWNTREND bounce (+ a ceiling)

Broke down chg_3d within the current book (`ATR% ≥ 0.02 & vol_slope ≥ 0.01 & chg_1d ≥ 0.20`, PF 2.11).
`corr(chg_1d, chg_3d) = 0.533` within the book — partly collinear, but NOT redundant: it isolates a failure
mode chg_1d can't see.

**Deciles within book — a BAND, not a monotone floor (both tails are bad):**

| dec | chg_3d range | n | win% | PF | avg% | net |
|---|---|---|---|---|---|---|
| 1 | −30% → +10% | 39 | 38 | 1.46 | +3.27 | $13k |
| 2–8 | +10% → +150% | 271 | ~45 | 1.80–5.53 | +6.6 to +18 | strong |
| **9** | +151–233% | 38 | 42 | **0.87** | −1.18 | −$4k |
| **10** | ≥ +243% | 38 | 32 | **0.96** | −0.44 | −$2k |

Two effects:
- **FLOOR — reject the 3-day downtrend bounce.** `chg_3d < 0` (down over 3 days but up +20% TODAY) = **PF
  0.88 / avg% −0.84 / negative net** (28 trips). A one-day pop inside a multi-day downtrend is a dead-cat
  bounce, not a drive — the exact failure chg_1d ≥ 0.20 lets through.
- **CEILING — over-extended runners revert.** chg_3d ≥ +150% (already up 1.5-7× in 3 days) goes negative
  (deciles 9-10). So a high chg_3d floor (≥ 0.45) starts COSTING PF (2.34 → 1.89).

**Coarse cuts (book PF 2.11):**

| chg_3d cut | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| **≥ 0** | **358** | **44** | **2.31** | **+9.01** | **$323k** |
| ≥ 0.20 | 324 | 45 | 2.34 | +9.09 | $295k |
| ≥ 0.45 | 246 | 44 | 1.89 | +6.54 | $161k |
| < 0 (down-3d) | 28 | 36 | 0.88 | −0.84 | −$2k |

**`chg_3d ≥ 0` is the cut** — lifts PF 2.11 → 2.31 AND net RISES ($315k → $323k), because it removes only
losing trips (net-accretive, unlike chg_1d's flat-net concentration). The +0.20 floor is marginally higher
PF but sheds net; the value is the sign cut, not the magnitude. So chg_3d contributes despite r=0.53 — it
gates the day-trend *direction* (up today must not be against a 3-day downtrend).

**Running 09:45 book: `ATR% ≥ 0.02 & vol_slope ≥ 0.01 & chg_1d ≥ 0.20 & chg_3d ≥ 0` → PF 2.31 / +9.01%/tr /
358 trips / $323k.**

---

## F8 — chg_3d CEILING: cutting over-extended 3-day runners lifts PF and net together

Applied a chg_3d ceiling on top of the `chg_3d ≥ 0` floor (book PF 2.31). Two clean regions:

| chg_3d ceiling | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| (none) | 358 | 44 | 2.31 | +9.01 | $323k |
| ≤ 3.0 | 333 | 46 | 2.61 | +10.23 | **$341k** |
| **≤ 2.0** | **309** | **46** | **2.77** | **+10.78** | **$333k** |
| ≤ 1.5 | 280 | 46 | 2.96 | +11.69 | $327k |
| ≤ 1.2 | 255 | 46 | 3.08 | +12.31 | $314k |
| ≤ 0.8 | 196 | 47 | 3.34 | +12.89 | $253k |

- **≤ 3.0 is free money** — the 25 most-extended trips (up > 300% in 3 days) were net-negative; removing them
  lifts PF 2.31 → 2.61 AND net RISES to $341k (net-accretive, like the floor).
- **Below ~1.5 you trade net for PF** — ≤ 1.2 → PF 3.08 but net dips under the no-ceiling book; ≤ 0.8 → 3.34
  at a third less net.

**`chg_3d ≤ 2.0` is the book default** — PF 2.77 (+0.46 over floor-only) at net $333k (still above the
floor-only $323k). The over-extended runners (F7 ceiling insight) mean-revert; cap them. `≤ 1.2 → PF 3.08`
is the A+ variant (~flat net, tighter).

**⭐ 09:45 A-BOOK: `ATR% ≥ 0.02 & vol_slope ≥ 0.01 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 2.0` → PF 2.77 / +10.78%/tr
/ 309 trips / $333k.** All named levers placed (price_slope dropped as subsumed).

> chg_3d ceiling revised to **≤ 1.5** (PF 2.96 vs 2.77 at ≤2.0, ~flat net $327k — the PF gain outpaces the
> tiny net drop). Book before the ATR% relax: `... & 0 ≤ chg_3d ≤ 1.5` → PF 2.96 / +11.69%/tr / 280tr / $327k.

---

## F9 — ATR% floor is SUBSUMED by chg_1d ≥ 0.20 — relax it for +13% trips at flat PF

Now that the day-scale band carries the quality, re-swept the ATR% floor WITHIN the complete book
(`vol_slope ≥ 0.01 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 1.5`). Dropping it to 0 barely moves anything:

| ATR% floor | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| ≥ 0.0 (off) | 317 | 44 | 2.96 | +10.60 | $336k |
| **≥ 0.013** | **296** | **46** | **2.97** | **+11.37** | **$336k** |
| ≥ 0.015 | 295 | 46 | 2.97 | +11.39 | $336k |
| ≥ 0.02 (old) | 280 | 46 | 2.96 | +11.69 | $327k |

**chg_1d ≥ 0.20 subsumes the ATR% floor** — a stock up +20% on the day is high-ATR by construction, so the
explicit floor double-counts. Relaxing ATR% ≥ 0.02 → ≥ 0.013 buys **+16 trips (280 → 296, +6%) and +$9k net
at identical PF 2.97**; going all the way to 0 adds a few more at flat PF (the floor's only residual value is
a hair of avg%/trade, +11.7 vs +10.6). Keep a TOKEN floor `≥ 0.013` as a jumpiness guard — free.

**⭐ 09:45 A-BOOK (relaxed): `ATR% ≥ 0.013 & vol_slope ≥ 0.01 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 1.5` → PF 2.97 /
+11.37%/tr / 296 trips / $336k.** The day-strength band (chg_1d/chg_3d) is the real engine; ATR% and
price_slope are both subsumed by it.

---

## F10 — vol_slope is a two-sided U, NOT a floor — the `≥ 0.01` floor (F4) was a MISTAKE

Re-swept vol_slope WITHIN the complete day-scale book (`ATR% ≥ 0.013 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 1.5`).
Removing its own floor exposes the true book: **n=1417, PF 2.81, $895k** — far bigger than the 296 I was
carrying. vol_slope is NOT a monotone floor; it's a two-sided U (the same shape price_slope had — but this
one SURVIVES inside the full book, so it's real signal):

| vol_slope bucket | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| steep NEG (< −0.05, falling vol) | 696 | 38 | **3.07** | +4.60 | $320k |
| mild neg [−0.05, 0) | 374 | 36 | 2.50 | +5.97 | $223k |
| **flat [0, 0.025) — DEAD** | 121 | 40 | **1.67** | +3.56 | $43k |
| steep POS (≥ 0.025, rising vol) | 226 | 47 | **3.43** | +13.69 | $309k |

Both extremes are strong; the FLAT-slope middle is the dead pocket. Interpretation:
- **Steep rising volume** = the accelerating drive (PF 3.43, +13.7%/tr, best win rate) — the A+ pocket.
- **Steep falling volume** = a climactic opening spike RECEDING by 09:45 (huge open, settling) — also strongly
  +EV (PF 3.07), lower avg% because the move already largely happened.
- **Flat volume** = no conviction either way → dead (1.67).

**The F4 `vol_slope ≥ 0.01` floor was wrong** — it kept the rising side but discarded the equally-good
FALLING side (~$320k net) for a marginal PF gain. Three correct uses:

| use | cut | n | PF | net |
|---|---|---|---|---|
| **Capacity book** (drop the floor) | (none) | **1417** | **2.81** | **$895k** |
| Exclude the dead middle | \|vol_slope\| ≥ 0.025 | 922 | ~3.15 | ~$629k |
| A+ tightening dial | vol_slope ≥ 0.025 | 226 | **3.43** | $309k |

**⭐ 09:45 CAPACITY BOOK: `ATR% ≥ 0.013 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 1.5` (NO vol_slope floor) → PF 2.81 /
+6.40%/tr / 1417 trips / $895k.** The day-strength band is the whole engine; vol_slope, ATR%, price_slope
are all shape features it subsumes — vol_slope only as an A+ dial (≥ 0.025 → PF 3.43).

---

## F11 — Yearly stability: ALL-WEATHER (positive every year, incl. 2021); the edge is STRONGER recently

09:45 book, 2020-26. Both tiers are positive every single year.

**Capacity book (`ATR% ≥ 0.013 & chg_1d ≥ 0.20 & 0 ≤ chg_3d ≤ 1.5`):**

| year | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| 2020 | 191 | 45 | 2.42 | +4.19 | $80k |
| **2021** | 366 | 33 | **1.48** | +1.89 | $69k |
| 2022 | 151 | 41 | 2.81 | +6.34 | $96k |
| 2023 | 106 | 37 | 2.19 | +4.21 | $45k |
| 2024 | 178 | 45 | **5.20** | +15.48 | $276k |
| 2025 | 273 | 40 | 3.29 | +7.38 | $202k |
| 2026 | 152 | 38 | 3.53 | +8.48 | $129k |
| **TOTAL** | **1417** | 39 | **2.81** | +6.32 | $895k |

**A+ dial (`+ vol_slope ≥ 0.025`):**

| year | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| 2020 | 24 | 58 | 3.44 | +12.37 | $30k |
| 2021 | 67 | 39 | 1.76 | +4.48 | $30k |
| 2022 | 27 | 37 | 2.77 | +12.24 | $33k |
| 2023 | 13 | 38 | 2.98 | +14.10 | $18k |
| 2024 | 40 | 55 | 5.12 | +21.72 | $87k |
| 2025 | 38 | 50 | 5.12 | +18.85 | $72k |
| 2026 | 17 | 59 | 5.30 | +23.34 | $40k |
| **TOTAL** | **226** | 47 | **3.43** | +13.69 | $309k |

- **All-weather:** positive every year. Worst case is "less good," not a loss.
- **2021 is the weak year (PF 1.48)** — the adverse meme-chop regime, AND the highest trip count (366),
  i.e. the edge DILUTED as the setup fired most often. Still +$69k / PF 1.48 — it survives rather than
  blowing up. Every other year is PF 2.2–5.2. The A+ dial firms 2021 to PF 1.76.
- **Stronger recently:** 2024/25/26 are the BEST years (PF 3.3–5.2) — the edge is not decaying, reassuring
  for forward trading.
- A regime filter could lift 2021 (most trips + worst PF), but not urgent — it stays positive.

**⭐ The 09:45 opening-drive book is all-weather and tradable.**

---

## F12 — vol_slope dead zone is a NARROW notch [0, 0.025), NOT a symmetric band — [−0.025, 0) is fine

Fine buckets around zero (capacity book, PF 2.81). Answers "is only [0, 0.025) dull, or [−0.025, 0) too?"

| vol_slope bucket | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| < −0.10 | 170 | 36 | **4.21** | +5.71 | $97k |
| [−0.10, −0.05) | 526 | 39 | 2.80 | +4.24 | $223k |
| [−0.05, −0.025) | 204 | 36 | 2.35 | +4.91 | $100k |
| **[−0.025, 0)** | 170 | 36 | **2.65** | +7.24 | $123k |
| **[0, 0.025) — DEAD** | 121 | 40 | **1.67** | +3.56 | $43k |
| [0.025, 0.05) | 72 | 47 | **3.72** | +16.25 | $117k |
| ≥ 0.05 | 154 | 47 | 3.28 | +12.49 | $192k |

**The dead zone is ONLY [0, 0.025) — a narrow, isolated notch.** [−0.025, 0) is healthy (PF 2.65 / +7.24%).
The whole negative side is strong (2.35–4.21; steepest-falling < −0.10 is the STRONGEST at 4.21), and
[0.025, 0.05) is the single best bucket (3.72 / +16.25%). Interpretation: it's NOT a symmetric U — it's
"any DECISIVE volume trend is good; only the barely-positive-flat drift [0, 0.025) is noise" (neither the
climactic-open-receding signal of a negative slope nor real acceleration).

**The precise cut — exclude just the [0, 0.025) notch:**

| cut | n | PF | avg% | net |
|---|---|---|---|---|
| no cut (capacity) | 1417 | 2.81 | +6.32 | $895k |
| **exclude [0, 0.025)** | 1296 | **2.98** | +6.58 | $852k |
| exclude [−0.025, 0.025) (wider) | 1126 | 3.05 | +6.48 | $729k |

Dropping the 121-trip notch lifts PF 2.81 → 2.98 keeping 91% of net. The wider band pushes PF to 3.05 but
sacrifices the GOOD [−0.025, 0) slice ($123k) — not worth it. **Refined capacity cut: `vol_slope < 0 OR
vol_slope ≥ 0.025` → PF 2.98 / 1296 trips / $852k.** vol_slope ≥ 0.025 stays the A+ dial (PF 3.43).

---

## F13 — Per-year: the 0.025 split is a YEAR-STABLE regime boundary; the [0, 0.025) notch is real but noisy

Testing whether the F12 notch holds annually, and reframing as `< 0.025` vs `≥ 0.025` (accelerating side).

**The [0, 0.025) notch — a real, recurring soft spot, but noise-dominated (tiny n):**

| year | n | PF | avg% |
|---|---|---|---|
| 2020 | 11 | 0.22 | −5.29 |
| 2021 | 34 | 1.25 | +1.15 |
| 2022 | 16 | 0.75 | −1.42 |
| 2023 | 7 | 0.24 | −3.34 |
| 2024 | 14 | 3.88 | +15.73 |
| 2025 | 28 | 2.53 | +9.36 |
| 2026 | 11 | 1.34 | +1.25 |

Bad/mediocre in **5 of 7 years** (2020/22/23 negative, 2021/26 barely positive) — the weakness IS repeated,
not a one-off. But 7–34 trips/year and two good years (2024/25) → low-conviction: worth EXCLUDING, but not a
reliable short.

**`≥ 0.025` (accelerating) vs `< 0.025` — the clean, stable split (both all-weather; accel wins EVERY year):**

| year | < 0.025 PF | < 0.025 avg% | **≥ 0.025 PF** | **≥ 0.025 avg%** |
|---|---|---|---|---|
| 2020 | 2.14 | +3.02 | **3.44** | +12.37 |
| 2021 | 1.38 | +1.31 | **1.76** | +4.48 |
| 2022 | 2.82 | +5.05 | **2.77** | +12.24 |
| 2023 | 1.94 | +2.83 | **2.98** | +14.10 |
| 2024 | 5.24 | +13.67 | **5.12** | +21.72 |
| 2025 | 2.84 | +5.53 | **5.12** | +18.85 |
| 2026 | 3.14 | +6.61 | **5.30** | +23.34 |
| **TOT** | 2.60 | +4.92 | **3.43** | +13.69 |

**`≥ 0.025` beats `< 0.025` on avg%/trade EVERY year (2–3×) and on PF in 6 of 7** (2022 ties). The 0.025
threshold is a genuine year-stable regime boundary — the real signal is "is volume ACCELERATING." So the
clean framing is a TWO-TIER system, not a notch-exclusion:

- **A+ tier `vol_slope ≥ 0.025`** (accelerating drive) → PF 3.43 / +13.7%/tr / 226 tr / best-in-class every year.
- **Base tier `vol_slope < 0.025`** → PF 2.60 all-weather (carried by the NEGATIVE slopes = climactic-open
  receding; the [0, 0.025) notch is its one soft spot — excluding it nudges the base to ~2.7).

**⭐ The 0.025 split is the vol_slope structure: accelerating (≥ 0.025) = the A+ book; the rest is a solid
all-weather base.**

---

## F14 — vol_slope shape, big-sample (filters OFF): the notch is REAL; the negative-side strength is a chg_1d INTERACTION

Bucketed vol_slope on the RAW 09:45 book (no day-scale filters, n=21,277, PF 1.31) — 10× the sample — and
compared the shape across three filter levels to separate real structure from filter artifacts.

**Fine buckets, RAW book:**

| bucket | n | win% | PF | avg% |
|---|---|---|---|---|
| < −0.10 | 2926 | 28 | 1.36 | +0.44 |
| [−0.10, −0.05) | 7879 | 31 | **1.14** | +0.18 |
| [−0.05, −0.025) | 3981 | 34 | 1.19 | +0.28 |
| [−0.025, 0) | 2768 | 36 | 1.38 | +0.60 |
| **[0, 0.025)** | 1603 | 38 | **1.28** | +0.50 |
| [0.025, 0.05) | 932 | 41 | **1.70** | +1.60 |
| [0.05, 0.10) | 781 | 36 | 1.50 | +1.36 |
| ≥ 0.10 | 407 | 39 | **1.94** | +2.75 |

**Shape across filter levels (PF by bucket):**

| bucket | RAW | chg_1d ≥ 0.20 | full book |
|---|---|---|---|
| < −0.05 | 1.20 | 2.11 | **3.07** |
| [−0.05, 0) | 1.27 | 1.97 | 2.50 |
| **[0, 0.025)** | **1.28** | **1.69** | **1.67** |
| [0.025, 0.10) | 1.60 | 2.02 | **3.46** |
| ≥ 0.10 | 1.94 | 2.90 | 3.36 |

Two structural facts, now sample-robust:

1. **The [0, 0.025) notch is REAL, not small-n noise** (F12/F13 hedged this). It's the weakest positive-adjacent
   bucket in ALL THREE books — a genuine local minimum at barely-positive-drift volume. Correcting F13: the
   notch is a real structural dip, not a fluke.
2. **The negative side's strength is a chg_1d INTERACTION, not standalone.** On the RAW book, negative slopes
   are the WEAKEST (< −0.05 = 1.20, [−0.10, −0.05) = 1.14 the single worst). Only after `chg_1d ≥ 0.20` do
   they jump (2.11 → 3.07 in the full book). "Receding volume is +EV" holds ONLY conditional on a big day-mover
   (climactic open cooling off on a stock already up +20%); on a random stock, falling volume is a mild
   negative. The day-filter lifts negative-slope PF ~2.5× — far more than it lifts the positive side.

**Synthesis:** the positive/accelerating side (≥ 0.025) is a clean monotone lever EVERYWHERE (raw, filtered,
all years — F13); the [0, 0.025) drift is a real local soft spot; and the negative side is only strong
*inside* the day-strength book (an interaction). The F13 two-tier operational split (≥ 0.025 A+ / < 0.025
base) stands — this just explains WHY: the base tier's edge comes from the day-filter × receding-volume
interaction, not from falling volume per se. Next: entry-minute sweep (09:46…10:30).
