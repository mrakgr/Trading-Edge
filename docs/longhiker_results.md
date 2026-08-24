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

## 💀 S3 IS DEAD — read S5 first (2026-08-23)

**Everything in S3 below was measured on ONE MONTH (2026-07) and did not survive the full
post-2020 pass (387.8M trips).** 2026 is the single year whose top `eff_open` bands read positive;
S3b generalised from it. The section is kept verbatim as the record of the mistake, not as a
result. ⚠ Do not quote S3.

## S3 — First breakdowns (2026-07, 7,557,018 CLEAN trips at `signal_sec >= 35100`) — 💀 VOID

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


---

# S5 — THE FULL POST-2020 BASE PASS (2026-08-23)

`data/longhiker_trips_v1/y{2020..2026}/` — **387,833,933 trips** over **1,164,334 candidate
ticker-days**, 119 GB, 73 min. Raw PF is 0.996-1.015 every single year: a raw state sampler is a
coin flip, as it should be. Everything below is the clean book, `signal_sec >= 35100`
(342.9M trips).

## 💀 S5a — `eff_open` carries NO edge at any level

The S3b headline ("the edge lives at eff_open >= 0.6") is **withdrawn**. Fine bands × year,
mean h5m bp:

| band | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | **pooled** | n |
|---|---|---|---|---|---|---|---|---|---|
| 0.30-0.35 | −0.05 | −0.46 | −0.01 | −0.62 | −0.05 | −0.17 | +0.02 | **−0.19** | 118.6M |
| 0.55-0.60 | −0.22 | −1.71 | +1.13 | −0.22 | −2.01 | −0.95 | +0.33 | **−0.55** | 15.0M |
| 0.60-0.65 | −1.26 | −1.27 | +1.76 | −0.33 | +0.45 | −1.78 | −0.00 | **−0.41** | 10.4M |
| 0.70-0.80 | −1.14 | −2.19 | +1.08 | −0.86 | −1.30 | −1.10 | +0.45 | **−0.75** | 8.6M |
| 0.80-0.90 | −0.97 | −1.84 | −0.55 | −0.79 | −0.29 | −1.61 | +0.78 | **−0.81** | 4.2M |
| 0.90+ | +0.27 | +0.36 | +0.64 | +0.43 | −1.74 | −1.46 | +1.50 | **−0.07** | 2.5M |

Every band negative pooled; no rise at the top; the high bands are negative in 5 of 7 years.
**💀 THE METHOD LESSON: a cutoff was put on the table off one month.** The year table existed to
prevent exactly that and was not run first (`feedback_journal_tables_not_prose`).

Nor does the horizon structure survive — the "momentum dies at 10m" claim was also 2026-only:

| yr | n | h30s | h1m | h5m | h10m | h20m |
|---|---|---|---|---|---|---|
| 2020 | 41.7M | +0.03 | +0.03 | −0.36 | −0.31 | −1.09 |
| 2021 | 49.1M | −0.08 | −0.01 | −0.58 | −0.65 | −1.22 |
| 2022 | 45.6M | 0.00 | −0.01 | +0.06 | −0.15 | −0.09 |
| 2023 | 45.6M | −0.06 | −0.08 | −0.55 | −1.12 | −1.62 |
| 2024 | 46.5M | −0.03 | −0.07 | −0.41 | −0.39 | −0.46 |
| 2025 | 62.9M | −0.03 | −0.06 | −0.62 | −1.42 | −2.02 |
| 2026 | 51.5M | +0.06 | +0.02 | +0.18 | +0.54 | +0.45 |

A small uniform negative drift, no horizon shape. ⭐ **2026 is the outlier year in every table so
far** — worth remembering before any future smoke test is run on recent data.

The 09:40-09:45 lookahead slice flips sign year to year (−3.91 to +2.39 bp h5m) with no systematic
direction, so the universe lookahead is not manufacturing a phantom edge — but it is not harmless
either, just noisy. Keep the `signal_sec >= 35100` control on every headline.

## S5b — ⭐ The density cut (user, 2026-08-23) INVERTS the table

Trading only dense tape was the hypothesis: *"sparse rises will inevitably be faded."* The data
says the opposite, at every gap horizon, in every year.

| cut | keeps | h5m |
|---|---|---|
| whole clean book | 100% | −0.34 bp |
| `gap_600 < 30` | 4.1% | −4.19 bp |
| `gap_1200 < 60` | 3.9% | −3.17 bp |
| **`gap_60 < 4`** | **5.3%** | **−5.90 bp** |
| `gap_60 = 0` | 3.0% | −8.80 bp |

⚠ **The disproportion test does NOT fire here.** Rule 3 catches a filter that changes a *tiny*
slice yet moves the result hugely. This one *discards 94.7% of the book* — a large effect from a
large change is ordinary, not suspicious.

`eff_open` × year under `gap_60 < 4`, pooled: −4.29 / −5.89 / −10.11 / −12.59 / −29.23 / −52.99 bp
from the 0.30-0.40 band to 0.80+. **And it is BROAD, not a tail** — median and win rate move with
the mean, which is what separates a real effect from three fat trades:

| eff_open (dense) | n | mean bp | **median bp** | **win %** |
|---|---|---|---|---|
| 0.30-0.40 | 11,192,759 | −4.29 | −1.36 | 48.79 |
| 0.50-0.60 | 1,701,676 | −10.11 | −3.24 | 47.62 |
| 0.70-0.80 | 182,921 | −29.23 | −2.82 | 48.28 |
| 0.80+ | 56,285 | −52.99 | **−17.67** | **43.28** |

(whole-book control: median pinned at +0.13 to −0.07, win 49.4-49.8% across the same bands)

## S5c — ⭐⭐ …but the VOLATILITY control dissolves it

At matched `volat_20m`, `eff_open` barely matters. Dense book, **median** h5m bp:

| volat_20m | eff 0.30-0.40 | 0.40-0.50 | 0.50-0.60 | 0.60-0.70 | 0.70+ |
|---|---|---|---|---|---|
| **< 20bp** | **+0.18** | +0.02 | +0.35 | **+0.73** | +0.45 |
| 20-40 | −1.59 | −3.84 | −3.99 | −6.99 | −0.82 |
| 40-80 | −17.51 | −21.59 | −34.52 | −43.26 | −21.53 |
| **80bp+** | −99.65 | −104.32 | −126.93 | −121.42 | **−169.92** |

