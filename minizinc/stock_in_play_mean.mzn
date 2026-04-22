% Mean-maximizing variant of the (Tv, Ta) threshold optimizer.
%
% Instead of a precision floor ("≥ P% of fires must hit target_rvol"), this
% model maximizes the *sum* of rvol_final over fired rows, subject to a
% *ceiling* on n_fired. The ceiling keeps the solver from trivially picking
% all rows; at the ceiling, maximizing sum is equivalent to maximizing mean
% (since mean = sum / n_fired and n_fired is bounded above).
%
% No precision parameter. The notion of "hit" goes away entirely — we're
% now asking: "which (Tv, Ta) rectangle, constrained to at most n_ceil
% fires, captures the highest total end-of-day session RVOL?"

int: n;
array[1..n] of tuple(float, float, float): data;

int: int_scale;
constraint assert(int_scale > 0, "The int scale should be positive.");

function int: scale(float: x) = floor(x * int_scale);
function float: data_vr(int: i) = data[i].1;                  % cum_volume / avg_raw_4w (split-safe)
function float: data_tr(int: i) = data[i].2;                  % cum_transactions / avg_transactions_4w
function float: data_rvol_final(int: i) = data[i].3;          % end-of-day session RVOL

float: target_rvol;
% No clamp on vr/tr here (unlike the precision-style model). For the
% mean-maximizing objective we want Tv/Ta to be able to distinguish the
% genuinely-extreme rows from merely-above-threshold ones, so the clamp
% would throw away information. The [0, Tv_max]/[0, Ta_max] decision
% domains still bound the decision variables; the data just won't be
% limited to that domain.
array[1..n] of int: vr = [scale(data_vr(i)) | i in 1..n];
array[1..n] of int: tr = [scale(data_tr(i)) | i in 1..n];

% Quantize rvol_final the same way so the objective is integer.
array[1..n] of int: rvol_scaled = [scale(data_rvol_final(i)) | i in 1..n];

% ----- Solver knobs -----
% Ceiling on n_fired, specified in basis points (1 bp = 0.01%). So
% n_ceil_bp = 10 means "n_ceil = 0.1% of n" = n / 1000.
int: n_ceil_bp;
constraint assert(n_ceil_bp > 0, "n_ceil_bp must be positive.");
int: n_ceil = (n * n_ceil_bp) div 10000;
constraint assert(n_ceil >= 1, "n_ceil_bp too small: resulting ceiling is 0.");

% Decision-variable upper bound must cover the actual data range. If it
% stopped at target_rvol*int_scale the solver couldn't exclude rows with
% vr (or tr) > target_rvol, and when there are more such forced-fires
% than n_ceil the problem becomes UNSAT. Bound each decision variable
% by its own column's max (plus one so "exclude the max row" is reachable).
int: Tv_max = max([scale(data_vr(i)) | i in 1..n]) + 1;
int: Ta_max = max([scale(data_tr(i)) | i in 1..n]) + 1;

% ----- Decision variables (in int_scale units) -----
var 0..Tv_max: Tv;
var 0..Ta_max: Ta;

array [1..n] of var bool: fired = [vr[i] >= Tv /\ tr[i] >= Ta | i in 1..n];

var int: n_fired = sum(fired);
var int: sum_rvol = sum(i in 1..n)(rvol_scaled[i] * fired[i]);

% n_hit reported for CSV schema compatibility with the precision-style model,
% computed post-hoc against target_rvol.
var int: n_hit = sum(i in 1..n)(data_rvol_final(i) >= target_rvol /\ fired[i]);

constraint n_fired >= 1;
constraint n_fired <= n_ceil;

% Lexicographic objective: maximize sum_rvol first; among ties, maximize Ta,
% then Tv (smoother in-sample series than the permissive-corner variant, per
% the experiment documented in docs/orb_session_findings.md section 24a).
%
% Weights: one unit of sum_rvol must outweigh any possible tie-break
% contribution. Tie-break ceiling = W_ta * Ta_max + Tv_max <= (Tv_max+1)*(Ta_max+1).
% So W_sum = (Tv_max+1)*(Ta_max+1) suffices — sum_rvol strictly dominates.
%
% int64 fit: sum_rvol upper bound is n_ceil * max(rvol_scaled). With
% int_scale=256, target_rvol=8, n_ceil=0.1% of 400k = 400, and rvol_final
% plausibly up to 100x (=> rvol_scaled up to 25600), sum_rvol <= ~10M.
% W_sum at int_scale=256, target=8 is (2049)(2049) ≈ 4.2M. Objective top
% ≈ 4.2M * 10M = 4.2e13, well under int64 max (9.2e18).
int: W_sum = (Ta_max + 1) * (Tv_max + 1);
int: W_ta  = Tv_max + 1;
var int: objective = W_sum * sum_rvol + W_ta * Ta + Tv;

solve maximize objective;

output [
    "{\n",
    "  \"Tv\": ",      show(int2float(Tv) / int2float(int_scale)), ",\n",
    "  \"Ta\": ",      show(int2float(Ta) / int2float(int_scale)), ",\n",
    "  \"n_fired\": ", show(n_fired), ",\n",
    "  \"n_hit\": ",   show(n_hit),   ",\n",
    "  \"sum_rvol\": ", show(sum_rvol), ",\n",
    "  \"n_ceil\": ",   show(n_ceil), ",\n",
    "  \"n_ceil_bp\": ", show(n_ceil_bp), ",\n",
    "  \"int_scale\": ",show(int_scale), ",\n",
    "  \"n_rows\": ",  show(n), "\n",
    "}\n"
];
