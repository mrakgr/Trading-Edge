# LongHiker — the SMB "Hitchhiker", quantified

**1-second-bar intraday MOMENTUM, long only.** The first momentum system built on the 1s tape,
and the deliberate inverse of FlushFader's thesis: buy a clean, efficient move off the open and
*ride* it instead of fading it.

Engine: `TradingEdge.LongHiker/` (`Roll.fs`, `Intraday.fs`, `Backtest.fs`, `Program.fs`).
Universe: `mr_candidate_1s_v2` — the shared causal 1s-tape-native candidate table (user: *"no need
to reinvent the wheel"*), overridable via `LH_CANDIDATE_TABLE`.

---

## S1 — The design (2026-08-23)

### ⭐⭐ The sampler shape is the point

Every other engine in this repo fires on an **event** — a new 20m low, a breakout, a reclaim — so
one trip is one opportunity and the fill is one moment of microstructure. **A momentum system
evaluated that way cannot separate edge from the luck of where in the move you happened to
enter.**

LongHiker fires on a **state** instead:

| | |
|---|---|
| **ENTRY** | **EVERY** present bar whose efficiency-ratio-since-the-open is `>= 0.3`, inside the entry window and over the liquidity floors. No leg machine, no latch, no one-trip-per-move rule. |
| **FILL** | the **next** present bar's vwap (the house convention). |
| **EXIT** | a pure **bar timestop**, `HoldBars` (default **30**) present bars after the fill bar, filled **at** that bar's vwap. |
| **BACKSTOP** | `MocSec` 16:00, and the day's last bar. |

> *"Averaging out the entries during the breakdown will average out the microstructure noise."*
> — the user. That is the method, not a convenience.

⭐ **Why the exit fills AT the timestop bar rather than the one after.** The exit second was fixed
`HoldBars` bars earlier, so no information from that bar enters the decision. The *entry* needs
the next-bar convention because its decision is made from the signal bar's own close; a
pre-scheduled exit does not.

⭐ **The horizon is not a re-run.** `fwd_vwap_{30,60,120,300,600,1200}` mark the vwap at the first
present bar `>= entry + N` **wall-clock** seconds. Every alternative hold is one SQL expression
away; `--hold-bars` should not be swept.

### The entry gate, and only the entry gate

Live gates: `eff_open >= 0.3`, `dv_60 >= $100k`, `tc_60 >= 60`. That is the whole system. Everything
else is a **recorded column** and the tightening happens post-hoc over the trip parquet.

---

## ⚠⚠ S1a — Two knowability notes, both live

### 1. The 09:40 entry window is a universe-shaped lookahead

The user's spec starts entries at **09:40** (`--entry-start-sec 34800`). The candidate universe
gates on `dv_0945_tape` and `n_bars_1s` measured over **[09:30, 09:45)** — determined at 09:45.
So every trip with `signal_sec < 35100` was **selected using five minutes of tape that had not
happened yet**. This is exactly rule 5 of `lookahead_protocol.md` (the knowability clock vs
`EntryStartMin`).

It is **quarantined, not ignored**, and the engine prints a banner on every such run:

```sql
-- ⭐ THE CONTROL. Free, no re-run, because signal_sec is a recorded column.
WHERE signal_sec >= 35100
```

**Quote every headline both ways.** If the 09:40-09:45 slice carries the edge, the edge is the
lookahead. The engine also records its own tape-native `dv_0945_tape` so a live-consistent floor
can replace the table's.

First measurement (2026-08-20, one day, 323,368 trips): the pre-09:45 slice is **12.6%** of trips
and reads **+1.32 bp** mean vs **−0.90 bp** after — i.e. the contaminated slice *does* read better.
Watch it.

### 2. `eff_open` is NOT span-free

Kaufman efficiency over a **growing** window drifts with its length (measured on FlushFader's
anchored twins: median 0.771 at 10-20 slots → 0.139 at ≥ 80). `eff_open >= 0.3` at 09:41 is a
different statement from `eff_open >= 0.3` at 15:00. `eff_open_slots` is recorded for exactly this
reason — **condition on it in every breakdown.** The same warning applies to `ols_*_open` and
`vol_*_open`, whose span is `bars_present`.

---

## S1b — The feature set

All of it is recorded, none of it gates. Levels are stored raw; the distances are one SQL
expression away (`signal_vwap/lo_60 - 1`, `ln(hi_N/lo_N)`, …) and are deliberately **not**
duplicated as columns.

**The 30-present-bar slot block** — volatility, every efficiency ratio and the drawdown all read
the *same* slot-vwap stream, anchored on the session's opening slot (F5c: sub-30s returns are
microstructure).

