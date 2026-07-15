# Trading Systems Portfolio — Overview

This document summarizes six intraday/swing equity systems developed and back-tested over a
multi-decade US-equity 1-minute dataset. Each was built as an explicit state-machine engine in
F#, validated against a no-lookahead fill model, and stress-tested for per-year robustness and
winner-concentration (fat-tail discipline) rather than a single blended profit factor.

**Conventions used throughout.** Profit factor (PF) is gross-win ÷ gross-loss on a fixed
$10k-per-trip notional. For the long momentum books I report both **raw** PF (hold-to-MOC, which
exposes the fat right tail) and **clip** PF (winners capped at +50%, which neutralizes jackpots and
reveals the durable edge) — a jackpot-skewed cell fails the clip test. P&L is gross (no
commissions/slippage modeled); these are edge-discovery and viability numbers, not live-execution
statements. All engines fold features *including* the current already-closed bar and snapshot
pre-decision, so nothing reads the future.

All research logs are in the public repository **[mrakgr/Trading-Edge](https://github.com/mrakgr/Trading-Edge)**.
Each system below links to its full findings log (F1, F2, … findings, with the bucket tables and
per-year breakdowns behind every claim).

---

## Candidate-day universe — how each system chooses which (ticker, day)s to scan

Every system's intraday engine only ever streams the 1-minute bars of a pre-selected set of
`(ticker, day)` rows. That selection is pure daily-context SQL and happens *before* any intraday
signal — it is the "in-play" filter. Five of the six systems draw from one of two shared tables;
HighFlyerV2 is the exception (it scans every stock daily and gates in-engine). Builder scripts:
[`build_mr_candidate.fsx`](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/scripts/equity/build_mr_candidate.fsx),
[`build_vwap_reclaim_candidate.fsx`](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/scripts/equity/build_vwap_reclaim_candidate.fsx).

**`mr_candidate` — the shared base table (the broad "liquid & alive" universe).**
One row per `(ticker, day)` that clears these daily preconditions (no lookahead — every field is
known by 09:45 ET or is a strictly-reported forward return):
- **Liquidity prune (the hard cut):** median of the 09:30–09:45 ET one-minute-bar volumes ≥ **10,000
  shares**, AND ≥ **10 of the (max 15)** minute bars present. Only qualifying days have their minute
  bars streamed to the engine.
- **Instrument type:** common stock or ADR only (`CS`/`ADRC`).
- **Price floor:** the day's adjusted close ≥ **$1**.
- **Warmup:** ≥ **21 bars** in the current price episode (episodes are gap-severed at >45 calendar
  days, so a symbol coming back from a long halt/delisting gap starts fresh).
- **rvol floor:** `rvol_0945 = (premarket-inclusive volume 04:00→09:45) / (20-day avg daily volume)`
  ≥ **0.1** (drops the barely-traded tail that is dead in every slice).

**`vwap_reclaim_candidate` — the "genuinely in-play" subset** (a strict subset of `mr_candidate`,
~19% of it: 161,979 of 850,107 rows). Adds two liquidity/interest prunes:
- **Dollar-ADV floor:** `avgvol20 × day_close ≥ $30M` (a real liquidity floor — $1M let in
  sub-dollar / thin-float names with unrealistic 1m fills; $30M is the empirical sweet-spot floor).
- **rvol_0945 > 1** — trading at *more* than its normal volume into the open, i.e. actually in play,
  not merely liquid.

**Which system reads what:**

| System | Universe table | Extra in-engine day gates layered on top |
|--------|----------------|------------------------------------------|
| OpeningDriverV2 | `vwap_reclaim_candidate` | `chg_1d ≥ +20%`, `chg_3d ∈ [0,1.5]`, `log_ATR ≥ 0.013`, `stop_dist ≥ 3%`, `bh ≥ 1` |
| DipRiderV4 | `vwap_reclaim_candidate` | `chg1d ≥ 10%`, `chg3d ≥ 0%`, `log_ATR ≥ 0.013`, breakout-timer + vol_climb |
| VwapReclaimV3 | `vwap_reclaim_candidate` | reclaim cross + weakness-run `rb ≥ 11` + tightness ≥ 3 |
| MaxFlyerV3 (short) | **`mr_candidate`** (full base) | `brv20d ≥ 100`, intraday ATR% ≥ 0.03 |
| LowFlyer | **`mr_candidate`** (full base) | flush ≤ −0.7%, depth ≥ −12%, log-ATR < 0.02, vol-confirm; + float/trend selection filters |
| HighFlyerV2 | *(own universe — see below)* | float < $300M, up ≥10% by 10:00, rvol ≥ 1.0, dip 1–6% |

The two short/mean-reversion books (MaxFlyerV3, LowFlyer) deliberately keep the *broad* `mr_candidate`
base — they want the full population of movers (including thin, low-dollar-ADV names that pop and
fade), and do their own filtering in-engine. The three long-momentum books take the pre-narrowed
`vwap_reclaim_candidate` because their edge lives in liquid, genuinely-in-play names.

**HighFlyerV2 — the exception (daily-driven, not candidate-table-driven).** It does *not* read either
candidate table. Its engine streams the daily universe directly — `split_adjusted_prices` for every
`CS`/`ADRC` ticker, left-joined to a **`partial_candle_1000`** table (the 10:00 ET partial candle,
premarket-inclusive) — and applies every numeric gate (dollar-float < $300M, up ≥10% on the day by
10:00, rvol ≥ 1.0, decline-from-open 1–6%) inside the engine at the 10:00 checkpoint. So its "universe"
is simply *all common-stock/ADR daily bars that have a usable 10:00 candle*, narrowed entirely by the
in-engine gates listed in its section below.

---

## 1. OpeningDriverV2 — opening-drive momentum (long, intraday)

**Setup.** A long opening-drive continuation. An arm/disarm state machine scans each ticker-day
inside the `[09:45, 10:00)` ET window and fires on the *first* bar that clears all gates, opens
**one** position, then disarms for the day — one honest trade per ticker-day.

**Universe.** `vwap_reclaim_candidate` (the in-play subset: ≥$30M dollar-ADV, rvol_0945 > 1).

**Signals / gates (production default).**
- `chg_1d ≥ +20%` (the day-strength gate — this single condition is the core of the engine; it
  selects idiosyncratic catalyst movers and subsumes most downstream "quality" filters)
- `chg_3d ∈ [0, 1.5]` · `log_ATR₂₀ ≥ 0.013` (volatility floor) · `stop_dist ≥ 3%` · `bh ≥ 1` (require
  at least one pullback before entry)
- **Exhaustion latch (blow-off kill-switch):** once any bar prints a new session-high *close* on
  `brv20d ≥ 110` (single-minute volume vs per-minute 20-day ADV) **and** ATR% ≥ 0.03, the day is
  latched — no further arm, and any open position is flushed. This is the same climax signature the
  short book (MaxFlyerV3) uses to *initiate* shorts.
- **Sizing lever:** bet **3×** notional when the **9-EMA < VWAP at entry** (trend still below fair
  value = buying support, not chasing). This was the single strongest lever found — all-weather,
  beats above-VWAP in all seven years.

**Stops / exits.** Wide `sess-ema-low` stop: exit when the live 9-EMA falls below the frozen 9-EMA
session-minimum at entry (~1.8% average room — deliberately distant so the drive can run). Otherwise
exhaustion-flush or hold-to-MOC. Exit mix ≈ 50% MOC / 47% stop / 3% exhaustion.

**Performance (2020 → mid-2026, 1,028 trips).**
Raw PF **3.50** · clip PF **2.03** · win **42%** · flat net **$1.17M**. With the 3× 9-EMA<VWAP sizing:
**PF 4.11 · net $2.39M**. Positive every year (worst-year clip PF 1.35 in 2021); **83% green months**
(65/78). Max drawdown ≈ 1.4 base position-sizes flat (~2.5× at 3× sizing). Breadth-neutral /
market-agnostic — the +20%-day gate already isolates single-name catalysts; diversified, not
lottery-driven (top trade = 4% of gross profit).

**Research log:** [docs/opening_driver_v2_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/opening_driver_v2_results.md)

---

## 2. DipRiderV4 — momentum continuation (long, intraday)

**Setup.** A long intraday momentum-continuation engine on in-play stocks. It buys strong up-moves
that make a fresh session/rolling EMA-high on *accelerating* volume. A price pattern arms the setup
(rolling-20-minute-low arm/re-arm state machine); a volume gate decides whether an armed trigger
opens a real position. Multi-hour intraday round-trips, exited same day.

**Universe.** `vwap_reclaim_candidate` (the in-play subset: ≥$30M dollar-ADV, rvol_0945 > 1).

**Signals / gates (A-book default).**
- **Breakout timers (the structural edge):** `bars_since_breakout ∈ [0, 10)`, where bar 0 is the
  first bar the 9-EMA makes a strict new high after the rolling-20m-low reset. Three windows —
  session-high (strongest), 60-minute, 20-minute — form a quality dial. The breakout structure
  *subsumes* price-slope, tightness and consecutive-run features (which proved to be dead weight and
  are OFF by default).
- **`vol_climb ≥ 0.5`** base volume floor, where `vol_climb = (volEma − volEmaMin)/volEma` — reads as
  "volume EMA is ≥2× its 20-minute floor"; higher values are the primary A-tier lever. The tiered
  books OR the three breakout windows with *differentiated* per-window vol_climb floors.
- `log_ATR ≥ 0.013` (volatility floor) · `chg1d ≥ 10%` · `chg3d ≥ 0%` · `ema-vs-vwap ≥ −2%`
- **Exhaustion cut** `MaxRvol5m20d = 100` ON (rejects late blow-offs).

**Stops / exits.** EMA-triggered stop at the 20-minute-minimum 9-EMA frozen at entry; entries with a
too-tight stop (`stop_dist < 3%`) fire-and-disarm but open no position (a room guard, so a stopless
trade is impossible). Otherwise hold-to-MOC.

**Performance (2020 → mid-2026).** The system is delivered as an **A → A+ → A++ → S** book ladder (OR
of the three breakout windows with progressively higher vol_climb floors), trading breadth for
quality monotonically:

| Book | Trips | Net | Raw PF | Clip PF | Avg %/trade |
|------|------:|----:|-------:|--------:|------------:|
| Capacity | 5,583 | $1.90M | 2.04 | 1.45 | 3.40% |
| **A (default)** | 1,860 | $1.43M | 2.77 | 1.86 | 7.68% |
| A+ | 786 | $773k | 3.17 | 2.05 | 9.83% |
| A++ | 460 | $597k | 4.05 | 2.54 | 12.98% |
| S | 180 | $248k | 4.46 | 3.01 | 13.79% |

The shipped default adds the 3%-stop-distance floor to the A book: **1,608 trips · win 44% · net
$1.39M · raw PF 2.88.** Positive every year 2020–2026 and 2021-robust (win rate climbs 27% → 52% up
the ladder).

**Research log:** [docs/diprider_v4_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/diprider_v4_results.md)

---

## 3. VwapReclaimV3 — VWAP reclaim scalp (long, intraday)

**Setup.** A long VWAP-reclaim setup (in the SMB idiom). Price sells off below the session VWAP
(anchored at the 09:30 open) for a sustained run of bars with the 9-EMA below VWAP, then reclaims
VWAP — the entry fires on that reclaim cross. Single-day, hold-to-MOC unless a structure stop fires,
gated to a `10:00–13:30` ET window. This is the debloated long-only fork of the VwapReclaim lineage.

**Universe.** `vwap_reclaim_candidate` (the in-play subset: ≥$30M dollar-ADV, rvol_0945 > 1).

**Signals / gates.**
- Reclaim cross: prior-bar 9-EMA/VWAP below → current 9-EMA/VWAP above.
- Weakness run `rb ≥ 11` consecutive below-VWAP bars into the reclaim · **tightness ≥ 3.0**
- Universe: ADV ≥ $1M and premarket-inclusive rvol (through 09:45) > 1.
- Quality graded on run-scoped features: `updn` (up-vol/down-vol conviction ratio, the primary A-tier
  dial), `run_max_dist` (run depth), `dpa = run_max_dist / run_atr` (vol-normalized over-extension).

**Stops / exits.** **9-EMA pullback-low stop:** the running minimum of the 9-EMA over the current
below-VWAP run; the stop fires when the live 9-EMA falls back under that run-min (the reclaim failed
and price is rolling back beneath the base it was built on). A small `0.002` buffer (~0.2% of EMA
tick-noise) is the settled engine default. This structure-based stop beat the prior geometric
VWAP-low stop (+$99k net, +0.046 PF on the full book).

**Performance (2003 → mid-2026).** Full capacity book: **41,027 trips · win 38% · net $1.60M · PF
1.34** — the deep, high-capacity engine. Graded quality cells (all `run_atr ≥ 0.013 &
run_max_dist ≥ 3.5% & dpa < 3`, with `updn` as the dial): Capacity (updn≥0.8) PF **2.86**; A (≥1.0)
PF **3.32**; A+ (≥1.1) PF **3.74**; A++ (≥1.3) PF **4.33** at +18.9%/trade / 239 trips — all-weather
(positive every modern year). Edge concentrates post-2020.

**Research log:** [docs/vwap_reclaim_v3_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/vwap_reclaim_v3_results.md)

---

## 4. MaxFlyerV3 — pop-fade (short, intraday, drawdown-controlled)

**Setup.** A short-only intraday pop-fade. It shorts intraday breakouts to new session highs on names
with an abnormal volume/volatility surge, betting the pop mean-reverts, and covers into weakness or at
the close. Because a short's upside is unbounded, the V3 mandate is explicit **drawdown control via
stops** — accepting a lower PF for a capped left tail.

**Universe.** The full `mr_candidate` base (broad "liquid & alive" — deliberately *not* the
dollar-ADV-narrowed subset; it wants the whole population of pop-and-fade movers).

**Signals / gates (A-book, production default).**
- `brv20d ≥ 100` (the main lever: breakout-bar volume vs per-minute 20-day ADV) · intraday
  **ATR% ≥ 0.03** (rolling-20-minute log true-range floor — "moving fast right now")
- Daily-ATR normalization and rvol tiers carried over from the settled V2 short book.
- **Entry — short-the-high:** short the new-session-high breakout bar *immediately* (at the high,
  before the EMA rolls over). Median entry ≈ 10:03.

**Stops / exits / re-entries.** The 9-EMA down-tick is a *stop-arming* signal, not an entry: the
position is MOC-only until the first down-tick, which arms an **EMA-max stop** frozen at the
rolling-30-minute maximum 9-EMA, covering when the live 9-EMA closes above base × 1.10. The
rolling-30m anchor (vs a stale session max) is what bounds the tail. Up to 2 re-entries per pop;
single concurrency slot.

**Performance (2020 → mid-2026, 2,510 trips).** Win **73.8%** · PF **3.77** raw · net **+$3.03M**.
(No clip PF — a short's win is bounded at +100%.) **Zero negative months in 78**; every year positive
with PF ≥ 2.77. Worst single trade −$17.1k, worst calendar day −$14.6k, worst week −$10.9k — the tail
is a single bad day, not a losing streak. This trades PF/net down from the un-stopped V2 baseline
(PF 6.65 / +$4.78M, but with a −839% single-trade / −$238k-worst-day tail) for a ~9–15× smaller tail,
exactly as intended.

**Research log:** [docs/maxflyerv3_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/maxflyerv3_results.md)

---

## 5. LowFlyer — flush-fade mean reversion (long, intraday)

**Setup.** A long intraday mean-reversion / "flush-fade." It buys a high-volume intraday flush — a bar
closing below the running session low on confirming volume — and fades it back up, betting the panic
overshoots and snaps back. Framed as a *pullback in an uptrend* on a recently-strong, liquid, low-float
name — not a falling knife. Same-day trade, held to MOC; the edge is purely intraday (it round-trips to
flat over the next 1–5 days).

**Universe.** The full `mr_candidate` base (broad "liquid & alive"); the float/trend/depth cuts below
are layered on in-engine and post-hoc.

**Signals / gates.**
- **Engine gates:** entry-bar flush `close/prevClose ≤ −0.7%`; flush-depth floor `≥ −12%` (the
  falling-knife cut); intraday log-ATR `< 0.02`; volume-confirm `vol_vs_high ≥ 0.90` (within 10% of the
  running session 1m-volume high).
- **Selection filters:** 1d change `≤ −8%`; 20m velocity `≤ −3%`; 3d change `∈ [−3%, +30%]`
  (flat-to-strong, not a decliner, not parabolic); 7d change `≥ −5%`; **dollar-float < $300M** (sweet
  spot $50–300M); ADV `≥ $500k`.
- **Timing:** morning-concentrated — 70% of trips fire 09:30–11:00, with 10:00–10:30 the sweet spot.
- **Sizing:** base size, **3× when prior-day breadth (`pct_above_20`) ≥ 0.65**; also size up on deeper
  flushes (deeper flush → higher PF, down to the −12% floor).

**Stops / exits.** No stop. Fill at the flush bar's close, hold to MOC. Multiple entries per day
allowed; strictly same-day (the bounce is intraday and the edge disappears overnight).

**Performance (2003 → 2026, 1,109 trips).** PF **3.38** · win **68%** · **+3.19%/trade**. The
breadth-sized 3× book: **PF 3.40 · +$668k net** (+102% vs the flat +$330k). Positive PF every
meaningful-sample year 2017→2026 (peak 2021); modern-era-strongest and volatility-regime-tilted
(richest 2020–22).

**Research log:** [docs/lowflyer_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/lowflyer_results.md)

---

## 6. HighFlyerV2 — low-float continuation swing (long, multi-day, intraday entry)

**Setup.** A long multi-day continuation swing on low-float momentum runners, entered intraday. It
scans for a low-float name already up ~10–20% on the day that has **pulled back 1–6% off its opening
print into a ~10:00 ET checkpoint** — buying the shallow morning *dip* (a red / low-of-range partial
candle) rather than chasing the spike — and holds ~5 days. This *inverts* the Qullamaggie
buy-strength intuition: for low-float continuation, buying the pullback beats buying the breakout
extension.

**Universe.** *(Exception to the shared tables.)* Streams all `CS`/`ADRC` daily bars that have a
usable 10:00 ET partial candle (`partial_candle_1000`); the float / ≥10%-move / rvol / dip gates below
are all applied in-engine at the 10:00 checkpoint.

**Signals / gates.** *(The A book is all of these except the red-dip gate; adding the dip gate makes
the A+ cell.)*
- **Dollar-float < $300M** at the 10:00 entry price (the strongest split; break point is right at
  $300M) — this is the A book's defining gate.
- **10:00 ET checkpoint** (a validated local optimum: 9:45 admits fade-prone spikes, 10:30 dilutes
  with weak late-bloomers).
- Up **≥10%** on the day by 10:00 (ideal band 15–20%, population capped ~30%) · **rvol ≥ 1.0 @ 10:00**
  (checkpoint-calibrated).
- **The edge — candle shape:** a red / low-position-in-range partial candle, i.e. pulled back off a
  higher intraday high. Refined gate: decline-from-open `∈ [1%, 6%]` (>6% is a breakdown, not a dip).
  *This dip gate is what turns the A book into the sharper-but-thinner A+ cell.*
- Tradeability: price ≥ $5 and ≥ $5M traded by 10:00 (a patience setup, ~1.5–2.5 clean setups/year).
- **Adds:** a second tranche at 3:30 ET only if the name built 6–15× rvol since 10:00 (confirmed
  accumulation).

**Stops / exits / hold.** Fixed ~5-day hold — the 10:00 entry improves the *fill*, the multi-day hold
*makes the money* (~84% of the return accrues after the day-D close; do not flip same-day). No tight
protective stop (any stop tight enough to fire lands in the −10..−20% band, which is the *best*
forward-hold cell). Hard cut below −20% from entry; take-profit at +35%+ any day, or +20%+ on day 1
only (for low-float, let winners run toward ~+60%).

**Book taxonomy.** **A** = all gates including low-float (<$300M), no early-dip requirement — the
*deployable* book; **A+** = A *and* an early red-pullback dip off the open — sharper but thin; **B** =
all gates except float (smaller size).

**Performance (2005 → mid-2026).** The **tradeable book is the A low-float book** (low-float + up ≥10%
by 10:00, rvol ≥ 1.0, 10:00 entry, 5-day hold): **clip PF 2.412 · 868 trips** — deep enough to actually
trade, profitable **22/22 years**, and knowable with no lookahead. The **A+ refined cell** (A + red
partial candle, decline-from-open 1–6%) is the sharpest edge — **95 trips · 70.5% win · clip PF 4.998 /
raw 5.842**, holding across eras (3.61 pre-2014 / 4.30 post-2015) — but at ~1.5–2.5 clean setups/year it
is too sparse to be the core book; treat it as a size-up tier layered on the A book, not a standalone
strategy. The core finding is a *timing* edge, not just selection: on names both the 10:00 engine and a
daily-close engine trade, entering at 10:00 vs the close gives PF 2.29 vs 1.98 (+33% net P&L); the 10:00
fill is on average 0.57%–1.47% *cheaper* than the close with identical exits — the entire P&L gap is the
entry price. The A low-float book survives the 2008 GFC (PF 1.70).

**Research log:** [docs/highflyer_v2_results.md](https://github.com/mrakgr/Trading-Edge/blob/research_summary_july_2026/docs/highflyer_v2_results.md)

---

## Portfolio at a glance

| System | Side | Horizon | Core pattern | Headline (settled book) |
|--------|------|---------|--------------|-------------------------|
| **OpeningDriverV2** | Long | Intraday | Opening-drive continuation | PF 4.11 · $2.39M · 1,028 tr (3× sized) |
| **DipRiderV4** | Long | Intraday | EMA-breakout momentum continuation | A: clip PF 1.86 · $1.43M · 1,860 tr (→ S: clip PF 3.01) |
| **VwapReclaimV3** | Long | Intraday | VWAP reclaim scalp | Cap PF 1.34 · $1.60M · 41k tr (A++ PF 4.33) |
| **MaxFlyerV3** | Short | Intraday | Pop-fade of new highs (stopped) | PF 3.77 · $3.03M · 2,510 tr · 0 losing months |
| **LowFlyer** | Long | Intraday | Flush-fade mean reversion | PF 3.38 · 68% win · $668k · 1,109 tr |
| **HighFlyerV2** | Long | ~5-day swing | Low-float pullback continuation | A: clip PF 2.41 · 868 tr (A+ dip cell: PF 5.0 · 95 tr) |

The books are deliberately uncorrelated by side and horizon: three intraday long-momentum engines that
fire in different windows (OpeningDriverV2 09:45–10:00, DipRiderV4 and VwapReclaimV3 after 10:00), a
short pop-fade that profits from the same overextension the longs avoid, an intraday long
mean-reversion book, and a multi-day low-float swing. Every headline number above is backed by a
per-year robustness table and a winner-concentration check in the linked research log.
