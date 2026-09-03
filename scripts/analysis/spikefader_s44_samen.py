import sys, pandas as pd, numpy as np; sys.path.insert(0,'scripts/analysis')
from spikefader_zq import pfm1
SC='/tmp/claude-1000/-home-mrakgr-Trading-Edge/48da2bbb-3acf-462f-b6d5-74fb8b86dee0/scratchpad/'
df=pd.read_pickle(SC+'s44_spec.pkl')

def mc1_fast(d):
    """Greedy one-position-per-ticker-day in signal order. Same semantics as
    spikefader_zq.mc1 (itertuples loop) but grouped so the inner loop is over
    a single day's trips, not the whole book."""
    d=d.sort_values(['symbol','trade_date','signal_sec'])
    ss=d['signal_sec'].to_numpy(); es=d['exit_sec'].to_numpy()
    keys=(d['symbol'].astype(str)+'|'+d['trade_date'].astype(str)).to_numpy()
    keep=np.zeros(len(d),dtype=bool); busy=-1.0; prev=None
    for i in range(len(d)):
        if keys[i]!=prev: busy=-1.0; prev=keys[i]
        if ss[i]>=busy: keep[i]=True; busy=es[i]
    return d[keep]

# oracle: identical to the reference implementation on a slice
from spikefader_zq import mc1 as mc1_ref
chk=df.head(4000)
a=mc1_fast(chk).index; b=mc1_ref(chk).index
assert list(a)==list(b), "mc1_fast diverges from the reference"
print(f"mc1_fast == reference on 4,000-trip slice ({len(a)} kept)  OK", flush=True)

inc=['k600','k300','k180','dlv','be6030','eff_10m']
cands=['sma_highs_600','sma_highs_300','sma_highs_1200','raw_leg_rate_600','raw_leg_mag','sma_leg_mag_1200']
rng=np.random.default_rng(0)
print("\n=== SAME-N CONTROL: at the SAME mc=1 book size, does the candidate beat")
print("    a TIGHTENED INCUMBENT, and beat a RANDOM subset of equal size? ===")
for col in cands:
    thr=df[col].quantile(0.5); b=mc1_fast(df[df[col]>=thr]); n=len(b)
    print(f"\n--- {col} >= median   book n={n:,}   PF-1 {pfm1(b['ret']):.3f} ---", flush=True)
    rows=[]
    for i in inc:
        di=df[~df[i].isna()].sort_values(i,ascending=False)
        lo,hi=0,len(di)
        for _ in range(18):
            mid=(lo+hi)//2
            if len(mc1_fast(di.head(mid)))<n: lo=mid
            else: hi=mid
        bi=mc1_fast(di.head(hi))
        rows.append({'control':f'tighten {i}','n':len(bi),'PF-1':round(pfm1(bi['ret']),3)})
    rs=[pfm1(mc1_fast(df.iloc[rng.choice(len(df),size=int(len(df)*0.5),replace=False)])['ret']) for _ in range(30)]
    rows.append({'control':'RANDOM 50% mean(30)','n':n,'PF-1':round(float(np.mean(rs)),3)})
    rows.append({'control':'  random p90','n':n,'PF-1':round(float(np.quantile(rs,0.9)),3)})
    rows.append({'control':'  random max','n':n,'PF-1':round(float(np.max(rs)),3)})
    print(pd.DataFrame(rows).to_string(index=False), flush=True)
