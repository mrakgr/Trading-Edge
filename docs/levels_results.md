# Key-level trend — results

The user's idea (2026-11-03, after a Neerav Vadera video: large orders are the key levels): a trend-following futures
system that decides by levels. The engine is `TradingEdge.Breakout/LevelEngine.fs` (`--window level`); the spec and its
rulings are in the engine's header.

## LV-1 — the first look (2026-11-03)

**The rule:**
- **The level:** each 1s bar's priority is a 5 s half-life EWMA of the volume, priced at the same EWMA of the vwap. The
  key level is the session's max of those priorities decayed with a 5 min half-life (ties go to the newest bar).
- **The bias:** a 30 s half-life, volume-weighted EWMA mean and σ of the 1s vwaps. Long when the mean is more than 2σ
  above the level, short when it's more than 2σ below; otherwise the position is kept. It's stop-and-reverse, with
  fills at the next present bar's vwap.
- **Scope:** RTH; no signal in the first 5 min, no entry after 15:00, flat at 16:00. Every root, 2015–2026 (fit
  2015–22 / holdout 2023–26).

Run: `dotnet TradingEdge.Breakout/bin/Debug/net10.0/TradingEdge.Breakout.dll --window level` (2 min 10 s, 1.5 GB) →
`data/futures/levels/run_lv1` (1,215,435 trips). Report: `scripts/futures/levels_report.py` →
`data/futures/levels/report_lv1.md`.

**Check:** `scripts/futures/levels_check.py` recomputes everything in numpy from the raw bars: the EWMAs in clock time,
the level by BRUTE FORCE (the max over every past bar of its decayed priority, no ln shortcut), the signals, the trips
and the marks. Results:
- PASS: 7,232 trip fields and 3,182 brute-force points on 12 random sessions of the full run, and 4,960 / 2,370 on the
  ES/NQ test run.
- Control: the check recomputed with a 290 s level half-life against the engine's 300 s gives FAIL (31 mismatches,
  some sessions with a different trip count).
- No ties were seen in either sample, so the tie rule is untested by the check.

| group | trips/day (holdout) | PF raw fit → holdout | holdout −1 tick | holdout mean ticks | median hold | % reversals |
|---|---|---|---|---|---|---|
| ags | 50.0 | 0.98 → **0.99** | 0.66 | −0.02 | 12.5 min | 94 |
| bonds | 37.5 | 0.94 → **0.92** | 0.44 | −0.10 | 14.7 min | 94 |
| energy | 78.1 | 0.98 → **0.97** | 0.84 | −0.13 | 12.4 min | 95 |
| equity | 51.3 | 0.99 → **1.00** | 0.93 | +0.33 | 13.4 min | 94 |
| fx | 178.9 | 0.96 → **0.95** | 0.63 | −0.10 | 13.1 min | 94 |
| metals | 38.6 | 0.96 → **0.98** | 0.85 | −0.16 | 12.6 min | 95 |
| sofr | 10.8 | 0.86 → **0.78** | 0.28 | −0.19 | 22.6 min | 91 |
| vx | 14.4 | 1.01 → **0.95** | 0.40 | −0.06 | 15.8 min | 93 |

**Findings:**
- **No edge.** The best roots are NQ (1.00 → 1.03, +1.32 ticks/trip, 1.01 net of a tick) and RB (1.02 → 1.01).
  Every other root is at 0.87–1.02, and every group-year is near 1.
- **Whipsaw.** 94 % of trips end in a reversal after about 13 minutes: the price crosses the band on the other side of
  the level. The bias does not follow through.
- **The band is tiny.** The 30 s σ has a median of about 1 tick on most roots (0.2–0.6 ticks on bonds, SOFR and VX;
  15 on NQ). "2σ from the level" is often 2–3 ticks, inside the ordinary noise.
- **The same entries held a fixed 5 s to 30 min:** PF 1.01 down to 0.98. The timing of the exit doesn't hide an edge.
- **No bucket stands out:** not the signal's z, nor the level's age, nor σ in ticks. The σ ≥ 8 ticks bucket is NQ's
  +0.64 ticks, at PF 1.00.

