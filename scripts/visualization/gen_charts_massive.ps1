param(
    [string[]]$Files = @('data/trades/LW/2025-12-19.json'),
    [int]$SecondsPerBar = 60,
    [int]$VolumePerBar = 10000,
    [double]$MarketOpen = 15.5,
    [double]$MarketClose = 22.0
)

foreach ($file in $Files) {
    $ticker = Split-Path (Split-Path $file -Parent) -Leaf
    $date = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $basename = "${ticker}_${date}"
    Write-Host "Generating charts for $basename..."

    # Generate tick chart
    python3 scripts/visualization/massive_tick.py $file "data/charts/massive_tick_$basename.html" $MarketOpen $MarketClose

    # Generate candle chart
    python3 scripts/visualization/massive_candle.py $file $SecondsPerBar "data/charts/massive_candle_$basename.html"

    # Generate volume chart
    python3 scripts/visualization/massive_volume.py $file $VolumePerBar "data/charts/massive_volume_$basename.html" $MarketOpen $MarketClose
}

Write-Host "Done generating all charts."
