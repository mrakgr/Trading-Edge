module TradingEdge.ReplaySimulatorV3.TapeView

// Time & sales display. Renders Snapshot.Trades (rolling 1-minute window) as
// a virtualized scrolling list with newest at the top. Buy/sell coloring is
// driven by the aggressor side — for a trade with Side = 'A' (resting ask
// was hit) the aggressor was a buyer; Side = 'B' means the aggressor was
// a seller.
//
// Performance design:
//   * Backing store is a BulkObservableList<TradeRow> — an ObservableCollection
//     subclass that batches a full replacement into a single Reset event
//     instead of N Add events.
//   * Display is a ListBox over a VirtualizingStackPanel — only the rows
//     currently inside the viewport are materialized as Avalonia controls.
//     Off-screen rows live in the data layer only.
//
// Net effect: a few hundred trades per frame turns into one Reset event
// and a viewport-sized re-bind (~30 rows), regardless of how many trades
// are in the rolling window.

open System
open System.Collections.ObjectModel
open System.Collections.Specialized
open System.ComponentModel
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Templates
open Avalonia.Media
open Avalonia.Styling
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
// Same gold used for Mid in the ladder. Off-book / dark / TRF prints get
// this color in both views so the eye learns to flag them as "weird flow".
let private midBrush    = SolidColorBrush(Color.FromRgb(0xf2uy, 0xc5uy, 0x5cuy))
let private timeBrush   = SolidColorBrush(Color.FromRgb(0x5cuy, 0xa8uy, 0xd0uy))

let private SIDE_ASK : byte = byte 'A'
let private SIDE_BID : byte = byte 'B'

let private fmtTime (utcNs: int64) =
    (toNy utcNs).ToString("HH:mm:ss.fff")

/// Row model exposed to the ListBox. Plain strings + a single brush so
/// the template can data-bind without recomputing anything.
type TradeRow() =
    member val Time = "" with get, set
    member val Price = "" with get, set
    member val Size = "" with get, set
    member val Venue = "" with get, set
    member val SideBrush : IBrush = mutedBrush :> IBrush with get, set

let private toRow (t: TradeMsg) : TradeRow =
    // DBN Side is the aggressor side on a trade record. 'B' = buyer hit an
    // ask → uptick coloring; 'A' = seller hit a bid → downtick coloring;
    // 'N' = off-book / dark / TRF print → mid color (matches the ladder).
    let brush =
        if t.Side = SIDE_BID then upBrush :> IBrush
        elif t.Side = SIDE_ASK then downBrush :> IBrush
        else midBrush :> IBrush
    TradeRow(
        Time = fmtTime t.TsEvent,
        Price = sprintf "%.4f" (priceToUsd t.Price),
        Size = (int64 t.Size).ToString("N0"),
        Venue = venueCode (int t.PublisherId),
        SideBrush = brush)

/// ObservableCollection that supports bulk replacement with a single
/// Reset event. The standard ObservableCollection raises N events for an
/// N-item refill; a ListBox subscribing to those events recomputes layout
/// per-event. A single Reset means the ListBox re-binds its viewport once.
type BulkObservableList<'T>() =
    inherit ObservableCollection<'T>()

    /// Clear and refill the collection with one Reset notification.
    member this.ReplaceAll(items: seq<'T>) =
        this.Items.Clear()
        for x in items do this.Items.Add(x)
        this.OnCollectionChanged(NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset))
        this.OnPropertyChanged(PropertyChangedEventArgs("Count"))
        this.OnPropertyChanged(PropertyChangedEventArgs("Item[]"))

/// Style that zeros out the default Fluent-theme padding on ListBoxItem so
/// tape rows render compactly. Selection / hover visuals are left at the
/// theme defaults — the rows are clickable like any other ListBox.
let private mkListBoxItemStyles () : Style[] =
    let asListBoxItem = System.Func<Selector, Selector>(fun s -> s.OfType(typeof<ListBoxItem>))
    let baseStyle = Style(asListBoxItem)
    baseStyle.Setters.Add(Setter(ListBoxItem.PaddingProperty, Thickness(0.0)))
    baseStyle.Setters.Add(Setter(ListBoxItem.MinHeightProperty, 0.0))
    [| baseStyle |]

