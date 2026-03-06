param(
    [int[]]$Seeds = @(42, 123, 456, 789)
)

foreach ($seed in $Seeds) {
    Write-Host "Generating charts for seed $seed..."

    # Dump trades
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -d "data/tdigests/LW_2025-12-19.tdigest" -o "data/test_hmm_$seed.csv"

    # Generate tick chart
    python3 scripts/visualization/sim_tick.py "data/test_hmm_$seed.csv" "data/charts/sim_tick_$seed.html"

    # Generate candle chart
    python3 scripts/visualization/sim_candle.py "data/test_hmm_$seed.csv" 60 "data/charts/sim_candle_$seed.html"

    # Generate volume chart
    python3 scripts/visualization/sim_volume.py "data/test_hmm_$seed.csv" 10000 "data/charts/sim_volume_$seed.html"
}

Write-Host "Done generating all charts."
