"""GapFader — step 1: the GAP CENSUS (user 2026-10-12: "do large gaps on weak volume revert to the mean during the day,
and how often?"). Descriptive: one row per CS/ADRC ticker-day whose open gaps >= MIN_GAP either way, with features
knowable at entry and the outcomes. Nothing here is a rule yet.

Daily part (data/v2/daily_adjusted — the CAUSAL scheme, docs/price_adjustment.md):
  close_m1   = close(D−1) × n(D−1)/n(D)                                         the previous close in D's raw share terms
  div_m1     = (cum_div(D−1) − cum_div(D)) / n(D)                               the dividend going ex on D, same terms (<= 0)
  gap        = (open(D) − div_m1) / close_m1 − 1                                ⭐ THE HOLDER'S RETURN close(D−1) -> open(D)
                                                                                (user 2026-10-31; the Oms's changeFromPrevClose;
                                                                                until then open / pc_total − 1)
  pc_total   = close_m1 + div_m1                                                the price on D whose holder's return is 0 (the
                                                                                target's other end; fill_sec below)
  avgvol20_prior, dv20_prior                                                    the 20 PRIOR sessions only (D excluded), inside
                                                                                D's EPISODE (a > 45-day gap breaks it: GF-46)
  the previous session must be at most 4 calendar days back (else the row is dropped: a stale prev close)
Intraday part (data/intraday_1s_slim, raw 1s vwap bars 04:00–20:00, the whole market):
  pm_vol / pm_dv          04:00 – 09:29:59                          knowable at 09:30
  vol_5m / 15m / 30m      09:30:00 – +5 / +15 / +30 min (the open cross in the 09:30:00 bar)   knowable then
  open_1s                 the 09:30:00-or-later first bar's vwap
  rth_hi / rth_lo         max / min of the RTH bar vwaps (09:30 – 16:00; 13:00 on early closes)
  close_1s                the last RTH bar's vwap
  fill_sec                the first RTH second whose vwap reaches pc_total (gap up: <=, gap down: >=), NaN = never
  hi_before_fill / lo_…   the adverse excursion before the fill (gap up: the max vwap before it — the pain of a fade)
plus close(D) (official) and open(D+1) (in D's terms: open(D+1) × n(D+1)/n(D) + dividend cash) for the overnight leg.

Universe: ticker_reference type CS / ADRC minus the Oms's mislabel exclusions (warrants, rights, SPAC units, preferred
issues, the CEF list — TradingEdge.Oms/Universe.fs, mirrored below), raw open >= $0.50 (bands later), dv20_prior >= $250k.

Run from research/:
    python -u scripts/equity/gapfader_census.py > logs/gapfader_census.log 2>&1; echo exit $?
"""
import argparse, os, re, time
import numpy as np, pandas as pd, duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--min-gap", type=float, default=0.02)
ap.add_argument("--from", dest="from_", default="2016-08-08")
ap.add_argument("--to", default="2026-09-04")
ap.add_argument("--min-dv20", type=float, default=250_000.0)
ap.add_argument("--min-open", type=float, default=0.5)
ap.add_argument("--out-dir", default="data/equity/gapfader")
ap.add_argument("--limit-days", type=int, default=0)
args = ap.parse_args()
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, flush=True)
os.makedirs(args.out_dir, exist_ok=True)
con = duckdb.connect(); con.execute("SET memory_limit='4GB'"); con.execute("SET threads=6")

# ---------------------------------------------------------------- the universe (Universe.fs mirrored)
CEF = {"CCD", "CGO", "CHI", "CHW", "CHY", "CPZ", "CSQ", "HERZ", "SDHY", "VCX", "RVII", "BPRE", "CFND"}
RX = {k: re.compile(p, re.I) for k, p in {
    "warrant": r"\bwarrants?\b|\(adw\)|\bwts?\b", "rights": r"\brights\b", "units": r"\bunits?\b|\buts\b",
    "equity_units": r"common units|\bcom (uts|units?)\b|limited partner|ltd partner|partnership|\bpartners\b|\bl\.? ?p\.? (uts|units?)\b|units of bene|\btr (uts|units?)\b|\btrust\b.*\bunits?\b|paired share|\bads\b|american depositary|ltd liability|^unit corp",
    "preferred": r"\bpreferred (stock|shares?|units?|securities)\b|\bcumulative\b|\bperpetual\b|depositary shares.*\bpreferred\b",
    "adr": r"american depositary"}.items()}
