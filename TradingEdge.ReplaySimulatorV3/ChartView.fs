module TradingEdge.ReplaySimulatorV2.ChartView

// LiveCharts2 chart for V3. Single pane, dark theme, candles + VWAP + faint
// volume background. Series values are bound to ObservableCollection<T> so
// every Add / set_Item raises the right INotifyCollectionChanged event and the
// chart redraws deterministically — no axis side-channel needed.
//
// Diff strategy: the data model is an immutable F# Bar list, head-first
// (newest = head). Walk current.Bars peeling heads until its tail is
// reference-equal to some tail of prev.Bars — at that point we've found the
// shared structural region. `unsharedCount` heads on current's side need to
// be reflected on the chart. Chart-side: drop the prior live head's slot, then
// append the `unsharedCount` newest bars in oldest-first order. If no shared
// cell is ever found (e.g. first frame, or seek), full rebuild.

open System
open System.Collections.ObjectModel
open Avalonia.Media
open LiveChartsCore
open LiveChartsCore.Kernel.Sketches
open LiveChartsCore.SkiaSharpView
open LiveChartsCore.SkiaSharpView.Avalonia
open LiveChartsCore.SkiaSharpView.Painting
open LiveChartsCore.Defaults
open SkiaSharp
open TradingEdge.ReplaySimulatorV2.Bars
open TradingEdge.ReplaySimulatorV2.Snapshots
open System.Collections.Generic

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

