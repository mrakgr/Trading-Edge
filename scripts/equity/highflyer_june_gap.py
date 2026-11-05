"""v5c — WHERE DID JUNE'S HIGHFLYER HEADLINE GO? (user 2026-11-05: "investigate why the HighFlyer system has missing trips
now ... If that's not it, we'll have to branch out from the June commit and try rebuilding the database").

June's v3 headline (docs/highflyer_results.md "The system in one screen"): breadth lag1 > 0.5 · >= 2005: 4,245 trips,
PF raw 1.960 / clip 1.600, +$1.08M (unfiltered 6,647 / PF 1.851). The 11-01 repro of the documented command: 3,577 /
1.741 / 1.688 (unfiltered 5,884). Two findings:

 1. THE COUNT: the headline was measured BEFORE the max-log-ATR >= 0.04 floor went in (same day, 06-21: "Unfiltered-
    engine headline PF 1.851 -> 1.922, 6,647 -> 5,617 trips"); the table above it lists the floor but the numbers were
    never refreshed. The repro ran WITH the floor (the engine default). Like for like (--min-max-atr-log 0) today's
    engine gives MORE trips, not fewer.
 2. THE RAW TAIL: June's data lacked dozens of reverse splits (Polygon re-keyed / re-issued them later; the mirror fix
    40aef2b and the 09-24 history rewrite, docs/data_v2.md). June's own v0 trip files (data/equity/momentum_v0/*.csv,
    06-15/16, back-adjusted on June's tables) show it: the June/today price scale steps by exactly the split ratio.
    Where the missing split falls INSIDE a v3 hold, June's unadjusted entry against the post-split exit is a phantom
    moonshot. SNS (Steak n Shake) 2009-12-15: the 1-for-20 of 2009-12-21 sits inside the 12-15 -> 12-23 hold; June's
    entry $13.11 (scale 0.05 of today's) -> June ret ~ +2,342 % = +$232k of the ~$330k raw-P&L gap.

Inputs: data/equity/highflyer_repro/v3.csv (the 11-01 repro, floor on), v3_nofloor.csv (the same command with
--min-max-atr-log 0, logs/highflyer_repro_v3_nofloor.log), the June breadth parquet (06-22, unchanged), the June v0
trip CSVs, data/trading.db (read only: split_adjusted_prices, splits).
    cd research && python3 -u scripts/equity/highflyer_june_gap.py > data/equity/highflyer_repro/june_gap.md 2> logs/highflyer_june_gap.log
"""
import duckdb

con = duckdb.connect()
con.execute("SET memory_limit='3GB'")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")
con.execute("CREATE TABLE br AS SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b1 FROM 'data/equity/momentum_v0/breadth.parquet'")
for name, f in (("fl", "v3.csv"), ("nf", "v3_nofloor.csv")):
    con.execute(f"""CREATE TABLE {name} AS SELECT t.*, exit_price / entry_price - 1 AS ret, coalesce(br.b1 > 0.5, false) AND year(t.entry_date) >= 2005 AS inbook
                    FROM read_csv_auto('data/equity/highflyer_repro/{f}') t LEFT JOIN br ON br.date = t.entry_date""")

def stats(sql):
    n, raw, clip, pnl = con.execute(f"""WITH a AS ({sql}) SELECT count(*), sum(ret) FILTER (WHERE ret > 0) / -sum(ret) FILTER (WHERE ret < 0),
        sum(least(ret, .5)) FILTER (WHERE ret > 0) / -sum(ret) FILTER (WHERE ret < 0), sum(ret) * 1e4 FROM a""").fetchone()
    return f"{n:,} | {raw:.3f} | {clip:.3f} | +\\${pnl / 1e3:,.0f}k"

print("# v5c — June's HighFlyer headline vs today (scripts/equity/highflyer_june_gap.py)\n")
print("## 1. Like for like: the max-log-ATR floor\n")
print("| run | all trips | all PF (\\$) | breadth · ≥ 2005: trips | PF raw | PF clip | P&L (\\$10k / trip) |\n|---|---|---|---|---|---|---|")
print("| June headline (floor OFF) | 6,647 | 1.851 | 4,245 | 1.960 | 1.600 | +\\$1,079k |")
for name, lab in (("nf", "today, floor OFF"), ("fl", "today, floor ON (the 11-01 repro)")):
    n, pf = con.execute(f"SELECT count(*), sum(net_pnl) FILTER (WHERE net_pnl > 0) / -sum(net_pnl) FILTER (WHERE net_pnl < 0) FROM {name}").fetchone()
    print(f"| {lab} | {n:,} | {pf:.3f} | " + stats(f"SELECT ret FROM {name} WHERE inbook AND \"open\" = 0") + " |")
