#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Quantitative Comparison between Independent Peregrine (1967) FP64 Reference Solver
and GPU Boussinesq Simulation Results.
"""

import json
import math
import os
import sys

def load_data():
    ref_path = "Docs/Reference_Peregrine_FP64_Data.json"
    gpu_path = "Docs/BoussinesqSolitonFissionReport.json"

    with open(ref_path, "r", encoding="utf-8") as f:
        ref = json.load(f)
    with open(gpu_path, "r", encoding="utf-8-sig") as f:
        gpu = json.load(f)
    return ref, gpu

def find_gpu_case(gpu_data, name):
    for r in gpu_data["results"]:
        if r["caseName"] == name:
            return r
    return None

def main():
    ref, gpu = load_data()
    print("=== 1. FLAT BED SOLITARY WAVE COMPARISON ===")
    ref_flat = ref["flatBedVerification"]
    print(f"Independent Peregrine FP64: speed={ref_flat['theoreticalSpeed']:.4f} m/s, peaks={ref_flat['peakCount_t14']}, status={ref_flat['status']}")
    
    for case_name in ["FlatBed_FP32_Control_CurrentFCT_N256", "FlatBed_FP32_Control_FilterOff_N256", "FlatBed_FP32_Control_FilterOff_N512"]:
        c = find_gpu_case(gpu, case_name)
        if c:
            speed = c.get("leadWaveSpeed", 0.0)
            c_th = math.sqrt(9.81 * (0.60 + 0.12))
            err_spd = (speed - c_th) / c_th * 100.0
            print(f"GPU {case_name:38s}: N={c['resolutionN']:3d}, speed={speed:.4f} m/s (err={err_spd:+.2f}%), leadH={c['leadWaveHeight']:.4f}m, ret={c['amplitudeRetentionRatio']:.3f}, peaks={c['significantPeakCountOnShelf']}")

    print("\n=== 2. SHELF EXPLORATION COMPARISON AT t = 14.0s (L = 50m) ===")
    ref_conv = ref["spatialConvergenceTable"]
    print(f"Independent Peregrine FP64 (N=2048, dz=0.024m): leadZ={ref_conv['leadPeak_z']:.2f}m, leadH={ref_conv['leadPeak_height']:.4f}m, shelfPeaks={ref_conv['shelfPeakCount_t14']}")

    cases_to_compare = [
        "Shelf_FP32_Baseline_CurrentFCT_N256",
        "Shelf_FP32_GridConverge_CurrentFCT_N512",
        "Shelf_FP32_Isolated_NoSlope2nd_N256",
        "Shelf_FP32_Isolated_FilterOff_N256",
        "Shelf_FP32_GridConverge_FilterOff_N512",
        "Shelf_FP32_Comparison_LegacyFilter_N256"
    ]

    for name in cases_to_compare:
        c = find_gpu_case(gpu, name)
        if not c:
            continue
        # Find snapshot near t = 14.0s
        snap = None
        for s in c.get("snapshots", []):
            if abs(s["time"] - 14.0) < 0.1:
                snap = s
                break
        
        lead_peak = None
        if snap and snap.get("peaks"):
            lead_peak = snap["peaks"][0]
            lead_z = lead_peak["z"]
            lead_h = lead_peak["height"]
            peak_cnt = len([p for p in snap["peaks"] if p["z"] >= 18.0 and p["height"] >= 0.01])
        else:
            lead_z = c.get("leadWaveZ", 0.0)
            lead_h = c.get("leadWaveHeight", 0.0)
            peak_cnt = c.get("significantPeakCountOnShelf", 0)

        dz = c["domainLength"] / c["resolutionN"]
        print(f"GPU {name:40s}: N={c['resolutionN']:3d} (dz={dz:.3f}m) | leadZ={lead_z:5.2f}m | leadH={lead_h:6.4f}m | peaks={peak_cnt:2d}")

    print("\n=== 3. STATION ARRIVAL TIME COMPARISON AT SHELF GAUGE z = 30.0m ===")
    # Station at 30m is index 3
    for name in ["Shelf_FP32_Baseline_CurrentFCT_N256", "Shelf_FP32_GridConverge_CurrentFCT_N512", "Shelf_FP32_Isolated_FilterOff_N256", "Shelf_FP32_GridConverge_FilterOff_N512"]:
        c = find_gpu_case(gpu, name)
        if not c: continue
        st = [s for s in c.get("stationSeries", []) if abs(s["stationZ"] - 30.0) < 0.5]
        if st:
            st0 = st[0]
            # find peak arrival time
            max_eta = -1.0
            t_arr = 0.0
            for t_val, e_val in zip(st0["times"], st0["etas"]):
                if e_val > max_eta:
                    max_eta = e_val
                    t_arr = t_val
            print(f"GPU {name:40s}: station z={st0['stationZ']}m -> arrival_t={t_arr:5.2f}s, peakEta={max_eta:.4f}m")

    print("\n=== 4. EXTENDED SHELF PHYSICAL FISSION IN REFERENCE SOLVER ===")
    ext = ref["extendedShelfSolitonFission"]
    for t_str, p_list in ext["peaksSummary"].items():
        print(f"Reference FP64 at t = {t_str}s (L=100m):")
        for i, p in enumerate(p_list):
            print(f"  Peak {i+1}: z = {p[0]:5.2f}m, H = {p[1]:.4f}m")

if __name__ == "__main__":
    main()
