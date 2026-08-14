"""S43bw — what do FlushFader's MOC exits become if the tape runs past 16:00?

FlushFader force-exits any still-open position at `MocSec = 57600` (16:00 ET).
Until 2026-08-13 that bound was doubly enforced: the engine's own rule AND the
1s corpus itself, which stopped at 15:58:59. The full-day rebuild removed the
second one, so the question is finally askable — a MOC exit is a *forced* exit,
and forced exits are the ones most likely to be leaving money on the table.

⭐ TWO ARMS, so the two changes do not get confounded. The v44 book was built on
the OLD corpus, which is missing BOTH the final RTH minute and the post-market:

    v44 : old corpus, moc 57600  -> exits pin at 57539 (the old tape's last bar)
    ctl : NEW corpus, moc 57600  -> isolates the newly-visible final RTH minute
    ext : NEW corpus, moc 86399  -> adds ~4h of post-market

ctl-vs-v44 is the control (CLAUDE.md rule 6): it must move only by the last
minute's worth of price, or something other than the session bound changed.

⚠ These trips are the mc=0 SAMPLER, and the production book contains ZERO MOC
exits (they all fail `gap_60 < 4`). So nothing here changes current P&L. It
answers whether the MOC bound is *costing* anything, which is what decides
whether an after-hours exit variant is worth building.

⚠ Post-market tape is sparse, and every FlushFader channel is measured in
PRESENT BARS, not wall-clock seconds. A "5-minute high" (300 present bars) can
span hours after 16:00. Exits resolving deep in the post-market are therefore
resolving on a different clock than the one the rule was tuned on — read the
exit_sec distribution before reading the returns.

Usage:  python scripts/equity/flushfader_moc_extended.py [scratch_dir]
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
KEY = "symbol, trade_date, entry_sec"

con = duckdb.connect()
con.execute("SET enable_progress_bar=false")
for arm in ("ctl", "ext"):
    con.execute(f"""CREATE OR REPLACE TEMP VIEW {arm} AS
        SELECT * FROM read_parquet('{SP}/moc_{arm}/*/*.parquet')""")

con.execute(f"""CREATE OR REPLACE TEMP TABLE M AS
SELECT a.symbol, a.trade_date, a.entry_sec, a.entry_px, a.gap_60,
       a.exit_sec AS sec_v44, a.exit_px AS px_v44, a.ret_exit AS ret_v44,
       c.exit_sec AS sec_ctl, c.exit_reason AS why_ctl, c.ret_exit AS ret_ctl,
       e.exit_sec AS sec_ext, e.exit_reason AS why_ext, e.ret_exit AS ret_ext,
       -- the overnight alternative: hold to the next session's OPEN instead
       (a.open_p1 + a.div_p1) / a.entry_px - 1 AS ret_ovn
