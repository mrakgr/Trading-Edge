# models/ — the production models (tracked)

The fitted coefficients the live system runs on. They are kept out of `data/`, which git ignores (run inputs and outputs),
so they are versioned like code (user 2026-11-05). Each is written by a research script, applied by the research
references the Oms is parity-sealed against, and embedded in the Oms, whose tests pin it against the file here. Change
one only together with its system's port.

| file | model | written by | read by |
|---|---|---|---|
| `gapfader/gf_coefficients.json` | GapFader's sizing, GF-67 (GF-54's + deep10 + deep10 × QQQ ≤ −1 %) | `scripts/equity/gapfader_model_v3.py` | the Oms's GapFaderTests (200 pins) |
| `gapfader/gf_coefficients_pre_gf67.json` | GapFader GF-54 (production 2026-10-27 .. 11-05) | `gapfader_model_v2.py` (then) | — |
| `gapfader/gf_coefficients_pre_gf54.json` | GapFader GF-45d (production until 2026-10-27) | `gapfader_sizing_model.py` | the older GapFader studies (spiral, consistency, dv60) |
| `flushfaderv2/v2_coefficients.json` | FlushFader's sizing, V2-25 (the CUSUM term) | `scripts/equity/flushfaderv2_reference.py` (fit mode, then promoted here) | `flushfaderv2_reference.py --coef` (the reference book), the Oms's SizingTests |
| `flushfader_gate_review/a3_coefficients.json` | FlushFader's A3 model (the broad book, §S49aj; superseded by V2 in 2026-10) | `flushfader_sizing_model.py --dump-coefficients` | `flushfader_broad_reference.py` |

A writer that fits into its own output directory (`flushfaderv2_reference.py` writes `{--out-dir}/v2_coefficients.json`,
default `data/flushfaderv2/`) produces a CANDIDATE; promoting it to production is a deliberate copy here, together with the
Oms's port and its pins.

Commands run from `research/`, as everywhere (CLAUDE.md), so `models/...` paths are cwd-relative.
