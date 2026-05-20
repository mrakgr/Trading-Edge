module TradingEdge.ReplaySimulatorV2.MainWindow

// Window + toolbar + chart. The UI thread runs a DispatcherTimer that polls
// the worker's channel for the latest PlayResult and feeds it to the chart's
// ApplyDiff. Worker computation never touches the UI thread.

open System
open System.Threading
open System.Threading.Channels
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Layout
open Avalonia.Threading
open TradingEdge.ReplaySimulatorV2.Snapshots
open TradingEdge.ReplaySimulatorV2.Play
open TradingEdge.ReplaySimulatorV2.Worker

let private bgBrush     = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush  = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush   = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush  = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))
let private accentBrush = SolidColorBrush(Color.FromRgb(0x2euy, 0xb8uy, 0x88uy))

let private NY_TZ_MW =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private nyOf (utcNs: int64) =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ_MW)

let private fmtClock (utcNs: int64) =
    if utcNs = Int64.MinValue then "--:--:--"
    else (nyOf utcNs).ToString("HH:mm:ss")

let private mkButton (text: string) =
    let b = Button()
    b.Content <- text
    b.Margin <- Thickness(2.0)
    b.Padding <- Thickness(10.0, 4.0)
    b.Foreground <- textBrush
    b.Background <- panelBrush
    b.BorderBrush <- mutedBrush
    b

let create
        (symbol: string)
        (date: string)
        (store: SnapshotStore)
        (startCursorNs: int64)
        : Window =
    let w = Window()
    w.Title <- sprintf "ReplaySimulatorV2 — %s %s" symbol date
    w.Width <- 1400.0
    w.Height <- 800.0
    w.Background <- bgBrush

    let header = TextBlock()
    header.Text <-
        sprintf "%s — %s | %d snapshots | %d MBO records | V2 (immutable book)"
            symbol date store.Snapshots.Count store.Records.Length
    header.Foreground <- textBrush
    header.FontSize <- 13.0
    header.Margin <- Thickness(10.0, 6.0, 10.0, 4.0)

    let controller = ChartView.build ()

    let state = ReplayState()
    let cts = new CancellationTokenSource()
    let reader, producer = Worker.start store state startCursorNs cts.Token

    // ---- toolbar ----
    let toolbar = StackPanel()
    toolbar.Orientation <- Orientation.Horizontal
    toolbar.Margin <- Thickness(10.0, 2.0, 10.0, 6.0)
    toolbar.Background <- bgBrush

    let playBtn = mkButton "Play"
    let speedLabel =
        TextBlock(Text = "Speed:", Foreground = mutedBrush,
                  VerticalAlignment = VerticalAlignment.Center,
                  Margin = Thickness(12.0, 0.0, 4.0, 0.0))
    let speedButtons =
        [| 1.0; 5.0; 30.0; 300.0 |]
        |> Array.map (fun m ->
            let b = mkButton (sprintf "%.0fx" m)
            b.Tag <- box m
            b)
    let resumeBtn = mkButton "Resume Auto"
    resumeBtn.Margin <- Thickness(12.0, 2.0, 2.0, 2.0)

    let clockLabel =
        TextBlock(Foreground = textBrush, FontFamily = FontFamily("monospace"),
                  FontSize = 14.0, VerticalAlignment = VerticalAlignment.Center,
                  Margin = Thickness(20.0, 0.0, 8.0, 0.0))
    let statusLabel =
        TextBlock(Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center,
                  Margin = Thickness(8.0, 0.0, 0.0, 0.0))
    clockLabel.Text <- "--:--:--"
    statusLabel.Text <- "paused"

    toolbar.Children.Add(playBtn)
    toolbar.Children.Add(speedLabel)
    for b in speedButtons do toolbar.Children.Add(b)
    toolbar.Children.Add(resumeBtn)
    toolbar.Children.Add(clockLabel)
    toolbar.Children.Add(statusLabel)

    let refreshSpeedButtons () =
        for b in speedButtons do
            let m : float = unbox b.Tag
            if abs (m - state.Speed) < 1e-9 then
                b.BorderBrush <- accentBrush
                b.Foreground <- accentBrush
            else
                b.BorderBrush <- mutedBrush
                b.Foreground <- textBrush
    refreshSpeedButtons ()

    let refreshPlayBtn () =
        playBtn.Content <- if state.Paused then "Play" else "Pause"
        statusLabel.Text <- if state.Paused then "paused" else "playing"
    refreshPlayBtn ()

    let refreshResumeBtn () =
        if controller.IsAutoFollow () then
            resumeBtn.BorderBrush <- mutedBrush
            resumeBtn.Foreground <- mutedBrush
            resumeBtn.IsEnabled <- false
        else
            resumeBtn.BorderBrush <- accentBrush
            resumeBtn.Foreground <- accentBrush
            resumeBtn.IsEnabled <- true
    refreshResumeBtn ()

    playBtn.Click.Add(fun _ ->
        state.Paused <- not state.Paused
        refreshPlayBtn ())

    for b in speedButtons do
        b.Click.Add(fun _ ->
            let m : float = unbox b.Tag
            state.Speed <- m
            refreshSpeedButtons ())

    resumeBtn.Click.Add(fun _ ->
        controller.ResumeAutoFollow ()
        refreshResumeBtn ())

    // ---- layout ----
    let grid = Grid()
    grid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    grid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    grid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(header, 0)
    Grid.SetRow(toolbar, 1)
    Grid.SetRow(controller.Chart, 2)
    grid.Children.Add(header)
    grid.Children.Add(toolbar)
    grid.Children.Add(controller.Chart)
    w.Content <- grid

    // ---- UI pump: DispatcherTimer drains the channel non-blockingly ----
    let mutable lastApplied : PlayResult option = None
    let timer = DispatcherTimer()
    timer.Interval <- TimeSpan.FromMilliseconds(float TICK_MS)
    timer.Tick.Add(fun _ ->
        // Drain any pending frames; only the LAST one matters since DropOldest
        // ensures the channel never holds more than one anyway.
        let mutable latest : PlayResult voption = ValueNone
        let mutable tick = Unchecked.defaultof<PlayResult>
        while reader.TryRead(&tick) do
            latest <- ValueSome tick
        match latest with
        | ValueNone -> ()
        | ValueSome r ->
            controller.ApplyDiff lastApplied r
            lastApplied <- Some r
            clockLabel.Text <- fmtClock r.Time
            refreshResumeBtn ())
    timer.Start()

    // ---- cleanup ----
    w.Closed.Add(fun _ ->
        timer.Stop()
        cts.Cancel()
        try producer.Wait(500) |> ignore with _ -> ()
        cts.Dispose())

    w
