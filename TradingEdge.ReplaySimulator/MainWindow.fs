module TradingEdge.ReplaySimulator.MainWindow

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Layout
open TradingEdge.ReplaySimulator.Bars

let private bgBrush =
    SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private textBrush =
    SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))

/// Compose the chart inside a window, with a title bar showing the loaded day.
let create (symbol: string) (date: string) (bars: Bar list) : Window =
    let w = Window()
    w.Title <- sprintf "ReplaySimulator — %s %s" symbol date
    w.Width <- 1400.0
    w.Height <- 800.0
    w.Background <- bgBrush

    let header = TextBlock()
    header.Text <-
        let firstNs =
            if bars.IsEmpty then 0L
            else bars |> List.head |> (fun b -> b.BucketStartNs)
        let lastNs =
            if bars.IsEmpty then 0L
            else bars |> List.last |> (fun b -> b.BucketStartNs)
        let totalVol = bars |> List.sumBy (fun b -> b.Volume)
        let totalTrades = bars |> List.sumBy (fun b -> b.TradeCount)
        sprintf "%s — %s | %d bars | %d trades | %s shares"
            symbol date bars.Length totalTrades (totalVol.ToString("N0"))
    header.Foreground <- textBrush
    header.FontSize <- 13.0
    header.Margin <- Thickness(10.0, 6.0, 10.0, 4.0)

    let chart = ChartView.build bars

    let grid = Grid()
    grid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    grid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(header, 0)
    Grid.SetRow(chart, 1)
    grid.Children.Add(header)
    grid.Children.Add(chart)

    w.Content <- grid
    w
