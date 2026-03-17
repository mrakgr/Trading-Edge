$Files = 
    @(
        @{
            Path = 'data/trades/LW/2025-12-19.json'
            VolumePerBar = 10000
        }
        @{
            Path = 'data/trades/OPEN/2025-09-11.json'
            VolumePerBar = 300000
        }
        @{
            Path = 'data/trades/NBIS/2025-09-09.json'
            VolumePerBar = 30000
        }
        @{
            Path = 'data/trades/NBIS/2025-09-10.json'
            VolumePerBar = 30000
        }
        @{
            Path = 'data/trades/MSTR/2024-11-21.json'
            VolumePerBar = 30000
        }
    )

foreach ($file in $Files) {
    $ticker = Split-Path (Split-Path $file.Path -Parent) -Leaf
    $date = [System.IO.Path]::GetFileNameWithoutExtension($file.Path)
    $basename = "${ticker}_${date}"

    Write-Host "Generating volume chart for $basename..."
    python3 scripts/visualization/massive_volume.py $file.Path $file.VolumePerBar "docs/charts/$basename.html" $true
}

Write-Host "Done generating all charts."
