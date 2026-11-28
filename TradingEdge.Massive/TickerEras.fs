/// THE TICKER ERAS (2026-11-28): which security a REUSED ticker symbol named on a given day.
///
/// `ticker_reference` is one (ticker, type) row per type ever seen, with no dates — so a symbol that was a common stock
/// once and an ETF now passes every `type IN ('CS','ADRC')` filter on ALL its days. RAM was Aries I Acquisition (CS,
/// listed 2021-07-09) and is the Roundhill 2X DRAM ETF since 2026-06-24: the research universe traded the ETF as a
/// stock (exec review week 7: 8 reference trips in two weeks that the live engine, on Massive's CURRENT list, never saw).
/// 72 symbols carry both a CS/ADRC and a non-CS row.
///
/// Massive's ticker details are point-in-time: `/v3/reference/tickers/{T}?date=D` returns the listing in force on D
/// (its type, name and `list_date`, the day that listing began). `erasOf` asks it on the days the symbol TRADED (our
/// own daily bars) and bisects where the type changes. Every era's type is what was known that day: no lookahead.
module TradingEdge.TickerEras

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// One listing of a symbol: its type on the days From .. To (To = None: still listed)
type Era =
    { Ticker: string
      Type: string
      Name: string
      From: DateOnly
      To: DateOnly option }

type private Listing = { Type: string; Name: string; ListDate: DateOnly option }

let private baseUrl = "https://api.polygon.io/v3/reference/tickers"
/// the walk stops here: the daily data starts in 2003
let Earliest = DateOnly(2003, 1, 1)
let private iso (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let private str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString() |> nonNull)
    | _ -> None

/// GET `url` as JSON: None on a 404; 429s and transient failures retried
let private getJson (http: HttpClient) (url: string) (ct: CancellationToken) : Task<JsonDocument option> =
    task {
        let mutable result = None
        let mutable attempt = 0
        let mutable fin = false
        while not fin do
            let! outcome =
                task {
                    try
                        use! resp = http.GetAsync(url, ct)
                        if resp.StatusCode = HttpStatusCode.NotFound then return Ok None
                        elif resp.StatusCode = HttpStatusCode.TooManyRequests then return Error "429"
                        else
                            resp.EnsureSuccessStatusCode() |> ignore
                            let! s = resp.Content.ReadAsStringAsync(ct)
                            return Ok (Some (JsonDocument.Parse s))
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException ct)
                    | ex -> return Error ex.Message
                }
            match outcome with
            | Ok r ->
                result <- r
                fin <- true
            | Error e when attempt < 6 ->
                attempt <- attempt + 1
                do! Task.Delay(500 * attempt, ct)
            | Error e -> failwithf "ticker eras: %s failed after %d tries: %s" (url.Split('?').[0]) attempt e
        return result
    }

/// the listing of `ticker` in force on `d` (None: no listing that day)
let private listingOn (http: HttpClient) (apiKey: string) (ticker: string) (d: DateOnly) ct : Task<Listing option> =
    task {
        let! doc = getJson http $"{baseUrl}/{Uri.EscapeDataString ticker}?date={iso d}&apiKey={apiKey}" ct
        match doc with
        | None -> return None
        | Some doc ->
            use doc = doc
            match doc.RootElement.TryGetProperty "results" with
            | true, r when r.ValueKind = JsonValueKind.Object ->
                let listDate =
                    str r "list_date"
                    |> Option.bind (fun s ->
                        match DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                        | true, x -> Some x
                        | _ -> None)
                return Some { Type = str r "type" |> Option.defaultValue "?"; Name = str r "name" |> Option.defaultValue ""; ListDate = listDate }
            | _ -> return None
    }

/// the first day in lo .. hi on which `ticker` is listed as `typ` (it is on hi): bisected on `?date=`
let private firstDayOf (http: HttpClient) (apiKey: string) (ticker: string) (typ: string) (lo: DateOnly) (hi: DateOnly) ct : Task<DateOnly> =
    task {
        let mutable lo = lo
        let mutable hi = hi
        while lo < hi do
            let mid = lo.AddDays((hi.DayNumber - lo.DayNumber) / 2)
            let! l = listingOn http apiKey ticker mid ct
            match l with
            | Some l when l.Type = typ -> hi <- mid
            | _ -> lo <- mid.AddDays 1
        return lo
    }

/// the last day in lo .. hi on which `ticker` is listed as `typ` (it is on lo): bisected on `?date=`
let private lastDayOf (http: HttpClient) (apiKey: string) (ticker: string) (typ: string) (lo: DateOnly) (hi: DateOnly) ct : Task<DateOnly> =
    task {
        let mutable lo = lo
        let mutable hi = hi
        while lo < hi do
            let mid = lo.AddDays((hi.DayNumber - lo.DayNumber + 1) / 2)
            let! l = listingOn http apiKey ticker mid ct
            match l with
            | Some l when l.Type = typ -> lo <- mid
            | _ -> hi <- mid.AddDays -1
        return lo
    }

/// `ticker`'s eras over the days it traded. `runs`: its runs of daily bars (first, last day; a gap > 45 calendar days
/// starts a new run — the episodes of build_daily_episodes_causal_view.sql). The listing in force TODAY starts at its
/// list_date (else bisected) and runs on; each run of bars before it is classified by the listing on its first and its
/// last day — one type: one era; two types: the change bisected. (An old listing need not leave an inactive row with
/// a delisting date: RAM's Aries I has none, yet `?date=2023-01-03` returns it — so the bars, not the delistings, say
/// where to look.) A day no listing covers gets no era: the caller's undated rule applies there.
let erasOf (http: HttpClient) (apiKey: string) (ticker: string) (runs: (DateOnly * DateOnly) list) (today: DateOnly)
           (ct: CancellationToken) : Task<Era list> =
    task {
        let eras = ResizeArray<Era>()
        let era (l: Listing) (a: DateOnly) (b: DateOnly option) =
            eras.Add { Ticker = ticker; Type = l.Type; Name = l.Name; From = a; To = b }
        let! now = listingOn http apiKey ticker today ct
        let! nowFrom =
            match now with
            | Some l ->
                task {
                    let! f =
                        match l.ListDate with
                        | Some x when x <= today -> Task.FromResult x
                        | _ -> firstDayOf http apiKey ticker l.Type Earliest today ct
                    era l f None
                    return Some f
                }
            | None -> Task.FromResult None
        for a, b in runs do
            let b = match nowFrom with Some f when f <= b -> f.AddDays -1 | _ -> b      // the current listing's days are done
            if a <= b then
                let! la = listingOn http apiKey ticker a ct
                let! lb = listingOn http apiKey ticker b ct
                match la, lb with
                | Some x, Some y when x.Type = y.Type -> era x a (Some b)
                | _, Some y ->
                    let! f = firstDayOf http apiKey ticker y.Type a b ct
                    era y f (Some b)
                    match la with
                    | Some x -> era x a (Some (f.AddDays -1))
                    | None -> ()
                | Some x, None ->
                    let! l = lastDayOf http apiKey ticker x.Type a b ct
                    era x a (Some l)
                | None, None -> ()
        return eras |> Seq.sortByDescending (fun e -> e.From) |> List.ofSeq
    }
