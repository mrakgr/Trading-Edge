module TradingEdge.ReplaySimulator.ChartView

// Builds the LiveCharts2 CartesianChart for milestone 1:
//   * single pane (candles + VWAP line; volume bars drawn faintly in the background)
//   * dark theme
//   * America/New_York time axis labels
//   * full extended session range visible
//
// Static load only — we hand it a finished Bar list and it renders once.

open System
open System.Collections.Generic
open Avalonia.Media
open LiveChartsCore
open LiveChartsCore.Kernel.Sketches
open LiveChartsCore.SkiaSharpView
open LiveChartsCore.SkiaSharpView.Avalonia
open LiveChartsCore.SkiaSharpView.Painting
open LiveChartsCore.Defaults
open SkiaSharp
open TradingEdge.ReplaySimulator.Bars

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private toNyTime (utcNs: int64) : DateTime =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)

// Dark-theme palette
let private bgColor      = SKColor(0x10uy, 0x12uy, 0x18uy)
let private gridColor    = SKColor(0x2auy, 0x2euy, 0x38uy)
let private textColor    = SKColor(0xc8uy, 0xccuy, 0xd6uy)
let private upColor      = SKColor(0x2euy, 0xb8uy, 0x88uy)   // green
let private downColor    = SKColor(0xe5uy, 0x4buy, 0x4buy)   // red
let private vwapColor    = SKColor(0xf2uy, 0xc5uy, 0x5cuy)   // amber
let private volColor     = SKColor(0x4auy, 0x55uy, 0x68uy, 0x60uy) // translucent slate

let private mkPaint (c: SKColor) = SolidColorPaint(c)
let private mkStrokePaint (c: SKColor) (w: float32) =
    let p = SolidColorPaint(c)
    p.StrokeThickness <- w
    p

/// Build the CartesianChart control from a list of bars.
let build (bars: Bar list) : CartesianChart =
    let chart = CartesianChart()
    chart.Background <- SolidColorBrush(Color.FromArgb(0xFFuy, bgColor.Red, bgColor.Green, bgColor.Blue))
    chart.AnimationsSpeed <- TimeSpan.Zero  // snappier for static load

    // ---------- candles ----------
    let candleValues =
        let arr = ResizeArray<FinancialPoint>()
        for b in bars do
            let t = toNyTime b.BucketStartNs
            arr.Add(FinancialPoint(t, b.High, b.Open, b.Close, b.Low))
        arr
    let candleSeries = CandlesticksSeries<FinancialPoint>()
    candleSeries.Values <- candleValues
    candleSeries.UpFill <- mkPaint upColor
    candleSeries.DownFill <- mkPaint downColor
    candleSeries.UpStroke <- mkStrokePaint upColor 1.0f
    candleSeries.DownStroke <- mkStrokePaint downColor 1.0f
    candleSeries.Name <- "OHLC"
    // Default tooltip formatter uses arrow/dot glyphs the bundled Skia font can't
    // render — override with plain ASCII single-line OHLC.
    candleSeries.YToolTipLabelFormatter <- fun (cp: LiveChartsCore.Kernel.ChartPoint<FinancialPoint, _, _>) ->
        let fp = cp.Model
        sprintf "O %.4f  H %.4f  L %.4f  C %.4f" fp.Open fp.High fp.Low fp.Close

    // ---------- VWAP line (overlaid in price pane) ----------
    let vwapPoints =
        let arr = ResizeArray<DateTimePoint>()
        for b in bars do
            let v =
                match b.SessionVwap with
                | Some x -> Nullable<double>(x)
                | None -> Nullable<double>()
            arr.Add(DateTimePoint(toNyTime b.BucketStartNs, v))
        arr
    let vwapSeries = LineSeries<DateTimePoint>()
    vwapSeries.Values <- vwapPoints
    vwapSeries.Stroke <- mkStrokePaint vwapColor 1.5f
    vwapSeries.Fill <- null
    vwapSeries.GeometrySize <- 0.0
    vwapSeries.Name <- "VWAP"

    // ---------- volume bars in the background (secondary Y axis, faint) ----------
    let volPoints =
        let arr = ResizeArray<DateTimePoint>()
        for b in bars do
            arr.Add(DateTimePoint(toNyTime b.BucketStartNs, Nullable<double>(float b.Volume)))
        arr
    let volSeries = ColumnSeries<DateTimePoint>()
    volSeries.Values <- volPoints
    volSeries.Fill <- mkPaint volColor
    volSeries.Stroke <- null
    volSeries.MaxBarWidth <- 12.0
    volSeries.ScalesYAt <- 1     // attach to secondary Y axis (volume scale)
    volSeries.Name <- "V"
    volSeries.YToolTipLabelFormatter <- fun (cp: LiveChartsCore.Kernel.ChartPoint<DateTimePoint, _, _>) ->
        let v = cp.Model.Value.GetValueOrDefault(0.0)
        (int64 v).ToString("N0")

    chart.Series <- [|
        volSeries :> ISeries
        candleSeries :> ISeries
        vwapSeries :> ISeries
    |]

    // ---------- axes ----------
    let xAxis = DateTimeAxis(TimeSpan.FromMinutes 1.0, fun (dt: DateTime) -> dt.ToString("HH:mm"))
    xAxis.LabelsPaint <- mkPaint textColor
    xAxis.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
    xAxis.TextSize <- 11.0
    xAxis.NamePaint <- mkPaint textColor

    // Frame the initial view to RTH (09:30 ET of the loaded day's last bar onward).
    // The user can still scroll-pan left into premarket.
    if not bars.IsEmpty then
        let lastNy = toNyTime (bars |> List.last |> fun b -> b.BucketStartNs)
        let rthOpen = DateTime(lastNy.Year, lastNy.Month, lastNy.Day, 9, 30, 0)
        xAxis.MinLimit <- Nullable<double>(float rthOpen.Ticks)

    let yPrice = Axis()
    yPrice.LabelsPaint <- mkPaint textColor
    yPrice.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
    yPrice.TextSize <- 11.0
    yPrice.Position <- LiveChartsCore.Measure.AxisPosition.End

    // Secondary axis for volume — hidden labels, scaled so columns sit in bottom ~30% of pane.
    let yVol = Axis()
    yVol.IsVisible <- false
    yVol.ShowSeparatorLines <- false
    yVol.MinLimit <- Nullable<double>(0.0)
    // Pad max so volume columns top out around 30% of the price-pane height.
    let volMax =
        if bars.IsEmpty then 1.0
        else bars |> List.map (fun b -> float b.Volume) |> List.max
    yVol.MaxLimit <- Nullable<double>(volMax * 3.5)

    chart.XAxes <- [| xAxis :> ICartesianAxis |]
    chart.YAxes <- [| yPrice :> ICartesianAxis; yVol :> ICartesianAxis |]

    chart.LegendPosition <- LiveChartsCore.Measure.LegendPosition.Top
    chart.LegendTextPaint <- mkPaint textColor
    chart.LegendBackgroundPaint <- mkPaint bgColor

    chart.TooltipPosition <- LiveChartsCore.Measure.TooltipPosition.Top
    chart.TooltipTextPaint <- mkPaint textColor
    chart.TooltipBackgroundPaint <- mkPaint gridColor

    // Pan/zoom on the X axis only — price scaling stays automatic.
    chart.ZoomMode <- LiveChartsCore.Measure.ZoomAndPanMode.X

    chart