FROM read_parquet('{V44}') a
LEFT JOIN ctl c USING ({KEY})
LEFT JOIN ext e USING ({KEY})
WHERE a.exit_reason = 'moc'""")

n, miss_c, miss_e = con.execute(
    "SELECT count(*), count(*) FILTER (ret_ctl IS NULL), "
    "count(*) FILTER (ret_ext IS NULL) FROM M").fetchone()
print(f"v44 MOC trips: {n}   unmatched in ctl: {miss_c}   in ext: {miss_e}")
if miss_c or miss_e:
    print("  ⚠ entries should be IDENTICAL across arms (entry cutoff 15:00 is far\n"
          "    below every session bound) — any miss is a bug, not a finding.")

print("\n=== 1. does the extra tape change the exit at all? ===")
print(con.execute("""SELECT why_ext AS exit_reason_ext, count(*) n,
    count(*) FILTER (sec_ext > 57600) AS resolved_after_1600,
    round(median(sec_ext)) AS med_exit_sec,
    round(max(sec_ext)) AS last_exit_sec
    FROM M GROUP BY 1 ORDER BY n DESC""").fetchdf().to_string(index=False))

print("\n=== 2. WHEN they resolve once 16:00 stops being a wall ===")
print(con.execute("""SELECT CASE
      WHEN sec_ext <= 57600 THEN 'a. by 16:00'
      WHEN sec_ext <= 61200 THEN 'b. 16:00-17:00'
      WHEN sec_ext <= 64800 THEN 'c. 17:00-18:00'
      WHEN sec_ext <= 72000 THEN 'd. 18:00-20:00'
      ELSE 'e. after 20:00' END AS window,
    count(*) n, round(avg(ret_ext)*100, 3) AS mean_ret,
    round(median(ret_ext)*100, 3) AS med_ret,
    round(avg(CASE WHEN ret_ext > 0 THEN 1.0 ELSE 0 END)*100) AS win_pct
    FROM M GROUP BY 1 ORDER BY 1""").fetchdf().to_string(index=False))

print("\n=== 3. ⭐ THE COMPARISON — same 181 trips, four exit rules ===")
rows = []
for lbl, col in [("v44  moc 16:00 (old corpus)", "ret_v44"),
                 ("ctl  moc 16:00 (new corpus)", "ret_ctl"),
                 ("ext  run to tape end",        "ret_ext"),
                 ("ovn  hold to next open",      "ret_ovn")]:
    # ⚠ alias away from DataFrame method names (`mean`, `min`, `max`) — `d.mean`
    # resolves to the METHOD, not the column, and fails only at subscript time.
    d = con.execute(f"""SELECT count({col}) n_, avg({col})*100 mean_,
        median({col})*100 med_, avg(CASE WHEN {col}>0 THEN 1.0 ELSE 0 END)*100 w_,
        sum(CASE WHEN {col}>0 THEN {col} ELSE 0 END)
          / nullif(-sum(CASE WHEN {col}<0 THEN {col} ELSE 0 END),0) pf_,
        min({col})*100 worst_, max({col})*100 best_ FROM M""").fetchdf()
    rows.append({"rule": lbl, "n": int(d.n_[0]), "mean%": round(d.mean_[0], 3),
                 "median%": round(d.med_[0], 3), "win%": round(d.w_[0], 1),
                 "PF": round(d.pf_[0], 3) if pd.notna(d.pf_[0]) else float("inf"),
                 "worst%": round(d.worst_[0], 2), "best%": round(d.best_[0], 2)})
print(pd.DataFrame(rows).to_string(index=False))

print("\n=== 4. PAIRED differences (the only honest read — same trips) ===")
print(con.execute("""SELECT 'ctl - v44' AS delta, count(*) n,
      count(*) FILTER (abs(ret_ctl-ret_v44) > 1e-12) AS n_changed,
      round(avg(ret_ctl-ret_v44)*100, 4) AS mean_pp,
      round(median(ret_ctl-ret_v44)*100, 4) AS med_pp,
      round(max(abs(ret_ctl-ret_v44))*100, 2) AS max_abs_pp FROM M
    UNION ALL SELECT 'ext - ctl', count(*),
      count(*) FILTER (abs(ret_ext-ret_ctl) > 1e-12),
      round(avg(ret_ext-ret_ctl)*100, 4), round(median(ret_ext-ret_ctl)*100, 4),
      round(max(abs(ret_ext-ret_ctl))*100, 2) FROM M
    UNION ALL SELECT 'ovn - ext', count(*),
      count(*) FILTER (abs(ret_ovn-ret_ext) > 1e-12),
      round(avg(ret_ovn-ret_ext)*100, 4), round(median(ret_ovn-ret_ext)*100, 4),
      round(max(abs(ret_ovn-ret_ext))*100, 2) FROM M""").fetchdf()
      .to_string(index=False))

print("\n=== 5. the biggest movers, ext vs ctl ===")
print(con.execute("""SELECT symbol, trade_date, entry_sec, sec_ctl, sec_ext,
      why_ext, round(gap_60) gap60, round(ret_ctl*100,2) ret_ctl,
      round(ret_ext*100,2) ret_ext, round(ret_ovn*100,2) ret_ovn
    FROM M ORDER BY abs(ret_ext-ret_ctl) DESC LIMIT 15""")
      .fetchdf().to_string(index=False))

print("\n=== 6. book-eligible slice (gap_60 < 4 — the production door) ===")
print(con.execute("""SELECT count(*) n_book_eligible FROM M WHERE gap_60 < 4""")
      .fetchdf().to_string(index=False))
print("  (the production book contains ZERO MOC exits; a nonzero count here would\n"
      "   mean the door moved, not that the exit rule did)")
