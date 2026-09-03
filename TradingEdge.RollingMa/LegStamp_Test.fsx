// Oracle for the S44 LegCounters first-event stamp.
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System
let mutable fails = 0
let chk name cond = printfn "  %s %s" (if cond then "✓" else "✗ FAIL") name
                    if not cond then fails <- fails + 1

printfn "1. VIRGIN"
let v = LegCounters()
chk "FirstEventPx nan while disarmed" (Double.IsNaN v.FirstEventPx)
chk "MagSinceFirst nan while disarmed" (Double.IsNaN (v.MagSinceFirst 10.0))
chk "RateSinceFirst nan while disarmed" (Double.IsNaN (v.RateSinceFirst(10.0, 36000)))

printfn "\n2. THE ANCHOR HOLDS — later events must NOT move it"
let c = LegCounters()
c.OnEventAt(36000, 100.0)          // first breakout
chk "armed, events = 0" (c.EventsSinceFirst = 0)
chk "anchor = 100" (abs (c.FirstEventPx - 100.0) < 1e-12)
c.Step(); c.OnEventAt(36060, 103.0)   // a LATER, HIGHER breakout
chk "events = 1" (c.EventsSinceFirst = 1)
chk "⭐ anchor STILL 100 (not restamped to 103)" (abs (c.FirstEventPx - 100.0) < 1e-12)
c.Step(); c.OnEventAt(36120, 107.0)
chk "⭐ anchor still 100 after a third event" (abs (c.FirstEventPx - 100.0) < 1e-12)
// this is the whole fix: a per-event restamp would read log(107/107)=0 here.
let m = c.MagSinceFirst 107.0
chk "MagSinceFirst = log(107/100) = 0.067659" (abs (m - log (107.0/100.0)) < 1e-12)
chk "  ... and it is NOT ~0 (the bug it replaces)" (m > 0.06)
chk "elapsed 120s" (c.SecsSinceFirst 36120 = 120)
chk "rate = mag / 2 min" (abs (c.RateSinceFirst(107.0, 36120) - m / 2.0) < 1e-12)

printfn "\n3. RESET clears the anchor; the next leg re-anchors"
c.Reset()
chk "disarmed" (not c.Armed)
chk "anchor cleared" (Double.IsNaN c.FirstEventPx)
c.OnEventAt(36300, 90.0)
chk "new leg anchors at 90" (abs (c.FirstEventPx - 90.0) < 1e-12)
chk "events back to 0" (c.EventsSinceFirst = 0)

printfn "\n4. LEGACY OnEvent — count-only, no anchor"
let l = LegCounters()
l.OnEvent 36000
chk "armed" (l.Armed)
chk "anchor stays nan" (Double.IsNaN l.FirstEventPx)
chk "MagSinceFirst nan" (Double.IsNaN (l.MagSinceFirst 110.0))

printfn "\n5. ZERO-ELAPSED guard"
let z = LegCounters()
z.OnEventAt(36000, 100.0)
chk "rate at the anchor bar itself is nan (0 elapsed)" (Double.IsNaN (z.RateSinceFirst(100.0, 36000)))
chk "mag at the anchor bar is 0, not nan" (abs (z.MagSinceFirst 100.0) < 1e-12)

printfn "\n%s" (if fails = 0 then "ALL PASS" else sprintf "%d FAILURES" fails)
if fails > 0 then exit 1
