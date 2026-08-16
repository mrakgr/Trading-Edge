"""S43cf — VOLATILITY per intraday window for the Snoozer family.

⭐ USER (2026-08-16): "Let's try out the volatility features for the Snoozer system.
We'll try out first 15m, 30m, 60m and the entire day's volatility. I want to see
whether along with volume that makes a difference to the trade."

## The measure is LOCKED, not chosen here

`project_surgerider_vol_bakeoff_2026-07-21` settled it: **mean |r| on 30-SECOND SLOT
VWAPS** beat r², path-RV and every kernel tried, on 43/43 days. The fixed-window
analogue used below is exactly that, with the EMA replaced by a flat mean over the
window:

    30s slot vwap  sv_i = Σ(vwap·volume) / Σ(volume)   over the slot
    r_i            = ln(sv_i / sv_{i-1})
    volat(window)  = mean |r_i|   over slots i with BOTH i and i-1 inside the window

⚠ Do NOT substitute stdev or close-to-close — that is the comparison the bake-off
already lost. ⚠ `volat_*` = VOLATILITY, `vol_*` = VOLUME in this codebase.

## ⚠⚠ ONE DELIBERATE DEPARTURE FROM FlushFader: THE SLOT CLOCK

FlushFader's `volat_20m` slots **30 PRESENT BARS** (`AnchoredEff`/`SlotVwapMa` in
`Intraday.fs` count `slotN = slotBars`), not 30 wall-clock seconds. On a dense name
the two coincide; on a gappy one a 30-bar slot can span many minutes. That is the
right choice for a streaming intraday feature, and the WRONG one here, for two
reasons:

1. **Coverage.** Measured on 2024-03-05: only **4,804 of 10,670** ticker-days
   (45%) accumulate even 3 complete 30-bar slots inside the last hour, so a
   present-bar `volat_lh` is not estimable for over half the universe. The
   wall-clock version is defined for every name that trades at all.
2. **Comparability.** These are FIXED CALENDAR WINDOWS (first 15m, first 30m, …).
   A present-bar slot straddles those boundaries by construction, and a thin name's
   "first 15m volatility" would be measured over a horizon a dense name's is not.

So the slot is 30 WALL-CLOCK SECONDS here — literally the "30s slots" of the user's
instruction. The measure itself (dollar-weighted slot vwap → ln ratio → mean of the
absolute value) is unchanged from FlushFader. This is the same two-clocks question as
§S43cd, resolved the other way because the use is cross-sectional, not streaming.

⚠ **Returns are between consecutive PRESENT slots**, so on a gappy tape an r_i can
span more than 30 seconds. That is deliberate and matches the locked measure: a thin
name's few observed moves are what it actually did. It does mean volatility and tape
density are not independent by construction — which is precisely why every test must
run the density feature as a control rather than assuming orthogonality.

⚠ Requiring the PREVIOUS slot to be in the window too drops exactly one slot per
window; without it the first slot of the last hour would carry a return spanning the
whole afternoon.

## The windows (slot = bucket // 30)

| feature | clock | slots | knowable at |
|---|---|---|---|
| `volat_open15` | 09:30–09:45 | [1140, 1170) | 09:45 |
| `volat_open30` | 09:30–10:00 | [1140, 1200) | 10:00 |
| `volat_open60` | 09:30–10:30 | [1140, 1260) | 10:30 |
| `volat_day`    | 09:30–15:00 | [1140, 1800) | 15:00 |
| `volat_lh`     | 15:00–15:59 | [1800, 1918) | 15:59 |
| `volat_dayfull`| 09:30–15:59 | [1140, 1918) | 15:59 |

`volat_day` STOPS at 15:00 so it is disjoint from the last hour — a "day" volatility
that contained the last hour would be partly the thing it is being used to explain.
`volat_dayfull` is the user's literal "entire day" and is kept alongside it.

Every window ends at or before **15:59**, the k59 limit-entry decision time
(§S43bz), so no feature here is a lookahead against the tradeable entry.

## Build note

⚠ `snoozer_build_shape.py` OOM-killed twice when aggregates were added to it (13.4GB
RSS on a 15GB box). This scan is heavier still — it needs a two-level reduction
(1s → slot → return) and the window function cannot stream — so it runs in **BATCHES
of days**, each batch fully reduced to one row per (ticker, date) before the next
starts. Peak memory is a batch, not the corpus. ~0.8s/day measured, so ~35 minutes.

Usage:  python -u scripts/equity/snoozer_build_volat.py [--force] [--batch 8]
"""
import argparse
import glob
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--batch", type=int, default=8)
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

