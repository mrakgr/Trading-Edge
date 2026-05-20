module TradingEdge.ReplaySimulator.MainWindow

open System
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Layout
open Avalonia.Threading
open TradingEdge.ReplaySimulator.Dbn
open TradingEdge.ReplaySimulator.Bars
open TradingEdge.ReplaySimulator.ReplayEngine

let private bgBrush      = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush   = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush    = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush   = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))
let private accentBrush  = SolidColorBrush(Color.FromRgb(0x2euy, 0xb8uy, 0x88uy))

let private NY_TZ_MW =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private fmtClock (utcNs: int64) =
    if utcNs = Int64.MinValue then "--:--:--"
    else
        let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
        let ny = TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ_MW)
        ny.ToString("HH:mm:ss")

let private mkButton (text: string) =
    let b = Button()
    b.Content <- text
    b.Margin <- Thickness(2.0)
    b.Padding <- Thickness(10.0, 4.0)
    b.Foreground <- textBrush
    b.Background <- panelBrush
    b.BorderBrush <- mutedBrush
    b

/// Compose the chart inside a window, set up the replay engine, and wire the
/// playback toolbar. The window owns the engine's CancellationTokenSource so
/// the producer task is cleanly torn down on close.
let create
        (symbol: string)
        (date: string)
        (mergedStream: seq<MboMsg>)
        (totalVenues: int)
        : Window =
    let w = Window()
    w.Title <- sprintf "ReplaySimulator — %s %s" symbol date
    w.Width <- 1400.0
    w.Height <- 800.0
    w.Background <- bgBrush

    // ---- header ----
    let header = TextBlock()
    header.Text <- sprintf "%s — %s | %d venues | replay (1m bars from MBO T records)" symbol date totalVenues
    header.Foreground <- textBrush
    header.FontSize <- 13.0
    header.Margin <- Thickness(10.0, 6.0, 10.0, 4.0)

    // ---- chart + controller ----
    let controller = ChartView.buildLive []

    // ---- replay engine ----
    // Start replay at 09:30 ET of the loaded day (skip premarket).
    let startCursorNs =
        match DateTime.TryParseExact(
                date, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None) with
        | true, d ->
            let nyOpen = DateTime(d.Year, d.Month, d.Day, 9, 30, 0, DateTimeKind.Unspecified)
            let utc = TimeZoneInfo.ConvertTimeToUtc(nyOpen, NY_TZ_MW)
            let ticksSinceEpoch = utc.Ticks - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
            Some (ticksSinceEpoch * 100L)
        | _ -> None

    let state = ReplayState()
    let cts = new CancellationTokenSource()
    let reader, producer = ReplayEngine.start mergedStream state startCursorNs cts.Token

    // ---- toolbar ----
    let toolbar = StackPanel()
    toolbar.Orientation <- Orientation.Horizontal
    toolbar.Margin <- Thickness(10.0, 2.0, 10.0, 6.0)
    toolbar.Background <- bgBrush

    let playBtn = mkButton "Play"
    let speedLabel = TextBlock(Text = "Speed:", Foreground = mutedBrush,
                               VerticalAlignment = VerticalAlignment.Center,
                               Margin = Thickness(12.0, 0.0, 4.0, 0.0))
    let speedButtons = [| 1.0; 5.0; 30.0; 300.0 |] |> Array.map (fun m ->
        let b = mkButton (sprintf "%.0fx" m)
        b.Tag <- box m
        b)
    let resumeBtn = mkButton "Resume Auto"
    resumeBtn.Margin <- Thickness(12.0, 2.0, 2.0, 2.0)

    let clockLabel = TextBlock(Foreground = textBrush, FontFamily = FontFamily("monospace"),
                               FontSize = 14.0, VerticalAlignment = VerticalAlignment.Center,
                               Margin = Thickness(20.0, 0.0, 8.0, 0.0))
    let statusLabel = TextBlock(Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center,
                                Margin = Thickness(8.0, 0.0, 0.0, 0.0))
    clockLabel.Text <- "--:--:--"
    statusLabel.Text <- "paused"

    toolbar.Children.Add(playBtn)
    toolbar.Children.Add(speedLabel)
    for b in speedButtons do toolbar.Children.Add(b)
    toolbar.Children.Add(resumeBtn)
    toolbar.Children.Add(clockLabel)
    toolbar.Children.Add(statusLabel)

    // Highlight the active speed button.
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

    // ---- pump replay ticks to the UI thread ----
    let pumpTask =
        task {
            try
                let! _ = reader.WaitToReadAsync(cts.Token)
                let mutable keepGoing = true
                while keepGoing && not cts.IsCancellationRequested do
                    let mutable tick = Unchecked.defaultof<ReplayTick>
                    if reader.TryRead(&tick) then
                        let captured = tick
                        do! Dispatcher.UIThread.InvokeAsync(fun () ->
                            controller.ApplyTick captured
                            clockLabel.Text <- fmtClock captured.StreamTimeNs
                            refreshResumeBtn ()
                            if captured.EndOfStream then
                                state.Paused <- true
                                refreshPlayBtn ()
                                statusLabel.Text <- "end of stream"
                        )
                        if tick.EndOfStream then keepGoing <- false
                    else
                        let! more = reader.WaitToReadAsync(cts.Token)
                        if not more then keepGoing <- false
            with
            | :? OperationCanceledException -> ()
        }

    // ---- cleanup on close ----
    w.Closed.Add(fun _ ->
        cts.Cancel()
        try producer.Wait(500) |> ignore with _ -> ()
        try pumpTask.Wait(500) |> ignore with _ -> ()
        cts.Dispose())

    w
