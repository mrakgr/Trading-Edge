"""The two Snoozer PRODUCTION books, reproduced from the caches — the registry entry (docs/production_specs.md).

LongSnoozer  §4 REBUILT BOOK (docs/longsnoozer_results.md:1605): A++ = volat_open30 [30,60)bp ∧ inten ≥ q50 ∧ pers ≥ q50;
             A+ = volat [60,120) ∧ inten ≥ q50 ∧ pers ≥ q75. Signal chg60k59 < −6%; entry = 15:59 LIMIT (px_lim_1559_1600);
             exit = next open (+ dividend). r = +ovn_from_lim59. Quantiles fixed on the 4,164-row population:
             inten q50 = 0.498393, pers q50 = 0.652937, q75 = 0.925649 (the doc's rounded 0.50/0.65/0.93 do NOT reproduce).
ShortSnoozer §S43cw three-tier book (docs/shortsnoozer_results.md:1455): signal chg60k59 > +8%; S = volat < 40bp ∧ gaps ≥ 1500
             (size 1.00); A = volat < 40 ∧ gaps ∈ [500,1500) (0.50); B = volat [40,100) ∧ gaps ≥ 2000 (0.35). r = −ovn_from_lim59.
             gaps = 3540 − nb60k59 (absent seconds in (15:00,15:59]).
Both: universe mr_candidate_1s_v2 (dv_0945_tape ≥ $2M, n_bars_1s ≥ 200), shape ⋈ volat ⋈ cache on (ticker, date).
⚠ Caches are ns-era (built 2026-08-14/16); both books are marked NOT ADOPTED in their docs (A+ thin pre-2020; short
side borrow/fees unmodelled). --floor 1 applies the $1 floor on the 15:59 decision price (p1559, RAW).

Run from research/:  python scripts/equity/snoozer_production_books.py [--floor 1]
"""
import argparse, duckdb, numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument("--floor", type=float, default=0.0, help="$ floor on p1559 (the 15:59 decision price); 0 = off")
ap.add_argument("--dir", default="data/equity/flushfader")
args = ap.parse_args()
con = duckdb.connect(); con.execute("SET memory_limit='6GB'")
con.execute(f"""CREATE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) yr, s.ovn_from_lim59 r0, s.chg60k59, 3540 - s.nb60k59 gaps, s.dv_over_open30 inten,
       s.bar_over_open30 pers, v.volat_open30*1e4 vb, s.px_lim_1559_1600 fill, c.p1559 px
FROM read_parquet('{args.dir}/snoozer_shape.parquet') s
JOIN read_parquet('{args.dir}/snoozer_volat.parquet') v ON v.ticker=s.ticker AND v.date=s.date
JOIN read_parquet('{args.dir}/snoozer_cache.parquet') c ON c.ticker=s.ticker AND c.date=s.date
WHERE s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0 AND s.dv_over_open15 IS NOT NULL""")
I, B, BB = 0.498393, 0.652937, 0.925649
FL = f" AND px >= {args.floor}" if args.floor > 0 else ""
BOOKS = {
    "LONG A++ (1.00)": (+1, f"chg60k59 < -0.06 AND vb >= 30 AND vb < 60 AND inten >= {I} AND pers >= {B}"),
    "LONG A+ (1.00)":  (+1, f"chg60k59 < -0.06 AND vb >= 60 AND vb < 120 AND inten >= {I} AND pers >= {BB}"),
    "SHORT S (1.00)":  (-1, "chg60k59 > 0.08 AND vb < 40 AND gaps >= 1500"),
    "SHORT A (0.50)":  (-1, "chg60k59 > 0.08 AND vb < 40 AND gaps >= 500 AND gaps < 1500"),
    "SHORT B (0.35)":  (-1, "chg60k59 > 0.08 AND vb >= 40 AND vb < 100 AND gaps >= 2000"),
}
def pf(x): g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
years = list(range(2016, 2027))
print(f"floor = {'off' if args.floor <= 0 else f'p1559 >= ${args.floor}'}\n")
print("| book | n | PF | avg% | med% | win% | worst% | " + " | ".join(str(y) for y in years) + " |"); print("|---|---|---|---|---|---|---|" + "---|" * len(years))
for k, (sg, w) in BOOKS.items():
    d = con.execute(f"SELECT * FROM S WHERE {w}{FL}").df(); r = d.r0.values * sg; y = d.yr.values
    print(f"| {k} | {len(d)} | {pf(r):.3f} | {r.mean()*100:+.2f} | {np.median(r)*100:+.2f} | {(r>0).mean()*100:.0f} | {r.min()*100:+.1f} | "
          + " | ".join((f"{pf(r[y==yy]):.2f} ({(y==yy).sum()})" if (y==yy).sum() else "—") for yy in years) + " |")