The gradient **down** the volatility axis is 100-500× the gradient **across** the efficiency axis.
Holding eff flat at 0.30-0.40 (T9) the volatility gradient survives intact (+0.62 → −76.71 in the
09:45-10:00 bucket) while the time-of-day gradient is second-order.

⭐⭐ **THE DENSITY CUT WORKS BY SELECTING HIGH-VOLATILITY TAPE, WHICH IS THE OPPOSITE OF ITS
INTENT.** `gap_60 < 4` requires a print in ~every one of the last 60 seconds; under an
`eff_open >= 0.3` gate that is not "a calm liquid mega-cap", it is "a name in a violent move right
now" — mean `volat_20m` in the dense subset is **35-91 bp** against **17-25 bp** for the whole book.

**So the single dominant signal in 388M trips is: 1s-tape volatility predicts NEGATIVE 5-minute
forward return, monotonically and hugely.** Every other feature examined so far is a proxy for it —
`dd` (ρ 0.78 with volat), the gap cut, and `eff_open` weakly. That is a fade signal, not a
Hitchhiker signal.

## S5d — Where the Hitchhiker actually shows up

Exactly one cell: **dense AND calm** — `gap_60 < 4` × `volat_20m < 20bp` × `eff_open 0.60-0.70`,
median **+0.73 bp** on n = 263,200. Positive, year-stable-ish, and far too small to trade at a 5m
horizon. That is the honest answer to *"do high efficiency ratios give an edge in momentum
trading"* on this universe: **no, not at this horizon.**

## S5e — What is NOT yet tested

`highs_20m_since_lo_*` (the reseat family), the `dd_20m_w{40,20,10}` spread, and the volume-trend
pair `vol_slope_*` / `vol_r_*` have not had the full-period year audit — their S3 readings are
void along with the rest of S3. ⚠ Test each **at matched volatility**, or the volatility signal
will impersonate every one of them.

---

# S6 — THE FEATURE AUDIT AT MATCHED VOLATILITY (2026-08-23)

Off `data/longhiker_study_v1.parquet` — a **narrow projection** of the base pass (same 342,908,590
clean trips, ~40 columns instead of 128, returns precomputed as FLOAT, 23 GB). Not a sample: every
table below is the full book. Built because S5 needed six scans of 119 GB and this one costs 86 s.

## ⭐⭐ S6a — THE HORIZON WAS THE WHOLE STORY (user's catch)

Every table in S5 reported **h5m**. The system's actual exit is the **30-bar timestop** (`r30s`),
and it behaves completely differently. Whole clean book, **median `r30s` bp**:

| volat_20m | eff 0.30-0.40 | 0.40-0.50 | 0.50-0.60 | 0.60-0.70 | 0.70+ |
|---|---|---|---|---|---|
| < 20bp | +0.06 | +0.08 | +0.14 | +0.15 | +0.13 |
| 20-40 | +0.16 | +0.18 | +0.30 | +0.28 | **+0.50** |
| 40-80 | +0.13 | +0.24 | +0.34 | +0.48 | **+0.61** |
| 80bp+ | **−1.30** | −0.78 | −0.99 | −0.04 | **+0.54** |

…against the same cells at **median `r5m`**:

| volat_20m | eff 0.30-0.40 | 0.40-0.50 | 0.50-0.60 | 0.60-0.70 | 0.70+ |
|---|---|---|---|---|---|
| < 20bp | +0.18 | +0.23 | +0.31 | +0.24 | +0.06 |
| 20-40 | +0.09 | −0.12 | −0.07 | −0.02 | +0.25 |
| 40-80 | −2.16 | −2.60 | −2.47 | −1.87 | −1.15 |
| 80bp+ | **−32.22** | −23.25 | −28.61 | −18.95 | −12.30 |

⭐⭐ **At 30 seconds `eff_open` is monotone-POSITIVE in every volatility band, and win rate rises
with it too** (v3: 49.56 → 50.32%; v4: 49.13 → 50.00%). ⭐ **`eff_open`'s real job is rescuing the
HIGH-volatility cells**: v4 runs −1.30 → **+0.54** across the eff axis — the one place in this whole
study where it is unambiguously load-bearing.

**So the structure is: momentum persists for ~30 seconds and has reversed by 5 minutes.** S5's
"volatility predicts negative forward returns" is a 5-minute statement, not a 30-second one. The
volatility fade only bites the production exit in the `80bp+` band.

💀 **METHOD LESSON, the second in one session:** S5 was written up entirely on a horizon the system
does not trade. **Always table the production exit first**, and the counterfactual marks beside it.

⚠⚠ **BUT THE MAGNITUDES ARE ~0.5 bp.** That is far below any realistic spread + fee on this
universe. Nothing here is tradeable as it stands; it is a *direction*, and the question it poses is
whether the effect can be concentrated by an order of magnitude, not whether to trade it.

⚠ Under `gap_60 < 4` the production exit still holds in the calm bands (v1 +0.13→+0.29, v2
+0.30→+0.57) but v3/v4 go negative — density hurts once volatility is high, even at 30 s.

## S6b — The reseat family at matched volatility

`highs_20m_since_lo_1200`, **median `r30s` bp**:

| volat_20m | 0 | 1-4 | 5-14 | 15-39 | 40-99 | 100-299 | 300+ |
|---|---|---|---|---|---|---|---|
| < 20bp | +0.02 | +0.02 | +0.03 | +0.04 | +0.07 | +0.13 | **+0.20** |
| 20-40 | +0.16 | +0.22 | +0.12 | +0.17 | +0.18 | +0.32 | −0.01 |
| 40-80 | +0.04 | **+0.93** | +0.59 | +0.30 | +0.39 | −0.23 | **−4.42** |
| 80bp+ | +0.32 | +6.79 | −0.47 | +0.13 | −0.94 | **−4.88** | −3.34 |

⭐ **The sign of the reseat feature is CONDITIONAL ON VOLATILITY** — and it flips:
- **calm tape (< 20bp)**: monotone RISING in extension, +0.02 → +0.20. More 20m highs is better.
- **volatile tape (40bp+)**: peaks at a *fresh* reseat (1-4) and collapses at extension, −4.42.

That reconciles the S3 reading (which pooled volatility and saw only the volatile half). ⚠ The
`v4 × 1-4` cell reading +6.79 is **n = 9,000** — noise, do not quote it.

