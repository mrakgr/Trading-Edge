# OpeningDriver — results

An **opening-drive study** (long-only, branch `opening-driver`), forked from DipRiderV4 for its clean
feature-folding but with the arm/re-arm state machine and breakout timers deleted. The engine is a pure
recorder: for each candidate day it emits **every bar in the entry window [open+15, open+60] = [09:45, 10:30]
as a trip**, held to MOC or a 9-EMA stop. All gate/feature analysis is done POST-HOC on the emitted CSV
(analyzed via DuckDB column projection — never loading the full multi-hundred-MB CSV into memory).

**Recorded feature levers:** 20m log-ATR (ATR%), 20m OLS log-price slope, 20m OLS log-volume slope, chg_1d
(entry vs prev daily close), chg_3d (entry vs close 3d ago), and `rvol_cum` (cumulative session volume vs
the 20d-average pace through the entry bar). $10k notional/trip. Era slices (run separately to keep each CSV
manageable); the MODERN era is 2020-01 → 2026-06.

**Universe = `vwap_reclaim_candidate` — the SHARED momentum pool.** DipRiderV4, VwapReclaimV3, and
OpeningDriver all read this SAME table, so the candidate day-pool is identical across the three momentum
systems. It is `mr_candidate` (median 1m-bar vol 09:30–09:45 ≥ **10k** AND ≥ 10 bars; CS/ADRC; day-close
≥ $1) pruned by the two VwapReclaim Layer-1 filters:
- **$30M dollar-ADV floor** — `avgvol20 * day_close ≥ $30,000,000` (20-day avg DOLLAR volume; VwapReclaim
  Finding 20: $30M is the floor, $100M over-cut).
- **premarket+15m ≥ 1× 20d ADV** — `rvol_0945 > 1.0`, where `rvol_0945 = vol_0945_pm / avgvol20` and
  `vol_0945_pm` is the premarket-inclusive volume 04:00 → 09:45 (genuinely in play into the open).

(All three of these — 10k median bar vol, $30M ADV, premarket+15m ≥ ADV — are the same liquidity/in-play
requirements the user confirmed for the momentum book; the median-bar & rvol windows are the first 15m to 09:45.)

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
interaction, not from falling volume per se.

---

## F15 — vol_slope is a STEP FUNCTION at 0.025, not monotone (corrects F14's "clean monotone" claim)

Fine grid straddling 0.025 (raw 09:45 book, no day-scale filters, n=21,277) — testing step vs ramp on
avg%/trade. **The FULL table (note the trip distribution — the `< −0.05` bucket alone is 10,805 of 21,277
trades, ~51%; the book is dominated by falling-volume names):**

| bucket | n | win% | PF | avg%/tr | net |
|---|---|---|---|---|---|
| < −0.05 | 10805 | 30 | 1.20 | +0.25 | $274,734 |
| [−0.05, −0.025) | 3981 | 34 | 1.19 | +0.28 | $109,938 |
| [−0.025, −0.01) | 1845 | 36 | 1.55 | +0.82 | $150,930 |
| [−0.01, 0.0) | 923 | 35 | 1.09 | +0.15 | $14,230 |
| [0.0, 0.01) | 770 | 37 | 1.06 | +0.11 | $8,355 |
| [0.01, 0.02) | 570 | 39 | 1.50 | +0.94 | $53,812 |
| [0.02, 0.025) | 263 | 40 | 1.38 | +0.70 | $18,493 |
| **[0.025, 0.03)** | 242 | 41 | **1.89** | **+2.22** ← STEP | $53,648 |
| [0.03, 0.04) | 387 | 41 | 2.18 | +2.14 | $82,887 |
| [0.04, 0.05) | 303 | 41 | 1.15 | +0.40 | $12,186 |
| [0.05, 0.075) | 495 | 36 | 1.65 | +1.65 | $81,487 |
| [0.075, 0.10) | 286 | 37 | 1.29 | +0.87 | $25,010 |
| ≥ 0.10 | 407 | 39 | 1.94 | +2.75 | $112,036 |

