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
    Write-Host "Generating all charts for $basename..."

    # Generate regular charts (use default naming)
    python3 scripts/visualization/massive_tick.py $file
    python3 scripts/visualization/massive_candle.py $file $SecondsPerBar
    python3 scripts/visualization/massive_volume.py $file $VolumePerBar
}

Write-Host "Done generating all charts."
