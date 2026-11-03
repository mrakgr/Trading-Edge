# The realized spread — does quoting pay? (2026-11-03)

**The question (user, 2026-11-03).** The edges found lately are thin, so would market making pay? In the main futures
contracts it's hard from our setup:
- Globex is first-in-first-out, and the queue belongs to colocated firms.
- There are no maker rebates for non-members, and on micros the fees eat most of a tick.

The user's own data point: "I traded a market making strategy manually in [CBRS's IPO] in TapeSim and made 50% that
day". TapeSim models MBO queue position and doesn't reroute, but it has zero order latency, and the user paused the
replay to place orders.

## The measure

For every trade, with **q = +1** when the aggressor bought (the maker sold) and **−1** when it sold, **P** the price,
**M₀** the NBBO mid just before it and **M_Δ** the mid Δ seconds later:

| measure | formula | meaning |
|---|---|---|
| effective half-spread | q·(P − M₀) | the maker's take at the fill |
| price impact | q·(M_Δ − M₀) | the move against the maker: adverse selection |
| **realized spread** | q·(P − M_Δ) | what the maker keeps unwinding at the mid Δ later |

It's an **upper bound** for us. A back-of-the-queue maker gets the worst fills, the mid is not a tradeable price, and
inventory risk doesn't show in an average. So it's a filter: where it's ≤ fees, quoting is closed for us.

`scripts/equity/realized_spread.py`:
- **Data:** the bulk trades tape (the cap sim's condition exclusions) and the Massive NBBO, cached in
  `data/equity/realized_spread/quotes/`.
- **The mid:** valid quotes only (two-sided, not locked, spread < 10 % of the mid: CBRS's open had 0.9999 × 2500.01
  stubs).
- **Halts:** a gap of more than 30 s holding locked quotes. A mid due inside one is the reopening mid, and a trade with
  no valid quote since the halt is skipped.
- **The aggressor:** by the mid; trades at the mid are left out.
- **The output:** share-weighted bp, all trades and lit-only (a lit maker can't be on the other side of TRF prints).
- **The MR split:** the maker's edge against a causal 10 / 30 s share-weighted EWMA of the trade prices.
- **Pooling:** `--save` writes per-trade parquet and `realized_spread_pool.py` pools it with equal weight per name.
- **The IPO list:** `ipo_screen.py` finds IPO-like first days (CS, a first daily row with no row in the 45 days before,
  the first lit trade after 10:05 ET): **210 of the 282 candidates with ≥ \$300M on day one**.

## CBRS, 2026-05-14 (the IPO the user traded)

- **The day:** 458,628 trades and 95,952 NBBO quotes; first trade 12:59:43 ET.
- **An LULD halt** 13:00:01 → 13:05:01.

Lit trades, bp of the mid:

| since the open | quoted spread | effective half | realized 1 s | 5 s | 30 s | 60 s | 5 min |
|---|---|---|---|---|---|---|---|
| 0–30 s (the run into the halt) | 49 | 43 | −99 | −224 | −12 | −12 | −12 |
| 5–10 min | 63 | 35 | −1 | −17 | +6 | +27 | +88 |
| 10–30 min | 38 | 17 | −4 | −2 | +6 | −2 | −10 |
| 30–60 min | 29 | 13 | 0 | −8 | −10 | −15 | −71 |
| 1–3 h | 23 | 10 | +3 | 0 | −1 | +5 | +10 |
| **the day** | 30 | 18 | −3 | −12 | **0** | +3 | +4 |

- **Pro rata, a maker on every lit share broke even:** +0.2 bp at 30 s, on \$5.5B traded.
- **The MR split looked strong on CBRS.** From 5 min, 30 s MA: the idea's fills (sold above / bought below) were
  −2.5 / −8.6 / +2.6 / **+18.5 / +62.2** bp at 1 / 5 / 30 / 60 s / 5 min; the others +6.0 / +2.1 / −3.6 / −26.9 /
  −112.5.

## 40 IPO first days, pooled (the top 40 by first-day dollar volume, 2020–2026)

Lit trades, equal weight per name; in brackets, how many of the 40 names are > 0:

| since the open | quoted spread | effective half | realized 5 s | 30 s | 60 s | 5 min |
|---|---|---|---|---|---|---|
| 0–30 s | 56 | 30 | +1.9 (27) | +6.6 (25) | **+20.5 (29)** | +36.9 (29) |
| 30 s–5 min | 29 | 15 | +2.9 (26) | −5.7 (19) | −10.1 (22) | −21.8 (22) |
| 5–10 min | 27 | 21 | +8.9 (27) | −0.4 (16) | −3.7 (19) | +0.7 (23) |
| 10–30 min | 26 | 12 | −0.7 (19) | −0.1 (20) | −2.2 (18) | −8.7 (17) |
| 30–60 min | 23 | 10 | −0.6 (18) | −2.0 (20) | +0.5 (22) | −3.3 (24) |
| 1 h + | 18 | 8 | −1.6 (8) | −2.4 (12) | −2.2 (12) | +1.0 (24) |
| **the day** | 21 | 12 | 0.0 (22) | **−3.8 (15)** | −4.4 (19) | −6.8 (18) |

| the MR split (30 s MA, from 5 min) | realized 30 s | 60 s | 5 min |
|---|---|---|---|
| with the idea (e > 0) | −5.1 (12) | −5.1 (16) | −5.5 (20) |
| against it (e ≤ 0) | −1.7 (14) | −2.7 (16) | −1.2 (17) |
| with the idea, 5–30 min only | −10.3 (13) | −15.0 (18) | −21.1 (18) |

The 10 s MA tells the same story. Run: `data/equity/realized_spread/report_ipo40.md` (per name) and `report_pool.md`;
4 min, 4.7 GB peak (SPCX's 8.6M trades).

## Findings

- **Blind quoting on hot IPOs loses.** Over the day it's −3.8 bp at 30 s, with 15 of 40 names positive: the wide
  spreads are fully paid for by adverse selection.
- **The mean-reversion bias was CBRS-only.** Pooled, the idea's fills are worse than the others, −10 to −21 bp in
  5–30 min: on most hot IPOs the early moves away from the average keep going. CBRS's back-and-forth was one sample path.
- **One pocket: the first 30 s after the open.**
  - Makers are +20 bp at 60 s on 29–30 of 40 names: the opening price overshoots and partly comes back.
  - It's not the auction print, which is 6.8 % of those shares; without it, +23 bp at 60 s on 30/40 names.
  - It's one burst per IPO, latency-critical, and CBRS itself lost there.
- **The user's 50 % on CBRS looks like discretion.** Pausing the replay and zero latency let the user sit out the
  first run and quote where the flow was two-sided — what a rule on the averages can't reproduce.

**Verdict:** rule-based market making on hot IPOs is not viable on the averages. The first-30-s pocket would need MBO
data and a queue sim, and is parked.
