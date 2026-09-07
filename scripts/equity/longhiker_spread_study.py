#!/usr/bin/env python3
"""S41 — EFFECTIVE SPREAD on the LongHiker universe, from the trades tape (user, 2026-09-07):
"measure the spread by price bucket and restate every cell as maker vs taker economics".

Per ticker-day on sample days, lit prints only (trf_id = 0), the 1s-builder's condition
exclude set, 09:45-16:00 ET, tickers restricted to the study slice's FRAME ticker-days:
  roll_bp   = 2*sqrt(max(0, -cov(dp_t, dp_{t-1}))) / median price   (Roll 1984 effective spread)
  rev_rate  = share of consecutive NON-ZERO price changes that reverse sign (0.5 = random walk;
              higher = bid-ask bounce)
  dabs_bp   = mean |dp| over price-changing consecutive prints, bp (a bounce-magnitude proxy)
  tick_bp   = 0.01 / median price
Aggregated by price bucket; then the economics table with the S40 bounce-test edges.
"""
import sys, time, duckdb, numpy as np, pandas as pd
DAYS = ['2020-06-15','2021-06-15','2022-06-15','2023-06-14','2024-06-12','2025-06-11','2026-08-26']
SLICE = 'data/longhiker_study_consol.parquet'
EXCL = [2,7,10,13,20,21,22,29,32,52,53]
con = duckdb.connect(); con.execute("SET memory_limit='8GB'; SET threads=8")
parts = []
for d in DAYS:
    t0 = time.time()
    df = con.execute(f"""
    WITH uni AS (SELECT DISTINCT ticker FROM read_parquet('{SLICE}')
                 WHERE date = DATE '{d}' AND gap_60 < 30 AND volat_20m >= 0.002 AND signal_sec >= 35100),
    t AS (
      SELECT ticker, price, sip_timestamp, sequence_number
      FROM read_parquet('/mnt/d/trading-edge-bulk/trades/{d}.parquet')
      WHERE trf_id = 0 AND ticker IN (SELECT ticker FROM uni)
        AND NOT list_has_any(conditions, {EXCL})
        AND (sip_timestamp % 86400000000000) BETWEEN 49500000000000 AND 72000000000000  -- sip is UTC ns; 13:45-20:00 UTC = 09:45-16:00 EDT (all sample days are EDT)
    ),
    p AS (SELECT ticker, price,
                 price - lag(price, 1) OVER w AS dp,
                 lag(price, 1) OVER w - lag(price, 2) OVER w AS dp1
          FROM t WINDOW w AS (PARTITION BY ticker ORDER BY sip_timestamp, sequence_number))
    SELECT ticker, DATE '{d}' AS date, count(*) AS n, median(price) AS px,
      2*sqrt(greatest(0, -covar_pop(dp, dp1)))/median(price)*1e4 AS roll_bp,
      avg(CASE WHEN dp <> 0 AND dp1 <> 0 THEN (sign(dp) <> sign(dp1))::INT END) AS rev_rate,
      avg(CASE WHEN dp <> 0 THEN abs(dp) END)/median(price)*1e4 AS dabs_bp,
      0.01/median(price)*1e4 AS tick_bp
    FROM p WHERE dp1 IS NOT NULL GROUP BY ticker HAVING count(*) >= 200
    """).df()
    print(f"{d}: {len(df)} tickers in {time.time()-t0:.0f}s", flush=True)
    parts.append(df)
sp = pd.concat(parts); sp.to_parquet('data/longhiker_spread_sample.parquet')
PB = [(2,5,'$2-5'),(5,10,'$5-10'),(10,20,'$10-20'),(20,50,'$20-50'),(50,100,'$50-100'),(100,1e9,'$100+')]
print("\n### S41 — effective spread by price bucket (lit prints, 09:45-16:00, FRAME universe, 7 sample days)")
print("| price | tkd | med px | tick bp | Roll bp med | Roll bp mean | rev rate | mean abs dp bp | prints/tkd |")
print("|---|---|---|---|---|---|---|---|---|")
rows = {}
for a,b,lab in PB:
    g = sp[(sp.px>=a)&(sp.px<b)]
    rows[lab] = dict(tkd=len(g), px=g.px.median(), tick=g.tick_bp.median(), roll_med=g.roll_bp.median(), roll_mean=g.roll_bp.mean(), rev=g.rev_rate.mean(), dabs=g.dabs_bp.median(), n=g.n.median())
    r = rows[lab]; print(f"| {lab} | {r['tkd']} | {r['px']:.1f} | {r['tick']:.2f} | {r['roll_med']:.2f} | {r['roll_mean']:.2f} | {r['rev']:.3f} | {r['dabs']:.2f} | {r['n']:,.0f} |")
# the S40 bounce-test edges (ts30, eqw bp): fade edge = -(trend cell), avg of long/short; coil = long unlagged >=.45
EDGE = {'$2-5':(( 7.53+6.45)/2, 1.03), '$5-10':((5.78+5.26)/2, 1.00), '$10-20':((4.70+3.98)/2, 0.84),
        '$20-50':((3.21+3.76)/2, 3.77), '$50-100':((2.64+3.16)/2, 5.29), '$100+':((2.30+3.16)/2, 4.74)}
REB, TAKE = 0.002, 0.003   # $/share, GENERIC ECN assumptions (no broker schedule in hand)
print("\n### S41 — economics per round trip, bp (Roll median as the spread; rebate $0.002/sh, take fee $0.003/sh, generic)")
print("| price | fade edge (ts30) | half-spread | rebate | FADE as MAKER: edge+half+reb | FADE as TAKER: edge−half−fee | coil edge | COIL as TAKER: edge−half−fee |")
print("|---|---|---|---|---|---|---|---|")
for a,b,lab in PB:
    r = rows[lab]; fe, ce = EDGE[lab]; hs = r['roll_med']/2; reb = REB/r['px']*1e4; fee = TAKE/r['px']*1e4
    print(f"| {lab} | +{fe:.2f} | {hs:.2f} | {reb:.2f} | **{fe+hs+reb:+.2f}** | {fe-hs-fee:+.2f} | +{ce:.2f} | {ce-hs-fee:+.2f} |")
