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

## Finding 3 — OLS `slope_per_atr` (speed) confirms the speed-cap thesis but does NOT beat `dist/atr` as a swap — CEILING helps, FLOOR does not

Added 20m OLS log-price & log-volume slopes (`OlsSlopeMa(20)`, mirroring DipRiderV3/BreakoutTimer — fed on the
same valid-prior-close bars as `atrLog` so x-axes align) and derived **`slope_per_atr` = price-slope / log-ATR**
= speed-normalized momentum. Recorded-only. Verified byte-identical on all 47 shared columns (41,027 trips / PF
1.298 unchanged); new columns no-NaN. Motivation (user): `dist/atr` is essentially a CAP on runs that rose too
FAST; an OLS price-slope-per-ATR is a cleaner, more principled version of the same idea, and might replace both
`dist/atr` and `tightness`.

**Distribution** on the base (`updn≥1.3 & run_max_dist≥3.5% & rvol15m<2`, no d/atr, n=402): slope/atr min −0.08,
p10 0.03, p50 0.145, p90 0.29, max 0.54.

**Buckets — the speed-cap thesis holds (high slope/atr = worse), but it's non-monotone:**

| slope/atr | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| [−∞, 0) | 24 | 62.5 | 5.37 | +25.5 |
| [0.00, 0.05) | 30 | 60.0 | 3.74 | +11.9 |
| **[0.05, 0.10)** | 69 | 55.1 | **6.01** | +24.3 |
| [0.10, 0.15) | 88 | 38.6 | 2.16 | +8.3 |
| [0.15, 0.20) | 74 | 47.3 | 3.53 | +15.0 |
| [0.20, 0.30) | 81 | 43.2 | 2.18 | +7.1 |
| [0.30, +∞) | 36 | 50.0 | 3.13 | +6.9 |

Slow/controlled reclaims (even slightly-negative slope) run hardest to MOC (PF 5–6, +24–25%); fast rises
(≥0.20) fade to PF 2.2/+7%. **A CEILING is indicated; a FLOOR is NOT** — the lowest bucket is the best, so
banding `[0,X)` cuts the good part.

**Ceiling sweep vs the incumbent — close, but d/atr still wins:**

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| INCUMBENT `d/atr<3` (no floor) | 232 | 50.4 | **4.38** | 20.3 |
| swap `slope/atr<0.18` (drop d/atr) | 256 | 50.0 | 3.94 | 16.9 |
| swap `slope/atr<0.20` | 285 | 49.1 | 3.68 | 15.7 |
| band `slope/atr∈[0,0.20)` | 261 | 47.9 | 3.52 | 14.8 |

The ceiling monotonically improves PF as it tightens (3.34 @ <0.30 → 3.94 @ <0.18), confirming the mechanism —
but at matched breadth `d/atr<3` still beats it (4.38 vs 3.94, +20.3% vs +16.9%). **As a straight 1:1 swap,
slope/atr LOSES.**