def excluded(t: str, name: str):
    name = name or ""
    if t in CEF: return "closed-end fund"
    if t.endswith(".WS") or RX["warrant"].search(name): return "warrant"
    if t.endswith(".RT") or RX["rights"].search(name): return "rights"
    if RX["preferred"].search(name) and not RX["adr"].search(name): return "preferred"
    if t.endswith(".U") or (RX["units"].search(name) and not RX["equity_units"].search(name)): return "units"
    return None
ref = con.execute("SELECT ticker, any_value(coalesce(name,'')) AS name FROM 'data/v2/ticker_reference.parquet' WHERE type IN ('CS','ADRC') GROUP BY ticker").df()
ref["why"] = [excluded(t, n) for t, n in zip(ref.ticker, ref.name)]
UNI = ref[ref.why.isna()].ticker
log(f"universe: {len(ref):,} CS/ADRC tickers, {ref.why.notna().sum():,} excluded, {len(UNI):,} kept")
con.register("uni", pd.DataFrame({"ticker": UNI}))

# ---------------------------------------------------------------- 1. the daily gaps
G = con.execute(f"""
    -- ⭐ GF-46 (2026-10-21): the 20-session averages run inside an EPISODE (a > 45-calendar-day gap between a ticker's
    -- rows starts a new one — scripts/equity/build_daily_episodes_causal_view.sql, the Oms's barnums / volume20s), so a
    -- recycled ticker's window cannot reach into an earlier listing (ARIA 2026-08 had 17 of ARIAD's 2017 rows)
    WITH e0 AS (SELECT a.*, CASE WHEN a.date - lag(a.date) OVER (PARTITION BY a.ticker ORDER BY a.date) > 45 THEN 1 ELSE 0 END AS brk
                FROM 'data/v2/daily_adjusted.parquet' a JOIN uni USING (ticker)),
    ep AS (SELECT *, sum(brk) OVER (PARTITION BY ticker ORDER BY date) AS episode FROM e0),
    d AS (
      SELECT a.ticker, a.date, a.open, a.high, a.low, a.close, a.volume, a.n, a.cum_div,
             lag(a.date) OVER w AS prev_date, lag(a.close) OVER w AS prev_close_raw, lag(a.n) OVER w AS n_prev,
             lag(a.cum_div) OVER w AS cd_prev,
             lead(a.open) OVER w AS open_next_raw, lead(a.n) OVER w AS n_next, lead(a.cum_div) OVER w AS cd_next,
             lead(a.date) OVER w AS next_date,
             avg(a.volume / a.n) OVER (we ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) * a.n AS avgvol20_prior,
             avg(a.volume * a.close) OVER (we ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS dv20_prior,
             count(*) OVER (we ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS n_prior
      FROM ep a
      WINDOW w AS (PARTITION BY a.ticker ORDER BY a.date), we AS (PARTITION BY a.ticker, a.episode ORDER BY a.date))
    SELECT *, prev_close_raw * n_prev / n - (cum_div - cd_prev) / n AS pc_total,
           (open_next_raw * n_next / n) + (cd_next - cum_div) / n AS open_next_total
    FROM d
    WHERE date BETWEEN '{args.from_}' AND '{args.to}' AND prev_date IS NOT NULL AND date - prev_date <= 4
      AND n_prior = 20 AND dv20_prior >= {args.min_dv20} AND open >= {args.min_open}""").df()
G["close_m1"] = G.prev_close_raw * G.n_prev / G.n
G["div_m1"] = (G.cd_prev - G.cum_div) / G.n
G["gap"] = (G.open - G.div_m1) / G.close_m1 - 1.0           # the holder's return (user 2026-10-31)
G = G[np.isfinite(G.gap) & (G.gap.abs() >= args.min_gap)].copy()
G["date"] = G.date.astype(str)
log(f"daily gaps |gap| >= {args.min_gap:.0%}: {len(G):,} ticker-days, {G.date.nunique():,} days (up {int((G.gap > 0).sum()):,}, down {int((G.gap < 0).sum()):,})")

