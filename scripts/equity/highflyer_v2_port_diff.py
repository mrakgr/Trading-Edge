"""The port's control: diff highflyer_v2_port.py --scale back against the engine's CSV, trip for trip.
    cd research && python3 scripts/equity/highflyer_v2_port_diff.py data/equity/highflyer_repro/v2_engine_partial.csv data/equity/highflyer_repro/v2port_partial_back.parquet
"""
import sys, numpy as np, pandas as pd
E = pd.read_csv(sys.argv[1], parse_dates=["entry_date", "exit_date"]); P = pd.read_parquet(sys.argv[2])
P["entry_date"] = pd.to_datetime(P.entry_date); P["exit_date"] = pd.to_datetime(P.exit_date)
k = ["symbol", "entry_date"]
m = E.merge(P, on=k, how="outer", suffixes=("_e", "_p"), indicator=True)
print("engine", len(E), "port", len(P), m._merge.value_counts().to_dict())
b = m[m._merge == "both"]
for ce, cp in [("entry_price", "entry_price"), ("exit_price", "exit_price"), ("rvol_at_entry", "rvol"), ("pct_up_at_entry", "pct_up"),
               ("atr_pct_14_at_entry", "atr_log"), ("tightness_14_at_entry", "tightness"), ("avg_dollar_volume_4w_at_entry", "adv")]:
    rel = np.abs(b[ce + ("_e" if ce in P.columns else "")] / b[cp + ("_p" if cp in E.columns else "")] - 1)
    print(f"  {ce:32} max rel diff {np.nanmax(rel):.2e}")
print("  exit_date mismatches", int((b.exit_date_e != b.exit_date_p).sum()), " open flag mismatches", int((b.open_e != b.open_p).sum()))
print(m[m._merge != "both"][k + ["_merge"]].head(20).to_string())
