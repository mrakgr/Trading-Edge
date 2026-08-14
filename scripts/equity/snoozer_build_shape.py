"""S43cb — INTRADAY DOLLAR-VOLUME SHAPE for the Snoozer family.

⭐ USER (2026-08-14): "Let's compare the last hour's dollars to the rest of the day
instead. Relative 15m volume was a good feature for the MaxFlyer system, but we never
really tested the cumulative dollar volume feature."

## Why this exists

S43ca ran a substitution test on three "thin tape" measures over the ShortSnoozer
population (`chg60k59 > +6%`), all at matched selectivity n=1,019:

    last-hour dollars / FIRST-15-MIN dollars, lowest 25%   PF 3.414
    gap count (persistence), thinnest 25%                  PF 2.670
    last-hour shares / avgvol20_prior, lowest 25%          PF 1.275   <- WORSE than
    no filter                                              PF 1.666      baseline

⚠ The first two are only ρ −0.26 apart and the first and third are ρ **0.008** — these
are not three versions of one idea, they are three different quantities, and one of
them is actively harmful. So "shorts want thin tape" is right but underdetermined:
WHICH thinness decides the result.

The measure that worked was an accident of what the old cache happened to carry
(`dv_0945_tape` was in it because it is the universe gate). Comparing to a 15-minute
window is arbitrary, and sharing a term with the universe gate is a coincidence worth
designing out rather than building on. This scan replaces it with a proper family.

## The buckets (seconds since ET midnight)

    ⭐ THE OPENING REFERENCE LADDER (user 2026-08-14: "would the first 30m or 60m be
    better as reference than just the first 15?"). The opening block is split finely so
    the reference WINDOW LENGTH becomes a free parameter to sweep, not a fixed choice:

    O5   [09:30, 09:35)  34200-34499   \
    O15  [09:35, 09:45)  34500-35099    |  cumulative sums give a reference of
    O30  [09:45, 10:00)  35100-35999    |  5 / 15 / 30 / 60 / 90 minutes
    O60  [10:00, 10:30)  36000-37799    |
    O90  [10:30, 11:00)  37800-39599   /

    A  = O5+O15   [09:30, 09:45)         the 15m open (= the existing dv_0945_tape)
    B  = O30+O60+O90  [09:45, 11:00)     morning
    C  [11:00, 13:00)  39600-46799   midday
    D  [13:00, 15:00)  46800-53999   afternoon
    E  (15:00, 15:30]  54001-55800   pre-close
    F  (15:30, 16:00]  55801-57600   the close

Dollars are Σ(vwap x volume) per bucket — the real thing, NOT shares x close_d, which
overstates a rallying day's dollars and is biased in the direction of the signal.
Trade counts ride along: count shape and dollar shape need not agree.

Every ratio a study wants is then derivable without another scan, in particular
    last hour / rest of day  =  (E+F) / (A+B+C+D)        <- the user's ask
    last hour SHARE          =  (E+F) / (A+B+C+D+E+F)    <- bounded [0,1], scale-free
    RATE ratio               =  ((E+F)/3600) / ((A+B+C+D)/19800)   <- 1.0 = flat day

⚠ All six buckets are complete by 16:00, so every ratio is knowable AT the close but
NOT before it. For a 15:59 limit entry the F bucket is only 29/30 complete — close
enough that this is a rounding question, not a lookahead, but a spec that gates on
these must say which endpoint it assumes.

Usage:  python scripts/equity/snoozer_build_shape.py [--force]
"""
import argparse
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--rth", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

con = duckdb.connect(config={"memory_limit": "6GB", "threads": 6})
con.execute("SET enable_progress_bar=false")