Open levers (not run): a σ floor or a band in absolute ticks / ATR; a level that must be significant (its priority
against the session's typical); a longer bias EWMA; entries only on the first cross of the level.

## LV-2 — the bias window (2026-11-03)

User: "I am surprised that 2 sigma is 1 tick wide on most roots ... Should we try making it 1m then". σ is the spread of
the vwaps over the last ~30–60 s around their own mean: a contract mostly sits on a few ticks over half a minute (bonds,
SOFR and VX are tick-constrained), and signals happen more easily when σ is small. A random walk's spread grows ~√time.
Runs: `--bias-half-life 60 / 120 / 300` → `data/futures/levels/run_lv1_b{60,120,300}`, reports `report_lv1_b*.md`.

| bias half-life | trips | median σ | % reversals | median hold | PF raw fit → holdout | holdout −1 tick |
|---|---|---|---|---|---|---|
| 30 s | 1,215k | 1.2 ticks | 94 % | 13 min | 0.98 → 0.97 | 0.68 |
| 1 min | 519k | 1.7 ticks | 87 % | 31 min | 0.98 → **1.00** | 0.79 |
| 2 min | 150k | 2.7 ticks | 57 % | 101 min | 1.00 → 0.99 | 0.86 |
| 5 min | 25k | 4.2 ticks | 12 % | 260 min | 0.99 → 1.04 | 0.97 |

- **A slower bias cuts the whipsaw.** Reversals fall from 94 % to 12 % as the band widens.
- **It creates no edge.** Raw PF stays at 0.98–1.00, and the net PF rises only because fewer trips pay the tick.
- **The 5-minute group cells are noise.** Ags 1.20, SOFR 1.35 and VX 1.45 come from 1–4 trips a day, most held to the
  close, and fit and holdout disagree (equity 1.04 → 0.92).

## LV-3 — the level's significance (2026-11-03, 1 min bias)

User: "trade only when the champion's volume is large compared with the session's typical priority". Recorded at each
signal, causally:
- `typ_med`: the session-to-date RUNNING MEDIAN of every bar's priority (two heaps, exact).
- `level_now`: the champion's priority decayed to the signal.
- The trips are the same 519,063 as LV-2's 1 min run.

The check (`levels_check.py --bias-hl 60`, typ_med by brute-force np.median): PASS, 2,550 fields on 10 sessions.
Deciles with FIT-year edges: `scripts/futures/levels_sig.py` → `data/futures/levels/report_lv3.md`.

| level_vol / typ_med | PF raw fit → holdout | holdout −1 tick |
|---|---|---|
| D1 (≤ 2.2×) | 0.96 → 0.98 | 0.82 |
| D3 (2.9–3.6×) | 1.00 → 1.00 | 0.85 |
| D5 (4.4–5.3×) | 0.96 → 1.01 | 0.84 |
| D7 (6.5–8.0×) | 0.97 → 0.95 | 0.76 |
| D9 (10.7–16.5×) | 0.98 → 1.05 | 0.78 |
| D10 (> 16.5×) | 0.96 → 0.96 | 0.59 |

- **Flat.** Every decile is 0.95–1.05 on the holdout and 0.95–1.00 on the fit, with no slope from weak levels to
  strong ones.
- **The decayed `level_now / typ_med` is as flat** (0.95–1.05).
- **Even a level 16× the typical priority gives no follow-through.**

**Verdict after LV-1..3:** the price crossing a high-volume level by a margin doesn't predict the next move on 1s
futures — the same picture as BO-1..8 (breakouts revert). Not run: a band in absolute ticks / ATR, and first cross
only.

## LV-4 — an absolute band, and the session VWAP as the level (2026-11-03, 1 min bias)

User: "A band in absolute ticks or ATR ... Let's give this a try. We also try using the session VWAP as a level. That
worked very well in one paper on Nasdaq" (most likely Zarattini & Aziz's QQQ study: long above the VWAP, short below,
1-minute bars, stop-and-reverse).

**What was added:**
- `--band ticks|atr`: the band is K ticks, or K × the root's causal 1-minute ATR (its mean 1-minute vwap range over the
  previous 20 sessions; a root trades from its 21st session).
- `--level vwap`: the cumulative RTH session VWAP replaces the key level.
- The regression: the new code at the LV-3 settings reproduces run_lv3 row for row (519,063 = 519,063, no diff).
- The checks (`levels_check.py --band atr`, the ATR recomputed from scratch — the fronts query, the previous sessions'
  minute ranges — and `--level vwap`): PASS on run_lv4_ka05 (8,021 fields) and run_lv4_va05 (1,604 fields).

Runs: `data/futures/levels/run_lv4_*`. Summary: `scripts/futures/levels_summary.py` → `data/futures/levels/report_lv4.md`.

Cells: PF raw fit → **holdout** (the holdout net of one tick per trip):

