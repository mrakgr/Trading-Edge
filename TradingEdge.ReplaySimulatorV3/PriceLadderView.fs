module TradingEdge.ReplaySimulatorV3.PriceLadderView

// Futures-style DOM price ladder. One row per price tick, with seven columns:
//
//   VolumeBar | Price | AskSize | AskTradeSize | MidTradeSize | BidTradeSize | BidSize
//
// All sizes aggregated across venues. Resting Bid/Ask sizes come from
// snap.Books; recent BidTrade/AskTrade/MidTrade sizes come from the side-trade
// dicts on Snapshot, with a UI-side staleness check (entries older than
// TRADE_RESET_NS relative to the snapshot's cursor are blanked). Session-
// cumulative VolumeAtPrice fills the leftmost histogram column.
//
// Auto-center: by default the ladder anchors centerPrice on the inside-market
// midpoint, snapped to the tick. Mouse-wheel detaches; the toolbar (or the
// panel itself) provides a Recenter action that re-engages.
//
// Row count adapts to the panel's vertical size at layout time; pre-allocated
// row controls grow/shrink to fit.

open System
open System.Collections.Generic
open System.Collections.Immutable
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open TradingEdge.ReplaySimulatorV3.Snapshots
open TradingEdge.ReplaySimulatorV3.Book
open TradingEdge.ReplaySimulatorV3.Bars

// ---- palette ----
let private bgBrush      = SolidColorBrush(Color.FromRgb(0x10uy, 0x12uy, 0x18uy))
let private panelBrush   = SolidColorBrush(Color.FromRgb(0x18uy, 0x1cuy, 0x24uy))
let private textBrush    = SolidColorBrush(Color.FromRgb(0xc8uy, 0xccuy, 0xd6uy))
let private mutedBrush   = SolidColorBrush(Color.FromRgb(0x78uy, 0x80uy, 0x90uy))
// Futures DOM convention: bid = red (support level / downside flow), ask =
// green (resistance / upside flow). The opposite of the equity uptick/downtick
// convention used in TapeView.
let private bidBrush     = SolidColorBrush(Color.FromRgb(0xe5uy, 0x4buy, 0x4buy))
let private askBrush     = SolidColorBrush(Color.FromRgb(0x2euy, 0xb8uy, 0x88uy))
let private bidBandBrush = SolidColorBrush(Color.FromArgb(0x40uy, 0xe5uy, 0x4buy, 0x4buy))
let private askBandBrush = SolidColorBrush(Color.FromArgb(0x40uy, 0x2euy, 0xb8uy, 0x88uy))
let private volBarBrush  = SolidColorBrush(Color.FromArgb(0x80uy, 0x4auy, 0x55uy, 0x68uy))
let private midBrush     = SolidColorBrush(Color.FromRgb(0xf2uy, 0xc5uy, 0x5cuy))

/// Tick size: 1 cent expressed in 1e-9 USD.
let TICK_NS : int64 = 10_000_000L

/// Row pixel height — tuned to roughly match the L2 box rows.
let private ROW_HEIGHT = 20.0

/// Snap a price to the nearest tick.
let private snapToTick (p: int64) : int64 =
    let q = p / TICK_NS
    let rem = p - q * TICK_NS
    if rem * 2L >= TICK_NS then (q + 1L) * TICK_NS else q * TICK_NS

/// Walk every venue's side map and sum sizes per price.
let private aggregateSide (books: Map<int, L3Book>) (selector: L3Book -> Map<int64, Level>) : Dictionary<int64, uint64> =
    let result = Dictionary<int64, uint64>()
    for kvBook in books do
        let sideMap = selector kvBook.Value
        for kvLvl in sideMap do
            let price = kvLvl.Key
            let mutable total = 0UL
            for kvOrd in kvLvl.Value do
                total <- total + uint64 kvOrd.Value.Size
            match result.TryGetValue(price) with
            | true, prev -> result.[price] <- prev + total
            | false, _ -> result.[price] <- total
    result

/// Pick a center price snapped to the tick grid.
let private chooseCenter
        (bidByPrice: Dictionary<int64, uint64>)
        (askByPrice: Dictionary<int64, uint64>)
        (lastTradePrice: int64 option)
        : int64 =
    let mutable bestBid = Int64.MinValue
    for k in bidByPrice.Keys do if k > bestBid then bestBid <- k
    let mutable bestAsk = Int64.MaxValue
    for k in askByPrice.Keys do if k < bestAsk then bestAsk <- k
    let center =
        if bestBid > Int64.MinValue && bestAsk < Int64.MaxValue then
            (bestBid + bestAsk) / 2L
        elif bestBid > Int64.MinValue then bestBid
        elif bestAsk < Int64.MaxValue then bestAsk
        else defaultArg lastTradePrice 0L
    snapToTick center

