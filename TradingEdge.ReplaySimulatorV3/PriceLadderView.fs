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

/// Streak fade window. A trade-cell tint starts at full intensity when the
/// print lands and linearly decays to zero over this interval. Tighter than
/// TRADE_RESET_NS (the staleness/blanking window) because the fade is meant
/// to convey *velocity* — what just happened in the last second — while the
/// numeric value should stay legible for up to a minute.
let private STREAK_FADE_NS : int64 = 1_000_000_000L

/// Max alpha of the streak tint, applied at age = 0.
let private STREAK_MAX_ALPHA : byte = 0xA0uy

/// Row pixel height — tuned to comfortably hold the 14pt monospace cell text.
let private ROW_HEIGHT = 24.0

/// Cell font size. The ladder is the primary read surface during replay, so
/// bias toward legibility over information density.
let private CELL_FONT_SIZE = 14.0

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

/// One materialized row in the ladder. Cells get mutated per frame. Trade
/// cells are exposed as (border, textblock) pairs so the streak-fade can paint
/// the full cell rect via the border while the text content/foreground stay
/// on the textblock. The Vol cell is a (container, bar, text) triple: the bar
/// is a right-anchored Border whose Width we mutate to draw the histogram,
/// and the text overlays on top.
type private LadderRow = {
    Container: Border
    VolContainer: Grid
    VolBar: Border
    VolText: TextBlock
    PriceCell: TextBlock
    AskSizeCell: TextBlock
    AskTradeBorder: Border
    AskTradeText: TextBlock
    MidTradeBorder: Border
    MidTradeText: TextBlock
    BidTradeBorder: Border
    BidTradeText: TextBlock
    BidSizeCell: TextBlock
}

let private mkCell (alignment: HorizontalAlignment) (brush: IBrush) =
    TextBlock(
        Foreground = brush,
        FontFamily = FontFamily("monospace"),
        FontSize = CELL_FONT_SIZE,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = Thickness(6.0, 0.0, 6.0, 0.0),
        TextTrimming = TextTrimming.None,
        ClipToBounds = false)

/// Trade cells need a Border wrapper so the streak-fade tint covers the full
/// column slice rather than just the text glyph rect. The TextBlock inside
/// keeps its alignment so the number sits where the eye expects.
let private mkTradeCell (alignment: HorizontalAlignment) (brush: IBrush) =
    let tb =
        TextBlock(
            Foreground = brush,
            FontFamily = FontFamily("monospace"),
            FontSize = CELL_FONT_SIZE,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = Thickness(6.0, 0.0, 6.0, 0.0),
            TextTrimming = TextTrimming.None,
            ClipToBounds = false)
    let border = Border(Child = tb)
    border, tb

let private mkHeaderCell (text: string) (align: HorizontalAlignment) =
    TextBlock(
        Text = text,
        Foreground = mutedBrush,
        FontFamily = FontFamily("monospace"),
        FontSize = 12.0,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = Thickness(6.0, 4.0, 6.0, 4.0))

/// Column-width policy for both header and data rows. All columns are Star-
/// weighted so the ladder expands horizontally with the panel — drag the
/// vertical splitter and every data column gets wider, not just Vol.
///
/// Sibling Grids stretched to the same parent width compute Star widths
/// identically, so header and all data rows agree on per-column pixel widths
/// without needing SharedSizeGroup (which only works on Auto columns anyway).
///
/// Star weights are tuned by hand: Vol gets a generous slice for the future
/// histogram, Price is wide enough for "999.99", trade/size columns get ~1.2
/// each so they comfortably fit 6-7 digit volumes on high-flow names.
let private addColumns (g: Grid) =
    let star (w: float) =
        g.ColumnDefinitions.Add(ColumnDefinition(GridLength(w, GridUnitType.Star)))
    star 1.5  // 0 Vol
    star 1.2  // 1 Price
    star 1.2  // 2 BidSize
    star 1.2  // 3 BidTrade
    star 1.0  // 4 MidTrade
    star 1.2  // 5 AskTrade
    star 1.2  // 6 AskSize

/// Volume-profile histogram bar fill. Subtle slate-ish blue at moderate alpha
/// — visible against the dark row background but not loud enough to compete
/// with the streak tints in the trade columns.
let private volHistBrush = SolidColorBrush(Color.FromArgb(0x70uy, 0x4auy, 0x6auy, 0x90uy))

