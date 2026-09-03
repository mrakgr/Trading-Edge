// Oracle for the S44 BreachCounter stamp (price + ET second of the last breach).
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System

let mutable fails = 0
let chk name cond = printfn "  %s %s" (if cond then "✓" else "✗ FAIL") name
                    if not cond then fails <- fails + 1
let near (a: float) (b: float) = abs (a - b) < 1e-12

printfn "1. VIRGIN — never breached reads -1 / nan / -1"
let v = BreachCounter()
chk "bars = -1" (v.BarsSinceBreach = -1)
chk "BreachPx is nan" (Double.IsNaN v.BreachPx)
chk "BreachSec = -1" (v.BreachSec = -1)
chk "SecsSinceBreach = -1 (mirrors bars)" (v.SecsSinceBreach 40000 = -1)
v.Step()
chk "Step on a virgin counter does NOT arm it" (v.BarsSinceBreach = -1)

printfn "\n2. STAMP — OnBreachAt records the breached extreme and the second"
let c = BreachCounter()
c.Step()
c.OnBreachAt(10.50, 36000)
chk "breach bar reads 0" (c.BarsSinceBreach = 0)
chk "BreachPx = 10.50" (near c.BreachPx 10.50)
chk "BreachSec = 36000" (c.BreachSec = 36000)
chk "elapsed at the breach bar itself = 0" (c.SecsSinceBreach 36000 = 0)
c.Step(); c.Step(); c.Step()
chk "3 bars later reads 3" (c.BarsSinceBreach = 3)
chk "stamp is UNCHANGED by Step" (near c.BreachPx 10.50 && c.BreachSec = 36000)
chk "elapsed 90s later = 90" (c.SecsSinceBreach 36090 = 90)

printfn "\n3. RESTAMP — a later breach overwrites both fields"
c.OnBreachAt(9.75, 36200)
chk "bars back to 0" (c.BarsSinceBreach = 0)
chk "px overwritten" (near c.BreachPx 9.75)
chk "sec overwritten" (c.BreachSec = 36200)

printfn "\n4. LEGACY OnBreach — count-only, leaves the stamp alone"
let l = BreachCounter()
l.Step(); l.OnBreach()
chk "bars = 0" (l.BarsSinceBreach = 0)
chk "unstamped BreachPx stays nan" (Double.IsNaN l.BreachPx)
chk "unstamped BreachSec stays -1" (l.BreachSec = -1)
// a stamped counter later marked by the legacy path keeps its OLD stamp:
// that is why the S44 raw counters are a PARALLEL copy, never a mutation of
// the production ones -- mixing the two paths would silently staledate a stamp.
let m = BreachCounter()
m.OnBreachAt(5.0, 100)
m.OnBreach()
chk "legacy OnBreach after a stamp leaves a STALE price (documents the hazard)"
    (near m.BreachPx 5.0 && m.BreachSec = 100)

printfn "\n5. RESET — clears all three"
c.Reset()
chk "bars = -1" (c.BarsSinceBreach = -1)
chk "px = nan" (Double.IsNaN c.BreachPx)
chk "sec = -1" (c.BreachSec = -1)

printfn "\n6. MAGNITUDE / RATE arithmetic (the engine's resetMag/resetRate)"
let r = BreachCounter()
r.OnBreachAt(100.0, 36000)
let mag = log (105.0 / r.BreachPx)
chk "log(105/100) = 0.048790" (abs (mag - 0.0487901641694) < 1e-10)
let dt = r.SecsSinceBreach 36300      // 300s = 5 min
chk "elapsed 300s" (dt = 300)
let rate = mag / (float dt / 60.0)
chk "rate = mag / 5 min" (abs (rate - mag / 5.0) < 1e-12)
chk "zero-elapsed guard: dt=0 at the breach bar" (r.SecsSinceBreach 36000 = 0)

printfn "\n%s" (if fails = 0 then "ALL PASS" else sprintf "%d FAILURES" fails)
if fails > 0 then exit 1