type ChartView() =
    let chart = CartesianChart()
    do
        chart.Background <-
            SolidColorBrush(Color.FromArgb(0xFFuy, bgColor.Red, bgColor.Green, bgColor.Blue))
        chart.AnimationsSpeed <- TimeSpan.Zero

    // Observable backing collections. LiveCharts2 subscribes to
    // INotifyCollectionChanged on these and incremental-updates its rendering.
    let candleValues = ObservableCollection<FinancialPoint>()
    let openValues   = ObservableCollection<DateTimePoint>()
    let highValues   = ObservableCollection<DateTimePoint>()
    let lowValues    = ObservableCollection<DateTimePoint>()
    let closeValues  = ObservableCollection<DateTimePoint>()
    let vwapValues   = ObservableCollection<DateTimePoint>()
    let volValues    = ObservableCollection<DateTimePoint>()

    let candleSeries = CandlesticksSeries<FinancialPoint>()
    do
        candleSeries.Values <- candleValues
        candleSeries.UpFill <- mkPaint upColor
        candleSeries.DownFill <- mkPaint downColor
        candleSeries.UpStroke <- mkStrokePaint upColor 1.0f
        candleSeries.DownStroke <- mkStrokePaint downColor 1.0f
        candleSeries.Name <- "OHLC"
        candleSeries.IsHoverable <- false

    let mkOhlcSeries (name: string) (values: ObservableCollection<DateTimePoint>) =
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
    do
        vwapSeries.Values <- vwapValues
        vwapSeries.Stroke <- mkStrokePaint vwapColor 1.5f
        vwapSeries.Fill <- null
        vwapSeries.GeometrySize <- 0.0
        vwapSeries.Name <- "VWAP"

    let volSeries = ColumnSeries<DateTimePoint>()
    do
        volSeries.Values <- volValues
        volSeries.Fill <- mkPaint volColor
        volSeries.Stroke <- null
        volSeries.MaxBarWidth <- 12.0
        volSeries.ScalesYAt <- 1
        volSeries.Name <- "V"
        volSeries.YToolTipLabelFormatter <- fun (cp: LiveChartsCore.Kernel.ChartPoint<DateTimePoint, _, _>) ->
            let v = cp.Model.Value.GetValueOrDefault(0.0)
            (int64 v).ToString("N0")

    do
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
    do
        xAxis.LabelsPaint <- mkPaint textColor
        xAxis.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
        xAxis.TextSize <- 11.0
        xAxis.NamePaint <- mkPaint textColor

    let yPrice = Axis()
    do
        yPrice.LabelsPaint <- mkPaint textColor
        yPrice.SeparatorsPaint <- mkStrokePaint gridColor 1.0f
        yPrice.TextSize <- 11.0
        yPrice.Position <- LiveChartsCore.Measure.AxisPosition.End

    let yVol = Axis()
    do
        yVol.IsVisible <- false
        yVol.ShowSeparatorLines <- false
        yVol.MinLimit <- Nullable<double>(0.0)
        yVol.MaxLimit <- Nullable<double>(1.0)
    let mutable maxVolume = 0.0

    do
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

    do
        let onAxisChanged (_: obj) (_: System.ComponentModel.PropertyChangedEventArgs) =
            if not suppressAxisChangeHandler then autoFollow <- false
        (xAxis :> System.ComponentModel.INotifyPropertyChanged).PropertyChanged.AddHandler(
            System.ComponentModel.PropertyChangedEventHandler(onAxisChanged))

    // ---------- per-bar projections ----------
    let barFinancial (b: Bar) =
        FinancialPoint(toNyTime b.BucketStartNs, b.High, b.Open, b.Close, b.Low)

    let nullableVwap (b: Bar) =
        match sessionVwap b with
        | Some x -> Nullable<double>(x)
        | None -> Nullable<double>()

    let dtPoint (b: Bar) (v: float) =
        DateTimePoint(toNyTime b.BucketStartNs, Nullable<double>(v))

    let appendBar (b: Bar) =
        candleValues.Add(barFinancial b)
        openValues.Add(dtPoint b b.Open)
        highValues.Add(dtPoint b b.High)
        lowValues.Add(dtPoint b b.Low)
        closeValues.Add(dtPoint b b.Close)
        vwapValues.Add(DateTimePoint(toNyTime b.BucketStartNs, nullableVwap b))
        volValues.Add(dtPoint b (float b.Volume))
        if float b.Volume > maxVolume then maxVolume <- float b.Volume

    let removeLast () =
        let i = candleValues.Count - 1
        if i >= 0 then
            candleValues.RemoveAt i
            openValues.RemoveAt i
            highValues.RemoveAt i
            lowValues.RemoveAt i
            closeValues.RemoveAt i
            vwapValues.RemoveAt i
            volValues.RemoveAt i

    let clearAll () =
        candleValues.Clear()
        openValues.Clear()
        highValues.Clear()
        lowValues.Clear()
        closeValues.Clear()
        vwapValues.Clear()
        volValues.Clear()
        maxVolume <- 0.0

    /// Push the first `n` bars from `bars` (which is head-first / newest-first)
    /// into the chart in oldest-first order.
    let appendNewest (bars: Bar list) (n: int) =
        if n > 0 then
            let buf = Array.zeroCreate<Bar> n
            let mutable rest = bars
            for i in 0 .. n - 1 do
                buf.[n - 1 - i] <- List.head rest
                rest <- List.tail rest
            for b in buf do appendBar b

    let fullRebuild (current: Snapshot) =
        clearAll ()
        let asArray = current.Bars |> List.toArray
        for i in asArray.Length - 1 .. -1 .. 0 do
            appendBar asArray.[i]

    member _.Chart = chart

    member _.ApplyDiff(prev: Snapshot option, current: Snapshot) =
        // How many elements does the first list need to drop before its tail
        // matches the tail of the second list.
        let findPrefixIndex aa bb =
            let rec loop count aa bb =
                match aa, bb with
                | _ :: aa', _ :: bb' ->
                    if Object.ReferenceEquals(aa', bb') then ValueSome(count + 1)
                    else loop (count + 1) aa' bb
                | _ -> ValueNone
            loop 0 aa bb
        let prevBars = match prev with | Some p -> p.Bars | None -> []
        match findPrefixIndex current.Bars prevBars with
        | ValueSome unsharedCount ->
            removeLast ()
            appendNewest current.Bars unsharedCount
        | ValueNone ->
            fullRebuild current

        if maxVolume > 0.0 then
            yVol.MaxLimit <- Nullable<double>(maxVolume * 3.5)

        if autoFollow && candleValues.Count > 0 then
            let latest = candleValues.[candleValues.Count - 1].Date
            frameToLatest latest

    member _.ResumeAutoFollow() =
        autoFollow <- true
        if candleValues.Count > 0 then
            frameToLatest candleValues.[candleValues.Count - 1].Date

    member _.IsAutoFollow = autoFollow
