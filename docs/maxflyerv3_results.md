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
