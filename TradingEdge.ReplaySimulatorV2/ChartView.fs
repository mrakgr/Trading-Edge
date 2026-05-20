module TradingEdge.ReplaySimulatorV2.ChartView

// LiveCharts2 chart for the V2 GUI. Single pane, dark theme, candles + VWAP
// + faint volume background. Exposes ApplyDiff which exploits structural
// equality of PlayResult.BarBuckets to avoid touching the historical-bars
// portion of the chart on every frame.

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
open TradingEdge.ReplaySimulatorV2.Bars
open TradingEdge.ReplaySimulatorV2.Play

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private toNyTime (utcNs: int64) : DateTime =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)

// dark-theme palette
let private bgColor    = SKColor(0x10uy, 0x12uy, 0x18uy)
let private gridColor  = SKColor(0x2auy, 0x2euy, 0x38uy)
let private textColor  = SKColor(0xc8uy, 0xccuy, 0xd6uy)
let private upColor    = SKColor(0x2euy, 0xb8uy, 0x88uy)
let private downColor  = SKColor(0xe5uy, 0x4buy, 0x4buy)
let private vwapColor  = SKColor(0xf2uy, 0xc5uy, 0x5cuy)
let private volColor   = SKColor(0x4auy, 0x55uy, 0x68uy, 0x60uy)

let private mkPaint (c: SKColor) = SolidColorPaint(c)
let private mkStrokePaint (c: SKColor) (w: float32) =
    let p = SolidColorPaint(c)
    p.StrokeThickness <- w
    p

type ChartController = {
    Chart: CartesianChart
    ApplyDiff: PlayResult option -> PlayResult -> unit
    ResumeAutoFollow: unit -> unit
    IsAutoFollow: unit -> bool
}

