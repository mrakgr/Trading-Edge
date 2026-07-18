# DipRiderV5 — results

`TradingEdge.DipRiderV5` (branch `diprider-v5-scalp`). **The live-safe rebuild of DipRiderV4**, forked
2026-07-17, the day after the lookahead audit killed V4 (PF 2.876 → 1.158 honest).

**Read `docs/lookahead_protocol.md` first.** V5 exists because V4's universe filter
(`avgvol20 * day_close >= $30M`) was a backdoor "today is a 12×-volume day" selector. This document
starts from the assumption that **every V4 number and every V4-fitted threshold is void** until
re-measured on the honest universe.

**Standing conventions:** LONG intraday momentum. Period **2020-01-01 → 2026-06-30** (all numbers below,
unless stated). Raw MOC PF unless a clip is named. Notional $10k/trip. `mr_candidate` → the V5-only
`diprider_v5_candidate` universe.

---

## The V5 delta vs V4 — what changed and why

| # | change | why |
|---|---|---|
| 1 | **universe: `avgvol20 * day_close >= $30M` → `dv_0945 >= $5M`** | the ADV floor was the lookahead that killed V4. `dv_0945` = D's OWN 09:30–09:45 dollar volume. |
| 2 | **rvol gate: `rvol_0945 > 1` → `rvol_0945_honest >= 1`** | same gate, honest denominator (`avgvol20_prior`, not `avgvol20`). |
| 3 | **exhaustion cut: `permin20d` → `permin15m` denominator** | `permin20d = avgvol20/390` contained D's own session volume. |
| 4 | **NEW: vol-stop scalp exit** | ported from BreakoutTimer F14, on an **average** basis (warmup-safe). |

### Why `dv_0945` is structurally different, not just a re-tuned threshold

The point is **not** that $5M ≠ $30M. It is that `dv_0945` reads a **FIXED, CLOSED window**. A rolling
average ending at `CURRENT ROW` can always swallow D's own outcome; a window that ends at 09:45 cannot.

⚠ **Legal ONLY because `EntryStartMin = 10:00 >= 09:45`.** This alignment is load-bearing (protocol R3):
lower the entry window below 09:45 and `dv_0945` silently becomes a lookahead.

The old ADV floor also had the wrong *shape* for the job (user): it "would eliminate the biggest winners,
and loosening it would let all kinds of trash through" — it selected **stale daily liquidity**, not
**is this name in play THIS morning**.

---

## Finding 1 — the new universe filter is VERIFIABLY leak-free (the test V4's filter fails)

Applied the same test that exposed the ADV filter: for names each filter **admits** vs **rejects**, what
was D's OWN realized volume vs its prior-20d average? A legitimate filter is *blind* to this.

| filter | n admitted | median D-vol ÷ prior-20d | mean |
|---|---|---|---|
| baseline (all candidates) | 390,803 | 1.32× | 9.1 |
| **NEW `dv_0945 >= $10M` ADMITTED** | 242,479 | **1.32×** | 11.1 |
| **NEW `dv_0945 >= $10M` REJECTED** | 148,324 | **1.32×** | 5.8 |
| OLD `adv >= $30M` ADMITTED | 229,401 | 1.28× | 3.5 |
| OLD `adv >= $30M` REJECTED | 161,402 | 1.39× | 17.0 |

**The new filter admits and rejects names with IDENTICAL D-volume ratios (1.32 vs 1.32).** It cannot see
whether today was a volume spike — exactly the property the ADV floor lacked. (The old floor's *median*
looks innocent here only because this is the whole universe, not the marginal names it flips; the 12.7×
signature appears only among names admitted **solely by** the lookahead. This table is evidence the NEW
filter is clean, not a retraction of F14.)

**The honest rvol gate is near-inert — as the disproportion test (R4) predicts it should be:**

| | n |
|---|---|
| admitted by contaminated `rvol_0945 >= 1` | 97,631 |
| admitted by honest `rvol_0945_honest >= 1` | 101,240 |
| **disagreement (either-only)** | **4,548 = 1.2% of universe** |
| median ratio honest ÷ contaminated | **1.026** |

