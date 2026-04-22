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
% minimize Ta, then Tv. Picking the most-permissive corner of the Pareto
% frontier — the smallest (Ta, Tv) that still hits n_fired — gives a
% threshold defined by the actual data distribution (the marginal firing
% row that would drop if either threshold rose by one unit). Adjacent
% buckets share most of their data, so their permissive corners vary
% smoothly. The prior max-Ta formulation put Ta at an arbitrary far
% corner, which jumped unpredictably across neighboring buckets.
%
% Implementation: maximize the complement (Ta_max - Ta) and
% (Tv_max - Tv). Weighted sum with W1 >> W2 >> 1 so n_fired strictly
% dominates and Ta strictly dominates Tv within ties:
%   W1 = (Ta_max + 1) * (Tv_max + 1)
%   W2 = (Tv_max + 1)
% With int_scale = 1024 and target_rvol ≤ 5, W1 ~= 25M; W1*n_fired_max
% (~1000) fits in int64.
int: W_fired = (Ta_max + 1) * (Tv_max + 1);
int: W_ta    = Tv_max + 1;
var int: objective = W_fired * n_fired + W_ta * (Ta_max - Ta) + (Tv_max - Tv);

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
