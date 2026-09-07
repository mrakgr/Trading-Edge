#!/usr/bin/env python3
"""SpringFlyer — first pass on DAILY bars (user, 2026-09-07).

The pattern: a large intraday decline from a reference point (the previous close
or the open), ideally BELOW an obvious support (the 7-day low), that the stock
then CLOSES ABOVE the reference — Wyckoff's spring. Entry AT THE CLOSE of the
signal day (the S43bv 15:59 convention: the close is the last knowable print;
live this is a 15:59 limit / MOC), hold k days, exit at the close of D+k.

Every gate column is knowable at D's close. Forward columns are OUTCOMES ONLY.
Prices per docs/price_adjustment.md §2: prior days in D's raw scale
(P(t)*n(t)/n(D)), dividends as cash increments, floors on RAW prices.

  python3 scripts/equity/springflyer_daily.py [--rebuild]
"""
import argparse, os, sys, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--rebuild', action='store_true')
ap.add_argument('--feat', default='data/springflyer_daily.parquet')
args = ap.parse_args()

if args.rebuild or not os.path.exists(args.feat):
    t0 = time.time()
    con = duckdb.connect('data/trading.db', read_only=True)
    con.execute("SET memory_limit='8GB'; SET threads=8")
    con.execute(f"""
    COPY (
    WITH b00 AS (
      SELECT *, close*n AS cn, ABS(close*n - LAG(close*n,1) OVER e) AS dcn,
        close*n / LAG(close*n,1) OVER e - 1 AS ret1,
        CASE WHEN close*n > LAG(close*n,1) OVER e THEN 0 ELSE 1 END AS down_marker
      FROM daily_episodes_causal
      WHERE date >= '2004-06-01'
      WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
    ), b01 AS (
      -- ⭐ the up-close RUN (2026-09-07, the climax short): a run starts on a non-up day and
      -- holds every consecutive up close after it. run_id increments on each non-up day.
      SELECT *, SUM(down_marker) OVER (PARTITION BY ticker, episode ORDER BY date ROWS UNBOUNDED PRECEDING) AS run_id
      FROM b00
    ), b0 AS (
      SELECT *,
        ROW_NUMBER() OVER r - 1                                   AS up_streak,     -- consecutive up closes ending today
        FIRST_VALUE(cn) OVER r                                    AS run_base_cn,   -- the close the run started from
        MAX(ret1) OVER (r ROWS UNBOUNDED PRECEDING)               AS run_max_ret1   -- biggest single up day in the run so far
      FROM b01
      WINDOW r AS (PARTITION BY ticker, episode, run_id ORDER BY date)
    ), b AS (
      SELECT ticker, date, episode, open, high, low, close, volume, n, cum_div,
        ROW_NUMBER() OVER e AS barnum,
        LAG(close,1) OVER e * LAG(n,1) OVER e / n           AS prev_close,     -- D-1 close, D's scale
        LAG(up_streak,1) OVER e                             AS prev_streak,    -- up closes in a row ending D-1
        LAG(cn,1) OVER e / LAG(run_base_cn,1) OVER e - 1    AS prev_run_gain,  -- D-1 close vs the run's base close
        LAG(run_base_cn,1) OVER e / n                       AS run_base,       -- the run's base close, D's scale
        LAG(run_max_ret1,1) OVER e                          AS prev_run_maxday,
        up_streak                                           AS up_streak_d,    -- D's own streak (0 if D closed down)
        LAG(close,1) OVER e                                 AS prev_close_raw, -- the $ floor
        MIN(low*n)  OVER (e ROWS BETWEEN 7 PRECEDING AND 1 PRECEDING) / n   AS low7_prior,
        MIN(low*n)  OVER (e ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) / n  AS low20_prior,
        MAX(high*n) OVER (e ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) / n  AS high20_prior,
        LAG(close,20) OVER e * LAG(n,20) OVER e / n         AS close_m20,
        n * AVG(volume / n) OVER (e ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avgvol20_prior,
        AVG((high - low) / close) OVER (e ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS atr20_prior,  -- avg daily range, fraction of close, PRIOR 20 days
        -- ⭐ efficiency ratio on CLOSES (user, 2026-09-07), share-consistent (close*n), INCLUDING day D:
        --   er_N = |cn(D) - cn(D-N)| / sum over t=D-N+1..D of |cn(t) - cn(t-1)|   (Kaufman); signed twin carries the direction
        LAG(close*n, 10) OVER e                                                 AS cn_m10,
        LAG(close*n, 20) OVER e                                                 AS cn_m20,
        SUM(dcn) OVER (e ROWS BETWEEN 9 PRECEDING AND CURRENT ROW)  AS path10,
        SUM(dcn) OVER (e ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS path20,
        -- outcomes (lookahead BY DESIGN — never gate)
        LEAD(open,1)  OVER e * LEAD(n,1)  OVER e / n        AS open_p1,
        LEAD(close,1) OVER e * LEAD(n,1)  OVER e / n        AS close_p1,
        LEAD(close,2) OVER e * LEAD(n,2)  OVER e / n        AS close_p2,
        LEAD(close,3) OVER e * LEAD(n,3)  OVER e / n        AS close_p3,
        LEAD(close,5) OVER e * LEAD(n,5)  OVER e / n        AS close_p5,
        LEAD(close,7) OVER e * LEAD(n,7)  OVER e / n        AS close_p7,
        LEAD(close,10) OVER e * LEAD(n,10) OVER e / n       AS close_p10,
        LEAD(low,1)  OVER e * LEAD(n,1)  OVER e / n         AS low_p1,
        MIN(low*n)  OVER (e ROWS BETWEEN 1 FOLLOWING AND 5 FOLLOWING) / n    AS low_f5,
        MAX(high*n) OVER (e ROWS BETWEEN 1 FOLLOWING AND 5 FOLLOWING) / n    AS high_f5,
        LEAD(high,1) OVER e * LEAD(n,1) OVER e / n          AS high_p1,
        (LEAD(cum_div,1) OVER e - cum_div) / n              AS div_p1,
        (LEAD(cum_div,2) OVER e - cum_div) / n              AS div_p2,
        (LEAD(cum_div,3) OVER e - cum_div) / n              AS div_p3,
        (LEAD(cum_div,5) OVER e - cum_div) / n              AS div_p5,
        (LEAD(cum_div,7) OVER e - cum_div) / n              AS div_p7,
        (LEAD(cum_div,10) OVER e - cum_div) / n             AS div_p10,
        -- per-row twins for LAGGING (the climax spec, 2026-09-07)
        volume / NULLIF(AVG(volume / n) OVER (e ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) * n, 0) AS rvol_t,
        (cn - LAG(cn,10) OVER e) / NULLIF(SUM(dcn) OVER (e ROWS BETWEEN 9 PRECEDING AND CURRENT ROW), 0) AS er10s_t,
        (cn - LAG(cn,20) OVER e) / NULLIF(SUM(dcn) OVER (e ROWS BETWEEN 19 PRECEDING AND CURRENT ROW), 0) AS er20s_t,
        MAX(cn) OVER (e ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) / MIN(cn) OVER (e ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) - 1 AS rng20c_t,  -- 20-session CLOSE range incl. today
        MAX(cn) OVER (e ROWS BETWEEN 252 PRECEDING AND 1 PRECEDING) AS hi252c_prior,   -- 52w CLOSING high before D
        cn
      FROM b0
      WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
    ), c AS (
      SELECT *,
        LAG(er10s_t,1) OVER e                               AS prev_er10s,     -- efficiency at D-1's close
        LAG(er20s_t,1) OVER e                               AS prev_er20s,
        LAG(rng20c_t,1) OVER e                              AS prev_rng20c,    -- 20-session close range as of D-1
        GREATEST(LAG(rvol_t,1) OVER e, LAG(rvol_t,2) OVER e, LAG(rvol_t,3) OVER e) AS run_rvol_max3,  -- loudest of D-1..D-3
        LAG(cn,1) OVER e / LAG(cn,4) OVER e - 1             AS chg3_prev,      -- the 3-day move ending D-1
        CASE WHEN LAG(cn,1) OVER e >= LAG(hi252c_prior,1) OVER e THEN 1 ELSE 0 END AS at52_prev,  -- D-1 closed at a 52w closing high
        LAG(cn,4) OVER e / n                                AS close_m4,       -- D-4 close, D's scale (the 3-day move's base)
        cn / LAG(cn,3) OVER e - 1                           AS chg3_d,         -- the 3-day move ending TODAY (green-day control)
        CASE WHEN cn >= hi252c_prior THEN 1 ELSE 0 END      AS at52_d
      FROM b
      WINDOW e AS (PARTITION BY ticker, episode ORDER BY date)
    )
    SELECT ticker, date, year(date) AS yr, barnum, open, high, low, close, volume,
      prev_close, prev_close_raw, low7_prior, low20_prior, high20_prior, close_m20,
      avgvol20_prior, avgvol20_prior * prev_close AS dv20_prior,
      volume / NULLIF(avgvol20_prior,0) AS rvol,
      high / low - 1                    AS rng,           -- the day's range
      prev_streak, prev_run_gain, prev_run_maxday, up_streak_d, run_base,
      prev_er10s, run_rvol_max3, chg3_prev, at52_prev, chg3_d, at52_d, rvol_t AS rvol_d,
      prev_er20s, prev_rng20c, rng20c_t AS rng20c_d,
      high20_prior / low20_prior - 1 AS prev_rng20,   -- 20-session HIGH/LOW range as of D-1
      CASE WHEN prev_close > close_m4 THEN (prev_close - close) / (prev_close - close_m4) END AS retrace3,  -- D's give-back of the 3-day move
      CASE WHEN prev_close > run_base THEN (prev_close - close) / (prev_close - run_base) END AS retrace,  -- D's give-back of the run (1 = all of it)
      (high / low - 1) / NULLIF(atr20_prior, 0) AS rng_atr,  -- in units of the stock's own prior average range
      atr20_prior,
      ABS(close*n - cn_m10) / NULLIF(path10, 0)  AS er10,
      ABS(close*n - cn_m20) / NULLIF(path20, 0)  AS er20,
      (close*n - cn_m10) / NULLIF(path10, 0)     AS er10_signed,
      (close*n - cn_m20) / NULLIF(path20, 0)     AS er20_signed,
      -- the day's shape, all at the close
      low / prev_close - 1              AS decl_prev,    -- worst point vs yesterday's close
      low / open - 1                    AS decl_open,    -- worst point vs the open
      open / prev_close - 1             AS gap,
      close / prev_close - 1            AS rev_prev,     -- where it closed vs yesterday
      close / open - 1                  AS rev_open,     -- where it closed vs the open
      CASE WHEN high > low THEN (close - low) / (high - low) END AS clpos,  -- close in the range
      low / low7_prior - 1              AS below7,       -- <0 = broke the 7-day low
      low / low20_prior - 1             AS below20,
      close / low7_prior - 1            AS close_vs_low7,
      close / close_m20 - 1             AS chg20,
      -- outcomes, bp, from D's CLOSE
      1e4*((open_p1 - close) / close)                     AS r_open1,
      1e4*((close_p1 + div_p1 - close) / close)           AS r1,
      1e4*((close_p2 + div_p2 - close) / close)           AS r2,
      1e4*((close_p3 + div_p3 - close) / close)           AS r3,
      1e4*((close_p5 + div_p5 - close) / close)           AS r5,
      1e4*((close_p7 + div_p7 - close) / close)           AS r7,
      1e4*((close_p10 + div_p10 - close) / close)         AS r10,
      1e4*((low_f5 - close) / close)                      AS mae5,  -- worst low over the next 5 days
      1e4*((high_f5 - close) / close)                     AS mfe5,  -- highest high over the next 5 days (the SHORT's adverse excursion)
      1e4*((high_p1 - close) / close)                     AS hi1
    FROM c
    WHERE barnum >= 22 AND prev_close IS NOT NULL AND date >= '2005-01-01'
    ) TO '{args.feat}' (FORMAT PARQUET, COMPRESSION 'zstd');""")
    print(f"features built in {time.time()-t0:.0f}s -> {args.feat}", flush=True)

