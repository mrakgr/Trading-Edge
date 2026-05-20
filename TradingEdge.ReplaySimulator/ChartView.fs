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
open System.ComponentModel
open Avalonia.Media
open LiveChartsCore
open LiveChartsCore.Kernel.Sketches
open LiveChartsCore.SkiaSharpView
open LiveChartsCore.SkiaSharpView.Avalonia
open LiveChartsCore.SkiaSharpView.Painting
open LiveChartsCore.Defaults
open SkiaSharp
open TradingEdge.ReplaySimulator.Bars
open TradingEdge.ReplaySimulator.ReplayEngine

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

/// Handle returned alongside the chart control. UI thread feeds replay ticks
/// to `ApplyTick`; the controller mutates the underlying series ResizeArrays
/// and (when in auto-follow mode) slides the X axis to keep the latest bar in
/// view. Pan/zoom by the user implicitly drops auto-follow (see `IsAutoFollow`).
type ChartController = {
    Chart: CartesianChart
    /// Apply a replay tick on the UI thread.
    ApplyTick: ReplayTick -> unit
    /// Re-enable auto-follow. Called when the user clicks "Resume".
    ResumeAutoFollow: unit -> unit
    /// Whether auto-follow is currently active (read-only signal for the UI).
    IsAutoFollow: unit -> bool
}

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
    // Candle's own tooltip row would use missing-glyph chars. Suppress its row and
    // synthesize O/H/L/C as separate invisible series below.
    candleSeries.IsHoverable <- false

    // Invisible per-field series so each OHLC value gets its own tooltip row.
    // Stroke/fill nulled and geometry size 0 → no marker drawn or shown on hover.
    let mkOhlcSeries (name: string) (extract: Bar -> double) =
        let pts = ResizeArray<DateTimePoint>()
        for b in bars do
            pts.Add(DateTimePoint(toNyTime b.BucketStartNs, Nullable<double>(extract b)))
        let s = LineSeries<DateTimePoint>()
        s.Values <- pts
        s.Stroke <- null
        s.Fill <- null
        s.GeometrySize <- 0.0
        s.GeometryStroke <- null
        s.GeometryFill <- null
        s.Name <- name
        s.YToolTipLabelFormatter <- fun cp ->
            sprintf "%.4f" (cp.Model.Value.GetValueOrDefault 0.0)
        s
    let openSeries  = mkOhlcSeries "O" (fun b -> b.Open)
    let highSeries  = mkOhlcSeries "H" (fun b -> b.High)
    let lowSeries   = mkOhlcSeries "L" (fun b -> b.Low)
    let closeSeries = mkOhlcSeries "C" (fun b -> b.Close)

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
        openSeries :> ISeries
        highSeries :> ISeries
        lowSeries :> ISeries
        closeSeries :> ISeries
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

