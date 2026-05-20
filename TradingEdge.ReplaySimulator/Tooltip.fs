module TradingEdge.ReplaySimulator.Tooltip

// Custom tooltip for the price chart. The default SKDefaultTooltip can't render
// multi-line text in a single row (the bundled v2.0.4 LabelGeometry tokenizer
// emits '\n' as part of the surrounding token rather than as its own line-break
// sentinel, so the newline gets shaped as a missing-glyph box).
//
// We work around that by composing the OHLC values as four sibling rows in a
// TableLayout — same as the built-in tooltip uses for multi-series — but for
// the *single* candle series, we emit one row per OHLC field.

open System
open System.Collections.Generic
open LiveChartsCore
open LiveChartsCore.Drawing
open LiveChartsCore.Drawing.Layouts
open LiveChartsCore.Kernel
open LiveChartsCore.Kernel.Sketches
open LiveChartsCore.Painting
open LiveChartsCore.SkiaSharpView.Drawing
open LiveChartsCore.SkiaSharpView.Drawing.Geometries
open LiveChartsCore.SkiaSharpView.Drawing.Layouts
open LiveChartsCore.SkiaSharpView.Painting
open LiveChartsCore.SkiaSharpView.SKCharts
open SkiaSharp

type CandleTooltip() =
    inherit SKDefaultTooltip()

    let mkLabel (text: string) (paint: Paint) (size: float32) (padding: Padding) (hAlign: Align) =
        let l = LabelGeometry()
        l.Text <- text
        l.Paint <- paint
        l.TextSize <- size
        l.Padding <- padding
        l.MaxWidth <- 320.0f
        l.VerticalAlign <- Align.Start
        l.HorizontalAlign <- hAlign
        l

    override this.GetLayout(foundPoints: IEnumerable<ChartPoint>, chart: Chart) =
        let theme = chart.GetTheme()
        let textSize =
            let configured = float32 chart.View.TooltipTextSize
            if configured < 0.0f then theme.TooltipTextSize else configured
        let fontPaint : Paint =
            match chart.View.TooltipTextPaint with
            | null ->
                match theme.TooltipTextPaint with
                | null -> SolidColorPaint(SKColor(0xC8uy, 0xCCuy, 0xD6uy)) :> Paint
                | p -> p
            | p -> p

        let stack = StackLayout()
        stack.Orientation <- ContainerOrientation.Vertical
        stack.HorizontalAlignment <- Align.Start
        stack.VerticalAlignment <- Align.Middle

        let table = TableLayout()
        table.HorizontalAlignment <- Align.Start
        table.VerticalAlignment <- Align.Middle

        // First found point's secondary text is the X-axis caption (e.g. the timestamp).
        let firstPoint = foundPoints |> Seq.tryHead
        match firstPoint with
        | Some p ->
            let title = p.Context.Series.GetSecondaryToolTipText(p)
            if not (String.IsNullOrEmpty title) && title <> LiveCharts.IgnoreToolTipLabel then
                stack.Children.Add(
                    mkLabel title fontPaint textSize (Padding(0.0, 0.0, 0.0, 6.0)) Align.Start)
        | None -> ()

        let mutable row = 0
        let addRow (name: string) (value: string) =
            table.AddChild(
                mkLabel name fontPaint textSize (Padding(0.0, 2.0, 12.0, 2.0)) Align.Start,
                row, 0,
                horizontalAlign = Align.Start) |> ignore
            table.AddChild(
                mkLabel value fontPaint textSize (Padding(0.0, 2.0, 0.0, 2.0)) Align.End,
                row, 1,
                horizontalAlign = Align.End) |> ignore
            row <- row + 1

        for p in foundPoints do
            let series = p.Context.Series
            match series with
            | :? IFinancialSeries as _fin ->
                // Candle: emit four rows.
                let c = p.Coordinate
                // CoreFinancialSeries layout: Primary=High, Tertiary=Open,
                // Quaternary=Close, Quinary=Low.
                addRow "O" (sprintf "%.4f" c.TertiaryValue)
                addRow "H" (sprintf "%.4f" c.PrimaryValue)
                addRow "L" (sprintf "%.4f" c.QuinaryValue)
                addRow "C" (sprintf "%.4f" c.QuaternaryValue)
            | _ ->
                // Other series (Volume, VWAP) — one row each, using the series's
                // own formatter for the value.
                let content = series.GetPrimaryToolTipText(p)
                if not (String.IsNullOrEmpty content) && content <> LiveCharts.IgnoreToolTipLabel then
                    let label =
                        if String.IsNullOrEmpty series.Name then "" else series.Name
                    addRow label content

        stack.Children.Add(table)
        stack :> Layout<SkiaSharpDrawingContext>