con = duckdb.connect(); con.execute("SET memory_limit='6GB'; SET threads=8")
F = f"read_parquet('{args.feat}')"
n = con.execute(f"SELECT count(*), count(DISTINCT ticker), min(date), max(date) FROM {F}").fetchone()
print(f"feature rows {n[0]:,} / tickers {n[1]:,} / {n[2]} -> {n[3]}")

FRAME = "dv20_prior >= 5e6 AND prev_close_raw >= 2 AND r1 IS NOT NULL"
NY = 2026 - 2005 + 1
def pf(r):
    g = r[r > 0].sum(); l = -r[r < 0].sum(); return g / l if l > 0 else float('inf')

def row(where, hs=('r_open1','r1','r3','r5','r10'), label=''):
    d = con.execute(f"SELECT yr, {', '.join(hs)}, mae5 FROM {F} WHERE {FRAME} AND ({where})").df()
    if len(d) == 0: return f"| {label} | 0 |" + " | " * (2 * len(hs) + 1)
    out = [f"| {label} | {len(d)/NY:,.0f}"]
    for h in hs:
        r = d[h].dropna().values
        out.append(f"{r.mean():+.0f} / {pf(r):.2f}")
    r5 = d['r5'].dropna(); yrs = d.groupby('yr')['r5'].mean()
    out.append(f"{(r5>0).mean()*100:.0f}% | {(yrs>0).sum()}/{yrs.size} | {np.median(d.mae5.dropna()):+.0f}")
    return " | ".join(out) + " |"

