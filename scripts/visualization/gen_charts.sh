#!/bin/bash
SEEDS="${@:-42 123 456 789}"
for seed in $SEEDS; do
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -o data/test_hmm_${seed}.csv
    python3 scripts/visualization/chart_trades.py data/test_hmm_${seed}.csv data/hmm_chart_${seed}.html
    python3 scripts/visualization/equivolume_sim.py data/test_hmm_${seed}.csv 10000 data/equivolume_${seed}.html
done