/// One materialized row in the ladder. Cells get mutated per frame.
type private LadderRow = {
    Container: Border
    VolCell: TextBlock
    PriceCell: TextBlock
    AskSizeCell: TextBlock
    AskTradeCell: TextBlock
    MidTradeCell: TextBlock
    BidTradeCell: TextBlock
    BidSizeCell: TextBlock
}

let private mkCell (alignment: HorizontalAlignment) (brush: IBrush) =
    TextBlock(
        Foreground = brush,
        FontFamily = FontFamily("monospace"),
        FontSize = 12.0,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = Thickness(6.0, 0.0, 6.0, 0.0),
        TextTrimming = TextTrimming.None,
        ClipToBounds = false)

let private mkHeaderCell (text: string) (align: HorizontalAlignment) =
    TextBlock(
        Text = text,
        Foreground = mutedBrush,
        FontFamily = FontFamily("monospace"),
        FontSize = 11.0,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = Thickness(6.0, 4.0, 6.0, 4.0))

/// Column-width policy for both header and data rows. Vol is Star (eats
/// leftover horizontal space — that's where the histogram bar will live).
/// The other six columns are Auto and participate in SharedSizeGroup so the
/// header and all data rows agree on per-column widths. Without the groups
/// each row's Grid sizes its Auto columns to *its own* content, drifting out
/// of alignment with the header and adjacent rows. The SharedSizeGroup-scope
/// is set on the outer grid (Grid.IsSharedSizeScope).
let private addColumns (g: Grid) =
    let auto name =
        let c = ColumnDefinition(GridLength.Auto)
        c.SharedSizeGroup <- name
        g.ColumnDefinitions.Add(c)
    g.ColumnDefinitions.Add(ColumnDefinition(GridLength(1.0, GridUnitType.Star)))   // 0 Vol
    auto "ladder_price"  // 1 Price
    auto "ladder_bid"    // 2 BidSize
    auto "ladder_bidT"   // 3 BidTrade
    auto "ladder_mid"    // 4 MidTrade
    auto "ladder_askT"   // 5 AskTrade
    auto "ladder_ask"    // 6 AskSize

let private mkRow () : LadderRow =
    let g = Grid()
    g.Height <- ROW_HEIGHT
    addColumns g
    let cVol      = mkCell HorizontalAlignment.Right mutedBrush
    let cPrice    = mkCell HorizontalAlignment.Center textBrush
    let cAskSize  = mkCell HorizontalAlignment.Right askBrush
    let cAskTrade = mkCell HorizontalAlignment.Right askBrush
    let cMidTrade = mkCell HorizontalAlignment.Center midBrush
    let cBidTrade = mkCell HorizontalAlignment.Right bidBrush
    let cBidSize  = mkCell HorizontalAlignment.Right bidBrush
    Grid.SetColumn(cVol,      0)
    Grid.SetColumn(cPrice,    1)
    Grid.SetColumn(cBidSize,  2)
    Grid.SetColumn(cBidTrade, 3)
    Grid.SetColumn(cMidTrade, 4)
    Grid.SetColumn(cAskTrade, 5)
    Grid.SetColumn(cAskSize,  6)
    g.Children.Add(cVol)
    g.Children.Add(cPrice)
    g.Children.Add(cAskSize)
    g.Children.Add(cAskTrade)
    g.Children.Add(cMidTrade)
    g.Children.Add(cBidTrade)
    g.Children.Add(cBidSize)
    let border = Border(Background = bgBrush, Child = g)
    {
        Container = border
        VolCell = cVol
        PriceCell = cPrice
        AskSizeCell = cAskSize
        AskTradeCell = cAskTrade
        MidTradeCell = cMidTrade
        BidTradeCell = cBidTrade
        BidSizeCell = cBidSize
    }

