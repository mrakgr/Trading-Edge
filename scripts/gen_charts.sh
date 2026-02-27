#!/bin/bash
SEEDS="${@:-42 123 456 789}"
for seed in $SEEDS; do
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -o data/test_hmm_${seed}.csv
    python3 scripts/chart_trades.py data/test_hmm_${seed}.csv data/hmm_chart_${seed}.html
done
