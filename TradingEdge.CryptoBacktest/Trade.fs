module TradingEdge.CryptoBacktest.Trade

open System.Runtime.InteropServices

// =============================================================================
// Trade — the in-memory tape record the whole crypto pipeline folds over.
// =============================================================================
//
// Per-day parquet schema (written by TradingEdge.CryptoData):
//   price DOUBLE, quantity DOUBLE, timestamp_us BIGINT, sign DOUBLE
//
// Formerly lived in TradingEdge.Simulation.BinanceLoader; relocated here when the
// Simulation project (synthetic-data generator) was retired. CryptoBacktest only
// ever used this struct from that project, so it now owns it. Sequential struct
// layout is kept for the tight packed arrays the bar builders / fill sims walk.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type Trade = {
    Price: float
    Quantity: float
    TimestampUs: int64
    Sign: float    // +1.0 buyer aggressive, -1.0 seller aggressive
}
