"""LowFader SPEC-SUPERSET whitelist prepass (2026-09-04): provably complete for LowFlyer's spec book, not for the base-run signal. Adds: adv >= 500k (avgvol20_prior x close_m1), rvol_0945_honest >= 0.1 (table-exact), and the chg_1d <= -8% bound: some bar in [09:45,15:01] <= 0.92 x close_m1 (entry_px is a bar in that window, so the gate implies this). (2026-09-03). PURE-SQL, provably a SUPERSET of the
engine's --base-run signal, so the whitelist rerun is complete by construction.
Flag a candidate ticker-day iff ALL of:
  * barnum >= 22                                   (the engine's table gate)
  * some bar with bucket in [09:45, 15:00] prints STRICTLY above the running
    max of all strictly-prior bars since 09:30      (the SESSION channel trigger;
                                                    the engine's entry window)
  * max |ln(slot_vwap_k / slot_vwap_{k-1})| over full 30-present-bar slots
    >= 40bp - 1e-6                                 (volat_20m is a CONVEX
    combination of this |r| stream, so a day whose max is under the floor can
    NEVER open the gate — the S39j argument, rebuilt here because
    mr_candidate_1s_v2 has no max_slot_absr_bp column)
The dv60/tc60 floors are deliberately NOT applied: the engine's window is
halt-adjusted tradeable time, which can exceed 60 wall-clock seconds, so a
wall-clock SQL floor could reject a day the engine accepts. Floors only shrink
the engine's set; leaving them out keeps this a superset.
One-day check (2021-03-01): 44/44 smoke ticker-days covered.
"""
import duckdb, os, sys, time, glob
SEC="data/intraday_1s_slim"; OUT="data/lowfader_spec_whitelist.parquet"
c=duckdb.connect("data/trading.db", read_only=True)
c.execute("SET memory_limit='8GB'; SET threads=8")
days=[r[0] for r in c.execute("SELECT DISTINCT date FROM mr_candidate_1s_v2 WHERE barnum >= 22 AND avgvol20_prior*close_m1 >= 5e5 AND rvol_0945_honest >= 0.1 ORDER BY date").fetchall()]
print(f"{len(days)} candidate days", flush=True)
rows=[]; t0=time.time(); missing=0
Q="""
WITH cand AS (SELECT ticker, close_m1 FROM mr_candidate_1s_v2 WHERE date = ? AND barnum >= 22 AND avgvol20_prior*close_m1 >= 5e5 AND rvol_0945_honest >= 0.1),
tape AS (
  SELECT ticker, bucket, vwap, volume,
         min(vwap) OVER (PARTITION BY ticker ORDER BY bucket
                         ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS prior_min,
         (row_number() OVER (PARTITION BY ticker ORDER BY bucket) - 1) // 30 AS slot
  FROM read_parquet(?)
  WHERE ticker IN (SELECT ticker FROM cand) AND bucket >= 34200 AND bucket < 57600
    AND vwap > 0 AND volume > 0),
sh AS (SELECT t.ticker FROM tape t JOIN cand c ON c.ticker = t.ticker WHERE t.bucket >= 35100 AND t.bucket <= 54000 AND t.vwap < t.prior_min GROUP BY 1),
deep AS (SELECT t.ticker FROM tape t JOIN cand c ON c.ticker = t.ticker WHERE t.bucket >= 35100 AND t.bucket <= 54001 AND t.vwap <= 0.92 * c.close_m1 GROUP BY 1),
slots AS (SELECT ticker, slot, sum(vwap*volume)/sum(volume) AS sv, count(*) AS n FROM tape GROUP BY 1,2),
sr AS (SELECT ticker, abs(ln(sv / lag(sv) OVER (PARTITION BY ticker ORDER BY slot))) AS r FROM slots WHERE n = 30),
vol AS (SELECT ticker FROM sr GROUP BY 1 HAVING max(r) >= 0.0040 - 1e-6)
SELECT ticker FROM sh WHERE ticker IN (SELECT ticker FROM vol) AND ticker IN (SELECT ticker FROM deep)"""
for i,d in enumerate(days):
    path=f"{SEC}/{d}.parquet"
    if not os.path.exists(path): missing+=1; continue
    for (t,) in c.execute(Q,[d,path]).fetchall(): rows.append((t,d))
    if (i+1)%50==0 or i+1==len(days):
        el=time.time()-t0
        print(f"  {d}  day {i+1}/{len(days)}  flagged {len(rows):,}  {el:.0f}s  ~{el/(i+1)*(len(days)-i-1)/60:.0f}m left", flush=True)
print(f"done: {len(rows):,} tkd flagged, {missing} days without a tape file, {time.time()-t0:.0f}s", flush=True)
w=duckdb.connect()
w.execute("CREATE TABLE wl (ticker VARCHAR, date DATE)")
w.executemany("INSERT INTO wl VALUES (?,?)", rows)
w.execute(f"COPY wl TO '{OUT}' (FORMAT PARQUET)")
print(f"wrote {OUT}", flush=True)
# materialize the candidate-schema table the engine reads via FF_CANDIDATE_TABLE
c.close()
wc=duckdb.connect("data/trading.db")
wc.execute("DROP TABLE IF EXISTS lowfader_spec_whitelist")
wc.execute(f"""CREATE TABLE lowfader_spec_whitelist AS
  SELECT m.* FROM mr_candidate_1s_v2 m
  WHERE EXISTS (SELECT 1 FROM read_parquet('{OUT}') w WHERE w.ticker = m.ticker AND w.date = m.date)""")
n=wc.execute("SELECT count(*), count(DISTINCT ticker), min(date), max(date) FROM lowfader_spec_whitelist").fetchone()
tot=wc.execute("SELECT count(*) FROM mr_candidate_1s_v2").fetchone()[0]
print(f"lowfader_spec_whitelist: {n[0]:,} tkd ({100*n[0]/tot:.1f}% of {tot:,}), {n[1]:,} tickers, {n[2]}..{n[3]}", flush=True)
