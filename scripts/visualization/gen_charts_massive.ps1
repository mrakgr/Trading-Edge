param(
    [string[]]$Files = @(
        'data/trades/LW/2025-12-19.json'
        'data/trades/QNCCF/2025-01-02.json'
        # 'data/trades/NBIS/2025-09-09.json'
        # 'data/trades/NBIS/2025-09-10.json'
        # 'data/trades/OPEN/2025-08-22.json'
        # 'data/trades/OPEN/2025-09-11.json'
        # 'data/trades/MSTR/2025-12-01.json'
        # 'data/trades/ZJYL/2023-12-18.json'
        # 'data/trades/LMT/2025-03-21.json'
        # 'data/trades/PLTR/2025-02-20.json'
        # 'data/trades/FTAI/2025-01-15.json'
        # 'data/trades/MSTR/2024-11-21.json'
        ),
    [int]$SecondsPerBar = 60,
    [int]$VolumePerBar = 30000,
    [switch]$ShowExtendedHours
)

foreach ($file in $Files) {
    $ticker = Split-Path (Split-Path $file -Parent) -Leaf
    $date = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $basename = "${ticker}_${date}"

    Write-Host "Generating all charts for $basename..."

    $showExtended = if ($ShowExtendedHours) { "true" } else { "false" }

    # Generate charts with market hours detection from trade conditions
    python3 scripts/visualization/massive_tick.py $file "" $showExtended
    python3 scripts/visualization/massive_candle.py $file $SecondsPerBar "" $showExtended
    python3 scripts/visualization/massive_volume.py $file $VolumePerBar "" $showExtended
}

Write-Host "Done generating all charts."
