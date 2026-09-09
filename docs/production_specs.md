# PRODUCTION SPECS — the registry (started 2026-09-08, user: "save the production specs somewhere they can be found and reproduced")

One entry per system: the EXACT rules, the corpus/caches, the command that reproduces the reference book, the numbers
to check against, the price floor, and the status. Every research doc's detail stays where it is; this file is the
index that tells you WHERE the current truth lives and HOW to regenerate it. Commands run from `research/`.
⚠ Update this file in the same commit as any spec change.

---

## 1. FlushFader — 1s LONG mean reversion (THE system) — `docs/flushfader_results.md`

**Signal**: new strict 1200-bar (≈20m) vwap low → buy next bar vwap; exit strict new 300-bar (≈5m) high → sell next
bar vwap, else hold to the NEXT session's open (calendar-aware close 16:00 / 13:00). One leg per new 1200-bar high.
Engine `TradingEdge.FlushFader` (Argu flags; `--base-run` = every spec gate OFF).

**Reference frame = SPEC v3.1 × ROSTER v3.3 (sealed 2026-08-30, S43cr-b)** — book **1,369 @ 4.128, trimPF 9.860**:
- corpus `data/equity/flushfader/v49_spec20/` (whitelist `flushfader_v49tkd_cand`, flags in `data/equity/flushfader/v49_spec20.log`);
  reproduce: `scripts/equity/rr_port_reruns.sh` (v50 block) — `--start-date 2020-01-02 --end-date 2026-08-21
  --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 --min-lows-180 0`; then
  `python scripts/equity/flushfader_book.py` (BOOK_WHERE = frame + ROSTER v3.3 + mc=1 per ticker-day).
- Layer 1 frame: dv_0945_tape ≥ **$2M** (the candidate floor; $3M RETIRED S49a) · dv60 ≥ $100k ∧ tc60 ≥ 60 over 60
  tradeable s (S43cr) · volat_20m ≥ 40 bp · 09:45 ≤ signal ≤ 15:00 · gap_60 < 4 · raw px ≥ $1 · barnum ≥ 22.
- Layer 2 engine gates (18): speed < −2%/1m · d1m < −2% · ssf ∈ [−375,−25) bp/m · dlv < −3% · rflow ≥ −0.95 ·
  z20 < −1.5σ · cascade (ht ≥ 1 wait 120 s, ht ≥ 3 wait 1200 s) · K ∈ [26,50] · |eff20| ∈ [.30,.50) · |eff10| ≥ .15 ·
  eff9ema10 ≥ −.10 · vol10rate ≥ .75 (bar-clock) · lows300 ≥ 6 · lows180 ≥ 3 · rngfront < .80 · accel1020 ≥ −80 ·
  slope20 < −10 · slope5 ≥ −400.
- Layer 3 ROSTER v3.3 (≥ 1 of): volat ≥ 140 bp · d20a < −28% · dslo ≥ +8% · vexp > 12 · vcrush ≤ −24 · acneg < −.1 ·
  legage ≤ 450 s · dsu ≥ 8 · halt band ssh ∈ [1200,4800) · S-tier ht ≥ 1 ∧ ssh ∈ [120,1200).
- Sizing tiers A/B/C/D: gap_adj_1200 < 15 × ols_slope_60·6e5 ≤ −350; multipliers 2.44/1.80/1.14/1.00.

**§S49 review (2026-09-08) — the candidate frames** (tool `scripts/equity/flushfader_gate_review.py`, corpus
`data/equity/flushfader/base_v19/` = gates OFF + consol, slice `data/flushfader_review_slice.parquet`; the predicate
dictionary reproduces v49 EXACTLY, 39,769 = 39,769):
- **SPEC v4 candidate "E"** (9 gates): K · eff20 · d1m · **crf ≤ −0.2%** (`chg_since_run_first_low`) · cascade · rngf ·
  lows300 · ssf · accel + ROSTER vote, $2M floor → **1,423 @ 3.861, trimPF−1 8.38**.
  `python scripts/equity/flushfader_gate_review.py --drop lows180,speed,dlv,rflow,z20,eff10,e9,v10r,s20,s5 --add crf --eval E`