## S6c — The drawdown spread `dd_20m_w10 / dd_20m`

**Median `r30s`** is essentially FLAT across the ratio inside every volatility band (v1 +0.10 →
+0.06, v2 +0.33 → +0.14, v3 non-monotone). At `r5m` there is a gradient in the volatile bands —
v3 −8.67 → −0.88, v4 −51.36 → −16.34 as the ratio goes 0→1.

⚠⚠ **BUT THE RATIO IS NOT INTERPRETABLE AS WRITTEN.** `dd_20m_w10/dd_20m ≈ 1` was supposed to mean
"the damage is happening now", but it is *also* what a move with **no drawdown at all** produces —
both windows near zero, ratio ≈ 1. The `~1` bucket holds 94.2M of 145M v1 trips, so it is dominated
by clean moves, not fresh damage. **The ratio must be crossed with the LEVEL `dd_20m` before it
means anything**, and that is not yet done. Verdict on the max-window family: **not yet earned, and
not yet refuted.**

## S6d — ⭐ The volume trend: the first feature that works as a VETO

`vol_r_300` (signed Pearson of ln(volume) vs bar index over 5m), **median `r30s` bp**:

| volat_20m | < −0.3 | −0.3..−0.1 | −0.1..0.1 | 0.1..0.3 | > 0.3 |
|---|---|---|---|---|---|
| < 20bp | +0.01 | +0.04 | +0.09 | +0.10 | +0.01 |
| 20-40 | +0.14 | +0.14 | +0.24 | +0.33 | **+0.37** |
| 40-80 | **−3.20** | +0.04 | +0.33 | +0.23 | −0.81 |
| 80bp+ | **−12.82** | −1.72 | +0.06 | −0.37 | **−4.10** |

Win rate at the production exit agrees: the `< −0.3` column reads **47.09%** (v3) and **46.49%**
(v4) against ~49.8% for the body.

⭐ **COLLAPSING PARTICIPATION IS THE STRONGEST SINGLE NEGATIVE IN THE STUDY** at the production
exit — −3.20 bp at v3 and −12.82 bp at v4, roughly 10-25× the size of the positive eff_open effect.
⭐ **SURGING participation is ALSO bad at high volatility** (`> 0.3`: −0.81, −4.10) — the shape is
an inverted U, not a ramp. So the Hitchhiker's "price up AND volume up" is only half right: what you
want is **steady** participation, and what kills you is participation falling out from under a
volatile move.

This is the first feature whose effect size is materially larger than eff_open's, and it is the one
nothing in S3 or S5 had touched. `vol_r_open` (T14) shows the same inverted U, weaker.

## S6e — Standing

| feature | verdict at the production exit |
|---|---|
| `eff_open` | ⭐ weakly POSITIVE, monotone in every volat band, ~0.5 bp. Rescues the 80bp+ cells. |
| `volat_20m` | fade only in the `80bp+` band at 30 s; dominates at 5 m |
| `gap_60 < 4` | ❌ hurts once volat is high; selects volatile tape, not calm tape |
| `highs_20m_since_lo_*` | ⭐ sign FLIPS with volatility — rises in calm tape, collapses in volatile |
| `dd_20m_w10/dd_20m` | ⚠ uninterpretable alone — must be crossed with the `dd_20m` level |
| **`vol_r_300`** | ⭐⭐ **strongest effect found; an inverted U. Use as a VETO on collapsing volume.** |

⚠ None of these has had a **year-by-year audit at the production exit** yet. Given that S3 died to
exactly that omission, no cutoff goes into a spec before the year table is on the page.

---

# S7 — `dd_20m` ON THE FULL BOOK (2026-08-23)

The S3d cell re-run on all **342,908,590** clean trips, production exit first.

## S7a — The ORDERING replicates; the MAGNITUDE was a one-month artifact

`dd_20m` (absolute level) × `volat_20m`, **median `r30s` bp**:

| volat_20m | <25bp | 25-50 | 50-100 | 100-200 | 200-300 | 300-500 | 500bp+ |
|---|---|---|---|---|---|---|---|
| < 20bp | +0.04 | +0.08 | +0.11 | +0.11 | +0.08 | +0.12 | — |
| 20-40 | +0.37 | +0.26 | +0.14 | +0.24 | +0.28 | +0.20 | −0.27 |
| **40-80** | **+1.13** | +0.39 | +0.46 | +0.22 | +0.23 | +0.05 | **−0.20** |
| 80bp+ | +1.73 | +4.62 | −0.03 | +0.22 | −0.22 | −0.82 | **−2.71** |

win% `r30s` moves with it in the v3 row: **50.80 → 50.03 → 50.01 → 49.71 → 49.76 → 49.47 → 49.21.**

⭐ **The direction is real: at matched volatility, LESS give-back predicts a better 30-second
forward return, monotonically.** The v1 row is flat — the feature only has anything to say once
there is volatility for the drawdown to be measured against.

💀 **But the S3d magnitudes do NOT replicate.** S3d read `v3 × <100bp` at **+4.92 bp mean h5m**; on
the full book the same cell's mean `r5m` is ≈ **−0.04 bp** (2.02 / −0.11 / −0.28 across the three
sub-bands, n-weighted). The *ordering* held; the *size* was one month of 2026. Same lesson as S3b,
one table over.

## S7b — ⭐⭐ THE YEAR AUDIT: the first thing in this study to pass one

`volat_20m ∈ [40,80)bp`, median `r30s` bp by year:

| dd_20m | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | n |
|---|---|---|---|---|---|---|---|---|
| **< 25bp** | **+2.11** | **+0.99** | **+1.61** | **+3.32** | **+1.10** | **+0.26** | **+0.15** | 377k |
| 25-50 | +1.19 | +0.25 | +0.40 | +2.91 | +0.82 | −0.34 | −0.31 | 698k |
| 50-100 | +0.50 | +0.27 | +0.83 | +0.33 | +0.63 | +0.42 | +0.06 | 2,969k |
| 100-200 | +0.45 | −0.28 | +0.69 | +0.19 | +0.25 | +0.10 | +0.02 | 8,264k |
| 300-500 | +0.03 | −0.65 | +0.41 | +0.46 | +0.42 | −0.49 | −0.06 | 3,444k |
| 500bp+ | +0.02 | −0.46 | −0.04 | −0.27 | +0.54 | −0.74 | +0.09 | 667k |