t0 = time.time()
print("scanning RTH [09:30, 16:00] for dollar-volume shape ...")
con.execute(f"""
CREATE OR REPLACE TABLE shp AS
SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date,
       ticker,
       sum(vwap*volume) FILTER (bucket >= 34200 AND bucket < 34500) AS dvO5,
       sum(vwap*volume) FILTER (bucket >= 34500 AND bucket < 35100) AS dvO15,
       sum(vwap*volume) FILTER (bucket >= 35100 AND bucket < 36000) AS dvO30,
       sum(vwap*volume) FILTER (bucket >= 36000 AND bucket < 37800) AS dvO60,
       sum(vwap*volume) FILTER (bucket >= 37800 AND bucket < 39600) AS dvO90,
       sum(vwap*volume) FILTER (bucket >= 34200 AND bucket < 35100) AS dvA,
       sum(vwap*volume) FILTER (bucket >= 35100 AND bucket < 39600) AS dvB,
       sum(vwap*volume) FILTER (bucket >= 39600 AND bucket < 46800) AS dvC,
       sum(vwap*volume) FILTER (bucket >= 46800 AND bucket < 54000) AS dvD,
       sum(vwap*volume) FILTER (bucket >  54000 AND bucket <= 55800) AS dvE,
       sum(vwap*volume) FILTER (bucket >  55800 AND bucket <= 57600) AS dvF,
       sum(trade_count) FILTER (bucket >= 34200 AND bucket < 54000)  AS tcDay,
       sum(trade_count) FILTER (bucket >  54000 AND bucket <= 57600) AS tcLh,
       count(*)         FILTER (bucket >= 34200 AND bucket < 54000)  AS nbDay,
       -- ⭐ S43cd (user 2026-08-14): BAR COUNTS on the same buckets as the dollar
       -- family. A bar = one second that traded at all, so this measures PRESENCE
       -- where dv measures MAGNITUDE. The ratio is RELATIVE persistence, which the
       -- absolute gap count cannot express: a name trading 850/900s at the open and
       -- 2000/3600s in the last hour has DECAYED, while 300/900 -> 1200/3600 has
       -- IMPROVED, and both can land on the same absolute gap count.
       count(*) FILTER (bucket >= 34200 AND bucket < 34500) AS nbO5,
       count(*) FILTER (bucket >= 34500 AND bucket < 35100) AS nbO15,
       count(*) FILTER (bucket >= 35100 AND bucket < 36000) AS nbO30,
       count(*) FILTER (bucket >= 36000 AND bucket < 37800) AS nbO60,
       count(*) FILTER (bucket >= 37800 AND bucket < 39600) AS nbO90,
       count(*) FILTER (bucket >  54000 AND bucket <= 57600) AS nbLh
FROM read_parquet('{args.bars1s}', filename = true)
WHERE bucket >= 34200 AND bucket <= 57600
GROUP BY 1, 2""")
print(f"  scan {time.time()-t0:.0f}s")