let private mkHeaderRow () : Grid =
    let g = Grid()
    g.Height <- ROW_HEIGHT
    addColumns g
    let h0 = mkHeaderCell "Vol"   HorizontalAlignment.Right
    let h1 = mkHeaderCell "Price" HorizontalAlignment.Center
    let h2 = mkHeaderCell "Bid"   HorizontalAlignment.Right
    let h3 = mkHeaderCell "Bid T" HorizontalAlignment.Right
    let h4 = mkHeaderCell "Mid"   HorizontalAlignment.Center
    let h5 = mkHeaderCell "Ask T" HorizontalAlignment.Right
    let h6 = mkHeaderCell "Ask"   HorizontalAlignment.Right
    Grid.SetColumn(h0, 0)
    Grid.SetColumn(h1, 1)
    Grid.SetColumn(h2, 2)
    Grid.SetColumn(h3, 3)
    Grid.SetColumn(h4, 4)
    Grid.SetColumn(h5, 5)
    Grid.SetColumn(h6, 6)
    g.Children.Add(h0)
    g.Children.Add(h1)
    g.Children.Add(h2)
    g.Children.Add(h3)
    g.Children.Add(h4)
    g.Children.Add(h5)
    g.Children.Add(h6)
    g

let private formatSize (s: uint64) : string =
    if s = 0UL then "" else (int64 s).ToString("N0")

let private formatPrice (p: int64) : string =
    sprintf "%.2f" (priceToUsd p)

/// Look up (size, lastTs) and blank if stale relative to cursorNs.
let private readTradeCell
        (dict: TradeAtPrice)
        (price: int64)
        (cursorNs: int64)
        : uint64 =
    match dict.TryGetValue(price) with
    | true, struct (size, lastTs) ->
        if cursorNs - lastTs > TRADE_RESET_NS then 0UL else size
    | false, _ -> 0UL

