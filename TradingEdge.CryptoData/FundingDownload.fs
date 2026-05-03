module TradingEdge.CryptoData.FundingDownload

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open DuckDB.NET.Data
open Sylvan.Data.Csv

// =============================================================================
// Funding-rate downloader
// =============================================================================
//
// Source: https://data.binance.vision/data/futures/um/monthly/fundingRate/{SYMBOL}/
//   {SYMBOL}-fundingRate-{YYYY-MM}.zip
//
// CSV schema (per row):
//   calc_time             (int64 ms)  — funding settlement timestamp UTC
//   funding_interval_hours (int)      — always 8 on Binance USDM
//   last_funding_rate     (float)     — decimal rate (0.0001 = 0.01%/8h)
//
// Output: one parquet per symbol at {root}/{SYMBOL}.parquet covering every
// month listed in S3 (we just download everything available — funding data
// is tiny, ~3 rows × 365 days × 2 years = 2200 rows per symbol). Re-running
// re-downloads everything; idempotency via .complete sentinel per symbol.
//
// Output schema:
//   calc_time_us          int64       — ms timestamp converted to microseconds
//                                       (matches our bar/trade convention)
//   funding_interval_us   int64       — 8h × 3.6e9 us = 28_800_000_000
//   funding_rate          double      — decimal rate (0.0001 = 0.01%/8h)

let private s3ListUrl = "https://s3-ap-northeast-1.amazonaws.com/data.binance.vision/"
let private baseUrl = "https://data.binance.vision/"
let private prefixFmt (symbol: string) =
    sprintf "data/futures/um/monthly/fundingRate/%s/" symbol

let private keyRe (symbol: string) =
    Regex(sprintf @"^data/futures/um/monthly/fundingRate/%s/%s-fundingRate-(\d{4})-(\d{2})\.zip$"
              (Regex.Escape symbol) (Regex.Escape symbol),
          RegexOptions.Compiled)

let private contentsRe =
    Regex(@"<Contents>\s*<Key>([^<]+)</Key>", RegexOptions.Compiled ||| RegexOptions.Singleline)
let private isTruncatedRe = Regex("<IsTruncated>([^<]+)</IsTruncated>", RegexOptions.Compiled)
let private nextMarkerRe = Regex("<NextMarker>([^<]+)</NextMarker>", RegexOptions.Compiled)

type FundingMonthKey = {
    Symbol: string
    Year: int
    Month: int
    Key: string
}

let private fundingOutputPath (root: string) (symbol: string) : string =
    Path.Combine(root, sprintf "%s.parquet" symbol)

let private completeSentinelPath (root: string) (symbol: string) : string =
    Path.Combine(root, sprintf ".complete-%s" symbol)

// -----------------------------------------------------------------------------
// S3 listing
// -----------------------------------------------------------------------------

/// List all funding ZIP keys for one symbol, paginating across multiple pages.
let private listSymbolKeys
    (http: HttpClient)
    (symbol: string)
    : Async<FundingMonthKey list> =
    async {
        let result = ResizeArray<FundingMonthKey>()
        let pat = keyRe symbol
        let mutable marker = ""
        let mutable more = true
        while more do
            let url =
                sprintf "%s?prefix=%s&max-keys=1000%s"
                    s3ListUrl (Uri.EscapeDataString(prefixFmt symbol))
                    (if marker = "" then "" else "&marker=" + Uri.EscapeDataString marker)
            let! body = http.GetStringAsync url |> Async.AwaitTask
            for m in contentsRe.Matches body do
                let key = m.Groups.[1].Value
                let pm = pat.Match key
                if pm.Success then
                    result.Add {
                        Symbol = symbol
                        Year = int pm.Groups.[1].Value
                        Month = int pm.Groups.[2].Value
                        Key = key
                    }
            let truncated =
                let m = isTruncatedRe.Match body
                m.Success && m.Groups.[1].Value = "true"
            if truncated then
                let nm = nextMarkerRe.Match body
                if nm.Success then
                    marker <- nm.Groups.[1].Value
                    more <- true
                else
                    // Some S3-compatible setups omit NextMarker; fall back to
                    // last key from this page.
                    if result.Count > 0 then
                        let last = result.[result.Count - 1]
                        marker <- last.Key
                        more <- true
                    else
                        more <- false
            else
                more <- false
        return List.ofSeq result
    }

// -----------------------------------------------------------------------------
// ZIP download + parse
// -----------------------------------------------------------------------------

type FundingRow = {
    CalcTimeUs: int64
    IntervalUs: int64
    Rate: float
}

