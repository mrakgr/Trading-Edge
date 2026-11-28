"""DATA v2 (2026-09-29): the daily data (data/v2, built by `TradingEdge.Database build-v2` / `backfill-daily`) and the
research universe (data/v2_candidates, built by `build_mr_candidate_1s.fsx --v2 data/v2`) as parquet, exposed to a DuckDB
connection under the v1 table names, so a query written for trading.db runs unchanged:

    con = duckdb.connect(); data_v2.attach(con)            # db.daily_prices, db.daily_adjusted, db.mr_candidate_1s_v2, ...
    con = duckdb.connect(); data_v2.attach(con, 'main')    # the same names, unqualified

`daily_episodes_causal` is created from this directory's build_daily_episodes_causal_view.sql — the one definition v1,
the Scanner and these scripts share.
"""
import os

DAILY = ['daily_prices', 'splits', 'dividends', 'ticker_reference', 'ticker_eras', 'ticker_types', 'split_corrections', 'daily_adjusted']
HERE = os.path.dirname(os.path.abspath(__file__))


def attach(con, schema='db', v2='data/v2', candidates='data/v2_candidates'):
    if not os.path.exists(os.path.join(v2, 'manifest.json')):
        raise SystemExit(f'{v2} is not a data v2 build (no manifest.json): run TradingEdge.Database build-v2 first')
    q = lambda p: os.path.abspath(p).replace("'", "''")
    pre = '' if schema == 'main' else f'{schema}.'
    if schema != 'main':
        con.execute(f'CREATE SCHEMA IF NOT EXISTS {schema}')
    for t in DAILY:
        con.execute(f"CREATE OR REPLACE VIEW {pre}{t} AS SELECT * FROM '{q(os.path.join(v2, t + '.parquet'))}'")
    view = open(os.path.join(HERE, 'build_daily_episodes_causal_view.sql')).read()
    for a, b in [('CREATE OR REPLACE VIEW daily_episodes_causal AS', f'CREATE OR REPLACE VIEW {pre}daily_episodes_causal AS'),
                 ('FROM daily_adjusted a', f'FROM {pre}daily_adjusted a'),
                 ('JOIN ticker_types t', f'JOIN {pre}ticker_types t')]:
        assert view.count(a) == 1, f'the view SQL changed: {a!r} not found once'
        view = view.replace(a, b)
    con.execute(view)
    cand = os.path.join(candidates, 'mr_candidate_1s_v2')
    if os.path.isdir(cand):
        con.execute(f"CREATE OR REPLACE VIEW {pre}mr_candidate_1s_v2 AS SELECT * FROM "
                    f"read_parquet('{q(os.path.join(cand, '**', '*.parquet'))}', hive_partitioning = true)")
    return con


def with_stock_type(con, df, ticker='symbol', date='entry_date', v2='data/v2'):
    """`df` with `typ`: the ticker's stock type ON `date`, from data v2's ticker_types ('CS', 'ADRC', or 'ADRC/CS' for a
    ticker with both rows; None when it was not a stock that day). The DATED replacement (2026-11-28) of the undated
    `string_agg(DISTINCT type, '/') FROM ticker_reference WHERE type IN ('CS','ADRC')` tag, which a reused symbol (RAM:
    a SPAC, then since 2026-06-24 a 2X ETF) passed on all its days. Row order and index are kept."""
    import pandas as pd
    q = os.path.abspath(os.path.join(v2, 'ticker_types.parquet')).replace("'", "''")
    keys = pd.DataFrame({'i': range(len(df)), 't': df[ticker].values, 'd': pd.to_datetime(df[date]).values})
    con.register('_with_stock_type_keys', keys)
    try:
        r = con.execute(f"""SELECT k.i, CASE WHEN tt.is_stock THEN tt.type END AS typ FROM _with_stock_type_keys k
                            LEFT JOIN '{q}' tt ON tt.ticker = k.t AND k.d::DATE BETWEEN tt.from_date AND tt.to_date
                            ORDER BY k.i""").df()
    finally:
        con.unregister('_with_stock_type_keys')
    assert len(r) == len(df), 'ticker_types fanned out: overlapping intervals'
    out = df.copy()
    out['typ'] = r.typ.values
    return out
