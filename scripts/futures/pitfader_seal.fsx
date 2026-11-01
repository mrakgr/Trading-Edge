// THE SEAL of PitFader's feature port (user 2026-11-01: "Each stage gets checked against SpikeFader on the same
// bars"). The same futures RTH sessions are fed to the RESEARCH engines themselves — FlushFader's IntradaySystem for the
// LONG side, SpikeFader's for the SHORT side — with every gate at its documented OFF value, entries from 09:30, mc = 0.
//   control 1: the signal bars must then be IDENTICAL (PitFader's long signals == FlushFader's, short == SpikeFader's);
//   control 2: every PitFader feature that a research engine records is compared on those shared signal bars
//              (relative 1e-9; nan == nan). A feature neither engine records (delta_N_bar) is listed as unchecked.
// Input: a CSV of sessions (td, ticker, bucket, vwap, volume, trade_count, delta), RTH bars of one front contract each.
// Build first: dotnet build TradingEdge.{PitFader,FlushFader,SpikeFader} -c Release.
//
//   cd research && dotnet fsi scripts/futures/pitfader_seal.fsx -- <bars.csv> > logs/pitfader_seal.log 2>&1; echo exit $?
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.RollingMa.dll"
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/Nito.Collections.Deque.dll"
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/DuckDB.NET.Bindings.dll"
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/DuckDB.NET.Data.dll"
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "../../TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.SpikeFader.dll"
#r "../../TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader.dll"
#r "../../TradingEdge.PitFader/bin/Release/net10.0/TradingEdge.PitFader.dll"

open System
open System.IO

module SF = TradingEdge.SpikeFader.Intraday
module FF = TradingEdge.FlushFader.Intraday
module PT = TradingEdge.PitFader.Types
module PE = TradingEdge.PitFader.Engine
module PX = TradingEdge.PitFader.Features
module PL = TradingEdge.PitFader.Leg

let csv = fsi.CommandLineArgs.[1]
let inf = Double.PositiveInfinity

// ---- every gate OFF (each field's doc names its off sentinel)
let ffCfg : FF.IntradayConfig =
    { TradingEdge.FlushFader.Backtest.defaultConfig.Intraday with
        DvFloor60 = 0.0; TcFloor60 = 0.0; MinVolat20m = 0.0; MaxVolat20m = inf
        MaxSpeed1m = 0.0; MaxDist1mHi = 0.0; MinRSinceFlow = -1.0; CascadeHaltCount = 0; ReopenBlockSec = 0
        MaxZ20m = 0.0; KBandLo = 0; KBandHi = 0; AbsEff20Lo = 0.0; AbsEff20Hi = inf; MinAbsEff10m = 0.0
        MinEff9Ema10m = -inf; SsfLoBpm = -inf; SsfHiBpm = 0.0; MaxDistLegVwap = 0.0; MinVol10Rate = 0.0
        MinLows300 = 0; MinLows180 = 0; MaxRngFront = inf; MinAccel1020Bpm = -inf; MaxSlope20Bpm = 0.0
        MinSlope5Bpm = -inf; MinDv0945Tape = 0.0; MaxConcurrent = 0; EntryStartSec = 34200 }
let sfCfg : SF.IntradayConfig =
    { TradingEdge.SpikeFader.Backtest.defaultConfig.Intraday with
        DvFloor60 = 0.0; TcFloor60 = 0.0; MinVolat20m = 0.0; MaxVolat20m = inf
        MinSpeedBe6030 = 0.0; MinEff10m = 0.0; MinDv0945Tape = 0.0; MaxConcurrent = 0; EntryStartSec = 34200
        ExitChannelBars = 300 }   // PitFader's exit (SpikeFader's default is the ~9m 540)

