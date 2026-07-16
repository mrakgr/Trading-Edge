# Claude Guidelines for TradingEdge

## 🛑 LOOKAHEAD — read `docs/lookahead_protocol.md` before touching any universe/filter/backtest

On 2026-07-16 a single universe filter (`avgvol20 * day_close >= $30M`) was found to be a **backdoor
"today is a 12×-volume day" selector** — it destroyed **three systems** (VwapReclaimV3 1.501→0.964,
OpeningDriverV2 4.112→0.728, DipRiderV4 2.876→1.158). It survived a year because it read as *plumbing*.

**The rules that would have caught it in an afternoon:**

1. **Any filter touching day D's data is a SIGNAL GATE, whatever it is named.** "Liquidity floor",
   "universe prune", "sanity check" — irrelevant. Audit it like an entry gate.
2. **`ROWS BETWEEN 19 PRECEDING AND CURRENT ROW` is a lookahead in any GATE** (it includes D's own
   volume). `mr_candidate` carries **`avgvol20_prior`** (`20 PRECEDING AND 1 PRECEDING`) — **gate on that**.
   Plain `avgvol20` is correct *only* as the rvol denominator.
3. **⭐ THE DISPROPORTION TEST — free, needs no backtest:** a liquidity filter cannot move PF more than
   roughly the fraction of the universe it changes. Ours changed **0.8%** of the universe and moved PF
   **−26%**. That is arithmetically impossible for a real floor. **If "plumbing" is load-bearing, it isn't
   plumbing.**
4. **Knowability clock:** for every field in a filter, write the earliest minute it is determined and
   compare it to `EntryStartMin`. `day_close` / `avgvol20` / `rvol_0945` / `close_fwd_*` = ❌ never.
   `med_bar_vol_0945` = ✅ only because `EntryStartMin = 09:45` — **aligned to the minute; lower the entry
   window and it silently becomes a lookahead.**
5. **Always run a control.** A genuine system is *indifferent* to removing a lookahead, or **improves**
   (MaxFlyerV3's $1 floor: 3.767 → **4.162**). Without a control you cannot tell "the system is fake" from
   "my audit is broken".
6. **Post-hoc SQL counts.** LowFlyer's production book lives entirely in `scripts/equity/*.sql` — the
   contaminated formula was *there*, not in the engine. Audit the `.sql`, not just the `.fs`.

**Status:** LowFlyer ✅ clean · MaxFlyerV3 ⚠️ unconfirmed (`brv20d` denominator fails) ·
VwapReclaimV3 / OpeningDriverV2 / DipRiderV4 ❌ dead. `docs/systems_showcase.md` quotes dead numbers.

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

- No special formatting constraints. The user manually approves each command, so heredocs / command substitution in commits are fine if convenient.

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

