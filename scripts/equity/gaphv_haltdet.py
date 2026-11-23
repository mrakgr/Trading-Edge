"""GH-25 — does FlushFader's S40x halt detector find ZJYL's / PGHL's halts? (user 2026-11-23: "I bet there would be all kinds of
stock halts without sufficient activity to meet the last condition ... maybe it could be worth raising that to 20 or 30. We
should test whether the halt detector as stated in FlushFader could detect the ZJYL halts").

The S40x detector (TradingEdge.FlushFader/Intraday.fs, user design 2026-08-01), re-implemented on the 1s slim bars of the day:
a tradeless run (the seconds between two present 1s bars, RTH) is a HALT iff
  run >= 58 s  ·  the pre-hole range ln(max / min) of the 1s vwaps over the last 300 PRESENT bars >= 4 %  ·
  the pre-hole ADJUSTED gap60 < G, gap60 = the missing seconds in the 60 s wall-clock window ending at the last bar before the
  hole, minus those inside earlier classified halts (back-to-back halts classify by recursion)
with G = 4 (FlushFader's), 20, 30. The truth: GH-15's halts from the quotes (the listing's locked indicative; ZJYL 6, PGHL 3).
    cd research && python3 scripts/equity/gaphv_haltdet.py > data/equity/gaphv/haltdet.md
"""
import duckdb, numpy as np

DAYS = [('ZJYL', '2023-12-18', ['09:32:39', '09:38:11', '09:48:31', '09:58:54', '10:14:33', '11:25:09']),
        ('PGHL', '2024-11-26', ['09:32:44', '09:42:21', '09:48:26'])]
GS = (4, 20, 30)
O, C = 34200, 57600
def hs(t): h, m, s = map(int, t.split(':')); return h * 3600 + m * 60 + s
def fm(s): s = int(s); return f'{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}'

con = duckdb.connect()
print('# GH-25 — FlushFader\'s S40x halt detector on ZJYL 2023-12-18 and PGHL 2024-11-26\n')
print('Every tradeless run >= 58 s in RTH: its pre-hole range over the last 300 present bars (>= 4 % needed) and its adjusted '
      'gap60 (the missing seconds of the minute before, < G needed), classified at G = 4 / 20 / 30. "truth" = a halt in the quotes '
      '(GH-15).\n')
for tk, d, truth in DAYS:
    B = con.execute(f"SELECT bucket, vwap FROM 'data/intraday_1s_slim/{d}.parquet' WHERE ticker = '{tk}' AND bucket >= {O} AND bucket < {C} "
                    "AND volume > 0 AND vwap > 0 ORDER BY bucket").fetchnumpy()
    sec, px = B['bucket'].astype(int), B['vwap'].astype(float)
    tr = [hs(t) for t in truth]
    print(f'## {tk} {d}: {len(sec):,} present bars, {len(truth)} halts in the quotes\n')
    print('| run (last bar → next bar) | run s | rng300 | gap60 raw | ' + ' | '.join(f'G={g}: adj gap60 · halt?' for g in GS) + ' | truth |')
    print('|---|---|---|---|' + '---|' * len(GS) + '---|')
    halts = {g: [] for g in GS}            # the classified (start, end) intervals so far, per G
    found = {g: 0 for g in GS}; false = {g: 0 for g in GS}
    for i in range(len(sec)):
        nxt = sec[i + 1] if i + 1 < len(sec) else C
        run = nxt - sec[i] - 1
        if run < 58: continue
        lo = max(0, i - 299); w = px[lo:i + 1]
        rng = np.log(w.max() / w.min()) if len(w) else np.nan
        win0 = max(O, sec[i] - 59)
        present = ((sec >= win0) & (sec <= sec[i])).sum()
        raw = (sec[i] - win0 + 1) - present
        cells = []
        is_true = any(abs(sec[i] + 1 - t) <= 2 for t in tr)
        for g in GS:
            ov = sum(max(0, min(b, sec[i]) - max(a, win0) + 1) for a, b in halts[g])
            adj = max(0, raw - ov)
            h = (rng >= 0.04) and (adj < g) and sec[i] >= O
            if h:
                halts[g].append((sec[i] + 1, nxt - 1))
                if is_true: found[g] += 1
                else: false[g] += 1
            cells.append(f'{adj} · {"✅" if h else "—"}')
        print(f'| {fm(sec[i])} → {fm(nxt) if nxt < C else "16:00 (close)"} | {run:,} | {100 * rng:.1f} % | {raw} | ' + ' | '.join(cells) +
              f' | {"HALT" if is_true else ""} |')
    print('\n' + ' · '.join(f'G={g}: found {found[g]} of {len(truth)}, false {false[g]}' for g in GS) + '\n')
