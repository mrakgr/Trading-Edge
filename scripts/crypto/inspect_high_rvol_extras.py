"""Extra cuts on the high-rvol shorts to extend the qualitative review.

  - quarterly P&L: are the losers concentrated in a regime?
  - "single-trip symbols": how much P&L comes from symbols where we
    only ever entered once at this rvol level (likely just-listed
    pumps that don't yet have repeat behaviour)?
  - mfe-bucket P&L: what fraction of net P&L sits in trades that
    eventually reached MFE > 200bp? if the edge is mostly the long
    tail, a tighter wait-for-mfe rule could matter.
"""
import os, pandas as pd

DEFAULT = "data/crypto/cumsum_z_persistexit/trips_th15_volratio_30d8h.csv"

def show_quarterly(df, label):
    df = df.copy()
    df["q"] = pd.to_datetime(df.entry_us, unit="us", utc=True).dt.to_period("Q").astype(str)
    g = df.groupby("q").agg(n=("net_pnl","size"),
                            pnl=("net_pnl","sum"),
                            w=("net_pnl", lambda s: int((s>0).sum())))
    g["wr"] = (100.0 * g.w / g.n).round(1)
    g["pf"] = df.groupby("q").apply(
        lambda g_: g_.loc[g_.net_pnl>0,"net_pnl"].sum() /
                   max(1e-9, -g_.loc[g_.net_pnl<0,"net_pnl"].sum())).round(2)
    print(f"--- {label}: quarterly ---")
    print(g.to_string(formatters={"pnl": "{:+,.0f}".format}))
    print()

def show_singletrip(df, full_df, label):
    """Count, per symbol, how many shorts at >=5x rvol it has across the
    whole tape. Trades on symbols that only ever showed >=5x once tend
    to be just-listed pumps without an established mean-reversion."""
    rvol_ge_5 = full_df[(full_df.side=="short") & (full_df.ratio>=5.0)]
    counts = rvol_ge_5.symbol.value_counts()
    df = df.copy()
    df["sym_ge5x_count"] = df.symbol.map(counts).fillna(1).astype(int)
    print(f"--- {label}: P&L by symbol's repeat-count at >=5x rvol ---")
    rows = []
    for k,lbl in [(1,"1 trade"), (2,"2 trades"), (3,"3-5"), (6,">=6")]:
        if lbl == "1 trade":   sub = df[df.sym_ge5x_count==1]
        elif lbl == "2 trades":sub = df[df.sym_ge5x_count==2]
        elif lbl == "3-5":     sub = df[df.sym_ge5x_count.between(3,5)]
        else:                  sub = df[df.sym_ge5x_count>=6]
        if len(sub)==0: continue
        wins = sub[sub.net_pnl>0]; losses = sub[sub.net_pnl<0]
        pf = wins.net_pnl.sum() / max(1e-9, -losses.net_pnl.sum())
        rows.append((lbl, len(sub), 100.0*len(wins)/len(sub),
                     pf, sub.net_pnl.sum(), sub.net_pnl.mean()))
    print(f"  {'rep_count':<10s}  {'n':>5s}  {'wr%':>6s}  {'pf':>6s}  "
          f"{'pnl':>10s}  {'avg':>9s}")
    for lbl,n,wr,pf,pnl,avg in rows:
        print(f"  {lbl:<10s}  {n:>5d}  {wr:>5.1f}%  {pf:>6.2f}  "
              f"{pnl:>+10,.0f}  {avg:>+9,.2f}")
    print()

def show_mfe_bucket(df, label):
    """How much of the net P&L comes from how big a peak MFE? If almost
    all of it sits at MFE > 200bp, then a tighter 'must reach +Xbp by
    bar Y' filter could improve PF."""
    df = df.copy()
    df["mfe_b"] = pd.cut(df.mfe, [-1, 50, 100, 200, 400, 1e9],
                        labels=["<50","50-100","100-200","200-400",">=400"])
    print(f"--- {label}: P&L by max favorable excursion ---")
    g = df.groupby("mfe_b", observed=False).agg(
        n=("net_pnl","size"), pnl=("net_pnl","sum"),
        w=("net_pnl", lambda s: int((s>0).sum())))
    g["wr%"] = (100.0*g.w/g.n).round(1)
    g["avg"] = (g.pnl/g.n).round(2)
    print(g.to_string(formatters={"pnl":"{:+,.0f}".format, "avg":"{:+,.2f}".format}))
    print()

def main():
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__),"..",".."))
    df = pd.read_csv(os.path.join(repo, DEFAULT))
    short = df[df.side=="short"]
    b5_10  = short[(short.ratio>=5)  & (short.ratio<10)]
    b10p   = short[short.ratio>=10]

    show_quarterly(b5_10, "SHORT 5-10x")
    show_quarterly(b10p,  "SHORT >=10x")

    show_singletrip(b5_10, df, "SHORT 5-10x")
    show_singletrip(b10p,  df, "SHORT >=10x")

    show_mfe_bucket(b5_10, "SHORT 5-10x")
    show_mfe_bucket(b10p,  "SHORT >=10x")

if __name__ == "__main__":
    main()
