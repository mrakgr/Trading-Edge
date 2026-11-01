"""GapFader GF-57 (user 2026-11-01): the gap-up long swing ENTERED ON A DIP — the first minute after 10:00 whose signed
EFFICIENCY RATIO over the last W minutes is below a threshold.

The user: "stocks gapping up on high rvol during the first 30m do well if they hold up the open ... would [they] be better
entries when the efficiency ratio is < 0? What about < -0.3 or < -0.6? ... use the 20m and 30m windows."
THE BOOK: GF-42's (gapfader_rvol30.py): gap-up >= 4 % · open >= $1 · dv20_prior >= $1M · no early close · held the open
at 10:00 (the last 1s vwap before 10:00 >= the official open) · rvol30 (09:30–10:00 volume / avgvol20_prior) >= 1 ·
calm (volat_clk < 40 bp, GF-11). The non-calm tape is shown beside it (GF-40: it loses at 10:00).
THE SIGNAL: P = the 1-minute vwap (forward-filled; the official open stands before 09:30). ER_W(m) = (P[m] − P[m−W]) /
Σ |P[k] − P[k−1]| over the W minutes to m (−1 = a straight decline; a flat path has no ER). Evaluated at the end of
every minute from 09:59 (decision at 10:00) to 15:29; the FIRST minute with ER_W < threshold triggers; the entry is the
first 1s vwap at or after the next minute's start (as GF-38's entry_30). Out at the official close, net 0.7c / share.
CONTROLS, on the SAME names: (a) bought at 10:00 (entry_30); (b) bought at a RANDOM minute drawn from the triggered
entries' own time distribution (a per-row seed of its ticker, date and the cell), so a later entry is not mistaken for
the ER's work. ⭐ Pipeline control: the cache's 10:00 entry must equal swing_allpm.parquet's entry_30.
Cache: data/equity/gapfader/er_minutes.parquet (the candidates' minute bars, 09:30–16:00).

  cd research && python -u scripts/equity/gapfader_er_entry.py > data/equity/gapfader/er_entry.md 2> logs/gapfader_er_entry.log; echo exit $?
"""
import hashlib, os, sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & (C.rvol_30m >= 1)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
log(f"{len(C):,} held gap-ups with rvol30 >= 1 ({(C.volat_clk < 40).sum():,} calm)")

# ---------------------------------------------------------------- the minute bars (built once)
CACHE = "data/equity/gapfader/er_minutes.parquet"
if os.path.exists(CACHE):
    M = pd.read_parquet(CACHE)
else:
    con = duckdb.connect()
    con.execute("set memory_limit='1GB'; set threads=3; set enable_progress_bar=false")
    parts = []
    for i, (d, g) in enumerate(C.groupby("date")):
        con.register("tk", g[["ticker"]])
        X = con.execute(f"""
            SELECT ticker, (bucket - 34200) // 60 AS m, sum(vwap::DOUBLE * volume) / sum(volume) AS pv,
                   arg_min(vwap::DOUBLE, bucket) AS first_px
            FROM read_parquet('data/intraday_1s_slim/{d}.parquet')
            WHERE ticker IN (SELECT ticker FROM tk) AND bucket >= 34200 AND bucket < 57600 AND volume > 0
            GROUP BY ticker, m""").df()
        con.unregister("tk"); X["date"] = d; parts.append(X)
        if i % 300 == 0: log(f"  {i + 1} {d}")
    M = pd.concat(parts, ignore_index=True); M["m"] = M.m.astype(np.int16)
    M.to_parquet(CACHE + ".tmp"); os.replace(CACHE + ".tmp", CACHE)
    log(f"wrote {CACHE}: {len(M):,} minute bars")

# ---------------------------------------------------------------- per candidate: P (390 minutes) and the entry price per minute
NM = 390
key = {(t, d): i for i, (t, d) in enumerate(zip(C.ticker, C.date))}
M = M[[(t, d) in key for t, d in zip(M.ticker, M.date)]]
row = np.array([key[(t, d)] for t, d in zip(M.ticker, M.date)])
P = np.full((len(C), NM), np.nan); E = np.full((len(C), NM), np.nan)
P[row, M.m.values] = M.pv.values; E[row, M.m.values] = M.first_px.values
P = pd.DataFrame(P).ffill(axis=1).values
P = np.where(np.isnan(P), C.open.values[:, None], P)
P = np.hstack([C.open.values[:, None], P])                    # P[:, 0] = the open; minute m is column m + 1
E = pd.DataFrame(E).bfill(axis=1).values                     # the first 1s vwap at or after each minute's start
err = np.nanmax(np.abs(E[:, 30] / C.entry_30.values - 1))
assert err < 1e-9, f"the 10:00 entry does not reproduce entry_30: {err}"
log(f"pipeline control: the 10:00 entry == entry_30 (max rel err {err:.1e})")
CL = C.close.values
def ret(px, sel): return CL[sel] / px[sel] - 1 - 0.007 / px[sel]      # px full-length, sel a mask

