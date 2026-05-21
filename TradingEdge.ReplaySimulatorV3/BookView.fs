module TradingEdge.ReplaySimulatorV3.BookView

// SMB/Nasdaq-style L2 montage. Two columns: bids (descending price, top of
// book first) on the left, asks (ascending price, top first) on the right.
// Each row is one venue at one price — disaggregated. Sort within a price
// level is by earliest arrival sequence (time-priority FIFO order).
//
// Rows are background-tinted by their price level (top-of-book = brightest;
// fading out as we descend into the book), so it's easy to see size shuffle
// within a single level.
//
// Layout: the bid side is a Grid of 3 columns (Venue | Size | Price); the
// ask side is a Grid of 3 columns (Price | Size | Venue). Each row is a
// Border (for the tint) containing a 3-cell Grid. Avalonia handles measure
// and alignment per column — no monospace-string tricks needed.

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Layout
open TradingEdge.ReplaySimulatorV3.Snapshots
open TradingEdge.ReplaySimulatorV3.Book
open TradingEdge.ReplaySimulatorV3.Bars
open TradingEdge.ReplaySimulatorV3.Publishers

/// How many rows per side. 15 keeps it comparable to retail L2 boxes.
let DEPTH_ROWS = 15

// Dark-theme palette mirroring ChartView/MainWindow.
let private bgBrush     = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush  = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush   = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush  = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))

// Per-price-level row tints. Index 0 = top of book (brightest), fading out.
// Five distinct shades cycled if depth > 5; the user can still tell adjacent
// levels apart by alternating brightness even past row 5.
let private bidTints =
    [|
        Color.FromArgb(0x40uy, 0x2euy, 0xb8uy, 0x88uy)
        Color.FromArgb(0x30uy, 0x2euy, 0xb8uy, 0x88uy)
        Color.FromArgb(0x20uy, 0x2euy, 0xb8uy, 0x88uy)
        Color.FromArgb(0x18uy, 0x2euy, 0xb8uy, 0x88uy)
        Color.FromArgb(0x10uy, 0x2euy, 0xb8uy, 0x88uy)
    |]
    |> Array.map SolidColorBrush

let private askTints =
    [|
        Color.FromArgb(0x40uy, 0xe5uy, 0x4buy, 0x4buy)
        Color.FromArgb(0x30uy, 0xe5uy, 0x4buy, 0x4buy)
        Color.FromArgb(0x20uy, 0xe5uy, 0x4buy, 0x4buy)
        Color.FromArgb(0x18uy, 0xe5uy, 0x4buy, 0x4buy)
        Color.FromArgb(0x10uy, 0xe5uy, 0x4buy, 0x4buy)
    |]
    |> Array.map SolidColorBrush

let private tintFor (tints: SolidColorBrush[]) (levelIdx: int) : SolidColorBrush =
    tints.[min levelIdx (tints.Length - 1)]

/// One row in the projected montage: a single venue's quote at a single price.
/// EarliestSeq is the per-level arrival sequence used for time-priority sort.
type private Row = {
    Price: float
    Size: uint64
    Venue: string
    EarliestSeq: uint64
}

/// Walk one side of one book, projecting (price, totalSize, earliestSeq) per
/// price level for that venue.
let private projectVenue (publisherId: int) (sideMap: Map<int64, Level>) : Row seq =
    seq {
        for kv in sideMap do
            let price = kv.Key
            let lvl = kv.Value
            let mutable totalSize = 0UL
            let mutable earliest = UInt64.MaxValue
            for kv2 in lvl do
                totalSize <- totalSize + uint64 kv2.Value.Size
                if kv2.Key < earliest then earliest <- kv2.Key
            yield {
                Price = priceToUsd price
                Size = totalSize
                Venue = venueCode publisherId
                EarliestSeq = earliest
            }
    }