/// Build the chart in live-replay mode. Series start empty (or with the optional
/// `seed` bars); `controller.ApplyTick` is called on the UI thread per replay
/// tick to mutate the series in-place.
///
/// Auto-follow rolls the X axis so the latest bar is always 90% from the left;
/// the user can pan/zoom freely to drop into manual mode, then `ResumeAutoFollow`
/// re-arms it.
let buildLive (seed: Bar list) : ChartController =
    let chart = CartesianChart()
    chart.Background <- SolidColorBrush(Color.FromArgb(0xFFuy, bgColor.Red, bgColor.Green, bgColor.Blue))
    // Animations off — at 15ms tick cadence any tweening just smears the candle.
    chart.AnimationsSpeed <- TimeSpan.Zero

    let candleValues = ResizeArray<FinancialPoint>()
    let openValues = ResizeArray<DateTimePoint>()
    let highValues = ResizeArray<DateTimePoint>()
    let lowValues = ResizeArray<DateTimePoint>()
    let closeValues = ResizeArray<DateTimePoint>()
    let vwapValues = ResizeArray<DateTimePoint>()
    let volValues = ResizeArray<DateTimePoint>()

    let barToFinancial (b: Bar) =
        FinancialPoint(toNyTime b.BucketStartNs, b.High, b.Open, b.Close, b.Low)
    let nullableVwap (b: Bar) =
        match b.SessionVwap with
        | Some x -> Nullable<double>(x)
        | None -> Nullable<double>()

    let pushClosed (b: Bar) =
        let t = toNyTime b.BucketStartNs
        candleValues.Add(barToFinancial b)
        openValues.Add(DateTimePoint(t, Nullable<double>(b.Open)))
        highValues.Add(DateTimePoint(t, Nullable<double>(b.High)))
        lowValues.Add(DateTimePoint(t, Nullable<double>(b.Low)))
        closeValues.Add(DateTimePoint(t, Nullable<double>(b.Close)))
        vwapValues.Add(DateTimePoint(t, nullableVwap b))
        volValues.Add(DateTimePoint(t, Nullable<double>(float b.Volume)))

    for b in seed do pushClosed b

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
    // Volume scale auto-grows: re-pin MaxLimit on each tick to (3.5 × maxVolume).
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

    // ---------- auto-follow logic ----------
    // Visible window width (stream ns) — fixed at 90 minutes; the latest bar
    // sits at ~90% from the left edge so there's headroom on the right.
    let WINDOW_NS = 90L * 60L * 1_000_000_000L
    let HEADROOM_FRAC = 0.10
    let mutable autoFollow = true
    let mutable suppressAxisChangeHandler = false

    // When the user manually pans/zooms, the X-axis MinLimit/MaxLimit change to
    // values we didn't set. Detect that and drop auto-follow.
    let onAxisChanged (sender: obj) (_: PropertyChangedEventArgs) =
        if not suppressAxisChangeHandler then
            autoFollow <- false
    // PropertyChanged is the easiest hook on Axis.
    (xAxis :> INotifyPropertyChanged).PropertyChanged.AddHandler(
        PropertyChangedEventHandler(fun s e -> onAxisChanged s e))

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

    // Initial framing: 09:30 ET of today if we have no seed, else around the seed's last bar.
    let seedTime =
        match seed with
        | [] -> None
        | _  -> Some (toNyTime ((List.last seed).BucketStartNs))
    match seedTime with
    | Some t -> frameToLatest t
    | None ->
        let now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, NY_TZ)
        let rthOpen = DateTime(now.Year, now.Month, now.Day, 9, 30, 0)
        frameToLatest rthOpen

    // Seed the volume axis from any seed bars.
    if not seed.IsEmpty then
        maxVolume <- seed |> List.map (fun b -> float b.Volume) |> List.max
        yVol.MaxLimit <- Nullable<double>(maxVolume * 3.5)

    // ---------- ApplyTick ----------
    let applyTick (tick: ReplayTick) =
        // Closed bars become permanent history.
        for cb in tick.ClosedBars do
            pushClosed cb
            if float cb.Volume > maxVolume then maxVolume <- float cb.Volume

        // Current (still-forming) bar overwrites the last element (or appends if it's a new bucket).
        match tick.Current with
        | None -> ()
        | Some cur ->
            let t = toNyTime cur.BucketStartNs
            let lastIsSameBucket =
                candleValues.Count > 0 && candleValues.[candleValues.Count - 1].Date = t
            if lastIsSameBucket then
                let i = candleValues.Count - 1
                candleValues.[i] <- barToFinancial cur
                openValues.[i]   <- DateTimePoint(t, Nullable<double>(cur.Open))
                highValues.[i]   <- DateTimePoint(t, Nullable<double>(cur.High))
                lowValues.[i]    <- DateTimePoint(t, Nullable<double>(cur.Low))
                closeValues.[i]  <- DateTimePoint(t, Nullable<double>(cur.Close))
                vwapValues.[i]   <- DateTimePoint(t, nullableVwap cur)
                volValues.[i]    <- DateTimePoint(t, Nullable<double>(float cur.Volume))
            else
                pushClosed cur   // first sighting of a new bucket
            if float cur.Volume > maxVolume then maxVolume <- float cur.Volume

        if maxVolume > 0.0 then
            yVol.MaxLimit <- Nullable<double>(maxVolume * 3.5)

        // Auto-follow: keep the latest bar near the right edge.
        if autoFollow && candleValues.Count > 0 then
            let latest = candleValues.[candleValues.Count - 1].Date
            frameToLatest latest

    {
        Chart = chart
        ApplyTick = applyTick
        ResumeAutoFollow = fun () ->
            autoFollow <- true
            if candleValues.Count > 0 then
                frameToLatest candleValues.[candleValues.Count - 1].Date
        IsAutoFollow = fun () -> autoFollow
    }