def table(title, cells, hs=('r_open1','r1','r3','r5','r10')):
    print(f"\n### {title}")
    print("| cell | n/yr | " + " | ".join(f"{h}: mean bp / PF" for h in hs) + " | win5 | yrs5 | med mae5 |")
    print("|---|---|" + "---|" * len(hs) + "---|---|---|")
    for lab, w in cells: print(row(w, hs, lab))
    sys.stdout.flush()

print(f"\nFRAME = {FRAME}   (dollar-volume floor on the PRIOR 20 days, raw $2 floor on D-1's close)")
table("T0 — the frame, and the naive references",
      [("every day (the frame)", "TRUE"),
       ("close > prev close (any)", "rev_prev > 0"),
       ("close < prev close (any)", "rev_prev < 0")])

DECL = [("-3..-5%", -0.05, -0.03), ("-5..-7%", -0.07, -0.05), ("-7..-10%", -0.10, -0.07),
        ("-10..-15%", -0.15, -0.10), ("-15..-25%", -0.25, -0.15), ("<-25%", -9, -0.25)]
table("T1 — DECLINE from the previous close (low vs prev close) x the SPRING close (close > prev close), with the failed-reversal control",
      [(f"spring: decl {lab}, close > prev", f"decl_prev >= {a} AND decl_prev < {b} AND rev_prev > 0") for lab, a, b in DECL]
      + [(f"CONTROL: decl {lab}, close <= prev", f"decl_prev >= {a} AND decl_prev < {b} AND rev_prev <= 0") for lab, a, b in DECL])

