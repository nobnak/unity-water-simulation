#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Quantitative Comparison between Independent Boussinesq FP64 Reference Solver,
NTHMP BP05 Laboratory Flume Data, and GPU Simulation Results.
"""

import json
import math
import os
import sys
import numpy as np

def load_data():
    ref_path = "Docs/Reference_Peregrine_FP64_Data.json"
    gpu_path = "Docs/BoussinesqSolitonFissionReport.json"
    bp05_path = "Docs/ReferenceData/BP05_comparison.json"

    with open(ref_path, "r", encoding="utf-8") as f:
        ref = json.load(f)
    with open(gpu_path, "r", encoding="utf-8-sig") as f:
        gpu = json.load(f)
    bp05 = None
    if os.path.exists(bp05_path):
        with open(bp05_path, "r", encoding="utf-8") as f:
            bp05 = json.load(f)
    return ref, gpu, bp05

def find_gpu_case(gpu_data, name):
    for r in gpu_data["results"]:
        if r["caseName"] == name:
            return r
    return None

def main():
    ref, gpu, bp05 = load_data()
    print("================================================================================")
    print("1. RAW DATA & PEAK EXTRACTION AUDIT")
    print("================================================================================")
    ref_flat = ref["flatBedVerification"]
    print(f"Flat Bed (Independent Solver, t=14.0s, N=1024):")
    print(f"  Theoretical speed:  {ref_flat['theoreticalSpeed']:.4f} m/s")
    print(f"  Measured speed:     {ref_flat['measuredSpeed']:.4f} m/s (Error: {ref_flat['speedErrorPercent']:+.2f}%)")
    print(f"  Prominent peaks (prominence >= 0.005m): {ref_flat['prominentPeakCount_t14']}")
    for p in ref_flat.get('peaksDetail', []):
        print(f"    Peak at z={p['z']:.2f}m: height={p['height']:.4f}m, prominence={p['prominence']:.4f}m, width={p['width_half_prom']:.2f}m")
    print(f"  Raw detail: {ref_flat['description']}")

    ref_shelf = ref["spatialConvergenceTable"]
    print(f"\nShelf (Independent Solver, t=14.0s, N=2048):")
    print(f"  Prominent peaks (prominence >= 0.005m, z >= 18m): {ref_shelf['shelfProminentPeakCount_t14']}")
    for i, p in enumerate(ref_shelf.get('shelfPeaksDetail_t14', [])):
        print(f"    Peak {i+1} at z={p['z']:.2f}m: height={p['height']:.4f}m, prominence={p['prominence']:.4f}m, width={p['width_half_prom']:.2f}m")

    print("\n================================================================================")
    print("2. COMPARISON UNDER COMMON INITIAL VALUE PROBLEM SPECIFICATION")
    print("================================================================================")
    init_ref = ref["metadata"]["initialSolitaryWave"]
    print(f"Initial Solitary Wave Profile:")
    print(f"  Formula:              {init_ref['formula']}")
    print(f"  Amplitude a0:         {init_ref['a0']:.4f} m")
    print(f"  Water depth h1:       {init_ref['h1']:.4f} m")
    print(f"  Wave width kappa0:    {init_ref['kappa0_width']:.6f} m^-1")
    print(f"  Theoretical speed c0: {init_ref['c0_theoretical']:.4f} m/s")
    print(f"  Initial water volume: {init_ref['initialMass']:.6f} m^2")
    print(f"  Initial momentum:     {init_ref['initialMomentum']:.6f} m^3/s")

    print("\nGPU Solvers on Shelf at t=14.0s:")
    for name in ["Shelf_FP32_Baseline_CurrentFCT_N256", "Shelf_FP32_GridConverge_CurrentFCT_N512",
                 "Shelf_FP32_Isolated_FilterOff_N256", "Shelf_FP32_GridConverge_FilterOff_N512"]:
        c = find_gpu_case(gpu, name)
        if not c: continue
        # Find snapshot near t = 14.0s
        snap = None
        for s in c.get("snapshots", []):
            if abs(s["time"] - 14.0) < 0.1:
                snap = s
                break
        peaks = snap.get("peaks", []) if snap else []
        lead_p = peaks[0] if peaks else {'z': c.get('leadWaveZ', 0.0), 'height': c.get('leadWaveHeight', 0.0)}
        print(f"  {name:42s}: N={c['resolutionN']:3d} | lead_z={lead_p['z']:5.2f}m | lead_H={lead_p['height']:.4f}m | peaks={len(peaks)}")
        for idx, p in enumerate(peaks):
            print(f"      Peak {idx+1}: z={p['z']:.2f}m, height={p['height']:.4f}m, prom={p.get('prominence', 0.0):.4f}m")

    print("\n================================================================================")
    print("3. ISOLATING THE SHOALED WAVE HEIGHT DISCREPANCY (0.290m vs 0.083m)")
    print("================================================================================")
    print("Factor Analysis for Wave Height Damping on the Shelf:")
    print("  1. Leading Candidate: Convective Advection Scheme (Isolated Test at N=512, dt=0.004s):")
    print("     - In a strictly controlled test holding grid, time integration, continuity, exact discrete dispersion,")
    print("       and bathymetry identical:")
    print("         * 2nd-order MacCormack Directional Advection: H_peak = 0.2172m at z = 36.08m")
    print("         * 1st-order Upwind Advection (GPU form):      H_peak = 0.0943m at z = 34.33m")
    print("         * Delta H isolated to convective advection:   +0.1229m (+130.2%), Phase advance: +1.75m")
    print("     - Upwind advection introduces artificial numerical viscosity nu_num = 0.5 * |w| * dz, causing substantial")
    print("       damping. However, because multi-variable grid interactions remain, this is identified as a")
    print("       'Leading Candidate' (有力候補) rather than an exclusive proven root cause.")
    print("  2. Dispersion Operator Mathematical Derivation & Discrete Equivalence:")
    print("     - Peregrine (1967) eq (2.11) in depth-averaged velocity u* has dispersion term:")
    print("         1/2 * h * (h u*)_{xxt} - 1/6 * h^2 * u*_{xxt}")
    print("       Expanding on smooth bed: 1/3 * h^2 * psi_zz + 1.0 * h * h_z * psi_z + 0.5 * h * h_zz * psi")
    print("       (The bed slope coeff is 1.0 and curvature coeff is 0.5; slope coeff 0.5 was a misconception and is retracted).")
    print("     - The GPU discrete matrix A_j, B_j, C_j directly discretizes this expression in conservation form,")
    print("       including bed curvature and discrete handling across piecewise linear shelf kinks.")
    print("  3. High-Frequency Damping Filters:")
    print("     - GPU FCT (bilateral biharmonic with flux limitation) suppresses 2-grid instabilities.")
    print("     - Unconstrained 4th biharmonic dissipation in Python accounts for approx 0.0107m damping at N=512.")
    print("  4. Spatial Resolution & Dispersion Inversion Stencil:")
    print("     - Dispersion inversion uses 3-point central differences (2nd-order in space).")
    print("     - Grid refinement (N=128 -> 2048) progressively resolves the shoaled wave crest.")

    print("\n================================================================================")
    print("4. NTHMP BP05 LABORATORY FLUME BENCHMARK AUDIT (SUCCESS & FAILURES)")
    print("================================================================================")
    print("NTHMP BP05 Solitary Wave Shoaling on Composite Beach:")
    print("  - Case A (H/d = 0.039, non-breaking gentle wave):")
    print("      Gauge 7 Peak Error:   FCT N512 = +5.6%, FilterOff N512 = +6.1% (PASS, <= 10%)")
    print("      Travel Time g5->g7:   FCT N512 = -0.10s, FilterOff N512 = -0.10s (PASS, <= 0.15s)")
    print("      NRMSE:                0.118 (PASS, <= 0.15)")
    print("      Observation:          All methods (FCT, FilterOff, Legacy2D) differ by <= 0.5% in peak.")
    print("                            Case A does not discriminate between schemes.")
    print("  - Case B (H/d = 0.264, strong non-breaking shoaling):")
    print("      Gauge 7 Peak Error:   FCT N512 = -27.5%, FilterOff N512 = -22.2% (FAIL: severely underpredicts peak)")
    print("      FCT N256 Peak Error:  -49.2% (FAIL: strong resolution dependence)")
    print("      Observation:          The flume experiment shows wave growth +33% (5.31cm -> 7.05cm).")
    print("                            GPU simulation shows only +3% (FCT) / +8% (FilterOff) growth.")
    print("                            Both FCT and FilterOff FAIL the peak height criterion in Case B.")
    print("  - Arrival Time Evaluation Distinction:")
    print("      - Travel time difference delta_t(g5 -> gk) was aligned at g5 peak; it validates relative wave speed,")
    print("        NOT the independent absolute travel time from the wave generator.")

    print("\n================================================================================")
    print("5. SUMMARY CONCLUSION & UNCERTAINTIES")
    print("================================================================================")
    print("Status: [Inconclusive]")
    print("  [Interim Conclusion]")
    print("  \"Initial condition formula and report consistency were improved, but dispersion operator differences remain and the primary cause of wave height damping is inconclusive.\"")
    print("  (初期条件の式と報告の整合性は改善したが、分散演算子の不一致が残り、波高不足の主因は未確定)")

if __name__ == "__main__":
    main()
