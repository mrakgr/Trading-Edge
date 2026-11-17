r"""BUILDUP STUDY, step 0 (user 2026-11-17: "focus on creating a signal to measure the potential market maker build up for a
longer term mean reversion system"). The signal is the SIGNED aggressor volume: the inventory the liquidity providers
were forced to absorb. The 1 s bars (data/intraday_1s_slim: ticker, bucket = ET second, vwap, volume, trade_count) carry
no aggressor side, so a bar is signed by the TICK RULE on its vwap (the sign of the change from the previous bar of the
ticker; an unchanged vwap inherits the last sign). Validated here against the TRUE aggressor side of the Databento MBO
trade records (T, side B = buy aggressor, A = sell; lit venues only) on every MBO name-day on disk: per minute and over
rolling 15 / 60 minutes.

  cd research && python -u scripts/equity/buildup_validate.py > data/equity/buildup/validate.md 2> logs/buildup_validate.log; echo exit $?
"""
import glob, os
import duckdb, numpy as np, pandas as pd

MBO = '/mnt/c/Users/mrakg/.tapesim/data/databento/mbo'
os.makedirs('data/equity/buildup', exist_ok=True)
con = duckdb.connect()
TICK = """
WITH x AS (SELECT bucket, vwap, volume FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
           WHERE ticker = '{t}' AND bucket >= 34200 AND bucket < 57600),
y AS (SELECT *, vwap - lag(vwap) OVER (ORDER BY bucket) AS dv FROM x),
z AS (SELECT *, last_value(CASE WHEN dv <> 0 THEN sign(dv) END IGNORE NULLS) OVER (ORDER BY bucket ROWS UNBOUNDED PRECEDING) AS s FROM y)
SELECT (bucket - 34200) // 60 AS m, sum(volume) vol, sum(coalesce(s, 0) * volume) sv FROM z GROUP BY m ORDER BY m"""
print('# The tick-rule signed volume (1 s bars, all prints) vs the true aggressor side (MBO, lit)\n')
print('| name-day | minutes | corr per minute | corr 15 min rolling | corr 60 min rolling | |imbalance| tick rule vs true (median ratio) |')
print('|---|---|---|---|---|---|')
for d in sorted(glob.glob(f'{MBO}/*/*/')):
    files = glob.glob(f'{d}*.dbn.zst')
    if len(files) < 5: continue
    t = d.rstrip('/').split('/')[-2]; day = d.rstrip('/').split('/')[-1]
    import databento as db
    parts = []
    for f in files:
        x = db.DBNStore.from_file(f).to_df(map_symbols=False, pretty_ts=False)
        x = x[x.action == 'T']
        parts.append(pd.DataFrame(dict(ts=x.index.to_numpy(np.int64), side=x.side.to_numpy(), size=x['size'].to_numpy(float))))
    T = pd.concat(parts)
    et = pd.to_datetime(T.ts).dt.tz_localize('UTC').dt.tz_convert('America/New_York')
    sec = et.dt.hour * 3600 + et.dt.minute * 60 + et.dt.second
    T = T[(sec >= 34200) & (sec < 57600)].assign(m=((sec - 34200) // 60)[(sec >= 34200) & (sec < 57600)])
    T['sv'] = np.where(T.side == 'B', T['size'], np.where(T.side == 'A', -T['size'], 0.0))
    A = T.groupby('m').agg(true_sv=('sv', 'sum'), true_vol=('size', 'sum'))
    B = con.execute(TICK.format(d=day, t=t)).df().set_index('m')
    J = A.join(B, how='inner')
    J = J[J.vol > 0]
    if len(J) < 30: continue
    r = [J.true_sv.corr(J.sv)]
    for w in (15, 60):
        r.append(J.true_sv.rolling(w).sum().corr(J.sv.rolling(w).sum()))
    ratio = (J.sv.abs() / J.vol) / (J.true_sv.abs() / J.true_vol).replace(0, np.nan)
    print(f'| {t} {day} | {len(J)} | {r[0]:+.2f} | {r[1]:+.2f} | {r[2]:+.2f} | {ratio.median():.2f} |', flush=True)