type PriceLadderView() =
    let outerGrid = Grid(Background = panelBrush)
    do
        outerGrid.RowDefinitions.Add(RowDefinition(GridLength.Auto))             // header
        outerGrid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star))) // rows
        // Enable shared-size so the header grid and per-row grids agree on
        // column widths across all named groups (ladder_price, ladder_ask,
        // ladder_askT, ladder_mid, ladder_bidT, ladder_bid).
        Grid.SetIsSharedSizeScope(outerGrid, true)

    let headerRow = mkHeaderRow ()
    do
        Grid.SetRow(headerRow, 0)
        outerGrid.Children.Add(headerRow)

    // Container for the per-tick rows. We use a StackPanel so we can simply
    // Children.Add / Children.RemoveAt without manually positioning each row.
    let rowsPanel = StackPanel(Orientation = Orientation.Vertical, Background = panelBrush)
    do
        Grid.SetRow(rowsPanel, 1)
        outerGrid.Children.Add(rowsPanel)

    // Pool of row controls grown on demand. We never destroy rows once
    // allocated; if the panel shrinks we just stop appending past the
    // visible count.
    let rowPool = ResizeArray<LadderRow>()

    let mutable autoCenter = true
    // Manual center price used when autoCenter is false. Snapped to tick.
    let mutable manualCenter : int64 = 0L
    // Last computed inside-market center, kept so Recenter() can target it
    // even when no Apply has just run.
    let mutable lastInsideCenter : int64 = 0L
    // Last snapshot applied. Cached so the mouse-wheel handler and Recenter
    // can re-render against the current data when the worker is paused (no
    // new frames flowing in).
    let mutable lastSnap : Snapshot option = None
    // Callback to detach-state changes so MainWindow can highlight the
    // Recenter button.
    let mutable onDetachChanged : bool -> unit = ignore

    let ensureRowCount (n: int) =
        while rowPool.Count < n do
            let r = mkRow ()
            rowPool.Add(r)
            rowsPanel.Children.Add(r.Container)
        // Hide extra rows beyond n (we keep them in the pool).
        for i in 0 .. rowPool.Count - 1 do
            rowPool.[i].Container.IsVisible <- i < n

    /// Best bid / best ask snapped to the tick grid. Used to highlight the
    /// inside-market rows.
    let topOfBook (bidByPrice: Dictionary<int64, uint64>) (askByPrice: Dictionary<int64, uint64>) =
        let mutable bestBid = Int64.MinValue
        for k in bidByPrice.Keys do if k > bestBid then bestBid <- k
        let mutable bestAsk = Int64.MaxValue
        for k in askByPrice.Keys do if k < bestAsk then bestAsk <- k
        bestBid, bestAsk

    /// Render against a given snapshot. Called from Apply (worker pumps a new
    /// frame), and from the wheel/Recenter handlers (so the view updates even
    /// when the worker is paused).
    let render (snap: Snapshot) =
        let bidByPrice = aggregateSide snap.Books (fun b -> b.Bids)
        let askByPrice = aggregateSide snap.Books (fun b -> b.Asks)

        let bestBid, bestAsk = topOfBook bidByPrice askByPrice

        let insideCenter = chooseCenter bidByPrice askByPrice None
        lastInsideCenter <- insideCenter
        let centerPrice = if autoCenter then insideCenter else manualCenter

        // Pick row count to fit the panel. We rely on rowsPanel.Bounds which
        // Avalonia keeps updated post-layout.
        let availableHeight = rowsPanel.Bounds.Height
        let rowCount =
            if availableHeight < ROW_HEIGHT then 1
            else int (availableHeight / ROW_HEIGHT) |> max 1
        ensureRowCount rowCount

        // Walk rows top-to-bottom = highest price first. The center sits at
        // index rowCount/2 (or one above for even rowCount).
        let half = rowCount / 2
        let cursorNs = snap.BucketStartNs

        for i in 0 .. rowCount - 1 do
            let price = centerPrice + int64 (half - i) * TICK_NS
            let row = rowPool.[i]

            // Background band for inside-market: best ask gets ask tint, best
            // bid gets bid tint, neither otherwise.
            let bg =
                if price = bestAsk then askBandBrush :> IBrush
                elif price = bestBid then bidBandBrush :> IBrush
                else bgBrush :> IBrush
            row.Container.Background <- bg

            row.PriceCell.Text <- formatPrice price

            // Resting sizes (asks shown only at/above bestBid+tick, bids only
            // at/below bestAsk-tick — i.e. don't display "bid resting above
            // best ask"; that should never happen in a clean book, but the
            // aggregator could see crossed books briefly).
            let askSz =
                match askByPrice.TryGetValue(price) with
                | true, v -> v
                | false, _ -> 0UL
            let bidSz =
                match bidByPrice.TryGetValue(price) with
                | true, v -> v
                | false, _ -> 0UL
            row.AskSizeCell.Text <- formatSize askSz
            row.BidSizeCell.Text <- formatSize bidSz

            // Recent trade columns with staleness check.
            row.AskTradeCell.Text <- formatSize (readTradeCell snap.AskTradeAtPrice price cursorNs)
            row.MidTradeCell.Text <- formatSize (readTradeCell snap.MidTradeAtPrice price cursorNs)
            row.BidTradeCell.Text <- formatSize (readTradeCell snap.BidTradeAtPrice price cursorNs)

            // Volume column. Plain text at a consistent foreground — the
            // alpha-fade-by-frac scheme made low-volume rows nearly invisible
            // even when they had real activity. Magnitude encoding belongs
            // in a histogram bar (not yet implemented).
            let vol =
                match snap.VolumeAtPrice.TryGetValue(price) with
                | true, v -> v
                | false, _ -> 0UL
            row.VolCell.Text <- formatSize vol
            row.VolCell.Foreground <- textBrush

    // First-paint problem: when the user starts on a different tab, the
    // ladder's rowsPanel has no allocated height until the tab is first
    // activated. At that point SelectionChanged fires Apply(lastSnap), but
    // rowsPanel.Bounds.Height is still 0 — so rowCount comes out as 0 and
    // nothing materializes. The next snap (when the user scrolls or plays)
    // recovers it, but on a paused ladder you'd never see anything until
    // then. SizeChanged fires once the tab's layout pass runs, so we
    // re-render against the cached snap and the rows pop in.
    do
        rowsPanel.SizeChanged.Add(fun _ ->
            match lastSnap with
            | Some s -> render s
            | None -> ())

    // Mouse-wheel detaches and pans by N ticks per notch. When paused we
    // re-render against the cached snapshot so the view tracks the pan.
    do
        outerGrid.PointerWheelChanged.Add(fun e ->
            let dy = e.Delta.Y
            if dy <> 0.0 then
                let wasDetached = not autoCenter
                if autoCenter then manualCenter <- lastInsideCenter
                let step = if dy > 0.0 then 1L else -1L
                manualCenter <- snapToTick (manualCenter + step * TICK_NS)
                autoCenter <- false
                if not wasDetached then onDetachChanged false
                match lastSnap with
                | Some s -> render s
                | None -> ()
                e.Handled <- true)

    member _.Control = outerGrid :> Control

    member _.IsAutoCenter = autoCenter

    /// Wire a callback fired whenever auto-center engages/disengages. The
    /// argument is the new IsAutoCenter value. Used by MainWindow to toggle
    /// the Recenter button highlight.
    member _.OnAutoCenterChanged(cb: bool -> unit) =
        onDetachChanged <- cb

    member _.Recenter() =
        let wasDetached = not autoCenter
        autoCenter <- true
        manualCenter <- lastInsideCenter
        if wasDetached then onDetachChanged true
        match lastSnap with
        | Some s -> render s
        | None -> ()

    member _.Apply(snap: Snapshot) =
        lastSnap <- Some snap
        render snap
