module TradingEdge.CryptoData.Universe

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions

let private jsonOptions =
    let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
    o

// =============================================================================
// Universe enumeration
// =============================================================================
//
// Two sources merged:
//   1. fapi.binance.com/fapi/v1/exchangeInfo
//        -> currently-trading symbols only (status=TRADING)
//   2. data.binance.vision S3 listing (?prefix=...&delimiter=/)
//        -> every symbol that ever published a daily trade archive,
//           including delisted ones.
//
// Without (2) we'd have survivorship bias: any perp listed-and-delisted
// inside our 2-year window would silently disappear from the backtest.

[<CLIMutable>]
type private ExchangeInfoSymbol = {
    [<JsonPropertyName("symbol")>] Symbol: string
    [<JsonPropertyName("status")>] Status: string
    [<JsonPropertyName("contractType")>] ContractType: string
    [<JsonPropertyName("quoteAsset")>] QuoteAsset: string
    [<JsonPropertyName("underlyingType")>] UnderlyingType: string
}

[<CLIMutable>]
type private ExchangeInfo = {
    [<JsonPropertyName("symbols")>] Symbols: ExchangeInfoSymbol[]
}

type SymbolStatus = Active | DelistedOrArchived

type UniverseEntry = {
    Symbol: string
    Status: SymbolStatus
}

let private exchangeInfoUrl = "https://fapi.binance.com/fapi/v1/exchangeInfo"
let private s3ListUrl = "https://s3-ap-northeast-1.amazonaws.com/data.binance.vision/"
let private archivePrefix = "data/futures/um/daily/trades/"

/// Result of an exchangeInfo fetch — split into the perps we want and the
/// stock-tracker perps (TRADIFI_PERPETUAL / underlyingType=EQUITY) we drop.
/// Binance launched a small product in 2025-2026 wrapping AAPL, MSFT, SPY,
/// QQQ, etc. as USDT-quoted perps. They share the venue's archive layout but
/// they're a different asset class — thin volume, RTH-bound liquidity — and
/// have no business in our crypto-orderflow study.
type private ActiveLists = {
    Perps: Set<string>     // crypto USDT perps (contractType=PERPETUAL)
    StockPerps: Set<string> // equity-tracker perps; we deny-list these
}

let private fetchActive (http: HttpClient) : Async<ActiveLists> =
    async {
        let! json = http.GetStringAsync(exchangeInfoUrl) |> Async.AwaitTask
        let info = JsonSerializer.Deserialize<ExchangeInfo>(json, jsonOptions)
        let usdtTrading =
            info.Symbols
            |> Array.filter (fun s -> s.QuoteAsset = "USDT" && s.Status = "TRADING")
        let isStockPerp (s: ExchangeInfoSymbol) =
            s.ContractType = "TRADIFI_PERPETUAL"
            || (not (isNull s.UnderlyingType) && s.UnderlyingType = "EQUITY")
        let stockPerps =
            usdtTrading
            |> Array.filter isStockPerp
            |> Array.map (fun s -> s.Symbol)
            |> Set.ofArray
        let perps =
            usdtTrading
            |> Array.filter (fun s -> not (isStockPerp s) && s.ContractType = "PERPETUAL")
            |> Array.map (fun s -> s.Symbol)
            |> Set.ofArray
        return { Perps = perps; StockPerps = stockPerps }
    }

// S3 listing returns up to 1000 keys per page; we paginate via the
// <NextMarker> element. Parsing with a regex over <Prefix>...</Prefix>
// keeps us out of the System.Xml dependency.
let private prefixRe = Regex("<Prefix>([^<]+)</Prefix>", RegexOptions.Compiled)
let private nextMarkerRe = Regex("<NextMarker>([^<]+)</NextMarker>", RegexOptions.Compiled)
let private isTruncatedRe = Regex("<IsTruncated>([^<]+)</IsTruncated>", RegexOptions.Compiled)