table("T2 — the same, decline measured from the OPEN and the close judged vs the OPEN",
      [(f"spring: decl_open {lab}, close > open", f"decl_open >= {a} AND decl_open < {b} AND rev_open > 0") for lab, a, b in DECL]
      + [(f"CONTROL: decl_open {lab}, close <= open", f"decl_open >= {a} AND decl_open < {b} AND rev_open <= 0") for lab, a, b in DECL])

SP = "decl_prev < -0.05 AND rev_prev > 0"
table("T3 — SUPPORT: did the low break the 7-day / 20-day low? (spring days with decline <= -5% vs prev close)",
      [("spring, low ABOVE 7d low (no break)", SP + " AND below7 >= 0"),
       ("spring, broke 7d low by 0..-2%", SP + " AND below7 < 0 AND below7 >= -0.02"),
       ("spring, broke 7d low by -2..-5%", SP + " AND below7 < -0.02 AND below7 >= -0.05"),
       ("spring, broke 7d low by <-5%", SP + " AND below7 < -0.05"),
       ("spring, broke 20d low", SP + " AND below20 < 0"),
       ("spring, broke 20d low AND closed back above the 7d low", SP + " AND below20 < 0 AND close_vs_low7 > 0"),
       ("CONTROL: broke 7d low, decl<=-5%, close <= prev", "decl_prev < -0.05 AND rev_prev <= 0 AND below7 < 0")])

