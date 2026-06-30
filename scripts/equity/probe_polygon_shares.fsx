#r "nuget: FSharp.Data, 6.4.0"

// One-off COVERAGE PROBE (read-only, no DB writes): for each (ticker, date) in a
// CSV, hit Polygon /v3/reference/tickers/{ticker}?date={date} and record whether
// share_class_shares_outstanding (scso) comes back. Answers "would Polygon's
// ?date= shares-outstanding fill the float gap the SEC scrape leaves?" BEFORE we
// decide whether to buy Sharadar.
//
// scso is the reliable field: weighted_shares_outstanding (wso) is often a
// current-ish figure and frequently null for delisted names; scso is point-in-time
// per the ?date= snapshot and present for delisted small-caps (verified ABLX).
//
// Input CSV: symbol,first_entry,...  (first_entry = the probe date)
// Output CSV: symbol,date,status,scso  (scso blank = no coverage)
//
// Run: dotnet fsi scripts/equity/probe_polygon_shares.fsx -- <in.csv> <out.csv>

open System
open System.Net.Http
open System.Text.Json
open System.Threading

let args = fsi.CommandLineArgs |> Array.skip 1
let inPath, outPath =
    match args with
    | [| i; o |] -> i, o
    | _ -> failwith "usage: probe_polygon_shares.fsx -- <in.csv> <out.csv>"

let apiKey =
    let doc = JsonDocument.Parse(IO.File.ReadAllText "api_key.json")
    doc.RootElement.GetProperty("massive_api_key").GetString()

let rows =
    IO.File.ReadLines inPath
    |> Seq.skip 1
    |> Seq.map (fun l -> let f = l.Split(',') in f.[0], f.[1])   // symbol, first_entry
    |> Seq.toArray

printfn "Probing %d tickers against Polygon /v3/reference/tickers ?date=" rows.Length

use http = new HttpClient()
http.Timeout <- TimeSpan.FromSeconds 30.0

// scso for one (ticker, date), with a light 429/error retry. Returns (status, scso option).
let probe (ticker: string) (date: string) : Async<string * int64 option> =
    let url = $"https://api.polygon.io/v3/reference/tickers/{ticker}?date={date}&apiKey={apiKey}"
    let rec go attempt = async {
        try
            let! resp = http.GetAsync url |> Async.AwaitTask
            if int resp.StatusCode = 429 && attempt < 6 then
                do! Async.Sleep (250 * (attempt + 1))
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

// Bounded concurrency so we don't hammer the API.
let sem = new SemaphoreSlim(8)
let results =
    rows
    |> Array.map (fun (sym, date) -> async {
        do! sem.WaitAsync() |> Async.AwaitTask
        try return! probe sym date
        finally sem.Release() |> ignore })
    |> Async.Parallel
    |> Async.RunSynchronously

use w = new IO.StreamWriter(outPath)
w.WriteLine "symbol,date,status,scso"
(rows, results) ||> Array.iter2 (fun (sym, date) (status, scso) ->
    w.WriteLine(sprintf "%s,%s,%s,%s" sym date status (match scso with Some v -> string v | None -> "")))
w.Flush()

let covered = results |> Array.filter (fun (_, s) -> s.IsSome) |> Array.length
printfn "Done: %d / %d have share_class_shares_outstanding (%.1f%%)"
    covered rows.Length (100.0 * float covered / float rows.Length)
