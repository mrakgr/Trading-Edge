#r "nuget: FSharp.SystemTextJson, 1.4.36"
#r "nuget: Argu, 6.2.5"

// Side-by-side comparison of two backtest JSONs (e.g. baseline vs p80_t30).
// Prints one table with both columns and a delta row, so we can read whether
// the gated config adds edge at a glance.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Argu

type CliArgs =
    | [<Mandatory; AltCommandLine("-b")>] Baseline of string
    | [<Mandatory; AltCommandLine("-g")>] Gated of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Baseline _ -> "Baseline JSON (e.g. minizinc/backtest_baseline.json)."
            | Gated _ -> "Gated JSON (e.g. minizinc/backtest_p80_t30.json)."

let parser = ArgumentParser.Create<CliArgs>(programName = "report_backtest.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

type Summary = {
    [<JsonPropertyName "config">] Config: string
    [<JsonPropertyName "days_ran">] DaysRan: int
    [<JsonPropertyName "round_trips">] RoundTrips: int
    [<JsonPropertyName "net_pnl">] NetPnL: double
    [<JsonPropertyName "commission">] Commission: double
    [<JsonPropertyName "gross_wins">] GrossWins: double
    [<JsonPropertyName "gross_losses">] GrossLosses: double
    [<JsonPropertyName "profit_factor">] ProfitFactor: double
    [<JsonPropertyName "win_rate">] WinRate: double
    [<JsonPropertyName "avg_win">] AvgWin: double
    [<JsonPropertyName "avg_loss">] AvgLoss: double
    [<JsonPropertyName "max_drawdown">] MaxDrawdown: double
    [<JsonPropertyName "sharpe_daily">] SharpeDaily: double
}

let load path =
    let bytes = File.ReadAllBytes path
    JsonSerializer.Deserialize<Summary>(bytes, JsonSerializerOptions())

let b = load (parsed.GetResult Baseline)
let g = load (parsed.GetResult Gated)

let fmt label fmtStr (vb: double) (vg: double) =
    let formatted v = sprintf fmtStr v
    let delta = vg - vb
    let deltaStr = sprintf fmtStr delta
    let pctDelta = if vb <> 0.0 then sprintf "%+.1f%%" (100.0 * delta / abs vb) else "--"
    printfn "  %-18s %14s  %14s  %14s  %8s" label (formatted vb) (formatted vg) deltaStr pctDelta

printfn ""
printfn "=== Backtest comparison: %s (baseline) vs %s (gated) ===" b.Config g.Config
printfn "  %-18s %14s  %14s  %14s  %8s" "metric" "baseline" "gated" "delta" "pct"
printfn "  %s" (String.replicate 80 "-")
printfn "  %-18s %14d  %14d  %14d  %8s" "days_ran" b.DaysRan g.DaysRan (g.DaysRan - b.DaysRan)
    (if b.DaysRan <> 0 then sprintf "%+.1f%%" (100.0 * float (g.DaysRan - b.DaysRan) / float b.DaysRan) else "--")
printfn "  %-18s %14d  %14d  %14d  %8s" "round_trips" b.RoundTrips g.RoundTrips (g.RoundTrips - b.RoundTrips)
    (if b.RoundTrips <> 0 then sprintf "%+.1f%%" (100.0 * float (g.RoundTrips - b.RoundTrips) / float b.RoundTrips) else "--")
fmt "net_pnl"       "$%13.2f" b.NetPnL g.NetPnL
fmt "commission"    "$%13.2f" b.Commission g.Commission
fmt "gross_wins"    "$%13.2f" b.GrossWins g.GrossWins
fmt "gross_losses"  "$%13.2f" b.GrossLosses g.GrossLosses
fmt "profit_factor" "%14.3f" b.ProfitFactor g.ProfitFactor
fmt "win_rate"      "%13.2f%%" (100.0 * b.WinRate) (100.0 * g.WinRate)
fmt "avg_win"       "$%13.2f" b.AvgWin g.AvgWin
fmt "avg_loss"      "$%13.2f" b.AvgLoss g.AvgLoss
fmt "max_drawdown"  "$%13.2f" b.MaxDrawdown g.MaxDrawdown
fmt "sharpe_daily"  "%14.2f" b.SharpeDaily g.SharpeDaily

printfn ""
let verdict =
    if g.NetPnL > b.NetPnL && g.ProfitFactor > b.ProfitFactor then "Thresholds ADD edge."
    elif g.NetPnL > b.NetPnL then "Thresholds help on net PnL but not on PF."
    elif g.ProfitFactor > b.ProfitFactor && g.RoundTrips < b.RoundTrips then
        "Thresholds tighten the filter (higher PF, lower trade count) but reduce net PnL."
    else "Thresholds do NOT add edge. Do not proceed."
printfn "%s" verdict

printfn ""
printfn "NOTE: This is in-sample — calibration and backtest both used the same ~262 days."
printfn "      A proper OOS split comes after the full download completes."
