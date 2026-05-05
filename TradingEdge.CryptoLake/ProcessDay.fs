module TradingEdge.CryptoLake.ProcessDay

open System
open System.IO
open System.Globalization
open DuckDB.NET.Data
open TradingEdge.CryptoLake

// =============================================================================
// process-day: build a volume-bar CSV with VWAP, signed flow, and OBI overlay
// =============================================================================
//
// Inputs:
//   - trades parquet (price, quantity, timestamp_us, sign)
//   - book parquet (timestamp_us, bid_{0..19}_{price,size}, ask_{0..19}_{price,size})
//
// Output: a CSV per (symbol, date) at {output-dir}/{symbol}_{date}.csv with
// one row per volume bar. Schema:
//   bar_idx             int      0-indexed
//   start_us            int64    timestamp of first trade in bar
//   end_us              int64    timestamp of last trade in bar
//   cumulative_volume   float    cumulative base-asset volume up to and incl. this bar
//   volume              float    Σ qty (always == volume_per_bar except possibly last)
//   num_trades          int
//   vwap                float    Σ(p·v) / Σv
//   stddev              float    sqrt(max 0 (Σv·(p-vwap)²/Σv))
//   buy_volume          float    Σ qty where sign > 0
//   sell_volume         float    Σ qty where sign < 0
//   signed_volume       float    buy_volume - sell_volume
//   obi_top5            float    OBI sampled from book at bar end_us, depth 5
//   obi_top10           float    OBI sampled from book at bar end_us, depth 10
//
// Volume-bar semantics: a trade that doesn't fit in the current bar is split
// volume-weighted into both bars (matching binance_volume.py / futures_volume.py).
//
// OBI sampling: nearest-snapshot lookup. Book snapshots arrive at ~100ms
// cadence so this is accurate to ~50ms which is plenty for visual overlay.

// -----------------------------------------------------------------------------
// Bar building
// -----------------------------------------------------------------------------

type private BarAccum = {
    mutable Prices: ResizeArray<float>
    mutable Vols: ResizeArray<float>
    mutable Signs: ResizeArray<float>
    mutable StartUs: int64
    mutable EndUs: int64
    mutable Vol: float
}

type Bar = {
    BarIdx: int
    StartUs: int64
    EndUs: int64
    CumulativeVolume: float
    Volume: float
    NumTrades: int
    Vwap: float
    Stddev: float
    BuyVolume: float
    SellVolume: float
    SignedVolume: float
}

let private emptyAccum () : BarAccum = {
    Prices = ResizeArray()
    Vols = ResizeArray()
    Signs = ResizeArray()
    StartUs = 0L
    EndUs = 0L
    Vol = 0.0
}

let private addToBar (acc: BarAccum) (price: float) (vol: float) (ts: int64) (sign: float) =
    if acc.Prices.Count = 0 then acc.StartUs <- ts
    acc.EndUs <- ts
    acc.Prices.Add price
    acc.Vols.Add vol
    acc.Signs.Add sign
    acc.Vol <- acc.Vol + vol

let private finalizeBar (idx: int) (cumVol: float) (acc: BarAccum) : Bar =
    let n = acc.Prices.Count
    let mutable totalV = 0.0
    let mutable sumPV = 0.0
    for i = 0 to n - 1 do
        totalV <- totalV + acc.Vols.[i]
        sumPV <- sumPV + acc.Prices.[i] * acc.Vols.[i]
    let vwap = if totalV > 0.0 then sumPV / totalV else 0.0
    let mutable sumVar = 0.0
    let mutable buyV = 0.0
    let mutable sellV = 0.0
    for i = 0 to n - 1 do
        let p = acc.Prices.[i]
        let v = acc.Vols.[i]
        sumVar <- sumVar + v * (p - vwap) * (p - vwap)
        if acc.Signs.[i] > 0.0 then buyV <- buyV + v else sellV <- sellV + v
    let var = if totalV > 0.0 then sumVar / totalV else 0.0
    {
        BarIdx = idx
        StartUs = acc.StartUs
        EndUs = acc.EndUs
        CumulativeVolume = cumVol + totalV
        Volume = totalV
        NumTrades = n
        Vwap = vwap
        Stddev = sqrt (max 0.0 var)
        BuyVolume = buyV
        SellVolume = sellV
        SignedVolume = buyV - sellV
    }