**It's a STEP, not a monotone ramp** (user call). avg%/trade sits at +0.1 to +0.9 for every bucket BELOW
0.025, then the first bucket ABOVE it jumps to **+2.22** — a ~3× discontinuity on adjacent same-width
buckets, mirrored in PF (1.38 → 1.89). Within each segment it's WEAKLY monotone / noisy:
- **Below 0.025:** low plateau, noisy (+0.1 to +0.9), no strong trend. NOTE the mass is here — the raw book
  is dominated by falling-volume (< −0.05) names (51% of trips), which sit near breakeven UNFILTERED (PF 1.20,
  avg% +0.25) and only become strong once the day-strength filter is applied (F14 interaction).
- **Above 0.025:** high plateau (+0.4 to +2.75), noisy sub-buckets (small n 240–500 each) — don't over-read
  the wiggles; the STEP itself is the robust feature (holds in PF, avg%, AND the filtered books of F13).

**This corrects F14** ("clean monotone positive side" — wrong) and reframes the [0, 0.025) "notch": it is NOT
a special dip, it's just the TOP of the LOWER plateau, which looks weak only next to the post-step buckets.
Model: **avg%/trade ≈ a step function, riser at ~0.025** (volume ACCELERATING past a threshold), weak/noisy
monotonicity on either side.