⭐ **`volat 40-80bp × dd_20m < 25bp` is positive in ALL SEVEN YEARS.** That is the first cell in
LongHiker to survive the audit that killed S3.

⚠⚠ **It is also decaying, hard: 2.11 → 0.99 → 1.61 → 3.32 → 1.10 → 0.26 → 0.15.** The two most
recent years are the two weakest, by a factor of ~10 against the 2020-2023 average. Whatever this
is, it is being competed away — and 2026 (+0.15 bp) is the year we would actually be trading.

## S7c — ⭐ `ddz = dd_20m / volat_20m` — the 2-D cell as ONE feature

The matched test says the feature is *drawdown relative to the volatility it occurred in*. Building
that ratio directly gives a single clean feature over the whole book instead of a corner cell.
**Median `r30s` bp:**

| ddz | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | n |
|---|---|---|---|---|---|---|---|---|
| **< 2** | +0.22 | +0.33 | +0.16 | +0.16 | +0.14 | +0.03 | +0.07 | 41.0M |
| 2-4 | +0.15 | +0.13 | +0.13 | +0.10 | +0.09 | +0.06 | +0.06 | 122.3M |
| 4-6 | +0.18 | +0.13 | +0.08 | +0.09 | +0.05 | +0.06 | +0.02 | 99.4M |
| 6-9 | +0.15 | +0.09 | +0.08 | +0.06 | +0.06 | +0.05 | +0.02 | 59.4M |
| 9-14 | +0.12 | +0.06 | +0.06 | +0.03 | +0.03 | +0.03 | +0.01 | 18.8M |
| **14+** | +0.05 | +0.07 | +0.04 | +0.01 | +0.02 | +0.01 | 0.00 | 2.2M |

win% `r30s`, same cells:

| ddz | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| < 2 | 49.93 | 50.47 | 49.82 | 50.08 | 49.87 | 49.34 | 49.72 |
| 14+ | 49.39 | 49.57 | 49.18 | **48.40** | 49.28 | 48.82 | 48.86 |

⭐⭐ **Monotone decreasing in EVERY ONE of the seven years, in both median and win rate, on 343M
trips.** This is the cleanest signal in the study: normalising the drawdown by volatility turns a
noisy corner cell into a feature that works across the entire book.

⚠ It is a **30-second effect only** — the `r5m` version of the same table is flat and sign-unstable.

## ⚠⚠ S7d — The size problem, stated plainly

The `ddz` spread is **~0.2 bp** end to end, and the strongest single cell in S7b reads **+0.15 bp in
2026**. A round trip on this universe costs one to two orders of magnitude more than that. Both
findings are **statistically solid and economically negligible** as they stand.

That is not a reason to discard them — it is the reason the next question is *concentration*, not
*validation*: the same features have to be stacked (`ddz` low × `vol_r_300` not collapsing ×
`eff_open` high × `volat` in band) to see whether the effect compounds into something that clears
costs, or whether it is a thin film spread over 343M trips that cannot be concentrated. ⚠ Every
step of that stack needs an iso-trip control (`feedback_iso_trip_control_for_stacked_features`):
PF rises mechanically when trips are cut.

⭐ And the decay is the thing to watch above all: if the effect is ~10× weaker in 2025-2026 than in
2020-2023, then a stack fitted on the pooled book is fitted mostly on years we will never trade.
**Weight every future breakdown toward 2025-2026, or run them as the holdout.**

---

# S8 — DENSITY, VOLATILITY, AND WHAT IS ACTUALLY INDEPENDENT (2026-08-23)

## ⭐⭐ S8a — The density effect's SIGN FLIPS with volatility

`gap_60` × `volat_20m`, **median `r30s` bp** (full 342.9M book):

| volat_20m | gap 0 | 1-3 | 4-9 | 10-29 | 30-49 | 50+ | n (M) |
|---|---|---|---|---|---|---|---|
| **< 20bp** | **+0.18** | +0.15 | +0.12 | +0.12 | +0.08 | **0.00** | 246.8 |
| **20-40** | **+0.39** | +0.17 | +0.28 | +0.21 | +0.17 | **+0.15** | 68.9 |
| 40-80 | **−0.80** | −0.30 | −0.18 | +0.24 | +0.20 | **+0.48** | 22.0 |
| 80bp+ | **−5.45** | −4.36 | −1.32 | −0.43 | +0.48 | **+1.06** | 5.2 |

win% `r30s` tracks it exactly — v1: **50.68 → 48.65** as tape thins; v4: **48.72 → 50.15** as it thins.

⭐ **You were right, in the regime that matters.** On calm and moderate tape — `volat_20m < 40bp`,
which is **315.7M of 342.9M trips (92%)** — denser tape is monotonically better, and sparse tape
takes the edge to exactly zero. The momentum pattern does not survive on sparse tape, precisely as
you said.

⚠ And it inverts above 40bp: there, dense tape is the *worst* cell. That is why S5b read the way it
did — I cut on `gap_60 < 4` **pooled**, which mixes a regime where density helps with one where it
hurts, then read the result at a horizon the system does not trade. Two errors compounding.

**Pooled over volatility, `gap_60` is year-stable** (median `r30s`):

| gap_60 | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | win% 2020→2026 |
|---|---|---|---|---|---|---|---|---|
| 1-3 | +0.36 | +0.14 | +0.06 | +0.09 | +0.13 | +0.08 | +0.04 | 50.93 → 50.05 |
| 4-9 | +0.31 | +0.18 | +0.15 | +0.08 | +0.14 | +0.09 | +0.03 | 50.87 → 49.85 |
| 10-29 | +0.23 | +0.17 | +0.15 | +0.15 | +0.12 | +0.10 | +0.06 | 50.38 → 49.78 |
| 30-49 | +0.12 | +0.12 | +0.07 | +0.07 | +0.06 | +0.03 | +0.03 | 49.72 → 49.36 |
| **50+** | +0.09 | +0.07 | +0.03 | +0.02 | +0.02 | +0.01 | +0.01 | 49.44 → **48.73** |

Positive in 7/7 years for every band, monotone in density in every year, and the win-rate ladder is
monotone in all seven. ⚠ `gap_60 = 0` is NOT the best row pooled (+0.18/−0.16/…) because it is where
the volatile-dense cells concentrate — the useful cut is a **band**, roughly `1 <= gap_60 < 30`,
not "as dense as possible".

