// Measures how long it takes to build (ticker, date) -> avg_volume_4w
// from data/continuation_plays_augmented.json.

open System
open System.IO
open System.Text.Json

let path = "data/continuation_plays_augmented.json"

let run () =
    let swTotal = System.Diagnostics.Stopwatch.StartNew()

    let swRead = System.Diagnostics.Stopwatch.StartNew()
    let bytes = File.ReadAllBytes path
    swRead.Stop()
    printfn "Read %d bytes in %.3fs" bytes.Length swRead.Elapsed.TotalSeconds

    let swParse = System.Diagnostics.Stopwatch.StartNew()
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    swParse.Stop()
    printfn "Parse in %.3fs" swParse.Elapsed.TotalSeconds

    let swWalk = System.Diagnostics.Stopwatch.StartNew()
    let d = System.Collections.Generic.Dictionary<struct (string * string), float>()
    let mutable n = 0
    for el in doc.RootElement.EnumerateArray() do
        let ticker = el.GetProperty("ticker").GetString()
        let date = el.GetProperty("date").GetString()
        let avgVol = el.GetProperty("avg_volume_4w").GetDouble()
        if avgVol > 0.0 then d.[struct (ticker, date)] <- avgVol
        n <- n + 1
    swWalk.Stop()
    printfn "Walk + dict fill: %d rows, %d kept, in %.3fs" n d.Count swWalk.Elapsed.TotalSeconds

    swTotal.Stop()
    printfn "TOTAL: %.3fs" swTotal.Elapsed.TotalSeconds

// Run a warmup and then a timed pass so JIT cost doesn't contaminate.
printfn "=== Warmup ==="
run ()
printfn ""
printfn "=== Timed ==="
run ()