type TapeView() =
    let rows = BulkObservableList<TradeRow>()
    let listBox = ListBox()
    do
        listBox.ItemsSource <- rows
        listBox.Background <- panelBrush
        listBox.BorderThickness <- Thickness(0.0)
        listBox.Padding <- Thickness(0.0)
        // Always show the vertical scrollbar so the user can drag through
        // the rolling window when paused.
        ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Visible)
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled)
        // Compact, non-interactive rows.
        for s in mkListBoxItemStyles () do listBox.Styles.Add(s)
        // Explicit virtualization: only viewport rows are materialized.
        listBox.ItemsPanel <-
            FuncTemplate<Panel>(fun () -> VirtualizingStackPanel() :> Panel)

    // Row template: 4-column Grid. Cells inherit the row's SideBrush for
    // price/size; time stays muted, venue uses the default text color.
    do
        let build (row: TradeRow) (_: INameScope) : Control =
            let g = Grid()
            let cTime  = ColumnDefinition(GridLength.Auto)
            let cPrice = ColumnDefinition(GridLength(1.0, GridUnitType.Star))
            let cSize  = ColumnDefinition(GridLength(1.0, GridUnitType.Star))
            let cVenue = ColumnDefinition(GridLength.Auto)
            cTime.SharedSizeGroup <- "tape_time"
            cVenue.SharedSizeGroup <- "tape_venue"
            g.ColumnDefinitions.Add(cTime)
            g.ColumnDefinitions.Add(cPrice)
            g.ColumnDefinitions.Add(cSize)
            g.ColumnDefinitions.Add(cVenue)
            let mk (text: string) (col: int) (brush: IBrush) (align: TextAlignment) =
                let tb = TextBlock(
                            Text = text,
                            Foreground = brush,
                            FontFamily = FontFamily("monospace"),
                            FontSize = 13.0,
                            TextAlignment = align,
                            Margin = Thickness(0.0, 0.0, 8.0, 0.0))
                Grid.SetColumn(tb, col)
                g.Children.Add(tb)
            mk row.Time  0 timeBrush     TextAlignment.Left
            mk row.Price 1 row.SideBrush TextAlignment.Right
            mk row.Size  2 row.SideBrush TextAlignment.Right
            mk row.Venue 3 textBrush     TextAlignment.Left
            g :> Control
        listBox.ItemTemplate <- FuncDataTemplate<TradeRow>(System.Func<_,_,_> build, true)

    // Grid (not StackPanel) so the ListBox gets a bounded height — required
    // for virtualization to actually engage and for the scrollbar to appear.
    let panel = Grid(Background = panelBrush)
    do
        // SharedSizeScope lets the header Grid and the per-row Grids agree on
        // the Time/Venue column widths via SharedSizeGroup names. Without this
        // the header sizes its Auto columns to its own text ("TIME"/"VENUE")
        // while rows size theirs to the trade text — misalignment.
        Grid.SetIsSharedSizeScope(panel, true)
        panel.RowDefinitions.Add(RowDefinition(GridLength.Auto))
        panel.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star)))
        let header = Grid()
        let hTime  = ColumnDefinition(GridLength.Auto)
        let hPrice = ColumnDefinition(GridLength(1.0, GridUnitType.Star))
        let hSize  = ColumnDefinition(GridLength(1.0, GridUnitType.Star))
        let hVenue = ColumnDefinition(GridLength.Auto)
        hTime.SharedSizeGroup <- "tape_time"
        hVenue.SharedSizeGroup <- "tape_venue"
        header.ColumnDefinitions.Add(hTime)
        header.ColumnDefinitions.Add(hPrice)
        header.ColumnDefinitions.Add(hSize)
        header.ColumnDefinitions.Add(hVenue)
        // Outer header Margin removed: with the ListBox at zero padding the
        // rows start flush left, so the header must too. Per-cell right pad
        // mirrors the row template so column edges line up.
        let mk (text: string) (col: int) (align: TextAlignment) =
            let tb = TextBlock(
                        Text = text,
                        Foreground = mutedBrush,
                        FontFamily = FontFamily("monospace"),
                        FontSize = 11.0,
                        FontWeight = FontWeight.SemiBold,
                        TextAlignment = align,
                        Margin = Thickness(0.0, 2.0, 8.0, 2.0))
            Grid.SetColumn(tb, col)
            header.Children.Add(tb)
        mk "TIME"  0 TextAlignment.Left
        mk "PRICE" 1 TextAlignment.Right
        mk "SIZE"  2 TextAlignment.Right
        mk "VENUE" 3 TextAlignment.Left
        Grid.SetRow(header, 0)
        Grid.SetRow(listBox, 1)
        panel.Children.Add(header)
        panel.Children.Add(listBox)

    // Buffer reused across frames to avoid per-frame ResizeArray allocation.
    let buf = ResizeArray<TradeMsg>()

    member _.Control = panel :> Control

    member _.Apply(snap: Snapshot) =
        // Queue is oldest-first; we want newest-first in the display.
        buf.Clear()
        for t in snap.Trades do buf.Add t
        let newest =
            seq {
                for i in buf.Count - 1 .. -1 .. 0 do
                    yield toRow buf.[i]
            }
        rows.ReplaceAll(newest)
