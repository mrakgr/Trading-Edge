"""GapFader GF-63 (user 2026-11-01): the gap-up swing's OVERNIGHT LOSER REVERSAL (GF-62) — is it the Snoozers' pattern,
and is it on the non-calm tape too?

The user: "Maybe it is a different pattern. Snoozers only look at declines in the last hour. Besides looking into that,
I am curious if the loser reversal effect is also present on stocks whose tape isn't calm during the first 30m."
The books: GF-57's held gap-ups with rvol30 >= 1 (gapfader_er_entry.py), CALM (volat_clk < 40 bp, 2,705) and NOT calm
(5,633); bought at 10:00 (entry_30). The day's trade = net at D's close (0.7c / share); a LOSER is <= 0. The overnight =
the holder's return close(D) → open(D+1) on daily_adjusted (raw P, causal n and cum_div).
The split: the LAST HOUR = the close / the 15:00 minute's vwap − 1 (er_minutes.parquet, minute 330), the Snoozers'
window; the REST OF THE DAY = the 15:00 vwap / the 10:00 entry − 1. A loser that fell in the last hour is the Snoozers'
shape; one that lost by 15:00 and was flat or up into the close is not.

  cd research && python -u scripts/equity/gapfader_overnight_reversal.py > data/equity/gapfader/overnight_reversal.md 2> logs/gapfader_overnight_reversal.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd, duckdb
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:7.1f}s]", *a, file=sys.stderr, flush=True)
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.gap >= 0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/swing_allpm.parquet"), on=["ticker", "date"])
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & (C.act_a > 0) & (C.rvol_30m >= 1)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True); C["i"] = np.arange(len(C)); N = len(C)

M = pd.read_parquet("data/equity/gapfader/er_minutes.parquet")
M = M[M.m <= 330].sort_values("m").groupby(["ticker", "date"]).last().reset_index()     # the last minute bar <= 15:00
C = C.merge(M[["ticker", "date", "pv"]].rename(columns={"pv": "p15"}), on=["ticker", "date"], how="left")
assert C.p15.notna().all()

con = duckdb.connect(); con.execute("set memory_limit='2GB'; set threads=4; set enable_progress_bar=false")
bk = C[["i", "ticker", "date"]].copy(); bk["date"] = pd.to_datetime(bk.date); con.register("bk", bk)
D = con.execute("""
    SELECT bk.i, row_number() OVER (PARTITION BY bk.i ORDER BY a.date) - 1 AS k, a.open, a.close, a.n, a.cum_div
    FROM bk JOIN 'data/v2/daily_adjusted.parquet' a ON a.ticker = bk.ticker AND a.date >= bk.date
         AND a.date <= bk.date + INTERVAL 20 DAY
    ORDER BY bk.i, a.date""").df()
d0 = D[D.k == 0].set_index("i"); d1 = D[D.k == 1].set_index("i")
assert np.max(np.abs(d0.close.reindex(C.i).values / C.close.values - 1)) < 1e-9
has1 = C.i.isin(d1.index).values
c0, n0, k0 = (d0[x].reindex(C.i).values for x in ("close", "n", "cum_div"))
o1, n1, k1 = (d1[x].reindex(C.i).values for x in ("open", "n", "cum_div"))
OVN = ((o1 * n1 - c0 * n0) + (k1 - k0)) / (c0 * n0)                    # the holder's return close(D) → open(D+1)
DAY = C.close.values / C.entry_30.values - 1 - 0.007 / C.entry_30.values
H1 = C.close.values / C.p15.values - 1                                  # the last hour
REST = C.p15.values / C.entry_30.values - 1                             # 10:00 → 15:00
CALM = (C.volat_clk < 40).values
log(f"{N:,} names; {int((~has1).sum())} without a next day (left out)")

def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
EARLY = C.date.str[:4].values <= "2020"
def cell(sel):
    s = sel & has1; r = OVN[s]
    if len(r) < 20: return f"{len(r)} | | | | "
    return (f"{len(r):,} | {100 * r.mean():+.2f} | {100 * np.median(r):+.2f} | **{pf(r):.2f}** ({100 * (r > 0).mean():.0f} %) | "
            f"{pf(OVN[s & EARLY]):.2f} / {pf(OVN[s & ~EARLY]):.2f} |")

print("# GF-63 — the overnight loser reversal: the Snoozers' last hour, and the non-calm tape\n")
print(f"Held gap-ups with rvol30 ≥ 1 bought at 10:00 ({N:,}: {CALM.sum():,} calm, {(~CALM).sum():,} not). The overnight = "
      "the holder's return close(D) → open(D+1); its mean %, median %, PF (win %).\n")
for tape, T in (("CALM", CALM), ("NOT calm", ~CALM)):
    W, L = T & (DAY > 0), T & (DAY <= 0)
    print(f"## {tape}\n")
    print("| group | n | overnight mean % | median % | PF (win %) | 2016–20 / 2021–26 |\n|---|---|---|---|---|---|")
    print(f"| all | {cell(T)}"); print(f"| winners at the close | {cell(W)}"); print(f"| **losers** at the close | {cell(L)}")
    print("| *losers by the LAST HOUR (close / 15:00 − 1)* | | | | | |")
    for a, b, nm in ((-9, -0.02, "fell ≥ 2 %"), (-0.02, -0.005, "fell 0.5–2 %"), (-0.005, 0.005, "flat (±0.5 %)"), (0.005, 9, "rose > 0.5 %")):
        print(f"| losers · last hour {nm} | {cell(L & (H1 > a) & (H1 <= b))}")
    print("| *winners by the LAST HOUR* | | | | | |")
    for a, b, nm in ((-9, -0.02, "fell ≥ 2 %"), (-0.02, -0.005, "fell 0.5–2 %"), (-0.005, 0.005, "flat (±0.5 %)"), (0.005, 9, "rose > 0.5 %")):
        print(f"| winners · last hour {nm} | {cell(W & (H1 > a) & (H1 <= b))}")
    print("| *losers by the day's loss at the close* | | | | | |")
    for a, b, nm in ((-9, -0.05, "≤ −5 %"), (-0.05, -0.02, "−5 … −2 %"), (-0.02, 0.0, "−2 … 0 %")):
        print(f"| losers · net {nm} | {cell(L & (DAY > a) & (DAY <= b))}")
    print("| *losers who were already down at 15:00 AND did not fall in the last hour (not the Snoozers' shape)* | | | | | |")
    print(f"| losers · down by 15:00 · last hour ≥ −0.5 % | {cell(L & (REST < 0) & (H1 > -0.005))}")
    print(f"| losers · last hour < −0.5 % (the Snoozers' shape) | {cell(L & (H1 <= -0.005))}")
    print()
# ---------------------------------------------------------------- the hold past the open: close(D) → D+k (user 2026-11-01)
K = 6
CLk = np.full((N, K), np.nan); Nk = np.full((N, K), np.nan); Dk = np.full((N, K), np.nan)
dd = D[D.k < K]; CLk[dd.i, dd.k] = dd.close; Nk[dd.i, dd.k] = dd.n; Dk[dd.i, dd.k] = dd.cum_div
for kk in range(1, K):                                       # a missing later row: the last close (a delisting)
    for A in (CLk, Nk, Dk):
        A[:, kk] = np.where(np.isnan(A[:, kk]), A[:, kk - 1], A[:, kk])
HOLD = {"D+1 open": OVN}
for kk in (1, 2, 3, 5):
    HOLD[f"D+{kk} close"] = ((CLk[:, kk] * Nk[:, kk] - c0 * n0) + (Dk[:, kk] - k0)) / (c0 * n0)
def hc(sel, r):
    x = r[sel & has1]
    return f"{100 * x.mean():+.2f} · **{pf(x):.2f}**" if len(x) >= 20 else "·"
print("## The hold from D's close: mean % · PF to each exit\n")
for tape, T in (("CALM", CALM), ("NOT calm", ~CALM)):
    W, L = T & (DAY > 0), T & (DAY <= 0)
    G = [("winners at the close", W), ("winners · last hour rose > 0.5 %", W & (H1 > 0.005)), ("losers at the close", L),
         ("losers · last hour < −0.5 % (the Snoozers' shape)", L & (H1 <= -0.005)),
         ("losers · down by 15:00, last hour ≥ −0.5 % (the day-loser)", L & (REST < 0) & (H1 > -0.005)),
         ("losers · net ≤ −5 %", L & (DAY <= -0.05)), ("losers · net −5 … 0 %", L & (DAY > -0.05))]
    print(f"### {tape}\n")
    print("| group | n | " + " | ".join(HOLD) + " | 2016–20 / 2021–26 at D+3 |\n|---|---|" + "---|" * (len(HOLD) + 1))
    for nm, g in G:
        r3 = HOLD["D+3 close"]; s3 = g & has1
        print(f"| {nm} | {(g & has1).sum():,} | " + " | ".join(hc(g, r) for r in HOLD.values())
              + f" | {pf(r3[s3 & EARLY]):.2f} / {pf(r3[s3 & ~EARLY]):.2f} |")
    print()
log("done")
