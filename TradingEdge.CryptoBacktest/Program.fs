module TradingEdge.CryptoBacktest.Program

open System
open System.Diagnostics
open System.IO
open Argu
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.Backtest
open TradingEdge.CryptoBacktest.TradeLoader
open TradingEdge.CryptoBacktest.Reporting

let defaultDataRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
let defaultBarsRoot = "data/crypto/perps_bars"
let defaultFundingRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps_funding"
let defaultStart = DateTime(2024, 5, 1)
let defaultEnd   = DateTime(2026, 4, 30)
let defaultResultsCsv = "data/crypto/backtest_results.csv"
let defaultSummaryCsv = "data/crypto/backtest_summary.csv"

let defaultTimeframes = [| "1m"; "5m"; "15m"; "1h"; "4h" |]
// Default MA windows in hours. 24h = 1 day; 240h = 10 days.
let defaultMaHours    = [| 24; 72; 168; 240 |]

// =============================================================================
// Argu DU
// =============================================================================

type RunArgs =
    | [<Mandatory; AltCommandLine "-t">] Timeframe of string
    | [<Mandatory; AltCommandLine "-m">] Ma_Hours of int
    | [<AltCommandLine "-s">] Symbol of string
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | Start_Date of string
    | End_Date of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Trips_Dir of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Timeframe _   -> "Bar timeframe (e.g. 1m, 5m, 15m, 1h, 4h)."
            | Ma_Hours _    -> "Rolling-sum signal window length, in HOURS. Timeframe-independent (240h = same span at 1h or 4h)."
            | Symbol _      -> "Symbol to backtest. Defaults to BTCUSDT."
            | Data_Root _   -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _   -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Start_Date _  -> "Inclusive start date YYYY-MM-DD."
            | End_Date _    -> "Inclusive end date YYYY-MM-DD."
            | Notional _    -> "Per-trade notional in quote currency. Default 1000."
            | Taker_Fee _   -> "Per-fill taker fee fraction. Default 0.0004 (4 bps)."
            | Allow_Short   -> "When set, take a short on bear signal instead of going flat."
            | Use_Trades    -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Trips_Dir _   -> "If set, write per-trade round-trips CSV to this directory."

type SweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Ma_Hours of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Min_Daily_Volume of float
    | Min_Bar_Quote_Volume of float
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Long_Adv of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Funding_Root of string
    | No_Funding
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _   -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _   -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _      -> "Restrict sweep to a comma-separated symbol list (default: all symbols with bar parquets)."
            | Start_Date _  -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _    -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _  -> "Comma-separated timeframes. Default 1m,5m,15m,1h,4h."
            | SweepArgs.Ma_Hours _ -> "Comma-separated MA signal-window lengths in HOURS. Default 24,72,168,240 (=1d, 3d, 7d, 10d). Independent of timeframe."
            | Notional _    -> "Per-trade notional. Default 1000."
            | Taker_Fee _   -> "Per-fill taker fee fraction. Default 0.0004."
            | Allow_Short   -> "When set, sweep includes short legs."
            | Use_Trades    -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Min_Daily_Volume _ -> "Minimum average daily quote-volume (USDT) for a symbol-timeframe to be included. Default 500000 (skip cells where a $1000 taker order is unrealistic)."
            | Min_Bar_Quote_Volume _ -> "Per-bar absolute quote-volume floor (USDT, entries only). Catches stub-data bars (e.g. pre-redenomination tail with $5-30/hour trading). Independent of --min-daily-volume. Default 0 (disabled)."
            | Max_Adverse_Pct _ -> "Stop-loss as percent of notional (e.g. 10.0 = stop at -10% MAE). Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent (e.g. 1.0 = 1%/bar). Drives vol-based position sizing: high-vol entries are downsized, low-vol entries get full notional. Set per timeframe (1h~1.0, 2h~1.4, 4h~2.0). Default 0 (disabled)."
            | Min_Long_Adv _  -> "Minimum trailing-90d ADV (USDT/day) required for a long entry. Evaluated at signal-fire time using the engine's leak-free rolling window. Below threshold the signal is consumed (no retry on next bar) and the engine stays flat. Default 0 (disabled)."
            | Min_Short_Adv _ -> "Minimum trailing-90d ADV (USDT/day) required for a short entry. Same semantics as --min-long-adv. Default 0 (disabled)."
            | Vol_Window_Days _ -> "Vol-window length in days for the rolling log-return std used by vol-based position sizing. Independent of --ma-hours. Default 90; lower values (e.g. 7) make the vol estimate more responsive to recent regime shifts."
            | Max_Bar_Price_Ratio _ -> "Bidirectional bar-to-bar price-ratio gap detector for redenomination / relisting events. While a position is open, if bar.Close/prevClose or its inverse exceeds this ratio, the engine force-closes at the previous bar's close, drops the gap bar, AND locks out new entries for one full signal-window period so the rolling state can refresh. Default 0 (disabled). Recommended 3.0 (catches all known Binance redenominations down to MANTRA's 1:4)."
            | Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting even if data is available."
            | Results_Csv _ -> "Per-(symbol,timeframe,ma) results CSV path."
            | Summary_Csv _ -> "Aggregate per-(timeframe,ma) summary CSV path."
            | Parallelism _ -> "Max symbols processed concurrently. Default 4."

type CumsumSweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Cumsum_Thresholds of string
    | Ma_Hours of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Long_Adv of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Funding_Root of string
    | No_Funding
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _ -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _ -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _ -> "Restrict sweep to a comma-separated symbol list (default: all symbols with bar parquets)."
            | Start_Date _ -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _ -> "Comma-separated timeframes. Default 1m. (Cumsum is designed for 1m; other tfs work but the threshold-as-vote-count semantics shift.)"
            | Cumsum_Thresholds _ -> "Comma-separated cumsum clamp magnitudes. Default 60. Long fires at +threshold, short at -threshold; exit on opposite extreme."
            | CumsumSweepArgs.Ma_Hours _ -> "Comma-separated MA window lengths in HOURS for the inner v0 orderflow signal. The cumsum votes on the sign of (buyMa - sellMa) over this window. Default 200 (matches v0)."
            | CumsumSweepArgs.Notional _ -> "Per-trade notional. Default 1000."
            | Taker_Fee _ -> "Per-fill taker fee fraction. Default 0.0004."
            | CumsumSweepArgs.Allow_Short -> "When set, sweep includes short legs."
            | Use_Trades -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Max_Adverse_Pct _ -> "Stop-loss as percent of notional. Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent. Default 0 (disabled)."
            | CumsumSweepArgs.Min_Long_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a long entry. Default 0 (disabled)."
            | CumsumSweepArgs.Min_Short_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a short entry. Default 0."
            | CumsumSweepArgs.Vol_Window_Days _ -> "Vol-window length in days. Default 90."
            | CumsumSweepArgs.Max_Bar_Price_Ratio _ -> "Bar-to-bar price-ratio gap detector. Default 0 (disabled). Recommended 3.0."
            | CumsumSweepArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting even if data is available."
            | Results_Csv _ -> "Per-(symbol,timeframe,threshold) results CSV path."
            | Summary_Csv _ -> "Aggregate per-(timeframe,threshold) summary CSV path."
            | CumsumSweepArgs.Parallelism _ -> "Max symbols processed concurrently. Default 4."

type CumsumZSweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Cumsum_Thresholds of string
    | Ma_Hours of string
    | Magnitude_Mode of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Long_Adv of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Funding_Root of string
    | No_Funding
    | Require_Persistence_For_Exit
    | Vwap_Stop_Hours of int
    | Vol_Stop_Multiplier of float
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _ -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _ -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _ -> "Restrict sweep to a comma-separated symbol list."
            | Start_Date _ -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _ -> "Comma-separated timeframes. Default 1m."
            | Cumsum_Thresholds _ -> "Comma-separated z-cumsum clamp magnitudes (float). Default 10. In erf mode per-bar magnitude is erf(z/sqrt 2) ∈ (-1,+1) so threshold is small (~10-25). In raw-z mode contribution is unbounded; threshold should scale up (~25-200)."
            | Magnitude_Mode _ -> "Per-bar magnitude encoding: 'erf' (default; bounded ∈ (-1,+1)) or 'raw-z' (unbounded; full z-score per bar). Raw-z lets a single multi-sigma bar fire faster but needs a larger threshold."
            | CumsumZSweepArgs.Ma_Hours _ -> "Comma-separated MA window lengths in HOURS for both the std-of-delta normalizer and the buy/sell persistence gate. Default 200."
            | CumsumZSweepArgs.Notional _ -> "Per-trade notional. Default 1000."
            | Taker_Fee _ -> "Per-fill taker fee fraction. Default 0.0004."
            | CumsumZSweepArgs.Allow_Short -> "When set, sweep includes short legs."
            | Use_Trades -> "Force the trade-stream backtest path."
            | Max_Adverse_Pct _ -> "Stop-loss as percent of notional. Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent. Default 0 (disabled). Recommended 0.1019 for 1m."
            | CumsumZSweepArgs.Min_Long_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a long entry."
            | CumsumZSweepArgs.Min_Short_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a short entry."
            | CumsumZSweepArgs.Vol_Window_Days _ -> "Vol-window length in days. Default 90."
            | CumsumZSweepArgs.Max_Bar_Price_Ratio _ -> "Bar-to-bar price-ratio gap detector. Default 0. Recommended 3.0."
            | CumsumZSweepArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting."
            | Require_Persistence_For_Exit -> "When set, opposite-clamp cumsum touches do NOT exit by themselves; positions are held until the 200h persistence regime flips against them. Default off (cumsum-driven exit)."
            | Vwap_Stop_Hours _ -> "Trailing-VWAP-band stop in HOURS. When N>0, exits a long if bar.VWAP <= rolling_min_vwap_Nh, exits a short if bar.VWAP >= rolling_max_vwap_Nh. Volume-weighted so wicks/spikes don't trigger. Replaces --max-adverse-pct when set. Default 0 (disabled)."
            | Vol_Stop_Multiplier _ -> "Vol-based stop in log-return multiples of the symbol's own realized vol (volMa.SampleStd at entry, fixed for the trade). Stop distance = M * volAtEntry; long exits when price drops M*sigma against entry, short symmetric. Adapts to per-symbol vol. Takes precedence over --vwap-stop-hours and --max-adverse-pct. Default 0 (disabled). Try 50, 100, 200, 400, 600, 1000."
            | Results_Csv _ -> "Per-cell results CSV path."
            | Summary_Csv _ -> "Aggregate summary CSV path."
            | CumsumZSweepArgs.Parallelism _ -> "Max symbols processed concurrently. Default 4."

type ExtremeRvolSweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Rvol_Entry_Thresholds of string
    | Rvol_Exit_Threshold of float
    | Price_Rise_Thresholds of string
    | Entry_Rvol_Hours of int
    | Exit_Rvol_Minutes of int
    | Recent_Hours of int
    | Lookback_Days of int
    | Lag_Hours of int
    | Cvd_Minutes of int
    | Stop_High_Window_Minutes of int
    | Time_Stop_Minutes of string
    | Time_Stop_Mode of string
    | Entry_Distance_Max_Pct of string
    | Entry_Distance_Ref of string
    | Reentry_Timeout_Minutes of string
    | Notional of float
    | Taker_Fee of float
    | Use_Trades
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Funding_Root of string
    | No_Funding
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _ -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _ -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _ -> "Restrict sweep to a comma-separated symbol list."
            | Start_Date _ -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _ -> "Comma-separated timeframes. Default 1m."
            | Rvol_Entry_Thresholds _ -> "Comma-separated rvol entry thresholds (mean-recent / mean-baseline volume ratio). Default '8,10,12,15'. Entry requires this AND the price-rise gate AND a 1h-CVD cross."
            | Rvol_Exit_Threshold _ -> "Cover when rvol-exit falls below this. Default 2.0. Must be < entry."
            | Price_Rise_Thresholds _ -> "Comma-separated price-rise thresholds (fractional). Default '0.05,0.10,0.15'. Entry requires close >= (1+threshold) * 8h-MA-lagged-16h."
            | Entry_Rvol_Hours _ -> "Entry-rvol numerator window in HOURS. Default 1. Short window so a single hot hour fires the entry gate before the slower 8h cover frame catches up."
            | Exit_Rvol_Minutes _ -> "Cover-rvol numerator window in MINUTES. Default 75 — kept identical to --cvd-minutes so the cover frame matches the entry-trigger horizon."
            | Recent_Hours _ -> "Recent-window hours for the lagged-MA derivation (the '8h' in 8h-MA-lagged-16h). Default 8."
            | Lookback_Days _ -> "Baseline-window days for the rvol denominator (shared by entry and cover gates). Default 30."
            | Lag_Hours _ -> "Lag (hours) for the lagged-MA. Default 16. Two trailing MAs are over (RecentHours+LagHours)=24h and LagHours=16h."
            | Cvd_Minutes _ -> "Trailing-CVD window in MINUTES for the entry trigger. Default 75 — same length as --exit-rvol-minutes so the trigger and cover MA agree on horizon."
            | Stop_High_Window_Minutes _ -> "High-stop window (minutes) for the trailing-MaxMa of bar.High snapshotted at entry. Default 20."
            | Time_Stop_Minutes _ -> "Comma-separated time-stop windows in MINUTES. When >0, close the trade after this many minutes per --time-stop-mode. Default '90' (chart-review-validated). Pass '0' to disable. Example: '0,30,60,90,120'."
            | Time_Stop_Mode _ -> "Time-stop semantics: 'hard' closes unconditionally at the timer; 'conditional' closes only if the trade isn't currently profitable. Default 'conditional'."
            | Entry_Distance_Max_Pct _ -> "Comma-separated max fractional distance (0-1) from --entry-distance-ref to bar.Close at entry. Reject when (refHigh - close) / refHigh > value. Default '0.20' (sweep-validated). Pass '0' to disable. Example: '0,0.05,0.10,0.15,0.20'. Filters late entries where the flush has already started."
            | Entry_Distance_Ref _ -> "Reference high for the entry-distance gate: 'stop20m' (the 20m trailing max-High, same surface as the stop) or 'high8h' (separate trailing 8h max-High). Default 'high8h' — sweep-validated; the 8h frame is a 'where was the actual peak' indicator, while stop20m is a risk-management level."
            | Reentry_Timeout_Minutes _ -> "Comma-separated re-entry cooldown values in MINUTES, applied AFTER A TIME-STOP ONLY. Default '0' (disabled). Pass '0,15,30,60,120' to sweep. Cover and high-stop do NOT engage the cooldown — only time-stops, which signal that the previous trade went sideways without edge."
            | ExtremeRvolSweepArgs.Notional _ -> "Per-trade notional. Default 1000."
            | Taker_Fee _ -> "Per-fill taker fee fraction. Default 0.0004."
            | Use_Trades -> "Force the trade-stream backtest path (currently not supported; runs from pre-aggregated bars)."
            | Max_Adverse_Pct _ -> "Optional fixed-fraction stop on top of the VWAP stop. Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent. Default 0 (disabled). Recommended 0.1019 for 1m."
            | ExtremeRvolSweepArgs.Min_Short_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a short entry. Default 0."
            | ExtremeRvolSweepArgs.Vol_Window_Days _ -> "Vol-window length in days. Default 90."
            | ExtremeRvolSweepArgs.Max_Bar_Price_Ratio _ -> "Bar-to-bar price-ratio gap detector. Default 0. Recommended 3.0."
            | ExtremeRvolSweepArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting."
            | ExtremeRvolSweepArgs.Results_Csv _ -> "Per-cell results CSV path."
            | ExtremeRvolSweepArgs.Summary_Csv _ -> "Aggregate summary CSV path."
            | ExtremeRvolSweepArgs.Parallelism _ -> "Max symbols processed concurrently. Default 4."

type ExtremeRvolLongSweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Rvol_Entry_Thresholds of string
    | Rvol_Exit_Threshold of float
    | Price_Decline_Thresholds of string
    | Entry_Rvol_Hours of int
    | Exit_Rvol_Minutes of int
    | Recent_Hours of int
    | Lookback_Days of int
    | Lag_Hours of int
    | Cvd_Minutes of int
    | Stop_Low_Window_Minutes of int
    | Time_Stop_Minutes of string
    | Time_Stop_Mode of string
    | Entry_Distance_Max_Pct of string
    | Entry_Distance_Ref of string
    | Notional of float
    | Taker_Fee of float
    | Use_Trades
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Long_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Funding_Root of string
    | No_Funding
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _ -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _ -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _ -> "Restrict sweep to a comma-separated symbol list."
            | Start_Date _ -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _ -> "Comma-separated timeframes. Default 1m."
            | Rvol_Entry_Thresholds _ -> "Comma-separated rvol entry thresholds. Default '8'. Entry requires this AND the price-decline gate AND a 75m-CVD positive cross."
            | Rvol_Exit_Threshold _ -> "Cover when rvol-exit falls below this. Default 2.0."
            | Price_Decline_Thresholds _ -> "Comma-separated price-decline thresholds (fractional). Default '0.05'. Entry requires close <= (1 - threshold) * 8h-MA-lagged-16h."
            | Entry_Rvol_Hours _ -> "Entry-rvol numerator window in HOURS. Default 1."
            | Exit_Rvol_Minutes _ -> "Cover-rvol numerator window in MINUTES. Default 75 — kept identical to --cvd-minutes."
            | Recent_Hours _ -> "Recent-window hours for the lagged-MA. Default 8."
            | Lookback_Days _ -> "Baseline-window days. Default 30."
            | Lag_Hours _ -> "Lag (hours) for the lagged-MA. Default 16."
            | Cvd_Minutes _ -> "Trailing-CVD window in MINUTES. Default 75."
            | Stop_Low_Window_Minutes _ -> "Low-stop window (minutes) for the trailing-MinMa of bar.Low at entry. Default 20."
            | Time_Stop_Minutes _ -> "Comma-separated time-stop windows in MINUTES. Default '90'. Pass '0' to disable."
            | Time_Stop_Mode _ -> "Time-stop semantics: 'hard' or 'conditional'. Default 'conditional'."
            | Entry_Distance_Max_Pct _ -> "Comma-separated max fractional distance from --entry-distance-ref to bar.Close at entry. Reject when (close - refLow) / refLow > value. Default '0.20'. Pass '0' to disable."
            | Entry_Distance_Ref _ -> "Reference low for the entry-distance gate: 'stop20m' or 'low8h'. Default 'low8h'."
            | ExtremeRvolLongSweepArgs.Notional _ -> "Per-trade notional. Default 1000."
            | Taker_Fee _ -> "Per-fill taker fee fraction. Default 0.0004."
            | Use_Trades -> "Force the trade-stream backtest path (currently unsupported)."
            | Max_Adverse_Pct _ -> "Optional fixed-fraction stop. Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent. Recommended 0.1019 for 1m."
            | ExtremeRvolLongSweepArgs.Min_Long_Adv _ -> "Minimum trailing-90d ADV (USDT/day) for a long entry. Default 0."
            | ExtremeRvolLongSweepArgs.Vol_Window_Days _ -> "Vol-window length in days. Default 90."
            | ExtremeRvolLongSweepArgs.Max_Bar_Price_Ratio _ -> "Bar-to-bar price-ratio gap detector. Default 0. Recommended 3.0."
            | ExtremeRvolLongSweepArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting."
            | ExtremeRvolLongSweepArgs.Results_Csv _ -> "Per-cell results CSV path."
            | ExtremeRvolLongSweepArgs.Summary_Csv _ -> "Aggregate summary CSV path."
            | ExtremeRvolLongSweepArgs.Parallelism _ -> "Max symbols processed concurrently. Default 4."

type BuildBreadthArgs =
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframe of string
    | Ma_Hours of int
    | Allow_Short
    | Min_Daily_Volume of float
    | Min_Bar_Quote_Volume of float
    | Min_Long_Adv of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Max_Bar_Price_Ratio of float
    | Per_Symbol_Hour_Out of string
    | Per_Hour_Out of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Bars_Root _ -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _ -> "Restrict to a comma-separated symbol list (default: every symbol with bars at this timeframe)."
            | Start_Date _ -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _ -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframe _ -> "Bar timeframe. Default 1h (matches the v0 backtest)."
            | BuildBreadthArgs.Ma_Hours _ -> "Rolling-sum signal window length, in HOURS. Default 200 (v0 pinned)."
            | BuildBreadthArgs.Allow_Short -> "When set, the engine emits Short on bear regime (else Flat). Default off; v0 uses --allow-short."
            | BuildBreadthArgs.Min_Daily_Volume _ -> "Minimum daily quote volume gate (USDT) for trade fires. Does not affect the breadth target view (LastEvaluatedTarget is updated regardless). Pass-through to the engine for parity with v0."
            | BuildBreadthArgs.Min_Bar_Quote_Volume _ -> "Per-bar absolute liquidity floor (USDT). Same engine-pass-through semantics as --min-daily-volume."
            | BuildBreadthArgs.Min_Long_Adv _ -> "Minimum trailing-90d ADV gate for long trade fires. Default 0 (disabled); v0 uses 28800000."
            | BuildBreadthArgs.Min_Short_Adv _ -> "Minimum trailing-90d ADV gate for short trade fires. Default 0; v0 uses 8000000."
            | BuildBreadthArgs.Vol_Window_Days _ -> "Vol-window in days. Default 90; v0 uses 7."
            | BuildBreadthArgs.Max_Bar_Price_Ratio _ -> "Bar-to-bar price-ratio gap detector. Default 0; v0 uses 3.0."
            | Per_Symbol_Hour_Out _ -> "Per-(symbol, hour) parquet output. Default /mnt/d/trading-edge-bulk/crypto/binance/breadth/per_symbol_hour.parquet"
            | Per_Hour_Out _ -> "Per-hour aggregate parquet output. Default /mnt/d/trading-edge-bulk/crypto/binance/breadth/per_hour.parquet"
            | BuildBreadthArgs.Parallelism _ -> "Max symbols processed concurrently. Default 4."

type BreadthStratifyArgs =
    | [<Mandatory; AltCommandLine "-t">] Trips of string
    | Per_Hour of string
    | Rank_Column of string
    | Value_Column of string
    | Value_Scale of float
    | Value_Label of string
    | Bucket_By of string
    | Value_Cutpoints of string
    | [<AltCommandLine "-n">] Buckets of int
    | [<AltCommandLine "-o">] Output of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Trips _ -> "Path to a trips CSV from a prior `sweep` run (e.g. /tmp/.../results_trips_1h_ma200h_ls.csv)."
            | Per_Hour _ -> "Per-hour breadth parquet (must contain hour_us and the rank column). Default: /mnt/d/trading-edge-bulk/crypto/binance/breadth/per_hour.parquet"
            | Rank_Column _ -> "Column name in the per-hour parquet whose at-trip-entry value drives the join. Default: composite_signed_rank_ma200. For funding-rate breadth, pass --rank-column median_funding_rank --per-hour /mnt/d/.../funding_per_hour.parquet."
            | Value_Column _ -> "Optional second column from the per-hour parquet whose raw value is reported per bucket (so the table shows e.g. funding bps instead of just rank in [0,1])."
            | Value_Scale _ -> "Multiplier applied to --value-column for display only. Default 1.0. Pass 10000 to display funding rates as bps/interval."
            | Value_Label _ -> "Header label for the value column. Default 'value'."
            | Bucket_By _ -> "What to bucket by: 'rank' uses --rank-column directly via FLOOR(rank * N), giving leak-free time-ordered buckets but possibly non-monotonic raw-value ranges. 'value' uses NTILE(N) over --value-column for equal-count buckets — fails when the value distribution has point masses (e.g. funding floors). 'cutpoints' uses --value-cutpoints for fixed boundaries on the value column. Default 'rank'."
            | Value_Cutpoints _ -> "Comma-separated value cutpoints (in raw units, BEFORE --value-scale) for --bucket-by cutpoints. E.g. '0.0,0.00005,0.0001' produces 4 buckets: <0, [0,0.5bps), [0.5,1bps), >=1bps."
            | Buckets _ -> "Number of buckets when --bucket-by ∈ {rank, value}. Ignored when --bucket-by cutpoints. Default 10."
            | Output _ -> "Optional CSV output path for the breakdown. Default prints to stdout only."

type FundingStratifyArgs =
    | [<Mandatory; AltCommandLine "-t">] Trips of string
    | Funding_Root of string
    | Bucket_By of string
    | Value_Cutpoints of string
    | [<AltCommandLine "-n">] Buckets of int
    | [<AltCommandLine "-o">] Output of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | FundingStratifyArgs.Trips _ -> "Path to a trips CSV from a prior `sweep` run."
            | FundingStratifyArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | FundingStratifyArgs.Bucket_By _ -> "'value' uses NTILE(N) over per-symbol funding (equal-count, default). 'cutpoints' uses --value-cutpoints for fixed boundaries on the raw funding rate."
            | FundingStratifyArgs.Value_Cutpoints _ -> "Comma-separated cutpoints in raw decimal units (BEFORE bps scaling) for --bucket-by cutpoints. To match the universe-funding regime cuts, pass '0.00005,0.0000501,0.0001' (sub-baseline / =baseline / mildly elevated / strongly elevated)."
            | FundingStratifyArgs.Buckets _ -> "Number of NTILE buckets when --bucket-by value. Ignored under cutpoints. Default 10."
            | FundingStratifyArgs.Output _ -> "Optional CSV output path for the breakdown."

type BuildFundingBreadthArgs =
    | Funding_Root of string
    | Per_Symbol_Hour of string
    | [<AltCommandLine "-o">] Output of string
    | Extreme_Positive of float
    | Extreme_Negative of float
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | BuildFundingBreadthArgs.Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | Per_Symbol_Hour _ -> "Per-(symbol, hour) parquet from build-breadth. Used to determine which symbols are active in each hour. Default: /mnt/d/trading-edge-bulk/crypto/binance/breadth/per_symbol_hour.parquet"
            | BuildFundingBreadthArgs.Output _ -> "Output parquet path. Default: /mnt/d/trading-edge-bulk/crypto/binance/breadth/funding_per_hour.parquet"
            | Extreme_Positive _ -> "Threshold for 'extreme positive funding' bin (default 0.0001 = 0.01% per interval = ~3.6× the +0.005% baseline)."
            | Extreme_Negative _ -> "Threshold for 'extreme negative funding' bin (default -0.0001)."

