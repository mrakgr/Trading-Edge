% Threshold optimization for intraday "stock in play" rule — direct form.
%
% At a single checkpoint, we observe per (ticker, date) i:
%   vr[i] = cum_volume / (avg_session_adj_volume_4w / split_factor_today)  -- raw-basis
%   tr[i] = cum_transactions / avg_session_transactions_4w
%   hit[i] = 1 iff session_volume_rvol_final >= 3   (end-of-day ground truth)
%
% Rule fires iff vr[i] >= Tv AND tr[i] >= Ta. Find (Tv, Ta) maximizing firing
% count subject to firing-conditional precision >= precision_pct.

% ----- Data parameters -----
int: n;
array[1..n] of float: vr;
array[1..n] of float: tr;
array[1..n] of bool:  hit;

% Solver knobs
int: precision_pct = 80;
float: Tv_max = 3.0;
float: Ta_max = 3.0;

% ----- Decision variables -----
var 0.0..Tv_max: Tv;
var 0.0..Ta_max: Ta;

% ----- Counts (let the solver flatten >= into indicators) -----

array [1..n] of var bool: fired = [vr[i] >= Tv /\ tr[i] >= Ta | i in 1..n];

var int: n_fired = sum(fired);
var int: n_hit = sum(i in 1..n)(hit[i] /\ fired[i]);

% ----- Precision constraint -----
constraint n_fired >= 1;
constraint 100 * n_hit >= precision_pct * n_fired;

% ----- Objective -----
solve maximize n_fired;

% ----- Output -----
output [
    "{\n",
    "  \"Tv\": ",      show_float(6, 4, Tv), ",\n",
    "  \"Ta\": ",      show_float(6, 4, Ta), ",\n",
    "  \"n_fired\": ", show(n_fired),        ",\n",
    "  \"n_hit\": ",   show(n_hit),          ",\n",
    "  \"precision_pct_requested\": ", show(precision_pct), ",\n",
    "  \"n_rows\": ",  show(n),              "\n",
    "}\n"
];
