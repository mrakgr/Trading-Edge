module TradingEdge.ReplaySimulatorV3.MainWindow

// Window + toolbar + chart. A background task awaits Snapshots from the
// worker's outbox; for each frame it marshals onto the UI thread via the
// Dispatcher and applies the diff there. All UI mutation happens on the UI
// thread; the worker computation happens entirely off it.

open System
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
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
    // No keyboard focus on toolbar buttons. Space is reserved for play/pause
    // and arrow keys for seeking; a focused button would consume those via
    // its default Click-on-Space handler.
    b.Focusable <- false
    b

let create
        (symbol: string)
        (date: string)
        (store: SnapshotStore)
        (startCursorNs: int64)
        : Window =
    let w = Window()
    w.Title <- sprintf "ReplaySimulatorV3 — %s %s" symbol date
    w.Background <- bgBrush
    w.WindowStartupLocation <- WindowStartupLocation.Manual
    w.Position <- PixelPoint(0, 0)
    // Size against the primary screen at startup: full height, 60% of width.
    // The WSLg compositor wraps the window in a translucent shadow band that
    // makes edge-resize fiddly, so we just pick a useful default and don't
    // ask the user to drag every time. Falls back to fixed 1400x800 if the
    // screen list isn't populated yet.
    w.Opened.Add(fun _ ->
        let scr =
            match w.Screens with
            | null -> null
            | ss when ss.Primary <> null -> ss.Primary
            | ss when ss.All.Count > 0 -> ss.All.[0]
            | _ -> null
        if scr <> null then
            let bounds = scr.WorkingArea
            let scale = if scr.Scaling > 0.0 then scr.Scaling else 1.0
            w.Width <- float bounds.Width * 0.7 / scale
            w.Height <- float bounds.Height * 0.92 / scale
            w.Position <- PixelPoint(bounds.X, bounds.Y)
        else
            w.Width <- 1400.0
            w.Height <- 800.0)

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
    // Without this wire, panning while paused leaves the Resume button stuck
    // disabled until the next snap arrives (uiPump's per-frame refresh runs
    // only while playing).
    chartView.OnAutoFollowChanged(fun _ -> refreshResumeBtn ())

    let refreshRecenterBtn () =
        if ladderView.IsAutoCenter then
            recenterBtn.BorderBrush <- mutedBrush
            recenterBtn.Foreground <- mutedBrush
            recenterBtn.IsEnabled <- false
        else
            recenterBtn.BorderBrush <- accentBrush
            recenterBtn.Foreground <- accentBrush
            recenterBtn.IsEnabled <- true
    refreshRecenterBtn ()
    // Wire the ladder's auto-center state changes back to the button highlight.
    ladderView.OnAutoCenterChanged(fun _ -> refreshRecenterBtn ())

    // Forward-declared so togglePlayPause can flash the seek-indicator
    // overlay. The actual flash function is wired up below where the overlay
    // is created; we capture it via a ref cell.
    let flashOverlayRef : (string -> unit) ref = ref (fun _ -> ())

    let togglePlayPause () =
        uiPaused <- not uiPaused
        worker.Inbox.TryWrite(SetPaused uiPaused) |> ignore
        refreshPlayBtn ()
        flashOverlayRef.Value (if uiPaused then "||" else "▶")

    playBtn.Click.Add(fun _ -> togglePlayPause ())

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

    // Double-click anywhere on the chart re-engages auto-follow. Same intent
    // as the Resume button but reachable without leaving the chart surface;
    // useful when placing limit orders (you'll often disable centering to
    // pick a price, then want to snap back fast).
    chartView.Chart.DoubleTapped.Add(fun _ ->
        chartView.ResumeAutoFollow ()
        refreshResumeBtn ())

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
    bookLadderTabs.Items.Add(ladderTab) |> ignore
    bookLadderTabs.Items.Add(bookTab) |> ignore
    bookLadderTabs.SelectedIndex <- 0
    let mutable activeTabIdx = 0
    // Lifted out of the uiPump task closure so SelectionChanged can apply the
    // last-known snapshot to a tab that's just become visible — otherwise the
    // newly-visible tab would show stale or empty state until the next frame.
    let mutable lastApplied : Snapshot option = None
    // Tab index 0 = Ladder, 1 = L2. The T&S panel sits inside the L2 tab, so
    // when the ladder tab is showing we skip T&S work too — see the uiPump
    // dispatch. On every tab switch we re-render against the last snapshot so
    // the newly-visible tab is current even if it was skipped while hidden.
    //
    // SelectionChanged is a routed event that bubbles from descendant
    // selectors (the T&S ListBox lives inside the L2 tab and raises its own
    // SelectionChanged when ReplaceAll fires the underlying CollectionChanged
    // Reset). Without the source guard we recurse: tapeView.Apply → Reset →
    // bubbled SelectionChanged → tapeView.Apply → … → StackOverflow on click.
    bookLadderTabs.SelectionChanged.Add(fun e ->
        if obj.ReferenceEquals(e.Source, bookLadderTabs) then
            activeTabIdx <- bookLadderTabs.SelectedIndex
            match lastApplied with
            | Some s ->
                if activeTabIdx = 0 then
                    ladderView.Apply(s)
                else
                    bookView.Apply(s)
                    tapeView.Apply(s)
            | None -> ())

    let rightGrid = Grid()
    rightGrid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
    Grid.SetRow(bookLadderTabs, 0)
    rightGrid.Children.Add(bookLadderTabs)

    // Outer 2-column grid: chart-stack | (L2 + T&S). Horizontal GridSplitter
    // lets the user resize the right panel. Both columns are Star-sized so
    // the splitter redistributes space proportionally (default 3:1).
    let outer = Grid()
    outer.ColumnDefinitions.Add(ColumnDefinition(GridLength(2.0, GridUnitType.Star)))
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

    // Seek indicator: a large translucent "<<" / ">>" glyph flashed over the
    // center of the window on Left/Right keypress. The user records the
    // session for review and the cursor jump alone is too subtle on playback,
    // so this gives an unmistakable visual cue of the seek direction.
    let seekIndicator =
        TextBlock(
            Text = "",
            FontFamily = FontFamily("monospace"),
            FontSize = 120.0,
            FontWeight = FontWeight.Bold,
            Foreground = SolidColorBrush(Color.FromArgb(0xc0uy, 0xf0uy, 0xf4uy, 0xfauy)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0.0)
    let rootPanel = Panel()
    rootPanel.Children.Add(outer)
    rootPanel.Children.Add(seekIndicator)
    w.Content <- rootPanel

    // Linear fade-out via DispatcherTimer: 16ms tick, ~400ms total duration.
    // We restart from full opacity on every press so a rapid sequence of
    // arrow taps keeps the indicator solid until the user pauses.
    let seekFadeStep = 16.0 / 400.0
    let seekFadeTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 16.0)
    seekFadeTimer.Tick.Add(fun _ ->
        let next = seekIndicator.Opacity - seekFadeStep
        if next <= 0.0 then
            seekIndicator.Opacity <- 0.0
            seekFadeTimer.Stop()
        else
            seekIndicator.Opacity <- next)
    let flashSeekIndicator (glyph: string) =
        seekIndicator.Text <- glyph
        seekIndicator.Opacity <- 1.0
        seekFadeTimer.Stop()
        seekFadeTimer.Start()
    // Now that the flash function exists, bind the forward-declared ref so
    // togglePlayPause (above) can flash ▶ / || on every toggle — from keyboard
    // shortcut and from the toolbar button alike.
    do flashOverlayRef.Value <- flashSeekIndicator

    // ---- UI pump: a single reader task awaits each Snapshot off the worker's
    // outbox and applies it on the UI thread via the Dispatcher.
    let _uiPump =
        task {
            try
                for snap in worker.Outbox.ReadAllAsync(cts.Token) do
                    do! Dispatcher.UIThread.InvokeAsync(fun () ->
                        chartView.ApplyDiff(lastApplied, snap)
                        // Skip the hidden tab's aggregation work — L2 and the
                        // ladder both walk every venue's books per Apply, and
                        // the tape rebuilds the visible-trades collection.
                        // The tape lives inside the L2 tab, so it's gated on
                        // the same condition. Tab-switch re-applies the cached
                        // snap so a newly-visible view isn't blank.
                        if activeTabIdx = 0 then
                            ladderView.Apply(snap)
                        else
                            bookView.Apply(snap)
                            tapeView.Apply(snap)
                        lastApplied <- Some snap
                        clockLabel.Text <- fmtClock snap.BucketStartNs
                        suppressSliderHandler <- true
                        slider.Value <- float snap.BucketStartNs
                        suppressSliderHandler <- false
                        refreshResumeBtn ())
            with :? OperationCanceledException -> ()
        }

    // ---- keyboard shortcuts ----
    // Space toggles play/pause; Left/Right seek ±5s. We attach to the
    // tunnel-phase KeyDown so the keys reach us even when the Slider or a
    // Button has focus and would otherwise consume them.
    let SEEK_NS_5S = 5L * 1_000_000_000L
    let onKey (e: KeyEventArgs) =
        match e.Key with
        | Key.Space ->
            togglePlayPause ()
            e.Handled <- true
        | Key.Left ->
            slider.Value <- max slider.Minimum (slider.Value - float SEEK_NS_5S)
            flashSeekIndicator "<<"
            e.Handled <- true
        | Key.Right ->
            slider.Value <- min slider.Maximum (slider.Value + float SEEK_NS_5S)
            flashSeekIndicator ">>"
            e.Handled <- true
        | _ -> ()
    w.AddHandler(
        InputElement.KeyDownEvent,
        EventHandler<KeyEventArgs>(fun _ e -> onKey e),
        RoutingStrategies.Tunnel ||| RoutingStrategies.Bubble)

    // ---- cleanup ----
    // Fire-and-forget cancellation. Worker and uiPump both observe the token
    // and exit on their own; we don't wait on them because the window is
    // already closing and the OS reaps the tasks.
    w.Closed.Add(fun _ -> cts.Cancel())

    w
