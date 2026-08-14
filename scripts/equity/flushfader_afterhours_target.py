"""S43bw — should an unresolved FlushFader trip exit AFTER HOURS or at the NEXT OPEN?

Context. A `moc` exit means the ~5m-high reversion target was never hit during the
session. As of 2026-08-14 those no longer dump into the last bar; they hold
overnight and exit at the next session's open. The open question is whether the
post-market session should get a chance first — and if so, on what target.

⭐ The reason a SECOND target is even in play: every FlushFader channel counts
PRESENT BARS, not wall-clock seconds. Post-market tape is sparse, so a 300-bar
"5m high" can span hours after 16:00, while a 60-bar rung stays reachable. So the
arms vary the after-hours target, not just whether the session runs:

    v44  : moc at the last bar               (the old rule, old corpus)
    ovn  : 16:00 -> next open                (the new rule; no post-market at all)
    ah5m : post-market, SAME 5m target, else next open
    ah1m : post-market, TIGHTER 1m target after 16:00, else next open

All arms share the same 181 trips and the same next-open fallback, so they differ
only in the after-hours rule. Paired differences are the honest read.

⚠ These are the mc=0 SAMPLER's unresolved trips, and NONE of them are in the
production book (all fail `gap_60 < 4`). Nothing here moves current P&L.

Usage:  python scripts/equity/flushfader_afterhours_target.py [scratch_dir]
"""
import sys
import duckdb
import pandas as pd

pd.set_option("display.width", 220)
pd.set_option("display.max_columns", 40)

SP = sys.argv[1] if len(sys.argv) > 1 else (
    "/tmp/claude-1000/-home-mrakgr-Trading-Edge/"
    "f7cfbf77-c979-4edf-9d2f-3251f3ede451/scratchpad")
V44 = "data/equity/flushfader/v44_causal/*.parquet"

con = duckdb.connect()
con.execute("SET enable_progress_bar=false")
for arm, d in (("ovn", "moc_ovn"), ("a5", "moc_ah5m"), ("a1", "moc_ah1m")):
    con.execute(f"CREATE OR REPLACE TEMP VIEW {arm} AS "
                f"SELECT * FROM read_parquet('{SP}/{d}/*/*.parquet')")

con.execute(f"""CREATE OR REPLACE TEMP TABLE M AS
SELECT a.symbol, a.trade_date, a.entry_sec, a.gap_60, a.gap_1200, a.dv_0945_tape,
       a.ret_exit AS ret_v44,
       o.ret_exit AS ret_ovn,
       f.ret_exit AS ret_a5, f.exit_reason AS why_a5, f.exit_sec AS sec_a5,
       g.ret_exit AS ret_a1, g.exit_reason AS why_a1, g.exit_sec AS sec_a1
FROM read_parquet('{V44}') a
JOIN ovn o USING (symbol, trade_date, entry_sec)
JOIN a5  f USING (symbol, trade_date, entry_sec)
JOIN a1  g USING (symbol, trade_date, entry_sec)
WHERE a.exit_reason = 'moc'""")
print(f"matched trips: {con.execute('SELECT count(*) FROM M').fetchone()[0]} of 181\n")

ARMS = [("v44  moc at the last bar",       "ret_v44"),
        ("ovn  16:00 -> next open",        "ret_ovn"),
        ("ah5m post-mkt 5m tgt -> open",   "ret_a5"),
        ("ah1m post-mkt 1m tgt -> open",   "ret_a1")]

