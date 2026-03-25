# Overview

A few commands to get started for myself.

```
dotnet run --project TradingEdge.Massive -- download-bulk
dotnet run --project TradingEdge.Massive -- download-splits
dotnet run --project TradingEdge.Massive -- download-dividends
dotnet run --project TradingEdge.Massive -- ingest-data
dotnet run --project TradingEdge.Massive -- stocks-in-play
```

Here is how to get trades data

```
dotnet run --project TradingEdge.Massive -- download-trades -t LW -s 2025-12-19 --pretty
dotnet run --project TradingEdge.Massive -- download-quotes -t LW -s 2025-12-19 --pretty
```