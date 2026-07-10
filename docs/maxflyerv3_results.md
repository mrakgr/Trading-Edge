# MaxFlyerV3 — SHORT pop-fade + STOPS Research Log

**`TradingEdge.MaxFlyerV3`** — forked from **`TradingEdge.MaxFlyerV2`** (2026-07-10, branch `maxflyer-v3`).
Same intraday **short pop-fade** engine and defaults as V2; the V3 mandate is **drawdown control via STOPS**.

**Why V3 exists.** The V2 short book has settled edge (`brv20d>=100` main lever, rvol tiers, daily-ATR norm,
volfrac inverted-U, all-day — PF 6.65 / +$2760 on the A book; see `docs/maxflyerv2_results.md`). BUT a short
is **unbounded on the upside**, and V2 holds to MOC with no stop — so its **max drawdowns are extreme**. The
long MR book is bounded to −100% per trade, so stops there are not urgent; the short is where DD control pays.
**We will accept a PF sacrifice** if stops meaningfully cut the tail. This log records that work.

**Methodology (inherited):** clip@+50% both-PF tables, gate≠post-hoc @ max-conc-1, RAW & CLIP shown together,
bucket TABLES not prose, THE 2021 LAW (2021 is two-sided chop — V2 F24: shorting the same days also loses).
See `docs/maxflyerv2_results.md` for the full V2 findings that carry over verbatim (engine/signals unchanged).

---

## Finding 1 — (baseline) V3 == V2 byte-for-byte at fork; visualize the worst losers to design the stop

Fork is a pure copy (module rename only; engine, signals, defaults identical). Next step per the plan: run the
settled book, pull the WORST losing trades, and chart them (1m candles + entry/exit) to see WHERE and HOW a
stop would have helped — i.e. is the damage a fast spike (a tight stop catches it) or a slow grind (only a wide
stop or a time-stop helps)? The stop design follows from what the losers actually look like.

_(table pending — first run + loser extraction)_