let private mkRow () : LadderRow =
    let g = Grid()
    g.Height <- ROW_HEIGHT
    addColumns g

    // Vol column: a stretching Grid that hosts the histogram bar (right-
    // anchored Border whose Width we mutate to draw the proportion) and the
    // numeric text overlaid on top.
    let volContainer = Grid(HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch)
    let volBar = Border(
                    Background = volHistBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 0.0)
    let volText = mkCell HorizontalAlignment.Right mutedBrush
    volContainer.Children.Add(volBar)
    volContainer.Children.Add(volText)

    let cPrice    = mkCell HorizontalAlignment.Center textBrush
    let cAskSize  = mkCell HorizontalAlignment.Right askBrush
    let askBorder, askText = mkTradeCell HorizontalAlignment.Right askBrush
    let midBorder, midText = mkTradeCell HorizontalAlignment.Center midBrush
    let bidBorder, bidText = mkTradeCell HorizontalAlignment.Right bidBrush
    let cBidSize  = mkCell HorizontalAlignment.Right bidBrush
    Grid.SetColumn(volContainer, 0)
    Grid.SetColumn(cPrice,    1)
    Grid.SetColumn(cBidSize,  2)
    Grid.SetColumn(bidBorder, 3)
    Grid.SetColumn(midBorder, 4)
    Grid.SetColumn(askBorder, 5)
    Grid.SetColumn(cAskSize,  6)
    g.Children.Add(volContainer)
    g.Children.Add(cPrice)
    g.Children.Add(cAskSize)
    g.Children.Add(askBorder)
    g.Children.Add(midBorder)
    g.Children.Add(bidBorder)
    g.Children.Add(cBidSize)
    let outer = Border(Background = bgBrush, Child = g)
    {
        Container = outer
        VolContainer = volContainer
        VolBar = volBar
        VolText = volText
        PriceCell = cPrice
        AskSizeCell = cAskSize
        AskTradeBorder = askBorder
        AskTradeText = askText
        MidTradeBorder = midBorder
        MidTradeText = midText
        BidTradeBorder = bidBorder
        BidTradeText = bidText
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

/// Look up (size, lastTs) and return (size, freshness). Freshness is 1.0 for
/// a print that just landed and decays linearly to 0.0 at TRADE_RESET_NS; past
/// that horizon it stays at 0.0 (invisible text), but the size is still the
/// real accumulated value — no hard blank. The fade alone is what hides stale
/// prints, gradually rather than with a pop.
let private readTradeCell
        (dict: TradeAtPrice)
        (price: int64)
        (cursorNs: int64)
        : uint64 * float =
    match dict.TryGetValue(price) with
    | true, struct (size, lastTs) ->
        let age = cursorNs - lastTs
        if age <= 0L then size, 1.0
        elif age >= TRADE_RESET_NS then size, 0.0
        else size, 1.0 - float age / float TRADE_RESET_NS
    | false, _ -> 0UL, 0.0

let private fadedBrush (baseBrush: SolidColorBrush) (freshness: float) : IBrush =
    let f = max 0.0 (min 1.0 freshness)
    let c = baseBrush.Color
    SolidColorBrush(Color.FromArgb(byte (f * 255.0), c.R, c.G, c.B)) :> IBrush

/// Streak tint brush for a trade cell. Returns null when the cell has no
/// recent print (or the print is older than STREAK_FADE_NS). Alpha decays
/// linearly from STREAK_MAX_ALPHA at age=0 down to 0 at age=STREAK_FADE_NS.
let private streakBrush
        (dict: TradeAtPrice)
        (price: int64)
        (cursorNs: int64)
        (r: byte) (g: byte) (b: byte)
        : IBrush =
    match dict.TryGetValue(price) with
    | true, struct (_, lastTs) ->
        let age = cursorNs - lastTs
        if age < 0L || age >= STREAK_FADE_NS then null
        else
            let frac = 1.0 - float age / float STREAK_FADE_NS
            let alpha = byte (float STREAK_MAX_ALPHA * frac)
            SolidColorBrush(Color.FromArgb(alpha, r, g, b)) :> IBrush
    | false, _ -> null

type PriceLadderView() =
    let outerGrid = Grid(Background = panelBrush)
    do
        outerGrid.RowDefinitions.Add(RowDefinition(GridLength.Auto))             // header
        outerGrid.RowDefinitions.Add(RowDefinition(GridLength(1.0, GridUnitType.Star))) // rows

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

    // Forward-declared so the per-row SizeChanged handler can re-render
    // against the cached snap once the Vol container gets a real width. The
    // actual `render` lambda is defined below; we capture it via a ref cell.
    let renderRef : (Snapshot -> unit) ref = ref (fun _ -> ())

    let ensureRowCount (n: int) =
        while rowPool.Count < n do
            let r = mkRow ()
            // The Vol container has Bounds.Width = 0 until Avalonia's layout
            // pass runs, which means the very first render writes a bar Width
            // of 0. Re-render when the container's actual size lands, so the
            // bars pop in without needing the user to scroll first.
            r.VolContainer.SizeChanged.Add(fun _ ->
                match lastSnap with
                | Some s -> renderRef.Value s
                | None -> ())
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

        // Session-max volume across every traded tick. Used to normalize each
        // row's histogram bar — biggest level in the session pegs the bar to
        // the full column width; everything else scales proportionally. Stable
        // across pans (no rescale flicker), but means an empty session starts
        // with nothing showing, which is fine.
        let mutable sessionMaxVol = 0UL
        for kv in snap.VolumeAtPrice do
            if kv.Value > sessionMaxVol then sessionMaxVol <- kv.Value

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

            // Recent trade columns with staleness check + age-proportional
            // text fade. Foreground alpha decays from full at age=0 down to
            // TRADE_TEXT_MIN_ALPHA at age≈TRADE_RESET_NS, then the size is
            // blanked entirely. Gives the eye a gradient of "recency" instead
            // of a hard pop-to-zero at the 1m mark.
            let askSize, askFresh = readTradeCell snap.AskTradeAtPrice price cursorNs
            let midSize, midFresh = readTradeCell snap.MidTradeAtPrice price cursorNs
            let bidSize, bidFresh = readTradeCell snap.BidTradeAtPrice price cursorNs
            row.AskTradeText.Text <- formatSize askSize
            row.MidTradeText.Text <- formatSize midSize
            row.BidTradeText.Text <- formatSize bidSize
            row.AskTradeText.Foreground <- fadedBrush askBrush askFresh
            row.MidTradeText.Foreground <- fadedBrush midBrush midFresh
            row.BidTradeText.Foreground <- fadedBrush bidBrush bidFresh

            // Streak fade: per-cell background tint that decays linearly from
            // full intensity to zero over STREAK_FADE_NS. The tint paints the
            // Border (full grid-column slice) rather than the TextBlock (just
            // the glyph rect), so the eye gets a solid color block to track.
            row.AskTradeBorder.Background <- streakBrush snap.AskTradeAtPrice price cursorNs 0x2euy 0xb8uy 0x88uy
            row.MidTradeBorder.Background <- streakBrush snap.MidTradeAtPrice price cursorNs 0xf2uy 0xc5uy 0x5cuy
            row.BidTradeBorder.Background <- streakBrush snap.BidTradeAtPrice price cursorNs 0xe5uy 0x4buy 0x4buy

            // Volume column: numeric session-cumulative VolumeAtPrice with a
            // histogram bar behind it. The bar grows right-to-left, length
            // proportional to vol / sessionMaxVol. Container.Bounds.Width is
            // populated post-layout; on first paint it may be 0 and the bar
            // will pop in on the next render cycle (SizeChanged handler).
            let vol =
                match snap.VolumeAtPrice.TryGetValue(price) with
                | true, v -> v
                | false, _ -> 0UL
            row.VolText.Text <- formatSize vol
            row.VolText.Foreground <- textBrush
            let containerW = row.VolContainer.Bounds.Width
            let barW =
                if sessionMaxVol = 0UL || containerW <= 0.0 then 0.0
                else containerW * float vol / float sessionMaxVol
            row.VolBar.Width <- barW

    // Bind the forward-declared render ref now that render exists. The Vol-
    // container SizeChanged handler in ensureRowCount uses it to re-render
    // when a row's column width lands post-layout (otherwise the first paint
    // shows zero-width bars until the user scrolls).
    do renderRef.Value <- render

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