## ⭐⭐ S8b — Volatility is an INVERTED U with a hard year-stable ceiling

Fine bands × year, **median `r30s` bp**:

| volat_20m | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | yrs +ve |
|---|---|---|---|---|---|---|---|---|
| < 10bp | +0.06 | +0.08 | +0.03 | +0.04 | +0.05 | +0.04 | +0.03 | 7/7 |
| 10-15 | +0.17 | +0.21 | +0.09 | +0.16 | +0.09 | +0.09 | +0.08 | 7/7 |
| 15-20 | +0.27 | +0.31 | +0.09 | +0.17 | +0.16 | +0.07 | +0.07 | 7/7 |
| **20-30** | **+0.30** | **+0.32** | **+0.25** | **+0.24** | **+0.20** | +0.05 | +0.08 | **7/7** |
| **30-40** | **+0.33** | **+0.32** | **+0.35** | +0.19 | **+0.35** | +0.01 | +0.14 | **7/7** |
| 40-60 | +0.41 | −0.04 | +0.69 | +0.33 | +0.20 | +0.03 | −0.01 | 5/7 |
| 60-80 | +0.18 | −0.60 | +1.25 | +0.57 | +0.76 | −0.13 | −0.47 | 4/7 |
| 80-120 | +0.82 | −1.85 | +0.37 | −0.55 | +0.19 | −0.35 | −1.11 | 3/7 |
| **120bp+** | −3.70 | −6.23 | −0.57 | −3.42 | −2.63 | −4.92 | −1.85 | **0/7** |

⭐ **`volat_20m >= 120bp` is negative in ALL SEVEN YEARS** — the cleanest veto found so far, and it
is a *ceiling*, not a floor. The peak sits at **20-40bp**, and both tails are worse. FlushFader's
40bp volatility **floor** does not transfer: this system wants the opposite end of that scale.

## ⭐ S8c — The correlation test: `gap_60` is genuinely independent of `eff_open`

|  | eff_open | gap_60 | volat_20m | dd_20m | bars_present | vol_r_300 | ols_r_300 | eff_20m |
|---|---|---|---|---|---|---|---|---|
| **eff_open** | 1.00 | **0.04** | **0.10** | −0.17 | **−0.40** | 0.01 | 0.25 | 0.39 |
| **gap_60** | 0.04 | 1.00 | **−0.14** | −0.17 | −0.11 | −0.08 | −0.04 | −0.12 |
| **volat_20m** | 0.10 | −0.14 | 1.00 | **0.80** | −0.19 | −0.00 | 0.02 | 0.05 |

⭐ **`ρ(gap_60, eff_open) = 0.04`** — as close to orthogonal as anything in this study. `gap_60` is
also nearly independent of volatility (−0.14) and of the volume trend (−0.08). It is new
information, not a restatement.

⚠ But ρ is not what decides it (`feedback_high_correlation_proves_nothing` — ρ lives in the bulk, a
gate lives in the tail). **The substitution test is S8a itself:** if `gap_60` were a volatility
proxy, its row would flatten once volatility is held fixed. Instead the gradient stays strong *and
reverses sign* between the 20-40 and 40-80 bands — something no proxy can do. `gap_60` earns its
place.

⚠⚠ **`ρ(eff_open, bars_present) = −0.40`** is the span-drift warning made numeric: `eff_open` is
substantially a *time-of-day* variable. Any spec that uses it must carry `eff_open_slots` or a
session-time control alongside.

## S8d — The spec as it now stands (⚠ NOT yet a system)

Everything below passed a 7-year audit at the production exit, and every effect is **sub-basis-point**:

| lever | direction | evidence |
|---|---|---|
| `volat_20m` band | 20-40bp peak; **hard ceiling 120bp** | S8b, 7/7 and 0/7 |
| `gap_60` band | roughly `1-29`; sparse tape kills it below 40bp volat | S8a, 7/7 |
| `ddz = dd_20m/volat_20m` | LOW | S7c, monotone 7/7 |
| `vol_r_300` | not collapsing (`> −0.1`), not surging | S6d |
| `eff_open` | high, ⚠ carries a time-of-day confound | S6a |

⚠ The next step is the **iso-trip-controlled stack** (`feedback_iso_trip_control_for_stacked_features`):
each lever added must beat both the previous stack tightened to the same trip count AND a random
subsample of it. And per S7d, **weight 2025-2026 or hold them out** — every lever here is weaker in
the two most recent years than in 2020-2023.

---

# S9 — VOLATILITY **ON DENSE TAPE** (user, 2026-08-23)

## ⚠ First, a metric correction: TIES

`win%` counts `r30s > 0` strictly, so every tie is scored a loss — and the tie mass is **not
constant across density**: 0.30% dense (`gap_60 < 10`), 1.34% mid, **2.42% sparse**. Price pins on
thin tape. Comparing raw win rates across density bands therefore penalises sparse tape by ~2pp for
free.

| density | n | tie% | raw win% | **win% ex-ties** |
|---|---|---|---|---|
| dense `<10` | 31.5M | 0.30 | 50.28 | 50.42 |
| mid `10-29` | 78.5M | 1.34 | 50.11 | 50.79 |
| sparse `30+` | 232.9M | 2.42 | **49.49** | **50.72** |

💀 So "sparse tape has a sub-50% win rate in every calm band" — which the S8a/T21 raw tables
appear to say — **is an artifact.** Ex-ties, sparse is not worse on win rate at all. The **median**
comparison is unaffected (medians are tie-robust) and is what the rest of this section uses.

## ⭐ S9a — Density amplifies the edge in the calm bands

Median `r30s` bp, calm volatility bands, tightening the density cut:

| volat_20m | sparse `30+` | mid+dense `<30` | dense `<10` |
|---|---|---|---|
| < 10bp | +0.02 | +0.11 | **+0.18** |
| 10-15 | +0.11 | +0.29 | **+0.37** |
| 15-20 | +0.23 | +0.47 | **+0.83** |
| 20-30 | +0.24 | +0.41 | **+0.59** |

(2020 column shown; the ordering holds in every year — all three cuts are 7/7 positive in bands
a-d.) ⭐ **Tightening density multiplies the calm-band edge by 2-4×.** That is the user's hypothesis
confirmed on the metric that is immune to the tie problem.

**The headline cell — `gap_60 < 10` × `volat_20m ∈ [15, 40)bp`:**

