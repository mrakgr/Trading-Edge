module TradingEdge.ReplaySimulatorV3.MainWindow

// Window + toolbar + chart. A background task awaits Snapshots from the
// worker's outbox; for each frame it marshals onto the UI thread via the
// Dispatcher and applies the diff there. All UI mutation happens on the UI
// thread; the worker computation happens entirely off it.

open System
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Layout
open Avalonia.Threading
open FSharp.Control
open TradingEdge.ReplaySimulatorV3.Time
open TradingEdge.ReplaySimulatorV3.Snapshots
open TradingEdge.ReplaySimulatorV3.Worker

let private bgBrush     = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush  = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush   = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush  = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))
let private accentBrush = SolidColorBrush(Color.FromRgb(0x2euy, 0xb8uy, 0x88uy))

let private fmtClock (utcNs: int64) =
    if utcNs = Int64.MinValue then "--:--:--"
    else (toNy utcNs).ToString("HH:mm:ss")

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
    w.Title <- sprintf "ReplaySimulatorV3 — %s %s" symbol date
    w.Width <- 1400.0
    w.Height <- 800.0
    w.Background <- bgBrush

    let header = TextBlock()
    header.Text <-
        sprintf "%s — %s | %d snapshots | %d MBO records | V3"
            symbol date store.Snapshots.Length store.Records.Length
    header.Foreground <- textBrush
    header.FontSize <- 13.0
    header.Margin <- Thickness(10.0, 6.0, 10.0, 4.0)

    let chartView = ChartView.ChartView()
    let bookView = BookView.BookView()
    let tapeView = TapeView.TapeView()
    let ladderView = PriceLadderView.PriceLadderView()

    let cts = new CancellationTokenSource()
    let worker = Worker.start store startCursorNs cts.Token

    // UI-side replay state shadow. These mirror what the worker holds; we
    // keep them only so the toolbar buttons can re-render their highlight
    // state. The worker is the source of truth.
    let mutable uiSpeed = 1.0
    let mutable uiPaused = true

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
    let recenterBtn = mkButton "Recenter Ladder"

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
    toolbar.Children.Add(recenterBtn)
    toolbar.Children.Add(clockLabel)
    toolbar.Children.Add(statusLabel)

    let refreshSpeedButtons () =
        for b in speedButtons do
            let m : float = unbox b.Tag
            if abs (m - uiSpeed) < 1e-9 then
                b.BorderBrush <- accentBrush
                b.Foreground <- accentBrush
            else
                b.BorderBrush <- mutedBrush
                b.Foreground <- textBrush
    refreshSpeedButtons ()

    let refreshPlayBtn () =
        playBtn.Content <- if uiPaused then "Play" else "Pause"
        statusLabel.Text <- if uiPaused then "paused" else "playing"
    refreshPlayBtn ()

    let refreshResumeBtn () =
        if chartView.IsAutoFollow then
            resumeBtn.BorderBrush <- mutedBrush
            resumeBtn.Foreground <- mutedBrush
            resumeBtn.IsEnabled <- false
        else
            resumeBtn.BorderBrush <- accentBrush
            resumeBtn.Foreground <- accentBrush
            resumeBtn.IsEnabled <- true
    refreshResumeBtn ()

    playBtn.Click.Add(fun _ ->
        uiPaused <- not uiPaused
        worker.Inbox.TryWrite(SetPaused uiPaused) |> ignore
        refreshPlayBtn ())

    for b in speedButtons do
        b.Click.Add(fun _ ->
            let m : float = unbox b.Tag
            uiSpeed <- m
            worker.Inbox.TryWrite(SetSpeed m) |> ignore
            refreshSpeedButtons ())

    resumeBtn.Click.Add(fun _ ->
        chartView.ResumeAutoFollow ()
        refreshResumeBtn ())

    recenterBtn.Click.Add(fun _ -> ladderView.Recenter())

    // ---- scrub slider ----
    // Drives the worker via Seek messages. Range is the loaded day's MBO
    // span. We also sync the slider's position from each incoming Snapshot,
    // gated by suppressSliderHandler so that echo doesn't loop back as Seek.
    let firstTs, lastTs =
        if store.Records.Length = 0 then 0L, 0L
        else store.Records.[0].TsEvent, store.Records.[store.Records.Length - 1].TsEvent
    let slider = Slider()
    slider.Minimum <- float firstTs
    slider.Maximum <- float (max lastTs (firstTs + 1L))
    slider.Value <- float (max firstTs (min lastTs startCursorNs))
    slider.IsSnapToTickEnabled <- false
    slider.Margin <- Thickness(10.0, 0.0, 10.0, 6.0)
    slider.Foreground <- accentBrush
    slider.Background <- panelBrush
    let mutable suppressSliderHandler = false
    slider.PropertyChanged.Add(fun e ->
        if e.Property = Slider.ValueProperty && not suppressSliderHandler then
            let v = slider.Value
            worker.Inbox.TryWrite(Seek (int64 v)) |> ignore)

    // ---- layout ----
    // Left column: header + toolbar + slider + chart (stacked).
    let leftGrid = Grid()
    leftGrid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    leftGrid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    leftGrid.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    leftGrid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(header, 0)
    Grid.SetRow(toolbar, 1)
    Grid.SetRow(slider, 2)
    Grid.SetRow(chartView.Chart, 3)
    leftGrid.Children.Add(header)
    leftGrid.Children.Add(toolbar)
    leftGrid.Children.Add(slider)
    leftGrid.Children.Add(chartView.Chart)

    // Right column is a TabControl with two tabs:
    //   L2     — BookView (top) + GridSplitter + T&S (bottom)
    //   Ladder — PriceLadderView fills the whole pane
    // Putting T&S inside the L2 tab means the TabControl swap automatically
    // removes it from the visual tree when the Ladder tab is active — no
    // empty-space leftover and no manual visibility toggles.
    let bookTabContent = Grid()
    bookTabContent.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    bookTabContent.RowDefinitions.Add(RowDefinition(GridLength.Auto))
    bookTabContent.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(bookView.Control, 0)
    Grid.SetRow(tapeView.Control, 2)
    let vSplitter = GridSplitter(Height = 4.0, Background = mutedBrush,
                                 HorizontalAlignment = HorizontalAlignment.Stretch)
    Grid.SetRow(vSplitter, 1)
    bookTabContent.Children.Add(bookView.Control)
    bookTabContent.Children.Add(vSplitter)
    bookTabContent.Children.Add(tapeView.Control)

    let bookTab = TabItem(Header = "L2", Content = bookTabContent)
    let ladderTab = TabItem(Header = "Ladder", Content = ladderView.Control)
    let bookLadderTabs = TabControl()
    bookLadderTabs.Background <- bgBrush
    bookLadderTabs.Padding <- Thickness(0.0)
    bookLadderTabs.Items.Add(bookTab) |> ignore
    bookLadderTabs.Items.Add(ladderTab) |> ignore
    bookLadderTabs.SelectedIndex <- 0
    let mutable activeTabIdx = 0
    bookLadderTabs.SelectionChanged.Add(fun _ ->
        activeTabIdx <- bookLadderTabs.SelectedIndex)

    let rightGrid = Grid()
    rightGrid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(bookLadderTabs, 0)
    rightGrid.Children.Add(bookLadderTabs)

    // Outer 2-column grid: chart-stack | (L2 + T&S). Horizontal GridSplitter
    // lets the user resize the right panel. Both columns are Star-sized so
    // the splitter redistributes space proportionally (default 3:1).
    let outer = Grid()
    outer.ColumnDefinitions.Add(ColumnDefinition(GridLength(3.0, GridUnitType.Star)))
    outer.ColumnDefinitions.Add(ColumnDefinition(GridLength(4.0)))
    outer.ColumnDefinitions.Add(ColumnDefinition(GridLength(1.0, GridUnitType.Star)))
    let hSplitter = GridSplitter(Width = 4.0, Background = mutedBrush,
                                 VerticalAlignment = VerticalAlignment.Stretch)
    Grid.SetColumn(leftGrid, 0)
    Grid.SetColumn(hSplitter, 1)
    Grid.SetColumn(rightGrid, 2)
    outer.Children.Add(leftGrid)
    outer.Children.Add(hSplitter)
    outer.Children.Add(rightGrid)
    w.Content <- outer

    // ---- UI pump: a single reader task awaits each Snapshot off the worker's
    // outbox and applies it on the UI thread via the Dispatcher.
    let _uiPump =
        task {
            try let mutable lastApplied : Snapshot option = None
                for snap in worker.Outbox.ReadAllAsync(cts.Token) do
                    do! Dispatcher.UIThread.InvokeAsync(fun () ->
                        chartView.ApplyDiff(lastApplied, snap)
                        // Skip the hidden tab's aggregation work — both the
                        // L2 box and the ladder walk every venue's books
                        // per Apply call and that adds up.
                        if activeTabIdx = 0 then bookView.Apply(snap)
                        else ladderView.Apply(snap)
                        tapeView.Apply(snap)
                        lastApplied <- Some snap
                        clockLabel.Text <- fmtClock snap.BucketStartNs
                        suppressSliderHandler <- true
                        slider.Value <- float snap.BucketStartNs
                        suppressSliderHandler <- false
                        refreshResumeBtn ())
            with :? OperationCanceledException -> ()
        }

    // ---- cleanup ----
    // Fire-and-forget cancellation. Worker and uiPump both observe the token
    // and exit on their own; we don't wait on them because the window is
    // already closing and the OS reaps the tasks.
    w.Closed.Add(fun _ -> cts.Cancel())

    w