/// Build the chart with empty series; ApplyDiff fills them as PlayResults arrive.
let build () : ChartController =
    let chart = CartesianChart()
    chart.Background <- SolidColorBrush(Color.FromArgb(0xFFuy, bgColor.Red, bgColor.Green, bgColor.Blue))
    chart.AnimationsSpeed <- TimeSpan.Zero

    // Shared ResizeArrays — LiveCharts2 reads from these directly and we mutate
    // them in place on each frame.
    let candleValues = ResizeArray<FinancialPoint>()
    let openValues   = ResizeArray<DateTimePoint>()
    let highValues   = ResizeArray<DateTimePoint>()
    let lowValues    = ResizeArray<DateTimePoint>()
    let closeValues  = ResizeArray<DateTimePoint>()
    let vwapValues   = ResizeArray<DateTimePoint>()
    let volValues    = ResizeArray<DateTimePoint>()

    let candleSeries = CandlesticksSeries<FinancialPoint>()
    candleSeries.Values <- candleValues
    candleSeries.UpFill <- mkPaint upColor
    candleSeries.DownFill <- mkPaint downColor
    candleSeries.UpStroke <- mkStrokePaint upColor 1.0f
    candleSeries.DownStroke <- mkStrokePaint downColor 1.0f
    candleSeries.Name <- "OHLC"
    candleSeries.IsHoverable <- false

    let mkOhlcSeries (name: string) (values: ResizeArray<DateTimePoint>) =
        let s = LineSeries<DateTimePoint>()
        s.Values <- values
        s.Stroke <- null
        s.Fill <- null
        s.GeometrySize <- 0.0
        s.GeometryStroke <- null
        s.GeometryFill <- null
        s.Name <- name
        s.YToolTipLabelFormatter <- fun cp ->
            sprintf "%.4f" (cp.Model.Value.GetValueOrDefault 0.0)
        s
    let openSeries  = mkOhlcSeries "O" openValues
    let highSeries  = mkOhlcSeries "H" highValues
    let lowSeries   = mkOhlcSeries "L" lowValues
    let closeSeries = mkOhlcSeries "C" closeValues

    let vwapSeries = LineSeries<DateTimePoint>()
    vwapSeries.Values <- vwapValues
    vwapSeries.Stroke <- mkStrokePaint vwapColor 1.5f
    vwapSeries.Fill <- null
    vwapSeries.GeometrySize <- 0.0
    vwapSeries.Name <- "VWAP"

    let volSeries = ColumnSeries<DateTimePoint>()
    volSeries.Values <- volValues
    volSeries.Fill <- mkPaint volColor
    volSeries.Stroke <- null
    volSeries.MaxBarWidth <- 12.0
    volSeries.ScalesYAt <- 1
    volSeries.Name <- "V"
    volSeries.YToolTipLabelFormatter <- fun (cp: LiveChartsCore.Kernel.ChartPoint<DateTimePoint, _, _>) ->
        let v = cp.Model.Value.GetValueOrDefault(0.0)
        (int64 v).ToString("N0")

    chart.Series <- [|
        volSeries :> ISeries
        candleSeries :> ISeries
        openSeries :> ISeries
        highSeries :> ISeries
        lowSeries :> ISeries
        closeSeries :> ISeries
        vwapSeries :> ISeries
    |]

    // axes
    let xAxis = DateTimeAxis(TimeSpan.FromMinutes 1.0, fun (dt: DateTime) -> dt.ToString("HH:mm"))
    xAxis.LabelsPaint <- mkPaint textColor
    xAxis.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
    xAxis.TextSize <- 11.0
    xAxis.NamePaint <- mkPaint textColor

    let yPrice = Axis()
    yPrice.LabelsPaint <- mkPaint textColor
    yPrice.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
    yPrice.TextSize <- 11.0
    yPrice.Position <- LiveChartsCore.Measure.AxisPosition.End

    let yVol = Axis()
    yVol.IsVisible <- false
    yVol.ShowSeparatorLines <- false
    yVol.MinLimit <- Nullable<double>(0.0)
    yVol.MaxLimit <- Nullable<double>(1.0)
    let mutable maxVolume = 0.0

    chart.XAxes <- [| xAxis :> ICartesianAxis |]
    chart.YAxes <- [| yPrice :> ICartesianAxis; yVol :> ICartesianAxis |]

    chart.LegendPosition <- LiveChartsCore.Measure.LegendPosition.Top
    chart.LegendTextPaint <- mkPaint textColor
    chart.LegendBackgroundPaint <- mkPaint bgColor

    chart.TooltipPosition <- LiveChartsCore.Measure.TooltipPosition.Top
    chart.TooltipTextPaint <- mkPaint textColor
    chart.TooltipBackgroundPaint <- mkPaint gridColor

    chart.ZoomMode <- LiveChartsCore.Measure.ZoomAndPanMode.X

    // ---------- auto-follow ----------
    let WINDOW_NS = 90L * 60L * 1_000_000_000L
    let HEADROOM_FRAC = 0.10
    let mutable autoFollow = true
    let mutable suppressAxisChangeHandler = false

    let onAxisChanged (_: obj) (_: System.ComponentModel.PropertyChangedEventArgs) =
        if not suppressAxisChangeHandler then autoFollow <- false
    (xAxis :> System.ComponentModel.INotifyPropertyChanged).PropertyChanged.AddHandler(
        System.ComponentModel.PropertyChangedEventHandler(onAxisChanged))

    let setAxisLimits (minTicks: float) (maxTicks: float) =
        suppressAxisChangeHandler <- true
        xAxis.MinLimit <- Nullable<double>(minTicks)
        xAxis.MaxLimit <- Nullable<double>(maxTicks)
        suppressAxisChangeHandler <- false

    let frameToLatest (latestNyTime: DateTime) =
        let windowSpan = TimeSpan.FromTicks(WINDOW_NS / 100L)
        let rightEdge = latestNyTime + TimeSpan.FromTicks(int64 (float windowSpan.Ticks * HEADROOM_FRAC))
        let leftEdge = rightEdge - windowSpan
        setAxisLimits (float leftEdge.Ticks) (float rightEdge.Ticks)

    // ---------- helpers ----------
    let barToFinancial (b: Bar) =
        FinancialPoint(toNyTime b.BucketStartNs, b.High, b.Open, b.Close, b.Low)

    let nullableVwap (b: Bar) =
        match b.SessionVwap with
        | Some x -> Nullable<double>(x)
        | None -> Nullable<double>()

    let pushBar (b: Bar) =
        let t = toNyTime b.BucketStartNs
        candleValues.Add(barToFinancial b)
        openValues.Add(DateTimePoint(t, Nullable<double>(b.Open)))
        highValues.Add(DateTimePoint(t, Nullable<double>(b.High)))
        lowValues.Add(DateTimePoint(t, Nullable<double>(b.Low)))
        closeValues.Add(DateTimePoint(t, Nullable<double>(b.Close)))
        vwapValues.Add(DateTimePoint(t, nullableVwap b))
        volValues.Add(DateTimePoint(t, Nullable<double>(float b.Volume)))
        if float b.Volume > maxVolume then maxVolume <- float b.Volume

    let truncateAllTo (n: int) =
        let cap = candleValues.Count
        if cap > n then
            let drop = cap - n
            candleValues.RemoveRange(n, drop)
            openValues.RemoveRange(n, drop)
            highValues.RemoveRange(n, drop)
            lowValues.RemoveRange(n, drop)
            closeValues.RemoveRange(n, drop)
            vwapValues.RemoveRange(n, drop)
            volValues.RemoveRange(n, drop)

    // Number of bars currently represented by frozen prefix buckets (not the tail
    // or forming bar). Lets us avoid touching the prefix on every diff.
    let mutable prefixBarCount = 0

    let rebuildFromPlay (r: PlayResult) =
        candleValues.Clear()
        openValues.Clear()
        highValues.Clear()
        lowValues.Clear()
        closeValues.Clear()
        vwapValues.Clear()
        volValues.Clear()
        maxVolume <- 0.0
        let mutable count = 0
        for bucket in r.BarBuckets do
            for b in bucket do
                pushBar b
                count <- count + 1
        prefixBarCount <- count
        for b in r.BarTail do pushBar b
        match r.FormingBar with
        | Some b -> pushBar b
        | None -> ()

    /// Apply diff: keep the prefix if BarBuckets is reference-equal; otherwise rebuild.
    let applyDiff (prev: PlayResult option) (current: PlayResult) =
        let prefixUnchanged =
            match prev with
            | Some p -> obj.ReferenceEquals(p.BarBuckets, current.BarBuckets)
            | None -> false

        if not prefixUnchanged then
            rebuildFromPlay current
        else
            // Prefix is the same; replace only tail + forming bar.
            truncateAllTo prefixBarCount
            for b in current.BarTail do pushBar b
            match current.FormingBar with
            | Some b -> pushBar b
            | None -> ()

        // Volume axis.
        if maxVolume > 0.0 then
            yVol.MaxLimit <- Nullable<double>(maxVolume * 3.5)

        // Auto-follow keeps the latest bar visible.
        if autoFollow && candleValues.Count > 0 then
            let latest = candleValues.[candleValues.Count - 1].Date
            frameToLatest latest

    {
        Chart = chart
        ApplyDiff = applyDiff
        ResumeAutoFollow = fun () ->
            autoFollow <- true
            if candleValues.Count > 0 then
                frameToLatest candleValues.[candleValues.Count - 1].Date
        IsAutoFollow = fun () -> autoFollow
    }
