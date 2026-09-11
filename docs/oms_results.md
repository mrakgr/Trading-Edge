# The SYSTEMS MANAGER (OMS) — beta on historical 1s bars

Built 2026-09-11 in the private Scanner (`TradingEdge.Scanner/Oms/*`, `OmsRun.fs`, `scan oms`). Spec = the user's
2026-09-10 message; rulings in `docs/production_specs.md` §7. Build-vs-adapt: Malcolm's Rust trade-engine was
assessed and NOT adopted (private notes); two ideas borrowed — two-pass settlement and "never drop a message silently".

## O0 — design

**One principle.** The OMS is a RECONCILER between desired states (from subsystems) and actual positions (from the
fill model) under account-wide constraints. Subsystems never see the account; the account never sees features.

**Contract** (`Oms/Messages.fs`). A subsystem = one (system, ticker, day) wrapping a sealed engine. After every
present bar it may emit messages, only on a state change: `LongMeanReversion(pf1, signalPx)` /
`ShortMeanReversion(pf1, signalPx)` = "I want in" (a re-signal updates a pending want's price and PF−1);
`Close signalSec` = "the position I signalled at signalSec is out". ⚠ Close carries the signal second because the
engines are SAMPLERS (several engine positions can overlap on a ticker-day, the OMS holds one): the OMS closes only
the position or want with that signal; a Close for a signal it never took is a logged `stale_close` — the per-trip
logic of the §S49ag replay. Adapters (`Oms/Adapters.fs`): a NEW signal = the engine's `Pending` entry whose SignalSec
is the bar just folded (read-only accessors `Pending` / `HoldingCount` added to the three faders, re-sealed zero-diff:
FlushFader 1,180/201 10d, SpikeFader 232/15, LowFader 11/5, Snoozer 26/26); an engine position leaving `Holding` →
Close. PF−1: FlushFader the fitted A3 `Pf1` unnormalized; LowFader A 20.6 / rest 2.63 (§L23); SpikeFader A 3.26 B 4.02
X 3.42 C 3.29 D 2.20 E 0.79 (§S50); Snoozer A++ 2.452 A+ 3.152 B++ 0.590 / S 5.884 A 2.244 B 1.775 — **one A3 map for
every system**: `units = clip(pf1 / 0.519, 0.25, 4)`. v1: the Snoozers are SIGNAL-ONLY (logged, never traded).

**Fills** (`Oms/Fills.fs`). Observation = the 1s bar {sec, vwap, volume}. Every order is tested from the FIRST
observation after placement (the engines' next-bar convention). `Rest`: a limit fills AT the limit when a later vwap
reaches it (buy: vwap ≤ px; sell: vwap ≥ px) — conservative on vwap-only bars. `Cross`: the next observation's vwap
slipped against the order by `SlippageBp`. `Repeg(after, bp, max)`: a limit moved toward the market by bp every
`after` s, `max` times, then a cross. No partial fills. Fees: take fee per share on crosses, maker rebate on rests.

**The manager** (`Oms/Manager.fs`), per second, in this order (two-pass settlement — what FREES capacity settles
before what CONSUMES it): (1) fills of resting orders; (2) every `Close` — cancel a want / a resting entry, or place
the exit (Rest at the exit-signal vwap until 15:45, Cross after; a MARKET exit frees its units at placement);
(3) every Long/Short — a re-signal updates the want; else the entry checks in order, each a logged reject reason:
`signal_only`, `after_cutoff` (≥ 15:45), `sub_dollar` (signalPx < $1, FlushFader only), `cap` (units + u > 20),
`too_small` (0 shares), `gross` (> 4× equity); cap/gross → PENDING (skip-not-queue is a knob); (4) pending wants enter
when capacity allows AND price is STRICTLY past the signal (below a long's, above a short's), re-checked every second;
(5) the schedule: from 15:45 resting exits become crosses; from 15:45 while gross > 200 % close largest-units first;
at 15:58 cross everything out and drop every want; early closes shift every knob by the close. Size: `shares =
floor(units × 0.075 × equity / signalPx)` on the day's opening equity. A cross with no later observation fills at the
last vwap, flagged `stale`. Invariants counted every second (never thrown): units ≤ cap, gross ≤ max, gross ≤ cut after
the cut second (both on the UNSETTLED book — positions without a market exit pending — because a market exit on a
thin tape fills at the next print, seconds later), one position per ticker, nothing open after the flatten,
cash + marks = equity, Σ pnl = Σ realized, every message has an outcome row.

**Ledger** (`Oms/Ledger.fs`): `signals.parquet` (every message with outcome + reason), `orders.parquet` (place /
replace / repeg / cancel / fill), `positions.parquet` (exit reason signal | cross_from | gross_cut | flatten | stale),
`daily.parquet` (equity, realized, peaks, rejects by reason, invariant violations). Runner (`OmsRun.fs`): the same
1s parquet as the trips harness read with `ORDER BY bucket, ticker`, every engine resident, the second's observations
and messages handed to the manager on each bucket change; ties in ticker order (= the replay's (day, ent, tkd)).

**Tests** (`Oms/Oms_Test.fsx`, 135 checks): rest fill / miss / repeg / cross, exit rest / cross, cap → pending →
re-entry only when price is past the signal, ticker_busy, stale_close, a Close and a Long in the same second (the
close's units admit the long), gross cut largest-first, flatten, early-close offsets, every reject reason, short
accounting, the clip map; then 4 random subsystems × 40 tickers × the full session for 20 seeds: 0 invariant
violations, flat at EOD, Σ pnl = realized, cash + marks = equity, no silent drops; two runs of one seed →
byte-identical parquets.

## O0a — smoke runs (2026-09-11)

`scan oms --random 8` on 2026-07-27..31 (the test load: 8 random subsystems per ticker, ~30k messages a day):

| day | messages | entries | closed | rejects (cap / busy / cutoff) | peak units | peak gross | re-entries | INV |
|---|---|---|---|---|---|---|---|---|
| 07-27 | 30,038 | 360 | 358 | 7,212 / 6,997 / 1,180 | 20.00 | 173% | 336 | 0 |
| 07-28 | 30,972 | 346 | 342 | 7,502 / 7,111 / 1,288 | 20.00 | 174% | 318 | 0 |
| 07-29 | 31,754 | 393 | 393 | 7,637 / 7,393 / 1,136 | 20.00 | 169% | 375 | 0 |
| 07-30 | 33,662 | 325 | 323 | 8,104 / 7,723 / 1,436 | 20.00 | 177% | 301 | 0 |
| 07-31 | 33,944 | 322 | 322 | 8,169 / 7,872 / 1,356 | 20.00 | 161% | 296 | 0 |

The cap binds all day (peak exactly 20.00), the flatten catches 5–9 positions a day, zero invariant violations,
160,370 messages in 228 s (random signals lose money, as they should: −50 % in five days at 4 units a pop).

`scan oms` (the four real systems, defaults: rest/rest, cap 20, 200 % cut at 15:45, flatten 15:58) on
2026-07-27..08-07 — 2,850 messages, 297 s, 0 violations, **equity 100,000 → 117,374**:

| day | entries | closed | rejects | peak units | peak gross | realized |
|---|---|---|---|---|---|---|
| 07-27 | 26 | 23 | sub_dollar 22, signal_only 1 | 5.27 | 39% | +607 |
| 07-28 | 25 | 25 | sub_dollar 48 | 7.86 | 59% | +283 |
| 07-29 | 22 | 22 | sub_dollar 5, signal_only 21 | 5.87 | 44% | +482 |
| 07-30 | 23 | 20 | sub_dollar 11 | 4.33 | 32% | +2,226 |
| 07-31 | 25 | 25 | sub_dollar 109 | 10.98 | 85% | +7,292 |
| 08-03 | 18 | 17 | sub_dollar 45 | 6.09 | 45% | +1,678 |
| 08-04 | 14 | 14 | sub_dollar 57 | 4.70 | 36% | +7,468 |
| 08-05 | 22 | 21 | sub_dollar 25 | 9.60 | 81% | +984 |
| 08-06 | 14 | 14 | sub_dollar 62, signal_only 4 | 7.47 | 56% | −4,027 |
| 08-07 | 21 | 20 | sub_dollar 21 | 7.24 | 54% | +383 |

| system | side | n | pnl | PF | avg bp | units | hold min | entries rested (filled at the limit) |
|---|---|---|---|---|---|---|---|---|
| FlushFader | long | 187 | +9,251 | 1.46 | +31 | 1.11 | 23 | 100% (195 placed, 187 filled, 8 closed before the fill) |
| SpikeFader | short | 14 | +8,123 | 2.53 | +204 | 2.76 | 18 | 100% |

No LowFader signal in this window (as the seals: the standard window has none); the Snoozers logged 26 signal-only
decisions. **The cap never bound** (peak 11 units): the four real systems together sit far below 20 units on ordinary
days — the cap is a tail rule. 4 exits were converted to crosses at 15:45 (−2,574 in total, the LVWR class); every
other exit was the sampler's own. Message accounting: 580 FlushFader / 217 SpikeFader `already_open` re-signals while
the OMS held the ticker, matched by the same number of `stale_close`s — the sampler's overlapping positions, ignored
by construction. The two biggest days are two SpikeFader 4-unit shorts (MGRX +5,204, AMIX +3,431) and the worst is
one (JLHL −4,668): SpikeFader's A/B/X/C grades all saturate the clip at 4 units (ruling 1), so one short = 30 % of
equity. ⚠ Worth a look before this is believed: 4 units on a short whose grade PF−1 is 3.3–4 is the ruling's
consequence, not a bug, but the +17 % in ten days is mostly three SpikeFader trades.
