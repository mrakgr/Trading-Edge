"""S43bw/S43bx — build v45 by SPLICING, not by re-running 2,514 days.

⭐ THE ARGUMENT (user, 2026-08-14): the three engine changes cannot touch entries,
so only the trips whose EXIT can move need re-deriving.

  * `next_open` replaces the last-bar `moc` fill  -> touches only `moc` trips.
  * `aux_hi_60` is a recorded column              -> touches nothing.
  * `MocSecShort` bounds early-close days at 13:00 -> no v44 trip was ever open
    past 12:48 on one, so it moves nothing that exists.

And the mc=0 sampler opens a trip on every new low regardless of what is already
open, so an exit change cannot feed back into entries.

⭐ VERIFIED, not merely argued. Re-running the 42 days that carry `moc` trips on
the new corpus and joining to v44 on (symbol, trade_date, entry_sec):

    1,338 trips, 1,338 keys matched one-for-one, entry_px changed on 0
    target trips: 1,157   ret changed 0    exit_sec changed 0
    moc    trips:   181   ret changed 181  exit_sec changed 181

No NEW `moc` trip can appear either: the rebuilt corpus only ADDS tape at the end
of the session, which can resolve an open trip but can never leave one open that
previously closed.

⚠ THE COST, stated plainly: `aux_hi_60` exists ONLY on the 42 re-run days. The
1m/2m/5m/10m/20m exit-window sweep is therefore NOT answerable from v45 — it needs
a full rebuild. Everything else in v45 is exact.

Usage:  python scripts/equity/flushfader_splice_v45.py [scratch_dir]
"""
import os
import sys
import duckdb

SP = sys.argv[1] if len(sys.argv) > 1 else (
    "/tmp/claude-1000/-home-mrakgr-Trading-Edge/"
    "f7cfbf77-c979-4edf-9d2f-3251f3ede451/scratchpad")
V44 = "data/equity/flushfader/v44_causal/*.parquet"
NEW = f"{SP}/v45/*/*.parquet"
OUT_DIR = "data/equity/flushfader/v45_nextopen"
OUT = f"{OUT_DIR}/trips_p000.parquet"

con = duckdb.connect()
con.execute("SET enable_progress_bar=false")

days = [d[0] for d in con.execute(
    f"SELECT DISTINCT trade_date FROM read_parquet('{NEW}') ORDER BY 1").fetchall()]
print(f"re-run days: {len(days)}")

# ⭐ Guard the whole premise before writing anything: on the re-run days, every
# non-`moc` v44 trip must come back BIT-IDENTICAL. If that fails, the splice is
# invalid and a full rebuild is the only correct option.
chk = con.execute(f"""
SELECT count(*) AS matched,
       count(*) FILTER (v.exit_reason <> 'moc'
                        AND (abs(v.ret_exit - n.ret_exit) > 1e-12
                             OR v.exit_sec <> n.exit_sec)) AS unchanged_moved,
       count(*) FILTER (abs(v.entry_px - n.entry_px) > 1e-12) AS entry_moved,
       (SELECT count(*) FROM read_parquet('{V44}')
        WHERE trade_date IN (SELECT DISTINCT trade_date FROM read_parquet('{NEW}'))) AS v44_on_days,
       (SELECT count(*) FROM read_parquet('{NEW}')) AS new_on_days
FROM read_parquet('{V44}') v JOIN read_parquet('{NEW}') n
  USING (symbol, trade_date, entry_sec)""").fetchone()
matched, unchanged_moved, entry_moved, v44_on_days, new_on_days = chk
print(f"  v44 trips on those days: {v44_on_days}   re-run trips: {new_on_days}   "
      f"matched keys: {matched}")
print(f"  non-moc trips that MOVED: {unchanged_moved}   entry_px moved: {entry_moved}")
assert matched == v44_on_days == new_on_days, "trip sets differ — entries are NOT stable"
assert unchanged_moved == 0, "a non-moc trip changed — splice premise is false"
assert entry_moved == 0, "entry_px changed — splice premise is false"

os.makedirs(OUT_DIR, exist_ok=True)
# ⭐ NEW set FIRST so the output schema is the new one (with aux_hi_60); UNION ALL
# BY NAME then fills that column with NULL on the carried-over rows.
con.execute(f"""COPY (
    SELECT * FROM read_parquet('{NEW}')
    UNION ALL BY NAME
    SELECT * FROM read_parquet('{V44}')
    WHERE trade_date NOT IN (SELECT DISTINCT trade_date FROM read_parquet('{NEW}'))
) TO '{OUT}' (FORMAT PARQUET, COMPRESSION ZSTD)""")

print(f"\nwrote {OUT}")
print(con.execute(f"""SELECT exit_reason, count(*) n,
    count(*) FILTER (aux_hi_60_sec IS NOT NULL) AS has_aux60
    FROM read_parquet('{OUT}') GROUP BY 1 ORDER BY n DESC""").fetchdf().to_string(index=False))

tot_v44 = con.execute(f"SELECT count(*) FROM read_parquet('{V44}')").fetchone()[0]
tot_v45 = con.execute(f"SELECT count(*) FROM read_parquet('{OUT}')").fetchone()[0]
print(f"\ntrips: v44 {tot_v44:,} -> v45 {tot_v45:,}  (must be equal)")
assert tot_v44 == tot_v45, "trip count changed — splice dropped or duplicated rows"

# the identity every downstream tool relies on
err = con.execute(f"""SELECT max(abs(ret_exit - (exit_px/entry_px - 1)))
    FROM read_parquet('{OUT}') WHERE entry_px > 0""").fetchone()[0]
print(f"max |ret_exit - (exit_px/entry_px - 1)| = {err:.3e}")