A ~1% membership change should move PF ~1%, not 26%. The asymmetry is also the right sign: `honest_only`
(4,078) ≫ `contam_only` (470) — the contaminated denominator is *larger* (it absorbs D's spike), so it
**suppresses** rvol and wrongly excludes genuinely active names. Fixing it **admits** names.

---

## Finding 2 — ⭐ THE HONEST BASELINE: PF 1.056 / 11,440 trips / +$371k

The first clean number DipRider has ever had. V4's A-book config, with only the two contaminated pieces
removed (ADV floor → `dv_0945 >= $5M`; exhaustion cut → OFF, pending re-tune).

| | value |
|---|---|
| candidates (ticker-days) | 86,054 |
| **trips** | **11,440** |
| win rate | 32.0% |
| net P&L | **+$371,417** |
| **PF (raw MOC)** | **1.056** |

**Per-year:**

| year | n | PF | net |
|---|---|---|---|
| 2020 | 1662 | 1.072 | +$65,814 |
| 2021 | 1923 | 1.080 | +$77,083 |
| 2022 | 1413 | **0.891** | −$84,867 |
| 2023 | 1571 | 0.986 | −$12,759 |
| 2024 | 2085 | 1.099 | +$124,645 |
| 2025 | 2024 | 1.103 | +$125,756 |
| 2026 | 762 | 1.145 | +$75,744 |

**Read:** barely above break-even, 2 losing years. But this is the **fat book with every quality lever
off** — it is a starting point, not a verdict. ⚠ **The trip count is the tell: 11,440 vs V4's A-book
1,860 (6×).** This is not a fat book by design; it is the same book **with the exhaustion cut missing**
(V4 F2/F4: that cut removed ~half the book for +0.35 PF). V5 is not yet apples-to-apples with V4.

---

## Finding 3 — ⭐ the EXHAUSTION CUT SURVIVES the denominator fix — monotone, and V4's threshold was a no-op

The V4 idea was right; only the measurement was contaminated. Re-based on `permin15m` (D's own 09:30–09:45
mean 1m volume — complete at 09:45, legal), PF declines **monotonically** as the 5m volume ratio rises:

| 5m-vol ÷ permin15m | n | win% | net | **PF** |
|---|---|---|---|---|
| **< 1** | **7732** | 31.9 | **+$446,106** | **1.106** |
| 1–2 | 2083 | 32.2 | +$31,047 | 1.024 |
| 2–4 | 973 | 32.7 | −$23,621 | 0.962 |
| 4–8 | 427 | 31.6 | −$39,113 | 0.855 |
| 8–16 | 162 | 27.8 | −$23,805 | 0.796 |
| ≥ 16 | 63 | 38.1 | −$19,198 | **0.625** |

**Six buckets, strictly monotone, spanning PF 1.106 → 0.625.** The exhaustion thesis is intact:
**entering into a volume blow-off is a late entry.**

**The scale moved by ~2 orders of magnitude.** Honest ratio distribution:

| p10 | p25 | p50 | p75 | p90 | p99 |
|---|---|---|---|---|---|
| 0.22 | 0.37 | **0.66** | 1.24 | 2.59 | 11.43 |

**V4's `MaxRvol5m20d = 100` would be a NO-OP here** — nothing reaches it. The live threshold is near
**1–2**. This is R5 in action (*the threshold matters more than the formula*), and it is why V4's config
could not simply be carried across.

⏭ **Sweep `--max-rvol-5m-15m` ∈ {0.5, 0.75, 1.0, 1.5, 2.0}.** Parked by the user for later.

---

## Finding 4 — 💀 vol_climb is INVERTED on the honest universe: tightening the floor MONOTONICALLY HURTS

**The single most important negative result of the day.** On V4 the per-window `vol_climb` floors were the
backbone of the entire A/A+/A++/S ladder — F14 called differentiated per-window vc floors *"DOMINATE the
frontier"*. On the honest universe **the dial runs backwards.**

20m-EMA-high breakout ONLY (session + 60m timers OFF), sweeping the 20m vc floor at `vc = n/(n+1)`:

| n | vc floor | trips | win% | net | **PF** |
|---|---|---|---|---|---|
| **control** | **0.0** | **10,639** | 29.8 | **+$454,707** | **1.076** |
| 0.5 | 0.333 | 9,365 | 30.5 | +$334,694 | 1.061 |
| 1 | 0.500 | 7,575 | 30.6 | +$219,371 | 1.048 |
| 2 | 0.667 | 4,919 | 31.2 | −$30,772 | 0.990 |
| 3 | 0.750 | 3,410 | 31.6 | −$62,817 | 0.971 |
| 4 | 0.800 | 2,526 | 31.7 | −$116,634 | **0.928** |

**Strictly monotone DOWNWARD. Every increment is worse; past n=2 the book goes negative.**

**This is the weak-proxy signature from the lookahead protocol §3, verbatim:**

> *Tightening an honest filter makes things WORSE. That is the signature of a weak proxy: it discards good
> and bad at nearly the same rate, paying variance without buying selection. A real lever improves PF as it
> tightens.*

The win rate confirms it: **29.8% → 31.7% (+1.9pt) while trips drop 4.2×.** It is discarding good and bad
almost indiscriminately.

**Diagnosis — why V4 believed the opposite.** `vol_climb` measures *"is volume surging RIGHT NOW."* On the
contaminated universe that correlated with the ADV filter's smuggled-in signal — *today is a 12×-volume
day*. **vol_climb was substantially reading the leak.** Strip the leak and the correlation goes with it.

**⭐ The two volume levers now point in OPPOSITE directions, and the honest one wins:**

| lever | what it says | honest verdict |
|---|---|---|
| `vol_climb` floor (V4's backbone) | high volume at entry = GOOD | ❌ **inverted — hurts monotonically** |
| exhaustion cut (F3) | high volume at entry = **BAD** | ✅ **survives — monotone, PF 1.106 → 0.625** |

Both are volume-tempo features at entry. **The honest data says LOW volume at entry is better** — the
reverse of V4's core belief. V4 held the opposite because the lookahead had pre-selected the winners.
(Inversions are a known pattern in this book: V4 F19 found `dist/ATR` inverts vs VwapReclaimV3.)

**Default: `vc = 0` (floors OFF).** Do not restore V4's `session@0 ∥ 60m@⅓ ∥ 20m@½` ladder — it is void.

---

## Finding 5 — the vol-stop SCALP EXIT: built, verified firing, but the 9-EMA basis is too fast

Ported from **BreakoutTimer F14** (`--vol-stop-frac`), not DipRiderV2 — the mechanism the user remembered
("exit if the 9-EMA of volume fell to ⅔ × the 9-EMA of volume at entry") exists there and nowhere else.
Every `2/3` in the DipRider docs is *stop geometry* (`stop = entry − d·2/3`), a different thing.

**It is lookahead-free:** both sides of the ratio are D's own realized bars — no `avgvol20` anywhere.

**⚠ Built on an AVERAGE, not BreakoutTimer's raw 20-bar SUM (user).** A raw sum is **warmup-dependent**: a
sparse name reaching 10:00 with <20 bars folded (no trades ⇒ no bar) gets an artificially SMALL basis, so
the ratio against it is garbage and the exit misfires. `AvgMa.State` divides by the **live bar Count** and
`EmaMa` self-normalizes — both warmup-safe. Two bases wired: `volEma` (9-EMA, default) and
`vol20avg` (`AvgMa(20)`, via `--vol-stop-use-avg20`).

**Verified ENFORCING** (the `--vol-high-frac` lesson: a gate that silently no-ops is worse than no gate).
Q1-2024 smoke test, `--vol-stop-frac 0.667`:

| exit_reason | n | avg hold (min) | med hold | avg % |
|---|---|---|---|---|
| **vol_stop** | **408** | 21.3 | **12.0** | −0.39 |
| stop | 16 | 24.1 | 20.0 | −10.62 |
| moc | 2 | 188.0 | 188.0 | +20.58 |

**96% of trips, median hold 12 minutes** — it fires. But **too fast**: BreakoutTimer F14 fired on 89% at a
45-min median. **The 9-EMA basis is the problem** — entry happens *at* a volume climax by construction, so
the fast EMA sits at a local peak at entry and mechanically decays below ⅔ within a few bars. It is a
near-immediate **time-exit in disguise**, not a disinterest detector. (F14 diagnosed the same failure mode
on its own slower basis; the 9-EMA makes it worse.)

**Q1-2024 A/B (426 trips — far too small to conclude from; recorded only to show the plumbing works):**

| config | trips | win% | net | PF |
|---|---|---|---|---|
| baseline hold-to-MOC | 426 | 32.6 | −$612 | 0.998 |
| vol-stop 0.667, 9-EMA basis | 426 | 26.5 | −$28,595 | 0.757 |
| vol-stop 0.667, 20m-avg basis | 426 | 30.5 | −$31,405 | 0.798 |

⏭ **Not yet tested on the full 2020+ book.** Default `--vol-stop-frac 0` (OFF) so the baseline stays
hold-to-MOC and the A/B is clean.

**Why it is still worth testing despite F14's "not a strict improvement" verdict:** F14 rejected it because
it *caps the fat tail* (net $302k → $262k). But **V4's tail was largely the LOOKAHEAD's doing** — the
golden trips the ADV filter smuggled in. If the tail was fake, an exit that trades tail for PF gives up
little. That is a real reason to expect a different answer, not a hope.

---

## Finding 6 — ⭐ MEAN-REVERSION MODE: buy the 20m low above VWAP, sell into the 20m high — PF 1.285, ALL-WEATHER, from a NEAR-BARE config

User's idea (2026-07-17): *"turn DipRiderV5 into a mean reversion system that buys 20m lows above the VWAP
and sells above the 20m highs"* — a short-term fade using V5's live-safe universe.

**The engine (`--mean-reversion`):**

| | |
|---|---|
| **ENTRY** | close ≤ the **strictly-prior** 20m MIN of 1m-bar **CLOSES**, AND close > VWAP |
| **EXIT** | close ≥ the **strictly-prior** 20m MAX of closes (`target`), or MOC |
| gates kept | log-ATR ≥ 0.013, entry window 10:00–13:30, `dv_0945 ≥ $5M`, `rvol_0945_honest ≥ 1` |
| gates DROPPED | arm/re-arm, all 3 breakout timers, vc floors, price-slope, sum6, chg1d, chg3d, ema-vs-vwap, stop-dist |

**CLOSES, not high/low wicks (user):** a wick low is noise a limit may never trade at; a close is a price the
tape printed and held. ⚠ Both windows are read **strictly-prior** — if the current close were inside its own
window, `close <= 20m min` would be trivially true **on every bar**.

**Why the momentum machinery had to go:** the two triggers are mutually exclusive. The arm/re-arm re-arms
when the 9-EMA breaks a 20m **low** — the MR entry condition itself. The breakout timers fire on new
**highs**. The stop-distance floor gates on distance *above* the 20m-EMA-low, which MR **buys into**.

### ⭐ THE RESULT — and why `MaxConcurrent` is the whole story (user caught this)

`MaxConcurrent = 0` (unlimited) is the inherited V4 default. In V4 the arm/re-arm cycle throttled entries;
**MR has no arm/re-arm, so with no slot cap every consecutive new-20m-low bar opens ANOTHER position.**
The trips are then heavily overlapping — not independent bets — and required capital is unbounded.

| config | trips | win% | net | **avg %/tr** | med hold | **PF** |
|---|---|---|---|---|---|---|
| mc=0 unlimited (**misleading**) | 38,103 | 65.0 | $2,551,168 | 0.670 | 28m | 1.429 |
| **mc=1 (HONEST)** | **14,440** | **62.5** | **$657,752** | **0.456** | 29m | **1.285** |

**62% of the trips were stacked duplicates, and they flattered everything.** PF 1.429 → **1.285**,
avg/trade 0.670% → **0.456%**. ⚠ **Never report an MR number at mc=0.**

**Per-year (mc=1) — positive every year, but the edge is NOT evenly spread:**

| year | n | win% | PF | net | avg %/tr |
|---|---|---|---|---|---|
| 2020 | 2187 | 57.7 | **1.077** | +$26,242 | **0.120** |
| 2021 | 2566 | 58.2 | **1.077** | +$31,221 | **0.122** |
| 2022 | 1909 | 63.4 | 1.418 | +$110,938 | 0.581 |
| 2023 | 1936 | 64.5 | 1.400 | +$118,738 | 0.613 |
| 2024 | 2692 | 66.5 | **1.510** | +$211,493 | 0.786 |
| 2025 | 2430 | 64.4 | 1.297 | +$129,898 | 0.535 |
| 2026 | 720 | 63.9 | 1.194 | +$29,223 | 0.406 |

**⚠ 2020/2021 are net LOSERS after costs** (+0.12%/tr is below round-trip cost). The edge is concentrated
in **2022–2026**. Contrast the momentum book, which lost money in 2022 **and** 2023 — the two systems are
strong in opposite regimes.

### Cost sensitivity (user's question)

| metric | value |
|---|---|
| avg %/trade (mc=1) | **+0.456%** |
| median %/trade | +1.50% (mc=0 book) |
| avg WIN | +3.43% |
| avg LOSS | −4.46% |
| win rate | 62.5% |
| median hold | 29 min |
| p01 / p05 | **−17.79%** / −8.21% |

Entries and exits are **at bar closes on liquid names** (`dv_0945 ≥ $5M`), so spread ≈ 2–5bps/side ⇒
**~0.1% round trip**, plus commissions (~$2–10 on a $10k clip). **+0.456% gross → ~0.33–0.35% net: costs
eat ~25–30% of the edge.** Viable, but thin — and materially worse than the mc=0 book implied.

**The classic MR signature, internally consistent:** win small and often (62.5% @ +3.4%), lose rarely but
bigger (−4.5%). **The p01 of −17.8% is the tail risk — there is NO STOP**, so a name that just keeps
falling runs to MOC.

### The ablations — the VWAP condition is REAL, the stop is DESTRUCTIVE

(2020+, mc=0 — ⚠ these were run before the concurrency fix; the *ordering* holds but the levels are inflated.)

| config | trips | win% | net | avg %/tr | PF |
|---|---|---|---|---|---|
| **MR bare (above-VWAP ON)** | 38,103 | 65.0 | $2,551,168 | 0.670 | **1.429** |
| MR, **above-VWAP OFF** | 318,281 | 65.4 | $17,862,879 | 0.561 | **1.319** |
| MR, **9-EMA stop ON** | 38,103 | 47.7 | $436,585 | 0.115 | **1.164** |
| MR, ATR floor OFF | 435,126 | 65.6 | $8,071,808 | — | 1.350 |

- **⭐ `close > VWAP` is LOAD-BEARING** — dropping it costs PF 1.429 → 1.319 and avg/trade 0.670% → 0.561%
  across **8× the trips**. It selects **quality**, not just volume. **This is the OPPOSITE of the
  weak-proxy signature** that killed `vol_climb` (F4): a real lever improves PF as it tightens, and this one
  does. **The user's instinct to require above-VWAP was right.**
- **The 9-EMA stop is DESTRUCTIVE** — PF 1.429 → 1.164, win 65% → 47.7%, avg/trade 0.670% → **0.115%**.
  Exactly as predicted: it arms off the 20m-EMA-**low**, which is what MR buys **into**, so it fires at or
  near the entry bar. **Default OFF is correct** (`--mr-use-stop` to re-test).
- **The ATR floor does far less work here than in momentum** (1.350 → 1.429 for an 11× trip cut). The MR
  edge is **broad and structural**, not selected — encouraging for realness, but it means the ATR floor is
  a capacity dial, not the source of the edge.

### ⚠ Known data defect (cosmetic, NOT load-bearing)

6,002 of 38,103 rows (16%) carry absurd `entry_price` (max **$409,302,000**) — split-adjustment blowup on
old pre-split data (same class as the `split_adjusted_prices` dividend bug). **It does not create the
finding — it DILUTES it:**

| group | n | net | PF | avg %/tr |
|---|---|---|---|---|
| px > $1000 (SUSPECT) | 6,002 | $289,027 | **1.295** | 0.482 |
| px ≤ $1000 (sane) | 32,101 | $2,262,140 | **1.456** | 0.705 |

The sane rows are **better** (1.456 vs 1.429 overall). Percentages are scale-invariant so the % figures
stand; only the absolute price display is nonsense. **Still worth fixing before any sizing work.**

### Verdict

**The simplest possible structure — buy the 20m low above VWAP, sell the 20m high — has a real,
live-safe, all-weather edge of PF 1.285 / +0.456%/trade, from a near-bare config with no fitted gates.**
That is a better honest starting point than the momentum book ever had (F2: PF 1.056), and it was reached
in one afternoon rather than 20 findings of tuning.

⏭ **What it needs before it is a system:**
1. **A real stop.** p01 = −17.8% with no stop is the headline risk. The 9-EMA stop is structurally wrong;
   MR wants a stop *below* the entry (a % stop or a session-low break), not an EMA that sits above it.
2. **2020/2021 are cost-negative.** Find what separates them (regime? the ATR floor? price tier?) or accept
   the system as 2022+ only.
3. **Add the gates back ONE AT A TIME** (chg1d/chg3d/ema-vs-vwap were all bypassed here) — but expect
   inversions: they were fitted for **momentum**, and F4 already proved one V4 "dominant lever" inverts.
4. **Fix the split-adjustment blowup** before any position sizing.
5. **The exhaustion cut (F3) is untested here** — it says LOW volume at entry is better, which is a
   *mean-reversion* thesis. It may well be a stronger fit for THIS book than for momentum.

---

## Status / next

**Where V5 stands:** the honest baseline is **PF 1.056**. One real lever found (**exhaustion cut**, F3),
one V4 cornerstone destroyed (**vol_climb**, F4), one exit built and verified but untuned (F5).

⏭ **The live threads, in the order they matter:**

1. **Sweep the exhaustion cut** `--max-rvol-5m-15m` ∈ {0.5, 0.75, 1.0, 1.5, 2.0}. The only surviving
   volume lever; monotone; the honest scale puts the knee near 1–2. **(parked by user, 2026-07-17)**
2. **Test `vol_climb` as a CEILING** — F4 says the edge is in the LOW-vc trips, which mirrors F3. The two
   may be the same underlying effect measured twice; if so, one of them is redundant.
3. **Re-check the breakout structure itself.** all-3-OR + V4 vc floors = PF 1.056; 20m-only at vc=0 =
   **1.076**. Nearly identical — **does the breakout timer carry ANY honest edge?** V4's F9 claimed the
   breakout "adds a real premium", but that was measured on the leak. Test each window alone at vc=0 vs
   breakout OFF entirely.
4. **Vol-stop on the full book** + frac sweep, both bases.
5. **Everything V4-fitted is suspect.** `MinAtrPct = 0.013`, `MinChg1d = 0.10`, `MinChg3d = 0`,
   `MinEmaVsVwap = −0.02`, `MinStopDistPct = 0.03`, breakout window = 10 bars — **every one** was tuned
   against the contaminated universe. F4 proves at least one V4 "dominant lever" inverts. Re-tune from
   scratch; do not assume any of them port.
