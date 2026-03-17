# Market Hours Visualization

## Overview

The visualization scripts now automatically detect market hours from trade conditions instead of using hardcoded DST calculations.

## Implementation

### Market Hours Detection (`scripts/visualization/market_hours.py`)

- Detects opening/closing prints (conditions 8, 15, 16, 17, 19, 25, 28, 55)
- Falls back to condition 12 (extended hours) for OTC stocks
- Returns timestamps for market open/close boundaries

### Visual Zones

Charts now display three colored background zones:
- **Light blue**: Pre-market hours
- **Light green**: Regular trading hours (9:30 AM - 4:00 PM ET)
- **Light yellow**: Post-market hours

### Script Changes

All three visualization scripts updated:
- `massive_tick.py`: Tick-level trades
- `massive_candle.py`: Time-based candlesticks
- `massive_volume.py`: Volume-based bars

### Usage

```bash
# Show all hours with background zones
python3 scripts/visualization/massive_tick.py data/trades/MSTR/2024-11-21.json "" true

# Show regular hours only (no extended hours)
python3 scripts/visualization/massive_tick.py data/trades/MSTR/2024-11-21.json "" false
```

PowerShell script:
```powershell
# Show extended hours
pwsh scripts/visualization/gen_charts_massive.ps1 -ShowExtendedHours

# Regular hours only (default)
pwsh scripts/visualization/gen_charts_massive.ps1
```

## Stock Support

- **Exchange-traded stocks** (MSTR, PLTR, etc.): Full support with opening/closing prints
- **OTC stocks** (QNCCF, etc.): Fallback using condition 12, may not distinguish pre/post market