**The real open question — can slope/atr let us DROP tightness? — needs an ENGINE run.** All fat-book rows are
already engine-gated at `tightness≥3` (min observed 3.06), so it can't be un-gated post-hoc. If `slope/atr-ceiling
+ NO-tightness ≈ d/atr + tightness`, we'd trade two features for one (a real simplification). That is the next
test: a `--min-tightness 0` engine run with the slope/atr ceiling applied post-hoc. VERDICT so far: keep d/atr;
slope/atr is a promising CEILING whose value is TBD pending the tightness-replacement test.

## Finding 4 — ⭐ TIGHTNESS is REDUNDANT once you have `d/atr` — dropped for free; slope/atr stacks additively. TIGHTNESS NOW OFF BY DEFAULT.

Ran the fat book with `--min-tightness 0` (46,241 trips vs 41,027 with tight≥3 — the gate was removing ~5,200
low-range names) to test whether the speed features make the tightness gate redundant.

**Tightness is dead weight once `d/atr` is present:**

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| `d/atr<3` **& tight≥3** (incumbent A+) | 232 | 50.4 | 4.38 | 20.3 |
| `d/atr<3` **& NO tightness** | 235 | 50.6 | **4.42** | 20.4 |

Same trips, same PF, same avg — `d/atr<3` already removes everything tightness removed. **DECISION: tightness
is OFF BY DEFAULT in VwapReclaimV2 from here on.** (base for all rows below = `updn≥1.3 & run_max_dist≥3.5% &
rvol15m<2`, tightness OFF.)

**CEILING sweeps on the no-tightness book** (capping fast rises — the speed-cap thesis):

| d/atr ceiling | n | PF | avg% | | slope/atr ceiling | n | PF | avg% |
|---|---:|---:|---:|---|---|---:|---:|---:|
| d/atr<2.5 | 184 | 5.01 | 22.9 | | slope/atr<0.15 | 218 | 3.76 | 16.1 |
| d/atr<3.0 | 235 | 4.42 | 20.4 | | slope/atr<0.18 | 263 | 3.96 | 17.0 |
| d/atr<3.5 | 274 | 3.85 | 17.9 | | slope/atr<0.20 | 292 | 3.70 | 15.9 |

**STACK both ceilings — additive (orthogonal denominators):**

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| `d/atr<3` alone | 235 | 50.6 | 4.42 | 20.4 |
| `slope/atr<0.18` alone | 263 | 50.2 | 3.96 | 17.0 |
| **`d/atr<3 & slope/atr<0.18`** | 195 | 53.8 | **4.96** | 22.1 |
| **`d/atr<3 & slope/atr<0.20`** | 210 | 52.4 | 4.79 | 21.4 |
| `d/atr<2.5` alone | 184 | 51.6 | 5.01 | 22.9 |

The stack beats either cap alone (PF 4.96 vs 4.42/3.96) — `d/atr` normalizes depth by run-vol, `slope/atr`
normalizes trend by trailing-vol; different denominators catch different bad trades. Caveat: `d/atr<2.5` alone
(PF 5.01) nearly matches the stack, so part of the stack's gain is "cut harder," not purely new information.
NEXT: these were CEILINGS; test FLOORS on d/atr and slope/atr (a minimum-speed gate — the reclaim needs some
momentum) now that tightness is gone.

## Finding 5 — FLOORS on d/atr and slope/atr do NOT help — the edge is a CEILING on both axes; the SLOWEST reclaims are the best

Tested the hypothesis that after removing tightness we want a FLOOR (minimum speed — "the reclaim needs some
momentum"): `d/atr > 0.5` or `slope/atr > 0.06`. Base = `updn≥1.3 & run_max_dist≥3.5% & rvol15m<2`, tightness
OFF (n=409). Base distribution: d/atr p10=1.4 / p50=2.69; slope/atr p10=0.029 / p50=0.144.

**d/atr FLOOR — a no-op below ~0.7, then monotone DEGRADATION:**

| filter | n | PF | avg% |
|---|---:|---:|---:|
| no floor | 409 | 3.35 | 13.3 |
| d/atr > 0.5 | 409 | 3.35 | 13.3 |
| d/atr > 1.0 | 395 | 3.24 | 12.8 |
| d/atr > 2.0 | 299 | 2.70 | 9.5 |

**slope/atr FLOOR — actively HARMFUL:**

| filter | n | PF | avg% |
|---|---:|---:|---:|
| no floor | 409 | 3.35 | 13.3 |
| slope/atr > 0.06 | 340 | 3.18 | 12.6 |
| slope/atr > 0.10 | 279 | 2.59 | 9.5 |

**Bands confirm the floor does nothing** — `d/atr (0.5,2.5)` = PF 5.01 is identical to the `d/atr<2.5` ceiling
alone (the 0.5 floor removes 0 trips); every slope/atr band is worse than the d/atr ceiling.

**MECHANISM: for a VWAP reclaim, LESS speed is better.** Both features say the same thing from opposite ends —
a reclaim that grinds back slowly & controlled (low slope, moderate depth-per-vol) is a healthier continuation
than one that snaps back violently. Consistent with F3's buckets (the `slope/atr<0` bucket was PF 5.4, the
`≥0.20` tail PF ~2.2). **The edge is a CEILING on both axes, with NO floor.** Requiring a minimum speed removes
exactly the slow-grinding winners.

**VERDICT:** no floors. Keep the speed CEILINGS (`d/atr<3`, optional `slope/atr<0.18` stack), tightness OFF. The
settled V2 A+ direction: `updn≥1.3 & run_max_dist≥3.5% & rvol15m<2 & d/atr<3` (≈ PF 4.42), optionally + slope/atr
ceiling for a tighter/higher-PF cell.

## Finding 6 — added trailing-20m MIN 9-EMA + EMA-climb depth. It does NOT replace `run_max_dist` (VWAP reference is load-bearing). But swapping the PULLBACK atr for the ROLLING-20m log-ATR in d/atr IMPROVES it.

Added `emaMin20 = MinMa(20)` fed `ema.State` each bar (later a stop basis, like DipRiderV3/BreakoutTimer).
Derived **`ema_climb` = (9-EMA − 20m-min-9-EMA) / 9-EMA** as an alt depth `d`, and both ratios `ema_climb/run_atr`
(pullback atr) and `ema_climb/log_atr` (rolling-20m atr). Also computed `run_max_dist/log_atr` to isolate the ATR
choice. Recorded-only; 50 shared CSV columns byte-identical (46,241 trips, tightness OFF), no NaNs. NOTE:
`price_slope_20` is LOG-space (OLS of `log close`) and `slope_per_atr` divides it by the LOG-ATR — unit-consistent.
The incumbent `d/atr` uses the PULLBACK atr (`run_atr` = mean log-TR over the run bars), NOT the rolling window.

**(1) EMA-climb does NOT replace `run_max_dist` — ~1.5 PF worse even doing both jobs (floor+ceiling):**

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| INCUMBENT `run_max_dist≥3.5% & run_max_dist/run_atr<3` | 235 | 50.6 | **4.42** | 20.4 |
| `ema_climb≥0.015 & ema_climb/run_atr<3` | 687 | 42.9 | 2.66 | 8.5 |
| `ema_climb≥0.020 & ema_climb/run_atr<3` | 485 | 44.7 | 2.93 | 11.2 |
| `ema_climb≥0.015 & ema_climb/log_atr<3` | 699 | 42.3 | 2.60 | 8.2 |

**Mechanism:** `run_max_dist` measures depth against **VWAP** (the session's volume-weighted fair value — a
MEANINGFUL level), reset per run. `ema_climb` measures the EMA against **its own trailing-20m min** — a local,
self-referential floor. A big VWAP dislocation is a real dip-buy; a big climb off a 20m EMA-min is just "the EMA
rose recently" — far less selective (3× the trips at half the PF, +8% vs +20% avg). **The VWAP reference is
load-bearing; the EMA-climb throws it away.** (The 20m-min-9-EMA is still worth keeping for the STOP-basis test.)

**(2) ⭐ Swapping the pullback `run_atr` → rolling-20m `log_atr` in d/atr IMPROVES it:**

| `d/atr` denominator | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| pullback `run_atr` (incumbent) | 235 | 50.6 | 4.42 | 20.4 |
| **rolling `log_atr` < 2.5** | 156 | 49.4 | **4.65** | 22.4 |
| rolling `log_atr` < 2.0 | 96 | 51.0 | 4.13 | 20.5 |
| rolling `log_atr` < 3.0 | 231 | 49.8 | 3.87 | 17.9 |

Higher PF and avg at fewer trips. Beyond the marginal PF bump, the rolling-20m log-ATR is the SAME denominator
`slope/atr` already uses AND that DipRiderV3/BreakoutTimer use — switching makes `d/atr` coherent across all
features/systems (drops the bespoke run-reset `run_atr` accumulator). **Candidate: redefine V2 `d/atr` =
`run_max_dist / log_atr` (rolling), ceiling ~2.5.** Open: yearly stability of the log-ATR variant vs pullback.

## Finding 7 — CORRECTION to F6(2): the rolling-log-ATR d/atr does NOT beat the pullback run_atr — the "win" was a BREADTH ILLUSION

F6 claimed `run_max_dist/log_atr < 2.5` (PF 4.65) beat the incumbent `run_max_dist/run_atr < 3` (PF 4.42). But
that compared 156 trips to 235 — better PF at 33% fewer trips is not a win, it's just a tighter cut. Dialed out
to MATCHED breadth:

| book | n | win% | PF | avg% | net$ |
|---|---:|---:|---:|---:|---:|
| **INCUMBENT `run_max_dist/run_atr < 3` (pullback)** | 235 | 50.6 | **4.42** | 20.4 | 480k |
| `run_max_dist/log_atr < 2.5` (rolling) | 156 | 49.4 | 4.65 | 22.4 | 350k |
| `run_max_dist/log_atr < 3.0` (rolling) | 231 | 49.8 | 3.87 | 17.9 | 413k |
| `run_max_dist/log_atr < 3.25` (rolling) | 253 | 49.0 | 3.93 | 18.1 | 458k |

**At matched trips (~231–235) the pullback ATR WINS by ~0.5 PF and ~$67k net** (4.42 vs 3.87). The rolling
log-ATR only looked better because it cut harder. **F6(2) is RETRACTED — keep the pullback `run_atr` in d/atr.**

**Mechanism:** `run_atr` is the RUN'S OWN realized volatility (the pullback that just happened), so
`run_max_dist / run_atr` asks "how deep was this dip relative to how volatile THIS dip was." The rolling-20m
log-ATR is a generic, slower volatility estimate that doesn't track the run's own character. For a
depth-of-THIS-run measure, the run's own ATR is the more natural — and empirically better — denominator. (The
coherence argument for log-ATR still stands for `slope/atr`, which has no run-scoped analogue — but d/atr keeps
`run_atr`.) Net of F6+F7: neither the EMA-climb depth nor the rolling ATR improves `d/atr`; the incumbent
`run_max_dist / run_atr` stands.

## Finding 8 — 15m/30m rolling log-ATR windows also LOSE to the pullback run_atr in d/atr (longer window is better, but none beats it at matched breadth)

Added 15m & 30m rolling mean log-TR (`AvgMa(15)`, `AvgMa(30)`) to retry the d/atr-denominator swap with
different windows. `run_max_dist / rolling-atrN` ceiling sweeps, matched to the incumbent's ~235 trips:

| denominator | matched cell | n | PF | avg% |
|---|---|---:|---:|---:|
| **pullback `run_atr < 3`** (INCUMBENT) | — | 235 | **4.42** | 20.4 |
| rolling 15m log-ATR < 3.0 | | 245 | 3.60 | 16.5 |
| rolling 20m log-ATR < 3.0 | (F7) | 231 | 3.87 | 17.9 |
| rolling 30m log-ATR < 3.0 | | 221 | 3.95 | 18.3 |

**No rolling window beats the pullback ATR at matched breadth.** There IS a clean monotone trend — longer window
= better (15m 3.60 → 20m 3.87 → 30m 3.95) — but even 30m lands ~0.5 PF below the incumbent. The 30m at a tighter
cut (`<2.25`, n=138) reaches PF 4.38, but only by dropping 40% of trips (the same breadth illusion as F7).

**Why longer is better yet still loses:** a longer ATR window is smoother/slower, closer to a stable per-name
volatility baseline — but it's still a GENERIC estimate, not the run's OWN realized vol. `run_atr` is scoped to
THIS specific dip, which is the right normalizer for a depth-of-this-run measure. **VERDICT: d/atr keeps the
pullback `run_atr`. The rolling-ATR window sweep (15/20/30) confirms F7 — none is a real improvement.**

## Finding 9 — ⭐ ema_climb/atr is a BETTER quality feature than slope/atr, and it STACKS with d/atr (4.42→5.13). (As a QUALITY gate it works; as a DEPTH replacement it failed in F6.)

Repeated the ema_climb experiment but as a QUALITY feature (keep `run_max_dist≥3.5%` as the depth as usual), head
-to-head vs `slope/atr`. Base = `updn≥1.3 & rvol15m<2 & run_max_dist≥3.5%` (n=409, PF 3.35, tightness off).

**slope/atr vs ema_climb/atr — ema_climb wins at every matched breadth:**

| feature | n | PF | avg% | | feature | n | PF | avg% |
|---|---:|---:|---:|---|---|---:|---:|---:|
| slope/atr<0.10 | 130 | 5.33 | 21.4 | | ema_climb/run_atr<1.5 | 114 | **6.03** | 25.3 |
| slope/atr<0.18 | 263 | 3.96 | 17.0 | | ema_climb/run_atr<2.5 | 281 | **4.02** | 17.4 |
| slope/atr<0.20 | 292 | 3.70 | 15.9 | | ema_climb/log_atr<2.5 | 262 | **4.36** | 19.0 |

Highly correlated (corr slope/atr ↔ ema_climb/run_atr = 0.81 — same "reclaim speed per unit vol" idea), but
ema_climb does it better ⇒ **slope/atr is likely REDUNDANT once ema_climb/atr is in.**

**ema_climb/atr STACKS with the incumbent d/atr (not redundant — corr 0.68):**

| book | n | win% | PF | avg% |
|---|---:|---:|---:|---:|
| INCUMBENT `d/atr<3` | 235 | 50.6 | 4.42 | 20.4 |
| `ema_climb/run_atr<2.5` alone | 281 | 48.4 | 4.02 | 17.4 |
| **`d/atr<3 & ema_climb/log_atr<2.5`** | 210 | 53.3 | **5.13** | 22.9 |
| **`d/atr<3 & ema_climb/run_atr<2.0`** | 172 | 53.5 | 5.07 | 22.9 |

**The reframe vs F6:** as a DEPTH measure (replacing run_max_dist) ema_climb FAILED — the VWAP reference is
load-bearing (F6). But as a QUALITY/SPEED gate on top of the run-distance floor it's the best feature found —
"how far the EMA lifted off its recent floor per unit vol" = a cleaner speed-of-reclaim axis than the OLS slope,
consistent with the F3/F5 speed-cap (slower reclaims win, so a ceiling).

**⚠ CAVEAT (learned from F6/F7): the PF-6 cell is THIN & LUMPY.** Yearly of `ema_climb/run_atr<1.5` (n=114):
2020 PF 30.8 (n=9, +82% avg), 2023 PF 16.5 (n=7) — two low-n monster years inflate it; 2021/2024/2025 are
3.66/2.05/3.75. **The trustworthy number is the STACKED ~210-trip cell (PF 5.13), broader and still clearly >
4.42.** NEXT: yearly stability of the stacked cell; then decide whether ema_climb/atr REPLACES slope/atr (corr
0.81) in the V2 A+ = `updn≥1.3 & run_max_dist≥3.5% & rvol15m<2 & d/atr<3 & ema_climb/log_atr<2.5`.
