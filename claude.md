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
