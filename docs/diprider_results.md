# DipRider — pullback-in-uptrend re-break (LONG intraday)

**System:** `TradingEdge.DipRider` (branch `dip-rider`), forked from `TradingEdge.VwapReclaim`.
**Doc discipline:** journal each finding here AS YOU GO (see `feedback_document_findings_as_you_go`).

## The pattern

The mirror image of the reversion systems (VwapReclaim / LowFlyer buy *weakness that reverts*).
DipRider buys **strength that pauses and resumes**: after an established intraday UPTREND, price pulls
back (closes below the 9-EMA for a stretch), then a **RE-BREAK** bar closes decisively above the prior
bar's high — buy the resumption. This is Qullamaggie's / Ross Cameron's / Tim Sykes' long dip-buy.

## The engine (v0 defaults — arbitrary, pre-tuning)

- **Universe:** `vwap_reclaim_candidate` (ADV ≥ $30M & rvol_0945 > 1; 52,630 ticker-days 2003-2026).
- **Entry** (long, fill at the re-break bar's close), all gates ON by default:
  - **Re-break:** `close ≥ prevBar.high · (1 + 0.5·ATR%)` (a half-ATR expansion over the prior high).
  - **Pullback:** ≥ 3 consecutive bars closed below the 9-EMA right before the re-break.
  - **Uptrend:** the re-break close ≥ 2% above the session open.
  - tightness ≥ 3; window 10:00–13:30 ET.
- **Exit** (user's spec): **exit-to-new-session-high** (the resumption ran), else a **15m time-stop**,
  else MOC. **Stop** = the re-break bar's low (close-based).

### The four handcrafted features (recorded in the CSV, not yet gates)

1. `bars_since_hi` — # bars since the session PRICE high (recency of the 1st push).
2. `bars_since_vol_hi` — # bars since the session max-1m-VOLUME high (recency of peak interest).
3. `bars_below_ema` — # consecutive bars closed below the 9-EMA before the re-break (pullback DEPTH).
4. `trend_pct` — re-break close / session open − 1 (how far up the session the trend had run).

Feature #1 vs #4 pins how STALE the first move is on both the price and volume axes; the gap between
them (price-recency vs volume-recency) says whether the second push is on fading or re-igniting volume.

---

## Findings

### Finding 1 — v0 harness works end-to-end (2026 H1 smoke test)

Bare defaults, 2026-01-01 .. 2026-06-25: **871 trips / 459 candidate-days / PF 1.037 / 40.1% win /
+$3.7k.** Thin (as expected pre-tuning), but the engine fires and the features populate sensibly:
- avg bars-since-price-high ≈ **81 min**, avg bars-since-vol-high ≈ **126 min** → the typical entry is
  a LATE second push, well after both the price and (especially) the volume peak. Volume is staler than
  price — the re-igniting-interest case the features were built to catch.
- avg pullback depth ≈ 8 bars below the 9-EMA; avg trend ≈ 21% up on the session (tail-heavy universe).
- **Exit mix: 8% new-high, 42% time-stop, ~49% stop, 0% MOC** — most re-breaks do NOT immediately make
  a fresh high; they chop into the 15m time-stop. ⇒ the edge (if any) is in SELECTION: which re-breaks
  resume. That's what the four features are for. NEXT = bucket-breakdown each feature vs ret_moc.
