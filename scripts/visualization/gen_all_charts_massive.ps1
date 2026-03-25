param(
    [string[]]$Files = @(
        'data/trades/LW/2025-12-19.json'
        'data/trades/QNCCF/2025-01-02.json'
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

    # Generate t-digest charts (use default naming)
    python3 scripts/visualization/massive_tdigest_volume.py $file "" $showExtended
    python3 scripts/visualization/massive_tdigest_time.py $file "" $showExtended
    python3 scripts/visualization/massive_tdigest_volume_duration.py $file $VolumePerBar "" $showExtended

    # Generate regular charts with market hours detection
    python3 scripts/visualization/massive_tick.py $file "" $showExtended
    python3 scripts/visualization/massive_candle.py $file $SecondsPerBar "" $showExtended
    $pythonArgs = @($file)
    if ($VolumePerBar) { $pythonArgs += @("-v", $VolumePerBar) }
    if ($showExtended -eq "false") { $pythonArgs += "--no-extended-hours" }
    python3 scripts/visualization/massive_volume.py @pythonArgs
}

Write-Host "Done generating all charts."
