"""rr-QUALIFIED LEG COUNT PORT (2026-09-06, user): LowFader's lows_rr{1,2,3}_{N} (§L16/§L18: on LowFader rr>=5 OR lows_rr3_120>=20
is the size-up voice) read on FlushFader (v50_rr = v49 frame + the new columns; production book from flushfader_book.py, mc=1) and
SpikeFader (s48_rr = s47 frame + the new columns; S38e SPEC, 9m exit, mc=1). First: trip-set identity vs the reference corpora.
Then band tables with year columns, floors, and the counts inside each system's own tiers/grades.
Run: python3 -u scripts/analysis/rrcount_port.py > data/rrcount_port.log
"""
import numpy as np, pandas as pd, duckdb, sys
pd.set_option('display.width', 340); pd.set_option('display.max_columns', 40)
sys.path.insert(0,'scripts/analysis'); from spikefader_zq import SPEC, DERIVED
con=duckdb.connect()
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def tpf1(s, q=0.05):
    if len(s)<20: return np.nan
    k=s[s>=np.quantile(s,q)]; return pf(k)-1
def mc1(f):
    f=f.sort_values(['tkd','signal_sec']).reset_index(drop=True); keep=np.zeros(len(f),bool); last=-1; prev=None
    key=f.tkd.values; ent=f.entry_sec.values; ext=f.exit_sec.values
    for i in range(len(f)):
        if key[i]!=prev: prev=key[i]; last=-1
        if ent[i]>=last: keep[i]=True; last=ext[i]
    return f[keep].sort_values(['trade_date','entry_sec']).reset_index(drop=True)
YE=[2020,2021,2022,2023,2024,2025,2026]
def bands(b, col, edges, title, extra=None):
    d=b.copy(); d['band']=pd.cut(d[col],edges); rows=[]
    for k,g in d.groupby('band',observed=True):
        if len(g)<20: continue
        r=dict(band=str(k), n=len(g), pct=round(len(g)/len(d)*100), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1))
        for y in YE: gy=g[g.year==y]; r[str(y)]=round(pf(gy.ret)-1,2) if len(gy)>=10 else None
        if extra: r.update(extra(g))
        rows.append(r)
    print(f'\n--- {title} ---'); print(pd.DataFrame(rows).to_string(index=False))
def floors(b, col, ths, title):
    rows=[]
    for lbl,m in [('all',np.ones(len(b),bool))]+[(f'{col} >= {t}',(b[col]>=t).values) for t in ths]+[(f'{col} = 0',(b[col]==0).values)]:
        g=b[m]
        if len(g)==0: continue
        r=dict(gate=lbl, n=len(g), pct=round(len(g)/len(b)*100), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), net=round(g.ret.sum()*100), worst=round(g.ret.min()*100,1))
        for y in YE: gy=g[g.year==y]; r[str(y)]=round(pf(gy.ret)-1,2) if len(gy)>=10 else None
        rows.append(r)
    print(f'\n--- {title} ---'); print(pd.DataFrame(rows).to_string(index=False))
