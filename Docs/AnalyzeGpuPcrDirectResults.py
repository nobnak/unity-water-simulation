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
    print(f"{'Case Name':32s} | {'Max Diff [m/s^2]':17s} | {'Rel Diff':12s} | {'Residual Norm':14s} | {'Half Rounding':14s} | Status")
    print("-" * 105)

    for c in data.get("linearDispersionTests", []):
        name = c["caseName"]
        max_d = c["maxNormalDiff"]
        rel_d = c["relNormalDiff"]
        res = c["maxNormalResidual"]
        half_d = c.get("halfStorageRoundingDiff", 0.0)
        status = "PASS" if c["passed"] else "FAIL"
        print(f"{name:32s} | {max_d:17.6e} | {rel_d:12.6e} | {res:14.6e} | {half_d:14.6e} | {status}")

    print("\nTangential Component ax [m/s^2] (Neumann Border d(ax)/dz = 0):")
    for c in data.get("linearDispersionTests", []):
        print(f"  {c['caseName']:30s}: MaxDiff={c['maxTangentialDiff']:.6e}, RelDiff={c['relTangentialDiff']:.6e}, RMS={c['rmsTangentialDiff']:.6e}")

    print("\n================================================================================")
    print("2. STEP-BY-STEP TIME EVOLUTION BREAKDOWN (FilterOff, Substep 1)")
    print("================================================================================")
    te = data.get("timeEvolutionBreakdown", {})
    print(f"Grid H = {te.get('gridH')}, dt = {te.get('dt'):.6f}s")
    print(f"{'Substep Phase':42s} | {'Max Diff':14s} | {'RMS Diff':14s} | {'Rel Diff':12s}")
    print("-" * 90)
    for p in te.get("phaseAudits", []):
        print(f"{p['phaseName']:42s} | {p['maxDiff']:14.6e} | {p['rmsDiff']:14.6e} | {p['relDiff']:12.6e}")

    print(f"\nSmoothTotalDepth Delta at Step 1: max |eta_smooth - eta_raw| = {te.get('maxSmoothTotalDepthDeltaEta'):.6e} m")
    print(f"Conclusion: {te.get('conclusion')}")

if __name__ == "__main__":
    main()