let private fetchArchived (http: HttpClient) : Async<Set<string>> =
    async {
        let symbols = ResizeArray<string>()
        let mutable marker : string option = None
        let mutable keepGoing = true
        while keepGoing do
            let url =
                match marker with
                | Some m -> sprintf "%s?prefix=%s&delimiter=/&marker=%s" s3ListUrl archivePrefix (Uri.EscapeDataString m)
                | None -> sprintf "%s?prefix=%s&delimiter=/" s3ListUrl archivePrefix
            let! body = http.GetStringAsync(url) |> Async.AwaitTask
            // Each <Prefix> after the wrapper-prefix line is "data/.../{SYMBOL}/".
            // The first match in the body is the wrapper <Prefix>data/futures/um/daily/trades/</Prefix>
            // — strip it by checking for trailing-symbol shape.
            for m in prefixRe.Matches body do
                let p = m.Groups.[1].Value
                // Expect form: data/futures/um/daily/trades/{SYMBOL}/
                if p.StartsWith archivePrefix && p.Length > archivePrefix.Length then
                    let tail = p.Substring(archivePrefix.Length).TrimEnd '/'
                    if tail.Length > 0 && not (tail.Contains '/') then
                        symbols.Add tail
            let truncated =
                let m = isTruncatedRe.Match body
                m.Success && m.Groups.[1].Value = "true"
            if truncated then
                let nm = nextMarkerRe.Match body
                if nm.Success then
                    marker <- Some nm.Groups.[1].Value
                else
                    // Truncated but no NextMarker — fall back to last symbol
                    if symbols.Count > 0 then
                        marker <- Some (sprintf "%s%s/" archivePrefix symbols.[symbols.Count - 1])
                    else
                        keepGoing <- false
            else
                keepGoing <- false
        return symbols |> Set.ofSeq
    }

/// Fetch and merge the active and archived symbol sets, restricted to USDT-quoted
/// crypto perpetuals (excludes Binance's stock-tracker TRADIFI_PERPETUAL product).
/// Stock-perp tickers identified in the live list are also stripped from the
/// archived set, so legacy stock listings (AAPLUSDT etc. — currently active
/// but flagged as TRADIFI) don't leak into the universe via the archive route.
let fetchUniverse (http: HttpClient) : Async<UniverseEntry[]> =
    async {
        printfn "Fetching live exchangeInfo..."
        let! active = fetchActive http
        printfn "  %d active crypto USDT-perps" active.Perps.Count
        printfn "  %d active stock-perps (TRADIFI_PERPETUAL) — excluded" active.StockPerps.Count
        printfn "Listing S3 archive prefix..."
        let! archivedRaw = fetchArchived http
        printfn "  %d archived symbol prefixes" archivedRaw.Count
        // Restrict archived to USDT-quoted; the listing also contains BUSD/USDC
        // and odd assets we don't want.
        let archivedUsdt = archivedRaw |> Set.filter (fun s -> s.EndsWith "USDT")
        printfn "  %d archived USDT-quoted" archivedUsdt.Count
        // Drop any archive entry that's currently flagged as a stock-perp.
        // For symbols not in the active list at all, we have no contractType
        // signal — the historical archive doesn't carry it. We rely on the
        // live deny-list to catch the ones that exist; legacy delisted stock
        // perps would slip through, but we haven't observed any at this time.
        let archivedFiltered = Set.difference archivedUsdt active.StockPerps
        let nDroppedFromArchive = archivedUsdt.Count - archivedFiltered.Count
        if nDroppedFromArchive > 0 then
            printfn "  %d archived entries dropped as stock-perps" nDroppedFromArchive
        let union = Set.union active.Perps archivedFiltered
        let entries =
            union
            |> Seq.sort
            |> Seq.map (fun s ->
                let status = if Set.contains s active.Perps then Active else DelistedOrArchived
                { Symbol = s; Status = status })
            |> Seq.toArray
        let nActive = entries |> Array.filter (fun e -> e.Status = Active) |> Array.length
        let nDelisted = entries.Length - nActive
        printfn "Universe: %d total (%d active, %d delisted/archived)"
            entries.Length nActive nDelisted
        return entries
    }

// =============================================================================
// JSON IO
// =============================================================================

let private statusToString = function
    | Active -> "active"
    | DelistedOrArchived -> "delisted_or_archived"

let private statusFromString = function
    | "active" -> Active
    | "delisted_or_archived" -> DelistedOrArchived
    | s -> failwithf "Unknown universe status: %s" s

let writeUniverse (path: string) (entries: UniverseEntry[]) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    use sw = new StreamWriter(path)
    sw.WriteLine "["
    let mutable first = true
    for e in entries do
        if first then first <- false else sw.WriteLine ","
        sw.Write(sprintf "  {\"symbol\": \"%s\", \"status\": \"%s\"}"
            e.Symbol (statusToString e.Status))
    sw.WriteLine ()
    sw.WriteLine "]"

[<CLIMutable>]
type private UniverseEntryDto = {
    [<JsonPropertyName("symbol")>] Symbol: string
    [<JsonPropertyName("status")>] Status: string
}

