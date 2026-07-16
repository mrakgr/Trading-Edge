# The Lookahead Protocol

**Written 2026-07-16, the day a single universe filter destroyed three systems.** This is the checklist
that would have caught it in an afternoon instead of a year.

---

## 0. What happened (read this first — it is the whole argument)

`scripts/equity/build_vwap_reclaim_candidate.fsx` filtered the tradable universe on:

```sql
WHERE avgvol20 * day_close >= 30000000.0   -- "ADV >= $30M". Looks like plumbing.
```

**Both factors are unknowable at the 10:00 entry:**
- `day_close` — day D's *closing* price.
- `avgvol20` — computed `ROWS BETWEEN 19 PRECEDING AND CURRENT ROW`, i.e. it **includes day D's own
  full-session volume**.

Nobody audited it because it read as a liquidity floor, not a signal. It was in fact a **backdoor
"today is a 12×-volume day" selector**: a volume spike on D inflates D's *own* 20-day average, pushing the
name over the $30M floor — so the universe admitted it *because of what happened that day*.

| | |
|---|---|
| names admitted **only** by the lookahead | traded at a **median 12.7× (mean 190×)** their prior-20d volume |
| names admitted legitimately | traded at **0.98×** — an ordinary day |

**The damage** (2020-26, live-safe universe):

| system | trips | PF | net |
|---|---|---|---|
| VwapReclaimV3 | 16,788 → 15,567 | **1.501 → 0.964** | $1.48M → **−$92k** |
| OpeningDriverV2 | 1,028 → **473** | **4.112 → 0.728** | $2.39M → **−$104k** |
| DipRiderV4 | 1,608 → 717 | **2.876 → 1.158** | $1.39M → $53k |

**The 10% of trips the lookahead admitted carried 99.3% of all P&L.** The other 15,134 printed PF 1.00.

---

## 1. The rules

### R1 — Any filter touching day D's data is a SIGNAL GATE, whatever it is named

"Liquidity floor", "universe prune", "tradeability filter", "sanity check" — the name is irrelevant. If a
row is admitted or rejected using information from day D, **it is selecting on the outcome.** Audit it with
the same suspicion you would apply to an entry gate.

### R2 — Rolling averages: `CURRENT ROW` is a lookahead in any GATE

```sql
-- LOOKAHEAD as a gate (includes D's own volume):
AVG(adj_volume) OVER (... ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS avgvol20
-- LIVE-SAFE (ends at D-1):
AVG(adj_volume) OVER (... ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avgvol20_prior
```