RR=lambda p: [f'{p}_rr{k}_{N}' for N in [120,300,600,1200] for k in [1,2,3]]
RUN_FF='--sf-only' not in sys.argv; RUN_SF='--ff-only' not in sys.argv
# ================= FlushFader =================
if RUN_FF:
    NEW='data/equity/flushfader/v50_rr/trips_p*.parquet'; REF='data/equity/flushfader/v49_spec20/trips_p*.parquet'
    n_new=con.execute(f"SELECT count(*) FROM read_parquet('{NEW}')").fetchone()[0]; n_ref=con.execute(f"SELECT count(*) FROM read_parquet('{REF}')").fetchone()[0]
    n_both=con.execute(f"SELECT count(*) FROM (SELECT symbol, trade_date, signal_sec FROM read_parquet('{NEW}') INTERSECT SELECT symbol, trade_date, signal_sec FROM read_parquet('{REF}'))").fetchone()[0]
    print(f"FlushFader trip-set identity: v50_rr {n_new:,} vs v49 {n_ref:,}, common {n_both:,}")
    BOOK_WHERE="""gap_60 < 4 AND entry_px >= 1 AND volat_20m >= 0.004 AND signal_sec <= 54000 AND lows_since_first_low_180 >= 3 AND (
        COALESCE(volat_20m*1e4 >= 140, false) OR COALESCE((signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28, false)
        OR COALESCE(signal_vwap/sess_low - 1 >= 0.08, false) OR COALESCE((volat_slope_10m - volat_slope_20m)*2e4 > 12, false)
        OR COALESCE(volat_slope_5m*2e4 <= -24, false) OR COALESCE(ac1_ewma < -0.1, false)
        OR COALESCE(secs_since_first_low >= 0 AND secs_since_first_low <= 450, false) OR COALESCE(downticks_since_uptick >= 8, false)
        OR COALESCE(secs_since_halt >= 1200 AND secs_since_halt < 4800, false)
        OR COALESCE(halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200, false))"""
    f=con.execute(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, gap_adj_1200, ols_slope_60, vol_60/NULLIF(vol_0945_tape/15.0,0) AS rr, lows_since_first_low_600 AS k600, lows_since_first_low_120 AS k120, {', '.join(RR('lows'))}
      FROM read_parquet('{NEW}') WHERE {BOOK_WHERE}""").df(); f['tkd']=f.symbol+'|'+f.trade_date.astype(str)
    b=mc1(f); b['year']=pd.to_datetime(b.trade_date).dt.year
    b['tier']=np.where((b.gap_adj_1200<15)&(b.ols_slope_60*6e5<=-350),'A',np.where(b.gap_adj_1200<15,'B',np.where(b.ols_slope_60*6e5<=-350,'C','D')))
    print(f"FlushFader production book on v50_rr: {len(b):,} trades, PF {pf(b.ret):.3f} (reference 1,369 @ 4.128), tiers {b.tier.value_counts().to_dict()}")
    print('\nquantiles (FlushFader book):'); print(b[RR('lows')+['rr','k120','k600']].quantile([.1,.25,.5,.75,.9]).round(2).to_string())
    print('rho with ret:', {c: round(b[[c,'ret']].corr().iloc[0,1],3) for c in RR('lows')})
    for col in ['lows_rr3_120','lows_rr2_120','lows_rr3_600','lows_rr2_600','lows_rr3_1200']:
        bands(b, col, [-1,0,2,5,10,20,40,1e9], f'FlushFader {col} (mc=1 book, PF-1 by band; year cols = PF-1)')
    floors(b, 'lows_rr3_120', [1,3,5,10,20], 'FlushFader floors lows_rr3_120'); floors(b, 'lows_rr2_120', [5,10,20,40], 'FlushFader floors lows_rr2_120')
    print('\n--- FlushFader: lows_rr3_120 >= 10 inside each production tier (A/B/C/D) ---'); rows=[]
    for t in 'ABCD':
        for lbl,m in [('all',b.tier==t),('rr3_120>=10',(b.tier==t)&(b.lows_rr3_120>=10)),('rr3_120<10',(b.tier==t)&(b.lows_rr3_120<10))]:
            g=b[m]; rows.append(dict(tier=t, cut=lbl, n=len(g), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1)))
    print(pd.DataFrame(rows).to_string(index=False))
    print('\n--- FlushFader: the LowFader voice transplanted: rr>=5 OR lows_rr3_120>=20 ---'); rows=[]
    for lbl,m in [('X: rr>=5 | rr3_120>=20',((b.rr>=5)|(b.lows_rr3_120>=20)).values),('not X',~((b.rr>=5)|(b.lows_rr3_120>=20)).values),('rr>=5',(b.rr>=5).values),('rr<0.5 (SpikeFader quiet voice, for reference)',(b.rr<0.5).values)]:
        g=b[m]; r=dict(cell=lbl, n=len(g), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1))
        for y in YE: gy=g[g.year==y]; r[str(y)]=round(pf(gy.ret)-1,2) if len(gy)>=10 else None
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
# ================= SpikeFader =================
if RUN_SF:
    NEW='data/spikefader_s48_rr/*.parquet'; REF='data/spikefader_s47/*.parquet'
    n_new=con.execute(f"SELECT count(*) FROM read_parquet('{NEW}')").fetchone()[0]; n_ref=con.execute(f"SELECT count(*) FROM read_parquet('{REF}')").fetchone()[0]
    n_both=con.execute(f"SELECT count(*) FROM (SELECT symbol, trade_date, signal_sec FROM read_parquet('{NEW}') INTERSECT SELECT symbol, trade_date, signal_sec FROM read_parquet('{REF}'))").fetchone()[0]
    print(f"\n\nSpikeFader trip-set identity: s48_rr {n_new:,} vs s47 {n_ref:,}, common {n_both:,}")
    sp=SPEC.replace('dlv >', 'signal_vwap / NULLIF(sess_low,0) - 1 >').replace('ols_slope_1200 >= 0.0030', 'ols_slope_1200 >= 5.0e-05')
    f=con.execute(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, ret_exit AS ret, volat_20m, halts_today, secs_since_halt, highs_since_first_high_600 AS k600, highs_since_first_high_120 AS k120, {', '.join(RR('highs'))} {DERIVED}
      FROM read_parquet('{NEW}') WHERE {sp}""").df(); f['tkd']=f.symbol+'|'+f.trade_date.astype(str)
    b=mc1(f); b['year']=pd.to_datetime(b.trade_date).dt.year
    # ROSTER v3.4 grade = strongest voice: A rr<0.5, B dslo<=-5%, X rr>=12, C ht>=2 & fresh[60,300), D volat>=100bp, E none
    b['grade']=np.where(b.rr<0.5,'A',np.where(b.dslo<=-0.05,'B',np.where(b.rr>=12,'X',np.where((b.halts_today>=2)&(b.secs_since_halt>=60)&(b.secs_since_halt<300),'C',np.where(b.volat_20m>=0.010,'D','E')))))
    print(f"SpikeFader spec book on s48_rr: {len(b):,} trades, PF-1 {pf(b.ret)-1:.3f} (reference 3,573 @ ~1.18), grades {b.grade.value_counts().to_dict()}")
    print('\nquantiles (SpikeFader book):'); print(b[RR('highs')+['rr','k120','k600']].quantile([.1,.25,.5,.75,.9]).round(2).to_string())
    print('rho with ret:', {c: round(b[[c,'ret']].corr().iloc[0,1],3) for c in RR('highs')})
    for col in ['highs_rr3_120','highs_rr2_120','highs_rr1_120','highs_rr3_600','highs_rr2_600','highs_rr3_1200']:
        bands(b, col, [-1,0,2,5,10,20,40,1e9], f'SpikeFader {col} (mc=1 spec book, 9m exit)')
    floors(b, 'highs_rr3_120', [1,3,5,10,20], 'SpikeFader floors highs_rr3_120'); floors(b, 'highs_rr2_600', [5,10,20,40], 'SpikeFader floors highs_rr2_600')
    print('\n--- SpikeFader: the count inside each ROSTER grade (A rr<0.5 · B dslo · X rr>=12 · C halts · D volat · E none) ---'); rows=[]
    for gname in ['A','B','X','C','D','E']:
        for lbl,m in [('all',b.grade==gname),('rr3_120>=10',(b.grade==gname)&(b.highs_rr3_120>=10)),('rr3_120<10',(b.grade==gname)&(b.highs_rr3_120<10)),('rr3_120=0',(b.grade==gname)&(b.highs_rr3_120==0))]:
            g=b[m]
            if len(g)<10: continue
            rows.append(dict(grade=gname, cut=lbl, n=len(g), PF1=round(pf(g.ret)-1,3), tPF1=round(tpf1(g.ret),3), win=round((g.ret>0).mean()*100,1), avg=round(g.ret.mean()*100,2), worst=round(g.ret.min()*100,1)))
    print(pd.DataFrame(rows).to_string(index=False))
    print('\n--- SpikeFader: the INVERSE of the LowFader voice (quiet legs): highs_rr1_120 = 0 / frac of rr>=1 highs ---')
    b['frac1_120']=b.highs_rr1_120/(b.k120+1); b['frac2_600']=b.highs_rr2_600/(b.k600+1)
    bands(b, 'frac1_120', [-0.01,0.1,0.25,0.5,0.75,1.01], 'SpikeFader frac of 2m-leg highs with rr>=1'); bands(b, 'frac2_600', [-0.01,0.1,0.25,0.5,0.75,1.01], 'SpikeFader frac of 10m-leg highs with rr>=2')
print('\nDONE')