type CliArgs =
    | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<RunArgs>
    | [<CliPrefix(CliPrefix.None)>] Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Cumsum_Sweep of ParseResults<CumsumSweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Cumsum_Z_Sweep of ParseResults<CumsumZSweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Extreme_Rvol_Sweep of ParseResults<ExtremeRvolSweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Extreme_Rvol_Long_Sweep of ParseResults<ExtremeRvolLongSweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Vwma_Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Breakout_Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Build_Breadth of ParseResults<BuildBreadthArgs>
    | [<CliPrefix(CliPrefix.None)>] Breadth_Stratify of ParseResults<BreadthStratifyArgs>
    | [<CliPrefix(CliPrefix.None)>] Funding_Stratify of ParseResults<FundingStratifyArgs>
    | [<CliPrefix(CliPrefix.None)>] Build_Funding_Breadth of ParseResults<BuildFundingBreadthArgs>
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Run _            -> "Run one (symbol, timeframe, ma) configuration."
            | Sweep _          -> "Run the full grid: timeframes × MA hours × symbols."
            | Cumsum_Sweep _   -> "Clamped-cumsum 1m successor to the orderflow-MA system: per-bar +/-1 vote on buy/sell sign, cumsum clamped to [-threshold, +threshold]; long fires at +threshold, short at -threshold, exit on opposite extreme."
            | Cumsum_Z_Sweep _ -> "Z-cumsum variant: per-bar magnitude is erf(z/sqrt 2) where z = (buyDV-sellDV)/std200h(buyDV-sellDV). Persistence-gated: long fires at +threshold AND buyMa>sellMa, short symmetric. Exit on opposite clamp; flip only if persistence confirms."
            | Extreme_Rvol_Sweep _ -> "Extreme-rvol blowoff short engine (chart-review-derived): enter short when 8h-mean volume >= Nx 30d-mean AND close >= 1.10 * 8h-MA-lagged-16h AND 1h CVD just turned negative. Stop at trailing-8h-MaxVWAP snapshotted at entry. Cover when rvol < 2x. Short-only by design."
            | Extreme_Rvol_Long_Sweep _ -> "Extreme-rvol long fade engine (mirror of --extreme-rvol-sweep): enter long when 1h-mean volume >= Nx 30d-mean AND close <= (1 - threshold) * 8h-MA-lagged-16h AND 75m CVD just turned positive. Stop at trailing-20m-MinLow snapshotted at entry. Cover when rvol < 2x. Long-only by design."
            | Vwma_Sweep _     -> "Research baseline: same grid, but signal is close-vs-VWMA crossover instead of orderflow ratio."
            | Breakout_Sweep _ -> "Research baseline: same grid, but signal is symmetric N-hour VWAP-breakout/breakdown instead of orderflow ratio."
            | Build_Breadth _  -> "Build the universe-wide breadth panel: per-(symbol, hour) signal trace plus per-hour aggregates with composite signed-volume t-digest rank."
            | Breadth_Stratify _ -> "Stratify a trips CSV by composite_signed_rank_ma200 at entry; per-side per-decile PF / win-rate / P&L breakdown."
            | Funding_Stratify _ -> "Stratify a trips CSV by per-symbol funding rate at entry; per-side per-decile PF / win-rate / P&L breakdown."
            | Build_Funding_Breadth _ -> "Build the universe-wide funding-rate breadth panel: per-hour mean/median/quantile of funding rates across active symbols, plus a t-digest rank of the median."

// =============================================================================
// Helpers
// =============================================================================

let parseDate (s: string) : DateTime =
    DateTime.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)