print("| June, floor ON (06-21) | 5,617 | 1.922 | — | — | — | — |\n")

print("## 2. Price bands (floor OFF, the book): June's \\$10–20 band was raw 2.854 / clip 1.780 (920 trips)\n")
print("| entry price | trips | PF raw | PF clip | units over the +50 % clip | max ret |\n|---|---|---|---|---|---|")
for r in con.execute("""SELECT CASE WHEN entry_price < 2 THEN '\\$1–2' WHEN entry_price < 3 THEN '\\$2–3' WHEN entry_price < 5 THEN '\\$3–5'
        WHEN entry_price < 10 THEN '\\$5–10' WHEN entry_price < 20 THEN '\\$10–20' ELSE '\\$20+' END b, min(entry_price) o, count(*),
        sum(ret) FILTER (WHERE ret > 0) / -sum(ret) FILTER (WHERE ret < 0), sum(least(ret, .5)) FILTER (WHERE ret > 0) / -sum(ret) FILTER (WHERE ret < 0),
        sum(greatest(ret - .5, 0)), max(ret) FROM nf WHERE inbook AND "open" = 0 GROUP BY 1 ORDER BY 2""").fetchall():
    print(f"| {r[0]} | {r[2]:,} | {r[3]:.3f} | {r[4]:.3f} | {r[5]:.2f} | {100 * r[6]:+.0f} % |")

print("\n## 3. The June scale (June's v0 trip prices / today's back-adjusted close) stepping INSIDE a book trip's hold\n")
con.execute("""CREATE TABLE v AS SELECT DISTINCT symbol, entry_date, exit_date, entry_price, exit_price FROM (
    SELECT symbol, entry_date, exit_date, entry_price, exit_price FROM read_csv_auto('data/equity/momentum_v0/trips_structure.csv')
    UNION ALL SELECT symbol, entry_date, exit_date, entry_price, exit_price FROM read_csv_auto('data/equity/momentum_v0/trips_variantB_qulla.csv')
    UNION ALL SELECT symbol, entry_date, exit_date, entry_price, exit_price FROM read_csv_auto('data/equity/momentum_v0/trips_adv100k.csv')) WHERE entry_price > 0""")
con.execute("""CREATE TABLE ob AS SELECT v.symbol, v.entry_date d, v.entry_price / a.adj_close r FROM v
               JOIN db.split_adjusted_prices a ON a.ticker = v.symbol AND a.date = v.entry_date WHERE a.adj_close > 0""")
con.execute("""CREATE TABLE ox AS SELECT DISTINCT v.symbol, v.exit_date d, least(v.exit_price / b.adj_open, v.exit_price / b.adj_close) r FROM v
               JOIN db.split_adjusted_prices b ON b.ticker = v.symbol AND b.date = v.exit_date WHERE b.adj_open > 0 AND b.adj_close > 0""")
rows = con.execute("""SELECT DISTINCT t.symbol, t.entry_date, t.exit_date, t.entry_price, t.ret, o.r, x.r FROM nf t
    JOIN ob o ON o.symbol = t.symbol AND o.d = t.entry_date JOIN ox x ON x.symbol = t.symbol AND x.d = t.exit_date
    WHERE t.inbook AND t."open" = 0 AND abs(ln(x.r / o.r)) > ln(1.3) ORDER BY t.entry_date""").fetchall()
print("| symbol | entry → exit | today's entry | today ret | June scale entry → exit | June ret | split inside the hold today |\n|---|---|---|---|---|---|---|")
for s, e, x, px, ret, r0, r1 in rows:
    sp = con.execute(f"SELECT string_agg(execution_date::VARCHAR || ' ' || split_from::INT || '→' || split_to::INT, ', ') FROM db.splits WHERE ticker = '{s}' AND execution_date > '{e}' AND execution_date <= '{x}'").fetchone()[0]
    print(f"| {s} | {e} → {x} | \\${px:,.2f} | {100 * ret:+.1f} % | {r0:.4f} → {r1:.4f} | {100 * ((1 + ret) * r1 / r0 - 1):+,.0f} % | {sp or '—'} |")

print("\n## 4. Today's book with SNS at its June return\n")
print("| book | trips | PF raw | PF clip | P&L |\n|---|---|---|---|---|")
print("| today, floor OFF | " + stats("SELECT ret FROM nf WHERE inbook AND \"open\" = 0") + " |")
print("| the same, SNS 2009-12-15 at June's scale | " + stats("""SELECT CASE WHEN symbol = 'SNS' AND entry_date = '2009-12-15' THEN (1 + ret) * 19.788 - 1 ELSE ret END ret
                                                              FROM nf WHERE inbook AND "open" = 0""") + " |")
print("| June headline | 4,245 | 1.960 | 1.600 | +\\$1,079k |")
