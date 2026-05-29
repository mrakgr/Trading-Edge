# Claude Guidelines for TradingEdge

## F# Async Patterns

When writing concurrent F# code in this project:

1. **Always use `task {}` computation expressions** for async operations
2. **Use `ReadAllAsync()` for channel reading** - iterate with `for item in channel.Reader.ReadAllAsync() do`. **Do not** hand-roll a `WaitToReadAsync` + `while TryRead` drain loop just to consume messages one-by-one — that is the same shape with more ceremony and more places to get cancellation wrong. The only valid reason to drain manually is when you genuinely need to batch every queued message at one instant (e.g. collapse multiple Tick messages into a single fold). If you're not batching, use `ReadAllAsync`.
3. **Avoid `.Wait()` and `.Result`** inside async code - use `do!` and `let!` instead
4. **FSharp.Control.TaskSeq** is available for `IAsyncEnumerable` support in `task {}` blocks
5. **`open FSharp.Control`** is required to use `for ... in` with `IAsyncEnumerable` inside `task {}` - without it you get a type mismatch error about `IAsyncEnumerable` not being compatible with `seq`

## Concurrency Primitives — Prefer Channels

**Always reach for `System.Threading.Channels` first** when coordinating concurrent work. Avoid `SemaphoreSlim`, `lock`, `Mutex`, `Monitor`, `Interlocked`, `ConcurrentQueue`, and other low-level primitives unless a channel-based design genuinely doesn't fit.

Why:
- A pipeline of `task { }` workers connected by `Channel.CreateUnbounded<T>()` is easier to reason about, easier to tune (parallelism per stage), and avoids the deadlock classes that bite mixed `async`/sync code (e.g. I/O completion thread starvation when a synchronous DuckDB call lands inside an `async` continuation).
- Channels let one stage absorb tempo mismatches: e.g. fast downloads + slow conversions need a buffer between them; with an unbounded channel the downloader never blocks waiting for the converter.
- Ownership becomes implicit: the reader of a channel is the sole mutator of whatever state it derives from the messages. No locks, no `Interlocked`.

The `TradingEdge.CryptoData/PerpsDownload.fs` two-stage pipeline (manifest → download workers → unbounded channel → convert workers → reporter) is the canonical example in this codebase. Prefer mirroring that shape over wiring `Async.Parallel` + `SemaphoreSlim`.

## Command-Line Argument Parsing

- **Use Argu, not manual parsing**, for any F# script or program that takes CLI args. Define an `IArgParserTemplate` discriminated union, parse via `ArgumentParser.Create<_>().Parse(...)`, and pull values with `GetResult` / `TryGetResult`. Manual `Array.skip`/positional matching is hard to read and easy to get wrong.
- In `.fsx` scripts, add `#r "nuget: Argu, 6.2.5"` and get script args via `fsi.CommandLineArgs |> Array.skip 1`. When invoking a script as a subprocess with `dotnet fsi script.fsx`, pass a literal `--` between the script path and the script's own flags so dotnet doesn't intercept them.

## MathNet.Numerics Usage

- **Prefer MathNet distributions over manual calculations** - use `Normal.PDFLn`, `Exponential.Sample`, `Categorical.Sample`, etc. from `MathNet.Numerics.Distributions` instead of hand-rolling equivalent math.

## Visualization

- **PowerShell scripts in `scripts/visualization/`** are used for chart generation
- Run `pwsh scripts/visualization/gen_all_charts_massive.ps1` to regenerate per-day charts (tick, time-bar VWAP, volume-bar VWAP)
- **Always inject `scripts/visualization/chart_controls.js` as the plotly `post_script`** when writing any new chart script. It provides middle-click pan/zoom toggle and `a`/`s`/`d` dragmode shortcuts that the user relies on. See `massive_timebar.py` for the pattern (`fig.write_html(..., post_script=post_script)`).

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