// ---- PitFader's snake_case name -> the research record's field (and an array index for consol). The LEG names are
// side-neutral (Leg.fs): on a LONG they map to FlushFader's low-side fields, on a SHORT to SpikeFader's mirror.
let legMap : Map<string, string * string> =   // neutral -> (FlushFader long, SpikeFader short)
    Map [ "k", ("LowsSinceFirstLow", "HighsSinceFirstHigh")
          for w in [ 600; 300; 180; 120; 60; 30 ] do
              $"k_{w}", ($"LowsSinceFirstLow{w}", $"HighsSinceFirstHigh{w}")
          "bars_since_first", ("BarsSinceFirstLow", "BarsSinceFirstHigh")
          "secs_since_first", ("SecsSinceFirstLow", "SecsSinceFirstHigh")
          "bars_since_first_300", ("BarsSinceFirstLow300", "BarsSinceFirstHigh300")
          "bars_since_first_600", ("BarsSinceFirstLow600", "BarsSinceFirstHigh600")
          "trade_idx", ("TradeIdx", "TradeIdx")
          for w in [ "Sess"; "1200"; "600"; "300"; "120"; "60"; "30" ] do
              $"breach_opp_{w.ToLower()}", ($"Breach{w}", $"BreachLo{w}")
              $"breach_ext_{w.ToLower()}", ($"BreachLo{w}", $"Breach{w}")
          for w in [ 30; 60; 120; 180 ] do $"opp_{w}", ($"Hi{w}", $"Lo{w}")
          "chan_ext", ("ChanLo", "ChanHi"); "chan_opp", ("ChanHi", "ChanLo"); "exit_chan", ("ExitChanHi", "ExitChanLo")
          "chan_ext_prev_60", ("ChanLoPrev", "ChanHiPrev"); "chan_ext_prev_120", ("ChanLoPrev120", "ChanHiPrev120")
          "chan_ext_prev_180", ("ChanLoPrev180", "ChanHiPrev180"); "first_ext_vwap", ("FirstLowVwap", "FirstHighVwap")
          "ols_slope_since_opp", ("OlsSlopeSinceHigh", "OlsSlopeSinceLow"); "ols_r_since_opp", ("OlsRSinceHigh", "OlsRSinceLow")
          "bars_since_opp", ("BarsSinceHigh", "BarsSinceLow")
          "ols_slope_since_flow", ("OlsSlopeSinceFlow", "OlsSlopeSinceFlow"); "ols_r_since_flow", ("OlsRSinceFlow", "OlsRSinceFlow")
          "eff_since_opp", ("EffSinceHigh", "EffSinceLow"); "eff_since_flow", ("EffSinceFlow", "EffSinceFlow")
          "eff9_since_opp", ("Eff9SinceHigh", "Eff9SinceLow"); "eff9_since_flow", ("Eff9SinceFlow", "Eff9SinceFlow")
          "z_since_opp", ("ZSinceHigh", "ZSinceLow"); "z_since_flow", ("ZSinceFlow", "ZSinceFlow")
          "slots_since_opp", ("SlotsSinceHigh", "SlotsSinceLow"); "slots_since_flow", ("SlotsSinceFlow", "SlotsSinceFlow")
          "d_opp_flow", ("DHiFlow", "DLoFlow"); "ols_slope_opp_flow", ("OlsSlopeHiFlow", "OlsSlopeLoFlow")
          "ols_r_opp_flow", ("OlsRHiFlow", "OlsRLoFlow"); "eff_opp_flow", ("EffHiFlow", "EffLoFlow")
          "arm_opp_eff_20m", ("ArmHiEff20m", "ArmLoEff20m"); "arm_opp_eff_10m", ("ArmHiEff10m", "ArmLoEff10m")
          "arm_opp_slots_20m", ("ArmHiSlots20m", "ArmLoSlots20m"); "arm_opp_slots_10m", ("ArmHiSlots10m", "ArmLoSlots10m")
          "vol_leg", ("VolLeg", "VolLeg"); "tc_leg", ("TcLeg", "TcLeg"); "dv_leg", ("DvLeg", "DvLeg")
          "wt_since_ct", ("DownticksSinceUptick", "UpticksSinceDowntick"); "run_wt", ("RunDownticks", "RunUpticks")
          "secs_since_ct", ("SecsSinceLastUptick", "SecsSinceLastDowntick")
          for w in [ 15; 30; 60; 120 ] do
              $"wt_{w}", ($"Dn{w}", $"Up{w}")
              $"ct_{w}", ($"Up{w}", $"Dn{w}")
          "wt_flow", ("DownticksSinceFlow", "UpticksSinceFlow"); "ct_flow", ("UpticksSinceFlow", "DownticksSinceFlow")
          "ext_since_ct", ("LowsSinceUptick", "HighsSinceDowntick"); "chg_since_ct", ("ChgSinceLastUptick", "ChgSinceLastDowntick")
          "chg_since_run_pre_ext", ("ChgSinceRunPreLow", "ChgSinceRunPreHigh")
          "chg_since_run_first_ext", ("ChgSinceRunFirstLow", "ChgSinceRunFirstHigh")
          "chg_since_run_first_wt", ("ChgSinceRunFirstDn", "ChgSinceRunFirstUp") ]
let fieldOf (side: int) (name: string) : string * int =
    if name.StartsWith "consol_" then
        let k = Array.IndexOf(PX.ConsolNames, name.Split('_').[1])
        ((if name.EndsWith "_lag1m" then "ConsolLag1m" else "Consol"), k)
    else
        match legMap.TryFind name with
        | Some (l, s) -> ((if side > 0 then l else s), -1)
        | None ->
            let pascal = name.Split('_') |> Array.map (fun p -> string (Char.ToUpper p.[0]) + p.Substring 1) |> String.concat ""
            (pascal.Replace("Sessmax", "SessMax").Replace("9ema", "9Ema"), -1)
