#!/usr/bin/env python3
"""News pilot follow-ups (2026-11-19) — two checks on top of `edgar_tape_pilot_report.py`:
  A. the EDGAR-FIRST subset: small names (prior ADV < $20M) outside regular hours whose 30 min BEFORE acceptance was flat
     (|pre| < 2%) and whose 30 min after moved ≥ 10% — how fast that move happens, by form and 8-K item;
  B. mover-side coverage against EVERY filing form (the report counts only the news forms; small caps often report in a
     10-Q with no 8-K): any filing from the prior 16:00 (pre) / 09:30 (post) to the onset + 5 min.

    cd research && python3 scripts/news/edgar_tape_pilot_followups.py > logs/news_edgar_tape_pilot_followups.log 2>&1
"""
# ---------------- A ----------------
import glob, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)
P='data/news/pilot'
ev=pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{P}/events/*.parquet'))], ignore_index=True)
ev['split_today']=ev.split_today.astype('boolean').fillna(False).astype(bool)
ev=ev[(ev.n_bars>0)&ev.p_0.notna()&~ev.split_today].drop_duplicates(['ticker','day','t0'])
liq=duckdb.sql(f"""with s as (select ticker, regexp_extract(filename,'(\\d{{4}}-\\d{{2}}-\\d{{2}})',1) as day, dv_pre+dv_rth+dv_post as dv
  from read_parquet('{P}/stats/*.parquet', filename=true))
  select ticker, day, avg(dv) over (partition by ticker order by day rows between 20 preceding and 1 preceding) as adv20 from s""").df()
ev=ev.merge(liq,on=['ticker','day'],how='left')
ev['pre']=np.log(ev.p_0/ev.p_m1800); ev['post']=np.log(ev.p_1800/ev.p_0)
for h in (1,5,10,30,60,120,300,600): ev[f'd{h}']=(np.log(ev[f'p_{h}']/ev.p_0)/ev.post).clip(-1,2)
ev['sess']=np.select([ev.overnight, ev.t0<34200, ev.t0<57600],['overnight','pre','rth'],'post')
s=ev[(ev.adv20<20e6)&(ev.sess.isin(['pre','post']))]
ef=s[(s.pre.abs()<0.02)&(s.post.abs()>=0.10)]
big=s[s.post.abs()>=0.10]
print('small out-of-hours events', len(s), '| |post|>=10%:', len(big), '| of those EDGAR-first (|pre|<2%):', len(ef), f'{len(ef)/len(big):.2f}')
print('\nEDGAR-first: share of the 30-min move done by +h s (quartiles)')
print(ef[[f'd{h}' for h in (1,5,10,30,60,120,300,600)]].quantile([.25,.5,.75]).round(2).to_string())
print('\nfirst print after acceptance (s), EDGAR-first:', ef.first_after.quantile([.25,.5,.75,.9]).round(0).tolist(),
      '| all small out-of-hours:', s.first_after.quantile([.25,.5,.75,.9]).round(0).tolist())
print('\nEDGAR-first by session × direction:'); print(ef.groupby(['sess', np.sign(ef.post)]).size().to_string())
# per form share EDGAR-first among big movers
def lab(r):
    return r.form
big=big.assign(ef=(big.pre.abs()<0.02))
print('\nby form: n big, share EDGAR-first, median d60, d300 among EDGAR-first')
g=big.groupby('form').agg(n=('ef','size'), ef_share=('ef','mean'))
g2=big[big.ef].groupby('form')[['d60','d300']].median()
print(g.join(g2).sort_values('n',ascending=False).head(14).round(2).to_string())
k=big[big.form.str.startswith('8-K')].assign(item=lambda d: d['items'].fillna('').str.split(',')).explode('item')
k=k[~k.item.isin(['','9.01'])]
g=k.groupby('item').agg(n=('ef','size'), ef_share=('ef','mean'), up=('post', lambda x:(x>0).mean()))
print('\n8-K items among big movers:'); print(g[g.n>=20].sort_values('n',ascending=False).round(2).to_string())

# ---------------- B ----------------
uni=pd.read_parquet('data/news/edgar/universe.parquet')[['cik','ticker']].drop_duplicates()
fl=duckdb.sql("""select cik, form, timezone('America/New_York', acceptance_utc) as et from read_parquet('data/news/edgar/filings/*.parquet')
                 where acceptance_utc is not null""").df().drop_duplicates()
fl=fl.merge(uni,on='cik')
mv=pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{P}/movers/*.parquet'))], ignore_index=True)
mv=mv[mv.ticker.isin(set(uni.ticker)) & ~mv.split_today.astype('boolean').fillna(False).astype(bool)]
liq=duckdb.sql(f"""with s as (select ticker, regexp_extract(filename,'(\\d{{4}}-\\d{{2}}-\\d{{2}})',1) as day, dv_pre+dv_rth+dv_post as dv
  from read_parquet('{P}/stats/*.parquet', filename=true))
  select ticker, day, avg(dv) over (partition by ticker order by day rows between 20 preceding and 1 preceding) as adv20 from s""").df()
mv=mv.merge(liq,on=['ticker','day'],how='left')
days=sorted(mv.day.unique()); prevd=dict(zip(days[1:],days[:-1]))
fl=fl.sort_values('et'); g={t:x for t,x in fl.groupby('ticker')}
NEWS={'8-K','8-K/A','6-K','6-K/A','424B1','424B3','424B4','424B5','424B7','SC 13D','SC 13D/A','SCHEDULE 13D','SCHEDULE 13D/A','S-1','F-1','S-3','F-3'}
for X,cu,cd in ((0.10,'t_up10','t_dn10'),(0.20,'t_up20','t_dn20')):
  m=mv[(mv.dv>=1e6)&(mv.adv20<20e6)].copy()
  m['onset']=np.fmin(m[cu].astype(float), m[cd].astype(float)); m=m[m.onset.notna()]
  for sess in ('pre','post'):
    ms=m[m.sess==sess]; res=[]; forms=[]
    for r in ms.itertuples(index=False):
      x=g.get(r.ticker)
      on=pd.Timestamp(r.day)+pd.Timedelta(seconds=float(r.onset))
      lo=(pd.Timestamp(prevd.get(r.day, r.day))+pd.Timedelta(hours=16)) if sess=='pre' else pd.Timestamp(r.day)+pd.Timedelta(hours=9.5)
      if x is None: res.append('none'); forms.append(None); continue
      et=x.et.dt.tz_localize(None)
      w=x[(et>=lo)&(et<=on+pd.Timedelta(minutes=5))]
      if len(w)==0: res.append('none'); forms.append(None); continue
      wt=w.et.dt.tz_localize(None)
      at=w[(wt>=on-pd.Timedelta(minutes=15))]
      nw=w.form.isin(NEWS)
      res.append(('at_onset' if len(at) else 'earlier') + ('' if nw.any() else '_other'))
      forms.append(','.join(sorted(set(w.form))))
    s=pd.Series(res)
    print(f'±{X:.0%} {sess} n={len(ms)}:', s.value_counts(normalize=True).round(3).to_dict())
    if X==0.20:
      f=pd.Series([y for y in forms if y]).str.split(',').explode()
      print('   forms in window (top):', f.value_counts().head(12).to_dict())
