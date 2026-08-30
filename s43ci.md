## 🔒 S43ci (2026-08-30) — ROSTER v3.2: vcrush ADOPTED, ramp RENAMED vexp

**The quadrant decomposition (user question — "is ramp firing on both-negative
slopes?")**: NO. Inside the 606-trip bucket: s10 > 0 ∧ s20 < 0 (the intended "fresh
expansion against the 20m trend") = 61.1% @ PF 9.74 · both > 0 (accelerating
expansion) = 38.0% @ 5.82 · both < 0 = 1.0% (6 trips). Median s10 = +13.5, s5 =
+33.7. The voice blends two tiers (noted, kept as-is per user).

**⭐ ramp ∩ s5neg = ZERO TRIPS** — the 5m-slope contraction tail (`volat_slope_5m ×
2e4 ≤ −24`) is the OPPOSITE PHASE of the same volatility wave (88.6% of ramp trips
have s5 > 0): ramp = the explosion building, s5neg = the explosion breaking. Both
bracket the climax; marginal sets add exactly. Individual-slope audit (16 tails,
count-matched 606): s5neg is the ONLY PF-accretive expansion-slot voice ever measured
(+0.018 vs the no-voice base; ramp −0.030; s10neg +0.007; every expansion tail and
every EWMA single worse, to −0.20). ⚠ Its marginals (33 @ 5.17) carry the fast-family
2023-24 soft years (0.45/0.94).

**🔒 ADOPTED (user)**: `vcrush = volat_slope_5m × 2e4 ≤ −24` joins as the 8th voice
(vote ≥ 1, so seat-vs-merged is book-identical). `ramp` KEPT and RENAMED **`vexp`**
with the sign FLIPPED for readability: `vexp = (volat_slope_10m − volat_slope_20m) ×
2e4 > 12` (strict — set-identical to the old `< −12`; zero ties measured). U2/ed105/
r35/crest all REJECTED for the seat (S43ch); crest stays a SIZING-tier candidate.

**ROSTER v3.2** = {v20 ≥ 140 · d20a < −28% · dslo ≥ +8% · **vexp > 12** · legage ≤
450s · dsu ≥ 8 · haltband ssh ∈ [20,80m) · **vcrush ≤ −24**} + S-tier, vote ≥ 1.

| book (v47 ref frame, per-tkd mc=1) | n | pf | net | years |
|---|---|---|---|---|
| v3.0 | 1,351 | 4.001 | 2,620% | 8.43 3.40 3.21 3.49 3.67 3.50 4.01 |
| **v3.2** | **1,378** | **4.011** | **2,670%** | 8.64 3.49 3.21 3.27 3.58 3.53 4.13 |

**Scripts updated** (flushfader_{book,breakdown,kelly,voice_test,tier_inten,
shape_test,inten_sizing,broker_volume}.py): vexp flipped-sign expr, vcrush added,
`--trips` defaults → `v47_spec20`, and ⚠ the reference-frame guard `volat_20m ≥
0.004 ∧ signal_sec ≤ 54000` added to every book WHERE — the old scripts relied on
the corpus being 40bp/15:00-bounded, which v47 (20bp/16:00) is NOT; without the
guard the book widens silently. **Validated end-to-end**: flushfader_book.py on v47
reproduces 1,378 @ 4.011 / win 77.7% / avg +1.94% exactly. ⚠ Fresh ms-era tier
multipliers read A 1.75 / B 1.77 / C 1.18 (locked: 2.44/1.80/1.14; B ≈ A now) —
re-derive at the next sizing pass. Historical sections keep the name "ramp"; it is
`vexp` from here forward.
