#!/usr/bin/env python3
"""
CMA-ES optimizer for the VWAP trading system.
Requires the F# evaluation server running: dotnet fsi scripts/optimize_server.fsx

Uses the /eval/batch endpoint to evaluate the entire CMA-ES population in parallel
on the server side (Array.Parallel.map).
"""

import argparse
import json
import sys
import requests
import cma
import numpy as np

SERVER = "http://127.0.0.1:8085"


def check_server():
    try:
        r = requests.get(f"{SERVER}/health", timeout=5)
        r.raise_for_status()
    except Exception:
        print(f"ERROR: Cannot reach server at {SERVER}")
        print(f"Start it first: dotnet fsi scripts/optimize_server.fsx")
        sys.exit(1)

    config = requests.get(f"{SERVER}/config").json()
    print(f"Server connected — {config['numDays']} days loaded")
    print(f"  basePct={config['basePct']}, decay={config['decay']}, "
          f"positionSize={config['positionSize']}, referenceVol={config['referenceVol']}")
    return config


def exponents_to_pcts(exponents, base_pct, decay):
    return [base_pct * decay ** e for e in exponents]


def eval_batch(items):
    """Send a batch of evaluation requests to the server. Returns list of profit factors."""
    r = requests.post(f"{SERVER}/eval/batch", json={"items": items}, timeout=600)
    r.raise_for_status()
    return r.json()["profitFactors"]


def cma_optimize(x0, sigma0, bounds, build_item, label, args):
    """
    Generic CMA-ES loop using ask/tell with batch evaluation.

    build_item(x) -> dict suitable for /eval/batch items
    Returns (best_x, best_f, total_evals).
    """
    opts = {
        "maxiter": args.max_iter,
        "popsize": args.pop_size,
        "tolx": 1e-3,
        "tolfun": 1e-4,
        "bounds": bounds,
        "verbose": -1,
    }

    es = cma.CMAEvolutionStrategy(x0, sigma0, opts)

    eval_count = 0
    best_pf = 0.0
    generation = 0

    while not es.stop():
        generation += 1
        solutions = es.ask()
        items = [build_item(x) for x in solutions]
        profit_factors = eval_batch(items)
        fitnesses = [-pf for pf in profit_factors]
        es.tell(solutions, fitnesses)

        eval_count += len(solutions)
        gen_best_idx = np.argmax(profit_factors)
        gen_best_pf = profit_factors[gen_best_idx]

        if gen_best_pf > best_pf:
            best_pf = gen_best_pf
            x = solutions[gen_best_idx]
            exp_str = ", ".join(f"{e:.1f}" for e in x[:4])
            extra = "".join(f" {k}={v:.3f}" for k, v in _extra_params(x))
            print(f"  [{label}] gen {generation:3d} ({eval_count:4d} evals): "
                  f"exponents=[{exp_str}]{extra} PF={gen_best_pf:.4f} <-- new best")
        elif generation % 5 == 0:
            x = solutions[gen_best_idx]
            exp_str = ", ".join(f"{e:.1f}" for e in x[:4])
            extra = "".join(f" {k}={v:.3f}" for k, v in _extra_params(x))
            print(f"  [{label}] gen {generation:3d} ({eval_count:4d} evals): "
                  f"exponents=[{exp_str}]{extra} PF={gen_best_pf:.4f}")

    result = es.result
    return result.xbest, -result.fbest, result.evaluations


def _extra_params(x):
    """Extract named extra params beyond the 4 exponents."""
    params = []
    if len(x) > 4:
        params.append(("bandVol", max(x[4], 0.0)))
    if len(x) > 5:
        params.append(("pctile", float(np.clip(x[5], 0.05, 0.95))))
    return params


def run_stage1(config, args):
    """Stage 1: Optimize exponents + bandVol using decisions (no fill sim)."""
    base_pct = config["basePct"]
    decay = config["decay"]

    x0 = list(args.start_exponents) + [args.start_band_vol]
    sigma0 = args.sigma
    bounds = [[-50, -50, -50, -50, 0.0], [0, 0, 0, 0, 3.0]]

    def build_item(x):
        pcts = exponents_to_pcts(x[:4], base_pct, decay)
        return {"pcts": pcts, "bandVol": max(float(x[4]), 0.0)}

    print(f"\n{'='*60}")
    print(f"STAGE 1: CMA-ES on exponents + bandVol (decisions, no fill sim)")
    print(f"  x0 = {x0}")
    print(f"  sigma0 = {sigma0}, popsize = {args.pop_size}, maxiter = {args.max_iter}")
    print(f"{'='*60}\n")

    best_x, best_pf, total_evals = cma_optimize(x0, sigma0, bounds, build_item, "stage1", args)

    best_exponents = best_x[:4]
    best_band_vol = max(best_x[4], 0.0)
    best_pcts = exponents_to_pcts(best_exponents, base_pct, decay)

    print(f"\nStage 1 result ({total_evals} evals):")
    exp_str = ", ".join(f"{e!r}" for e in best_exponents)
    pcts_str = ", ".join(f"{p!r}" for p in best_pcts)
    print(f"  exponents = [{exp_str}]")
    print(f"  pcts      = [{pcts_str}]")
    print(f"  bandVol   = {best_band_vol!r}")
    print(f"  PF        = {best_pf:.4f}")

    return best_exponents, best_band_vol, best_pcts


