# MaxFader — MaxFlyerV2 on the 1s tape (the hold-to-close SHORT pop-fade)

**`TradingEdge.MaxFader`** (2026-09-03). The second of the two 1m→1s conversions
(user: "the hard one first"; LowFlyer follows). Fade the **new SESSION HIGH**, short,
**hold to the close**. Sampler mc=0, record-first, every lever a recorded column.

## What is being ported, and why none of its numbers are a baseline

`TradingEdge.MaxFlyerV2` (1m): a bar CLOSES above the prior session high (close-based
ref) on a bar whose volume EXCEEDS the running session 1m-volume max; short; MOC. Its
master gate was **`brv20d ≥ 100` = 2,760 trips / PF 6.65 / 88.7% win** — and it is
**contaminated**: `brv20d = bar_vol / (avgvol20 × adj_ratio / 390)` uses `avgvol20`
(INCLUDES day D) and `adj_ratio` (folds in FUTURE splits). Both are the lookahead classes
of `docs/lookahead_protocol.md`; the doc's own tail says so. The secondary lever
`bar_rvol_15m` (vs the opening-15m mean) is causal.

**The design problem the user named:** it HOLDS TO CLOSE, so every large up day hits
it — worst trip −839%, 3% of trips below −20%. Per the SpikeFader lessons
(`project_next_session_2026-09-01`): time stops and loss stops LOSE on a short MR book;
look at **entry selection and sizing**, not exits. Every table in this doc carries the
tail block (worst / p1 / p5 / %<−20%) for that reason.

## The engine: a fork of SpikeFader, three edits

