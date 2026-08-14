"""S43bx — how much tape is there after a 13:00 early close?

The question. FlushFader's `MocSec` is a flat 57600 (16:00) on every day, and the
1s corpus now carries the full day, so on the 21 NYSE half-days in sample the
engine reads ~3 hours of POST-MARKET tape as if it were RTH. With the new
next-open exit rule (S43bw), a position still open at 13:00 keeps trading in that
post-close tape until 16:00 instead of going overnight. Before deciding whether
that is right, measure what is actually there.

Three windows, all measured on the candidate universe only (so it reflects what
the engine trades, not the whole tape):

    W1  [09:30, 13:00)  buckets 34200-46799   12,600s   the half-day's RTH
    W2  [13:00, 16:00)  buckets 46800-57599   10,800s   post-close on a half-day,
                                                        a normal afternoon otherwise

⭐ THE METRIC THAT MATTERS IS DENSITY, not dollars. Every FlushFader channel counts
PRESENT BARS, so a "5m high" is 300 traded seconds however long they take. A window
with half the dollars but a tenth of the traded seconds is far more damaging to the
exit rule than the dollar figure suggests.

⭐ MATCHED CONTROL. Half-days are all holiday-adjacent (Jul 3, the Friday after
Thanksgiving, Dec 24), so the whole period is thin — comparing them to the annual
average would confound "half-day" with "holiday week". Each early close is
therefore compared against the nearest REGULAR trading days on either side, and a
broad random sample is reported alongside so the seasonal effect is visible.

Usage:  python scripts/equity/earlyclose_liquidity.py
"""
import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 240)
pd.set_option("display.max_columns", 40)

EARLY = ["2016-11-25", "2017-07-03", "2017-11-24", "2018-07-03", "2018-11-23",
         "2018-12-24", "2019-07-03", "2019-11-29", "2019-12-24", "2020-11-27",
         "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24",
         "2024-07-03", "2024-11-29", "2024-12-24", "2025-07-03", "2025-11-28",
         "2025-12-24"]
NEIGHBOURS = 2          # regular days taken on EACH side of every early close
SAMPLE_REGULAR = 60     # broad random regular-day sample, for the seasonal read

con = duckdb.connect()
con.execute("SET enable_progress_bar=false")
con.execute("ATTACH 'data/trading.db' AS db (READ_ONLY)")

days = sorted(f.split("/")[-1].removesuffix(".parquet")
              for f in con.execute(
                  "SELECT file FROM glob('data/intraday_1s_slim/*.parquet')")
              .fetchdf().file)

early = [d for d in EARLY if d in days]
idx = {d: i for i, d in enumerate(days)}
early_set = set(early)

matched = set()
for d in early:
    i = idx[d]
    for j in range(i - NEIGHBOURS, i + NEIGHBOURS + 1):
        if 0 <= j < len(days) and days[j] not in early_set:
            matched.add(days[j])
matched = sorted(matched)

rng = np.random.default_rng(0)
pool = [d for d in days if d not in early_set and d not in set(matched)]
broad = sorted(rng.choice(pool, SAMPLE_REGULAR, replace=False).tolist())

print(f"early closes: {len(early)}   matched neighbours: {len(matched)}   "
      f"broad regular sample: {len(broad)}\n")


def scan(daylist, label):
    """Per (ticker, day) liquidity in W1 and W2, candidate universe only."""
    files = ", ".join(f"'data/intraday_1s_slim/{d}.parquet'" for d in daylist)
    return con.execute(f"""
    WITH raw AS (
      SELECT ticker,
        regexp_extract(filename, '([0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}})\\.parquet', 1)::DATE AS date,
        count(*) FILTER (bucket >= 34200 AND bucket < 46800) AS bars_w1,
        count(*) FILTER (bucket >= 46800 AND bucket < 57600) AS bars_w2,
        sum(vwap*volume) FILTER (bucket >= 34200 AND bucket < 46800) AS dv_w1,
        sum(vwap*volume) FILTER (bucket >= 46800 AND bucket < 57600) AS dv_w2,
        sum(trade_count) FILTER (bucket >= 34200 AND bucket < 46800) AS tc_w1,
        sum(trade_count) FILTER (bucket >= 46800 AND bucket < 57600) AS tc_w2
      FROM read_parquet([{files}], filename = true)
      WHERE bucket >= 34200 AND bucket < 57600
      GROUP BY 1, 2)
    SELECT '{label}' AS grp, r.* FROM raw r
    JOIN db.mr_candidate_1s_v2 c ON c.ticker = r.ticker AND c.date = r.date
    WHERE r.bars_w1 > 0""").fetchdf()


parts = [scan(early, "early-close"), scan(matched, "regular (matched)"),
         scan(broad, "regular (broad)")]
df = pd.concat(parts, ignore_index=True)
print(f"ticker-days scanned: {len(df):,}\n")

print("=== 1. ⭐ DENSITY — traded seconds, and what fraction of the window they are ===")
rows = []
for g, d in df.groupby("grp", sort=False):
    rows.append({
        "group": g, "ticker-days": f"{len(d):,}",
        "W1 bars (med)": int(d.bars_w1.median()),
        "W1 % of 12600s": f"{d.bars_w1.median()/12600*100:.1f}%",
        "W2 bars (med)": int(d.bars_w2.median()),
        "W2 % of 10800s": f"{d.bars_w2.median()/10800*100:.1f}%",
        "W2/W1 per-sec": f"{(d.bars_w2/10800).median()/(d.bars_w1/12600).median():.3f}",
    })
print(pd.DataFrame(rows).to_string(index=False))

print("\n=== 2. DOLLARS — the same windows, median $ per ticker-day ===")
rows = []
for g, d in df.groupby("grp", sort=False):
    rows.append({"group": g,
                 "W1 $ (med)": f"${d.dv_w1.median()/1e6:.2f}M",
                 "W2 $ (med)": f"${d.dv_w2.median()/1e6:.2f}M",
                 "W2/W1 $": f"{(d.dv_w2/d.dv_w1.replace(0, np.nan)).median():.3f}",
                 "W2/W1 trades": f"{(d.tc_w2/d.tc_w1.replace(0, np.nan)).median():.3f}"})
print(pd.DataFrame(rows).to_string(index=False))

print("\n=== 3. ⭐ THE EXIT-RULE QUESTION — can a 300-bar (5m) high even form in W2? ===")
print("   a trip needs 300 TRADED SECONDS in the window to make one 5m high at all.")
rows = []
for g, d in df.groupby("grp", sort=False):
    n = len(d)
    rows.append({"group": g, "ticker-days": f"{n:,}",
                 ">=300 bars in W2": f"{(d.bars_w2 >= 300).mean()*100:.1f}%",
                 ">=60 bars (1m)":   f"{(d.bars_w2 >= 60).mean()*100:.1f}%",
                 "ZERO bars in W2":  f"{(d.bars_w2 == 0).mean()*100:.1f}%",
                 "med bars W2":      int(d.bars_w2.median())})
print(pd.DataFrame(rows).to_string(index=False))

print("\n=== 4. per early-close day (is any one day driving it?) ===")
e = df[df.grp == "early-close"]
print(e.groupby("date").agg(
    ticker_days=("ticker", "size"), med_bars_w1=("bars_w1", "median"),
    med_bars_w2=("bars_w2", "median"),
    pct_ge300=("bars_w2", lambda s: round((s >= 300).mean()*100, 1)),
    med_dv_w2_M=("dv_w2", lambda s: round(s.median()/1e6, 2)),
).to_string())
