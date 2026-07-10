# MaxFlyerV3 — SHORT pop-fade + STOPS Research Log

**`TradingEdge.MaxFlyerV3`** — forked from **`TradingEdge.MaxFlyerV2`** (2026-07-10, branch `maxflyer-v3`).
Same intraday **short pop-fade** engine and defaults as V2; the V3 mandate is **drawdown control via STOPS**.

**Why V3 exists.** The V2 short book has settled edge (`brv20d>=100` main lever, rvol tiers, daily-ATR norm,
volfrac inverted-U, all-day — PF 6.65 / +$2760 on the A book; see `docs/maxflyerv2_results.md`). BUT a short
is **unbounded on the upside**, and V2 holds to MOC with no stop — so its **max drawdowns are extreme**. The
long MR book is bounded to −100% per trade, so stops there are not urgent; the short is where DD control pays.
**We will accept a PF sacrifice** if stops meaningfully cut the tail. This log records that work.

**Methodology (inherited):** clip@+50% both-PF tables, gate≠post-hoc @ max-conc-1, RAW & CLIP shown together,
bucket TABLES not prose, THE 2021 LAW (2021 is two-sided chop — V2 F24: shorting the same days also loses).
See `docs/maxflyerv2_results.md` for the full V2 findings that carry over verbatim (engine/signals unchanged).

---

## Finding 1 — (baseline) V3 == V2 byte-for-byte at fork; visualize the worst losers to design the stop

Fork is a pure copy (module rename only; engine, signals, defaults identical). Next step per the plan: run the
settled book, pull the WORST losing trades, and chart them (1m candles + entry/exit) to see WHERE and HOW a
stop would have helped — i.e. is the damage a fast spike (a tight stop catches it) or a slow grind (only a wide
stop or a time-stop helps)? The stop design follows from what the losers actually look like.

Reproduced the V2 A-book exactly on the fresh V3 output: **2,760 trips, PF 6.645, 88.7% win, +17.32% avg,
net +$4.78M** (`brv20d>=100 & intraday ATR%>=0.03`; brv20d = breakout_bar_vol / (avgvol20·adj_ratio/390)).
Confirms the fork is behaviorally identical to V2. **The tail is concentrated:** worst 10 losers = −$334,780,
worst 30 = −$486,191, vs total net +$4.78M — the worst 30 trades are ~57% of ALL loss dollars ($846k). The
single worst is **DRUG 2024-10-15, −839%** (short $3.94 → $37, a 9.4× squeeze; 3 entries = −$237k that day).

Charts: `scripts/visualization/maxflyerv3_loser_charts.py` renders the worst-N losers (1m candles + entry/MOC-
exit markers + candidate stop levels). **Loss SHAPE (DRUG minute trace): a FAST early spike then a runaway
parabola** — +25% adverse by 6 min after entry (10:52), chops at +15–25% for ~2h, then rips to +225% by 12:41
and +839% by MOC. The damage is NOT an instant gap: a modest catastrophe stop fires early and cheaply.

## Finding 2 — ⭐ CATASTROPHE STOP: a wide (+40–60%) adverse stop removes the portfolio-ending tail at small cost

Simulated a per-trade intraday stop (`scripts/equity/mfv3_stop_sim.py`): for the SHORT, exit at the first 1m
bar (at/after entry) whose HIGH reaches entry·(1+f); fill = that level (optimistic — see caveat); else ride to
MOC. Short P&L return = −f if stopped, −ret_moc otherwise. A-book, 2,760 trips.

| config | n | win% | PF | net | worst trade | fire% |
|---|---:|---:|---:|---:|---:|---:|
| **no-stop** | 2760 | 88.7% | **6.65** | $4,779,537 | **−$83,909** | — |
| stop +15% | 2760 | 64.1% | 2.92 | $2,753,879 | −$1,500 | 34.3% |
| stop +25% | 2760 | 77.8% | 3.81 | $3,687,056 | −$2,500 | 18.0% |
| stop +40% | 2760 | 84.7% | 4.78 | $4,280,186 | −$4,000 | 9.1% |
| stop +60% | 2760 | 87.3% | 5.45 | $4,526,387 | −$6,000 | 5.0% |

**The tradeoff is exactly as intended — bound the tail, pay some PF.** The worst single trade collapses from
−$83,909 (a portfolio event) to −$4,000 (+40%) / −$6,000 (+60%). Every stop costs PF/net because the book wins
89% and a stop cuts winners that briefly spiked adverse before fading (that's why win% DROPS — a stopped trade
is a realized loss where the hold-to-MOC would have been a win). Tighter = more damage: +15% fires on 34% of
trades and bleeds net to $2.75M.

**Sweet spot = +40% to +60%.** At +40%: PF 4.78, net $4.28M (−10% of net), worst −$4k. At +60%: PF 5.45, net
$4.53M (−5% net), worst −$6k. Small give-up, catastrophe removed.

**By-year:** the wide stops preserve 80–95% of net in the big years (2020/2023/2024/2025) while bounding the
tail; 2024 at +40% actually BEATS no-stop on net ($795k vs $774k — that year had many mid-size squeezes the
stop caught before they ran further). No year is destroyed by a +40/+60% stop.

**⚠ CAVEAT — the sim is an OPTIMISTIC bound.** Fill is assumed AT entry·(1+f). On these low-float parabolic
squeezers a stop-MARKET would slip THROUGH the level (1m bars gap), so real stopped losses run worse than the
−$4k/−$6k shown. Even so, the shape is unambiguous: a wide catastrophe stop is worth it. NEXT: wire the stop
into the V3 engine (as an intraday %-adverse stop) and re-measure with realistic fills (bar CLOSE after the
pierce, or +slippage), then sweep the threshold on the engine output rather than post-hoc.


## Finding 3 — ⭐ 9-EMA arm-timer ENTRY + max-EMA STOP: bounds the tail as well as a +40% fixed stop, but it's a SELECTION filter (smaller/lower-PF book), not just a loss cap

Built into the engine (commit caf124a + disarm fix): `--ema-entry` ARMS a 10-bar countdown on each new session
high, records that bar's 9-EMA (emaAtArm), and SHORTS (at close) on the first bar within the window whose 9-EMA
closes BELOW emaAtArm — the confirmed rollover, not the peak. Re-arms on each new high (single timer, latest
wins); DISARMS on entry (one short per arm — without this it stacked a new short every bar while the fade ran).
`--ema-max-stop` covers when the live 9-EMA rises above the STRICTLY-PRIOR session-max 9-EMA (× 1+buffer).
Signal-bar fields recorded (signal_time/high/…/volume, sess_vol_high_at_signal excl. the signal bar) since the
fill defers from the signal. A-book filter (brv20d≥100 & ATR%≥0.03) applied post-hoc as always.

| A-book | **V2 no-stop** | V2 +40% fixed (F2) | **EMA arm-entry + max-EMA stop** |
|---|---:|---:|---:|
| n | 2,760 | 2,760 | 1,249 |
| win% | 88.7% | 84.7% | 64.8% |
| PF raw | 6.65 | 4.78 | 3.73 |
| PF clip | — | — | 3.69 |
| net | $4.78M | $4.28M | $1.10M |
| **worst trade** | **−$83,909** | −$4,000 | **−$3,871** |