/// Build the bid rows: walk every venue's bids, project, sort by
/// (price DESC, earliestSeq ASC), take the top N. Top of book is index 0.
let private buildBids (books: Map<int, L3Book>) : Row[] =
    let rows = ResizeArray<Row>()
    for kv in books do
        for r in projectVenue kv.Key kv.Value.Bids do
            rows.Add r
    rows
    |> Seq.sortWith (fun a b ->
        let pc = compare b.Price a.Price        // DESC
        if pc <> 0 then pc else compare a.EarliestSeq b.EarliestSeq)
    |> Seq.truncate DEPTH_ROWS
    |> Array.ofSeq

/// Ask rows: same idea but price ASC.
let private buildAsks (books: Map<int, L3Book>) : Row[] =
    let rows = ResizeArray<Row>()
    for kv in books do
        for r in projectVenue kv.Key kv.Value.Asks do
            rows.Add r
    rows
    |> Seq.sortWith (fun a b ->
        let pc = compare a.Price b.Price        // ASC
        if pc <> 0 then pc else compare a.EarliestSeq b.EarliestSeq)
    |> Seq.truncate DEPTH_ROWS
    |> Array.ofSeq

/// Assign a level index per row based on price runs. Rows sharing a price share
/// a level index; the next distinct price gets index+1. So the tint per row
/// follows price-level, not row position.
let private assignLevels (rows: Row[]) : int[] =
    let levels = Array.zeroCreate rows.Length
    if rows.Length = 0 then levels
    else
        levels.[0] <- 0
        let mutable lvl = 0
        for i in 1 .. rows.Length - 1 do
            if rows.[i].Price <> rows.[i - 1].Price then lvl <- lvl + 1
            levels.[i] <- lvl
        levels

let private rowPad = Thickness(8.0, 1.0, 8.0, 1.0)
let private headerPad = Thickness(8.0, 4.0, 8.0, 4.0)

/// Build a cell TextBlock with consistent styling. Alignment controls
/// horizontal positioning inside its grid column.
let private mkCell (alignment: HorizontalAlignment) =
    TextBlock(
        Foreground = textBrush,
        FontFamily = FontFamily("monospace"),
        FontSize = 15.0,
        Margin = rowPad,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.None,
        ClipToBounds = false)

let private mkHeaderCell (text: string) (alignment: HorizontalAlignment) =
    TextBlock(
        Text = text,
        Foreground = mutedBrush,
        FontSize = 14.0,
        FontWeight = FontWeight.SemiBold,
        Margin = headerPad,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center)

/// Build a side: a vertical Grid with one header row + DEPTH_ROWS body rows.
/// Inside each body row is a 3-column Grid. The side is parameterised by the
/// column order — bids put Venue first, asks put Price first.
type private SideLayout = {
    /// Column widths in the inner 3-column row grid.
    Col0Width: float
    Col1Width: float
    Col2Width: float
    /// Header labels.
    Header0: string
    Header1: string
    Header2: string
    /// Per-cell horizontal alignment.
    Align0: HorizontalAlignment
    Align1: HorizontalAlignment
    Align2: HorizontalAlignment
}

let private widths = {|
    venue = 35.0
    size = 70.0
    price = 50.0
|}

let private bidLayout = {
    Col0Width = widths.venue    // Venue
    Col1Width = widths.size     // Size
    Col2Width = widths.price    // Price
    Header0 = "Venue"
    Header1 = "Size"
    Header2 = "Price"
    Align0 = HorizontalAlignment.Left
    Align1 = HorizontalAlignment.Right
    Align2 = HorizontalAlignment.Right
}

let private askLayout = {
    Col0Width = widths.price    // Price
    Col1Width = widths.size     // Size
    Col2Width = widths.venue    // Venue
    Header0 = "Price"
    Header1 = "Size"
    Header2 = "Venue"
    Align0 = HorizontalAlignment.Left
    Align1 = HorizontalAlignment.Right
    Align2 = HorizontalAlignment.Right
}

type private SideRow = {
    Border: Border
    Cell0: TextBlock
    Cell1: TextBlock
    Cell2: TextBlock
}

