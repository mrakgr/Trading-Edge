module TradingEdge.PitFader.Types

/// One 1s present bar of the front contract. `t` = seconds since 00:00 ET (the corpus's bucket).
[<Struct>]
type Bar = { t: int; vwap: float; volume: float; tradeCount: int; delta: float }
