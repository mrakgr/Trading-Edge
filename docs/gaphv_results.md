# GapFader HIGH-VOLUME variants — results (from 2026-11-21)

The four gap shorts (GF-64's rules, `docs/gapfader_results.md`), each at dv60_med ≥ \$500k, open ≥ \$1, a full session,
rvol on the 60-session MEDIAN volume:

| variant | rule | entry |
|---|---|---|
| U0930 | gap-UP ≥ +2 %, rvol_pm ≥ 0.5 | short the official open |
| D0930 | gap-DOWN ≤ −2 %, rvol_pm ≥ 0.25 | short the official open (the follow) |
| U1000 | gap-UP ≥ +4 %, rvol30 ≥ 1 | short the 10:00 \$5k VWAP |
| D1000 | gap-DOWN ≤ −4 %, rvol30 ≥ 1 | short the 10:00 \$5k VWAP |

Every trade covers at the official close, gross, no borrow fee. **The research direction (user 2026-11-21): top-down** —
start from the biggest winners and losers of the existing setups and ask what would have caught them (and what would
have cut the losers without a stop: "Stops always seem to make things worse"); the variants hold all day, and the aim is
to trade them "with more skill and nuance" instead of sitting through a short that runs against us.

The event table: `scripts/equity/gaphv_events.py` → `data/equity/gaphv/events.parquet` (46,450 ticker-days, 2016-08 ..
2026-09; per event the first-30-minute tape — volat30, range30, ret30 — the 10:00 \$5k VWAP, marks at 10:30 .. 15:30, and
the 1s-vwap highs / lows with their times from 09:30 and from 10:00: the path, for the exit work).

## GH-1 — the biggest winners and losers by the first-30-minute volatility (2026-11-21, user: "whether big winners have the highest intraday volatility in the first 30m or whether they are moderate")

`scripts/equity/gaphv_winners.py` → `data/equity/gaphv/winners_volat30.md` (every decile table, plus the 15 biggest winners
and losers of each variant). volat30 = GapShort.fs's: the 30 s clock slots' vwaps 09:30–10:00, mean |ln(v_i / v_{i−1})|,
bp. Big winner / loser = the variant's own top / bottom 10 % of r. ⚠ For the 09:30 variants volat30 is NOT known at the
entry (it describes the trade's first 30 minutes); for the 10:00 variants it is known at the entry.

| variant | n | /yr | PF | mean | win % | p10 / p90 | PF 2016–20 / 2021–26 |
|---|---|---|---|---|---|---|---|
| U0930 | 11,812 | 1,173 | 1.42 | +1.75 % | 62 | −12.8 / +16.8 % | 1.61 / 1.37 |
| D0930 | 13,174 | 1,308 | 1.27 | +0.87 % | 54 | −10.1 / +12.5 % | 1.38 / 1.24 |
| U1000 | 22,814 | 2,265 | 1.05 | +0.17 % | 54 | −9.0 / +9.9 % | 1.00 / 1.08 |
| D1000 | 16,267 | 1,615 | 1.16 | +0.42 % | 54 | −7.7 / +8.8 % | 1.22 / 1.14 |

(U1000 here is every gap-up ≥ 4 % at rvol30 ≥ 1; GF-64's 1.14–1.26 was GF-10's rvol_pm ≥ 0.2 sample only.)

**By volat30 decile, the short's PF / big-winner % / big-loser % / the WINNER SHARE of the big moves:**

| decile | U0930 | D0930 | U1000 | D1000 |
|---|---|---|---|---|
| 1 (calmest) | 1.00 / 0 / 0 / 0 % | 0.88 / 0 / 0 / 25 % | 0.81 / 0 / 1 / 22 % | 1.39 / 0 / 1 / 27 % |
| 2 | 1.43 / 1 / 3 / 16 % | 1.21 / 1 / 2 / 22 % | 0.80 / 1 / 3 / 28 % | 0.92 / 1 / 3 / 31 % |
| 3 | 1.56 / 1 / 6 / 19 % | 1.21 / 3 / 4 / 37 % | 0.88 / 2 / 5 / 31 % | 1.23 / 3 / 4 / 44 % |
| 4 | **1.92** / 3 / 6 / 33 % | 1.25 / 4 / 7 / 37 % | 0.79 / 3 / 8 / 29 % | 1.15 / 4 / 7 / 40 % |
| 5 | 1.51 / 5 / 9 / 34 % | 1.03 / 7 / 10 / 41 % | 0.94 / 5 / 9 / 36 % | 1.03 / 7 / 9 / 44 % |
| 6 | 1.54 / 9 / 11 / 45 % | 1.26 / 10 / 10 / 48 % | 0.91 / 7 / 11 / 41 % | 1.03 / 10 / 11 / 48 % |
| 7 | **1.82** / 13 / 11 / 53 % | 1.25 / 12 / 13 / 48 % | 1.04 / 12 / 12 / 48 % | 1.15 / 12 / 14 / 48 % |
| 8 | 1.54 / 15 / 15 / 51 % | **1.44** / 16 / 14 / 52 % | 1.07 / 15 / 15 / 49 % | 1.08 / 14 / 15 / 49 % |
| 9 | 1.31 / 22 / 18 / 55 % | 1.34 / 22 / 19 / 54 % | 1.06 / 19 / 17 / 53 % | **1.31** / 20 / 17 / 54 % |
| 10 (wildest) | 1.09 / 31 / 22 / 59 % | 1.34 / 27 / 20 / 57 % | **1.46** / 35 / 19 / **65 %** | 1.28 / 27 / 20 / 58 % |

volat30 medians, big winners / big losers / all: U0930 94 / 81 / 58 bp · D0930 70 / 63 / 45 · U1000 83 / 67 / 51 · D1000
69 / 61 / 45.

Reading:
- **The biggest winners DO sit in the highest volatility — in every variant.** The big-winner rate climbs from 0 % in the
  calmest decile to 27–35 % in the wildest; the big winners' median volat30 is the highest of any group.
- **But the biggest LOSERS are there too** (0 % → 19–22 %): volatility mostly sets the SIZE of the move. What it adds on
  top is the DIRECTION: the winners' share of the big moves rises monotonically, from ~20 % in the calm deciles to 57–65 %
  in the wildest, on all four variants. Calm names (decile 1, a \$23–35 median price) barely move at all.
- **U0930 is the exception in PF: its best deciles are the MODERATE ones (4–8, PF 1.5–1.9); the wildest decile is 1.09.**
  The split at 10:00 says why: in decile 10 the open → 10:00 leg LOSES −2.82 % on average (the stock squeezes first),
  then 10:00 → close makes +3.50 % (PF 1.47). The wild gap-ups are winners, but only after they have run against the
  short. That is the same shape as U1000's top decile (PF 1.46, +2.78 %, 65 % winner share). ⚠ GH-2: this is NOT
  knowable at the open — a wild open predicted from the premarket is a BETTER open short (PF 1.68), not a worse one; the
  decile-10 loss is the realized squeeze itself (it is what makes volat30 high).
- For the 10:00 variants volat30 is KNOWN at the entry: U1000 is flat-to-losing in deciles 1–9 (0.79–1.07) and works only
  at volat30 ≥ ~100 bp (1.46 in both halves). D1000 is flatter (1.03–1.39, best in deciles 9–10).
- volat30 is tied to the price: the calm deciles are the \$20–35 names, deciles 3–10 of the 09:30 variants all sit at
  \$4–5. A price control comes before any gate.

## GH-2 — the REVERSE analysis: what the big winners looked like at the decision (2026-11-21, user: "what is their relative volume, intensity and so on? Let's borrow some features from the Snoozer systems")

`scripts/equity/gaphv_features.py` → `data/equity/gaphv/features.parquet`; `scripts/equity/gaphv_reverse.py` →
`data/equity/gaphv/reverse.md` (every feature × variant: medians of the big winners / all / big losers, the PF by
quintile, the winners' share of the big moves Q1 → Q5, ρ with r). Only features known at the decision: the premarket to
09:25 for the 09:30 variants; those plus 09:30–10:00 for the 10:00 variants. The borrowed features: the Snoozers'
volat_open30 (→ pm_volat over 08:55–09:25, and volat30), inten (→ pm_inten: \$ of the last premarket 30 m / the earlier \$;
inten30: \$ 09:45–10:00 / 09:30–09:45), pers / LowFader's rate (→ pm_rate: traded seconds 08:55–09:25 / 1800; nb30),
relative volume (rvol_pm, rvol30), FlushFader's retail share (BJZZ), plus the trade size, the trend / position in the
range, the efficiency ratio and the time to the 30 m high.

**The separating features (the short's PF Q1 → Q5, the winners' share of the big moves Q1 → Q5):**

| variant | feature | big winners / all / big losers (median) | PF Q1 → Q5 | winner share Q1 → Q5 |
|---|---|---|---|---|
| U0930 | pm volat bp | 50 / 34 / 39 | 1.15 → 1.68 | 24 → 62 % |
| U0930 | pm rate | 0.54 / 0.20 / 0.29 | 1.10 → 1.66 | 28 → 63 % |
| U0930 | rvol_pm | 5.48 / 1.56 / 1.81 | 1.16 → 1.64 | 29 → 65 % |
| U0930 | premarket traded seconds | 4,178 / 1,577 / 2,375 | 1.02 → 1.75 | 32 → 62 % |
| U0930 | pm trend (last / pm vwap − 1) | −0.7 / −0.5 / −0.3 % | **1.93** (Q1, fading into the open) → 1.32 | 60 → 53 % |
| D0930 | pm trend | −2.1 / −0.8 / −1.2 % | 1.60 (Q1, falling into the open) → 1.04 | 58 → 45 % |
| D0930 | pm retail share | 0.05 / 0.03 / 0.04 | 1.09 → 1.47 | 41 → 53 % |
| U1000 | rvol_pm | 1.49 / 0.33 / 0.55 | 0.85 → 1.30 | 36 → 62 % |
| U1000 | range30 / volat30 / rvol30 | 16.0 / 9.6 / 12.7 % · 83 / 51 / 67 bp | 0.79 → 1.34 · 0.80 → 1.29 · 0.98 → 1.30 | 28 → 61 % · 27 → 60 % |
| U1000 | ret30 | +0.6 / +0.2 / +1.4 % | **1.41** (Q1, failed the open) → 1.01 | 59 → 52 % |
| U1000 | retail share 30 m | 0.23 / 0.18 / 0.21 | 0.86 → 1.14 | 34 → 55 % |
| D1000 | (nothing separates) | | every feature 1.03–1.30 | |

Reading:
- **The big winners are the MOST ACTIVE names, not moderate ones** — on every activity measure (premarket volatility,
  premarket rate, rvol_pm, traded seconds), the big winners' median is far above the population's and above the big
  losers'. The big losers are also above the population (activity sizes the move), but less.
- **The Snoozers' intensity (recent \$ / earlier \$) is dead here** in every variant (ρ −0.02 .. +0.02), and so are the
  trade size and the efficiency ratio. Activity LEVELS matter; their ratios over time do not.
- **The trend into the decision is the second lever:** a name already fading into the open (U0930 pm trend Q1: 1.93;
  D0930 Q1: 1.60) or one that failed its open by 10:00 (U1000 ret30 Q1: 1.41) is the better short.
- **D1000 has no feature that separates its winners** — its PF is 1.03–1.30 in every quintile of every feature.
- ⚠ The activity features are one family (ρ among them high: pm volat / pm rate / rvol_pm / traded seconds), and every
  one leans to the low-priced names — a multivariate fit with a price control comes before any gate. ⚠ census `pm_vol`
  (rvol_pm) runs to 09:30, 5 minutes past the 09:25 decision; the 1s features stop at 09:25.

**How will we know ahead of time (the wild gap-up open, U0930 volat30 ≥ 115 bp, its top decile)?**

| known at | feature | wild % Q1 → Q5 | ρ with volat30 | the short's PF open→close Q1 / Q5 |
|---|---|---|---|---|
| 09:25 | pm volat 08:55–09:25 | 0 → 28 % | +0.62 | 1.15 / 1.68 |
| 09:25 | pm rate | 3 → 26 % | +0.44 | 1.10 / 1.67 |
| 09:25 | traded seconds | 4 → 22 % | +0.43 | 1.08 / 1.75 |
| 09:25 | \|gap\| | 2 → 28 % | +0.39 | 1.48 / 1.49 |
| 09:35 | volat 09:30–09:35 | 0 → 41 % | +0.87 | 1.13 / 1.59 |
| 09:35 | ret 09:30–09:35 | 16 / 5 / 3 / 4 / 22 % | −0.01 | 4.30 / **0.53** (10:00→close 1.30 / 1.26) |

- The premarket DOES forecast the wild open (pm volatility ρ 0.62; the first 5 minutes ρ 0.87). **But a forecast wild open
  is the BEST open short (PF 1.68), not one to skip** — GH-1's "enter the wild ones at 10:00" does not survive: its
  losing decile is selected on the realized squeeze. What predicts the squeeze-first trade is its DIRECTION in the first
  minutes (ret5 Q5: PF 0.53 from the open, 1.26 from 10:00) — known only after the entry; a manage question, not a gate.

## GH-3 — the counterfactual hold, the session extremes, the time below the moving averages (2026-11-21, user: "instead of thinking in terms of stops we should find the area that has the most profit"; SMB: "80 % of the time the stock puts in its high or low in the first hour")

`scripts/equity/gaphv_path.py` → `data/equity/gaphv/path.parquet` (the \$5k VWAP from every half hour 10:00 .. 15:30; per
period the share of the clock seconds below the session VWAP and the 5 / 15 / 30 / 60-minute simple averages, and their
crossings); `scripts/equity/gaphv_hold.py` → `data/equity/gaphv/hold.md`. The short, gross; every intraday exit = the \$5k
VWAP from the mark.

**1. Exit at each mark (PF · mean):**

| variant | 10:00 | 10:30 | 11:00 | 12:00 | 13:00 | 14:00 | 15:00 | 15:30 | close |
|---|---|---|---|---|---|---|---|---|---|
| U0930 | 1.52 · +1.24 % | 1.61 · +1.64 % | **1.62 · +1.78 %** | 1.54 · +1.77 % | 1.51 · +1.82 % | 1.53 · +1.95 % | 1.48 · +1.88 % | 1.51 · **+1.99 %** | 1.42 · +1.75 % |
| D0930 | 1.26 · +0.53 % | 1.28 · +0.64 % | 1.24 · +0.60 % | 1.25 · +0.69 % | 1.25 · +0.71 % | 1.26 · +0.78 % | 1.26 · +0.81 % | **1.29 · +0.90 %** | 1.27 · +0.87 % |
| U1000 | | **1.14** · +0.20 % | 1.14 · +0.25 % | 1.11 · +0.24 % | 1.09 · +0.23 % | 1.11 · +0.30 % | 1.09 · +0.26 % | 1.11 · **+0.31 %** | 1.05 · +0.17 % |
| D1000 | | 1.07 · +0.09 % | 1.06 · +0.10 % | 1.12 · +0.23 % | 1.14 · +0.29 % | 1.16 · +0.36 % | 1.16 · +0.38 % | **1.19 · +0.46 %** | 1.16 · +0.42 % |

- **The gap-UPS make their profit in the first 60–90 minutes**: U0930 is at PF 1.61–1.62 by 10:30–11:00; after that the
  half hours are near flat (PF 0.93–1.12 each). The gap-DOWNS spread theirs over the whole day (D1000 rises 1.07 → 1.19).
- **The last half hour LOSES on every variant** (15:30 → close: U0930 −0.19 %, U1000 −0.15 %, D0930 −0.02 %, D1000
  −0.03 %); a 15:30 exit beats the close in all four.

**2. Hold the winners, cut the losers — by TIME, not price** (the rest of the day from the mark, PF · mean, by the position
at the mark):

| variant | at 11:00: in profit / losing | at 12:00 | at 13:00 | at 14:00 |
|---|---|---|---|---|
| U0930 | 1.07 · +0.19 % / 0.96 · −0.15 % | 1.10 · +0.22 % / 0.93 · −0.21 % | 1.04 · +0.07 % / 0.94 · −0.16 % | 1.00 · +0.00 % / **0.83 · −0.39 %** |
| U1000 | 1.01 · +0.02 % / 0.94 · −0.17 % | 1.05 · +0.10 % / 0.89 · −0.26 % | 1.04 · +0.06 % / 0.91 · −0.19 % | 1.00 · −0.01 % / **0.84 · −0.27 %** |
| D0930 | 1.15 · +0.34 % / 1.09 · +0.18 % | 1.14 · +0.27 % / 1.07 · +0.14 % | 1.13 · +0.21 % / 1.11 · +0.17 % | 1.13 · +0.18 % / 1.01 · +0.02 % |
| D1000 | 1.14 · +0.30 % / 1.17 · +0.34 % | 1.15 · +0.27 % / 1.07 · +0.12 % | 1.16 · +0.24 % / 1.03 · +0.04 % | 1.13 · +0.16 % / 0.98 · −0.03 % |

- On the gap-UPS a short still LOSING at 11:00 or later keeps losing for the rest of the day (0.83–0.96); the winners keep
  a small edge. A loser exit at a half-hour CHECK, not a price stop. On the gap-DOWNS a loser still earns until ~13:00
  (1.03–1.17): there, do not cut early.

**3. When the session extremes are set (the 1s vwap from 09:30):**

| variant | high by 10:30 | low by 10:30 | either by 10:30 | both | the short's best exit (the session low after the entry) by 10:30 / 11:00 / 12:00 / 14:00 / later |
|---|---|---|---|---|---|
| U0930 | 65 % | 45 % | **92 %** | 17 % | 45 / 52 / 62 / 76 / 24 % |
| D0930 | 59 % | 54 % | **93 %** | 20 % | 54 / 59 / 67 / 78 / 22 % |
| U1000 | 62 % | 55 % | **95 %** | 23 % | 32 / 43 / 56 / 73 / 27 % |
| D1000 | 65 % | 56 % | **95 %** | 26 % | 32 / 43 / 55 / 70 / 30 % |

- SMB holds and then some: one of the two extremes is in by 10:30 on 92–95 % of these days. But it is mostly the HIGH (the
  short's adverse extreme, 59–65 %); the LOW, the short's best exit, comes after 10:30 on about half of them, after 14:00
  on 22–30 %. Past 10:30 the risk extreme is usually behind us; the profit extreme often still ahead.

**4. The share of the period below the session VWAP (big winners / all / big losers; descriptive — the groups are the
final outcome):** after 10:30 the big winners sit below the VWAP 82–99 % of the time, the big losers 6–32 %, the
population 52–66 %. The 5- and 30-minute averages separate far less (s5: 53–60 % vs 43–48 %): **the anchored VWAP is
the average that tells the groups apart.**

**5. Range-bound / below-VWAP as a filter (knowable): the rest of the day from 10:30 by the share of 10:00–10:30 below the
VWAP, and by the s15 crossings:**

| variant | share below VWAP Q1 (≈ 0) → Q5 (≈ 1), PF | s15 crossings Q1 (few) → Q5 (many), PF |
|---|---|---|
| U0930 | 0.99 · 0.98 · 1.01 · **1.21 · 1.15** | 1.05 · 1.04 · 1.09 · 1.00 · 1.12 |
| U1000 | **0.89 · 0.90** · 0.97 · 1.11 · 1.13 | 0.99 · 0.95 · 1.01 · 0.99 · 1.02 |
| D0930 | 1.04 · 1.12 · 1.08 · 1.07 · 1.13 | 1.04 · 1.03 · 1.11 · 1.11 · 1.17 |
| D1000 | 1.10 · 1.09 · 1.17 · 1.10 · 1.23 | 1.12 · 1.12 · 1.12 · 1.14 · 1.19 |

- **On the gap-ups the VWAP is the hold rule**: a name that spent 10:00–10:30 above its VWAP is a losing short for the rest
  of the day (U1000 0.89–0.90; at 11:00 Q1 0.81); one below it keeps paying (1.11–1.21). The gap-downs pay either way.
- **Range-bound is NOT a reason to exit**: more crossings is, if anything, slightly better (D0930 1.04 → 1.17). The
  ranging names are not the losers; the names trending UP through the VWAP are.

## GH-4 — the big LOSERS as LONG trades: what they have in common (2026-11-21, user: "profile the losers as they could be good long trades. We already have some long setups in gap up that hold the open")

`scripts/equity/gaphv_longs.py` → `data/equity/gaphv/longs.md` (every trait × variant: its share of the big long winners vs
of all, the long's PF with / without it); `scripts/equity/gaphv_longs_1030.py` → `data/equity/gaphv/longs_1030.md` (the
knowable long entries). The LONG on the variant's names, gross / net of 0.7c a share (GF's swing cost).

**1. The big losers share the big winners' profile.** U1000's big long winners (its short's big losers, the top 10 % of
the long, r ≥ +9 %) are WILD and heavy: 12 % calm vs 33 % of all (lift 0.35), rvol30 ≥ 2 60 % vs 48 %, heavy premarket
39 % vs 29 %, retail share ≥ 0.2 56 % vs 41 %, price ≥ \$10 32 % vs 47 %. That is the big short winners' profile too
(GH-2): volatility and activity set the SIZE of the move in both directions. Every one of those traits LOSES as a long on
average (PF 0.80–0.88): the wild names hold both tails. The same holds on all four variants.

**2. What separates them is the path after the entry**, not anything at it: the 09:30 variants' big long winners HELD the
open at 10:00 (76–80 % vs 37–46 %), sat near the 30-minute high (lift 2.0–2.4), made that high late (≥ 20 min: lift
2.1–2.4), stayed above the VWAP (lift 1.6–2.0). Read from the open those are outcomes (the long's own first 30 minutes —
"held" alone gives PF 3.7 from the open); the knowable versions are entries at 10:00 / 10:30:

| names | bought 10:00: all | 10:00 · held | **10:00 · calm · held** | 10:30 · above the VWAP 10–10:30 | 10:30 · above · calm | 10:30 · below |
|---|---|---|---|---|---|---|
| U0930 | 0.88 / 0.83 | 0.92 / 0.88 | **114/yr · 1.49 / 1.40** (1.76 / 1.24) | 1.00 / 0.96 | 124/yr · 1.44 / 1.34 | 0.91 / 0.86 |
| U1000 | 0.95 / 0.91 | 1.04 / 1.01 | **399/yr · 1.31 / 1.26** (1.24 / 1.28) | 1.11 / 1.07 | 366/yr · 1.30 / 1.24 | 0.93 / 0.88 |
| D0930 | 0.90 / 0.85 | 0.87 / 0.83 | 273/yr · 1.06 / 0.99 | 0.93 / 0.86 | 1.05 / 0.97 | 0.91 / 0.85 |
| D1000 | 0.86 / 0.82 | 0.85 / 0.81 | 288/yr · 0.99 / 0.94 | 0.91 / 0.86 | 0.99 / 0.94 | 0.85 / 0.81 |

(PF gross / net; (net 2016–20 / 2021–26).)

- **The only long is the existing one: the CALM gap-up that HELD its open** — the GF-42 swing, reproduced here inside U1000
  (399 / yr, net 1.26, both halves 1.24 / 1.28; GF-42: 1.29) and on U0930's heavier-premarket names (114 / yr, net 1.40;
  2021–26 1.24).
- **Calm is the lever, monotone** (U1000 held, net, by volat30): < 40 bp 1.26 · 40–60 1.15 · 60–80 1.05 · 80–120 0.97 ·
  ≥ 120 0.67. Held beats failed in every band (failed: 1.09 → 0.60). Inside the calm · held cell, rvol_pm ≥ 0.5 grades it up
  (80 / yr, net 1.54 — GF-39's ≥ 0.2: 1.53); the price, pos30 and the VWAP add nothing.
- **Above the VWAP 10:00–10:30 is NOT a long by itself** (U1000 1.07 net, U0930 0.96): it adds nothing beyond calm. The
  GH-3 VWAP rule is a reason to get OUT of a gap-up short, not to get long.
- **No long on the gap-downs** in any cut (net ≤ 0.99): the heavy-volume gap-down is a short or nothing.
- **The calm long makes its money all day, into the close** (U1000 calm · held, exit at 10:30 1.04 → 12:00 1.15 → 14:00 1.21
  → 15:30 1.26 → close 1.31) — the mirror of the shorts' losing last half hour.
- ⇒ **One population, two tapes:** the heavy-volume gap-up is a SHORT on the active / wild tape and a LONG on the calm tape
  that held its open. The big short losers are not predictable longs; they are the tail of the wild tape.

## GH-5 — the RETAIL SHARE on the high-volume variants (2026-11-21, user: "Does the new retail share of volume feature make any difference to any of the setups?")

`scripts/equity/gaphv_retail.py` → `data/equity/gaphv/retail.{parquet,md}`. The BJZZ share Σ retail_volume / Σ total_volume
(FlushFader's V2-54): pm_retail (04:00–09:25), retail30 (09:30–10:00), and FlushFader's own window rs1200 (the last 1,200
present 1s bars before 09:25 / 10:00).

⚠ **Coverage:** the PREMARKET share is ~0 in 2016–18 (median 0.000; 0.02–0.05 in 2019–24, 0.09–0.10 in 2025–26) — the
premarket off-exchange retail prints barely exist in the early tape. The regular-hours share is steady (0.12–0.19 by year).
Any premarket-share cut is partly a YEAR selector → the control below uses within-year quintiles.

| setup | the share | PF Q1 → Q5 (pooled) | within-year Q1 → Q5 | FlushFader gate ≥ 0.153 pass / fail |
|---|---|---|---|---|
| U0930 short | pm_retail | 1.50 · 1.48 · 1.38 · 1.33 · 1.45 | — | 1.49 / 1.41 |
| D0930 short | pm_retail | 1.09 · 1.19 · 1.22 · 1.31 · **1.47** | (2019+) **1.02** · 1.27 · 1.35 · 1.28 · 1.36 | 1.41 / 1.25 |
| U1000 short | retail30 | **0.86** · 0.92 · 1.04 · 1.15 · 1.14 | **0.82** · 0.94 · 1.02 · 1.20 · 1.12 | **1.13 / 0.88** |
| D1000 short | retail30 | 1.07 · 1.13 · 1.23 · 1.14 · 1.21 | 1.07 · 1.15 · 1.21 · 1.11 · 1.23 | 1.22 / 1.11 |
| U1000 calm · held LONG (net) | retail30 | 1.13 · 1.13 · 1.28 · 1.21 · 1.44 (ρ 0.00; 2016–20 flat) | — | 1.38 / 1.19 |
| U0930 names calm · held LONG (net) | pm_retail | 1.91 · 1.66 · 1.80 · 1.29 · 1.05 (n 1,148) | — | 1.21 / 1.54 |

**Controls** (within-year quintiles, inside volat30 and price terciles):
- **U1000 short — REAL, and it lives on the wild tape:** volat30 T3 0.75 → 1.07 → 1.15 → **1.36** → 1.23; calm / mid
  terciles flat at ≤ 1 (no short there anyway). It holds in every price tercile (\$2.5: 0.91 → 1.12; \$9: 0.70 → 1.39;
  \$38: 0.88 → 1.27) and in both halves (pooled Q1 0.80 / 0.89 → Q5 1.07 / 1.17). The bad cell is the LOW share: a
  gap-up the retail crowd is not trading keeps going — FlushFader's mechanism ("a flush the retail crowd is not trading
  has no fade"), on the short side.
- **D0930 short — the bottom quintile only** (the near-zero premarket share, 2019+): 1.02 vs 1.27–1.36 above it, in
  most volatility / price bands (Q1 0.81–1.19). A floor, not a grade.
- **D1000 short — mild** (1.07 → 1.23), concentrated on the wild tape (T3 0.98 → 1.42; calm Q5 0.93).
- **U0930 short and both longs — nothing** that holds (flat, or noisy on n ≈ 1–4k with mixed halves).

## GH-6 — the EXIT RULES as whole trades: the worst-case losses vs the profit (2026-11-21, user: "If they can do that without reducing net profit or the profit factor much, they would be well worth it")

`scripts/equity/gaphv_vwap30.py` → `data/equity/gaphv/vwap30.parquet` (per half hour the share below the session VWAP; the
last price before every mark); `scripts/equity/gaphv_exits.py` → `data/equity/gaphv/exits.md`. GH-3's rules, fixed before the
run: **T1530** out at 15:30 · **VWAP** at every check 10:30 .. 15:00 out when the previous half hour was mostly ABOVE the
session VWAP (< 50 % of its seconds below) · **VWAP10** plus a 10:00 check (09:30 variants) · **LOSER** at every check
11:00 .. 15:00 out when the short is losing (the last price before the mark > the entry) · **BOTH** = either; every rule
not fired is out at 15:30. Decisions on what is known before the mark; fills = the \$5k VWAP from the mark. The short,
gross, one unit a trade; sum = Σ % returns; CVaR = the mean of the worst 1 % / 5 %; max DD on the cumulative Σ % by date.

| variant | rule | PF | mean | sum | win % | CVaR 1 % | CVaR 5 % | max DD | PF 2016–20 / 2021–26 |
|---|---|---|---|---|---|---|---|---|---|
| U0930 | HOLD | 1.42 | +1.75 % | 20,706 | 62 % | −93.7 % | −44.7 % | 572 | 1.61 / 1.37 |
| | T1530 | 1.48 | +1.93 % | 22,813 | 64 % | −93.5 % | −43.4 % | 493 | 1.69 / 1.42 |
| | VWAP | 1.59 | +1.86 % | 22,015 | 59 % | −59.6 % | −30.1 % | 402 | 2.01 / 1.49 |
| | **LOSER** | **1.61** | **+1.97 %** | **23,272** | 50 % | −59.0 % | −30.2 % | 417 | **2.04 / 1.50** |
| | BOTH | 1.60 | +1.82 % | 21,548 | 57 % | −58.8 % | −29.3 % | 402 | 2.01 / 1.50 |
| | BOTH10 | 1.55 | +1.62 % | 19,111 | 55 % | −53.2 % | −26.8 % | 401 | 2.01 / 1.44 |
| U1000 | HOLD | 1.05 | +0.17 % | 3,864 | 54 % | −59.4 % | −28.8 % | 2,053 | 1.00 / 1.08 |
| | T1530 | 1.11 | +0.31 % | 7,093 | 55 % | −57.9 % | −27.3 % | 2,092 | 1.05 / 1.13 |
| | **VWAP** | **1.18** | **+0.36 %** | **8,162** | 47 % | −26.9 % | −14.6 % | 929 | 1.13 / 1.21 |
| | LOSER | 1.17 | +0.35 % | 8,073 | 35 % | −29.3 % | −15.5 % | 893 | 1.11 / 1.20 |
| | BOTH | 1.18 | +0.32 % | 7,188 | 42 % | −25.2 % | −13.5 % | 988 | 1.11 / 1.21 |
| D0930 | HOLD | 1.27 | +0.87 % | 11,502 | 54 % | −51.7 % | −27.4 % | 732 | 1.37 / 1.24 |
| | **T1530** | **1.29** | **+0.90 %** | **11,844** | 55 % | −50.6 % | −26.7 % | **455** | 1.38 / 1.26 |
| | LOSER | 1.27 | +0.75 % | 9,835 | 40 % | −37.1 % | −20.7 % | 628 | 1.33 / 1.25 |
| | BOTH | 1.27 | +0.69 % | 9,047 | 48 % | −34.2 % | −19.4 % | 555 | 1.28 / 1.27 |
| D1000 | HOLD | 1.16 | +0.42 % | 6,772 | 54 % | −41.5 % | −21.1 % | 1,161 | 1.22 / 1.14 |
| | **T1530** | **1.19** | **+0.46 %** | **7,479** | 54 % | −38.6 % | −20.0 % | **953** | 1.22 / 1.17 |
| | LOSER | 1.13 | +0.25 % | 4,036 | 33 % | −24.4 % | −13.3 % | 1,435 | 1.12 / 1.13 |
| | BOTH | 1.12 | +0.19 % | 3,105 | 41 % | −21.8 % | −11.7 % | 1,313 | 1.07 / 1.14 |

- **The gap-UPS: the rules cut the tail by a third to a half AND raise the profit.** U0930 LOSER: CVaR 1 % −94 → −59 %,
  CVaR 5 % −45 → −30 %, max DD 572 → 417, while PF 1.42 → 1.61 and the sum +12 % (both halves up: 1.61 → 2.04, 1.37 →
  1.50). U1000 VWAP: CVaR 1 % −59 → −27 %, max DD 2,053 → 929, PF 1.05 → 1.18, the sum ×2.1 (T1530 alone is ×1.8 of it).
  VWAP, LOSER and BOTH land within a few % of each other — the choice is loose; the 10:00 check (VWAP10 / BOTH10) cuts a
  little more tail but costs ~10 % of the sum.
- **The gap-DOWNS: only T1530.** The rules cut the tail there too (D0930 CVaR 1 % −52 → −34 %) but cost 15–55 % of the
  profit and leave the PF flat or lower; T1530 alone gives the best sum AND the smallest max DD (D0930 732 → 455). As GH-3
  said: a losing gap-down short still earns until ~13:00.
- **Where the gap-up exits happen** (U0930 BOTH10): 34 % at 10:00, 14 % at 10:30, 10 % at 11:00, then 1–5 % per check,
  26 % held to 15:30 (mean +13.3 %). The exited trades' average outcome is about what holding gave them (10:00: −6.0 % vs
  −5.5 % held) — the rule does not pick better exits on average; it removes the TAIL (the MLGO class: −248 % held, out at
  10:00 at −26 %).
- ⚠ **What no rule can cut:** the halted / untradeable tails — PGHL 2024-11-26 (−437 %; Primega Group Holdings, a Hong Kong
  construction-haulage small cap listed 2024-07-23: HALTED from 09:48:26 — 09:42 is only its session high — to the close,
  reopened at 16:00 at \$107.49 (the official close \$107.36 vs the \$20 open), \$204 after hours, then \$15.40 at the
  next open and \$1.65 at the next close on 43M shares) and ZJYL 2023-12-18 (stale thin prints) stay in every rule's worst trade. Those need SIZING and a
  position cap, not an exit.
- ⚠ The rules were read off GH-3 on the same sample (in-sample), though they were fixed before this run and both halves
  improve; gross of costs and borrow (an early exit adds no extra order: every trade has exactly one exit either way).

## GH-7 — a higher dv60_med floor against the untradeable tails (2026-11-21, user, after PGHL / ZJYL: "Could we filter out such trades using a 60d median volume filter?")

`scripts/equity/gaphv_floor.py` → `data/equity/gaphv/floor.md`. Each variant on its GH-6 exit (U0930 LOSER, U1000 VWAP,
D0930 / D1000 T1530), the short, gross.

| variant | floor \$0.5M (now) | \$1M | \$2M | \$5M | \$10M |
|---|---|---|---|---|---|
| U0930: n/yr · PF · sum · CVaR 1 % | 1,170 · 1.61 · 23,272 · −59 % | 816 · 1.55 · 14,070 · −57 % | 554 · 1.54 · 8,730 · −47 % | 333 · 1.60 · 5,202 · −43 % | 224 · 1.68 · 3,502 · −37 % |
| U1000 | 2,259 · 1.18 · 8,162 · −27 % | 1,785 · 1.14 · 4,644 · −23 % | 1,393 · 1.11 · 2,472 · −21 % | 961 · 1.10 · 1,495 · −18 % | 712 · 1.06 · 562 · −17 % |
| D0930 | 1,305 · 1.29 · 11,844 · −51 % | 988 · 1.27 · 8,145 · −51 % | 751 · 1.24 · 5,262 · −45 % | 530 · 1.24 · 3,426 · −38 % | 408 · 1.17 · 1,922 · −35 % |
| D1000 | 1,611 · 1.19 · 7,479 · −39 % | 1,340 · 1.18 · 5,619 · −39 % | 1,108 · 1.20 · 4,824 · −35 % | 828 · 1.23 · 3,758 · −31 % | 647 · 1.21 · 2,498 · −29 % |

- **A floor is the wrong tool: it removes the edge with the tail.** At \$2M (which drops PGHL, \$1.64M, and ZJYL, \$1.01M)
  U0930 keeps 47 % of its trades and 38 % of its profit for a CVaR 1 % of −47 % (vs −59 %); U1000's PF FALLS with every
  step (1.18 → 1.06): its edge is in the thin names (as GF-42's swing, U-shaped in dv20). Only D1000 is indifferent
  (1.19 → 1.23).
- **The tails are only mildly concentrated in thin names:** the worst 1 %'s median dv60 is \$1.2M (U0930, U1000) vs
  \$1.8M / \$3.4M for all; the 20 worst trades include MULN (\$4.9M), WAL (\$56.5M, the SVB week), DRYS (\$10M), XELA,
  SRXH (\$3.8–4.2M) — a floor high enough to catch them takes most of the book.
- ⇒ The tail is a SIZE problem: a per-name dollar cap tied to the name's liquidity (a % of dv60_med), so a thin name is
  traded smaller rather than not at all. (ZJYL 2023-12-18 — gap +4 %, \$29, dv60 \$1.01M, then +100 % after hours —
  and PGHL 2024-11-26 — +90 % gap, halted from 09:42 — are not separable at the entry by volume.)

## GH-8 — ZJYL 2023-12-18: what the REPORTED SHORT INTEREST said before the squeeze (2026-11-21, user: "We need to start looking into reported short interest")

Sources (raw JSON in `data/short_interest/`): Massive `/stocks/v1/short-interest` (FINRA's twice-monthly settlement
snapshots; history from 2017-12-29; one call per settlement date returns every ticker — 19,266 rows on 2023-12-15);
Massive `/v3/reference/tickers/ZJYL?date=` (point-in-time shares outstanding). Benzinga `/api/v1/shortinterest` answers
only with the LATEST record on the current (pre-trial) key, whatever date is asked (it adds the float and % of float:
2026-11-10 SI 7,541 = 0.49 % of a 1.55M float, 7.83M outstanding); Benzinga news has nothing on ZJYL. float.db has no
ZJYL (a 20-F filer: no XBRL float).

| FINRA settlement | short interest (shares) | FINRA avg daily volume | ≈ % of 7.75M outstanding | ≈ % of a ~1.5M float* | known on 12-18? |
|---|---|---|---|---|---|
| 2023-11-15 | 8,118 | 22,041 | 0.10 % | 0.5 % | ✅ |
| **2023-11-30** | **22,648** | 74,348 | **0.29 %** | **1.5 %** | ✅ the latest published |
| 2023-12-15 (the Friday before) | 54,906 | 86,194 | 0.71 % | 3.5 % | ❌ published ~12-26 |
| 2023-12-29 | 64,130 | 196,494 | 0.83 % | 4.1 % | after |
| 2024-01-12 | 43,621 | 36,705 | 0.56 % | 2.8 % | after |

\* the float is NOT point-in-time: Benzinga's current float / outstanding ratio (20 %) applied to Dec 2023's 7.75M
(the 2024-02-08 20:1 split and the 2026-03-16 1:20 reverse cancel). FINRA publishes ~7 business days after the
settlement date: the 12-15 snapshot was not knowable on 12-18.

**The day** (1s tape, RAW): premarket \$28–42 on thin prints (\$3.4M 09:00–09:30); open \$29 (+3.5 % gap); a crash to
**\$11.21 by 10:13 (−61 %)** through LULD pauses; a rebound to \$25.30 by 11:25; then **HALTED from 11:25 to the close** —
no print until 16:00:00, when it reopened at \$116 (the official close \$101.87; the daily high \$509.87) and traded to
\$339 after hours; 12-19 opened \$175. The day traded 783,865 shares, **14× the short interest of 12-15** and about half
the estimated float.

Reading:
- **The reported short interest was tiny**: 22.6k shares known on the day (0.3 % of outstanding, ~1.5 % of the float, about
  \$0.6M at \$28, under one day to cover). Nothing in it says "squeeze". Even the unpublished 12-15 figure (54.9k, ~3.5 %
  of the float) is small, and the shorts ADDED into the move (12-29: 64.1k).
- **This was a tiny-float halt, not a short-interest squeeze**: ~80 % insider-held, ~1.5M shares free, a 4.5-hour halt,
  and the reopen far above the halt price. The trade was +60 % at 10:13 and still +13 % at the halt — no GH-6 rule fires
  (not losing, not above the VWAP), and the halt then removed every exit. The float, not the reported SI, is the
  feature to measure; and since no exit survives a halt, the cap on a thin name has to be set at the entry.

## GH-9 — the PGHL / ZJYL profile, and the "thin shares at a high price" cell (2026-11-21, user: "What was the profile of that stock according to various features?")

PGHL 2024-11-26 (Primega; halted for volatility from 09:48 to the close — Seeking Alpha: "halted for volatility most of the
day ... up almost 1,000%"; a Nasdaq unpaid-fees delisting notice of 11-21, paid on 11-26; renamed ZDAI 2025-05-16) and ZJYL
2023-12-18, against U0930's population (percentile in brackets):

| feature (known at 09:25 unless noted) | PGHL | ZJYL | U0930 median |
|---|---|---|---|
| gap | +89.8 % (98) | +3.5 % (10) | +13.0 % |
| price (open) | \$20.00 (83) | \$29.00 (88) | \$5.35 |
| **vol60_med (shares / day)** | **124,480 (12)** | **53,712 (3)** | 489,962 |
| dv60_med | \$1.64M (47) | \$1.01M (31) | \$1.81M |
| shares outstanding (Massive, point-in-time) | 24.0M (IPO 2024-07-23) | 7.75M (IPO 2023-03-28) | |
| rvol_pm | 1.34 (45) | 3.89 (71) | 1.56 |
| premarket volume (shares) | 166,992 (11) | 209,181 (14) | 1,202,545 |
| premarket traded seconds · rate 08:55–09:25 | 382 (16) · 0.08 (26) | 732 (30) · 0.10 (31) | 1,577 · 0.20 |
| premarket shares / trade | 88 (12) | 123 (24) | 199 |
| pm volat · pm trend | 54 bp (78) · −7.6 % (9) | 25 bp (33) · −0.7 % (47) | 34 bp · −0.5 % |
| retail share, 1,200 bars to 09:25 | 0.067 (62) | 0.011 (23) | 0.044 |
| reported SI before (FINRA) | 98,357 (11-15) / 144,449 (10-31) ≈ 0.4–0.6 % of outstanding | 22,648 (11-30) ≈ 0.3 % | |
| *after the entry:* volat 09:30–09:35 · ret5 | 581 bp (100) · +8.9 % (96) | 427 bp (99) · −19.3 % (0) | 86 bp · −0.6 % |
| *after:* volat30 · retail share 30 m | 550 bp (100) · 0.164 (32) | 653 bp (100) · 0.028 (5) | 58 bp · 0.212 |

**What they share:** a HIGH share price (83–88th pct) on very little SHARE volume (vol60 3rd / 12th pct; the premarket
11–14th pct in shares, small trades): the price makes the dollar volume look ordinary (dv60 31st / 47th pct, above
every floor), but few shares trade — a small float, recently listed Asian small caps. Both then opened with
first-5-minute volatility at the 99–100th percentile. Reported SI was small in both (≤ 0.6 % of outstanding).

**The cell, on all of U0930 (LOSER exit, the short, gross):**

| U0930 cell | n / yr | PF | mean | sum | worst | CVaR 1 % | PF 2016–20 / 2021–26 |
|---|---|---|---|---|---|---|---|
| all | 1,170 | 1.61 | +1.97 % | 23,272 | −437 % | −59.0 % | 2.04 / 1.50 |
| vol60 < 100k shares | 102 | 1.45 | +1.68 % | 1,720 | −251 % | −98.2 % | 1.35 / 1.48 |
| vol60 100–200k | 160 | 1.48 | +1.64 % | 2,655 | −437 % | −88.2 % | 1.98 / 1.36 |
| vol60 200–500k | 331 | 1.49 | +1.70 % | 5,673 | −114 % | −50.3 % | 2.12 / 1.33 |
| vol60 ≥ 500k | 577 | 1.75 | +2.27 % | 13,224 | −89 % | −46.8 % | 2.16 / 1.65 |
| **vol60 < 200k shares AND price ≥ \$10** | 124 | **1.26** | +0.91 % | 1,141 | −437 % | **−115.8 %** | 1.36 / 1.23 |
| the rest | 1,046 | **1.65** | +2.09 % | 22,131 | **−130 %** | **−52.3 %** | 2.15 / 1.53 |
| volat5 ≥ 300 bp (known 09:35) | 21 | 1.17 | +2.15 % | 460 | −437 % | −344 % | 1.41 / 1.13 |
| volat5 ≥ 400 bp | 9 | 0.84 | −2.72 % | −248 | −437 % | −437 % | 1.95 / 0.76 |

- **Excluding "thin shares at a high price"** drops 11 % of the trades and 5 % of the profit; PF 1.61 → 1.65 (both
  halves up), the worst trade −437 → −130 %, CVaR 1 % −59 → −52 %. The share volume, not the dollar volume (GH-7), is the
  measure that finds these names. ⚠ The cell was drawn from two examples (in-sample): a holdout and the other variants
  come before a gate.
- The 09:35 volatility extreme (≥ 300–400 bp) is where both blew up, but it is 9–21 trades a year — an exit-at-09:35 idea,
  too thin to rule on.

## GH-10 — exit at 5 minutes on extreme after-entry volatility (2026-11-21, user: "It might not be a bad idea to exit 5m if the after-entry volatility is >= 400 bps")

`scripts/equity/gaphv_exit5.py` → `data/equity/gaphv/exit5.{parquet,md}`. v5 = the 30 s slot volat over the first 5 minutes
after the entry (09:30–09:35 / 10:00–10:05), the exit = the \$5k VWAP from 09:35 / 10:05; on top of each variant's GH-6
exit. The short, gross. (The halts, for the record: PGHL's last print 09:48:26 → the 16:00 reopen; ZJYL paused after
09:37 / 09:48 / 09:58 / 10:13 (5–15 min each), last print 11:25:09 → the 16:00 reopen.)

| variant | rule | fired / yr | PF | sum | worst | CVaR 1 % | PF halves | fired trades: 5-min exit / GH-6 result |
|---|---|---|---|---|---|---|---|---|
| U0930 | GH-6 (LOSER) | — | 1.61 | 23,272 | −437 % | −59.0 % | 2.04 / 1.50 | |
| | + v5 ≥ 200 bp | 87 | 1.53 | 19,154 | −114 % | −46.5 % | 1.88 / 1.44 | −1.0 / +3.7 % |
| | + v5 ≥ 300 bp | 21 | 1.61 | 22,626 | −130 % | −50.7 % | 2.00 / 1.51 | −0.9 / +2.2 % |
| | **+ v5 ≥ 400 bp** | 9 | **1.62** | **23,412** | **−130 %** | **−52.4 %** | 2.02 / 1.52 | −1.2 / −2.7 % |
| | + v5 ≥ 500 bp | 4 | 1.63 | 23,771 | −251 % | −54.5 % | 2.03 / 1.53 | +2.1 / −10.7 % |
| D0930 | GH-6 (T1530) | — | 1.29 | 11,844 | −328 % | −50.6 % | 1.38 / 1.26 | |
| | + v5 ≥ 400 bp | 8 | 1.29 | 11,960 | −328 % | −47.2 % | 1.37 / 1.27 | +1.1 / −0.4 % |
| U1000 | GH-6 (VWAP) + v5 ≥ 400 bp | 3 | 1.18 | 8,045 | −123 % | −26.5 % | 1.13 / 1.21 | (v5 median 32 bp: rarely fires) |
| D1000 | GH-6 (T1530) + v5 ≥ 200 bp | 7 | 1.20 | 7,799 | −107 % | −34.5 % | 1.22 / 1.20 | +0.5 / −4.1 % |

- **U0930 at ≥ 400 bp is a free tail cut**: 9 trades a year, the sum +0.6 %, PF 1.61 → 1.62, the worst trade −437 → −130 %
  (PGHL and ZJYL both out at 09:35), CVaR 1 % −59 → −52 %. At 200 bp it fires 87 / yr and costs 18 % of the profit (the
  wild opens are good shorts on average, GH-2); 300–500 bp all sit near the same spot.
- **D0930: neutral** (MULN, −328 %, is not a fast open). **The 10:00 variants:** the 10:00–10:05 tape is far calmer
  (median 26–32 bp vs 68–86 bp at the open), so 400 bp almost never fires; D1000 at 200 bp cuts its worst −362 → −107 %
  for +4 % profit, but on 7 trades a year.
- It overlaps GH-9's thin-shares cell (the same two trades); it needs no price / share threshold drawn from two examples,
  and costs nothing. ⚠ The threshold came after seeing PGHL / ZJYL (in-sample); 300–500 bp agreeing is the robustness check.

## GH-11 — the 40-session Yang-Zhang volatility (2026-11-21, user: "both stocks had in common the extreme volatility on the daily charts. What would the 40d Yang Zhang look like?")

`scripts/equity/gaphv_yz40.py` → `data/equity/gaphv/yz40.md`. σ_YZ over D−40 .. D−1 (share-consistent OHLC from
daily_adjusted, P × n; k = 0.34 / (1.34 + 41/39)), daily %. Each variant on its GH-6 exit (+ GH-10's 09:35 rule on U0930).

- **PGHL 13.2 % a day (77th pct of U0930), ZJYL 11.5 % (71st)**; U0930's median is 7.4 %, its top decile 19–97 %. Among
  the U0930 names priced ≥ \$10 they are at the 88th / 85th pct (median 4.5 %): high for an expensive stock, not
  extreme for this population.
- **Daily volatility is a GRADER of the short, the right way round:** U0930 PF by decile 1.09 · 1.25 · 1.50 · 1.47 · 1.53
  · 1.72 · **1.97** · 1.70 · 1.70 · 1.73; D0930 1.04–1.11 in deciles 1–3, 1.43–1.57 in 9–10; U1000 0.87–1.01 in 1–4,
  1.35–1.43 in 9–10. The quiet names (yz40 < ~3–4 %, \$13–47 stocks) do not work as shorts at all. D1000 is the
  exception (flat 1.09–1.35; decile 1 1.35).
- **The tail grows with it:** CVaR 1 % −20 % (decile 1) → −66 % (U0930 decile 10), −15 → −91 % (D0930) — the bigger move
  both ways (as volat30, GH-1).
- ⇒ yz40 does not single out PGHL / ZJYL — high daily volatility is where the short's profit is. What made those two
  different is the COMBINATION (GH-9: few shares at a high price) plus the halt. yz40 is a floor candidate on the low
  side (yz40 < ~3 % ≈ no edge); NOT a vol-scaled size (the standing rule: no vol-scaled sizing in MR books).

### GH-10 → the DISASTER STOP (2026-11-21, user: "We're going to add this as a disaster stop. The worst trades on the short side are just so bad that we need this")

**Ruling: out at 5 minutes after the entry when v5 ≥ 300 bp** (the 300 bp row, not 400: U0930 21 / yr fired, PF 1.61,
sum 22,626 vs 23,272, worst −437 → −130 %, CVaR 1 % −59.0 → −50.7 %). The fill is a ratcheted maker cover (no taker
orders, ever); the research fill = the \$5k VWAP from the 5-minute mark. It applies to every variant (on the 10:00 ones it
almost never fires at 300 bp). yz40 (GH-11): **a sizing feature to keep in mind** (user) — not ruled on.

**The 40-session range** (max high / min low − 1 over D−40 .. D−1, share-consistent): PGHL +165 % (\$8.49 on 10-09 →
\$22.50 on 10-28; 64th pct of U0930, median +104 %; 78th pct among names ≥ \$10, median +54 %), the previous close
\$10.54 at 0.15 of that range; ZJYL +343 % (\$8.03 on 10-24 → \$35.56 on 12-14; 80th pct; 88th among ≥ \$10), the
previous close \$28.01 at 0.73 of the range.

## GH-12 — the volatility disaster stop at ANY point of the trade (2026-11-21, user: "what if we exit at any point in the stock's lifecycle if the 5m volatility reaches this point?")

`scripts/equity/gaphv_volstop.py` → `data/equity/gaphv/volstop.{parquet,md}`. The trailing 5-minute volat (10 × 30 s slots,
GapShort.fs's measure) checked every 30 s from 5 minutes after the entry to 15:30; out at the \$5k VWAP from the
firing second, unless the variant's GH-6 exit came first. any = the volat alone; adverse = only when the window moved
AGAINST the short (its last traded slot vwap > its first). The short, gross.

| variant | stop | fired / yr | PF | sum | worst | CVaR 1 % | CVaR 5 % | PF halves |
|---|---|---|---|---|---|---|---|---|
| U0930 | none | — | 1.61 | 23,272 | −437 % | −59.0 % | −30.2 % | 2.04 / 1.50 |
| | **300 at 5 min (the GH-10 ruling)** | 21 | **1.61** | **22,626** | −130 % | −50.7 % | −28.0 % | 2.00 / 1.51 |
| | 300 any time · any | 80 | 1.53 | 20,035 | −111 % | −45.9 % | −27.4 % | 1.94 / 1.43 |
| | 300 any time · adverse | 60 | 1.55 | 20,984 | −111 % | −46.3 % | −27.6 % | 1.92 / 1.46 |
| | 300 at 5 min + 500 adverse any time | 33 | 1.58 | 21,762 | −111 % | −49.5 % | −28.3 % | 1.92 / 1.49 |
| D0930 | none | — | 1.29 | 11,844 | −328 % | −50.6 % | −26.7 % | 1.38 / 1.26 |
| | 300 at 5 min | 16 | 1.29 | 11,650 | −328 % | −46.8 % | −25.7 % | 1.37 / 1.27 |
| | **300 any time · adverse** | 53 | 1.28 | 11,312 | **−103 %** | **−42.6 %** | −25.1 % | 1.32 / 1.27 |
| U1000 | none | — | 1.18 | 8,162 | −123 % | −26.9 % | −14.6 % | 1.13 / 1.21 |
| | 300 at 5 min | 5 | 1.19 | 8,173 | −77 % | −25.7 % | −14.3 % | 1.14 / 1.21 |
| | **300 any time · adverse** | 24 | 1.18 | 8,007 | **−62 %** | −24.2 % | −14.1 % | 1.14 / 1.20 |
| D1000 | none | — | 1.19 | 7,479 | −362 % | −38.6 % | −20.0 % | 1.22 / 1.17 |
| | 300 at 5 min | 3 | 1.19 | 7,563 | −147 % | −36.2 % | −19.5 % | 1.22 / 1.18 |
| | **300 any time · adverse** | 24 | 1.18 | 7,139 | **−107 %** | −34.3 % | −19.2 % | 1.21 / 1.17 |

(every threshold 200–500 bp, any / adverse: `data/equity/gaphv/volstop.md`.)

- **Adverse beats any** at every threshold and on every variant: a volatility burst in the short's favour (a crash) is a
  winner to keep.
- **Any time, adverse, 300 bp caps the worst trade at −62 to −111 % on all four variants** (vs −77 to −328 % with the
  5-minute check alone: it catches MULN on D0930, −328 → −103 %, and COSM on D1000) and trims CVaR 1 % by 1.5–4 points.
  The price: vs the 5-minute ruling −7 % of the profit on U0930 (PF 1.61 → 1.55), −3 % D0930, −2 % U1000, −6 % D1000.
- **The later firings are insurance, not alpha:** on U0930 the adverse stop's trades exit at −8.6 % on average vs −4.8 %
  had they been held (the spike mean-reverts — why stops "make things worse"); what they buy is the cap on the tail.
- On U0930 the 5-minute check already takes most of the tail: a later stop adds little there unless set higher (500 bp
  adverse after the first 5 min: −4 % of the profit for worst −130 → −111 %).

**GH-12b — both sides vs adverse, at 5 min and any time** (user: "I wouldn't mind seeing the exit on both sides"). The
measure (GH-10 / GH-12 alike): the 1s bars aggregated into GRID-ALIGNED 30 s clock slots (from 09:30:00, boundaries at :00 /
:30), the slot vwap = Σ vwap·vol / Σ vol; the 5-minute window = the last 10 complete slots, evaluated at each slot
boundary; volat = the mean |ln(v_j / v_i)| over successive TRADED slots both inside the window (an empty slot is skipped,
the pair spans it), ≥ 2 traded slots; bp. Not a sliding 300-second window of 1s bars.

| variant | 300 bp at 5 min: both sides / adverse | 300 bp any time: both sides / adverse |
|---|---|---|
| U0930 sum · worst · CVaR 1 % | 22,626 · **−130** · −50.7 / 23,093 · **−251** · −52.7 | 20,035 · −111 · −45.9 / 20,984 · −111 · −46.3 |
| D0930 | 11,650 · −328 · −46.8 / 11,816 · −328 · −47.5 | 10,862 · −103 · −42.5 / 11,312 · −103 · −42.6 |
| U1000 | 8,173 · −77 · −25.7 / 8,158 · −79 · −26.1 | 7,810 · −62 · −24.0 / 8,007 · −62 · −24.2 |
| D1000 | 7,563 · −147 · −36.2 / 7,612 · −147 · −36.4 | 6,825 · −107 · −34.4 / 7,139 · −107 · −34.3 |

- **At 5 minutes, BOTH SIDES is the right one:** ZJYL crashed −19 % in its first 5 minutes (in the short's favour), so the
  adverse check misses it (worst −251 %); the both-sides check takes it out at 09:35. The ruling stands as both sides.
- **Any time, the two give the same tail** (identical worst, CVaR within 0.5 point) and adverse keeps 2–5 % more of the
  profit — the both-sides exits after the first 5 minutes are winners cut short (U0930: −4.4 % at the stop vs −0.5 %
  held; D0930 +0.4 vs +1.8 %).

## GH-13 — the disaster stop: three volatility clocks × three triggers, with the uncapped fill (2026-11-21, user: "I am curious how a FlushFader style window would do")

`scripts/equity/gaphv_volstop2.py` → `data/equity/gaphv/volstop2.{parquet,md}` (every measure × trigger × 200–600 bp). Measures
from the entry on: **clk** (GH-12: 10 grid-aligned 30 s clock slots, simple mean; blind across a pause) · **pb** (FlushFader's
slot clock: 30 PRESENT 1s bars per slot, the simple mean of the last 10 slots' |r|; a pause is one return) · **ew** (the
same slots, FlushFader's bias-corrected EWMA, hl 10 slots). Triggers: **any** (both sides) · **win** (the window moved
against the short) · **uw** (the position underwater vs the entry). **The fill: the \$5k VWAP with NO time cap** (a
shortfall at the close fills at the official close) — after GH-12's ZJYL case (200 shares at the limit-up band). It
changes almost nothing in aggregate (clk · win · 300 on U0930: 20,951 vs 20,984) but ZJYL's 10:14:30 stop now fills at
\$22.99 (the 11:24:54 reopening print) — the short still +21 %.

The rows that matter (sum = Σ % · worst · CVaR 1 %):

| variant | GH-6 only | r5 = the ruling (clk, both sides, 5-min window) | best any-time rows |
|---|---|---|---|
| U0930 | 23,272 · −437 · −59.0 | **22,626 · −130 · −50.7** | r5 + pb · win · 400: 21,532 · −104 · −50.9 · r5 + clk · win · 400: 21,158 · −111 · −48.9 |
| D0930 | 11,844 · −328 · −50.6 | 11,650 · −328 · −46.8 | **ew · win · 300: 11,789 · −109 · −42.9 (PF 1.30)** · pb · win · 300: 11,429 · −103 · −42.6 · clk · win · 300: 11,312 · −103 · −42.6 |
| U1000 | 8,162 · −123 · −26.9 | 8,173 · −77 · −25.7 | ew · win · 300: 8,184 · −77 · −25.0 · clk · win · 300: 8,007 · −62 · −24.2 |
| D1000 | 7,479 · −362 · −38.6 | 7,563 · −147 · −36.2 | **ew · win · 400: 7,933 · −107 · −34.6 (PF 1.21)** · pb · win · 400: 7,917 · −107 · −34.7 |

- **Underwater (uw) is the worst trigger**: it fires after the loss is in (U0930 clk · uw · 300: the stopped trades exit
  −16.8 % vs −12.3 % held) and misses ZJYL (never underwater before its final halt: worst −251 %). The window's own
  direction (win) is the better "adverse".
- **The present-bar clock's ADVERSE versions miss ZJYL** (the 11:24:54 reopen had 6 traded seconds before the final halt —
  the 30-bar slot never completes, the +72 % jump is never seen). ⚠ Corrected in GH-13b: its BOTH-SIDES version catches
  ZJYL at 10:13:37 (+56 %), and PGHL at 09:42:03.
- **On the gap-DOWNS and the 10:00 gap-up, the FlushFader-style EWMA with the window trigger is the best stop**: D0930
  ew · win · 300 keeps 99.5 % of the no-stop profit (PF 1.29 → 1.30) and caps MULN (−328 → −109 %); D1000 ew · win · 400
  ADDS 5 % (7,479 → 7,933, PF 1.21) and caps COSM / WAL (−362 → −107 %). The present-bar clock only counts active
  trading and the EWMA smooths the noise, so it fires less on ordinary chop.
- **On U0930** the ruling already takes most of the tail; a later pb / clk window stop at 400 bp caps the worst at
  −104 / −111 % for −5 / −6 % of the profit — insurance, the user's call.
- ⚠ ~30 cells per variant on one sample: differences of 1–3 % in the sum are noise. The robust readings: win > uw; r5 is
  needed for halts; a later window stop on the gap-downs is nearly free and caps their tails (any of clk / pb / ew).

**GH-13b — ew · any (both sides) on ZJYL and on the book** (user: "Could ew any exited from ZJYL?"). Yes — at every
threshold 200–600 bp, at **10:13:37 @ \$12.74 (the short +56 %)**: the slot completing at the reopen after the 14.5-minute
pause spans it (\$14.48 at 09:58:54 → \$12.28 at 10:13:28: one |r| ≈ 1,600 bp), so the EWMA jumps past any threshold —
on a move in the short's FAVOUR (why "win" never fires), with ~\$1.6M trading that minute (a real fill). PGHL: 09:42:03 @
\$22.04 (−10 %; the ruling 09:35 @ \$22.57, −13 %). On the book (sum · worst · CVaR 1 %):

| variant | r5 (the ruling) | ew · any · 400 alone | ew · any · 600 alone | r5 + ew · any · 400 |
|---|---|---|---|---|
| U0930 | **22,626 · −130 · −50.7** | 21,827 · −130 · −51.6 | 23,460 · −190 (KODK 2020-07-29) · −54.5 | 21,627 · −130 · −49.8 |
| D0930 | 11,650 · −328 · −46.8 | **11,914 · −140 · −44.7** | 12,104 · −328 · −47.9 | 11,532 · −140 · −44.6 |
| U1000 | 8,173 · −77 · −25.7 | 7,900 · −77 · −25.9 | 8,133 · −79 · −26.4 | 7,810 · −77 · −25.6 |
| D1000 | 7,563 · −147 · −36.2 | **7,767 · −107 · −34.6** | 7,435 · −362 · −37.9 | 7,678 · −107 · −34.5 |

- **U0930: r5 is still the best tail-per-profit** — ew · any matches its worst only at 400 bp, for −3.5 % of the profit;
  at 600 bp it keeps more profit but lets KODK through (−190 %). Adding ew to r5 buys ~1 point of CVaR for −4 %.
- **D0930 / D1000: ew (any at 400, or win — GH-13) beats the 5-minute clock check on all three counts** (more profit, a
  smaller worst trade, a smaller CVaR). U1000: no difference.

**GH-13c — the EWMA stop everywhere: 300 / 350 / 400 bp** (user: "I am personally leaning towards exponential for
everything ... these differences are minor and probably noise, and the exponential itself seems very stable"). 350 bp
added to the pass. ew · any (both sides), sum · worst · CVaR 1 %:

| variant | none | r5 (clock ruling) | ew · any · 300 | **ew · any · 350** | ew · any · 400 |
|---|---|---|---|---|---|
| U0930 | 23,272 · −437 · −59.0 | 22,626 · −130 · −50.7 | 19,844 · −95 · −46.3 | **20,776 · −106 · −49.4** | 21,827 · −130 · −51.6 |
| D0930 | 11,844 · −328 · −50.6 | 11,650 · −328 · −46.8 | 10,735 · −109 · −42.7 | **11,598 · −113 · −43.1** | 11,914 · −140 · −44.7 |
| U1000 | 8,162 · −123 · −26.9 | 8,173 · −77 · −25.7 | 7,825 · −77 · −24.8 | **7,968 · −77 · −25.4** | 7,900 · −77 · −25.9 |
| D1000 | 7,479 · −362 · −38.6 | 7,563 · −147 · −36.2 | 7,376 · −160 · −34.0 | **7,655 · −107 · −34.1** | 7,767 · −107 · −34.6 |
| **all four: sum · the worst** | 50,757 · −437 | 50,012 · −328 | 45,780 (−8.5 %) · −160 | **47,997 (−4.0 %) · −113** | 49,408 (−1.2 %) · −140 |

(% vs r5.) **350 bp is the one threshold that holds every variant's worst trade to ≈ −113 % or better** (U0930 −106,
D0930 −113, U1000 −77, D1000 −107) for 4 % of the total profit vs the clock ruling (5.4 % vs no stop); PF 1.56 / 1.29 /
1.18 / 1.20, both halves of every variant ≥ 1.14. 300 costs twice as much for a smaller U0930 tail (−95 %); 400 is
nearly free but lets the D0930 tail back to −140 %. The window trigger (ew · win) does better on the gap-downs (D0930 350:
12,204 · −113) but misses ZJYL on U0930 (−251 %) — both sides is the one rule for all four.

**GH-13d — the EWMA stop's WARM-UP** (2026-11-22, user, after PGHL: "Sure" to a shorter warm-up). `scripts/equity/gaphv_volstop3.py`
→ `data/equity/gaphv/volstop3.{parquet,md}`. The ew · any stop needs W returns before it may fire; GH-13c's is W = 3 (the
4th 30-present-bar slot = the 120th traded second after the entry). **PGHL 2024-11-26 halted at 09:32:44 after 119 traded
seconds** with the EWMA already at 742 / 784 bp (slots ending 09:31:00 / 09:31:30): W = 3 armed only at the 09:42:02
reopen. W = 3 reproduces GH-13c exactly (the control). At 350 bp (sum · worst · CVaR 1 % · PF; fired / yr, of which within
5 min of the entry):

| variant | W = 3 (GH-13c) | W = 2 (the 3rd slot) | W = 1 (the 2nd slot) |
|---|---|---|---|
| U0930 | 20,776 · −106 · −49.4 · 1.56; 57 (36) | 20,459 · −106 · −47.4 · 1.56; 83 (61) | 19,458 · −106 · −44.4 · 1.56; 149 (127) |
| D0930 | 11,598 · −113 · −43.1 · 1.29; 57 (23) | 10,859 · −113 · −42.8 · 1.28; 83 (47) | 9,889 · −113 · −40.7 · 1.26; 154 (117) |
| U1000 | 7,968 · −77 · −25.4 · 1.18; 12 (3) | 7,847 · −77 · −25.2 · 1.18; 16 (5) | 7,778 · −77 · −24.5 · 1.18; 27 (14) |
| D1000 | 7,655 · −107 · −34.1 · 1.20; 11 (1) | 7,545 · −107 · −33.9 · 1.20; 14 (3) | 7,573 · −107 · −33.6 · 1.20; 23 (9) |
| **all four** | **47,998 · −113** | **46,710 (−2.7 %) · −113** | **44,698 (−6.9 %) · −113** |

- **The shorter warm-up costs profit and buys nothing in the backtest's tails**: the worst trade is the same in every
  variant; CVaR 1 % improves 2–5 points on U0930 / D0930. The extra fires are the open's chop: U0930's early W = 2 fires
  exit at −0.6 % where holding gave +3.5 %.
- **PGHL** at 350 bp: W = 3 09:42:03 @ \$22.04 (−10 %) · W = 2 09:31:31 @ \$20.38 (−2 %, 73 s before the first halt) · W = 1
  09:31:01 @ \$19.24 (+4 %). **ZJYL** the other way: W = 3 10:13:37 @ \$12.74 (+56 %) · W = 2 09:48:19 @ \$17.29 (+40 %) ·
  W = 1 09:37:44 @ \$20.75 (+28 %).
- ⚠ The backtest cannot price what W = 2 insures against: its fill takes the \$5k VWAP at the next reopen, so a position
  trapped by a halt BEFORE the stop arms costs only the reopen's price here (PGHL −10 %, filled in an 18 s window). Live,
  a halt into the close or a reopen that gaps up is the risk (PGHL's 09:48 halt ran to the close at 5×).

**GH-13d, the other way — LONGER warm-ups** (user: "What if we ... required at least 5 slots to be filled? What if we went
even higher?"). Same run, W = 4 … 20 (5 … 21 slots). All four at 350 bp, sum · the worst; the worst trades on U0930:

| W (slots) | all four | U0930's three worst |
|---|---|---|
| 2 (3) | 46,710 · −113 % | KODK 2020-07-29 −106 · … |
| **3 (4), GH-13c** | **47,998 · −113 %** | KODK −106 · JLHL 2026-08-05 −95 · PCSA 2025-12-17 −93 |
| 4 (5) | 48,724 (+1.5 %) · −136 % | PCSA −136 · CNST 2019-11-06 −128 · KODK −106 |
| 5 (6) | 48,936 (+2.0 %) · −251 % | **ZJYL −251** · CNST −128 · PHUN 2021-10-22 −125 |
| 7 (8) | 49,217 (+2.5 %) · −437 % | **PGHL −437** · ZJYL −251 · PHUN −219 |
| 10 (11) | 50,359 (+4.9 %) · −437 % | PGHL −437 · ZJYL −251 · PCSA −133 |
| 20 (21) | 50,655 (+5.5 %) · −437 % | PGHL −437 · ZJYL −251 · PCSA −134 (D1000: COSM 2022-12-16 −362 back) |

- **Every slot of warm-up past 4 trades the disasters for chop**: the profit rises steadily (the open's chop no longer
  fires the stop — no early fires at all from W = 10), but the stocks the stop exists for move in the first minutes and
  halt: ZJYL is never caught from W = 5 (−251 %), PGHL from W = 7 (−437 %; W = 5 fired at 09:48:02, 24 s before the
  lock-up at 09:48:26). U0930 carries all of it; D0930 / U1000 / D1000 barely move until W = 10–20.
- **W = 3 (4 slots) is the knee**: the most profit with every variant's worst at ≈ −113 % or better. W = 4 is +1.5 % for
  PCSA / CNST through (−136 / −128 %); W = 2 is −2.7 % for the halt insurance the backtest cannot price (above).

**GH-13e — the stop ARMED FROM A CLOCK TIME** (2026-11-23, user: "whether only activating the stop after 9:40 ET makes
things better"). `scripts/equity/gaphv_volstop4.py` → `data/equity/gaphv/volstop4.{parquet,md}`. GH-13c's ew · any stop (W =
3, 4 slots), computed from the entry as ever, may fire only from the gate. The 10:00 variants cannot fire before 10:00: the
gate moves U0930 / D0930 only. 09:30 reproduces GH-13d W = 3 (the control). At 350 bp (sum · worst · CVaR 1 % · PF halves;
fired / yr):

| gate | U0930 | D0930 | the two | all four |
|---|---|---|---|---|
| 09:30 (now) | 20,776 · −106 · −49.4 · 1.87 / 1.47; 57 | 11,598 · −113 · −43.1 · 1.33 / 1.28; 57 | 32,374 | 47,998 · −113 |
| 09:35 | 22,507 · −128 · −51.7 · 1.93 / 1.51; 30 | 12,026 · −113 · −44.2 · 1.34 / 1.29; 42 | 34,533 (+6.7 %) | 50,157 (+4.5 %) · −128 |
| **09:40** | 22,870 · −128 · −53.4 · 1.96 / 1.50; 20 | 12,013 · −129 · −46.4 · 1.35 / 1.28; 28 | 34,883 (+7.8 %) | 50,506 (+5.2 %) · −129 |
| 09:45 | 23,105 · −128 · −53.9 · 1.95 / 1.52; 15 | 12,187 · −129 · −47.8 · 1.34 / 1.29; 22 | 35,292 (+9.0 %) | 50,915 (+6.1 %) · −129 |
| 10:00 | 23,288 · −437 · −59.6 · 1.99 / 1.51; 8 | 12,222 · −129 · −48.5 · 1.34 / 1.29; 12 | 35,510 (+9.7 %) | 51,133 (+6.5 %) · −437 |

- **The early fires are mostly the open's chop**: U0930's 45 / yr fires before 09:40 end at −1.0 % stopped but +3.6 % held
  to the GH-6 exit (D0930: 38 / yr, +3.0 → +4.1 %). Gating them adds +7.8 % to the two 09:30 variants at 09:40; both
  halves' PF rise.
- **The price is the early disasters**: CNST 2019-11-06 −2 → −128 %, PHUN 2021-10-22 −58 → −125 % (U0930); YELL
  2023-08-01 −30 → −129 %, ENTA 2025-09-29 −7 → −96 % (D0930). The worst trade goes −106 / −113 → −128 / −129 and CVaR 1 %
  4 points worse. PGHL and ZJYL do not change until 09:45 (they fire at 09:42:03 / 10:13:37); 10:00 lets PGHL through
  (−437 %).
- **A gate beats a longer warm-up** (GH-13d): W = 5 bought +2.0 % for ZJYL through (−251 %); the 09:40 gate buys +5.2 % for
  −129 %. ⚠ The trades it costs are the ones that move and halt in the first minutes — what the backtest's reopen fill
  cannot price (GH-13d) — and the gate lengthens exactly that window.

**GH-13f — a TWO-LEVEL threshold** (2026-11-23, user: "Good idea, let's give it a try"). `scripts/equity/gaphv_volstop5.py` →
`data/equity/gaphv/volstop5.{parquet,md}`. Before the gate the stop fires only at E bp; from the gate at 350 (W = 3). The
idea: the open's chop rarely reaches 600–1,000 bp, the disasters might. All four variants (the 10:00 ones at today's stop):

| gate | E | all four | worst | CNST | PHUN | YELL | ENTA | PGHL | ZJYL |
|---|---|---|---|---|---|---|---|---|---|
| 09:30 (now) | — | 47,998 | −113 % | −2 | −58 | −30 | −7 | −10 | +56 |
| 09:35 | 600 | 50,189 (+4.6 %) | −128 % | −128 | −58 | −30 | −96 | −10 | +56 |
| 09:35 | ∞ | 50,157 (+4.5 %) | −128 % | −128 | −58 | −30 | −96 | −10 | +56 |
| 09:40 | 500 | 49,838 (+3.8 %) | −129 % | −128 | −58 | −129 | −96 | −10 | +56 |
| **09:40** | **1000** | **50,581 (+5.4 %)** | −129 % | −128 | −58 | −129 | −96 | −10 | +56 |
| 09:40 | ∞ | 50,507 (+5.2 %) | −129 % | −128 | −125 | −129 | −96 | −10 | +56 |
| 09:45 | 700 | 51,037 (+6.3 %) | −129 % | −128 | −58 | −129 | −96 | −10 | +56 |

- **The early level rescues PHUN only** (any E ≤ 1,000: −125 → −58 %). CNST, YELL and ENTA are not caught even at 500 bp:
  today's stop caught them before the gate at an ORDINARY level (350–500 bp), indistinguishable from the open's chop. The
  early disasters were not extreme-volatility events in their first minutes.
- So the two-level threshold is worth ~+0.1–0.2 % over the plain gate; **the choice is the gate itself**: +4.5–6.3 % of
  the profit for 3 more trades at −96 to −129 % (CNST, YELL, ENTA) in ten years, and a longer window for a halt before the
  stop can act (the risk the backtest's reopen fill does not price, GH-13d).

**GH-13g — a STEPPED schedule** (2026-11-23, user: "Past 9:45 we use the 350 bp stop. Past 9:40 we'll use a higher threshold
and past 9:35 a higher threshold still. It's going to be overfitting to some degree, but all skill is fitting").
`scripts/equity/gaphv_volstop6.py` → `data/equity/gaphv/volstop6.{parquet,md}`. The threshold by the clock: P before 09:35
(∞ or 1,000) · T1 09:35–09:40 · T2 09:40–09:45 (T1 ≥ T2) · 350 from 09:45; 79 schedules; each with the halves 2016–2020 /
2021–2026 and their ranks. All four variants:

| P · T1 · T2 | all four | halves (rank of 79) | worst | CVaR 1 % U0930 / D0930 |
|---|---|---|---|---|
| 350 · 350 · 350 (today) | 47,998 | 14,379 (#79) / 33,619 (#79) | −113 % | −49.4 / −43.1 % |
| 1000 · 700 · 700 (the best sum) | 51,132 (+6.5 %) | 15,459 (#35) / 35,673 (#1) | −129 % | −53.3 / −46.8 % |
| ∞ · 700 · 500 | 51,041 (+6.3 %) | 15,494 (#15) / 35,547 (#16) | −129 % | −53.2 / −46.8 % |
| ∞ · 700 · 450 | 51,001 (+6.3 %) | 15,513 (#5) / 35,487 (#23) | −129 % | −53.0 / −46.9 % |
| ∞ · ∞ · ∞ (GH-13e's 09:45 gate) | 50,916 (+6.1 %) | 15,343 (#73) / 35,573 (#9) | −129 % | −53.9 / −47.8 % |

- **Gating the open is robust; the schedule's numbers are not**: today's stop is LAST (#79) in both halves, every gated
  schedule beats it in both — but the halves' ranks of the 79 correlate −0.07, and NO schedule is in the top 10 of both.
  Between schedules the differences (≤ 0.5 %) are noise; any T1 ≈ 600–700, T2 ≈ 450–700 is the same stop.
- The early disasters are unchanged by any schedule (CNST −128, YELL −129, ENTA −96; PHUN −58 with any finite T1): GH-13f.

**GH-13h — the 10:00 variants skip a stock whose stop fired before 10:00** (user: "the 10 ET variant should only make an
entry if the volatility stop didn't trigger earlier"). `scripts/equity/gaphv_volstop7.py` → `data/equity/gaphv/volstop7.md`.
"Fired" = the stop computed from 09:30 (by the schedule) fired before 10:00 — knowable at 10:00. The trades kept as ever:

| skip if fired by | U1000 sum · PF · CVaR 1 % | skipped / yr · mean | D1000 sum · PF · CVaR 1 % | skipped / yr · mean |
|---|---|---|---|---|
| no filter (today) | 7,968 · 1.18 · −25.4 % | | 7,655 · 1.20 · −34.1 % | |
| today's stop (350 from 09:30) | 6,401 · 1.16 · −23.7 % | 95 · +1.6 % | 5,883 · 1.17 · −31.9 % | 104 · +1.7 % |
| ∞ · 700 · 500 | 7,789 · 1.18 · −24.7 % | 22 · +0.8 % | 7,091 · 1.19 · −33.2 % | 38 · +1.5 % |
| ∞ · ∞ · ∞ | 7,747 · 1.18 · −24.7 % | 21 · +1.1 % | 7,168 · 1.19 · −33.3 % | 36 · +1.4 % |

- **The filter skips PROFITABLE trades**: a stock whose early volatility tripped the stop is, at 10:00, a slightly better
  short than average (+1.6 % / +1.7 % vs the variants' +0.35 % / +0.47 %). The worst trade does not move (−77 / −107 %, the
  skipped ones' worst −44 / −68 %); CVaR 1 % improves 1–2 points for −20 % (today's stop) or −2 / −7 % (a gated schedule) of
  the profit. Not a tail filter.

## GH-14 — the ratchet cover on ZJYL: would the live exit have got out? (2026-11-21, user: "We need to do a fill sim on ZJYL. Would the FlushFader ratchet maker exits have worked to exit the trade with 3s, 5s and 30s moves?")

The private Oms's `scripts/cover_ratchet_sim.py` (the live ratchet exit rule: a buy cover rests at the bid, every step →
max(our price, the bid) + 1 tick, at most the ask − 1 tick, never down, only on a two-sided quote, the clock restarting on a
fill; LAT 0.216 s) against ZJYL's Massive quotes and its bulk-tape prints; fill models opt (any print ≤ our price) and
queue (lit prints below us; at our price after the displayed bid ahead of us); a reopening cross (a print after ≥ 60 s of
silence) at or below our limit fills at the cross price. 100 / 500 / 2,000 shares; the short from \$29.00.

| stop | the cover's path | fill (every step 3 / 5 / 30 s, every size, both models unless noted) | the short |
|---|---|---|---|
| **ew · any · 350 — 10:13:37** | joins the bid in the reopening crash (\$1.07M traded that minute) | **\$12.61, in 0.9–2.1 s**, no step needed | **+56.5 %** |
| r5 (clock, 5 min) — 09:35:00 | the stock was in a LULD pause 09:32:06–09:37:40; the cover rests at the last bid | the 09:37:40 reopening cross, \$22.51 | +22.4 % |
| clk · win — 10:14:30 | pinned at the \$13.43 band, 11,400+ shares bid ahead of us; paused 10:14:33–11:24:54 | opt: 100 sh at \$13.43 (doubtful); queue: nothing before the pause — the SIP kept quoting through it (\$13.43 / 13.44, then locked \$23.49 / 23.50 before the reopen), the ratchet steps on those quotes (15–23 steps) to above the cross → **the 11:24:54 reopening cross at \$23.00** (23,937 shares) | +20.7 % |

- **The step size did not matter on any of the three**: the EWMA stop fills at once (a crash INTO our bid), the other two
  in a reopening cross. A faster step only climbs faster through a pause's quotes.
- **What the cross fills depend on, live** (to confirm, not assumed): the Oms keeps stepping during a LULD pause (it steps on
  any two-sided NBBO — there were quotes); Lightspeed accepts the replaces during a pause; and the resting cover takes part in
  the listing's reopening auction.
- One stock, a favourable one for the EWMA stop (it fired into a crash). ⚠ The 16:00:00.03 \$25.00 print is a stale
  market-centre official-close print (condition 15), not a cross.

## GH-15 — the QUOTES DURING THE HALTS (2026-11-21, user: "what were the quotes like during the halts? We should study that")

The private Oms's `scripts/halt_quotes.py` (Massive v3 quotes with every field, the bulk tape's prints; a halt = a silence
in the RTH prints ≥ 60 s) → `out/halt_quotes_{zjyl,pghl}.md`. ZJYL 2023-12-18: 6 halts (5 LULD-style pauses of 5–70 min,
then 11:25 → the close); PGHL 2024-11-26: 3 (two pauses, then 09:48 → the close).

**One structure in all nine:** the moment the prints stop, every other venue's quote is wiped (a 0.00 / 0 update) and the
NBBO becomes a single **LOCKED quote on Nasdaq (the listing market), 100 × 100, bid = ask**, re-stamped at regular
sub-second marks (…:xx.32 / .34). **It is the auction's indicative price: the reopening print equalled the last locked
quote exactly in every one of the eight reopens** — ZJYL \$22.51, \$18.87, \$16.09, \$12.34, \$23.00; PGHL \$22.50, \$20.23 —
and the 15:50:00 locked quote equalled the official close (ZJYL \$101.87, PGHL \$107.36).

| halt | length | last print | the locked indicative's path | reopen |
|---|---|---|---|---|
| ZJYL 4 (09:58:54 →) | 14.6 min | \$14.48 | \$1.26 → 5.00 → 7.00 → 9.00 → 8.00 → 10.00 → 12.34 | \$12.34 |
| ZJYL 5 (10:14:33 →) | 70.3 min | \$13.43 | 14.10 → 19.20 → 23.85 → **25.92** (10:32–10:43) → 23.15 → 20.91 → 22.00 → 23.00 | \$23.00 |
| ZJYL 6 (11:25:09 → close) | 4.6 h | \$25.30 | blank to 12:30, then \$742 → 487 → 476 → 133 → 120 → 105 → 98.50 → 105 → **101.87** at 15:50 | the close cross \$101.87 |
| PGHL 3 (09:48:26 → close) | 6.2 h | \$20.95 | \$199,999 / 70 / 30 / 50 → 20.96 → 28.00 by 09:52:47; then BLANK for 5¾ h; 15:36–15:41 \$199,999 / 1,999 / 999 / 350 / 200 / 174.75; 15:50:00 **\$107.36** | the close cross \$107.36 |

Reading:
- **During a halt the NBBO is not a market** — no other venue quotes, nothing trades; the "quote" is the listing market's
  indicative price, uncollared (\$199,999, \$1,999 from absurd orders), sometimes blank for hours (PGHL 09:52 → 15:36).
- **It is visible in real time and it IS the reopen price** — a live halt detector (prints stopped + a locked 100 × 100
  listing-venue quote) also sees where the stock will reopen, minutes ahead.
- 🛑 **The live ratchet would sit ONE TICK BELOW the cross.** On a locked quote its step is min(max(ours, bid) + 1 tick,
  ask − 1 tick) = the indicative − 1 tick; a buy limit below the cross price does not fill in the cross. GH-14's ZJYL
  10:14:30 cover filled at the \$23.00 reopen only because the indicative had been to \$25.92 earlier (the ratchet never
  steps down: its limit stayed \$25.91) — luck. On a cross that reopens at its highest indicative it misses, and the
  position rides into the next pause.
- ⇒ For the disaster stop, a halt needs its own rule: join the reopening cross with a limit AT (or a band above) the
  indicative — an auction order with a limit, like the LOO / MOC we already send, not a taker order in continuous trading
  (the user's call). To verify live: the Oms's NBBO in a halt, Lightspeed's replaces during a pause, and the reopening
  auction participation.

### GH-15 → the HALT EXIT (2026-11-21, user: "We need to build in halt exits into the OMS. If the volatility stop has triggered and the stock halts we absolutely have to exit on the next reopen")

**The time between a reopen and the next halt** (the bulk tape, RTH):

| ZJYL window | from → to | length | shares (lit) | \$ | price | the reopening cross |
|---|---|---|---|---|---|---|
| the open | 09:30:01 → 09:32:39 | 158 s | 33,632 (28,122) | \$0.87M | \$29.00 → 23.40 | |
| after halt 1 | 09:37:40.56 → 09:38:11.10 | **30.5 s** | 94,463 (72,232) | \$1.94M | \$22.51 → 20.26 | 4,014 sh @ \$22.51 |
| after halt 2 | 09:48:11.23 → 09:48:31.51 | **20.3 s** | 98,672 (79,679) | \$1.72M | \$18.87 → 16.98 | 15,520 sh @ \$18.87 |
| after halt 3 | 09:58:39.74 → 09:58:54.21 | **14.5 s** | 20,585 (19,692) | \$0.31M | \$16.09 → 14.48 | 6,386 sh @ \$16.09 |
| after halt 4 | 10:13:28.32 → 10:14:33.47 | **65.1 s** | 136,178 (108,966) | \$1.69M | \$12.34 → 13.43 (\$11.20–13.57) | 27,025 sh @ \$12.34 |
| after halt 5 | 11:24:54.32 → 11:25:09.80 | **15.5 s** | 29,738 (27,867) | \$0.69M | \$23.00 → 25.30 | 23,937 sh @ \$23.00 |

PGHL: the open 164 s; after halt 1 **18.3 s** (\$1.60M, \$22.50 → 20.25, the cross 22,313 sh); after halt 2 **65.6 s**
(\$2.99M; the cross 3,818 sh @ \$20.23), then halted to the close.

- **15–65 seconds between a reopen and the next halt** — one to two steps of a 30 s ratchet, a handful of a 3 s one. The
  liquidity is there (\$0.3–1.9M per window), but it moves 10 % in those seconds. **The reopening cross itself is the
  exit**: 4k–27k shares each, at the price the locked indicative quote announced minutes ahead.
- ⇒ The build: a stopped position whose stock halts joins the next reopening cross (a cross order, limited at a band above
  the indicative); if the cross misses, the window after it is worked at the shortest step; repeat at every reopen until flat.

**GH-15b — the ratchet in the windows between halts, the cross MISSED** (user: "If we were stuck and had only 15-60s to exit
... would the ratchet orders have triggered in time?"). The private `scripts/halt_window_sim.py` → `out/halt_window_sim_{zjyl,pghl}.md`:
the live ratchet cover from just after each reopening cross to the next halt's last print, steps 1 / 3 / 5 / 30 s, 100 /
500 / 2,000 shares, opt / queue fills; start = what the live ratchet holds out of the halt (the last indicative − 1 tick)
or a fresh bid; no cross fill.

| window | length | after the cross | the ratchet cover |
|---|---|---|---|
| ZJYL 1 / 3, PGHL 1 / 2 | 14.5–65.6 s | the price FELL into our bid | filled in full (2,000 sh) within 0.2–3.0 s, every step, both models |
| ZJYL 2 | 20.3 s | fell \$18.87 → 16.98 | full in 1.1 s from the held price; a FRESH cover at 30 s steps never fills (stuck at the \$16.10 bid below the market) |
| ZJYL 4 | 65.1 s | \$11.20 – 13.57, two-way | full within 2–14 s at every step |
| **ZJYL 5 — the squeeze into the final halt** | **15.5 s** | **\$23.00 → 21.00 → 25.30**, only 5,401 shares after the cross | **1–3 s steps: 100 sh (opt) / 17 sh (queue); 500 – 2,000 sh: 217 (opt) / 17 (queue); 5 s: 13 sh; 30 s: NOTHING** |

- **The ratchet worked in every window where the stock fell after the reopen — and failed in the one that mattered**: in
  the upward squeeze a passive buy fills only if someone sells into it, and almost nobody did (17 lit shares at our price in
  the queue model). The position would have been trapped into the \$101.87 close.
- ⇒ The maker ratchet cannot be the halt exit's fallback in a squeeze. The reopening cross is the exit (ZJYL's last one:
  23,937 shares at \$23.00, the price its locked indicative had shown for minutes). A cross missed means waiting for the
  next reopen — or a marketable order, which the no-taker rule forbids: the user's decision.

**GH-15c — a LIVE halt detector** (user: "How will we detect halts in the OMS? Crossed quote + no trades in the last
minute?"). The private `scripts/halt_detect_check.py` → `out/halt_detect_check.md`: rules evaluated second by second over
RTH on ZJYL / PGHL and 20 random thin U0930 / U1000 event days (vol60 < 500k shares, 2021+); truth = silences ≥ 60 s with the
NBBO locked / empty ≥ 80 % of them. **Crossed never happens in a halt (0 % of the halt quotes, GH-15) — the signature is
LOCKED (bid = ask, the listing venue's indicative) or EMPTY (0 / 0).**

| rule | the 11 halts found (ZJYL 6, PGHL 3, SBLX 2025-10-14 2): delay after the last print | false-alarm episodes on 22 days |
|---|---|---|
| no print ≥ 10 s | 10 s | 9,536 |
| no print ≥ 30 s | 30 s | 2,474 |
| no print ≥ 60 s | 60 s | 693 |
| **no print ≥ 5 s AND the NBBO locked or empty** | **3–8 s** | **1** |
| … ≥ 3 s / 10 s / 20 s | 3–8 / 10 / 20 s | 2 / 1 / 1 |

- **Prints alone are useless on thin names** (a 60 s silence is routine: 693 false episodes). The locked / empty NBBO is what
  makes it a halt: outside the halts it is locked / empty 0–0.6 % of the time.
- The one "false alarm" (PGHL 09:32:01 → 09:32:44, locked \$21.90 / 21.90) is almost certainly the real start of its first
  halt, split from the 558 s silence by one stray print at 09:32:44.52.
- FlushFader's S40x halt classifier (HaltsToday) cannot serve: it classifies a silence only at the REOPEN and needs an
  active pre-hole tape (5 m range ≥ 4 %, < 2 missing seconds in the prior minute; user: "isn't foolproof").
- ⇒ **The rule: HALTED ⇔ no print for ≥ 5 s AND the last NBBO locked (bid = ask > 0) or empty; cleared by the next print;
  the locked price = the indicative.** The user's design: any exit leg (L2 target 0) on a halted ticker → the reopening-
  auction order (its type to ask the broker on Monday 11-23: MOO / LOO-style during a halt?), no need to know a stop
  fired. To verify live: the Oms's quote feed delivers the locked / wiped NBBO in a halt (Massive WS Q).

## GH-16 — the 10:00 entry: halts and the plunge from 09:30 (2026-11-23, user: "we need to at least avoid entering stocks like ZJYL that are being halted. In ZJYL's case in particular we need to avoid entering stocks that have plunged down too much. Maybe we'll introduce a slope and a z score filter from 9:30 to 10 ET")

`scripts/equity/gaphv_entry10.py` → `data/equity/gaphv/entry10.{parquet,md}`. ZJYL 2023-12-18 missed U1000 only by its gap
(3.5 % < 4 %): −50 % from the open at 10:00, halted (09:58:54 → 10:13:28). Features from the 1s bars BEFORE 10:00 (knowable at
the entry): the longest silence; HALTED AT 10:00 (the silence running into 10:00 ≥ 60 s); the HALTS (silences ≥ 290 s); the
SLOPE (OLS of ln 30 s slot vwap, bp / min); Z (the last slot's ln vwap vs the window's mean, in sd); ret30, volat30. The
trades: GH-6 exit + today's stop from 10:00.

- **An ABSOLUTE plunge is the best short, not the worst**: U1000's steepest-falling 1 % (slope < −69 bp/min) +4.26 % · PF 1.98;
  ret30 < −19 % +2.54 % · PF 1.52 (D1000 alike: +3.15 % / +3.78 %). A slope or ret30 floor would cut the most profitable
  trades.
- **Z — the price STRETCHED to the bottom of its own 30 minutes — is the losing band in both variants and both halves**:

| skip | U1000: n · mean (halves) · Σ after | D1000: n · mean (halves) · Σ after |
|---|---|---|
| z < −2.5 | 154 · −1.24 % (−1.89 / −0.75) · 8,160 (+2.4 %) | 118 · −0.67 % (−0.73 / −0.63) · 7,734 (+1.0 %) |
| **z < −2.35** | 234 · −1.04 % (−1.78 / −0.53) · 8,211 (+3.0 %) | 161 · −0.75 % (−1.18 / −0.48) · 7,776 (+1.6 %) |
| z < −2.0 | 605 · −0.28 % (−1.02 / +0.18) · 8,135 (+2.1 %) | 425 · −0.29 % (−0.31 / −0.28) · 7,779 (+1.6 %) |
| halted at 10:00 | 226 · −0.64 % (−0.90 / −0.37) · 8,113 (+1.8 %) | 161 · +0.98 % (−0.29 / +1.66) · 7,497 (−2.1 %) |
| z < −2.35 or halted at 10:00 | 448 · −1.00 % (−1.55 / −0.55) · 8,416 (+5.6 %) | 318 · +0.13 % · 7,613 (−0.6 %) |

- **The cut-off** (user: "z < −2.0 — let's gate these trades out. What about z < −1.5?"): the band [−2.0, −1.5) is
  PROFITABLE in both variants (U1000 [−2.0, −1.75) +0.76 %, [−1.75, −1.5) +0.73 % with both halves positive; D1000 +0.17 % /
  +1.26 %). z < −1.5 skips 2,010 / 1,389 trades at +0.43 % / +0.51 % and costs −11.0 % / −9.3 % of the profit; z < −2.0
  adds +2.1 % / +1.6 %. ⭐ **Ruling: skip z < −2.0** on both 10:00 variants.
- **Halts are a GAP-UP problem**: U1000 halted at 10:00 after ≥ 2 halts — the ZJYL / PGHL profile (both: halted at 10:00, 3
  halts; z −1.79 / −0.20, so z alone misses them) — 36 trades, −2.98 %, PF 0.66, both halves negative. D1000's halting
  stocks are good shorts (2+ halts: +3.49 % / +2.85 %).
- None of these filters moves the worst trade (U1000 −77 % LIFW, D1000 −107 % XELA: neither plunged nor halted).
- ⚠ The backtest drops a 10:00 trade with no fill to the close (U1000 4, D1000 1 — PGHL 2024-11-26: halted 09:48 → close);
  live it would be an order waiting for the reopen (the Oms's halt freeze: HaltWait).
- ⚠ "Halted at 10:00" here is a 60 s silence (a thin name's quiet minute counts); live, the Oms's haltOf (quote rule + LULD) is
  the real thing.

## GH-17 — the 10:00-eligible stocks shorted at the FIRST NEW SESSION HIGH after 10:00 (2026-11-23, user: "how a system that shorts 10 ET eligible stocks at the session highs would do?")

`scripts/equity/gaphv_newhigh.py` → `data/equity/gaphv/newhigh.{parquet,md}`. The user's choices: one trade per stock-day at the
first 1s bar after 10:00 above the 09:30–10:00 high (the 1s-vwap high; the premarket ignored), to 15:00; the \$5k fill from the
next second; the variant's GH-6 rule at the marks after the entry + the 350 bp stop from the entry.

| book | U1000: n · PF · mean · Σ · worst · PF halves | D1000: same |
|---|---|---|
| the 10:00 entry (today) | 22,814 · 1.18 · +0.35 % · 7,968 · −77 % · 1.14 / 1.20 | 16,267 · 1.20 · +0.47 % · 7,655 · −107 % · 1.22 / 1.19 |
| **the first new high after 10:00** | 10,440 · 1.06 · +0.11 % · 1,134 · −86 % · 0.92 / 1.12 | 6,627 · 1.10 · +0.21 % · 1,392 · −215 % · 1.03 / 1.13 |
| new highs from 10:00–10:30 only | 6,137 · 1.23 · +0.44 % · 2,708 · −86 % | 3,605 · 1.16 · +0.37 % · 1,320 · −102 % |
| (hindsight split) the 10:00 entry on the stock-days that later make a new high | 10,440 · 0.22 · −2.44 % · −25,514 | 6,627 · 0.26 · −3.27 % · −21,683 |
| (hindsight split) … on those that never do | 12,374 · 3.94 · +2.71 % · 33,483 | 9,640 · 4.23 · +3.04 % · 29,339 |

- **Shorting the new high is a much weaker system**: 46 % / 41 % of the stock-days break their 09:30–10:00 high, and a short
  there makes +0.11 % / +0.21 % a trade — a seventh / a fifth of the 10:00 entry's Σ. Only the breakouts of the first half
  hour pay (PF 1.23 / 1.16); from 10:30 on U1000 loses in 9 of 10 half hours. A new high after the first half hour is
  momentum, not exhaustion. D1000 adds COSM 2022-12-16 at −215 %.
- The hindsight split (NOT tradeable — it conditions on the rest of the day) shows where the 10:00 entry's edge lives: the
  stock-days that never take out their first-half-hour high (PF ~4); those that do lose −2.4 % / −3.3 % from 10:00.

## GH-18 — the 10:00 shorts with a STOP AT THE RUNNING SESSION HIGH (2026-11-23, user: "If it has a low or a negative PF1 we could use that justification to put a stop at the session high past 10:30 ET")

The justification (GH-17): the first-new-high shorts entered 10:30–15:00 — the path a 10:00 short holds through such a break —
U1000 4,303 · PF 0.78 (0.69 / 0.83), D1000 3,022 · PF 1.01 (1.00 / 1.02; 0.92 from 11:00). `scripts/equity/gaphv_histop.py` →
`data/equity/gaphv/histop.{parquet,md}`: today's 10:00 book + a cover when a 1s bar from the arming time trades above the
session high so far (the bars before) by more than a buffer; the \$5k fill from the next second; the first exit wins.

| book | U1000: Σ · PF (halves) · worst · CVaR 1 % · fired | D1000: same |
|---|---|---|
| today | 7,968 · 1.18 (1.14 / 1.20) · −77 % · −25.4 % | 7,655 · 1.20 (1.22 / 1.19) · −107 % · −34.1 % |
| armed 10:00 | 7,087 (−11.1 %) · 1.18 · −38 % · −18.2 % · 31 % | 5,954 (−22.2 %) · 1.18 · −54 % · −20.9 % · 42 % |
| **armed 10:30** | 8,061 (+1.2 %) · 1.18 (1.15 / 1.20) · −77 % · −24.2 % · 5 % | 6,606 (−13.7 %) · 1.18 (1.21 / 1.16) · −54 % · −24.4 % · 33 % |
| armed 11:00 | 8,079 (+1.4 %) · 1.18 · −77 % · −24.8 % · 2 % | 7,056 (−7.8 %) · 1.19 · −64 % · −27.0 % · 28 % |
| armed 10:30, buffer 1 % | 7,764 (−2.6 %) · 1.18 · −77 % · −24.9 % · 1 % | 6,588 (−13.9 %) · 1.17 · −65 % · −30.5 % · 4 % |

- **U1000: nearly free and nearly idle** — it fires on 5 % (the VWAP rule has usually exited first): +1.2 %, the covered
  trades −6.57 % against −6.66 % held; the worst (LIFW −77 %) is not touched (no new high after 10:30).
- **D1000: a TAIL stop that costs profit** — a gap-down stock makes a new session high after 10:30 on a third of the days
  (the bounce from a low open), and covering there costs ~0.2 % a covered trade (−5.03 % against −4.84 % held): −13.7 % of the
  profit. It halves the tail: worst −107 → −54 %, CVaR 1 % −34.1 → −24.4 %; XELA −107 → −6 %, CLDX −91 → −2 %, ENTA −68 → −18 %.
- The 1 % buffer is worse in every cell (it fires late, after the break has run). Arming at 10:00 cuts the tail most but
  costs −11 % / −22 %.

## GH-19 — STOP METHODS on the new baseline (2026-11-23, user: "we should test out different stop methods ... Exiting past 10 ET or 10:30 ET if the stock is higher than the 30m high or the 60m high. The 10:30 ET variant that exits if the 30m high hasn't been breached between 10 and 10:30 ET ... is a genuine long momentum setup")

`scripts/equity/gaphv_stops.py` → `data/equity/gaphv/stops.{parquet,md}`. **The baseline** (the user's): each variant's GH-6 rule +
the volatility stop — 09:30 entries off · 700 · 500 · 350 by the clock (GH-13g), 10:00 entries 350 from the entry — + the
10:00 variants only when z > −2 (GH-16). It reconciles: U0930 + D0930 = 35,417 (GH-13g's ∞·700·500), U1000 / D1000 = GH-16's
z < −2.0 rows. Each method adds an exit (the first wins; the \$5k fill). H30 = the 09:30–10:00 high, H1000 = the 10:00–10:30
high (1s vwap): **A** above H30 from 10:00 · **B** above H30 from 10:30 · **C** above the 60m high max(H30, H1000) from 10:30
· **D** as B only when the 10:00 bar did NOT breach H30 (then the 60m high is H30: D = C on those days) · **E** at a mark the
bar just completed has a higher vwap than the bar before (the user's idea) · **F** out at 10:30 when the 10:00 bar breached
H30 · **D+F**. Σ (the worst trade):

| variant | baseline | + A | + B | + C | **+ D** | + E | + F | + D+F |
|---|---|---|---|---|---|---|---|---|
| U0930 | 23,173 (−128) | 20,761 (−104) | 22,718 (−128) | 23,524 (−128) | **23,636 (−128)** | 19,391 | 21,486 | 21,949 |
| D0930 | 12,244 (−129) | 10,746 (−77) | 11,280 (−106) | 11,662 (−106) | **12,289 (−129)** | 8,050 | 10,596 | 10,641 |
| U1000 | 8,135 (−77) | 7,255 (−38) | 8,210 (−77) | 8,241 (−77) | **8,248 (−77)** | 5,133 | 7,842 | 7,954 |
| D1000 | 7,779 (−107) | 6,036 (−54) | 6,251 (−54) | 6,674 (−54) | **7,427 (−107)** | 1,919 | 6,076 | 5,724 (−54) |
| all four | 51,332 | 44,798 (−12.7 %) | 48,459 (−5.6 %) | 50,101 (−2.4 %) | **51,599 (+0.5 %)** | 34,492 (−32.8 %) | 46,001 (−10.4 %) | 46,268 (−9.9 %) |

- **The 10:00 bar's breach of H30 splits every book** (known at 10:30): breached 23–28 % of the stock-days, baseline PF
  0.14 / 0.18 / 0.27 / 0.48 (U0930 / D0930 / U1000 / D1000); not breached 3.89 / 2.50 / 2.00 / 1.60. But the breached days'
  loss is mostly in BEFORE 10:30: covering them at 10:30 (F) is worse than holding (U0930's covered −7.55 % vs −6.92 % held).
- **D (the user's) is the only method that adds**: +2.0 % U0930, +0.4 % D0930, +1.4 % U1000, −4.5 % D1000; PF up in both
  halves on three; CVaR 1 % better (D0930 −46.8 → −41.6 %, D1000 −33.2 → −29.0 %); no worst trade moves. On the not-breached
  days a first break of H30 after 10:30 is the momentum GH-17 measured (the shorts at it: PF 0.78 on U1000).
- **A is the tail cutter** (worst −104 / −77 / −38 / −54 %) at −12.7 % of the profit; C sits between (U-variants +1.3–1.5 %,
  D-variants −4.8 / −14.2 %).
- **E (a bar's vwap above the previous bar's) fires on 46–99 % of the trades and costs a third of the profit** — a rising bar
  is ordinary noise in a fading stock. Rejected.
- The halt proxy on the new baseline: U1000 +2.1 %, D1000 −1.8 % (unvalidated, GH-16).

## GH-20 — the break of the 10 ET high after 10:30, from the LONG side (2026-11-23, user: "Let's consider this from the long side ... look for the first break above the 10 ET high past the 10:30 ET period and look back whether that level was also breached during 10-10:30 ET")

`scripts/equity/gaphv_break30.py` → `data/equity/gaphv/break30.{parquet,md}`. The universe: the 10:00-eligible stock-days, kept
when z > −2 and the 09:30 volatility stop (off · 700 · 500 · 350) has not fired before 10:00 (U1000 21,990, D1000 15,474).
H30 = the 10 ET high (the 09:30–10:00 1s-vwap high). Groups: **fresh** (not above H30 during 10:00–10:30, first crossing
after 10:30) · **re-break** (above it during 10:00–10:30, back below, crossing again after 10:30) · **still above** (above it
during 10:00–10:30 and still at 10:30: entry 10:30) · never. The trade: LONG from the \$5k VWAP after the entry, out at
15:30, ± the 350 bp stop from the entry. No VWAP rule.

| group | U1000: share · n · PF (+ stop) · mean · PF halves | D1000: same |
|---|---|---|
| fresh | 19 % · 4,176 · 1.12 (1.15) · +0.27 % · 1.21 / 1.08 | 19 % · 2,895 · 0.97 (0.97) · −0.05 % · 1.01 / 0.95 |
| re-break | 9 % · 2,047 · 1.09 (1.09) · +0.24 % · 1.20 / 1.05 | 8 % · 1,194 · 1.04 (1.05) · +0.07 % · 1.12 / 1.01 |
| still above | 14 % · 3,012 · 1.00 (0.99) · +0.00 % · 1.07 / 0.97 | 11 % · 1,751 · **0.73** (0.74) · −0.70 % · 0.76 / 0.72 |
| never | 58 % | 62 % |

- **On the gap-ups a fresh break after 10:30 is mild momentum**: the long makes +0.27 % (+0.33 % with the stop), PF 1.12–1.15,
  both halves > 1; a re-break about the same (1.09); a break still in force at 10:30 nothing (1.00).
- **On the gap-downs none is momentum**: fresh 0.97, re-break 1.04 — and a break still in force at 10:30 FADES (the long
  −0.70 %, PF 0.73, both halves): a short held there makes +0.70 %.
- For the shorts' stops this is the forward path a short holds through each break: on U1000 a fresh break costs a held
  short ~0.3 % (GH-19's D: +1.4 %), on D1000 nothing (D −4.5 %), and the still-above days should NOT be stopped (A / C cost
  D1000 14–22 %).

## GH-21 — when the "still above the 10 ET high" short plays out, and when being above H30 turns neutral (2026-11-23, user: "For gap downs, maybe the stop simply needs to be a bit further out in time ... how long does it take for it to play out. Maybe if the stock is still above the 30m high by 11 ET or 11:30 ET the expectancy becomes neutral and that is the point to stop out?")

`scripts/equity/gaphv_still_above.py` → `data/equity/gaphv/still_above.md` (no tape pass: the marks' prices and \$5k fills, H30
from GH-20). GH-20's eligible universe; forward SHORT returns, no stop, no exit rule.

**The still-above group's short from 10:30** (mean to each mark): D1000 (1,751) +0.12 % 11:00 · +0.29 % 11:30 · +0.37 % 12:00 ·
+0.51 % 13:30 · +0.70 % 15:30 (PF 1.37) — it plays out steadily all day (≈ half by 11:30–12:00). U1000 (3,012) +0.28 % 11:00 ·
+0.38 % 11:30 · then decays to −0.01 % by 15:00: an hour's fade, then nothing.

**Above H30 at a mark → the short from that mark to 15:30** (mean · PF · halves):

| mark | D1000 above H30 | D1000 not above | U1000 above H30 | U1000 not above |
|---|---|---|---|---|
| 10:30 | +0.70 % · 1.37 · +0.50 / +0.82 | +0.35 % · 1.17 | −0.01 % · 1.00 · −0.19 / +0.10 | +0.10 % · 1.04 |
| 11:00 | +0.59 % · 1.34 · +0.34 / +0.74 | +0.33 % · 1.18 | **−0.31 % · 0.90 · −0.32 / −0.31** | +0.11 % · 1.05 |
| 11:30 | +0.57 % · 1.35 · +0.27 / +0.75 | +0.25 % · 1.15 | −0.27 % · 0.90 · −0.29 / −0.26 | +0.08 % · 1.04 |
| 12:00 | +0.31 % · 1.20 | +0.24 % · 1.16 | −0.18 % · 0.92 | +0.10 % · 1.06 |
| 13:00 | +0.26 % · 1.21 | +0.18 % · 1.14 | −0.10 % · 0.95 | +0.10 % · 1.07 |
| 13:30 | +0.13 % · 1.12 · −0.01 / +0.22 | +0.15 % · 1.14 | −0.17 % · 0.91 | +0.08 % · 1.07 |
| 14:30 | +0.00 % · 1.00 · −0.11 / +0.07 | +0.11 % · 1.15 | −0.13 % · 0.90 | +0.05 % · 1.06 |

- **Gap-DOWNS: above the 10 ET high is the short's FRIEND until ~13:00** (a bounce into resistance that fades: better than
  the rest at every mark to 13:00), and only neutral after ~14:00 — never negative. There is no point at which a stop above
  H30 pays: the user's "neutral by 11 / 11:30" does not happen on D1000; the neutral point is ~14:00.
- **Gap-UPS: above the 10 ET high from 11:00 on is a losing short** (−0.31 % at 11:00, PF 0.90, both halves; negative at every
  mark to 14:30), neutral at 10:30. ⇒ a mark-based stop — at 11:00 or any later mark, cover when above H30 — fits U1000.

## GH-22 — the stop's REFERENCE (the 30m high vs the 30m VWAP) and TRIGGER (1s break vs the mark's last price vs the 30m bar's VWAP) (2026-11-23, user: "whether using the 30m VWAP price as the reference instead of the 30m high might work better? Also, instead of using 1s breaks as the triggers, what if we used the 30m bar VWAP prices as the triggers instead?")

`scripts/equity/gaphv_refs.py` → `data/equity/gaphv/refs.{parquet,md}`. H30 = the 09:30–10:00 high; V30 = the 09:30–10:00 bar's
VWAP. Triggers: BRK (a 1s break), LAST (the last price at a half-hour mark), BAR (the VWAP of the 30-minute bar ending at the
mark). Armed from 10:30 or 11:00. THE BOOK: GH-20's eligible universe (z > −2, the 09:30 stepped stop not fired before 10:00);
the 10:00 short held to 15:30 with the 350 bp stop — NO VWAP rule (user: "forget the VWAP rule") — plus one cover.

| U1000 book | Σ | PF (halves) | worst | CVaR 1 % | fired |
|---|---|---|---|---|---|
| baseline (to 15:30 + the stop) | 6,014 | 1.10 (1.06 / 1.11) | −249 % | −54.5 % | |
| (for reference: GH-19's baseline WITH the GH-6 VWAP rule, z > −2) | 8,135 | 1.19 (1.17 / 1.21) | −77 % | −25.3 % | |
| H30 · BRK · from 10:30 | 7,703 (+28 %) | 1.15 | −77 % | −24.4 % | 43 % |
| H30 · BRK · from 11:00 | 8,591 (+43 %) | 1.16 | −111 % | −28.6 % | 40 % |
| H30 · LAST / BAR · from 11:00 | 7,658 / 7,342 | 1.14 / 1.13 | −111 % | −31.5 / −32.5 % | 33 % |
| V30 · BRK · from 10:30 | 7,058 (+17 %) | 1.19 | −77 % | −21.9 % | 73 % |
| **V30 · LAST · from 10:30** | 8,378 (+39 %) | 1.20 (1.17 / 1.22) | −77 % | −23.9 % | 65 % |
| V30 · BAR · from 10:30 | 7,969 (+33 %) | 1.19 | −77 % | −24.3 % | 66 % |
| **V30 · BAR · from 11:00** | 9,108 (+51 %) | 1.20 (1.17 / 1.21) | −111 % | −28.5 % | 62 % |
| GH-19 D | 7,352 (+22 %) | 1.13 | −249 % | −44.5 % | 20 % |

- **Without the VWAP rule U1000 needs a replacement**: held to 15:30 it is PF 1.10, worst −249 %, CVaR −54.5 % (the GH-6 VWAP
  rule — out when the last half hour was mostly above the SESSION VWAP — had been doing that job). Every level stop helps.
- **The 30m VWAP is the better reference on the gap-ups**: above V30 at a mark the forward short loses in both halves from
  10:30 (−0.14 to −0.24 %, PF 0.90–0.95, 44 % of the trades); V30 · LAST from 10:30 matches the VWAP-rule book (8,378 · 1.20 ·
  −77 % vs 8,135 · 1.19 · −77 %), V30 · BAR from 11:00 makes the most (9,108) for a worse tail (−111 %).
- **The trigger depends on the level**: on V30 a mark (LAST / BAR) beats a 1s break (the price crosses its 30m VWAP all the
  time: BRK fires on 73 % and covers early); on H30 a 1s break beats a mark (a break of the high is a real event).
- **D1000: no stop of either kind helps** — every row costs 17–48 % (baseline 7,280 · 1.20 · −107 %; GH-19's D −2.6 %). Above
  the 30m VWAP or high, a gap-down short keeps its edge (Part 1: +0.30 to +0.70 % at 10:30–11:00) — GH-21's finding.
- ⚠ The eligibility filter "the 09:30 stop not fired before 10:00" removes 227 / 380 stock-days that GH-13h found PROFITABLE
  (D1000's baseline here 7,280 vs 7,779 with the z filter alone).
- **The stop's EWMA from 09:30 instead of restarted at 10:00** (user: "I'd like it if we used the same EWMA of volatility from
  9:30 throughout instead of restarting it at 10 ET"; `--ew0930` → `data/equity/gaphv/refs_ew0930.md`; the universe already
  excludes a 09:30 stop fired before 10:00, so the one EWMA's first firing is the cover). It hardly matters: U1000 baseline
  6,048 vs 6,014, V30 · LAST from 10:30 8,411 vs 8,378, V30 · BAR from 11:00 9,059 vs 9,108 (worst −84 / −119 % vs −77 / −111 %);
  D1000 baseline 7,419 vs 7,280 (+1.9 %), every row within ±2 %. ⭐ Ruling: ONE EWMA per ticker from 09:30 for every variant.

## GH-23 — D1000: a stop above the MAX of the first half-hour bars' VWAPs (2026-11-23, user: "Let's focus on D1000 for now. What if we used the max of 9:30 ET and 10 ET bars' VWAPs as the reference? Or even added 10:30 ET to that? Does absolutely no stop at least turn pf1 neutral")

⭐ User ruling first: **U1000's default stop = V30 · the 30m bar's VWAP from 11:00** (GH-22; to be retested as features are added).
`scripts/equity/gaphv_d1000.py` → `data/equity/gaphv/d1000.{parquet,md}`. M2 = max(the 09:30 and 10:00 bars' VWAPs; known 10:30),
M3 = with the 10:30 bar (known 11:00); triggers BRK / LAST / BAR (GH-22); armed 10:30 / 11:00 / 12:00 / 13:00. The book: GH-20's
eligible D1000 (15,474), the 10:00 short to 15:30 + the one volatility-stop EWMA from 09:30.

| D1000 book | Σ | PF (halves) | worst | CVaR 1 % | fired |
|---|---|---|---|---|---|
| baseline (no level stop) | 7,419 | 1.21 (1.21 / 1.21) | −107 % | −32.4 % | |
| M2 · BRK · from 10:30 | 4,309 (−41.9 %) | 1.18 | −48 % | −17.7 % | 74 % |
| M3 · BAR · from 11:00 | 6,119 (−17.5 %) | 1.19 | −60 % | −26.0 % | 48 % |
| M2 · BRK · from 12:00 | 6,713 (−9.5 %) | 1.22 (1.21 / 1.22) | −80 % | −24.9 % | 59 % |
| **M2 · BRK · from 13:00** | 7,266 (−2.1 %) | **1.22 (1.22 / 1.23)** | **−63 %** | −27.8 % | 54 % |
| M3 · BRK · from 13:00 | 7,235 (−2.5 %) | 1.22 (1.21 / 1.22) | −63 % | −28.1 % | 50 % |
| M2 · LAST / BAR · from 13:00 | 7,023 / 7,008 (−5 %) | 1.21 | −63 % | −28 % | 47 % |

- **No level stop adds profit on D1000** — but late ones are neutral: armed at 13:00, a 1s break of M2 (or M3) costs −2 % of Σ,
  keeps / lifts the PF in both halves (1.22 / 1.23 vs 1.21 / 1.21) and cuts the worst −107 → −63 %, CVaR 1 % −32.4 → −27.8 %.
  Armed earlier every variant costs 10–42 %.
- The forward view agrees: above M2 at a mark the gap-down short still earns +0.42 % (10:30) → +0.12 % (12:00) to 15:30, and
  is neutral from 12:30 (+0.04 / +0.08 / +0.00 %); above M3 alike. The bounce above the first bars' VWAP is the short's friend
  until about noon.
- The break (BRK) beats the mark triggers here (unlike V30 on U1000, GH-22): by 13:00 a crossing of the morning's VWAP max is
  a real event.
- **The mark triggers around noon** (user: "What about M2 last mark/bar from 12 to 13 ET?"): from 12:00 −11.7 / −12.1 %
  (LAST / BAR), from 13:00 −5.3 / −5.5 %; checked ONLY at the marks 12:00 / 12:30 / 13:00 −9.8 / −9.4 %, only 12:30 / 13:00
  −3.5 / −4.2 % (PF 1.21, worst −66 %). The 12:00 mark is the expensive one (above M2 the short still earns +0.12 % from
  there); none beats the 13:00 break (−2.1 %, PF 1.22, worst −63 %).

**GH-23b — the 13:00 triggers YEAR BY YEAR** (user: "Does it perform better than `bar VWAP, from 13:00` and `last price, from
13:00` for all the years?"; `data/equity/gaphv/d1000_years.md`): **no.** The 1s break beats the last price in 5 of 11 years and
the bar VWAP in 5 of 11; its whole lead (+242 / +258 Σ) is 2021 (+201 / +198) — without 2021 +41 / +60, noise. Against NO stop
it wins in 3 of 11 years (2019, 2021, 2023) and costs in 8; but it shrinks the year's worst trade in 10 of 11 (2017 −97 → −29,
2020 −91 → −58, 2023 −107 → −63; 2026 the exception −46 → −54). ⇒ The three 13:00 triggers are indistinguishable; the stop is
tail insurance that costs ~2 % — consistently a smaller worst trade, inconsistently the sum.

**GH-23c — why 2021** (user: "Why was its lead so big in 2021?"): SPEED in fast afternoon squeezes. The 1s break covers at the
crossing, the mark triggers at the next half hour: RFL 2021-10-28 crossed M2 at 13:25:51 (break −0.5 %), by the 14:00 mark it had
run (−30.2 %); IMTE 2021-03-22 spiked above it between marks and was back below at each (the mark trigger never fired: −29.1 %
vs −4.8 %); PRPO, TRKA, OLB, BTBT (the January crypto run), AMC / KOSS / EXPR on the 2021-06-03 meme day. The lead is positive
in 10 of 2021's 12 months (the top 10 trades +147 of +201): the retail-mania regime had far more fast afternoon squeezes in
gap-down small caps. In calm years the break's speed buys nothing and its sensitivity to brief pokes costs a little. ⭐ **User
ruling 2026-11-23: D1000's default stop = the 1s break of M2 (max of the 09:30 and 10:00 bars' VWAPs) from 13:00** — the trigger
that works in the bad regime, as tail insurance (~2 % of the profit).

## GH-24 — level stops for the 09:30 variants (2026-11-23, user: "Let's move on to researching the stops for these variants next. Maybe the same stops we have now will work well on them")

`scripts/equity/gaphv_stops0930.py` → `data/equity/gaphv/stops0930.{parquet,md}`; the years `stops0930_years.md`. Every U0930 /
D0930 trade (the open), the ONE volatility-stop EWMA from 09:30 (off · 700 · 500 · 350); held to 15:30 or with the GH-6 rule;
the GH-22 / GH-23 grid of level stops (H30 / V30 / M2 × BRK / LAST / BAR × the arming). The baselines reconcile with GH-19.

| U0930 book | Σ | PF (halves) | worst | CVaR 1 % |
|---|---|---|---|---|
| held to 15:30 | 22,130 | 1.47 (1.65 / 1.42) | −265 % | −84.4 % |
| the GH-6 rule (LOSER) | 23,173 | 1.61 (1.97 / 1.52) | −128 % | −53.2 % |
| **held + V30 · BAR from 11:00 (U1000's default)** | **23,637** | **1.62 (1.98 / 1.53)** | −128 % | −53.4 % |
| held + H30 · BRK from 11:00 | 23,643 | 1.57 (1.90 / 1.49) | −128 % | −54.7 % |
| held + M2 · BRK from 13:00 (D1000's default) | 22,412 | 1.52 | −207 % | −71.3 % |
| LOSER + any level stop | 22,248 … 23,326 (−4.0 … +0.7 %) | 1.58–1.64 | −128 % | |

| D0930 book (its GH-6 rule = 15:30) | Σ | PF (halves) | worst | CVaR 1 % |
|---|---|---|---|---|
| no level stop | 12,244 | 1.30 (1.35 / 1.29) | −129 % | −46.8 % |
| **M2 · BRK from 13:00 (D1000's default)** | 11,563 (−5.6 %) | 1.30 (1.35 / 1.29) | −113 % | −41.1 % |
| H30 · BRK from 10:30 | 11,280 (−7.9 %) | 1.30 (1.38 / 1.28) | −106 % | −33.5 % |
| V30 · BAR from 11:00 (U1000's default) | 9,426 (−23.0 %) | 1.26 | −113 % | −36.1 % |

- **U0930: U1000's default does the LOSER rule's job** — held + V30 · BAR from 11:00 = 23,637 · PF 1.62 · −128 % vs LOSER's 23,173
  · 1.61 · −128 % (the worst trade identical in every year: the volatility stop sets the tail). Year by year it is ahead in 5
  of 11 (its +463 is 2021 + 2023): equivalent, not better. On top of LOSER no level stop adds (−4.0 … +0.7 %).
- **D0930: like D1000, no level stop adds**; the D1000 default costs more here (−5.6 %: ahead in 4 of 11 years, 2024–2025
  −265 / −312) for the same insurance (the year's worst smaller in 8 of 11; 2023 −129 → −68 %, 2020 −103 → −66 %). The gap-ups'
  VWAP stop does not travel to the gap-downs (−23 %): above the 30m VWAP a gap-down short keeps its edge (GH-21 / GH-22).

⭐ **User rulings 2026-11-23 — ONE RULE PER SIDE:** U0930 switches from LOSER to the gap-up default (held to 15:30 + the 30m bar
VWAP above V30 at a mark from 11:00); D0930 takes the gap-down default (the 1s break of M2 from 13:00), paying the −5.6 % as
insurance. **The four variants' exits now:**

| | U0930 | U1000 | D0930 | D1000 |
|---|---|---|---|---|
| entry | the open | 10:00, z > −2 | the open | 10:00, z > −2 |
| volatility stop | one EWMA from 09:30: off · 700 · 500 · 350 from 09:45 | same | same | same |
| level stop | the 30m bar VWAP > V30 at a mark from 11:00 | same | the 1s break of M2 from 13:00 | same |
| otherwise | out at 15:30 | same | same | same |

(V30 = the 09:30–10:00 bar's VWAP; M2 = max of the 09:30 and 10:00 bars' VWAPs.) Open: the "09:30 stop not fired before 10:00"
eligibility of the 10:00 variants (GH-13h: it drops profitable trades) and the halt filter's validation (GH-16).

⭐ **User ruling 2026-11-23: KEEP the "09:30 volatility stop not fired before 10:00" eligibility on the 10:00 variants** ("Without it
we have no way of avoiding the worst of the losses ... ZJYL ... spiked to 400% after hours. An event like that could lead to a
complete account wipeout"), despite GH-13h's cost (the skipped trades were profitable on average). Which disaster catches which
day: PGHL 2024-11-26's 09:30 stop fired at 09:42:03 → no 10:00 entry; ZJYL 2023-12-18's fired only at 10:13:37 — ZJYL at 10:00 is
caught by the HALT filter (halted 09:58:54 → 10:13:28), not by this rule. Both filters stay.

## GH-25 — FlushFader's S40x halt detector on ZJYL and PGHL (2026-11-23, user: "I bet there would be all kinds of stock halts without sufficient activity to meet the last condition ... maybe it could be worth raising that to 20 or 30. We should test whether the halt detector as stated in FlushFader could detect the ZJYL halts")

The GH-16 halt proxy was cruder: a silence ≥ 60 s running into 10:00, nothing else. The S40x detector (user design 2026-08-01,
`TradingEdge.FlushFader/Intraday.fs`): a tradeless run ≥ 58 s whose pre-hole range ln(max/min) over the last 300 PRESENT 1s
bars ≥ 4 % and whose pre-hole ADJUSTED gap60 < G — gap60 = the missing seconds of the 60 s wall-clock window (60 − the present
bars), minus those inside earlier classified halts (back-to-back halts classify by recursion). FlushFader's G = 4.
`scripts/equity/gaphv_haltdet.py` → `data/equity/gaphv/haltdet.md`:

| day | G = 4 | G = 20 | G = 30 | other runs ≥ 58 s classified |
|---|---|---|---|---|
| ZJYL 2023-12-18 (6 halts; 172 present bars all day) | 0 of 6 | 2 of 6 | **6 of 6** | none |
| PGHL 2024-11-26 (3 halts) | 2 of 3 | **3 of 3** | **3 of 3** | none |

- **G = 4 is an active-stock detector**: ZJYL traded so thinly before its first halt (27 of the minute's 60 seconds empty) that no
  halt classifies — and without the first, the recursion never discounts the next ones (raw gap60 35 / 42 / 50). At G = 30 the
  first classifies, and the recursion then brings the chained halts' adjusted gap60 down to 3–7.
- PGHL's first halt starts at 09:32:01 in the bars (the quotes' last print 09:32:44 is a cond-32 late report, GH-15c) — it is
  the real first halt, not a false one.
- ⚠ Two days of truth, both halters: G = 30's false-alarm rate on thin stocks that are NOT halted (a 58 s silence after a 4 %
  move) is the open question — the quote validation over the 10:00 universe.

## GH-26 — the 10:00 halt filter: the S40x detector vs the bare proxy, checked against the QUOTES (2026-11-23, user: "Yes")

`scripts/equity/gaphv_halt10.py` → `data/equity/gaphv/halt10.{parquet,md}` + `halt10_quotes.parquet` (383 Massive REST calls: the
last NBBO before 10:00 for every stock-day any flag marks; 0 failed). "Really halted" = that quote locked / crossed / one-sided /
empty, with no print for ≥ 58 s — the Oms's rule. S40x at 10:00 = GH-25's detector on the run in progress. The book: the 10:00
defaults (U1000: to 15:30 + the 30m bar VWAP > V30 from 11:00; D1000: + the M2 1s break from 13:00; the one EWMA stop), the
universe z > −2 and the 09:30 stop not fired before 10:00.

| flag | U1000: flagged · really halted · flagged mean · skip → Σ | D1000: same |
|---|---|---|
| bare (GH-16) | 178 · 27 (15 %) · −1.11 % · +2.2 % | 107 · 25 (23 %) · +1.05 % · −1.6 % |
| S40x G = 4 | 35 · 25 (71 %) · −2.98 % · +1.2 % | 17 · 15 (88 %) · +3.04 % · −0.7 % |
| S40x G = 20 | 41 · 27 (66 %) · −3.59 % · +1.6 % | 28 · 22 (79 %) · +3.08 % · −1.2 % |
| **S40x G = 30** | 41 · 27 (66 %) · −3.59 % · +1.6 % | 30 · 24 (80 %) · +3.34 % · −1.4 % |

- **The bare proxy is mostly NOT halts**: 15 % / 23 % of its flags are halted in the quotes; S40x G = 30 is 66 % / 80 % and finds
  every real halt the bare proxy finds (27 / 24 of 27 / 25). G = 30 ≥ G = 20 ≥ G = 4 in halts found.
- **The real 10:00 halts are not bad trades in the backtest**: U1000 27 · +0.53 % · PF 1.10 (halves +4.13 / +0.24 %), D1000 25 ·
  +3.38 % · PF 1.64. GH-16's "halted at 10:00 loses on U1000" was the THIN stocks: the bare proxy's non-halted U1000 flags (151)
  −1.41 %, PF 0.54 — but halves −2.30 / +0.02 %: a 2016–2020 effect.
- The filter stays as the user's wipeout protection (ZJYL is the case the backtest's sample lacks); with S40x G = 30 it costs
  +1.6 % / −1.4 % — free. Live it is the Oms's quote rule (haltOf), which S40x G = 30 approximates in the backtest.
