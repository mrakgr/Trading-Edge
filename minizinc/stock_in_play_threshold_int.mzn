% Integer variant of stock_in_play_threshold_direct.mzn for CP-SAT.
%
% CP-SAT requires integer variables. We use two separate scale factors:
%   * var_scale      — precision of the per-row data (vr_int, tr_int).
%   * threshold_scale — granularity of the Tv, Ta search grid.
% Keeping them separate lets us search a coarse threshold grid (fast) while
% retaining a fine representation of the observed RVOLs (accurate). The
% constraint asserting var_scale % threshold_scale == 0 makes the scale-up
% (var_scale / threshold_scale) an exact integer, so the firing comparison
% at line 35 is a clean int >= int without rounding.

% ----- Per-row data (caller supplies as floats in the .dzn) -----
int: n;
array[1..n] of float: vr;                            % cum_volume / avg_raw_4w (split-safe)
array[1..n] of float: tr;                            % cum_transactions / avg_transactions_4w
array[1..n] of float: session_volume_rvol_final;     % end-of-day session RVOL (ground truth)

% ----- Scaling knobs -----
% var_scale controls the int range of vr_int/tr_int. Power-of-2 scales avoid
% floating-point drift in the generator's float-to-int conversion
% (x * var_scale is exactly representable when x has finite binary fraction).
% Rows with observed RVOL above target_rvol are clamped because the search
% is bounded by target_rvol anyway — any row passing Tv <= target_rvol with
% vr = target_rvol still fires exactly the same as rows with vr > target_rvol,
% so clamping is lossless.
int: var_scale;
float: target_rvol;
% 2^31 - 1 = int32 max. CP-SAT uses int64 internally, but keeping the
% scaled values inside int32 avoids any surprise with overflow in
% generator-side computations (e.g. float2int rounding).
constraint assert(var_scale * target_rvol <= 2147483647.0,
    "The var scale must not be too high for the target rvol.");

array[1..n] of int: vr_int = [floor(min(x, target_rvol) * var_scale) | x in vr];
array[1..n] of int: tr_int = [floor(min(x, target_rvol) * var_scale) | x in tr];
array[1..n] of bool: hit   = [x >= target_rvol | x in session_volume_rvol_final];

% ----- Solver knobs (caller supplies via -D on the CLI) -----
int: precision_pct;
% threshold_scale can be set coarser than var_scale (e.g. threshold_scale=128)
% to shrink the search grid, trading precision for solve time. Must divide
% var_scale exactly (see assert below). Typical value: equal to var_scale.
int: threshold_scale;
constraint assert(var_scale mod threshold_scale = 0,
    "`var_scale` must be divisible by `threshold_scale`");
int: Tv_int_max = ceil(target_rvol * int2float(threshold_scale));
int: Ta_int_max = ceil(target_rvol * int2float(threshold_scale));

% ----- Decision variables (in threshold_scale units) -----
var 0..Tv_int_max: Tv_int;
var 0..Ta_int_max: Ta_int;

% To compare a row value (var_scale units) against a threshold (threshold_scale
% units), we scale the threshold up by (var_scale / threshold_scale). This
% factor is an exact integer thanks to the divisibility assert above.
array [1..n] of var bool: fired =
    [vr_int[i] >= (var_scale div threshold_scale) * Tv_int
     /\ tr_int[i] >= (var_scale div threshold_scale) * Ta_int | i in 1..n];

var int: n_fired = sum(fired);
var int: n_hit   = sum(i in 1..n)(hit[i] /\ fired[i]);

constraint n_fired >= 1;
constraint 100 * n_hit >= precision_pct * n_fired;

% Lexicographic objective: maximize n_fired, then (among tied solutions)
% maximize Ta_int, then Tv_int. Without this the solver picks arbitrarily
% among (Tv, Ta) pairs that give the same n_fired, which makes Ta jump
% around from run to run and across neighboring bucket/target cells.
%
% Implementation: weighted sum with W1 >> W2 >> 1. Because Tv_int and
% Ta_int are in [0, Tv_int_max] and [0, Ta_int_max], choosing
%   W1 = (Ta_int_max + 1) * (Tv_int_max + 1)
%   W2 = (Tv_int_max + 1)
% guarantees one more fired trade always beats any possible tie-break gain
% and one extra Ta_int unit always beats any Tv_int gain. With threshold_scale
% = 1024 and target_rvol ≤ 5, W1 ~= 25M; W1*n_fired_max (~1000) fits in int64.
int: W_fired = (Ta_int_max + 1) * (Tv_int_max + 1);
int: W_ta    = Tv_int_max + 1;
% Overflow guard. CP-SAT uses int64 internally (int64 max = 9,223,372,036,854,775,807
% ~= 9.2e18). Max possible objective = W_fired * n + W_ta * Ta_int_max + Tv_int_max.
% Since the tie-break terms are bounded by W_fired - 1 by construction, the
% entire objective is <= W_fired * (n + 1). We require that to stay under 1e15,
% keeping a ~9000x safety margin. For threshold_scale=1024, target_rvol<=10,
% this allows n up to ~9e6 — far beyond any realistic bucket.
constraint assert(W_fired * (n + 1) <= 1000000000000000,
    "Objective weights may overflow int64. Reduce threshold_scale or target_rvol.");
var int: objective = W_fired * n_fired + W_ta * Ta_int + Tv_int;

solve maximize objective;

output [
    "{\n",
    "  \"Tv\": ",      show(int2float(Tv_int) / int2float(threshold_scale)), ",\n",
    "  \"Ta\": ",      show(int2float(Ta_int) / int2float(threshold_scale)), ",\n",
    "  \"n_fired\": ", show(n_fired), ",\n",
    "  \"n_hit\": ",   show(n_hit),   ",\n",
    "  \"precision_pct_requested\": ", show(precision_pct), ",\n",
    "  \"threshold_scale\": ",         show(threshold_scale), ",\n",
    "  \"n_rows\": ",  show(n), "\n",
    "}\n"
];