def er_first(W, thr):
    """the first signal minute m in [29, 359] with ER_W(m) < thr; -1 if none"""
    dP = np.abs(np.diff(P, axis=1))                          # dP[:, k] = |P[k+1] − P[k]|, i.e. minute k's move
    cs = np.hstack([np.zeros((len(C), 1)), np.cumsum(dP, axis=1)])
    m = np.arange(29, 360)
    hi, lo = m + 1, np.maximum(m + 1 - W, 0)                 # P columns
    net = P[:, hi] - P[:, lo]; path = cs[:, hi] - cs[:, lo]
    with np.errstate(invalid="ignore", divide="ignore"):
        er = np.where(path > 0, net / path, np.nan)
    hit = er < thr
    any_ = hit.any(axis=1)
    return np.where(any_, m[np.argmax(hit, axis=1)], -1)

def pf(x):
    x = np.asarray(x); x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
YR = C.date.str[:4].values; EARLY = YR <= "2020"; NY = len(set(YR))
CALM = (C.volat_clk < 40).values
def seed(t, d, cell): return int.from_bytes(hashlib.sha256(f"{t}|{d}|{cell}".encode()).digest()[:8], "little")

H = ("| entry | n | % of book | median entry | entry vs 10:00 (median) | mean % | win % | net PF | net PF clip +50 % | "
     "net 2016–20 / 2021–26 | years < 1 | same names @10:00 | same names @ random time |\n|" + "---|" * 13)
def line(nm, sel, px, ctl10=None, ctlr=None, mins=None):
    r = ret(px, sel); y = YR[sel]
    yrs = pd.Series(r).groupby(y).apply(pf)
    s = (f"| {nm} | {sel.sum():,} | {100 * sel.sum() / BOOK.sum():.0f} | "
         + (f"{9 + (30 + int(np.median(mins))) // 60}:{(30 + int(np.median(mins))) % 60:02d} | " if mins is not None else "10:00 | ")
         + (f"{1e4 * np.median(px[sel] / C.entry_30.values[sel] - 1):+.0f} bp | " if mins is not None else "— | ")
         + f"{100 * r.mean():+.2f} | {100 * (r > 0).mean():.0f} | **{pf(r):.2f}** | {pf(np.minimum(r, 0.5)):.2f} | "
         f"{pf(r[EARLY[sel]]):.2f} / {pf(r[~EARLY[sel]]):.2f} | {int((yrs < 1).sum())}/{len(yrs)} | ")
    s += (f"{pf(ret(*ctl10)):.2f} | " if ctl10 is not None else "— | ") + (f"{pf(ret(*ctlr)):.2f} |" if ctlr is not None else "— |")
    return s

print("# GF-57 — the gap-up long swing entered on a dip: the efficiency ratio after 10:00\n")
print(f"Book: GF-42's (held the open at 10:00 · rvol30 ≥ 1; calm = volat_clk < 40 bp), {len(C):,} names "
      f"({CALM.sum():,} calm), {C.date.min()}..{C.date.max()}. Out at the close, net 0.7c / share. The controls are the "
      f"SAME names bought at 10:00, and at a random minute from the triggered entries' own times.\n")
for tape, BOOK in [("CALM (the base book)", CALM), ("NOT calm", ~CALM)]:
    print(f"## {tape}\n"); print(H)
    print(line("bought at 10:00 (GF-42)", BOOK, C.entry_30.values))
    for W in (20, 30):
        for thr in (0.0, -0.3, -0.6):
            mf = er_first(W, thr); trig = BOOK & (mf >= 0)
            px = np.full(len(C), np.nan); px[trig] = E[trig, mf[trig] + 1]
            ok = trig & np.isfinite(px)
            # the random-time control: per row, a minute drawn from this cell's triggered minutes
            pool = mf[ok]; cr = np.full(len(C), np.nan)
            for i in np.flatnonzero(ok):
                g = np.random.default_rng(seed(C.ticker[i], C.date[i], f"{tape}|{W}|{thr}"))
                cr[i] = E[i, int(pool[g.integers(len(pool))]) + 1]
            okr = ok & np.isfinite(cr)
            held = ok & (px >= C.open.values)
            print(line(f"ER{W} < {thr:g}", ok, px, (C.entry_30.values, ok), (cr, okr), mf[ok]))
            print(line(f"ER{W} < {thr:g} · still above the open", held, px, (C.entry_30.values, held), None, mf[held]))
            un = BOOK & ~(mf >= 0)
            if thr == -0.6:
                print(line(f"ER{W} never < {thr:g}: those names @10:00", un, C.entry_30.values))
    print()
log("done")