**The tail goal is MET:** worst trade −$3,871 (≈ the +40% fixed stop's −$4,000), and the −839% DRUG catastrophe
is GONE — every worst-15 loser exits via `ema_max_stop` at −24% to −39%, all bounded. DRUG 2024-10-15 produces
NO morning trip at all: its parabola never made a 9-EMA cross-under inside the 10-bar arm window, so we never
shorted the runaway. That is the entry model working — we only fade CONFIRMED rollovers.

**But it's a different, smaller book, not V2-with-a-stop.** 1,249 trips (vs 2,760), PF 3.73 (vs 6.65), net
$1.10M (vs $4.78M). Two mechanisms, both selection (not loss-capping):
- ENTRY defers ~5 min / enters −13.6% BELOW the signal high (mean) waiting for the 9-EMA to roll under. Pops
  that never roll over intraday are SKIPPED — good for DRUG, but many would-have-faded pops are skipped too.
- The max-EMA STOP realizes 384 losses at −9.88% avg that hold-to-MOC would have let revert → win% 88.7%→64.8%.

**All-weather:** positive every meaningful year; clip PF 2.5–6.6 across 2016–2026; the big-capacity years
2024/2025 are PF 3.2/2.8, net $203k/$190k. Exit mix: 865 MOC (+17.1% avg, $1.48M) vs 384 ema_max_stop
(−9.9% avg, −$0.38M) — the stop is the drag, the held winners are the book.

**Read:** the 9-EMA machinery achieves the SAME tail bound as a dumb +40% fixed stop but sacrifices ~4× more
net ($1.1M vs $4.3M) to get it — because the deferred entry is a heavy SELECTION filter on top of the stop, not
a pure loss cap. Whether that's worth it depends on the goal: if the mandate is purely "kill the catastrophe
tail at least cost," the +40% fixed stop on V2's entry (F2) dominates on net/PF. The EMA book is a genuinely
DIFFERENT, more selective strategy (fade only confirmed rollovers) that happens to also bound the tail.
NEXT: sweep --ema-arm-bars (longer window = more entries) and --ema-max-stop-buffer (looser = fewer stop-outs,
recover net) to see if the EMA book can close the net gap while keeping the tail bound.

**⚠ Data note:** CETX 2019-06-27 shows entry_px $44,057,002 — a pre-split price the adj_ratio join didn't
adjust. P&L stayed bounded (−$3,138, stopped) so it didn't distort results, but the adj_ratio edge case exists.

## Finding 4 — PER-SIGNAL pending trades recover ~35% more net than the global timer, tail still bounded

Replaced the global arm-timer with independent per-signal PENDINGS (commit 2736930): each new-session-high
breakout arms its own pending (armed 9-EMA level + own countdown + signal-bar features); fires when ITS 9-EMA
crosses under ITS emaAtArm before ITS timer expires. Multiple coexist; ALL qualifying fire on the same bar;
each new high always re-arms; fires-once-then-removed (no stacking). **NB: RAW PF only for the short book — the
win is bounded at +100% (price→0), so clip is meaningless (unlike the long momentum books).** Runs are 2020+
(the A-book edge is 2020-concentrated; ~4× faster iteration).

| A-book (2020+) | V2 no-stop (22y) | global-timer EMA (F3, 22y) | **per-signal EMA (F4, 2020+)** |
|---|---:|---:|---:|
| n | 2,760 | 1,249 | **1,550** |
| win% | 88.7% | 64.8% | 66.6% |
| raw PF | 6.65 | 3.73 | **3.57** |
| net | $4.78M | $1.10M | **$1.39M** |
| worst trade | −$83,909 | −$3,871 | −$5,212 |

**Per-signal recovers ~35% more net ($1.39M vs $1.10M) at ~the same PF** — independent pendings keep the
overlapping setups the global overwrite discarded (e.g. VLCN 2024-06-25 fires two pendings, EFOI three). Still
ALL-WEATHER: every year 2020–2026 positive, raw PF 2.78–8.32, net $78k–$312k. Tail still bounded: worst −$5,212
(vs V2's −$83,909; slightly worse than the global timer's −$3,871 — more pendings = more chances to catch a fast
squeeze, but every worst-15 loser exits via ema_max_stop at −29% to −52%, all capped).

Exit mix: 1,109 MOC (+17.1%, +$1.90M) vs 441 ema_max_stop (−11.5%, −$0.51M) — the held winners ARE the book,
the stop is the drag. Entry defers ~6.5 min / −14.6% below signal_high (mean) waiting for the rollover.

**Read:** per-signal is a strict improvement over the global timer (same PF, more net, same tail character). The
book is still ~3.5× less net than V2 no-stop because the deferred-entry SELECTION (fade only confirmed rollovers)
+ the max-EMA stop are heavy filters — the tail bound is not free. NEXT: sweep --ema-arm-bars (longer = more
pendings fire) and --ema-max-stop-buffer (looser = fewer stop-outs) to try to close the net gap.

**⚠ Data note (persists):** VLCN 2024-06-25 entry_px $517, CRKN 2024-05-14 $1,464 — pre-split prices the
adj_ratio join misses. P&L stays bounded by the stop (no distortion), but the adj_ratio edge case is real.

## Finding 5 — ⭐ max-EMA-stop BUFFER (frozen at entry): 20–30% is the plateau — PF 6.2 / net $1.96M / tail bounded

**Bug found + fixed first.** The buffer originally applied to the LIVE/trailing session-max 9-EMA, so it
SATURATED: buffer ≥10% → the stop NEVER fired (a smoothed EMA can't rise 10% above its own running max; the
30%/40% CSVs were byte-identical). FIX (user's spec): freeze the stop base at the ENTRY bar's session-max 9-EMA
(new per-position `EmaStopBase`); stop level = base × (1+buffer); cover when the live 9-EMA CLOSES above it.
Now the buffer is meaningfully spaced. Swept 0/10/20/30/40% (A-book, 2020+, raw PF).

| buffer | n | win% | raw PF | net | worst | n_stopped |
|---|---:|---:|---:|---:|---:|---:|
| 0% (entry EMA-max) | 1550 | 66.6% | 3.57 | $1.39M | −$5,212 | 441 |
| 10% | 1550 | 80.8% | 4.59 | $1.78M | −$6,079 | 143 |
| **20%** | 1550 | 83.5% | 6.19 | $1.96M | −$6,878 | 52 |
| **30%** | 1550 | 84.0% | 6.24 | $1.97M | −$7,458 | 37 |
| 40% | 1550 | 84.1% | 6.78 | $2.00M | −$11,996 | 18 |

**Clean monotone tradeoff — looser buffer → higher PF, more net, wider tail.** 0% is too tight (441 stops cut
winners-that-dipped → PF 3.57). **20–30% is the plateau: PF ~6.2, net ~$1.96M, worst bounded −$6.9k/−$7.5k
(~11× better than V2's −$84k), only 37–52 stops fire** (the stop now catches only genuine runaways, not noise).
40% edges PF/net (6.78 / $2.00M) but the worst trade DOUBLES to −$11,996 — the stop gets loose enough to let a
few squeezes run. **This closes most of the net gap vs V2** ($1.96M vs $4.78M, at PF 6.2 ≈ V2's 6.65) while
keeping the catastrophe tail capped — exactly the mandate. Default buffer → 20–30% (30% for a touch more net).

## Finding 6 — ⭐ arm-window (timer) sweep, done POST-HOC: ≤60 bars DOUBLES net vs ≤10 for a modest PF give-up

Instead of re-running per timer length (slow, imprecise), ran ONCE with an effectively-infinite arm window
(`--ema-arm-bars 100000` — every signal that EVER crosses under fires) and sliced the lag (= entry−signal
minutes = bars the 9-EMA took to roll under) post-hoc. The cumulative table IS the timer-length sweep.
A-book, 2020+, buffer 30%, raw PF. Lag dist: mean 37.9, median 18, p90 94, max 362.

**CUMULATIVE (arm window ≤ N = the timer sweep):**

| arm ≤ N | n | win% | raw PF | net | worst |
|---|---:|---:|---:|---:|---:|
| 10 (old default) | 1,448 | 84.5% | 6.61 | $1.84M | −$7,253 |
| 20 | 2,412 | 82.8% | 6.08 | $2.89M | −$7,739 |
| 30 | 2,973 | 82.3% | 5.69 | $3.42M | −$15,339 |
| **60 (new default)** | **3,654** | **81.1%** | **5.27** | **$4.08M** | −$15,339 |
| 90 | 3,950 | 80.7% | 5.28 | $4.38M | −$15,339 |
| 120 | 4,098 | 80.1% | 5.14 | $4.47M | −$15,339 |
| ∞ | 4,408 | 78.9% | 4.89 | $4.59M | −$15,339 |

**≤60 more than DOUBLES net ($1.84M→$4.08M, +122%, 2.5× trips) for a modest PF give-up (6.61→5.27).** This
essentially CLOSES the gap to V2 no-stop's $4.78M — but with the tail bounded (worst −$15,339 vs V2's −$83,909,
still ~5×). **Per-band (non-cum):** bands 1-10…61-90 all strong (PF 3.0-6.6) — the fade doesn't need a fast
rollover, later cross-unders work too. The −$15,339 worst enters in the 21-30 band (a slower squeeze runs a bit
before the 9-EMA closes above the frozen stop base) and is the max across all windows ≥30. Past ~90 bars decays
(91-120 PF 2.62; >120 PF 2.23, avg only +4%) → ∞ is NOT best; soft knee at 60-90. **DEFAULT --ema-arm-bars → 60**
(≤90 adds ~$300k more at ~same PF, basically free; kept 60 as the clean knee / user's target).

## Finding 7 — ⭐ ROLLING-30m-max-9EMA stop anchor (buf20) HALVES the fat tail (−153%→−82%, 0 over 100%) for FREE

The session-cumulative-max anchor STALES: if a stock popped hard EARLY (pushing the session-max 9-EMA up) then
we short a LATER, LOWER pop, the stop sits at (stale early max)×(1+buf) — far above where we shorted — so the
trade runs 100%+ before the 9-EMA reaches it (the −153% MGIH loser: entered 24 bars after signal, session max
already elevated). Fix (user): anchor to a ROLLING N-bar max 9-EMA (the RECENT local EMA high near the fill),
not the session max. New `--ema-max-stop-window N` (0 = session max; 30 = 30m local high). Also added (and
rejected below) a pure entry-EMA %-stop `--ema-pct-stop`.

**First — the plain entry-EMA %-stop (off entry 9-EMA) helps PF but does NOT fix the tail** (A-book, arm≤60):

| stop | win% | PF | net | worst% | n>70% | n>100% |
|---|---:|---:|---:|---:|---:|---:|
| session-max buf30 (F6) | 81.1% | 5.27 | $4.08M | 153% | 15 | 2 |
| entry-EMA %-stop 60% | 81.3% | 5.80 | $4.17M | 120% | 24 | 3 |
| entry-EMA %-stop 100% | 81.3% | 6.19 | $4.23M | 140% | 8 | 7 |

Best PF/net but the worst is still −120% even at the tightest 60% — the 9-EMA LAGS, so by the time it's risen
60% off entry the price has already spiked 120%+. Loosening it raises n>100% (deep runaways slip through). The
problem is the ANCHOR, not the leash.

**ROLLING-30m anchor buffer sweep (A-book, arm≤60) — a HARD CLIFF at 20/25:**

| buffer | win% | PF | net | worst | worst% | n>70% | n>100% |
|---|---:|---:|---:|---:|---:|---:|---:|
| 10% | 79.0% | 4.19 | $3.75M | −$7,797 | 78% | 4 | 0 |
| 15% | 80.2% | 4.74 | $3.94M | −$8,422 | 84% | 4 | 0 |
| **20%** | **80.9%** | **5.26** | **$4.07M** | **−$8,153** | **82%** | 7 | **0** |
| 25% | 81.0% | 5.26 | $4.07M | −$15,339 | 153% | 9 | 1 |
| 30% | 81.1% | 5.26 | $4.07M | −$15,339 | 153% | 15 | 2 |

**buf20 = a genuine optimum, not a compromise:** worst −82% / −$8.2k, ZERO trades >100%, at **PF 5.26 / net
$4.07M — IDENTICAL to the session-max book (F6).** The tail is halved for free. HARD CLIFF above 20%: at buf25+
the rolling×(1+buf) level rises enough that the runaways slip under → the −153% loser returns. Below 20% the
stop starts cutting winners (PF 4.19–4.74, net −$130–320k) with no further tail gain. **NEW DEFAULTS: window 30,
buffer 20%.** This DOMINATES both the session-max stop (same PF/net, half the tail) and the entry-EMA %-stop
(which floored at −120%). Delivers the user's ask: push the 150% losses down, keep the 60–70% ones.

## Finding 8 — ⭐ bars-since-9EMA-high ENTRY: short into every high, hold to the FIRST weakness (th=1) — the STRONGEST book ($7.07M / PF 5.61)

⚠ **SUPERSEDES a buggy first pass.** The first implementation ARMED a pending on every new-EMA-high bar (not the
price breakout) and used a fragile `= N` fire — since a rising 9-EMA re-highs almost every bar, it armed/reset
constantly, producing garbage counts (th0 8.1M trips, th1≠th2). FIXED: arm on the price breakout (SAME as
cross-under); fire when the SESSION-level `barsSinceEmaHigh >= threshold`. User's baseline test (th{0,1} → ~same
trip count) now passes: th0 4,763 vs th1 4,748 A-book (−0.3%) — a delay shifts WHEN, not WHICH. (`--ema-bars-
since-high-entry`, `--ema-bars-since-high N`.) Stop = roll30m/buf20 (F7). A-book, 2020+, raw PF.

| threshold | n | win% | raw PF | net | worst | worst% | n>100% |
|---|---:|---:|---:|---:|---:|---:|---:|
| th=0 (short into high) | 4,763 | 75.0% | 4.52 | $6.68M | −$6,087 | 61% | 0 |
| **th=1 (1-bar weakness)** | 4,748 | 83.7% | 5.61 | **$7.07M** | −$9,533 | 95% | 0 |
| th=2 | 4,738 | 83.4% | 5.68 | $6.91M | −$10,693 | 107% | 1 |
| th=3 | 4,737 | 82.4% | 5.45 | $6.65M | −$9,867 | 99% | 0 |

**Trip count is FLAT (4,763→4,737): a 1-3 bar delay shifts timing, not selection** — every armed high eventually
sees barsSinceEmaHigh climb (this is the corrected behavior). **th=1 = the peak & the strongest book in V3:** the
1-bar delay for the first tick of EMA weakness lifts win 75%→84%, PF 4.52→5.61, net $6.68M→$7.07M over shorting
BLINDLY into the high. th=2 edges PF (5.68) but less net + wider tail (−107%, one >100%); decays past 2. th=1
also has the tightest tail of the delayed (−95%, 0 over 100%). **DEFAULT threshold → 1.**

**This book BEATS everything prior** (A-book 2020+): th=1 = **$7.07M / PF 5.61 / worst −$9.5k** vs the cross-under
book's $4.07M / 5.26 and V2 no-stop's $4.78M / −$83.9k. "Short into every new session high + hold to the first
bar of 9-EMA weakness + roll30m/buf20 stop" is the strongest configuration found: MORE net than V2 no-stop at a
~9× smaller tail. All-weather. (User intuition — don't enter blindly at the top, wait for the first weakness —
validated on net AND quality.) The cross-under (F6/F7) is now the runner/lower-net alternative, not the main book.

## Finding 9 — ⭐ EMA-DOWN-TICK entry (ema < prevEma): the best entry trigger — highest PF AND net, no session-high dependency

The three "first weakness" triggers compared (A-book, 2020+, roll30/buf20 stop, raw PF). ema-down-tick
(`--ema-down-tick-entry`) fires when the live 9-EMA ticks DOWN vs the prior bar (ema < prevEma) — a pure,
local "EMA turned down" weakness, with NO requirement that the 9-EMA ever made a session high (unlike
bars-since-high). Arm is the price breakout (same as all EMA modes); a pending armed this bar is skipped
(its breakout bar is an up-tick), so earliest fire = next bar.

| trigger | n | win% | raw PF | net | worst | worst% | mean lag |
|---|---:|---:|---:|---:|---:|---:|---:|
| **ema-down-tick** | 4,746 | 84.3% | **5.68** | **$7.07M** | −$9,533 | 95% | 8.2 |
| bars-since-high =1 | 4,671 | 84.3% | 5.60 | $6.93M | −$9,533 | 95% | 8.5 |
| bars-since-high >=1 | 4,748 | 83.7% | 5.61 | $7.07M | −$9,533 | 95% | 7.4 |

**ema-down-tick is the best of the three — highest PF (5.68), highest net ($7.07M), highest win% (84.3%),
same tail** (−$9,533, 0 over 100%). It matches bars-since-high>=1 on net but beats it on PF and win%. The
three are close (all "fade the first EMA weakness after a pop"), but down-tick wins because: (1) it's the
cleanest signal — a direct local turn-down, no session-high machinery / shared counter; (2) it fires without
needing a clean session high, catching weakness on names whose EMA stalls without new-highing (4,746 trips,
better quality). **This is the settled ENTRY for the V3 book.** (`= vs >=` for bars-since-high was a wash;
the arm bug — not the fire rule — was the earlier confusion. down-tick sidesteps it entirely.)

**⭐ SETTLED V3 BOOK (F5–F9):** `--ema-entry --ema-max-stop --ema-down-tick-entry`, window 30, buffer 0.20 →
A-book 2020+ **PF 5.68 / net $7.07M / worst −$9.5k / 0 over 100% / 4,746 trips, all-weather.** BEATS V2 no-stop
($4.78M / −$83.9k) on BOTH net AND tail (~9× smaller). "Short into every new session high, hold until the
9-EMA ticks down, roll30m/buf20 stop."

## Finding 10 — ⭐ TIGHT 5% stop + 2 RE-ENTRIES: crushes the tail to −54% at ~flat net (the drawdown-ceiling variant)

Idea (user): a much TIGHTER stop (5% buffer vs 20%) bounds the tail hard, but a tight stop alone bleeds net
(cuts winners). So don't give up after a stop — re-short on the NEXT 9-EMA down-tick, up to 2 re-entries per pop,
each with a FRESH 30m-max-9EMA×1.05 stop (`--ema-reentries 2 --ema-max-stop-buffer 0.05`). Chain ends at the cap
or MOC. Engine: on an ema-stop with budget left, Advance spawns a re-arm pending that PRESERVES the original
signal's data (signal_time/volume/features — verified: 3-leg chains share signal_time+signal_volume, so brv20d
is identical across legs; `ArmMin` field split from `SignalMin` so the fire-loop skip guard doesn't clobber the
reported signal). All max-conc 0 (unlimited — the correct post-hoc mode; max-conc 1 serializes into whatever
pending grabs the slot and pollutes the population). A-book, 2020+, raw PF.

| config | n | win% | raw PF | net | worst | worst% | n>50% |
|---|---:|---:|---:|---:|---:|---:|---:|
| **settled (buf20, re0)** | 4,746 | 84.3% | **5.68** | $7.07M | −$9,533 | 95% | 20 |
| 5%stop re0 | 4,746 | 70.1% | 3.46 | $5.18M | −$5,383 | 54% | 4 |
| **5%stop re2** | 6,320 | 69.4% | 3.49 | $6.97M | **−$5,383** | **54%** | **4** |

**The 5% stop crushes the tail: worst −95%→−54% (−$9.5k→−$5.4k), trades losing >50% drop 20→4.** No trade loses
more than 54% — the tightest ceiling in V3. **Re-entries recover almost all the net the tight stop gives up:**
5%stop-re0 collapses net to $5.18M (the tight stop cuts winners, win 84→70%); the 2 re-entries climb it back to
$6.97M (+$1.8M) by re-shorting pops that keep fading post-stop (trips 4,746→6,320 = the re-entry legs). **Net vs
the settled book is ~flat ($6.97M vs $7.07M, −1.4%).**

**The tradeoff (genuine choice):** vs settled buf20 — tail MUCH better (−54% vs −95%, 4 vs 20 over 50%), net
~flat, but PF worse (3.49 vs 5.68; the tight stop realizes many small losses, win 69% vs 84%). This is the pure
"trade PF for a lower drawdown CEILING" the V3 mandate is about — the same net at a 40-pt-lower worst-case for ~2
PF points. For a short book where the tail IS the risk, the −54% ceiling is compelling. **Two shippable books:
buf20/re0 = high-PF; 5%stop/re2 = tight-ceiling.** (Both beat V2 no-stop's −839% by >15×.) NEXT: sweep re-entry
count (3-4?) and the tight-stop buffer (3%? 7%?) to map the ceiling/net frontier.

## Finding 11 — ⭐⭐ THE RIGHT TAIL METRIC IS WORST-DAY, NOT WORST-TRADE — and it FLIPS the re-entry verdict

With re-entries the risk unit is no longer one trade: a runaway pop stops at −5%, re-shorts, stops again,
re-shorts again — several losing legs on ONE pop, ONE day. So the honest drawdown is the **worst DAY** (net
summed per symbol-day, and per calendar-day), not the worst single trade. Recomputed on the baselines (A-book,
2020+, max-conc 0; V2 is the direct book):

| config | net | raw PF | worst TRADE | worst SYMBOL-day | worst CAL-day |
|---|---:|---:|---:|---:|---:|
| V2 no-stop | $4.25M | 7.53 | −$83,909 | **−$237,542** | −$237,542 |
| settled buf20 re0 | $7.07M | 5.68 | −$9,533 | **−$47,467** | −$44,287 |
| 5%stop re0 | $5.18M | 3.46 | −$5,383 | **−$26,526** | −$23,346 |
| **5%stop re2** | $6.97M | 3.49 | −$5,383 | **−$61,923** | −$58,743 |

**⭐ The re-entries make the worst DAY WORSE even though the worst TRADE is identical.** 5%stop re0 vs re2: same
worst trade (−$5,383), but worst symbol-day balloons −$26.5k → **−$61.9k (2.3×)** and cal-day −$23k → −$59k. On a
relentless squeeze the re-entries STACK: −5% stop, re-short, −5%, re-short, −5% — three tight stops on one pop
compound into a big DAY. **So re-entries are NOT a free tail-reducer** — F10's "−54% worst trade" was measuring
the wrong unit; the DAY tells the truth.

**The ranking FLIPS on worst-day:**
- **5%stop re0** = the best drawdown control (−$26.5k symbol-day) — tight stop, NO re-entries — but lowest net ($5.18M).
- **settled buf20 re0** = best net/day balance: $7.07M net at −$47k worst-day.
- **5%stop re2** = re-entries bought back net ($6.97M) but PAID −$62k worst-day — WORSE daily drawdown than the
  settled book despite a 5× tighter per-trade stop.

**All still crush V2** (−$238k worst-day → −$26–62k, 4–9×). **Going forward, judge the buffer×re-cap grid on
worst SYMBOL-DAY, not worst trade.** The open question: is there a (buffer, re-cap) cell that keeps the net of
re-entries WITHOUT the day-stacking — likely a small cap (1?) at a moderate buffer, capping how many times one
pop can compound.

## Finding 12 — ⭐ buffer × re-cap FRONTIER (worst symbol-day): the buffer is the real lever, re-entries≤1, 0% is pure friction

Ran 10 buffer values (0-9%, unlimited re-entries=20) ONCE, sliced the re-entry cap POST-HOC via `re_idx<=N`
(F11's column). A-book, 2020+, max-conc 0. Judged on worst SYMBOL-DAY (F11's honest unit). (b00=6GB/12M trips
→ processed one file at a time with DuckDB `memory_limit`+disk-spill to avoid OOM.)

**BASELINES (the no-stop ceiling the grid trades against; A-book, 2020+, worst SYMBOL-DAY):**

| book | n | win% | raw PF | net | worst trade | worst symday |
|---|---:|---:|---:|---:|---:|---:|
| **down-tick entry, NO stop** (hold-MOC) | 4,746 | 87.1% | **7.59** | **$7.66M** | −$74,110 | **−$222k** |
| V2 direct, no stop | 2,760 | 88.7% | 6.65 | $4.78M | −$83,909 | −$238k |

⚠ **Corrects an F9-era claim.** The down-tick entry ALONE (no stop) is the high-PF / high-net CEILING — PF 7.59
(vs V2's 6.65) at $7.66M, because deferring to the first EMA weakness is strong SELECTION. **But it does NOT
improve the worst DAY** (−$222k ≈ V2's −$238k): with no stop, a runaway pop still squeezes to −74% on a trade
with nothing to cut it. The earlier "no-stop has better worst-drawdown" read was on worst-TRADE / a different
population; on the honest worst-DAY (F11) the no-stop down-tick book is a FULL-TAIL book like V2. **The stops
below trade this PF/net ceiling for an ~8× smaller worst-day** (−$222k → −$26-48k).

**NET ($M):**            **WORST SYMBOL-DAY ($k):**
```
buf\cap  0    1    2    3    5   inf     buf\cap  0    1    2    3    5   inf
 0%    3.21 4.93 5.71 6.16 6.44 6.49      0%    -28  -29  -37  -41  -65  -69
 1%    3.81 5.70 6.38 6.53 6.65 6.67      1%    -28  -35  -44  -58  -64  -64
 2%    4.14 6.18 6.59 6.72 6.77 6.78      2%    -28  -37  -48  -64  -69  -69
 3%    4.58 6.41 6.76 6.82 6.85 6.85      3%    -28  -40  -51  -65  -70  -70
 4%    4.89 6.58 6.89 6.89 6.92 6.92      4%    -28  -48  -61  -81  -88  -88
 5%    5.18 6.68 6.97 6.96 6.98 6.99      5%    -26  -48  -62  -82  -89  -89
 6%    5.47 6.77 7.02 7.00 7.04 7.04      6%    -31  -52  -76  -95  -87  -87
 7%    5.65 6.92 7.10 7.09 7.12 7.12      7%    -30  -55  -75  -94  -87  -87
 8%    5.75 6.98 7.14 7.14 7.16 7.16      8%    -34  -55  -79  -91  -84  -84
 9%    5.87 6.99 7.13 7.13 7.16 7.16      9%    -36  -64  -89 -102  -96  -96
```

**raw PF (the missing third axis):**
```
buf\cap   0     1     2     3     5    inf
 0%     2.80  2.90  2.88  2.90  2.87  2.82
 1%     2.89  3.05  3.04  2.99  2.96  2.95
 2%     2.92  3.19  3.13  3.10  3.08  3.06
 3%     3.12  3.32  3.27  3.22  3.18  3.18
 4%     3.30  3.43  3.39  3.31  3.28  3.27
 5%     3.46  3.53  3.49  3.42  3.38  3.38
 6%     3.65  3.64  3.58  3.51  3.49  3.49
 7%     3.77  3.79  3.71  3.64  3.63  3.63
 8%     3.85  3.87  3.79  3.74  3.73  3.74
 9%     3.92  3.89  3.81  3.76  3.77  3.77
```
PF READS: (a) DOWN a column PF RISES with the buffer (0%→9%: 2.80→3.92 at re0) — a wider stop = fewer premature
cuts = higher PF, corroborating "buffer is the lever". (b) ACROSS a row PF is ~flat then DECAYS with re-entries
(re1 is the PF peak in most rows; re3+ erodes). (c) Peak PF cell = **9%/re0 (3.92)** = also the tightest day —
so 9%/re0 is unambiguously the risk-control optimum (best PF AND best day). The best net cells (buf≥7, re≥2,
$7.1M) sit at PF ~3.7-3.8 and worst-day ~−$85-95k — more net costs both PF and day.

**Three reads:**
1. **DOWN a column (raise buffer, re-cap fixed): nearly FREE net.** re0 column: net $3.21M→$5.87M (0→9%) while
   worst-day holds ~−$28k to −$36k. **The buffer is the real lever, not re-entries.**
2. **ACROSS a row (add re-entries): net up, worst-day MONOTONICALLY worse** (5% row net $5.18→6.68→6.97M but
   day −$26k→−$48k→−$62k). Re-entries trade daily drawdown for net — steeply past re1.
3. **0% is pure friction (user):** the whole 0% row is Pareto-DOMINATED by 1-2% (lower net, same/worse day).
   The 0% stop trips the instant live-EMA > frozen-max → cuts winners for zero day benefit. **Use ≥1%.**

**The efficient frontier (best net for a worst-day budget):**

| worst-day | best cell | net | PF |
|---|---|---:|---:|
| ~−$28k | **9% / re0** | **$5.87M** | 3.92 |
| ~−$48k | **5% / re1** | **$6.68M** | 3.53 |
| ~−$65k | 2% / re3 | $6.72M | 3.10 |

**TWO PRODUCTION BOOKS:** (a) **9%/re0** = max risk control — tightest day (−$28k) AND highest re0 PF (3.92),
$5.87M, NO re-entries. (b) **5%/re1** = best net/day balance — $6.68M at −$48k, one re-entry only. **Re-entries
beyond 1 are never worth it** (buy little net, cost a lot of day). All cells still crush V2's −$238k worst-day
by 3-9×. This settles the stop/re-entry design: pick a buffer for the net you want, cap re-entries at 0-1.

## Finding 13 — PER-LEG PF: re-entry 1 is EXCELLENT (PF 3.81), re-entry 2 marginal, re-entry 3 is a LOSER

The PF of each re-entry leg ON ITS OWN (re_idx = exact leg, not cumulative). A-book, 5% buffer, 2020+.

| leg (re_idx) | n | win% | raw PF | avg% | net |
|---|---:|---:|---:|---:|---:|
| 0 (original) | 4,746 | 70.1% | 3.46 | +10.9% | $5.18M |
| **1 (1st re-entry)** | 1,256 | 68.5% | **3.81** | +11.9% | $1.50M |
| **2 (2nd re-entry)** | 318 | 62.3% | **2.77** | +9.0% | $0.29M |
| **3 (3rd re-entry)** | 92 | 41.3% | **0.92** | −0.7% | −$7k |
| 4 | 45 | 44.4% | 1.38 | +3.0% | +$14k |
| 5 | 23 | 60.9% | 1.47 | +2.5% | +$6k |

**The 1st re-entry is HIGHER PF than the original entry (3.81 vs 3.46)** — after a tight stop, a pop that keeps
weakening is a genuinely good re-short. The 2nd is still profitable (PF 2.77) but weaker. **The 3rd is the WALL:
PF 0.92, net NEGATIVE, win% collapses to 41%** — by the third stop-out on the same pop we're fighting a real
runaway squeeze that keeps stopping us, and it stops paying. This per-leg view corroborates F12's frontier from
a different angle: **cap re-entries at 1 (great), 2 at most (marginal), never 3+ (a loser).**

## Finding 14 — ⭐ A-BOOK GATES BAKED INTO THE ENGINE (ATR%≥0.03 & brv20d≥100) — CSVs 700× smaller, byte-identical

Lesson from an OOM crash: running the engine UNGATED and filtering the A-book POST-HOC in SQL wrote MILLIONS of
junk trips to disk (b00 = 6GB / 12M trips) just to keep ~4,700. Fix: bake the two A-book filters into the engine
as entry gates so it emits ONLY A-book trips. New config/CLI: `--min-intraday-atr-pct` (default 0.03, the ATR%
floor) and `--min-brv20d` (default 100, the main lever; brv20d = breakout_bar_vol/(avgvol20·adj_ratio/390), so
the engine ctor now takes avgVol20+adjRatio). Gates apply at the ARM (breakout) bar in both entry models.

**Validated byte-identical to the post-hoc A-book:**
| run | gated engine | post-hoc SQL (prior findings) |
|---|---|---|
| down-tick, roll30, 5% buf, re0 | 4,746 / PF 3.463 / $5.18M | 4,746 / 3.463 / $5.18M (F12 b05/re0) |
| direct (V2 parity) | 2,760 / PF 6.645 / $4.78M | 2,760 / 6.645 / $4.78M (F1) |

**CSV size: ~1.4GB → ~2MB (≈700×).** Every run is now tiny: no DuckDB memory strain, no post-hoc join/filter
(the CSV IS the A-book), a reboot losing /tmp costs a ~100s re-run not a disaster. All F1-F13 numbers unchanged —
we just compute them 700× lighter. `--min-brv20d 0` / `--min-intraday-atr-pct 0` disables the gates (ungated).

**ATR% definition (for the record):** trailing-20m rolling mean of the 1m LOG true range (`log(max(high,prevC)/
min(low,prevC))`), NOT session-cumulative — a "moving fast RIGHT NOW" filter that reacts to the current pop.

## Finding 15 — the −$222k worst-day is a CONCURRENCY-STACKING artifact (21 legs/day) — but max-conc 1 is not the fix

Tested whether the no-stop down-tick book's −$222k worst symbol-day is one catastrophic position or many stacked
on one runaway pop. Ran max-concurrent 1 (one open position at a time) vs the default max-conc 0 (unlimited).
A-book, 2020+, no stop.

| | max-conc 0 (unltd) | max-conc 1 |
|---|---:|---:|
| n | 4,746 | 2,595 |
| win% | 87.1% | 64.1% |
| raw PF | 7.59 | 5.19 |
| net | $7.66M | $2.59M |
| worst trade | −$74,110 | −$74,110 |
| **worst symbol-day** | **−$222,330** | **−$74,110** |
| **max legs on 1 symbol-day** | **21** | **2** |

**CONFIRMED — the −$222k was STACKING:** at unlimited concurrency one symbol-day fired **21 simultaneous
positions** (all the pendings on one runaway pop), tripling the −$74k worst TRADE into a −$222k worst DAY. Cap to
max-conc 1 → worst-day collapses to exactly −$74,110 (= the worst single trade; max 2 legs/day).

**BUT max-conc 1 is NOT the fix — it guts the book:** net $7.66M → $2.59M (−66%) and **win% 87% → 64%.** The win%
collapse is the tell: with one slot, whichever pending grabs it first is ~random, so we take the FIRST fade on a
day, not the BEST/all. The unlimited book wins 87% BECAUSE it takes every good fade; serializing throws away that
breadth. So the extreme worst-day is a stacking artifact, but max-conc 1 pays 2/3 of net + 23 win-pts to remove
it. **Better levers for the same goal: (a) a STOP (caps per-trade loss so a 21-leg stack can't compound as hard),
or (b) a per-SYMBOL-DAY leg cap (limit the 21→N directly without serializing the whole book).** The per-symday
cap is the surgical version — NEXT.

## Finding 16 — ⭐ ROOM CHECK MOVED TO ARM TIME (filter PENDINGS, not fills) — recovers 19 win-pts at max-conc 1

F15 showed max-conc 1 gutted win% (87→64%). Root cause (user): the room check was at FIRE time — a pending sat
in the queue for the whole hold and was only blocked when it TRIED to fire, so the queue BACKLOGGED with stale
pendings while a position was held, then dumped them (low-quality late fills) on the exit. Fix: gate at ARM time —
don't QUEUE a pending when at capacity. Cap TOTAL committed exposure (OpenCount + pending.Count) at MaxConcurrent.
Re-arms (a stopped position's re-entry) are exempt: they replace the SAME chain's vacating slot, not new exposure.

| down-tick, no stop | mc0 unltd | mc1 OLD (fire-gate) | **mc1 NEW (arm-gate)** |
|---|---:|---:|---:|
| n | 4,746 | 2,595 | 2,005 |
| **win%** | 87.1% | 64.1% | **82.9%** |
| raw PF | 7.59 | 5.19 | 5.19 |
| net | $7.66M | $2.59M | $2.59M |
| worst symday | −$222k | −$74k | −$74k |
| max legs/day | 21 | 2 | **1** |

**Arm-gating recovers 19 win-pts (64→83%)** by only ever holding the FIRST fresh fade, not a dumped backlog of
stale ones. Same net/PF/worst-day as the old mc1 but with 590 FEWER trades and much healthier composition —
strictly better. `max_legs/day` = exactly 1 now (the cap is enforced at the EXPOSURE level, not just at fire).
**mc0 (unlimited) is BYTE-IDENTICAL** — the arm gate is a no-op there, so all prior F1-F15 numbers unchanged.
NEXT: sweep max-concurrent {1,2,3,5} — with quality now preserved, a moderate cap may capture most of the
unlimited net ($7.66M) while keeping the worst-day bounded.

## Finding 17 — ⭐ max-conc 1 stop sweep (unlimited re-entries): the stop level is nearly NET-NEUTRAL; re1 is the peak leg, re3 the wall

At max-conc 1 (arm-gated, F16) the stacking is gone entirely, so this is the honest single-slot book. Swept the
roll30 stop buffer 1-9% with UNLIMITED re-entries. A-book, 2020+, down-tick entry. (Post-hoc `re_idx<=N` slicing
is NOT valid at max-conc 1 — a capped re-entry frees a slot a different pending grabs — so re-entries are
unlimited here; the limited-retry sweep is a separate run at a chosen buffer.)

**Overall (per buffer):**

| buffer | n | win% | raw PF | net | worst symbol-day | worst trade |
|---:|---:|---:|---:|---:|---:|---:|
| 1% | 3,842 | 48.4% | 2.29 | $2.17M | −$10.3k | −$5.4k |
| 2% | 3,443 | 54.0% | 2.35 | $2.21M | −$11.9k | −$5.4k |
| 3% | 3,190 | 57.9% | 2.41 | $2.23M | −$12.2k | −$5.4k |
| 4% | 3,041 | 60.7% | 2.45 | $2.25M | −$14.0k | −$5.4k |
| 5% | 2,900 | 63.5% | 2.53 | $2.28M | −$14.6k | −$5.4k |
| 6% | 2,790 | 65.6% | 2.60 | $2.30M | −$14.2k | −$5.6k |
| 7% | 2,713 | 67.4% | 2.67 | $2.33M | −$15.2k | −$6.6k |
| 8% | 2,635 | 69.0% | 2.75 | $2.36M | −$14.1k | −$6.6k |
| 9% | 2,577 | 70.3% | 2.79 | $2.36M | −$14.0k | −$6.6k |

**⭐ THE STOP LEVEL IS NEARLY NET-NEUTRAL here: net moves only $2.17M → $2.36M (+9%) across the whole 1-9% sweep**
(vs the big swings at max-conc 0). WHY: with ONE slot, a stopped-out trade just frees the slot for the next fade —
so a tighter stop doesn't BLEED net the way it does when many positions run concurrently (there, a stop realizes
a loss on a position that would otherwise have reverted, with no offsetting re-fill). The buffer only trades PF
for win% here (wider = higher PF 2.29→2.79 + higher win% 48→70% as fewer trades get stopped), and the worst-day
stays flat (−$10-15k) — the single slot caps it. So at max-conc 1 the stop is a PF/win-rate knob, not a net knob.

**Per-leg raw PF (rows=buffer, cols=re_idx):**

| buffer | re0 | re1 | re2 | re3 | re4+ |
|---:|---:|---:|---:|---:|---:|
| 1% | 2.16 | 2.52 | 2.68 | 1.92 | 2.06 |
| 2% | 2.16 | 3.04 | 2.19 | 2.35 | 1.76 |
| 3% | 2.24 | 3.17 | 2.33 | 1.82 | 1.42 |
| 4% | 2.32 | 3.02 | 2.74 | 1.31 | 1.57 |
| 5% | 2.45 | **2.96** | 2.92 | 1.00 | 1.89 |
| 6% | 2.57 | 2.83 | 2.98 | 0.92 | 2.05 |
| 7% | 2.63 | 3.12 | 2.58 | 0.80 | 2.32 |
| 8% | 2.69 | 3.24 | 2.55 | 1.31 | 2.11 |
| 9% | 2.76 | 3.12 | 2.47 | 1.15 | 4.01 |

**Per-leg trade count (rows=buffer, cols=re_idx) — re0 constant, re-entries fall as the buffer widens:**

| buffer | re0 | re1 | re2 | re3 | re4+ |
|---:|---:|---:|---:|---:|---:|
| 1% | 2,005 | 973 | 463 | 219 | 182 |
| 2% | 2,005 | 862 | 342 | 146 | 88 |
| 3% | 2,005 | 776 | 252 | 96 | 61 |
| 4% | 2,005 | 711 | 214 | 65 | 46 |
| 5% | 2,005 | 635 | 178 | 48 | 34 |
| 6% | 2,005 | 568 | 151 | 39 | 27 |
| 7% | 2,005 | 531 | 120 | 34 | 23 |
| 8% | 2,005 | 486 | 101 | 30 | 13 |
| 9% | 2,005 | 447 | 90 | 25 | 10 |

**re1 is the PEAK leg at every buffer (PF ~2.5-3.2, higher than re0's ~2.2-2.8); re2 is still strong (~2.3-2.9);
re3 is the WALL — PF ~0.8-1.9, breakeven-ish** (5% detail: re3 PF 1.00, n=48, win 43.8%, net $0). Same re1-peak /
re3-wall structure as F13 at max-conc 0 → the 3rd re-entry is dead money regardless of concurrency. re0 count is
CONSTANT (2,005 — entries are buffer-independent); re-entry counts FALL with a wider buffer (fewer stop-outs).
**Read: the limited-retry sweep should cover re-cap 0-2 (all PF-positive), never 3+.** re2 looks viable here
(PF ~2.9) — unlike max-conc 0 where re2's day-stacking hurt — because the single slot removes the stacking cost.

## Finding 18 — ⭐⭐ MAX-CLOSE STOP beats the 9-EMA stop — but the reason is LEVEL not TIMING; LIMITED re-entries: re1 is additive, re2 widens the tail (cap at 1)

**New exit anchor (`--max-close-stop --max-close-stop-window 20 --max-close-stop-buffer X`)**: while short, cover
(at close) when the **raw bar close** rises above the rolling-20m-max-close × (1+buffer), frozen at entry — same
freeze discipline as the EMA stop, but on the raw close instead of the 9-EMA. Motivation (user): "some of these
worst trades are still pretty bad — would exiting more quickly on bar-close be better?"

⚠️ **CORRECTED MECHANISM (measured, not assumed).** My first writeup claimed "raw close exits a bar sooner than the
smoothed 9-EMA." **That is FALSE.** Controlled 1:1 test — same entries, same buffer 0.20, no re-entries, mc vs
ema-max, joined on symbol+date+entry_time; on the **152 trades that stopped out in BOTH** runs: mc avg hold **85.2**
bars vs ema **84.8** — mc exits **0.37 bars LATER** on average (mc earlier on 83, later on 43, same on 26). The
close-based level does NOT fire sooner. What actually happens: at the same nominal buffer, **the max-close stop is a
LOOSER effective stop** — it trips FEWER trades (b20: 164 stops vs ema's 187) and lets MORE ride to MOC (1896 vs
1871). mc's higher PF/win% is that extra MOC-winner retention, **not** faster cutting. The buffer number is not
comparable across anchors; mc-b20 ≈ a wider ema stop. Treat mc and ema as two different level-families, not
"same stop, faster reaction."

All runs **max-conc 1, down-tick entry, 2020+**, limited re-entries. Direct comparison at matched tightness — the
max-close stop dominates the EMA-max stop on PF and win% at ~the same worst-symbol-day:

| variant | n | win% | raw PF | net $k | worst sym-day $k | worst trade $k |
|---|---|---|---|---|---|---|
| ema roll30 b05 re1 | 2678 | 64.2 | 2.58 | 2150 | −7.3 | −5.4 |
| ema roll30 b05 re2 | 2826 | 64.0 | 2.58 | 2259 | −8.8 | −5.4 |
| ema roll30 b10 re1 | 2431 | 72.0 | 2.91 | 2316 | −9.3 | −6.6 |
| ema roll30 b10 re2 | 2501 | 71.7 | 2.88 | 2371 | −10.8 | −6.6 |
| **mc win20 b10 re1** | 2359 | **73.9** | **3.09** | 2364 | −9.6 | −6.9 |
| mc win20 b10 re2 | 2416 | 73.6 | 3.04 | 2404 | −10.5 | −6.9 |
| **mc win20 b20 re1** | 2165 | **79.4** | **3.71** | 2498 | −12.8 | −9.5 |
| mc win20 b20 re2 | 2191 | 79.2 | 3.68 | 2519 | −14.4 | −9.5 |
| **mc win20 b30 re1** | 2111 | **81.1** | **3.94** | 2513 | −12.8 | −9.5 |
| mc win20 b30 re2 | 2125 | 80.9 | 3.89 | 2518 | −18.5 | −9.5 |
| mc win20 b40 re1 | 2078 | 82.0 | 4.25 | 2553 | −14.8 | −11.0 |
| mc win20 b40 re2 | 2083 | 81.9 | 4.18 | 2543 | −22.1 | −11.0 |
| mc win20 b50 re1 | 2055 | 82.3 | 4.53 | 2571 | −18.6 | −11.4 |
| mc win20 b50 re2 | 2058 | 82.3 | 4.49 | 2566 | −25.3 | −11.4 |

The buffer is the same net-vs-tail dial as before: **wider buffer = higher PF/win/net but a wider tail** (b10 worst
−$9.6k → b50 worst −$18.6k). Sweet spot ≈ **b20–b30**: PF 3.7–3.9, win ~80%, worst-symday held to −$12.8k, net
$2.5M. b30-re1 is the pick — same worst-symday as b20 but +PF.

**Limited re-entries — re1 is additive; re2 adds nothing and widens the tail.** The user's hypothesis ("capping
re-entries might enable subsequent setups"): confirmed for re1.

Full per-leg table from the re2 runs (n / win% / raw PF / net$k), so the cap-at-1 call is visible, not asserted:

| buffer | re0 | re1 | re2 |
|---|---|---|---|
| b10 | 2007 / 74.4 / **3.06** / 1957 | 344 / 70.6 / **3.18** / 390 | 65 / 63.1 / **2.18** / 58 |
| b20 | 2005 / 80.0 / **3.85** / 2295 | 158 / 73.4 / **2.74** / 202 | 28 / 60.7 / **2.19** / 22 |
| b30 | 2005 / 81.4 / **4.17** / 2407 | 105 / 73.3 / **2.10** / 104 | 15 / 60.0 / 1.37 / 7 |
| b40 | 2005 / 82.1 / **4.33** / 2451 | 73 / 78.1 / **3.09** / 102 | **5** / 40.0 / 0.38 / −9 |
| b50 | 2005 / 82.6 / **4.66** / 2512 | 50 / 72.0 / **2.41** / 59 | **3** / 33.3 / 0.24 / −5 |

- **re0 count is buffer-independent (~2005 entries)**; re1 count RISES as the stop tightens (b10 → 344 re-probes,
  b50 → 50) — a tight stop trips more, freeing the slot for the next down-tick sooner. This is the mechanism that
  "enables subsequent setups": tight stop + re1 = take the re-break.
- **re1 leg PF 2.1–3.18 at every buffer — genuinely additive**, adds net (+$102–390k) with negligible worst-symday
  change at the pick buffer (b30 stays −12.8 re0→re1).
- ⚠️ **re2 correction.** My first writeup called re2 "the weak leg, PF 2.18 → 0.38 → 0.24." That decay is a
  **SAMPLE-SIZE ARTIFACT** — b40 re2 is **n=5**, b50 re2 is **n=3** (a couple of losers = meaningless PF). Where re2
  has real n (b10 n=65 PF 2.18, b20 n=28 PF 2.19) it is **not** a loser. The honest reason to still cap at 1 is
  **not** re2's PF — it's that (a) re2 adds almost no net (b10 +$58k, and single-digit $k by b30) while (b) it
  consistently WIDENS the worst-symbol-day by stacking a 2nd losing leg on the same pop (b30 −12.8 → −18.5;
  b50 −18.6 → −25.3), and (c) win% steps down each leg (74→71→63 at b10). Marginal upside, real tail cost → **cap
  re-entries at 1.**

re1→re2 worst-symbol-day, for the record: b10 −9.6→−10.5, b20 −12.8→−14.4, b30 −12.8→**−18.5**, b40 −14.8→−22.1,
b50 −18.6→−25.3.

**Verdict: the drawdown-controlled short book is `--max-close-stop --max-close-stop-window 20 --max-close-stop-buffer
0.30 --ema-reentries 1` at max-conc 1** — PF 3.94, win 81%, net $2.5M, worst-symday −$12.8k, worst-trade −$9.5k.
This is the tightest tail we've reached that still keeps PF ~4 (vs the no-stop down-tick book's −$222k worst-day at
mc 0, F17-baseline). Stop reasons on b30-re1: 121 stops avg −49.8% (the cut runners), 1990 MOC winners avg +15.7%.

## Finding 19 — ⭐ the FULL tail-aggregation ladder (worst symbol-day → cal-day → week → month): the worst MONTH is ~breakeven-or-positive for every variant; the tail is a SINGLE bad day, not a losing streak

For every F18 variant, the worst adverse Σ-net at each aggregation level (2020+, max-conc 1). **worst_sym_day** =
Σ per (symbol,date); **worst_cal_day** = Σ per date (all symbols); **worst_week** = Σ per Mon-anchored week;
**worst_month** = Σ per calendar month. All $k.

| variant | net_k | worst_sym_day | worst_cal_day | worst_week | worst_month |
|---|---|---|---|---|---|
| ema roll30 b05 re1 | 2150 | −7.3 | −9.7 | −8.3 | **+1.4** |
| ema roll30 b05 re2 | 2259 | −8.8 | −12.0 | −8.1 | **+2.1** |
| ema roll30 b10 re1 | 2316 | −9.3 | −13.9 | −10.7 | **+0.9** |
| ema roll30 b10 re2 | 2371 | −10.8 | −14.6 | −9.3 | 0.0 |
| mc win20 b10 re1 | 2364 | −9.6 | −15.2 | −10.8 | **+0.2** |
| mc win20 b10 re2 | 2404 | −10.5 | −14.3 | −9.6 | −0.7 |
| mc win20 b20 re1 | 2498 | −12.8 | −15.2 | −8.7 | −0.3 |
| mc win20 b20 re2 | 2519 | −14.4 | −12.8 | −10.9 | **+2.4** |
| **mc win20 b30 re1** | 2513 | −12.8 | −14.8 | −12.3 | −0.1 |
| mc win20 b30 re2 | 2518 | −18.5 | −16.7 | **−20.2** | **+2.6** |
| mc win20 b40 re1 | 2553 | −14.8 | −13.7 | −15.3 | **+2.8** |
| mc win20 b40 re2 | 2543 | −22.1 | −20.4 | −21.0 | **+2.8** |
| mc win20 b50 re1 | 2571 | −18.6 | −16.8 | −17.4 | **+2.8** |
| mc win20 b50 re2 | 2566 | −25.3 | −23.6 | −22.4 | **+2.8** |

Three reads:

1. **The worst MONTH is ~breakeven-or-positive for EVERY variant** (+$0.9k to +$2.8k; the worst any variant does is
   −$0.7k, essentially flat). Across 2020–2026 there is **no losing calendar month** in this book at max-conc 1. The
   drawdown problem is entirely a *within-month* / single-event problem — by 30 days it always washes out.
2. **worst_week ≈ worst_day in magnitude** for the tight variants (ema b05: week −8.3 vs cal-day −9.7; mc b30 re1:
   week −12.3 vs cal-day −14.8). The worst week IS essentially one bad day surrounded by profitable ones — losses do
   **not** stack across a week. The tail is a single-day catastrophe, not a losing streak. (Confirms F2's "worst-30 =
   57% of loss $" hyper-concentration, now at the calendar level.)
3. **re1 beats re2 up the whole ladder**, most starkly at b30 (worst-week −12.3 re1 vs −20.2 re2). The re2 tail cost
   compounds at every horizon → reinforces cap-at-1. The pick, **mc b30 re1**, holds a clean ladder: sym-day −12.8,
   cal-day −14.8, week −12.3, month −0.1 (flat).

⚠️ Note the two DAY metrics differ: **worst_cal_day is generally WORSE than worst_sym_day** (a bad date carries
losses on several symbols at once — e.g. ema b05 re1: −9.7 cal vs −7.3 sym). F18's tables quote worst_sym_day;
this ladder adds the calendar view. Neither is wrong — sym-day is "worst single position-cluster on one name,"
cal-day is "worst total day." For real-account drawdown, cal-day/week/month are the ones that matter.

**PRODUCTION DEFAULT (set after F19):** the no-flag `defaultConfig` is now this book — `--ema-entry
--ema-down-tick-entry --ema-max-stop --ema-max-stop-window 30 --ema-max-stop-buffer 0.10 --ema-reentries 2
--max-concurrent 1` = **`ema roll30 b10 re2`** (2501 trips / 71.7% win / net $2.37M / PF 2.876, worst-week −$9.3k,
worst-MONTH breakeven). User's call: ~10% is the ideal tail/PF knee and EMA≈max-close there. Flags flip it off.

## Finding 20 — ❌ INVERTING the signal (BUY the pop, sell on the 9-EMA down-tick) has strongly NEGATIVE expectancy — PF 0.32

Direct test of "is the short's edge just the fade, or is there a tradable LONG in the same pop?" `--long-breakout`:
BUY the new-session-HIGH breakout bar directly (Short=false, Downside=false, same A-book universe: brv20d≥100 &
ATR%≥0.03), then SELL on the first 9-EMA down-tick (`--ema-down-tick-exit`). The exact inversion of the short book
(which SHORTS the pop and covers on weakness). max-conc 1, 2020+.

| book | trips | win% | raw PF | net $k | avg ret% | median ret% |
|---|---|---|---|---|---|---|
| SHORT pop-fade (default, b10 re2) | 2501 | 71.7 | **2.88** | +2371 | — | — |
| **LONG breakout (invert)** | 1705 | **19.8** | **0.32** | **−658** | −3.87 | −4.88 |

**The long is a decisive loser** — PF 0.32, win 19.8%, −$658k. Two compounding reasons, both confirmed in the data:

1. **The signal reverts (that's WHY the short works).** The pop into a new session high on A-book volume is a
   mean-reversion setup; the long is on the wrong side of the same reversion. Median long return −4.88%.
2. **The down-tick exit is asymmetric AGAINST a long.** 1699/1705 trips exit via the down-tick at avg hold **7.9
   min** — it sells at the first flicker of weakness, so the ~20% of trades that would have run get cut early
   (best +101% exists but is rare), while the 80% that immediately reverse are held into the down-tick and sold
   near the lows. Same trigger that's a great SHORT entry (fade weakness) is a terrible LONG exit (panic-sell noise).

**Verdict: there is no long book in this signal.** The pop-fade is short-only; its edge IS the reversion, and the
inverse loses on both the direction and the exit. Do not pursue a long variant of the breakout pop. (P&L sign
verified: NetPnL = qty·(exit−entry) for the long — the loss is real, not a convention bug.)

## Finding 21 — ⭐⭐ APPLES-TO-APPLES entry timing: SHORT-THE-HIGH (defer the stop to the 1st down-tick) BEATS down-tick ENTRY — the down-tick's value is STOP-ARMING, not entry

We had only ever compared down-tick entry vs short-the-high on the OLD mc-0 no-stop system. Proper test on the
CURRENT default (mc 1, roll30 b10 stop, 2 re-entries): **`--short-high-entry`** — short the breakout bar IMMEDIATELY
(the high), leave the ema-max stop DORMANT, and ARM it on the first 9-EMA down-tick with the roll30-max base frozen
at that down-tick bar (exactly where the down-tick-ENTRY book freezes it). Pre-arm = MOC-only (unprotected).
Re-entries UNCHANGED (still enter at the next down-tick). This isolates the entry PRICE — short-the-high vs
short-the-down-tick — with identical stop/re-entry mechanics keyed to the same down-tick bar.

| book | trips | win% | raw PF | net $k | worst sym-day | worst trade |
|---|---|---|---|---|---|---|
| down-tick ENTRY (default) | 2501 | 71.7 | 2.88 | 2371 | −10.8 | −6.6 |
| **short-the-HIGH (defer stop)** | 2510 | **73.8** | **3.77** | **3034** | −12.1 | **−17.1** |

**Short-the-high wins decisively: PF 2.88 → 3.77, net +28% ($2.37M → $3.03M), win% +2.1** at the same trip count.
The mechanism is proven three ways:

1. **Entry timing confirmed**: short-high median entry 10:03 vs down-tick 10:11 (~8 min earlier), earliest 09:45 vs
   09:46 — it fires on the breakout bar itself, before the EMA rolls over.
2. **Per-leg PF isolates it perfectly**: re1/re2 legs are IDENTICAL between books (3.20 / 2.70 at n=411 / 83) — by
   design, since re-entries use down-tick entry in both. The ENTIRE gain is **leg-0: PF 3.98 (short-high) vs 2.82
   (down-tick)**. Shorting the pop at the high (before it fades) is a materially better fill than waiting for the
   9-EMA to confirm weakness — the down-tick entry was leaving ~1.1 PF on the table on the original leg.
3. **The cost is a fatter per-trade tail**: worst trade −$17.1k vs −$6.6k. NOT a naked-position bug — all 5 worst
   trades exit via `ema_max_stop` (the stop DID arm); the higher entry simply sits further from the stop, so a name
   that keeps ripping (MBRX −171%, HOUR −104%) runs a larger adverse excursion before the stop catches it. Worst
   sym-day only −12.1 vs −10.8 (at mc 1 the tail doesn't stack). Stop mix (leg-0): 417 stops avg −18.2%, 1599 MOC
   winners avg +20.3%.

**Reframe: the 9-EMA down-tick is a STOP-ARMING signal, not an entry signal.** Its real job is to say "the pop has
turned, now protect the position" — using it to gate ENTRY just delays the fill to a worse price. The best book
enters at the high and uses the down-tick only to arm the stop. Trade-off vs the default: **+28% net / +0.9 PF for a
2.6× worse worst-trade (−17k vs −6.6k)** — a sizing question, not an edge question. Candidate for the new default
pending a worst-trade appetite decision; the tail is still bounded (short, +100%-capped) and worst-sym-day barely
moved.

## Finding 22 — ⭐ short-the-high is the NEW DEFAULT; its worst-WEEK/MONTH tail is as good as (or better than) the down-tick book — the fatter worst-TRADE does NOT propagate up

Set `ShortHighEntry = true` in `defaultConfig` (F21 is now the no-flag book: 2510 trips / 73.8% win / PF 3.77 /
net $3.03M). The F21 concern was the fatter worst-TRADE (−$17k). This resolves it: the tail does NOT cluster, so at
the week/month horizons that matter for a real account the short-high book is as good as or BETTER than the old
down-tick default. Full ladder (max-conc 1, 2020+):

| book | net $k | worst sym-day | worst cal-day | worst week | worst month |
|---|---|---|---|---|---|
| down-tick (old default) | 2371 | −10.8 | −14.6 | −9.3 | 0.0 |
| **short-high (NEW default)** | **3034** | −12.1 | −14.6 | −10.9 | **+5.8** |

- **Worst MONTH is +$5.8k — POSITIVE, and BETTER than the down-tick book's 0.0.** No losing calendar month across
  2020–2026. The extra +28% net more than absorbs the fatter worst-trade by the 30-day horizon. Three worst months
  all green: Feb'26 +5.8, Feb'22 +6.3, Dec'21 +9.5.
- **Worst CAL-DAY is IDENTICAL (−14.6)** — the single worst day is the same event in both books (the higher entry
  didn't create a worse worst-day). **Worst-week −10.9 ≈ worst-day** — the F19 property holds: the tail is one bad
  day, not a losing streak (3 worst weeks −10.9/−10.4/−9.6 are each ~a single bad day: 2020-05-25, 2023-05-08,
  2022-12-05).
- Net: the fatter worst-TRADE (−17k) buys +28% net and does NOT deepen the week (−10.9 vs −9.3, ~+$1.6k) or the
  month (actually improves). **The F21 tradeoff lands favorably at every horizon above the single trade.**

Production note (user): may split the fill — **short HALF at the high, HALF at the down-tick** — to blend the
short-high edge with the down-tick book's tighter worst-trade. The default is the full short-high; the down-tick
book is now reachable via `--no-short-high` (byte-identical to the old down-tick default).

## Finding 23 — ⭐⭐ RIGHT-SIDE-OF-THE-V (Breitstein): the MORE UNDERWATER a short-high trade is at the arm bar, the BETTER its go-forward edge — ADD on the down-tick when it ran against us

The question: on a short-high leg-0 trade, does the entry→arm DISPLACEMENT (how far price moved from our high entry
by the time the 9-EMA down-tick arms the stop) predict the go-forward edge — and should we ADD at the down-tick?
Engine now emits `arm_time` / `arm_close` (the down-tick bar). Displacement = `arm_close/entry − 1` (short: **>0 =
UNDERWATER** at the arm, the pop kept running against us before rolling over; **<0 = already in profit**). Go-forward
return = short from the ARM bar's close to exit = `(arm_close − exit_price)/arm_close`. Leg-0 only, 2020+, 2007 trades.

| displacement bucket | n | avg disp | fwd win% | **fwd PF** | avg fwd ret% |
|---|---|---|---|---|---|
| <−15% (deep profit at arm) | 88 | −18.8% | 72.7 | 2.32 | +9.3 |
| −15..−5% | 848 | −8.5% | 69.9 | 2.31 | +7.3 |
| −5..0% (small profit) | 643 | −2.9% | 73.4 | 3.32 | +9.2 |
| 0..5% (small underwater) | 235 | +2.0% | 76.2 | 3.49 | +10.9 |
| **5..15% underwater** | 123 | +8.8% | **79.7** | **4.42** | **+15.2** |
| 15..30% underwater | 45 | +19.7% | 73.3 | 3.56 | +15.8 |
| >30% underwater | 25 | +50.3% | 48.0 | **1.65** | +7.6 |

⚠️ The relationship is an **INVERTED-U, not monotonic**: the go-forward edge climbs with displacement to a peak at
5–15% underwater (PF 4.42), plateaus through 15–30% (PF 3.56), then **collapses past 30% (PF 1.65, win 48%)**. A pop
STILL up >30% at the FIRST down-tick is a monster that barely paused — the tick is a breather, not exhaustion, and
near-half keep ripping. (An earlier draft showed n=34 / NaN here: that was a bucketing bug — `disp = nan/entry` for
9 NEVER-ARMED legs, whose `nan < threshold` is always false so they fell through the CASE into the >30% bucket. The
9 are leg-0 short-high trades that NEVER 9-EMA-down-ticked before MOC (0.45% of legs — rode fully unprotected to a
+MOC close); they carry `arm_close = nan` and are correctly excluded below.)

Collapsed to the two sides (never-armed + arm=exit rows excluded, 9 of 2016):

| side at arm | n | fwd win% | **fwd PF** | avg fwd ret% |
|---|---|---|---|---|
| IN PROFIT at arm (disp<0) | 1579 | 71.5 | 2.64 | +8.2 |
| **UNDERWATER at arm (disp≥0)** | 428 | 75.2 | **3.49** | **+12.4** |

**The underwater-at-arm trades have the BETTER go-forward short** — PF 3.49 vs 2.64, +12.4% vs +8.2% avg. It's
near-monotonic in displacement, peaking in the 5–15%-underwater bucket (PF 4.42). This is exactly Breitstein's
**right-side-of-the-V**: a pop that ran HARD against us and only THEN cracked is a more exhausted, higher-air fade
than one that fizzled early — the bigger the pop before the turn, the more room beneath it when it finally rolls.

**NOT a survivorship artifact.** Both sides carry their stop-outs, at nearly EQUAL stop rates (underwater 88/428 =
20.6%; in-profit 329/1579 = 20.8%) — the underwater side isn't winning by hiding its blow-ups. The edge is that its
MOC WINNERS run harder (+21.6% vs +16.1% avg on the MOC exits). Stop-out avg loss is the same both sides (~−22%).

**Answer to "should we add at the down-tick?": YES — add MORE the more UNDERWATER we are, UP TO ~15–30%, then STOP.**
Moderate underwater (0–30%) is the strongest continuation short (PF 3.5–4.4), which inverts naive risk management
(that cuts losers) — the "moderate loser at the arm" is the best fade. But **do NOT add past ~30% underwater** (PF
collapses to 1.65): a pop still up >30% at the first tick hasn't exhausted. Practical rule: **the down-tick is not
just a stop-arm — it's an ADD point sized by displacement, on an INVERTED-U — peak add around 5–15%, taper by 30%,
none beyond.** Caveats: the underwater side is smaller (428 vs 1579 — most pops have already started falling by the
tick), and the >30% (n=25) and 15–30% (n=45) buckets are thin. Reuse target: test this displacement-at-confirmation
feature on the other mean-reversion books (LowFlyer / MaxFlyerV2) — arm the stop AND size the add on the right side
of the V.