let parseList (sep: char) (s: string) : string[] =
    s.Split(sep, StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

// =============================================================================
// run
// =============================================================================

let cmdRun (args: ParseResults<RunArgs>) : int =
    let timeframe = args.GetResult RunArgs.Timeframe
    let maHours = args.GetResult RunArgs.Ma_Hours
    let symbol = args.GetResult(RunArgs.Symbol, defaultValue = "BTCUSDT")
    let dataRoot = args.GetResult(RunArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(RunArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains RunArgs.Use_Trades
    let startDate = args.TryGetResult RunArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult RunArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let cfg =
        { defaultConfig maHours with
            Notional = args.GetResult(RunArgs.Notional, defaultValue = 1000.0)
            TakerFee = args.GetResult(RunArgs.Taker_Fee, defaultValue = 0.0004)
            AllowShort = args.Contains RunArgs.Allow_Short }
    let useBarPath = not useTrades && BarLoader.exists barsRoot timeframe symbol
    let pathLabel = if useBarPath then "bars" else "trades"
    printfn "[run] symbol=%s timeframe=%s ma=%dh short=%b range=%s..%s notional=%g fee=%g path=%s"
        symbol timeframe maHours cfg.AllowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        cfg.Notional cfg.TakerFee pathLabel
    let sw = Stopwatch.StartNew()
    let metrics, trips =
        if useBarPath then
            let cell = Cell(symbol, timeframe, cfg)
            // run subcommand always reads funding when available; pass --use-trades
            // to disable both the bar path and funding accounting.
            let fundingRoot = Some defaultFundingRoot
            let metrics, _adv = runCellsFromBars barsRoot symbol startDate endDate [| cell |] fundingRoot
            metrics.[0], cell.Trips
        else
            let inp = {
                DataRoot = dataRoot
                Symbol = symbol
                Timeframe = timeframe
                StartDate = startDate
                EndDate = endDate
                Config = cfg
            }
            let mutable daysSeen = 0
            let mutable tradesAccum = 0L
            let progressEveryDays = 30
            let onDay (d: DateTime) (n: int) =
                daysSeen <- daysSeen + 1
                tradesAccum <- tradesAccum + int64 n
                if daysSeen % progressEveryDays = 0 then
                    printfn "[run] %s @ %s: %d days, %s trades, %.1fs"
                        symbol (d.ToString "yyyy-MM-dd")
                        daysSeen (tradesAccum.ToString "N0")
                        sw.Elapsed.TotalSeconds
            runOne inp (Some onDay)
    sw.Stop()
    printfn "[run] bars=%d trades=%d win_rate=%.3f pf=%.3f net_pnl=%.2f sharpe=%.3f maxDD=%.2f wall=%.1fs"
        metrics.BarsTotal metrics.Trades metrics.WinRate
        metrics.ProfitFactor metrics.NetPnL metrics.Sharpe metrics.MaxDrawdown
        sw.Elapsed.TotalSeconds
    match args.TryGetResult Trips_Dir with
    | Some dir ->
        let path =
            Path.Combine(
                dir,
                sprintf "%s-%s-ma%dh%s.csv"
                    symbol timeframe maHours (if cfg.AllowShort then "-short" else ""))
        writeTrips path symbol timeframe cfg trips
        printfn "[run] wrote %d trips to %s" trips.Length path
    | None -> ()
    0

// =============================================================================
// sweep
// =============================================================================

let cmdSweep (args: ParseResults<SweepArgs>) : int =
    let dataRoot = args.GetResult(SweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(SweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains SweepArgs.Use_Trades
    let startDate = args.TryGetResult SweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult SweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult SweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue defaultTimeframes
    let maHoursList =
        args.TryGetResult SweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaHours
    let symbols =
        match args.TryGetResult SweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            // If we're going through bars, list bar symbols (intersection of
            // all requested timeframes); otherwise fall back to trade symbols.
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(SweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(SweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains SweepArgs.Allow_Short
    let minDailyVolume = args.GetResult(SweepArgs.Min_Daily_Volume, defaultValue = 500_000.0)
    let minBarQuoteVolume = args.GetResult(SweepArgs.Min_Bar_Quote_Volume, defaultValue = 0.0)
    let maxAdversePct = args.GetResult(SweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(SweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(SweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(SweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(SweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(SweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains SweepArgs.No_Funding then None
        else Some (args.GetResult(SweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(SweepArgs.Results_Csv, defaultValue = defaultResultsCsv)
    let summaryCsv = args.GetResult(SweepArgs.Summary_Csv, defaultValue = defaultSummaryCsv)
    let parallelism = args.GetResult(SweepArgs.Parallelism, defaultValue = 4)

    printfn "[sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s minDailyVol=$%s minBarQVol=$%s maxAdversePct=%g referenceVolPct=%g minLongAdv=$%s minShortAdv=$%s volWindowDays=%d"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")
        (minDailyVolume.ToString("N0"))
        (minBarQuoteVolume.ToString("N0"))
        maxAdversePct
        referenceVolPct
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays

    // Stream results: one row per (symbol, timeframe, ma) appended as it
    // finishes, so a partial run still leaves a usable file. Header is
    // written once on first append.
    if File.Exists resultsCsv then File.Delete resultsCsv
    // Clear any per-cell-config trips files from a prior run so this run
    // starts fresh. Pattern: {results-stem}_trips_{tf}_ma{N}_{ls|long}.csv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    // Round-trips are captured per (timeframe, ma_hours, allow_short) so the
    // breakdown report can pool them within each cell-config without mixing
    // across configs.
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (ma: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, ma, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    // Per-symbol average daily quote volume (USDT). Computed once per symbol
    // during runCellsFromBars and kept here for the breakdown's volume-decile
    // stratification.
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maHoursList.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    let progressEveryDays = 30
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for maHours in maHoursList do
                            let cfg =
                                { defaultConfig maHours with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort
                                    MinDailyQuoteVolume = minDailyVolume
                                    MinBarQuoteVolume = minBarQuoteVolume
                                    MaxAdverseFraction = maxAdversePct / 100.0
                                    ReferenceVol = referenceVolPct / 100.0
                                    MinLongAdv = minLongAdv
                                    MinShortAdv = minShortAdv
                                    VolWindowDays = volWindowDays
                                    MaxBarPriceRatio = maxBarPriceRatio }
                            yield Cell(symbol, tf, cfg) |]
                let metrics, adv =
                    if useTrades then
                        let mutable daysSeen = 0
                        let mutable tradesAccum = 0L
                        let onDay (d: DateTime) (n: int) =
                            daysSeen <- daysSeen + 1
                            tradesAccum <- tradesAccum + int64 n
                            if daysSeen % progressEveryDays = 0 then
                                lock writeLock (fun () ->
                                    printfn "[sweep] %s @ %s: %d days, %s trades, %.1fs"
                                        symbol (d.ToString "yyyy-MM-dd")
                                        daysSeen (tradesAccum.ToString "N0")
                                        swSym.Elapsed.TotalSeconds)
                        // Trades path doesn't compute ADV (would require a
                        // separate pass); leave it 0 and the breakdown will
                        // see all symbols in the bottom decile.
                        runCells dataRoot symbol startDate endDate cells (Some onDay), 0.0
                    else
                        runCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    // Capture round-trips per (tf, ma, allowShort) bucket so the
                    // breakdown can pool them later. Cells are 1:1 with metrics.
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.Config.MaWindowHours cell.Config.AllowShort
                        for t in trips do bag.Add t
                        // Also stream this cell's trips to a per-cell-config CSV
                        // (one file per (timeframe, ma_hours, allow_short)).
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.Config.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_ma%dh_%s.csv"
                                        stem cell.Timeframe cell.Config.MaWindowHours shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    // Orb-style breakdown — one section per (timeframe, ma_hours, allow_short)
    // group, written to both stdout and a log file alongside the CSVs.
    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    // Sort groups by their summary order so the most interesting cells come
    // first when scrolling through.
    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[sweep] breakdown -> %s" breakdownLogPath
    printfn "[sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// cumsum-sweep — clamped-cumsum 1m-bar successor to the orderflow-MA system
// =============================================================================
//
// Runs OrderflowCumsum.Engine over 1m bars across the universe. Same
// reporting / breakdown / trips-CSV pipeline as `cmdSweep`. CumsumCell
// surfaces a synthetic StrategyConfig where MaWindowHours stores the
// cumsum threshold so the existing reporting layer just works.

let cmdCumsumSweep (args: ParseResults<CumsumSweepArgs>) : int =
    let dataRoot = args.GetResult(CumsumSweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(CumsumSweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains CumsumSweepArgs.Use_Trades
    let startDate = args.TryGetResult CumsumSweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult CumsumSweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult CumsumSweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue [| "1m" |]
    let thresholds =
        args.TryGetResult CumsumSweepArgs.Cumsum_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 60 |]
    let maHoursList =
        args.TryGetResult CumsumSweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 200 |]
    let symbols =
        match args.TryGetResult CumsumSweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(CumsumSweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(CumsumSweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains CumsumSweepArgs.Allow_Short
    let maxAdversePct = args.GetResult(CumsumSweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(CumsumSweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(CumsumSweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(CumsumSweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(CumsumSweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(CumsumSweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains CumsumSweepArgs.No_Funding then None
        else Some (args.GetResult(CumsumSweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(CumsumSweepArgs.Results_Csv, defaultValue = "data/crypto/cumsum/backtest_results.csv")
    let summaryCsv = args.GetResult(CumsumSweepArgs.Summary_Csv, defaultValue = "data/crypto/cumsum/backtest_summary.csv")
    let parallelism = args.GetResult(CumsumSweepArgs.Parallelism, defaultValue = 4)

    if useTrades then
        eprintfn "[cumsum-sweep] --use-trades is not supported; cumsum runs from pre-aggregated bars."
        2
    else

    printfn "[cumsum-sweep] symbols=%d timeframes=[%s] thresholds=[%s] maHours=[%s] short=%b range=%s..%s parallelism=%d minLongAdv=$%s minShortAdv=$%s volWindowDays=%d maxBarPriceRatio=%g"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (thresholds |> Array.map string))
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays
        maxBarPriceRatio

    Directory.CreateDirectory(Path.GetDirectoryName resultsCsv) |> ignore
    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (th: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, th, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * thresholds.Length * maHoursList.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for th in thresholds do
                            for ma in maHoursList do
                                let cfg : OrderflowCumsum.CumsumConfig =
                                    { OrderflowCumsum.defaultCumsumConfig th with
                                        MaWindowHours = ma
                                        Notional = notional
                                        TakerFee = takerFee
                                        AllowShort = allowShort
                                        MaxAdverseFraction = maxAdversePct / 100.0
                                        ReferenceVol = referenceVolPct / 100.0
                                        MinLongAdv = minLongAdv
                                        MinShortAdv = minShortAdv
                                        VolWindowDays = volWindowDays
                                        MaxBarPriceRatio = maxBarPriceRatio }
                                yield CumsumCell(symbol, tf, cfg) |]
                let metrics, adv =
                    runCumsumCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[cumsum-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.CumsumConfig.CumsumThreshold cell.CumsumConfig.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.CumsumConfig.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_th%d_%s.csv"
                                        stem cell.Timeframe cell.CumsumConfig.CumsumThreshold shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[cumsum-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[cumsum-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[cumsum-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[cumsum-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[cumsum-sweep] breakdown -> %s" breakdownLogPath
    printfn "[cumsum-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// cumsum-z-sweep — short-term-conviction z-cumsum with persistence gate
// =============================================================================
//
// Per-bar magnitude vote = erf(z / sqrt 2) where z = (buyDV - sellDV) /
// std200h(buyDV - sellDV). Cumsum clamped to [-threshold, +threshold] (float).
// Long fires only when cumsum hits +threshold AND buyMa_200h > sellMa_200h
// (persistence gate); short fires symmetrically. Exits on opposite-clamp
// touch regardless of persistence; flip only if persistence confirms the new
// direction, otherwise stay flat.

let cmdCumsumZSweep (args: ParseResults<CumsumZSweepArgs>) : int =
    let dataRoot = args.GetResult(CumsumZSweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(CumsumZSweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains CumsumZSweepArgs.Use_Trades
    let startDate = args.TryGetResult CumsumZSweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult CumsumZSweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult CumsumZSweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue [| "1m" |]
    let thresholds =
        args.TryGetResult CumsumZSweepArgs.Cumsum_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 10.0 |]
    let maHoursList =
        args.TryGetResult CumsumZSweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 200 |]
    let symbols =
        match args.TryGetResult CumsumZSweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(CumsumZSweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(CumsumZSweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains CumsumZSweepArgs.Allow_Short
    let maxAdversePct = args.GetResult(CumsumZSweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(CumsumZSweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(CumsumZSweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(CumsumZSweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(CumsumZSweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(CumsumZSweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let requirePersistenceForExit = args.Contains CumsumZSweepArgs.Require_Persistence_For_Exit
    let vwapStopHours = args.GetResult(CumsumZSweepArgs.Vwap_Stop_Hours, defaultValue = 0)
    let volStopMultiplier = args.GetResult(CumsumZSweepArgs.Vol_Stop_Multiplier, defaultValue = 0.0)
    let magnitudeMode =
        let raw = args.GetResult(CumsumZSweepArgs.Magnitude_Mode, defaultValue = "erf")
        match raw.ToLowerInvariant() with
        | "erf" -> OrderflowCumsumZ.Erf
        | "raw-z" | "rawz" | "raw_z" -> OrderflowCumsumZ.RawZ
        | other ->
            eprintfn "[cumsum-z-sweep] unknown --magnitude-mode '%s' (expected erf|raw-z); using erf" other
            OrderflowCumsumZ.Erf
    let fundingRoot =
        if args.Contains CumsumZSweepArgs.No_Funding then None
        else Some (args.GetResult(CumsumZSweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(CumsumZSweepArgs.Results_Csv, defaultValue = "data/crypto/cumsum_z/backtest_results.csv")
    let summaryCsv = args.GetResult(CumsumZSweepArgs.Summary_Csv, defaultValue = "data/crypto/cumsum_z/backtest_summary.csv")
    let parallelism = args.GetResult(CumsumZSweepArgs.Parallelism, defaultValue = 4)

    if useTrades then
        eprintfn "[cumsum-z-sweep] --use-trades is not supported; runs from pre-aggregated bars."
        2
    else

    let magnitudeModeStr =
        match magnitudeMode with
        | OrderflowCumsumZ.Erf  -> "erf"
        | OrderflowCumsumZ.RawZ -> "raw-z"
    printfn "[cumsum-z-sweep] symbols=%d timeframes=[%s] thresholds=[%s] maHours=[%s] magnitudeMode=%s short=%b range=%s..%s parallelism=%d minLongAdv=$%s minShortAdv=$%s volWindowDays=%d maxBarPriceRatio=%g referenceVolPct=%g requirePersistenceForExit=%b vwapStopHours=%d volStopMultiplier=%g"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (thresholds |> Array.map (sprintf "%g")))
        (String.concat "," (maHoursList |> Array.map string))
        magnitudeModeStr
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays
        maxBarPriceRatio
        referenceVolPct
        requirePersistenceForExit
        vwapStopHours
        volStopMultiplier

    Directory.CreateDirectory(Path.GetDirectoryName resultsCsv) |> ignore
    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (th: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, th, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * thresholds.Length * maHoursList.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for th in thresholds do
                            for ma in maHoursList do
                                let cfg : OrderflowCumsumZ.CumsumZConfig =
                                    { OrderflowCumsumZ.defaultCumsumZConfig th with
                                        MagnitudeMode = magnitudeMode
                                        MaWindowHours = ma
                                        Notional = notional
                                        TakerFee = takerFee
                                        AllowShort = allowShort
                                        MaxAdverseFraction = maxAdversePct / 100.0
                                        ReferenceVol = referenceVolPct / 100.0
                                        MinLongAdv = minLongAdv
                                        MinShortAdv = minShortAdv
                                        VolWindowDays = volWindowDays
                                        MaxBarPriceRatio = maxBarPriceRatio
                                        RequirePersistenceForExit = requirePersistenceForExit
                                        VwapStopHours = vwapStopHours
                                        VolStopMultiplier = volStopMultiplier }
                                yield CumsumZCell(symbol, tf, cfg) |]
                let metrics, adv =
                    runCumsumZCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[cumsum-z-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let thInt = int (round cell.CumsumZConfig.CumsumThreshold)
                        let bag = getTripBag cell.Timeframe thInt cell.CumsumZConfig.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.CumsumZConfig.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_th%g_%s.csv"
                                        stem cell.Timeframe cell.CumsumZConfig.CumsumThreshold shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[cumsum-z-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[cumsum-z-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[cumsum-z-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[cumsum-z-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[cumsum-z-sweep] breakdown -> %s" breakdownLogPath
    printfn "[cumsum-z-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// extreme-rvol-sweep — chart-review-derived blowoff short engine
// =============================================================================
//
// Sweeps (rvol entry threshold × price-rise threshold) cells. Always
// short-only. Output paths default under data/crypto/extreme_rvol/.
// Reuses Reporting.appendTrips / appendResults / printGroupBreakdown so
// downstream stratify scripts (volume_momentum_stratify.py etc.) work
// unchanged on the new trip CSVs.

let cmdExtremeRvolSweep (args: ParseResults<ExtremeRvolSweepArgs>) : int =
    let dataRoot = args.GetResult(ExtremeRvolSweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(ExtremeRvolSweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains ExtremeRvolSweepArgs.Use_Trades
    let startDate = args.TryGetResult ExtremeRvolSweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult ExtremeRvolSweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult ExtremeRvolSweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue [| "1m" |]
    let rvolEntryThresholds =
        args.TryGetResult ExtremeRvolSweepArgs.Rvol_Entry_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 8.0; 10.0; 12.0; 15.0 |]
    let priceRiseThresholds =
        args.TryGetResult ExtremeRvolSweepArgs.Price_Rise_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 0.05; 0.10; 0.15 |]
    let rvolExitThreshold = args.GetResult(ExtremeRvolSweepArgs.Rvol_Exit_Threshold, defaultValue = 2.0)
    let entryRvolHours = args.GetResult(ExtremeRvolSweepArgs.Entry_Rvol_Hours, defaultValue = 1)
    let exitRvolMinutes = args.GetResult(ExtremeRvolSweepArgs.Exit_Rvol_Minutes, defaultValue = 75)
    let recentHours = args.GetResult(ExtremeRvolSweepArgs.Recent_Hours, defaultValue = 8)
    let lookbackDays = args.GetResult(ExtremeRvolSweepArgs.Lookback_Days, defaultValue = 30)
    let lagHours = args.GetResult(ExtremeRvolSweepArgs.Lag_Hours, defaultValue = 16)
    let cvdMinutes = args.GetResult(ExtremeRvolSweepArgs.Cvd_Minutes, defaultValue = 75)
    let stopHighWindowMinutes = args.GetResult(ExtremeRvolSweepArgs.Stop_High_Window_Minutes, defaultValue = 20)
    let timeStopMinutesList =
        args.TryGetResult ExtremeRvolSweepArgs.Time_Stop_Minutes
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 90 |]
    let timeStopMode =
        let raw = args.GetResult(ExtremeRvolSweepArgs.Time_Stop_Mode, defaultValue = "conditional")
        match raw.ToLowerInvariant() with
        | "hard" -> OrderflowExtremeRvol.Hard
        | "conditional" | "cond" -> OrderflowExtremeRvol.Conditional
        | other ->
            eprintfn "[extreme-rvol-sweep] unknown --time-stop-mode '%s' (expected hard|conditional); using conditional" other
            OrderflowExtremeRvol.Conditional
    let entryDistanceMaxPctList =
        args.TryGetResult ExtremeRvolSweepArgs.Entry_Distance_Max_Pct
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 0.20 |]
    let entryDistanceRef =
        let raw = args.GetResult(ExtremeRvolSweepArgs.Entry_Distance_Ref, defaultValue = "high8h")
        match raw.ToLowerInvariant() with
        | "stop20m" | "stop" -> OrderflowExtremeRvol.Stop20m
        | "high8h" | "8h" -> OrderflowExtremeRvol.High8h
        | other ->
            eprintfn "[extreme-rvol-sweep] unknown --entry-distance-ref '%s' (expected stop20m|high8h); using stop20m" other
            OrderflowExtremeRvol.Stop20m
    let reentryTimeoutMinutesList =
        args.TryGetResult ExtremeRvolSweepArgs.Reentry_Timeout_Minutes
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 0 |]
    let symbols =
        match args.TryGetResult ExtremeRvolSweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(ExtremeRvolSweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(ExtremeRvolSweepArgs.Taker_Fee, defaultValue = 0.0004)
    let maxAdversePct = args.GetResult(ExtremeRvolSweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(ExtremeRvolSweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minShortAdv = args.GetResult(ExtremeRvolSweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(ExtremeRvolSweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(ExtremeRvolSweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains ExtremeRvolSweepArgs.No_Funding then None
        else Some (args.GetResult(ExtremeRvolSweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(ExtremeRvolSweepArgs.Results_Csv, defaultValue = "data/crypto/extreme_rvol/backtest_results.csv")
    let summaryCsv = args.GetResult(ExtremeRvolSweepArgs.Summary_Csv, defaultValue = "data/crypto/extreme_rvol/backtest_summary.csv")
    let parallelism = args.GetResult(ExtremeRvolSweepArgs.Parallelism, defaultValue = 4)

    if useTrades then
        eprintfn "[extreme-rvol-sweep] --use-trades is not supported; runs from pre-aggregated bars."
        2
    else

    let timeStopModeStr =
        match timeStopMode with
        | OrderflowExtremeRvol.Hard -> "hard"
        | OrderflowExtremeRvol.Conditional -> "conditional"
    let entryDistanceRefStr =
        match entryDistanceRef with
        | OrderflowExtremeRvol.Stop20m -> "stop20m"
        | OrderflowExtremeRvol.High8h  -> "high8h"
    printfn "[extreme-rvol-sweep] symbols=%d timeframes=[%s] rvolEntry=[%s] rvolExit=%g priceRise=[%s] entryRvolH=%d exitRvolM=%d recentH=%d lookbackD=%d lagH=%d cvdM=%d stopHighM=%d timeStopMin=[%s] timeStopMode=%s entryDistMaxPct=[%s] entryDistRef=%s reentryTimeoutMin=[%s] range=%s..%s parallelism=%d minShortAdv=$%s volWindowDays=%d maxBarPriceRatio=%g referenceVolPct=%g"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (rvolEntryThresholds |> Array.map (sprintf "%g")))
        rvolExitThreshold
        (String.concat "," (priceRiseThresholds |> Array.map (sprintf "%g")))
        entryRvolHours exitRvolMinutes recentHours lookbackDays lagHours cvdMinutes stopHighWindowMinutes
        (String.concat "," (timeStopMinutesList |> Array.map string))
        timeStopModeStr
        (String.concat "," (entryDistanceMaxPctList |> Array.map (sprintf "%g")))
        entryDistanceRefStr
        (String.concat "," (reentryTimeoutMinutesList |> Array.map string))
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (minShortAdv.ToString("N0"))
        volWindowDays
        maxBarPriceRatio
        referenceVolPct

    Directory.CreateDirectory(Path.GetDirectoryName resultsCsv) |> ignore
    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    // Trip-bag key: (timeframe, rvolEntry*10 as int, allowShort=true). The
    // *10 lets a 10x threshold round-trip through the existing
    // MaWindowHours-as-int reporting field without losing precision for
    // typical fractional thresholds (e.g. 12.5 -> 125).
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (th: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, th, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells =
        symbols.Length * timeframes.Length
        * rvolEntryThresholds.Length * priceRiseThresholds.Length
        * timeStopMinutesList.Length
        * entryDistanceMaxPctList.Length
        * reentryTimeoutMinutesList.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for rvolEntry in rvolEntryThresholds do
                            for priceRise in priceRiseThresholds do
                                for timeStop in timeStopMinutesList do
                                    for entryDistMaxPct in entryDistanceMaxPctList do
                                        for reentryTimeout in reentryTimeoutMinutesList do
                                            let cfg : OrderflowExtremeRvol.ExtremeRvolConfig =
                                                { OrderflowExtremeRvol.defaultExtremeRvolConfig () with
                                                    RvolEntryThreshold = rvolEntry
                                                    RvolExitThreshold = rvolExitThreshold
                                                    PriceRiseThreshold = priceRise
                                                    EntryRvolHours = entryRvolHours
                                                    ExitRvolMinutes = exitRvolMinutes
                                                    RecentHours = recentHours
                                                    LookbackDays = lookbackDays
                                                    LagHours = lagHours
                                                    CvdMinutes = cvdMinutes
                                                    StopHighWindowMinutes = stopHighWindowMinutes
                                                    TimeStopMinutes = timeStop
                                                    TimeStopMode = timeStopMode
                                                    EntryDistanceMaxPct = entryDistMaxPct
                                                    EntryDistanceRef = entryDistanceRef
                                                    ReentryTimeoutMinutes = reentryTimeout
                                                    Notional = notional
                                                    TakerFee = takerFee
                                                    MaxAdverseFraction = maxAdversePct / 100.0
                                                    ReferenceVol = referenceVolPct / 100.0
                                                    MinShortAdv = minShortAdv
                                                    VolWindowDays = volWindowDays
                                                    MaxBarPriceRatio = maxBarPriceRatio }
                                            yield ExtremeRvolCell(symbol, tf, cfg) |]
                let metrics, adv =
                    runExtremeRvolCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[extreme-rvol-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let rvolKey = int (round (cell.ExtremeRvolConfig.RvolEntryThreshold * 10.0))
                        let bag = getTripBag cell.Timeframe rvolKey cell.ExtremeRvolConfig.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let tsTag =
                                    if cell.ExtremeRvolConfig.TimeStopMinutes > 0
                                    then sprintf "_ts%dm" cell.ExtremeRvolConfig.TimeStopMinutes
                                    else ""
                                let edTag =
                                    if cell.ExtremeRvolConfig.EntryDistanceMaxPct > 0.0
                                    then sprintf "_ed%g" cell.ExtremeRvolConfig.EntryDistanceMaxPct
                                    else ""
                                let rtTag =
                                    if cell.ExtremeRvolConfig.ReentryTimeoutMinutes > 0
                                    then sprintf "_rt%dm" cell.ExtremeRvolConfig.ReentryTimeoutMinutes
                                    else ""
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_rvol%g_pr%g%s%s%s_short.csv"
                                        stem cell.Timeframe
                                        cell.ExtremeRvolConfig.RvolEntryThreshold
                                        cell.ExtremeRvolConfig.PriceRiseThreshold
                                        tsTag
                                        edTag
                                        rtTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[extreme-rvol-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[extreme-rvol-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[extreme-rvol-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[extreme-rvol-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[extreme-rvol-sweep] breakdown -> %s" breakdownLogPath
    printfn "[extreme-rvol-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// extreme-rvol-long-sweep — long fade mirror of extreme-rvol-sweep
// =============================================================================
//
// Long-only mirror of the short engine. Same wrapper pattern, same trip-CSV
// schema, same breakdown layer — just substituting MinLow for MaxHigh and
// the symmetric CVD cross. Default output paths under
// data/crypto/extreme_rvol_long/.

let cmdExtremeRvolLongSweep (args: ParseResults<ExtremeRvolLongSweepArgs>) : int =
    let dataRoot = args.GetResult(ExtremeRvolLongSweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(ExtremeRvolLongSweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains ExtremeRvolLongSweepArgs.Use_Trades
    let startDate = args.TryGetResult ExtremeRvolLongSweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult ExtremeRvolLongSweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult ExtremeRvolLongSweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue [| "1m" |]
    let rvolEntryThresholds =
        args.TryGetResult ExtremeRvolLongSweepArgs.Rvol_Entry_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 8.0 |]
    let priceDeclineThresholds =
        args.TryGetResult ExtremeRvolLongSweepArgs.Price_Decline_Thresholds
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 0.05 |]
    let rvolExitThreshold = args.GetResult(ExtremeRvolLongSweepArgs.Rvol_Exit_Threshold, defaultValue = 2.0)
    let entryRvolHours = args.GetResult(ExtremeRvolLongSweepArgs.Entry_Rvol_Hours, defaultValue = 1)
    let exitRvolMinutes = args.GetResult(ExtremeRvolLongSweepArgs.Exit_Rvol_Minutes, defaultValue = 75)
    let recentHours = args.GetResult(ExtremeRvolLongSweepArgs.Recent_Hours, defaultValue = 8)
    let lookbackDays = args.GetResult(ExtremeRvolLongSweepArgs.Lookback_Days, defaultValue = 30)
    let lagHours = args.GetResult(ExtremeRvolLongSweepArgs.Lag_Hours, defaultValue = 16)
    let cvdMinutes = args.GetResult(ExtremeRvolLongSweepArgs.Cvd_Minutes, defaultValue = 75)
    let stopLowWindowMinutes = args.GetResult(ExtremeRvolLongSweepArgs.Stop_Low_Window_Minutes, defaultValue = 20)
    let timeStopMinutesList =
        args.TryGetResult ExtremeRvolLongSweepArgs.Time_Stop_Minutes
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue [| 90 |]
    let timeStopMode =
        let raw = args.GetResult(ExtremeRvolLongSweepArgs.Time_Stop_Mode, defaultValue = "conditional")
        match raw.ToLowerInvariant() with
        | "hard" -> OrderflowExtremeRvolLong.Hard
        | "conditional" | "cond" -> OrderflowExtremeRvolLong.Conditional
        | other ->
            eprintfn "[extreme-rvol-long-sweep] unknown --time-stop-mode '%s'; using conditional" other
            OrderflowExtremeRvolLong.Conditional
    let entryDistanceMaxPctList =
        args.TryGetResult ExtremeRvolLongSweepArgs.Entry_Distance_Max_Pct
        |> Option.map (parseList ',')
        |> Option.map (Array.map (fun s -> Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)))
        |> Option.defaultValue [| 0.20 |]
    let entryDistanceRef =
        let raw = args.GetResult(ExtremeRvolLongSweepArgs.Entry_Distance_Ref, defaultValue = "low8h")
        match raw.ToLowerInvariant() with
        | "stop20m" | "stop" -> OrderflowExtremeRvolLong.Stop20m
        | "low8h" | "8h" -> OrderflowExtremeRvolLong.Low8h
        | other ->
            eprintfn "[extreme-rvol-long-sweep] unknown --entry-distance-ref '%s'; using low8h" other
            OrderflowExtremeRvolLong.Low8h
    let symbols =
        match args.TryGetResult ExtremeRvolLongSweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(ExtremeRvolLongSweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(ExtremeRvolLongSweepArgs.Taker_Fee, defaultValue = 0.0004)
    let maxAdversePct = args.GetResult(ExtremeRvolLongSweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(ExtremeRvolLongSweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(ExtremeRvolLongSweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(ExtremeRvolLongSweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(ExtremeRvolLongSweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains ExtremeRvolLongSweepArgs.No_Funding then None
        else Some (args.GetResult(ExtremeRvolLongSweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(ExtremeRvolLongSweepArgs.Results_Csv, defaultValue = "data/crypto/extreme_rvol_long/backtest_results.csv")
    let summaryCsv = args.GetResult(ExtremeRvolLongSweepArgs.Summary_Csv, defaultValue = "data/crypto/extreme_rvol_long/backtest_summary.csv")
    let parallelism = args.GetResult(ExtremeRvolLongSweepArgs.Parallelism, defaultValue = 4)

    if useTrades then
        eprintfn "[extreme-rvol-long-sweep] --use-trades is not supported; runs from pre-aggregated bars."
        2
    else

    let timeStopModeStr =
        match timeStopMode with
        | OrderflowExtremeRvolLong.Hard -> "hard"
        | OrderflowExtremeRvolLong.Conditional -> "conditional"
    let entryDistanceRefStr =
        match entryDistanceRef with
        | OrderflowExtremeRvolLong.Stop20m -> "stop20m"
        | OrderflowExtremeRvolLong.Low8h   -> "low8h"
    printfn "[extreme-rvol-long-sweep] symbols=%d timeframes=[%s] rvolEntry=[%s] rvolExit=%g priceDecline=[%s] entryRvolH=%d exitRvolM=%d cvdM=%d stopLowM=%d timeStopMin=[%s] timeStopMode=%s entryDistMaxPct=[%s] entryDistRef=%s range=%s..%s parallelism=%d minLongAdv=$%s maxBarPriceRatio=%g referenceVolPct=%g"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (rvolEntryThresholds |> Array.map (sprintf "%g")))
        rvolExitThreshold
        (String.concat "," (priceDeclineThresholds |> Array.map (sprintf "%g")))
        entryRvolHours exitRvolMinutes cvdMinutes stopLowWindowMinutes
        (String.concat "," (timeStopMinutesList |> Array.map string))
        timeStopModeStr
        (String.concat "," (entryDistanceMaxPctList |> Array.map (sprintf "%g")))
        entryDistanceRefStr
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (minLongAdv.ToString("N0"))
        maxBarPriceRatio
        referenceVolPct

    Directory.CreateDirectory(Path.GetDirectoryName resultsCsv) |> ignore
    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (th: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, th, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells =
        symbols.Length * timeframes.Length
        * rvolEntryThresholds.Length * priceDeclineThresholds.Length
        * timeStopMinutesList.Length
        * entryDistanceMaxPctList.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for rvolEntry in rvolEntryThresholds do
                            for priceDecline in priceDeclineThresholds do
                                for timeStop in timeStopMinutesList do
                                    for entryDistMaxPct in entryDistanceMaxPctList do
                                        let cfg : OrderflowExtremeRvolLong.ExtremeRvolLongConfig =
                                            { OrderflowExtremeRvolLong.defaultExtremeRvolLongConfig () with
                                                RvolEntryThreshold = rvolEntry
                                                RvolExitThreshold = rvolExitThreshold
                                                PriceDeclineThreshold = priceDecline
                                                EntryRvolHours = entryRvolHours
                                                ExitRvolMinutes = exitRvolMinutes
                                                RecentHours = recentHours
                                                LookbackDays = lookbackDays
                                                LagHours = lagHours
                                                CvdMinutes = cvdMinutes
                                                StopLowWindowMinutes = stopLowWindowMinutes
                                                TimeStopMinutes = timeStop
                                                TimeStopMode = timeStopMode
                                                EntryDistanceMaxPct = entryDistMaxPct
                                                EntryDistanceRef = entryDistanceRef
                                                Notional = notional
                                                TakerFee = takerFee
                                                MaxAdverseFraction = maxAdversePct / 100.0
                                                ReferenceVol = referenceVolPct / 100.0
                                                MinLongAdv = minLongAdv
                                                VolWindowDays = volWindowDays
                                                MaxBarPriceRatio = maxBarPriceRatio }
                                        yield ExtremeRvolLongCell(symbol, tf, cfg) |]
                let metrics, adv =
                    runExtremeRvolLongCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[extreme-rvol-long-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let rvolKey = int (round (cell.ExtremeRvolLongConfig.RvolEntryThreshold * 10.0))
                        // allowShort=false flag in the bag key — long bag is its own group
                        let bag = getTripBag cell.Timeframe rvolKey false
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let tsTag =
                                    if cell.ExtremeRvolLongConfig.TimeStopMinutes > 0
                                    then sprintf "_ts%dm" cell.ExtremeRvolLongConfig.TimeStopMinutes
                                    else ""
                                let edTag =
                                    if cell.ExtremeRvolLongConfig.EntryDistanceMaxPct > 0.0
                                    then sprintf "_ed%g" cell.ExtremeRvolLongConfig.EntryDistanceMaxPct
                                    else ""
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_rvol%g_pd%g%s%s_long.csv"
                                        stem cell.Timeframe
                                        cell.ExtremeRvolLongConfig.RvolEntryThreshold
                                        cell.ExtremeRvolLongConfig.PriceDeclineThreshold
                                        tsTag
                                        edTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[extreme-rvol-long-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[extreme-rvol-long-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[extreme-rvol-long-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[extreme-rvol-long-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[extreme-rvol-long-sweep] breakdown -> %s" breakdownLogPath
    printfn "[extreme-rvol-long-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// vwma-sweep — research baseline (close-vs-VWMA crossover signal)
// =============================================================================
//
// Same flags as `sweep`, same gates, same output schema. Default output paths
// suffixed with `_vwma` so the two backtests don't clobber each other when
// the user runs both. Default results dir is `data/crypto/vwma/`.
//
// Wraps `Backtest.VwmaCell` instead of `Backtest.Cell` — the only difference
// from `cmdSweep`. We deliberately did NOT factor the two into a generic
// helper: this command exists to verify a one-off research hypothesis (does
// VWMA-crossover beat orderflow-MA?) and will be deleted once that question
// is answered. Keeping it self-contained makes the deletion easy.

let cmdVwmaSweep (args: ParseResults<SweepArgs>) : int =
    let dataRoot = args.GetResult(SweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(SweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains SweepArgs.Use_Trades
    let startDate = args.TryGetResult SweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult SweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult SweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue defaultTimeframes
    let maHoursList =
        args.TryGetResult SweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaHours
    let symbols =
        match args.TryGetResult SweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(SweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(SweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains SweepArgs.Allow_Short
    let minDailyVolume = args.GetResult(SweepArgs.Min_Daily_Volume, defaultValue = 500_000.0)
    let minBarQuoteVolume = args.GetResult(SweepArgs.Min_Bar_Quote_Volume, defaultValue = 0.0)
    let maxAdversePct = args.GetResult(SweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(SweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(SweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(SweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(SweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(SweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains SweepArgs.No_Funding then None
        else Some (args.GetResult(SweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(SweepArgs.Results_Csv, defaultValue = "data/crypto/vwma/backtest_results.csv")
    let summaryCsv = args.GetResult(SweepArgs.Summary_Csv, defaultValue = "data/crypto/vwma/backtest_summary.csv")
    let parallelism = args.GetResult(SweepArgs.Parallelism, defaultValue = 4)

    printfn "[vwma-sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s minDailyVol=$%s minBarQVol=$%s maxAdversePct=%g referenceVolPct=%g minLongAdv=$%s minShortAdv=$%s volWindowDays=%d"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")
        (minDailyVolume.ToString("N0"))
        (minBarQuoteVolume.ToString("N0"))
        maxAdversePct
        referenceVolPct
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays

    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (ma: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, ma, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maHoursList.Length
    let mutable doneCells = 0

    // VWMA-cell variant of runCellsFromBars. Inline because there's only one
    // call site and we don't want a generic helper for one-off research code.
    let runVwmaCellsFromBars
        (symbol: string)
        (cells: VwmaCell[])
        : Metrics[] * float =
        let fundingEvents =
            match fundingRoot with
            | Some root when FundingLoader.exists root symbol ->
                FundingLoader.loadByDate root symbol startDate endDate
                |> Array.map (fun e -> e.TimestampUs, e.Rate)
            | _ -> [||]
        if fundingEvents.Length > 0 then
            for cell in cells do
                cell.SetFundingEvents fundingEvents
        let byTf = cells |> Array.groupBy (fun c -> c.Timeframe)
        let mutable adv = 0.0
        let mutable advSet = false
        for (tf, group) in byTf do
            let bars = BarLoader.loadByDate barsRoot tf symbol startDate endDate
            if not advSet && bars.Length > 0 then
                adv <- avgDailyVolume bars
                advSet <- true
            for cell in group do
                cell.PushBars bars
        for cell in cells do
            cell.Close()
        let metrics = cells |> Array.map (fun c -> c.BuildMetrics())
        metrics, adv

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for maHours in maHoursList do
                            let cfg =
                                { defaultConfig maHours with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort
                                    MinDailyQuoteVolume = minDailyVolume
                                    MinBarQuoteVolume = minBarQuoteVolume
                                    MaxAdverseFraction = maxAdversePct / 100.0
                                    ReferenceVol = referenceVolPct / 100.0
                                    MinLongAdv = minLongAdv
                                    MinShortAdv = minShortAdv
                                    VolWindowDays = volWindowDays
                                    MaxBarPriceRatio = maxBarPriceRatio }
                            yield VwmaCell(symbol, tf, cfg) |]
                let metrics, adv =
                    if useTrades then
                        // Trades-path is unsupported for vwma-sweep — the
                        // research command only runs against pre-aggregated
                        // bars. Fall back to bars regardless of --use-trades.
                        runVwmaCellsFromBars symbol cells
                    else
                        runVwmaCellsFromBars symbol cells
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[vwma-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.Config.MaWindowHours cell.Config.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.Config.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_ma%dh_%s.csv"
                                        stem cell.Timeframe cell.Config.MaWindowHours shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[vwma-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[vwma-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[vwma-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[vwma-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[vwma-sweep] breakdown -> %s" breakdownLogPath
    printfn "[vwma-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// breakout-sweep — research baseline (VWAP N-hour breakout signal)
// =============================================================================
//
// Same flags as `sweep` and `vwma-sweep`. Default output paths under
// `data/crypto/breakout/`. Wraps `Backtest.BreakoutCell`.

let cmdBreakoutSweep (args: ParseResults<SweepArgs>) : int =
    let dataRoot = args.GetResult(SweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(SweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains SweepArgs.Use_Trades
    let startDate = args.TryGetResult SweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult SweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult SweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue defaultTimeframes
    let maHoursList =
        args.TryGetResult SweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaHours
    let symbols =
        match args.TryGetResult SweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(SweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(SweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains SweepArgs.Allow_Short
    let minDailyVolume = args.GetResult(SweepArgs.Min_Daily_Volume, defaultValue = 500_000.0)
    let minBarQuoteVolume = args.GetResult(SweepArgs.Min_Bar_Quote_Volume, defaultValue = 0.0)
    let maxAdversePct = args.GetResult(SweepArgs.Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(SweepArgs.Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(SweepArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(SweepArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(SweepArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(SweepArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let fundingRoot =
        if args.Contains SweepArgs.No_Funding then None
        else Some (args.GetResult(SweepArgs.Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(SweepArgs.Results_Csv, defaultValue = "data/crypto/breakout/backtest_results.csv")
    let summaryCsv = args.GetResult(SweepArgs.Summary_Csv, defaultValue = "data/crypto/breakout/backtest_summary.csv")
    let parallelism = args.GetResult(SweepArgs.Parallelism, defaultValue = 4)

    printfn "[breakout-sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s minDailyVol=$%s minBarQVol=$%s maxAdversePct=%g referenceVolPct=%g minLongAdv=$%s minShortAdv=$%s volWindowDays=%d"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")
        (minDailyVolume.ToString("N0"))
        (minBarQuoteVolume.ToString("N0"))
        maxAdversePct
        referenceVolPct
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays

    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (ma: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, ma, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maHoursList.Length
    let mutable doneCells = 0

    let runBreakoutCellsFromBars
        (symbol: string)
        (cells: BreakoutCell[])
        : Metrics[] * float =
        let fundingEvents =
            match fundingRoot with
            | Some root when FundingLoader.exists root symbol ->
                FundingLoader.loadByDate root symbol startDate endDate
                |> Array.map (fun e -> e.TimestampUs, e.Rate)
            | _ -> [||]
        if fundingEvents.Length > 0 then
            for cell in cells do
                cell.SetFundingEvents fundingEvents
        let byTf = cells |> Array.groupBy (fun c -> c.Timeframe)
        let mutable adv = 0.0
        let mutable advSet = false
        for (tf, group) in byTf do
            let bars = BarLoader.loadByDate barsRoot tf symbol startDate endDate
            if not advSet && bars.Length > 0 then
                adv <- avgDailyVolume bars
                advSet <- true
            for cell in group do
                cell.PushBars bars
        for cell in cells do
            cell.Close()
        let metrics = cells |> Array.map (fun c -> c.BuildMetrics())
        metrics, adv

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for maHours in maHoursList do
                            let cfg =
                                { defaultConfig maHours with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort
                                    MinDailyQuoteVolume = minDailyVolume
                                    MinBarQuoteVolume = minBarQuoteVolume
                                    MaxAdverseFraction = maxAdversePct / 100.0
                                    ReferenceVol = referenceVolPct / 100.0
                                    MinLongAdv = minLongAdv
                                    MinShortAdv = minShortAdv
                                    VolWindowDays = volWindowDays
                                    MaxBarPriceRatio = maxBarPriceRatio }
                            yield BreakoutCell(symbol, tf, cfg) |]
                let metrics, adv = runBreakoutCellsFromBars symbol cells
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[breakout-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.Config.MaWindowHours cell.Config.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.Config.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_ma%dh_%s.csv"
                                        stem cell.Timeframe cell.Config.MaWindowHours shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[breakout-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[breakout-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[breakout-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[breakout-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[breakout-sweep] breakdown -> %s" breakdownLogPath
    printfn "[breakout-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// build-breadth
// =============================================================================

let cmdBuildBreadth (args: ParseResults<BuildBreadthArgs>) : int =
    let barsRoot = args.GetResult(BuildBreadthArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let timeframe = args.GetResult(BuildBreadthArgs.Timeframe, defaultValue = "1h")
    let maHours = args.GetResult(BuildBreadthArgs.Ma_Hours, defaultValue = 200)
    let allowShort = args.Contains BuildBreadthArgs.Allow_Short
    let minDailyVolume = args.GetResult(BuildBreadthArgs.Min_Daily_Volume, defaultValue = 0.0)
    let minBarQuoteVolume = args.GetResult(BuildBreadthArgs.Min_Bar_Quote_Volume, defaultValue = 0.0)
    let minLongAdv = args.GetResult(BuildBreadthArgs.Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(BuildBreadthArgs.Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(BuildBreadthArgs.Vol_Window_Days, defaultValue = 90)
    let maxBarPriceRatio = args.GetResult(BuildBreadthArgs.Max_Bar_Price_Ratio, defaultValue = 0.0)
    let parallelism = args.GetResult(BuildBreadthArgs.Parallelism, defaultValue = 4)
    let startDate = args.TryGetResult BuildBreadthArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate = args.TryGetResult BuildBreadthArgs.End_Date |> Option.map parseDate |> Option.defaultValue defaultEnd
    let perSymHourOut =
        args.GetResult(Per_Symbol_Hour_Out,
            defaultValue = "/mnt/d/trading-edge-bulk/crypto/binance/breadth/per_symbol_hour.parquet")
    let perHourOut =
        args.GetResult(Per_Hour_Out,
            defaultValue = "/mnt/d/trading-edge-bulk/crypto/binance/breadth/per_hour.parquet")
    let symbols =
        match args.TryGetResult BuildBreadthArgs.Symbol with
        | Some s -> parseList ',' s
        | None -> BarLoader.listSymbols barsRoot timeframe
    if symbols.Length = 0 then
        eprintfn "[build-breadth] No symbols found at %s/%s. Did you run `build-bars`?" barsRoot timeframe
        1
    else

    let bucketUs = SignedBar.bucketUsOfTimeframe timeframe
    let cfg =
        { defaultConfig maHours with
            BucketUs = bucketUs
            AllowShort = allowShort
            MinDailyQuoteVolume = minDailyVolume
            MinBarQuoteVolume = minBarQuoteVolume
            MinLongAdv = minLongAdv
            MinShortAdv = minShortAdv
            VolWindowDays = volWindowDays
            MaxBarPriceRatio = maxBarPriceRatio }

    printfn "[build-breadth] symbols=%d timeframe=%s ma-hours=%d allow-short=%b range=%s..%s parallelism=%d"
        symbols.Length timeframe maHours allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
    printfn "[build-breadth] gates: minLongAdv=$%s minShortAdv=$%s minBarQVol=$%s maxBarRatio=%g"
        (minLongAdv.ToString "N0")
        (minShortAdv.ToString "N0")
        (minBarQuoteVolume.ToString "N0")
        maxBarPriceRatio

    // Streaming pipeline: each worker thread loads its symbol's bars, runs
    // OrderflowMA, and emits each row twice — once into the per-symbol-hour
    // parquet writer (DuckDB-buffered, columnar; fits 7M rows easily), and
    // once into the per-hour aggregator (folds into per-hour totals only,
    // O(hours) not O(rows)). No managed-heap row list; peak RSS stays bounded.
    Directory.CreateDirectory(Path.GetDirectoryName perSymHourOut) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName perHourOut) |> ignore
    use perSymWriter = new Breadth.PerSymbolHourWriter(perSymHourOut)
    let aggregator = Breadth.PerHourAggregator()
    let emit (r: Breadth.BreadthRow) =
        perSymWriter.Add r
        aggregator.Push r

    let progressLock = obj()
    let mutable doneCount = 0
    let mutable failCount = 0
    let mutable totalRows = 0L
    let total = symbols.Length

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            try
                let bars = BarLoader.loadByDate barsRoot timeframe symbol startDate endDate
                if bars.Length = 0 then
                    lock progressLock (fun () ->
                        doneCount <- doneCount + 1
                        printfn "[build-breadth] %4d/%-4d %-20s no bars" doneCount total symbol)
                else
                    let n = Breadth.runSymbolStreaming symbol cfg bars emit
                    lock progressLock (fun () ->
                        doneCount <- doneCount + 1
                        totalRows <- totalRows + int64 n
                        if doneCount % 10 = 0 || doneCount = total then
                            printfn "[build-breadth] %4d/%-4d %-20s %d rows (total %s, %.1fs)"
                                doneCount total symbol n
                                (totalRows.ToString "N0") swAll.Elapsed.TotalSeconds)
            with ex ->
                lock progressLock (fun () ->
                    failCount <- failCount + 1
                    eprintfn "[build-breadth] %s FAILED: %s" symbol ex.Message))
    |> ignore
    swAll.Stop()

    printfn ""
    printfn "[build-breadth] processed %d symbols (%d failed), %s rows in %.1fs"
        doneCount failCount (totalRows.ToString "N0") swAll.Elapsed.TotalSeconds

    if perSymWriter.RowCount = 0 then
        eprintfn "[build-breadth] no rows produced; nothing to write"
        if failCount > 0 then 1 else 0
    else

    printfn "[build-breadth] writing per-(symbol, hour) parquet -> %s" perSymHourOut
    let swWrite1 = Stopwatch.StartNew()
    perSymWriter.Close()
    swWrite1.Stop()
    printfn "[build-breadth]   %s rows in %.1fs"
        (perSymWriter.RowCount.ToString "N0") swWrite1.Elapsed.TotalSeconds

    printfn "[build-breadth] finalising per-hour aggregator..."
    let swAgg = Stopwatch.StartNew()
    let perHour = aggregator.Finalize()
    swAgg.Stop()
    printfn "[build-breadth]   %d hours in %.1fs"
        perHour.Length swAgg.Elapsed.TotalSeconds

    printfn "[build-breadth] writing per-hour parquet -> %s" perHourOut
    let swWrite2 = Stopwatch.StartNew()
    let nPerHour = Breadth.writePerHourParquet perHour perHourOut
    swWrite2.Stop()
    printfn "[build-breadth]   %d rows in %.1fs"
        nPerHour swWrite2.Elapsed.TotalSeconds
    if failCount > 0 then 1 else 0

// =============================================================================
// breadth-stratify: pair trips with breadth rank at entry, decile breakdown
// =============================================================================

let cmdBreadthStratify (args: ParseResults<BreadthStratifyArgs>) : int =
    let tripsPath = args.GetResult BreadthStratifyArgs.Trips
    let perHourPath =
        args.GetResult(Per_Hour,
            defaultValue = "/mnt/d/trading-edge-bulk/crypto/binance/breadth/per_hour.parquet")
    let rankColumn =
        args.GetResult(Rank_Column, defaultValue = "composite_signed_rank_ma200")
    let valueColumn = args.TryGetResult Value_Column
    let valueScale = args.GetResult(Value_Scale, defaultValue = 1.0)
    let valueLabel = args.GetResult(Value_Label, defaultValue = "value")
    let bucketBy = args.GetResult(BreadthStratifyArgs.Bucket_By, defaultValue = "rank")
    let valueCutpointsStr = args.TryGetResult BreadthStratifyArgs.Value_Cutpoints
    let nBuckets = args.GetResult(BreadthStratifyArgs.Buckets, defaultValue = 10)
    let outputPath = args.TryGetResult BreadthStratifyArgs.Output

    if bucketBy <> "rank" && bucketBy <> "value" && bucketBy <> "cutpoints" then
        eprintfn "[breadth-stratify] --bucket-by must be 'rank', 'value', or 'cutpoints', got: %s" bucketBy
        exit 1
    if bucketBy <> "rank" && valueColumn.IsNone then
        eprintfn "[breadth-stratify] --bucket-by %s requires --value-column to be set" bucketBy
        exit 1
    if bucketBy = "cutpoints" && valueCutpointsStr.IsNone then
        eprintfn "[breadth-stratify] --bucket-by cutpoints requires --value-cutpoints"
        exit 1

    let valueCutpoints =
        match valueCutpointsStr with
        | Some s ->
            s.Split ',' |> Array.map (fun x ->
                System.Double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            |> Array.sort
        | None -> [||]

    if not (File.Exists tripsPath) then
        eprintfn "[breadth-stratify] trips file not found: %s" tripsPath
        exit 1
    if not (File.Exists perHourPath) then
        eprintfn "[breadth-stratify] per-hour parquet not found: %s" perHourPath
        exit 1

    printfn "[breadth-stratify] trips:     %s" tripsPath
    printfn "[breadth-stratify] breadth:   %s" perHourPath
    printfn "[breadth-stratify] rank-col:  %s" rankColumn
    match valueColumn with
    | Some v -> printfn "[breadth-stratify] value-col: %s (scale=%g, label=%s)" v valueScale valueLabel
    | None -> ()
    printfn "[breadth-stratify] bucket-by: %s" bucketBy
    printfn "[breadth-stratify] buckets:   %d" nBuckets
    printfn ""

    // Defence against bad column names — only allow alphanumeric + underscore.
    let validColumn (c: string) =
        System.Text.RegularExpressions.Regex.IsMatch(c, @"^[A-Za-z_][A-Za-z0-9_]*$")
    if not (validColumn rankColumn) then
        eprintfn "[breadth-stratify] invalid --rank-column: %s" rankColumn
        exit 1
    match valueColumn with
    | Some v when not (validColumn v) ->
        eprintfn "[breadth-stratify] invalid --value-column: %s" v
        exit 1
    | _ -> ()

    use conn = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:")
    conn.Open()

    // ASOF JOIN: each trip gets the breadth rank at the latest hour ≤ entry_us.
    // Filter out warmup rows where the rank is NaN. When a value column is
    // also requested, it travels alongside the rank for per-bucket reporting.
    //
    // Bucketing semantics (--bucket-by):
    //   "rank"  — FLOOR(rank * N): leak-free time-ordered buckets, faithful
    //             to what a backtest filter would see. Per-bucket raw-value
    //             ranges may overlap because rank ≠ value order (the rank
    //             is a running CDF, so the same value can land in different
    //             buckets depending on when it arrived).
    //   "value" — NTILE(N) over the value column: clean monotonic value
    //             boundaries by construction, equal trade counts. Uses the
    //             full distribution after the join is done — so this is
    //             retrospective, not leak-free.
    let valueSelect =
        match valueColumn with
        | Some v -> sprintf ", %s AS value" v
        | None -> ", CAST(NULL AS DOUBLE) AS value"
    let bucketingCte =
        match bucketBy with
        | "rank" ->
            sprintf """bucketed AS (
          SELECT side, net_pnl, rank, value,
                 LEAST(CAST(FLOOR(rank * %d) AS INTEGER), %d) AS bucket
          FROM joined
        )"""
                nBuckets (nBuckets - 1)
        | "value" ->
            sprintf """bucketed AS (
          SELECT side, net_pnl, rank, value,
                 NTILE(%d) OVER (PARTITION BY side ORDER BY value) - 1 AS bucket
          FROM joined
          WHERE value IS NOT NULL AND NOT isnan(value)
        )"""
                nBuckets
        | _ ->  // "cutpoints"
            // Build a CASE chain: bucket = number of cutpoints the value is ≥.
            // E.g. cutpoints [a,b,c] (sorted) → 4 buckets: <a, [a,b), [b,c), ≥c.
            let inv = System.Globalization.CultureInfo.InvariantCulture
            let cases =
                valueCutpoints
                |> Array.mapi (fun i cp ->
                    sprintf "WHEN value < %s THEN %d" (cp.ToString("R", inv)) i)
                |> String.concat " "
            let elseBucket = valueCutpoints.Length
            sprintf """bucketed AS (
          SELECT side, net_pnl, rank, value,
                 CASE %s ELSE %d END AS bucket
          FROM joined
          WHERE value IS NOT NULL AND NOT isnan(value)
        )"""
                cases elseBucket
    let q =
        sprintf """
        WITH trips AS (
          SELECT side, entry_us, net_pnl
          FROM read_csv_auto('%s')
        ),
        breadth AS (
          SELECT hour_us, %s AS rank %s
          FROM read_parquet('%s')
          WHERE %s IS NOT NULL
            AND NOT isnan(%s)
        ),
        joined AS (
          SELECT t.side, t.entry_us, t.net_pnl, b.rank, b.value
          FROM trips t
          ASOF JOIN breadth b ON t.entry_us >= b.hour_us
          WHERE b.rank IS NOT NULL
        ),
        %s
        SELECT
          side,
          bucket,
          COUNT(*) AS trades,
          SUM(CASE WHEN net_pnl > 0 THEN 1 ELSE 0 END) AS wins,
          AVG(CASE WHEN net_pnl > 0 THEN 1.0 ELSE 0.0 END) AS win_rate,
          SUM(CASE WHEN net_pnl > 0 THEN net_pnl ELSE 0 END) AS gross_wins,
          SUM(CASE WHEN net_pnl < 0 THEN -net_pnl ELSE 0 END) AS gross_losses,
          SUM(net_pnl) AS sum_pnl,
          AVG(net_pnl) AS avg_pnl,
          MIN(rank) AS rank_lo,
          MAX(rank) AS rank_hi,
          MIN(value) AS value_lo,
          MAX(value) AS value_hi,
          AVG(value) AS value_avg
        FROM bucketed
        GROUP BY side, bucket
        ORDER BY side, bucket
        """
            (tripsPath.Replace("'", "''"))
            rankColumn valueSelect
            (perHourPath.Replace("'", "''"))
            rankColumn rankColumn
            bucketingCte

    use cmd = conn.CreateCommand()
    cmd.CommandText <- q
    use reader = cmd.ExecuteReader()

    // (side, bucket, trades, wins, win_rate, grossW, grossL, sumPnl, avgPnl,
    //  rankLo, rankHi, valueLo, valueHi, valueAvg)
    let rows = ResizeArray<string * int * int * int * float * float * float * float * float * float * float * float * float * float>()
    while reader.Read() do
        let side = reader.GetString 0
        let bucket = reader.GetInt32 1
        let trades = reader.GetInt32 2
        let wins = reader.GetInt64 3 |> int
        let winRate = reader.GetDouble 4
        let grossW = reader.GetDouble 5
        let grossL = reader.GetDouble 6
        let sumPnl = reader.GetDouble 7
        let avgPnl = reader.GetDouble 8
        let rankLo = reader.GetDouble 9
        let rankHi = reader.GetDouble 10
        let valueLo = if reader.IsDBNull 11 then nan else reader.GetDouble 11
        let valueHi = if reader.IsDBNull 12 then nan else reader.GetDouble 12
        let valueAvg = if reader.IsDBNull 13 then nan else reader.GetDouble 13
        rows.Add(side, bucket, trades, wins, winRate, grossW, grossL, sumPnl, avgPnl,
                 rankLo, rankHi, valueLo, valueHi, valueAvg)

    let hasValue = valueColumn.IsSome

    let printSection (side: string) =
        printfn ""
        if hasValue then
            printfn "--- %s side: rank-decile breakdown (%s scaled by %g) ---"
                side valueLabel valueScale
            printfn "  %-7s %-10s %-10s %-12s %-12s %-7s %-7s %-9s %11s %11s %11s %11s %11s"
                "bucket" "rank_lo" "rank_hi"
                (sprintf "%s_lo" valueLabel) (sprintf "%s_hi" valueLabel)
                "trades" "wins" "win_rate"
                "grossW$" "grossL$" "PF" "sumPnl$" "avgPnl$"
        else
            printfn "--- %s side: rank-decile breakdown ---" side
            printfn "  %-7s %-12s %-12s %-7s %-7s %-9s %11s %11s %11s %11s %11s"
                "bucket" "rank_lo" "rank_hi"
                "trades" "wins" "win_rate"
                "grossW$" "grossL$" "PF" "sumPnl$" "avgPnl$"
        for s, b, n, w, wr, gw, gl, sp, ap, rl, rh, vl, vh, _ in rows do
            if s = side then
                let pf = if gl > 0.0 then gw / gl else nan
                if hasValue then
                    printfn "  %-7d %-10.4f %-10.4f %-12.3f %-12.3f %-7d %-7d %-9.3f %11.0f %11.0f %11.3f %11.0f %11.2f"
                        b rl rh (vl * valueScale) (vh * valueScale) n w wr gw gl pf sp ap
                else
                    printfn "  %-7d %-12.4f %-12.4f %-7d %-7d %-9.3f %11.0f %11.0f %11.3f %11.0f %11.2f"
                        b rl rh n w wr gw gl pf sp ap

    printSection "long"
    printSection "short"

    match outputPath with
    | Some path ->
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        use sw = new StreamWriter(path)
        if hasValue then
            sw.WriteLine(
                sprintf "side,bucket,rank_lo,rank_hi,%s_lo,%s_hi,%s_avg,trades,wins,win_rate,gross_wins,gross_losses,profit_factor,sum_pnl,avg_pnl"
                    valueLabel valueLabel valueLabel)
        else
            sw.WriteLine "side,bucket,rank_lo,rank_hi,trades,wins,win_rate,gross_wins,gross_losses,profit_factor,sum_pnl,avg_pnl"
        for s, b, n, w, wr, gw, gl, sp, ap, rl, rh, vl, vh, va in rows do
            let pf = if gl > 0.0 then gw / gl else System.Double.NaN
            if hasValue then
                sw.WriteLine(
                    sprintf "%s,%d,%g,%g,%g,%g,%g,%d,%d,%g,%g,%g,%g,%g,%g"
                        s b rl rh (vl * valueScale) (vh * valueScale) (va * valueScale)
                        n w wr gw gl pf sp ap)
            else
                sw.WriteLine(
                    sprintf "%s,%d,%g,%g,%d,%d,%g,%g,%g,%g,%g,%g"
                        s b rl rh n w wr gw gl pf sp ap)
        printfn ""
        printfn "[breadth-stratify] wrote %s" path
    | None -> ()
    0

// =============================================================================
// funding-stratify: pair trips with funding rate at entry, decile breakdown
// =============================================================================
//
// Symmetric to breadth-stratify. For each trade with (symbol, entry_us), we
// look up the most recent funding event for that symbol whose calc_time_us
// is ≤ entry_us, via DuckDB's per-symbol ASOF JOIN. Bucketing is by funding
// rate value (NOT by rank within the symbol) — the absolute scale of funding
// is meaningful: +1% per 8h is "longs paying shorts heavily, top sign,"
// −0.1% is "structural alt-perp baseline." A uniform-ranked-percentile bucket
// would lose that interpretability.
//
// Bucket boundaries: NTILE over the joined data so each bucket has roughly
// equal trade count. We report rank_lo/rank_hi as the actual funding-rate
// range in each bucket so the result is interpretable.

let cmdFundingStratify (args: ParseResults<FundingStratifyArgs>) : int =
    let tripsPath = args.GetResult FundingStratifyArgs.Trips
    let fundingRoot =
        args.GetResult(FundingStratifyArgs.Funding_Root, defaultValue = defaultFundingRoot)
    let bucketBy = args.GetResult(FundingStratifyArgs.Bucket_By, defaultValue = "value")
    let valueCutpointsStr = args.TryGetResult FundingStratifyArgs.Value_Cutpoints
    let nBuckets = args.GetResult(FundingStratifyArgs.Buckets, defaultValue = 10)
    let outputPath = args.TryGetResult FundingStratifyArgs.Output

    if bucketBy <> "value" && bucketBy <> "cutpoints" then
        eprintfn "[funding-stratify] --bucket-by must be 'value' or 'cutpoints', got: %s" bucketBy
        exit 1
    if bucketBy = "cutpoints" && valueCutpointsStr.IsNone then
        eprintfn "[funding-stratify] --bucket-by cutpoints requires --value-cutpoints"
        exit 1

    let valueCutpoints =
        match valueCutpointsStr with
        | Some s ->
            s.Split ',' |> Array.map (fun x ->
                System.Double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            |> Array.sort
        | None -> [||]

    if not (File.Exists tripsPath) then
        eprintfn "[funding-stratify] trips file not found: %s" tripsPath
        exit 1
    if not (Directory.Exists fundingRoot) then
        eprintfn "[funding-stratify] funding root not found: %s" fundingRoot
        exit 1

    printfn "[funding-stratify] trips:     %s" tripsPath
    printfn "[funding-stratify] funding:   %s" fundingRoot
    printfn "[funding-stratify] bucket-by: %s" bucketBy
    printfn "[funding-stratify] buckets:   %d" nBuckets
    printfn ""

    use conn = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:")
    conn.Open()

    let bucketingCte =
        match bucketBy with
        | "value" ->
            sprintf """bucketed AS (
          SELECT side, net_pnl, fr,
                 NTILE(%d) OVER (PARTITION BY side ORDER BY fr) - 1 AS bucket
          FROM joined
        )"""
                nBuckets
        | _ ->  // "cutpoints"
            let inv = System.Globalization.CultureInfo.InvariantCulture
            let cases =
                valueCutpoints
                |> Array.mapi (fun i cp ->
                    sprintf "WHEN fr < %s THEN %d" (cp.ToString("R", inv)) i)
                |> String.concat " "
            let elseBucket = valueCutpoints.Length
            sprintf """bucketed AS (
          SELECT side, net_pnl, fr,
                 CASE %s ELSE %d END AS bucket
          FROM joined
        )"""
                cases elseBucket

    let fundingGlob = Path.Combine(fundingRoot, "*.parquet")
    let q =
        sprintf """
        WITH trips AS (
          SELECT symbol, side, entry_us, net_pnl
          FROM read_csv_auto('%s')
        ),
        funding_all AS (
          SELECT
            regexp_extract(filename, '([^/]+)\.parquet$', 1) AS symbol,
            calc_time_us,
            funding_rate
          FROM read_parquet('%s', filename = true)
        ),
        joined AS (
          SELECT t.symbol, t.side, t.entry_us, t.net_pnl, f.funding_rate AS fr
          FROM trips t
          ASOF JOIN funding_all f
            ON t.symbol = f.symbol AND t.entry_us >= f.calc_time_us
          WHERE f.funding_rate IS NOT NULL
        ),
        %s
        SELECT
          side,
          bucket,
          COUNT(*) AS trades,
          SUM(CASE WHEN net_pnl > 0 THEN 1 ELSE 0 END) AS wins,
          AVG(CASE WHEN net_pnl > 0 THEN 1.0 ELSE 0.0 END) AS win_rate,
          SUM(CASE WHEN net_pnl > 0 THEN net_pnl ELSE 0 END) AS gross_wins,
          SUM(CASE WHEN net_pnl < 0 THEN -net_pnl ELSE 0 END) AS gross_losses,
          SUM(net_pnl) AS sum_pnl,
          AVG(net_pnl) AS avg_pnl,
          MIN(fr) AS fr_lo,
          MAX(fr) AS fr_hi
        FROM bucketed
        GROUP BY side, bucket
        ORDER BY side, bucket
        """
            (tripsPath.Replace("'", "''"))
            (fundingGlob.Replace("'", "''"))
            bucketingCte

    use cmd = conn.CreateCommand()
    cmd.CommandText <- q
    use reader = cmd.ExecuteReader()

    let rows = ResizeArray<string * int * int * int * float * float * float * float * float * float * float>()
    while reader.Read() do
        let side = reader.GetString 0
        let bucket = reader.GetInt32 1
        let trades = reader.GetInt32 2
        let wins = reader.GetInt64 3 |> int
        let winRate = reader.GetDouble 4
        let grossW = reader.GetDouble 5
        let grossL = reader.GetDouble 6
        let sumPnl = reader.GetDouble 7
        let avgPnl = reader.GetDouble 8
        let frLo = reader.GetDouble 9
        let frHi = reader.GetDouble 10
        rows.Add(side, bucket, trades, wins, winRate, grossW, grossL, sumPnl, avgPnl, frLo, frHi)

    // Funding rates are reported in bps per interval (1 bps = 0.0001 decimal,
    // i.e. fr * 10_000). Per-interval rates of 0.5–10 bps are typical; the
    // Binance default-baseline floor sits at 0.5 bps. With 8h intervals
    // 1 bps/interval ≈ 1095 bps/year ≈ 11% annualised funding cost.
    let toBps (x: float) = x * 10_000.0

    let printSection (side: string) =
        printfn ""
        printfn "--- %s side: funding-rate decile breakdown (bps per funding interval) ---" side
        printfn "  %-7s %-12s %-12s %-7s %-7s %-9s %11s %11s %11s %11s %11s"
            "bucket" "fr_lo_bps" "fr_hi_bps"
            "trades" "wins" "win_rate"
            "grossW$" "grossL$" "PF" "sumPnl$" "avgPnl$"
        for s, b, n, w, wr, gw, gl, sp, ap, fl, fh in rows do
            if s = side then
                let pf = if gl > 0.0 then gw / gl else nan
                printfn "  %-7d %-12.3f %-12.3f %-7d %-7d %-9.3f %11.0f %11.0f %11.3f %11.0f %11.2f"
                    b (toBps fl) (toBps fh) n w wr gw gl pf sp ap

    printSection "long"
    printSection "short"

    match outputPath with
    | Some path ->
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        use sw = new StreamWriter(path)
        sw.WriteLine "side,bucket,fr_lo_bps,fr_hi_bps,trades,wins,win_rate,gross_wins,gross_losses,profit_factor,sum_pnl,avg_pnl"
        for s, b, n, w, wr, gw, gl, sp, ap, fl, fh in rows do
            let pf = if gl > 0.0 then gw / gl else System.Double.NaN
            sw.WriteLine(
                sprintf "%s,%d,%g,%g,%d,%d,%g,%g,%g,%g,%g,%g"
                    s b (toBps fl) (toBps fh) n w wr gw gl pf sp ap)
        printfn ""
        printfn "[funding-stratify] wrote %s" path
    | None -> ()
    0

// =============================================================================
// build-funding-breadth: per-hour universe-wide funding aggregates
// =============================================================================
//
// Funding events fire at 4–8 h cadence per symbol. To get a per-hour panel
// we forward-fill: at any 1h bucket, each symbol carries the most recent
// funding rate whose calc_time_us ≤ bucket. We restrict to (symbol, hour)
// pairs that have a bar in per_symbol_hour.parquet — that's the natural
// "is this symbol active right now" filter (built by `build-breadth`).
//
// Output columns:
//   hour_us BIGINT
//   n_active_symbols INTEGER
//   avg_funding DOUBLE         -- mean across active symbols
//   median_funding DOUBLE      -- median (robust to outliers)
//   pct_positive DOUBLE        -- fraction with funding > 0
//   pct_extreme_positive DOUBLE
//   pct_extreme_negative DOUBLE
//   median_funding_rank DOUBLE -- t-digest CDF over time-ordered medians

let cmdBuildFundingBreadth (args: ParseResults<BuildFundingBreadthArgs>) : int =
    let fundingRoot =
        args.GetResult(BuildFundingBreadthArgs.Funding_Root,
            defaultValue = defaultFundingRoot)
    let perSymHour =
        args.GetResult(Per_Symbol_Hour,
            defaultValue = "/mnt/d/trading-edge-bulk/crypto/binance/breadth/per_symbol_hour.parquet")
    let out =
        args.GetResult(BuildFundingBreadthArgs.Output,
            defaultValue = "/mnt/d/trading-edge-bulk/crypto/binance/breadth/funding_per_hour.parquet")
    let extPos = args.GetResult(Extreme_Positive, defaultValue = 0.0001)
    let extNeg = args.GetResult(Extreme_Negative, defaultValue = -0.0001)

    if not (Directory.Exists fundingRoot) then
        eprintfn "[build-funding-breadth] funding root not found: %s" fundingRoot
        exit 1
    if not (File.Exists perSymHour) then
        eprintfn "[build-funding-breadth] per-symbol-hour parquet not found: %s" perSymHour
        eprintfn "  Run `build-breadth` first to produce it."
        exit 1

    printfn "[build-funding-breadth] funding-root: %s" fundingRoot
    printfn "[build-funding-breadth] per-symbol-hour: %s" perSymHour
    printfn "[build-funding-breadth] output: %s" out
    printfn "[build-funding-breadth] extreme-positive: %g, extreme-negative: %g" extPos extNeg
    printfn ""

    Directory.CreateDirectory(Path.GetDirectoryName out) |> ignore
    let tmp = out + ".tmp"
    if File.Exists tmp then File.Delete tmp

    use conn = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:")
    conn.Open()

    let fundingGlob = Path.Combine(fundingRoot, "*.parquet")
    let sw = Stopwatch.StartNew()

    // Pipeline:
    //  1. activity = (symbol, hour_us) pairs with a bar (from per_symbol_hour).
    //  2. funding_all = (symbol, calc_time_us, funding_rate) over the full universe.
    //  3. forward_filled = ASOF LEFT JOIN activity to funding_all on
    //     (symbol, hour_us >= calc_time_us). Each (symbol, hour) gets the most
    //     recent funding rate; symbols with no funding event yet get NULL.
    //  4. per_hour = aggregate forward_filled by hour_us.
    //  5. Walk the rows in time order, feed median_funding into a t-digest, query
    //     CDF after add. (Doable in pure SQL? Probably not cleanly — use a SQL
    //     window-pass + post-process in F#.)
    let qAggregate =
        sprintf """
        WITH activity AS (
          SELECT symbol, start_us AS hour_us
          FROM read_parquet('%s')
        ),
        funding_all AS (
          SELECT
            regexp_extract(filename, '([^/]+)\.parquet$', 1) AS symbol,
            calc_time_us,
            funding_rate
          FROM read_parquet('%s', filename = true)
        ),
        ffilled AS (
          SELECT a.hour_us, a.symbol, f.funding_rate
          FROM activity a
          ASOF LEFT JOIN funding_all f
            ON a.symbol = f.symbol AND a.hour_us >= f.calc_time_us
        )
        SELECT
          hour_us,
          COUNT(*) FILTER (WHERE funding_rate IS NOT NULL) AS n_active_symbols,
          AVG(funding_rate) FILTER (WHERE funding_rate IS NOT NULL) AS avg_funding,
          MEDIAN(funding_rate) FILTER (WHERE funding_rate IS NOT NULL) AS median_funding,
          AVG(CASE WHEN funding_rate > 0 THEN 1.0 ELSE 0.0 END)
            FILTER (WHERE funding_rate IS NOT NULL) AS pct_positive,
          AVG(CASE WHEN funding_rate > %g THEN 1.0 ELSE 0.0 END)
            FILTER (WHERE funding_rate IS NOT NULL) AS pct_extreme_positive,
          AVG(CASE WHEN funding_rate < %g THEN 1.0 ELSE 0.0 END)
            FILTER (WHERE funding_rate IS NOT NULL) AS pct_extreme_negative
        FROM ffilled
        GROUP BY hour_us
        ORDER BY hour_us
        """
            (perSymHour.Replace("'", "''"))
            (fundingGlob.Replace("'", "''"))
            extPos extNeg

    printfn "[build-funding-breadth] aggregating per-hour..."
    use cmd = conn.CreateCommand()
    cmd.CommandText <- qAggregate
    use reader = cmd.ExecuteReader()

    // Pull rows in time order, feed median into t-digest, append rank column.
    let rows = ResizeArray<int64 * int * float * float * float * float * float * float>()
    let digest = TDigest.MergingDigest(200.0)
    while reader.Read() do
        let hourUs = reader.GetInt64 0
        let nActive = reader.GetInt32 1
        let avgF =
            if reader.IsDBNull 2 then nan else reader.GetDouble 2
        let medF =
            if reader.IsDBNull 3 then nan else reader.GetDouble 3
        let pctPos =
            if reader.IsDBNull 4 then nan else reader.GetDouble 4
        let pctEPos =
            if reader.IsDBNull 5 then nan else reader.GetDouble 5
        let pctENeg =
            if reader.IsDBNull 6 then nan else reader.GetDouble 6
        let rank =
            if System.Double.IsNaN medF then nan
            else
                digest.Add medF
                digest.Cdf medF
        rows.Add(hourUs, nActive, avgF, medF, pctPos, pctEPos, pctENeg, rank)
    reader.Close()
    sw.Stop()
    printfn "[build-funding-breadth]   %d hours in %.1fs" rows.Count sw.Elapsed.TotalSeconds

    // Write the parquet via a fresh appender table.
    let swWrite = Stopwatch.StartNew()
    use cmdSchema = conn.CreateCommand()
    cmdSchema.CommandText <- "
        CREATE TABLE funding_per_hour (
            hour_us BIGINT,
            n_active_symbols INTEGER,
            avg_funding DOUBLE,
            median_funding DOUBLE,
            pct_positive DOUBLE,
            pct_extreme_positive DOUBLE,
            pct_extreme_negative DOUBLE,
            median_funding_rank DOUBLE
        )"
    cmdSchema.ExecuteNonQuery() |> ignore
    use appender = conn.CreateAppender("funding_per_hour")
    for hourUs, nActive, avgF, medF, pctPos, pctEPos, pctENeg, rank in rows do
        let row = appender.CreateRow()
        row.AppendValue(Nullable hourUs) |> ignore
        row.AppendValue(Nullable nActive) |> ignore
        row.AppendValue(Nullable avgF) |> ignore
        row.AppendValue(Nullable medF) |> ignore
        row.AppendValue(Nullable pctPos) |> ignore
        row.AppendValue(Nullable pctEPos) |> ignore
        row.AppendValue(Nullable pctENeg) |> ignore
        row.AppendValue(Nullable rank) |> ignore
        row.EndRow()
    appender.Close()

    let normalized = tmp.Replace('\\', '/').Replace("'", "''")
    use copyCmd = conn.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY funding_per_hour TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            normalized
    copyCmd.ExecuteNonQuery() |> ignore
    if File.Exists out then File.Delete out
    File.Move(tmp, out)
    swWrite.Stop()
    printfn "[build-funding-breadth] wrote %d rows -> %s in %.1fs"
        rows.Count out swWrite.Elapsed.TotalSeconds
    0

// =============================================================================
// Entry point
// =============================================================================

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "TradingEdge.CryptoBacktest")
    try
        let res = parser.Parse(argv, raiseOnUsage = true)
        match res.GetSubCommand() with
        | Run a -> cmdRun a
        | Sweep a -> cmdSweep a
        | Cumsum_Sweep a -> cmdCumsumSweep a
        | Cumsum_Z_Sweep a -> cmdCumsumZSweep a
        | Extreme_Rvol_Sweep a -> cmdExtremeRvolSweep a
        | Extreme_Rvol_Long_Sweep a -> cmdExtremeRvolLongSweep a
        | Vwma_Sweep a -> cmdVwmaSweep a
        | Breakout_Sweep a -> cmdBreakoutSweep a
        | Build_Breadth a -> cmdBuildBreadth a
        | Breadth_Stratify a -> cmdBreadthStratify a
        | Funding_Stratify a -> cmdFundingStratify a
        | Build_Funding_Breadth a -> cmdBuildFundingBreadth a
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        2