| yr | n | median bp | mean bp | win% ex-ties |
|---|---|---|---|---|
| 2020 | 1,488,108 | **+0.591** | +0.132 | 50.99 |
| 2021 | 1,840,636 | **+0.318** | +0.232 | 50.56 |
| 2022 | 1,490,035 | **+0.227** | −0.070 | 50.45 |
| 2023 | 985,071 | **+0.112** | −0.441 | 50.25 |
| 2024 | 1,098,410 | **+0.494** | +0.240 | 51.01 |
| 2025 | 1,832,915 | **+0.352** | +0.532 | 50.66 |
| **2026** | 2,418,007 | **+0.018** | +0.023 | **50.11** |

7/7 positive on the median, 7/7 above 50% ex-ties. ⚠ **And 2026 is ~1/30th of 2020.** The mean is
negative in two years while the median is positive in all seven — a left tail the median does not
see.

## ⭐⭐ S9b — THE VOLATILITY CEILING ONLY EXISTS ON DENSE TAPE. It INVERTS on sparse.

`volat_20m >= 120bp`, isolated:

| density | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | yrs +ve |
|---|---|---|---|---|---|---|---|---|
| **dense `<10`** median | −9.18 | −6.34 | −4.17 | −5.93 | −6.28 | −10.03 | −5.08 | **0/7** |
| dense `<10` win ex-ties | 48.25 | 48.60 | 48.91 | 48.69 | 48.93 | 48.13 | 48.98 | **0/7** |
| mid `10-29` median | −1.38 | −10.66 | +3.26 | −3.29 | +0.77 | −0.49 | +0.75 | 3/7 |
| **sparse `30+`** median | +1.02 | −2.23 | +3.32 | +3.94 | +1.85 | +1.88 | +2.19 | **6/7** |
| sparse `30+` win ex-ties | 50.43 | 49.10 | 51.36 | 51.48 | 50.82 | 51.01 | 51.12 | **6/7** |

⭐⭐ **A violent move on DENSE tape is the worst cell in the study (0/7, −4 to −10 bp). The same
violent move on SPARSE tape is one of the best (6/7, +1 to +4 bp).** Both metrics agree, both
directions, every year.

This is a true **interaction**, not either feature acting alone — and it is the strongest structural
result LongHiker has produced. The reading: violence *with* everyone participating is exhaustion and
fades; violence *without* participation is a thin tape still repricing and it continues.

⚠ It also explains S5b completely. `gap_60 < 4` pooled over volatility mixes the best calm cells
with the worst volatile ones, and I read the mixture at 5 minutes. Density and volatility are not
separable here — **neither one is interpretable without the other.**

## S9c — Revised lever list

| lever | direction | evidence |
|---|---|---|
| **`volat_20m` × `gap_60`** | ⭐⭐ **jointly, not separately** — dense+calm long; dense+violent is the fade | S9b, 0/7 vs 6/7 |
| `volat_20m` on dense tape | band **15-40bp**; ceiling **120bp** is dense-only | S9a/b |
| `gap_60` on calm tape | tighter is better, 2-4× | S9a |
| `ddz = dd_20m/volat_20m` | LOW | S7c, 7/7 |
| `vol_r_300` | steady, neither collapsing nor surging | S6d |
| `eff_open` | high ⚠ carries a −0.40 time-of-day correlation | S6a/S8c |

⚠⚠ The headline cell still reads **+0.02 bp in 2026** on 2.4M trips. Every lever found so far is
real, year-stable, and ~50-100× too small to pay for a round trip. **The open question is entirely
whether stacking concentrates them, and the 2026 column is the only one that answers it.**

---

# S10 — `ddz` vs THE EFF FAMILY, AND THE TIME CONFOUND THAT BREAKS S7c (2026-08-23)

## S10a — The correlations

Pearson over 343M trips (`lddz` = ln(ddz); the ratio has a heavy right tail, so where the two
disagree, believe the log):

| | ddz | lddz |
|---|---|---|
| **eff_open** | **−0.533** | **−0.622** |
| **eff_20m** | **−0.534** | **−0.563** |
| **eff_10m** | −0.305 | −0.350 |
| volat_20m | −0.104 | −0.090 |
| dd_20m | +0.301 | +0.224 |
| gap_60 | −0.071 | −0.090 |
| vol_r_300 | +0.015 | +0.007 |
| **bars_present** | **+0.507** | **+0.371** |

⭐ **`ddz` and `eff_open` are the most correlated cross-family pair in the study (−0.53 / −0.62).**
Mechanically obvious in hindsight: a move that gives back little relative to its own volatility *is*
an efficient move. They are two measurements of "how clean is this".

## ⚠⚠ S10b — But BOTH are session-time clocks, and that breaks S7c

`corr(ddz, bars_present) = +0.507` and `corr(eff_open, bars_present) = −0.402`. Mean `bars_present`
across the ddz bands runs **606 → 1,278 → 1,821 → 2,345 → 3,080 → 4,152** — i.e. `ddz < 2` is
essentially *"it is 09:45"* and `ddz > 14` is *"it is midday"*. Neither S7c nor any eff table
controlled for this.

**`ddz` inside a fixed time bucket** (median `r30s` bp):

| bucket | z1 <2 | z2 2-4 | z3 4-6 | z4 6-9 | z5 9-14 | z6 14+ | n (M) |
|---|---|---|---|---|---|---|---|
| **09:45-10** | 0.188 | 0.142 | 0.178 | 0.193 | 0.188 | 0.163 | 94.5 |
| 10-10:30 | 0.109 | 0.104 | 0.097 | 0.080 | 0.057 | 0.431 | 105.2 |
| 10:30-11:30 | 0.105 | 0.086 | 0.063 | 0.070 | 0.064 | 0.043 | 82.5 |
| 11:30-13:30 | 0.122 | 0.081 | 0.046 | 0.060 | 0.027 | 0.030 | 45.3 |
| 13:30-15:50 | 0.064 | 0.026 | 0.026 | 0.008 | 0.006 | 0.002 | 15.4 |

💀 **In the largest bucket (09:45-10:00, 94.5M trips) the ddz gradient is GONE — flat at ~0.18
across all six bands.** And holding eff_open fixed *inside* that bucket, it **reverses and rises**:

