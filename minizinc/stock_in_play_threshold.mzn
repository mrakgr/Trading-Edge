% Threshold optimization for intraday "stock in play" rule.
%
% At a single checkpoint (e.g. 09:31 ET), we observe per (ticker, date) i:
%   vr[i] = cum_volume / (avg_session_adj_volume_4w / split_factor_today)  -- raw-basis
%   tr[i] = cum_transactions / avg_session_transactions_4w
%   hit[i] = 1 iff session_volume_rvol_final >= 3   (end-of-day ground truth)
%
% Rule: fire iff vr[i] >= Tv AND tr[i] >= Ta.
% Find (Tv, Ta) maximizing firing count, subject to firing-conditional
% precision >= precision_pct (0..100).
%
% Solved as a MIP with SCIP. Tv, Ta are continuous in [0, T_max]. Each row
% carries one binary indicator f[i] = [vr[i] >= Tv AND tr[i] >= Ta], plus a
% pair of big-M implications enforcing both directions of the biconditional:
%
%   (forward)  if vr[i] >= Tv and tr[i] >= Ta, then f[i] = 1
%   (reverse)  if f[i] = 1, then vr[i] >= Tv and tr[i] >= Ta
%
% The forward direction is what stops the solver from cheating by setting
% f[i] = 0 everywhere to trivialize the precision constraint. We use a
% linearization via auxiliary binaries gv[i], ga[i] where gv[i] = 1 iff
% vr[i] >= Tv (similarly ga[i]) and f[i] = gv[i] AND ga[i].

% ----- Data parameters (from converter) -----
int: n;                                        % number of rows
array[1..n] of float: vr;                      % raw-basis volume RVOL at checkpoint
array[1..n] of float: tr;                      % transaction RVOL at checkpoint
array[1..n] of bool:  hit;                     % session_volume_rvol_final >= 3

% Solver knobs (override via -D on the CLI)
int: precision_pct = 80;                       % min % of firings that must be hits
float: Tv_max = 3.0;                           % Tv upper bound (search in [0, Tv_max])
float: Ta_max = 3.0;                           % Ta upper bound
float: eps    = 0.001;                         % strict-inequality slack for big-M

% Derived big-M constants
float: vr_max = max(i in 1..n)(vr[i]);
float: tr_max = max(i in 1..n)(tr[i]);
float: M_v = Tv_max + vr_max + 1.0;
float: M_a = Ta_max + tr_max + 1.0;

% ----- Decision variables -----
var 0.0..Tv_max: Tv;
var 0.0..Ta_max: Ta;
array[1..n] of var 0..1: gv;                   % gv[i] = 1 iff vr[i] >= Tv
array[1..n] of var 0..1: ga;                   % ga[i] = 1 iff tr[i] >= Ta
array[1..n] of var 0..1: f;                    % f[i] = gv[i] AND ga[i]

% ----- Constraints: gv[i] <-> (vr[i] >= Tv) -----
% If gv[i] = 1: vr[i] >= Tv (so vr[i] - Tv >= 0)
% If gv[i] = 0: vr[i] <  Tv - eps (so vr[i] - Tv <= -eps)
constraint forall(i in 1..n)(
    vr[i] - Tv >= -M_v * (1 - gv[i])          % gv=1 => vr >= Tv
);
constraint forall(i in 1..n)(
    vr[i] - Tv <= -eps + M_v * gv[i]          % gv=0 => vr < Tv
);

% ----- Constraints: ga[i] <-> (tr[i] >= Ta) -----
constraint forall(i in 1..n)(
    tr[i] - Ta >= -M_a * (1 - ga[i])
);
constraint forall(i in 1..n)(
    tr[i] - Ta <= -eps + M_a * ga[i]
);

% ----- Constraints: f[i] = gv[i] AND ga[i] -----
% Standard linearization of conjunction of binaries.
constraint forall(i in 1..n)(
    f[i] <= gv[i] /\ f[i] <= ga[i] /\ f[i] >= gv[i] + ga[i] - 1
);

% ----- Precision constraint -----
% sum(f[i] where hit[i]) * 100 >= precision_pct * sum(f[i])
% Require at least 1 firing to avoid division-by-zero / trivial solution.
constraint sum(i in 1..n)(f[i]) >= 1;
constraint
    100 * sum(i in 1..n where hit[i])(f[i])
    >= precision_pct * sum(i in 1..n)(f[i]);

% ----- Objective -----
solve maximize sum(i in 1..n)(f[i]);

% ----- Output -----
output [
    "{\n",
    "  \"Tv\": ",        show_float(6, 4, Tv),        ",\n",
    "  \"Ta\": ",        show_float(6, 4, Ta),        ",\n",
    "  \"n_fired\": ",   show(sum(i in 1..n)(f[i])),  ",\n",
    "  \"n_hit\": ",     show(sum(i in 1..n where hit[i])(f[i])), ",\n",
    "  \"precision_pct_requested\": ", show(precision_pct), ",\n",
    "  \"n_rows\": ",    show(n),                      "\n",
    "}\n"
];