let readUniverse (path: string) : UniverseEntry[] =
    let json = File.ReadAllText path
    let dtos = JsonSerializer.Deserialize<UniverseEntryDto[]>(json, jsonOptions)
    dtos |> Array.map (fun d -> { Symbol = d.Symbol; Status = statusFromString d.Status })

// =============================================================================
// File-size enumeration (estimate-size)
// =============================================================================
//
// The S3 listing endpoint can return Contents (full file objects with Size)
// when called WITHOUT &delimiter=/. Each <Contents> block has <Key>...</Key>
// and <Size>...</Size>. We list per-symbol prefixes so we can attribute the
// bytes back to a (symbol, date) and filter to our window.
//
// Listing is paginated at 1000 keys per page. Big symbols hold ~2 archive
// files per day (the trade ZIP plus the .CHECKSUM), so 1000 keys ~= 500 days
// — anything past ~16 months needs at least 2 pages per symbol.

type ArchiveEntry = {
    Symbol: string
    Date: DateTime
    SizeBytes: int64
}

let private contentsRe =
    Regex(@"<Contents>\s*<Key>([^<]+)</Key>(?:[^<]|<(?!Size))*<Size>(\d+)</Size>",
        RegexOptions.Compiled ||| RegexOptions.Singleline)

// Match {SYMBOL}-trades-{YYYY-MM-DD}.zip  (skip .CHECKSUM)
let private archiveKeyRe =
    Regex(@"^data/futures/um/daily/trades/([^/]+)/\1-trades-(\d{4}-\d{2}-\d{2})\.zip$",
        RegexOptions.Compiled)

let private listSymbolFiles
    (http: HttpClient)
    (symbol: string)
    : Async<ArchiveEntry[]> =
    async {
        let prefix = sprintf "%s%s/" archivePrefix symbol
        let acc = ResizeArray<ArchiveEntry>()
        let mutable marker : string option = None
        let mutable keepGoing = true
        while keepGoing do
            let url =
                match marker with
                | Some m -> sprintf "%s?prefix=%s&marker=%s" s3ListUrl prefix (Uri.EscapeDataString m)
                | None -> sprintf "%s?prefix=%s" s3ListUrl prefix
            let! body = http.GetStringAsync(url) |> Async.AwaitTask
            for m in contentsRe.Matches body do
                let key = m.Groups.[1].Value
                let size = Int64.Parse(m.Groups.[2].Value)
                let km = archiveKeyRe.Match key
                if km.Success then
                    let date = DateTime.ParseExact(km.Groups.[2].Value, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
                    acc.Add { Symbol = symbol; Date = date; SizeBytes = size }
            let truncated =
                let m = isTruncatedRe.Match body
                m.Success && m.Groups.[1].Value = "true"
            if truncated then
                let nm = nextMarkerRe.Match body
                if nm.Success then
                    marker <- Some nm.Groups.[1].Value
                else
                    // No NextMarker but truncated — fall back to last key
                    if acc.Count > 0 then
                        let last = acc.[acc.Count - 1]
                        marker <- Some (sprintf "%s%s-trades-%s.zip" prefix last.Symbol (last.Date.ToString("yyyy-MM-dd")))
                    else keepGoing <- false
            else
                keepGoing <- false
        return acc.ToArray()
    }

/// Enumerate every published archive ZIP for the given symbols within the
/// date range, returning per-file sizes from the S3 listing (no downloads).
let listArchiveSizes
    (http: HttpClient)
    (symbols: string[])
    (startDate: DateTime)
    (endDate: DateTime)
    (parallelism: int)
    : Async<ArchiveEntry[]> =
    async {
        use sem = new System.Threading.SemaphoreSlim(parallelism, parallelism)
        let completed = ref 0
        let listOne symbol =
            async {
                do! sem.WaitAsync() |> Async.AwaitTask
                try
                    try
                        let! files = listSymbolFiles http symbol
                        let inWindow =
                            files |> Array.filter (fun f -> f.Date >= startDate && f.Date <= endDate)
                        let c = System.Threading.Interlocked.Increment completed
                        if c % 25 = 0 || c = symbols.Length then
                            printfn "  listed %d/%d symbols" c symbols.Length
                        return inWindow
                    with ex ->
                        eprintfn "  listing %s failed: %s" symbol ex.Message
                        return [||]
                finally
                    sem.Release() |> ignore
            }
        let! results = symbols |> Array.map listOne |> Async.Parallel
        return Array.concat results
    }