| column | meaning |
|---|---|
| `volat_20m` / `volat_10m` | `EmaHlMa` hl = 40 / 20 slots of \|slot return\| (the F1-F8 vol lock) |
| `volat_open` | plain mean \|slot return\| since the open — no decay |
| `eff_20m` / `eff_10m` | `ln(V/V_40ago) / Σ40\|r\|` ∈ [−1,1]; nan until 41 / 21 slots |
| ⭐ `eff_open` | **THE GATE.** `ln(V_last/V_first) / Σ_all\|r\|`, anchored on the opening slot |
| `slot_count`, `eff_open_slots` | warmth / span |

**⭐ The slot drawdown** (user's design). Measured on slot vwaps, *not* 1s bars, so it is not a
noise statistic: `d_t = ln( max(V_{t−39..t}) / V_t ) >= 0` is a slot's log distance below its own
40-slot high, and `dd_20m` is the **max of `d` over the last 40 slots** — "the worst this move has
been underwater against its 20m high, in the last 20m".

| column | meaning |
|---|---|
| `dd_now_20m` / `dd_now_10m` | the *current* slot's distance below its 40 / 20-slot high |
| `dd_20m` / `dd_20m_w20` / `dd_20m_w10` | ⭐ `MaxMa` **40 / 20 / 10** of `dd_now_20m` |
| `dd_10m` | `MaxMa` 20 of `dd_now_10m` — the odd one out (20-slot *reference high*) |

⭐ **The max-window family** (user, 2026-08-23, after the S3d result). Same reference high — always
the 40-slot (20m) one — and the same per-slot distance; only the length of the running max changes.
They read as *"the worst give-back against the 20m high in the last 20m / 10m / 5m"*, and the
**spread between them dates the drawdown**: `dd_20m >> dd_20m_w10` means the damage is old and
already walked off; `dd_20m ≈ dd_20m_w10` means it is happening now. Nested by construction, so
every trip carries a free invariant:

```
dd_20m >= dd_20m_w20 >= dd_20m_w10 >= dd_now_20m >= 0
```

Verified over all 323,368 trips of 2026-08-20: **zero violations, zero nulls.** Means 61.6 / 54.5 /
45.5 / 24.9 bp. ⚠ `ρ(dd_20m, dd_20m_w20) = 0.974` — the w20 member may not be earning its place;
`ρ(dd_20m, dd_20m_w10) = 0.871`. Decide that with the substitution test, not with ρ
(`feedback_high_correlation_proves_nothing`), and note the likely-useful quantity is the **ratio**
`dd_20m_w10 / dd_20m`, which is derivable in SQL and not stored.

⚠ `dd_10m` stays outside the family on purpose: its reference high is the 20-slot high, so it is a
different measurement rather than a fourth member.

**⭐ Time measures** — wall-clock seconds since the last strict new N-present-bar high / low, for
N ∈ {60,120,300,600,1200} bars (1m/2m/5m/10m/20m): `secs_since_hi_*`, `secs_since_lo_*`.
`−1` = no such event yet this session (the anchor is then the open). Era-invariant, unlike a bar
count — and the pair is what makes *"is the 1m low or the 1m high more recent"* a comparison
instead of a chart impression.

**⭐ The reseat family** — `highs_20m_since_lo_{60,120,300,600,1200}`: how many new **20m highs**
have printed since the last new **N-bar low**. A stock that has made eight fresh 20m highs without
touching a 5m low is in a different state from one that has made one. By construction
`..._1200 >= ..._600 >= … >= ..._60`.

**⭐ The trend pair** — OLS of `ln(price)` **and** of `ln(volume)` against the present-bar index,
over the same horizons: **since the open**, and trailing 1m / 5m / 10m / 20m. `ols_slope_*` /
`ols_r_*` and `vol_slope_*` / `vol_r_*`. Slope × 6e5 ≈ bp/min; `r` is **signed**
(`sign(slope)·sqrt(R²)`).

> Price slope alone cannot tell a move the tape is *joining* from one it is *abandoning*. The
> Hitchhiker thesis is that the good ones are both — price rising **and** participation rising —
> and the pair is what makes that testable. Volume is logged for the same reason price is: a raw
> volume slope is shares-per-bar-per-bar and could never be banded across the universe.

**Gaps and activity, at matching horizons** — `gap_{open,10,30,60,120,300,600,1200}` (missing
seconds in the trailing wall-clock window) with `dv_*` and `tc_*` on the same horizon set. The gap
family is exactly the correction between the present-bar convention and wall clock.

**Levels** — `open_px`, `sess_hi/lo/vwap`, `hi_*` / `lo_*` at the five channels, `vwap_60`,
**`vwap_60_prev`** (the speed denominator — the momentum twin of FlushFader's flush speed),
`vwap_300`, `vwap_1200`.

### ⭐ Channels are PARTIAL-TOLERANT here

Unlike FlushFader's warm-only entry channel. At 09:40 the session is ten minutes old, so a "20m
high" *is* the session high — which is what a trader looking at the chart sees, and refusing to
answer would blank the headline reseat features across the whole early window the system exists to
trade. `bars_present` and `secs_since_hi_*` are recorded so warmth is filterable.

---

## S1c — Engineering notes

**⚠ The lifecycle is pointer-advance, not a per-bar scan.** Trips are appended in fill order, so
every deadline they carry — the timestop's bar index, each forward mark's ET second — is
non-decreasing down the list. Each schedule is therefore a single monotone cursor. The obvious
immutable spelling (`positions.[i] <- {p with ...}` once per open trip per bar) allocates a
~100-field record per trip per bar: at ~1,200 concurrent trips × ~20,000 bars × ~1 KB that is
**tens of GB of garbage per ticker-day**. The Faders can afford it; LongHiker cannot. The position
record's lifecycle fields are `mutable` and `all` is never compacted (the cursors index into it).

**Queue sharing** is reused as-is: one 1200-slot ring of 32-byte `RollBar` feeds 17 sums and 8 OLS
windows. `SumRoll.Project` / `OlsRoll.Project` stay abstract methods, not lambdas — see
`docs/queue_sharing.md` for why that is the difference between a win and a loss.

**Backpressure is tighter than the Faders'.** A LongHiker ticker-day carries hundreds to thousands
of trips, so one in-flight message is orders of magnitude larger than a FlushFader one: the results
channel is bounded at **64**, and each finished ticker-day is handed off immediately rather than
buffered into a per-day list.

**`--signal-stride N`** fires only every Nth qualifying bar — a *uniform subsample* of the same
signal set, unbiased for means. It exists as an escape hatch if a full-period run's trip count is
unmanageable. ⚠ Never report a stride run as a book.

---

## S2 — Smoke tests

### One day, 2026-08-20

```
candidates = 938 ticker-days       trips = 323,368  (10.4 s)      trips/tkd = 344.7
win rate   = 48.6%                 PF    = 0.920  [ATTRIBUTION ONLY — mc=0]
```

Every trip exits on the timestop at exactly 30 bars (the entry window closes at 15:50, so MOC never
binds). Parquet: **103 MB/day**.

Invariants verified over all 323,368 trips (all zero):
`eff_open < 0.3` · `entry_sec <= signal_sec` · `signal_vwap` outside `[lo_60, hi_60]` · `dd_20m < 0`
· `dd_20m < dd_now_20m` · reseat ordering violations · `secs_since_* < -1` ·
`ret_exit != exit_px/entry_px - 1` · signals outside the entry window.

Null rates (expected, and exactly the "some 20m features will be null at 09:40" the user
anticipated): `eff_20m` 58.2%, `eff_10m` 31.3%, everything else ~0.

### One month, 2026-07 (the sizing smoke test the user asked for)

```
candidates = 21,733 ticker-days    trips = 8,489,648  (107 s)     trips/tkd = 390.6
win rate   = 49.6%                 PF    = 1.029  [ATTRIBUTION ONLY — mc=0]
parquet    = 2.7 GB
```

**Projection to the full post-2020 base pass** (1,164,334 candidate ticker-days over 1,667
sessions = **53.6×** July 2026):

| | |
|---|---|
| trips | **~455M** |
| parquet | **~145 GB** |
| wall clock | **~96 min** at 14 workers |

⚠ Trip density is *not* uniform — the 2020-21 mover era is denser — so the base pass runs **year by
year** with a 60 GB free-space floor between chunks (`/tmp/lh_full.sh`). If it runs hot, the
remaining years go to `--signal-stride 5` (a uniform subsample, unbiased for means, 5× smaller).
Per-year parts are also the natural post-hoc partition.

---

## S3 — First breakdowns (2026-07, 7,557,018 CLEAN trips at `signal_sec >= 35100`)

⚠ One month. mc = 0, so PF and the bp figures are **attribution, not portfolio**. **No costs
modelled** — at a 30-second hold, a bp is not a lot of room. Everything below is a direction to
test, not a result.

Returns are basis points, measured from `entry_px`: `h30s` = the production 30-bar timestop,
`h5m`/`h10m` = the `fwd_vwap_300/600` marks.

### S3a — ⭐ The correlation matrix the user asked for

|  | dd_20m | dd_now_20m | dd_10m | eff_open | eff_20m | eff_10m | volat_20m | volat_10m | volat_open |
|---|---|---|---|---|---|---|---|---|---|
| **dd_20m** | 1.000 | 0.662 | 0.921 | −0.184 | −0.137 | −0.078 | **0.784** | 0.773 | 0.797 |
| **dd_now_20m** | 0.662 | 1.000 | 0.735 | −0.148 | −0.274 | −0.354 | 0.520 | 0.513 | 0.529 |
| **dd_10m** | 0.921 | 0.735 | 1.000 | −0.136 | −0.169 | −0.169 | 0.803 | 0.797 | 0.809 |
| **eff_open** | −0.184 | −0.148 | −0.136 | 1.000 | 0.378 | 0.359 | 0.140 | 0.147 | 0.124 |
| **eff_20m** | −0.137 | −0.274 | −0.169 | 0.378 | 1.000 | 0.587 | 0.051 | 0.063 | 0.027 |
| **eff_10m** | −0.078 | −0.354 | −0.169 | 0.359 | 0.587 | 1.000 | 0.053 | 0.063 | 0.038 |
| **volat_20m** | 0.784 | 0.520 | 0.803 | 0.140 | 0.051 | 0.053 | 1.000 | **0.998** | **0.997** |

Three readings:

1. ⚠⚠ **The drawdown feature is 78-80% volatility.** `corr(dd_20m, volat_20m) = 0.784`. Any raw
   `dd` table is mostly a volatility table wearing a hat — it **must** be read at matched volat
   (S3d). (ρ alone does not condemn it: `feedback_high_correlation_proves_nothing` — ρ 0.998 twins
   once read PF 2.92 vs 1.83 at matched selectivity. ρ lives in the bulk; the gate lives in the
   tail.)
2. ⭐ **The drawdown is nearly ORTHOGONAL to efficiency** (−0.08 to −0.18). That is the genuinely
   new axis this feature buys — "how much did the move give back" is not "how straight was it".
3. ⚠⚠ **`volat_20m` / `volat_10m` / `volat_open` are ONE feature, not three** — ρ 0.992-0.998. All
   three are the average \|slot return\| this session under different weightings. The information
   is in the *trajectory* (`volat_10m / volat_20m`), not in the levels. Do not spend three gates on
   them.

### S3b — ⭐ `eff_open` bands: the edge is at 0.6+, NOT at the 0.3 gate

| eff_open | n | h30s | h1m | h5m | h10m | h20m | win5m |
|---|---|---|---|---|---|---|---|
| 0.30-0.35 | 2,514,896 | −0.14 | −0.12 | −0.46 | −0.86 | −4.45 | 49.2% |
| 0.35-0.40 | 1,618,999 | −0.06 | −0.04 | −0.86 | −1.72 | −4.88 | 49.1% |
| 0.40-0.50 | 1,782,313 | −0.14 | −0.24 | −0.57 | −1.13 | −4.38 | 49.3% |
| 0.50-0.60 | 863,482 | −0.13 | −0.26 | −0.62 | −1.13 | −4.47 | 50.2% |
| **0.60-0.70** | 413,397 | +0.53 | +0.76 | **+1.99** | +0.92 | −6.91 | 50.6% |
| **0.70-0.80** | 211,968 | +0.88 | +0.63 | **+3.84** | **+4.91** | −0.03 | 52.5% |
| **0.80+** | 151,963 | **+1.23** | +0.51 | +1.72 | +4.40 | +1.90 | 51.4% |

**The 0.3 level the spec starts from is exactly where nothing happens.** Everything below 0.6 is
flat-to-slightly-negative; the whole signal sits in the top 10% of the gated population.

⭐ **AND MOMENTUM DIES AT ~10 MINUTES.** The `h20m` column is negative in six of seven bands
(−4 to −7 bp) while `h5m`/`h10m` are positive at the top. Held long enough, this book turns into a
Fader book. If that survives the full period it is the single most important structural fact about
the system — it sets the exit horizon before any feature work does.

### S3c — ⭐ The span control (the eff_open drift warning, discharged)

`eff_open` grows its window all session, so the top band could have been an early-session artefact.
It is not — `eff_open >= 0.6` beats `0.3-0.6` inside **every** span band:

| eff_open_slots | eff_open 0.3-0.6 (h5m) | eff_open 0.6+ (h5m) | n (hi) |
|---|---|---|---|
| < 20 | +0.88 | **+3.54** | 440,730 |
| 20-39 | +0.47 | **+0.88** | 285,432 |
| 40-79 | −2.03 | **+1.81** | 50,266 |
| 80+ | −0.59 | −3.29 | 900 |

(the 80+ cell inverts on n = 900 — ignore it). The effect also *decays with span*, consistent with
the drift warning: the strongest reading is the shortest window.

### S3d — ⭐⭐ The drawdown feature at MATCHED volatility

The raw `dd_20m` table is monotone-negative, but that table is 78% volatility. Crossed against
`volat_20m` it separates:

| volat_20m | dd_20m | n | h5m | h10m |
|---|---|---|---|---|
| < 20bp | < 100bp | 5,060,521 | −0.08 | −0.26 |
| < 20bp | 100-300 | 449,200 | +1.16 | +1.73 |
| < 20bp | 300bp+ | 1,800 | −3.16 | −16.38 |
| 20-40 | < 100bp | 910,597 | +0.35 | −0.57 |
| 20-40 | 100-300 | 696,605 | −2.37 | −4.88 |
| 20-40 | 300bp+ | 23,344 | −1.36 | −4.48 |
| **40-80** | **< 100bp** | 101,910 | **+4.92** | **+12.41** |
| 40-80 | 100-300 | 221,376 | +1.46 | −0.92 |
| 40-80 | 300bp+ | 31,760 | **−14.07** | −19.79 |
| 80bp+ | < 100bp | 4,138 | −35.83 | −54.49 |
| 80bp+ | 100-300 | 16,964 | −20.20 | −40.63 |
| 80bp+ | 300bp+ | 38,803 | −22.17 | −19.38 |

⭐ **At 40-80bp volatility the drawdown is a 27bp spread at 5m** (+4.92 → −14.07) — a *clean,
undrawn-down* move in a genuinely volatile name is the best cell in the table, and the same
volatility with a 300bp+ give-back is one of the worst. That is the Hitchhiker claim, stated
quantitatively: **it is not the size of the move, it is how little of it was handed back.**

⚠ It is NOT monotone everywhere (`v1` inverts on d1 vs d2), and `volat_20m >= 80bp` is bad in every
drawdown bucket — the tail is not tradeable regardless.

### S3e — ⭐ The reseat feature: fresh reseats good, extension bad

`highs_20m_since_lo_1200` — new 20m highs since the last new 20m low:

| k | n | h30s | h5m | h10m |
|---|---|---|---|---|
| 0 | 304,951 | −0.04 | −0.00 | −0.13 |
| **1-4** | 82,588 | +0.47 | **+1.13** | **+2.72** |
| 5-14 | 440,289 | +0.13 | +0.22 | +0.12 |
| 15-39 | 2,036,352 | +0.12 | +0.64 | +0.46 |
| 40-99 | 3,457,605 | +0.01 | +0.05 | +0.11 |
| **100-299** | 1,225,013 | −0.49 | **−3.13** | **−5.95** |
| **300+** | 10,220 | −0.49 | **−4.63** | **−7.93** |

⚠⚠ **This is the OPPOSITE of the naive hypothesis.** "More 20m highs without a 20m low" is not
"stronger trend" — past ~100 it is *extension*, and it reads −3 to −8 bp. The good cell is the
**freshly reseated** move (1-4 highs since the low). The 5m-scale twin
(`highs_20m_since_lo_300`) is flatter and less useful.

---

## S4 — Open questions for the full period

1. **Does the ≥ 10-minute reversal hold?** If so, the exit horizon is settled empirically and
   `HoldBars` should move off 30.
2. **Does `eff_open >= 0.6` survive year by year?** One month cannot answer it. The 0.3 gate should
   probably become 0.6 in the spec — with an iso-trip control
   (`feedback_iso_trip_control_for_stacked_features`), since raising it cuts trips ~10×.
3. **Is `dd` load-bearing at matched volat AND matched eff?** S3d matched volatility only.
4. **The 09:40-09:45 slice.** It reads better than the clean book on day one. Re-measure on the
   full period; if it stays better, the universe lookahead is doing the work and the entry window
   moves to 09:45.
5. **The volume trend pair** (`vol_slope_*` / `vol_r_*`) is recorded but untested — it is the half
   of the Hitchhiker thesis nothing above touches.
