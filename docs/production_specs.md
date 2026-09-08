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
- **THE BROAD BOOK** (user 2026-09-08, "maximise trades for a small account"): efficiency-curve step 7 = frame +
  lows300 + eff10 + v10r + z20 + lows180 + crf + **coil5lo** (consol_5m_lag1m ≤ .22) , NO vote, **gap_60 < 40**,
  RULE volat ≥ 140 bp only if gap < 4 → **37,348 @ 1.46 gross, +0.55%/trade at a $0.001/sh/side rebate credit**;
  sized on gap × volat (`scripts/equity/flushfader_sizing_broad.py --door 40 --credit 0.001 --rule140`).
  Volume at $10k: 469 trades/mo, 2.3M sh/mo (80% sub-$5). OPEN: the passive fill model (S49l).
- Ruled OUT of the spec (S49b): eff10, s20, s5, speed, dlv (magnitude dials → sizing), z20, rflow.

**Status**: Scanner (`TradingEdge.Scanner`, private repo) is at SPEC v3.1, sealed zero-diff; the production frame
(E vs broad) is the user's pending decision; consol columns not yet in the Scanner.

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

## 5. LowFader — (to be filled from docs/lowfader_results.md SPEC v4)

## 6. SpringFlyer — (to be filled from docs/springflyer_results.md SPEC v2)