print("=== ⭐ THE COMPARISON — same 181 trips ===")
rows = []
for lbl, c in ARMS:
    # ⚠ alias away from DataFrame method names — `d.mean` is the METHOD.
    d = con.execute(f"""SELECT avg({c})*100 mean_, median({c})*100 med_,
        avg(CASE WHEN {c}>0 THEN 1.0 ELSE 0 END)*100 w_,
        sum(CASE WHEN {c}>0 THEN {c} ELSE 0 END)
          / nullif(-sum(CASE WHEN {c}<0 THEN {c} ELSE 0 END),0) pf_,
        min({c})*100 worst_, max({c})*100 best_ FROM M""").fetchdf()
    rows.append({"rule": lbl, "mean%": round(d.mean_[0], 3),
                 "median%": round(d.med_[0], 3), "win%": round(d.w_[0], 1),
                 "PF": round(d.pf_[0], 3), "worst%": round(d.worst_[0], 2),
                 "best%": round(d.best_[0], 2)})
print(pd.DataFrame(rows).to_string(index=False))

print("\n=== how each after-hours arm resolves ===")
for arm, why, sec in (("ah5m", "why_a5", "sec_a5"), ("ah1m", "why_a1", "sec_a1")):
    d = con.execute(f"""SELECT '{arm}' arm, {why} reason, count(*) n,
        count(*) FILTER ({sec} > 57600) AS after_1600,
        round(median({sec})) AS med_exit_sec FROM M GROUP BY 1,2""").fetchdf()
    print(d.to_string(index=False))

print("\n=== PAIRED differences vs the next-open rule (positive = the arm is better) ===")
print(con.execute("""SELECT 'ah5m - ovn' AS delta, count(*) FILTER (abs(ret_a5-ret_ovn)>1e-12) n_changed,
      round(avg(ret_a5-ret_ovn)*100,4) mean_pp, round(median(ret_a5-ret_ovn)*100,4) med_pp,
      count(*) FILTER (ret_a5 > ret_ovn + 1e-12) AS n_better,
      count(*) FILTER (ret_a5 < ret_ovn - 1e-12) AS n_worse FROM M
    UNION ALL SELECT 'ah1m - ovn', count(*) FILTER (abs(ret_a1-ret_ovn)>1e-12),
      round(avg(ret_a1-ret_ovn)*100,4), round(median(ret_a1-ret_ovn)*100,4),
      count(*) FILTER (ret_a1 > ret_ovn + 1e-12),
      count(*) FILTER (ret_a1 < ret_ovn - 1e-12) FROM M
    UNION ALL SELECT 'ah1m - ah5m', count(*) FILTER (abs(ret_a1-ret_a5)>1e-12),
      round(avg(ret_a1-ret_a5)*100,4), round(median(ret_a1-ret_a5)*100,4),
      count(*) FILTER (ret_a1 > ret_a5 + 1e-12),
      count(*) FILTER (ret_a1 < ret_a5 - 1e-12) FROM M""").fetchdf().to_string(index=False))

print("\n=== ⭐ is the unresolved bucket the ILLIQUID one? (vs the resolved sampler) ===")
print(con.execute(f"""SELECT
    CASE WHEN exit_reason='moc' THEN 'unresolved (moc)' ELSE 'resolved (target)' END AS grp,
    count(*) n, round(median(gap_60),1) med_gap60,
    round(median(gap_1200),1) med_gap1200, round(median(dv_0945_tape)/1e6,2) med_dv_M
    FROM read_parquet('{V44}') GROUP BY 1 ORDER BY 1""").fetchdf().to_string(index=False))
print("  gap_60 = seconds of the last 60 that did NOT trade; gap_1200 likewise of 1200.")

print("\n=== does the next-open edge survive INSIDE the thin bucket? ===")
print(con.execute("""SELECT CASE WHEN gap_1200 < 900 THEN 'a. gap1200 < 900'
        WHEN gap_1200 < 1100 THEN 'b. 900-1100' ELSE 'c. >= 1100 (thinnest)' END AS bucket,
    count(*) n, round(avg(ret_v44)*100,2) v44, round(avg(ret_ovn)*100,2) ovn,
    round(avg(ret_a1)*100,2) ah1m,
    round(avg(ret_ovn-ret_v44)*100,2) AS ovn_minus_v44_pp
    FROM M GROUP BY 1 ORDER BY 1""").fetchdf().to_string(index=False))