t1 = time.time()
con.execute(f"""
COPY (
  SELECT r.ticker, r.date, r.close_d, r.open_p1, r.div_p1,
         r.chg60, r.chg60k59, r.chg30k59, r.chg15k59, r.chg05k59,
         r.nb60k59, r.nb30k59, r.px_lim_1559_1600, r.dv_0945_tape,
         s.dvO5, s.dvO15, s.dvO30, s.dvO60, s.dvO90,
         s.dvA, s.dvB, s.dvC, s.dvD, s.dvE, s.dvF, s.tcDay, s.tcLh, s.nbDay,
         s.nbO5, s.nbO15, s.nbO30, s.nbO60, s.nbO90, s.nbLh,
         -- ⭐ BAR-COUNT twins of dv_over_open*. ⚠ RATE-NORMALISED, unlike the dollar
         -- versions: a bar count is capped by the window length (900 seconds cannot
         -- exceed 900 bars), so an un-normalised ratio would be bounded by the window
         -- ratio itself and would compress at the top. Dividing each side by its own
         -- window makes 1.0 mean "the last hour traded as continuously as the open".
         (s.nbLh/3600.0) / nullif((s.nbO5+s.nbO15)/900.0, 0)          AS bar_over_open15,
         (s.nbLh/3600.0) / nullif((s.nbO5+s.nbO15+s.nbO30)/1800.0, 0) AS bar_over_open30,
         (s.nbLh/3600.0) / nullif(s.nbO5/300.0, 0)                    AS bar_over_open5,
         (s.nbLh/3600.0) / nullif((s.nbO5+s.nbO15+s.nbO30+s.nbO60)/3600.0, 0) AS bar_over_open60,
         (s.nbLh/3600.0) / nullif(s.nbDay/19800.0, 0)                 AS bar_over_rest,
         -- ⭐ cumulative opening references: first 5 / 15 / 30 / 60 / 90 minutes
         s.dvO5                                              AS dv_open5,
         s.dvO5+s.dvO15                                      AS dv_open15,
         s.dvO5+s.dvO15+s.dvO30                              AS dv_open30,
         s.dvO5+s.dvO15+s.dvO30+s.dvO60                      AS dv_open60,
         s.dvO5+s.dvO15+s.dvO30+s.dvO60+s.dvO90              AS dv_open90,
         -- last hour measured against each. ⚠ These are NOT rate-normalised: a
         -- longer reference window mechanically has more dollars, so the LEVELS are
         -- not comparable across the ladder. Only the ORDERING within each column
         -- matters, and every test below cuts on a quantile, so that is sufficient.
         (s.dvE+s.dvF) / nullif(s.dvO5, 0)                   AS dv_over_open5,
         (s.dvE+s.dvF) / nullif(s.dvO5+s.dvO15, 0)           AS dv_over_open15,
         (s.dvE+s.dvF) / nullif(s.dvO5+s.dvO15+s.dvO30, 0)   AS dv_over_open30,
         (s.dvE+s.dvF) / nullif(s.dvO5+s.dvO15+s.dvO30+s.dvO60, 0) AS dv_over_open60,
         (s.dvE+s.dvF) / nullif(s.dvO5+s.dvO15+s.dvO30+s.dvO60+s.dvO90, 0) AS dv_over_open90,
         (s.dvE + s.dvF)                                  AS dv_lh,
         (s.dvA + s.dvB + s.dvC + s.dvD)                  AS dv_rest,
         -- ⭐ the user's feature: last hour vs the REST of the day
         (s.dvE + s.dvF) / nullif(s.dvA+s.dvB+s.dvC+s.dvD, 0)          AS dv_over_rest,
         -- scale-free twin, bounded [0,1]
         (s.dvE + s.dvF) / nullif(s.dvA+s.dvB+s.dvC+s.dvD+s.dvE+s.dvF, 0) AS lh_share,
         -- RATE ratio: 1.0 = the last hour traded at the day's average pace
         ((s.dvE + s.dvF)/3600.0) / nullif((s.dvA+s.dvB+s.dvC+s.dvD)/19800.0, 0) AS lh_rate,
         -- trade-count shape, which need not agree with dollar shape
         (s.tcLh/3600.0) / nullif(s.tcDay/19800.0, 0)                  AS tc_rate,
         -- the S43ca proxy, kept ONLY so the new features can be compared to it
         (s.dvE + s.dvF) / nullif(s.dvA, 0)                            AS dv_over_open,
         -- final-30m concentration: is the move happening on a burst or a fade?
         s.dvF / nullif(s.dvE + s.dvF, 0)                              AS f_share_of_lh,
         (r.open_p1 + r.div_p1)/nullif(r.close_d,0) - 1                AS ovn_from_close,
         (r.open_p1 + r.div_p1)/nullif(r.px_lim_1559_1600,0) - 1       AS ovn_from_lim59
  FROM read_parquet('{args.rth}') r
  JOIN shp s ON s.ticker = r.ticker AND s.date = r.date
  WHERE r.close_d > 0 AND r.open_p1 IS NOT NULL
) TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(f"  join+write {time.time()-t1:.0f}s -> {args.out}")

print(con.execute(f"""SELECT count(*) n,
    round(median(dv_over_rest),4) AS med_dv_over_rest,
    round(median(lh_share),4)     AS med_lh_share,
    round(median(lh_rate),3)      AS med_lh_rate,
    round(median(tc_rate),3)      AS med_tc_rate,
    count(*) FILTER (dv_over_rest IS NULL) AS null_dv_over_rest
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
