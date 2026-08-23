# The 1s corpus is built at MILLISECOND precision (2026-08-22)

⚠ This is the human-readable record. The builder's mixing guard keys on a
separate machine marker, `data/intraday_1s_slim/.precision`, which must stay
beside the data — see the end of this file.

## Why

The live Massive WebSocket carries **millisecond** timestamps; the bulk tape
carries **nanoseconds**. The 1s row filter compares
`sip - participant <= 50ms`, so ms truncation widens that cap to an effective
51ms — admitting **~23,233 trades/day** that the ns rule rejects, and **zero**
the other way (measured over 85.8M trades, 2026-08-21).

Building the corpus at ms makes backtest and live agree **by construction**
instead of by repeated measurement.

## What it cost — measured over the 1-year audit window (237,105 ticker-days)

| | ns | ms |
|---|---|---|
| trips | 6,289 | 6,290 &nbsp;(⚠ 31 in / 30 out, not +1) |
| book n | 260 | 260 — **256 IDENTICAL** |
| book PF | 4.313 | 4.306 |
| win / avg / worst | 76.9% / +2.24% / −12.9% | **unchanged** |
| tiers | A38 B80 C46 D96 | **unchanged** |
| equity per year / maxDD | +4.35% / −0.24% | **unchanged** |

The 4 book trades that differ are the **same ticker-day and same tier** with
entry shifted **1–10 seconds** — a gate crossing a second earlier, not a
different trade.

⭐ The universe change is **inert**: ms bars + ns universe and ms bars + ms
universe produce byte-identical trip sets. Bar-level effect on 2026-06-09:
**+2,736 present-bars gained, 0 lost**, of 31.1M.

## The nanosecond corpus was DELETED on 2026-08-22

It is derived data and remains rebuildable from `data/bulk/trades`:

```bash
TE_1S_OUT_DIR=/somewhere/else \
  dotnet fsi scripts/conversion/build_all_1s_bars.fsx -- --ns-precision   # ~13 h
```

⚠ It must go to a **separate** directory. Mixing precisions inside one corpus is
undetectable afterwards — the files look identical and only the trade counts
differ, by ~0.02%. The builder enforces this and will refuse.

## ⚠ The machine marker

`data/intraday_1s_slim/.precision` contains one line (`MILLISECOND`) and is what
`build_all_1s_bars.fsx` actually checks. It has to live beside the data, because
a corpus can be copied or pointed at via `TE_1S_OUT_DIR` and the guard must
travel with it. **Do not delete it**; without it the builder cannot tell the two
corpora apart.

Related: the ns-era banners at the end of `flushfader_results.md`,
`longsnoozer_results.md` and `shortsnoozer_results.md`.
