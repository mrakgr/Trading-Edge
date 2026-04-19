% Integer variant of stock_in_play_threshold_direct.mzn for CP-SAT.
%
% CP-SAT requires integer variables. Quantize Tv, Ta to milli-RVOL units
% (thousandths) and pre-scale vr, tr from float to int in the same units.
%
% Identical semantics up to 0.001 RVOL quantization. Significantly faster
% to solve than the float MIP because the LP relaxation bounds become
% exact at integer boundaries.

% ----- Data parameters (caller supplies int-scaled arrays) -----
int: n;
array[1..n] of int: vr_milli;   % 1000 * vr[i]
array[1..n] of int: tr_milli;   % 1000 * tr[i]
array[1..n] of bool: hit;

% Solver knobs (override via -D on the CLI)
int: precision_pct;
int: Tv_milli_max = 3000;       % 1000 * Tv_max
int: Ta_milli_max = 3000;

% ----- Decision variables -----
var 0..Tv_milli_max: Tv_milli;
var 0..Ta_milli_max: Ta_milli;

array [1..n] of var bool: fired =
    [vr_milli[i] >= Tv_milli /\ tr_milli[i] >= Ta_milli | i in 1..n];

var int: n_fired = sum(fired);
var int: n_hit   = sum(i in 1..n)(hit[i] /\ fired[i]);

constraint n_fired >= 1;
constraint 100 * n_hit >= precision_pct * n_fired;

solve maximize n_fired;

output [
    "{\n",
    "  \"Tv\": ",      show(Tv_milli / 1000.0), ",\n",
    "  \"Ta\": ",      show(Ta_milli / 1000.0), ",\n",
    "  \"n_fired\": ", show(n_fired),           ",\n",
    "  \"n_hit\": ",   show(n_hit),             ",\n",
    "  \"precision_pct_requested\": ", show(precision_pct), ",\n",
    "  \"n_rows\": ",  show(n),                 "\n",
    "}\n"
];
