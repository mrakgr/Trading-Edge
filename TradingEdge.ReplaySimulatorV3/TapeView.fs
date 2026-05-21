module TradingEdge.ReplaySimulatorV3.TapeView

// Time & sales display. Renders Snapshot.Trades (rolling 1-minute window) as
// a scrolling table with newest at the top. Buy/sell coloring is driven by
// the aggressor side — for a trade with Side = 'A' (resting ask was hit) the
// aggressor was a buyer; Side = 'B' means the aggressor was a seller.
//
// Full rebuild per frame. The queue holds 1 minute of trades — a few hundred
// rows on a busy minute — and Avalonia's ItemsControl handles a Reset cleanly,
// so this is sub-millisecond. If we hit jank we can layer in a diff later.

open System
open System.Collections.ObjectModel
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Templates
open Avalonia.Media
open Avalonia.Layout
open TradingEdge.ReplaySimulatorV3.Time
open TradingEdge.ReplaySimulatorV3.Snapshots
open TradingEdge.ReplaySimulatorV3.Trades
open TradingEdge.ReplaySimulatorV3.Bars
open TradingEdge.ReplaySimulatorV3.Publishers

let private bgBrush     = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush  = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush   = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush  = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))
let private upBrush     = SolidColorBrush(Color.FromRgb(0x2euy, 0xb8uy, 0x88uy))
let private downBrush   = SolidColorBrush(Color.FromRgb(0xe5uy, 0x4buy, 0x4buy))

let private SIDE_ASK : byte = byte 'A'
let private SIDE_BID : byte = byte 'B'

let private fmtTime (utcNs: int64) =
    (toNy utcNs).ToString("HH:mm:ss.fff")

/// Row model exposed to the ItemsControl. Plain strings + a single brush so
/// the template can data-bind without recomputing anything.
type TradeRow() =
    member val Time = "" with get, set
    member val Price = "" with get, set
    member val Size = "" with get, set
    member val Venue = "" with get, set
    member val SideBrush : IBrush = mutedBrush :> IBrush with get, set

let private toRow (t: TradeMsg) : TradeRow =
    let brush =
        if t.Side = SIDE_ASK then upBrush :> IBrush
        elif t.Side = SIDE_BID then downBrush :> IBrush
        else mutedBrush :> IBrush
    TradeRow(
        Time = fmtTime t.TsEvent,
        Price = sprintf "%9.4f" (priceToUsd t.Price),
        Size = (int64 t.Size).ToString("N0"),
        Venue = venueCode (int t.PublisherId),
        SideBrush = brush)

type TapeView() =
    let rows = ObservableCollection<TradeRow>()
    let items = ItemsControl()
    do
        items.ItemsSource <- rows
        items.Background <- panelBrush

    let scroll = ScrollViewer()
    do
        scroll.Content <- items
        scroll.HorizontalScrollBarVisibility <- Primitives.ScrollBarVisibility.Disabled
        scroll.VerticalScrollBarVisibility <- Primitives.ScrollBarVisibility.Auto
        scroll.Background <- panelBrush

    let panel = StackPanel(Orientation = Orientation.Vertical, Background = panelBrush)
    do
        let header = Grid()
        header.ColumnDefinitions.Add(ColumnDefinition(GridLength(100.0)))
        header.ColumnDefinitions.Add(ColumnDefinition(GridLength(80.0)))
        header.ColumnDefinitions.Add(ColumnDefinition(GridLength(70.0)))
        header.ColumnDefinitions.Add(ColumnDefinition(GridLength(60.0)))
        header.Margin <- Thickness(8.0, 4.0, 8.0, 4.0)
        let mk text col =
            let tb = TextBlock(
                        Text = text,
                        Foreground = mutedBrush,
                        FontSize = 11.0,
                        FontWeight = FontWeight.SemiBold)
            Grid.SetColumn(tb, col)
            header.Children.Add(tb)
        mk "TIME"  0
        mk "PRICE" 1
        mk "SIZE"  2
        mk "VENUE" 3
        panel.Children.Add(header)
        panel.Children.Add(scroll)

    // Row template: a 4-column Grid that picks up SideBrush as Foreground.
    do
        let build (row: TradeRow) (_: INameScope) : Control =
            let g = Grid()
            g.ColumnDefinitions.Add(ColumnDefinition(GridLength 100.0))
            g.ColumnDefinitions.Add(ColumnDefinition(GridLength 80.0))
            g.ColumnDefinitions.Add(ColumnDefinition(GridLength 70.0))
            g.ColumnDefinitions.Add(ColumnDefinition(GridLength 60.0))
            let mk (text: string) (col: int) (brush: IBrush) =
                let tb = TextBlock(
                            Text = text,
                            Foreground = brush,
                            FontFamily = FontFamily("monospace"),
                            FontSize = 11.5,
                            Margin = Thickness(0.0, 0.0, 8.0, 0.0))
                Grid.SetColumn(tb, col)
                g.Children.Add(tb)
            mk row.Time  0 mutedBrush
            mk row.Price 1 row.SideBrush
            mk row.Size  2 row.SideBrush
            mk row.Venue 3 textBrush
            g :> Control
        items.ItemTemplate <- FuncDataTemplate<TradeRow>(System.Func<_,_,_> build, true)

    member _.Control = panel :> Control

    member _.Apply(snap: Snapshot) =
        // The queue is oldest-first; we want newest-first. Walk it once into
        // a buffer, then push to ObservableCollection in reverse.
        let buf = ResizeArray<TradeMsg>()
        for t in snap.Trades do buf.Add t
        rows.Clear()
        for i in buf.Count - 1 .. -1 .. 0 do
            rows.Add(toRow buf.[i])