**Threshold choice (user):** the riser is around 0.025, but **0.01 or even 0 are defensible cut points too** —
below the step the plateau is low-but-noisy, so exactly where you draw the line in [0, 0.025) is a judgement
call, not a sharp knee. `0.025` (F13's split) is good enough and cleanly on the high side of the riser. The
0.025 two-tier split remains the operationalization — it's cutting at the step riser. Next: entry-minute
sweep (09:46…10:30).

## F16 — sess-ema-low stop DISTANCE: sub-3% = no stop room = near-instant scratch (not a "larger stops help" lever)

Ported the DipRiderV4 stop-distance study to OpeningDriver's sess-ema-low stop: `stop_dist_pct = (entry −
sess_ema_low)/entry`. On the 09:45 capacity book (n=1417, PF 2.81, mean 4.9% / median 3.7%). But unlike
DipRiderV4 (a clean floor lever), here the distribution STRADDLES ZERO — and the shape reveals a mechanical
issue, not a quality gradient.

**Three regimes (by stop-exit rate + hold time — the tell):**

| regime | n | win% | PF | avg% | stop-exit% | avg hold |
|---|---|---|---|---|---|---|
| **sd < 0** (entry BELOW sess-EMA-min) | 390 | 42 | 3.77 | +2.72 | **95%** | **23m** |
| **sd ∈ [0, 3%)** (near the floor) | 253 | 22 | 3.33 | +6.40 | 76% | 120m |
| **sd ≥ 3%** (real stop room) | 774 | 44 | 2.62 | +8.11 | 39% | 264m |

**The sub-3% entries have no stop room and get scratched almost instantly.** When the entry sits at/below the
session-EMA-min, the stop (9-EMA < sess-min) is at or above entry, so it trips on the next tick — 95%
stop-exit, 23-min holds for sd<0. Their high PF (3.77) is MISLEADING: tiny wins (+2.72%) with fast scratches
that rarely blow up — these aren't trades, they're the setup being REJECTED by the stop before the drive
plays out. **45% of the raw book (643 of 1417) is these no-room scratches.**

Only `sd ≥ 3%` is the REAL-TRADE book (39% stop-exit, 264-min holds, +8.11%/trade to MOC). This inverts the
naive floor-sweep reading: a moderate floor *lowers* headline PF (2.81 → 2.62) ONLY because it removes the
anomalous near-zero scratch deciles — but it's removing junk, not edge.

**Yearly (09:45 book + stop_dist ≥ 3% — the real book), all-weather:**

| year | n | PF | avg% | net |
|---|---|---|---|---|
| 2020 | 90 | 2.62 | +7.11 | $64k |
| 2021 | 189 | 1.39 | +2.33 | $44k |
| 2022 | 82 | 2.53 | +7.77 | $64k |
| 2023 | 55 | 2.44 | +6.56 | $36k |
| 2024 | 113 | 4.09 | +15.66 | $177k |
| 2025 | 159 | 3.60 | +11.60 | $184k |
| 2026 | 86 | 2.50 | +6.80 | $58k |
| **TOTAL** | **774** | **2.62** | +8.11 | $628k |

**KEY LESSON (mirrors the DipRiderV4 hasRoom/3%-skip decision):** an entry with NO stop room below it should
be SKIPPED — sub-3% stop distance on the sess-ema-low reference = the 9-EMA is already at/below its session
floor = an instant scratch, not a real position. The engine currently has no such guard (unlike DipRiderV4's
`hasRoom` + 3% skip), so 45% of the book is scratch-noise. **The clean opening-drive book requires `stop_dist
≥ 3%`** → 774 tr / PF 2.62 / +8.11%/tr / 264-min holds, all-weather. NEXT: build this room-guard into the
engine (skip sub-3% entries) like DipRiderV4, then re-confirm.

## F17 — bars-since-9EMA-high/low: the best drives are EARLY & PULLING BACK, not the most extended

Added two features: bars since the 9-EMA last made a new session HIGH / LOW (0 = the entry bar set it, +1/bar
after). On the 09:45 real-trade book (stop_dist ≥ 3%, n=774, PF 2.62). At 09:45 they're near-mirrors
(bars_since_high median 0 = entering as it makes new highs; bars_since_low median 14 = bottomed near the
open) — but the SPREAD within each predicts outcome, and it's slightly counter-intuitive: extended ≠ better.

**bars_since_ema_HIGH (how recently a new high):**

| bucket | n | win% | PF | avg% |
|---|---|---|---|---|
| **0 (chase the exact high)** | 506 | 46 | **2.37** | +7.02 |
| 1 | 55 | 45 | 3.24 | +10.47 |
| 2–3 | 56 | 45 | **3.67** | +12.07 |
| 4–6 | 56 | 39 | 2.32 | +6.00 |
| 7–15 (stale/re-break) | 101 | 32 | 3.25 | +11.23 |

`bh = 0` (the biggest bucket) is the WEAKEST — buying the exact new-high tick underperforms. `bh ≥ 1` (any
pullback off the high) = PF 3.14 vs bh=0's 2.37. **Enter a hair after the high, on a small dip — don't chase.**

**bars_since_ema_LOW (how long since the low):**

| bucket | n | win% | PF | avg% |
|---|---|---|---|---|
| **< 5 (fresh bounce off the low)** | 39 | 36 | **3.99** | +15.65 |
| 5–9 | 116 | 41 | 3.43 | +12.19 |
| 10–12 | 137 | 41 | 2.48 | +8.64 |
| 13–14 | 128 | 44 | 3.22 | +11.04 |
| **15 (low at the open, extended)** | 354 | 47 | **2.01** | +4.68 |

`bl < 15` (low NOT at the open) = PF 3.08 vs `bl ≥ 15` (climbing since the open, extended) = PF 2.01. The
extended late-drive is the weak pocket; the fresh bounce (bl < 5) is the strongest.

**They STACK — a strong A+ cell from these two alone:**

| cell | n | win% | PF | avg% | net |
|---|---|---|---|---|---|
| book (stop_dist ≥ 3%) | 774 | 44 | 2.62 | +8.11 | $628k |
| **bl < 13 & bh ≥ 1 (fresh low + pullback)** | 111 | 32 | **3.84** | +14.96 | $166k |

All-weather (positive every year, 2021 = 2.28, 2024 = PF 9.76 / +46%). **KEY: the best opening-drive entries
are EARLY & PULLING BACK, not the most extended** — a stock that bottomed recently (fresh drive, room left)
entering on a small dip off its high (not chasing the exact high tick). The extended drives (bottomed at the
open, climbing 15+ bars, buying the new-high tick) have already made most of their move. Caveat: the combined
cell has a LOWER win rate (32%) but higher PF — a fatter-tailed book (fewer, bigger winners), fine for momentum.

## F18 — bars_since_ema_low focused: DROP the "bottomed-at-open" pile (bl=15); fresher low = monotone better

Focused breakdown of `bars_since_ema_low` on the 09:45 real-trade book (n=774, PF 2.62). (Note on the value 0:
in the RAW firehose `bl=0` is the biggest bucket — the 9-EMA making a new session low that bar. But WITHIN the
drive book `bl ≥ 1` — a stock up +20% on the day never makes a fresh 9-EMA session low at 09:45 by
construction. So `min=1` in the book is a CONFIRMATION it selects genuine drives, not an off-by-one.)

**Per-value — one value dominates: `bl=15` is 46% of the book at the WEAKEST PF:**

| bl | n | PF | avg% | | bl | n | PF | avg% |
|---|---|---|---|---|---|---|---|---|
| 2–3 | 19 | ~8 | +30 | | 10 | 31 | 4.38 | +14.96 |
| 4–5 | 32 | 1.15–3.46 | +0.9–10.5 | | 11–12 | 106 | 1.85–2.22 | +5–8 |
| 6–7 | 52 | 3.59–4.78 | +12–18 | | 13–14 | 128 | 2.67–3.97 | +9–13 |
| 8–9 | 50 | 2.51–2.84 | +6–13 | | **15** | **354** | **2.01** | **+4.68** |

`bl=15` (the 9-EMA bottomed at the very OPEN, 09:30, and climbed all 15 bars to 09:45) is the single biggest
pile AND the weakest — the most EXTENDED drives, least room left. Every other value (bl<15 = the low was set
LATER, i.e. an intraday dip/reset gave the drive fresh legs) averages better.

**Ceiling sweep — fresher low = monotone better:**

| ceiling | n | PF | avg% | net |
|---|---|---|---|---|
| book (bl ≤ 15) | 774 | 2.62 | +8.11 | $628k |
| **bl < 15** | 420 | **3.08** | +11.00 | $462k |
| bl < 10 | 155 | 3.58 | +13.06 | $202k |
| bl < 7 | 81 | **4.17** | +15.57 | $126k |
| bl < 5 | 39 | 3.99 | +15.65 | $61k |

**The cleanest single cut is `bl < 15`** — just drop the bottomed-at-open pile: PF 2.62 → 3.08 on 420 trips,
all-weather (2020 2.82, 2021 1.34, 2022 3.14, 2023 3.27, 2024 5.66, 2025 4.35, 2026 3.34). Tighten to `bl < 7`
(PF 4.17) for an A+ cell. **The extended drive (9-EMA never dipped since the open) is the weak pocket; a drive
that put in a LATER low — an intraday reset — has more room and is the better buy.**

## F19 — bl freshness × vol_slope: the effect COMPOUNDS on the rising-volume side (A+ pocket)

Split the 09:45 real-trade book at `vol_slope = 0.01` (a gentle acceleration cut; the F13/F15 step is at
0.025) and ran the `bl` (bars-since-low) freshness sweep on BOTH sides. The freshness effect holds on both,
but is dramatically stronger and cleaner on the RISING-volume side.

**RISING volume (vol_slope ≥ 0.01, n=272, PF 2.90) — `bl` is monotone and steep:**

| bl cut | n | win% | PF | avg% |
|---|---|---|---|---|
| all | 272 | 46 | 2.90 | +11.66 |
| bl < 15 | 170 | 45 | 3.36 | +14.59 |
| bl < 13 | 125 | 47 | 3.81 | +16.56 |
| bl < 10 | 53 | 49 | 4.89 | +20.70 |
| **bl < 7** | 16 | 56 | **7.28** | +30.36 |
| bl = 15 (extended) | 102 | 48 | 2.12 | +6.78 |

Clean monotone climb PF 2.90 → 7.28, win rate RISING (46 → 56%). Fresh drive + accelerating volume = premium.

**FALLING/FLAT volume (vol_slope < 0.01, n=502, PF 2.41) — effect present but weaker/noisier:**

| bl cut | n | win% | PF |
|---|---|---|---|
| all | 502 | 43 | 2.41 |
| bl < 15 | 250 | 39 | 2.83 |
| bl < 7 | 65 | 32 | 3.42 |

Directionally right (bl<15 → 2.83, bl<7 → 3.42) but bumpy (bl<13 dips to 2.34) and win rate FALLS (43→32%) —
a fatter-tailed, second-tier version. The `bl=15` weak pocket is weak on BOTH sides (rising 2.12, falling
1.94) — the bottomed-at-open extended drive is the universal weak spot regardless of volume.

**⭐ A+ POCKET (rising-vol + fresh-low): `vol_slope ≥ 0.01 & bl < 13`** → PF 3.81 / +16.56%/tr / 125 tr /
$207k. All-weather AND firms the adverse year (2021 = 2.13 vs the base book's 1.34); 2024 = PF 8.15 / +33.8%.
The two features are COMPLEMENTARY and strongest together on the rising-volume side — a fresh-low drive with
accelerating volume is the best opening-drive cell found. (Tighten to `bl < 10` for PF 4.89 / +20.7%.)

## F20 — buying pullbacks (bars_since_ema_high) × vol_slope: real in aggregate but REGIME-DEPENDENT (unlike bl)

Split the 09:45 book at vol_slope 0.01 and swept `bh` (bars-since-high; bh=0 = at the new high, bh≥1 = N bars
into a pullback off it). The pullback beats chasing the high on BOTH sides in AGGREGATE:

| side | bh=0 (at high) | bh≥1 (pullback) | bh∈[1,3] (shallow) |
|---|---|---|---|
| RISING vol (≥0.01) | PF 2.68 (221 tr) | **4.07** (51 tr) | 3.29 (38 tr) |
| FALLING/FLAT (<0.01) | PF 1.99 (285 tr) | **2.91** (217 tr) | **3.56** (73 tr) |

The pullback lift is complementary to `bl`: `bh` matters MOST on the FALLING/flat side (bh=0 is weak there,
1.99 — chasing an UNCONFIRMED high; the shallow pullback fixes it to 3.56), whereas `bl` freshness compounded
on the RISING side (F19). Intuition: with rising volume, buying the high is fine (volume confirms
continuation); with flat/falling volume, the exact high is a chase, so wait for the pullback.

**BUT — the pullback cut is NOT all-weather (the key caveat):**

| shallow pullback (bh∈[1,3]) | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOT |
|---|---|---|---|---|---|---|---|---|
| both sides PF | 1.34 | 3.73 | **0.67** | **0.61** | 4.26 | 4.94 | 6.49 | 3.45 |
| falling-vol PF | **0.72** | **0.91** | 1.87 | 1.55 | 12.77 | 6.12 | 3.82 | 3.56 |

**2022-2023 are NEGATIVE years** for the pullback cut (both-sides 0.67 / 0.61; falling-side 2020-2021 also
negative). The high aggregate PF (3.45) is carried by 2024-2026 papering over losing years — 10-27 trips/year
is thin. **This is a DIFFERENT quality of signal from `bl`:** `bl` freshness was positive EVERY year (robust,
monotone); `bh` pullback is REGIME-DEPENDENT. Mechanism: in a choppy/low-momentum tape (2022-23) a "pullback"
keeps pulling back (it's a reversal, not a dip); in strong-momentum years (2024-26) it's a genuine dip-buy. So
the pullback edge is CONDITIONAL on a strong-momentum regime. **Verdict: `bl` (freshness) is the reliable
timing lever; `bh` (pullback) adds aggregate PF but is not trustworthy standalone — don't lean on it alone.**

## F21 — Yearly robustness of the bl variants: `bl < 15` is the trustworthy cut; tighter is overfit

Per-year PF(n) for the `bl` (bars-since-low freshness) cuts on the 09:45 real-trade book. The aggregate PF
rises as you tighten, but the yearly view shows the tight cuts are small-sample noise.

| cut | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---|---|---|---|---|---|---|---|
| base (all bl) | 2.6 | 1.4 | 2.5 | 2.4 | 4.1 | 3.6 | 2.5 | 2.62 (774) |
| **bl < 15** | 2.8 | 1.3 | 3.1 | 3.3 | 5.7 | 4.3 | 3.3 | **3.08 (420)** |
| bl < 13 | 3.1 | 1.6 | 3.0 | 2.6 | 6.7 | 3.1 | 2.6 | 3.02 (292) |
| bl < 10 | 8.6 | 1.7 | 4.2 | 2.1 | 6.8 | 4.2 | **0.5** | 3.58 (155) |
| bl < 7 | 18.2 | 1.0 | 7.2 | 2.7 | 11.2 | 4.1 | **0.2** | 4.17 (81) |
| bl = 15 (weak pile) | 2.3 | 1.5 | 1.7 | 1.2 | 2.1 | 2.9 | 1.8 | 2.01 (354) |

Three robustness reads:
- **`bl < 15` is the sweet spot** — improves or holds vs base in EVERY year, positive everywhere (2021 = 1.3,
  the only sub-2, same as base). Broad lift (2022 +0.6, 2023 +0.9, 2024 +1.6, 2025 +0.7), not concentrated.
  **The reliable cut: PF 3.08 / 420 tr / all-weather.**
- **Tighter than bl<13 is OVERFIT** — counts collapse to single digits/year and PF gets erratic. `bl < 7`'s
  4.17 aggregate is carried by 2020 (18.2, 7 tr) & 2024 (11.2, 10 tr), but **2026 BREAKS (0.2)** — the tightest
  cut fails in the most RECENT year, a red flag for forward trading. Don't lean on bl<10 or tighter standalone.
- **`bl = 15` is weak EVERY year** (2.3/1.5/1.7/1.2/2.1/2.9/1.8 — never > 2.9) — the cleanest robustness result
  in the study. The bottomed-at-open extended drive is a UNIVERSALLY weak pocket; dropping it is unambiguous.

**Rising-vol side (vs ≥ 0.01):**

| cut | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---|---|---|---|---|---|---|---|
| rising, all bl | 2.4 | 1.7 | 2.4 | 2.6 | 3.9 | 4.2 | 4.7 | 2.90 (272) |
| rising & bl<15 | 2.5 | 1.4 | 3.1 | 4.0 | 5.4 | 6.7 | 4.5 | 3.36 (170) |
| rising & bl<13 | 2.6 | 2.1 | 3.2 | 2.7 | 8.1 | 5.2 | 3.0 | 3.81 (125) |

`rising & bl<15` is the best-populated strong cell (170 tr, positive every year bar a soft 2021 = 1.4);
`bl<13` firms 2021 (2.1) but thins the other years. **VERDICT: `bl < 15` is the production freshness cut
(all-weather, robust); the rising-vol × bl<15 is the A+ overlay. Tighter bl is overfit — the aggregate PF
is a small-sample mirage.**

## F22 — Correction to F20: the BROAD `bh ≥ 1` pullback cut IS all-weather (the [1,3] band was small-sample)

F20 judged the pullback effect "regime-dependent" from the NARROW `bh ∈ [1,3]` band (which had losing 2022/23).
But that was a small-sample artifact — the BROAD `bh ≥ 1` (ANY pullback off the high) cut is well-populated
(268 tr) and positive EVERY year:

| cut | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---|---|---|---|---|---|---|---|
| **bh ≥ 1 (any pullback)** | 2.4 | 2.0 | 2.2 | 2.0 | 4.7 | 4.5 | 3.3 | **3.14 (268)** |
| bh = 0 (at the high) | 2.7 | **1.1** | 2.6 | 2.6 | 3.8 | 3.1 | **2.1** | 2.37 (506) |

`bh ≥ 1` is all-weather (min 2.0) and DOMINATES bh=0 in exactly the years bh=0 is weakest (2021: 2.0 vs 1.1;
2026: 3.3 vs 2.1). "Wait for ANY pullback off the high" is a real, robust improvement over "buy the exact
high" — comparable in robustness to `bl < 15`.

**Side split (the F20 asymmetry holds, but note the population):**

| cut | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | TOTAL |
|---|---|---|---|---|---|---|---|---|
| rising & bh≥1 | 3.0(4) | 5.1(15) | 0.0(3) | 2.7(4) | 3.3(10) | 4.8(14) | -(1) | 4.07 (51) |
| falling & bh≥1 | 2.4 | 1.4 | 3.2 | 1.6 | 5.4 | 4.5 | 2.2 | 2.91 (217) |

`rising & bh≥1` has the highest aggregate PF (4.07) but is THIN (51 tr, 1-15/yr, erratic per-year) — don't
trust it standalone. `falling & bh≥1` is the well-populated version (217 tr) — positive most years (soft 2021
1.4, 2023 1.6). This confirms F20's mechanism: the pullback does its real WORK on the FALLING/flat side (where
bh=0 = chasing an unconfirmed high), and there it's broad enough to trust.

**Revised verdict (supersedes F20's caution): BOTH `bl < 15` (freshness) AND `bh ≥ 1` (any pullback) are
robust, all-weather timing cuts.** The earlier "regime-dependent" call was specific to the narrow bh∈[1,3]
band, not the pullback feature itself. Use `bh ≥ 1` broadly (not the tight band); the rising-vol combos stay
A+ overlays but are too thin to lean on per-year.

## F23 — BOTH timing cuts (`bl < 15 & bh ≥ 1`): additive AND more robust than either alone

Requiring both robust timing cuts — a FRESH drive (bl<15) entered on a PULLBACK off the high (bh≥1):

| cut | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | PF | win% | avg% | n |
|---|---|---|---|---|---|---|---|---|---|---|---|
| base | 2.6 | 1.4 | 2.5 | 2.4 | 4.1 | 3.6 | 2.5 | 2.62 | 44 | +8.1 | 774 |
| bl < 15 only | 2.8 | 1.3 | 3.1 | 3.3 | 5.7 | 4.3 | 3.3 | 3.08 | 41 | +11.0 | 420 |
| bh ≥ 1 only | 2.4 | 2.0 | 2.2 | 2.0 | 4.7 | 4.5 | 3.3 | 3.14 | 39 | +10.2 | 268 |
| **BOTH** | 2.5 | 2.0 | 2.6 | 2.3 | 5.9 | 6.1 | 4.4 | **3.65** | 34 | **+13.8** | 146 |

**The combination is more ROBUST, not just stronger.** BOTH is positive every year with a min of **2.3 (2023)**
— a HIGHER floor than either component (bl<15 dips to 1.3 in 2021; bh≥1 to 2.0). It firms 2021 specifically
(2.0 vs base 1.4). The two timers catch DIFFERENT weak-setup failure modes, so requiring both filters the
adverse-regime junk each alone admits. Interpretation: a fresh drive (room to run) entered on a small pullback
(better fill, not chasing) = the ideal opening-drive entry.

**Caveat:** win rate falls to 34% — a fatter-tailed book (fewer, bigger winners; +13.8% avg confirms). Fine
for momentum, matters for sizing.

**Vol split (don't over-slice):** BOTH & rising = PF 7.07 but only 23 tr (1-2/yr some years) — too thin to
trust per-year. BOTH & falling = 123 tr / PF 3.02, positive most years.

**⭐⭐ THE OPENING-DRIVE A+ CELL: `bl < 15 & bh ≥ 1`** (on top of the settled book: 09:45, ATR%≥0.013,
chg_1d≥0.20, chg_3d∈[0,1.5], stop_dist≥3%) → PF 3.65 / +13.8%/tr / 146 tr / $201k, all-weather (min 2.3),
firms the adverse year. Fresh drive + pullback entry = the cleanest, most robust cell in the study — the two
timing features stack without the vol split.