let read (side: int) (o: obj) (name: string) : float voption =
    let fld, k = fieldOf side name
    match o.GetType().GetProperty fld with
    | null -> ValueNone
    | p ->
        match p.GetValue o with
        | :? float as x -> ValueSome x
        | :? int as x -> ValueSome (float x)
        | :? (float[]) as a -> ValueSome a.[k]
        | v -> failwithf "%s: unexpected %O" fld (v.GetType())

let same (a: float) (b: float) =
    (Double.IsNaN a && Double.IsNaN b) || a = b || abs (a - b) <= 1e-9 * max (abs a) (abs b)

// ---- the sessions
let rows = File.ReadAllLines csv |> Array.skip 1 |> Array.map (fun l -> l.Split ',')
let sessions = rows |> Array.groupBy (fun r -> r.[0], r.[1])
let checkedBy = Collections.Generic.Dictionary<string, int * int * int>()   // name -> (compared, mismatches, engines)
let mutable sigMismatch = 0
let mutable nLong, nShort = 0, 0
let firstDiffs = ResizeArray<string>()
for (td, ticker), rs in sessions do
    let day = DateOnly.Parse td
    let bars = rs |> Array.map (fun r -> struct (int r.[2], float r.[3], float r.[4], int r.[5], float r.[6]))
    let pit = bars |> Array.map (fun struct (t, v, vol, tc, d) -> ({ t = t; vwap = v; volume = vol; tradeCount = tc; delta = d } : PT.Bar))
    let trips = PE.runSession 1200 300 [| 540; 600; 900; 1200; 1800; 2400; 3600 |] 34200 54000 0 pit
    let ff = FF.IntradaySystem(ffCfg, ticker, day)
    let sf = SF.IntradaySystem(sfCfg, ticker, day)
    for struct (t, v, vol, tc, _) in bars do
        ff.Process({ etSec = t; vwap = v; volume = vol; tradeCount = tc } : FF.SecBar)
        sf.Process({ etSec = t; vwap = v; volume = vol; tradeCount = tc } : SF.SecBar)
    let lastBar = bars.[bars.Length - 1]
    let struct (lt, lv, lvol, ltc, _) = lastBar
    ff.Flatten({ etSec = lt; vwap = lv; volume = lvol; tradeCount = ltc } : FF.SecBar)
    sf.Flatten({ etSec = lt; vwap = lv; volume = lvol; tradeCount = ltc } : SF.SecBar)
    let early = TradingEdge.Orb.Timezone.early_closes.Contains day
    for side, research in [ (1, ff.Positions |> Seq.map (fun p -> p.SignalSec, box p) |> Map.ofSeq)
                            (-1, sf.Positions |> Seq.map (fun p -> p.SignalSec, box p) |> Map.ofSeq) ] do
        let mine = trips |> Seq.filter (fun t -> t.Side = side) |> Seq.map (fun t -> t.SigT, t) |> Map.ofSeq
        let a, b = set (Map.keys mine), set (Map.keys research)
        if a <> b then
            sigMismatch <- sigMismatch + 1
            printfn "SIGNALS DIFFER %s %s side %+d: PitFader %d, research %d, only PitFader %A, only research %A%s" td ticker side
                a.Count b.Count (Set.difference a b |> Seq.truncate 5 |> List.ofSeq) (Set.difference b a |> Seq.truncate 5 |> List.ofSeq)
                (if early then " (NYSE early close: the research cutoff is 12:00)" else "")
        if side = 1 then nLong <- nLong + a.Count else nShort <- nShort + a.Count
        for s in Set.intersect a b do
            let tr, o = mine.[s], research.[s]
            for k in 0 .. PL.AllNames.Length - 1 do
                let nm = PL.AllNames.[k]
                match read side o nm with
                | ValueNone -> ()
                | ValueSome x ->
                    let c, m, _ = match checkedBy.TryGetValue nm with | true, v -> v | _ -> (0, 0, 0)
                    let bad = not (same tr.Features.[k] x)
                    checkedBy.[nm] <- (c + 1, m + (if bad then 1 else 0), 0)
                    if bad && firstDiffs.Count < 25 then
                        firstDiffs.Add(sprintf "  %s %s side %+d sig %d %s: PitFader %.12g research %.12g" td ticker side s nm tr.Features.[k] x)

printfn "%d sessions; PitFader signals long %d / short %d; sessions x sides with different signal sets: %d" sessions.Length nLong nShort sigMismatch
printfn "feature | compared | mismatches"
for nm in PL.AllNames do
    match checkedBy.TryGetValue nm with
    | true, (c, m, _) -> printfn "%s | %d | %d%s" nm c m (if m > 0 then "  <-- DIFF" else "")
    | _ -> printfn "%s | UNCHECKED (no research engine records it)" nm
for d in firstDiffs do printfn "%s" d
let bad = checkedBy.Values |> Seq.sumBy (fun (_, m, _) -> m)
printfn "TOTAL mismatches %d, signal-set differences %d" bad sigMismatch
exit (if bad = 0 && sigMismatch = 0 then 0 else 1)
