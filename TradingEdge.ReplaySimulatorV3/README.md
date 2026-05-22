# ReplaySimulatorV3

A tape-reading practice GUI for discretionary equity trading. Loads a day of Databento MBO data and replays the market with chart, L2 book, T&S tape, and a futures-style price ladder.

## Running

```
dotnet run --project TradingEdge.ReplaySimulatorV3                          # EOSE 2026-05-13 (default)
dotnet run --project TradingEdge.ReplaySimulatorV3 -- chart <SYM> <YYYY-MM-DD>
dotnet run --project TradingEdge.ReplaySimulatorV3 -- chart <day-dir>
```

Symbol/date pairs resolve to `data/databento/mbo/<SYM>/<YYYY-MM-DD>/`.

## Layout

- **Left:** chart (1-minute candles + volume) and the scrub slider.
- **Right (tabbed):**
  - **Ladder** (default) — futures-style DOM with Vol | Price | Bid | Bid T | Mid | Ask T | Ask. Bid = red, Ask = green (futures convention).
  - **L2** — per-venue book disaggregated by publisher, with the T&S tape below.

Only the visible tab is updated each frame; switching tabs re-renders against the cached snapshot.

## Controls

### Keyboard

| Key       | Action                            |
|-----------|-----------------------------------|
| `Space`   | Toggle play / pause               |
| `Left`    | Seek back 5 seconds               |
| `Right`   | Seek forward 5 seconds            |

Seek and play/pause flash a centered overlay (`<<` / `>>` / `▶` / `||`) for recorded-session readability.

### Mouse

| Gesture                          | Action                                                |
|----------------------------------|-------------------------------------------------------|
| Double-click chart               | Re-engage chart auto-follow                           |
| Double-click ladder              | Re-engage ladder auto-center                          |
| Mouse-wheel over ladder          | Detach auto-center and pan ±1 tick                    |
| Drag chart                       | Detach auto-follow and pan freely                     |
| Drag horizontal splitter         | Resize left (chart) vs. right (book/ladder) columns   |
| Drag vertical splitter in L2 tab | Resize L2 box vs. T&S tape                            |

### Toolbar

| Button            | Action                                                  |
|-------------------|---------------------------------------------------------|
| `Play` / `Pause`  | Same as `Space`                                         |
| `1×` … `100×`     | Replay speed multiplier                                 |
| `Resume Chart`    | Re-engage chart auto-follow (highlighted when detached) |
| `Recenter Ladder` | Re-engage ladder auto-center (highlighted when detached)|

Both Resume and Recenter highlight in cyan when their view has been detached from auto-tracking, and grey out when already engaged.

### Slider

Drag the scrub slider below the chart to seek to any time in the loaded day. The slider also tracks the current cursor during play.

## Data window

- Ladder per-side trade columns (Bid T / Mid / Ask T) show running totals at each price; they reset after 60 seconds of inactivity at that price. During those 60 seconds, the fade goes from 1 to 0.2 opacity after which it disappears at the 60s mark. The streaks last 1s and are intended for sensing overall trade activity as well as directional momentum, which would otherwise be difficult if the price ladder is set to the auto-center mode.
- Tape (T&S) shows the rolling 60-second trade window, newest first.
- Vol column on the ladder is the session-cumulative volume-at-price with a histogram bar normalized to the session maximum.
