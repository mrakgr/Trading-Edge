module TradingEdge.Hmm.BinanceLoader

open System
open System.IO

/// Per-trade record extracted from Binance public trade data
/// (https://data.binance.vision). The schema of the source CSV is:
///   trade_id, price, quantity, quote_quantity, timestamp_us,
///   isBuyerMaker, isBestMatch
///
/// We keep the price (for downstream charting), the quantity (volume), the
/// microsecond timestamp, and the sign of aggression derived from
/// isBuyerMaker. isBuyerMaker = True means the buyer was the resting order
/// (passive), so the seller was the aggressor → sign = -1. The reverse maps
/// to sign = +1.
[<Struct>]
type Trade = {
    Price: float
    Quantity: float
    TimestampUs: int64
    Sign: float    // +1.0 buyer aggressive, -1.0 seller aggressive
}

/// Stream a Binance trade CSV (no header) and return the parsed Trade[].
/// Parses ~15M rows per run on BTCUSDT for a busy day; uses Span-based
/// integer/double parsing to avoid string allocations for the hot fields.
let load (path: string) : Trade[] =
    use stream = File.OpenRead path
    use reader = new StreamReader(stream)
    let result = ResizeArray<Trade>(capacity = 4_000_000)
    let mutable line = reader.ReadLine()
    while not (isNull line) do
        let span = line.AsSpan()
        // Find the six commas that separate the seven fields.
        let mutable i0 = 0
        let i1 = span.Slice(i0).IndexOf(',') + i0
        let i2 = span.Slice(i1 + 1).IndexOf(',') + i1 + 1
        let i3 = span.Slice(i2 + 1).IndexOf(',') + i2 + 1
        let i4 = span.Slice(i3 + 1).IndexOf(',') + i3 + 1
        let i5 = span.Slice(i4 + 1).IndexOf(',') + i4 + 1
        // Only fields 1, 2, 4, 5 are needed.
        let priceSpan = span.Slice(i1 + 1, i2 - i1 - 1)
        let qtySpan = span.Slice(i2 + 1, i3 - i2 - 1)
        let tsSpan = span.Slice(i4 + 1, i5 - i4 - 1)
        let bmSpan = span.Slice(i5 + 1)
        // bmSpan is "True" or "False" optionally followed by ",..." — we only
        // care about its first character.
        let trade = {
            Price = Double.Parse(priceSpan, System.Globalization.CultureInfo.InvariantCulture)
            Quantity = Double.Parse(qtySpan, System.Globalization.CultureInfo.InvariantCulture)
            TimestampUs = Int64.Parse(tsSpan, System.Globalization.CultureInfo.InvariantCulture)
            Sign = if bmSpan.[0] = 'T' then -1.0 else +1.0
        }
        result.Add trade
        line <- reader.ReadLine()
    result.ToArray()