files = sorted(glob.glob(os.path.join(args.bars1s, "*.parquet")))
print(f"{len(files):,} day files, batch = {args.batch}", flush=True)

con = duckdb.connect(config={"memory_limit": "5GB", "threads": 6})
con.execute("SET enable_progress_bar=false")
con.execute("SET preserve_insertion_order=false")

# both endpoints of the return must be inside the window
W = [("open15", 1140, 1170), ("open30", 1140, 1200), ("open60", 1140, 1260),
     ("day", 1140, 1800), ("lh", 1800, 1918), ("dayfull", 1140, 1918)]
AGG = ",\n       ".join(
    f"avg(abs(lr)) FILTER (slot >= {a} AND slot < {b} AND pslot >= {a}) AS volat_{n}, "
    f"count(lr)    FILTER (slot >= {a} AND slot < {b} AND pslot >= {a}) AS nsl_{n}"
    for n, a, b in W)

con.execute("CREATE OR REPLACE TABLE volat (date DATE, ticker VARCHAR, " +
            ", ".join(f"volat_{n} DOUBLE, nsl_{n} BIGINT" for n, _, _ in W) + ")")

t0 = time.time()
for i in range(0, len(files), args.batch):
    chunk = files[i:i + args.batch]
    lst = "[" + ",".join(f"'{f}'" for f in chunk) + "]"
    con.execute(f"""
INSERT INTO volat
WITH s AS (
  SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date,
         ticker, bucket//30 AS slot,
         sum(vwap*volume)/nullif(sum(volume), 0) AS sv
  FROM read_parquet({lst}, filename = true)
  WHERE bucket >= 34200 AND bucket < 57540 AND volume > 0 AND vwap > 0
  GROUP BY 1, 2, 3),
r AS (
  SELECT date, ticker, slot,
         lag(slot) OVER w AS pslot,
         ln(sv / lag(sv) OVER w) AS lr
  FROM s WINDOW w AS (PARTITION BY date, ticker ORDER BY slot))
SELECT date, ticker,
       {AGG}
FROM r GROUP BY 1, 2""")
    if (i // args.batch) % 25 == 0:
        n = con.execute("SELECT count(*) FROM volat").fetchone()[0]
        el = time.time() - t0
        print(f"  {i+len(chunk):>5}/{len(files)} files  {n:>12,} rows  "
              f"{el:>6.0f}s  eta {el/(i+len(chunk))*(len(files)-i-len(chunk)):.0f}s",
              flush=True)

print(f"scan done in {time.time()-t0:.0f}s", flush=True)
con.execute(f"COPY (SELECT * FROM volat) TO '{args.out}' "
            f"(FORMAT PARQUET, COMPRESSION ZSTD)")
print(f"-> {args.out}", flush=True)

print(con.execute(f"""SELECT count(*) n,
    round(median(volat_open15)*1e4, 1) AS med_open15_bp,
    round(median(volat_open30)*1e4, 1) AS med_open30_bp,
    round(median(volat_open60)*1e4, 1) AS med_open60_bp,
    round(median(volat_day)*1e4, 1)    AS med_day_bp,
    round(median(volat_lh)*1e4, 1)     AS med_lh_bp,
    round(median(volat_lh/nullif(volat_open30,0)), 3) AS med_lh_over_open30,
    round(median(nsl_lh)) AS med_slots_lh_of_118
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False), flush=True)