/// Download one monthly archive and return the parsed rows. Caller is
/// responsible for retry; failures throw.
let private downloadMonth
    (http: HttpClient)
    (key: FundingMonthKey)
    (ct: CancellationToken)
    : Async<FundingRow list> =
    async {
        let url = baseUrl + key.Key
        use! resp =
            http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
            |> Async.AwaitTask
        if resp.StatusCode = HttpStatusCode.NotFound then
            return []
        else
            resp.EnsureSuccessStatusCode() |> ignore
            let! bytes = resp.Content.ReadAsByteArrayAsync(ct) |> Async.AwaitTask
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
            let entry =
                zip.Entries
                |> Seq.tryFind (fun e -> e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            match entry with
            | None -> return []
            | Some e ->
                use stream = e.Open()
                use reader = new StreamReader(stream)
                let opts = CsvDataReaderOptions(HasHeaders = true)
                use csv = CsvDataReader.Create(reader, opts)
                let rows = ResizeArray<FundingRow>()
                while csv.Read() do
                    let calcMs = csv.GetInt64 0
                    let intervalH = csv.GetInt32 1
                    let rate = csv.GetDouble 2
                    rows.Add {
                        CalcTimeUs = calcMs * 1000L
                        IntervalUs = int64 intervalH * 3_600_000_000L
                        Rate = rate
                    }
                return List.ofSeq rows
    }

// -----------------------------------------------------------------------------
// Parquet writer
// -----------------------------------------------------------------------------

let private writeParquet (outputPath: string) (rows: FundingRow list) : int =
    let tmp = outputPath + ".tmp"
    if File.Exists tmp then File.Delete tmp
    Directory.CreateDirectory(Path.GetDirectoryName outputPath) |> ignore
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    use createCmd = conn.CreateCommand()
    createCmd.CommandText <-
        "CREATE TABLE funding (
            calc_time_us BIGINT,
            funding_interval_us BIGINT,
            funding_rate DOUBLE
         )"
    createCmd.ExecuteNonQuery() |> ignore
    use appender = conn.CreateAppender("funding")
    let mutable n = 0
    let sorted = rows |> List.sortBy (fun r -> r.CalcTimeUs)
    for r in sorted do
        let row = appender.CreateRow()
        row.AppendValue(Nullable r.CalcTimeUs) |> ignore
        row.AppendValue(Nullable r.IntervalUs) |> ignore
        row.AppendValue(Nullable r.Rate) |> ignore
        row.EndRow()
        n <- n + 1
    appender.Close()
    let normalized = tmp.Replace('\\', '/').Replace("'", "''")
    use copyCmd = conn.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY funding TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)" normalized
    copyCmd.ExecuteNonQuery() |> ignore
    if File.Exists outputPath then File.Delete outputPath
    File.Move(tmp, outputPath)
    n

// -----------------------------------------------------------------------------
// Per-symbol orchestration
// -----------------------------------------------------------------------------

type SymbolResult =
    | DownloadedFunding of symbol: string * months: int * rows: int
    | SkippedFunding of symbol: string
    | NoFundingData of symbol: string
    | FailedFunding of symbol: string * error: string

/// Download all funding data for one symbol, writing a single parquet file.
/// Idempotent via .complete-{symbol} sentinel. overwrite=true forces re-download.
let downloadSymbol
    (http: HttpClient)
    (outputRoot: string)
    (symbol: string)
    (overwrite: bool)
    (ct: CancellationToken)
    : Async<SymbolResult> =
    async {
        let outputPath = fundingOutputPath outputRoot symbol
        let sentinelPath = completeSentinelPath outputRoot symbol
        if not overwrite && File.Exists sentinelPath && File.Exists outputPath then
            return SkippedFunding symbol
        else
            try
                let! keys = listSymbolKeys http symbol
                if keys.IsEmpty then
                    return NoFundingData symbol
                else
                    let allRows = ResizeArray<FundingRow>()
                    for key in keys do
                        let mutable attempts = 0
                        let mutable success = false
                        let mutable last: FundingRow list = []
                        while not success && attempts < 3 do
                            try
                                let! rows = downloadMonth http key ct
                                last <- rows
                                success <- true
                            with _ ->
                                attempts <- attempts + 1
                                do! Async.Sleep(500 * attempts)
                        for r in last do allRows.Add r
                    let n = writeParquet outputPath (List.ofSeq allRows)
                    Directory.CreateDirectory(Path.GetDirectoryName sentinelPath) |> ignore
                    File.WriteAllBytes(sentinelPath, [||])
                    return DownloadedFunding(symbol, keys.Length, n)
            with ex ->
                return FailedFunding(symbol, ex.Message)
    }
