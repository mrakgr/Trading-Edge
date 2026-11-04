"""FlushFader S49al-3 (user 2026-11-04): what if a resting fill before the trigger did NOT end the signal (the Oms's
RestedOut rule off)? The capped maker entry re-buys once a 1s vwap is at or below the entry (the cap), at the
NEXT bar's vwap capped at the entry, and the re-entry is held to the model's exit. From S49al's sim.parquet + the 1s slim bars (raw, = the book's prices).
    python3 scripts/equity/flushfader_rebuy_sim.py > logs/s49al3_buyback.log 2>&1; echo exit $?
"""
import pandas as pd, numpy as np, pyarrow.parquet as pq, pyarrow as pa, sys
c=['key','symbol','trade_date','year','entry_sec','exit_sec','entry_px','exit_px','n','pl_MODEL','pl_REST60|queue','t1_REST60|queue','px_REST60|queue','pl_REST60|back','t1_REST60|back','px_REST60|back']
d=pd.read_parquet('data/equity/flushfader_exit_sim/sim.parquet', columns=c)
out=[]
for fm in ('queue','back'):
    d[f'fill_{fm}']=d.exit_sec + d[f't1_REST60|{fm}']
early=d[(d['t1_REST60|queue']<0)|(d['t1_REST60|back']<0)]
print('trips with a fill before the trigger (either model):', len(early), file=sys.stderr)
res={}
for day, g in early.groupby('trade_date'):
    t=pq.read_table(f'data/intraday_1s_slim/{day}.parquet', columns=['ticker','bucket','vwap'],
                    filters=[('ticker','in',list(g.symbol.unique()))]).to_pandas()
    by={k: v.sort_values('bucket') for k, v in t.groupby('ticker')}
    for r in g.itertuples():
        b=by.get(r.symbol)
        for fm in ('queue','back'):
            f=getattr(r, f'fill_{fm}')
            if not (f < r.exit_sec): continue
            # bars strictly after the fill, before the trigger bar (exit_sec - 1)
            w=b[(b.bucket >= np.floor(f) + 1) & (b.bucket < r.exit_sec - 1)]
            hit=np.flatnonzero(w.vwap.values <= r.entry_px)
            # the capped maker bid buys at the market when the market is below the cap: the NEXT bar's vwap, capped
            # (2026-11-04 fix: the first version bought at the cap itself, but these resting fills are mostly BELOW the
            # entry, a 5m high that had decayed under it, so the market was already below the cap)
            if len(hit) and hit[0] + 1 < len(w):
                res[(r.key, fm)] = (True, min(r.entry_px, float(w.vwap.values[hit[0] + 1])))
            else:
                res[(r.key, fm)] = (False, np.nan)
rows=[]
for fm in ('queue','back'):
    x=d.copy()
    k=[res.get((kk, fm), (False, np.nan)) for kk in x.key]
    x['rebuy']=[a for a, _ in k]; x['rebuy_px']=[p for _, p in k]
    early_m = x[f't1_REST60|{fm}'] < 0
    # the re-entry: n shares at entry_px (the cap), held to the model exit
    x['pl_rebuy']=np.where(x.rebuy, x.n * (x.exit_px - x.rebuy_px), 0.0)
    nb=1e4 / (x.n * x.entry_px)
    base=x[f'pl_REST60|{fm}']; alt=base + x.pl_rebuy
    def pf(r): g_=r[r>0].sum(); l=-r[r<0].sum(); return g_/l
    rows.append(dict(fill_model=fm, early_fills=int(early_m.sum()), rebuys=int(x.rebuy.sum()),
                     rebuy_pnl=round(x.pl_rebuy.sum()), rebuy_bp=round((x.pl_rebuy*nb)[x.rebuy].mean(),1),
                     rebuy_win=round(100*(x.pl_rebuy[x.rebuy]>0).mean(),1),
                     rest60_bp=round((base*nb).mean(),2), with_rebuy_bp=round((alt*nb).mean(),2),
                     pf_rest60=round(pf(base*nb),3), pf_with=round(pf(alt*nb),3), model_bp=round((x.pl_MODEL*nb).mean(),2)))
    yr=x[x.rebuy].groupby('year')[['n','pl_rebuy','entry_px']].apply(lambda y: pd.Series({'n': len(y), 'bp': (y.pl_rebuy*1e4/(y.n*y.entry_px)).mean()}))
    print(fm, 'rebuys by year:', ' · '.join(f'{int(i)} {v.bp:+.0f} bp ({int(v.n)})' for i, v in yr.iterrows()))
print(pd.DataFrame(rows).to_string(index=False))
