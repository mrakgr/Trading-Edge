/// Test script for the band-averaged weighted quantile function.
/// Develops and validates bandPrice before integrating into VwapSystem.

/// Weighted average price of trades in the quantile band [q_lo, q_hi].
/// prices and volumes must be same length, sorted by (price, volume).
/// Walks cumulative weight and takes fractional overlaps at band edges.
let bandPrice (prices_and_volumes : struct (float * float) []) (q_lo: float) (q_hi: float) : float =
    let n = prices_and_volumes.Length
    let inline structFst struct (x,_) = x
    if n = 0 then nan
    elif n = 1 then prices_and_volumes.[0] |> structFst
    else
        let prices_and_volumes = Array.sort prices_and_volumes
        let totalWeight = prices_and_volumes |> Array.sumBy (fun struct (_,b) -> b)
        let w_lo = q_lo * totalWeight
        let w_hi = q_hi * totalWeight
        if w_lo < w_hi then
            let mutable cumWeight = 0.0
            let mutable sumPriceWeight = 0.0
            let mutable sumWeight = 0.0
            for i in 0 .. n - 1 do
                let struct (price, volume) = prices_and_volumes.[i]
                let nextCum = cumWeight + volume
                let overlapStart = max cumWeight w_lo
                let overlapEnd = min nextCum w_hi
                if overlapEnd > overlapStart then
                    let fraction = overlapEnd - overlapStart
                    sumPriceWeight <- sumPriceWeight + price * fraction
                    sumWeight <- sumWeight + fraction
                cumWeight <- nextCum
            if sumPriceWeight > 0.0 then sumPriceWeight / sumWeight else failwith "sumPriceWeight > 0.0 check failed"
        elif w_lo = w_hi then
            // Zero-width band (q_lo = q_hi): find the trade containing this
            // exact weight point and return its price.
            let target = w_lo
            let mutable cum = 0.0
            let mutable i = 0
            let mutable found = ValueNone
            while i < n && found.IsNone do
                let struct (price, volume) = prices_and_volumes.[i]
                cum <- cum + volume
                if cum >= target then
                    found <- ValueSome struct (price, volume)
                i <- i + 1
            found.Value |> structFst
        else failwith "w_lo sholdn't be higher than w_hi."

/// Compute the band-averaged price for a given percentile and compression.
let computeBandPrice (prices_and_volumes : struct (float * float) []) (percentile: float) (compression: float) =
    let q = max 0.0 (min 1.0 percentile)
    let hw = q * (1.0 - q) / compression
    let q_lo = q - hw
    let q_hi = q + hw
    printfn "  q=%.4f  hw=%.6f  band=[%.6f, %.6f]" q hw q_lo q_hi
    bandPrice prices_and_volumes q_lo q_hi

// ============================================================================
// Test cases
// ============================================================================

printfn "=== Test 1: Single trade ==="
let t1 = [| struct (100.0, 500.0) |]
let r1 = computeBandPrice t1 0.3 100.0
printfn "  Result: %.4f (expected: 100.0000)\n" r1

printfn "=== Test 2: Two trades, equal volume, percentile=0.5 ==="
let t2 = [| struct (0.0, 100.0); struct (100.0, 100.0) |]
let r2 = computeBandPrice t2 0.5 2.0
printfn "  Result: %.4f (expected: ~50.0 — midpoint)\n" r2

printfn "=== Test 3: Two trades, equal volume, percentile=0.4 (buy side) ==="
let r3 = computeBandPrice t2 0.4 2.0
printfn "  Result: %.4f (expected: ~0.0 — near the low)\n" r3

printfn "=== Test 4: Two trades, equal volume, percentile=0.6 (sell side) ==="
let r4 = computeBandPrice t2 0.6 2.0
printfn "  Result: %.4f (expected: ~100.0 — near the high)\n" r4

printfn "=== Test 5: Three price levels with unequal volume ==="
// 100 shares at $10, 800 shares at $10.50, 100 shares at $11
// Cumulative: [0..100] = $10, [100..900] = $10.50, [900..1000] = $11
let t5 = [| struct (10.0, 100.0); struct (10.5, 800.0); struct (11.0, 100.0) |]

printfn "--- percentile=0.90000001 with infinite compression (point quantile in $10.50 region) ---"
let r5a = computeBandPrice t5 0.90000001 infinity
printfn "  Result: %.4f (expected: 11.0000)\n" r5a

printfn "--- percentile=0.05 (in the $10 region) ---"
let r5b = computeBandPrice t5 0.05 100.0
printfn "  Result: %.4f (expected: ~10.0)\n" r5b

printfn "--- percentile=0.95 (in the $11 region) ---"
let r5c = computeBandPrice t5 0.95 100.0
printfn "  Result: %.4f (expected: ~11.0)\n" r5c

printfn "=== Test 6: Many trades at same price ==="
let t6 = Array.init 10 (fun _ -> struct (50.0, 100.0))
let r6 = computeBandPrice t6 0.3 100.0
printfn "  Result: %.4f (expected: 50.0000 — all same price)\n" r6

printfn "=== Test 7: Edge case — percentile=0.0 ==="
let r7a = computeBandPrice t5 0.0 100.0
printfn "  Result: %.4f (expected: 10.0000 — minimum price)\n" r7a

printfn "=== Test 8: Edge case — percentile=1.0 ==="
let r7b = computeBandPrice t5 1.0 100.0
printfn "  Result: %.4f (expected: 11.0000 — maximum price)\n" r7b

printfn "=== Test 9: Varying compression ==="
printfn "--- compression=10 (wide bands) ---"
let r9a = computeBandPrice t5 0.3 10.0
printfn "  Result: %.4f\n" r9a
printfn "--- compression=100 (narrow bands) ---"
let r9b = computeBandPrice t5 0.3 100.0
printfn "  Result: %.4f\n" r9b
printfn "--- compression=1000 (very narrow bands) ---"
let r9c = computeBandPrice t5 0.3 1000.0
printfn "  Result: %.4f\n" r9c

printfn "=== Test 10: Sorted by (price, volume) — deterministic tie-breaking ==="
// Two trades at same price but different volume — sort order matters for determinism
let t10 = [| struct (10.0, 300.0); struct (10.0, 100.0); struct (11.0, 600.0) |]
let r10 = computeBandPrice t10 0.3 100.0
printfn "  Result: %.4f\n" r10
// Swap the first two (same price, reversed volume order)
let t10b = [| struct (10.0, 100.0); struct (10.0, 300.0); struct (11.0, 600.0) |]
let r10b = computeBandPrice t10b 0.3 100.0
printfn "  Result (swapped): %.4f (should be same: %.4f)\n" r10b r10
