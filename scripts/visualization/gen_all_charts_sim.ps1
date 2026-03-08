param(
    [int[]]$Seeds = @(42, 123, 456, 789)
)

foreach ($seed in $Seeds) {
    Write-Host "Generating all charts for seed $seed..."
    $csvFile = "data/test_nested_atm_$seed.csv"
    
    # Dump trades
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -d "data/tdigests/LW_2025-12-19.tdigest" -o $csvFile

    # Generate t-digest charts (use default naming)
    python3 scripts/visualization/sim_tdigest_volume.py $csvFile
    python3 scripts/visualization/sim_tdigest_time.py $csvFile
    python3 scripts/visualization/sim_tdigest_volume_duration.py $csvFile

    # Generate regular charts (use default naming)
    python3 scripts/visualization/sim_tick.py $csvFile
    python3 scripts/visualization/sim_candle.py $csvFile 60
    python3 scripts/visualization/sim_volume.py $csvFile 10000
}

Write-Host "Done generating all charts."