- **THE BROAD BOOK — PRODUCTION (user rulings 2026-09-08/09)**: efficiency-curve step 7 = frame ($2M floor, volat ≥ 40 bp,
  09:45–15:00, raw px ≥ $1, barnum ≥ 22, dv60/tc60) + lows300 ≥ 6 + |eff10| ≥ .15 + vol10rate ≥ .75 + z20 < −1.5σ +
  lows180 ≥ 3 + crf ≤ −0.2% + **coil5lo** (consol_5m_lag1m ≤ .22), NO vote, **gap_60 < 40**, RULE volat ≥ 140 bp only if
  gap < 4, **WAIT: ht ≥ 4 ∧ ssh < 300 → no trade** (S49p) → **37,279 trades @ 1.536 net of a $0.001/sh/side credit,
  +0.55%/trade** (`flushfader_gate_review.py --preset broad`; sizing scripts rebuild the same book from the slice).
  **SIZING = model A3 (S49ae)**: multiplier = fitted PF−1 / 0.519, power 1, clip [0.25, 4]; PF−1 = p/((1−p)·L/W) − 1 from
  three log-linear component fits (logit p, log W, log L) on gap {0, 1–3, 4–39} × volat {40–60, 60–90, 90–140,
  140–250, 250+} × rate600 {< .07, ≥ .07} × S tier {ht ≥ 1 ∧ ssh ∈ [300,2400)}; the finite table (0.52 – 3.12) is in
  `data/flushfader_gate_review/sizing_model.md` (PRODUCTION TABLE) and §S49ae. Holdouts 1.527 / 9,744 fwd · 1.728 /
  15,266 mirror vs flat 1.442 / 7,624 · 1.615 / 12,768. `python scripts/equity/flushfader_sizing_model.py --credit 0.001 --rule140`.
  Volume at $10k: 469 trades/mo, 2.3M sh/mo (80% sub-$5). OPEN: the passive fill model (S49l); consistency study.
- Ruled OUT of the spec (S49b): eff10, s20, s5, speed, dlv (magnitude dials → sizing), z20, rflow.

