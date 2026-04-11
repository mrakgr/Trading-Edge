$Files = @(
    @{Ticker = 'LW';   Date = "2025-12-19"; VolumePerBar = 10000}
    @{Ticker = 'OPEN'; Date = "2025-09-11"; VolumePerBar = 300000}
    @{Ticker = 'NBIS'; Date = "2025-09-09"; VolumePerBar = 30000}
    @{Ticker = 'NBIS'; Date = "2025-09-10"; VolumePerBar = 30000; SecondsPerBar = 60}
    @{Ticker = 'MSTR'; Date = "2024-11-21"; VolumePerBar = 30000}
    @{Ticker = "SMCI"; Date = "2026-03-20"; VolumePerBar = 60000}
    @{Ticker = "PL";   Date = "2026-03-20"; VolumePerBar = 20000}
    @{Ticker = "FDX";  Date = "2026-03-20"; VolumePerBar = 2000}
    @{Ticker = "ARM";  Date = "2026-03-20"; VolumePerBar = 4000}
    @{Ticker = "VG";   Date = "2026-03-19"; VolumePerBar = 33000}
    @{Ticker = "NBIS"; Date = "2026-03-16"; VolumePerBar = 20000}
    @{Ticker = "NBIS"; Date = "2026-03-17"; VolumePerBar = 20000}
    @{Ticker = "TME";  Date = "2026-03-17"; VolumePerBar = 20000}
    @{Ticker = "TME";  Date = "2026-03-18"; VolumePerBar = 20000}
    @{Ticker = "ULTA"; Date = "2026-03-13"; VolumePerBar = 1000}
    @{Ticker = "ULTA"; Date = "2026-03-16"; VolumePerBar = 1000}
)

$showExtended = "true"

foreach ($file in $Files) {
    $basename = "$($file.Ticker)_$($file.Date)"
    $path = "data/trades/$($file.Ticker)/$($file.Date).parquet"
    if (-not (Test-Path $path)) {
        Write-Host "Downloading data for $basename..."
        dotnet run --project TradingEdge.Massive -- download-trades -t $file.Ticker -s $file.Date
        Write-Host "Generating volume chart for $basename..."
        $pythonArgs = @($path)
        if ($file.VolumePerBar) { $pythonArgs += @("-v", $file.VolumePerBar) }
        if ($showExtended -eq "false") { $pythonArgs += "--no-extended-hours" }
        python3 scripts/visualization/massive_volume.py @pythonArgs
        if ($file.SecondsPerBar) {
            Write-Host "Generating intraday candlestick chart for $basename..."
            python3 scripts/visualization/massive_candle.py $file.Path $file.SecondsPerBar "" $showExtended
        }
    }
}

Write-Host "Done generating all charts."