table("T4 — the close's position in the day's range (spring days, decline <= -5%, broke the 7d low)",
      [(f"clpos {lab}", SP + f" AND below7 < 0 AND clpos >= {a} AND clpos < {b}") for lab, a, b in
       [("0.5-0.7", 0.5, 0.7), ("0.7-0.85", 0.7, 0.85), ("0.85-0.95", 0.85, 0.95), ("0.95-1 (closed at the high)", 0.95, 1.01)]])

table("T5 — relative volume on the spring day (decline <= -5%, broke 7d low, close > prev)",
      [(f"rvol {lab}", SP + f" AND below7 < 0 AND rvol >= {a} AND rvol < {b}") for lab, a, b in
       [("<1", 0, 1), ("1-2", 1, 2), ("2-4", 2, 4), ("4-8", 4, 8), ("8+", 8, 1e9)]])

table("T6 — the 20-day context (spring, decline <= -5%, broke 7d low): was it already down?",
      [(f"chg20 {lab}", SP + f" AND below7 < 0 AND chg20 >= {a} AND chg20 < {b}") for lab, a, b in
       [("<-30%", -9, -0.3), ("-30..-15%", -0.3, -0.15), ("-15..-5%", -0.15, -0.05), ("-5..+5%", -0.05, 0.05), ("+5%+", 0.05, 9)]])

table("T7 — liquidity tiers (spring, decline <= -5%, broke 7d low)",
      [(f"dv20 {lab}", SP + f" AND below7 < 0 AND dv20_prior >= {a} AND dv20_prior < {b}") for lab, a, b in
       [("$5-20M", 5e6, 2e7), ("$20-100M", 2e7, 1e8), ("$100M-1B", 1e8, 1e9), ("$1B+", 1e9, 1e12)]])

# year table for the headline cell
HEAD = SP + " AND below7 < 0"
print(f"\n### T8 — year table, headline cell = spring (decl<=-5%, close>prev) x broke the 7d low\n{HEAD}")
d = con.execute(f"SELECT yr, r_open1, r1, r3, r5, r10 FROM {F} WHERE {FRAME} AND {HEAD}").df()
print("| year | n | r_open1 mean/PF | r1 | r3 | r5 | r10 |\n|---|---|---|---|---|---|---|")
for y, g in d.groupby('yr'):
    print(f"| {y} | {len(g):,} | " + " | ".join(f"{g[h].dropna().mean():+.0f} / {pf(g[h].dropna().values):.2f}" for h in ['r_open1','r1','r3','r5','r10']) + " |")