**Status (2026-09-09 evening, §S49aj)**: the Scanner (`TradingEdge.Scanner`, private repo) IS THE BROAD BOOK — crf, coil,
counters600/rate600, the gap door, rule140, the WAIT and the A3 sizing (`Engine/Sizing.fs`, betas from the committed
`data/flushfader_gate_review/a3_coefficients.json`) are in; the 13 dropped gates and the ROSTER are deleted. Sealed 2026-09-09: full period `--from-bars` 209,252 = 209,252 engine-level trips and **37,254 = 37,254 book** (multiplier |Δ| < 4e−15); every gate OFF per year vs base_v19 **8,273,415 = 8,273,415** on 53 columns; 10-day trades tape `--gate --ms-precision` 1,180 / 201 zero-diff, gate exact 10/10; `Sizing_Test.fsx` 52/52 cells.
Reference = `scripts/equity/flushfader_broad_reference.py` (double precision, calendar-aware cutoff: book 37,254 — the
S49 scripts' 37,279 includes 25 half-day 12:00–13:00 trades the engine refuses); diff = `scripts/equity/scanner_diff.py`.
Not built: the order-management layer (20-unit cap, one open position per ticker, equity fraction per unit).

---

## 2. SpikeFader — 1s SHORT spike fade — `docs/spikefader_results.md`

**SPEC** (`scripts/analysis/spikefader_zq.py` `SPEC`, engine `TradingEdge.SpikeFader`): volat_20m ≥ 40 bp ·
signal_vwap / vwap_ewp_6030_be − 1 > 2% · eff_10m ≥ 0.3 · highs300 ≥ 40 · highs600 ≥ 90 · highs180 ≥ 15 ·
gap_adj_60 < 10 · dlv (signal/sess_low − 1) > 3% · ols_slope_300 ≥ 0 · ols_slope_1200 ≥ 5e-5 (the script's
rescale of 0.0030) · signal_sec < 15:30 · ac1_ewma ≥ −0.1 · **signal_vwap ≥ $1 (§S49, 2026-09-08)**.
Exit = 9m (540 s) channel low (§S47: 30m REJECTED). ROSTER v3.4 sizing: A rr < 0.5 (1.00) · B dslo ≤ −5% (0.99) ·
X rr ≥ 12 (0.74) · C halts ∧ fresh (0.75) · D volat ≥ 100 bp (0.48) · rest 0.19.
**Corpus** `data/spikefader_s47/` (`data/spikefader_s47.log` has the flags); reproduce the book with
`python3 -u scripts/analysis/spikefader_s47_exits.py` (SLICE header) → **3,067 @ 2.327** with the floor
(3,573 @ 2.213 without; sub-$1 slice 544 @ 1.93). Volume at $10k: 46 trades/mo, ~150k sh/mo above $1.
**Status**: research-complete; no Scanner port yet; borrow = the live constraint.

---

## 3. LongSnoozer — overnight reversal LONG (15:59 LIMIT → next open) — `docs/longsnoozer_results.md` §4

`python scripts/equity/snoozer_production_books.py [--floor 1]` (caches `data/equity/flushfader/snoozer_{shape,volat,cache}.parquet`,
universe mr_candidate_1s_v2 = dv_0945_tape ≥ $2M ∧ n_bars_1s ≥ 200, 2016-08..2026-08).
Signal chg60k59 < −6% (15:00 → 15:59 vwap). r = (open_p1 + div_p1) / px_lim_1559_1600 − 1.
- **A++ (1.00)**: volat_open30 ∈ [30,60) bp ∧ dv_over_open30 ≥ 0.498393 ∧ bar_over_open30 ≥ 0.652937 → 476 @ 3.452
- **A+ (1.00)**: volat_open30 ∈ [60,120) ∧ dv_over_open30 ≥ 0.498393 ∧ bar_over_open30 ≥ 0.925649 → 211 @ 4.152
- (B++ satellite 0.35: volat [60,120) ∧ I+ ∧ pers ∈ [0.652937, 0.925649) → 304 @ 1.590)
⚠ The quantile thresholds are the exact population values; the doc's rounded 0.50/0.65/0.93 do NOT reproduce.
**$1 floor (p1559 ≥ 1)**: A++ 453 @ 3.044 / A+ 195 @ 3.426 — the 39 sub-$1 trades are PF 33.8, +18.6%/trade (the
floor COSTS here; user ruling pending). Volume at $10k: 7.6 trades/mo, 27k sh/mo. **Status: NOT ADOPTED** (A+ has
< 5 trades/yr before 2020; A++ 2026 = 29.8 on n=46); caches ns-era.

## 4. ShortSnoozer — overnight reversal SHORT — `docs/shortsnoozer_results.md` §S43cw

Same script/caches. Signal chg60k59 > +8%; r = −ovn_from_lim59; gaps = 3540 − nb60k59.
- **S (1.00)**: volat_open30 < 40 bp ∧ gaps ≥ 1500 → 142 @ 6.884 (worst −15%)
- **A (0.50)**: volat < 40 ∧ gaps ∈ [500,1500) → 108 @ 3.244
- **B (0.35)**: volat ∈ [40,100) ∧ gaps ≥ 2000 → 378 @ 2.775 (worst −64%)
- SKIP: volat [40,100) ∧ gaps < 2000 (509 @ 0.918, worst −235%); volat ≥ 100 not in the book.
**$1 floor**: S 141 @ 6.848 / A 103 @ 4.135 / B 357 @ 2.735 — free (sub-$1 slice 27 @ 2.12). Volume at $10k: 6.7
trades/mo, 28k sh/mo. **Status: NOT ADOPTED** (borrow, fees, spreads unmodelled); open item = last-hour staleness guard.

---

## 5. LowFader — 1s LONG session-low fade, hold to MOC — `docs/lowfader_results.md` §L23 (SPEC v4 RATIFIED 2026-09-06)

**Engine** `TradingEdge.LowFader` — the config DEFAULTS ARE the spec (`Backtest.fs:73-87`); a trip with `spec_ord > 0`
in the corpus passed every gate. Signal = new SESSION vwap low (EntryChannelBars 0), fill next bar vwap; **exit MOC**
(16:00 / 13:00 early; 10m cover REJECTED §L24; next-open OFF). Window 09:45–15:00. Universe mr_candidate_1s_v2 ∧
barnum ≥ 22. Floors: dollar_vol_60 ≥ $100k ∧ trade_count_60 ≥ 60 (present bars) ∧ volat_20m ≥ 30 bp.
**Ord gates (SPEC v4 = WIDE-VG)**: volat_20m ∈ (0.003, 0.010] · eff_ewma_10m < −0.7 · lows_since_first_low_600 ≥ 40 ·
rate_600 ≥ 0.15 · rr = vol_60 / (vol_0945_tape/15) ≥ 2.0 (TIME clock) · vwap/(close_m1+div_m1) − 1 ≤ −4% ·
gap_adj_60 ≤ 60 · dollar_vol_60 ≥ $1M (TIME clock) · lows_since_first_low_120 ≥ 30 · **crf ≤ −0.2%** (the
which-bar gate; the run's first low fails) · dv_0945_tape < $20M. Cold features FAIL.
**mc = 1** = the FIRST qualifying bar per ticker-day, NO averaging down. **Sizing**: A = STRICT ∧ X → 1.00, rest 0.3,
where STRICT = volat_20m > 0.005 ∧ gap_adj_60 ≤ 30 and X = rr ≥ 5 ∨ lows_rr3_120 ≥ 20 (`scripts/analysis/lowfader_wide_grades.py:16`).
**Corpus** `data/lowfader_wl_wide/` (whitelist `lowfader_wide_whitelist`, 360 tkd, 2020-01-01..2026-08-31; banner
`data/lowfader_wl_wide.log`); rebuild `scripts/equity/lowfader_run_wl.sh 2020-01-01 2026-08-31 wide lowfader_wide_whitelist "--min-volat-20m 0.003"`.
**Reference**: SPEC v4 = **221 trades / 33.3 per yr / PF 5.17** (PF22 4.57, worst −12.8); grade A 36 @ 21.6; years
2020 8.60 (49) · 2021 4.06 (33) · 2022 12.8 (37) · 2023 1.88 (30) · 2024 6.00 (27) · 2025 6.60 (33) · 2026 1.71 (12).
Reproduce = `WHERE spec_ord > 0` on the corpus (verified 221). ⚠ GAP: the committed replay scripts
`lowfader_wide_book.py` / `lowfader_wide_grades.py` hardcode crf ≤ −0.001 (the REJECTED wide variant, 275 trades) —
change to −0.002 or use `spec_ord`. **Price floor: NONE** (`MinPrevClose` 0; 7 of the 221 book trades enter under $1,
min $0.05) — the $1 decision is pending (SpikeFader/FlushFader carry $1; SpringFlyer $2).
**Status**: ratified; Scanner port pending; live sizing per the A/rest ladder.

## 6. SpringFlyer — DAILY-bar SHORT of the straight-line climax — `docs/springflyer_results.md` §S9 (SPEC v2 RATIFIED 2026-09-07)

**Rules** (one close, day D, on share-consistent closes cn = close·n from `daily_episodes_causal`): er10 > 0.9 ·
er20 > 0.6 (Kaufman efficiency ratio over 10/20 sessions) · cn(D) ≥ max cn over the 20 sessions INCLUDING D (new 20d
closing high) · rng20c_d = max/min − 1 over the same 20 sessions > 2.0 (> 200%). FRAME: dv20_prior ≥ $5M ∧
**prev_close_raw ≥ $2** ∧ barnum ≥ 22 ∧ CS/ADRC. Feature table `data/springflyer_daily.parquet`
(`python3 scripts/equity/springflyer_daily.py --rebuild`); the 20d-high term is computed at query time (SQL in the
S9 section / the registry agent's note). **Entry** SHORT at D's own close (15:59 limit / MOC live); **exit** cover at the
close 10 sessions later (the 10d timestop; low-exit variants NOT adopted); returns dividend-inclusive, bp.
**mc = 1 per NAME** (repeats inside the hold dropped; hold approximated as 10 × 1.45 calendar days — open item).
**Reference**: 827 signals (2005..2026-09-04, 439 names, 37.6/yr, 66/yr since 2017; reproduced exactly); mc=1 book
**480 trades / 22 per yr / PF 3.17 / win 74% / mean +1,731 bp / median +2,107 / worst −19,094 bp / 17 of 21 years**.
**Sizing: NONE yet** — a −200% trade at full size is ruin (open blocker with the adverse-side stop). ⚠ GAP: **the
script that produced `data/springflyer_spec_v2.log` was never committed** — the signal SQL reproduces, the mc=1
collapse must be re-implemented. **Status**: ratified; **BORROW is the blocker** (TradeZero / Lightspeed / IBKR
availability for $2–10 mania runners to be evaluated); squeeze tail p95 5d-high +98%.