| variant | trips/day/root | all roots | equity | NQ | NQ holdout mean ticks |
|---|---|---|---|---|---|
| key level, 2σ (LV-2) | 7.7 | 0.98 → **1.00** (0.79) | 0.99 → **1.00** | 1.00 → **0.98** | −0.1 |
| key, 0.5 ATR | 34.0 | 1.01 → **1.01** (0.69) | 1.02 → **1.05** | 1.01 → **1.06** | +2.2 |
| key, 1 ATR | 16.2 | 0.99 → **1.00** (0.77) | 1.01 → **1.07** | 1.00 → **1.09** | +5.4 |
| key, 2 ATR | 5.9 | 0.99 → **0.96** | 1.07 → **0.99** | 1.04 → **0.97** | −2.4 |
| key, 4 ticks | 17.1 | 0.99 → **1.01** (0.89) | 1.02 → **1.05** | 1.02 → **1.05** | +1.2 |
| VWAP, no band (the paper's rule) | 10.1 | 1.01 → **0.98** (0.69) | 1.09 → **1.04** | 1.11 → **1.04** (1.02) | +2.4 |
| VWAP, no band, 30 s bias | 13.6 | 1.00 → **0.97** | 1.08 → **1.02** | 1.11 → **1.03** | +1.4 |
| VWAP, 0.5 ATR | 6.1 | 1.02 → **0.99** | 1.09 → **1.02** | 1.10 → **1.02** | +2.4 |
| VWAP, 1 ATR | 4.6 | 1.03 → **0.99** | 1.12 → **1.02** | 1.12 → **1.01** | +2.4 |
| VWAP, 2σ | 4.1 | 1.05 → **1.00** (0.83) | 1.13 → **1.06** | 1.12 → **1.05** (1.04) | +6.1 |

**By year, the VWAP variants on equity indices:**

| year | NQ, no band | NQ, 2σ | ES, 2σ | MES, 2σ |
|---|---|---|---|---|
| 2015 | 1.08 | 1.02 | — | — |
| 2016 | 1.06 | 1.04 | — | — |
| 2017 | 1.08 | 1.11 | — | — |
| 2018 | 1.21 | 1.31 | — | — |
| 2019 | 1.09 | 1.12 | — | 1.02 |
| 2020 | 1.16 | 1.09 | 1.04 | 1.14 |
| 2021 | 1.10 | 1.09 | 1.19 | 1.21 |
| 2022 | 1.09 | 1.13 | 1.14 | 1.16 |
| 2023 | 1.01 | 1.03 | 1.00 | 0.96 |
| 2024 | 1.08 | 1.21 | 1.19 | 1.19 |
| 2025 | 1.05 | 0.97 | 1.06 | 1.07 |
| 2026 (partial) | 0.99 | 0.99 | 0.94 | 1.02 |

**Findings:**
- **An absolute band does not rescue the key level.** All roots stay at ~1.00. The NQ / equity cells at 1.05–1.09 are
  holdout-only (fit 1.00–1.02), and the 2 ATR band fails.
- **The session VWAP is the first level that holds between fit and holdout, but only on equity indices.** Every VWAP
  variant gives equity 1.08–1.13 on the fit and 1.02–1.06 on the holdout. Every other asset class is ~1.0, so the
  all-roots cell sits at ~0.98–1.00.
- **The edge is steady.** NQ is above 1 in 10 of the 11 full years under both rules. ES and MES agree year by year from
  2020, the weak 2023 included.
- **It is thin per trade.** NQ makes +2.4 to +6.1 ticks a trip, and net of one tick NQ is left at PF 1.02–1.04.

That's the paper's finding in futures form: trend-following around the VWAP works on the Nasdaq, but the edge is small
and has narrowed since 2023.

## LV-5 — the baseline: long at the open, short when it fails (user, 2026-11-03)

User: "compare the trend following VWAP system on NQ with just buying at the open and staying long if the open holds,
otherwise going short ... use that as a baseline"; then "buying at the open, holding 10-30m and only flipping then if
the open fails to hold".

**The flags:**
- `--level open`: the level is the session's first bar's vwap.
- `--open-long`: a LONG from the first bar. The warm-up (`--warm-sec`) then only delays the first reversal: the hold.
- The regression: the new code at LV-4's VWAP no-band settings reproduces its 26,407 NQ trips exactly.
- Runs: `data/futures/levels/run_nq_*` and `run_open_w{300,600,1200,1800}_{t,s}` (NQ, ES, MES; 1 min bias; `t` = band
  0, `s` = 2σ).

**NQ, the open rule against the VWAP rules** (PF raw fit → holdout; net of a tick per trip; daily figures holdout, net):

| rule | trips/day | PF raw fit → holdout | −1 tick | ticks / trip | bp / day fit → holdout | daily Sharpe |
|---|---|---|---|---|---|---|
| VWAP, no band | 9.1 | 1.11 → 1.04 | 1.02 | +2.4 | 7.5 → 2.5 | 0.22 |
| VWAP, 2σ | 3.7 | 1.12 → 1.05 | 1.04 | +6.1 | 6.4 → 2.8 | 0.39 |
| open, no band (5 min warm-up) | 5.5 | 1.04 → 1.11 | 1.09 | +8.6 | 2.2 → 5.8 | 0.84 |
| open, 2σ | 2.7 | 1.04 → 1.18 | 1.17 | +25.7 | 2.0 → 8.2 | 1.30 |
| open-long, no band | 6.0 | 1.04 → 1.12 | 1.11 | +9.0 | 2.4 → 6.9 | 0.98 |
| open-long, 2σ | 3.2 | 1.04 → 1.18 | 1.17 | +22.8 | 2.3 → 8.8 | 1.36 |

**Holding the open's long before the first flip allowed** (PF raw fit → holdout, net of a tick):

| hold | NQ, no band | NQ, 2σ | ES, no band | ES, 2σ | MES, 2σ |
|---|---|---|---|---|---|
| 5 min | 1.04 → 1.12 (1.11) | 1.04 → 1.18 (1.17) | 1.04 → 1.09 (1.02) | 1.02 → 1.13 (1.09) | 0.99 → 1.12 (1.08) |
| 10 min | 1.03 → 1.14 (1.12) | 1.02 → 1.20 (1.19) | 1.07 → 1.10 (1.03) | 1.03 → 1.14 (1.09) | 0.97 → 1.12 (1.08) |
| 20 min | 1.02 → 1.15 (1.13) | 1.03 → 1.19 (1.18) | 1.10 → 1.11 (1.04) | 1.08 → 1.14 (1.10) | 0.99 → 1.14 (1.10) |
| 30 min | 1.05 → 1.13 (1.11) | 1.07 → 1.15 (1.14) | 1.12 → 1.13 (1.06) | 1.10 → 1.15 (1.11) | 1.04 → 1.13 (1.10) |

**By year, the 30 min hold** (PF raw):

| year | NQ 2σ | NQ no band | ES 2σ | ES no band |
|---|---|---|---|---|
| 2015 | 0.98 | 0.98 | — | — |
| 2016 | 0.88 | 0.90 | — | — |
| 2017 | 0.93 | 0.91 | — | — |
| 2018 | 1.28 | 1.30 | — | — |
| 2019 | 1.05 | 1.05 | — | — |
| 2020 | 1.05 | 1.01 | 1.17 | 1.17 |
| 2021 | 1.11 | 1.11 | 1.08 | 1.11 |
| 2022 | 1.14 | 1.08 | 1.08 | 1.11 |
| 2023 | 1.15 | 1.19 | 1.16 | 1.12 |
| 2024 | 1.05 | 1.01 | 0.99 | 1.01 |
| 2025 | 1.23 | 1.19 | 1.29 | 1.28 |
| 2026 | 1.21 | 1.11 | 1.10 | 0.97 |

The 30 min hold with 2σ, per day net of a tick: NQ +2.8 → **+7.0 bp** (Sharpe 0.41 → 1.12), ES +3.3 → +4.0
(0.54 → 0.82), MES +0.1 → +3.6.

**Findings:**
- **The trivial open baseline is as good as the VWAP rules.** Over 2015–26 both are PF ~1.05–1.08. They swap places
  between the periods: the VWAP leads 2015–22 (1.11–1.12 against 1.04), the open since 2023 (1.11–1.18 against
  1.04–1.05).
- **What's there is generic intraday trend persistence on the equity index futures, not the VWAP.** The VWAP is the
  steadier by year (> 1 in 11 of 11 full years); the open is lumpier (it loses 2016, 2017, 2020).
- **A 20–30 min hold before the first flip makes the two periods agree best.** ES is 1.10–1.12 → 1.13–1.15.
- **The open rule's NQ edge rests on 2018 onward.** It loses 2015–17.
- **The scale:** NQ's +7 bp a day is about \$280 per NQ contract at ~20,000 points (\$28 per MNQ). Costs are small
  next to it: a tick is 0.125 bp on NQ.
