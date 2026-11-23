#r "../../TradingEdge.Orb/bin/Release/net11.0/TradingEdge.Orb.dll"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "nuget: Argu, 6.2.5"

// ⭐ 2026-11-18: the STREAMING bar builder (Bars.BarAccumulator + Bars.tradeFlags — the live path's) against the SQL
// corpus builder (build_all_1s_bars.fsx) on one day: every bar of a ticker sample, all eight columns, must agree.
// The tape is truncated to MILLISECONDS first, as the live feed is (Bars: TIMESTAMP PRECISION).
//   cd research && dotnet fsi scripts/conversion/check_1s_share_stream.fsx -- --date 2025-06-02 --bars <dir> --tickers 300

open System
open System.IO
open Argu
open DuckDB.NET.Data
open TradingEdge.Orb

type Args =
    | Date of string
    | Bars of string
    | Tickers of int
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Date _ -> "the day (yyyy-MM-dd)"
            | Bars _ -> "the directory of the SQL-built day file"
            | Tickers _ -> "how many tickers (the most-traded half, the rest at random); default 300"

let p = ArgumentParser.Create<Args>().Parse(fsi.CommandLineArgs |> Array.skip 1)
let date = p.GetResult Date
let barsFile = Path.Combine(p.GetResult Bars, $"{date}.parquet")
let nTickers = p.GetResult(Tickers, 300)
let baseNs = Bars.baseNsForDate date
let endNs = Bars.endNsExclusive baseNs

let conn = new DuckDBConnection("DataSource=:memory:")
conn.Open()
let q (sql: string) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.UseStreamingMode <- true
    cmd.ExecuteReader()

// the sample: the most-traded tickers + a random draw of the rest
let tickers =
    use rd = q $"""SET threads=6; SET memory_limit='4GB';
                   WITH t AS (SELECT ticker, sum(trade_count) n FROM read_parquet('{barsFile}') GROUP BY ticker)
                   (SELECT ticker FROM t ORDER BY n DESC LIMIT {nTickers / 2})
                   UNION (SELECT ticker FROM t USING SAMPLE {nTickers / 2} ROWS (reservoir, 7))"""
    [| while rd.Read() do yield rd.GetString 0 |]
let lst = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ", "

// the streaming side
let streamed = Collections.Generic.Dictionary<string, ResizeArray<Bars.Bar1s>>()
let mutable rows = 0L
do
    use rd = q $"""SELECT ticker, sip_timestamp, participant_timestamp, exchange, price, size, conditions
                   FROM read_parquet('data/bulk/trades/{date}.parquet') WHERE ticker IN ({lst})
                   ORDER BY ticker, sip_timestamp"""
    let mutable cur = ""
    let mutable acc = Bars.BarAccumulator()
    let mutable out = ResizeArray<Bars.Bar1s>()
    let close () =
        match acc.Flush() with ValueSome b -> out.Add b | ValueNone -> ()
        if cur <> "" then streamed.[cur] <- out
    while rd.Read() do
        rows <- rows + 1L
        let tk = rd.GetString 0
        if tk <> cur then
            close ()
            cur <- tk; acc <- Bars.BarAccumulator(); out <- ResizeArray()
        let sip = (rd.GetInt64 1 / 1_000_000L) * 1_000_000L          // the live feed's ms
        let part = if rd.IsDBNull 2 then 0L else (rd.GetInt64 2 / 1_000_000L) * 1_000_000L
        let ex = if rd.IsDBNull 3 then 0 else Convert.ToInt32(rd.GetValue 3)
        let price = rd.GetDouble 4
        let size = rd.GetDouble 5
        let mask = if rd.IsDBNull 6 then 0UL else Bars.maskOfBytes (rd.GetValue 6 :?> Collections.Generic.IReadOnlyList<byte>)
        let ts = Bars.tsOf sip part
        if ts >= baseNs && ts < endNs then
            let flags = Bars.tradeFlags mask sip part ex price size
            if flags <> 0uy then
                match acc.Push(Bars.bucketOfNs baseNs ts, price, size, flags) with
                | ValueSome b -> out.Add b
                | ValueNone -> ()
    close ()

// the SQL side, compared bar by bar
let mutable bars = 0
let mutable bad = 0
do
    use rd = q $"""SELECT ticker, bucket, vwap, volume, trade_count, total_volume, retail_volume, mid_volume
                   FROM read_parquet('{barsFile}') WHERE ticker IN ({lst}) ORDER BY ticker, bucket"""
    let idx = Collections.Generic.Dictionary<string, int>()
    while rd.Read() do
        let tk = rd.GetString 0
        let i = match idx.TryGetValue tk with | true, i -> i | _ -> 0
        idx.[tk] <- i + 1
        bars <- bars + 1
        let s = match streamed.TryGetValue tk with | true, l when i < l.Count -> ValueSome l.[i] | _ -> ValueNone
        let sql : Bars.Bar1s =
            { Bucket = rd.GetInt32 1; Vwap = rd.GetFloat 2; Volume = rd.GetFloat 3; TradeCount = rd.GetInt32 4
              TotalVolume = rd.GetFloat 5; RetailVolume = rd.GetFloat 6; MidVolume = rd.GetFloat 7 }
        match s with
        | ValueSome b when b = sql -> ()
        | other ->
            bad <- bad + 1
            if bad <= 10 then printfn "MISMATCH %s #%d sql %A stream %A" tk i sql other
    for kv in streamed do
        let n = match idx.TryGetValue kv.Key with | true, i -> i | _ -> 0
        if kv.Value.Count <> n then
            bad <- bad + 1
            printfn "COUNT %s: stream %d bars, sql %d" kv.Key kv.Value.Count n

printfn "%s: %d tickers, %d trades, %d SQL bars, %d mismatches" date tickers.Length rows bars bad
exit (if bad = 0 then 0 else 1)
