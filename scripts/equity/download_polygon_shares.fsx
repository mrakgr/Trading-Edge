#r "nuget: DuckDB.NET.Data.Full, 1.4.4"

// Download point-in-time SHARES OUTSTANDING from Polygon into a `polygon_shares`
// table in float.db. For each (ticker, date) pair we hit
//   /v3/reference/tickers/{ticker}?date={date}
// and store `share_class_shares_outstanding` (scso) — the field VERIFIED to honor
// ?date= (it tracks splits: NVDA 612M -> 2.5B -> 24.6B across its 2021/2024
// splits), unlike weighted_shares_outstanding which is not reliably point-in-time.
//
// The dollar float at entry is then scso * adj_close_at_entry, ASOF-joined in the
// post-hoc analysis (like scripts/equity/move_x_float.sql does for SEC float).
// Because ?date= is already split-correct, NO period_end re-scaling is needed (the
// SEC path needed it; this doesn't).
//
// There is NO bulk endpoint for scso: the LIST endpoint omits it, the flat-files
// have no reference dump, and vX/financials carries only accounting period-average
// shares. So this is inherently one HTTP call per (ticker, date) — which is fine
// because a backtest only needs scso AT each entry date (~7.6k distinct pairs).
//
// Idempotent + resumable: pairs already in polygon_shares are skipped, so a re-run
// after an interruption only fetches the remainder.
//
// Input CSV: symbol,date[,...]   (extra cols ignored)
// Run: dotnet fsi scripts/equity/download_polygon_shares.fsx -- <pairs.csv> [float.db]

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open DuckDB.NET.Data

let args = fsi.CommandLineArgs |> Array.skip 1
let inPath, dbPath =
    match args with
    | [| i |]    -> i, "data/equity/float/float.db"
    | [| i; db |] -> i, db
    | _ -> failwith "usage: download_polygon_shares.fsx -- <pairs.csv> [float.db]"

let apiKey =
    let doc = JsonDocument.Parse(IO.File.ReadAllText "api_key.json")
    doc.RootElement.GetProperty("massive_api_key").GetString()

// ----- table + already-have set -----
let conn = new DuckDBConnection($"DataSource={dbPath}")
conn.Open()
let exec (q: string) =
    use cmd = conn.CreateCommand() in cmd.CommandText <- q; cmd.CommandTimeout <- 0
    cmd.ExecuteNonQuery() |> ignore
// scso NULL = Polygon returned no share count for that (ticker, date) — a real
// "no data" outcome we record (so a resume doesn't re-fetch known-empty pairs).
exec """
CREATE TABLE IF NOT EXISTS polygon_shares (
    ticker VARCHAR NOT NULL,
    date   DATE    NOT NULL,
    scso   BIGINT,
    status VARCHAR,
    PRIMARY KEY (ticker, date));
"""

let have =
    let s = System.Collections.Generic.HashSet<string*DateOnly>()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT ticker, date FROM polygon_shares"
    use r = cmd.ExecuteReader()
    while r.Read() do s.Add(r.GetString 0, DateOnly.FromDateTime(r.GetDateTime 1)) |> ignore
    s

let pairs =
    IO.File.ReadLines inPath
    |> Seq.skip 1
    |> Seq.map (fun l -> let f = l.Split(',') in f.[0], DateOnly.ParseExact(f.[1], "yyyy-MM-dd"))
    |> Seq.distinct
    |> Seq.filter (fun p -> not (have.Contains p))
    |> Seq.toArray

printfn "polygon_shares: %d already stored; %d pairs to fetch" have.Count pairs.Length

// ----- fetch scso for one (ticker, date) -----
let http = new HttpClient()
http.Timeout <- TimeSpan.FromSeconds 30.0

let probe (ticker: string) (date: DateOnly) : Async<string * int64 option> =
    let ds = date.ToString("yyyy-MM-dd")
    let url = $"https://api.polygon.io/v3/reference/tickers/{ticker}?date={ds}&apiKey={apiKey}"
    let rec go attempt = async {
        try
            let! resp = http.GetAsync url |> Async.AwaitTask
            if int resp.StatusCode = 429 && attempt < 6 then
                do! Async.Sleep (300 * (attempt + 1))
                return! go (attempt + 1)
            else
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                use doc = JsonDocument.Parse body
                let root = doc.RootElement
                let status = match root.TryGetProperty "status" with true, v -> v.GetString() | _ -> "?"
                let scso =
                    match root.TryGetProperty "results" with
                    | true, r ->
                        match r.TryGetProperty "share_class_shares_outstanding" with
                        | true, v when v.ValueKind = JsonValueKind.Number -> Some (v.GetInt64())
                        | _ -> None
                    | _ -> None
                return status, scso
        with ex when attempt < 4 ->
            do! Async.Sleep 500
            return! go (attempt + 1)
    }
    go 0

// ----- run with bounded concurrency, batch-insert results -----
let sem = new SemaphoreSlim(8)
let sw = Diagnostics.Stopwatch.StartNew()
let mutable done_ = 0

let results =
    pairs
    |> Array.map (fun (sym, date) -> async {
        do! sem.WaitAsync() |> Async.AwaitTask
        try
            let! (status, scso) = probe sym date
            let n = Interlocked.Increment(&done_)
            if n % 500 = 0 then printfn "  %d / %d (%.0fs)" n pairs.Length sw.Elapsed.TotalSeconds
            return (sym, date, status, scso)
        finally sem.Release() |> ignore })
    |> Async.Parallel
    |> Async.RunSynchronously

// Insert via the Appender (fast, transactional). Discrete per-column appends
// (matches TradesDownload.fs) to avoid AppendValue overload ambiguity.
let appender = conn.CreateAppender("polygon_shares")
for (sym, date, status, scso) in results do
    let row = appender.CreateRow()
    row.AppendValue(sym) |> ignore
    row.AppendValue(Nullable(DateTime(date.Year, date.Month, date.Day))) |> ignore
    (match scso with
     | Some v -> row.AppendValue(Nullable v) |> ignore
     | None   -> row.AppendNullValue() |> ignore)
    row.AppendValue(status) |> ignore
    row.EndRow()
appender.Close()
sw.Stop()

let covered = results |> Array.filter (fun (_,_,_,s) -> s.IsSome) |> Array.length
printfn "Done in %.0fs: fetched %d, %d with scso (%.1f%%)"
    sw.Elapsed.TotalSeconds results.Length covered
    (100.0 * float covered / float (max 1 results.Length))
let total = (use c = conn.CreateCommand() in c.CommandText <- "SELECT COUNT(*) FROM polygon_shares"; c.ExecuteScalar() :?> int64)
printfn "polygon_shares now holds %d rows" total
conn.Dispose()
