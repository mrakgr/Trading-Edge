param(
    [int[]]$Seeds = @(42, 123, 456, 789)
)

foreach ($seed in $Seeds) {
    Write-Host "Generating all charts for seed $seed..."

    # Dump trades
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -o "data/test_hmm_$seed.csv"

    $csvFile = "data/test_hmm_$seed.csv"

    # Generate t-digest charts
    python3 scripts/visualization/sim_tdigest_volume.py $csvFile
    python3 scripts/visualization/sim_tdigest_time.py $csvFile
    python3 scripts/visualization/sim_tdigest_volume_duration.py $csvFile

    # Generate regular charts
    python3 scripts/visualization/sim_tick.py $csvFile "data/charts/sim_tick_$seed.html"
    python3 scripts/visualization/sim_candle.py $csvFile 60 "data/charts/sim_candle_$seed.html"
    python3 scripts/visualization/sim_volume.py $csvFile 10000 "data/charts/sim_volume_$seed.html"
}

Write-Host "Done generating all charts."
