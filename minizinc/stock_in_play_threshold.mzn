int: n;
array[1..n] of tuple(float, float, float): data;

int: int_scale;
constraint assert(int_scale > 0, "The int scale should be positive.");

function int: scale(float: x) = floor(x * int_scale);
function float: data_vr(int: i) = data[i].1;                  % cum_volume / avg_raw_4w (split-safe)
function float: data_tr(int: i) = data[i].2;                  % cum_transactions / avg_transactions_4w
function float: data_rvol_final(int: i) = data[i].3;          % end-of-day session RVOL (ground truth)

float: target_rvol;
array[1..n] of int: vr = [scale(min(data_vr(i), target_rvol)) | i in 1..n];
array[1..n] of int: tr = [scale(min(data_tr(i), target_rvol)) | i in 1..n];

% ----- Solver knobs (caller supplies via -D on the CLI) -----
int: precision_pct;
int: Tv_max = ceil(target_rvol * int_scale);
int: Ta_max = ceil(target_rvol * int_scale);

% ----- Decision variables (in int_scale units) -----
var 0..Tv_max: Tv;
var 0..Ta_max: Ta;

array [1..n] of var bool: fired = [vr[i] >= Tv /\ tr[i] >= Ta | i in 1..n]; % Entries above the threshold.

var int: n_fired = sum(fired);
var int: n_hit   = sum(i in 1..n)(data_rvol_final(i) >= target_rvol /\ fired[i]);

constraint n_fired >= 1;
constraint 100 * n_hit >= precision_pct * n_fired;

% Lexicographic objective: maximize n_fired, then (among tied solutions)
% maximize Ta, then Tv. Without this the solver picks arbitrarily
% among (Tv, Ta) pairs that give the same n_fired, which makes Ta jump
% around from run to run and across neighboring bucket/target cells.
%
% Implementation: weighted sum with W1 >> W2 >> 1. Because Tv and
% Ta are in [0, Tv_max] and [0, Ta_max], choosing
%   W1 = (Ta_max + 1) * (Tv_max + 1)
%   W2 = (Tv_max + 1)
% guarantees one more fired trade always beats any possible tie-break gain
% and one extra Ta unit always beats any Tv gain. With int_scale
% = 1024 and target_rvol ≤ 5, W1 ~= 25M; W1*n_fired_max (~1000) fits in int64.
int: W_fired = (Ta_max + 1) * (Tv_max + 1);
int: W_ta    = Tv_max + 1;
var int: objective = W_fired * n_fired + W_ta * Ta + Tv;

solve maximize objective;

output [
    "{\n",
    "  \"Tv\": ",      show(int2float(Tv) / int2float(int_scale)), ",\n",
    "  \"Ta\": ",      show(int2float(Ta) / int2float(int_scale)), ",\n",
    "  \"n_fired\": ", show(n_fired), ",\n",
    "  \"n_hit\": ",   show(n_hit),   ",\n",
    "  \"precision_pct_requested\": ", show(precision_pct), ",\n",
    "  \"int_scale\": ",         show(int_scale), ",\n",
    "  \"n_rows\": ",  show(n), "\n",
    "}\n"
];
