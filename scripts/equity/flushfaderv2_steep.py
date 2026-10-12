"""FlushFaderV2 — V2-5: the MATCHED-STEEPNESS control for the features after dslo (2026-10-12).

A feature that spreads the multipliers can raise sized PF−1 by concentration alone. The control: for a model M (base +
features), take the BASE model's multipliers m_b and raise them to the power p at which their spread (coefficient of
variation of the final, clipped, mean-1 weights on the apply set) EQUALS M's. Matched separately on each holdout's
apply set. A feature is credited only by what M beats base ^ p by.

Base = A3 + dslo ≥ 4 % (V2-3 step 1; its own gain over A3 was 2.6× A3 ^ 1.5's). Two tables:
  1. each remaining candidate ALONE on top of the base, vs base ^ p(matched)
  2. the V2-3 forward path (s20, speed, K ≥ 77, rflow, ...) cumulatively, vs base ^ p(matched)
Run from research/:
    python -u scripts/equity/flushfaderv2_steep.py > data/flushfaderv2/steep.log 2>&1; echo exit $?
    (any flushfaderv2_linear.py flag works here, e.g. --book data/flushfaderv2/v2_book.parquet --tag v2)
"""
import os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import flushfaderv2_linear as L

BASE = ["dslo>=4%"]
PATH = ["s20", "speed", "K>=77", "rflow", "haltband", "ssf", "dlv", "esf", "K", "eff20"]

def raw_mult(names, fit):
    X = L.design(names, fit); b = L.fit_model(X, fit); m = L.predict(X, b)[3]
    return np.maximum(m / m[fit].mean(), 0.0)
def final_w(m, app, p=1.0):
    w = np.clip(m ** p, 0.25, 4.0)[app]; return w / w.mean()
def cv(w): return w.std() / w.mean()
def matched(mb, app, target):
    """the power p at which base's final weights have spread `target` (bisection; cv is increasing in p)"""
    lo, hi = 0.0, 6.0
    for _ in range(60):
        mid = (lo + hi) / 2
        if cv(final_w(mb, app, mid)) < target: lo = mid
        else: hi = mid
    return (lo + hi) / 2
def pf1_w(w, app): return L.pf1(w * L.R[app])

HOLD = [("fwd", L.early, L.late), ("mirror", L.late, L.early)]
MB = {h: raw_mult(BASE, fit) for h, fit, _ in HOLD}

def compare(names):
    """per holdout: (M's sized PF−1, base^p matched sized PF−1, p, M's cv)"""
    res = {}
    for h, fit, app in HOLD:
        wm = final_w(raw_mult(names, fit), app); c = cv(wm)
        p = matched(MB[h], app, c); wb = final_w(MB[h], app, p)
        res[h] = (pf1_w(wm, app), pf1_w(wb, app), p, c)
    return res

f = L.f
b0 = {h: pf1_w(final_w(MB[h], app), app) for h, _, app in HOLD}
c0 = {h: cv(final_w(MB[h], app)) for h, _, app in HOLD}
out = [f"# V2-5 — matched-steepness control, base = A3 + dslo ≥ 4 % ({L.n:,} trades, book {L.args.book})\n",
       f"Base: fwd {f(b0['fwd'])} (cv {c0['fwd']:.3f}) · mirror {f(b0['mirror'])} (cv {c0['mirror']:.3f}) · mean {f((b0['fwd'] + b0['mirror']) / 2)}\n",
       "credit = M − base^p at M's spread (cv of the final mean-1 weights on the apply set); p > 1 = M is steeper than the base\n"]

hdr = ("| model | fwd M | fwd base^p | p | mirror M | mirror base^p | p | **credit fwd** | **credit mirror** | mean credit |\n"
       "|---|---|---|---|---|---|---|---|---|---|")
out += ["## 1. Each candidate ALONE on top of the base\n", hdr]
rows = []
for k in L.CAND:
    if k in BASE: continue
    r = compare(BASE + [k]); cf = r["fwd"][0] - r["fwd"][1]; cm = r["mirror"][0] - r["mirror"][1]
    rows.append((k, r, cf, cm))
rows.sort(key=lambda t: -(t[2] + t[3]))
for k, r, cf, cm in rows:
    out.append(f"| + {k} | {f(r['fwd'][0])} | {f(r['fwd'][1])} | {r['fwd'][2]:.2f} | {f(r['mirror'][0])} | {f(r['mirror'][1])} | {r['mirror'][2]:.2f} | "
               f"{cf:+.3f} | {cm:+.3f} | {(cf + cm) / 2:+.3f}{' ✓' if cf > 0 and cm > 0 else ''} |")
out.append(""); L.log("singles done")

out += ["## 2. The V2-3 forward path, cumulative\n", hdr]
sel = list(BASE)
for k in PATH:
    sel.append(k); r = compare(sel); cf = r["fwd"][0] - r["fwd"][1]; cm = r["mirror"][0] - r["mirror"][1]
    out.append(f"| base + {', '.join(sel[1:])} | {f(r['fwd'][0])} | {f(r['fwd'][1])} | {r['fwd'][2]:.2f} | {f(r['mirror'][0])} | {f(r['mirror'][1])} | {r['mirror'][2]:.2f} | "
               f"{cf:+.3f} | {cm:+.3f} | {(cf + cm) / 2:+.3f}{' ✓' if cf > 0 and cm > 0 else ''} |")
out.append("")

# the same control applied to dslo itself (base = A3), for the record
A3 = {h: raw_mult([], fit) for h, fit, _ in HOLD}
r = {}
for h, fit, app in HOLD:
    wm = final_w(MB[h], app); c = cv(wm); p = matched(A3[h], app, c)
    r[h] = (pf1_w(wm, app), pf1_w(final_w(A3[h], app, p), app), p)
out += ["## 3. dslo ≥ 4 % itself under the same control (base = A3)\n",
        f"fwd {f(r['fwd'][0])} vs A3^{r['fwd'][2]:.2f} {f(r['fwd'][1])} (credit {r['fwd'][0] - r['fwd'][1]:+.3f}) · "
        f"mirror {f(r['mirror'][0])} vs A3^{r['mirror'][2]:.2f} {f(r['mirror'][1])} (credit {r['mirror'][0] - r['mirror'][1]:+.3f})", ""]
txt = "\n".join(out); op = f"data/flushfaderv2/steep{'_' + L.args.tag if L.args.tag else ''}.md"; open(op, "w").write(txt); print(txt); L.log(f"wrote {op}")
