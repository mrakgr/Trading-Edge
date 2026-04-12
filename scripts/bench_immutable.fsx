open System
open System.Collections.Immutable
open System.Diagnostics

[<Struct>]
type Trade = { Price: float; Volume: float; Timestamp: DateTime; Session: int }

let iterations = 10000
let tradesPerBar = 50

let benchResizeArrayToArray () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let ra = ResizeArray<Trade>()
        for j in 1 .. tradesPerBar do
            ra.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.Now; Session = 0 }
        let arr = ra.ToArray()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "ResizeArray + ToArray:          %7.3f ms  (%d iters x %d items = %d total)" sw.Elapsed.TotalMilliseconds iterations tradesPerBar sink

let benchBuilderToImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let b = ImmutableArray.CreateBuilder<Trade>()
        for j in 1 .. tradesPerBar do
            b.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.Now; Session = 0 }
        let arr = b.ToImmutableArray()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Builder + ToImmutableArray:     %7.3f ms  (%d iters x %d items = %d total)" sw.Elapsed.TotalMilliseconds iterations tradesPerBar sink

let benchBuilderMoveToImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let b = ImmutableArray.CreateBuilder<Trade>(tradesPerBar)
        for j in 1 .. tradesPerBar do
            b.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.Now; Session = 0 }
        let arr = b.MoveToImmutable()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Builder + MoveToImmutable:      %7.3f ms  (%d iters x %d items = %d total)" sw.Elapsed.TotalMilliseconds iterations tradesPerBar sink

let benchResizeArrayAsImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let ra = ResizeArray<Trade>()
        for j in 1 .. tradesPerBar do
            ra.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.Now; Session = 0 }
        let arr = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(ra.ToArray())
        sink <- sink + arr.Length
    sw.Stop()
    printfn "ResizeArray + AsImmutableArray: %7.3f ms  (%d iters x %d items = %d total)" sw.Elapsed.TotalMilliseconds iterations tradesPerBar sink

// Warm up
for i = 1 to 10 do
    benchResizeArrayToArray ()
    benchBuilderToImmutable ()
    benchBuilderMoveToImmutable ()
    benchResizeArrayAsImmutable ()

printfn "\n=== Measured ==="
benchResizeArrayToArray ()
benchBuilderToImmutable ()
benchBuilderMoveToImmutable ()
benchResizeArrayAsImmutable ()
