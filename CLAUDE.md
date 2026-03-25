# Claude Guidelines for TradingEdge

## F# Async Patterns

When writing concurrent F# code in this project:

1. **Always use `task {}` computation expressions** for async operations
2. **Use `ReadAllAsync()` for channel reading** - iterate with `for item in channel.Reader.ReadAllAsync() do`
3. **Avoid `.Wait()` and `.Result`** inside async code - use `do!` and `let!` instead
4. **FSharp.Control.TaskSeq** is available for `IAsyncEnumerable` support in `task {}` blocks
5. **`open FSharp.Control`** is required to use `for ... in` with `IAsyncEnumerable` inside `task {}` - without it you get a type mismatch error about `IAsyncEnumerable` not being compatible with `seq`

## MathNet.Numerics Usage

- **Prefer MathNet distributions over manual calculations** - use `Normal.PDFLn`, `Exponential.Sample`, `Categorical.Sample`, etc. from `MathNet.Numerics.Distributions` instead of hand-rolling equivalent math.

## Visualization

- **PowerShell scripts in `scripts/visualization/`** are used for chart generation
- Run `pwsh scripts/visualization/gen_all_charts_sim.ps1` to regenerate simulation charts

## Git Commits

- **Do not use command substitutions (heredocs)** when creating git commits
- Use simple `-m` flags instead: `git commit -m "Title" -m "Body"`

## News Search Strategy

### Multi-Source Approach

The Polygon news API (used by `download-news`) provides baseline coverage but has significant gaps, especially for:
- Short-seller research reports (Citron, Muddy Waters, Hindenburg)
- Intraday catalyst events
- Real-time market-moving announcements

### Comprehensive News Gathering

For any ticker/date requiring news analysis:

1. **Primary: Polygon API** (automated baseline)
   - Run: `dotnet run --project TradingEdge.Massive -- download-news -t TICKER -e DATE`
   - Provides structured data from major outlets
   - Often incomplete for critical events

2. **Supplementary: Google News with Date Range** (comprehensive coverage)
   - Use WebSearch with: `TICKER after:YYYY-MM-DD before:YYYY-MM-DD`
   - Example: `MSTR after:2024-11-20 before:2024-11-22`
   - Captures all news sources including those Polygon misses
   - More reliable than Twitter (7-day limit) or catalyst-specific searches

### Implementation Pattern

```bash
# 1. Download baseline from Polygon API
dotnet run --project TradingEdge.Massive -- download-news -t MSTR -e 2024-11-21

# 2. Supplement with Google News date range search
# Use WebSearch: "MSTR after:2024-11-20 before:2024-11-22"
# Summarize the comprehensive results

# 3. Create news summary from combined sources
```

### Best Practices

- **Always use Google News date range search** for important trading days
- **Don't rely on catalyst labels** - they're estimates, not definitive
- **Use generic date range searches** rather than catalyst-specific queries
- **Combine both sources** - Polygon for structure, Google for completeness

