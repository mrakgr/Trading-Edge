"""S43bq — MOC exits vs holding to the NEXT OPEN, and the overnight-reversal map.

Answers: trades that never reach a 5m high exit at the close. Is exiting on the
next OPEN better? Thesis: stocks strongly negative in the last hour have a
positive edge on the next open.

Three stages:
  --stage moc    the 175 `moc` trips: gap_60 profile, ticker-day dedup, the
                 substitution, bootstrap CI, and the matched population
  --stage pop    1.42M universe ticker-days: overnight return bucketed by the
                 last-hour change, with YEAR columns, split by tape density
  --stage all    both (default)

⚠ SCALE DISCIPLINE (CLAUDE.md rule 4)
  * The overnight leg is built from RAW `daily_prices` + `splits.split_ratio` +
    cash dividends added back. It must NOT use `split_adjusted_prices`, which
    carries a live dividend-denominator bug (factor divides by the ex-date close
    instead of the cum-dividend close) that yields adj_close = $0.000020 against
    a raw $19.53 on VISN 2026-04-27 -> a +493,598% overnight return.
        overnight = [raw_open(D+1)*split_ratio(D+1) + cash_div(ex=D+1)] / raw_close(D) - 1
  * `daily_prices.open` IS the tradeable 09:30 RTH print (verified vs the first
    1s bar at/after bucket 34200: median ratio 1.0000, 91.8% within 50bp).
  * The 1s slim `vwap` is RAW. `lh_chg` is a same-day ratio so the adjustment
    factor cancels — but never compare a 1s slim level to an adjusted daily one.
  * MEDIAN is the headline statistic. The raw mean is unusable: 485 of 1.42M
    rows exceed |50%| from corporate actions the splits table does not carry
    (VISN 2026-04-28, LU 2024-06-04 are real 2:1 events with no splits row).
    A +/-50% winsorized mean is shown alongside.

The 1s scan (~150s over 2,514 files) is cached to --cache; delete it to rebuild.

Usage:
  python scripts/equity/flushfader_overnight.py
  python scripts/equity/flushfader_overnight.py --stage pop
"""
import argparse, os, time
import duckdb, numpy as np, pandas as pd

pd.set_option("display.width", 240)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--stage", choices=["moc", "pop", "all"], default="all")
ap.add_argument("--trips", default="data/equity/flushfader/v43_legtick/trips_p*.parquet")
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--cache", default="data/equity/flushfader/lasthour_cache.parquet")
ap.add_argument("--thin", type=int, default=600, help="last-hour seconds traded below which the tape is THIN")
ap.add_argument("--liquid", type=int, default=3000, help="... at or above which it is LIQUID")
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "12GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")

# --- last-hour change per universe ticker-day, from the 1s tape ---------------
if not os.path.exists(args.cache):
    t0 = time.time()
    print(f"scanning 1s bars -> {args.cache} (~150s) ...")
    con.execute(f"""
    CREATE OR REPLACE TABLE lh AS
    SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date, ticker,
           max_by(vwap, bucket) FILTER (WHERE bucket <= 54000) AS p1500,
           max_by(vwap, bucket) FILTER (WHERE bucket <= 57600) AS p1600,
           count(*)    FILTER (WHERE bucket > 54000) AS nbars_lh,
           sum(volume) FILTER (WHERE bucket > 54000) AS vol_lh
    FROM read_parquet('{args.bars1s}', filename = true)
    WHERE bucket >= 50400 AND bucket <= 57600 GROUP BY 1, 2""")
    con.execute(f"""
    COPY (SELECT u.ticker, u.date, u.dv_0945_tape, l.p1500, l.p1600, l.nbars_lh, l.vol_lh,
                 l.p1600 / nullif(l.p1500, 0) - 1 AS lh_chg
          FROM db.mr_candidate_1s u JOIN lh l ON l.ticker = u.ticker AND l.date = u.date
          WHERE u.dv_0945_tape >= 2e6 AND u.n_bars_1s >= 200
            AND l.p1500 IS NOT NULL AND l.p1600 IS NOT NULL)
    TO '{args.cache}' (FORMAT PARQUET)""")
    print(f"  done in {time.time()-t0:.0f}s")
LH = f"read_parquet('{args.cache}')"

