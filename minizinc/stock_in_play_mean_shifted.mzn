% Shifted-mean-maximizing variant of the (Tv, Ta) threshold optimizer.
%
% Sibling of minizinc/stock_in_play_mean.mzn. Same decision variables,
% same ceiling constraint (n_fired <= n_ceil). The only difference is the
% primary objective:
%
%   sum_excess = sum(i fired)(rvol_final_i - target_rvol)
%
% which is the *signed* sum of end-of-day session RVOL minus a reference
% level. Rows with rvol_final >= target_rvol contribute positively; rows
% with rvol_final <  target_rvol contribute negatively. The optimizer is
% now directly penalised for sweeping in below-target days, not just for
% not sweeping in above-target days.
%
% Functionally this is the mean-minus-target (`sum(rvol-target)/n_fired`)
% objective stripped of its divisor: with n_fired capped at n_ceil,
% maximising sum_excess is the same as maximising the shifted mean at
% the ceiling (the subset that packs the most positive excess and the
% least negative excess into n_ceil firings wins).

int: n;
array[1..n] of tuple(float, float, float): data;

int: int_scale;
constraint assert(int_scale > 0, "The int scale should be positive.");

function int: scale(float: x) = floor(x * int_scale);
function float: data_vr(int: i) = data[i].1;
function float: data_tr(int: i) = data[i].2;
function float: data_rvol_final(int: i) = data[i].3;

float: target_rvol;
% No clamp on vr/tr (same as stock_in_play_mean.mzn).
array[1..n] of int: vr = [scale(data_vr(i)) | i in 1..n];
array[1..n] of int: tr = [scale(data_tr(i)) | i in 1..n];

% Signed scaled excess: scale(rvol_final) - scale(target_rvol).
% Quantising the subtraction this way keeps the grid consistent across
% rows and across the Tv/Ta domains.
int: target_scaled = scale(target_rvol);
array[1..n] of int: excess_scaled = [scale(data_rvol_final(i)) - target_scaled | i in 1..n];

% ----- Solver knobs -----
int: n_ceil_bp;
constraint assert(n_ceil_bp > 0, "n_ceil_bp must be positive.");
int: n_ceil = (n * n_ceil_bp) div 10000;
constraint assert(n_ceil >= 1, "n_ceil_bp too small: resulting ceiling is 0.");

% Decision-variable bounds must cover the actual data range, same as
% stock_in_play_mean.mzn. With a clamped domain at target_rvol the solver
% can't exclude rows whose vr (or tr) exceed target_rvol.
int: Tv_max = max([scale(data_vr(i)) | i in 1..n]) + 1;
int: Ta_max = max([scale(data_tr(i)) | i in 1..n]) + 1;

var 0..Tv_max: Tv;
var 0..Ta_max: Ta;

array [1..n] of var bool: fired = [vr[i] >= Tv /\ tr[i] >= Ta | i in 1..n];

var int: n_fired = sum(fired);
var int: sum_excess = sum(i in 1..n)(excess_scaled[i] * fired[i]);

% n_hit reported post-hoc against the nominal target for CSV compatibility.
var int: n_hit = sum(i in 1..n)(data_rvol_final(i) >= target_rvol /\ fired[i]);

constraint n_fired >= 1;
constraint n_fired <= n_ceil;

% Lexicographic objective: maximize sum_excess first; among ties, maximize
% Ta, then Tv (smoothness, same as the precision model's current tie-break).
%
% Weight derivation: the tie-break block W_ta*Ta + Tv is bounded above by
% (Tv_max+1)*(Ta_max+1). Offsetting sum_excess into a non-negative domain
% (by adding n_ceil * |min_excess_scaled|) isn't necessary — the scaled
% objective `W_sum*sum_excess + ...` remains strictly dominated by a single
% unit of sum_excess provided W_sum > tie-break block ceiling. So the same
% W_sum choice as the unshifted model suffices.
%
% int64 budget: |sum_excess| <= n_ceil * max(|excess_scaled|). With
% target=4, int_scale=256, n_ceil ~ n/1000 ~ 340, and |rvol_final-target|
% plausibly up to ~100, |excess_scaled| <= 25600. So |sum_excess| <= ~8.7M.
% W_sum at the same bucket is <= (Tv_max+1)*(Ta_max+1) <= ~165M if Tv_max
% reaches 12800. Objective top ~= 165M * 8.7M ~= 1.4e15, well within int64.
int: W_sum = (Ta_max + 1) * (Tv_max + 1);
int: W_ta  = Tv_max + 1;
var int: objective = W_sum * sum_excess + W_ta * Ta + Tv;

solve maximize objective;

output [
    "{\n",
    "  \"Tv\": ",      show(int2float(Tv) / int2float(int_scale)), ",\n",
    "  \"Ta\": ",      show(int2float(Ta) / int2float(int_scale)), ",\n",
    "  \"n_fired\": ", show(n_fired), ",\n",
    "  \"n_hit\": ",   show(n_hit),   ",\n",
    "  \"sum_excess\": ", show(sum_excess), ",\n",
    "  \"n_ceil\": ",   show(n_ceil), ",\n",
    "  \"n_ceil_bp\": ", show(n_ceil_bp), ",\n",
    "  \"int_scale\": ",show(int_scale), ",\n",
    "  \"target_scaled\": ", show(target_scaled), ",\n",
    "  \"n_rows\": ",  show(n), "\n",
    "}\n"
];