# ---------------------------------------------------------------- 2. the intraday features and paths, per day file
EARLY = {"2016-11-25", "2017-07-03", "2017-11-24", "2018-07-03", "2018-11-23", "2018-12-24", "2019-07-03", "2019-11-29", "2019-12-24",
         "2020-11-27", "2020-12-24", "2021-11-26", "2022-11-25", "2023-07-03", "2023-11-24", "2024-07-03", "2024-11-29", "2024-12-24",
         "2025-07-03", "2025-11-28", "2025-12-24", "2026-11-27", "2026-12-24"}
days = sorted(G.date.unique())
if args.limit_days: days = days[:args.limit_days]
parts = []
for k, d in enumerate(days):
    path = f"data/intraday_1s_slim/{d}.parquet"
    if not os.path.exists(path): continue
    gd = G[G.date == d][["ticker", "pc_total", "gap"]]
    con.register("gd", gd)
    close_sec = 46800 if d in EARLY else 57600
    X = con.execute(f"""
        WITH b AS (SELECT s.ticker, s.bucket, s.vwap::DOUBLE AS v, s.volume::DOUBLE AS vol, gd.pc_total, gd.gap
                   FROM read_parquet('{path}') s JOIN gd ON gd.ticker = s.ticker),
        r AS (SELECT * FROM b WHERE bucket >= 34200 AND bucket < {close_sec}),
        f AS (SELECT ticker, min(bucket) AS fill_sec FROM r
              WHERE (gap > 0 AND v <= pc_total) OR (gap < 0 AND v >= pc_total) GROUP BY ticker)
        SELECT b.ticker,
               sum(vol) FILTER (bucket < 34200) AS pm_vol, sum(v * vol) FILTER (bucket < 34200) AS pm_dv,
               sum(vol) FILTER (bucket >= 34200 AND bucket < 34500) AS vol_5m,
               sum(vol) FILTER (bucket >= 34200 AND bucket < 35100) AS vol_15m,
               sum(vol) FILTER (bucket >= 34200 AND bucket < 36000) AS vol_30m,
               sum(vol) FILTER (bucket >= 34200 AND bucket < {close_sec}) AS rth_vol,
               arg_min(v, bucket) FILTER (bucket >= 34200) AS open_1s,
               max(v) FILTER (bucket >= 34200 AND bucket < {close_sec}) AS rth_hi,
               min(v) FILTER (bucket >= 34200 AND bucket < {close_sec}) AS rth_lo,
               arg_max(v, bucket) FILTER (bucket >= 34200 AND bucket < {close_sec}) AS close_1s,
               any_value(f.fill_sec) AS fill_sec,
               max(v) FILTER (bucket >= 34200 AND bucket < coalesce(f.fill_sec, {close_sec})) AS hi_before_fill,
               min(v) FILTER (bucket >= 34200 AND bucket < coalesce(f.fill_sec, {close_sec})) AS lo_before_fill
        FROM b LEFT JOIN f USING (ticker) GROUP BY b.ticker""").df()
    X["date"] = d
    parts.append(X)
    con.unregister("gd")
    if k % 100 == 0: log(f"{k+1}/{len(days)} {d}: {len(gd)} gaps")
I = pd.concat(parts, ignore_index=True)
C = G.merge(I, on=["ticker", "date"], how="inner")
C["early_close"] = C.date.isin(EARLY)
C["rvol_pm"] = C.pm_vol / C.avgvol20_prior
for m in (5, 15, 30): C[f"rvol_{m}m"] = C[f"vol_{m}m"] / C.avgvol20_prior
side = np.sign(C.gap)
C["fade_oc"] = -side * (C.close / C.open - 1.0)                          # open -> official close, the fade's sign
C["fade_on"] = -side * (C.open_next_total / C.open - 1.0)                # open -> next open
C["retraced"] = (C.open - C.close) / (C.open - C.pc_total)               # share of the gap closed by the close (1 = filled)
C["filled"] = C.fill_sec.notna()
C["adverse"] = np.where(side > 0, C.hi_before_fill / C.open - 1.0, 1.0 - C.lo_before_fill / C.open)
out = os.path.join(args.out_dir, "census.parquet"); C.to_parquet(out, index=False)
log(f"census {len(C):,} rows (of {len(G):,} daily gaps; no 1s file / no bars dropped) -> {out}")