# --- the overnight leg: raw + splits + dividends added back -------------------
con.execute("""
CREATE OR REPLACE TABLE ov AS
WITH n AS (
  SELECT ticker, date, close AS rc, lead(open) OVER w AS o1, lead(date) OVER w AS d1
  FROM db.daily_prices WHERE date >= DATE '2016-01-01'
  WINDOW w AS (PARTITION BY ticker ORDER BY date)
), dv AS (
  SELECT ticker, ex_dividend_date, sum(cash_amount) AS cash FROM db.dividends GROUP BY 1, 2
)
SELECT n.ticker, n.date, n.rc, n.o1,
       COALESCE(s.split_ratio, 1.0) AS sr, COALESCE(dv.cash, 0.0) AS div1,
       (n.o1 * COALESCE(s.split_ratio, 1.0) + COALESCE(dv.cash, 0.0)) / nullif(n.rc, 0) - 1 AS ovn
FROM n LEFT JOIN db.splits s ON s.ticker = n.ticker AND s.execution_date = n.d1
       LEFT JOIN dv         ON dv.ticker = n.ticker AND dv.ex_dividend_date = n.d1""")

BUCKET = """CASE WHEN lh_chg < -0.06 THEN 'a  <-6%'
   WHEN lh_chg < -0.04  THEN 'b  -6..-4'   WHEN lh_chg < -0.03 THEN 'c  -4..-3'
   WHEN lh_chg < -0.02  THEN 'd  -3..-2'   WHEN lh_chg < -0.01 THEN 'e  -2..-1'
   WHEN lh_chg < -0.005 THEN 'f  -1..-0.5' WHEN lh_chg <  0.005 THEN 'g -0.5..+0.5'
   WHEN lh_chg <  0.02  THEN 'h +0.5..+2'  WHEN lh_chg <  0.04 THEN 'i  +2..+4'
   WHEN lh_chg <  0.06  THEN 'j  +4..+6'   ELSE 'k  >+6%' END"""


def population():
    con.execute(f"""CREATE OR REPLACE TABLE F AS
      SELECT l.*, o.sr, o.div1, o.ovn FROM {LH} l JOIN ov o USING (ticker, date)
      WHERE o.ovn IS NOT NULL""")
    d = con.execute("""SELECT count(*) n, sum(CASE WHEN abs(ovn)>0.5 THEN 1 ELSE 0 END) n_gt50,
      sum(CASE WHEN sr<>1 THEN 1 ELSE 0 END) n_split, sum(CASE WHEN div1>0 THEN 1 ELSE 0 END) n_div
      FROM F""").fetchdf()
    print(f"\npopulation: {int(d.n[0]):,} ticker-days   "
          f"|ovn|>50%: {int(d.n_gt50[0])} (corporate actions the splits table misses)   "
          f"splits {int(d.n_split[0])}   dividends {int(d.n_div[0])}")

    def table(where, title):
        print(f"\n{'='*168}\n{title}\n{'='*168}")
        tot = con.execute(f"""SELECT {BUCKET} b, count(*) n, median(ovn)*100 med,
          avg(CASE WHEN ovn>0 THEN 1.0 ELSE 0 END)*100 win,
          avg(CASE WHEN ovn>0.5 THEN 0.5 WHEN ovn<-0.5 THEN -0.5 ELSE ovn END)*100 wmean
          FROM F WHERE {where} GROUP BY 1 ORDER BY 1""").fetchdf()
        yr = con.execute(f"""SELECT {BUCKET} b, strftime(date,'%Y') y, median(ovn)*100 med
          FROM F WHERE {where} GROUP BY 1, 2""").fetchdf()
        out = tot.set_index("b"); out.columns = ["n", "med%", "win%", "wmean%"]
        print(pd.concat([out.round(3), yr.pivot(index="b", columns="y", values="med").round(2)],
                        axis=1).to_string())
        a = con.execute(f"""SELECT count(*) n, median(ovn)*100 m,
          avg(CASE WHEN ovn>0 THEN 1.0 ELSE 0 END)*100 w FROM F WHERE {where}""").fetchdf()
        print(f"ALL: n {int(a.n[0]):,}   median {a.m[0]:+.3f}%   win {a.w[0]:.1f}%")

    table("true", "OVERNIGHT close->next-open BY LAST-HOUR CHANGE — full 1s universe (median %, YEAR cols)")
    table(f"nbars_lh >= {args.liquid}", f"LIQUID last hour (>={args.liquid}/3600 s traded) — the g60-like slice")
    table(f"nbars_lh < {args.thin}", f"THIN last hour (<{args.thin}/3600 s traded) — where ALL MOC exits live")