**Both are legitimate — for different jobs.** `avgvol20` is the correct **rvol denominator** (it matches the
engine's `AvgMa(20)` and every published rvol number). It is a **lookahead the moment you gate on it.**
`mr_candidate` now carries both; use `_prior` for every gate.

### R3 — The knowability clock

For every field in a filter, write down **the earliest minute it is fully determined**, and compare it to
`EntryStartMin`:

| field | knowable at | legal for a 10:00 entry? |
|---|---|---|
| `prev_adj_close`, `close_3d`, `avgvol20_prior` | pre-open | ✅ |
| `day_open`, gap | 09:30 | ✅ |
| `med_bar_vol_0945`, `nbar_0945`, `vol_0945` | **09:45** | ✅ **iff `EntryStartMin >= 09:45`** |
| `day_close`, `avgvol20`, `rvol_0945`, `close_fwd_*` | **D's close or later** | ❌ **NEVER** |

`mr_candidate`'s `median(1m vol 09:30-09:45) >= 10k` prune is legal **only** because MaxFlyerV3 and
LowFlyer both set `EntryStartMin = 09:45` — the prune and the first entry are aligned *to the minute*.
That alignment is load-bearing. **Lower `EntryStartMin` below 09:45 and it silently becomes a lookahead.**

### R4 — ⭐ THE DISPROPORTION TEST (this one needs no backtest, and it is the cheapest tell you have)

**A liquidity filter cannot move PF by more than roughly the fraction of the universe it changes.**

| | universe change | PF impact | verdict |
|---|---|---|---|
| VwapReclaimV3 ADV @ **$30M** | 0.8% | **−26%** | 🚩 **impossible for a real floor** |
| LowFlyer ADV @ **$500k** | 0.2% | +0.4% | ✅ plumbing, as advertised |

**A 0.8% membership change carrying $1.17M is arithmetically absurd.** That single line, written down, was
enough to diagnose this — no run required. **If a "plumbing" filter is load-bearing, it is not plumbing.**

### R5 — The threshold matters more than the formula

LowFlyer's production book uses the **exact same contaminated formula** (`avgvol20 * day_close`) and is
**completely clean**: 2 trips of 1,122 change, PF +0.014.

The difference is **where the threshold sits**. At **$500k** the floor is too loose for D-vs-D-1 to move
membership. At **$30M** it sits exactly where volume-spike inflation flips names in and out. **A lookahead
is only dangerous where it is doing selection work.** So: don't just grep for the formula — ask whether the
threshold is near the contamination's magnitude.

### R6 — The control: a genuine system is INDIFFERENT to removing a lookahead

Always audit a system you *believe* is clean, using the same method, as a control. Removing a lookahead:
- **genuine system** → nothing happens, or it **improves** (MaxFlyerV3's $1 price floor: PF 3.767 →
  **4.162**, because it was admitting sub-$1-on-D-1 names that had already squeezed — terrible shorts).
- **lookahead-dependent system** → **collapses**.

Without a control you cannot distinguish "the system is fake" from "my audit is broken."

### R7 — Fix the CROSS, not the FEATURES (isolate what you change)

When testing a change to a reference series (VWAP, a baseline, a denominator), remember that **anything
measured relative to it inherits the change.** Shifting VWAP by −20bps rescaled `run_max_dist` (avg
**3.58% → 1.01%**) and 7×'d the trip count — measuring "the gate now means something different", not the
thing under test. Split the knob (`VwapOffset` vs `VwapOffsetFeatures`) so the decision moves and the
measurement does not.

### R8 — Sweep the whole range before concluding a direction

Three monotone points are **not** a gradient — they can be one shoulder of a hump. The 09:00/09:15/09:30
VWAP-anchor readings looked like a clean dose-response and produced a confident, **wrong** conclusion. The
full 04:00→09:30 sweep showed a *hump*, and a 5-min neighbourhood sweep showed a smooth hill.

---

## 2. The audit procedure (run this on every system, and on every new one)

1. **Enumerate every filter** between raw data and a trip: candidate-table SQL, engine gates, and
   **post-hoc SQL selections** (LowFlyer's production book lives entirely in `.sql` — the contamination
   was *there*, not in the engine).
2. **Apply the knowability clock (R3)** to every field in every one.
3. **Apply the disproportion test (R4)** — free, and it fingered this in one line.
4. **Build the live-safe twin universe** and A/B it. Isolate each lookahead (fix one at a time): both of
   ours were *independently* load-bearing (fix-close-only → PF 1.107, fix-vol-only → 1.173, both → 0.964).
5. **Split the trips** into "kept" vs "dropped by the fix" and PF each group. This is the money shot:
   dropped = **PF 4.05 / +$1.47M**; kept = **PF 1.00 / +$10k**.
6. **Run a control (R6).**

---

## 3. When the edge is real but unselectable — the hardest lesson

**The VwapReclaim edge was NOT fabricated.** 89% of the golden trips survive in the honest universe and
still print **PF 3.19 / +6.04%/trade**. They are simply drowned by 60,294 neighbours at PF 0.87.

The edge exists. **It cannot be selected**, because the discriminator is unavailable:

| feature | GOLDEN | rest | separation |
|---|---|---|---|
| **D's whole-session volume ÷ prior-20d** (ILLEGAL) | **10.36×** | 1.07× | **9.7×** |
| pre-open rvol through 09:45 ÷ prior-20d (legal) | 7.32× | 4.93× | **1.5×** |

Every legal selector tried — ADV floor (30× range), ADV bucket (500× range), gap-up, gap+ADV band, honest
rvol — lands at **PF 0.86–0.99**. The same ~1.5× wall reappeared in MaxFlyerV3's `brv20d` audit (best legal
separator 1.68×).

**Diagnostic:** *tightening an honest filter makes things WORSE* (0.98 → 0.88). That is the signature of a
**weak proxy**: it discards good and bad at nearly the same rate, paying variance without buying selection.
A real lever improves PF as it tightens.

**"The strategy needs to know something it cannot know at entry time" is a valid, final conclusion.** The
knowledge was the edge — not the signal.

---

## 4. Honest levers found (the salvage)

Not everything died. Legal, monotone, and worth keeping:

- **`brv15m`** = `bar_vol / (D's own 09:30-09:45 mean 1m volume)`. The lookahead-free twin of `brv20d`:
  same concept, but the baseline completes at 09:45 = `EntryStartMin`. Monotone on MaxFlyerV3:
  1.24 → 1.30 → 1.44 → 1.62 → **2.11** → 2.36. Recovers ~60% of the PF at ~15% of the capacity.
- **New session 1m-vol high** (`vol_vs_high >= 1.0`): PF **1.73** vs 1.09–1.34. ⚠️ **`--vol-high-frac` is
  NOT enforcing on the arm path** — the book contains bars at 0.22 of the session high with the gate at
  0.90. **Bug; fix before trusting any vol-high result.**

---

## 5. Status board (2026-07-16)

| system | verdict |
|---|---|
| **LowFlyer** | ✅ **CLEAN** — production PF **3.329** honest (was 3.315); the only fully-cleared system |
| **MaxFlyerV3** | ⚠️ **UNCONFIRMED** — survives the $1 floor (3.767 → **4.162**) but **NOT** the `brv20d` denominator (3.758 → 1.301, and no honest threshold recovers it across a 12× sweep) |
| **VwapReclaimV3** | ❌ **DEAD** — 1.501 → 0.964 |
| **OpeningDriverV2** | ❌ **DEAD** — 4.112 → 0.728 |
| **DipRiderV4** | ❌ **DEAD** — 2.876 → 1.158 |
| VwapReclaim V1/V2, DipRider/V3(+Backside), BreakoutTimer(+Backside), OpeningDriver V1 | ❌ same contaminated table, unmeasured |

**`docs/systems_showcase.md` (branch `research_summary_july_2026`) quotes numbers from all three dead
systems. It must not go in front of anyone until rebuilt.**

---

## 6. The one-line summary

> **A universe filter that touches day D's own data is a signal gate wearing plumbing's clothes.**
> If a "liquidity floor" is load-bearing, it was never a liquidity floor.
