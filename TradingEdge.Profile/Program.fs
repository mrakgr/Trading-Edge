module TradingEdge.Profile.Program

open System
open System.Collections.Immutable
open System.Diagnostics

[<Struct>]
type Trade = { Price: float; Volume: float; Timestamp: DateTime; Session: int }

let iterations = 10000
let tradesPerBar = 50

let benchPreallocatedArray () =
    let arr = Array.zeroCreate<Trade> tradesPerBar
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        for j in 0 .. tradesPerBar - 1 do
            arr.[j] <- { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Preallocated array (reused):      %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchNewArrayEachTime () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let arr = Array.zeroCreate<Trade> tradesPerBar
        for j in 0 .. tradesPerBar - 1 do
            arr.[j] <- { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        sink <- sink + arr.Length
    sw.Stop()
    printfn "New array each iter:              %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchPreallocatedWithCopy () =
    let arr = Array.zeroCreate<Trade> tradesPerBar
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        for j in 0 .. tradesPerBar - 1 do
            arr.[j] <- { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let copy = Array.copy arr
        sink <- sink + copy.Length
    sw.Stop()
    printfn "Preallocated + Array.copy:        %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchBuilderToImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let b = ImmutableArray.CreateBuilder<Trade>()
        for j in 1 .. tradesPerBar do
            b.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let arr = b.ToImmutableArray()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Builder -> ToImmutableArray:       %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchBuilderMoveToImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let b = ImmutableArray.CreateBuilder<Trade>(tradesPerBar)
        for j in 1 .. tradesPerBar do
            b.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let arr = b.MoveToImmutable()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Builder -> MoveToImmutable:        %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchBuilderToArray () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let b = ImmutableArray.CreateBuilder<Trade>()
        for j in 1 .. tradesPerBar do
            b.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let arr = b.ToArray()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "Builder -> ToArray:                %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchResizeArrayToArray () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let ra = ResizeArray<Trade>()
        for j in 1 .. tradesPerBar do
            ra.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let arr = ra.ToArray()
        sink <- sink + arr.Length
    sw.Stop()
    printfn "ResizeArray -> ToArray:            %7.3f ms" sw.Elapsed.TotalMilliseconds

let benchResizeArrayToImmutable () =
    let sw = Stopwatch.StartNew()
    let mutable sink = 0
    for _ in 1 .. iterations do
        let ra = ResizeArray<Trade>()
        for j in 1 .. tradesPerBar do
            ra.Add { Price = float j; Volume = 100.0; Timestamp = DateTime.MinValue; Session = 0 }
        let arr = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(ra.ToArray())
        sink <- sink + arr.Length
    sw.Stop()
    printfn "ResizeArray -> AsImmutableArray:   %7.3f ms" sw.Elapsed.TotalMilliseconds

[<EntryPoint>]
let main argv =
    for _ = 1 to 10 do
        benchPreallocatedArray ()
        benchPreallocatedWithCopy ()
        benchNewArrayEachTime ()
        benchBuilderToImmutable ()
        benchBuilderMoveToImmutable ()
        benchBuilderToArray ()
        benchResizeArrayToArray ()
        benchResizeArrayToImmutable ()

    printfn "\n=== Measured ==="
    benchPreallocatedArray ()
    benchPreallocatedWithCopy ()
    benchNewArrayEachTime ()
    benchBuilderToImmutable ()
    benchBuilderMoveToImmutable ()
    benchBuilderToArray ()
    benchResizeArrayToArray ()
    benchResizeArrayToImmutable ()
    0
