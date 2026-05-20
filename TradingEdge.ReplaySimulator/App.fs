module TradingEdge.ReplaySimulator.App

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open TradingEdge.ReplaySimulator.Bars

type App(symbol: string, date: string, bars: Bar list) =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow.create symbol date bars
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
