# FlushFader — Live Sizing & Escalation Policy (DRAFT — NOT ADOPTED, 2026-08-06)

> **STATUS: DRAFT.** The user chose to sit on this pending a first month of live
> trading at small size. The user's provisional plan (preferred over §3–§4 below):
>
> - Month 1: trade small (1% D-base), see how it feels. Fills log from day one.
> - Ramp **1% → 10% D-base within ~6 months** (≈ +45%/month) if all goes to plan,
>   then **+10–20%/month**, gated on the month-end equity being at a **3-month high**
>   (not all-time — so a halving doesn't freeze the ramp for months).
> - **Cut (halve) + rethink the system at a month-end 3-month equity low.**
> - Principle: **trend-follow our own equity curve; change gradually.**
>
> Backtest calibration of those gates (per-tkd book at 1% D-base, 79 months):
> "new month-end high" fires **97% of months** (one negative month in 6.5y) and a
> month-end 3-month low fired **zero times** — so the increase gate filters almost
> nothing in a working system, and the 3m-low alarm, if it ever fires, means the
> system has left the model. The load-bearing choices are the CEILING and the CUT
> SIZE, not the increase condition. At 10% D-base the yardsticks scale to: A-trade
> ≈ 34% of account at ref vol, backtest-worst DD ≈ 13%, worst trade ≈ −4.5%.
>
> §1 (size formula), §5 (fills log + participation guard) and §6 (re-derivation at
> 1,000 live trades) apply regardless of which escalation variant is adopted.

**Purpose.** Pre-committed BEFORE any live results exist. The live-money version of
overfitting is ratcheting size up after a lucky month; this document is the ratchet
lock. Increases happen only by these rules; cuts happen automatically. Written at
S43az, calibrated on the per-ticker-day mc=1 book (6,385 trades, 2020→2026-07).

## 1. Position size formula

```
size(trade) = BASE × tier_mult × sqrt(99bp / volat_20m_bp)
tier_mult   = { A 2.44, B 1.80, C 1.14, D 1.00 }     (S43bi: PF−1 on the bottom-5%-trimmed book; Kelly RETIRED — it estimated the worst-trade order statistic, S43bh, 🔒)
BASE        = fraction of account for a D-tier trade at reference vol (99bp)
```

**TODO (user, 2026-08-07):** S-tier A trades become their OWN sizing class at
scale-up — sized up MORE than plain A. Small-sample today (38 trades), so they
ride as tier A until the §6 re-derivation, which should output five
multipliers {S, A, B, C, D}.

**BASE starts at 1.0%** (⇒ A ≈ 2.4% at reference vol). ⚠ The §2 yardsticks below are STALE (computed on the wider reference at older
multipliers). Current trading-book yardsticks at BASE = 1% with the S43bi
multipliers: **+5.65%/yr average, max DD −0.40%, worst trade −0.25% of account**
(book = **1,325 trades, ROSTER v3.0** on the causal `v44_causal` reference — S43br;
the pre-migration `v43_legtick` book was 1,318 @ +5.65% — 7 voices incl. `downticks_since_uptick ≥ 8`
(S43bp) at `secs ≤ 450` (S43bj); the 6-voice book gave +5.39%/−0.40%, and the
older 1,312-trade `secs ≤ 516` book +5.5%/−0.42%). Execution: cross the entry,
rest the exit. mc = 1: one position per ticker-day; global concurrency per S43ay book.

## 2. Backtest yardsticks at BASE = 1% (what "normal" looks like)

| metric | value |
|---|---|
| trades/year | 573–1,292 (median ≈ 1,080; ≈ 80/month) |
| worst-ever drawdown (6.5y) | **−1.30% of account** (= −1.3 × BASE) |
| within-100-trade max DD: med / p90 / worst | −0.28% / −0.55% / −1.30% |
| 100-trade net return: p5 / median / % negative | +0.26% / +1.60% / **2%** |
| 250-trade net return: p5 / % negative | +2.08% / **0%** |
| worst single trade | −0.45% of account (rn −44.8%, tier D) |
| worst year (2022/2023) | ≈ +6.9% |
| pooled mean per trade (vol-normalised rn) | +1.17% (A 2.15 / B 1.57 / C 1.24 / D 1.05) |

All drawdown triggers below are expressed in **multiples of the CURRENT BASE**, so
they scale automatically when size changes.

## 3. De-escalation — automatic, immediate, no discretion

| trigger | backtest context | action |
|---|---|---|
| drawdown from HWM ≥ **2.0 × BASE** | 1.5× the worst DD ever seen in 6.5y | **halve BASE** immediately |
| drawdown from HWM ≥ **3.5 × BASE** | ~2.7× backtest worst — outside the model | **BASE → 0.25%** (observation size) + full research audit before any restore |
| any 250-trade stretch with **negative** net P&L | never happened in backtest (p5 +2.08%) | **BASE → 0.25%** + audit — presumption is the edge is impaired |
| single trade rn worse than **−50%** | worst observed is −44.8% | finish audit of that trade (halt? data? execution?) before taking the next signal |
| slippage erosion > 0.6%/trade (see §5) | half the pooled edge | freeze escalation; if persistent over 100 trades, halve BASE |

Cuts take effect on the next trade. A halved BASE re-enters the normal escalation
ladder (§4) — it does not restore automatically.

## 4. Escalation — only at scheduled reviews, one step, all conditions AND-ed

**Review cadence:** when BOTH ≥ 250 live trades at the current BASE **and** ≥ 8 weeks
since the last size change. (≈ quarterly at backtest frequency.)

**Ladder:** BASE steps ×1.5, rounded: **1.0% → 1.5% → 2.25% → 3.4% → 5.0% (ceiling)**.
The 5% ceiling (A ≈ 17%) stands until the re-derivation of §6 — it is ≈ 1/4 of the
disaster-anchored Kelly (f*_D = 0.25 at w = 1/134), not of the empirical corner.

**Conditions to take one step up (ALL must hold):**

1. ≥ 250 live trades at current BASE since the last change.
2. Realized pooled mean rn (slippage-adjusted, §5) **≥ +0.5%/trade** — roughly half
   the backtest's 1.17%, i.e. the edge survives overfit + costs at ≥ half strength.
3. No de-escalation trigger fired since the last review.
4. Current drawdown from HWM < 1.0 × BASE (never escalate mid-drawdown).
5. Measured slippage erosion ≤ 0.3%/trade vs the sim assumption.

If any condition fails: hold size, review again next cadence. There is no
"catch-up" — a skipped step is not taken retroactively.

## 5. The fills log (prerequisite for ANY escalation)

Per trade, from day one: sim-implied entry (cross at signal) and exit (rest) prices
vs actual fills; realized rn vs sim rn; the per-trade **erosion** = sim − realized.
No fills log ⇒ condition 4.5 is unmeasurable ⇒ no escalation, ever. This log is
also the input for §6.

**Participation guard:** per-trade notional ≤ 1% of the trailing 20m dollar volume
(universe floor is dv ≥ $2M ⇒ ≥ $20k headroom per trade). If BASE growth pushes
typical trades past this cap, the cap wins — the account has outgrown the D tier's
liquidity, revisit universe or accept sublinear sizing.

## 6. Re-derivation checkpoint

At **1,000 live trades**: recompute multipliers as **PF−1 on the bottom-5%-trimmed
distribution** (S43bi) with the live trades appended (weighted 1:1 with backtest),
and revisit the ceiling. ⚠ Do NOT use empirical Kelly — S43bh showed f* pins at
`1/|worst kept loss|` at any trim depth, so it estimates the sample's extreme
order statistic rather than the edge.
From that point live data is the authority; this document gets a v2. Until then the
multipliers {A 2.44, B 1.80, C 1.14, D 1.00} are frozen — no mid-flight tier re-tuning off live
results (232 A-trades took 6.5 backtest years; a live quarter cannot re-estimate them).

## 7. Amendment rule

This policy may be edited only (a) at a scheduled review, or (b) after a §3 audit —
never in the middle of a drawdown to avoid a cut, and never on the day of a big win.
Every amendment gets a dated changelog entry here.

---
*Changelog: v1 2026-08-06 — initial, S43az.*