SpikeFader already carried every primitive — and said so in its comments:
`sessHigh`/`sSessHigh`/`brSess` (the session-high event); `tVol60PriorMax =
TimeLagMaxMa 60` over `tVolSum60` (*"the MaxRider F10/F12 volHIGH mirror … LowFlyer
uses the session 1m max — baked in here for the eventual port"*); `vol0945Tape`; the
halt detector; time-clock volume; calendar-aware MOC; the mc=0 sampler; the day-worker
pipeline; forward/aux marks at every minute. So:

| step | commit | what | proof |
|---|---|---|---|
| 1 | `eafeab1` | verbatim fork | zero-diff: 5,902 = 5,902 trips, 461 cols, symmetric difference 0 |
| 2 | `acfc9d5` | `EntryChannelBars = 0` → SESSION channel (`priorEntryMax` reads `sSessHigh`); `ExitChannelBars = 0` → no target, MOC only (`sExitMin` forced ValueNone); `avgvol20_prior` on the row | signal > prior session high on 2,973/2,973; exit = `moc` on all |
| 3 | `cd1220e` | prepass + harness | one-day superset check 44/44 |

The causal levers, all derivable from recorded columns:

| lever | formula | note |
|---|---|---|
| `brv15_tape` | `vol_60 / (vol_0945_tape / 15)` | MaxFlyerV2's `bar_rvol_15m`; same-day, **split-immune — PRIMARY** |
| `brv20d_prior` | `vol_60 / (avgvol20_prior / 390)` | the causal `brv20d`; `20 PRECEDING AND 1 PRECEDING`, day-D raw shares. ⚠ straddling a reverse split → near-zero denominator (S39d) — **SECONDARY, split-guarded** (`n == 1`) |
| `volhigh60` | `vol_60 > vol_60_prior_max` | MaxFlyerV2's STRICT "new session volume high" (F10/F12) |
| `chg_1d` | `entry_px / close_m1 − 1` | extension into the fade, both day-D raw |
| `ret_540` | `−(aux_lo_540_px / entry_px − 1)` | SpikeFader's 9m cover on MaxFader's entry — an exit comparison without a rerun |

`vol_60` is the 60-**tradeable-second** sum (halt-adjusted clock, §S7); the 1m system's
"bar volume" has no cleaner analog.

## ⭐⭐ The whitelist-warmth lesson (the step-1 argument was wrong, measurably)

Step 1 claimed: *every session high is a 20m high, so base_v4's trip ticker-days
(`spikefader_s44_whitelist`, 208,708) are a complete whitelist.* The price inequality is
true; the claim is false, because a channel signal also needs the channel **WARM**. On
the 5-day smoke, 650 of 2,973 MaxFader signals fired with `bars_present` 632–1,197 —
the first ~10 minutes after 09:45, where the 1200-bar channel is cold and SpikeFader
structurally cannot fire. At ticker-day level **34 of 137 (24.8%, carrying 11.0% of
trips) had NO SpikeFader trip** and would have been silently dropped: the early spike
that never re-takes its high — a real, distinct population.

*General form: a subset argument between two channel signals must account for warmth,
not just the price inequality.*

Replacement — `scripts/equity/maxfader_prepass.py`, a pure-SQL **superset of the
`--base-run` signal by construction**: `barnum ≥ 22` ∧ a bar in [09:45, 15:00] strictly
above the running prior max since 09:30 ∧ `max |30-bar-slot log return| ≥ 40bp − 1e-6`
(the S39j convexity argument rebuilt — `volat_20m` is a convex combination of that |r|
stream, so a day whose max is under the floor can never open the gate; the v2 table has
no `max_slot_absr_bp`). **No dv/tc floors**: the engine's 60-second window is
halt-adjusted tradeable time and can exceed 60 wall-clock seconds, so a wall-clock SQL
floor could reject a day the engine accepts. 0.9 s/day; ~59% of candidates flagged →
~3.3 h engine vs ~5.5 h base. Table `maxfader_whitelist` via `FF_CANDIDATE_TABLE`.

## Method

* **Record-first, `--base-run`.** SpikeFader's speed/eff stack is OFF: it was derived
  for a 20m-high / 9m-cover trade; this is a session-high / MOC trade. Ports are
  hypotheses (`feedback_side_flip_inverts_rulings`).
* **Universe gates kept identical to SpikeFader's base**: volat_20m ≥ 40bp, dv60 ≥ $100k,
  tc60 ≥ 60, barnum ≥ 22, entries 09:45–15:00 (12:00 on early closes), MOC 16:00 (13:00).
* Metric = PF−1 (`feedback: measure edge as PF−1`), raw (a short's return is +100%-bounded;
  MaxFlyerV2 doc), with the tail block on every table. Three controls before any gate is
  called a feature: year table (2026 first), same-n tightened incumbent + random floor,
  and mc=1 replay.
* ⚠ Stale defaults: pass `--db-path` and `--sec-dir` explicitly (Program.fs points at
  the pre-split path; the stale sec-dir yields "0 had a 1s tape" and 0 trips silently).

## Smoke (2021-03-01..05, 2,973 trips) — an anecdote, recorded before the run

Bare session-high short: PF−1 −0.032, worst −133.8%, 6.1% < −20%. The `brv15_tape`
ladder is monotone and **the tail vanishes up it**: ≥4 → PF−1 1.69 (n 708, worst −78%);
≥8 → **PF−1 24.6, win 86%, worst −5.6%, 0% < −20%** (n 219); ≥12 → win 94% (n 87).
`volhigh60` fires on 12.5% of session-high signals. Five days; the corpus decides.

---

## ⏭ The risk rule (user, 2026-09-03 evening) — to build after the base-run tables

MaxFlyerV2's stop-out / re-entry machinery is rejected ("ridiculous, doesn't fit my
style"). Instead: an **anchored VWAP on the entry**, and the trade runs regardless. At
**{1h, 2h, 3h} after entry**, if the AVWAP is **above the entry** (the short is losing on
a volume-weighted basis), the MOC order is **replaced with a 5m-low exit** — and that is
the entire rule. Then find the horizon at which holding to the close stops making sense.

Engine mapping: the AVWAP is the window difference of the running session-VWAP sums
(stamp `cumDv`/`cumVol` at entry; `(cumDv − dv₀)/(cumVol − vol₀)` at any later bar — no
new structure). New record-only marks: `avwap_{1h,2h,3h}` at entry+{3600,7200,10800}s
and, when that AVWAP > entry, the FIRST 300-bar-low breach after the check (px + sec).
The existing per-minute lo marks fire from entry, not from the check time, so this is a
small genuine addition; the horizon study is then post-hoc SQL on one run.

## §S1 — the 2023+ base run: MaxFlyerV2's gates fail causally; the AVWAP rule works (2026-09-03/04)

Corpus `data/maxfader_wl_2023p` (`--base-run`, 2023-01-03..2026-08-21, 257,701 whitelisted
ticker-days, 2.9 h): **953,166 trips / 35,726 tkd**. 2020–2022 runs tomorrow (sibling dir,
same glob). Everything below is mc=0 attribution unless marked mc=1. Tables:
`data/maxfader_study1.log` (`scripts/analysis/maxfader_study1.py`).

### S1a — the bare session-high short is barely a system, and its tail is 4× its net

| view | n | PF−1 | net% | win% | worst% | p1% | p5% | <−20% |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| mc=0 | 953,166 | 0.133 | 537,258 | 55.9 | −887.1 | −56.1 | −18.2 | 4.41 |
| mc=1 | 35,726 | 0.048 | 5,243 | 53.3 | −887.1 | −35.2 | −12.3 | 2.33 |

Trips below −20%: 42,029 (4.41%) netting **−2,073,066%** against a corpus net of +537,258%
— the squeeze tail is four times the book. By year (mc=0 PF−1): 2023 0.365, 2024 0.058,
2025 0.154, 2026 0.050. **28 of the 30 worst trips are ONE ticker-day** (DRUG 2024-10-15,
`halts_today = 1`, brv20d_prior 500–870, entered +56%..+118% on the day; −887% = the
price went 9.9×). The tail is halted, extended, high-volume names — exactly the ones
every MaxFlyerV2 lever selects FOR.

### S1b — ⭐⭐ MaxFlyerV2's volume gates do NOT reproduce causally, and they buy mean by buying the tail

**`brv15_tape`** (the causal `bar_rvol_15m`), monotone floors:

| floor | n | PF−1 | win% | worst% | p5% | <−20% |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 953,166 | 0.133 | 55.9 | −887 | −18.2 | 4.41 |
| 4 | 78,189 | 0.258 | 61.7 | −887 | −37.3 | 10.28 |
| 8 | 19,112 | 0.227 | 63.0 | −866 | −45.8 | 12.62 |
| 12 | 7,309 | 0.445 | 67.9 | −266 | −49.4 | 11.53 |
| 20 | 1,960 | 0.397 | 72.0 | −192 | −64.0 | 9.44 |
| 40 | 238 | 13.88 | 86.1 | **−9.7** | −7.5 | **0.00** |

The smoke's "tail vanishes at ≥8" was an anecdote: on the corpus the tail share TRIPLES
from 4.4% to 12.6% up the ladder, and only the ≥40 rung (n = 238) is clean. By year
brv15≥12 is 1.71 / 0.89 / 1.02 / **−0.21** (2026, 18% tail); brv15≥8 is −0.10 in 2024.

**`brv20d_prior`** (the causal `brv20d`): ≥100 gives PF−1 0.325 unguarded, **0.128
split-guarded** (`n == 1`, n = 43,760), tail 15%, 2026 −0.096. **MaxFlyerV2's "S bucket"
(brv20d ≥ 100 = PF 6.65 / 88.7% win) was a lookahead + split-straddle artifact.** 35% of
trips carry `n ≠ 1`; the unguarded ladder is inflated by near-zero denominators.

**`volhigh60`** (the STRICT volume-high gate): 0.279 vs 0.117 without, all four years
positive (0.12–0.52) — but it loses the same-n control decisively (below).

**⭐ SAME-N: every volume gate loses to `k600`, and `k600` is the only incumbent that does
not fatten the tail** (PF−1 / <−20% at the candidate's n):

| candidate | its PF−1 / tail | tighten k600 | tighten dlv | tighten volat_20m | tighten chg_1d | random |
|---|---|---|---|---|---|---|
| brv15 ≥ 8 (19,112) | 0.227 / 12.6% | **1.193 / 4.1%** | 0.902 / 16.3% | 1.009 / 14.0% | 0.805 / 18.4% | 0.131 |
| brv15 ≥ 12 (7,309) | 0.445 / 11.5% | **1.791 / 3.6%** | 1.827 / 13.2% | 1.477 / 14.1% | 2.378 / 12.3% | 0.124 |
| volhigh60 (63,925) | 0.279 / 7.8% | **0.738 / 3.9%** | 0.571 / 15.3% | 0.843 / 12.9% | 0.303 / 18.2% | 0.130 |

`k600` deciles: PF−1 −0.011 → 0.005 → 0.032 → 0.124 → 0.276 → **0.629** with the tail FLAT
(5.4 → 4.0%). Every other lever — volume magnitude, extension (`chg_1d ≥ 1.5`: 0.372 at
19% tail), volatility (top decile 0.599 at 13.7%), distance above the low (0.487 at
14.4%) — raises the mean by raising squeeze exposure. **The breakout count raises the
mean without it.** "Raw highs and lows are formidable" transfers to the hold-to-close
short intact, and MaxFlyerV2's whole volume vocabulary ranks below it.

Entry time INVERTS the 1m finding ("robust all day"): 09:45–10:30 PF−1 0.17–0.19 at a 2.7–3.9%
tail; 10:30–11:00 negative; the tail grows monotonically to 9% by 12:00–13:00.

### S1c — ⭐⭐ THE AVWAP RULE (user's design): 2h is the horizon; the rule is a tail trade

`rule_h`: at entry+h, if the anchored VWAP > entry, exit at the first 5m low after the
check (MOC if none prints; 99.5% print one) — else MOC. `always_h`: switch regardless.

**All trips, mc=0 (n = 953,166):**

| exit | PF−1 | net% | win% | worst% | p1% | p5% | <−20% |
|---|---:|---:|---:|---:|---:|---:|---:|
| MOC (hold to close) | 0.133 | 537,258 | 55.9 | −887 | −56.1 | −18.2 | 4.41 |
| RULE 1h (49% switched) | 0.161 | 513,004 | 50.3 | −448 | −42.2 | −14.5 | 3.22 |
| always 1h | 0.171 | 439,395 | 56.4 | −266 | −37.2 | −12.2 | 2.58 |
| **RULE 2h (47% switched)** | **0.178** | **610,097** | 52.0 | **−456** | −45.6 | −15.5 | **3.58** |
| always 2h | 0.181 | 552,925 | 56.2 | −331 | −42.8 | −14.2 | 3.22 |
| RULE 3h (45% switched) | 0.137 | 509,910 | 53.1 | −558 | −51.3 | −16.5 | 3.91 |
| always 3h | 0.118 | 412,721 | 55.9 | −558 | −50.1 | −15.8 | 3.72 |

**mc=1 (n = 35,726):** MOC 0.048 / net 5,243 / tail 2.33 / worst −887 → **RULE 2h 0.079 /
7,435 / 1.76 / −331** (+65% PF−1, +42% net).

Readings:
1. **2h is the horizon.** It is the only setting that improves PF−1 (+34%), net (+14%),
   tail share (−19%) and worst (−887 → −456) TOGETHER. 1h is too early — the AVWAP has
   not discriminated yet (rule 0.161 vs always 0.171: the condition adds nothing), and
   always-switching at 1h buys the best tail by exiting winners (net −18%). 3h is too late
   — net falls back to MOC — though by then the AVWAP does discriminate (0.137 vs 0.118).
2. **The rule is a tail trade, not a mean trade.** On the switched trips at 1h (470,576),
   MOC gives PF−1 −0.632 / win 36% — the AVWAP correctly flags the losers — and the rule's
   exit on them nets −2,096,887% vs MOC's −2,072,634% (−1.2%) while cutting the worst from
   −887% to −266% and the tail from 7.5% to 5.1%. It pays ~1% of the switched net to cap
   the disaster. That is the whole point.
3. **By year** (mc=0, rule 2h vs MOC): 2023 0.305 vs 0.365 (loses, the best MOC year), 2024
   0.076 vs 0.058, 2025 0.232 vs 0.154, 2026 0.139 vs 0.050. The tail improves every year.
4. **Inside the k600 book (top 2 deciles, n = 241,658):** MOC 0.406 / 394,150 / 4.21% →
   RULE 2h **0.482 / 399,579 / 3.38%** — PF and net both up. brv15≥12: 0.445 → RULE 3h 0.825
   (net 27k → 38k). `chg_1d ≥ 0.5`: the rule barely moves its 14% tail — the
   extension-tail is NOT fixed by an AVWAP check; `volhigh60`: rule ≈ MOC.

### S1d — verdicts and tomorrow

* **MaxFlyerV2's levers: CLOSED as gates.** brv15/brv20d/volhigh60/chg_1d all lose same-n
  to `k600`, all fatten the tail, brv20d≥100 was an artifact. Kept as recorded columns.
* **`k600` (breakout count) = the candidate master voice**, as in SpikeFader. ⏭ needs its
  own year table, mc=1 replay, and the AVWAP rule measured INSIDE a k600-gated mc=1 book.
* **The AVWAP rule at 2h: ADOPTED as the working exit** — it replaces stop-outs and
  re-entries with one check and one channel, and improves PF, net and tail at once.
* ⏭ **Halts:** the 28 worst trips share `halts_today = 1`. A halt gate (or the S40x
  detector's `secs_since_halt`) is the obvious tail lever to test next — with the
  disproportion test, since halted names are a small fraction of the book.
* ⏭ 2020–2022 (`maxfader_run_wl.sh 2020-01-02 2022-12-31 2020_22`), then the merged
  year table with 2026 first.
* Caveats: 2023+ only; mc=0 attribution throughout unless marked; brv15≥40 is n = 238;
  the bare book is weak (mc=1 0.048) so the rule's value must be re-read inside a gated
  book; DRUG 2024-10-15 dominates the worst-30 list.

### S1e — study 2 on 2023+: k600 as the book, the rule inside it, the halt gate closed

(`scripts/analysis/maxfader_study2.py`, `data/maxfader_study2_2023p.log`.)

**k600 ladder, mc=0 — monotone, tail FLAT, worst SHRINKS:**

| floor | n | PF−1 | win% | worst% | p5% | <−20% |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 953,166 | 0.133 | 55.9 | −887 | −18.2 | 4.41 |
| 35 | 487,581 | 0.260 | 56.9 | −887 | −17.5 | 4.19 |
| 63 | 241,658 | 0.406 | 58.0 | −887 | −17.8 | 4.21 |
| 96 | 97,619 | 0.621 | 60.0 | −724 | −17.3 | 4.01 |
| 140 | 27,552 | 1.041 | 63.4 | −153 | −16.4 | 4.03 |
| 200 | 5,193 | 1.830 | 67.8 | −67 | −16.8 | 3.47 |

Every year positive at every floor (mc=0; 2024 the weak year). ⚠ mc=1 is much weaker —
k600≥63: 0.180 (n 8,332), ≥96: 0.330 (n 3,134) — the sampler's averaging-up is doing
real work, as in SpikeFader.

**⭐ The AVWAP rule inside the k600 mc=1 book** (SLICE; REPLAY-INSIDE agrees within 0.01):

| book | MOC PF−1 / net / tail / worst | RULE 2h | always 2h |
|---|---|---|---|
| k600≥35 (18,277) | 0.104 / 6,441 / 2.79% / −887 | **0.145 / 7,622 / 2.13% / −327** | 0.135 / 6,402 / 1.92% |
| k600≥63 (8,332) | 0.180 / 5,720 / 3.64% / −887 | 0.204 / 5,633 / 2.94% / −306 | 0.232 / 5,595 / 2.59% |
| k600≥96 (3,134) | 0.330 / 4,089 / 3.67% / −720 | **0.425 / 4,356 / 2.84% / −167** | 0.442 / 4,027 / 2.55% |

The rule holds inside the gated book: PF−1 +13–40%, tail −20%, worst −887 → −167..−327,
net flat-to-up. By year in k600≥63: rule beats MOC 2024 (0.085→0.145) and 2025
(0.242→0.309), ties 2026, loses 2023 (0.308→0.211). **Inside the k600 book the AVWAP
condition adds little over always-switching at 2h** (0.204 vs 0.232 at ≥63) — the value is
in the *5m-low-after-2h exit itself*; the AVWAP check earns its keep on the bare book, not
the gated one. 2h remains the horizon at every floor.

**⭐ The halt gate — CLOSED, by the disproportion test it was meant to pass:** halted-day
trips are 4.42% of the book and carry 15.5% of tail trips / 23.4% of tail net — a genuine
3.5–5× disproportion — **but also 29.1% of the corpus NET** (PF−1 0.288 vs 0.109 unhalted,
win 67% vs 55%). They are the highest-mean AND highest-tail trips. Cutting them from the
k600≥63 mc=1 book: 0.180 → 0.143, net 5,720 → 3,958 (−31%), tail 3.64 → 3.29% (−10%). A
halt gate is a MEAN lever pointing the wrong way. Halted days are a sizing/tail question,
not a cut. (Curious cell: `secs_since_halt` 60–300 s → PF−1 1.19 at n 6,361, 11.6% tail —
the first five minutes after a resumption.)

**Entry time inside k600≥63 mc=1:** 09:45–10:30 IS the book (0.206 / 0.286); 10:30–11:00
negative; after 12:00 dead. The rule lifts 10:00–11:00 (0.286→0.361, −0.07→0.15) and
slightly hurts 09:45–10:00 (0.206→0.190).

**`brv15 ≥ 40`, by year (mc=0):** 49 / 56 / 66 / 67 trips, PF−1 inf / 734 / 6.0 / 9.3, worst
≥ −9.7%, **0% below −20% every year** — the out-of-scale climax bar is real and clean, and
too rare to be a system (~60/yr). Satellite tier; re-check on 2020–22.

**Working spec candidate (2023+):** session-high SHORT, `k600 ≥ 63..96`, entries 09:45–10:30,
hold with the AVWAP rule at 2h (or simply the 5m-low-after-2h exit), no halt gate; sizing
on k600 and brv15≥40 as the S-tier. All of it re-read on the merged seven years next.

### S1f — SpikeFader's gates on MaxFader's entries (user's question, 2023+)

(`scripts/analysis/maxfader_study3.py`, `data/maxfader_study3_2023p.log`.) The spec =
volat≥40bp ∧ be6030>2% ∧ eff10≥0.3 ∧ k300≥40 ∧ k600≥90 ∧ k180≥15 ∧ gap_adj_60<10 ∧
dlv>3% ∧ slope_5m≥0 ∧ slope_20m≥30bp/min ∧ ac1≥−0.1, applied post-hoc (every column is
recorded; `--base-run` only stopped gating). `dslo` is identically 0 on a session-high
entry and cannot participate. `rr` ≡ `brv15_tape` (same column).

**⭐⭐ The spec lifts the hold-to-close book tenfold — and on the same entries SpikeFader's
own exit still beats holding:**

| SPEC, mc=0 (n = 25,697) | PF−1 | net% | win% | worst% | p5% | <−20% |
|---|---:|---:|---:|---:|---:|---:|
| SpikeFader's 9m cover (`aux_lo_540`) | **3.205** | 105,942 | 79.3 | **−45.7** | −8.2 | **1.33** |
| MOC (hold to close) | 1.300 | **160,668** | 68.6 | −239.3 | −25.4 | 6.86 |
| RULE 2h (34% switched) | 1.388 | 154,065 | 65.7 | −162.7 | −23.5 | 5.90 |
| always switch at 2h | 1.569 | 147,832 | 71.1 | −162.7 | −20.2 | 5.09 |

mc=1 (n = 1,153): 9m cover **1.662** / net 2,865 / tail 1.39%; MOC 0.863 / 4,848 / 6.07%;
RULE 2h 1.034 / **4,907** / 5.46%. By year (mc=1) the 9m cover has the higher PF−1 in all
four years (0.90 / 2.14 / 1.77 / 1.63 vs MOC 0.51 / 1.01 / 1.16 / 0.54) and the rule beats
MOC in all four. **Holding to the close buys ~50% more net for a 4–5× fatter tail.** That
is the MaxFader-vs-SpikeFader question answered on shared entries: the 9m cover is the
better *risk* trade, MOC the bigger *net* trade, and the 2h rule sits between them.

**Same-n:** the spec (1.300 / tail 6.9%) beats a tightened `k600` (1.072 / 4.0%) and `dlv`
(0.826 / 16%) at n = 25,697 — the spec adds real information over the count alone, but
its extra PF comes with extra tail.

**Remove-one — what is load-bearing on the hold-to-close:**

| drop | n | PF−1 | <−20% |
|---|---:|---:|---:|
| (full spec) | 25,697 | 1.300 | 6.86 |
| **k600 ≥ 90** | 58,147 | **0.654** | 9.54 |
| **slope_20m ≥ 30bp/min** | 34,874 | **1.026** | 6.58 |
| be6030 > 2% | 32,123 | 1.145 | 5.75 |
| eff10 ≥ 0.3 | 26,319 | 1.196 | 7.06 |
| k300 / k180 / gap60 / ac1 / slope5m / dlv / volat | ≈ | 1.24–1.30 | ≈ |

Three gates carry it — `k600 ≥ 90`, `slope_20m`, `be6030` — the rest are inert here.
Gate-by-gate on top of `k600 ≥ 63`: the trend/speed gates ADD mean and ADD tail
(`be6030`: 0.406 → 0.566, tail 4.2 → 6.4%; `slope_20m`: → 0.626, 6.1%), the count gates
add a little mean with the tail flat (`k300`, `k180`: → 0.47, 4.0–4.2%).

**The voices INVERT, as the side-flip law predicts:** `rr < 0.5` (SpikeFader's quiet-volume
voice) → PF−1 **0.111** inside `k600 ≥ 63` (0.41% tail — safe and empty); `rr ≥ 4` → 0.662
at 9.5% tail; `rr ≥ 40` → **inf** (97 trips, 100% win, +13.6% avg). `volat ≥ 100bp` → 0.954 at
11.7% tail; halted-and-resumed ≤ 300 s → 1.309 at 11.5% tail. Every "strong" voice here is
a tail voice.

**Verdict:** the transferable SpikeFader core is **`k600 ≥ 90` + `slope_20m ≥ 30bp/min` +
`be6030 > 2%`**, and with it the honest comparison is exit-vs-exit on the same entries.
The rule at 2h keeps ~95% of MOC's net at a 15% smaller tail; the 9m cover keeps 65% of
the net at a 5× smaller tail. Where on that line to sit is a sizing question, not a gate
question — and it is the seven-year merged run's to settle.

### S1g — the `rr ≥ 40` CLIMAX system (user's idea): ~7 trades a year, hold to the close, NO rule

(`scripts/analysis/maxfader_study4.py`, `data/maxfader_study4_2023p.log`; `rr` ≡ `brv15_tape`.)

**Bare `rr ≥ 40`, 2023+:** 238 sampler signals collapse to **27 ticker-days** — ~9 signals per
climax day; the sampler averages up into the climax (mc=0 PF−1 13.9) but the tradeable object
is the mc=1 book:

| exit (mc=1, n = 27) | PF−1 | net% | win% | avg% | worst% | <−20% |
|---|---:|---:|---:|---:|---:|---:|
| **MOC (hold to close)** | **6.48** | **167** | 81.5 | **+6.18** | −9.3 | 0.00 |
| RULE 2h (37% switched) | 2.34 | 108 | 74.1 | +3.99 | −11.9 | 0.00 |
| always 2h | 2.73 | 119 | 77.8 | +4.42 | −11.9 | 0.00 |
| 9m cover | 2.85 | 97 | 74.1 | +3.59 | −17.9 | 0.00 |

By year (MOC): 2023 inf (4), 2024 inf (5), 2025 5.46 (10), 2026 1.47 (8) — every year
positive, worst trade −9.3% across all four. **The 2h rule HURTS the climax book** (2026:
+1.47 → −0.40): a >40× bar's fade grinds slowly under a heavy VWAP, so "AVWAP above entry at
2h" is not failure here, and the 5m-low switch hands the win back. So do the always-switch
and the 9m cover. This is the one book in the study where MOC beats every exit rule —
the climax wants to be held.

Rungs: `rr ≥ 60` 12 tkd (MOC 4.31, worst −8.8%); `rr ≥ 100` 4 tkd. `rr ≥ 20` is NOT clean at
mc=1 (119 tkd, 0.445, 2026 −0.18, 8.8% tail) — its mc=0 strength was clustering. Under the
SpikeFader core the ≥40 book is 6 trips (all winners) — too few to gate further.

**Spec candidate (2023+):** session-high SHORT, `rr ≥ 40` at the signal, entries 09:45–15:00
(p50 entry 11:12), **hold to the close**, no rule, no other gate. ~7 trades/yr, +6%/trade,
worst −9%. A satellite by capacity; sized up per the S-tier logic. 2020–22 adds the count.

---

## §S2 — SEVEN YEARS (2020-01-02..2026-08-21): what survives the merge (2026-09-04)

Corpus `data/maxfader_wl_*` = 2023p (953,166) + 2020_22 (1,041,946; 215,539 tkd, 2.9 h):
**1,995,112 trips / 75,302 ticker-days.** All four studies rerun on the merge
(`data/maxfader_study{1,2,3,4}_merged.log`). What changed vs 2023+, and what did not.

### S2a — base and years

Bare session-high short: PF−1 **0.136** mc=0 / **0.045** mc=1, worst −887%, tail 3.95%.
By year (mc=0): 2020 0.173 · 2021 0.165 · 2022 0.053 · 2023 0.365 · 2024 0.058 · 2025
0.154 · 2026 0.050. mc=1: 2022 is a losing year (−0.029). The bare book is not a system on
any horizon; everything below is about what is built on it.

### S2b — ⭐⭐ the AVWAP rule: 6 of 7 years, 2h confirmed

| all entries, mc=0 (n = 1,995,112) | PF−1 | net% | worst% | <−20% |
|---|---:|---:|---:|---:|
| MOC | 0.136 | 1,042,681 | −887 | 3.95 |
| RULE 1h | 0.172 | 1,045,965 | −448 | 2.74 |
| **RULE 2h** | **0.177** | **1,165,976** | −456 | 3.18 |
| RULE 3h | 0.146 | 1,028,393 | −558 | 3.50 |

mc=1 (75,302): 0.045 → **0.065** at 2h (+44%), net 9,590 → 11,890 (+24%), worst −887 → −331.
**By year, rule 2h vs MOC: 2020 0.223 > 0.173 · 2021 0.211 > 0.165 · 2022 0.062 > 0.053 · 2023
0.305 < 0.365 · 2024 0.076 > 0.058 · 2025 0.232 > 0.154 · 2026 0.139 > 0.050 — 6 of 7**, the
tail smaller in all seven. Inside `k600 ≥ 63` mc=1 (17,528): 0.165 → 0.200, net 10,115 →
10,577, tail 3.27 → 2.55%, worst −887 → −306; by year the rule wins 2020 (0.154 → 0.266),
ties 2021/2022, loses 2023. Inside the gated book always-switching at 2h still matches the
conditioned rule (0.218 vs 0.200) — the value is the 5m-low-after-2h exit; the AVWAP
condition earns its keep on the bare book (0.177 vs 0.182 always, but +10% net).
**2h stands as the horizon on seven years.**

### S2c — k600: the book, with a ceiling

Ladder (mc=0): 0.136 → 0.229 (≥35) → 0.344 (≥63) → 0.509 (≥96) → **0.680 (≥140)** → 0.494
(≥200, tail 7.4%). The 2023+ top rung (1.83 at ≥200) does NOT hold: the ladder peaks at
~140 and the ≥200 cell is thin and tail-heavy. `k600 ≥ 63` is positive every year at mc=0
(0.199–0.513) but at mc=1 loses 2022 (−0.039) and is weak in 2024 (0.085). Same-n: the
full SpikeFader spec (0.830) beats a tightened `k600` (0.681) at n = 55,921, as on 2023+.

### S2d — the halt gate stays closed, harder

Halted-day trips: 3.59% of the book, 18.8% of tail net — and **33.3% of corpus net**.
Inside `k600 ≥ 63` mc=1, cutting them: 0.165 → 0.128, net −29%. A mean lever, wrong way.

### S2e — the SpikeFader spec on these entries, seven years

| SPEC, mc=1 (n = 2,420) | PF−1 | net% | worst% | <−20% |
|---|---:|---:|---:|---:|
| 9m cover | **1.412** | 5,083 | **−83** | **1.16** |
| RULE 2h | 0.724 | 7,499 | −130 | 5.91 |
| MOC | 0.643 | **7,604** | −239 | 6.74 |

Weaker than 2023+ (mc=0 1.30 → 0.83: 2020–22 are 0.37–0.57 years at mc=1) but the
ranking holds in **every** year: the 9m cover on PF and tail, MOC on net, the rule between.
Remove-one: `k600 ≥ 90` is the load-bearing gate (drop → 0.480); `be6030` (→ 0.745) and
`eff10` (→ 0.782) matter a little; `slope_20m` (→ 0.806) less than on 2023+.

### S2f — ⭐ the `rr ≥ 40` climax system: real, ~10 a year, one bad year

The 2023+ "0% tail" was an artifact of four clean years. Seven years, mc=1:

| exit (n = 69) | PF−1 | net% | win% | avg% | worst% | <−20% |
|---|---:|---:|---:|---:|---:|---:|
| MOC | 2.304 | **383** | 76.8 | **+5.55** | −51.0 | 2.90 |
| RULE 2h (32% sw) | 2.416 | 362 | 75.4 | +5.25 | −32.9 | 1.45 |
| always 2h | **2.701** | 363 | 79.7 | +5.26 | −32.9 | 1.45 |
| 9m cover | 2.440 | 240 | 75.4 | +3.48 | −33.1 | 1.45 |

By year (MOC): 2020 4.84 (6) · **2021 0.53 (28, the meme year: worst −51%, the only two tail
trips)** · 2022 inf (8) · 2023 inf (4) · 2024 inf (5) · 2025 5.46 (10) · 2026 1.47 (8). Six of
seven years strong; 2021 supplied 40% of the trips and nearly none of the edge — a >40×
bar in a market where everything was a climax was not a climax. The rule is now a wash on
PF and halves the tail (it clips 2021 and costs 2025–26's slow grinders); optional
tail insurance at ~5% of net. `rr ≥ 20` is not clean at mc=1 in any era.

### S2g — verdicts on seven years

* **MaxFlyerV2's volume gates: CLOSED** (unchanged). `brv15 ≥ 40` survives as a satellite:
  ~10 trades/yr, +5.6%/trade, held to the close, 2021 the warning.
* **The AVWAP rule at 2h: CONFIRMED** — 6 of 7 years, tail smaller in 7 of 7, on the bare
  book and inside the k600 book. Its condition matters on the bare book; inside a gated
  book the 5m-low-after-2h exit alone does the work.
* **`k600` is the book**, peaking near ≥140; it is not strong enough at mc=1 to carry 2022.
* **Exit choice is a sizing point on the net-vs-tail line**, in every year: 9m cover
  (PF, tail) → 2h rule → MOC (net). A hold-to-close MaxFader book is SpikeFader's entries
  with a fatter tail and more net; the honest product is the pair, sized by tail budget.
* ⏭ Open: a k600-gated MaxFader mc=1 book carries 2022 negative — either a regime gate
  (2022 was the bear year; the long FlushFader's best) or accept it. Entry-time gate
  (09:45–10:30) not yet applied to the seven-year book. Sizing pass (grade by k600, the
  climax tier) not yet done.

---

## §S3 — THE ARMED STOPS on the `rr ≥ 8` book: hold through the FIRST stop, exit on the SECOND (2026-09-04)

**User's design (MaxRiderV2's rubber-band control on 1s bars):** enter with no stop; the bar
after a new {5m,10m,20m} low ARMS a stop at the prior session high × 1.20; a hit records an
exit mark (next-bar fill), removes the stop, counts a pull-up, and the position STAYS OPEN;
the next low after the hit re-arms at the higher session high. Record-only (commit
`a4094aa`); "exit at the k-th hit" is post-hoc against MOC, the 2h rule and the 9m cover.
Corpus: `maxfader_rr8_whitelist` (ticker-days with any `rr ≥ 8` signal, 2,455 tkd, 2020–26)
→ `data/maxfader_rr8` (182,291 trips, 55 min — IO-bound, one parquet per day);
`scripts/analysis/maxfader_study5.py`, `data/maxfader_study5_rr8.log`.

### S3a — `rr ≥ 8` signals, mc=1 (n = 2,455): the exit table

| exit | PF−1 | net% | win% | worst% | p5% | <−20% |
|---|---:|---:|---:|---:|---:|---:|
| MOC | 0.313 | 3,866 | 62.3 | −866 | −23.3 | 6.15 |
| RULE 2h | 0.369 | 3,872 | 58.6 | −214 | −21.3 | 5.58 |
| 9m cover | **0.481** | 2,714 | 66.5 | **−106** | −12.2 | **2.48** |
| STOP 1 @ 5m arm | **0.287** | 3,482 | 60.7 | −177 | −27.3 | **10.14** |
| **STOP 2 @ 5m arm** | 0.365 | **4,312** | 62.2 | −437 | −23.6 | 6.31 |
| STOP 3 @ 5m arm | 0.314 | 3,862 | 62.3 | −785 | −23.3 | 6.19 |
| STOP 1 @ 10m arm | 0.350 | 4,102 | 61.2 | −210 | −26.0 | 8.80 |
| STOP 1 @ 20m arm | 0.389 | **4,500** | 61.8 | −292 | −25.3 | 7.33 |
| RULE 2h + STOP 1 @ 5m | 0.252 | 2,857 | 57.6 | −177 | −24.4 | 8.47 |

Pull-ups (5m machine): hit once 7.4% of trips, twice 1.3%, three times 0.3%, four 0.1%.

**⭐ The FIRST stop is the worst exit in the table — and it is worst in every slice:** PF−1
below MOC, net −10%, and the tail share **doubles** (6.2% → 10.1%). The mechanism is
mechanical: a hit at +20% over the session high IS a ≥20% realised loss, so every hit
lands in the tail by construction, and a stock that just ripped 20% through its high
reverses often enough that holding recovers more than the stop saves. The user's prior
("exits will have negative expectancy") holds exactly. By year, STOP 1 @ 5m beats MOC in
**1 of 7** years (2024, the −866% year) and raises the tail share in all seven.

**⭐ The SECOND stop is the best of the family:** net +12% over MOC (4,312 vs 3,866), PF−1
+17% (0.365 vs 0.313), worst −866 → −437, tail share unchanged — and it beats MOC in
**5 of 7 years** (2021, 2022, 2023, 2024, 2025; loses 2020 and 2026). Holding through the
first pull-up and exiting on the second is where the expectancy of staying turns — the 1s
mirror of MaxRiderV2's "re-entering more than twice is neutral". The 20m-armed first stop
(0.389 / 4,500 / −292) is the single-stop variant that works, because a 20m low arms
later and rarer (95 hits vs 181), so it behaves like a second stop in timing. The third
stop is MOC with extra steps (hit 0.3% of the time).

**Rule + stop does not add:** whichever fires first, the combination sits below the rule
alone (0.25–0.38 vs 0.369): the stop's early hits override the rule's better exits.

### S3b — the same shape at `rr ≥ 12` and on the climax book

`rr ≥ 12` mc=1 (1,075): MOC 0.755 / 3,611 / 5.95% → STOP 1 @ 5m **0.483** / 2,617 / **11.4%**;
STOP 2 @ 5m 0.735 / 3,557; STOP 1 @ 20m 0.787 / 3,695; rule 0.797; 9m cover 0.847 / 2.6%.
`rr ≥ 40` mc=1 (67): MOC 2.29 → STOP 1 @ 5m 1.46 (tail 3.0 → 6.0%); STOP 2 = MOC (never hit
twice); rule 2.40; 9m cover 2.42. On the climax book the first stop is again the only
thing that hurts.

### S3c — what the stops do and do not do

The stops cut the **worst case** (−866 → −177 at the first hit, −437 at the second) but
not the **tail share** — the share of trips below −20% is 6–10% under every stop variant,
against 2.5% under the 9m cover. The user's stated target was the tail ("the 300% losses
with the 2h rule are still horrible"): on this book the only exit that moves the tail
*frequency* is the 9m cover, at the cost of ~30% of net; the stops and the rule move the
tail *depth* at roughly zero net cost (rule) or +12% net (second stop). Three tools,
three different objects: **rule = depth at par, second stop = depth with net, 9m cover =
frequency for net.** They compose as a sizing choice, not as a stack (the rule+stop
combination is worse than either).

**Verdicts.** (1) Never exit on the first armed stop: it is the worst rule in the study in
every slice and every year but one. (2) If an armed stop is used, hold through the first
hit and exit on the second (5m arm), or use a single 20m-armed stop — both +12–16% net over
MOC with the worst case halved, 5 of 7 years. (3) The 20% offset was not swept; 10% and 30%
are one flag (`--stop-pct`) and a 55-min run each. (4) A "pull up without a hit" variant
(re-set the armed level on every later low) is the untested cousin.

---

## §S4 — the speed fix, the LONG EXITS (30m wins), and the stop-offset sweep (2026-09-04)

### S4a — why 2,455 ticker-days took an hour (and now take twelve minutes)

Not IO. The 1s files are sorted by ticker and DuckDB prunes on it: the engine's exact
per-day query runs in **0.05 s**, a DuckDB copy of every bar the run needed took **16 s**
(`scripts/equity/extract_tape.py`), and the engine on that mini-tape took the **same**
time as on the full tape (42.2 vs 44.9 s, 10 days). The cost was the **mc=0 sampler
fold**: an average of **66 positions open per bar** (max 318) on a loud day, and the
advance loop rebuilt each open position's ~700-field record **seven times per bar** (fwd,
AVWAP, armed, aux, MA, BarsHeld, State) — ~70 GB of memcpy per loud ticker-day. mc=1 on
the same days: 2.4 s. The seven copies never read each other's outputs within a bar, so
they collapse into one: **44.9 s → 10.3 s, byte-identical on all 531 columns** (`a98a27c`).
Every mc=0 run inherits the 4.4×; the rr8 sweep runs are ~7 min each.

### S4b — ⭐⭐ the LONG EXITS: the 30m-low cover is the best exit in the study

User: "maybe we could try really long exits at 30m, 1h, 2h, 3h lows?" Marks added
(`aux_lo_{1800,3600,7200,10800}`: first new N-bar low after entry, next-bar fill,
MOC-resolved with a flag). `rr ≥ 8` mc=1 (2,455), stop offset 0.20:

| exit | PF−1 | net% | win% | worst% | p5% | <−20% | moc-resolved |
|---|---:|---:|---:|---:|---:|---:|---:|
| MOC | 0.313 | 3,866 | 62.3 | −866 | −23.3 | 6.15 | — |
| RULE 2h | 0.369 | 3,872 | 58.6 | −214 | −21.3 | 5.58 | — |
| 9m cover | 0.481 | 2,714 | 66.5 | **−106** | −12.2 | **2.48** | — |
| **30m-low cover** | **0.485** | **3,848** | 65.6 | −190 | −17.1 | **3.75** | 11% |
| 1h-low cover | 0.310 | 3,285 | 64.8 | −473 | −20.4 | 5.25 | 34% |
| 2h-low cover | 0.270 | 3,219 | 63.0 | −866 | −22.9 | 5.99 | 62% |
| 3h-low cover | 0.257 | 3,165 | 63.0 | −866 | −23.3 | 6.19 | 74% |

**The 30m cover has the 9m cover's PF, MOC's net, and a tail 40% smaller than MOC** — the
first exit that does not trade net for tail. 1h converges toward MOC and 2h/3h *are* MOC:
62–74% of trips never print such a low before the close, so the channel cannot form.
By year (mc=1): beats MOC on PF−1 in 4 of 7 years (loses 2020/2022/2026 narrowly), tail
smaller in **7 of 7**, worst better in 6 of 7.

**At `rr ≥ 12` (mc=1, 1,075) it dominates outright:** PF−1 **1.075 vs MOC 0.755** (+42%),
net 3,191 vs 3,611 (−12%), tail 5.95 → **3.44%**, worst −266 → −88; beats MOC in **6 of 7
years** (2022: 2.45 vs 1.27; 2026: 1.00 vs 0.33), tail smaller in 6. The 9m cover there is
0.847 at 55% of the net. The long exit is the sweet spot between "cover fast" and "hold".

### S4c — the stop-offset sweep: the offset moves depth vs frequency, never the sign

`rr ≥ 8` mc=1, first / second stop on the 5m arm and first on the 20m arm:

| offset | STOP 1 @ 5m (PF−1 / tail) | STOP 2 @ 5m (PF−1 / net) | STOP 1 @ 20m (PF−1 / net) |
|---|---|---|---|
| 10% | 0.244 / 5.4% | 0.363 / 4,274 | 0.351 / 4,124 |
| 20% | 0.287 / 10.1% | **0.365 / 4,312** | **0.389 / 4,500** |
| 30% | 0.311 / 8.1% | 0.332 / 4,032 | 0.375 / 4,396 |
| MOC | 0.313 / 6.2% | 0.313 / 3,866 | 0.313 / 3,866 |

A tighter first stop (10%) cuts the tail *frequency* (5.4%) but the PF falls further
(0.244); a looser one (30%) is merely neutral. The first stop never beats MOC at any
offset. The second stop and the 20m-armed first stop are flat across offsets — robust,
+10–16% net over MOC — and 20% is as good as any. **The sweep changes nothing in §S3's
verdict; the armed-stop family is dominated by the 30m cover on every column but net.**

### S4d — the exit ladder for a hold-to-close short, seven years, `rr ≥ 8` mc=1

| tool | PF−1 | net | tail | what it buys |
|---|---:|---:|---:|---|
| MOC | 0.313 | 3,866 | 6.2% | the net |
| second armed stop (5m, 20%) | 0.365 | 4,312 | 6.3% | +12% net, worst halved |
| 2h AVWAP rule | 0.369 | 3,872 | 5.6% | depth at par |
| **30m-low cover** | **0.485** | **3,848** | **3.8%** | **PF and tail at par net** |
| 9m cover | 0.481 | 2,714 | 2.5% | frequency, for 30% of net |

**Working exit for MaxFader: the 30m-low cover.** The 2h rule and the second stop remain
as depth insurance that can stack on it only if a later study shows they fire first
(untested); the 9m cover is the tail-budget option. Next: the 30m cover inside the
`k600` book and with entry-time gating; and whether a 20m/40m cover sits between.

### S4e — the 30m cover by rr band (user: "does it hurt the rr ≥ 40 bucket?") — no

`data/maxfader_30m_by_rr.log`, corpus `data/maxfader_rr8_sp20` (the rr8 whitelist days,
2020–26). Every band, four exits, both mc views. Bands below `rr ≥ 8` are shown for
completeness — every exit is negative there and they are not traded.

**mc=0 — all signals on the rr8 days:**

| band | exit | n | PF−1 | net% | win% | worst% | <−20% |
|---|---|---:|---:|---:|---:|---:|---:|
| (0.0, 1.0] | MOC | 9960 | -0.848 | -80965 | 31.2 | -232.5 | 14.05 |
| (0.0, 1.0] | 30m cover | 9960 | -0.873 | -71088 | 32.9 | -194.5 | 10.82 |
| (0.0, 1.0] | 1h cover | 9960 | -0.867 | -79209 | 31.9 | -232.5 | 12.32 |
| (0.0, 1.0] | 9m cover | 9960 | -0.864 | -46454 | 36.3 | -102.7 | 5.78 |
| (1.0, 2.0] | MOC | 23361 | -0.830 | -232186 | 32.4 | -244.4 | 18.08 |
| (1.0, 2.0] | 30m cover | 23361 | -0.865 | -187341 | 32.7 | -215.9 | 13.76 |
| (1.0, 2.0] | 1h cover | 23361 | -0.858 | -223184 | 33.7 | -239.8 | 16.87 |
| (1.0, 2.0] | 9m cover | 23361 | -0.826 | -104317 | 37.4 | -154.0 | 5.89 |
| (2.0, 4.0] | MOC | 43747 | -0.682 | -343059 | 41.7 | -802.7 | 17.36 |
| (2.0, 4.0] | 30m cover | 43747 | -0.739 | -260370 | 41.8 | -282.8 | 12.22 |
| (2.0, 4.0] | 1h cover | 43747 | -0.736 | -329784 | 42.6 | -439.9 | 15.28 |
| (2.0, 4.0] | 9m cover | 43747 | -0.709 | -158114 | 43.1 | -148.4 | 5.67 |
| (4.0, 8.0] | MOC | 59909 | -0.226 | -123753 | 55.4 | -887.1 | 13.22 |
| (4.0, 8.0] | 30m cover | 59909 | -0.193 | -63035 | 58.8 | -246.6 | 8.38 |
| (4.0, 8.0] | 1h cover | 59909 | -0.302 | -143195 | 58.2 | -485.6 | 10.84 |
| (4.0, 8.0] | 9m cover | 59909 | -0.215 | -48123 | 58.9 | -125.3 | 4.81 |
| (8.0, 12.0] | MOC | 26864 | 0.110 | 22800 | 62.7 | -865.7 | 11.11 |
| (8.0, 12.0] | 30m cover | 26864 | 0.657 | 64855 | 68.9 | -189.8 | 5.49 |
| (8.0, 12.0] | 1h cover | 26864 | 0.137 | 23543 | 66.4 | -472.9 | 8.74 |
| (8.0, 12.0] | 9m cover | 26864 | 0.501 | 36446 | 68.5 | -109.9 | 3.52 |
| (12.0, 20.0] | MOC | 12880 | 0.586 | 50482 | 66.8 | -266.0 | 9.62 |
| (12.0, 20.0] | 30m cover | 12880 | 0.749 | 37960 | 70.8 | -117.1 | 5.98 |
| (12.0, 20.0] | 1h cover | 12880 | 0.508 | 36429 | 69.0 | -183.8 | 7.78 |
| (12.0, 20.0] | 9m cover | 12880 | 1.186 | 32271 | 70.6 | -81.2 | 2.07 |
| (20.0, 40.0] | MOC | 4791 | 0.701 | 21232 | 68.8 | -191.9 | 8.16 |
| (20.0, 40.0] | 30m cover | 4791 | 1.082 | 18028 | 72.4 | -95.4 | 4.55 |
| (20.0, 40.0] | 1h cover | 4791 | 1.235 | 22807 | 72.7 | -170.7 | 5.28 |
| (20.0, 40.0] | 9m cover | 4791 | 1.688 | 15276 | 73.2 | -57.6 | 2.05 |
| (40.0, inf] | MOC | 779 | 1.486 | 3767 | 69.7 | -51.0 | 3.21 |
| (40.0, inf] | 30m cover | 779 | 2.295 | 3754 | 80.5 | -44.8 | 2.95 |
| (40.0, inf] | 1h cover | 779 | 2.050 | 3958 | 76.8 | -51.0 | 2.95 |
| (40.0, inf] | 9m cover | 779 | 2.113 | 2305 | 77.7 | -33.1 | 2.95 |

**mc=1 — first signal per ticker-day, banded by that signal's rr:**

| band | exit | n | PF−1 | net% | win% | worst% | <−20% |
|---|---|---:|---:|---:|---:|---:|---:|
| (0.0, 1.0] | MOC | 456 | -0.912 | -5186 | 21.3 | -175.9 | 18.42 |
| (0.0, 1.0] | 30m cover | 456 | -0.940 | -4434 | 21.3 | -194.5 | 14.25 |
| (0.0, 1.0] | 1h cover | 456 | -0.925 | -5121 | 23.5 | -175.9 | 16.23 |
| (0.0, 1.0] | 9m cover | 456 | -0.922 | -2809 | 25.0 | -102.4 | 7.68 |
| (1.0, 2.0] | MOC | 689 | -0.852 | -8498 | 29.8 | -239.8 | 19.88 |
| (1.0, 2.0] | 30m cover | 689 | -0.907 | -7303 | 26.1 | -214.4 | 18.00 |
| (1.0, 2.0] | 1h cover | 689 | -0.890 | -8385 | 28.7 | -239.8 | 18.58 |
| (1.0, 2.0] | 9m cover | 689 | -0.880 | -4362 | 26.3 | -154.0 | 10.01 |
| (2.0, 4.0] | MOC | 562 | -0.765 | -5511 | 36.1 | -400.1 | 19.40 |
| (2.0, 4.0] | 30m cover | 562 | -0.853 | -5159 | 32.0 | -282.8 | 16.01 |
| (2.0, 4.0] | 1h cover | 562 | -0.827 | -5676 | 35.1 | -439.9 | 18.15 |
| (2.0, 4.0] | 9m cover | 562 | -0.827 | -3484 | 33.1 | -113.0 | 10.32 |
| (4.0, 8.0] | MOC | 374 | -0.316 | -1174 | 53.5 | -887.1 | 10.43 |
| (4.0, 8.0] | 30m cover | 374 | -0.390 | -980 | 55.3 | -196.3 | 9.63 |
| (4.0, 8.0] | 1h cover | 374 | -0.419 | -1362 | 54.8 | -485.6 | 10.16 |
| (4.0, 8.0] | 9m cover | 374 | -0.348 | -582 | 48.9 | -110.6 | 5.35 |
| (8.0, 12.0] | MOC | 211 | 0.499 | 303 | 56.9 | -120.4 | 2.84 |
| (8.0, 12.0] | 30m cover | 211 | 0.485 | 219 | 61.1 | -59.6 | 1.42 |
| (8.0, 12.0] | 1h cover | 211 | 0.384 | 213 | 58.8 | -101.1 | 3.32 |
| (8.0, 12.0] | 9m cover | 211 | 0.166 | 71 | 62.6 | -82.5 | 1.90 |
| (12.0, 20.0] | MOC | 101 | -0.309 | -162 | 54.5 | -266.0 | 3.96 |
| (12.0, 20.0] | 30m cover | 101 | -0.100 | -30 | 59.4 | -66.6 | 2.97 |
| (12.0, 20.0] | 1h cover | 101 | 0.088 | 25 | 56.4 | -41.6 | 3.96 |
| (12.0, 20.0] | 9m cover | 101 | 0.014 | 3 | 65.3 | -79.6 | 0.99 |
| (20.0, 40.0] | MOC | 46 | 0.275 | 51 | 67.4 | -78.8 | 2.17 |
| (20.0, 40.0] | 30m cover | 46 | 0.201 | 31 | 76.1 | -71.4 | 2.17 |
| (20.0, 40.0] | 1h cover | 46 | 0.072 | 13 | 69.6 | -78.8 | 2.17 |
| (20.0, 40.0] | 9m cover | 46 | 0.405 | 41 | 69.6 | -57.6 | 2.17 |
| (40.0, inf] | MOC | 16 | 3.138 | 55 | 75.0 | -10.5 | 0.00 |
| (40.0, inf] | 30m cover | 16 | 4.950 | 57 | 81.2 | -5.8 | 0.00 |
| (40.0, inf] | 1h cover | 16 | 4.713 | 65 | 75.0 | -5.9 | 0.00 |
| (40.0, inf] | 9m cover | 16 | 2.214 | 43 | 68.8 | -7.8 | 0.00 |

**The dedicated climax book (any `rr ≥ 40` signal that day → the day's first such signal;
67 ticker-days, mc=1), by year:**

| year | n | MOC | 30m cover | 9m cover | net MOC | net 30m | worst MOC | worst 30m |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 2020 | 6 | 4.84 | 46.53 | 37.30 | 51 | 47 | -11 | -1 |
| 2021 | 28 | 0.53 | 0.64 | 1.22 | 69 | 64 | -51 | -45 |
| 2022 | 8 | inf | inf | 6.12 | 97 | 88 | 1 | 2 |
| 2023 | 4 | inf | inf | inf | 21 | 31 | 3 | 3 |
| 2024 | 5 | inf | 21.66 | 18.22 | 64 | 47 | 0 | -2 |
| 2025 | 10 | 5.46 | 2.67 | 1.62 | 61 | 47 | -9 | -9 |
| 2026 | 6 | 1.30 | -0.26 | -0.25 | 19 | -5 | -9 | -10 |

All seven years: MOC 2.29/net 381  30m 2.29/net 319  9m 2.42/net 238   30m moc-resolved 10%

**Reading.** At mc=0 the 30m cover raises PF−1 in every band from `rr ≥ 8` up — 8–12: 0.110
→ 0.657, 12–20: 0.586 → 0.749, 20–40: 0.701 → 1.082, **> 40: 1.486 → 2.295 at identical net
(3,754 vs 3,767)** — and cuts the tail share in each. A climax bar rarely prints a fresh
30m low before its fade is done (10% resolve at MOC), so the cover mostly rides. On the
dedicated climax book the 30m cover keeps the PF (2.29 = 2.29) and gives up 16% of net,
losing 2025 (5.46 → 2.67) and 2026 (1.30 → −0.26, six trades) — the slow-grinder years of
§S1g. **Run the climax book to the close; inside the general `rr ≥ 8` book the 30m cover
is free on the ≥ 40 bucket.**

**⭐ Conclusion (user, 2026-09-04): the complex exit-and-re-entry machinery is not needed.**
On the `rr ≥ 8` book the 30m-low cover beats the 2h AVWAP rule, every armed-stop variant
(first, second, third hit; 5m/10m/20m arm; 10/20/30% offset) and the rule+stop
combinations on PF−1 and tail at MOC's net (§S4b, §S4c). One channel exit, no stops, no
re-arms, no pull-ups.