/// Stream-read trades parquet via DuckDB and build constant-volume bars.
/// volumePerBar is in base-asset units (e.g. BTC). Trades that overflow a bar
/// boundary contribute volume-weighted to both bars.
let buildVolumeBars (tradesParquet: string) (volumePerBar: float) : Bar[] =
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf "SELECT price, quantity, timestamp_us, sign FROM read_parquet('%s') ORDER BY timestamp_us"
            (tradesParquet.Replace("'", "''"))
    use reader = cmd.ExecuteReader()
    let bars = ResizeArray<Bar>()
    let mutable cur = emptyAccum()
    let mutable cumVol = 0.0
    let mutable barIdx = 0
    while reader.Read() do
        let price = reader.GetDouble 0
        let qty = reader.GetDouble 1
        let ts = reader.GetInt64 2
        let sign = reader.GetDouble 3
        let mutable remaining = qty
        while remaining > 0.0 do
            let space = volumePerBar - cur.Vol
            if remaining <= space then
                addToBar cur price remaining ts sign
                remaining <- 0.0
            else
                if space > 0.0 then
                    addToBar cur price space ts sign
                    remaining <- remaining - space
                let bar = finalizeBar barIdx cumVol cur
                bars.Add bar
                cumVol <- cumVol + cur.Vol
                barIdx <- barIdx + 1
                cur <- emptyAccum()
    if cur.Vol > 0.0 then
        let bar = finalizeBar barIdx cumVol cur
        bars.Add bar
    bars.ToArray()

// -----------------------------------------------------------------------------
// OBI sampling from book
// -----------------------------------------------------------------------------

/// Read book snapshots and compute (timestamp_us, OBI_top5, OBI_top10) per row.
/// Pulls only the columns we need from the parquet via a column-projected
/// DuckDB SELECT to keep memory footprint manageable.
let sampleObiSeries (bookParquet: string) (lambdaDecay: float)
    : struct (int64[] * float[] * float[]) =
    let cols =
        [
            yield "timestamp_us"
            for i in 0 .. 9 do yield sprintf "bid_%d_size" i
            for i in 0 .. 9 do yield sprintf "ask_%d_size" i
        ]
        |> String.concat ", "
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        sprintf "SELECT %s FROM read_parquet('%s') ORDER BY timestamp_us"
            cols (bookParquet.Replace("'", "''"))
    use reader = cmd.ExecuteReader()
    let weights5 = Obi.exponentialWeights 5 lambdaDecay
    let weights10 = Obi.exponentialWeights 10 lambdaDecay
    let ts = ResizeArray<int64>()
    let obi5 = ResizeArray<float>()
    let obi10 = ResizeArray<float>()
    let bidSizes = Array.zeroCreate<float> 10
    let askSizes = Array.zeroCreate<float> 10
    while reader.Read() do
        ts.Add(reader.GetInt64 0)
        for i in 0 .. 9 do bidSizes.[i] <- reader.GetDouble(1 + i)
        for i in 0 .. 9 do askSizes.[i] <- reader.GetDouble(11 + i)
        let bid5 = Array.sub bidSizes 0 5
        let ask5 = Array.sub askSizes 0 5
        obi5.Add(Obi.computeOne bid5 ask5 weights5)
        obi10.Add(Obi.computeOne bidSizes askSizes weights10)
    struct (ts.ToArray(), obi5.ToArray(), obi10.ToArray())

/// For each bar, find the OBI sample whose timestamp is nearest to bar.EndUs.
/// Both arrays are sorted ascending; we walk them together in O(n+m).
let alignObiToBars (bars: Bar[]) (obiTs: int64[]) (obi5: float[]) (obi10: float[])
    : struct (float[] * float[]) =
    let n = bars.Length
    let m = obiTs.Length
    let bar5 = Array.zeroCreate<float> n
    let bar10 = Array.zeroCreate<float> n
    let mutable j = 0
    for i = 0 to n - 1 do
        let target = bars.[i].EndUs
        // Advance j until obiTs.[j] >= target or j is at the last element.
        while j < m - 1 && obiTs.[j + 1] <= target do
            j <- j + 1
        // j points to the largest index with obiTs.[j] <= target
        // (or j = 0 if target < obiTs.[0]). Pick j or j+1, whichever is closer.
        let chosen =
            if j = m - 1 then j
            elif obiTs.[j] <= target && target - obiTs.[j] <= obiTs.[j + 1] - target then j
            else j + 1
        bar5.[i] <- obi5.[chosen]
        bar10.[i] <- obi10.[chosen]
    struct (bar5, bar10)

// -----------------------------------------------------------------------------
// CSV writer
// -----------------------------------------------------------------------------

let writeCsv (outPath: string) (bars: Bar[]) (obi5: float[]) (obi10: float[]) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
    use w = new StreamWriter(outPath)
    w.WriteLine
        "bar_idx,start_us,end_us,cumulative_volume,volume,num_trades,vwap,stddev,buy_volume,sell_volume,signed_volume,obi_top5,obi_top10"
    let inv = CultureInfo.InvariantCulture
    for i = 0 to bars.Length - 1 do
        let b = bars.[i]
        w.WriteLine(
            sprintf "%d,%d,%d,%s,%s,%d,%s,%s,%s,%s,%s,%s,%s"
                b.BarIdx
                b.StartUs
                b.EndUs
                (b.CumulativeVolume.ToString("R", inv))
                (b.Volume.ToString("R", inv))
                b.NumTrades
                (b.Vwap.ToString("R", inv))
                (b.Stddev.ToString("R", inv))
                (b.BuyVolume.ToString("R", inv))
                (b.SellVolume.ToString("R", inv))
                (b.SignedVolume.ToString("R", inv))
                (obi5.[i].ToString("R", inv))
                (obi10.[i].ToString("R", inv)))