def moc():
    d = con.execute(f"""
    SELECT t.symbol, t.trade_date, t.gap_60, t.entry_px, t.day_close, t.ret_exit,
           l.lh_chg, l.nbars_lh, o.ovn
    FROM read_parquet('{args.trips}') t
    LEFT JOIN {LH} l ON l.ticker = t.symbol AND l.date = CAST(t.trade_date AS DATE)
    LEFT JOIN ov   o ON o.ticker = t.symbol AND o.date = CAST(t.trade_date AS DATE)
    WHERE t.exit_reason = 'moc'""").fetchdf()

    print(f"\n{'='*90}\nMOC EXITS\n{'='*90}")
    print(con.execute(f"""SELECT exit_reason, count(*) n, min(gap_60) mn,
      quantile_cont(gap_60,0.05) p5, median(gap_60) med, max(gap_60) mx,
      sum(CASE WHEN gap_60<4 THEN 1 ELSE 0 END) n_g60, avg(ret_exit)*100 avg_pct
      FROM read_parquet('{args.trips}') GROUP BY 1 ORDER BY n DESC""").fetchdf().to_string(index=False))
    print("\n⭐ every MOC exit has gap_60 >= 23; the book requires gap_60 < 4 => "
          "the traded book contains ZERO MOC exits.")
    print(f"\nlast-hour change: median {d.lh_chg.median()*100:+.2f}%  "
          f"p10 {d.lh_chg.quantile(.1)*100:+.2f}%  p90 {d.lh_chg.quantile(.9)*100:+.2f}%")
    print(f"last-hour seconds traded (of 3600): median {d.nbars_lh.median():.0f}  "
          f"p90 {d.nbars_lh.quantile(.9):.0f}  max {d.nbars_lh.max():.0f}")

    d = d.dropna(subset=["ovn"]).copy()
    d["ret_close"] = d.day_close / d.entry_px - 1
    d["ret_open"] = d.day_close * (1 + d.ovn) / d.entry_px - 1
    tk = d.groupby(["symbol", "trade_date"]).agg(
        ovn=("ovn", "first"), rc=("ret_close", "mean"), ro=("ret_open", "mean")).reset_index()
    for name, o in (("trips", d.ovn.values), ("TICKER-DAYS", tk.ovn.values)):
        print(f"  {name:<12} n {len(o):>4}  mean {o.mean()*100:+.3f}%  median {np.median(o)*100:+.3f}%"
              f"  win {(o>0).mean()*100:5.1f}%  t {o.mean()/(o.std(ddof=1)/np.sqrt(len(o))):+.2f}")
    o = tk.ovn.values
    rng = np.random.default_rng(7)
    bs = np.array([rng.choice(o, len(o), replace=True).mean() for _ in range(20000)])
    print(f"  bootstrap 95% CI on the ticker-day overnight mean: "
          f"[{np.quantile(bs,.025)*100:+.2f}%, {np.quantile(bs,.975)*100:+.2f}%]")
    print(f"  exit @ close : mean {tk.rc.mean()*100:+.2f}%   median {tk.rc.median()*100:+.2f}%")
    print(f"  exit @ open  : mean {tk.ro.mean()*100:+.2f}%   median {tk.ro.median()*100:+.2f}%")

    p = con.execute(f"""SELECT count(*) n, median(o.ovn)*100 med, avg(o.ovn)*100 mean,
      avg(CASE WHEN o.ovn>0 THEN 1.0 ELSE 0 END)*100 win
      FROM {LH} l JOIN ov o USING (ticker, date)
      WHERE l.nbars_lh < 1900 AND l.lh_chg < -0.02""").fetchdf()
    print(f"\nMATCHED POPULATION (nbars_lh<1900 AND lh_chg<-2%, the MOC exits' own profile):"
          f"  n {int(p.n[0]):,}  median {p.med[0]:+.3f}%  mean {p['mean'][0]:+.3f}%  win {p.win[0]:.1f}%")
    print("=> the 43-ticker-day +0.97% is noise around a true effect of ~+0.1% gross,\n"
          "   which is below the spread on a name trading 535 of 3,600 seconds. DO NOT SWITCH.")


if args.stage in ("moc", "all"):
    moc()
if args.stage in ("pop", "all"):
    population()
