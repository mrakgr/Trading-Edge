"""LowFader REBUILD step 25 (2026-09-05): ALTERNATIVE EXITS on the spec book — channel-HIGH covers {1m,2m,5m,10m,20m,40m,1h,2h,3h} vs MOC.
ret_h = aux_hi_h_px/entry_px - 1 if the mark filled before the close, else the MOC return (a cover that never triggers holds to MOC).
Views: per-trip (mc=0, same trips, different exit) and a per-exit mc=1 replay (exit_sec = mark sec or MOC; a faster cover frees the slot).
Corpus data/lowfader_wl_spec2 (the 466 spec tkd rerun with the long-horizon marks). PF-1, PF22-1.
Run: python3 -u scripts/analysis/lowfader_rebuild25.py > data/lowfader_rebuild_25.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 340)
H=[60,120,300,600,1200,2400,3600,7200,10800]; LBL={60:'1m',120:'2m',300:'5m',600:'10m',1200:'20m',2400:'40m',3600:'1h',7200:'2h',10800:'3h'}
cols=', '.join(f'aux_hi_{h}_px, aux_hi_{h}_sec' for h in H)
df = duckdb.query(f"""SELECT symbol, trade_date, signal_sec, entry_sec, exit_sec, entry_px, ret_exit, {cols}
  FROM read_parquet('data/lowfader_wl_spec2/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40
  AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04 AND gap_adj_60 <= 30""").df()
base = duckdb.query("""SELECT count(*) n, count(DISTINCT symbol||trade_date) tkd, round(sum(ret_exit),3) s FROM read_parquet('data/lowfader_wl_ewma/*.parquet') WHERE volat_20m > 0.0039 AND volat_20m <= 0.010 AND eff_ewma_10m < -0.7 AND lows_since_first_low_600 >= 40
  AND lows_since_first_low_600 >= 0.15*bars_since_first_low_600 AND vol_60 >= 2.0*vol_0945_tape/15.0 AND entry_px/NULLIF(close_m1+coalesce(div_m1,0),0)-1 <= -0.04 AND gap_adj_60 <= 30""").fetchall()[0]
df['year']=pd.to_datetime(df['trade_date']).dt.year; YEARS=sorted(df.year.unique()); df['tkd']=df.symbol+'|'+df.trade_date.astype(str)
print(f"spec2 corpus: {len(df):,} trips {df.tkd.nunique()} tkd sum(ret) {df.ret_exit.sum():.3f}   |   ewma corpus spec: {base[0]:,} trips {base[1]} tkd sum {base[2]}   identical: {len(df)==base[0] and abs(df.ret_exit.sum()-base[2])<1e-3}")
print('fill rates: ' + '  '.join(f"{LBL[h]} {df[f'aux_hi_{h}_px'].notna().mean()*100:.1f}%" for h in H))
def pf(s):
    w=s[s>0].sum(); l=-s[s<0].sum(); return w/l if l>0 else np.inf
def mc1(d, exit_col):
    d=d.sort_values(['symbol','trade_date','signal_sec']); ss=d['signal_sec'].to_numpy(); es=d[exit_col].to_numpy()
    keys=d['tkd'].to_numpy(); keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]
rows=[]; rows1=[]
for h in [None]+H:
    if h is None: r=df.ret_exit; ex=df.exit_sec; lbl='MOC'; filled=np.zeros(len(df),dtype=bool)
    else:
        px=df[f'aux_hi_{h}_px']; sec=df[f'aux_hi_{h}_sec']; filled=px.notna().values
        r=np.where(filled, px/df.entry_px-1, df.ret_exit); ex=np.where(filled, sec, df.exit_sec); lbl=LBL[h]
    d=df.assign(ret=r, xsec=ex); s=d.ret; s2=d.loc[d.year>=2022,'ret']
    hold=((d.xsec-d.entry_sec)/60)
    rows.append(dict(exit=lbl, fill=round(filled.mean()*100,1), n=len(d), PF1=round(pf(s)-1,2), PF1_22=round(pf(s2)-1,2), win=round((s>0).mean()*100,1), win22=round((s2>0).mean()*100,1), avg=round(s.mean()*100,2), avg22=round(s2.mean()*100,2), net22=round(s2.sum()*100), worst=round(s.min()*100,1), p5=round(s.quantile(.05)*100,1), tail=round((s<-.2).mean()*100,2), hold_med_min=round(hold.median(),1), **{str(y): round(pf(d[d.year==y].ret)-1,2) for y in YEARS}))
    b=mc1(d,'xsec'); s=b.ret; s2=b.loc[b.year>=2022,'ret']
    rows1.append(dict(exit=lbl, n=len(b), tkd=b.tkd.nunique(), PF1=round(pf(s)-1,2), PF1_22=round(pf(s2)-1,2), win22=round((s2>0).mean()*100,1), avg22=round(s2.mean()*100,2), net22=round(s2.sum()*100), worst=round(s.min()*100,1), tail=round((s<-.2).mean()*100,2), trips_per_tkd=round(len(b)/b.tkd.nunique(),2), **{str(y): round(pf(b[b.year==y].ret)-1,2) for y in YEARS}))
print('\n########## A. PER-TRIP (mc=0): same trips, each exit; unfilled mark -> MOC ##########'); print(pd.DataFrame(rows).to_string(index=False))
print('\n########## B. mc=1 REPLAY per exit (one position per tkd at a time; a faster cover frees the slot) ##########'); print(pd.DataFrame(rows1).to_string(index=False))
print('\n########## C. filled-only vs its MOC counterfactual (what the cover changes on the trips it fires on) ##########')
rows=[]
for h in H:
    px=df[f'aux_hi_{h}_px']; f=px.notna(); d=df[f]; rc=d[f'aux_hi_{h}_px']/d.entry_px-1; rm=d.ret_exit
    rows.append(dict(exit=LBL[h], n_filled=len(d), cover_PF1=round(pf(rc)-1,2), moc_PF1=round(pf(rm)-1,2), cover_avg=round(rc.mean()*100,2), moc_avg=round(rm.mean()*100,2), cover_better_pct=round((rc>rm).mean()*100,1), unfilled_n=int((~f).sum()), unfilled_moc_avg=round(df[~f].ret_exit.mean()*100,2) if (~f).any() else None, unfilled_moc_PF1=round(pf(df[~f].ret_exit)-1,2) if (~f).any() else None))
print(pd.DataFrame(rows).to_string(index=False))
print('\nDONE')
