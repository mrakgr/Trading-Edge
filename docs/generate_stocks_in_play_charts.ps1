$Files = @(
    # @{Ticker = 'LW';   Date = "2025-12-19"; VolumePerBar = 10000; Float = "136.76m"}
    # @{Ticker = 'NBIS'; Date = "2025-09-09"; VolumePerBar = 30000; Float = "202.8m"}
    # @{Ticker = 'NBIS'; Date = "2025-09-10"; VolumePerBar = 30000; SecondsPerBar = 60; Float = "202.8m"}
    # @{Ticker = 'MSTR'; Date = "2024-11-21"; VolumePerBar = 30000; Float = "313.41m"}
    # @{Ticker = 'OPEN'; Date = "2025-09-11"; VolumePerBar = 300000; Float = "793m"}
    # @{Ticker = "SMCI"; Date = "2026-03-20"; VolumePerBar = 60000; Float = "514m"}
    # @{Ticker = "SMCI"; Date = "2026-03-23"; VolumePerBar = 60000; Float = "514m"}
    # @{Ticker = "PL";   Date = "2026-03-20"; VolumePerBar = 20000; Float = "260m"}
    # @{Ticker = "FDX";  Date = "2026-03-20"; VolumePerBar = 2000; Float = "218m"}
    # @{Ticker = "ARM";  Date = "2026-03-20"; VolumePerBar = 4000; Float = "1.06b"}
    # @{Ticker = "VG";   Date = "2026-03-19"; VolumePerBar = 33000; Float = "477m"}
    # @{Ticker = "NBIS"; Date = "2026-03-16"; VolumePerBar = 20000; Float = "202m"}
    # @{Ticker = "NBIS"; Date = "2026-03-17"; VolumePerBar = 20000; Float = "202m"}
    # @{Ticker = "TME";  Date = "2026-03-17"; VolumePerBar = 20000; Float = "575m"}
    # @{Ticker = "TME";  Date = "2026-03-18"; VolumePerBar = 20000; Float = "575m"}
    # @{Ticker = "ULTA"; Date = "2026-03-13"; VolumePerBar = 1000; Float = "44.2m"}
    # @{Ticker = "ULTA"; Date = "2026-03-16"; VolumePerBar = 1000; Float = "44.2m"}
    # @{Ticker = "BYND"; Date = "2025-10-20"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    # @{Ticker = "BYND"; Date = "2025-10-21"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    # @{Ticker = "BYND"; Date = "2025-10-22"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    @{Ticker = "MOS"; Date = "2026-03-12"; VolumePerBar = 5000; Float = "316m"}
)

$showExtended = "true"

function Generate {
    param (
        [string]$Path,
        [scriptblock]$Action
    )
    if (-not (Test-Path $Path)) {
        & $Action $Path
    }
}

foreach ($file in $Files) {
    $basename = "$($file.Ticker)_$($file.Date)"
    $jsonPath = "data/trades/$($file.Ticker)/$($file.Date).json"
    Generate -Path $jsonPath -Action {param ($outputPath)
        Write-Host "Downloading data for $basename..."
        dotnet run --project TradingEdge.Massive -- download-trades -t $file.Ticker -s $file.Date
    }
    if ($file.Float) {
        Write-Host "## Ticker: $($file.Ticker) Date: $($file.Date)"
        Write-Host ""
        Write-Host "### Big Picture"
        Write-Host ""
        Write-Host "Market Momentum: "
        Write-Host ""
        Write-Host "### Intraday Fundamentals"
        Write-Host ""
        python3 scripts/visualization/fetch_stock_fundamentals.py $file.Ticker $file.Date $file.Float
        Write-Host "Short %: "
        Write-Host "Catalyst: "
        Write-Host ""
        Write-Host "### Technical Analysis"
        Write-Host ""
        Write-Host "<iframe src=""charts/$($file.Ticker)_$($file.Date)_daily.html"" width=""100%"" height=""100%"" style=""border: 1px solid #ccc;""></iframe>"
        if ($file.SecondsPerBar) {
            Write-Host "<iframe src=""charts/$($file.Ticker)_$($file.Date)_intraday_candle.html"" width=""100%"" height=""100%"" style=""border: 1px solid #ccc;""></iframe>"
        }
        Write-Host ""
        Write-Host "Overall Pattern: "
        Write-Host "Play: "
        Write-Host ""
        Write-Host "### Orderflow Analysis"
        Write-Host "<iframe src=""charts/$($file.Ticker)_$($file.Date).html"" width=""100%"" height=""100%"" style=""border: 1px solid #ccc;""></iframe>"
        Write-Host ""
        Write-Host "### News Summary"
        Write-Host ""
    }
    Generate -Path "docs/charts/${basename}.html" -Action {param ($outputPath) 
        Write-Host "Generating volume chart for $basename..."
        python3 scripts/visualization/massive_volume.py $jsonPath $file.VolumePerBar $outputPath $showExtended
    }
    Generate -Path "docs/charts/${basename}_daily.html" -Action {param ($outputPath) 
        Write-Host "Generating daily chart for $basename..."
        python3 scripts/visualization/daily_chart.py $file.Ticker $file.Date $outputPath
    }
    if ($file.SecondsPerBar) {
        Generate -Path "docs/charts/${basename}_intraday_candle.html" -Action {param ($outputPath) 
            Write-Host "Generating intraday candlestick chart for $basename..."
            python3 scripts/visualization/massive_candle.py $jsonPath $file.SecondsPerBar $outputPath $showExtended
        }
    }
}

Write-Host "Done generating all charts."
