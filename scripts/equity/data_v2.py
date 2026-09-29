"""DATA v2 (2026-09-29): the daily data (data/v2, built by `TradingEdge.Database build-v2` / `backfill-daily`) and the
research universe (data/v2_candidates, built by `build_mr_candidate_1s.fsx --v2 data/v2`) as parquet, exposed to a DuckDB
connection under the v1 table names, so a query written for trading.db runs unchanged:

    con = duckdb.connect(); data_v2.attach(con)            # db.daily_prices, db.daily_adjusted, db.mr_candidate_1s_v2, ...
    con = duckdb.connect(); data_v2.attach(con, 'main')    # the same names, unqualified

`daily_episodes_causal` is created from this directory's build_daily_episodes_causal_view.sql — the one definition v1,
the Scanner and these scripts share.
"""
import os

DAILY = ['daily_prices', 'splits', 'dividends', 'ticker_reference', 'split_corrections', 'daily_adjusted']
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
                 ('FROM ticker_reference r', f'FROM {pre}ticker_reference r')]:
        assert view.count(a) == 1, f'the view SQL changed: {a!r} not found once'
        view = view.replace(a, b)
    con.execute(view)
    cand = os.path.join(candidates, 'mr_candidate_1s_v2')
    if os.path.isdir(cand):
        con.execute(f"CREATE OR REPLACE VIEW {pre}mr_candidate_1s_v2 AS SELECT * FROM "
                    f"read_parquet('{q(os.path.join(cand, '**', '*.parquet'))}', hive_partitioning = true)")
    return con
