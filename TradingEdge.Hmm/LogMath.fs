module TradingEdge.Hmm.LogMath

open System

let logSumExp (xs: float[]) =
    let mutable m = Double.NegativeInfinity
    for x in xs do if x > m then m <- x
    if Double.IsNegativeInfinity m then m
    else
        let mutable s = 0.0
        for x in xs do s <- s + exp (x - m)
        m + log s
