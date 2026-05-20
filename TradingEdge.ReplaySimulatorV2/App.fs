module TradingEdge.ReplaySimulatorV2.App

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open LiveChartsCore
open LiveChartsCore.SkiaSharpView
open SkiaSharp
open TradingEdge.ReplaySimulatorV2.Snapshots

type App(symbol: string, date: string, store: SnapshotStore, startCursorNs: int64) =
    inherit Application()

    override this.Initialize() =
        // Pick a wide-coverage typeface so default LiveCharts glyphs render.
        let tf = SKFontManager.Default.MatchCharacter(int '汉')
        LiveCharts.Configure(fun config ->
            config.HasTextSettings(TextSettings(DefaultTypeface = tf)) |> ignore)
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow.create symbol date store startCursorNs
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
