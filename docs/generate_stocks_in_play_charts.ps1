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
    # @{Ticker = "MOS"; Date = "2026-03-12"; VolumePerBar = 5000; Float = "316m"}
    # @{Ticker = "CF"; Date = "2026-03-12"; VolumePerBar = 5000; Float = "152m"}
    # @{Ticker = "ORCL"; Date = "2026-03-11"; VolumePerBar = 25000; Float = "1.71b"}
    # @{Ticker = "BNTX"; Date = "2026-03-10"; Float = "250m"; Short = 14.8}
    # @{Ticker = "BNTX"; Date = "2026-03-11"; Float = "250m"; Short = 15}
    # @{Ticker = "CRSP"; Date = "2026-03-10"; Float = "89m"; Short = 21.2}
    # @{Ticker = "NIO"; Date = "2026-03-10"; Float = "2.08b"; Short = 20.8}
    # @{Ticker = "NIO"; Date = "2026-03-11"; Float = "2.08b"; Short = 16.4}
    # @{Ticker = "HIMS"; Date = "2026-03-09"; Float = "207m"; Short = 14.1}
    # @{Ticker = "HIMS"; Date = "2026-03-10"; Float = "207m"; Short = 31.1}
    # @{Ticker = "USO"; Date = "2026-03-02"; Short = 13.0}
    # @{Ticker = "USO"; Date = "2026-03-03"; Short = 9.7}
    # @{Ticker = "USO"; Date = "2026-03-04"; Short = 10.3}
    # @{Ticker = "USO"; Date = "2026-03-05"; Short = 7.5}
    # @{Ticker = "USO"; Date = "2026-03-06"; Short = 13.7}
    # @{Ticker = "USO"; Date = "2026-03-09"; Short = 17.9}
    # @{Ticker = "MRVL"; Date = "2026-03-06"; Float = "869m"; Short = 11.9}
    # @{Ticker = "MRVL"; Date = "2026-03-09"; Float = "869m"; Short = 18.0}
    # @{Ticker = "TPET"; Date = "2026-03-02"; Float = "25m"; Short = 22.1}
    # @{Ticker = "TPET"; Date = "2026-03-03"; Float = "25m"; Short = 22.4}
    # @{Ticker = "TPET"; Date = "2026-03-04"; Float = "25m"; Short = 19.4}
    # @{Ticker = "TPET"; Date = "2026-03-05"; Float = "25m"; Short = 18.5}
    # @{Ticker = "TPET"; Date = "2026-03-06"; Float = "25m"; Short = 30.9}
    # @{Ticker = "TPET"; Date = "2026-03-09"; Float = "25m"; Short = 22.9}
    # @{Ticker = "IOT"; Date = "2026-03-06"; Float = "357m"; Short = 11.3}
    # @{Ticker = "IOT"; Date = "2026-03-09"; Float = "357m"; Short = 21.0}
    # @{Ticker = "IOT"; Date = "2026-03-10"; Float = "357m"; Short = 19.0}
    # @{Ticker = "TTD"; Date = "2026-03-05"; Float = "429m"; Short = 17.0}
    # @{Ticker = "TTD"; Date = "2026-03-06"; Float = "429m"; Short = 22.7}
    # @{Ticker = "VSCO"; Date = "2026-03-05"; Float = "69m"; Short = 19.2}
    # @{Ticker = "VSCO"; Date = "2026-03-06"; Float = "69m"; Short = 25.7}
    # @{Ticker = "BATL"; Date = "2026-02-27"; Float = "15.5m"; Short = 32.8}
    # @{Ticker = "BATL"; Date = "2026-03-02"; Float = "15.5m"; Short = 27.0}
    # @{Ticker = "BATL"; Date = "2026-03-03"; Float = "15.5m"; Short = 22.0}
    # @{Ticker = "BATL"; Date = "2026-03-04"; Float = "15.5m"; Short = 19.0}
    # @{Ticker = "BATL"; Date = "2026-03-05"; Float = "15.5m"; Short = 23.8}
    # @{Ticker = "BATL"; Date = "2026-03-06"; Float = "15.5m"; Short = 24.6}
    # @{Ticker = "BATL"; Date = "2026-03-09"; Float = "15.5m"; Short = 23.3}
    # @{Ticker = "BATL"; Date = "2026-03-10"; Float = "15.5m"; Short = 24.8}

    # @{Ticker = "MOBX"; Date = "2026-03-03"; Float = "86.2m"; Short = 30.0}
    # @{Ticker = "MOBX"; Date = "2026-03-04"; Float = "86.2m"; Short = 30.2}
    # @{Ticker = "MOBX"; Date = "2026-03-05"; Float = "86.2m"; Short = 30.2}
    # @{Ticker = "MOBX"; Date = "2026-03-06"; Float = "86.2m"; Short = 27.8}
    # @{Ticker = "UGRO"; Date = "2026-03-23"; Float = "671k"; Short = 19.3}
    # @{Ticker = "UGRO"; Date = "2026-03-24"; Float = "671k"; Short = 20.5}
    # @{Ticker = "UGRO"; Date = "2026-03-25"; Float = "671k"; Short = 19.6}
    # @{Ticker = "UGRO"; Date = "2026-03-26"; Float = "671k"; Short = 24.6}
    # @{Ticker = "ARM"; Date = "2026-03-25"; Float = "1.06b"; Short = 22.1}
    # @{Ticker = "DOCN"; Date = "2026-03-25"; Float = "67.5m"; Short = 29.9}
    # @{Ticker = "AGX"; Date = "2026-03-27"; Float = "13.3m"; Short = 20.8}
    # @{Ticker = "U"; Date = "2026-03-27"; Float = "348m"; Short = 7.4}
    # @{Ticker = "MDB"; Date = "2026-03-03"; Float = "77.8m"; Short = 11.9}
    # @{Ticker = "MDB"; Date = "2026-03-04"; Float = "77.8m"; Short = 24.8}
    # @{Ticker = "VG"; Date = "2026-03-03"; Float = "477.3m"; Short = 18.7}
    # @{Ticker = "VG"; Date = "2026-03-04"; Float = "477.3m"; Short = 16.6}
    # @{Ticker = "SE"; Date = "2026-03-03"; Float = "297.2m"; Short = 13.8}
    # @{Ticker = "SE"; Date = "2026-03-04"; Float = "297.2m"; Short = 13.8}
    # @{Ticker = "SE"; Date = "2026-03-05"; Float = "297.2m"; Short = 8.1}
    # @{Ticker = "NRG"; Date = "2026-03-03"; Float = "185.2m"; Short = 18.5}
    # @{Ticker = "NRG"; Date = "2026-03-04"; Float = "185.2m"; Short = 11.5}
    # @{Ticker = "ONON"; Date = "2026-03-03"; Float = "220.3m"; Short = 32.4}
    # @{Ticker = "ONON"; Date = "2026-03-04"; Float = "220.3m"; Short = 18.0}
    # @{Ticker = "AAOI"; Date = "2026-02-27"; Float = "70.9m"; Short = 15.4}
    # @{Ticker = "AAOI"; Date = "2026-03-02"; Float = "70.9m"; Short = 12.8}
    # @{Ticker = "AVAV"; Date = "2026-03-02"; Float = "70.9m"; Short = 19.5}
    # @{Ticker = "AVAV"; Date = "2026-03-03"; Float = "70.9m"; Short = 26.2}
    # @{Ticker = "AES"; Date = "2026-02-27"; Float = "708.5m"; Short = 12.0}
    # @{Ticker = "KTOS"; Date = "2026-03-02"; Float = "183.4m"; Short = 11.1}
    # @{Ticker = "KTOS"; Date = "2026-03-03"; Float = "183.4m"; Short = 18.0}
    # @{Ticker = "RCAT"; Date = "2026-03-02"; Float = "106.4m"; Short = 28.9}
    # @{Ticker = "RCAT"; Date = "2026-03-03"; Float = "106.4m"; Short = 16.3}
    # @{Ticker = "LASR"; Date = "2026-02-27"; Float = "51.65m"; Short = 27.0}
    # @{Ticker = "LASR"; Date = "2026-03-02"; Float = "51.65m"; Short = 18.3}
    # @{Ticker = "LASR"; Date = "2026-03-03"; Float = "51.65m"; Short = 23.8}
    # @{Ticker = "DUOL"; Date = "2026-02-27"; Float = "28.9m"; Short = 15.7}
    # @{Ticker = "DUOL"; Date = "2026-03-02"; Float = "28.9m"; Short = 14.0}
    # @{Ticker = "DELL"; Date = "2026-02-27"; Float = "296.5m"; Short = 13.7}
    # @{Ticker = "DELL"; Date = "2026-03-02"; Float = "296.5m"; Short = 15.9}
    # @{Ticker = "XYZ"; Date = "2026-02-27"; Float = "524.4m"; Short = 10.6}
    # @{Ticker = "XYZ"; Date = "2026-03-02"; Float = "524.4m"; Short = 22.9}
    # @{Ticker = "NFLX"; Date = "2026-02-27"; Float = "4.2b"; Short = 14.6}
    # @{Ticker = "NFLX"; Date = "2026-03-02"; Float = "4.2b"; Short = 19.8}
    # @{Ticker = "RUN"; Date = "2026-02-27"; Float = "226m"; Short = 17.9}
    # @{Ticker = "RUN"; Date = "2026-03-02"; Float = "226m"; Short = 28.5}
    # @{Ticker = "FLUT"; Date = "2026-02-27"; Float = "142.3m"; Short = 15.1}
    # @{Ticker = "FLUT"; Date = "2026-03-02"; Float = "142.3m"; Short = 15.9}
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
