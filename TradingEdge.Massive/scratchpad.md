# Overview

A few commands to get started for myself.

```
dotnet run --project TradingEdge.Massive -- download-bulk -s 2003-09-10
dotnet run --project TradingEdge.Massive -- download-splits -s 2003-09-10
dotnet run --project TradingEdge.Massive -- ingest-data
```

Here is a command to plot the DOM.

```
dotnet run --project TradingEdge.Massive -- plot-chart -t SPY
dotnet run --project TradingEdge.Massive -- plot-dom -t SPY
```

Here is how to get trades data

```
dotnet run --project TradingEdge.Massive -- download-trades -t LW -s 2025-12-19 --pretty
dotnet run --project TradingEdge.Massive -- download-quotes -t LW -s 2025-12-19 --pretty
```