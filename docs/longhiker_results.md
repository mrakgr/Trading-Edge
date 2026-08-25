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

`data/longhiker_trips_v1/y{2020..2026}/` — **387,832,933 trips** over **1,164,334 candidate
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

---

# S13 — TREND PERSISTENCE: autocorrelation, variance ratio, sign persistence (user, 2026-08-24)

Three new primitives in `TradingEdge.RollingMa`, all O(1) per push, each shipping an
**open-anchored** value and a **rolling 40-slot twin**:

| class | statistic |
|---|---|
| `AutoCorrMa(windowSize, maxLag)` | sample ACF at lags 1..maxLag |
| `VarianceRatioMa(windowSize, q)` | `Var(q-slot ret) / (q · Var(1-slot ret))` over overlapping q-sums |
| `SignPersistMa(windowSize)` | P(this return shares the previous one's sign), ties excluded, + the signed run |

`windowSize <= 0` = anchored, `> 0` = rolling. ⭐ **One implementation per statistic, two window
policies** — the anchored and rolling variants must compute the *same* statistic, and two separate
implementations are one edit away from silently disagreeing, which is exactly how a "rolling twin
control" stops being a control.

## The three design decisions that matter

⚠⚠ **1. Fed the SLOT-return stream, never 1s bar returns.** Autocorrelation of 1-second vwap
returns measures **bid-ask bounce**: strongly negative, magnitude a function of spread/price — a
liquidity feature wearing a trend costume, and precisely the confound class that has cost this
study three retractions. The 30-bar slot returns are the honest level (the F7 vol-lock finding).

⚠⚠ **2. MEAN-CENTERED, not optional.** The uncentered form `Σ r_t·r_{t−k} / Σ r_t²` is biased
upward by the series' **drift**, so on a trending name it reads high even for i.i.d. returns — it
would simply rediscover `eff_open` under a new name. Centering is what makes this a statement about
**persistence** rather than about **direction**, and therefore the only version that can add
anything.

⚠ **3. SIGNED for the trend stats, ABSOLUTE for volatility** — the same slot return, two uses.
Taking `abs` first would silently make every persistence measure read the volatility stream.

## Validation

`TradingEdge.RollingMa/TrendStats_Test.fsx` compares each class **at every push** against a naive
O(n) recomputation over exactly the points it claims to hold, both window policies, on a stream
built to be nasty: non-zero drift, a planted AR(1), exact zeros (the tie path), sign flips.

```
checks           47,948
worst rel err    autocorr 4.3e-15   varratio 1.9e-14   signpersist 0.0e+00
recovered AR(1)  rho1 0.3557 (planted 0.35)   VR(2) 1.356   VR(4) 1.713
i.i.d. CONTROL   rho1 +0.0066   VR(2) 1.0066   signpersist 0.5031   (expect 0, 1, 0.5)
```

⭐ The i.i.d. control is the part that matters: without it, a bug that returns "trending" for
everything passes every other check.

💀 The test caught one real bug: `SignPersistMa` windowed over **pairs** while its twins window over
**returns**, so its "last 40" was a different 40 (W returns form W−1 pairs). Off by 2.3e-2 — small
enough to have shipped unnoticed.

## ⭐⭐ First measurement (2026-08-20, 323,368 trips): VR does NOT agree with eff_open

| pair | ρ |
|---|---|
| `eff_open` × `vr2_open` | **−0.098** |
| `eff_open` × `vr4_open` | **−0.147** |
| `eff_open` × `ac1_open` | −0.119 |
| `eff_open` × `sign_pers_open` | +0.303 |
| `vr2_open` × `ac1_open` | **+0.898** |
| `eff_open` × `eff_20m` | +0.379 (for scale) |

⭐ **The variance ratio is essentially orthogonal to the efficiency ratio — very slightly
NEGATIVE.** That is the best possible answer to "will it agree with eff": it is a genuinely new
axis, not a restatement. The reason is that the two measure different things — `eff` is
displacement ÷ path length (direction *and* smoothness), while VR asks whether returns **predict
each other**. A steady low-noise drift is highly efficient with near-zero autocorrelation: pure
drift, no persistence.

⚠ `vr2` and `ac1` correlate **0.898** — near-twins, as theory requires (VR(2) ≈ 1 + ρ₁). Prefer VR:
same information, less noise. `sign_pers` is a third, partly independent measure (0.30 with both).

Population means on the gated book: `ac1_open` **+0.187**, `vr2_open` **1.108**, `sign_pers` **0.588**
— i.e. an `eff_open >= 0.3` universe genuinely is positively autocorrelated, as it should be.

⚠ Untested: everything above is one day and the correlations only. The features go into base pass
**v2** (`data/longhiker_trips_v2/`, 13 new columns) and must clear the standing three controls —
year table, production exit, time bucket — before any of them is called a feature.

---

# ⚠⚠ S14 IS TRIP-WEIGHTED — read S15 first (2026-08-24)

Every number in S14 pools TRIPS. On this rung the trip count per ticker-day is itself an
**outcome variable** (a day that keeps making new 20m highs is a day that went up), so trip-pooling
weights days by how well they went. Equal-weighted by ticker-day the cell **loses money in every
year, including the good ones.** S14 is kept as the record; S15 is the correction.

# S14 — THE VARIANCE RATIO ON THE 20m-HIGH RUNG (2026-08-24) — ⚠ TRIP-WEIGHTED

Base pass **v2** (`data/longhiker_trips_v2/`, 387,832,933 trips — ⚠ trip counts identical to v1 in
all seven years, as adding recorded columns must be). Study file `longhiker_study_v3.parquet`
(20 GB). ⚠ v1's corpus was deleted only after that identity check.

## S14a — On the whole default population, VR says almost nothing

Standing defaults (`entry_px>1`, `eff_open>0.70`, `gap_60<30`, `volat 40-80bp`), `vr4_roll` bands:
median `r30s` **1.12 / 0.19 / 0.84 / 1.02 / 1.75 / 0.68**, win% 51.16 → 50.80. Flat, no year
stability, no time gradient. **On its own, VR is not a feature here.**

## ⭐⭐ S14b — On the NEW-20m-HIGH rung it is the strongest thing in the study

Same filter **plus `secs_since_hi_1200 = 0`** (the S11 rung):

| vr4_roll | n | med r30s | mean r30s | win% ex-ties | med r5m |
|---|---|---|---|---|---|
| < 0.7 | 12,870 | +3.19 | +1.72 | 52.76 | −2.36 |
| 0.7-0.9 | 5,149 | +2.80 | +3.10 | 52.50 | −9.49 |
| 0.9-1.1 | 4,967 | +7.04 | +14.08 | 55.63 | −5.82 |
| **1.1-1.3** | 3,621 | **+11.79** | +14.68 | **57.67** | −2.56 |
| **1.3-1.6** | 3,389 | **+10.08** | +14.96 | **57.50** | **+0.46** |
| 1.6+ | 2,838 | +5.07 | +6.26 | 53.63 | **−16.91** |

⭐ A clean **inverted U peaking at VR ∈ [1.1, 1.6]**: median 3.19 → **11.79** (3.7×) and win rate
52.76% → **57.67%** (+4.9pp). And `1.3-1.6` is the **only cell in this entire study positive at
5 minutes** (+0.46) while `1.6+` is −16.91 — the prediction that bursts overshoot, confirmed.

## S14c — The controls

**TIME** ✅ — inside 09:45-10:00: 3.45 / 6.36 / **11.49** / 5.59. The shape survives.

**SUBSTITUTION vs `ac1_roll`** ✅ — VR keeps its gradient inside **every** ρ₁ band:

| ac1_roll | VR<0.9 | 0.9-1.1 | **1.1-1.6** | 1.6+ |
|---|---|---|---|---|
| < 0.05 | 2.06 | 10.60 | **15.01** | −30.78 |
| 0.05-0.2 | 4.68 | 1.44 | **9.27** | 3.06 |
| 0.2-0.35 | 5.17 | 7.88 | **10.71** | 6.46 |
| 0.35+ | 2.80 | 13.28 | **10.72** | 5.70 |

The mirror table is much weaker — ρ₁'s own gradient inside a fixed VR band is noisy and small.
⭐ **VR(4) is the load-bearing member of the pair, not ρ₁**, which is exactly what the identity
predicts: VR(4) carries ρ₂ and ρ₃ that ρ₁ cannot see.

**YEAR** ⚠⚠ — passes six years and **breaks in the seventh**:

| vr4_roll | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | **2026** |
|---|---|---|---|---|---|---|---|
| < 0.9 | +1.46 | +2.15 | +1.05 | +2.16 | +6.29 | +5.68 | **+3.45** |
| 0.9-1.1 | +9.45 | +6.53 | +10.71 | +2.24 | +10.02 | +6.00 | **−1.66** |
| **1.1-1.6** | **+14.18** | **+11.17** | **+17.21** | **+17.48** | **+13.25** | **+9.16** | **−0.12** |
| 1.6+ | +5.31 | +8.96 | +4.09 | +5.36 | +15.59 | −0.25 | +0.23 |

win% ex-ties for the 1.1-1.6 band: **60.0 / 56.7 / 60.9 / 61.3 / 60.1 / 56.2 / 49.9**.

💀💀 **In 2026 the ordering INVERTS** — the low-VR band becomes the best and the peak band goes
negative on both median and win rate. n = 910 for that cell, comparable to other years, so it is
not a sample-size artifact.

## ⚠⚠ S14d — 2026 is now THE question, not a footnote

Every feature in this study is weakest in 2026 (S7b: +2.11 → +0.15; S11c: H20m +5.68 → +1.64;
S12: the 100-200bp speed band +6.48 → +3.58). **VR is the first one that does not merely weaken but
REVERSES.**

Three candidate explanations, none yet tested:
1. **Regime** — 2026 differs structurally and the effect returns.
2. **Partial year** — the corpus ends 2026-08-21, so "2026" is ~8 months, all of it winter/spring.
   ⭐ Testable immediately: split 2025 the same way and see whether Jan-Aug 2025 also inverts.
3. **Competition** — the effect is being arbitraged out, and 2026 is what live trading looks like.

⚠ Until that is settled, **nothing here goes into a spec.** A 57.7% win rate at 11.8 bp across
2020-2025 is by a wide margin the best cell LongHiker has produced — and it is worth exactly
nothing if 2026 is the truth rather than the exception.

⭐ Note what did NOT break: `ac1_roll` in [0.2, 0.35] is **7/7 positive including 2026** (+13.95 /
+6.24 / +4.83 / +9.18 / +11.93 / +8.73 / **+5.30**). Weaker than VR's best cell in 2020-2025, but it
is the only persistence measure still standing in the most recent year.

---

# 🛑 S15 — THE TRIP COUNT IS AN OUTCOME VARIABLE (2026-08-24)

The user asked whether one big losing trade was skewing 2026. It is not — and answering it exposed a
much larger problem that applies to **every trip-pooled table in this document**.

## S15a — Not a tail, and not a partial year

**Outlier check** (peak cell = 20m high × `vr4_roll` 1.1-1.6):

| set | n | median | mean | win% ex-ties |
|---|---|---|---|---|
| 2026 raw | 910 | −0.13 | −2.25 | 49.89 |
| 2026 ex-top-20 | 890 | +0.53 | −0.41 | 50.45 |
| 2020-2025 raw | 6,100 | +12.81 | +17.36 | 58.73 |
| 2020-2025 ex-top-20 | 6,080 | +12.39 | +17.21 | 58.74 |

Trimming the 20 most extreme trades moves nothing. ⭐ It could not have: the headline was already a
**median** and a win rate, and neither is movable by a handful of trades.

**Partial-year test** — restrict *every* year to Jan-Aug, the months 2026 has:

| yr | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| median r30s | +15.13 | +17.08 | +17.23 | +17.67 | +15.14 | +11.09 | **−0.13** |
| win% ex-ties | 61.19 | 59.65 | 60.69 | 62.65 | 61.87 | 58.95 | **49.89** |

💀 **The partial-year hypothesis is dead.** Like for like, 2026 still collapses.

## 🛑🛑 S15b — But the real problem is the weighting

The cell holds 7,010 trips — spread over only **565 ticker-days in 2020-2025** (91-119 per year) at
**8.5-13.1 trips per ticker-day**. And the trip count per day is not incidental:

| yr | corr(trips that day, that day's mean return) | mean day, k ≥ 10 | mean day, k < 10 |
|---|---|---|---|
| 2020 | **+0.47** | +26.15 | −25.70 |
| 2021 | **+0.46** | +41.27 | −40.32 |
| 2022 | +0.33 | +18.05 | −13.52 |
| 2023 | +0.37 | +21.00 | −28.65 |
| 2024 | +0.29 | +17.74 | −28.14 |
| 2025 | +0.34 | +21.52 | −27.98 |
| 2026 | +0.27 | +9.02 | −25.92 |

⚠⚠ **Days that produce more trips are days that win — mechanically.** A trip fires on every bar
that prints a new 20m high; a stock that keeps printing new 20m highs is a stock that keeps going
up. **The trip count IS an outcome variable.** Pooling trips therefore weights each day by how well
it went, roughly 10:1 in favour of the winners.

Both weightings, side by side:

| era | ticker-days | trips | **equal-weight day** | trip-weighted | % days up |
|---|---|---|---|---|---|
| 2020-2025 | 565 | 6,100 | **−8.48** | **+17.36** | **46.0%** |
| 2026 | 107 | 910 | **−15.47** | −2.25 | 37.4% |

⭐⭐ **Equal-weighted by ticker-day the cell loses money in EVERY year** (−7.05 / −8.48 / 0.00 /
+3.11 / +0.32 / −6.39 / −8.35 median day), and only 46% of days are up. The 58.7% trip win rate and
+12.81 bp median were a **weighting artifact**, not an edge.

## S15c — What is and is not salvageable

⚠ Trip-weighting is not *arithmetically* wrong for P&L: if you truly sized every signal identically,
your realised total would be the trip-weighted number. But it means the strategy is **pyramiding** —
it adds size precisely when the trend persists — and it must be described and risked that way, not
as "58.7% of trades win". Its real profile is:

- **~110 independent ticker-days per year**, not 900 trips
- **the median day loses**; the mean is carried by a right tail
- exposure is ~10× larger on the days that happen to work, which is not knowable at entry

That is the lottery profile, and it is the same shape the V2 flagship was retired for
(`project_v2_size_without_crowd_2026-07-26`: monthly P&L = lottery → satellite book).

## 🛑 S15d — THE METHOD LESSON (the fourth this session, and a new class)

The three prior traps were **one month** (S3b), **the wrong horizon** (S5), and **an uncontrolled
time confound** (S7c). This is a fourth and it defeats all three defences:

> **In a state sampler, the NUMBER of trips an episode produces can itself be an outcome. When it
> is, every trip-pooled statistic — mean, median, win rate — is self-selecting, and the year table,
> the production-exit table and the time control all pass anyway.**

⭐ The tell is cheap and should now be standard: **`corr(trips_per_episode, episode_return)`**.
Anything materially above zero means the trip-pooled number is weighted by the outcome. Report the
**equal-weight-by-ticker-day** figure alongside every headline, and size significance on ticker-days
(`feedback_three_mc_questions`: *"null must resample TICKER-DAYS, not trips"* — the same warning,
which this study had recorded and did not apply).

⚠ **Every trip-pooled table in S3-S14 is now suspect** and must be re-read at the ticker-day level
before anything is quoted. The features may still order correctly — high VR still beats low VR at
the day level in 5 of 7 years — but the *magnitudes and win rates are not what was reported*.

---

# S16 — 2026 BY MONTH, AND EARLY vs LATE IN THE RUN (user, 2026-08-24)

## ⚠ First, a correction to S15's framing (user)

S15 called the trip-weighting "pyramiding". **It is not** — every position is an independent
30-present-bar hold, closed on its own clock, not size added to a runner. The accurate statement is
narrower and still holds:

- ✅ **The P&L is real.** If every signal is sized identically, the realised total *is* the
  trip-weighted number. Nothing is fake.
- ⚠ **The significance was overstated.** 6,100 trips are 565 ticker-days at 8.5-13 trips each; the
  independent draw is the ticker-day, so `n` for any confidence statement is ~110/year, not ~900.
- ⚠ **The risk is concentrated.** The typical qualifying day loses; the aggregate is carried by the
  minority of days that keep printing new highs — which is also *why* those days generate more
  trips.

## S16a — 2026, month by month

| month | trips | ticker-days | med r30s | mean | win% ex-ties | worst | best |
|---|---|---|---|---|---|---|---|
| 2026-01 | 104 | 14 | −9.53 | −10.79 | 44.66 | −147.69 | +96.69 |
| 2026-02 | 104 | 12 | +2.21 | −7.81 | 52.88 | −222.60 | +113.91 |
| 2026-03 | 134 | 10 | +8.75 | +8.54 | 54.89 | −165.85 | +198.03 |
| 2026-04 | 21 | 4 | −1.98 | +0.39 | 47.62 | −37.01 | +64.52 |
| 2026-05 | 96 | 13 | −5.29 | −14.88 | 48.96 | −203.47 | +78.68 |
| 2026-06 | 258 | 24 | +6.15 | +7.95 | 55.08 | −147.73 | +136.07 |
| 2026-07 | 128 | 19 | −6.30 | −5.13 | 44.53 | −133.94 | +127.22 |
| 2026-08 | 65 | 11 | −14.09 | −18.93 | 34.92 | −107.36 | +126.19 |

3 of 8 months positive, no within-year trend — it alternates. ⚠⚠ **But look at the ticker-day
column: 4 to 24 per month.** A monthly figure here rests on ~10 independent episodes, so the
month-to-month scatter is almost entirely noise and nothing should be read into any single month.
The year as a whole (107 ticker-days) is the smallest unit worth interpreting, and it reads
trip-weighted −2.25 / equal-weight-day −15.47 / 37.4% days up.

## ⭐⭐ S16b — EARLY beats LATE, and it is the first cell that survives equal-weighting

`highs_20m_since_lo_1200` on this rung = **which number-in-sequence this new 20m high is**. ⚠ Note
the distribution: almost everything is ≥ 21, most ≥ 51 — because on an `eff_open > 0.70` name a new
20m *low* essentially never prints (S11b), so the counter is anchored at the open and just
accumulates. It is therefore "how many 20m highs so far today", closer to a run-maturity clock than
to a leg counter.

**Trip-pooled:**

| sequence | trips | ticker-days | med r30s | win% ex-ties | med r5m |
|---|---|---|---|---|---|
| 21-50 | 1,008 | 222 | **+12.20** | **60.80** | +0.02 |
| 51+ | 5,989 | 516 | +10.48 | 57.00 | −1.93 |

**⭐ Equal-weight by ticker-day — the honest unit:**

| sequence | ticker-days | med day | mean day | % days up |
|---|---|---|---|---|
| **21-50** | 222 | **+2.61** | −0.29 | **52.70%** |
| 51+ | 516 | **−6.87** | −9.93 | **43.02%** |

⭐⭐ **The early band is the first cell in this entire study that is not negative once
equal-weighted** (+2.61 median day, 52.7% of days up) while the late band is clearly negative. The
user's hypothesis — early trades in the run beat late ones — is confirmed, and it is confirmed on
the weighting that S15 showed to be the load-bearing one.

**Year control (trip-pooled median):**

| sequence | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|
| **21-50** | +11.44 | +41.35 | +25.45 | +18.14 | +12.13 | +7.18 | **+0.01** |
| 51+ | +15.43 | +8.32 | +15.88 | +16.93 | +13.52 | +9.46 | **−0.68** |

21-50 is positive in **7/7 years** (2026 only just, at +0.01). ⚠ But it is thin: 63-179 trips and
~32 ticker-days per year. Directional, not decisive.

## ⭐ S16c — The exit change this implies (user's proposal)

S12 already concluded the exit is the whole system; S16b says the same thing from the other side —
the edge lives early in the run and decays as the run matures, which a **fixed 30-bar timestop
cannot exploit**. The user's proposal is the right shape:

> exit at the **1m low**, OR when **no new high has printed in the last 30-60s** — probably both.

⚠ This cannot be answered post-hoc: `fwd_vwap_*` are fixed-horizon marks, and a trailing/decay exit
needs per-bar state after the fill. It is an engine change (an exit-condition block in
`IntradaySystem`, plus counterfactual exit marks so several variants can be compared from one run),
followed by another base pass.

⭐ Worth doing as **counterfactual marks rather than a hard exit** — record, for every trip, the
fill price at (a) the first new 1m low after entry, (b) the first bar with no new high in the last
{30, 60} seconds, and (c) the existing 30-bar timestop. One run then answers every combination,
which is the same discipline that made `fwd_vwap_*` worth carrying.

---

# S17 — EXIT MARKS, THE EXTREMES GATE, AND THE 09:50 HYBRID (user, 2026-08-24)

## ⭐ S17a — The open variants already existed

`volat_open`, `vr2_open`, `vr4_open` and `eff_open` are **all recorded in the base pass already**.
Only `volat_open` was missing from the study file — my omission when projecting it, not an engine
gap. So the user's hybrid needs **no engine change**:

```sql
-- before 09:50:30 the 20m features are structurally cold (volat_20m/eff_20m warm at
-- 41 slots = 1,230 present bars ~= 20.5 min after 09:30), so use the anchored ones
case when signal_sec < 35430 then volat_open else volat_20m end
case when signal_sec < 35430 then vr4_open   else vr4_roll  end
```

⚠ The two halves are **not the same statistic** — anchored features grow their window all session
and carry the time-of-day defect S10b measured (`corr(ddz, bars_present) = +0.51`). A hybrid column
is therefore discontinuous at 09:50:30 by construction, and any breakdown on it must carry
`signal_sec` so the seam is visible. It is the right operational choice; it is not a free one.

## S17b — The extremes-only gate

`SignalOnExtremesOnly` (default **true**): a bar opens a trip only if it prints a new
{1,2,5,10,20}m **high or low**. Measured on 2026-08-20: **323,368 → 29,990 trips**, 344.7 → 32.0 per
ticker-day — a 10.8× cut, matching the 88% "none" rung from S11a.

⚠⚠ **This shrinks but does not remove the S15 problem.** A day that keeps making new highs still
produces more trips than one that does not, so the trip count stays outcome-correlated. It reduces
the multiplier, not the mechanism. **Keep reporting equal-weight-by-ticker-day.**

## ⭐ S17c — Counterfactual exit marks

Five new marks, each recorded as (px, sec) and **never enforced** — the `fwd_vwap_*` discipline, so
one base pass answers every exit rule and every intersection of them:

| mark | condition |
|---|---|
| `ex_lo` | first new **1m low** after the fill |
| `ex_nohi60_30` / `_60` | first bar with **no new 1m high** for ≥ 30 s / 60 s |
| `ex_nohi1200_30` / `_60` | first bar with **no new 20m high** for ≥ 30 s / 60 s |

💀 **A design bug the smoke test caught.** The first version anchored on `lastHiSec` alone. But "no
new 20m high for 30 s" is TRUE almost always — most bars are nowhere near a 20m high — so a trip
entered on a 1m *low* fired it on the next bar: **median 5 seconds from fill.** That is an immediate
exit wearing a trailing exit's name.

⭐ The fix anchors on **`max(lastHigh, this trip's own fill)`**, so the rule means what it should for
any entry: *"30 seconds have passed without a new high since I got in."* Median fill-to-mark went
5-6 s → **34-36 s** (30 s rules), **65-69 s** (60 s rules), **85 s** (1m low).

⚠ Still one monotone cursor per mark, so it stays O(1) per bar: `EntrySec` rises with the index, so
`max(lastHiSec, EntrySec)` rises and the elapsed time falls — the condition holds on a **prefix** of
the unmarked positions, which is exactly what the cursor requires.

Invariants verified on all 29,990 trips of the smoke day: no mark at or before its trip's fill, `60s`
never before `30s`, no mark firing sooner than its own window, px/sec null-agreement, no
non-positive prices. ⚠ An unfired mark writes **NULL for the second too**, not a `-1` sentinel that
would silently average into any query that forgot to exclude it.

---

# S18 — EWMA FORMS OF eff, VR AND AUTOCORRELATION (user, 2026-08-24)

## ⚠ First, a correction: `eff_20m` was never an EWMA

```fsharp
let ew40 = EmaHlMa 40.0          // volat_20m   ← THE EWMA
let slotLag40 = LagMa<float> 40  // eff_20m numerator
let slotAbs40 = SumMa 40         // eff_20m denominator   ← a HARD window
```

`volat_20m` is the EWMA; `eff_20m` is a hard 40-slot window with an explicit
`Count = WindowSize` guard. That is precisely why one is live from slot 1 and the other is still
**89% null 29 minutes into the session** — slots count PRESENT bars and a typical name yields ~1
slot per *minute*, not 2, so a "20m" window needs ~40 wall-clock minutes.

**Half-life:** `EmaHlMa 40.0` = **40 slots** ≈ 1,200 present bars ≈ 20 min. So `volat_20m`'s
half-life really is 20m and `volat_10m`'s is 10m — the naming is half-life-based and consistent.
⚠ Mean lag is 40/ln2 ≈ **57.7 slots**, so its memory is *longer* than a 40-slot window's.

**Initialization:** ⭐ NOT an SMA-style fill. `EmaHlMa` carries a decayed numerator/denominator pair
and reads `num/den`, the correctly normalised weighted mean over whatever history exists — after one
push `State = x₁` exactly. (A plain `EmaMa` seeded on the first value would still be 59% seed 30
pushes in at hl = 40.)

⚠ But *correct* ≠ *precise*: at 5 slots it is an honest mean of 5 noisy values and **not the same
statistic** it becomes at 100. ⭐ That finally explains S3a's unexplained
`ρ(volat_20m, volat_10m, volat_open) = 0.992-0.998` — early in the session the three are
arithmetically nearly the same number, and the entry window is concentrated at 09:45-10:00, so they
never get the chance to diverge.

## ⭐⭐ S18a — The telescoping insight (user)

The windowed eff numerator `ln(V_t / V_{t−40})` is **exactly** `Σ r_k` over the same span, because
log returns telescope. Written as a sum it becomes EWMA-able; written as a two-endpoint difference
it cannot be. Same statistic, one representation admits exponential weighting and the other does
not. Verified to 2e-15.

⭐ **And the bias correction cancels.** `EwmaEffMa` reads `EWMA(r)/EWMA(|r|)`, and both halves carry
the *same* `den` — so the ratio is `num_r / num_abs` and is **exactly unbiased from the first push**,
with no warm-up term at all. The denominator is not even accumulated. This is the one construction
where EmaHlMa's correction costs nothing and buys everything.

## S18b — Three new classes

`EwmaEffMa(hl)`, `EwmaVarRatioMa(hl, q)`, `EwmaAutoCorrMa(hl, maxLag)` — all on the same signed
slot-return stream as their windowed twins, so a pair differs **only in its weighting**, which is
what makes it a control rather than two unrelated numbers.

⚠ Unlike eff, the bias correction does **not** cancel for VR or autocorrelation — a variance is
`E[x²] − E[x]²` and the normaliser enters the two terms differently — so those moments go through
full bias-corrected `EmaHlMa`s. Two honest caveats: no Bessel correction (biases largely cancel in
the VR *ratio* at a shared half-life), and the q-sum / cross-moment streams start a few pushes after
the level stream. Band on them; do not quote them as significance tests.

**Validation** (`EwmaTrend_Test.fsx`), four ways:

| check | result |
|---|---|
| `EwmaEffMa(hl=1e7)` vs unweighted `Σr/Σ\|r\|` | rel err 2.5e-07 |
| telescoping: `Σr` vs `ln(V_n/V_0)` | rel err 2.0e-15 |
| planted AR(1) φ=0.35 → recovered ρ₁ | +0.3470 |
| `VR(2)` vs the identity `1 + ρ₁` | rel err 5.4e-05 |
| `VR(4)` vs `1 + 2(0.75ρ₁+0.50ρ₂+0.25ρ₃)` | rel err 7.1e-05 |
| **i.i.d. control** | ρ₁ −0.0044, VR 0.9953, eff +0.0050 |

⭐ The i.i.d. control is the one that matters — without it, a bug that says "trending" about
everything passes every other check.

## S18c — Measured effect on warm-up (2026-08-20, 25,963 trips)

| feature | % null |
|---|---|
| **`eff_ewma_20m`** | **0.00** |
| `eff_20m` (windowed) | **54.21** |
| `vr4_ewma` / `vr4_roll` | 0.62 / 0.62 |
| `ac1_ewma` / `ac1_roll` | 0.62 / 0.00 |

Bounds hold (`|eff| ≤ 1`, `|ρ| ≤ 1`, `VR > 0`, zero violations). Correlation with the windowed
originals: eff **0.786**, vr4 0.856, vr2 0.871, ac1 0.838 — related but not redundant, which is what
a different weighting of the same stream should look like.

## S18d — Base pass v4

`data/longhiker_trips_v4/` carries, in one run: the EWMA twins, the five counterfactual exit marks,
the extremes-only signal gate, and entries from **09:45** (the candidate table's knowability floor —
⭐ it is the *universe*, not feature warmth, that sets the start; `volat_20m` is live from slot 1 and
`vr4_roll` is 91% available at 09:45).


---

# S19 — μ² WAS AN ASSUMPTION, AND IT LEAKED DRIFT (user, 2026-08-24)

The user asked what `μ` is in the EWMA autocorrelation. It is `EWMA(r)`, the exponentially-weighted
mean return — and answering it exposed that using **μ² for both halves of the numerator** is not a
definition but an **assumption**.

```
Cov(r_t, r_{t−k}) = E[r_t·r_{t−k}] − E[r_t]·E[r_{t−k}]
```

Collapsing `E[r_t]·E[r_{t−k}]` to `μ²` presumes the mean is the same at `t` and at `t−k` — true
under stationarity, **false for a stock whose drift is changing**, which is the only kind this
system trades. The windowed `AutoCorrMa` never took that shortcut (it tracks the paired sums `A_k`
and `B_k` explicitly); the first EWMA version did.

**Measured at hl = 40**, shipped vs a paired-mean reference:

| stream | μ² shortcut | paired means | diff |
|---|---|---|---|
| AR(1) φ=.35, constant drift | +0.41199 | +0.41134 | 6.5e-04 |
| AR(1) φ=.35, no drift | +0.41155 | +0.41134 | 2.1e-04 |
| drift flips +2% → −2% | +0.04304 | +0.08331 | −4.0e-02 |
| **drift ramps 0 → 5%** | **+0.18016** | **+0.09772** | **+8.2e-02** |

⚠⚠ Under a stationary mean the shortcut is free. **Under a ramping drift it inflates ρ₁ by ~2×** —
the same drift-leak that mean-centering exists to prevent, reintroduced through the back door. A
trending stock's drift is precisely non-stationary, so this would have made `ac1_ewma` look like a
trend feature for the worst possible reason.

⭐ **Fixed** by tracking `E_A[r_t]` and `E_B[r_{t−k}]` as their own EWMAs, pushed at the same
instant as the product so all three share one support exactly. The denominator keeps the
full-stream variance, matching `AutoCorrMa`'s convention. After the fix the class matches the paired
reference to **0.00e+00** on all four streams, and the whole validation suite still passes (AR(1)
recovery ρ₁ 0.3470; VR identities to 2.5e-05 / 1.4e-04; i.i.d. control ρ₁ −0.0044, VR 0.9953,
eff +0.0050).

⚠ `EwmaEffMa` and `EwmaVarRatioMa` are **unaffected** — neither has a cross-moment, so neither has
two means to reconcile. Only the three `ac*_ewma` columns changed.

💀 The base pass was killed 10 minutes in and restarted rather than ship a feature with a known
drift-leak. Cheap next to discovering it in a breakdown three sections later — which, on the record
of S3b / S5 / S7c / S15, is exactly where it would have surfaced.

---

# S20 — DO THE EWMA INDICATORS DO ANYTHING? (user, 2026-08-25)

Base pass **v4**: 37,466,769 trips / 15 GB (10.4× smaller than v2 — the extremes-only gate),
entries from 09:45, five counterfactual exit marks, EWMA twins. Standing filter + the S11 rung
(`secs_since_hi_1200 = 0`) leaves **378,887 trips over 19,693 ticker-days**.

## S20a — Coverage: the warm-up problem is solved

| feature | % null |
|---|---|
| **`eff_ewma_20m`** | **0.00** |
| `eff_20m` (windowed) | 49.99 |
| `vr4_ewma` / `vr4_roll` | 0.37 / 0.37 |

⚠ The engine gate is still `eff_open >= 0.30`, so every table below is measured on a population
already pre-filtered by the *old* feature. `eff_ewma_20m` nonetheless spans p01 0.145 → p99 0.871
inside that set, so there is real range to band on.

## S20b — Both new features are monotone, trip-pooled, and 7/7 in the top band

`eff_ewma_20m` — median `r30s` / win% ex-ties:

| band | n | med | win% | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|---|
| < 0.35 | 55,709 | 1.71 | 51.48 | 8.73 | 0.62 | 2.25 | −1.70 | 1.23 | 0.01 | 2.35 |
| 0.35-0.45 | 133,099 | 3.61 | 52.74 | 8.09 | 0.60 | 3.28 | 0.23 | 1.03 | 6.61 | 3.50 |
| 0.45-0.55 | 90,356 | 3.98 | 52.92 | 9.15 | 2.27 | 1.24 | 1.75 | 3.54 | 7.78 | 1.42 |
| 0.55-0.70 | 66,857 | 3.95 | 53.04 | 5.57 | 4.26 | 3.98 | 0.39 | 3.46 | 7.63 | −0.28 |
| **0.70+** | 32,866 | **5.51** | **54.21** | 6.11 | 5.66 | 4.94 | 3.77 | 9.68 | 5.52 | **2.01** |

`vr4_ewma`:

| band | n | med | win% | 2026 |
|---|---|---|---|---|
| < 0.7 | 71,993 | 1.89 | 51.79 | 1.39 |
| 0.9-1.1 | 74,081 | 2.91 | 52.42 | 2.47 |
| 1.1-1.4 | 96,080 | 4.17 | 52.91 | 2.98 |
| **1.4+** | 74,871 | **6.91** | **54.60** | **2.56** |

⭐ Both top bands are **positive in all seven years including 2026** — which S14's `vr4_roll` was
not (it inverted in 2026). ⚠ Note `vr4_ewma` is monotone-rising, **not** the inverted U `vr4_roll`
showed; the exponential weighting has changed the shape, not just the noise.

## ⭐⭐ S20c — Head-to-head at matched selectivity

| selection | trips | ticker-days | trip mean | trip med | win% | **eqw day** | **% days up** |
|---|---|---|---|---|---|---|---|
| `eff_open >= 0.62` | 59,319 | 4,740 | 5.88 | 4.94 | 53.84 | −15.79 | 38.61 |
| **`eff_ewma_20m >= 0.70`** | 32,866 | 2,951 | **7.04** | **5.51** | **54.21** | −15.61 | 39.65 |
| `vr4_roll >= 1.62` | 39,210 | 2,784 | 10.56 | 7.11 | 54.60 | −16.86 | 39.69 |
| `vr4_ewma >= 1.53` | 47,715 | 3,097 | 9.74 | 7.06 | 54.52 | −16.41 | 39.49 |
| *baseline (whole rung)* | 378,887 | 19,693 | 4.99 | 3.75 | 52.78 | −14.48 | 38.81 |

⭐ **`eff_ewma_20m` beats `eff_open` on every trip metric while being MORE selective** (32.9k vs
59.3k trips) — better and cheaper. ⚠ `vr4_ewma` and `vr4_roll` are a tie; the EWMA version's win is
availability and shape stability, not information.

**Substitution tests** (T37/T38): ρ(eff_ewma, eff_open) = 0.930 and ρ(vr4_ewma, vr4_roll) = 0.880,
and the joint tables are diagonal-dominant — the off-diagonal cells that would prove independent
information carry n in the tens. `vr4_ewma` retains a gradient inside `vr4_roll >= 1.4`
(0.86 → 5.28 → 7.25 on 1.6k/13.3k/63.5k), the reverse direction is weaker. **Neither pair is two
features; each is one feature measured two ways, and the EWMA way is the better-behaved one.**

## 🛑 S20d — But none of them touches the ticker-day problem

Equal-weight by ticker-day, `eff_ewma_20m` bands: **−5.01 / −7.86 / −9.80 / −11.29 / −8.96**, with
`pct_days_up` **45.10 → 41.70 → 39.84 → 37.46 → 39.65**. `vr4_ewma` bands: **−8.72 / −8.78 / −9.58 /
−9.29 / −9.58**, `pct_days_up` flat at ~40%.

⭐⭐ **The two weightings point in OPPOSITE directions.** Higher `eff_ewma` is monotonically better
per *trip* and monotonically worse per *day*. `vr4_ewma`'s day-level gradient is flat — meaning its
entire trip-pooled gradient is a trip-count effect.

And selecting harder makes the day picture *worse*, not better: baseline −14.48 → −15.6 to −16.9 for
every top-band selection, while `pct_days_up` barely moves (38.8% → 39.5%). `corr(k, day return) =
0.29` on the rung.

⚠ **A refinement of S15's framing, which was too harsh.** The trip-weighted number is not an
artifact — it is the realised P&L per unit of exposure, and it is *causally* earned: more signals
fire because the stock keeps printing new 20m highs, which is knowable at the time. What the system
does is **scale exposure into persistence**, taking few trades on days that fail and many on days
that work. That is a legitimate design. But it must be risked as what it is:

- **~19 trips per ticker-day**, so the independent draw is the day, not the trip
- **the typical day loses**; ~39% of days are up
- the aggregate is carried by a minority of high-signal days

## S20e — Verdict on the user's question

| | verdict |
|---|---|
| `eff_ewma_20m` | ⭐ **keep, replaces `eff_open`** — strictly better at matched selectivity, 0% null vs 50%, 7/7 years |
| `vr4_ewma` | ⭐ **keep, replaces `vr4_roll`** — ties on returns, but monotone instead of inverted-U and positive in 2026 where `vr4_roll` inverted |
| both | ⚠ **neither improves the day-level picture at all** |

⭐ Next: the exit rules. S12 and S16b both concluded the exit is the system, and the day-level
problem is an *exit* problem — a fixed 30-bar timestop cannot cut a bad day short. The five
counterfactual marks in v4 are the test.

---

# S21 — STACKING eff_ewma × vr4_ewma (user, 2026-08-25)

## 💀 S21a — The proposed stack fails the iso-trip control

`eff_ewma >= 0.62 AND vr4_ewma >= 1.53` → 6,755 trips (1.8% of the rung). Against controls
tightened to the **same trip count** (`feedback_iso_trip_control_for_stacked_features`):

| selection | trips | tkd | trip mean | trip med | win% | eqw day | % days up |
|---|---|---|---|---|---|---|---|
| **STACK** 0.62 × 1.53 | 6,755 | 587 | 5.93 | 5.45 | 53.43 | −21.27 | 38.50 |
| iso: `eff_ewma >= 0.880` | 6,689 | 726 | **10.63** | **7.61** | **56.46** | −12.08 | 41.60 |
| iso: `vr4_ewma >= 1.976` | 6,798 | 642 | 8.09 | 5.33 | 53.15 | −20.03 | 38.47 |
| iso: RANDOM subsample | 6,746 | 4,692 | 4.35 | 2.11 | 51.76 | +1.04 | 50.51 |

**Either feature alone at the same selectivity beats the conjunction.** It only clears the random
floor.

⭐ **And NOT because they are redundant** — `ρ(eff_ewma, vr4_ewma) = −0.14`, near-orthogonal. The
cause is that `eff_ewma`'s payoff is **concentrated in its extreme tail**, so a 0.62 floor forfeits
exactly the part that pays:

| `eff_ewma >=` | trips | trip mean | win% | eqw day | % days up |
|---|---|---|---|---|---|
| 0.55 | 99,723 | 5.64 | 53.43 | −17.87 | 37.70 |
| 0.70 | 32,866 | 7.04 | 54.21 | −15.61 | 39.65 |
| **0.88** | 6,676 | **10.59** | **56.48** | **−12.14** | **41.60** |
| 1.00 | 1,003 | 4.14 | 55.71 | −12.80 | **44.78** |

⭐⭐ This is also **the first selection in the whole study whose equal-weight-day metric IMPROVES
with selectivity** — −14.48 baseline → −12.14, `% days up` 38.8% → 41.6% → 44.8%. Every earlier cut
made it worse.

## ⭐⭐ S21b — Stack on the STRONG base and it wins decisively

| selection | trips | tkd | trip mean | trip med | win% | eqw day | % days up |
|---|---|---|---|---|---|---|---|
| base `ee>=0.88` | 6,676 | 726 | 10.59 | 7.67 | 56.48 | −12.14 | 41.60 |
| **`+ vr4_ewma >= 1.0`** | 2,090 | 201 | **18.45** | **11.18** | **58.59** | −8.45 | 46.27 |
| ⤷ iso `ee>=0.961` alone | 2,122 | 263 | 7.70 | 7.79 | 56.64 | −9.40 | 43.35 |
| ⤷ iso random | 2,074 | 533 | 8.09 | 5.26 | 54.60 | −9.24 | 44.84 |
| **`+ vr4_ewma >= 1.2`** | 1,267 | 126 | 17.29 | **11.32** | 57.78 | **−6.79** | **51.59** |
| ⤷ iso `ee>=0.984` alone | 1,266 | 175 | 3.70 | 5.98 | 55.46 | −13.61 | 41.71 |
| ⤷ iso random | 1,244 | 448 | 7.53 | 3.91 | 53.79 | −5.21 | 46.88 |

It beats **both** iso controls on every trip metric, and `+ vr >= 1.2` gives **51.59% of ticker-days
profitable — the first cell in this study to clear 50%.**

⭐ **The lesson generalises: a conjunction must be built on the part of each feature that actually
carries signal.** Two moderate conditions on orthogonal axes lost; the same two axes at
0.88 × 1.2 won. "Orthogonal features stack" is only true where each is in its paying region.

## ⚠⚠ S21c — But the sample is too thin to call it anything

Year control, `ee>=0.88 & vr>=1.2`:

| yr | trips | **tkd** | med | mean | win% | eqw day | % days up |
|---|---|---|---|---|---|---|---|
| 2020 | 199 | **18** | 4.84 | 21.29 | 54.04 | −11.65 | 38.89 |
| 2021 | 297 | **22** | 20.78 | 44.70 | 60.61 | +0.46 | 54.55 |
| 2022 | 175 | **19** | 7.69 | −0.97 | 55.75 | −12.94 | 42.11 |
| 2023 | 88 | **10** | 2.61 | −20.72 | 50.00 | −48.87 | 50.00 |
| 2024 | 133 | **15** | 29.09 | 9.22 | 67.69 | +5.21 | 66.67 |
| 2025 | 200 | **19** | 17.68 | 20.58 | 60.80 | +8.05 | 57.89 |
| 2026 | 175 | **23** | 1.80 | 5.97 | 52.30 | −6.65 | 52.17 |

Medians are **7/7 positive** in both stacks, which is not nothing. But **10-23 independent
ticker-days per year** is not a year audit, it is seven small samples — the mean is negative in 2 of
7, 2023 rests on 10 ticker-days, and 2026 (the year we would trade) is the weakest at +1.80 median
/ 52.30% win.

⭐ The fix is sample, and it is available: this cell lives inside the **`volat_20m ∈ [40,80)bp` focus
band only**. Volatility was always meant to be the varied axis — widening it is the obvious next
step, and the first real test of whether 0.88 × 1.2 is a rule or a small-sample shape.

---

# S22 — EXITS, mc=1, AND WHERE THE TRADES SIT IN THE SESSION (user, 2026-08-25)

## 💀 S22a — The exit rules are refuted, and the diagnostic says why

Composite policies (whichever named condition fires first, 30-bar timestop as backstop), whole rung:

| policy | mean | med | win% |
|---|---|---|---|
| **timestop 30 bars (current)** | 4.99 | **3.75** | **52.78** |
| 1m low | 5.03 | 3.40 | 52.61 |
| no-new-1m-high 30s | 5.03 | 1.35 | 51.27 |
| 1m low OR nohi 30s | 5.04 | 1.30 | 51.18 |
| 1m low OR nohi 60s | 5.05 | 3.27 | 52.51 |

Means move by ≤ 0.06 bp; medians and win rates get **worse**. The diagnostic explains it:

| | |
|---|---|
| timestop fires at | median **36 s** |
| `nohi 30s` mark fires at | median 48 s |
| `1m low` mark fires at | median 102 s |
| `nohi 60s` binds before the timestop | **2.74%** of trips |

⭐ **The trailing rules almost never get to act** — the timestop wins the race. So the composite is
the timestop wearing a different name.

Take the timestop out and let the trail run (10m cap):

| rule | mean | med | win% |
|---|---|---|---|
| 30-bar timestop | 4.99 | **+3.65** | **52.78** |
| 5m fwd mark | −3.95 | −10.76 | 47.14 |
| 10m fwd mark | −18.93 | −24.82 | 45.17 |
| trail: 1m low | 5.48 | −24.54 | 39.01 |
| trail: 1m low OR nohi 30s | 3.60 | −8.46 | 43.69 |

⭐⭐ **Every longer hold is worse.** This is S6a/S12 restated from the exit side: the edge lives for
~30 seconds and has reversed by 5 minutes, and *any* trailing rule must wait longer than a 30-second
timestop before it can trigger. There is no trailing exit to find here — the horizon is the
constraint, not the rule.

⚠ `no-new-1m-high` and `no-new-20m-high` give near-identical results, because on this rung every
entry IS a new 20m high (hence also a new 1m high) and the hold is too short for the two clocks to
diverge.

## ⭐⭐ S22b — mc=1 is the real answer to the trip-weighting problem

Greedy mc=1 replay (the FlushFader S38 method — walk the day's signals in time order, take one only
if the previous position has closed):

**The S21 stack (`ee>=0.88 & vr>=1.2`):**

| book | trips | tkd | mean | med | win% | **eqw day** | **% days up** |
|---|---|---|---|---|---|---|---|
| mc=0 (sampler) | 1,267 | 126 | 17.29 | 11.19 | 57.78 | −6.79 | 51.59 |
| **⭐ mc=1** | **230** | 126 | **19.34** | **16.24** | **60.53** | **+8.65** | **53.17** |

⭐⭐ **`eqw_day` turns POSITIVE (+8.65) — the first positive day-level number anywhere in this
study**, alongside a higher median (16.24) and win rate (60.53%).

⭐ The mechanism is exactly the S15 problem dissolving: mc=0 lets one move contribute ~10
overlapping trips, so trips are not independent and the pooled statistics get weighted by how the
day went. mc=1 takes ~1.8 trips per ticker-day, so **a trip is approximately an opportunity again**
and the trip-level and day-level views converge instead of contradicting each other.

`eff_ewma >= 0.88` alone, mc=1: 1,384 trips / 726 tkd, mean 8.52, med 5.93, win 55.27%,
eqw_day −2.77, **50.83% of days up**.

mc=1 stack by year — median **7.04 / 26.71 / 14.37 / 26.08 / 8.10 / 29.68 / 1.29** (7/7 positive),
mean 6/7 (2024 −1.98). ⚠ **20-43 trips and 10-23 ticker-days per year.** Still far too thin.

## ⭐ S22c — 92% of the selected trades are in the first fifteen minutes

| | 09:45-10:00 | 10:00-10:30 | later |
|---|---|---|---|
| whole rung | 56.5% of trips (med 3.55) | 31.8% (med **4.64**) | 11.7% |
| `ee>=0.88` | **92.0%** (med 6.75) | 8.0% (med **22.40**) | — |
| the stack | **82.6%** (med 10.98) | 17.4% (med 18.45) | — |

⭐ **Yes — the EWMA features do let us trade early, and that is where nearly all of the selection
lands.** With no 1,200-bar warm-up there is nothing left blocking the 09:45-10:00 window, and
`eff_ewma >= 0.88` puts 92% of its trips there.

⚠⚠ **But that is also a confound to watch.** At 09:45 the EWMA has absorbed only ~16 slots, so a
high reading means "the whole session so far has been one clean move" — structurally easier early
than at 11:00, when the same threshold demands recent cleanliness against a long memory. `eff_ewma
>= 0.88` is therefore *partly* a statement that it is 09:45-10:00, the same defect class as `ddz`
(S10b). ⭐ The tell that it is not ONLY that: the 10:00-10:30 slice reads **better** than the early
one (med 22.40 vs 6.75), so early is where the population lives, not where the edge is.

## S22d — Standing

| question | answer |
|---|---|
| do the trailing exits help? | ❌ **no** — they bind 2.7% of the time and every longer hold is worse |
| does mc=1 help? | ⭐⭐ **decisively** — first positive `eqw_day` in the study (+8.65), win 60.53% |
| more early breakouts now? | ⭐ **yes, 92%** of selected trips are 09:45-10:00 — ⚠ and partly by construction |

⚠ The blocker is unchanged and now sharper: **10-23 ticker-days per year**. The next move is to
widen the `volat` band, which was always meant to be the varied axis, and re-run this whole block
with mc=1 as the default book.

---

# S23 — THE FIRST CELL THAT PASSES EVERYTHING (user, 2026-08-25)

## ⚠ S23a — The `nohi20m 60s` exit is worse than the timestop

Compared on an **identical trip set** (the book is sized on the trail exit, so this is exit-rule-only):

| exit | mean | med | win% | eqw day | % days up |
|---|---|---|---|---|---|
| nohi20m 60s | **8.13** | −6.14 | 46.36 | 2.44 | 44.12 |
| **30-bar timestop** | 7.04 | **+5.25** | **54.47** | **4.84** | **53.03** |

And on the `k20 21-40` cell, year by year — the trail's higher pooled mean (18.53) comes almost
entirely from **2020 alone (64.60)** plus 2025 (31.53):

| metric | TIMESTOP | trail |
|---|---|---|
| years median > 0 | **7/7** | 3/7 |
| years `% days up` > 50 | **7/7** | 3/7 |

⭐ The trail buys mean and sells median, win rate, day return and days-up — in six of seven years.
**Recommendation: keep the 30-bar timestop.** (S22a already explained why no trailing rule can win
here: the edge horizon is ~30 s, and a trail must wait longer than that before it can fire.)

⭐ Why the "30s" trail does not match the "30-bar" timestop: they are different clocks. The timestop
is 30 **present bars** after the fill (median 36 s — present bars ≠ seconds on gappy tape); the
`nohi 30s` mark is 30 **wall-clock** seconds since the last new high, and **that clock RESETS on
every new high**, so it is a genuine trail firing at median 48 s.

## ⭐⭐⭐ S23b — `eff_ewma >= 0.70` × `highs_20m_since_lo_1200 ∈ [21,40]`, mc=1, timestop

| selection | trips | **tkd/yr** | mean | med | win% | **eqw day** | **% days up** | yrs med>0 | yrs eqw>0 |
|---|---|---|---|---|---|---|---|---|---|
| `ee>=0.70` (all k) | 6,470 | 421.6 | 6.75 | 5.61 | 54.65 | **−4.66** | 49.31 | 7/7 | **1/7** |
| **⭐ `+ k20 21-40`** | 907 | **102.0** | **9.60** | **6.02** | **55.69** | **+7.69** | **54.06** | **7/7** | **7/7** |
| `+ vr4_ewma>=1.0` | 215 | 24.0 | 10.81 | 5.28 | 56.74 | 8.95 | 56.55 | 7/7 | 6/7 |
| `+ vr4_ewma>=1.2` | 136 | 15.4 | 13.93 | 5.77 | 57.35 | 14.40 | 57.41 | 6/7 | 6/7 |
| `+ ee>=0.88` instead | 212 | 23.7 | 6.35 | 2.63 | 53.08 | 4.70 | 53.61 | **4/7** | **3/7** |

⭐⭐⭐ **The first cell in this study to be 7/7 positive on median AND 7/7 on equal-weight-day, with a
usable sample (102 ticker-days/year).** By year: eqw **10.74 / 9.74 / 3.93 / 10.26 / 9.69 / 8.40 /
5.59**, `% days up` **55.6 / 52.0 / 52.0 / 59.6 / 51.3 / 58.2 / 52.1** — every year above 50%.

⭐ **The `k20 21-40` filter is doing all the work.** Without it the same `ee>=0.70` book has
`eqw_day = −4.66` and is positive in only **1 of 7** years; with it, +7.69 and 7/7.

## ⚠ S23c — Two things that do NOT earn their place

**`vr4_ewma` on top.** It raises every pooled number, but cuts the sample **4-13×** (102 → 24 → 15 →
8 tkd/yr) and *loses* year-consistency (7/7 → 6/7 → 5/7 on median). At this sample it is buying
statistics, not edge — the iso-trip lesson applied to a cell that is already thin.

**⭐⭐ `ee >= 0.88` is WORSE than `ee >= 0.70` here** — 4/7 and 3/7 against 7/7 and 7/7. S21 found
0.88 to be the peak, but that was measured **at mc=0 and without the k20 filter**. Conditioned on
`k20 21-40` the two are partial substitutes: both select "a clean run that has not gone on too
long", so stacking them just shrinks the sample. ⚠ A reminder that a threshold tuned in isolation
does not survive being conditioned on.

## S23d — The spec as it now stands

```
universe   mr_candidate_1s_v2, entry_px > $1, barnum >= 22
window     09:45 - 15:50 ET          (09:45 = the candidate table's knowability floor)
tape       gap_60 < 30               (dense)
regime     volat_20m ∈ [40, 80) bp   ⚠ the focus band — NOT yet varied
signal     a new 20m high  AND  eff_ewma_20m >= 0.70  AND  highs_20m_since_lo_1200 ∈ [21, 40]
book       mc = 1
exit       30 present bars after the fill, at that bar's vwap
```

**102 ticker-days/year · mean +9.60 bp · median +6.02 · win 55.69% · day return +7.69 bp · 54.06% of
days up · 7/7 years on both median and day return.**

⚠⚠ **Costs are still not modelled**, and ~9.6 bp is not a large cushion. ⚠ The `volat` band is still
the un-varied axis — widening it is the next step and the obvious source of more sample.