| eff_open (09:45-10 only) | z1 <2 | z2 2-4 | z3 4-6 | z4 6-9 |
|---|---|---|---|---|
| 0.30-0.40 | 0.044 | 0.106 | 0.134 | **0.174** |
| 0.40-0.50 | 0.137 | 0.129 | 0.193 | **0.364** |
| 0.50-0.60 | 0.171 | 0.195 | 0.259 | **0.556** |
| 0.60+ | 0.211 | 0.235 | 0.283 | — |

💀💀 **S7c IS RETRACTED.** "ddz monotone decreasing in every one of seven years, the cleanest signal
in the study" was **the time-of-day effect wearing a drawdown costume.** The monotone ladder came
from ddz sorting trips by session time, not by give-back. Inside a fixed bucket it is flat (t1) or
sign-reversed with eff held fixed, and only survives in t2-t5 where it is again confounded with the
same clock. ⚠ The `dd_20m` results in S7a/S7b inherit this doubt — they were never time-controlled
either.

## ⭐ S10c — `eff_open` SURVIVES the same control

Median `r30s` bp, inside each bucket:

| bucket | eff 0.30-0.40 | 0.40-0.50 | 0.50-0.60 | 0.60+ | n (M) |
|---|---|---|---|---|---|
| 09:45-10 | 0.099 | 0.118 | 0.170 | **0.226** | 94.5 |
| 10-10:30 | 0.095 | 0.086 | 0.142 | **0.151** | 105.2 |
| 10:30-11:30 | 0.046 | 0.104 | 0.143 | **0.266** | 82.5 |
| 11:30-13:30 | 0.061 | 0.085 | 0.097 | 0.025 | 45.3 |
| 13:30-15:50 | 0.016 | 0.007 | 0.004 | 0.188 | 15.4 |

⭐ Monotone-rising inside every bucket that carries volume (t1-t3 = 282M of 343M trips). **`eff_open`
is a real feature and `ddz` is not** — the exact opposite of what S5a and S7c concluded. (S5a's
"eff_open is dead" was the h5m horizon error, corrected in S6a; this is the time control confirming
S6a.)

## ⭐⭐ S10d — The biggest single effect in LongHiker is the CLOCK

Look down any column of either table: **09:45-10:00 → 13:30-15:50 costs roughly 3× the edge**
(0.188 → 0.064 at z1; 0.099 → 0.016 at eff 0.30-0.40). No feature found so far moves the number as
much as *what time it is*. The Hitchhiker is a morning pattern, and every breakdown from here must
either fix the bucket or carry it as a control column.

⚠ The methodological pattern is now three-for-three: **S3b** generalised from one month, **S5**
read the wrong horizon, **S7c** read an uncontrolled confound. Each time the artifact was
*monotone, year-stable and huge* — none of those properties distinguishes a signal from a
confound. The only things that have caught it are the year table, the production-exit table, and
the time control. Run all three before anything is called a feature.

---

# S11 — HIGHS vs DIPS, UNDER THE NEW STANDING DEFAULTS (user, 2026-08-24)

## ⭐ The standing filter, from here on

```sql
entry_px > 1  AND  gap_60 < 4  AND  eff_open > 0.70      -- always
volat_20m ∈ [0.004, 0.008)                                -- the focus band; varied elsewhere
```

Price above $1 (sub-$1 is fee-dead on every EU-accessible route), dense tape, and a smoothly
trending open. Study file: `data/longhiker_study_v2.parquet` (28 GB, same 342.9M clean rows, adds
the **full** `secs_since_hi/lo` ladder — v1 carried only the 60 and 1200 rungs and could not answer
this question at all).

⭐ `secs_since_{hi,lo}_N = 0` **means this bar printed that extreme** (the engine stamps
`lastHiSec`/`lastLoSec` in step 3, before the signal is captured in step 7), so the ladder is a pure
equality test with no reconstruction.

⚠ **n is thin under the exact defaults: 37,623 trips over 7 years** (~5,400/yr). Everything below is
therefore run twice — the user's cut, and `gap_60 < 30` (419,527) as the wider check that carries
the year and time controls.

## ⭐⭐ S11a — Buying new 20m HIGHS beats buying dips, decisively

**User defaults (`gap_60 < 4`), inclusive form:**

| event | n | med r30s | mean r30s | win% ex-ties | med r1m | med r5m |
|---|---|---|---|---|---|---|
| **20m HIGH** | 3,561 | **+5.75** | **+10.34** | **53.43** | +6.32 | −24.07 |
| *ALL (baseline)* | 37,623 | +2.63 | +2.11 | 51.50 | +1.85 | −20.68 |
| 1m low | 1,691 | **−1.23** | −12.84 | 49.29 | −5.28 | −36.46 |
| 2m low | 552 | +1.68 | −14.89 | 51.91 | −9.54 | −26.62 |
| 5m low | 45 | +23.31 | +4.72 | 68.89 | — | — |
| 10m low | **0** | — | — | — | — | — |
| 20m low | **0** | — | — | — | — | — |

**Wider check (`gap_60 < 30`), where the n is real:**

| event | n | med r30s | mean r30s | win% ex-ties | med r5m |
|---|---|---|---|---|---|
| **20m HIGH** | 32,834 | **+5.51** | **+6.99** | **54.26** | −5.10 |
| *ALL (baseline)* | 419,527 | +0.98 | +0.33 | 50.97 | −3.93 |
| 1m low | 16,044 | **−2.77** | −5.95 | 47.96 | −6.63 |
| 2m low | 6,196 | **−3.55** | −6.08 | 47.52 | −3.18 |
| 5m low | 425 | **−11.77** | −10.08 | 43.16 | −35.08 |

⭐⭐ **The answer is unambiguous: buy the high, not the dip.** In a strongly trending stock the dip
is monotone-worse the deeper it goes (−2.77 → −3.55 → −11.77), and the 20m high beats the baseline
by **4.5 bp** and beats the 1m dip by **8.3 bp** on the median, with a 6.3pp win-rate gap.
The mutually-exclusive ladder says the same thing and adds that *every* high rung is positive
(H20m +5.45, H2m +6.86, H1m +4.14) while *every* low rung is negative.

## ⚠ S11b — 10m and 20m lows DO NOT EXIST in this population

Zero trips, at both density cuts. That is not a data problem — it is what `eff_open > 0.70` means:
a stock trending that smoothly off the open does not print a new 10m or 20m low. **The "buy the deep
dip" branch of the question is structurally unanswerable inside this filter**, and the fact that the
event never occurs is itself the answer for anyone waiting to buy one.

