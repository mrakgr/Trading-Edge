param(
    [int[]]$Seeds = @(42, 123, 456, 789, 1001, 2048, 3141, 9999)
)

foreach ($seed in $Seeds) {
    Write-Host "Generating charts for seed $seed..."

    $csvFile = "data/test_combinator_atm_$seed.csv"

    # Dump trades
    dotnet run --project TradingEdge.Simulation -- dump-trades -s $seed -o $csvFile

    # Generate tick chart (note that it's subsampled)
    python3 scripts/visualization/sim_tick.py $csvFile

    # Generate candle chart
    python3 scripts/visualization/sim_candle.py $csvFile 60

    # Generate volume chart
    python3 scripts/visualization/sim_volume.py $csvFile 10000
}

Write-Host "Done generating all charts."
