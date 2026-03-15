param(
    [string[]]$Files = @(
        # 'data/trades/LW/2025-12-19.json'
        # 'data/trades/NBIS/2025-09-09.json'
        # 'data/trades/NBIS/2025-09-10.json'
        # 'data/trades/OPEN/2025-08-22.json'
        # 'data/trades/OPEN/2025-09-11.json'
        'data/trades/MSTR/2025-12-01.json'
        ),
    [int]$SecondsPerBar = 60,
    [int]$VolumePerBar = 10000
)

foreach ($file in $Files) {
    $ticker = Split-Path (Split-Path $file -Parent) -Leaf
    $date = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $basename = "${ticker}_${date}"

    # Determine market hours based on DST
    # US DST: second Sunday in March to first Sunday in November
    $tradeDate = [DateTime]::ParseExact($date, 'yyyy-MM-dd', $null)
    $year = $tradeDate.Year

    # Find second Sunday in March
    $marchStart = Get-Date -Year $year -Month 3 -Day 1
    $dstStart = $marchStart.AddDays((7 - [int]$marchStart.DayOfWeek) % 7 + 7)

    # Find first Sunday in November
    $novStart = Get-Date -Year $year -Month 11 -Day 1
    $dstEnd = $novStart.AddDays((7 - [int]$novStart.DayOfWeek) % 7)

    if ($tradeDate -ge $dstStart -and $tradeDate -lt $dstEnd) {
        # DST active: ET = UTC-4, so 9:30 AM - 4:00 PM ET = 13:30 - 20:00 UTC
        $marketOpen = 13.5
        $marketClose = 20.0
    } else {
        # Standard time: ET = UTC-5, so 9:30 AM - 4:00 PM ET = 14:30 - 21:00 UTC
        $marketOpen = 14.5
        $marketClose = 21.0
    }

    Write-Host "Generating all charts for $basename (market hours: ${marketOpen}:00-${marketClose}:00 UTC)..."

    # Generate charts with market hours (empty string for default output path)
    # python3 scripts/visualization/massive_tick.py $file "" $marketOpen $marketClose
    python3 scripts/visualization/massive_candle.py $file $SecondsPerBar "" $marketOpen $marketClose
    python3 scripts/visualization/massive_volume.py $file $VolumePerBar "" $marketOpen $marketClose
}

Write-Host "Done generating all charts."
