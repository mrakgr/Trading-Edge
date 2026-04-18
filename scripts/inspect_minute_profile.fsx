#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"

open System
open TradingEdge.Orb.VolumeProfile

let minute = load "data/volume_profile_minute.json"
let tick   = load "data/volume_profile.json"

let probe (name: string) (p: SessionProfile) =
    printfn "=== %s ===" name
    printfn "  bucketCount=%d, daysUsed=%d" p.BucketCount p.DaysUsed
    // bucket i corresponds to 08:30 + i*dt
    // For 60s buckets: i=60 -> 09:30 (market open), i=60+390 -> 16:00 close
    // For 10s buckets: i=360 -> 09:30, i=2700 -> 16:00
    let monotonic =
        let mutable ok = true
        for i = 1 to p.BucketCount - 1 do
            if p.Profile.[i] < p.Profile.[i - 1] - 1e-12 then ok <- false
        ok
    printfn "  monotonic non-decreasing? %b" monotonic
    // Pick representative cumulative fractions
    let bucketLen = 27000 / p.BucketCount  // seconds per bucket for regular close
    let pickAtHours h =
        let secs = h * 3600.0 - 8.5 * 3600.0
        let i = int (secs / float bucketLen)
        if i < 0 || i >= p.BucketCount then nan else p.Profile.[i]
    printfn "  cum_frac(09:30) = %.4f  (market open)" (pickAtHours 9.5)
    printfn "  cum_frac(10:00) = %.4f" (pickAtHours 10.0)
    printfn "  cum_frac(11:00) = %.4f" (pickAtHours 11.0)
    printfn "  cum_frac(12:00) = %.4f" (pickAtHours 12.0)
    printfn "  cum_frac(14:00) = %.4f" (pickAtHours 14.0)
    printfn "  cum_frac(15:30) = %.4f" (pickAtHours 15.5)
    printfn "  cum_frac(16:00) = %.4f  (close)" p.Profile.[p.BucketCount - 1]

printfn "1-minute profile (new, from flat-file aggs):"
probe "regular_close" minute.RegularClose
probe "early_close"   minute.EarlyClose

printfn ""
printfn "10-second profile (existing, from tick binaries on curated universe):"
probe "regular_close" tick.RegularClose
probe "early_close"   tick.EarlyClose