## ⭐ S11c — Year audit (wider cut, median `r30s` bp)

| rung | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 | yrs +ve |
|---|---|---|---|---|---|---|---|---|
| **H20m** | +5.68 | +5.52 | +5.31 | +4.23 | +9.14 | +6.22 | **+1.64** | **7/7** |
| H1m | +11.16 | +2.22 | +0.72 | +3.55 | +2.27 | +9.53 | +2.12 | **7/7** |
| H2m | +8.25 | −5.36 | +15.96 | +17.37 | +11.98 | −6.87 | −3.83 | 4/7 |
| none | −0.01 | −0.83 | +1.31 | +1.56 | +1.99 | +1.54 | +0.12 | 5/7 |
| L1m | −5.80 | −0.34 | +0.64 | −2.95 | +0.21 | −2.77 | −6.63 | 2/7 |
| L2m | −4.23 | +1.90 | −4.58 | −13.25 | −5.05 | +1.25 | −2.34 | 2/7 |

⭐ **H20m is positive in all seven years** on 32,834 trips. ⚠ 2026 is again the weakest (+1.64
against a 5-9 bp body) — the decay is in this result too.

## ⭐ S11d — Time control

87% of the H20m population sits in the 09:45-10:00 bucket, so the rung and the clock are heavily
confounded. Inside that bucket, the ordering survives intact:

| rung | t1 09:45-10 | n |
|---|---|---|
| **H20m** | **+5.64** | 28,711 |
| none | +0.87 | — |
| L1m | −0.94 | — |
| L5m | −11.19 | — |

The +4.8 bp gap over the baseline is *within* one time bucket, so it is not the clock.

## ⚠⚠ S11e — It is a 30-SECOND effect and it reverses hard

`med r5m` for the 20m-high rung is **−5.10** (wider cut) and **−24.07** (strict cut) against +5.5 at
30 seconds. The continuation is real and it is *brief*: hold the 20m-high entry five minutes and you
give back everything and more. Consistent with S6a — momentum persists ~30 s and has reversed by
5 m. **Any spec built on this must exit fast, and exit timing is the dominant risk, not entry
selection.**

## S11f — Standing

⭐ **This is the first result in LongHiker with a magnitude worth discussing** — +5.5 bp median at
the production exit is ~10× everything in S6-S10. It is still not obviously past a round trip on
this universe, and 2026 reads +1.64, but it is the first cell in the right order of magnitude.

⚠ Not yet done: costs, the iso-trip control against a random subsample of the same size, and the
volat sweep (the user's stated intent to vary that axis — 40-80bp is the focus band, not a result).

---

# S12 — FAST vs SLOW RISES INTO THE 20m HIGH (user, 2026-08-24)

`speed_1m = signal_vwap / vwap_60_prev − 1`, on the **new-20m-high** trips only, under the standing
defaults. ⚠ Computed from `signal_vwap`, **not** `entry_px` — `entry_px` is the denominator of
`r30s`, so banding on it would share a term with the outcome and could manufacture a gradient.

Distribution on these trips (bp): p05 66 · p25 114 · **p50 158** · p75 212 · p95 326
(`gap_60 < 30`). These are *fast* moves by construction — a new 20m high in a smoothly trending
name.

## S12a — Wider cut (`gap_60 < 30`), where the n is real

| speed_1m | n | med r30s | mean r30s | win% ex-ties | med r5m |
|---|---|---|---|---|---|
| 0-25bp | 25 | +20.58 | +25.90 | 68.00 | +41.14 |
| 25-50 | 513 | +4.97 | +3.95 | 54.71 | −10.93 |
| 50-100 | 4,958 | +5.19 | +5.72 | 54.67 | −0.28 |
| **100-200** | **16,843** | **+6.07** | +6.49 | **55.06** | −3.71 |
| 200-400 | 9,477 | +4.22 | +5.75 | 52.80 | −10.17 |
| **400bp+** | 1,018 | +9.24 | +34.20 | **52.02** | **−58.86** |

## ⚠ S12b — The hypothesis is HALF right

The prediction was that slow rises win handily. **They do not** — the shape is another **inverted U**,
not a ramp:

- **Very fast (>200bp/1m) IS worse** — win rate falls 55.06 → 52.80 → 52.02, and the 5-minute
  column collapses to **−58.86 bp** at 400bp+. That half of the prediction holds.
- **But slow is not better than moderate.** 25-50 and 50-100 read +4.97/+5.19 against **+6.07** for
  100-200, and the win rates are flat across all three. The optimum sits in the middle.

⭐ **The 100-200bp band is the only one positive in all seven years** (+6.48 / +6.99 / +3.56 / +7.71
/ +10.45 / +3.38 / +3.58) and it holds 16,843 of the 29,273 trips. 50-100 is 5/7, 200-400 is 5/7,
25-50 is 5/7. The time control preserves the same mild inverted U inside the 09:45-10:00 bucket
(5.99 / 5.06 / **6.42** / 3.96 / 2.73).

## ⭐⭐ S12c — The MR intuition is right, but at the WRONG HORIZON

Read the `med r5m` column against `med r30s`:

| speed_1m | r30s | r5m | give-back |
|---|---|---|---|
| 50-100 | +5.19 | −0.28 | −5.5 |
| 100-200 | +6.07 | −3.71 | −9.8 |
| 200-400 | +4.22 | −10.17 | −14.4 |
| 400bp+ | +9.24 | **−58.86** | **−68.1** |

**A fast rise still continues for 30 seconds — and then gets faded, in proportion to how fast it
was.** That reconciles the FlushFader instinct with this data exactly: the fade is real and it
scales with speed, but it lives at the 5-minute horizon, not the 30-second one. At the production
exit even the 400bp+ rises are positive.

⚠ This makes the exit the whole system. The faster the entry, the shorter the window in which the
edge exists — a speed-dependent hold is the obvious next thing to test, and `fwd_vwap_*` answers it
without a re-run.

## S12d — On the strict cut (`gap_60 < 4`, n = 3,561)

Same shape, sharper and noisier: 100-200 reads **+12.07** (n = 1,401) while 200-400 reads −3.08
(n = 1,456) and 400bp+ −2.61. The drop past 200bp is more decisive on dense tape. ⚠ n per band is
in the hundreds-to-low-thousands over seven years; treat as directional only.
