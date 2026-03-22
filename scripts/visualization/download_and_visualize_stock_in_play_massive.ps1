$Files = @(
    @{
        Ticker = "SMCI"
        Date = "2026-03-20"
        VolumePerBar = 60000
    }
    @{
        Ticker = "PL"
        Date = "2026-03-20"
        VolumePerBar = 20000
    }
    @{
        Ticker = "FDX"
        Date = "2026-03-20"
        VolumePerBar = 2000
    }
    @{
        Ticker = "ARM"
        Date = "2026-03-20"
        VolumePerBar = 4000
    }
    @{
        Ticker = "VG"
        Date = "2026-03-19"
        VolumePerBar = 33000
    }
    @{
        Ticker = "NBIS"
        Date = "2026-03-16"
        VolumePerBar = 20000
    }
    @{
        Ticker = "NBIS"
        Date = "2026-03-17"
        VolumePerBar = 20000
    }
    @{
        Ticker = "TME"
        Date = "2026-03-17"
        VolumePerBar = 20000
    }
    @{
        Ticker = "TME"
        Date = "2026-03-18"
        VolumePerBar = 20000
    }
    @{
        Ticker = "ULTA"
        Date = "2026-03-13"
        VolumePerBar = 1000
    }
    @{
        Ticker = "ULTA"
        Date = "2026-03-16"
        VolumePerBar = 1000
    }
)

$showExtended = "true"

foreach ($file in $Files) {
    $basename = "$($file.Ticker)_$($file.Date)"
    $path = "data/trades/$($file.Ticker)/$($file.Date).json"
    if (-not (Test-Path $path)) {
        Write-Host "Downloading data for $basename..."
        dotnet run --project TradingEdge.Massive -- download-trades -t $file.Ticker -s $file.Date
        Write-Host "Generating all charts for $basename..."
        python3 scripts/visualization/massive_volume.py $path $file.VolumePerBar "" $showExtended
    }

}

Write-Host "Done generating all charts."
