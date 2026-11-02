#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Audits and summarizes GPU PCR vs FP64 direct precision results
from Docs/GpuPcrDirectPrecisionReport.json.
"""

import json
import os
import sys

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass
    json_path = "Docs/GpuPcrDirectPrecisionReport.json"
    if not os.path.exists(json_path):
        print(f"Error: {json_path} not found.")
        sys.exit(1)

    with open(json_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    print("================================================================================")
    print("1. DIRECT LINEAR DISPERSION INVERSION AUDIT (GPU PCR vs FP64 Tridiagonal)")
    print("================================================================================")
    print(f"GPU Device: {data.get('gpuDevice')}")
    print(f"Unity Version: {data.get('unityVersion')}")
    print(f"Shader Hash: {data.get('computeShaderHash')}")
    print("\nCase Results (Normal Component az [m/s^2], Dirichlet Border az = 0):")
    print(f"{'Case Name':32s} | {'Max Diff [m/s^2]':17s} | {'Rel Diff':12s} | {'Residual Norm':14s} | {'DepthFP16 [m]':14s} | {'AccelFP16 [m/s^2]':18s} | Status")
    print("-" * 125)

    for c in data.get("linearDispersionTests", []):
        name = c["caseName"]
        max_d = c["maxNormalDiff"]
        rel_d = c["relNormalDiff"]
        res = c["maxNormalResidual"]
        depth_half = c.get("halfStorageDepthRoundingDiff", 0.0)
        accel_half = c.get("halfStorageAccelRoundingDiff", 0.0)
        status = "PASS" if c["passed"] else "FAIL"
        print(f"{name:32s} | {max_d:17.6e} | {rel_d:12.6e} | {res:14.6e} | {depth_half:14.6e} | {accel_half:18.6e} | {status}")

    print("\nTangential Component ax [m/s^2] (Neumann Border d(ax)/dz = 0):")
    for c in data.get("linearDispersionTests", []):
        print(f"  {c['caseName']:30s}: MaxDiff={c['maxTangentialDiff']:.6e}, RelDiff={c['relTangentialDiff']:.6e}, RMS={c['rmsTangentialDiff']:.6e}")

    print("\n================================================================================")
    print("2. STEP-BY-STEP TIME EVOLUTION BREAKDOWN")
    print("================================================================================")
    te = data.get("timeEvolutionBreakdown", {})
    print(f"Grid H = {te.get('gridH')}, dt = {te.get('dt'):.6f}s, Overall Time Evolution Passed = {te.get('allPassed')}")

    for sc in te.get("scenarios", []):
        print(f"\n--- Scenario: {sc['scenarioName']} (All Phases Passed: {sc['allPhasesPassed']}) ---")
        print(f"Description: {sc.get('description')}")
        phases = sc.get("phaseAudits", [])
        if phases:
            print(f"{'Phase Name':40s} | {'MaxAll':10s} | {'Idx':4s} | {'Bnd?':5s} | {'MaxInt':10s} | {'RelAll':10s} | {'GpuMax':10s} | {'CpuMax':10s} | Status")
            print("-" * 115)
            for p in phases:
                status = "PASS" if p["passed"] else "FAIL"
                bnd_str = "YES" if p["isBoundary"] else "no"
                print(f"{p['phaseName']:40s} | {p['maxDiffAll']:10.3e} | {p['maxDiffIndex']:4d} | {bnd_str:5s} | {p['maxDiffInterior']:10.3e} | {p['relDiffAll']:10.3e} | {p['gpuValAtMax']:10.4f} | {p['cpuValAtMax']:10.4f} | {status}")

        multi = sc.get("multiStepAudits", [])
        if multi:
            print(f"{'Substep':10s} | {'Parity':6s} | {'MaxDiffEta [m]':16s} | {'RMSDiffEta [m]':16s} | {'MaxDiffW [m/s]':16s} | {'RMSDiffW [m/s]':16s} | Status")
            print("-" * 95)
            for m in multi:
                status = "PASS" if m["passed"] else "FAIL"
                print(f"Step {m['substepIndex']:<5d} | {m['stepParity']:<6d} | {m['maxDiffEta']:16.6e} | {m['rmsDiffEta']:16.6e} | {m['maxDiffW']:16.6e} | {m['rmsDiffW']:16.6e} | {status}")

    print(f"\nSmoothTotalDepth Delta at Step 1: max |eta_smooth - eta_raw| = {te.get('maxSmoothTotalDepthDeltaEta'):.6e} m")
    print(f"Conclusion: {te.get('conclusion')}")

if __name__ == "__main__":
    main()
