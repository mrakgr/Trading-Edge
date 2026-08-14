"""S43bz — is a POST-MARKET spike a good short? And is it better after an RTH rally?

⭐ USER QUESTION (2026-08-14). Answerable for the first time: the post-close session
did not exist in this dataset until the 2026-08-13 corpus rebuild.

Signal `pm{15,30,60}` = move off the 16:00 anchor to 16:15 / 16:30 / 17:00.
Entry = LIMIT resting in the 15 minutes AFTER the decision time (never overlapping
the signal). Exit = next session's open. Returns below are SHORT-SENSE (negated), so
positive = the short made money.

⚠⚠ THE CONTROL THAT MATTERS. A post-market spike is not interesting because it
predicts a fall — it is interesting only if it predicts a fall *beyond what the RTH
signal already told you, and beyond simply shorting at the close*. Every table
therefore carries `close_ref`: the same trades shorted at 16:00 instead. If the
post-market entry does not beat that, the extra session bought nothing.

⚠ POST-MARKET TAPE IS SPARSE. A "spike" measured across 4 prints is not a spike, and
a limit that cannot fill is not a trade. Density is a first-class column here, not a
footnote — §1 sizes the tradeable population before any expectancy is quoted.

Usage:  python scripts/equity/snoozer_postmarket.py
"""
import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

CACHE = "data/equity/flushfader/snoozer_pm_cache.parquet"
con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"CREATE OR REPLACE TEMP TABLE X AS SELECT *, year(date) AS yr "
            f"FROM read_parquet('{CACHE}')")
N = con.execute("SELECT count(*) FROM X").fetchone()[0]
print(f"population: {N:,} universe ticker-days with post-market coverage\n")

# (label, signal col, density col, window secs, fill col, return col, fill bars col)
LEGS = [("16:15", "pm15", "nb_pm15", 900, "px_f15", "ovn_f15", "nbf15"),
        ("16:30", "pm30", "nb_pm30", 1800, "px_f30", "ovn_f30", "nbf30"),
        ("17:00", "pm60", "nb_pm60", 3600, "px_f60", "ovn_f60", "nbf60")]

print("=" * 150)
print("§1 HOW MUCH POST-MARKET TAPE IS THERE? (before any expectancy is quoted)")
print("=" * 150)
rows = []
for lbl, sig, nb, secs, px, ret, nbf in LEGS:
    d = con.execute(f"""SELECT
        round(100.0*avg(CASE WHEN {nb} = 0 THEN 1.0 ELSE 0 END), 1) AS pct_no_signal_tape,
        round(median({nb})) AS med_secs, round(quantile_cont({nb}, 0.9)) AS p90_secs,
        round(100.0*avg(CASE WHEN {px} IS NULL THEN 1.0 ELSE 0 END), 1) AS pct_no_fill,
        count(*) FILTER ({sig} IS NOT NULL AND {ret} IS NOT NULL) AS tradeable
        FROM X""").fetchdf()
    d.insert(0, "decision", lbl)
    d.insert(1, "of secs", secs)
    rows.append(d)
print(pd.concat(rows).to_string(index=False))

SHORT = "-({})"
PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")


def table(title, extra_where, buckets, sig, nb, px, ret, min_bars):
    print(f"\n{title}")
    rows = []
    for blabel, bcond in buckets:
        w = (f"{sig} IS NOT NULL AND {ret} IS NOT NULL AND {nb} >= {min_bars} "
             f"AND {bcond}" + (f" AND {extra_where}" if extra_where else ""))
        d = con.execute(f"""SELECT count(*) n,
            median(-({ret}))*100 med_, avg(-({ret}))*100 mean_,
            avg(CASE WHEN -({ret})>0 THEN 1.0 ELSE 0 END)*100 win_,
            (SELECT {PF} FROM (SELECT -({ret}) AS r FROM X WHERE {w})) pf,
            min(-({ret}))*100 worst_,
            median(-(ovn_from_close))*100 cref_
            FROM X WHERE {w}""").fetchdf()
        n = int(d.n[0])
        rows.append({
            "post-mkt move": blabel, "n": f"{n:,}",
            "med%": "." if n < 30 else f"{d.med_[0]:+.2f}",
            "mean%": "." if n < 30 else f"{d.mean_[0]:+.2f}",
            "win%": "." if n < 30 else f"{d.win_[0]:.0f}",
            "PF": "." if n < 30 else (f"{d.pf[0]:.3f}" if pd.notna(d.pf[0]) else "inf"),
            "worst%": "." if n < 30 else f"{d.worst_[0]:.0f}",
            "⚠ close_ref med%": "." if n < 30 else f"{d.cref_[0]:+.2f}"})
    print(pd.DataFrame(rows).to_string(index=False))


UP = [("[+1,+2)", "{s} >= 0.01 AND {s} < 0.02"), ("[+2,+4)", "{s} >= 0.02 AND {s} < 0.04"),
      ("[+4,+6)", "{s} >= 0.04 AND {s} < 0.06"), ("[+6,+10)", "{s} >= 0.06 AND {s} < 0.10"),
      ("[+10,inf)", "{s} >= 0.10")]
DOWN = [("(-inf,-6]", "{s} <= -0.06"), ("(-6,-2]", "{s} > -0.06 AND {s} <= -0.02")]

for MIN_BARS, tag in ((60, "tape >= 60s in the signal window"),
                      (300, "tape >= 300s — a REAL post-market tape")):
    print(f"\n{'='*150}\n§2 SHORT THE POST-MARKET SPIKE — {tag}\n"
          f"short-sense returns; `close_ref` = the SAME trades shorted at 16:00 instead\n{'='*150}")
    for lbl, sig, nb, secs, px, ret, nbf in LEGS:
        table(f"--- decide {lbl}, limit-fill the next 15m ---", None,
              [(b, c.format(s=sig)) for b, c in UP], sig, nb, px, ret, MIN_BARS)

print(f"\n{'='*150}\n⭐ §3 CONDITIONED ON THE RTH LAST HOUR — the user's actual question\n"
      f"'especially if they went up significantly during the last hour of RTH'\n{'='*150}")
for rth_lbl, rth_cond in [("RTH last hour > +6%", "chg60k59 > 0.06"),
                          ("RTH last hour > +2%", "chg60k59 > 0.02"),
                          ("RTH last hour flat/down (<= +2%)", "chg60k59 <= 0.02")]:
    for lbl, sig, nb, secs, px, ret, nbf in LEGS:
        if lbl != "17:00":
            continue
        table(f"--- {rth_lbl}  x  post-market to 17:00, tape>=60s ---",
              rth_cond, [(b, c.format(s=sig)) for b, c in UP], sig, nb, px, ret, 60)

print(f"\n{'='*150}\n§4 THE MIRROR — does a post-market DROP keep dropping? (short-sense)\n{'='*150}")
for lbl, sig, nb, secs, px, ret, nbf in LEGS:
    if lbl != "17:00":
        continue
    table("--- post-market decline to 17:00, tape>=60s ---", None,
          [(b, c.format(s=sig)) for b, c in DOWN], sig, nb, px, ret, 60)
