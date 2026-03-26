$Files = @(
    @{Ticker = 'LW';   Date = "2025-12-19"; VolumePerBar = 10000; Float = "136.76m"}
    @{Ticker = 'NBIS'; Date = "2025-09-09"; VolumePerBar = 30000; Float = "202.8m"}
    @{Ticker = 'NBIS'; Date = "2025-09-10"; VolumePerBar = 30000; SecondsPerBar = 60; Float = "202.8m"}
    @{Ticker = 'MSTR'; Date = "2024-11-21"; VolumePerBar = 30000; Float = "313.41m"}
    @{Ticker = 'OPEN'; Date = "2025-09-11"; VolumePerBar = 300000; Float = "793m"}
    @{Ticker = "SMCI"; Date = "2026-03-20"; VolumePerBar = 60000; Float = "514m"}
    @{Ticker = "SMCI"; Date = "2026-03-23"; VolumePerBar = 60000; Float = "514m"}
    @{Ticker = "PL";   Date = "2026-03-20"; VolumePerBar = 20000; Float = "260m"}
    @{Ticker = "FDX";  Date = "2026-03-20"; VolumePerBar = 2000; Float = "218m"}
    @{Ticker = "ARM";  Date = "2026-03-20"; VolumePerBar = 4000; Float = "1.06b"}
    @{Ticker = "VG";   Date = "2026-03-19"; VolumePerBar = 33000; Float = "477m"}
    @{Ticker = "NBIS"; Date = "2026-03-16"; VolumePerBar = 20000; Float = "202m"}
    @{Ticker = "NBIS"; Date = "2026-03-17"; VolumePerBar = 20000; Float = "202m"}
    @{Ticker = "TME";  Date = "2026-03-17"; VolumePerBar = 20000; Float = "575m"}
    @{Ticker = "TME";  Date = "2026-03-18"; VolumePerBar = 20000; Float = "575m"}
    @{Ticker = "ULTA"; Date = "2026-03-13"; VolumePerBar = 1000; Float = "44.2m"}
    @{Ticker = "ULTA"; Date = "2026-03-16"; VolumePerBar = 1000; Float = "44.2m"}
    @{Ticker = "BYND"; Date = "2025-10-20"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    @{Ticker = "BYND"; Date = "2025-10-21"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    @{Ticker = "BYND"; Date = "2025-10-22"; VolumePerBar = 600000; SecondsPerBar = 60; Float = "438m"}
    @{Ticker = "MOS"; Date = "2026-03-12"; VolumePerBar = 5000; Float = "316m"}
    @{Ticker = "CF"; Date = "2026-03-12"; VolumePerBar = 5000; Float = "152m"}
    @{Ticker = "ORCL"; Date = "2026-03-11"; VolumePerBar = 25000; Float = "1.71b"}
    @{Ticker = "BNTX"; Date = "2026-03-10"; Float = "250m"; Short = 14.8}
    @{Ticker = "BNTX"; Date = "2026-03-11"; Float = "250m"; Short = 15}
    @{Ticker = "CRSP"; Date = "2026-03-10"; Float = "89m"; Short = 21.2}
    @{Ticker = "NIO"; Date = "2026-03-10"; Float = "2.08b"; Short = 20.8}
    @{Ticker = "NIO"; Date = "2026-03-11"; Float = "2.08b"; Short = 16.4}
    @{Ticker = "HIMS"; Date = "2026-03-09"; Float = "207m"; Short = 14.1}
    @{Ticker = "HIMS"; Date = "2026-03-10"; Float = "207m"; Short = 31.1}
    @{Ticker = "USO"; Date = "2026-03-02"; Short = 13.0}
    @{Ticker = "USO"; Date = "2026-03-03"; Short = 9.7}
    @{Ticker = "USO"; Date = "2026-03-04"; Short = 10.3}
    @{Ticker = "USO"; Date = "2026-03-05"; Short = 7.5}
    @{Ticker = "USO"; Date = "2026-03-06"; Short = 13.7}
    @{Ticker = "USO"; Date = "2026-03-09"; Short = 17.9}
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

function ReGenerate {
    param (
        [string]$Path,
        [scriptblock]$Action
    )
    & $Action $Path
}

foreach ($file in $Files) {
    $basename = "$($file.Ticker)_$($file.Date)"
    $jsonPath = "data/trades/$($file.Ticker)/$($file.Date).json"
    Generate -Path $jsonPath -Action {param ($outputPath)
        Write-Host "Downloading data for $basename..."
        dotnet run --project TradingEdge.Massive -- download-trades -t $file.Ticker -s $file.Date
    }
    Generate -Path "docs/charts/${basename}.html" -Action {param ($outputPath)
        Write-Host "Generating volume chart for $basename..."
        $pythonArgs = @($jsonPath, "-o", $outputPath)
        if ($file.VolumePerBar) { $pythonArgs += @("-v", $file.VolumePerBar) }
        if ($showExtended -eq "false") { $pythonArgs += "--no-extended-hours" }
        python3 scripts/visualization/massive_volume.py @pythonArgs
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

Write-Host "Done generating all charts. Moving on to reference templates..."

foreach ($file in $Files) {
    $basename = "$($file.Ticker)_$($file.Date)"
    $jsonPath = "data/trades/$($file.Ticker)/$($file.Date).json"
    Write-Host "## Ticker: $($file.Ticker) Date: $($file.Date)"
    Write-Host ""
    Write-Host "### Big Picture"
    Write-Host ""
    Write-Host "Market Momentum: "
    Write-Host ""
    Write-Host "### Intraday Fundamentals"
    Write-Host ""
    if ($file.Float) {
        python3 scripts/visualization/fetch_stock_fundamentals.py $file.Ticker $file.Date $file.Float
    } else {
        python3 scripts/visualization/fetch_stock_fundamentals.py $file.Ticker $file.Date
    }
    Write-Host "Short %: $($file.Short)"
    Write-Host "Catalyst: "
    Write-Host ""
    Write-Host "### Technical Analysis"
    Write-Host ""
    Write-Host "<div class=""chart-placeholder"" data-src=""charts/${basename}_daily.html"" style=""width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;"">Click to load chart</div>"
    if ($file.SecondsPerBar) {
        Write-Host "<div class=""chart-placeholder"" data-src=""charts/${basename}_intraday_candle.html"" style="width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;"">Click to load chart</div>"
    }
    Write-Host ""
    Write-Host "Overall Pattern: "
    Write-Host "Play: "
    Write-Host ""
    Write-Host "### Orderflow Analysis"
    Write-Host "<div class=""chart-placeholder"" data-src=""charts/${basename}.html"" style=""width:100%; height:600px; border:1px solid #ccc; display:flex; align-items:center; justify-content:center; cursor:pointer; background:#f5f5f5;"">Click to load chart</div>"
    Write-Host ""
    Write-Host "### News Summary"
    Write-Host ""
}
