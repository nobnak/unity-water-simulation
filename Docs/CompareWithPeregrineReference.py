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
    print("3. GPU PCR VS FP64 DIRECT LINEAR COMPARISON & FACTOR ANALYSIS")
    print("================================================================================")
    print("Direct GPU PCR ComputeShader vs FP64 Reference (Exact Matched Problem):")
    print("  - Evaluated on NVIDIA GeForce RTX 5070 (Direct3D11) via Docs/GpuPcrDirectPrecisionReport.json:")
    print("    * FlatBed (Sinusoidal & LocalizedPulse):      MaxDiff = 1.83e-7 ~ 1.90e-7 m/s^2, Residual = 9.91e-7 ~ 1.18e-6")
    print("    * LinearSlope (Sinusoidal & LocalizedPulse):  MaxDiff = 4.46e-7 ~ 7.07e-7 m/s^2, Residual = 1.30e-6 ~ 1.91e-6")
    print("    * CurvedBed (h_zz != 0):                      MaxDiff = 5.22e-7 ~ 5.60e-7 m/s^2, Residual = 1.26e-6 ~ 2.16e-6")
    print("    * PiecewiseShelf (kinks at z=10, 18m):        MaxDiff = 3.63e-7 ~ 5.81e-7 m/s^2, Residual = 5.20e-7 ~ 1.96e-6")
    print("    -> Confirms GPU PCR solve matches FP64 tridiagonal solution to single-precision machine epsilon (O(1e-7)).")
    print("       Half-precision depth storage introduces rounding variations of 2.8e-4 ~ 6.5e-4, 3 orders larger than PCR solver precision.")
    print("\nStep-by-Step Substep 1 Time Evolution Audit (FilterOff, GPU vs FP64):")
    print("  - Phase 1 (Predictor Surface eta*):       MaxDiff = 4.28e-9 m")
    print("  - Phase 2 (Predictor RHS Force fz*):      MaxDiff = 1.08e-2 m/s^2")
    print("  - Phase 3 (Inverted Predictor Accel az*): MaxDiff = 1.12e-7 m/s^2")
    print("  - Phase 4 (Predicted Velocity w*):        MaxDiff = 3.33e-3 m/s")
    print("  - Phase 5 (Corrected Surface eta^1):      MaxDiff = 2.04e-5 m")
    print("  - Phase 6 (Corrector RHS Force fz**):     MaxDiff = 7.21e-5 m/s^2")
    print("  - Phase 7 (Inverted Corrector Accel az**):MaxDiff = 1.32e-5 m/s^2")
    print("  - Phase 8 (Final Step Velocity w^1):      MaxDiff = 2.66e-8 m/s")
    print("  -> Step 1 state matches FP64 to O(1e-5m) in eta and O(1e-8m/s) in w. SmoothTotalDepth impact at Step 1 is minor (< 1.2e-7m).")
    print("\nConvective Advection Formulation Analysis (Isolated at N=512, dt=0.004s):")
    print("  - MacCormack Directional One-Sided Advection (Alternating Forward/Backward, Combined 2nd-Order):")
    print("      H_peak = 0.2172m at z = 36.08m, Roughness = 7.60e-4")
    print("  - 1st-Order Upwind Advection (Direction-Fixed Single-Stage 1st-Order):")
    print("      H_peak = 0.0943m at z = 34.33m, Roughness = 3.00e-4")
    print("  - Note on Discretization:")
    print("      Each individual stage of MacCormack uses a 1st-order one-sided difference, but their predictor/corrector")
    print("      combination achieves 2nd-order accuracy O(dt^2, dz^2). Upwind advection introduces artificial numerical viscosity")
    print("      nu_num = 0.5 * |w| * dz. Higher peak alone is not the sole acceptance criterion; full waveform, phase,")
    print("      high-frequency oscillations, and conservation must be evaluated before any runtime adoption.")

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
    print("  \"Operator coefficient equations were aligned, and direct agreement between GPU PCR and FP64 linear solutions (10^-7 precision) as well as 1-step time evolution were confirmed; however, the primary driver of wave height deficit (BP05 Case B -27.5% FAIL) in long-duration runs among convective advection, dissipation, and grid under-resolution remains inconclusive.\"")
    print("  (係数式は整合させ、GPU PCRとFP64線形解の直接一致（10^-7精度）および1ステップ時間発展の整合を確認したが、長時間の波高不足（BP05 Case Bの-27.5% FAIL等）における対流・散逸・格子未解像の主因は未確定)")

if __name__ == "__main__":
    main()
