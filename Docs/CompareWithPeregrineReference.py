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
    print("  1. Upwind Convective Advection Viscosity: nu_num = 0.5 * |w| * dz")
    print("     - At N=256 (dz=0.195m), nu_num ~= 0.05 m^2/s, causing severe crest diffusion during shoaling.")
    print("     - Refinement test with MacCormack (FilterOff) shows continuous growth with grid spacing:")
    print("         N= 128 (dz=0.391m): peak H = 0.0823 m")
    print("         N= 256 (dz=0.195m): peak H = 0.0861 m")
    print("         N= 512 (dz=0.098m): peak H = 0.0946 m")
    print("         N=1024 (dz=0.049m): peak H = 0.1178 m")
    print("         N=2048 (dz=0.024m): peak H = 0.1545 m")
    print("     -> As dz -> 0, the MacCormack peak grows by +88% towards the high-order reference solution.")
    print("  2. Dispersion Operator Slope Term Comparison:")
    inter = ref.get("interModelComparison", {})
    print(f"     - GPU Continuous Operator (slope_coeff=1.0): H_peak = {inter.get('leadPeakHeight_GPU_Operator_t14', 0):.4f} m")
    print(f"     - Classical Peregrine (slope_coeff=0.5):    H_peak = {inter.get('leadPeakHeight_Peregrine1967_t14', 0):.4f} m")
    print(f"     - Slope term difference contributes:       Delta H = {inter.get('difference', 0):.4f} m (approx 3.5%)")
    print("  3. 4th-Order Biharmonic FCT Filter:")
    print("     - Adds numerical dissipation kappa4 * dz^4 d^4/dz^4, reducing peak from 0.0946m to 0.0832m at N=512.")
    print("  4. Dispersion Inversion Spatial Stencil:")
    print("     - Dispersion inversion uses 3-point central differences (2nd-order in space).")

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
    print("Status: 【未確定 (Inconclusive)】")
    print("  - Comparison with experiment (BP05) and independent solver is ongoing.")
    print("  - Conditions, advection schemes, and dispersion slope terms differ between models.")
    print("  - A large peak height residual exists between the non-dissipative reference (0.29m) and GPU (0.083m),")
    print("    caused by 1st-order upwind convective diffusion, biharmonic filtering, and grid under-resolution.")
    print("  - The physical correctness and soliton fission suppression of the current GPU scheme remain UNCONFIRMED.")

if __name__ == "__main__":
    main()