let private mkRowGrid (layout: SideLayout) : SideRow * Grid =
    let g = Grid()
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col0Width))
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col1Width))
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col2Width))
    let c0 = mkCell layout.Align0
    let c1 = mkCell layout.Align1
    let c2 = mkCell layout.Align2
    Grid.SetColumn(c0, 0)
    Grid.SetColumn(c1, 1)
    Grid.SetColumn(c2, 2)
    g.Children.Add(c0)
    g.Children.Add(c1)
    g.Children.Add(c2)
    let border = Border(Background = bgBrush, Child = g)
    { Border = border; Cell0 = c0; Cell1 = c1; Cell2 = c2 }, g

let private mkHeaderGrid (layout: SideLayout) : Grid =
    let g = Grid()
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col0Width))
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col1Width))
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength layout.Col2Width))
    let h0 = mkHeaderCell layout.Header0 layout.Align0
    let h1 = mkHeaderCell layout.Header1 layout.Align1
    let h2 = mkHeaderCell layout.Header2 layout.Align2
    Grid.SetColumn(h0, 0)
    Grid.SetColumn(h1, 1)
    Grid.SetColumn(h2, 2)
    g.Children.Add(h0)
    g.Children.Add(h1)
    g.Children.Add(h2)
    g

/// Build the panel for one side. Returns the outer StackPanel and the array
/// of pre-allocated SideRows whose cells we'll mutate per frame.
let private mkSidePanel (layout: SideLayout) : StackPanel * SideRow[] =
    let panel = StackPanel(Orientation = Orientation.Vertical, Background = panelBrush)
    panel.Children.Add(mkHeaderGrid layout)
    let rows =
        Array.init DEPTH_ROWS (fun _ ->
            let row, _grid = mkRowGrid layout
            panel.Children.Add(row.Border)
            row)
    panel, rows

type BookView() =
    let outer = Grid()
    do
        outer.Background <- panelBrush
        outer.ColumnDefinitions.Add(ColumnDefinition(GridLength.Auto))
        outer.ColumnDefinitions.Add(ColumnDefinition(GridLength.Auto))

    let bidPanel, bidRows = mkSidePanel bidLayout
    let askPanel, askRows = mkSidePanel askLayout
    do
        Grid.SetColumn(bidPanel, 0)
        Grid.SetColumn(askPanel, 1)
        outer.Children.Add(bidPanel)
        outer.Children.Add(askPanel)

    let renderBid (row: SideRow) (r: Row) =
        row.Cell0.Text <- r.Venue
        row.Cell1.Text <- (int64 r.Size).ToString("N0")
        row.Cell2.Text <- sprintf "%.2f" r.Price

    let renderAsk (row: SideRow) (r: Row) =
        row.Cell0.Text <- sprintf "%.2f" r.Price
        row.Cell1.Text <- (int64 r.Size).ToString("N0")
        row.Cell2.Text <- r.Venue

    let renderSide
            (rows: Row[])
            (levels: int[])
            (tints: SolidColorBrush[])
            (sideRows: SideRow[])
            (renderOne: SideRow -> Row -> unit) =
        for i in 0 .. DEPTH_ROWS - 1 do
            if i < rows.Length then
                sideRows.[i].Border.Background <- tintFor tints levels.[i]
                renderOne sideRows.[i] rows.[i]
            else
                sideRows.[i].Border.Background <- bgBrush
                sideRows.[i].Cell0.Text <- ""
                sideRows.[i].Cell1.Text <- ""
                sideRows.[i].Cell2.Text <- ""

    member _.Control = outer :> Control

    member _.Apply(snap: Snapshot) =
        let bidRowsData = buildBids snap.Books
        let askRowsData = buildAsks snap.Books
        let bidLevels = assignLevels bidRowsData
        let askLevels = assignLevels askRowsData
        renderSide bidRowsData bidLevels bidTints bidRows renderBid
        renderSide askRowsData askLevels askTints askRows renderAsk