def run_stage2(config, best_exponents, best_band_vol, args):
    """Stage 2: Re-optimize with fill simulation, adding percentile as a parameter."""
    base_pct = config["basePct"]
    decay = config["decay"]

    x0 = list(best_exponents) + [best_band_vol, args.start_percentile]
    sigma0 = args.sigma * 0.5
    bounds = [[-50, -50, -50, -50, 0.0, 0.05], [0, 0, 0, 0, 3.0, 0.95]]

    def build_item(x):
        pcts = exponents_to_pcts(x[:4], base_pct, decay)
        return {
            "pcts": pcts,
            "bandVol": max(float(x[4]), 0.0),
            "percentile": float(np.clip(x[5], 0.05, 0.95)),
        }

    print(f"\n{'='*60}")
    print(f"STAGE 2: CMA-ES on exponents + bandVol + percentile (with fill sim)")
    print(f"  x0 = [{', '.join(f'{v:.2f}' for v in x0)}]")
    print(f"  sigma0 = {sigma0:.2f}, popsize = {args.pop_size}, maxiter = {args.max_iter}")
    print(f"{'='*60}\n")

    best_x, best_pf, total_evals = cma_optimize(x0, sigma0, bounds, build_item, "stage2", args)

    best_exponents = best_x[:4]
    best_band_vol = max(best_x[4], 0.0)
    best_percentile = float(np.clip(best_x[5], 0.05, 0.95))
    best_pcts = exponents_to_pcts(best_exponents, base_pct, decay)

    print(f"\nStage 2 result ({total_evals} evals):")
    exp_str = ", ".join(f"{e!r}" for e in best_exponents)
    pcts_str = ", ".join(f"{p!r}" for p in best_pcts)
    print(f"  exponents  = [{exp_str}]")
    print(f"  pcts       = [{pcts_str}]")
    print(f"  bandVol    = {best_band_vol!r}")
    print(f"  percentile = {best_percentile!r}")
    print(f"  PF         = {best_pf:.4f}")

    return best_exponents, best_band_vol, best_percentile, best_pcts


def main():
    parser = argparse.ArgumentParser(description="CMA-ES optimizer for VWAP system")
    parser.add_argument("--start-exponents", type=float, nargs=4,
                        default=[-14, -2, -9, -18],
                        help="Initial exponents (default: -14 -2 -9 -18)")
    parser.add_argument("--start-band-vol", type=float, default=0.2,
                        help="Initial bandVol (default: 0.2)")
    parser.add_argument("--start-percentile", type=float, default=0.3,
                        help="Initial fill percentile for stage 2 (default: 0.3)")
    parser.add_argument("--sigma", type=float, default=5.0,
                        help="Initial step size (default: 5.0)")
    parser.add_argument("--pop-size", type=int, default=14,
                        help="Population size (default: 14)")
    parser.add_argument("--max-iter", type=int, default=100,
                        help="Max CMA-ES iterations (default: 100)")
    parser.add_argument("--stage", choices=["1", "2", "both"], default="both",
                        help="Which stages to run (default: both)")
    parser.add_argument("--log", type=str, default="logs/optimize_cma.log",
                        help="Log file path (default: logs/optimize_cma.log)")

    args = parser.parse_args()
    config = check_server()

    # Tee stdout to log
    import os
    os.makedirs(os.path.dirname(args.log), exist_ok=True)
    log_file = open(args.log, "w")

    class Tee:
        def __init__(self, *streams):
            self.streams = streams
        def write(self, data):
            for s in self.streams:
                s.write(data)
                s.flush()
        def flush(self):
            for s in self.streams:
                s.flush()

    sys.stdout = Tee(sys.__stdout__, log_file)

    print(f"CMA-ES Optimization")
    print(f"Server: {SERVER}")
    print(f"Config: {json.dumps(config, indent=2)}")

    if args.stage in ("1", "both"):
        best_exponents, best_band_vol, best_pcts = run_stage1(config, args)
    else:
        best_exponents = np.array(args.start_exponents)
        best_band_vol = args.start_band_vol
        best_pcts = exponents_to_pcts(best_exponents, config["basePct"], config["decay"])

    if args.stage in ("2", "both"):
        best_exponents, best_band_vol, best_percentile, best_pcts = \
            run_stage2(config, best_exponents, best_band_vol, args)

    print(f"\n{'='*60}")
    print(f"FINAL RESULTS")
    print(f"{'='*60}")
    exp_str = ", ".join(f"{e!r}" for e in best_exponents)
    pcts_str = ", ".join(f"{p!r}" for p in best_pcts)
    print(f"  exponents  = [{exp_str}]")
    print(f"  pcts       = [{pcts_str}]")
    print(f"  bandVol    = {best_band_vol!r}")
    if args.stage in ("2", "both"):
        print(f"  percentile = {best_percentile!r}")

    log_file.close()


if __name__ == "__main__":
    main()
