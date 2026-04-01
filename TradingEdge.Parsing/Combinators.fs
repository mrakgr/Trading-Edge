module TradingEdge.Parsing.Combinators

open System

// =============================================================================
// Core Types
// =============================================================================

type Trade = {
    Timestamp: DateTime
    Price: float
    Volume: float
}

type ParseResult<'a> =
    | Success of 'a * Trade list  // Result and remaining trades
    | Failure of string

type Parser<'a> = Trade list -> ParseResult<'a>

// =============================================================================
// Basic Combinators
// =============================================================================

let succeed value : Parser<'a> =
    fun trades -> Success(value, trades)

let fail msg : Parser<'a> =
    fun _ -> Failure msg

let bind (p: Parser<'a>) (f: 'a -> Parser<'b>) : Parser<'b> =
    fun trades ->
        match p trades with
        | Success(value, remaining) -> f value remaining
        | Failure msg -> Failure msg

let map (f: 'a -> 'b) (p: Parser<'a>) : Parser<'b> =
    bind p (f >> succeed)

let sequence (parsers: Parser<'a> list) : Parser<'a list> =
    List.foldBack
        (fun p acc -> bind p (fun x -> map (fun xs -> x :: xs) acc))
        parsers
        (succeed [])

let choice (parsers: Parser<'a> list) : Parser<'a> =
    fun trades ->
        let rec tryParsers = function
            | [] -> Failure "All choices failed"
            | p :: rest ->
                match p trades with
                | Success _ as result -> result
                | Failure _ -> tryParsers rest
        tryParsers parsers

// =============================================================================
// Time-based Combinators
// =============================================================================

let openingPrint : Parser<Trade> =
    fun trades ->
        match trades with
        | [] -> Failure "No trades available"
        | first :: rest -> Success(first, rest)

let closingPrint : Parser<Trade> =
    fun trades ->
        match List.tryLast trades with
        | None -> Failure "No trades available"
        | Some last -> Success(last, [])

let afterMin (minutes: float) : Parser<unit> =
    fun trades ->
        match trades with
        | [] -> Failure "No trades available"
        | first :: _ ->
            let cutoff = first.Timestamp.AddMinutes(minutes)
            let remaining = trades |> List.skipWhile (fun t -> t.Timestamp < cutoff)
            Success((), remaining)

let beforeMin (minutes: float) : Parser<unit> =
    fun trades ->
        match List.tryLast trades with
        | None -> Failure "No trades available"
        | Some last ->
            let cutoff = last.Timestamp.AddMinutes(-minutes)
            let remaining = trades |> List.takeWhile (fun t -> t.Timestamp <= cutoff)
            Success((), remaining)

// =============================================================================
// VWAP Calculation
// =============================================================================

let vwap (trades: Trade list) : float =
    let totalPV = trades |> List.sumBy (fun t -> t.Price * t.Volume)
    let totalV = trades |> List.sumBy (fun t -> t.Volume)
    totalPV / totalV

let between (start: Parser<'a>) (finish: Parser<'b>) : Parser<Trade list> =
    fun trades ->
        match start trades with
        | Failure msg -> Failure msg
        | Success(_, afterStart) ->
            match finish trades with
            | Failure msg -> Failure msg
            | Success(_, beforeFinish) ->
                let startIdx = trades.Length - afterStart.Length
                let endIdx = beforeFinish.Length
                let window = trades |> List.skip startIdx |> List.take (endIdx - startIdx)
                Success(window, afterStart)
