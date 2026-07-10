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
